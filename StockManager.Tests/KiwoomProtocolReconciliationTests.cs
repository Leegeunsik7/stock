// ============================================================================
// KiwoomProtocolReconciliationTests — [키움 프로토콜 · 추천 A] 체결량 0 통보 모호성 해소 · 거부 즉시 슬롯 반납 · 상태 불일치 Disarm
//
//  §1 BridgeFillStatusCodec — 상태·매직·잔량 비트 · 구 Pad=0/쓰레기 = Legacy.
//  §2 BridgeFillReader — 56B 신 프레임 · 48B 구 프레임(잔량 무효) · 길이 미달 거부 · 바이트 즉시 상태 판독.
//  §3 KiwoomChejanClassifier — 913 주문상태 · 905 주문구분 · 911 체결량 → 접수/거부/취소확인/체결.
//  §4 [실결함] 브리지 검증 게이트 — 수정 전: 체결량 0 통보(체결가 0)를 '체결' 규칙으로 전량 폐기.
//  §5 [실결함] LiveAuditService — 수정 전: 체결량 0 통보를 형식 오류 체결로 ForceFreeze.
//  §6 OrderExecutionFeedAdapter — 거부 0ms 반납 · 접수 장기 TTL · 비캐스케이드 취소 해제 · 구 프록시 동작 보존.
//  §7 [방향 B] 브로커 잔량 불일치 Disarm · [방향 A] 대사 포렌식 체인 각인 · E2E · δ=0 · 호스트 결선.
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
    [Collection("ConcurrencyStress")]
    public sealed class KiwoomProtocolReconciliationTests
    {
        private const long Code = 5930;

        private static FillPayload Notice(long brokerOrder, BridgeFillStatus st, long qty = 0, double px = 0, long remaining = -1)
            => new()
            {
                OrderId = brokerOrder, SymbolCode = Code, FillPrice = px, FillQty = qty, Side = 1, TimestampTicks = 1,
                Status = BridgeFillStatusCodec.Encode(st, remaining >= 0), RemainingQty = remaining >= 0 ? remaining : 0,
            };

        // ════════════ §1 코덱 ════════════

        [Fact]
        public void Codec_RoundTrip_Magic_Legacy()
        {
            foreach (BridgeFillStatus s in new[] { BridgeFillStatus.Filled, BridgeFillStatus.Accepted, BridgeFillStatus.Rejected, BridgeFillStatus.CancelConfirmed })
            {
                Assert.Equal(s, BridgeFillStatusCodec.Decode(BridgeFillStatusCodec.Encode(s, true), out bool k1));
                Assert.True(k1);
                Assert.Equal(s, BridgeFillStatusCodec.Decode(BridgeFillStatusCodec.Encode(s, false), out bool k2));
                Assert.False(k2);
            }
            Assert.Equal(BridgeFillStatus.Legacy, BridgeFillStatusCodec.Decode(0, out _));                 // 구 프록시 Pad = 0
            Assert.Equal(BridgeFillStatus.Legacy, BridgeFillStatusCodec.Decode(3, out _));                 // 매직 없는 쓰레기
            Assert.Equal(BridgeFillStatus.Legacy, BridgeFillStatusCodec.Decode((0xA5 << 8) | 9, out _));   // 미지 코드
            Assert.Equal(BridgeFillStatus.Legacy, BridgeFillStatusCodec.Decode((0xA5 << 8) | 0, out _));
            var f = Notice(1, BridgeFillStatus.Accepted, remaining: 7);
            Assert.Equal(BridgeFillStatus.Accepted, BridgeFillStatusCodec.Decode(in f, out long rem));
            Assert.Equal(7, rem);
        }

        // ════════════ §2 판독기 ════════════

        [Fact]
        public void Reader_NewFrame_LegacyFrame_ShortFrame()
        {
            var src = Notice(42, BridgeFillStatus.Rejected, remaining: 0);
            byte[] frame = BridgeSerializer.Pack(BridgeMessageType.OrderRejected, in src);
            var payload = frame.AsSpan(BridgeSerializer.HeaderSize);
            Assert.Equal(56, payload.Length);
            Assert.True(BridgeFillReader.TryReadFill(payload, out var got));
            Assert.Equal(42, got.OrderId);
            Assert.Equal(BridgeFillStatus.Rejected, BridgeFillStatusCodec.Decode(in got, out long rem));
            Assert.Equal(0, rem);
            Assert.Equal(BridgeFillStatus.Rejected, BridgeFillReader.PeekStatus(payload));

            // 구 프록시 48B(RemainingQty 없음) — 잔량 유효 비트가 서 있어도 무효화
            Assert.True(BridgeFillReader.TryReadFill(payload[..48], out var legacy));
            Assert.Equal(42, legacy.OrderId);
            Assert.Equal(BridgeFillStatus.Rejected, BridgeFillStatusCodec.Decode(in legacy, out long lrem));
            Assert.Equal(-1, lrem);
            Assert.False(BridgeFillReader.TryReadFill(payload[..47], out _));
            Assert.Equal(BridgeFillStatus.Legacy, BridgeFillReader.PeekStatus(payload[..20]));
        }

        // ════════════ §3 키움 분류 ════════════

        [Theory]
        [InlineData("접수", "+매수", 0, BridgeFillStatus.Accepted)]
        [InlineData(" 접수 ", "-매도", 0, BridgeFillStatus.Accepted)]
        [InlineData("확인", "매수취소", 0, BridgeFillStatus.CancelConfirmed)]
        [InlineData("확인", "매도정정", 0, BridgeFillStatus.Accepted)]
        [InlineData("거부", "+매수", 0, BridgeFillStatus.Rejected)]
        [InlineData("체결", "+매수", 3, BridgeFillStatus.Filled)]
        [InlineData("체결", "+매수", 0, BridgeFillStatus.Legacy)]
        [InlineData("", "", 0, BridgeFillStatus.Legacy)]
        public void Classifier_KiwoomFids(string state913, string kind905, long qty911, BridgeFillStatus expected)
            => Assert.Equal(expected, KiwoomChejanClassifier.Classify(state913, kind905, qty911));

        // ════════════ §4 브리지 검증 게이트 ════════════

        private static bool Gate(BridgeMessageType type, byte[] payload)
        {
            var m = typeof(KiwoomBridgeClient).GetMethod("ValidateIncoming", BindingFlags.NonPublic | BindingFlags.Static)!;
            return (bool)m.Invoke(null, new object[] { type, payload })!;
        }

        private static byte[] PayloadOf(in FillPayload f)
        {
            byte[] frame = BridgeSerializer.Pack(BridgeMessageType.OrderRejected, in f);
            return frame.AsSpan(BridgeSerializer.HeaderSize).ToArray();
        }

        [Fact]
        public void BridgeGate_StatusNotices_PriceZero_Admitted_FillsStillStrict()
        {
            // [수정 전 Red] 체결가 0 인 접수/거부/취소확인 통보를 ValidateFill 로 검증해 전량 폐기 — 호스트가 거부를 받지 못했다.
            Assert.True(Gate(BridgeMessageType.OrderRejected, PayloadOf(Notice(1, BridgeFillStatus.Rejected))));
            Assert.True(Gate(BridgeMessageType.OrderRejected, PayloadOf(Notice(1, BridgeFillStatus.Accepted, remaining: 10))));
            Assert.True(Gate(BridgeMessageType.OrderRejected, PayloadOf(Notice(1, BridgeFillStatus.CancelConfirmed))));
            Assert.False(Gate(BridgeMessageType.OrderFilled, PayloadOf(Notice(1, BridgeFillStatus.Filled, qty: 5, px: 0))));     // 체결은 가격 필수
            Assert.True(Gate(BridgeMessageType.OrderFilled, PayloadOf(Notice(1, BridgeFillStatus.Filled, qty: 5, px: 70_000))));
            var bad = Notice(1, BridgeFillStatus.Rejected); bad.SymbolCode = 0;
            Assert.False(Gate(BridgeMessageType.OrderRejected, PayloadOf(bad)));                                                 // 형식 오류는 여전히 폐기
            Assert.True(Gate(BridgeMessageType.OrderRejected, PayloadOf(Notice(1, BridgeFillStatus.Rejected))[..48]));           // 구 48B 프레임
            Assert.False(Gate(BridgeMessageType.OrderRejected, new byte[40]));
        }

        // ════════════ §5 LiveAuditService ════════════

        [Fact]
        public void LiveAudit_StatusNotice_NoFreeze_UpdatesOrderState()
        {
            // [수정 전 Red] 체결량 0 · 체결가 0 통보 → StockValidator.ValidateFill 실패 → ForceFreeze.
            var att = new OrderAttestationService();
            var audit = new LiveAuditService(att);
            audit.RegisterOrder(11, "005930", PaperSide.Buy, PaperKind.Limit, 70_000, 10);
            audit.RegisterOrder(12, "005930", PaperSide.Buy, PaperKind.Limit, 70_000, 10);
            Assert.True(audit.ProcessFill(Notice(11, BridgeFillStatus.Accepted, remaining: 10)).Passed);
            Assert.True(audit.ProcessFill(Notice(11, BridgeFillStatus.Rejected)).Passed);
            Assert.True(audit.ProcessFill(Notice(12, BridgeFillStatus.CancelConfirmed)).Passed);
            Assert.True(audit.ProcessFill(new FillPayload { OrderId = 12, SymbolCode = Code, FillQty = 0, Side = 1 }).Passed);   // 구 프록시 모호 통보
            Assert.Equal(GatingMode.Normal, att.Mode);                                                                           // ★동결 0
            Assert.Equal(PendingOrderState.Rejected, audit.TryGetPending(11)!.State);
            Assert.Equal(PendingOrderState.Cancelled, audit.TryGetPending(12)!.State);
        }

        // ════════════ §6 추적 슬롯 수명주기 ════════════

        private sealed class Rig
        {
            public long Now = 1_000_000;
            public readonly List<ReconciliationRecord> Records = new(4096);   // 사전 용량 — δ=0 실측에서 테스트 싱크 성장 할당 배제
            public ReferencePriceFeedAdapter Price = null!;
            public PendingOrderCascadeCanceller Pending = null!;
            public ResilientOrderRouter Router = null!;
            public OrderExecutionFeedAdapter Exec = null!;
            public BrokerStateMismatchInterlock Mismatch = null!;
            public RouterFeedGate Gate = null!;
        }

        private static Rig Make(long ttl = 120_000_000, long acceptedTtl = 600_000_000)
        {
            var r = new Rig();
            Func<long> clock = () => Volatile.Read(ref r.Now);
            r.Price = new ReferencePriceFeedAdapter(capacityPow2: 64, nowMicros: clock);
            var board = new LockFreeBrokerSwitchboard(1);
            r.Pending = new PendingOrderCascadeCanceller(64);
            var sentinel = new BrokerFailoverSentinel(board, pendingOrders: r.Pending);
            r.Router = new ResilientOrderRouter(board, sentinel, r.Pending, idMapCapacityPow2: 256, maxSlippageBps: 50);
            r.Mismatch = new BrokerStateMismatchInterlock();
            r.Exec = new OrderExecutionFeedAdapter(r.Router, 64, ttl, clock, rec => r.Records.Add(rec), null, acceptedTtl, r.Mismatch);
            var breaker = new FeedDisconnectCircuitBreaker(r.Price, () => true);
            r.Gate = new RouterFeedGate(r.Router, r.Price, r.Exec, breaker, requireDepthForMarket: false);
            r.Price.OnTick(ReferencePriceFeedAdapter.SymbolHashForCode(Code), 70_000, 1);
            breaker.Evaluate();
            return r;
        }

        private static void Admit(Rig r, long proposal, string brokerId, decimal qty = 10m)
        {
            var p = new OrderProposal(proposal, 0, Code, OrderSide.Buy, 1, QPrice.Of(70_000m), QQty.Of(qty), 0);
            Assert.True(r.Gate.TryAdmit(in p, out _));
            r.Gate.OnBrokerAccepted(proposal, brokerId);
        }

        [Fact]
        public void Rejected_ReleasesSlotImmediately_NoTtlBlock()
        {
            // [수정 전 Red] 체결량 0 모호 통보 → 슬롯 유지 → TTL 회수 + OrderSlotAge sticky 차단(재무장 필요).
            var r = Make();
            var mask = new ArmingInterlockBitmask(ArmingInterlockSource.OrderSlotAge);
            r.Exec.BindInterlock(mask, ArmingInterlockSource.OrderSlotAge);
            Assert.True(mask.Seal(out _));
            Admit(r, 1, "301");
            r.Exec.OnFill(Notice(301, BridgeFillStatus.Rejected));
            Assert.Equal(0, r.Router.TrackedOrders);                          // ★0ms 반납
            Assert.Equal(0, r.Exec.Tracked);
            Assert.Equal(1, r.Router.TerminalReleases);
            Assert.Equal(1, r.Exec.RejectedReleased);
            var rec = Assert.Single(r.Records);
            Assert.Equal(ReconciliationKind.Rejected, rec.Kind);
            Assert.Equal(10, rec.Qty);
            r.Now += 200_000_000;                                              // TTL(2분) 경과
            Assert.Equal(0, r.Exec.SweepExpired());
            Assert.False(mask.Blocked);                                        // ★무장 유지
            r.Price.OnTick(ReferencePriceFeedAdapter.SymbolHashForCode(Code), 70_000, 2);   // 참조가 재신선화
            Admit(r, 2, "302");                                                // 슬롯 즉시 재사용
        }

        [Fact]
        public void Accepted_UsesLongTtl_NotShortResponseTtl()
        {
            var r = Make(ttl: 120_000_000, acceptedTtl: 600_000_000);
            Admit(r, 1, "401");
            Admit(r, 2, "402");                                                // 무응답(접수 통보 없음)
            r.Exec.OnFill(Notice(401, BridgeFillStatus.Accepted, remaining: 10));
            Assert.Equal(1, r.Exec.AcceptedNotices);
            r.Now += 120_000_001;
            Assert.Equal(1, r.Exec.SweepExpired());                            // 무응답만 회수(오펀 위험) — 접수 주문은 유지
            Assert.Equal(1, r.Router.TrackedOrders);
            Assert.True(r.Exec.ArmingBlocked);                                 // 무응답 회수는 여전히 차단(안전 측)
            r.Now += 600_000_000;
            Assert.Equal(1, r.Exec.SweepExpired());
            Assert.Contains(r.Records, x => x.Kind == ReconciliationKind.ExpiredAccepted);
            Assert.Equal(0, r.Router.TrackedOrders);
        }

        [Fact]
        public void CancelConfirmed_NonCascade_Released_Cascade_Confirmed()
        {
            var r = Make();
            Admit(r, 1, "501");
            r.Exec.OnFill(Notice(501, BridgeFillStatus.CancelConfirmed));     // 운용자/브로커 직접 취소
            Assert.Equal(0, r.Router.TrackedOrders);
            Assert.Equal(1, r.Router.TerminalReleases);
            Admit(r, 2, "502");
            r.Pending.CancelAllForBroker(0);
            r.Exec.OnFill(Notice(502, BridgeFillStatus.CancelConfirmed));     // 캐스케이드 취소 정식 확인
            Assert.Equal(0, r.Router.TrackedOrders);
            Assert.Equal(1, r.Router.TerminalReleases);
            Assert.All(r.Records, x => Assert.Equal(ReconciliationKind.CancelConfirmed, x.Kind));
        }

        [Fact]
        public void LegacyZeroQty_KeepsPreviousBehavior()
        {
            var r = Make();
            Admit(r, 1, "601");
            r.Exec.OnFill(new FillPayload { OrderId = 601, SymbolCode = Code, FillQty = 0, Side = 1 });   // 구 프록시(Status 0)
            Assert.Equal(1, r.Router.TrackedOrders);                          // 모호 → 유지(구 동작)
            Assert.Equal(1, r.Exec.ZeroQtyIgnored);
            Assert.Equal(0, r.Exec.RejectedReleased);
        }

        // ════════════ §7 방향 B · 방향 A · E2E · δ=0 ════════════

        [Fact]
        public void StateMismatch_OnAcceptOrFill_DisarmsSticky()
        {
            var r = Make();
            var mask = new ArmingInterlockBitmask(ArmingInterlockSource.BrokerStateMismatch);
            r.Mismatch.BindInterlock(mask, ArmingInterlockSource.BrokerStateMismatch);
            Assert.True(mask.Seal(out _));
            Admit(r, 1, "701");
            r.Exec.OnFill(Notice(701, BridgeFillStatus.Accepted, remaining: 10));          // 일치
            r.Exec.OnFill(Notice(701, BridgeFillStatus.Filled, qty: 4, px: 70_000, remaining: 6));   // 일치
            Assert.False(mask.Blocked);
            r.Exec.OnFill(Notice(701, BridgeFillStatus.Filled, qty: 2, px: 70_000, remaining: 1));   // 추적 4 ≠ 브로커 1
            Assert.True(mask.Blocked);
            Assert.True(r.Mismatch.Tripped);
            Assert.Contains(r.Records, x => x.Kind == ReconciliationKind.BrokerStateMismatch && x.Qty == 1);
            Assert.Equal(1, r.Router.TrackedOrders);                          // 대사 진행은 유지(추적 보존)
            Assert.False(r.Mismatch.TryRearm(false, true));
            Assert.True(r.Mismatch.TryRearm(true, true));
            Assert.False(mask.Blocked);

            var r2 = Make();
            Admit(r2, 1, "702");
            r2.Exec.OnFill(Notice(702, BridgeFillStatus.Accepted, remaining: 7));          // 주문 10 ≠ 브로커 7
            Assert.True(r2.Mismatch.Tripped);
            r2.Exec.OnFill(Notice(702, BridgeFillStatus.Filled, qty: 3, px: 70_000));      // 잔량 미동봉 = 대조 생략
            Assert.Equal(1, r2.Mismatch.Trips);
        }

        [Fact]
        public void Reconciliation_ChainedIntoForensicStream()
        {
            var r = Make();
            var stream = new ZeroAllocForensicAuditStream(64);
            r.Exec.AttachForensicStream(stream);
            Admit(r, 1, "801");
            r.Exec.OnFill(Notice(801, BridgeFillStatus.Rejected));
            Assert.Equal(1L, stream.WriteHead);
            Assert.True(stream.VerifyDetailed().Intact);
        }

        [Fact]
        public void E2E_PackedRejectFrame_ThroughReader_ReleasesSlot()
        {
            var r = Make();
            Admit(r, 1, "901");
            var src = Notice(901, BridgeFillStatus.Rejected);
            byte[] frame = BridgeSerializer.Pack(BridgeMessageType.OrderRejected, in src);
            Assert.True(BridgeFillReader.TryReadFill(frame.AsSpan(BridgeSerializer.HeaderSize), out var parsed));
            r.Exec.OnFill(parsed);
            Assert.Equal(0, r.Router.TrackedOrders);
        }

        [Fact]
        public void HotPaths_ZeroAlloc()
        {
            var r = Make(acceptedTtl: long.MaxValue / 4);
            var ids = new string[600];
            for (int i = 0; i < ids.Length; i++) ids[i] = (20_000 + i).ToString();
            var src = Notice(1, BridgeFillStatus.Rejected, remaining: 0);
            byte[] frame = BridgeSerializer.Pack(BridgeMessageType.OrderRejected, in src);
            long sink = 0;
            Admit(r, 1, ids[0]); r.Exec.OnFill(Notice(20_000, BridgeFillStatus.Rejected));
            Assert.Equal(0L, MinAllocated(() =>
            {
                ReadOnlySpan<byte> pl = frame.AsSpan(BridgeSerializer.HeaderSize);
                for (int i = 0; i < 10_000; i++)
                {
                    if (BridgeFillReader.TryReadFill(pl, out var f)) sink += (long)BridgeFillStatusCodec.Decode(in f, out long rem) + rem;
                    sink += (long)BridgeFillReader.PeekStatus(pl);
                }
            }));
            long next = 1;
            Assert.Equal(0L, MinAllocated(() =>
            {
                for (int i = 1; i < 400; i++)
                {
                    long id = ++next;
                    var p = new OrderProposal(id, 0, Code, OrderSide.Buy, 1, QPrice.Of(70_000m), QQty.Of(10m), 0);
                    if (!r.Gate.TryAdmit(in p, out _)) continue;
                    r.Gate.OnBrokerAccepted(id, ids[i]);
                    long bid = 20_000 + i;
                    r.Exec.OnFill(Notice(bid, BridgeFillStatus.Accepted, remaining: 10));
                    if ((i & 1) == 0) r.Exec.OnFill(Notice(bid, BridgeFillStatus.Rejected));
                    else r.Exec.OnFill(Notice(bid, BridgeFillStatus.CancelConfirmed));
                    sink++;
                }
            }));
            Assert.True(sink > 0);
            Assert.Equal(0, r.Router.TrackedOrders);
        }

        [Fact]
        public void Host_WiresMismatchInterlock_AndForensicReconciliation()
        {
            const BindingFlags AllF = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
            var host = typeof(StockManager.Blazor.BlazorHomeForm);
            var targets = new MethodBase[]
            {
                typeof(BrokerStateMismatchInterlock).GetMethod(nameof(BrokerStateMismatchInterlock.BindInterlock))!,
                typeof(OrderExecutionFeedAdapter).GetMethod(nameof(OrderExecutionFeedAdapter.AttachForensicStream))!,
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
