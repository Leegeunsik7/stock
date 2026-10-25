// ============================================================================
// PlutoRegimeVectorLakeTests — v12.18.82-DEV: 12대 역사적 경제 사이클 레짐 벡터 레이크 · 무할당 seqlock 국면 판정기 · 거버너 편향 · 근거 카드/UI 뱃지
//
//   ① 레이크 정합성: 12종 · id/이름 유일 · 정규화 −100~+100 포화 · 사전 정규화 벡터 = Normalize · 자기 매칭 = 100.0%(코사인 1 · 거리 0 · 6차원).
//   ② 유사도 성질: 대칭 · 차원 부족 = −1 · 영벡터 규약 · 방향 반대 = 저유사 · 거리 단조 감소.
//   ③ 극단 장세: 팬데믹급 폭락 → 2020 코로나(방어) · 금리 폭등 → 2022 금리인상(방어) · AI 랠리 → 추세 추종 · 박스권 → 평균 회귀 · 무특징 = 편향 미적용.
//   ④ 편향: 유사도 비례 · 상·하한 포화 · 세대/섀도우 보존 · 위험 국면 = 악마의 변호인 거부권 강화.
//   ⑤ seqlock 판정기 · 스웜 게시 · 포트 투영 · KRX 근거 카드 뱃지 · DualArenaPage 뱃지 · 호스트 결선 문자열.
//   ⑥ δ=0: 레이크 매칭 · 판정기 게시/판독 · 편향 · 거버너 승인 10만 회 = 0B(MinAllocated · Release) · 경합 중 찢어진 판독 0.
// ============================================================================
using System;
using System.Threading;
using System.Threading.Tasks;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MS2E.UI.Portable.Pages.Trading;
using MS2E.UI.Portable.Ports.Trading;
using StockManager.Adapters;
using StockManager.Engines.Arena.Common;
using StockManager.Engines.Arena.Crypto;
using StockManager.Engines.Arena.Domestic;
using StockManager.Engines.Arena.Us;
using StockManager.Engines.Cognitive.Governance;
using StockManager.Engines.Cognitive.Learning;
using StockManager.Engines.Cognitive.Regime;
using MarketRegime = StockManager.Engines.Cognitive.Regime.MarketRegime;   // MS2E.Core.MarketRegime(전역 using) 동명 타입과 구분
using Xunit;
using static StockManager.Tests.ZeroAllocProbe;

namespace StockManager.Tests.Cognitive
{
    [Collection("ConcurrencyStress")]
    public sealed class PlutoRegimeVectorLakeTests
    {
        private const long Samsung = 5930;
        private const int Covid = 8, Hike = 10, Goldilocks2019 = 7, Ai2024 = 12;
        private static readonly DateTime Kst10 = new(2026, 10, 22, 10, 0, 0);
        private static readonly long Sec = TimeSpan.TicksPerSecond;
        private static readonly long T0 = new DateTime(2026, 10, 25, 0, 0, 0, DateTimeKind.Utc).Ticks;

        private static PlutoMacroSnapshot Obs(int vix, int us10y, int dxy, int krw, int trend, int sentiment) =>
            new(PlutoMacroField.All, vix, us10y, dxy, krw, trend, sentiment, T0);

        private static readonly PlutoMacroSnapshot PandemicCrash = Obs(8000, -150, 400, 800, -3500, -90);
        private static readonly PlutoMacroSnapshot RateShock = Obs(2900, 55, 230, 420, -850, -42);
        private static readonly PlutoMacroSnapshot AiRally = Obs(1400, 5, 50, 100, 600, 50);
        private static readonly PlutoMacroSnapshot Featureless = Obs(2000, 0, 0, 0, 0, 0);

        private static PlutoRegimeState StateOf(MarketRegime r) => new() { Regime = r, Seen = 100 };

        // ── ① 레이크 정합성 ───────────────────────────────────────────────────

