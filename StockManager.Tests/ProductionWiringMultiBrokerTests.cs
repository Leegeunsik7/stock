// ============================================================================
// ProductionWiringMultiBrokerTests — [실전 결선 · 다중 브로커 한도 피드 · 추천 A]
//
//  §1 BrokerLimitFeedProvider — 매수 여유 → 주(부모 예약 되돌림) · 매도 보유 · 주문당 상한 · 미결속/만료 = 자식 0 · 소진 격리(전이 1회)·회복 · δ=0.
//  §2 게이트 + 드레인 — 분할 가능 브로커 0 = 전방 거부 + 비트 23 + 송출 0 + 계좌 예약 반환 · 키움 소진 → KIS 우회 · 전 소진 → Disarm ·
//     ★실 계좌 예약 이중 차감 방지(부모 명목 = 주문가능금액 전액이어도 분할 성립).
//  §3 게이트웨이 자식 봉인 — 자식 전용 exactly-once 래치(일반 주문 슬롯 비오염) · 이중키 veto · 자식 ID 공간 밖 거부 ·
//     게이트웨이 경유 분할 송출 · 첫 자식 veto = 포기(Disarm 없음 · 추적 롤백).
//  §4 호스트 조합 뿌리 결선(소스/IL) · 오중 빌드 회귀.
// ============================================================================
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using MS2E.Core;
using MS2E.Infrastructure;
using StockManager.Adapters;
using StockManager.Engines;
using Xunit;

namespace StockManager.Tests
{
    [Collection("ConcurrencyStress")]
    public sealed class ProductionWiringMultiBrokerTests
    {
        private const long S = 100_000_000L;
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

        private sealed class FakeView : IBrokerAccountView
        {
            public bool BoundFlag = true, Fresh = true, Shares;
            public long Headroom, Sellable;
            public bool Bound => BoundFlag;
            public bool SharesGateReservation => Shares;
            public bool TryGetBuyHeadroomWon(out long won) { won = Headroom; return Fresh; }
            public bool TryGetSellableShares(long symbolCode, out long shares) { shares = Sellable; return Fresh; }
        }

        // ════════════ §1 한도 피드 ════════════

        [Fact]
        public void LimitFeed_BuyHeadroomToShares_ReservationAddBack_Sell_PerOrderCap_ZeroAlloc()
        {
            var kiwoom = new FakeView { Headroom = 700_000, Shares = true };
            var kis = new FakeView { Headroom = 140_000, Sellable = 7 };
            var feed = new BrokerLimitFeedProvider(new IBrokerAccountView?[] { kiwoom, kis }, new long[] { 0, 1 });
            var dst = new BrokerSplitCap[4];
            Assert.Equal(2, feed.Fill(dst, 5930, OrderSide.Buy, 70_000 * S, parentReservedWon: 700_000));
            Assert.Equal((true, 20L), (dst[0].Available, dst[0].MaxShares));      // ★(700,000 + 부모 예약 700,000) / 70,000 = 20(같은 계좌만 되돌림)
            Assert.Equal((true, 1L), (dst[1].Available, dst[1].MaxShares));       // 140,000/70,000 = 2 → 주문당 상한 1
            feed.Fill(dst, 5930, OrderSide.Sell, 70_000 * S, 0);
            Assert.Equal((false, true, 1L), (dst[0].Available, dst[1].Available, dst[1].MaxShares));   // 키움 보유 0 = 소진 · KIS 보유 7 → 상한 1
            Assert.Equal(0, MinAllocated(() => { for (int i = 0; i < 1000; i++) feed.Fill(dst, 5930, OrderSide.Buy, 70_000 * S, 700_000); }));
        }

        [Fact]
        public void LimitFeed_UnboundStale_ZeroChildren_Exhaustion_IsolatesOnce_Recovers()
        {
            var a = new FakeView { Headroom = 1_000_000 };
            var b = new FakeView { Headroom = 1_000_000 };
            var feed = new BrokerLimitFeedProvider(new IBrokerAccountView?[] { a, b, null });
            var dst = new BrokerSplitCap[3];
            a.BoundFlag = false; b.Fresh = false;
            feed.Fill(dst, 5930, OrderSide.Buy, 70_000 * S, 0);
            Assert.Equal((false, false, false), (dst[0].Available, dst[1].Available, dst[2].Available));   // 미결속 · 만료 · null
            Assert.Equal(0, feed.LastAvailable);
            Assert.Equal((2L, 1L), (feed.UnboundSamples, feed.StaleSamples));
            a.BoundFlag = true; b.Fresh = true; a.Headroom = 0;
            feed.Fill(dst, 5930, OrderSide.Buy, 70_000 * S, 0);
            feed.Fill(dst, 5930, OrderSide.Buy, 70_000 * S, 0);
            Assert.Equal(1L, feed.IsolatedMask);                                   // ★방향 B — 소진 브로커 격리
            Assert.Equal(1, feed.Exhaustions);                                     // 전이 1회 계수
            Assert.True(dst[1].Available);                                         // 잔여 한도 브로커로 우회
            a.Headroom = 500_000;
            feed.Fill(dst, 5930, OrderSide.Buy, 70_000 * S, 0);
            Assert.Equal(0L, feed.IsolatedMask);                                   // 회복 = 격리 해제
        }

