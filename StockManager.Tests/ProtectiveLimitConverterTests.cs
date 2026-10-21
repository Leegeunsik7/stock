// ============================================================================
// ProtectiveLimitConverterTests — [보호 지정가 · 추천 A] 시장가 → 보호 지정가 · 호가 단위 정렬 · 호가 깊이 가드 · 극한 슬리피지 Disarm
//
//  §1 [방향 A] KrxTickAligner — KrxTickLadder 와 전 구간 동치 · 보호 방향 정렬 · 격자 유효성 · 포화.
//  §2 ProtectiveLimitOrderConverter — 매수 상한 ≤ 참조가×(1+offset) · 매도 하한 ≥ ×(1−offset) · 가격제한폭 클램프/거부 · 포화.
//  §3 DepthBook — 1~3호가 · 보호선 밖/호가 공백 제외 · Ok/Reduced/Insufficient/Missing/Stale.
//  §4 RouterFeedGate — 비보호 시장가 유출 0(송출 Kind 항상 지정가) · 깊이 미상 거부 · 수량 축소 · 슬리피지 대사.
//  §5 E2E — OrderDrainWorker 가 원 시장가가 아니라 보호 지정가·축소 수량으로 송출.
//  §6 [방향 B] 극한 슬리피지 Disarm — 불리 방향만 · sticky · 집계 비트 · 이중키.
//  §7 δ=0 · 호스트 결선 IL.
// ============================================================================
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using MS2E.Core;
using MS2E.Infrastructure;
using StockManager.Adapters;
using StockManager.Engines;
using Xunit;
using static StockManager.Tests.ZeroAllocProbe;

namespace StockManager.Tests
{
    internal sealed class KindSpyBroker : IBroker
    {
        public readonly List<(PaperKind Kind, decimal Price, decimal Qty)> Orders = new();
        public string Name => "KindSpy";
        public bool IsLive => false;
        public (bool ok, string msg) Place(PaperAsset asset, string symbol, string name, PaperSide side, PaperKind kind,
                                           decimal qty, decimal limitPrice, decimal currentPrice)
        {
            Orders.Add((kind, limitPrice, qty));
            return (true, (7000 + Orders.Count).ToString());
        }
        public void Cancel(string orderId) { }
        public decimal HeldQty(PaperAsset asset, string symbol) => 0m;
    }

    [Collection("ConcurrencyStress")]
    public sealed class ProtectiveLimitConverterTests
    {
        private const long S = 100_000_000L;
        private const long Code = 5930;
        private static readonly long Sym = ReferencePriceFeedAdapter.SymbolHashForCode(Code);

        // ════════════ §1 KrxTickAligner ════════════

        [Fact]
        public void Aligner_MatchesExistingLadder_AllBands()
        {
            var rnd = new Random(42);
            var samples = new List<long> { 1, 1_999, 2_000, 4_999, 5_000, 19_999, 20_000, 49_999, 50_000, 199_999, 200_000, 499_999, 500_000, 3_000_000 };
            for (int i = 0; i < 5_000; i++) samples.Add(rnd.NextInt64(1, 2_000_000));
            foreach (long won in samples)
            {
                long expected = (long)(KrxTickLadder.TickWonFor(won) * S);
                Assert.Equal(expected, KrxTickAligner.TickFor(won * S));
            }
        }

        [Fact]
        public void Aligner_DownUp_ProtectiveDirection_ValidGrid()
        {
            var rnd = new Random(7);
            for (int i = 0; i < 20_000; i++)
            {
                long raw = rnd.NextInt64(1, 1_500_000 * S);
                long down = KrxTickAligner.AlignDown(raw), up = KrxTickAligner.AlignUp(raw);
                Assert.True(down <= raw && raw <= up, $"{raw}: {down}..{up}");
                Assert.True(KrxTickAligner.IsAligned(up), $"up {up} 비격자");
                if (down > 0) Assert.True(KrxTickAligner.IsAligned(down), $"down {down} 비격자");
                Assert.True(up - down <= KrxTickAligner.TickFor(raw));
            }
            Assert.Equal(2_000 * S, KrxTickAligner.AlignUp(1_999 * S + 1));   // 경계 진입 시 상위 구간 격자점
            Assert.Equal(50_000 * S, KrxTickAligner.AlignUp(49_990 * S + 1));
            Assert.Equal(0, KrxTickAligner.AlignDown(0));
            Assert.Equal(0, KrxTickAligner.AlignUp(-5));
            Assert.Equal(0, KrxTickAligner.AlignUp(long.MaxValue));          // 포화 → 무효
        }