        [Fact]
        public void Lake_Has_Twelve_Unique_Cycles_Normalized_Within_Bounds_And_Self_Match_Is_100()
        {
            Assert.Equal(12, PlutoRegimeVectorLake.Count);
            Assert.Null(PlutoRegimeVectorLake.Get(0));
            Assert.Null(PlutoRegimeVectorLake.Get(13));
            var names = new System.Collections.Generic.HashSet<string>();
            for (int id = 1; id <= PlutoRegimeVectorLake.Count; id++)
            {
                var c = PlutoRegimeVectorLake.Get(id)!;
                Assert.Equal(id, c.Id);
                Assert.True(names.Add(c.Name));
                Assert.True(c.Raw.IsWellFormed);
                Assert.Equal(PlutoMacroField.All, c.Raw.Fields);
                Assert.NotEqual(PlutoEpisodeKind.Unknown, c.Kind);
                Assert.NotEqual(PlutoRegimeStrategy.None, c.Strategy);
                Assert.False(string.IsNullOrWhiteSpace(c.Note));
                // 위험 국면(패닉 · 약세) = 방어 전략 · 상승/유동성 = 추세 추종 · 박스권 = 평균 회귀
                var expected = c.Kind switch
                {
                    PlutoEpisodeKind.Panic or PlutoEpisodeKind.Bear => PlutoRegimeStrategy.DevilsAdvocate,
                    PlutoEpisodeKind.Bull or PlutoEpisodeKind.Liquidity => PlutoRegimeStrategy.TrendFollow,
                    _ => PlutoRegimeStrategy.MeanReversion,
                };
                Assert.Equal(expected, c.Strategy);

                var raw = c.Raw;
                int[] rawDims = { raw.VixX100, raw.Us10yChangeBp, raw.DollarIndexChangeBp, raw.UsdKrwChangeBp, raw.EquityTrendBp, raw.SentimentIndex };
                for (int d = 0; d < PlutoRegimeVectorLake.Dims; d++)
                {
                    int z = PlutoRegimeVectorLake.NormalizedAt(id - 1, d);
                    Assert.InRange(z, -100, 100);
                    Assert.Equal(PlutoRegimeVectorLake.Normalize(d, rawDims[d]), z);
                }

                var m = PlutoRegimeVectorLake.FindNearest(in raw);
                Assert.Equal(id, m.CycleId);
                Assert.Equal(1000, m.SimilarityX10);
                Assert.Equal(1000, m.CosineX1000);
                Assert.Equal(0, m.DistanceX10);
                Assert.Equal(6, m.Dimensions);
                Assert.Equal(c.Kind, m.Kind);
                Assert.Equal(c.Strategy, m.Strategy);
                Assert.True(m.IsWellFormed);
            }
            Assert.Equal(12, names.Count);

            // 정규화 기준점 · 포화
            Assert.Equal(0, PlutoRegimeVectorLake.Normalize(0, 2000));               // VIX 20 = 중심
            Assert.Equal(100, PlutoRegimeVectorLake.Normalize(0, 6000));             // VIX 60 = +100
            Assert.Equal(100, PlutoRegimeVectorLake.Normalize(0, 20000));            // 포화
            Assert.Equal(-50, PlutoRegimeVectorLake.Normalize(0, 0));
            Assert.Equal(-100, PlutoRegimeVectorLake.Normalize(1, -5000));
            Assert.Equal(60, PlutoRegimeVectorLake.Normalize(1, 60));
            Assert.Equal(-100, PlutoRegimeVectorLake.Normalize(4, -3000));
            Assert.Equal(50, PlutoRegimeVectorLake.Normalize(5, 50));
            Assert.Equal(100, PlutoRegimeVectorLake.NormalizedAt(1, 0));            // 2008 VIX 60
            Assert.Equal(-100, PlutoRegimeVectorLake.NormalizedAt(Covid - 1, 1));   // 2020 10년물 −100bp
            Assert.Equal(-100, PlutoRegimeVectorLake.NormalizedAt(Covid - 1, 4));   // 2020 −30%

            // 다른 국면 간 유사도 < 100%(국면 구별)
            for (int i = 1; i <= 12; i++)
                for (int j = 1; j <= 12; j++)
                {
                    if (i == j) continue;
                    var a = PlutoRegimeVectorLake.Get(i)!.Raw;
                    Assert.True(PlutoRegimeVectorLake.SimilarityX10(in a, j - 1, out _, out _, out _) < 1000);
                }
        }

