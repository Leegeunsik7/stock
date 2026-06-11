// ============================================================================
// AlertEngineService - 알림·체결 백그라운드 평가 서비스 (v3.4.2)
//  Blazor 홈이 기본 진입점이 되면서 "창이 열려 있어야만 돌던" 3개 엔진이
//  침묵하는 문제를 해결한다. 앱 시작 시 Start() 1회 호출로 상시 평가.
//   ① 코인 지정가 알림  — Crypto.State(JSON) 기반, 이탈 시 재무장(re-arm) 정책
//   ② 주식 AlertRules    — DB 기반, 발동 즉시 IsTriggered=1 (one-shot 정책)
//      + 폴링 간 5% 이상 급변 감지 (AlertRules 등록 종목 한정)
//   ③ 모의투자 지정가    — PaperBroker.ProcessLimits 자동 체결
//  ⚠️ 해당 WinForms 창(CryptoWatcher/RealtimeWatcher/PaperTrading)이 열려 있으면
//     그 창이 같은 일을 하므로 이중 발동을 막기 위해 해당 항목 평가를 건너뛴다.
// ============================================================================
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace StockManager.Common
{
    public class AlertEngineService
    {
        public static AlertEngineService Instance { get; } = new();

        private LocalDatabase? _db;
        private IQuoteProvider? _provider;
        private readonly UpbitProvider _upbit = new();
        private readonly FxConverter _fx = new();
        private PaperBroker? _paper;
        private System.Windows.Forms.Timer? _timer;
        private bool _busy;

        // 5% 급변 감지용 직전 폴링 가격 (원본 RealtimeWatcher._prevPrices 대응)
        private readonly Dictionary<string, decimal> _prevStock = new(StringComparer.OrdinalIgnoreCase);
        // 지정가 체결용 가격 캐시 (주식은 KRW 환산값 — 원본 PaperTrading 규약)
        private readonly Dictionary<string, decimal> _coinPx = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, decimal> _stockPxKrw = new(StringComparer.OrdinalIgnoreCase);
        private decimal _usdKrw = 1380m;

        public DateTime LastRun { get; private set; }

        private AlertEngineService() { }

        /// <summary>앱 시작 시 1회 호출. 기본 10초 주기(원본 PaperTrading 갱신 주기와 동일).</summary>
        public void Start(LocalDatabase db, IQuoteProvider provider, int intervalMs = 10_000)
        {
            _db = db; _provider = provider;
            _paper = new PaperBroker(db);
            if (_timer != null) return;
            _timer = new System.Windows.Forms.Timer { Interval = intervalMs };
            _timer.Tick += async (s, e) => await TickAsync();
            _timer.Start();
        }

        public void Stop() { _timer?.Stop(); _timer?.Dispose(); _timer = null; }

        private static bool FormOpen<T>() where T : Form
        {
            try { return Application.OpenForms.OfType<T>().Any(f => !f.IsDisposed); }
            catch { return false; }
        }

        private async Task TickAsync()
        {
            if (_db == null || _busy) return;
            _busy = true;
            try
            {
                await CheckCoinAlertsAsync();
                await CheckStockRulesAsync();
                await ProcessPaperLimitsAsync();
                await CheckDailyBriefAsync();
                await CheckAutoDcaAsync();
                LastRun = DateTime.Now;
            }
            catch (Exception ex) { Log.Warn($"[AlertEngine] {ex.Message}"); }
            finally { _busy = false; }
        }

        // ===== 아침 브리핑 (옵트인) — Brief.Enabled일 때 하루 1회 텔레그램 전송 =====
        private async Task CheckDailyBriefAsync()
        {
            if (_db!.GetSetting("Brief.Enabled") != "true") return;
            int hour = int.TryParse(_db.GetSetting("Brief.Hour"), out var h) ? Math.Clamp(h, 0, 23) : 8;
            if (DateTime.Now.Hour < hour) return;                       // 설정 시각 이전이면 대기
            var today = DateTime.Now.ToString("yyyy-MM-dd");
            if (_db.GetSetting("Brief.LastSent") == today) return;      // 오늘 이미 전송됨

            var ai = new AiService(_db);
            if (!ai.HasKey) return;                                     // 키 없으면 마킹도 안 함(키 넣으면 그날 전송)

            _db.SetSetting("Brief.LastSent", today);                    // 중복 전송 방지(생성 실패해도 당일 재시도 안 함)
            decimal usd = 1380m;
            try { usd = await _fx.GetUsdKrwAsync(); } catch { }
            await ai.MorningBriefAsync(usd, pushTelegram: true);
            Log.Info("[AlertEngine] 아침 브리핑 생성·전송");
        }

        // ===== 자동 DCA (⚠️ 모의 전용) — 실주문 경로(BrokerHub/LiveBrokers) 절대 미접촉 =====
        //  옵트인(AutoDca.Enabled). 일정 간격마다 PaperBroker로만 정액 매수. 킬스위치=토글 OFF.
        private async Task CheckAutoDcaAsync()
        {
            if (_db!.GetSetting("AutoDca.Enabled") != "true" || _paper == null) return;
            int hours = int.TryParse(_db.GetSetting("AutoDca.IntervalHours"), out var hh) && hh > 0 ? hh : 24;
            if (DateTime.TryParse(_db.GetSetting("AutoDca.LastRun"), out var last) && (DateTime.Now - last).TotalHours < hours) return;

            var market = _db.GetSetting("AutoDca.Market");
            if (string.IsNullOrWhiteSpace(market)) market = "KRW-BTC";
            if (!market.StartsWith("KRW-", StringComparison.OrdinalIgnoreCase)) return;   // 안전: 코인 KRW 마켓만
            decimal amount = decimal.TryParse(_db.GetSetting("AutoDca.AmountKrw"), out var a) && a > 0 ? a : 100_000m;
            amount = Math.Min(amount, 10_000_000m);                                       // 안전 상한

            decimal price = 0m;
            try { var ts = await _upbit.GetTickersAsync(new[] { market }); price = ts.FirstOrDefault()?.Price ?? 0m; } catch { }
            if (price <= 0) return;

            _db.SetSetting("AutoDca.LastRun", DateTime.Now.ToString("o"));                // 먼저 마킹(중복 매수 방지)
            _paper.Reload();
            var name = market.Replace("KRW-", "");
            var (ok, msg) = _paper.Place(PaperAsset.Coin, market, name, PaperSide.Buy, PaperKind.Market, amount / price, 0, price);
            Log.Info($"[AutoDCA·모의] {market} {amount:N0}원 → {(ok ? "체결" : "실패")}: {msg}");
            if (ok) AlertCenter.Instance.RaiseSystem("🤖 자동 DCA (모의)", $"{name} {amount:N0}원 매수 — {msg}", AlertLevel.Info);
        }

        // ===== ① 코인 지정가 알림 (재무장 정책 — 원본 CryptoWatcher.CheckAlerts와 동일) =====
        private async Task CheckCoinAlertsAsync()
        {
            if (FormOpen<CryptoWatcher.MainForm>()) return;   // 창이 직접 처리 중
            var state = LoadCryptoState();
            if (state.Alerts.Count == 0) return;

            var markets = state.Alerts.Select(a => a.Market)
                .Where(m => !string.IsNullOrWhiteSpace(m))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (markets.Count == 0) return;

            var ticks = await _upbit.GetTickersAsync(markets);
            bool changed = false;
            foreach (var t in ticks)
            {
                _coinPx[t.Market] = t.Price;
                foreach (var a in state.Alerts.Where(a => string.Equals(a.Market, t.Market, StringComparison.OrdinalIgnoreCase)))
                {
                    var (fired, nt) = AlertPolicy.StepAlert(a.Above, a.Target, t.Price, a.Triggered);
                    if (nt != a.Triggered) { a.Triggered = nt; changed = true; }   // 발동/재무장 시 상태 변화
                    if (fired)
                    {
                        var dir = a.Above ? "이상" : "이하";
                        AlertCenter.Instance.RaisePrice(t.Market,
                            $"🪙 {t.KoreanName} 지정가 {dir} 도달",
                            $"{t.KoreanName}({t.Market}) 현재가 {t.Price:N0}원이 목표 {a.Target:N0}원 {dir}입니다.",
                            AlertLevel.Critical);
                    }
                }
            }
            if (changed) SaveCryptoState(state);
        }

        // ===== ② 주식 AlertRules (one-shot) + 5% 급변 (원본 RealtimeWatcher.CheckPriceAlerts) =====
        private async Task CheckStockRulesAsync()
        {
            if (_provider == null || FormOpen<RealtimeWatcher.MainForm>()) return;

            var symDt = _db!.Query("SELECT DISTINCT Symbol FROM AlertRules WHERE IsActive=1 AND IsTriggered=0");
            if (symDt.Rows.Count == 0) return;
            var symbols = symDt.Rows.Cast<System.Data.DataRow>()
                .Select(r => Safe.Str(r["Symbol"]))
                .Where(s => !string.IsNullOrEmpty(s)).ToList();

            foreach (var group in symbols.GroupBy(DetectMarket))
            {
                List<Quote> quotes;
                try { quotes = await _provider.GetQuotesAsync(group, group.Key); }
                catch { continue; }

                foreach (var q in quotes)
                {
                    // 5% 이상 급변 (prev > 0 가드 — 원본과 동일)
                    if (_prevStock.TryGetValue(q.Symbol, out var prev) && AlertPolicy.IsSurge(prev, q.Price))
                    {
                        var pct = (q.Price - prev) / prev * 100m;
                        AlertCenter.Instance.RaisePrice(q.Symbol,
                            $"급변동 감지 · {q.Symbol} {q.Name}",
                            $"{Formatter.Pct(pct)} ({Formatter.Money(prev, q.Currency)} → {Formatter.Money(q.Price, q.Currency)})",
                            AlertLevel.Warning);
                    }
                    _prevStock[q.Symbol] = q.Price;
                    _stockPxKrw[q.Symbol] = q.Price * (q.Currency == Currency.USD ? _usdKrw : 1m);

                    var rules = _db.Query(
                        "SELECT * FROM AlertRules WHERE Symbol=@s AND IsActive=1 AND IsTriggered=0",
                        new() { ["@s"] = q.Symbol });
                    foreach (System.Data.DataRow r in rules.Rows)
                    {
                        var cond = (AlertCondition)Safe.Int(r["Condition"]);
                        var target = Safe.Dec(r["TargetValue"]);
                        bool hit = AlertPolicy.StockRuleHit(cond, target, q.Price, q.ChangeRate);
                        if (hit)
                        {
                            AlertCenter.Instance.RaisePrice(q.Symbol,
                                $"알림 달성 · {q.Name}",
                                $"{cond} {target} (현재 {Formatter.Money(q.Price, q.Currency)})",
                                AlertLevel.Critical);
                            _db.Execute("UPDATE AlertRules SET IsTriggered=1 WHERE Id=@id",
                                new() { ["@id"] = Safe.Int(r["Id"]) });   // one-shot 자동 해제
                        }
                    }
                }
            }
        }

        // ===== ③ 모의투자 지정가 자동 체결 (원본 PaperTrading.RefreshAsync 대응) =====
        private async Task ProcessPaperLimitsAsync()
        {
            if (_paper == null || FormOpen<PaperTrading.MainForm>()) return;

            _paper.Reload();   // 다른 인스턴스(Blazor QuickPaper 등)가 저장한 최신 상태 반영
            var pending = _paper.Account.Orders
                .Where(o => o.Status == PaperStatus.Pending && o.Kind == PaperKind.Limit).ToList();
            if (pending.Count == 0) return;

            try { _usdKrw = await _fx.GetUsdKrwAsync(); } catch { /* 캐시값 유지 */ }

            // 코인 가격
            var coinSyms = pending.Where(o => o.Asset == PaperAsset.Coin).Select(o => o.Symbol)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (coinSyms.Count > 0)
            {
                try { foreach (var t in await _upbit.GetTickersAsync(coinSyms)) _coinPx[t.Market] = t.Price; }
                catch { }
            }

            // 주식 가격 (시장별 그룹 · USD는 KRW 환산 — 원본 규약 유지)
            var stockSyms = pending.Where(o => o.Asset == PaperAsset.Stock)
                .Select(o => (o.Symbol, o.StockMarket)).Distinct().ToList();
            foreach (var grp in stockSyms.GroupBy(x => x.StockMarket))
            {
                try
                {
                    var quotes = await _provider!.GetQuotesAsync(grp.Select(x => x.Symbol), grp.Key);
                    bool usd = grp.Key == Market.US_NYSE || grp.Key == Market.US_NASDAQ || grp.Key == Market.US_AMEX;
                    foreach (var q in quotes)
                        if (q.Price > 0) _stockPxKrw[q.Symbol] = q.Price * (usd ? _usdKrw : 1m);
                }
                catch { }
            }

            if (_paper.ProcessLimits(PriceOf))
                Log.Info("[AlertEngine] 모의 지정가 자동 체결 발생");
        }

        private decimal PriceOf(PaperAsset a, string sym) =>
            a == PaperAsset.Coin
                ? (_coinPx.TryGetValue(sym, out var p) ? p : 0m)
                : (_stockPxKrw.TryGetValue(sym, out var s) ? s : 0m);

        // 원본 RealtimeWatcher.DetectMarket 룰: 6자리 + 숫자 시작 → KRX, 아니면 NASDAQ
        private static Market DetectMarket(string symbol) =>
            symbol.Length == 6 && char.IsDigit(symbol[0]) ? Market.KRX_KOSPI : Market.US_NASDAQ;

        private CryptoWatcher.CryptoState LoadCryptoState()
        {
            try
            {
                var j = _db!.GetSetting("Crypto.State");
                if (!string.IsNullOrWhiteSpace(j))
                    return System.Text.Json.JsonSerializer.Deserialize<CryptoWatcher.CryptoState>(j)
                           ?? new CryptoWatcher.CryptoState();
            }
            catch { }
            return new CryptoWatcher.CryptoState();
        }

        private void SaveCryptoState(CryptoWatcher.CryptoState s)
        {
            try { _db!.SetSetting("Crypto.State", System.Text.Json.JsonSerializer.Serialize(s)); }
            catch (Exception ex) { Log.Warn($"[AlertEngine] Crypto.State 저장 실패: {ex.Message}"); }
        }
    }
}
