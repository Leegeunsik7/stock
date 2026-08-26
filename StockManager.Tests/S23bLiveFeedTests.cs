using System;
using Xunit;

namespace StockManager.Tests
{
    // S23-b — 라이브 피드 결속의 3대 비대칭 안전 규칙 검증.
    //   규칙1: isDirty 숏서킷 (GreenhouseService.SkipUnchanged)
    //   규칙2: 캔들 staleness fail-closed (GreenhouseService.StalenessWindow) + 피드 검증(LiveCandleFeed)
    //   규칙3: As-Of 락아웃 + 승인→활성 레이스 방어 (PromotionGateway)
    // 모든 시간 의존 로직은 주입 클록으로 결정론화.
    public sealed class S23bLiveFeedTests
    {
        private sealed class TestClock
        {
            public DateTime Now = new DateTime(2026, 3, 1, 9, 0, 0, DateTimeKind.Utc);
            public DateTime Get() => Now;
        }

        private sealed class FakeSource : ICandleSource
        {
            public Func<Candle[], int> Filler = _ => 0;
            public Candle[]? LastBuffer;
            public int Calls;
            public int FillLatest(Candle[] buffer) { Calls++; LastBuffer = buffer; return Filler(buffer); }
        }

        private static int FillValid(Candle[] buf, int n, int phase = 0)
        {
            var start = new DateTime(2026, 3, 1);
            for (int i = 0; i < n; i++)
            {
                decimal close = 10000m + (decimal)(Math.Sin((i + phase) * 0.5) * 800.0) + i * 12m;
                buf[i].Date = start.AddDays(i);
                buf[i].Open = close; buf[i].High = close * 1.01m;
                buf[i].Low = close * 0.99m; buf[i].Close = close; buf[i].Volume = 1000;
            }
            return n;
        }

        private static StrategySpec Spec() => GreenhouseActivator.DefaultSeeds()[0];

        // ═══════════════════ 규칙3: As-Of 락아웃 (PromotionGateway) ═══════════════════

        [Fact]
        public void Approve_FreshProposal_Succeeds()
        {
            var clk = new TestClock();
            var gw = new PromotionGateway(null, null, s => s == "CMDR")
            { ProposalFreshnessWindow = TimeSpan.FromMinutes(5), Clock = clk.Get };
            gw.Submit(Spec(), 1.0, clk.Now.Ticks);
            clk.Now = clk.Now.AddMinutes(2);                  // 윈도우 내
            Assert.True(gw.Approve("CMDR"));
            Assert.Equal(PromotionState.Approved, gw.State);
        }

        [Fact]
        public void Approve_StaleProposal_BouncesToRejected()
        {
            var clk = new TestClock();
            var gw = new PromotionGateway(null, null, s => s == "CMDR")
            { ProposalFreshnessWindow = TimeSpan.FromMinutes(5), Clock = clk.Get };
            gw.Submit(Spec(), 1.0, clk.Now.Ticks);
            clk.Now = clk.Now.AddMinutes(10);                 // 윈도우 초과
            Assert.False(gw.Approve("CMDR"));                 // 서명 올발라도 거부
            Assert.Equal(PromotionState.Rejected, gw.State);
        }

        [Fact]
        public void Activate_StaleAfterApprove_BouncesToRejected_NoSwap()
        {
            // 핵심 레이스: 승인 시점엔 신선했으나, 활성 직전 마켓 타임이 흘러 열화 → 핫스왑 차단
            var clk = new TestClock();
            var gw = new PromotionGateway(null, null, s => s == "CMDR")
            { ProposalFreshnessWindow = TimeSpan.FromMinutes(5), Clock = clk.Get };
            gw.Submit(Spec(), 1.0, clk.Now.Ticks);
            Assert.True(gw.Approve("CMDR"));                  // 신선 → Approved
            clk.Now = clk.Now.AddMinutes(10);                // 승인→활성 갭에서 열화

            var slot = new StrategySlot();
            Assert.False(gw.Activate(slot));                 // 비가역 swap 직전 재검문 → 거부
            Assert.Equal(PromotionState.Rejected, gw.State);
            Assert.Equal(0, slot.Generation);                // swap 미발생(원자 방어)
        }