        // ════════════ §2 게이트 + 드레인 ════════════

        private sealed class RecBroker : IBroker
        {
            public readonly string Tag; public long NextId; public int Places; public long Shares;
            public readonly List<string> Cancels = new();
            public RecBroker(string tag, long firstId) { Tag = tag; NextId = firstId; }
            public string Name => Tag;
            public bool IsLive => false;
            public (bool ok, string msg) Place(PaperAsset asset, string symbol, string name, PaperSide side, PaperKind kind, decimal qty, decimal limitPrice, decimal currentPrice)
            { Places++; Shares += (long)qty; return (true, (NextId++).ToString()); }
            public void Cancel(string orderId) => Cancels.Add(orderId);
            public decimal HeldQty(PaperAsset asset, string symbol) => 0m;
        }

        private sealed class Rig
        {
            public long Now = 1_000_000;
            public bool Valve = true;
            public ResilientOrderRouter Router = null!;
            public OrderExecutionFeedAdapter Exec = null!;
            public RouterFeedGate Gate = null!;
            public OrderDispatchRing Ring = null!;
            public OrderDrainWorker Worker = null!;
            public RecBroker Kiwoom = null!, Kis = null!;
            public ChildSlotTrackingMatrix Matrix = null!;
            public ArmingInterlockBitmask Mask = null!;
            public AccountStateSyncAdapter Account = null!;
            public BrokerLimitFeedProvider Feed = null!;
            public GenesisLiveIgnitionGateway? Gateway;
        }

        private static Rig Make(IBrokerAccountView?[]? views = null, bool realAccount = false, bool gateway = false)
        {
            var r = new Rig();
            Func<long> clock = () => Volatile.Read(ref r.Now);
            var price = new ReferencePriceFeedAdapter(capacityPow2: 64, nowMicros: clock);
            var board = new LockFreeBrokerSwitchboard(2, initialActive: 0);
            var pending = new PendingOrderCascadeCanceller(64);
            var sentinel = new BrokerFailoverSentinel(board, pendingOrders: pending);
            r.Router = new ResilientOrderRouter(board, sentinel, pending, idMapCapacityPow2: 256, maxSlippageBps: 50);
            r.Exec = new OrderExecutionFeedAdapter(r.Router, 64, 120_000_000, clock);
            var breaker = new FeedDisconnectCircuitBreaker(price, () => true);
            r.Account = new AccountStateSyncAdapter(64, nowMicros: clock);
            r.Gate = new RouterFeedGate(r.Router, price, r.Exec, breaker, requireDepthForMarket: false, account: realAccount ? r.Account : null);
            price.OnTick(ReferencePriceFeedAdapter.SymbolHashForCode(5930), 70_000, 1);
            breaker.Evaluate();
            r.Kiwoom = new RecBroker("kiwoom", 117057);
            r.Kis = new RecBroker("kis", KisOrderIdSpace.Encode(5000));
            r.Matrix = new ChildSlotTrackingMatrix(16, clock);
            r.Mask = new ArmingInterlockBitmask(ArmingInterlockSource.ChildSlotRejection);
            r.Matrix.BindInterlock(r.Mask, ArmingInterlockSource.ChildSlotRejection);
            r.Feed = new BrokerLimitFeedProvider(views ?? new IBrokerAccountView?[] { new AccountSyncBrokerView(r.Account, () => true), null });
            r.Gate.EnableSplit(r.Feed, r.Matrix.TripExternal);
            r.Ring = new OrderDispatchRing(64);
            var disp = new GatedOrderDispatcher(r.Ring, () => true);
            if (gateway)
                r.Gateway = new GenesisLiveIgnitionGateway(() => r.Valve, () => r.Valve, () => true, sealedEmission: new SealedEmissionInterlock(8));
            r.Worker = new OrderDrainWorker(disp, r.Kiwoom, wired: false, routeGate: r.Gate, gateway: r.Gateway, brokersByIndex: new IBroker?[] { r.Kiwoom, r.Kis });
            r.Worker.EnableChildSplit(r.Matrix);
            return r;
        }

