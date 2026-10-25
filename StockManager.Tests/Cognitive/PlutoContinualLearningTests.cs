// ============================================================================
// PlutoContinualLearningTests — v12.18.77-DEV: 플루토 지속 학습 2-트랙(Track A δ=0 판독 · Track B 수집/연상/보정/섀도우/핫스왑)
//
//   ① 가중치 · seqlock: 중립 default · tilt/엄격도 산식 · 범위 · 섀도우 불통과/범위 밖 = 스왑 거부 · 세대 증가.
//   ② 거버너 결속: 엄격도 +20 = 확신도 거부권 · 모멘텀 tilt +50 = 진입 점수 통과 · 태그 무결성 불변.
//   ③ 글로벌 인제스천: JSON 파싱 · 헤드라인 감성 지수 · 형식 오류/범위 밖 = 거부 + 직전 안정값 유지 · 필드 병합 · Stale · 수집원 장애 Fail-Safe.
//   ④ 에피소딕 기억: 시드 12종(v12.18.82 레이크) · 동일 특성 = 100% · 거리 단조 · 차원 부족 = 판정 불가 · 용량.
//   ⑤ 보정기: 표본 부족 = 제안 없음 · 교훈 압력 방향 · 세대당 상한 · 섀도우 통과/승률 악화/홀드아웃 부족.
//   ⑥ 스웜: 근거 링 수확(중복 0) · 진입 확신도 결합 · 국면 매칭 · 거시 역풍 주입 · 핫스왑 · 포트 투영 · DualArenaPage 뱃지(결선/미결선) · 호스트 결선 문자열.
//   ⑦ δ=0: 핫스왑 경합 중 10만 회 판독 · 거버너 10만 회 · 학습 결속 KRX 정산 10만 회 = 0B(MinAllocated · Release) + 찢어진 판독 0.
// ============================================================================
using System;
using System.Collections.Generic;
using System.Text;
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
using StockManager.Engines.Cognitive.News;
using StockManager.Engines.Cognitive.Regime;
using MarketRegime = StockManager.Engines.Cognitive.Regime.MarketRegime;   // MS2E.Core.MarketRegime(전역 using) 동명 타입과 구분
using Xunit;
using static StockManager.Tests.ZeroAllocProbe;

namespace StockManager.Tests.Cognitive
{
    [Collection("ConcurrencyStress")]
    public sealed class PlutoContinualLearningTests
    {
        private const long Samsung = 5930;
        private static readonly DateTime Kst10 = new(2026, 10, 22, 10, 0, 0);
        private static readonly long Sec = TimeSpan.TicksPerSecond;
        private static readonly long T0 = new DateTime(2026, 10, 25, 0, 0, 0, DateTimeKind.Utc).Ticks;

        private static PlutoRegimeState StateOf(MarketRegime r) => new() { Regime = r, Seen = 100 };

        private static readonly PlutoMacroSnapshot AiRally = new(PlutoMacroField.All, 1400, 5, 50, 100, 600, 50, T0);

        // ── ① 가중치 · seqlock ─────────────────────────────────────────────────

        [Fact]
        public void Weights_Default_Is_Neutral_And_Formulas_Hold()
        {
            PlutoCognitiveWeights n = default;
            Assert.True(n.IsNeutral);
            Assert.True(n.IsWellFormed);
            Assert.Equal(3210, n.ApplyMomentumTilt(3210, 4000, 25));
            Assert.Equal(45, n.ApplyStrictness(45));

            var w = new PlutoCognitiveWeights(0, 50, 20);
            Assert.Equal(2900 + 4000 * 25 * 50 / 10000, w.ApplyMomentumTilt(2900, 4000, 25));
            Assert.Equal(10000, w.ApplyMomentumTilt(9900, 10000, 100));          // 포화
            Assert.Equal(25, w.ApplyStrictness(45));
            Assert.Equal(0, w.ApplyStrictness(10));                              // 하한 0
            Assert.Equal(100, new PlutoCognitiveWeights(0, 0, -10).ApplyStrictness(95));

            Assert.False(new PlutoCognitiveWeights(0, 51, 0).IsWellFormed);
            Assert.False(new PlutoCognitiveWeights(0, 0, 21).IsWellFormed);
            Assert.False(new PlutoCognitiveWeights(0, 0, -11).IsWellFormed);
        }

        [Fact]
        public void Seqlock_Swap_Requires_Shadow_Pass_And_Bumps_Generation()
        {
            var sl = new PlutoCognitiveWeightSeqlock();
            Assert.True(sl.TryRead(out var w0));
            Assert.True(w0.IsNeutral);
            Assert.Equal(0, w0.Generation);

            Assert.False(sl.TrySwap(new PlutoCognitiveWeights(0, 5, 2), shadowPassed: false));
            Assert.False(sl.TrySwap(new PlutoCognitiveWeights(0, 99, 2), shadowPassed: true));
            Assert.Equal(2, sl.RejectedSwaps);
            Assert.True(sl.TryRead(out var w1));
            Assert.True(w1.IsNeutral);

            Assert.True(sl.TrySwap(new PlutoCognitiveWeights(77, 5, 2), shadowPassed: true));
            Assert.True(sl.TrySwap(new PlutoCognitiveWeights(0, 10, 4), shadowPassed: true));
            Assert.True(sl.TryRead(out var w2));
            Assert.Equal(2, w2.Generation);                                      // 후보의 세대 값은 무시 · 발행 순서대로 +1
            Assert.Equal(10, w2.MomentumTiltPct);
            Assert.Equal(4, w2.DevilStrictnessPts);
            Assert.Equal(2, sl.Swaps);
            Assert.Equal(0, sl.Version & 1);
        }

