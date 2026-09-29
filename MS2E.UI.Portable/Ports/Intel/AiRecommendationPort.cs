// ============================================================================
// AiRecommendationPort — v12.4.0 AI Quantitative Selection & Microstructure Analysis Co-Pilot (순수 UI 경계)
//
//   IntelHub Co-Pilot 의 소비자 계약 — AI 추천 등급/목표가/손절가/리포트를 원시 값(decimal/string)만으로 노출.
//   ★엔진 타입 참조 0(RCL 순수성) — FastFactorSieve·AiModelFallbackCircuitBreaker·AiService 는 어댑터
//   (Infrastructure 계층)만 알고 있다. ★IsAdvisoryOnly 는 항상 true — 이 시그널이 발주 파이프라인을 무장·
//   우회할 수 없음을 구조적으로 표식하는 불변 계약(liveArmed:false 와 별개 계층에서 이중 보증).
// ============================================================================
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace MS2E.UI.Portable.Ports.Intel
{
    public enum AiRecommendationGrade { StrongSell = -2, Sell = -1, Hold = 0, Buy = 1, StrongBuy = 2 }

    /// <summary>AI 추천 산출용 후보 입력 — SIMD 사전필터(FastFactorSieve) 통과 종목. 가격/팩터/변동성은
    /// 호출측이 결정적으로(C# 순수 계산) 산출해 전달한다(환각 방지 — LLM 은 서술문만 생성).</summary>
    public interface IAiCandidateInput
    {
        string Symbol { get; }
        decimal CurrentPrice { get; }
        /// <summary>합성 팩터 점수(정규화, 대략 -1..+1) — FastFactorSieve.ComputeCompositeScores 유래.</summary>
        decimal CompositeFactorScore { get; }
        /// <summary>목표가/손절가 산출용 변동성(%) — 예: StreamingParametricVarCalculator.StdDevBps 유래.</summary>
        decimal VolatilityPct { get; }
    }

    /// <summary>AI 추천 결과 뷰 — 등급/목표가/손절가는 결정론적 산출, Rationale 만 LLM 서술(제공 데이터 근거).</summary>
    public interface IAiRecommendationView
    {
        string Symbol { get; }
        AiRecommendationGrade Grade { get; }
        decimal TargetPrice { get; }
        decimal StopLossPrice { get; }
        /// <summary>0..1 — AI 모델 신뢰도(AiModelFallbackCircuitBreaker 유래).</summary>
        decimal Confidence { get; }
        string Rationale { get; }
        /// <summary>★항상 true — 발주 파이프라인 무장/우회 불가 구조적 표식(불변 계약).</summary>
        bool IsAdvisoryOnly { get; }
    }

    public interface IAiRecommendationPort
    {
        /// <summary>SIMD 사전필터로 선별된 후보 종목들에 대해 AI 추천을 생성(Advisory-Only — 발주 결속 없음).</summary>
        Task<IReadOnlyList<IAiRecommendationView>> RecommendAsync(IReadOnlyList<IAiCandidateInput> candidates, CancellationToken ct);
    }
}
