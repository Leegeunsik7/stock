// ============================================================================
// ZeroDowntimeRolloverTests — [롤오버 · 헬스 · 추천 A]
//
//  §1 ProcessRolloverGuard — 발행→승계→Ack 왕복(필드 보존 · 승계 슬롯 체결 대사) · 미결속 송신 존재 = 승계 거부 · 체크섬 1바이트 변조 =
//     불일치 sticky(Ack 없음 → 구 프로세스 종료 불허) · 중복 번호 = 불일치 · 철회 · 맵 없음 · epoch 재발행 Ack 재요구 · 대사 전 차단 · δ=0.
//  §2 HealthMonitoringAgent — Working Set 폭주 · 스레드풀 정체(실행 지연 주입 · 미실행 대기 시간 포함) · GC 정지 폭주 · 기동 유예 ·
//     실 스레드 가동/처분 · δ=0(Scan + 하트비트).
//  §3 [방향 A+B] HostHeartbeatWriter/HostHeartbeatEgressGate — 신선 = 허용 · 200ms 초과 · 트립 플래그 · 맵 없음 · 기록 중(홀수 seq) = 거부 · δ=0.
//  §4 호스트/프록시 결선 · ExecutionBridgeBroker ID 하한 · 오중 빌드 회귀.
// ============================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using MS2E.Core;
using MS2E.Infrastructure;
using StockManager.Adapters;
using StockManager.Services;
using StockManager.Telemetry;
using Xunit;

namespace StockManager.Tests
{
    [Collection("ConcurrencyStress")]
    public sealed class ZeroDowntimeRolloverTests
    {
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

        private static string MapName() => "Local\\MS2E_TEST_ROLL_" + Guid.NewGuid().ToString("N");

        private sealed class Exec
        {
            public long Now = 5_000_000;
            public ResilientOrderRouter Router = null!;
            public OrderExecutionFeedAdapter Feed = null!;
        }

        private static Exec MakeExec()
        {
            var e = new Exec();
            var board = new LockFreeBrokerSwitchboard(1);
            var pending = new PendingOrderCascadeCanceller(64);
            e.Router = new ResilientOrderRouter(board, new BrokerFailoverSentinel(board, pendingOrders: pending), pending, idMapCapacityPow2: 256, maxSlippageBps: 50);
            e.Feed = new OrderExecutionFeedAdapter(e.Router, 64, 120_000_000, () => Volatile.Read(ref e.Now));
            return e;
        }

        private static void Accepted(Exec e, long proposal, long brokerId, long qty = 10, bool buy = true)
        {
            var key = RouterFeedGate.KeyFor(proposal);
            Assert.True(e.Router.Route(key, 7_000_000_000_000, 7_000_000_000_000, 0.0).Accepted);
            Assert.True(e.Feed.TryTrack(proposal, key, 0, qty, buy, 7_000_000_000_000));
            Assert.True(e.Feed.BindBrokerOrder(proposal, brokerId));
        }

        private static FillPayload Fill(long brokerId, long qty)
            => new() { OrderId = brokerId, SymbolCode = 5930, FillQty = qty, FillPrice = 70_000, Side = 1, Status = BridgeFillStatusCodec.Encode(BridgeFillStatus.Filled, false) };

        // ════════════ §1 롤오버 ════════════

