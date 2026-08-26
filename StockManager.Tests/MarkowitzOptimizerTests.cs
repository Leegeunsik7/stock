// ============================================================================
// MarkowitzOptimizerTests - MPT 평균-분산 최적화 불변식/경계 테스트 (순수·결정적)
// ============================================================================
using System;
using System.Linq;
using Xunit;

namespace StockManager.Tests
{
    public class MarkowitzOptimizerTests
    {
        // 결정적 의사난수 일수익률(%) — drift/vol/상관 제어
        private static double[] Series(int n, double drift, double vol, int seed)
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

        [Fact]
        public void Weights_SumToOne_NonNegative_RespectCap()
        {
            var R = new[] { Series(150, 0.05, 1.0, 1), Series(150, 0.03, 1.2, 2), Series(150, 0.04, 0.8, 3) };
            var res = MarkowitzOptimizer.Optimize(R, new[] { "A", "B", "C" }, targetVolAnnualPct: 18, maxWeight: 0.5);
            Assert.True(res.IsValid);
            Assert.Equal(1.0, res.Weights.Sum(), 3);
            Assert.All(res.Weights, w => Assert.True(w >= -1e-9 && w <= 0.5 + 1e-6));
        }

        [Fact]
        public void DominantAsset_GetsMoreWeight()
        {
            // A: 고수익·저변동, B: 저수익·고변동 → A 비중이 더 커야
            var R = new[] { Series(200, 0.10, 0.8, 10), Series(200, 0.00, 2.0, 20) };
            var res = MarkowitzOptimizer.Optimize(R, new[] { "A", "B" }, targetVolAnnualPct: null, maxWeight: 1.0);
            Assert.True(res.IsValid);
            Assert.True(res.Weights[0] > res.Weights[1]);
        }

        [Fact]
        public void TargetVolatility_RespectedWithinFrontier()
        {
            var R = new[] { Series(200, 0.06, 1.0, 4), Series(200, 0.04, 1.5, 5), Series(200, 0.05, 0.9, 6) };
            var res = MarkowitzOptimizer.Optimize(R, new[] { "A", "B", "C" }, targetVolAnnualPct: 15, maxWeight: 0.6);
            Assert.True(res.IsValid);
            // 프론티어에 목표 이하 점이 있으면 결과 변동성은 목표를 (수치오차 내) 넘지 않아야
            if (res.Frontier.Any(p => p.VolPct <= 15))
                Assert.True(res.VolPct <= 15 + 0.5);
        }

        [Fact]
        public void GmvHasLowestVol_OnFrontier()
        {
            var R = new[] { Series(200, 0.05, 1.0, 7), Series(200, 0.05, 1.0, 8), Series(200, 0.05, 1.0, 9) };
            var res = MarkowitzOptimizer.Optimize(R, new[] { "A", "B", "C" });
            Assert.True(res.IsValid);
            var minVol = res.Frontier.Min(p => p.VolPct);
            Assert.Equal(minVol, res.Frontier.First().VolPct, 6);   // 프론티어는 변동성 오름차순
        }

        [Fact]
        public void SingleAsset_FullWeight()
        {
            var res = MarkowitzOptimizer.Optimize(new[] { Series(100, 0.05, 1.0, 1) }, new[] { "A" });
            Assert.True(res.IsValid);
            Assert.Single(res.Weights);
            Assert.Equal(1.0, res.Weights[0], 6);
        }

        [Fact]
        public void InsufficientData_Invalid()
        {
            Assert.False(MarkowitzOptimizer.Optimize(Array.Empty<double[]>(), Array.Empty<string>()).IsValid);
            Assert.False(MarkowitzOptimizer.Optimize(new[] { new[] { 0.1 } }, new[] { "A" }).IsValid);  // t<2
        }

        [Fact]
        public void Invert_IdentityRoundTrip()
        {
            var m = new[] { new[] { 4.0, 2 }, new[] { 1.0, 3 } };
            var inv = MarkowitzOptimizer.Invert(m)!;
            Assert.NotNull(inv);
            // m·inv ≈ I
            double a = m[0][0] * inv[0][0] + m[0][1] * inv[1][0];
            double b = m[0][0] * inv[0][1] + m[0][1] * inv[1][1];
            Assert.Equal(1.0, a, 6);
            Assert.Equal(0.0, b, 6);
        }

        [Fact]
        public void Deterministic_SameInput_SameWeights()
        {
            var R = new[] { Series(180, 0.05, 1.0, 11), Series(180, 0.03, 1.1, 12), Series(180, 0.06, 1.3, 13) };
            var a = MarkowitzOptimizer.Optimize(R, new[] { "A", "B", "C" }, 16, 0.5);
            var b = MarkowitzOptimizer.Optimize(R, new[] { "A", "B", "C" }, 16, 0.5);
            Assert.Equal(a.Weights, b.Weights);
            Assert.Equal(a.VolPct, b.VolPct, 9);
        }
    }
}
