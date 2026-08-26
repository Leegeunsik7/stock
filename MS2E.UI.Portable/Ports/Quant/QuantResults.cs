// ============================================================================
// QuantResults — Phase 94-C: Risk/Quant 엔진 결과 읽기 전용 계약 (순수 DTO·0 Engine Ref)
//
//   콘크리트 엔진 결과(RiskReport·McResult·ScenarioResult + 하위 RiskContribution·AssetSensitivity)를 순수 UI 가
//   소비하는 읽기 전용 인터페이스. UI(RCL)는 이 계약만 알고 엔진 콘크리트 타입은 0개 참조한다. 호스트의 콘크리트
//   결과 타입이 이 인터페이스를 구현(대부분 기존 프로퍼티가 자동 충족)하고, 어댑터가 계약 뒤로 반환한다.
//   ★모든 멤버 getter-only → UI 는 읽기만(캐스케이드 상태 오염 1차 차단, 배열 변조는 PortfolioSnapshotIntegritySentinel 감시).
// ============================================================================
using System.Collections.Generic;

namespace MS2E.UI.Portable.Ports.Quant
{
    /// <summary>종목별 위험 기여(Euler 분해).</summary>
    public interface IRiskContribution
    {
        string Name           { get; }
        double WeightPct      { get; }
        double RiskContribPct { get; }
        double Mctr           { get; }
        double Ratio          { get; }   // 위험비중/비중(>1이면 비중 대비 위험 큼)
    }

    /// <summary>리스크 기여도 분해 리포트.</summary>
    public interface IRiskReport
    {
        bool     IsValid             { get; }
        string   Note                { get; }
        double   PortfolioVolPct     { get; }
        IReadOnlyList<IRiskContribution> Contributions { get; }
        double   TopConcentrationPct { get; }
        string   TopName             { get; }
        string[] Names               { get; }
        double[] CurrentWeights      { get; }
        double[] RiskParityWeights   { get; }
        string   Insight             { get; }
    }

    /// <summary>몬테카를로 시뮬 결과.</summary>
    public interface IMcResult
    {
        int    Sims         { get; }
        int    Days         { get; }
        int    Assets       { get; }
        double ExpReturnPct { get; }
        double P5           { get; }
        double P50          { get; }
        double P95          { get; }
        double Var95Pct     { get; }
        double ProbLossPct  { get; }
        int[]  Histogram    { get; }
        double HistMin      { get; }
        double HistMax      { get; }
    }

    /// <summary>종목별 시장 팩터 민감도.</summary>
    public interface IAssetSensitivity
    {
        string Name             { get; }
        double Weight           { get; }
        double Beta             { get; }
        double R2               { get; }
        double PredictedMovePct { get; }
    }

    /// <summary>시나리오 스트레스 결과(시장 팩터 충격 기준·UI 에서 선형 스케일).</summary>
    public interface IScenarioResult
    {
        bool   IsValid          { get; }
        string Note             { get; }
        double FactorShockPct   { get; }
        double PortfolioMovePct { get; }
        IReadOnlyList<IAssetSensitivity> Assets { get; }
    }
}
