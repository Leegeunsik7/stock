// ============================================================================
// PlutoCockpitUiTests — v12.18.87-DEV: DualArena 다크 슬레이트 벤토 콕핏 · 인지 학습 관제탑(HUD) · 추천 매트릭스(점수 분해 · 정직 대기 행)
//
//   ① PlutoWeightScale = 엔진 PlutoCognitiveWeights 경계 미러(드리프트 = 실패) · 정규화/시소 기울기.
//   ② PlutoScoreBreakdown = 기본(4대 팩터 합) + 모멘텀 − 변호인 = 최종 · 0~100 포화 표기.
//   ③ PlutoCockpitMatrix.Rows = 전체 탭 점수 통합 순위 · 시장 탭 엔진 순위 · 피드 미판독 = 시장별 대기 행(종목명/점수 생성 0) · 미결선/첫 스캔 전 = 빈 목록.
//   ④ 페이지 렌더: 콕핏 래퍼 · 헤더(FAIL-CLOSED · 엔진 상태) · HUD 4타일(결선/미결선 정직) · 매트릭스 탭/분해/대기 행/담기 · 기존 셀렉터 보존.
//   ⑤ RCL 순수성: 신규 컴포넌트 주입 0 · 엔진 참조 0 · 페이지 주입 6종 불변.
// ============================================================================
using System;
using System.IO;
using System.Linq;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MS2E.UI.Portable.Pages.Trading;
using MS2E.UI.Portable.Ports.Trading;
using StockManager.Adapters;
using StockManager.Engines.Arena.Common;
using StockManager.Engines.Arena.Crypto;
using StockManager.Engines.Arena.Domestic;
using StockManager.Engines.Arena.Us;
using StockManager.Engines.Cognitive.Learning;
using Xunit;

namespace StockManager.Tests.Cognitive
{
    [Collection("ConcurrencyStress")]
    public sealed class PlutoCockpitUiTests
    {
        // ── ① 가중치 스케일 ───────────────────────────────────────────────────

        [Fact]
        public void Weight_Scale_Mirrors_Engine_Bounds_And_Normalizes()
        {
            Assert.Equal(PlutoCognitiveWeights.MaxMomentumTiltPct, PlutoWeightScale.MaxMomentumTiltPct);
            Assert.Equal(PlutoCognitiveWeights.MinStrictnessPts, PlutoWeightScale.MinStrictnessPts);
            Assert.Equal(PlutoCognitiveWeights.MaxStrictnessPts, PlutoWeightScale.MaxStrictnessPts);
            Assert.Equal(100, PlutoWeightScale.MomentumX100(50));
            Assert.Equal(-100, PlutoWeightScale.MomentumX100(-80));               // 범위 밖 = 포화
            Assert.Equal(30, PlutoWeightScale.MomentumX100(15));
            Assert.Equal(100, PlutoWeightScale.StrictnessX100(20));
            Assert.Equal(-50, PlutoWeightScale.StrictnessX100(-5));
            Assert.Equal(0, PlutoWeightScale.TiltX100(0, 0));
            Assert.Equal(-50, PlutoWeightScale.TiltX100(0, 20));                  // 방어 우세
            Assert.Equal(50, PlutoWeightScale.TiltX100(50, 0));                   // 공격 우세
            Assert.Equal("균형", PlutoWeightScale.TiltLabel(5));
            Assert.Equal("방어 우세(Devil)", PlutoWeightScale.TiltLabel(-50));
            Assert.Equal("공격 우세(Bull)", PlutoWeightScale.TiltLabel(50));
        }

        // ── ② 점수 분해 ───────────────────────────────────────────────────────

        private static PlutoRecommendationCard Card(int market, int rank, string code, int score, int v, int t, int m, int mac, int mom = 0, int devil = 0, int counters = 0, long qty = 10) =>
            new(market, rank, rank, code, "N-" + code, score, v, t, m, mac, new[] { "근거-" + code }, false, 0, "1", "", false, qty, counters,
                counters == 0 ? "반대 논리 없음" : "단기 과매수")
            { LearnMomentumPts = mom, LearnDevilPts = devil, LearnText = "" };

