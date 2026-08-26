// ============================================================================
// BacktestEngineTests - 백테스트 엔진 불변식/경계 회귀 테스트
//  결정적 입력(합성 캔들)으로 구조 불변식 검증: EquityCurve 길이, MDD/승률 범위,
//  플랫 시리즈 무거래·무수익, 5개 전략 전부 예외 없이 실행.
// ============================================================================
using System;
using System.Collections.Generic;
using Xunit;

namespace StockManager.Tests
{
    public class BacktestEngineTests
    {
        private static List<Candle> Flat(decimal price, int n)
        {
            var list = new List<Candle>();
            var d = new DateTime(2025, 1, 1);
            for (int i = 0; i < n; i++)
                list.Add(new Candle { Date = d.AddDays(i), Open = price, High = price, Low = price, Close = price, Volume = 1000 });
            return list;
        }

        private static List<Candle> Wave(int n)
        {
            var list = new List<Candle>();
            var d = new DateTime(2025, 1, 1);
            for (int i = 0; i < n; i++)
            {
                var p = 1000m + (decimal)(Math.Sin(i * 0.3) * 120);   // 진동 → MA 교차/돌파 발생
                list.Add(new Candle { Date = d.AddDays(i), Open = p, High = p + 5, Low = p - 5, Close = p, Volume = 1000 + i * 10 });
            }
            return list;
        }

        [Fact]
        public void StrategyNames_HasFive() => Assert.Equal(5, BacktestEngine.StrategyNames.Length);

        [Fact]
        public void ShortSeries_NoTrades_EmptyCurve()
        {
            var r = BacktestEngine.Run(Flat(1000, 10), 0, Market.KRX_KOSPI);   // 30개 미만 → 루프 미진입
            Assert.Equal(0, r.TradeCount);
            Assert.Empty(r.EquityCurve);
            Assert.Equal(0m, r.TotalReturn);
            Assert.Equal(0m, r.MaxDrawdown);
        }

        [Fact]
        public void FlatSeries_NoTrades_NoReturn()
        {
            var r = BacktestEngine.Run(Flat(1000, 60), 0, Market.KRX_KOSPI);
            Assert.Equal(0, r.TradeCount);
            Assert.Equal(0m, r.TotalReturn);
            Assert.Equal(0m, r.MaxDrawdown);
            Assert.Equal(30, r.EquityCurve.Count);                            // i=30..59
        }

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        [InlineData(4)]
        public void AllStrategies_ProduceValidBounds(int strategy)
        {
            var r = BacktestEngine.Run(Wave(120), strategy, Market.KRX_KOSPI);
            Assert.Equal(90, r.EquityCurve.Count);                            // 120 - 30
            Assert.InRange(r.MaxDrawdown, 0m, 100m);
            Assert.InRange(r.WinRate, 0m, 100m);
            Assert.True(r.TradeCount >= 0);
        }

        [Fact]
        public void Kosdaq_HasHigherSellTax_NotMoreProfitThanKospi()
        {
            // 동일 시그널/시리즈에서 코스닥(0.23%)이 코스피(0.18%)보다 수익률이 높을 수 없다(세율 가정 검증)
            var wave = Wave(120);
            var kospi = BacktestEngine.Run(wave, 0, Market.KRX_KOSPI);
            var kosdaq = BacktestEngine.Run(wave, 0, Market.KRX_KOSDAQ);
            Assert.True(kosdaq.TotalReturn <= kospi.TotalReturn + 0.0001m);
        }
    }
}
