// ============================================================================
// DualArenaModeTabTests — v12.18.62-DEV: DualArenaPage 상단 모드 탭 [국내 대형주(KRW ₩) | 미국 빅테크(USD $)] · IUsArenaPort 바인딩
//
// §1 기본 모드 = 국내 · 탭 2개 · 미국 모드 정직 렌더(펌프 미결선 / 시세 미수신 — 가짜 손익 · 가짜 카드 0)
// §2 미국 합성 대결 — USD 스코어보드($100,000.00 · $0.01 표기) · 세션 뱃지 · 빅테크 5종 티켓 · USD 근거 카드(목표/손절 $ · 확신도 · 팩터)
// §3 모드 간 교차 오염 0 — 미국 휴먼 주문은 미국 원장에만 · 국내 원장/메시지 불변 · 출처 탭은 활성 모드 포트로만
// §4 렌더 펌프 — 미국 모드에서 미국 게시 버전 변화만으로 다시 그린다 · 포트 표면에 무장/브로커/실주문 멤버 0
// ============================================================================
using System;
using System.Linq;
using System.Text.RegularExpressions;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MS2E.UI.Portable.Pages.Trading;
using MS2E.UI.Portable.Ports.Trading;
using StockManager.Adapters;
using StockManager.Engines.Arena.Domestic;
using StockManager.Engines.Arena.Us;
using Xunit;
using Xunit.Abstractions;

namespace StockManager.Tests.Engines.Arena
{
    [Collection("ConcurrencyStress")]
    public sealed class DualArenaModeTabTests
    {
        // 2026-10-22(목) 12:00 UTC 기점 · 정산 간격 50ms(합성 가상 세션 ×20)
        private static readonly long Utc12 = new DateTime(2026, 10, 22, 12, 0, 0).Ticks;
        private static readonly Regex UsdText = new(@"^[+-]?-?\$\d{1,3}(,\d{3})*\.\d{2}$", RegexOptions.CultureInvariant);
        private readonly ITestOutputHelper _out;
        private long _utc = Utc12;

        public DualArenaModeTabTests(ITestOutputHelper o) => _out = o;

        private void Pump(UsArenaQuoteFeeder f) { _utc += 50 * TimeSpan.TicksPerMillisecond; f.Pump(_utc); }

        private static UsArenaSnapshot Snap(UsTechArenaEngine e) { Assert.True(e.TryReadSnapshot(out var s)); return s; }

        private static (Bunit.TestContext ctx, IRenderedComponent<DualArenaPage> cut) Render(IKrxArenaPort krx, IUsArenaPort us)
        {
            var ctx = new Bunit.TestContext();
            ctx.Services.AddSingleton(krx);
            ctx.Services.AddSingleton(us);
            ctx.Services.AddSingleton<ICryptoArenaPort>(new CryptoArenaPortAdapter(new StockManager.Engines.Arena.Crypto.UpbitCryptoArenaEngine()));   // [v12.18.66-DEV] 3번째 탭 포트(미사용)
            return (ctx, ctx.RenderComponent<DualArenaPage>());
        }

        private static void ToUs(IRenderedComponent<DualArenaPage> cut) => cut.Find(".da-mode-tab[data-mode=us]").Click();
        private static void ToKrx(IRenderedComponent<DualArenaPage> cut) => cut.Find(".da-mode-tab[data-mode=krx]").Click();

        // 합성 출처로 정규장 AI 체결(근거 ≥ 1)이 생길 때까지 정산
        private void RunSyntheticUntilRegularWithRationale(UsTechArenaEngine e, UsArenaQuoteFeeder f)
        {
            Assert.True(f.SelectSource(UsArenaFeed.Synthetic));
            for (int i = 0; i < 200_000; i++)
            {
                Pump(f);
                var s = Snap(e);
                if (s.RationaleCount >= 1 && s.Tradable) return;
            }
            Assert.Fail("합성 장세에서 정규장 AI 체결이 발생하지 않음");
        }

        // ── §1 ─────────────────────────────────────────────────────────────────

