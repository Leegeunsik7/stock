// ============================================================================
// SyntheticSpreadRadarTests — [v12.18.19-DEV] 멀티 브로커 합성 스프레드 레이더 회귀
//
//   §1 산출 정확도: 정상(합성 0 · 교차 음수) · 괴리(합성 bp) · 역전(교차 양수 · 모의 차익 상승 에지 1회) · 잔량 비대칭(피드별 · 괴리)
//   §2 괴리 래치: 30bp 499ms 미격발 / 500ms 격발 → 비트 22(실 집계 마스크) Raise · 임계 하회 = 타이머 리셋 · 래치 중 모의 차익 거부 ·
//      재무장 = 물리 이중키(한쪽 키 = 거부) · 실 대사기 AttachQuoteGuard(2차 입력 OR · 교체 금지 · 판독 예외 = fail-closed)
//   §3 이종 피드 방어: 수신 시각 차 > 1초 = 대조 생략(타이머 리셋) · venue 전문 시각 역전 = 폐기(최신 상태 보존) · 무효 호가 폐기 · 테이블 포화
//   §4 δ=0: 1M 틱 대조 + 펌프 표본 + 판독 힙 0B · GC 0 / 허브 펌프 투영 0B · 프레임 필드
//   §5 동시성: 두 피드 작성자 + 판독자 — 찢긴 래더 0(CountdownEvent 조인 후 처분)
//   §6 Fail-Closed: IL 참조 허용목록(브로커 · 디스패처 · 무장 형식 0) · 포트 표면에 발주/무장 멤버 0 · 헤드리스 화면(미수신 정직 렌더 · 래치 배너)
// ============================================================================
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MS2E.Core;
using MS2E.Infrastructure;
using MS2E.UI.Portable.Ports.Trading;
using StockManager.Adapters;
using StockManager.Blazor.Services;
using StockManager.Engines.Arbitrage;
using Xunit;
using Xunit.Abstractions;

namespace StockManager.Tests.Engines.Arbitrage
{
    [Collection("ConcurrencyStress")]
    public sealed class SyntheticSpreadRadarTests
    {
        private const long Sym = 5930;
        private const int K = ArbRadar.VenueKiwoom, S = ArbRadar.VenueKis;
        private readonly ITestOutputHelper _out;

        public SyntheticSpreadRadarTests(ITestOutputHelper output) => _out = output;

        private sealed class Clock { public long Micros = 1_000_000; public long Now() => Micros; }

        private static OrderBookPayload Book(long bid, long ask, long bidQty = 1_000, long askQty = 1_000, long tick = 100, long symbol = Sym, long ts = 0)
        {
            var b = new OrderBookPayload { SymbolCode = symbol, TimestampTicks = ts };
            for (int i = 0; i < 10; i++)
            {
                b.BidPrice[i] = bid - i * tick; b.AskPrice[i] = ask + i * tick;
                b.BidQty[i] = bidQty; b.AskQty[i] = askQty;
            }
            return b;
        }

        /// <summary>비트 22 를 실 집계 마스크에 결속한 대사기 대역(레이더 2차 입력만 OR).</summary>
        private sealed class Bit22Host : IQuoteGuardHost, IArmingBlockSource
        {
            private IArmingBlockSource? _guard;
            private ArmingInterlockLink? _link;
            public readonly ArmingInterlockBitmask Mask = new(ArmingInterlockSource.OpenOrderDivergence);
            public Bit22Host() => _link = Mask.Bind(ArmingInterlockSource.OpenOrderDivergence, this);
            public bool ArmingBlocked => _guard?.ArmingBlocked ?? false;
            public void AttachQuoteGuard(IArmingBlockSource guard) { _guard = guard; _link?.Resync(); }
            public void Resync() => _link?.Resync();
            public bool Raised => Mask.IsRaised(ArmingInterlockSource.OpenOrderDivergence);
        }

        // ════════════ §1 산출 정확도 ════════════

