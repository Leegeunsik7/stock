// ============================================================================
// PlutoBriefingRecommenderTests — v12.18.85-DEV: 플루토 인지 학습 브리핑 · 레짐 적응형 Top-Picks 추천 엔진
//   ① 투영 산식: 중립 = 조정 0(기존 점수 보존) · 모멘텀 tilt = (기술 − 12) × tilt ÷ 100 · 엄격도 = 엄격도 × 반대 논리 ÷ 2 · 무결성 위반 = 미적용.
//   ② 랭킹: 학습 가중치가 순위를 바꿈(모멘텀 강화 ↔ 축소) · 판독 전무(피드 미결선) · 탈락 · 적응 점수 50점 미만 = 제외(가짜 추천 0) · TryFind = 적응 점수.
//   ③ 브리핑 합성: 국면 · 누적 에피소드 · 가중치 · 섀도우 개선치 · 자산군별 Top N · 미결선/첫 스캔 전 정직 문구 · 섀도우 투영.
//   ④ CLI --pluto-briefing: 판독 전용(DB 미생성 · 행 수 불변) · 체크포인트 복원 출력 · 사용법 오류.
//   ⑤ DualArenaPage [📊 인지 브리핑] 패널 · 카드 학습 투영 줄 · 호스트 결선 문자열 · RCL 순수성.
//   ⑥ δ=0: 추천 엔진 스캔 + 가중치 핫스왑 동시 진행 중 체결 핫패스(가중치 seqlock 판독 + 추천 보드 조회) 10만 회 = 0B · KRX 정산 10만 회 = 0B.
// ============================================================================
using System;
using System.IO;
using System.Linq;
using System.Threading;
using Bunit;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using MS2E.Infrastructure;
using MS2E.UI.Portable.Pages.Trading;
using MS2E.UI.Portable.Ports.Trading;
using StockManager.Adapters;
using StockManager.Engines.Arena.Common;
using StockManager.Engines.Arena.Crypto;
using StockManager.Engines.Arena.Domestic;
using StockManager.Engines.Arena.Us;
using StockManager.Engines.Cognitive.Learning;
using StockManager.Engines.Cognitive.Microstructure;
using StockManager.Engines.Cognitive.Screening;
using StockManager.Engines.Cognitive.Technical;
using StockManager.Engines.Cognitive.Valuation;
using StockManager.Tools;
using MarketRegime = StockManager.Engines.Cognitive.Regime.MarketRegime;   // MS2E.Core.MarketRegime(전역 using) 동명 타입과 구분
using Xunit;
using static StockManager.Tests.ZeroAllocProbe;

namespace StockManager.Tests.Cognitive
{
    [Collection("ConcurrencyStress")]
    public sealed class PlutoBriefingRecommenderTests
    {
        private const long Samsung = 5930, Hynix = 660;
        private static readonly DateTime Kst10 = new(2026, 10, 22, 10, 0, 0);
        private static readonly long Sec = TimeSpan.TicksPerSecond;
        private static readonly long T0 = new DateTime(2026, 10, 26, 0, 0, 0, DateTimeKind.Utc).Ticks;

        private static PlutoScreenInputs In(long sym, int? val, int? tech, int? micro, MarketRegime regime = MarketRegime.TrendingBull,
            bool depthTrap = false, PlutoScreenMarket market = PlutoScreenMarket.Krx, long price = 50_000)
            => new(market, sym, price,
                val.HasValue, val ?? 0, val.HasValue ? 2000 : 0, val.HasValue ? PlutoValuationGrade.FairDiscount : PlutoValuationGrade.Unknown, val.HasValue ? 62_500 : 0, val.HasValue,
                tech.HasValue, tech ?? 0, tech.HasValue ? PlutoSqueezePhase.BreakoutUp : default, tech.HasValue ? PlutoProfileZone.PocSupport : default,
                micro.HasValue, micro ?? 0, PlutoDivergenceType.None, micro.HasValue, false, micro.HasValue && depthTrap,
                regime);

        private static PlutoCognitiveWeights W(int tilt, int strict, long gen = 3) => new(gen, tilt, strict, 6200, 800, 40);

        private static PlutoMarketScreener ScreenerOf(Func<PlutoCognitiveWeights>? weights, params PlutoScreenInputs[] universe)
        {
            var s = new PlutoMarketScreener(new PlutoRecommendationBoard(), null, () => T0, weights);
            s.AddUniverse(PlutoScreenMarket.Krx, dest => { universe.AsSpan().CopyTo(dest); return universe.Length; }, KrxArenaEngine.DefaultCapitalWon);
            return s;
        }

        // ── ① 투영 산식 ──────────────────────────────────────────────────────

