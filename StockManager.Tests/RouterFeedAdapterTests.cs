// ============================================================================
// RouterFeedAdapterTests — [피드 연동] ResilientOrderRouter 참조가 피드 · 체결/취소 통보 피드 · 슬롯 수명 샌니타이저 · 피드 단절 회로
//
//  §1 참조가 피드 — 게이트웨이 ITickSink 결선 · 종목 키 정합 · 신선/만료/미수신 · 스케일 오버플로 폐기 · 라우터 전방 거부.
//  §2 체결/취소 대사 — 부분 체결 누적(조기 해제 금지) · 완전 체결 · 초과 체결 · 캐스케이드 취소 확인 · 취소 후 체결 · 미상 번호 ·
//     체결량 0 모호 통보 · 송출 미도달 롤백 · 브로커 번호 파싱 실패 · 소수 수량 · 원장 포화.
//  §3 [방향 A] 슬롯 수명 샌니타이저 — TTL 회수(슬롯 영구 고착 0) · sticky 차단 · 늦은 통보 · 이중키 재무장.
//  §4 [방향 B] 피드 단절 회로 — 기동 레벨 차단 · 단절 래치 · 복구 후에도 유지 · 재무장 · 집계 비트.
//  §5 E2E — 링 → 디스패처 → OrderDrainWorker(routeGate) → IBroker · 미송출 롤백 · 호스트 IL 결선.
//  §6 δ=0 — 틱 사상 · 참조가 판독 · 송출 수명주기(Admit→Accept→Fill) · 스윕 · 회로 평가.
// ============================================================================
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using MS2E.Core;
using MS2E.Infrastructure;
using StockManager.Adapters;
using Xunit;
using static StockManager.Tests.ZeroAllocProbe;

namespace StockManager.Tests
{
    internal sealed class FeedSpyBroker : IBroker
    {
        public int Calls;
        public bool Reject;
        public string NextId = "1001";
        public string Name => "FeedSpy";
        public bool IsLive => false;
        public (bool ok, string msg) Place(PaperAsset asset, string symbol, string name, PaperSide side, PaperKind kind,
                                           decimal qty, decimal limitPrice, decimal currentPrice)
        {
            Interlocked.Increment(ref Calls);
            return Reject ? (false, "rejected") : (true, NextId);
        }
        public void Cancel(string orderId) { }
        public decimal HeldQty(PaperAsset asset, string symbol) => 0m;
    }

    [Collection("ConcurrencyStress")]
    public sealed class RouterFeedAdapterTests
    {
        private const long Scale = 100_000_000L;
        private const long Code = 5930;
        private static readonly long Sym = ReferencePriceFeedAdapter.SymbolHashForCode(Code);

        private sealed class Rig
        {
            public long Now = 1_000_000;
            public bool Alive = true;
            public readonly List<ReconciliationRecord> Records = new();
            public ReferencePriceFeedAdapter Price = null!;
            public LockFreeBrokerSwitchboard Board = null!;
            public PendingOrderCascadeCanceller Pending = null!;
            public BrokerFailoverSentinel Sentinel = null!;
            public ResilientOrderRouter Router = null!;
            public OrderExecutionFeedAdapter Exec = null!;
            public FeedDisconnectCircuitBreaker Breaker = null!;
            public RouterFeedGate Gate = null!;
        }

        private static Rig Make(int pendingCap = 64, int execCap = 64, long ttlMicros = 10_000_000, long maxSlipBps = 50, bool requireDepth = true)
        {
            var r = new Rig();
            Func<long> clock = () => Volatile.Read(ref r.Now);
            r.Price = new ReferencePriceFeedAdapter(capacityPow2: 64, maxAgeMicros: 3_000_000, nowMicros: clock);
            r.Board = new LockFreeBrokerSwitchboard(2);
            r.Pending = new PendingOrderCascadeCanceller(pendingCap);
            r.Sentinel = new BrokerFailoverSentinel(r.Board, pendingOrders: r.Pending);
            r.Router = new ResilientOrderRouter(r.Board, r.Sentinel, r.Pending, idMapCapacityPow2: 256, maxSlippageBps: maxSlipBps);
            r.Exec = new OrderExecutionFeedAdapter(r.Router, execCap, ttlMicros, clock, rec => r.Records.Add(rec));
            r.Breaker = new FeedDisconnectCircuitBreaker(r.Price, () => r.Alive, maxPriceSilenceMicros: 5_000_000);
            r.Gate = new RouterFeedGate(r.Router, r.Price, r.Exec, r.Breaker, requireDepthForMarket: requireDepth);
            return r;
        }

