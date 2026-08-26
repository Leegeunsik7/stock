// ============================================================================
// ChaosSandboxTests — v9.1.0 극단적 카오스 결함 주입 샌드박스 적대적 검증(100% 단언)
//   🧪 ChaosEngineSanitizer: 결함 주입·SafeHold·ForceFreeze·결함 효과 질의·δ=0
//   ♻️ DeterministicChaosReplayer: SplitMix64 시드 100% 재현·δ=0
//   🚦 ChaosRecoveryRateLimiter: 단계별 재인증·Thundering Herd 거부·δ=0
//   🛡️ ChaosResilientProgressGuard(Think Beyond): 스핀 상한 라이브락 탈출·이중버퍼 torn 방어·δ=0
//   🔒 liveArmed:false: 극단 결함 주입/파멸 동결 하에서도 이중키 전 발주 0
// ============================================================================
using System;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace StockManager.Tests
{
    [Collection("ConcurrencyStress")]   // 타이밍/δ=0/스레드 — master 병렬 플래키 0% 방어
    public sealed class ChaosSandboxTests
    {
        private const long Freq = 10_000_000L;
        private static readonly long CE_JITTER  = ChaosEngineSanitizer.F_SOCKET_JITTER;
        private static readonly long CE_MEMORY  = ChaosEngineSanitizer.F_MEMORY_PRESSURE;
        private static readonly long CE_THERMAL = ChaosEngineSanitizer.F_THERMAL_DROP;
        private static readonly long CE_TORN    = ChaosEngineSanitizer.F_TORN_READ;

        // ══ 🧪 ChaosEngineSanitizer ═════════════════════════════════════════
        [Fact]
        public void Chaos_Inject_SafeHold_Clear_And_Catastrophic_ForceFreeze()
        {
            int freezes = 0;
            var chaos = new ChaosEngineSanitizer(onForceFreeze: () => freezes++, decisionSeed: 42);
            Assert.False(chaos.ChaosActive);
            Assert.False(chaos.SafeHoldActive);
            Assert.Equal(0L, chaos.StateCode);

            // 경미한 결함 → SafeHold(가역·tighten-only)·파멸 아님.
            chaos.Inject(CE_JITTER, intensityPermille: 300);
            Assert.True(chaos.ChaosActive);
            Assert.True(chaos.SafeHoldActive);           // ★무장 하향
            Assert.Equal(1L, chaos.StateCode);
            Assert.Equal(CE_JITTER, chaos.FaultMask);
            Assert.Equal(1L, chaos.Injections);
            Assert.Equal(0L, chaos.ForceFreezes);
            Assert.Equal(0, freezes);

            // 파멸 강도(≥950) → 원자 하드 동결(onForceFreeze 격발).
            chaos.Inject(CE_THERMAL | CE_MEMORY, intensityPermille: 1000);
            Assert.Equal(1L, chaos.ForceFreezes);
            Assert.Equal(1, freezes);
            Assert.Equal(CE_THERMAL | CE_MEMORY, chaos.FaultMask);

            // 해제 → SafeHold 가역 복원.
            chaos.Clear();
            Assert.False(chaos.ChaosActive);
            Assert.False(chaos.SafeHoldActive);
            Assert.Equal(0L, chaos.FaultMask);
        }

        [Fact]
        public void Chaos_FaultEffect_Queries_Deterministic()
        {
            var chaos = new ChaosEngineSanitizer(decisionSeed: 7);
            // 결함 없음 → 무효과(안전 기본).
            Assert.False(chaos.ShouldDropPacket());
            Assert.Equal(0L, chaos.InjectedJitterNanos());
            Assert.Equal(0L, chaos.MemoryPressureDelayNanos());
            Assert.Equal(1000L, chaos.ThermalFreqPermille());       // 1000 = 정상
            Assert.False(chaos.ShouldStallSeqlockRead());

            // 소켓 지터 100%(1000‰) → 항상 드롭(fail-safe=미발송).
            chaos.Inject(CE_JITTER, 1000);
            Assert.True(chaos.ShouldDropPacket());
            Assert.True(chaos.PacketDrops >= 1);
            Assert.True(chaos.InjectedJitterNanos() >= 0L);
            Assert.True(chaos.JitterInjections >= 1);

            // 서멀 드롭 400‰ → 유효주파수 600‰.
            chaos.Inject(CE_THERMAL, 400);
            Assert.Equal(600L, chaos.ThermalFreqPermille());

            // 메모리 압박 → 지연 산출(≥0).
            chaos.Inject(CE_MEMORY, 800);
            Assert.True(chaos.MemoryPressureDelayNanos() >= 0L);

            // torn read 100% → 항상 stall 유발.
            chaos.Inject(CE_TORN, 1000);
            Assert.True(chaos.ShouldStallSeqlockRead());
        }

        [Fact]
        public void Chaos_Engine_ZeroAllocation()
        {
            var chaos = new ChaosEngineSanitizer(decisionSeed: 99);
            chaos.Inject(ChaosEngineSanitizer.F_ALL, 500);
            for (int i = 0; i < 10_000; i++) { _ = chaos.ShouldDropPacket(); _ = chaos.ThermalFreqPermille(); }
            long Measure()
            {
                long b = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < 200_000; i++)
                {
                    _ = chaos.ShouldDropPacket(); _ = chaos.InjectedJitterNanos();
                    _ = chaos.MemoryPressureDelayNanos(); _ = chaos.ThermalFreqPermille(); _ = chaos.ShouldStallSeqlockRead();
                }
                return GC.GetAllocatedBytesForCurrentThread() - b;
            }
            _ = Measure(); Assert.Equal(0L, Measure());   // ★결함 주입 질의 핫패스 힙 0
        }

        // ══ ♻️ DeterministicChaosReplayer ═══════════════════════════════════
        [Fact]
        public void Replayer_SameSeed_100pct_Reproducible_DiffSeed_Differs()
        {
            var a = new DeterministicChaosReplayer(seed: 0xDEADBEEF, maxDelayTicks: 1_000_000);
            var b = new DeterministicChaosReplayer(seed: 0xDEADBEEF, maxDelayTicks: 1_000_000);
            for (int i = 0; i < 32; i++)
            {
                var ea = a.Next();
                var eb = b.Next();
                Assert.Equal(ea.DelayTicks, eb.DelayTicks);          // ★동일 시드 → 동일 시퀀스 100%
                Assert.Equal(ea.FaultMask, eb.FaultMask);
                Assert.Equal(ea.IntensityPermille, eb.IntensityPermille);
                Assert.Equal(ea.Catastrophic, eb.Catastrophic);
                Assert.True(ea.FaultMask != 0L);                     // ≥1 비트 보장(빈 결함 없음)
                Assert.InRange(ea.IntensityPermille, 0L, 1000L);
            }
            Assert.Equal(32L, a.Generated);

            // Reset(동일 시드) → 처음부터 동일 재현.
            a.Reset(0xDEADBEEF);
            var first = a.Next();
            b.Reset(0xDEADBEEF);
            var firstB = b.Next();
            Assert.Equal(first.FaultMask, firstB.FaultMask);
            Assert.Equal(first.DelayTicks, firstB.DelayTicks);

            // 다른 시드 → 시퀀스 상이(적어도 한 필드).
            var c = new DeterministicChaosReplayer(seed: 0x12345678, maxDelayTicks: 1_000_000);
            bool anyDiff = false;
            a.Reset(0xDEADBEEF);
            for (int i = 0; i < 16 && !anyDiff; i++)
            {
                var ea = a.Next(); var ec = c.Next();
                if (ea.DelayTicks != ec.DelayTicks || ea.FaultMask != ec.FaultMask || ea.IntensityPermille != ec.IntensityPermille) anyDiff = true;
            }
            Assert.True(anyDiff);
        }

        [Fact]
        public void Replayer_ZeroAllocation()
        {
            var r = new DeterministicChaosReplayer(seed: 1);
            for (int i = 0; i < 10_000; i++) _ = r.Next();
            long Measure() { long b = GC.GetAllocatedBytesForCurrentThread(); for (int i = 0; i < 200_000; i++) _ = r.Next(); return GC.GetAllocatedBytesForCurrentThread() - b; }
            _ = Measure(); Assert.Equal(0L, Measure());
        }

        // ══ 🚦 ChaosRecoveryRateLimiter ═════════════════════════════════════
        [Fact]
        public void Recovery_StagedReauth_Rejects_Herd_Then_Ready()
        {
            var rl = new ChaosRecoveryRateLimiter(requiredStages: 3, cooldownMillis: 3000, tickFrequency: Freq);
            long stage = rl.CooldownTicks / rl.RequiredStages;   // 1e7
            Assert.Equal(ChaosRecoveryRateLimiter.S_READY, rl.State);
            Assert.False(rl.RecoveryBlocking);

            rl.EnterChaos();
            Assert.Equal(ChaosRecoveryRateLimiter.S_FROZEN, rl.State);
            Assert.True(rl.RecoveryBlocking);                    // 카오스 활성 → 무장 차단

            long t0 = 1_000_000;
            rl.BeginRecovery(t0);
            Assert.Equal(ChaosRecoveryRateLimiter.S_COOLDOWN, rl.State);
            Assert.True(rl.RecoveryBlocking);                    // 쿨다운 중 여전히 차단

            // ★Thundering Herd: 너무 이른 재인증 → 거부.
            Assert.False(rl.AttemptReauth(t0 + stage / 2));
            Assert.True(rl.Rejects >= 1);
            Assert.Equal(0L, rl.StagesPassed);

            // 단계별 간격 준수 → 통과.
            Assert.True(rl.AttemptReauth(t0 + stage));
            Assert.Equal(1L, rl.StagesPassed);
            Assert.True(rl.RecoveryBlocking);
            Assert.True(rl.AttemptReauth(t0 + 2 * stage));
            Assert.Equal(2L, rl.StagesPassed);
            Assert.True(rl.RecoveryBlocking);
            Assert.True(rl.AttemptReauth(t0 + 3 * stage));       // 마지막 단계 → Ready
            Assert.Equal(ChaosRecoveryRateLimiter.S_READY, rl.State);
            Assert.False(rl.RecoveryBlocking);                   // ★완전 복구 → 무장 재개 허용
            Assert.Equal(1L, rl.Completions);
            Assert.Equal(1L, rl.ChaosEntries);
            // ★Ready 후 재인증/오버슛 봉쇄: 큰 tick 재인증도 무효·Completions 단조 유일·StagesPassed 상한 불변.
            Assert.False(rl.AttemptReauth(t0 + 100 * stage));
            Assert.Equal(1L, rl.Completions);                    // 중복 완료 없음
            Assert.Equal((long)rl.RequiredStages, rl.StagesPassed); // StagesPassed ≤ RequiredStages(오버슛 없음)
        }

        [Fact]
        public void Recovery_Reauth_Invalid_Outside_Cooldown()
        {
            var rl = new ChaosRecoveryRateLimiter(requiredStages: 2, cooldownMillis: 1000, tickFrequency: Freq);
            Assert.False(rl.AttemptReauth(1_000));               // Ready 상태 재인증 무효
            rl.EnterChaos();
            Assert.False(rl.AttemptReauth(1_000));               // Frozen 상태 재인증 무효(BeginRecovery 전)
        }

        [Fact]
        public async Task Recovery_ConcurrentHerd_Advances_At_Most_One_Stage_Per_Interval()
        {
            // ★Thundering Herd TOCTOU 방어: 동일 인터벌 경계에서 다수 스레드가 재인증을 쇄도(각 다회 반복)해도 단 1단계만
            //   전진(CAS 원자 전진 + 시간 게이트). 조기 재무장 물리 봉쇄. 소수 워커·짧은 반복으로 풀 부하 최소화(병렬 스위트 무간섭).
            var rl = new ChaosRecoveryRateLimiter(requiredStages: 5, cooldownMillis: 5000, tickFrequency: Freq);
            long stage = rl.CooldownTicks / rl.RequiredStages;
            rl.EnterChaos();
            rl.BeginRecovery(0);
            int workers = Math.Min(4, Environment.ProcessorCount);
            var tasks = new Task[workers];
            for (int w = 0; w < workers; w++)
                tasks[w] = Task.Run(() => { for (int k = 0; k < 5000; k++) rl.AttemptReauth(stage); });   // 첫 인터벌 t=stage 에 쇄도
            await Task.WhenAll(tasks);
            Assert.Equal(1L, rl.StagesPassed);                   // ★수만 회 동시 쇄도에도 정확히 1단계(조기 재무장 없음)
            Assert.True(rl.RecoveryBlocking);                    // 아직 복구 미완(5단계 중 1)
            Assert.True(rl.Rejects >= 1L);                       // 나머지 쇄도는 거부(CAS 패배/시간 게이트)
        }

        // ══ 🛡️ ChaosResilientProgressGuard (Think Beyond) ═══════════════════
        [Fact]
        public void Guard_SpinFence_Bounded_Livelock_Escape()
        {
            var g = new ChaosResilientProgressGuard(spinCeiling: 8);
            var fence = g.NewFence();
            int n = 0;
            while (fence.Retry()) n++;                            // 상한 8 → 8회 후 false(O(1) 탈출)
            Assert.Equal(8, n);
            Assert.Equal(8, fence.Spins);

            Assert.False(g.SafeHoldActive);
            g.OnLivelockEscape();                                 // 라이브락 → 계수 + 가역 홀드
            Assert.Equal(1L, g.LivelockEscapes);
            Assert.True(g.SafeHoldActive);                        // ★무장 하향(fail-closed)
            g.Reset();
            Assert.False(g.SafeHoldActive);                      // 가역 홀드 해제
            Assert.Equal(1L, g.LivelockEscapes);                 // 카운터는 유지
        }

        [Fact]
        public void Guard_DoubleBuffer_Commit_Monotonic_And_Consistent()
        {
            var g = new ChaosResilientProgressGuard(spinCeiling: 1024);
            Assert.True(g.TryRead(out var s0));
            Assert.Equal(0L, s0.Epoch);

            Assert.True(g.TryCommit(1, 5, -5));
            Assert.True(g.TryRead(out var s1));
            Assert.Equal(1L, s1.Epoch);
            Assert.Equal(5L, s1.PayloadA);
            Assert.Equal(-5L, s1.PayloadB);

            Assert.True(g.TryCommit(1, 7, -7));
            Assert.True(g.TryRead(out var s2));
            Assert.Equal(2L, s2.Epoch);                          // ★에폭 단조 증가(원자 발행)
            Assert.Equal(0L, s2.PayloadA + s2.PayloadB);         // 불변식 A+B=0 유지
            Assert.Equal(2L, g.Commits);
            Assert.Equal(0L, g.TornStateBlocks);
        }

        [Fact]
        public async Task Guard_TornState_Never_Observed_Under_Concurrent_Commit()
        {
            // ★단일 라이터 커밋(SPSC) + 다중 독자: 독자는 절대 부분/찢어진 상태를 보지 않는다(원자 이중버퍼 발행).
            var g = new ChaosResilientProgressGuard(spinCeiling: 4096);
            using var cts = new CancellationTokenSource();
            var writer = Task.Run(() =>
            {
                long e = 0;
                while (!cts.IsCancellationRequested) { e++; g.TryCommit(1, e, -e); }   // 불변식 A+B=0
            });
            bool torn = false;
            for (int i = 0; i < 300_000 && !torn; i++)
                if (g.TryRead(out var s) && (s.PayloadA + s.PayloadB) != 0L) torn = true;   // torn 이면 A+B≠0
            cts.Cancel();
            await writer;
            Assert.False(torn);                                  // ★torn 상태 0회 관측
            Assert.True(g.Commits > 0L);
        }

        [Fact]
        public void Guard_ZeroAllocation()
        {
            var g = new ChaosResilientProgressGuard(spinCeiling: 16);
            for (int i = 0; i < 10_000; i++) { g.TryCommit(1, i, -i); _ = g.TryRead(out _); }
            long Measure()
            {
                long b = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < 200_000; i++)
                {
                    var f = g.NewFence(); _ = f.Retry();
                    g.TryCommit(1, i, -i);
                    _ = g.TryRead(out _);
                }
                return GC.GetAllocatedBytesForCurrentThread() - b;
            }
            _ = Measure(); Assert.Equal(0L, Measure());   // ★커밋/독출/펜스 핫패스 힙 0
        }

        // ══ 🔒 liveArmed:false 아키텍처 격리 ════════════════════════════════
        [Fact]
        public void Isolation_ChaosInjected_NoEmission_UntilArmed()
        {
            // ★극단 결함 주입 중에는 무장 술어가 tighten 되어 실 사출 0(단 1비트 오발주 누수 봉쇄).
            var att   = new OrderAttestationService();
            var ring  = new OrderDispatchRing(8);
            var chaos = new ChaosEngineSanitizer();
            var recov = new ChaosRecoveryRateLimiter(requiredStages: 2, cooldownMillis: 2000, tickFrequency: Freq);
            var guard = new ChaosResilientProgressGuard(spinCeiling: 8);
            // BlazorHomeForm blockWhile 미러: 카오스 SafeHold ∨ 복구 미완 ∨ 가드 홀드 → 미무장.
            Func<bool> armed = () => !chaos.SafeHoldActive && !recov.RecoveryBlocking && !guard.SafeHoldActive;
            var disp = new GatedOrderDispatcher(ring, isArmed: armed, attestation: att);

            chaos.Inject(CE_JITTER | CE_TORN, intensityPermille: 700);   // 결함 주입 → SafeHold
            Assert.True(chaos.SafeHoldActive);
            ring.Publish(genomeId: 0, symbolCode: 5930, OrderSide.Buy, (byte)0, QPrice.Of(100m), QQty.Of(1m), 100);
            int forwarded = 0;
            disp.Drain(_ => forwarded++);
            Assert.Equal(0, forwarded);                          // ★카오스 주입 중 발주 0
            Assert.Equal(0L, disp.Forwarded);
            Assert.True(disp.GatedOut >= 1);

            // 라이브락 감지도 tighten(가드 홀드).
            chaos.Clear();
            guard.OnLivelockEscape();
            Assert.True(guard.SafeHoldActive);
            ring.Publish(genomeId: 0, symbolCode: 5930, OrderSide.Buy, (byte)0, QPrice.Of(100m), QQty.Of(1m), 101);
            int forwarded2 = 0;
            disp.Drain(_ => forwarded2++);
            Assert.Equal(0, forwarded2);                         // ★라이브락 감지 중에도 발주 0
        }

        [Fact]
        public void Isolation_CatastrophicChaos_ForceFreeze_HardBlocks_Drain()
        {
            // ★파멸 결함 → attestation.ForceFreeze → 디스패처 early-out 0(무장 여부 무관 하드 동결).
            var att   = new OrderAttestationService();
            var ring  = new OrderDispatchRing(8);
            var chaos = new ChaosEngineSanitizer(onForceFreeze: () => att.ForceFreeze("chaos catastrophic test"));
            var disp  = new GatedOrderDispatcher(ring, isArmed: () => true, attestation: att);   // 완전 무장이어도

            chaos.Inject(CE_THERMAL, intensityPermille: 1000);   // 파멸 강도 → ForceFreeze
            Assert.Equal(GatingMode.EmergencyFreeze, att.Mode);
            ring.Publish(genomeId: 0, symbolCode: 5930, OrderSide.Buy, (byte)0, QPrice.Of(100m), QQty.Of(1m), 200);
            int forwarded = 0;
            disp.Drain(_ => forwarded++);
            Assert.Equal(0, forwarded);                          // ★파멸 동결 → 사출 0(하드 봉쇄)
        }
    }
}