        [Fact]
        public void Normal_Divergent_And_Imbalance_Bp_Are_Exact()
        {
            var c = new Clock();
            var r = new SyntheticSpreadRadar(c.Now);
            Assert.False(r.OnBook(K, Book(70_000, 70_100)));                                   // 한 피드만 = 대조 대기
            Assert.Equal(ArbRadar.Waiting, r.Status.State);
            Assert.True(r.OnBook(S, Book(70_000, 70_100)));
            var st = r.Status;
            Assert.Equal(ArbRadar.Ok, st.State);
            Assert.Equal(0, st.SpreadCbp);
            Assert.Equal((70_000 - 70_100) * 4_000_000L / 280_200, st.CrossCbp);                // −14.27bp(정상 = 음수)
            Assert.Equal(-1_427, st.CrossCbp);

            Assert.True(r.OnBook(S, Book(70_250, 70_350, bidQty: 3_000, askQty: 1_000)));       // KIS Mid +250원
            st = r.Status;
            Assert.Equal(500L * 2_000_000 / 280_700, st.SpreadCbp);                             // 35.62bp
            Assert.Equal(3_562, st.SpreadCbp);
            Assert.Equal(ArbRadar.Diverging, st.State);
            Assert.Equal(0, st.ImbalanceKiwoomCbp);
            Assert.Equal(500_000, st.ImbalanceKisCbp);                                           // (3000−1000)/4000 = 50%
            Assert.Equal(500_000, st.ImbalanceDivergenceCbp);
            Assert.Equal("35.62bp", ArbRadar.Bp(st.SpreadCbp));
        }

        [Fact]
        public void Inverted_Cross_Records_One_Paper_Arb_Per_Rising_Edge()
        {
            var c = new Clock();
            var r = new SyntheticSpreadRadar(c.Now, maxPaperQty: 100);
            r.OnBook(S, Book(70_000, 70_100, askQty: 40));                                      // KIS 매도 1호가 40주
            r.OnBook(K, Book(70_200, 70_300, bidQty: 500));                                     // 키움 매수 70,200 > KIS 매도 70,100 = 역전
            var st = r.Status;
            Assert.Equal(ArbRadar.Inverted, st.State);
            Assert.Equal(100L * 4_000_000 / 280_600, st.CrossCbp);                              // +14.25bp
            Assert.Equal(1, st.Opportunities);
            Assert.Equal(40, st.PaperQty);                                                      // min(40, 500, 100)
            Assert.Equal(100 * 40, st.PaperGrossWon);                                           // (70,200 − 70,100) × 40

            for (int i = 0; i < 50; i++) r.OnBook(K, Book(70_200, 70_300, bidQty: 500));        // 같은 역전 지속 = 중복 0
            Assert.Equal(1, r.Status.Opportunities);
            r.OnBook(K, Book(70_000, 70_100));                                                  // 해소
            Assert.Equal(ArbRadar.Ok, r.Status.State);
            r.OnBook(S, Book(70_200, 70_300, askQty: 1_000));                                   // 반대 방향 역전(KIS 매수 > 키움 매도 70,100)
            st = r.Status;
            Assert.Equal(2, st.Opportunities);
            Assert.Equal(40 + 100, st.PaperQty);                                                // 상한 100
            Assert.Equal(4_000 + 100 * 100, st.PaperGrossWon);
        }

        // ════════════ §2 괴리 래치 ════════════

        [Fact]
        public void Divergence_Sustained_500ms_Latches_Bit22_And_Rearm_Needs_Both_Keys()
        {
            var c = new Clock();
            var host = new Bit22Host();
            var r = new SyntheticSpreadRadar(c.Now);
            r.BindDivergenceInterlock(host);
            Assert.False(host.Raised);
            r.OnBook(K, Book(70_000, 70_500));
            r.OnBook(S, Book(70_250, 70_750));                                                  // 35.52bp ≥ 30bp(교차 아님) — 타이머 개시
            c.Micros += 499_000; r.OnBook(S, Book(70_250, 70_750));
            Assert.False(r.Latched);
            c.Micros += 1_000; r.OnBook(S, Book(70_250, 70_750));                               // 500ms 지속
            Assert.True(r.Latched);
            Assert.True(host.Raised);
            Assert.True(host.Mask.Blocked);
            var st = r.Status;
            Assert.Equal(ArbRadar.Latched, st.State);
            Assert.Equal(Sym, st.LatchSymbol);
            Assert.Equal(3_552, st.LatchSpreadCbp);
            Assert.Equal(1, st.Trips);

            r.OnBook(K, Book(70_800, 70_900));                                                  // 래치 중 역전 = 모의 기록 거부
            Assert.Equal(0, r.Opportunities);
            Assert.Equal(1, r.PaperRefusedLatched);

            Assert.False(r.TryRearm(true, false));
            Assert.False(r.TryRearm(false, true));
            Assert.True(host.Raised);
            r.OnBook(K, Book(70_250, 70_750));                                                  // 정합 회복
            Assert.True(r.TryRearm(true, true));
            Assert.False(r.Latched);
            Assert.False(host.Raised);
            Assert.Equal(1, r.Trips);
        }

