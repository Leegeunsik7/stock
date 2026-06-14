// ============================================================================
// PositionSizerTests - 켈리/변동성 타깃 사이징 (순수·결정적)
// ============================================================================
using System;
using System.Collections.Generic;
using System.Linq;
using StockManager.Common;
using Xunit;

namespace StockManager.Tests
{
    public class PositionSizerTests
    {
        [Fact]
        public void Kelly_KnownValue_HalfFraction()
        {
            // p=0.6, b=1 → f*=0.2, 분할 0.5 → 0.1
            Assert.Equal(0.1, PositionSizer.Kelly(0.6, 1.0, 0.5, 0.25), 6);
            // 전체 켈리
            Assert.Equal(0.2, PositionSizer.Kelly(0.6, 1.0, 1.0, 0.25), 6);
        }

        [Fact]
        public void Kelly_NoEdge_Zero()
        {
            Assert.Equal(0, PositionSizer.Kelly(0.4, 1.0), 9);     // f*=-0.2 → 0
            Assert.Equal(0, PositionSizer.Kelly(0.9, 0.0), 9);     // b=0 → 0
        }

        [Fact]
        public void Kelly_Capped()
        {
            // p=0.9,b=3 → f*=0.9-0.1/3≈0.867 → 전체켈리도 cap 0.25
            Assert.Equal(0.25, PositionSizer.Kelly(0.9, 3.0, 1.0, 0.25), 6);
        }

        [Fact]
        public void VolTarget_HalvesWhenDoubleVol()
        {
            Assert.Equal(0.5, PositionSizer.VolTarget(30, 15, 1.0), 6);
            Assert.Equal(1.0, PositionSizer.VolTarget(10, 15, 1.0), 6);   // 저변동 → cap
        }

        [Fact]
        public void EdgeFromReturns_Known()
        {
            var (p, b, exp) = PositionSizer.EdgeFromReturns(new[] { 10.0, -5, 10, -5 });
            Assert.Equal(0.5, p, 6);
            Assert.Equal(2.0, b, 6);       // avgWin 10 / avgLoss 5
            Assert.Equal(2.5, exp, 6);
        }

        [Fact]
        public void Suggest_RecommendsConservativeOfKellyAndVol()
        {
            // 강한 엣지(승률↑·손익비↑)지만 저변동 → 변동성타깃이 cap, 켈리가 더 작으면 켈리 선택
            var fwd = Enumerable.Repeat(8.0, 7).Concat(Enumerable.Repeat(-2.0, 3)).ToList(); // p=0.7,b=4
            var r = PositionSizer.Suggest(fwd, assetVolAnnualPct: 12, targetVolAnnualPct: 15, kellyFraction: 0.5, maxWeight: 0.5);
            Assert.True(r.IsValid);
            Assert.True(r.RecommendedWeight > 0);
            Assert.Equal(Math.Min(r.KellyWeight, r.VolWeight), r.RecommendedWeight, 9);
        }

        [Fact]
        public void Suggest_NegativeExpectancy_Hold()
        {
            var fwd = Enumerable.Repeat(-3.0, 8).Concat(Enumerable.Repeat(1.0, 2)).ToList();  // 기대값 음수
            var r = PositionSizer.Suggest(fwd, 25);
            Assert.Equal(0, r.RecommendedWeight, 9);
            Assert.Contains("보류", r.Verdict);
        }

        [Fact]
        public void Suggest_TooFewSamples_Invalid()
        {
            Assert.False(PositionSizer.Suggest(new[] { 1.0, 2 }, 20).IsValid);
        }

        [Fact]
        public void Deterministic_SameInput_SameResult()
        {
            var fwd = new[] { 5.0, -3, 7, -2, 4, -6, 8 };
            var a = PositionSizer.Suggest(fwd, 22, 15, 0.5, 0.25);
            var b = PositionSizer.Suggest(fwd, 22, 15, 0.5, 0.25);
            Assert.Equal(a.RecommendedWeight, b.RecommendedWeight, 9);
            Assert.Equal(a.KellyWeight, b.KellyWeight, 9);
        }
    }
}
