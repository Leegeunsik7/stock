// ============================================================================
// KisOpenOrderReconciliationTests — [KIS 미체결 대사 · 추천 A] TTTC8001R 실배선 · 재접속 후 확정 해제 · 불일치 Disarm · 유령 비상 취소
//
//  §1 판독 δ=0: 20행 페이지 · ord_gno_brno(취소 조직번호) · 연속 조회 키(ctx_area_fk100/nk100) · 이탈 = 거부.
//  §2 조회기: 페이지 루프(키 · 연속 플래그 전달) → 확정 → 게이트 개방 · 실패(HTTP · 키 누락 · 페이지 상한) = Incomplete + 게이트 닫힘 ·
//     키움 배치 진행 중 = 덮어쓰기 0(바쁨) · 대사 요청 계수 변화 = 주기 무시 즉시 조회.
//  §3 재접속 해제: RequestReconcile → 한도 0 → 새 패스 확정 → 복구 관리자 '즉시'(펌프 없이) Connected · 격리 해제.
//  §4 불일치: 수량 괴리 = 비트 22 sticky + 게이트 닫힘 + 영구 래치(이후 일치 패스도 차단) + 호스트 격리 1회 · 이중키 리셋 후 새 확정에서만 개방.
//  §5 [방향 B] 유령 = 비트 22 + 비상 취소(ODNO + ord_gno_brno) 패스당 ≤4건 · 지점번호 미상 = 송신 안 함 · 5초 중복 억제.
//  §6 ★낡은 스냅샷 경합: 조회 진행 중 재접속(RequestReconcile) → 그 페이지는 폐기 · 확정 금지 → 다음 주기 새 스냅샷으로 확정.
//  §7 δ=0 핫경로 · 호스트 결선 · 오중 빌드 회귀.
// ============================================================================
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using MS2E.Core;
using MS2E.Infrastructure;
using StockManager.Adapters;
using Xunit;
using Xunit.Abstractions;

namespace StockManager.Tests
{
    [Collection("ConcurrencyStress")]
    public sealed class KisOpenOrderReconciliationTests
    {
        private readonly ITestOutputHelper _out;
        public KisOpenOrderReconciliationTests(ITestOutputHelper output) => _out = output;

        private const long Code = 5930, Limit = 70_000;
        private static readonly long Sec = TimeSpan.TicksPerSecond;

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

        private static string RowJson(long odno, long qty, long rmn, long price = Limit, string side = "02", long org = 91252, string cncl = "N")
            => "{\"odno\":\"" + odno.ToString("D10") + "\",\"orgn_odno\":\"\",\"pdno\":\"005930\",\"ord_qty\":\"" + qty + "\",\"rmn_qty\":\"" + rmn
               + "\",\"ord_unpr\":\"" + price + "\",\"sll_buy_dvsn_cd\":\"" + side + "\",\"cncl_yn\":\"" + cncl + "\",\"ord_gno_brno\":\"" + (org > 0 ? org.ToString("D5") : "") + "\"}";

        private static byte[] Page(string rows, string fk = "", string nk = "")
            => Encoding.UTF8.GetBytes("{\"ctx_area_fk100\":\"" + fk + "\",\"ctx_area_nk100\":\"" + nk + "\",\"output1\":[" + rows + "],\"rt_cd\":\"0\",\"msg1\":\"ok\"}");

        private static void Track(OpenOrderRig.Exec e, long proposal, long odno, long qty, bool buy = true, long limit = Limit)
        {
            var key = RouterFeedGate.KeyFor(proposal);
            Assert.True(e.Router.Route(key, limit * 100_000_000, limit * 100_000_000, 0.0).Accepted);
            Assert.True(e.Feed.TryTrack(proposal, key, 0, qty, buy, limit * 100_000_000, Code, limit));
            Assert.True(e.Feed.BindBrokerOrder(proposal, KisOrderIdSpace.Encode(odno)));
        }