        // ── ② 거버너 결속 ─────────────────────────────────────────────────────

        [Fact]
        public void Governor_Learned_Strictness_Vetoes_And_Tilt_Promotes()
        {
            var sizer = new PlutoSizerState();
            long now = Kst10.Ticks;
            // 중립: 점수 4500 → 원확신도 45 → 승인
            Assert.Equal(PlutoBlockReason.None, PlutoCognitiveGovernor.Approve(StateOf(MarketRegime.ChoppyRange), 4500, 0, 25, 0, 35, 3000, 1000, 1000, 0, 0, in sizer, now, out var t0));
            Assert.Equal(45, t0.RawConfidence);
            Assert.True(t0.IsWellFormed);
            // 학습 엄격도 +20 → 원확신도 25 < 30 → 거부권
            var strict = new PlutoCognitiveWeights(1, 0, 20);
            Assert.Equal(PlutoBlockReason.CounterThesisVeto, PlutoCognitiveGovernor.Approve(StateOf(MarketRegime.ChoppyRange), 4500, 0, 25, 0, 35, 3000, 1000, 1000, 0, 0, in sizer, now, out _,
                learned: in strict));
            // 엄격도 +10 → 원확신도 35 → 승인 · 태그 무결성(감점 합 · 확신도 산식) 불변
            var mild = new PlutoCognitiveWeights(1, 0, 10);
            Assert.Equal(PlutoBlockReason.None, PlutoCognitiveGovernor.Approve(StateOf(MarketRegime.ChoppyRange), 4500, 0, 25, 0, 35, 3000, 1000, 1000, 0, 0, in sizer, now, out var t1,
                learned: in mild));
            Assert.Equal(35, t1.RawConfidence);
            Assert.True(t1.IsWellFormed);

            // 모멘텀 tilt: 점수 2900(미달) + 모멘텀 4000bp × 가중 25 × 50% = +500 → 3400 승인
            Assert.Equal(PlutoBlockReason.BelowEntryScore, PlutoCognitiveGovernor.Approve(StateOf(MarketRegime.Unknown), 2900, 4000, 25, 0, 35, 3000, 1000, 1000, 0, 0, in sizer, now, out _));
            var tilt = new PlutoCognitiveWeights(1, 50, 0);
            Assert.Equal(PlutoBlockReason.None, PlutoCognitiveGovernor.Approve(StateOf(MarketRegime.Unknown), 2900, 4000, 25, 0, 35, 3000, 1000, 1000, 0, 0, in sizer, now, out var t2,
                learned: in tilt));
            Assert.Equal(3400, t2.AdjustedScore);
            Assert.True(t2.IsWellFormed);
            // 음의 tilt 는 모멘텀 의존 진입을 떨어뜨린다
            var distrust = new PlutoCognitiveWeights(1, -50, 0);
            Assert.Equal(PlutoBlockReason.BelowEntryScore, PlutoCognitiveGovernor.Approve(StateOf(MarketRegime.Unknown), 3200, 4000, 25, 0, 35, 3000, 1000, 1000, 0, 0, in sizer, now, out _,
                learned: in distrust));
            // 패닉 차단은 학습과 무관하게 우선
            Assert.Equal(PlutoBlockReason.PanicRegime, PlutoCognitiveGovernor.Approve(StateOf(MarketRegime.PanicLiquidation), 9000, 4000, 25, 0, 35, 3000, 1000, 1000, 0, 0, in sizer, now, out _,
                learned: in tilt));
        }

        // ── ③ 글로벌 인제스천 ─────────────────────────────────────────────────

        [Fact]
        public void Ingestor_Parses_Full_Json_And_Derives_Headline_Sentiment()
        {
            var json = """{"vix":14.25,"us10yChangeBp":5,"dxyChangeBp":50,"usdKrwChangeBp":-30,"equityTrendBp":600,"asOfUtc":"2026-10-25T00:00:00Z","extra":{"a":[1,2]}}"""u8;
            Assert.True(PlutoGlobalDataIngestor.TryParse(json, 1, out var s));
            Assert.Equal(PlutoMacroField.All & ~PlutoMacroField.Sentiment, s.Fields);
            Assert.Equal(1425, s.VixX100);
            Assert.Equal(5, s.Us10yChangeBp);
            Assert.Equal(50, s.DollarIndexChangeBp);
            Assert.Equal(-30, s.UsdKrwChangeBp);
            Assert.Equal(600, s.EquityTrendBp);
            Assert.Equal(T0, s.AsOfUtcTicks);

            string[] heads = { "삼성전자 대규모 공급계약 체결", "삼성전자 유상증자 결정", "삼성전자 찌라시 유상증자" };
            long wsum = 0, wt = 0;
            foreach (var h in heads)
            {
                var v = PlutoCatalystAnalyzer.Classify(PlutoSource.Wire, h);
                if (v.IsNoise) continue;
                wsum += (long)v.Score * v.Confidence; wt += v.Confidence;
            }
            Assert.True(wt > 0);
            var body = Encoding.UTF8.GetBytes("{\"vix\":20,\"headlines\":[\"" + string.Join("\",\"", heads) + "\"]}");
            Assert.True(PlutoGlobalDataIngestor.TryParse(body, T0, out var s2));
            Assert.True(s2.Has(PlutoMacroField.Sentiment));
            Assert.Equal((int)Math.Clamp(wsum / wt, -100, 100), s2.SentimentIndex);
            Assert.Equal(T0, s2.AsOfUtcTicks);                                   // asOfUtc 없음 = now

            // 명시 sentiment 가 헤드라인보다 우선
            Assert.True(PlutoGlobalDataIngestor.TryParse("""{"sentiment":-12,"headlines":["삼성전자 대규모 공급계약 체결"]}"""u8, T0, out var s3));
            Assert.Equal(-12, s3.SentimentIndex);
            // 노이즈 헤드라인뿐 + 다른 필드 0 = 필드 없음 → 거부
            Assert.False(PlutoGlobalDataIngestor.TryParse("""{"headlines":["삼성전자 찌라시 유상증자"]}"""u8, T0, out _));
        }

