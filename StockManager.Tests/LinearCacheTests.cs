// ============================================================================
// LinearCacheTests — S13 #2 선형 정렬 인-메모리 캐시 단위 테스트
//  · LoadAll: 정렬 보장, 입력 격리(뮤테이션 방지), 빈 데이터
//  · GetSpan: O(1) 조회 · 길이·첫·끝 캔들 정확도 · 대소문자 무시
//  · GetFrameSpan: Close 일치 · ZeroAllocTickEngine 직결 검증
//  · GetSpanRange: 이진 탐색 O(log N) · 양 끝 포함 · 범위 외 → Empty
//  · TryGetDescriptor: 오프셋·길이 정합성 · 심볼 간 비겹침
//  · ReadOnly 제약: 반환 타입이 ReadOnlySpan — 쓰기 불가 (컴파일 타임)
// ============================================================================
using System;
using System.Collections.Generic;
using StockManager.Common;
using Xunit;

namespace StockManager.Tests
{
    public class LinearCacheTests
    {
        // ── 헬퍼 ──────────────────────────────────────────────────────────────

        private static readonly DateTime D1 = new(2024, 1, 1);
        private static readonly DateTime D2 = new(2024, 1, 2);
        private static readonly DateTime D3 = new(2024, 1, 3);
        private static readonly DateTime D4 = new(2024, 1, 4);
        private static readonly DateTime D5 = new(2024, 1, 5);

        private static Candle C(DateTime date, decimal close = 100m) => new()
        {
            Date = date, Open = close,
            High = close * 1.01m, Low = close * 0.99m, Close = close, Volume = 1_000
        };

        private static LinearQuoteCache Build(params (string sym, Candle[] candles)[] data)
        {
            var dict = new Dictionary<string, IReadOnlyList<Candle>>(StringComparer.OrdinalIgnoreCase);
            foreach (var (sym, arr) in data) dict[sym] = arr;
            var cache = new LinearQuoteCache();
            cache.LoadAll(dict);
            return cache;
        }

        // ════════════════════════════════════════════════════════════════════
        // 1. LoadAll — 기본 동작
        // ════════════════════════════════════════════════════════════════════

        [Fact]
        public void LoadAll_EmptyData_ZeroCountsNoException()
        {
            var cache = new LinearQuoteCache();
            cache.LoadAll(new Dictionary<string, IReadOnlyList<Candle>>());
            Assert.Equal(0, cache.TotalCount);
            Assert.Equal(0, cache.SymbolCount);
        }

        [Fact]
        public void LoadAll_UnsortedInput_StoredSortedByDate()
        {
            // 의도적 역순 입력 — 캐시 내부는 오름차순이어야 함
            var cache = Build(("A", new[] { C(D3, 3m), C(D1, 1m), C(D2, 2m) }));
            var span = cache.GetSpan("A");
            Assert.Equal(D1, span[0].Date);
            Assert.Equal(D2, span[1].Date);
            Assert.Equal(D3, span[2].Date);
        }

        [Fact]
        public void LoadAll_InputMutated_CacheUnchanged()
        {
            var list = new List<Candle> { C(D1, 100m), C(D2, 200m) };
            var dict = new Dictionary<string, IReadOnlyList<Candle>> { ["A"] = list };
            var cache = new LinearQuoteCache();
            cache.LoadAll(dict);

            list.Add(C(D3, 300m));  // 적재 후 입력 변경

            Assert.Equal(2, cache.GetSpan("A").Length);  // 캐시는 2개 유지
        }

        // ════════════════════════════════════════════════════════════════════
        // 2. GetSpan — O(1) 조회
        // ════════════════════════════════════════════════════════════════════

        [Fact]
        public void GetSpan_UnknownSymbol_ReturnsEmpty()
        {
            var cache = Build(("AAPL", new[] { C(D1) }));
            Assert.True(cache.GetSpan("TSLA").IsEmpty);
        }

        [Fact]
        public void GetSpan_Length_MatchesInput()
        {
            var cache = Build(("AAPL", new[] { C(D1), C(D2), C(D3) }));
            Assert.Equal(3, cache.GetSpan("AAPL").Length);
        }

