// ============================================================================
// KiwoomComLiveIntegrationTests — [키움 COM · 추천 A] COM 무의존 코어(가짜 OCX)로 키움 이벤트 전 경로 실측
//
//  §1 OnReceiveMsg 서버 메시지 — "[코드] 메시지" 분해 · 성공/거부/중대(비밀번호·계좌 정지·증거금)/정보 보수 분류 · RQName 파싱.
//  §2 송신 — rc≠0 · 계좌 미설정 → 즉시 Rejected(종목/방향 메타 동봉 → 호스트 형식 검증 통과).
//  §3 TR 응답 — 주문번호 공란 = 서버 거부 · 사상 · 중복 거부 억제.
//  §4 서버 거부 메시지 — 미사상 주문만 거부 확정(★이미 접수된 주문의 '거부' 메시지는 무시 — 오분류 비대칭 방어) · 중대 → ErrorReport 코드.
//  §5 [실결함] 체결 통보 ↔ TR 응답 경합 — 보류 후 호스트 ID 로 방출 · 만료 방출 · 종결 시 표 정리.
//  §6 실시간 — 주식체결 → 틱 + 기준가(변경 시 1회) · 주식호가잔량 → 10호가.
//  §7 KiwoomOrderNumberMap — 역방향 삭제 무결성(참조 사전 대조 무작위 검증) · 포화.
//  §8 [방향 A] STA 펌프 감시 · [방향 B] 중대 오류 인터락 · E2E(거부 → 브리지 게이트 → 추적 슬롯 0ms 반납) · δ=0 · 호스트 결선.
// ============================================================================
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using MS2E.Core;
using MS2E.Infrastructure;
using StockManager.Adapters;
using Xunit;

namespace StockManager.Tests
{
    internal sealed class FakeOcx : IKiwoomOcx
    {
        public int SendRc;
        public readonly List<string> SentRq = new();
        public readonly Dictionary<int, string> Chejan = new();
        public readonly Dictionary<(string, int), string> Real = new();
        public string OrderNoReply = "";
        public string GetCommRealData(string realKey, int fid) => Real.TryGetValue((realKey, fid), out var v) ? v : "";
        public string GetChejanData(int fid) => Chejan.TryGetValue(fid, out var v) ? v : "";
        // [정정/취소·계좌] 송신 인자 기록 · TR 입력/요청 기록 · 다중 행 TR 응답 표(rqName → 행 → 항목)
        public readonly List<(string Rq, int Type, string Code, int Qty, int Price, string OrgNo)> Orders = new();
        public readonly List<(string Id, string Value)> Inputs = new();
        public readonly List<(string Rq, string Tr)> Requests = new();
        public readonly Dictionary<string, List<Dictionary<string, string>>> TrRows = new();
        public int RqRc { get; set; }
        public string GetCommData(string trCode, string recordName, int index, string itemName)
        {
            if (itemName == "주문번호") return OrderNoReply;
            return TrRows.TryGetValue(recordName, out var rows) && index < rows.Count && rows[index].TryGetValue(itemName, out var v) ? v : "";
        }
        public int SendOrder(string rqName, string screenNo, string accountNo, int orderType, string code, int qty, int price, string hoga, string orgOrderNo)
        { SentRq.Add(rqName); Orders.Add((rqName, orderType, code, qty, price, orgOrderNo)); return SendRc; }
        public void SetInputValue(string id, string value) => Inputs.Add((id, value));
        public int CommRqData(string rqName, string trCode, int prevNext, string screenNo) { Requests.Add((rqName, trCode)); return RqRc; }
        public int GetRepeatCnt(string trCode, string recordName) => TrRows.TryGetValue(recordName, out var rows) ? rows.Count : 0;

        public void SetChejan(long kiwoomNo, string state, string kind, long fillQty, long remaining, long price = 70_000)
        {
            Chejan[9203] = kiwoomNo.ToString(); Chejan[9001] = "A005930"; Chejan[910] = price.ToString(); Chejan[911] = fillQty.ToString();
            Chejan[907] = "2"; Chejan[913] = state; Chejan[905] = kind; Chejan[902] = remaining.ToString();
        }
    }

    [Collection("ConcurrencyStress")]
    public sealed class KiwoomComLiveIntegrationTests
    {
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

        private sealed class Rig
        {
            public long Now = 1_000_000_000;
            public readonly FakeOcx Ocx = new();
            public readonly List<FillPayload> Fills = new();
            public readonly List<TickPayload> Ticks = new();
            public readonly List<OrderBookPayload> Books = new();
            public readonly List<ReferencePricePayload> Bases = new();
            public readonly List<int> Errors = new();
            public KiwoomComEventCore Core = null!;
        }

