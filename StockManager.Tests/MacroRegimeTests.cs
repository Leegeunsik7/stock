// ============================================================================
// MacroRegimeTests — S20 #2: 거시 매크로 레짐 융합 검증 (17개)
//
//  §1 StressScore (5)   — 평온·지수위기·환율위기·금리역전·최악주도(max)
//  §2 Classify (4)      — Calm/Elevated/Turbulent/Crisis 밴드
//  §3 Modulate (5)      — 평온 불변·위기 수축·단조·타 상수 보존·클램프
//  §4 Fuse·통합 (3)     — 일괄 산출·온실 위기 시 적응도 하락·피드 실패 그레이스풀
// ============================================================================
using System;
using System.Collections.Generic;
using StockManager.Common;
using Xunit;

namespace StockManager.Tests
{
    public sealed class MacroRegimeTests
    {
        private static MacroSnapshot Calm    => new(15.0, 5.0, 100.0);
        private static MacroSnapshot CrisisX => new(40.0, 5.0, 100.0);  // 지수 변동성만 위기
        private static MacroSnapshot CrisisAll => new(40.0, 20.0, -20.0);

        // ════════════════════════ §1 StressScore ══════════════════════════════════

        [Fact]
        public void Stress_CalmInputs_NearZero()
            => Assert.Equal(0.0, MacroRegimeFusion.StressScore(Calm), 6);

        [Fact]
        public void Stress_CrisisIndexVol_Saturates()
            => Assert.Equal(1.0, MacroRegimeFusion.StressScore(new MacroSnapshot(40.0, 5.0, 100.0)), 6);

        [Fact]
        public void Stress_CrisisFxVol_Saturates()
            => Assert.Equal(1.0, MacroRegimeFusion.StressScore(new MacroSnapshot(15.0, 20.0, 100.0)), 6);

        [Fact]
        public void Stress_InvertedRateSpread_Saturates()
            => Assert.Equal(1.0, MacroRegimeFusion.StressScore(new MacroSnapshot(15.0, 5.0, -20.0)), 6);

        [Fact]
        public void Stress_MaxDriven_WorstIndicatorLeads()
        {
            // 지수만 위기, 나머지 평온 → 최악(지수)이 스트레스 주도
            double s = MacroRegimeFusion.StressScore(CrisisX);
            Assert.Equal(1.0, s, 6);
            // 부분 스트레스: 지수가 calm~crisis 중간(27.5)이면 ~0.5
            double mid = MacroRegimeFusion.StressScore(new MacroSnapshot(27.5, 5.0, 100.0));
            Assert.Equal(0.5, mid, 2);
        }

        // ════════════════════════ §2 Classify ═════════════════════════════════════

        [Fact]
        public void Classify_Calm()      => Assert.Equal(MacroRegime.Calm,      MacroRegimeFusion.Classify(0.10));
        [Fact]
        public void Classify_Elevated()  => Assert.Equal(MacroRegime.Elevated,  MacroRegimeFusion.Classify(0.30));
        [Fact]
        public void Classify_Turbulent() => Assert.Equal(MacroRegime.Turbulent, MacroRegimeFusion.Classify(0.60));
        [Fact]
        public void Classify_Crisis()    => Assert.Equal(MacroRegime.Crisis,    MacroRegimeFusion.Classify(0.90));

        // ════════════════════════ §3 Modulate ═════════════════════════════════════

        [Fact]
        public void Modulate_CalmStress_BoundsUnchanged()
        {
            var b = FitnessBounds.Default;
            var m = MacroRegimeFusion.Modulate(b, stress: 0.0);
            Assert.Equal(b.MddTolPct, m.MddTolPct, 9);
            Assert.Equal(b.ZTol,      m.ZTol, 9);
        }

        [Fact]
        public void Modulate_CrisisStress_ShrinksToMinMult()
        {
            var b  = FitnessBounds.Default;                       // MddTolPct=40, ZTol=2
            var mb = MacroRegimeBounds.Default;                   // MinToleranceMult=0.25
            var m  = MacroRegimeFusion.Modulate(b, stress: 1.0, mb);
            Assert.Equal(b.MddTolPct * mb.MinToleranceMult, m.MddTolPct, 9);  // 40→10
            Assert.Equal(b.ZTol      * mb.MinToleranceMult, m.ZTol, 9);       // 2→0.5
        }

        [Fact]
        public void Modulate_Monotonic_HigherStressTighter()
        {
            var b = FitnessBounds.Default;
            var lo = MacroRegimeFusion.Modulate(b, 0.3);
            var hi = MacroRegimeFusion.Modulate(b, 0.8);
            Assert.True(hi.MddTolPct < lo.MddTolPct);
            Assert.True(hi.ZTol      < lo.ZTol);
        }