        [Fact]
        public void Project_Neutral_Weights_Are_Zero_Adjust_And_Preserve_Base_Score()
        {
            var s = PlutoDiscoveryScorer.Score(In(1, 80, 60, 40), default);
            var a = PlutoRecommendationEngine.Project(in s, default);
            Assert.True(a.IsZero);
            Assert.Equal(s.Total, PlutoRecommendationEngine.AdaptiveTotal(in s, in a));
            Assert.Equal("🧠 학습 가중치 투영: 중립(조정 0점 · 기본 점수 그대로)", PlutoRecommendationEngine.RationaleText(a));
        }

        [Theory]
        [InlineData(100, 50, 6)]     // 기술 25점 − 12 = 13 × 50% = 6(내림)
        [InlineData(100, -50, -6)]
        [InlineData(-100, 50, -6)]   // 기술 0점 − 12 = −12 × 50% = −6
        [InlineData(0, 50, 0)]       // 중립 기술 = 조정 0
        [InlineData(60, 20, 1)]      // 기술 20점 − 12 = 8 × 20% = 1
        public void Project_Momentum_Tilt_Scales_Technical_Excess(int tech, int tilt, int expected)
        {
            var s = PlutoDiscoveryScorer.Score(In(1, 50, tech, 50), default);
            Assert.Equal(expected, PlutoRecommendationEngine.Project(in s, W(tilt, 0)).MomentumPts);
        }

        [Fact]
        public void Project_Strictness_Penalizes_Per_Counter_Thesis_And_Malformed_Is_Ignored()
        {
            var clean = PlutoDiscoveryScorer.Score(In(1, 50, 50, 50), default);
            var risky = PlutoDiscoveryScorer.Score(In(2, 50, 50, 50, depthTrap: true), default);
            Assert.Equal(1, risky.RiskCount);
            Assert.Equal(0, PlutoRecommendationEngine.Project(in clean, W(0, 20)).DevilPts);    // 반대 논리 0건 = 감점 0
            Assert.Equal(10, PlutoRecommendationEngine.Project(in risky, W(0, 20)).DevilPts);
            Assert.Equal(-5, PlutoRecommendationEngine.Project(in risky, W(0, -10)).DevilPts);  // 엄격도 완화 = 가점
            var bad = new PlutoCognitiveWeights(1, 99, 50);                                     // 범위 위반(tilt ±50 · 엄격도 −10~+20)
            Assert.False(bad.IsWellFormed);
            Assert.True(PlutoRecommendationEngine.Project(in risky, in bad).IsZero);
            var a = PlutoRecommendationEngine.Project(in risky, W(50, 20, gen: 7));
            Assert.Equal("🧠 학습 가중치 투영(세대 #7): 모멘텀 +3점 · 악마의 변호인 -10점 → 순 -7점", PlutoRecommendationEngine.RationaleText(a));   // 기술 18점 − 12 = 6 × 50%
            Assert.Equal(0, PlutoRecommendationEngine.AdaptiveTotal(new PlutoDiscoveryScore(0, 0, 0, 0, default, 1), new PlutoAdaptiveAdjust(-6, 0, 1)));   // 하한 포화
        }

        // ── ② 랭킹 ───────────────────────────────────────────────────────────

        // A = 가치/수급 우위 · 기술 약세(7점) · B = 기술 강세(25점) · 가치 중립.
        private static readonly PlutoScreenInputs A = In(Samsung, 100, -40, 100);
        private static readonly PlutoScreenInputs B = In(Hynix, 0, 100, 40);

        [Fact]
        public void Rank_Learned_Momentum_Tilt_Reorders_Picks_Neutral_Preserves_Legacy_Order()
        {
            Assert.Equal(77, PlutoDiscoveryScorer.Score(in A, default).Total);
            Assert.Equal(74, PlutoDiscoveryScorer.Score(in B, default).Total);

            using var neutral = ScreenerOf(null, A, B);
            var n = neutral.Step().Of(PlutoScreenMarket.Krx);
            Assert.Equal(new[] { Samsung, Hynix }, n.Select(r => r.Symbol));
            Assert.Equal(new[] { 77, 74 }, n.Select(r => r.FinalScore));
            Assert.All(n, r => Assert.True(r.Adaptive.IsZero));

            var lockW = new PlutoCognitiveWeightSeqlock();
            Assert.True(lockW.TrySwap(W(50, 0), shadowPassed: true));
            using var bull = ScreenerOf(PlutoMarketScreener.WeightsFrom(lockW), A, B);
            var b = bull.Step().Of(PlutoScreenMarket.Krx);
            Assert.Equal(new[] { Hynix, Samsung }, b.Select(r => r.Symbol));              // 모멘텀 강화 = 기술 강세 종목 역전
            Assert.Equal(new[] { 80, 75 }, b.Select(r => r.FinalScore));                  // 74 + 6 · 77 − 2
            Assert.Equal(1, b[0].Adaptive.Generation);
            Assert.True(bull.Board.TryFind(PlutoScreenMarket.Krx, Hynix, out int score, out int rank));
            Assert.Equal((80, 1), (score, rank));                                        // 핫패스 조회 = 적응 점수

            Assert.True(lockW.TrySwap(W(-50, 0), shadowPassed: true));
            var c = bull.Step().Of(PlutoScreenMarket.Krx);                                // 같은 스크리너 · 핫스왑 즉시 반영
            Assert.Equal(new[] { Samsung, Hynix }, c.Select(r => r.Symbol));
            Assert.Equal(new[] { 79, 68 }, c.Select(r => r.FinalScore));
        }

