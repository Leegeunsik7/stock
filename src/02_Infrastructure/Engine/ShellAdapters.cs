// ============================================================================
// ShellAdapters — Phase 94-K: Main Shell & Dashboard 위젯 Port 호스트 어댑터 (DIP 구현)
//   순수 UI(NavMenu·ToastHost·SymbolBar·CrossLinks·IndexTickerBar·MarketTicker)를 엔진 구상(SymbolContext·
//   ToastService·LocalDatabase·CommandPaletteController·GlobalIndexService·IQuoteProvider)에 결속.
//   ★liveArmed:false / fail-closed: 조회·네비·토스트 전용(발주·소켓 무접촉).
// ============================================================================
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MS2E.UI.Portable.Ports.Portfolio;   // PfMarket
using MS2E.UI.Portable.Ports.Shell;
using StockManager.Blazor;                // ToastService, ToastMessage, CommandPaletteController

namespace MS2E.Infrastructure
{
    internal sealed record TickerRowDto(string Name, string Px, string Rate, bool Up) : ITickerRowView;

    // ── 심볼 컨텍스트(SymbolBar·CrossLinks) ─────────────────────────────────────
    public sealed class SymbolContextAdapter : ISymbolContextPort
    {
        private readonly SymbolContext _ctx;
        public SymbolContextAdapter(SymbolContext ctx)
        {
            _ctx = ctx;
            _ctx.OnChange += () => OnChange?.Invoke();
        }
        public bool HasSymbol => _ctx.HasSymbol;
        public string Symbol => _ctx.Symbol;
        public PfMarket Market => (PfMarket)(int)_ctx.Market;
        public event Action? OnChange;
        public void Set(string symbol, PfMarket market) => _ctx.Set(symbol, (Market)(int)market);
    }

    // ── 토스트(ToastHost) ───────────────────────────────────────────────────────
    public sealed class ToastAdapter : IToastPort
    {
        private readonly ToastService _svc;
        public ToastAdapter(ToastService svc)
        {
            _svc = svc;
            _svc.Changed += () => Changed?.Invoke();
        }
        public IReadOnlyList<IToastView> Active => _svc.Active;   // List<ToastMessage> → IReadOnlyList<IToastView>(공변)
        public event Action? Changed;
        public void Dismiss(Guid id) => _svc.Dismiss(id);

        public async Task InvokeButtonAsync(Guid toastId, int buttonIndex)
        {
            var t = _svc.Active.FirstOrDefault(x => x.Id == toastId);
            if (t is null || (uint)buttonIndex >= (uint)t.Buttons.Count) return;
            try { await t.Buttons[buttonIndex].OnClick(); }   // 엔진 Func 실행(RCL 로 노출 안 함)
            catch (Exception ex) { Log.Warn($"[Toast] 액션 실패: {ex.Message}"); }
        }
    }

    // ── 메인 셸 내비게이션(NavMenu) ────────────────────────────────────────────
    public sealed class MainShellNavigationAdapter : IMainShellNavigationPort
    {
        private readonly LocalDatabase _db;
        private readonly CommandPaletteController _palette;
        private readonly StockManager.Common.Account.SessionState _session;
        public MainShellNavigationAdapter(LocalDatabase db, CommandPaletteController palette, StockManager.Common.Account.SessionState session)
        { _db = db; _palette = palette; _session = session; }
        public bool IsAuthenticated => _session.IsAuthenticated;

        public int AlertUnreadCount
        {
            get { try { var dt = _db.Query("SELECT COUNT(*) AS c FROM Alerts WHERE IsRead=0"); return dt.Rows.Count > 0 ? Safe.Int(dt.Rows[0]["c"]) : 0; } catch { return 0; } }
        }
        public int FraudUnreadCount { get { try { return _db.GetUnreviewedFraudCount(); } catch { return 0; } } }
        public string AppVersion => SystemVersionProvider.Version;
        public string? GetSetting(string key) => _db.GetSetting(key);
        public void RequestOpenPalette() => _palette.RequestOpen();
    }

