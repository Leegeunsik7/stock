// ============================================================================
// RegimeDetectorTests - 레짐 이상탐지 통계 모듈 테스트 (DB 불필요·순수·결정적)
// ============================================================================
using System;
using System.Collections.Generic;
using System.Linq;
using StockManager.Common;
using Xunit;

namespace StockManager.Tests
{
    public class RegimeDetectorTests
    {
        // 수익률(%) 배열 → 종가 시계열(시작 100)
        private static List<decimal> Closes(IEnumerable<double> retsPct)
        {
            var c = new List<decimal> { 100m };
            decimal p = 100m;
            foreach (var r in retsPct) { p *= (decimal)(1 + r / 100.0); c.Add(p); }
            return c;
        }

        // 결정적 ±진동(분산 확보), seed로 부호 패턴 고정
        private static double[] Wobble(int n, double amp, int seed)
        {
            var a = new double[n];
            int s = seed;
            for (int i = 0; i < n; i++) { s = (s * 1103515245 + 12345) & 0x7fffffff; a[i] = ((s % 1000) / 1000.0 - 0.5) * 2 * amp; }
            return a;
        }

        [Fact]
        public void ToReturns_ComputesSimplePct()
        {
            var r = RegimeDetector.ToReturns(new List<decimal> { 100m, 110m, 99m });
            Assert.Equal(10.0, r[0], 6);
            Assert.Equal(-10.0, r[1], 6);
        }

        [Fact]
        public void CalmSeries_NoAnomaly()
        {
            var sig = RegimeDetector.Analyze(Closes(Wobble(60, 0.3, 7)));
            Assert.False(sig.IsAnomaly);
            Assert.Equal(RegimeKind.None, sig.Kind);
        }

        [Fact]
        public void ReturnOutlier_DetectedAndDescribed()
        {
            var rets = Wobble(40, 0.3, 11).ToList();
            rets.Add(8.0);   // 마지막에 +8% — 평소(±0.3%)의 수십 σ
            var sig = RegimeDetector.Analyze(Closes(rets));
            Assert.True(sig.IsAnomaly);
            Assert.Equal(RegimeKind.ReturnOutlier, sig.Kind);
            Assert.True(Math.Abs(sig.ZScore) >= 3.0);
            Assert.Contains("이상치", sig.Describe());
        }

        [Fact]
        public void LatestZScore_HighWhenLatestFarFromWindow()
        {
            var rets = Wobble(30, 0.2, 3).ToList();
            rets.Add(5.0);
            var z = RegimeDetector.LatestZScore(rets.ToArray(), 20);
            Assert.True(Math.Abs(z) > 5);
        }

        [Fact]
        public void EwmaVolRatio_LargeOnSpike()
        {
            var rets = Wobble(40, 0.25, 5).ToList();
            rets.Add(6.0);
            var ratio = RegimeDetector.EwmaVolRatio(rets.ToArray(), 0.94);
            Assert.True(ratio > 3);
        }

        [Fact]
        public void Cusum_DetectsUpwardMeanShift()
        {
            // 전반 0 → 후반 +3 의 평균 이동: 최종 sHi가 h 초과, sLo는 0
            var rets = Enumerable.Repeat(0.0, 20).Concat(Enumerable.Repeat(3.0, 20)).ToArray();
            var (up, down, sHi, sLo) = RegimeDetector.Cusum(rets, 0.5, 5.0);
            Assert.True(up);
            Assert.False(down);
            Assert.True(sHi > 5 && sLo <= 5);
        }

        [Fact]
        public void AvgPairwiseCorrelation_BoundsAndSign()
        {
            var a = new[] { 1.0, 2, 3, 4, 5 };
            var same = RegimeDetector.AvgPairwiseCorrelation(new[] { a, (double[])a.Clone() });
            Assert.True(same > 0.99);                          // 동일 → +1
            var opp = RegimeDetector.AvgPairwiseCorrelation(new[] { a, new[] { 5.0, 4, 3, 2, 1 } });
            Assert.True(opp < -0.99);                          // 반대 → -1
            Assert.Equal(0, RegimeDetector.AvgPairwiseCorrelation(new[] { a }));   // 1자산 → 0
        }

        [Fact]
        public void InsufficientData_NoAnomaly_NoThrow()
        {
            Assert.False(RegimeDetector.Analyze(Closes(new[] { 0.1, 0.2, 0.1 })).IsAnomaly);
            Assert.Empty(RegimeDetector.ToReturns(new List<decimal> { 100m }));
        }

        [Fact]
        public void Deterministic_SameInput_SameSignal()
        {
            var c = Closes(Wobble(50, 0.4, 9).Append(7.0));
            var s1 = RegimeDetector.Analyze(c);
            var s2 = RegimeDetector.Analyze(c);
            Assert.Equal(s1.Kind, s2.Kind);
            Assert.Equal(s1.ZScore, s2.ZScore, 9);
            Assert.Equal(s1.VolRatio, s2.VolRatio, 9);
        }
    }
}
