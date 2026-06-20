// ============================================================================
// ChaosInjectionTests — S21 #1-b: 카오스 폭격 + 불변식 방어 검증 (20개)
//
//  청사진(docs/S21-ChaosInjection-Blueprint.md) 구현. 모든 카오스 하니스는 테스트 사이드(J1).
//  결정론(seeded xorshift) — 실패 재현 가능. 검증 불변식 6:
//    INV-1 Fail-Closed · INV-2 Zero-Leak · INV-3 Recovery · INV-4 No-Torn · INV-5 Host-Survival · INV-6 Determinism
//
//  §A MMF 오염(5) §B 브로커 카오스(5) §C 동시 핫스왑+카오스(4) §D 누수·격리(3) §E 복구·멱등(3)
// ============================================================================
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using StockManager.Common;
using Xunit;

namespace StockManager.Tests
{
    public sealed class ChaosInjectionTests : IDisposable
    {
        private readonly List<string> _tempFiles = new();
        public void Dispose()
        {
            foreach (var f in _tempFiles)
                try { if (File.Exists(f)) File.Delete(f); } catch { }
        }

        // ── 공통 빌더 ───────────────────────────────────────────────────────────
        private string NewPath()
        {
            var p = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + "-chaos.hydr");
            _tempFiles.Add(p);
            return p;
        }

        private static (StateHydrationEngine engine, HydrationSlot<int> slot) NewEngine(string path)
        {
            var engine = new StateHydrationEngine(path);
            var slot   = new HydrationSlot<int>((int)GatingMode.Normal);
            engine.RegisterSlot("gatingMode", slot);
            return (engine, slot);
        }

        private void WriteSnapshot(string path, GatingMode mode)
        {
            var (engine, slot) = NewEngine(path);
            slot.Value = (int)mode;
            engine.Snapshot();
            engine.Dispose();
        }

        private static StrategySpec Spec(string id) => new StrategySpec(
            id, id,
            new List<SpecCond> { new SpecCond(new SpecTerm(SpecKind.Rsi, 14), SpecOp.Lt, true, 30, null) },
            SpecLogic.And, new List<SpecCond>(), SpecLogic.And, 5, 10);

        private static StrategySlot SlotWith(StrategySpec a) { var s = new StrategySlot(); s.Swap(a); return s; }

        private static OrderIntent Order(StrategySpec spec, decimal qty, decimal price)
            => new OrderIntent(spec, "A005930", "삼성전자", PaperSide.Buy, PaperKind.Market, qty, price, true);

        private static (LocalState, BrokerState) SyncBooks()
            => (new LocalState(1_000_000m, new Dictionary<string, decimal>()),
                new BrokerState(1_000_000m, new Dictionary<string, decimal>()));

        private static (LocalState, BrokerState) DesyncBooks()
            => (new LocalState(1_000_000m, new Dictionary<string, decimal> { ["A005930"] = 10m }),
                new BrokerState(1_000_000m, new Dictionary<string, decimal> { ["A005930"] = 9m }));

        // ════════════════════════ §A MMF 오염 ═════════════════════════════════════

        [Fact]
        public void A1_CorruptMagic_FailsClosed()
        {
            var path = NewPath();
            WriteSnapshot(path, GatingMode.Normal);
            ChaosMmf.CorruptByte(path, 0);   // 매직 바이트 오염

            var (engine, slot) = NewEngine(path);
            var att = new OrderAttestationService();
            var r = BootIntegrityChecker.CheckAndHydrate(engine, att, slot);

            Assert.Equal(HydrationStatus.Corrupted, r.Status);
            Assert.Equal(GatingMode.EmergencyFreeze, att.Mode);   // INV-1
            Assert.False(engine.HasValidSnapshot);                 // 폐기
        }

        [Fact]
        public void A2_CorruptSlotRegion_FailsClosed()
        {
            var path = NewPath();
            WriteSnapshot(path, GatingMode.Normal);
            ChaosMmf.CorruptByte(path, 40);   // 슬롯 데이터 영역 → CRC 불일치

            var (engine, slot) = NewEngine(path);
            var att = new OrderAttestationService();
            var r = BootIntegrityChecker.CheckAndHydrate(engine, att, slot);

            Assert.Equal(HydrationStatus.Corrupted, r.Status);
            Assert.Equal(GatingMode.EmergencyFreeze, att.Mode);
        }

