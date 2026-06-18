// ============================================================================
// ScenarioTrackerEngineTests — S11 #2 시나리오 실현 추적기 단위 테스트
//  · 코사인 유사도 경계값 (완전일치=100 / 직교=50 / 완전반대=0)
//  · PORTFOLIO NAV 조인 실패 가드
//  · Committee 환각 방화벽 (keyword→enum 매핑)
//  · 캘리브레이션 테이블 집계 정확도
//  · 비동기 취소 토큰 전파
// ============================================================================
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using StockManager.Common;
using Xunit;

namespace StockManager.Tests
{
    public class ScenarioTrackerEngineTests : IDisposable
    {
        private readonly string _dbPath;
        private readonly LocalDatabase _db;
        private readonly ForecastVaultService _svc;

        public ScenarioTrackerEngineTests()
        {
            _dbPath = Path.Combine(Path.GetTempPath(), $"tracker_test_{Guid.NewGuid():N}.db");
            _db  = new LocalDatabase(_dbPath);
            _svc = new ForecastVaultService(_db);
        }

        public void Dispose()
        {
            _db.Dispose();
            try { File.Delete(_dbPath); } catch { }
        }

        // ════════════════════════════════════════════════════════════════════════
        // 1. 코사인 유사도 경계값
        // ════════════════════════════════════════════════════════════════════════

        [Fact]
        public void CosineSimilarity_IdenticalVectors_ReturnsOne()
        {
            double cos   = ScenarioTrackerEngine.CosineSimilarity(new[] { 1.0, 0.0 }, new[] { 1.0, 0.0 });
            double score = ScenarioTrackerEngine.MaterializationScore(cos);
            Assert.Equal(1.0,   cos,   10);
            Assert.Equal(100.0, score, 10);
        }

        [Fact]
        public void CosineSimilarity_OrthogonalVectors_ReturnsZero()
        {
            double cos   = ScenarioTrackerEngine.CosineSimilarity(new[] { 1.0, 0.0 }, new[] { 0.0, 1.0 });
            double score = ScenarioTrackerEngine.MaterializationScore(cos);
            Assert.Equal(0.0,  cos,   10);
            Assert.Equal(50.0, score, 10);
        }

        [Fact]
        public void CosineSimilarity_OppositeVectors_ReturnsMinusOne()
        {
            double cos   = ScenarioTrackerEngine.CosineSimilarity(new[] { 1.0, 0.0 }, new[] { -1.0, 0.0 });
            double score = ScenarioTrackerEngine.MaterializationScore(cos);
            Assert.Equal(-1.0, cos,   10);
            Assert.Equal(0.0,  score, 10);
        }

        [Fact]
        public void CosineSimilarity_ZeroVector_ReturnsZero()
        {
            // 영벡터: ZeroDivision 방어 → 0 반환
            double cos = ScenarioTrackerEngine.CosineSimilarity(new[] { 0.0, 0.0 }, new[] { 1.0, 1.0 });
            Assert.Equal(0.0, cos);
        }

        [Fact]
        public void CosineSimilarity_NullInput_ReturnsZero()
        {
            double cos = ScenarioTrackerEngine.CosineSimilarity(null!, new[] { 1.0 });
            Assert.Equal(0.0, cos);
        }

        [Fact]
        public void CosineSimilarity_LengthMismatch_ReturnsZero()
        {
            double cos = ScenarioTrackerEngine.CosineSimilarity(new[] { 1.0, 0.0 }, new[] { 1.0 });
            Assert.Equal(0.0, cos);
        }

        [Fact]
        public void MaterializationScore_CosineOne_Returns100()
            => Assert.Equal(100.0, ScenarioTrackerEngine.MaterializationScore(1.0), 10);

        [Fact]
        public void MaterializationScore_CosineZero_Returns50()
            => Assert.Equal(50.0, ScenarioTrackerEngine.MaterializationScore(0.0), 10);

        [Fact]
        public void MaterializationScore_CosineMinusOne_Returns0()
            => Assert.Equal(0.0, ScenarioTrackerEngine.MaterializationScore(-1.0), 10);

        [Fact]
        public void MaterializationScore_OutOfBounds_Clamped()
        {
            // 입력이 [-1,1]을 벗어나도 [0,100]으로 클램프
            Assert.Equal(0.0,   ScenarioTrackerEngine.MaterializationScore(-2.0), 10);
            Assert.Equal(100.0, ScenarioTrackerEngine.MaterializationScore(2.0),  10);
        }

        // ════════════════════════════════════════════════════════════════════════
        // 2. PORTFOLIO NAV 오차 분석 (조인 실패 가드)
        // ════════════════════════════════════════════════════════════════════════

        [Fact]
        public void EvaluatePortfolioNav_EmptyHoldings_IsInvalid()
        {
            var result = ScenarioTrackerEngine.EvaluatePortfolioNav(
                Array.Empty<PortfolioHoldingCapture>(), 5.0);
            Assert.False(result.IsValid);
        }

