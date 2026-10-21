// ============================================================================
// ChildSlotSplitRoutingTests — [자식 슬롯 분할 라우팅 · 추천 A]
//
//  §1 ChildSlotOrderSplitter — 한도 비례 · 한도 부족 · 1~3호가 단계 분할(자식 지정가 = 단계 호가 · 부모보다 불리 0) · 매도 대칭 ·
//     판정(빈 깊이/브로커 없음/부모 무효/버퍼 부족) · 퍼징 20,000건 불변식(★No Over-Dispatch · 한도 · 깊이 · ID 고유) · δ=0.
//  §2 ResilientOrderRouter.RouteTo — 지정 브로커 · 장애/블랙리스트/범위 밖 거부 · 중복 ID.
//  §3 ChildSlotTrackingMatrix — 수렴(AllFilled/PartialCancelled/ChildRejected) · 송출 실패 = 미송출 형제 회수 + Disarm(sticky · 이중키) ·
//     브로커 거부 통보 회수 · 과체결 · 방향 B 불균형(500ms) 잔여 일괄 취소 1회 · seqlock 동시 판독 찢김 0 · δ=0.
//  §4 게이트 + 드레인 통합 — 2개 브로커 분할 송출 · 자식별 추적 슬롯/라우터 키 · 추적 실패 원자 롤백 · 자식 거부 시 미송출 형제 회수 + Disarm ·
//     송출 합 ≤ 부모.
// ============================================================================
using System;
using System.Collections.Generic;
using System.Threading;
using MS2E.Core;
using MS2E.Infrastructure;
using StockManager.Adapters;
using StockManager.Engines;
using Xunit;
using static StockManager.Tests.ZeroAllocProbe;

namespace StockManager.Tests
{
    [Collection("ConcurrencyStress")]
    public sealed class ChildSlotSplitRoutingTests
    {
        private const long S = 100_000_000L;   // raw 10^8

        private static BrokerSplitCap Cap(int broker, long max, bool ok = true) => new() { Broker = broker, MaxShares = max, Available = ok };

        private static L2Depth Asks(params (long px, long sh)[] lv)
        {
            var d = new L2Depth();
            for (int i = 0; i < lv.Length; i++) { d.AskPriceRaw[i] = lv[i].px * S; d.AskQtyRaw[i] = lv[i].sh * S; }
            return d;
        }

        private static L2Depth Bids(params (long px, long sh)[] lv)
        {
            var d = new L2Depth();
            for (int i = 0; i < lv.Length; i++) { d.BidPriceRaw[i] = lv[i].px * S; d.BidQtyRaw[i] = lv[i].sh * S; }
            return d;
        }

        // ════════════ §1 분할기 ════════════

        [Fact]
        public void Split_NoDepth_ProportionalToCaps_IdsCarryParent()
        {
            var none = new L2Depth();
            var dst = new ChildOrderPlan[64];
            Assert.Equal(SplitVerdict.Ok, ChildSlotOrderSplitter.Split(77, OrderSide.Buy, 100, 70_000 * S, in none, false, new[] { Cap(0, 60), Cap(1, 40) }, dst, out int n, out long total));
            Assert.Equal((2, 100L), (n, total));
            Assert.Equal((0, 60L, 70_000 * S, -1), (dst[0].Broker, dst[0].Shares, dst[0].LimitRaw, dst[0].Level));
            Assert.Equal((1, 40L), (dst[1].Broker, dst[1].Shares));
            Assert.Equal(77, ChildIdSpace.ParentOf(dst[1].ChildId));
            Assert.Equal(1, ChildIdSpace.IndexOf(dst[1].ChildId));
            Assert.Equal(77UL, (ulong)(dst[0].TrackingId >> 64));                 // 128비트 추적 ID 상위 = 부모
            Assert.Equal((ulong)dst[0].ChildId, (ulong)dst[0].TrackingId);

            // 한도 부족 — 한도 합만큼만(★한도 초과 0 · 부모 초과 0)
            Assert.Equal(SplitVerdict.Ok, ChildSlotOrderSplitter.Split(78, OrderSide.Buy, 100, 70_000 * S, in none, false, new[] { Cap(0, 30), Cap(1, 20), Cap(2, 999, ok: false) }, dst, out n, out total));
            Assert.Equal((2, 50L), (n, total));
            Assert.Equal((30L, 20L), (dst[0].Shares, dst[1].Shares));
        }

