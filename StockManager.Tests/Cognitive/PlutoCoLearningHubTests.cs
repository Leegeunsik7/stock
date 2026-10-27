// ============================================================================
// PlutoCoLearningHubTests — v12.18.93-DEV: 플루토 퀀트 코-러닝 허브 · 자율 학습 가속기 회귀
//
//  §1 사전 가중치 — 표 범위 · Resolve 폴백 순서(국면 슬롯 → 기준선 → 전역 보정 → 사전 → 전역 중립) · 옵트인(미적재 = 기존 동작) · 첫 보정 출발점 · δ=0(Release)
//  §2 로컬 거시 추정 — 3차원(VIX 대용 · 지수 추세 · BTC 심리) · 주식만 = 2차원 · 코인만/빈 입력 = 추정 불가 · 수집기 병합 → 12대 국면 판정 해소
//  §3 자율 학습 가속기 — 자산군당 100표본 3라운드 · 세대 단조 · 리플레이 계수(합성) · 실 에피소드 무오염 · NoData/Failed 정직 · 위상 0 = 기존 리플레이 동일
//  §4 Portable 합성 — 생각 독백 · 오답 노트 · 개념 과외 · 주문 코칭 · 학습 일지 · 셀 문구 · 어댑터 투영
//  §5 UI — 독백/오답 노트/코칭 뱃지/개념 카드/학습 일지 렌더 · 인스펙터 가속 버튼 · 페이지 관제탑 버튼(미결선 = 비활성) · 휴먼 매수 → 코칭 뱃지
// ============================================================================
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MS2E.UI.Portable.Components;
using MS2E.UI.Portable.Pages.Trading;
using MS2E.UI.Portable.Ports.Trading;
using StockManager.Adapters;
using StockManager.Engines.Arena.Common;
using StockManager.Engines.Arena.Crypto;
using StockManager.Engines.Arena.Domestic;
using StockManager.Engines.Arena.Us;
using StockManager.Engines.Cognitive.Governance;
using StockManager.Engines.Cognitive.Learning;
using Xunit;

namespace StockManager.Tests.Cognitive
{
    [Collection("ConcurrencyStress")]
    public sealed class PlutoCoLearningHubTests
    {
        private static readonly long T0 = new DateTime(2026, 10, 27, 0, 0, 0, DateTimeKind.Utc).Ticks;

        private sealed class FakeCandles : IPlutoCandleSource
        {
            private readonly IReadOnlyList<PlutoCandleSeries> _series;
            public int Calls;
            public FakeCandles(IReadOnlyList<PlutoCandleSeries> series) => _series = series;
            public IReadOnlyList<PlutoCandleSeries> LoadRecent(int maxSymbolsPerAsset, int bars) { Calls++; return _series; }
        }

        private sealed class ThrowingCandles : IPlutoCandleSource
        {
            public IReadOnlyList<PlutoCandleSeries> LoadRecent(int maxSymbolsPerAsset, int bars) => throw new InvalidOperationException("db");
        }

        /// <summary>결정적 시계열: 추세(bp/봉) + 사인 진동(진폭 %) · 양수.</summary>
        private static double[] Wave(int bars, double trendBpPerBar, double amplitudePct, double period, double start = 100)
        {
            var c = new double[bars];
            for (int i = 0; i < bars; i++)
                c[i] = start * Math.Pow(1 + trendBpPerBar / 10000, i) * (1 + amplitudePct / 100 * Math.Sin(2 * Math.PI * i / period));
            return c;
        }

        private static List<PlutoCandleSeries> Universe(bool equities = true, bool crypto = true)
        {
            var list = new List<PlutoCandleSeries>();
            if (equities)
            {
                list.Add(new PlutoCandleSeries("005930", PlutoAssetClass.Krx, Wave(120, 15, 3, 9)));
                list.Add(new PlutoCandleSeries("000660", PlutoAssetClass.Krx, Wave(120, -10, 4, 13, 80)));
                list.Add(new PlutoCandleSeries("NVDA", PlutoAssetClass.Us, Wave(120, 25, 2.5, 11, 120)));
                list.Add(new PlutoCandleSeries("AAPL", PlutoAssetClass.Us, Wave(120, 5, 2, 7, 190)));
            }
            if (crypto)
            {
                list.Add(new PlutoCandleSeries("KRW-BTC", PlutoAssetClass.Upbit, Wave(120, 30, 6, 10, 9e7)));
                list.Add(new PlutoCandleSeries("KRW-ETH", PlutoAssetClass.Upbit, Wave(120, -20, 7, 8, 4e6)));
            }
            return list;
        }

        // ── §1 사전 가중치 ─────────────────────────────────────────────────

        [Fact]
        public void Priors_Table_In_Bounds_WellFormed_And_Baseline_Neutral()
        {
            for (int r = 1; r <= PlutoRegimeVectorLake.Count; r++)
                for (int a = 0; a < PlutoCognitiveTensorLake.Assets; a++)
                {
                    var w = PlutoRegimePriors.Of(r, (PlutoAssetClass)a);
                    Assert.True(w.IsWellFormed);
                    Assert.Equal(0, w.Generation);
                    Assert.InRange(w.MomentumTiltPct, -PlutoRegimePriors.MaxTiltPct, PlutoRegimePriors.MaxTiltPct);
                    Assert.InRange(w.DevilStrictnessPts, -PlutoRegimePriors.MaxStrictnessPts, PlutoRegimePriors.MaxStrictnessPts);
                    Assert.False(w.IsNeutral);
                }
            Assert.True(PlutoRegimePriors.Of(0, PlutoAssetClass.Krx).IsNeutral);
            Assert.True(PlutoRegimePriors.Of(13, PlutoAssetClass.Us).IsNeutral);
            // 패닉(코로나) = 방어 · 코인이 가장 방어적 / 유동성 장세 = 코인 추세 가중
            Assert.True(PlutoRegimePriors.Of(8, PlutoAssetClass.Upbit).DevilStrictnessPts >= PlutoRegimePriors.Of(8, PlutoAssetClass.Krx).DevilStrictnessPts);
            Assert.Equal(10, PlutoRegimePriors.Of(9, PlutoAssetClass.Upbit).MomentumTiltPct);
        }

