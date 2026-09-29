// ============================================================================
// AiMicrostructureDivergenceGuard — v12.6.0 Think Beyond Gemini Idea B: AI-Microstructure Divergence Warning
//
//   AI 추천 등급(FastFactorSieve→AiRecommendationAdapter)과 실시간 호가 잔량 불균형(OrderBookDepthMatrixCoalescer
//   →ImbalanceRatio, v12.3.0)이 상충하는지 판정하는 순수 결정 함수. Strong/Buy 인데 매도 잔량이 폭주(불균형비가
//   음의 임계 이하)하거나, Strong/Sell 인데 매수 잔량이 폭주(대칭)하면 괴리(Divergent)로 판정한다.
//
//   불변: 결정론(순수 함수) · δ=0(IsDivergent 힙 0) · fail-open(불균형비 미제공(null) → 판정 생략, false —
//        괴리 검사는 Advisory 보강 신호일 뿐 그 부재가 추천 자체를 차단하지 않는다. 반대로 판정된 Divergent=true
//        는 OrderPrefillAdapter.TryStage 에서 fail-closed 로 소비되어 프리필을 실제로 차단한다).
// ============================================================================
namespace MS2E.Infrastructure
{
    public sealed class AiMicrostructureDivergenceGuard
    {
        private readonly decimal _imbalanceThreshold;   // 예: 0.8 = ±80%

        public AiMicrostructureDivergenceGuard(decimal imbalanceThreshold = 0.8m)
            => _imbalanceThreshold = imbalanceThreshold > 0 ? imbalanceThreshold : 0.8m;

        /// <summary>AI 등급과 호가 불균형비(-1..+1, 양수=매수 우위)가 상충하는지 판정(δ=0·순수).
        /// imbalanceRatio 가 null 이면(호가 데이터 없음) fail-open — 판정 생략(false).</summary>
        public bool IsDivergent(MS2E.UI.Portable.Ports.Intel.AiRecommendationGrade grade, decimal? imbalanceRatio)
        {
            if (imbalanceRatio is null) return false;
            decimal imb = imbalanceRatio.Value;

            bool bullish = grade is MS2E.UI.Portable.Ports.Intel.AiRecommendationGrade.StrongBuy
                                  or MS2E.UI.Portable.Ports.Intel.AiRecommendationGrade.Buy;
            bool bearish = grade is MS2E.UI.Portable.Ports.Intel.AiRecommendationGrade.StrongSell
                                  or MS2E.UI.Portable.Ports.Intel.AiRecommendationGrade.Sell;

            if (bullish && imb <= -_imbalanceThreshold) return true;   // 매수 추천인데 매도 잔량 폭주
            if (bearish && imb >= _imbalanceThreshold) return true;    // 매도 추천인데 매수 잔량 폭주
            return false;
        }
    }
}
