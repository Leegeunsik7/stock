// ============================================================================
// LinearQuoteCache — S13 #2 선형 정렬 인-메모리 캐시 (v3.82.0)
//  · Dictionary<string, List<Candle>> 파편화 구조를 단일 Candle[] 연속 배열로 통합.
//  · SymbolDescriptor(Offset, Length) 맵 → O(1) 인덱스 참조로 Span 즉시 반환.
//  · GetSpanRange: Date 이진 탐색 O(log N) 날짜 범위 조회.
//  · GetFrameSpan: ReadOnlySpan<CandleFrame> 반환 → ZeroAllocTickEngine 직결 (할당 0).
//  · 물리 메모리 연속 배치 → CPU 하드웨어 프리페처 최대 활용 (캐시 적중률 극대화).
// ============================================================================
using System;
using System.Collections.Generic;
using System.Linq;

namespace MS2E.Infrastructure
{
    public sealed class LinearQuoteCache
    {
        // ── 심볼 디스크립터 — 연속 배열 내 포인터 (값 타입, 힙 할당 없음) ──────
        public readonly record struct SymbolDescriptor(int Offset, int Length);

        // ── 단일 연속 backing store ─────────────────────────────────────────────
        private Candle[]                               _store  = Array.Empty<Candle>();
        private CandleFrame[]                          _frames = Array.Empty<CandleFrame>();
        private Dictionary<string, SymbolDescriptor>  _index  =
            new(StringComparer.OrdinalIgnoreCase);

        // ── 메타데이터 ──────────────────────────────────────────────────────────
        public int    SymbolCount => _index.Count;
        public int    TotalCount  => _store.Length;
        public IReadOnlyCollection<string> Symbols => _index.Keys;

        // ── 적재 ────────────────────────────────────────────────────────────────

        /// <summary>
        /// 복수 심볼의 캔들을 단일 연속 배열로 원자 교체 적재.
        /// 각 심볼 내 캔들은 Date 오름차순 보장. 입력 컬렉션 변경은 캐시에 영향 없음.
        /// </summary>
        public void LoadAll(IReadOnlyDictionary<string, IReadOnlyList<Candle>> data)
        {
            // 심볼 알파벳 순 → 결정론적·재현 가능한 오프셋 배치
            var ordered = data
                .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                .Select(kv => (Symbol: kv.Key,
                               Sorted: kv.Value.OrderBy(c => c.Date).ToArray()))
                .ToList();

            int total = 0;
            foreach (var o in ordered) total += o.Sorted.Length;

            var store  = new Candle[total];
            var frames = new CandleFrame[total];
            var index  = new Dictionary<string, SymbolDescriptor>(
                ordered.Count, StringComparer.OrdinalIgnoreCase);

            int offset = 0;
            foreach (var (sym, arr) in ordered)
            {
                if (arr.Length == 0) continue;
                arr.CopyTo(store, offset);                          // 연속 배열에 블록 복사
                for (int i = 0; i < arr.Length; i++)
                    frames[offset + i] = CandleFrame.From(arr[i]); // 병렬 CandleFrame 배열
                index[sym] = new SymbolDescriptor(offset, arr.Length);
                offset += arr.Length;
            }

            // 세 필드를 함께 교체 — LoadAll 이후 독자적으로 일관된 뷰 보장
            _store  = store;
            _frames = frames;
            _index  = index;
        }

        // ── O(1) 전체 구간 조회 ────────────────────────────────────────────────

        /// <summary>
        /// 심볼 전체 구간 Candle ReadOnlySpan — 복사 없음, GC 힙 할당 0.
        /// 반환 타입이 ReadOnlySpan이므로 쓰기 불가(컴파일 타임 보장).
        /// </summary>
        public ReadOnlySpan<Candle> GetSpan(string symbol)
        {
            if (!_index.TryGetValue(symbol, out var d)) return ReadOnlySpan<Candle>.Empty;
            return _store.AsSpan(d.Offset, d.Length);
        }

        /// <summary>
        /// 심볼 전체 구간 CandleFrame ReadOnlySpan — ZeroAllocTickEngine.RunMaCross 직결.
        /// </summary>
        public ReadOnlySpan<CandleFrame> GetFrameSpan(string symbol)
        {
            if (!_index.TryGetValue(symbol, out var d)) return ReadOnlySpan<CandleFrame>.Empty;
            return _frames.AsSpan(d.Offset, d.Length);
        }

        /// <summary>SymbolDescriptor(Offset, Length) 직접 참조 — 커스텀 슬라이싱 전용.</summary>
        public bool TryGetDescriptor(string symbol, out SymbolDescriptor d)
            => _index.TryGetValue(symbol, out d);

        // ── O(log N) 날짜 범위 조회 ────────────────────────────────────────────

        /// <summary>
        /// [from, to] 날짜 범위 Candle ReadOnlySpan — 이진 탐색 O(log N).
        /// 양 끝 포함(inclusive). 범위 내 캔들 없으면 Empty 반환.
        /// </summary>
        public ReadOnlySpan<Candle> GetSpanRange(string symbol, DateTime from, DateTime to)
        {
            if (!_index.TryGetValue(symbol, out var d) || d.Length == 0)
                return ReadOnlySpan<Candle>.Empty;

            var span = _store.AsSpan(d.Offset, d.Length);
            int lo = LowerBound(span, from);  // 첫 인덱스 : Date >= from
            int hi = UpperBound(span, to);    // 마지막 인덱스 : Date <= to
            if (lo > hi) return ReadOnlySpan<Candle>.Empty;
            return span.Slice(lo, hi - lo + 1);
        }

        // ── 이진 탐색 헬퍼 ────────────────────────────────────────────────────

        // lower_bound: span[i].Date >= target 인 최소 i (없으면 span.Length)
        private static int LowerBound(ReadOnlySpan<Candle> span, DateTime target)
        {
            int lo = 0, hi = span.Length;
            while (lo < hi)
            {
                int mid = (lo + hi) >> 1;
                if (span[mid].Date < target) lo = mid + 1;
                else                          hi = mid;
            }
            return lo;
        }

        // upper_bound: span[i].Date <= target 인 최대 i (없으면 -1)
        private static int UpperBound(ReadOnlySpan<Candle> span, DateTime target)
        {
            int lo = 0, hi = span.Length;
            while (lo < hi)
            {
                int mid = (lo + hi) >> 1;
                if (span[mid].Date <= target) lo = mid + 1;
                else                           hi = mid;
            }
            return lo - 1;
        }
    }
}
