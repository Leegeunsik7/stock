// ============================================================================
// OpenOrderReconciliationTests — [미체결 대사 · 추천 A] 브로커 미체결 원장 ↔ 호스트 원장 1:1 대사 · 롤오버 확정 · 유령 주문 Disarm
//
//  §1 키움 코어(프록시) — OPT10075 입력/다행 판독/빈 결과 · 사상 통지(접수·정정) · 승계 사상 재등록 · 원장 번호 직접 취소.
//  §2 판독기 — 키움 행 형식 검증 · KIS TTTC8001R output1(취소 행/잔량 0 제외 · rt_cd · 절단 · 버퍼 · 소수 가격) · δ=0.
//  §3 대사기 — 승계 전량 일치 = Rollover(20) 0ms 해제 + 사상 재등록 · 잔량/누락 불일치 = sticky · 유령 = Disarm 선반영 후 비상 취소
//     (패스당 상한 · 상태 구동 재취소) · 스냅샷 경합(조회 후 체결/종결/신규) 오판 0 · 번호 미상 보류 · 사상 선도착 · 크래시 고아 ·
//     최초 대사 전 차단 · 배치 무결성(요청 없는 응답 · 행 번호 · 배치 교체 · 시한 · 페이지 상한) · KIS 경로 · 재무장 · 감사/블랙박스 · δ=0.
//  §4 슬롯 v2 승계 · 호스트/프록시 결선.
// ============================================================================
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using MS2E.Core;
using MS2E.Infrastructure;
using StockManager.Adapters;
using StockManager.Services;
using Xunit;

namespace StockManager.Tests
{
    [Collection("ConcurrencyStress")]
    public sealed class OpenOrderReconciliationTests
    {
        private const long Code = 5930;
        private const string Account = "8012345611";
        private const long Limit = 70_000;
        private static readonly long TickSec = TimeSpan.TicksPerSecond;

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

        // ── 원장 리그 ──
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

        private static void Track(Exec e, long proposal, long brokerId, long venueNo, long qty = 10, bool buy = true, long sym = Code, long limit = Limit)
        {
            var key = RouterFeedGate.KeyFor(proposal);
            Assert.True(e.Router.Route(key, limit * 100_000_000, limit * 100_000_000, 0.0).Accepted);
            Assert.True(e.Feed.TryTrack(proposal, key, 0, qty, buy, limit * 100_000_000, sym, limit));
            Assert.True(e.Feed.BindBrokerOrder(proposal, brokerId));
            if (venueNo > 0) e.Feed.OnOrderMapped(new OrderMappedPayload { ClientId = brokerId, VenueOrderNo = venueNo });
        }

        private static FillPayload Fill(long brokerId, long qty, int side = 1)
            => new() { OrderId = brokerId, SymbolCode = Code, FillQty = qty, FillPrice = Limit, Side = side, Status = BridgeFillStatusCodec.Encode(BridgeFillStatus.Filled, false) };

        private static FillPayload CancelConfirm(long brokerId)
            => new() { OrderId = brokerId, SymbolCode = Code, FillQty = 0, Side = 1, Status = BridgeFillStatusCodec.Encode(BridgeFillStatus.CancelConfirmed, true), RemainingQty = 0 };

        private static OpenOrderPayload Row(long batch, int idx, int count, long venueNo, long rem, int side = 1, long sym = Code, long price = Limit, long orig = 0)
            => new() { BatchId = batch, RowIndex = idx, RowCount = count, VenueOrderNo = venueNo, OrigVenueOrderNo = orig, SymbolCode = sym, OrderQty = rem, RemainingQty = rem, OrderPriceWon = price, Side = side };

        private sealed class FakeVenue : IOpenOrderVenue
        {
            public bool Alive = true;
            public int Requests, Cancels, Adopts, CancelsWhileBlocked;
            public readonly long[] CancelNo = new long[64], CancelQty = new long[64];
            public readonly long[] AdoptClient = new long[64], AdoptVenue = new long[64], AdoptRem = new long[64];
            public Func<bool>? BlockedProbe;
            public int VenueKind => OpenOrderVenueKind.Kiwoom;
            public bool RequestOpenOrders() { if (!Alive) return false; Requests++; return true; }
            public bool AdoptMapping(long clientId, long venueOrderNo, long symbolCode, int side, long remaining, long priceWon)
            {
                AdoptClient[Adopts] = clientId; AdoptVenue[Adopts] = venueOrderNo; AdoptRem[Adopts] = remaining; Adopts++;
                return true;
            }
            public bool CancelByVenueNo(long venueOrderNo, long symbolCode, int side, long qty)
            {
                if (BlockedProbe?.Invoke() == true) CancelsWhileBlocked++;
                CancelNo[Cancels % 64] = venueOrderNo; CancelQty[Cancels % 64] = qty; Cancels++;
                return true;
            }
        }

        private sealed class Rig
        {
            public Exec E = null!;
            public FakeVenue V = new();
            public bool Armed = true;
            public long Ticks = 1_000_000 * TimeSpan.TicksPerSecond;
            public BrokerOpenOrderReconciler R = null!;
            public ArmingInterlockBitmask Mask = null!;
            public readonly List<OpenOrderPassResult> Passes = new();
            public int Marked;
            public void Advance(double seconds) => Ticks += (long)(seconds * TickSec);
            public void Feed(params OpenOrderPayload[] rows) { foreach (var r in rows) R.OnKiwoomRow(r); }
            public OpenOrderPassResult Last => Passes[^1];
        }

