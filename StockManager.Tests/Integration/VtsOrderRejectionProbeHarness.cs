// ============================================================================
// VtsOrderRejectionProbeHarness — [v12.18.41-DEV] KIS 모의투자(VTS) 전용 고의 거부 주문 프로브(테스트 도구 · 실도메인 송출 0)
//
//  목적: 실 KIS 거부 응답(REST rt_cd/msg_cd/msg1) · 모의 체결통보(H0STCNI9 · AES 암호문) 원시 페이로드를 캡처해
//   v12.18.40 의 미추적 거부 래치(비트 23) · KisProtocol 판독기와 실물 정렬을 대조한다.
//  ★도메인 격리(Fail-Closed 하드 가드 · 송출 직전 재검):
//   ① isRealDomain == false ② REST = https · 호스트 정확히 openapivts.koreainvestment.com · 포트 29443(부분 문자열 'openapivts' 포함 판정은
//      openapivts.koreainvestment.com.evil.io 같은 위장 호스트를 통과시키므로 정확 일치) ③ WebSocket = KisRealtimeSession.VtsUri(31000)
//   ④ 주문 TR = VTTC…(모의) 만. 하나라도 어긋나면 InvalidOperationException — HttpMessageInvoker.SendAsync 이전이라 패킷 0.
//  ★제품 경로 무관: 사용자 LocalDatabase · KisLiveBroker · 디스패처 · 무장 키 참조 0(자격증명 = 환경변수만) — liveArmed:false 불변.
//  ★주문 명세(체결 위험 0): 005930 매도 1주 지정가 999,999원 — 상한가(±30%) 대폭 초과 + 보유 없는 매도(잔고 초과) 이중 거부 유도.
//   만에 하나 접수(rt_cd = 0)되면 즉시 전량 취소(VTTC0803U) 후 보고 — 매도 지정가가 시장가 훨씬 위라 체결 불가.
//  ★실행 조건(옵트인 2중): 자격증명 4종(MS2E_KIS_VTS_APPKEY · SECRETKEY · HTSID · ACCOUNT) + MS2E_KIS_VTS_PROBE=1 + KRX 정규장.
//   소크 테스트 자격증명만 넣은 채 전체 스위트를 장중에 돌려도 주문은 나가지 않는다(PROBE=1 별도 요구). 미충족 = Skip(실패 아님).
//  덤프: ITestOutputHelper + %TEMP%\ms2e-vts-probe\probe-yyyyMMdd-HHmmss.txt — 고객 ID · 계좌번호 마스킹(필드 0 · 1 · REST CANO).
// ============================================================================
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MS2E.Infrastructure;
using StockManager.Adapters;
using Xunit;

namespace StockManager.Tests.Integration
{
    /// <summary>VTS 도메인 하드 가드 — 실도메인 · 위장 호스트 · 실 TR · 실 WebSocket 포트 = 예외(송출 전).</summary>
    public static class VtsDomainGuard
    {
        public const string VtsHost = "openapivts.koreainvestment.com";
        public const int VtsPort = 29443;
        public static readonly Uri VtsRestBase = new("https://" + VtsHost + ":29443");

        public static void EnsureVts(Uri restBase, bool isRealDomain, Uri wsUri, string trId)
        {
            if (isRealDomain) throw new InvalidOperationException("VTS 프로브 거부: IsRealDomain = true(실거래 도메인)");
            EnsureVtsRest(restBase);
            if (wsUri != KisRealtimeSession.VtsUri) throw new InvalidOperationException("VTS 프로브 거부: WebSocket ≠ 모의 " + KisRealtimeSession.VtsUri);
            EnsureVtsTr(trId);
        }

        public static void EnsureVtsRest(Uri uri)
        {
            if (uri is null || !uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps
                || !string.Equals(uri.Host, VtsHost, StringComparison.OrdinalIgnoreCase) || uri.Port != VtsPort)
                throw new InvalidOperationException("VTS 프로브 거부: REST 대상 ≠ https://" + VtsHost + ":" + VtsPort);
        }

