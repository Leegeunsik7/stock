// ModuleBridge - Blazor UI에서 기존 WinForms 모듈 창을 그대로 여는 다리 (v3.1)
using System.Windows.Forms;
using StockManager.Common;

namespace StockManager.Blazor
{
    public static class ModuleBridge
    {
        private static IQuoteProvider _provider = null!;
        private static LocalDatabase _db = null!;
        public static void Configure(IQuoteProvider provider, LocalDatabase db) { _provider = provider; _db = db; }

        public static void Open(string key)
        {
            void T(Form f) { try { ThemeManager.Apply(f); } catch { } f.Show(); f.BringToFront(); }
            switch (key)
            {
                case "Realtime": T(new RealtimeWatcher.MainForm(_provider, _db)); break;
                case "Portfolio": T(new Portfolio.MainForm(_provider, _db)); break;
                case "AI": T(new AIAnalysis.MainForm(_provider, _db)); break;
                case "News": T(new NewsRadar.MainForm(_db)); break;
                case "Signal": T(new SignalEngine.MainForm(_provider, _db)); break;
                case "Alert": T(new AlertCenterForm(_db)); break;
                case "Crypto": T(new CryptoWatcher.MainForm(_db)); break;
                case "TotalAsset": T(new TotalAsset.MainForm(_provider, _db)); break;
                case "Paper": T(new PaperTrading.MainForm(_provider, _db)); break;
                case "Composite": T(new CompositeAlerts.MainForm(_db, _provider)); break;
                case "BrokerSettings": T(new BrokerSettingsForm(_db)); break;
                case "AlertHistory": T(new AlertHistoryForm(_db)); break;
                case "Settings": new SetupWizard(_db).ShowDialog(); break;
            }
        }
    }
}