        private static void Submit(Rig r, long shares, long price = 70_000)
        {
            r.Ring.Publish(0, 5930, OrderSide.Buy, 1, new QPrice(price * S), new QQty(shares * S), 0);
            r.Worker.DrainOnce();
        }

        [Fact]
        public void Unbound_SplitRefused_Bit23Raised_ZeroSends_ReservationReleased()
        {
            var r = Make(new IBrokerAccountView?[] { null, null }, realAccount: true);
            r.Account.OnCash(new AccountCashPayload { OrderableCash = 10_000_000 });
            Submit(r, 10);
            Assert.Equal((0, 0), (r.Kiwoom.Places, r.Kis.Places));                // ★미결속 = 분할 주문 송출 0
            Assert.True(r.Mask.IsRaised(ArmingInterlockSource.ChildSlotRejection)); // ★비트 23
            Assert.Equal(1, r.Gate.SplitUnavailable);
            Assert.Equal(0, r.Exec.Tracked);
            Assert.Equal(0, r.Account.ReservedBuyNotional);                        // 부모 예약 반환
        }

        [Fact]
        public void KiwoomExhausted_FallsBackToKis_AllExhausted_Disarms()
        {
            var a = new FakeView { Headroom = 0 };
            var b = new FakeView { Headroom = 7_000_000 };
            var r = Make(new IBrokerAccountView?[] { a, b });
            Submit(r, 10);
            Assert.Equal((0, 1, 10L), (r.Kiwoom.Places, r.Kis.Places, r.Kis.Shares));   // ★소진 키움 격리 → KIS 로 안전 우회
            Assert.Equal(1L, r.Feed.IsolatedMask);
            Assert.False(r.Mask.IsRaised(ArmingInterlockSource.ChildSlotRejection));
            b.Headroom = 0;
            Submit(r, 10);
            Assert.Equal(1, r.Kis.Places);                                         // 추가 송출 0
            Assert.True(r.Mask.IsRaised(ArmingInterlockSource.ChildSlotRejection)); // ★전 브로커 소진 = Disarm
        }

        [Fact]
        public void RealAccount_ParentReservation_NotDoubleCounted()
        {
            var r = Make(realAccount: true);
            r.Account.OnCash(new AccountCashPayload { OrderableCash = 700_000 });   // 정확히 부모 명목(10주 × 70,000)
            Submit(r, 10);
            Assert.Equal((1, 10L), (r.Kiwoom.Places, r.Kiwoom.Shares));          // ★게이트 예약 후 여유 0 이지만 부모 예약 되돌림으로 분할 성립
            Assert.False(r.Mask.IsRaised(ArmingInterlockSource.ChildSlotRejection));
            Submit(r, 1);                                                          // 이제 실제로 여유 0 → 계좌 게이트에서 거부(분할 전)
            Assert.Equal(1, r.Kiwoom.Places);
        }

        // ════════════ §3 게이트웨이 자식 봉인 ════════════

        [Fact]
        public void Gateway_ChildLatchIndependent_NormalOrdersNotBlocked_VetoAndNonChildRefused()
        {
            bool valve = true;
            var sealedLatch = new SealedEmissionInterlock(16);                    // 슬롯 16개(mask 15 · 최소 용량) — 충돌 유도
            var gw = new GenesisLiveIgnitionGateway(() => valve, () => valve, () => true, sealedEmission: sealedLatch);
            int sends = 0;
            SealedSend send = (in SealedOrderToken t) => sends++;
            long child = ChildIdSpace.Compose(5, 0);                              // 슬롯 0 · 값 > 2^62
            Assert.True(gw.EmitChild(child, 5930, 0, 1, 70_000 * S, S, send));
            Assert.Equal(0, (int)(child & 15));
            Assert.True(gw.Emit(16, 5930, 0, 1, 70_000 * S, S, send));            // ★같은 슬롯(16 & 15 = 0)의 일반 주문 — 자식 래치와 독립이라 통과
            Assert.False(gw.EmitChild(child, 5930, 0, 1, 70_000 * S, S, send));   // 자식 exactly-once
            Assert.False(gw.EmitChild(8, 5930, 0, 1, 70_000 * S, S, send));       // 자식 ID 공간 밖 = 거부
            valve = false;
            Assert.False(gw.EmitChild(ChildIdSpace.Compose(6, 0), 5930, 0, 1, 70_000 * S, S, send));   // 이중키 veto
            Assert.Equal((2, 1L), (sends, gw.ChildEmissions));
        }

