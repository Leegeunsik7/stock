// ============================================================================
// PaperTradingDashboardUiTests — [대시보드 · 추천 A]
//
//  §1 TelemetryRing — 순서 · 덮어쓰기 유실 판정 · 다중 생산자 총량 보존.
//  §2 묶음 처리 — 50ms 창 동안 1만 틱 + 500 체결 = 펌프 1회 · 프레임 1장 · 내용 불변이면 새 버전 0.
//  §3 모의 원장 — 가중평단 · 실현손익 · 승/패 · 반대 포지션 개시 · 시가평가 · MDD · 자산곡선.
//  §4 δ=0 — 생산자 4종 · 펌프(매트릭스 · 한도 · 집계어 · 구독자 통지 포함) · Acquire/Release 할당 0.
//  §5 3중 버퍼 리스 — 리스 중 프레임 불변(찢김 0) · 3버퍼 모두 묶이면 조립 생략 · 남의 프레임 반납 무시.
//  §6 ★Zero UI Interference — UI 구독자가 예외/무기한 대기해도 생산자(매매 스레드 대리) 무대기 완주 · 펌프 무전파 ·
//     포트 = 읽기 전용(주문/무장 명령 멤버 0) · 허브 객체 그래프에 브로커/드레인 참조 0.
//  §7 인터락 LED · Disarm 포렌식 — 집계어 → 포렌식 기록기(해시 체인 먼저) → 2차 체인 허브 · 체인 예외 격리.
//  §8 [방향 A] 자식 슬롯 매트릭스 · 브로커 한도 · [방향 B] AI 판단 근거 스트림.
//  §9 E2E — 모의투자 하네스(실 게이팅 사슬 + 가상 브로커 · 실 브로커 0) 체결 → 대시보드 수렴 · 래칫 · 결선 · 오중 빌드.
// ============================================================================
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using MS2E.Core;
using MS2E.Infrastructure;
using MS2E.UI.Portable.Ports.Trading;
using StockManager.Adapters;
using StockManager.Blazor.Services;
using StockManager.Engines;
using StockManager.Simulation;
using StockManager.Telemetry;
using Xunit;
using static StockManager.Tests.ZeroAllocProbe;

namespace StockManager.Tests
{
    [Collection("ConcurrencyStress")]
    public sealed class PaperTradingDashboardUiTests
    {
        private sealed class Clock { public long Now = 638_000_000_000_000_000; }

        private static FillPayload Fill(long sym, int side, long qty, double px) => new() { OrderId = 1, SymbolCode = sym, Side = side, FillQty = qty, FillPrice = px };

        // ════════════ §1 링 ════════════

        [Fact]
        public void Ring_Order_OverwriteLoss_MultiProducerTotal()
        {
            var r = new TelemetryRing<long>(8);
            for (long i = 0; i < 5; i++) r.Publish(i * 10);
            for (long p = 0; p < 5; p++) { Assert.Equal(1, r.TryRead(p, out var v)); Assert.Equal(p * 10, v); }
            Assert.Equal(0, r.TryRead(5, out _));                                             // 미게시
            for (long i = 5; i < 20; i++) r.Publish(i * 10);
            Assert.Equal(-1, r.TryRead(3, out _));                                            // 덮어써짐 = 유실
            Assert.Equal(1, r.TryRead(19, out var last));
            Assert.Equal(190, last);
            Assert.Throws<ArgumentOutOfRangeException>(() => new TelemetryRing<long>(6));

            var big = new TelemetryRing<long>(1 << 16);
            Parallel.For(0, 4, t => { for (int i = 0; i < 10_000; i++) big.Publish(t * 1_000_000L + i); });
            Assert.Equal(40_000, big.Head);
            long sum = 0;
            for (long p = 0; p < 40_000; p++) { Assert.Equal(1, big.TryRead(p, out var v)); sum += v % 1_000_000; }
            Assert.Equal(4L * (9_999L * 10_000L / 2), sum);                                   // 다중 생산자 총량 보존
        }

        // ════════════ §2 묶음 처리 ════════════

