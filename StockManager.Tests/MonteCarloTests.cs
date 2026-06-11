// ============================================================================
// MonteCarloTests - 포트폴리오 몬테카를로 불변식/결정론 테스트
//  난수 시드 고정으로 재현 가능. 분포·VaR·확률의 구조 불변식 검증.
// ============================================================================
using System;
using System.Linq;
using StockManager.Common;
using Xunit;

namespace StockManager.Tests
{
    public class MonteCarloTests
    {
        // 일정한(분산 0) 양(+)의 일수익률 → 미래수익은 결정적(분포 폭 0), 손실확률 0
        [Fact]
        public void ZeroVariance_PositiveDrift_NoSpread_NoLoss()
        {
            var ret = Enumerable.Repeat(0.001, 100).ToArray();   // 매일 +0.1%
            var r = MonteCarlo.Simulate(new[] { ret }, new[] { 1.0 }, horizonDays: 20, sims: 1000);
            Assert.Equal(r.P5, r.P95, 3);            // 분산 0 → 백분위 동일
            Assert.True(r.ExpReturnPct > 0);         // 양의 드리프트
            Assert.Equal(0, r.ProbLossPct, 3);       // 손실 없음
            Assert.Equal(0, r.Var95Pct, 3);
        }

        [Fact]
        public void Deterministic_SameSeed_SameResult()
        {
            var a = RandomWalk(120, 0.0003, 0.02, 7);
            var r1 = MonteCarlo.Simulate(new[] { a }, new[] { 1.0 }, 21, 2000, seed: 999);
            var r2 = MonteCarlo.Simulate(new[] { a }, new[] { 1.0 }, 21, 2000, seed: 999);
            Assert.Equal(r1.P50, r2.P50, 10);        // 동일 시드 → 완전 동일
            Assert.Equal(r1.Var95Pct, r2.Var95Pct, 10);
        }

        [Fact]
        public void Bounds_AndOrdering()
        {
            var a = RandomWalk(120, 0.0005, 0.03, 1);
            var b = RandomWalk(120, -0.0002, 0.02, 2);
            var r = MonteCarlo.Simulate(new[] { a, b }, new[] { 0.6, 0.4 }, 21, 3000, seed: 42);
            Assert.True(r.P5 <= r.P50 && r.P50 <= r.P95);            // 백분위 순서
            Assert.InRange(r.ProbLossPct, 0, 100);
            Assert.Equal(40, r.Histogram.Length);
            Assert.Equal(r.Sims, r.Histogram.Sum());                 // 히스토그램 합 = 시뮬 수
            Assert.True(r.Var95Pct >= 0);
        }

        [Fact]
        public void EmptyOrTooShort_ReturnsZeroSims()
        {
            Assert.Equal(0, MonteCarlo.Simulate(Array.Empty<double[]>(), Array.Empty<double>(), 21, 1000).Sims);
            Assert.Equal(0, MonteCarlo.Simulate(new[] { new[] { 0.01 } }, new[] { 1.0 }, 21, 1000).Sims);  // len<2
        }

        [Fact]
        public void HigherVolatility_WidensVaR()
        {
            var lo = RandomWalk(150, 0.0, 0.01, 5);
            var hi = RandomWalk(150, 0.0, 0.05, 5);
            var rLo = MonteCarlo.Simulate(new[] { lo }, new[] { 1.0 }, 21, 4000, seed: 7);
            var rHi = MonteCarlo.Simulate(new[] { hi }, new[] { 1.0 }, 21, 4000, seed: 7);
            Assert.True(rHi.Var95Pct > rLo.Var95Pct);   // 변동성↑ → VaR↑
        }

        // 결정적 의사난수 수익률 시계열(테스트 입력 생성용)
        private static double[] RandomWalk(int n, double drift, double vol, int seed)
        {
            var r = new Random(seed);
            var a = new double[n];
            for (int i = 0; i < n; i++)
            {
                double u1 = 1 - r.NextDouble(), u2 = 1 - r.NextDouble();
                double z = Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2);
                a[i] = drift + vol * z;
            }
            return a;
        }
    }
}