        private static Rig Make()
        {
            var r = new Rig();
            r.Core = new KiwoomComEventCore(r.Ocx, r.Ticks.Add, r.Fills.Add, r.Books.Add, r.Bases.Add, r.Errors.Add, () => r.Now, mapCapacityPow2: 64);
            return r;
        }

        private static BridgeFillStatus St(in FillPayload f) => BridgeFillStatusCodec.Decode(in f, out _);

        // ════════════ §1 서버 메시지 ════════════

        [Theory]
        [InlineData("[00Z112] 모의투자 정상처리 되었습니다", KiwoomMsgVerdict.Accepted, KiwoomCriticalKind.None)]
        [InlineData("[00Z113] 모의투자 매수주문이 완료되었습니다.", KiwoomMsgVerdict.Accepted, KiwoomCriticalKind.None)]
        [InlineData("[800033] 모의투자 주문가능금액이 부족합니다.", KiwoomMsgVerdict.CriticalRejected, KiwoomCriticalKind.Margin)]
        [InlineData("[RC4007] 증거금 부족으로 주문이 거부되었습니다", KiwoomMsgVerdict.CriticalRejected, KiwoomCriticalKind.Margin)]
        [InlineData("[571489] 비밀번호 입력오류입니다", KiwoomMsgVerdict.CriticalRejected, KiwoomCriticalKind.Password)]
        [InlineData("[100013] 사고계좌로 거래가 정지된 계좌입니다", KiwoomMsgVerdict.CriticalRejected, KiwoomCriticalKind.AccountSuspended)]
        [InlineData("[RC4058] 모의투자 장종료 되었습니다", KiwoomMsgVerdict.Rejected, KiwoomCriticalKind.None)]
        [InlineData("[505217] 상한가 호가범위를 초과하였습니다", KiwoomMsgVerdict.Rejected, KiwoomCriticalKind.None)]
        [InlineData("조회가 완료되었습니다", KiwoomMsgVerdict.Accepted, KiwoomCriticalKind.None)]
        [InlineData("[000000] 알림", KiwoomMsgVerdict.Info, KiwoomCriticalKind.None)]
        [InlineData("", KiwoomMsgVerdict.Info, KiwoomCriticalKind.None)]
        public void ServerMessage_Classify(string msg, KiwoomMsgVerdict expected, KiwoomCriticalKind critical)
        {
            Assert.Equal(expected, KiwoomServerMessage.Classify(msg, out var c));
            Assert.Equal(critical, c);
        }

        [Fact]
        public void ServerMessage_Split_And_RqName()
        {
            KiwoomServerMessage.Split(" [800033] 주문가능금액이 부족합니다 ", out var code, out var text);
            Assert.Equal("800033", code.ToString());
            Assert.Equal("주문가능금액이 부족합니다", text.ToString());
            KiwoomServerMessage.Split("평문 메시지", out code, out text);
            Assert.True(code.IsEmpty);
            Assert.True(KiwoomServerMessage.TryParseRqName("RQ12345", out long id));
            Assert.Equal(12345, id);
            Assert.False(KiwoomServerMessage.TryParseRqName("주식기본정보", out _));
            Assert.False(KiwoomServerMessage.TryParseRqName("RQ-5", out _));
            Assert.False(KiwoomServerMessage.TryParseRqName("RQ", out _));
        }

        // ════════════ §2 · §3 · §4 ════════════

        [Fact]
        public void SendOrder_RcNonZero_OrNoAccount_ImmediateReject_WithSymbolMeta()
        {
            var r = Make();
            r.Ocx.SendRc = -308;                                               // 송신 단계 오류
            Assert.Equal(-308, r.Core.SendOrder(7, "5001", "8012345611", 5930, 1, 70_000, 10));
            var f = Assert.Single(r.Fills);
            Assert.Equal(BridgeFillStatus.Rejected, St(in f));
            Assert.Equal(7, f.OrderId);
            Assert.Equal(5930, f.SymbolCode);
            Assert.True(StockValidator.ValidateOrderNotice(f).IsValid);        // 호스트 형식 검증 통과(종목 메타)
            Assert.Equal(0, r.Core.Map.Count);

            r.Fills.Clear();
            Assert.Equal(-1, r.Core.SendOrder(8, "5001", "", 5930, -1, 70_000, 10));   // 계좌 미설정 — OCX 호출 0
            Assert.Single(r.Ocx.SentRq);
            Assert.Equal(-1, Assert.Single(r.Fills).Side);
        }

