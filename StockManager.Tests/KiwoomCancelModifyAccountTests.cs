// ============================================================================
// KiwoomCancelModifyAccountTests — [정정/취소·계좌 · 추천 A] 정정/취소 파이프라인 + 계좌 상태 동기화(δ=0) + 방향 A/B
//
//  §1 코어 정정/취소 — 원주문번호(D7) 사상 · 매수/매도 3·4(취소) 5·6(정정) · 잔량 초과/중복/미접수 차단 · 사상 전 취소 보류 집행.
//  §2 [실결함] 취소/정정은 새 주문번호 — 수정 전: 취소확인 통보가 미사상 번호로 보류→만료 → 호스트 슬롯 TTL 잔존(sticky Disarm).
//     교정: RC 응답 사상 + FID 904 원주문번호 폴백 + 대체된 구 번호의 비체결 통보 무시.
//  §3 코어 계좌 TR — OPW00018 보유 행 배치 · OPW00001 예수금/주문가능금액 · 조회 입력값.
//  §4 호스트 전방 게이트 — 종결/중복/잔량 초과 취소·정정 송신 0 · ExecutionBridgeBroker 전방 차단.
//  §5 [방향 A] Cancel-Fill Race 배리어 — 취소 후 체결 반영 · 경합 완전 체결 종결 · 정정 진행 중 잔량 불일치 유예 · 정정 수량 재기준.
//  §6 계좌 동기화 — 주문가능금액 예약 게이트 · 보유수량 매도 게이트 · 배치 조립 · [방향 B] 정적 구간 괴리 Disarm.
//  §7 송출 게이트(RouterFeedGate) 계좌 거부 · 브리지 검증 · δ=0 · 호스트 결선 · 사중 빌드(프록시 링크/계약) 회귀.
// ============================================================================
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using MS2E.Core;
using MS2E.Infrastructure;
using StockManager.Adapters;
using Xunit;

namespace StockManager.Tests
{
    [Collection("ConcurrencyStress")]
    public sealed class KiwoomCancelModifyAccountTests
    {
        private const long Code = 5930;
        private const string Account = "8012345611";

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

        // ── 코어 리그 ──
        private sealed class CoreRig
        {
            public long Now = 1_000_000_000;
            public readonly FakeOcx Ocx = new();
            public readonly List<FillPayload> Fills = new();
            public readonly List<AccountCashPayload> Cash = new();
            public readonly List<PositionPayload> Rows = new();
            public KiwoomComEventCore Core = null!;
        }

        private static CoreRig MakeCore()
        {
            var r = new CoreRig();
            r.Core = new KiwoomComEventCore(r.Ocx, null, r.Fills.Add, null, null, null, () => r.Now, mapCapacityPow2: 64,
                                            onCash: r.Cash.Add, onPosition: r.Rows.Add);
            return r;
        }

        // 신규 송신 → RQ 응답 사상(kiwoomNo) → 부분 체결(filled) 상태까지
        private static void Placed(CoreRig r, long client, long kiwoomNo, int side = 1, long qty = 10, long filled = 0)
        {
            Assert.Equal(0, r.Core.SendOrder(client, "5001", Account, Code, side, 70_000, qty));
            r.Ocx.OrderNoReply = kiwoomNo.ToString("D7");
            r.Core.OnTrData("RQ" + client, "KOA_NORMAL_BUY_KP_ORD");
            if (filled > 0)
            {
                r.Ocx.SetChejan(kiwoomNo, "체결", "+매수", filled, qty - filled);
                r.Ocx.Chejan[904] = "";
                r.Core.OnChejan("0");
            }
        }

        private static BridgeFillStatus St(in FillPayload f) => BridgeFillStatusCodec.Decode(in f, out _);

        // ════════════ §1 코어 정정/취소 ════════════

        [Fact]
        public void Cancel_OriginalOrderNumberD7_RemainingQty_BuyType3_SellType4()
        {
            var r = MakeCore();
            Placed(r, 31, 12345, side: 1, qty: 10, filled: 4);
            Assert.Equal(KiwoomAmendResult.Sent, r.Core.CancelOrder(31));
            var o = r.Ocx.Orders[^1];
            Assert.Equal("RC31", o.Rq);
            Assert.Equal(3, o.Type);                 // 매수취소
            Assert.Equal(6, o.Qty);                  // ★최신 미체결(10 − 4)
            Assert.Equal("0012345", o.OrgNo);        // ★원주문번호 7자리
            Assert.Equal("005930", o.Code);
            Assert.Equal(KiwoomAmendResult.Duplicate, r.Core.CancelOrder(31));   // 중복 취소 송신 0
            Assert.Equal(2, r.Ocx.Orders.Count);

            Placed(r, 32, 12346, side: -1, qty: 5);
            Assert.Equal(KiwoomAmendResult.Sent, r.Core.CancelOrder(32));
            Assert.Equal(4, r.Ocx.Orders[^1].Type);  // 매도취소
            Assert.Equal(5, r.Ocx.Orders[^1].Qty);
        }