        [Fact]
        public void Split_Depth3Levels_ChildLimitIsLevelPrice_NeverWorseThanParent_SellSymmetric()
        {
            var dst = new ChildOrderPlan[64];
            var asks = Asks((70_000, 30), (70_010, 50), (70_020, 100));
            // 매수 지정가 70,010 — 3호가(70,020)는 지정가 밖 → 깊이 상한 80
            Assert.Equal(SplitVerdict.Ok, ChildSlotOrderSplitter.Split(9, OrderSide.Buy, 200, 70_010 * S, in asks, true, new[] { Cap(0, 500), Cap(1, 500) }, dst, out int n, out long total));
            Assert.Equal(80, total);                                              // ★과대 발주 0(즉시 체결 가능 잔량만)
            long lv0 = 0, lv1 = 0;
            for (int i = 0; i < n; i++)
            {
                Assert.True(dst[i].LimitRaw <= 70_010 * S);                       // 부모보다 불리하지 않음
                if (dst[i].Level == 0) { lv0 += dst[i].Shares; Assert.Equal(70_000 * S, dst[i].LimitRaw); }
                if (dst[i].Level == 1) { lv1 += dst[i].Shares; Assert.Equal(70_010 * S, dst[i].LimitRaw); }
            }
            Assert.Equal((30L, 50L), (lv0, lv1));                                 // 단계 잔량 비례

            var bids = Bids((69_990, 40), (69_980, 40), (69_970, 40));
            Assert.Equal(SplitVerdict.Ok, ChildSlotOrderSplitter.Split(10, OrderSide.Sell, 70, 69_980 * S, in bids, true, new[] { Cap(0, 35), Cap(1, 35) }, dst, out n, out total));
            Assert.Equal(70, total);
            for (int i = 0; i < n; i++) Assert.True(dst[i].LimitRaw >= 69_980 * S);   // 매도 = 부모 이상

            var empty = new L2Depth();
            Assert.Equal(SplitVerdict.NothingFillable, ChildSlotOrderSplitter.Split(11, OrderSide.Buy, 10, 70_000 * S, in empty, true, new[] { Cap(0, 10) }, dst, out n, out _));
            var far = Asks((71_000, 100));
            Assert.Equal(SplitVerdict.NothingFillable, ChildSlotOrderSplitter.Split(12, OrderSide.Buy, 10, 70_000 * S, in far, true, new[] { Cap(0, 10) }, dst, out _, out _));
            Assert.Equal(SplitVerdict.NoBroker, ChildSlotOrderSplitter.Split(13, OrderSide.Buy, 10, 70_000 * S, in empty, false, new[] { Cap(0, 0), Cap(1, 5, ok: false) }, dst, out _, out _));
            Assert.Equal(SplitVerdict.InvalidParent, ChildSlotOrderSplitter.Split(14, OrderSide.Buy, 0, 70_000 * S, in empty, false, new[] { Cap(0, 5) }, dst, out _, out _));
            Assert.Equal(SplitVerdict.InvalidParent, ChildSlotOrderSplitter.Split(1L << 57, OrderSide.Buy, 5, 70_000 * S, in empty, false, new[] { Cap(0, 5) }, dst, out _, out _));
            Assert.Equal(SplitVerdict.TooManyChildren, ChildSlotOrderSplitter.Split(15, OrderSide.Buy, 10, 70_000 * S, in empty, false, new[] { Cap(0, 5), Cap(1, 5) }, new ChildOrderPlan[1], out n, out total));
            Assert.Equal((0, 0L), (n, total));                                    // 부분 산출 0
        }