        [Fact]
        public void Rank_Excludes_Unfed_Vetoed_And_Strictness_Demoted_Without_Fabrication()
        {
            var unfed = In(1, null, null, null);                                          // 피드 판독 전무
            var vetoed = In(2, 90, 90, 90, MarketRegime.TrendingBear, depthTrap: true);   // 반대 논리 2건
            var marginal = In(3, -20, -20, 0, depthTrap: true);                           // 기본 52점(10 + 10 + 12 + 20) · 반대 논리 1건
            var strong = In(4, 90, 90, 90);
            var noQuote = In(5, 90, 90, 90, price: 0);                                    // 시세 미수신 = 유니버스 밖
            Assert.Equal(52, PlutoDiscoveryScorer.Score(in marginal, default).Total);
            var universe = new[] { unfed, vetoed, marginal, strong, noQuote };

            var neutral = PlutoRecommendationEngine.Rank(universe, PlutoScreenMarket.Krx, default, default, KrxArenaEngine.DefaultCapitalWon, 200, 5, out var nt);
            Assert.Equal(new long[] { 4, 3 }, neutral.Select(r => r.Symbol));
            Assert.Equal(new PlutoScreenTally(4, 1, 1, 0, 200, 2), nt);

            var strict = PlutoRecommendationEngine.Rank(universe, PlutoScreenMarket.Krx, default, W(0, 20), KrxArenaEngine.DefaultCapitalWon, 200, 3, out var st);
            var only = Assert.Single(strict);                                             // 엄격도 +20 → 52 − 10 = 42 < 50 탈락
            Assert.Equal(4L, only.Symbol);
            Assert.Equal(new PlutoScreenTally(4, 1, 1, 1, 200, 1), st);
            Assert.DoesNotContain(strict, r => r.Symbol is 1 or 2 or 5);
            Assert.Empty(PlutoRecommendationEngine.Rank(new[] { unfed }, PlutoScreenMarket.Krx, default, W(50, -10), 1_000_000, 200, 5, out _));   // 완화 가중치도 판독 전무 = 0건
            Assert.Throws<ArgumentOutOfRangeException>(() => PlutoRecommendationEngine.Rank(universe, PlutoScreenMarket.Krx, default, default, 1, 0, 6, out _));
        }

        [Fact]
        public void Adapter_Card_Carries_Adaptive_Score_And_Learning_Rationale()
        {
            var lockW = new PlutoCognitiveWeightSeqlock();
            Assert.True(lockW.TrySwap(W(50, 0), shadowPassed: true));
            using var s = ScreenerOf(PlutoMarketScreener.WeightsFrom(lockW), A, B);
            s.Step();
            var cards = new PlutoRecommendationPortAdapter(s).Current.Krx.Cards;
            Assert.Equal(("000660", 80, 6, 0, 6), (cards[0].Code, cards[0].Score, cards[0].LearnMomentumPts, cards[0].LearnDevilPts, cards[0].LearnNetPts));
            Assert.Equal("🧠 학습 가중치 투영(세대 #1): 모멘텀 +6점 · 악마의 변호인 +0점 → 순 +6점", cards[0].LearnText);
            Assert.Equal(cards[0].Score - cards[0].LearnNetPts, cards[0].ValuationPts + cards[0].TechnicalPts + cards[0].MicroPts + cards[0].MacroPts);
        }

        // ── ③ 브리핑 합성 ────────────────────────────────────────────────────

        private static PlutoRecommendationCard Card(int market, int rank, string code, string name, int score, int net) =>
            new PlutoRecommendationCard(market, rank, rank, code, name, score, 20, 20, 20, 20, Array.AsReadOnly(new[] { "💎 안전마진 +20%", "📐 스퀴즈 상방", "🌐 상승 국면" }),
                true, 2000, "70,000원", "62,500원", true, 10, 1, "😈 악마의 변호인: 반대 논리 1건 — 관찰") { LearnMomentumPts = net, LearnText = net != 0 ? "🧠 학습 투영 " + net : "" };

        private static PlutoRecoBoardView Board() => new(true, 4, new DateTime(T0, DateTimeKind.Utc),
            new PlutoRecoMarketView(Array.AsReadOnly(new[]
            {
                Card(PlutoRecoMarket.Krx, 1, "000660", "SK하이닉스", 86, 6), Card(PlutoRecoMarket.Krx, 2, "005930", "삼성전자", 80, -2),
                Card(PlutoRecoMarket.Krx, 3, "035420", "NAVER", 70, 0), Card(PlutoRecoMarket.Krx, 4, "051910", "LG화학", 60, 0),
            }), 180, 12, 3, 100, 200, 65),
            PlutoRecoMarketView.None with { Monitored = 100 }) { Crypto = new PlutoRecoMarketView(Array.Empty<PlutoRecommendationCard>(), 117, 9, 0, 108, 120, 0) };