        [Fact]
        public void Resolve_Without_Priors_Is_Unchanged_Global()
        {
            var t = new PlutoCognitiveTensorLake();
            var w = default(PlutoCognitiveWeights);
            Assert.Equal(PlutoTensorSource.Global, t.Resolve(PlutoAssetClass.Krx, 8, ref w));
            Assert.True(w.IsNeutral);
            Assert.False(t.PriorsLoaded);
            Assert.False(t.Cell(PlutoCognitiveTensorLake.CellOf(PlutoAssetClass.Krx, 8)).HasPrior);
        }

        [Fact]
        public void Resolve_Fallback_Order_Slot_Baseline_GlobalCalibrated_Prior()
        {
            var t = new PlutoCognitiveTensorLake();
            Assert.Equal(36, t.LoadPriors());
            var w = default(PlutoCognitiveWeights);
            Assert.Equal(PlutoTensorSource.Prior, t.Resolve(PlutoAssetClass.Upbit, 8, ref w));
            Assert.Equal(PlutoRegimePriors.Of(8, PlutoAssetClass.Upbit).MomentumTiltPct, w.MomentumTiltPct);
            Assert.Equal(0, w.Generation);

            // 판정 전(국면 0) = 사전 없음 → 전역 그대로
            var g0 = default(PlutoCognitiveWeights);
            Assert.Equal(PlutoTensorSource.Global, t.Resolve(PlutoAssetClass.Krx, 0, ref g0));

            // 전역 보정 가중치(세대 > 0) = 사전보다 우선
            var global = new PlutoCognitiveWeights(3, 7, 1);
            Assert.Equal(PlutoTensorSource.Global, t.Resolve(PlutoAssetClass.Krx, 8, ref global));
            Assert.Equal(7, global.MomentumTiltPct);

            var cell = t.Cell(PlutoCognitiveTensorLake.CellOf(PlutoAssetClass.Krx, 8));
            Assert.True(cell.HasPrior);
            Assert.Equal(PlutoRegimePriors.Of(8, PlutoAssetClass.Krx).DevilStrictnessPts, cell.Prior.DevilStrictnessPts);
            Assert.Equal(PlutoSlotMaturity.Untrained, cell.Maturity);       // 사전 가중치 = 보정 아님
            Assert.False(t.Cell(PlutoCognitiveTensorLake.CellOf(PlutoAssetClass.Krx, 0)).HasPrior);   // 기준선 = 사전 없음
            Assert.Equal(PlutoRegimePriors.Of(8, PlutoAssetClass.Krx).DevilStrictnessPts, t.StrictnessOf(PlutoAssetClass.Krx, 8));
        }

        [Fact]
        public void First_Calibration_Starts_From_Prior_And_Records_Delta()
        {
            var t = new PlutoCognitiveTensorLake();
            t.LoadPriors();
            var prior = PlutoRegimePriors.Of(2, PlutoAssetClass.Krx);
            var cal = new PlutoWeightCalibrator();
            // 추세 순응 승리만 → 모멘텀 +5% 제안 · 엄격도 −2(규율 승) · 홀드아웃 전부 승 → 통과
            for (int i = 0; i < 40; i++)
                cal.ObserveSample(new PlutoLearningSample(150, 80, prior.DevilStrictnessPts, PlutoLesson.TrendFollow, PostMortemVerdict.Win));
            Assert.True(t.ApplyReplay(PlutoAssetClass.Krx, 2, cal, out var verdict));
            Assert.True(verdict.Passed);
            Assert.True(t.TryRead(PlutoAssetClass.Krx, 2, out var w));
            Assert.Equal(1, w.Generation);
            Assert.Equal(prior.MomentumTiltPct + PlutoWeightCalibrator.MaxTiltStep, w.MomentumTiltPct);   // 출발점 = 사전 가중치
            var c = t.Cell(PlutoCognitiveTensorLake.CellOf(PlutoAssetClass.Krx, 2));
            Assert.Equal(PlutoWeightCalibrator.MaxTiltStep, c.LastDeltaTilt);
            Assert.Equal(w.DevilStrictnessPts - prior.DevilStrictnessPts, c.LastDeltaStrict);
            // 보정된 슬롯 = 국면 슬롯 출처
            var g = default(PlutoCognitiveWeights);
            Assert.Equal(PlutoTensorSource.Regime, t.Resolve(PlutoAssetClass.Krx, 2, ref g));
        }

        [Fact]
        public void Resolve_With_Priors_Allocates_Zero_Bytes()
        {
            var t = new PlutoCognitiveTensorLake();
            t.LoadPriors();
            long sum = 0;
            void Body()
            {
                for (int i = 0; i < 100_000; i++)
                {
                    var w = default(PlutoCognitiveWeights);
                    sum += (int)t.Resolve((PlutoAssetClass)(i % 3), i % 13, ref w) + w.MomentumTiltPct;
                }
            }
            Body();
            Assert.Equal(0, MinAllocated(Body));
            Assert.NotEqual(0, sum);
        }

        private static long MinAllocated(Action body, int windows = 3)
        {
            long min = long.MaxValue;
            for (int w = 0; w < windows; w++)
            {
                long before = GC.GetAllocatedBytesForCurrentThread();
                body();
                min = Math.Min(min, GC.GetAllocatedBytesForCurrentThread() - before);
            }
            return min;
        }

        // ── §2 로컬 거시 추정 ──────────────────────────────────────────────