        [Fact]
        public void Split_Fuzz20k_NoOverDispatch_CapsDepthIdsInvariants_ZeroAlloc()
        {
            var rng = new Random(20261007);
            var dst = new ChildOrderPlan[64];
            var caps = new BrokerSplitCap[4];
            var ids = new HashSet<long>();
            var perBroker = new long[4];
            for (int it = 0; it < 20_000; it++)
            {
                bool buy = rng.Next(2) == 0;
                long limit = 70_000 + rng.Next(-30, 31);
                var d = new L2Depth();
                long fillable = 0;
                int lv = rng.Next(0, 4);
                for (int i = 0; i < lv; i++)
                {
                    long px = buy ? 69_990 + i * 10 : 70_010 - i * 10;
                    long sh = rng.Next(0, 400);
                    if (buy) { d.AskPriceRaw[i] = px * S; d.AskQtyRaw[i] = sh * S; } else { d.BidPriceRaw[i] = px * S; d.BidQtyRaw[i] = sh * S; }
                }
                bool hasDepth = rng.Next(4) != 0;
                if (hasDepth)
                    for (int i = 0; i < 3; i++)
                    {
                        long px = buy ? d.AskPriceRaw[i] : d.BidPriceRaw[i], q = buy ? d.AskQtyRaw[i] : d.BidQtyRaw[i];
                        if (px <= 0 || q <= 0) break;
                        if (buy ? px > limit * S : px < limit * S) break;
                        fillable += q / S;
                    }
                int nb = rng.Next(1, 5);
                for (int b = 0; b < nb; b++) caps[b] = Cap(b, rng.Next(0, 300), rng.Next(5) != 0);
                long parent = rng.Next(1, 1_000);
                var v = ChildSlotOrderSplitter.Split(1000 + it, buy ? OrderSide.Buy : OrderSide.Sell, parent, limit * S, in d, hasDepth, caps.AsSpan(0, nb), dst, out int n, out long total);
                if (v != SplitVerdict.Ok) { Assert.Equal(0, n); continue; }
                long sum = 0; ids.Clear();
                Array.Clear(perBroker);
                for (int i = 0; i < n; i++)
                {
                    sum += dst[i].Shares;
                    perBroker[dst[i].Broker] += dst[i].Shares;
                    Assert.True(dst[i].Shares > 0);
                    Assert.True(buy ? dst[i].LimitRaw <= limit * S : dst[i].LimitRaw >= limit * S);
                    Assert.True(ids.Add(dst[i].ChildId));
                    Assert.Equal(1000 + it, ChildIdSpace.ParentOf(dst[i].ChildId));
                }
                Assert.Equal(total, sum);
                Assert.True(sum <= parent, "★No Over-Dispatch");
                if (hasDepth) Assert.True(sum <= fillable);
                for (int b = 0; b < nb; b++) Assert.True(perBroker[b] <= (caps[b].Available ? caps[b].MaxShares : 0));
                Assert.True(n <= ChildIdSpace.MaxChildren);
            }
            var deep = Asks((70_000, 100), (70_010, 100), (70_020, 100));
            var cp = new[] { Cap(0, 120), Cap(1, 80), Cap(2, 50) };
            Assert.Equal(0, MinAllocated(() => { for (int i = 0; i < 200; i++) ChildSlotOrderSplitter.Split(5_000 + i, OrderSide.Buy, 250, 70_020 * S, in deep, true, cp, dst, out _, out _); }));
        }

        // ════════════ §2 라우터 RouteTo ════════════

        [Fact]
        public void RouteTo_SpecifiedBroker_RejectsFaultedBlacklistedOutOfRange()
        {
            var board = new LockFreeBrokerSwitchboard(2, initialActive: 0);
            var pending = new PendingOrderCascadeCanceller(64);
            var sentinel = new BrokerFailoverSentinel(board, pendingOrders: pending);
            var router = new ResilientOrderRouter(board, sentinel, pending, idMapCapacityPow2: 64, maxSlippageBps: 50);
            var r = router.RouteTo(new UInt128(1, 1), 1, 70_000, 70_000, 0.0);
            Assert.Equal((OrderRouteVerdict.Routed, 1), (r.Verdict, r.Broker));    // 활성(0)이 아닌 지정 브로커(1)
            Assert.Equal(OrderRouteVerdict.DuplicateOrderId, router.RouteTo(new UInt128(1, 1), 1, 70_000, 70_000, 0.0).Verdict);
            Assert.Equal(OrderRouteVerdict.BrokerBlacklisted, router.RouteTo(new UInt128(1, 2), 5, 70_000, 70_000, 0.0).Verdict);
            board.Blacklist(1);
            Assert.Equal(OrderRouteVerdict.BrokerBlacklisted, router.RouteTo(new UInt128(1, 3), 1, 70_000, 70_000, 0.0).Verdict);
            Assert.Equal(OrderRouteVerdict.SlippageExceeded, router.RouteTo(new UInt128(1, 4), 0, 71_000, 70_000, 0.0).Verdict);
        }

        // ════════════ §3 추적 매트릭스 ════════════

        private sealed class Clock { public long Now = 1_000_000; }