        // ── ② 유사도 성질 ─────────────────────────────────────────────────────

        [Fact]
        public void Similarity_Is_Symmetric_Bounded_Direction_Aware_And_Monotone()
        {
            for (int i = 1; i <= 12; i++)
                for (int j = 1; j <= 12; j++)
                {
                    var a = PlutoRegimeVectorLake.Get(i)!.Raw;
                    var b = PlutoRegimeVectorLake.Get(j)!.Raw;
                    int ab = PlutoRegimeVectorLake.SimilarityX10(in a, in b, out int dims, out int cos, out int dist);
                    Assert.Equal(ab, PlutoRegimeVectorLake.SimilarityX10(in b, in a, out _, out _, out _));
                    Assert.Equal(ab, PlutoRegimeVectorLake.SimilarityX10(in a, j - 1, out _, out _, out _));   // 사전 정규화 경로 = 범용 경로
                    Assert.InRange(ab, 0, 1000);
                    Assert.Equal(6, dims);
                    Assert.InRange(cos, -1000, 1000);
                    Assert.True(dist >= 0);
                }

            // 공통 차원 1개 = 판정 불가
            var vixOnly = new PlutoMacroSnapshot(PlutoMacroField.Vix, 6000, 0, 0, 0, 0, 0, T0);
            Assert.Equal(-1, PlutoRegimeVectorLake.SimilarityX10(in vixOnly, 0, out int d1, out _, out _));
            Assert.Equal(1, d1);
            Assert.False(PlutoRegimeVectorLake.FindNearest(in vixOnly).Found);
            Assert.False(PlutoRegimeVectorLake.FindNearest(default).Found);          // 형식 불량 = 판정 전

            // 영벡터 규약: 양쪽 영 = 동일(1000) · 한쪽만 영 = 코사인 0(계수 0.5)
            var zero = Featureless;
            Assert.Equal(1000, PlutoRegimeVectorLake.SimilarityX10(in zero, in zero, out _, out int zc, out _));
            Assert.Equal(1000, zc);
            Assert.True(PlutoRegimeVectorLake.SimilarityX10(in zero, in AiRally, out _, out int zc2, out _) <= 500);
            Assert.Equal(0, zc2);

            // 방향 반대 = 저유사(코사인 −1 근방)
            var mirror = Obs(2000 - 3000, 60, 300, 600, 1500, 50);
            var bear = Obs(2000 + 3000, -60, -300, -600, -1500, -50);
            int opp = PlutoRegimeVectorLake.SimilarityX10(in mirror, in bear, out _, out int oc, out _);
            Assert.Equal(-1000, oc);
            Assert.Equal(0, opp);

            // 거리 단조: 2020 코로나 원값에서 같은 방향으로 멀어질수록 유사도 비증가
            var covid = PlutoRegimeVectorLake.Get(Covid)!.Raw;
            int prev = 1001;
            for (int k = 0; k <= 10; k++)
            {
                var drift = Obs(covid.VixX100 - 300 * k, covid.Us10yChangeBp + 10 * k, covid.DollarIndexChangeBp - 30 * k, covid.UsdKrwChangeBp - 60 * k,
                    covid.EquityTrendBp + 250 * k, covid.SentimentIndex + 7 * k);
                int sim = PlutoRegimeVectorLake.SimilarityX10(in drift, Covid - 1, out _, out _, out _);
                Assert.True(sim <= prev, $"k={k} sim={sim} prev={prev}");
                prev = sim;
            }
            Assert.True(prev < 300);

            // 에피소딕 기억 척도 = 레이크 척도 % 반올림(두 뱃지 일치)
            var hike = PlutoRegimeVectorLake.Get(Hike)!.Raw;
            int x10 = PlutoRegimeVectorLake.SimilarityX10(in RateShock, in hike, out _, out _, out _);
            Assert.Equal((x10 + 5) / 10, PlutoEpisodicMemoryStore.Similarity(in RateShock, in hike, out _));
        }