        [Fact]
        public void Dip_Below_Threshold_Resets_Sustain_Timer()
        {
            var c = new Clock();
            var r = new SyntheticSpreadRadar(c.Now);
            r.OnBook(K, Book(70_000, 70_100));
            r.OnBook(S, Book(70_250, 70_350));
            c.Micros += 400_000; r.OnBook(S, Book(70_000, 70_100));                             // 정합 → 리셋
            c.Micros += 1_000; r.OnBook(S, Book(70_250, 70_350));                               // 재개시
            c.Micros += 450_000; r.OnBook(S, Book(70_250, 70_350));
            Assert.False(r.Latched);                                                            // 누적 851ms 이지만 연속 450ms
            c.Micros += 50_000; r.OnBook(S, Book(70_250, 70_350));
            Assert.True(r.Latched);
        }

        [Fact]
        public void Real_Reconciler_Ors_Quote_Guard_Into_Bit22_FailClosed()
        {
            var exec = OpenOrderRig.MakeExec();
            var recon = new BrokerOpenOrderReconciler(exec.Feed, null, null, requireKiwoom: false, requireKis: false, capacity: 64);
            var mask = new ArmingInterlockBitmask(ArmingInterlockSource.OpenOrderDivergence);
            recon.BindInterlock(mask, ArmingInterlockSource.OpenOrderDivergence);
            Assert.False(recon.ArmingBlocked);
            Assert.False(mask.IsRaised(ArmingInterlockSource.OpenOrderDivergence));

            var c = new Clock();
            var r = new SyntheticSpreadRadar(c.Now, sustainMicros: 0);
            r.BindDivergenceInterlock(recon);
            Assert.Throws<InvalidOperationException>(() => recon.AttachQuoteGuard(new SyntheticSpreadRadar()));   // 교체 금지
            r.OnBook(K, Book(70_000, 70_100));
            r.OnBook(S, Book(70_250, 70_350));
            r.OnBook(S, Book(70_250, 70_350));
            Assert.True(recon.QuoteBlocked);
            Assert.True(recon.ArmingBlocked);
            Assert.True(mask.IsRaised(ArmingInterlockSource.OpenOrderDivergence));
            Assert.False(recon.GhostDetected);                                                  // 대사기 자체 판정과 분리(비상 취소 경로 무관)
            Assert.Equal(0, recon.CancelsSent);
            Assert.True(r.TryRearm(true, true));
            Assert.False(mask.IsRaised(ArmingInterlockSource.OpenOrderDivergence));

            var recon2 = new BrokerOpenOrderReconciler(OpenOrderRig.MakeExec().Feed, null, null, requireKiwoom: false, requireKis: false, capacity: 64);
            recon2.AttachQuoteGuard(new ThrowingSource());
            Assert.True(recon2.QuoteBlocked);                                                   // 판독 예외 = 차단
        }

        private sealed class ThrowingSource : IArmingBlockSource { public bool ArmingBlocked => throw new InvalidOperationException(); }

        // ════════════ §3 이종 피드 방어 ════════════

        [Fact]
        public void Stale_Feed_Skips_Comparison_And_Resets_Timer()
        {
            var c = new Clock();
            var r = new SyntheticSpreadRadar(c.Now);
            r.OnBook(K, Book(70_000, 70_100));
            r.OnBook(S, Book(70_250, 70_350));
            c.Micros += 1_000_001;
            Assert.False(r.OnBook(K, Book(70_000, 70_100)));                                    // KIS 1초 초과 노후 = 대조 생략
            Assert.Equal(1, r.StaleSkips);
            r.OnBook(S, Book(70_250, 70_350));                                                  // 재개시
            c.Micros += 499_000; r.OnBook(S, Book(70_250, 70_350));
            Assert.False(r.Latched);
        }

