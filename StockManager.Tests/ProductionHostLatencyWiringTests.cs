// ============================================================================
// ProductionHostLatencyWiringTests — [v12.17.6 · 추천 A] 실발주 경로 지연 스탬프 프로덕션 결선(링 → 디스패처 → 게이트 → 출구 → /aidash)
//
//  §1 수명 경계 — 실 OrderDispatchRing · GatedOrderDispatcher 에 관측자 결선: 미무장 차단 · 송출 · 하류 veto · throw(동결) 모든 처분 경로에서
//     종결 정확히 1회 · [방향 A] 미도달 주문 슬롯 즉시 회수(축출 0 · 미종결 0) — 관측자 없는 v12.17.5 구성은 1,024 뒤 축출이 쌓임(대조).
//  §2 스탬프 핫패스 — 발행 → 드레인 → 스탬프 게이트 → 출구 프로브 → 종결 전 경로 δ=0 · 스탬프 오버헤드 주문당 < 1µs(중앙값 · 동일 사슬 무계측 대조).
//  §3 /aidash — 호스트와 같은 결선의 2 브로커 드라이런 사슬: 키 off(liveArmed:false) = '계측 대기 · 발행 N · 회수 N' · 키 on = p50/p99 실측 ·
//     미승인 도달 0 · 축출 0.
//  §4 [방향 B] 심각 지연 Disarm — 설계된 드레인 대기만 = 무격발 · 예산 50ms 고정이면 그 대기만으로 오격발(예산 설계 근거 실측) · 병목 60ms =
//     연속 3회 격발 → 비트 28 → 집계어 차단 · 예산 이하 1회 = 연속 리셋 · 새 표본 없음 = 판정 아님 · 표본 하한 · 재무장(물리 이중키 · 기준선) · δ=0.
//  §5 바이너리 · 결선 — 빌드 DLL ShippedBinaryLiveArmedGuard Ok(시뮬레이션 격리 위반 0 · 데코레이터 = Telemetry 네임스페이스) · 호스트 조립부 IL 이
//     데코레이터 · 지연 인터락 생성자를 실제로 호출 · 소스 결선 · 래칫 ≤ 40 · 오중 빌드.
// ============================================================================
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using MS2E.Core;
using MS2E.Infrastructure;
using MS2E.UI.Portable.Helpers;
using MS2E.UI.Portable.Ports.Trading;
using StockManager.Blazor.Services;
using StockManager.Simulation;
using StockManager.Telemetry;
using StockManager.Tools;
using Xunit;
using Xunit.Abstractions;

namespace StockManager.Tests
{
    [Collection("ConcurrencyStress")]
    public sealed class ProductionHostLatencyWiringTests
    {
        private const long Ns = 1_000_000_000;
        private const long Ms = 1_000_000;                                       // ns
        private readonly ITestOutputHelper _out;
        public ProductionHostLatencyWiringTests(ITestOutputHelper o) => _out = o;

        private static long MinAllocated(Action body, int windows = 3)
        {
            long min = long.MaxValue;
            for (int w = 0; w < windows && min != 0; w++)
            {
                long b = GC.GetAllocatedBytesForCurrentThread();
                body();
                min = Math.Min(min, GC.GetAllocatedBytesForCurrentThread() - b);
            }
            return min;
        }