        [Fact]
        public void Modify_Type5_6_BlocksOverQuantity_Duplicate_NotAccepted()
        {
            var r = MakeCore();
            Assert.Equal(KiwoomAmendResult.NoAccount, r.Core.ModifyOrder(1, 70_000, 1));   // 계좌 미설정(송신 이력 없음)
            Placed(r, 41, 22345, qty: 10, filled: 4);
            int before = r.Ocx.Orders.Count;
            Assert.Equal(KiwoomAmendResult.ExceedsRemaining, r.Core.ModifyOrder(41, 69_900, 7));   // ★잔량 6 초과 정정 차단
            Assert.Equal(KiwoomAmendResult.InvalidPrice, r.Core.ModifyOrder(41, 0, 3));
            Assert.Equal(KiwoomAmendResult.InvalidQuantity, r.Core.ModifyOrder(41, 69_900, 0));
            Assert.Equal(KiwoomAmendResult.NotFound, r.Core.ModifyOrder(999, 69_900, 1));
            Assert.Equal(before, r.Ocx.Orders.Count);                                              // 전부 송신 0
            Assert.Equal(KiwoomAmendResult.Sent, r.Core.ModifyOrder(41, 69_900, 6));
            var o = r.Ocx.Orders[^1];
            Assert.Equal(("RM41", 5, 6, 69_900, "0022345"), (o.Rq, o.Type, o.Qty, o.Price, o.OrgNo));
            Assert.Equal(KiwoomAmendResult.Duplicate, r.Core.ModifyOrder(41, 69_800, 5));          // 진행 중 정정 중복 차단

            Assert.Equal(0, r.Core.SendOrder(42, "5001", Account, Code, -1, 70_000, 3));
            Assert.Equal(KiwoomAmendResult.NotAccepted, r.Core.ModifyOrder(42, 70_100, 3));        // 주문번호 미사상 — 원주문번호 없음
            r.Ocx.OrderNoReply = "0022346";
            r.Core.OnTrData("RQ42", "x");
            Assert.Equal(KiwoomAmendResult.Sent, r.Core.ModifyOrder(42, 70_100, 3));
            Assert.Equal(6, r.Ocx.Orders[^1].Type);                                                // 매도정정
        }

        [Fact]
        public void Cancel_BeforeOrderNumber_DeferredThenExecutedOnTr()
        {
            var r = MakeCore();
            Assert.Equal(0, r.Core.SendOrder(51, "5001", Account, Code, 1, 70_000, 8));
            Assert.Equal(KiwoomAmendResult.Deferred, r.Core.CancelOrder(51));
            Assert.Equal(KiwoomAmendResult.Duplicate, r.Core.CancelOrder(51));
            Assert.Single(r.Ocx.Orders);                                       // 신규 1건만
            r.Ocx.OrderNoReply = "0032345";
            r.Core.OnTrData("RQ51", "x");
            Assert.Equal(1, r.Core.CancelDeferredExecuted);
            Assert.Equal(("RC51", 3, 8, "0032345"), (r.Ocx.Orders[^1].Rq, r.Ocx.Orders[^1].Type, r.Ocx.Orders[^1].Qty, r.Ocx.Orders[^1].OrgNo));
        }

        [Fact]
        public void Amend_SendFailedOrEmptyTrReply_ClearsFlag_OriginalSurvives()
        {
            var r = MakeCore();
            Placed(r, 61, 42345);
            r.Ocx.SendRc = -308;
            Assert.Equal(KiwoomAmendResult.SendFailed, r.Core.CancelOrder(61));
            r.Ocx.SendRc = 0;
            Assert.Equal(KiwoomAmendResult.Sent, r.Core.CancelOrder(61));       // 송신 실패 = 표지 해제 → 재요청 가능
            r.Ocx.OrderNoReply = "  ";
            r.Core.OnTrData("RC61", "x");                                       // 취소 TR 주문번호 공란 = 취소 실패
            Assert.DoesNotContain(r.Fills, f => St(in f) == BridgeFillStatus.Rejected);   // ★원주문은 거부 아님(생존)
            Assert.Equal(KiwoomAmendResult.Sent, r.Core.CancelOrder(61));       // 취소 실패 → 표지 해제 → 재요청 가능
            Assert.Equal(KiwoomAmendResult.Duplicate, r.Core.ModifyOrder(61, 69_000, 10));   // 취소 진행 중 정정 금지
            r.Core.OnTrData("RC61", "x");
            Assert.Equal(KiwoomAmendResult.Sent, r.Core.ModifyOrder(61, 69_000, 10));
            r.Core.OnTrData("RM61", "x");                                       // 정정 실패 → 정정 표지 해제
            Assert.Equal(KiwoomAmendResult.Sent, r.Core.ModifyOrder(61, 69_000, 10));
            Assert.True(r.Core.AmendFailed >= 2);
        }

        // ════════════ §2 [실결함] 새 주문번호 사상 ════════════

        [Fact]
        public void CancelConfirm_OnNewNumber_ResolvedVia904_BeforeRcReply()
        {
            // [수정 전 Red] 취소확인 통보(9203 = 취소 주문번호)가 미사상 → 보류 → 만료 후 키움 번호 그대로 방출(호스트 UnknownOrder) — 슬롯 잔존
            var r = MakeCore();
            Placed(r, 71, 52345, qty: 10, filled: 3);
            Assert.Equal(KiwoomAmendResult.Sent, r.Core.CancelOrder(71));
            r.Fills.Clear();
            r.Ocx.SetChejan(52399, "확인", "매수취소", 0, 0);
            r.Ocx.Chejan[904] = "0052345";                                       // 원주문번호
            r.Core.OnChejan("0");
            var f = Assert.Single(r.Fills);
            Assert.Equal(71, f.OrderId);                                         // ★호스트 ID 로 즉시 방출
            Assert.Equal(BridgeFillStatus.CancelConfirmed, St(in f));
        }