        [Fact]
        public void A3_CorruptCrcBytes_FailsClosed()
        {
            var path = NewPath();
            WriteSnapshot(path, GatingMode.Normal);
            ChaosMmf.CorruptByte(path, 24);   // CRC 영역(헤더 24~27)

            var (engine, slot) = NewEngine(path);
            var att = new OrderAttestationService();
            var r = BootIntegrityChecker.CheckAndHydrate(engine, att, slot);

            Assert.Equal(HydrationStatus.Corrupted, r.Status);
            Assert.Equal(GatingMode.EmergencyFreeze, att.Mode);
        }

        [Fact]
        public void A4_CorruptedThenRecheck_Idempotent_StaysFrozen()
        {
            var path = NewPath();
            WriteSnapshot(path, GatingMode.Normal);
            ChaosMmf.CorruptByte(path, 40);

            var (e1, s1) = NewEngine(path);
            var att = new OrderAttestationService();
            BootIntegrityChecker.CheckAndHydrate(e1, att, s1);   // Corrupted → 동결 + 폐기
            Assert.Equal(GatingMode.EmergencyFreeze, att.Mode);

            // 재검 — 스냅샷은 이미 폐기됨 → NoSnapshot, 동결은 유지(무단 해제 금지), 무크래시
            var (e2, s2) = NewEngine(path);
            var r2 = BootIntegrityChecker.CheckAndHydrate(e2, att, s2);
            Assert.Equal(HydrationStatus.NoSnapshot, r2.Status);
            Assert.Equal(GatingMode.EmergencyFreeze, att.Mode);
            Assert.False(r2.FrozeOnBoot);
        }

        [Fact]
        public void A5_SeededCorruption_IsDeterministic()
        {
            var p1 = NewPath(); var p2 = NewPath();
            WriteSnapshot(p1, GatingMode.Normal);
            File.Copy(p1, p2, overwrite: true);   // 동일 사전이미지 보장(스냅샷 헤더 시각차 제거)

            ChaosMmf.CorruptSeeded(p1, seed: 7, regionStart: 32, regionLen: 100);
            ChaosMmf.CorruptSeeded(p2, seed: 7, regionStart: 32, regionLen: 100);

            Assert.True(File.ReadAllBytes(p1).SequenceEqual(File.ReadAllBytes(p2)));  // INV-6
        }

        // ════════════════════════ §B 브로커 카오스 ════════════════════════════════

        [Fact]
        public void B1_BrokerThrows_Live_FailsClosed()
        {
            var spec = Spec("s");
            var att  = new OrderAttestationService();
            var gate = new AlgoExecutionGate(SlotWith(spec), att, new ChaosBroker(ChaosBroker.Mode.Throw)) { IsDryRun = false };
            var (l, b) = SyncBooks();

            var v = gate.Submit(Order(spec, 100m, 70000m), l, b, 100_000_000m);

            Assert.Equal(ExecOutcome.RejectedFrozen, v.Outcome);
            Assert.Equal(AlgoGateState.Frozen, gate.State);
            Assert.Equal(GatingMode.EmergencyFreeze, att.Mode);   // INV-1
        }

        [Fact]
        public void B2_BrokerThrows_DryRun_NoEffect()
        {
            var spec = Spec("s");
            var att  = new OrderAttestationService();
            var broker = new ChaosBroker(ChaosBroker.Mode.Throw);
            var gate = new AlgoExecutionGate(SlotWith(spec), att, broker);   // IsDryRun 기본 true
            var (l, b) = SyncBooks();

            var v = gate.Submit(Order(spec, 100m, 70000m), l, b, 100_000_000m);

            Assert.Equal(ExecOutcome.DryRunLogged, v.Outcome);
            Assert.Equal(0, broker.PlaceCount);                   // 브로커 미호출
            Assert.Equal(GatingMode.Normal, att.Mode);
        }