        [Fact]
        public void Timestamp_Inversion_Is_Dropped_And_Newest_State_Kept()
        {
            var c = new Clock();
            var r = new SyntheticSpreadRadar(c.Now);
            r.OnBook(K, Book(70_000, 70_100, ts: 2_000));
            r.OnBook(S, Book(70_000, 70_100, ts: 50));                                          // venue 별 독립 시각
            Assert.False(r.OnBook(K, Book(69_000, 69_100, ts: 1_999)));                         // 역전 = 폐기
            Assert.Equal(1, r.TimestampInversions);
            Span<long> bp = stackalloc long[10], bq = stackalloc long[10], ap = stackalloc long[10], aq = stackalloc long[10];
            Assert.True(r.CopyDepth(K, bp, bq, ap, aq));
            Assert.Equal(70_000, bp[0]);
            Assert.True(r.OnBook(K, Book(70_100, 70_200, ts: 2_000)));                          // 같은 시각 = 수용
            Assert.Equal(0, r.InvalidBooks);

            Assert.False(r.OnBook(K, Book(70_100, 70_100)));                                    // 잠긴 호가
            Assert.False(r.OnBook(K, Book(0, 70_100)));                                         // 결측
            Assert.False(r.OnBook(K, Book(70_000, 70_100, bidQty: -1)));                        // 음수 잔량
            Assert.False(r.OnBook(2, Book(70_000, 70_100)));                                    // venue 이탈
            Assert.Equal(4, r.InvalidBooks);

            var full = new SyntheticSpreadRadar(c.Now);
            for (int i = 1; i <= SyntheticSpreadRadar.Capacity; i++) full.OnBook(K, Book(70_000, 70_100, symbol: i));
            full.OnBook(K, Book(70_000, 70_100, symbol: 999_999));
            Assert.Equal(1, full.TableFull);
        }

        // ════════════ §4 δ=0 ════════════

        private static long MinAllocated(Action body, int windows = 3)
        {
            long min = long.MaxValue;
            for (int w = 0; w < windows; w++)
            {
                long before = GC.GetAllocatedBytesForCurrentThread();
                body();
                min = Math.Min(min, GC.GetAllocatedBytesForCurrentThread() - before);
            }
            return min;
        }

        [Fact]
        public void OneMillion_Cross_Venue_Ticks_Allocate_Zero_Bytes()
        {
            var c = new Clock();
            var host = new Bit22Host();
            var r = new SyntheticSpreadRadar(c.Now);
            r.BindDivergenceInterlock(host);
            var books = new OrderBookPayload[16];
            for (int i = 0; i < 8; i++)
            {
                books[i * 2] = Book(70_000 + i * 100, 70_100 + i * 100, symbol: 100 + i);
                books[i * 2 + 1] = Book(70_000 + i * 100 + (i % 3 == 0 ? 300 : 0), 70_100 + i * 100 + (i % 3 == 0 ? 300 : 0), symbol: 100 + i);   // 괴리 · 역전 섞음
            }
            long compared = 0;
            void Run(Span<long> a, Span<long> b2, Span<long> d, Span<long> e, Span<int> sp)
            {
                for (int i = 0; i < 1_000_000; i++)
                {
                    c.Micros += 7;
                    if (r.OnBook(i & 1, in books[i & 15])) compared++;
                    if ((i & 1023) == 0)
                    {
                        r.Sample(); _ = r.Status; r.CopyDepth(K, a, b2, d, e); r.CopySpark(sp);
                        if ((i & 262143) == 0) r.TryRearm(true, true);                           // 래치 ↔ 재무장 경로 포함
                    }
                }
            }
            long[] arrA = new long[10], arrB = new long[10], arrC = new long[10], arrD = new long[10];
            int[] arrS = new int[ArbRadar.SparkLength];
            Run(arrA, arrB, arrC, arrD, arrS);                                                  // 예열(JIT)
            int g0 = GC.CollectionCount(0);
            long alloc = MinAllocated(() => Run(arrA, arrB, arrC, arrD, arrS));
            int gcs = GC.CollectionCount(0) - g0;
            _out.WriteLine($"[δ] 1M 틱 × 3창 최소 = {alloc}B · Gen0 {gcs} · 대조 {compared:N0} · 래치 {r.Trips} · 모의 {r.Opportunities}");
            Assert.Equal(0, alloc);
            Assert.True(compared > 2_000_000);
            Assert.True(r.Trips > 0);
            Assert.True(r.Opportunities > 0);
            GC.KeepAlive(host);
        }

