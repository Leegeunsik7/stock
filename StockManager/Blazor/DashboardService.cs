// ============================================================================
// DashboardService - Blazor 대시보드용 데이터 서비스 (v3.1)
//  · 기존 엔진(LocalDatabase / UpbitProvider / FxConverter / PaperBroker)을 그대로 호출.
//  · 위젯 데이터를 한 번에 로드하여 컴포넌트에 제공.
// ============================================================================
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace StockManager.Blazor
{
    public record CoinRow(string Name, string Market, decimal Price, decimal ChangeRate, double[] Spark);
    public record ActItem(string Text, string When);
    public record SignalRow(string Side, string Name, string Reason, int Pct);

    public class DashboardService : IDisposable
    {
        private readonly IQuoteProvider _provider;
        private readonly LocalDatabase _db;
        private readonly UpbitProvider _upbit = new();
        private readonly FxConverter _fx = new();
        public PaperBroker Broker { get; }

        // 업비트 WebSocket 실시간가 오버레이 (추가형 — 폴링이 권위 있는 폴백). 백그라운드 스레드가
        // _wsPx(ConcurrentDictionary)만 쓰고, _coinPx 병합은 UI 스레드 LoadAsync에서 수행(스레드 안전).
        private readonly UpbitWebSocketClient _ws = new();
        private readonly ConcurrentDictionary<string, (decimal px, DateTime at)> _wsPx = new(StringComparer.OrdinalIgnoreCase);
        private string _wsKey = "";
        private bool _wsHooked;

        public DashboardService(IQuoteProvider provider, LocalDatabase db)
        {
            _provider = provider; _db = db;
            Broker = new PaperBroker(db);
        }

        public decimal UsdKrw { get; private set; } = 1380m;

        // 요약 타일
        public decimal Total { get; private set; }
        public decimal Pnl { get; private set; }
        public decimal Ret { get; private set; }
        public decimal StockValue { get; private set; }
        public decimal CoinValue { get; private set; }

        // 자산 구성 도넛 (KRW)
        public decimal AllocKrwStock { get; private set; }
        public decimal AllocUsdStock { get; private set; }
        public decimal AllocCoin { get; private set; }

        // 코인 위젯
        public List<CoinRow> Coins { get; private set; } = new();

        // 모의투자
        public decimal PaperCash { get; private set; }
        public decimal PaperPosVal { get; private set; }
        public decimal PaperPnl { get; private set; }
        public decimal PaperRet { get; private set; }

        // 주간 총자산 추이(일별 스냅샷 집계, 없으면 샘플)
        public List<(string Label, decimal Value)> Weekly { get; private set; } = new();
        public bool WeeklySample { get; private set; } = true;

        // 최근 활동(모의 주문 이력 기반)
        public List<ActItem> Activity { get; private set; } = new();

        // 임박 신호(코인 일봉 실시간 스캔)
        public List<SignalRow> Signals { get; private set; } = new();

        private readonly Dictionary<string, decimal> _coinPx = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, decimal> _stockPx = new(StringComparer.OrdinalIgnoreCase);

        // 일봉 캔들 캐시 (API 한도 방어) — 일봉은 분 단위로 거의 불변하므로 5분 TTL.
        // LoadAsync가 watch 코인마다 GetCandlesAsync를 반복 호출하던 비용을 ~30배 절감.
        private readonly Dictionary<string, List<decimal>> _candleCache = new(StringComparer.OrdinalIgnoreCase);
        private DateTime _candleCacheAt;
        private string _candleCacheKey = "";
        private static readonly TimeSpan CandleTtl = TimeSpan.FromMinutes(5);

        /// <summary>모의 포지션 평가용 현재가 (주식은 KRW 환산값 — 원본 PaperTrading 규약)</summary>
        public decimal PriceOf(PaperAsset a, string sym) =>
            a == PaperAsset.Coin ? (_coinPx.TryGetValue(sym, out var p) ? p : 0m)
                                  : (_stockPx.TryGetValue(sym, out var s) ? s : 0m);

        public async Task LoadAsync()
        {
            // 백그라운드 엔진/WinForms 창이 체결·변경한 모의 계좌를 반영 (Settings가 진실의 원천)
            Broker.Reload();
            try { UsdKrw = await _fx.GetUsdKrwAsync(); } catch { }

            // ----- 주식 -----
            var holdings = _db.GetAllHoldings();
            decimal krwStock = 0, usdStock = 0, stockPnl = 0;
            foreach (var h in holdings)
            {
                if (h.Quantity <= 0) continue;
                var fx = h.Currency == Currency.USD ? UsdKrw : 1m;
                var val = h.CurrentPrice * h.Quantity * fx;
                stockPnl += (h.CurrentPrice - h.AvgPrice) * h.Quantity * fx;
                if (h.Currency == Currency.USD) usdStock += val; else krwStock += val;
                _stockPx[h.Symbol] = h.CurrentPrice * fx;
            }

            // ----- 코인 보유 / 워치 -----
            var state = LoadCryptoState();
            var watch = state.Watch.Count > 0 ? state.Watch
                : new List<string> { "KRW-BTC", "KRW-ETH", "KRW-XRP", "KRW-SOL", "KRW-DOGE" };

            EnsureWs(watch);
            var tickMap = new Dictionary<string, CryptoTick>(StringComparer.OrdinalIgnoreCase);
            try { foreach (var t in await _upbit.GetTickersAsync(watch)) tickMap[t.Market] = t; } catch { }
            foreach (var kv in tickMap) _coinPx[kv.Key] = kv.Value.Price;
            // WS 실시간가가 더 신선(15초 이내)하면 우선 적용 — 폴링은 폴백으로 유지
            foreach (var m in watch)
                if (_wsPx.TryGetValue(m, out var w) && (DateTime.Now - w.at).TotalSeconds < 15)
                    _coinPx[m] = w.px;

            decimal coinValue = 0;
            foreach (var kv in state.Holdings)
                if (kv.Value.Qty > 0 && _coinPx.TryGetValue(kv.Key, out var px))
                    coinValue += kv.Value.Qty * px;

            // 코인 위젯 행 + 신호 스캔 — 일봉은 5분 캐시(GetWatchCandlesAsync)로 API 호출 절감
            var candleMap = await GetWatchCandlesAsync(watch);
            Coins = new List<CoinRow>();
            int idx = 0;
            foreach (var m in watch)
            {
                if (idx++ >= 4) break;   // 위젯에는 상위 4종만 표시
                tickMap.TryGetValue(m, out var t);
                candleMap.TryGetValue(m, out var closes);
                var spark = (closes != null && closes.Count >= 2)
                    ? closes.Skip(Math.Max(0, closes.Count - 8)).Select(c => (double)c).ToArray()
                    : Array.Empty<double>();
                decimal px = (_wsPx.TryGetValue(m, out var ws) && (DateTime.Now - ws.at).TotalSeconds < 15)
                    ? ws.px : (t?.Price ?? 0m);   // 위젯도 WS 실시간가 우선
                Coins.Add(new CoinRow(t?.KoreanName ?? m.Replace("KRW-", ""), m,
                    px, t?.ChangeRate ?? 0m, spark));
            }
            ScanSignals(watch, tickMap, candleMap);

            // ----- 합계 -----
            StockValue = krwStock + usdStock;
            CoinValue = coinValue;
            Total = StockValue + coinValue;
            Pnl = stockPnl; // 코인 평가손익은 보유 평단 필요 — 주식 손익 기준(코인 평단은 모의/원장 영역)
            var cost = Total - Pnl;
            Ret = cost > 0 ? Pnl / cost * 100m : 0m;
            AllocKrwStock = krwStock; AllocUsdStock = usdStock; AllocCoin = coinValue;

            // ----- 모의 포지션 시세 보강 (보유/감시 목록에 없는 종목 — 원본 PaperTrading.RefreshAsync 규약) -----
            var paperCoinSyms = Broker.Account.Positions.Where(p => p.Asset == PaperAsset.Coin).Select(p => p.Symbol)
                .Concat(Broker.Account.Orders.Where(o => o.Status == PaperStatus.Pending && o.Asset == PaperAsset.Coin).Select(o => o.Symbol))
                .Where(s => !_coinPx.ContainsKey(s))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (paperCoinSyms.Count > 0)
            {
                try { foreach (var t in await _upbit.GetTickersAsync(paperCoinSyms)) _coinPx[t.Market] = t.Price; }
                catch { }
            }
            var paperStock = Broker.Account.Positions.Where(p => p.Asset == PaperAsset.Stock).Select(p => (p.Symbol, p.StockMarket))
                .Concat(Broker.Account.Orders.Where(o => o.Status == PaperStatus.Pending && o.Asset == PaperAsset.Stock).Select(o => (o.Symbol, o.StockMarket)))
                .Where(x => !_stockPx.ContainsKey(x.Symbol)).Distinct().ToList();
            foreach (var grp in paperStock.GroupBy(x => x.StockMarket))
            {
                try
                {
                    var quotes = await _provider.GetQuotesAsync(grp.Select(x => x.Symbol), grp.Key);
                    bool usd = grp.Key == Market.US_NYSE || grp.Key == Market.US_NASDAQ || grp.Key == Market.US_AMEX;
                    foreach (var q in quotes)
                        if (q.Price > 0) _stockPx[q.Symbol] = q.Price * (usd ? UsdKrw : 1m);   // USD → KRW 환산 저장
                }
                catch { }
            }

            // ----- 모의투자 -----
            PaperCash = Broker.Account.Cash;
            PaperPosVal = Broker.PositionsValue(PriceOf);
            var eq = PaperCash + PaperPosVal;
            PaperPnl = eq - Broker.Account.Seed;
            PaperRet = Broker.Account.Seed > 0 ? PaperPnl / Broker.Account.Seed * 100m : 0m;

            // ----- 주간: 오늘 총자산 스냅샷 저장 후 최근 7일 집계 -----
            var snaps = LoadSnaps();
            snaps[DateTime.Today.ToString("yyyy-MM-dd")] = Total;
            snaps = snaps.OrderByDescending(k => k.Key).Take(30).ToDictionary(k => k.Key, v => v.Value);
            SaveSnaps(snaps);
            var last7 = snaps.OrderBy(k => k.Key).TakeLast(7).ToList();
            if (last7.Count >= 2)
            {
                Weekly = last7.Select(k => (DowLabel(k.Key), k.Value)).ToList();
                WeeklySample = false;
            }
            else
            {
                Weekly = new List<(string, decimal)> { ("월", 62), ("화", 70), ("수", 66), ("목", 80), ("금", 74), ("토", 90), ("일", 78) };
                WeeklySample = true;
            }

            // ----- 최근 활동(모의 주문 이력) -----
            Activity = Broker.Account.Orders.Take(6).Select(o => new ActItem(
                $"{(o.Side == PaperSide.Buy ? "매수" : "매도")} {o.Name} {Trim(o.Qty)} · {StatusKr(o.Status)}",
                o.Time.ToString("MM-dd HH:mm"))).ToList();
        }

        public (bool ok, string msg) QuickPaper(string market, decimal krw, PaperSide side)
        {
            if (!_coinPx.TryGetValue(market, out var price) || price <= 0) return (false, "시세 없음 — 먼저 새로고침");
            var qty = krw / price;
            if (side == PaperSide.Sell)
            {
                var held = Broker.HeldQty(PaperAsset.Coin, market);
                qty = Math.Min(qty, held);
                if (qty <= 0) return (false, "보유 수량 없음");
            }
            var name = Coins.FirstOrDefault(c => c.Market == market)?.Name ?? market.Replace("KRW-", "");
            return Broker.Place(PaperAsset.Coin, market, name, side, PaperKind.Market, qty, 0, price);
        }

        private Dictionary<string, decimal> LoadSnaps()
        {
            try
            {
                var j = _db.GetSetting("Dashboard.Snapshots");
                if (!string.IsNullOrWhiteSpace(j))
                    return JsonSerializer.Deserialize<Dictionary<string, decimal>>(j) ?? new();
            }
            catch { }
            return new Dictionary<string, decimal>();
        }
        private void SaveSnaps(Dictionary<string, decimal> d)
        {
            try { _db.SetSetting("Dashboard.Snapshots", JsonSerializer.Serialize(d)); } catch { }
        }
        private static string DowLabel(string ymd)
        {
            if (!DateTime.TryParse(ymd, out var dt)) return ymd;
            return new[] { "일", "월", "화", "수", "목", "금", "토" }[(int)dt.DayOfWeek];
        }

        // 업비트 WebSocket 구독 보장 — 최초 1회 핸들러 연결, watch 구성이 바뀌면 재구독.
        // 실패해도 폴링이 계속 동작하므로 try/catch로 흡수(추가형 보강). ⚠️ 런타임 검증 필요.
        private void EnsureWs(List<string> watch)
        {
            if (watch.Count == 0) return;
            if (!_wsHooked)
            {
                _ws.TickReceived += t => _wsPx[t.Market] = (t.Price, DateTime.Now);   // 백그라운드 스레드
                _wsHooked = true;
            }
            var key = string.Join(",", watch);
            if (key != _wsKey)
            {
                _wsKey = key;
                try { _ = _ws.StartAsync(watch); } catch { /* 폴링 폴백 */ }
            }
        }

        public void Dispose() { try { _ws.Dispose(); } catch { } }

        // 일봉 캔들을 5분 TTL로 캐시 — watch 구성이 바뀌거나 만료 시에만 재페치.
        private async Task<Dictionary<string, List<decimal>>> GetWatchCandlesAsync(List<string> watch)
        {
            var key = string.Join(",", watch);
            if (key == _candleCacheKey && _candleCache.Count > 0 && DateTime.Now - _candleCacheAt < CandleTtl)
                return _candleCache;   // 캐시 적중 — Upbit 캔들 API 호출 생략

            var map = new Dictionary<string, List<decimal>>(StringComparer.OrdinalIgnoreCase);
            foreach (var m in watch)
            {
                try
                {
                    var candles = await _upbit.GetCandlesAsync(m, 30);
                    var closes = candles.Select(c => c.Close).ToList();
                    if (closes.Count > 0) map[m] = closes;
                }
                catch { }
            }
            if (map.Count > 0)   // 일부라도 받았으면 캐시 갱신 (전부 실패 시 직전 캐시 유지)
            {
                _candleCache.Clear();
                foreach (var kv in map) _candleCache[kv.Key] = kv.Value;
                _candleCacheAt = DateTime.Now;
                _candleCacheKey = key;
            }
            return _candleCache.Count > 0 ? _candleCache : map;
        }

        // ===== 임박 신호 스캔(코인 일봉) =====
        private void ScanSignals(List<string> markets,
            Dictionary<string, CryptoTick> ticks, Dictionary<string, List<decimal>> candleMap)
        {
            var found = new List<SignalRow>();
            foreach (var m in markets)
            {
                if (!candleMap.TryGetValue(m, out var closes) || closes.Count < 21) continue;
                ticks.TryGetValue(m, out var t);
                var name = t?.KoreanName ?? m.Replace("KRW-", "");
                decimal price = t?.Price ?? closes[^1];

                decimal ma5 = Ma(closes, 5), ma20 = Ma(closes, 20);
                var prev = closes.Take(closes.Count - 1).ToList();
                decimal pMa5 = Ma(prev, 5), pMa20 = Ma(prev, 20);
                decimal rsi = Rsi(closes, 14);
                decimal hi20 = closes.Skip(closes.Count - 20).Max();

                // 골든/데드 크로스
                if (pMa5 <= pMa20 && ma5 > ma20)
                {
                    found.Add(new SignalRow("BUY", name, "골든크로스 (MA5 > MA20)", Strength(ma20 == 0 ? 0 : (ma5 - ma20) / ma20 * 100m, 5m)));
                    PersistCrossSignal(m, name, buy: true, price);
                }
                else if (pMa5 >= pMa20 && ma5 < ma20)
                {
                    found.Add(new SignalRow("SELL", name, "데드크로스 (MA5 < MA20)", Strength(ma20 == 0 ? 0 : (ma20 - ma5) / ma20 * 100m, 5m)));
                    PersistCrossSignal(m, name, buy: false, price);
                }

                // RSI 과매수/과매도
                if (rsi <= 30) found.Add(new SignalRow("BUY", name, $"RSI 과매도 ({rsi:0})", Strength(30 - rsi, 30m)));
                else if (rsi >= 70) found.Add(new SignalRow("SELL", name, $"RSI 과매수 ({rsi:0})", Strength(rsi - 70, 30m)));

                // 20일 신고가 돌파
                if (price >= hi20 && price > 0)
                    found.Add(new SignalRow("BUY", name, "20일 신고가 돌파", Strength((t?.ChangeRate ?? 0m) * 100m, 5m)));
            }
            Signals = found.OrderByDescending(s => s.Pct).Take(6).ToList();
        }

        // ===== 시그널 영속 + 알림 — 원본 SignalEngine 룰 보존 =====
        //  · 비대칭: 골든크로스(매수)=DB 저장+알림(Warning) / 데드크로스(매도)=저장만
        //  · 목표가/손절가: 매수 ×1.05/×0.97(신뢰도 65) · 매도 ×0.95/×1.02(신뢰도 60)
        //  · 대시보드는 자동 재스캔되므로 (종목·전략·당일) 1회 발행 가드 추가 (ISO 문자열 비교)
        private void PersistCrossSignal(string symbol, string name, bool buy, decimal price)
        {
            try
            {
                var strategy = buy ? "이평 골든크로스" : "이평 데드크로스";
                var dup = _db.QueryScalar<long>(
                    "SELECT COUNT(*) FROM Signals WHERE Symbol=@s AND Strategy=@st AND GeneratedAt>=@d",
                    new() { ["@s"] = symbol, ["@st"] = strategy, ["@d"] = DateTime.Today.ToString("o") });
                if (dup > 0) return;

                _db.Execute(@"INSERT INTO Signals(Symbol,Name,Signal,Strength,Strategy,Reason,TargetPrice,StopLoss,Confidence,GeneratedAt)
                    VALUES(@s,@n,@sig,@str,@stra,@r,@t,@sl,@c,@g)",
                    new()
                    {
                        ["@s"] = symbol, ["@n"] = name,
                        ["@sig"] = (int)(buy ? SignalType.Buy : SignalType.Sell),
                        ["@str"] = (int)SignalStrength.Medium,
                        ["@stra"] = strategy,
                        ["@r"] = buy ? "MA5 > MA20 돌파" : "MA5 < MA20 이탈",
                        ["@t"] = (double)(price * (buy ? 1.05m : 0.95m)),
                        ["@sl"] = (double)(price * (buy ? 0.97m : 1.02m)),
                        ["@c"] = buy ? 65d : 60d,
                        ["@g"] = DateTime.Now.ToString("o")
                    });

                if (buy)   // 매수 시그널만 알림 발행 (원본 비대칭 룰)
                    AlertCenter.Instance.RaiseSignal(symbol,
                        $"🟢 매수 시그널 · {name}", $"골든크로스 ({price:N0})", AlertLevel.Warning);
            }
            catch (Exception ex) { Log.Warn($"[Dashboard] 시그널 저장 실패: {ex.Message}"); }
        }

        private static int Strength(decimal v, decimal scaleAt100)
        {
            if (scaleAt100 <= 0) return 50;
            var p = (int)Math.Round(Math.Abs(v) / scaleAt100 * 100m);
            return Math.Clamp(p, 10, 100);
        }
        private static decimal Ma(List<decimal> c, int n)
        {
            if (c.Count < n || n <= 0) return 0;
            decimal s = 0; for (int i = c.Count - n; i < c.Count; i++) s += c[i]; return s / n;
        }
        private static decimal Rsi(List<decimal> c, int n)
        {
            if (c.Count <= n || n <= 0) return 50;
            decimal g = 0, l = 0;
            for (int i = c.Count - n; i < c.Count; i++) { var d = c[i] - c[i - 1]; if (d >= 0) g += d; else l -= d; }
            if (l == 0) return 100m;
            var rs = (g / n) / (l / n);
            return 100m - 100m / (1m + rs);
        }

        // 위젯 배치 영속
        public List<string> LoadLayout()
        {
            var def = new List<string> { "coins", "pie", "weekly", "paper", "imminent", "activity" };
            try
            {
                var j = _db.GetSetting("Dashboard.Layout");
                if (!string.IsNullOrWhiteSpace(j))
                {
                    var arr = JsonSerializer.Deserialize<List<string>>(j);
                    if (arr is { Count: > 0 }) return arr;
                }
            }
            catch { }
            return def;
        }
        public void SaveLayout(IEnumerable<string> order)
        {
            try { _db.SetSetting("Dashboard.Layout", JsonSerializer.Serialize(order.ToList())); } catch { }
        }

        public string Theme
        {
            get => _db.GetSetting("UI.Theme") == "light" ? "light" : "dark";   // 기본 dark(엔터프라이즈) — 명시적 light만 라이트
            set
            {
                // ⚠️ 이원화 방지: 설정 키만 쓰지 않고 ThemeManager.Set으로 일원화 —
                // 저장 + 열린 WinForms 창 재칠 + Changed 이벤트(커스텀 폼 확정) 순서 계약 유지
                try { ThemeManager.Set(value == "dark" ? ThemeMode.Dark : ThemeMode.Light, _db); }
                catch { try { _db.SetSetting("UI.Theme", value == "dark" ? "dark" : "light"); } catch { } }
            }
        }

        // ===== 코인 지정가 알림 매크로 (Ctrl+K — 원본 Launcher.AddCoinAlert/DisableAllCoinAlerts 이식) =====
        /// <summary>이상(Above) 알림 등록. 원본 룰: 감시목록에 없으면 자동 추가.</summary>
        public void AddCoinAlert(string market, decimal target)
        {
            var s = LoadCryptoState();
            if (!s.Watch.Contains(market)) s.Watch.Add(market);
            s.Alerts.Add(new CryptoWatcher.CoinAlert { Market = market, Above = true, Target = target });
            SaveCryptoState(s);
        }

        /// <summary>원본 "알림 끄기" 매크로의 원자적 동작 ①: 코인 지정가 알림 전체 제거.</summary>
        public void DisableAllCoinAlerts()
        {
            var s = LoadCryptoState();
            s.Alerts.Clear();
            SaveCryptoState(s);
        }

        /// <summary>원본 "알림 끄기" 매크로의 원자적 동작 ②: 텔레그램 자동 전송 토글.</summary>
        public void SetTelegramEnabled(bool on)
        {
            try { _db.SetSetting("Telegram.Enabled", on ? "true" : "false"); } catch { }
        }

        private void SaveCryptoState(CryptoWatcher.CryptoState s)
        {
            try { _db.SetSetting("Crypto.State", JsonSerializer.Serialize(s)); }
            catch (Exception ex) { Log.Warn($"[Dashboard] Crypto.State 저장 실패: {ex.Message}"); }
        }

        public CryptoWatcher.CryptoState GetCryptoState() => LoadCryptoState();
        private CryptoWatcher.CryptoState LoadCryptoState()
        {
            try
            {
                var j = _db.GetSetting("Crypto.State");
                if (!string.IsNullOrWhiteSpace(j))
                    return JsonSerializer.Deserialize<CryptoWatcher.CryptoState>(j) ?? new CryptoWatcher.CryptoState();
            }
            catch { }
            return new CryptoWatcher.CryptoState();
        }

        private static string Trim(decimal d) => d == Math.Floor(d) ? d.ToString("N0") : d.ToString("0.####");
        private static string StatusKr(PaperStatus s) => s switch
        { PaperStatus.Pending => "대기", PaperStatus.Filled => "체결", PaperStatus.Canceled => "취소", _ => "거부" };
    }
}