        [Fact]
        public void LocalMacro_Three_Dimensions_From_Equities_And_Crypto()
        {
            Assert.True(PlutoLocalMacroEstimator.TryEstimate(Universe(), T0, out var s, out var b));
            Assert.True(s.IsWellFormed);
            Assert.Equal(PlutoMacroField.Vix | PlutoMacroField.EquityTrend | PlutoMacroField.Sentiment, s.Fields);
            Assert.Equal((4, 2, 3), (b.EquitySeries, b.CryptoSeries, b.Dimensions));
            Assert.InRange(s.VixX100, 1, PlutoMacroSnapshot.MaxVixX100);
            Assert.Equal(b.RealizedVolX100, s.VixX100);
            Assert.InRange(s.SentimentIndex, -100, 100);
            Assert.Equal(T0, s.AsOfUtcTicks);
            var m = PlutoRegimeVectorLake.FindNearest(in s);
            Assert.True(m.Found);
            Assert.Equal(3, m.Dimensions);
            Assert.Contains("실현변동성(VIX 대용)", PlutoLocalMacroEstimator.Describe(in b));
            Assert.Contains("BTC 흐름", PlutoLocalMacroEstimator.Describe(in b));
        }

        [Fact]
        public void LocalMacro_Equities_Only_Two_Dims_Crypto_Only_Or_Empty_Fails()
        {
            Assert.True(PlutoLocalMacroEstimator.TryEstimate(Universe(crypto: false), T0, out var s, out _));
            Assert.Equal(PlutoMacroField.Vix | PlutoMacroField.EquityTrend, s.Fields);
            Assert.False(PlutoLocalMacroEstimator.TryEstimate(Universe(equities: false), T0, out _, out var b));
            Assert.Equal(1, b.Dimensions);
            Assert.False(PlutoLocalMacroEstimator.TryEstimate(Array.Empty<PlutoCandleSeries>(), T0, out _, out _));
            Assert.False(PlutoLocalMacroEstimator.TryEstimate(Universe(), 0, out _, out _));
            // 봉 부족 · 비양수 = 해당 시계열 제외
            var shortSeries = new[] { new PlutoCandleSeries("005930", PlutoAssetClass.Krx, new double[] { 1, 2, 3 }), new PlutoCandleSeries("AAPL", PlutoAssetClass.Us, Wave(30, 0, 1, 5).Select((x, i) => i == 25 ? -1 : x).ToArray()) };
            Assert.False(PlutoLocalMacroEstimator.TryEstimate(shortSeries, T0, out _, out _));
        }

        [Fact]
        public void LocalMacro_Flat_Series_Zero_Vol_And_Trend_Math()
        {
            var flat = Enumerable.Repeat(100.0, 40).ToArray();
            Assert.True(PlutoLocalMacroEstimator.TryRealizedVolX100(flat, out int v));
            Assert.Equal(0, v);
            Assert.True(PlutoLocalMacroEstimator.TryTrendBp(flat, out int t));
            Assert.Equal(0, t);
            var up = flat.ToArray();
            up[^1] = 110;
            Assert.True(PlutoLocalMacroEstimator.TryTrendBp(up, out t));
            Assert.Equal(1000, t);
        }

        [Fact]
        public void LocalMacro_IngestOnce_Resolves_Regime_Pending_State()
        {
            var ingestor = new PlutoGlobalDataIngestor(() => T0);
            var regime = new PlutoRegimeMatcher();
            Assert.False(ingestor.TryRead(out _));
            Assert.True(PlutoLocalMacroEstimator.IngestOnce(new FakeCandles(Universe()), ingestor, T0, out var b));
            Assert.Equal(3, b.Dimensions);
            Assert.True(ingestor.TryRead(out var snap));
            Assert.Equal(PlutoIngestHealth.Live, ingestor.Health);
            Assert.True(regime.Publish(in snap).Found);                          // '판정 전' 해소
            Assert.False(PlutoLocalMacroEstimator.IngestOnce(new ThrowingCandles(), ingestor, T0, out _));   // 장애 = 직전 안정값 유지
            Assert.True(ingestor.TryRead(out _));
        }

        // ── §3 자율 학습 가속기 ────────────────────────────────────────────

        [Fact]
        public void FastReplay_Three_Rounds_Per_Asset_Monotonic_Generations_And_Replay_Counters()
        {
            var t = new PlutoCognitiveTensorLake();
            t.LoadPriors();
            var src = new FakeCandles(Universe());
            var fast = new PlutoFastReplayLearner(t, src, () => 9, () => T0);
            Assert.Equal(PlutoFastReplayState.Idle, fast.Report.State);
            var r = fast.Run();
            Assert.Equal(PlutoFastReplayState.Completed, r.State);
            Assert.Equal(9, r.RegimeId);
            Assert.Equal(1, r.Runs);
            Assert.Equal(T0, r.CompletedUtcTicks);
            Assert.Equal(3, r.Lanes.Count);
            foreach (var lane in r.Lanes)
            {
                Assert.Equal(PlutoFastReplayLearner.Simulations, lane.Samples);
                Assert.Equal(PlutoFastReplayLearner.Rounds, lane.Rounds.Count);
                Assert.Equal(new[] { 34, 33, 33 }, lane.Rounds.Select(x => x.Samples));
                long gen = lane.Rounds[0].Before.Generation;
                foreach (var round in lane.Rounds)
                {
                    Assert.Equal(gen, round.Before.Generation);
                    Assert.Equal(round.Before.Generation + (round.Swapped ? 1 : 0), round.After.Generation);
                    Assert.True(round.After.IsWellFormed);
                    gen = round.After.Generation;
                }
                var tl = lane.Lessons;
                Assert.Equal(lane.Wins, tl.TrendWins + tl.DisciplinedWins);
                Assert.Equal(lane.Samples - lane.Wins, tl.ShockLosses + tl.CounterLosses + tl.RegimeShiftLosses + tl.DecayLosses);
                var regimeCell = t.Cell(PlutoCognitiveTensorLake.CellOf(lane.Asset, 9));
                var baseCell = t.Cell(PlutoCognitiveTensorLake.CellOf(lane.Asset, 0));
                Assert.Equal(100, regimeCell.ReplaySamples);
                Assert.Equal(100, baseCell.ReplaySamples);
                Assert.Equal(lane.Wins, regimeCell.ReplayWins);
                Assert.Equal(0, regimeCell.LifetimeEpisodes);                    // 합성 표본 = 실 에피소드 무오염
                Assert.Equal(0, baseCell.SessionSamples);
            }
            // 재실행 = 위상 이동(다른 진입점) · 계수 누적
            var r2 = fast.Run();
            Assert.Equal(2, r2.Runs);
            Assert.Equal(200, t.Cell(PlutoCognitiveTensorLake.CellOf(PlutoAssetClass.Krx, 9)).ReplaySamples);
            Assert.Equal(2, src.Calls);
        }

