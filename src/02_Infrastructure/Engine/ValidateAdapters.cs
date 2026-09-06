// ============================================================================
// ValidateAdapters — v12.0.0 Phase 94-P: ValidateHub Port 호스트 어댑터
//   ★원시 SQL/DataRow 를 ValidateRepositoryAdapter 단일 초크포인트로 격리(QueryProject=ZeroAllocRowStreamMapper,
//     DataTable 무물성화). compute 어댑터(Calibrator/Twin/Replay/Forecast)가 데이터+엔진 오케스트레이션.
//   ★liveArmed:false: 사후 검증/보정도/복기 — 정보 제공 전용(실 발주 무접촉).
// ============================================================================
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MS2E.Core;                              // RowProjector
using MS2E.UI.Portable.Ports.Portfolio;      // PfMarket
using MS2E.UI.Portable.Ports.Validate;

namespace MS2E.Infrastructure
{
    // ── ⓪ 불변 스냅샷 리포지토리(원시 SQL 격리·DataRow 무물성화) ─────────────────
    public sealed class ValidateRepositoryAdapter : IValidateRepositoryPort
    {
        private readonly LocalDatabase _db;
        public ValidateRepositoryAdapter(LocalDatabase db) => _db = db;

        public IReadOnlyList<IAlertRecordView> GetAlerts(DateTime from, DateTime to)
            => _db.QueryProject("SELECT Time,Source,Symbol FROM Alerts",
                    c => new AlertRow(c.Time(0), (int)c.Int64(1), c.Str(2)))
                  .Where(a => a.Time >= from && a.Time <= to).ToList();

        public IReadOnlyList<ITxRecordView> GetTransactions(string symbol, DateTime from, DateTime to)
            => _db.QueryProject("SELECT Type,Quantity,Price,ExecutedAt FROM Transactions WHERE Symbol=@s",
                    c => new TxRow((int)c.Int64(0), c.Decimal(1), c.Decimal(2), c.Time(3)),
                    new() { ["@s"] = symbol })
                  .Where(t => t.ExecutedAt >= from && t.ExecutedAt <= to).ToList();

        public IReadOnlyList<ISignalRecordView> GetSignals(string symbol, DateTime from, DateTime to)
            => _db.QueryProject("SELECT Signal,GeneratedAt FROM Signals WHERE Symbol=@s",
                    c => new SignalRow((int)c.Int64(0), c.Time(1)),
                    new() { ["@s"] = symbol })
                  .Where(s => s.GeneratedAt >= from && s.GeneratedAt <= to).ToList();

        private sealed record AlertRow(DateTime Time, int Source, string Symbol) : IAlertRecordView;
        private sealed record TxRow(int Type, decimal Quantity, decimal Price, DateTime ExecutedAt) : ITxRecordView;
        private sealed record SignalRow(int Signal, DateTime GeneratedAt) : ISignalRecordView;
    }

    // ── ① 캘리브레이터 ────────────────────────────────────────────────────────
    public sealed class CalibratorAdapter : ICalibratorPort
    {
        private readonly IValidateRepositoryPort _repo;
        private readonly IQuoteProvider _prov;
        private static readonly string[] SourceKr = { "가격", "뉴스", "시그널", "배당", "시스템" };

        public CalibratorAdapter(IValidateRepositoryPort repo, IQuoteProvider prov) { _repo = repo; _prov = prov; }

        public async Task<ICalibrationReportView> ScoreAsync(int days, int horizon, int thr)
        {
            var to = DateTime.Now;
            var from = to.AddDays(-Math.Max(7, days));
            var alerts = new List<AlertRecord>();
            var symbols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var a in _repo.GetAlerts(from, to))
            {
                var cat = a.Source >= 0 && a.Source < SourceKr.Length ? SourceKr[a.Source] : "기타";
                alerts.Add(new AlertRecord(a.Time, cat, a.Symbol));
                if (!string.IsNullOrWhiteSpace(a.Symbol)) symbols.Add(a.Symbol);
            }
            if (alerts.Count == 0) return new CalibrationReport { Note = "기간 내 알림이 없습니다. 알림이 쌓이면 여기서 신뢰도를 채점합니다." };

