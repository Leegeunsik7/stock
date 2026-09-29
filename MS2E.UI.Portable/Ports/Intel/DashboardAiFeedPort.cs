// ============================================================================
// DashboardAiFeedPort — v12.6.0 Main Dashboard AI Recommendation Feed Widget (순수 UI 경계)
//   v12.7.0 — MultiFactorConvictionSynthesizer(4대 축 XAI 기여도) 필드 추가.
//
//   메인 대시보드 AI 위젯이 소비하는 단일 진입점. 내부적으로 IPortfolioBoardPort(보유 종목) → FastFactorSieve
//   (SIMD Top-N) → IAiRecommendationPort(Advisory-Only 추천)를 어댑터가 합성해, 위젯은 이 Port 하나만 주입받는다
//   (엔진 타입 참조 0 — RCL 순수성 유지). ★IsAdvisoryOnly 는 IAiRecommendationCardView 에도 항상 true.
// ============================================================================
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace MS2E.UI.Portable.Ports.Intel
{
    /// <summary>대시보드 카드 뷰 — 기본 추천 정보 + 호가 괴리 경고(Think Beyond Idea B) + XAI 팩터 기여도(v12.7.0).</summary>
    public interface IAiRecommendationCardView
    {
        string Symbol { get; }
        AiRecommendationGrade Grade { get; }
        decimal TargetPrice { get; }
        decimal StopLossPrice { get; }
        decimal Confidence { get; }
        string Rationale { get; }
        bool IsAdvisoryOnly { get; }
        /// <summary>AI 등급과 실시간 호가 잔량 불균형이 상충(괴리)하는지 — true 면 UI 경고 배너 + 프리필 자동 차단.</summary>
        bool Divergent { get; }
        /// <summary>참조된 호가 불균형비(-1..+1). 조회 불가 시 null(괴리 판정 생략 — fail-open, Advisory 표시일 뿐).</summary>
        decimal? OrderBookImbalance { get; }

        // ── v12.7.0 XAI(Explainable AI) 4대 축 기여도(합 100%, MultiFactorConvictionSynthesizer.Attribute 유래) ──
        decimal TechnicalPct { get; }
        decimal FundamentalPct { get; }
        decimal MicrostructurePct { get; }
        decimal SupplyDemandPct { get; }
    }

    public interface IDashboardAiFeedPort
    {
        /// <summary>보유 종목 기반 SIMD Top-N AI 추천 카드 조회.</summary>
        Task<IReadOnlyList<IAiRecommendationCardView>> GetTopRecommendationsAsync(int topN, CancellationToken ct);

        /// <summary>카드에서 "주문 창으로 연동" 선택 — 괴리(Divergent) 카드는 구조적으로 거부(false).</summary>
        bool TryStageOrderPrefill(IAiRecommendationCardView card);
    }
}