        [Fact]
        public void Governor_Coalesces_10kTicks_500Fills_Into_OneFrame_Unchanged_NoNewVersion()
        {
            var hub = new DashboardSnapshotHub();
            var versions = new List<long>();
            hub.FrameReady += versions.Add;
            Assert.Null(hub.Acquire());                                                       // 게시 전
            for (int i = 0; i < 10_000; i++) hub.OnTick(5930 + i % 8, 70_000 + i % 50);       // 50ms 창의 부하(초당 20만 틱 상당)
            for (int i = 0; i < 500; i++) hub.OnFill(Fill(5930, (i & 1) == 0 ? 1 : -1, 1, 70_000), 2);
            Assert.True(hub.Pump());
            Assert.Equal(new[] { 1L }, versions.ToArray());                                   // ★1만+ 이벤트 → 프레임 1장
            var f = hub.Acquire()!;
            Assert.Equal((10_000L, 500L, 0L), (f.TicksSeen, f.FillsSeen, f.FillsLost));
            Assert.Equal(PaperDashboardFrame.MaxFills, f.FillCount);
            hub.Release(f);
            Assert.False(hub.Pump());                                                         // 내용 불변 = 게시 0
            Assert.Single(versions);
            hub.OnTick(5930, 71_000);
            Assert.True(hub.Pump());
            Assert.Equal(2, hub.Version);
        }

        [Fact]
        public void FillRingOverflow_CountsDisplayLoss_NotCorruption()
        {
            var hub = new DashboardSnapshotHub();
            for (int i = 0; i < 3_000; i++) hub.OnFill(Fill(5930, 1, 1, 70_000), 2);         // 링 1024 초과
            hub.Pump();
            var f = hub.Acquire()!;
            Assert.Equal(3_000, f.FillsSeen + f.FillsLost);
            Assert.Equal(1_024, f.FillsSeen);
            hub.Release(f);
        }

        // ════════════ §3 모의 원장 ════════════

        [Fact]
        public void PaperLedger_WeightedAvg_Realized_WinLoss_Flip_MarkToMarket_Mdd_Curve()
        {
            var clk = new Clock();
            var hub = new DashboardSnapshotHub(nowTicks: () => clk.Now, equitySampleEveryPumps: 1);
            hub.OnFill(Fill(5930, 1, 10, 100), 2);
            hub.OnFill(Fill(5930, 1, 10, 120), 2);                                            // 평단 110
            hub.OnTick(5930, 115);
            hub.Pump();
            var f = hub.Acquire()!;
            Assert.Equal((0L, 100L, 100L, 1), (f.RealizedPnlWon, f.UnrealizedPnlWon, f.EquityWon, f.OpenPositions));   // 20 × (115−110)
            hub.Release(f);
            hub.OnFill(Fill(5930, -1, 5, 130), 2);                                            // +100 승
            hub.OnFill(Fill(5930, -1, 20, 100), 2);                                           // 15 청산 −150 패 · 5 공매도 개시 @100
            hub.OnTick(5930, 90);
            hub.Pump();
            f = hub.Acquire()!;
            Assert.Equal((-50L, 1, 1), (f.RealizedPnlWon, f.Wins, f.Losses));
            Assert.Equal(50, f.UnrealizedPnlWon);                                             // −5 × (90 − 100)
            Assert.Equal(0.5, f.WinRate);
            Assert.Equal((100L, 100L), (f.PeakEquityWon, f.MaxDrawdownWon));                 // 고점 100 → 0
            Assert.Equal(2, f.EquityCount);
            Assert.Equal(new[] { 100L, 0L }, f.Equity.Take(2).Select(p => p.EquityWon).ToArray());   // 과거 → 최신
            hub.Release(f);
            hub.OnFill(new FillPayload { SymbolCode = 5930, Side = 1, FillQty = 0, FillPrice = 0 }, 2);   // 접수 통보 = 무시
            hub.OnFill(Fill(5930, 0, 5, 100), 2);                                             // 방향 없음 = 무시
            hub.OnFill(Fill(5930, 1, 5, double.NaN), 2);                                      // 가격 이상 = 무시
            hub.OnTick(5930, 91);
            hub.Pump();
            f = hub.Acquire()!;
            Assert.Equal(4, f.FillsSeen);                                                     // 무시 3건은 원장 미반영
            hub.Release(f);
        }

        // ════════════ §4 δ=0 ════════════

        private sealed class View : IBrokerAccountView
        {
            public bool Bound { get; set; } = true;
            public long Room = 1_000_000;
            public bool SharesGateReservation => false;
            public bool TryGetBuyHeadroomWon(out long won) { won = Room; return true; }
            public bool TryGetSellableShares(long symbolCode, out long shares) { shares = 0; return true; }
        }