        private sealed class BoundView : IBrokerAccountView
        {
            public bool Bound => true;
            public bool SharesGateReservation => false;
            public bool TryGetBuyHeadroomWon(out long won) { won = 1_000_000_000; return true; }
            public bool TryGetSellableShares(long symbolCode, out long shares) { shares = 1_000; return true; }
        }

        /// <summary>스크립트 페이지 전송(요청 기록 · 순서대로 응답).</summary>
        private sealed class FakeKis
        {
            public readonly Queue<(byte[]? Body, bool More)> Script = new();
            public readonly List<(string Fk, string Nk, bool Cont)> Calls = new();
            public Action? OnCall;
            public byte[]? Fetch(string fk, string nk, bool cont, out bool more)
            {
                Calls.Add((fk, nk, cont));
                OnCall?.Invoke();
                if (Script.Count == 0) { more = false; return null; }
                var (b, m) = Script.Dequeue();
                more = m;
                return b;
            }
        }

        private sealed class Rig
        {
            public long Now = 10_000 * Sec;
            public bool Armed = true;
            public OpenOrderRig.Exec E = null!;
            public BrokerOpenOrderReconciler R = null!;
            public ArmingInterlockBitmask Mask = null!;
            public BrokerLimitFeedProvider Limits = null!;
            public KisOpenOrderQuery Q = null!;
            public SessionRecoveryManager? M;
            public readonly FakeKis Kis = new();
            public readonly List<OpenOrderPassResult> Passes = new();
            public readonly List<(long Odno, long Org)> Cancels = new();
            public readonly BrokerSplitCap[] Caps = new BrokerSplitCap[2];
            public int HostIsolations;
            public void Advance(double s) => Now += (long)(s * Sec);
            public bool KisOpen() { Limits.Fill(Caps, Code, OrderSide.Buy, Limit * 100_000_000, 0); return Caps[1].Available; }
        }

        private static Rig MakeRig(bool withRecovery = false, bool canceller = true, int maxPages = 20)
        {
            var r = new Rig { E = OpenOrderRig.MakeExec() };
            KisOpenOrderQuery? q = null;
            r.R = new BrokerOpenOrderReconciler(r.E.Feed, null, null, requireKiwoom: false, requireKis: false, capacity: 64,
                                                nowTicks: () => Volatile.Read(ref r.Now), onPass: p => { r.Passes.Add(p); q?.OnPass(p); });
            r.Mask = new ArmingInterlockBitmask(ArmingInterlockSource.OpenOrderDivergence);
            r.R.BindInterlock(r.Mask, ArmingInterlockSource.OpenOrderDivergence);
            if (canceller) r.R.AttachKisCanceller((odno, org) => { r.Cancels.Add((odno, org)); return true; });
            r.R.AttachCancelArming(() => r.Armed);                              // [v12.18.7-DEV] 무장 경로 회귀(기본 무장 · 무장 가드 시험은 BrokerOpenOrderReconcilerTests)
            r.Limits = new BrokerLimitFeedProvider(new IBrokerAccountView?[] { new BoundView(), new BoundView() });
            if (withRecovery)
                r.M = new SessionRecoveryManager(1, on => r.Limits.SetSessionIsolated(1, on),
                    requestReconcile: () => r.R.RequestReconcile(OpenOrderVenueKind.Kis),
                    reconciled: () => r.R.IsConfirmed(OpenOrderVenueKind.Kis), nowTicks: () => r.Now);
            q = r.Q = new KisOpenOrderQuery(r.R, r.Kis.Fetch, r.Limits, r.M, () => r.HostIsolations++, maxPages: maxPages, nowTicks: () => r.Now);
            return r;
        }

        // ════════════ §1 판독 ════════════

