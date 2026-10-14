// ============================================================================
// LongSoakIntegrationTests — [v12.18.4-DEV · 전 사슬 가속 Soak & 다중 벡터 스트레스 퍼징]
//
//  §1 100만 틱 가속 Soak(전 사슬 결속 · 국면 교차) — VolatilityAdaptiveCalibrator · SignalChurnCircuitBreaker · OrderPathStageProfiler ·
//     MonotonicSettingAuditLog(배지) · MicrosecondJitterSentinel 이 모두 걸린 LongSoakProfileHarness 를 10만 틱 예열 후 90만 틱 더 구동.
//     측정 구간: 구동 스레드 관리 힙 할당 0B · GC Gen0/1/2 수집 0 · 워밍업 후 인터락 raised 0 · 최종 집계어 0 · 보정기 Trend ↔ Choppy 실전이 ≥ 2.
//  §2 3중 경합(실 스레드) — 주문 경로(목표 초당 5만 제안 · 소유 스레드) + 관제 판독(목표 초당 1,000회 원장 스냅샷 · 50ms 마다 허브 프레임) +
//     강화 설정 퍼저(목표 초당 100회 TryWrite · 강화 3 : 완화 1). 원장 용량 256 → 운전 중 90% 포화 → 100% 동결.
//     판정: 찢김 0(판독 사본 ↔ 기록자 머리 이력 대조) · 버전/개수 역행 0 · 교착 0(합류 제한 시간) · 세 스레드 관리 힙 할당 0B · 측정 창 GC 0 ·
//     동결을 관측한 바로 그 명령에서 비트 27 이미 세워짐(대기 0) · 동결 뒤 기입 0(AuditFrozen) · 동결 뒤 송출 0 · 비트 27 외 인터락 0.
//     퍼저 할당은 회차별로 잰다 — 90% 포화 1회 고지(Trace 문자열 · 콜드 설계) 회차만 분리 · 나머지 정상 회차 0B.
//  §3 [방향 A] 지터 센티널 단위 — 임계 · 링 덮어씀 순서 · 정확한 상위 K(100만 표본 중 심은 10개 = 0.001%) · GC 원인 · 기록 0B · 폐기 후 거부.
//  §4 결정론 포화 전이(단일 스레드) — 90%~100% 경고(무장 불변) · 100% 다음 기입 = 동결 + 그 반환 시점 비트 27 · 이후 기입 0 · 주문 송출 0 · 오중 빌드 · 래칫 ≤ 40.
//  정직: 속도 목표(5만/s · 1,000/s · 100/s)는 '목표'다 — 실제 달성률을 출력하고, 판정은 속도가 아니라 불변식(찢김/교착/할당/격발)으로 한다.
// ============================================================================
using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Threading;
using MS2E.Core;
using MS2E.Infrastructure;
using StockManager.Engines;
using StockManager.Security;
using StockManager.Simulation;
using StockManager.Telemetry;
using StockManager.Tools;
using Xunit;
using Xunit.Abstractions;

namespace StockManager.Tests
{
    /// <summary>
    /// [v12.18.4-DEV] 장기 Soak 전용 비병렬 컬렉션. 정직: 이 어셈블리는 이미 전역 직렬(TestCollectionBehavior · DisableTestParallelization)이라
    /// 동시 실행 경합은 원래 없다 — 이 컬렉션은 그 전역 설정이 바뀌어도 Soak 가 다른 테스트와 겹치지 않게 하는 방어적 고정이다.
    /// 간헐 실패(전체 스위트에서만 · 워밍업 후 raised 1)의 원인 후보는 병렬성이 아니라 헬스 에이전트의 실 프로세스 탐침(GC 정지 · 스레드풀)이다 —
    /// 테스트가 스스로 일으킨 강제 GC 정지는 하네스 Settle 이 판정에서 뺀다. 실패 시 단언 메시지가 위반 사유를 남긴다.
    /// </summary>
    [CollectionDefinition(Name, DisableParallelization = true)]
    public sealed class NonParallelSoakCollection { public const string Name = "NonParallelSoak"; }