        [Fact]
        public void B3_BrokerThrows_Live_DoesNotPropagate()
        {
            var spec = Spec("s");
            var gate = new AlgoExecutionGate(SlotWith(spec), new OrderAttestationService(), new ChaosBroker(ChaosBroker.Mode.Throw)) { IsDryRun = false };
            var (l, b) = SyncBooks();

            var ex = Record.Exception(() => gate.Submit(Order(spec, 100m, 70000m), l, b, 100_000_000m));
            Assert.Null(ex);   // INV-1 — 예외가 게이트 밖으로 전파되지 않음
        }

        [Fact]
        public void B4_ChaosBroker_Seeded_Deterministic()
        {
            string Seq(int seed)
            {
                var br = new ChaosBroker(ChaosBroker.Mode.Seeded, seed);
                var sb = new System.Text.StringBuilder();
                for (int i = 0; i < 30; i++)
                {
                    try { var (ok, _) = br.Place(PaperAsset.Stock, "A", "n", PaperSide.Buy, PaperKind.Market, 1m, 0m, 1m); sb.Append(ok ? 'O' : 'R'); }
                    catch { sb.Append('X'); }
                }
                return sb.ToString();
            }
            Assert.Equal(Seq(99), Seq(99));   // INV-6
        }

        [Fact]
        public void B5_Router_ChaosBrokerThrow_Live_NoFills_Frozen()
        {
            var spec = Spec("s");
            var att  = new OrderAttestationService();
            var gate = new AlgoExecutionGate(SlotWith(spec), att, new ChaosBroker(ChaosBroker.Mode.Throw)) { IsDryRun = false };
            var router = new ShadowExecutionRouter(gate, new MultiAccountMesh(new[] { new SubAccountSpec("a", 1m) }));
            var (l, b) = SyncBooks();

            var r = router.Route(Order(spec, 100m, 70000m), l, b, 100_000_000m);

            Assert.Equal(ExecOutcome.RejectedFrozen, r.Outcome);
            Assert.Empty(r.Fills);
            Assert.Equal(GatingMode.EmergencyFreeze, att.Mode);
        }

        // ════════════════════════ §C 동시 핫스왑 + 카오스 ═════════════════════════

        [Fact]
        public void C1_ConcurrentRoute_RandomDesync_FreezesNoTorn_NoException()
        {
            var spec = Spec("s");
            var att  = new OrderAttestationService();
            var gate = new AlgoExecutionGate(SlotWith(spec), att);
            var router = new ShadowExecutionRouter(gate, new MultiAccountMesh(new[] { new SubAccountSpec("a", 1m) }));
            var errors = new ConcurrentQueue<Exception>();

            var threads = new List<Thread>();
            for (int t = 0; t < 4; t++)
            {
                int seed = t + 1;
                threads.Add(new Thread(() =>
                {
                    try
                    {
                        var rng = new ChaosRandom(seed);
                        for (int i = 0; i < 500; i++)
                        {
                            var (l, b) = rng.NextBool(0.2) ? DesyncBooks() : SyncBooks();
                            var r = router.Route(Order(spec, 10m, 1000m), l, b, 100_000_000m);
                            // 결과는 승인/거부/동결 중 하나 — 어떤 경우도 예외·torn 없어야 함
                            if (r.Fills.Any(f => f.Qty <= 0)) throw new Exception("invalid fill qty");
                        }
                    }
                    catch (Exception e) { errors.Enqueue(e); }
                }) { IsBackground = true, Priority = ThreadPriority.BelowNormal });
            }
            foreach (var th in threads) th.Start();
            foreach (var th in threads) th.Join(5000);

            Assert.Empty(errors);
            // 디싱크가 한 번이라도 발생했으면 동결 — 이후 모든 라우팅 거부(fail-closed)
            var (sl, sb) = SyncBooks();
            if (att.Mode == GatingMode.EmergencyFreeze)
                Assert.Equal(ExecOutcome.RejectedFrozen, router.Route(Order(spec, 10m, 1000m), sl, sb, 100_000_000m).Outcome);
        }