        [Fact]
        public void Score_Breakdown_Decomposes_Base_Plus_Momentum_Minus_Devil()
        {
            var sb = PlutoScoreBreakdown.Of(Card(0, 1, "005930", 78, 20, 25, 15, 10, mom: 12, devil: 4));
            Assert.Equal((70, 12, 4, 78, false), (sb.Base, sb.Momentum, sb.Devil, sb.Final, sb.Saturated));
            Assert.Equal("기본 70 + 모멘텀 +12 − 변호인 4 = 78", sb.Text);
            var sat = PlutoScoreBreakdown.Of(Card(0, 1, "000660", 100, 25, 25, 25, 20, mom: 10));
            Assert.True(sat.Saturated);                                            // 95 + 10 → 100 포화
            Assert.EndsWith("(0~100 포화)", sat.Text);
            var neg = PlutoScoreBreakdown.Of(Card(1, 1, "AAPL", 52, 15, 15, 15, 15, mom: -8));
            Assert.Equal("기본 60 + 모멘텀 -8 − 변호인 0 = 52", neg.Text);
        }

        // ── ③ 매트릭스 행 ─────────────────────────────────────────────────────

        private static PlutoRecoBoardView Board(bool usConnected = false) => new(true, 7, DateTime.UtcNow,
            new PlutoRecoMarketView(new[] { Card(0, 1, "005930", 81, 20, 25, 20, 10, mom: 6), Card(0, 2, "000660", 66, 20, 20, 16, 10, counters: 1) }, 120, 3, 0, 20, Monitored: 200, Passed: 2),
            new PlutoRecoMarketView(Array.Empty<PlutoRecommendationCard>(), 0, 0, 0, 0, Monitored: 100, Passed: 0))
        {
            Crypto = new PlutoRecoMarketView(new[] { Card(2, 1, "KRW-BTC", 84, 20, 24, 25, 15) }, 120, 0, 0, 10, Monitored: 120, Passed: 1),
            Swarm = new PlutoSwarmView(true, true, 25,
                new PlutoSwarmLaneView(0, true, "KIS", 200, 120, 80, 0, 0, false, false, 120, 0),
                new PlutoSwarmLaneView(1, usConnected, "KIS 미구성", 100, 0, 100, 0, 0, false, false, 0, 0),
                new PlutoSwarmLaneView(2, true, "업비트", 120, 120, 0, 0, 0, false, false, 120, 0)),
        };

        [Fact]
        public void Matrix_All_Tab_Ranks_By_Score_And_Adds_Honest_Awaiting_Rows()
        {
            var rows = PlutoCockpitMatrix.Rows(Board(), wired: true, PlutoCockpitMatrix.All);
            var picks = rows.Where(r => r.Card is not null).ToList();
            Assert.Equal(new[] { "KRW-BTC", "005930", "000660" }, picks.Select(r => r.Card!.Code));
            Assert.Equal(new[] { 1, 2, 3 }, picks.Select(r => r.Rank));
            var wait = rows.Where(r => r.Card is null).ToList();
            Assert.Equal(new[] { 0, 1 }, wait.Select(r => r.Market));             // Upbit 전수 판독 = 대기 행 0
            Assert.Equal(80, wait[0].Awaiting);
            Assert.Equal(100, wait[1].Awaiting);
            Assert.Contains("실피드 연결 대기", wait[1].Note);                       // US 레인 미결선
            Assert.Contains("시세 피드 판독 대기", wait[0].Note);
            Assert.Equal(420, PlutoCockpitMatrix.Monitored(Board(), PlutoCockpitMatrix.All));
        }

