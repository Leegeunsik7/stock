// ============================================================================
// KisErrorClassificationTests — [v12.18.22-DEV] KIS 비정형 한도 초과(HTTP 500 + msg_cd EGW00201) 분류 · 백오프/래치 결선
//
//  §1 분류 — 429(본문 무관) · 500 + EGW00201 · BOM · 공백/필드 순서 · 성공 상태 무시 · 다른 msg_cd · 중첩 msg_cd · msg1 문구만 · 비문자열 · 형식 오류 · 빈 본문
//  §2 KisProvider 결선(스텁 핸들러 · 실 네트워크 0) — EGW 1회 = 연속 1 + 백오프 창 · 3회 = sticky 래치 + 이후 서버 도달 0 · 다른 500 = 백오프 무기록 · 성공 = 연속 리셋
//  §3 δ=0 — 10만 회 분류 0B
// ============================================================================
using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MS2E.Infrastructure;
using Xunit;

namespace StockManager.Tests.Adapters
{
    [Collection("ConcurrencyStress")]
    public sealed class KisErrorClassificationTests
    {
        private const long Us = 1_000_000;
        private const string Egw = "{\"rt_cd\":\"1\",\"msg_cd\":\"EGW00201\",\"msg1\":\"초당 거래건수를 초과하였습니다.\"}";

        private static bool Classify(int status, string body) => KisRateLimitClassifier.IsRateLimit(status, Encoding.UTF8.GetBytes(body));

        // ════════════ §1 분류 ════════════

        [Theory]
        [InlineData(500, Egw, true)]
        [InlineData(500, "{ \"msg1\" : \"x\" ,\n \"msg_cd\" : \"EGW00201\" , \"rt_cd\":\"1\" }", true)]   // 필드 순서 · 공백
        [InlineData(400, Egw, true)]                                                                  // 비성공 상태 전반
        [InlineData(429, "", true)]                                                                   // 표준 429 = 본문 무관
        [InlineData(429, "not json", true)]
        [InlineData(200, Egw, false)]                                                                 // 성공 상태 = 무시
        [InlineData(500, "{\"rt_cd\":\"1\",\"msg_cd\":\"EGW00123\",\"msg1\":\"기간이 만료된 token 입니다.\"}", false)]
        [InlineData(500, "{\"output\":{\"msg_cd\":\"EGW00201\"},\"msg_cd\":\"OPSQ0001\"}", false)]    // 중첩 msg_cd 무시 · 최상위 판정
        [InlineData(500, "{\"output\":[{\"msg_cd\":\"EGW00201\"}]}", false)]
        [InlineData(500, "{\"msg1\":\"초당 거래건수를 초과하였습니다.\"}", false)]                       // 문구만 = 아님(보수적)
        [InlineData(500, "{\"msg_cd\":201}", false)]                                                  // 비문자열
        [InlineData(500, "{\"msg_cd\":\"EGW002010\"}", false)]                                        // 접두 일치 ≠ 일치
        [InlineData(500, "{\"msg_cd\":\"EGW0020", false)]                                             // 잘린 본문
        [InlineData(500, "<html>500 Internal Server Error</html>", false)]
        [InlineData(500, "", false)]
        [InlineData(500, "[\"EGW00201\"]", false)]
        public void Classifier_Cases(int status, string body, bool expected) => Assert.Equal(expected, Classify(status, body));

        [Fact]
        public void Classifier_Accepts_Utf8Bom_And_NestedBeforeTopLevel()
        {
            byte[] bom = new byte[] { 0xEF, 0xBB, 0xBF };
            byte[] body = Encoding.UTF8.GetBytes(Egw);
            var withBom = new byte[bom.Length + body.Length];
            bom.CopyTo(withBom, 0); body.CopyTo(withBom, bom.Length);
            Assert.True(KisRateLimitClassifier.IsRateLimit(500, withBom));
            Assert.True(Classify(500, "{\"output\":{\"msg_cd\":\"OK\",\"x\":[1,2,{\"y\":3}]},\"msg_cd\":\"EGW00201\"}"));
            Assert.Equal("EGW00201", KisRateLimitClassifier.RateLimitMsgCode);
        }

        // ════════════ §2 KisProvider 결선 ════════════

        private sealed class Clock
        {
            public long T;
            public long Now() => Volatile.Read(ref T);
            public void Advance(long micros) => Interlocked.Add(ref T, micros);
        }

