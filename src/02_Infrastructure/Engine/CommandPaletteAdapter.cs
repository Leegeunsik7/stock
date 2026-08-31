// ============================================================================
// CommandPaletteAdapter + HotkeyBridgeAdapter — Phase 94-L: 커맨드 팔레트 Port 호스트 어댑터 (DIP 구현)
//   CommandPalette 의 8중 결합(DashboardService·ToastService·SymbolContext·AsOfContext·LocalDatabase·
//   CommandPaletteController·ModuleBridge·CartridgeService·NavigationManager)을 흡수. 명령 레지스트리 패턴 —
//   어댑터가 Cmd 목록 + 액션(Func)을 소유하고, UI 는 ICommandView + Id 실행만 안다(엔진 타입/Func 무유입).
//   ★실 발주 경로 0: 명령은 모의(paper)·네비·알림·테마뿐 — 실 발주는 물리 이중 키(CanaryHealthSentinel)라 무관.
//   ★수명: NavigationManager(scoped) 주입 → 어댑터도 scoped. JS interop 안전 센티널은 HotkeyBridge(singleton).
// ============================================================================
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MS2E.UI.Portable.Ports.Portfolio;   // (미사용이지만 일관성)
using MS2E.UI.Portable.Ports.Shell;
using StockManager.Blazor;                // DashboardService, ToastService, ToastButton, CommandPaletteController

namespace MS2E.Infrastructure
{
    internal sealed record CommandViewDto(int Id, string Icon, string Label, string Cat) : ICommandView;

    public sealed class CommandPaletteAdapter : ICommandPalettePort
    {
        private sealed record Cmd(string Icon, string Label, string Cat, Func<Task> Action);

        private readonly DashboardService _svc;
        private readonly ToastService _toasts;
        private readonly SymbolContext _sym;
        private readonly AsOfContext _asof;
        private readonly LocalDatabase _db;
        private readonly Microsoft.AspNetCore.Components.NavigationManager _nav;

        private readonly List<Cmd> _modules = new();
        private Dictionary<string, string> _macros = new();
        private List<Cmd> _lastFiltered = new();   // Filter 결과 캐시(Id → 액션)

        public CommandPaletteAdapter(DashboardService svc, ToastService toasts, SymbolContext sym,
            AsOfContext asof, LocalDatabase db, Microsoft.AspNetCore.Components.NavigationManager nav)
        {
            _svc = svc; _toasts = toasts; _sym = sym; _asof = asof; _db = db; _nav = nav;
            BuildModules();
        }

        private void BuildModules()
        {
            void Mod(string icon, string label, string key) =>
                _modules.Add(new Cmd(icon, label, "이동", () => { ModuleBridge.Open(key); return Task.CompletedTask; }));
            Mod("📈", "실시간 시세", "Realtime"); Mod("💼", "포트폴리오", "Portfolio");
            Mod("🧠", "AI 분석", "AI"); Mod("📰", "뉴스 레이더", "News");
            Mod("⚡", "시그널 엔진", "Signal"); Mod("🔔", "알림 센터", "Alert");
            Mod("🪙", "코인 실시간", "Crypto"); Mod("🧮", "총자산", "TotalAsset");
            Mod("🧪", "모의투자", "Paper"); Mod("🧠", "복합 알림", "Composite");
            Mod("⚙️", "거래 설정", "BrokerSettings"); Mod("⚙️", "설정", "Settings");
            _modules.Add(new Cmd("🌗", "테마 전환 (라이트/다크)", "보기", () => { _svc.Theme = _svc.Theme == "dark" ? "light" : "dark"; return Task.CompletedTask; }));
        }

        public string CurrentTheme => _svc.Theme;
        public string ToggleTheme() { _svc.Theme = _svc.Theme == "dark" ? "light" : "dark"; return _svc.Theme; }
        public void RefreshMacros() { try { _macros = CartridgeService.MacroMap(_db); } catch { } }