        [Fact]
        public void Parse_TwentyRowPage_ZeroAlloc_OrgNo_Continuation()
        {
            var sb = new StringBuilder();
            for (int i = 0; i < 20; i++) { if (i > 0) sb.Append(','); sb.Append(RowJson(117_000 + i, 10, 10 - (i % 3), org: 91_200 + i)); }
            byte[] page = Page(sb.ToString(), fk: "FK" + new string('A', 60), nk: "NK0000000117019");
            var rows = new BrokerOpenOrderRow[64];
            Assert.Equal(20, KisProtocol.ParseOpenOrders(page, rows));
            Assert.Equal(117_005, rows[5].VenueOrderNo);
            Assert.Equal(91_205, rows[5].Reserved);                                      // ord_gno_brno = 취소 조직번호
            Assert.Equal(OpenOrderVenueKind.Kis, rows[5].Venue);
            Span<byte> fk = stackalloc byte[128], nk = stackalloc byte[128];
            Assert.True(KisProtocol.TryParseContinuation(page, fk, out int fl, nk, out int nl));
            Assert.Equal("FK" + new string('A', 60), Encoding.ASCII.GetString(fk[..fl]));
            Assert.Equal("NK0000000117019", Encoding.ASCII.GetString(nk[..nl]));
            Assert.Equal(0, MinAllocated(() => { for (int i = 0; i < 2_000; i++) KisProtocol.ParseOpenOrders(page, rows); }));
            byte[] fkb = new byte[128], nkb = new byte[128];
            Assert.Equal(0, MinAllocated(() => { for (int i = 0; i < 2_000; i++) KisProtocol.TryParseContinuation(page, fkb, out _, nkb, out _); }));
            // 이탈: 키 버퍼 초과 · 이스케이프 · 깨진 JSON = 거부(배치 폐기 근거)
            Assert.False(KisProtocol.TryParseContinuation(Page("", fk: new string('X', 200)), fkb, out _, nkb, out _));
            Assert.False(KisProtocol.TryParseContinuation(Encoding.UTF8.GetBytes("{\"ctx_area_fk100\":\"a\\u0041\"}"), fkb, out _, nkb, out _));
            Assert.False(KisProtocol.TryParseContinuation(Encoding.UTF8.GetBytes("{\"ctx_area_fk100\":"), fkb, out _, nkb, out _));
            Assert.Equal(-1, KisProtocol.ParseOpenOrders(Encoding.UTF8.GetBytes("{\"output1\":[" + RowJson(1, 5, 5).Replace("\"91252\"", "\"9x\"") + "],\"rt_cd\":\"0\"}"), rows));
        }

        // ════════════ §2 조회기 ════════════

        [Fact]
        public void Query_Pages_PassesContinuationKeys_ConfirmsAndOpensGate()
        {
            var r = MakeRig();
            Track(r.E, 1, 117_001, 6);
            Track(r.E, 2, 117_002, 4);
            Assert.False(r.KisOpen());                                                  // ★최초 확정 전 KIS 한도 0
            r.Kis.Script.Enqueue((Page(RowJson(117_001, 6, 6), fk: "FKEY1", nk: "NKEY1"), true));
            r.Kis.Script.Enqueue((Page(RowJson(117_002, 4, 4), fk: "FKEY2", nk: "NKEY2"), true));
            r.Kis.Script.Enqueue((Page(""), false));
            Assert.True(r.Q.Pump());
            Assert.Equal(3, r.Kis.Calls.Count);
            Assert.Equal(("", "", false), r.Kis.Calls[0]);
            Assert.Equal(("FKEY1", "NKEY1", true), r.Kis.Calls[1]);                      // 직전 응답 키 + 연속 플래그
            Assert.Equal(("FKEY2", "NKEY2", true), r.Kis.Calls[2]);
            Assert.Equal(OpenOrderVerdict.Confirmed, r.Passes[^1].Verdict);
            Assert.Equal(2, r.Passes[^1].Matched);
            Assert.True(r.R.IsConfirmed(OpenOrderVenueKind.Kis));
            Assert.False(r.Q.Blocked);
            Assert.True(r.KisOpen());
            // 평시 주기(30초) 전엔 재조회 없음
            r.Advance(29); Assert.False(r.Q.Pump());
            r.Kis.Script.Enqueue((Page(RowJson(117_001, 6, 6) + "," + RowJson(117_002, 4, 4)), false));
            r.Advance(1); Assert.True(r.Q.Pump());
            Assert.Equal(4, r.Kis.Calls.Count);
        }

