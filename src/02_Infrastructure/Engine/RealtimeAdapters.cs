// ============================================================================
// RealtimeBrokerAdapter + WatchlistAdapter — Phase 94-O: RealtimePage 이주 호스트 어댑터
//   IRealtimeBrokerPort → IQuoteProvider(시세/이력) + ExtremeScanner/SampleData/AlertCenter(52주 스캔+알림 내재화).
//   IWatchlistPort → LocalDatabase/WatchlistStore(카테고리·종목·재정렬·시드) + 알림규칙 INSERT + 테마설정.
//   ★liveArmed:false: 조회/표시/워치리스트 관리 전용(실 발주 무접촉).
// ============================================================================
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MS2E.UI.Portable.Ports.Portfolio;   // PfMarket
using MS2E.UI.Portable.Ports.Realtime;

namespace MS2E.Infrastructure
{
    public sealed class RealtimeBrokerAdapter : IRealtimeBrokerPort
    {
        private readonly IQuoteProvider _prov;
        public RealtimeBrokerAdapter(IQuoteProvider prov) => _prov = prov;

        public async Task<IReadOnlyList<IRealtimeQuoteView>> GetQuotesAsync(IEnumerable<string> symbols, PfMarket market)
            => await _prov.GetQuotesAsync(symbols, (Market)(int)market);   // List<Quote> → 공변

        public async Task<IReadOnlyList<ICandleView>> GetHistoryAsync(string symbol, PfMarket market, DateTime from, DateTime to)
            => await _prov.GetHistoryAsync(symbol, (Market)(int)market, from, to);   // List<Candle> → 공변

        public async Task<IReadOnlyList<IExtremeEventView>> Scan52Async(CancellationToken ct)
        {
            // 원본 룰: SampleData 전체 · 종가 기준 · 이벤트당 Warning 알림 발생(어댑터 내재화).
            var events = await ExtremeScanner.ScanAsync(_prov, SampleData.All(), ct);
            foreach (var ev in events)
                AlertCenter.Instance.RaisePrice(ev.Symbol,
                    $"{(ev.IsHigh ? "📈 52주 신고가" : "📉 52주 신저가")} · {ev.Name}",
                    $"현재 {ev.CurrentPrice:N0} · 기존 {ev.Extreme52:N0} (Δ {Formatter.Pct(ev.ChangePct)})",
                    AlertLevel.Warning);
            return events;   // List<ExtremeEvent> → 공변
        }
    }

    public sealed class WatchlistAdapter : IWatchlistPort
    {
        private readonly LocalDatabase _db;
        public WatchlistAdapter(LocalDatabase db) => _db = db;

        public IReadOnlyList<string> LoadCategories() => WatchlistStore.LoadCategories(_db);
        public bool IsBuiltIn(string category) => WatchlistStore.IsBuiltIn(category);

        public IReadOnlyList<WatchItem> LoadSymbols(string group)
            => WatchlistStore.LoadSymbols(_db, group).Select(t => new WatchItem(t.sym, t.name)).ToList();

        public IReadOnlyList<WatchItem> DefaultsFor(string group)
            => DefaultsForImpl(group).Select(t => new WatchItem(t.sym, t.name)).ToList();

        public void AddSymbol(string group, string code, string name, PfMarket market)
            => WatchlistStore.AddSymbol(_db, group, code, name, (Market)(int)market);

        public void RemoveSymbol(string group, string sym) => WatchlistStore.RemoveSymbol(_db, group, sym);
        public void Reorder(string group, IReadOnlyList<string> order) => WatchlistStore.Reorder(_db, group, order);
        public bool AddCategory(string name) => WatchlistStore.AddCategory(_db, name);
        public bool RemoveCategory(string category) => WatchlistStore.RemoveCategory(_db, category);

        public void AddAlertRule(string symbol, int condition, decimal target, int channel)
        {
            var cond = (AlertCondition)condition;   // 0=PriceAbove/1=PriceBelow/2=ChangeRateAbove/3=ChangeRateBelow
            var ch = channel switch { 1 => AlertChannel.Telegram, 2 => AlertChannel.All, _ => AlertChannel.Desktop };
            _db.Execute(@"INSERT INTO AlertRules(Symbol,Condition,TargetValue,IsActive,IsTriggered,CreatedAt,Channel)
                         VALUES(@s,@c,@t,1,0,@d,@ch)",
                new()
                {
                    ["@s"] = symbol.Trim().ToUpperInvariant(),
                    ["@c"] = (int)cond,
                    ["@t"] = (double)target,
                    ["@d"] = DateTime.Now.ToString("o"),
                    ["@ch"] = (int)ch
                });
        }

        public bool IsDarkTheme() => _db.GetSetting("UI.Theme") != "light";   // 기본 dark

        // 그룹별 큐레이션 종목(빈 그룹 폴백) — 원본 RealtimePage.DefaultsFor 이관(SampleData 격리).
        //  ※ '피지컬'을 'AI'보다 먼저 검사(둘 다 "AI" 포함하므로 순서 중요).
        private static (string sym, string name)[] DefaultsForImpl(string group)
        {
            if (group.Contains("반도체")) return new[] {
                ("005930","삼성전자"),("000660","SK하이닉스"),("042700","한미반도체"),("NVDA","NVIDIA"),("AMD","AMD"),
                ("TSM","TSMC"),("INTC","Intel"),("AVGO","Broadcom"),("MU","Micron"),("ASML","ASML") };
            if (group.Contains("2차전지")) return new[] {
                ("373220","LG에너지솔루션"),("006400","삼성SDI"),("051910","LG화학"),("247540","에코프로비엠"),
                ("086520","에코프로"),("066970","엘앤에프"),("TSLA","Tesla"),("ALB","Albemarle") };
            if (group.Contains("피지컬")) return new[] {           // 피지컬 AI(로보틱스/하드웨어) — AI 검사보다 먼저
                ("454910","두산로보틱스"),("277810","레인보우로보틱스"),("108490","로보티즈"),
                ("TSLA","Tesla"),("NVDA","NVIDIA"),("ISRG","Intuitive Surgical"),("ABB","ABB") };
            if (group.Contains("AI")) return new[] {
                ("NVDA","NVIDIA"),("MSFT","Microsoft"),("GOOGL","Alphabet"),("META","Meta"),("PLTR","Palantir"),
                ("AMD","AMD"),("035420","NAVER"),("035720","카카오") };
            if (group.Contains("미국")) return SampleData.USTop.Select(x => (x.sym, x.name)).ToArray();
            return SampleData.KoreanTop.Select(x => (x.sym, x.name)).ToArray();
        }
    }
}
