// ============================================================================
// KrxArenaFeedAndPageTests — v12.18.60-DEV: KRX 아레나 시세 주입기(KrxArenaQuoteFeeder) · 허브 펌프 결선 · DualArenaPage(순수 RCL)
//
//  §1 브리지 주입 — 최우선 호가 → 엔진 OnQuote · 누적 거래량 차분(급증 시그널 → AI 진입 근거 SurgeBp) · 감소(일 리셋) = 0 · 미갱신 슬롯 재주입 0 ·
//     호가 전 체결 = 거래량 이월 · 무효 코드/표 포화 폐기 · 갱신 0 = Drain(휴먼 큐 처리)
//  §2 다중 생산자 — 4 생산자 스레드 + 펌프 스레드: 엔진 호가 사본에서 찢어진 슬롯(ask ≠ bid + 100 등) 0
//  §3 합성 데모 — 가상 세션(평일 09:00 · ×20 · 15:30 → 다음 평일) · AI 근거 생성 · AI 체결 == 근거 · 출처 전환 = 원장 초기화 · 복귀 시 묵은 슬롯 재주입 0
//  §4 δ=0 — 브리지 OnBook/OnTrade/Pump 10만 회 · 합성 Pump 10만 회 힙 할당 0B
//  §5 허브 — DashboardSnapshotHub.Pump 가 주입기를 구동(정산 = 허브 펌프 단일 작성자)
//  §6 UI — DualArenaPage: 펌프 미결선 / 시세 미수신 정직 렌더 · 합성 대결 스코어보드 · 근거 카드(요약 · 확신도) · 출처 탭 · 휴먼 티켓 ·
//     Pure Shell(페이지 주입 = IKrxArenaPort 1개 · 호스트 셸 [Inject] 0) · DI/허브 결선 · 내비 항목
// ============================================================================
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MS2E.Infrastructure;
using MS2E.UI.Portable.Pages.Trading;
using MS2E.UI.Portable.Ports.Trading;
using StockManager.Adapters;
using StockManager.Blazor.Services;
using StockManager.Engines.Arena.Common;
using StockManager.Engines.Arena.Domestic;
using Xunit;
using Xunit.Abstractions;
using static StockManager.Tests.ZeroAllocProbe;

namespace StockManager.Tests.Engines.Arena
{
    [Collection("ConcurrencyStress")]
    public sealed class KrxArenaFeedAndPageTests
    {
        private const long Samsung = 5930, Hynix = 660;
        // 2026-10-22(목) 10:00 KST = 01:00 UTC
        private static readonly long Utc10Kst = new DateTime(2026, 10, 22, 1, 0, 0, DateTimeKind.Utc).Ticks;
        private readonly ITestOutputHelper _out;
        private long _utc = Utc10Kst;

        public KrxArenaFeedAndPageTests(ITestOutputHelper o) => _out = o;

        private static OrderBookPayload Book(long symbol, long bid, long ask, long bidQty = 1000, long askQty = 1000)
        {
            var b = new OrderBookPayload { SymbolCode = symbol };
            b.BidPrice[0] = bid; b.AskPrice[0] = ask; b.BidQty[0] = bidQty; b.AskQty[0] = askQty;
            return b;
        }

        private void Pump(KrxArenaQuoteFeeder f) { _utc += 100 * TimeSpan.TicksPerMillisecond; f.Pump(_utc); }

        private static DuelSnapshot Snap(KrxArenaEngine e) { Assert.True(e.TryReadSnapshot(out var s)); return s; }

        private static AiTradeRationale[] Rationales(KrxArenaEngine e)
        {
            var buf = new AiTradeRationale[KrxArenaEngine.RationaleCapacity];
            return buf.AsSpan(0, e.CopyRationales(buf)).ToArray();
        }

        // ── §1 브리지 주입 ───────────────────────────────────────────────────────