        public static void EnsureVtsTr(string? trId)
        {
            if (trId is null || !trId.StartsWith("VTTC", StringComparison.Ordinal))
                throw new InvalidOperationException("VTS 프로브 거부: 주문 TR ≠ 모의(VTTC…)");
        }

        /// <summary>송출 직전 재검(요청 URI · tr_id 헤더) 후에만 전송 — 가드 위반 = 예외(전송 0).</summary>
        public static Task<HttpResponseMessage> SendGuardedAsync(HttpMessageInvoker http, HttpRequestMessage req, CancellationToken ct)
        {
            EnsureVtsRest(req.RequestUri!);
            if (req.Headers.TryGetValues("tr_id", out var tr)) foreach (var t in tr) EnsureVtsTr(t);
            return http.SendAsync(req, ct);
        }
    }

    /// <summary>프로브 자격증명(환경변수) · 실행 조건 판정.</summary>
    public sealed record VtsProbeConfig(string AppKey, string Secret, string HtsId, string Cano, string Prdt)
    {
        public const string AppKeyVar = "MS2E_KIS_VTS_APPKEY", SecretVar = "MS2E_KIS_VTS_SECRETKEY", HtsIdVar = "MS2E_KIS_VTS_HTSID",
                            AccountVar = "MS2E_KIS_VTS_ACCOUNT", OptInVar = "MS2E_KIS_VTS_PROBE";

        /// <summary>Skip 사유(null = 실행). env 는 주입식(테스트).</summary>
        public static string? SkipReason(Func<string, string?> env, DateTime utcNow)
        {
            foreach (var v in new[] { AppKeyVar, SecretVar, HtsIdVar, AccountVar })
                if (string.IsNullOrWhiteSpace(env(v))) return "KIS VTS 프로브 자격증명 미설정(" + v + ") — 고의 거부 주문 미송출";
            if (!TryParseAccount(env(AccountVar)!, out _, out _)) return "MS2E_KIS_VTS_ACCOUNT 형식 오류(8자리-2자리) — 미송출";
            if (env(OptInVar) != "1") return "MS2E_KIS_VTS_PROBE ≠ 1(주문 송출 옵트인 없음) — 미송출";
            if (!KisSessionInterlock.IsKrxRegularSession(utcNow)) return "KRX 정규장(평일 09:00 ~ 15:20 KST) 밖 — 미송출";
            return null;
        }

        public static bool TryParseAccount(string s, out string cano, out string prdt)
        {
            cano = prdt = "";
            var p = s.Trim().Split('-');
            if (p.Length != 2 || p[0].Length != 8 || p[1].Length != 2) return false;
            foreach (char ch in p[0] + p[1]) if (ch is < '0' or > '9') return false;
            (cano, prdt) = (p[0], p[1]);
            return true;
        }

        public static VtsProbeConfig FromEnvironment()
        {
            string Env(string v) => Environment.GetEnvironmentVariable(v)!.Trim();
            Assert.True(TryParseAccount(Env(AccountVar), out var cano, out var prdt));
            return new VtsProbeConfig(Env(AppKeyVar), Env(SecretVar), Env(HtsIdVar), cano, prdt);
        }
    }

    public sealed class KisVtsProbeFactAttribute : FactAttribute
    {
        public KisVtsProbeFactAttribute() => Skip = VtsProbeConfig.SkipReason(Environment.GetEnvironmentVariable, DateTime.UtcNow);
    }

    /// <summary>고의 거부 주문 요청 생성기(순수 · 오프라인 검증 가능).</summary>
    public static class VtsRejectionOrder
    {
        public const string Symbol = "005930", TrSellVts = "VTTC0801U", TrCancelVts = "VTTC0803U";
        public const long Qty = 1, LimitWon = 999_999;

        public static HttpRequestMessage Build(Uri restBase, VtsProbeConfig cfg, string token)
        {
            VtsDomainGuard.EnsureVts(restBase, isRealDomain: false, KisRealtimeSession.VtsUri, TrSellVts);
            var req = new HttpRequestMessage(HttpMethod.Post, new Uri(restBase, "/uapi/domestic-stock/v1/trading/order-cash"))
            {
                Content = Json(w =>
                {
                    w.WriteString("CANO", cfg.Cano); w.WriteString("ACNT_PRDT_CD", cfg.Prdt); w.WriteString("PDNO", Symbol);
                    w.WriteString("ORD_DVSN", "00"); w.WriteString("ORD_QTY", Qty.ToString()); w.WriteString("ORD_UNPR", LimitWon.ToString());
                }),
            };
            Headers(req, cfg, token, TrSellVts);
            return req;
        }