        // ════════════ §2 ProtectiveLimitOrderConverter ════════════

        [Fact]
        public void Convert_BuyCap_SellFloor_Aligned()
        {
            var c = new ProtectiveLimitOrderConverter(30);
            Assert.Equal(70_200 * S, c.Convert(OrderSide.Buy, 70_000 * S));     // 70,210 → 100원 내림
            Assert.Equal(69_800 * S, c.Convert(OrderSide.Sell, 70_000 * S));    // 69,790 → 100원 올림
            Assert.Equal(2, c.Converted);
            var rnd = new Random(3);
            for (int i = 0; i < 10_000; i++)
            {
                long refRaw = rnd.NextInt64(100, 1_000_000) * S;
                long bps = rnd.Next(1, 300);
                long buy = ProtectiveLimitOrderConverter.Compute(OrderSide.Buy, refRaw, 0, bps, out _);
                long sell = ProtectiveLimitOrderConverter.Compute(OrderSide.Sell, refRaw, 0, bps, out _);
                Assert.True((Int128)buy * 10_000 <= (Int128)refRaw * (10_000 + bps), "매수 상한 초과");
                Assert.True((Int128)sell * 10_000 >= (Int128)refRaw * (10_000 - bps), "매도 하한 미달");
                Assert.True(KrxTickAligner.IsAligned(buy) && KrxTickAligner.IsAligned(sell));
            }
        }

        [Fact]
        public void Convert_PriceBand_ClampProtective_RejectAnomalous()
        {
            long basePx = 54_000 * S;                                          // 상한가 70,200 · 하한가 37,800
            Assert.Equal(70_200 * S, ProtectiveLimitOrderConverter.Compute(OrderSide.Buy, 70_150 * S, basePx, 30, out bool clamped));
            Assert.True(clamped);                                              // 70,360 → 70,300 → 상한가로 하향
            Assert.Equal(37_800 * S, ProtectiveLimitOrderConverter.Compute(OrderSide.Sell, 37_850 * S, basePx, 30, out clamped));
            Assert.True(clamped);
            Assert.Equal(0, ProtectiveLimitOrderConverter.Compute(OrderSide.Buy, 30_000 * S, basePx, 30, out _));    // 참조가 < 하한가 = 이상치
            Assert.Equal(0, ProtectiveLimitOrderConverter.Compute(OrderSide.Sell, 80_000 * S, basePx, 30, out _));   // 참조가 > 상한가
        }

        [Fact]
        public void Convert_FailClosed_Edges()
        {
            var c = new ProtectiveLimitOrderConverter(30);
            Assert.Equal(0, c.Convert(OrderSide.Buy, 0));
            Assert.Equal(0, c.Convert(OrderSide.Buy, -1));
            Assert.Equal(0, c.Convert(OrderSide.Buy, long.MaxValue));          // Int128 포화 → 실패
            Assert.Equal(3, c.Failed);
            Assert.Throws<ArgumentOutOfRangeException>(() => new ProtectiveLimitOrderConverter(0));
            Assert.Throws<ArgumentOutOfRangeException>(() => new ProtectiveLimitOrderConverter(1_001));
            Assert.Equal(1_999 * S, ProtectiveLimitOrderConverter.Compute(OrderSide.Buy, 1_999 * S, 0, 1, out _));   // 초소 offset = 참조가 내림
        }

        // ════════════ §3 DepthBook ════════════

        private static L2Depth Book(long[] askP, long[] askQ, long[] bidP, long[] bidQ)
        {
            var d = new L2Depth { SymbolCode = Code };
            for (int i = 0; i < 5; i++)
            {
                d.AskPriceRaw[i] = i < askP.Length ? askP[i] * S : 0; d.AskQtyRaw[i] = i < askQ.Length ? askQ[i] * S : 0;
                d.BidPriceRaw[i] = i < bidP.Length ? bidP[i] * S : 0; d.BidQtyRaw[i] = i < bidQ.Length ? bidQ[i] * S : 0;
            }
            return d;
        }

