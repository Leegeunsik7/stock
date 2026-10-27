// ============================================================================
// DualArenaUxPolishTests — v12.18.91-DEV: /dualarena UX 결함 일괄(입력 시인성 · 🔍 종목선택 모달 · 원클릭 전 자산 데모 시세 · 거버너 상태 유지)
//
//  §1 유니버스 어댑터 — 420종 카탈로그 → 4탭(코스피 · 코스닥 · 미국주 · 업비트) 투사 · 아레나 표기 코드 · 범위 밖 탭 = 빈 목록 · 미결선 null 객체
//  §2 모달 필터 — 이름 · 코드 부분일치(대소문자 무시) · 호가 수신 종목 우선
//  §3 페이지 피커 — 실수신 종목 = 선택 가능(클릭 → 모드 전환 + 티켓 바인딩 + 호가 문구) · 미수신 = 비활성 행(가짜 현재가 0) · 검색 · 닫기
//  §4 원클릭 데모 — 3대 피더 합성 출처 일괄 요청 · 가동 중 = 비활성 · 주입기 미결선 = 정직 안내
//  §5 거버너 — 페이지 재생성(메뉴 이동 후 복귀) 후에도 ON 유지(싱글톤 엔진 플래그가 단일 진실원)
//  §6 CSS — 콕핏 · /arena 입력 상자 명시 대비색(#1F2937 / #F9FAFB !important / #374151) · color: inherit 제거
// ============================================================================
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MS2E.UI.Portable.Components;
using MS2E.UI.Portable.Pages.Trading;
using MS2E.UI.Portable.Ports.Trading;
using StockManager.Adapters;
using StockManager.Engines.Arena.Crypto;
using StockManager.Engines.Arena.Domestic;
using StockManager.Engines.Arena.Us;
using StockManager.Engines.Cognitive.Screening;
using Xunit;

namespace StockManager.Tests.Engines.Arena
{
    [Collection("ConcurrencyStress")]
    public sealed class DualArenaUxPolishTests
    {
        private const long Samsung = 5930;
        private static readonly DateTime Open10 = new(2026, 10, 22, 10, 0, 0);   // 목요일 10:00 KST

        private static (Bunit.TestContext Ctx, IRenderedComponent<DualArenaPage> Cut) Render(IKrxArenaPort krx, IUsArenaPort us, ICryptoArenaPort crypto, IArenaUniversePort? universe = null)
        {
            var ctx = new Bunit.TestContext();
            ctx.Services.AddSingleton(krx);
            ctx.Services.AddSingleton(us);
            ctx.Services.AddSingleton(crypto);
            ctx.Services.AddSingleton<IArenaPreferencePort>(UnwiredArenaPreferencePort.Instance);
            ctx.Services.AddSingleton<IPlutoLearningPort>(UnwiredPlutoLearningPort.Instance);
            ctx.Services.AddSingleton<IPlutoRecommendationPort>(UnwiredPlutoRecommendationPort.Instance);
            if (universe is not null) ctx.Services.AddSingleton(universe);
            return (ctx, ctx.RenderComponent<DualArenaPage>());
        }

        private static KrxArenaPortAdapter QuotedKrx(KrxArenaEngine e)
        {
            e.OnQuote(Samsung, 70_000, 70_100, 1000, 1000, 100, Open10);
            var f = new KrxArenaQuoteFeeder(e);
            f.Pump(Open10.AddHours(-9).Ticks);
            return new KrxArenaPortAdapter(e, f);
        }