        private static ChildOrderPlan[] Plans(long parent, params (int broker, long shares)[] k)
        {
            var a = new ChildOrderPlan[k.Length];
            for (int i = 0; i < k.Length; i++)
                a[i] = new ChildOrderPlan { ParentId = parent, ChildId = ChildIdSpace.Compose(parent, i), Broker = k[i].broker, Shares = k[i].shares, LimitRaw = 70_000 * S, Index = i };
            return a;
        }

        private static FillPayload Fill(long bid, long qty) => new() { OrderId = bid, SymbolCode = 5930, FillQty = qty, FillPrice = 70_000, Side = 1, Status = BridgeFillStatusCodec.Encode(BridgeFillStatus.Filled, false) };
        private static FillPayload Notice(long bid, BridgeFillStatus st) => new() { OrderId = bid, SymbolCode = 5930, FillQty = 0, Side = 1, Status = BridgeFillStatusCodec.Encode(st, true), RemainingQty = 0 };

        [Fact]
        public void Matrix_Converges_AllFilled_PartialCancelled_OverDispatchRefused()
        {
            var clk = new Clock();
            var m = new ChildSlotTrackingMatrix(8, () => clk.Now);
            Assert.False(m.Open(1, 10, Plans(1, (0, 6), (1, 5))));                 // ★합 11 > 부모 10 = 거부
            var p = Plans(1, (0, 6), (1, 4));
            Assert.True(m.Open(1, 10, p));
            m.MarkSent(p[0].ChildId, 501); m.MarkSent(p[1].ChildId, 502);
            m.OnFill(Fill(501, 6)); m.OnFill(Fill(502, 3));
            Assert.True(m.TryGetParent(1, out var s));
            Assert.Equal((ParentConvergence.Open, 9L), (s.State, s.FilledQty));
            m.OnFill(Fill(502, 1));
            Assert.True(m.TryGetParent(1, out s));
            Assert.Equal(ParentConvergence.AllFilled, s.State);

            var q = Plans(2, (0, 5), (1, 5));
            Assert.True(m.Open(2, 10, q));
            m.MarkSent(q[0].ChildId, 601); m.MarkSent(q[1].ChildId, 602);
            m.OnFill(Fill(601, 5)); m.OnFill(Notice(602, BridgeFillStatus.CancelConfirmed));
            Assert.True(m.TryGetParent(2, out s));
            Assert.Equal((ParentConvergence.PartialCancelled, 5L, 5L), (s.State, s.FilledQty, s.CancelledQty));
            Assert.Equal(2, m.ReleaseConverged());
            Assert.False(m.Tripped);
        }

        [Fact]
        public void Matrix_SendFailure_ReclaimsUnsentSiblings_DisarmsSticky_RearmNeedsDualKey()
        {
            var m = new ChildSlotTrackingMatrix(8);
            var mask = new ArmingInterlockBitmask(ArmingInterlockSource.ChildSlotRejection);
            m.BindInterlock(mask, ArmingInterlockSource.ChildSlotRejection);
            Assert.False(mask.IsRaised(ArmingInterlockSource.ChildSlotRejection));
            var p = Plans(3, (0, 2), (1, 2), (0, 2), (1, 2));
            Assert.True(m.Open(3, 8, p));
            m.MarkSent(p[0].ChildId, 701);
            Assert.Equal(2, m.MarkSendFailed(p[1].ChildId));                       // ★미송출 형제 2건(#2 #3) 회수
            Assert.True(mask.IsRaised(ArmingInterlockSource.ChildSlotRejection));  // ★Disarm
            Assert.True(m.TryGetParent(3, out var s));
            Assert.Equal((1, 2, ParentConvergence.Open), (s.Rejected, s.Reclaimed, s.State));   // #0 은 접수 중
            m.OnFill(Fill(701, 2));
            Assert.True(m.TryGetParent(3, out s));
            Assert.Equal(ParentConvergence.ChildRejected, s.State);
            Assert.False(m.TryRearm(true, false));
            Assert.True(m.TryRearm(true, true));
            Assert.False(mask.IsRaised(ArmingInterlockSource.ChildSlotRejection));

            var q = Plans(4, (0, 3), (1, 3), (1, 3));
            Assert.True(m.Open(4, 9, q));
            m.MarkSent(q[0].ChildId, 801);
            m.OnFill(Notice(801, BridgeFillStatus.Rejected));                      // 브로커 거부 통보
            Assert.True(m.Tripped);
            Assert.True(m.TryGetParent(4, out s));
            Assert.Equal(2, s.Reclaimed);

            var o = Plans(5, (0, 2));
            Assert.True(m.Open(5, 2, o));
            m.MarkSent(o[0].ChildId, 901);
            m.TryRearm(true, true);
            m.OnFill(Fill(901, 3));                                                // 과체결
            Assert.Equal(1, m.Overfills);
            Assert.True(m.Tripped);
        }

