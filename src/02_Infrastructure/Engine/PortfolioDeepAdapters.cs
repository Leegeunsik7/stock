// ============================================================================
// PortfolioDeepAdapters — Phase 94-J: PortfolioPage 6중 심층 결합 호스트 어댑터 (DIP 구현)
//   순수 UI 의 5대 Port + 프리젠터(IPortfolioBoardPort) + 심볼검색을 엔진 구상(LocalDatabase·IQuoteProvider·
//   UpbitProvider·FxConverter·AsOfContext·IJSRuntime + 정적 엔진 PortfolioService/Breakdown/Sort/AsOf/Tax/Corr/Fee)에 결속.
//   ★데이터+계산은 프리젠터가 흡수(페이지는 얇은 렌더러) · 도메인/드래프트 매핑 · liveArmed:false(조회/계산/영속 전용).
// ============================================================================
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.JSInterop;
using MS2E.UI.Portable.Ports.Portfolio;
using StockManager.Blazor;

namespace MS2E.Infrastructure
{
    // ── 투영 DTO(RCL 뷰 구현) ────────────────────────────────────────────────
    internal sealed record TxRowDto(long Id, DateTime At, string Sym, PfTxType Type, decimal Qty, decimal Price, decimal Comm, decimal Tax, string Broker) : ITxRowView;
    internal sealed record CoinTickDto(string Market, decimal Price, decimal ChangeRate) : ICoinTickView;
    internal sealed record TaxRowDto(string Symbol, string Name, decimal Quantity, decimal AvgPrice, decimal CurrentPrice, PfCurrency Currency, decimal UnrealizedGain, decimal TaxIfSoldNow, decimal NetProceeds, string Recommendation) : ITaxRowView;
    internal sealed record DividendRowDto(DateTime ExDate, string Symbol, string Name, decimal DividendPerShare, PfCurrency Currency, DateTime PayDate, decimal YieldPct, decimal HeldQuantity, decimal ExpectedDividend) : IDividendView;

    // ── ① 트랜잭션 CRUD ─────────────────────────────────────────────────────
    public sealed class TransactionCrudAdapter : ITransactionCrudPort
    {
        private readonly LocalDatabase _db;
        public TransactionCrudAdapter(LocalDatabase db) => _db = db;

        public void Add(TxDraft d)
        {
            var tx = new Transaction
            {
                Symbol = (d.Symbol ?? "").Trim(),
                Type = (TransactionType)(int)d.Type,
                Quantity = d.Qty, Price = d.Price, Commission = d.Commission, Tax = d.Tax,
                ExecutedAt = d.ExecutedAt, BrokerAccount = d.Broker, Memo = d.Memo
            };
            _db.AddTransaction(tx);
            PortfolioService.RecomputeAverage(_db, tx.Symbol, d.Name,
                d.Picked ? (Market?)(Market)(int)d.Market : null,
                d.Picked ? (Currency?)(Currency)(int)d.Currency : null);
        }

        public void Delete(long id, string symbol)
        {
            _db.DeleteTransaction(id);
            PortfolioService.RecomputeAverage(_db, symbol);
        }

        public IReadOnlyList<ITxRowView> Recent(int limit)
        {
            try
            {
                var dt = _db.Query($"SELECT * FROM Transactions ORDER BY ExecutedAt DESC LIMIT {Math.Clamp(limit, 1, 100000)}");
                return dt.Rows.Cast<DataRow>().Select(r => (ITxRowView)new TxRowDto(
                    Safe.Long(r["Id"]), Safe.DT(r["ExecutedAt"]), Safe.Str(r["Symbol"]),
                    (PfTxType)Safe.Int(r["Type"]), Safe.Dec(r["Quantity"]), Safe.Dec(r["Price"]),
                    Safe.Dec(r["Commission"]), Safe.Dec(r["Tax"]), Safe.Str(r["BrokerAccount"]))).ToList();
            }
            catch { return Array.Empty<ITxRowView>(); }
        }

