// ============================================================================
// Phase94LJsInteropSafetyTests — v11.4.6 JS Interop GC 누수 & 콜백 변조 방어 검증(Think-Beyond)
//   ★DotNetObjectReference 파기 추적(GCHandle 누수 방어) + JSInvokable 토큰 검증(위조 콜백 차단) 실증.
//   ★liveArmed:false: 위조 핫키 콜백은 토큰 검증에서 거부 → 팔레트조차 열 수 없다(실 발주 경로는 애초에 0).
// ============================================================================
using System;
using System.Threading;
using MS2E.Infrastructure;
using Xunit;

namespace StockManager.Tests
{
    public sealed class Phase94LJsInteropSafetyTests
    {
        // ── §1 DotNetObjectReference GCHandle 누수 센티널 ───────────────────────
        [Fact]
        public void Sentinel_Register_Release_Balance_No_Leak()
        {
            var s = new DotNetObjectRefSentinel();
            Assert.False(s.HasLeak);
            s.Register(); s.Register();
            Assert.Equal(2L, s.ActiveHandles);
            Assert.True(s.HasLeak);                 // 등록 2·해제 0 → 누수
            s.Release(); s.Release();
            Assert.Equal(0L, s.ActiveHandles);
            Assert.False(s.HasLeak);                // 균형 → 누수 0
        }

        // ── §2 TrackedJsRef — 정확히 1회 파기 보증 ──────────────────────────────
        private sealed class FakeDisposable : IDisposable
        {
            public int DisposeCount;
            public void Dispose() => DisposeCount++;
        }

        [Fact]
        public void TrackedJsRef_Disposes_Exactly_Once()
        {
            var s = new DotNetObjectRefSentinel();
            var inner = new FakeDisposable();
            var tracked = new TrackedJsRef(inner, s);
            Assert.Equal(1L, s.ActiveHandles);      // 생성 시 등록
            Assert.False(tracked.IsDisposed);

            tracked.Dispose();
            tracked.Dispose();                       // ★이중 파기 → no-op
            Assert.True(tracked.IsDisposed);
            Assert.Equal(1, inner.DisposeCount);     // 내부 ref 정확히 1회 파기(GCHandle 해제)
            Assert.Equal(0L, s.ActiveHandles);       // Release 1회 → 누수 0
            Assert.False(s.HasLeak);
        }

        // ── §3 JSInvokable 토큰 검증기 — 위조/파기/stale 거부 ────────────────────
        [Fact]
        public void TokenVerifier_Valid_Token_Accepted_Forged_Rejected()
        {
            var v = new JSInvokableTokenVerifier();
            long token = v.Issue();
            Assert.NotEqual(0L, token);
            Assert.True(v.IsActive);
            Assert.True(v.Verify(token));            // 발급 토큰 유효
            Assert.False(v.Verify(0));               // ★0 → 거부(fail-closed)
            Assert.False(v.Verify(token + 1));       // ★위조 토큰 → 거부
            Assert.False(v.Verify(unchecked(token ^ 0x5555)));  // 변조 → 거부
        }

        [Fact]
        public void TokenVerifier_Revoke_Rejects_All()
        {
            var v = new JSInvokableTokenVerifier();
            long token = v.Issue();
            Assert.True(v.Verify(token));
            v.Revoke();                              // ★파기(컴포넌트 Dispose)
            Assert.False(v.IsActive);
            Assert.False(v.Verify(token));           // 파기 후 잔여 콜백 전량 거부
        }

        [Fact]
        public void TokenVerifier_ReIssue_Invalidates_Stale_Token()
        {
            var v = new JSInvokableTokenVerifier();
            long old = v.Issue();
            long @new = v.Issue();                   // 재발급(새 컴포넌트 인스턴스)
            Assert.NotEqual(old, @new);
            Assert.False(v.Verify(old));             // ★구 인스턴스 stale 토큰 → 거부
            Assert.True(v.Verify(@new));
            Assert.True(v.Nonce >= 2);
        }
    }

    // ── §4 적대검증(동시 + δ=0) ────────────────────────────────────────────────
    [Collection("ConcurrencyStress")]
    public sealed class Phase94LJsInteropConcurrencyTests
    {
        [Fact]
        public void Sentinel_Concurrent_Register_Release_Balances_To_Zero()
        {
            // ★다중 스레드가 등록/해제를 폭주시켜도 균형은 정확히 0(누수 오검출 없음).
            var s = new DotNetObjectRefSentinel();
            const int threads = 12, perThread = 100_000;
            var ths = new Thread[threads];
            for (int t = 0; t < threads; t++)
                ths[t] = new Thread(() => { for (int i = 0; i < perThread; i++) { s.Register(); s.Release(); } });
            foreach (var th in ths) th.Start();
            foreach (var th in ths) Assert.True(th.Join(30_000));
            Assert.Equal(0L, s.ActiveHandles);
            Assert.False(s.HasLeak);
            Assert.Equal((long)threads * perThread, s.TotalRegistered);
        }

        [Fact]
        public void Verify_And_Register_ZeroAllocation()
        {
            long alloc = 0; Exception? err = null;
            var v = new JSInvokableTokenVerifier();
            var s = new DotNetObjectRefSentinel();
            long token = v.Issue();
            var th = new Thread(() =>
            {
                try
                {
                    for (int i = 0; i < 300_000; i++) { if (!v.Verify(token)) throw new Exception("w"); s.Register(); s.Release(); }
                    long before = GC.GetAllocatedBytesForCurrentThread();
                    for (int i = 0; i < 100_000; i++) { if (!v.Verify(token)) throw new Exception("h"); s.Register(); s.Release(); }
                    alloc = GC.GetAllocatedBytesForCurrentThread() - before;
                }
                catch (Exception e) { err = e; }
            });
            th.Start();
            Assert.True(th.Join(30_000));
            Assert.Null(err);
            Assert.Equal(0L, alloc);                 // ★검증/등록/해제 핫패스 δ=0
        }
    }
}