        [Fact]
        public void CancelConfirm_OnNewNumber_ResolvedByRcReplyMapping()
        {
            var r = MakeCore();
            Placed(r, 72, 62345);
            Assert.Equal(KiwoomAmendResult.Sent, r.Core.CancelOrder(72));
            r.Ocx.OrderNoReply = "0062399";
            r.Core.OnTrData("RC72", "x");                                        // ★취소 주문번호 사상
            r.Fills.Clear();
            r.Ocx.SetChejan(62399, "확인", "매수취소", 0, 0);
            r.Ocx.Chejan[904] = "";
            r.Core.OnChejan("0");
            var f = Assert.Single(r.Fills);
            Assert.Equal(72, f.OrderId);
            Assert.Equal(BridgeFillStatus.CancelConfirmed, St(in f));
        }

        [Fact]
        public void Modify_NewNumberMapped_SupersededOriginalNonFillIgnored_FillsStillEmitted()
        {
            var r = MakeCore();
            Placed(r, 81, 72345, qty: 10, filled: 4);
            Assert.Equal(KiwoomAmendResult.Sent, r.Core.ModifyOrder(81, 69_900, 6));
            r.Ocx.OrderNoReply = "0072399";
            r.Core.OnTrData("RM81", "x");
            r.Fills.Clear();
            r.Ocx.SetChejan(72399, "접수", "+매수정정", 0, 6);
            r.Ocx.Chejan[904] = "0072345";
            r.Core.OnChejan("0");
            var acc = Assert.Single(r.Fills);
            Assert.Equal((81L, BridgeFillStatus.Accepted), (acc.OrderId, St(in acc)));
            // 구 번호의 정정확인(잔량 0) — 현재 주문 상태 아님 → 무시
            r.Ocx.SetChejan(72345, "확인", "+매수정정", 0, 0);
            r.Ocx.Chejan[904] = "";
            r.Core.OnChejan("0");
            Assert.Single(r.Fills);
            Assert.Equal(1, r.Core.SupersededIgnored);
            // 구 번호 체결(정정 전 경합)은 실 노출 — 항상 방출
            r.Ocx.SetChejan(72345, "체결", "+매수", 1, 5);
            r.Core.OnChejan("0");
            Assert.Equal(2, r.Fills.Count);
            Assert.Equal((81L, 1L), (r.Fills[^1].OrderId, r.Fills[^1].FillQty));
        }

        // ════════════ §3 코어 계좌 TR ════════════

        [Fact]
        public void AccountTr_Opw00018Rows_Opw00001Cash_Inputs()
        {
            var r = MakeCore();
            Assert.Equal(0, r.Core.QueryAccount(Account, "5002"));
            Assert.Equal(new[] { ("ACCBAL", "opw00018"), ("ACCCASH", "opw00001") }, r.Ocx.Requests.ToArray());
            Assert.Contains(("계좌번호", Account), r.Ocx.Inputs);
            Assert.Equal(-1, r.Core.QueryAccount("", "5002"));                     // 계좌 미설정 = 요청 0

            r.Ocx.TrRows["ACCBAL"] = new()
            {
                new() { ["종목번호"] = "A005930", ["보유수량"] = "000000000010", ["매입가"] = "000000070000" },
                new() { ["종목번호"] = "A000660", ["보유수량"] = "000000000003", ["매입가"] = "000000150000" },
            };
            r.Core.OnTrData("ACCBAL", "opw00018");
            Assert.Equal(2, r.Rows.Count);
            Assert.Equal((r.Rows[0].BatchId, 0, 2, 5930L, 10L, 70_000L), (r.Rows[0].BatchId, r.Rows[0].RowIndex, r.Rows[0].RowCount, r.Rows[0].SymbolCode, r.Rows[0].Quantity, r.Rows[0].AvgPrice));
            Assert.Equal((1, 660L, 3L), (r.Rows[1].RowIndex, r.Rows[1].SymbolCode, r.Rows[1].Quantity));
            Assert.Equal(r.Rows[0].BatchId, r.Rows[1].BatchId);

            r.Ocx.TrRows["ACCBAL"] = new();
            r.Core.OnTrData("ACCBAL", "opw00018");
            Assert.Equal((0, 0), (r.Rows[^1].RowIndex, r.Rows[^1].RowCount));     // 보유 없음 = 0행 표지
            Assert.NotEqual(r.Rows[0].BatchId, r.Rows[^1].BatchId);

            r.Ocx.TrRows["ACCCASH"] = new() { new() { ["예수금"] = "000000010000000", ["d+2추정예수금"] = "000000009500000", ["주문가능금액"] = "000000005000000" } };
            r.Core.OnTrData("ACCCASH", "opw00001");
            var c = Assert.Single(r.Cash);
            Assert.Equal((10_000_000L, 9_500_000L, 5_000_000L), (c.Deposit, c.D2Deposit, c.OrderableCash));
        }

        // ════════════ §4 호스트 전방 게이트 ════════════

        private sealed class Rig
        {
            public long Now = 1_000_000;
            public readonly List<ReconciliationRecord> Records = new(256);
            public ReferencePriceFeedAdapter Price = null!;
            public ResilientOrderRouter Router = null!;
            public OrderExecutionFeedAdapter Exec = null!;
            public BrokerStateMismatchInterlock Mismatch = null!;
            public AccountStateSyncAdapter? Account;
            public AccountDivergenceInterlock Divergence = null!;
            public RouterFeedGate Gate = null!;
        }

