// ============================================================================
// TossOpenApiLiveAdapter — v10.2.1: 토스증권 Live REST 실통신 트랜스포트 (조회 전용·liveArmed:false)
//
//   TossPortfolioSyncAdapter 의 스텁(tokenProvider:()=>"" / accountFetch:_=>null)을 '실통신'으로 대체하는 트랜스포트.
//   기존 자산을 최대 재사용한다:
//     · 토큰 수명주기 = TossTokenService(락-프리 핫패스 CurrentAccessToken·백그라운드 선제 갱신·fail-closed) 재사용.
//     · JSON 파싱     = TossJsonParser(δ=0 Utf8JsonReader) 재사용 — 본 트랜스포트는 응답 '바이트'만 반환.
//     · 자격증명      = SecurityAssetVault(DPAPI at-rest) → ProductionCredentialBinder.WithSecret 로 콜드 패스에서만
//                       복호화(SecretLease 즉시 와이프). 잔고 조회는 읽기 전용 → requireArmed:false(주문 경로와 분리).
//     · Live 방어     = TossLiveApiGovernor(Think-Beyond: 레이트리밋·429 락아웃·서킷브레이커) 로 모든 실 요청을 게이팅.
//
//   실 계약(웹 확인·2026): 토큰 POST https://openapi.tossinvest.com/oauth2/token (Basic Base64(id:secret)·form
//   grant_type=client_credentials), 잔고 GET /v1/accounts/holdings (Authorization: Bearer·X-Tossinvest-Account).
//   ★잔고 응답 필드명은 공식 openapi.json(/openapi-docs/latest/openapi.json)에서 최종 확정 필요 → TossJsonParser
//   스키마는 조정 가능(현재 프리셋). 엔드포인트/계좌는 생성자 주입으로 구성 가능.
//
//   ★불변: δ=0(핫패스 AccessToken()=TossTokenService 락-프리·힙 0 / 파싱=TossJsonParser δ=0. HTTP 취득은 콜드
//     패스 — 네트워크 I/O 라 할당 허용) · fail-closed(무토큰/429/회로개방/rate초과/네트워크예외/미저장/미무장 → null·
//     미변경) · liveArmed:false(★발주 메서드 자체가 없음. RejectOrderAttempt 하드 veto. 실 주문은 별도
//     GenesisLiveIgnitionGateway 물리 이중 키 게이트 경유 — 본 트랜스포트는 조회 GET 만).
// ============================================================================
using System;
using System.Buffers;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace StockManager.Common
{
    public sealed class TossOpenApiLiveAdapter : IDisposable
    {
        public const string DefaultTokenUrl    = "https://openapi.tossinvest.com/oauth2/token";
        public const string DefaultHoldingsUrl = "https://openapi.tossinvest.com/v1/accounts/holdings";

        private readonly SecurityAssetVault  _vault;
        private readonly string              _accountKey;
        private readonly string              _holdingsUrl;
        private readonly TossTokenService    _tokenService;
        private readonly TossLiveApiGovernor _governor;
        private readonly HttpClient          _http;

        private long _fetches, _fetchFailures, _rateBlocked, _orderVetoes;

        /// <param name="vault">DPAPI 금고(복호화는 콜드 패스에서만·requireArmed:false 읽기전용).</param>
        /// <param name="clientIdKey">금고 키(예: "toss.apiKey" = client_id).</param>
        /// <param name="clientSecretKey">금고 키(예: "toss.secret" = client_secret).</param>
        /// <param name="accountKey">금고 키(예: "toss.account" = X-Tossinvest-Account).</param>
        /// <param name="clock">현재 시각(ticks·100ns) — 주입 필수(토큰서비스·거버너 결정론 공유).</param>
        /// <param name="attestation">동결 채널(옵션·토큰 연속 실패 임계).</param>
        public TossOpenApiLiveAdapter(
            SecurityAssetVault vault, string clientIdKey, string clientSecretKey, string accountKey,
            Func<long> clock, OrderAttestationService? attestation = null,
            string? tokenUrl = null, string? holdingsUrl = null,
            long maxRequestsPerSecond = 8, int httpTimeoutSeconds = 10)
        {
            _vault       = vault ?? throw new ArgumentNullException(nameof(vault));
            _accountKey  = accountKey ?? "";
            _holdingsUrl = string.IsNullOrWhiteSpace(holdingsUrl) ? DefaultHoldingsUrl : holdingsUrl!;
            if (clock is null) throw new ArgumentNullException(nameof(clock));

            var auth = new TossLiveAuthClient(vault, clientIdKey, clientSecretKey,
                                              string.IsNullOrWhiteSpace(tokenUrl) ? DefaultTokenUrl : tokenUrl!);
            _tokenService = new TossTokenService(auth, clock, attestation);
            _governor     = new TossLiveApiGovernor(clock, maxPerSecond: maxRequestsPerSecond);
            _http         = new HttpClient { Timeout = TimeSpan.FromSeconds(httpTimeoutSeconds <= 0 ? 10 : httpTimeoutSeconds) };
        }

        // ── 수명주기(콜드) ──────────────────────────────────────────────────
        public void Start() => _tokenService.Start();
        public void Stop()  => _tokenService.Stop();

        // ── 텔레메트리(관제) ───────────────────────────────────────────────
        public long Fetches           => Interlocked.Read(ref _fetches);
        public long FetchFailures     => Interlocked.Read(ref _fetchFailures);
        public long RateBlocked       => Interlocked.Read(ref _rateBlocked);
        public long OrderVetoes       => Interlocked.Read(ref _orderVetoes);
        public bool HasValidToken     => _tokenService.HasValidToken;
        public TossLiveApiGovernor Governor => _governor;
        public TossTokenService    Tokens   => _tokenService;

        // ====================================================================
        // TossPortfolioSyncAdapter 델리게이트 — 실통신 배선.
        // ====================================================================

        /// <summary>tokenProvider 델리게이트(δ=0 핫패스). 없음/만료 → "" (fail-closed — 미인증 조회 차단).</summary>
        public string? AccessToken() => _tokenService.CurrentAccessToken();

        /// <summary>
        /// accountFetch 델리게이트(콜드·거버너 게이팅). 잔고 GET → 응답 바이트(TossJsonParser 가 δ=0 파싱). fail-closed:
        /// 무토큰/rate초과/429/회로개방/비2xx/네트워크예외 → null(미변경). 429 는 Retry-After 만큼 완전 봉쇄(IP 보호).
        /// </summary>
        public byte[]? FetchAccountJson(string token)
        {
            if (string.IsNullOrEmpty(token)) return null;                       // 미인증 → fail-closed
            if (!_governor.TryAcquire()) { Interlocked.Increment(ref _rateBlocked); return null; }  // rate/429/회로 → fail-closed
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, _holdingsUrl);
                req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
                string acct = RevealAccount();
                if (acct.Length != 0) req.Headers.TryAddWithoutValidation("X-Tossinvest-Account", acct);

                using var res = _http.Send(req);
                if ((int)res.StatusCode == 429)                                 // ★Rate-Limit Lockout — IP 차단 회피
                {
                    long ra = res.Headers.RetryAfter?.Delta is { } d ? (long)d.TotalSeconds : 0;
                    _governor.NoteRateLimited(ra);
                    return null;                                                // fail-closed(재조회는 거버너 봉쇄창 이후)
                }
                if (!res.IsSuccessStatusCode) { _governor.NoteFailure(); Interlocked.Increment(ref _fetchFailures); return null; }

                byte[] body = res.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
                _governor.NoteSuccess();
                Interlocked.Increment(ref _fetches);
                return body;
            }
            catch
            {
                _governor.NoteFailure();
                Interlocked.Increment(ref _fetchFailures);
                return null;                                                    // 네트워크/기타 → fail-closed
            }
        }

        /// <summary>★비인가 발주/변조 시도 하드 거부(항상 false). 본 트랜스포트는 조회 전용 — 발주 라우팅 원천 차단.</summary>
        public bool RejectOrderAttempt(long reasonCode)
        {
            Interlocked.Increment(ref _orderVetoes);
            return false;
        }

        // 계좌번호 복호화(콜드·읽기전용). 미저장/미무장 예외 → "" (fail-closed).
        private string RevealAccount()
        {
            if (string.IsNullOrEmpty(_accountKey)) return "";
            try { return ProductionCredentialBinder.WithSecret(_vault, _accountKey, requireArmed: false, s => new string(s)); }
            catch { return ""; }
        }

        public void Dispose()
        {
            try { _tokenService.Dispose(); } catch { }
            try { _http.Dispose(); } catch { }
        }
    }

    // ========================================================================
    // TossLiveAuthClient — 실 토스 OAuth 토큰 발급(client_credentials·Basic). 콜드 패스(백그라운드) 전용.
    //   ★자격증명은 금고에서 '취득 순간에만' 복호화(requireArmed:false 읽기전용)하고 Basic 헤더 구성 후 임시 버퍼를
    //   ZeroMemory 소거 — 평문 client_id/secret 를 힙에 영속시키지 않는다(Think-Beyond 메모리 보안).
    // ========================================================================
    internal sealed class TossLiveAuthClient : ITossAuthClient
    {
        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
        private readonly SecurityAssetVault _vault;
        private readonly string _idKey, _secretKey, _tokenUrl;

        public TossLiveAuthClient(SecurityAssetVault vault, string idKey, string secretKey, string tokenUrl)
        {
            _vault    = vault ?? throw new ArgumentNullException(nameof(vault));
            _idKey    = idKey ?? "";
            _secretKey = secretKey ?? "";
            _tokenUrl = tokenUrl ?? TossOpenApiLiveAdapter.DefaultTokenUrl;
        }

        public TossTokenResult AcquireToken()
        {
            // ★미저장 fail-fast(예외 비용·자동 동결 유발 회피) — 키 없으면 즉시 무효 반환(백그라운드 루프 저비용).
            if (!_vault.Has(_idKey) || !_vault.Has(_secretKey)) return new TossTokenResult(false, "", 0);
            try
            {
                // 금고에서 id·secret 를 '순간 복호화' → Basic 구성(임시 버퍼 즉시 와이프). 미저장/미무장 → 예외 → fail-closed.
                //   ★두 SecretLease 를 직접 using(중첩 람다 span 캡처 CS9108 회피) — 스코프 종료 시 char 버퍼 ZeroMemory 와이프.
                string basic;
                using (var idLease = _vault.Reveal(_idKey, requireArmed: false))
                using (var secretLease = _vault.Reveal(_secretKey, requireArmed: false))
                    basic = BuildBasic(idLease.Value, secretLease.Value);
                if (basic.Length == 0) return new TossTokenResult(false, "", 0);

                using var req = new HttpRequestMessage(HttpMethod.Post, _tokenUrl);
                req.Headers.TryAddWithoutValidation("Authorization", "Basic " + basic);
                req.Content = new StringContent("grant_type=client_credentials", Encoding.UTF8, "application/x-www-form-urlencoded");
                using var res = Http.Send(req);
                if (!res.IsSuccessStatusCode) return new TossTokenResult(false, "", 0);
                byte[] raw = res.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
                return ParseTokenResponse(raw);
            }
            catch { return new TossTokenResult(false, "", 0); }   // 미저장/미무장/네트워크 → fail-closed(토큰 미갱신)
        }

        // "Base64(id:secret)" — 풀 버퍼로 구성 후 평문 바이트 ZeroMemory 소거(base64 결과 문자열만 잔존→전송 후 GC).
        private static string BuildBasic(ReadOnlySpan<char> id, ReadOnlySpan<char> secret)
        {
            int n = id.Length + 1 + secret.Length;
            char[] cbuf = ArrayPool<char>.Shared.Rent(n);
            try
            {
                id.CopyTo(cbuf);
                cbuf[id.Length] = ':';
                secret.CopyTo(cbuf.AsSpan(id.Length + 1));
                var chars = cbuf.AsSpan(0, n);
                int bcount = Encoding.UTF8.GetByteCount(chars);
                byte[] bbuf = ArrayPool<byte>.Shared.Rent(bcount);
                try
                {
                    int wrote = Encoding.UTF8.GetBytes(chars, bbuf);
                    return Convert.ToBase64String(bbuf.AsSpan(0, wrote));
                }
                finally { CryptographicOperations.ZeroMemory(bbuf.AsSpan(0, bcount)); ArrayPool<byte>.Shared.Return(bbuf); }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(cbuf.AsSpan(0, n)));
                ArrayPool<char>.Shared.Return(cbuf);
            }
        }

        private static TossTokenResult ParseTokenResponse(ReadOnlySpan<byte> json)
        {
            try
            {
                var r = new Utf8JsonReader(json);
                string token = ""; long life = 0;
                while (r.Read())
                {
                    if (r.TokenType != JsonTokenType.PropertyName) continue;
                    if      (r.ValueTextEquals("access_token"u8)) { r.Read(); token = r.GetString() ?? ""; }
                    else if (r.ValueTextEquals("expires_in"u8))   { r.Read(); TossJsonParser.TryReadLong(ref r, out life); }
                    else r.Skip();
                }
                return new TossTokenResult(token.Length != 0 && life > 0, token, life);
            }
            catch { return new TossTokenResult(false, "", 0); }
        }
    }
}