        public static HttpRequestMessage BuildCancel(Uri restBase, VtsProbeConfig cfg, string token, long orgNo, long odno)
        {
            VtsDomainGuard.EnsureVts(restBase, isRealDomain: false, KisRealtimeSession.VtsUri, TrCancelVts);
            var req = new HttpRequestMessage(HttpMethod.Post, new Uri(restBase, "/uapi/domestic-stock/v1/trading/order-rvsecncl"))
            {
                Content = Json(w =>
                {
                    w.WriteString("CANO", cfg.Cano); w.WriteString("ACNT_PRDT_CD", cfg.Prdt);
                    w.WriteString("KRX_FWDG_ORD_ORGNO", orgNo.ToString("D5")); w.WriteString("ORGN_ODNO", odno.ToString("D10"));
                    w.WriteString("ORD_DVSN", "00"); w.WriteString("RVSE_CNCL_DVSN_CD", "02"); w.WriteString("ORD_QTY", "0");
                    w.WriteString("ORD_UNPR", "0"); w.WriteString("QTY_ALL_ORD_YN", "Y");
                }),
            };
            Headers(req, cfg, token, TrCancelVts);
            return req;
        }

        private static void Headers(HttpRequestMessage req, VtsProbeConfig cfg, string token, string trId)
        {
            req.Headers.TryAddWithoutValidation("authorization", "Bearer " + token);
            req.Headers.TryAddWithoutValidation("appkey", cfg.AppKey);
            req.Headers.TryAddWithoutValidation("appsecret", cfg.Secret);
            req.Headers.TryAddWithoutValidation("tr_id", trId);
            req.Headers.TryAddWithoutValidation("custtype", "P");
        }

        internal static ByteArrayContent Json(Action<Utf8JsonWriter> body)
        {
            var buf = new ArrayBufferWriter<byte>(256);
            using (var w = new Utf8JsonWriter(buf)) { w.WriteStartObject(); body(w); w.WriteEndObject(); }
            var c = new ByteArrayContent(buf.WrittenSpan.ToArray());
            c.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            return c;
        }
    }

    /// <summary>덤프 마스킹 — 체결통보 필드 0(고객 ID) · 1(계좌) · REST 본문 CANO 값.</summary>
    public static class VtsPayloadMask
    {
        public static string MaskNotice(string plain)
        {
            var f = plain.Split('^');
            for (int i = 0; i < f.Length && i < 2; i++) f[i] = Mask(f[i]);
            return string.Join('^', f);
        }

        public static string MaskAccount(string text, string cano) => string.IsNullOrEmpty(cano) ? text : text.Replace(cano, Mask(cano), StringComparison.Ordinal);

        public static string Mask(string s) => s.Length <= 2 ? new string('*', s.Length) : s[..2] + new string('*', s.Length - 2);
    }

    /// <summary>수신 프레임 사본 캡처(조각 → 메시지 단위 조립).</summary>
    public sealed class VtsCapturingTransport : IKisWsTransport
    {
        private readonly ClientWebSocketTransport _inner = new();
        private readonly List<byte> _pending = new();
        public readonly List<byte[]> Messages = new();

        public Task ConnectAsync(Uri uri, CancellationToken ct)
        {
            if (uri != KisRealtimeSession.VtsUri) throw new InvalidOperationException("VTS 프로브 거부: WebSocket ≠ 모의 " + KisRealtimeSession.VtsUri);
            return _inner.ConnectAsync(uri, ct);
        }

        public Task SendTextAsync(ReadOnlyMemory<byte> payload, CancellationToken ct) => _inner.SendTextAsync(payload, ct);