        public (bool holdingExists, decimal holdingQty, int txCount, string storedRaw) Diagnose(string symbol)
        {
            int txCnt = 0; decimal hQty = 0; bool hExists = false; string storedQ = "?";
            try
            {
                var tc = _db.Query("SELECT COUNT(*) AS c FROM Transactions WHERE Symbol=@s", new() { ["@s"] = symbol });
                txCnt = tc.Rows.Count > 0 ? Safe.Int(tc.Rows[0]["c"]) : 0;
                var sd = _db.Query("SELECT Quantity AS q, typeof(Quantity) AS t FROM Transactions WHERE Symbol=@s ORDER BY Id DESC LIMIT 1", new() { ["@s"] = symbol });
                if (sd.Rows.Count > 0) storedQ = $"{Safe.Str(sd.Rows[0]["q"])}/{Safe.Str(sd.Rows[0]["t"])}";
                var hd = _db.Query("SELECT Quantity FROM Holdings WHERE Symbol=@s", new() { ["@s"] = symbol });
                hExists = hd.Rows.Count > 0;
                if (hExists) hQty = Safe.Dec(hd.Rows[0]["Quantity"]);
            }
            catch { }
            return (hExists, hQty, txCnt, storedQ);
        }
    }

    // ── ② 실시간 시세 스트림 ────────────────────────────────────────────────
    public sealed class QuoteStreamAdapter : IQuoteStreamPort
    {
        private readonly IQuoteProvider _provider;
        private readonly UpbitProvider _upbit;
        public QuoteStreamAdapter(IQuoteProvider provider, UpbitProvider upbit) { _provider = provider; _upbit = upbit; }

        public async Task<IQuoteTickView?> GetStockQuoteAsync(string symbol, PfMarket market)
        {
            try { return await _provider.GetQuoteAsync(symbol, (Market)(int)market); }
            catch { return null; }   // fail-closed(직전가 유지)
        }

        public async Task<IReadOnlyList<ICoinTickView>> GetCoinTicksAsync(IEnumerable<string> markets)
        {
            try
            {
                var ticks = await _upbit.GetTickersAsync(markets);
                return ticks.Where(t => t.Price > 0)
                    .Select(t => (ICoinTickView)new CoinTickDto(t.Market, t.Price, t.ChangeRate)).ToList();
            }
            catch { return Array.Empty<ICoinTickView>(); }
        }

        public async Task<double[]> GetSparkAsync(string symbol, PfMarket market, int days)
        {
            try
            {
                var cs = await _provider.GetHistoryAsync(symbol, (Market)(int)market, DateTime.Now.AddDays(-days), DateTime.Now);
                return cs.Count >= 2 ? cs.TakeLast(30).Select(c => (double)c.Close).ToArray() : Array.Empty<double>();
            }
            catch { return Array.Empty<double>(); }
        }
    }

    // ── ③ FX 실시간 환율 ────────────────────────────────────────────────────
    public sealed class FxRateAdapter : IFxRatePort
    {
        private readonly FxConverter _fx;
        public FxRateAdapter(FxConverter fx) => _fx = fx;
        public Task<decimal> GetUsdKrwAsync() => _fx.GetUsdKrwAsync();
    }

    // ── ④ 시점 여행(AsOf) ───────────────────────────────────────────────────
    public sealed class TimeTravelAsOfAdapter : ITimeTravelAsOfPort
    {
        private readonly AsOfContext _ctx;
        private readonly LocalDatabase _db;
        public TimeTravelAsOfAdapter(AsOfContext ctx, LocalDatabase db)
        {
            _ctx = ctx; _db = db;
            _ctx.OnChange += () => OnChange?.Invoke();   // 엔진 컨텍스트 변경 → 포트 이벤트 재발화
        }
        public bool IsLive => _ctx.IsLive;
        public DateTime? AsOf => _ctx.AsOf;
        public event Action? OnChange;
        public void EnterAsOf(DateTime date) => _ctx.Set(date);   // Phase 94-K
        public void ReturnLive() => _ctx.Live();                  // Phase 94-K