        private static Rig Make(bool withAccount = false, bool records = true)
        {
            var r = new Rig();
            Func<long> clock = () => Volatile.Read(ref r.Now);
            r.Price = new ReferencePriceFeedAdapter(capacityPow2: 64, nowMicros: clock);
            var board = new LockFreeBrokerSwitchboard(1);
            var pending = new PendingOrderCascadeCanceller(64);
            var sentinel = new BrokerFailoverSentinel(board, pendingOrders: pending);
            r.Router = new ResilientOrderRouter(board, sentinel, pending, idMapCapacityPow2: 256, maxSlippageBps: 50);
            r.Mismatch = new BrokerStateMismatchInterlock();
            r.Exec = new OrderExecutionFeedAdapter(r.Router, 64, 120_000_000, clock, records ? rec => r.Records.Add(rec) : null, null, 600_000_000, r.Mismatch);
            r.Divergence = new AccountDivergenceInterlock();
            if (withAccount) r.Account = new AccountStateSyncAdapter(64, nowMicros: clock, interlock: r.Divergence, onRecord: records ? rec => r.Records.Add(rec) : null);
            var breaker = new FeedDisconnectCircuitBreaker(r.Price, () => true);
            r.Gate = new RouterFeedGate(r.Router, r.Price, r.Exec, breaker, requireDepthForMarket: false, account: r.Account);
            r.Price.OnTick(ReferencePriceFeedAdapter.SymbolHashForCode(Code), 70_000, 1);
            breaker.Evaluate();
            return r;
        }

        private static bool TryAdmit(Rig r, long proposal, OrderSide side = OrderSide.Buy, decimal qty = 10m, decimal px = 70_000m)
        {
            var p = new OrderProposal(proposal, 0, Code, side, 1, QPrice.Of(px), QQty.Of(qty), 0);
            return r.Gate.TryAdmit(in p, out _);
        }

        private static void Admit(Rig r, long proposal, long brokerId, decimal qty = 10m)
        {
            Assert.True(TryAdmit(r, proposal, qty: qty));
            r.Gate.OnBrokerAccepted(proposal, brokerId.ToString());
        }

        private static FillPayload Notice(long brokerOrder, BridgeFillStatus st, long qty = 0, long remaining = -1, int side = 1, long symbol = Code)
            => new()
            {
                OrderId = brokerOrder, SymbolCode = symbol, FillPrice = qty > 0 ? 70_000 : 0, FillQty = qty, Side = side, TimestampTicks = 1,
                Status = BridgeFillStatusCodec.Encode(st, remaining >= 0), RemainingQty = remaining >= 0 ? remaining : 0,
            };

        [Fact]
        public void AmendGate_BlocksUnknownTerminalDuplicateOverQuantity()
        {
            var r = Make();
            Admit(r, 1, 301);
            Assert.False(r.Exec.TryBeginCancel(999));                           // 미추적
            Assert.False(r.Exec.TryBeginModify(301, 11, 70_000));               // ★잔량 10 초과 정정
            Assert.False(r.Exec.TryBeginModify(301, 0, 70_000));
            Assert.True(r.Exec.TryBeginModify(301, 10, 69_900));
            Assert.False(r.Exec.TryBeginModify(301, 9, 69_800));                // 진행 중 정정 중복
            Assert.True(r.Exec.TryBeginCancel(301));
            Assert.False(r.Exec.TryBeginCancel(301));                           // ★중복 취소
            Assert.False(r.Exec.TryBeginModify(301, 5, 69_800));                // 취소 진행 중 정정 금지
            r.Now += OrderExecutionFeedAdapter.AmendWindowMicros + 1;           // 확인 무응답 = 실패 간주 → 수동 재요청 허용
            Assert.True(r.Exec.TryBeginCancel(301));
            r.Exec.OnFill(Notice(301, BridgeFillStatus.Filled, 10, 0));          // 완전 체결 = 종결
            Assert.False(r.Exec.TryBeginCancel(301));                           // ★종결 주문 취소 송신 0
            Assert.Equal(7, r.Exec.AmendBlocked);
            Assert.Contains(r.Records, x => x.Kind == ReconciliationKind.AmendBlocked && x.Qty == 11);
        }

        private sealed class CountingGate : IOrderAmendGate
        {
            public bool Allow; public int Cancels, Modifies; public long LastQty, LastPrice;
            public bool TryBeginCancel(long brokerOrderId) { Cancels++; return Allow; }
            public bool TryBeginModify(long brokerOrderId, long newQtyShares, long newPriceWon) { Modifies++; LastQty = newQtyShares; LastPrice = newPriceWon; return Allow; }
        }

        [Fact]
        public void ExecutionBridgeBroker_AmendGate_FrontBlocks()
        {
            var att = new OrderAttestationService();
            var (broker, bridge) = ExecutionBridgeBroker.Create(att, $"CMA_{Guid.NewGuid():N}");
            using (broker) using (bridge)
            {
                var g = new CountingGate { Allow = false };
                broker.AmendGate = g;
                broker.Cancel("301");
                Assert.False(broker.Modify("301", 69_900m, 5m));
                Assert.Equal((1, 1, 2L), (g.Cancels, g.Modifies, broker.AmendBlocked));
                Assert.Equal((5L, 69_900L), (g.LastQty, g.LastPrice));
                Assert.False(broker.Modify("301", 69_900m, 2.5m));                // 소수 수량 — 게이트 이전 거부
                Assert.False(broker.Modify("x", 69_900m, 1m));
                Assert.False(broker.Modify("301", 0m, 1m));
                Assert.Equal(1, g.Modifies);
                g.Allow = true;
                Assert.True(broker.Modify("301", 69_900m, 5m));
                Assert.Equal(2L, broker.AmendBlocked);
            }
        }

