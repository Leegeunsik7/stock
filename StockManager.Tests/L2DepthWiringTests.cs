// ============================================================================
// L2DepthWiringTests — [L2 · 추천 A] 키움 10호가 L2 피드 · 기준가 자동 주입 · 깊이/스프레드 가드 · 플래시 크래시 Disarm
//
//  §1 KiwoomRealFids — 키움 부호 가격/수량 문자열 · 기준가(현재가−전일대비) · SetRealReg FID 목록.
//  §2 L2OrderBookDepthParser — 336B 프레임 ReadOnlySpan 판독 · Ok/Empty/OneSided/Malformed/NonMonotonic/Crossed/FlashHole · 스프레드.
//  §3 브리지 — 와이어 타입 8/9 헤더 수용 · 검증 게이트 · 디스패치 이벤트.
//  §4 DepthBook 스프레드 가드 → RouterFeedGate 거부 + 대사(SpreadRejected).
//  §5 [방향 B] OrderbookIlliquidityInterlock — 연속매매 중 교차/공백/극한 스프레드 → sticky Disarm · 장외 미격발.
//  §6 E2E — L2 프레임 → 파서 → DepthBook → 보호 지정가(깊이 이내 송출 · 부족 축소 · 공백 즉시 거부) · 기준가 → 가격제한폭 클램프.
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
    [Collection("ConcurrencyStress")]
    public sealed class L2DepthWiringTests
    {
        private const long S = 100_000_000L;
        private const long Code = 5930;
        private static readonly long Sym = ReferencePriceFeedAdapter.SymbolHashForCode(Code);

        /// <summary>매도 70,000 부터 100원 간격 오름 · 매수 69,900 부터 100원 간격 내림 · 각 qty 주.</summary>
        private static OrderBookPayload Book(long qty = 100, long ask1 = 70_000, long bid1 = 69_900, int levels = 10)
        {
            var b = new OrderBookPayload { SymbolCode = Code, TimestampTicks = 1 };
            for (int i = 0; i < levels; i++)
            {
                b.AskPrice[i] = ask1 + i * 100; b.AskQty[i] = qty;
                b.BidPrice[i] = bid1 - i * 100; b.BidQty[i] = qty;
            }
            return b;
        }

        private static byte[] Payload(in OrderBookPayload b)
            => BridgeSerializer.Pack(BridgeMessageType.OrderBook, in b).AsSpan(BridgeSerializer.HeaderSize).ToArray();

        // ════════════ §1 KiwoomRealFids ════════════

        [Theory]
        [InlineData("+70000", 70_000)] [InlineData("-69900", 69_900)] [InlineData("  71000 ", 71_000)]
        [InlineData("", 0)] [InlineData("abc", 0)] [InlineData("0", 0)]
        public void ParsePrice_KiwoomSigned(string s, long expected) => Assert.Equal(expected, KiwoomRealFids.ParsePrice(s));

        [Fact]
        public void BasePrice_FromCurrentAndChange_And_RegistrationList()
        {
            Assert.Equal(70_000, KiwoomRealFids.BasePrice("+70500", "+500"));     // 상승
            Assert.Equal(70_000, KiwoomRealFids.BasePrice("-69500", "-500"));     // 하락
            Assert.Equal(70_000, KiwoomRealFids.BasePrice("70000", "0"));         // 보합
            Assert.Equal(0, KiwoomRealFids.BasePrice("", "+500"));
            Assert.Equal(0, KiwoomRealFids.BasePrice("500", "+500"));             // 기준가 0 = 무효
            Assert.Equal(12, KiwoomRealFids.ParseQty(" 12 "));
            Assert.Equal(0, KiwoomRealFids.ParseQty("-3"));
            var fids = KiwoomRealFids.DefaultRegistrationList().Split(';');
            Assert.Equal(44, fids.Length);
            Assert.Contains("11", fids); Assert.Contains("41", fids); Assert.Contains("80", fids);
        }

        // ════════════ §2 파서 ════════════

        [Fact]
        public void Parser_Ok_RawDepth_Spread()
        {
            var b = Book(qty: 100);
            Assert.Equal(336, System.Runtime.CompilerServices.Unsafe.SizeOf<OrderBookPayload>());
            var v = L2OrderBookDepthParser.TryParse(Payload(in b), out var back, out var d, out long spread);
            Assert.Equal(L2BookVerdict.Ok, v);
            Assert.Equal(Code, back.SymbolCode);
            Assert.Equal(70_000 * S, d.AskPriceRaw[0]);
            Assert.Equal(70_400 * S, d.AskPriceRaw[4]);
            Assert.Equal(100 * S, d.BidQtyRaw[2]);
            Assert.Equal(14, spread);                                          // 100 / 69,950 ≈ 14.3bps
        }

        [Fact]
        public void Parser_AnomalyVerdicts()
        {
            var crossed = Book(ask1: 70_000, bid1: 70_000);
            Assert.Equal(L2BookVerdict.Crossed, L2OrderBookDepthParser.Validate(in crossed, out _, out long sp));
            Assert.Equal(-1, sp);

            var hole = Book(); hole.AskQty[1] = 0;                             // 2호가 잔량 공백 + 깊은 호가 잔량 존재
            Assert.Equal(L2BookVerdict.FlashHole, L2OrderBookDepthParser.Validate(in hole, out var hd, out _));
            Assert.Equal(0, hd.AskPriceRaw[0]);                                // 비정상 판정 = 빈 스냅샷

            var nonMono = Book(); nonMono.BidPrice[3] = nonMono.BidPrice[1];
            Assert.Equal(L2BookVerdict.NonMonotonic, L2OrderBookDepthParser.Validate(in nonMono, out _, out _));

            var gap = Book(); gap.AskPrice[2] = 0;                              // 사다리 중간 가격 구멍
            Assert.Equal(L2BookVerdict.Malformed, L2OrderBookDepthParser.Validate(in gap, out _, out _));

            var neg = Book(); neg.BidQty[0] = -1;
            Assert.Equal(L2BookVerdict.Malformed, L2OrderBookDepthParser.Validate(in neg, out _, out _));

            var empty = new OrderBookPayload { SymbolCode = Code };
            Assert.Equal(L2BookVerdict.Empty, L2OrderBookDepthParser.Validate(in empty, out _, out _));

            var oneSided = Book(); for (int i = 0; i < 10; i++) { oneSided.BidPrice[i] = 0; oneSided.BidQty[i] = 0; }
            Assert.Equal(L2BookVerdict.OneSided, L2OrderBookDepthParser.Validate(in oneSided, out _, out _));

            var badSym = Book(); badSym.SymbolCode = 0;
            Assert.Equal(L2BookVerdict.Malformed, L2OrderBookDepthParser.Validate(in badSym, out _, out _));
            Assert.Equal(L2BookVerdict.Malformed, L2OrderBookDepthParser.TryParse(new byte[100], out _, out _, out _));

            var deepThin = Book(); deepThin.AskQty[5] = 0;                      // 4호가 이후 공백은 가드 범위 밖 — 정상
            Assert.Equal(L2BookVerdict.Ok, L2OrderBookDepthParser.Validate(in deepThin, out _, out _));
        }

        // ════════════ §3 브리지 ════════════

        [Fact]
        public void Bridge_WireTypes_Gate_Dispatch()
        {
            var b = Book();
            byte[] frame = BridgeSerializer.Pack(BridgeMessageType.OrderBook, in b);
            Assert.True(BridgeSerializer.TryParseHeader(frame.AsSpan(0, BridgeSerializer.HeaderSize), out var t, out int len));
            Assert.Equal(BridgeMessageType.OrderBook, t);
            Assert.Equal(336, len);
            var rp = new ReferencePricePayload { SymbolCode = Code, BasePrice = 54_000, TimestampTicks = 1 };
            byte[] rframe = BridgeSerializer.Pack(BridgeMessageType.ReferencePrice, in rp);
            Assert.True(BridgeSerializer.TryParseHeader(rframe.AsSpan(0, BridgeSerializer.HeaderSize), out var rt, out _));
            Assert.Equal(BridgeMessageType.ReferencePrice, rt);

            var gate = typeof(KiwoomBridgeClient).GetMethod("ValidateIncoming", BindingFlags.NonPublic | BindingFlags.Static)!;
            Assert.True((bool)gate.Invoke(null, new object[] { BridgeMessageType.OrderBook, Payload(in b) })!);
            var crossed = Book(ask1: 70_000, bid1: 70_100);
            Assert.True((bool)gate.Invoke(null, new object[] { BridgeMessageType.OrderBook, Payload(in crossed) })!);   // 시장 상태 이상은 하류 판정(관측 보존)
            Assert.False((bool)gate.Invoke(null, new object[] { BridgeMessageType.OrderBook, new byte[100] })!);
            var badRp = new ReferencePricePayload { SymbolCode = Code, BasePrice = 0 };
            Assert.False((bool)gate.Invoke(null, new object[] { BridgeMessageType.ReferencePrice,
                BridgeSerializer.Pack(BridgeMessageType.ReferencePrice, in badRp).AsSpan(BridgeSerializer.HeaderSize).ToArray() })!);

            using var client = new KiwoomBridgeClient(pipeName: "MS2E_TEST_L2_" + Guid.NewGuid().ToString("N"));
            OrderBookPayload got = default; ReferencePricePayload gotRp = default;
            client.OrderBookReceived += x => got = x;
            client.ReferencePriceReceived += x => gotRp = x;
            var dispatch = typeof(KiwoomBridgeClient).GetMethod("Dispatch", BindingFlags.NonPublic | BindingFlags.Instance)!;
            dispatch.Invoke(client, new object[] { new BridgeEnvelope(BridgeMessageType.OrderBook, Payload(in b)) });
            dispatch.Invoke(client, new object[] { new BridgeEnvelope(BridgeMessageType.ReferencePrice, rframe.AsSpan(BridgeSerializer.HeaderSize).ToArray()) });
            Assert.Equal(70_000, got.AskPrice[0]);
            Assert.Equal(54_000, gotRp.BasePrice);
        }

        // ════════════ §4 · §5 · §6 ════════════

        private sealed class Rig
        {
            public long Now = 1_000_000;
            public readonly List<ReconciliationRecord> Records = new(256);
            public ReferencePriceFeedAdapter Price = null!;
            public ResilientOrderRouter Router = null!;
            public OrderExecutionFeedAdapter Exec = null!;
            public DepthBook Depth = null!;
            public MarketDataSpikeInterlock Spike = null!;
            public OrderbookIlliquidityInterlock Illiquidity = null!;
            public RouterFeedGate Gate = null!;
            public bool Session = true;

            /// <summary>호스트(BlazorHomeForm) L2 핸들러와 동일 로직.</summary>
            public void OnBook(in OrderBookPayload book)
            {
                var v = L2OrderBookDepthParser.Validate(in book, out var depth, out long spreadBps);
                Illiquidity.Observe(v, spreadBps);
                Depth.OnDepth(ReferencePriceFeedAdapter.SymbolHashForCode(book.SymbolCode), in depth);
            }

            public void OnBase(in ReferencePricePayload rp) => Spike.SetReference(ReferencePriceFeedAdapter.SymbolHashForCode(rp.SymbolCode), rp.BasePrice);
        }

        private static Rig Make(long maxSpreadBps = 300)
        {
            var r = new Rig();
            Func<long> clock = () => Volatile.Read(ref r.Now);
            r.Price = new ReferencePriceFeedAdapter(capacityPow2: 64, nowMicros: clock);
            var board = new LockFreeBrokerSwitchboard(1);
            var pending = new PendingOrderCascadeCanceller(64);
            var sentinel = new BrokerFailoverSentinel(board, pendingOrders: pending);
            r.Router = new ResilientOrderRouter(board, sentinel, pending, idMapCapacityPow2: 256, maxSlippageBps: 50);
            r.Exec = new OrderExecutionFeedAdapter(r.Router, 64, 120_000_000, clock, rec => r.Records.Add(rec));
            r.Depth = new DepthBook(capacityPow2: 64, maxAgeMicros: 2_000_000, nowMicros: clock, maxSpreadBps: maxSpreadBps);
            r.Spike = new MarketDataSpikeInterlock();
            r.Illiquidity = new OrderbookIlliquidityInterlock(1_000, () => r.Session);
            var breaker = new FeedDisconnectCircuitBreaker(r.Price, () => true);
            r.Gate = new RouterFeedGate(r.Router, r.Price, r.Exec, breaker, depth: r.Depth, requireDepthForMarket: true,
                basePriceRaw: sym => r.Spike.TryGetReference(sym, out long b) ? b * S : 0L);
            r.Price.OnTick(Sym, 70_000, 1);
            breaker.Evaluate();
            return r;
        }

        private static OrderProposal Market(long id, decimal qty, OrderSide side = OrderSide.Buy)
            => new(id, 0, Code, side, 0, QPrice.Of(0m), QQty.Of(qty), 0);

        [Fact]
        public void E2E_L2Frame_EnablesProtectedMarketOrder_WithinDepth()
        {
            var r = Make();
            var m = Market(1, 50m);
            Assert.False(r.Gate.TryAdmit(in m, out _));                         // L2 미수신 = 거부(fail-closed)
            Assert.Equal(OrderRouteVerdict.DepthInsufficient, r.Gate.LastVerdict);
            var b = Book(qty: 100);
            Assert.Equal(L2BookVerdict.Ok, L2OrderBookDepthParser.TryParse(Payload(in b), out var parsed, out _, out _));
            r.OnBook(in parsed);
            var m2 = Market(2, 50m);
            Assert.True(r.Gate.TryAdmit(in m2, out var adm));
            Assert.Equal(1, adm.KindByte);                                     // ★보호 지정가
            Assert.Equal(70_200 * S, adm.PriceRaw);
            Assert.Equal(50 * S, adm.QtyRaw);
        }

        [Fact]
        public void E2E_ThinBook_Reduced_FlashHole_ClearsDepth_AndDisarms()
        {
            var r = Make();
            var mask = new ArmingInterlockBitmask(ArmingInterlockSource.OrderbookIlliquidity);
            r.Illiquidity.BindInterlock(mask, ArmingInterlockSource.OrderbookIlliquidity);
            Assert.True(mask.Seal(out _));
            var thin = Book(qty: 4);                                           // 70,000·70,100·70,200 각 4주 = 12주
            r.OnBook(in thin);
            var m = Market(1, 50m);
            Assert.True(r.Gate.TryAdmit(in m, out var adm));
            Assert.Equal(12 * S, adm.QtyRaw);
            Assert.Contains(r.Records, x => x.Kind == ReconciliationKind.DepthReduced && x.Qty == 12);

            var hole = Book(qty: 100); hole.AskQty[0] = 0;                     // 1호가 잔량 공백 — 플래시 크래시 징후
            r.OnBook(in hole);
            Assert.True(mask.Blocked);                                         // ★연속매매 중 → 회로 Disarm
            Assert.Equal(L2BookVerdict.FlashHole, r.Illiquidity.LastTripVerdict);
            var m2 = Market(2, 1m);
            Assert.False(r.Gate.TryAdmit(in m2, out _));                       // 직전 정상 스냅샷 잔존 사용 금지(빈 스냅샷 덮어씀)
            Assert.Equal(OrderRouteVerdict.DepthInsufficient, r.Gate.LastVerdict);
        }

        [Fact]
        public void SpreadTooWide_RejectsMarket_Reconciled()
        {
            var r = Make(maxSpreadBps: 300);
            var wide = Book(qty: 100, ask1: 70_000, bid1: 67_500);             // ≈ 363bps
            r.OnBook(in wide);
            var m = Market(1, 5m);
            Assert.False(r.Gate.TryAdmit(in m, out _));
            Assert.Equal(OrderRouteVerdict.SpreadTooWide, r.Gate.LastVerdict);
            Assert.Contains(r.Records, x => x.Kind == ReconciliationKind.SpreadRejected);
            Assert.False(r.Illiquidity.Tripped);                               // 극한(1,000bps) 미만 — 주문 거부만, 회로는 유지
            var lim = new OrderProposal(2, 0, Code, OrderSide.Buy, 1, QPrice.Of(70_000m), QQty.Of(5m), 0);
            Assert.True(r.Gate.TryAdmit(in lim, out _));                       // 지정가는 가격이 보호선 — 영향 없음
        }

        [Fact]
        public void Illiquidity_ExtremeSpread_Trips_OffSession_CountsOnly()
        {
            var r = Make();
            r.Session = false;
            var crossed = Book(ask1: 70_000, bid1: 70_100);
            r.OnBook(in crossed);                                              // 동시호가 예상가 교차 — 장외 미격발
            Assert.False(r.Illiquidity.Tripped);
            Assert.Equal(1, r.Illiquidity.AnomaliesOffSession);
            r.Session = true;
            var blown = Book(qty: 100, ask1: 70_000, bid1: 62_000);            // ≈ 1,212bps
            r.OnBook(in blown);
            Assert.True(r.Illiquidity.Tripped);
            Assert.False(r.Illiquidity.TryRearm(true, false));
            Assert.True(r.Illiquidity.TryRearm(true, true));
            Assert.False(r.Illiquidity.Tripped);
        }

        [Fact]
        public void ReferencePrice_Injected_ActivatesBandClamp_AndSpikeBand()
        {
            var r = Make();
            r.OnBook(Book(qty: 100, ask1: 70_200, bid1: 70_100));
            r.Price.OnTick(Sym, 70_150, 2);
            var before = Market(1, 1m);
            Assert.True(r.Gate.TryAdmit(in before, out var a0));
            Assert.Equal(70_300 * S, a0.PriceRaw);                             // 기준가 미주입 — 클램프 없음(70,360 → 70,300)
            r.OnBase(new ReferencePricePayload { SymbolCode = Code, BasePrice = 54_000 });   // 상한가 70,200
            var after = Market(2, 1m);
            Assert.True(r.Gate.TryAdmit(in after, out var a1));
            Assert.Equal(70_200 * S, a1.PriceRaw);                             // ★상한가 클램프 활성
            Assert.Equal(SpikeVerdict.OutOfPriceBand, r.Spike.Observe(Sym, 80_000)); // 이상 시세 밴드도 활성
        }

        // ════════════ §7 δ=0 · 호스트 결선 ════════════

        [Fact]
        public void HotPaths_ZeroAlloc()
        {
            var r = Make();
            var b = Book(qty: 100);
            byte[] pl = Payload(in b);
            long sink = 0;
            r.OnBook(in b);
            Assert.Equal(0L, MinAllocated(() =>
            {
                for (int i = 0; i < 5_000; i++)
                {
                    var v = L2OrderBookDepthParser.TryParse(pl, out var parsed, out var d, out long spread);
                    sink += (long)v + spread + d.AskPriceRaw[0];
                    r.Illiquidity.Observe(v, spread);
                    r.Depth.OnDepth(Sym, in d);
                    sink += (long)r.Depth.Evaluate(Sym, OrderSide.Buy, 70_200 * S, 10, out long allowed) + allowed;
                    sink += KiwoomRealFids.ParsePrice("+70000") + KiwoomRealFids.BasePrice("+70500", "+500");
                }
            }));
            Assert.True(sink > 0);
        }

        [Fact]
        public void Host_SubscribesL2_Base_Ticks_AndBindsIlliquidity()
        {
            const BindingFlags AllF = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
            var host = typeof(StockManager.Blazor.BlazorHomeForm);
            var targets = new MethodBase[]
            {
                typeof(KiwoomBridgeClient).GetEvent(nameof(KiwoomBridgeClient.OrderBookReceived))!.AddMethod!,
                typeof(KiwoomBridgeClient).GetEvent(nameof(KiwoomBridgeClient.ReferencePriceReceived))!.AddMethod!,
                typeof(OrderbookIlliquidityInterlock).GetMethod(nameof(OrderbookIlliquidityInterlock.BindInterlock))!,
                typeof(L2OrderBookDepthParser).GetMethod(nameof(L2OrderBookDepthParser.Validate))!,
                typeof(MarketDataSpikeInterlock).GetMethod(nameof(MarketDataSpikeInterlock.SetReference))!,
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
                        if (il[i] is 0x28 or 0x6F)
                            try
                            {
                                var mb = m.Module.ResolveMethod(BitConverter.ToInt32(il, i + 1));
                                for (int k = 0; k < targets.Length; k++) if (mb == targets[k]) found[k] = true;
                            }
                            catch (ArgumentException) { }
                }
            }
            for (int k = 0; k < targets.Length; k++) Assert.True(found[k], $"결선 누락: {targets[k].DeclaringType!.Name}.{targets[k].Name}");
        }
    }
}
