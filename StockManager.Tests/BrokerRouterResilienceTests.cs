// ============================================================================
// BrokerRouterResilienceTests — [라우터 하드닝 · 추천 A] 스위치보드 블랙리스트 · 페일오버 캐스케이드 · 세대 기반 체결 판정 ·
//                               128비트 ID 사상 · 경계 가드 · 하트비트 비트마스크(방향 A) · 이중 장애 Disarm(방향 B)
//
//   수정 전 실측(Red):
//     · 장애 판정 브로커로 SwitchTo/TrySwitch 가 그대로 라우팅을 되돌렸다(장애 이력이 센티널 내부에만 존재)
//     · 페일오버 시 장애 브로커의 미체결이 취소 전환되지 않았다(센티널↔캔슬러 미결속)
//     · MarkFilled 가 void — 취소 전환 뒤 도착한 실체결과 옛 세대 통보를 구분 불가(체결 유실 위험)
//     · 주문 1건 기준 조립(ID 사상·오배송 대조) 부재 · 전 브로커 장애 도달 신호 부재
// ============================================================================
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MS2E.Core;
using Xunit;
using static StockManager.Tests.ZeroAllocProbe;

namespace StockManager.Tests
{
    [Collection("ConcurrencyStress")]
    public sealed class BrokerRouterResilienceTests
    {
        private static (LockFreeBrokerSwitchboard Board, BrokerFailoverSentinel Sentinel, PendingOrderCascadeCanceller Pending, ResilientOrderRouter Router)
            Rig(int brokers = 2, Action? onDual = null, BrokerHeartbeatBitmaskMonitor? hb = null, long maxSlipBps = 50)
        {
            var board = new LockFreeBrokerSwitchboard(brokers);
            var pending = new PendingOrderCascadeCanceller(1024);
            var sentinel = new BrokerFailoverSentinel(board, pendingOrders: pending, onDualBreakdown: onDual);
            return (board, sentinel, pending, new ResilientOrderRouter(board, sentinel, pending, 1024, maxSlipBps, hb));
        }

        private static UInt128 Id(ulong n) => new UInt128(0xABCD_0000_0000_0000UL, n);

        // ═══ §1 스위치보드 블랙리스트 · 페일오버 ═══

        [Fact]
        public void Faulted_Broker_Cannot_Be_Routed_Back_Via_Any_Switch_Path()
        {
            var (board, sentinel, _, _) = Rig(3);
            sentinel.ReportFault(0, BrokerFaultKind.Disconnect, 0, 100);
            Assert.Equal(1, board.Active);
            Assert.True(board.IsBlacklisted(0));
            Assert.Equal(1, board.SwitchTo(0, 200));             // 수정 전: 0 으로 되돌아감
            Assert.Equal(1, board.Active);
            Assert.False(board.TrySwitch(1, 0, 300));            // CAS 경로도 거부
            Assert.Equal(2, board.RefusedSwitches);
            Assert.True(board.TrySwitch(1, 2, 400));             // 건강 브로커로는 정상 전환
        }

        [Fact]
        public void Concurrent_Fault_Reports_Converge_To_One_Healthy_Broker_Without_Revisiting_Faulted()
        {
            for (int round = 0; round < 50; round++)
            {
                var (board, sentinel, _, _) = Rig(4);
                using var go = new ManualResetEventSlim(false);
                var ts = new[] { 0, 1, 2 }.Select(b => new Thread(() => { go.Wait(); sentinel.ReportFault(b, BrokerFaultKind.ProxyDead, 0, 1); })).ToList();
                ts.ForEach(t => t.Start()); go.Set(); ts.ForEach(t => t.Join());
                // 0·1·2 가 동시 장애 — 어떤 인터리빙이든 최종 활성은 유일 건강 브로커 3, 혹은 CAS 경합으로 전환이 지연돼도 장애 브로커가 아니어야
                Assert.True(!board.IsBlacklisted(board.Active) || board.AllBlacklisted, $"round {round}: active={board.Active}");
                Assert.False(sentinel.DualBreakdown);
            }
        }