        [Theory]
        [InlineData("[1,2]")]
        [InlineData("{\"vix\":-1}")]
        [InlineData("{\"vix\":250}")]
        [InlineData("{\"vix\":\"14\"}")]
        [InlineData("{\"us10yChangeBp\":5001}")]
        [InlineData("{\"us10yChangeBp\":1.5}")]
        [InlineData("{\"sentiment\":101}")]
        [InlineData("{\"asOfUtc\":\"not-a-date\",\"vix\":10}")]
        [InlineData("{\"headlines\":[1]}")]
        [InlineData("{\"vix\":10")]
        [InlineData("{}")]
        [InlineData("")]
        public void Ingestor_Rejects_Malformed_Or_Out_Of_Range(string json) =>
            Assert.False(PlutoGlobalDataIngestor.TryParse(Encoding.UTF8.GetBytes(json), T0, out _));

        [Fact]
        public void Ingestor_FailSafe_Keeps_Last_Stable_Merges_Fields_And_Marks_Stale()
        {
            long now = T0;
            var ing = new PlutoGlobalDataIngestor(() => now, TimeSpan.FromMinutes(30));
            Assert.Equal(PlutoIngestHealth.NoData, ing.Health);
            Assert.False(ing.TryRead(out _));

            Assert.True(ing.Ingest("""{"vix":14,"us10yChangeBp":5,"asOfUtc":"2026-10-25T00:00:00Z"}"""u8));
            Assert.Equal(PlutoIngestHealth.Live, ing.Health);
            Assert.False(ing.Ingest("""{"vix":999}"""u8));                        // 범위 밖 = 전체 거부
            Assert.False(ing.Ingest("garbage"u8));
            Assert.True(ing.TryRead(out var s));
            Assert.Equal(1400, s.VixX100);
            Assert.Equal(2, ing.Rejected);

            Assert.True(ing.Ingest("""{"dxyChangeBp":70,"asOfUtc":"2026-10-25T00:10:00Z"}"""u8));   // 부분 관측 = 병합
            Assert.True(ing.TryRead(out var m));
            Assert.Equal(1400, m.VixX100);
            Assert.Equal(5, m.Us10yChangeBp);
            Assert.Equal(70, m.DollarIndexChangeBp);
            Assert.Equal(PlutoMacroField.Vix | PlutoMacroField.Us10y | PlutoMacroField.Dollar, m.Fields);
            Assert.Equal(T0 + TimeSpan.FromMinutes(10).Ticks, m.AsOfUtcTicks);

            now = T0 + TimeSpan.FromMinutes(41).Ticks;
            Assert.Equal(PlutoIngestHealth.Stale, ing.Health);
            Assert.True(ing.TryRead(out var stale));
            Assert.Equal(1400, stale.VixX100);                                  // 값은 직전 안정값 유지
        }

        private sealed class ScriptedSource : IPlutoMacroSource
        {
            private readonly Queue<Func<byte[]>> _script;
            public readonly CountdownEvent Done;
            public ScriptedSource(params Func<byte[]>[] steps) { _script = new Queue<Func<byte[]>>(steps); Done = new CountdownEvent(steps.Length); }
            public string Name => "scripted";
            public Task<byte[]> FetchAsync(CancellationToken ct)
            {
                Func<byte[]>? step;
                lock (_script) step = _script.Count > 0 ? _script.Dequeue() : null;
                try { return Task.FromResult(step is null ? Array.Empty<byte>() : step()); }
                finally { if (step is not null) Done.Signal(); }
            }
        }

        [Fact]
        public async Task Ingestor_RunAsync_Source_Failures_Keep_Last_Stable()
        {
            var ing = new PlutoGlobalDataIngestor(() => T0);
            using var src = new ScriptedSourceHolder(new ScriptedSource(
                () => Encoding.UTF8.GetBytes("{\"vix\":18,\"us10yChangeBp\":-20}"),
                () => throw new System.Net.Http.HttpRequestException("network down"),
                () => Encoding.UTF8.GetBytes("{\"vix\":\"bad\"}"),
                () => Array.Empty<byte>()));
            using var cts = new CancellationTokenSource();
            var run = ing.RunAsync(src.Source, TimeSpan.FromMilliseconds(10), cts.Token);
            Assert.True(src.Source.Done.Wait(TimeSpan.FromSeconds(10)));
            cts.Cancel();
            await run;
            Assert.Equal("scripted", ing.SourceName);
            Assert.True(ing.TryRead(out var s));
            Assert.Equal(1800, s.VixX100);
            Assert.Equal(-20, s.Us10yChangeBp);
            Assert.True(ing.SourceFailures >= 3);
            Assert.Equal(1, ing.Accepted);
        }