        [Fact]
        public void Default_Mode_Is_Domestic_And_Us_Mode_Renders_Honest_Empty_States()
        {
            var krx = new KrxArenaEngine();
            var us = new UsTechArenaEngine();
            var (ctx, cut) = Render(new KrxArenaPortAdapter(krx), new UsArenaPortAdapter(us));
            using (ctx)
            {
                var tabs = cut.FindAll(".da-mode-tab");
                Assert.Equal(3, tabs.Count);   // [v12.18.66-DEV] 가상자산 탭 추가
                Assert.Contains("국내 대형주 (KRW ₩)", tabs[0].TextContent);
                Assert.Contains("미국 빅테크 (USD $)", tabs[1].TextContent);
                Assert.Contains("가상자산 (Upbit ₿)", tabs[2].TextContent);
                Assert.Contains("on", tabs[0].ClassList);
                Assert.Single(cut.FindAll(".da-krx"));
                Assert.Empty(cut.FindAll(".da-us"));

                ToUs(cut);
                Assert.Contains("on", cut.Find(".da-mode-tab[data-mode=us]").ClassList);
                Assert.Empty(cut.FindAll(".da-krx"));
                Assert.Single(cut.FindAll(".da-us"));
                Assert.Contains("미국 빅테크(US Tech)", cut.Find("h1").TextContent);
                Assert.Contains("정산 펌프 미결선", cut.Markup);
                Assert.Empty(cut.FindAll(".da-book"));
                Assert.Empty(cut.FindAll(".da-card"));
                Assert.Empty(cut.FindAll(".da-phase"));
            }

            var f = new UsArenaQuoteFeeder(us);
            f.Pump(Utc12);
            var (ctx2, cut2) = Render(new KrxArenaPortAdapter(krx), new UsArenaPortAdapter(us, f));
            using (ctx2)
            {
                ToUs(cut2);
                Assert.Contains("시세 미수신 — KIS 해외 현재체결가", cut2.Markup);
                Assert.Contains("KIS 해외 시세 대기", cut2.Markup);
                Assert.Empty(cut2.FindAll(".da-book"));
                Assert.Contains("근거 카드는 실제 가상 체결이 일어날 때만", cut2.Markup);
            }
        }

        // ── §2 · §3 ────────────────────────────────────────────────────────────

        [Fact]
        public void Us_Mode_Shows_Usd_Dashboard_And_Ticket_Routes_Only_To_Us_Ledger()
        {
            var krx = new KrxArenaEngine();
            var kf = new KrxArenaQuoteFeeder(krx);
            kf.Pump(new DateTime(2026, 10, 22, 1, 0, 0).Ticks);
            var us = new UsTechArenaEngine();
            var uf = new UsArenaQuoteFeeder(us);
            var usPort = new UsArenaPortAdapter(us, uf);
            RunSyntheticUntilRegularWithRationale(us, uf);

            var (ctx, cut) = Render(new KrxArenaPortAdapter(krx, kf), usPort);
            using (ctx)
            {
                Assert.Contains("시세 미수신", cut.Markup);                 // 국내 기본 모드 = 국내 포트 상태
                ToUs(cut);

                // 스코어보드 — 양 진영 · 자본금 $100,000.00 · USD 표기
                var books = cut.FindAll(".da-book");
                Assert.Equal(2, books.Count);
                Assert.All(books, b => Assert.Contains("자본금 $100,000.00", b.TextContent));
                Assert.All(books, b => Assert.Contains("MDD", b.TextContent));
                Assert.All(books, b => Assert.Contains("승률", b.TextContent));
                Assert.DoesNotContain("원", string.Concat(books.Select(b => b.QuerySelector(".ar-pnl")!.TextContent)));

                // 세션 뱃지 = 정규장 매매 가동 · 출처 뱃지 = 합성 명시
                var phase = cut.Find(".da-phase");
                Assert.Equal(UsArenaPhase.ContinuousTrading.ToString(), phase.GetAttribute("data-phase"));
                Assert.Contains("정규장 매매 가동", phase.TextContent);
                Assert.Contains("합성 시세(데모", cut.Find(".da-badge.syn").TextContent);

                // 티켓 — 빅테크 5종 · 거래소 · $0.01 호가
                var opts = cut.FindAll(".da-tkr option");
                Assert.Equal(new[] { "AAPL", "NVDA", "MSFT", "AMZN", "TSLA" }, opts.Select(o => o.GetAttribute("value")).ToArray());
                Assert.All(opts, o => Assert.Matches(@"매수 \$[\d,]+\.\d{2} / 매도 \$[\d,]+\.\d{2}", o.TextContent));

                // 근거 카드 — 최신순 · USD 목표/손절 · 확신도 · 팩터 · 한글 요약
                var cards = cut.FindAll(".da-card");
                Assert.Equal((int)Math.Min(Snap(us).RationaleCount, DualArenaPage.MaxCards), cards.Count);
                var first = cards[0];
                Assert.Equal(Snap(us).RationaleCount.ToString(), first.GetAttribute("data-seq"));
                Assert.Contains("/100", first.QuerySelector(".da-conf")!.TextContent);
                var tgt = first.QuerySelector(".da-tgt")!.TextContent;
                var stop = first.QuerySelector(".da-stop")!.TextContent;
                Assert.Matches(UsdText, tgt);
                Assert.Matches(UsdText, stop);
                Assert.Contains("ETF 베타", first.QuerySelector(".da-factors")!.TextContent);
                Assert.Contains("모멘텀", first.QuerySelector(".da-factors")!.TextContent);
                Assert.False(string.IsNullOrWhiteSpace(first.QuerySelector(".da-summary")!.TextContent));
                Assert.False(string.IsNullOrWhiteSpace(first.QuerySelector(".da-trigger")!.TextContent));

                // 휴먼 USD 티켓 → 미국 원장만
                long usFills0 = Snap(us).Human.Fills;
                cut.Find(".da-tkr").Change("MSFT");
                cut.Find(".da-uqty").Change("2");
                cut.Find(".ar-btn.buy").Click();
                Assert.Contains("매수 접수", cut.Find(".da-msg").TextContent);
                Pump(uf);
                Assert.Equal(usFills0 + 1, Snap(us).Human.Fills);
                Assert.Contains(usPort.Positions(ArenaSide.Human), p => p.Ticker == "MSFT" && p.Qty == 2);
                Assert.True(krx.TryReadSnapshot(out var ks));
                Assert.Equal(0, ks.Human.Fills);
                Assert.Equal(0, ks.Human.Rejects);
                Assert.Equal(KrxArenaEngine.DefaultCapitalWon, ks.Human.CashWon);

                // 국내 모드 복귀 — 미국 메시지 · 티켓 비노출 · 국내 출처 탭은 국내 주입기로만
                ToKrx(cut);
                Assert.Empty(cut.FindAll(".da-us"));
                Assert.Empty(cut.FindAll(".da-msg"));
                Assert.Contains("시세 미수신", cut.Markup);
                cut.FindAll(".ar-tab")[1].Click();
                Assert.Equal(KrxArenaFeed.Synthetic, kf.RequestedSource);
                Assert.Equal(UsArenaFeed.Synthetic, uf.RequestedSource);

                // 미국 모드 재진입 — 미국 출처 탭은 미국 주입기로만
                ToUs(cut);
                Assert.Contains("매수 접수", cut.Find(".da-msg").TextContent);
                cut.FindAll(".ar-tab")[0].Click();
                Assert.Equal(UsArenaFeed.KisOverseas, uf.RequestedSource);
                Assert.Equal(KrxArenaFeed.Synthetic, kf.RequestedSource);
                Assert.Contains("양 진영 원장 초기화", cut.Find(".da-msg").TextContent);
            }
        }

