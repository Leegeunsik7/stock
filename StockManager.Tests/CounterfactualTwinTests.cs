// ============================================================================
// CounterfactualTwinTests - 반사실 트윈 트랙 계산/판정/경계 테스트 (순수·결정적)
// ============================================================================
using System;
using System.Collections.Generic;
using System.Linq;
using StockManager.Common;
using Xunit;

namespace StockManager.Tests
{
    public class CounterfactualTwinTests
    {
        private static List<Candle> Candles(params double[] closes)
        {
            var list = new List<Candle>();
            for (int i = 0; i < closes.Length; i++)
                list.Add(new Candle
                {
                    Date = new DateTime(2025, 1, 1).AddDays(i),
                    Open = (decimal)closes[i], High = (decimal)closes[i], Low = (decimal)closes[i],
                    Close = (decimal)closes[i], Volume = 1000
                });
            return list;
        }
        private static DateTime D(int dayIndex) => new DateTime(2025, 1, 1).AddDays(dayIndex);
        private static List<(DateTime, bool, decimal, decimal)> NoTrades() => new();
        private static List<(DateTime, SignalType)> NoSignals() => new();

        [Fact]
        public void BuyHold_RisingMarket_PositiveReturn()
        {
            var r = CounterfactualTwin.Compare(NoTrades(), NoSignals(), Candles(100, 110, 120, 132), Market.KRX_KOSPI);
            Assert.True(r.IsValid);
            Assert.Equal(32m, r.BuyHoldReturnPct, 1);   // 100→132 = +32%
        }

        [Fact]
        public void SignalTrack_BuyLowSellHigh_Profits()
        {
            // 4일째 매수 신호(저점 90), 6일째 매도 신호(고점 140)
            var candles = Candles(100, 95, 92, 90, 110, 130, 140, 135);
            var sigs = new List<(DateTime, SignalType)>
            {
                (D(3), SignalType.Buy),   // close 90에 진입
                (D(6), SignalType.Sell),  // close 140에 청산
            };
            var r = CounterfactualTwin.Compare(NoTrades(), sigs, candles, Market.US_NASDAQ);
            Assert.True(r.SignalEntered);
            Assert.Equal(2, r.SignalTrades);
            Assert.True(r.SignalReturnPct > 40m);   // 90→140 ≈ +55% (비용 차감 후에도 +)
        }

        [Fact]
        public void NoSignals_SignalTrackStaysCash_NotEntered()
        {
            var r = CounterfactualTwin.Compare(NoTrades(), NoSignals(), Candles(100, 120, 140), Market.KRX_KOSPI);
            Assert.False(r.SignalEntered);
            Assert.Equal(0m, r.SignalReturnPct, 6);   // 현금 유지 → 0%
            Assert.False(r.HasActual);
        }

        [Fact]
        public void Actual_BuyThenHold_RisingMarket_MatchesGain()
        {
            // 1일째 100에 10주 매수 후 보유 → 마지막 종가 150 → +50%
            var trades = new List<(DateTime, bool, decimal, decimal)> { (D(0), true, 10m, 100m) };
            var r = CounterfactualTwin.Compare(trades, NoSignals(), Candles(100, 120, 150), Market.KRX_KOSPI);
            Assert.True(r.HasActual);
            Assert.Equal(50m, r.ActualReturnPct, 1);
            Assert.Equal(1, r.ActualTrades);
        }

        [Fact]
        public void Actual_SellAll_RealizedReturn()
        {
            // 100에 10주 매수 → 120에 10주 전량 매도 → +20%, 잔량 0
            var trades = new List<(DateTime, bool, decimal, decimal)>
            {
                (D(0), true, 10m, 100m),
                (D(2), false, 10m, 120m),
            };
            var r = CounterfactualTwin.Compare(trades, NoSignals(), Candles(100, 110, 120), Market.KRX_KOSPI);
            Assert.True(r.HasActual);
            Assert.Equal(20m, r.ActualReturnPct, 1);
        }

        [Fact]
        public void GapVsHold_ZeroWhenActualEqualsBuyHold()
        {
            // 1일째 전량 매수 후 보유 = buy&hold와 동일 → 격차 ≈ 0
            var trades = new List<(DateTime, bool, decimal, decimal)> { (D(0), true, 5m, 100m) };
            var r = CounterfactualTwin.Compare(trades, NoSignals(), Candles(100, 130), Market.KRX_KOSPI);
            Assert.Equal(r.BuyHoldReturnPct, r.ActualReturnPct, 1);
            Assert.Equal(0m, r.GapVsHold, 1);
        }

        [Fact]
        public void Curve_NormalizedStartsAt100()
        {
            var r = CounterfactualTwin.Compare(NoTrades(), NoSignals(), Candles(100, 110, 120), Market.KRX_KOSPI);
            Assert.Equal(100m, r.Curve.First().Hold, 1);
            Assert.Equal(100m, r.Curve.First().Signal, 1);
            Assert.Equal(3, r.Curve.Count);
        }

        [Fact]
        public void EmptyCandles_Invalid()
        {
            Assert.False(CounterfactualTwin.Compare(NoTrades(), NoSignals(), Candles(100), Market.KRX_KOSPI).IsValid);
            Assert.False(CounterfactualTwin.Compare(NoTrades(), NoSignals(), new List<Candle>(), Market.KRX_KOSPI).IsValid);
        }

        [Fact]
        public void Deterministic_SameInput_SameResult()
        {
            var c = Candles(100, 95, 105, 110, 90, 130);
            var s = new List<(DateTime, SignalType)> { (D(1), SignalType.Buy), (D(5), SignalType.Sell) };
            var a = CounterfactualTwin.Compare(NoTrades(), s, c, Market.KRX_KOSPI);
            var b = CounterfactualTwin.Compare(NoTrades(), s, c, Market.KRX_KOSPI);
            Assert.Equal(a.SignalReturnPct, b.SignalReturnPct, 9);
            Assert.Equal(a.BuyHoldReturnPct, b.BuyHoldReturnPct, 9);
        }
    }
}
