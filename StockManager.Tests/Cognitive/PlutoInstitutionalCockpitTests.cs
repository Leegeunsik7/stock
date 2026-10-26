// ============================================================================
// PlutoInstitutionalCockpitTests — v12.18.90-DEV: DualArena 기관용 인지 콕핏 · 리스크 비토 차단 쇼케이스
//
//   ① 엔진: Rank 비토 차단 목록 = 리스크 통제관(실측 VIX 인용 사유) · 악마의 변호인(반대 논리 목록) · 판독 전무/50점 미만 = 목록 제외 · 상한/정렬 결정적.
//   ② 스냅샷 · 어댑터: Vetoes 게시 → PlutoVetoCard 투영(시장 · 주체 · 사유) · 시장별 리스크 통제관 비토 수 · 차단 합계 · 체결 핫패스 TryFind δ=0 불변.
//   ③ 표시 모델: 브리핑 2번째 줄(분석 현황 · 국면 연상 · 비토 실적) · 위원회 평균 발언권/주도 · 웜스타트 집계 · 켈리 뱃지/행동 가이드.
//   ④ 렌더: HUD 3대 인지 카드 · 매트릭스 [💎 AI 엄선 추천]/[🛡️ 리스크 비토 차단 목록 (N)] 탭 · 미결선/첫 스캔 전 = 가짜 차단 0.
//   ⑤ RCL 순수성: 신규 표시 모델 엔진 참조 0 · 시계 판독 0.
// ============================================================================
using System;
using System.IO;
using System.Linq;
using Bunit;
using MS2E.UI.Portable.Components;
using MS2E.UI.Portable.Ports.Trading;
using StockManager.Adapters;
using StockManager.Engines.Arena.Domestic;
using StockManager.Engines.Cognitive.Council;
using StockManager.Engines.Cognitive.Learning;
using StockManager.Engines.Cognitive.Microstructure;
using StockManager.Engines.Cognitive.Screening;
using StockManager.Engines.Cognitive.Technical;
using StockManager.Engines.Cognitive.Valuation;
using MarketRegime = StockManager.Engines.Cognitive.Regime.MarketRegime;   // MS2E.Core.MarketRegime(전역 using) 동명 타입과 구분
using Xunit;
using static StockManager.Tests.ZeroAllocProbe;

namespace StockManager.Tests.Cognitive
{
    [Collection("ConcurrencyStress")]
    public sealed class PlutoInstitutionalCockpitTests
    {
        private const long T0 = 638_000_000_000_000_000;

        private static PlutoScreenInputs In(long sym, int? val, int? tech, int? micro, MarketRegime regime = MarketRegime.TrendingBull,
            PlutoScreenMarket market = PlutoScreenMarket.Krx, long price = 50_000, bool depthTrap = false)
            => new(market, sym, price,
                val.HasValue, val ?? 0, val.HasValue ? 2000 : 0, val.HasValue ? PlutoValuationGrade.FairDiscount : PlutoValuationGrade.Unknown, val.HasValue ? 62_500 : 0, val.HasValue,
                tech.HasValue, tech ?? 0, tech.HasValue ? PlutoSqueezePhase.BreakoutUp : default, tech.HasValue ? PlutoProfileZone.PocSupport : default,
                micro.HasValue, micro ?? 0, PlutoDivergenceType.None, micro.HasValue, false, micro.HasValue && depthTrap,
                regime);

        private static PlutoCouncilInputs Inputs(in PlutoCouncilMacro m) => new(in m, 0, default, default, false, false, null);

        /// <summary>정상 3종 + 반대 논리 2건 중첩 1종(패닉 투매 국면 + 15호가 함정) + 판독 전무 1종.</summary>
        private static readonly PlutoScreenInputs[] s_universe =
        {
            In(5930, 60, 40, 30), In(660, 40, 60, 20), In(3550, 20, 30, 50),
            In(7, 60, 40, 30, MarketRegime.PanicLiquidation, depthTrap: true), In(9999, null, null, null),
        };

        // ── ① 엔진 비토 차단 목록 ─────────────────────────────────────────────

