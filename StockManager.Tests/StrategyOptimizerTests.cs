// ============================================================================
// StrategyOptimizerTests - 오버나잇 옵티마이저 격자탐색 불변식/결정론 테스트
//  외부 의존성 없는 순수 계산. 합성 캔들로 교차·거래를 유발해 검증.
// ============================================================================
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace StockManager.Tests
{
    public class StrategyOptimizerTests
    {
        // 진폭이 큰 사인파 + 완만한 상승 → MA 골든/데드크로스가 반복 발생(거래 유발)
        private static List<Candle> Wave(int n, double amp = 30, double period = 25, double drift = 0.1)
        {
            var list = new List<Candle>(n);
            for (int i = 0; i < n; i++)
            {
                double p = 100 + drift * i + amp * Math.Sin(2 * Math.PI * i / period);
                decimal close = (decimal)Math.Max(1.0, p);
                list.Add(new Candle
                {
                    Date = new DateTime(2020, 1, 1).AddDays(i),
                    Open = close, High = close * 1.01m, Low = close * 0.99m,
                    Close = close, Volume = 1000 + i % 7
                });
            }
            return list;
        }

        [Fact]
        public void GridSize_Is16_AllShortLessThanLong()
        {
            // 단기 {5,8,10,12} 모두 < 장기 {20,26,30,60} → 4×4 = 16 조합
            Assert.Equal(16, StrategyOptimizer.GridSize);
        }

        [Fact]
        public void Wave_ProducesValidWinner_WithFullGrid()
        {
            var r = StrategyOptimizer.Optimize("TEST", "테스트", Wave(220), Market.KRX_KOSPI);
            Assert.True(r.IsValid);
            Assert.NotNull(r.Best);
            Assert.Equal(StrategyOptimizer.GridSize, r.Candidates.Count);   // 16 후보 모두 평가
            Assert.True(r.Best!.ShortP < r.Best.LongP);
        }

        [Fact]
        public void Winner_HasMaxTotalReturn_AmongTraded()
        {
            var r = StrategyOptimizer.Optimize("TEST", "테스트", Wave(220), Market.KRX_KOSDAQ);
            var traded = r.Candidates.Where(c => c.TradeCount > 0).ToList();
            Assert.NotEmpty(traded);
            var maxRet = traded.Max(c => c.TotalReturn);
            Assert.Equal(maxRet, r.Best!.TotalReturn);                      // 우승 = 거래발생 중 최고 수익률
        }

        [Fact]
        public void Deterministic_SameCandles_SameWinner()
        {
            var candles = Wave(200);
            var a = StrategyOptimizer.Optimize("X", "X", candles, Market.US_NASDAQ);
            var b = StrategyOptimizer.Optimize("X", "X", candles, Market.US_NASDAQ);
            Assert.Equal(a.Best!.ShortP, b.Best!.ShortP);
            Assert.Equal(a.Best.LongP, b.Best.LongP);
            Assert.Equal(a.Best.TotalReturn, b.Best.TotalReturn);
        }

        [Fact]
        public void TooFewCandles_Invalid_NoBest()
        {
            var r = StrategyOptimizer.Optimize("S", "S", Wave(40), Market.KRX_KOSPI);   // 최장 장기(60)+2 미만
            Assert.False(r.IsValid);
            Assert.Null(r.Best);
            Assert.Contains("캔들", r.Note);
        }

        [Fact]
        public void FlatPrice_NoCrosses_NoTrades_Invalid()
        {
            var flat = Enumerable.Range(0, 200).Select(i => new Candle
            {
                Date = new DateTime(2021, 1, 1).AddDays(i),
                Open = 100, High = 100, Low = 100, Close = 100, Volume = 10
            }).ToList();
            var r = StrategyOptimizer.Optimize("F", "F", flat, Market.KRX_KOSPI);
            Assert.False(r.IsValid);              // 평탄 → 교차 없음 → 전 조합 무거래
            Assert.All(r.Candidates, c => Assert.Equal(0, c.TradeCount));
        }

        [Fact]
        public void RunMaCross_InvalidParams_ReturnsEmpty()
        {
            var c = Wave(100);
            Assert.Equal(0, BacktestEngine.RunMaCross(c, Market.KRX_KOSPI, 20, 10).TradeCount);  // short>=long
            Assert.Equal(0, BacktestEngine.RunMaCross(c, Market.KRX_KOSPI, 10, 10).TradeCount);
            Assert.Empty(BacktestEngine.RunMaCross(c, Market.KRX_KOSPI, 0, 20).EquityCurve);     // short<1
        }

        [Fact]
        public void ToRow_MapsBest_InvalidGivesDashLabel()
        {
            var valid = StrategyOptimizer.Optimize("V", "브이", Wave(220), Market.KRX_KOSPI);
            var row = StrategyOptimizer.ToRow(valid);
            Assert.Equal(valid.Best!.ShortP, row.ShortP);
            Assert.Equal(valid.Best.TotalReturn, row.TotalReturn);
            Assert.True(row.IsValid);
            Assert.Equal($"MA{valid.Best.ShortP}/{valid.Best.LongP}", row.Label);

            var invalid = StrategyOptimizer.Optimize("Z", "Z", Wave(30), Market.KRX_KOSPI);
            Assert.Equal("-", StrategyOptimizer.ToRow(invalid).Label);
        }
    }
}