        [Fact]
        public void NoWindow_NoGating_BackwardCompat()
        {
            var gw = new PromotionGateway(null, null, s => s == "CMDR");   // ProposalFreshnessWindow=null
            gw.Submit(Spec(), 1.0);                                         // asOf=0
            Assert.True(gw.Approve("CMDR"));
            Assert.True(gw.Activate(new StrategySlot()));                   // 게이팅 없음 → 현행 동작
        }

        [Fact]
        public void AsOfZero_NeverStale_EvenWithWindow()
        {
            var clk = new TestClock();
            var gw = new PromotionGateway(null, null, s => s == "CMDR")
            { ProposalFreshnessWindow = TimeSpan.FromMinutes(5), Clock = clk.Get };
            gw.Submit(Spec(), 1.0, 0L);                       // As-Of 미박제
            clk.Now = clk.Now.AddDays(365);
            Assert.True(gw.Approve("CMDR"));                  // asOf=0 → 게이팅 면제
        }

        [Fact]
        public void Submit_StampsAsOf()
        {
            var gw = new PromotionGateway();
            long ticks = new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc).Ticks;
            gw.Submit(Spec(), 1.0, ticks);
            Assert.Equal(ticks, gw.Current!.AsOfTicks);
        }

        // ═══════════════════ 규칙2: staleness fail-closed (Greenhouse) ════════════════

        [Fact]
        public void Staleness_FreshData_Cultivates_StaleData_FailsClosed()
        {
            var clk = new TestClock();
            var gh = new GreenhouseService(new PromotionGateway())
            { StalenessWindow = TimeSpan.FromSeconds(30), Clock = clk.Get };
            gh.Prime(GreenhouseActivator.DefaultSeeds(), GreenhouseActivator.BootstrapCandles(), Market.KRX_KOSPI);

            clk.Now = clk.Now.AddSeconds(10);                 // 윈도우 내 → 배양
            gh.RunCycleOnce();
            long m1 = gh.Probe().Mutations;
            Assert.True(m1 > 0);

            clk.Now = clk.Now.AddSeconds(60);                 // staleness 초과 → fail-closed
            gh.RunCycleOnce();
            Assert.Equal(m1, gh.Probe().Mutations);           // 새 진화 없음
            Assert.True(gh.StalePauses > 0);
            Assert.True(gh.FeedStale);
        }

        [Fact]
        public void Staleness_Off_ByDefault_NoGating()
        {
            var gh = new GreenhouseService(new PromotionGateway());   // StalenessWindow=null
            Assert.False(gh.FeedStale);                                // 게이팅 없음 → 항상 신선 취급
            gh.Prime(GreenhouseActivator.DefaultSeeds(), GreenhouseActivator.BootstrapCandles(), Market.KRX_KOSPI);
            gh.RunCycleOnce();
            Assert.True(gh.Probe().Mutations > 0);
        }

        // ═══════════════════ 규칙1: isDirty 숏서킷 (Greenhouse) ═══════════════════════

        [Fact]
        public void SkipUnchanged_SameGeneration_ShortCircuits()
        {
            var gh = new GreenhouseService(new PromotionGateway()) { SkipUnchanged = true };
            gh.Prime(GreenhouseActivator.DefaultSeeds(), GreenhouseActivator.BootstrapCandles(), Market.KRX_KOSPI);

            gh.RunCycleOnce();                                // 세대1 dirty → 진화
            long m1 = gh.Probe().Mutations;
            Assert.True(m1 > 0);

            gh.RunCycleOnce();                                // 세대 불변 → 숏서킷(진화 0)
            Assert.Equal(m1, gh.Probe().Mutations);
            Assert.True(gh.Probe().Cycles >= 2);              // 사이클은 돌되 변이는 안 늘어남
        }

        [Fact]
        public void SkipUnchanged_AfterReprime_EvolvesAgain()
        {
            var gh = new GreenhouseService(new PromotionGateway()) { SkipUnchanged = true };
            gh.Prime(GreenhouseActivator.DefaultSeeds(), GreenhouseActivator.BootstrapCandles(), Market.KRX_KOSPI);
            gh.RunCycleOnce();
            long m1 = gh.Probe().Mutations;

            gh.Prime(GreenhouseActivator.DefaultSeeds(), GreenhouseActivator.BootstrapCandles(), Market.KRX_KOSPI);  // 새 세대
            gh.RunCycleOnce();                                // dirty 재개 → 진화
            Assert.True(gh.Probe().Mutations > m1);
        }

        // ═══════════════════ 규칙1·2: LiveCandleFeed seam ════════════════════════════