        // ── ③ 극단 장세 매칭 · ④ 편향 ────────────────────────────────────────

        [Fact]
        public void Extreme_Regimes_Match_Expected_Cycles_And_Map_To_Strategy_Bias()
        {
            var panic = PlutoRegimeVectorLake.FindNearest(in PandemicCrash);
            Assert.Equal(Covid, panic.CycleId);
            Assert.Equal("2020 코로나 팬데믹 폭락", PlutoRegimeVectorLake.Get(panic.CycleId)!.Name);
            Assert.Equal(PlutoEpisodeKind.Panic, panic.Kind);
            Assert.Equal(PlutoRegimeStrategy.DevilsAdvocate, panic.Strategy);
            Assert.InRange(panic.SimilarityX10, 850, 999);
            Assert.True(panic.Biases);
            Assert.Equal(10 * panic.SimilarityX10 / 1000, panic.StrictnessBiasPts);
            Assert.Equal(0, panic.MomentumTiltBiasPct);

            var hike = PlutoRegimeVectorLake.FindNearest(in RateShock);
            Assert.Equal(Hike, hike.CycleId);
            Assert.Equal(PlutoEpisodeKind.Bear, hike.Kind);
            Assert.InRange(hike.SimilarityX10, 950, 999);
            Assert.Equal(9, hike.StrictnessBiasPts);
            Assert.Equal($"[🧠 과거 국면 매칭: 2022 연준 금리인상 충격 (유사도 {hike.SimilarityX10 / 10}.{hike.SimilarityX10 % 10}%)]", PlutoRegimeText.Badge(in hike));
            Assert.Equal("악마의 변호인 강화(방어) → 엄격도 +9점", PlutoRegimeText.Effect(in hike));
            Assert.StartsWith("2022.01–2022.10 · 자이언트 스텝", PlutoRegimeText.Explain(in hike));

            var ai = PlutoRegimeVectorLake.FindNearest(in AiRally);
            Assert.Equal(Ai2024, ai.CycleId);
            Assert.Equal(1000, ai.SimilarityX10);
            Assert.Equal(PlutoRegimeStrategy.TrendFollow, ai.Strategy);
            Assert.Equal(20, ai.MomentumTiltBiasPct);
            Assert.Equal(0, ai.StrictnessBiasPts);
            Assert.Equal("추세 추종 우위 → 모멘텀 +20%", PlutoRegimeText.Effect(in ai));

            var range = PlutoRegimeVectorLake.FindNearest(PlutoRegimeVectorLake.Get(Goldilocks2019)!.Raw);
            Assert.Equal(PlutoRegimeStrategy.MeanReversion, range.Strategy);
            Assert.Equal(-20, range.MomentumTiltBiasPct);

            // 무특징 관측 = 최근접은 있으나 유사도 60% 미만 → 편향 미적용
            var flat = PlutoRegimeVectorLake.FindNearest(in Featureless);
            Assert.True(flat.Found);
            Assert.True(flat.SimilarityX10 < PlutoRegimeMatch.MinBiasSimilarityX10);
            Assert.False(flat.Biases);
            Assert.Equal(0, flat.MomentumTiltBiasPct + flat.StrictnessBiasPts);
            var learned = new PlutoCognitiveWeights(3, 10, 4, 6000, 300, 25);
            var same = flat.Bias(in learned);
            Assert.Equal(10, same.MomentumTiltPct);
            Assert.Equal(4, same.DevilStrictnessPts);
            Assert.EndsWith("편향 미적용(유사도 60% 미만)", PlutoRegimeText.Effect(in flat));

            // 편향 = 학습 가중치 가산 · 포화 · 세대/섀도우 보존
            var b = hike.Bias(in learned);
            Assert.Equal(3, b.Generation);
            Assert.Equal(13, b.DevilStrictnessPts);
            Assert.Equal(10, b.MomentumTiltPct);
            Assert.Equal(6000, b.ShadowWinRateBp);
            Assert.Equal(300, b.ShadowMddBp);
            Assert.Equal(25, b.ShadowSamples);
            Assert.True(b.IsWellFormed);
            Assert.Equal(PlutoCognitiveWeights.MaxStrictnessPts, hike.Bias(new PlutoCognitiveWeights(1, 0, 18)).DevilStrictnessPts);
            Assert.Equal(PlutoCognitiveWeights.MaxMomentumTiltPct, ai.Bias(new PlutoCognitiveWeights(1, 45, 0)).MomentumTiltPct);
            Assert.Equal(-PlutoCognitiveWeights.MaxMomentumTiltPct, range.Bias(new PlutoCognitiveWeights(1, -45, 0)).MomentumTiltPct);
            PlutoRegimeMatch none = default;
            Assert.True(none.IsWellFormed);
            Assert.False(none.Biases);
            Assert.Equal("[🧠 과거 국면 매칭: 판정 전]", PlutoRegimeText.Badge(in none));
            Assert.Equal("", PlutoRegimeText.Effect(in none));
            // 무결성: 국면 id 와 유형/전략 불일치 = 위반
            Assert.False(new PlutoRegimeMatch(Hike, 900, 900, 10, 6, PlutoEpisodeKind.Bull, PlutoRegimeStrategy.TrendFollow, T0).IsWellFormed);
            Assert.False(new PlutoRegimeMatch(13, 900, 900, 10, 6, PlutoEpisodeKind.Bear, PlutoRegimeStrategy.DevilsAdvocate, T0).IsWellFormed);
            Assert.False(new PlutoRegimeMatch(Hike, 1001, 900, 10, 6, PlutoEpisodeKind.Bear, PlutoRegimeStrategy.DevilsAdvocate, T0).IsWellFormed);
        }

