// ============================================================================
// VtsPreFlightConnectivityProbe — [v12.18.53-DEV] KIS 모의투자(VTS) REST 사전 통신 점검(토큰 발급 · 잔고 조회 · 주문 0 · 테스트 도구)
//
//  목적: 장중 VTS 고의 거부 캡처(VtsOrderRejectionProbeHarness) 전에 portfolio.db 에 DPAPI 로 봉인된 PaperDomestic 자격증명이
//   실제 VTS 서버에서 통하는지 확인한다 — POST /oauth2/tokenP(access_token · 수명) → GET inquire-balance(VTTC8434R · 예수금).
//  ★자격증명: %LOCALAPPDATA%\StockManager\portfolio.db Settings 의 KIS.Profile.PaperDomestic.{AppKey · AppSecret · Account} 만(다른 프로필 키 판독 0).
//   읽기 전용 연결(Mode=ReadOnly · LocalDatabase 미사용 = 스키마 초기화/WAL 전환 쓰기 0). 값이 enc:v1:(DPAPI) 가 아니면 거부(평문 저장 = 미실행).
//  ★송출 허용 목록(그 외 = 예외 · HttpMessageInvoker.SendAsync 이전이라 패킷 0):
//   ① POST https://openapivts.koreainvestment.com:29443/oauth2/tokenP · tr_id 헤더 없음
//   ② GET  같은 호스트 /uapi/domestic-stock/v1/trading/inquire-balance · tr_id 정확히 VTTC8434R(조회) — 주문 TR(VTTC0801U 등) · 실 TR(TTTC…) 거부
//   자동 리다이렉트 꺼짐(3xx 로 실도메인 유도 = 따라가지 않음). 제품 KisLiveBroker · 디스패처 · 무장 키 참조 0 — liveArmed:false 불변.
//  ★마스킹: 출력에는 rt_cd · msg_cd · msg1 · 수명 · 금액 · 지연만. AppKey · Secret · 토큰 · 계좌번호는 앞 2자리 외 '*'(PreFlightRedactor 가 출력 전 전수 치환).
//  ★실행 조건(옵트인): MS2E_KIS_VTS_PREFLIGHT=1 + DB · 프로필 키 3종 존재. 미충족 = Skip(실패 아님). 장 시간 무관(조회 전용).
// ============================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using MS2E.Infrastructure;
using Xunit;

namespace StockManager.Tests.Integration
{
    /// <summary>portfolio.db PaperDomestic 프로필 자격증명(복호 평문 — 출력 금지 · ToString 마스킹).</summary>
    public sealed class VtsPreFlightCredentials
    {
        public const string Profile = "PaperDomestic";
        public const string KeyPrefix = "KIS.Profile." + Profile + ".";
        public const string AppKeyKey = KeyPrefix + "AppKey", SecretKey = KeyPrefix + "AppSecret", AccountKey = KeyPrefix + "Account";
        public const string OptInVar = "MS2E_KIS_VTS_PREFLIGHT";

        public string AppKey { get; }
        public string Secret { get; }
        public string Cano { get; }
        public string Prdt { get; }

        public VtsPreFlightCredentials(string appKey, string secret, string cano, string prdt) => (AppKey, Secret, Cano, Prdt) = (appKey, secret, cano, prdt);

        public override string ToString() => "PaperDomestic(appkey=" + VtsPayloadMask.Mask(AppKey) + " · cano=" + VtsPayloadMask.Mask(Cano) + ")";

        public static string DefaultDbPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "StockManager", "portfolio.db");

        /// <summary>계좌 = "12345678"(상품코드 01 가정) 또는 "12345678-01". 그 외 = false.</summary>
        public static bool TryParseAccount(string s, out string cano, out string prdt)
        {
            cano = prdt = "";
            s = s.Trim();
            if (s.Length == 8) s += "-01";
            return VtsProbeConfig.TryParseAccount(s, out cano, out prdt);
        }

        /// <summary>Skip 사유(null = 실행). env 는 주입식(테스트).</summary>
        public static string? SkipReason(Func<string, string?> env, string dbPath)
        {
            if (env(OptInVar) != "1") return OptInVar + " ≠ 1(사전 통신 점검 옵트인 없음) — 미송출";
            if (!File.Exists(dbPath)) return "portfolio.db 없음 — 미송출";
            return null;
        }