        [Fact]
        public void Depth_Levels1to3_WithinLimit_Verdicts()
        {
            long now = 1_000;
            var book = new DepthBook(capacityPow2: 16, maxAgeMicros: 1_000, nowMicros: () => now);
            Assert.Equal(DepthVerdict.Missing, book.Evaluate(Sym, OrderSide.Buy, 70_200 * S, 10, out _));
            var d = Book(new long[] { 70_000, 70_100, 70_200, 70_300, 70_400 }, new long[] { 3, 3, 3, 100, 100 },
                         new long[] { 69_900, 69_800, 69_700 }, new long[] { 5, 5, 5 });
            book.OnDepth(Sym, in d);
            Assert.Equal(DepthVerdict.Ok, book.Evaluate(Sym, OrderSide.Buy, 70_200 * S, 9, out long a));
            Assert.Equal(9, a);
            Assert.Equal(DepthVerdict.Reduced, book.Evaluate(Sym, OrderSide.Buy, 70_200 * S, 50, out a));
            Assert.Equal(9, a);                                                // 4~5호가(100주)는 제외
            Assert.Equal(DepthVerdict.Reduced, book.Evaluate(Sym, OrderSide.Buy, 70_100 * S, 50, out a));
            Assert.Equal(6, a);                                                // 보호선 밖 3호가 제외
            Assert.Equal(DepthVerdict.Insufficient, book.Evaluate(Sym, OrderSide.Buy, 69_999 * S, 5, out _));
            Assert.Equal(DepthVerdict.Ok, book.Evaluate(Sym, OrderSide.Sell, 69_700 * S, 15, out _));
            now += 1_001;
            Assert.Equal(DepthVerdict.Stale, book.Evaluate(Sym, OrderSide.Buy, 70_200 * S, 1, out _));

            var gap = Book(new long[] { 70_000, 0, 70_200 }, new long[] { 2, 0, 50 }, new long[0], new long[0]);
            book.OnDepth(Sym, in gap);
            Assert.Equal(DepthVerdict.Reduced, book.Evaluate(Sym, OrderSide.Buy, 70_200 * S, 10, out a));
            Assert.Equal(2, a);                                                // 호가 공백 이후 불신
        }

        // ════════════ §4 RouterFeedGate 통합 ════════════

        private sealed class Rig
        {
            public long Now = 1_000_000;
            public readonly List<ReconciliationRecord> Records = new();
            public ReferencePriceFeedAdapter Price = null!;
            public ResilientOrderRouter Router = null!;
            public OrderExecutionFeedAdapter Exec = null!;
            public FeedDisconnectCircuitBreaker Breaker = null!;
            public DepthBook Depth = null!;
            public ExtremeSlippageDisarmInterlock Extreme = null!;
            public RouterFeedGate Gate = null!;
        }

        private static Rig Make(bool withDepth = true, bool requireDepth = true, Func<long, long>? basePx = null, long offset = 30)
        {
            var r = new Rig();
            Func<long> clock = () => Volatile.Read(ref r.Now);
            r.Price = new ReferencePriceFeedAdapter(capacityPow2: 64, nowMicros: clock);
            var board = new LockFreeBrokerSwitchboard(1);
            var pending = new PendingOrderCascadeCanceller(64);
            var sentinel = new BrokerFailoverSentinel(board, pendingOrders: pending);
            r.Router = new ResilientOrderRouter(board, sentinel, pending, idMapCapacityPow2: 256, maxSlippageBps: 50);
            r.Extreme = new ExtremeSlippageDisarmInterlock(200);
            r.Exec = new OrderExecutionFeedAdapter(r.Router, 64, 60_000_000, clock, rec => r.Records.Add(rec), r.Extreme);
            r.Breaker = new FeedDisconnectCircuitBreaker(r.Price, () => true);
            r.Depth = new DepthBook(capacityPow2: 64, maxAgeMicros: 2_000_000, nowMicros: clock);
            r.Gate = new RouterFeedGate(r.Router, r.Price, r.Exec, r.Breaker, new ProtectiveLimitOrderConverter(offset),
                                        withDepth ? r.Depth : null, requireDepth, basePx);
            r.Price.OnTick(Sym, 70_000, 1);
            r.Breaker.Evaluate();
            return r;
        }

        private static OrderProposal P(long id, byte kind, decimal price, decimal qty, OrderSide side = OrderSide.Buy)
            => new(id, 0, Code, side, kind, QPrice.Of(price), QQty.Of(qty), 0);