        private static Rig MakeRig(Exec? exec = null, Action? mark = null, bool requireKiwoom = true, bool requireKis = false, int maxCancels = 4,
                                   int pageCap = 100, bool record = true, ArmingInterlockBitmask? mask = null, bool venue = true)
        {
            var r = new Rig { E = exec ?? MakeExec() };
            r.R = new BrokerOpenOrderReconciler(r.E.Feed, venue ? r.V : null, mark ?? (() => r.Marked++), requireKiwoom, requireKis, capacity: 64,
                                                maxCancelsPerPass: maxCancels, nowTicks: () => Volatile.Read(ref r.Ticks),
                                                onPass: record ? r.Passes.Add : null, kiwoomPageCap: pageCap);
            r.Mask = mask ?? new ArmingInterlockBitmask(ArmingInterlockSource.OpenOrderDivergence);
            r.R.BindInterlock(r.Mask, ArmingInterlockSource.OpenOrderDivergence);
            r.R.AttachCancelArming(() => r.Armed);                              // [v12.18.7-DEV] 무장 경로 회귀(기본 무장)
            return r;
        }

        // ════════════ §1 키움 코어(프록시) ════════════

        private sealed class CoreRig
        {
            public readonly FakeOcx Ocx = new();
            public readonly List<FillPayload> Fills = new();
            public readonly List<OpenOrderPayload> Rows = new();
            public readonly List<OrderMappedPayload> Maps = new();
            public KiwoomComEventCore Core = null!;
        }

        private static CoreRig MakeCore()
        {
            var c = new CoreRig();
            c.Core = new KiwoomComEventCore(c.Ocx, null, c.Fills.Add, null, null, null, () => 1_000_000_000L, mapCapacityPow2: 64,
                                            onOpenOrder: c.Rows.Add, onMapped: c.Maps.Add);
            return c;
        }

        [Fact]
        public void Core_Opt10075_Inputs_MultiRowParse_EmptyBatch()
        {
            var c = MakeCore();
            Assert.Equal(-1, c.Core.QueryOpenOrders("", "5001"));
            Assert.Equal(0, c.Core.QueryOpenOrders(Account, "5001"));
            Assert.Equal(("OPENORD", "opt10075"), c.Ocx.Requests[^1]);
            Assert.Contains(("계좌번호", Account), c.Ocx.Inputs);
            Assert.Contains(("체결구분", "1"), c.Ocx.Inputs);                     // ★1 = 미체결만
            c.Ocx.TrRows["OPENORD"] = new List<Dictionary<string, string>>
            {
                new() { ["주문번호"] = "0012345", ["원주문번호"] = "0000000", ["종목코드"] = "A005930", ["주문수량"] = "10", ["미체결수량"] = "6", ["주문가격"] = "70000", ["주문구분"] = "+매수" },
                new() { ["주문번호"] = "0012346", ["원주문번호"] = "0012300", ["종목코드"] = "000660", ["주문수량"] = "3", ["미체결수량"] = "3", ["주문가격"] = "120000", ["주문구분"] = "-매도정정" },
            };
            c.Core.OnTrData("OPENORD", "opt10075");
            Assert.Equal(2, c.Rows.Count);
            var a = c.Rows[0]; var b = c.Rows[1];
            Assert.Equal((1L, 0, 2, 12345L, 0L, 5930L, 10L, 6L, 70_000L, 1), (a.BatchId, a.RowIndex, a.RowCount, a.VenueOrderNo, a.OrigVenueOrderNo, a.SymbolCode, a.OrderQty, a.RemainingQty, a.OrderPriceWon, a.Side));
            Assert.Equal((1L, 1, 12346L, 12300L, 660L, 3L, 120_000L, -1), (b.BatchId, b.RowIndex, b.VenueOrderNo, b.OrigVenueOrderNo, b.SymbolCode, b.RemainingQty, b.OrderPriceWon, b.Side));
            c.Ocx.TrRows["OPENORD"].Clear();
            c.Core.OnTrData("OPENORD", "opt10075");
            Assert.Equal((2L, 0, 0, 0L), (c.Rows[2].BatchId, c.Rows[2].RowIndex, c.Rows[2].RowCount, c.Rows[2].VenueOrderNo));   // ★빈 결과도 배치 완결 통지
            Assert.Equal(2, c.Core.OpenOrderBatches);
        }

