// ============================================================================
// TossTokenService — 1단계: 토스증권 OAuth 토큰 수명주기 격리(핫패스↔갱신 분리)
//
//  ⚠️ liveArmed:false 불변 — 본 서비스는 '구성'만. 자동 배선·실 키 로드는 호출부 옵트인.
//
//  ── 인증 격리(명령 1단계 §1) ───────────────────────────────────────────────
//   · 실 주문 핫패스(TossLiveBroker.Place → ITossTransport)는 CurrentAccessToken()을
//     '락-프리 단일 읽기'로만 만진다(네트워크 블록 0·할당 0). 토큰 만료/갱신 네트워크 I/O는
//     백그라운드 스레드(RefreshLoop)가 콜드 패스에서 수행 — 두 경로를 물리 분리한다.
//   · 토큰 스냅샷은 불변 참조(Snapshot)로 원자 스왑(Volatile.Write) — 리더는 락 없이 관측.
//     갱신은 단일-비행 CAS 게이트(_refreshing)로 중첩 방지(다중 라이터 경합 0).
//
//  ── fail-closed(적대검증 — fail-open 원천 차단) ────────────────────────────
//   · 스냅샷 없음(초기)·만료(경계 포함)·수명≤0·취득 실패 → CurrentAccessToken()=="".
//     빈 토큰은 인증 헤더 미부착 → REST 401 → 상위 브로커 (false,msg) 거부. '모호한 통과' 0.
//   · 갱신이 '연속' _maxConsecutiveFailures 회 실패 → ForceFreeze(1회 래치). 성공 시 스트릭 리셋.
//   · 수명 오버플로(now+life*ticks > long) → 취득 실패로 취급(오염 스냅샷 금지).
//
//  ★clock 주입 필수 — 단위 ticks(100ns, DateTime.Ticks 의미) 일관·결정론 테스트.
// ============================================================================
using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace MS2E.Infrastructure
{
    /// <summary>OAuth 토큰 1회 취득 결과(불변 값). Ok=false 또는 LifetimeSeconds≤0 은 무효로 간주(fail-closed).</summary>
    public readonly record struct TossTokenResult(bool Ok, string AccessToken, long LifetimeSeconds);

    /// <summary>
    /// 토스증권 OAuth 토큰 발급 운반 추상. 실 엔드포인트·client_id/secret·서명 스킴을 이 뒤로 격리
    /// (테스트는 페이크 주입). 구현체는 동기 blocking 취득(백그라운드 스레드에서만 호출) — throw 허용(상위 흡수).
    /// </summary>
    public interface ITossAuthClient
    {
        /// <summary>토큰 1회 취득(콜드 패스). 실패 시 Ok=false 반환 또는 throw(서비스가 실패로 계상).</summary>
        TossTokenResult AcquireToken();
    }

    /// <summary>
    /// 토스증권 액세스 토큰 수명주기 서비스(싱글톤 수명 전제). 리더 락-프리·갱신 백그라운드 격리.
    /// </summary>
    public sealed class TossTokenService : IDisposable
    {
        // 불변 스냅샷 — 참조 스왑은 원자적. 리더는 Volatile.Read 로 최신 관측(락 0).
        private sealed class Snapshot
        {
            public readonly string Access;
            public readonly long   ExpiresAtTicks;   // 절대 만료 시각(ticks). clock() >= 이 값이면 만료.
            public Snapshot(string access, long expiresAtTicks) { Access = access; ExpiresAtTicks = expiresAtTicks; }
        }

        private readonly ITossAuthClient          _auth;
        private readonly Func<long>               _clock;               // 단위 ticks(100ns) — 주입 필수
        private readonly OrderAttestationService? _att;                 // 동결 채널(연속 실패 임계 초과)
        private readonly long                     _refreshSkewTicks;    // 만료 이 시각 전에 선제 갱신
        private readonly long                     _maxConsecutiveFailures;

        private Snapshot?      _snapshot;       // 락-프리 참조 스왑 대상(volatile 접근)
        private int            _refreshing;     // 단일-비행 CAS 게이트: 0=idle, 1=refreshing
        private long           _consecutiveFailures;
        private int            _frozenLatch;    // 임계 동결 1회 래치(재동결 방지)

        // 백그라운드 갱신 루프
        private Thread?        _loopThread;
        private volatile bool  _running;
        private readonly int   _loopTickMs;     // 갱신 필요 여부 점검 주기(콜드 패스)

        /// <param name="auth">토큰 발급 운반(실 HTTP 또는 페이크).</param>
        /// <param name="clock">현재 시각(ticks·100ns) 공급 — 주입 필수(단위/결정론).</param>
        /// <param name="attestation">동결 채널(옵션). null 이면 카운터만 유지(테스트 격리).</param>
        /// <param name="refreshSkewSeconds">만료 이 초 전에 선제 갱신(기본 60s).</param>
        /// <param name="maxConsecutiveFailures">연속 갱신 실패 임계(초과 시 동결). ≤0 이면 자동 동결 비활성.</param>
        /// <param name="loopTickMs">백그라운드 점검 주기(ms).</param>
        public TossTokenService(
            ITossAuthClient auth,
            Func<long> clock,
            OrderAttestationService? attestation = null,
            long refreshSkewSeconds = 60,
            long maxConsecutiveFailures = 5,
            int loopTickMs = 1000)
        {
            _auth  = auth  ?? throw new ArgumentNullException(nameof(auth));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));   // 단위 오염 차단(기본값 금지)
            _att   = attestation;
            _refreshSkewTicks = (refreshSkewSeconds < 0 ? 0 : refreshSkewSeconds) * TimeSpan.TicksPerSecond;
            // ≤0 → 자동 동결 비활성(사실상 무한 임계). TossLiveBroker.parseFailFreezeThreshold 관례 계승.
            _maxConsecutiveFailures = maxConsecutiveFailures <= 0 ? long.MaxValue : maxConsecutiveFailures;
            _loopTickMs = loopTickMs < 50 ? 50 : loopTickMs;
        }

        // ── 텔레메트리(관제) ──────────────────────────────────────────────────
        public long ConsecutiveFailures => Interlocked.Read(ref _consecutiveFailures);
        public bool Running             => _running;

        /// <summary>현재 유효 토큰 유무(빈 문자열이면 false).</summary>
        public bool HasValidToken => CurrentAccessToken().Length != 0;

        /// <summary>현재 토큰 만료까지 남은 초. 스냅샷 없음 → -1(N/A) · 만료(경계 포함) → 0. 관제 표시용(δ=0·기존 상태 판독).</summary>
        public long SecondsUntilExpiry()
        {
            Snapshot? s = Volatile.Read(ref _snapshot);
            if (s is null) return -1;
            long remain = s.ExpiresAtTicks - _clock();
            return remain <= 0 ? 0 : remain / TimeSpan.TicksPerSecond;
        }

        // ====================================================================
        // 핫패스(락-프리) — 실 주문 경로가 만지는 유일 지점. 네트워크 블록 0·할당 0.
        // ====================================================================
        /// <summary>
        /// 현재 유효 액세스 토큰. 없음/만료(경계 포함)면 "" (fail-closed — 미인증 주문 송출 원천 차단).
        /// </summary>
        public string CurrentAccessToken()
        {
            Snapshot? s = Volatile.Read(ref _snapshot);
            if (s is null) return "";
            // 만료 경계 포함(>=): 정확히 만료 tick 이면 무효 — 경계 통과 fail-open 차단.
            if (_clock() >= s.ExpiresAtTicks) return "";
            return s.Access;
        }

        // ====================================================================
        // 콜드 패스(백그라운드) — 토큰 취득/갱신. 할당·blocking 허용(핫패스 아님).
        // ====================================================================

        /// <summary>백그라운드 갱신 루프 시작. 이미 동작 중이면 무동작.</summary>
        public void Start()
        {
            if (_running) return;
            _running = true;
            _loopThread = new Thread(RefreshLoop) { IsBackground = true, Name = "TossTokenRefresh" };
            _loopThread.Start();
        }

        /// <summary>백그라운드 갱신 루프 정지(Join). 핫패스 무접촉.</summary>
        public void Stop()
        {
            _running = false;
            var t = _loopThread;
            if (t is not null && t.IsAlive) t.Join(2000);
            _loopThread = null;
        }

        private void RefreshLoop()
        {
            // 진입 즉시 1회 취득 시도(초기 스냅샷 확보) 후 주기 점검.
            MaybeRefresh();
            while (_running)
            {
                int slept = 0;
                while (slept < _loopTickMs && _running) { int c = Math.Min(50, _loopTickMs - slept); Thread.Sleep(c); slept += c; }
                if (!_running) break;
                MaybeRefresh();
            }
        }

        /// <summary>스냅샷이 없거나 (만료-skew) 도달 시에만 갱신. 결정론 테스트용으로 공개.</summary>
        public void MaybeRefresh()
        {
            Snapshot? s = Volatile.Read(ref _snapshot);
            bool need = s is null || _clock() >= (s.ExpiresAtTicks - _refreshSkewTicks);
            if (need) RefreshNow();
        }

        /// <summary>즉시 1회 갱신(단일-비행). 결정론 테스트용으로 공개.</summary>
        public void RefreshNow()
        {
            // 단일-비행: 루프·수동 호출이 겹쳐도 동시 취득 1회로 직렬화(중복 네트워크·경합 0).
            if (Interlocked.CompareExchange(ref _refreshing, 1, 0) != 0) return;
            try
            {
                TossTokenResult r;
                try { r = _auth.AcquireToken(); }
                catch (Exception ex) { OnRefreshFailure("토큰 취득 예외: " + ex.Message); return; }

                // ★fail-closed: 무효 결과(실패/빈 토큰/수명≤0)는 스냅샷을 절대 갱신하지 않음(오염 금지).
                if (!r.Ok || string.IsNullOrEmpty(r.AccessToken) || r.LifetimeSeconds <= 0)
                {
                    OnRefreshFailure("토큰 취득 실패 또는 무효 수명");
                    return;
                }

                long now = _clock();
                long expires;
                try { expires = checked(now + r.LifetimeSeconds * TimeSpan.TicksPerSecond); }
                catch (OverflowException) { OnRefreshFailure("토큰 수명 오버플로"); return; }

                Volatile.Write(ref _snapshot, new Snapshot(r.AccessToken, expires));
                Interlocked.Exchange(ref _consecutiveFailures, 0);   // 성공 → 스트릭 리셋
            }
            finally { Volatile.Write(ref _refreshing, 0); }
        }

        // 연속 실패 계상 — 임계 초과 시 1회 래치 동결. 기존 스냅샷은 만료까지 유효(만료되면 핫패스가 "" 반환).
        private void OnRefreshFailure(string reason)
        {
            long n = Interlocked.Increment(ref _consecutiveFailures);
            if (n >= _maxConsecutiveFailures && Interlocked.Exchange(ref _frozenLatch, 1) == 0)
            {
                try { _att?.ForceFreeze($"TossTokenService: {reason} (연속 {n}회) — 인증 fail-closed"); }
                catch { /* 동결 실패해도 계상은 정직 유지 */ }
            }
        }

        public void Dispose() => Stop();
    }

    // ========================================================================
    // ITossAuthClient 의 실 HttpClient 어댑터 골격 — 엔드포인트·자격증명 미확정(openQuestions).
    //   ★liveArmed:false 불변: '구성'만. 실 키 로드·자동 배선은 호출부 옵트인.
    // ========================================================================
    /// <summary>
    /// 실 토스증권 OAuth 토큰 발급 골격(client_credentials 가정). 실 URL·필드 스키마·서명 미확정 → openQuestions.
    /// </summary>
    public sealed class TossOAuthClient : ITossAuthClient
    {
        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
        private readonly string _tokenUrl;
        private readonly string _clientId;
        private readonly string _clientSecret;

        public TossOAuthClient(string tokenUrl, string clientId, string clientSecret)
        {
            _tokenUrl     = string.IsNullOrWhiteSpace(tokenUrl) ? "https://api.toss-invest.example/oauth2/token" : tokenUrl;
            _clientId     = clientId ?? "";
            _clientSecret = clientSecret ?? "";
        }

        public TossTokenResult AcquireToken()
        {
            // 콜드 패스(백그라운드) — 할당·blocking 허용. 실패는 Ok=false 로 정직 반환(서비스가 계상).
            var body = new StringContent(
                $"{{\"grant_type\":\"client_credentials\",\"client_id\":\"{_clientId}\",\"client_secret\":\"{_clientSecret}\"}}",
                Encoding.UTF8, "application/json");
            using var req = new HttpRequestMessage(HttpMethod.Post, _tokenUrl) { Content = body };
            using var res = Http.Send(req);
            string raw = res.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            if (!res.IsSuccessStatusCode) return new TossTokenResult(false, "", 0);
            try
            {
                using var doc = JsonDocument.Parse(raw);
                var root = doc.RootElement;
                string token = root.TryGetProperty("access_token", out var t) ? (t.GetString() ?? "") : "";
                long life = root.TryGetProperty("expires_in", out var e) && e.TryGetInt64(out var v) ? v : 0;
                return new TossTokenResult(token.Length != 0 && life > 0, token, life);
            }
            catch { return new TossTokenResult(false, "", 0); }
        }
    }
}
