// ============================================================================
// AsOfPortfolioTests - 그 시점 보유 재구성 (순수). 룩어헤드 차단·이동평균 규약 검증.
// ============================================================================
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace StockManager.Tests
{
    public class AsOfPortfolioTests
    {
        private static AsOfTx Buy(string s, int q, decimal p, string d) => new(s, TransactionType.Buy, q, p, DateTime.Parse(d));
        private static AsOfTx Sell(string s, int q, decimal p, string d) => new(s, TransactionType.Sell, q, p, DateTime.Parse(d));

        [Fact]
        public void MovingAverage_AccumulatesAcrossBuys()
        {
            var txs = new List<AsOfTx> { Buy("005930", 10, 100, "2024-01-02"), Buy("005930", 10, 200, "2024-01-10") };
            var h = AsOfPortfolio.Reconstruct(txs, DateTime.Parse("2024-02-01"));
            var s = Assert.Single(h);
            Assert.Equal(20, s.Quantity);
            Assert.Equal(150m, s.AvgCost);       // (10*100 + 10*200)/20
            Assert.Equal(3000m, s.CostBasis);
        }

        [Fact]
        public void PartialSell_KeepsAvg_ReducesCostProportionally()
        {
            var txs = new List<AsOfTx> { Buy("A", 20, 150, "2024-01-02"), Sell("A", 5, 999, "2024-01-20") };
            var s = Assert.Single(AsOfPortfolio.Reconstruct(txs, DateTime.Parse("2024-03-01")));
            Assert.Equal(15, s.Quantity);
            Assert.Equal(150m, s.AvgCost);        // 평단 불변
            Assert.Equal(2250m, s.CostBasis);     // 3000 * 15/20
        }

        [Fact]
        public void AsOf_IgnoresFutureTransactions()
        {
            var txs = new List<AsOfTx> { Buy("A", 10, 100, "2024-01-02"), Buy("A", 10, 300, "2025-06-01") };
            // 2024-12-31 시점 → 2025 매수는 보이지 않음
            var s = Assert.Single(AsOfPortfolio.Reconstruct(txs, DateTime.Parse("2024-12-31")));
            Assert.Equal(10, s.Quantity);
            Assert.Equal(100m, s.AvgCost);
        }

        [Fact]
        public void FullSell_RemovesPosition()
        {
            var txs = new List<AsOfTx> { Buy("A", 10, 100, "2024-01-02"), Sell("A", 10, 120, "2024-02-02") };
            Assert.Empty(AsOfPortfolio.Reconstruct(txs, DateTime.Parse("2024-03-01")));
        }

        [Fact]
        public void MultiSymbol_SortedAndIndependent()
        {
            var txs = new List<AsOfTx>
            {
                Buy("000660", 5, 100000, "2024-01-02"),
                Buy("005930", 10, 70000, "2024-01-03"),
                Sell("005930", 10, 80000, "2024-01-04"),   // 005930 전량매도 → 제외
            };
            var h = AsOfPortfolio.Reconstruct(txs, DateTime.Parse("2024-02-01"));
            var s = Assert.Single(h);
            Assert.Equal("000660", s.Symbol);
            Assert.Equal(5, s.Quantity);
        }

        [Fact]
        public void SellWithNoHolding_Ignored()
        {
            var txs = new List<AsOfTx> { Sell("A", 5, 100, "2024-01-02"), Buy("A", 3, 100, "2024-01-03") };
            var s = Assert.Single(AsOfPortfolio.Reconstruct(txs, DateTime.Parse("2024-02-01")));
            Assert.Equal(3, s.Quantity);   // 보유 없는 매도 무시 후 매수만 반영
        }
    }
}