        [Fact]
        public void Core_EmitsMapped_OnAcceptAndModify_AdoptMapping_CancelByVenueNo()
        {
            var c = MakeCore();
            Assert.Equal(0, c.Core.SendOrder(31, "5001", Account, Code, 1, 70_000, 10));
            c.Ocx.OrderNoReply = "0012345";
            c.Core.OnTrData("RQ31", "x");
            Assert.Equal((31L, 12345L), (c.Maps[0].ClientId, c.Maps[0].VenueOrderNo));   // ★접수 사상 통지
            Assert.Equal(KiwoomAmendResult.Sent, c.Core.ModifyOrder(31, 69_900, 10));
            c.Ocx.OrderNoReply = "0012399";
            c.Core.OnTrData("RM31", "x");
            Assert.Equal((31L, 12399L), (c.Maps[1].ClientId, c.Maps[1].VenueOrderNo));   // ★정정 새 번호 통지

            // 새 프록시(사상표 공백) — 승계 사상 재등록 후 체결 통보가 호스트 클라이언트 ID 로 사상
            var n = MakeCore();
            Assert.Equal(KiwoomAmendResult.NoAccount, n.Core.CancelByVenueNo(1, Code, 1, 1));
            Assert.Equal(0, n.Core.QueryOpenOrders(Account, "5001"));
            Assert.False(n.Core.AdoptMapping(77, 22222, Code, 1, 0));
            Assert.True(n.Core.AdoptMapping(77, 22222, Code, 1, 4));
            Assert.Equal(1, n.Core.AdoptedMappings);
            n.Ocx.SetChejan(22222, "체결", "+매수", 4, 0);
            n.Ocx.Chejan[904] = "";
            n.Core.OnChejan("0");
            Assert.Equal(77, n.Fills[^1].OrderId);
            Assert.Equal(4, n.Fills[^1].FillQty);

            Assert.Equal(KiwoomAmendResult.InvalidQuantity, n.Core.CancelByVenueNo(33333, Code, -1, 0));
            Assert.Equal(KiwoomAmendResult.Sent, n.Core.CancelByVenueNo(33333, Code, -1, 7));
            Assert.Equal(("RX33333", 4, "005930", 7, 0, "0033333"), n.Ocx.Orders[^1]);   // ★매도취소 · 원주문번호 7자리 · 'RX' = 응답 무시
            Assert.Equal(1, n.Core.GhostCancels);
            n.Core.OnTrData("RX33333", "x");                                          // 응답이 신규/정정 사상으로 오분류되지 않음
            Assert.Empty(n.Maps);
        }

        // ════════════ §2 판독기 ════════════

        [Fact]
        public void KiwoomRow_Validation()
        {
            Assert.True(BrokerOpenOrderRow.TryFromKiwoom(Row(1, 0, 1, 500, 3), out var ok));
            Assert.Equal((500L, 3L, 1, OpenOrderVenueKind.Kiwoom), (ok.VenueOrderNo, ok.RemainingQty, ok.Side, ok.Venue));
            Assert.False(BrokerOpenOrderRow.TryFromKiwoom(Row(1, 0, 1, 500, 3, side: 0), out _));
            Assert.False(BrokerOpenOrderRow.TryFromKiwoom(Row(1, 0, 1, 0, 3), out _));
            Assert.False(BrokerOpenOrderRow.TryFromKiwoom(Row(1, 0, 1, 500, 3, sym: 0), out _));
            Assert.False(BrokerOpenOrderRow.TryFromKiwoom(Row(1, 0, 1, 500, -1), out _));
            Assert.Equal((80, 24, 64), (Marshal.SizeOf<OpenOrderPayload>(), Marshal.SizeOf<OrderMappedPayload>(), Marshal.SizeOf<BrokerOpenOrderRow>()));
            Assert.Equal((13, 14), ((int)BridgeMessageType.OpenOrder, (int)BridgeMessageType.OrderMapped));
            Assert.Equal((8, 9, 10), ((int)OrderRequestType.QueryOpenOrders, (int)OrderRequestType.AdoptMapping, (int)OrderRequestType.CancelByVenueNo));
        }

        private const string KisPage =
            "{\"ctx_area_fk100\":\"x\",\"ctx_area_nk100\":\"y\",\"output1\":[" +
            "{\"odno\":\"0000117057\",\"orgn_odno\":\"\",\"pdno\":\"005930\",\"ord_qty\":\"10\",\"tot_ccld_qty\":\"4\",\"rmn_qty\":\"6\",\"ord_unpr\":\"70000\",\"sll_buy_dvsn_cd\":\"02\",\"cncl_yn\":\"N\"}," +
            "{\"odno\":\"0000117058\",\"orgn_odno\":\"\",\"pdno\":\"000660\",\"ord_qty\":\"5\",\"rmn_qty\":\"5\",\"ord_unpr\":\"120000.00\",\"sll_buy_dvsn_cd\":\"01\",\"cncl_yn\":\"Y\"}," +
            "{\"odno\":\"0000117059\",\"orgn_odno\":\"0000117001\",\"pdno\":\"035720\",\"ord_qty\":\"3\",\"rmn_qty\":\"0\",\"ord_unpr\":\"50000\",\"sll_buy_dvsn_cd\":\"01\",\"cncl_yn\":\"N\"}," +
            "{\"odno\":\"0000117060\",\"orgn_odno\":\"\",\"pdno\":\"A035420\",\"ord_qty\":\"2\",\"rmn_qty\":\"2\",\"ord_unpr\":\"200000.00\",\"sll_buy_dvsn_cd\":\"01\",\"cncl_yn\":\"N\"}]," +
            "\"output2\":{\"tot_ord_qty\":\"20\"},\"rt_cd\":\"0\",\"msg_cd\":\"KIOK0000\",\"msg1\":\"ok\"}";