        [Fact]
        public void Failover_Cascade_Cancels_Only_Faulted_Brokers_Pending_Orders()
        {
            var (board, sentinel, pending, router) = Rig(2);
            for (ulong i = 1; i <= 5; i++) Assert.True(router.Route(Id(i), 1000, 1000, 0.1).Accepted);   // 브로커 0
            board.SwitchTo(1, 1);
            sentinel.TryRearm(true, true);                                                                // 테스트용: 전환 후 재무장 상태로
            for (ulong i = 6; i <= 8; i++) Assert.Equal(1, router.Route(Id(i), 1000, 1000, 0.1).Broker);  // 브로커 1
            board.SwitchTo(0, 2);
            sentinel.ReportFault(0, BrokerFaultKind.Disconnect, 0, 3);
            Assert.Equal(5, sentinel.CascadeCancelled);                                                   // 수정 전: 0(미결속)
            Assert.Equal(0, pending.PendingCount(0));
            Assert.Equal(3, pending.PendingCount(1));                                                     // 건강 브로커 주문은 무접촉
            Assert.Equal(1, board.Active);
        }

        // ═══ §1 세대 기반 체결 판정(오해제·유실 0) ═══

        [Fact]
        public void Late_Fill_After_Cascade_Cancel_Is_Reported_Not_Lost()
        {
            var (_, sentinel, pending, router) = Rig(2);
            Assert.True(router.Route(Id(1), 1000, 1000, 0).Accepted);
            sentinel.ReportFault(0, BrokerFaultKind.Http5xx, 99, 1);   // 캐스케이드 취소 전환
            Assert.Equal(FillNoticeKind.FilledAfterCancel, router.OnFill(Id(1), 0));   // 취소 요청과 경합한 실체결 — 대사 대상
            Assert.Equal(1, pending.LateFillsAfterCancel);
            Assert.Equal(0, router.TrackedOrders);
        }

        [Fact]
        public void Stale_Generation_Notice_Cannot_Release_A_Reused_Slot()
        {
            var pending = new PendingOrderCascadeCanceller(1);
            long h1 = pending.Register(0);
            Assert.Equal(FillOutcome.Filled, pending.MarkFilled(h1));
            long h2 = pending.Register(0);                          // 같은 슬롯 재사용(세대 +1)
            Assert.Equal(h1 & 0xFFFF_FFFF, h2 & 0xFFFF_FFFF);
            Assert.Equal(FillOutcome.Stale, pending.MarkFilled(h1)); // 옛 통보
            Assert.Equal(1, pending.PendingCount(0));               // 새 주문은 살아있음
            Assert.Equal(1, pending.StaleNotices);
        }

        [Fact]
        public void Misrouted_Fill_From_Other_Broker_Is_Rejected_Without_Side_Effect()
        {
            var (_, _, pending, router) = Rig(2);
            router.Route(Id(7), 1000, 1000, 0);
            Assert.Equal(FillNoticeKind.MisroutedBroker, router.OnFill(Id(7), 1));   // 브로커 1 이 브로커 0 주문 체결 통보
            Assert.Equal(1, pending.PendingCount(0));
            Assert.Equal(FillNoticeKind.Filled, router.OnFill(Id(7), 0));
            Assert.Equal(FillNoticeKind.UnknownOrder, router.OnFill(Id(7), 0));      // 중복 통보
        }

        [Fact]
        public void Cancel_Confirm_Only_Releases_Cascade_Cancelled_Orders()
        {
            var (_, sentinel, pending, router) = Rig(2);
            router.Route(Id(1), 1000, 1000, 0);
            Assert.False(router.OnCancelConfirmed(Id(1), 0));                          // 취소 요청 전 — 살아있는 주문 해제 금지
            sentinel.ReportFault(0, BrokerFaultKind.Disconnect, 0, 1);
            Assert.True(router.OnCancelConfirmed(Id(1), 0));
            Assert.Equal(0, pending.PendingCount(0));
            Assert.Equal(0, router.TrackedOrders);
        }

        // ═══ §2 경계 가드(원자 거부 · 부작용 0) ═══