        [Fact]
        public void TrData_EmptyOrderNo_IsServerReject_ThenDuplicatesSuppressed()
        {
            var r = Make();
            Assert.Equal(0, r.Core.SendOrder(11, "5001", "8012345611", 5930, 1, 70_000, 10));
            Assert.Equal("RQ11", r.Ocx.SentRq[0]);
            r.Ocx.OrderNoReply = "   ";
            r.Core.OnTrData("RQ11", "KOA_NORMAL_BUY_KP_ORD");
            var f = Assert.Single(r.Fills);
            Assert.Equal(BridgeFillStatus.Rejected, St(in f));
            Assert.Equal(5930, f.SymbolCode);
            r.Core.OnReceiveMsg("RQ11", "[RC4058] 장종료 되었습니다");           // 같은 주문 두 번째 거부 신호 — 중복 통보 0
            Assert.Single(r.Fills);
            Assert.Equal(1, r.Core.RejectsAtTr);
        }

        [Fact]
        public void ServerRejectMsg_OnlyForUnmappedOrders_CriticalRaisesErrorCode()
        {
            var r = Make();
            r.Core.SendOrder(21, "5001", "8012345611", 5930, 1, 70_000, 10);
            r.Core.OnReceiveMsg("RQ21", "[800033] 모의투자 주문가능금액이 부족합니다.");
            Assert.Equal(BridgeFillStatus.Rejected, St(Assert.Single(r.Fills)));   // ★0ms 거부 통보
            Assert.Equal(KiwoomServerErrorCodes.Margin, Assert.Single(r.Errors));  // 중대 → ErrorReport 코드

            r.Core.SendOrder(22, "5001", "8012345611", 5930, 1, 70_000, 10);
            r.Ocx.OrderNoReply = "0000123";
            r.Core.OnTrData("RQ22", "TR");                                     // 서버 접수(주문번호 사상)
            r.Core.OnReceiveMsg("RQ22", "[505217] 호가범위를 초과하였습니다");   // 접수된 주문의 '거부' 메시지 — 신뢰 안 함
            Assert.Single(r.Fills);
            Assert.Equal(1, r.Core.IgnoredMsgRejects);
            Assert.Equal(2, r.Core.Map.StateOf(22));

            r.Core.OnReceiveMsg("RQ22", "[00Z112] 정상처리 되었습니다");        // 성공 메시지 — 무동작
            r.Core.OnReceiveMsg(null, "[571489] 비밀번호 입력오류");            // RQ 없는 중대 오류 — 코드만
            Assert.Equal(new[] { KiwoomServerErrorCodes.Margin, KiwoomServerErrorCodes.Password }, r.Errors);
        }

        // ════════════ §5 체결 통보 경합 ════════════

        [Fact]
        public void Chejan_BeforeTrData_Buffered_ThenFlushedWithClientId()
        {
            // [실결함] 수정 전: TR 응답 전 접수 통보는 키움 주문번호로 송신 → 호스트 '미상 주문'으로 유실.
            var r = Make();
            r.Core.SendOrder(31, "5001", "8012345611", 5930, 1, 70_000, 10);
            r.Ocx.SetChejan(555, "접수", "+매수", 0, 10);
            r.Core.OnChejan("0");
            Assert.Empty(r.Fills);                                             // 보류
            Assert.Equal(1, r.Core.PendingCount);
            r.Ocx.OrderNoReply = "555";
            r.Core.OnTrData("RQ31", "TR");
            var f = Assert.Single(r.Fills);
            Assert.Equal(31, f.OrderId);                                       // ★호스트 ID 로 방출
            Assert.Equal(BridgeFillStatus.Accepted, St(in f));
            Assert.Equal(0, r.Core.PendingCount);

            r.Ocx.SetChejan(555, "체결", "+매수", 10, 0);                      // 완전 체결 — 표 정리
            r.Core.OnChejan("0");
            Assert.Equal(31, r.Fills[^1].OrderId);
            Assert.Equal(0, r.Core.Map.Count);
            r.Core.OnChejan("1");                                              // 잔고 통보(gubun 1) 무시
            Assert.Equal(2, r.Fills.Count);
        }

        [Fact]
        public void Chejan_Unmapped_Expires_EmitsLegacyNumber()
        {
            var r = Make();
            r.Ocx.SetChejan(777, "접수", "+매수", 0, 5);
            r.Core.OnChejan("0");
            r.Now += 1 * TimeSpan.TicksPerSecond;
            Assert.Equal(0, r.Core.FlushExpired());
            r.Now += 2 * TimeSpan.TicksPerSecond;
            Assert.Equal(1, r.Core.FlushExpired());
            Assert.Equal(777, Assert.Single(r.Fills).OrderId);                 // 사상 불가 — 구 동작(호스트 미상 → TTL)
            Assert.Equal(1, r.Core.ExpiredPending);
        }