        public IReadOnlyList<IAsOfHoldingView> Reconstruct()
        {
            if (_ctx.IsLive || _ctx.AsOf is null) return Array.Empty<IAsOfHoldingView>();
            try
            {
                var txs = _db.Query("SELECT * FROM Transactions").Rows.Cast<DataRow>()
                    .Select(r => new AsOfTx(Safe.Str(r["Symbol"]), (TransactionType)Safe.Int(r["Type"]),
                        Safe.Dec(r["Quantity"]), Safe.Dec(r["Price"]), Safe.DT(r["ExecutedAt"]))).ToList();
                return AsOfPortfolio.Reconstruct(txs, _ctx.AsOf!.Value);   // List<AsOfHolding> → IReadOnlyList<IAsOfHoldingView>(공변)
            }
            catch { return Array.Empty<IAsOfHoldingView>(); }
        }
    }

    // ── ⑤ JS 대화상자 ───────────────────────────────────────────────────────
    public sealed class JsDialogAdapter : IJsDialogPort
    {
        private readonly IJSRuntime _js;
        public JsDialogAdapter(IJSRuntime js) => _js = js;
        public async Task<bool> ConfirmAsync(string message)
        {
            try { return await _js.InvokeAsync<bool>("confirm", message); }
            catch { return true; }   // JS 미가용 환경 폴백(원본 룰)
        }
    }

    // ── 심볼 검색(SymbolPicker 이주) ────────────────────────────────────────
    public sealed class SymbolSearchAdapter : ISymbolSearchPort
    {
        private readonly UpbitProvider _upbit;
        private readonly ISymbolSearch _search;
        private static List<SymbolEntry>? _liveCrypto;
        private static bool _loadTried;

        public SymbolSearchAdapter(UpbitProvider upbit, ISymbolSearch search) { _upbit = upbit; _search = search; }

        public async Task WarmupAsync()
        {
            if (_loadTried) return;
            _loadTried = true;
            try
            {
                var markets = await _upbit.GetMarketsAsync();
                if (markets.Count > 0)
                    _liveCrypto = markets.Select(m => new SymbolEntry(m.Market,
                        string.IsNullOrWhiteSpace(m.KoreanName) ? m.Market : m.KoreanName,
                        Market.CRYPTO, Currency.KRW, true)).ToList();
            }
            catch { /* 라이브 실패 → 내장 카탈로그 폴백 */ }
        }

        public IReadOnlyList<ISymbolEntryView> SearchLocal(string query, int limit)
            => SymbolCatalog.Search(query ?? "", limit, _liveCrypto).Cast<ISymbolEntryView>().ToList();