        private static void DeepBook(Rig r, long askQty = 1_000, long bidQty = 1_000)
        {
            var d = Book(new long[] { 70_000, 70_100, 70_200 }, new long[] { askQty, askQty, askQty },
                         new long[] { 69_900, 69_800, 69_700 }, new long[] { bidQty, bidQty, bidQty });
            r.Depth.OnDepth(Sym, in d);
        }

        [Fact]
        public void Market_NoDepth_Rejected_ByDefault_NoSlotSideEffect()
        {
            var r = Make(withDepth: false);
            var m = P(1, 0, 0m, 10m);
            Assert.False(r.Gate.TryAdmit(in m, out var adm));
            Assert.Equal(default, adm);
            Assert.Equal(OrderRouteVerdict.DepthInsufficient, r.Gate.LastVerdict);
            Assert.Equal(ReconciliationKind.DepthRejected, Assert.Single(r.Records).Kind);
            Assert.Equal(0, r.Router.TrackedOrders);

            var r2 = Make(withDepth: true);                                    // 표는 있으나 종목 깊이 미수신
            Assert.False(r2.Gate.TryAdmit(in m, out _));
            Assert.Equal(OrderRouteVerdict.DepthInsufficient, r2.Gate.LastVerdict);
        }

        [Fact]
        public void Market_DeepBook_ConvertedToProtectiveLimit()
        {
            var r = Make();
            DeepBook(r);
            var buy = P(1, 0, 0m, 10m);
            Assert.True(r.Gate.TryAdmit(in buy, out var adm));
            Assert.Equal(1, adm.KindByte);                                     // ★비보호 시장가 유출 0
            Assert.Equal(70_200 * S, adm.PriceRaw);
            Assert.Equal(10 * S, adm.QtyRaw);
            var sell = P(2, 0, 0m, 10m, OrderSide.Sell);
            Assert.True(r.Gate.TryAdmit(in sell, out adm));
            Assert.Equal(69_800 * S, adm.PriceRaw);
            Assert.Equal(2, r.Gate.MarketConverted);
        }

        [Fact]
        public void Market_ThinBook_QuantityReduced_Reconciled()
        {
            var r = Make();
            DeepBook(r, askQty: 4);                                            // 1~3호가 12주(모두 보호선 70,200 이내)
            var m = P(1, 0, 0m, 50m);
            Assert.True(r.Gate.TryAdmit(in m, out var adm));
            Assert.Equal(12 * S, adm.QtyRaw);
            var rec = Assert.Single(r.Records);
            Assert.Equal(ReconciliationKind.DepthReduced, rec.Kind);
            Assert.Equal(12, rec.Qty);
            Assert.Equal(1, r.Gate.DepthReduced);
        }

        [Fact]
        public void Market_EmptyWithinProtection_Rejected()
        {
            var r = Make();
            var d = Book(new long[] { 71_000 }, new long[] { 500 }, new long[] { 69_000 }, new long[] { 500 });   // 최우선 매도가 보호선 밖
            r.Depth.OnDepth(Sym, in d);
            var m = P(1, 0, 0m, 5m);
            Assert.False(r.Gate.TryAdmit(in m, out _));
            Assert.Equal(OrderRouteVerdict.DepthInsufficient, r.Gate.LastVerdict);
        }

        [Fact]
        public void Market_NoReference_Rejected_EvenWithDeepBook()
        {
            var r = Make();
            DeepBook(r);
            r.Now += ReferencePriceFeedAdapter.DefaultMaxAgeMicros + 1;          // 참조가 만료(깊이는 갱신)
            DeepBook(r);
            var m = P(1, 0, 0m, 5m);
            Assert.False(r.Gate.TryAdmit(in m, out _));
            Assert.Equal(OrderRouteVerdict.StaleReference, r.Gate.LastVerdict);
        }

        [Fact]
        public void Limit_NoDepth_PassesUnchanged_SlippageBeyondLimit_Reconciled()
        {
            var r = Make(withDepth: false);
            var lim = P(1, 1, 70_100m, 10m);
            Assert.True(r.Gate.TryAdmit(in lim, out var adm));
            Assert.Equal((1, 70_100 * S, 10 * S), (adm.KindByte, adm.PriceRaw, adm.QtyRaw));
            var far = P(2, 1, 71_000m, 10m);                                    // +142bps > 50
            Assert.False(r.Gate.TryAdmit(in far, out _));
            Assert.Equal(OrderRouteVerdict.SlippageExceeded, r.Gate.LastVerdict);
            Assert.Equal(ReconciliationKind.SlippageRejected, Assert.Single(r.Records).Kind);
        }

