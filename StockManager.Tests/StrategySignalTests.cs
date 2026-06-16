// ============================================================================
// StrategySignalTests - 현재 신호 평가기 + MA크로스→스펙 변환 (순수·결정론·네트워크 0)
// ============================================================================
using System;
using System.Collections.Generic;
using System.Linq;
using StockManager.Common;
using Xunit;

namespace StockManager.Tests
{
    public class StrategySignalTests
    {
        private static List<Candle> Series(Func<int, double> f, int n)
        {
            var d = new DateTime(2022, 1, 1);
            return Enumerable.Range(0, n).Select(i =>
            {
                decimal c = (decimal)Math.Round(f(i), 2);
                return new Candle { Date = d.AddDays(i), Open = c, High = c * 1.01m, Low = c * 0.99m, Close = c, Volume = 1000 };
            }).ToList();
        }

        [Fact]
        public void FromMaCross_ReducesBack()
        {
            var spec = StrategyCompiler.FromMaCross(5, 20);
            Assert.True(StrategyCompiler.ReducesToMaCross(spec, out var s, out var l));
            Assert.Equal(5, s);
            Assert.Equal(20, l);
        }

        [Fact]
        public void CurrentSignal_VShape_InPosition()
        {
            // 하락 후 반등(V자) → 워밍업 이후 SMA5가 SMA20 위로 골든크로스 → 이후 유지 → 마지막 봉 '보유 중'
            var candles = Series(i => i < 50 ? 200 - i * 1.0 : 150 + (i - 50) * 1.2, 130);
            var sig = StrategyCompiler.CurrentSignal(StrategyCompiler.FromMaCross(5, 20), candles);
            Assert.True(sig.InPosition);
            Assert.Equal("보유 중", sig.State);
            Assert.Equal(candles[^1].Date, sig.AsOf);
        }

        [Fact]
        public void CurrentSignal_TrendDown_Watch()
        {
            // 꾸준히 하락 → SMA5 < SMA20 → 골든크로스 없음 → '관망', 미보유
            var candles = Series(i => 200 - i * 0.8, 120);
            var sig = StrategyCompiler.CurrentSignal(StrategyCompiler.FromMaCross(5, 20), candles);
            Assert.False(sig.InPosition);
            Assert.Equal("관망", sig.State);
        }

        [Fact]
        public void CurrentSignal_TooFew_Watch()
        {
            var sig = StrategyCompiler.CurrentSignal(StrategyCompiler.FromMaCross(5, 20), Series(i => 100 + i, 3));
            Assert.Equal("관망", sig.State);
            Assert.Contains("부족", sig.Detail);
        }

        [Fact]
        public void CurrentSignal_UnderWarmup_NoFakeSignal()
        {
            // 캔들 수(100)가 최장 지표기간+1(201)보다 적음 → 시드값 가짜 신호 대신 '표본 부족(워밍업 미달)'
            var sig = StrategyCompiler.CurrentSignal(StrategyCompiler.FromMaCross(50, 200), Series(i => 100 + i * 0.5, 100));
            Assert.Equal("관망", sig.State);
            Assert.False(sig.InPosition);
            Assert.Contains("워밍업", sig.Detail);
        }

        [Fact]
        public void CurrentSignal_Deterministic()
        {
            var candles = Series(i => 100 + 10 * Math.Sin(i / 7.0), 150);
            var spec = StrategyCompiler.FromMaCross(5, 20);
            var a = StrategyCompiler.CurrentSignal(spec, candles);
            var b = StrategyCompiler.CurrentSignal(spec, candles);
            Assert.Equal(a.State, b.State);
            Assert.Equal(a.InPosition, b.InPosition);
        }

        [Fact]
        public void CurrentSignal_RsiSpec_Evaluable()
        {
            // RSI 스펙도 평가 가능(예외 없이 상태 반환)
            var spec = StrategyParser.ParseNl("rsi 30 이하면 매수, 70 이상이면 매도").Spec!;
            var candles = Series(i => 100 + 12 * Math.Sin(i / 5.0), 120);
            var sig = StrategyCompiler.CurrentSignal(spec, candles);
            Assert.Contains(sig.State, new[] { "신규 진입", "청산 신호", "보유 중", "관망" });
        }
    }
}
