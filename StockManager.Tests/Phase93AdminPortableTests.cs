// ============================================================================
// Phase93AdminPortableTests — v10.1.0 Phase 93-B: Admin Portable(Ports & Adapters) 적대적 검증(100% 단언)
//   🔐 AdminAuthoritySealGuard(Think Beyond): 봉인 라운드트립·rogue 위조 거부·revoke freeze-safe·δ=0
//   🚌 AdminCommandPortDispatcher: 불변 Intent 포맷+봉인 발급·미인가 미전송·δ=0
//   🕶️ SecureAdminViewPresenter: 마스킹(first3+last2)·중간 plain 미노출·버퍼 0x00·δ=0
//   🧩 AdminCommandPortAdapter: seal 검증·rogue -403·ForceFreeze -503+revoke·liveArmed:false
// ============================================================================
using System;
using MS2E.UI.Portable.Bus;
using MS2E.UI.Portable.Dto;
using MS2E.UI.Portable.Ports;
using MS2E.UI.Portable.Security;
using Xunit;

namespace StockManager.Tests
{
    [Collection("ConcurrencyStress")]   // δ=0/타이밍 — 방어적 격리
    public sealed class Phase93AdminPortableTests
    {
        private sealed class NullPort : IAdminCommandPort
        {
            public AdminCommandResult Execute(in AdminCommandIntent intent, long nonce, long seal) => new(true, 0L, 0L);
        }
        private sealed class RecordingPort : IAdminCommandPort
        {
            public bool Called; public AdminCommandIntent Intent; public long Nonce, Seal;
            public AdminCommandResult Execute(in AdminCommandIntent intent, long nonce, long seal)
            { Called = true; Intent = intent; Nonce = nonce; Seal = seal; return new AdminCommandResult(true, 1L, 0L); }
            public void Reset() => Called = false;
        }
        private sealed class StubSecret : IAdminSecretPort
        {
            private readonly string _s;
            public StubSecret(string s) => _s = s;
            public int LoadSecret(long id, Span<char> dest) { int n = Math.Min(_s.Length, dest.Length); _s.AsSpan(0, n).CopyTo(dest); return n; }
        }

        // ══ 🔐 AdminAuthoritySealGuard (Think Beyond) ══════════════════════
        [Fact]
        public void Seal_RoundTrip_Rogue_Replay_KeyDependent_Revoke()
        {
            bool purged = false;
            var g = new AdminAuthoritySealGuard(onRevoke: () => purged = true);
            Assert.False(g.Authorized);              // 미개시 → 미인가
            g.BeginSession(0x1234ABCDUL);
            Assert.True(g.Authorized);

            var intent = new AdminCommandIntent(AdminCommandKind.QuantumVaultArm, 5L, 6L, 100L);
            long seal = g.IssueSeal(in intent, nonce: 1L);
            Assert.True(g.VerifySeal(in intent, 1L, seal));       // ★정당 봉인 → 통과
            Assert.False(g.VerifySeal(in intent, 1L, seal ^ 1L)); // ★위조 봉인 → 거부(rogue)
            Assert.False(g.VerifySeal(in intent, 2L, seal));      // ★nonce 불일치 → 거부(replay)

            var g2 = new AdminAuthoritySealGuard();
            g2.BeginSession(0x9999_9999UL);
            Assert.False(g2.VerifySeal(in intent, 1L, seal));     // ★키 미지 rogue → 거부(세션 키 의존)

            g.Revoke();
            Assert.False(g.Authorized);
            Assert.False(g.VerifySeal(in intent, 1L, seal));      // ★revoke 후 전 봉인 무효(freeze-safe)
            Assert.True(purged);                                  // ★onRevoke → 퍼지 격발
            Assert.True(g.Revocations >= 1L);
        }

        [Fact]
        public void Seal_BeginSession_Zero_FailClosed()
        {
            var g = new AdminAuthoritySealGuard();
            g.BeginSession(0UL);                       // ★0 키 → 미개시(fail-closed·공개 상수 대체 없음)
            Assert.False(g.Authorized);
            var intent = new AdminCommandIntent(AdminCommandKind.QuantumVaultArm, 0L, 0L, 0L);
            long seal = g.IssueSeal(in intent, 1L);
            Assert.False(g.VerifySeal(in intent, 1L, seal));   // ★키 0 → 검증 거부(known-key 강등 봉쇄)
            g.BeginSession(0x1234UL);                  // 유효 키 재개시 → 정상
            Assert.True(g.Authorized);
        }

        [Fact]
        public void Seal_IssueVerify_ZeroAllocation()
        {
            var g = new AdminAuthoritySealGuard();
            g.BeginSession(0x1234ABCDUL);
            var intent = new AdminCommandIntent(AdminCommandKind.AiCacheInvalidate, 5L, 6L, 100L);
            long acc = 0;
            long Measure() { long b = GC.GetAllocatedBytesForCurrentThread(); for (int i = 0; i < 500_000; i++) { long s = g.IssueSeal(in intent, i); if (g.VerifySeal(in intent, i, s)) acc++; } return GC.GetAllocatedBytesForCurrentThread() - b; }
            _ = Measure();
            Assert.Equal(0L, Measure());   // ★봉인 발급/검증 핫패스 힙 0
            Assert.True(acc > 0);
        }