        [Fact]
        public void Modulate_PreservesOtherBounds()
        {
            var b = FitnessBounds.Default with { SharpeRef = 3.0, WinRateFloorPct = 45.0, TradeFloor = 7 };
            var m = MacroRegimeFusion.Modulate(b, stress: 1.0);
            Assert.Equal(3.0,  m.SharpeRef, 9);
            Assert.Equal(45.0, m.WinRateFloorPct, 9);
            Assert.Equal(7,    m.TradeFloor);
            Assert.Equal(b.WP, m.WP, 9);
        }

        [Fact]
        public void Modulate_ClampsStressRange()
        {
            var b = FitnessBounds.Default;
            var over  = MacroRegimeFusion.Modulate(b, stress: 5.0);    // >1 클램프
            var under = MacroRegimeFusion.Modulate(b, stress: -3.0);   // <0 클램프
            Assert.Equal(b.MddTolPct * MacroRegimeBounds.Default.MinToleranceMult, over.MddTolPct, 9);
            Assert.Equal(b.MddTolPct, under.MddTolPct, 9);
        }

        // ════════════════════════ §4 Fuse·통합 ════════════════════════════════════

        [Fact]
        public void Fuse_ReturnsConsistentTriple()
        {
            var (regime, stress, bounds) = MacroRegimeFusion.Fuse(CrisisAll, FitnessBounds.Default);
            Assert.Equal(MacroRegime.Crisis, regime);
            Assert.Equal(1.0, stress, 6);
            Assert.True(bounds.MddTolPct < FitnessBounds.Default.MddTolPct);
        }

        [Fact]
        public void Greenhouse_CrisisRegime_LowersChampionFitness_VsCalm()
        {
            var bounds = new FitnessBounds { TradeFloor = 1 };   // 거래 챔피언이 게이트로 0 안 되게

            var ghCalm = new GreenhouseService(new PromotionGateway(), fitBounds: bounds)
            { MacroFeed = () => Calm };
            ghCalm.Prime(Seeds(), Candles(), Market.KRX_KOSPI);
            ghCalm.RunCycleOnce();
            double fCalm = ghCalm.Probe().BestFitness;
            Assert.True(fCalm > 0, $"평온 챔피언 적응도 양수여야: {fCalm}");
            Assert.Equal(MacroRegime.Calm, ghCalm.Probe().Regime);

            var ghCrisis = new GreenhouseService(new PromotionGateway(), fitBounds: bounds)
            { MacroFeed = () => CrisisAll };
            ghCrisis.Prime(Seeds(), Candles(), Market.KRX_KOSPI);
            ghCrisis.RunCycleOnce();
            double fCrisis = ghCrisis.Probe().BestFitness;

            Assert.Equal(MacroRegime.Crisis, ghCrisis.Probe().Regime);
            Assert.True(fCrisis < fCalm, $"위기 레짐이 적응도를 깎아야: fCrisis={fCrisis} fCalm={fCalm}");
        }

        [Fact]
        public void Greenhouse_MacroFeedThrows_GracefulFallback_NoCrash()
        {
            var gw = new PromotionGateway();
            var gh = new GreenhouseService(gw) { MacroFeed = () => throw new InvalidOperationException("피드 장애") };
            gh.Prime(Seeds(), Candles(), Market.KRX_KOSPI);

            var ex = Record.Exception(() => gh.RunCycleOnce());
            Assert.Null(ex);                                   // 피드 장애가 배양을 깨지 않음
            Assert.Equal(PromotionState.PendingApproval, gw.State);   // 배양은 정상 진행
            Assert.Equal(MacroRegime.Calm, gh.Probe().Regime);        // 비변조 폴백
        }

        // ── 빌더 (LiveBindingTests 와 동일한 진동파 — 다중 교차·거래 보장) ──────────
        private static List<StrategySpec> Seeds() => new()
        {
            new StrategySpec("seed", "씨앗",
                new List<SpecCond> { new SpecCond(new SpecTerm(SpecKind.Sma, 5), SpecOp.CrossAbove, false, 0, new SpecTerm(SpecKind.Sma, 20)) },
                SpecLogic.And,
                new List<SpecCond> { new SpecCond(new SpecTerm(SpecKind.Sma, 5), SpecOp.CrossBelow, false, 0, new SpecTerm(SpecKind.Sma, 20)) },
                SpecLogic.And, 5, 10),
        };

        private static List<Candle> Candles(int n = 80)
        {
            var list = new List<Candle>(n);
            var start = new DateTime(2026, 1, 1);
            for (int i = 0; i < n; i++)
            {
                decimal close = 11000m + (decimal)(Math.Sin(i * 0.45) * 2000.0);
                list.Add(new Candle
                {
                    Date = start.AddDays(i),
                    Open = close, High = close * 1.01m, Low = close * 0.99m,
                    Close = close, Volume = 1000,
                });
            }
            return list;
        }
    }
}