        [Fact]
        public void Rank_Collects_Council_And_Devil_Vetoes_With_Measured_Reasons()
        {
            var devil = PlutoDiscoveryScorer.Score(in s_universe[3], default);
            Assert.True(devil.Vetoed);                                                     // 전제: 반대 논리 ≥ 2

            var m = new PlutoCouncilMacro(3820, PlutoEpisodeKind.Unknown, 0, default);   // VIX 38.2
            var picks = PlutoRecommendationEngine.Rank(s_universe, PlutoScreenMarket.Krx, default, default, KrxArenaEngine.DefaultCapitalWon, 200, 5,
                Inputs(in m), out var t, out var vetoes);
            Assert.Empty(picks);
            Assert.Equal((1, 3, 1), (t.Vetoed, t.CouncilVetoed, t.Insufficient));
            Assert.Equal(4, vetoes.Length);                                                // 판독 전무 = 차단 목록 제외(날조 0)
            Assert.DoesNotContain(vetoes, v => v.Symbol == 9999);
            var cro = vetoes.Where(v => v.Source == PlutoVetoSource.Council).ToArray();
            Assert.Equal(3, cro.Length);
            Assert.All(cro, v => Assert.Equal("VIX 38.2 (35 초과) — 패닉 투매 위험 차단", v.ReasonText));
            Assert.All(cro, v => Assert.Equal(PlutoVetoReason.VixSpike, v.CouncilVeto));
            var dv = vetoes.Single(v => v.Source == PlutoVetoSource.DevilsAdvocate);
            Assert.Equal(7, dv.Symbol);
            Assert.Equal(PlutoVetoReason.None, dv.CouncilVeto);
            Assert.StartsWith("반대 논리 2건 중첩 — ", dv.ReasonText);
            Assert.Contains("15호가 매도벽/허매수 함정", dv.ReasonText);
            Assert.EndsWith("→ 진입 차단", dv.ReasonText);
            for (int i = 1; i < vetoes.Length; i++)                                        // 차단 전 기본 점수 내림차순 → 종목 오름차순
                Assert.True(vetoes[i - 1].BaseScore > vetoes[i].BaseScore || (vetoes[i - 1].BaseScore == vetoes[i].BaseScore && vetoes[i - 1].Symbol < vetoes[i].Symbol));
            Assert.Equal("005930", vetoes.Single(v => v.Symbol == 5930).Code);
        }

        [Fact]
        public void Rank_Without_Veto_Leaves_List_Empty_And_Legacy_Overload_Unchanged()
        {
            var clean = s_universe.Take(3).ToArray();
            var a = PlutoRecommendationEngine.Rank(clean, PlutoScreenMarket.Krx, default, default, KrxArenaEngine.DefaultCapitalWon, 200, 5, default, out var t1, out var vetoes);
            var b = PlutoRecommendationEngine.Rank(clean, PlutoScreenMarket.Krx, default, default, KrxArenaEngine.DefaultCapitalWon, 200, 5, out var t2);
            Assert.Empty(vetoes);
            Assert.Equal(t1, t2);
            Assert.Equal(a.Select(r => (r.Symbol, r.FinalScore)), b.Select(r => (r.Symbol, r.FinalScore)));
        }

        [Fact]
        public void Rank_Caps_Veto_List_But_Tally_Counts_All()
        {
            var many = Enumerable.Range(1, 25).Select(i => In(100 + i, 60, 40, 30)).ToArray();
            var m = new PlutoCouncilMacro(4000, PlutoEpisodeKind.Unknown, 0, default);
            PlutoRecommendationEngine.Rank(many, PlutoScreenMarket.Krx, default, default, KrxArenaEngine.DefaultCapitalWon, 200, 5, Inputs(in m), out var t, out var vetoes);
            Assert.Equal(25, t.CouncilVetoed);
            Assert.Equal(PlutoRecommendationSnapshot.MaxVetoesPerMarket, vetoes.Length);
            Assert.Equal(Enumerable.Range(101, 10).Select(i => (long)i), vetoes.Select(v => v.Symbol));   // 동점 = 종목 오름차순(결정적)
        }

