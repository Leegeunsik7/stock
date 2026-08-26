// ============================================================================
// StrategyForgeTests - 자가 전략 발견 (순수·결정론). 결정성·순위 정렬·캔들 가드.
// ============================================================================
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace StockManager.Tests
{
    public class StrategyForgeTests
    {
        // 결정론 합성 캔들(시드) — 구조가 있어 일부 조합에 교차/거래 발생
        private static List<Candle> Series(int seed, int n) => CartridgeEngine.ReferenceSeries(seed, n);

        [Fact]
        public void InsufficientCandles_IsInvalid()
        {
            var r = StrategyForge.Run(Series(1, 60));
            Assert.False(r.IsValid);
            Assert.Empty(r.Ranked);
        }

        [Fact]
        public void Deterministic_SameCandles_SameRanking()
        {
            var c = Series(42, 500);
            var a = StrategyForge.Run(c, folds: 4);
            var b = StrategyForge.Run(c, folds: 4);
            Assert.Equal(a.Tested, b.Tested);
            Assert.Equal(a.Ranked.Count, b.Ranked.Count);
            if (a.Ranked.Count > 0)
            {
                Assert.Equal(a.Ranked[0].Short, b.Ranked[0].Short);
                Assert.Equal(a.Ranked[0].Long, b.Ranked[0].Long);
                Assert.Equal(a.Ranked[0].Score, b.Ranked[0].Score, 10);
            }
        }

        [Fact]
        public void Ranked_SortedByScoreDescending_AndValidOnly()
        {
            var r = StrategyForge.Run(Series(7, 520), folds: 4);
            for (int i = 1; i < r.Ranked.Count; i++)
                Assert.True(r.Ranked[i - 1].Score >= r.Ranked[i].Score - 1e-9);
            Assert.All(r.Ranked, c => Assert.True(c.Valid && c.Short < c.Long));
        }

        [Fact]
        public void Tested_CountsValidGridCombos()
        {
            var r = StrategyForge.Run(Series(3, 400), folds: 3,
                shorts: new[] { 5, 10 }, longs: new[] { 20, 40 });
            Assert.Equal(4, r.Tested);   // 5/20, 5/40, 10/20, 10/40 (모두 short<long)
        }
    }
}