        [Fact]
        public void Kis_Tttc8001R_Parse_FailClosed_ZeroAlloc()
        {
            byte[] page = Encoding.UTF8.GetBytes(KisPage);
            var dst = new BrokerOpenOrderRow[8];
            Assert.Equal(2, KisProtocol.ParseOpenOrders(page, dst));               // 취소 행 · 잔량 0 행 제외
            Assert.Equal((117057L, 5930L, 10L, 6L, 70_000L, 1, OpenOrderVenueKind.Kis), (dst[0].VenueOrderNo, dst[0].SymbolCode, dst[0].OrderQty, dst[0].RemainingQty, dst[0].PriceWon, dst[0].Side, dst[0].Venue));
            Assert.Equal((117060L, 35420L, 200_000L, -1), (dst[1].VenueOrderNo, dst[1].SymbolCode, dst[1].PriceWon, dst[1].Side));
            Assert.Equal(-2, KisProtocol.ParseOpenOrders(page, new BrokerOpenOrderRow[1]));
            Assert.Equal(-1, KisProtocol.ParseOpenOrders(Encoding.UTF8.GetBytes(KisPage.Replace("\"rt_cd\":\"0\"", "\"rt_cd\":\"1\"")), dst));
            Assert.Equal(-1, KisProtocol.ParseOpenOrders(Encoding.UTF8.GetBytes(KisPage.Replace(",\"rt_cd\":\"0\"", "")), dst));
            Assert.Equal(-1, KisProtocol.ParseOpenOrders(page.AsSpan(0, page.Length - 40), dst));    // 절단
            Assert.Equal(-1, KisProtocol.ParseOpenOrders(Encoding.UTF8.GetBytes(KisPage.Replace("\"70000\"", "\"70000.5\"")), dst));
            Assert.Equal(-1, KisProtocol.ParseOpenOrders(Encoding.UTF8.GetBytes(KisPage.Replace("\"sll_buy_dvsn_cd\":\"02\"", "\"sll_buy_dvsn_cd\":\"03\"")), dst));
            Assert.Equal(-1, KisProtocol.ParseOpenOrders(Encoding.UTF8.GetBytes(KisPage.Replace("\"rmn_qty\":\"6\"", "\"rmn_qty\":\"16\"")), dst));   // 잔량 > 주문수량
            Assert.Equal(0, MinAllocated(() => KisProtocol.ParseOpenOrders(page, dst)));
        }

        // ════════════ §3 대사기 ════════════

        [Fact]
        public void Rollover_AllAdoptedMatch_UnblocksBit20_ZeroMs_AndAdoptsMapping()
        {
            string name = "Local\\MS2E_TEST_OOR_" + Guid.NewGuid().ToString("N");
            var old = MakeExec();
            Track(old, 1, 1001, 12345, 10);
            Track(old, 2, 1002, 12346, 5, buy: false, sym: 660, limit: 120_000);
            old.Feed.OnFill(Fill(1001, 4));                                         // 잔량 6
            using var pub = ProcessRolloverGuard.CreatePublisher(name, 16);
            Assert.Equal(RolloverPublishResult.Published, pub.Publish(old.Feed, 7));
            using var adopter = ProcessRolloverGuard.OpenAdopter(name, 16)!;
            var mask = new ArmingInterlockBitmask(ArmingInterlockSource.Rollover, ArmingInterlockSource.OpenOrderDivergence);
            adopter.BindInterlock(mask, ArmingInterlockSource.Rollover);
            var exec = MakeExec();
            Assert.Equal(RolloverAdoptResult.Adopted, adopter.TryAdopt(exec.Feed));
            var r = MakeRig(exec, adopter.MarkReconciled, mask: mask);
            Assert.True(mask.IsRaised(ArmingInterlockSource.Rollover) && mask.IsRaised(ArmingInterlockSource.OpenOrderDivergence));

            Assert.True(r.R.Pump());
            Assert.Equal(1, r.V.Requests);
            r.Feed(Row(1, 0, 2, 12345, 6), Row(1, 1, 2, 12346, 5, side: -1, sym: 660, price: 120_000));
            Assert.Equal(OpenOrderVerdict.Confirmed, r.Last.Verdict);
            Assert.Equal((2, 0, 0), (r.Last.Matched, r.Last.Mismatched, r.Last.Ghosts));
            Assert.False(mask.IsRaised(ArmingInterlockSource.Rollover));             // ★Bit 20 해제(대사 확정 즉시)
            Assert.False(mask.IsRaised(ArmingInterlockSource.OpenOrderDivergence));
            Assert.True(adopter.Reconciled);
            Assert.Equal(2, r.V.Adopts);                                             // ★프록시 사상 재등록(이후 체결 통보 사상)
            Assert.Equal((1001L, 12345L, 6L), (r.V.AdoptClient[0], r.V.AdoptVenue[0], r.V.AdoptRem[0]));
            Assert.Equal((1002L, 12346L, 5L), (r.V.AdoptClient[1], r.V.AdoptVenue[1], r.V.AdoptRem[1]));
            Assert.True(r.R.AdoptMappingSent);
        }

        [Fact]
        public void Rollover_RemainingMismatch_DivergenceSticky_Bit20Held_RearmNeedsDualKeyAndFreshConfirm()
        {
            var r = MakeRig();
            r.E.Feed.ImportSlot(new HandoffSlot { ProposalId = 1, BrokerOrderId = 1001, OrderQty = 10, Filled = 4, Broker = 0, Flags = 1 | 2 | 4, VenueOrderNo = 12345, SymbolCode = Code, LimitPriceWon = Limit });
            Assert.True(r.R.Pump());
            r.Feed(Row(1, 0, 1, 12345, 4));                                          // 브로커 잔량 4 ≠ 호스트 6(승계 공백 중 체결 통보 유실)
            Assert.Equal(OpenOrderVerdict.Divergence, r.Last.Verdict);
            Assert.True(r.R.DivergenceDetected);
            Assert.True(r.Mask.IsRaised(ArmingInterlockSource.OpenOrderDivergence));
            Assert.Equal(0, r.Marked);                                               // ★Rollover 해제 금지
            r.Advance(4); Assert.True(r.R.Pump());
            r.Feed(Row(2, 0, 1, 12345, 6));                                          // 다음 패스가 일치해도 sticky
            Assert.Equal(OpenOrderVerdict.Confirmed, r.Last.Verdict);
            Assert.Equal(0, r.Marked);
            Assert.True(r.Mask.IsRaised(ArmingInterlockSource.OpenOrderDivergence));
            Assert.False(r.R.TryRearm(true, false));
            Assert.True(r.R.TryRearm(true, true));
            Assert.True(r.Mask.IsRaised(ArmingInterlockSource.OpenOrderDivergence)); // 재무장 후에도 새 대사 확정 전 차단
            Assert.True(r.R.Pump());
            r.Feed(Row(3, 0, 1, 12345, 6));
            Assert.False(r.Mask.IsRaised(ArmingInterlockSource.OpenOrderDivergence));
            Assert.Equal(1, r.Marked);
        }