        [Fact]
        public void Query_Failures_AreIncomplete_GateStaysClosed_NoRetryStorm()
        {
            // ① HTTP 실패(중도)
            var a = MakeRig();
            a.Kis.Script.Enqueue((Page("", fk: "F", nk: "N"), true));
            a.Kis.Script.Enqueue((null, false));
            Assert.True(a.Q.Pump());
            Assert.Equal(OpenOrderVerdict.Incomplete, a.Passes[^1].Verdict);
            Assert.Equal(1, a.Q.FetchFailures);
            Assert.False(a.KisOpen());
            Assert.False(a.R.QueryPending);
            Assert.False(a.Q.Pump());                                                   // 같은 주기 재시도 폭주 없음(빠른 주기 3초)
            a.Advance(3);
            a.Kis.Script.Enqueue((Page(""), false));
            Assert.True(a.Q.Pump());
            Assert.True(a.KisOpen());
            // ② more=true 인데 연속 키 없음 = 폐기(그 페이지도 대사기에 넣지 않음)
            var b = MakeRig();
            b.Kis.Script.Enqueue((Page(""), true));
            Assert.True(b.Q.Pump());
            Assert.Equal(1, b.Q.ContinuationFaults);
            Assert.Equal(OpenOrderVerdict.Incomplete, b.Passes[^1].Verdict);
            Assert.False(b.KisOpen());
            // ③ 페이지 상한
            var c = MakeRig(maxPages: 3);
            for (int i = 0; i < 5; i++) c.Kis.Script.Enqueue((Page("", fk: "F" + i, nk: "N" + i), true));
            Assert.True(c.Q.Pump());
            Assert.Equal(3, c.Kis.Calls.Count);
            Assert.Equal(1, c.Q.PageCapHits);
            Assert.Equal(OpenOrderVerdict.Incomplete, c.Passes[^1].Verdict);
            Assert.False(c.KisOpen());
            // ④ 키움 배치 진행 중 = KIS 조회가 덮어쓰지 않음
            var d = MakeRig();
            Assert.True(d.R.TryBeginSnapshot(OpenOrderVenueKind.Kiwoom));
            Assert.False(d.Q.Pump());
            Assert.Equal(1, d.Q.BusySkips);
            Assert.Empty(d.Kis.Calls);
            Assert.True(d.R.QueryPending);                                              // 키움 배치 그대로
            // ⑤ 조회 수단 없음(KIS 미설정) = 영구 격리
            var e = MakeRig();
            var none = new KisOpenOrderQuery(e.R, null, e.Limits);
            Assert.False(none.Pump());
            Assert.True(none.Blocked);
            Assert.False(e.KisOpen());
        }

        // ════════════ §3 재접속 해제 ════════════

        [Fact]
        public void Reconnect_RequestReconcile_ClosesGate_FreshConfirm_PushReleasesImmediately()
        {
            var r = MakeRig(withRecovery: true);
            var m = r.M!;
            Track(r.E, 1, 117_001, 6);
            r.Kis.Script.Enqueue((Page(RowJson(117_001, 6, 6)), false));
            Assert.True(r.Q.Pump());
            Assert.True(r.KisOpen());
            Assert.True(m.WaitForAttemptAsync().IsCompletedSuccessfully);
            m.OnConnected();                                                            // 최초
            m.OnConnectionLost();
            Assert.False(r.KisOpen());                                                  // 세션 격리
            r.Advance(1); m.Pump();
            Assert.True(m.WaitForAttemptAsync().IsCompletedSuccessfully);
            m.OnConnected();                                                            // 재접속 → RequestReconcile(Kis)
            Assert.Equal(SessionRecoveryState.Reconciling, m.State);
            Assert.False(r.R.IsConfirmed(OpenOrderVenueKind.Kis));
            r.Q.RefreshGate();
            Assert.True((r.Limits.ReconcileIsolatedMask & 2) != 0);
            m.OnReconciliationCompleted();                                              // 확정 전 통지(오발/중복) = 무시 — 락 안 프로브 재확인
            Assert.Equal(SessionRecoveryState.Reconciling, m.State);                     // ★대사 게이트도 닫힘(이중)
            // 평시 주기(30초) 전이어도 대사 요청 계수 변화 = 즉시 조회
            r.Kis.Script.Enqueue((Page(RowJson(117_001, 6, 6)), false));
            Assert.True(r.Q.Pump());
            Assert.Equal(OpenOrderVerdict.Confirmed, r.Passes[^1].Verdict);
            Assert.Equal(SessionRecoveryState.Connected, m.State);                     // ★펌프 없이 즉시(OnReconciliationCompleted)
            Assert.Equal(1, m.PushedReleases);
            Assert.False(m.Held || m.Isolated);
            Assert.True(r.KisOpen());
        }