        [Fact]
        public void C2_RingBuffer_ChaosDropper_NeverTorn()
        {
            var rb = new RingBufferCore<ChaosTick>(16);   // 작은 용량 → 격심 추월
            var consumer = rb.CreateConsumer();
            var errors = new ConcurrentQueue<string>();
            var done = false;

            var reader = new Thread(() =>
            {
                long last = -1;
                while (!Volatile.Read(ref done) || consumer.Cursor < rb.Published)
                {
                    if (consumer.TryRead(out var t))
                    {
                        if (!t.IsConsistent) { errors.Enqueue($"torn {t.N}"); return; }
                        if (t.N <= last)     { errors.Enqueue($"non-monotonic {t.N}<={last}"); return; }
                        last = t.N;
                        Thread.SpinWait(15);
                    }
                }
            }) { IsBackground = true, Priority = ThreadPriority.BelowNormal };
            reader.Start();

            var dropper = new Thread(() =>
            {
                for (int i = 0; i < 20_000; i++) rb.Publish(new ChaosTick(i));
                Volatile.Write(ref done, true);
            }) { IsBackground = true, Priority = ThreadPriority.BelowNormal };
            dropper.Start();

            dropper.Join(10000); reader.Join(10000);
            Assert.Empty(errors);                   // INV-4 — 격심 추월에도 torn 0
            Assert.True(consumer.Dropped > 0);      // 추월 발생 확인
        }

        [Fact]
        public void C3_SlotSwapStorm_DuringSubmit_NoCrash()
        {
            var specs = new[] { Spec("a"), Spec("b"), Spec("c") };
            var slot  = new StrategySlot(); slot.Swap(specs[0]);
            var gate  = new AlgoExecutionGate(slot, new OrderAttestationService());
            var errors = new ConcurrentQueue<Exception>();
            var cts = new CancellationTokenSource();

            var swapper = new Thread(() =>
            {
                try { int i = 1; while (!cts.IsCancellationRequested) { slot.Swap(specs[i % specs.Length]); i++; } }
                catch (Exception e) { errors.Enqueue(e); }
            }) { IsBackground = true, Priority = ThreadPriority.BelowNormal };

            var submitter = new Thread(() =>
            {
                try
                {
                    var (l, b) = SyncBooks();
                    for (int i = 0; i < 2000; i++)
                    {
                        var active = slot.Active;
                        if (active is null) continue;
                        gate.Submit(Order(active, 10m, 1000m), l, b, 100_000_000m);   // 권한 일치/불일치 모두 무크래시
                    }
                }
                catch (Exception e) { errors.Enqueue(e); }
            }) { IsBackground = true, Priority = ThreadPriority.BelowNormal };

            swapper.Start(); submitter.Start();
            submitter.Join(5000); cts.Cancel(); swapper.Join(5000);
            Assert.Empty(errors);   // INV-4/INV-5
        }

        [Fact]
        public void C4_ConcurrentCorruptBoot_IndependentFailClosed()
        {
            var atts = new OrderAttestationService[4];
            var threads = new List<Thread>();
            for (int t = 0; t < 4; t++)
            {
                int ti = t;
                var path = NewPath();
                WriteSnapshot(path, GatingMode.Normal);
                ChaosMmf.CorruptByte(path, 40);
                atts[ti] = new OrderAttestationService();
                threads.Add(new Thread(() =>
                {
                    var (e, s) = NewEngine(path);
                    BootIntegrityChecker.CheckAndHydrate(e, atts[ti], s);
                }) { IsBackground = true });
            }
            foreach (var th in threads) th.Start();
            foreach (var th in threads) th.Join(5000);

            foreach (var a in atts) Assert.Equal(GatingMode.EmergencyFreeze, a.Mode);   // 각자 독립 fail-closed
        }

        // ════════════════════════ §D 누수·격리 ════════════════════════════════════

        [Fact]
        public void D1_ChaosLeaseBombardment_ConvergesToZero()
        {
            var guard = new MemoryLeakGuard();
            var att   = new OrderAttestationService();
            var threads = new List<Thread>();
            for (int t = 0; t < 4; t++)
                threads.Add(new Thread(() =>
                {
                    for (int i = 0; i < 5000; i++) { var le = guard.Acquire("chaos"); le.Dispose(); }
                }) { IsBackground = true, Priority = ThreadPriority.BelowNormal });
            // 동시 동결 카오스
            threads.Add(new Thread(() => { for (int i = 0; i < 100; i++) { att.ForceFreeze("x"); att.ManualRelease("s"); } })
                { IsBackground = true, Priority = ThreadPriority.BelowNormal });

            foreach (var th in threads) th.Start();
            foreach (var th in threads) th.Join(5000);

            Assert.Equal(0, guard.LiveCount);   // INV-2 — 고아 핸들러 0 수렴
        }