        [Fact]
        public void PriceOrSideMismatch_And_MissingAtBroker_AreDivergence()
        {
            var r = MakeRig();
            Track(r.E, 1, 1001, 500, 10);
            Assert.True(r.R.Pump());
            r.Feed(Row(1, 0, 1, 500, 10, price: 70_100));                            // 지정가 100원 괴리
            Assert.Equal(OpenOrderVerdict.Divergence, r.Last.Verdict);

            var s = MakeRig();
            Track(s.E, 1, 1001, 500, 10);
            Assert.True(s.R.Pump());
            s.Feed(Row(1, 0, 1, 500, 10, side: -1));
            Assert.Equal(OpenOrderVerdict.Divergence, s.Last.Verdict);

            var m = MakeRig();
            Track(m.E, 1, 1001, 500, 10);
            Assert.True(m.R.Pump());
            m.Feed(Row(1, 0, 0, 0, 0));                                              // 브로커 미체결 0건 — 호스트는 미체결 10주 보유
            Assert.Equal(OpenOrderVerdict.Divergence, m.Last.Verdict);
            Assert.Equal(1, m.Last.Mismatched);

            var p = MakeRig();
            Track(p.E, 1, 1001, 500, 10);
            Assert.True(p.R.Pump());
            p.Feed(Row(1, 0, 1, 500, 10, price: 70_001));                            // ±1원(가격 반올림) 허용
            Assert.Equal(OpenOrderVerdict.Confirmed, p.Last.Verdict);
        }

        [Fact]
        public void Ghost_DisarmBeforeCancel_CancelAllWithinBudget_StateDrivenNoQueue()
        {
            var r = MakeRig(maxCancels: 4);
            r.V.BlockedProbe = () => r.Mask.IsRaised(ArmingInterlockSource.OpenOrderDivergence) && r.R.GhostDetected;
            Track(r.E, 1, 1001, 500, 10);
            Assert.True(r.R.Pump());
            r.Feed(Row(1, 0, 6, 500, 10), Row(1, 1, 6, 900, 1), Row(1, 2, 6, 901, 2), Row(1, 3, 6, 902, 3), Row(1, 4, 6, 903, 4), Row(1, 5, 6, 904, 5));
            Assert.Equal(OpenOrderVerdict.Ghost, r.Last.Verdict);
            Assert.Equal((5, 4), (r.Last.Ghosts, r.Last.CancelsSent));                // ★패스당 상한 4(키움 주문 TR 초당 5회 미만)
            Assert.Equal(4, r.V.CancelsWhileBlocked);                                // ★Disarm(비트 Raise) 이 모든 취소 송신보다 먼저
            Assert.Equal(new long[] { 500, 900, 901, 902 }, r.V.CancelNo[..4]);      // 비상 일괄 취소 = 자기 주문 포함 전 행

            r.Advance(3);
            Assert.True(r.R.Pump());                                                 // 유령 정리 중 = 3초 고속 재조회
            r.Feed(Row(2, 0, 6, 500, 10), Row(2, 1, 6, 900, 1), Row(2, 2, 6, 901, 2), Row(2, 3, 6, 902, 3), Row(2, 4, 6, 903, 4), Row(2, 5, 6, 904, 5));
            Assert.Equal(2, r.Last.CancelsSent);                                     // 5초 내 중복 억제 → 미송신 행만(대기열 아님 · 새 조회 상태 구동)
            Assert.Equal(new long[] { 903, 904 }, r.V.CancelNo[4..6]);

            r.E.Feed.OnFill(CancelConfirm(1001));
            r.Advance(3);
            Assert.True(r.R.Pump());
            r.Feed(Row(3, 0, 0, 0, 0));
            Assert.Equal(OpenOrderVerdict.Confirmed, r.Last.Verdict);
            Assert.True(r.R.GhostDetected);                                          // ★유령 sticky — 재무장(물리 이중키) 전 차단 유지
            Assert.True(r.Mask.IsRaised(ArmingInterlockSource.OpenOrderDivergence));
            Assert.Equal(6, r.R.CancelsSent);
        }

        [Fact]
        public void CrashOrphan_NoHandoff_FirstPassGhost_And_InitialReconcileRequired()
        {
            var fresh = MakeRig();
            Assert.True(fresh.Mask.IsRaised(ArmingInterlockSource.OpenOrderDivergence)); // ★최초 대사 전 신규 발주 차단
            Assert.True(fresh.R.Pump());
            fresh.Feed(Row(1, 0, 0, 0, 0));
            Assert.Equal(OpenOrderVerdict.Confirmed, fresh.Last.Verdict);
            Assert.False(fresh.Mask.IsRaised(ArmingInterlockSource.OpenOrderDivergence));
            Assert.True(fresh.R.InitialReconciled);

            var crash = MakeRig();                                                   // 이전 프로세스가 승계 없이 종료 — 브로커에만 주문 잔존
            Assert.True(crash.R.Pump());
            crash.Feed(Row(1, 0, 1, 777, 9));
            Assert.Equal(OpenOrderVerdict.Ghost, crash.Last.Verdict);
            Assert.Equal(1, crash.V.Cancels);
            Assert.Equal((777L, 9L), (crash.V.CancelNo[0], crash.V.CancelQty[0]));
        }