        [Theory]
        [InlineData(PlutoVetoReason.PanicRegimeMatch, "과거 패닉 국면 유사도 72% — 위기 재현 위험 차단")]
        [InlineData(PlutoVetoReason.PanicLiquidation, "종목 패닉 투매 국면 — 떨어지는 칼날 진입 차단")]
        [InlineData(PlutoVetoReason.Unreadable, "판정 보드 판독 불가 — 안전 차단(fail-closed)")]
        public void Veto_Detail_Quotes_Measured_Values(PlutoVetoReason v, string expected)
        {
            var m = new PlutoCouncilMacro(-1, PlutoEpisodeKind.Panic, 72, default);
            Assert.Equal(expected, PlutoCognitiveCouncil.VetoDetail(v, in m));
            Assert.Equal("", PlutoCognitiveCouncil.VetoDetail(PlutoVetoReason.None, in m));
        }

        [Fact]
        public void Veto_Detail_Spillover_Lists_Each_Shock()
        {
            var shock = new PlutoSpilloverSignal(PlutoSpilloverField.Us10y | PlutoSpilloverField.BigTech, PlutoSpilloverShock.Us10yJump | PlutoSpilloverShock.BigTechCrash,
                31, -340, 0, 0, 0, 0, -3, 1, T0);
            var m = new PlutoCouncilMacro(-1, PlutoEpisodeKind.Unknown, 0, in shock);
            Assert.Equal("미 10년물 금리 +31bp 급변 — 거시 충격 차단 · 미 빅테크 -3.40% 급락 — 교차 자산 역풍 차단",
                PlutoCognitiveCouncil.VetoDetail(PlutoVetoReason.SpilloverShock, in m));
            var vixUnread = PlutoCouncilMacro.None;                                        // 수치 미판독 = 사유만(가짜 수치 0)
            Assert.Equal("VIX 35 초과 — 패닉 투매 위험 차단", PlutoCognitiveCouncil.VetoDetail(PlutoVetoReason.VixSpike, in vixUnread));
        }

        // ── ② 스냅샷 · 어댑터 · δ=0 ──────────────────────────────────────────

        private static PlutoRecommendationSnapshot VetoSnapshot()
        {
            var m = new PlutoCouncilMacro(3820, PlutoEpisodeKind.Unknown, 0, default);
            var picks = PlutoRecommendationEngine.Rank(s_universe.Take(2).ToArray(), PlutoScreenMarket.Krx, default, default, KrxArenaEngine.DefaultCapitalWon, 200, 5, default, out var kt, out _);
            PlutoRecommendationEngine.Rank(s_universe, PlutoScreenMarket.Krx, default, default, KrxArenaEngine.DefaultCapitalWon, 200, 5, Inputs(in m), out var vt, out var vetoes);
            return new PlutoRecommendationSnapshot(3, T0, picks, kt with { CouncilVetoed = vt.CouncilVetoed, Vetoed = vt.Vetoed }, Array.Empty<PlutoRecommendation>(), default,
                Array.Empty<PlutoRecommendation>(), default) { Vetoes = vetoes };
        }

        [Fact]
        public void Adapter_Maps_Vetoes_Council_Counts_And_Totals()
        {
            var snap = VetoSnapshot();
            var view = PlutoRecommendationPortAdapter.Map(snap, true);
            Assert.Equal(4, view.Vetoes.Count);
            Assert.Equal(3, view.Krx.CouncilVetoed);
            Assert.Equal(4, view.Krx.BlockedTotal);
            Assert.Equal(4, view.TotalBlocked);
            var samsung = view.Vetoes.Single(v => v.Code == "005930");
            Assert.Equal(PlutoRecoMarket.Krx, samsung.Market);
            Assert.Equal(PlutoVetoSourceCode.Council, samsung.Source);
            Assert.Equal("50,000원", samsung.PriceText);
            Assert.NotEqual("005930", samsung.Name);                                     // 종목명 해석(미등록이면 코드 — 삼성전자 = 등록)
            Assert.Equal(PlutoVetoSourceCode.Devil, view.Vetoes.Single(v => v.Code == "000007").Source);
            Assert.Empty(PlutoRecommendationPortAdapter.Map(PlutoRecommendationSnapshot.Empty, false).Vetoes);
        }

