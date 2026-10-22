// ============================================================================
// KisOverseasPaperProbe — [v12.18.53-DEV → v12.18.57-DEV] KIS 해외주식 모의투자(VTS) REST 통신 점검(토큰 · VTTS3012R 3대 거래소 잔고 · HHDFS00000300 현재가 · 주문 0 · 테스트 도구)
//
//  목적: portfolio.db 에 DPAPI 로 봉인된 PaperOverseas 자격증명이 실제 VTS 서버에서 통하는지 확인하고 미국 3대 거래소(NASD · NYSE · AMEX) 잔고와
//   거래소별 대표 종목 현재가 조회 지연을 실측한다 — POST /oauth2/tokenP → GET inquire-balance(VTTS3012R × 3) → GET quotations/price(HHDFS00000300 × 3).
//  ★자격증명: Settings 의 KIS.Profile.PaperOverseas.{AppKey · AppSecret · Account} 만 — VtsPreFlightCredentials.Load(db, "PaperOverseas")
//   (읽기 전용 연결 · 평문 저장/미등록/복호 실패/계좌 형식 이탈 = 거부 · 허용 프로필 = 모의 2종뿐).
//  ★송출 허용 목록(그 외 = 예외 · HttpMessageInvoker.SendAsync 이전이라 패킷 0):
//   ① POST https://openapivts.koreainvestment.com:29443/oauth2/tokenP · tr_id 헤더 없음
//   ② GET  같은 호스트 /uapi/overseas-stock/v1/trading/inquire-balance · tr_id 정확히 VTTS3012R · OVRS_EXCG_CD ∈ {NASD, NYSE, AMEX} 1회 · TR_CRCY_CD = USD 1회
//   ③ GET  같은 호스트 /uapi/overseas-price/v1/quotations/price · tr_id 정확히 HHDFS00000300 · EXCD ∈ {NAS, NYS, AMS} 1회 · SYMB = 심볼 형식 1회
//   경로 ↔ TR 교차 = 거부 · 해외 주문 TR(VTTT1002U 등) · 실 TR(TTTS3012R) · 국내 경로 거부 · 본문 동봉 거부. 자동 리다이렉트 꺼짐.
//   제품 KisLiveBroker · 디스패처 · 무장 키 참조 0 — liveArmed:false 불변. 판독 = 제품 KisOverseasProtocol(무할당 파서) · 세션 = UsSessionStateMachine.
//  ★마스킹: 출력에는 rt_cd · msg_cd · msg1 · 수명 · USD 금액 · 가격 · 세션 · 지연만. AppKey · Secret · 토큰 · 계좌번호는 앞 2자리 외 '*'(PreFlightRedactor 전수 치환).
//  ★실행 조건(옵트인): MS2E_KIS_VTS_OVERSEAS=1 + DB 존재. 미충족 = Skip(실패 아님). 장 시간 무관(조회 전용 · 시세 유효성은 세션 페이즈로 분류만).
//  ★요청 간격: 모의 REST 한도 대비 요청 사이 Pace(기본 1,100ms) 대기 · 재시도 0(실패 = 그대로 보고).
//   (2026-10-22 실측: 600ms 간격에서 6번째 요청 = EGW00201 초당 거래건수 초과 · HTTP 500 → 초당 1건 이하로 상향)
//  요청 파라미터: OVRS_EXCG_CD = 거래소별 개별 조회(모의 = 실전의 'NASD = 미국 전체' 와 다름) · TR_CRCY_CD = USD · 시세 EXCD = 3자 시세 코드(UsExchangeCode).
// ============================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MS2E.Infrastructure;
using StockManager.Engines.Brokers.Kis;
using StockManager.Engines.Feeds;
using StockManager.Engines.Pricing;
using Xunit;