        [Fact]
        public void Matrix_ImbalanceOver500ms_CancelsRemainingChildrenOnce()
        {
            var clk = new Clock();
            var cancels = new List<(int, long)>();
            var m = new ChildSlotTrackingMatrix(8, () => clk.Now, 500_000, (b, o) => cancels.Add((b, o)));
            var p = Plans(6, (0, 10), (1, 10), (1, 10));
            Assert.True(m.Open(6, 30, p));
            m.MarkSent(p[0].ChildId, 1001); m.MarkSent(p[1].ChildId, 1002); m.MarkSent(p[2].ChildId, 1003);
            m.OnFill(Fill(1001, 10));                                              // 키움 자식 완전 체결
            m.OnFill(Fill(1003, 4));                                               // KIS 자식 하나는 부분 체결
            clk.Now += 400_000;
            Assert.Equal(0, m.Evaluate());                                         // 임계 전
            clk.Now += 200_000;
            Assert.Equal(2, m.Evaluate());                                         // ★600ms — 잔여 자식(1002 미체결 · 1003 잔량 6) 일괄 취소
            Assert.Equal(new[] { (1, 1002L), (1, 1003L) }, cancels.ToArray());
            Assert.True(m.Tripped);
            Assert.Equal(0, m.Evaluate());                                         // 같은 자식 재취소 0
        }

        [Fact]
        public void Matrix_SeqlockReaders_NeverTorn_UnderConcurrentWriters_ZeroAlloc()
        {
            var m = new ChildSlotTrackingMatrix(4, imbalanceMicros: long.MaxValue / 4);
            var p = Plans(7, (0, 1_000_000), (1, 1_000_000));
            Assert.True(m.Open(7, 2_000_000, p));
            m.MarkSent(p[0].ChildId, 1101); m.MarkSent(p[1].ChildId, 1102);
            int stop = 0, torn = 0; Exception? err = null;
            var writers = new Thread[2];
            for (int w = 0; w < 2; w++)
            {
                long bid = 1101 + w;
                writers[w] = new Thread(() =>
                {
                    try { for (int i = 0; i < 200_000 && Volatile.Read(ref stop) == 0; i++) m.OnFill(Fill(bid, 1)); }
                    catch (Exception ex) { err = ex; }
                }) { IsBackground = true };
                writers[w].Start();
            }
            long lastFilled = 0;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (writers[0].IsAlive || writers[1].IsAlive)
            {
                if (m.TryGetParent(7, out var s))
                {
                    if (s.FilledQty < lastFilled || s.FilledQty > s.OrderQty || s.OrderQty != 2_000_000) Interlocked.Increment(ref torn);
                    lastFilled = s.FilledQty;
                }
                if (sw.ElapsedMilliseconds > 20_000) { Volatile.Write(ref stop, 1); break; }
            }
            foreach (var t in writers) Assert.True(t.Join(10_000));
            Assert.Null(err);
            Assert.Equal(0, torn);
            Assert.True(m.TryGetParent(7, out var fin));
            Assert.Equal(400_000, fin.FilledQty);

            var z = new ChildSlotTrackingMatrix(4);
            var zp = Plans(8, (0, 1_000_000), (1, 1_000_000));
            z.Open(8, 2_000_000, zp);
            z.MarkSent(zp[0].ChildId, 1201); z.MarkSent(zp[1].ChildId, 1202);
            var f = Fill(1201, 1);
            Assert.Equal(0, MinAllocated(() => { for (int i = 0; i < 500; i++) { z.OnFill(f); z.TryGetParent(8, out _); z.Evaluate(); } }));
        }

        // ════════════ §4 게이트 + 드레인 통합 ════════════