        private static OrderProposal P(long id, byte kind = 1, decimal price = 70_000m, decimal qty = 10m, long code = Code)
            => new(id, 0, code, OrderSide.Buy, kind, QPrice.Of(price), QQty.Of(qty), 0);

        private static FillPayload Fill(long brokerOrder, long qty) => new() { OrderId = brokerOrder, SymbolCode = Code, FillPrice = 70_000, FillQty = qty, Side = 1 };

        private static void Tick(Rig r, long price = 70_000, long seq = 1) => r.Price.OnTick(Sym, price, seq);

        // ════════════ §1 참조가 피드 ════════════

        [Fact]
        public void SymbolKey_MatchesGatewayHash()
        {
            Assert.Equal(TossWebSocketMarketDataGateway.FnvSpan("005930"u8), Sym);
            Assert.Equal(0, ReferencePriceFeedAdapter.SymbolHashForCode(-1));
            Assert.Equal(0, ReferencePriceFeedAdapter.SymbolHashForCode(1_000_000));
        }

        [Fact]
        public void Gateway_AcceptedTicks_FlowIntoReferenceTable_RejectedTicksDoNot()
        {
            var r = Make();
            var gw = new TossWebSocketMarketDataGateway(new SequenceContinuityGuardian(), tickSink: r.Price);
            Assert.True(gw.IngestPacket("{\"seq\":1,\"symbol\":\"005930\",\"price\":70000,\"qty\":3}"u8, 0));
            Assert.Equal(ReferencePriceVerdict.Fresh, r.Price.TryGetReference(Sym, out long px, out _));
            Assert.Equal(70_000 * Scale, px);
            Assert.False(gw.IngestPacket("{\"seq\":1,\"symbol\":\"005930\",\"price\":99999}"u8, 0));   // 역순 seq — 미반영
            Assert.False(gw.IngestPacket("{\"seq\":2,\"symbol\":\"005930\",\"price\":0}"u8, 0));       // 가격 0 — 미반영
            r.Price.TryGetReference(Sym, out px, out _);
            Assert.Equal(70_000 * Scale, px);
            Assert.Equal(1, r.Price.Ticks);
        }

        [Fact]
        public void Reference_Fresh_Stale_Missing_Overflow()
        {
            var r = Make();
            Assert.Equal(ReferencePriceVerdict.Missing, r.Price.TryGetReference(Sym, out _, out _));
            Assert.False(r.Price.HasTicks);
            Tick(r);
            Assert.Equal(ReferencePriceVerdict.Fresh, r.Price.TryGetReference(Sym, out _, out long age));
            Assert.Equal(0, age);
            r.Now += 3_000_001;
            Assert.Equal(ReferencePriceVerdict.Stale, r.Price.TryGetReference(Sym, out long stalePx, out _));
            Assert.Equal(0, stalePx);                                         // 만료 가격은 반환하지 않음
            r.Price.OnTick(Sym, long.MaxValue / 2, 2);                        // × 10^8 오버플로
            Assert.Equal(1, r.Price.OverflowDropped);
            Assert.Equal(ReferencePriceVerdict.Missing, r.Price.TryGetReference(ReferencePriceFeedAdapter.SymbolHashForCode(660), out _, out _));
        }

