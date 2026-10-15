// ============================================================================
// PreFlightSentinelTests — v12.18.10-DEV Live Session Pre-Flight Sentinel(브로커 생존성 사전 진단 + 인터락 선제 래치)
//
//  §1 무응답 주입 → 시한(주입 100ms) 판정 continuation 안에서 비트 25 · 14 즉시 래치(다른 레인 대기 0 · 반환 전 차단)
//  §2 KIS 토큰 무효 → 비트 14 만 · 예외/호스트 취소 = Fault 래치(fail-closed) · 결선 레인의 '미판정' 응답 = 무효
//  §3 정상/미결선 → Passed · 비트 0 · 임대 갱신 술어(Pending/Failed = 갱신 0)
//  §4 sticky — Failed 후 재진단 거부(프로브 미호출) · 물리 이중키 없이는 해제 불가
//  §5 진단 통과 후 핑퐁 계측 · 판독 δ=0(Release · MinAllocated 3창)
//  §6 비파괴 — 진단 타입에 송출/취소 경로 0 · 호스트 KIS 프로브 = 잔고 조회 GET 만 · /aidash 투영
// ============================================================================
using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MS2E.Core;
using MS2E.UI.Portable.Helpers;
using MS2E.UI.Portable.Ports.Trading;
using StockManager.Adapters;
using StockManager.Blazor;
using Xunit;

namespace StockManager.Tests.Services
{
    [Collection("ConcurrencyStress")]
    public class PreFlightSentinelTests
    {
        private static readonly TimeSpan ShortTimeout = TimeSpan.FromMilliseconds(100);

        private sealed class Rig
        {
            public readonly ArmingInterlockBitmask Mask = new();
            public readonly AccountDivergenceInterlock Account = new();
            public readonly KiwoomBridgeLinkInterlock Link = new(() => true, required: false);
            public Rig()
            {
                Account.BindInterlock(Mask, ArmingInterlockSource.AccountDivergence);
                Link.BindInterlock(Mask, ArmingInterlockSource.KiwoomBridgeLink);
            }
            public bool Raised(ArmingInterlockSource s) => Mask.IsRaised(s);
        }

        /// <summary>비트 상승 순간의 사전 진단 상태를 기록하는 관측자(비트 반영 직후 동기 호출).</summary>
        private sealed class RaiseProbe : IInterlockTransitionSink
        {
            private readonly long _bit;
            public volatile BrokerLivenessPreFlight? Target;
            public long RaisedAtTicks;
            public int KiwoomVerdictAtRaise = -1, KisVerdictAtRaise = -1;
            public RaiseProbe(ArmingInterlockSource s) => _bit = 1L << (int)s;
            public void OnTransition(long raised, long cleared, long maskAfter)
            {
                if ((raised & _bit) == 0 || Interlocked.Read(ref RaisedAtTicks) != 0) return;
                var t = Target;
                if (t is not null)
                {
                    KiwoomVerdictAtRaise = (int)t.Verdict(BrokerLivenessPreFlight.LaneKiwoom);
                    KisVerdictAtRaise = (int)t.Verdict(BrokerLivenessPreFlight.LaneKis);
                }
                Interlocked.Exchange(ref RaisedAtTicks, Stopwatch.GetTimestamp());
            }
        }

        private static Func<CancellationToken, Task<BrokerProbeVerdict>> Never()
            => _ => new TaskCompletionSource<BrokerProbeVerdict>(TaskCreationOptions.RunContinuationsAsynchronously).Task;   // 취소 무시 · 영구 무응답

        private static Func<CancellationToken, Task<BrokerProbeVerdict>> Returns(BrokerProbeVerdict v, int delayMs = 0)
            => async ct => { if (delayMs > 0) await Task.Delay(delayMs, CancellationToken.None); return v; };

