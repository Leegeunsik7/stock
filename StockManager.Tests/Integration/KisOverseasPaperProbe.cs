// ============================================================================
// KisOverseasPaperProbe — [v12.18.53-DEV] KIS 해외주식 모의투자(VTS) REST 통신 점검(토큰 발급 · VTTS3012R 해외 잔고 · 주문 0 · 테스트 도구)
//
//  목적: portfolio.db 에 DPAPI 로 봉인된 PaperOverseas 자격증명이 실제 VTS 서버에서 통하는지 확인하고 해외 잔고 조회 지연을 실측한다 —
//   POST /oauth2/tokenP(access_token · 수명) → GET /uapi/overseas-stock/v1/trading/inquire-balance(VTTS3012R · 외화 매입/평가손익).
//  ★자격증명: Settings 의 KIS.Profile.PaperOverseas.{AppKey · AppSecret · Account} 만 — VtsPreFlightCredentials.Load(db, "PaperOverseas")
//   (읽기 전용 연결 · 평문 저장/미등록/복호 실패/계좌 형식 이탈 = 거부 · 허용 프로필 = 모의 2종뿐).
//  ★송출 허용 목록(그 외 = 예외 · HttpMessageInvoker.SendAsync 이전이라 패킷 0):
//   ① POST https://openapivts.koreainvestment.com:29443/oauth2/tokenP · tr_id 헤더 없음
//   ② GET  같은 호스트 /uapi/overseas-stock/v1/trading/inquire-balance · tr_id 정확히 VTTS3012R(조회) — 해외 주문 TR(VTTT1002U 등) ·
//      실 TR(TTTS3012R) · 국내 경로 거부. 자동 리다이렉트 꺼짐. 제품 KisLiveBroker · 디스패처 · 무장 키 참조 0 — liveArmed:false 불변.
//  ★마스킹: 출력에는 rt_cd · msg_cd · msg1 · 수명 · USD 금액 · 지연만. AppKey · Secret · 토큰 · 계좌번호는 앞 2자리 외 '*'(PreFlightRedactor 전수 치환).
//  ★실행 조건(옵트인): MS2E_KIS_VTS_OVERSEAS=1 + DB 존재. 미충족 = Skip(실패 아님). 장 시간 무관(조회 전용).
//  요청 파라미터: OVRS_EXCG_CD = NASD(모의 = 거래소별 개별 조회 · 실전의 'NASD = 미국 전체' 와 다름) · TR_CRCY_CD = USD.
// ============================================================================
using System;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MS2E.Infrastructure;
using StockManager.Engines.Pricing;
using Xunit;

namespace StockManager.Tests.Integration
{
    /// <summary>해외 모의 송출 허용 목록 가드 — 토큰 발급(POST) · 해외 잔고 조회(GET VTTS3012R) 두 요청만.</summary>
    public static class KisOverseasPaperGuard
    {
        public const string BalancePath = "/uapi/overseas-stock/v1/trading/inquire-balance";
        public const string TrBalanceVts = "VTTS3012R";
        public const string OptInVar = "MS2E_KIS_VTS_OVERSEAS";

        public static void EnsureAllowed(HttpRequestMessage req)
        {
            VtsDomainGuard.EnsureVtsRest(req.RequestUri!);                                   // https · 호스트 정확 일치 · 29443
            string path = req.RequestUri!.AbsolutePath;
            bool hasTr = req.Headers.TryGetValues("tr_id", out var trs);
            if (req.Method == HttpMethod.Post && path == VtsPreFlightGuard.TokenPath)
            {
                if (hasTr) throw new InvalidOperationException("해외 모의 점검 거부: 토큰 발급에 tr_id 헤더");
                return;
            }
            if (req.Method == HttpMethod.Get && path == BalancePath && hasTr && req.Content is null)
            {
                int n = 0;
                foreach (var t in trs!) { if (t != TrBalanceVts) throw new InvalidOperationException("해외 모의 점검 거부: 조회 TR ≠ " + TrBalanceVts); n++; }
                if (n == 1) return;
            }
            throw new InvalidOperationException("해외 모의 점검 거부: 허용 목록 밖 요청(" + req.Method + " " + path + ")");
        }

        public static Task<HttpResponseMessage> SendAsync(HttpMessageInvoker http, HttpRequestMessage req, CancellationToken ct)
        {
            EnsureAllowed(req);
            return http.SendAsync(req, ct);
        }

        /// <summary>Skip 사유(null = 실행). env 는 주입식(테스트).</summary>
        public static string? SkipReason(Func<string, string?> env, string dbPath)
        {
            if (env(OptInVar) != "1") return OptInVar + " ≠ 1(해외 모의 통신 점검 옵트인 없음) — 미송출";
            if (!System.IO.File.Exists(dbPath)) return "portfolio.db 없음 — 미송출";
            return null;
        }
    }