        [Fact]
        public void Publish_Adopt_Ack_RoundTrip_FieldsPreserved_AdoptedSlotReconcilesByFill()
        {
            string name = MapName();
            var oldExec = MakeExec();
            Accepted(oldExec, 1, 1001, 10);
            Accepted(oldExec, 2, 1002, 5, buy: false);
            Accepted(oldExec, 3, 1003, 7);
            oldExec.Feed.OnFill(Fill(1003, 2));                                  // 부분 체결 2/7
            oldExec.Now += 3_000_000;
            using var oldGuard = ProcessRolloverGuard.CreatePublisher(name, 16);
            Assert.Equal(RolloverPublishResult.Published, oldGuard.Publish(oldExec.Feed, 42));
            Assert.False(oldGuard.IsAcked());
            Assert.False(oldGuard.WaitForAck(TimeSpan.FromMilliseconds(20)));     // ★Ack 전 = 구 프로세스 종료 불허

            using var newGuard = ProcessRolloverGuard.OpenAdopter(name, 16)!;
            var mask = new ArmingInterlockBitmask(ArmingInterlockSource.Rollover);
            newGuard.BindInterlock(mask, ArmingInterlockSource.Rollover);
            Assert.True(mask.Seal(out _));
            var newExec = MakeExec();
            Assert.Equal(RolloverAdoptResult.Adopted, newGuard.TryAdopt(newExec.Feed));
            Assert.Equal(3, newExec.Feed.Tracked);
            Assert.Equal(3, newGuard.AdoptedCount);
            Assert.True(oldGuard.IsAcked() && oldGuard.WaitForAck(TimeSpan.Zero));   // ★Ack 확인 = 안전 종료 허가
            Assert.True(mask.Blocked);                                           // ★브로커 대사 전 신규 발주 차단
            Span<HandoffSlot> back = new HandoffSlot[8];
            Assert.Equal(3, newExec.Feed.ExportSlots(back));
            var s3 = FindSlot(back, 1003);
            Assert.Equal((3L, 7L, 2L, 7_000_000_000_000L, 1), (s3.ProposalId, s3.OrderQty, s3.Filled, s3.ReferenceRaw, s3.Flags & 1));
            Assert.Equal(3_000_000, s3.AgeMicros);                               // 경과 시간 보존(승계 측 시계로 재기준)
            Assert.Equal(0, FindSlot(back, 1002).Flags & 1);                     // 매도 플래그 보존
            newExec.Feed.OnFill(Fill(1003, 5));                                  // 승계 슬롯 잔량 체결 → 대사(라우터 기록 없이도 종결)
            newExec.Feed.OnFill(Fill(1001, 10));
            Assert.Equal(1, newExec.Feed.Tracked);
            newGuard.MarkReconciled();
            Assert.False(mask.Blocked);
        }

        private static HandoffSlot FindSlot(ReadOnlySpan<HandoffSlot> s, long brokerId)
        {
            foreach (var x in s) if (x.BrokerOrderId == brokerId) return x;
            throw new InvalidOperationException("slot");
        }

        [Fact]
        public void Publish_RefusedWhileUnboundInFlight()
        {
            string name = MapName();
            var e = MakeExec();
            Accepted(e, 1, 1001);
            var key = RouterFeedGate.KeyFor(2);
            Assert.True(e.Router.Route(key, 7_000_000_000_000, 7_000_000_000_000, 0.0).Accepted);
            Assert.True(e.Feed.TryTrack(2, key, 0, 10));                         // 송신했으나 브로커 번호 미수신
            Assert.Equal(1, e.Feed.InFlightUnbound);
            using var g = ProcessRolloverGuard.CreatePublisher(name, 16);
            Assert.Equal(RolloverPublishResult.NotQuiescent, g.Publish(e.Feed, 1));
            using var adopter = ProcessRolloverGuard.OpenAdopter(name, 16)!;
            Assert.Equal(RolloverAdoptResult.NoHandoff, adopter.TryAdopt(MakeExec().Feed));   // 미발행 = 승계 없음
            Assert.Equal(RolloverPublishResult.CapacityExceeded, ProcessRolloverGuard.CreatePublisher(MapName(), 1).Publish(BuildTwo(), 1));
        }

        private static OrderExecutionFeedAdapter BuildTwo() { var e = MakeExec(); Accepted(e, 1, 11); Accepted(e, 2, 12); return e.Feed; }

