// ============================================================================
// KisOverseasDailyBarSourceTests — v12.18.81-DEV: 플루토 스웜 US 레인 실피드 = KIS 해외 일봉(HHDFS76240000) 결선
//   ① 상장 거래소 표: US 카탈로그 100종 전원 등재(None 0) · 표 = 카탈로그(초과 0) · 이전 상장 반영(LIN/PLTR/WMT = 나스닥) · 미등재 = None.
//   ② KisProvider 해외 일봉: GET 1회 · 경로/tr_id/EXCD(NAS · NYS · AMS)/GUBN=0/MODP=1/BYMD · 봉 판독 · 범위 밖 제외 · rt_cd ≠ 0 / output2 부재 / HTTP 오류 = 빈 목록 · 미지원 시장 = 송신 0.
//   ③ 소스(US): 센트 변환(반올림) · 오래된 것부터 · 미등재 티커 = NoData(송신 0) · 게이트 거부 = RateLimited · 시장 불일치 = Faulted · 라벨.
//   ④ 스웜: US 레인 Live(가격 센트) · KRX/US 동시 부착 · 호스트 결선(페이서 공유) 문자열.
//   ⑤ [보정] 송신 간격 1,150ms(해외 VTS EGW00201 마진): 기본/호스트 페이서 = MinRequestInterval · 연속 3회 실측 ≥ 2.3초 · ms 생성자 범위 · 라벨.
//   ⑥ [보정] 완료 기준 간격: 토큰 POST 수신 → GET1 · GET1 수신 → GET2 각 ≥ 1,150ms · 공유 페이서 KRX/US 동시 워커 중첩 0 · 토큰 실패 = Faulted(슬롯 반납) · 취소 = 송신 0.
//   (스텁 핸들러 · 실 네트워크 0 · 주문 0 · POST = 토큰 발급 스텁만)
// ============================================================================
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MS2E.Infrastructure;
using StockManager.Adapters;
using StockManager.Engines.Arena.Us;
using StockManager.Engines.Brokers.Kis;
using StockManager.Engines.Cognitive.Screening;
using Xunit;