        [Fact]
        public void FastReplay_Clean_Uptrend_Evolves_G0_G1_G2_G3_Through_Shadow_Gate()
        {
            var t = new PlutoCognitiveTensorLake();
            var up = new List<PlutoCandleSeries>
            {
                new("005930", PlutoAssetClass.Krx, Wave(120, 40, 0.3, 9)),
                new("000660", PlutoAssetClass.Krx, Wave(120, 35, 0.3, 7, 80)),
            };
            var r = new PlutoFastReplayLearner(t, new FakeCandles(up), () => 0, () => T0).Run();
            var lane = Assert.Single(r.Lanes);
            Assert.Equal(new long[] { 1, 2, 3 }, lane.Rounds.Select(x => x.After.Generation));
            Assert.All(lane.Rounds, x => Assert.True(x.Verdict.Passed));
            Assert.Equal(new[] { 5, 10, 15 }, lane.Rounds.Select(x => x.After.MomentumTiltPct));   // 추세 순응 승리 → 세대당 +5% 상한
            Assert.True(t.TryRead(PlutoAssetClass.Krx, 0, out var w));
            Assert.Equal(3, w.Generation);
            var view = PlutoLearningPortAdapter.MapReplay(r);
            Assert.Equal(3, view.Evolutions);
            Assert.Contains("3회(G0 → G3)", PlutoStudyJournal.Compose(view)[1].Body);
        }

        [Fact]
        public void FastReplay_Regime_Pending_Targets_Baseline_Only()
        {
            var t = new PlutoCognitiveTensorLake();
            var r = new PlutoFastReplayLearner(t, new FakeCandles(Universe(crypto: false)), () => 0, () => T0).Run();
            Assert.Equal(PlutoFastReplayState.Completed, r.State);
            Assert.Equal(2, r.Lanes.Count);
            Assert.All(r.Lanes, l => Assert.Equal(0, l.TargetRegimeId));
            Assert.Equal(100, t.Cell(PlutoCognitiveTensorLake.CellOf(PlutoAssetClass.Krx, 0)).ReplaySamples);
            for (int reg = 1; reg <= 12; reg++) Assert.Equal(0, t.Cell(PlutoCognitiveTensorLake.CellOf(PlutoAssetClass.Krx, reg)).ReplaySamples);
        }

        [Fact]
        public void FastReplay_NoData_And_Failed_Are_Honest()
        {
            var t = new PlutoCognitiveTensorLake();
            var none = new PlutoFastReplayLearner(t, new FakeCandles(Array.Empty<PlutoCandleSeries>()), utcNowTicks: () => T0).Run();
            Assert.Equal(PlutoFastReplayState.NoData, none.State);
            Assert.Empty(none.Lanes);
            var failed = new PlutoFastReplayLearner(t, new ThrowingCandles(), utcNowTicks: () => T0).Run();
            Assert.Equal(PlutoFastReplayState.Failed, failed.State);
            Assert.Equal(nameof(InvalidOperationException), failed.Error);
            Assert.Equal(0, t.Swaps);
        }

        [Fact]
        public async Task FastReplay_Concurrent_Calls_Share_Inflight_Task()
        {
            var t = new PlutoCognitiveTensorLake();
            var fast = new PlutoFastReplayLearner(t, new FakeCandles(Universe()), () => 0, () => T0);
            var a = fast.RunAsync();
            var b = fast.RunAsync();
            var ra = await a;
            var rb = await b;
            Assert.True(ReferenceEquals(ra, rb) || ra.Runs + 1 == rb.Runs);   // 진행 중 공유 또는 완료 직후 새 실행
            Assert.False(fast.Running);
        }

        [Fact]
        public void Replay_Phase_Zero_Equals_Legacy_Replay()
        {
            var series = Universe();
            var a = new PlutoLearningSample[100];
            var b = new PlutoLearningSample[100];
            int na = PlutoReplayWarmStarter.Replay(series, 3, a);
            int nb = PlutoReplayWarmStarter.Replay(series, 3, b, 0, 0);
            Assert.Equal(na, nb);
            for (int i = 0; i < na; i++) Assert.Equal((a[i].ReturnBp, a[i].Lesson, a[i].EntryConfidence), (b[i].ReturnBp, b[i].Lesson, b[i].EntryConfidence));
            var c = new PlutoLearningSample[100];
            PlutoReplayWarmStarter.Replay(series, 3, c, 5, 1);
            Assert.Contains(Enumerable.Range(0, 100), i => c[i].ReturnBp != a[i].ReturnBp);
        }

        // ── §4 Portable 합성 ───────────────────────────────────────────────

        private static readonly DateTime At = new(2026, 10, 27, 10, 5, 0);

        [Fact]
        public void Thought_Buy_Monologue_Observe_Debate_Decide()
        {
            var gov = new ArenaGovernanceCard(ArenaRegimeCode.TrendingBull, new[] { new ArenaPenaltyTag("과열", -8), new ArenaPenaltyTag("매도벽", -5) }, 78, 65, 600, 0, false, null, false);
            var r = new CryptoRationaleView(11, "KRW-BTC", "BTC", 1, 100_000_000, 100_000_000_00000000, At, "호가 매수벽 집중 · 돌파", 42, 65,
                103_000_000_00000000, 98_000_000_00000000, 1.5, 0, 1800, 120, 300, 40, 5_000_000, 2_000_000, "요약", gov);
            var l = PlutoThoughtComposer.Of(r, "비트코인");
            Assert.StartsWith("[관찰] 호가 매수벽 집중 · 돌파 · 감성 낙관 +42 · 호가 매수벽 우위 18.0% · 📈 상승추세", l.Text);
            Assert.Contains("[논쟁] 추세관 진입 찬성(원확신도 78) vs 리스크관 경고(과열 -8 · 매도벽 -5)", l.Text);
            Assert.Contains("[결정] 최종 확신도 65 · 동적 비중 6% 진입 · 목표 +3.0% · 손절 -2.0% (손익비 1.50)", l.Text);
            Assert.Equal("비트코인", l.Name);
        }

