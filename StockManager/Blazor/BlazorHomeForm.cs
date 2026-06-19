// BlazorHomeForm - WinForms 폼 안에 Blazor 대시보드 호스팅 (Blazor Hybrid, v3.1)
using System.Windows.Forms;
using Microsoft.AspNetCore.Components.WebView.WindowsForms;
using Microsoft.Extensions.DependencyInjection;
using StockManager.Common;

namespace StockManager.Blazor
{
    public class BlazorHomeForm : Form
    {
        public BlazorHomeForm(IQuoteProvider provider, LocalDatabase db)
        {
            Text = "MS2-E Stock Suite";
            WindowState = FormWindowState.Maximized;
            StartPosition = FormStartPosition.CenterScreen;
            Width = 1280; Height = 860;

            ModuleBridge.Configure(provider, db);

            var services = new ServiceCollection();
            services.AddWindowsFormsBlazorWebView();
            services.AddSingleton<IQuoteProvider>(provider);
            services.AddSingleton(db);
            services.AddSingleton<DashboardService>();
            services.AddSingleton<BithumbProvider>();
            services.AddSingleton<NewsService>();
            services.AddSingleton<AiService>();
            services.AddSingleton<FxConverter>();   // ⚠️ Singleton 필수 — 10분 환율 캐시가 수명에 의존
            services.AddSingleton<ToastService>();  // 액션형 전역 토스트 (어느 페이지에서나 주입)
            services.AddSingleton<SymbolContext>();  // 전역 활성 종목 (연구 도구 횡이동 시드)
            services.AddSingleton<AsOfContext>();    // 전역 시점(As-Of) — 글로벌 타임-스크러버
            services.AddSingleton<GlobalIndexService>(); // 전세계 주요 지수 60초 폴링
            services.AddSingleton<ForecastVaultService>(); // S11 #1 — 예측 타임캡슐

            // S14 #2/#3 — 실거래 체결 엔진: 인스턴스를 명시적으로 생성해 세 서비스가 동일 객체를 공유
            var attestation = new OrderAttestationService();

            // S18 #1 — 콜드 하든: 부팅 무결성 검사를 브로커 파이프라인 구성 '이전'에 수행.
            // 이전 세션이 비상 동결이었거나 스냅샷이 손상되었으면 여기서 fail-closed 동결된다.
            var hydration = WireBootIntegrity(attestation);

            var (broker, bridge) = ExecutionBridgeBroker.Create(attestation);
            services.AddSingleton(attestation);            // S12 #2 — 거래 증적 감사 (ForceFreeze 게이트)
            services.AddSingleton(bridge);                 // S14 #1 — KiwoomBridgeClient (하트비트 500ms)
            services.AddSingleton(broker.Audit);           // S14 #2 — LiveAuditService (체결 원장)
            services.AddSingleton(broker);                 // S14 #2 — ExecutionBridgeBroker (IBroker 실거래)
            services.AddSingleton(hydration);              // S18 #1 — 상태 복원 엔진 (관제탑 신선도 조회용)

            var view = new BlazorWebView
            {
                Dock = DockStyle.Fill,
                HostPage = "wwwroot/index.html",
                Services = services.BuildServiceProvider()
            };
            view.RootComponents.Add<App>("#app");
            Controls.Add(view);
        }

        // ── S18 #1: 콜드 하든 배선 ─────────────────────────────────────────────
        private System.Threading.Timer? _snapshotTimer;

        /// <summary>
        /// StateHydrationEngine ↔ OrderAttestationService 를 부팅 시점에 결속한다.
        /// 손상·동결 스냅샷이면 브로커 파이프라인 구성 전에 fail-closed 동결시키고,
        /// 이후 60초 주기로 게이팅 모드를 MMF 에 영속화한다.
        /// </summary>
        private StateHydrationEngine WireBootIntegrity(OrderAttestationService attestation)
        {
            var stateDir = System.IO.Path.Combine(
                System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
                "StockManager");
            try { System.IO.Directory.CreateDirectory(stateDir); } catch { }
            var path = System.IO.Path.Combine(stateDir, "state.hydr");

            var hydration  = new StateHydrationEngine(path);
            var gatingSlot = new HydrationSlot<int>((int)GatingMode.Normal);
            // 스냅샷 시 현재 모드를 라이브 캡처, 복원 시 슬롯에 기록
            hydration.Register<int>("gatingMode",
                () => (int)attestation.Mode,
                v  => gatingSlot.Value = v);

            try
            {
                var report = BootIntegrityChecker.CheckAndHydrate(
                    hydration, attestation, gatingSlot, System.TimeSpan.FromHours(12));
                Log.Info($"[BOOT] 무결성={report.Status} 모드={report.RecoveredMode} " +
                         $"동결적용={report.FrozeOnBoot} — {report.Detail}");

                if (report.FrozeOnBoot)
                    AlertCenter.Instance.RaiseSystem("부팅 안전 동결",
                        $"이전 세션 상태 복원으로 트레이딩이 동결되었습니다. ({report.Status}) " +
                        "/broker/gating 에서 수동 정합 서명 후 해제하세요.", AlertLevel.Critical);

                // 메모리 누수 가드 부팅 베이스라인
                var mem = MemoryLeakGuard.Shared.Probe();
                Log.Info($"[BOOT] 메모리 베이스라인 WS={mem.WorkingSetBytes / 1048576}MB " +
                         $"Heap={mem.ManagedHeapBytes / 1048576}MB LiveLeases={mem.LiveLeases}");
            }
            catch (System.Exception ex)
            {
                // 검사 자체가 실패해도 fail-closed: 안전을 위해 동결
                Log.Error("[BOOT] 무결성 검사 예외 — 안전 동결", ex);
                try { attestation.ForceFreeze("부팅 무결성 검사 예외 — 안전 동결"); } catch { }
            }

            // 부팅 직후 즉시 스냅샷 — 성공·예외 경로 모두에서 결과 상태를 영속화해
            // 다음 스냅샷(60초) 전 재충돌 시의 fail-open 창을 제거한다.
            try { hydration.Snapshot(); } catch { }

            // 60초 주기 상태 스냅샷 (게이팅 모드 변화를 영속화)
            _snapshotTimer = new System.Threading.Timer(
                _ => { try { hydration.Snapshot(); } catch { } },
                null, 60_000, 60_000);

            FormClosed += (_, _) =>
            {
                try { _snapshotTimer?.Dispose(); } catch { }
                try { hydration.Snapshot(); } catch { }   // 종료 시 최종 상태 영속화
            };

            return hydration;
        }
    }
}