        // ════════════ §5 [방향 A] Cancel-Fill Race ════════════

        [Fact]
        public void CancelFillRace_FullFillAfterCancel_ReleasedAsRace_LateConfirmHarmless()
        {
            var r = Make();
            Admit(r, 1, 401);
            Assert.True(r.Exec.TryBeginCancel(401));
            r.Exec.OnFill(Notice(401, BridgeFillStatus.Filled, 10, 0));          // 취소가 닿기 전 완전 체결
            Assert.Equal(0, r.Exec.Tracked);
            Assert.Equal(0, r.Router.TrackedOrders);
            Assert.Equal(1, r.Exec.CancelRaceFilled);
            var rec = Assert.Single(r.Records, x => x.Kind == ReconciliationKind.CancelRaceFilled);
            Assert.Equal(10, rec.Qty);
            r.Exec.OnFill(Notice(401, BridgeFillStatus.CancelConfirmed, 0, 0)); // 뒤늦은 취소 통보 — 미추적 번호
            Assert.Contains(r.Records, x => x.Kind == ReconciliationKind.UnknownOrder && x.BrokerOrderId == 401);
            Assert.False(r.Mismatch.Tripped);
            Assert.False(r.Exec.ArmingBlocked);
        }

        [Fact]
        public void CancelFillRace_PartialThenConfirm_ExposureRecorded()
        {
            var r = Make();
            Admit(r, 2, 402);
            Assert.True(r.Exec.TryBeginCancel(402));
            r.Exec.OnFill(Notice(402, BridgeFillStatus.Filled, 4, 6));
            Assert.Equal(1, r.Exec.Tracked);                                     // 부분 체결 반영 · 슬롯 유지
            r.Exec.OnFill(Notice(402, BridgeFillStatus.CancelConfirmed, 0, 0));
            Assert.Equal(0, r.Exec.Tracked);
            Assert.Equal(0, r.Router.TrackedOrders);
            var rec = Assert.Single(r.Records, x => x.Kind == ReconciliationKind.CancelConfirmed);
            Assert.Equal(4, rec.Qty);                                            // ★취소 전 경합 체결 노출
            Assert.False(r.Mismatch.Tripped);
        }

        [Fact]
        public void AmendInFlight_RemainingMismatchTolerated_OutsideWindowTrips()
        {
            var r = Make();
            Admit(r, 3, 403);
            Assert.True(r.Exec.TryBeginCancel(403));
            r.Exec.OnFill(Notice(403, BridgeFillStatus.Accepted, 0, 0));         // 취소 주문 접수(잔량 0 표기) — 경합 구간
            Assert.False(r.Mismatch.Tripped);
            Assert.Equal(1, r.Exec.AmendTolerated);
            r.Now += OrderExecutionFeedAdapter.AmendWindowMicros + 1;            // 창 밖 = 진짜 불일치
            r.Exec.OnFill(Notice(403, BridgeFillStatus.Accepted, 0, 0));
            Assert.True(r.Mismatch.Tripped);
        }

        [Fact]
        public void Modify_AcceptedWithNewQty_RebasesTrackedQuantity()
        {
            // [수정 전 Red] 정정 반영 없음 → 접수 잔량 5 ≠ 추적 잔량 8 → BrokerStateMismatch 오격발(정상 정정이 전체 Disarm)
            var r = Make();
            Admit(r, 4, 404);
            r.Exec.OnFill(Notice(404, BridgeFillStatus.Filled, 2, 8));
            Assert.True(r.Exec.TryBeginModify(404, 5, 69_900));
            r.Exec.OnFill(Notice(404, BridgeFillStatus.Accepted, 0, 5));
            Assert.False(r.Mismatch.Tripped);
            Assert.Equal(1, r.Exec.ModifyApplied);
            Assert.Contains(r.Records, x => x.Kind == ReconciliationKind.ModifyApplied && x.Qty == 5);
            r.Now += OrderExecutionFeedAdapter.AmendWindowMicros + 1;            // 정정 반영 후엔 창 밖에서도 정합
            r.Exec.OnFill(Notice(404, BridgeFillStatus.Filled, 5, 0));
            Assert.False(r.Mismatch.Tripped);
            Assert.Equal(0, r.Exec.Tracked);                                     // 2 + 5 = 정정 기준 완전 체결 → 종결
            Assert.Equal(0, r.Router.TrackedOrders);
        }

        // ════════════ §6 계좌 동기화 ════════════

        private static PositionPayload Row(long batch, int idx, int count, long sym, long qty)
            => new() { BatchId = batch, RowIndex = idx, RowCount = count, SymbolCode = sym, Quantity = qty, AvgPrice = 70_000 };

        private static AccountStateSyncAdapter Acct(Func<long> clock, AccountDivergenceInterlock? il = null, List<ReconciliationRecord>? recs = null)
            => new(64, nowMicros: clock, interlock: il, onRecord: recs is null ? null : recs.Add);

