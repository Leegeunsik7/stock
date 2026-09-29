// ============================================================================
// AiRecommendationAdapter — v12.4.0 IAiRecommendationPort 구현(AI Co-Pilot Technical & Fundamental Analysis Engine)
//   v12.7.0 — MultiLlmConsensusVoter(모델 간 불일치 → Hold 하향) + RollingWinRateCalibrationEngine(적중률 기반
//   확신도 감쇠) 선택적 결속 추가(둘 다 옵션 — 미주입 시 v12.4.0 동작 그대로 보존).
//
//   FastFactorSieve 합성 점수(호출측 산출) → 결정론적 등급/목표가/손절가 매핑(★수치는 항상 C# 순수 계산 —
//   AiService 등 LLM 은 서술문(Rationale)만 생성해 환각 위험을 수치 경로에서 원천 배제). AiModelFallbackCircuitBreaker
//   가 Suspend 상태면 전 종목 시그널을 중립·억제로 강등한다.
//
//   ★Advisory-Only 구조적 격리: 이 클래스는 OrderDispatchRing·GatedOrderDispatcher·CanaryHealthSentinel·
//   HardwareKillSwitchBridge·HardwareMasterKeyHandshake 등 발주/무장 계열 타입을 단 하나도 참조하지 않는다
//   (필드·생성자 인자 전부 시세/팩터/서술 데이터 경로만) — AiCoPilotTests.cs 의 리플렉션 검증 대상.
// ============================================================================
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace MS2E.Infrastructure
{
    public sealed class AiRecommendationAdapter : MS2E.UI.Portable.Ports.Intel.IAiRecommendationPort
    {
        private sealed record RecommendationView(
            string Symbol,
            MS2E.UI.Portable.Ports.Intel.AiRecommendationGrade Grade,
            decimal TargetPrice,
            decimal StopLossPrice,
            decimal Confidence,
            string Rationale) : MS2E.UI.Portable.Ports.Intel.IAiRecommendationView
        {
            public bool IsAdvisoryOnly => true;   // ★항상 true(불변 계약) — 레코드 필드가 아닌 고정 상수로 봉인
        }

        private readonly MS2E.Core.AiModelFallbackCircuitBreaker _breaker;
        private readonly Func<string, CancellationToken, Task<string>>? _narrativeProvider;   // 예: AiService.AnalyzeAsync 델리게이트
        private readonly Func<string, MS2E.Core.LlmVote[]>? _multiLlmVoteProvider;             // v12.7.0 §2
        private readonly MS2E.Core.RollingWinRateCalibrationEngine? _winRateEngine;             // v12.7.0 Think Beyond A

        public AiRecommendationAdapter(MS2E.Core.AiModelFallbackCircuitBreaker breaker,
            Func<string, CancellationToken, Task<string>>? narrativeProvider = null,
            Func<string, MS2E.Core.LlmVote[]>? multiLlmVoteProvider = null,
            MS2E.Core.RollingWinRateCalibrationEngine? winRateEngine = null)
        {
            _breaker = breaker ?? throw new ArgumentNullException(nameof(breaker));
            _narrativeProvider = narrativeProvider;
            _multiLlmVoteProvider = multiLlmVoteProvider;
            _winRateEngine = winRateEngine;
        }

        public async Task<IReadOnlyList<MS2E.UI.Portable.Ports.Intel.IAiRecommendationView>> RecommendAsync(
            IReadOnlyList<MS2E.UI.Portable.Ports.Intel.IAiCandidateInput> candidates, CancellationToken ct)
        {
            var results = new List<MS2E.UI.Portable.Ports.Intel.IAiRecommendationView>(candidates.Count);
            foreach (var c in candidates)
            {
                ct.ThrowIfCancellationRequested();

                if (!_breaker.CanEmitSignal)
                {
                    results.Add(new RecommendationView(c.Symbol, MS2E.UI.Portable.Ports.Intel.AiRecommendationGrade.Hold,
                        c.CurrentPrice, c.CurrentPrice, 0m,
                        "AI 서킷 브레이커 억제(모델 신뢰도 저하) — 시그널 중립 강등."));
                    continue;
                }

                var grade = GradeFrom(c.CompositeFactorScore);

                // v12.7.0 §2 — 멀티 LLM 합의 게이트: 모델 간 방향 불일치(만장일치 아님) → 원자적으로 Hold 하향.
                bool consensusDowngraded = false;
                if (_multiLlmVoteProvider is not null)
                {
                    var votes = _multiLlmVoteProvider(c.Symbol) ?? Array.Empty<MS2E.Core.LlmVote>();
                    if (votes.Length > 0 && !MS2E.Core.MultiLlmConsensusVoter.IsUnanimous(votes)
                        && MS2E.Core.MultiLlmConsensusVoter.Consensus(votes) == MS2E.Core.LlmVote.Neutral)
                    {
                        grade = MS2E.UI.Portable.Ports.Intel.AiRecommendationGrade.Hold;
                        consensusDowngraded = true;
                    }
                }

                decimal volFrac = c.VolatilityPct / 100m;
                decimal target = Math.Round(c.CurrentPrice * (1m + volFrac * TargetMultiplier(grade)), 0);
                decimal stop = Math.Round(c.CurrentPrice * (1m - volFrac), 0);

                // v12.7.0 Think Beyond A — 과거 적중률 기반 확신도 감쇠(엔진 미주입 시 무감쇠 그대로).
                decimal confidence = _breaker.ConfidencePpm / 1_000_000m;
                if (_winRateEngine is not null) confidence *= _winRateEngine.ConfidenceAttenuationPpm / 1_000_000m;

                string rationale = consensusDowngraded
                    ? $"{c.Symbol}: 멀티 LLM 모델 간 방향 불일치 감지 — Hold 로 하향(Advisory Safety Gate)."
                    : _narrativeProvider is not null
                        ? await _narrativeProvider(c.Symbol, ct).ConfigureAwait(false)
                        : $"{c.Symbol}: 정량 팩터 합성 점수 {c.CompositeFactorScore:F2} 기반 결정론적 등급(뉴스/공시 서술 미결속).";

                results.Add(new RecommendationView(c.Symbol, grade, target, stop, confidence, rationale));
            }
            return results;
        }

        private static MS2E.UI.Portable.Ports.Intel.AiRecommendationGrade GradeFrom(decimal compositeScore) => compositeScore switch
        {
            >= 0.6m => MS2E.UI.Portable.Ports.Intel.AiRecommendationGrade.StrongBuy,
            >= 0.2m => MS2E.UI.Portable.Ports.Intel.AiRecommendationGrade.Buy,
            > -0.2m => MS2E.UI.Portable.Ports.Intel.AiRecommendationGrade.Hold,
            > -0.6m => MS2E.UI.Portable.Ports.Intel.AiRecommendationGrade.Sell,
            _ => MS2E.UI.Portable.Ports.Intel.AiRecommendationGrade.StrongSell,
        };

        private static decimal TargetMultiplier(MS2E.UI.Portable.Ports.Intel.AiRecommendationGrade grade) => grade switch
        {
            MS2E.UI.Portable.Ports.Intel.AiRecommendationGrade.StrongBuy => 2.5m,
            MS2E.UI.Portable.Ports.Intel.AiRecommendationGrade.Buy => 1.5m,
            MS2E.UI.Portable.Ports.Intel.AiRecommendationGrade.Hold => 0.5m,
            MS2E.UI.Portable.Ports.Intel.AiRecommendationGrade.Sell => -0.5m,
            _ => -1.5m,
        };
    }
}