        // ════════════ §6 실시간 ════════════

        [Fact]
        public void RealData_Tick_Base_OnlyOnChange_And_OrderBook()
        {
            var r = Make();
            r.Ocx.Real[("005930", 10)] = "+70500"; r.Ocx.Real[("005930", 11)] = "+500"; r.Ocx.Real[("005930", 12)] = "+0.71"; r.Ocx.Real[("005930", 13)] = "1234";
            r.Core.OnRealData("주식체결", "005930");
            r.Core.OnRealData("주식체결", "005930");
            Assert.Equal(2, r.Ticks.Count);
            Assert.Equal(70_500, r.Ticks[0].Price);
            Assert.Equal(1234, r.Ticks[0].Volume);
            Assert.Equal(70_000, Assert.Single(r.Bases).BasePrice);            // 변경 없을 때 재통지 0
            for (int i = 0; i < 10; i++)
            {
                r.Ocx.Real[("005930", 41 + i)] = "-" + (70_600 + i * 100); r.Ocx.Real[("005930", 61 + i)] = "10";
                r.Ocx.Real[("005930", 51 + i)] = "+" + (70_500 - i * 100); r.Ocx.Real[("005930", 71 + i)] = "20";
            }
            r.Core.OnRealData("주식호가잔량", "005930");
            var b = Assert.Single(r.Books);
            Assert.Equal(5930, b.SymbolCode);
            Assert.Equal(70_600, b.AskPrice[0]);
            Assert.Equal(69_600, b.BidPrice[9]);
            Assert.Equal(L2BookVerdict.Ok, L2OrderBookDepthParser.Validate(in b, out _, out _));
        }

        // ════════════ §7 주문번호 표 ════════════

        [Fact]
        public void OrderNumberMap_RandomOps_MatchReference_BackwardShiftIntegrity()
        {
            var map = new KiwoomOrderNumberMap(256);
            var refC = new Dictionary<long, long>();
            var rnd = new Random(5);
            for (int step = 0; step < 20_000; step++)
            {
                long client = rnd.Next(1, 400);
                int op = rnd.Next(3);
                if (op == 0 && refC.Count < 200) { map.RegisterPending(client, 5930, 1); refC.TryAdd(client, 0); }
                else if (op == 1 && refC.Count < 200) { long k = client * 1000 + 7; map.Map(client, k); refC[client] = k; }
                else { map.RemoveClient(client); refC.Remove(client); }
            }
            Assert.Equal(refC.Count, map.Count);
            foreach (var (c, k) in refC)
            {
                Assert.Equal(k == 0 ? 1 : 2, map.StateOf(c));
                if (k != 0) { Assert.True(map.TryGetClient(k, out long back)); Assert.Equal(c, back); }
            }
            var full = new KiwoomOrderNumberMap(4);
            for (int i = 1; i <= 3; i++) Assert.True(full.RegisterPending(i));
            Assert.False(full.RegisterPending(99));                            // 포화(용량−1)
            Assert.Equal(1, full.FullRejects);
        }

        // ════════════ §8 방향 A · 방향 B · E2E · δ=0 · 결선 ════════════

        [Fact]
        public void StaPumpWatchdog_StallSticky()
        {
            long now = 1_000;
            var w = new StaPumpWatchdog(stallMillis: 100, nowTicks: () => now, tickFrequency: 1_000);   // 1 tick = 1ms
            Assert.False(w.IsStalled());                                       // Beat 전 = 미확립(정상)
            w.Beat(); now += 50; w.Beat();
            now += 100;
            Assert.False(w.IsStalled());
            now += 1;
            Assert.True(w.IsStalled());                                        // ★100ms 초과 불통
            w.Beat();
            Assert.True(w.IsStalled());                                        // sticky — 재기동 전 회복 불인정
            Assert.Equal(1, w.Stalls);
            Assert.Equal(101, w.MaxGapTicks);                                  // 1,050 → 1,151 Beat 간격
        }

        [Fact]
        public void ServerErrorInterlock_CriticalTrips_OthersIgnored_Rearm()
        {
            var il = new KiwoomServerErrorInterlock();
            var mask = new ArmingInterlockBitmask(ArmingInterlockSource.KiwoomServerError);
            il.BindInterlock(mask, ArmingInterlockSource.KiwoomServerError);
            Assert.True(mask.Seal(out _));
            il.OnError(new ErrorPayload { ErrorCode = 5 });
            Assert.False(mask.Blocked);
            Assert.Equal(1, il.Ignored);
            il.OnError(new ErrorPayload { ErrorCode = KiwoomServerErrorCodes.StaPumpStall });
            Assert.True(mask.Blocked);
            Assert.Equal(KiwoomServerErrorCodes.StaPumpStall, il.LastCode);
            Assert.False(il.TryRearm(true, false));
            Assert.True(il.TryRearm(true, true));
            Assert.False(mask.Blocked);
        }