        [Fact]
        public void Producers_Pump_Lease_ZeroAlloc()
        {
            var mask = new ArmingInterlockBitmask(ArmingInterlockSource.RouterFeed);
            var m = new ChildSlotTrackingMatrix(8);
            long parent = 9001;
            Assert.True(m.Open(parent, 10, new[] { new ChildOrderPlan { ChildId = ChildIdSpace.Compose(parent, 0), ParentId = parent, Shares = 10, Broker = 0 } }));
            var views = new IBrokerAccountView?[] { new View(), null };
            var hub = new DashboardSnapshotHub(mask, m, views, equitySampleEveryPumps: 1);
            long seen = 0;
            hub.FrameReady += v => seen = v;
            for (int i = 0; i < 64; i++) { hub.OnTick(5930, 70_000 + i); hub.OnFill(Fill(5930, 1, 1, 70_000), 0); hub.PublishDecision(1, 5930, 100, 50, 1, 7); hub.OnTransition(1, 0, 1); hub.Pump(); }
            Assert.Equal(0, MinAllocated(() =>
            {
                for (int i = 0; i < 200; i++)
                {
                    hub.OnTick(5930, 70_000 + i);
                    hub.OnFill(Fill(5930, (i & 1) == 0 ? 1 : -1, 1, 70_000 + i), 0);
                    hub.PublishDecision(DashDecisionKind.Entry, 5930, 2_500, 180, 70_000, 3);
                    hub.OnTransition(1L << 4, 0, 1L << 4);
                    hub.Pump();
                    var f = hub.Acquire();
                    if (f is not null) hub.Release(f);
                }
            }));
            Assert.True(seen > 64);
            Assert.Equal(0, hub.PumpFaults);
        }

        // ════════════ §5 3중 버퍼 리스 ════════════

        [Fact]
        public void TripleBuffer_LeasedFrameNeverOverwritten_AllLeased_SkipsBuild()
        {
            var hub = new DashboardSnapshotHub();
            hub.OnTick(5930, 1); hub.Pump();
            var a = hub.Acquire()!;
            long aVer = a.Version, aTicks = a.TicksSeen;
            for (int i = 0; i < 50; i++) { hub.OnTick(5930, 2 + i); hub.Pump(); }
            Assert.Equal((aVer, aTicks), (a.Version, a.TicksSeen));                           // ★리스 중 불변(찢김 0)
            var b = hub.Acquire()!;
            Assert.NotSame(a, b);
            hub.OnTick(5930, 999); hub.Pump();
            var c = hub.Acquire()!;
            Assert.True(new[] { a.BufferSlot, b.BufferSlot, c.BufferSlot }.Distinct().Count() == 3);
            long skipped = hub.BuildSkipped;
            hub.OnFill(Fill(5930, 1, 7, 70_000), 2);                                         // 서명 밖 변경(링 소비분)
            Assert.False(hub.Pump());                                                         // 3버퍼 모두 리스 = 조립 생략
            Assert.Equal(skipped + 1, hub.BuildSkipped);
            hub.Release(new PaperDashboardFrame { BufferSlot = 0 });                         // 남의 프레임 = 무시
            Assert.Equal(1, hub.LeaseCount(a.BufferSlot));
            hub.Release(a); hub.Release(b); hub.Release(c);
            hub.Release(c);                                                                   // 중복 반납 = 음수 방지
            Assert.Equal(0, hub.LeaseCount(c.BufferSlot));
            Assert.True(hub.Pump());                                                          // ★생략 회차에 소비한 체결도 다음 프레임에 반영
            var d = hub.Acquire()!;
            Assert.Equal((1L, 7L), (d.FillsSeen, d.Fills[0].Qty));
            hub.Release(d);
        }

        // ════════════ §6 Zero UI Interference ════════════