        [Fact]
        public void Matrix_Market_Tab_Keeps_Engine_Rank_And_Unwired_Or_PreScan_Is_Empty()
        {
            var krx = PlutoCockpitMatrix.Rows(Board(), true, PlutoRecoMarket.Krx);
            Assert.Equal(new[] { 1, 2 }, krx.Where(r => r.Card is not null).Select(r => r.Rank));
            Assert.Single(krx, r => r.Card is null);
            var us = PlutoCockpitMatrix.Rows(Board(), true, PlutoRecoMarket.Us);
            Assert.Equal(PlutoRecoMarket.Us, Assert.Single(us).Market);            // 통과 0 = 대기 행만(날조 0)
            Assert.Empty(PlutoCockpitMatrix.Rows(Board(), false, PlutoCockpitMatrix.All));
            Assert.Empty(PlutoCockpitMatrix.Rows(PlutoRecoBoardView.None, true, PlutoCockpitMatrix.All));
            Assert.Equal("Upbit", PlutoCockpitMatrix.AssetTag(PlutoRecoMarket.Crypto));
        }

        // ── ④ 페이지 렌더 ─────────────────────────────────────────────────────

        private sealed class FixedLearningPort : IPlutoLearningPort
        {
            public FixedLearningPort(PlutoLearningView v) => Current = v;
            public bool IsWired => true;
            public PlutoLearningView Current { get; }
        }

        private sealed class FixedRecoPort : IPlutoRecommendationPort
        {
            public FixedRecoPort(PlutoRecoBoardView v) => Current = v;
            public bool IsWired => true;
            public PlutoRecoBoardView Current { get; }
        }

        private static (Bunit.TestContext, IRenderedComponent<DualArenaPage>) Render(IPlutoLearningPort learning, IPlutoRecommendationPort reco)
        {
            var ctx = new Bunit.TestContext();
            var krx = new KrxArenaEngine();
            var f = new KrxArenaQuoteFeeder(krx);
            Assert.True(f.SelectSource(KrxArenaFeed.Synthetic));
            ctx.Services.AddSingleton<IKrxArenaPort>(new KrxArenaPortAdapter(krx, f));
            var us = new UsTechArenaEngine();
            var uf = new UsArenaQuoteFeeder(us);
            Assert.True(uf.SelectSource(UsArenaFeed.Synthetic));
            ctx.Services.AddSingleton<IUsArenaPort>(new UsArenaPortAdapter(us, uf));
            ctx.Services.AddSingleton<ICryptoArenaPort>(new CryptoArenaPortAdapter(new UpbitCryptoArenaEngine()));
            ctx.Services.AddSingleton<IArenaPreferencePort>(UnwiredArenaPreferencePort.Instance);
            ctx.Services.AddSingleton(learning);
            ctx.Services.AddSingleton(reco);
            return (ctx, ctx.RenderComponent<DualArenaPage>());
        }

        private static PlutoLearningView Learned() => new(true, PlutoLearningHealth.Live, "2022 긴축", 88, false, 64, 15, 8, 40, 9, 2,
            RegimeName: "2022 긴축 충격", RegimeSimilarityX10: 942, RegimeNote: "2022", RegimeEffect: "방어 편향", LifetimeEpisodes: 1234, PersistState: PlutoLearningPersist.Active,
            Shadow: new PlutoShadowView(PlutoShadowVerdictCode.Passed, 5000, 5420, 900, 800, 30, 5420, 800, 30),
            Tensor: new PlutoTensorView(5, "2022 긴축 충격", 4, new[]
            {
                new PlutoTensorLaneView(0, PlutoTensorSourceCode.Regime, 3, 12, 2, PlutoWarmCode.Ready, 4, 100),
                new PlutoTensorLaneView(1, PlutoTensorSourceCode.Global, 0, 0, 0, PlutoWarmCode.NoData, 0, 0),
                new PlutoTensorLaneView(2, PlutoTensorSourceCode.Baseline, 1, -5, 1, PlutoWarmCode.NoData, 0, 0),
            }));