        public async ValueTask<(int Count, bool EndOfMessage, bool Closed)> ReceiveAsync(Memory<byte> buffer, CancellationToken ct)
        {
            var r = await _inner.ReceiveAsync(buffer, ct).ConfigureAwait(false);
            lock (Messages)
            {
                _pending.AddRange(buffer.Span[..r.Count].ToArray());
                if (r.EndOfMessage) { Messages.Add(_pending.ToArray()); _pending.Clear(); }
            }
            return r;
        }

        public byte[][] Snapshot() { lock (Messages) return Messages.ToArray(); }
        public void Dispose() => _inner.Dispose();
    }

    /// <summary>체결통보 평문 복호(덤프 전용 · 할당 허용 — 판정은 제품 KisOpenApiAdapter 가 한다).</summary>
    public static class VtsNoticeDecoder
    {
        public static bool TryKeys(IEnumerable<byte[]> messages, out byte[] key, out byte[] iv)
        {
            key = new byte[32]; iv = new byte[16];
            foreach (var m in messages)
                if (KisProtocol.TryParseSubscribeAck(m, key, iv, out bool ok, out bool isNotice, out bool hasKeys, out _) && ok && isNotice && hasKeys) return true;
            return false;
        }

        /// <summary>"1|H0STCNI9|001|base64" → 평문(복호 실패 = null). 평문 프레임("0|…")은 본문 그대로.</summary>
        public static string? Plain(byte[] frame, byte[]? key, byte[]? iv)
        {
            string s = Encoding.ASCII.GetString(frame);
            var p = s.Split('|', 4);
            if (p.Length < 4) return null;
            if (p[0] == "0") return p[3];
            if (key is null || iv is null) return null;
            try
            {
                using var aes = Aes.Create();
                aes.Key = key;
                return Encoding.UTF8.GetString(aes.DecryptCbc(Convert.FromBase64String(p[3]), iv, PaddingMode.PKCS7));
            }
            catch (Exception ex) when (ex is CryptographicException or FormatException) { return null; }
        }

        public static bool IsNotice(byte[] frame)
        {
            string s = Encoding.ASCII.GetString(frame, 0, Math.Min(frame.Length, 16));
            return s.Contains("|H0STCNI9|", StringComparison.Ordinal) || s.Contains("|H0STCNI0|", StringComparison.Ordinal);
        }
    }

    /// <summary>VTS REST 최소 클라이언트(토큰 · 접속키) — 가드 경유 전송만.</summary>
    public static class VtsRestClient
    {
        public static async Task<(int Status, byte[] Body)> PostAsync(HttpMessageInvoker http, HttpRequestMessage req, CancellationToken ct)
        {
            using (req)
            using (var resp = await VtsDomainGuard.SendGuardedAsync(http, req, ct).ConfigureAwait(false))
                return ((int)resp.StatusCode, await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false));
        }

        public static async Task<string?> TokenAsync(HttpMessageInvoker http, VtsProbeConfig cfg, CancellationToken ct)
        {
            var req = new HttpRequestMessage(HttpMethod.Post, new Uri(VtsDomainGuard.VtsRestBase, "/oauth2/tokenP"))
            {
                Content = VtsRejectionOrder.Json(w => { w.WriteString("grant_type", "client_credentials"); w.WriteString("appkey", cfg.AppKey); w.WriteString("appsecret", cfg.Secret); }),
            };
            var (_, body) = await PostAsync(http, req, ct).ConfigureAwait(false);
            try
            {
                using var doc = JsonDocument.Parse(body);
                return doc.RootElement.TryGetProperty("access_token", out var t) ? t.GetString() : null;
            }
            catch (JsonException) { return null; }
        }

        public static string? ApprovalKey(HttpMessageInvoker http, VtsProbeConfig cfg)
        {
            var req = new HttpRequestMessage(HttpMethod.Post, new Uri(VtsDomainGuard.VtsRestBase, "/oauth2/Approval"))
            {
                Content = VtsRejectionOrder.Json(w => { w.WriteString("grant_type", "client_credentials"); w.WriteString("appkey", cfg.AppKey); w.WriteString("secretkey", cfg.Secret); }),
            };
            var (status, body) = PostAsync(http, req, CancellationToken.None).GetAwaiter().GetResult();
            return KisLiveBroker.ParseApprovalResponse(status, body);
        }
    }
}