        private static string RepoRoot()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d is not null && !File.Exists(Path.Combine(d.FullName, "StockManager.sln"))) d = d.Parent;
            Assert.NotNull(d);
            return d!.FullName;
        }

        private static void Publish(OrderDispatchRing ring, int i)
            => ring.Publish(1, 5930, OrderSide.Buy, 1, new QPrice(70_000L * 100_000_000), new QQty(10L * 100_000_000), i);

        /// <summary>판정 위임 대상 게이트(승인/거부 스위치 · 할당 0).</summary>
        private sealed class SwitchGate : IChildSplitRouteGate
        {
            public bool Admit = true;
            public bool TryAdmitSplit(in OrderProposal proposal, Span<ChildOrderPlan> children, out int count) { count = 0; return Admit; }
            public bool TryAdmit(in OrderProposal proposal, out OrderAdmission admission)
            {
                admission = new OrderAdmission(proposal.KindByte, proposal.PriceRaw, proposal.QtyRaw);
                return Admit;
            }
            public void OnBrokerAccepted(long proposalId, string brokerOrderId) { }
            public void OnNotSent(long proposalId) { }
        }

        /// <summary>출구 센티널 대역(판정 스위치 · 할당 0 — 응답 문자열 상수).</summary>
        private sealed class VerdictBroker : IBroker
        {
            public bool Forward;
            public long Places;
            public string Name => "verdict";
            public bool IsLive => false;
            public (bool ok, string msg) Place(PaperAsset asset, string symbol, string name, PaperSide side, PaperKind kind, decimal qty, decimal limitPrice, decimal currentPrice)
            {
                Places++;
                return Forward ? (true, "1") : (false, "veto");
            }
            public void Cancel(string orderId) { }
            public decimal HeldQty(PaperAsset asset, string symbol) => 0m;
        }

        /// <summary>호스트 드레인의 핵심 순서(게이트 → 출구 Place) 축약 — 실 OrderDrainWorker 는 §3 드라이런 사슬이 쓴다.</summary>
        private static bool Send(IChildSplitRouteGate gate, IBroker broker, in OrderProposal p)
        {
            if (!gate.TryAdmit(in p, out _)) return false;
            return broker.Place(PaperAsset.Stock, "005930", "005930", PaperSide.Buy, PaperKind.Limit, 10m, 70_000m, 70_000m).ok;
        }

        // ════════════ §1 수명 경계 · [방향 A] 슬롯 즉시 회수 ════════════

        [Fact]
        public void Lifecycle_EveryDispositionEndsOnce_UnreachedSlotsReclaimed_NoEviction()
        {
            var ring = new OrderDispatchRing(4096);
            var prof = new OrderPathStageProfiler(1024);
            ring.PathObserver = prof;
            bool armed = false;
            var disp = new GatedOrderDispatcher(ring, () => armed);
            var gate = new StageStampingSplitGate(new SwitchGate(), prof);
            var exit = new VerdictBroker();
            var probe = new EgressStageProbe(exit, prof);

            // ① 미무장(liveArmed:false) 3,000건 — 전부 무장 게이트 차단 → 회수 3,000 · 축출 0 · 미종결 0
            for (int i = 0; i < 3_000; i++) { Publish(ring, i); if ((i & 511) == 511) disp.Drain(p => Send(gate, probe, in p)); }
            disp.Drain(p => Send(gate, probe, in p));
            Assert.Equal(3_000, prof.Begun);
            Assert.Equal(3_000, prof.Reclaimed);
            Assert.Equal(0, prof.Evicted);
            Assert.Equal(0, prof.Outstanding);
            Assert.Equal(0, prof.RecentTotal);
            Assert.Equal(0, exit.Places);

            // ② 무장 · 게이트 거부 → 회수(게이트 데코레이터 즉시 + 종결 멱등)
            armed = true;
            ((SwitchGate)gate.Inner).Admit = false;
            for (int i = 0; i < 100; i++) Publish(ring, i);
            disp.Drain(p => Send(gate, probe, in p));
            Assert.Equal(3_100, prof.Reclaimed);
            Assert.Equal(0, prof.UnknownGate);

            // ③ 무장 · 게이트 승인 · 출구 veto(liveArmed:false 센티널 대역) → 출구 판정까지 시간 기록(완료) · 송출 계상 0
            ((SwitchGate)gate.Inner).Admit = true;
            for (int i = 0; i < 100; i++) Publish(ring, i);
            disp.Drain(p => Send(gate, probe, in p));
            Assert.Equal(100, prof.Completed);
            Assert.Equal(100, prof.EgressVetoed);
            Assert.Equal(0, disp.Forwarded);

            // ④ 출구 전달 → 완료 · 송출 계상
            exit.Forward = true;
            for (int i = 0; i < 100; i++) Publish(ring, i);
            disp.Drain(p => Send(gate, probe, in p));
            Assert.Equal(200, prof.Completed);
            Assert.Equal(100, prof.EgressForwarded);
            Assert.Equal(100, disp.Forwarded);

            // ⑤ 하류 throw(Mission-A 벽 · 루프 break) — 그 주문도 종결 1회(회수) · 남은 주문은 다음 드레인에서 종결
            int k = 0;
            for (int i = 0; i < 5; i++) Publish(ring, i);
            disp.Drain(p => { if (k++ == 2) throw new InvalidOperationException("단절"); return Send(gate, probe, in p); });
            disp.Drain(p => Send(gate, probe, in p));
            Assert.Equal(1, disp.SendErrors);
            Assert.Equal(prof.Begun, prof.Completed + prof.Reclaimed);                               // 모든 발행 = 정확히 1회 종결
            Assert.Equal(0, prof.Outstanding);
            Assert.Equal(0, prof.Evicted);
            Assert.Equal(0, prof.EgressOrphans);
        }

        [Fact]
        public void Contrast_WithoutDispositionObserver_UnreachedSlotsLeak_AndAreMiscountedAsEvictions()
        {
            // v12.17.5 이전 구성(발행 스탬프만 · 종결 관측 없음) — 미무장 차단 주문 슬롯이 남아 1,024 뒤부터 '축출'로 오계수
            var prof = new OrderPathStageProfiler(1024);
            for (long id = 1; id <= 3_000; id++) prof.BeginOrder(id, prof.Timestamp());
            Assert.Equal(3_000 - 1_024, prof.Evicted);
            Assert.Equal(1_024, prof.Outstanding);
            // 회수는 태그 일치만(CAS) — 이미 덮어쓴 옛 주문 회수 요청은 새 주문을 지우지 않는다
            Assert.False(prof.Abandon(1));
            Assert.True(prof.Abandon(3_000));
            Assert.False(prof.Abandon(3_000));                                                      // 멱등
        }

        // ════════════ §2 스탬프 핫패스 δ=0 · < 1µs ════════════

        [Fact]
        public void StampedPath_ZeroAlloc_OverheadUnderOneMicrosecondPerOrder()
        {
            var ring = new OrderDispatchRing(4096);
            var prof = new OrderPathStageProfiler(1024);
            var disp = new GatedOrderDispatcher(ring, () => true);
            var exit = new VerdictBroker { Forward = true };
            var gate = new SwitchGate();
            var sGate = new StageStampingSplitGate(gate, prof);
            var sExit = new EgressStageProbe(exit, prof);
            Func<OrderProposal, bool> plain = p => Send(gate, exit, in p);
            Func<OrderProposal, bool> stamped = p => Send(sGate, sExit, in p);
            const int Batch = 256;

            void Run(Func<OrderProposal, bool> fn, int batches) { for (int b = 0; b < batches; b++) { for (int i = 0; i < Batch; i++) Publish(ring, i); disp.Drain(fn); } }
            ring.PathObserver = prof;
            Run(stamped, 50);                                                                       // 워밍업(JIT)
            ring.PathObserver = null;
            Run(plain, 50);

            ring.PathObserver = prof;
            Assert.Equal(0, MinAllocated(() => Run(stamped, 40)));                                  // 발행 → 드레인 → 게이트 → 출구 → 종결 δ=0
            Assert.Equal(prof.Begun, prof.Completed + prof.Reclaimed);

            // 오버헤드 = 최소(계측 사슬) − 최소(무계측 사슬) 주문당 · 교대 측정 25회(최솟값 = 스케줄러/GC/병렬 테스트 간섭이 가장 적은 창 —
            //   마이크로벤치 표준. 중앙값 차분은 병렬 테스트 컬렉션 부하에 수백 ns 흔들려 판정 불능).
            double plainMin = double.MaxValue, stampMin = double.MaxValue;
            for (int r = 0; r < 25; r++)
            {
                ring.PathObserver = null;
                long t0 = Stopwatch.GetTimestamp(); Run(plain, 40); double pt = (Stopwatch.GetTimestamp() - t0) * 1e9 / Stopwatch.Frequency / (40.0 * Batch);
                ring.PathObserver = prof;
                t0 = Stopwatch.GetTimestamp(); Run(stamped, 40); double st = (Stopwatch.GetTimestamp() - t0) * 1e9 / Stopwatch.Frequency / (40.0 * Batch);
                plainMin = Math.Min(plainMin, pt); stampMin = Math.Min(stampMin, st);
            }
            double overhead = stampMin - plainMin;
            _out.WriteLine($"[스탬프 오버헤드] 주문당 {overhead:F0}ns (계측 {stampMin:F0}ns · 무계측 {plainMin:F0}ns) · 완료 {prof.Completed:N0}");
            Assert.True(overhead < 1_000, $"스탬프 오버헤드 {overhead:F0}ns ≥ 1µs");
            Assert.Equal(0, prof.Evicted);
        }

        // ════════════ §3 /aidash — 호스트 동일 결선 드라이런 사슬 ════════════

        private const long Sym = 5930, Mid = 70_000, Tick = 100;

        private static void StrongBuy(MultiBrokerDryRunHarness h)
        {
            for (int k = 0; k < 6; k++)
            {
                double imb10 = -0.05 + 0.2 * Math.Min(k, 5);
                long bid10 = (long)Math.Round(5_000 * (1 + imb10)), ask10 = 10_000 - bid10;
                h.OnBook(Sym, Mid, 950, 50, (bid10 - 4_750) / 5, (ask10 - 250) / 5);
            }
        }

        [Fact]
        public void Dashboard_KeysOff_HonestWaitingWithCounts_KeysOn_LiveP50P99_ZeroUnsanctioned()
        {
            // liveArmed:false(물리 키 off · 기본) — 디스패처 무장 게이트 차단 → 출구 0 · 발행/회수 계수만
            using var off = new MultiBrokerDryRunHarness(confidenceThreshold: 0.5);
            var hubOff = new DashboardSnapshotHub(off.Interlocks, orderPath: off.Stages);
            for (int i = 0; i < 20; i++) { StrongBuy(off); off.SubmitFromSignal(Sym, OrderSide.Buy, 10, Mid + 3 * Tick, force: true); off.Pump(); }
            hubOff.Pump();
            var w = hubOff.Acquire()!;
            Assert.True(w.LatencyBound);
            Assert.Equal(DashLatencyGrade.Unmeasured, w.LatencyGrade);
            Assert.Equal(20, w.LatencyPublished);
            Assert.Equal(20, w.LatencyReclaimed);
            Assert.Equal(0, w.Latency(DashLatencyGrade.SignalToEgress).Count);
            Assert.Equal(0, off.InnerPlaces);
            Assert.Equal(0, off.Stages.Evicted);
            hubOff.Release(w);

            // 키 on — 실 사슬(OrderDrainWorker · RouterFeedGate · 센티널) 실측 p50/p99
            using var h = new MultiBrokerDryRunHarness(confidenceThreshold: 0.5);
            h.SetPhysicalKeys(true, true);
            h.SetCaps(60, 40);
            var hub = new DashboardSnapshotHub(h.Interlocks, h.Matrix, orderPath: h.Stages);
            int sent = 0;
            for (int i = 0; i < 300; i++)
            {
                StrongBuy(h);
                if (h.SubmitFromSignal(Sym, OrderSide.Buy, 10, Mid + 3 * Tick) > 0) sent++;
                h.Pump();
                h.Advance(1_000);
            }
            hub.Pump();
            var f = hub.Acquire()!;
            var e2e = f.Latency(DashLatencyGrade.SignalToEgress);
            _out.WriteLine($"[aidash] 발행 {f.LatencyPublished} · 완료 {e2e.Count} · 회수 {f.LatencyReclaimed} · p50 {SignalLabelMapper.LatencyText(e2e.P50Nanos)} · p99 {SignalLabelMapper.LatencyText(e2e.P99Nanos)} · 등급 {SignalLabelMapper.LatencyGradeLabel(f.LatencyGrade)}");
            Assert.True(sent >= 100, "발행 " + sent);
            Assert.NotEqual(DashLatencyGrade.Unmeasured, f.LatencyGrade);                           // ★미계측 해소
            Assert.True(e2e.Count >= 100 && e2e.P50Nanos > 0 && e2e.P99Nanos >= e2e.P50Nanos);
            Assert.Equal(f.LatencyPublished, h.Stages.Completed + h.Stages.Reclaimed);              // 발행 전부 종결
            Assert.Equal(0, h.Stages.Evicted);
            Assert.Equal(0, h.Stages.EgressOrphans);
            Assert.Equal(0, h.UnsanctionedPlaces);
            hub.Release(f);
        }

        // ════════════ §4 [방향 B] 심각 지연 Disarm ════════════

        /// <summary>주입 시계 계측기(1틱 = 1ns) — 부모 1건의 S2 를 정확히 지정.</summary>
        private sealed class Clocked
        {
            public long T = 1_000;
            public readonly OrderPathStageProfiler P;
            private long _id;
            public Clocked() => P = new OrderPathStageProfiler(1024, () => T, Ns);
            public void Order(long queueNs, long processNs)
            {
                long id = ++_id;
                P.OnPublished(id);
                T += queueNs; P.MarkGate(id);
                long e = T; T += processNs; P.MarkEgress(e, false);
                P.OnDispatchEnd(id, false);
            }
        }

        private static ArmingInterlockBitmask Mask(OrderPathLatencyInterlock il)
        {
            var m = new ArmingInterlockBitmask(ArmingInterlockSource.OrderPathLatency);
            il.BindInterlock(m, ArmingInterlockSource.OrderPathLatency);
            Assert.True(m.Seal(out _));
            return m;
        }

        [Fact]
        public void LatencyDisarm_DesignedDrainWaitNeverTrips_LiteralBudgetWouldFalseTrip_BottleneckTripsAfterThree()
        {
            var rng = new Random(20261013);
            long hostBudget = OrderPathLatencyInterlock.HostBudgetNanos(50);
            Assert.Equal(100 * Ms, hostBudget);

            // 설계된 링 대기(0~50ms 균일 · 드레인 주기) + 처리 1ms — 호스트 예산 = 무격발 · 오더 문자 그대로(50ms) = 오격발
            var c = new Clocked();
            var host = new OrderPathLatencyInterlock(c.P, hostBudget);
            var literal = new OrderPathLatencyInterlock(c.P, 50 * Ms);
            var hostMask = Mask(host);
            for (int round = 0; round < 30; round++)
            {
                for (int i = 0; i < 50; i++) c.Order((long)(rng.NextDouble() * 50 * Ms), 1 * Ms);
                host.Evaluate(); literal.Evaluate();
            }
            _out.WriteLine($"[방향 B] 설계 대기만: p99 {host.LastP99Nanos / 1e6:F1}ms · 호스트 예산 Trip {host.Trips} · 50ms 고정 예산 Trip {literal.Trips}");
            Assert.False(host.Tripped);
            Assert.False(hostMask.Blocked);
            Assert.True(literal.Tripped);                                                           // ★예산 설계 근거: 정상 운용 오격발

            // 병목: 설계 대기 + 60ms 정체가 5% 주문 → 연속 3 판정에서 격발
            var b = new Clocked();
            var il = new OrderPathLatencyInterlock(b.P, hostBudget);
            var mask = Mask(il);
            for (int i = 0; i < 200; i++) b.Order((long)(rng.NextDouble() * 50 * Ms), 1 * Ms);
            Assert.False(il.Evaluate());
            Assert.Equal(0, il.Streak);
            for (int round = 1; round <= 3; round++)
            {
                for (int i = 0; i < 20; i++) b.Order((long)(rng.NextDouble() * 50 * Ms), (i == 0 ? 60 : 1) * Ms + 50 * Ms);
                bool tripped = il.Evaluate();
                Assert.Equal(round == 3, tripped);
            }
            Assert.True(il.Tripped);
            Assert.True(mask.IsRaised(ArmingInterlockSource.OrderPathLatency));
            Assert.True(mask.Blocked);                                                               // ★무장 차단(liveArmed:false)
            Assert.True(il.LastP99Nanos > hostBudget);
        }

        [Fact]
        public void LatencyDisarm_StreakResets_NoFreshNoCount_MinSamples_RearmNeedsBothKeys_Baseline_ZeroAlloc()
        {
            var c = new Clocked();
            var il = new OrderPathLatencyInterlock(c.P, 10 * Ms, consecutive: 3, minSamples: 100);
            var mask = Mask(il);
            for (int i = 0; i < 99; i++) c.Order(0, 20 * Ms);
            Assert.False(il.Evaluate());
            Assert.Equal(0, il.Judgements);                                                          // 표본 99 < 하한
            c.Order(0, 20 * Ms);
            il.Evaluate(); Assert.Equal(1, il.Streak);
            Assert.False(il.Evaluate()); Assert.Equal(1, il.Streak);                                 // 새 표본 없음 = 판정 아님(계수 유지)
            c.Order(0, 20 * Ms); il.Evaluate(); Assert.Equal(2, il.Streak);
            for (int i = 0; i < 1_000; i++) c.Order(0, 1 * Ms);                                     // 창 전체 정상
            il.Evaluate(); Assert.Equal(0, il.Streak);                                               // 예산 이하 1회 = 리셋
            for (int r = 0; r < 3; r++) { for (int i = 0; i < 30; i++) c.Order(0, 20 * Ms); il.Evaluate(); }
            Assert.True(il.Tripped);
            Assert.True(mask.Blocked);

            Assert.False(il.TryRearm(true, false));
            Assert.False(il.TryRearm(false, true));
            Assert.True(il.Tripped);
            Assert.True(il.TryRearm(true, true));
            Assert.False(il.Tripped);
            Assert.False(mask.Blocked);
            for (int i = 0; i < 50; i++) c.Order(0, 1 * Ms);
            Assert.False(il.Evaluate());
            Assert.Equal(0, il.Streak);                                                              // 기준선: 재무장 이전 정체 표본 재판정 0(새 표본 50 < 하한)

            Assert.Throws<ArgumentOutOfRangeException>(() => new OrderPathLatencyInterlock(c.P, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => new OrderPathLatencyInterlock(c.P, consecutive: 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => new OrderPathLatencyInterlock(c.P, minSamples: 1_001));

            var z = new Clocked();
            var zl = new OrderPathLatencyInterlock(z.P, 100 * Ms);
            for (int i = 0; i < 1_000; i++) z.Order(0, 1 * Ms);
            zl.Evaluate();
            Assert.Equal(0, MinAllocated(() => { for (int k = 0; k < 200; k++) { z.Order(0, 1 * Ms); zl.Evaluate(); } }));
            Assert.True(zl.Judgements >= 200);
        }

        // ════════════ §5 바이너리 · 결선 ════════════

        [Fact]
        public void ShippedBinary_GuardOk_StampingIsProductionNamespace_HostIlCallsDecorators_SourceWiring_Ratchet_FiveBuilds()
        {
            var host = typeof(ReleasePackager).Assembly;
            var r = ShippedBinaryLiveArmedGuard.Scan(new MemoryStream(File.ReadAllBytes(host.Location)));
            _out.WriteLine($"[IL] 키 {r.Sites} · 마스터 {r.MasterSites} · UI {r.UiGuardSites}/3 · 시뮬레이션 형식 {r.SimulationTypes} · CLI 참조 {r.SimulationCliSites} · 위반 {r.Violations.Count}");
            Assert.True(r.Ok, string.Join(" | ", r.Violations));
            Assert.Empty(r.Violations);
            Assert.True(r.SimulationTypes >= 5);

            foreach (var t in new[] { typeof(StageStampingSplitGate), typeof(EgressStageProbe), typeof(OrderPathStageProfiler), typeof(OrderPathLatencyInterlock), typeof(LatencyHistogram) })
                Assert.Equal("StockManager.Telemetry", t.Namespace);
            Assert.DoesNotContain(host.GetTypes(), t => t.Namespace == "StockManager.Simulation" && (t.Name == "StageStampingSplitGate" || t.Name == "EgressStageProbe"));
            Assert.True(typeof(IOrderPathObserver).IsAssignableFrom(typeof(OrderPathStageProfiler)));

            // 호스트 조립부(BlazorHomeForm · 컴파일러 생성 중첩 포함) IL 이 데코레이터 · 계측기 · 지연 인터락 생성자와 PathObserver setter 를 실제로 호출
            var form = host.GetType("StockManager.Blazor.BlazorHomeForm")!;
            var targets = new MethodBase[]
            {
                typeof(StageStampingSplitGate).GetConstructors()[0], typeof(EgressStageProbe).GetConstructors()[0],
                typeof(OrderPathStageProfiler).GetConstructors()[0], typeof(OrderPathLatencyInterlock).GetConstructors()[0],
                typeof(OrderDispatchRing).GetProperty(nameof(OrderDispatchRing.PathObserver))!.SetMethod!,
                typeof(OrderPathLatencyInterlock).GetMethod(nameof(OrderPathLatencyInterlock.Evaluate))!,
            };
            var found = new bool[targets.Length];
            const BindingFlags All = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            foreach (var type in new[] { form }.Concat(form.GetNestedTypes(BindingFlags.NonPublic | BindingFlags.Public)))
                foreach (var m in type.GetMethods(All).Cast<MethodBase>().Concat(type.GetConstructors(All)))
                {
                    var il = m.GetMethodBody()?.GetILAsByteArray();
                    if (il is null) continue;
                    for (int i = 0; i + 4 < il.Length; i++)
                        if (il[i] is 0x73 or 0x28 or 0x6F)
                            try
                            {
                                var mb = m.Module.ResolveMethod(BitConverter.ToInt32(il, i + 1));
                                for (int k = 0; k < targets.Length; k++) if (mb == targets[k]) found[k] = true;
                            }
                            catch (ArgumentException) { }
                }
            for (int k = 0; k < targets.Length; k++) Assert.True(found[k], "결선 누락: " + targets[k].DeclaringType!.Name + "." + targets[k].Name);

            string root = RepoRoot();
            string src = File.ReadAllText(Path.Combine(root, "StockManager", "Blazor", "BlazorHomeForm.cs"));
            Assert.Contains("dispatchRing.PathObserver = orderPathProfiler;", src);
            Assert.Contains("routeGate: new StockManager.Telemetry.StageStampingSplitGate(routerGate, orderPathProfiler)", src);
            Assert.Contains("brokersByIndex: stampedBrokerMap);", src);
            Assert.Contains("orderPath: orderPathProfiler", src);
            Assert.Contains("MS2E.Core.ArmingInterlockSource.OrderPathLatency, //", src);               // 필수 비트
            Assert.Contains("orderPathLatency.BindInterlock(interlockMask, MS2E.Core.ArmingInterlockSource.OrderPathLatency);", src);
            Assert.Contains("try { orderPathLatency.Evaluate(); } catch { }", src);
            Assert.Contains("periodMs: orderDrainPeriodMs,", src);
            Assert.DoesNotContain("StockManager.Simulation", src);
            Assert.Equal(28, (int)ArmingInterlockSource.OrderPathLatency);
            Assert.Equal("주문 경로 지연(p99)", SignalLabelMapper.Interlock((int)ArmingInterlockSource.OrderPathLatency));

            var fit = PureShellFitnessSentinel.Scan(typeof(CanaryHealthSentinel).Assembly);
            Assert.True(fit.WithinRatchet && fit.HostCoupledUi <= 40, $"coupled {fit.HostCoupledUi}");
            Assert.True(PureShellFitnessSentinel.RatchetCeiling <= 40);
            string sln = File.ReadAllText(Path.Combine(root, "StockManager.sln"));
            foreach (var proj in new[] { @"StockManager\StockManager.csproj", @"StockManager.PrimesBench\StockManager.PrimesBench.csproj",
                                         @"StockManager.KiwoomProxy\StockManager.KiwoomProxy.csproj", @"tools\KiwoomComShim\KiwoomComShim.csproj" })
                Assert.Contains(proj, sln);
            Assert.Contains("WebOnly", File.ReadAllText(Path.Combine(root, "StockManager", "StockManager.csproj")));
        }
    }
}