        private static long MinAllocated(Action body, int windows = 3)
        {
            long min = long.MaxValue;
            for (int w = 0; w < windows && min != 0; w++)
            {
                long before = GC.GetAllocatedBytesForCurrentThread();
                body();
                min = Math.Min(min, GC.GetAllocatedBytesForCurrentThread() - before);
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

        // ═══ §1 무응답 주입 → 선제 래치 ═══

        [Fact]
        public async Task Unresponsive_Kiwoom_Latches_Bits_25_And_14_In_Timeout_Continuation_Without_Waiting_For_Kis()
        {
            var rig = new Rig();
            var sink = new RaiseProbe(ArmingInterlockSource.KiwoomBridgeLink);
            rig.Mask.AttachTransitionSink(sink);
            Assert.False(rig.Raised(ArmingInterlockSource.KiwoomBridgeLink));
            Assert.False(rig.Raised(ArmingInterlockSource.AccountDivergence));

            var pf = new BrokerLivenessPreFlight(Never(), Returns(BrokerProbeVerdict.Healthy, 20), rig.Account, rig.Link, ShortTimeout);
            sink.Target = pf;
            Assert.Equal(PreFlightLivenessState.Pending, pf.State);

            long start = Stopwatch.GetTimestamp();
            var report = await pf.ProbeAsync();

            // 래치 시점: 시한(100ms) 판정 continuation — 판정 기록 직후 같은 흐름에서 비트 반영
            Assert.NotEqual(0, sink.RaisedAtTicks);
            double raisedMs = (sink.RaisedAtTicks - start) * 1000.0 / Stopwatch.Frequency;
            Assert.InRange(raisedMs, ShortTimeout.TotalMilliseconds - 5, 2_000);
            Assert.Equal((int)BrokerProbeVerdict.Timeout, sink.KiwoomVerdictAtRaise);

            Assert.True(rig.Raised(ArmingInterlockSource.KiwoomBridgeLink));
            Assert.True(rig.Raised(ArmingInterlockSource.AccountDivergence));
            Assert.True(rig.Mask.Blocked);
            Assert.True(rig.Link.PreFlightLatched);
            Assert.Equal(BrokerLivenessPreFlight.PreFlightSymbolCode, rig.Account.LastSymbol);
            Assert.Equal(-(long)BrokerProbeVerdict.Timeout, rig.Account.LastBrokerQty);

            Assert.Equal(PreFlightLivenessState.Failed, report.State);
            Assert.False(report.Passed);
            Assert.Equal(BrokerProbeVerdict.Timeout, report.Kiwoom);
            Assert.Equal(BrokerProbeVerdict.Healthy, report.Kis);
            Assert.False(pf.Cleared);
            Assert.Equal(1, pf.Latches);
        }

        [Fact]
        public async Task Failing_Lane_Latches_Before_Other_Lane_Resolves()
        {
            var rig = new Rig();
            var sink = new RaiseProbe(ArmingInterlockSource.KiwoomBridgeLink);
            rig.Mask.AttachTransitionSink(sink);
            const int kisDelayMs = 400;   // KIS 는 느리지만 시한(2초) 안의 정상 — 키움 래치는 이 레인을 기다리지 않아야 한다
            var pf = new BrokerLivenessPreFlight(Returns(BrokerProbeVerdict.Invalid), Returns(BrokerProbeVerdict.Healthy, kisDelayMs),
                                                 rig.Account, rig.Link, TimeSpan.FromSeconds(2));
            sink.Target = pf;
            long start = Stopwatch.GetTimestamp();
            var r = await pf.ProbeAsync();
            double raisedMs = (sink.RaisedAtTicks - start) * 1000.0 / Stopwatch.Frequency;
            Assert.InRange(raisedMs, 0, kisDelayMs - 1);
            Assert.Equal((int)BrokerProbeVerdict.Invalid, sink.KiwoomVerdictAtRaise);
            Assert.Equal((int)BrokerProbeVerdict.Pending, sink.KisVerdictAtRaise);           // KIS 는 아직 판정 중이었다
            Assert.Equal(BrokerProbeVerdict.Healthy, r.Kis);
            Assert.Equal(PreFlightLivenessState.Failed, r.State);                            // 다른 레인 통과가 Failed 를 덮지 않음
        }

        [Fact]
        public async Task Default_Timeout_Is_Three_Seconds_And_Link_Reestablishment_Does_Not_Clear_Latch()
        {
            Assert.Equal(TimeSpan.FromSeconds(3), BrokerLivenessPreFlight.DefaultTimeout);
            Assert.Equal(TimeSpan.FromSeconds(3), new BrokerLivenessPreFlight(null, null, null, null).Timeout);

            bool linked = false;
            var mask = new ArmingInterlockBitmask();
            var link = new KiwoomBridgeLinkInterlock(() => linked, required: true);
            link.BindInterlock(mask, ArmingInterlockSource.KiwoomBridgeLink);
            var pf = new BrokerLivenessPreFlight(Never(), null, null, link, ShortTimeout);
            await pf.ProbeAsync();
            Assert.True(mask.IsRaised(ArmingInterlockSource.KiwoomBridgeLink));

            linked = true; link.Evaluate();                                                // 뒤늦은 링크 수립
            Assert.True(mask.IsRaised(ArmingInterlockSource.KiwoomBridgeLink));             // ★사전 진단 래치는 sticky
            Assert.False(link.TryClearPreFlightLatch(true, false));
            Assert.True(mask.IsRaised(ArmingInterlockSource.KiwoomBridgeLink));
            Assert.True(link.TryClearPreFlightLatch(true, true));                          // 물리 이중키
            Assert.False(mask.IsRaised(ArmingInterlockSource.KiwoomBridgeLink));
        }

        // ═══ §2 토큰 무효 · 예외 · 취소 ═══

        [Fact]
        public async Task Kis_Token_Invalid_Latches_Bit_14_Only()
        {
            var rig = new Rig();
            var pf = new BrokerLivenessPreFlight(Returns(BrokerProbeVerdict.Healthy), Returns(BrokerProbeVerdict.Invalid), rig.Account, rig.Link, ShortTimeout);
            var r = await pf.ProbeAsync();
            Assert.Equal(PreFlightLivenessState.Failed, r.State);
            Assert.Equal(BrokerProbeVerdict.Invalid, r.Kis);
            Assert.True(rig.Raised(ArmingInterlockSource.AccountDivergence));
            Assert.False(rig.Raised(ArmingInterlockSource.KiwoomBridgeLink));
            Assert.False(rig.Link.PreFlightLatched);
        }

        [Fact]
        public async Task Probe_Exception_Undetermined_Answer_And_Host_Cancel_Are_Fail_Closed()
        {
            var rig = new Rig();
            var pf = new BrokerLivenessPreFlight(_ => throw new InvalidOperationException("boom"), null, rig.Account, rig.Link, ShortTimeout);
            Assert.Equal(BrokerProbeVerdict.Fault, (await pf.ProbeAsync()).Kiwoom);
            Assert.True(rig.Raised(ArmingInterlockSource.KiwoomBridgeLink));

            var rig2 = new Rig();
            var pf2 = new BrokerLivenessPreFlight(null, Returns(BrokerProbeVerdict.NotConfigured), rig2.Account, rig2.Link, ShortTimeout);
            Assert.Equal(BrokerProbeVerdict.Invalid, (await pf2.ProbeAsync()).Kis);           // 결선된 레인이 '미결선' 응답 = 무효
            Assert.True(rig2.Raised(ArmingInterlockSource.AccountDivergence));

            var rig3 = new Rig();
            using var cts = new CancellationTokenSource();
            var pf3 = new BrokerLivenessPreFlight(ct => Task.Delay(Timeout.Infinite, ct).ContinueWith(_ => BrokerProbeVerdict.Healthy, TaskScheduler.Default),
                                                  null, rig3.Account, rig3.Link, TimeSpan.FromSeconds(30));
            var t = pf3.ProbeAsync(cts.Token);
            cts.Cancel();
            var r3 = await t;
            Assert.NotEqual(BrokerProbeVerdict.Healthy, r3.Kiwoom);
            Assert.Equal(PreFlightLivenessState.Failed, r3.State);
            Assert.True(rig3.Raised(ArmingInterlockSource.KiwoomBridgeLink));
        }

        // ═══ §3 정상 · 미결선 · 임대 갱신 술어 ═══

        [Fact]
        public async Task Healthy_And_Unconfigured_Lanes_Pass_With_No_Bits()
        {
            var rig = new Rig();
            var pf = new BrokerLivenessPreFlight(Returns(BrokerProbeVerdict.Healthy, 10), null, rig.Account, rig.Link, ShortTimeout);
            var r = await pf.ProbeAsync();
            Assert.True(r.Passed);
            Assert.Equal(BrokerProbeVerdict.Healthy, r.Kiwoom);
            Assert.Equal(BrokerProbeVerdict.NotConfigured, r.Kis);
            Assert.True(pf.Cleared);
            Assert.Equal(0, rig.Mask.Mask & ((1L << (int)ArmingInterlockSource.AccountDivergence) | (1L << (int)ArmingInterlockSource.KiwoomBridgeLink)));
            Assert.Equal(0, pf.Latches);
            Assert.True(r.ElapsedMicros > 0);

            var none = await new BrokerLivenessPreFlight(null, null, null, null).ProbeAsync();
            Assert.True(none.Passed);
        }

        [Fact]
        public async Task Lease_Renewal_Requires_Liveness_Passed()
        {
            Assert.True(GenesisPreFlightUpdater.RenewAllowed(true, true, null));                 // 미결선 = 기존 동작
            Assert.False(GenesisPreFlightUpdater.RenewAllowed(false, true, null));
            Assert.False(GenesisPreFlightUpdater.RenewAllowed(true, false, null));

            var pending = new BrokerLivenessPreFlight(Returns(BrokerProbeVerdict.Healthy), null, null, null, ShortTimeout);
            Assert.False(GenesisPreFlightUpdater.RenewAllowed(true, true, pending));             // ★진단 전 = 갱신 0
            await pending.ProbeAsync();
            Assert.True(GenesisPreFlightUpdater.RenewAllowed(true, true, pending));

            var failed = new BrokerLivenessPreFlight(Never(), null, null, null, ShortTimeout);
            await failed.ProbeAsync();
            Assert.False(GenesisPreFlightUpdater.RenewAllowed(true, true, failed));

            var updater = new GenesisPreFlightUpdater(new MS2E.Infrastructure.GenesisPreFlightCertifier(), new MS2E.Infrastructure.AtomicArmingLease(), liveness: failed);
            Assert.Same(failed, updater.Liveness);
            Assert.Equal(PreFlightLivenessState.Failed, (await updater.ProbeBrokerLiveness()).State);
            var bare = new GenesisPreFlightUpdater(new MS2E.Infrastructure.GenesisPreFlightCertifier(), new MS2E.Infrastructure.AtomicArmingLease());
            Assert.True((await bare.ProbeBrokerLiveness()).Passed);
            updater.Dispose(); bare.Dispose();
        }

        // ═══ §4 sticky ═══

        [Fact]
        public async Task Failed_Is_Sticky_Until_Physical_Dual_Key_Then_Requires_Reprobe()
        {
            var rig = new Rig();
            int calls = 0;
            var verdict = BrokerProbeVerdict.Invalid;
            var pf = new BrokerLivenessPreFlight(_ => { Interlocked.Increment(ref calls); return Task.FromResult(verdict); }, null, rig.Account, rig.Link, ShortTimeout);
            await pf.ProbeAsync();
            Assert.Equal(1, calls);
            verdict = BrokerProbeVerdict.Healthy;
            Assert.Equal(PreFlightLivenessState.Failed, (await pf.ProbeAsync()).State);
            Assert.Equal(1, calls);                                                               // 재진단 거부(프로브 미호출)

            Assert.False(pf.TryRearm(true, false));
            Assert.Equal(PreFlightLivenessState.Failed, pf.State);
            Assert.True(pf.TryRearm(true, true));
            Assert.Equal(PreFlightLivenessState.Pending, pf.State);
            Assert.False(pf.Cleared);                                                             // 재무장 ≠ 통과 — 재진단 필요
            Assert.True((await pf.ProbeAsync()).Passed);
            Assert.Equal(2, calls);
            Assert.True(rig.Raised(ArmingInterlockSource.AccountDivergence));                     // 비트 14 는 소유 인터락 재무장 경로로만
            Assert.True(rig.Account.TryRearm(true, true));
            Assert.True(rig.Link.TryClearPreFlightLatch(true, true));
            Assert.False(rig.Raised(ArmingInterlockSource.AccountDivergence));
            Assert.False(rig.Raised(ArmingInterlockSource.KiwoomBridgeLink));
        }

        // ═══ §5 δ=0 ═══

        [Fact]
        public async Task PingPong_And_Readout_Path_Is_Zero_Alloc_After_Pass()
        {
            var pf = new BrokerLivenessPreFlight(Returns(BrokerProbeVerdict.Healthy), Returns(BrokerProbeVerdict.Healthy), null, null, ShortTimeout);
            Assert.True((await pf.ProbeAsync()).Passed);

            pf.RecordPingPong(BrokerLivenessPreFlight.LaneKiwoom, 1);   // 워밍업(JIT)
            _ = pf.Snapshot(); _ = pf.Verdict(0); _ = pf.PingMaxMicros(0);
            long sink = 0;
            long alloc = MinAllocated(() =>
            {
                for (int i = 0; i < 10_000; i++)
                {
                    pf.RecordPingPong(i & 1, 1_000 + (i % 97) * 10);
                    var s = pf.Snapshot();
                    sink += (long)s.Kiwoom + pf.PingLastMicros(i & 1) + pf.PingMaxMicros(i & 1) + (pf.Cleared ? 1 : 0);
                }
            });
            Assert.Equal(0, alloc);
            Assert.True(sink > 0);
            Assert.True(pf.PingCount(0) >= 5_001 && pf.PingCount(1) >= 5_000);
            Assert.Equal((1_000 + 96 * 10) / 10, pf.PingMaxMicros(0));                              // 100ns 틱 → µs · CAS 최대
            Assert.Equal((1_000 + 96 * 10) / 10, pf.PingMaxMicros(1));
            Assert.Equal(0, pf.PingOverruns(0));
            pf.RecordPingPong(1, TimeSpan.FromSeconds(4).Ticks);                                // 시한 초과 = 누계만
            Assert.Equal(1, pf.PingOverruns(1));
            Assert.True(pf.Cleared);                                                              // 핑퐁 경로는 래치하지 않는다(세션 인터락 소유)
        }

        // ═══ §6 비파괴 · 투영 ═══

        [Fact]
        public void Sentinel_Has_No_Order_Or_Cancel_Path_And_Host_Kis_Probe_Is_Read_Only()
        {
            string root = RepoRoot();
            string src = File.ReadAllText(Path.Combine(root, "StockManager", "Blazor", "BrokerLivenessPreFlight.cs"));
            string code = string.Join("\n", Array.FindAll(src.Split('\n'), l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));
            foreach (var banned in new[] { "IBroker", ".Place(", ".Cancel(", "CancelOpenOrder", "SendOrder", "TTTC0802U", "TTTC0801U", "TTTC0803U" })
                Assert.DoesNotContain(banned, code);

            string host = File.ReadAllText(Path.Combine(root, "StockManager", "Blazor", "BlazorHomeForm.cs"));
            int a = host.IndexOf("var livenessPreFlight = new BrokerLivenessPreFlight(", StringComparison.Ordinal);
            int b = host.IndexOf("services.AddSingleton(livenessPreFlight);", a, StringComparison.Ordinal);
            Assert.True(a > 0 && b > a);
            string wiring = host[a..b];
            Assert.Contains("kisLive.QueryBalanceJson()", wiring);
            Assert.Contains("bridge.IsProductionProxy", wiring);
            foreach (var banned in new[] { ".Place(", ".Cancel(", "CancelOpenOrder", "QueryOpenOrdersJson", "Send" })
                Assert.DoesNotContain(banned, wiring);
            Assert.Contains("liveness: livenessPreFlight", host);
            Assert.Contains("dashHub.AttachPreFlight(livenessPreFlight)", host);
        }

        [Fact]
        public void Dashboard_Projection_Codes_Match_Host_Enums_And_Labels_Are_Total()
        {
            Assert.Equal(DashPreFlight.LaneUnbound, (int)BrokerProbeVerdict.NotConfigured);
            Assert.Equal(DashPreFlight.LaneHealthy, (int)BrokerProbeVerdict.Healthy);
            Assert.Equal(DashPreFlight.LaneTimeout, (int)BrokerProbeVerdict.Timeout);
            Assert.Equal(DashPreFlight.LaneInvalid, (int)BrokerProbeVerdict.Invalid);
            Assert.Equal(DashPreFlight.LaneFault, (int)BrokerProbeVerdict.Fault);
            Assert.Equal(DashPreFlight.LanePending, (int)BrokerProbeVerdict.Pending);
            Assert.Equal(DashPreFlight.Unbound, DashPreFlight.Classify(false, 2));
            Assert.Equal(DashPreFlight.Probing, DashPreFlight.Classify(true, (int)PreFlightLivenessState.Pending));
            Assert.Equal(DashPreFlight.Passed, DashPreFlight.Classify(true, (int)PreFlightLivenessState.Passed));
            Assert.Equal(DashPreFlight.Failed, DashPreFlight.Classify(true, (int)PreFlightLivenessState.Failed));
            Assert.Equal(4, SignalLabelMapper.PreFlightStateCount);
            Assert.Equal(6, SignalLabelMapper.PreFlightLaneCount);
            Assert.Equal("bad", SignalLabelMapper.PreFlightStateClass(DashPreFlight.Failed));
            Assert.Equal("ok", SignalLabelMapper.PreFlightLaneClass(DashPreFlight.LaneHealthy));
            Assert.Equal("off", SignalLabelMapper.PreFlightLaneClass(99));
        }
    }
}