        [Fact]
        public void EvaluatePortfolioNav_NoActualPrice_IsInvalid()
        {
            // ActualPrice = null → 실현가 없음 → 정직한 검증 경계
            var holdings = new[]
            {
                new PortfolioHoldingCapture("A", 50.0, 100m, null, Market.KRX_KOSPI),
                new PortfolioHoldingCapture("B", 50.0, 200m, null, Market.KRX_KOSPI),
            };
            var result = ScenarioTrackerEngine.EvaluatePortfolioNav(holdings, 5.0);
            Assert.False(result.IsValid);
        }

        [Fact]
        public void EvaluatePortfolioNav_KnownWeightsAndPrices_CorrectNav()
        {
            // A: 50% 비중, +10%  /  B: 50% 비중, -10%  →  가중 NAV = 0%
            // NavError = pred(5%) - actual(0%) = 5%
            var holdings = new[]
            {
                new PortfolioHoldingCapture("A", 50.0, 100m, 110m, Market.KRX_KOSPI),
                new PortfolioHoldingCapture("B", 50.0, 100m,  90m, Market.KRX_KOSPI),
            };
            var result = ScenarioTrackerEngine.EvaluatePortfolioNav(holdings, 5.0);
            Assert.True(result.IsValid);
            Assert.Equal(0.0, result.ActualNavPct, 10);
            Assert.Equal(5.0, result.NavError,     10);
        }

        // ════════════════════════════════════════════════════════════════════════
        // 3. Committee 환각 방화벽
        // ════════════════════════════════════════════════════════════════════════

        [Fact]
        public void ParseCommitteeSignal_BullishKeyword_ReturnsBullish()
        {
            var sig = ScenarioTrackerEngine.ParseCommitteeSignal("강세 전환 기대, 매수 타이밍");
            Assert.Equal(CommitteeSignal.Bullish, sig);
        }

        [Fact]
        public void ParseCommitteeSignal_BearishKeyword_ReturnsBearish()
        {
            var sig = ScenarioTrackerEngine.ParseCommitteeSignal("약세 지속, 하락 위험 경고");
            Assert.Equal(CommitteeSignal.Bearish, sig);
        }

        [Fact]
        public void ParseCommitteeSignal_EmptyOrIrrelevantText_ReturnsUnknown()
        {
            // 빈 텍스트
            Assert.Equal(CommitteeSignal.Unknown, ScenarioTrackerEngine.ParseCommitteeSignal(""));
            Assert.Equal(CommitteeSignal.Unknown, ScenarioTrackerEngine.ParseCommitteeSignal(null!));
            // 키워드가 전혀 없는 텍스트 → Unknown (Neutral이 아님)
            Assert.Equal(CommitteeSignal.Unknown, ScenarioTrackerEngine.ParseCommitteeSignal("시장 동향 보고서 요약"));
        }

        [Fact]
        public void ScoreCommitteeAlignment_BullishPositiveReturn_Aligned()
        {
            var r = ScenarioTrackerEngine.ScoreCommitteeAlignment(CommitteeSignal.Bullish, 5.0);
            Assert.True(r.IsValid);
            Assert.True(r.IsAligned);
            Assert.True(r.AlignmentScore > 60.0);   // 강한 일치 → 60 이상
        }

        [Fact]
        public void ScoreCommitteeAlignment_BullishNegativeReturn_NotAligned()
        {
            var r = ScenarioTrackerEngine.ScoreCommitteeAlignment(CommitteeSignal.Bullish, -3.0);
            Assert.True(r.IsValid);
            Assert.False(r.IsAligned);
            Assert.True(r.AlignmentScore < 40.0);   // 강한 불일치 → 40 미만
        }

        [Fact]
        public void ScoreCommitteeAlignment_UnknownSignal_IsInvalid()
        {
            var r = ScenarioTrackerEngine.ScoreCommitteeAlignment(CommitteeSignal.Unknown, 5.0);
            Assert.False(r.IsValid);
        }

        // ════════════════════════════════════════════════════════════════════════
        // 4. 시나리오 캘리브레이션 테이블
        // ════════════════════════════════════════════════════════════════════════

        [Fact]
        public void BuildCalibrationTable_EmptyList_ReturnsEmptyRows()
        {
            var table = ScenarioTrackerEngine.BuildCalibrationTable(
                Array.Empty<ForecastRecord>(), DateTime.UtcNow);
            Assert.Empty(table.Rows);
        }

        [Fact]
        public void BuildCalibrationTable_UnevaluatedRecords_ZeroScoreAndEvalCount()
        {
            // ActualValue 없는 Multiverse 레코드 → EvaluatedCount=0, Score=0
            var records = new[]
            {
                MakeMvRecord("mv_01", "BullRun", predictedReturn: 5.0, actualReturn: null),
                MakeMvRecord("mv_02", "BullRun", predictedReturn: 8.0, actualReturn: null),
            };
            var table = ScenarioTrackerEngine.BuildCalibrationTable(records, DateTime.UtcNow);
            Assert.Single(table.Rows);
            Assert.Equal("BullRun",  table.Rows[0].ScenarioTag);
            Assert.Equal(2,          table.Rows[0].TotalCount);
            Assert.Equal(0,          table.Rows[0].EvaluatedCount);
            Assert.Equal(0.0,        table.Rows[0].AvgMaterializationScore);
        }

