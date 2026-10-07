// ============================================================================
// KisVtsProtocolIntegrationTests — [KIS VTS · 추천 A]
//
//  §1 TR 도메인 대조 — 모의 VTTC0802U/0801U/0803U/8434R/8908R · H0STCNI9 ↔ 실 TTTC…/H0STCNI0 · 브로커 상수 정합 · 응답 tr_id 헤더 정합.
//  §2 KisVtsProtocolValidator — 교차 도메인 = sticky Disarm(비트 26) + 격리 1회 + 보안 로그(원문 0) · 주문/잔고 응답 봉투 · 업무 거부 ≠ 이탈 · δ=0.
//  §3 [실결함] Zero Protocol Leakage — 절단 후 마스킹은 경계에 걸친 등록 비밀값 접두를 흘린다 → 마스킹 후 절단.
//  §4 어댑터 핀 — 모의 세션: 실 도메인 통보/구독 응답 = 폐기 + Disarm + KIS 슬롯 격리 · 타 계좌 통보 = 폐기(비 Disarm).
//  §5 E2E — 키움 시뮬레이션 프록시(실 파이프) + KIS VTS 모의 스트림 동시 구동 → 자식 분할 체결 수렴(가중평균) · 500ms 불균형 차단 ·
//     ★프로토콜 이탈 0ms 격리(미송출 회수 + 송출분 취소 = 미체결 슬롯 고착 0).
//  §6 δ=0 — 암호 VTS 통보 → 도메인/계좌 핀 → 매트릭스 + 원장 핫패스 · [방향 A] 고정 메모리 50,000 프레임 리플레이(할당 0 · 처리량 실측).
//  §7 [방향 B] 포지션 원장 대사 — 기준선 + 순체결 ≡ 브로커 보유 · 불일치 연속 2회 = 비트 24 Disarm + 한도 무효 · 조회 중 체결 = 판정 보류.
//  §8 [실결함] 인터락 봉인 순서 · 호스트 결선 · 오중 빌드 회귀.
// ============================================================================
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MS2E.Core;
using MS2E.Infrastructure;
using StockManager.Adapters;
using StockManager.Engines;
using StockManager.Simulation;
using Xunit;
using Xunit.Abstractions;

namespace StockManager.Tests
{
    [Collection("ConcurrencyStress")]
    public sealed class KisVtsProtocolIntegrationTests
    {
        private readonly ITestOutputHelper _out;
        public KisVtsProtocolIntegrationTests(ITestOutputHelper output) => _out = output;

        private const string Key = "abcdefghijklmnopqrstuvwxyz012345", Iv = "ABCDEFGHIJKLMNOP";
        private const string Cano = "50000000";                                         // 합성 모의 계좌(실 자격증명 아님)
        private static byte[] U8(string s) => Encoding.UTF8.GetBytes(s);

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

        private sealed class Clock { public long Now = 1_000_000; }

        private static ChildOrderPlan Plan(long parent, int idx, int broker, long shares)
            => new() { ChildId = ChildIdSpace.Compose(parent, idx), ParentId = parent, Shares = shares, Broker = broker, Index = idx };

        // ════════════ §1 TR 도메인 ════════════

        [Fact]
        public void TrDomain_VtsVsReal_Table_And_BrokerConstants()
        {
            foreach (var t in new[] { "VTTC0802U", "VTTC0801U", "VTTC0803U", "VTTC8434R", "VTTC8908R", "VTTC8001R", "H0STCNI9" })
                Assert.Equal(KisTrDomain.Vts, KisProtocol.ClassifyTr(U8(t)));
            foreach (var t in new[] { "TTTC0802U", "TTTC0801U", "TTTC0803U", "TTTC8434R", "TTTC8908R", "H0STCNI0" })
                Assert.Equal(KisTrDomain.Real, KisProtocol.ClassifyTr(U8(t)));
            Assert.Equal(KisTrDomain.Neutral, KisProtocol.ClassifyTr(U8("H0STCNT0")));
            Assert.Equal(KisTrDomain.Neutral, KisProtocol.ClassifyTr(U8("PINGPONG")));
            foreach (var t in new[] { "XTTC0802U", "VTTC0802", "VTTC0802u", "VTTX0802U", "", "FHKST01010100" })
                Assert.Equal(KisTrDomain.Unknown, KisProtocol.ClassifyTr(U8(t)));
            // KisLiveBroker 주문 TR 상수 = 도메인 정합(모의 = VTTC · 실 = TTTC)
            foreach (var t in new[] { KisLiveBroker.TrBuyVts, KisLiveBroker.TrSellVts, KisLiveBroker.TrCancelVts })
                Assert.Equal(KisTrDomain.Vts, KisProtocol.ClassifyTr(U8(t)));
            foreach (var t in new[] { KisLiveBroker.TrBuyReal, KisLiveBroker.TrSellReal, KisLiveBroker.TrCancelReal })
                Assert.Equal(KisTrDomain.Real, KisProtocol.ClassifyTr(U8(t)));
            Assert.Equal(("VTTC0802U", "VTTC0801U"), (KisLiveBroker.TrBuyVts, KisLiveBroker.TrSellVts));   // 지시서 대조
            Assert.True(KisProtocol.ResponseTrIdConsistent("VTTC0802U", "VTTC0802U"));
            Assert.True(KisProtocol.ResponseTrIdConsistent("VTTC0802U", ""));                        // 헤더 부재 = 판정 불가 → 통과
            Assert.False(KisProtocol.ResponseTrIdConsistent("VTTC0802U", "TTTC0802U"));
            Assert.False(KisProtocol.ResponseTrIdConsistent("VTTC0802U", "vttc0802u"));              // 서수 비교
        }