    /// <summary>해외 잔고 요청 생성기(순수 · 오프라인 검증 가능).</summary>
    public static class KisOverseasPaperRequests
    {
        public const string Exchange = "NASD", Currency = "USD";

        public static HttpRequestMessage Balance(VtsPreFlightCredentials c, string token)
        {
            var q = "CANO=" + c.Cano + "&ACNT_PRDT_CD=" + c.Prdt + "&OVRS_EXCG_CD=" + Exchange + "&TR_CRCY_CD=" + Currency + "&CTX_AREA_FK200=&CTX_AREA_NK200=";
            var req = new HttpRequestMessage(HttpMethod.Get, new Uri(VtsDomainGuard.VtsRestBase, KisOverseasPaperGuard.BalancePath + "?" + q));
            req.Headers.TryAddWithoutValidation("authorization", "Bearer " + token);
            req.Headers.TryAddWithoutValidation("appkey", c.AppKey);
            req.Headers.TryAddWithoutValidation("appsecret", c.Secret);
            req.Headers.TryAddWithoutValidation("tr_id", KisOverseasPaperGuard.TrBalanceVts);
            req.Headers.TryAddWithoutValidation("custtype", "P");
            return req;
        }
    }

    /// <summary>해외 잔고 응답 요약(민감 필드 0 — USD 금액 raw 10^8 · 행 수 · 결과 코드만 · 금액 부재/형식 이탈 = Missing).</summary>
    public readonly record struct VtsOverseasBalanceSummary(bool Parsed, string RtCd, string MsgCd, string Msg1, int HoldingRows, long PurchaseUsdRaw, long EvalPnlUsdRaw, long TotalPnlUsdRaw)
    {
        public const long Missing = long.MinValue;
        public bool Ok => Parsed && RtCd == "0";

        public static string Usd(long raw) => raw == Missing ? "n/a" : "$" + ((decimal)raw / (decimal)UsTickAligner.Scale).ToString("N2", CultureInfo.InvariantCulture);

        public static VtsOverseasBalanceSummary Parse(ReadOnlySpan<byte> body)
        {
            try
            {
                using var doc = JsonDocument.Parse(body.ToArray());
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return default;                    // 배열/스칼라 최상위 = 형식 이탈(TryGetProperty 예외 회피)
                string S(JsonElement e, string n) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
                long U(JsonElement e, string n)
                {
                    if (!decimal.TryParse(S(e, n), NumberStyles.Number, CultureInfo.InvariantCulture, out var d)) return Missing;
                    if (Math.Abs(d) >= (decimal)long.MaxValue / UsTickAligner.Scale) return Missing;      // raw 오버플로(곱셈 전 차단 · decimal 예외 0)
                    return (long)decimal.Round(d * UsTickAligner.Scale);
                }
                int rows = root.TryGetProperty("output1", out var o1) && o1.ValueKind == JsonValueKind.Array ? o1.GetArrayLength() : -1;
                // output2 = 객체(VTTS3012R 명세) — 배열로 오는 변형도 첫 원소로 수용
                var o2 = root.TryGetProperty("output2", out var a2)
                    ? a2.ValueKind == JsonValueKind.Object ? a2 : a2.ValueKind == JsonValueKind.Array && a2.GetArrayLength() > 0 ? a2[0] : default
                    : default;
                return new VtsOverseasBalanceSummary(true, S(root, "rt_cd"), S(root, "msg_cd"), S(root, "msg1").Trim(), rows,
                    U(o2, "frcr_pchs_amt1"), U(o2, "tot_evlu_pfls_amt"), U(o2, "ovrs_tot_pfls"));
            }
            catch (JsonException) { return default; }
        }
    }

    /// <summary>해외 점검 결과(마스킹된 출력 줄만 보관).</summary>
    public sealed class KisOverseasPaperReport
    {
        public bool TokenIssued;
        public long TokenExpiresInSeconds;
        public int TokenHttpStatus;
        public double TokenLatencyMs;
        public bool BalanceOk;
        public int BalanceHttpStatus;
        public double BalanceLatencyMs;
        public VtsOverseasBalanceSummary Balance;
        public readonly System.Collections.Generic.List<string> Lines = new();
    }