        [Fact]
        public void Gate_NoReference_RejectsMarketAndLimit_NoTracking()
        {
            var r = Make();
            r.Breaker.Evaluate();
            Tick(r, seq: 1);                                                  // 회로 정상화용(다른 종목 아님 — 같은 종목)
            r.Breaker.Evaluate();
            var rOther = P(1, code: 660);                                     // 참조가 없는 종목
            Assert.False(r.Gate.TryAdmit(in rOther));
            Assert.Equal(OrderRouteVerdict.StaleReference, r.Gate.LastVerdict);
            r.Now += 3_000_001;                                               // 같은 종목 참조가 만료(회로는 침묵 5초 전이라 정상)
            var market = P(2, kind: 0, price: 0m);
            Assert.False(r.Gate.TryAdmit(in market));
            Assert.Equal(OrderRouteVerdict.StaleReference, r.Gate.LastVerdict);
            Assert.Equal(0, r.Router.TrackedOrders);
            Assert.Equal(0, r.Exec.Tracked);
            Assert.Equal(2, r.Router.Rejected);
        }

        [Fact]
        public void Gate_MarketOrder_WithFreshReference_Admitted()
        {
            // [수정 전 Red] 라우터 직결 시 시장가(limit 0)는 InvalidPrice 로 전량 거부됐다 — 이제 신선 참조가 기반 보호 지정가로 판정.
            //   (깊이 피드 미결선 상태에서 시장가 허용은 명시 옵트아웃 requireDepth:false 일 때만 — 기본은 거부.)
            var r = Make(requireDepth: false);
            Tick(r); r.Breaker.Evaluate();
            Assert.Equal(OrderRouteVerdict.InvalidPrice, r.Router.Route(RouterFeedGate.KeyFor(99), 0, 0, 0.0).Verdict);
            var market = P(1, kind: 0, price: 0m);
            Assert.True(r.Gate.TryAdmit(in market, out var adm));
            Assert.Equal(1, adm.KindByte);                                    // ★송출은 지정가
            Assert.Equal(70_200 * Scale, adm.PriceRaw);                       // 70,000 × 1.003 = 70,210 → 100원 단위 내림
            Assert.Equal(1, r.Router.TrackedOrders);
            Assert.Equal(1, r.Exec.Tracked);
        }

        [Fact]
        public void Gate_LimitBeyondSlippage_Rejected()
        {
            var r = Make(maxSlipBps: 50);
            Tick(r); r.Breaker.Evaluate();
            var far = P(1, price: 71_000m);                                   // +142bps
            Assert.False(r.Gate.TryAdmit(in far));
            Assert.Equal(OrderRouteVerdict.SlippageExceeded, r.Gate.LastVerdict);
            var near = P(2, price: 70_200m);                                  // +28bps
            Assert.True(r.Gate.TryAdmit(in near));
        }

        // ════════════ §2 체결/취소 대사 ════════════

        private static Rig Admitted(long proposalId = 1, string brokerId = "17", decimal qty = 10m)
        {
            var r = Make();
            Tick(r); r.Breaker.Evaluate();
            var p = P(proposalId, qty: qty);
            Assert.True(r.Gate.TryAdmit(in p));
            r.Gate.OnBrokerAccepted(proposalId, brokerId);
            return r;
        }

        [Fact]
        public void PartialFills_KeepSlot_UntilComplete()
        {
            var r = Admitted();
            r.Exec.OnFill(Fill(17, 3));
            Assert.Equal(1, r.Router.TrackedOrders);                          // ★부분 체결로 조기 해제 금지
            Assert.Equal(1, r.Exec.PartialFills);
            r.Exec.OnFill(Fill(17, 7));
            Assert.Equal(0, r.Router.TrackedOrders);
            Assert.Equal(0, r.Exec.Tracked);
            Assert.Empty(r.Records);                                          // 정상 완전 체결은 이상 대사 아님
        }

