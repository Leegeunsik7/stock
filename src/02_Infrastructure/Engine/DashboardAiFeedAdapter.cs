// ============================================================================
// DashboardAiFeedAdapter — v12.6.0 IDashboardAiFeedPort 구현(Main Dashboard AI Recommendation Feed Widget)
//   v12.7.0 — MultiFactorConvictionSynthesizer(4대 축 XAI) + RegimeAwareDynamicFactorWeightingEngine(국면 적응
//   가중치) 결속. 4대 축(Technical/Fundamental/Microstructure/SupplyDemand) 중 Technical·Fundamental 은 동일
//   실측 신호(평단가 대비 수익률)를 정직 재사용하고, Microstructure·SupplyDemand 는 실제 호가 불균형(imbalanceLookup)
//   을 사용한다(별도 RSI/Bollinger/거래대금 히스토리 피드가 없어 서로 다른 팩터를 날조하지 않음 — 정직 스코핑).
//
//   ★정직 데이터 소스: "전 종목 시장 데이터"를 날조하지 않고, 실제로 존재하는 IPortfolioBoardPort.Holdings
//   (보유 종목의 평단가 대비 수익률)를 정규화한 값을 팩터 입력으로 사용한다.
//   보유 종목 → 국면 분류(ClassifyRegime) → 4대 축 SIMD Top-N 선별 → IAiRecommendationPort(Advisory-Only) →
//   AiMicrostructureDivergenceGuard(호가 불균형 상충 판정) → IAiRecommendationCardView(XAI 포함)로 합성한다.
//
//   ★Advisory-Only 구조적 격리: 이 클래스는 CanaryHealthSentinel·HardwareKillSwitchBridge·OrderDispatchRing 등
//   발주/무장 계열 타입을 단 하나도 참조하지 않는다(MainDashboardAiFeedTests.cs 리플렉션 검증 대상).
// ============================================================================
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace MS2E.Infrastructure
{
    public sealed class DashboardAiFeedAdapter : MS2E.UI.Portable.Ports.Intel.IDashboardAiFeedPort
    {
        private sealed record CardView(
            string Symbol, MS2E.UI.Portable.Ports.Intel.AiRecommendationGrade Grade,
            decimal TargetPrice, decimal StopLossPrice, decimal Confidence, string Rationale,
            bool Divergent, decimal? OrderBookImbalance,
            decimal TechnicalPct, decimal FundamentalPct, decimal MicrostructurePct, decimal SupplyDemandPct)
            : MS2E.UI.Portable.Ports.Intel.IAiRecommendationCardView
        {
            public bool IsAdvisoryOnly => true;
        }

        private sealed record CandidateInput(string Symbol, decimal CurrentPrice, decimal CompositeFactorScore, decimal VolatilityPct)
            : MS2E.UI.Portable.Ports.Intel.IAiCandidateInput;

        private readonly MS2E.UI.Portable.Ports.Portfolio.IPortfolioBoardPort _board;
        private readonly MS2E.UI.Portable.Ports.Intel.IAiRecommendationPort _ai;
        private readonly MS2E.UI.Portable.Ports.Shell.IOrderPrefillPort _prefill;
        private readonly AiMicrostructureDivergenceGuard _divergenceGuard;
        private readonly MS2E.Core.RegimeAwareDynamicFactorWeightingEngine _regimeWeighting;
        private readonly Func<string, decimal?> _imbalanceLookup;

        public DashboardAiFeedAdapter(
            MS2E.UI.Portable.Ports.Portfolio.IPortfolioBoardPort board,
            MS2E.UI.Portable.Ports.Intel.IAiRecommendationPort ai,
            MS2E.UI.Portable.Ports.Shell.IOrderPrefillPort prefill,
            AiMicrostructureDivergenceGuard? divergenceGuard = null,
            Func<string, decimal?>? imbalanceLookup = null,
            MS2E.Core.RegimeAwareDynamicFactorWeightingEngine? regimeWeighting = null)
        {
            _board = board ?? throw new ArgumentNullException(nameof(board));
            _ai = ai ?? throw new ArgumentNullException(nameof(ai));
            _prefill = prefill ?? throw new ArgumentNullException(nameof(prefill));
            _divergenceGuard = divergenceGuard ?? new AiMicrostructureDivergenceGuard();
            _imbalanceLookup = imbalanceLookup ?? (_ => null);
            _regimeWeighting = regimeWeighting ?? new MS2E.Core.RegimeAwareDynamicFactorWeightingEngine();
        }

        public async Task<IReadOnlyList<MS2E.UI.Portable.Ports.Intel.IAiRecommendationCardView>> GetTopRecommendationsAsync(int topN, CancellationToken ct)
        {
            var holdings = _board.Holdings;
            int n = holdings.Count;
            if (n == 0 || topN <= 0) return Array.Empty<MS2E.UI.Portable.Ports.Intel.IAiRecommendationCardView>();

            // ★정직 팩터: 평단가 대비 수익률을 ±20%→±1.0 로 정규화(Technical/Fundamental 공용 입력).
            var technical = new float[n];
            var fundamental = new float[n];
            var microstructure = new float[n];
            var supplyDemand = new float[n];
            var retPcts = new decimal[n];
            for (int i = 0; i < n; i++)
            {
                var h = holdings[i];
                decimal retPct = h.AvgPrice > 0 ? (h.CurrentPrice - h.AvgPrice) / h.AvgPrice : 0m;
                retPcts[i] = retPct;
                float norm = (float)Math.Clamp(retPct * 5m, -1m, 1m);
                technical[i] = norm;
                fundamental[i] = norm;   // ★동일 실측 신호 재사용(별도 재무제표 피드 없음 — 정직 명기)
                supplyDemand[i] = norm;  // ★수급 축도 동일 실측 신호 재사용(별도 거래대금 흐름 피드 없음 — 정직 명기)
                decimal? imb = _imbalanceLookup(h.Symbol);
                // ★Microstructure 만 실제 별도 신호(호가 불균형)를 담는다 — 나머지 3축과 동일 값을 중복 주입하면
                //   미시구조 신호가 과대가중되어 '등급 자체가 이미 불균형을 반영' → 괴리(Divergent) 판정이 사실상
                //   발동 불가능해지는 부작용이 있었다(v12.7.0 초안에서 실측 발견·수정). 미시구조는 독립 축으로 유지.
                microstructure[i] = imb.HasValue ? (float)Math.Clamp(imb.Value, -1m, 1m) : 0f;
            }

            // v12.7.0 direction_b — 포트폴리오 평균 추세/변동성(bps)으로 시장 국면 분류 → 팩터 가중치 원자 재배치.
            decimal meanRet = 0m; foreach (var r in retPcts) meanRet += r; meanRet /= n;
            decimal variance = 0m; foreach (var r in retPcts) variance += (r - meanRet) * (r - meanRet); variance /= n;
            long trendBps = (long)(meanRet * 10_000m);
            long volBps = (long)((decimal)Math.Sqrt((double)variance) * 10_000m);
            var regime = MS2E.Core.RegimeAwareDynamicFactorWeightingEngine.ClassifyRegime(trendBps, volBps);
            _regimeWeighting.ApplyRegime(regime);
            var (wT, wF, wM, wS) = _regimeWeighting.CurrentWeights();

            // v12.7.0 §1 — MultiFactorConvictionSynthesizer(FastFactorSieve SIMD 위임)로 4대 축 합성.
            var composite = new float[n];
            MultiFactorConvictionSynthesizer.ComputeConviction(technical, fundamental, microstructure, supplyDemand, wT, wF, wM, wS, composite);

            int k = Math.Min(topN, n);
            var idx = new int[k]; var top = new float[k];
            int filled = FastFactorSieve.SelectTopN(composite, idx, top);

            var candidates = new List<MS2E.UI.Portable.Ports.Intel.IAiCandidateInput>(filled);
            for (int i = 0; i < filled; i++)
            {
                int hi = idx[i];
                var h = holdings[hi];
                decimal baseline = h.AvgPrice > 0 ? h.AvgPrice : (h.CurrentPrice > 0 ? h.CurrentPrice : 1m);
                decimal volPct = Math.Max(1m, Math.Abs((h.CurrentPrice - h.AvgPrice) / baseline) * 100m);
                candidates.Add(new CandidateInput(h.Symbol, h.CurrentPrice, (decimal)top[i], volPct));
            }

            var recs = await _ai.RecommendAsync(candidates, ct).ConfigureAwait(false);

            var cards = new List<MS2E.UI.Portable.Ports.Intel.IAiRecommendationCardView>(recs.Count);
            for (int i = 0; i < recs.Count; i++)
            {
                var r = recs[i];
                int hi = idx[i];
                var attr = MultiFactorConvictionSynthesizer.Attribute(
                    technical[hi], fundamental[hi], microstructure[hi], supplyDemand[hi], wT, wF, wM, wS);
                decimal? imb = _imbalanceLookup(r.Symbol);
                bool divergent = _divergenceGuard.IsDivergent(r.Grade, imb);
                cards.Add(new CardView(r.Symbol, r.Grade, r.TargetPrice, r.StopLossPrice, r.Confidence, r.Rationale,
                    divergent, imb, attr.TechnicalPct, attr.FundamentalPct, attr.MicrostructurePct, attr.SupplyDemandPct));
            }
            return cards;
        }

        public bool TryStageOrderPrefill(MS2E.UI.Portable.Ports.Intel.IAiRecommendationCardView card)
        {
            if (card is null) throw new ArgumentNullException(nameof(card));
            return _prefill.TryStage(card.Symbol, card.TargetPrice, card.StopLossPrice, card.Divergent);
        }
    }
}
