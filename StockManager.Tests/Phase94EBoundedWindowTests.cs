// ============================================================================
// Phase94EBoundedWindowTests — v11.4.0(94-E) BoundedQuoteWindow 적대적 검증(100% 단언, Think-Beyond)
//   대용량 틱 UI 로딩의 LOH/Off-Screen 맹점을 격리하는 경계 고정 데시메이션 창을 지상 검증:
//     ① 경계 상한: 100만 유입에도 작업집합 ≤ 2N (LOH·off-screen 원천 차단)
//     ② 전 구간 보존: 첫 점 시각 + 마지막 점 시각이 창 양끝에 정확히 유지(데시메이션해도 범위 유실 0)
//     ③ 병합 OHLC 정확성: High=max·Low=min·Close=last·Vol=sum
//     ④ stride 2의 거듭제곱 증가(무손실→데시메이션 전이)
//     ⑤ δ=0: Push(제자리 병합 포함) 힙 0
// ============================================================================
using System;
using MS2E.UI.Portable.Infrastructure;
using Xunit;

namespace StockManager.Tests
{
    [Collection("ConcurrencyStress")]
    public sealed class Phase94EBoundedWindowTests
    {
        private static QuotePoint P(long t, double o, double h, double l, double c, double v) => new(t, o, h, l, c, v);

        // ══ ① 경계 상한: 100만 유입에도 ≤ 2N ═══════════════════════════════════
        [Fact]
        public void Bounded_Never_Exceeds_2N_Under_Millions()
        {
            var w = new BoundedQuoteWindow(1024);
            for (int i = 0; i < 1_000_000; i++)
            {
                w.Push(P(i, i, i, i, i, 1));
                Assert.True(w.Count <= w.Capacity * 2);   // ★작업집합 항상 경계 안(LOH/off-screen 차단)
            }
            Assert.Equal(1_000_000L, w.TotalIngested);
            Assert.True(w.Count <= 2048);
            Assert.True(w.Stride > 1);   // 데시메이션 발생
        }

        // ══ ② 전 구간 보존: 첫/마지막 시각이 창 양끝에 유지 ════════════════════
        [Fact]
        public void FullSpan_Endpoints_Preserved()
        {
            var w = new BoundedQuoteWindow(256);
            const int M = 100_000;
            for (int i = 0; i < M; i++) w.Push(P(i, i, i, i, i, 1));

            Span<QuotePoint> dest = stackalloc QuotePoint[512];   // 2N
            int n = w.Snapshot(dest);
            Assert.True(n > 0 && n <= 512);
            Assert.Equal(0L, dest[0].TimeTicks);          // ★첫 점 시각 보존(범위 왼쪽 끝)
            Assert.Equal((long)(M - 1), dest[n - 1].TimeTicks);   // ★마지막 점 시각 보존(오른쪽 끝·최신 raw)
        }

        // ══ ③ 병합 OHLC 정확성 ════════════════════════════════════════════════
        [Fact]
        public void Merge_OHLC_Correct_After_Compaction()
        {
            var w = new BoundedQuoteWindow(8);   // 버퍼 16 → 17번째 Push 에서 1회 병합
            // p_i = (t=i, O=i, H=i*10, L=-i, C=i, V=1)
            for (int i = 0; i < 17; i++) w.Push(P(i, i, i * 10, -i, i, 1));

            Assert.Equal(2, w.Stride);   // 1회 데시메이션
            Span<QuotePoint> dest = stackalloc QuotePoint[16];
            int n = w.Snapshot(dest);
            // 첫 버킷 = Merge(p0,p1): O=0 · H=max(0,10)=10 · L=min(0,-1)=-1 · C=p1.C=1 · V=2 · T=0
            var b0 = dest[0];
            Assert.Equal(0L, b0.TimeTicks);
            Assert.Equal(0d, b0.Open);
            Assert.Equal(10d, b0.High);
            Assert.Equal(-1d, b0.Low);
            Assert.Equal(1d, b0.Close);
            Assert.Equal(2d, b0.Volume);
            // 마지막 원소 = 17번째 raw(p16): 병합 안 됨(최신은 항상 raw 말미)
            Assert.Equal(16L, dest[n - 1].TimeTicks);
            Assert.Equal(160d, dest[n - 1].High);
        }

        // ══ ④ stride 2의 거듭제곱 ═════════════════════════════════════════════
        [Fact]
        public void Stride_Doubles_Per_Compaction()
        {
            var w = new BoundedQuoteWindow(16);   // 버퍼 32
            Assert.Equal(1, w.Stride);
            for (int i = 0; i < 33; i++) w.Push(P(i, i, i, i, i, 1));   // 33번째에서 1회 병합
            Assert.Equal(2, w.Stride);
            for (int i = 33; i < 200; i++) w.Push(P(i, i, i, i, i, 1));  // 추가 병합
            Assert.True(w.Stride >= 4);
            Assert.True((w.Stride & (w.Stride - 1)) == 0);   // 2의 거듭제곱
        }

        // ══ ⑤ δ=0: Push(병합 포함) 핫패스 힙 0 ═══════════════════════════════
        [Fact]
        public void Push_ZeroAllocation()
        {
            var w = new BoundedQuoteWindow(1024);
            for (int i = 0; i < 100_000; i++) w.Push(P(i, i, i, i, i, 1));   // 워밍업(병합 다수 유발)
            long Measure()
            {
                long b = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < 500_000; i++) w.Push(P(i, i, i, i, i, 1));
                return GC.GetAllocatedBytesForCurrentThread() - b;
            }
            _ = Measure();
            Assert.Equal(0L, ZeroAllocProbe.MinOf(Measure));   // ★Push+제자리 Compact 힙 0(고정 버퍼·in 구조체)
        }

        // ══ Snapshot 경계·시간순 ══════════════════════════════════════════════
        [Fact]
        public void Snapshot_Bounded_And_TimeOrdered()
        {
            var w = new BoundedQuoteWindow(64);
            for (int i = 0; i < 5000; i++) w.Push(P(i, i, i, i, i, 1));
            Span<QuotePoint> small = stackalloc QuotePoint[10];
            int n = w.Snapshot(small);
            Assert.Equal(10, n);                       // dest 용량 한도
            for (int i = 1; i < n; i++)
                Assert.True(small[i].TimeTicks >= small[i - 1].TimeTicks);   // 시간순(비감소)
        }
    }
}