        [Fact]
        public void Market_ProtectionUnavailable_OutOfBandReference()
        {
            var r = Make(basePx: _ => 100_000 * S);                             // 하한가 70,000 · 참조가 70,000 매수 70,200 OK
            DeepBook(r);
            var ok = P(1, 0, 0m, 1m);
            Assert.True(r.Gate.TryAdmit(in ok, out _));
            var r2 = Make(basePx: _ => 200_000 * S);                            // 하한가 140,000 > 참조가 = 이상치
            DeepBook(r2);
            Assert.False(r2.Gate.TryAdmit(in ok, out _));
            Assert.Equal(OrderRouteVerdict.ProtectionUnavailable, r2.Gate.LastVerdict);
        }

        [Fact]
        public void Gate_OffsetAboveRouterLimit_Throws()
        {
            Assert.Throws<ArgumentException>(() => Make(offset: 60));
        }

        // ════════════ §5 E2E ════════════

        [Fact]
        public void E2E_DrainWorker_SendsProtectiveLimit_NotMarket()
        {
            var r = Make();
            DeepBook(r, askQty: 2);                                             // 6주 가용
            var ring = new OrderDispatchRing(64);
            ring.Publish(0, Code, OrderSide.Buy, 0, QPrice.Of(0m), QQty.Of(20m), 1);   // 시장가 20주
            var broker = new KindSpyBroker();
            var disp = new GatedOrderDispatcher(ring, isArmed: () => true, new OrderAttestationService());
            using var w = new OrderDrainWorker(disp, broker, wired: false, routeGate: r.Gate);
            w.DrainOnce();
            var o = Assert.Single(broker.Orders);
            Assert.Equal(PaperKind.Limit, o.Kind);                              // ★시장가 미유출
            Assert.Equal(70_200m, o.Price);
            Assert.Equal(6m, o.Qty);                                            // 잔량 축소
        }

        [Fact]
        public void E2E_NoDepth_MarketNeverReachesBroker()
        {
            var r = Make(withDepth: false);
            var ring = new OrderDispatchRing(64);
            for (int i = 0; i < 5; i++) ring.Publish(0, Code, OrderSide.Buy, 0, QPrice.Of(0m), QQty.Of(1m), i);
            var broker = new KindSpyBroker();
            var disp = new GatedOrderDispatcher(ring, isArmed: () => true, new OrderAttestationService());
            using var w = new OrderDrainWorker(disp, broker, wired: false, routeGate: r.Gate);
            w.DrainOnce();
            Assert.Empty(broker.Orders);
            Assert.Equal(5, w.RouteVetoes);
        }

        // ════════════ §6 [방향 B] 극한 슬리피지 Disarm ════════════

        private static FillPayload Fill(long brokerOrder, long qty, double px) => new() { OrderId = brokerOrder, SymbolCode = Code, FillPrice = px, FillQty = qty, Side = 1 };

        [Fact]
        public void ExtremeSlippage_AdverseFill_DisarmsSticky_FavorableIgnored()
        {
            var r = Make(withDepth: false);
            var mask = new ArmingInterlockBitmask(ArmingInterlockSource.ExtremeSlippage);
            r.Extreme.BindInterlock(mask, ArmingInterlockSource.ExtremeSlippage);
            Assert.True(mask.Seal(out _));
            var a = P(1, 1, 70_100m, 10m); Assert.True(r.Gate.TryAdmit(in a, out _)); r.Gate.OnBrokerAccepted(1, "501");
            r.Exec.OnFill(Fill(501, 3, 69_000));                               // 매수 유리 체결
            r.Exec.OnFill(Fill(501, 3, 71_000));                               // +142bps — 한도 이내
            Assert.False(mask.Blocked);
            r.Exec.OnFill(Fill(501, 4, 71_500));                               // +214bps — 극한
            Assert.True(r.Extreme.Tripped);
            Assert.True(mask.Blocked);
            Assert.Contains(r.Records, x => x.Kind == ReconciliationKind.ExtremeSlippage && x.Qty == 71_500 * S);
            Assert.Equal(0, r.Router.TrackedOrders);                           // 완전 체결 해제는 정상 진행
            Assert.False(r.Extreme.TryRearm(true, false));
            Assert.True(r.Extreme.TryRearm(true, true));
            Assert.False(mask.Blocked);
        }