        [Fact]
        public void SnapshotRace_FillFreeNewAfterQuery_NeverMisjudged()
        {
            // 조회 송신 후 부분 체결 — 브로커 스냅샷은 체결 전 잔량 → 보류(불일치 오판 0) → 다음 패스 확정
            var r = MakeRig();
            Track(r.E, 1, 1001, 500, 10);
            Assert.True(r.R.Pump());
            r.E.Feed.OnFill(Fill(1001, 4));
            r.Feed(Row(1, 0, 1, 500, 10));
            Assert.Equal(OpenOrderVerdict.Inconclusive, r.Last.Verdict);
            Assert.False(r.R.DivergenceDetected);
            r.Advance(3); Assert.True(r.R.Pump());
            r.Feed(Row(2, 0, 1, 500, 6));
            Assert.Equal(OpenOrderVerdict.Confirmed, r.Last.Verdict);

            // 조회 송신 후 완전 체결(슬롯 해제) — 스냅샷에 남은 행은 유령 아님
            var f = MakeRig();
            Track(f.E, 1, 1001, 500, 10);
            Assert.True(f.R.Pump());
            f.E.Feed.OnFill(Fill(1001, 10));
            f.Feed(Row(1, 0, 1, 500, 10));
            Assert.Equal(OpenOrderVerdict.Inconclusive, f.Last.Verdict);
            Assert.False(f.R.GhostDetected);
            Assert.Equal(0, f.V.Cancels);

            // 조회 송신 후 신규 송출 — 스냅샷에 없음 = 누락 아님
            var n = MakeRig();
            Assert.True(n.R.Pump());
            Track(n.E, 1, 1001, 500, 10);
            n.Feed(Row(1, 0, 0, 0, 0));
            Assert.Equal(OpenOrderVerdict.Inconclusive, n.Last.Verdict);
            Assert.False(n.R.DivergenceDetected);
        }

        [Fact]
        public void UnmappedOrder_HeldNotGhost_MappingBeforeBind_AppliedOnBind()
        {
            var r = MakeRig();
            Track(r.E, 1, 1001, 0, 10);                                              // 사상 통지 미도착
            Assert.True(r.R.Pump());
            r.Feed(Row(1, 0, 1, 777, 10));
            Assert.Equal(OpenOrderVerdict.Inconclusive, r.Last.Verdict);             // ★번호 미상 건과 방향·종목 일치 = 보류(유령 오판 금지)
            Assert.False(r.R.GhostDetected);

            // 정정 새 번호(사상 미도착) — 원주문번호로 연결 = 보류
            var m = MakeRig();
            Track(m.E, 1, 1001, 500, 10);
            Assert.True(m.E.Feed.TryBeginModify(1001, 10, 70_000));                  // 정정 진행 중(창 5초)
            Assert.True(m.R.Pump());
            m.Feed(Row(1, 0, 1, 501, 10, orig: 500));
            Assert.Equal(OpenOrderVerdict.Inconclusive, m.Last.Verdict);
            Assert.False(m.R.GhostDetected);

            var e = MakeExec();
            var key = RouterFeedGate.KeyFor(3);
            Assert.True(e.Router.Route(key, Limit * 100_000_000, Limit * 100_000_000, 0.0).Accepted);
            Assert.True(e.Feed.TryTrack(3, key, 0, 5, true, Limit * 100_000_000, Code, Limit));
            e.Feed.OnOrderMapped(new OrderMappedPayload { ClientId = 1003, VenueOrderNo = 888 });   // 결속 전 선도착
            Assert.Equal(1, e.Feed.MappedPending);
            Assert.True(e.Feed.BindBrokerOrder(3, 1003));
            Span<OpenOrderLedgerEntry> led = new OpenOrderLedgerEntry[4];
            Assert.Equal(1, e.Feed.CopyOpenOrderLedger(led));
            Assert.Equal((888L, 5930L, 70_000L, 5L, 1), (led[0].VenueOrderNo, led[0].SymbolCode, led[0].LimitPriceWon, led[0].Remaining, led[0].Side));
            Assert.Equal(1, e.Feed.MappedApplied);
        }

