// ============================================================================
// ForecastVaultTests — S11 #1 예측 타임캡슐 단위 테스트
//  · 순수 수학 (외부 의존 0), 팩토리, DB 지원 (임시 SQLite 파일)
//  · Committee는 텍스트 전용 → 수치 통계에서 제외 확인
// ============================================================================
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using StockManager.Common;
using Xunit;

namespace StockManager.Tests
{
    public class ForecastVaultTests : IDisposable
    {
        private readonly string _dbPath;
        private readonly LocalDatabase _db;
        private readonly ForecastVaultService _svc;

        public ForecastVaultTests()
        {
            _dbPath = Path.Combine(Path.GetTempPath(), $"vault_test_{Guid.NewGuid():N}.db");
            _db  = new LocalDatabase(_dbPath);
            _svc = new ForecastVaultService(_db);
        }

        public void Dispose()
        {
            _db.Dispose();
            try { File.Delete(_dbPath); } catch { }
        }

        // ════════════════════════════════════════════════════════════════════════
        // 1. 순수 수학
        // ════════════════════════════════════════════════════════════════════════

        [Fact]
        public void ComputeMAE_KnownValues()
        {
            // errors: |3-1|=2, |1-3|=2  →  MAE = 2.0
            double r = ForecastVaultService.ComputeMAE(new[] { 3.0, 1.0 }, new[] { 1.0, 3.0 });
            Assert.Equal(2.0, r, 10);
        }

        [Fact]
        public void ComputeMAE_EmptyList_ReturnsZero()
        {
            double r = ForecastVaultService.ComputeMAE(Array.Empty<double>(), Array.Empty<double>());
            Assert.Equal(0.0, r);
        }

        [Fact]
        public void ComputeMAE_MismatchedLengths_ReturnsZero()
        {
            double r = ForecastVaultService.ComputeMAE(new[] { 1.0 }, new[] { 1.0, 2.0 });
            Assert.Equal(0.0, r);
        }

        [Fact]
        public void ComputeRMSE_KnownValues()
        {
            // errors: +2, -2  →  RMSE = sqrt((4+4)/2) = 2.0
            double r = ForecastVaultService.ComputeRMSE(new[] { 3.0, 1.0 }, new[] { 1.0, 3.0 });
            Assert.Equal(2.0, r, 10);
        }

        [Fact]
        public void ComputeBias_ZeroBias()
        {
            // (3-1) + (1-3) = 0  →  bias = 0.0
            double r = ForecastVaultService.ComputeBias(new[] { 3.0, 1.0 }, new[] { 1.0, 3.0 });
            Assert.Equal(0.0, r, 10);
        }

        [Fact]
        public void ComputeBias_PositiveBias()
        {
            // pred > actual  →  양수 = 낙관 편향
            double r = ForecastVaultService.ComputeBias(new[] { 2.0, 2.0 }, new[] { 0.0, 0.0 });
            Assert.Equal(2.0, r, 10);
        }

        [Fact]
        public void ComputeHitRate_AllCorrect()
        {
            double r = ForecastVaultService.ComputeHitRate(new[] { 1.0, -1.0 }, new[] { 1.0, -1.0 });
            Assert.Equal(100.0, r, 10);
        }

        [Fact]
        public void ComputeHitRate_AllWrong()
        {
            double r = ForecastVaultService.ComputeHitRate(new[] { 1.0, -1.0 }, new[] { -1.0, 1.0 });
            Assert.Equal(0.0, r, 10);
        }

        [Fact]
        public void ComputeCoverage_AllInRange()
        {
            double r = ForecastVaultService.ComputeCoverage(
                actual: new[] { 0.0, 0.5 },
                low:    new[] { -1.0, -1.0 },
                high:   new[] { 1.0,  1.0 });
            Assert.Equal(100.0, r, 10);
        }

        [Fact]
        public void ComputeCoverage_NoneInRange()
        {
            double r = ForecastVaultService.ComputeCoverage(
                actual: new[] { 2.0, 2.0 },
                low:    new[] { -1.0, -1.0 },
                high:   new[] { 1.0,  1.0 });
            Assert.Equal(0.0, r, 10);
        }