        private sealed class SplitBroker : IBroker
        {
            public readonly string Tag; public long NextId; public int Places; public long Shares; public int RejectFrom = int.MaxValue;
            public readonly List<decimal> Prices = new();
            public SplitBroker(string tag, long firstId) { Tag = tag; NextId = firstId; }
            public string Name => Tag;
            public bool IsLive => false;
            public (bool ok, string msg) Place(PaperAsset asset, string symbol, string name, PaperSide side, PaperKind kind, decimal qty, decimal limitPrice, decimal currentPrice)
            {
                Places++;
                if (Places >= RejectFrom) return (false, "증거금 부족");
                Shares += (long)qty; Prices.Add(limitPrice);
                return (true, (NextId++).ToString());
            }
            public void Cancel(string orderId) { }
            public decimal HeldQty(PaperAsset asset, string symbol) => 0m;
        }

        private sealed class Rig
        {
            public long Now = 1_000_000;
            public ResilientOrderRouter Router = null!;
            public OrderExecutionFeedAdapter Exec = null!;
            public RouterFeedGate Gate = null!;
            public OrderDispatchRing Ring = null!;
            public OrderDrainWorker Worker = null!;
            public SplitBroker Kiwoom = null!, Kis = null!;
            public ChildSlotTrackingMatrix Matrix = null!;
            public ArmingInterlockBitmask Mask = null!;
            public DepthBook Depth = null!;
            public void Matrix_TripExternalLater() => Matrix?.TripExternal();
        }

        private sealed class FixedFeed : IBrokerLimitFeed
        {
            private readonly long _a, _b;
            public FixedFeed(long a, long b) { _a = a; _b = b; }
            public int Fill(Span<BrokerSplitCap> dst, long symbolCode, OrderSide side, long limitPriceRaw, long parentReservedWon)
            { dst[0] = Cap(0, _a); dst[1] = Cap(1, _b); return 2; }
        }

        private static Rig Make(long capKiwoom, long capKis, int execCapacity = 64, bool depth = false)
        {
            var r = new Rig();
            Func<long> clock = () => Volatile.Read(ref r.Now);
            var price = new ReferencePriceFeedAdapter(capacityPow2: 64, nowMicros: clock);
            var board = new LockFreeBrokerSwitchboard(2, initialActive: 0);
            var pending = new PendingOrderCascadeCanceller(64);
            var sentinel = new BrokerFailoverSentinel(board, pendingOrders: pending);
            r.Router = new ResilientOrderRouter(board, sentinel, pending, idMapCapacityPow2: 256, maxSlippageBps: 50);
            r.Exec = new OrderExecutionFeedAdapter(r.Router, execCapacity, 120_000_000, clock);
            var breaker = new FeedDisconnectCircuitBreaker(price, () => true);
            r.Depth = new DepthBook(64, nowMicros: clock);
            r.Gate = new RouterFeedGate(r.Router, price, r.Exec, breaker, depth: depth ? r.Depth : null, requireDepthForMarket: false);
            price.OnTick(ReferencePriceFeedAdapter.SymbolHashForCode(5930), 70_000, 1);
            breaker.Evaluate();
            r.Kiwoom = new SplitBroker("kiwoom", 117057);
            r.Kis = new SplitBroker("kis", KisOrderIdSpace.Encode(5000));
            r.Ring = new OrderDispatchRing(64);
            var disp = new GatedOrderDispatcher(r.Ring, () => true);
            r.Worker = new OrderDrainWorker(disp, r.Kiwoom, wired: false, routeGate: r.Gate, brokersByIndex: new IBroker?[] { r.Kiwoom, r.Kis });
            r.Gate.EnableSplit(new FixedFeed(capKiwoom, capKis), r.Matrix_TripExternalLater);
            r.Matrix = new ChildSlotTrackingMatrix(16, clock);
            r.Mask = new ArmingInterlockBitmask(ArmingInterlockSource.ChildSlotRejection);
            r.Matrix.BindInterlock(r.Mask, ArmingInterlockSource.ChildSlotRejection);
            r.Worker.EnableChildSplit(r.Matrix);
            return r;
        }

        private static void Submit(Rig r, long shares, long price = 70_000)
        {
            r.Ring.Publish(0, 5930, OrderSide.Buy, 1, new QPrice(price * S), new QQty(shares * S), 0);
            r.Worker.DrainOnce();
        }

