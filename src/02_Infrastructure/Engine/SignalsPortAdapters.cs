// ============================================================================
// SignalsPortAdapters — Phase 94-F: 시그널 저장소 + 서비스 Port 호스트 어댑터 (원시 SQL/인프라 격리)
//   SignalPage 의 ISignalsRepositoryPort/ISignalsServicePort 를 콘크리트(LocalDatabase 원시 SQL·DataTable·Safe·
//   DashboardService·SampleData·AlertCenter·BacktestEngine·IQuoteProvider)에 결속. ★UI 는 이들 콘크리트를 0개 참조.
//   ★liveArmed:false: 조회/스캔/백테스트/알림 발행 — 발주·소켓·IBroker 무접촉.
// ============================================================================
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MS2E.UI.Portable.Ports.Strategy;
using StockManager.Blazor;   // DashboardService, SignalRow

namespace MS2E.Infrastructure
{
    /// <summary>읽기 전용 시그널 스냅샷(DataRow → 불변 DTO).</summary>
    internal sealed record SignalSnapshot(
        DateTime At, string Symbol, string Name, string TypeLabel, SignalVerdict Verdict,
        string Strategy, string Reason, decimal Target, decimal Stop, decimal Confidence) : ISignalRecordSnapshot;

    internal sealed record SampleSymbolDto(string Symbol, string Name) : ISampleSymbol;

    public sealed class SignalsRepositoryAdapter : ISignalsRepositoryPort
    {
        private readonly LocalDatabase _db;
        public SignalsRepositoryAdapter(LocalDatabase db) => _db = db;

        public IReadOnlyList<ISignalRecordSnapshot> GetRecent(int limit)
        {
            int n = Math.Clamp(limit, 1, 5000);   // 정수 클램프(주입 여지 0)
            var list = new List<ISignalRecordSnapshot>();
            try
            {
                var dt = _db.Query($"SELECT * FROM Signals ORDER BY GeneratedAt DESC LIMIT {n}");
                foreach (DataRow r in dt.Rows)
                {
                    var type = (SignalType)Safe.Int(r["Signal"]);
                    var (label, verdict) = MapType(type);
                    list.Add(new SignalSnapshot(
                        Safe.DT(r["GeneratedAt"]), Safe.Str(r["Symbol"]), Safe.Str(r["Name"]),
                        label, verdict, Safe.Str(r["Strategy"]), Safe.Str(r["Reason"]),
                        Safe.Dec(r["TargetPrice"]), Safe.Dec(r["StopLoss"]), Safe.Dec(r["Confidence"])));
                }
            }
            catch { /* 손상/부재 → 빈 목록(fail-closed) */ }
            return list;
        }

        public int DeleteOlderThan(int days)
        {
            try
            {
                int d = days < 0 ? 0 : days;
                return _db.Execute("DELETE FROM Signals WHERE GeneratedAt < @d",
                    new() { ["@d"] = DateTime.Now.AddDays(-d).ToString("o") });
            }
            catch { return 0; }
        }

        private static (string label, SignalVerdict verdict) MapType(SignalType t) => t switch
        {
            SignalType.StrongBuy  => ("⭐ 강력매수", SignalVerdict.Buy),
            SignalType.Buy        => ("🟢 매수", SignalVerdict.Buy),
            SignalType.Hold       => ("➖ 관망", SignalVerdict.Neutral),
            SignalType.Sell       => ("🔴 매도", SignalVerdict.Sell),
            SignalType.StrongSell => ("❌ 강력매도", SignalVerdict.Sell),
            _                     => ("?", SignalVerdict.Neutral),
        };
    }

    public sealed class SignalsServiceAdapter : ISignalsServicePort
    {
        private readonly DashboardService _svc;
        private readonly IQuoteProvider _provider;
        private readonly LocalDatabase _db;

        public SignalsServiceAdapter(DashboardService svc, IQuoteProvider provider, LocalDatabase db)
        {
            _svc = svc;
            _provider = provider;
            _db = db;
        }

        public Task RefreshCoinSignalsAsync() => _svc.LoadAsync();