    /// <summary>테스트 전후 강제 전체 GC — 앞 테스트 가비지가 Soak 창으로, Soak 가비지가 다음 테스트로 넘어가지 않게(메모리 고점 억제).</summary>
    public abstract class SoakTestBase : IDisposable
    {
        protected SoakTestBase() => FullCollect();
        public void Dispose() { FullCollect(); GC.SuppressFinalize(this); }
        protected static void FullCollect()
        {
            GC.Collect(2, GCCollectionMode.Forced, true, true);
            GC.WaitForPendingFinalizers();
            GC.Collect(2, GCCollectionMode.Forced, true, true);
        }
    }

    [Collection(NonParallelSoakCollection.Name)]
    public sealed class LongSoakIntegrationTests : SoakTestBase
    {
        private readonly ITestOutputHelper _out;
        public LongSoakIntegrationTests(ITestOutputHelper o) => _out = o;

        private const long Bit27 = 1L << (int)ArmingInterlockSource.StrategyAnomaly;
        private const string Key = "Strategy.ConfidenceThresholdBp";

        /// <summary>메모리 설정 저장소(설정 스레드 단독 사용) — 문자열은 미리 만든 표만 쓴다(퍼저 자체 할당 0).</summary>
        private sealed class MemStore : ITightenSettingStore
        {
            public string? Value;
            public long Writes;
            public string? ReadEnvironment(string key) => null;
            public string? ReadStored(string key) => Value;
            public void WriteStored(string key, string value) { Value = value; Writes++; }
        }

        private static string[] ValueTable()
        {
            var t = new string[10_001];
            for (int v = 0; v < t.Length; v++) t[v] = v.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return t;
        }

        private static ulong HeadPrefix(MonotonicSettingAuditLog ledger)
        {
            Span<byte> h = stackalloc byte[32];
            ledger.CopyHead(h);
            return BinaryPrimitives.ReadUInt64BigEndian(h);
        }

        // ════════════ §1 100만 틱 가속 Soak ════════════

        [Fact]
        public void Soak_OneMillionTicks_FullChain_RegimeCrossing_ZeroAllocAfter100k_ZeroGc_NoFalsePositive()
        {
            using var soak = new LongSoakProfileHarness(fullChain: true, regimeCycleSeconds: 600, bookEverySeconds: 1);
            soak.Warmup();
            Assert.Equal(0L, soak.Sim.Interlocks.Mask);
            var wall = Stopwatch.StartNew();
            long warm = soak.RunTicks(100_000);                                                    // 예열: JIT · 링 회전 · 보정기 워밍업 · 첫 국면 전이
            soak.Settle();                                                                         // 측정 직전 정리(그 GC 정지는 헬스 판정 제외)
            soak.ResetBaseline();
            soak.Jitter!.Reset();
            long choppy0 = soak.Calibrator!.ChoppyEntries;
            int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2);
            long a0 = GC.GetAllocatedBytesForCurrentThread();
            long measured = soak.RunTicks(900_000);                                                // ★측정 구간(10만 틱 이후)
            long alloc = GC.GetAllocatedBytesForCurrentThread() - a0;
            int d0 = GC.CollectionCount(0) - g0, d1 = GC.CollectionCount(1) - g1, d2 = GC.CollectionCount(2) - g2;
            wall.Stop();
            var r = soak.Report();
            var j = soak.Jitter;
            Span<JitterSpike> tail = stackalloc JitterSpike[MicrosecondJitterSentinel.DefaultTailCapacity];
            int tn = j.CopyTail(tail);
            _out.WriteLine($"[Soak 1M] 틱 {warm + measured:N0}(예열 {warm:N0} + 측정 {measured:N0}) · 모의 {r.SimSeconds:N0}초 · 호가 {r.Books:N0} · 주문 {r.Orders:N0} · 체결 {r.Filled:N0} · 실시간 {wall.Elapsed.TotalSeconds:F1}s");
            _out.WriteLine($"[Soak 1M] 측정 구간 할당 {alloc}B · GC {d0}/{d1}/{d2} · 관리 힙 Δ {r.ManagedHeapDelta:N0}B · WS Δ {r.WorkingSetDelta:N0}B · 집계어 0x{r.FinalMask:X} · raised {r.RaisedAfterWarmup}");
            _out.WriteLine($"[Soak 1M] 보정기 관측 {soak.Calibrator.Observations:N0} · 횡보 진입 {soak.Calibrator.ChoppyEntries - choppy0}(측정 구간) · 횡보 호가 {soak.Calibrator.ChoppyBooks:N0} · 감쇠 {soak.Calibrator.DampenedBooks:N0} · 국면 거부 {soak.Quant.RejectedRegime:N0} · 승인 {soak.Churn!.Approved:N0} · 억제 {soak.Churn.Suppressed} · 붕괴 {soak.Churn.Breakdowns}");
            _out.WriteLine($"[Soak 1M] 지터 표본 {j.Samples:N0} · 평균 {j.MeanNanos / 1000.0:F2}µs · p99 {j.Percentile(990) / 1000.0:F1}µs · p99.9 {j.Percentile(999) / 1000.0:F1}µs · 최대 {j.MaxNanos / 1000.0:F1}µs · >500µs {j.Spikes}(GC {j.GcSpikes})");
            for (int i = 0; i < Math.Min(tn, 5); i++)
                _out.WriteLine($"  꼬리 #{i + 1}: {tail[i].Nanos / 1000.0:F1}µs · 종류 {tail[i].Kind} · 원인 {tail[i].Cause} · 순번 {tail[i].Sequence:N0}");