namespace StockManager.Tests.Integration
{
    /// <summary>해외 모의 송출 허용 목록 가드 — 토큰 발급(POST) · 해외 잔고(GET VTTS3012R) · 해외 현재가(GET HHDFS00000300) 세 종류만.</summary>
    public static class KisOverseasPaperGuard
    {
        public const string BalancePath = "/uapi/overseas-stock/v1/trading/inquire-balance";
        public const string QuotePath = "/uapi/overseas-price/v1/quotations/price";
        public const string TrBalanceVts = "VTTS3012R";
        public const string TrQuote = "HHDFS00000300";
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
            string? tr = path == BalancePath ? TrBalanceVts : path == QuotePath ? TrQuote : null;
            if (tr is not null && req.Method == HttpMethod.Get && hasTr && req.Content is null)
            {
                int n = 0;
                foreach (var t in trs!) { if (t != tr) throw new InvalidOperationException("해외 모의 점검 거부: 조회 TR ≠ " + tr); n++; }
                if (n == 1)
                {
                    var q = QueryValues(req.RequestUri.Query);
                    bool ok = tr == TrBalanceVts
                        ? Single(q, "OVRS_EXCG_CD", out var ex) && UsExchangeCode.FromTradeCode(Encoding.ASCII.GetBytes(ex)) != UsExchange.None
                          && Single(q, "TR_CRCY_CD", out var cur) && cur == KisOverseasPaperRequests.Currency
                        : Single(q, "EXCD", out var qx) && UsExchangeCode.FromQuoteCode(Encoding.ASCII.GetBytes(qx)) != UsExchange.None
                          && Single(q, "SYMB", out var sym) && UsExchangeCode.IsValidSymbol(sym);
                    if (ok) return;
                    throw new InvalidOperationException("해외 모의 점검 거부: 거래소/통화/심볼 파라미터 이탈(" + path + ")");
                }
            }
            throw new InvalidOperationException("해외 모의 점검 거부: 허용 목록 밖 요청(" + req.Method + " " + path + ")");
        }

