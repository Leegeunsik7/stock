// ============================================================================
// RiskAttributionTests - 리스크 기여도 분해/패리티 불변식·경계 (순수·결정적)
// ============================================================================
using System;
using System.Linq;
using StockManager.Common;
using Xunit;

namespace StockManager.Tests
{
    public class RiskAttributionTests
    {
        // 독립(무상관) 정규 수익률, vol 지정
        private static double[] Series(int n, double vol, int seed)
        {
            var r = new Random(seed);
            var a = new double[n];
            for (int i = 0; i < n; i++)
            {
                double u1 = 1 - r.NextDouble(), u2 = 1 - r.NextDouble();
                double z = Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2);
                a[i] = vol * z;
            }
            return a;
        }

        [Fact]
        public void RiskContributions_SumTo100()
        {
            var R = new[] { Series(250, 1.0, 1), Series(250, 1.5, 2), Series(250, 0.8, 3) };
            var rep = RiskAttribution.Analyze(R, new[] { "A", "B", "C" }, new[] { 0.4, 0.3, 0.3 });
            Assert.True(rep.IsValid);
            Assert.Equal(100.0, rep.Contributions.Sum(c => c.RiskContribPct), 1);
        }

        [Fact]
        public void EqualVol_ZeroCorr_EqualWeight_EqualRiskContrib()
        {
            // 동일 변동성·무상관·동일 비중 → 위험 비중도 균등(≈33%)
            var R = new[] { Series(400, 1.0, 11), Series(400, 1.0, 22), Series(400, 1.0, 33) };
            var rep = RiskAttribution.Analyze(R, new[] { "A", "B", "C" }, new[] { 1.0 / 3, 1.0 / 3, 1.0 / 3 });
            Assert.All(rep.Contributions, c => Assert.InRange(c.RiskContribPct, 28, 39));
        }

        [Fact]
        public void HighVolAsset_DominatesRisk_AtEqualWeight()
        {
            // 동일 비중인데 B가 고변동 → B의 위험 비중이 가장 큼
            var R = new[] { Series(300, 0.5, 5), Series(300, 3.0, 6), Series(300, 0.5, 7) };
            var rep = RiskAttribution.Analyze(R, new[] { "A", "B", "C" }, new[] { 1.0 / 3, 1.0 / 3, 1.0 / 3 });
            Assert.Equal("B", rep.Contributions.First().Name);
            Assert.True(rep.Contributions.First().RiskContribPct > 60);
            Assert.True(rep.Contributions.First().Ratio > 1.5);   // 비중보다 위험이 훨씬 큼
        }

        [Fact]
        public void RiskParity_Equalizes_RiskContributions()
        {
            var R = new[] { Series(400, 0.5, 1), Series(400, 2.0, 2), Series(400, 1.0, 3) };
            var rp = RiskAttribution.RiskParityWeights(R);
            Assert.Equal(1.0, rp.Sum(), 3);
            Assert.All(rp, w => Assert.True(w > 0));
            // 패리티 비중으로 분해하면 위험 비중이 거의 균등
            var rep = RiskAttribution.Analyze(R, new[] { "A", "B", "C" }, rp);
            var spread = rep.Contributions.Max(c => c.RiskContribPct) - rep.Contributions.Min(c => c.RiskContribPct);
            Assert.True(spread < 8, $"위험 비중 편차 {spread:0.0}%p가 너무 큼");
        }

        [Fact]
        public void RiskParity_UncorrelatedAssets_InverseVolWeighting()
        {
            // 무상관에서 패리티는 1/σ 비중 → 고변동 자산이 더 작은 비중
            var R = new[] { Series(500, 1.0, 41), Series(500, 2.0, 42) };
            var rp = RiskAttribution.RiskParityWeights(R);
            Assert.True(rp[0] > rp[1]);            // 저변동 A가 더 큰 비중
            Assert.InRange(rp[0], 0.6, 0.72);      // 대략 2/3 (σ비 1:2)
        }

        [Fact]
        public void InsufficientData_Invalid()
        {
            Assert.False(RiskAttribution.Analyze(Array.Empty<double[]>(), Array.Empty<string>(), Array.Empty<double>()).IsValid);
            Assert.False(RiskAttribution.Analyze(new[] { new[] { 0.1 } }, new[] { "A" }, new[] { 1.0 }).IsValid);
        }

        [Fact]
        public void Deterministic_SameInput_SameResult()
        {
            var R = new[] { Series(200, 1.0, 9), Series(200, 1.3, 8), Series(200, 0.7, 7) };
            var a = RiskAttribution.Analyze(R, new[] { "A", "B", "C" }, new[] { 0.5, 0.3, 0.2 });
            var b = RiskAttribution.Analyze(R, new[] { "A", "B", "C" }, new[] { 0.5, 0.3, 0.2 });
            Assert.Equal(a.PortfolioVolPct, b.PortfolioVolPct, 9);
            Assert.Equal(a.Contributions.First().RiskContribPct, b.Contributions.First().RiskContribPct, 9);
            Assert.Equal(a.RiskParityWeights, b.RiskParityWeights);
        }
    }
}