        private sealed class StubHandler : HttpMessageHandler
        {
            public int Quotes;
            public HttpStatusCode Status = HttpStatusCode.OK;
            public string Body = "{\"output\":{\"stck_prpr\":\"70000\",\"prdy_vrss\":\"100\",\"prdy_vrss_sign\":\"2\"}}";
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
            {
                if (req.RequestUri!.AbsolutePath.EndsWith("/oauth2/tokenP", StringComparison.Ordinal))
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"access_token\":\"t\"}", Encoding.UTF8, "application/json") });
                Interlocked.Increment(ref Quotes);
                return Task.FromResult(new HttpResponseMessage(Status) { Content = new StringContent(Body, Encoding.UTF8, "application/json") });
            }
        }

        private static KisProvider MakeKis(StubHandler h, Clock c, out BrokerBackoffController bo)
        {
            bo = new BrokerBackoffController(c.Now, Us);
            return new KisProvider(new KisConfig { AppKey = "k", AppSecret = "s", IsPaper = true }, new HttpClient(h),
                                   new LockFreeTokenBucket(KisProvider.DefaultPerSecond, LockFreeTokenBucket.SecondMicros, nowTicks: c.Now, tickFrequency: Us), bo);
        }

        [Fact]
        public async Task Egw00201_Once_ImmediateBackoff_ConsecutiveOne()
        {
            var h = new StubHandler { Status = HttpStatusCode.InternalServerError, Body = Egw };
            var c = new Clock();
            var kis = MakeKis(h, c, out var bo);
            Assert.Null(await kis.GetQuoteAsync("005930", Market.KRX_KOSPI));
            Assert.Equal((1L, 1L, 1L), (bo.Consecutive, bo.LimitHits, kis.EgwRateLimits));
            Assert.InRange(bo.LastDelayMicros, 100_000, 125_000);
            Assert.False(bo.TryEnter());                                         // 즉시 백오프 창
            Assert.Null(await kis.GetQuoteAsync("005930", Market.KRX_KOSPI));    // 창 안 = 서버 도달 0
            Assert.Equal(1, h.Quotes);
            Assert.False(bo.Latched);
        }

        [Fact]
        public async Task Egw00201_ThreeConsecutive_StickyLatch_NoFurtherServerCalls()
        {
            var h = new StubHandler { Status = HttpStatusCode.InternalServerError, Body = Egw };
            var c = new Clock();
            var kis = MakeKis(h, c, out var bo);
            for (int i = 0; i < 3; i++) { Assert.Null(await kis.GetQuoteAsync("005930", Market.KRX_KOSPI)); c.Advance(4 * Us); }
            Assert.True(bo.Latched);
            Assert.Equal(3, kis.EgwRateLimits);
            h.Status = HttpStatusCode.OK; h.Body = new StubHandler().Body;
            c.Advance(60 * Us);
            Assert.Null(await kis.GetQuoteAsync("005930", Market.KRX_KOSPI));
            Assert.Empty(await kis.GetHistoryAsync("005930", Market.KRX_KOSPI, DateTime.Today.AddDays(-5), DateTime.Today));
            Assert.Equal(3, h.Quotes);                                           // ★래치 후 서버 도달 0
            Assert.True(bo.TryRearm(true, true));
            Assert.NotNull(await kis.GetQuoteAsync("005930", Market.KRX_KOSPI));
        }

        [Fact]
        public async Task MixedEgwAnd429_CountTogether_TowardLatch()
        {
            var h = new StubHandler { Status = HttpStatusCode.InternalServerError, Body = Egw };
            var c = new Clock();
            var kis = MakeKis(h, c, out var bo);
            await kis.GetQuoteAsync("005930", Market.KRX_KOSPI); c.Advance(4 * Us);
            h.Status = (HttpStatusCode)429; h.Body = "";
            await kis.GetQuoteAsync("005930", Market.KRX_KOSPI); c.Advance(4 * Us);
            h.Status = HttpStatusCode.InternalServerError; h.Body = Egw;
            await kis.GetQuoteAsync("005930", Market.KRX_KOSPI);
            Assert.True(bo.Latched);
            Assert.Equal(2, kis.EgwRateLimits);
        }

        [Fact]
        public async Task Other500_NotRateLimit_SuccessResets()
        {
            var h = new StubHandler { Status = HttpStatusCode.InternalServerError, Body = "{\"rt_cd\":\"1\",\"msg_cd\":\"EGW00123\"}" };
            var c = new Clock();
            var kis = MakeKis(h, c, out var bo);
            Assert.Null(await kis.GetQuoteAsync("005930", Market.KRX_KOSPI));
            Assert.Equal((0L, 0L), (bo.Consecutive, kis.EgwRateLimits));
            Assert.True(bo.TryEnter());                                          // 일반 오류 = 백오프 없음
            h.Body = Egw;
            await kis.GetQuoteAsync("005930", Market.KRX_KOSPI);
            Assert.Equal(1, bo.Consecutive);
            c.Advance(4 * Us);
            h.Status = HttpStatusCode.OK; h.Body = "{\"output\":{\"stck_prpr\":\"70000\",\"prdy_vrss\":\"0\",\"prdy_vrss_sign\":\"3\"}}";
            Assert.NotNull(await kis.GetQuoteAsync("005930", Market.KRX_KOSPI));
            Assert.Equal(0, bo.Consecutive);                                     // 성공 = 연속 리셋
        }

        // ════════════ §3 δ=0 ════════════

        [Fact]
        public void HundredThousand_Classifications_ZeroAlloc()
        {
            // 정상 형식 본문(일치 · 불일치 · 중첩 건너뜀) = 0B. 형식 오류 본문은 판독기가 JsonException 을 던진다(예외 객체 할당 · 콜드 오류 경로 · 판정 = false).
            byte[] egw = Encoding.UTF8.GetBytes(Egw);
            byte[] other = Encoding.UTF8.GetBytes("{\"output\":{\"msg_cd\":\"EGW00201\"},\"rt_cd\":\"1\",\"msg_cd\":\"EGW00123\"}");
            KisRateLimitClassifier.IsRateLimit(500, egw); KisRateLimitClassifier.IsRateLimit(500, other);
            int hits = 0;
            long min = long.MaxValue;
            for (int w = 0; w < 3 && min != 0; w++)
            {
                hits = 0;
                long b = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < 100_000; i++)
                {
                    if (KisRateLimitClassifier.IsRateLimit(500, egw)) hits++;
                    if (KisRateLimitClassifier.IsRateLimit(500, other)) hits--;
                }
                min = Math.Min(min, GC.GetAllocatedBytesForCurrentThread() - b);
            }
            Assert.Equal(0, min);
            Assert.Equal(100_000, hits);
        }
    }
}