        [Fact]
        public void ChecksumTamper_OneByte_StickyMismatch_NoAck_NothingImported()
        {
            string name = MapName();
            var e = MakeExec();
            Accepted(e, 1, 1001, 10);
            using var oldGuard = ProcessRolloverGuard.CreatePublisher(name, 16);
            Assert.Equal(RolloverPublishResult.Published, oldGuard.Publish(e.Feed, 7));
            using (var raw = MemoryMappedFile.OpenExisting(name))
            using (var v = raw.CreateViewAccessor())
            {
                int qtyOffset = 128 + (int)Marshal.OffsetOf<HandoffSlot>(nameof(HandoffSlot.OrderQty));
                v.Write(qtyOffset, v.ReadInt64(qtyOffset) + 1);                  // ★수량 1 변조
            }
            using var newGuard = ProcessRolloverGuard.OpenAdopter(name, 16)!;
            var newExec = MakeExec();
            Assert.Equal(RolloverAdoptResult.ChecksumMismatch, newGuard.TryAdopt(newExec.Feed));
            Assert.Equal(0, newExec.Feed.Tracked);                               // 검증 전 가져오기 0
            Assert.True(newGuard.Mismatch && newGuard.ArmingBlocked);
            newGuard.MarkReconciled();
            Assert.True(newGuard.ArmingBlocked);                                 // ★불일치는 대사로 해제 불가(sticky)
            Assert.False(oldGuard.WaitForAck(TimeSpan.FromMilliseconds(20)));     // Ack 없음 = 구 프로세스 종료 불허
        }

        [Fact]
        public void DuplicateBrokerId_ImportFails_Mismatch_Abort_And_Reack()
        {
            string name = MapName();
            var e = MakeExec();
            Accepted(e, 1, 1001);
            using var oldGuard = ProcessRolloverGuard.CreatePublisher(name, 16);
            Assert.Equal(RolloverPublishResult.Published, oldGuard.Publish(e.Feed, 1));
            var newExec = MakeExec();
            Accepted(newExec, 9, 1001);                                          // 같은 브로커 번호를 이미 추적 중(충돌)
            using (var g = ProcessRolloverGuard.OpenAdopter(name, 16)!)
            {
                Assert.Equal(RolloverAdoptResult.ImportFailed, g.TryAdopt(newExec.Feed));
                Assert.True(g.Mismatch);
            }
            Assert.Equal(RolloverPublishResult.Published, oldGuard.Publish(e.Feed, 2));
            oldGuard.Abort();
            using (var g2 = ProcessRolloverGuard.OpenAdopter(name, 16)!)
            {
                Assert.Equal(RolloverAdoptResult.Aborted, g2.TryAdopt(MakeExec().Feed));   // 철회 = 승계 거부(불일치 아님)
                Assert.False(g2.Mismatch);
            }
            Assert.Equal(RolloverPublishResult.Published, oldGuard.Publish(e.Feed, 3));
            using (var g3 = ProcessRolloverGuard.OpenAdopter(name, 16)!) Assert.Equal(RolloverAdoptResult.Adopted, g3.TryAdopt(MakeExec().Feed));
            Assert.True(oldGuard.IsAcked());
            Assert.Equal(RolloverPublishResult.Published, oldGuard.Publish(e.Feed, 4));   // 재발행 = 이전 Ack 무효(epoch 4)
            Assert.False(oldGuard.IsAcked());
            Assert.Null(ProcessRolloverGuard.OpenAdopter(MapName(), 16));           // 맵 없음 = 신규 기동
        }

        [Fact]
        public void ZeroAlloc_PublishAdoptAck()
        {
            string name = MapName();
            var e = MakeExec();
            for (int i = 1; i <= 20; i++) Accepted(e, i, 1000 + i);
            using var pub = ProcessRolloverGuard.CreatePublisher(name, 64);
            using var adopt = ProcessRolloverGuard.OpenAdopter(name, 64)!;
            const int Rounds = 30;
            var targets = new OrderExecutionFeedAdapter[Rounds * 4];
            for (int i = 0; i < targets.Length; i++) targets[i] = MakeExec().Feed;
            int t = 0;
            Action body = () =>
            {
                for (int k = 0; k < Rounds; k++)
                {
                    pub.Publish(e.Feed, t + 1);
                    if (adopt.TryAdopt(targets[t++]) != RolloverAdoptResult.Adopted) throw new InvalidOperationException("adopt");
                    if (!pub.IsAcked()) throw new InvalidOperationException("ack");
                }
            };
            body();
            Assert.Equal(0, MinAllocated(body));
        }

        // ════════════ §2 헬스 에이전트 ════════════

