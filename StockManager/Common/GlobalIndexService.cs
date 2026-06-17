// ============================================================================
// GlobalIndexService — 전세계 주요 지수 60초 폴링 (v3.67.0)
//  · Yahoo Finance v8/chart API (인증 불필요)
//  · 외부 의존 0 (HttpPool.Shared + Safe + Log)
//  · 실패 시 직전 데이터 유지 (null 미노출)
// ============================================================================
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace StockManager.Common
{
    public sealed record IndexRow(
        string Symbol, string Name, string Flag, string Region,
        decimal Last, decimal PrevClose, string Currency)
    {
        public decimal Chg    => Last - PrevClose;
        public decimal ChgPct => PrevClose > 0 ? (Last - PrevClose) / PrevClose * 100m : 0m;
        public bool Up        => Chg >= 0;
    }

    public sealed class GlobalIndexService : IDisposable
    {
        // 11개 지수 — 아시아 → 미국 → 유럽 순서
        private static readonly (string sym, string name, string flag, string region, string currency)[] _catalog =
        {
            ("^KS11",     "코스피",      "🇰🇷", "아시아",  "KRW"),
            ("^KQ11",     "코스닥",      "🇰🇷", "아시아",  "KRW"),
            ("^N225",     "닛케이225",   "🇯🇵", "아시아",  "JPY"),
            ("000001.SS", "상하이종합",  "🇨🇳", "아시아",  "CNY"),
            ("^HSI",      "항셍",        "🇭🇰", "아시아",  "HKD"),
            ("^GSPC",     "S&P 500",     "🇺🇸", "미국",    "USD"),
            ("^IXIC",     "나스닥",      "🇺🇸", "미국",    "USD"),
            ("^DJI",      "다우존스",    "🇺🇸", "미국",    "USD"),
            ("^VIX",      "VIX 공포지수","🌐",  "미국",    "USD"),
            ("^FTSE",     "FTSE 100",   "🇬🇧", "유럽",    "GBP"),
            ("^GDAXI",    "DAX",         "🇩🇪", "유럽",    "EUR"),
        };

        public IReadOnlyList<IndexRow> Indices { get; private set; } = Array.Empty<IndexRow>();
        public event Action? OnRefreshed;

        private readonly CancellationTokenSource _cts = new();

        public GlobalIndexService() { _ = LoopAsync(_cts.Token); }

        private async Task LoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                await RefreshAsync(ct);
                OnRefreshed?.Invoke();
                try { await Task.Delay(60_000, ct); } catch { return; }
            }
        }

        private async Task RefreshAsync(CancellationToken ct)
        {
            var rows = new List<IndexRow>();
            foreach (var (sym, name, flag, region, currency) in _catalog)
            {
                var encoded = Uri.EscapeDataString(sym);
                var url = $"https://query1.finance.yahoo.com/v8/finance/chart/{encoded}?interval=1m&range=1d";
                try
                {
                    var json = await HttpPool.Shared.GetStringAsync(url, ct);
                    using var doc = System.Text.Json.JsonDocument.Parse(json);
                    var meta = doc.RootElement
                        .GetProperty("chart").GetProperty("result")[0].GetProperty("meta");
                    var last = Safe.JsonDec(meta, "regularMarketPrice");
                    var prev = Safe.JsonDec(meta, "previousClose");
                    if (last > 0)
                        rows.Add(new IndexRow(sym, name, flag, region, last, prev > 0 ? prev : last, currency));
                }
                catch (Exception ex) { Log.Warn($"[GlobalIndex] {sym} 실패: {ex.Message}"); }

                try { await Task.Delay(100, ct); } catch { return; }
            }
            if (rows.Count > 0) Indices = rows;
        }

        public void Dispose() { _cts.Cancel(); _cts.Dispose(); }
    }
}