        // ════════════════════════════════════════════════════════════════════════
        // 2. 정적 팩토리
        // ════════════════════════════════════════════════════════════════════════

        [Fact]
        public void BuildConeRecord_FieldsCorrect()
        {
            var bands = new ConeBand[]
            {
                new(1, -1.0, -0.5,  0.5, 1.0, 2.0),
                new(2, -2.0, -1.0,  1.5, 2.0, 4.0),  // ← last band: P5=-2 P95=4
            };
            var cone = new FutureConeResult(true, 2, 200, bands,
                1.5, 1.5, -2.0, 4.0, 30.0, 0.0, 0.0, 5.0, "");
            var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

            var rec = ForecastVaultService.BuildConeRecord("005930", Market.KRX_KOSPI, 50000m, cone, now);

            Assert.NotEmpty(rec.ForecastId);
            Assert.Equal(ForecastEngineType.FutureCone, rec.EngineType);
            Assert.Equal("005930", rec.Symbol);
            Assert.Equal(now.AddDays(2), rec.TargetDate);
            Assert.Equal(1.5, rec.PredictedValue, 10);   // MedianReturnPct
            Assert.Equal(-2.0, rec.ConfidenceLow, 10);   // last band P5
            Assert.Equal(4.0, rec.ConfidenceHigh, 10);   // last band P95
            Assert.Null(rec.ActualValue);
            Assert.Contains("\"p\"", rec.CaptureMetaJson);
            Assert.Contains("\"m\"", rec.CaptureMetaJson);
        }

        [Fact]
        public void BuildMultiverseRecord_SymbolIsPortfolio()
        {
            var bands = new ConeBand[] { new(252, -10.0, -5.0, 8.0, 12.0, 20.0) };
            var mv = new MultiverseResult(true, MultiverseScenario.BullRun, bands,
                8.0, 5.0, 0.8, null, Array.Empty<AssetProjection>(), "강세장 · 골디락스");
            var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

            var rec = ForecastVaultService.BuildMultiverseRecord(mv, now);

            Assert.Equal("PORTFOLIO", rec.Symbol);
            Assert.Equal(ForecastEngineType.Multiverse, rec.EngineType);
            Assert.Equal(8.0, rec.PredictedValue, 10);   // ProjectedNavPct
            Assert.Equal("강세장 · 골디락스", rec.ScenarioTag);
            Assert.Contains("scenario", rec.CaptureMetaJson);
        }

        [Fact]
        public void BuildAlphaRecord_PredictedScaled()
        {
            var report = new AlphaReport(
                IsValid: true, Symbol: "TEST", Benchmark: "BM", AlignedDays: 100,
                Beta: 1.0, R2Pct: 50.0, TotalReturnPct: 10.0, MarketAttributedPct: 5.0,
                TrueAlphaPct: 5.0, TrueAlphaAnnualPct: 10.0, InformationRatio: 0.5,
                Series: Array.Empty<AlphaPoint>(), Note: "");
            var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

            var rec = ForecastVaultService.BuildAlphaRecord(report, 100m, Market.KRX_KOSPI, 21, now);

            Assert.Equal("TEST", rec.Symbol);
            Assert.Equal(ForecastEngineType.Alpha, rec.EngineType);
            Assert.Equal(21, rec.HorizonDays);
            Assert.Equal(10.0 * 21.0 / 252.0, rec.PredictedValue, 10);
            Assert.Equal("Alpha", rec.ScenarioTag);
        }

        // ════════════════════════════════════════════════════════════════════════
        // 3. DB 지원 테스트
        // ════════════════════════════════════════════════════════════════════════

        private static ForecastRecord MakeRecord(
            string id = "test01",
            ForecastEngineType et = ForecastEngineType.FutureCone,
            string sym = "A",
            double? actual = null)
        {
            return new ForecastRecord(
                ForecastId:      id,
                CreatedAt:       new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                EngineType:      et,
                Symbol:          sym,
                TargetDate:      new DateTime(2026, 1, 31, 0, 0, 0, DateTimeKind.Utc),
                HorizonDays:     30,
                PredictedValue:  5.0,
                ConfidenceLow:   -2.0,
                ConfidenceHigh:  12.0,
                ScenarioTag:     "",
                CaptureMetaJson: "{\"p\":100.0,\"m\":0}",
                ActualValue:     actual,
                EvaluatedAt:     actual.HasValue
                                    ? new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc)
                                    : null,
                AbsoluteError:   actual.HasValue ? Math.Abs(5.0 - actual.Value) : null,
                Note:            ""
            );
        }

