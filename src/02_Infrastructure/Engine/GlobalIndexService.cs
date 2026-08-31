// ============================================================================
// GlobalIndexService — 전세계 주요 지수 폴링 (v3.67.0 · v5.4.1 국내 실시간 오버레이)
//  · 해외/선물: Yahoo Finance v8/chart API (인증 불필요·60초)
//  · ★국내(코스피/코스닥): 네이버 realtime 지수 API (KRX 실시간·12초) — Yahoo ^KS11/^KQ11 은 KRX 대비 지연이라 전담 대체
//  · 외부 의존 0 (HttpPool.Shared + Safe + Log)
//  · 실패 시 직전 데이터 유지 (null 미노출·심볼별 upsert 병합)
// ============================================================================
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace MS2E.Infrastructure
{
    public sealed record IndexRow(
        string Symbol, string Name, string Flag, string Region,
        decimal Last, decimal PrevClose, string Currency)
        : MS2E.UI.Portable.Ports.Shell.IIndexRowView   // Phase 94-K: Name/Last/ChgPct 공변
    {
        public decimal Chg    => Last - PrevClose;
        public decimal ChgPct => PrevClose > 0 ? (Last - PrevClose) / PrevClose * 100m : 0m;
        public bool Up        => Chg >= 0;
    }

    public sealed class GlobalIndexService : IDisposable
    {
        // 지수 카탈로그(표시 순서) — 아시아 → 미국 → 미국선물 → 유럽. 런타임은 _catalog.Length 기준(개수 하드코딩 금지).
        private static readonly (string sym, string name, string flag, string region, string currency)[] _catalog =
        {
            ("^KS11",     "코스피",       "🇰🇷", "아시아",  "KRW"),
            ("^KQ11",     "코스닥",       "🇰🇷", "아시아",  "KRW"),
            ("^N225",     "닛케이225",    "🇯🇵", "아시아",  "JPY"),
            ("000001.SS", "상하이종합",   "🇨🇳", "아시아",  "CNY"),
            ("^HSI",      "항셍",         "🇭🇰", "아시아",  "HKD"),
            ("^GSPC",     "S&P 500",      "🇺🇸", "미국",    "USD"),
            ("^IXIC",     "나스닥",       "🇺🇸", "미국",    "USD"),
            ("^DJI",      "다우존스",     "🇺🇸", "미국",    "USD"),
            // v4.64.0 — 미국 지수 선물(Yahoo v8 =F 연속물·24h 거래라 장외 추세 관측). v5.4.1 러셀2000 선물(RTY=F) 합류.
            ("NQ=F",      "나스닥 선물",  "🇺🇸", "미국선물","USD"),
            ("ES=F",      "S&P 선물",     "🇺🇸", "미국선물","USD"),
            ("RTY=F",     "러셀2000 선물","🇺🇸", "미국선물","USD"),
            ("YM=F",      "다우 선물",    "🇺🇸", "미국선물","USD"),
            ("^VIX",      "VIX 공포지수", "🌐",  "미국",    "USD"),
            ("^FTSE",     "FTSE 100",    "🇬🇧", "유럽",    "GBP"),
            ("^GDAXI",    "DAX",          "🇩🇪", "유럽",    "EUR"),
        };

        // ★국내 지수 네이버 실시간 전담(sym 은 카탈로그와 동일 키 유지 → 표시 순서·구독 심볼 불변). naverCode=네이버 지수 코드.
        private static readonly (string sym, string naverCode, string name)[] _krCatalog =
        {
            ("^KS11", "KOSPI",  "코스피"),
            ("^KQ11", "KOSDAQ", "코스닥"),
        };

        public IReadOnlyList<IndexRow> Indices { get; private set; } = Array.Empty<IndexRow>();
        public event Action? OnRefreshed;

        private readonly Dictionary<string, IndexRow> _bySym = new();   // 심볼별 최신 행(Yahoo·네이버 loop 공용 병합)
        private readonly object _lock = new();
        private readonly CancellationTokenSource _cts = new();
        private long _krLastNaverMs;   // 네이버 국내 지수 마지막 성공 시각(TickCount64) — Yahoo 국내 폴백 게이트(0=미성공→Yahoo 제공)

        public GlobalIndexService()
        {
            _ = YahooLoopAsync(_cts.Token);     // 해외/선물 60초
            _ = NaverKrLoopAsync(_cts.Token);   // 국내 실시간 12초
        }

        private void Upsert(IndexRow row) { lock (_lock) _bySym[row.Symbol] = row; }

        private void Publish()
        {
            var snap = new List<IndexRow>(_catalog.Length);
            lock (_lock) { foreach (var c in _catalog) if (_bySym.TryGetValue(c.sym, out var r)) snap.Add(r); }
            if (snap.Count > 0) { Indices = snap; OnRefreshed?.Invoke(); }
        }

        // ── 해외/선물: Yahoo v8 (국내 ^KS11/^KQ11 은 네이버 전담이라 스킵 — 지연값 덮어써 깜빡임 방지) ──
        private async Task YahooLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                foreach (var (sym, name, flag, region, currency) in _catalog)
                {
                    // 국내(코스피/코스닥)는 네이버 실시간 전담 — 단 네이버가 최근 45초 내 성공 중일 때만 스킵(지연값 깜빡임 방지).
                    //   네이버 미성공/장애(엔드포인트 오류 등)면 Yahoo 가 폴백 제공(국내 지수가 사라지지 않도록·회귀 차단).
                    if (sym is "^KS11" or "^KQ11"
                        && Environment.TickCount64 - System.Threading.Interlocked.Read(ref _krLastNaverMs) < 45_000) continue;
                    var url = $"https://query1.finance.yahoo.com/v8/finance/chart/{Uri.EscapeDataString(sym)}?interval=1m&range=1d";
                    try
                    {
                        var json = await HttpPool.Shared.GetStringAsync(url, ct);
                        using var doc = System.Text.Json.JsonDocument.Parse(json);
                        var meta = doc.RootElement.GetProperty("chart").GetProperty("result")[0].GetProperty("meta");
                        var last = Safe.JsonDec(meta, "regularMarketPrice");
                        var prev = Safe.JsonDec(meta, "previousClose");
                        if (last > 0) Upsert(new IndexRow(sym, name, flag, region, last, prev > 0 ? prev : last, currency));
                    }
                    catch (Exception ex) { Log.Warn($"[GlobalIndex] {sym} 실패: {ex.Message}"); }
                    try { await Task.Delay(100, ct); } catch { return; }
                }
                Publish();
                try { await Task.Delay(60_000, ct); } catch { return; }
            }
        }

        // ── ★국내: 네이버 realtime 지수(KRX 실시간·12초) — Yahoo ^KS11 지연(수십 pt) 교정 ──
        private async Task NaverKrLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                foreach (var (sym, code, name) in _krCatalog)
                {
                    var url = $"https://polling.finance.naver.com/api/realtime/domestic/index/{code}";
                    try
                    {
                        var json = await HttpPool.Shared.GetStringAsync(url, ct);
                        using var doc = System.Text.Json.JsonDocument.Parse(json);
                        var data = doc.RootElement.GetProperty("datas")[0];
                        var last  = Safe.JsonDec(data, "closePrice");                            // 현재 지수(콤마 문자열도 파싱)
                        var delta = Math.Abs(Safe.JsonDec(data, "compareToPreviousClosePrice")); // 변동 크기(부호는 방향 코드로)
                        var change = NaverFinanceProvider.DirectionIsDown(data) ? -delta : delta;  // ★code(4/5=하락) 기준 — 부호 반전 교정
                        if (last > 0)
                        {
                            Upsert(new IndexRow(sym, name, "🇰🇷", "아시아", last, last - change, "KRW"));
                            System.Threading.Interlocked.Exchange(ref _krLastNaverMs, Environment.TickCount64);   // 네이버 성공 → Yahoo 국내 폴백 게이트 갱신
                        }
                    }
                    catch (Exception ex) { Log.Warn($"[NaverIndex] {code} 실패: {ex.Message}"); }
                    try { await Task.Delay(100, ct); } catch { return; }
                }
                Publish();
                try { await Task.Delay(12_000, ct); } catch { return; }
            }
        }

        public void Dispose() { _cts.Cancel(); _cts.Dispose(); }
    }
}
