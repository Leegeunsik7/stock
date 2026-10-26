// ============================================================================
// PlutoCouncilUiTests — v12.18.88-DEV: 4대 인지 에이전트 위원회 밸런스 위젯 · 추천 매트릭스 Kelly % 열 · 선행 파급 점수 분해(bUnit DOM)
//
//   ① 위젯: 미결선 · 첫 게시 전 = 정직 문구(가짜 가중치 0) · 게시 = 레인 3개 스택 바(합 100%) · 주도 에이전트 · 켈리 셀(산출/표본 부족/음의 엣지) · 비토/파급 문구.
//   ② 매트릭스: Kelly % 열 헤더 · 카드 셀(산출 = % · 표본 부족 = n/20 · 위원회 미결선 = —) · 대기 행 '--' · 점수 분해 선행 파급 삽입.
//   ③ RCL 순수성: 신규 컴포넌트 주입 0 · 엔진 참조 0 · 신규 포트 엔진 참조 0.
// ============================================================================
using System;
using System.IO;
using System.Linq;
using Bunit;
using MS2E.UI.Portable.Components;
using MS2E.UI.Portable.Ports.Trading;
using Xunit;

namespace StockManager.Tests.Cognitive
{
    [Collection("ConcurrencyStress")]
    public sealed class PlutoCouncilUiTests
    {
        private static PlutoCouncilView Live(string veto = "", int lead = 2) => new(true, true, 5, "2022 긴축 충격", new[]
        {
            new PlutoCouncilLaneView(0, 1500, 2000, 2000, 4500, PlutoKellyCode.Sized, 1250, 5800, 140, 64),
            new PlutoCouncilLaneView(1, 4000, 1500, 2500, 2000, PlutoKellyCode.Insufficient, 0, 0, 0, 7),
            new PlutoCouncilLaneView(2, 1500, 1500, 3000, 4000, PlutoKellyCode.NoEdge, 0, 4200, 90, 31),
        }, veto, lead, true, "🌐 선행 파급 +2점: 빅테크 +120bp × 상관 0.42", veto.Length > 0 ? 4 : 0);

        [Fact]
        public void Balance_Widget_Renders_Lanes_Kelly_And_Spillover()
        {
            using var ctx = new TestContext();
            var cut = ctx.RenderComponent<PlutoCouncilBalance>(p => p.Add(x => x.View, Live()));
            Assert.Equal("live", cut.Find(".cc").GetAttribute("data-cc"));
            Assert.Equal("off", cut.Find(".cc").GetAttribute("data-cc-veto"));
            Assert.Contains("2022 긴축 충격", cut.Find(".cc-head").TextContent);
            var lanes = cut.FindAll("[data-cc-lane]");
            Assert.Equal(3, lanes.Count);
            Assert.All(lanes, l => Assert.Equal("10000", l.GetAttribute("data-cc-sum")));
            Assert.Equal("3", cut.Find("[data-cc-lane='0']").GetAttribute("data-cc-lead"));            // KRX = 위험 통제 주도
            Assert.Contains("위험 통제 45%", cut.Find("[data-cc-lane='0']").TextContent);
            Assert.Contains("width:45%", cut.FindAll("[data-cc-lane='0'] .cc-stack i")[3].GetAttribute("style"));
            Assert.Contains("권장 비중: 12.5%", cut.Find("[data-cc-lane='0']").TextContent);
            Assert.Contains("권장 비중: 0% (관망)", cut.Find("[data-cc-lane='1']").TextContent);         // [v12.18.89-DEV] 표본 부족 = 관망(표본 수 = 툴팁)
            Assert.Contains("7/20", cut.Find("[data-cc-lane='1'] .hud-tag").GetAttribute("title"));
            Assert.Contains("권장 비중: 0% (진입 비권장)", cut.Find("[data-cc-lane='2']").TextContent);  // 음의 엣지
            Assert.Contains("거부권 미발동", cut.Find(".cc-foot").TextContent);
            Assert.Equal("2", cut.Find("[data-cc-leadpts]").GetAttribute("data-cc-leadpts"));
            Assert.Contains("up", cut.Find("[data-cc-leadpts]").ClassList);                            // 상승 = 적색
            Assert.Contains("+2점", cut.Find("[data-cc-leadpts]").TextContent);
        }

        [Fact]
        public void Balance_Widget_Shows_Veto_And_Honest_Empty_States()
        {
            using var ctx = new TestContext();
            var veto = ctx.RenderComponent<PlutoCouncilBalance>(p => p.Add(x => x.View, Live("⛔ 리스크 통제관: VIX ≥ 35", -3)));
            Assert.Equal("on", veto.Find(".cc").GetAttribute("data-cc-veto"));
            Assert.Contains("VIX", veto.Find("[data-cc-veto-text]").TextContent);
            Assert.Contains("비토 탈락 4종목", veto.Find(".cc-foot").TextContent);
            Assert.Contains("down", veto.Find("[data-cc-leadpts]").ClassList);

            var none = ctx.RenderComponent<PlutoCouncilBalance>(p => p.Add(x => x.View, PlutoCouncilView.None));
            Assert.Equal("unwired", none.Find(".cc").GetAttribute("data-cc"));
            Assert.Contains("위원회 미결선", none.Find(".cc-empty").TextContent);
            Assert.Empty(none.FindAll("[data-cc-lane]"));

            var pending = ctx.RenderComponent<PlutoCouncilBalance>(p => p.Add(x => x.View, PlutoCouncilView.None with { Wired = true }));
            Assert.Equal("pending", pending.Find(".cc").GetAttribute("data-cc"));
            Assert.Empty(pending.FindAll("[data-cc-lane]"));                                           // 가짜 가중치 0
        }

