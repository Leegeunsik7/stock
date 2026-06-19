// ============================================================================
// ExecutionQualityTests — S17 #3: 실행 품질 서비스 단위 테스트 (12개)
//
//  §1 타임라인 빌드 (6)  — 빈 입력, 정렬 역순, 소스 보존, 심각도 보존,
//                          미검토 필터링, 전체 포함 옵션
//  §2 슬리피지 산출 (3)  — 0 기준가 방어, 양수 슬리피지, 음수 슬리피지
//  §3 CSS·KPI 맵핑 (3)  — 심각도 CSS 클래스, 소스 CSS 클래스, KPI 집계
// ============================================================================
using System;
using System.Collections.Generic;
using System.Linq;
using StockManager.Common;
using Xunit;

namespace StockManager.Tests
{
    public sealed class ExecutionQualityTests
    {
        // ── §1 타임라인 빌드 ────────────────────────────────────────────────

        [Fact]
        public void Timeline_NullInput_ReturnsEmpty()
        {
            var result = ExecutionQualityService.BuildTimeline(null!);
            Assert.Empty(result);
        }

        [Fact]
        public void Timeline_EmptyInput_ReturnsEmpty()
        {
            var result = ExecutionQualityService.BuildTimeline(new List<FraudAlert>());
            Assert.Empty(result);
        }

        [Fact]
        public void Timeline_SortsNewestFirst()
        {
            var alerts = new List<FraudAlert>
            {
                FA(1, "SENTINEL", "HIGH", new DateTime(2026, 1, 1)),
                FA(2, "TUNER",    "CRITICAL", new DateTime(2026, 6, 1)),
                FA(3, "SYSTEM",   "MEDIUM",   new DateTime(2026, 3, 1)),
            };
            var result = ExecutionQualityService.BuildTimeline(alerts);

            Assert.Equal(3, result.Count);
            // 최신순: 2026-06-01 → 2026-03-01 → 2026-01-01
            Assert.Equal(new DateTime(2026, 6, 1), result[0].OccurredAt);
            Assert.Equal(new DateTime(2026, 3, 1), result[1].OccurredAt);
            Assert.Equal(new DateTime(2026, 1, 1), result[2].OccurredAt);
        }

        [Fact]
        public void Timeline_Source_PreservedFromTransactionType()
        {
            var alerts = new List<FraudAlert>
            {
                FA(1, "SENTINEL", "HIGH"),
                FA(2, "TUNER",    "CRITICAL"),
                FA(3, "system",   "MEDIUM"),  // 소문자 → 대문자 정규화
            };
            var result = ExecutionQualityService.BuildTimeline(alerts)
                .OrderBy(e => e.DbId).ToList();

            Assert.Equal("SENTINEL", result[0].Source);
            Assert.Equal("TUNER",    result[1].Source);
            Assert.Equal("SYSTEM",   result[2].Source);
        }

        [Fact]
        public void Timeline_Severity_UpperCasedAndPreserved()
        {
            var alerts = new List<FraudAlert>
            {
                FA(1, "SENTINEL", "critical"),  // 소문자 → CRITICAL
                FA(2, "TUNER",    "HIGH"),
            };
            var result = ExecutionQualityService.BuildTimeline(alerts);

            Assert.Contains(result, e => e.Severity == "CRITICAL");
            Assert.Contains(result, e => e.Severity == "HIGH");
        }

        [Fact]
        public void Timeline_ExcludesReviewed_WhenFlagFalse()
        {
            var alerts = new List<FraudAlert>
            {
                FA(1, "SENTINEL", "HIGH",  isReviewed: false),
                FA(2, "TUNER",    "HIGH",  isReviewed: true),
                FA(3, "SYSTEM",   "HIGH",  isReviewed: false),
            };
            var result = ExecutionQualityService.BuildTimeline(alerts, includeReviewed: false);

            Assert.Equal(2, result.Count);
            Assert.All(result, e => Assert.False(e.IsReviewed));
        }