        [Fact]
        public void E2E_ServerReject_ThroughBridgeGate_ReleasesHostSlotImmediately()
        {
            var r = Make();
            long now = 1_000_000;
            Func<long> clock = () => now;
            var price = new ReferencePriceFeedAdapter(capacityPow2: 64, nowMicros: clock);
            var board = new LockFreeBrokerSwitchboard(1);
            var pending = new PendingOrderCascadeCanceller(64);
            var router = new ResilientOrderRouter(board, new BrokerFailoverSentinel(board, pendingOrders: pending), pending, idMapCapacityPow2: 256, maxSlippageBps: 50);
            var exec = new OrderExecutionFeedAdapter(router, 64, 120_000_000, clock);
            var breaker = new FeedDisconnectCircuitBreaker(price, () => true);
            var gate = new RouterFeedGate(router, price, exec, breaker, requireDepthForMarket: false);
            price.OnTick(ReferencePriceFeedAdapter.SymbolHashForCode(5930), 70_000, 1);
            breaker.Evaluate();
            var p = new OrderProposal(1, 0, 5930, OrderSide.Buy, 1, QPrice.Of(70_000m), QQty.Of(10m), 0);
            Assert.True(gate.TryAdmit(in p, out _));
            gate.OnBrokerAccepted(1, "41");                                    // ExecutionBridgeBroker 주문ID = 41

            r.Core.SendOrder(41, "5001", "8012345611", 5930, 1, 70_000, 10);
            r.Core.OnReceiveMsg("RQ41", "[800033] 주문가능금액이 부족합니다.");  // 서버 거부
            var reject = Assert.Single(r.Fills);
            byte[] wire = BridgeSerializer.Pack(BridgeMessageType.OrderRejected, in reject);
            var payload = wire.AsSpan(BridgeSerializer.HeaderSize).ToArray();
            var validate = typeof(KiwoomBridgeClient).GetMethod("ValidateIncoming", BindingFlags.NonPublic | BindingFlags.Static)!;
            Assert.True((bool)validate.Invoke(null, new object[] { BridgeMessageType.OrderRejected, payload })!);   // 브리지 게이트 통과
            Assert.True(BridgeFillReader.TryReadFill(payload, out var parsed));
            exec.OnFill(parsed);
            Assert.Equal(0, router.TrackedOrders);                             // ★2분 TTL 없이 0ms 반납
            Assert.Equal(1, exec.RejectedReleased);
            Assert.False(exec.ArmingBlocked);                                  // 무장 유지
        }

        [Fact]
        public void HotPaths_ZeroAlloc()
        {
            var map = new KiwoomOrderNumberMap(1024);
            long sink = 0, t = 0;
            var w = new StaPumpWatchdog(100, () => t, 1_000);
            const string msg = "[800033] 모의투자 주문가능금액이 부족합니다.";
            KiwoomServerMessage.Classify(msg, out _);
            Assert.Equal(0L, MinAllocated(() =>
            {
                for (int i = 1; i < 500; i++)
                {
                    sink += (long)KiwoomServerMessage.Classify(msg, out var c) + (long)c;
                    if (KiwoomServerMessage.TryParseRqName("RQ12345", out long id)) sink += id;
                    map.RegisterPending(i, 5930, 1);
                    map.Map(i, 100_000 + i);
                    if (map.TryGetClient(100_000 + i, out long back)) sink += back;
                    map.RemoveClient(i);
                    t++; w.Beat(); if (!w.IsStalled()) sink++;
                    sink += KiwoomRealFids.ParsePrice("+70000");
                }
            }));
            Assert.True(sink > 0);
            Assert.Equal(0, map.Count);
        }

        [Fact]
        public void Host_WiresServerErrorInterlock()
        {
            const BindingFlags AllF = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
            var host = typeof(StockManager.Blazor.BlazorHomeForm);
            var targets = new MethodBase[]
            {
                typeof(KiwoomServerErrorInterlock).GetMethod(nameof(KiwoomServerErrorInterlock.BindInterlock))!,
                typeof(KiwoomBridgeClient).GetEvent(nameof(KiwoomBridgeClient.ErrorReceived))!.AddMethod!,
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