        [Fact]
        public async Task ThrowingAndBlockingUiSubscribers_NeverBlockProducers_NorPropagate()
        {
            var hub = new DashboardSnapshotHub();
            hub.FrameReady += _ => throw new InvalidOperationException("UI 렌더 예외");
            hub.OnTick(5930, 1);
            Assert.True(hub.Pump());                                                          // 예외 무전파
            Assert.Equal(1, hub.SubscriberFaults);

            using var uiBlocked = new ManualResetEventSlim(false);
            using var producersDone = new ManualResetEventSlim(false);
            var blocking = new DashboardSnapshotHub();
            blocking.FrameReady += _ => { uiBlocked.Set(); producersDone.Wait(TimeSpan.FromSeconds(10)); };   // UI 동결(무기한 대기 대리)
            blocking.OnTick(1, 1);
            var pump = Task.Run(() => blocking.Pump());
            Assert.True(uiBlocked.Wait(5000));
            var sw = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 0; i < 100_000; i++)                                                 // 매매 스레드 대리 — UI 가 멈춘 동안
            {
                blocking.OnFill(Fill(5930, 1, 1, 70_000), 0);
                blocking.OnTick(5930, 70_000 + (i & 63));
                blocking.OnTransition(1, 0, 1);
            }
            sw.Stop();
            Assert.False(pump.IsCompleted);                                                   // UI 는 여전히 동결
            producersDone.Set();
            await pump.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(sw.ElapsedMilliseconds < 5_000, "생산자가 UI 동결에 묶임: " + sw.ElapsedMilliseconds + "ms");
            Assert.True(blocking.Pump());
            var bf = blocking.Acquire()!;
            Assert.Equal(100_001, bf.TicksSeen);                                             // 동결 중 유입분 보존(표시 링 유실은 계수)
            Assert.Equal(100_000, bf.FillsSeen + bf.FillsLost);
            blocking.Release(bf);
        }

        [Fact]
        public void Port_IsReadOnly_Hub_HoldsNoBrokerOrDrainReference()
        {
            var members = typeof(IPaperDashboardPort).GetMembers().Select(m => m.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();
            Assert.Equal(new[] { "Acquire", "FrameReady", "Release", "Running", "Version", "add_FrameReady", "get_Running", "get_Version", "remove_FrameReady" }, members);
            var forbidden = new[] { typeof(IBroker), typeof(OrderDrainWorker), typeof(ExecutionBridgeBroker), typeof(KiwoomBridgeClient), typeof(KisLiveBroker), typeof(GatedOrderDispatcher) };
            foreach (var fld in typeof(DashboardSnapshotHub).GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public))
                foreach (var t in forbidden)
                    Assert.False(t.IsAssignableFrom(fld.FieldType), $"대시보드 허브가 {t.Name} 참조: {fld.Name}");
        }

        // ════════════ §7 인터락 LED · 포렌식 ════════════

        private sealed class Src : IArmingBlockSource { public bool Blocked; public bool ArmingBlocked => Blocked; }

        [Fact]
        public void InterlockTransitions_ReachDashboard_AfterForensicChain_ChainFaultIsolated()
        {
            var mask = new ArmingInterlockBitmask(ArmingInterlockSource.KisProtocolDivergence);
            var stream = new ZeroAllocForensicAuditStream(256);
            var rec = new InterlockForensicRecorder(stream);
            mask.AttachTransitionSink(rec);
            var hub = new DashboardSnapshotHub(mask);
            rec.Chain(hub);
            Assert.Throws<InvalidOperationException>(() => rec.Chain(hub));                   // 교체 금지
            var src = new Src();
            var link = mask.Bind(ArmingInterlockSource.KisProtocolDivergence, src);
            Assert.True(mask.Seal(out _));
            src.Blocked = true; link.Resync();                                                // Disarm 격발
            hub.Pump();
            var f = hub.Acquire()!;
            Assert.True(f.IsRaised(26));
            Assert.Equal("KisProtocolDivergence", f.BitName(26));
            Assert.True(f.Sealed && f.IsRequired(26) && f.IsBound(26));
            Assert.Contains(f.Events.Take(f.EventCount), e => (e.Raised & (1L << 26)) != 0);
            hub.Release(f);
            long head = stream.WriteHead;

            var bad = new InterlockForensicRecorder(new ZeroAllocForensicAuditStream(64));
            bad.Chain(new ThrowingSink());
            bad.OnTransition(1, 0, 1);
            Assert.Equal(1, bad.ChainFaults);                                                 // 관측자 예외 = 계수만
            Assert.True(head > 0);                                                            // 포렌식 각인이 먼저
        }

        private sealed class ThrowingSink : IInterlockTransitionSink { public void OnTransition(long r, long c, long m) => throw new InvalidOperationException(); }

        // ════════════ §8 방향 A · B ════════════