        [Fact]
        public void Governor_Danger_Regime_Strengthens_Devil_Veto_And_Tags_History()
        {
            var sizer = new PlutoSizerState();
            long now = Kst10.Ticks;
            var hike = PlutoRegimeVectorLake.FindNearest(in RateShock);
            // 점수 3800 → 원확신도 38 → 중립 승인
            Assert.Equal(PlutoBlockReason.None, PlutoCognitiveGovernor.Approve(StateOf(MarketRegime.ChoppyRange), 3800, 0, 25, 0, 35, 3000, 1000, 1000, 0, 0, in sizer, now, out var t0));
            Assert.False(t0.History.Found);
            // 2022 금리인상 국면(유사도 ≥ 95%) 편향 엄격도 +9 → 원확신도 29 < 30 → 악마의 변호인 거부권
            var biased = hike.Bias(default);
            Assert.Equal(PlutoBlockReason.CounterThesisVeto, PlutoCognitiveGovernor.Approve(StateOf(MarketRegime.ChoppyRange), 3800, 0, 25, 0, 35, 3000, 1000, 1000, 0, 0, in sizer, now,
                out _, learned: in biased, history: in hike));
            // 점수 5000 → 원확신도 41 → 승인 · 진입 태그에 국면 결속 · 무결성 · 문장 뱃지
            Assert.Equal(PlutoBlockReason.None, PlutoCognitiveGovernor.Approve(StateOf(MarketRegime.ChoppyRange), 5000, 0, 25, 0, 35, 3000, 1000, 1000, 0, 0, in sizer, now,
                out var t1, learned: in biased, history: in hike));
            Assert.Equal(41, t1.RawConfidence);
            Assert.Equal(Hike, t1.History.CycleId);
            Assert.True(t1.IsWellFormed);
            string text = PlutoGovernanceText.Compose(in t1);
            Assert.Contains(PlutoRegimeText.Badge(in hike), text);
            Assert.EndsWith("악마의 변호인 강화(방어) → 엄격도 +9점.", text);
            // 추세 추종 국면 = 모멘텀 의존 진입 승격(2900 + 4000 × 25 × 20% = 3100)
            var ai = PlutoRegimeVectorLake.FindNearest(in AiRally);
            Assert.Equal(PlutoBlockReason.BelowEntryScore, PlutoCognitiveGovernor.Approve(StateOf(MarketRegime.Unknown), 2900, 4000, 25, 0, 35, 3000, 1000, 1000, 0, 0, in sizer, now, out _));
            var trend = ai.Bias(default);
            Assert.Equal(PlutoBlockReason.None, PlutoCognitiveGovernor.Approve(StateOf(MarketRegime.Unknown), 2900, 4000, 25, 0, 35, 3000, 1000, 1000, 0, 0, in sizer, now,
                out var t2, learned: in trend, history: in ai));
            Assert.Equal(3100, t2.AdjustedScore);
            Assert.True(t2.IsWellFormed);
            // 패닉 차단은 국면 편향과 무관하게 우선
            Assert.Equal(PlutoBlockReason.PanicRegime, PlutoCognitiveGovernor.Approve(StateOf(MarketRegime.PanicLiquidation), 9000, 4000, 25, 0, 35, 3000, 1000, 1000, 0, 0, in sizer, now,
                out _, learned: in trend, history: in ai));
        }