        [Fact]
        public void Overfill_Reconciled_And_Released()
        {
            var r = Admitted();
            r.Exec.OnFill(Fill(17, 12));
            Assert.Equal(0, r.Router.TrackedOrders);
            var rec = Assert.Single(r.Records);
            Assert.Equal(ReconciliationKind.Overfill, rec.Kind);
            Assert.Equal(12, rec.Qty);
        }

        [Fact]
        public void CascadeCancel_ZeroQtyNotice_ConfirmsCancel_FillAfterCancel_Reconciled()
        {
            var r = Admitted(1, "17");
            var p2 = P(2); Assert.True(r.Gate.TryAdmit(in p2)); r.Gate.OnBrokerAccepted(2, "18");
            Assert.Equal(2, r.Pending.CancelAllForBroker(0));                // 페일오버 캐스케이드
            r.Exec.OnFill(Fill(17, 0));                                       // 취소 확인(체결량 0)
            r.Exec.OnFill(Fill(18, 10));                                      // 취소 경합 실체결
            Assert.Equal(0, r.Router.TrackedOrders);
            Assert.Equal(new[] { ReconciliationKind.CancelConfirmed, ReconciliationKind.FilledAfterCancel }, r.Records.ConvertAll(x => x.Kind));
            Assert.Equal(1, r.Pending.LateFillsAfterCancel);
        }

        [Fact]
        public void ZeroQtyNotice_WithoutCancel_IsAmbiguous_SlotKept()
        {
            var r = Admitted();
            r.Exec.OnFill(Fill(17, 0));                                       // 접수/거부 구분 불가 → 유지
            Assert.Equal(1, r.Router.TrackedOrders);
            Assert.Equal(1, r.Exec.ZeroQtyIgnored);
        }

        [Fact]
        public void UnknownBrokerOrder_Reconciled()
        {
            var r = Admitted();
            r.Exec.OnFill(Fill(999, 5));
            Assert.Equal(ReconciliationKind.UnknownOrder, Assert.Single(r.Records).Kind);
            Assert.Equal(1, r.Router.TrackedOrders);
        }

        [Fact]
        public void NotSent_RollsBack_OnlyBeforeAcceptance()
        {
            var r = Make();
            Tick(r); r.Breaker.Evaluate();
            var p = P(1);
            Assert.True(r.Gate.TryAdmit(in p));
            r.Gate.OnNotSent(1);                                              // 브로커 미도달 → 롤백
            Assert.Equal(0, r.Router.TrackedOrders);
            Assert.Equal(1, r.Router.AbortedRoutes);
            Assert.Equal(1, r.Gate.NotSentRollbacks);

            var p2 = P(2);
            Assert.True(r.Gate.TryAdmit(in p2));
            r.Gate.OnBrokerAccepted(2, "55");
            r.Gate.OnNotSent(2);                                              // 접수된 주문은 롤백 금지(실 노출)
            Assert.Equal(1, r.Router.TrackedOrders);
        }

        [Fact]
        public void UnparsedBrokerId_KeepsTracking_Reconciled()
        {
            var r = Make();
            Tick(r); r.Breaker.Evaluate();
            var p = P(1);
            Assert.True(r.Gate.TryAdmit(in p));
            r.Gate.OnBrokerAccepted(1, "ORD-X");
            Assert.Equal(ReconciliationKind.BrokerIdUnparsed, Assert.Single(r.Records).Kind);
            Assert.Equal(1, r.Router.TrackedOrders);                          // 매핑 불가분은 TTL 회수 대상
        }

        [Fact]
        public void FractionalQty_And_LedgerFull_AbortRouterSlot()
        {
            var r = Make(execCap: 1);
            Tick(r); r.Breaker.Evaluate();
            var frac = P(1, qty: 0.5m);
            Assert.False(r.Gate.TryAdmit(in frac));
            Assert.Equal(1, r.Gate.FractionalRejects);
            var a = P(2); Assert.True(r.Gate.TryAdmit(in a));
            var b = P(3); Assert.False(r.Gate.TryAdmit(in b));                // 원장 포화
            Assert.Equal(1, r.Gate.TrackRejects);
            Assert.Equal(1, r.Router.TrackedOrders);                          // 롤백으로 라우터 슬롯 누수 0
            Assert.Equal(1, r.Router.AbortedRoutes);                          // 소수 수량은 슬롯 확보 전 거부(부작용 0) · 원장 포화만 Abort
        }

