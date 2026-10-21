// ============================================================================
// BrokerOpenOrderReconcilerTests — [v12.18.7-DEV · 무장 가드] 유령 비상 취소(키움 CancelByVenueNo · KIS TTTC0803U)는 무장일 때만 송신
//
//  결함(실측 · HANDOVER §64.1 ③): CancelRowsLocked 가 무장 술어를 보지 않아 liveArmed:false 에서도 브로커 미체결(HTS 수동 주문 포함)에
//   실 취소를 송신했다. 교정 = AttachCancelArming(호스트 liveArmed) · 미결속/거짓/예외 = 송신 0 + CancelsSuppressed 계수 · 비트 22 는 무장 무관.
//  §1 무장 해제(KIS · 키움) — 유령 6행 → 취소 송신 0 · 비트 22 Raise · 한도/격리 그대로 · 억제 수 = 잔량 행 수 · 반복 패스도 0.
//  §2 미결속 · 예외 = 송신 0(fail-closed).
//  §3 무장 — 송신은 무장일 때만(패스당 상한 4 · Disarm 선반영). ★호스트형 술어(!집계 마스크 Blocked): 자기 비트 22 가 술어를 거짓으로
//     만들기 전에 표본 → 그 패스는 송신 · 다음 패스(비트 22 sticky)는 억제. 송신 직전 재판정이었다면 무장 상태에서도 송신 0(교정 핀).
//  §4 δ=0 — 무장 해제 유령 패스(판정 · 억제 계수) · 무장 판정 표본 0B.
//  §5 결선 — 호스트 AttachCancelArming(liveArmed) · 드라이런 술어(armedPredicate) 아님 · 로그에 억제 수 · 인터락 정의 불변(14/22/25).
// ============================================================================
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using MS2E.Core;
using MS2E.Infrastructure;
using StockManager.Adapters;
using Xunit;
using Xunit.Abstractions;
using static StockManager.Tests.ZeroAllocProbe;

namespace StockManager.Tests.Adapters
{
    [Collection("ConcurrencyStress")]
    public sealed class BrokerOpenOrderReconcilerTests
    {
        private const long Code = 5930, Limit = 70_000;
        private static readonly long Sec = TimeSpan.TicksPerSecond;
        private readonly ITestOutputHelper _out;
        public BrokerOpenOrderReconcilerTests(ITestOutputHelper o) => _out = o;