        [Fact]
        public void Bridge_Quotes_Feed_Engine_And_Cumulative_Volume_Surge_Drives_Ai_Rationale()
        {
            var e = new KrxArenaEngine();
            var f = new KrxArenaQuoteFeeder(e);
            long cum = 50_000;
            for (int i = 0; i < KrxArenaEngine.WarmupTicks + 1; i++)
            {
                f.OnTrade(Samsung, cum += 100);
                f.OnBook(Book(Samsung, 70_000, 70_100));
                Pump(f);
            }
            Assert.Equal(KrxArenaEngine.WarmupTicks + 1, Snap(e).Ticks);
            Assert.Empty(Rationales(e));

            e.SetOvernightMacro(3000);
            f.OnTrade(Samsung, cum += 1000);                 // 틱 거래량 = 차분 1000(EWMA 100 의 10배)
            f.OnBook(Book(Samsung, 70_100, 70_200, 9000, 1000));
            Pump(f);
            var r = Assert.Single(Rationales(e));
            Assert.True(r.IsWellFormed);
            Assert.Equal(10000, r.SurgeBp);
            Assert.Equal(8000, r.ImbalanceBp);
            Assert.Equal(70_200, r.PriceWon);
            Assert.Equal(new DateTime(2026, 10, 22, 10, 0, 0).Date, r.AtKst.Date);
            Assert.Equal(10, r.AtKst.Hour);

            // 누적 감소(일 리셋) = 0 거래량 · 예외 0
            f.OnTrade(Samsung, 10);
            f.OnBook(Book(Samsung, 70_100, 70_200));
            Pump(f);
            Assert.Equal(KrxArenaEngine.WarmupTicks + 3, Snap(e).Ticks);
        }

        [Fact]
        public void Unchanged_Slots_Are_Not_Refed_And_Empty_Pump_Drains_Human_Queue()
        {
            var e = new KrxArenaEngine();
            var f = new KrxArenaQuoteFeeder(e);
            Pump(f);
            Assert.Equal(KrxArenaReject.None, e.SubmitHuman(Samsung, 1, 1));
            Pump(f);                                           // 슬롯 0 → Drain → 미수신 거부
            Assert.Equal(1, Snap(e).Human.Rejects);

            f.OnBook(Book(Samsung, 70_000, 70_100));
            Pump(f); Pump(f); Pump(f);
            Assert.Equal(1, Snap(e).Ticks);
            Assert.Equal(KrxArenaReject.None, e.SubmitHuman(Samsung, 1, 10));
            Pump(f);                                           // 갱신 0 이어도 휴먼 큐는 체결
            var s = Snap(e);
            Assert.Equal(1, s.Human.Fills);
            Assert.Equal(1, s.Ticks);

            f.OnTrade(Hynix, 1_000);                           // 체결만 먼저 도착 → 엔진 미주입(호가 없음)
            Pump(f);
            Assert.Equal(1, Snap(e).Ticks);
            Assert.Equal(0, Snap(e).DroppedQuotes);
            f.OnBook(Book(Hynix, 180_000, 180_100));
            Pump(f);
            Assert.Equal(2, Snap(e).Ticks);
        }

        [Fact]
        public void Invalid_And_Overflow_Symbols_Are_Dropped()
        {
            var e = new KrxArenaEngine();
            var f = new KrxArenaQuoteFeeder(e);
            f.OnBook(Book(0, 70_000, 70_100));
            f.OnBook(Book(-5, 70_000, 70_100));
            f.OnTrade(Samsung, -1);
            Assert.Equal(2, f.DroppedBooks);
            for (int i = 1; i <= KrxArenaQuoteFeeder.MaxSlots + 3; i++) f.OnBook(Book(100_000 + i, 10_000, 10_010));
            Assert.Equal(5, f.DroppedBooks);
            Assert.Equal(KrxArenaQuoteFeeder.MaxSlots, f.BridgeUpdates);
            Assert.False(f.SelectSource(0));
            Assert.False(f.SelectSource(3));
            Pump(f);
            Assert.Equal(KrxArenaEngine.MaxSymbols, Snap(e).Ticks);
        }

        // ── §2 다중 생산자 ────────────────────────────────────────────────────────