        private static PlutoLearningView Learned() => new(true, PlutoLearningHealth.Live, "2020 코로나 유동성", 88, false,
            12, 15, 6, 240, 9, 4, "2020 팬데믹 유동성 랠리", 914, "2020.03~2021.06 · 해설", "모멘텀 가중 +", null, 1_234, PlutoLearningPersist.Active,
            new PlutoShadowView(PlutoShadowVerdictCode.Passed, 5400, 6150, 1200, 900, 30, 6150, 900, 30));

        [Fact]
        public void Compose_Quantifies_Regime_Episodes_Weights_Shadow_And_Top_Picks()
        {
            var r = PlutoBriefingComposer.Compose(true, Learned(), true, Board());
            Assert.Equal("실시간 연동 중", r.LearningState);
            Assert.Equal("2020 팬데믹 유동성 랠리 · 유사도 91.4% · 모멘텀 가중 +", r.RegimeLine);
            Assert.Equal("1,234회(재기동 합산 평생 누적 · 영속화 가동 중)", r.EpisodesLine);
            Assert.Equal("세대 #12 · 모멘텀 조정 +15% · 악마의 변호인 엄격도 +6점 · 핫스왑 9회 / 거부 4회", r.WeightsLine);
            Assert.Equal((750, 300), (r.Shadow.WinRateGainBp, r.Shadow.MddGainBp));
            Assert.Equal("직전 판정: 통과(핫스왑) · 승률 54.00% → 61.50% (+7.50%p) · MDD 12.00% → 9.00% (개선 +3.00%p) · 홀드아웃 30 · 현행 가중치 섀도우 승률 61.50% · MDD 9.00% · 표본 30", r.ShadowLine);
            Assert.Equal((12L, 15, 6, 1_234L, 914), (r.Generation, r.MomentumTiltPct, r.DevilStrictnessPts, r.Episodes, r.RegimeSimilarityX10));

            var krx = r.Of(PlutoRecoMarket.Krx);
            Assert.Equal(3, krx.Picks.Count);                                             // 기본 Top 3
            Assert.Equal(new[] { "000660", "005930", "035420" }, krx.Picks.Select(p => p.Code));
            Assert.Equal("감시 200 · 피드 판독 180 · 판독 대기 20 · 통과 65", krx.Status);
            Assert.Equal(("💎 안전마진 +20%", "😈 악마의 변호인: 반대 논리 1건 — 관찰", 6), (krx.Picks[0].Reason, krx.Picks[0].Risk, krx.Picks[0].LearnNetPts));
            Assert.Empty(r.Of(PlutoRecoMarket.Us).Picks);
            Assert.EndsWith("추천 기준 통과 종목 없음(근거 없는 추천 0)", r.Of(PlutoRecoMarket.Us).Status);
            Assert.Contains("판독 대기 100", r.Of(PlutoRecoMarket.Us).Status);           // 피드 미결선 = 감시만 · 가짜 종목 0
            Assert.Empty(r.Of(PlutoRecoMarket.Crypto).Picks);
            Assert.Equal(3, r.TotalPicks);
            Assert.Equal(4, PlutoBriefingComposer.Compose(true, Learned(), true, Board(), topN: 9).Of(PlutoRecoMarket.Krx).Picks.Count);   // 5 클램프 · 카드 4건

            var lines = r.Lines();
            Assert.Equal("📊 플루토 인지 학습 브리핑", lines[0]);
            Assert.Contains("  국면 매칭 : 2020 팬데믹 유동성 랠리 · 유사도 91.4% · 모멘텀 가중 +", lines);
            Assert.Contains("  [KRX] 감시 200 · 피드 판독 180 · 판독 대기 20 · 통과 65", lines);
            Assert.Contains("    #1 SK하이닉스(000660) 86점 (학습 +6) · 근거: 💎 안전마진 +20% · 리스크: 😈 악마의 변호인: 반대 논리 1건 — 관찰", lines);
            Assert.Contains("    #2 삼성전자(005930) 80점 (학습 -2) · 근거: 💎 안전마진 +20% · 리스크: 😈 악마의 변호인: 반대 논리 1건 — 관찰", lines);
            Assert.StartsWith("※ 가상 원장 전용", lines[^1]);
        }