        private static string RepoRoot()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d is not null && !File.Exists(Path.Combine(d.FullName, "StockManager.sln"))) d = d.Parent;
            Assert.NotNull(d);
            return d!.FullName;
        }

        // ── KIS(TTTC8001R 페이지 → 대사 → TTTC0803U 취소 위임) ──
        private static string RowJson(long odno, long rmn, long org)
            => "{\"odno\":\"" + odno.ToString("D10") + "\",\"orgn_odno\":\"\",\"pdno\":\"005930\",\"ord_qty\":\"" + rmn + "\",\"rmn_qty\":\"" + rmn
               + "\",\"ord_unpr\":\"" + Limit + "\",\"sll_buy_dvsn_cd\":\"02\",\"cncl_yn\":\"N\",\"ord_gno_brno\":\"" + org.ToString("D5") + "\"}";

        private static byte[] Page(int n, long baseOdno = 118_000)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < n; i++) { if (i > 0) sb.Append(','); sb.Append(RowJson(baseOdno + i, 3, 91_300 + i)); }
            return Encoding.UTF8.GetBytes("{\"ctx_area_fk100\":\"\",\"ctx_area_nk100\":\"\",\"output1\":[" + sb + "],\"rt_cd\":\"0\",\"msg1\":\"ok\"}");
        }

        private sealed class KisRig
        {
            public long Now = 10_000 * TimeSpan.TicksPerSecond;
            public BrokerOpenOrderReconciler R = null!;
            public ArmingInterlockBitmask Mask = null!;
            public KisOpenOrderQuery Q = null!;
            public readonly Queue<byte[]> Script = new();
            public readonly List<OpenOrderPassResult> Passes = new();
            public int Sent;                                                     // ★'실 송신' 대역 — 이 위임이 호출되면 TTTC0803U 전문이 나간 것
            public void Advance(double s) => Now += (long)(s * Sec);
            public OpenOrderPassResult Last => Passes[^1];
        }

        private static KisRig MakeKis(Func<KisRig, Func<bool>>? arming)
        {
            var r = new KisRig();
            var e = OpenOrderRig.MakeExec();
            KisOpenOrderQuery? q = null;
            r.R = new BrokerOpenOrderReconciler(e.Feed, null, null, requireKiwoom: false, requireKis: false, capacity: 64,
                                                nowTicks: () => Volatile.Read(ref r.Now), onPass: p => { r.Passes.Add(p); q?.OnPass(p); });
            r.Mask = new ArmingInterlockBitmask(ArmingInterlockSource.OpenOrderDivergence);
            r.R.BindInterlock(r.Mask, ArmingInterlockSource.OpenOrderDivergence);
            r.R.AttachKisCanceller((odno, org) => { r.Sent++; return true; });
            if (arming is not null) r.R.AttachCancelArming(arming(r));
            q = r.Q = new KisOpenOrderQuery(r.R, (string fk, string nk, bool cont, out bool more) => { more = false; return r.Script.Count > 0 ? r.Script.Dequeue() : null; },
                                            nowTicks: () => r.Now);
            return r;
        }

        private static void KisPass(KisRig r, int ghosts)
        {
            r.Script.Enqueue(Page(ghosts));
            r.Advance(31);
            Assert.True(r.Q.Pump());
        }

        // ── 키움(OPT10075 행 → 대사 → CancelByVenueNo) ──
        private sealed class FakeVenue : IOpenOrderVenue
        {
            public int Cancels, CancelsWhileBlocked;
            public Func<bool>? BlockedProbe;
            public int VenueKind => OpenOrderVenueKind.Kiwoom;
            public bool RequestOpenOrders() => true;
            public bool AdoptMapping(long clientId, long venueOrderNo, long symbolCode, int side, long remaining, long priceWon) => true;
            public bool CancelByVenueNo(long venueOrderNo, long symbolCode, int side, long qty)
            {
                if (BlockedProbe?.Invoke() == true) CancelsWhileBlocked++;
                Cancels++;
                return true;
            }
        }

        private sealed class KiwoomRig
        {
            public long Ticks = 1_000_000 * TimeSpan.TicksPerSecond;
            public BrokerOpenOrderReconciler R = null!;
            public ArmingInterlockBitmask Mask = null!;
            public readonly FakeVenue V = new();
            public readonly List<OpenOrderPassResult> Passes = new();
            public long Batch;
            public OpenOrderPassResult Last => Passes[^1];
        }

        private static KiwoomRig MakeKiwoom(Func<KiwoomRig, Func<bool>>? arming)
        {
            var r = new KiwoomRig();
            var e = OpenOrderRig.MakeExec();
            r.R = new BrokerOpenOrderReconciler(e.Feed, r.V, null, requireKiwoom: true, requireKis: false, capacity: 64,
                                                nowTicks: () => Volatile.Read(ref r.Ticks), onPass: r.Passes.Add);
            r.Mask = new ArmingInterlockBitmask(ArmingInterlockSource.OpenOrderDivergence);
            r.R.BindInterlock(r.Mask, ArmingInterlockSource.OpenOrderDivergence);
            if (arming is not null) r.R.AttachCancelArming(arming(r));
            return r;
        }

        private static void KiwoomPass(KiwoomRig r, int ghosts)
        {
            r.Ticks += 31 * Sec;
            Assert.True(r.R.Pump());
            long batch = ++r.Batch;
            for (int i = 0; i < ghosts; i++)
                r.R.OnKiwoomRow(new OpenOrderPayload { BatchId = batch, RowIndex = i, RowCount = ghosts, VenueOrderNo = 900 + i + batch * 100, SymbolCode = Code,
                                                       OrderQty = 1 + i, RemainingQty = 1 + i, OrderPriceWon = Limit, Side = 1 });
        }

        // ════════════ §1 무장 해제 ════════════

        [Fact]
        public void Disarmed_KisGhosts_ZeroCancelSent_Bit22Raised_SuppressedCounted()
        {
            var r = MakeKis(_ => () => false);
            KisPass(r, 6);
            Assert.Equal(OpenOrderVerdict.Ghost, r.Last.Verdict);
            Assert.Equal(0, r.Sent);                                                              // ★TTTC0803U 전문 0
            Assert.Equal((0, 6), (r.Last.CancelsSent, r.Last.CancelsSuppressed));
            Assert.True(r.Mask.IsRaised(ArmingInterlockSource.OpenOrderDivergence));              // ★비트 22 = 무장 무관 즉시
            Assert.True(r.R.GhostDetected && r.R.ArmingBlocked);
            for (int k = 0; k < 5; k++) KisPass(r, 6);                                            // 반복 패스(고속 재조회)도 0
            Assert.Equal(0, r.Sent);
            Assert.Equal(0, r.R.CancelsSent);
            Assert.Equal(36, r.R.CancelsSuppressed);
            Assert.Equal(6, r.R.SuppressedPasses);
            _out.WriteLine($"[§1] KIS 무장 해제 유령 6행 × 6패스 → 송신 {r.Sent} · 억제 {r.R.CancelsSuppressed} · 비트 22 {r.Mask.IsRaised(ArmingInterlockSource.OpenOrderDivergence)}");
        }

        [Fact]
        public void Disarmed_KiwoomGhosts_ZeroCancelSent_Bit22Raised()
        {
            var r = MakeKiwoom(_ => () => false);
            KiwoomPass(r, 5);
            Assert.Equal(OpenOrderVerdict.Ghost, r.Last.Verdict);
            Assert.Equal(0, r.V.Cancels);                                                          // 키움 정정취소 전문 0
            Assert.Equal((0, 5), (r.Last.CancelsSent, r.Last.CancelsSuppressed));
            Assert.True(r.Mask.IsRaised(ArmingInterlockSource.OpenOrderDivergence));
        }

        // ════════════ §2 미결속 · 예외 ════════════

        [Fact]
        public void UnboundOrThrowingArming_FailClosed_ZeroCancelSent()
        {
            var unbound = MakeKis(null);
            KisPass(unbound, 3);
            Assert.Equal((0, 3), (unbound.Sent, unbound.Last.CancelsSuppressed));
            Assert.True(unbound.Mask.IsRaised(ArmingInterlockSource.OpenOrderDivergence));

            var throwing = MakeKis(_ => () => throw new InvalidOperationException("술어 결함"));
            KisPass(throwing, 3);
            Assert.Equal((0, 3), (throwing.Sent, throwing.Last.CancelsSuppressed));
            Assert.Equal(OpenOrderVerdict.Ghost, throwing.Last.Verdict);

            var kw = MakeKiwoom(null);
            KiwoomPass(kw, 2);
            Assert.Equal(0, kw.V.Cancels);
            Assert.Throws<ArgumentNullException>(() => kw.R.AttachCancelArming(null!));
        }

        // ════════════ §3 무장 ════════════

        [Fact]
        public void Armed_CancelsOnlyWhenArmed_CappedFour_DisarmFirst()
        {
            bool armed = false;
            var r = MakeKis(_ => () => Volatile.Read(ref armed));
            KisPass(r, 6);
            Assert.Equal(0, r.Sent);                                                               // 해제 = 0
            Volatile.Write(ref armed, true);
            r.Advance(6);                                                                          // 5초 중복 억제 창 밖
            KisPass(r, 6);
            Assert.Equal(4, r.Sent);                                                               // 무장 = 패스당 상한 4
            Assert.Equal((4, 0), (r.Last.CancelsSent, r.Last.CancelsSuppressed));

            var kw = MakeKiwoom(_ => () => true);
            kw.V.BlockedProbe = () => kw.Mask.IsRaised(ArmingInterlockSource.OpenOrderDivergence);
            KiwoomPass(kw, 6);
            Assert.Equal(4, kw.V.Cancels);
            Assert.Equal(4, kw.V.CancelsWhileBlocked);                                             // ★Disarm(비트 22)이 모든 송신보다 먼저
        }

        [Fact]
        public void HostShapedPredicate_SampledBeforeOwnDisarm_ThenSticky()
        {
            // 호스트 liveArmed 와 같은 형태: 집계 마스크에 어떤 비트라도 서면 거짓(이 대사기의 비트 22 포함)
            var r = MakeKis(x => () => !x.Mask.Blocked);
            Assert.True(r.Mask.Seal(out long missing) && missing == 0);                             // 호스트처럼 결속 완료 봉인 = 평시 무장 가능
            Assert.False(r.Mask.Blocked);
            KisPass(r, 6);
            Assert.Equal(4, r.Sent);                                                               // ★자기 Disarm '이전' 표본 → 그 패스는 송신
            Assert.True(r.Mask.Blocked);
            r.Advance(6);
            KisPass(r, 6);
            Assert.Equal(4, r.Sent);                                                               // 다음 패스 = 비트 22 sticky → 무장 거짓 → 억제
            Assert.Equal(6, r.Last.CancelsSuppressed);
            _out.WriteLine($"[§3] 호스트형 술어: 1패스 송신 4 · 2패스 억제 {r.Last.CancelsSuppressed}(비트 22 sticky)");

            // 다른 방어선이 이미 무장을 거두고 있으면(실전 무장 아님) 첫 유령 패스부터 0
            bool otherDefense = true;
            var pre = MakeKis(x => () => !x.Mask.Blocked && !Volatile.Read(ref otherDefense));
            Assert.True(pre.Mask.Seal(out _));
            KisPass(pre, 6);
            Assert.Equal((0, 6), (pre.Sent, pre.Last.CancelsSuppressed));
            Assert.True(pre.Mask.IsRaised(ArmingInterlockSource.OpenOrderDivergence));
        }

        // ════════════ §4 δ=0 ════════════

        [Fact]
        public void ZeroAlloc_DisarmedGhostPass_KiwoomRows()
        {
            var r = MakeKiwoom(_ => () => false);
            for (int i = 0; i < 32; i++) KiwoomPass(r, 6);                                         // 예열(JIT)
            Assert.Equal(0, r.V.Cancels);
            r.Passes.Capacity = r.Passes.Count + 4_096;
            Assert.Equal(0, MinAllocated(() => { for (int i = 0; i < 500; i++) KiwoomPass(r, 6); }));   // 판정 · 억제 계수 · 비트 Resync
            Assert.Equal(0, r.V.Cancels);
            Assert.True(r.R.CancelsSuppressed >= 6 * 532);
        }

        // ════════════ §5 결선 · 불변 ════════════

        [Fact]
        public void HostWiring_LiveArmedNotDryRun_LogCarriesSuppressed_InterlockDefinitionsUnchanged()
        {
            string root = RepoRoot();
            string form = File.ReadAllText(Path.Combine(root, "StockManager", "Blazor", "BlazorHomeForm.cs"));
            Assert.Contains("openOrderRecon.AttachCancelArming(liveArmed);", form);
            Assert.DoesNotContain("openOrderRecon.AttachCancelArming(armedPredicate)", form);   // 드라이런 스코프 = 실 송신 허가 아님
            Assert.True(form.IndexOf("Func<bool> liveArmed = DispatchArming.ResolvePredicate(", StringComparison.Ordinal)
                        < form.IndexOf("openOrderRecon.AttachCancelArming(liveArmed);", StringComparison.Ordinal));
            Assert.Contains("무장 해제로 억제 {r.CancelsSuppressed}건", form);

            string src = File.ReadAllText(Path.Combine(root, "StockManager", "Adapters", "BrokerOpenOrderReconciler.cs"));
            int ghost = src.IndexOf("case OpenOrderVerdict.Ghost:", StringComparison.Ordinal);
            int sample = src.IndexOf("bool armed = CancelArmedNow();", ghost, StringComparison.Ordinal);
            int resync = src.IndexOf("Volatile.Read(ref _link)?.Resync();", ghost, StringComparison.Ordinal);
            int send = src.IndexOf("if (armed) cancels = CancelRowsLocked(", ghost, StringComparison.Ordinal);
            Assert.True(ghost > 0 && sample > ghost && resync > sample && send > resync, "표본 → Disarm → (무장 시) 송신 순서");

            Assert.Equal(14, (int)ArmingInterlockSource.AccountDivergence);
            Assert.Equal(22, (int)ArmingInterlockSource.OpenOrderDivergence);
            Assert.Equal(25, (int)ArmingInterlockSource.KiwoomBridgeLink);
            Assert.Equal(30, Enum.GetValues<ArmingInterlockSource>().Length);
        }
    }
}