        // ════════════ §2 검증기 ════════════

        private const string OrderAckOk = "{\"rt_cd\":\"0\",\"msg_cd\":\"APBK0013\",\"msg1\":\"주문 전송 완료 되었습니다.\",\"output\":{\"KRX_FWDG_ORD_ORGNO\":\"91252\",\"ODNO\":\"0000117057\",\"ORD_TMD\":\"121052\"}}";
        private const string OrderAckReject = "{\"rt_cd\":\"1\",\"msg_cd\":\"APBK0919\",\"msg1\":\"모의투자 장종료 입니다.\",\"output\":{}}";
        private const string Buyable = "{\"rt_cd\":\"0\",\"msg_cd\":\"KIOK0000\",\"output\":{\"ord_psbl_cash\":\"1400000\"}}";
        private static string Holdings(params (string Sym, long Qty)[] rows)
        {
            var sb = new StringBuilder("{\"rt_cd\":\"0\",\"output1\":[");
            for (int i = 0; i < rows.Length; i++)
                sb.Append(i > 0 ? "," : "").Append("{\"pdno\":\"").Append(rows[i].Sym).Append("\",\"hldg_qty\":\"").Append(rows[i].Qty).Append("\",\"ord_psbl_qty\":\"").Append(rows[i].Qty).Append("\"}");
            return sb.Append("],\"output2\":[{\"dnca_tot_amt\":\"1500000\"}]}").ToString();
        }

        [Fact]
        public void Validator_CrossDomain_StickyDisarm_IsolateOnce_LogCarriesNoPayload()
        {
            var logs = new List<string>();
            int isolations = 0;
            var v = new KisVtsProtocolValidator(vts: true, (k, label) => logs.Add(k + "|" + label), () => isolations++);
            var mask = new ArmingInterlockBitmask(ArmingInterlockSource.KisProtocolDivergence);
            v.BindInterlock(mask, ArmingInterlockSource.KisProtocolDivergence);
            Assert.False(mask.IsRaised(ArmingInterlockSource.KisProtocolDivergence));
            Assert.True(v.CheckRealtimeTr(U8("H0STCNI9")));
            Assert.True(v.CheckRealtimeTr(U8("H0STCNT0")));
            Assert.True(v.CheckRealtimeTr(U8("ZZZZ")));                                        // 미구독 TR = 어댑터 무시(이탈 아님)
            Assert.False(v.Tripped);
            Assert.False(v.CheckRealtimeTr(U8("H0STCNI0")));                                    // ★모의 세션에 실 체결통보
            Assert.False(v.CheckSubscribeDomain(KisTrDomain.Real));
            Assert.True(mask.IsRaised(ArmingInterlockSource.KisProtocolDivergence));
            Assert.Equal((2L, 1L, 1), (v.Divergences, v.Trips, isolations));                   // 격리 = 최초 1회
            Assert.Equal(KisDivergenceKind.RealtimeDomain, v.LastKind);
            Assert.Equal(new[] { "RealtimeDomain|expected=VTS" }, logs.ToArray());             // 종류 + 상수 라벨뿐(원문 0)
            Assert.False(v.TryRearm(true, false));
            Assert.True(v.TryRearm(true, true));
            Assert.False(mask.IsRaised(ArmingInterlockSource.KisProtocolDivergence));
            var real = new KisVtsProtocolValidator(vts: false);
            Assert.False(real.CheckRealtimeTr(U8("H0STCNI9")));                                 // 실 세션에 모의 통보 = 대칭
        }