        [Fact]
        public void Drain_ViaGateway_ChildrenSealed_FirstChildVeto_AbandonsWithoutDisarm()
        {
            var r = Make(new IBrokerAccountView?[] { new FakeView { Headroom = 420_000 }, new FakeView { Headroom = 280_000 } }, gateway: true);
            Submit(r, 10);
            Assert.Equal((6L, 4L), (r.Kiwoom.Shares, r.Kis.Shares));              // 게이트웨이 경유 자식 봉인 사출(한도 비례 6/4)
            Assert.Equal(2, r.Gateway!.ChildEmissions);
            Assert.Equal(2, r.Exec.Tracked);
            r.Valve = false;                                                       // 무장 상실
            Submit(r, 10);
            Assert.Equal((1, 1), (r.Kiwoom.Places, r.Kis.Places));                // 추가 송출 0
            Assert.Equal(2, r.Exec.Tracked);                                       // 두 번째 부모의 자식 슬롯 전부 롤백
            Assert.False(r.Mask.IsRaised(ArmingInterlockSource.ChildSlotRejection)); // 송출 0 veto = 포기(거부 아님) · Disarm 없음
        }

        // ════════════ §4 호스트 결선 ════════════

        private static string RepoRoot()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d is not null && !File.Exists(Path.Combine(d.FullName, "StockManager.sln"))) d = d.Parent;
            Assert.NotNull(d);
            return d!.FullName;
        }

        [Fact]
        public void Host_CompositionRoot_MultiBroker_LimitFeed_Matrix_Wired()
        {
            const BindingFlags AllF = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
            var host = typeof(StockManager.Blazor.BlazorHomeForm);
            var targets = new MethodBase[]
            {
                typeof(BrokerLimitFeedProvider).GetConstructors()[0],
                typeof(AccountSyncBrokerView).GetConstructors()[0],
                typeof(ChildSlotTrackingMatrix).GetConstructors()[0],
                typeof(ChildSlotTrackingMatrix).GetMethod(nameof(ChildSlotTrackingMatrix.BindInterlock))!,
                typeof(RouterFeedGate).GetMethod(nameof(RouterFeedGate.EnableSplit))!,
                typeof(OrderDrainWorker).GetMethod(nameof(OrderDrainWorker.EnableChildSplit))!,
                typeof(KisLiveBroker).GetConstructors()[0],
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
            string form = File.ReadAllText(Path.Combine(RepoRoot(), "StockManager", "Blazor", "BlazorHomeForm.cs"));
            Assert.Contains("new MS2E.Core.LockFreeBrokerSwitchboard(2)", form);
            Assert.Contains("if (!kisConfigured) routerBoard.Blacklist(1);", form);   // ★KIS 미결속 = 라우터 선택 불가
            Assert.Contains("brokersByIndex: stampedBrokerMap);", form);   // [v12.17.6] 출구 스탬프 프로브 = 같은 센티널을 감쌈(우회 0)
            Assert.Contains("new StockManager.Telemetry.EgressStageProbe(egressSentinel, orderPathProfiler)", form);
            Assert.Contains("kisEgress is null ? null : new StockManager.Telemetry.EgressStageProbe(kisEgress, orderPathProfiler)", form);
            Assert.Contains("MS2E.Core.ArmingInterlockSource.ChildSlotRejection,", form);   // 필수 비트
            Assert.Contains("bridge.FillReceived += onChildFill;", form);
            Assert.Contains("routerGate.EnableSplit(limitFeed, childMatrix.TripExternal)", form);
            Assert.Contains("cfg.GetBool(\"Routing.SplitEnabled\", false)", form);   // 분할은 명시 설정으로만
            Assert.Contains("bridge.ServerIdentityVerifier = MS2E.Infrastructure.PipeServerIdentity.ImagePathVerifier(proxyExe)", form);   // 파이프 신원 검증(기결선)
            string sln = File.ReadAllText(Path.Combine(RepoRoot(), "StockManager.sln"));
            foreach (var proj in new[] { @"StockManager.PrimesBench\StockManager.PrimesBench.csproj", @"StockManager.KiwoomProxy\StockManager.KiwoomProxy.csproj", @"tools\KiwoomComShim\KiwoomComShim.csproj" })
                Assert.Contains(proj, sln);
        }
    }
}