    // ── 대시보드 위젯(IndexTickerBar·MarketTicker) ──────────────────────────────
    internal sealed record WeeklyPointDto(string Label, decimal Value) : IWeeklyPointView;

    public sealed class DashboardWidgetAdapter : IDashboardWidgetPort
    {
        private readonly GlobalIndexService _idx;
        private readonly IQuoteProvider _provider;
        private readonly DashboardService _dash;

        // MarketTicker 고정 감시목록(엔진 측 소유 — RCL 로 유입 안 함).
        private static readonly (string sym, Market mkt)[] _watch =
        {
            ("005930", Market.KRX_KOSPI), ("000660", Market.KRX_KOSPI), ("035420", Market.KRX_KOSPI),
            ("AAPL", Market.US_NASDAQ), ("NVDA", Market.US_NASDAQ), ("TSLA", Market.US_NASDAQ), ("MSFT", Market.US_NASDAQ),
        };

        public DashboardWidgetAdapter(GlobalIndexService idx, IQuoteProvider provider, DashboardService dash)
        {
            _idx = idx; _provider = provider; _dash = dash;
            _idx.OnRefreshed += () => IndicesRefreshed?.Invoke();
        }

        // ── 대시보드 KPI(Dashboard·DashboardHome) ──
        public Task LoadAsync() => _dash.LoadAsync();
        public decimal Total => _dash.Total;
        public decimal Ret => _dash.Ret;
        public decimal Pnl => _dash.Pnl;
        public decimal CoinValue => _dash.CoinValue;
        public decimal AllocKrwStock => _dash.AllocKrwStock;
        public decimal AllocUsdStock => _dash.AllocUsdStock;
        public decimal AllocCoin => _dash.AllocCoin;
        public decimal PaperCash => _dash.PaperCash;
        public decimal PaperPosVal => _dash.PaperPosVal;
        public decimal PaperPnl => _dash.PaperPnl;
        public decimal PaperRet => _dash.PaperRet;
        public bool WeeklySample => _dash.WeeklySample;
        public string Theme { get => _dash.Theme; set => _dash.Theme = value; }
        public IReadOnlyList<MS2E.UI.Portable.Ports.ICoinHoldingView> Coins => _dash.Coins;   // List<CoinRow> 공변
        public IReadOnlyList<IWeeklyPointView> Weekly => _dash.Weekly.Select(w => (IWeeklyPointView)new WeeklyPointDto(w.Label, w.Value)).ToList();
        public IReadOnlyList<MS2E.UI.Portable.Ports.Strategy.ICoinSignalView> Signals => _dash.Signals;   // List<SignalRow> 공변
        public void OpenModule(string key) => ModuleBridge.Open(key);

        public IReadOnlyList<IIndexRowView> Indices => _idx.Indices;   // List<IndexRow> → IReadOnlyList<IIndexRowView>(공변)
        public event Action? IndicesRefreshed;

        public async Task<IReadOnlyList<ITickerRowView>> GetTickerRowsAsync(CancellationToken ct)
        {
            var rows = new List<ITickerRowView>();
            foreach (var grp in _watch.GroupBy(w => w.mkt))
            {
                try
                {
                    var qs = await _provider.GetQuotesAsync(grp.Select(x => x.sym), grp.Key, ct);
                    foreach (var (sym, _) in grp)
                    {
                        var q = qs.FirstOrDefault(x => string.Equals(x.Symbol, sym, StringComparison.OrdinalIgnoreCase));
                        if (q == null || q.Price <= 0) continue;
                        var rate = q.PreviousClose > 0 ? (q.Price - q.PreviousClose) / q.PreviousClose * 100m : 0m;
                        rows.Add(new TickerRowDto(
                            string.IsNullOrEmpty(q.Name) ? q.Symbol : q.Name,
                            Formatter.Money(q.Price, q.Currency),
                            Math.Abs(rate).ToString("0.00") + "%",
                            rate >= 0));
                    }
                }
                catch { /* 실패 시 빈 목록 — 컴포넌트가 직전 유지 */ }
            }
            return rows;
        }
    }
}