        public IReadOnlyList<ICoinSignalView> GetCoinSignals() => _svc.Signals;   // SignalRow : ICoinSignalView(공변)

        public IReadOnlyList<ISampleSymbol> GetSampleSymbols()
            => SampleData.All().Select(x => (ISampleSymbol)new SampleSymbolDto(x.Item1, x.Item2)).ToList();

        public IReadOnlyList<string> GetStrategyNames() => BacktestEngine.StrategyNames;

        // 주식 전체 스캔 — 원본 SignalPage.ScanStocksAsync 룰(60캔들↑·골든=저장+알림/데드=저장만·비대칭) 격리.
        public async Task<int> ScanStocksAsync(CancellationToken ct = default)
        {
            int found = 0;
            foreach (var (sym, name, mkt) in SampleData.All())
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var candles = await _provider.GetHistoryAsync(sym, mkt, DateTime.Now.AddMonths(-6), DateTime.Now, ct).ConfigureAwait(false);
                    if (candles.Count < 60) continue;
                    var closes = candles.Select(c => c.Close).ToList();
                    var ma5 = closes.TakeLast(5).Average();
                    var ma20 = closes.TakeLast(20).Average();
                    var ma5P = closes.SkipLast(1).TakeLast(5).Average();
                    var ma20P = closes.SkipLast(1).TakeLast(20).Average();
                    var last = closes.Last();

                    if (ma5P < ma20P && ma5 > ma20)
                    {
                        SaveSignal(sym, name, SignalType.Buy, "이평 골든크로스", $"MA5({ma5:N0}) > MA20({ma20:N0}) 돌파", last * 1.05m, last * 0.97m, 65);
                        AlertCenter.Instance.RaiseSignal(sym, $"🟢 매수 시그널 · {name}", $"골든크로스 ({last:N0})", AlertLevel.Warning);
                        found++;
                    }
                    else if (ma5P > ma20P && ma5 < ma20)
                    {
                        SaveSignal(sym, name, SignalType.Sell, "이평 데드크로스", $"MA5({ma5:N0}) < MA20({ma20:N0}) 이탈", last * 0.95m, last * 1.02m, 60);
                        found++;   // 매도는 알림 없음(원본 비대칭 룰)
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { Log.Warn($"[Scan] {sym}: {ex.Message}"); }
            }
            return found;
        }

        private void SaveSignal(string sym, string name, SignalType type, string strategy, string reason, decimal target, decimal stop, decimal conf)
        {
            _db.Execute(@"INSERT INTO Signals(Symbol,Name,Signal,Strength,Strategy,Reason,TargetPrice,StopLoss,Confidence,GeneratedAt)
                VALUES(@s,@n,@sig,@str,@stra,@r,@t,@sl,@c,@g)",
                new()
                {
                    ["@s"] = sym, ["@n"] = name,
                    ["@sig"] = (int)type, ["@str"] = (int)SignalStrength.Medium,
                    ["@stra"] = strategy, ["@r"] = reason,
                    ["@t"] = (double)target, ["@sl"] = (double)stop,
                    ["@c"] = (double)conf, ["@g"] = DateTime.Now.ToString("o")
                });
        }

        public async Task<IBacktestSummaryView?> BacktestAsync(string symbol, int strategyIndex, int months, CancellationToken ct = default)
        {
            var sym = (symbol ?? "").Trim().Split(' ')[0];
            if (sym.Length == 0) return null;
            var market = SampleData.All().FirstOrDefault(x => x.Item1 == sym).Item3;   // 미발견 → 기본 KOSPI(원본 룰)
            int m = Math.Clamp(months, 1, 60);
            var candles = await _provider.GetHistoryAsync(sym, market, DateTime.Now.AddMonths(-m), DateTime.Now, ct).ConfigureAwait(false);
            if (candles.Count < 30) return null;
            return await Task.Run<IBacktestSummaryView>(() => BacktestEngine.Run(candles, strategyIndex, market), ct).ConfigureAwait(false);
        }
    }
}