        [Fact]
        public void Hub_Pump_Projects_Arbitrage_Frame_With_Zero_Alloc()
        {
            var c = new Clock();
            var r = new SyntheticSpreadRadar(c.Now);
            long ticks = 1;
            var hub = new DashboardSnapshotHub(nowTicks: () => ++ticks);
            Assert.True(hub.Pump());
            var f0 = hub.Acquire();
            Assert.NotNull(f0);
            Assert.Equal(ArbRadar.Unbound, f0!.ArbState);
            hub.Release(f0);
            hub.AttachArbitrage(r);
            r.OnBook(K, Book(70_000, 70_100));
            r.OnBook(S, Book(70_250, 70_350));
            ticks += TimeSpan.TicksPerSecond;
            Assert.True(hub.Pump());
            var f = hub.Acquire()!;
            Assert.Equal(ArbRadar.Diverging, f.ArbState);
            Assert.Equal(3_562, f.ArbSpreadCbp);
            hub.Release(f);
            Assert.Equal(1, r.Status.Samples);
            long pump = MinAllocated(() => { for (int i = 0; i < 200; i++) { ticks += TimeSpan.TicksPerSecond; r.OnBook(i & 1, Book(70_000 + (i & 3) * 100, 70_100 + (i & 3) * 100)); hub.Pump(); } });
            _out.WriteLine($"[δ] 허브 펌프 200 = {pump}B");
            Assert.Equal(0, pump);
            Span<int> sp = stackalloc int[ArbRadar.SparkLength];
            Assert.Equal(ArbRadar.SparkLength, r.CopySpark(sp));
        }

        // ════════════ §5 동시성 ════════════

        [Fact]
        public void Two_Feed_Writers_And_Reader_Never_See_Torn_Ladder()
        {
            var r = new SyntheticSpreadRadar();
            const int N = 200_000;
            int torn = 0;
            using var done = new CountdownEvent(3);
            using var stop = new ManualResetEventSlim(false);
            void Writer(int venue)
            {
                try
                {
                    for (int i = 0; i < N; i++)
                    {
                        long bid = 50_000 + (i % 97) * 10 + venue * 5;
                        var b = Book(bid, bid + 10, bidQty: bid, askQty: bid + 10, tick: 10);   // 행 내부 정합(잔량 = 가격) = 찢김 판별
                        r.OnBook(venue, in b);
                    }
                }
                finally { done.Signal(); }
            }
            var reader = new Thread(() =>
            {
                long[] bp = new long[10], bq = new long[10], ap = new long[10], aq = new long[10];
                try
                {
                    while (!stop.IsSet)
                        for (int v = 0; v < 2; v++)
                            if (r.CopyDepth(v, bp, bq, ap, aq))
                                for (int i = 0; i < 10; i++)
                                    if (bq[i] != bp[0] || aq[i] != ap[0] || ap[0] - bp[0] != 10 || bp[i] != bp[0] - i * 10 || ap[i] != ap[0] + i * 10) { Interlocked.Increment(ref torn); break; }
                }
                finally { done.Signal(); }
            });
            var w0 = new Thread(() => Writer(K)); var w1 = new Thread(() => Writer(S));
            reader.Start(); w0.Start(); w1.Start();
            w0.Join(); w1.Join();
            stop.Set();
            Assert.True(done.Wait(TimeSpan.FromSeconds(30)));
            Assert.Equal(0, Volatile.Read(ref torn));
            Assert.Equal(2L * N, r.Books);
            Assert.Equal(0, r.InvalidBooks);
        }

        // ════════════ §6 Fail-Closed ════════════

        private static bool Allowed(Type t)
        {
            while (t.IsArray || t.IsByRef || t.IsPointer) t = t.GetElementType()!;
            if (t.IsGenericType && !t.IsGenericTypeDefinition) { if (!t.GetGenericArguments().All(Allowed)) return false; t = t.GetGenericTypeDefinition(); }
            if (t.IsGenericParameter || t.Name.StartsWith("<PrivateImplementationDetails>", StringComparison.Ordinal)) return true;
            if (t.DeclaringType is { } d) return Allowed(d.IsGenericType ? d.GetGenericTypeDefinition() : d);
            if (t == typeof(OrderBookPayload) || t == typeof(OrderBookLevels10) || t == typeof(IArmingBlockSource)) return true;
            string ns = t.Namespace ?? "";
            return ns == "System" || ns.StartsWith("System.Runtime", StringComparison.Ordinal) || ns.StartsWith("System.Numerics", StringComparison.Ordinal)
                || (ns == "System.Threading" && !t.Name.Contains("Thread", StringComparison.Ordinal)) || ns == "System.Diagnostics" && t == typeof(System.Diagnostics.Stopwatch)
                || ns == "MS2E.UI.Portable.Ports.Trading" || ns == "StockManager.Engines.Arbitrage";
        }

