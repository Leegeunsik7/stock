// ============================================================================
// PortfolioBreakdownTests — 자산군 3분류(국내/해외/코인) 집계.
//  고정 룰: 시장→분류 매핑 · USD만 환율 환산 · 항상 3분류 순서 반환 · 비중/손익/수익률 산식.
// ============================================================================
using System;
using System.Collections.Generic;
using StockManager.Common;
using Xunit;

namespace StockManager.Tests
{
    public class PortfolioBreakdownTests
    {
        [Fact]
        public void Classify_MapsMarketsToThreeClasses()
        {
            Assert.Equal(AssetClass.DomesticStock, PortfolioBreakdown.Classify(Market.KRX_KOSPI));
            Assert.Equal(AssetClass.DomesticStock, PortfolioBreakdown.Classify(Market.KRX_KOSDAQ));
            Assert.Equal(AssetClass.ForeignStock,  PortfolioBreakdown.Classify(Market.US_NYSE));
            Assert.Equal(AssetClass.ForeignStock,  PortfolioBreakdown.Classify(Market.US_NASDAQ));
            Assert.Equal(AssetClass.ForeignStock,  PortfolioBreakdown.Classify(Market.US_AMEX));
            Assert.Equal(AssetClass.Crypto,        PortfolioBreakdown.Classify(Market.CRYPTO));
        }

        [Fact]
        public void Classify_EveryMarketEnumValue_IsExplicitlyMapped()
        {
            // fail-closed 강제: 새 Market 값 추가 시 이 기대표(와 Classify)를 함께 갱신하지 않으면 실패한다.
            //   (Classify의 discard 폴백이 컴파일 경고를 숨기므로, 누락은 컴파일러 대신 이 테스트가 잡는다.)
            var expected = new Dictionary<Market, AssetClass>
            {
                [Market.KRX_KOSPI]  = AssetClass.DomesticStock,
                [Market.KRX_KOSDAQ] = AssetClass.DomesticStock,
                [Market.US_NYSE]    = AssetClass.ForeignStock,
                [Market.US_NASDAQ]  = AssetClass.ForeignStock,
                [Market.US_AMEX]    = AssetClass.ForeignStock,
                [Market.CRYPTO]     = AssetClass.Crypto,
            };
            foreach (Market m in Enum.GetValues<Market>())
            {
                Assert.True(expected.ContainsKey(m), $"새 Market 값 '{m}'이 분류 기대표에 없음 — PortfolioBreakdown.Classify와 이 테스트에 명시 추가 필요");
                Assert.Equal(expected[m], PortfolioBreakdown.Classify(m));
            }
        }

        [Fact]
        public void Compute_AggregatesPerClass_WithUsdConversion_AndWeights()
        {
            var hs = new List<Holding>
            {
                new() { Symbol = "005930",  Market = Market.KRX_KOSPI,  Currency = Currency.KRW, Quantity = 10,   AvgPrice = 70_000m,     CurrentPrice = 75_000m },
                new() { Symbol = "AAPL",    Market = Market.US_NASDAQ,  Currency = Currency.USD, Quantity = 2,    AvgPrice = 100m,        CurrentPrice = 110m },
                new() { Symbol = "KRW-BTC", Market = Market.CRYPTO,     Currency = Currency.KRW, Quantity = 0.1m, AvgPrice = 90_000_000m, CurrentPrice = 100_000_000m },
            };
            var cats = PortfolioBreakdown.Compute(hs, 1300m);

            Assert.Equal(3, cats.Count);
            Assert.Equal(AssetClass.DomesticStock, cats[0].Class);   // 항상 국내·해외·코인 순서
            Assert.Equal(AssetClass.ForeignStock,  cats[1].Class);
            Assert.Equal(AssetClass.Crypto,        cats[2].Class);

            // 국내: KRW 그대로
            Assert.Equal(750_000m, cats[0].ValueKrw);
            Assert.Equal(700_000m, cats[0].CostKrw);
            Assert.Equal(50_000m,  cats[0].PnlKrw);

            // 해외: USD ×환율 1300
            Assert.Equal(286_000m, cats[1].ValueKrw);   // 2×110×1300
            Assert.Equal(260_000m, cats[1].CostKrw);    // 2×100×1300
            Assert.Equal(10m,      cats[1].ReturnPct);  // 26000/260000×100

            // 코인: KRW 그대로(환율 적용 안 함)
            Assert.Equal(10_000_000m, cats[2].ValueKrw);
            Assert.Equal(1, cats[2].Count);

            // 불변식 — 동어반복(공식 재계산) 대신 하드 리터럴/합으로 고정.
            decimal total = 11_036_000m;   // 750,000 + 286,000 + 10,000,000
            Assert.Equal(total, cats[0].ValueKrw + cats[1].ValueKrw + cats[2].ValueKrw);   // 버킷 합 = 총평가
            Assert.Equal(90.61m, cats[2].WeightPct(total), 2);   // 10,000,000/11,036,000×100 = 90.612…
            Assert.Equal(100m,
                cats[0].WeightPct(total) + cats[1].WeightPct(total) + cats[2].WeightPct(total), 6);   // 비중 합 = 100
        }

        [Fact]
        public void Compute_Empty_ReturnsThreeZeroCards_NoDivideByZero()
        {
            var cats = PortfolioBreakdown.Compute(new List<Holding>(), 1300m);
            Assert.Equal(3, cats.Count);
            Assert.All(cats, c =>
            {
                Assert.Equal(0m, c.ValueKrw);
                Assert.Equal(0,  c.Count);
                Assert.Equal(0m, c.ReturnPct);        // cost 0 → 0 (0으로 나누기 없음)
                Assert.Equal(0m, c.WeightPct(0m));    // total 0 → 0
            });
        }
    }
}