        [Fact]
        public void Us_Ticket_Reject_Is_Shown_And_Leaves_Ledger_Untouched()
        {
            var us = new UsTechArenaEngine();
            var uf = new UsArenaQuoteFeeder(us);
            RunSyntheticUntilRegularWithRationale(us, uf);
            var (ctx, cut) = Render(new KrxArenaPortAdapter(new KrxArenaEngine()), new UsArenaPortAdapter(us, uf));
            using (ctx)
            {
                ToUs(cut);
                long cash0 = Snap(us).Human.CashWon;
                cut.Find(".da-tkr").Change("AAPL");
                cut.Find(".ar-btn.sell").Click();                     // 보유 0 매도 = 접수 단계 또는 정산 단계 거부
                Pump(uf);
                var s = Snap(us);
                Assert.Equal(cash0, s.Human.CashWon);
                Assert.Equal(0, s.Human.Fills);
                cut.Find(".da-uqty").Change("0");
                cut.Find(".ar-btn.buy").Click();
                Assert.Contains("접수 거부", cut.Find(".da-msg").TextContent);
            }
        }

        // ── §4 ─────────────────────────────────────────────────────────────────

        [Fact]
        public void Render_Pump_Follows_Active_Us_Port_Version()
        {
            var us = new UsTechArenaEngine();
            var uf = new UsArenaQuoteFeeder(us);
            RunSyntheticUntilRegularWithRationale(us, uf);
            var (ctx, cut) = Render(new KrxArenaPortAdapter(new KrxArenaEngine()), new UsArenaPortAdapter(us, uf));
            using (ctx)
            {
                ToUs(cut);
                string ticks0 = Snap(us).Ticks.ToString("N0");
                Assert.Contains("정산 틱 " + ticks0, cut.Find(".ar-lead").TextContent);
                for (int i = 0; i < 20; i++) Pump(uf);
                string ticks1 = Snap(us).Ticks.ToString("N0");
                Assert.NotEqual(ticks0, ticks1);
                cut.WaitForAssertion(() => Assert.Contains("정산 틱 " + ticks1, cut.Find(".ar-lead").TextContent), TimeSpan.FromSeconds(5));
                _out.WriteLine($"ticks {ticks0} → {ticks1}");
            }

            var names = typeof(IUsArenaPort).GetMembers().Select(m => m.Name).ToArray();
            foreach (var bad in new[] { "Arm", "Broker", "Live", "Send", "Route", "Valve", "Order" })
                Assert.DoesNotContain(names, n => n.Contains(bad, StringComparison.OrdinalIgnoreCase));
        }
    }
}