        [Fact]
        public void Validator_OrderAndBalanceEnvelopes_BusinessRejectIsNotDivergence()
        {
            var v = new KisVtsProtocolValidator(vts: true);
            Assert.True(v.ValidateOrderResponse("VTTC0802U", "VTTC0802U", U8(OrderAckOk), out var ack));
            Assert.True(ack.Ok);
            Assert.Equal((117057L, 91252L), (ack.OrderNo, ack.OrgNo));
            Assert.True(v.ValidateOrderResponse("VTTC0801U", "", U8(OrderAckOk), out _));      // 헤더 부재 = 통과
            Assert.True(v.ValidateOrderResponse("VTTC0802U", "VTTC0802U", U8(OrderAckReject), out var rej));
            Assert.False(rej.Ok);                                                               // 업무 거부 = ack 실패(이탈 아님)
            Span<KisHolding> rows = stackalloc KisHolding[8];
            Assert.Equal(2, v.ValidateBalanceResponse("VTTC8434R", "VTTC8434R", U8(Holdings(("005930", 10), ("035420", 3))), rows));
            Assert.Equal(-1, v.ValidateBalanceResponse("VTTC8434R", "VTTC8434R", U8("{\"rt_cd\":\"1\",\"msg_cd\":\"EGW00201\",\"msg1\":\"초당 거래건수를 초과하였습니다.\"}"), rows));
            Assert.False(v.Tripped);                                                            // 모의 호출 제한 = 이탈 아님(비트 24 몫)

            var h = new KisVtsProtocolValidator(vts: true);
            Assert.False(h.ValidateOrderResponse("VTTC0802U", "TTTC0802U", U8(OrderAckOk), out var hdr));
            Assert.Equal((KisDivergenceKind.RestTrHeader, false), (h.LastKind, hdr.Ok));
            var d = new KisVtsProtocolValidator(vts: true);
            Assert.False(d.ValidateOrderResponse("TTTC0802U", "TTTC0802U", U8(OrderAckOk), out _));   // 실 TR 을 모의 세션에서
            Assert.Equal(KisDivergenceKind.RestTrDomain, d.LastKind);
            var m = new KisVtsProtocolValidator(vts: true);
            Assert.False(m.ValidateOrderResponse("VTTC0802U", "VTTC0802U", U8("{\"output\":{\"ODNO\":\"1\"}"), out _));
            Assert.Equal(KisDivergenceKind.RestMalformed, m.LastKind);
            var b = new KisVtsProtocolValidator(vts: true);
            Assert.Equal(-1, b.ValidateBalanceResponse("VTTC8434R", "TTTC8434R", U8(Holdings(("005930", 10))), rows));
            Assert.True(b.Tripped);
        }

        [Fact]
        public void Validator_HotPaths_ZeroAlloc()
        {
            var v = new KisVtsProtocolValidator(vts: true);
            byte[] ok = U8(OrderAckOk), hold = U8(Holdings(("005930", 10), ("035420", 3))), tr = U8("H0STCNI9"), tick = U8("H0STCNT0");
            var rows = new KisHolding[8];
            for (int i = 0; i < 50; i++) { v.ValidateOrderResponse("VTTC0802U", "VTTC0802U", ok, out _); v.ValidateBalanceResponse("VTTC8434R", "", hold, rows); }
            Assert.Equal(0, MinAllocated(() =>
            {
                for (int i = 0; i < 500; i++)
                {
                    v.CheckRealtimeTr(tr); v.CheckRealtimeTr(tick); KisProtocol.ClassifyTr(tr);
                    v.ValidateOrderResponse("VTTC0802U", "VTTC0802U", ok, out _);
                    v.ValidateBalanceResponse("VTTC8434R", "VTTC8434R", hold, rows);
                }
            }));
            Assert.False(v.Tripped);
        }

        // ════════════ §3 Zero Protocol Leakage ════════════

        [Fact]
        public void TruncateThenMask_LeaksSecretPrefix_MaskThenTruncate_DoesNot()
        {
            string secret = "VTSTOKEN" + Guid.NewGuid().ToString("N");                        // 등록 비밀값(합성)
            Assert.True(FinancialPiiMasker.RegisterSecret(secret));
            string body = new string('x', 170) + secret + "\"}";                                // 비밀값이 180자 경계에 걸침
            static string Trunc(string s) => s.Length > 180 ? s[..180] : s;
            string before = FinancialPiiMasker.Mask(Trunc(body));                                // 수정 전 순서
            string after = Trunc(FinancialPiiMasker.Mask(body));                                 // 수정 후 순서
            Assert.Contains(secret[..10], before);                                               // ★접두 10자 유출(실결함 재현)
            Assert.DoesNotContain(secret[..4], after);
            string src = File.ReadAllText(Path.Combine(RepoRoot(), "src", "02_Infrastructure", "Adapters", "Brokers", "LiveBrokers.cs"));
            Assert.DoesNotContain("Mask(Trunc(", src);
            Assert.Equal(2, Regex.Matches(src, Regex.Escape("Trunc(FinancialPiiMasker.Mask(body))")).Count);   // KIS · 업비트 오류 본문
            Assert.Contains("if (!KisProtocol.ResponseTrIdConsistent(s, g))", src);              // 응답 tr_id 헤더 대조
            Assert.Contains("return (false, \"\");", src);                                       // 이탈 응답 본문 미반환
            Assert.Contains("응답 TR 이탈({trId}) — 주문 상태 미확정", src);
        }