        [Fact]
        public void Radar_Il_References_Only_Allowlisted_Types_And_Port_Has_No_Order_Surface()
        {
            const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;
            var bad = new List<string>();
            var types = typeof(SyntheticSpreadRadar).GetNestedTypes(All).Prepend(typeof(SyntheticSpreadRadar)).Append(typeof(IQuoteGuardHost));
            foreach (var type in types)
            {
                foreach (var f in type.GetFields(All)) if (!Allowed(f.FieldType)) bad.Add(type.Name + "." + f.Name + " : " + f.FieldType.FullName);
                foreach (var m in type.GetMethods(All).Cast<MethodBase>().Concat(type.GetConstructors(All)))
                    foreach (var rm in ZeroAllocIlInspector.ReferencedMembers(m))
                    {
                        var owner = rm as Type ?? rm.DeclaringType;
                        if (owner is not null && !Allowed(owner)) bad.Add(type.Name + "." + m.Name + " → " + owner.FullName + "::" + rm.Name);
                    }
            }
            Assert.True(bad.Count == 0, string.Join("\n", bad));
            foreach (var n in typeof(IArbitrageRadarPort).GetMembers().Select(m => m.Name).Concat(typeof(IQuoteGuardHost).GetMembers().Select(m => m.Name)))
                foreach (var banned in new[] { "Submit", "Place", "Order", "Cancel", "Arm", "Broker", "Live", "Send" })
                    Assert.DoesNotContain(banned, n, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Headless_View_Renders_Missing_Feed_Honestly_Then_Latch_Banner()
        {
            var c = new Clock();
            var r = new SyntheticSpreadRadar(c.Now, sustainMicros: 0);
            using var ctx = new Bunit.TestContext();
            ctx.Services.AddSingleton<IArbitrageRadarPort>(r);
            var cut = ctx.RenderComponent<MS2E.UI.Portable.Pages.Trading.ArbitrageRadarView>();
            Assert.Contains(ArbRadar.Label(ArbRadar.Waiting), cut.Markup);
            Assert.Contains("미수신", cut.Markup);
            Assert.Empty(cut.FindAll(".ab-latch"));

            r.OnBook(K, Book(70_000, 70_100));
            r.OnBook(S, Book(70_250, 70_350));
            r.OnBook(S, Book(70_250, 70_350));
            cut.Render();
            Assert.DoesNotContain("미수신", cut.Markup);
            Assert.Single(cut.FindAll(".ab-latch"));
            Assert.Contains("35.62bp", cut.Markup);
            Assert.Equal(20, cut.FindAll(".ab-ladder tbody tr").Count);
            Assert.NotEmpty(cut.FindAll(".ab-ladder td.px.diff"));                               // 피드 간 가격 불일치 강조
            Assert.Empty(cut.FindAll("button.buy, button.sell"));                               // 발주 수단 0

            cut.Find(".ab-sym").Change("660");
            cut.FindAll(".ab-btn")[0].Click();
            Assert.Equal(660, r.Focus);
            cut.FindAll(".ab-btn")[1].Click();
            Assert.Equal(Sym, r.Focus);

            string nav = System.IO.File.ReadAllText(System.IO.Path.Combine(RepoRoot(), "MS2E.UI.Portable", "Components", "NavMenu.razor"));
            Assert.Contains("new Item(\"⚡\", \"아비트라지 레이더\", \"/arbitrage\"", nav);
        }

        private static string RepoRoot()
        {
            var d = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
            while (d is not null && !System.IO.File.Exists(System.IO.Path.Combine(d.FullName, "StockManager.sln"))) d = d.Parent;
            return d?.FullName ?? throw new InvalidOperationException("repo root");
        }
    }
}
