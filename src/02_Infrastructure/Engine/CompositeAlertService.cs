// ============================================================================
// CompositeAlertService - 복합 알림 백그라운드 평가 서비스 (v3.2.3)
//  · 앱 시작 시 Start()로 1회 기동 → 관리창 개폐와 무관하게 상시 평가.
//  · 규칙의 단일 소스(Rules). 관리창은 이 서비스의 Rules를 편집/저장.
// ============================================================================
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace MS2E.Infrastructure
{
    public class CompositeAlertService
    {
        public static CompositeAlertService Instance { get; } = new();

        private LocalDatabase? _db;
        private IQuoteProvider? _provider;
        private readonly UpbitProvider _upbit = new();
        private System.Windows.Forms.Timer? _timer;
        private bool _busy;

        // 코인 일봉 캐시 (API 한도 방어) — 30초 주기 평가가 같은 일봉을 반복 페치하던 비용 절감.
        // 티커(현재가/등락률)는 매번 신선하게, 캔들만 5분 TTL로 재사용.
        private readonly Dictionary<string, (List<decimal> closes, List<decimal> vols, DateTime at)> _coinCandleCache
            = new(StringComparer.OrdinalIgnoreCase);
        private static readonly TimeSpan CandleTtl = TimeSpan.FromMinutes(5);

        public List<CompositeRule> Rules { get; private set; } = new();
        public DateTime LastRun { get; private set; }
        public int LastFiredCount { get; private set; }
        public int IntervalSec { get; private set; } = 30;
        public event Action? Evaluated;   // 평가/저장 후 UI 갱신용

        /// <summary>앱 시작 시 1회 호출. 저장된 주기(기본 30초)로 평가.</summary>
        public void Start(LocalDatabase db, IQuoteProvider? provider, int intervalMs = 0)
        {
            _db = db; _provider = provider;
            Rules = CompositeAlertEngine.Load(db);
            IntervalSec = LoadIntervalSec(db);
            if (_timer != null) return;
            _timer = new System.Windows.Forms.Timer { Interval = IntervalSec * 1000 };
            _timer.Tick += async (s, e) => await EvaluateNowAsync();
            _timer.Start();
        }

        public void SetInterval(int sec)
        {
            IntervalSec = Math.Clamp(sec, 10, 600);
            try { _db?.SetSetting("CompositeAlerts.IntervalSec", IntervalSec.ToString()); } catch { }
            if (_timer != null) _timer.Interval = IntervalSec * 1000;
        }

        private static int LoadIntervalSec(LocalDatabase db)
        {
            try { if (int.TryParse(db.GetSetting("CompositeAlerts.IntervalSec"), out var v)) return Math.Clamp(v, 10, 600); }
            catch { }
            return 30;
        }

        public void Stop() { _timer?.Stop(); _timer?.Dispose(); _timer = null; }

        public void Reload() { if (_db != null) Rules = CompositeAlertEngine.Load(_db); }
        public void Persist() { if (_db != null) CompositeAlertEngine.Save(_db, Rules); Evaluated?.Invoke(); }

        public async Task EvaluateNowAsync()
        {
            if (_db == null || _busy) return;
            _busy = true;
            int fired = 0;
            try
            {
                foreach (var grp in Rules.Where(r => r.Enabled).GroupBy(r => (r.AssetKind, r.Target)))
                {
                    try
                    {
                        var ctx = grp.Key.AssetKind == "stock"
                            ? await BuildStockCtxAsync(grp.Key.Target)
                            : await BuildCoinCtxAsync(grp.Key.Target);
                        if (ctx == null) continue;
                        foreach (var rule in grp)
                            if (CompositeAlertEngine.TryFire(rule, ctx)) fired++;
                    }
                    catch { }
                }
                if (fired > 0) CompositeAlertEngine.Save(_db, Rules);
                LastRun = DateTime.Now; LastFiredCount = fired;
            }
            finally { _busy = false; }
            Evaluated?.Invoke();
        }

        private async Task<MarketCtx?> BuildCoinCtxAsync(string market)
        {
            var ts = await _upbit.GetTickersAsync(new[] { market });   // 현재가/등락률은 매번 신선하게
            var t = ts.FirstOrDefault();
            if (t == null) return null;
            var (closes, vols) = await GetCoinCandlesCachedAsync(market);
            return new MarketCtx
            {
                Market = market, Price = t.Price, ChangePct = t.ChangeRate * 100m,
                Closes = closes, Volumes = vols
            };
        }

        // 일봉 5분 TTL 캐시 — 만료 시에만 재페치.
        private async Task<(List<decimal> closes, List<decimal> vols)> GetCoinCandlesCachedAsync(string market)
        {
            if (_coinCandleCache.TryGetValue(market, out var c) && DateTime.Now - c.at < CandleTtl)
                return (c.closes, c.vols);
            var candles = await _upbit.GetCandlesAsync(market, 30);
            var closes = candles.Select(x => x.Close).ToList();
            var vols = candles.Select(x => (decimal)x.Volume).ToList();
            if (closes.Count > 0) _coinCandleCache[market] = (closes, vols, DateTime.Now);
            return (closes, vols);
        }

        private async Task<MarketCtx?> BuildStockCtxAsync(string symbol)
        {
            if (_provider == null) return null;
            var market = SampleData.All().FirstOrDefault(x => x.Item1 == symbol).Item3;
            var q = await _provider.GetQuoteAsync(symbol, market);
            if (q == null) return null;
            var candles = await _provider.GetHistoryAsync(symbol, market, DateTime.Now.AddMonths(-3), DateTime.Now);
            return new MarketCtx
            {
                Market = symbol, Price = q.Price, ChangePct = q.ChangeRate,
                Closes = candles.Select(c => c.Close).ToList(),
                Volumes = candles.Select(c => (decimal)c.Volume).ToList()
            };
        }
    }
}