        [Fact]
        public void BuildCalibrationTable_EvaluatedRecord_IdenticalPredActual_Score100()
        {
            // pred=5, actual=5 → vPred=[5,1] / vAct=[5,1] → cosine=1 → score=100
            var records = new[]
            {
                MakeMvRecord("mv_01", "BullRun", predictedReturn: 5.0, actualReturn: 5.0),
            };
            var table = ScenarioTrackerEngine.BuildCalibrationTable(records, DateTime.UtcNow);
            Assert.Single(table.Rows);
            Assert.Equal(1,     table.Rows[0].EvaluatedCount);
            Assert.Equal(100.0, table.Rows[0].AvgMaterializationScore, 10);
            Assert.Equal(100.0, table.Rows[0].HitRatePct, 10);   // same sign
        }

        [Fact]
        public void BuildCalibrationTable_MultiScenario_GroupsCorrectly()
        {
            var records = new[]
            {
                MakeMvRecord("a1", "BullRun",    5.0,  4.0),
                MakeMvRecord("a2", "BullRun",    3.0,  2.0),
                MakeMvRecord("b1", "DeepRecession", -8.0, -6.0),
            };
            var table = ScenarioTrackerEngine.BuildCalibrationTable(records, DateTime.UtcNow);
            Assert.Equal(2, table.Rows.Count);
            Assert.Equal("BullRun",       table.Rows[0].ScenarioTag);
            Assert.Equal("DeepRecession", table.Rows[1].ScenarioTag);
            Assert.Equal(2, table.Rows[0].TotalCount);
            Assert.Equal(1, table.Rows[1].TotalCount);
        }

        [Fact]
        public void BuildCalibrationTable_NonMultiverseRecords_Ignored()
        {
            // Committee/FutureCone 레코드는 제외됨
            var records = new[]
            {
                MakeRecord("c1", ForecastEngineType.Committee, "X", 3.0, "SomeTag"),
                MakeRecord("c2", ForecastEngineType.FutureCone, "Y", 2.0, "SomeTag"),
                MakeMvRecord("m1", "BullRun", 5.0, 4.0),
            };
            var table = ScenarioTrackerEngine.BuildCalibrationTable(records, DateTime.UtcNow);
            Assert.Single(table.Rows);
            Assert.Equal("BullRun", table.Rows[0].ScenarioTag);
        }

        // ════════════════════════════════════════════════════════════════════════
        // 5. 비동기 취소 토큰 전파
        // ════════════════════════════════════════════════════════════════════════

        [Fact]
        public async Task GetCalibrationTableAsync_PreCancelledToken_ThrowsOperationCancelled()
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => _svc.GetCalibrationTableAsync(cts.Token));
        }

        [Fact]
        public async Task GetCalibrationTableAsync_EmptyVault_ReturnsEmptyRows()
        {
            var table = await _svc.GetCalibrationTableAsync();
            Assert.NotNull(table);
            Assert.Empty(table.Rows);
        }

        // ════════════════════════════════════════════════════════════════════════
        // 헬퍼
        // ════════════════════════════════════════════════════════════════════════

        private static ForecastRecord MakeMvRecord(
            string id, string scenarioTag,
            double predictedReturn, double? actualReturn)
        {
            DateTime now = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            return new ForecastRecord(
                ForecastId:      id,
                CreatedAt:       now,
                EngineType:      ForecastEngineType.Multiverse,
                Symbol:          "PORTFOLIO",
                TargetDate:      now.AddDays(252),
                HorizonDays:     252,
                PredictedValue:  predictedReturn,
                ConfidenceLow:   -15.0,
                ConfidenceHigh:  25.0,
                ScenarioTag:     scenarioTag,
                CaptureMetaJson: $"{{\"scenario\":\"{scenarioTag}\"}}",
                ActualValue:     actualReturn,
                EvaluatedAt:     actualReturn.HasValue
                                    ? new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc)
                                    : null,
                AbsoluteError:   actualReturn.HasValue
                                    ? Math.Abs(predictedReturn - actualReturn.Value)
                                    : null,
                Note:            ""
            );
        }

        private static ForecastRecord MakeRecord(
            string id, ForecastEngineType et, string sym,
            double? actualReturn, string scenarioTag = "")
        {
            DateTime now = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            return new ForecastRecord(
                ForecastId:      id,
                CreatedAt:       now,
                EngineType:      et,
                Symbol:          sym,
                TargetDate:      now.AddDays(252),
                HorizonDays:     252,
                PredictedValue:  5.0,
                ConfidenceLow:   -10.0,
                ConfidenceHigh:  20.0,
                ScenarioTag:     scenarioTag,
                CaptureMetaJson: "{\"p\":100.0,\"m\":0}",
                ActualValue:     actualReturn,
                EvaluatedAt:     actualReturn.HasValue ? now.AddDays(252) : null,
                AbsoluteError:   actualReturn.HasValue ? Math.Abs(5.0 - actualReturn.Value) : null,
                Note:            ""
            );
        }
    }
}