            _out.WriteLine($"[Soak 1M] 헬스 위반 {soak.Health.Breach} · 마지막 스레드풀 대기 {soak.Health.LastStallMicros:N0}µs · 제외된 Settle GC 정지 {soak.ExcludedPauseMicros:N0}µs · 집계어 0x{soak.Sim.Interlocks.Mask:X}");
            Assert.True(warm + measured >= 1_000_000, "틱 " + (warm + measured));
            Assert.Equal(0, alloc);                                                               // ★10만 틱 이후 구동 스레드 관리 힙 할당 0B
            Assert.Equal((0, 0, 0), (d0, d1, d2));                                                // ★측정 구간 GC 수집 0
            Assert.True(r.RaisedAfterWarmup == 0, $"raised {r.RaisedAfterWarmup} · 헬스 {soak.Health.Breach} · 스레드풀 대기 {soak.Health.LastStallMicros:N0}µs");   // 오작동 0
            Assert.Equal(0L, r.BlackboxRaisedAfterWarmup);
            Assert.Equal(0L, r.FinalMask);
            Assert.False(r.StormTripped);
            Assert.Equal(r.Placed, r.Filled);
            Assert.True(soak.Calibrator.ChoppyEntries - choppy0 >= 2, "측정 구간 Trend ↔ Choppy 전이 " + (soak.Calibrator.ChoppyEntries - choppy0));
            Assert.Equal(0L, soak.Churn.Breakdowns);
            Assert.Equal(tn, Math.Min(MicrosecondJitterSentinel.DefaultTailCapacity, (int)j.Samples));
            for (int i = 1; i < tn; i++) Assert.True(tail[i - 1].Nanos >= tail[i].Nanos);
            Assert.True(soak.Stages!.Begun >= r.Orders - 2 * 60, "주문 경로 계측 결속(발행 스탬프) " + soak.Stages.Begun);
            var ls = soak.Ledger!.Snapshot();
            Assert.Equal((0, AuditLedgerState.Normal), (ls.Count, ls.State));                      // 설정 변경 없는 Soak = 원장 무기록
        }

        // ════════════ §2 3중 경합 ════════════