        private sealed class ScriptedSourceHolder : IDisposable
        {
            public readonly ScriptedSource Source;
            public ScriptedSourceHolder(ScriptedSource s) => Source = s;
            public void Dispose() => Source.Done.Dispose();
        }

        // ── ④ 에피소딕 기억 ───────────────────────────────────────────────────

        [Fact]
        public void Episodic_Seeds_Exact_Match_Is_100_And_Similarity_Monotone()
        {
            var mem = new PlutoEpisodicMemoryStore();
            Assert.False(mem.FindNearest(in AiRally).Found);
            mem.SeedDefaults();
            mem.SeedDefaults();                                                  // 재시드 = 생략
            Assert.Equal(PlutoRegimeVectorLake.Count, mem.Count);                  // [v12.18.82-DEV] 12대 역사적 국면 레이크 시드

            var m = mem.FindNearest(in AiRally);
            Assert.True(m.Found);
            Assert.Equal(100, m.SimilarityPct);
            Assert.Equal(6, m.Dimensions);
            var e = mem.Get(m.EpisodeId)!;
            Assert.Equal("2024 AI 유동성 랠리", e.Name);
            Assert.Equal(PlutoEpisodeKind.Bull, e.Kind);
            Assert.True(e.IsSeed);

            var panic = new PlutoMacroSnapshot(PlutoMacroField.All, 5800, -90, 450, 1000, -2700, -75, T0);
            Assert.Equal(PlutoEpisodeKind.Panic, mem.Get(mem.FindNearest(in panic).EpisodeId)!.Kind);

            int prev = 101;
            for (int d = 0; d <= 3000; d += 500)
            {
                var drift = new PlutoMacroSnapshot(PlutoMacroField.All, 1400 + d, 5, 50, 100, 600, 50, T0);
                var ep = mem.Get(m.EpisodeId)!.Features;
                int sim = PlutoEpisodicMemoryStore.Similarity(in drift, in ep, out _);
                Assert.InRange(sim, 0, 100);
                Assert.True(sim <= prev);
                prev = sim;
            }

            // 공통 차원 1개 = 판정 불가
            var vixOnly = new PlutoMacroSnapshot(PlutoMacroField.Vix, 1400, 0, 0, 0, 0, 0, T0);
            Assert.False(mem.FindNearest(in vixOnly).Found);
            // 2차원 = 판정 · 관측 차원만 사용
            var two = new PlutoMacroSnapshot(PlutoMacroField.Vix | PlutoMacroField.EquityTrend, 1400, 0, 0, 0, 600, 0, T0);
            var m2 = mem.FindNearest(in two);
            Assert.Equal(2, m2.Dimensions);
            Assert.Equal(100, m2.SimilarityPct);
        }

        [Fact]
        public void Episodic_Remember_Validates_And_Caps()
        {
            var mem = new PlutoEpisodicMemoryStore();
            Assert.Equal(0, mem.Remember("", PlutoEpisodeKind.Bull, in AiRally));
            Assert.Equal(0, mem.Remember("x", PlutoEpisodeKind.Unknown, in AiRally));
            Assert.Equal(0, mem.Remember("x", PlutoEpisodeKind.Bull, default));
            for (int i = 0; i < PlutoEpisodicMemoryStore.Capacity; i++) Assert.Equal(i + 1, mem.Remember("e" + i, PlutoEpisodeKind.Range, in AiRally));
            Assert.Equal(0, mem.Remember("overflow", PlutoEpisodeKind.Range, in AiRally));
            Assert.Null(mem.Get(0));
            Assert.Null(mem.Get(PlutoEpisodicMemoryStore.Capacity + 1));
            Assert.False(mem.Get(1)!.IsSeed);
        }

        // ── ⑤ 보정기 ─────────────────────────────────────────────────────────

        private static PlutoPostMortem Loss(long seq, bool counterThesis, long pnl = -1000) =>
            PlutoPostMortemReviewer.Review(new PlutoEntryMemo { EntrySeq = seq, EntryTicks = 0, EntryCost = 100_000, Regime = MarketRegime.ChoppyRange,
                Theses = counterThesis ? CounterThesis.Overheat : CounterThesis.None }, ArenaTrigger.StopLoss, MarketRegime.ChoppyRange, pnl, true, Sec * 60, 0);

        private static PlutoPostMortem Win(long seq, long pnl = 1000) =>
            PlutoPostMortemReviewer.Review(new PlutoEntryMemo { EntrySeq = seq, EntryTicks = 0, EntryCost = 100_000, Regime = MarketRegime.ChoppyRange },
                ArenaTrigger.TakeProfit, MarketRegime.ChoppyRange, pnl, true, Sec * 60, 0);

        private static PlutoPostMortem TrendWin(long seq, long pnl = 1000) =>
            PlutoPostMortemReviewer.Review(new PlutoEntryMemo { EntrySeq = seq, EntryTicks = 0, EntryCost = 100_000, Regime = MarketRegime.TrendingBull },
                ArenaTrigger.TakeProfit, MarketRegime.TrendingBull, pnl, true, Sec * 60, 0);

