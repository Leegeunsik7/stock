// ============================================================================
// PlutoCouncilTests — v12.18.88-DEV: 4대 인지 에이전트 위원회 · 교차 자산 선행-지행 파급 · 프랙탈 켈리 사이징
//
//   ① 위원회 39셀 합의 가중치(합 10000 · 하한 500 · 국면 전략별 주도 에이전트 · 학습 투영) · 에이전트 점수 · 비토(VIX · 파급 충격 · 패닉 국면 · 패닉 투매).
//   ② 랭킹 결선: 위원회 미결선 = 기존 결과와 완전 동일 · 비토 = 탈락(CouncilVetoed) · KRX 만 선행 점수 가산 · 켈리 Sized = 추천 수량 켈리 비중 기준.
//   ③ 파급: 선행 상관(Pearson · 쌍 ≥ 10) · 상관 미추정 = 빅테크 기여 0 · 10년물 · BTC 변동성(단기/기준 창 분리) · 충격 · 과거 키 무시.
//   ④ 켈리: f* = p − (1−p)/b · ½ 켈리 · 변동성 배율 · [5%, 25%] · 표본 부족/음의 엣지 정직 상태 · 보정기 실측 엣지 · ATR.
//   ⑤ 허브: 스크리너 스텝 → 거시 충격 비토 · 39셀 보드 게시 · 포트 투영 · ⑥ δ=0: 핫패스 조회 10만 회 0B.
// ============================================================================
using System;
using System.Linq;
using MS2E.UI.Portable.Ports.Trading;
using StockManager.Adapters;
using StockManager.Engines.Arena.Domestic;
using StockManager.Engines.Cognitive.Council;
using StockManager.Engines.Cognitive.Governance;
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
    public sealed class PlutoCouncilTests
    {
        private const long T0 = 638_000_000_000_000_000;

        private static PlutoScreenInputs In(long sym, int? val, int? tech, int? micro, MarketRegime regime = MarketRegime.TrendingBull,
            PlutoScreenMarket market = PlutoScreenMarket.Krx, long price = 50_000, bool bidWall = false, bool depthTrap = false)
            => new(market, sym, price,
                val.HasValue, val ?? 0, val.HasValue ? 2000 : 0, val.HasValue ? PlutoValuationGrade.FairDiscount : PlutoValuationGrade.Unknown, val.HasValue ? 62_500 : 0, val.HasValue,
                tech.HasValue, tech ?? 0, tech.HasValue ? PlutoSqueezePhase.BreakoutUp : default, tech.HasValue ? PlutoProfileZone.PocSupport : default,
                micro.HasValue, micro ?? 0, PlutoDivergenceType.None, micro.HasValue, bidWall, micro.HasValue && depthTrap,
                regime);

        private static PlutoSpilloverSignal Shock() =>
            new(PlutoSpilloverField.Us10y, PlutoSpilloverShock.Us10yJump, 30, 0, 0, 0, 0, 0, -3, 1, T0);

        private static PlutoCouncilInputs Inputs(in PlutoCouncilMacro m, int regime = 0, PlutoEdgeStats edge = default, bool edgeWired = false) =>
            new(in m, regime, default, in edge, edgeWired, false, null);

        // ── ① 위원회 가중치 · 에이전트 · 비토 ────────────────────────────────

        [Fact]
        public void All_39_Cells_Sum_To_10000_With_Floor_And_Strategy_Lead()
        {
            Assert.Equal(39, PlutoCognitiveCouncil.Cells);
            for (int cell = 0; cell < PlutoCognitiveCouncil.Cells; cell++)
            {
                var w = PlutoCognitiveCouncil.BaseOf(cell);
                Assert.Equal(10000, w.Sum);
                Assert.All(new[] { w.TrendBp, w.ReversionBp, w.LiquidityBp, w.RiskBp }, x => Assert.True(x >= PlutoCognitiveCouncil.MinAgentBp));
            }
            var baseline = PlutoCognitiveCouncil.BaseOf(PlutoCognitiveTensorLake.CellOf(PlutoAssetClass.Krx, 0));
            Assert.Equal((2500, 2500, 2500, 2500), (baseline.TrendBp, baseline.ReversionBp, baseline.LiquidityBp, baseline.RiskBp));   // 판정 전 = 균등
            int checkedRegimes = 0;
            for (int id = 1; id <= PlutoRegimeVectorLake.Count; id++)
            {
                var cycle = PlutoRegimeVectorLake.Get(id)!;
                var w = PlutoCognitiveCouncil.BaseOf(PlutoCognitiveTensorLake.CellOf(PlutoAssetClass.Krx, id));
                var up = PlutoCognitiveCouncil.BaseOf(PlutoCognitiveTensorLake.CellOf(PlutoAssetClass.Upbit, id));
                Assert.True(up.LiquidityBp > w.LiquidityBp);                                                         // 업비트 열 = 수급 강화
                if (cycle.Kind == PlutoEpisodeKind.Liquidity) continue;
                var expected = cycle.Strategy switch
                {
                    PlutoRegimeStrategy.TrendFollow => PlutoCouncilAgent.TrendScout,
                    PlutoRegimeStrategy.MeanReversion => PlutoCouncilAgent.MeanReversionHunter,
                    PlutoRegimeStrategy.DevilsAdvocate => PlutoCouncilAgent.ChiefRiskOfficer,
                    _ => PlutoCouncilAgent.TrendScout,
                };
                if (cycle.Strategy != PlutoRegimeStrategy.None) { Assert.Equal(expected, w.Lead); checkedRegimes++; }
            }
            Assert.True(checkedRegimes >= 3);
        }

        [Fact]
        public void Learned_Weights_Shift_Trend_And_Risk_Keeping_Sum()
        {
            var neutral = PlutoCognitiveCouncil.Weights(PlutoAssetClass.Krx, 0, default);
            var bull = PlutoCognitiveCouncil.Weights(PlutoAssetClass.Krx, 0, new PlutoCognitiveWeights(3, 25, 0));
            var strict = PlutoCognitiveCouncil.Weights(PlutoAssetClass.Krx, 0, new PlutoCognitiveWeights(3, 0, 20));
            var malformed = PlutoCognitiveCouncil.Weights(PlutoAssetClass.Krx, 0, new PlutoCognitiveWeights(3, 999, 0));
            Assert.Equal(10000, bull.Sum);
            Assert.Equal(10000, strict.Sum);
            Assert.Equal(3000, bull.TrendBp);                    // +25% × 20bp = +500(역발상 −500)
            Assert.Equal(2000, bull.ReversionBp);
            Assert.True(strict.RiskBp > neutral.RiskBp + 900);   // +20점 × 50bp = +1000
            Assert.Equal(neutral, malformed);                    // 무결성 위반 = 기본 그대로
            var extreme = PlutoCognitiveCouncil.Weights(PlutoAssetClass.Krx, 0, new PlutoCognitiveWeights(3, 50, 20));
            Assert.Equal(10000, extreme.Sum);
            Assert.True(extreme.ReversionBp >= PlutoCognitiveCouncil.MinAgentBp);
        }

        [Fact]
        public void Agents_Score_From_Real_Factors_Only()
        {
            var x = In(1, val: 60, tech: 40, micro: 30, bidWall: true);
            Assert.Equal(60, PlutoCognitiveCouncil.TrendScout(in x));                                   // 40 + 상승 추세 20
            Assert.Equal(40, PlutoCognitiveCouncil.MeanReversionHunter(in x));                          // 60 − 40/2
            Assert.Equal(55, PlutoCognitiveCouncil.LiquidityPredator(in x));                            // 30 + 벽 15 + 매수 우위 10
            var none = In(2, null, null, null, MarketRegime.Unknown);
            Assert.Equal((0, 0, 0), (PlutoCognitiveCouncil.TrendScout(in none), PlutoCognitiveCouncil.MeanReversionHunter(in none), PlutoCognitiveCouncil.LiquidityPredator(in none)));
            var s = PlutoDiscoveryScorer.Score(in x, default);
            Assert.Equal(PlutoCognitiveCouncil.CalmBonus, PlutoCognitiveCouncil.ChiefRiskOfficer(in s, PlutoCouncilMacro.None));
            var stressed = new PlutoCouncilMacro(3000, PlutoEpisodeKind.Bear, 70, default);
            Assert.Equal(-10 - 30, PlutoCognitiveCouncil.ChiefRiskOfficer(in s, in stressed));          // VIX 30 → −10 · 약세 국면 유사 −30
        }

        [Theory]
        [InlineData(3500, PlutoEpisodeKind.Unknown, 0, false, MarketRegime.TrendingBull, PlutoVetoReason.VixSpike)]
        [InlineData(3499, PlutoEpisodeKind.Unknown, 0, false, MarketRegime.TrendingBull, PlutoVetoReason.None)]
        [InlineData(-1, PlutoEpisodeKind.Unknown, 0, true, MarketRegime.TrendingBull, PlutoVetoReason.SpilloverShock)]
        [InlineData(-1, PlutoEpisodeKind.Panic, 60, false, MarketRegime.TrendingBull, PlutoVetoReason.PanicRegimeMatch)]
        [InlineData(-1, PlutoEpisodeKind.Panic, 59, false, MarketRegime.TrendingBull, PlutoVetoReason.None)]
        [InlineData(-1, PlutoEpisodeKind.Unknown, 0, false, MarketRegime.PanicLiquidation, PlutoVetoReason.PanicLiquidation)]
        public void Chief_Risk_Officer_Veto_Triggers(int vix, PlutoEpisodeKind kind, int sim, bool shock, MarketRegime regime, PlutoVetoReason expected)
        {
            var m = new PlutoCouncilMacro(vix, kind, sim, shock ? Shock() : default);
            var x = In(1, 60, 40, 30, regime);
            Assert.Equal(expected, PlutoCognitiveCouncil.VetoOf(in x, in m));
            var v = PlutoCognitiveCouncil.Convene(in x, PlutoDiscoveryScorer.Score(in x, default), in m, 0, default);
            Assert.Equal(expected != PlutoVetoReason.None, v.Vetoed);
            Assert.True(v.IsConvened);
            Assert.InRange(v.Consensus, -100, 100);
        }

        // ── ② 랭킹 결선 ───────────────────────────────────────────────────────

        private static readonly PlutoScreenInputs[] s_universe =
        {
            In(5930, 60, 40, 30), In(660, 40, 60, 20), In(3550, 20, 30, 50), In(9999, null, null, null),
        };

        [Fact]
        public void Unwired_Council_Is_Identical_To_Legacy_Ranking()
        {
            var legacy = PlutoRecommendationEngine.Rank(s_universe, PlutoScreenMarket.Krx, default, default, KrxArenaEngine.DefaultCapitalWon, 200, 5, out var t1);
            var unwired = PlutoRecommendationEngine.Rank(s_universe, PlutoScreenMarket.Krx, default, default, KrxArenaEngine.DefaultCapitalWon, 200, 5, default, out var t2);
            Assert.Equal(t1, t2);
            Assert.Equal(legacy.Select(r => (r.Symbol, r.FinalScore, r.RecommendedQty)), unwired.Select(r => (r.Symbol, r.FinalScore, r.RecommendedQty)));
            Assert.All(unwired, r => Assert.False(r.CouncilWired));
            Assert.True(legacy.Length >= 2);
        }

        [Fact]
        public void Veto_Drops_All_Picks_And_Counts_Them()
        {
            var m = new PlutoCouncilMacro(4000, PlutoEpisodeKind.Unknown, 0, default);
            var picks = PlutoRecommendationEngine.Rank(s_universe, PlutoScreenMarket.Krx, default, default, KrxArenaEngine.DefaultCapitalWon, 200, 5, Inputs(in m), out var t);
            Assert.Empty(picks);
            Assert.Equal(3, t.CouncilVetoed);       // 근거 부족 1건은 비토 전에 집계(점수 산출 0)
            Assert.Equal(1, t.Insufficient);
            Assert.Equal(0, t.Passed);
        }

        [Fact]
        public void Lead_Points_Apply_To_Krx_Only_And_Kelly_Drives_Qty()
        {
            var lead = new PlutoSpilloverSignal(PlutoSpilloverField.BigTech | PlutoSpilloverField.LeadCorr, PlutoSpilloverShock.None, 0, 150, 0, 0, 1000, 12, 3, 1, T0);
            var m = new PlutoCouncilMacro(-1, PlutoEpisodeKind.Unknown, 0, in lead);
            var edge = new PlutoEdgeStats(20, 12, 8, 12 * 150, 8 * 100);   // p 60% · b 1.5 → ½켈리 16.67%
            var plain = PlutoRecommendationEngine.Rank(s_universe, PlutoScreenMarket.Krx, default, default, KrxArenaEngine.DefaultCapitalWon, 200, 5, out _);
            var wired = PlutoRecommendationEngine.Rank(s_universe, PlutoScreenMarket.Krx, default, default, KrxArenaEngine.DefaultCapitalWon, 200, 5,
                Inputs(in m, edge: edge, edgeWired: true), out _);
            Assert.Equal(plain.Length, wired.Length);
            for (int i = 0; i < plain.Length; i++)
            {
                Assert.Equal(Math.Min(100, plain[i].FinalScore + 3), wired[i].FinalScore);
                Assert.Equal(3, wired[i].Adaptive.LeadPts);
                Assert.True(wired[i].Kelly.IsSized);
                Assert.Equal(1667, wired[i].Kelly.WeightBp);
                Assert.Equal(PlutoMarketScreener.RecommendedQtyBp(PlutoScreenMarket.Krx, KrxArenaEngine.DefaultCapitalWon, 1667, wired[i].Price), wired[i].RecommendedQty);
                Assert.True(wired[i].CouncilWired);
            }
            var us = new[] { In(1, 60, 40, 30, market: PlutoScreenMarket.Us, price: 20_000) };
            var usPick = PlutoRecommendationEngine.Rank(us, PlutoScreenMarket.Us, default, default, 10_000_000, 100, 5, Inputs(in m), out _);
            Assert.Equal(0, usPick.Single().Adaptive.LeadPts);   // 선행 파급 = KRX 전용
            Assert.Equal(PlutoKellyState.Unwired, usPick.Single().Kelly.State);
        }

        // ── ③ 교차 자산 파급 ──────────────────────────────────────────────────

        private static PlutoCrossAssetSpillover Correlated(int pairs, int sign, int lastUs)
        {
            var sp = new PlutoCrossAssetSpillover();
            for (int i = 0; i < pairs; i++)
            {
                int x = i == pairs - 1 ? lastUs : (i % 3 - 1) * 80 + i * 5;
                Assert.True(sp.ObserveReturn(PlutoLeadLane.BigTech, 100 + i * 2, x));
                Assert.True(sp.ObserveReturn(PlutoLeadLane.Krx, 101 + i * 2, sign * 2 * x));   // 다음 KRX 세션 = 2배 동조(또는 역행)
            }
            return sp;
        }

        [Fact]
        public void Lead_Correlation_Projects_BigTech_Into_Krx_Points()
        {
            var s = Correlated(12, +1, 100).Publish(T0);
            Assert.True(s.Has(PlutoSpilloverField.LeadCorr));
            Assert.Equal(1000, s.LeadCorrX1000);
            Assert.Equal(12, s.Pairs);
            Assert.Equal(100, s.BigTechReturnBp);
            Assert.Equal(2, s.LeadPts);                           // 100bp × 1.00 ÷ 50
            var inverse = Correlated(12, -1, 100).Publish(T0);
            Assert.Equal(-1000, inverse.LeadCorrX1000);
            Assert.Equal(0, inverse.LeadPts);                     // 음의 상관 = 빅테크 기여 0
            var few = Correlated(9, +1, 300).Publish(T0);
            Assert.False(few.Has(PlutoSpilloverField.LeadCorr));  // 쌍 9 < 10 = 미추정
            Assert.True(few.Has(PlutoSpilloverField.BigTech));
            Assert.Equal(0, few.LeadPts);
            Assert.False(new PlutoCrossAssetSpillover().Publish(T0).HasData);
        }

        [Fact]
        public void Rates_And_Btc_Volatility_Drive_Points_And_Shocks()
        {
            var sp = new PlutoCrossAssetSpillover();
            sp.ObserveUs10y(true, 30);
            var s = sp.Publish(T0);
            Assert.Equal(-3, s.LeadPts);
            Assert.True((s.Shocks & PlutoSpilloverShock.Us10yJump) != 0);
            sp.ObserveUs10y(true, -12);
            Assert.Equal(1, sp.Publish(T0).LeadPts);
            Assert.False(sp.Publish(T0).IsShock);
            sp.ObserveUs10y(false, 0);
            Assert.False(sp.Publish(T0).HasData);

            var btc = new PlutoCrossAssetSpillover();
            for (int i = 1; i <= 20; i++) btc.ObserveReturn(PlutoLeadLane.Btc, i, i % 2 == 0 ? 10 : -10);
            for (int i = 21; i <= 24; i++) btc.ObserveReturn(PlutoLeadLane.Btc, i, 17);
            Assert.False(btc.Publish(T0).Has(PlutoSpilloverField.BtcVol));   // 24 < 25 = 미추정
            btc.ObserveReturn(PlutoLeadLane.Btc, 25, -17);
            var calm = btc.Publish(T0);
            Assert.Equal(170, calm.BtcVolRatioPct);
            Assert.Equal(-1, calm.LeadPts);
            Assert.False(calm.IsShock);
            for (int i = 26; i <= 30; i++) btc.ObserveReturn(PlutoLeadLane.Btc, i, 400);
            var spike = btc.Publish(T0);
            Assert.True(spike.BtcVolRatioPct >= PlutoCrossAssetSpillover.ShockBtcVolRatioPct);
            Assert.Equal(-2, spike.LeadPts);
            Assert.True((spike.Shocks & PlutoSpilloverShock.BtcVolSpike) != 0);
        }

        [Fact]
        public void Rolling_Buffer_Ignores_Past_Keys_And_Evicts_Oldest()
        {
            var sp = new PlutoCrossAssetSpillover();
            Assert.True(sp.ObserveReturn(PlutoLeadLane.BigTech, 10, 5));
            Assert.True(sp.ObserveReturn(PlutoLeadLane.BigTech, 10, 7));    // 같은 키 = 갱신
            Assert.False(sp.ObserveReturn(PlutoLeadLane.BigTech, 9, 99));   // 과거 키 = 무시
            Assert.Equal(1, sp.CountOf(PlutoLeadLane.BigTech));
            Assert.Equal(7, sp.Publish(T0).BigTechReturnBp);
            for (int i = 11; i < 11 + PlutoCrossAssetSpillover.Capacity; i++) sp.ObserveReturn(PlutoLeadLane.BigTech, i, i);
            Assert.Equal(PlutoCrossAssetSpillover.Capacity, sp.CountOf(PlutoLeadLane.BigTech));
            Assert.Equal(10 + PlutoCrossAssetSpillover.Capacity, sp.Publish(T0).BigTechReturnBp);
            Assert.Contains("판독 없음", PlutoCrossAssetSpillover.Text(default));
        }

        [Fact]
        public void Bars_Convert_To_Returns_With_Day_Keys()
        {
            var day = TimeSpan.TicksPerDay;
            var bars = new[]
            {
                new PlutoOhlcvBar(T0, 100, 101, 99, 100, 10), new PlutoOhlcvBar(T0 + day, 100, 103, 99, 102, 10),
                new PlutoOhlcvBar(T0 + 2 * day, 0, 0, 0, 0, 0), new PlutoOhlcvBar(T0 + 3 * day, 102, 103, 100, 101, 10),
            };
            var sp = new PlutoCrossAssetSpillover();
            Assert.Equal(1, sp.ObserveBars(PlutoLeadLane.Krx, bars, daily: true));   // 무효 봉 인접 = 건너뜀
            Assert.Equal(200, PlutoCrossAssetSpillover.ReturnBp(100, 102));
            Assert.Equal(0, PlutoCrossAssetSpillover.ReturnBp(0, 102));
        }

        // ── ④ 켈리 ────────────────────────────────────────────────────────────

        [Theory]
        [InlineData(20, 12, 8, 150, 100, 0, PlutoKellyState.Sized, 1667)]      // f* 33.34% → ½ = 16.67%
        [InlineData(20, 12, 8, 150, 100, 4000, PlutoKellyState.Sized, 833)]    // ATR 40bp → 변동성 50%
        [InlineData(20, 11, 9, 100, 100, 0, PlutoKellyState.Sized, 500)]       // f* 10% → ½ = 5% 하한
        [InlineData(20, 20, 0, 200, 0, 0, PlutoKellyState.Sized, 2500)]        // 패배 0 = 손익비 5.00 포화 → 상한 25%
        [InlineData(20, 8, 12, 100, 100, 0, PlutoKellyState.NoEdge, 0)]        // 음의 엣지 = 0(하한으로 끌어올리지 않음)
        [InlineData(20, 0, 20, 0, 100, 0, PlutoKellyState.NoEdge, 0)]
        [InlineData(19, 15, 4, 300, 100, 0, PlutoKellyState.Insufficient, 0)]
        public void Fractional_Kelly_Is_Bounded_And_Honest(int n, int wins, int losses, int avgWin, int avgLoss, int atrCbp, PlutoKellyState state, int weightBp)
        {
            var k = PlutoDynamicKellyEngine.Compute(new PlutoEdgeStats(n, wins, losses, (long)wins * avgWin, (long)losses * avgLoss), atrCbp);
            Assert.Equal(state, k.State);
            Assert.Equal(weightBp, k.WeightBp);
            if (k.IsSized) Assert.InRange(k.WeightBp, PlutoDynamicKellyEngine.MinWeightBp, PlutoDynamicKellyEngine.MaxWeightBp);
            Assert.False(string.IsNullOrEmpty(PlutoDynamicKellyEngine.Text(in k)));
        }

        [Fact]
        public void Calibrator_Edge_Counts_Real_Samples_And_Lake_Falls_Back_To_Baseline()
        {
            var cal = new PlutoWeightCalibrator();
            for (int i = 0; i < 12; i++) cal.ObserveSample(new PlutoLearningSample(150, 60, 0, PlutoLesson.None, PostMortemVerdict.Win));
            for (int i = 0; i < 8; i++) cal.ObserveSample(new PlutoLearningSample(-100, 60, 0, PlutoLesson.None, PostMortemVerdict.Loss));
            var e = cal.Edge();
            Assert.Equal((20, 12, 8, 1800L, 800L), (e.Samples, e.Wins, e.Losses, e.SumWinBp, e.SumLossBp));
            Assert.Equal(6000, e.WinRateBp);
            Assert.Equal(1667, PlutoDynamicKellyEngine.Compute(in e, 0).WeightBp);
            var lake = new PlutoCognitiveTensorLake();
            var none = lake.EdgeOf(PlutoAssetClass.Krx, 3, out bool fromRegime);
            Assert.False(fromRegime);
            Assert.Equal(0, none.Samples);
            Assert.Equal(PlutoKellyState.Insufficient, PlutoDynamicKellyEngine.Compute(in none, 0).State);
        }

        [Fact]
        public void Atr_From_Bars_In_Centi_Bp()
        {
            var bars = new PlutoOhlcvBar[15];
            for (int i = 0; i < bars.Length; i++) bars[i] = new PlutoOhlcvBar(T0 + i, 10_000, 10_100, 9_900, 10_000, 1);   // TR 200 / 10,000 = 2% = 20,000 cbp
            Assert.Equal(20_000, PlutoDynamicKellyEngine.AtrCbp(bars));
            Assert.Equal(0, PlutoDynamicKellyEngine.AtrCbp(bars.AsSpan(0, 1)));
        }

        // ── ⑤ 허브 · 스크리너 · 포트 ──────────────────────────────────────────

        private static (PlutoMarketScreener Screener, PlutoCouncilHub Hub) Wired(Func<(bool, PlutoMacroSnapshot)> macro)
        {
            var hub = new PlutoCouncilHub(new PlutoCrossAssetSpillover(), new PlutoCouncilBoard(), macro: macro, tensor: new PlutoCognitiveTensorLake());
            var s = new PlutoMarketScreener(new PlutoRecommendationBoard(), null, () => T0, council: hub);
            s.AddUniverse(PlutoScreenMarket.Krx, dest => { s_universe.AsSpan().CopyTo(dest); return s_universe.Length; }, KrxArenaEngine.DefaultCapitalWon);
            return (s, hub);
        }

        [Fact]
        public void Hub_Vetoes_On_Macro_Shock_And_Publishes_39_Cells()
        {
            var (calm, calmHub) = Wired(() => (true, new PlutoMacroSnapshot(PlutoMacroField.Vix, 1800, 0, 0, 0, 0, 0, T0)));
            var snap = calm.Step();
            Assert.NotEmpty(snap.Of(PlutoScreenMarket.Krx));
            Assert.Equal(39, calmHub.Board.Published);
            Assert.True(calmHub.Board.TryRead(PlutoAssetClass.Krx, 0, out var cell));
            Assert.False(cell.Vetoed);
            Assert.Equal(10000, cell.Weights.Sum);
            Assert.Equal(PlutoKellyState.Insufficient, cell.KellyState);    // 실측 표본 0 = 비중 미산출(날조 0)

            var view = PlutoRecommendationPortAdapter.MapCouncil(calmHub, snap);
            Assert.True(view.Wired && view.Published);
            Assert.Equal(3, view.Lanes.Count);
            Assert.All(view.Lanes, l => Assert.Equal(10000, l.Sum));
            Assert.False(view.Vetoed);
            var card = PlutoRecommendationPortAdapter.Map(snap.Of(PlutoScreenMarket.Krx)[0]);
            Assert.True(card.CouncilWired);
            Assert.Equal(PlutoKellyCode.Insufficient, card.KellyState);
            Assert.Equal("0/20", PlutoKellyCode.Cell(card.KellyState, card.KellyBp, card.KellySamples));

            var (panic, panicHub) = Wired(() => (true, new PlutoMacroSnapshot(PlutoMacroField.Vix | PlutoMacroField.Us10y, 4200, 35, 0, 0, 0, 0, T0)));
            var ps = panic.Step();
            Assert.Empty(ps.Of(PlutoScreenMarket.Krx));
            Assert.Equal(3, ps.TallyOf(PlutoScreenMarket.Krx).CouncilVetoed);
            Assert.True(panicHub.Board.TryRead(PlutoAssetClass.Us, 5, out var pc));
            Assert.Equal(PlutoVetoReason.VixSpike | PlutoVetoReason.SpilloverShock, pc.Veto);
            var pv = PlutoRecommendationPortAdapter.MapCouncil(panicHub, ps);
            Assert.True(pv.Vetoed);
            Assert.Contains("VIX", pv.VetoText);
            Assert.Equal(3, pv.CouncilVetoed);
        }

        [Fact]
        public void Unprepared_Hub_Maps_Wired_But_Unpublished()
        {
            var hub = new PlutoCouncilHub(new PlutoCrossAssetSpillover(), new PlutoCouncilBoard());
            var v = PlutoRecommendationPortAdapter.MapCouncil(hub, PlutoRecommendationSnapshot.Empty);
            Assert.True(v.Wired);
            Assert.False(v.Published);
            Assert.Empty(v.Lanes);
        }

        // ── ⑥ δ=0 ─────────────────────────────────────────────────────────────

        [Fact]
        public void Hot_Path_Council_Spillover_Kelly_Lookups_Allocate_Zero_Over_100k()
        {
            var (s, hub) = Wired(() => (true, new PlutoMacroSnapshot(PlutoMacroField.Vix, 1800, 0, 0, 0, 0, 0, T0)));
            s.Step();
            var board = hub.Board;
            var spill = hub.Spillover;
            var x = In(5930, 60, 40, 30);
            var score = PlutoDiscoveryScorer.Score(in x, default);
            var m = hub.LastMacro;
            var edge = new PlutoEdgeStats(20, 12, 8, 1800, 800);
            var learned = new PlutoCognitiveWeights(2, 10, 4);
            long sink = 0;
            void Body()
            {
                for (int i = 0; i < 100_000; i++)
                {
                    board.TryRead((PlutoAssetClass)(i % 3), i % 13, out var c);
                    spill.TryRead(out var sig);
                    var v = PlutoCognitiveCouncil.Convene(in x, in score, in m, i % 13, in learned);
                    var k = PlutoDynamicKellyEngine.Compute(in edge, i & 0x3FFF);
                    sink += c.KellyBp + sig.LeadPts + v.Consensus + k.WeightBp;
                }
            }
            Body();   // JIT 예열
            Assert.Equal(0, MinAllocated(Body));
            Assert.NotEqual(long.MinValue, sink);
        }
    }
}