        [Fact]
        public void DirectionA_ChildSlotMatrix_And_BrokerLimits_Visualized()
        {
            var m = new ChildSlotTrackingMatrix(8);
            long parent = 9100;
            var plans = new[]
            {
                new ChildOrderPlan { ChildId = ChildIdSpace.Compose(parent, 0), ParentId = parent, Shares = 60, Broker = 0 },
                new ChildOrderPlan { ChildId = ChildIdSpace.Compose(parent, 1), ParentId = parent, Shares = 40, Broker = 1 },
            };
            Assert.True(m.Open(parent, 100, plans));
            m.MarkSent(plans[0].ChildId, 777);
            m.MarkSent(plans[1].ChildId, KisOrderIdSpace.Encode(55));
            m.OnFill(new FillPayload { OrderId = 777, SymbolCode = 5930, FillQty = 60, FillPrice = 70_100, Side = 1 });
            m.OnFill(new FillPayload { OrderId = KisOrderIdSpace.Encode(55), SymbolCode = 5930, FillQty = 10, FillPrice = 70_000, Side = 1 });
            var kis = new View { Bound = false };
            var hub = new DashboardSnapshotHub(matrix: m, views: new IBrokerAccountView?[] { new View { Room = 5_000_000 }, kis });
            hub.Pump();
            var f = hub.Acquire()!;
            Assert.Equal(2, f.ChildCount);
            var kw = f.Children.Take(2).Single(c => c.Broker == 0);
            var ks = f.Children.Take(2).Single(c => c.Broker == 1);
            Assert.Equal((parent, 60L, 60L, 70_100L, (int)ChildSlotState.Filled), (kw.ParentId, kw.OrderQty, kw.FilledQty, kw.AvgPriceWon, kw.State));
            Assert.Equal((10L, (int)ChildSlotState.Accepted, 1), (ks.FilledQty, ks.State, ks.Index));
            Assert.Equal(2, f.LimitCount);
            Assert.Equal((true, 5_000_000L), (f.Limits[0].Bound, f.Limits[0].HeadroomWon));
            Assert.False(f.Limits[1].Bound);                                                  // 미결속 = 정직 표시
            Assert.Equal("체결", DashChildState.Label(kw.State));
            hub.Release(f);
            m.OnFill(new FillPayload { OrderId = KisOrderIdSpace.Encode(55), SymbolCode = 5930, FillQty = 30, FillPrice = 70_000, Side = 1 });
            Assert.True(hub.Pump());                                                          // 슬롯 변경 = 새 프레임
        }

        [Fact]
        public void DirectionB_DecisionRationaleStream_NewestFirst_LossCounted()
        {
            var hub = new DashboardSnapshotHub();
            for (int i = 0; i < 100; i++) hub.PublishDecision(i % 2 == 0 ? DashDecisionKind.Entry : DashDecisionKind.Exit, 5930, 100 * i, 25, i, i);
            hub.Pump();
            var f = hub.Acquire()!;
            Assert.Equal(PaperDashboardFrame.MaxDecisions, f.DecisionCount);
            Assert.Equal(99, f.Decisions[0].Reason);                                          // 최신이 위
            Assert.Equal(99 - PaperDashboardFrame.MaxDecisions + 1, f.Decisions[f.DecisionCount - 1].Reason);
            Assert.Equal(("진입", "청산"), (DashDecisionKind.Label(f.Decisions[1].Kind), DashDecisionKind.Label(f.Decisions[0].Kind)));
            hub.Release(f);
            for (int i = 0; i < 2_000; i++) hub.PublishDecision(DashDecisionKind.Hold, 1, 0, 0, 0, 0);   // 링 512 초과
            hub.Pump();
            f = hub.Acquire()!;
            Assert.True(f.DecisionsLost >= 2_000 - 512);
            hub.Release(f);
        }

        // ════════════ §9 E2E · 래칫 · 결선 ════════════

        private static readonly long[] Codes = { 5930, 660, 35420 };
        private const long Px = 70_000, Tick = 100;

        private static OrderBookPayload Book(long code)
        {
            var b = new OrderBookPayload { SymbolCode = code, TimestampTicks = 1 };
            for (int i = 0; i < 10; i++) { b.AskPrice[i] = Px + Tick * (i + 1); b.AskQty[i] = 1_000; b.BidPrice[i] = Px - Tick * i; b.BidQty[i] = 1_000; }
            return b;
        }