        private static List<KeyValuePair<string, string>> QueryValues(string query)
        {
            var list = new List<KeyValuePair<string, string>>();
            foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = part.IndexOf('=');
                list.Add(eq < 0 ? new(part, "") : new(part[..eq], part[(eq + 1)..]));
            }
            return list;
        }

        private static bool Single(List<KeyValuePair<string, string>> q, string key, out string value)
        {
            value = "";
            int n = 0;
            foreach (var kv in q) if (kv.Key == key) { value = kv.Value; n++; }
            return n == 1;
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

    /// <summary>해외 잔고 · 현재가 요청 생성기(순수 · 오프라인 검증 가능).</summary>
    public static class KisOverseasPaperRequests
    {
        public const string Currency = "USD";

        /// <summary>나스닥 잔고(v12.18.53 호환 기본값).</summary>
        public static HttpRequestMessage Balance(VtsPreFlightCredentials c, string token) => Balance(c, token, UsExchange.Nasdaq);

        public static HttpRequestMessage Balance(VtsPreFlightCredentials c, string token, UsExchange exchange)
        {
            if (!UsExchangeCode.IsValid(exchange)) throw new ArgumentOutOfRangeException(nameof(exchange), "미국 3대 거래소 외 잔고 조회 거부");
            var q = "CANO=" + c.Cano + "&ACNT_PRDT_CD=" + c.Prdt + "&OVRS_EXCG_CD=" + UsExchangeCode.TradeCode(exchange) + "&TR_CRCY_CD=" + Currency + "&CTX_AREA_FK200=&CTX_AREA_NK200=";
            return Signed(new HttpRequestMessage(HttpMethod.Get, new Uri(VtsDomainGuard.VtsRestBase, KisOverseasPaperGuard.BalancePath + "?" + q)), c, token, KisOverseasPaperGuard.TrBalanceVts);
        }

        public static HttpRequestMessage Quote(VtsPreFlightCredentials c, string token, UsExchange exchange, string symbol)
        {
            if (!UsExchangeCode.IsValid(exchange)) throw new ArgumentOutOfRangeException(nameof(exchange), "미국 3대 거래소 외 시세 조회 거부");
            if (!UsExchangeCode.IsValidSymbol(symbol)) throw new ArgumentException("심볼 형식 이탈", nameof(symbol));
            var q = "AUTH=&EXCD=" + UsExchangeCode.QuoteCode(exchange) + "&SYMB=" + symbol;
            return Signed(new HttpRequestMessage(HttpMethod.Get, new Uri(VtsDomainGuard.VtsRestBase, KisOverseasPaperGuard.QuotePath + "?" + q)), c, token, KisOverseasPaperGuard.TrQuote);
        }

        private static HttpRequestMessage Signed(HttpRequestMessage req, VtsPreFlightCredentials c, string token, string tr)
        {
            req.Headers.TryAddWithoutValidation("authorization", "Bearer " + token);
            req.Headers.TryAddWithoutValidation("appkey", c.AppKey);
            req.Headers.TryAddWithoutValidation("appsecret", c.Secret);
            req.Headers.TryAddWithoutValidation("tr_id", tr);
            req.Headers.TryAddWithoutValidation("custtype", "P");
            return req;
        }
    }

    /// <summary>해외 잔고 응답 요약(민감 필드 0 — USD 금액 raw 10^8 · 행 수 · 결과 코드만 · 금액 부재/형식 이탈 = Missing). 판독 = KisOverseasProtocol(무할당).</summary>
    public readonly record struct VtsOverseasBalanceSummary(bool Parsed, string RtCd, string MsgCd, string Msg1, int HoldingRows, long PurchaseUsdRaw, long EvalPnlUsdRaw, long TotalPnlUsdRaw,
                                                            int MismatchedExchangeRows = 0)
    {
        public const long Missing = long.MinValue;
        public bool Ok => Parsed && RtCd == "0";

        public static string Usd(long raw) => raw == Missing ? "n/a" : "$" + ((decimal)raw / (decimal)UsTickAligner.Scale).ToString("N2", CultureInfo.InvariantCulture);

        public static VtsOverseasBalanceSummary Parse(ReadOnlySpan<byte> body) => Parse(body, UsExchange.None);

        public static VtsOverseasBalanceSummary Parse(ReadOnlySpan<byte> body, UsExchange expected)
        {
            if (!KisOverseasProtocol.TryParseBalance(body, expected, out var b)) return default;
            return new VtsOverseasBalanceSummary(true, b.RtCd.Decode(body), b.MsgCd.Decode(body), b.Msg1.Decode(body).Trim(), b.HoldingRows,
                b.PurchaseUsdRaw, b.EvalPnlUsdRaw, b.TotalPnlUsdRaw, b.MismatchedExchangeRows);
        }
    }

    /// <summary>해외 현재가 응답 요약(가격 raw 10^8 · 세션 페이즈 · 유효성 판정). 판독 = KisOverseasProtocol(무할당).</summary>
    public readonly record struct VtsOverseasQuoteSummary(bool Parsed, string RtCd, string MsgCd, string Msg1, bool SymbolVerified, long LastRaw, long BaseRaw, long Volume,
                                                          UsSessionPhase Phase, UsQuoteValidity Validity)
    {
        public bool Ok => Parsed && RtCd == "0";

        public static VtsOverseasQuoteSummary Parse(ReadOnlySpan<byte> body, UsExchange exchange, string symbol, UsSessionPhase phase)
        {
            if (!KisOverseasProtocol.TryParseQuote(body, exchange, Encoding.ASCII.GetBytes(symbol), out var q)) return new VtsOverseasQuoteSummary { Phase = phase };
            return new VtsOverseasQuoteSummary(true, q.RtCd.Decode(body), q.MsgCd.Decode(body), q.Msg1.Decode(body).Trim(), q.SymbolVerified, q.LastRaw, q.BaseRaw, q.Volume,
                phase, UsQuoteSessionGate.Classify(phase, q));
        }
    }

    public sealed class KisOverseasExchangeBalance
    {
        public UsExchange Exchange;
        public int HttpStatus;
        public double LatencyMs;
        public VtsOverseasBalanceSummary Summary;
        public bool Ok => HttpStatus == 200 && Summary.Ok;
    }

    public sealed class KisOverseasExchangeQuote
    {
        public UsExchange Exchange;
        public string Symbol = "";
        public int HttpStatus;
        public double LatencyMs;
        public VtsOverseasQuoteSummary Summary;
        public bool Ok => HttpStatus == 200 && Summary.Ok;
    }

    /// <summary>점검 계획 — 잔고 거래소 · 시세 (거래소, 심볼) · 요청 간격 · 세션 판정 시계(유닉스 UTC 마이크로초).</summary>
    public sealed class KisOverseasProbeOptions
    {
        public UsExchange[] BalanceExchanges = { UsExchange.Nasdaq, UsExchange.Nyse, UsExchange.Amex };
        public (UsExchange Exchange, string Symbol)[] Quotes = { (UsExchange.Nasdaq, "AAPL"), (UsExchange.Nyse, "IBM"), (UsExchange.Amex, "SPY") };
        public TimeSpan Pace = TimeSpan.FromMilliseconds(1100);
        public Func<long> UtcMicrosNow = static () => (DateTime.UtcNow.Ticks - DateTime.UnixEpoch.Ticks) / 10;

        public static KisOverseasProbeOptions Default => new();
    }

    /// <summary>해외 점검 결과(마스킹된 출력 줄만 보관).</summary>
    public sealed class KisOverseasPaperReport
    {
        public bool TokenIssued;
        public long TokenExpiresInSeconds;
        public int TokenHttpStatus;
        public double TokenLatencyMs;
        public readonly List<KisOverseasExchangeBalance> Balances = new();
        public readonly List<KisOverseasExchangeQuote> Quotes = new();
        public readonly List<string> Lines = new();

        public bool BalanceOk => Balances.Count > 0 && Balances.TrueForAll(static b => b.Ok);
        public bool QuotesOk => Quotes.Count > 0 && Quotes.TrueForAll(static q => q.Ok);
    }

    public static class KisOverseasPaperProbe
    {
        public static Task<KisOverseasPaperReport> RunAsync(HttpMessageInvoker http, VtsPreFlightCredentials c, CancellationToken ct)
            => RunAsync(http, c, KisOverseasProbeOptions.Default, ct);

        public static async Task<KisOverseasPaperReport> RunAsync(HttpMessageInvoker http, VtsPreFlightCredentials c, KisOverseasProbeOptions o, CancellationToken ct)
        {
            if (c.ProfileName != VtsPreFlightCredentials.OverseasProfile)
                throw new InvalidOperationException("해외 모의 점검 거부: 프로필 ≠ " + VtsPreFlightCredentials.OverseasProfile);
            var rep = new KisOverseasPaperReport();
            var red = new PreFlightRedactor();
            red.Add(c.AppKey); red.Add(c.Secret); red.Add(c.Cano); red.Add(c.Cano + c.Prdt); red.Add(c.Cano + "-" + c.Prdt);
            void Line(string s) => rep.Lines.Add(red.Redact(s));
            var plan = new StringBuilder();
            foreach (var ex in o.BalanceExchanges) plan.Append(plan.Length == 0 ? "" : "/").Append(UsExchangeCode.TradeCode(ex));
            Line("[overseas] 대상 " + VtsDomainGuard.VtsRestBase + " · 프로필 " + c + " · 잔고 거래소 " + plan + " · " + KisOverseasPaperRequests.Currency + " · 시세 " + o.Quotes.Length + "종 · 간격 "
                 + o.Pace.TotalMilliseconds.ToString("F0", CultureInfo.InvariantCulture) + "ms");

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

            // ② 거래소별 해외 잔고 조회(VTTS3012R) — 한 거래소 실패여도 나머지 진행(재시도 0)
            foreach (var ex in o.BalanceExchanges)
            {
                if (o.Pace > TimeSpan.Zero) await Task.Delay(o.Pace, ct).ConfigureAwait(false);
                var r = new KisOverseasExchangeBalance { Exchange = ex };
                byte[] body;
                sw.Restart();
                using (var req = KisOverseasPaperRequests.Balance(c, token, ex))
                using (var resp = await KisOverseasPaperGuard.SendAsync(http, req, ct).ConfigureAwait(false))
                {
                    body = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
                    r.HttpStatus = (int)resp.StatusCode;
                }
                r.LatencyMs = sw.Elapsed.TotalMilliseconds;
                r.Summary = VtsOverseasBalanceSummary.Parse(body, ex);
                rep.Balances.Add(r);
                var b = r.Summary;
                Line("[balance " + UsExchangeCode.TradeCode(ex) + "] " + (r.Ok ? "해외 잔고 조회 성공" : "해외 잔고 조회 실패") + " · HTTP " + r.HttpStatus + " · rt_cd " + b.RtCd + " · msg_cd " + b.MsgCd
                     + " · " + b.Msg1 + " · 보유 행 " + b.HoldingRows + (b.MismatchedExchangeRows > 0 ? " · ★거래소 불일치 행 " + b.MismatchedExchangeRows : "")
                     + " · 외화매입 " + VtsOverseasBalanceSummary.Usd(b.PurchaseUsdRaw) + " · 평가손익 " + VtsOverseasBalanceSummary.Usd(b.EvalPnlUsdRaw)
                     + " · 총손익 " + VtsOverseasBalanceSummary.Usd(b.TotalPnlUsdRaw) + " · 지연 " + r.LatencyMs.ToString("F1") + "ms");
            }

            // ③ 거래소별 현재가(HHDFS00000300) + 미국 세션 페이즈 유효성 판정
            foreach (var (ex, sym) in o.Quotes)
            {
                if (o.Pace > TimeSpan.Zero) await Task.Delay(o.Pace, ct).ConfigureAwait(false);
                var r = new KisOverseasExchangeQuote { Exchange = ex, Symbol = sym };
                byte[] body;
                sw.Restart();
                using (var req = KisOverseasPaperRequests.Quote(c, token, ex, sym))
                using (var resp = await KisOverseasPaperGuard.SendAsync(http, req, ct).ConfigureAwait(false))
                {
                    body = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
                    r.HttpStatus = (int)resp.StatusCode;
                }
                r.LatencyMs = sw.Elapsed.TotalMilliseconds;
                var phase = UsSessionStateMachine.GetSessionPhaseUtcMicros(o.UtcMicrosNow());
                r.Summary = VtsOverseasQuoteSummary.Parse(body, ex, sym, phase);
                rep.Quotes.Add(r);
                var q = r.Summary;
                Line("[quote " + UsExchangeCode.QuoteCode(ex) + " " + sym + "] " + (r.Ok ? "현재가 조회 성공" : "현재가 조회 실패") + " · HTTP " + r.HttpStatus + " · rt_cd " + q.RtCd + " · msg_cd " + q.MsgCd
                     + " · " + q.Msg1 + " · 심볼 검증 " + (q.SymbolVerified ? "일치" : "불가") + " · 현재가 " + VtsOverseasBalanceSummary.Usd(q.LastRaw)
                     + " · 전일 종가 " + VtsOverseasBalanceSummary.Usd(q.BaseRaw) + " · 거래량 " + (q.Volume == KisOverseasQuote.Missing ? "n/a" : q.Volume.ToString("N0", CultureInfo.InvariantCulture))
                     + " · 세션 " + q.Phase + " · 유효성 " + q.Validity + " · 주문 근거 " + (UsQuoteSessionGate.IsOrderBasis(q.Validity) ? "가능(송출 경로 없음)" : "불가")
                     + " · 지연 " + r.LatencyMs.ToString("F1") + "ms");
            }
            token = "";
            return rep;
        }
    }

    public sealed class KisOverseasPaperFactAttribute : FactAttribute
    {
        public KisOverseasPaperFactAttribute() => Skip = KisOverseasPaperGuard.SkipReason(Environment.GetEnvironmentVariable, VtsPreFlightCredentials.DefaultDbPath);
    }
}