        [Fact]
        public void Hot_Path_TryFind_Stays_Zero_Alloc_With_Vetoes_Published()
        {
            var board = new PlutoRecommendationBoard();
            board.Publish(VetoSnapshot());
            Assert.True(board.TryFind(PlutoScreenMarket.Krx, 5930, out _, out _));
            long bytes = MinAllocated(() =>
            {
                for (int i = 0; i < 1000; i++) board.TryFind(PlutoScreenMarket.Krx, i % 2 == 0 ? 5930 : 7, out _, out _);
            });
            Assert.Equal(0, bytes);
        }

        // ── ③ 표시 모델 ───────────────────────────────────────────────────────

        private static PlutoLearningView Learned(int momentum = 0, int strict = 12) => new(true, PlutoLearningHealth.Live, "2022 긴축", 88, false, 64, momentum, strict, 40, 9, 2,
            RegimeName: "2022 연준 금리인상", RegimeSimilarityX10: 942, RegimeEffect: "방어 편향", LifetimeEpisodes: 1234, PersistState: PlutoLearningPersist.Active,
            Shadow: new PlutoShadowView(PlutoShadowVerdictCode.Passed, 5000, 5420, 900, 780, 30, 5420, 780, 30),
            Tensor: new PlutoTensorView(5, "2022 연준 금리인상", 4, new[]
            {
                new PlutoTensorLaneView(0, PlutoTensorSourceCode.Regime, 3, 0, 12, PlutoWarmCode.Ready, 4, 100),
                new PlutoTensorLaneView(1, PlutoTensorSourceCode.Global, 0, 0, 0, PlutoWarmCode.Ready, 0, 0),
                new PlutoTensorLaneView(2, PlutoTensorSourceCode.Baseline, 1, 0, 0, PlutoWarmCode.NoData, 0, 0),
            }));

        private static PlutoCouncilView Council(string veto = "") => new(true, true, 5, "2022 연준 금리인상", new[]
        {
            new PlutoCouncilLaneView(0, 1500, 2000, 2000, 4500, PlutoKellyCode.Sized, 1250, 5800, 140, 64),
            new PlutoCouncilLaneView(1, 4000, 1500, 2500, 2000, PlutoKellyCode.Insufficient, 0, 0, 0, 7),
            new PlutoCouncilLaneView(2, 1500, 1500, 3000, 4000, PlutoKellyCode.NoEdge, 0, 4200, 90, 31),
        }, veto, 0, false, "", veto.Length > 0 ? 3 : 0);

        private static PlutoRecommendationCard Card(string code, int score, int kelly, int kellyBp) =>
            new(PlutoRecoMarket.Krx, 1, 1, code, "N-" + code, score, 20, 20, 20, 10, new[] { "근거" }, false, 0, "1", "", false, 10, 0, "반대 논리 없음")
            { CouncilWired = kelly != 0, KellyState = kelly, KellyBp = kellyBp };

        private static PlutoRecoBoardView Board(bool council = true) => new(true, 4, DateTime.UtcNow,
            new PlutoRecoMarketView(new[] { Card("005930", 81, PlutoKellyCode.Sized, 1250) }, 190, 2, 0, 20, 200, 1) { CouncilVetoed = council ? 3 : 0 },
            new PlutoRecoMarketView(Array.Empty<PlutoRecommendationCard>(), 90, 1, 0, 89, 100, 0))
        {
            Crypto = new PlutoRecoMarketView(Array.Empty<PlutoRecommendationCard>(), 117, 0, 0, 117, 120, 0),
            Council = council ? Council("⛔ 리스크 통제관 비토: VIX ≥ 35") : PlutoCouncilView.None,
            Vetoes = new[]
            {
                new PlutoVetoCard(PlutoRecoMarket.Krx, "000660", "SK하이닉스", 78, PlutoVetoSourceCode.Council, "VIX 38.2 (35 초과) — 패닉 투매 위험 차단", "190,000원"),
                new PlutoVetoCard(PlutoRecoMarket.Us, "NVDA", "엔비디아", 70, PlutoVetoSourceCode.Devil, "반대 논리 2건 중첩 — 고평가 경계 · CVD 약세 → 진입 차단", "$120.00"),
            },
        };