        [Fact]
        public void ThreeWayContention_OrderPath_DashboardReader_TightenFuzzer_NoTornNoDeadlock_ZeroAlloc_Bit27AtFreeze()
        {
            const int Capacity = 256;
            var soak = new LongSoakProfileHarness(fullChain: true, ledgerCapacity: Capacity);
            var ledger = soak.Ledger!;
            var store = new MemStore { Value = "5000" };
            string[] values = ValueTable();
            var history = new ulong[Capacity + 1];                                                 // 개수 c → 그때의 머리 접두(기록자만 기입)
            history[0] = ledger.Snapshot().HeadPrefix;

            // 예열(소유 스레드 이관 전 — Thread.Start 가 happens-before)
            soak.Warmup();
            soak.RunOrderBurst(50_000);
            for (int v = 5_001; v <= 5_004; v++) Assert.Equal(TightenVerdict.Tightened, TightenOnlyUiSettingWriter.TryWrite(Key, values[v], store, ledger));
            history[ledger.Snapshot().Count] = HeadPrefix(ledger);                                 // 예열 기록 = 마지막만 판정 대상(중간은 0 = 미검증)
            { var f = soak.Hub.Acquire(); if (f is not null) soak.Hub.Release(f); var s = ledger.Snapshot(); _ = s.State; }
            Assert.Equal(0L, soak.Sim.Interlocks.Mask);
            soak.Settle();

            var stop = new CancellationTokenSource();
            long torn = 0, regress = 0, frameTorn = 0, polls = 0, frames = 0, verified = 0, sawSaturating = 0, sawFrozen = 0;
            long orderAlloc = -1, readerAlloc = -1, fuzzAlloc = -1, noticeAlloc = 0, noticeAttempts = 0, attempts = 0, frozenRejects = 0, storeWritesAtFreeze = -1;
            long bitAtFreeze = -1, freezeObservedAtAttempt = -1;
            long placedAtFreeze = -1;
            Exception? fault = null;
            int gc0 = GC.CollectionCount(0), gc1 = GC.CollectionCount(1), gc2 = GC.CollectionCount(2);
            var wall = Stopwatch.StartNew();
            long freq = Stopwatch.Frequency;

            var order = new Thread(() =>
            {
                try
                {
                    long a = GC.GetAllocatedBytesForCurrentThread();
                    long start = Stopwatch.GetTimestamp(), done = 0;
                    while (!stop.IsCancellationRequested)
                    {
                        done += soak.RunOrderBurst(1_000);
                        long due = start + done * freq / 50_000;                                   // 목표 초당 5만 제안
                        while (Stopwatch.GetTimestamp() < due && !stop.IsCancellationRequested) Thread.SpinWait(64);
                    }
                    orderAlloc = GC.GetAllocatedBytesForCurrentThread() - a;
                }
                catch (Exception ex) { fault ??= ex; }
            }) { IsBackground = true, Name = "soak-order" };

            var reader = new Thread(() =>
            {
                try
                {
                    long a = GC.GetAllocatedBytesForCurrentThread();
                    long lastVer = -1, lastCount = -1; ulong lastHead = 0;
                    long start = Stopwatch.GetTimestamp();
                    while (!stop.IsCancellationRequested)
                    {
                        var s = ledger.Snapshot();
                        polls++;
                        if (s.Contended || s.Count < 0 || s.Count > Capacity) torn++;
                        else
                        {
                            if (s.Version < lastVer || s.Count < lastCount) regress++;
                            if (s.Version == lastVer && (s.Count != lastCount || s.HeadPrefix != lastHead)) torn++;
                            ulong h = Volatile.Read(ref history[s.Count]);
                            if (h != 0) { verified++; if (h != s.HeadPrefix) torn++; }
                            if (s.State == AuditLedgerState.Saturating) sawSaturating++;
                            if (s.State == AuditLedgerState.Frozen) sawFrozen++;
                            lastVer = s.Version; lastCount = s.Count; lastHead = s.HeadPrefix;
                        }
                        if (polls % 50 == 0)                                                         // 50ms 마다 대시보드 프레임(1,000/s 기준)
                        {
                            var f = soak.Hub.Acquire();
                            if (f is not null)
                            {
                                frames++;
                                if (f.AuditCount >= 0 && f.AuditCount <= Capacity)
                                {
                                    ulong h = Volatile.Read(ref history[f.AuditCount]);
                                    if (h != 0 && h != f.AuditHeadPrefix) frameTorn++;
                                }
                                else frameTorn++;
                                soak.Hub.Release(f);
                            }
                        }
                        long due = start + polls * freq / 1_000;                                    // 목표 초당 1,000회
                        while (Stopwatch.GetTimestamp() < due && !stop.IsCancellationRequested) Thread.SpinWait(32);
                    }
                    readerAlloc = GC.GetAllocatedBytesForCurrentThread() - a;
                }
                catch (Exception ex) { fault ??= ex; }
            }) { IsBackground = true, Name = "soak-reader" };

            var fuzzer = new Thread(() =>
            {
                try
                {
                    long steady = 0;
                    long start = Stopwatch.GetTimestamp();
                    int cur = 5_004;
                    long afterFreeze = 0;
                    while (!stop.IsCancellationRequested && afterFreeze < 50)
                    {
                        long n = ++attempts;
                        bool loosen = n % 4 == 0;
                        string v = values[loosen ? cur - 1 : Math.Min(10_000, cur + 1)];
                        long warns = ledger.SaturationWarnings, a = GC.GetAllocatedBytesForCurrentThread();
                        var verdict = TightenOnlyUiSettingWriter.TryWrite(Key, v, store, ledger);
                        long d = GC.GetAllocatedBytesForCurrentThread() - a;
                        // 90% 포화 1회 고지(Trace 문자열 · 설계상 콜드) 회차만 분리 계수 — 그 밖의 회차는 정상 상태(0B 이어야 함)
                        if (ledger.SaturationWarnings != warns) { noticeAlloc += d; noticeAttempts++; } else steady += d;
                        if (verdict == TightenVerdict.Tightened) cur++;
                        if (ledger.Frozen)
                        {
                            if (bitAtFreeze < 0)
                            {
                                bitAtFreeze = soak.Sim.Interlocks.Mask & Bit27;                     // ★동결을 본 바로 그 명령 — 대기 0
                                freezeObservedAtAttempt = n;
                                storeWritesAtFreeze = store.Writes;
                                placedAtFreeze = soak.Sim.Broker.Placed;
                            }
                            else if (verdict == TightenVerdict.AuditFrozen) frozenRejects++;
                            afterFreeze++;
                        }
                        else
                        {
                            // 유일 기록자 — TryWrite 반환 직후의 (개수, 머리) 는 일관(다음 기록 전)
                            int c = ledger.Snapshot().Count;
                            Volatile.Write(ref history[c], HeadPrefix(ledger));
                        }
                        long due = start + attempts * freq / 100;                                   // 목표 초당 100회
                        while (Stopwatch.GetTimestamp() < due && !stop.IsCancellationRequested) Thread.Sleep(1);
                    }
                    fuzzAlloc = steady;
                }
                catch (Exception ex) { fault ??= ex; }
            }) { IsBackground = true, Name = "soak-fuzzer" };

            // [v12.18.9-DEV] 드레인 규약: 작업 스레드 Join 시간 초과(부하)로 반환해도 하네스(비관리 지터 센티널 · 시뮬레이터)를 살아 있는 스레드 밑에서
            //   폐기하지 않는다 — 취소 → JoinAll → 전부 종료 시에만 폐기(생존 = 격리 · 시험은 단언 실패).
            try
            {
                order.Start(); reader.Start(); fuzzer.Start();
                bool fuzzDone = fuzzer.Join(TimeSpan.FromSeconds(30));                                  // 동결 + 50회 → 종료
                Thread.Sleep(200);                                                                      // 동결 후 송출 0 관측 창
                stop.Cancel();
                bool orderDone = order.Join(TimeSpan.FromSeconds(10)), readerDone = reader.Join(TimeSpan.FromSeconds(10));
                wall.Stop();
                int dg0 = GC.CollectionCount(0) - gc0, dg1 = GC.CollectionCount(1) - gc1, dg2 = GC.CollectionCount(2) - gc2;
                double sec = wall.Elapsed.TotalSeconds;
                _out.WriteLine($"[3중 경합] {sec:F2}s · 제안 {soak.Proposals:N0}({soak.Proposals / sec:N0}/s) · 판독 {polls:N0}({polls / sec:N0}/s · 검증 {verified:N0}) · 프레임 {frames:N0} · 기입 시도 {attempts}({attempts / sec:F0}/s)");
                _out.WriteLine($"[3중 경합] 찢김 {torn} · 역행 {regress} · 프레임 찢김 {frameTorn} · 포화 관측 {sawSaturating} · 동결 관측 {sawFrozen} · 동결 시도 #{freezeObservedAtAttempt} · 동결 뒤 거부 {frozenRejects}");
                _out.WriteLine($"[3중 경합] 할당 주문 {orderAlloc}B · 판독 {readerAlloc}B · 퍼저 정상 {fuzzAlloc}B(+ 90% 고지 1회 {noticeAlloc}B) · GC {dg0}/{dg1}/{dg2} · 집계어 0x{soak.Sim.Interlocks.Mask:X} · 송출 {soak.Sim.Broker.Placed:N0}(동결 시 {placedAtFreeze:N0}) · 거부 {soak.Sim.Worker.InterlockVetoes:N0}");
                var j = soak.Jitter!;
                _out.WriteLine($"[3중 경합] 주문 제안 지연 p99 {j.Percentile(990) / 1000.0:F1}µs · 최대 {j.MaxNanos / 1000.0:F1}µs · >500µs {j.Spikes}(GC {j.GcSpikes})");

                Assert.Null(fault);
                Assert.True(fuzzDone && orderDone && readerDone, $"교착/정지 — 퍼저 {fuzzDone} · 주문 {orderDone} · 판독 {readerDone}");
                Assert.Equal((0L, 0L, 0L), (torn, regress, frameTorn));                                   // ★찢김 · 역행 0
                Assert.True(verified > 0 && frames > 0);
                Assert.Equal(0L, orderAlloc);                                                           // ★세 스레드 관리 힙 0B
                Assert.Equal(0L, readerAlloc);
                Assert.Equal(0L, fuzzAlloc);                                                            // 퍼저 정상 회차 0B
                Assert.Equal(1L, noticeAttempts);                                                       // 고지 할당은 정확히 1회차(포화 전이)
                Assert.Equal((0, 0, 0), (dg0, dg1, dg2));                                               // ★경합 창 GC 0
                Assert.Equal(Bit27, bitAtFreeze);                                                       // ★동결 관측 순간 비트 27 이미 세워짐
                Assert.True(sawSaturating > 0, "90% 포화 상태를 판독자가 관측");
                Assert.True(sawFrozen > 0);
                Assert.Equal(1L, ledger.SaturationWarnings);
                Assert.Equal(50L, frozenRejects + 1);                                                   // 동결 첫 관측 뒤 49회 = 전부 AuditFrozen
                Assert.Equal(storeWritesAtFreeze, store.Writes);                                        // ★동결 뒤 기입 0
                Assert.Equal((1L, 0L), (soak.Anomaly.AuditLedgerFulls, soak.Anomaly.AuditChainBreaks));
                Assert.Equal(Bit27, soak.Sim.Interlocks.Mask);                                           // ★비트 27 외 인터락 훼손 0
                var snap = ledger.Snapshot();
                Assert.Equal((Capacity, AuditFreezeCause.Full), (snap.Count, snap.FreezeCause));

                // 동결 뒤 송출 0 — 소유 스레드(이제 이 스레드)에서 결정론 확인
                long placed = soak.Sim.Broker.Placed, vetoes = soak.Sim.Worker.InterlockVetoes;
                soak.RunOrderBurst(10_000);
                Assert.Equal(placed, soak.Sim.Broker.Placed);
                Assert.True(soak.Sim.Worker.InterlockVetoes > vetoes);
            }
            finally
            {
                stop.Cancel();
                bool drained = TeardownDrain.JoinAll(TimeSpan.FromSeconds(30), order, reader, fuzzer);
                TeardownDrain.DisposeOrQuarantine(drained, stop, soak);
            }
        }

