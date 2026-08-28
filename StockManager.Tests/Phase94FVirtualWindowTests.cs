// ============================================================================
// Phase94FVirtualWindowTests — v11.5.0(94-F) BoundedVirtualWindow<T> 적대적 검증(100% 단언, Think-Beyond)
//   대용량 DB 레코드 렌더링의 LOH/DOM 폭발 맹점을 격리하는 경계 고정 가상 윈도우 거버너를 지상 검증:
//     ① 경계 물성화: 10만 논리 레코드에도 적재(Loaded) ≤ 용량(뷰포트만 물성화)
//     ② 히트: 뷰포트가 윈도우(마진 포함) 안이면 page-fault 0(재조회 0)
//     ③ 미스: 스크롤 이탈 시 1회 page-fault(loader 정확한 범위 호출)
//     ④ fail-closed: 윈도우 밖/미적재/total=0 → TryGet false
//     ⑤ δ=0: 히트 EnsureWindow + TryGet 핫패스 힙 0
// ============================================================================
using System;
using System.Collections.Generic;
using MS2E.UI.Portable.Infrastructure;
using Xunit;

namespace StockManager.Tests
{
    [Collection("ConcurrencyStress")]
    public sealed class Phase94FVirtualWindowTests
    {
        // data[i] = i 인 논리 레코드 집합에서 [start,start+count) 페이지를 반환하는 loader.
        private static Func<int, int, IReadOnlyList<int>> SliceLoader(int total, out int[] loadCalls)
        {
            var calls = new int[2] { -1, -1 };   // 마지막 (start, count)
            loadCalls = calls;
            return (start, count) =>
            {
                calls[0] = start; calls[1] = count;
                var page = new List<int>(count);
                for (int i = 0; i < count; i++) page.Add(start + i);
                return page;
            };
        }

        // ══ ① 경계 물성화: 10만에도 Loaded ≤ 용량 ═════════════════════════════
        [Fact]
        public void Viewport_Bounded_Materialization_Under_100k()
        {
            var w = new BoundedVirtualWindow<int>(capacity: 256, margin: 32);
            var loader = SliceLoader(100_000, out _);
            for (int first = 0; first < 100_000; first += 37)   // 다양한 스크롤 위치
            {
                w.EnsureWindow(first, 50, 100_000, loader);
                Assert.True(w.Loaded <= w.Capacity);            // ★물성화 항상 경계 안(DOM/LOH 방어)
                Assert.True(w.TryGet(first, out var v));         // 뷰포트 첫 행 접근 가능
                Assert.Equal(first, v);                          // 값 정확(논리 인덱스 == 값)
            }
            Assert.Equal(100_000, w.Total);
        }

        // ══ ② 히트: 윈도우 내 재요청 → page-fault 0 ═══════════════════════════
        [Fact]
        public void Hit_Within_Window_No_PageFault()
        {
            var w = new BoundedVirtualWindow<int>(256, 32);
            var loader = SliceLoader(10_000, out _);
            w.EnsureWindow(0, 50, 10_000, loader);
            long pf1 = w.PageFaults;
            Assert.Equal(1, pf1);
            w.EnsureWindow(10, 50, 10_000, loader);   // [10,60) ⊂ [0,256) → 히트
            w.EnsureWindow(100, 50, 10_000, loader);  // [100,150) ⊂ [0,256) → 히트
            Assert.Equal(pf1, w.PageFaults);          // ★재조회 0
            Assert.True(w.Hits >= 2);
        }

        // ══ ③ 미스: 스크롤 이탈 → page-fault + loader 정확 범위 ═══════════════
        [Fact]
        public void PageFault_On_Scroll_Out()
        {
            var w = new BoundedVirtualWindow<int>(256, 32);
            var loader = SliceLoader(10_000, out var calls);
            w.EnsureWindow(0, 50, 10_000, loader);
            w.EnsureWindow(1000, 50, 10_000, loader);   // [1000,1050) ∉ [0,256) → page-fault
            Assert.Equal(2, w.PageFaults);
            Assert.Equal(1000 - 32, calls[0]);          // need-마진 = 968 부터 적재
            Assert.True(w.TryGet(1000, out var v) && v == 1000);
        }

        // ══ ④ fail-closed: 윈도우 밖/미적재/total=0 ═══════════════════════════
        [Fact]
        public void TryGet_OutOfWindow_And_Empty_FailClosed()
        {
            var w = new BoundedVirtualWindow<int>(256, 32);
            var loader = SliceLoader(10_000, out _);
            w.EnsureWindow(0, 50, 10_000, loader);
            Assert.True(w.TryGet(5, out var a) && a == 5);
            Assert.False(w.TryGet(9000, out _));    // 윈도우 밖
            Assert.False(w.TryGet(-1, out _));      // 음수

            var w0 = new BoundedVirtualWindow<int>(64, 8);
            bool loaderCalled = false;
            w0.EnsureWindow(0, 50, 0, (s, c) => { loaderCalled = true; return Array.Empty<int>(); });
            Assert.Equal(0, w0.Loaded);
            Assert.False(w0.TryGet(0, out _));      // total=0 → fail-closed
            Assert.False(loaderCalled);             // 빈 집합 → loader 미호출
        }

        // ══ ⑤ δ=0: 히트 EnsureWindow + TryGet 핫패스 힙 0 ═════════════════════
        [Fact]
        public void Hit_And_TryGet_ZeroAllocation()
        {
            var w = new BoundedVirtualWindow<int>(256, 32);
            var page = new List<int>(256);
            for (int i = 0; i < 256; i++) page.Add(i);
            Func<int, int, IReadOnlyList<int>> loader = (s, c) => page;   // 1회 할당(히트 시 미호출)
            w.EnsureWindow(0, 50, 10_000, loader);   // 최초 1회 page-fault(측정 밖)
            for (int k = 0; k < 50_000; k++) { w.EnsureWindow(0, 50, 10_000, loader); _ = w.TryGet(k & 63, out _); }  // 워밍업
            long Measure()
            {
                long b = GC.GetAllocatedBytesForCurrentThread();
                int acc = 0;
                for (int k = 0; k < 500_000; k++)
                {
                    w.EnsureWindow(0, 50, 10_000, loader);          // 히트(loader 미호출)
                    if (w.TryGet(k & 63, out var v)) acc += v;
                }
                GC.KeepAlive(acc);
                return GC.GetAllocatedBytesForCurrentThread() - b;
            }
            _ = Measure();
            Assert.Equal(0L, Measure());   // ★히트 EnsureWindow + TryGet 힙 0(정수 산술·배열 참조)
        }
    }
}