        [Fact]
        public void Thought_Sell_And_No_Governance_Are_Honest()
        {
            var exit = new ArenaGovernanceCard(ArenaRegimeCode.ChoppyRange, Array.Empty<ArenaPenaltyTag>(), 0, 0, 0, 1, false, "손절 — 국면 전환으로 근거 소멸", false);
            var s = new KrxRationaleView(5, 5930, -1, 10, 69_000, At, "손절", -20, 40, 0, 0, 0, -12_000, "요약", exit);
            var ls = PlutoThoughtComposer.Of(s, "삼성전자");
            Assert.Contains("보유 논리 vs 청산 논리 — 손절 — 국면 전환으로 근거 소멸", ls.Debate);
            Assert.Equal("청산 — 손실 확정(오답 노트 기록)", ls.Decide);

            var b = new UsRationaleView(6, "NVDA", 1, 3, 0, At, "", 0, 55, 0, 0, double.NaN, 0, 0, 0, 0, 0, "요약");
            var lb = PlutoThoughtComposer.Of(b, "엔비디아");
            Assert.Equal("시그널 감지 · 감성 중립 0", lb.Observe);
            Assert.Contains("거버넌스 판독 없음", lb.Debate);
            Assert.Equal("확신도 55 진입", lb.Decide);                        // 가격 0 = 계획 미표기(날조 0)
        }

        [Fact]
        public void PostMortem_Loss_Notes_Cite_Real_Progress_And_Veto_Has_Zero_Samples()
        {
            var lv = new PlutoLearningView(true, PlutoLearningHealth.Live, null, -1, false, 2, 5, 3, 7, 1, 0, EvolveAnchor: 0);
            var win = new ArenaClosedTradeView(1, At, "005930", "삼성전자", 10, 70_000, 72_000, 18_000, 257, ArenaExitKind.TakeProfit, "익절", "");
            Assert.Null(PlutoPostMortemComposer.FromClosed(win, true, in lv));

            var sl = new ArenaClosedTradeView(2, At, "005930", "삼성전자", 10, 70_000, 65_000, -52_000, -742, ArenaExitKind.StopLoss, "손절선 도달", "");
            var n = PlutoPostMortemComposer.FromClosed(sl, true, in lv)!;
            Assert.Equal(PlutoPostMortemKind.StopLoss, n.Kind);
            Assert.Contains("-7.42%", n.Headline);
            Assert.Contains("엄격도 압력 +1", n.Calibration);
            Assert.Contains("7 / 20", n.Calibration);                            // 실제 진화 진행도 인용
            Assert.Contains("현재 G2 모멘텀 +5% · 엄격도 +3점", n.Calibration);
            Assert.Contains("엔진 사유: 손절선 도달", n.WhyWrong);

            var miss = new ArenaClosedTradeView(3, At, "NVDA", "엔비디아", 3, 100_00, 99_00, -310, -103, ArenaExitKind.SignalReversal, "", "");
            var m = PlutoPostMortemComposer.FromClosed(miss, false, default)!;
            Assert.Equal(PlutoPostMortemKind.MissedProfit, m.Kind);
            Assert.Contains("모멘텀 tilt 압력 −1", PlutoPostMortemComposer.FromClosed(miss, true, in lv)!.Calibration);
            Assert.Equal("학습 엔진 미결선 — 이 회고는 텐서 보정에 반영되지 않습니다", m.Calibration);

            var veto = new ArenaCouncilVetoFeed(true, 4, new[] { new ArenaCouncilVetoView(9, "KRW-ETH", At, 72, "변동성 과열 · VaR 초과", 5) });
            var all = PlutoPostMortemComposer.Collect(new[] { win, sl, miss }, veto, c => c == "KRW-ETH" ? "이더리움" : c, true, in lv);
            Assert.Equal(3, all.Count);
            Assert.Equal(PlutoPostMortemKind.Veto, all[2].Kind);
            Assert.Contains("보정 표본 0", all[2].Calibration);
            Assert.Equal("이더리움", all[2].Name);
            Assert.Equal(2, PlutoPostMortemComposer.Collect(new[] { sl, miss }, ArenaCouncilVetoFeed.Unwired, c => c, true, in lv).Count);
        }

        [Fact]
        public void Concept_Guide_Four_Cards()
        {
            Assert.Equal(new[] { "cvd", "kelly", "bollinger", "regime" }, PlutoConceptGuide.All.Select(c => c.Key));
            Assert.All(PlutoConceptGuide.All, c => { Assert.NotEmpty(c.WhyPluto); Assert.NotEmpty(c.HumanLesson); Assert.NotEmpty(c.Formula); });
            Assert.Null(PlutoConceptGuide.Get("rsi"));
            Assert.Null(PlutoConceptGuide.Get(null));
            Assert.Contains("켈리", PlutoConceptGuide.Get(PlutoConceptGuide.Kelly)!.Title);
        }

