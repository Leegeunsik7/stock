// ============================================================================
// StrategyResults — Phase 94-D: 전략/백테스트 결과 읽기 전용 계약 (순수 DTO·0 Engine Ref)
//   콘크리트 엔진 결과(OptResult·OptCandidate·OptRow·OptSnapshot)를 순수 UI 가 소비하는 읽기 전용 인터페이스.
//   호스트 콘크리트 결과 타입이 이 인터페이스를 구현(대부분 기존 프로퍼티가 자동 충족)하고, 어댑터가 계약 뒤로 반환한다.
//   ★모든 멤버 getter-only → UI 는 읽기만(캐스케이드 상태 오염 차단).
// ============================================================================
using System.Collections.Generic;

namespace MS2E.UI.Portable.Ports.Strategy
{
    /// <summary>한 (short,long) MA 조합의 백테스트 점수(읽기 전용).</summary>
    public interface IBacktestCandidate
    {
        int     ShortP      { get; }
        int     LongP       { get; }
        string  Label       { get; }   // "MA{short}/{long}"
        decimal TotalReturn { get; }
        decimal MaxDrawdown { get; }
        decimal WinRate     { get; }
        int     TradeCount  { get; }
        decimal Sharpe      { get; }
    }

    /// <summary>한 종목 격자탐색 최적화 리포트(우승자 + 전체 후보).</summary>
    public interface IOptimizationReport
    {
        bool    IsValid     { get; }
        string  Note        { get; }
        string  Symbol      { get; }
        string  Name        { get; }
        IReadOnlyList<IBacktestCandidate> Candidates { get; }
        IBacktestCandidate? Best        { get; }
        int     CandlesUsed { get; }
    }

    /// <summary>포트폴리오 스윕의 종목별 경량 결과 행(읽기 전용).</summary>
    public interface IBacktestResultRow
    {
        string  Symbol      { get; }
        string  Name        { get; }
        bool    IsValid     { get; }
        string  Label       { get; }
        string  Note        { get; }
        decimal TotalReturn { get; }
        decimal MaxDrawdown { get; }
        decimal WinRate     { get; }
        int     TradeCount  { get; }
        decimal Sharpe      { get; }
    }

    /// <summary>밤사이 포트폴리오 스윕 스냅샷(읽기 전용).</summary>
    public interface IBacktestResultSnapshot
    {
        string GeneratedAt { get; }
        IReadOnlyList<IBacktestResultRow> Rows { get; }
    }
}