        // ── ⑤ 판정기 · 스웜 · 포트 · 엔진 · UI ──────────────────────────────

        [Fact]
        public void Matcher_Publishes_Reads_And_Clears_Atomically()
        {
            var m = new PlutoRegimeMatcher();
            Assert.True(m.TryRead(out var none));
            Assert.False(none.Found);
            var p = m.Publish(in PandemicCrash);
            Assert.Equal(Covid, p.CycleId);
            Assert.True(m.TryRead(out var r));
            Assert.Equal(p.CycleId, r.CycleId);
            Assert.Equal(p.SimilarityX10, r.SimilarityX10);
            Assert.Equal(T0, r.AsOfUtcTicks);
            Assert.Equal(1, m.Published);
            Assert.Equal(2, m.Version);
            Assert.False(m.Publish(default).Found);                                 // 형식 불량 관측 = 판정 전 게시(직전 매칭 잔존 0)
            Assert.True(m.TryRead(out var cleared));
            Assert.False(cleared.Found);
            m.Publish(in AiRally);
            m.Clear();
            Assert.True(m.TryRead(out var c2));
            Assert.False(c2.Found);
            Assert.Equal(4, m.Published);
        }

        [Fact]
        public void Swarm_Publishes_Regime_And_Port_Projects_Badge()
        {
            var matcher = new PlutoRegimeMatcher();
            var mem = new PlutoEpisodicMemoryStore();
            mem.SeedDefaults();
            Assert.Equal(12, mem.Count);
            var swarm = new PlutoContinuousLearningSwarm(new PlutoGlobalDataIngestor(() => T0), mem, new PlutoWeightCalibrator(), new PlutoCognitiveWeightSeqlock(), () => T0,
                matcher);
            Assert.Same(matcher, swarm.RegimeMatcher);
            var first = swarm.Step();
            Assert.False(first.Regime.Found);                                        // 수집 전 = 판정 전
            Assert.Equal(0, matcher.Published);

            Assert.True(swarm.Ingestor.Ingest("""{"vix":29,"us10yChangeBp":55,"dxyChangeBp":230,"usdKrwChangeBp":420,"equityTrendBp":-850,"sentiment":-42}"""u8));
            var st = swarm.Step();
            Assert.Equal(Hike, st.Regime.CycleId);
            Assert.Equal(Hike, st.Match.EpisodeId);                                   // 에피소딕 기억(레이크 시드 · 같은 척도) 과 일치
            Assert.Equal(st.Regime.SimilarityPct, st.Match.SimilarityPct);
            Assert.True(matcher.TryRead(out var live));
            Assert.Equal(Hike, live.CycleId);

            var view = PlutoLearningPortAdapter.Map(swarm.Status, running: true);
            Assert.True(view.HasRegime);
            Assert.Equal("2022 연준 금리인상 충격", view.RegimeName);
            Assert.Equal(st.Regime.SimilarityX10, view.RegimeSimilarityX10);
            Assert.Equal(PlutoRegimeText.Badge(st.Regime), view.RegimeBadgeText);
            Assert.Equal("악마의 변호인 강화(방어) → 엄격도 +9점", view.RegimeEffect);
            Assert.StartsWith("2022.01–2022.10 · ", view.RegimeNote);
            Assert.Equal("[🧠 과거 국면 매칭: 판정 전]", PlutoLearningPortAdapter.Map(null, running: false).RegimeBadgeText);
            Assert.False(UnwiredPlutoLearningPort.Instance.Current.HasRegime);
        }