        [Fact]
        public async Task Capture_ThenGetRecent_ReturnsRecord()
        {
            await _svc.CaptureForecastAsync(MakeRecord("id_001"));
            var recent = await _svc.GetRecentAsync(10);
            Assert.Single(recent);
            Assert.Equal("id_001", recent[0].ForecastId);
            Assert.Equal("A", recent[0].Symbol);
        }

        [Fact]
        public async Task Capture_DuplicateIgnored_CountStaysOne()
        {
            var rec = MakeRecord("dup_001");
            await _svc.CaptureForecastAsync(rec);
            await _svc.CaptureForecastAsync(rec);   // INSERT OR IGNORE — 두 번째는 무시
            var recent = await _svc.GetRecentAsync(10);
            Assert.Single(recent.Where(r => r.ForecastId == "dup_001").ToList());
        }

        [Fact]
        public async Task GetSummary_EmptyVault_ZeroStats()
        {
            var s = await _svc.GetSummaryAsync();
            Assert.Equal(0, s.TotalForecasts);
            Assert.Equal(0, s.EvaluatedCount);
            Assert.Equal(0, s.PendingCount);
            Assert.Equal(0.0, s.OverallHitRatePct);
            Assert.Equal(0.0, s.OverallMAE);
        }

        [Fact]
        public async Task GetSummary_ByEngine_AlwaysThreeEntries()
        {
            // 빈 Vault → Cone·Multiverse·Alpha 각각 0건이지만 엔트리는 3개
            var s = await _svc.GetSummaryAsync();
            Assert.Equal(3, s.ByEngine.Count);
            Assert.DoesNotContain(s.ByEngine, e => e.EngineType == ForecastEngineType.Committee);
        }

        [Fact]
        public async Task GetSummary_CommitteeRecord_ExcludedFromStats()
        {
            // Committee 예측(실현값 있음) → TotalForecasts에는 포함되나 수치 통계 제외
            await _svc.CaptureForecastAsync(
                MakeRecord("comm_01", ForecastEngineType.Committee, sym: "X", actual: 3.0));

            var s = await _svc.GetSummaryAsync();

            Assert.Equal(1, s.TotalForecasts);      // 전체 건수에는 포함
            Assert.Equal(0.0, s.OverallHitRatePct); // 수치 통계에서 제외
            Assert.Equal(0.0, s.OverallMAE);
            Assert.DoesNotContain(s.ByEngine, e => e.EngineType == ForecastEngineType.Committee);
        }

        [Fact]
        public async Task GetSummary_WithEvaluatedRecords_CorrectMAEAndHitRate()
        {
            // r1: pred=5, actual=3  → |err|=2, hit (동일 부호)
            await _svc.CaptureForecastAsync(MakeRecord("ev_001", actual: 3.0));

            // r2: pred=5, actual=-1 → |err|=6, miss (반대 부호)
            await _svc.CaptureForecastAsync(new ForecastRecord(
                "ev_002",
                new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                ForecastEngineType.FutureCone, "B",
                new DateTime(2026, 1, 31, 0, 0, 0, DateTimeKind.Utc),
                30, 5.0, -2.0, 12.0, "", "{\"p\":100.0,\"m\":0}",
                -1.0,
                new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc),
                6.0, ""));

            var s = await _svc.GetSummaryAsync();

            Assert.Equal(2, s.EvaluatedCount);
            Assert.Equal(4.0, s.OverallMAE, 10);          // (2 + 6) / 2 = 4.0
            Assert.Equal(50.0, s.OverallHitRatePct, 10);  // 1/2 = 50%
        }

        [Fact]
        public async Task GetRecent_LimitRespected()
        {
            for (int i = 0; i < 5; i++)
                await _svc.CaptureForecastAsync(MakeRecord($"lim_{i:D2}"));
            var recent = await _svc.GetRecentAsync(3);
            Assert.Equal(3, recent.Count);
        }
    }
}