        [Fact]
        public void Compose_Unwired_And_Pre_Scan_Render_Honestly_Without_Fabrication()
        {
            var u = PlutoBriefingComposer.Compose(false, UnwiredPlutoLearningPort.Instance.Current, false, PlutoRecoBoardView.None);
            Assert.Equal(("학습 엔진 미결선", "학습 엔진 미결선", "학습 엔진 미결선", "학습 엔진 미결선", "학습 엔진 미결선"),
                (u.LearningState, u.RegimeLine, u.EpisodesLine, u.WeightsLine, u.ShadowLine));
            Assert.All(u.Markets, m => { Assert.Empty(m.Picks); Assert.Equal("추천 엔진 미결선 — 추천 없음", m.Status); });
            Assert.Equal(-1, u.RegimeSimilarityX10);

            var fresh = new PlutoLearningView(true, PlutoLearningHealth.NoData, null, -1, false, 0, 0, 0, 0, 0, 0);
            var f = PlutoBriefingComposer.Compose(true, fresh, true, PlutoRecoBoardView.None);
            Assert.Equal("수집원 미결선", f.LearningState);
            Assert.Equal("판정 전(12대 역사적 국면 · 거시 관측 2차원 이상 수집 대기)", f.RegimeLine);
            Assert.Equal("0회(이번 기동 청산 회고 표본 · 영속화 미결선)", f.EpisodesLine);
            Assert.Equal("학습 전 중립(세대 #0 · 모멘텀 +0% · 엄격도 +0점)", f.WeightsLine);
            Assert.Equal("직전 판정: 판정 전 · 현행 가중치 섀도우 기록 없음(학습 전 중립)", f.ShadowLine);
            Assert.All(f.Markets, m => Assert.Equal("첫 스캔 대기 — 추천 없음", m.Status));

            var rejected = new PlutoShadowView(PlutoShadowVerdictCode.WinRateDegraded, 6000, 5500, 900, 900, 20, 0, 0, 0);
            Assert.Equal((-500, 0), (rejected.WinRateGainBp, rejected.MddGainBp));
            Assert.Equal(0, new PlutoShadowView(PlutoShadowVerdictCode.InsufficientHoldout, 6000, 7000, 0, 0, 5, 0, 0, 0).WinRateGainBp);   // 비교 미도달 = 개선치 0
            Assert.Equal("판정 전", PlutoShadowVerdictCode.Label(77));
        }

        [Fact]
        public void Learning_Adapter_Projects_Shadow_Verdict_And_Live_Weights()
        {
            var v = new PlutoShadowVerdict(PlutoShadowReason.DrawdownWorse, 5800, 6000, 700, 1100, 24, 48);
            var sv = PlutoLearningPortAdapter.MapShadow(in v, W(10, 4));
            Assert.Equal(new PlutoShadowView(PlutoShadowVerdictCode.DrawdownWorse, 5800, 6000, 700, 1100, 24, 6200, 800, 40), sv);
            Assert.Equal((200, -400), (sv.WinRateGainBp, sv.MddGainBp));
            Assert.Equal(PlutoShadowVerdictCode.None, PlutoLearningPortAdapter.MapShadow(default, default).Verdict);
            foreach (PlutoShadowReason reason in Enum.GetValues<PlutoShadowReason>())   // 엔진 판정 코드 = 포트 코드 값 동일(재번호 0)
                Assert.Equal((int)reason, PlutoLearningPortAdapter.MapShadow(new PlutoShadowVerdict(reason, 0, 0, 0, 0, 0, 0), default).Verdict);
        }

        [Fact]
        public void Briefing_Service_Reads_Both_Ports()
        {
            var svc = new PlutoBriefingService(new FixedLearningPort(Learned()), new FixedRecoPort(Board()));
            var r = svc.Build();
            Assert.True(r.LearningWired && r.RecoWired);
            Assert.Equal(3, r.TotalPicks);
            Assert.Equal(r.Lines(), svc.Render());
            var u = new PlutoBriefingService(UnwiredPlutoLearningPort.Instance, UnwiredPlutoRecommendationPort.Instance).Build();
            Assert.False(u.LearningWired || u.RecoWired);
            Assert.Equal(0, u.TotalPicks);
        }

        // ── ④ CLI ────────────────────────────────────────────────────────────