        [Fact]
        public void Krx_Arena_Entry_Rationale_Carries_Historical_Regime_Badge()
        {
            var matcher = new PlutoRegimeMatcher();
            matcher.Publish(in AiRally);
            var e = new KrxArenaEngine();
            e.EnableGovernance(true);
            Assert.False(e.RegimeMatcherAttached);
            e.AttachRegimeMatcher(matcher);
            Assert.True(e.RegimeMatcherAttached);
            Assert.Throws<ArgumentNullException>(() => e.AttachRegimeMatcher(null!));
            var kt = Kst10;
            for (int j = 0; j < 4000; j++)
            {
                kt = kt.AddTicks(Sec * 2);
                long px = 68_000 + ((j >> 6) & 63) * 100 - (j & 1) * 100;
                e.OnQuote(Samsung + (j & 3), px, px + 100, (j & 32) != 0 ? 9000 : 1000, 1000, 100 + (j & 63) * ((j & 1) + 1), kt);
            }
            var buf = new AiTradeRationale[256];
            int n = e.CopyRationales(buf);
            int tagged = 0;
            for (int i = 0; i < n; i++)
            {
                ref readonly var r = ref buf[i];
                Assert.True(r.IsWellFormed);
                if (!r.Governance.IsEntry) continue;
                Assert.Equal(Ai2024, r.Governance.History.CycleId);
                Assert.Contains("[🧠 과거 국면 매칭: 2024 AI 유동성 랠리 (유사도 100.0%)] 추세 추종 우위 → 모멘텀 +20%.", r.HumanReadableSummary);
                tagged++;
            }
            Assert.True(tagged > 0);
        }

        [Fact]
        public void DualArenaPage_Renders_Regime_Badge()
        {
            var matcher = new PlutoRegimeMatcher();
            var swarm = new PlutoContinuousLearningSwarm(new PlutoGlobalDataIngestor(() => T0), new PlutoEpisodicMemoryStore(), new PlutoWeightCalibrator(),
                new PlutoCognitiveWeightSeqlock(), () => T0, matcher);
            swarm.Memory.SeedDefaults();
            Assert.True(swarm.Ingestor.Ingest(in RateShock));
            swarm.Step();
            int x10 = swarm.Status!.Regime.SimilarityX10;
            using var ctx = new Bunit.TestContext();
            var krx = new KrxArenaEngine();
            var f = new KrxArenaQuoteFeeder(krx);
            f.Pump(Kst10.AddHours(-9).Ticks);
            ctx.Services.AddSingleton<IKrxArenaPort>(new KrxArenaPortAdapter(krx, f));
            ctx.Services.AddSingleton<IUsArenaPort>(new UsArenaPortAdapter(new UsTechArenaEngine()));
            ctx.Services.AddSingleton<ICryptoArenaPort>(new CryptoArenaPortAdapter(new UpbitCryptoArenaEngine()));
            ctx.Services.AddSingleton<IArenaPreferencePort>(UnwiredArenaPreferencePort.Instance);
            ctx.Services.AddSingleton<IPlutoLearningPort>(new PlutoLearningPortAdapter(swarm));
            ctx.Services.AddSingleton<IPlutoRecommendationPort>(UnwiredPlutoRecommendationPort.Instance);
            var cut = ctx.RenderComponent<DualArenaPage>();
            var badge = cut.Find(".da-regime");
            Assert.Equal($"[🧠 과거 국면 매칭: 2022 연준 금리인상 충격 (유사도 {x10 / 10}.{x10 % 10}%)] 악마의 변호인 강화(방어) → 엄격도 +9점", badge.TextContent.Trim());
            Assert.Equal(x10.ToString(System.Globalization.CultureInfo.InvariantCulture), badge.GetAttribute("data-regime"));
            Assert.StartsWith("2022.01–2022.10 · ", badge.GetAttribute("title"));
            Assert.Single(cut.FindAll(".da-learn"));                                // 기존 학습 뱃지와 별개
        }

