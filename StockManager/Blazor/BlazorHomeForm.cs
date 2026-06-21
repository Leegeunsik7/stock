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
            // 설정 격리·런타임 보안 파사드 — precedence: 환경변수 → DB 설정 → (호출자 폴백).
            // 검증·바운드·fail-safe 접근. ⚠️ liveArmed 는 의도적으로 미노출(코드 게이트 전용).
            services.AddSingleton(new RuntimeConfig(key =>
                Environment.GetEnvironmentVariable(key) ?? db.GetSetting(key)));
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

            // S18 #2 — S15~17 알고/컴플라이언스 스택을 라이브 호스트에 결속해 관제 대상 센서로 노출.
            // (라이브 알고 집행 피드가 연결되기 전에는 휴면 상태이나, 센서 배선은 정합하게 가동된다.)
            var sentinel   = new SlippageSentinel();
            var friction   = new FrictionFeedbackBridge(sentinel);
            var tuner      = new AdaptiveExecutionTuner(friction, 4, System.TimeSpan.FromSeconds(30));
            var compliance = new ComplianceBridge();
            compliance.Initialize(db);
            compliance.Attach(sentinel);   // 슬리피지 경보 → FraudAlerts(SENTINEL/HIGH)
            compliance.Wire(tuner);        // 캡 연속 위반 → FraudAlerts(TUNER/CRITICAL) + 안전 동결
            services.AddSingleton(sentinel);
            services.AddSingleton(tuner);
            services.AddSingleton(compliance);

            // S19 #3/#4/#5 — 승급 게이트웨이 + 격리 배양 온실 + 원자 핫스왑 슬롯 + 집행 안전 게이트
            var promotionGateway = new PromotionGateway(db, attestation);
            var greenhouse       = new GreenhouseService(promotionGateway, attestation, MemoryLeakGuard.Shared)
            {
                Sentinel = sentinel,   // S19 #5 — 실시간 마찰 피드 결속(ProxyMeanAbsZ 제거)
            };
            var strategySlot = new StrategySlot();
            // ⚠️ IsDryRun=true 하드 박제 — 라이브 발주는 코드 변경 없이는 도달 불가(§7 안전 경계).
            //    디싱크 시 algoGate 가 attestation 까지 비상 동결시켜 ExecutionBridgeBroker 도 함께 차단.
            var algoGate = new AlgoExecutionGate(strategySlot, attestation, broker) { IsDryRun = true };
            services.AddSingleton(promotionGateway);
            services.AddSingleton(greenhouse);
            services.AddSingleton(strategySlot);
            services.AddSingleton(algoGate);

            // S20 #4 — 드라이런 종단 조립체: 게이트 + 가상 멀티계좌 메시 결속.
            // 기본 단일 가상계좌(primary) — 다중 동시 전략 활성화 시 메시를 확장한다.
            var accountMesh  = new MultiAccountMesh(new[] { new SubAccountSpec("primary", 1m) });
            var shadowRouter = new ShadowExecutionRouter(algoGate, accountMesh);
            services.AddSingleton(accountMesh);
            services.AddSingleton(shadowRouter);

            // v5.0.0-a/b — 점화 제어반 두뇌. 부팅 기본 Locked. 3σ 경보 시 자동 후퇴.
            var ignition = new IgnitionLock(attestation: attestation);
            ignition.Attach(sentinel);
            services.AddSingleton(ignition);

            // S21 #2/#3 — 그림자 추적기 + 2중 게이트 핫-밸브: 키움 실시간 틱·체결 미러링.
            // ⚠️ liveArmed:false 박제 — 조종석으로 점화(Arm)·프록시 연결을 해도 마스터 플래그가
            //    OFF 인 한 실거래는 0(2중 게이트 미충족). 실 점화는 이 플래그를 의도적으로
            //    flip 하는 최종 단계에서만 일어난다(비가역 선은 마지막까지 미교차).
            var tickMirror    = new RingBufferCore<TickPayload>(4096);
            var shadowMetrics = new ShadowMetricsBridge(
                tickMirror, sentinel, realBroker: broker, ignition: ignition, liveArmed: false);
            shadowMetrics.Attach(bridge);
            services.AddSingleton(tickMirror);
            services.AddSingleton(shadowMetrics);
            // 주: 배양 스레드 Start 및 시그널→주문 루프 점화는 운영자 명시 가동에 둔다(드라이런 기본).

            // S18 #2 / S19 #4 — 중앙 관제 허브: 5대 핵심 센서를 단일 불변 스냅샷으로 취합
            services.AddSingleton(new SystemStatusAggregator(
                attestation, MemoryLeakGuard.Shared, tuner, bridge, promotionGateway));

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
