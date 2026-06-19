// ============================================================================
// ExecutionQualityService — S17 #3: 실행 품질 대시보드 순수 로직 (v3.94.0)
//
//  TimelineEntry   : DB FraudAlert → 타임라인 뷰 모델
//  ExecutionRow    : Transaction + Candle → VWAP 대비 슬리피지 행
//  ExecutionQualityService : 테스트 가능한 순수 정적 함수 모음
//
//  설계 원칙:
//  · 외부 의존성 없음 (순수 C# 결정론 모듈)
//  · UI 레이어와 DB 레이어 중간에서 변환·집계만 수행
//  · 모든 public 메서드는 단위 테스트 가능
// ============================================================================
using System;
using System.Collections.Generic;
using System.Linq;

namespace StockManager.Common
{
    // ── 뷰 모델 ──────────────────────────────────────────────────────────────

    /// <summary>통합 컴플라이언스 타임라인의 항목 1건.</summary>
    public sealed record TimelineEntry(
        DateTime OccurredAt,
        string   Source,      // SENTINEL / TUNER / SYSTEM
        string   Severity,    // HIGH / CRITICAL / MEDIUM / LOW
        string   Symbol,
        string   Message,
        bool     IsReviewed,
        int?     DbId);       // FraudAlerts.Id (검토 마킹용)

    /// <summary>주문 집행 품질 테이블 1행: 체결가 vs 기준VWAP.</summary>
    public sealed record ExecutionRow(
        string   Symbol,
        string   TypeLabel,
        int      Quantity,
        decimal  FilledPrice,
        decimal  BenchmarkPrice,  // 0 = 기준가 없음
        decimal  SlippagePct,     // (filled - bench) / bench × 100
        DateTime ExecutedAt);

    // ── 순수 서비스 ───────────────────────────────────────────────────────────

    public static class ExecutionQualityService
    {
        // ── 타임라인 빌드 ─────────────────────────────────────────────────────

        /// <summary>
        /// DB FraudAlert 목록을 TimelineEntry 목록으로 변환하고 시간 역순으로 반환합니다.
        /// </summary>
        public static IReadOnlyList<TimelineEntry> BuildTimeline(
            IEnumerable<FraudAlert> dbAlerts,
            bool includeReviewed = true)
        {
            if (dbAlerts is null) return Array.Empty<TimelineEntry>();

            return dbAlerts
                .Where(fa => includeReviewed || !fa.IsReviewed)
                .Select(fa => new TimelineEntry(
                    OccurredAt: fa.AttemptedAt,
                    Source:     NormaliseSource(fa.TransactionType),
                    Severity:   string.IsNullOrEmpty(fa.Severity)
                                    ? "HIGH"
                                    : fa.Severity.ToUpperInvariant(),
                    Symbol:     fa.Symbol ?? "",
                    Message:    fa.Reason ?? "",
                    IsReviewed: fa.IsReviewed,
                    DbId:       fa.Id))
                .OrderByDescending(e => e.OccurredAt)
                .ToList();
        }

        private static string NormaliseSource(string? raw)
        {
            if (string.IsNullOrEmpty(raw)) return "SYSTEM";
            var up = raw.ToUpperInvariant();
            return up is "SENTINEL" or "TUNER" or "SYSTEM" ? up : up;
        }

        // ── 슬리피지 산출 ─────────────────────────────────────────────────────

        /// <summary>
        /// 슬리피지 비율(%) = (체결가 − 기준VWAP) / 기준VWAP × 100.
        /// benchmarkPrice ≤ 0 이면 0 반환 (측정 불가).
        /// </summary>
        public static decimal SlippagePct(decimal filledPrice, decimal benchmarkPrice)
        {
            if (benchmarkPrice <= 0m) return 0m;
            return Math.Round((filledPrice - benchmarkPrice) / benchmarkPrice * 100m, 4);
        }

        // ── CSS 클래스 매핑 (UI 계산, 순수 함수) ──────────────────────────────

        public static string SeverityCssClass(string? severity) =>
            severity?.ToUpperInvariant() switch
            {
                "CRITICAL" => "sev-critical",
                "HIGH"     => "sev-high",
                "MEDIUM"   => "sev-medium",
                _          => "sev-low",
            };

        public static string SourceCssClass(string? source) =>
            source?.ToUpperInvariant() switch
            {
                "TUNER"    => "src-tuner",
                "SENTINEL" => "src-sentinel",
                _          => "src-system",
            };

        // ── KPI 집계 ──────────────────────────────────────────────────────────

        /// <summary>타임라인 기반 KPI 값을 계산합니다.</summary>
        public static (int Total, int UnreviewedHigh, int Critical) ComputeKpi(
            IReadOnlyList<TimelineEntry> timeline)
        {
            if (timeline is null || timeline.Count == 0) return (0, 0, 0);
            int total         = timeline.Count;
            int unreviewedHigh = timeline.Count(e => !e.IsReviewed && e.Severity != "CRITICAL");
            int critical      = timeline.Count(e => !e.IsReviewed && e.Severity == "CRITICAL");
            return (total, unreviewedHigh, critical);
        }
    }
}