        private static string RepoFile(params string[] parts)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "StockManager.sln"))) dir = dir.Parent;
            Assert.NotNull(dir);
            return File.ReadAllText(Path.Combine(new[] { dir!.FullName }.Concat(parts).ToArray()));
        }

        // ── §1 유니버스 어댑터 ──────────────────────────────────────────────

        [Fact]
        public void Universe_Adapter_Projects_Full_Catalog_Into_Four_Tabs()
        {
            var u = new ArenaUniversePortAdapter();
            Assert.Equal(PlutoMultiAssetUniverseCatalog.TotalCount, u.TotalCount);
            Assert.Equal(420, u.TotalCount);
            int sum = 0;
            for (int t = 0; t < ArenaUniverseTab.Count; t++)
            {
                var rows = u.Entries(t);
                Assert.NotEmpty(rows);
                Assert.All(rows, r => Assert.Equal(t, r.Tab));
                sum += rows.Count;
            }
            Assert.Equal(u.TotalCount, sum);
            Assert.Equal(PlutoMultiAssetUniverseCatalog.KrxCount, u.Entries(ArenaUniverseTab.Kospi).Count + u.Entries(ArenaUniverseTab.Kosdaq).Count);
            Assert.All(u.Entries(ArenaUniverseTab.Kospi).Concat(u.Entries(ArenaUniverseTab.Kosdaq)), r => Assert.Matches("^[0-9]{6}$", r.Code));
            Assert.All(u.Entries(ArenaUniverseTab.Crypto), r => Assert.StartsWith("KRW-", r.Code));
            Assert.Contains(u.Entries(ArenaUniverseTab.Kospi), r => r.Code == "005930" && r.Name == "삼성전자");
            Assert.Contains(u.Entries(ArenaUniverseTab.Us), r => r.Code == "NVDA");
            Assert.Empty(u.Entries(-1));
            Assert.Empty(u.Entries(ArenaUniverseTab.Count));

            Assert.False(UnwiredArenaUniversePort.Instance.IsWired);
            Assert.Equal(0, UnwiredArenaUniversePort.Instance.TotalCount);
            Assert.Empty(UnwiredArenaUniversePort.Instance.Entries(ArenaUniverseTab.Kospi));
            Assert.Equal(new[] { "코스피", "코스닥", "미국주", "업비트" }, Enumerable.Range(0, ArenaUniverseTab.Count).Select(ArenaUniverseTab.Label));
        }

        // ── §2 모달 필터 ────────────────────────────────────────────────────

        [Fact]
        public void Modal_Filter_Matches_Name_Or_Code_And_Puts_Quoted_First()
        {
            var all = new[]
            {
                new ArenaUniverseEntryView(ArenaUniverseTab.Kospi, "000660", "SK하이닉스", "코스피 200"),
                new ArenaUniverseEntryView(ArenaUniverseTab.Kospi, "005930", "삼성전자", "코스피 200"),
                new ArenaUniverseEntryView(ArenaUniverseTab.Kospi, "006400", "삼성SDI", "코스피 200"),
            };
            Assert.Equal(3, StockSearchModal.Filter(all, "").Count);
            Assert.Equal(new[] { "005930", "006400" }, StockSearchModal.Filter(all, " 삼성 ").Select(e => e.Code));
            Assert.Equal(new[] { "000660" }, StockSearchModal.Filter(all, "0006").Select(e => e.Code));
            Assert.Equal(new[] { "000660" }, StockSearchModal.Filter(all, "sk").Select(e => e.Code));
            Assert.Empty(StockSearchModal.Filter(all, "없는종목"));
            var quoted = new Dictionary<string, string> { ["006400"] = "매수 1 / 매도 2원" };
            Assert.Equal(new[] { "006400", "000660", "005930" }, StockSearchModal.Filter(all, null, quoted).Select(e => e.Code));
        }

        // ── §3 페이지 피커 ──────────────────────────────────────────────────

        [Fact]
        public void Picker_Binds_Quoted_Stock_And_Blocks_Unquoted()
        {
            var krx = new KrxArenaEngine();
            var (ctx, cut) = Render(QuotedKrx(krx), new UsArenaPortAdapter(new UsTechArenaEngine()), new CryptoArenaPortAdapter(new UpbitCryptoArenaEngine()), new ArenaUniversePortAdapter());
            using (ctx)
            {
                Assert.Empty(cut.FindAll(".ssm-modal"));
                cut.Find(".da-pick[data-pick=krx]").Click();
                var modal = cut.Find(".ssm-modal");
                Assert.Equal(ArenaUniverseTab.Kospi.ToString(), modal.GetAttribute("data-ssm-tab"));
                Assert.Equal("1", cut.Find(".ssm-count").GetAttribute("data-ssm-live"));   // 코스피 200 중 실수신 = 삼성전자 1종
                Assert.Contains("420", cut.Find(".ssm-title").TextContent);

                var live = cut.Find(".ssm-row.live[data-ssm-code=\"005930\"]");
                Assert.Contains("매수 70,000 / 매도 70,100원", live.TextContent);
                Assert.Equal("005930", cut.Find(".ssm-row").GetAttribute("data-ssm-code"));   // 수신 종목 우선 정렬
                var idle = cut.Find(".ssm-row.idle[data-ssm-code=\"000660\"]");
                Assert.Contains("시세 미수신 · 선택 불가", idle.TextContent);
                Assert.Equal("true", idle.GetAttribute("aria-disabled"));
                Assert.DoesNotContain("원", idle.QuerySelector(".ssm-px")!.TextContent);       // 미수신 = 가짜 현재가 0

                cut.Find(".ssm-q").Input("하이닉스");
                Assert.Single(cut.FindAll(".ssm-row"));
                cut.Find(".ssm-q").Input("삼성전자");
                cut.Find(".ssm-row.live[data-ssm-code=\"005930\"]").Click();

                Assert.Empty(cut.FindAll(".ssm-modal"));
                Assert.Equal(Samsung.ToString(), cut.Find("select.da-sym").GetAttribute("value"));
                Assert.Contains("삼성전자(005930)", cut.Find(".da-msg").TextContent);
                Assert.Contains("매수/매도로 접수", cut.Find(".da-msg").TextContent);
            }
        }

        [Fact]
        public void Picker_Us_And_Crypto_Tabs_Without_Quotes_Are_Idle_And_Close_On_X()
        {
            var (ctx, cut) = Render(QuotedKrx(new KrxArenaEngine()), new UsArenaPortAdapter(new UsTechArenaEngine()), new CryptoArenaPortAdapter(new UpbitCryptoArenaEngine()), new ArenaUniversePortAdapter());
            using (ctx)
            {
                cut.Find(".da-pick[data-pick=krx]").Click();
                cut.Find(".ssm-tab[data-ssm-tabbtn=\"2\"]").Click();                            // 미국주 탭 — KIS 시세 미수신
                Assert.Equal(ArenaUniverseTab.Us.ToString(), cut.Find(".ssm-modal").GetAttribute("data-ssm-tab"));
                Assert.Equal("0", cut.Find(".ssm-count").GetAttribute("data-ssm-live"));
                Assert.Empty(cut.FindAll(".ssm-row.live"));
                Assert.NotEmpty(cut.FindAll(".ssm-row.idle[data-ssm-code=\"NVDA\"]"));
                cut.Find(".ssm-tab[data-ssm-tabbtn=\"3\"]").Click();
                Assert.Equal(ArenaUniverseTab.Crypto.ToString(), cut.Find(".ssm-modal").GetAttribute("data-ssm-tab"));
                Assert.Empty(cut.FindAll(".ssm-row.live"));
                cut.Find(".ssm-x").Click();
                Assert.Empty(cut.FindAll(".ssm-modal"));
                Assert.Equal("da-mode-tab on", cut.Find(".da-mode-tab[data-mode=krx]").ClassName);   // 선택 없이 닫기 = 모드 불변
            }
        }

        [Fact]
        public void Picker_With_Unwired_Universe_Says_So()
        {
            var (ctx, cut) = Render(QuotedKrx(new KrxArenaEngine()), new UsArenaPortAdapter(new UsTechArenaEngine()), new CryptoArenaPortAdapter(new UpbitCryptoArenaEngine()), UnwiredArenaUniversePort.Instance);
            using (ctx)
            {
                cut.Find(".da-pick[data-pick=krx]").Click();
                Assert.Contains("유니버스 미결선", cut.Find(".ssm-empty").TextContent);
            }
        }

        // ── §4 원클릭 데모 ──────────────────────────────────────────────────

        [Fact]
        public void Demo_Button_Requests_Synthetic_On_All_Three_Feeders()
        {
            var krx = new KrxArenaEngine(); var us = new UsTechArenaEngine(); var cx = new UpbitCryptoArenaEngine();
            var kf = new KrxArenaQuoteFeeder(krx); var uf = new UsArenaQuoteFeeder(us); var cf = new CryptoArenaQuoteFeeder(cx);
            var (ctx, cut) = Render(new KrxArenaPortAdapter(krx, kf), new UsArenaPortAdapter(us, uf), new CryptoArenaPortAdapter(cx, cf));
            using (ctx)
            {
                var btn = cut.Find(".ck-demo");
                Assert.Equal("off", btn.GetAttribute("data-demo-all"));
                Assert.Contains("원클릭 전 자산 데모 시세 주입", btn.TextContent);
                btn.Click();

                Assert.Equal(KrxArenaFeed.Synthetic, kf.RequestedSource);
                Assert.Equal(UsArenaFeed.Synthetic, uf.RequestedSource);
                Assert.Equal(CryptoArenaFeed.Synthetic, cf.RequestedSource);
                btn = cut.Find(".ck-demo");
                Assert.Equal("on", btn.GetAttribute("data-demo-all"));
                Assert.True(btn.HasAttribute("disabled"));
                Assert.Contains("가동 중", btn.TextContent);
                var msg = cut.Find(".ck-demo-msg").TextContent;
                Assert.Contains("합성", msg);
                Assert.Contains("실시세 아님", msg);
            }
        }

        [Fact]
        public void Demo_Button_Without_Feeders_Reports_Unwired_Honestly()
        {
            var (ctx, cut) = Render(new KrxArenaPortAdapter(new KrxArenaEngine()), new UsArenaPortAdapter(new UsTechArenaEngine()), new CryptoArenaPortAdapter(new UpbitCryptoArenaEngine()));
            using (ctx)
            {
                cut.Find(".ck-demo").Click();
                Assert.Equal("off", cut.Find(".ck-demo").GetAttribute("data-demo-all"));
                Assert.Contains("국내 미국 코인 주입기 미결선", cut.Find(".ck-demo-msg").TextContent);
            }
        }

        // ── §5 거버너 상태 유지 ─────────────────────────────────────────────

        [Fact]
        public void Governor_On_Survives_Page_Recreation()
        {
            var krx = new KrxArenaEngine(); var us = new UsTechArenaEngine(); var cx = new UpbitCryptoArenaEngine();
            IKrxArenaPort kp = new KrxArenaPortAdapter(krx); IUsArenaPort up = new UsArenaPortAdapter(us); ICryptoArenaPort cp = new CryptoArenaPortAdapter(cx);

            var (ctx1, cut1) = Render(kp, up, cp);
            using (ctx1)
            {
                cut1.Find(".da-gov-toggle").Click();
                Assert.Equal("on", cut1.Find(".da-gov-toggle").GetAttribute("data-gov"));
            }                                                                                   // 메뉴 이동 = 페이지 컴포넌트 폐기

            var (ctx2, cut2) = Render(kp, up, cp);                                             // 복귀 = 새 컴포넌트 인스턴스 · 같은 싱글톤 포트
            using (ctx2)
            {
                var btn = cut2.Find(".da-gov-toggle");
                Assert.Equal("on", btn.GetAttribute("data-gov"));
                Assert.Contains("(ON)", btn.TextContent);
                Assert.True(krx.GovernanceEnabled && us.GovernanceEnabled && cx.GovernanceEnabled);
            }
        }

        // ── §6 CSS ──────────────────────────────────────────────────────────

        [Fact]
        public void Inputs_Use_Explicit_High_Contrast_Colors()
        {
            const string Contrast = "background: #1F2937; color: #F9FAFB !important; border: 1px solid #374151;";
            string page = RepoFile("MS2E.UI.Portable", "Pages", "Trading", "DualArenaPage.razor");
            Assert.Contains(".da-cockpit input:not([type=checkbox]):not([type=radio]), .da-cockpit select { " + Contrast, page);
            Assert.DoesNotContain(".da-ticket input { width: 90px; background: #0f1219; color: inherit;", page);
            Assert.DoesNotContain(".da-ticket select { background: #0f1219; color: inherit;", page);

            string arena = RepoFile("MS2E.UI.Portable", "Pages", "Trading", "ArenaBattleView.razor");
            Assert.Contains(".ar-ctl input, .ar-row input { width: 130px; " + Contrast, arena);
            Assert.Contains(".ar-iceberg input.ar-ice-peak { width: 90px; " + Contrast, arena);
            Assert.Contains(".ar-replay select { " + Contrast, arena);

            string modal = RepoFile("MS2E.UI.Portable", "Components", "StockSearchModal.razor");
            Assert.Contains(".ssm-q { " + Contrast, modal);
            Assert.DoesNotContain("StockManager.", modal);                                     // 순수 RCL(엔진 참조 0)
        }
    }
}