        // ══ 🚌 AdminCommandPortDispatcher ══════════════════════════════════
        [Fact]
        public void Dispatch_IssuesValidSeal_Unauthorized_NoSend()
        {
            var g = new AdminAuthoritySealGuard();
            g.BeginSession(0x1234ABCDUL);
            var port = new RecordingPort();
            var disp = new AdminCommandPortDispatcher(port, g);

            var r = disp.Dispatch(AdminCommandKind.QuantumVaultArm, 7L, 8L);
            Assert.True(r.Ok);
            Assert.True(port.Called);
            Assert.Equal(1L, disp.Dispatched);
            Assert.True(g.VerifySeal(in port.Intent, port.Nonce, port.Seal));   // ★dispatcher 가 정당 봉인 발급

            g.Revoke();
            port.Reset();
            var r2 = disp.Dispatch(AdminCommandKind.QuantumVaultArm);
            Assert.False(r2.Ok);
            Assert.False(port.Called);          // ★미인가 → Port 미전송(fail-closed)
            Assert.Equal(1L, disp.Rejected);
        }

        [Fact]
        public void Dispatch_ZeroAllocation()
        {
            var g = new AdminAuthoritySealGuard();
            g.BeginSession(0x1234ABCDUL);
            var disp = new AdminCommandPortDispatcher(new NullPort(), g);
            for (int i = 0; i < 50_000; i++) disp.Dispatch(AdminCommandKind.QuantumVaultArm, i, i);
            long Measure() { long b = GC.GetAllocatedBytesForCurrentThread(); for (int i = 0; i < 500_000; i++) disp.Dispatch(AdminCommandKind.QuantumVaultArm, i, i); return GC.GetAllocatedBytesForCurrentThread() - b; }
            _ = Measure();
            Assert.Equal(0L, Measure());   // ★Intent 포맷+봉인+전송 핫패스 힙 0(record struct 값타입)
        }

        // ══ 🕶️ SecureAdminViewPresenter ═══════════════════════════════════
        [Fact]
        public void Presenter_Masks_MiddleHidden_BufferZeroized()
        {
            var p = new SecureAdminViewPresenter(new StubSecret("sk-1234567890ab"));
            Span<char> dest = stackalloc char[64];
            int n = p.RenderMasked(1L, dest);
            Assert.True(n > 0);
            string masked = new string(dest[..n]);
            Assert.StartsWith("sk-", masked);          // first-3 노출
            Assert.EndsWith("ab", masked);             // last-2 노출
            Assert.Contains("*", masked);              // 중간 마스킹
            Assert.DoesNotContain("1234567890", masked); // ★중간 plain 미노출
            Assert.Equal(1L, p.Renders);
            Assert.True(p.Zeroizations >= 1L);         // ★렌더 후 plain 버퍼 0x00
        }

        [Fact]
        public void Presenter_ShortSecret_FullyMasked()
        {
            var p = new SecureAdminViewPresenter(new StubSecret("abc"));   // 3자 시크릿
            Span<char> dest = stackalloc char[64];
            int n = p.RenderMasked(1L, dest);
            string masked = new string(dest[..n]);
            Assert.Equal("***", masked);              // ★단문 전량 마스킹(전부 평문 노출 봉쇄)
            Assert.DoesNotContain("a", masked);
            Assert.DoesNotContain("c", masked);
        }

        [Fact]
        public void Presenter_RenderMasked_ZeroAllocation()
        {
            var p = new SecureAdminViewPresenter(new StubSecret("sk-1234567890ab"));
            Span<char> warm = stackalloc char[64];
            for (int i = 0; i < 50_000; i++) p.RenderMasked(1L, warm);
            long Measure()
            {
                Span<char> d = stackalloc char[64];   // ★Measure 내부 stackalloc — ref local 캡처(CS8175) 회피
                long b = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < 200_000; i++) p.RenderMasked(1L, d);
                return GC.GetAllocatedBytesForCurrentThread() - b;
            }
            _ = Measure();
            Assert.Equal(0L, Measure());   // ★마스킹 렌더 핫패스 힙 0(string 미생성·dest span 직기입)
        }

        // ══ 🧩 AdminCommandPortAdapter ════════════════════════════════════
        [Fact]
        public void Adapter_Verifies_Seal_Rogue_And_FreezeRevoke()
        {
            var g = new AdminAuthoritySealGuard();
            g.BeginSession(0x1234ABCDUL);
            var att = new OrderAttestationService();
            var adapter = new AdminCommandPortAdapter(g, att);

            var intent = new AdminCommandIntent(AdminCommandKind.QuantumVaultArm, 0L, 0L, 0L);
            long seal = g.IssueSeal(in intent, 1L);
            var r = adapter.Execute(in intent, 1L, seal);
            Assert.True(r.Ok);
            Assert.True(adapter.VaultArmed);           // 정당 봉인 → 금고 무장

            var r2 = adapter.Execute(in intent, 2L, seal + 999L);   // ★위조 봉인 → 거부
            Assert.False(r2.Ok);
            Assert.Equal(-403L, r2.Code);

            // ★ForceFreeze → adapter 가 revoke + 거부(freeze-safe).
            att.ForceFreeze("test freeze");
            var intent3 = new AdminCommandIntent(AdminCommandKind.QuantumVaultDisarm, 0L, 0L, 0L);
            long seal3 = g.IssueSeal(in intent3, 3L);
            var r3 = adapter.Execute(in intent3, 3L, seal3);
            Assert.False(r3.Ok);
            Assert.Equal(-503L, r3.Code);              // 동결 → -503
            Assert.True(g.Revoked);                    // ★adapter 가 seal 가드 revoke
        }
    }
}