        [Fact]
        public void Cockpit_Renders_Header_Hud_And_Preserves_Legacy_Selectors()
        {
            var (ctx, cut) = Render(new FixedLearningPort(Learned()), new FixedRecoPort(Board()));
            using var _ = ctx;
            Assert.Single(cut.FindAll(".da-cockpit"));
            Assert.Equal("false", cut.Find("[data-ck-armed]").GetAttribute("data-ck-armed"));
            Assert.Contains("FAIL-CLOSED", cut.Find("[data-ck-armed]").TextContent);
            Assert.Contains("활성", cut.Find("[data-ck-engine]").TextContent);
            Assert.Equal(3, cut.FindAll(".ck-card").Count);                       // HUD · 매트릭스 · 아레나 3단 벤토

            var hud = cut.Find(".hud");
            Assert.Equal("wired", hud.GetAttribute("data-hud"));
            Assert.Equal("942", cut.Find("[data-hud-tile='regime']").GetAttribute("data-hud-sim"));
            Assert.Contains("2022 긴축 충격", cut.Find("[data-hud-tile='regime']").TextContent);
            Assert.Contains("94.2%", cut.Find("[data-hud-tile='regime']").TextContent);
            Assert.Contains("width:94%", cut.Find("[data-hud-tile='regime'] .hud-gauge span").GetAttribute("style"));
            Assert.Equal(PlutoWeightScale.TiltX100(15, 8).ToString(), cut.Find("[data-hud-tile='balance']").GetAttribute("data-hud-tilt"));
            Assert.Contains("+15%", cut.Find("[data-hud-mom]").TextContent);
            Assert.Contains("up", cut.Find("[data-hud-mom]").ClassList);         // 공격(+) = 적색
            Assert.Contains("+8점", cut.Find("[data-hud-strict]").TextContent);
            Assert.Contains("G64", cut.Find("[data-hud-tile='maturity']").TextContent);
            Assert.Contains("1,234", cut.Find("[data-hud-tile='maturity']").TextContent);
            Assert.Contains("+4.2%p", cut.Find("[data-hud-gain]").TextContent);
            Assert.Equal(3, cut.FindAll("[data-hud-lane]").Count);
            Assert.Contains("WarmReady", cut.Find("[data-hud-lane='0']").TextContent);
            Assert.Contains("NoData", cut.Find("[data-hud-lane='1']").TextContent);

            Assert.Single(cut.FindAll(".da-gov-toggle"));                         // 기존 셀렉터 보존(진단 태그 = 접힘 details 안)
            Assert.NotNull(cut.Find("details.da-diag .da-badge.da-learn"));
            Assert.NotNull(cut.Find("details.da-reco-detail .da-reco"));
            Assert.Contains("휴먼 vs AI 대결", cut.Find("h1").TextContent);
        }

        [Fact]
        public void Cockpit_Unwired_Ports_Render_Honest_Empty_Hud_And_Matrix()
        {
            var (ctx, cut) = Render(UnwiredPlutoLearningPort.Instance, UnwiredPlutoRecommendationPort.Instance);
            using var _ = ctx;
            Assert.Equal("unwired", cut.Find(".hud").GetAttribute("data-hud"));
            Assert.Contains("미결선", cut.Find("[data-ck-engine]").TextContent);
            Assert.Contains("학습 엔진 미결선", cut.Find("[data-hud-tile='regime']").TextContent);
            Assert.Equal("-1", cut.Find("[data-hud-tile='regime']").GetAttribute("data-hud-sim"));
            Assert.Contains("width:0%", cut.Find(".hud-gauge.skel span").GetAttribute("style"));
            Assert.Equal("unwired", cut.Find("[data-hud-tile='tensor']").GetAttribute("data-hud-tensor"));
            Assert.Empty(cut.FindAll("[data-hud-mom]"));                          // 가짜 가중치 0
            Assert.Contains("추천 엔진 미결선", cut.Find(".mx-empty").TextContent);
            Assert.Empty(cut.FindAll(".mx-grid"));
        }