        [Fact]
        public void Calibrator_Lesson_Fixtures_Are_As_Expected()
        {
            Assert.Equal(PlutoLesson.RespectCounterThesis, Loss(1, true).Lesson);
            Assert.Equal(PlutoLesson.SignalDecay, Loss(1, false).Lesson);
            Assert.Equal(PlutoLesson.DisciplinedExit, Win(1).Lesson);
            Assert.Equal(PlutoLesson.TrendFollow, TrendWin(1).Lesson);
        }

        [Fact]
        public void Calibrator_Needs_MinSamples_And_Skips_Partial()
        {
            var c = new PlutoWeightCalibrator();
            var partial = PlutoPostMortemReviewer.Review(new PlutoEntryMemo { EntrySeq = 1, EntryCost = 100_000, Regime = MarketRegime.ChoppyRange },
                ArenaTrigger.TakeProfit, MarketRegime.ChoppyRange, 500, false, Sec, 0);
            Assert.False(c.Observe(in partial, 50, 0));
            Assert.False(c.Observe(default, 50, 0));
            for (int i = 0; i < PlutoWeightCalibrator.MinSamples - 1; i++) Assert.True(c.Observe(Loss(i + 1, true), 50, 0));
            Assert.False(c.Propose(default, out var cand));
            Assert.True(cand.IsNeutral);
            Assert.True(c.Observe(Loss(99, true), 50, 0));
            Assert.True(c.Propose(default, out cand));
            Assert.Equal(PlutoWeightCalibrator.MaxStrictnessStep, cand.DevilStrictnessPts);   // 세대당 상한
            Assert.Equal(0, cand.MomentumTiltPct);
        }

        [Fact]
        public void Calibrator_Trend_Wins_Raise_Tilt_Signal_Decay_Lowers_It_And_Bounds_Hold()
        {
            var up = new PlutoWeightCalibrator();
            for (int i = 0; i < 40; i++) up.Observe(TrendWin(i + 1), 60, 0);
            Assert.True(up.Propose(default, out var c1));
            Assert.Equal(PlutoWeightCalibrator.MaxTiltStep, c1.MomentumTiltPct);
            Assert.Equal(-PlutoWeightCalibrator.MaxStrictnessStep, c1.DevilStrictnessPts);     // 추세 순응 승리 = 엄격도 완화 압력
            var top = new PlutoCognitiveWeights(3, PlutoCognitiveWeights.MaxMomentumTiltPct, PlutoCognitiveWeights.MinStrictnessPts);
            Assert.True(up.Propose(in top, out var c2));
            Assert.Equal(PlutoCognitiveWeights.MaxMomentumTiltPct, c2.MomentumTiltPct);         // 상한 포화
            Assert.Equal(PlutoCognitiveWeights.MinStrictnessPts, c2.DevilStrictnessPts);

            var down = new PlutoWeightCalibrator();
            for (int i = 0; i < 40; i++) down.Observe(Loss(i + 1, false), 60, 0);
            Assert.True(down.Propose(default, out var c3));
            Assert.Equal(-PlutoWeightCalibrator.MaxTiltStep, c3.MomentumTiltPct);
        }

        [Fact]
        public void Shadow_Gate_Passes_When_Low_Confidence_Trades_Lose_And_Rejects_When_They_Win()
        {
            // 홀드아웃(홀수 순번): 확신도 31(엄격도 +2 면 29 → 건너뜀) 거래 = 패착 · 확신도 80 거래 = 승리 → 엄격화가 성과 개선
            var good = new PlutoWeightCalibrator();
            for (int i = 0; i < 60; i++)
            {
                if ((i & 1) == 0) good.Observe(Loss(i + 1, true), 50, 0);                  // 학습 절반 = 반대 논리 무시 패착 압력
                else if (i % 4 == 1) good.Observe(Loss(i + 1, true, -3000), 31, 0);
                else good.Observe(Win(i + 1, 2000), 80, 0);
            }
            PlutoCognitiveWeights cur = default;
            Assert.True(good.Propose(in cur, out var cand));
            Assert.Equal(2, cand.DevilStrictnessPts);
            var v = good.ShadowValidate(in cur, ref cand);
            Assert.Equal(PlutoShadowReason.Passed, v.Reason);
            Assert.Equal(30, v.Holdout);
            Assert.Equal(15, v.Kept);
            Assert.True(v.ShadowWinRateBp > v.BaselineWinRateBp);
            Assert.True(v.ShadowMddBp <= v.BaselineMddBp);
            Assert.Equal(10000, cand.ShadowWinRateBp);
            Assert.Equal(15, cand.ShadowSamples);

            // 같은 구조에서 저확신 거래가 승리 → 엄격화 = 승률 악화 → 거부
            var bad = new PlutoWeightCalibrator();
            for (int i = 0; i < 60; i++)
            {
                if ((i & 1) == 0) bad.Observe(Loss(i + 1, true), 50, 0);
                else if (i % 4 == 1) bad.Observe(Win(i + 1, 3000), 31, 0);
                else bad.Observe(Loss(i + 1, false, -500), 80, 0);
            }
            Assert.True(bad.Propose(in cur, out var cand2));
            var v2 = bad.ShadowValidate(in cur, ref cand2);
            Assert.Equal(PlutoShadowReason.WinRateDegraded, v2.Reason);
            Assert.False(v2.Passed);

            // 남는 거래 < 10 = 홀드아웃 부족(과최적화 방지)
            var thin = new PlutoWeightCalibrator();
            for (int i = 0; i < 24; i++) thin.Observe(Loss(i + 1, true), (i & 1) == 0 ? 50 : 31, 0);
            Assert.True(thin.Propose(in cur, out var cand3));
            Assert.Equal(PlutoShadowReason.InsufficientHoldout, thin.ShadowValidate(in cur, ref cand3).Reason);

            // 변화 없음
            var same = cur;
            Assert.Equal(PlutoShadowReason.NoChange, thin.ShadowValidate(in cur, ref same).Reason);
        }