        public IReadOnlyList<ICommandView> Filter(string query)
        {
            var quick = new List<Cmd>();
            if (TryMacro(query) is { } mac) quick.Add(mac);
            if (TryParseFunction(query) is { } fnc) quick.Add(fnc);
            if (TryParseAlertsOff(query) is { } off) quick.Add(off);
            if (TryParseCoinAlert(query) is { } ca) quick.Add(ca);
            if (TryParsePaper(query) is { } paper) quick.Add(paper);

            var list = new List<Cmd>(quick);
            list.AddRange(_modules);
            if (!string.IsNullOrWhiteSpace(query))
            {
                var q = query.Trim();
                list = list.Where(c => quick.Contains(c)
                    || c.Label.Contains(q, StringComparison.OrdinalIgnoreCase)
                    || c.Cat.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
            }
            _lastFiltered = list;
            var views = new List<ICommandView>(list.Count);
            for (int i = 0; i < list.Count; i++) views.Add(new CommandViewDto(i, list[i].Icon, list[i].Label, list[i].Cat));
            return views;
        }

        public Task ExecuteAsync(int id)
        {
            if ((uint)id >= (uint)_lastFiltered.Count) return Task.CompletedTask;   // fail-closed
            return _lastFiltered[id].Action();
        }

        // ── 파서(원본 CommandPalette 룰 이관) ────────────────────────────────────
        private Cmd? TryMacro(string q)
        {
            if (string.IsNullOrWhiteSpace(q) || _macros.Count == 0) return null;
            if (!_macros.TryGetValue(q.Trim(), out var exp)) return null;
            var inner = TryParseFunction(exp);
            return new Cmd("⭐", $"매크로: {q.Trim()} → {exp}", "매크로", () => inner?.Action() ?? Task.CompletedTask);
        }

        private Cmd? TryParsePaper(string q)
        {
            if (string.IsNullOrWhiteSpace(q)) return null;
            var parts = q.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3) return null;
            if (!(parts[2].Contains("매수") || parts[2].Contains("매도"))) return null;
            var amt = ParseAmt(parts[1]); if (amt == null) return null;
            var market = "KRW-" + parts[0].ToUpperInvariant();
            var side = parts[2].Contains("매도") ? PaperSide.Sell : PaperSide.Buy;
            return new Cmd("⚡", $"모의 {(side == PaperSide.Buy ? "매수" : "매도")}: {market} {amt:N0}원", "주문",
                () =>
                {
                    var (ok, msg) = _svc.QuickPaper(market, amt.Value, side);
                    if (ok)
                        _toasts.Success("모의 주문 체결", msg + "\n(모의투자 — 실제 주문 아님)", new()
                        {
                            new ToastButton("모의투자 보기", () => { _nav.NavigateTo("/paper"); return Task.CompletedTask; }, Primary: true)
                        });
                    else
                        _toasts.Warning("주문 실패", msg);
                    return Task.CompletedTask;
                });
        }

        private Cmd? TryParseCoinAlert(string q)
        {
            if (string.IsNullOrWhiteSpace(q)) return null;
            var parts = q.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3 || !parts[1].Contains("알림")) return null;
            var amt = ParseAmt(parts[2]); if (amt == null) return null;
            var market = "KRW-" + parts[0].ToUpperInvariant();
            return new Cmd("🔔", $"코인 알림 등록: {market} {amt:N0}원 이상", "액션",
                () =>
                {
                    _svc.AddCoinAlert(market, amt.Value);
                    _toasts.Success("알림 등록", $"{market} {amt:N0}원 이상", new()
                    {
                        new ToastButton("코인 화면", () => { _nav.NavigateTo("/live/coin"); return Task.CompletedTask; })
                    });
                    return Task.CompletedTask;
                });
        }

        private Cmd? TryParseAlertsOff(string q)
        {
            if (string.IsNullOrWhiteSpace(q)) return null;
            var t = q.Trim(); var low = t.ToLowerInvariant();
            if (!t.Contains("알림") || !(t.Contains("끄") || t.Contains("해제") || low.Contains("off"))) return null;
            return new Cmd("🔕", "모든 코인 알림 + 텔레그램 자동전송 끄기", "액션",
                () =>
                {
                    _svc.SetTelegramEnabled(false);
                    _svc.DisableAllCoinAlerts();
                    _toasts.Info("알림 끄기 완료", "텔레그램 자동 전송 OFF · 코인 지정가 알림 모두 제거");
                    return Task.CompletedTask;
                });
        }