        [Fact]
        public void Cli_Reads_Checkpoint_ReadOnly_And_Prints_Restored_Briefing()
        {
            string path = Path.Combine(Path.GetTempPath(), "ms2e_brief_" + Guid.NewGuid().ToString("N") + ".db");
            try
            {
                using (var db = new LocalDatabase(path))
                {
                    var store = new PlutoLearningStore(db);
                    store.Append(PlutoLearningCheckpoint.Seal(1, 300, W(10, 2, gen: 4), T0), 64);
                    store.Append(PlutoLearningCheckpoint.Seal(2, 512, W(20, 5, gen: 9), T0 + Sec), 64);
                }
                SqliteConnection.ClearAllPools();
                var before = File.ReadAllBytes(path);
                Assert.True(PlutoBriefingCli.IsInvocation(new[] { "--PLUTO-BRIEFING" }));
                Assert.False(PlutoBriefingCli.IsInvocation(Array.Empty<string>()));
                var sw = new StringWriter();
                Assert.Equal(0, PlutoBriefingCli.Run(new[] { "--pluto-briefing", "--db", path, "--top", "2" }, sw));
                string o = sw.ToString();
                Assert.Contains("원샷 CLI · 영속 학습 체크포인트 판독 전용(기록 2행)", o);
                Assert.Contains("학습 상태 : 학습 엔진 정지", o);
                Assert.Contains("국면 매칭 : " + PlutoRegimeVectorLake.Get(2)!.Name + "(직전 기록 국면 · 유사도는 실시간 거시 관측 후 판정)", o);
                Assert.Contains("누적 학습 : 512회(재기동 합산 평생 누적 · 영속화 가동 중)", o);
                Assert.Contains("최적 가중 : 세대 #9 · 모멘텀 조정 +20% · 악마의 변호인 엄격도 +5점", o);
                Assert.Contains("현행 가중치 섀도우 승률 62.00% · MDD 8.00% · 표본 40", o);
                Assert.Contains("[KRX] 추천 엔진 미결선 — 추천 없음", o);                    // 원샷 = 시세 피드 없음 → 가짜 종목 0
                SqliteConnection.ClearAllPools();
                Assert.Equal(before, File.ReadAllBytes(path));                                // 판독 전용 = 바이트 불변(기록 0)
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                foreach (var f in new[] { path, path + "-wal", path + "-shm" }) if (File.Exists(f)) File.Delete(f);
            }
        }

        [Fact]
        public void Cli_Missing_Db_Is_Not_Created_And_Usage_Errors_Return_1()
        {
            string path = Path.Combine(Path.GetTempPath(), "ms2e_brief_missing_" + Guid.NewGuid().ToString("N") + ".db");
            var sw = new StringWriter();
            Assert.Equal(0, PlutoBriefingCli.Run(new[] { "--pluto-briefing", "--db", path }, sw));
            Assert.False(File.Exists(path));                                                  // 생성 0
            string o = sw.ToString();
            Assert.Contains("DB 없음 · 첫 기동 전", o);
            Assert.Contains("최적 가중 : 학습 전 중립(세대 #0 · 모멘텀 +0% · 엄격도 +0점)", o);
            Assert.Contains("누적 학습 : 0회(재기동 합산 평생 누적 · 영속화 미결선)", o);
            Assert.Contains("국면 매칭 : 판정 전", o);
            Assert.Equal(1, PlutoBriefingCli.Run(new[] { "--pluto-briefing", "--top", "9" }, new StringWriter()));
            Assert.Equal(1, PlutoBriefingCli.Run(new[] { "--pluto-briefing", "--db" }, new StringWriter()));
        }

        [Fact]
        public void Cli_Corrupt_Latest_Rolls_Back_To_Prior_Golden()
        {
            var good = PlutoLearningCheckpoint.Seal(3, 100, W(5, 1, gen: 2), T0);
            var v = PlutoBriefingCli.Restore(new[] { PlutoLearningCheckpoint.Corrupt, good });
            Assert.Equal((2L, 5, 1, 100L, PlutoLearningPersist.RolledBack), (v.Generation, v.MomentumTiltPct, v.DevilStrictnessPts, v.LifetimeEpisodes, v.PersistState));
            Assert.Equal(PlutoRegimeVectorLake.Get(3)!.Name, v.RegimeName);
            var all = PlutoBriefingCli.Restore(new[] { PlutoLearningCheckpoint.Corrupt });
            Assert.Equal((0L, PlutoLearningPersist.NeutralFallback), (all.Generation, all.PersistState));
            Assert.Null(all.RegimeName);
        }

        // ── ⑤ 페이지 · 호스트 · 순수성 ──────────────────────────────────────

        private sealed class FixedRecoPort : IPlutoRecommendationPort
        {
            public FixedRecoPort(PlutoRecoBoardView v) => Current = v;
            public bool IsWired => true;
            public PlutoRecoBoardView Current { get; }
        }

        private sealed class FixedLearningPort : IPlutoLearningPort
        {
            public FixedLearningPort(PlutoLearningView v) => Current = v;
            public bool IsWired => true;
            public PlutoLearningView Current { get; }
        }

        private static (Bunit.TestContext, IRenderedComponent<DualArenaPage>) Render(IPlutoLearningPort learning, IPlutoRecommendationPort reco)
        {
            var ctx = new Bunit.TestContext();
            var krx = new KrxArenaEngine();
            var f = new KrxArenaQuoteFeeder(krx);
            Assert.True(f.SelectSource(KrxArenaFeed.Synthetic));
            for (int i = 0; i < 3; i++) f.Pump(Kst10.AddHours(-9).Ticks + i * Sec);
            ctx.Services.AddSingleton<IKrxArenaPort>(new KrxArenaPortAdapter(krx, f));
            var us = new UsTechArenaEngine();
            var uf = new UsArenaQuoteFeeder(us);
            Assert.True(uf.SelectSource(UsArenaFeed.Synthetic));
            for (int i = 0; i < 3; i++) uf.Pump(Kst10.AddHours(-9).Ticks + i * Sec);
            ctx.Services.AddSingleton<IUsArenaPort>(new UsArenaPortAdapter(us, uf));
            ctx.Services.AddSingleton<ICryptoArenaPort>(new CryptoArenaPortAdapter(new UpbitCryptoArenaEngine()));
            ctx.Services.AddSingleton<IArenaPreferencePort>(UnwiredArenaPreferencePort.Instance);
            ctx.Services.AddSingleton(learning);
            ctx.Services.AddSingleton(reco);
            return (ctx, ctx.RenderComponent<DualArenaPage>());
        }