        [Fact]
        public void GetSpan_FirstCandle_DateAndClose()
        {
            var cache = Build(("AAPL", new[] { C(D1, 150m), C(D2, 160m) }));
            var span = cache.GetSpan("AAPL");
            Assert.Equal(D1,   span[0].Date);
            Assert.Equal(150m, span[0].Close);
        }

        [Fact]
        public void GetSpan_LastCandle_DateAndClose()
        {
            var cache = Build(("AAPL", new[] { C(D1, 150m), C(D2, 160m) }));
            var span = cache.GetSpan("AAPL");
            Assert.Equal(D2,   span[^1].Date);
            Assert.Equal(160m, span[^1].Close);
        }

        [Fact]
        public void GetSpan_CaseInsensitive()
        {
            var cache = Build(("aapl", new[] { C(D1), C(D2) }));
            Assert.Equal(2, cache.GetSpan("AAPL").Length);
            Assert.Equal(2, cache.GetSpan("Aapl").Length);
        }

        [Fact]
        public void GetSpan_ReturnsReadOnlySpan_Type()
        {
            // ReadOnlySpan<T>는 ref struct — 인터페이스 불가; 타입명으로 반환 시그니처 확인
            var cache = Build(("A", new[] { C(D1) }));
            ReadOnlySpan<Candle> span = cache.GetSpan("A");  // 컴파일 성공 = ReadOnly 확인
            Assert.Equal(1, span.Length);
        }

        // ════════════════════════════════════════════════════════════════════
        // 3. GetFrameSpan — ZeroAllocTickEngine 직결 검증
        // ════════════════════════════════════════════════════════════════════

        [Fact]
        public void GetFrameSpan_UnknownSymbol_ReturnsEmpty()
        {
            var cache = Build(("A", new[] { C(D1) }));
            Assert.True(cache.GetFrameSpan("TSLA").IsEmpty);
        }

        [Fact]
        public void GetFrameSpan_CloseMatchesCandle()
        {
            var cache = Build(("A", new[] { C(D1, 123.45m), C(D2, 678.90m) }));
            var candles = cache.GetSpan("A");
            var frames  = cache.GetFrameSpan("A");
            Assert.Equal(candles.Length, frames.Length);
            for (int i = 0; i < candles.Length; i++)
                Assert.Equal((double)candles[i].Close, frames[i].Close, precision: 8);
        }

        [Fact]
        public void GetFrameSpan_ZeroAllocTickEngine_RunMaCross()
        {
            // LinearQuoteCache → GetFrameSpan → ZeroAllocTickEngine 전체 파이프라인 연결 확인
            var candles = CartridgeEngine.ReferenceSeries(42);
            var dict = new Dictionary<string, IReadOnlyList<Candle>> { ["REF"] = candles };
            var cache = new LinearQuoteCache();
            cache.LoadAll(dict);

            var result = ZeroAllocTickEngine.RunMaCross(cache.GetFrameSpan("REF"), 5, 20);
            var direct = CartridgeEngine.BacktestFast(5, 20, 42);

            Assert.Equal(direct.TradeCount,    result.TradeCount);
            Assert.Equal(direct.TotalReturnPct, result.TotalReturnPct, precision: 8);
        }

        // ════════════════════════════════════════════════════════════════════
        // 4. TotalCount / SymbolCount
        // ════════════════════════════════════════════════════════════════════

        [Fact]
        public void TotalCount_SumOfAllSymbolCandles()
        {
            var cache = Build(
                ("AAPL", new[] { C(D1), C(D2) }),
                ("TSLA", new[] { C(D1), C(D2), C(D3) }));
            Assert.Equal(5, cache.TotalCount);
        }

        [Fact]
        public void SymbolCount_DistinctSymbols()
        {
            var cache = Build(("A", new[] { C(D1) }), ("B", new[] { C(D1) }));
            Assert.Equal(2, cache.SymbolCount);
        }

        // ════════════════════════════════════════════════════════════════════
        // 5. TryGetDescriptor — 오프셋·길이·비겹침
        // ════════════════════════════════════════════════════════════════════

        [Fact]
        public void TryGetDescriptor_ValidSymbol_CorrectLength()
        {
            var cache = Build(("AAPL", new[] { C(D1), C(D2), C(D3) }));
            Assert.True(cache.TryGetDescriptor("AAPL", out var d));
            Assert.Equal(3, d.Length);
        }

