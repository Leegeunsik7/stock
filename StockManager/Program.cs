// ============================================================================
// MS2-E Stock Suite v2.2 - 엔트리포인트
// ============================================================================
using System;
using System.Windows.Forms;

namespace StockManager
{
    internal static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            // [봉인 운영] 릴리스 봉인 수검 CLI — UI·DB·로그·뮤텍스 초기화 이전 분기(부작용 0 · 종료 코드로 판정 전달).
            if (StockManager.Tools.ReleaseVerificationCli.IsInvocation(args))
            {
                Environment.ExitCode = StockManager.Tools.ReleaseVerificationCli.Run(args, Console.Out);
                return;
            }
            // [릴리스 CI] 출하 zip 무추출 수검 · 다중 저장소 매니페스트 동기화/핸드셰이크 — 동일하게 UI 초기화 이전 분기.
            if (StockManager.Tools.ReleaseHandshakeCli.IsInvocation(args))
            {
                Environment.ExitCode = StockManager.Tools.ReleaseHandshakeCli.Run(args, Console.Out);
                return;
            }
            // [자율 UI 검증] 헤드리스 UI 스위치 퍼징 수검 — 운영 DB/UI/뮤텍스 초기화 이전 분기(일회용 DB · 실 발주 경로 0).
            if (StockManager.Tools.UiSwitchVerifierCli.IsInvocation(args))
            {
                Environment.ExitCode = StockManager.Tools.UiSwitchVerifierCli.Run(args, Console.Out);
                return;
            }

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

            // S22 #3 — 로그 비밀 차폐: 실 비밀값을 LogRedactor 에 등록 → 이후 모든 Log 출력(파일·예외
            // 덤프 포함)에서 평문 마스킹. 비밀이 로그/덤프에 생으로 남는 경로를 프로세스 전역에서 차단.
            RegisterLogSecrets(db);

            // S22 #3-b — 콘솔 봉합선 밀봉: 서드파티 라이브러리(WebView2·Sqlite 등)가 stdout/stderr 로
            // 직접 써도 LogRedactor 를 통과하도록 Console 출력을 래핑. 앱 외 출력까지 비밀 차폐.
            System.Console.SetOut(new ScrubbingTextWriter(System.Console.Out, LogRedactor.Shared));
            System.Console.SetError(new ScrubbingTextWriter(System.Console.Error, LogRedactor.Shared));

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
                RegisterLogSecrets(db);   // 최초 실행 갭 차단 — 방금 입력된 비밀을 즉시 등록(멱등)
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
            // S22 PathA — 트레이 호스트에도 RuntimeConfig 검증 파사드를 주입(env→DB). Blazor 호스트와
            // 동일 BootConfigSpecs SSOT·동일 provider 의미 → 인프라 설정 파이프라인 단일화.
            var cfg = new RuntimeConfig(key => Environment.GetEnvironmentVariable(key) ?? db.GetSetting(key));
            LanStatusServer.Instance.Start(db, cfg);

            void Cleanup()
            {
                LanStatusServer.Instance.Stop();
                AlertEngineService.Instance.Stop();
                CompositeAlertService.Instance.Stop();
                TrayNotifier.Dispose();
                db.Dispose();
                Log.Info("===== 종료 =====");
            }

            bool Has(string flag) => args is { Length: > 0 } && Array.Exists(args, a => a.Equals(flag, StringComparison.OrdinalIgnoreCase));
            bool classic = Has("--classic");
            bool headless = Has("--headless");

            // 헤드리스: UI(WebView) 없이 엔진 + LAN 서버만 트레이로 상주 (상시 켜두는 홈 서버)
            if (headless)
            {
                Log.Info("[Headless] UI 없이 엔진/LAN 서버 상주 모드");
                RunHeadless(db, Cleanup);
                return;
            }

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

        // S22 #3 — 실 비밀값을 LogRedactor 에 등록. DPAPI 비밀 + 민감 평문 식별자(계좌번호).
        // 이후 모든 Log 출력은 이 값들을 어느 라인에 박혀 있든 마스킹한다.
        private static void RegisterLogSecrets(LocalDatabase db)
        {
            try
            {
                // [PII 하드닝] 키 목록 단일 원천(LogSecretCatalog) — 교정 전 인라인 6키는 Toss.ClientId/ClientSecret/Account·KIS.Account·
                //   Email.Pass·Cartridge.PrivKey 누락. 자격증명은 RawCredentialLeakInterlock 에도 등록(원문 유출 시도 → 토큰 소거·무장 차단).
                var (creds, ids) = LogSecretCatalog.RegisterAll(db.GetSecret, db.GetSetting, LogRedactor.Shared, RawCredentialLeakInterlock.Shared);
                Log.Info($"[보안] 로그 비밀 차폐 활성 — 등록 {LogRedactor.Shared.Count}건(자격증명 {creds} · 식별자 {ids})");
            }
            catch (Exception ex) { Log.Error("[보안] 로그 비밀 등록 실패", ex); }
        }

        // 헤드리스 상주 — 트레이 아이콘 + 메시지 루프(UI 폼 없음). LAN 서버는 Lan.Enabled일 때만 동작.
        private static void RunHeadless(LocalDatabase db, Action cleanup)
        {
            var lan = LanStatusServer.Instance;
            lan.Start(db);   // 멱등 — 이미 위에서 시작됐으면 무시

            var tray = new NotifyIcon
            {
                Icon = System.Drawing.SystemIcons.Information,
                Visible = true,
                Text = $"MS2-E {AppVersion()} (헤드리스)"
            };
            var menu = new ContextMenuStrip();
            menu.Items.Add(new ToolStripMenuItem(lan.Running ? $"🟢 LAN 서버 :{lan.Port}" : "⚪ LAN 비활성 (UI 모드에서 설정)") { Enabled = false });
            menu.Items.Add(new ToolStripMenuItem("대시보드 열기", null, (s, e) =>
            {
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo($"http://localhost:{lan.Port}") { UseShellExecute = true }); }
                catch { }
            }) { Enabled = lan.Running });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("종료", null, (s, e) => { try { tray.Visible = false; } catch { } cleanup(); Application.Exit(); }));
            tray.ContextMenuStrip = menu;

            tray.BalloonTipTitle = $"MS2-E {AppVersion()} · 헤드리스";
            tray.BalloonTipText = lan.Running
                ? $"LAN 서버 실행 중 :{lan.Port} (읽기전용). 트레이 우클릭 → 대시보드 열기"
                : "엔진 상주 중. LAN 서버는 UI 모드에서 활성화·PIN 설정 후 사용하세요.";
            try { tray.ShowBalloonTip(5000); } catch { }

            Application.Run();   // 폼 없는 메시지 루프 (트레이 '종료'까지 유지)
        }
    }
}