        // ════════════ §4 어댑터 도메인 · 계좌 핀 ════════════

        private static (KisOpenApiAdapter Adapter, KisVtsProtocolValidator Validator, List<FillPayload> Fills) VtsAdapter(Action? onIsolate = null)
        {
            var fills = new List<FillPayload>();
            var v = new KisVtsProtocolValidator(vts: true, onIsolate: onIsolate);
            var a = new KisOpenApiAdapter(new KisSessionInterlock(null, 1, () => 1), onFill: fills.Add, validator: v, accountCano: Cano);
            return (a, v, fills);
        }

        [Fact]
        public void Adapter_VtsSession_AcceptsVts_RejectsRealDomain_IgnoresForeignAccount()
        {
            using var s = new KisVtsMockStreamer(Key, Iv, account: Cano + "01");
            int ack = s.AddSubscribeAck();
            int fill = s.AddNotice(117057, +1, "005930", 3, 71_900, 10);
            int foreign = s.AddNotice(117058, +1, "005930", 1, 71_900, 1, account: "6000000001");
            int realNotice = s.AddNotice(117059, +1, "005930", 1, 71_900, 1, trId: KisVtsMockStreamer.RealNoticeTr);

            int isolations = 0;
            var (a, v, fills) = VtsAdapter(() => isolations++);
            Assert.True(a.OnFrame(s.Frame(ack)));
            Assert.True(a.HasNoticeKeys);                                                        // 모의 구독 응답 = 키 장착
            Assert.True(a.OnFrame(s.Frame(fill)));
            Assert.Single(fills);
            Assert.Equal((KisOrderIdSpace.Encode(117057), 3L, 71_900.0, 1), (fills[0].OrderId, fills[0].FillQty, fills[0].FillPrice, fills[0].Side));
            Assert.False(a.OnFrame(s.Frame(foreign)));                                           // 같은 HTS ID 의 타 계좌 = 폐기
            Assert.Equal(1, a.ForeignAccountNotices);
            Assert.False(v.Tripped);                                                             // 타 계좌는 이탈 아님
            Assert.False(a.OnFrame(s.Frame(realNotice)));                                        // ★모의 세션에 실 도메인 통보
            Assert.Single(fills);
            Assert.True(v.Tripped);
            Assert.Equal(1, isolations);

            using var s2 = new KisVtsMockStreamer(Key, Iv);
            int realAck = s2.AddSubscribeAck(KisVtsMockStreamer.RealNoticeTr);
            var (a2, v2, _) = VtsAdapter();
            Assert.False(a2.OnFrame(s2.Frame(realAck)));                                         // 교차 도메인 구독 응답
            Assert.False(a2.HasNoticeKeys);                                                      // ★키 장착 거부
            Assert.Equal(KisDivergenceKind.SubscribeDomain, v2.LastKind);
        }

        // ════════════ §5 E2E ════════════