        [Fact]
        public void TryGetDescriptor_UnknownSymbol_ReturnsFalse()
        {
            var cache = Build(("AAPL", new[] { C(D1) }));
            Assert.False(cache.TryGetDescriptor("TSLA", out _));
        }

        [Fact]
        public void TryGetDescriptor_TwoSymbols_NonOverlapping()
        {
            var cache = Build(
                ("AAPL", new[] { C(D1), C(D2), C(D3) }),
                ("TSLA", new[] { C(D1), C(D2) }));
            cache.TryGetDescriptor("AAPL", out var dA);
            cache.TryGetDescriptor("TSLA", out var dT);
            bool noOverlap = dA.Offset + dA.Length <= dT.Offset
                          || dT.Offset + dT.Length <= dA.Offset;
            Assert.True(noOverlap, $"AAPL [{dA.Offset},{dA.Offset+dA.Length}) 와 TSLA [{dT.Offset},{dT.Offset+dT.Length}) 겹침");
        }

        [Fact]
        public void TryGetDescriptor_OffsetPlusLength_EqualsTotalCount()
        {
            var cache = Build(
                ("A", new[] { C(D1), C(D2) }),
                ("B", new[] { C(D3), C(D4), C(D5) }));
            // 모든 디스크립터 Offset+Length 의 최대값 = TotalCount
            int maxEnd = 0;
            foreach (var sym in cache.Symbols)
            {
                cache.TryGetDescriptor(sym, out var d);
                maxEnd = Math.Max(maxEnd, d.Offset + d.Length);
            }
            Assert.Equal(cache.TotalCount, maxEnd);
        }

        // ════════════════════════════════════════════════════════════════════
        // 6. GetSpanRange — 이진 탐색 O(log N)
        // ════════════════════════════════════════════════════════════════════

        [Fact]
        public void GetSpanRange_FullRange_ReturnsAll()
        {
            var cache = Build(("A", new[] { C(D1), C(D2), C(D3) }));
            var span = cache.GetSpanRange("A", D1, D3);
            Assert.Equal(3, span.Length);
        }

        [Fact]
        public void GetSpanRange_MiddleRange_CorrectSubset()
        {
            var cache = Build(("A", new[] { C(D1), C(D2), C(D3), C(D4), C(D5) }));
            var span = cache.GetSpanRange("A", D2, D4);
            Assert.Equal(3, span.Length);
            Assert.Equal(D2, span[0].Date);
            Assert.Equal(D4, span[^1].Date);
        }

        [Fact]
        public void GetSpanRange_SingleDay_ReturnsOneCandle()
        {
            var cache = Build(("A", new[] { C(D1), C(D2, 42m), C(D3) }));
            var span = cache.GetSpanRange("A", D2, D2);
            Assert.Equal(1, span.Length);
            Assert.Equal(42m, span[0].Close);
        }

        [Fact]
        public void GetSpanRange_BeforeAllDates_ReturnsEmpty()
        {
            var cache = Build(("A", new[] { C(D3), C(D4), C(D5) }));
            Assert.True(cache.GetSpanRange("A", D1, D2).IsEmpty);
        }

        [Fact]
        public void GetSpanRange_AfterAllDates_ReturnsEmpty()
        {
            var cache = Build(("A", new[] { C(D1), C(D2) }));
            Assert.True(cache.GetSpanRange("A", D4, D5).IsEmpty);
        }

        [Fact]
        public void GetSpanRange_GapInDates_SkipsGap()
        {
            // D1, D3, D5 — D2·D4 없음
            var cache = Build(("A", new[] { C(D1), C(D3), C(D5) }));
            var span = cache.GetSpanRange("A", D1, D4);  // D1, D3 포함, D5 제외
            Assert.Equal(2, span.Length);
            Assert.Equal(D1, span[0].Date);
            Assert.Equal(D3, span[^1].Date);
        }

        [Fact]
        public void GetSpanRange_UnknownSymbol_ReturnsEmpty()
        {
            var cache = Build(("A", new[] { C(D1) }));
            Assert.True(cache.GetSpanRange("Z", D1, D5).IsEmpty);
        }
    }
}
