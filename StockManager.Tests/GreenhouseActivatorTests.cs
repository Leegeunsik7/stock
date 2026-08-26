using System;
using System.Linq;
using System.Threading;
using Xunit;

namespace StockManager.Tests
{
    // S23 — 자율 배양 엔진 점등 검증. 결정론 코어(씨앗·캔들·1회 사이클)는 동기 검증,
    // 스레드 수명은 WaitFor 로 안정 검증(타이밍 플래키 회피).
    public sealed class GreenhouseActivatorTests
    {
        private static bool WaitFor(Func<bool> cond, int timeoutMs = 2000)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < deadline) { if (cond()) return true; Thread.Sleep(10); }
            return cond();
        }

        // ── 결정론 씨앗 ─────────────────────────────────────────────────────────

        [Fact]
        public void DefaultSeeds_AreAllValid()
        {
            var seeds = GreenhouseActivator.DefaultSeeds();
            Assert.NotEmpty(seeds);
            foreach (var s in seeds)
            {
                var (ok, err) = StrategyCompiler.Validate(s);
                Assert.True(ok, $"{s.Id}: {err}");
            }
        }

        [Fact]
        public void DefaultSeeds_HaveUniqueIds()
        {
            var ids = GreenhouseActivator.DefaultSeeds().Select(s => s.Id).ToList();
            Assert.Equal(ids.Count, ids.Distinct().Count());
        }

        // ── 결정론 부트스트랩 캔들 ───────────────────────────────────────────────

        [Fact]
        public void BootstrapCandles_AreSufficientAndDeterministic()
        {
            var a = GreenhouseActivator.BootstrapCandles();
            var b = GreenhouseActivator.BootstrapCandles();
            Assert.True(a.Count >= 60);
            Assert.Equal(a.Count, b.Count);
            for (int i = 0; i < a.Count; i++)
                Assert.Equal(a[i].Close, b[i].Close);   // 같은 호출 → 같은 시리즈
        }

        [Fact]
        public void BootstrapCandles_MinimumEnforced()
            => Assert.True(GreenhouseActivator.BootstrapCandles(10).Count >= 60);   // 하한 강제

        [Fact]
        public void BootstrapCandles_OhlcConsistent()
        {
            foreach (var c in GreenhouseActivator.BootstrapCandles())
            {
                Assert.True(c.High >= c.Low);
                Assert.True(c.High >= c.Close && c.Close >= c.Low);
                Assert.True(c.Volume > 0);
            }
        }

        // ── 결정론 진화 (동기·1회 사이클) ────────────────────────────────────────

        [Fact]
        public void DefaultSeedsAndCandles_ProduceChampion_ViaRunCycleOnce()
        {
            var gw = new PromotionGateway();
            var gh = new GreenhouseService(gw);
            gh.Prime(GreenhouseActivator.DefaultSeeds(), GreenhouseActivator.BootstrapCandles(), Market.KRX_KOSPI);

            gh.RunCycleOnce();

            var p = gh.Probe();
            Assert.True(p.Mutations > 0, "변이가 생성돼야 한다");
            Assert.NotNull(p.BestSpecId);          // 챔피언이 게이트웨이에 제출됨(적응도는 0일 수도 있음)
            Assert.True(p.Cycles > 0);
        }

        // ── 활성화 수명 주기 ─────────────────────────────────────────────────────

        [Fact]
        public void Activate_LightsLoop_ThenDeactivateStopsCleanly()
        {
            var gh = new GreenhouseService(new PromotionGateway());
            var act = new GreenhouseActivator(gh);

            Assert.True(act.Activate(Market.KRX_KOSPI));
            Assert.True(WaitFor(() => gh.Running && gh.Probe().Cycles > 0), "루프가 점등·구동돼야 한다");

            act.Deactivate();
            Assert.False(gh.Running);   // 정지·스레드 합류
        }

        [Fact]
        public void Activate_IsIdempotent()
        {
            var gh = new GreenhouseService(new PromotionGateway());
            var act = new GreenhouseActivator(gh);
            try
            {
                Assert.True(act.Activate(Market.KRX_KOSPI));
                Assert.False(act.Activate(Market.KRX_KOSPI));   // 이미 가동 중 → 무시
            }
            finally { act.Deactivate(); }
        }

        [Fact]
        public void Frozen_Attestation_HaltsCultivation_FailClosed()
        {
            // 비상 동결 시 배양 사이클이 아무 일도 하지 않아야(제안 0)
            var att = new OrderAttestationService();
            att.ForceFreeze("test");
            var gh = new GreenhouseService(new PromotionGateway(), att);
            gh.Prime(GreenhouseActivator.DefaultSeeds(), GreenhouseActivator.BootstrapCandles(), Market.KRX_KOSPI);

            gh.RunCycleOnce();

            var p = gh.Probe();
            Assert.Equal(0, p.Mutations);     // 동결 → 배양 정지
            Assert.Null(p.BestSpecId);
        }

        [Fact]
        public void NullGreenhouse_Throws()
            => Assert.Throws<ArgumentNullException>(() => new GreenhouseActivator(null!));
    }
}