        // ════════════ §3 [방향 A] 지터 센티널 ════════════

        [Fact]
        public void JitterSentinel_ExactTopK_RingOrder_GcCause_ZeroAlloc_DisposedRejects()
        {
            using var j = new MicrosecondJitterSentinel(thresholdNanos: 500_000, ringCapacity: 8, tailCapacity: 16);
            ulong rng = 0x1234_5678_9ABCUL;
            long Next() { rng ^= rng >> 12; rng ^= rng << 25; rng ^= rng >> 27; return (long)((rng * 0x2545F4914F6CDD1DUL) % 200_000); }
            // 100만 표본(≤ 200µs) 중 10개(= 0.001%)를 600µs + k 로 심는다 — 그중 3개는 GC 구간
            var planted = new long[10];
            for (int k = 0; k < 10; k++) planted[k] = 600_000 + k * 1_000;
            j.Record(1, 0, 0, 0);
            long a = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1_000_000; i++)
            {
                int p = i % 100_000 == 50_000 ? i / 100_000 : -1;
                if (p >= 0) j.Record(planted[p], p < 3 ? 1 : 0, 9, i);
                else j.Record(Next(), 0, 0, i);
            }
            long alloc = GC.GetAllocatedBytesForCurrentThread() - a;
            Span<JitterSpike> tail = stackalloc JitterSpike[16];
            Span<JitterSpike> ring = stackalloc JitterSpike[16];
            int tn = j.CopyTail(tail), rn = j.CopySpikes(ring);
            _out.WriteLine($"[지터 센티널] 표본 {j.Samples:N0} · 스파이크 {j.Spikes}(GC {j.GcSpikes} · 덮어씀 {j.Overwritten}) · 꼬리 {tn} · 1M 기록 할당 {alloc}B · p99.9 {j.Percentile(999) / 1000.0:F1}µs");
            Assert.Equal(0, alloc);
            Assert.Equal(1_000_001L, j.Samples);
            Assert.Equal((10L, 3L, 2L), (j.Spikes, j.GcSpikes, j.Overwritten));                    // 링 8 → 오래된 2개 덮어씀
            Assert.Equal(16, tn);
            for (int k = 0; k < 10; k++)                                                            // ★상위 10 = 심은 10개(큰 순) 정확히
            {
                Assert.Equal(planted[9 - k], tail[k].Nanos);
                Assert.Equal((byte)9, tail[k].Kind);
                Assert.Equal(9 - k < 3 ? JitterCause.Gc : JitterCause.Unattributed, tail[k].Cause);
            }
            Assert.True(tail[10].Nanos < 500_000 && tail[10].Nanos >= tail[15].Nanos);              // 나머지 꼬리 = 임계 미만 최대들
            Assert.Equal(8, rn);
            for (int k = 0; k < 8; k++) Assert.Equal(planted[k + 2], ring[k].Nanos);                // 링 = 오래된 → 최신(최근 8)
            Assert.Equal(planted[9], j.MaxNanos);

