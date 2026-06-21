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
            Func<string, string?> rawConfig = key => Environment.GetEnvironmentVariable(key) ?? db.GetSetting(key);
            var cfg = new RuntimeConfig(rawConfig);
            services.AddSingleton(cfg);

            // S22 #2 — 부팅 설정 무결성 감사: 무위험 설정의 출처를 검사해 redacted 스냅샷을 원장에 남기고,
            // 잘못된 값이 안전 폴백/클램프됐으면(이상) 관제탑에 [⚠️ 설정 복구] 경보를 점등한다.
            // ConfigAuditor 는 RuntimeConfig 와 '동일 provider'를 공유 → 감사 스냅샷 = 런타임 실효값.
            var configAudit = new ConfigAuditor(rawConfig).Audit(BootConfigSpecs.All);
            Log.Info($"[BOOT] 설정 무결성 — {configAudit.Summary()}");
            if (configAudit.HasAnomaly)
            {
                foreach (var c in configAudit.Coerced)
                    Log.Warn($"[BOOT] ⚠️ 설정 복구: {c.Label}({c.Key}) — {c.Detail} → 안전값 {c.Display}");
                AlertCenter.Instance.RaiseSystem("설정 복구",
                    $"{configAudit.CoercedCount}개 설정이 잘못되어 안전 값으로 클램프됐습니다. /command 에서 확인하세요.",
                    AlertLevel.Warning);
            }
            services.AddSingleton(configAudit);

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
            var (hydration, recoveryJournaler) = WireBootIntegrity(attestation, cfg);

            var (broker, bridge) = ExecutionBridgeBroker.Create(attestation);
            services.AddSingleton(attestation);            // S12 #2 — 거래 증적 감사 (ForceFreeze 게이트)
            services.AddSingleton(bridge);                 // S14 #1 — KiwoomBridgeClient (하트비트 500ms)
            services.AddSingleton(broker.Audit);           // S14 #2 — LiveAuditService (체결 원장)
            services.AddSingleton(broker);                 // S14 #2 — ExecutionBridgeBroker (IBroker 실거래)
            services.AddSingleton(hydration);              // S18 #1 — 상태 복원 엔진 (관제탑 신선도 조회용)
            services.AddSingleton(recoveryJournaler);      // S24-b — 크래시 복구 저널(부팅 검증·플러시 펌프 공유)

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

            // S23-c — 비가역 거부 포렌식 감사기(임계구역 내 무할당 박제·락-프리 독출). 게이트웨이·관제탑 공유.
            var rejectionForensics = new RejectionForensicAuditor();
            services.AddSingleton(rejectionForensics);

            // S24-b — 비동기 플러시 펌프(옵트인 기본 OFF). 핫패스는 RequestFlush(Interlocked) 한 줄만,
            //   격리 백그라운드가 ring→MMF 영속화. OFF면 펌프 null → 저널 미기록 → 부팅 항상 Empty(후방호환).
            JournalFlushPump? flushPump = null;
            if (cfg.GetBool(BootConfigSpecs.RecoveryPumpEnabledKey, BootConfigSpecs.RecoveryPumpEnabledDefault))
            {
                // S25-b — 복합 포맷 펌프: 원자 세션(genome+forensic 단일 seqlock) 스냅샷 → PersistComposite.
                // 사전 할당 스크래치 클로저 → per-flush 무할당. 펌프 백그라운드 단일 스레드에서만 실행.
                var scratchRej     = new RejectionRecord[recoveryJournaler.Capacity];
                var scratchJournal = new JournalRecord[recoveryJournaler.Capacity];
                flushPump = new JournalFlushPump(() =>
                {
                    int n = rejectionForensics.SnapshotSession(scratchRej, out var genome);   // ★원자 세션(split-brain 봉인)
                    for (int i = 0; i < n; i++)
                    {
                        var r = scratchRej[i];
                        scratchJournal[i] = new JournalRecord(r.Seq, RecoveryKind.Rejection, (byte)r.Stage, 0,
                            r.AsOfTicks, r.NowTicks, r.WindowTicks, r.Fitness, JournalRecord.HashId(r.SpecId));
                    }
                    return recoveryJournaler.PersistComposite(genome, scratchJournal.AsSpan(0, n));
                });
                flushPump.Start();
                services.AddSingleton(flushPump);
            }

            // S25-a — 라이브 캔들 인그레스 옵트인(기본 OFF). ON 시 온실에 staleness fail-closed + isDirty 게이트 결속.
            bool liveFeed = cfg.GetBool(BootConfigSpecs.GreenhouseLiveFeedKey, BootConfigSpecs.GreenhouseLiveFeedDefault);

            // S19 #3/#4/#5 — 승급 게이트웨이 + 격리 배양 온실 + 원자 핫스왑 슬롯 + 집행 안전 게이트
            var promotionGateway = new PromotionGateway(db, attestation)
            {
                Forensics      = rejectionForensics,
                OnLedgerChanged = flushPump is null ? (Action?)null : flushPump.RequestFlush,   // S24-c 전방위 트리거(승인·활성·거부)
            };
            var greenhouse       = new GreenhouseService(promotionGateway, attestation, MemoryLeakGuard.Shared)
            {
                Sentinel = sentinel,   // S19 #5 — 실시간 마찰 피드 결속(ProxyMeanAbsZ 제거)
                CycleIntervalMs = cfg.GetInt(BootConfigSpecs.GreenhouseCycleMsKey,   // S22 #2 — 검증 경로 라우팅
                    BootConfigSpecs.GreenhouseCycleMsDefault,
                    BootConfigSpecs.GreenhouseCycleMsMin, BootConfigSpecs.GreenhouseCycleMsMax),
                // S24-b/S25-b — 챔피언 발행: 게놈 스냅샷을 원자 세션에 발행(auditor) + 플러시 트리거.
                OnChampionPromoted = flushPump is null ? (Action?)null : () =>
                {
                    var cur = promotionGateway.Current;
                    if (cur is not null)
                        rejectionForensics.PublishGenome(new GenomeSnapshot(
                            cur.SubmittedUtc.Ticks, JournalRecord.HashId(cur.Spec.Id),
                            cur.Fitness, cur.AsOfTicks, (byte)promotionGateway.State));
                    flushPump.RequestFlush();
                },
                // S25-a — 라이브 시 데이터 신선도 fail-closed(한도 초과 배양 정지) + isDirty 숏서킷. OFF면 합성 모드 유지.
                SkipUnchanged   = liveFeed,
                StalenessWindow = liveFeed
                    ? System.TimeSpan.FromSeconds(cfg.GetInt("Greenhouse.FeedStaleSec", 90, 10, 3600))
                    : (System.TimeSpan?)null,
            };
            var strategySlot = new StrategySlot();
            // ⚠️ IsDryRun=true 하드 박제 — 라이브 발주는 코드 변경 없이는 도달 불가(§7 안전 경계).
            //    디싱크 시 algoGate 가 attestation 까지 비상 동결시켜 ExecutionBridgeBroker 도 함께 차단.
            var algoGate = new AlgoExecutionGate(strategySlot, attestation, broker) { IsDryRun = true };
            services.AddSingleton(promotionGateway);
            services.AddSingleton(greenhouse);
            services.AddSingleton(strategySlot);
            services.AddSingleton(algoGate);

            // S23 — 자율 배양 엔진 점등(부팅 시). Greenhouse.AutoStart=false 면 휴면 유지(운영자 제어).
            // ⚠️ 안전: 온실은 '제안'만(PromotionGateway 승인 대기). 실행은 인간 승인 → 슬롯 → algoGate
            //    (IsDryRun=true) → ShadowMetricsBridge(liveArmed=false)를 거쳐야 하므로 실거래 0%.
            //    EmergencyFreeze 시 배양 자동 정지(fail-closed). 종료 시 스레드 합류.
            var greenhouseActivator = new GreenhouseActivator(greenhouse);
            if (cfg.GetBool(BootConfigSpecs.GreenhouseAutoStartKey, BootConfigSpecs.GreenhouseAutoStartDefault))
                greenhouseActivator.Activate(Market.KRX_KOSPI);
            services.AddSingleton(greenhouseActivator);
            // 종료 순서: 온실 정지(라이터 차단) → 펌프 Dispose(Join 후 최종 플러시). 동시 라이터 torn write 차단.
            FormClosed += (_, _) => greenhouseActivator.Deactivate();
            FormClosed += (_, _) => { try { flushPump?.Dispose(); } catch { } };

            // S25-a — 라이브 캔들 인그레스(옵트인). 방벽: 네트워크는 지연·고갈만(→staleness fail-closed),
            //   결정 오염 불가(→BulkheadCandleSource 0반환·LiveCandleFeed 검증 드롭). 실거래선(liveArmed:false) 무관.
            //   ⚠️ 실 provider 왕복은 CI 미검증 — 코어 계약은 fake 로 검증, 실환경 검증은 사용자 측.
            if (liveFeed)
            {
                var feedSymbol = cfg.GetString("Greenhouse.FeedSymbol", "069500");   // 기본 KODEX 200 ETF
                var liveSource = new BulkheadCandleSource(provider, feedSymbol, Market.KRX_KOSPI);
                var liveFeedPipe = new LiveCandleFeed(greenhouse, liveSource, GreenhouseActivator.DefaultSeeds(), Market.KRX_KOSPI);
                var ingress = new LiveIngressDriver(liveFeedPipe,
                    intervalMs:     cfg.GetInt("Greenhouse.FeedIntervalSec", 60, 5, 3600) * 1000,
                    staleProbe:     () => greenhouse.FeedStale,
                    onStaleChanged: stale =>
                    {
                        if (stale)
                            AlertCenter.Instance.RaiseSystem("온실 데이터 피드 단절",
                                "라이브 캔들 staleness 한도 초과 — 배양 fail-closed. 피드 복구 시 자동 재개.",
                                AlertLevel.Warning);
                    });
                ingress.Start();
                services.AddSingleton(ingress);
                FormClosed += (_, _) => { try { ingress.Dispose(); } catch { } };
            }

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
                attestation, MemoryLeakGuard.Shared, tuner, bridge, promotionGateway, rejectionForensics));

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
        private (StateHydrationEngine Hydration, CrashRecoveryJournaler Journaler) WireBootIntegrity(
            OrderAttestationService attestation, RuntimeConfig cfg)
        {
            // S22 #2 — 무위험 부팅 튜너블을 검증 경로로 라우팅(BootConfigSpecs 와 동일 키·폴백·바운드).
            int freshnessHours = cfg.GetInt(BootConfigSpecs.FreshnessWindowHoursKey,
                BootConfigSpecs.FreshnessWindowHoursDefault,
                BootConfigSpecs.FreshnessWindowHoursMin, BootConfigSpecs.FreshnessWindowHoursMax);
            int snapshotSec    = cfg.GetInt(BootConfigSpecs.SnapshotIntervalSecKey,
                BootConfigSpecs.SnapshotIntervalSecDefault,
                BootConfigSpecs.SnapshotIntervalSecMin, BootConfigSpecs.SnapshotIntervalSecMax);

            var stateDir = System.IO.Path.Combine(
                System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
                "StockManager");
            try { System.IO.Directory.CreateDirectory(stateDir); } catch { }
            var path        = System.IO.Path.Combine(stateDir, "state.hydr");
            var journalPath = System.IO.Path.Combine(stateDir, "state.jrnl");

            var hydration  = new StateHydrationEngine(path);
            // S24-b — 크래시 복구 저널. 부팅 시 무결성 검증(오염→동결·접근불가→무동결·자가당착 차단).
            // 펌프 OFF면 파일 미기록 → 항상 Empty → RunSafeBoot 은 CheckAndHydrate 단독과 바이트 동일.
            var journaler   = new CrashRecoveryJournaler(journalPath);
            var journalDest = new JournalRecord[journaler.Capacity];   // 부팅 1회 할당(복구 대상)
            var gatingSlot = new HydrationSlot<int>((int)GatingMode.Normal);
            // 스냅샷 시 현재 모드를 라이브 캡처, 복원 시 슬롯에 기록
            hydration.Register<int>("gatingMode",
                () => (int)attestation.Mode,
                v  => gatingSlot.Value = v);

            try
            {
                // S24-b/S25-b — 가역적 안전 부팅: (1)복합 저널 무결성 → (3)하이드레이션 게이트(단조-동결).
                // composite:true — 복합 포맷 복구. 구 플랫(v1·MS2C)·버전 스큐는 Empty(마이그레이션·무동결),
                // I/O 접근불가도 비동결(자가당착 차단), 내용 오염만 fail-closed. 파일 부재 → Empty(클린).
                var boot = BootOrchestrator.RunSafeBoot(
                    hydration, attestation, gatingSlot, journaler, journalDest,
                    System.TimeSpan.FromHours(freshnessHours), composite: true);
                Log.Info($"[BOOT] {boot.Detail} 모드={boot.Integrity.RecoveredMode} 동결적용={boot.Integrity.FrozeOnBoot}");

                if (boot.Safety == BootSafety.Frozen)
                    AlertCenter.Instance.RaiseSystem("부팅 안전 동결",
                        $"부팅 무결성/크래시 저널 검증으로 트레이딩이 동결되었습니다. " +
                        $"(무결성={boot.Integrity.Status} 저널={boot.Journal}) " +
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

            // 주기적 상태 스냅샷 (게이팅 모드 변화를 영속화) — 주기는 검증 경로에서 라우팅(기본 60초)
            int snapshotMs = snapshotSec * 1000;
            _snapshotTimer = new System.Threading.Timer(
                _ => { try { hydration.Snapshot(); } catch { } },
                null, snapshotMs, snapshotMs);

            FormClosed += (_, _) =>
            {
                try { _snapshotTimer?.Dispose(); } catch { }
                try { hydration.Snapshot(); } catch { }   // 종료 시 최종 상태 영속화
            };

            return (hydration, journaler);
        }
    }
}