        // ════════════ §3 [방향 A] 슬롯 수명 샌니타이저 ════════════

        [Fact]
        public void Sweep_ReclaimsExpired_StickyBlock_LateFillUnknown_Rearm()
        {
            var r = Admitted();
            var mask = new ArmingInterlockBitmask(ArmingInterlockSource.OrderSlotAge);
            r.Exec.BindInterlock(mask, ArmingInterlockSource.OrderSlotAge);
            Assert.True(mask.Seal(out _));
            Assert.Equal(0, r.Exec.SweepExpired());                           // TTL 이내
            Assert.False(mask.Blocked);
            r.Now += 10_000_001;
            Assert.Equal(1, r.Exec.SweepExpired());
            Assert.Equal(0, r.Router.TrackedOrders);
            Assert.Equal(1, r.Router.ReclaimedSlots);
            Assert.True(r.Exec.ArmingBlocked);
            Assert.True(mask.Blocked);                                        // ★미확인 노출 → 무장 차단
            Assert.Equal(ReconciliationKind.Expired, r.Records[0].Kind);
            r.Exec.OnFill(Fill(17, 10));                                      // 회수 뒤 늦은 체결 통보
            Assert.Contains(r.Records, x => x.Kind == ReconciliationKind.UnknownOrder);
            Assert.False(r.Exec.TryRearm(true, false));
            Assert.True(r.Exec.ArmingBlocked);
            Assert.True(r.Exec.TryRearm(true, true));
            Assert.False(mask.Blocked);
        }

        [Fact]
        public void Sweep_ClassifiesAcknowledged_And_Cancelling()
        {
            var r = Admitted(1, "17");
            var p2 = P(2); Assert.True(r.Gate.TryAdmit(in p2)); r.Gate.OnBrokerAccepted(2, "18");
            r.Exec.OnFill(Fill(17, 0));                                       // 접수/거부 모호 통보
            r.Pending.CancelAllForBroker(0);                                  // 둘 다 취소 전환 → 17 은 이미 acked
            r.Now += 10_000_001;
            Assert.Equal(2, r.Exec.SweepExpired());
            Assert.All(r.Records, x => Assert.Equal(ReconciliationKind.ExpiredWhileCancelling, x.Kind));
        }

        [Fact]
        public void NoSlotDeadlock_LostNotices_CapacityRecovered()
        {
            // [수정 전 Red] 통보가 안 오면 라우터 추적 슬롯이 영구 점유 → TrackingFull 로 이후 전 주문 거부(교착).
            var r = Make(pendingCap: 4);
            Tick(r); r.Breaker.Evaluate();
            for (long id = 1; id <= 4; id++) { var p = P(id); Assert.True(r.Gate.TryAdmit(in p)); r.Gate.OnBrokerAccepted(id, (100 + id).ToString()); }
            var blocked = P(5);
            Assert.False(r.Gate.TryAdmit(in blocked));
            Assert.Equal(OrderRouteVerdict.TrackingFull, r.Gate.LastVerdict);
            r.Now += 10_000_001;
            Tick(r, seq: 2); r.Breaker.Evaluate();
            Assert.Equal(4, r.Exec.SweepExpired());
            var after = P(6);
            Assert.True(r.Gate.TryAdmit(in after));                           // 용량 회복
        }

        // ════════════ §4 [방향 B] 피드 단절 회로 ════════════