            j.Reset();
            Assert.Equal((0L, 0L, 0), (j.Samples, j.Spikes, j.TailCount));
            long t0 = MicrosecondJitterSentinel.Begin(out int g0);
            long ns = j.End(t0, g0, 2, 0);
            Assert.True(ns >= 0 && j.Samples == 1);
            j.Dispose();
            Assert.Throws<ObjectDisposedException>(() => j.Record(1, 0, 0, 0));
            Assert.Equal(0, j.CopyTail(tail));
        }

        // ════════════ §4 결정론 포화 전이 · 오중 빌드 ════════════

        [Fact]
        public void LedgerSaturation_90Warn_100Freeze_Bit27Synchronous_OrdersVetoed_SettingsBlocked()
        {
            const int Capacity = 64;
            using var soak = new LongSoakProfileHarness(fullChain: true, ledgerCapacity: Capacity);
            soak.Warmup();
            soak.RunSeconds(120);
            var ledger = soak.Ledger!;
            var store = new MemStore { Value = "5000" };
            string[] values = ValueTable();
            int warnAt = AuditLedgerSnapshot.SaturationThreshold(Capacity);
            for (int c = 1; c <= Capacity; c++)
            {
                Assert.Equal(TightenVerdict.Tightened, TightenOnlyUiSettingWriter.TryWrite(Key, values[5_000 + c], store, ledger));
                var s = ledger.Snapshot();
                Assert.Equal(c, s.Count);
                if (c < warnAt) Assert.Equal((AuditLedgerState.Normal, 0L, 0L), (s.State, ledger.SaturationWarnings, soak.Sim.Interlocks.Mask));
                else Assert.Equal((AuditLedgerState.Saturating, 1L, 0L), (s.State, ledger.SaturationWarnings, soak.Sim.Interlocks.Mask));   // 90%~100%: 경고만 · 무장 판정 불변
            }
            // 100% = 다음 기입은 감사 기록 불가 → 그 기입이 동결(가득 참) + 비트 27(기존 설계 §55 · 재기동 시 가득 찬 파일은 기동 즉시 동결)
            long writes = store.Writes, placed = soak.Sim.Broker.Placed;
            Assert.Equal(TightenVerdict.AuditFrozen, TightenOnlyUiSettingWriter.TryWrite(Key, values[9_000], store, ledger));
            Assert.Equal((AuditLedgerState.Frozen, Bit27), (ledger.Snapshot().State, soak.Sim.Interlocks.Mask));   // ★그 기입 반환 시점 = 비트 27(대기 0)
            Assert.Equal(StrategyAnomalyKind.AuditLedgerFull, soak.Anomaly.FirstCause);
            Assert.Equal(writes, store.Writes);
            Assert.Equal(TightenVerdict.AuditFrozen, TightenOnlyUiSettingWriter.TryWrite(Key, values[9_001], store, ledger));
            Assert.Equal(writes, store.Writes);
            soak.RunSeconds(600);                                                                  // 동결 뒤 10분: 주문 제안 20 → 송출 0
            Assert.Equal(placed, soak.Sim.Broker.Placed);
            Assert.True(soak.Sim.Worker.InterlockVetoes >= 20);
            var f = soak.Hub.Acquire();
            Assert.NotNull(f);
            Assert.Equal((3, Capacity, 100), (f!.AuditState, f.AuditCount, f.AuditUsedPercent));   // DashAuditLedger.Frozen = 3
            soak.Hub.Release(f);
        }

        [Fact]
        public void FiveBuilds_WarningsAsErrors_Ratchet_InterlockCount()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d is not null && !File.Exists(Path.Combine(d.FullName, "StockManager.sln"))) d = d.Parent;
            Assert.NotNull(d);
            string sln = File.ReadAllText(Path.Combine(d!.FullName, "StockManager.sln"));
            foreach (var proj in new[] { @"StockManager\StockManager.csproj", @"StockManager.PrimesBench\StockManager.PrimesBench.csproj",
                                         @"StockManager.KiwoomProxy\StockManager.KiwoomProxy.csproj", @"tools\KiwoomComShim\KiwoomComShim.csproj" })
            {
                Assert.Contains(proj, sln);
                Assert.Contains("<TreatWarningsAsErrors>true</TreatWarningsAsErrors>", File.ReadAllText(Path.Combine(d.FullName, proj)));
            }
            Assert.True(PureShellFitnessSentinel.RatchetCeiling <= 40);
            Assert.Equal(30, Enum.GetValues<ArmingInterlockSource>().Length);
            Assert.Equal(6, TightenOnlyUiSettingWriter.KeyCount);
        }
    }
}