        [Fact]
        public void D2_GreenhouseCycleFault_Chaos_HostSurvives_NoLeak()
        {
            var guard = new MemoryLeakGuard();
            var att   = new OrderAttestationService();
            var gw    = new PromotionGateway(attestation: att);
            using var gh = new GreenhouseService(gw, att, guard)
            { CycleIntervalMs = 5, CycleFaultHook = () => throw new InvalidOperationException("카오스") };
            gh.Start(new List<StrategySpec>(), new List<Candle>(), Market.KRX_KOSPI);

            Assert.True(WaitFor(() => gh.Probe().Errors > 0), "방화벽이 카오스를 흡수해야 함");
            Assert.Equal(GatingMode.Normal, att.Mode);   // INV-5 — 메인 게이트 무사

            gh.Stop();
            Assert.Equal(0, guard.LiveCount);             // INV-2 — 스레드 리스 반납
        }

        [Fact]
        public void D3_ChaosFreeze_LeasesRemainBalanced()
        {
            var guard = new MemoryLeakGuard();
            var leases = new List<ResourceLease>();
            for (int i = 0; i < 50; i++) leases.Add(guard.Acquire("x"));
            Assert.Equal(50, guard.LiveCount);

            // 동결 카오스가 리스 회계를 오염시키지 않음
            var att = new OrderAttestationService();
            att.ForceFreeze("카오스 동결");
            foreach (var le in leases) le.Dispose();

            Assert.Equal(0, guard.LiveCount);   // INV-2
        }

        // ════════════════════════ §E 복구·멱등 ════════════════════════════════════

        [Fact]
        public void E1_DesyncFreeze_ManualReleaseRearm_Recovers()
        {
            var spec = Spec("s");
            var att  = new OrderAttestationService();
            var gate = new AlgoExecutionGate(SlotWith(spec), att);
            var (dl, db) = DesyncBooks();
            gate.Submit(Order(spec, 10m, 1000m), dl, db, 100_000_000m);   // 디싱크 → 동결
            Assert.Equal(AlgoGateState.Frozen, gate.State);

            // 복구: 수동 서명 해제 + 게이트 재무장
            att.ManualRelease("operator-signature", "정합 확인");
            gate.Rearm();
            Assert.Equal(AlgoGateState.Armed, gate.State);

            var (sl, sb) = SyncBooks();
            var v = gate.Submit(Order(spec, 10m, 1000m), sl, sb, 100_000_000m);
            Assert.Equal(ExecOutcome.DryRunLogged, v.Outcome);   // INV-3 — 정상 복귀
        }

        [Fact]
        public void E2_BrokerFaultFreeze_Recovers()
        {
            var spec = Spec("s");
            var att  = new OrderAttestationService();
            var gate = new AlgoExecutionGate(SlotWith(spec), att, new ChaosBroker(ChaosBroker.Mode.Throw)) { IsDryRun = false };
            var (l, b) = SyncBooks();
            gate.Submit(Order(spec, 10m, 1000m), l, b, 100_000_000m);   // 브로커 장애 → 동결
            Assert.Equal(GatingMode.EmergencyFreeze, att.Mode);

            att.ManualRelease("sig"); gate.Rearm();
            Assert.Equal(AlgoGateState.Armed, gate.State);   // INV-3
        }

        [Fact]
        public void E3_FullChaosScenario_SameSeed_SameFinalState()
        {
            (GatingMode mode, int fills) Run(int seed)
            {
                var spec = Spec("s");
                var att  = new OrderAttestationService();
                var gate = new AlgoExecutionGate(SlotWith(spec), att);
                var rng  = new ChaosRandom(seed);
                for (int i = 0; i < 20; i++)
                {
                    var (l, b) = rng.NextBool(0.3) ? DesyncBooks() : SyncBooks();
                    gate.Submit(Order(spec, 10m, 1000m), l, b, 100_000_000m);
                }
                return (att.Mode, gate.VirtualLedger.Count);
            }
            Assert.Equal(Run(42), Run(42));   // INV-6 — 동일 시드 → 동일 최종 상태
        }

