using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace StockManager.Tests
{
    // S25-a — 격리 라이브 인그레스 + 방벽 어댑터.
    //   §1 LiveIngressDriver(펌프·트립와이어 edge·수명)  §2 BulkheadCandleSource(어댑터 fail-safe)
    //   §3 방벽 불변식 반증(네트워크는 지연/고갈만, 결정 오염 불가) — 검증된 기계가 안전하게 멈춤
    public sealed class S25aLiveIngressTests
    {
        private sealed class TestClock
        {
            public DateTime Now = new DateTime(2026, 6, 1, 9, 0, 0, DateTimeKind.Utc);
            public DateTime Get() => Now;
        }

        private sealed class FakeSource : ICandleSource
        {
            public Func<Candle[], int>? Fill;
            public int  N;
            public bool Throw;
            public int FillLatest(Candle[] buffer)
            {
                if (Throw) throw new InvalidOperationException("net down");
                return Fill != null ? Fill(buffer) : N;
            }
        }

        private sealed class FakeQuoteProvider : IQuoteProvider
        {
            public Func<List<Candle>?>? History;
            public bool Throw;

            public DataSource Source => default;
            public Market[] SupportedMarkets => Array.Empty<Market>();
            public bool IsRealtime => false;
            public Task<Quote?> GetQuoteAsync(string s, Market m, CancellationToken ct = default) => Task.FromResult<Quote?>(null);
            public Task<List<Quote>> GetQuotesAsync(IEnumerable<string> s, Market m, CancellationToken ct = default) => Task.FromResult(new List<Quote>());
            public Task<List<Candle>> GetHistoryAsync(string s, Market m, DateTime from, DateTime to, CancellationToken ct = default)
            {
                if (Throw) throw new InvalidOperationException("net down");
                return Task.FromResult(History?.Invoke() ?? new List<Candle>());
            }
            public event EventHandler<Quote>? QuoteUpdated { add { } remove { } }   // CS0067 회피
            public Task SubscribeAsync(string s, Market m) => Task.CompletedTask;
            public Task UnsubscribeAsync(string s, Market m) => Task.CompletedTask;
        }

        private static int FillValid(Candle[] buf, int n, int phase = 0)
        {
            var start = new DateTime(2026, 6, 1);
            for (int i = 0; i < n; i++)
            {
                decimal close = 10000m + (decimal)(Math.Sin((i + phase) * 0.5) * 800.0) + i * 12m;
                buf[i].Date = start.AddDays(i);
                buf[i].Open = close; buf[i].High = close * 1.01m;
                buf[i].Low = close * 0.99m; buf[i].Close = close; buf[i].Volume = 1000;
            }
            return n;
        }

        private static List<Candle> CandleList(int n)
        {
            var list = new List<Candle>(n);
            var start = new DateTime(2026, 6, 1);
            for (int i = 0; i < n; i++)
            {
                decimal close = 10000m + (decimal)(Math.Sin(i * 0.5) * 800.0) + i * 12m;
                list.Add(new Candle { Date = start.AddDays(i), Open = close, High = close * 1.01m, Low = close * 0.99m, Close = close, Volume = 1000 });
            }
            return list;
        }

        private static (GreenhouseService gh, LiveCandleFeed feed) MakeFeed(ICandleSource src, GreenhouseService? gh = null)
        {
            gh ??= new GreenhouseService(new PromotionGateway());
            var feed = new LiveCandleFeed(gh, src, GreenhouseActivator.DefaultSeeds(), Market.KRX_KOSPI, 120, 60);
            return (gh, feed);
        }
        private static bool WaitFor(Func<bool> c, int ms = 2000)
        {
            var dl = DateTime.UtcNow.AddMilliseconds(ms);
            while (DateTime.UtcNow < dl) { if (c()) return true; Thread.Sleep(10); }
            return c();
        }

        // ═══════════════════ §1 LiveIngressDriver ═══════════════════

        [Fact]
        public void Driver_PumpOnce_ValidData_Reprimes()
        {
            var (gh, feed) = MakeFeed(new FakeSource { Fill = b => FillValid(b, 120) });
            var drv = new LiveIngressDriver(feed);
            Assert.Equal(FeedOutcome.Reprimed, drv.PumpOnce());
            Assert.Equal(1, drv.Refreshes);
            gh.RunCycleOnce();
            Assert.True(gh.Probe().Mutations > 0);   // 실 데이터로 진화
        }

        [Fact]
        public void Driver_Disconnected_Drops()
        {
            var (_, feed) = MakeFeed(new FakeSource { N = 0 });
            var drv = new LiveIngressDriver(feed);
            Assert.Equal(FeedOutcome.Insufficient, drv.PumpOnce());
            Assert.Equal(1, drv.Drops);
        }

        [Fact]
        public void Driver_SourceThrows_Drops_NoCrash()
        {
            var (_, feed) = MakeFeed(new FakeSource { Throw = true });
            Assert.Equal(FeedOutcome.SourceError, new LiveIngressDriver(feed).PumpOnce());
        }

        [Fact]
        public void Driver_Tripwire_EdgeTriggered()
        {
            bool stale = false;
            var events = new List<bool>();
            var (_, feed) = MakeFeed(new FakeSource { N = 0 });
            var drv = new LiveIngressDriver(feed, staleProbe: () => stale, onStaleChanged: events.Add);

            drv.PumpOnce();                       // false→false: 발화 없음
            Assert.Empty(events);
            stale = true; drv.PumpOnce();          // false→true: 발화(true)
            stale = true; drv.PumpOnce();          // true→true: 발화 없음
            stale = false; drv.PumpOnce();         // true→false: 발화(false)
            Assert.Equal(new[] { true, false }, events);
        }

        [Fact]
        public void Driver_StartStop_Lifecycle()
        {
            var (_, feed) = MakeFeed(new FakeSource { Fill = b => FillValid(b, 120) });
            var drv = new LiveIngressDriver(feed, intervalMs: 100);
            drv.Start();
            Assert.True(drv.Running);
            Assert.True(WaitFor(() => drv.Pumps > 0), "백그라운드 펌프 가동");
            drv.Stop();
            Assert.False(drv.Running);
        }

        // ═══════════════════ §2 BulkheadCandleSource ═══════════════════

        [Fact]
        public void Bulkhead_ProviderCandles_FillsNewest()
        {
            var list = CandleList(200);
            var src  = new BulkheadCandleSource(new FakeQuoteProvider { History = () => list }, "069500", Market.KRX_KOSPI);
            var buf  = new Candle[120];
            for (int i = 0; i < 120; i++) buf[i] = new Candle();
            int n = src.FillLatest(buf);
            Assert.Equal(120, n);                       // min(200,120)
            Assert.Equal(list[199].Close, buf[119].Close);   // 최신(200번째) 보존
            Assert.Equal(list[80].Close, buf[0].Close);      // 200-120 = 80
        }

        [Fact]
        public void Bulkhead_ProviderThrows_ReturnsZero()
        {
            var src = new BulkheadCandleSource(new FakeQuoteProvider { Throw = true }, "x", Market.KRX_KOSPI);
            Assert.Equal(0, src.FillLatest(new[] { new Candle() }));   // 네트워크 예외 → 고갈(0), 무전파
        }

        [Fact]
        public void Bulkhead_EmptyOrNull_ReturnsZero()
        {
            Assert.Equal(0, new BulkheadCandleSource(new FakeQuoteProvider { History = () => new List<Candle>() }, "x", Market.KRX_KOSPI).FillLatest(new[] { new Candle() }));
            Assert.Equal(0, new BulkheadCandleSource(new FakeQuoteProvider { History = () => null }, "x", Market.KRX_KOSPI).FillLatest(new[] { new Candle() }));
        }

        [Fact]
        public void Bulkhead_NullArgs_Throw()
        {
            Assert.Throws<ArgumentNullException>(() => new BulkheadCandleSource(null!, "x", Market.KRX_KOSPI));
            Assert.Throws<ArgumentNullException>(() => new BulkheadCandleSource(new FakeQuoteProvider(), null!, Market.KRX_KOSPI));
        }

        // ═══════════════════ §3 방벽 불변식 반증 ═══════════════════

        [Fact]
        public void Bulkhead_Disconnected_GreenhouseFailsClosed_NotCorrupted()
        {
            // 미션2 핵심 — "네트워크 오류가 결정을 오염시키는 게 아니라, 검증된 기계가 안전하게 멈춘다."
            var clk = new TestClock();
            var gh = new GreenhouseService(new PromotionGateway()) { StalenessWindow = TimeSpan.FromSeconds(30), Clock = clk.Get };
            var src = new FakeSource { Fill = b => FillValid(b, 120) };
            var (_, feed) = MakeFeed(src, gh);
            var drv = new LiveIngressDriver(feed);

            drv.PumpOnce();                         // 유효 → prime(신선)
            gh.RunCycleOnce();
            long m1 = gh.Probe().Mutations;
            Assert.True(m1 > 0);

            src.Fill = null; src.N = 0;             // 네트워크 단절
            clk.Now = clk.Now.AddSeconds(60);       // staleness 초과
            Assert.Equal(FeedOutcome.Insufficient, drv.PumpOnce());   // 드롭(무프라임)
            gh.RunCycleOnce();
            Assert.Equal(m1, gh.Probe().Mutations); // 새 진화 0 — 결정 오염 아닌 안전 정지
            Assert.True(gh.FeedStale);
            Assert.True(gh.StalePauses > 0);
        }

        [Fact]
        public void Bulkhead_MalformedData_Dropped_NotPrimed()
        {
            // 독약 패킷(음수 가격) — 방벽 검증이 통째 드롭, 내부 코어 미오염.
            var (gh, feed) = MakeFeed(new FakeSource { Fill = b => { FillValid(b, 120); b[40].Close = -5m; return 120; } });
            var drv = new LiveIngressDriver(feed);
            Assert.Equal(FeedOutcome.Corrupt, drv.PumpOnce());
            gh.RunCycleOnce();
            Assert.Equal(0, gh.Probe().Mutations);   // 오염 데이터 미프라임 → 배양 안 함
        }

        [Fact]
        public void Bulkhead_NonMonotonic_Dropped()
        {
            var (_, feed) = MakeFeed(new FakeSource { Fill = b => { FillValid(b, 120); b[50].Date = b[49].Date.AddDays(-3); return 120; } });
            Assert.Equal(FeedOutcome.Corrupt, new LiveIngressDriver(feed).PumpOnce());
        }

        [Fact]
        public void Prime_DeepCopies_IngressBufferMutationDoesNotLeak()
        {
            // ★적대적 리뷰 회귀(critical) — Candle 은 가변 class. 라이브 인그레스가 재사용 버퍼를 in-place
            //   덮어써도, Prime 의 값 복제(deep copy) 덕에 온실 _candles 는 격리돼 오염이 결정으로 새지 않는다.
            var seeds = GreenhouseActivator.DefaultSeeds();
            var input = new List<Candle>();
            foreach (var c in GreenhouseActivator.BootstrapCandles())
                input.Add(new Candle { Date = c.Date, Open = c.Open, High = c.High, Low = c.Low, Close = c.Close, Volume = c.Volume });

            var ghRef = new GreenhouseService(new PromotionGateway());
            ghRef.Prime(seeds, input, Market.KRX_KOSPI);
            ghRef.RunCycleOnce();
            double refFit = ghRef.Probe().BestFitness;

            var ghMut = new GreenhouseService(new PromotionGateway());
            ghMut.Prime(seeds, input, Market.KRX_KOSPI);                       // 양호 입력으로 prime
            foreach (var c in input) { c.Open = c.High = c.Low = c.Close = 1m; }   // 입력 오염(버퍼 덮어쓰기 시뮬)
            ghMut.RunCycleOnce();                                              // 격리됐다면 오염 무영향

            Assert.Equal(refFit, ghMut.Probe().BestFitness);   // deep-copy 격리 → 동일. 얕은 복사였다면 달라짐.
        }

        [Fact]
        public void Bulkhead_Hiccup_AbsorbedWithinWindow()
        {
            // 일시적 1회 누락은 윈도우 내에서 마지막 유효로 흡수(가용성), 윈도우 내엔 fail-closed 안 함.
            var clk = new TestClock();
            var gh = new GreenhouseService(new PromotionGateway()) { StalenessWindow = TimeSpan.FromSeconds(60), Clock = clk.Get };
            var src = new FakeSource { Fill = b => FillValid(b, 120) };
            var (_, feed) = MakeFeed(src, gh);
            var drv = new LiveIngressDriver(feed);

            drv.PumpOnce();                         // 유효 prime
            src.Fill = null; src.N = 0;             // 힉컵
            clk.Now = clk.Now.AddSeconds(20);       // 윈도우(60s) 내
            drv.PumpOnce();                         // 드롭이나 아직 신선
            gh.RunCycleOnce();
            Assert.True(gh.Probe().Mutations > 0);  // 마지막 유효로 계속 배양(가용성 흡수)
            Assert.False(gh.FeedStale);
        }
    }
}