        private static PlutoRecommendationCard Card(string code, int score, int lead, bool council, int kellyState, int kellyBp, int samples) =>
            new(PlutoRecoMarket.Krx, 1, 1, code, "N-" + code, score, 20, 20, 20, 10, new[] { "근거" }, false, 0, "1", "", false, 10, 0, "반대 논리 없음")
            { LeadPts = lead, CouncilWired = council, KellyState = kellyState, KellyBp = kellyBp, KellySamples = samples, KellyText = council ? "켈리 근거" : "" };

        [Fact]
        public void Matrix_Kelly_Column_And_Lead_Breakdown()
        {
            var cards = new[] { Card("005930", 73, 3, true, PlutoKellyCode.Sized, 1667, 40), Card("000660", 70, 0, true, PlutoKellyCode.Insufficient, 0, 5), Card("035420", 70, 0, false, 0, 0, 0) };
            var board = new PlutoRecoBoardView(true, 1, DateTime.UtcNow, new PlutoRecoMarketView(cards, 3, 0, 0, 0, 200, 3), PlutoRecoMarketView.None);
            using var ctx = new TestContext();
            var cut = ctx.RenderComponent<PlutoRecoMatrix>(p => p.Add(x => x.Board, board).Add(x => x.Wired, true).Add(x => x.Tab, PlutoRecoMarket.Krx));
            Assert.Contains("권장 비중", cut.Find(".mx-grid thead").TextContent);
            Assert.Equal("16.7%", cut.Find(".mx-row[data-mx-code='005930'] .mx-kelly").TextContent.Trim());
            Assert.Equal("1667", cut.Find(".mx-row[data-mx-code='005930'] .mx-kelly").GetAttribute("data-mx-kelly-bp"));
            Assert.Equal("0% (관망)", cut.Find(".mx-row[data-mx-code='000660'] .mx-kelly").TextContent.Trim());
            Assert.StartsWith("표본 부족 5/20", cut.Find(".mx-row[data-mx-code='000660'] .mx-kelly").GetAttribute("title"));
            Assert.Equal("—", cut.Find(".mx-row[data-mx-code='035420'] .mx-kelly").TextContent.Trim());
            Assert.Equal("기본 70 + 모멘텀 +0 − 변호인 0 + 선행 +3 = 73", cut.Find(".mx-row[data-mx-code='005930'] .mx-score").GetAttribute("title"));
            Assert.Equal("3", cut.Find(".mx-row[data-mx-code='005930'] [data-mx-lead]").GetAttribute("data-mx-lead"));
            Assert.Empty(cut.FindAll(".mx-row[data-mx-code='000660'] [data-mx-lead]"));
            var await = cut.Find(".mx-await");
            Assert.Equal(cut.FindAll(".mx-row[data-mx-code='005930'] td").Count, await.QuerySelectorAll("td").Length);   // 열 수 일치
            Assert.False(PlutoScoreBreakdown.Of(cards[0]).Saturated);
        }

        [Fact]
        public void Council_Ui_Stays_Pure()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "StockManager.sln"))) dir = dir.Parent;
            Assert.NotNull(dir);
            string comp = File.ReadAllText(Path.Combine(dir!.FullName, "MS2E.UI.Portable", "Components", "PlutoCouncilBalance.razor"));
            Assert.DoesNotContain("@inject", comp);
            Assert.DoesNotContain("[Inject]", comp);
            Assert.DoesNotContain("StockManager.", comp);
            Assert.DoesNotContain("StockManager.Engines", File.ReadAllText(Path.Combine(dir.FullName, "MS2E.UI.Portable", "Ports", "Trading", "PlutoCouncilPort.cs")));
            Assert.Equal(PlutoKellyCode.MinSamples, StockManager.Engines.Cognitive.Council.PlutoDynamicKellyEngine.MinSamples);   // 포트 미러 드리프트 = 실패
            Assert.Equal(PlutoKellyCode.Sized, (int)StockManager.Engines.Cognitive.Council.PlutoKellyState.Sized);
            Assert.Equal(PlutoKellyCode.Insufficient, (int)StockManager.Engines.Cognitive.Council.PlutoKellyState.Insufficient);
            Assert.Equal(PlutoKellyCode.NoEdge, (int)StockManager.Engines.Cognitive.Council.PlutoKellyState.NoEdge);
        }
    }
}
