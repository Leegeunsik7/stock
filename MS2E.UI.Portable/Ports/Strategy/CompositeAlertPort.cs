// ============================================================================
// CompositeAlertPort — Phase 94-F: 복합 알림 Port & 읽기 전용 도메인 계약
//   OptimizerPage 가 CompositeAlertEngine/CompositeRule/CompositeCondition/CondMetric 등 도메인 구상 타입을
//   직접 만지지 않도록 하는 경계. ★RCL 내부로 구상 도메인 타입이 유입되지 않는다 — 주입은 InjectTrendAlert 로
//   전량 어댑터가 조립하고, 읽기는 ICompositeRuleContract/ICondMetricView 로만 투사한다.
//   ★liveArmed:false: 알림 규칙 조립/저장 — 발주·소켓 무접촉.
// ============================================================================
using System.Collections.Generic;

namespace MS2E.UI.Portable.Ports.Strategy
{
    /// <summary>복합 조건 1개의 읽기 전용 뷰(CondMetric/CompositeCondition 추상).</summary>
    public interface ICondMetricView
    {
        string  MetricLabel { get; }   // 예: "MA20 괴리"
        string  Summary     { get; }   // 전체 서술(예: "MA20 괴리 ≥ 0%")
        int     Param       { get; }
        decimal Threshold   { get; }
    }

    /// <summary>복합 알림 규칙의 읽기 전용 계약(CompositeRule 추상).</summary>
    public interface ICompositeRuleContract
    {
        string Name      { get; }
        string Target    { get; }
        string AssetKind { get; }
        bool   Enabled   { get; }
        IReadOnlyList<ICondMetricView> Conditions { get; }
    }

    /// <summary>복합 알림 Port — 도메인 구상 타입을 은닉한 채 주입/조회.</summary>
    public interface ICompositeAlertPort
    {
        /// <summary>우승 장기선(MA long) 복귀 추세 필터를 복합 알림 규칙으로 주입(CompositeRule 조립은 어댑터 내부).</summary>
        void InjectTrendAlert(string symbol, string name, int longP);

        /// <summary>현재 복합 알림 규칙 목록(읽기 전용 계약 — 도메인 구상 미유입).</summary>
        IReadOnlyList<ICompositeRuleContract> ListRules();
    }
}
