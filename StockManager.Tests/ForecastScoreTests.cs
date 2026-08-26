// ============================================================================
// ForecastScoreTests - 예측 보정도 채점/백테스트 (순수·결정적)
// ============================================================================
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace StockManager.Tests
{
    public class ForecastScoreTests
    {
        private static ResolvedForecast F(double pred, double lo, double hi, double realized, double nom = 0.9, int h = 10)
            => new() { Kind = "StatBand", HorizonDays = h, PredictedPct = pred, LowPct = lo, HighPct = hi, NominalCoverage = nom, RealizedPct = realized };

        [Fact]
        public void Coverage_AllInside_Is100()
        {
            var fs = Enumerable.Range(0, 20).Select(i => F(0, -10, 10, (i % 5) - 2)).ToList();  // 실현 -2..2 ∈ [-10,10]
            var rep = ForecastScore.Score(fs);
            Assert.True(rep.IsValid);
            Assert.Equal(100.0, rep.Scores[0].CoveragePct, 1);
        }

        [Fact]
        public void Coverage_HalfOutside_Is50()
        {
            var fs = new List<ResolvedForecast>();
            for (int i = 0; i < 10; i++) fs.Add(F(0, -5, 5, 1));     // 안
            for (int i = 0; i < 10; i++) fs.Add(F(0, -5, 5, 99));    // 밖
            var rep = ForecastScore.Score(fs);
            Assert.Equal(50.0, rep.Scores[0].CoveragePct, 1);
        }

        [Fact]
        public void Bias_And_Mae_Computed()
        {
            var fs = Enumerable.Range(0, 12).Select(_ => F(1, -10, 10, 3)).ToList();   // 실현-예측 = +2 일정
            var s = ForecastScore.Score(fs).Scores[0];
            Assert.Equal(2.0, s.BiasPct, 6);
            Assert.Equal(2.0, s.MaePct, 6);
            Assert.Contains("낙관", s.Verdict);   // bias>0.7
        }

        [Fact]
        public void DirectionalHit_AndLowBreach()
        {
            var fs = new List<ResolvedForecast>
            {
                F(1, -5, 5, 2),    // pred + , realized + → 적중, 안
                F(1, -5, 5, -1),   // pred + , realized - → 불일치, 안
                F(-1, -5, 5, -2),  // pred - , realized - → 적중, 안
                F(1, -5, 5, -9),   // 하단(-5) 위반
            };
            var s = ForecastScore.Score(fs).Scores[0];
            Assert.Equal(50.0, s.DirHitPct, 1);     // 2/4
            Assert.Equal(25.0, s.LowBreachPct, 1);  // 1/4 < -5
        }

        [Fact]
        public void GroupsByKindAndHorizon()
        {
            var fs = new List<ResolvedForecast>
            {
                F(0,-5,5,1,0.9,5), F(0,-5,5,1,0.9,5),
                F(0,-5,5,1,0.9,21),
            };
            var rep = ForecastScore.Score(fs);
            Assert.Equal(2, rep.Scores.Count);              // (StatBand,5) · (StatBand,21)
            Assert.Equal(2, rep.Scores.First(x => x.HorizonDays == 5).N);
        }

        [Fact]
        public void Empty_Invalid()
        {
            Assert.False(ForecastScore.Score(Enumerable.Empty<ResolvedForecast>()).IsValid);
        }

        [Fact]
        public void CalibrationBacktest_WiderBand_HigherCoverage()
        {
            var rng = new Random(7);
            var closes = new List<decimal>(); decimal p = 100;
            for (int i = 0; i < 400; i++) { p *= (decimal)(1 + (rng.NextDouble() - 0.5) * 0.04); closes.Add(p); }
            var narrow = ForecastScore.Score(ForecastScore.CalibrationBacktest(closes, 10, 60, 1.0, 0.68));
            var wide = ForecastScore.Score(ForecastScore.CalibrationBacktest(closes, 10, 60, 2.5, 0.99));
            Assert.True(narrow.IsValid && wide.IsValid);
            Assert.True(wide.Scores[0].CoveragePct >= narrow.Scores[0].CoveragePct);   // 넓은 구간 → 커버리지↑
            Assert.InRange(narrow.Scores[0].CoveragePct, 0, 100);
        }

        [Fact]
        public void CalibrationBacktest_TooShort_Empty()
        {
            var closes = Enumerable.Range(0, 30).Select(i => (decimal)(100 + i)).ToList();
            Assert.Empty(ForecastScore.CalibrationBacktest(closes, 10, 60, 1.645, 0.9));   // lookback+horizon+1 미만
        }

        [Fact]
        public void CalibrationBacktest_Deterministic()
        {
            var closes = Enumerable.Range(0, 200).Select(i => (decimal)(100 + 5 * Math.Sin(i / 6.0))).ToList();
            var a = ForecastScore.Score(ForecastScore.CalibrationBacktest(closes, 5, 60, 1.645, 0.9));
            var b = ForecastScore.Score(ForecastScore.CalibrationBacktest(closes, 5, 60, 1.645, 0.9));
            Assert.Equal(a.Scores[0].CoveragePct, b.Scores[0].CoveragePct, 9);
            Assert.Equal(a.Scores[0].BiasPct, b.Scores[0].BiasPct, 9);
        }
    }
}