        // ════════════ §4 불일치 ════════════

        [Fact]
        public void Divergence_RaisesBit22_LatchesIsolation_UntilDualKeyReset_AndFreshConfirm()
        {
            var r = MakeRig(withRecovery: true);
            Assert.True(r.M!.WaitForAttemptAsync().IsCompletedSuccessfully);
            r.M.OnConnected();                                                          // 세션 정상(Connected) — 이후 Held 는 오직 래치로만
            Assert.False(r.M.Held);
            Track(r.E, 1, 117_001, 6);
            r.Kis.Script.Enqueue((Page(RowJson(117_001, 6, 6)), false));
            Assert.True(r.Q.Pump());
            Assert.True(r.KisOpen());
            Assert.False(r.Mask.IsRaised(ArmingInterlockSource.OpenOrderDivergence));
            // 브로커 잔량 4 ≠ 호스트 6(체결 통보 유실)
            r.Kis.Script.Enqueue((Page(RowJson(117_001, 6, 4)), false));
            r.Advance(30); Assert.True(r.Q.Pump());
            Assert.Equal(OpenOrderVerdict.Divergence, r.Passes[^1].Verdict);
            Assert.True(r.Mask.IsRaised(ArmingInterlockSource.OpenOrderDivergence));    // ★비트 22 sticky
            Assert.False(r.KisOpen());
            Assert.True(r.Q.DiscrepancyLatched);
            Assert.True(r.M.Discrepancy && r.M.Isolated);
            Assert.True(r.M.Held);                                                      // ★연결 정상이어도 불일치 래치 = 격리(세션 경로 독립)
            Assert.Equal(1, r.HostIsolations);
            // 가격 괴리도 불일치(별도 리그)
            var p = MakeRig();
            Track(p.E, 1, 117_001, 6);
            p.Kis.Script.Enqueue((Page(RowJson(117_001, 6, 6, price: 70_100)), false));
            Assert.True(p.Q.Pump());
            Assert.Equal(OpenOrderVerdict.Divergence, p.Passes[^1].Verdict);
            Assert.False(p.KisOpen());
            // 이후 브로커가 다시 일치해도 래치 유지(이미 노출된 불일치의 증거)
            r.E.Feed.OnFill(new FillPayload { OrderId = KisOrderIdSpace.Encode(117_001), SymbolCode = Code, FillQty = 2, FillPrice = Limit, Side = 1,
                                              Status = BridgeFillStatusCodec.Encode(BridgeFillStatus.Filled, false) });   // 부분 체결 2주 → 호스트 잔량 4
            Assert.True(r.R.TryRearm(true, true));                                      // 대사기만 재무장 — 래치는 남음
            r.Kis.Script.Enqueue((Page(RowJson(117_001, 6, 4)), false));
            r.Advance(3); Assert.True(r.Q.Pump());
            Assert.Equal(OpenOrderVerdict.Confirmed, r.Passes[^1].Verdict);
            Assert.False(r.KisOpen());                                                  // ★래치 = 차단 유지
            Assert.True(r.M.Held);
            Assert.False(r.Q.TryReset(true, false));
            Assert.True(r.Q.TryReset(true, true));
            Assert.True(r.M.TryReset(true, true));
            Assert.True(r.KisOpen());                                                   // 이중키 + 최신 확정 = 개방
            Assert.Equal(1, r.HostIsolations);                                          // 호스트 격리 통지는 최초 1회
        }

