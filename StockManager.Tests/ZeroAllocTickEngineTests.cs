// ============================================================================
// ZeroAllocTickEngineTests — S13 #1 제로-할당 틱 파이프라인 단위 테스트
//  · BacktestFast vs Backtest 패리티 (double vs decimal 부동소수 오차 ≤ 0.05% p)
//  · TradeCount 정확 일치
//  · GC 힙 할당량 = 0 (JIT 웜업 후 핫 루프 측정)
//  · 경계 조건 (빈 Span, 시리즈 길이 부족)
//  · CandleFrame / ZeroAllocResult 값 타입 확인
// ============================================================================
using System;
using Xunit;

namespace StockManager.Tests
{
    public class ZeroAllocTickEngineTests
    {
        // double vs decimal 누적 오차 허용 범위 (단위: 퍼센트포인트)
        private const double Eps = 0.05;

        // ════════════════════════════════════════════════════════════════════
        // 1. BacktestFast vs Backtest 패리티
        // ════════════════════════════════════════════════════════════════════

        [Theory]
        [InlineData(5, 20, 42)]
        [InlineData(10, 30, 99)]
        [InlineData(5, 15, 7)]
        public void BacktestFast_MatchesBacktest_TotalReturn(int s, int l, int seed)
        {
            var (ret, _, _) = CartridgeEngine.Backtest(s, l, seed);
            var fast = CartridgeEngine.BacktestFast(s, l, seed);
            Assert.InRange(fast.TotalReturnPct, ret - Eps, ret + Eps);
        }

        [Theory]
        [InlineData(5, 20, 42)]
        [InlineData(10, 30, 99)]
        [InlineData(5, 15, 7)]
        public void BacktestFast_MatchesBacktest_MaxDrawdown(int s, int l, int seed)
        {
            var (_, mdd, _) = CartridgeEngine.Backtest(s, l, seed);
            var fast = CartridgeEngine.BacktestFast(s, l, seed);
            Assert.InRange(fast.MaxDrawdownPct, mdd - Eps, mdd + Eps);
        }

        [Theory]
        [InlineData(5, 20, 42)]
        [InlineData(10, 30, 99)]
        [InlineData(5, 15, 7)]
        public void BacktestFast_MatchesBacktest_TradeCount(int s, int l, int seed)
        {
            var (_, _, trades) = CartridgeEngine.Backtest(s, l, seed);
            var fast = CartridgeEngine.BacktestFast(s, l, seed);
            Assert.Equal(trades, fast.TradeCount);
        }

        // ════════════════════════════════════════════════════════════════════
        // 2. GC 힙 할당 = 0 (핫 루프 기준)
        // ════════════════════════════════════════════════════════════════════

        [Fact]
        public void RunMaCross_ZeroAllocation_HotLoop()
        {
            // JIT 웜업: 첫 호출에서 컴파일·최적화 완료
            var frames = CartridgeEngine.ReferenceFrames(42);
            ZeroAllocTickEngine.RunMaCross(frames, 5, 20);
            ZeroAllocTickEngine.RunMaCross(frames, 5, 20);  // 안정화

            // 힙 할당 측정
            long before = GC.GetAllocatedBytesForCurrentThread();
            ZeroAllocTickEngine.RunMaCross(frames, 5, 20);
            long after = GC.GetAllocatedBytesForCurrentThread();

            Assert.Equal(0L, after - before);
        }

        // ════════════════════════════════════════════════════════════════════
        // 3. 경계 조건
        // ════════════════════════════════════════════════════════════════════

        [Fact]
        public void RunMaCross_EmptySpan_ReturnsZero()
        {
            var result = ZeroAllocTickEngine.RunMaCross(
                ReadOnlySpan<CandleFrame>.Empty, 5, 20);
            Assert.Equal(0.0, result.TotalReturnPct);
            Assert.Equal(0.0, result.MaxDrawdownPct);
            Assert.Equal(0, result.TradeCount);
        }

        [Fact]
        public void RunMaCross_TooShortSeries_ReturnsZeroTrades()
        {
            // frames.Length = longP + 1 = 21 → warmup 기준 신호 바 없음
            var frames = ZeroAllocTickEngine.ToFrameArray(
                CartridgeEngine.ReferenceSeries(1, 21));
            var result = ZeroAllocTickEngine.RunMaCross(frames, 5, 20);
            Assert.Equal(0, result.TradeCount);
        }

        [Fact]
        public void RunMaCross_InvalidParams_ReturnsZero()
        {
            var frames = CartridgeEngine.ReferenceFrames(42);
            // shortP >= longP — 無効
            var r = ZeroAllocTickEngine.RunMaCross(frames, 20, 5);
            Assert.Equal(0, r.TradeCount);
        }

        // ════════════════════════════════════════════════════════════════════
        // 4. 캐시 동작
        // ════════════════════════════════════════════════════════════════════

        [Fact]
        public void ReferenceFrames_IsCached_ReturnsSameArray()
        {
            var a = CartridgeEngine.ReferenceFrames(42);
            var b = CartridgeEngine.ReferenceFrames(42);
            Assert.True(ReferenceEquals(a, b));
        }

        [Fact]
        public void ReferenceFrames_DifferentSeeds_ReturnDifferentArrays()
        {
            var a = CartridgeEngine.ReferenceFrames(1);
            var b = CartridgeEngine.ReferenceFrames(2);
            Assert.False(ReferenceEquals(a, b));
        }

        // ════════════════════════════════════════════════════════════════════
        // 5. CandleFrame 정확도
        // ════════════════════════════════════════════════════════════════════

        [Fact]
        public void CandleFrame_From_PreservesClosePrice()
        {
            var candles = CartridgeEngine.ReferenceSeries(42, 10);
            var frames  = ZeroAllocTickEngine.ToFrameArray(candles);
            for (int i = 0; i < candles.Count; i++)
                Assert.Equal((double)candles[i].Close, frames[i].Close, precision: 8);
        }

        [Fact]
        public void CandleFrame_From_PreservesDateTicks()
        {
            var candles = CartridgeEngine.ReferenceSeries(42, 5);
            var frames  = ZeroAllocTickEngine.ToFrameArray(candles);
            for (int i = 0; i < candles.Count; i++)
                Assert.Equal(candles[i].Date.Ticks, frames[i].DateTicks);
        }

        // ════════════════════════════════════════════════════════════════════
        // 6. 값 타입 확인 (힙 할당 불가 구조 증명)
        // ════════════════════════════════════════════════════════════════════

        [Fact]
        public void CandleFrame_IsValueType()
            => Assert.True(typeof(CandleFrame).IsValueType);

        [Fact]
        public void ZeroAllocResult_IsValueType()
            => Assert.True(typeof(ZeroAllocResult).IsValueType);
    }
}