        [Fact]
        public void Multi_Producer_Slots_Never_Tear_Under_Concurrent_Pump()
        {
            var e = new KrxArenaEngine();
            var f = new KrxArenaQuoteFeeder(e);
            const int Producers = 4, PerProducer = 40_000, Symbols = 8;
            int done = 0; long torn = 0, pumps = 0;
            var producers = Enumerable.Range(0, Producers).Select(k => new Thread(() =>
            {
                var rnd = new Random(k + 1);
                for (int i = 0; i < PerProducer; i++)
                {
                    long sym = 1 + rnd.Next(Symbols);
                    long bid = 10_000 + rnd.Next(0, 5000) * 10;
                    f.OnBook(Book(sym, bid, bid + 100, bid / 10, bid / 10 + 1));
                    if ((i & 7) == 0) f.OnTrade(sym, i);
                }
                Interlocked.Increment(ref done);
            })).ToArray();
            var pumper = new Thread(() =>
            {
                var quotes = new DuelQuote[KrxArenaEngine.MaxSymbols];
                long utc = Utc10Kst;
                while (Volatile.Read(ref done) < Producers)
                {
                    f.Pump(utc += TimeSpan.TicksPerMillisecond);
                    pumps++;
                    int n = e.CopyQuotes(quotes);
                    for (int i = 0; i < n; i++)
                    {
                        var q = quotes[i];
                        if (q.AskWon != q.BidWon + 100 || q.BidQty != q.BidWon / 10 || q.AskQty != q.BidQty + 1) Interlocked.Increment(ref torn);
                    }
                }
            });
            pumper.Start();
            foreach (var t in producers) t.Start();
            foreach (var t in producers) t.Join();
            pumper.Join();
            _out.WriteLine($"pumps={pumps} bridgeUpdates={f.BridgeUpdates} engineTicks={Snap(e).Ticks}");
            Assert.Equal(0, torn);
            Assert.Equal(Producers * PerProducer, f.BridgeUpdates);
            Assert.Equal(0, f.DroppedBooks);
            Assert.True(pumps > 0);
        }

        // ── §3 합성 데모 · 출처 전환 ──────────────────────────────────────────────

        [Fact]
        public void Synthetic_Demo_Runs_Virtual_Sessions_And_Every_Ai_Fill_Has_Rationale()
        {
            var e = new KrxArenaEngine();
            var f = new KrxArenaQuoteFeeder(e);
            f.OnBook(Book(Samsung, 70_000, 70_100));
            Pump(f);
            Assert.Equal(1, Snap(e).Ticks);
            Assert.Equal(KrxArenaReject.None, e.SubmitHuman(Samsung, 1, 5));
            Pump(f);                                           // 브리지 주입 중 휴먼 매수 체결
            Assert.Equal(1, Snap(e).Human.Fills);

            Assert.True(f.SelectSource(KrxArenaFeed.Synthetic));
            Assert.Equal(KrxArenaFeed.Bridge, f.AppliedSource);
            Pump(f);
            Assert.Equal(KrxArenaFeed.Synthetic, f.AppliedSource);
            var s = Snap(e);
            Assert.Equal(0, s.Human.Fills);                    // 출처 전환 = 원장 초기화
            Assert.Equal(KrxArenaEngine.DefaultCapitalWon, s.Human.CashWon);
            Assert.Equal(KrxArenaQuoteFeeder.SyntheticSymbols.Length, s.Ticks);
            var kst0 = new DateTime(f.SyntheticKstTicks);
            Assert.Equal(DayOfWeek.Thursday, kst0.DayOfWeek);
            Assert.Equal(new TimeSpan(9, 0, 1), kst0.TimeOfDay);

            int pumps = 30_000;                                // 가상 30,000초 ≈ 8.3h → 다음 가상 거래일 진입
            for (int i = 0; i < pumps; i++)
            {
                Pump(f);
                if (i % 1000 == 999)
                {
                    var c = Snap(e);
                    Assert.Equal(c.Ai.Fills, c.RationaleCount);
                }
            }
            var fin = Snap(e);
            var kst = new DateTime(f.SyntheticKstTicks);
            _out.WriteLine($"virtual={kst:yyyy-MM-dd HH:mm:ss} aiFills={fin.Ai.Fills} w={fin.Ai.Wins} l={fin.Ai.Losses} ret={fin.Ai.ReturnBp}bp macro={fin.MacroBp}");
            Assert.True(kst.Date > kst0.Date, "15:30 도달 후 다음 평일 09:00 로 넘어가야 한다");
            Assert.InRange(kst.TimeOfDay, KrxArenaEngine.SessionStart, KrxArenaEngine.SessionEnd);
            Assert.True(fin.Ai.Fills >= 4, $"합성 장세에서 AI 체결이 너무 적음({fin.Ai.Fills})");
            Assert.Equal(fin.Ai.Fills, fin.RationaleCount);
            var rs = Rationales(e);
            Assert.All(rs, r => Assert.True(r.IsWellFormed, r.HumanReadableSummary));
            Assert.Contains(rs, r => r.TriggerType == ArenaTrigger.SessionClose || r.TriggerType == ArenaTrigger.TakeProfit || r.TriggerType == ArenaTrigger.StopLoss || r.TriggerType == ArenaTrigger.SignalReversal);
            Assert.All(rs, r => Assert.Contains(r.Symbol, KrxArenaQuoteFeeder.SyntheticSymbols));
            Assert.All(rs, r => Assert.True(KrxTickLadder.IsOnTick(r.PriceWon)));

            // 합성 중 브리지 갱신은 엔진 미주입 → 브리지 복귀 = 원장 초기화 · 묵은 슬롯 재주입 0
            f.OnBook(Book(Samsung, 71_000, 71_100));
            Pump(f);
            Assert.True(f.SelectSource(KrxArenaFeed.Bridge));
            Pump(f);
            s = Snap(e);
            Assert.Equal(KrxArenaFeed.Bridge, f.AppliedSource);
            Assert.Equal(0, s.Ticks);
            Assert.Equal(0, s.RationaleCount);
            Assert.Equal(0, f.SyntheticKstTicks);
            f.OnBook(Book(Samsung, 71_000, 71_100));
            Pump(f);
            Assert.Equal(1, Snap(e).Ticks);
        }