        [Fact]
        public void Coach_Spread_Size_Risk_Grades()
        {
            // 촘촘한 스프레드 · 5% 비중 · 상승 국면 = 양호(손절 안내)
            var good = PlutoOrderCoach.Evaluate(0, 1, true, 70_000, 70_010, 7, 1, 10_000_000, 10_000_000, 0, ArenaRegimeCode.TrendingBull, false);
            Assert.Equal(PlutoCoachGrade.Good, good.Grade);
            Assert.Contains("자본의 4.9%", good.Items[1].Text);
            Assert.Contains("-7%", good.Items[2].Text);

            // 넓은 스프레드(≥ 0.5%) · 과다 베팅(> 25%) · 패닉 = 경고
            var bad = PlutoOrderCoach.Evaluate(0, 1, true, 10_000, 10_100, 300, 1, 10_000_000, 10_000_000, 0, ArenaRegimeCode.PanicLiquidation, false);
            Assert.Equal(PlutoCoachGrade.Warn, bad.Grade);
            Assert.All(bad.Items, i => Assert.Equal(PlutoCoachGrade.Warn, i.Grade));
            Assert.Contains("과다 베팅", bad.Items[1].Text);
            Assert.Contains("신규 매수를 차단", bad.Items[2].Text);

            // 예수금 초과 · 쿨다운 = 경고/주의
            var cash = PlutoOrderCoach.Evaluate(1, 1, false, 100_00, 100_02, 50, 1, 1_000_000, 1_000, 0, ArenaRegimeCode.Unknown, true);
            Assert.Contains("예수금", cash.Items[1].Text);
            Assert.Equal(PlutoCoachGrade.Caution, cash.Items[2].Grade);
            Assert.EndsWith("접수 거부된 주문", cash.Headline);

            // 코인 raw 스케일(10^8) · 미보유 매도 = 경고
            var coin = PlutoOrderCoach.Evaluate(2, -1, true, 90_000_000L * 100_000_000, 90_010_000L * 100_000_000, 1_000_000, 100_000_000, 5_000_000L * 100_000_000, 0, 0, 0, false);
            Assert.Contains("공매도", coin.Items[1].Text);
            Assert.Equal(PlutoCoachGrade.Warn, coin.Grade);
            var coinBuy = PlutoOrderCoach.Evaluate(2, 1, true, 90_000_000L * 100_000_000, 90_010_000L * 100_000_000, 1_000_000, 100_000_000, 5_000_000L * 100_000_000, 5_000_000L * 100_000_000, 0, 0, false);
            Assert.Contains("자본의 18.0%", coinBuy.Items[1].Text);              // 0.01 BTC × 9,001만 = 90만 / 500만

            // 호가 미수신 = 판단 불가(주의)
            Assert.Contains("호가 미수신", PlutoOrderCoach.Evaluate(0, 1, true, 0, 0, 1, 1, 1, 1, 0, 0, false).Items[0].Text);
        }

        private static PlutoReplayRunView SampleRun(bool evolved) => new(PlutoReplayStateCode.Completed, 412, "2020-21 V자 반등 유동성 장세",
            new[]
            {
                new PlutoReplayLaneView(PlutoRecoMarket.Krx, 2, 100, 58, "2020-21 V자 반등 유동성 장세",
                    new[]
                    {
                        new PlutoReplayRoundView(1, 34, 20, 0, evolved ? 1 : 0, 5, evolved ? 10 : 5, -1, evolved ? -3 : -1, evolved ? PlutoShadowVerdictCode.Passed : PlutoShadowVerdictCode.WinRateDegraded, 5500, 5820),
                        new PlutoReplayRoundView(2, 33, 19, evolved ? 1 : 0, evolved ? 2 : 0, evolved ? 10 : 5, evolved ? 15 : 5, evolved ? -3 : -1, evolved ? -5 : -1, evolved ? PlutoShadowVerdictCode.Passed : PlutoShadowVerdictCode.NoChange, 5600, 5900),
                        new PlutoReplayRoundView(3, 33, 19, evolved ? 2 : 0, evolved ? 2 : 0, evolved ? 15 : 5, evolved ? 15 : 5, evolved ? -5 : -1, evolved ? -5 : -1, PlutoShadowVerdictCode.InsufficientHoldout, 0, 0),
                    },
                    new PlutoLessonTallyView(40, 18, 10, 20, 7, 5, 310, -280)),
            }, new DateTime(2026, 10, 27, 1, 0, 0, DateTimeKind.Utc), 2);

        [Fact]
        public void Replay_Views_And_Study_Journal()
        {
            var run = SampleRun(true);
            var r1 = run.Lanes[0].Rounds[0];
            Assert.True(r1.Swapped);
            Assert.Equal("G0 → G1", r1.GenText);
            Assert.Equal("+3.2%p", r1.GainText);
            Assert.Equal("모멘텀 +5% · 엄격도 -2점", r1.DeltaText);
            Assert.Equal(0, run.Lanes[0].Rounds[2].GainBp);                     // 홀드아웃 부족 = 비교 없음
            Assert.Equal(2, run.Evolutions);
            Assert.Equal("자율 학습 가속 #2 완료 — 가상 회고 100표본(합성) · 진화 2회 · 412ms", run.Summary);

            var j = PlutoStudyJournal.Compose(run);
            Assert.Equal(4, j.Count);
            Assert.Contains("KRX 2종 100회", j[0].Body);
            Assert.Contains("합성 리플레이", j[0].Body);
            Assert.Contains("58승 42패(승률 58.0%)", j[1].Body);
            Assert.Contains("2회(G0 → G2) · 채택 라운드 섀도우 승률 개선 합계 +6.2%p", j[1].Body);
            Assert.Contains("악마의 변호인 엄격도를 높여", j[2].Body);            // 충격+역추세(30) > 전환+약화(12)
            Assert.Contains("20표본", j[3].Body);

            var stuck = PlutoStudyJournal.Compose(SampleRun(false));
            Assert.Contains("0회 — 섀도우 검증이 모든 후보를 거부", stuck[1].Body);

            var idle = PlutoStudyJournal.Compose(PlutoReplayRunView.Unwired);
            Assert.Single(idle);
            Assert.Contains("미결선", idle[0].Body);
            Assert.Equal(PlutoReplayStateCode.Off, PlutoReplayStateCode.Normalize(99));
        }