        [Fact]
        public void Health_WorkingSet_GcPause_Breach_BitRaised_HeartbeatFlagged()
        {
            long ws = 100L << 20, pause = 0;
            string hb = MapName();
            using var writer = new HostHeartbeatWriter(hb);
            using var gate = new HostHeartbeatEgressGate(hb);
            var agent = new HealthMonitoringAgent(maxWorkingSetBytes: 1L << 30, maxGcPausePerScan: TimeSpan.FromMilliseconds(200),
                                                  heartbeat: writer, workingSet: () => ws, gcPauseTicks: () => pause, useThreadPoolProbe: false);
            var mask = new ArmingInterlockBitmask(ArmingInterlockSource.HealthAgent);
            agent.BindInterlock(mask, ArmingInterlockSource.HealthAgent);
            Assert.True(mask.Seal(out _));
            Assert.Equal(HealthBreach.None, agent.Scan());
            Assert.True(gate.AllowOrder());
            pause += TimeSpan.FromMilliseconds(150).Ticks;
            Assert.Equal(HealthBreach.None, agent.Scan());                       // 150ms < 200ms
            pause += TimeSpan.FromMilliseconds(250).Ticks;
            Assert.Equal(HealthBreach.GcPause, agent.Scan());
            Assert.True(mask.IsRaised(ArmingInterlockSource.HealthAgent));       // ★0ms Raise
            Assert.False(gate.AllowOrder());                                     // ★하트비트 트립 플래그 → 프록시 송신 거부
            Assert.Equal(1, gate.RejectedTripped);
            ws = (1L << 30) + 1;
            Assert.Equal(HealthBreach.WorkingSet, agent.Scan());
            Assert.Equal(HealthBreach.GcPause | HealthBreach.WorkingSet, agent.Breach);
            Assert.False(agent.TryRearm(true, false));
            ws = 1;
            Assert.True(agent.TryRearm(true, true));
            Assert.False(mask.Blocked);
        }

        [Fact]
        public void Health_ThreadPoolStall_PendingProbeAge_And_CompletedLatency()
        {
            long now = 0;
            long f = Stopwatch.Frequency;
            var queued = new List<IThreadPoolWorkItem>();
            var agent = new HealthMonitoringAgent(maxStallMicros: 100_000, workingSet: () => 1, gcPauseTicks: () => 0, nowTimestamp: () => now,
                                                  enqueueProbe: queued.Add);
            Assert.Equal(HealthBreach.None, agent.Scan());                       // 탐침 투입(미실행)
            Assert.Single(queued);
            now += f * 50 / 1000;
            Assert.Equal(HealthBreach.None, agent.Scan());                       // 대기 50ms
            Assert.Single(queued);                                               // 미실행 탐침은 재투입하지 않음
            now += f * 80 / 1000;
            Assert.Equal(HealthBreach.ThreadPoolStall, agent.Scan());            // ★대기 130ms(미실행) > 100ms — 완료를 기다리지 않고 감지
            Assert.Equal(130_000, agent.LastStallMicros);

            var agent2 = new HealthMonitoringAgent(maxStallMicros: 100_000, workingSet: () => 1, gcPauseTicks: () => 0, nowTimestamp: Stopwatch.GetTimestamp,
                                                   enqueueProbe: queued.Add);
            queued.Clear();
            agent2.Scan();
            Thread.Sleep(150);
            queued[0].Execute();                                                 // 150ms 늦게 실행 → 완료 지연 기록
            Assert.Equal(HealthBreach.ThreadPoolStall, agent2.Scan());
        }

        [Fact]
        public void Health_Warmup_SuppressesBootBreaches_ThenEnforces()
        {
            long now = 0, ws = 2L << 30;
            var agent = new HealthMonitoringAgent(maxWorkingSetBytes: 1L << 30, workingSet: () => ws, gcPauseTicks: () => 0, nowTimestamp: () => now,
                                                  useThreadPoolProbe: false, warmup: TimeSpan.FromSeconds(30));
            Assert.Equal(HealthBreach.None, agent.Scan());
            now += Stopwatch.Frequency * 29;
            Assert.Equal(HealthBreach.None, agent.Scan());                       // 유예 중 — 관측만
            Assert.Equal(2L << 30, agent.LastWorkingSetBytes);
            now += Stopwatch.Frequency * 2;
            Assert.Equal(HealthBreach.WorkingSet, agent.Scan());
        }