        [Fact]
        public void Cash_ReserveGate_Stale_Insufficient_Release()
        {
            long now = 1_000_000;
            var a = Acct(() => now);
            Assert.Equal(AccountVerdict.Stale, a.TryReserveBuy(1));             // 스냅샷 부재 = fail-closed
            a.OnCash(new AccountCashPayload { Deposit = 2_000_000, D2Deposit = 1_900_000, OrderableCash = 1_000_000 });
            Assert.Equal(AccountVerdict.Ok, a.TryReserveBuy(700_000));
            Assert.Equal(AccountVerdict.Insufficient, a.TryReserveBuy(400_000)); // ★예약 차감 후 부족
            a.ReleaseBuy(700_000);
            Assert.Equal(AccountVerdict.Ok, a.TryReserveBuy(1_000_000));
            Assert.Equal(AccountVerdict.Insufficient, a.TryReserveBuy(1));
            now += AccountStateSyncAdapter.DefaultStaleMicros + 1;
            Assert.Equal(AccountVerdict.Stale, a.TryReserveBuy(1));
            Assert.Equal((1L, 2L), (a.RejectCash > 0 ? 1L : 0L, a.RejectStale));
        }

        [Fact]
        public void Cash_AdmissionAfterRequest_StaysReserved()
        {
            long now = 1_000_000;
            var a = Acct(() => now);
            a.OnCash(new AccountCashPayload { OrderableCash = 2_000_000 });
            Assert.Equal(AccountVerdict.Ok, a.TryReserveBuy(600_000));
            a.MarkQueryRequested();
            Assert.Equal(AccountVerdict.Ok, a.TryReserveBuy(300_000));          // 요청 후 승인 — 응답 반영 여부 불명
            a.OnCash(new AccountCashPayload { OrderableCash = 1_000_000 });      // 브로커가 600k 반영한 새 주문가능금액
            Assert.Equal(300_000, a.ReservedBuyNotional);                       // 요청 이전분은 해제 · 이후분은 보수 유지
            Assert.Equal(AccountVerdict.Insufficient, a.TryReserveBuy(700_001));
            Assert.Equal(AccountVerdict.Ok, a.TryReserveBuy(700_000));
        }

        [Fact]
        public void Positions_SeedThenQuietMatch_SellGate()
        {
            long now = 1_000_000;
            var il = new AccountDivergenceInterlock();
            var a = Acct(() => now, il);
            Assert.Equal(AccountVerdict.Stale, a.CheckSell(Code, 1));           // 기준선 전 = fail-closed
            a.OnPosition(Row(1, 0, 2, Code, 10));
            Assert.False(a.Seeded);                                             // 배치 미완
            a.OnPosition(Row(1, 1, 2, 660, 3));
            Assert.True(a.Seeded);
            Assert.Equal(AccountVerdict.Ok, a.CheckSell(Code, 10));
            Assert.Equal(AccountVerdict.Insufficient, a.CheckSell(Code, 11));   // ★보유 초과 매도
            Assert.Equal(AccountVerdict.Insufficient, a.CheckSell(1234, 1));
            now += 10_000_000; a.MarkQueryRequested(); now += 100_000;
            a.OnPosition(Row(2, 0, 2, 660, 3)); a.OnPosition(Row(2, 1, 2, Code, 10));   // 순서 무관 일치
            Assert.False(il.Tripped);
            Assert.Equal(2, a.Batches);
        }

        [Fact]
        public void Positions_QuietDivergence_TripsInterlock_DirectionB()
        {
            // [방향 B] 정적 구간 브로커 9주 ≠ 호스트 10주 → sticky Disarm(물리 이중키 재무장)
            long now = 1_000_000;
            var il = new AccountDivergenceInterlock();
            var recs = new List<ReconciliationRecord>();
            var a = Acct(() => now, il, recs);
            var mask = new ArmingInterlockBitmask(ArmingInterlockSource.AccountDivergence);
            il.BindInterlock(mask, ArmingInterlockSource.AccountDivergence);
            Assert.True(mask.Seal(out _));
            a.OnPosition(Row(1, 0, 1, Code, 10));
            now += 10_000_000; a.MarkQueryRequested(); now += 100_000;
            a.OnPosition(Row(2, 0, 1, Code, 9));
            Assert.True(il.Tripped);
            Assert.True(mask.Blocked);
            Assert.Equal((Code, 9L), (il.LastSymbol, il.LastBrokerQty));
            Assert.Contains(recs, x => x.Kind == ReconciliationKind.AccountDivergence && x.BrokerOrderId == Code && x.Qty == 9);
            Assert.Equal(10, a.PositionOf(Code));                               // 괴리 배치로 덮어쓰지 않음(증거 보존)
            Assert.False(il.TryRearm(true, false));
            Assert.True(il.TryRearm(true, true));
            Assert.False(mask.Blocked);

            // 호스트에만 있는 보유(브로커 0행) — 역방향 괴리
            now += 10_000_000; a.MarkQueryRequested(); now += 100_000;
            a.OnPosition(Row(3, 0, 0, 0, 0));
            Assert.True(il.Tripped);
            Assert.Equal((Code, 0L), (il.LastSymbol, il.LastBrokerQty));
        }

        [Fact]
        public void Positions_FillNearRequest_SkipsComparison_NoFalseTrip()
        {
            long now = 1_000_000;
            var il = new AccountDivergenceInterlock();
            var a = Acct(() => now, il);
            a.OnPosition(Row(1, 0, 1, Code, 10));
            now += 10_000_000;
            a.OnFill(Notice(1, BridgeFillStatus.Filled, 5, 0));                  // 매수 5주 체결
            now += 500_000; a.MarkQueryRequested(); now += 100_000;              // 체결 직후 조회 — 응답은 체결 전 잔고일 수 있음
            a.OnPosition(Row(2, 0, 1, Code, 10));
            Assert.False(il.Tripped);                                            // ★시점 차이를 괴리로 오판하지 않음
            Assert.Equal(1, a.SkippedNotQuiet);
            Assert.Equal(15, a.PositionOf(Code));
            now += 10_000_000; a.MarkQueryRequested(); now += 100_000;          // 다음 정적 조회에서 정합
            a.OnPosition(Row(3, 0, 1, Code, 15));
            Assert.False(il.Tripped);
            a.OnFill(Notice(1, BridgeFillStatus.Filled, 15, 0, side: -1));       // 전량 매도 → 0
            Assert.Equal(0, a.PositionOf(Code));
            Assert.Equal(AccountVerdict.Insufficient, a.CheckSell(Code, 1));
        }