        [Theory]
        [InlineData(double.NaN, 1000, 1000, OrderRouteVerdict.InvalidRegimeSignal)]
        [InlineData(double.PositiveInfinity, 1000, 1000, OrderRouteVerdict.InvalidRegimeSignal)]
        [InlineData(0.5, 0, 1000, OrderRouteVerdict.InvalidPrice)]
        [InlineData(0.5, -5, 1000, OrderRouteVerdict.InvalidPrice)]
        [InlineData(0.5, 1000, 0, OrderRouteVerdict.InvalidPrice)]
        [InlineData(0.5, 1006, 1000, OrderRouteVerdict.SlippageExceeded)]          // 60bps > 50bps
        [InlineData(0.5, long.MaxValue, 1, OrderRouteVerdict.SlippageExceeded)]    // 오버플로 포화(Int128)
        public void Abnormal_Inputs_Are_Rejected_Atomically(double regime, long limit, long reference, OrderRouteVerdict expected)
        {
            var (_, _, pending, router) = Rig();
            var r = router.Route(Id(1), limit, reference, regime);
            Assert.Equal(expected, r.Verdict);
            Assert.Equal(-1, r.Handle);
            Assert.Equal(0, pending.PendingCount(0));   // 슬롯 무변경
            Assert.Equal(0, router.TrackedOrders);      // 맵 무변경
        }

        [Fact]
        public void Duplicate_Or_Zero_Order_Id_Rejected_And_Slot_Returned()
        {
            var (_, _, pending, router) = Rig();
            Assert.True(router.Route(Id(9), 1000, 1000, 0).Accepted);
            Assert.Equal(OrderRouteVerdict.DuplicateOrderId, router.Route(Id(9), 1000, 1000, 0).Verdict);
            Assert.Equal(OrderRouteVerdict.InvalidOrderId, router.Route(UInt128.Zero, 1000, 1000, 0).Verdict);
            Assert.Equal(1, pending.PendingCount(0));   // 중복 시 점유했던 슬롯은 즉시 반납
        }

        [Fact]
        public void Disarmed_Router_Refuses_Every_Order_Until_Physical_Rearm()
        {
            var (_, sentinel, _, router) = Rig(2);
            sentinel.ReportFault(0, BrokerFaultKind.Disconnect, 0, 1);
            Assert.Equal(OrderRouteVerdict.Disarmed, router.Route(Id(1), 1000, 1000, 0).Verdict);
            Assert.False(sentinel.TryRearm(true, false));
            Assert.Equal(OrderRouteVerdict.Disarmed, router.Route(Id(1), 1000, 1000, 0).Verdict);
            Assert.True(sentinel.TryRearm(true, true));
            var r = router.Route(Id(1), 1000, 1000, 0);
            Assert.True(r.Accepted);
            Assert.Equal(1, r.Broker);                  // 페일오버된 활성 브로커로 라우팅
        }

        // ═══ §3 방향 B — 이중 장애 Atomic Disarm ═══

        [Fact]
        public void Dual_Breakdown_Fires_Once_Blocks_Routing_And_Clears_Only_On_Rearm()
        {
            int kills = 0;
            var (board, sentinel, _, router) = Rig(2, onDual: () => Interlocked.Increment(ref kills));
            sentinel.ReportFault(0, BrokerFaultKind.Disconnect, 0, 1);
            Assert.False(sentinel.DualBreakdown);
            sentinel.ReportFault(1, BrokerFaultKind.ProxyDead, 0, 2);
            Assert.True(sentinel.DualBreakdown);
            Assert.True(board.AllBlacklisted);
            sentinel.ReportFault(1, BrokerFaultKind.Disconnect, 0, 3);
            Assert.Equal(1, kills);                                                         // 정확히 1회
            Assert.Equal(OrderRouteVerdict.DualBreakdown, router.Route(Id(1), 1000, 1000, 0).Verdict);
            Assert.True(sentinel.TryRearm(true, true));
            Assert.False(sentinel.DualBreakdown);
            Assert.False(board.AllBlacklisted);
        }

