// ============================================================================
// Phase94JPortfolioViewTests — v11.4.4 도메인→RCL 뷰 공변 구현 정확성 검증
//   ★잘못된 enum 캐스팅은 순수 UI 렌더를 조용히 오염시킨다(예: CRYPTO→다른 시장). 정수정렬 매핑을 명시 검증.
// ============================================================================
using MS2E.Infrastructure;
using MS2E.UI.Portable.Ports.Portfolio;
using Xunit;

namespace StockManager.Tests
{
    public sealed class Phase94JPortfolioViewTests
    {
        [Theory]
        [InlineData(Market.KRX_KOSPI, PfMarket.KRX_KOSPI)]
        [InlineData(Market.US_NASDAQ, PfMarket.US_NASDAQ)]
        [InlineData(Market.CRYPTO, PfMarket.CRYPTO)]
        public void Holding_Market_Maps_Covariantly(Market m, PfMarket expected)
        {
            IHoldingView v = new Holding { Symbol = "X", Market = m, Currency = Currency.USD, Quantity = 1m, AvgPrice = 10m };
            Assert.Equal(expected, v.Market);
            Assert.Equal(PfCurrency.USD, v.Currency);
        }

        [Fact]
        public void Holding_Scalar_Fields_Pass_Through()
        {
            IHoldingView v = new Holding { Id = 7, Symbol = "005930", Name = "삼성전자", Quantity = 3.5m, AvgPrice = 100m, CurrentPrice = 120m, BrokerAccount = "키움", Commission = 5m, Tax = 1m };
            Assert.Equal(7, v.Id);
            Assert.Equal("005930", v.Symbol);
            Assert.Equal(3.5m, v.Quantity);
            Assert.Equal(120m, v.CurrentPrice);
            Assert.Equal("키움", v.BrokerAccount);
        }

        [Fact]
        public void CategoryAgg_Class_And_Weight_Covariant()
        {
            ICategoryView c = new CategoryAgg(AssetClass.Crypto, "코인", ValueKrw: 500m, CostKrw: 400m, Count: 2);
            Assert.Equal(PfAssetClass.Crypto, c.Class);
            Assert.Equal(100m, c.PnlKrw);                 // 500-400
            Assert.Equal(50m, c.WeightPct(1000m));        // 500/1000*100
        }

        [Fact]
        public void AsOfHolding_Covariant()
        {
            IAsOfHoldingView a = new AsOfHolding("AAPL", 10m, 150m, 1500m);
            Assert.Equal("AAPL", a.Symbol);
            Assert.Equal(10m, a.Quantity);
            Assert.Equal(1500m, a.CostBasis);
        }

        [Fact]
        public void SymbolEntry_Covariant()
        {
            ISymbolEntryView s = new SymbolEntry("AAPL", "Apple", Market.US_NASDAQ, Currency.USD);
            Assert.Equal("AAPL", s.Code);
            Assert.Equal(PfMarket.US_NASDAQ, s.Market);
            Assert.Equal(PfCurrency.USD, s.Currency);
            Assert.False(s.IsCrypto);
        }

        [Fact]
        public void Quote_Covariant_As_QuoteTick()
        {
            IQuoteTickView q = new Quote { Price = 100m, PreviousClose = 90m, Session = "AFTER" };
            Assert.Equal(100m, q.Price);
            Assert.Equal("AFTER", q.Session);
        }
    }
}