        [Fact]
        public void Next_Session_Open_Skips_Weekend()
        {
            Assert.Equal(new DateTime(2026, 10, 26, 9, 0, 0), KrxArenaQuoteFeeder.NextSessionOpen(new DateTime(2026, 10, 24)));
            Assert.Equal(new DateTime(2026, 10, 26, 9, 0, 0), KrxArenaQuoteFeeder.NextSessionOpen(new DateTime(2026, 10, 25, 13, 0, 0)));
            Assert.Equal(new DateTime(2026, 10, 23, 9, 0, 0), KrxArenaQuoteFeeder.NextSessionOpen(new DateTime(2026, 10, 23)));
        }

        // ── §4 δ=0 ─────────────────────────────────────────────────────────────

        [Fact]
        public void Bridge_And_Synthetic_Feed_100k_Allocate_Zero_Bytes()
        {
            var e = new KrxArenaEngine();
            var f = new KrxArenaQuoteFeeder(e);
            var books = new OrderBookPayload[4];
            for (int i = 0; i < books.Length; i++) books[i] = Book(Samsung + i, 70_000 + i * 100, 70_100 + i * 100, 1000 + i, 900);
            long utc = Utc10Kst, cum = 1;
            int k = 0;

            void Bridge(int n)
            {
                for (int i = 0; i < n; i++, k++)
                {
                    ref var b = ref books[k & 3];
                    b.BidQty[0] = 500 + (k & 1023);
                    f.OnBook(in b);
                    f.OnTrade(b.SymbolCode, cum += 1 + (k & 63));
                    if ((k & 1) == 0) f.Pump(utc += TimeSpan.TicksPerMillisecond);
                    if ((k & 15) == 0) e.SubmitHuman(b.SymbolCode, (k & 16) == 0 ? 1 : -1, 1);
                }
            }

            void Synthetic(int n)
            {
                for (int i = 0; i < n; i++) f.Pump(utc += TimeSpan.TicksPerMillisecond);
            }

            Bridge(20_000);
            long bridgeBytes = MinAllocated(() => Bridge(100_000));
            long bridgeTicks = Snap(e).Ticks;
            f.SelectSource(KrxArenaFeed.Synthetic);
            Synthetic(20_000);
            long aiBefore = Snap(e).Ai.Fills;
            long synBytes = MinAllocated(() => Synthetic(100_000));
            var s = Snap(e);
            _out.WriteLine($"bridge δ={bridgeBytes}B ticks={bridgeTicks} · synthetic δ={synBytes}B aiFills={s.Ai.Fills}");
            Assert.Equal(0, bridgeBytes);
            Assert.Equal(0, synBytes);
            Assert.True(bridgeTicks > 50_000);
            Assert.True(s.Ai.Fills > aiBefore, "측정 구간 안에서 AI 가상 체결이 일어나야 한다");
        }

        // ── §5 허브 펌프 ────────────────────────────────────────────────────────