        [Fact]
        public void Breaker_BootLevelBlock_ThenHealthy_ThenDropLatches_SurvivesRecovery()
        {
            var r = Make();
            var mask = new ArmingInterlockBitmask(ArmingInterlockSource.RouterFeed);
            r.Breaker.BindInterlock(mask, ArmingInterlockSource.RouterFeed);
            Assert.True(mask.Seal(out _));
            Assert.True(r.Breaker.Evaluate());                                // 기동 직후 피드 미수립 = 차단(래치 아님)
            Assert.False(r.Breaker.Tripped);
            Tick(r);
            Assert.False(r.Breaker.Evaluate());
            Assert.False(mask.Blocked);

            r.Now += 5_000_001;                                               // 시세 침묵
            Assert.True(r.Breaker.Evaluate());
            Assert.True(r.Breaker.Tripped);
            Assert.True(mask.Blocked);
            Tick(r, seq: 2);                                                  // 피드 복구
            Assert.True(r.Breaker.Evaluate());                                // ★래치 유지
            var p = P(1);
            Assert.False(r.Gate.TryAdmit(in p));
            Assert.Equal(OrderRouteVerdict.FeedDisconnected, r.Gate.LastVerdict);
            Assert.False(r.Breaker.TryRearm(false, true));
            Assert.True(r.Breaker.TryRearm(true, true));
            Assert.False(mask.Blocked);
            Assert.True(r.Gate.TryAdmit(in p));
        }

        [Fact]
        public void Breaker_ExecFeedDead_Trips_ProbeFaultIsDead()
        {
            var r = Make();
            Tick(r);
            Assert.False(r.Breaker.Evaluate());
            r.Alive = false;
            Assert.True(r.Breaker.Evaluate());
            Assert.True(r.Breaker.Tripped);
            Assert.Equal(1, r.Breaker.Trips);

            var price = new ReferencePriceFeedAdapter(capacityPow2: 8, nowMicros: () => 1);
            price.OnTick(Sym, 1, 1);
            var faulty = new FeedDisconnectCircuitBreaker(price, () => throw new InvalidOperationException("probe"));
            Assert.True(faulty.Evaluate());
        }

        // ════════════ §5 E2E ════════════

        [Fact]
        public void E2E_DrainWorker_RouteGate_SendsOnlyAdmitted_RollsBackRejected()
        {
            var r = Make();
            Tick(r); r.Breaker.Evaluate();
            var ring = new OrderDispatchRing(64);
            ring.Publish(0, Code, OrderSide.Buy, 1, QPrice.Of(70_000m), QQty.Of(10m), 1);    // 통과
            ring.Publish(0, 660, OrderSide.Buy, 1, QPrice.Of(10_000m), QQty.Of(1m), 2);      // 참조가 없음 → 미송출
            var broker = new FeedSpyBroker { NextId = "4242" };
            var disp = new GatedOrderDispatcher(ring, isArmed: () => true, new OrderAttestationService());
            using var w = new OrderDrainWorker(disp, broker, wired: false, routeGate: r.Gate);
            w.DrainOnce();
            Assert.Equal(1, broker.Calls);
            Assert.Equal(1, w.RouteVetoes);
            Assert.Equal(1, r.Exec.Tracked);
            r.Exec.OnFill(Fill(4242, 10));                                     // 브로커 번호로 체결 대사 → 해제
            Assert.Equal(0, r.Router.TrackedOrders);

            var ring2 = new OrderDispatchRing(64);
            ring2.Publish(0, Code, OrderSide.Buy, 1, QPrice.Of(70_000m), QQty.Of(5m), 3);
            var rejecting = new FeedSpyBroker { Reject = true };
            var att = new OrderAttestationService();
            var disp2 = new GatedOrderDispatcher(ring2, isArmed: () => true, att);
            using var w2 = new OrderDrainWorker(disp2, rejecting, wired: false, attestation: att, routeGate: r.Gate);
            w2.DrainOnce();                                                    // Place ok=false → 롤백 후 Mission-A 동결
            Assert.Equal(0, r.Router.TrackedOrders);                           // ★슬롯 누수 0
            Assert.Equal(1, r.Router.AbortedRoutes);
        }

