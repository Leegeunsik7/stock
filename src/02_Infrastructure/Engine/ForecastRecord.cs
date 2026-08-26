// ============================================================================
// ForecastRecord — S11 #1 Forecast Vault 데이터 계약
//  · ForecastEngineType: FutureCone / Multiverse / Committee / Alpha
//  · ForecastRecord: 캡처 시 불변, 평가 후 ActualValue·AbsoluteError 채워짐
//  · EngineStatistics / VaultSummary: 통계 집계 결과 (순수 read-only)
//  · Committee는 텍스트 전용 — 수치 통계에서 제외
// ============================================================================
using System;
using System.Collections.Generic;

namespace StockManager.Common
{
    public enum ForecastEngineType
    {
        FutureCone  = 0,
        Multiverse  = 1,
        Committee   = 2,   // text-only; excluded from numerical scoring
        Alpha       = 3,
    }

    public sealed record ForecastRecord(
        string   ForecastId,
        DateTime CreatedAt,
        ForecastEngineType EngineType,
        string   Symbol,            // 종목코드 또는 "PORTFOLIO"
        DateTime TargetDate,        // = CreatedAt + HorizonDays (달력일 기준)
        int      HorizonDays,
        double   PredictedValue,    // P50 / 점추정 (수익률 %)
        double   ConfidenceLow,     // P5 하한 (%)
        double   ConfidenceHigh,    // P95 상한 (%)
        string   ScenarioTag,       // "" = 비-멀티버스; "BullRun" 등 = Multiverse
        string   CaptureMetaJson,   // {"p":123.0,"m":2} 단일심볼 | {"h":[...]} 포트폴리오
        double?  ActualValue,       // EvaluateExpiredForecastsAsync 후 채워짐
        DateTime? EvaluatedAt,
        double?  AbsoluteError,     // |PredictedValue - ActualValue|
        string   Note
    );

    // ── 엔진별 통계 ────────────────────────────────────────────────────────────
    public sealed record EngineStatistics(
        ForecastEngineType EngineType,
        int    TotalCount,
        int    EvaluatedCount,
        double HitRatePct,   // 방향 일치 % (부호 일치)
        double MAE,          // mean |predicted − actual|
        double RMSE,         // sqrt(mean((predicted − actual)²))
        double MeanBias,     // mean(predicted − actual) — 양수 = 낙관 편향
        double CoveragePct   // % actual ∈ [ConfidenceLow, ConfidenceHigh]
    );

    // ── 전체 요약 ──────────────────────────────────────────────────────────────
    public sealed record VaultSummary(
        int    TotalForecasts,
        int    EvaluatedCount,
        int    PendingCount,
        double OverallHitRatePct,
        double OverallMAE,
        IReadOnlyList<EngineStatistics> ByEngine
    );
}