            var candleFrom = from.AddDays(-20);
            var bySym = new Dictionary<string, List<Candle>>(StringComparer.OrdinalIgnoreCase);
            foreach (var sym in symbols.Take(30))
            {
                try
                {
                    var mkt = sym.Length == 6 && char.IsDigit(sym[0]) ? Market.KRX_KOSPI : Market.US_NASDAQ;
                    bySym[sym] = await _prov.GetHistoryAsync(sym, mkt, candleFrom, to);
                }
                catch { }
            }
            return AlertCalibrator.Score(alerts, bySym, horizon, thr);
        }
    }

    // ── ② 반사실 트윈 ─────────────────────────────────────────────────────────
    public sealed class TwinAdapter : ITwinPort
    {
        private readonly IValidateRepositoryPort _repo;
        private readonly IQuoteProvider _prov;
        public TwinAdapter(IValidateRepositoryPort repo, IQuoteProvider prov) { _repo = repo; _prov = prov; }

        public async Task<ITwinReportView> CompareAsync(string symbol, PfMarket market, int months)
        {
            var to = DateTime.Now;
            var from = to.AddMonths(-Math.Max(1, months));
            var mkt = (Market)(int)market;
            var candles = await _prov.GetHistoryAsync(symbol, mkt, from, to);
            if (candles.Count < 2) return new TwinReport { Note = "히스토리가 부족합니다(시세 제공자 확인)." };

            var trades = new List<(DateTime, bool, decimal, decimal)>();
            foreach (var t in _repo.GetTransactions(symbol, from, to))
            {
                var type = (TransactionType)t.Type;
                if (type != TransactionType.Buy && type != TransactionType.Sell) continue;
                trades.Add((t.ExecutedAt, type == TransactionType.Buy, t.Quantity, t.Price));
            }

            var sigs = new List<(DateTime, SignalType)>();
            foreach (var s in _repo.GetSignals(symbol, from, to))
                sigs.Add((s.GeneratedAt, (SignalType)s.Signal));

            return CounterfactualTwin.Compare(trades, sigs, candles, mkt);
        }
    }

    // ── ③ 결정 리플레이(빌드 + 시세 enrich) ────────────────────────────────────
    public sealed class ReplayAdapter : IReplayPort
    {
        private readonly LocalDatabase _db;
        private readonly IQuoteProvider _prov;
        public ReplayAdapter(LocalDatabase db, IQuoteProvider prov) { _db = db; _prov = prov; }

        public async Task<IReplayReportView> BuildAsync()
        {
            var rep = ReplayService.Build(_db);
            // 시그널 종목 현재가 조회 → 가상 수익(원본 ReplayPage.LoadAsync 이관).
            foreach (var grp in rep.Signals.GroupBy(s => s.Market))
            {
                var syms = grp.Select(s => s.Symbol).Distinct().ToList();
                try
                {
                    var quotes = await _prov.GetQuotesAsync(syms, grp.Key);
                    foreach (var q in quotes)
                        foreach (var s in rep.Signals.Where(x => x.Symbol == q.Symbol))
                            if (q.Price > 0) s.Current = q.Price;
                }
                catch { }
            }
            var ignored = rep.Signals.Where(s => !s.Followed && s.Current > 0 && s.EntryEst > 0).ToList();
            decimal ignoredAvg = ignored.Count > 0 ? ignored.Average(s => s.HypoReturnPct) : 0m;

            var sigRows = rep.Signals.Select(s => new ReplaySignalRow(
                s.At, s.Name, s.Symbol, s.Strategy, s.EntryEst, s.Current, s.HypoReturnPct, s.Followed)).ToList();
            return new ReplayReportView(rep.BuyCount, rep.FollowRate, rep.FollowedCount, ignoredAvg, rep.Equity, sigRows);
        }

        private sealed record ReplaySignalRow(DateTime At, string Name, string Symbol, string Strategy,
            decimal EntryEst, decimal Current, decimal HypoReturnPct, bool Followed) : IReplaySignalView;

        private sealed record ReplayReportView(int BuyCount, decimal FollowRate, int FollowedCount, decimal IgnoredAvgPct,
            IReadOnlyList<IEquityPointView> Equity, IReadOnlyList<IReplaySignalView> Signals) : IReplayReportView;
    }

    // ── ④ 예측 스코어보드(백테스트 + 라이브 로그 + Vault) ──────────────────────
    public sealed class ForecastExecutionAdapter : IForecastExecutionPort
    {
        private readonly LocalDatabase _db;
        private readonly IQuoteProvider _prov;
        private readonly ForecastVaultService _vault;
        public ForecastExecutionAdapter(LocalDatabase db, IQuoteProvider prov, ForecastVaultService vault)
        { _db = db; _prov = prov; _vault = vault; }

        private static double ZHalf(int band) => band >= 95 ? 1.96 : 1.645;

        public async Task<IForecastScoreView?> RunBacktestAsync(string symbol, PfMarket market, int horizon, int band, int lookback)
        {
            var candles = await _prov.GetHistoryAsync(symbol, (Market)(int)market, DateTime.Now.AddMonths(-36), DateTime.Now);
            var closes = candles.Select(c => c.Close).ToList();
            var resolved = ForecastScore.CalibrationBacktest(closes, horizon, lookback, ZHalf(band), band / 100.0);
            if (resolved.Count == 0) return null;
            return ForecastScore.Score(resolved);
        }

        public int PendingCount() => ForecastLog.PendingCount(_db);
        public IForecastScoreView LiveScore() => ForecastScore.Score(ForecastLog.ResolvedToScore(_db));

        public async Task<string> IssueLiveAsync(string symbol, PfMarket market, int horizon, int band, int lookback)
        {
            var candles = await _prov.GetHistoryAsync(symbol, (Market)(int)market, DateTime.Now.AddDays(-lookback - 20), DateTime.Now);
            var closes = candles.Select(c => c.Close).ToList();
            if (closes.Count < 10) return "시세 부족";
            int n = Math.Min(lookback, closes.Count - 1);
            var rets = new List<double>();
            for (int i = closes.Count - n; i < closes.Count; i++)
            {
                var p = (double)closes[i - 1];
                if (p != 0) rets.Add(((double)closes[i] - p) / p * 100.0);
            }
            double mean = rets.Average();
            double std = Math.Sqrt(rets.Select(r => (r - mean) * (r - mean)).Sum() / Math.Max(1, rets.Count - 1));
            double pred = mean * horizon, hStd = std * Math.Sqrt(horizon);
            ForecastLog.Record(_db, new LiveForecast
            {
                IssuedAt = DateTime.Now.ToString("o"),
                ResolveAt = DateTime.Now.AddDays(Math.Ceiling(horizon * 1.45)).ToString("o"),
                Symbol = symbol, Name = symbol, Market = (int)market, HorizonDays = horizon,
                BasePrice = (double)closes[^1], PredictedPct = pred,
                LowPct = pred - ZHalf(band) * hStd, HighPct = pred + ZHalf(band) * hStd, NominalCoverage = band / 100.0
            });
            return $"발행됨 (기준가 {closes[^1]:N0}, {horizon}일 후 정산)";
        }

        public async Task<int> ResolveLiveAsync()
        {
            var due = ForecastLog.Load(_db).Where(f => !f.Resolved && DateTime.TryParse(f.ResolveAt, out var ra) && ra <= DateTime.Now).ToList();
            var px = new Dictionary<(string, int), decimal>();
            foreach (var grp in due.GroupBy(f => (f.Symbol, f.Market)))
            {
                try
                {
                    var q = await _prov.GetQuotesAsync(new[] { grp.Key.Symbol }, (Market)grp.Key.Market);
                    var pr = q.FirstOrDefault()?.Price ?? 0m;
                    if (pr > 0) px[(grp.Key.Symbol, grp.Key.Market)] = pr;
                }
                catch { }
            }
            return ForecastLog.ResolveDue(_db, (s, m) => px.TryGetValue((s, (int)m), out var p) ? p : 0m, DateTime.Now);
        }

        public async Task<IVaultSummaryView?> GetVaultSummaryAsync(CancellationToken ct) => await _vault.GetSummaryAsync(ct);
        public async Task<IReadOnlyList<IForecastRecordView>> GetVaultRecentAsync(int n, CancellationToken ct)
            => await _vault.GetRecentAsync(n, ct);   // List<ForecastRecord> → 공변
        public async Task<int> EvaluateVaultAsync() => await _vault.EvaluateExpiredForecastsAsync(_prov);
    }
}