        [Fact]
        public void Positions_OutOfOrderRow_InvalidatesBatch_DuplicateSymbolRowsMerged()
        {
            long now = 1_000_000;
            var il = new AccountDivergenceInterlock();
            var a = Acct(() => now, il);
            a.OnPosition(Row(1, 0, 2, Code, 10));
            a.OnPosition(Row(1, 0, 2, Code, 10));                                // 중복 행 = 배치 무효
            Assert.False(a.Seeded);
            Assert.Equal(1, a.InvalidBatches);
            a.OnPosition(Row(2, 0, 2, Code, 4));                                 // 같은 종목 2행(현금/신용) 합산
            a.OnPosition(Row(2, 1, 2, Code, 6));
            Assert.True(a.Seeded);
            Assert.Equal(10, a.PositionOf(Code));
            a.OnPosition(Row(3, 0, 3, Code, 1));
            a.OnPosition(Row(4, 0, 1, Code, 10));                                // 미완 배치 버림 → 새 배치
            Assert.Equal(2, a.InvalidBatches);
        }

        // ════════════ §7 송출 게이트 · 검증 · δ=0 · 결선 ════════════

        [Fact]
        public void RouterGate_AccountRejects_Stale_Cash_Position_ReleaseOnRouteReject()
        {
            var r = Make(withAccount: true);
            var a = r.Account!;
            Assert.False(TryAdmit(r, 1));                                        // 스냅샷 부재
            Assert.Equal(OrderRouteVerdict.AccountStale, r.Gate.LastVerdict);
            a.OnCash(new AccountCashPayload { OrderableCash = 1_000_000 });
            Assert.False(TryAdmit(r, 2, qty: 15m));                              // 70,000 × 15 = 1,050,000 > 1,000,000
            Assert.Equal(OrderRouteVerdict.InsufficientCash, r.Gate.LastVerdict);
            Assert.Equal(0, r.Router.TrackedOrders);
            Assert.True(TryAdmit(r, 3, qty: 14m));                               // 980,000 예약
            Assert.Equal(980_000, a.ReservedBuyNotional);
            Assert.False(TryAdmit(r, 4, qty: 1m));                               // 잔여 20,000 < 70,000
            Assert.False(TryAdmit(r, 5, OrderSide.Sell, 1m));                    // 보유 기준선 없음
            Assert.Equal(OrderRouteVerdict.AccountStale, r.Gate.LastVerdict);
            a.OnPosition(Row(1, 0, 1, Code, 3));
            Assert.False(TryAdmit(r, 6, OrderSide.Sell, 4m));
            Assert.Equal(OrderRouteVerdict.InsufficientPosition, r.Gate.LastVerdict);
            Assert.True(TryAdmit(r, 7, OrderSide.Sell, 3m));
            Assert.Equal(5, r.Gate.AccountRejects);
            Assert.Contains(r.Records, x => x.Kind == ReconciliationKind.AccountRejected && x.ProposalId == 6 && x.Qty == 4);

            // 라우터 거부(슬리피지 초과) = 미송출 → 예약 반환
            a.OnCash(new AccountCashPayload { OrderableCash = 10_000_000 });
            Assert.False(TryAdmit(r, 8, qty: 1m, px: 80_000m));                  // 참조 70,000 대비 1,428bps > 50bps
            Assert.Equal(OrderRouteVerdict.SlippageExceeded, r.Gate.LastVerdict);
            Assert.Equal(980_000, a.ReservedBuyNotional);                     // 미송출분(80,000)만 반환 · 기존 예약 유지
            Assert.Equal(1L, RouterFeedGate.NotionalWonCeil(1, 1));              // 0.00000001원 → 1원 올림(보수)
            Assert.Equal(long.MaxValue, RouterFeedGate.NotionalWonCeil(long.MaxValue, long.MaxValue));
        }

        private static bool Gate(BridgeMessageType type, byte[] payload)
        {
            var m = typeof(KiwoomBridgeClient).GetMethod("ValidateIncoming", BindingFlags.NonPublic | BindingFlags.Static)!;
            return (bool)m.Invoke(null, new object[] { type, payload })!;
        }

        private static byte[] Payload<T>(BridgeMessageType t, in T v) where T : unmanaged
            => BridgeSerializer.Pack(t, in v).AsSpan(BridgeSerializer.HeaderSize).ToArray();