        [Fact]
        public void ExtremeSlippage_SellSide_And_InvalidFillPrices()
        {
            var x = new ExtremeSlippageDisarmInterlock(200);
            Assert.False(x.Observe(false, 70_000 * S, 71_000 * S));           // 매도 유리
            Assert.False(x.Observe(false, 70_000 * S, 0));
            Assert.True(x.Observe(false, 70_000 * S, 68_000 * S));            // −285bps
            Assert.False(x.Observe(false, 70_000 * S, 60_000 * S));           // 이미 Trip(원샷)
            Assert.Equal(1, x.Trips);

            var r = Make(withDepth: false);
            var a = P(1, 1, 70_000m, 2m); Assert.True(r.Gate.TryAdmit(in a, out _)); r.Gate.OnBrokerAccepted(1, "9");
            r.Exec.OnFill(Fill(9, 1, double.NaN));                             // 비유한 체결가 — 관측 생략(대사 진행)
            r.Exec.OnFill(Fill(9, 1, -5));
            Assert.False(r.Extreme.Tripped);
            Assert.Equal(0, r.Router.TrackedOrders);
        }

        // ════════════ §7 δ=0 · 호스트 결선 ════════════

        [Fact]
        public void HotPaths_ZeroAlloc()
        {
            var r = Make();
            DeepBook(r);
            var ids = new string[600];
            for (int i = 0; i < ids.Length; i++) ids[i] = (90_000 + i).ToString();
            long sink = 0;
            var warm = P(1, 0, 0m, 1m); r.Gate.TryAdmit(in warm, out _); r.Gate.OnBrokerAccepted(1, ids[0]); r.Exec.OnFill(Fill(90_000, 1, 70_000));

            Assert.Equal(0L, MinAllocated(() =>
            {
                for (long px = 1_000 * S; px < 1_000 * S + 50_000; px += 7)
                    sink += KrxTickAligner.AlignDown(px) + KrxTickAligner.AlignUp(px) + ProtectiveLimitOrderConverter.Compute(OrderSide.Buy, px * 50, 54_000 * S, 30, out _);
            }));
            long next = 10;
            Assert.Equal(0L, MinAllocated(() =>
            {
                for (int i = 1; i < 500; i++)
                {
                    long id = ++next;
                    var m = P(id, 0, 0m, 2m);
                    if (r.Gate.TryAdmit(in m, out var adm))
                    {
                        sink += adm.PriceRaw;
                        r.Gate.OnBrokerAccepted(id, ids[i]);
                        r.Exec.OnFill(Fill(90_000 + i, 2, 70_100));
                    }
                    sink += (long)r.Depth.Evaluate(Sym, OrderSide.Sell, 69_800 * S, 3, out long al) + al;
                }
            }));
            Assert.True(sink > 0);
            Assert.Equal(0, r.Router.TrackedOrders);
        }

        [Fact]
        public void Host_WiresProtectiveConversion()
        {
            const BindingFlags AllF = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
            var host = typeof(StockManager.Blazor.BlazorHomeForm);
            var targets = new MethodBase[]
            {
                typeof(ProtectiveLimitOrderConverter).GetConstructors()[0],
                typeof(DepthBook).GetConstructors()[0],
                typeof(ExtremeSlippageDisarmInterlock).GetMethod(nameof(ExtremeSlippageDisarmInterlock.BindInterlock))!,
            };
            var found = new bool[targets.Length];
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
                        if (il[i] is 0x28 or 0x6F or 0x73)
                            try
                            {
                                var mb = m.Module.ResolveMethod(BitConverter.ToInt32(il, i + 1));
                                for (int k = 0; k < targets.Length; k++) if (mb == targets[k]) found[k] = true;
                            }
                            catch (ArgumentException) { }
                }
            }
            for (int k = 0; k < targets.Length; k++) Assert.True(found[k], $"BlazorHomeForm 결선 누락: {targets[k].DeclaringType!.Name}.{targets[k].Name}");
        }
    }
}