        [Fact]
        public void Cell_View_Prior_Replay_Delta_Texts()
        {
            var c = new PlutoTensorCellView(8, "코로나", 2, 0, 0, 0, 0, 0, 0, 0, 0, -10, 5, true, 34, 20, 0, 0);
            Assert.Equal("사전 -10% · +5점", c.PriorText);
            Assert.Equal("리플레이 34표본 · 58.8%", c.ReplayText);
            Assert.Equal("", c.DeltaText);                                     // 세대 0 = Δ 미표기
            var d = new PlutoTensorCellView(8, "코로나", 2, 2, 5, 3, 3, 0, 0, 0, 0, LastDeltaTilt: 5, LastDeltaStrict: -2);
            Assert.Equal("Δ +5% · -2점", d.DeltaText);
            Assert.Equal("", d.PriorText);
            Assert.Equal("", d.ReplayText);
            Assert.Equal("국면 사전 가중치", PlutoTensorSourceCode.Label(PlutoTensorSourceCode.Prior));
            Assert.Equal(PlutoTensorSourceCode.Prior, (int)PlutoTensorSource.Prior);
        }

        [Fact]
        public async Task Adapter_Maps_Cells_Priors_And_Replay_Report()
        {
            var t = new PlutoCognitiveTensorLake();
            t.LoadPriors();
            var fast = new PlutoFastReplayLearner(t, new FakeCandles(Universe()), () => 8, () => T0);
            var swarm = new PlutoContinuousLearningSwarm(new PlutoGlobalDataIngestor(() => T0), new PlutoEpisodicMemoryStore(), new PlutoWeightCalibrator(), new PlutoCognitiveWeightSeqlock(),
                () => T0, tensor: t);
            var port = new PlutoLearningPortAdapter(swarm, "거시 피드: 정적", tensor: t, fastReplay: fast, macroFeedTextNow: () => "거시 피드: 로컬 캔들 추정");
            Assert.True(port.CanFastReplay);
            Assert.Equal(PlutoReplayStateCode.Idle, port.LastReplay.State);
            Assert.Equal("거시 피드: 로컬 캔들 추정", port.Current.MacroFeedText);

            var view = await port.RunFastReplayAsync();
            Assert.True(view.Completed);
            Assert.Equal("2020 코로나 팬데믹 폭락", view.RegimeName);
            Assert.Equal(3, view.Lanes.Count);
            Assert.All(view.Lanes, l => Assert.Equal("2020 코로나 팬데믹 폭락", l.TargetName));
            Assert.Equal(300, view.Samples);
            Assert.Equal(view.Summary, port.LastReplay.Summary);

            var cells = port.ReadTensorCells();
            var covidKrx = cells.Single(x => x.RegimeId == 8 && x.Asset == PlutoRecoMarket.Krx);
            Assert.True(covidKrx.HasPrior);
            Assert.Equal(PlutoRegimePriors.Of(8, PlutoAssetClass.Krx).MomentumTiltPct, covidKrx.PriorTiltPct);
            Assert.Equal(100, covidKrx.ReplaySamples);
            Assert.False(cells.Single(x => x.IsBaseline && x.Asset == PlutoRecoMarket.Us).HasPrior);

            IPlutoLearningPort unwired = UnwiredPlutoLearningPort.Instance;
            Assert.False(unwired.CanFastReplay);
            Assert.Equal(PlutoReplayStateCode.Off, (await unwired.RunFastReplayAsync()).State);
            swarm.Dispose();
        }

        // ── §5 UI ─────────────────────────────────────────────────────────

        [Fact]
        public void Components_Render_Stream_Notes_Coach_Concept_Journal()
        {
            using var ctx = new Bunit.TestContext();
            var empty = ctx.RenderComponent<PlutoThoughtStream>();
            Assert.NotNull(empty.Find("[data-pts-empty]"));
            var line = new PlutoThoughtLine(3, At, "삼성전자", 1, "관찰", "논쟁", "결정");
            var stream = ctx.RenderComponent<PlutoThoughtStream>(p => p.Add(x => x.Lines, new[] { line }));
            Assert.Contains("[결정]", stream.Find("[data-pts-seq='3']").TextContent);

            Assert.Empty(ctx.RenderComponent<PlutoPostMortemCard>().FindAll(".ppm"));
            var note = PlutoPostMortemComposer.FromVeto(new ArenaCouncilVetoView(9, "KRW-ETH", At, 72, "VaR 초과", 5), "이더리움");
            var card = ctx.RenderComponent<PlutoPostMortemCard>(p => p.Add(x => x.Notes, new[] { note }));
            Assert.Equal("veto", card.Find("[data-ppm-seq='9']").GetAttribute("data-ppm-kind"));

            var coach = ctx.RenderComponent<PlutoCoachBadge>(p => p.Add(x => x.Feedback,
                PlutoOrderCoach.Evaluate(0, 1, true, 10_000, 10_100, 300, 1, 10_000_000, 10_000_000, 0, ArenaRegimeCode.PanicLiquidation, false)));
            Assert.Equal("warn", coach.Find("[data-coach]").GetAttribute("data-coach"));
            Assert.Equal(3, coach.FindAll("[data-coach-topic]").Count);
            Assert.Empty(ctx.RenderComponent<PlutoCoachBadge>().FindAll("[data-coach]"));

            var tutor = ctx.RenderComponent<PlutoConceptTutor>();
            Assert.Empty(tutor.FindAll("[data-pct-card]"));
            tutor.Find("[data-pct-key='cvd']").Click();
            Assert.Contains("CVD", tutor.Find("[data-pct-card='cvd']").TextContent);
            Assert.Contains("인간 투자자가 배울 점", tutor.Markup);
            tutor.Find("[data-pct-key='cvd']").Click();
            Assert.Empty(tutor.FindAll("[data-pct-card]"));

            var journal = ctx.RenderComponent<PlutoStudyJournalPanel>(p => p.Add(x => x.Run, SampleRun(true)));
            Assert.Equal(3, journal.FindAll("[data-psj-round]").Count);
            Assert.Equal("320", journal.Find("[data-psj-round='1']").QuerySelector("[data-psj-gain]")!.GetAttribute("data-psj-gain"));
            Assert.Contains("up", journal.Find("[data-psj-round='1'] [data-psj-gain]").ClassName);
            Assert.Equal(4, journal.FindAll("[data-psj-journal] li").Count);
            var unwired = ctx.RenderComponent<PlutoStudyJournalPanel>();
            Assert.Empty(unwired.FindAll("[data-psj-rounds]"));
        }