        [Fact]
        public void DualArenaPage_Briefing_Toggle_Renders_Quantified_Panel()
        {
            var (ctx, cut) = Render(new FixedLearningPort(Learned()), new FixedRecoPort(Board()));
            using var _ = ctx;
            Assert.Empty(cut.FindAll(".da-brief"));                                           // 닫힘 = 합성 0
            Assert.Equal("off", cut.Find(".da-brief-toggle").GetAttribute("data-brief-toggle"));
            cut.Find(".da-brief-toggle").Click();
            var panel = cut.Find(".da-brief");
            Assert.Equal("wired", panel.GetAttribute("data-brief"));
            Assert.Equal("3", panel.GetAttribute("data-brief-picks"));
            Assert.Contains("2020 팬데믹 유동성 랠리 · 유사도 91.4%", cut.Find("[data-brief-regime]").TextContent);
            Assert.Equal("914", cut.Find("[data-brief-regime]").GetAttribute("data-brief-regime"));
            Assert.Equal("1234", cut.Find("[data-brief-episodes]").GetAttribute("data-brief-episodes"));
            Assert.Contains("모멘텀 조정 +15%", cut.Find("[data-brief-gen]").TextContent);
            Assert.Contains("up", cut.Find("[data-brief-gain]").ClassList);                  // 승률 개선 = 상승 적색
            Assert.Equal(3, cut.FindAll(".da-brief-market[data-brief-market='0'] .da-brief-picks li").Count);
            Assert.Contains("(학습 +6)", cut.Find(".da-brief-picks li[data-code='000660']").TextContent);
            Assert.Contains("down", cut.Find(".da-brief-picks li[data-code='005930'] .da-brief-net").ClassList);   // 하락 청색
            Assert.Equal("0", cut.Find(".da-brief-market[data-brief-market='2']").GetAttribute("data-brief-count"));
            Assert.Equal(2, cut.FindAll(".da-reco-card .da-reco-learn").Count);              // 카드 학습 투영 줄(LearnText 있는 카드만 · 국내 탭 4장 중 2장)
            cut.Find(".da-brief-close").Click();
            Assert.Empty(cut.FindAll(".da-brief"));
        }

        [Fact]
        public void DualArenaPage_Briefing_Unwired_Renders_Honestly()
        {
            var (ctx, cut) = Render(UnwiredPlutoLearningPort.Instance, UnwiredPlutoRecommendationPort.Instance);
            using var _ = ctx;
            cut.Find(".da-brief-toggle").Click();
            Assert.Equal("unwired", cut.Find(".da-brief").GetAttribute("data-brief"));
            Assert.Equal("0", cut.Find(".da-brief").GetAttribute("data-brief-picks"));
            Assert.Contains("학습 엔진 미결선", cut.Find("[data-brief-regime]").TextContent);
            Assert.Empty(cut.FindAll(".da-brief-picks li"));
            Assert.Equal(3, cut.FindAll(".da-brief-market").Count(m => m.TextContent.Contains("추천 엔진 미결선")));
        }

