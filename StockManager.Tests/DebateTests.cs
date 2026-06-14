// ============================================================================
// DebateTests - 토론 그라운딩/판정 파싱/승률 채점 (순수·결정적)
// ============================================================================
using System;
using System.Collections.Generic;
using System.Linq;
using StockManager.Common;
using Xunit;

namespace StockManager.Tests
{
    public class DebateTests
    {
        private static List<Candle> Candles(int n)
        {
            var list = new List<Candle>();
            for (int i = 0; i < n; i++)
            {
                decimal c = (decimal)(100 + 10 * Math.Sin(i / 5.0) + 0.1 * i);
                list.Add(new Candle { Date = new DateTime(2024, 1, 1).AddDays(i), Open = c, High = c * 1.01m, Low = c * 0.99m, Close = c, Volume = 1000 + i % 5 });
            }
            return list;
        }

        [Fact]
        public void BuildContext_ContainsGroundingMetrics()
        {
            var ctx = DebateService.BuildContext("005930", Candles(60));
            Assert.Contains("005930", ctx);
            Assert.Contains("RSI(14)", ctx);
            Assert.Contains("MA20 이격", ctx);
            Assert.Contains("레짐", ctx);
        }

        [Fact]
        public void BuildContext_TooFew_SaysInsufficient()
            => Assert.Contains("부족", DebateService.BuildContext("X", Candles(10)));

        [Theory]
        [InlineData("[판정: 매수]\n근거...", 1)]
        [InlineData("[판정: 매도] 하락 위험", -1)]
        [InlineData("[판정: 중립]", 0)]
        [InlineData("애매한 텍스트", 0)]
        public void ParseStance_Works(string judge, int expected)
            => Assert.Equal(expected, DebateService.ParseStance(judge));

        [Fact]
        public void DebateScore_ComputesDirectionalHitRates()
        {
            var samples = new (int, double)[]
            {
                (1, 5.0),    // 매수 판정 · 상승 → 적중
                (1, -2.0),   // 매수 판정 · 하락 → 실패
                (-1, -3.0),  // 매도 판정 · 하락 → 적중
                (-1, 4.0),   // 매도 판정 · 상승 → 실패
                (0, 1.0),    // 중립 → 제외
            };
            var r = DebateScore.Compute(samples);
            Assert.Equal(2, r.BullishN);
            Assert.Equal(1, r.BullishHit);
            Assert.Equal(50.0, r.BullishHitPct, 1);
            Assert.Equal(2, r.BearishN);
            Assert.Equal(1, r.BearishHit);
            Assert.Equal(50.0, r.BearishHitPct, 1);
            Assert.Equal(4, r.ResolvedN);
        }

        [Fact]
        public void DebateScore_Empty_ZeroRates()
        {
            var r = DebateScore.Compute(Enumerable.Empty<(int, double)>());
            Assert.Equal(0, r.ResolvedN);
            Assert.Equal(0, r.BullishHitPct, 1);
        }
    }
}