        [Fact]
        public void Matrix_Tabs_Breakdown_Awaiting_Rows_And_Load_Render()
        {
            var (ctx, cut) = Render(new FixedLearningPort(Learned()), new FixedRecoPort(Board()));
            using var _ = ctx;
            Assert.Equal("-1", cut.Find(".mx").GetAttribute("data-mx-tab"));
            Assert.Equal(4, cut.FindAll(".mx-tab").Count);
            Assert.Contains("(420)", cut.Find("[data-mx-tab-btn='-1']").TextContent);
            Assert.Contains("(200)", cut.Find("[data-mx-tab-btn='0']").TextContent);
            var first = cut.FindAll(".mx-row[data-mx-code]")[0];
            Assert.Equal("KRW-BTC", first.GetAttribute("data-mx-code"));          // 통합 점수 순위
            var krx = cut.Find(".mx-row[data-mx-code='005930'] .mx-score");
            Assert.Equal("기본 75 + 모멘텀 +6 − 변호인 0 = 81", krx.GetAttribute("title"));
            Assert.Equal("75", krx.GetAttribute("data-mx-base"));
            Assert.Contains("watch", cut.Find(".mx-row[data-mx-code='000660'] .mx-risk").ClassList);

            var waits = cut.FindAll(".mx-await");
            Assert.Equal(2, waits.Count);
            Assert.All(waits, w => Assert.Contains("--", w.QuerySelector(".mx-score")!.TextContent));   // 점수 생성 0
            Assert.All(waits, w => Assert.Contains("Awaiting Market Feed", w.TextContent));
            Assert.Equal("100", cut.Find(".mx-await[data-mx-await='1']").GetAttribute("data-mx-count"));

            cut.Find("[data-mx-tab-btn='1']").Click();
            Assert.Equal("1", cut.Find(".mx").GetAttribute("data-mx-tab"));
            Assert.Empty(cut.FindAll(".mx-row[data-mx-code]"));
            Assert.Single(cut.FindAll(".mx-await"));

            cut.Find("[data-mx-tab-btn='0']").Click();
            cut.Find(".mx-row[data-mx-code='005930'] .mx-load").Click();
            Assert.NotEmpty(cut.Find(".da-reco-msg").TextContent);                // 부모 LoadReco 경유(가상 원장)
        }

        // ── ⑤ RCL 순수성 ──────────────────────────────────────────────────────

        private static string Root()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "StockManager.sln"))) dir = dir.Parent;
            Assert.NotNull(dir);
            return dir!.FullName;
        }

        [Fact]
        public void Cockpit_Components_Stay_Pure_And_Page_Injections_Unchanged()
        {
            string root = Root();
            foreach (var c in new[] { "PlutoCognitiveHud.razor", "PlutoRecoMatrix.razor", "PlutoTensorPanel.razor" })
            {
                string src = File.ReadAllText(Path.Combine(root, "MS2E.UI.Portable", "Components", c));
                Assert.DoesNotContain("@inject", src);
                Assert.DoesNotContain("[Inject]", src);
                Assert.DoesNotContain("StockManager.", src);
            }
            Assert.DoesNotContain("StockManager.Engines", File.ReadAllText(Path.Combine(root, "MS2E.UI.Portable", "Ports", "Trading", "PlutoCockpit.cs")));
            string page = File.ReadAllText(Path.Combine(root, "MS2E.UI.Portable", "Pages", "Trading", "DualArenaPage.razor"));
            Assert.Equal(6, page.Split('\n').Count(l => l.TrimStart().StartsWith("@inject ", StringComparison.Ordinal)));
            Assert.Contains("--bg: #0B0F19; --canvas: #0B0F19; --card: #111827; --card-2: #1F2937;", page);
            Assert.Contains("--up: #E53935; --down: #1E88E5;", page);
        }
    }
}