        [Fact]
        public void BatchIntegrity_Unsolicited_BadIndex_BatchSwitch_Timeout_PageCap_ProxyDown()
        {
            var r = MakeRig();
            r.Feed(Row(1, 0, 1, 500, 1));                                            // 요청 없는 응답 = 스냅샷 기준 없음 → 폐기
            Assert.Empty(r.Passes);
            Assert.Equal(1, r.R.RowsRejected);

            Assert.True(r.R.Pump());
            r.Feed(Row(1, 5, 2, 500, 1));                                            // 행 번호 범위 이탈
            Assert.Equal(OpenOrderVerdict.Incomplete, r.Last.Verdict);
            Assert.False(r.R.InitialReconciled);

            r.Advance(3); Assert.True(r.R.Pump());
            r.Feed(Row(5, 0, 2, 500, 1));
            r.Feed(Row(6, 0, 0, 0, 0));                                              // 배치 교체 — 이전 배치 미완 폐기
            Assert.Equal(OpenOrderVerdict.Confirmed, r.Last.Verdict);
            Assert.True(r.R.IncompleteBatches >= 2);

            var t = MakeRig();
            Assert.True(t.R.Pump());
            t.Advance(2); Assert.False(t.R.Pump());                                  // 응답 대기 중 · 재송신 없음
            t.Advance(9); Assert.True(t.R.Pump());                                   // 10초 시한 초과 → 보류 · 재조회
            Assert.Equal((1L, 2), (t.R.QueryTimeouts, t.V.Requests));

            var cap = MakeRig(pageCap: 2);
            Assert.True(cap.R.Pump());
            cap.Feed(Row(1, 0, 2, 900, 1), Row(1, 1, 2, 901, 1));
            Assert.Equal(OpenOrderVerdict.Inconclusive, cap.Last.Verdict);           // 연속 조회 필요 가능성 — 유령 판정 보류
            Assert.False(cap.R.GhostDetected);

            var down = MakeRig();
            down.V.Alive = false;
            Assert.False(down.R.Pump());
            Assert.False(down.R.QueryPending);
        }

        [Fact]
        public void Kis_Path_ConfirmsByDecodedOrderNo_GhostBlocksWithoutCancelWiring()
        {
            var r = MakeRig(requireKiwoom: false, requireKis: true, venue: false);
            Track(r.E, 1, KisOrderIdSpace.Encode(117057), 0, 6);
            Assert.True(r.Mask.IsRaised(ArmingInterlockSource.OpenOrderDivergence));
            r.R.BeginSnapshot(OpenOrderVenueKind.Kis);
            string page1 = "{\"output1\":[{\"odno\":\"0000117057\",\"orgn_odno\":\"\",\"pdno\":\"005930\",\"ord_qty\":\"6\",\"rmn_qty\":\"6\",\"ord_unpr\":\"70000\",\"sll_buy_dvsn_cd\":\"02\",\"cncl_yn\":\"N\"}],\"rt_cd\":\"0\"}";
            r.R.OnKisPage(Encoding.UTF8.GetBytes(page1), last: false);
            Assert.Empty(r.Passes);
            r.R.OnKisPage(Encoding.UTF8.GetBytes("{\"output1\":[],\"rt_cd\":\"0\"}"), last: true);
            Assert.Equal(OpenOrderVerdict.Confirmed, r.Last.Verdict);
            Assert.False(r.Mask.IsRaised(ArmingInterlockSource.OpenOrderDivergence));

            r.R.BeginSnapshot(OpenOrderVenueKind.Kis);
            r.R.OnKisPage(Encoding.UTF8.GetBytes(KisPage), last: true);              // 117060 = 호스트 미추적
            Assert.Equal(OpenOrderVerdict.Ghost, r.Last.Verdict);
            Assert.Equal(0, r.Last.CancelsSent);                                     // KIS 취소 송신 미배선(정직) — 차단만
            Assert.True(r.Mask.IsRaised(ArmingInterlockSource.OpenOrderDivergence));

            var bad = MakeRig(requireKiwoom: false, requireKis: true, venue: false);
            bad.R.BeginSnapshot(OpenOrderVenueKind.Kis);
            bad.R.OnKisPage(Encoding.UTF8.GetBytes("{\"output1\":[{\"odno\":\"1\""), last: true);
            Assert.Equal(OpenOrderVerdict.Incomplete, bad.Last.Verdict);
            Assert.False(bad.R.InitialReconciled);
        }

        [Fact]
        public void Forensics_HashChainAndBlackbox_RecordPassDivergenceGhostCancel()
        {
            var stream = new ZeroAllocForensicAuditStream(64);
            var flight = new MmfFlightRecorder(1024);
            var r = MakeRig();
            r.R.AttachForensics(stream, flight);
            Track(r.E, 1, 1001, 500, 10);
            Assert.True(r.R.Pump());
            r.Feed(Row(1, 0, 2, 500, 9), Row(1, 1, 2, 990, 1));
            Assert.Equal(OpenOrderVerdict.Ghost, r.Last.Verdict);
            Assert.Equal(1, stream.WriteHead);
            var ev = new FlightRecordEvent[16];
            int n = flight.SnapshotRecent(ev);
            var kinds = new List<FlightEventKind>();
            for (int i = 0; i < n; i++) kinds.Add(ev[i].Event);
            Assert.Contains(FlightEventKind.OpenOrderDivergence, kinds);
            Assert.Contains(FlightEventKind.GhostOrderCancel, kinds);
            Assert.Contains(FlightEventKind.OpenOrderReconciled, kinds);
            long tag = BrokerOpenOrderReconciler.PackTag(OpenOrderVerdict.Ghost, OpenOrderVenueKind.Kiwoom, 1, 1, 2);
            Assert.Equal((4L, 1L, 1L, 1L, 2L), (tag >> 56, (tag >> 48) & 0xFF, (tag >> 32) & 0xFFFF, (tag >> 16) & 0xFFFF, tag & 0xFFFF));
            Assert.Equal((73, 74, 75), ((int)FlightEventKind.OpenOrderReconciled, (int)FlightEventKind.OpenOrderDivergence, (int)FlightEventKind.GhostOrderCancel));
        }

