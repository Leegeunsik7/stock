// ============================================================================
// PlutoMarketScreenerTests — v12.18.78-DEV: 플루토 멀티 마켓 스크리너 · AI 추천 종목 레이더
//   ① 4대 팩터 점수 산식(0~25 투영 · 국면 · 에피소드 · 판독 없음 = 0점) · 반대 논리 6종 · 2건 중첩 탈락.
//   ② 스크리너 스텝: 순위(점수 → 반대 논리 수 → 종목) · Top 5 상한 · 탈락/판독 부족/기준 미달 집계 · 오배선 무시 · 세대 증가 · 근거 3줄 · 추천 수량.
//   ③ 불변 스냅샷: 방어 복사 · TryFind · 학습 스웜 거시 입력 투영.
//   ④ 아레나 유니버스: KRX 합성 시세 × 시드 밸류에이션 → 후보 · 추천 종목 ⊂ 시세 유니버스.
//   ⑤ 포트 어댑터 투영 · DualArenaPage 패널(결선/미결선 · 1-Click 담기 → 티켓 바인딩) · 호스트 결선 문자열.
//   ⑥ δ=0: 동시 게시 중 TryFind 10만 회 = 0B · 찢어진 판독 0 · 스크리너 동시 스캔 중 KRX 정산 10만 회 = 0B(MinAllocated · Release).
// ============================================================================
using System;
using System.Threading;
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
    public sealed class PlutoMarketScreenerTests
    {
        private const long Samsung = 5930;
        private static readonly DateTime Kst10 = new(2026, 10, 22, 10, 0, 0);
        private static readonly long Sec = TimeSpan.TicksPerSecond;
        private static readonly long T0 = new DateTime(2026, 10, 25, 0, 0, 0, DateTimeKind.Utc).Ticks;

        private static PlutoScreenInputs In(long sym, int? val, int? tech, int? micro, MarketRegime regime = MarketRegime.TrendingBull,
            PlutoValuationGrade grade = PlutoValuationGrade.FairDiscount, bool depthTrap = false, PlutoDivergenceType div = PlutoDivergenceType.None,
            PlutoSqueezePhase phase = PlutoSqueezePhase.BreakoutUp, PlutoProfileZone zone = PlutoProfileZone.PocSupport,
            PlutoScreenMarket market = PlutoScreenMarket.Krx, long price = 50_000, int mosBps = 2000)
            => new(market, sym, price,
                val.HasValue, val ?? 0, val.HasValue ? mosBps : 0, val.HasValue ? grade : PlutoValuationGrade.Unknown, val.HasValue ? 62_500 : 0, val.HasValue,
                tech.HasValue, tech ?? 0, tech.HasValue ? phase : default, tech.HasValue ? zone : default,
                micro.HasValue, micro ?? 0, micro.HasValue ? div : PlutoDivergenceType.None, micro.HasValue, false, micro.HasValue && depthTrap,
                regime);

        private static PlutoMarketScreener ScreenerOf(params PlutoScreenInputs[] universe) => ScreenerOf(default, universe);

        private static PlutoMarketScreener ScreenerOf(PlutoScreenMacro macro, params PlutoScreenInputs[] universe)
        {
            var s = new PlutoMarketScreener(new PlutoRecommendationBoard(), () => macro, () => T0);
            s.AddUniverse(PlutoScreenMarket.Krx, dest => { universe.AsSpan().CopyTo(dest); return universe.Length; }, KrxArenaEngine.DefaultCapitalWon);
            return s;
        }

        // ── ① 점수 산식 · 반대 논리 ───────────────────────────────────────────

        [Theory]
        [InlineData(-100, 0)]
        [InlineData(-37, 7)]
        [InlineData(0, 12)]
        [InlineData(60, 20)]
        [InlineData(80, 22)]
        [InlineData(100, 25)]
        [InlineData(500, 25)]
        [InlineData(-500, 0)]
        public void Points_Project_Factor_Score_To_0_25(int factor, int expected) => Assert.Equal(expected, PlutoDiscoveryScorer.Points(factor));

        [Theory]
        [InlineData(PlutoEpisodeKind.Bull, 100, 10)]
        [InlineData(PlutoEpisodeKind.Liquidity, 40, 7)]
        [InlineData(PlutoEpisodeKind.Bear, 100, 0)]
        [InlineData(PlutoEpisodeKind.Bear, 40, 3)]
        [InlineData(PlutoEpisodeKind.Panic, 60, 2)]
        [InlineData(PlutoEpisodeKind.Range, 90, 5)]
        public void Episode_Points_Follow_Kind_And_Similarity(PlutoEpisodeKind kind, int sim, int expected)
        {
            Assert.Equal(expected, PlutoDiscoveryScorer.EpisodePoints(new PlutoScreenMacro(kind, sim, "에피소드", true)));
            Assert.Equal(5, PlutoDiscoveryScorer.EpisodePoints(default));   // 판정 전 = 중립
        }

        [Fact]
        public void Score_Sums_Four_Factors_And_Missing_Factor_Is_Zero_Not_Neutral()
        {
            var s = PlutoDiscoveryScorer.Score(In(1, 80, 60, 40), default);
            Assert.Equal((22, 20, 17, 20), (s.ValuationPts, s.TechnicalPts, s.MicroPts, s.MacroPts));
            Assert.Equal(79, s.Total);
            Assert.Equal(3, s.Coverage);
            Assert.True(s.Recommendable);

            var partial = PlutoDiscoveryScorer.Score(In(1, 0, null, null, MarketRegime.Unknown), default);
            Assert.Equal((12, 0, 0, 10), (partial.ValuationPts, partial.TechnicalPts, partial.MicroPts, partial.MacroPts));
            Assert.False(partial.Recommendable);   // 22점 < 50

            var none = PlutoDiscoveryScorer.Score(In(1, null, null, null), default);
            Assert.True(none.Insufficient);
            Assert.False(none.Recommendable);
            Assert.Equal(20, none.Total);          // 국면/거시만 — 판독 3종 전무 = 근거 부족
        }

        [Fact]
        public void Risks_Detect_Six_Counter_Theses_And_Two_Veto()
        {
            var bearMacro = new PlutoScreenMacro(PlutoEpisodeKind.Bear, 60, "2022 금리 인상", true);
            Assert.Equal(PlutoScreenRisk.Overvalued, PlutoDiscoveryScorer.RisksOf(In(1, -40, null, null, grade: PlutoValuationGrade.Overvalued), default));
            Assert.Equal(PlutoScreenRisk.Overvalued, PlutoDiscoveryScorer.RisksOf(In(1, -90, null, null, grade: PlutoValuationGrade.ExtremeBubble), default));
            Assert.Equal(PlutoScreenRisk.TechnicalBreakdown, PlutoDiscoveryScorer.RisksOf(In(1, null, -30, null, phase: PlutoSqueezePhase.BreakoutDown), default));
            Assert.Equal(PlutoScreenRisk.TechnicalBreakdown, PlutoDiscoveryScorer.RisksOf(In(1, null, 0, null, phase: PlutoSqueezePhase.Neutral, zone: PlutoProfileZone.PocResistance), default));
            Assert.Equal(PlutoScreenRisk.CvdBearish, PlutoDiscoveryScorer.RisksOf(In(1, null, null, -20, div: PlutoDivergenceType.Bearish), default));
            Assert.Equal(PlutoScreenRisk.DepthTrap, PlutoDiscoveryScorer.RisksOf(In(1, null, null, 0, depthTrap: true), default));
            Assert.Equal(PlutoScreenRisk.BearRegime, PlutoDiscoveryScorer.RisksOf(In(1, null, null, null, MarketRegime.TrendingBear), default));
            Assert.Equal(PlutoScreenRisk.BearRegime, PlutoDiscoveryScorer.RisksOf(In(1, null, null, null, MarketRegime.PanicLiquidation), default));
            Assert.Equal(PlutoScreenRisk.MacroRiskOff, PlutoDiscoveryScorer.RisksOf(In(1, null, null, null), bearMacro));
            Assert.Equal(PlutoScreenRisk.None, PlutoDiscoveryScorer.RisksOf(In(1, null, null, null), new PlutoScreenMacro(PlutoEpisodeKind.Bear, 59, "x", true)));   // 60% 미만 = 무시

            var one = PlutoDiscoveryScorer.Score(In(1, 80, 60, 40, depthTrap: true), default);
            Assert.Equal(1, one.RiskCount);
            Assert.False(one.Vetoed);
            Assert.True(one.Recommendable);
            Assert.Contains("감시 통과", PlutoDiscoveryScorer.RiskText(one.Risks));

            var two = PlutoDiscoveryScorer.Score(In(1, 80, 60, 40, depthTrap: true), bearMacro);   // 고득점이어도 반대 논리 2건 = 탈락
            Assert.Equal(2, two.RiskCount);
            Assert.True(two.Vetoed);
            Assert.False(two.Recommendable);
            string text = PlutoDiscoveryScorer.RiskText(two.Risks);
            Assert.Contains("2건 중첩 — 탈락", text);
            Assert.Contains("15호가 매도벽/허매수 함정", text);
            Assert.Contains("과거 약세 국면과 유사", text);
            Assert.Equal("😈 악마의 변호인: 반대 논리 0건 — 통과", PlutoDiscoveryScorer.RiskText(PlutoScreenRisk.None));
        }

        // ── ② 스크리너 스텝 ─────────────────────────────────────────────────

        [Fact]
        public void Step_Ranks_Top5_Excludes_Vetoed_Insufficient_BelowThreshold()
        {
            using var s = ScreenerOf(
                In(1001, 80, 60, 40),                                                                        // 79
                In(1002, -60, 60, 40, MarketRegime.TrendingBear, PlutoValuationGrade.Overvalued),            // 반대 논리 2 = 탈락
                In(1003, null, null, null),                                                                  // 판독 부족
                In(1004, 0, null, null, MarketRegime.Unknown),                                               // 22 = 기준 미달
                In(1005, 60, 60, 40),                                                                        // 77
                In(1000, 60, 60, 40, depthTrap: true),                                                       // 77 · 반대 논리 1 → 1005 뒤
                In(1007, 40, 40, 40),                                                                        // 71
                In(1008, 20, 20, 20),                                                                        // 65
                In(1009, 0, 0, 0),                                                                           // 56 = 6위 → 컷
                In(1010, 100, 100, 100, market: PlutoScreenMarket.Us),                                       // 오배선(시장 불일치) = 무시
                In(1011, 100, 100, 100, price: 0));                                                          // 무시세 = 무시
            var snap = s.Step();
            var krx = snap.Of(PlutoScreenMarket.Krx);
            Assert.Equal(new long[] { 1001, 1005, 1000, 1007, 1008 }, System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Select(krx, r => r.Symbol)));
            Assert.Equal(new[] { 79, 77, 77, 71, 65 }, System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Select(krx, r => r.Score.Total)));
            Assert.Equal(new[] { 1, 2, 3, 4, 5 }, System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Select(krx, r => r.Rank)));
            // [v12.18.79-DEV] Monitored = 감시 카탈로그(KRX 200) · Passed = 기준 통과 6(Top 5 절단 전)
            Assert.Equal(new PlutoScreenTally(9, 1, 1, 1, PlutoMultiAssetUniverseCatalog.KrxCount, 6), snap.TallyOf(PlutoScreenMarket.Krx));
            Assert.Empty(snap.Of(PlutoScreenMarket.Us));                           // 미등록 시장 = 빈 목록(감시 수만 집계)
            Assert.Equal(new PlutoScreenTally(0, 0, 0, 0, PlutoMultiAssetUniverseCatalog.UsCount, 0), snap.TallyOf(PlutoScreenMarket.Us));
            Assert.Equal(1, snap.Generation);
            Assert.Equal(T0, snap.UpdatedUtcTicks);
            Assert.Same(snap, s.Board.Current);
            Assert.Equal(2, s.Step().Generation);
            Assert.Equal(2, s.Board.Published);

            var top = krx[0];
            Assert.Equal("001001", top.Code);
            Assert.Equal(3, top.Reasons.Count);
            Assert.StartsWith("💎 안전마진 +20.0% · 합리적 저평가 · 적정가 62,500원(시드 프로필) [22/25]", top.Reasons[0]);
            Assert.StartsWith("📐 응축 후 상단 돌파 · POC 상단 지지 [20/25]", top.Reasons[1]);   // 기술 20 = 거시 20 동점 → 기술 우선
            Assert.StartsWith("🌐 종목 국면 상승 추세 · 과거 유사 국면 판정 전 [20/25]", top.Reasons[2]);
            Assert.Equal(380, top.RecommendedQty);                                // 1억 × 19%(확신도 79) ÷ 5만 원
            Assert.Equal("😈 악마의 변호인: 반대 논리 0건 — 통과", top.RiskText);
            Assert.Contains("감시 통과", krx[2].RiskText);
        }

        [Fact]
        public void Reasons_Fill_Placeholder_When_Factors_Missing_And_Use_Macro_Episode()
        {
            var macro = new PlutoScreenMacro(PlutoEpisodeKind.Bull, 100, "2024 AI 랠리", true);
            var x = In(7, 90, null, null);
            var r = PlutoDiscoveryScorer.Reasons(x, macro, PlutoDiscoveryScorer.Score(x, macro));
            Assert.Equal(3, r.Length);
            Assert.StartsWith("🌐 종목 국면 상승 추세 · 과거 유사 국면 2024 AI 랠리(시드 근사) 100% [25/25]", r[0]);
            Assert.StartsWith("💎 안전마진", r[1]);
            Assert.Equal("판독 대기 팩터(워밍업 · 0점 반영)", r[2]);
        }

        [Fact]
        public void RecommendedQty_Uses_Dynamic_Sizer_Weight_And_Floors()
        {
            Assert.Equal(380, PlutoMarketScreener.RecommendedQty(100_000_000, 79, 50_000));
            Assert.Equal(0, PlutoMarketScreener.RecommendedQty(100_000_000, 79, 900_000_000));   // 비중 예산 < 1주 = 0
            Assert.Equal(0, PlutoMarketScreener.RecommendedQty(0, 79, 50_000));
            Assert.Equal(0, PlutoMarketScreener.RecommendedQty(100_000_000, 79, 0));
            Assert.Equal(10_000_000L * 2500 / 10000 / 13_500, PlutoMarketScreener.RecommendedQty(10_000_000, 100, 13_500));   // US 센트 · 최대 비중 25%
            Assert.Equal("NVDA", PlutoMarketScreener.CodeOf(PlutoScreenMarket.Us, UsTechUniverse.Pack("NVDA")));
            Assert.Equal("$135.00", PlutoDiscoveryScorer.PriceText(PlutoScreenMarket.Us, 13_500));
            Assert.Equal("70,000원", PlutoDiscoveryScorer.PriceText(PlutoScreenMarket.Krx, 70_000));
        }

        [Fact]
        public void Screener_Rejects_Bad_Registration_And_Loop_Survives_Faults()
        {
            var s = new PlutoMarketScreener(new PlutoRecommendationBoard(), null, () => T0);
            Assert.Throws<ArgumentOutOfRangeException>(() => s.AddUniverse((PlutoScreenMarket)9, _ => 0, 1));
            Assert.Throws<ArgumentOutOfRangeException>(() => s.AddUniverse(PlutoScreenMarket.Krx, _ => 0, 0));
            Assert.Throws<ArgumentNullException>(() => s.AddUniverse(PlutoScreenMarket.Krx, null!, 1));
            int calls = 0;
            s.AddUniverse(PlutoScreenMarket.Us, _ => { if (Interlocked.Increment(ref calls) == 1) throw new InvalidOperationException("피드 장애"); return 0; }, 1_000);
            s.Start(TimeSpan.FromMilliseconds(10));
            var until = DateTime.UtcNow.AddSeconds(5);
            while (s.Cycles < 2 && DateTime.UtcNow < until) Thread.Sleep(5);
            Assert.True(s.Running);
            Assert.Equal(1, s.Faulted);                                          // 장애 = 계수만 · 루프 지속
            Assert.True(s.Cycles >= 2);
            s.Dispose();
            Assert.False(s.Running);
            s.Dispose();                                                          // 멱등
        }

        // ── ③ 스냅샷 · 거시 입력 ─────────────────────────────────────────────

        private static PlutoRecommendation Rec(long sym, int rank, int val) =>
            new(PlutoScreenMarket.Krx, rank, sym, sym.ToString("D6"), 50_000, PlutoDiscoveryScorer.Score(In(sym, val, val, val), default),
                Array.AsReadOnly(new[] { "a", "b", "c" }), true, 2000, 62_500, true, 10, "😈");

        [Fact]
        public void Snapshot_Is_Defensively_Copied_And_TryFind_Works()
        {
            var arr = new[] { Rec(Samsung, 1, 80), Rec(660, 2, 40) };
            var snap = new PlutoRecommendationSnapshot(1, T0, arr, new PlutoScreenTally(2, 0, 0, 0), Array.Empty<PlutoRecommendation>(), default);
            arr[0] = Rec(999, 1, 0);                                             // 게시 후 호출자 배열 변이 = 스냅샷 무영향
            Assert.Equal(Samsung, snap.Of(PlutoScreenMarket.Krx)[0].Symbol);
            Assert.True(snap.TryFind(PlutoScreenMarket.Krx, 660, out int score, out int rank));
            Assert.Equal((Rec(660, 2, 40).Score.Total, 2), (score, rank));
            Assert.False(snap.TryFind(PlutoScreenMarket.Us, Samsung, out score, out rank));
            Assert.Equal((0, 0), (score, rank));
            Assert.Throws<ArgumentOutOfRangeException>(() => new PlutoRecommendationSnapshot(1, T0, new PlutoRecommendation[6], default, Array.Empty<PlutoRecommendation>(), default));
            Assert.Equal(0, PlutoRecommendationSnapshot.Empty.Generation);
            Assert.False(new PlutoRecommendationBoard().TryFind(PlutoScreenMarket.Krx, Samsung, out _, out _));
        }

        [Fact]
        public void Macro_FromLearning_Projects_Nearest_Episode_Or_Default()
        {
            Assert.False(PlutoScreenMacro.FromLearning(null).HasMatch);
            var swarm = new PlutoContinuousLearningSwarm(new PlutoGlobalDataIngestor(() => T0), new PlutoEpisodicMemoryStore(), new PlutoWeightCalibrator(),
                new PlutoCognitiveWeightSeqlock(), () => T0);
            swarm.Memory.SeedDefaults();
            swarm.Step();
            Assert.False(PlutoScreenMacro.FromLearning(swarm.Status).HasMatch);   // 수집 전 = 판정 전
            Assert.True(swarm.Ingestor.Ingest(new PlutoMacroSnapshot(PlutoMacroField.All, 1400, 5, 50, 100, 600, 50, T0)));
            swarm.Step();
            var m = PlutoScreenMacro.FromLearning(swarm.Status);
            Assert.True(m.HasMatch);
            Assert.Equal(PlutoEpisodeKind.Bull, m.Kind);
            Assert.Equal("2024 AI 랠리", m.EpisodeName);
            Assert.Equal(100, m.SimilarityPct);
            Assert.True(m.EpisodeIsSeed);
        }

        // ── ④ 아레나 유니버스 ───────────────────────────────────────────────

        [Fact]
        public void Krx_Arena_Universe_Feeds_Real_Quotes_With_Seed_Valuation()
        {
            var krx = new KrxArenaEngine();
            krx.EnableTechnical(true);
            krx.EnableMicrostructure(true);
            var feeder = new KrxArenaQuoteFeeder(krx);
            Assert.True(feeder.SelectSource(KrxArenaFeed.Synthetic));             // 합성 시세 = 유니버스 전 종목 호가
            for (int i = 0; i < 3; i++) feeder.Pump(Kst10.AddHours(-9).Ticks + i * Sec);
            var buffer = new PlutoValuationBuffer();
            Assert.True(new PlutoValuationEngine(buffer).SeedDefaults() > 0);
            var scan = PlutoArenaUniverse.Krx(krx, buffer, () => T0);
            var dest = new PlutoScreenInputs[PlutoMarketScreener.MaxUniverse];
            int n = scan(dest);
            var quotes = new DuelQuote[KrxArenaEngine.MaxSymbols];
            Assert.Equal(krx.CopyQuotes(quotes), n);
            Assert.True(n >= 5);
            int samsung = Array.FindIndex(dest, 0, n, x => x.Symbol == Samsung);
            Assert.True(samsung >= 0);
            Assert.True(dest[samsung].HasValuation);
            Assert.True(dest[samsung].SeedProfile);
            Assert.Equal(PlutoScreenMarket.Krx, dest[samsung].Market);

            using var s = new PlutoMarketScreener(new PlutoRecommendationBoard(), null, () => T0);
            s.AddUniverse(PlutoScreenMarket.Krx, scan, krx.CapitalWon);
            var snap = s.Step();
            Assert.Equal(n, snap.TallyOf(PlutoScreenMarket.Krx).Scanned);
            foreach (var r in snap.Of(PlutoScreenMarket.Krx))
            {
                Assert.Contains(r.Symbol, System.Linq.Enumerable.Select(new ArraySegment<DuelQuote>(quotes, 0, n), q => q.Symbol));   // 추천 ⊂ 시세 유니버스(날조 0)
                Assert.True(r.Score.Recommendable);
            }

            var us = new UsTechArenaEngine();
            Assert.Equal(0, PlutoArenaUniverse.Us(us, buffer, () => T0)(dest));     // 시세 미수신 = 유니버스 0(가짜 후보 0)
        }

        // ── ⑤ 포트 · 페이지 · 호스트 ─────────────────────────────────────────

        [Fact]
        public void Adapter_Maps_Snapshot_To_Cards_And_Caches_Per_Snapshot()
        {
            using var s = new PlutoMarketScreener(new PlutoRecommendationBoard(), null, () => T0);
            s.AddUniverse(PlutoScreenMarket.Krx, dest => { dest[0] = In(Samsung, 80, 60, 40, price: 70_000); return 1; }, KrxArenaEngine.DefaultCapitalWon);
            s.AddUniverse(PlutoScreenMarket.Us, dest => { dest[0] = In(UsTechUniverse.Pack("NVDA"), 60, 60, 60, market: PlutoScreenMarket.Us, price: 13_500); return 1; },
                UsTechArenaEngine.DefaultCapitalCents);
            var port = new PlutoRecommendationPortAdapter(s);
            Assert.True(port.IsWired);
            Assert.Equal(0, port.Current.Generation);
            Assert.Null(port.Current.UpdatedUtc);
            s.Step();
            var v = port.Current;
            Assert.Same(v, port.Current);                                         // 같은 스냅샷 = 재매핑 0
            Assert.Equal(1, v.Generation);
            Assert.Equal(new DateTime(T0, DateTimeKind.Utc), v.UpdatedUtc);
            var k = Assert.Single(v.Krx.Cards);
            Assert.Equal(("삼성전자", "005930", Samsung, 79, "hot", "강력 추천"), (k.Name, k.Code, k.Symbol, k.Score, k.ScoreCss, k.ScoreLabel));
            Assert.Equal("70,000원", k.PriceText);
            Assert.Equal("🎯 목표 안전마진 +20.0% · 적정가 62,500원(시드 프로필)", k.MosText);
            Assert.Equal(100_000_000L * 1900 / 10000 / 70_000, k.RecommendedQty);
            var u = Assert.Single(v.Us.Cards);
            Assert.Equal((PlutoRecoMarket.Us, "NVDA", "엔비디아", "$135.00"), (u.Market, u.Code, u.Name, u.PriceText));
            Assert.Equal(1, v.Us.Scanned);
            s.Step();
            Assert.NotSame(v, port.Current);
            Assert.Equal("🎯 안전마진 판독 없음(재무 프로필 미보유)", (k with { HasValuation = false }).MosText);
            Assert.Same(PlutoRecoBoardView.None, UnwiredPlutoRecommendationPort.Instance.Current);
        }

        private sealed class FixedRecoPort : IPlutoRecommendationPort
        {
            public FixedRecoPort(PlutoRecoBoardView v) => Current = v;
            public bool IsWired => true;
            public PlutoRecoBoardView Current { get; }
        }

        private static PlutoRecommendationCard Card(int market, long sym, string code, string name, int score, long qty) =>
            new(market, 1, sym, code, name, score, 22, 20, 17, 20, Array.AsReadOnly(new[] { "💎 근거1", "📐 근거2", "🌐 근거3" }),
                true, 2000, "70,000원", "62,500원", true, qty, 0, "😈 악마의 변호인: 반대 논리 0건 — 통과");

        private static (Bunit.TestContext, IRenderedComponent<DualArenaPage>) Render(IPlutoRecommendationPort reco)
        {
            var ctx = new Bunit.TestContext();
            var krx = new KrxArenaEngine();
            var f = new KrxArenaQuoteFeeder(krx);
            Assert.True(f.SelectSource(KrxArenaFeed.Synthetic));                  // 합성 시세 → 국내 휴먼 티켓 렌더
            for (int i = 0; i < 3; i++) f.Pump(Kst10.AddHours(-9).Ticks + i * Sec);
            ctx.Services.AddSingleton<IKrxArenaPort>(new KrxArenaPortAdapter(krx, f));
            var us = new UsTechArenaEngine();
            var uf = new UsArenaQuoteFeeder(us);
            Assert.True(uf.SelectSource(UsArenaFeed.Synthetic));                  // 합성 시세 → 미국 휴먼 티켓 렌더
            for (int i = 0; i < 3; i++) uf.Pump(Kst10.AddHours(-9).Ticks + i * Sec);
            ctx.Services.AddSingleton<IUsArenaPort>(new UsArenaPortAdapter(us, uf));
            ctx.Services.AddSingleton<ICryptoArenaPort>(new CryptoArenaPortAdapter(new UpbitCryptoArenaEngine()));
            ctx.Services.AddSingleton<IArenaPreferencePort>(UnwiredArenaPreferencePort.Instance);
            ctx.Services.AddSingleton<IPlutoLearningPort>(UnwiredPlutoLearningPort.Instance);
            ctx.Services.AddSingleton(reco);
            return (ctx, ctx.RenderComponent<DualArenaPage>());
        }

        [Fact]
        public void DualArenaPage_Reco_Panel_Renders_Cards_And_OneClick_Binds_Ticket()
        {
            var view = new PlutoRecoBoardView(true, 3, new DateTime(T0, DateTimeKind.Utc),
                new PlutoRecoMarketView(Array.AsReadOnly(new[] { Card(PlutoRecoMarket.Krx, Samsung, "005930", "삼성전자", 79, 271), Card(PlutoRecoMarket.Krx, 660, "000660", "SK하이닉스", 61, 0) }), 20, 2, 1, 5),
                new PlutoRecoMarketView(Array.AsReadOnly(new[] { Card(PlutoRecoMarket.Us, UsTechUniverse.Pack("NVDA"), "NVDA", "엔비디아", 72, 14) }), 10, 1, 0, 3));
            var (ctx, cut) = Render(new FixedRecoPort(view));
            using var _ = ctx;
            Assert.Equal("wired", cut.Find(".da-reco").GetAttribute("data-reco"));
            Assert.Contains("🌟 플루토 오늘의 AI 추천 종목", cut.Find(".da-reco h3").TextContent);
            Assert.Contains("스캔 20종목 · 😈 악마의 변호인 탈락 2 · 판독 부족 1 · 기준(50점) 미달 5 · 세대 #3", cut.Find(".da-reco-tally").TextContent);
            var cards = cut.FindAll(".da-reco-card");
            Assert.Equal(2, cards.Count);                                         // 국내 모드 = 국내 탭 추종
            Assert.Equal("79점 · 강력 추천", cut.Find(".da-reco-card[data-code='005930'] .da-reco-score").TextContent.Trim());
            Assert.Contains("hot", cut.Find(".da-reco-card[data-code='005930'] .da-reco-score").ClassList);
            Assert.Equal(3, cut.FindAll(".da-reco-card[data-code='005930'] .da-reco-reasons li").Count);
            Assert.Contains("🎯 목표 안전마진 +20.0%", cut.Find(".da-reco-card[data-code='005930'] .da-reco-mos").TextContent);
            Assert.Equal("0", cut.Find(".da-reco-card[data-code='005930'] .da-reco-devil").GetAttribute("data-devil"));
            Assert.True(cut.Find(".da-reco-card[data-code='000660'] .da-reco-load").HasAttribute("disabled"));   // 추천 수량 0 = 담기 불가

            cut.Find(".da-reco-card[data-code='005930'] .da-reco-load").Click();
            Assert.Equal("5930", cut.Find("select.da-sym").GetAttribute("value"));
            Assert.Equal("271", cut.Find("input.da-qty").GetAttribute("value"));
            Assert.Contains("🌟 플루토 추천 담기: 삼성전자 271주", cut.Find(".da-msg").TextContent);
            Assert.Contains("휴먼 티켓에 담았습니다", cut.Find(".da-reco-msg").TextContent);

            cut.Find(".da-reco-tab[data-reco-tab='us']").Click();
            Assert.Single(cut.FindAll(".da-reco-card"));
            cut.Find(".da-reco-card[data-code='NVDA'] .da-reco-load").Click();
            Assert.Contains("on", cut.Find(".da-mode-tab[data-mode='us']").ClassList);
            Assert.Equal("NVDA", cut.Find("select.da-tkr").GetAttribute("value"));
            Assert.Equal("14", cut.Find("input.da-uqty").GetAttribute("value"));
        }

        [Fact]
        public void DualArenaPage_Reco_Panel_Unwired_And_Empty_Render_Honestly()
        {
            var (ctx, cut) = Render(UnwiredPlutoRecommendationPort.Instance);
            using (ctx)
            {
                Assert.Equal("unwired", cut.Find(".da-reco").GetAttribute("data-reco"));
                Assert.Contains("추천 엔진 미결선", cut.Find(".da-reco-empty").TextContent);
                Assert.Empty(cut.FindAll(".da-reco-card"));
            }
            var (ctx2, cut2) = Render(new FixedRecoPort(PlutoRecoBoardView.None));
            using (ctx2) Assert.Contains("첫 스캔 대기 중", cut2.Find(".da-reco-empty").TextContent);
            var (ctx3, cut3) = Render(new FixedRecoPort(PlutoRecoBoardView.None with { Generation = 1, UpdatedUtc = new DateTime(T0, DateTimeKind.Utc) }));
            using (ctx3) Assert.Contains("근거 없는 추천은 내지 않습니다", cut3.Find(".da-reco-empty").TextContent);
        }

        [Fact]
        public void Host_Wires_Screener_With_Both_Arenas_And_Unwired_Fallback()
        {
            var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "StockManager.sln"))) dir = dir.Parent;
            Assert.NotNull(dir);
            string form = System.IO.File.ReadAllText(System.IO.Path.Combine(dir!.FullName, "StockManager", "Blazor", "BlazorHomeForm.cs"));
            Assert.Contains("cfg.GetBool(\"Pluto.ScreenerEnabled\", true)", form);
            Assert.Contains("PlutoArenaUniverse.Krx(krxArena, plutoValuationBuffer))), krxArena.CapitalWon)", form);   // [v12.18.80-DEV] 매트릭스 병합 + 실 피드 게이트
            Assert.Contains("PlutoArenaUniverse.Us(usArena, plutoValuationBuffer))), usArena.CapitalCents)", form);
            Assert.Contains("PlutoScreenMacro.FromLearning(learningForScreener?.Status)", form);
            Assert.Contains("new StockManager.Adapters.PlutoRecommendationPortAdapter(plutoScreener, plutoSwarm)", form);
            Assert.Contains("MS2E.UI.Portable.Ports.Trading.UnwiredPlutoRecommendationPort.Instance", form);
            string port = System.IO.File.ReadAllText(System.IO.Path.Combine(dir.FullName, "MS2E.UI.Portable", "Ports", "Trading", "PlutoRecommendationPort.cs"));
            Assert.DoesNotContain("StockManager.Engines", port);                  // Pure Shell — 포트 DTO 엔진 타입 참조 0
        }

        // ── ⑥ δ=0 ────────────────────────────────────────────────────────────

        [Fact]
        public void TryFind_During_Concurrent_Publishes_Is_ZeroAlloc_And_Untorn()
        {
            var a = new PlutoRecommendationSnapshot(1, T0, new[] { Rec(Samsung, 1, 80), Rec(660, 2, 40) }, default, Array.Empty<PlutoRecommendation>(), default);
            var b = new PlutoRecommendationSnapshot(2, T0, new[] { Rec(660, 1, 100), Rec(Samsung, 2, 20) }, default, Array.Empty<PlutoRecommendation>(), default);
            int sa = Rec(Samsung, 1, 80).Score.Total, sb = Rec(Samsung, 2, 20).Score.Total;
            var board = new PlutoRecommendationBoard();
            board.Publish(a);
            int stop = 0, torn = 0;
            var writer = new Thread(() =>
            {
                bool flip = false;
                while (Volatile.Read(ref stop) == 0) { board.Publish(flip ? a : b); flip = !flip; }
            }) { IsBackground = true };
            writer.Start();
            void Reads(int n)
            {
                for (int i = 0; i < n; i++)
                {
                    if (!board.TryFind(PlutoScreenMarket.Krx, Samsung, out int score, out int rank)
                        || !((score == sa && rank == 1) || (score == sb && rank == 2))) torn++;
                }
            }
            try
            {
                Reads(10_000);
                long bytes = MinAllocated(() => Reads(100_000));
                Assert.Equal(0, bytes);
            }
            finally
            {
                Volatile.Write(ref stop, 1);
                writer.Join();
            }
            Assert.Equal(0, torn);
            Assert.True(board.Published > 1);
        }

        [Fact]
        public void Krx_Settle_Stays_ZeroAlloc_While_Screener_Scans_Concurrently()
        {
            var e = new KrxArenaEngine();
            e.EnableTechnical(true);
            e.EnableMicrostructure(true);
            var buffer = new PlutoValuationBuffer();
            new PlutoValuationEngine(buffer).SeedDefaults();
            e.AttachValuation(buffer);
            using var screener = new PlutoMarketScreener(new PlutoRecommendationBoard(), null, () => T0);
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
            Assert.True(screener.Cycles > 0);                                     // 스캔이 실제로 정산과 겹쳐 실행됨
            Assert.True(screener.Board.Current.TallyOf(PlutoScreenMarket.Krx).Scanned > 0);
        }
    }
}