        [Fact]
        public void Drain_SplitsAcrossBrokers_ChildTrackingSlots_ConvergesOnFills()
        {
            var r = Make(capKiwoom: 6, capKis: 4);
            Submit(r, 10);
            Assert.Equal((1, 6L), (r.Kiwoom.Places, r.Kiwoom.Shares));
            Assert.Equal((1, 4L), (r.Kis.Places, r.Kis.Shares));
            Assert.Equal(2, r.Exec.Tracked);                                       // ★자식별 추적 슬롯
            Assert.Equal(2, r.Router.TrackedOrders);                               // ★자식별 128비트 라우터 키
            Assert.Equal((1L, 2L), (r.Worker.SplitParents, r.Worker.ChildOrdersSent));
            Assert.Equal(1, r.Gate.SplitAdmitted);
            r.Exec.OnFill(Fill(117057, 6));
            r.Exec.OnFill(Fill(KisOrderIdSpace.Encode(5000), 4));
            Assert.Equal(0, r.Exec.Tracked);
            r.Matrix.OnFill(Fill(117057, 6));
            r.Matrix.OnFill(Fill(KisOrderIdSpace.Encode(5000), 4));
            Assert.Equal(1, r.Matrix.ReleaseConverged());                          // AllFilled → 반납
            Assert.False(r.Mask.IsRaised(ArmingInterlockSource.ChildSlotRejection));
        }

        [Fact]
        public void Drain_CapsBelowParent_SendsOnlyCaps_NoOverDispatch()
        {
            var r = Make(capKiwoom: 3, capKis: 2);
            Submit(r, 10);
            Assert.Equal(5, r.Kiwoom.Shares + r.Kis.Shares);                      // ★한도 합만큼만 — 부모 10 미만 · 초과 0
        }

        [Fact]
        public void Drain_DepthLevels_ChildLimitsAtLevelPrices()
        {
            var r = Make(capKiwoom: 100, capKis: 100, depth: true);
            var d = Asks((69_900, 4), (70_000, 6));                                            // [v12.18.42] 실 KRX 격자(100원 구간)
            r.Depth.OnDepth(ReferencePriceFeedAdapter.SymbolHashForCode(5930), in d);
            Submit(r, 20, 70_000);
            Assert.Equal(10, r.Kiwoom.Shares + r.Kis.Shares);                     // 1~2호가 잔량 10 만 송출
            foreach (var px in r.Kiwoom.Prices) Assert.True(px <= 70_000m);
            foreach (var px in r.Kis.Prices) Assert.True(px <= 70_000m);
        }

        [Fact]
        public void Drain_ChildRejected_UnsentSiblingsReclaimed_Disarmed_NoFurtherSends()
        {
            var r = Make(capKiwoom: 100, capKis: 100, depth: true);
            var d = Asks((69_900, 10), (70_000, 10), (70_100, 10));                             // [v12.18.42] 실 KRX 격자(100원 구간)
            r.Depth.OnDepth(ReferencePriceFeedAdapter.SymbolHashForCode(5930), in d);
            r.Kis.RejectFrom = 1;                                                  // KIS 첫 자식부터 거부
            Submit(r, 30, 70_100);                                                 // 3단계 × 2브로커 = 자식 6(순서: L0 키움, L0 KIS, …)
            Assert.Equal(1, r.Kiwoom.Places);                                      // L0 키움만 송출
            Assert.Equal(1, r.Kis.Places);                                         // L0 KIS 거부 → 이후 4자식 미송출
            Assert.True(r.Mask.IsRaised(ArmingInterlockSource.ChildSlotRejection)); // ★Disarm
            Assert.Equal(4, r.Worker.ChildReclaimed);                              // ★미송출 형제 회수(라우터 키 · 추적 슬롯 반납)
            Assert.Equal(1, r.Exec.Tracked);                                       // 접수된 L0 키움 자식만 추적
            Assert.Equal(1, r.Router.TrackedOrders);
            Assert.Equal(4, r.Matrix.Reclaimed);
        }

        [Fact]
        public void Gate_TrackFailureMidChildren_AtomicRollback_NothingSent()
        {
            var r = Make(capKiwoom: 5, capKis: 5, execCapacity: 1);                // 추적 슬롯 1개 — 두 번째 자식 확보 실패
            Submit(r, 10);
            Assert.Equal((0, 0), (r.Kiwoom.Places, r.Kis.Places));                // 송출 0
            Assert.Equal(0, r.Exec.Tracked);                                       // ★부분 확보 0
            Assert.Equal(0, r.Router.TrackedOrders);
            Assert.Equal(1, r.Gate.SplitRollbacks);
        }

        [Fact]
        public void InterlockBit23_Defined()
            => Assert.Equal(23, (int)ArmingInterlockSource.ChildSlotRejection);
    }
}