        private static string Root()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "StockManager.sln"))) dir = dir.Parent;
            Assert.NotNull(dir);
            return dir!.FullName;
        }

        [Fact]
        public void Host_Wires_Learned_Weights_Cli_Flag_And_Rcl_Stays_Pure()
        {
            string root = Root();
            string form = File.ReadAllText(Path.Combine(root, "StockManager", "Blazor", "BlazorHomeForm.cs"));
            Assert.Contains("plutoWeightsRef = plutoWeights;", form);
            Assert.Contains("weights: plutoWeightsRef is null ? null : StockManager.Engines.Cognitive.Screening.PlutoMarketScreener.WeightsFrom(plutoWeightsRef)", form);
            string program = File.ReadAllText(Path.Combine(root, "StockManager", "Program.cs"));
            Assert.True(program.IndexOf("PlutoBriefingCli.IsInvocation(args)", StringComparison.Ordinal) < program.IndexOf("ApplicationConfiguration.Initialize()", StringComparison.Ordinal));
            foreach (var f in new[] { "PlutoBriefing.cs", "PlutoLearningPort.cs", "PlutoRecommendationPort.cs" })
                Assert.DoesNotContain("StockManager.Engines", File.ReadAllText(Path.Combine(root, "MS2E.UI.Portable", "Ports", "Trading", f)));
            string page = File.ReadAllText(Path.Combine(root, "MS2E.UI.Portable", "Pages", "Trading", "DualArenaPage.razor"));
            Assert.DoesNotContain("StockManager.", page);
            string cli = File.ReadAllText(Path.Combine(root, "StockManager", "Tools", "PlutoBriefingCli.cs"));
            Assert.Contains("SqliteOpenMode.ReadOnly", cli);
            string code = string.Join("\n", cli.Split('\n').Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));
            Assert.DoesNotContain("QuantumVault", code);                                      // 주석 외 코드 = 피어 저장소 참조 0
        }

        // ── ⑥ δ=0 ────────────────────────────────────────────────────────────

        [Fact]
        public void HotPath_Weight_And_Reco_Lookups_Stay_ZeroAlloc_While_Engine_Ranks_And_Weights_HotSwap()
        {
            var lockW = new PlutoCognitiveWeightSeqlock();
            var universe = new PlutoScreenInputs[200];
            for (int i = 0; i < universe.Length; i++) universe[i] = In(1000 + i, (i * 37) % 200 - 100, (i * 53) % 200 - 100, (i * 71) % 200 - 100, depthTrap: (i & 7) == 0);
            using var screener = ScreenerOf(PlutoMarketScreener.WeightsFrom(lockW), universe);
            screener.Step();
            long probe = screener.Board.Current.Of(PlutoScreenMarket.Krx)[0].Symbol;
            int stop = 0;
            var ranker = new Thread(() => { while (Volatile.Read(ref stop) == 0) screener.Step(); }) { IsBackground = true };
            var swapper = new Thread(() =>
            {
                int k = 0;
                while (Volatile.Read(ref stop) == 0) { lockW.TrySwap(W((k % 101) - 50, (k % 31) - 10), shadowPassed: true); k++; Thread.Yield(); }
            }) { IsBackground = true };
            long sink = 0;
            void Reads(int n)
            {
                for (int i = 0; i < n; i++)
                {
                    if (lockW.TryRead(out var w)) sink += w.MomentumTiltPct + w.DevilStrictnessPts;
                    if (screener.Board.TryFind(PlutoScreenMarket.Krx, probe, out int score, out int rank)) sink += score + rank;
                }
            }
            ranker.Start();
            swapper.Start();
            try
            {
                Reads(10_000);
                long bytes = MinAllocated(() => Reads(100_000));
                Assert.Equal(0, bytes);
            }
            finally
            {
                Volatile.Write(ref stop, 1);
                ranker.Join();
                swapper.Join();
            }
            Assert.True(screener.Cycles > 1);
            Assert.True(lockW.TryRead(out var fin) && fin.Generation > 1);                     // 측정 중 핫스왑 실제 진행
            Assert.NotEqual(long.MinValue, sink);
        }

        [Fact]
        public void Krx_Settle_With_Learning_Stays_ZeroAlloc_While_Adaptive_Ranker_Scans()
        {
            var e = new KrxArenaEngine();
            e.EnableTechnical(true);
            e.EnableMicrostructure(true);
            var buffer = new PlutoValuationBuffer();
            new PlutoValuationEngine(buffer).SeedDefaults();
            e.AttachValuation(buffer);
            var lockW = new PlutoCognitiveWeightSeqlock();
            Assert.True(lockW.TrySwap(W(30, 8), shadowPassed: true));
            e.AttachLearning(lockW);
            using var screener = new PlutoMarketScreener(new PlutoRecommendationBoard(), null, () => T0, PlutoMarketScreener.WeightsFrom(lockW));
            screener.AddUniverse(PlutoScreenMarket.Krx, PlutoArenaUniverse.Krx(e, buffer, () => T0), e.CapitalWon);
            var kt = Kst10;
            int j = 0;
            void Settle(int n)
            {
                for (int i = 0; i < n; i++, j++)
                {
                    kt = kt.AddTicks(Sec * 2);
                    if (kt >= Kst10.AddHours(5)) kt = Kst10;
                    long px = 68_000 + ((j >> 6) & 63) * 100 - (j & 1) * 100;
                    e.OnQuote(Samsung + (j & 3), px, px + 100, (j & 32) != 0 ? 9000 : 1000, 1000, 100 + (j & 63) * ((j & 1) + 1), kt);
                }
            }
            Settle(20_000);
            int stop = 0;
            var scanner = new Thread(() => { while (Volatile.Read(ref stop) == 0) screener.Step(); }) { IsBackground = true };
            scanner.Start();
            try
            {
                long settleBytes = MinAllocated(() => Settle(100_000));
                Assert.Equal(0, settleBytes);
            }
            finally
            {
                Volatile.Write(ref stop, 1);
                scanner.Join();
            }
            Assert.True(screener.Cycles > 0);
            Assert.True(screener.Board.Current.TallyOf(PlutoScreenMarket.Krx).Scanned > 0);
        }
    }
}