        // ════════════ §5 [방향 B] 유령 비상 취소 ════════════

        [Fact]
        public void Ghost_RaisesBit22_EmergencyCancelsByOdnoAndOrgNo_CappedAndDeduped()
        {
            var r = MakeRig();
            var rows = new StringBuilder();
            for (int i = 0; i < 6; i++) { if (i > 0) rows.Append(','); rows.Append(RowJson(118_000 + i, 3, 3, org: i == 5 ? 0 : 91_300 + i)); }
            r.Kis.Script.Enqueue((Page(rows.ToString()), false));                        // 호스트가 모르는 KIS 주문 6건(1건 지점번호 미상)
            Assert.True(r.Q.Pump());
            Assert.Equal(OpenOrderVerdict.Ghost, r.Passes[^1].Verdict);
            Assert.True(r.Mask.IsRaised(ArmingInterlockSource.OpenOrderDivergence));
            Assert.False(r.KisOpen());
            Assert.Equal(4, r.Cancels.Count);                                           // 패스당 상한(큐잉 0)
            Assert.Equal((118_000L, 91_300L), r.Cancels[0]);
            Assert.Equal(4, r.Passes[^1].CancelsSent);
            Assert.Equal(1, r.HostIsolations);
            // 다음 패스: 같은 번호 5초 내 중복 억제 · 남은 행 중 지점번호 미상(118005)은 송신하지 않는다
            r.Kis.Script.Enqueue((Page(rows.ToString()), false));
            r.Advance(3); Assert.True(r.Q.Pump());
            Assert.Equal(5, r.Cancels.Count);                                           // 118004 만 추가
            Assert.Equal((118_004L, 91_304L), r.Cancels[4]);
            Assert.True(r.R.CancelFaults >= 1);                                          // 118005(지점번호 없음) = 송신 불가 계수
            Assert.DoesNotContain(r.Cancels, c => c.Odno == 118_005);
            // 취소 경로 미결속 = 차단만
            var n = MakeRig(canceller: false);
            n.Kis.Script.Enqueue((Page(RowJson(119_000, 1, 1)), false));
            Assert.True(n.Q.Pump());
            Assert.Equal(OpenOrderVerdict.Ghost, n.Passes[^1].Verdict);
            Assert.Equal(0, n.Passes[^1].CancelsSent);
            Assert.False(n.KisOpen());
        }

        // ════════════ §6 ★낡은 스냅샷 경합 ════════════

        [Fact]
        public void ReconcileRequestedMidQuery_StalePagesRejected_NoConfirm_NextPumpConfirmsFresh()
        {
            var r = MakeRig(withRecovery: true);
            Track(r.E, 1, 117_001, 6);
            int call = 0;
            r.Kis.OnCall = () => { if (++call == 2) r.R.RequestReconcile(OpenOrderVenueKind.Kis); };   // 2페이지 조회 중 재접속
            r.Kis.Script.Enqueue((Page("", fk: "F1", nk: "N1"), true));
            r.Kis.Script.Enqueue((Page(RowJson(117_001, 6, 6)), false));
            Assert.True(r.Q.Pump());
            Assert.Empty(r.Passes.FindAll(p => p.Verdict == OpenOrderVerdict.Confirmed));   // ★재접속 '전' 스냅샷으로 확정 금지
            Assert.False(r.R.IsConfirmed(OpenOrderVenueKind.Kis));
            Assert.True(r.R.InvalidatedBatches >= 1);
            Assert.False(r.KisOpen());
            r.Kis.OnCall = null;
            r.Kis.Script.Enqueue((Page(RowJson(117_001, 6, 6)), false));
            Assert.True(r.Q.Pump());                                                    // 요청 계수 변화 = 즉시 새 스냅샷
            Assert.Equal(OpenOrderVerdict.Confirmed, r.Passes[^1].Verdict);
            Assert.True(r.KisOpen());
        }

        // ════════════ §7 δ=0 · 결선 · 오중 빌드 ════════════