        private static LiveCandleFeed Feed(FakeSource src, GreenhouseService gh)
            => new LiveCandleFeed(gh, src, GreenhouseActivator.DefaultSeeds(), Market.KRX_KOSPI, windowSize: 120, minWindow: 60);

        [Fact]
        public void Refresh_ValidChanged_Reprimes()
        {
            var src = new FakeSource { Filler = b => FillValid(b, 120) };
            var gh = new GreenhouseService(new PromotionGateway());
            Assert.Equal(FeedOutcome.Reprimed, Feed(src, gh).Refresh());
            gh.RunCycleOnce();
            Assert.True(gh.Probe().Mutations > 0);            // 재-Prime 된 데이터로 실제 진화
        }

        [Fact]
        public void Refresh_Unchanged_ShortCircuits()
        {
            var src = new FakeSource { Filler = b => FillValid(b, 120) };
            var feed = Feed(src, new GreenhouseService(new PromotionGateway()));
            Assert.Equal(FeedOutcome.Reprimed, feed.Refresh());
            Assert.Equal(FeedOutcome.Unchanged, feed.Refresh());   // 동일 데이터 → 숏서킷
            Assert.Equal(1, feed.Reprimes);
        }

        [Fact]
        public void Refresh_ChangedData_ReprimesAgain()
        {
            int phase = 0;
            var src = new FakeSource { Filler = b => FillValid(b, 120, phase) };
            var feed = Feed(src, new GreenhouseService(new PromotionGateway()));
            Assert.Equal(FeedOutcome.Reprimed, feed.Refresh());
            phase = 7;                                         // 데이터 변화
            Assert.Equal(FeedOutcome.Reprimed, feed.Refresh());
            Assert.Equal(2, feed.Reprimes);
        }

        [Fact]
        public void Refresh_Insufficient_Drops()
        {
            var src = new FakeSource { Filler = b => FillValid(b, 40) };   // < minWindow 60
            var feed = Feed(src, new GreenhouseService(new PromotionGateway()));
            Assert.Equal(FeedOutcome.Insufficient, feed.Refresh());
            Assert.Equal(1, feed.Drops);
        }

        [Fact]
        public void Refresh_CorruptWindow_Drops()
        {
            // 비단조(역순) 타임스탬프 주입 → 검증 거부
            var src = new FakeSource
            {
                Filler = b =>
                {
                    FillValid(b, 120);
                    b[50].Date = b[49].Date.AddDays(-5);       // 단조성 위반
                    return 120;
                }
            };
            Assert.Equal(FeedOutcome.Corrupt, Feed(src, new GreenhouseService(new PromotionGateway())).Refresh());
        }

        [Fact]
        public void Refresh_NegativeOhlc_Drops()
        {
            var src = new FakeSource
            {
                Filler = b => { FillValid(b, 120); b[10].Close = -1m; return 120; }
            };
            Assert.Equal(FeedOutcome.Corrupt, Feed(src, new GreenhouseService(new PromotionGateway())).Refresh());
        }

        [Fact]
        public void Refresh_SourceThrows_Drops()
        {
            var src = new FakeSource { Filler = _ => throw new InvalidOperationException("net down") };
            Assert.Equal(FeedOutcome.SourceError, Feed(src, new GreenhouseService(new PromotionGateway())).Refresh());
        }

        [Fact]
        public void Refresh_ReusesSameBuffer_NoPerFetchAllocation()
        {
            var src = new FakeSource { Filler = b => FillValid(b, 120) };
            var feed = Feed(src, new GreenhouseService(new PromotionGateway()));
            feed.Refresh();
            var first = src.LastBuffer;
            feed.Refresh();
            Assert.Same(first, src.LastBuffer);                // 매 fetch 동일 버퍼 재사용(할당 0)
        }

        [Fact]
        public void Feed_NullArgs_Throw()
        {
            var gh = new GreenhouseService(new PromotionGateway());
            Assert.Throws<ArgumentNullException>(() => new LiveCandleFeed(null!, new FakeSource(), GreenhouseActivator.DefaultSeeds(), Market.KRX_KOSPI));
            Assert.Throws<ArgumentNullException>(() => new LiveCandleFeed(gh, null!, GreenhouseActivator.DefaultSeeds(), Market.KRX_KOSPI));
            Assert.Throws<ArgumentNullException>(() => new LiveCandleFeed(gh, new FakeSource(), null!, Market.KRX_KOSPI));
        }
    }
}