        // ── 헬퍼 ─────────────────────────────────────────────────────────────────
        private static bool WaitFor(Func<bool> cond, int timeoutMs = 3000)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < deadline) { if (cond()) return true; Thread.Sleep(5); }
            return cond();
        }
    }

    // ── 카오스 하니스 (테스트 전용 · 프로덕션 미출하 — J1) ──────────────────────────

    /// <summary>결정론적 의사난수 (xorshift32) — seeded 카오스 재현용.</summary>
    file sealed class ChaosRandom
    {
        private uint _s;
        public ChaosRandom(int seed) { _s = unchecked((uint)seed * 2654435761u + 2166136261u); if (_s == 0u) _s = 0x9E3779B9u; }
        private uint Next() { _s ^= _s << 13; _s ^= _s >> 17; _s ^= _s << 5; return _s; }
        public bool NextBool(double prob) => Next() % 10000u < (uint)(prob * 10000.0);
        public int  NextInt(int bound)    => bound <= 0 ? 0 : (int)(Next() % (uint)bound);
        public byte NextByte()            => (byte)(Next() & 0xFFu);
    }

    /// <summary>장애 주입 브로커 — 예외/거부/seeded 혼합.</summary>
    file sealed class ChaosBroker : IBroker
    {
        public enum Mode { Throw, Reject, Ok, Seeded }
        private readonly Mode _mode;
        private readonly ChaosRandom? _rng;
        public int PlaceCount;

        public ChaosBroker(Mode mode, int seed = 0) { _mode = mode; if (mode == Mode.Seeded) _rng = new ChaosRandom(seed); }

        public string Name => "Chaos"; public bool IsLive => true;
        public (bool ok, string msg) Place(PaperAsset a, string s, string n, PaperSide sd, PaperKind k, decimal q, decimal lp, decimal cp)
        {
            PlaceCount++;
            switch (_mode)
            {
                case Mode.Throw:  throw new InvalidOperationException("카오스 주입: 브로커 발주 장애");
                case Mode.Reject: return (false, "카오스 거부");
                case Mode.Seeded:
                    int r = _rng!.NextInt(3);
                    if (r == 0) throw new InvalidOperationException("카오스: 예외");
                    return r == 1 ? (false, "카오스: 거부") : (true, "ok");
                default:          return (true, "ok");
            }
        }
        public void    Cancel(string id) { }
        public decimal HeldQty(PaperAsset a, string s) => 0m;
    }

    /// <summary>MMF 스냅샷 파일 바이트 오염기 (seeded).</summary>
    file static class ChaosMmf
    {
        public static void CorruptByte(string path, int index)
        {
            var bytes = File.ReadAllBytes(path);
            if (index >= 0 && index < bytes.Length) bytes[index] ^= 0xFF;
            File.WriteAllBytes(path, bytes);
        }

        public static void CorruptSeeded(string path, int seed, int regionStart, int regionLen)
        {
            var bytes = File.ReadAllBytes(path);
            var rng   = new ChaosRandom(seed);
            int span  = Math.Min(regionLen, bytes.Length - regionStart);
            if (span <= 0) return;
            int hits = 1 + rng.NextInt(3);
            for (int i = 0; i < hits; i++)
            {
                int pos = regionStart + rng.NextInt(span);
                bytes[pos] ^= (byte)(0x80 | rng.NextByte());
            }
            File.WriteAllBytes(path, bytes);
        }
    }

    /// <summary>torn 검출용 3필드 틱(N·A·B 가 N 에서 파생).</summary>
    file readonly struct ChaosTick
    {
        public readonly long N, A, B;
        public ChaosTick(long n) { N = n; A = n * 2654435761L; B = (n << 20) ^ (n + 0x5DEECE66DL); }
        public bool IsConsistent => A == N * 2654435761L && B == ((N << 20) ^ (N + 0x5DEECE66DL));
    }
}