namespace StockManager.Tests.Cognitive
{
    [Collection("ConcurrencyStress")]
    public sealed class KisOverseasDailyBarSourceTests
    {
        private sealed class StubHandler : HttpMessageHandler
        {
            public readonly List<HttpRequestMessage> Requests = new();
            public HttpStatusCode Status = HttpStatusCode.OK;
            public Func<string>? Body;
            public int Tokens;

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
            {
                if (req.RequestUri!.AbsolutePath.EndsWith("/oauth2/tokenP", StringComparison.Ordinal))
                {
                    Interlocked.Increment(ref Tokens);
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"access_token\":\"t\"}", Encoding.UTF8, "application/json") });
                }
                lock (Requests) Requests.Add(req);
                return Task.FromResult(new HttpResponseMessage(Status) { Content = new StringContent(Body?.Invoke() ?? "{}", Encoding.UTF8, "application/json") });
            }
        }

        private static KisProvider MakeKis(StubHandler h, LockFreeTokenBucket? gate = null)
            => new(new KisConfig { AppKey = "k", AppSecret = "s", IsPaper = true }, new HttpClient(h), gate);

        private static string Row(DateTime d, string o, string hi, string lo, string c, string v)
            => "{\"xymd\":\"" + d.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + "\",\"clos\":\"" + c + "\",\"sign\":\"2\",\"diff\":\"1.0\",\"rate\":\"0.5\",\"open\":\"" + o +
               "\",\"high\":\"" + hi + "\",\"low\":\"" + lo + "\",\"tvol\":\"" + v + "\",\"tamt\":\"0\",\"pbid\":\"0\",\"vbid\":\"0\",\"pask\":\"0\",\"vask\":\"0\"}";

        private static string Daily(params string[] rows)
            => "{\"output1\":{\"rsym\":\"DNASAAPL\",\"zdiv\":\"4\",\"nrec\":\"" + rows.Length + "\"},\"output2\":[" + string.Join(",", rows) +
               "],\"rt_cd\":\"0\",\"msg_cd\":\"MCA00000\",\"msg1\":\"정상처리 되었습니다.\"}";

        private static Dictionary<string, string> Query(HttpRequestMessage r)
        {
            var d = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var kv in r.RequestUri!.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                int i = kv.IndexOf('=');
                d[kv[..i]] = Uri.UnescapeDataString(kv[(i + 1)..]);
            }
            return d;
        }

        private static string Header(HttpRequestMessage r, string name) => r.Headers.TryGetValues(name, out var v) ? string.Join(",", v) : "";

        // ── ① 상장 거래소 표 ─────────────────────────────────────────────────

        [Fact]
        public void ListingTable_Covers_Every_Us_Catalog_Entry_Exactly()
        {
            var us = PlutoMultiAssetUniverseCatalog.Of(PlutoScreenMarket.Us);
            Assert.Equal(100, us.Count);
            foreach (var e in us) Assert.True(UsListingExchange.Of(e.Code) != UsExchange.None, e.Code + " 상장 거래소 미등재");
            Assert.Equal(us.Count, UsListingExchange.Count);                     // 표 = 카탈로그(초과/중복 0 · 중복은 Dictionary.Add 가 정적 생성에서 거부)
        }

        [Theory]
        [InlineData("AAPL", UsExchange.Nasdaq)]
        [InlineData("LIN", UsExchange.Nasdaq)]
        [InlineData("PLTR", UsExchange.Nasdaq)]
        [InlineData("WMT", UsExchange.Nasdaq)]
        [InlineData("ASML", UsExchange.Nasdaq)]
        [InlineData("JPM", UsExchange.Nyse)]
        [InlineData("BRK.B", UsExchange.Nyse)]
        [InlineData("UBER", UsExchange.Nyse)]
        [InlineData("aapl", UsExchange.None)]
        [InlineData("SPY", UsExchange.None)]
        [InlineData("", UsExchange.None)]
        public void ListingTable_Maps_Ticker(string ticker, UsExchange expected) => Assert.Equal(expected, UsListingExchange.Of(ticker));

        // ── ② KisProvider 해외 일봉 ──────────────────────────────────────────

        [Theory]
        [InlineData(Market.US_NASDAQ, "NAS")]
        [InlineData(Market.US_NYSE, "NYS")]
        [InlineData(Market.US_AMEX, "AMS")]
        public async Task Provider_Overseas_Daily_Sends_One_Get_With_Quote_Series_Code(Market market, string excd)
        {
            var to = new DateTime(2026, 10, 23);
            var h = new StubHandler { Body = () => Daily(Row(to, "261.19", "264.13", "260.88", "262.82", "38253717"), Row(to.AddDays(-1), "259.00", "262.00", "258.50", "261.19", "41000000")) };
            var list = await MakeKis(h).GetHistoryAsync("AAPL", market, to.AddDays(-150), to);
            var r = Assert.Single(h.Requests);
            Assert.Equal(HttpMethod.Get, r.Method);
            Assert.Equal("/uapi/overseas-price/v1/quotations/dailyprice", r.RequestUri!.AbsolutePath);
            Assert.Equal("openapivts.koreainvestment.com", r.RequestUri.Host);
            Assert.Equal("HHDFS76240000", Header(r, "tr_id"));
            Assert.Equal("Bearer t", Header(r, "Authorization"));
            var q = Query(r);
            Assert.Equal((excd, "AAPL", "0", "20261023", "1"), (q["EXCD"], q["SYMB"], q["GUBN"], q["BYMD"], q["MODP"]));
            Assert.Equal(2, list.Count);
            Assert.Equal((to, 261.19m, 264.13m, 260.88m, 262.82m, 38253717L), (list[0].Date, list[0].Open, list[0].High, list[0].Low, list[0].Close, list[0].Volume));
        }

        [Fact]
        public async Task Provider_Overseas_Daily_Excludes_Out_Of_Range_And_Blank_Dates()
        {
            var to = new DateTime(2026, 10, 23);
            string blank = "{\"xymd\":\"\",\"clos\":\"1\",\"open\":\"1\",\"high\":\"1\",\"low\":\"1\",\"tvol\":\"1\"}";
            var h = new StubHandler { Body = () => Daily(Row(to.AddDays(1), "1", "1", "1", "1", "1"), Row(to, "2", "2", "2", "2", "2"), blank, Row(to.AddDays(-200), "3", "3", "3", "3", "3")) };
            var list = await MakeKis(h).GetHistoryAsync("AAPL", Market.US_NASDAQ, to.AddDays(-150), to);
            Assert.Equal(to, Assert.Single(list).Date);
        }

        [Theory]
        [InlineData("{\"rt_cd\":\"1\",\"msg_cd\":\"EGW00123\",\"msg1\":\"x\",\"output2\":[{\"xymd\":\"20261023\",\"clos\":\"1\",\"open\":\"1\",\"high\":\"1\",\"low\":\"1\",\"tvol\":\"1\"}]}")]
        [InlineData("{\"rt_cd\":\"0\",\"msg_cd\":\"MCA00000\",\"msg1\":\"x\"}")]
        [InlineData("{\"rt_cd\":\"0\",\"output2\":{}}")]
        [InlineData("not json")]
        public async Task Provider_Overseas_Daily_Fails_Closed_To_Empty(string body)
        {
            var to = new DateTime(2026, 10, 23);
            var h = new StubHandler { Body = () => body };
            Assert.Empty(await MakeKis(h).GetHistoryAsync("AAPL", Market.US_NASDAQ, to.AddDays(-150), to));
        }

        [Fact]
        public async Task Provider_Overseas_Daily_Http_Error_Empty_And_Unsupported_Market_Sends_Nothing()
        {
            var to = new DateTime(2026, 10, 23);
            var h = new StubHandler { Status = HttpStatusCode.InternalServerError, Body = () => Daily(Row(to, "1", "1", "1", "1", "1")) };
            var kis = MakeKis(h);
            Assert.Empty(await kis.GetHistoryAsync("AAPL", Market.US_NASDAQ, to.AddDays(-150), to));
            Assert.Single(h.Requests);
            Assert.Empty(await kis.GetHistoryAsync("BTC", Market.CRYPTO, to.AddDays(-150), to));
            Assert.Single(h.Requests);                                            // 미지원 시장 = 송신 0
        }

        // ── ③ 소스(US) ───────────────────────────────────────────────────────

        private static PlutoUniverseEntry UsEntry(string code)
        {
            foreach (var e in PlutoMultiAssetUniverseCatalog.Of(PlutoScreenMarket.Us)) if (e.Code == code) return e;
            throw new InvalidOperationException(code);
        }

        [Fact]
        public void Source_Us_Converts_To_Cents_Oldest_First()
        {
            var today = DateTime.Now.Date;
            var h = new StubHandler { Body = () => Daily(Row(today, "100.005", "101.234", "99.995", "100.50", "1200"), Row(today.AddDays(-1), "98", "99.5", "97.25", "98.75", "900")) };
            var src = new KisDailyBarSource(MakeKis(h), new PlutoRequestPacer(1000), PlutoScreenMarket.Us);
            Assert.Equal(PlutoScreenMarket.Us, src.Market);
            var s = new PlutoAssetSample();
            Assert.Equal(PlutoFetchOutcome.Ok, src.Fetch(UsEntry("AAPL"), s, CancellationToken.None));
            Assert.Equal(2, s.BarCount);
            Assert.Equal((9800L, 9950L, 9725L, 9875L, 900L), (s.Bars[0].Open, s.Bars[0].High, s.Bars[0].Low, s.Bars[0].Close, s.Bars[0].Volume));
            Assert.Equal((10001L, 10123L, 10000L, 10050L), (s.Bars[1].Open, s.Bars[1].High, s.Bars[1].Low, s.Bars[1].Close));   // 반올림(AwayFromZero)
            Assert.True(s.Bars[0].TimeUtcTicks < s.Bars[1].TimeUtcTicks);
            Assert.Equal("NAS", Query(Assert.Single(h.Requests))["EXCD"]);
        }

        [Fact]
        public void Source_Us_Nyse_Ticker_Uses_Nys()
        {
            var h = new StubHandler { Body = () => Daily(Row(DateTime.Now.Date, "1", "2", "1", "2", "1")) };
            var src = new KisDailyBarSource(MakeKis(h), new PlutoRequestPacer(1000), PlutoScreenMarket.Us);
            Assert.Equal(PlutoFetchOutcome.Ok, src.Fetch(UsEntry("JPM"), new PlutoAssetSample(), CancellationToken.None));
            Assert.Equal(("NYS", "JPM"), (Query(h.Requests[0])["EXCD"], Query(h.Requests[0])["SYMB"]));
        }

        [Fact]
        public void Source_Us_Unlisted_Ticker_NoData_Without_Request_And_Market_Mismatch_Faulted()
        {
            var h = new StubHandler { Body = () => Daily(Row(DateTime.Now.Date, "1", "2", "1", "2", "1")) };
            var pacer = new PlutoRequestPacer(1000);
            var src = new KisDailyBarSource(MakeKis(h), pacer, PlutoScreenMarket.Us);
            var unlisted = new PlutoUniverseEntry(PlutoScreenMarket.Us, PlutoUniverseSegment.Sp100, UsTechUniverse.Pack("SPY"), "SPY", "SPDR");
            Assert.Equal(PlutoFetchOutcome.NoData, src.Fetch(unlisted, new PlutoAssetSample(), CancellationToken.None));
            var krx = PlutoMultiAssetUniverseCatalog.Of(PlutoScreenMarket.Krx)[0];
            Assert.Equal(PlutoFetchOutcome.Faulted, src.Fetch(krx, new PlutoAssetSample(), CancellationToken.None));
            Assert.Empty(h.Requests);
            Assert.Equal(0, pacer.Granted);                                       // 페이서 예산 소비 0
        }

        [Fact]
        public void Source_Us_Gate_Denied_Is_RateLimited_And_Empty_Is_NoData()
        {
            var h = new StubHandler { Body = () => Daily() };
            var gate = new LockFreeTokenBucket(1, LockFreeTokenBucket.SecondMicros, nowTicks: static () => 0, tickFrequency: 1_000_000);
            var src = new KisDailyBarSource(MakeKis(h, gate), new PlutoRequestPacer(1000), PlutoScreenMarket.Us);
            Assert.Equal(PlutoFetchOutcome.NoData, src.Fetch(UsEntry("AAPL"), new PlutoAssetSample(), CancellationToken.None));
            Assert.Equal(PlutoFetchOutcome.RateLimited, src.Fetch(UsEntry("MSFT"), new PlutoAssetSample(), CancellationToken.None));
            Assert.Single(h.Requests);                                            // 거부 = 송신 0 · 재시도 0
        }

        [Fact]
        public void Source_Labels_And_Rejects_Crypto_Market()
        {
            var kis = MakeKis(new StubHandler());
            Assert.StartsWith("KIS 해외 일봉 REST", new KisDailyBarSource(kis, null, PlutoScreenMarket.Us).Label);
            Assert.StartsWith("KIS 일봉 REST", new KisDailyBarSource(kis).Label);
            Assert.Equal(PlutoScreenMarket.Krx, new KisDailyBarSource(kis).Market);
            Assert.Throws<ArgumentOutOfRangeException>(() => new KisDailyBarSource(kis, null, PlutoScreenMarket.Crypto));
            Assert.EndsWith("(인증 구성 · 간격 1,150ms 이상)", new KisDailyBarSource(kis, null, PlutoScreenMarket.Us).Label);
        }

        // ── ⑤ 송신 간격 보정(1,150ms) ─────────────────────────────────────────

        [Fact]
        public void Pacer_Interval_Is_1150ms_And_Ms_Constructor_Bounds()
        {
            Assert.Equal(TimeSpan.FromMilliseconds(1150), KisDailyBarSource.MinRequestInterval);
            var p = new PlutoRequestPacer(KisDailyBarSource.MinRequestInterval);
            Assert.Equal((1150L, 0), (p.MinIntervalMs, p.PerSecond));
            Assert.Equal((125L, 8), (new PlutoRequestPacer(8).MinIntervalMs, new PlutoRequestPacer(8).PerSecond));   // 기존 초당 생성자 무변경
            Assert.Equal((334L, 2), (new PlutoRequestPacer(TimeSpan.FromMilliseconds(333.2)).MinIntervalMs, new PlutoRequestPacer(TimeSpan.FromMilliseconds(333.2)).PerSecond));   // 올림
            Assert.Throws<ArgumentOutOfRangeException>(() => new PlutoRequestPacer(TimeSpan.Zero));
            Assert.Throws<ArgumentOutOfRangeException>(() => new PlutoRequestPacer(TimeSpan.FromMinutes(2)));
        }

        [Fact]
        public void Pacer_1150ms_Three_Consecutive_Sends_Span_At_Least_2300ms()
        {
            var p = new PlutoRequestPacer(KisDailyBarSource.MinRequestInterval);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            Assert.True(p.Wait(CancellationToken.None));                         // 1회 = 즉시
            long first = sw.ElapsedMilliseconds;
            Assert.True(p.Wait(CancellationToken.None));
            Assert.True(p.Wait(CancellationToken.None));
            Assert.True(first < 500, "첫 송신 지연 " + first + "ms");
            Assert.True(sw.ElapsedMilliseconds >= 2 * 1150 - 1, "3회 송신 " + sw.ElapsedMilliseconds + "ms < 2,300ms");   // 간격 미달 0
            Assert.Equal(3, p.Granted);
        }

        [Fact]
        public void Source_Default_Pacer_Spaces_Two_Us_Fetches_By_1150ms()
        {
            var h = new StubHandler { Body = () => Daily(Row(DateTime.Now.Date, "1", "2", "1", "2", "1")) };
            var src = new KisDailyBarSource(MakeKis(h), null, PlutoScreenMarket.Us);   // 기본 페이서 = MinRequestInterval
            var sw = System.Diagnostics.Stopwatch.StartNew();
            Assert.Equal(PlutoFetchOutcome.Ok, src.Fetch(UsEntry("AAPL"), new PlutoAssetSample(), CancellationToken.None));
            Assert.Equal(PlutoFetchOutcome.Ok, src.Fetch(UsEntry("JPM"), new PlutoAssetSample(), CancellationToken.None));
            Assert.True(sw.ElapsedMilliseconds >= 1149, "2회 조회 " + sw.ElapsedMilliseconds + "ms < 1,150ms");
            Assert.Equal(2, h.Requests.Count);
        }

        // ── ⑥ 완료 기준 간격(응답 수신 → 다음 송신 ≥ 1,150ms · 토큰 POST 포함) ─────────────

        /// <summary>응답 지연 스텁 — 송신 시작/응답 완료 Stopwatch 시각을 POST/GET 구분 없이 순서대로 기록.</summary>
        private sealed class TimedHandler : HttpMessageHandler
        {
            public readonly List<(string Kind, long Start, long End)> Sends = new();
            public int DelayMs = 300;
            public bool FailToken;
            public string DailyBody = "{}";

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
            {
                long start = System.Diagnostics.Stopwatch.GetTimestamp();
                await Task.Delay(DelayMs, ct);
                bool token = req.RequestUri!.AbsolutePath.EndsWith("/oauth2/tokenP", StringComparison.Ordinal);
                var res = token
                    ? new HttpResponseMessage(FailToken ? HttpStatusCode.Forbidden : HttpStatusCode.OK) { Content = new StringContent(FailToken ? "{\"error_code\":\"EGW00133\"}" : "{\"access_token\":\"t\"}", Encoding.UTF8, "application/json") }
                    : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(DailyBody, Encoding.UTF8, "application/json") };
                lock (Sends) Sends.Add((token ? "POST" : "GET", start, System.Diagnostics.Stopwatch.GetTimestamp()));
                return res;
            }
        }

        private static double Ms(long ticks) => ticks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

        private static void AssertCompletionSpacing(TimedHandler h, int expectedSends)
        {
            List<(string Kind, long Start, long End)> s;
            lock (h.Sends) s = new(h.Sends);
            Assert.Equal(expectedSends, s.Count);
            s.Sort(static (a, b) => a.Start.CompareTo(b.Start));
            for (int i = 1; i < s.Count; i++)
            {
                Assert.True(s[i].Start >= s[i - 1].End, "송신 중첩 " + s[i - 1].Kind + "→" + s[i].Kind);
                double gap = Ms(s[i].Start - s[i - 1].End);
                Assert.True(gap >= 1150, s[i - 1].Kind + " 수신 → " + s[i].Kind + " 송신 " + gap.ToString("F1", CultureInfo.InvariantCulture) + "ms < 1,150ms");
            }
        }

        [Fact]
        public void Source_Token_Post_Then_Two_Gets_Each_Spaced_1150ms_From_Previous_Response()
        {
            var h = new TimedHandler { DailyBody = Daily(Row(DateTime.Now.Date, "1", "2", "1", "2", "1")) };
            var kis = new KisProvider(new KisConfig { AppKey = "k", AppSecret = "s", IsPaper = true }, new HttpClient(h));
            var src = new KisDailyBarSource(kis, null, PlutoScreenMarket.Us);
            Assert.False(kis.HasFreshToken);
            Assert.Equal(PlutoFetchOutcome.Ok, src.Fetch(UsEntry("AAPL"), new PlutoAssetSample(), CancellationToken.None));
            Assert.Equal(PlutoFetchOutcome.Ok, src.Fetch(UsEntry("JPM"), new PlutoAssetSample(), CancellationToken.None));
            Assert.True(kis.HasFreshToken);
            Assert.Equal(new[] { "POST", "GET", "GET" }, h.Sends.ConvertAll(static x => x.Kind).ToArray());
            AssertCompletionSpacing(h, 3);                                          // 토큰 수신 → GET1 ≥ 1,150 · GET1 수신 → GET2 ≥ 1,150
        }

        [Fact]
        public async Task Shared_Pacer_Krx_And_Us_Workers_Never_Send_Within_1150ms_Of_A_Response()
        {
            var h = new TimedHandler { DelayMs = 200, DailyBody = Daily(Row(DateTime.Now.Date, "1", "2", "1", "2", "1")) };
            var kis = new KisProvider(new KisConfig { AppKey = "k", AppSecret = "s", IsPaper = true }, new HttpClient(h));
            var pacer = new PlutoRequestPacer(KisDailyBarSource.MinRequestInterval);
            var us = new KisDailyBarSource(kis, pacer, PlutoScreenMarket.Us);
            var krx = new KisDailyBarSource(kis, pacer);
            var krxEntry = PlutoMultiAssetUniverseCatalog.Of(PlutoScreenMarket.Krx)[0];
            using var go = new ManualResetEventSlim(false);
            var a = Task.Run(() => { go.Wait(); for (int i = 0; i < 2; i++) us.Fetch(UsEntry(i == 0 ? "AAPL" : "JPM"), new PlutoAssetSample(), CancellationToken.None); });
            var b = Task.Run(() => { go.Wait(); for (int i = 0; i < 2; i++) krx.Fetch(krxEntry, new PlutoAssetSample(), CancellationToken.None); });
            go.Set();
            await Task.WhenAll(a, b);
            Assert.True(h.Sends.Count >= 5);                                       // GET 4 + 토큰 POST 1(동시 진입 시 HasFreshToken 경합 = POST 도 슬롯 경유)
            AssertCompletionSpacing(h, h.Sends.Count);
        }

        [Fact]
        public void Source_Token_Failure_Faulted_Releases_Slot_And_Cancel_Sends_Nothing()
        {
            var h = new TimedHandler { DelayMs = 1, FailToken = true };
            var kis = new KisProvider(new KisConfig { AppKey = "k", AppSecret = "s", IsPaper = true }, new HttpClient(h));
            var pacer = new PlutoRequestPacer(TimeSpan.FromMilliseconds(50));
            var src = new KisDailyBarSource(kis, pacer, PlutoScreenMarket.Us);
            Assert.Equal(PlutoFetchOutcome.Faulted, src.Fetch(UsEntry("AAPL"), new PlutoAssetSample(), CancellationToken.None));
            Assert.Equal(PlutoFetchOutcome.Faulted, src.Fetch(UsEntry("AAPL"), new PlutoAssetSample(), CancellationToken.None));   // 독점 해제 확인(교착 0)
            Assert.Equal(2, h.Sends.Count);
            Assert.All(h.Sends, static x => Assert.Equal("POST", x.Kind));          // 토큰 실패 = GET 0
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            Assert.False(pacer.WaitExclusive(cts.Token));
            Assert.Throws<OperationCanceledException>(() => src.Fetch(UsEntry("AAPL"), new PlutoAssetSample(), cts.Token));
            Assert.Equal(2, h.Sends.Count);                                         // 취소 = 송신 0
            Assert.True(pacer.WaitExclusive(CancellationToken.None));               // 취소 경로도 슬롯 반납
            pacer.Complete();
        }

        // ── ④ 스웜 결선 ──────────────────────────────────────────────────────

        [Fact]
        public void Swarm_Us_Lane_Goes_Live_From_Kis_Overseas_Daily()
        {
            var today = DateTime.Now.Date;
            var rows = new string[40];
            for (int i = 0; i < rows.Length; i++)
            {
                decimal c = 200m + i * 0.5m;
                rows[i] = Row(today.AddDays(-i), (c - 0.25m).ToString(CultureInfo.InvariantCulture), (c + 1m).ToString(CultureInfo.InvariantCulture),
                    (c - 1m).ToString(CultureInfo.InvariantCulture), c.ToString(CultureInfo.InvariantCulture), (1_000_000 + i).ToString(CultureInfo.InvariantCulture));
            }
            var h = new StubHandler { Body = () => Daily(rows) };
            var kis = MakeKis(h);
            var pacer = new PlutoRequestPacer(1000);
            var board = new PlutoUniverseMatrixBoard();
            using var sw = new PlutoParallelLearningSwarm(board, new PlutoMultiAssetDataLake(board), 100);
            sw.Attach(new KisDailyBarSource(kis, pacer));
            sw.Attach(new KisDailyBarSource(kis, pacer, PlutoScreenMarket.Us));
            Assert.Equal(PlutoFetchOutcome.Ok, sw.ProcessOne(PlutoScreenMarket.Us, 0, CancellationToken.None));
            Assert.True(board.TryRead(board.SlotOf(PlutoScreenMarket.Us, 0), out var cell));
            Assert.Equal(PlutoMatrixStatus.Live, cell.Status);
            Assert.Equal(20_000L, cell.Inputs.Price);                             // 가장 최근 종가 $200.00 = 20,000센트
            Assert.Equal(UsTechUniverse.Pack("AAPL"), cell.Inputs.Symbol);
            Assert.Equal(40, cell.Bars);
            var st = sw.StatusOf(PlutoScreenMarket.Us);
            Assert.True(st.Attached && st.Connected);
            Assert.StartsWith("KIS 해외 일봉 REST", st.Label);
            Assert.StartsWith("KIS 일봉 REST", sw.StatusOf(PlutoScreenMarket.Krx).Label);
        }

        [Fact]
        public void Host_Wires_Us_Lane_With_Shared_Kis_Pacer()
        {
            var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "StockManager.sln"))) dir = dir.Parent;
            Assert.NotNull(dir);
            string Read(params string[] p) => System.IO.File.ReadAllText(System.IO.Path.Combine(dir!.FullName, System.IO.Path.Combine(p)));
            string form = Read("StockManager", "Blazor", "BlazorHomeForm.cs");
            int pacer = form.IndexOf("var kisSwarmPacer = new StockManager.Engines.Cognitive.Screening.PlutoRequestPacer(StockManager.Adapters.KisDailyBarSource.MinRequestInterval);", StringComparison.Ordinal);
            Assert.True(pacer > 0);
            Assert.True(form.IndexOf("new StockManager.Adapters.KisDailyBarSource(kisForSwarm, kisSwarmPacer), StockManager.Adapters.PlutoCandleCadence.Daily), plutoValuationBuffer)", StringComparison.Ordinal) > pacer);
            Assert.True(form.IndexOf("new StockManager.Adapters.KisDailyBarSource(kisForSwarm, kisSwarmPacer, StockManager.Engines.Cognitive.Screening.PlutoScreenMarket.Us), StockManager.Adapters.PlutoCandleCadence.Daily), plutoValuationBuffer)", StringComparison.Ordinal) > pacer);
            string src = Read("StockManager", "Adapters", "KisDailyBarSource.cs");
            Assert.DoesNotContain("HttpMethod.Post", src);
            Assert.DoesNotContain("/trading/", src);
            string prov = Read("src", "02_Infrastructure", "Adapters", "Brokers", "BrokerProviders.cs");
            int od = prov.IndexOf("private async Task<List<Candle>> GetOverseasDailyAsync", StringComparison.Ordinal);
            Assert.True(od > 0);
            Assert.Contains("SendGatedAsync(req, ct)", prov[od..]);                // 레이트 게이트 경유
        }
    }
}