        /// <summary>읽기 전용 판독 → DPAPI 복호. 키 부재 · 비암호화(평문 저장) · 복호 실패 · 계좌 형식 이탈 = 사유 문자열과 함께 null.</summary>
        public static VtsPreFlightCredentials? Load(string dbPath, out string? failure)
        {
            failure = null;
            var raw = new Dictionary<string, string?>(StringComparer.Ordinal);
            var csb = new SqliteConnectionStringBuilder { DataSource = dbPath, Mode = SqliteOpenMode.ReadOnly, Pooling = false };
            using (var conn = new SqliteConnection(csb.ToString()))
            {
                conn.Open();
                foreach (var k in new[] { AppKeyKey, SecretKey, AccountKey })
                {
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = "SELECT Value FROM Settings WHERE [Key]=@k";
                    cmd.Parameters.AddWithValue("@k", k);
                    raw[k] = cmd.ExecuteScalar() as string;
                }
            }
            var plain = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (k, v) in raw)
            {
                if (string.IsNullOrEmpty(v)) { failure = k + " 미등록"; return null; }
                if (!MS2E.Infrastructure.Secret.IsProtected(v)) { failure = k + " DPAPI 미봉인(평문 저장) — 거부"; return null; }
                var p = MS2E.Infrastructure.Secret.Unprotect(v);
                if (string.IsNullOrWhiteSpace(p)) { failure = k + " DPAPI 복호 실패(다른 사용자/PC 봉인)"; return null; }
                plain[k] = p.Trim();
            }
            if (!TryParseAccount(plain[AccountKey], out var cano, out var prdt)) { failure = AccountKey + " 형식 이탈(8자리 또는 8-2자리)"; return null; }
            return new VtsPreFlightCredentials(plain[AppKeyKey], plain[SecretKey], cano, prdt);
        }
    }

    /// <summary>출력 전 민감값 전수 치환(앞 2자리 외 '*') — 짧은 값(≤2자)은 치환 대상에서 제외(일반 텍스트 오염 방지 · 길이 하한 4).</summary>
    public sealed class PreFlightRedactor
    {
        private readonly List<string> _secrets = new();

        public void Add(string? s) { if (!string.IsNullOrEmpty(s) && s.Length >= 4 && !_secrets.Contains(s)) _secrets.Add(s); }

        public string Redact(string? text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            _secrets.Sort(static (a, b) => b.Length.CompareTo(a.Length));     // 긴 값 우선(부분 포함 관계 — 계좌 ⊂ 계좌-상품코드)
            foreach (var s in _secrets) text = text.Replace(s, VtsPayloadMask.Mask(s), StringComparison.Ordinal);
            return text;
        }
    }

    /// <summary>사전 점검 송출 허용 목록 가드 — 토큰 발급(POST) · 잔고 조회(GET VTTC8434R) 두 요청만.</summary>
    public static class VtsPreFlightGuard
    {
        public const string TokenPath = "/oauth2/tokenP";
        public const string BalancePath = "/uapi/domestic-stock/v1/trading/inquire-balance";
        public const string TrBalanceVts = "VTTC8434R";

        public static void EnsureAllowed(HttpRequestMessage req)
        {
            VtsDomainGuard.EnsureVtsRest(req.RequestUri!);                                   // https · 호스트 정확 일치 · 29443
            string path = req.RequestUri!.AbsolutePath;
            bool hasTr = req.Headers.TryGetValues("tr_id", out var trs);
            if (req.Method == HttpMethod.Post && path == TokenPath)
            {
                if (hasTr) throw new InvalidOperationException("VTS 사전 점검 거부: 토큰 발급에 tr_id 헤더");
                return;
            }
            if (req.Method == HttpMethod.Get && path == BalancePath && hasTr)
            {
                int n = 0;
                foreach (var t in trs!) { if (t != TrBalanceVts) throw new InvalidOperationException("VTS 사전 점검 거부: 조회 TR ≠ " + TrBalanceVts); n++; }
                if (n == 1) return;
            }
            throw new InvalidOperationException("VTS 사전 점검 거부: 허용 목록 밖 요청(" + req.Method + " " + path + ")");
        }

        public static Task<HttpResponseMessage> SendAsync(HttpMessageInvoker http, HttpRequestMessage req, CancellationToken ct)
        {
            EnsureAllowed(req);
            return http.SendAsync(req, ct);
        }

        /// <summary>실 송출용 핸들러 — 자동 리다이렉트 꺼짐(3xx = 그대로 실패 판정).</summary>
        public static SocketsHttpHandler CreateHandler() => new() { AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(10) };

        public static HttpMessageInvoker CreateInvoker() => new(CreateHandler(), disposeHandler: true);
    }

    /// <summary>요청 생성기(순수 · 오프라인 검증 가능).</summary>
    public static class VtsPreFlightRequests
    {
        public static HttpRequestMessage Token(VtsPreFlightCredentials c) =>
            new(HttpMethod.Post, new Uri(VtsDomainGuard.VtsRestBase, VtsPreFlightGuard.TokenPath))
            {
                Content = VtsRejectionOrder.Json(w => { w.WriteString("grant_type", "client_credentials"); w.WriteString("appkey", c.AppKey); w.WriteString("appsecret", c.Secret); }),
            };

        public static HttpRequestMessage Balance(VtsPreFlightCredentials c, string token)
        {
            var q = "CANO=" + c.Cano + "&ACNT_PRDT_CD=" + c.Prdt + "&AFHR_FLPR_YN=N&OFL_YN=&INQR_DVSN=02&UNPR_DVSN=01"
                  + "&FUND_STTL_ICLD_YN=N&FNCG_AMT_AUTO_RDPT_YN=N&PRCS_DVSN=00&CTX_AREA_FK100=&CTX_AREA_NK100=";
            var req = new HttpRequestMessage(HttpMethod.Get, new Uri(VtsDomainGuard.VtsRestBase, VtsPreFlightGuard.BalancePath + "?" + q));
            req.Headers.TryAddWithoutValidation("authorization", "Bearer " + token);
            req.Headers.TryAddWithoutValidation("appkey", c.AppKey);
            req.Headers.TryAddWithoutValidation("appsecret", c.Secret);
            req.Headers.TryAddWithoutValidation("tr_id", VtsPreFlightGuard.TrBalanceVts);
            req.Headers.TryAddWithoutValidation("custtype", "P");
            return req;
        }
    }

    /// <summary>잔고 응답 요약(민감 필드 0 — 금액 · 행 수 · 결과 코드만).</summary>
    public readonly record struct VtsBalanceSummary(bool Parsed, string RtCd, string MsgCd, string Msg1, int HoldingRows, long DepositWon, long TotalEvalWon, long NetAssetWon)
    {
        public bool Ok => Parsed && RtCd == "0";

        public static VtsBalanceSummary Parse(ReadOnlySpan<byte> body)
        {
            try
            {
                using var doc = JsonDocument.Parse(body.ToArray());
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return default;                    // 배열/스칼라 최상위 = 형식 이탈(TryGetProperty 예외 회피)
                string S(JsonElement e, string n) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
                long W(JsonElement e, string n) => long.TryParse(S(e, n).Split('.')[0], out var v) ? v : -1;
                int rows = root.TryGetProperty("output1", out var o1) && o1.ValueKind == JsonValueKind.Array ? o1.GetArrayLength() : -1;
                var o2 = root.TryGetProperty("output2", out var a2) && a2.ValueKind == JsonValueKind.Array && a2.GetArrayLength() > 0 ? a2[0] : default;
                return new VtsBalanceSummary(true, S(root, "rt_cd"), S(root, "msg_cd"), S(root, "msg1").Trim(), rows, W(o2, "dnca_tot_amt"), W(o2, "tot_evlu_amt"), W(o2, "nass_amt"));
            }
            catch (JsonException) { return default; }
        }
    }

    /// <summary>점검 결과(마스킹된 출력 줄만 보관).</summary>
    public sealed class VtsPreFlightReport
    {
        public bool TokenIssued;
        public long TokenExpiresInSeconds;
        public string TokenExpiredAtKst = "";
        public int TokenHttpStatus;
        public double TokenLatencyMs;
        public bool BalanceOk;
        public int BalanceHttpStatus;
        public double BalanceLatencyMs;
        public VtsBalanceSummary Balance;
        public readonly List<string> Lines = new();
    }

    public static class VtsPreFlightConnectivityProbe
    {
        public static async Task<VtsPreFlightReport> RunAsync(HttpMessageInvoker http, VtsPreFlightCredentials c, CancellationToken ct)
        {
            var rep = new VtsPreFlightReport();
            var red = new PreFlightRedactor();
            red.Add(c.AppKey); red.Add(c.Secret); red.Add(c.Cano); red.Add(c.Cano + c.Prdt); red.Add(c.Cano + "-" + c.Prdt);
            void Line(string s) => rep.Lines.Add(red.Redact(s));
            Line("[preflight] 대상 " + VtsDomainGuard.VtsRestBase + " · 프로필 " + c);

            // ① 토큰 발급
            byte[] tokenBody;
            var sw = Stopwatch.StartNew();
            using (var req = VtsPreFlightRequests.Token(c))
            using (var resp = await VtsPreFlightGuard.SendAsync(http, req, ct).ConfigureAwait(false))
            {
                tokenBody = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
                rep.TokenHttpStatus = (int)resp.StatusCode;
            }
            rep.TokenLatencyMs = sw.Elapsed.TotalMilliseconds;
            var tok = new byte[4096];
            string token = "";
            if (rep.TokenHttpStatus == 200 && KisProtocol.TryParseToken(tokenBody, tok, out int tokLen, out long life, out long expTicks))
            {
                rep.TokenIssued = true;
                rep.TokenExpiresInSeconds = life;
                rep.TokenExpiredAtKst = expTicks > 0 ? new DateTime(expTicks).ToString("yyyy-MM-dd HH:mm:ss") : "";
                token = Encoding.ASCII.GetString(tok, 0, tokLen);
            }
            Array.Clear(tok);
            red.Add(token);
            if (rep.TokenIssued)
                Line("[token] 발급 성공 · HTTP " + rep.TokenHttpStatus + " · 토큰 " + VtsPayloadMask.Mask(token) + "(" + token.Length + "자) · expires_in " + rep.TokenExpiresInSeconds
                     + "s · 만료(KST) " + rep.TokenExpiredAtKst + " · 지연 " + rep.TokenLatencyMs.ToString("F1") + "ms");
            else
            {
                var err = VtsBalanceSummary.Parse(tokenBody);
                using var d = TryDoc(tokenBody);
                string code = d is not null && d.RootElement.ValueKind == JsonValueKind.Object && d.RootElement.TryGetProperty("error_code", out var ec) ? ec.ToString() : err.MsgCd;
                string desc = d is not null && d.RootElement.ValueKind == JsonValueKind.Object && d.RootElement.TryGetProperty("error_description", out var ed) ? ed.ToString() : err.Msg1;
                Line("[token] 발급 실패 · HTTP " + rep.TokenHttpStatus + " · code " + code + " · " + desc + " · 지연 " + rep.TokenLatencyMs.ToString("F1") + "ms");
                return rep;
            }

            // ② 잔고 조회(VTTC8434R)
            byte[] balBody;
            sw.Restart();
            using (var req = VtsPreFlightRequests.Balance(c, token))
            using (var resp = await VtsPreFlightGuard.SendAsync(http, req, ct).ConfigureAwait(false))
            {
                balBody = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
                rep.BalanceHttpStatus = (int)resp.StatusCode;
            }
            rep.BalanceLatencyMs = sw.Elapsed.TotalMilliseconds;
            token = "";
            rep.Balance = VtsBalanceSummary.Parse(balBody);
            rep.BalanceOk = rep.BalanceHttpStatus == 200 && rep.Balance.Ok;
            Line("[balance] " + (rep.BalanceOk ? "조회 성공" : "조회 실패") + " · HTTP " + rep.BalanceHttpStatus + " · rt_cd " + rep.Balance.RtCd + " · msg_cd " + rep.Balance.MsgCd
                 + " · " + rep.Balance.Msg1 + " · 보유 행 " + rep.Balance.HoldingRows + " · 예수금 " + rep.Balance.DepositWon.ToString("N0") + "원 · 총평가 "
                 + rep.Balance.TotalEvalWon.ToString("N0") + "원 · 순자산 " + rep.Balance.NetAssetWon.ToString("N0") + "원 · 지연 " + rep.BalanceLatencyMs.ToString("F1") + "ms");
            return rep;
        }

        private static JsonDocument? TryDoc(byte[] body)
        {
            try { return JsonDocument.Parse(body); }
            catch (JsonException) { return null; }
        }
    }

    public sealed class KisVtsPreFlightFactAttribute : FactAttribute
    {
        public KisVtsPreFlightFactAttribute() => Skip = VtsPreFlightCredentials.SkipReason(Environment.GetEnvironmentVariable, VtsPreFlightCredentials.DefaultDbPath);
    }
}