        [Fact]
        public void Insight_Composes_Analysis_Regime_Stance_And_Veto_Record()
        {
            var lv = Learned();
            string s = PlutoHudInsight.Compose(true, in lv, true, Board());
            Assert.StartsWith("📊 3대 시장 420종 전수 분석: 시세 판독 397종 중 1종이 추천 기준을 통과했습니다.", s);
            Assert.Contains("과거 '2022 연준 금리인상' 국면과 94.2% 닮았습니다 — 🛡️ 방어 태세로 대응합니다.", s);
            Assert.Contains("🛡️ 리스크 통제관 거부권 발동: VIX 38.2 (35 초과) — 패닉 투매 위험 차단 → 위험 종목 6종 진입 차단.", s);

            string calm = PlutoHudInsight.Compose(true, in lv, true, Board(council: false) with { Vetoes = Array.Empty<PlutoVetoCard>() });
            Assert.Contains("🛡️ 위험 신호가 겹친 3종을 선제 차단해 자본을 보존했습니다.", calm);

            var none = default(PlutoLearningView);
            Assert.Equal("📊 추천 엔진 미결선 — 종목 분석을 수행하지 않는 호스트입니다.", PlutoHudInsight.Compose(false, in none, false, PlutoRecoBoardView.None));
            string first = PlutoHudInsight.Compose(true, in none, true, PlutoRecoBoardView.None with { Krx = PlutoRecoMarketView.None with { Monitored = 200 } });
            Assert.Contains("200종 첫 전수 분석을 기다리고 있습니다.", first);
            Assert.Contains("12대 역사적 국면 판정 대기", first);
            Assert.DoesNotContain("차단", first);                                         // 첫 스캔 전 = 차단 실적 문구 0
        }

        [Fact]
        public void Stance_Council_Summary_Warm_And_Kelly_Badges()
        {
            Assert.Equal("🛡️ 방어 태세", PlutoHudInsight.Stance(-15));
            Assert.Equal("⚔️ 공격 태세", PlutoHudInsight.Stance(15));
            Assert.Equal("⚖️ 균형 태세", PlutoHudInsight.Stance(14));

            var cs = PlutoCouncilSummary.Of(Council());
            Assert.True(cs.Has);
            Assert.Equal((2333, 1666, 2500, 3500), (cs.TrendBp, cs.ReversionBp, cs.LiquidityBp, cs.RiskBp));
            Assert.Equal(PlutoCouncilAgentCode.Risk, cs.Lead);
            Assert.False(PlutoCouncilSummary.Of(PlutoCouncilView.None).Has);
            Assert.False(PlutoCouncilSummary.Of(Council() with { Published = false }).Has);   // 첫 게시 전 = 가짜 가중치 0

            Assert.Equal("웜스타트 2/3 완료", PlutoHudInsight.WarmText(Learned().Tensor, out int r, out int t));
            Assert.Equal((2, 3), (r, t));
            Assert.Equal("웜스타트 미결선", PlutoHudInsight.WarmText(null, out _, out _));

            Assert.Equal("권장 비중: 켈리 12.5%", PlutoKellyCode.Badge(PlutoKellyCode.Sized, 1250));
            Assert.Equal("권장 비중: 0% (관망)", PlutoKellyCode.Badge(PlutoKellyCode.Insufficient, 0));
            Assert.Equal("권장 비중: —", PlutoKellyCode.Badge(PlutoKellyCode.Unwired, 0));
            Assert.Equal("🔍 관측", PlutoKellyCode.Action(PlutoKellyCode.Sized));
            Assert.Equal("👀 관망", PlutoKellyCode.Action(PlutoKellyCode.NoEdge));
            Assert.Equal("+4.2%p", PlutoHudInsight.PctP(420));
            Assert.Equal("−1.2%p", PlutoHudInsight.PctP(-120));
        }

        // ── ④ 렌더 ────────────────────────────────────────────────────────────