        [Fact]
        public void Health_RealThread_RealProbe_HeartbeatFresh_ZeroAllocScan()
        {
            string hb = MapName();
            using var writer = new HostHeartbeatWriter(hb);
            using var gate = new HostHeartbeatEgressGate(hb);
            using var agent = new HealthMonitoringAgent(maxWorkingSetBytes: 64L << 30, maxStallMicros: 5_000_000, heartbeat: writer);
            agent.Start(TimeSpan.FromMilliseconds(10));
            var sw = Stopwatch.StartNew();
            while (agent.Scans < 10 && sw.ElapsedMilliseconds < 5_000) Thread.Sleep(5);
            Assert.True(agent.Scans >= 10);
            Assert.True(gate.AllowOrder(), "실 가동 하트비트가 신선해야 함");
            Assert.True(agent.LastWorkingSetBytes > 0);
            Assert.False(agent.Tripped);
            var solo = new HealthMonitoringAgent(maxWorkingSetBytes: 64L << 30, maxStallMicros: 5_000_000, heartbeat: writer);
            Action body = () => { for (int k = 0; k < 200; k++) solo.Scan(); };
            body();
            Thread.Sleep(20);
            Assert.Equal(0, MinAllocated(body));                                 // ★실 WorkingSet · 실 스레드풀 탐침 · GC 정지 · CPU · 하트비트 δ=0
        }

        // ════════════ §3 하트비트 · 프록시 송신 게이트 ════════════

        [Fact]
        public void EgressGate_Fresh_Stale_Missing_Torn()
        {
            string hb = MapName();
            long now = 1_000_000_000;
            long f = Stopwatch.Frequency;
            using var missing = new HostHeartbeatEgressGate(hb, 200_000, () => now);
            Assert.False(missing.AllowOrder());                                  // ★호스트 하트비트 없음 = 송신 거부(fail-closed)
            Assert.Equal(1, missing.RejectedMissing);
            using var writer = new HostHeartbeatWriter(hb);
            writer.Beat(now, f, 123, 50, false);
            Assert.True(missing.AllowOrder());                                   // 호스트 기동 후 지연 열기
            Assert.True(missing.TryRead(out var snap));
            Assert.Equal((Environment.ProcessId, 123L, 50), (snap.Pid, snap.WorkingSetBytes, snap.CpuPermille));
            now += f * 199 / 1000;
            Assert.True(missing.AllowOrder());
            now += f * 2 / 1000;                                                 // 201ms
            Assert.False(missing.AllowOrder());                                  // ★동결 호스트 = 송신 거부
            Assert.Equal(1, missing.RejectedStale);
            writer.Beat(now, f, 1, 0, false);
            Assert.True(missing.AllowOrder());
            using (var raw = MemoryMappedFile.OpenExisting(hb))
            using (var v = raw.CreateViewAccessor())
                v.Write(8, v.ReadInt64(8) + 1);                                  // seq 홀수 = 기록 중(찢김 위험)
            Assert.False(missing.AllowOrder());
            Assert.Equal(1, missing.RejectedTorn);
            Action body = () => { for (int k = 0; k < 500; k++) { writer.Beat(now, f, k, 1, false); missing.AllowOrder(); } };
            body();
            Assert.Equal(0, MinAllocated(body));
        }

        // ════════════ §4 결선 · 회귀 ════════════

