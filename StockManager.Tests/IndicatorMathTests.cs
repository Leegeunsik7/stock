// ============================================================================
// IndicatorMathTests - 차트 보조지표 워밍업 시드/경계 회귀 테스트
//  숨은 룰 고정: RSI 워밍업=50·avgL==0→≈100, 볼린저%B 플랫=0.5, Stochastic 범위 [0,100],
//  EMA 첫값=data[0], MACD 길이 일치.
// ============================================================================
using System;
using System.Collections.Generic;
using System.Linq;
using StockManager.Common;
using Xunit;

namespace StockManager.Tests
{
    public class IndicatorMathTests
    {
        private static List<Candle> FromCloses(IEnumerable<decimal> closes)
        {
            var list = new List<Candle>();
            var d = new DateTime(2025, 1, 1);
            int i = 0;
            foreach (var c in closes)
                list.Add(new Candle { Date = d.AddDays(i++), Open = c, High = c, Low = c, Close = c, Volume = 1000 });
            return list;
        }
        private static List<Candle> Rising(int n) => FromCloses(Enumerable.Range(1, n).Select(i => (decimal)(1000 + i * 10)));
        private static List<Candle> Falling(int n) => FromCloses(Enumerable.Range(1, n).Select(i => (decimal)(1000 + (n - i) * 10)));
        private static List<Candle> Flat(decimal p, int n) => FromCloses(Enumerable.Repeat(p, n));

        [Fact]
        public void Ema_FirstEqualsData0()
        {
            var ema = IndicatorMath.Ema(new List<decimal> { 5, 10, 20, 40 }, 3);
            Assert.Equal(5m, ema[0]);
            Assert.Equal(4, ema.Count);
        }

        [Fact]
        public void Rsi_WarmupIs50_AndLengthMatches()
        {
            var candles = Rising(40);
            var rsi = IndicatorMath.Rsi(candles, 14);
            Assert.Equal(candles.Count, rsi.Count);
            Assert.Equal(50m, rsi[0]);
            Assert.Equal(50m, rsi[13]);   // i < period 구간은 시드 50
        }

        [Fact]
        public void Rsi_Rising_IsHigh_Falling_IsLow()
        {
            Assert.True(IndicatorMath.Rsi(Rising(40), 14)[^1] > 70m);   // 전부 상승 → ≈99
            Assert.True(IndicatorMath.Rsi(Falling(40), 14)[^1] < 30m);  // 전부 하락 → 0
        }

        [Fact]
        public void BollingerPctB_Flat_IsHalf()
        {
            var pctB = IndicatorMath.BollingerPctB(Flat(1000, 40), 20, 2m);
            Assert.All(pctB, v => Assert.Equal(0.5m, v));   // 밴드폭 0 → 0.5
        }

        [Fact]
        public void Stochastic_WarmupAndBounds()
        {
            var (k, dline) = IndicatorMath.Stochastic(Rising(40), 14, 3);
            Assert.Equal(50m, k[0]);                        // 워밍업 시드
            Assert.All(k, v => Assert.InRange(v, 0m, 100m));
            Assert.All(dline, v => Assert.InRange(v, 0m, 100m));
        }

        [Fact]
        public void Macd_AllSeriesSameLength()
        {
            var candles = Rising(60);
            var (macd, signal, hist) = IndicatorMath.Macd(candles);
            Assert.Equal(candles.Count, macd.Count);
            Assert.Equal(candles.Count, signal.Count);
            Assert.Equal(candles.Count, hist.Count);
        }
    }
}