        [Fact]
        public void HotPaths_ZeroAlloc_PageEvaluate_Gate_PassRouting()
        {
            var r = MakeRig(withRecovery: true);
            Track(r.E, 1, 117_001, 6);
            byte[] page = Page(RowJson(117_001, 6, 6));
            void Pass() { r.R.TryBeginSnapshot(OpenOrderVenueKind.Kis); r.R.OnKisPage(page, last: true); }
            for (int i = 0; i < 32; i++) Pass();
            r.Passes.Clear();
            r.Passes.Capacity = 20_000;
            Assert.Equal(0, MinAllocated(() => { for (int i = 0; i < 2_000; i++) Pass(); }));   // 판독 + 1:1 대사 + 라우팅
            Assert.Equal(0, MinAllocated(() => { for (int i = 0; i < 10_000; i++) r.Q.RefreshGate(); }));
            var confirmed = new OpenOrderPassResult(OpenOrderVerdict.Confirmed, OpenOrderVenueKind.Kis, 1, 1, 1, 0, 0, 0, 0, 0);
            Assert.Equal(0, MinAllocated(() => { for (int i = 0; i < 10_000; i++) r.Q.OnPass(confirmed); }));
            Assert.Equal(0, MinAllocated(() => { for (int i = 0; i < 2_000; i++) r.KisOpen(); }));
            Assert.True(r.KisOpen());
        }

        private static string RepoRoot()
        {
            var d = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
            while (d is not null && !System.IO.File.Exists(System.IO.Path.Combine(d.FullName, "StockManager.sln"))) d = d.Parent;
            Assert.NotNull(d);
            return d!.FullName;
        }

        [Fact]
        public void Host_Wiring_QueryTimer_PassHook_Canceller_Gate_FiveBuilds()
        {
            string root = RepoRoot();
            string form = System.IO.File.ReadAllText(System.IO.Path.Combine(root, "StockManager", "Blazor", "BlazorHomeForm.cs"));
            Assert.Contains("Volatile.Read(ref kisPassHook)?.Invoke(r);", form);
            Assert.Contains("Volatile.Write(ref kisPassHook, kisOpenOrdersRef.OnPass);", form);
            Assert.Contains("openOrderRecon.AttachKisCanceller((odno, orgNo) => kisLiveRef.CancelOpenOrder(odno, orgNo));", form);
            Assert.Contains("kisLiveRef.QueryOpenOrdersJson(fk, nk, cont, out more)", form);
            Assert.Contains("try { kisOpenOrdersRef.Pump(); } catch { }", form);                // 전용 1초 타이머(HTTP 블로킹 — 50ms 평가 타이머 아님)
            Assert.Contains("try { kisOpenOrders?.RefreshGate(); } catch { }", form);
            Assert.Contains("else limitFeed.SetReconcileIsolated(1, true);", form);              // KIS 미설정 = 한도 0
            string live = System.IO.File.ReadAllText(System.IO.Path.Combine(root, "src", "02_Infrastructure", "Adapters", "Brokers", "LiveBrokers.cs"));
            Assert.Contains("_real ? \"TTTC8001R\" : \"VTTC8001R\"", live);
            Assert.Contains("&CCLD_DVSN=02", live);
            Assert.Contains("more = cont is \"F\" or \"M\";", live);
            string sln = System.IO.File.ReadAllText(System.IO.Path.Combine(root, "StockManager.sln"));
            foreach (var proj in new[] { @"StockManager\StockManager.csproj", @"StockManager.PrimesBench\StockManager.PrimesBench.csproj",
                                         @"StockManager.KiwoomProxy\StockManager.KiwoomProxy.csproj", @"tools\KiwoomComShim\KiwoomComShim.csproj" })
            {
                Assert.Contains(proj, sln);
                Assert.Contains("<TreatWarningsAsErrors>true</TreatWarningsAsErrors>", System.IO.File.ReadAllText(System.IO.Path.Combine(root, proj)));
            }
            Assert.True(PureShellFitnessSentinel.RatchetCeiling <= 40);
        }
    }
}