        [Fact]
        public void Host_Wires_Regime_Matcher_To_Swarm_And_All_Three_Arenas()
        {
            var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "StockManager.sln"))) dir = dir.Parent;
            Assert.NotNull(dir);
            string form = System.IO.File.ReadAllText(System.IO.Path.Combine(dir!.FullName, "StockManager", "Blazor", "BlazorHomeForm.cs"));
            Assert.Contains("new StockManager.Engines.Cognitive.Learning.PlutoRegimeMatcher()", form);
            Assert.Contains("regime: plutoRegime", form);
            Assert.Contains("krxArena.AttachRegimeMatcher(plutoRegime);", form);
            Assert.Contains("usArena.AttachRegimeMatcher(plutoRegime);", form);
            Assert.Contains("cryptoArena.AttachRegimeMatcher(plutoRegime);", form);
        }

        // ── ⑥ δ=0 ────────────────────────────────────────────────────────────

        [Fact]
        public void Lake_Matcher_Bias_And_Governor_Are_Zero_Alloc_And_Untorn()
        {
            var snaps = new[] { PandemicCrash, RateShock, AiRally, Featureless };
            long acc = 0;
            void Match(int n)
            {
                for (int i = 0; i < n; i++)
                {
                    var m = PlutoRegimeVectorLake.FindNearest(in snaps[i & 3]);
                    acc += m.CycleId + m.SimilarityX10;
                }
            }
            Match(10_000);
            Assert.Equal(0, MinAllocated(() => Match(100_000)));

            var matcher = new PlutoRegimeMatcher();
            void Publish(int n) { for (int i = 0; i < n; i++) acc += matcher.Publish(in snaps[i & 3]).CycleId; }
            Publish(10_000);
            Assert.Equal(0, MinAllocated(() => Publish(100_000)));

            var weights = new PlutoCognitiveWeightSeqlock();
            Assert.True(weights.TrySwap(new PlutoCognitiveWeights(0, 10, 5), shadowPassed: true));
            var sizer = new PlutoSizerState();
            var regime = StateOf(MarketRegime.TrendingBull);
            void ReadBiasGovern(int n)
            {
                for (int i = 0; i < n; i++)
                {
                    weights.TryRead(out var w);
                    matcher.TryRead(out var h);
                    var learned = h.Bias(in w);
                    var r = PlutoCognitiveGovernor.Approve(in regime, 3000 + (i & 2047), (i & 255) * 10, 25, (i & 127) * 5, 35, 3000, 1000 + (i & 7), 1000, i & 15, 0, in sizer, i,
                        out var tag, learned: in learned, history: in h);
                    acc += (int)r + tag.WeightBp + tag.History.CycleId;
                }
            }
            ReadBiasGovern(10_000);
            Assert.Equal(0, MinAllocated(() => ReadBiasGovern(100_000)));
            Assert.True(acc > 0);

            // 경합: 작성자가 두 국면을 번갈아 게시하는 동안 판독 = 항상 레이크 일관(찢어진 판독 0)
            matcher.Publish(in snaps[0]);
            var stop = new ManualResetEventSlim(false);
            using var joined = new CountdownEvent(1);
            var writer = new Thread(() =>
            {
                int k = 0;
                while (!stop.IsSet) matcher.Publish(in snaps[k++ & 1]);
                joined.Signal();
            }) { IsBackground = true };
            writer.Start();
            long torn = 0, reads = 0;
            void Reads(int n)
            {
                for (int i = 0; i < n; i++)
                {
                    if (!matcher.TryRead(out var m)) continue;
                    reads++;
                    if (!m.IsWellFormed || (m.CycleId != Covid && m.CycleId != Hike)) torn++;
                }
            }
            Reads(10_000);
            long bytes = MinAllocated(() => Reads(100_000));
            stop.Set();
            Assert.True(joined.Wait(TimeSpan.FromSeconds(10)));
            stop.Dispose();
            Assert.Equal(0, torn);
            Assert.True(reads > 0);
            Assert.Equal(0, bytes);
        }
    }
}
