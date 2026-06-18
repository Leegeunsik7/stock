// ============================================================================
// ForecastVaultService — S11 #1 예측 타임캡슐
//  · ForecastVault SQLite 테이블 자체 관리 (InitSchema 미수정)
//  · Committee(=텍스트 전용) 수치 통계 제외
//  · PORTFOLIO 평가는 S11 #2 예정 — 정직한 검증 경계 명시
//  · 외부 의존 0 (System.Text.Json = BCL 기본 포함)
// ============================================================================
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace StockManager.Common
{
    public class ForecastVaultService
    {
        private readonly LocalDatabase _db;

        public ForecastVaultService(LocalDatabase db)
        {
            _db = db;
            EnsureSchema();
        }

        // ── 스키마 ─────────────────────────────────────────────────────────────────

        private void EnsureSchema()
        {
            _db.Execute(@"
CREATE TABLE IF NOT EXISTS ForecastVault (
    ForecastId      TEXT PRIMARY KEY,
    CreatedAt       TEXT NOT NULL,
    EngineType      INTEGER NOT NULL,
    Symbol          TEXT NOT NULL,
    TargetDate      TEXT NOT NULL,
    HorizonDays     INTEGER NOT NULL,
    PredictedValue  REAL NOT NULL,
    ConfidenceLow   REAL NOT NULL,
    ConfidenceHigh  REAL NOT NULL,
    ScenarioTag     TEXT NOT NULL DEFAULT '',
    CaptureMetaJson TEXT NOT NULL DEFAULT '{}',
    ActualValue     REAL,
    EvaluatedAt     TEXT,
    AbsoluteError   REAL,
    Note            TEXT NOT NULL DEFAULT ''
);
CREATE INDEX IF NOT EXISTS IX_FV_CreatedAt  ON ForecastVault(CreatedAt DESC);
CREATE INDEX IF NOT EXISTS IX_FV_TargetDate ON ForecastVault(TargetDate);
CREATE INDEX IF NOT EXISTS IX_FV_Engine     ON ForecastVault(EngineType);
");
        }

        // ── CRUD ───────────────────────────────────────────────────────────────────

        public Task CaptureForecastAsync(ForecastRecord record, CancellationToken ct = default)
            => Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                _db.Execute(
                    @"INSERT OR IGNORE INTO ForecastVault
                      (ForecastId,CreatedAt,EngineType,Symbol,TargetDate,HorizonDays,
                       PredictedValue,ConfidenceLow,ConfidenceHigh,
                       ScenarioTag,CaptureMetaJson,
                       ActualValue,EvaluatedAt,AbsoluteError,Note)
                      VALUES(@id,@ca,@et,@sy,@td,@hd,@pv,@cl,@ch,@st,@cm,@av,@ea,@ae,@no)",
                    new()
                    {
                        ["@id"] = record.ForecastId,
                        ["@ca"] = record.CreatedAt.ToString("o"),
                        ["@et"] = (int)record.EngineType,
                        ["@sy"] = record.Symbol,
                        ["@td"] = record.TargetDate.ToString("o"),
                        ["@hd"] = record.HorizonDays,
                        ["@pv"] = record.PredictedValue,
                        ["@cl"] = record.ConfidenceLow,
                        ["@ch"] = record.ConfidenceHigh,
                        ["@st"] = record.ScenarioTag,
                        ["@cm"] = record.CaptureMetaJson,
                        ["@av"] = (object?)record.ActualValue ?? DBNull.Value,
                        ["@ea"] = record.EvaluatedAt.HasValue
                                    ? (object)record.EvaluatedAt.Value.ToString("o")
                                    : DBNull.Value,
                        ["@ae"] = (object?)record.AbsoluteError ?? DBNull.Value,
                        ["@no"] = record.Note,
                    });
            }, ct);

        public Task<List<ForecastRecord>> GetRecentAsync(
            int limit = 50, CancellationToken ct = default)
            => Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                var dt = _db.Query(
                    "SELECT * FROM ForecastVault ORDER BY CreatedAt DESC LIMIT @lim",
                    new() { ["@lim"] = limit });
                return dt.Rows.Cast<DataRow>().Select(RowToRecord).ToList();
            }, ct);

        public Task<VaultSummary> GetSummaryAsync(CancellationToken ct = default)
            => Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                return BuildSummary(LoadAll());
            }, ct);

        // ── 만기 평가 ─────────────────────────────────────────────────────────────

        public async Task<int> EvaluateExpiredForecastsAsync(
            IQuoteProvider provider, CancellationToken ct = default)
        {
            var expired = await Task.Run(() => LoadExpired(), ct);
            if (expired.Count == 0) return 0;

            int count = 0;
            foreach (var rec in expired)
            {
                ct.ThrowIfCancellationRequested();

                // 정직한 검증 경계: PORTFOLIO·Committee는 수치 평가 미지원 (S11 #2 예정)
                if (rec.Symbol == "PORTFOLIO") continue;
                if (rec.EngineType == ForecastEngineType.Committee) continue;

                try
                {
                    double? actual = await EvalSymbolAsync(rec, provider, ct);
                    if (actual.HasValue)
                    {
                        WriteEvaluation(rec.ForecastId, actual.Value,
                            Math.Abs(rec.PredictedValue - actual.Value), DateTime.UtcNow);
                        count++;
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch { /* 시세 없음 — 다음 평가 주기에 재시도 */ }
            }
            return count;
        }

        // ── 정적 팩토리 ───────────────────────────────────────────────────────────

        public static ForecastRecord BuildConeRecord(
            string symbol, Market market, decimal capturePrice,
            FutureConeResult cone, DateTime now)
        {
            int n = cone.Bands.Count;
            double cl = n > 0 ? cone.Bands[n - 1].P5  : 0.0;
            double ch = n > 0 ? cone.Bands[n - 1].P95 : 0.0;
            return new ForecastRecord(
                ForecastId:      Guid.NewGuid().ToString("N"),
                CreatedAt:       now,
                EngineType:      ForecastEngineType.FutureCone,
                Symbol:          symbol,
                TargetDate:      now.AddDays(n),
                HorizonDays:     n,
                PredictedValue:  cone.MedianReturnPct,
                ConfidenceLow:   cl,
                ConfidenceHigh:  ch,
                ScenarioTag:     "",
                CaptureMetaJson: $"{{\"p\":{(double)capturePrice:R},\"m\":{(int)market}}}",
                ActualValue:     null,
                EvaluatedAt:     null,
                AbsoluteError:   null,
                Note:            ""
            );
        }

        public static ForecastRecord BuildMultiverseRecord(
            MultiverseResult mv, DateTime now)
        {
            int n = mv.Bands.Count;
            double cl = n > 0 ? mv.Bands[n - 1].P5  : 0.0;
            double ch = n > 0 ? mv.Bands[n - 1].P95 : 0.0;
            return new ForecastRecord(
                ForecastId:      Guid.NewGuid().ToString("N"),
                CreatedAt:       now,
                EngineType:      ForecastEngineType.Multiverse,
                Symbol:          "PORTFOLIO",
                TargetDate:      now.AddDays(n),
                HorizonDays:     n,
                PredictedValue:  mv.ProjectedNavPct,
                ConfidenceLow:   cl,
                ConfidenceHigh:  ch,
                ScenarioTag:     mv.Note,
                CaptureMetaJson: $"{{\"scenario\":\"{EscapeJson(mv.Note)}\"}}",
                ActualValue:     null,
                EvaluatedAt:     null,
                AbsoluteError:   null,
                Note:            ""
            );
        }

        public static ForecastRecord BuildAlphaRecord(
            AlphaReport report, decimal capturePrice,
            Market market, int horizonDays, DateTime now)
        {
            double predictedPct = report.TrueAlphaAnnualPct * horizonDays / 252.0;
            return new ForecastRecord(
                ForecastId:      Guid.NewGuid().ToString("N"),
                CreatedAt:       now,
                EngineType:      ForecastEngineType.Alpha,
                Symbol:          report.Symbol,
                TargetDate:      now.AddDays(horizonDays),
                HorizonDays:     horizonDays,
                PredictedValue:  predictedPct,
                ConfidenceLow:   0.0,
                ConfidenceHigh:  0.0,
                ScenarioTag:     "Alpha",
                CaptureMetaJson: $"{{\"p\":{(double)capturePrice:R},\"m\":{(int)market}}}",
                ActualValue:     null,
                EvaluatedAt:     null,
                AbsoluteError:   null,
                Note:            ""
            );
        }

        // ── 순수 수학 (internal — 테스트에서 직접 검증) ────────────────────────────

        public static double ComputeMAE(IList<double> predicted, IList<double> actual)
        {
            if (predicted.Count == 0 || predicted.Count != actual.Count) return 0.0;
            double s = 0.0;
            for (int i = 0; i < predicted.Count; i++)
                s += Math.Abs(predicted[i] - actual[i]);
            return s / predicted.Count;
        }

        public static double ComputeRMSE(IList<double> predicted, IList<double> actual)
        {
            if (predicted.Count == 0 || predicted.Count != actual.Count) return 0.0;
            double s = 0.0;
            for (int i = 0; i < predicted.Count; i++)
            {
                double d = predicted[i] - actual[i];
                s += d * d;
            }
            return Math.Sqrt(s / predicted.Count);
        }

        public static double ComputeBias(IList<double> predicted, IList<double> actual)
        {
            if (predicted.Count == 0 || predicted.Count != actual.Count) return 0.0;
            double s = 0.0;
            for (int i = 0; i < predicted.Count; i++)
                s += predicted[i] - actual[i];
            return s / predicted.Count;
        }

        public static double ComputeHitRate(IList<double> predicted, IList<double> actual)
        {
            if (predicted.Count == 0 || predicted.Count != actual.Count) return 0.0;
            int hits = 0;
            for (int i = 0; i < predicted.Count; i++)
                if (Math.Sign(predicted[i]) == Math.Sign(actual[i])) hits++;
            return hits * 100.0 / predicted.Count;
        }

        public static double ComputeCoverage(
            IList<double> actual, IList<double> low, IList<double> high)
        {
            if (actual.Count == 0 ||
                actual.Count != low.Count ||
                actual.Count != high.Count) return 0.0;
            int hits = 0;
            for (int i = 0; i < actual.Count; i++)
                if (actual[i] >= low[i] && actual[i] <= high[i]) hits++;
            return hits * 100.0 / actual.Count;
        }

        // ── 내부 ───────────────────────────────────────────────────────────────────

        private List<ForecastRecord> LoadAll()
        {
            var dt = _db.Query("SELECT * FROM ForecastVault ORDER BY CreatedAt DESC");
            return dt.Rows.Cast<DataRow>().Select(RowToRecord).ToList();
        }

        private List<ForecastRecord> LoadExpired()
        {
            var dt = _db.Query(
                "SELECT * FROM ForecastVault WHERE ActualValue IS NULL AND TargetDate <= @now",
                new() { ["@now"] = DateTime.UtcNow.ToString("o") });
            return dt.Rows.Cast<DataRow>().Select(RowToRecord).ToList();
        }

        private async Task<double?> EvalSymbolAsync(
            ForecastRecord rec, IQuoteProvider provider, CancellationToken ct)
        {
            double capturePrice;
            Market market;
            try
            {
                using var doc = JsonDocument.Parse(rec.CaptureMetaJson);
                capturePrice = doc.RootElement.GetProperty("p").GetDouble();
                market       = (Market)doc.RootElement.GetProperty("m").GetInt32();
            }
            catch { return null; }   // 메타 파싱 실패 → 건너뜀

            if (capturePrice <= 0.0) return null;

            // TargetDate ±10일 범위에서 가장 근접한 캔들로 실현 수익률 계산
            var from = rec.TargetDate.AddDays(-10);
            var to   = rec.TargetDate.AddDays(5);
            var candles = await provider.GetHistoryAsync(rec.Symbol, market, from, to, ct);
            if (candles.Count == 0) return null;

            var closest = candles
                .OrderBy(c => Math.Abs((c.Date - rec.TargetDate).TotalDays))
                .First();

            return ((double)closest.Close - capturePrice) / capturePrice * 100.0;
        }

        private void WriteEvaluation(
            string forecastId, double actualValue, double absErr, DateTime evaluatedAt)
        {
            _db.Execute(
                @"UPDATE ForecastVault
                  SET ActualValue=@av, EvaluatedAt=@ea, AbsoluteError=@ae
                  WHERE ForecastId=@id",
                new()
                {
                    ["@av"] = actualValue,
                    ["@ea"] = evaluatedAt.ToString("o"),
                    ["@ae"] = absErr,
                    ["@id"] = forecastId,
                });
        }

        private VaultSummary BuildSummary(List<ForecastRecord> all)
        {
            int total    = all.Count;
            var evaluated = all.Where(r => r.ActualValue.HasValue).ToList();
            int pending   = total - evaluated.Count;

            // 전체 통계 (Committee 제외)
            var scorable = evaluated
                .Where(r => r.EngineType != ForecastEngineType.Committee)
                .ToList();
            var sPred   = scorable.Select(r => r.PredictedValue).ToList();
            var sActual = scorable.Select(r => r.ActualValue!.Value).ToList();

            // 엔진별 통계
            var byEngine = new List<EngineStatistics>();
            foreach (ForecastEngineType et in Enum.GetValues<ForecastEngineType>())
            {
                if (et == ForecastEngineType.Committee) continue;

                var grp   = all.Where(r => r.EngineType == et).ToList();
                var evGrp = grp.Where(r => r.ActualValue.HasValue).ToList();

                var pred   = evGrp.Select(r => r.PredictedValue).ToList();
                var actual = evGrp.Select(r => r.ActualValue!.Value).ToList();
                var low    = evGrp.Select(r => r.ConfidenceLow).ToList();
                var high   = evGrp.Select(r => r.ConfidenceHigh).ToList();

                byEngine.Add(new EngineStatistics(
                    EngineType:     et,
                    TotalCount:     grp.Count,
                    EvaluatedCount: evGrp.Count,
                    HitRatePct:     ComputeHitRate(pred, actual),
                    MAE:            ComputeMAE(pred, actual),
                    RMSE:           ComputeRMSE(pred, actual),
                    MeanBias:       ComputeBias(pred, actual),
                    CoveragePct:    ComputeCoverage(actual, low, high)
                ));
            }

            return new VaultSummary(
                TotalForecasts:    total,
                EvaluatedCount:    evaluated.Count,
                PendingCount:      pending,
                OverallHitRatePct: ComputeHitRate(sPred, sActual),
                OverallMAE:        ComputeMAE(sPred, sActual),
                ByEngine:          byEngine
            );
        }

        private static ForecastRecord RowToRecord(DataRow r)
        {
            double? av  = r.IsNull("ActualValue")  ? null : (double?)Convert.ToDouble(r["ActualValue"]);
            double? ae  = r.IsNull("AbsoluteError") ? null : (double?)Convert.ToDouble(r["AbsoluteError"]);
            DateTime? ea = r.IsNull("EvaluatedAt") ? null
                : DateTime.TryParse(r["EvaluatedAt"]?.ToString(), out var dt) ? dt : null;

            return new ForecastRecord(
                ForecastId:      r["ForecastId"]?.ToString()     ?? "",
                CreatedAt:       DateTime.TryParse(r["CreatedAt"]?.ToString(), out var ca) ? ca : DateTime.MinValue,
                EngineType:      (ForecastEngineType)Convert.ToInt32(r["EngineType"]),
                Symbol:          r["Symbol"]?.ToString()         ?? "",
                TargetDate:      DateTime.TryParse(r["TargetDate"]?.ToString(), out var td) ? td : DateTime.MinValue,
                HorizonDays:     Convert.ToInt32(r["HorizonDays"]),
                PredictedValue:  Convert.ToDouble(r["PredictedValue"]),
                ConfidenceLow:   Convert.ToDouble(r["ConfidenceLow"]),
                ConfidenceHigh:  Convert.ToDouble(r["ConfidenceHigh"]),
                ScenarioTag:     r["ScenarioTag"]?.ToString()     ?? "",
                CaptureMetaJson: r["CaptureMetaJson"]?.ToString() ?? "{}",
                ActualValue:     av,
                EvaluatedAt:     ea,
                AbsoluteError:   ae,
                Note:            r["Note"]?.ToString()            ?? ""
            );
        }

        private static string EscapeJson(string s)
            => s.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }
}