        public async Task<IReadOnlyList<ISymbolEntryView>> SearchMergedAsync(string query, int limit)
        {
            var local = SymbolCatalog.Search(query ?? "", 9, _liveCrypto);
            List<SymbolEntry> live;
            try { live = await _search.SearchAsync(query ?? ""); }
            catch { live = new(); }
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var merged = new List<ISymbolEntryView>(live.Count + local.Count);
            foreach (var s in live)  if (s.HasValue && seen.Add(s.Code)) merged.Add(s);
            foreach (var s in local) if (s.HasValue && seen.Add(s.Code)) merged.Add(s);
            return merged.Count > limit ? merged.GetRange(0, limit) : merged;
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  프리젠터 — 데이터+계산 흡수(페이지 @code 이관). stateful(스코프 수명).
    // ══════════════════════════════════════════════════════════════════════════
    public sealed class PortfolioBoardAdapter : IPortfolioBoardPort
    {
        private readonly LocalDatabase _db;
        private readonly IQuoteStreamPort _quotes;
        private readonly IFxRatePort _fx;
        private readonly IQuoteProvider _provider;   // 상관분석 CollectReturnsAsync 전용

        private List<Holding> _holdings = new();
        private List<Holding> _deleted = new();
        private List<CategoryAgg> _cats = new();
        private readonly Dictionary<string, decimal> _prevPrice = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, decimal> _dayPct = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _session = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, double[]> _spark = new(StringComparer.OrdinalIgnoreCase);
        private List<string> _customOrder = new();
        private List<DividendEvent> _divs = new();
        private readonly SemaphoreSlim _refreshLock = new(1, 1);
        private static readonly string[] _brokers = { "키움증권", "한국투자증권", "삼성증권", "토스증권", "NH투자증권", "미래에셋" };

        public PortfolioBoardAdapter(LocalDatabase db, IQuoteStreamPort quotes, IFxRatePort fx, IQuoteProvider provider)
        { _db = db; _quotes = quotes; _fx = fx; _provider = provider; }

        public decimal TotalValue { get; private set; }
        public decimal TotalPnl { get; private set; }
        public decimal TotalRet { get; private set; }
        public decimal TodayPnl { get; private set; }
        public decimal TodayPct { get; private set; }
        public decimal UsdKrw { get; private set; } = 1380m;
        public string Timestamp { get; private set; } = "";
        public int HoldingCount => _holdings.Count;
        public IReadOnlyList<string> Brokers => _brokers;

        public IReadOnlyList<IHoldingView> Holdings => _holdings;
        public IReadOnlyList<IHoldingView> Deleted => _deleted;
        public IReadOnlyList<ICategoryView> Categories => _cats.Cast<ICategoryView>().ToList();

        public PfAssetClass Classify(PfMarket market) => (PfAssetClass)(int)PortfolioBreakdown.Classify((Market)(int)market);

        public IReadOnlyList<IHoldingView> ViewHoldings(string filter, string sortKey, bool sortDesc)
        {
            IEnumerable<Holding> src = filter == "all" ? _holdings
                : _holdings.Where(h => CatKey(PortfolioBreakdown.Classify(h.Market)) == filter);
            return HoldingSort.Sort(src, sortKey, sortDesc, _dayPct, _customOrder, UsdKrw).ToList();
        }
        public int FilterCount(string filter)
            => filter == "all" ? _holdings.Count : _holdings.Count(h => CatKey(PortfolioBreakdown.Classify(h.Market)) == filter);
        private static string CatKey(AssetClass c) => c switch
        { AssetClass.DomesticStock => "dom", AssetClass.ForeignStock => "for", AssetClass.Crypto => "crypto", _ => "all" };

        public bool TryGetDayPct(string symbol, out decimal pct) => _dayPct.TryGetValue(symbol, out pct);
        public string SessionOf(string symbol) => _session.TryGetValue(symbol, out var s) ? s : "";
        public bool TryGetPrevPrice(string symbol, out decimal prev) => _prevPrice.TryGetValue(symbol, out prev);
        public double[] Spark(string symbol) => _spark.TryGetValue(symbol, out var d) ? d : Array.Empty<double>();

        public HoldingDraft ToDraft(IHoldingView h) => new()
        {
            Id = h.Id, Symbol = h.Symbol, Name = h.Name, Market = h.Market, Currency = h.Currency,
            Quantity = h.Quantity, AvgPrice = h.AvgPrice, Broker = h.BrokerAccount, Commission = h.Commission, Tax = h.Tax
        };

        public (decimal commission, decimal tax) EstimateFees(string broker, PfMarket market, PfTxType type, decimal qty, decimal price)
            => BrokerFeeSchedule.Estimate(broker, (Market)(int)market, (TransactionType)(int)type, qty, price);

        // ── 로드/갱신(원본 LoadAsync/UpdatePrices 이관) ──
        public async Task LoadAsync(bool persist = true)
        {
            await _refreshLock.WaitAsync();
            try
            {
                try { UsdKrw = await _fx.GetUsdKrwAsync(); } catch { /* 캐시 유지 */ }
                if (persist)
                {
                    try { _holdings = _db.GetAllHoldings(); } catch { _holdings = new(); }
                    try { _deleted = _db.GetDeletedHoldings(); } catch { _deleted = new(); }
                }
                await UpdatePricesAsync(persist);
                TotalValue = _holdings.Sum(h => h.CurrentPrice * h.Quantity * (h.Currency == Currency.USD ? UsdKrw : 1m));
                var cost = _holdings.Sum(h => h.AvgPrice * h.Quantity * (h.Currency == Currency.USD ? UsdKrw : 1m));
                TotalPnl = TotalValue - cost;
                TotalRet = cost > 0 ? TotalPnl / cost * 100m : 0m;
                decimal todayGain = 0m, prevBase = 0m;
                foreach (var h in _holdings)
                {
                    decimal fxr2 = h.Currency == Currency.USD ? UsdKrw : 1m;
                    decimal cur = h.CurrentPrice * h.Quantity * fxr2;
                    decimal d = _dayPct.TryGetValue(h.Symbol, out var dv) ? dv : 0m;
                    decimal denom = 1m + d / 100m;
                    decimal prev = denom != 0m ? cur / denom : cur;
                    todayGain += cur - prev; prevBase += prev;
                }
                TodayPnl = todayGain;
                TodayPct = prevBase > 0 ? todayGain / prevBase * 100m : 0m;
                _cats = PortfolioBreakdown.Compute(_holdings, UsdKrw);
                Timestamp = DateTime.Now.ToString("HH:mm:ss");
            }
            finally { _refreshLock.Release(); }
        }

        private async Task UpdatePricesAsync(bool persist)
        {
            void Apply(Holding h, decimal p)
            {
                _prevPrice[h.Symbol] = h.CurrentPrice;
                h.CurrentPrice = p;
                if (persist) { try { _db.SaveHolding(h); } catch { } }
            }

            var coins = _holdings.Where(h => h.Market == Market.CRYPTO).ToList();
            if (coins.Count > 0)
            {
                var ticks = await _quotes.GetCoinTicksAsync(coins.Select(h => h.Symbol));
                var map = ticks.ToDictionary(t => t.Market, t => t.Price, StringComparer.OrdinalIgnoreCase);
                var chg = ticks.ToDictionary(t => t.Market, t => t.ChangeRate * 100m, StringComparer.OrdinalIgnoreCase);
                foreach (var h in coins)
                    if (map.TryGetValue(h.Symbol, out var p))
                    { Apply(h, p); _dayPct[h.Symbol] = chg.TryGetValue(h.Symbol, out var c) ? c : 0m; _session[h.Symbol] = ""; }
            }

            var stocks = _holdings.Where(h => h.Market != Market.CRYPTO).ToList();
            if (stocks.Count > 0)
            {
                using var gate = new SemaphoreSlim(6);
                var tasks = stocks.Select(async h =>
                {
                    await gate.WaitAsync();
                    try { return (h, q: await _quotes.GetStockQuoteAsync(h.Symbol, (PfMarket)(int)h.Market)); }
                    finally { gate.Release(); }
                }).ToList();
                foreach (var (h, q) in await Task.WhenAll(tasks))
                    if (q != null && q.Price > 0)
                    { Apply(h, q.Price); _dayPct[h.Symbol] = q.ChangeRate; _session[h.Symbol] = q.Session ?? ""; }
            }
        }

        public async Task LoadSparksAsync(Func<Task> onProgress)
        {
            foreach (var h in _holdings.ToList())
            {
                var d = await _quotes.GetSparkAsync(h.Symbol, (PfMarket)(int)h.Market, 50);
                if (d.Length >= 2) { _spark[h.Symbol] = d; await onProgress(); }
                try { await Task.Delay(60); } catch { }
            }
        }

        // ── 보유 CRUD ──
        public async Task SaveHoldingAsync(HoldingDraft d, bool edited)
        {
            var h = new Holding
            {
                Id = d.Id,
                Symbol = (d.Symbol ?? "").Trim(),
                Name = string.IsNullOrWhiteSpace(d.Name) ? (d.Symbol ?? "").Trim() : d.Name.Trim(),
                Market = (Market)(int)d.Market, Currency = (Currency)(int)d.Currency,
                Quantity = d.Quantity, AvgPrice = d.AvgPrice, CurrentPrice = d.AvgPrice, FirstBuyDate = DateTime.Now,
                BrokerAccount = (d.Broker ?? "").Trim(), Commission = d.Commission, Tax = d.Tax
            };
            if (edited) _db.UpdateHoldingById(h); else _db.SaveHolding(h);
            await LoadAsync();
        }
        public async Task DeleteHoldingAsync(string symbol, string brokerAccount) { _db.DeleteHolding(symbol, brokerAccount); await LoadAsync(); }
        public async Task RestoreHoldingAsync(string symbol, string brokerAccount) { _db.RestoreHolding(symbol, brokerAccount); await LoadAsync(); }
        public async Task HardDeleteHoldingAsync(string symbol, string brokerAccount) { _db.HardDeleteHolding(symbol, brokerAccount); await LoadAsync(); }

        // ── 직접설정 순서 ──
        public void LoadCustomOrder()
        {
            try
            {
                var s = _db.GetSetting("Portfolio.CustomOrder");
                _customOrder = string.IsNullOrWhiteSpace(s) ? new()
                    : s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            }
            catch { _customOrder = new(); }
        }
        public void MoveCustom(string symbol, int dir, IReadOnlyList<string> viewSymbols)
        {
            foreach (var h in _holdings) if (!_customOrder.Contains(h.Symbol)) _customOrder.Add(h.Symbol);
            if (HoldingSort.MoveInView(_customOrder, viewSymbols, symbol, dir))
                try { _db.SetSetting("Portfolio.CustomOrder", string.Join(",", _customOrder)); } catch { }
        }

        // ── 세금/상관/배당 ──
        public IReadOnlyList<ITaxRowView> TaxRows()
        {
            var rows = new List<ITaxRowView>();
            decimal runningRealized = 0;
            foreach (var h in _holdings.OrderByDescending(x => x.UnrealizedPnL))
            {
                var sim = TaxSimulator.SimulateSell(h, UsdKrw, runningRealized);
                rows.Add(new TaxRowDto(h.Symbol, h.Name, h.Quantity, h.AvgPrice, h.CurrentPrice, (PfCurrency)(int)h.Currency,
                    sim.UnrealizedGain, sim.TaxIfSoldNow, sim.NetProceeds, sim.Recommendation));
                if (h.Currency == Currency.USD) runningRealized += sim.UnrealizedGain * UsdKrw;
            }
            return rows;
        }

        public async Task<PfCorrResult> CalcCorrelationAsync()
        {
            if (_holdings.Count < 2) return new PfCorrResult(Array.Empty<string>(), null, "최소 2종목 이상 보유해야 합니다.");
            try
            {
                var syms = _holdings.Select(h => h.Symbol).ToList();
                var returns = await CorrelationAnalyzer.CollectReturnsAsync(_provider,
                    _holdings.Select(h => (h.Symbol, h.Market)), 90);
                return new PfCorrResult(syms, CorrelationAnalyzer.BuildMatrix(syms, returns), "");
            }
            catch (Exception ex) { return new PfCorrResult(Array.Empty<string>(), null, "계산 실패: " + ex.Message); }
        }

        public async Task LoadDividendsAsync()
        {
            try { _divs = await Task.Run(() => _db.GetUpcomingDividends(60)); } catch { _divs = new(); }
        }
        public IReadOnlyList<IDividendView> Dividends =>
            _divs.OrderBy(x => x.ExDate).Select(d =>
            {
                var qty = _holdings.FirstOrDefault(h => h.Symbol == d.Symbol)?.Quantity ?? 0m;
                return (IDividendView)new DividendRowDto(d.ExDate, d.Symbol, d.Name, d.DividendPerShare,
                    (PfCurrency)(int)d.Currency, d.PayDate, d.YieldPct, qty, d.DividendPerShare * qty);
            }).ToList();
    }
}
