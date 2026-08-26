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

namespace MS2E.Infrastructure
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
                await CheckOptimizerAsync();
                await CheckRegimeAsync();
                await CheckDriftAsync();
                await CheckNightlyAsync();
                await CheckNightlyResearchAsync();
                CheckCopilot();
                await CheckMaintenanceAsync();
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

        // ===== 오버나잇 전략 옵티마이저 🅔 (옵트인 · 읽기전용 백테스트) =====
        //  유휴 시간에 보유 주식마다 MA크로스 기간 격자를 전수 백테스트해 종목별 최적값 발굴.
        //  주문/DCA를 절대 건드리지 않음(발굴만). 결과는 Optimizer.Results(JSON)에 저장 → /optimizer가 표시.
        //  IntervalHours(기본 24) 간격으로 1회. API 보호: 종목당 짧은 간격 + 25종목 상한.
        private async Task CheckOptimizerAsync()
        {
            if (_db!.GetSetting("Optimizer.Enabled") != "true" || _provider == null) return;
            int hours = int.TryParse(_db.GetSetting("Optimizer.IntervalHours"), out var hh) && hh > 0 ? hh : 24;
            if (DateTime.TryParse(_db.GetSetting("Optimizer.LastRun"), out var last) && (DateTime.Now - last).TotalHours < hours) return;

            var holdings = _db.GetAllHoldings()
                .Where(h => h.Market != Market.CRYPTO && !string.IsNullOrWhiteSpace(h.Symbol))
                .GroupBy(h => h.Symbol, StringComparer.OrdinalIgnoreCase).Select(g => g.First())
                .Take(25).ToList();
            if (holdings.Count == 0) return;

            _db.SetSetting("Optimizer.LastRun", DateTime.Now.ToString("o"));   // 먼저 마킹(중복 실행 방지 — DCA/Brief와 동일 규약)

            var snap = new OptSnapshot { GeneratedAt = DateTime.Now.ToString("o") };
            foreach (var h in holdings)
            {
                try
                {
                    var candles = await _provider.GetHistoryAsync(h.Symbol, h.Market,
                        DateTime.Now.AddMonths(-18), DateTime.Now);
                    var r = StrategyOptimizer.Optimize(h.Symbol, string.IsNullOrWhiteSpace(h.Name) ? h.Symbol : h.Name, candles, h.Market);
                    snap.Rows.Add(StrategyOptimizer.ToRow(r));
                }
                catch (Exception ex) { Log.Warn($"[Optimizer] {h.Symbol} 실패: {ex.Message}"); }
                await Task.Delay(150);   // API 한도 보호
            }

            try { _db.SetSetting("Optimizer.Results", System.Text.Json.JsonSerializer.Serialize(snap)); }
            catch (Exception ex) { Log.Warn($"[Optimizer] 결과 저장 실패: {ex.Message}"); }

            var best = snap.Rows.Where(r => r.IsValid).OrderByDescending(r => r.TotalReturn).FirstOrDefault();
            int valid = snap.Rows.Count(r => r.IsValid);
            if (best != null)
                AlertCenter.Instance.RaiseSystem("🌙 오버나잇 옵티마이저",
                    $"{valid}/{snap.Rows.Count}종목 분석 완료 · 최고 {best.Name} {best.Label} ({best.TotalReturn:+0.0;-0.0}%)",
                    AlertLevel.Info);
            Log.Info($"[Optimizer] {valid}/{snap.Rows.Count}종목 격자탐색 완료");
        }

        // ===== 레짐 변화 이상탐지 🅘 (옵트인 · 읽기전용) =====
        //  보유 주식의 "평소 성격" 이탈(수익률 이상치·변동성 급등·평균 레짐 이동)을 통계로 감지.
        //  + 포트폴리오 평균 페어 상관이 급등하면 분산효과 붕괴 경고. 종목당 쿨다운으로 도배 방지.
        private async Task CheckRegimeAsync()
        {
            if (_db!.GetSetting("Regime.Enabled") != "true" || _provider == null) return;
            int hours = int.TryParse(_db.GetSetting("Regime.IntervalHours"), out var hh) && hh > 0 ? hh : 12;
            if (DateTime.TryParse(_db.GetSetting("Regime.LastRun"), out var last) && (DateTime.Now - last).TotalHours < hours) return;

            var holdings = _db.GetAllHoldings()
                .Where(h => h.Market != Market.CRYPTO && !string.IsNullOrWhiteSpace(h.Symbol))
                .GroupBy(h => h.Symbol, StringComparer.OrdinalIgnoreCase).Select(g => g.First())
                .Take(25).ToList();
            if (holdings.Count == 0) return;

            _db.SetSetting("Regime.LastRun", DateTime.Now.ToString("o"));   // 먼저 마킹(중복 실행 방지)
            var cooldown = LoadRegimeAlerts();
            var retsBySym = new Dictionary<string, double[]>();
            int alerted = 0;

            foreach (var h in holdings)
            {
                try
                {
                    var candles = await _provider.GetHistoryAsync(h.Symbol, h.Market, DateTime.Now.AddMonths(-4), DateTime.Now);
                    if (candles.Count < 25) continue;
                    var closes = candles.Select(c => c.Close).ToList();
                    retsBySym[h.Symbol] = RegimeDetector.ToReturns(closes);

                    var sig = RegimeDetector.Analyze(closes);
                    if (!sig.IsAnomaly) continue;

                    // 종목당 쿨다운 20h — 같은 이상을 매 주기 반복 경보하지 않음
                    if (cooldown.TryGetValue(h.Symbol, out var lastIso) &&
                        DateTime.TryParse(lastIso, out var la) && (DateTime.Now - la).TotalHours < 20) continue;

                    cooldown[h.Symbol] = DateTime.Now.ToString("o");
                    alerted++;
                    var name = string.IsNullOrWhiteSpace(h.Name) ? h.Symbol : h.Name;
                    AlertCenter.Instance.RaisePrice(h.Symbol,
                        $"🛰 레짐 변화 · {name}", $"{name}({h.Symbol}) {sig.Describe()}", AlertLevel.Warning);
                }
                catch (Exception ex) { Log.Warn($"[Regime] {h.Symbol} 실패: {ex.Message}"); }
                await Task.Delay(150);
            }

            // 분산효과 붕괴: 평균 페어 상관 급등
            if (retsBySym.Count >= 3)
            {
                var avgCorr = RegimeDetector.AvgPairwiseCorrelation(retsBySym.Values.ToList());
                if (avgCorr >= 0.85 &&
                    !(cooldown.TryGetValue("__corr__", out var cIso) && DateTime.TryParse(cIso, out var cl) && (DateTime.Now - cl).TotalHours < 20))
                {
                    cooldown["__corr__"] = DateTime.Now.ToString("o");
                    AlertCenter.Instance.RaiseSystem("🛰 분산효과 붕괴 경고",
                        $"보유 {retsBySym.Count}종목 평균 상관이 {avgCorr:0.00}로 급등 — 함께 움직이는 중(분산 약화)", AlertLevel.Warning);
                }
            }

            SaveRegimeAlerts(cooldown);
            if (alerted > 0) Log.Info($"[Regime] {alerted}종목 레짐 이상 경보");
        }

        private Dictionary<string, string> LoadRegimeAlerts()
        {
            try
            {
                var j = _db!.GetSetting("Regime.Alerts");
                if (!string.IsNullOrWhiteSpace(j))
                    return System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(j) ?? new();
            }
            catch { }
            return new(StringComparer.OrdinalIgnoreCase);
        }

        private void SaveRegimeAlerts(Dictionary<string, string> d)
        {
            try { _db!.SetSetting("Regime.Alerts", System.Text.Json.JsonSerializer.Serialize(d)); }
            catch (Exception ex) { Log.Warn($"[Regime] 쿨다운 저장 실패: {ex.Message}"); }
        }

        // ===== MPT 리밸런싱 드리프트 트리거 🄸 (옵트인 · 읽기전용) =====
        //  /portfolio 리밸런싱 탭에서 저장한 타겟 비중(Rebalance.Target) 대비 현재 비중 괴리가
        //  밴드(기본 ±5%p)를 넘으면 "리밸런싱 시점" 알림. 종목당이 아닌 포트 1건, 20h 쿨다운.
        private async Task CheckDriftAsync()
        {
            if (_db!.GetSetting("Drift.Enabled") != "true" || _provider == null) return;
            int hours = int.TryParse(_db.GetSetting("Drift.IntervalHours"), out var hh) && hh > 0 ? hh : 24;
            if (DateTime.TryParse(_db.GetSetting("Drift.LastRun"), out var last) && (DateTime.Now - last).TotalHours < hours) return;
            double band = double.TryParse(_db.GetSetting("Drift.BandPct"), out var b) && b > 0 ? b : 5.0;

            var target = LoadDriftTarget();
            if (target.Count == 0) return;                                  // 저장된 타겟 없으면 skip
            _db.SetSetting("Drift.LastRun", DateTime.Now.ToString("o"));    // 먼저 마킹

            var holdings = _db.GetAllHoldings()
                .Where(h => h.Market != Market.CRYPTO && !string.IsNullOrWhiteSpace(h.Symbol)).ToList();
            if (holdings.Count == 0) return;
            try { _usdKrw = await _fx.GetUsdKrwAsync(); } catch { }

            // 현재 비중: 보유 주식 시세 수집(USD→KRW). 실패 종목은 보유 CurrentPrice로 폴백.
            var priceKrw = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
            foreach (var grp in holdings.GroupBy(h => h.Market))
            {
                try
                {
                    var quotes = await _provider.GetQuotesAsync(grp.Select(h => h.Symbol), grp.Key);
                    bool usd = grp.Key is Market.US_NYSE or Market.US_NASDAQ or Market.US_AMEX;
                    foreach (var q in quotes) if (q.Price > 0) priceKrw[q.Symbol] = q.Price * (usd ? _usdKrw : 1m);
                }
                catch { }
            }
            double tot = 0; var vals = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (var h in holdings)
            {
                decimal px = priceKrw.TryGetValue(h.Symbol, out var p) ? p
                           : h.CurrentPrice * (h.Currency == Currency.USD ? _usdKrw : 1m);
                double v = (double)(px * h.Quantity);
                vals[h.Symbol] = vals.TryGetValue(h.Symbol, out var ex) ? ex + v : v;
                tot += v;
            }
            if (tot <= 0) return;
            var current = vals.ToDictionary(kv => kv.Key, kv => kv.Value / tot, StringComparer.OrdinalIgnoreCase);

            var rep = RebalanceDrift.Compute(current, target);
            if (!RebalanceDrift.Breached(rep, band)) return;
            if (DateTime.TryParse(_db.GetSetting("Drift.LastAlert"), out var la) && (DateTime.Now - la).TotalHours < 20) return;

            _db.SetSetting("Drift.LastAlert", DateTime.Now.ToString("o"));
            AlertCenter.Instance.RaiseSystem("⚖️ 리밸런싱 시점",
                $"타겟 대비 최대 {rep.MaxDriftPct:0.0}%p 이탈({rep.MaxSymbol}) · 회전율 {rep.TurnoverPct:0.0}% — 포트폴리오 허브에서 조정 검토",
                AlertLevel.Info);
            Log.Info($"[Drift] 최대 {rep.MaxDriftPct:0.0}%p (band {band})");
        }

        // ===== 야간 자동 퍼블리싱 ③ (옵트인) — 새벽에 종합 리포트 PDF/텍스트 → 텔레그램/메일 =====
        private async Task CheckNightlyAsync()
        {
            if (_db!.GetSetting("Nightly.Enabled") != "true") return;
            int hour = int.TryParse(_db.GetSetting("Nightly.Hour"), out var h) ? Math.Clamp(h, 0, 23) : 6;
            if (DateTime.Now.Hour < hour) return;
            var today = DateTime.Now.ToString("yyyy-MM-dd");
            if (_db.GetSetting("Nightly.LastSent") == today) return;   // 오늘 이미 전송

            _db.SetSetting("Nightly.LastSent", today);                  // 먼저 마킹(중복 방지)
            decimal usd = 1380m; try { usd = await _fx.GetUsdKrwAsync(); } catch { }
            var data = NightlyReport.Gather(_db, usd);
            var text = NightlyReport.ComposeText(data);

            bool sent = false;
            try { if (await TelegramNotifier.SendAsync(_db, "🌙 <b>야간 리포트</b>\n" + text)) sent = true; } catch { }
            try
            {
                if (EmailSender.Configured(_db))
                {
                    byte[]? pdf = null; try { pdf = NightlyReport.BuildPdf(data); } catch (Exception ex) { Log.Warn($"[Nightly] PDF 실패: {ex.Message}"); }
                    if (await EmailSender.SendAsync(_db, $"[MS2-E] 야간 리포트 {today}", text, pdf, $"ms2e_{today}.pdf")) sent = true;
                }
            }
            catch { }
            Log.Info($"[Nightly] 리포트 생성 · 전송 {(sent ? "성공" : "채널 없음/실패")}");
        }

        // ===== 자율 야간 연구 루프 🅰 (Season 8 #2, 옵트인 · 읽기전용 · 게이팅) =====
        //  새벽 설정 시각(Research.Hour, 기본 5시)에 하루 1회 한 바퀴:
        //   보유종목 전략 발굴(Forge) → 카트리지 자가서명 → 미래 투영(FutureCone) → 코파일럿 리스크 평가
        //   → 인과 저널 적재(시스템 알림) → /research 일지 + (옵트인)텔레그램 브리핑.
        //  주문/DCA 절대 미접촉. LastRun 선마킹·25종목 상한·API 보호는 옵티마이저와 동일 규약.
        private async Task CheckNightlyResearchAsync()
        {
            if (_db!.GetSetting("Research.Enabled") != "true" || _provider == null) return;
            int hour = int.TryParse(_db.GetSetting("Research.Hour"), out var h) ? Math.Clamp(h, 0, 23) : 5;
            if (DateTime.Now.Hour < hour) return;
            var today = DateTime.Now.ToString("yyyy-MM-dd");
            if (_db.GetSetting("Research.LastRun") == today) return;     // 오늘 이미 실행(수동 포함)
            // 선마킹하지 않음: RunCore가 성공 시 Research.LastRun을 마킹하고, 동시 진입은 RunAsync 내부 게이트가 차단.
            // 실행 중(수동 '지금 실행' 등)이면 RunAsync가 null 반환 → 마킹 없이 다음 틱에서 재평가.

            double goal = double.TryParse(_db.GetSetting("Research.GoalPct"), out var g) && g != 0 ? g : 10.0;
            ResearchBrief? brief;
            try { brief = await NightlyResearch.RunAsync(_db, _provider, goal); }
            catch (Exception ex) { Log.Warn($"[Research] 실행 실패: {ex.Message}"); return; }
            if (brief == null) return;                                   // 이미 진행 중 — 다음 틱에서 재시도

            if (_db.GetSetting("Research.Telegram") == "true")
            {
                try { await TelegramNotifier.SendAsync(_db, "🌙 <b>밤사이 연구</b>\n" + NightlyResearch.ComposeText(brief)); }
                catch (Exception ex) { Log.Warn($"[Research] 텔레그램 전송 실패: {ex.Message}"); }
            }
            Log.Info($"[Research] {brief.Scanned}종목 · {brief.Found}개 발굴 · 카트리지 {(string.IsNullOrEmpty(brief.CartridgeFile) ? "없음" : "서명")}");
        }

        // ===== DB 유지보수 (주 1회 자동, 안전 housekeeping) =====
        //  WAL 비대·파편화 정리. VACUUM은 잠금이 길 수 있어 Task.Run으로 타이머에서 오프로드.
        private async Task CheckMaintenanceAsync()
        {
            try
            {
                if (DateTime.TryParse(_db!.GetSetting("Maint.LastRun"), out var last) && (DateTime.Now - last).TotalDays < 7) return;
                _db.SetSetting("Maint.LastRun", DateTime.Now.ToString("o"));   // 먼저 마킹
                var (before, after) = await Task.Run(() => DbMaintenance.Run(_db));
                Log.Info($"[Maint] VACUUM/ANALYZE 완료 · {before}KB → {after}KB");
            }
            catch (Exception ex) { Log.Warn($"[Maint] {ex.Message}"); }
        }

        // ===== 목표주도 코파일럿 🅰 (옵트인 · 게이팅: 제안/알림만) =====
        //  Dashboard.Snapshots(일별 총자산)에서 변동성·낙폭·레짐을 산출해 정책을 평가,
        //  목표 위반 시 행동을 '제안'(시스템 알림 → 인과 저널에 자동 적재). 주문 절대 미접촉.
        private void CheckCopilot()
        {
            try
            {
                if (_db!.GetSetting("Copilot.Enabled") != "true") return;
                var policies = CopilotStore.Load(_db);
                if (policies.Count == 0) return;

                var j = _db.GetSetting("Dashboard.Snapshots");
                if (string.IsNullOrWhiteSpace(j)) return;
                var snaps = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, decimal>>(j);
                if (snaps == null || snaps.Count < 3) return;
                var equity = snaps.OrderBy(k => k.Key, StringComparer.Ordinal).Select(k => k.Value).ToList();

                var ctx = CopilotEngine.BuildCtx(equity);
                var proposals = CopilotEngine.Evaluate(policies, ctx);
                if (proposals.Count == 0) return;

                bool changed = false;
                foreach (var pr in proposals)
                {
                    var pol = policies.FirstOrDefault(x => x.Id == pr.PolicyId);
                    if (pol != null && DateTime.TryParse(pol.LastFired, out var lf) && (DateTime.Now - lf).TotalHours < 24) continue;  // 정책당 24h 쿨다운
                    if (pol != null) { pol.LastFired = DateTime.Now.ToString("o"); changed = true; }
                    AlertCenter.Instance.RaiseSystem("🧑‍✈️ 코파일럿 제안: " + pr.Title, pr.Detail + "  (제안일 뿐 자동 주문 없음)", AlertLevel.Info);
                }
                if (changed) CopilotStore.Save(_db, policies);
            }
            catch (Exception ex) { Log.Warn($"[Copilot] {ex.Message}"); }
        }

        private Dictionary<string, double> LoadDriftTarget()
        {
            try
            {
                var j = _db!.GetSetting("Rebalance.Target");
                if (!string.IsNullOrWhiteSpace(j))
                    return System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, double>>(j)
                           ?? new(StringComparer.OrdinalIgnoreCase);
            }
            catch { }
            return new(StringComparer.OrdinalIgnoreCase);
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