        private static Task KiwoomSimProxy(string pipe, FillPayload[] fills, ManualResetEventSlim go)
            => Task.Run(async () =>
            {
                using var srv = new NamedPipeServerStream(pipe, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await srv.WaitForConnectionAsync();
                var hello = new ProxyHelloPayload { ApiKind = ProxyApiKind.Simulation, ProtocolVersion = ProxyApiKind.ProtocolVersion };
                await srv.WriteAsync(BridgeSerializer.Pack(BridgeMessageType.ProxyHello, in hello));
                go.Wait(5000);
                long seq = 0;
                foreach (var f in fills) { var fv = f; await srv.WriteAsync(BridgeSerializer.Pack(BridgeMessageType.OrderFilled, in fv)); }
                for (int i = 0; i < 20; i++) { try { await srv.WriteAsync(BridgeSerializer.PackHeartbeat(++seq)); await srv.FlushAsync(); } catch (IOException) { break; } await Task.Delay(25); }
            });

        private static FillPayload KwFill(long orderId, long qty, double px)
            => new() { OrderId = orderId, SymbolCode = 5930, FillPrice = px, FillQty = qty, Side = 1, TimestampTicks = 1 };

        [Fact]
        public async Task E2E_KiwoomSimProxy_And_KisVtsStream_Concurrent_SplitConvergence_WeightedAverage()
        {
            var clk = new Clock();
            var cancels = new ConcurrentQueue<(int, long)>();
            var m = new ChildSlotTrackingMatrix(8, () => clk.Now, cancel: (b, o) => cancels.Enqueue((b, o)));
            const long parent = 7001, kwId = 900_001;
            long kisId = KisOrderIdSpace.Encode(117057);
            Assert.True(m.Open(parent, 100, new[] { Plan(parent, 0, 0, 60), Plan(parent, 1, 1, 40) }));
            m.MarkSent(ChildIdSpace.Compose(parent, 0), kwId);
            m.MarkSent(ChildIdSpace.Compose(parent, 1), kisId);

            // 키움 시뮬레이션 프록시(실 파이프 · ProxyHello=Simulation) → 브리지 → 매트릭스
            string pipe = "ms2e_vts_" + Guid.NewGuid().ToString("N");
            using var go = new ManualResetEventSlim(false);
            var srv = KiwoomSimProxy(pipe, new[] { KwFill(kwId, 30, 70_000), KwFill(kwId, 30, 70_100) }, go);
            using var bridge = new KiwoomBridgeClient(pipeName: pipe, heartbeatTimeoutMs: 10_000);
            bridge.FillReceived += m.OnFill;
            await bridge.ConnectAsync();
            var loop = bridge.StartAsync();
            Assert.True(SpinWait.SpinUntil(() => bridge.ProxyApiKind == ProxyApiKind.Simulation, 3000));

            // KIS VTS 모의 스트림(암호 H0STCNI9) → 도메인/계좌 핀 어댑터 → 매트릭스 — 키움 송출과 동시
            using var s = new KisVtsMockStreamer(Key, Iv, account: Cano + "01");
            int ack = s.AddSubscribeAck(), n1 = s.AddNotice(117057, +1, "005930", 25, 70_000, 40), n2 = s.AddNotice(117057, +1, "005930", 15, 70_200, 40);
            var v = new KisVtsProtocolValidator(vts: true);
            var a = new KisOpenApiAdapter(new KisSessionInterlock(null, 1, () => 1), onFill: m.OnFill, validator: v, accountCano: Cano);
            Assert.True(a.OnFrame(s.Frame(ack)));
            go.Set();
            Assert.True(a.OnFrame(s.Frame(n1)));
            Assert.True(a.OnFrame(s.Frame(n2)));

            Assert.True(SpinWait.SpinUntil(() => m.TryGetParent(parent, out var p) && p.State == ParentConvergence.AllFilled, 5000));
            Assert.True(m.TryGetParent(parent, out var snap));
            Assert.Equal(100, snap.FilledQty);
            Assert.Equal((30 * 70_000L + 30 * 70_100L + 25 * 70_000L + 15 * 70_200L) / 100, snap.AvgFillPriceWon);   // 브로커 교차 가중평균
            clk.Now += 600_000;
            Assert.Equal(0, m.Evaluate());                                                        // 전량 수렴 = 불균형 아님
            Assert.Equal((0L, false, false), (m.ImbalanceTrips, m.Tripped, v.Tripped));
            Assert.Empty(cancels);
            bridge.Dispose(); await srv; try { await loop.WaitAsync(TimeSpan.FromSeconds(1)); } catch { }
        }

        [Fact]
        public void E2E_KisVtsSilent_500msImbalance_CancelsKisChild()
        {
            var clk = new Clock();
            var cancels = new List<(int, long)>();
            var m = new ChildSlotTrackingMatrix(8, () => clk.Now, cancel: (b, o) => cancels.Add((b, o)));
            const long parent = 7002, kwId = 900_002;
            long kisId = KisOrderIdSpace.Encode(117060);
            Assert.True(m.Open(parent, 100, new[] { Plan(parent, 0, 0, 50), Plan(parent, 1, 1, 50) }));
            m.MarkSent(ChildIdSpace.Compose(parent, 0), kwId);
            m.MarkSent(ChildIdSpace.Compose(parent, 1), kisId);
            m.OnFill(KwFill(kwId, 50, 70_000));                                                   // 키움 전량 · KIS 모의 침묵
            clk.Now += 499_000;
            Assert.Equal(0, m.Evaluate());
            clk.Now += 2_000;                                                                     // 500ms 초과
            Assert.Equal(1, m.Evaluate());
            Assert.Equal(new[] { (1, kisId) }, cancels.ToArray());                                // ★KIS 지연 자식 취소
            Assert.Equal((1L, true), (m.ImbalanceTrips, m.Tripped));
        }

        [Fact]
        public void E2E_VtsProtocolDivergence_IsolatesKisSlots_ZeroMs_NoStuckSlot()
        {
            var cancels = new List<(int, long)>();
            var m = new ChildSlotTrackingMatrix(8, cancel: (b, o) => cancels.Add((b, o)));
            const long parent = 7003, kwId = 900_003;
            long kisSent = KisOrderIdSpace.Encode(117061);
            Assert.True(m.Open(parent, 90, new[] { Plan(parent, 0, 0, 30), Plan(parent, 1, 1, 30), Plan(parent, 2, 1, 30) }));
            m.MarkSent(ChildIdSpace.Compose(parent, 0), kwId);
            m.MarkSent(ChildIdSpace.Compose(parent, 1), kisSent);                                // KIS 자식 2 = 미송출(Pending)
            var mask = new ArmingInterlockBitmask(ArmingInterlockSource.KisProtocolDivergence);
            var (a, v, fills) = VtsAdapter(() => m.IsolateBroker(1));
            v.BindInterlock(mask, ArmingInterlockSource.KisProtocolDivergence);
            using var s = new KisVtsMockStreamer(Key, Iv, account: Cano + "01");
            int ack = s.AddSubscribeAck(), bad = s.AddNotice(117061, +1, "005930", 30, 70_000, 30, trId: KisVtsMockStreamer.RealNoticeTr);
            Assert.True(a.OnFrame(s.Frame(ack)));
            Assert.False(a.OnFrame(s.Frame(bad)));                                                // ★교차 도메인 체결통보
            Assert.Empty(fills);                                                                  // 이탈 통보는 체결로 반영 0
            Assert.True(mask.IsRaised(ArmingInterlockSource.KisProtocolDivergence));
            Assert.Equal(new[] { (1, kisSent) }, cancels.ToArray());                              // 송출분 즉시 취소(0ms · 불균형 대기 없음)
            Assert.True(m.TryGetParent(parent, out var p));
            Assert.Equal(1, p.Reclaimed);                                                         // 미송출 KIS 자식 = 회수(송출 0)
            Assert.Equal((1L, true), (m.Isolations, m.Tripped));
            Assert.False(a.OnFrame(s.Frame(bad)));
            Assert.Single(cancels);                                                               // 격리 1회 · 키움 자식 무간섭
            Assert.Equal(0, m.IsolateBroker(1));                                                  // 재격리 = 이미 취소 중(중복 취소 0)
        }

        // ════════════ §6 δ=0 · [방향 A] 리플레이 ════════════

        [Fact]
        public void HotPath_EncryptedVtsNotice_ToMatrixAndLedger_ZeroAlloc()
        {
            var m = new ChildSlotTrackingMatrix(8);
            const long parent = 7004;
            long kisId = KisOrderIdSpace.Encode(117070);
            Assert.True(m.Open(parent, 10_000_000, new[] { Plan(parent, 0, 1, 10_000_000) }));
            m.MarkSent(ChildIdSpace.Compose(parent, 0), kisId);
            var bal = new KisBalanceQueryAdapter(_ => null, configured: true, reconcilePositions: true);
            var v = new KisVtsProtocolValidator(vts: true);
            Action<FillPayload> sink = f => { m.OnFill(f); bal.OnKisFill(f); };
            var a = new KisOpenApiAdapter(new KisSessionInterlock(null, 1, () => 1), onFill: sink, validator: v, accountCano: Cano);
            using var s = new KisVtsMockStreamer(Key, Iv, account: Cano + "01");
            int ack = s.AddSubscribeAck(), n = s.AddNotice(117070, +1, "005930", 1, 70_000, 10_000_000), t = s.AddTick("005930", 70_000, 5, 1000);
            Assert.True(a.OnFrame(s.Frame(ack)));
            for (int i = 0; i < 50; i++) { a.OnFrame(s.Frame(n)); a.OnFrame(s.Frame(t)); }
            Assert.Equal(0, MinAllocated(() => { for (int i = 0; i < 1000; i++) { a.OnFrame(s.Frame(n)); a.OnFrame(s.Frame(t)); } }));
            Assert.False(v.Tripped);
            Assert.True(a.FillsEmitted >= 1050);
            Assert.True(m.TryGetParent(parent, out var p) && p.FilledQty == a.FillsEmitted);
        }

        [Fact]
        public void DirectionA_PinnedReplay_50kFrames_ZeroAlloc_ThroughputMeasured()
        {
            using var s = new KisVtsMockStreamer(Key, Iv, account: Cano + "01");
            int ack = s.AddSubscribeAck();
            for (int i = 0; i < 900; i++) s.AddTick(i % 2 == 0 ? "005930" : "000660", 70_000 + i, 1 + i % 7, 1000 + i);
            for (int i = 0; i < 99; i++) s.AddNotice(200_000 + i, i % 2 == 0 ? 1 : -1, "005930", 1, 70_000, 5);
            s.AddPingPong();
            var v = new KisVtsProtocolValidator(vts: true);
            long fills = 0;
            var a = new KisOpenApiAdapter(new KisSessionInterlock(null, 1, () => 1), onFill: _ => fills++, validator: v, accountCano: Cano);
            Func<ReadOnlyMemory<byte>, bool> sink = a.OnFrame;
            Assert.True(a.OnFrame(s.Frame(ack)));
            s.Replay(sink, 2, 1);                                                                 // 예열(JIT · 복호 컨텍스트)
            KisReplayReport rep = default;
            long alloc = MinAllocated(() => rep = s.Replay(sink, 50, 1));
            _out.WriteLine($"[VTS 리플레이] {rep.Frames:N0} 프레임 · {rep.FramesPerSecond:N0} fps · p50≤{rep.P50Nanos:N0}ns · p99≤{rep.P99Nanos:N0}ns · max {rep.MaxNanos:N0}ns · 할당 {alloc}B");
            Assert.Equal(50_000, rep.Frames);
            Assert.Equal(50_000, rep.Accepted);                                                   // 전 프레임 인식(형식 오류 0)
            Assert.Equal(0, alloc);                                                               // ★고정 메모리 리플레이 δ=0
            Assert.True(rep.FramesPerSecond >= 10_000, "리플레이 처리량 하한 미달: " + rep.FramesPerSecond);   // 하한만 단언(50k 는 실측 보고)
            Assert.True(rep.P99Nanos > 0 && rep.MaxNanos >= rep.P50Nanos);
            Assert.False(v.Tripped);
            Assert.Equal(0, a.Malformed);
        }

        // ════════════ §7 [방향 B] 포지션 원장 대사 ════════════

        private static FillPayload KisFill(long sym, int side, long qty) => new() { OrderId = KisOrderIdSpace.Encode(1), SymbolCode = sym, FillPrice = 70_000, FillQty = qty, Side = side };

        [Fact]
        public void DirectionB_LedgerMatchesBroker_TwoConsecutiveMismatches_DisarmBit24_InvalidateLimits()
        {
            var bal = new KisBalanceQueryAdapter(_ => null, configured: true, reconcilePositions: true);
            var mask = new ArmingInterlockBitmask(ArmingInterlockSource.KisLimitFeed);
            bal.BindInterlock(mask, ArmingInterlockSource.KisLimitFeed);
            byte[] b = U8(Buyable);
            Assert.True(bal.Ingest(b, U8(Holdings(("005930", 10), ("035420", 3)))));
            Assert.True(bal.Baselined);                                                           // 최초 = 기준선
            bal.OnKisFill(KisFill(5930, +1, 5));
            bal.OnKisFill(KisFill(35420, -1, 3));                                                 // 전량 매도
            bal.OnKisFill(KisFill(660, +1, 2));                                                   // 신규 종목
            bal.OnKisFill(new FillPayload { SymbolCode = 5930, FillQty = 0, Side = 1 });           // 접수 통보 = 보유 불변
            Assert.True(bal.Ingest(b, U8(Holdings(("005930", 15), ("000660", 2)))));
            Assert.Equal((1L, 0), (bal.Reconciles, bal.MismatchStreak));                         // 기준선 + 순체결 ≡ 보유
            Assert.True(bal.Ingest(b, U8(Holdings(("005930", 14), ("000660", 2)))));              // 체결통보 유실 1주
            Assert.Equal(1, bal.MismatchStreak);
            Assert.False(mask.IsRaised(ArmingInterlockSource.KisLimitFeed));                     // 1회 = 유예(통보 지연 흡수)
            Assert.True(bal.Bound);
            Assert.True(bal.Ingest(b, U8(Holdings(("005930", 14), ("000660", 2)))));
            Assert.True(mask.IsRaised(ArmingInterlockSource.KisLimitFeed));                      // ★연속 2회 = Disarm(비트 24)
            Assert.Equal(1, bal.DiscrepancyTrips);
            Assert.False(bal.Bound);                                                              // 한도 즉시 무효 = 분할기 KIS 자식 0
            Assert.False(bal.TryGetBuyHeadroomWon(out _));
            Assert.True(bal.TryRearm(true, true));                                                // 물리 이중키 → 기준선 재수립
            Assert.False(bal.Baselined);
            Assert.True(bal.Ingest(b, U8(Holdings(("005930", 14), ("000660", 2)))));
            Assert.True(bal.Baselined);
            Assert.False(mask.IsRaised(ArmingInterlockSource.KisLimitFeed));
        }

        [Fact]
        public void DirectionB_FillDuringQuery_DefersJudgement_UnknownSymbolAndOverflow_Mismatch()
        {
            KisBalanceQueryAdapter? bal = null;
            bool inject = false;
            byte[] buy = U8(Buyable), hold = U8(Holdings(("005930", 10)));
            bal = new KisBalanceQueryAdapter(q =>
            {
                if (q == KisQuery.Holdings && inject) bal!.OnKisFill(KisFill(5930, +1, 1));   // 조회 진행 중 체결 도착
                return q == KisQuery.Buyable ? buy : hold;
            }, configured: true, reconcilePositions: true);
            inject = true;
            Assert.True(bal.PollOnce());
            Assert.False(bal.Baselined);                                                          // 모호 회차엔 기준선도 잡지 않음
            Assert.Equal(1, bal.AmbiguousSkips);
            inject = false;
            Assert.True(bal.PollOnce());                                                          // 기준선 = 10(유입 체결 1 은 원장에서 제거)
            Assert.True(bal.Baselined);
            Assert.True(bal.PollOnce());
            Assert.Equal(0, bal.MismatchStreak);

            var u = new KisBalanceQueryAdapter(_ => null, configured: true, reconcilePositions: true, maxMismatches: 1);
            Assert.True(u.Ingest(buy, U8(Holdings(("005930", 10)))));
            Assert.True(u.Ingest(buy, U8(Holdings(("005930", 10), ("035420", 1)))));             // 원장 밖 종목 출현(앱 밖 거래)
            Assert.True(u.Tripped);

            var o = new KisBalanceQueryAdapter(_ => null, configured: true, capacity: 2, reconcilePositions: true, maxMismatches: 1);
            Assert.True(o.Ingest(buy, U8(Holdings())));
            o.OnKisFill(KisFill(1, 1, 1)); o.OnKisFill(KisFill(2, 1, 1)); o.OnKisFill(KisFill(3, 1, 1));   // 원장 용량 초과
            Assert.True(o.Ingest(buy, U8(Holdings(("000001", 1), ("000002", 1)))));
            Assert.True(o.Tripped);                                                               // fail-closed

            var off = new KisBalanceQueryAdapter(_ => null, configured: true);                   // 대사 비활성(실시간 미결선)
            Assert.True(off.Ingest(buy, U8(Holdings(("005930", 10)))));
            off.OnKisFill(KisFill(5930, 1, 99));
            Assert.True(off.Ingest(buy, U8(Holdings(("005930", 1)))));
            Assert.False(off.Tripped);
        }

        // ════════════ §8 봉인 순서 · 결선 · 오중 빌드 ════════════

        private static string RepoRoot()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d is not null && !File.Exists(Path.Combine(d.FullName, "StockManager.sln"))) d = d.Parent;
            Assert.NotNull(d);
            return d!.FullName;
        }