        private static string RepoRoot()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d is not null && !File.Exists(Path.Combine(d.FullName, "StockManager.sln"))) d = d.Parent;
            Assert.NotNull(d);
            return d!.FullName;
        }

        [Fact]
        public void BrokerOrderIdFloor_NeverDecreases()
        {
            var att = new OrderAttestationService();
            var (broker, bridge) = ExecutionBridgeBroker.Create(att, "RO_" + Guid.NewGuid().ToString("N"));
            using (broker) using (bridge)
            {
                broker.SeedOrderIdFloor(117_057);
                Assert.Equal(117_057, broker.LastIssuedOrderId);
                broker.SeedOrderIdFloor(5);
                Assert.Equal(117_057, broker.LastIssuedOrderId);                  // 하한은 내려가지 않음
            }
        }

        [Fact]
        public void Host_And_Proxy_Wiring_FiveBuild()
        {
            const BindingFlags AllF = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
            var host = typeof(StockManager.Blazor.BlazorHomeForm);
            var targets = new MethodBase[]
            {
                typeof(ProcessRolloverGuard).GetMethod(nameof(ProcessRolloverGuard.TryAdopt))!,
                typeof(ProcessRolloverGuard).GetMethod(nameof(ProcessRolloverGuard.BindInterlock))!,
                typeof(ProcessRolloverGuard).GetMethod(nameof(ProcessRolloverGuard.Publish))!,
                typeof(ProcessRolloverGuard).GetMethod(nameof(ProcessRolloverGuard.WaitForAck))!,
                typeof(ExecutionBridgeBroker).GetMethod(nameof(ExecutionBridgeBroker.SeedOrderIdFloor))!,
                typeof(HealthMonitoringAgent).GetMethod(nameof(HealthMonitoringAgent.BindInterlock))!,
                typeof(HealthMonitoringAgent).GetMethod(nameof(HealthMonitoringAgent.Start))!,
                typeof(HostHeartbeatWriter).GetConstructors()[0],
            };
            var found = new bool[targets.Length];
            var types = new List<Type> { host };
            types.AddRange(host.GetNestedTypes(BindingFlags.NonPublic | BindingFlags.Public));
            foreach (var ty in types)
            {
                var all = new List<MethodBase>(ty.GetMethods(AllF));
                all.AddRange(ty.GetConstructors(AllF));
                foreach (var m in all)
                {
                    var il = m.GetMethodBody()?.GetILAsByteArray();
                    if (il is null) continue;
                    for (int i = 0; i + 4 < il.Length; i++)
                        if (il[i] is 0x28 or 0x6F or 0x73)
                            try
                            {
                                var mb = m.Module.ResolveMethod(BitConverter.ToInt32(il, i + 1));
                                for (int k = 0; k < targets.Length; k++) if (mb == targets[k]) found[k] = true;
                            }
                            catch (ArgumentException) { }
                }
            }
            for (int k = 0; k < targets.Length; k++) Assert.True(found[k], $"결선 누락: {targets[k].DeclaringType!.Name}.{targets[k].Name}");
            string root = RepoRoot();
            string form = File.ReadAllText(Path.Combine(root, "StockManager", "Blazor", "BlazorHomeForm.cs"));
            Assert.Contains("if (!allow) { e.Cancel = true; return; }", form);   // 승계 미확인 종료 게이트
            Assert.Contains("MS2E.Core.ArmingInterlockSource.Rollover,", form);
            Assert.Contains("MS2E.Core.ArmingInterlockSource.HealthAgent);", form);
            string proxyProgram = File.ReadAllText(Path.Combine(root, "StockManager.KiwoomProxy", "Program.cs"));
            Assert.Contains("new KiwoomProxyHost(api, egressGate: egressGate)", proxyProgram);
            string proxyHost = File.ReadAllText(Path.Combine(root, "StockManager.KiwoomProxy", "KiwoomProxyHost.cs"));
            Assert.Contains("!gate.AllowOrder()", proxyHost);
            Assert.Contains(@"src\02_Infrastructure\Engine\HostHealthHeartbeat.cs", File.ReadAllText(Path.Combine(root, "StockManager.KiwoomProxy", "StockManager.KiwoomProxy.csproj")));
            Assert.Equal((20, 21), ((int)ArmingInterlockSource.Rollover, (int)ArmingInterlockSource.HealthAgent));
            Assert.Equal(80, Marshal.SizeOf<HandoffSlot>());
            string sln = File.ReadAllText(Path.Combine(root, "StockManager.sln"));
            foreach (var p in new[] { @"StockManager.PrimesBench\StockManager.PrimesBench.csproj", @"StockManager.KiwoomProxy\StockManager.KiwoomProxy.csproj", @"tools\KiwoomComShim\KiwoomComShim.csproj" })
                Assert.Contains(p, sln);
        }
    }
}
