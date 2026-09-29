// ============================================================================
// RiskThresholdMatrixEvaluator — v12.11.0 [Step 5/7] Think Beyond Gemini Idea A:
//   Zero-Alloc Risk Threshold Matrix Evaluator
//
//   일일 최대 손실·Peak-to-Trough 낙폭·Parametric VaR·슬리피지 4축 리스크 상태를 단일 값타입(RiskMatrixVerdict)
//   으로 O(1) 산출한다. DynamicRiskInterlock(v12.2.0)/ExecutionSlippageProfiler(v12.3.0) 가 이미 각 축을 개별
//   판정하지만, ★위젯/관제 UI 가 "4축 중 몇 개가 위험 영역인가"를 한 번에 조회하려면 매번 4개 프로퍼티를 따로
//   읽어야 했다(산발 조회) — 이 평가기는 그 4개를 단일 비트마스크 값타입으로 압축해 O(1) 조회 O(1) 판정을 제공한다.
//
//   불변: 결정론(순수 함수) · δ=0(Evaluate/EvaluateBatch 힙 0 — 값타입 반환) · fail-closed(4축 중 1개라도 임계
//        초과 시 IsAnyBreached=true) · liveArmed:false(판정 수치만 산출 — 발주 경로 무접촉).
// ============================================================================
using System;

namespace MS2E.Infrastructure
{
    [Flags]
    public enum RiskAxisFlags : byte
    {
        None = 0, DailyLoss = 1, Drawdown = 2, VaR = 4, Slippage = 8,
    }

    public readonly record struct RiskMatrixVerdict(RiskAxisFlags Breached)
    {
        public bool IsAnyBreached => Breached != RiskAxisFlags.None;
        public bool DailyLossBreached => (Breached & RiskAxisFlags.DailyLoss) != 0;
        public bool DrawdownBreached => (Breached & RiskAxisFlags.Drawdown) != 0;
        public bool VaRBreached => (Breached & RiskAxisFlags.VaR) != 0;
        public bool SlippageBreached => (Breached & RiskAxisFlags.Slippage) != 0;
    }

    public static class RiskThresholdMatrixEvaluator
    {
        /// <summary>4축(bps 단위)을 임계와 비교해 단일 비트마스크 값타입으로 압축 판정(δ=0). 임계≤0 축은 비활성
        /// (fail-closed 관례와 동일 — 미설정 축은 판정 생략).</summary>
        public static RiskMatrixVerdict Evaluate(
            long dailyLossBps, long dailyLossThresholdBps,
            long drawdownBps, long drawdownThresholdBps,
            long varBps, long varThresholdBps,
            long slippageBps, long slippageThresholdBps)
        {
            RiskAxisFlags f = RiskAxisFlags.None;
            if (dailyLossThresholdBps > 0 && dailyLossBps > dailyLossThresholdBps) f |= RiskAxisFlags.DailyLoss;
            if (drawdownThresholdBps > 0 && drawdownBps > drawdownThresholdBps) f |= RiskAxisFlags.Drawdown;
            if (varThresholdBps > 0 && varBps > varThresholdBps) f |= RiskAxisFlags.VaR;
            if (slippageThresholdBps > 0 && slippageBps > slippageThresholdBps) f |= RiskAxisFlags.Slippage;
            return new RiskMatrixVerdict(f);
        }

        /// <summary>여러 포트폴리오/심볼을 배치 평가(δ=0 — 호출자 제공 결과 버퍼). 4개 축 입력 span 은 동일 길이,
        /// 임계는 공통(스칼라) 적용.</summary>
        public static void EvaluateBatch(
            ReadOnlySpan<long> dailyLossBps, long dailyLossThresholdBps,
            ReadOnlySpan<long> drawdownBps, long drawdownThresholdBps,
            ReadOnlySpan<long> varBps, long varThresholdBps,
            ReadOnlySpan<long> slippageBps, long slippageThresholdBps,
            Span<RiskMatrixVerdict> resultsOut)
        {
            int n = dailyLossBps.Length;
            if (drawdownBps.Length != n || varBps.Length != n || slippageBps.Length != n || resultsOut.Length != n)
                throw new ArgumentException("모든 span 길이가 일치해야 합니다.");
            for (int i = 0; i < n; i++)
                resultsOut[i] = Evaluate(
                    dailyLossBps[i], dailyLossThresholdBps, drawdownBps[i], drawdownThresholdBps,
                    varBps[i], varThresholdBps, slippageBps[i], slippageThresholdBps);
        }
    }
}