        [Fact]
        public void Dashboard_Hub_Pump_Drives_Feeder()
        {
            long now = Utc10Kst;
            var e = new KrxArenaEngine();
            var f = new KrxArenaQuoteFeeder(e);
            using var hub = new DashboardSnapshotHub(nowTicks: () => now);
            Assert.Throws<ArgumentNullException>(() => hub.AttachKrxArena(null!));
            hub.AttachKrxArena(f);
            f.OnBook(Book(Samsung, 70_000, 70_100));
            hub.Pump();
            Assert.Equal(1, f.Pumps);
            Assert.Equal(1, Snap(e).Ticks);
            Assert.True(Snap(e).SessionOpen);
        }

        // ── §6 UI ─────────────────────────────────────────────────────────────

        private static (Bunit.TestContext ctx, IRenderedComponent<DualArenaPage> cut) Render(IKrxArenaPort port)
        {
            var ctx = new Bunit.TestContext();
            ctx.Services.AddSingleton(port);
            ctx.Services.AddSingleton<IUsArenaPort>(new UsArenaPortAdapter(new StockManager.Engines.Arena.Us.UsTechArenaEngine()));   // [v12.18.62-DEV] 미국 모드 탭 포트(국내 기본 모드 · 미사용)
            ctx.Services.AddSingleton<ICryptoArenaPort>(new CryptoArenaPortAdapter(new StockManager.Engines.Arena.Crypto.UpbitCryptoArenaEngine()));   // [v12.18.66-DEV] 가상자산 탭 포트(미사용)
            ctx.Services.AddSingleton<IArenaPreferencePort>(UnwiredArenaPreferencePort.Instance);   // [v12.18.69-DEV] 토글 영속화 포트(저장소 미결선 null 객체)
            ctx.Services.AddSingleton<IPlutoLearningPort>(UnwiredPlutoLearningPort.Instance);   // [v12.18.77-DEV] 학습 엔진 미결선
            return (ctx, ctx.RenderComponent<DualArenaPage>());
        }

        [Fact]
        public void Page_Renders_Honest_Empty_States()
        {
            var e = new KrxArenaEngine();
            var (ctx1, cut1) = Render(new KrxArenaPortAdapter(e));
            using (ctx1)
            {
                Assert.Contains("정산 펌프 미결선", cut1.Markup);
                Assert.Empty(cut1.FindAll(".da-book"));
                Assert.Empty(cut1.FindAll(".da-card"));
                Assert.Contains("근거 카드는 실제 가상 체결이 일어날 때만", cut1.Markup);
            }

            var f = new KrxArenaQuoteFeeder(e);
            f.Pump(Utc10Kst);
            var (ctx2, cut2) = Render(new KrxArenaPortAdapter(e, f));
            using (ctx2)
            {
                Assert.Contains("시세 미수신", cut2.Markup);
                Assert.Contains("브리지 호가 대기", cut2.Markup);
                Assert.Empty(cut2.FindAll(".da-book"));
            }
            Assert.False(new KrxArenaPortAdapter(e).SelectSource(KrxArenaFeed.Synthetic));
            Assert.Throws<ArgumentException>(() => new KrxArenaPortAdapter(new KrxArenaEngine(), f));
        }

