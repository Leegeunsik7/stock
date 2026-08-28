// ============================================================================
// SignalResults — Phase 94-F: 시그널 엔진 읽기 전용 계약 (순수 DTO·0 Concrete Ref)
//   원시 SQL/DataTable/DataRow, DashboardService.SignalRow, BacktestSummary, SampleData 를 UI 로 투사하지 않고
//   불변 읽기 전용 인터페이스로만 매핑한다. 가변 참조형(DataTable 등) 유입 차단.
// ============================================================================
using System;
using System.Collections.Generic;

namespace MS2E.UI.Portable.Ports.Strategy
{
    /// <summary>시그널 판정(색상/의미) — 한국 관례: Buy=상승(빨강)·Sell=하락(파랑).</summary>
    public enum SignalVerdict { Neutral = 0, Buy = 1, Sell = 2 }

    /// <summary>DB 시그널 이력 1건(읽기 전용 불변 스냅샷 — DataRow 미유입).</summary>
    public interface ISignalRecordSnapshot
    {
        DateTime At         { get; }
        string   Symbol     { get; }
        string   Name       { get; }
        string   TypeLabel  { get; }   // "🟢 매수" 등
        SignalVerdict Verdict { get; } // 색상 판정
        string   Strategy   { get; }
        string   Reason     { get; }
        decimal  Target     { get; }
        decimal  Stop       { get; }
        decimal  Confidence { get; }
    }

    /// <summary>코인 임박 신호(대시보드 실시간 스캔·읽기 전용).</summary>
    public interface ICoinSignalView
    {
        string Side   { get; }   // "BUY" | "SELL"
        string Name   { get; }
        string Reason { get; }
        int    Pct    { get; }
    }

    /// <summary>백테스트 샘플 종목(자유입력 datalist·읽기 전용).</summary>
    public interface ISampleSymbol
    {
        string Symbol { get; }
        string Name   { get; }
    }

    /// <summary>비용 반영 백테스트 요약(읽기 전용 — EquityCurve 는 읽기 전용 리스트).</summary>
    public interface IBacktestSummaryView
    {
        IReadOnlyList<decimal> EquityCurve { get; }
        decimal TotalReturn { get; }
        decimal MaxDrawdown { get; }
        decimal WinRate     { get; }
        int     TradeCount  { get; }
        decimal Sharpe      { get; }
    }
}
