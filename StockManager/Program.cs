// ============================================================================
// MS2-E Stock Suite v2.2 - 엔트리포인트
// ============================================================================
using System;
using System.Windows.Forms;
using StockManager.Common;

namespace StockManager
{
    internal static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            ApplicationConfiguration.Initialize();

            // 전역 예외 핸들러
            Application.ThreadException += (s, e) =>
            {
                Log.Error("UI 스레드 예외", e.Exception);
                MessageBox.Show($"예상치 못한 오류:\n\n{e.Exception.Message}\n\n로그: {Log.LogDir}",
                    "오류", MessageBoxButtons.OK, MessageBoxIcon.Error);
            };
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                if (e.ExceptionObject is Exception ex) Log.Error("처리되지 않은 예외", ex);
            };

            Log.Info($"===== MS2-E Stock Suite {AppVersion()} 시작 =====");

            // QuestPDF 라이센스 (Community MIT) - 최초 한 번만
            try
            {
                QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;
            }
            catch { /* 미설치 시 무시 - 주간 리포트만 사용 안 됨 */ }

            // DB 초기화
            var db = new LocalDatabase();

            // 보안(at-rest): 레거시 또는 DPAPI 폴백으로 평문 저장된 민감 키를 DPAPI로 재암호화
            db.MigrateSecretsAtRest(new[]
            {
                "Gemini.ApiKey", "KIS.AppKey", "KIS.AppSecret", "KIS.Account",
                "Upbit.AccessKey", "Upbit.SecretKey", "Telegram.Token"
            });

            // 테마 로드 (블랙/화이트)
            ThemeManager.Load(db);

            // 알림 센터 연결
            AlertCenter.Instance.Attach(db);
            TrayNotifier.Initialize();

            // 텔레그램 (Critical 레벨만 자동 전송)
            TelegramNotifier.AttachToAlertCenter(db);

            // 최초 실행 온보딩 — 설정 위저드 (provider 구성 전에 실행해 KIS 키 반영)
            if (db.GetSetting("Setup.Completed") != "true")
            {
                Log.Info("[Setup] 최초 실행 — 설정 마법사 표시");
                using (var wizard = new SetupWizard(db)) wizard.ShowDialog();
                db.SetSetting("Setup.Completed", "true"); // 취소해도 다음 실행부터 자동 표시 안 함
            }

            // 데이터 프로바이더 구성 (KIS 키가 있으면 KIS 우선)
            var naver = new NaverFinanceProvider();
            var yahoo = new YahooFinanceProvider();
            IQuoteProvider provider;

            var kisKey = db.GetSecret("KIS.AppKey");
            var kisSecret = db.GetSecret("KIS.AppSecret");
            if (!string.IsNullOrEmpty(kisKey) && !string.IsNullOrEmpty(kisSecret))
            {
                var kis = new KisProvider(new KisConfig
                {
                    AppKey = kisKey,
                    AppSecret = kisSecret,
                    AccountNo = db.GetSetting("KIS.AccountNo") ?? "",
                    IsPaper = db.GetSetting("KIS.IsPaper") != "false"
                });
                provider = new QuoteAggregator(kis, naver, yahoo);
                Log.Info("Provider: KIS + Naver + Yahoo");
            }
            else
            {
                provider = new QuoteAggregator(naver, yahoo);
                Log.Info("Provider: Naver + Yahoo");
            }

            // 시작 알림
            AlertCenter.Instance.RaiseSystem("시스템 시작",
                $"MS2-E Stock Suite {AppVersion()}가 시작되었습니다.", AlertLevel.Info);

            // 복합 알림 백그라운드 평가 서비스 기동(관리창 개폐와 무관하게 상시)
            CompositeAlertService.Instance.Start(db, provider);

            // 알림·체결 엔진 상시 기동 — 코인 지정가(재무장)/주식 AlertRules(one-shot)/모의 지정가 체결
            // (해당 WinForms 창이 열려 있으면 그 창이 처리하므로 자동 스킵)
            AlertEngineService.Instance.Start(db, provider);

            // 읽기전용 LAN 상태 서버(옵트인 — Lan.Enabled=="true"일 때만 시작)
            LanStatusServer.Instance.Start(db);

            void Cleanup()
            {
                LanStatusServer.Instance.Stop();
                AlertEngineService.Instance.Stop();
                CompositeAlertService.Instance.Stop();
                TrayNotifier.Dispose();
                db.Dispose();
                Log.Info("===== 종료 =====");
            }

            bool classic = args is { Length: > 0 } && args[0].Equals("--classic", StringComparison.OrdinalIgnoreCase);

            if (!classic)
            {
                try
                {
                    // Blazor 모드에는 클래식 런처가 없으므로 트레이 토스트를 직접 구독(경고 이상)
                    AlertCenter.Instance.AlertRaised += (s, a) =>
                    {
                        if (a.Level >= AlertLevel.Warning) { try { TrayNotifier.Show(a); } catch { } }
                    };
                    // 새 Blazor Hybrid 대시보드 (WebView2 런타임 필요)
                    var home = new StockManager.Blazor.BlazorHomeForm(provider, db);
                    home.FormClosed += (s, e) => Cleanup();
                    Application.Run(home);
                    return;
                }
                catch (Exception ex)
                {
                    Log.Warn($"[Blazor] 홈 시작 실패 → 클래식 런처로 대체: {ex.Message}");
                    // WebView2 미설치 등 → 아래 클래식 런처로 폴백
                }
            }

            var launcher = new LauncherForm(provider, db);
            launcher.FormClosed += (s, e) => Cleanup();
            Application.Run(launcher);
        }

        // 표시용 현재 버전 — 이후엔 csproj <Version> 한 곳만 올리면 로그·시작 알림에 자동 반영
        private static string AppVersion()
        {
            var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            return v == null ? "v3" : $"v{v.Major}.{v.Minor}.{v.Build}";
        }
    }
}
