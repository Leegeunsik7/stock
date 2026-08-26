// ============================================================================
// ScenarioStudioTests - 시나리오 스트레스 테스트 베타/전파/경계 (순수·결정적)
// ============================================================================
using System;
using System.Linq;
using Xunit;

namespace StockManager.Tests
{
    public class ScenarioStudioTests
    {
        [Fact]
        public void Beta_SameSeries_IsOne_R2One()
        {
            var f = new[] { 1.0, 2, 3, 2, 4 };
            var (beta, r2) = ScenarioStudio.Beta((double[])f.Clone(), f);
            Assert.Equal(1.0, beta, 6);
            Assert.Equal(1.0, r2, 6);
        }

        [Fact]
        public void Beta_DoubleSeries_IsTwo()
        {
            var f = new[] { 1.0, 2, 3, 4 };
            var a = f.Select(x => 2 * x).ToArray();
            var (beta, r2) = ScenarioStudio.Beta(a, f);
            Assert.Equal(2.0, beta, 6);
            Assert.Equal(1.0, r2, 6);
        }

        [Fact]
        public void Beta_AntiCorrelated_Negative()
        {
            var f = new[] { 1.0, 2, 3 };
            var a = new[] { 3.0, 2, 1 };
            var (beta, _) = ScenarioStudio.Beta(a, f);
            Assert.Equal(-1.0, beta, 6);
        }

        [Fact]
        public void Beta_ZeroVarianceFactor_GuardsZero()
        {
            var (beta, r2) = ScenarioStudio.Beta(new[] { 1.0, 2, 3 }, new[] { 5.0, 5, 5 });
            Assert.Equal(0, beta, 9);
            Assert.Equal(0, r2, 9);
        }

        [Fact]
        public void BuildMarketFactor_WeightedComposite()
        {
            var assets = new[] { new[] { 1.0, 2, 3 }, new[] { 2.0, 4, 6 } };
            var f = ScenarioStudio.BuildMarketFactor(assets, new[] { 0.5, 0.5 });
            Assert.Equal(3, f.Length);
            Assert.Equal(1.5, f[0], 6);   // 0.5*1+0.5*2
            Assert.Equal(3.0, f[1], 6);   // 0.5*2+0.5*4
        }

        [Fact]
        public void ApplyShock_PropagatesByWeightedBeta()
        {
            // 명시 팩터로 베타 결정성 확보: A 베타1, B 베타2, 동일가중, 시장 -10%
            var factor = new[] { 1.0, 2, 3, 4 };
            var assets = new[] { (double[])factor.Clone(), factor.Select(x => 2 * x).ToArray() };
            var res = ScenarioStudio.ApplyShock(assets, new[] { "A", "B" }, new[] { 0.5, 0.5 }, -10.0, factor);
            Assert.True(res.IsValid);
            Assert.Equal(1.0, res.Assets[0].Beta, 6);
            Assert.Equal(2.0, res.Assets[1].Beta, 6);
            Assert.Equal(-10.0, res.Assets[0].PredictedMovePct, 6);
            Assert.Equal(-20.0, res.Assets[1].PredictedMovePct, 6);
            Assert.Equal(-15.0, res.PortfolioMovePct, 6);   // 0.5*-10 + 0.5*-20
        }

        [Fact]
        public void ApplyShock_LinearInShock()
        {
            var factor = new[] { 1.0, -2, 3, -1, 2 };
            var assets = new[] { factor.Select(x => 1.5 * x).ToArray(), (double[])factor.Clone() };
            var r1 = ScenarioStudio.ApplyShock(assets, new[] { "A", "B" }, new[] { 0.6, 0.4 }, 1.0, factor);
            var r10 = ScenarioStudio.ApplyShock(assets, new[] { "A", "B" }, new[] { 0.6, 0.4 }, 10.0, factor);
            Assert.Equal(r1.PortfolioMovePct * 10, r10.PortfolioMovePct, 6);   // 충격에 선형
        }

        [Fact]
        public void InsufficientData_Invalid()
        {
            Assert.False(ScenarioStudio.ApplyShock(Array.Empty<double[]>(), Array.Empty<string>(), Array.Empty<double>(), -10).IsValid);
            Assert.False(ScenarioStudio.ApplyShock(new[] { new[] { 0.1 } }, new[] { "A" }, new[] { 1.0 }, -10).IsValid);
        }

        [Fact]
        public void Deterministic_SameInput_SameResult()
        {
            var assets = new[] { new[] { 1.0, -1, 2, -2, 1 }, new[] { 0.5, -0.5, 1, -1, 0.5 } };
            var a = ScenarioStudio.ApplyShock(assets, new[] { "A", "B" }, new[] { 0.5, 0.5 }, -12.0);
            var b = ScenarioStudio.ApplyShock(assets, new[] { "A", "B" }, new[] { 0.5, 0.5 }, -12.0);
            Assert.Equal(a.PortfolioMovePct, b.PortfolioMovePct, 9);
            Assert.Equal(a.Assets[0].Beta, b.Assets[0].Beta, 9);
        }
    }
}