        [Fact]
        public void SteadyStatePass_ZeroAlloc_Release()
        {
            var r = MakeRig(record: false);
            r.R.AttachForensics(new ZeroAllocForensicAuditStream(1024), new MmfFlightRecorder(4096));
            var rows = new OpenOrderPayload[8];
            for (int i = 0; i < 8; i++) { Track(r.E, i + 1, 1001 + i, 500 + i, 10); rows[i] = Row(0, i, 8, 500 + i, 10); }
            long batch = 0;
            Action body = () =>
            {
                r.Advance(31);
                r.R.Pump();
                batch++;
                for (int i = 0; i < 8; i++) { var p = rows[i]; p.BatchId = batch; r.R.OnKiwoomRow(p); }
            };
            body();
            Assert.True(r.R.InitialReconciled);
            Assert.Equal(0, MinAllocated(body));                                     // ★조회 구동 + 8행 수신 + 1:1 대사 + 감사/블랙박스 δ=0
            Assert.Equal(r.R.Passes, r.R.ConfirmedPasses);

            var k = MakeRig(requireKiwoom: false, requireKis: true, venue: false, record: false);
            byte[] page = Encoding.UTF8.GetBytes(KisPage);
            Action kis = () => { k.R.BeginSnapshot(OpenOrderVenueKind.Kis); k.R.OnKisPage(page, last: true); };
            kis();
            Assert.Equal(0, MinAllocated(kis));
        }

        // ════════════ §4 슬롯 v2 · 결선 ════════════

        [Fact]
        public void HandoffSlotV2_CarriesVenueSymbolLimit_RoundTrip()
        {
            Assert.Equal(96, Marshal.SizeOf<HandoffSlot>());
            var a = MakeExec();
            Track(a, 1, 1001, 12345, 10, buy: false, sym: 660, limit: 120_000);
            Span<HandoffSlot> s = new HandoffSlot[4];
            Assert.Equal(1, a.Feed.ExportSlots(s));
            Assert.Equal((12345L, 660L, 120_000L), (s[0].VenueOrderNo, s[0].SymbolCode, s[0].LimitPriceWon));
            var b = MakeExec();
            Assert.True(b.Feed.ImportSlot(s[0]));
            Span<OpenOrderLedgerEntry> led = new OpenOrderLedgerEntry[4];
            Assert.Equal(1, b.Feed.CopyOpenOrderLedger(led));
            Assert.Equal((12345L, 660L, 120_000L, -1, OpenOrderLedgerEntry.FlagAdopted), (led[0].VenueOrderNo, led[0].SymbolCode, led[0].LimitPriceWon, led[0].Side, led[0].Flags));
            var bad = s[0]; bad.BrokerOrderId = 2002; bad.VenueOrderNo = -1;
            Assert.False(b.Feed.ImportSlot(bad));
        }

        private static string RepoRoot()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d is not null && !File.Exists(Path.Combine(d.FullName, "StockManager.sln"))) d = d.Parent;
            Assert.NotNull(d);
            return d!.FullName;
        }

        [Fact]
        public void Host_And_Proxy_Wiring()
        {
            const BindingFlags AllF = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
            var host = typeof(StockManager.Blazor.BlazorHomeForm);
            var targets = new MethodBase[]
            {
                typeof(BrokerOpenOrderReconciler).GetConstructors()[0],
                typeof(BrokerOpenOrderReconciler).GetMethod(nameof(BrokerOpenOrderReconciler.BindInterlock))!,
                typeof(BrokerOpenOrderReconciler).GetMethod(nameof(BrokerOpenOrderReconciler.Pump))!,
                typeof(BrokerOpenOrderReconciler).GetMethod(nameof(BrokerOpenOrderReconciler.AttachForensics))!,
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
            Assert.Contains("MS2E.Core.ArmingInterlockSource.OpenOrderDivergence,", form);   // 필수 비트
            Assert.Contains("new StockManager.Adapters.BrokerOpenOrderReconciler(execFeed, broker, rolloverGuard.MarkReconciled,", form);
            Assert.Contains("bridge.OpenOrderReceived   += onOpenOrderRow;", form);
            Assert.Contains("bridge.OrderMappedReceived += onOrderMapped;", form);
            Assert.Contains("Action<OrderMappedPayload> onOrderMapped = execFeed.OnOrderMapped;", form);
            Assert.Contains("p.SymbolCode, limit / 100_000_000)", File.ReadAllText(Path.Combine(root, "StockManager", "Adapters", "RouterFeedGate.cs")));
            string proxyHost = File.ReadAllText(Path.Combine(root, "StockManager.KiwoomProxy", "KiwoomProxyHost.cs"));
            Assert.Contains("SendFrame(BridgeMessageType.OpenOrder, in p);", proxyHost);
            Assert.Contains("SendFrame(BridgeMessageType.OrderMapped, in p);", proxyHost);
            Assert.Contains("case OrderRequestType.CancelByVenueNo:", proxyHost);
            Assert.Contains("_api.AdoptMapping(req.OrderId, req.GenomeId, req.SymbolCode, req.Pad, req.Quantity);", proxyHost);
            string prod = File.ReadAllText(Path.Combine(root, "StockManager.KiwoomProxy", "ProductionKiwoomApi.cs"));
            Assert.Contains("onOpenOrder: row => OpenOrderReceived?.Invoke", prod);
            Assert.Contains("onMapped: m => OrderMapped?.Invoke", prod);
            Assert.Equal(22, (int)ArmingInterlockSource.OpenOrderDivergence);
            Assert.True(typeof(IOpenOrderVenue).IsAssignableFrom(typeof(ExecutionBridgeBroker)));
        }
    }
}