    public static class KisOverseasPaperProbe
    {
        public static async Task<KisOverseasPaperReport> RunAsync(HttpMessageInvoker http, VtsPreFlightCredentials c, CancellationToken ct)
        {
            if (c.ProfileName != VtsPreFlightCredentials.OverseasProfile)
                throw new InvalidOperationException("해외 모의 점검 거부: 프로필 ≠ " + VtsPreFlightCredentials.OverseasProfile);
            var rep = new KisOverseasPaperReport();
            var red = new PreFlightRedactor();
            red.Add(c.AppKey); red.Add(c.Secret); red.Add(c.Cano); red.Add(c.Cano + c.Prdt); red.Add(c.Cano + "-" + c.Prdt);
            void Line(string s) => rep.Lines.Add(red.Redact(s));
            Line("[overseas] 대상 " + VtsDomainGuard.VtsRestBase + " · 프로필 " + c + " · 거래소 " + KisOverseasPaperRequests.Exchange + " · " + KisOverseasPaperRequests.Currency);

            // ① 토큰 발급
            byte[] tokenBody;
            var sw = Stopwatch.StartNew();
            using (var req = VtsPreFlightRequests.Token(c))
            using (var resp = await KisOverseasPaperGuard.SendAsync(http, req, ct).ConfigureAwait(false))
            {
                tokenBody = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
                rep.TokenHttpStatus = (int)resp.StatusCode;
            }
            rep.TokenLatencyMs = sw.Elapsed.TotalMilliseconds;
            var tok = new byte[4096];
            string token = "";
            if (rep.TokenHttpStatus == 200 && KisProtocol.TryParseToken(tokenBody, tok, out int tokLen, out long life, out _))
            {
                rep.TokenIssued = true;
                rep.TokenExpiresInSeconds = life;
                token = Encoding.ASCII.GetString(tok, 0, tokLen);
            }
            Array.Clear(tok);
            red.Add(token);
            if (!rep.TokenIssued)
            {
                var err = VtsOverseasBalanceSummary.Parse(tokenBody);
                string code = err.MsgCd, desc = err.Msg1;
                try
                {
                    using var d = JsonDocument.Parse(tokenBody);
                    if (d.RootElement.ValueKind == JsonValueKind.Object)
                    {
                        if (d.RootElement.TryGetProperty("error_code", out var ec)) code = ec.ToString();
                        if (d.RootElement.TryGetProperty("error_description", out var ed)) desc = ed.ToString();
                    }
                }
                catch (JsonException) { }
                Line("[token] 발급 실패 · HTTP " + rep.TokenHttpStatus + " · code " + code + " · " + desc + " · 지연 " + rep.TokenLatencyMs.ToString("F1") + "ms");
                return rep;
            }
            Line("[token] 발급 성공 · HTTP " + rep.TokenHttpStatus + " · 토큰 " + VtsPayloadMask.Mask(token) + "(" + token.Length + "자) · expires_in "
                 + rep.TokenExpiresInSeconds + "s · 지연 " + rep.TokenLatencyMs.ToString("F1") + "ms");

            // ② 해외 잔고 조회(VTTS3012R)
            byte[] balBody;
            sw.Restart();
            using (var req = KisOverseasPaperRequests.Balance(c, token))
            using (var resp = await KisOverseasPaperGuard.SendAsync(http, req, ct).ConfigureAwait(false))
            {
                balBody = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
                rep.BalanceHttpStatus = (int)resp.StatusCode;
            }
            rep.BalanceLatencyMs = sw.Elapsed.TotalMilliseconds;
            token = "";
            rep.Balance = VtsOverseasBalanceSummary.Parse(balBody);
            rep.BalanceOk = rep.BalanceHttpStatus == 200 && rep.Balance.Ok;
            var b = rep.Balance;
            Line("[balance] " + (rep.BalanceOk ? "해외 잔고 조회 성공" : "해외 잔고 조회 실패") + " · HTTP " + rep.BalanceHttpStatus + " · rt_cd " + b.RtCd + " · msg_cd " + b.MsgCd
                 + " · " + b.Msg1 + " · 보유 행 " + b.HoldingRows + " · 외화매입 " + VtsOverseasBalanceSummary.Usd(b.PurchaseUsdRaw) + " · 평가손익 "
                 + VtsOverseasBalanceSummary.Usd(b.EvalPnlUsdRaw) + " · 총손익 " + VtsOverseasBalanceSummary.Usd(b.TotalPnlUsdRaw) + " · 지연 " + rep.BalanceLatencyMs.ToString("F1") + "ms");
            return rep;
        }
    }

    public sealed class KisOverseasPaperFactAttribute : FactAttribute
    {
        public KisOverseasPaperFactAttribute() => Skip = KisOverseasPaperGuard.SkipReason(Environment.GetEnvironmentVariable, VtsPreFlightCredentials.DefaultDbPath);
    }
}