        [Fact]
        public void Concurrent_Dual_Breakdown_Fires_Kill_Exactly_Once()
        {
            for (int round = 0; round < 100; round++)
            {
                int kills = 0;
                var (_, sentinel, _, _) = Rig(2, onDual: () => Interlocked.Increment(ref kills));
                Parallel.Invoke(() => sentinel.ReportFault(0, BrokerFaultKind.Disconnect, 0, 1),
                                () => sentinel.ReportFault(1, BrokerFaultKind.Disconnect, 0, 1));
                Assert.True(sentinel.DualBreakdown);
                Assert.Equal(1, kills);
            }
        }

        // ═══ §3 방향 A — 하트비트 비트마스크 ═══

        [Fact]
        public void Heartbeat_Silence_Reports_ProxyDead_Once_And_Fails_Over()
        {
            var hb = new BrokerHeartbeatBitmaskMonitor(2, timeoutTicks: 100);
            var (board, sentinel, _, router) = Rig(2, hb: hb);
            hb.Beat(0, 0); hb.Beat(1, 0);
            Assert.Equal(0, router.SweepHeartbeats(50));
            hb.Beat(1, 150);
            Assert.Equal(1, router.SweepHeartbeats(160));                 // 브로커 0 침묵 160 > 100
            Assert.Equal(1, board.Active);
            Assert.Equal(BrokerFaultKind.ProxyDead, sentinel.LastFault);
            Assert.Equal(0, router.SweepHeartbeats(170));                 // 같은 사망 재보고 0(에지)
            hb.Beat(0, 180);
            Assert.Equal(0b10, hb.HealthyMask);                           // 죽은 브로커 자동 부활 금지(fail-closed)
        }

        [Fact]
        public void Heartbeat_Never_Started_Broker_Is_Not_Declared_Dead()
        {
            var hb = new BrokerHeartbeatBitmaskMonitor(3, 10);
            hb.Beat(0, 0);
            Assert.Equal(0b1, hb.Sweep(1_000_000));
            Assert.Equal(0, hb.DeadMask & 0b110);
        }

        // ═══ §2 ID 맵 정합성 ═══

        [Fact]
        public void Order_Id_Map_Survives_Collision_Clusters_And_Backward_Shift_Deletes()
        {
            var map = new ZeroAllocOrderIdMap(64);
            var rnd = new Random(7);
            var live = new System.Collections.Generic.Dictionary<UInt128, long>();
            for (int step = 0; step < 20_000; step++)
            {
                var key = new UInt128((ulong)rnd.Next(1, 4), (ulong)rnd.Next(1, 80));   // 좁은 키 공간 — 군집·삭제 경로 집중
                if (rnd.Next(2) == 0)
                {
                    bool added = map.TryAdd(key, step, 0);
                    Assert.Equal(!live.ContainsKey(key) && live.Count < 56, added);
                    if (added) live[key] = step;
                }
                else
                {
                    Assert.Equal(live.Remove(key), map.TryRemove(key));
                }
                Assert.Equal(live.Count, map.Count);
            }
            foreach (var (k, v) in live) { Assert.True(map.TryGet(k, out long h, out _)); Assert.Equal(v, h); }
        }

        // ═══ δ=0 ═══

        [Fact]
        public void Route_Fill_And_Heartbeat_Hot_Paths_Are_Zero_Alloc()
        {
            var hb = new BrokerHeartbeatBitmaskMonitor(2, 1_000_000);
            var (_, _, _, router) = Rig(2, hb: hb, maxSlipBps: 10_000);
            ulong n = 1;
            void Cycle()
            {
                for (int i = 0; i < 500; i++)
                {
                    var id = Id(n++);
                    router.Route(id, 1000 + i % 7, 1000, 0.25);
                    router.OnFill(id, 0);
                    hb.Beat(0, i); hb.Beat(1, i);
                    router.SweepHeartbeats(i);
                }
            }
            Cycle();
            Assert.Equal(0, MinAllocated(Cycle));
            Assert.Equal(0, router.TrackedOrders);
        }
    }
}
