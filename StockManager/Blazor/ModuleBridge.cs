// ModuleBridge — Blazor UI에서 WinForms 레거시 창을 여는 브릿지 (v4.69.0)
//
// v4.69.0 변경:
//   · IModuleBridge 인터페이스 추가 — Blazor DI (@inject IModuleBridge) 를 통해 주입.
//     BlazorHomeForm 이 HostBoundModuleBridge(STA-safe 래퍼)를 AddSingleton 으로 결속.
//   · STA 마샬링 하드닝: Blazor Hybrid 에서 async-await 이후 continuation 이 스레드풀로
//     미끄러질 수 있다. Open() 에서 InvokeRequired 감지 시 BeginInvoke 로 WinForms
//     STA 스레드에 마샬링해 InvalidOperationException/CrossThread 크래시를 차단.
//   · Configure() 에 host Form 파라미터 추가(기본 null — 하위 호환).
//     마샬링 앵커가 없으면 직접 호출(기존 동작 보존).
//
// 정직 표기: 주요 모듈(실시간·포트폴리오·AI 등)은 이제 모두 Blazor 네이티브 라우트를
//   보유한다. DashboardHome.razor 에서는 NavigateTo() 를 쓰고, Bridge.Open() 은
//   WinForms 창이 필수인 엣지 케이스(설정 마법사 등) 전용으로 남는다.
using System.Windows.Forms;
using StockManager.Common;

namespace StockManager.Blazor
{
    // ── DI 주입형 인터페이스: @inject IModuleBridge Bridge ──────────────────────
    public interface IModuleBridge
    {
        void Open(string key);
    }

    // ── 정적 브릿지(하위 호환) — STA 마샬링 내장 ────────────────────────────────
    public static class ModuleBridge
    {
        private static IQuoteProvider _provider = null!;
        private static LocalDatabase  _db       = null!;
        private static Form?          _host;          // STA InvokeRequired 앵커

        /// <summary>BlazorHomeForm 생성자에서 호출. host = BlazorHomeForm 자신.</summary>
        public static void Configure(IQuoteProvider provider, LocalDatabase db, Form? host = null)
        {
            _provider = provider;
            _db       = db;
            _host     = host;
        }

        /// <summary>
        /// 지정 키의 WinForms 창을 연다. Blazor async 컨텍스트에서 호출해도 안전:
        /// InvokeRequired 시 BeginInvoke 로 STA 마샬링 후 반환.
        /// </summary>
        public static void Open(string key)
        {
            if (_host is { InvokeRequired: true })
            {
                _host.BeginInvoke(() => OpenInner(key));
                return;
            }
            OpenInner(key);
        }

        private static void OpenInner(string key)
        {
            void T(Form f) { try { ThemeManager.Apply(f); } catch { } f.Show(); f.BringToFront(); }
            switch (key)
            {
                case "Realtime":       T(new RealtimeWatcher.MainForm(_provider, _db)); break;
                case "Portfolio":      T(new Portfolio.MainForm(_provider, _db));       break;
                case "AI":             T(new AIAnalysis.MainForm(_provider, _db));      break;
                case "News":           T(new NewsRadar.MainForm(_db));                  break;
                case "Signal":         T(new SignalEngine.MainForm(_provider, _db));    break;
                case "Alert":          T(new AlertCenterForm(_db));                     break;
                case "Crypto":         T(new CryptoWatcher.MainForm(_db));             break;
                case "TotalAsset":     T(new TotalAsset.MainForm(_provider, _db));     break;
                case "Paper":          T(new PaperTrading.MainForm(_provider, _db));   break;
                case "Composite":      T(new CompositeAlerts.MainForm(_db, _provider)); break;
                case "BrokerSettings": T(new BrokerSettingsForm(_db));                 break;
                case "AlertHistory":   T(new AlertHistoryForm(_db));                   break;
                case "Settings":       new SetupWizard(_db).ShowDialog();              break;
            }
        }
    }
}