        [Fact]
        public void Bridge_AccountFrames_SizeAndValidation()
        {
            Assert.Equal(32, System.Runtime.CompilerServices.Unsafe.SizeOf<AccountCashPayload>());
            Assert.Equal(48, System.Runtime.CompilerServices.Unsafe.SizeOf<PositionPayload>());
            var cash = new AccountCashPayload { Deposit = -5, OrderableCash = 0 };
            Assert.True(Gate(BridgeMessageType.AccountCash, Payload(BridgeMessageType.AccountCash, in cash)));
            Assert.False(Gate(BridgeMessageType.AccountCash, new byte[31]));
            var ok = Row(1, 1, 2, Code, 3);
            Assert.True(Gate(BridgeMessageType.Position, Payload(BridgeMessageType.Position, in ok)));
            var empty = Row(1, 0, 0, 0, 0);
            Assert.True(Gate(BridgeMessageType.Position, Payload(BridgeMessageType.Position, in empty)));
            foreach (var bad in new[] { Row(1, 2, 2, Code, 3), Row(1, 0, 1, Code, -1), Row(1, 0, 1, 0, 3), Row(1, 0, 1, 1_000_000, 3), Row(1, 1, 0, 0, 0), Row(1, 0, -1, Code, 1) })
                Assert.False(Gate(BridgeMessageType.Position, Payload(BridgeMessageType.Position, in bad)));
            Assert.Equal(7, (int)OrderRequestType.ModifyOrder);
            Assert.Equal((11, 12), ((int)BridgeMessageType.AccountCash, (int)BridgeMessageType.Position));
        }

        [Fact]
        public void ZeroAlloc_AccountSync_AmendGate_RaceBarrier()
        {
            long now = 1_000_000;
            var il = new AccountDivergenceInterlock();
            var a = new AccountStateSyncAdapter(64, nowMicros: () => now, interlock: il);
            var r = Make(records: false);
            Admit(r, 1, 501, qty: 1_000_000m);
            var cash = new AccountCashPayload { OrderableCash = long.MaxValue / 4 };
            var row = Row(1, 0, 1, Code, 10);
            var fill = Notice(501, BridgeFillStatus.Filled, 1, 999_999);
            var acc = Notice(501, BridgeFillStatus.Accepted, 0, 0);
            long batch = 1;
            Action body = () =>
            {
                for (int k = 0; k < 200; k++)
                {
                    a.OnCash(cash);
                    a.MarkQueryRequested();
                    row = Row(++batch, 0, 1, Code, a.PositionOf(Code));
                    a.OnPosition(row);
                    a.OnFill(fill);
                    _ = a.TryReserveBuy(1);
                    _ = a.CheckSell(Code, 1);
                    a.ReleaseBuy(1);
                    _ = r.Exec.TryBeginCancel(501);                              // 첫 회 통과 · 이후 중복 차단(링 기록 경로)
                    _ = r.Exec.TryBeginModify(501, 2_000_000, 1);                // 잔량 초과 차단 경로
                    r.Exec.OnFill(acc);                                          // 취소 진행 중 잔량 불일치 유예 경로
                    now += 1;
                }
            };
            body();   // 워밍업(JIT)
            Assert.Equal(0, MinAllocated(body));
            Assert.False(il.Tripped);
        }

        [Fact]
        public void Host_WiresAccountSyncAndAmendGate()
        {
            const BindingFlags AllF = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
            var host = typeof(StockManager.Blazor.BlazorHomeForm);
            var targets = new MethodBase[]
            {
                typeof(AccountStateSyncAdapter).GetConstructors()[0],
                typeof(AccountDivergenceInterlock).GetMethod(nameof(AccountDivergenceInterlock.BindInterlock))!,
                typeof(KiwoomBridgeClient).GetEvent(nameof(KiwoomBridgeClient.AccountCashReceived))!.AddMethod!,
                typeof(KiwoomBridgeClient).GetEvent(nameof(KiwoomBridgeClient.PositionReceived))!.AddMethod!,
                typeof(ExecutionBridgeBroker).GetProperty(nameof(ExecutionBridgeBroker.AmendGate))!.SetMethod!,
                typeof(ExecutionBridgeBroker).GetMethod(nameof(ExecutionBridgeBroker.QueryAccount))!,
                typeof(AccountStateSyncAdapter).GetMethod(nameof(AccountStateSyncAdapter.MarkQueryRequested))!,
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
        }

        private static string RepoRoot()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d is not null && !File.Exists(Path.Combine(d.FullName, "StockManager.sln"))) d = d.Parent;
            Assert.NotNull(d);
            return d!.FullName;
        }

        [Fact]
        public void QuadBuild_ProxyContract_Regression()
        {
            // 사중 빌드(x86 KiwoomProxy) 계약 회귀 — 코어는 프록시에 소스 링크 · 정정 요청/계좌 프레임 처리 결선 유지
            string root = RepoRoot();
            string csproj = File.ReadAllText(Path.Combine(root, "StockManager.KiwoomProxy", "StockManager.KiwoomProxy.csproj"));
            Assert.Contains(@"src\02_Infrastructure\Engine\KiwoomComBridgeCore.cs", csproj);
            string api = File.ReadAllText(Path.Combine(root, "StockManager.KiwoomProxy", "IKiwoomApi.cs"));
            Assert.Contains("void ModifyOrder(long orderId, double price, long qty);", api);
            Assert.Contains("AccountCashReceived", api);
            string host = File.ReadAllText(Path.Combine(root, "StockManager.KiwoomProxy", "KiwoomProxyHost.cs"));
            Assert.Contains("case OrderRequestType.ModifyOrder:", host);
            Assert.Contains("BridgeMessageType.Position", host);
            string prod = File.ReadAllText(Path.Combine(root, "StockManager.KiwoomProxy", "ProductionKiwoomApi.cs"));
            Assert.Contains("_core?.CancelOrder(orderId)", prod);                 // 수정 전: 빈 스텁(취소 소실)
            Assert.Contains("_core?.QueryAccount(", prod);
            Assert.Equal(14, (int)ArmingInterlockSource.AccountDivergence);
        }
    }
}
