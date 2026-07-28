// ============================================================================
// QuantumVaultTests — v8.1.0 안티-RAM 퀀텀 금고 & Phase 83 인프라 해방 적대적 검증(100% 단언)
//   🛡️ VerifiedSecureZeroizer: 판독-검증 제로화·δ=0
//   🛡️ QuantumVaultSentinel: 주입 탐지→제로화+onBreach(테스트 안전·FailFast 미격발)·δ=0
//   🍯 HoneyKeyDecoyMesh: 유출/무결성 탐지·δ=0
//   🪝 AntiHookingCodeIntegrityGuard: 진입점 JMP 후킹 탐지(주입 판독기)·δ=0
//   🧹 InfraLiberationSlice2: RCU 스냅샷 δ=0·포트 어댑터 등가
//   🔒 liveArmed:false: 브리치/후킹 시에도 이중키 전 발주 0
// ============================================================================
using System;
using System.Collections.Generic;
using System.Threading;
using StockManager.Common;
using Xunit;

namespace StockManager.Tests
{
    [Collection("ConcurrencyStress")]   // 타이밍/δ=0/스레드 — master 병렬 플래키 0% 방어
    public sealed class QuantumVaultTests
    {
        // ══ 🛡️ VerifiedSecureZeroizer ═══════════════════════════════════════
        [Fact]
        public void Zeroizer_Wipes_And_VerifiesReadback()
        {
            var z = new VerifiedSecureZeroizer();
            var buf = new byte[64];
            for (int i = 0; i < buf.Length; i++) buf[i] = (byte)(i + 1);   // 비-0 시크릿 모사
            Assert.True(z.Zeroize(buf));                    // ★와이프 + 판독 증명 통과
            foreach (var b in buf) Assert.Equal(0, b);      // 전 바이트 0x00
            Assert.Equal(0L, z.VerificationFailures);       // 잔류 0
            Assert.True(z.BytesWiped >= 64);

            var chars = "sk_live_SECRET_ABCDEF".ToCharArray();
            Assert.True(z.ZeroizeChars(chars));
            foreach (var c in chars) Assert.Equal('\0', c);
        }

        [Fact]
        public void Zeroizer_ZeroAllocation()
        {
            var z = new VerifiedSecureZeroizer();
            var buf = new byte[128];
            for (int i = 0; i < 10_000; i++) z.Zeroize(buf);
            long Measure()
            {
                long b0 = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < 200_000; i++) z.Zeroize(buf);
                return GC.GetAllocatedBytesForCurrentThread() - b0;
            }
            _ = Measure();
            Assert.Equal(0L, Measure());   // ★제로화+판독 검증 힙 0
        }

        // ══ 🛡️ QuantumVaultSentinel (주입 탐지·테스트 안전) ═════════════════
        [Fact]
        public void Vault_BreachDetected_ZeroizesSecrets_InvokesOnBreach_NoFailFast()
        {
            bool tripped = false; string? reason = null;
            bool debuggerFake = false;
            using var vault = new QuantumVaultSentinel(
                onBreach: r => { tripped = true; reason = r; },   // ★FailFast 대신 플래그(테스트 안전)
                debuggerDetector: () => debuggerFake);
            var secret = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
            var charSecret = "TOKEN_XYZ".ToCharArray();
            vault.Register(secret); vault.RegisterChars(charSecret);

            vault.CheckOnce();
            Assert.False(vault.IsBreached);   // 탐지 없음 → 무브리치
            Assert.False(tripped);

            debuggerFake = true;              // 디버거 댓치 모사
            vault.CheckOnce();
            Assert.True(vault.IsBreached);    // ★브리치
            Assert.True(tripped);
            Assert.NotNull(reason);
            foreach (var b in secret) Assert.Equal(0, b);       // ★시크릿 원자 제로화
            foreach (var c in charSecret) Assert.Equal('\0', c);
            Assert.True(vault.ZeroizedBuffers >= 2);
            Assert.Equal(1L, vault.Detections);

            vault.CheckOnce();                // 1회 래치 — 재격발 없음
            Assert.Equal(1L, vault.Detections);
        }

        [Fact]
        public void Vault_DetectorException_FailSafe_NoBreach()
        {
            bool tripped = false;
            using var vault = new QuantumVaultSentinel(
                onBreach: _ => tripped = true,
                debuggerDetector: () => throw new InvalidOperationException());
            vault.CheckOnce();
            Assert.False(vault.IsBreached);   // ★탐지 예외 → fail-safe(오탐 자멸 방지)
            Assert.False(tripped);
        }