        private static readonly Dictionary<string, (string route, string label)> _fn = new(StringComparer.OrdinalIgnoreCase)
        {
            ["RISK"] = ("/portfolio/risk", "리스크 분해"), ["TM"] = ("/timemachine", "타임머신"),
            ["OPT"] = ("/lab/optimizer", "옵티마이저"), ["EV"] = ("/lab/eventstudy", "이벤트 스터디"),
            ["MINE"] = ("/lab/miner", "가설 발굴"), ["SIZE"] = ("/lab/sizer", "포지션 사이저"),
            ["TWIN"] = ("/validate", "디지털 트윈"), ["FC"] = ("/validate/forecast", "예측 스코어보드"),
            ["MC"] = ("/risklab", "몬테카를로"), ["SCN"] = ("/risklab/scenario", "시나리오"),
            ["AI"] = ("/intel/analyze", "AI 분석"), ["DEBATE"] = ("/intel/debate", "멀티 토론"),
            ["Q"] = ("/live", "실시간 차트"), ["CHART"] = ("/live", "실시간 차트"),
        };

        private Cmd? TryParseFunction(string q)
        {
            if (string.IsNullOrWhiteSpace(q)) return null;
            var parts = q.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || !_fn.TryGetValue(parts[1], out var fn)) return null;
            var sym = parts[0].ToUpperInvariant();
            bool looksSym = (sym.Length == 6 && sym.All(char.IsDigit)) || (sym.Length is >= 1 and <= 5 && sym.All(char.IsLetter));
            if (!looksSym) return null;
            var mkt = (sym.Length == 6 && char.IsDigit(sym[0])) ? Market.KRX_KOSPI : Market.US_NASDAQ;
            DateTime? asof = null; string dlabel = "";
            if (parts.Length >= 3 && TryParseAsOf(parts[2], out var dt)) { asof = dt; dlabel = $" · ⏳ {dt:yyyy-MM-dd}"; }
            return new Cmd("🔎", $"{sym} → {fn.label}{dlabel}", "함수",
                () =>
                {
                    _sym.Set(sym, mkt);
                    if (asof.HasValue) _asof.Set(asof.Value);
                    _nav.NavigateTo(fn.route);
                    return Task.CompletedTask;
                });
        }

        private static bool TryParseAsOf(string s, out DateTime dt)
        {
            s = (s ?? "").Trim();
            if (DateTime.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out dt)) return true;
            if (System.Text.RegularExpressions.Regex.IsMatch(s, @"^\d{4}-\d{1,2}$") &&
                DateTime.TryParse(s + "-01", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out dt)) return true;
            dt = default; return false;
        }

        private static decimal? ParseAmt(string s)
        {
            s = s.Replace(",", "").Replace("원", "").Trim();
            decimal mul = 1m;
            if (s.EndsWith("억")) { mul = 100_000_000m; s = s[..^1]; }
            else if (s.EndsWith("만")) { mul = 10_000m; s = s[..^1]; }
            return decimal.TryParse(s, out var v) ? v * mul : (decimal?)null;
        }
    }

    // ── 핫키 브릿지 + JS Interop 안전 센티널(Think-Beyond) ───────────────────────
    public sealed class HotkeyBridgeAdapter : IHotkeyBridgePort
    {
        private readonly CommandPaletteController _controller;
        private readonly DotNetObjectRefSentinel _refSentinel;
        private readonly JSInvokableTokenVerifier _tokenVerifier;

        public HotkeyBridgeAdapter(CommandPaletteController controller,
            DotNetObjectRefSentinel refSentinel, JSInvokableTokenVerifier tokenVerifier)
        {
            _controller = controller;
            _refSentinel = refSentinel;
            _tokenVerifier = tokenVerifier;
            _controller.OpenRequested += () => OpenRequested?.Invoke();
        }

        public event Action? OpenRequested;

        public void RegisterHandle() => _refSentinel.Register();
        public void ReleaseHandle() => _refSentinel.Release();
        public long ActiveHandles => _refSentinel.ActiveHandles;

        public long IssueToken() => _tokenVerifier.Issue();
        public bool VerifyToken(long token) => _tokenVerifier.Verify(token);
        public void RevokeToken() => _tokenVerifier.Revoke();
    }
}
