// ============================================================================
// Phase94QuantInterlockTests — Phase 94-C QuantComputeInterlock 적대적 검증(100% 단언, Think-Beyond)
//   비동기 퀀트 컴퓨트의 두 파멸 리스크를 인터락이 실제로 봉쇄하는지 지상 검증:
//     ① Uncanceled Compute Task Leak — Begin() 이 이전 토큰 취소·Dispose() 가 진행 토큰 취소
//     ② Disposed / Stale Component State Mutation — TryApply 가 파기·옛 세대 결과를 '드롭'(fail-closed)
//   ⚡ δ=0: TryApply 가드 핫패스 힙 0(Volatile/Interlocked·캐시 델리게이트 invoke)
//   🧵 경합: Interlocked.Increment 세대 손실 0(2,000 병렬 Begin → 정확히 2,000)
// ============================================================================
using System;
using System.Threading;
using System.Threading.Tasks;
using MS2E.UI.Portable.Infrastructure;
using Xunit;

namespace StockManager.Tests
{
    [Collection("ConcurrencyStress")]
    public sealed class Phase94QuantInterlockTests
    {
        // ══ ① 취소: 이전 연산·파기 토큰 취소(유출 차단) ═══════════════════════
        [Fact]
        public void Begin_Cancels_Previous_Dispose_Cancels_Current()
        {
            var il = new QuantComputeInterlock();

            var (t1, g1) = il.Begin();
            Assert.False(t1.IsCancellationRequested);   // 새 토큰은 미취소
            Assert.Equal(1L, g1);
            Assert.Equal(1L, il.Generation);

            var (t2, g2) = il.Begin();
            Assert.True(t1.IsCancellationRequested);     // ★이전 연산 토큰 즉시 취소(백그라운드 조기 중단)
            Assert.False(t2.IsCancellationRequested);
            Assert.True(g2 > g1);

            il.Dispose();
            Assert.True(t2.IsCancellationRequested);      // ★파기 시 진행 중 토큰 취소
            Assert.True(il.IsDisposed);
        }

        // ══ ② 드롭: 현재 세대만 적용 · stale 는 드롭(last-writer-wins) ════════
        [Fact]
        public void TryApply_Applies_Current_Drops_Stale()
        {
            var il = new QuantComputeInterlock();
            int applied = 0; string winner = "";

            var (_, g1) = il.Begin();     // 연산 1 시작
            var (_, g2) = il.Begin();     // 연산 2 시작 → 연산 1을 stale 로 강등

            // 연산 1이 '늦게' 끝나 결과 적용 시도 → 드롭(옛 세대)
            Assert.False(il.TryApply(g1, () => { applied++; winner = "stale"; }));
            // 연산 2가 끝나 적용 → 반영(현재 세대)
            Assert.True(il.TryApply(g2, () => { applied++; winner = "fresh"; }));

            Assert.Equal(1, applied);      // ★단 1회만 반영(옛 연산 덮어쓰기 0)
            Assert.Equal("fresh", winner); // ★마지막 연산 승리
        }

        // ══ ② 드롭: 파기된 컴포넌트 상태 변조 차단 ════════════════════════════
        [Fact]
        public void TryApply_After_Dispose_Drops()
        {
            var il = new QuantComputeInterlock();
            var (_, g) = il.Begin();
            il.Dispose();

            bool ran = false;
            Assert.False(il.TryApply(g, () => ran = true));   // ★파기 후 결과 적용 시도 → 드롭
            Assert.False(ran);                                 // 파기 UI 상태 변조 0
        }

        // ══ 파기 후 Begin: 이미-취소 토큰 + 세대 -1 (호출부 즉시 no-op) ═══════
        [Fact]
        public void Begin_After_Dispose_Returns_Cancelled_And_NegGeneration()
        {
            var il = new QuantComputeInterlock();
            il.Dispose();

            var (t, g) = il.Begin();
            Assert.True(t.IsCancellationRequested);   // 파기 후 → 이미-취소 토큰
            Assert.Equal(-1L, g);                      // 파기 후 → 세대 -1(현재 세대와 절대 불일치)

            bool ran = false;
            Assert.False(il.TryApply(g, () => ran = true));   // -1 세대 → 드롭
            Assert.False(ran);
        }

        // ══ 방어: null 액션·이중 파기 ════════════════════════════════════════
        [Fact]
        public void TryApply_Null_False_And_Double_Dispose_Safe()
        {
            var il = new QuantComputeInterlock();
            var (_, g) = il.Begin();
            Assert.False(il.TryApply(g, null!));   // null 액션 → false(NRE 없음)

            il.Dispose();
            il.Dispose();                          // ★이중 파기 무해(재취소·재드롭 없음)
            Assert.True(il.IsDisposed);
        }

        // ══ 🧵 경합: 병렬 Begin 세대 손실 0(Interlocked.Increment 정확성) ════
        [Fact]
        public void Begin_Concurrent_Generation_Exact_No_Lost_Increment()
        {
            var il = new QuantComputeInterlock();
            const int N = 2000;
            Parallel.For(0, N, _ => { il.Begin(); });   // 반환 튜플은 표현식 문으로 폐기
            Assert.Equal((long)N, il.Generation);   // ★경합 하에서도 정확히 N(증가 손실·중복 0)
            il.Dispose();
        }

        // ══ ⚡ δ=0: TryApply 가드 핫패스 힙 0 ════════════════════════════════
        [Fact]
        public void TryApply_ZeroAllocation()
        {
            var il = new QuantComputeInterlock();
            var (_, gen) = il.Begin();
            Action noop = () => { };   // 비캡처 → 정적 캐시(측정 밖 1회)
            for (int i = 0; i < 50_000; i++) _ = il.TryApply(gen, noop);   // 워밍업
            long Measure()
            {
                long b = GC.GetAllocatedBytesForCurrentThread();
                int acc = 0;
                for (int i = 0; i < 500_000; i++) if (il.TryApply(gen, noop)) acc++;
                GC.KeepAlive(acc);
                return GC.GetAllocatedBytesForCurrentThread() - b;
            }
            _ = Measure();
            Assert.Equal(0L, Measure());   // ★가드(Volatile/Interlocked 읽기 + 캐시 델리게이트 invoke) 힙 0
            il.Dispose();
        }
    }
}