        // ── ⑥ 스웜 ───────────────────────────────────────────────────────────

        private sealed class FakeArena
        {
            public readonly List<AiTradeRationale> Log = new();
            public long Seq;

            public int Copy(Span<AiTradeRationale> dst)
            {
                lock (Log)
                {
                    int n = Math.Min(Math.Min(Log.Count, 128), dst.Length);
                    for (int k = 0; k < n; k++) dst[k] = Log[Log.Count - n + k];
                    return n;
                }
            }

            public long Entry(int confidence)
            {
                var regime = StateOf(MarketRegime.ChoppyRange);
                var v = PlutoDevilsAdvocateFilter.Assess(confidence, 50, 10000, 1000, 1000, 0, 0);
                var g = PlutoGovernanceTag.ForEntry(in regime, in v, confidence * 100, PlutoDynamicSizer.MinWeightBp, 0);
                long s = ++Seq;
                lock (Log) Log.Add(new AiTradeRationale(s, Samsung, 1, 1, 70_000, Kst10.Ticks, ArenaTrigger.OrderbookImbalance, 0, confidence, 71_000, 69_000, 200, 0, 0, 0, 0, 0,
                    governance: in g));
                return s;
            }

            public void Exit(in PlutoPostMortem pm)
            {
                var regime = StateOf(MarketRegime.ChoppyRange);
                var g = PlutoGovernanceTag.ForExit(in regime, 0, in pm);
                long s = ++Seq;
                lock (Log) Log.Add(new AiTradeRationale(s, Samsung, -1, 1, 70_000, Kst10.Ticks, pm.ExitTrigger, 0, 0, 0, 0, 0, 0, 0, 0, 0, pm.TradePnl, governance: in g));
            }
        }

        private static PlutoPostMortem LossFor(long entrySeq) =>
            PlutoPostMortemReviewer.Review(new PlutoEntryMemo { EntrySeq = entrySeq, EntryCost = 100_000, Regime = MarketRegime.ChoppyRange, Theses = CounterThesis.Overheat },
                ArenaTrigger.StopLoss, MarketRegime.ChoppyRange, -1000, true, Sec * 60, 0);

        [Fact]
        public void Swarm_Harvests_Once_Links_Confidence_Matches_Episode_And_Feeds_Macro()
        {
            var weights = new PlutoCognitiveWeightSeqlock();
            var ing = new PlutoGlobalDataIngestor(() => T0);
            var mem = new PlutoEpisodicMemoryStore();
            mem.SeedDefaults();
            var cal = new PlutoWeightCalibrator();
            var swarm = new PlutoContinuousLearningSwarm(ing, mem, cal, weights, () => T0);
            var arena = new FakeArena();
            swarm.AddArena("krx", arena.Copy);
            int sinkUs = int.MinValue, sinkDxy = int.MinValue;
            swarm.SetMacroSink((u, d) => { sinkUs = u; sinkDxy = d; });

            Assert.Null(swarm.Status);
            var first = swarm.Step();
            Assert.Equal(PlutoIngestHealth.NoData, first.Health);
            Assert.False(first.Match.Found);
            Assert.Equal(int.MinValue, sinkUs);                                  // 관측 0 = 거시 주입 생략

            for (int i = 0; i < 12; i++) arena.Exit(LossFor(arena.Entry(40)));
            Assert.True(ing.Ingest("""{"vix":14,"us10yChangeBp":5,"dxyChangeBp":50,"usdKrwChangeBp":100,"equityTrendBp":600,"sentiment":50}"""u8));
            var st = swarm.Step();
            Assert.Equal(12, cal.Observed);
            Assert.Equal(PlutoIngestHealth.Live, st.Health);
            Assert.True(st.Match.Found);
            Assert.Equal("2024 AI 유동성 랠리", st.Episode!.Name);
            Assert.Equal(100, st.Match.SimilarityPct);
            Assert.Equal(5, sinkUs);
            Assert.Equal(50, sinkDxy);
            Assert.Equal(2, st.Cycles);

            swarm.Step();
            Assert.Equal(12, cal.Observed);                                      // 같은 근거 재수확 0

            // 포트 투영
            var view = PlutoLearningPortAdapter.Map(swarm.Status, running: true);
            Assert.Equal(PlutoLearningHealth.Live, view.Health);
            Assert.Equal("2024 AI 유동성 랠리", view.EpisodeName);
            Assert.Equal(100, view.SimilarityPct);
            Assert.Equal("🧠 글로벌 학습 상태: 실시간 연동 중 (과거 국면 유사도: 2024 AI 유동성 랠리(시드 근사) 100%)", view.BadgeText);
            var none = PlutoLearningPortAdapter.Map(null, running: false);
            Assert.Equal("🧠 글로벌 학습 상태: 학습 엔진 정지 (과거 국면 유사도: 판정 전)", none.BadgeText);
        }

