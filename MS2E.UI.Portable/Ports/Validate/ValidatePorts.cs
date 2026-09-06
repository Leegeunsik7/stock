// ============================================================================
// ValidatePorts — v12.0.0 Phase 94-P: ValidateHub(Calibrator/Forecast/Twin/Replay) Ports
//   순수 UI 를 엔진 구상(원시 SQL·DataRow·AlertCalibrator·CounterfactualTwin·ReplayService·ForecastScore/Log·
//   ForecastVaultService·결과 DTO)에서 물리 격리. ★원시 SQL/데이터 마샬링은 IValidateRepositoryPort(불변 스냅샷)
//   + Host 어댑터 내부로 완전 격리(페이지 0 Concrete Ref). compute 포트는 어댑터가 데이터+엔진 오케스트레이션.
//   ★liveArmed:false: 사후 검증/보정도/복기 — 정보 제공 전용(실 발주 무접촉).
// ============================================================================
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MS2E.UI.Portable.Ports.Portfolio;   // PfMarket

namespace MS2E.UI.Portable.Ports.Validate
{
    // ── ⓪ 불변 스냅샷 리포지토리(원시 SQL 격리 — Think-Beyond 매퍼로 DataRow 무물성화) ──────
    public interface IAlertRecordView { DateTime Time { get; } int Source { get; } string Symbol { get; } }
    public interface ITxRecordView { int Type { get; } decimal Quantity { get; } decimal Price { get; } DateTime ExecutedAt { get; } }
    public interface ISignalRecordView { int Signal { get; } DateTime GeneratedAt { get; } }

    /// <summary>검증 데이터 불변 스냅샷 접근(원시 SQL·DataRow 를 어댑터가 ZeroAllocRowStreamMapper 로 흡수).</summary>
    public interface IValidateRepositoryPort
    {
        IReadOnlyList<IAlertRecordView> GetAlerts(DateTime from, DateTime to);
        IReadOnlyList<ITxRecordView> GetTransactions(string symbol, DateTime from, DateTime to);
        IReadOnlyList<ISignalRecordView> GetSignals(string symbol, DateTime from, DateTime to);
    }

    // ── ① 캘리브레이터 ────────────────────────────────────────────────────────
    public interface IAlertGradeView
    {
        string Category { get; } int Count { get; } int Evaluable { get; }
        double HitRatePct { get; } double AvgAbsMovePct { get; } double AvgSignedMovePct { get; } string Recommendation { get; }
    }
    public interface ICalibrationReportView
    {
        bool IsValid { get; } int Total { get; } int Evaluable { get; } double OverallHitRatePct { get; } string Note { get; }
        IReadOnlyList<IAlertGradeView> Grades { get; }
    }
    public interface ICalibratorPort
    {
        Task<ICalibrationReportView> ScoreAsync(int days, int horizon, int thr);
    }

    // ── ② 반사실 트윈 ─────────────────────────────────────────────────────────
    public interface ITwinPointView { decimal Hold { get; } decimal Signal { get; } }
    public interface ITwinReportView
    {
        bool IsValid { get; } string Note { get; } string Verdict { get; }
        bool HasActual { get; } decimal ActualReturnPct { get; } bool SignalEntered { get; } decimal SignalReturnPct { get; }
        decimal BuyHoldReturnPct { get; } decimal GapVsSignal { get; } decimal GapVsHold { get; }
        int ActualTrades { get; } int SignalTrades { get; }
        IReadOnlyList<ITwinPointView> Curve { get; }
    }
    public interface ITwinPort
    {
        Task<ITwinReportView> CompareAsync(string symbol, PfMarket market, int months);
    }

    // ── ③ 결정 리플레이 ───────────────────────────────────────────────────────
    public interface IEquityPointView { string Date { get; } decimal Total { get; } }
    public interface IReplaySignalView
    {
        DateTime At { get; } string Name { get; } string Symbol { get; } string Strategy { get; }
        decimal EntryEst { get; } decimal Current { get; } decimal HypoReturnPct { get; } bool Followed { get; }
    }
    public interface IReplayReportView
    {
        int BuyCount { get; } decimal FollowRate { get; } int FollowedCount { get; } decimal IgnoredAvgPct { get; }
        IReadOnlyList<IEquityPointView> Equity { get; }
        IReadOnlyList<IReplaySignalView> Signals { get; }
    }
    public interface IReplayPort
    {
        /// <summary>리플레이 리포트 빌드 + 시세 enrich(현재가·가상수익) — 어댑터 내재화.</summary>
        Task<IReplayReportView> BuildAsync();
    }

    // ── ④ 예측 스코어보드(백테스트 + 라이브 로그 + Vault) ──────────────────────
    public interface IForecastKindScoreView
    {
        string Kind { get; } int HorizonDays { get; } int N { get; }
        double CoveragePct { get; } double NominalPct { get; } double BiasPct { get; } double MaePct { get; }
        double DirHitPct { get; } double LowBreachPct { get; } string Verdict { get; }
    }
    public interface IForecastScoreView
    {
        bool IsValid { get; } string Note { get; } int Resolved { get; }
        IReadOnlyList<IForecastKindScoreView> Scores { get; }
    }
    public interface IEngineStatView
    {
        int EngineType { get; } int TotalCount { get; } int EvaluatedCount { get; }
        double HitRatePct { get; } double MAE { get; } double RMSE { get; } double MeanBias { get; } double CoveragePct { get; }
    }
    public interface IVaultSummaryView
    {
        int TotalForecasts { get; } int PendingCount { get; } double OverallHitRatePct { get; } double OverallMAE { get; }
        IReadOnlyList<IEngineStatView> ByEngine { get; }
    }
    public interface IForecastRecordView
    {
        int EngineType { get; } string Symbol { get; } DateTime CreatedAt { get; } DateTime TargetDate { get; }
        double PredictedValue { get; } double? ActualValue { get; } double? AbsoluteError { get; }
    }

    /// <summary>예측 실행 Port — 보정도 백테스트·라이브 예측 발행/정산·Vault 평가(ForecastScore/Log/Vault 격리).</summary>
    public interface IForecastExecutionPort
    {
        Task<IForecastScoreView?> RunBacktestAsync(string symbol, PfMarket market, int horizon, int band, int lookback);
        int PendingCount();
        IForecastScoreView LiveScore();
        Task<string> IssueLiveAsync(string symbol, PfMarket market, int horizon, int band, int lookback);
        Task<int> ResolveLiveAsync();
        Task<IVaultSummaryView?> GetVaultSummaryAsync(CancellationToken ct);
        Task<IReadOnlyList<IForecastRecordView>> GetVaultRecentAsync(int n, CancellationToken ct);
        Task<int> EvaluateVaultAsync();
    }
}