        [Fact]
        public void Hud_Renders_Three_Cognitive_Cards_And_Insight()
        {
            using var ctx = new TestContext();
            var lv = Learned();
            var cut = ctx.RenderComponent<PlutoCognitiveHud>(p => p.Add(x => x.Wired, true).Add(x => x.View, lv).Add(x => x.Briefing, "브리핑")
                .Add(x => x.Insight, "📊 분석").Add(x => x.Council, Council("⛔ 리스크 통제관 비토: VIX ≥ 35")));
            Assert.Equal(3, cut.FindAll(".hud > .hud-tile").Count);
            Assert.Equal("📊 분석", cut.Find("[data-hud-insight]").TextContent);
            Assert.Contains("과거 기억 연상", cut.Find("[data-hud-tile='regime']").TextContent);
            Assert.Equal("def", cut.Find("[data-hud-stance]").GetAttribute("data-hud-stance"));
            Assert.Contains("방어 태세", cut.Find("[data-hud-stance]").TextContent);

            var council = cut.Find("[data-hud-tile='council']");
            Assert.Equal("live", council.GetAttribute("data-hud-council"));
            Assert.Equal("3", council.GetAttribute("data-hud-lead"));
            Assert.Contains("주도: 위험 통제 에이전트", council.TextContent);
            Assert.Equal(4, cut.FindAll("[data-hud-agent]").Count);
            Assert.Equal("on", cut.Find("[data-hud-veto]").GetAttribute("data-hud-veto"));

            var evo = cut.Find("[data-hud-tile='maturity']");
            Assert.Contains("자가 진화 성적표", evo.TextContent);
            Assert.Contains("64세대", evo.TextContent);
            Assert.Equal("+4.2%p", cut.Find("[data-hud-gain]").TextContent.Trim());
            Assert.Equal("−1.2%p", cut.Find("[data-hud-mdd]").TextContent.Trim());          // 낙폭 900 → 780bp = 1.2%p 축소
            Assert.Equal("2/3", cut.Find("[data-hud-warm-all]").GetAttribute("data-hud-warm-all"));
            Assert.NotNull(cut.Find("details.hud-more [data-hud-tile='balance']"));          // 세부 계기 = 접힘
            Assert.NotNull(cut.Find("details.hud-more [data-hud-tile='tensor']"));
        }

        [Fact]
        public void Hud_Unwired_Council_And_Pending_Render_Honest_Empty()
        {
            using var ctx = new TestContext();
            var none = ctx.RenderComponent<PlutoCognitiveHud>(p => p.Add(x => x.Wired, false).Add(x => x.View, default(PlutoLearningView)));
            Assert.Equal("unwired", none.Find("[data-hud-tile='council']").GetAttribute("data-hud-council"));
            Assert.Contains("위원회 미결선", none.Find("[data-hud-tile='council']").TextContent);
            Assert.Empty(none.FindAll("[data-hud-agent]"));
            Assert.Empty(none.FindAll("[data-hud-gain]"));
            Assert.Empty(none.FindAll("[data-hud-insight]"));

            var lv = Learned() with { Shadow = default };
            var pending = ctx.RenderComponent<PlutoCognitiveHud>(p => p.Add(x => x.Wired, true).Add(x => x.View, lv).Add(x => x.Council, Council() with { Published = false }));
            Assert.Equal("pending", pending.Find("[data-hud-tile='council']").GetAttribute("data-hud-council"));
            Assert.Empty(pending.FindAll("[data-hud-agent]"));
            Assert.Empty(pending.FindAll("[data-hud-mdd]"));                                 // 비교 전 = 수치 날조 0
            Assert.Contains("비교 전", pending.Find("[data-hud-tile='maturity']").TextContent);
        }