        [Fact]
        public void Swarm_Recalibrates_Through_Shadow_Gate_And_HotSwaps()
        {
            var weights = new PlutoCognitiveWeightSeqlock();
            var cal = new PlutoWeightCalibrator();
            var swarm = new PlutoContinuousLearningSwarm(new PlutoGlobalDataIngestor(() => T0), new PlutoEpisodicMemoryStore(), cal, weights, () => T0);
            var arena = new FakeArena();
            swarm.AddArena("krx", arena.Copy);
            // 학습 절반 = 반대 논리 무시 패착 · 홀드아웃 = 저확신(31) 패착 / 고확신(80) 승리 — 순번 교대(표본 순서 = 청산 순서)
            for (int i = 0; i < 40; i++)
            {
                if ((i & 1) == 0) arena.Exit(LossFor(arena.Entry(50)));
                else if (i % 4 == 1) arena.Exit(LossFor(arena.Entry(31)));
                else
                {
                    long e = arena.Entry(80);
                    arena.Exit(PlutoPostMortemReviewer.Review(new PlutoEntryMemo { EntrySeq = e, EntryCost = 100_000, Regime = MarketRegime.ChoppyRange },
                        ArenaTrigger.TakeProfit, MarketRegime.ChoppyRange, 2000, true, Sec * 60, 0));
                }
            }
            var st = swarm.Step();
            Assert.Equal(40, cal.Observed);
            Assert.Equal(PlutoShadowReason.Passed, st.LastVerdict.Reason);
            Assert.Equal(1, st.Weights.Generation);
            Assert.Equal(2, st.Weights.DevilStrictnessPts);
            Assert.Equal(1, weights.Swaps);
            Assert.True(weights.TryRead(out var live));
            Assert.Equal(2, live.DevilStrictnessPts);
        }

        [Fact]
        public void Swarm_Background_Loop_Starts_Steps_And_Disposes()
        {
            var swarm = new PlutoContinuousLearningSwarm(new PlutoGlobalDataIngestor(), new PlutoEpisodicMemoryStore(), new PlutoWeightCalibrator(), new PlutoCognitiveWeightSeqlock());
            int calls = 0;
            using var stepped = new CountdownEvent(3);
            swarm.AddArena("probe", _ => { if (Interlocked.Increment(ref calls) <= 3) stepped.Signal(); return 0; });
            swarm.Start(TimeSpan.FromMilliseconds(10));
            swarm.Start(TimeSpan.FromMilliseconds(10));                          // 중복 기동 = 무시
            Assert.True(stepped.Wait(TimeSpan.FromSeconds(10)));
            Assert.True(swarm.Running);
            swarm.Dispose();
            Assert.False(swarm.Running);
            Assert.True(swarm.Cycles >= 3);
            Assert.Equal(0, swarm.Faulted);
            swarm.Dispose();                                                     // 멱등
        }

        [Fact]
        public void DualArenaPage_Renders_Learning_Badge_Wired_And_Unwired()
        {
            var swarm = new PlutoContinuousLearningSwarm(new PlutoGlobalDataIngestor(() => T0), new PlutoEpisodicMemoryStore(), new PlutoWeightCalibrator(),
                new PlutoCognitiveWeightSeqlock(), () => T0);
            swarm.Memory.SeedDefaults();
            Assert.True(swarm.Ingestor.Ingest(in AiRally));
            swarm.Step();
            foreach (var (port, expected, css) in new (IPlutoLearningPort, string, string)[]
            {
                (new PlutoLearningPortAdapter(swarm), "🧠 글로벌 학습 상태: 학습 엔진 정지 (과거 국면 유사도: 2024 AI 유동성 랠리(시드 근사) 100%)", "none"),
                (UnwiredPlutoLearningPort.Instance, "🧠 글로벌 학습 상태: 학습 엔진 미결선", "unwired"),
            })
            {
                using var ctx = new Bunit.TestContext();
                var krx = new KrxArenaEngine();
                var f = new KrxArenaQuoteFeeder(krx);
                f.Pump(Kst10.AddHours(-9).Ticks);
                ctx.Services.AddSingleton<IKrxArenaPort>(new KrxArenaPortAdapter(krx, f));
                ctx.Services.AddSingleton<IUsArenaPort>(new UsArenaPortAdapter(new UsTechArenaEngine()));
                ctx.Services.AddSingleton<ICryptoArenaPort>(new CryptoArenaPortAdapter(new UpbitCryptoArenaEngine()));
                ctx.Services.AddSingleton<IArenaPreferencePort>(UnwiredArenaPreferencePort.Instance);
                ctx.Services.AddSingleton(port);
                ctx.Services.AddSingleton<IPlutoRecommendationPort>(UnwiredPlutoRecommendationPort.Instance);   // [v12.18.78-DEV] 추천 엔진 미결선
                var cut = ctx.RenderComponent<DualArenaPage>();
                var badge = cut.Find(".da-learn");
                Assert.Equal(expected, badge.TextContent.Trim());
                Assert.Equal(css, badge.GetAttribute("data-learn"));
                Assert.Equal(port.IsWired ? 1 : 0, cut.FindAll(".da-learn-w").Count);
            }
        }