        [Fact]
        public void Page_Shows_Scoreboard_Rationale_Cards_And_Routes_Ticket_And_Source_Tabs_Through_Port()
        {
            var e = new KrxArenaEngine();
            var f = new KrxArenaQuoteFeeder(e);
            var port = new KrxArenaPortAdapter(e, f);
            f.SelectSource(KrxArenaFeed.Synthetic);
            for (int i = 0; i < 30_000 && Snap(e).RationaleCount < 3; i++) Pump(f);
            Assert.True(Snap(e).RationaleCount >= 1);

            var (ctx, cut) = Render(port);
            using (ctx)
            {
                Assert.Equal(2, cut.FindAll(".da-book").Count);
                Assert.Contains("합성 시세(데모", cut.Find(".da-badge.syn").TextContent);
                var cards = cut.FindAll(".da-card");
                Assert.Equal((int)Math.Min(Snap(e).RationaleCount, DualArenaPage.MaxCards), cards.Count);
                var first = cards[0];
                Assert.Equal(Snap(e).RationaleCount.ToString(), first.GetAttribute("data-seq"));
                Assert.Contains("[AI ", first.QuerySelector(".da-summary")!.TextContent);
                Assert.Contains("확신도", first.TextContent);
                Assert.Contains("손익비", first.TextContent);
                Assert.Contains("/100", first.QuerySelector(".da-conf")!.TextContent);
                Assert.False(string.IsNullOrWhiteSpace(first.QuerySelector(".da-trigger")!.TextContent));
                Assert.Equal(KrxArenaQuoteFeeder.SyntheticSymbols.Length, cut.FindAll(".da-sym option").Count);

                // 휴먼 티켓 → 포트 → 휴먼 큐 → 다음 정산 체결
                cut.Find(".da-sym").Change(Samsung.ToString());
                cut.Find(".da-qty").Change("3");
                cut.Find(".ar-btn.buy").Click();
                Assert.Contains("매수 접수", cut.Find(".da-msg").TextContent);
                long fills0 = Snap(e).Human.Fills;
                Pump(f);
                Assert.Equal(fills0 + 1, Snap(e).Human.Fills);
                Assert.Contains(port.Positions(ArenaSide.Human), p => p.Symbol == Samsung && p.Qty == 3);

                // 출처 탭 → 포트 SelectSource(다음 정산 적용)
                cut.FindAll(".ar-tab")[0].Click();
                Assert.Equal(KrxArenaFeed.Bridge, f.RequestedSource);
                Assert.Contains("양 진영 원장 초기화", cut.Find(".da-msg").TextContent);
                Pump(f);
                Assert.Equal(KrxArenaFeed.Bridge, f.AppliedSource);
                cut.WaitForAssertion(() => Assert.Contains("시세 미수신", cut.Markup), TimeSpan.FromSeconds(5));
            }
        }

        [Fact]
        public void Pure_Shell_Page_Injects_Only_Arena_Ports_And_Host_Shell_Is_Uncoupled()
        {
            const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly;
            var injected = typeof(DualArenaPage).GetProperties(all).Where(p => p.GetCustomAttribute<InjectAttribute>() is not null).Select(p => p.PropertyType.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();
            Assert.Equal(new[] { nameof(IArenaPreferencePort), nameof(ICryptoArenaPort), nameof(IKrxArenaPort), nameof(IPlutoLearningPort), nameof(IUsArenaPort) }, injected);   // [v12.18.77-DEV] 지속 학습 뱃지 = IPlutoLearningPort · [v12.18.69-DEV] 거버너 토글 영속화 = IArenaPreferencePort · [v12.18.62-DEV] 미국 모드 탭 = IUsArenaPort · [v12.18.66-DEV] 가상자산 탭 = ICryptoArenaPort 추가

            var shell = typeof(KrxArenaEngine).Assembly.GetType("StockManager.Blazor.Pages.DualArena");
            Assert.NotNull(shell);
            Assert.False(PureShellFitnessSentinel.IsHostCoupledUi(shell!));
            Assert.Equal("/dualarena", shell!.GetCustomAttribute<RouteAttribute>()!.Template);

            var names = typeof(IKrxArenaPort).GetMembers().Select(m => m.Name).ToArray();
            foreach (var bad in new[] { "Arm", "Broker", "Live", "Send", "Route", "Valve", "Order" })
                Assert.DoesNotContain(names, n => n.Contains(bad, StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void Host_Wires_Bridge_To_Feeder_Hub_And_Port_And_Nav_Lists_Page()
        {
            string root = RepoRoot();
            string form = File.ReadAllText(Path.Combine(root, "StockManager", "Blazor", "BlazorHomeForm.cs"));
            Assert.Contains("bridge.OrderBookReceived += b => krxArenaFeeder.OnBook(in b);", form);
            Assert.Contains("bridge.TickReceived += t => krxArenaFeeder.OnTrade(t.SymbolCode, t.Volume);", form);
            Assert.Contains("dashHub.AttachKrxArena(krxArenaFeeder);", form);
            Assert.Contains("new StockManager.Adapters.KrxArenaPortAdapter(krxArena, krxArenaFeeder)", form);
            string nav = File.ReadAllText(Path.Combine(root, "MS2E.UI.Portable", "Components", "NavMenu.razor"));
            Assert.Contains("\"/dualarena\"", nav);
        }

        private static string RepoRoot()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d is not null && !File.Exists(Path.Combine(d.FullName, "StockManager.sln"))) d = d.Parent;
            return d?.FullName ?? throw new DirectoryNotFoundException("StockManager.sln");
        }
    }
}