        [Fact]
        public void Matrix_Veto_Tab_Shows_Blocked_Symbols_And_Reasons()
        {
            using var ctx = new TestContext();
            int view = -1;
            var cut = ctx.RenderComponent<PlutoRecoMatrix>(p => p.Add(x => x.Board, Board()).Add(x => x.Wired, true).Add(x => x.OnView, v => view = v));
            Assert.Equal("picks", cut.Find(".mx").GetAttribute("data-mx-view"));
            Assert.Contains("(6)", cut.Find("[data-mx-view-btn='veto']").TextContent);       // 차단 전수 = 2 + 3 + 1
            Assert.Contains("권장 비중: 켈리 12.5%", cut.Find(".mx-row[data-mx-code='005930'] .mx-score").TextContent);
            cut.Find("[data-mx-view-btn='veto']").Click();
            Assert.Equal(PlutoRecoMatrix.VetoView, view);

            cut.SetParametersAndRender(p => p.Add(x => x.View, PlutoRecoMatrix.VetoView));
            Assert.Equal("veto", cut.Find(".mx").GetAttribute("data-mx-view"));
            Assert.Equal("6", cut.Find("[data-mx-veto-total]").GetAttribute("data-mx-veto-total"));
            Assert.Equal(2, cut.FindAll("[data-mx-veto-code]").Count);
            var hynix = cut.Find("[data-mx-veto-code='000660']");
            Assert.Equal("cro", hynix.GetAttribute("data-mx-veto-src"));
            Assert.Contains("VIX 38.2 (35 초과) — 패닉 투매 위험 차단", hynix.QuerySelector("[data-mx-veto-reason]")!.TextContent);
            Assert.Equal("78", hynix.QuerySelector("[data-mx-veto-score]")!.GetAttribute("data-mx-veto-score"));
            Assert.Empty(cut.FindAll(".mx-row[data-mx-code]"));                              // 추천 행 미혼입

            cut.SetParametersAndRender(p => p.Add(x => x.Tab, PlutoRecoMarket.Us));
            Assert.Single(cut.FindAll("[data-mx-veto-code]"));
            Assert.Equal("devil", cut.Find("[data-mx-veto-code='NVDA']").GetAttribute("data-mx-veto-src"));
            cut.SetParametersAndRender(p => p.Add(x => x.Tab, PlutoRecoMarket.Crypto));
            Assert.Empty(cut.FindAll("[data-mx-veto-code]"));
            Assert.Contains("차단된 종목이 없습니다", cut.Find(".mx-empty").TextContent);
        }

        [Fact]
        public void Matrix_Veto_Tab_Unwired_And_First_Scan_Fabricate_Nothing()
        {
            using var ctx = new TestContext();
            var unwired = ctx.RenderComponent<PlutoRecoMatrix>(p => p.Add(x => x.Board, Board()).Add(x => x.Wired, false).Add(x => x.View, PlutoRecoMatrix.VetoView));
            Assert.Empty(unwired.FindAll("[data-mx-veto-code]"));
            Assert.Contains("추천 엔진 미결선", unwired.Find(".mx-empty").TextContent);
            var first = ctx.RenderComponent<PlutoRecoMatrix>(p => p.Add(x => x.Board, PlutoRecoBoardView.None).Add(x => x.Wired, true).Add(x => x.View, PlutoRecoMatrix.VetoView));
            Assert.Contains("(0)", first.Find("[data-mx-view-btn='veto']").TextContent);
            Assert.Empty(first.FindAll("[data-mx-veto-code]"));
            Assert.Empty(PlutoVetoMatrix.Rows(Board(), false, PlutoCockpitMatrix.All));
        }

        // ── ⑤ RCL 순수성 ─────────────────────────────────────────────────────

        [Fact]
        public void Institutional_Hud_Model_Stays_Pure()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "StockManager.sln"))) dir = dir.Parent;
            Assert.NotNull(dir);
            string src = File.ReadAllText(Path.Combine(dir!.FullName, "MS2E.UI.Portable", "Ports", "Trading", "PlutoInstitutionalHud.cs"));
            Assert.DoesNotContain("StockManager.", src);
            Assert.DoesNotContain("DateTime.Now", src);
            Assert.DoesNotContain("DateTime.UtcNow", src);
            foreach (var c in new[] { "PlutoCognitiveHud.razor", "PlutoRecoMatrix.razor" })
            {
                string razor = File.ReadAllText(Path.Combine(dir.FullName, "MS2E.UI.Portable", "Components", c));
                Assert.DoesNotContain("@inject", razor);
                Assert.DoesNotContain("StockManager.", razor);
            }
        }
    }
}