        [Fact]
        public void Host_Wires_Learning_Swarm_To_All_Three_Arenas_With_Unwired_Fallback()
        {
            var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "StockManager.sln"))) dir = dir.Parent;
            Assert.NotNull(dir);
            string form = System.IO.File.ReadAllText(System.IO.Path.Combine(dir!.FullName, "StockManager", "Blazor", "BlazorHomeForm.cs"));
            Assert.Contains("cfg.GetBool(\"Pluto.LearningEnabled\", true)", form);
            Assert.Contains("krxArena.AttachLearning(plutoWeights);", form);
            Assert.Contains("usArena.AttachLearning(plutoWeights);", form);
            Assert.Contains("cryptoArena.AttachLearning(plutoWeights);", form);
            Assert.Contains("new StockManager.Adapters.PlutoLearningPortAdapter(plutoSwarm)", form);
            Assert.Contains("MS2E.UI.Portable.Ports.Trading.UnwiredPlutoLearningPort.Instance", form);
            Assert.Contains("macroUri.Scheme == Uri.UriSchemeHttps", form);
            Assert.DoesNotContain("QuantumVault", form.Substring(form.IndexOf("Pluto.LearningEnabled", StringComparison.Ordinal), 4000));
        }

        // ── ⑦ δ=0 · 찢어진 판독 0 ─────────────────────────────────────────────

        [Fact]
        public void HotPath_Weight_Reads_During_Concurrent_HotSwaps_Are_Zero_Alloc_And_Untorn()
        {
            var sl = new PlutoCognitiveWeightSeqlock();
            using var stop = new ManualResetEventSlim(false);
            using var joined = new CountdownEvent(1);
            long swaps = 0;
            var writer = new Thread(() =>
            {
                try
                {
                    int k = 0;
                    while (!stop.IsSet)
                    {
                        int tilt = k % 51;                                       // 불변 관계: strictness = tilt % 31 − 10
                        sl.TrySwap(new PlutoCognitiveWeights(0, tilt, tilt % 31 - 10), shadowPassed: true);
                        k++;
                        Interlocked.Increment(ref swaps);
                    }
                }
                finally { joined.Signal(); }
            }) { IsBackground = true };
            writer.Start();
            while (Interlocked.Read(ref swaps) < 1000) Thread.SpinWait(64);

            long torn = 0, acc = 0, misses = 0;
            void Reads(int n)
            {
                for (int i = 0; i < n; i++)
                {
                    if (!sl.TryRead(out var w)) { misses++; continue; }
                    if (w.DevilStrictnessPts != w.MomentumTiltPct % 31 - 10 || !w.IsWellFormed) torn++;
                    acc += w.Generation;
                }
            }
            Reads(10_000);
            long bytes = MinAllocated(() => Reads(100_000));
            stop.Set();
            Assert.True(joined.Wait(TimeSpan.FromSeconds(10)));
            Assert.Equal(0, bytes);
            Assert.Equal(0, torn);
            Assert.True(acc > 0);
            Assert.True(Interlocked.Read(ref swaps) > 1000);
            Assert.True(misses < 10_000);
        }

        [Fact]
        public void Governor_With_Learned_Weights_And_Engine_Settle_Are_Zero_Alloc()
        {
            var sl = new PlutoCognitiveWeightSeqlock();
            Assert.True(sl.TrySwap(new PlutoCognitiveWeights(0, 20, 5), shadowPassed: true));
            var sizer = new PlutoSizerState();
            var regime = StateOf(MarketRegime.TrendingBull);
            long acc = 0;
            void Gov(int n)
            {
                for (int i = 0; i < n; i++)
                {
                    sl.TryRead(out var w);
                    var r = PlutoCognitiveGovernor.Approve(in regime, 3000 + (i & 2047), (i & 255) * 10, 25, (i & 127) * 5, 35, 3000, 1000 + (i & 7), 1000, i & 15, 0, in sizer, i,
                        out var tag, learned: in w);
                    acc += (int)r + tag.WeightBp;
                }
            }
            Gov(10_000);
            long govBytes = MinAllocated(() => Gov(100_000));
            Assert.Equal(0, govBytes);
            Assert.True(acc > 0);

            var e = new KrxArenaEngine();
            e.EnableGovernance(true);
            e.AttachLearning(sl);
            Assert.True(e.LearningAttached);
            var kt = Kst10;
            int j2 = 0;
            void Settle(int n)
            {
                for (int i = 0; i < n; i++, j2++)
                {
                    kt = kt.AddTicks(Sec * 2);
                    if (kt >= Kst10.AddHours(5)) kt = Kst10;
                    long px = 68_000 + ((j2 >> 6) & 63) * 100 - (j2 & 1) * 100;
                    e.OnQuote(Samsung + (j2 & 3), px, px + 100, (j2 & 32) != 0 ? 9000 : 1000, 1000, 100 + (j2 & 63) * ((j2 & 1) + 1), kt);   // 매수 잔량 9:1 구간 = AI 진입 시그널
                }
            }
            Settle(20_000);
            long settleBytes = MinAllocated(() => Settle(100_000));
            Assert.Equal(0, settleBytes);
            Assert.True(e.TryReadGovernor(out var g));
            Assert.True(g.Approved + g.VetoBlocks + g.PanicBlocks + g.CooldownBlocks > 0);   // 거버너 경로(학습 판독 포함)가 실제로 실행됨
        }
    }
}