        [Fact]
        public void Inspector_Fast_Replay_Button_Prior_Cells_And_Journal()
        {
            using var ctx = new Bunit.TestContext();
            int clicks = 0;
            var cells = new List<PlutoTensorCellView>();
            for (int r = 1; r <= 12; r++)
                for (int a = 0; a < 3; a++)
                    cells.Add(new PlutoTensorCellView(r, "국면" + r, a, 0, 0, 0, 0, 0, 0, 0, 0, -5, 4, true, r == 8 ? 34 : 0, r == 8 ? 20 : 0));
            for (int a = 0; a < 3; a++) cells.Add(new PlutoTensorCellView(-1, "자산 기준선", a, 0, 0, 0, 0, 0, 0, 0, 0));
            var cut = ctx.RenderComponent<PlutoCognitiveInspector>(p => p
                .Add(x => x.Wired, true)
                .Add(x => x.Cells, cells)
                .Add(x => x.CanReplay, true)
                .Add(x => x.Replay, SampleRun(true))
                .Add(x => x.OnFastReplay, () => clicks++));
            var btn = cut.Find("[data-pci-fast]");
            Assert.Equal("ready", btn.GetAttribute("data-pci-fast"));
            Assert.Contains("100회 리플레이", btn.TextContent);
            btn.Click();
            Assert.Equal(1, clicks);
            Assert.Equal(36, cut.FindAll("[data-pci-prior]").Count);
            Assert.Equal("-5:4", cut.Find("[data-pci-cell='1:0'] [data-pci-prior]").GetAttribute("data-pci-prior"));
            Assert.Contains("가중치 미보정", cut.Find("[data-pci-cell='-1:0']").TextContent);   // 기준선 = 사전 없음
            Assert.Contains("리플레이 34표본", cut.Find("[data-pci-cell='8:0']").TextContent);
            Assert.Contains("(합성)", cut.Find("[data-pci-cell='8:0']").TextContent);
            Assert.NotNull(cut.Find("[data-pci-sec='journal'] [data-psj-journal]"));
            Assert.NotNull(cut.Find("[data-pct]"));                            // 개념 과외 칩

            var off = ctx.RenderComponent<PlutoCognitiveInspector>(p => p.Add(x => x.Wired, true).Add(x => x.Replaying, true).Add(x => x.CanReplay, true).Add(x => x.OnFastReplay, () => { }));
            Assert.True(off.Find("[data-pci-fast]").HasAttribute("disabled"));
            Assert.Equal("running", off.Find("[data-pci-fast]").GetAttribute("data-pci-fast"));
        }

        private const long Samsung = 5930;
        private static readonly DateTime Open10 = new(2026, 10, 22, 10, 0, 0);

        private static KrxArenaPortAdapter QuotedKrx()
        {
            var e = new KrxArenaEngine();
            e.OnQuote(Samsung, 70_000, 70_100, 1000, 1000, 100, Open10);
            var f = new KrxArenaQuoteFeeder(e);
            f.Pump(Open10.AddHours(-9).Ticks);
            return new KrxArenaPortAdapter(e, f);
        }

        private sealed class FastPort : IPlutoLearningPort
        {
            public int Runs;
            public bool IsWired => true;
            public PlutoLearningView Current => new(true, PlutoLearningHealth.Live, null, -1, false, 0, 0, 0, 0, 0, 0);
            public bool CanFastReplay => true;
            public PlutoReplayRunView LastReplay { get; private set; } = PlutoReplayRunView.Idle;
            public Task<PlutoReplayRunView> RunFastReplayAsync() { Runs++; LastReplay = SampleRun(true); return Task.FromResult(LastReplay); }
        }

        private static (Bunit.TestContext Ctx, IRenderedComponent<DualArenaPage> Cut) RenderPage(IKrxArenaPort krx, IPlutoLearningPort learning)
        {
            var ctx = new Bunit.TestContext();
            ctx.Services.AddSingleton(krx);
            ctx.Services.AddSingleton<IUsArenaPort>(new UsArenaPortAdapter(new UsTechArenaEngine()));
            ctx.Services.AddSingleton<ICryptoArenaPort>(new CryptoArenaPortAdapter(new UpbitCryptoArenaEngine()));
            ctx.Services.AddSingleton<IArenaPreferencePort>(UnwiredArenaPreferencePort.Instance);
            ctx.Services.AddSingleton(learning);
            ctx.Services.AddSingleton<IPlutoRecommendationPort>(UnwiredPlutoRecommendationPort.Instance);
            return (ctx, ctx.RenderComponent<DualArenaPage>());
        }

        [Fact]
        public void Page_Unwired_Fast_Button_Disabled_Concept_Bar_Present()
        {
            var (ctx, cut) = RenderPage(QuotedKrx(), UnwiredPlutoLearningPort.Instance);
            using (ctx)
            {
                var b = cut.Find("[data-fast]");
                Assert.Equal("off", b.GetAttribute("data-fast"));
                Assert.True(b.HasAttribute("disabled"));
                Assert.NotNull(cut.Find("[data-pct]"));
            }
        }

        [Fact]
        public void Page_Fast_Replay_Click_Shows_Summary_And_Human_Buy_Shows_Coach()
        {
            var port = new FastPort();
            var (ctx, cut) = RenderPage(QuotedKrx(), port);
            using (ctx)
            {
                var b = cut.Find("[data-fast]");
                Assert.Equal("ready", b.GetAttribute("data-fast"));
                b.Click();
                Assert.Equal(1, port.Runs);
                Assert.Contains("자율 학습 가속 #2 완료", cut.Find("[data-fast-msg]").TextContent);

                cut.Find(".da-ticket .ar-btn.buy").Click();
                var coach = cut.Find(".da-ticket [data-coach]");
                Assert.Equal("1", coach.GetAttribute("data-coach-side"));
                Assert.Equal(3, coach.QuerySelectorAll("[data-coach-topic]").Length);
            }
        }
    }
}