        [Fact]
        public void Host_SealAfterEveryBind_ProtocolInterlockWired()
        {
            string root = RepoRoot();
            string form = File.ReadAllText(Path.Combine(root, "StockManager", "Blazor", "BlazorHomeForm.cs"));
            int seal = form.IndexOf("interlockMask.Seal(out long missingInterlocks)", StringComparison.Ordinal);
            Assert.True(seal > 0);
            Assert.Single(Regex.Matches(form, Regex.Escape("interlockMask.Seal(")));
            int lastBind = form.LastIndexOf("BindInterlock(interlockMask", StringComparison.Ordinal);
            Assert.True(seal > lastBind, "★Seal 이 필수 소스 결속보다 앞섬(봉인 실패 → 무장 영구 차단)");
            Assert.Contains("MS2E.Core.ArmingInterlockSource.KisProtocolDivergence, //", form);
            Assert.Contains("kisProtocol.BindInterlock(interlockMask, MS2E.Core.ArmingInterlockSource.KisProtocolDivergence);", form);
            Assert.Contains("onIsolate: () => { childMatrix.IsolateBroker(1); Volatile.Read(ref kisSession)?.MarkDisconnected(); }", form);
            Assert.Contains("kisLive.ResponseDivergence += _ => kisProtocol.Trip(StockManager.Adapters.KisDivergenceKind.RestTrHeader);", form);
            Assert.Contains("validator: kisProtocol, accountCano: kisLive!.AccountCano", form);
            Assert.Contains("kisBalance.OnKisFill(f);", form);
            Assert.Contains("reconcilePositions: kisRealtimeOn", form);
            Assert.Equal(26, (int)ArmingInterlockSource.KisProtocolDivergence);
            string sln = File.ReadAllText(Path.Combine(root, "StockManager.sln"));
            foreach (var proj in new[] { @"StockManager\StockManager.csproj", @"StockManager.PrimesBench\StockManager.PrimesBench.csproj",
                                         @"StockManager.KiwoomProxy\StockManager.KiwoomProxy.csproj", @"tools\KiwoomComShim\KiwoomComShim.csproj" })
                Assert.Contains(proj, sln);                                                       // 오중 빌드 대상(WebOnly = 동일 프로젝트 플래그)
        }

        [Fact]
        public void Bitmask_SealFailsWhenRequiredBoundAfter_SucceedsWhenBoundBefore()
        {
            // 실결함 기전 재현: 필수 비트 결속 전에 Seal → 실패(샌니티 비트 유지) · 결속 후 Seal → 성공
            var early = new ArmingInterlockBitmask(ArmingInterlockSource.KisProtocolDivergence);
            Assert.False(early.Seal(out long missing));
            Assert.Equal(1L << 26, missing);
            Assert.True(early.IsRaised(ArmingInterlockSource.StartupSanity));
            new KisVtsProtocolValidator(vts: true).BindInterlock(early, ArmingInterlockSource.KisProtocolDivergence);
            Assert.True(early.Seal(out _));
            Assert.False(early.IsRaised(ArmingInterlockSource.StartupSanity));
        }
    }
}