        private static bool HostCalls(MethodBase target)
        {
            const BindingFlags AllF = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
            var host = typeof(StockManager.Blazor.BlazorHomeForm);
            var types = new List<Type> { host };
            types.AddRange(host.GetNestedTypes(BindingFlags.NonPublic | BindingFlags.Public));
            foreach (var t in types)
            {
                var all = new List<MethodBase>(t.GetMethods(AllF));
                all.AddRange(t.GetConstructors(AllF));
                foreach (var m in all)
                {
                    var il = m.GetMethodBody()?.GetILAsByteArray();
                    if (il is null) continue;
                    for (int i = 0; i + 4 < il.Length; i++)
                        if (il[i] is 0x28 or 0x6F or 0x73 or 0x06)   // call/callvirt/newobj/ldftn(0xFE06 하위 바이트)
                            try { if (m.Module.ResolveMethod(BitConverter.ToInt32(il, i + 1)) == target) return true; } catch (ArgumentException) { }
                }
            }
            return false;
        }

        [Fact]
        public void Host_WiresRouterFeeds_IntoPipeline()
        {
            Assert.True(HostCalls(typeof(RouterFeedGate).GetConstructors()[0]), "RouterFeedGate 미생성");
            Assert.True(HostCalls(typeof(OrderExecutionFeedAdapter).GetMethod(nameof(OrderExecutionFeedAdapter.SweepExpired))!), "슬롯 샌니타이저 주기 호출 없음");
            Assert.True(HostCalls(typeof(OrderExecutionFeedAdapter).GetMethod(nameof(OrderExecutionFeedAdapter.OnFill))!), "체결 피드 구독 없음");
            Assert.True(HostCalls(typeof(FeedDisconnectCircuitBreaker).GetMethod(nameof(FeedDisconnectCircuitBreaker.BindInterlock))!), "회로 차단기 집계 비트 미결속");
            var gwCtor = typeof(TossWebSocketMarketDataGateway).GetConstructors()[0];
            Assert.Contains(gwCtor.GetParameters(), p => p.ParameterType == typeof(ITickSink));
            var wCtor = typeof(OrderDrainWorker).GetConstructors()[0];
            Assert.Contains(wCtor.GetParameters(), p => p.ParameterType == typeof(IOrderRouteGate));
        }

        // ════════════ §6 δ=0 ════════════

        [Fact]
        public void HotPaths_ZeroAlloc()
        {
            var r = Make(pendingCap: 256, execCap: 256, ttlMicros: long.MaxValue / 4);
            Tick(r); r.Breaker.Evaluate();
            var ids = new string[2_000];
            for (int i = 0; i < ids.Length; i++) ids[i] = (5000 + i).ToString();
            long sink = 0;
            // 예열
            var warm = P(1); r.Gate.TryAdmit(in warm); r.Gate.OnBrokerAccepted(1, ids[0]); r.Exec.OnFill(Fill(5000, 10));
            r.Exec.SweepExpired();

            Assert.Equal(0L, MinAllocated(() =>
            {
                for (int i = 0; i < 1_000; i++)
                {
                    r.Price.OnTick(Sym, 70_000 + (i & 7), 10 + i);
                    if (r.Price.TryGetReference(Sym, out long px, out _) == ReferencePriceVerdict.Fresh) sink += px;
                }
            }));

            long next = 10;
            Assert.Equal(0L, MinAllocated(() =>
            {
                for (int i = 1; i < 500; i++)
                {
                    long id = ++next;
                    var p = P(id);
                    if (r.Gate.TryAdmit(in p))
                    {
                        r.Gate.OnBrokerAccepted(id, ids[i]);
                        r.Exec.OnFill(Fill(5000 + i, 4));
                        r.Exec.OnFill(Fill(5000 + i, 6));
                        sink++;
                    }
                }
                sink += r.Exec.SweepExpired();
                sink += r.Breaker.Evaluate() ? 1 : 0;
            }));
            Assert.True(sink > 0);
            Assert.Equal(0, r.Router.TrackedOrders);
        }
    }
}