        [Fact]
        public void E2E_PaperHarness_GatedChain_VirtualFills_ConvergeOnDashboard_NoRealBroker()
        {
            DashboardSnapshotHub? hubRef = null;
            using var h = new PaperTradingSimulationHarness(fillObserver: f => hubRef!.OnFill(f, 2));
            var hub = new DashboardSnapshotHub(h.Interlocks);
            hubRef = hub;
            h.Recorder.Chain(hub);
            foreach (var c in Codes) { h.OnTossTick(c, Px); hub.OnTick(c, Px); var b = Book(c); h.OnKiwoomBook(in b); }
            h.Pump();
            int n = 0;
            for (int round = 0; round < 10; round++)
            {
                foreach (var c in Codes)
                {
                    h.OnTossTick(c, Px); var b = Book(c); h.OnKiwoomBook(in b);
                    h.Submit(c, OrderSide.Buy, 1, Px + Tick, 3); n++;
                    hub.PublishDecision(DashDecisionKind.Entry, c, 3_000, 150, Px + Tick, 1);   // 전략 계층 대리(판단 근거)
                    h.Submit(c, OrderSide.Sell, 1, Px, 2); n++;
                    hub.PublishDecision(DashDecisionKind.Exit, c, 2_000, 150, Px, 2);
                }
                h.Pump();
                hub.Pump();
            }
            Assert.Equal(n, h.Broker.Filled);
            var f = hub.Acquire()!;
            Assert.Equal(h.Broker.Filled, f.FillsSeen);                                       // ★가상 체결 전량 대시보드 수렴
            Assert.Equal(0, f.Mask);
            Assert.True(f.Sealed);
            Assert.Equal((Math.Min(Codes.Length * 10 * 2, PaperDashboardFrame.MaxDecisions), 0L), (f.DecisionCount, f.DecisionsLost));
            Assert.True(f.Wins + f.Losses > 0);
            hub.Release(f);
            Assert.False(h.Broker.IsLive);                                                    // 가상 브로커만(실 송출 경로 없음)
        }

        private static string RepoRoot()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d is not null && !File.Exists(Path.Combine(d.FullName, "StockManager.sln"))) d = d.Parent;
            Assert.NotNull(d);
            return d!.FullName;
        }

        [Fact]
        public void PureShellRatchet_Held_HostShimUncoupled_Wiring()
        {
            var host = typeof(CanaryHealthSentinel).Assembly;
            var r = PureShellFitnessSentinel.Scan(host);
            Assert.True(r.WithinRatchet, $"coupled {r.HostCoupledUi} > ratchet {r.Ratchet}");
            Assert.True(PureShellFitnessSentinel.RatchetCeiling <= 40);
            var shim = host.GetTypes().Single(t => t.Name == "PaperTradingDashboard" && typeof(Microsoft.AspNetCore.Components.ComponentBase).IsAssignableFrom(t));
            Assert.False(PureShellFitnessSentinel.IsHostCoupledUi(shim));                    // ★[Inject] 0 = 래칫 무증가
            string root = RepoRoot();
            string shimSrc = File.ReadAllText(Path.Combine(root, "StockManager", "Blazor", "Pages", "PaperTradingDashboard.razor"));
            Assert.Contains("@page \"/aidash\"", shimSrc);
            Assert.DoesNotContain("@inject", shimSrc);
            string view = File.ReadAllText(Path.Combine(root, "MS2E.UI.Portable", "Pages", "Trading", "PaperTradingDashboardView.razor"));
            Assert.Contains("@inject IPaperDashboardPort Dash", view);
            Assert.Contains("TimeSpan.FromMilliseconds(50), RenderGov", view);                // 50ms 창 렌더 병합기
            Assert.Contains("Dash.Release(f);", view);
            Assert.DoesNotContain("StockManager.", view);                                      // RCL 엔진 참조 0
            string form = File.ReadAllText(Path.Combine(root, "StockManager", "Blazor", "BlazorHomeForm.cs"));
            Assert.Contains("interlockRecorder.Chain(dashHub);", form);
            Assert.Contains("services.AddSingleton<MS2E.UI.Portable.Ports.Trading.IPaperDashboardPort>(dashHub);", form);
            Assert.Contains("bridge.FillReceived += f => dashHub.OnFill(f, 0);", form);
            Assert.Contains("dashHub.OnFill(f, 1);", form);
            Assert.Contains("dashHub.Start(cfg.GetInt(\"Dashboard.PeriodMs\", 50, 20, 1000));", form);
            string sln = File.ReadAllText(Path.Combine(root, "StockManager.sln"));
            foreach (var proj in new[] { @"StockManager\StockManager.csproj", @"StockManager.PrimesBench\StockManager.PrimesBench.csproj",
                                         @"StockManager.KiwoomProxy\StockManager.KiwoomProxy.csproj", @"tools\KiwoomComShim\KiwoomComShim.csproj" })
                Assert.Contains(proj, sln);
        }
    }
}