        [Fact]
        public void Timeline_IncludesReviewed_ByDefault()
        {
            var alerts = new List<FraudAlert>
            {
                FA(1, "SENTINEL", "HIGH", isReviewed: true),
                FA(2, "TUNER",    "HIGH", isReviewed: false),
            };
            var result = ExecutionQualityService.BuildTimeline(alerts);  // default: true

            Assert.Equal(2, result.Count);
        }

        // ── §2 슬리피지 산출 ────────────────────────────────────────────────

        [Fact]
        public void SlippagePct_ZeroBenchmark_ReturnsZero()
        {
            Assert.Equal(0m, ExecutionQualityService.SlippagePct(100_000m, 0m));
            Assert.Equal(0m, ExecutionQualityService.SlippagePct(100_000m, -1m));
        }

        [Fact]
        public void SlippagePct_HigherFill_ReturnsPositive()
        {
            // 110 vs benchmark 100 → +10%
            decimal slip = ExecutionQualityService.SlippagePct(110m, 100m);
            Assert.True(slip > 0m, $"Expected positive slippage, got {slip}");
            Assert.Equal(10m, slip);
        }

        [Fact]
        public void SlippagePct_LowerFill_ReturnsNegative()
        {
            // 90 vs benchmark 100 → -10%
            decimal slip = ExecutionQualityService.SlippagePct(90m, 100m);
            Assert.True(slip < 0m, $"Expected negative slippage, got {slip}");
            Assert.Equal(-10m, slip);
        }

        // ── §3 CSS·KPI 맵핑 ─────────────────────────────────────────────────

        [Fact]
        public void SeverityClass_MapsAllLevels()
        {
            Assert.Equal("sev-critical", ExecutionQualityService.SeverityCssClass("CRITICAL"));
            Assert.Equal("sev-high",     ExecutionQualityService.SeverityCssClass("HIGH"));
            Assert.Equal("sev-medium",   ExecutionQualityService.SeverityCssClass("MEDIUM"));
            Assert.Equal("sev-low",      ExecutionQualityService.SeverityCssClass("unknown"));
            Assert.Equal("sev-low",      ExecutionQualityService.SeverityCssClass(null));
        }

        [Fact]
        public void SourceClass_MapsAllSources()
        {
            Assert.Equal("src-sentinel", ExecutionQualityService.SourceCssClass("SENTINEL"));
            Assert.Equal("src-tuner",    ExecutionQualityService.SourceCssClass("TUNER"));
            Assert.Equal("src-system",   ExecutionQualityService.SourceCssClass("SYSTEM"));
            Assert.Equal("src-system",   ExecutionQualityService.SourceCssClass(null));
        }

        [Fact]
        public void ComputeKpi_Aggregates_Correctly()
        {
            var entries = new List<TimelineEntry>
            {
                TL("HIGH",     isReviewed: false),
                TL("HIGH",     isReviewed: true),   // 검토 완료 → UnreviewedHigh 제외
                TL("CRITICAL", isReviewed: false),
                TL("CRITICAL", isReviewed: true),   // 검토 완료 → Critical 제외
                TL("MEDIUM",   isReviewed: false),
            };

            var (total, unreviewedHigh, critical) =
                ExecutionQualityService.ComputeKpi(entries);

            Assert.Equal(5, total);
            Assert.Equal(2, unreviewedHigh);   // HIGH unreviewed + MEDIUM unreviewed
            Assert.Equal(1, critical);          // CRITICAL unreviewed only
        }

        // ── 헬퍼 ─────────────────────────────────────────────────────────────

        private static FraudAlert FA(
            int id, string source, string severity,
            DateTime? at = null, bool isReviewed = false)
            => new FraudAlert
            {
                Id              = id,
                AttemptedAt     = at ?? DateTime.UtcNow,
                Symbol          = "TEST",
                TransactionType = source,
                Reason          = $"테스트 경보 {id}",
                Severity        = severity,
                IsReviewed      = isReviewed,
            };

        private static TimelineEntry TL(string severity, bool isReviewed = false)
            => new TimelineEntry(
                OccurredAt: DateTime.UtcNow,
                Source:     "SENTINEL",
                Severity:   severity,
                Symbol:     "TEST",
                Message:    "test",
                IsReviewed: isReviewed,
                DbId:       1);
    }
}
