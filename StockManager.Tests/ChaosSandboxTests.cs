// ============================================================================
// ChaosSandboxTests — v9.1.0 극단적 카오스 결함 주입 샌드박스 적대적 검증(100% 단언)
//   🧪 ChaosEngineSanitizer: 결함 주입·SafeHold·ForceFreeze·결함 효과 질의·δ=0
//   ♻️ DeterministicChaosReplayer: SplitMix64 시드 100% 재현·δ=0
//   🚦 ChaosRecoveryRateLimiter: 단계별 재인증·Thundering Herd 거부·δ=0
//   🛡️ ChaosResilientProgressGuard(Think Beyond): 스핀 상한 라이브락 탈출·이중버퍼 torn 방어·δ=0
//   🔒 liveArmed:false: 극단 결함 주입/파멸 동결 하에서도 이중키 전 발주 0
// ============================================================================
using System;
using System.Reflection;
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

        // ── [동시성 Triage] 결정론 하네스 ─────────────────────────────────────────
        //  원 테스트(Task.Run 라이터 + 고정 30만 회 독자 예산 + 시작 핸드셰이크 부재)의 실패 원인(전체 스위트 1회 관측,
        //  ChaosSandboxTests.cs:283 `Assert.True(g.Commits > 0L)`): 스레드풀 포화로 라이터 태스크가 독자 예산 소진 전에
        //  스케줄되지 못해 커밋 0 — torn 이 아니라 **하네스의 스케줄링 의존**. 같은 원인으로 torn 단언도 동시 실행이
        //  보장되지 않아 공허(vacuous)했다. 교정: 전용 스레드 + Barrier 동시 출발 + '예산'이 아닌 '진행' 종료 조건
        //  (라이터 정확히 K 커밋 → 독자는 라이터 완료까지 판독 → 완료 후 최종 판독이 에폭 K 를 반드시 관측).
        //  판정 불변식: A+B=0 · Epoch=PayloadA(3필드 전부 교차 검증) · 독자별 에폭 단조 · 최종 가시성.

        private sealed class ChaosRoundStats
        {
            public long Torn, NonMonotonic, Reads, FinalMismatch, Rounds, OverlapRounds;
        }

        /// <summary>독자 1회 판독 검증(δ=0). 반환 = 관측 에폭(판독 실패 시 last 유지).</summary>
        private static long CheckRead(ChaosResilientProgressGuard g, long last, ChaosRoundStats st, ref long reads)
        {
            if (!g.TryRead(out var s)) return last;
            reads++;
            if (s.PayloadA + s.PayloadB != 0L || s.Epoch != s.PayloadA) Interlocked.Increment(ref st.Torn);
            if (s.Epoch < last) Interlocked.Increment(ref st.NonMonotonic);
            return s.Epoch > last ? s.Epoch : last;
        }

        /// <summary>
        /// 영속 스레드 결정론 라운드 러너 — 1 라이터 + readers 독자를 한 번만 만들고 Barrier 로 라운드마다 동시 출발/합류.
        /// 라운드 = 새 가드 · 라이터 commitsPerRound 커밋 · 독자는 라이터 완료까지 판독 + 완료 후 최종 판독(에폭 == commitsPerRound).
        /// 종료 조건이 '진행'이라 스케줄링/부하와 무관하게 결정론(라운드 타임아웃만 상한).
        /// </summary>
        private static ChaosRoundStats RunChaosRounds(int rounds, int readers, long commitsPerRound, Action? writerPreStart = null)
        {
            var st = new ChaosRoundStats();
            ChaosResilientProgressGuard? cur = null;
            int writerDone = 0;
            long overlapThisRound = 0;
            using var start = new Barrier(readers + 2);
            using var end = new Barrier(readers + 2);
            var threads = new Thread[readers + 1];
            threads[0] = new Thread(() =>
            {
                for (int r = 0; r < rounds; r++)
                {
                    if (!start.SignalAndWait(30_000)) return;
                    writerPreStart?.Invoke();
                    var g = Volatile.Read(ref cur)!;
                    for (long e = 1; e <= commitsPerRound; e++) g.TryCommit(1, e, -e);   // 불변식 A+B=0 · Epoch=A
                    Volatile.Write(ref writerDone, 1);
                    if (!end.SignalAndWait(30_000)) return;
                }
            }) { IsBackground = true, Name = "chaos-writer" };
            for (int k = 1; k <= readers; k++)
            {
                threads[k] = new Thread(() =>
                {
                    for (int r = 0; r < rounds; r++)
                    {
                        if (!start.SignalAndWait(30_000)) return;
                        var g = Volatile.Read(ref cur)!;
                        long last = 0, reads = 0;
                        while (Volatile.Read(ref writerDone) == 0)
                            last = CheckRead(g, last, st, ref reads);
                        if (last > 0 && last < commitsPerRound) Interlocked.Exchange(ref overlapThisRound, 1);   // 라이터 진행 중 관측 = 실제 동시 실행
                        // 라이터 완료 이후 판독은 반드시 최종 에폭을 관측(발행 가시성 · 스테일 고착 0).
                        if (!g.TryRead(out var fin) || fin.Epoch != commitsPerRound) Interlocked.Increment(ref st.FinalMismatch);
                        Interlocked.Add(ref st.Reads, reads);
                        if (!end.SignalAndWait(30_000)) return;
                    }
                }) { IsBackground = true, Name = "chaos-reader" };
            }
            foreach (var t in threads) t.Start();
            for (int r = 0; r < rounds; r++)
            {
                Volatile.Write(ref cur, new ChaosResilientProgressGuard(spinCeiling: 4096));
                Volatile.Write(ref writerDone, 0);
                Interlocked.Exchange(ref overlapThisRound, 0);
                Assert.True(start.SignalAndWait(30_000), $"라운드 {r} 출발 합류 타임아웃");
                Assert.True(end.SignalAndWait(30_000), $"라운드 {r} 종료 합류 타임아웃");
                Assert.Equal(commitsPerRound, cur!.Commits);                   // 라이터 커밋 결정론(스케줄링 무관)
                st.Rounds++;
                st.OverlapRounds += Interlocked.Read(ref overlapThisRound);
            }
            foreach (var t in threads) Assert.True(t.Join(30_000));
            return st;
        }

        [Fact]
        public void Guard_TornState_Never_Observed_Under_Concurrent_Commit()
        {
            // ★단일 라이터 커밋(SPSC) + 다중 독자: 독자는 절대 부분/찢어진/미발행/역행 상태를 보지 않는다.
            //   (원 테스트의 스케줄링 의존 결함 교정판 — 위 하네스 주석 참조.)
            var st = RunChaosRounds(rounds: 1, readers: 3, commitsPerRound: 200_000);
            Assert.Equal(0L, st.Torn);
            Assert.Equal(0L, st.NonMonotonic);
            Assert.Equal(0L, st.FinalMismatch);
        }

        [Fact]
        public void Guard_LegacyHarness_WriterStarvation_ReproducedDeterministically()
        {
            // [Red 재현 · 결정론] 원 하네스 패턴에서 라이터 시작이 독자 예산 소진 뒤로 밀리면(스레드풀 포화 모사) 커밋 = 0
            //   → 원 `Assert.True(g.Commits > 0L)` 은 스케줄링만으로 실패한다(코드 결함 아님). 동시에 torn 판정도 공허.
            var g = new ChaosResilientProgressGuard(spinCeiling: 4096);
            using var release = new ManualResetEventSlim(false);
            long e = 0;
            var writer = new Thread(() => { release.Wait(); for (int i = 0; i < 1_000; i++) { e++; g.TryCommit(1, e, -e); } }) { IsBackground = true };
            writer.Start();
            long distinctEpochs = 0, lastEpoch = -1;
            for (int i = 0; i < 300_000; i++)
                if (g.TryRead(out var s) && s.Epoch != lastEpoch) { distinctEpochs++; lastEpoch = s.Epoch; }
            long commitsAtBudgetEnd = g.Commits;
            release.Set();
            Assert.True(writer.Join(30_000));
            Assert.Equal(0L, commitsAtBudgetEnd);   // ★원 단언이 실패하는 정확한 상태
            Assert.Equal(1L, distinctEpochs);       // ★독자는 에폭 0 만 봤다 — torn '0회' 가 아무것도 증명하지 못함

            // 교정 하네스는 같은 기아(라이터 지연 출발)에서도 결정론 — 진행 종료 조건이라 커밋 K·최종 가시성 보장.
            var st = RunChaosRounds(rounds: 3, readers: 2, commitsPerRound: 1_000, writerPreStart: () => Thread.Sleep(20));
            Assert.Equal(0L, st.Torn);
            Assert.Equal(0L, st.NonMonotonic);
            Assert.Equal(0L, st.FinalMismatch);
            Assert.Equal(3L, st.Rounds);
        }

        [Fact]
        public void Guard_1000Rounds_Stress_ZeroTorn_ZeroRegression_FinalVisible()
        {
            // ★지시 불변식: 동시성 카오스 1,000 회 연속 — flaky 0 · torn 0 · 에폭 역행 0 · 최종 가시성 1,000/1,000.
            var st = RunChaosRounds(rounds: 1_000, readers: 2, commitsPerRound: 2_000);
            Assert.Equal(1_000L, st.Rounds);
            Assert.Equal(0L, st.Torn);
            Assert.Equal(0L, st.NonMonotonic);
            Assert.Equal(0L, st.FinalMismatch);
            Assert.True(st.Reads > 0);
        }

        // ── [방향 B] 발행 인덱스 검증형 seqlock — stale 인덱스 결정론 재현(선점 모사 seam) ──────────

        private static (ChaosCheckpoint[] Slots, long[] Vers) Internals(ChaosResilientProgressGuard g)
        {
            const BindingFlags F = BindingFlags.NonPublic | BindingFlags.Instance;
            var t = typeof(ChaosResilientProgressGuard);
            return ((ChaosCheckpoint[])t.GetField("_slots", F)!.GetValue(g)!, (long[])t.GetField("_vers", F)!.GetValue(g)!);
        }

        [Fact]
        public void Guard_StaleIndex_UnpublishedShadow_NeverReturned_EpochNeverRegresses()
        {
            // [실결함 · 수정 전 Red] 독자가 _active(=a) 를 읽고 선점 → 라이터가 두 번 커밋해 a 가 다음 그림자가 되고
            //   그림자 기록을 끝냈지만 CAS 발행 직전. 수정 전 독출은 a 슬롯만 고집해 **미발행 에폭 E+1** 을 반환,
            //   다음 TryRead 는 실제 활성(E)을 반환 → 에폭 역행.
            var g = new ChaosResilientProgressGuard(spinCeiling: 64);
            Assert.True(g.TryCommit(1, 1, -1));     // active = 1 (에폭 1)
            Assert.True(g.TryCommit(1, 2, -2));     // active = 0 (에폭 2)
            Assert.True(g.TryRead(out var pub));
            Assert.Equal(2L, pub.Epoch);
            int shadow = 1;                         // 현재 그림자 = 1 (독자의 stale 인덱스)
            var (slots, vers) = Internals(g);
            vers[shadow]++;                                                          // 라이터: 홀(쓰는 중)
            slots[shadow] = new ChaosCheckpoint { Epoch = 3, PayloadA = 3, PayloadB = -3 };
            vers[shadow]++;                                                          // 라이터: 짝(기록 완료) — 아직 CAS 발행 전

            Assert.True(g.TryReadFromStaleHint(shadow, out var s));
            Assert.Equal(2L, s.Epoch);              // ★발행된 상태만 — 미발행 3 반환 금지
            Assert.True(g.TryRead(out var s2));
            Assert.True(s2.Epoch >= s.Epoch);       // ★역행 0
            Assert.True(g.StaleIndexRedirects >= 1);
            Assert.False(g.SafeHoldActive);
        }

        [Fact]
        public void Guard_StaleIndex_WriterPausedMidWrite_NoFalsePositiveHold()
        {
            // [실결함 · 수정 전 Red] 라이터가 그림자 기록 도중 선점(버전 홀수 고착) — 발행된 안정 슬롯이 옆에 있는데
            //   수정 전 독출은 그 그림자에서 스핀 상한을 소진 → TornStateBlocks++ · SafeHoldActive 오발(무장 하향 false-positive).
            var g = new ChaosResilientProgressGuard(spinCeiling: 64);
            Assert.True(g.TryCommit(1, 1, -1));
            Assert.True(g.TryCommit(1, 2, -2));     // active = 0
            var (_, vers) = Internals(g);
            vers[1]++;                              // 슬롯 1(그림자) 기록 중 선점 — 홀수 고착

            Assert.True(g.TryReadFromStaleHint(1, out var s));
            Assert.Equal(2L, s.Epoch);
            Assert.Equal(0L, g.TornStateBlocks);
            Assert.False(g.SafeHoldActive);          // ★정상 경합으로 무장 하향 금지
            Assert.True(g.ReadRetries >= 1);

            // 진짜 경합(두 슬롯 모두 쓰는 중으로 고착)은 여전히 스핀 상한 → fail-closed 홀드.
            vers[0]++;
            Assert.False(g.TryRead(out _));
            Assert.Equal(1L, g.TornStateBlocks);
            Assert.True(g.SafeHoldActive);
        }

        [Fact]
        public void Guard_StaleRedirectPath_ZeroAllocation()
        {
            var g = new ChaosResilientProgressGuard(spinCeiling: 64);
            g.TryCommit(1, 1, -1); g.TryCommit(1, 2, -2);
            var (_, vers) = Internals(g);
            vers[1]++;                              // 그림자 홀수 고착 → 매 판독이 재시도 + 재추적 경로
            _ = g.TryReadFromStaleHint(1, out _);
            long min = long.MaxValue;
            for (int w = 0; w < 3 && min != 0; w++)
            {
                long b = GC.GetAllocatedBytesForCurrentThread();
                long sink = 0;
                for (int i = 0; i < 100_000; i++) { if (g.TryReadFromStaleHint(1, out var s)) sink += s.Epoch; }
                min = Math.Min(min, GC.GetAllocatedBytesForCurrentThread() - b);
                Assert.Equal(200_000L, sink);
            }
            Assert.Equal(0L, min);                  // ★재시도·재추적 핫패스 힙 0
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