        [Fact]
        public void Vault_CheckOnce_ZeroAllocation_CleanPath()
        {
            using var vault = new QuantumVaultSentinel(onBreach: _ => { }, debuggerDetector: () => false);
            for (int i = 0; i < 10_000; i++) { vault.CheckOnce(); _ = vault.IsBreached; _ = vault.IsArmed; }
            long Measure()
            {
                long b0 = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < 200_000; i++) { vault.CheckOnce(); _ = vault.IsBreached; }
                return GC.GetAllocatedBytesForCurrentThread() - b0;
            }
            _ = Measure();
            Assert.Equal(0L, Measure());   // ★탐지 술어 false 경로 힙 0(브리치 경로만 할당)
        }

        [Fact]
        public void Vault_BackgroundThread_Breaches_NoCrash()
        {
            bool tripped = false; bool fake = false;
            using var vault = new QuantumVaultSentinel(onBreach: _ => tripped = true, debuggerDetector: () => fake, intervalMs: 20);
            vault.Start();
            Assert.False(vault.IsBreached);
            fake = true;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (!vault.IsBreached && sw.ElapsedMilliseconds < 3000) Thread.Sleep(20);
            Assert.True(vault.IsBreached);
            Assert.True(tripped);
        }

        // ══ 🍯 HoneyKeyDecoyMesh ════════════════════════════════════════════
        [Fact]
        public void Honey_CleanIntegrity_ExfilDetection()
        {
            var mesh = new HoneyKeyDecoyMesh(decoyCount: 8);
            Assert.True(mesh.CheckIntegrity());       // 무변조 → 정상
            Assert.False(mesh.IsTripped);
            Assert.Equal(8, mesh.DecoyCount);

            string decoy = mesh.DecoyToken(3);        // 배치된 미끼
            Assert.StartsWith("HONEY_DECOY_", decoy);
            Assert.False(mesh.WasExfiltrated("normal_value"));   // 무관값 무반응
            Assert.False(mesh.IsTripped);
            Assert.True(mesh.WasExfiltrated(decoy));  // ★미끼 유출 → 침해 증명·트립
            Assert.True(mesh.IsTripped);
            Assert.True(mesh.ExfilHits >= 1);
        }

        [Fact]
        public void Honey_ZeroAllocation()
        {
            var mesh = new HoneyKeyDecoyMesh(8);
            for (int i = 0; i < 10_000; i++) { mesh.CheckIntegrity(); mesh.WasExfiltrated("nope_not_a_decoy_value"); }
            long Measure()
            {
                long b0 = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < 200_000; i++) { mesh.CheckIntegrity(); mesh.WasExfiltrated("nope_not_a_decoy_value"); }
                return GC.GetAllocatedBytesForCurrentThread() - b0;
            }
            _ = Measure();
            Assert.Equal(0L, Measure());
        }

        // ══ 🪝 AntiHookingCodeIntegrityGuard (주입 판독기) ═══════════════════
        [Fact]
        public void AntiHook_CleanVsHooked_EntryByte()
        {
            // 정상 진입점(0x8B mov edi,edi) → 무탐지.
            var clean = new AntiHookingCodeIntegrityGuard((_, _) => 0x8B);
            Assert.True(clean.CheckIntegrity());
            Assert.False(clean.IsTampered);
            Assert.Equal(0L, clean.HookStateCode);
            Assert.False(clean.SafeHoldActive);

            // 인라인 후킹(0xE9 near JMP) → 변조 트립·fail-closed 무장 하향.
            var hooked = new AntiHookingCodeIntegrityGuard((_, _) => 0xE9);
            Assert.False(hooked.CheckIntegrity());
            Assert.True(hooked.IsTampered);
            Assert.True(hooked.SafeHoldActive);     // ★blockWhile tighten-only
            Assert.Equal(1L, hooked.HookStateCode);
            Assert.True(hooked.TamperHits >= 1);

            // 판독 미가용(null) → 무탐지(안심 강등).
            var na = new AntiHookingCodeIntegrityGuard((_, _) => null);
            Assert.True(na.CheckIntegrity());
            Assert.False(na.IsTampered);
        }

        // ══ 🧹 InfraLiberationSlice2: RCU δ=0 · 포트 어댑터 ═════════════════
        private sealed record CfgSnap(int MaxOrders, double RiskPct);

        [Fact]
        public void Rcu_AtomicSnapshot_SwapVersioned_ZeroAllocRead()
        {
            var port = new AtomicConfigSnapshotPort<CfgSnap>(new CfgSnap(10, 0.5));
            Assert.Equal(10, port.Current.MaxOrders);
            Assert.Equal(0L, port.Version);
            var prev = port.Swap(new CfgSnap(20, 1.0));   // RCU 원자 교체
            Assert.Equal(10, prev.MaxOrders);
            Assert.Equal(20, port.Current.MaxOrders);     // ★최신 스냅샷
            Assert.Equal(1L, port.Version);

            for (int i = 0; i < 10_000; i++) { _ = port.Current.MaxOrders; }
            long Measure()
            {
                long b0 = GC.GetAllocatedBytesForCurrentThread();
                int acc = 0;
                for (int i = 0; i < 200_000; i++) acc += port.Current.MaxOrders;
                return GC.GetAllocatedBytesForCurrentThread() - b0;
            }
            _ = Measure();
            Assert.Equal(0L, Measure());   // ★핫패스 스냅샷 읽기 힙 0(Volatile.Read)
        }

        [Fact]
        public void Ports_InMemory_And_Engine_Adapters_Equivalent()
        {
            var mem = new InMemoryConfigAdapter(new Dictionary<string, string> { ["A.Flag"] = "1", ["A.Num"] = "3.5" });
            Assert.True(mem.GetBool("A.Flag", false));
            Assert.Equal(3.5, mem.GetDouble("A.Num", 0, 0, 10));
            Assert.Equal("def", mem.GetString("Missing", "def"));
            Assert.Equal(2.0, mem.GetDouble("A.Num", 0, 0, 2));    // clamp 3.5 → max 2

            var cfg = new RuntimeConfig(k => k == "B.Flag" ? "true" : null);
            var eng = new EngineConfigAdapter(cfg);
            Assert.True(eng.GetBool("B.Flag", false));
            Assert.False(eng.GetBool("Missing", false));

            var fs = new InMemoryFileSystemAdapter();
            Assert.False(fs.FileExists("x.txt"));
            Assert.True(fs.TryWriteAllText("x.txt", "hello"));
            Assert.True(fs.FileExists("x.txt"));
            Assert.Equal("hello", fs.ReadAllText("x.txt", "none"));
            Assert.Equal("none", fs.ReadAllText("missing.txt", "none"));
        }

        // ══ 🔒 liveArmed:false 아키텍처 격리 ════════════════════════════════
        [Fact]
        public void Isolation_BreachAndHook_NoEmission_UntilArmed()
        {
            // ★퀀텀 금고 브리치(→ ForceFreeze) 또는 후킹(→ blockWhile)이 있어도 실물 발주 0.
            var att = new OrderAttestationService();
            // 브리치 onBreach 를 ForceFreeze 로 결선(프로덕션 패턴) — 세 벽 동시 차단.
            using var vault = new QuantumVaultSentinel(onBreach: r => att.ForceFreeze("퀀텀 금고: " + r), debuggerDetector: () => true);
            vault.CheckOnce();
            Assert.Equal(GatingMode.EmergencyFreeze, att.Mode);   // ★브리치 → 비상 동결

            var ring = new OrderDispatchRing(8);
            var disp = new GatedOrderDispatcher(ring, isArmed: () => true, attestation: att);
            ring.Publish(genomeId: 0, symbolCode: 5930, OrderSide.Buy, kind: 1, QPrice.Of(100m), QQty.Of(1m), 100);
            int forwarded = 0;
            disp.Drain(_ => forwarded++);
            Assert.Equal(0, forwarded);            // ★동결 상태 → onSend 0(발주 0비트)

            // 후킹 가드 SafeHoldActive → blockWhile tighten-only.
            var hook = new AntiHookingCodeIntegrityGuard((_, _) => 0xE9);
            hook.CheckIntegrity();
            var ign = new IgnitionLock(s => s == "SIG");
            ign.Arm(IgnitionStage.MasterIgnited, "SIG");
            var freshAtt = new OrderAttestationService();
            var gate = new LiveArmingGate(ign, freshAtt, () => true);
            var pred = DispatchArming.ResolvePredicate(wired: true, gate, blockWhile: () => hook.SafeHoldActive);
            Assert.False(pred());                  // ★후킹 → 무장 거부(tighten-only)
        }
    }
}
