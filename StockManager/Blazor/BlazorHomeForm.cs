// BlazorHomeForm - WinForms 폼 안에 Blazor 대시보드 호스팅 (Blazor Hybrid, v3.1)
using System.Windows.Forms;
using Microsoft.AspNetCore.Components.WebView.WindowsForms;
using Microsoft.Extensions.DependencyInjection;
using MS2E.UI.Portable.Bus;
using MS2E.UI.Portable.Ports;
using MS2E.UI.Portable.Security;
using MS2E.UI.Portable.Infrastructure;

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

            // v4.69.0 — host(this) 주입: Open() 내부 InvokeRequired STA 마샬링 앵커.
            ModuleBridge.Configure(provider, db, this);

            var services = new ServiceCollection();
            services.AddWindowsFormsBlazorWebView();
            // v4.69.0 — IModuleBridge DI 결속: Blazor 컴포넌트가 @inject IModuleBridge 로 획득.
            //   HostBoundModuleBridge 는 ModuleBridge.Open() 정적 브릿지를 위임(STA 마샬링 포함).
            services.AddSingleton<IModuleBridge>(new HostBoundModuleBridge());
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
            services.AddSingleton<UpbitProvider>();   // 종목 검색(SymbolPicker)·코인 시세(포트폴리오) 공유
            services.AddSingleton<ISymbolSearch, NaverSymbolSearch>();   // 라이브 전 종목 검색(코스피·코스닥·미국) — SymbolPicker

            services.AddSingleton<NewsService>();
            services.AddSingleton<AiService>();
            services.AddSingleton<FxConverter>();   // ⚠️ Singleton 필수 — 10분 환율 캐시가 수명에 의존
            services.AddSingleton<ToastService>();  // 액션형 전역 토스트 (어느 페이지에서나 주입)
            services.AddSingleton<CommandPaletteController>();  // 명령 팔레트 순수 C# 열기 브로커(JS 홉 제거)
            // S28 Mission B — AI 비서 컴플라이언스 페르소나 가드(sticky 락). 단일 사용자 WebView → 싱글톤 =
            //   '앱 세션' 잠금 의미론(비정상 의도 1회 → 세션 봉인, 앱 재시작 시 초기화). AdvisorPage 가 분류→Admit.
            services.AddSingleton<ComplianceGate>();
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

            // v4.78.0 — 과제3: 키움 파이프 생존성 연쇄 동결 릴레이.
            //   ★체결 감사 경계: 체결(Fill) 감사·보유수량 갱신은 ExecutionBridgeBroker 가 소유한 LiveAuditService
            //     단일 경로로 일원화한다(OrderRejected 도 FillReceived 로 들어와 평범한 거부가 전체를 동결하는
            //     freeze-storm 회피). 따라서 Relay 는 FillReceived 를 구독하지 않고 ProxyLost/ErrorReceived 만
            //     멱등 CAS 동결 트리거로 처리한다(적대검증 critical 2건 교정 반영).
            var freezeRelay = new KiwoomFreezeRelay(attestation, bridge, com: null);
            freezeRelay.Attach();
            services.AddSingleton(freezeRelay);
            FormClosed += (_, _) => ShutdownTrace.Step("freezeRelay", () => freezeRelay.Dispose());
            services.AddSingleton(attestation);            // S12 #2 — 거래 증적 감사 (ForceFreeze 게이트)
            // v4.95.0 — 무할당 순환 커널 블랙박스 + 동결 원자 덤퍼(감사 오디트 매그넷). 조기 생성 — 핫패스 엔진들이 Record 결속.
            //   ★스레드 격리: Record 는 δ=0 락-프리 push(50ms 드레인·MMF IPC 무간섭). 동결 시 덤프는 전용 BelowNormal 스레드(무I/O 핫패스).
            //   ★liveArmed:false: 관측·영속화만 — IBroker/소켓/무장 술어 참조 0. 덤프 파일 생성이 이중키 게이트에 우회 미생성.
            var blackbox = new MmfFlightRecorder();
            services.AddSingleton(blackbox);   // TelemetryMonitor 🗃️ 블랙박스 & 감사 매그넷 패널 주입 대상
            var blackboxDumper = new CrashResilientDumper(blackbox, attestation);
            blackboxDumper.Start();            // att 동결 옵저버 등록(Seal+신호) + 격리 덤프 스레드 기동
            services.AddSingleton(blackboxDumper);
            FormClosed += (_, _) => ShutdownTrace.Step("blackboxDumper", () => blackboxDumper.Dispose());
            // v4.85.0 — 보안 자산 금고(DPAPI + 메모리 와이프 + 동결 연동 봉인). ★Reveal 은 liveArmed:false 옵트인
            //   펜스 뒤(무장 시에만 실 자격증명 복호화). EmergencyFreeze 감지 시 금고 영구 봉인(재복호화 원천 차단).
            var securityVault = new SecurityAssetVault(db, attestation, () => BrokerHub.IsArmed(db));
            services.AddSingleton(securityVault);
            // S27-c — 섀도우 컷오버 게이트(옵트인 기본 OFF = INERT). ON 시 청산 신호에 신 QFixed 병렬 대조+분기 동결.
            //   라이브 청산 미배선(현행 decimal 권위 유지)·기본 OFF 라 성벽 무영향. 실 컷오버 시 Clear() 결속.
            //   ⚠️ 이 기본 spec 은 'KRW 정수단가 전용 격자'(tick=1원). USD 센트·코인 소수·소수점 원 단가는
            //      ConservativeAlign 이 IsAligned 검사보다 '먼저' 정수원으로 조용히 반올림($100.10→101)하고 그 명목
            //      왜곡을 밴드(tick×qty)가 흡수해 동결되지 '않는다'(거짓 동결 아님 — fail-open 성격의 침묵 왜곡;
            //      회귀잠금 테스트 ProductionSpec_KrwInteger_DecimalPrice_SilentlyDistorts 가 박제). 즉 단가 단위
            //      비정렬은 동결로 안 잡히므로 이 KRW 정수 spec 을 USD/코인/소수단가에 직접 쓰면 명목이 틀린 채 샌다.
            //      실 컷오버 전 자산별(Currency→QTickSpec) 분기 + FX 필수 — 현재는 미배선이라 잠복(Clear() 미호출).
            //   ★Path B 적대적 리뷰(major) 하드닝 — 게이트에 '라이브 attestation 공유' 대신 '전용 섀도우 attestation'을
            //   주입한다. 공유하면 옵트인+배선 시 섀도우 발산 ForceFreeze 가 라이브 OrderAttestationService 를
            //   EmergencyFreeze 로 뒤집어 데코레이터 밖 실주문(ExecutionBridgeBroker.AssertNormal)을 차단할 수 있다.
            //   전용 att 로 격리하면 섀도우 동결은 gate.FrozenCount/전용 att 로만 관측되고 라이브 패닉 상태를 오염시키지 못함.
            // v4.56.0 — 단일 전역 spec(KRW 정수단가 전용·USD/코인 침묵왜곡) → 자산별 격자 레지스트리 ctor 로 전환.
            //   ON(옵트인) 시 Clear(symbol,..)가 자산별 tick/step 으로 보수 정렬(매수↑·매도↓)하고 미등록 심볼은
            //   fail-closed(전용 섀도우 attestation 동결 — 라이브 격리 유지). 기본 OFF(INERT)라 성벽 무영향.
            // v4.77.0 — 과제2: 가격대별 정확 KRX 격자 + USD 센트 격자로 레지스트리 보강.
            //   BuildAugmentedRegistry 는 DefaultEntries(불변) 위에 신규 심볼만 추가(동일 심볼은 ctor FNV 충돌
            //   fail-fast). KrxEntry(symbol, 참조가격)→KrxTickLadder.SpecFor(가격대 정확 호가단위), UsdEntry→0.01 센트.
            //   기본 OFF(INERT)라 성벽 무영향. 이종통화 USD→KRW base 청산은 KrxFxClearing.TryClearUsdToKrwBase(독립 fail-closed).
            var clearingRegistry = KrxTickSpecsExtensions.BuildAugmentedRegistry(new[]
            {
                ("068270", 180_000m),   // 셀트리온  (~18만 → 5만~20만 → 100원)
                ("207940", 800_000m),   // 삼성바이오 (~80만 → 50만↑ → 1000원)
                ("005490", 400_000m),   // POSCO홀딩스(~40만 → 20만~50만 → 500원)
                ("012330", 230_000m),   // 현대모비스 (~23만 → 20만~50만 → 500원)
            });
            var shadowGate = new ShadowCutoverGate(
                cfg.GetBool("Clearing.ShadowCutover", false),
                clearingRegistry, new OrderAttestationService());
            // v4.60.0 Path Alpha — 청산 연산 권위 역전 컷오버 배선. 옵트인(Clearing.QFixedPrimary.Enabled=true) 시에만
            //   QFixed 정렬값을 권위로 승격(SetAuthority — volatile int 무할당 가역 스위치). 기본 false → LegacyPrimary
            //   (decimal 권위·기존 의미론 1비트 불변). ⚠️ liveArmed:false 박제: 본 게이트는 라이브 청산 경로에 미배선
            //   (Launcher 가 BrokerHub.Create(db) 단일 인자 → shadowGate=null)이므로 권위 전환은 실주문에 영향 0 —
            //   연산 권위(반환값)만 신 QFixed 로 바뀌고, 발산/오버플로/FX 스큐는 전용 섀도우 att 로만 fail-closed 동결.
            if (cfg.GetBool("Clearing.QFixedPrimary.Enabled", false))
                shadowGate.SetAuthority(ClearingAuthority.QFixedPrimary);
            // v4.67.0 — WinForms Launcher 결속 브릿지 등록.
            //   Launcher.PaperQuick()가 TryGet()으로 게이트·증적 인스턴스를 추출해 BrokerHub.Create에 주입 → ShadowClearingBroker 배선.
            //   등록 실패(이미 등록됨)는 first-write wins로 무시 — 재진입·이중 초기화 안전.
            ExecutionInfrastructureHub.Register(shadowGate, attestation);
            services.AddSingleton(shadowGate);
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
            // S26-c(Horizon B) — 락-프리 텔레메트리 채널. 펌프(단일 라이터)가 발행, /command UI 가 무할당 독출.
            var forensicTelemetry = new ForensicTelemetryChannel();
            services.AddSingleton(forensicTelemetry);

            // S24-b — 비동기 플러시 펌프(옵트인 기본 OFF). 핫패스는 RequestFlush(Interlocked) 한 줄만,
            //   격리 백그라운드가 ring→MMF 영속화. OFF면 펌프 null → 저널 미기록 → 부팅 항상 Empty(후방호환).
            JournalFlushPump? flushPump = null;
            if (cfg.GetBool(BootConfigSpecs.RecoveryPumpEnabledKey, BootConfigSpecs.RecoveryPumpEnabledDefault))
            {
                // S25-b — 복합 포맷 펌프: 원자 세션(genome+forensic 단일 seqlock) 스냅샷 → PersistComposite.
                // 사전 할당 스크래치 클로저 → per-flush 무할당. 펌프 백그라운드 단일 스레드에서만 실행.
                var scratchRej     = new RejectionRecord[recoveryJournaler.Capacity];
                var scratchJournal = new JournalRecord[recoveryJournaler.Capacity];
                long telePersistSeq = 0;   // S26-c — 단일 라이터(펌프 스레드) 전용 카운터, 잠금 불요
                flushPump = new JournalFlushPump(() =>
                {
                    int n = rejectionForensics.SnapshotSession(scratchRej, out var genome);   // ★원자 세션(split-brain 봉인)
                    for (int i = 0; i < n; i++)
                    {
                        var r = scratchRej[i];
                        scratchJournal[i] = new JournalRecord(r.Seq, RecoveryKind.Rejection, (byte)r.Stage, 0,
                            r.AsOfTicks, r.NowTicks, r.WindowTicks, r.Fitness, JournalRecord.HashId(r.SpecId));
                    }
                    bool ok = recoveryJournaler.PersistComposite(genome, scratchJournal.AsSpan(0, n));
                    // S26-c — 텔레메트리 발행(펌프 스레드 = 단일 라이터). 무할당 seqlock 슬롯에 값 타입 복사.
                    telePersistSeq++;
                    long lagTicks = genome.HasValue ? System.Math.Max(0L, System.DateTime.UtcNow.Ticks - genome.AsOfTicks) : 0L;
                    forensicTelemetry.Publish(new ForensicTelemetry(
                        telePersistSeq, System.Diagnostics.Stopwatch.GetTimestamp(), rejectionForensics.Version,
                        lagTicks, n, ok ? 100 : 0,
                        unchecked((uint)genome.GenomeHash),
                        unchecked((uint)(n > 0 ? scratchJournal[n - 1].GenomeHash : genome.Seq))));
                    return ok;
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
                // S26-b C-1 — 라이브 시 시간 도메인 두-갈래 펜스 결속(코드-경로-only dead-path 해소). MonoClock 기본
                //   Stopwatch(주입 안 함). ①데이터 시장-나이(마켓시각 기준·Δt_network 반영) ②단조 liveness(NTP·역행 면역).
                //   OFF면 미결속(레거시). ⚠️ 라이브 provider 왕복은 CI 미검증(사용자 측) — 펜스 로직만 결정론 검증됨.
                DataFreshnessWindow = liveFeed
                    ? System.TimeSpan.FromSeconds(cfg.GetInt("Greenhouse.DataFreshSec", 180, 10, 86400))
                    : (System.TimeSpan?)null,
                LivenessWindow = liveFeed
                    ? System.TimeSpan.FromSeconds(cfg.GetInt("Greenhouse.LiveSec", 90, 5, 3600))
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
            FormClosed += (_, _) => ShutdownTrace.Step("greenhouse.Deactivate", () => greenhouseActivator.Deactivate());
            FormClosed += (_, _) => ShutdownTrace.Step("flushPump", () => flushPump?.Dispose());

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
                FormClosed += (_, _) => ShutdownTrace.Step("ingress", () => ingress.Dispose());
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

            // S29 Path C+Gamma — SPMC 주문 디스패치 링 + 락-프리 UI 텔레메트리 배선.
            // Mission A(v4.55): attestation 주입 → onSend throw 시 ForceFreeze 로 전체 파이프라인 비상 동결.
            var dispatchRing   = new OrderDispatchRing(1024);

            // v4.57.0 Mission B — 3중 안전 게이트 실 무장 술어 배선. att.Mode==Normal ∧ ignition.AllowPhysical()
            //   ∧ BrokerHub.IsArmed(db) — 평가 중 예외는 LiveArmingGate 가 fail-closed(false) 흡수(디스패처 Drain
            //   외곽 try/catch→GatedOut++ 와 이중 벽). ⚠️ liveArmed:false 박제 보존: 기본 OFF — 옵트인 config
            //   (Dispatch.LiveArmingWired)가 켜질 때만 실 술어, 아니면 수학적 0(()=>false). 옵트인 ON 이라도 송출은
            //   BrokerHub.IsArmed(기본 0)+점화 Locked 라 여전히 0 — 그리고 실 onSend/드레인 루프는 미배선(별도 단계).
            var armingGate = new LiveArmingGate(ignition, attestation, () => BrokerHub.IsArmed(db));
            // v4.59.0 — 진공관 결속: 스위치 결정을 테스트 가능한 DispatchArming.ResolvePredicate 로 분리(인라인 삼항 제거).
            //   기본(미옵트인) → 상수 ()=>false(수학적 0). 옵트인 ON 일 때만 armingGate.IsArmed 결속. 술어 선택은 부팅 1회.
            // v4.90.0 — 복구기 잠금 펜스: 웜 스타트 복구 진행 중(IsRecovering)이면 무장 술어를 무조건 false 로 감싼다
            //   (동결 해제 직후 잠긴 전략들의 '주문 폭주(Order Rush)' 물리 차단). warmRestart 는 아래에서 생성돼 지연 클로저로 결속.
            DeterministicWarmRestartEngine? warmRestartRef = null;
            // v5.5.0 — 커널 지터/I/O 방어선 안전 홀드도 무장 술어에 합성(지연 ref·아래에서 생성). 홀드 활성 시 무장을 false 로 억제 —
            //   OS 지터/디스크 stall/페이지 thrashing 시기의 오발주를 선제 차단. '더 안전한 방향'으로만 작동(tighten-only·liveArmed:false 정합).
            KernelJitterSpikeSentinel? kernelJitterRef = null;
            IoBackpressureFirewall?    ioFirewallRef   = null;
            // v7.2.0 — 포트폴리오 VaR 리스크 밸브도 무장 술어에 합성(지연 ref·아래에서 생성). VaR/스트레스 한도 초과 시 무장을
            //   false 로 억제 — 신규 발주만 tighten-only 차단('더 안전한 방향'·liveArmed:false 정합·발주 유발 경로 부재).
            PortfolioRiskVaREngine? varGuardRef = null;
            // v7.3.0 — 서멀 스로틀 안전홀드·캐시 코히런스 파열도 무장 술어에 합성(지연 ref·아래 생성). 하드웨어 위험 구간
            //   신규 발주 tighten-only 억제('더 안전한 방향'·liveArmed:false 정합·발주 유발 경로 부재).
            ThermalFrequencyJitterGuard? thermalGuardRef = null;
            CacheLineCoherenceSentinel?  coherenceRef    = null;
            // v7.4.0 — Genesis Pre-Flight 인증 veto·무장 임대 만료·데드맨 격발·fat-finger/폭주 동결도 무장 술어에 합성(지연 ref·아래 생성).
            //   전부 tighten-only — 무장을 '거둘' 뿐 부여 불가(liveArmed:false 정합). 실전 자본 집행 직전/지속 안전 격리.
            GenesisPreFlightCertifier?   preFlightRef   = null;
            AtomicArmingLease?           armingLeaseRef = null;
            DeadMansSwitchWatchdog?      deadManRef     = null;
            FatFingerVelocityInterlock?  fatFingerRef   = null;
            // v8.1.0 — Native API 후킹 변조도 무장 술어에 합성(지연 ref·아래 생성). 진입점 인라인 후킹 탐지 시 무장 tighten-only 하향.
            AntiHookingCodeIntegrityGuard? antiHookRef  = null;
            // v9.1.0 — 극단적 카오스 결함 주입도 무장 술어에 합성(지연 ref·아래 생성). 결함 주입(SafeHold)·복구 미완(RecoveryBlocking)·
            //   락프리 라이브락/torn 감지(SafeHold) 어느 하나라도 활성이면 무장을 false 로 억제. 전부 tighten-only — 극단 재난 주입
            //   하에서도 무장을 '거둘' 뿐 부여 불가(liveArmed:false 정합·발주 유발 0·단 1비트 오발주 누수 물리 봉쇄).
            ChaosEngineSanitizer?        chaosRef      = null;
            ChaosRecoveryRateLimiter?    chaosRecovRef = null;
            ChaosResilientProgressGuard? chaosGuardRef = null;
            // v10.0.0 — 하드웨어 키 펄스 단선/미수신도 무장 술어에 합성(지연 ref·아래 생성). 물리 무장 키가 살아있지 않으면
            //   무장 불가(tighten-only·liveArmed:false — 물리 키 없이는 실 사출 0). LiveArmingWired 결속 시에만 평가.
            HardwareKeyPulseHeartbeatSentinel? keyPulseRef = null;
            // v10.1.0 — 전방위 인증 봉인 미완(100/100 아님)·스코프 전환 쿨다운도 무장 술어에 합성(지연 ref·아래 생성). 유효 인증
            //   봉인 없이 또는 드라이런↔실전 전환 찰나엔 실전 무장 불가(tighten-only·liveArmed:false — 전 시스템 인증 전 실 사출 0).
            LiveFlightCertificationSealer? certSealerRef = null;
            ScopeSealedTransitionBarrier?  scopeBarrierRef = null;
            LatencySlaGatekeeper?          slaGateRef = null;       // v10.1.0 Final — SLA P99.99 이탈 시 무장 tighten
            SlaJitterRegressionGuard?      jitterGuardRef = null;   // v10.1.0 Final — 측정 지터 회귀 시 무장 tighten
            IgnitionSignoffValidator?      signoffRef = null;       // v10.1.0 Ops — 사전 점화 서명 미달 시 무장 하드 차단
            HostEnvironmentDriftSentinel?  driftRef = null;         // v10.1.0 Ops — ★호스트 환경 드리프트 시 무장 하향
            MicroCapitalCanaryGovernor?    canaryRef = null;        // v10.1.0 Ops — 카나리 슬리피지 강등 시 무장 tighten
            OperatorDuressPanicSwitch?     panicRef = null;         // v10.1.0 Ops — 패닉 격발 시 무장 tighten
            // v12.14.0-GM — ★DI 등록·텔레메트리(RingTelemetry) 소비까지 됐으나 무장 술어엔 미결속이었던 드먼트 갭 교정
            //   (Step 7/7 식 전수 스캔으로 발견): 릴리스 봉인 미검증/불일치(AttestationBlocking) → 무장 하드 차단.
            //   Release.ExpectedMasterSeal 미설정(기본값) 상태에서도 SealValid=false 이므로 기본이 곧 fail-closed.
            ReleaseArtifactSha256Attestor? releaseAttestorRef = null;
            // [F7] 콜드부트 안티탬퍼 핸드셰이크 — 설치본 manifest.sig 수렴 + SHA-256 0B 재검증. null(결속 실패) = 차단(?? true · fail-closed).
            MS2E.Infrastructure.BootIntegrityGate? bootIntegrityRef = null;
            // [MMF 하드닝] 포렌식 감사 체인 변조 → 무장 차단. null(결속 전/실패) = 차단(?? true · fail-closed).
            MS2E.Infrastructure.ForensicChainDisarmInterlock? forensicChainRef = null;
            // [시세 하드닝] 이상 시세(가격≤0·틱 점프·가격제한폭 이탈) 유입 → 시세 라인 격리 + 무장 차단. null = 차단(?? true).
            MS2E.Infrastructure.MarketDataSpikeInterlock? spikeRef = null;
            // [실배선 · 방향 B] 원자 인터락 집계기 — 5개 하드닝 차단 엔진(부팅 무결성·자격증명 유출·포렌식 체인·이상 시세·카오스 가드)을
            //   단일 long 비트마스크로 푸시 수렴. 필수 소스는 결속 전부터 차단 비트가 서 있고(미결속 = 차단), 조립 완료 후 Seal() 이
            //   '전부 결속' 을 확인해야 기동 샌니티 비트가 내려간다(방향 A). 무장 술어 앞단 O(1) 단락 + 송출 직전 최종 veto(OrderDrainWorker).
            var interlockMask = new MS2E.Core.ArmingInterlockBitmask(
                MS2E.Core.ArmingInterlockSource.BootIntegrity, MS2E.Core.ArmingInterlockSource.CredentialLeak,
                MS2E.Core.ArmingInterlockSource.ForensicChain, MS2E.Core.ArmingInterlockSource.MarketSpike,
                MS2E.Core.ArmingInterlockSource.ChaosProgress,
                MS2E.Core.ArmingInterlockSource.RouterFeed,      // [피드 연동 · 방향 B] 시세/체결 피드 단절 회로(기동 직후 피드 미수립 = 차단)
                MS2E.Core.ArmingInterlockSource.OrderSlotAge,    // [피드 연동 · 방향 A] 응답 타임아웃 슬롯 회수 = 미확인 노출 차단
                MS2E.Core.ArmingInterlockSource.ExtremeSlippage, // [보호 지정가 · 방향 B] 체결가 극한 괴리 기습 체결 → Disarm
                MS2E.Core.ArmingInterlockSource.BrokerStateMismatch, // [키움 프로토콜 · 방향 B] 추적 잔량 ≠ 브로커 미체결 잔량 → Disarm
                MS2E.Core.ArmingInterlockSource.OrderbookIlliquidity, // [L2 · 방향 B] 연속매매 중 호가 교차/공백/극한 스프레드 → Disarm
                MS2E.Core.ArmingInterlockSource.LatencyStall,    // [저지연 · 방향 B] Tick-to-Trade 프로세스 내 구간 정지 → Disarm
                MS2E.Core.ArmingInterlockSource.KiwoomServerError, // [키움 COM · 방향 B] 중대 서버 오류 · STA 펌프 동결 → Disarm
                MS2E.Core.ArmingInterlockSource.AccountDivergence, // [계좌 · 방향 B] 정적 구간 브로커 보유 ≠ 호스트 추정 보유 → Disarm
                MS2E.Core.ArmingInterlockSource.EgressBypass, // [E2E · 방향 B] liveArmed:false 인데 실 브로커 Place 도달 → 송출 차단 + Disarm
                MS2E.Core.ArmingInterlockSource.PreFlightBoot, // [사전 검증] 시계 역행 · 안전 설정 런타임 변조 · 프록시 파이프 신원 불일치 → Disarm + 키 소거
                MS2E.Core.ArmingInterlockSource.CorrelationCrash, // [포트폴리오 · 방향 B] 보유 종목 동시 하락 → 미체결 일괄 취소 + Disarm
                MS2E.Core.ArmingInterlockSource.Rollover,        // [롤오버] 승계 슬롯 불일치 · 승계 후 브로커 대사 미완료 → 신규 발주 차단
                MS2E.Core.ArmingInterlockSource.OpenOrderDivergence, // [미체결 대사] 최초 브로커 미체결 대사 미확정 · 원장 불일치 · 유령 주문 → Disarm(+ 비상 취소)
                MS2E.Core.ArmingInterlockSource.ChildSlotRejection, // [분할 라우팅] 자식 거부(미송출 형제 회수) · 체결 불균형 · 분할 가능 브로커 0(미결속/소진) → Disarm
                MS2E.Core.ArmingInterlockSource.KisLimitFeed,     // [KIS 실배선] REST 잔고/주문가능 연속 3회 실패 → KIS 한도 0 + Disarm(미설정 = 비차단)
                MS2E.Core.ArmingInterlockSource.KiwoomBridgeLink, // [키움 실배선] 연결 활성화인데 실 COM 프록시 링크 미수립/단절/시뮬레이션 → Disarm
                MS2E.Core.ArmingInterlockSource.KisProtocolDivergence, // [KIS VTS] 실/모의 TR 도메인 교차 · REST 응답 TR 헤더 이탈 → KIS 슬롯 격리 + Disarm
                MS2E.Core.ArmingInterlockSource.StrategyAnomaly, // [AI 전략 · 방향 B] 시그널 NaN/∞ · 범위 이탈 · 수량 0 이하 제안 · 전략 연산 > 10ms → Disarm
                MS2E.Core.ArmingInterlockSource.OrderPathLatency, // [v12.17.6 · 방향 B] 실발주 경로 시그널 → 출구 p99 > 예산(드레인 주기 + 50ms) 연속 3회 → Disarm
                MS2E.Core.ArmingInterlockSource.HealthAgent);    // [헬스] Working Set 폭주 · 스레드풀 정체 · GC 정지 폭주 → Disarm
            services.AddSingleton(interlockMask);
            // ★실전 무장 술어(liveArmed): 물리 키(LiveArmingWired) ∧ 전 방어선 ∧ 인증 봉인 ∧ 전환 아님. 실 브로커 사출 전담.
            Func<bool> liveArmed = DispatchArming.ResolvePredicate(
                cfg.GetBool("Dispatch.LiveArmingWired", false), armingGate,
                blockWhile: () => interlockMask.Blocked                         // [실배선] 집계 비트(미결속·미봉인·Trip) → O(1) 단락 차단
                                || (warmRestartRef?.IsRecovering ?? false)
                                || (kernelJitterRef?.SafeHoldActive ?? false)
                                || (ioFirewallRef?.HoldActive ?? false)
                                || (varGuardRef?.LimitBreached ?? false)
                                || (thermalGuardRef?.SafeHoldActive ?? false)
                                || (coherenceRef?.SafeHoldActive ?? false)
                                || (preFlightRef?.VetoActive ?? false)
                                || (armingLeaseRef?.Expired ?? false)
                                || (deadManRef?.Tripped ?? false)
                                || (fatFingerRef?.Frozen ?? false)
                                || (antiHookRef?.SafeHoldActive ?? false)
                                || (chaosRef?.SafeHoldActive ?? false)
                                || (chaosRecovRef?.RecoveryBlocking ?? false)
                                || (chaosGuardRef?.SafeHoldActive ?? false)
                                || (keyPulseRef?.SafeHoldActive ?? false)
                                || (certSealerRef?.CertificationBlocking ?? false)
                                || (scopeBarrierRef?.TransitionBlocking ?? false)
                                || (slaGateRef?.IsBreached ?? false)               // v10.1.0 Final — 나노 SLA P99.99 이탈 → 무장 tighten
                                || (jitterGuardRef?.RegressionTripped ?? false)    // v10.1.0 Final — 측정 신뢰불가 회귀 → 무장 tighten
                                || (signoffRef?.SignoffBlocking ?? false)          // v10.1.0 Ops — 사전 점화 서명 미달/리스 만료 → 무장 하드 차단
                                || (driftRef?.DriftBlocking ?? false)              // v10.1.0 Ops — ★호스트 환경 드리프트 → 무장 하향
                                || (canaryRef?.CanaryBlocking ?? false)            // v10.1.0 Ops — 카나리 슬리피지 강등 → 무장 tighten
                                || (panicRef?.PanicBlocking ?? false)              // v10.1.0 Ops — 운용자 패닉 격발 → 무장 tighten
                                || (releaseAttestorRef?.AttestationBlocking ?? true)     // v12.14.0-GM — 릴리스 봉인 미검증/불일치 → 무장 하드 차단([봉인 운영] manifest 게이트 위임 · null = 차단)
                                || (bootIntegrityRef?.ArmingBlocked ?? true)            // [F7] 바이너리 무결성 미봉인/검증중/변조 → 무장 하드 차단
                                || MS2E.Infrastructure.RawCredentialLeakInterlock.Shared.ArmingBlocked    // [PII] 원문 자격증명 로그 유출 시도 → 무장 영구 차단
                                || (forensicChainRef?.ArmingBlocked ?? true)          // [MMF] 포렌식 해시 체인 변조 → 무장 영구 차단
                                || (spikeRef?.ArmingBlocked ?? true));                // [시세] 이상 시세 유입 → 무장 차단(sticky·물리 재무장)
            // ★v10.1.0 디스패처 무장 술어 = 드라이런 스코프(가상 미러 급전·barrier RouteNow 가 broker 를 물리 차단) ∨ 실전 무장.
            //   드라이런 'armed' 는 오직 미러로만 도달 — 실 브로커 소켓 0비트(liveArmed:false 불변·barrier=DryRun→미러, =Live→게이트웨이).
            Func<bool> armedPredicate = () => { try { return (scopeBarrierRef?.DryRunActive ?? false) || liveArmed(); } catch { return false; } };
            // v5.0.0 — 블래스트 반경 제한기: 카나리 검역창 동안 명목 상한을 송출 초크포인트(디스패처)에서 물리 강제(기본 무제한).
            var blastRadius = new BlastRadiusLimiter();
            services.AddSingleton(blastRadius);
            // v7.4.0 — Genesis 안전 패브릭: 종합 점검 인증 + 무장 임대 + 데드맨 왓치독 + ★Think Beyond fat-finger/폭주 명목속도 인터록.
            //   ★liveArmed:false: 전부 순수 관측/veto — IBroker·소켓·onSend 참조 0. blockWhile tighten-only + fatFinger 는 디스패처
            //     초크포인트에서 사출 물리 차단(발주 유발 경로 부재). 밸브 미배선(기본)이면 드레인 미가동이라 전 경로 dormant.
            var preFlight   = new GenesisPreFlightCertifier();
            preFlightRef    = preFlight;
            var armingLease = new AtomicArmingLease(leaseSeconds: (int)cfg.GetDouble("Dispatch.ArmingLeaseSeconds", 300.0, 10.0, 3600.0));
            armingLeaseRef  = armingLease;
            double ffBudgetKrw = cfg.GetDouble("Risk.OrderFlowNotionalBudgetKrw", 0.0, 0.0, 1e15);
            var fatFinger   = new FatFingerVelocityInterlock(
                maxDeviationPermille: (long)cfg.GetDouble("Risk.FatFingerDeviationPermille", 100.0, 1.0, 1000.0),
                windowSeconds: 1,
                windowNotionalBudgetRaw: ffBudgetKrw > 0 ? QMoney.Of((decimal)ffBudgetKrw).Raw : 0L,
                maxSameSideBurst: (long)cfg.GetDouble("Risk.OrderFlowMaxSameSideBurst", 0.0, 0.0, 1e9),
                refPriceRaw: null);   // 라이브 경로가 per-symbol 기준가 프로브 주입 시 가격 축 활성(현 미배선=가격 축 dormant·속도 축은 예산 설정 시 활성)
            fatFingerRef    = fatFinger;
            var deadMan     = new DeadMansSwitchWatchdog(
                isArmed: () => { try { return armingGate.IsArmed(); } catch { return false; } },
                onDeadManTrip: () => { try { attestation.ForceFreeze("데드맨 스위치 격발 — 엔진/UI 행(≥임계) 감지·미체결 전량 취소·하드웨어 밸브 원자 차단"); } catch { } },
                hangThresholdMs: (int)cfg.GetDouble("Dispatch.DeadManHangMs", 500.0, 100.0, 5000.0));
            deadManRef      = deadMan;
            deadMan.Start();
            services.AddSingleton(preFlight);
            services.AddSingleton(armingLease);
            services.AddSingleton(fatFinger);
            services.AddSingleton(deadMan);
            FormClosed += (_, _) => ShutdownTrace.Step("deadMan", () => deadMan.Dispose());
            var ringDispatcher = new GatedOrderDispatcher(dispatchRing, armedPredicate, attestation, blackbox, blastRadius, fatFinger);
            var ringChannel    = new RingTelemetryChannel();
            // v4.63.0 — 다중 seqlock 통합: 시나리오 변이 seqlock(ScenarioPublisher·페이지 transient) 카운터를 싱글톤
            //   브리지로 받아 중앙 샘플러가 합류. FutureSimPage 가 매 틱 Report → ringPublisher.Sample 이 Read → 채널 발행.
            var scenarioMon    = new ScenarioSeqlockMonitor();
            // v4.73.0 — 자율 알파 공장: BelowNormal 백그라운드 스레드(24h 주기). EmergencyFreeze → fail-closed 스킵.
            //   외부 시세 API 의존 0(CartridgeEngine.ReferenceSeries(seed=42)). 카운터는 ringPublisher.Sample 이 합류.
            var alphaFactory = new AutonomousAlphaFactory(db, attestation);
            alphaFactory.Start();
            services.AddSingleton(alphaFactory);
            FormClosed += (_, _) => ShutdownTrace.Step("alphaFactory", () => alphaFactory.Dispose());
            // v4.86.0 — 자율 전략 진화: 금고 연동 서명기 + 3중 게이트(WalkForward·Tournament·AlphaEngine) 파이프라인.
            //   ★liveArmed:false 불변: 파이프라인은 PromotionGateway.Submit(=PendingApproval)까지만 — 자율 Approve/
            //   Activate 없음. 서명은 연구 산출물(.ms2cart)이며 실주문 아님. 파이프라인 지표는 ringPublisher.Sample 이 합류.
            var cartridgeSigner   = new AutonomousCartridgeSigner(db, securityVault, attestation);
            var evolutionPipeline = new StrategyEvolutionPipeline(attestation, alphaFactory, cartridgeSigner, promotionGateway);
            services.AddSingleton(cartridgeSigner);
            services.AddSingleton(evolutionPipeline);
            // v4.87.0 — 집행 미시구조 가드: 슬리피지 3σ 2연속·레이턴시 100ms 초과 → IgnitionLock.Downgrade(강등).
            //   ★강등 ≠ 락업. 파이프라인 무결성 붕괴·예외는 OrderDrainWorker 가 ForceFreeze 로 별도 에스컬레이션.
            var execGuard = new ExecutionMicrostructureGuard(sentinel, ignition);
            services.AddSingleton(execGuard);
            // v4.88.0 — 원장 보호 메시: 체결 원장 쓰기 + 평단 재계산을 발주 핫패스에서 비동기 분리(전용 드레인 스레드).
            //   ★liveArmed:false 불변: 순수 영속화 오프로드 — 주문 발주/점화 FSM 무결합. 큐 90% 포화·영속화 예외 → ForceFreeze.
            //   기본 생산자 미배선(핫패스가 Enqueue 결속 시 가동) — idle 이면 큐 깊이 0.
            var ledgerMesh = new LedgerProtectionMesh(db, attestation);
            ledgerMesh.Start();
            services.AddSingleton(ledgerMesh);
            FormClosed += (_, _) => ShutdownTrace.Step("ledgerMesh", () => ledgerMesh.Dispose());
            // v4.96.0 — 실시간 청산 메쉬: 체결 즉시 실전 제비용(수수료·거래세·농특세·해외 혼합) δ=0 정산 + 예수금 영점 조절.
            //   ★2단계 청산 래치: 핫패스(단일 디스패치 스레드) CAS 델타 차감 + 값타입 링 발행 → 회계 스레드가 exact+캐리 확정.
            //   ★liveArmed:false 불변: 내부 원장만 영점 조절 — IBroker/소켓 참조 0. 외부로 허위 체결 동기화·주문 0바이트 사출.
            //   시드는 모의 계좌 기준(라이브 예수금 소스 미보존 = 별도 선결). 체결이 없으면(미무장) idle.
            var accountingMesh = new LiveAccountingMesh(QMoney.Of(10_000_000m).Raw, attestation);
            accountingMesh.Start();
            services.AddSingleton(accountingMesh);   // TelemetryMonitor 📊 청산 메쉬 패널 주입 대상
            // ★체결 수신 결속: 브릿지 FillReceived(단일 디스패치 스레드) → 메쉬 OnFill. 실 체결은 이중키 미무장 시 발생 안 함(idle).
            //   ★C#5 freeze-storm 방지: OrderRejected/미체결도 같은 FillReceived 채널로 FillQty=0 이 흐르므로(KiwoomFreezeRelay 가
            //   FillReceived 를 의도적 미구독하는 이유), 실체결(FillQty>0)만 회계에 전달 — 정상 거부가 메쉬를 동결시키지 않게.
            bridge.FillReceived += fp => { if (fp.FillQty > 0) { try { accountingMesh.OnFillPayload(fp); } catch { } } };
            FormClosed += (_, _) => ShutdownTrace.Step("accountingMesh", () => accountingMesh.Dispose());
            // v4.76.0 — 안전 점화 시퀀서(격리 1주 프로브→3σ·지연 검증→MasterIgnited). ★프로브는 격리 PaperBroker(IsLive=false)로만
            //   — 실 자본 위험 0. ★ProbeReferencePrice>0(지정가 경로). MasterIgnited 도달 전까지 IgnitionLock=Locked·LiveArmingGate=false.
            //   v4.90.0 — 웜 스타트 엔진이 Escalating 단계에서 이 시퀀서를 재사용하므로 조립 순서를 ringPublisher 앞으로 상향.
            var probeBroker = new PaperBroker(db);
            var ignitionSeq = new SafeIgnitionSequencer(
                ignition, attestation, sentinel, probeBroker,
                valveWired:      () => cfg.GetBool("Dispatch.ValveWired", false),
                liveArmingWired: () => cfg.GetBool("Dispatch.LiveArmingWired", false))
            {
                ProbeSymbol          = "005930",
                ProbeReferencePrice  = 1.0,    // >0 필수(지정가 경로). 실 운영은 실제 호가 기준가 주입 권장.
                MaxPipelineLatencyMs = 2_000,
                MaxObservedSigma     = sentinel.AlertThresholdSigma,   // 센티넬 3σ 임계와 정렬
            };
            services.AddSingleton(ignitionSeq);    // ClearingAuthorityPage 와 동급 — /command 점화 트리거용
            // v4.97.0 — 실거래 섀도우 인입 & 원자적 컷오버 게이트웨이: 실 브로커 소켓은 시세/예수금/인증만 살리고, 최하위 주문
            //   송출은 IsShadowMode(기본 true)면 100% 바이패스(실 소켓 0바이트). 가로챈 주문은 전용 섀도우 청산 메쉬에 50ms 내
            //   가상 체결로 공급(슬리피지 모델). ★liveArmed:false 불변: 기본 섀도우=자본 100% 안전. 실전 전환은 오직
            //   AtomicCutoverGate.PreArm(이중키∧프로브∧3σ 의식)→Cutover(원자 flip)의 명시 2단으로만. 섀도우 메쉬는 전용
            //   attestation(격리)이라 가상 체결이 라이브 동결을 오염시키지 않음(freeze-pollution 방지·정찰 렌즈 C).
            var shadowAtt  = new OrderAttestationService();
            var shadowMesh = new LiveAccountingMesh(QMoney.Of(10_000_000m).Raw, shadowAtt);
            shadowMesh.Start();
            // ★DI 미등록 의도적: shadowMesh 도 LiveAccountingMesh 타입이라 AddSingleton 하면 라이브 accountingMesh(위) 등록을
            //   last-wins 로 덮어써 @inject LiveAccountingMesh(📊 패널)가 섀도우 장부로 오해소됨. 게이트웨이가 참조를 보유하므로 DI 불필요.
            FormClosed += (_, _) => ShutdownTrace.Step("shadowMesh", () => shadowMesh.Dispose());
            var shadowGateway = new ShadowExecutionGateway(liveAttestation: attestation, shadowMesh: shadowMesh);
            shadowGateway.StartPump();
            services.AddSingleton(shadowGateway);   // TelemetryMonitor 🕹️ 컷오버 & 섀도우 플라이트 대시보드 주입 대상
            FormClosed += (_, _) => ShutdownTrace.Step("shadowGateway", () => shadowGateway.Dispose());
            var cutoverGate = new AtomicCutoverGate(
                shadowGateway, ignitionSeq, ignition, attestation,
                valveWired:      () => cfg.GetBool("Dispatch.ValveWired", false),
                liveArmingWired: () => cfg.GetBool("Dispatch.LiveArmingWired", false),
                blackbox: blackbox);
            services.AddSingleton(cutoverGate);     // TelemetryMonitor 🕹️ 컷오버 트리거(PreArm/Cutover)용
            // v5.0.0 — 실전 총점화 3단계 소프트웨어. ★실 접속·자격증명·1주 사출은 운영자(사람)가 수행 — 아래는 그 안전 골격.
            //   Phase1 Genesis: 실 예수금을 라이브 청산 메쉬에 1raw 시딩(IsShadowMode 하드 검문). 쿼리 델리게이트는 운영자 배선.
            //   Phase2 Canary: 컷오버 후 RTT 단위 정규화 검역 + 건별 net 정산 1raw 대조(AtomicCutoverGate 재사용).
            //   Phase3 RedTeam: 원격 킬스위치(서명→금고 재봉인+소켓 Abort+ForceFreeze) + 데드맨 역-컷오버(생존신호 소실→자동 섀도우).
            //   ★liveArmed:false: 전부 '더 안전한 방향(정지/섀도우)'으로만 작동. 실 소켓/웹훅 수신 표면 미개방(운영자 옵트인).
            var accountGenesis = new LiveAccountGenesis(accountingMesh, shadowGateway, attestation, blackbox);
            services.AddSingleton(accountGenesis);
            var canaryProbe = new CanaryLiveProbe(cutoverGate, accountingMesh, blackbox);
            services.AddSingleton(canaryProbe);
            // 킬스위치: 서명 검증(기본 비공백 — 운영자가 실 서명 검증기로 교체)·금고 재봉인(securityVault.Seal)·Abort 콜백(섀도우 복귀 보강).
            var killSwitch = new RemoteKillSwitch(attestation, s => !string.IsNullOrWhiteSpace(s), securityVault,
                abortSockets: () => { try { shadowGateway.EnterShadow(); } catch { } }, blackbox: blackbox);
            services.AddSingleton(killSwitch);
            // 데드맨: 30초 생존신호 데드라인. 감시 시작(StartWatching)은 컷오버 성공 후 운영자/대시보드가 격발(기본 Idle).
            var deadman = new DeadmanReverseCutover(cutoverGate, TimeSpan.FromSeconds(30), blackbox: blackbox);
            services.AddSingleton(deadman);
            FormClosed += (_, _) => ShutdownTrace.Step("deadman", () => deadman.Dispose());
            // v5.1.0 — 자율 무정지 복구 & 실전 라이브 감시망. ★전부 '더 안전한 방향(강등/동결/섀도우)'으로만 작동(liveArmed:false).
            //   ① LiveSessionWatchdog: 20ms 핑퐁 RTT 연속 3회 100ms 초과→Coma→IgnitionLock.Downgrade. latencyProbe 는 실 세션 배선 시 주입(드라이런 미기동).
            var liveWatchdog = new LiveSessionWatchdog(ignition, resetSocket: null, latencyProbe: null, blackbox: blackbox);
            services.AddSingleton(liveWatchdog);
            FormClosed += (_, _) => ShutdownTrace.Step("liveWatchdog", () => liveWatchdog.Dispose());
            //   ② AutonomousFailoverController: 채널 물리 장애→대안 채널 자율 라우팅·exactly-once dedup·양채널 다운 fail-closed. 실 채널 배선은 운영자 옵트인.
            var failover = new AutonomousFailoverController(attestation, blackbox: blackbox);
            services.AddSingleton(failover);
            //   ③ ThreadStarvationSensor: ThreadPool 아사+CLR 지터 감시→SetMinThreads 프로비저닝. 순수 인프라(트레이딩 판정 무관)라 즉시 기동.
            var starvation = new ThreadStarvationSensor(
                provisionThreads: () => { try { ThreadPool.GetMinThreads(out int w, out int io); ThreadPool.SetMinThreads(w + 4, io); } catch { } },
                blackbox: blackbox);
            starvation.Start();
            services.AddSingleton(starvation);
            FormClosed += (_, _) => ShutdownTrace.Step("starvation", () => starvation.Dispose());
            // v5.5.0 — 영지식 초고속 로깅 패브릭 & 커널 지터 센티널 & I/O 역류 방화벽. ★liveArmed:false: 전부 관측/영속 인프라(IBroker·소켓 0).
            //   로거: 핫패스 δ=0 언매니지드 링 push → 백그라운드 Brotli 압축/XOR/CRC → .ms2log. 디스크 write 실패 시 attestation.ForceFreeze(fail-closed).
            //   커널 지터 센티널: QPC 스케줄링 지터·GC pause·NIC drop 단측 3σ → 가역 안전 홀드(무장 술어 억제·위 blockWhile). 지속 시 Downgrade.
            //   ★Think Beyond I/O 역류 방화벽: 로거 큐/진행 워치독으로 디스크 stall → ForceFreeze, 자가계측 페이지폴트 프로브로 thrashing → 가역 홀드.
            var ms2Logger = new ZeroAllocAsyncCompressedLogger(
                onDiskFault: msg => { try { attestation.ForceFreeze(msg); } catch { } },   // 디스크 영속 실패 → 전역 동결(fail-closed). 동결 중에도 로깅은 계속(포렌식).
                blackbox: blackbox);
            ms2Logger.Start();
            services.AddSingleton(ms2Logger);
            FormClosed += (_, _) => ShutdownTrace.Step("ms2Logger", () => ms2Logger.Dispose());
            var kernelJitter = new KernelJitterSpikeSentinel(ignition, blackbox);   // ctor 에서 20ms 폴 타이머 즉시 기동(순수 인프라)
            kernelJitterRef = kernelJitter;   // 무장 술어 지연 ref 결속(위 armedPredicate blockWhile)
            services.AddSingleton(kernelJitter);
            FormClosed += (_, _) => ShutdownTrace.Step("kernelJitter", () => kernelJitter.Dispose());
            var ioFirewall = new IoBackpressureFirewall(
                queueDepth: () => ms2Logger.QueueDepth,
                millisSinceDrain: () => ms2Logger.MillisSinceLastDrain,
                attestation: attestation, blackbox: blackbox);
            ioFirewallRef = ioFirewall;       // 무장 술어 지연 ref 결속
            services.AddSingleton(ioFirewall);   // TelemetryMonitor 📦 로깅/커널 센티널 패널 주입 대상
            FormClosed += (_, _) => ShutdownTrace.Step("ioFirewall", () => ioFirewall.Dispose());
            //   ④ ★Think Beyond AdaptiveOrphanReaper: 발주 후 종단 응답 무도착(자본 허공 묶임)을 실측 RTT 분포 적응형 데드라인으로 탐지→진상 조회 자본 회수→불확정 fail-closed. reconcile/reclaim 은 운영자 옵트인.
            var orphanReaper = new AdaptiveOrphanReaper(attestation, reconcile: null, reclaim: null);
            services.AddSingleton(orphanReaper);
            // v5.2.0 — 미시 유동성 슬라이서 & 적응형 오더 스로틀. ★전부 '주문 흐름을 줄이는(축소/Hold/강등)' 방향만(liveArmed:false).
            //   스로틀: 초당 주문 상한(운영자 브로커 한도로 튜닝) 90% 도달 시 Hold. 독약 유동성 센티널: Toxic→IgnitionLock.Downgrade(강등·영구락업 아님).
            //   슬라이서: L2 5호가 즉시체결 잔량분만 발라 미니 TWAP 분할 → dispatchRing(무장 게이트 하류). 실 L2 피드는 운영자 옵트인(시세 수신은 무장 무관).
            var orderThrottle = new AdaptiveOrderThrottle(maxPerSec: 20);
            services.AddSingleton(orderThrottle);
            var toxicSentinel = new ToxicFlowSentinel(ignition, rescaleSlippage: null, blackbox: blackbox);
            services.AddSingleton(toxicSentinel);
            // [알고 집행 하드닝] 호가 깊이 기반 조각 슬리피지 상한(기본 100bps · tighten-only — 상한 밖 호가는 즉시체결 잔량에서 제외).
            var liquiditySlicer = new MicroLiquiditySlicer(dispatchRing, toxicSentinel, orderThrottle, blackbox: blackbox,
                maxSliceSlippageBps: cfg.GetInt("Algo.MaxSliceSlippageBps", 100, 1, 10_000));
            services.AddSingleton(liquiditySlicer);
            // v5.3.0 — 무정지 전략 핫패치 게이트 & ALC 격리 그리드. ★liveArmed:false 불변: 핫스왑은 무장 상태 무접촉 —
            //   교체 전략의 신호도 GatedOrderDispatcher 무장 게이트∧이중키∧MasterIgnited 하류라 실 송출 0. 자율 Activate 없음.
            //   좀비 실드(세대 펜스+WeakReference 어테스테이션): 구 ALC 수거 실패 시 IgnitionLock.Downgrade(강등·클램프). 고아 이벤트 δ=0 거부.
            //   ★기본 배선은 검증기 미주입 + requireProvenance:true → 서명 검증기 배선 전까지 어떤 Reload 도 거부(fail-closed). 실 전략 DLL 로드는 운영자 옵트인.
            var stateCarrier = new StrategyStateCarrier();
            services.AddSingleton(stateCarrier);
            var zombieShield = new ZombieAssemblyShield(ignition, blackbox: blackbox);
            services.AddSingleton(zombieShield);
            var reloadGate = new HotStrategyReloadGate(stateCarrier, zombieShield,
                verifyProvenance: null, requireProvenance: true, attestation: attestation, blackbox: blackbox);
            services.AddSingleton(reloadGate);   // TelemetryMonitor 🔌 핫스트래티지 리로드 게이트 패널 주입 대상
            // v5.4.0 — 다중 전략 인터리빙 공존 & 실시간 바스켓 리밸런서. ★liveArmed:false 불변: 팬아웃/네팅/상쇄/잔여 사출은
            //   전부 무장 게이트 하류 — 잔여도 GatedOrderDispatcher 이중키∧MasterIgnited 없이 실 송출 0바이트. 자전거래 가드는 드레인 전 거부.
            //   네터: 동일 틱 대향 주문 내부 상쇄(왕복 수수료·세 절감), 잔여만 dispatchRing 사출. 방화벽: 공진/자폭 구획 봉인+캐스케이드 시 IgnitionLock 백스톱.
            //   ★자본 물리 분할은 전역 모의 예수금 위 논리 오버레이(가중치 정수비). 실 전략 인스턴스 레인 등록·OnQuote 펌프는 운영자 옵트인(현 dormant·시세 수신은 무장 무관).
            var basketNetter = new RealTimeBasketNetter(roundTripCostBps: 46);   // 한국 관례 왕복 비용 근사(거래세+수수료)
            services.AddSingleton(basketNetter);
            long washWindowTicks = System.Diagnostics.Stopwatch.Frequency / 100;   // 10ms 대향 금지 마이크로 윈도우
            var washGuard = new WashTradeGuard(washWindowTicks, capacity: 1024, blackbox: blackbox);
            services.AddSingleton(washGuard);
            var contagionFirewall = new CrossStrategyContagionFirewall(capacity: 16, resonanceThreshold: 6, velocityFuse: 8, cascadeSeals: 3, ignition: ignition, blackbox: blackbox);
            services.AddSingleton(contagionFirewall);
            var strategyMesh = new MultiStrategyInterleavedMesh(capacity: 16, meshGenomeId: 0L, ring: dispatchRing, attestation: attestation,
                netter: basketNetter, washGuard: washGuard, firewall: contagionFirewall, blackbox: blackbox);
            strategyMesh.Repartition(QMoney.Of(10_000_000m).Raw);   // 전역 예수금(모의 시드) 가중치 분할 준비 — 레인 등록 전엔 무동작
            services.AddSingleton(strategyMesh);   // TelemetryMonitor 🔀 다중전략 인터리빙 메쉬 패널 주입 대상
            // v4.90.0 — 결정론적 웜 스타트 복구 엔진: 동결 해제 후 원장 백로그 소화(동결창 가격정합성 펜스) + 점화 FSM 점진 승격.
            //   ★기존 싱글톤(attestation·securityVault·ledgerMesh·ignitionSeq) 공유 — 새 인스턴스 금지(백로그·봉인 래치가 그 인스턴스에만).
            //   fail-closed(ForceFreeze 단일 소스 + 금고 재봉인)·δ=0(펜스 정수 산술·SecretLease ArrayPool)·결정론.
            var warmRestart = new DeterministicWarmRestartEngine(attestation, securityVault, ledgerMesh, ignitionSeq);
            warmRestartRef = warmRestart;   // 복구기 잠금 펜스 지연 클로저 결속(위 armedPredicate)
            services.AddSingleton(warmRestart);
            FormClosed += (_, _) => ShutdownTrace.Step("warmRestart", () => warmRestart.Dispose());
            // v4.91.0 — 결정론적 카오스 테스팅 그리드: 시드 재현 가능한 복합 장애를 가상 시간선에서 투하·검증.
            //   ★liveArmed:false 불변: 순수 인메모리 시뮬 — 실 소켓/파이프/프로세스 미생성(외부 API 0바이트). 라이브 발주 무영향.
            var chaosGrid = new DeterministicChaosGrid();
            services.AddSingleton(chaosGrid);   // TelemetryMonitor 🕹️ 카오스 그리드 패널 주입 대상
            // v4.92.0 — 프로덕션 스왑 컨트롤러: 엔드포인트 변이 방화벽·영지식 패킷 절연·브로커 연결상태 관제.
            //   ★liveArmed:false 불변: 순수 관측+게이트 — 실 소켓/파이프 미오픈(방화벽·절연 통과 시에만 상위 결속부가 오픈). 발주 무접촉.
            //   실 3대 연동부(키움 COM·토스 WSS/REST·FX 피드) 런타임 결속은 옵트인/#if 게이트 — 결속 직전 이 컨트롤러 게이트를 통과해야 함.
            var prodSwap = new ProductionSwapController(attestation);
            services.AddSingleton(prodSwap);    // TelemetryMonitor 🔌 프로덕션 스왑 패널 주입 대상
            // v4.93.0 — 정보 인텔리전스: 감성 속도 브레이커(3σ 하향 → IgnitionLock.Downgrade) + 뉴스 인텔 컨트롤러(백그라운드 드레인).
            //   ★핫패스 격리: 뉴스 링을 전용 BelowNormal 스레드로 드레인 — 50ms 발주 드레인과 스레드 분리(자연어 폭주 지터 0).
            //   ★liveArmed:false 불변: 조언/쇼크 클램프는 방어 방향(가치 하향·무장 하강)만 — 실주문 경로 무접촉.
            var sentimentBreaker = new SentimentVelocityBreaker(ignition);
            var newsIntel = new NewsIntelController(sentimentBreaker, blackbox: blackbox);
            newsIntel.Start();
            services.AddSingleton(sentimentBreaker);
            services.AddSingleton(newsIntel);   // TelemetryMonitor 🧠 뉴스 레이더 & AI 멘토 패널 주입 대상
            FormClosed += (_, _) => ShutdownTrace.Step("newsIntel", () => newsIntel.Dispose());
            // v4.94.0 — 발주 전 리스크 실드(락-프리 원자 증거금/당일 예약). 시드는 모의 계좌 기준(PaperAccount 기본 1천만).
            //   ★liveArmed:false 불변: 실드는 '허용/거부'만 — 발주 0바이트. admit 해도 하류 이중키∧MasterIgnited 게이트가 지배.
            //   라이브 예수금(AccountPayload.Deposit) 소스는 미보존 → 실 예산 시드 배선은 별도 선결(정직 표기).
            long marginBudgetRaw = QMoney.Of(10_000_000m).Raw;   // 증거금(매수여력) 예산 — 모의 시드 1천만원
            long dailyLimitRaw   = QMoney.Of(50_000_000m).Raw;   // 당일 누적 투자한도 — 보수 기본 5천만원
            var riskShield = new PreTradeRiskShield(marginBudgetRaw, dailyLimitRaw, attestation, ignition, blackbox: blackbox);
            services.AddSingleton(riskShield);   // TelemetryMonitor ⚡ 리스크 실드 패널 주입 대상
            // v4.94.0 — 다중 브로커 섀도우 집행 분산 링. ★게이트는 옵트인 등록(기본 0 노드 — liveArmed:false·라이브 게이트 미자동배선).
            //   실 게이트 결속(키움 x86/토스)은 IsLive&IsArmed 옵트인 하 별도 단계. 링 자체는 라우팅·페어 원자성 센티널 인프라.
            var shadowRing = new MultiBrokerShadowRing(
                new System.Collections.Generic.Dictionary<AssetClass, IExtendedExecutionGate>(),
                attestation, ignition);
            services.AddSingleton(shadowRing);   // TelemetryMonitor ⚡ 다중 브로커 링 노드 상태 주입 대상
            // v4.94.0 — MMF 제로레이턴시 IPC 패브릭(Writer 역할·상주 공유메모리 락-프리 링). unsafe 포인터 δ=0.
            //   ★liveArmed:false 불변: '프레임 전송'만 — 외부 증권사 소켓 미오픈(그것은 x86 프록시 몫·주문 송출은 하류 게이트 지배).
            //   리더(x86 프록시 MMF 클라)는 옵트인 결속 — 미결속이면 latency 0·무오염(정직 idle). 생성 실패는 무해 격리(null).
            // ★하트비트 타임아웃(1000ms)은 상주 Writer 의 Beat 주기(ringTimer 200ms)보다 반드시 커야 한다 —
            //   timeout ≤ keepalive 주기이면 유휴 구간마다 상대가 라이터를 사망으로 오판해 허위 오염(ForceFreeze)한다.
            //   ★20ms 고속 사망감지 스펙은 옵트인 x86 프록시 리더 결속 시 '전용 고속 Beat + 20ms 타임아웃' 쌍으로 활성화한다.
            MmfIpcFabric? mmfFabric = null;
            try { mmfFabric = new MmfIpcFabric("MS2E_MMF_FABRIC_" + Environment.ProcessId, MmfFabricRole.Writer, attestation: attestation, heartbeatTimeoutMs: 1000, blackbox: blackbox); }
            catch { mmfFabric = null; }
            if (mmfFabric is { })
            {
                services.AddSingleton(mmfFabric);
                FormClosed += (_, _) => ShutdownTrace.Step("mmfFabric", () => mmfFabric.Dispose());
                // [MMF Triage · 방향 B] 시퀀스 역전/찢김/경계 파손 Poison → 집계 비트 Raise(송출 직전 veto). 선택 소스(생성 실패 시 미결속 —
                //   패브릭은 발주 경로가 아니므로 부재가 무장을 막지 않는다 · 오염은 기존대로 ForceFreeze 도 함께).
                mmfFabric.BindInterlock(interlockMask, MS2E.Core.ArmingInterlockSource.MmfFabric);
            }
            // v6.0.0/v7.0.0 — Path Gamma & Phase 7 텔레메트리 소스(수동·순수 인메모리 관측 인프라 — liveArmed:false·δ=0).
            //   ★부팅 생성 안전: CausalWeaveClock/Ring 은 관리형 배킹, RioHardwareProbe 는 콜드 1회 캡스 진단(Publisher
            //     생성자가 Probe() 1회 실행·Sample 핫패스는 캐시 결과만 읽음), CausalAuditVault 는 인메모리 원장 —
            //     넷 다 RIO 소켓/네이티브 자원을 열지 않고 IDisposable 도 아니다. RIO 수신 메쉬(native 소켓 바인딩)와
            //     플레이백 시뮬레이터(재생 세션 전용)는 운영자 옵트인까지 미생성(null) — 부팅 시 native 자동 기동 없음.
            var weaveClock = new CausalWeaveClock(attestation: attestation, ignition: ignition);
            var weaveRing  = new CausalWeaveRing(1024, 512);
            var hwProbe    = new RioHardwareProbe();
            var auditVault = new CausalAuditVault();
            services.AddSingleton(weaveClock);
            services.AddSingleton(weaveRing);
            services.AddSingleton(hwProbe);      // TelemetryMonitor 🛫 Phase 7 하드웨어 사전점검 패널 주입 대상
            services.AddSingleton(auditVault);   // TelemetryMonitor 🔐 감사 금고 패널 주입 대상
            // v6.3.0 — StockSuite 커널 텔레메트리 IPC 서버(음원 관제 앱 브리지의 서버측) 자동 기동.
            //   실 커널 상태(RIO/SIMD/MMF)를 Func<KernelFlags> 로 직결하고, UI 비상 FREEZE → CausalWeaveClock.TripFuse 배선.
            //   ★부팅 안전: NamedPipe/MMF 는 관리형 자원(native 소켓 아님). RIO 수신메쉬는 옵트인 미생성이라 rioActive=false(정직).
            var simdInterlock = new SimdRiskInterlock(attestation);   // 커널 SIMD 리스크 인터록(비가역 Veto 게이트)
            var kernelTelemetryServer = new StockSuiteKernelTelemetryServer(
                () => new KernelFlags(
                    rioActive: false,                                                             // RIO 수신메쉬 옵트인 미생성 — 부팅 비활성
                    simdArmed: simdInterlock.IsOrderEmissionAllowed && !weaveClock.FuseTripped,   // 비-Veto ∧ 커널 미봉인 = 무장
                    mmfIntact: auditVault.IsIntact,                                               // 감사 금고 INTACT(실)
                    framesReceived: weaveRing.Published),                                         // CausalWeave 링 발행 누적(실)
                onFreeze: reason => weaveClock.TripFuse(reason),   // ★UI 비상 FREEZE → 실 커널 비가역 봉인
                heartbeatMs: 100);
            try { kernelTelemetryServer.Start(); } catch { }
            services.AddSingleton(kernelTelemetryServer);
            FormClosed += (_, _) => ShutdownTrace.Step("kernelTelemetryServer", () => kernelTelemetryServer.Dispose());
            // v7.2.0 — 실시간 포트폴리오 VaR 엔진 + Gershgorin 특이점 방화벽 + 주기 구동기(콜드 백그라운드).
            //   ★liveArmed:false: 엔진은 순수 연산(IBroker·소켓·onSend 참조 0). 밸브(LimitBreached)는 위 armedPredicate
            //     blockWhile 로만 결선돼 신규 발주를 tighten-only 억제(발주 유발 경로 부재). 구동기는 BelowNormal 전용
            //     스레드(수집 I/O 지터를 50ms 드레인 핫패스와 분리·fail-safe). 밸브 한도 기본 0=inert(운영자 옵트인).
            var covGuard  = new CovarianceSingularityGuard(attestation: attestation);
            var varEngine = new PortfolioRiskVaREngine(covGuard);
            varGuardRef   = varEngine;   // 무장 술어 blockWhile 지연 ref 결속(위 armedPredicate)
            services.AddSingleton(varEngine);
            var riskUpdater = new PortfolioRiskUpdater(varEngine, provider, db, new FxConverter(),
                cfg.GetDouble("Risk.Var99LimitKrw", 0.0, 0.0, double.MaxValue));
            riskUpdater.Start();
            services.AddSingleton(riskUpdater);
            FormClosed += (_, _) => ShutdownTrace.Step("riskUpdater", () => riskUpdater.Dispose());
            // v7.3.0 — 하드웨어 P-Core 격리 그리드 + 서멀/주파수 스로틀 가드 + 캐시라인 코히런스 센티널.
            //   ★liveArmed:false: 전부 순수 관측/스케줄링 제어(IBroker·소켓·onSend 참조 0). 안전홀드/퓨즈는 위 armedPredicate
            //     blockWhile 로만 결선돼 tighten-only 억제(발주 유발 경로 부재). 서멀 가드는 BelowNormal 전용 스레드·δ=0 실측.
            var cpuGrid = new CpuAffinityPinningGrid();
            services.AddSingleton(cpuGrid);                         // TelemetryMonitor 🕹️ RIO/CPU 패널 + drainWorker 핀 대상
            var thermalGuard = new ThermalFrequencyJitterGuard();
            thermalGuardRef = thermalGuard;                         // 무장 술어 blockWhile 지연 ref 결속
            thermalGuard.Start();
            services.AddSingleton(thermalGuard);
            FormClosed += (_, _) => ShutdownTrace.Step("thermalGuard", () => thermalGuard.Dispose());
            var coherence = new CacheLineCoherenceSentinel(slots: 8, attestation: attestation);
            coherenceRef = coherence;                               // 무장 술어 blockWhile 지연 ref 결속
            services.AddSingleton(coherence);
            FormClosed += (_, _) => ShutdownTrace.Step("coherence", () => coherence.Dispose());
            // v8.0.0 — AVX-512 초저지연 미시 알파 엔진 + SIMD 정렬 벡터라이저 + SNR 노이즈 양자화기 + ★실행폭 오토튜너.
            //   ★liveArmed:false: 전부 순수 SIMD 연산/계측 — IBroker·소켓·onSend·ring 참조 0. 알파 '수치'만 산출, 발주는 하류
            //     무장 게이트 전담. 오토튜너는 상시 유용(웜업 keep-alive+폭 조율)이라 기동. 알파 엔진은 라이브 L2 피드 옵트인 시 활성.
            var alphaEngine = new Avx512MicroAlphaEngine();
            var noiseQuant  = new AlphaSignalNoiseQuantizer(snrThresholdMilli: (long)cfg.GetDouble("Alpha.SnrThresholdMilli", 500.0, 0.0, 100000.0));
            var alignedVec  = new SimdAlignedRingVectorizer();
            var avxTuner    = new AvxExecutionWidthAutotuner();
            avxTuner.Start();
            services.AddSingleton(alphaEngine);
            services.AddSingleton(noiseQuant);
            services.AddSingleton(alignedVec);
            services.AddSingleton(avxTuner);
            FormClosed += (_, _) => ShutdownTrace.Step("alignedVec", () => alignedVec.Dispose());
            FormClosed += (_, _) => ShutdownTrace.Step("avxTuner", () => avxTuner.Dispose());
            // v8.1.0 — 안티-RAM 퀀텀 금고 + 허니키 디코이 + anti-hook 가드 + 검증 제로라이저 + RCU Config 포트(Phase 83).
            //   ★liveArmed:false: 전부 순수 방어/관측 — IBroker·소켓·onSend 참조 0. onBreach 는 ForceFreeze(세 벽 차단)+FailFast(자가 소멸).
            //     기본 dormant(Security.QuantumVaultArmed 옵트인 — dev 디버깅·오탐 자멸 방지). anti-hook 는 blockWhile tighten-only.
            var secureZeroizer = new VerifiedSecureZeroizer();
            var honeyMesh = new HoneyKeyDecoyMesh(decoyCount: 8);
            var antiHook  = new AntiHookingCodeIntegrityGuard();
            antiHookRef   = antiHook;   // 무장 술어 blockWhile 지연 ref 결속
            var rcuConfigPort = new AtomicConfigSnapshotPort<RuntimeConfigSnapshot>(RuntimeConfigSnapshot.Capture(new EngineConfigAdapter(cfg)));   // Phase 83 RCU(포트 경유)
            services.AddSingleton(rcuConfigPort);
            var quantumVault = new QuantumVaultSentinel(
                onBreach: reason => { try { attestation.ForceFreeze("퀀텀 금고 브리치 — " + reason); } catch { } Environment.FailFast("퀀텀 금고 자가 소멸 — " + reason); },
                zeroizer: secureZeroizer,
                extraDetector: () => !honeyMesh.CheckIntegrity() || !antiHook.CheckIntegrity());
            if (cfg.GetBool("Security.QuantumVaultArmed", false)) quantumVault.Start();   // ★옵트인 — 기본 dormant(오탐 자멸 방지)
            services.AddSingleton(secureZeroizer);
            services.AddSingleton(honeyMesh);
            services.AddSingleton(antiHook);
            services.AddSingleton(quantumVault);
            FormClosed += (_, _) => ShutdownTrace.Step("quantumVault", () => quantumVault.Dispose());
            // v9.0.0 — 스마트 오더 라우터 & 빙산호가 포획: SOR + 빙산 헌터 + 유동성 진위 인증(Think Beyond) + 큐 위치 추정.
            //   ★liveArmed:false: 라우팅 '경로/수치'만 산출 — IBroker·소켓·onSend·ring 참조 0. 실 발주는 하류 무장 게이트 전담.
            //     라이브 L2 오더북 피드 옵트인 시 활성(현 dormant·NanoPipeline 미부팅 관례 정합·순수 상태 객체라 Dispose 불요).
            var liqAuthVerifier = new LiquidityAuthenticityVerifier();
            var icebergHunter = new IcebergHunterSentinel();
            var queuePosEst = new QueuePositionEstimator();
            var smartRouter = new SmartOrderRouterEngine(iceberg: icebergHunter, auth: liqAuthVerifier);
            services.AddSingleton(liqAuthVerifier);
            services.AddSingleton(icebergHunter);
            services.AddSingleton(queuePosEst);
            services.AddSingleton(smartRouter);
            // v9.1.0 — 극단적 카오스 결함 주입 샌드박스: 결함 주입 엔진 + 결정론 재현기 + 단계별 복구 스로틀러 + ★Think Beyond 진행 보장 가드.
            //   ★liveArmed:false: 전부 순수 결정/관측 산출 — IBroker·소켓·onSend·ring 참조 0. SafeHold/RecoveryBlocking/가드는 무장 술어에
            //     tighten-only 합성(위에서 결속), 파멸 결함은 attestation.ForceFreeze 원자 격발. 실 주입은 샌드박스 인터셉터/테스트 소비(현 dormant).
            var chaosSanitizer = new ChaosEngineSanitizer(
                onForceFreeze: () => { try { attestation.ForceFreeze("카오스 샌드박스 파멸 결함 — 무장 하드 동결(극단 재난 fail-closed 실증)"); } catch { } });
            var chaosReplayer  = new DeterministicChaosReplayer();
            var chaosRecovery  = new ChaosRecoveryRateLimiter();
            var chaosGuard     = new ChaosResilientProgressGuard();
            chaosRef      = chaosSanitizer;   // 무장 술어 blockWhile 결속(tighten-only)
            chaosRecovRef = chaosRecovery;
            chaosGuardRef = chaosGuard;
            chaosGuard.BindInterlock(interlockMask, MS2E.Core.ArmingInterlockSource.ChaosProgress);
            services.AddSingleton(chaosSanitizer);
            services.AddSingleton(chaosReplayer);
            services.AddSingleton(chaosRecovery);
            services.AddSingleton(chaosGuard);
            // v10.0.0 — Genesis Live Ignition Gateway: 봉인 사출 인터락 + 포렌식 감사 + 하드웨어 키 펄스 심박 + 실전 점화 게이트웨이.
            //   ★liveArmed:false: 이중 물리 키(ValveWired ∧ LiveArmingWired)는 운영자 하드웨어 — 소프트웨어는 산출/봉인/감사만.
            //     valve/arming 미결속(기본)이면 강한 사출 시도도 실물 0비트. 키 펄스 단선 시 무장 하향(gateway 최종 빗장 + 하드 래치).
            var sealedEmit = new SealedEmissionInterlock();
            var ignAudit   = new IgnitionAuditTrailRecorder();
            var keyPulse   = new HardwareKeyPulseHeartbeatSentinel(
                onKeyLost: () => { try { ignition.Lock("하드웨어 키 펄스 지속 단선 — 무장 하드 차단(접점 불량)"); } catch { } },
                startTimer: true);
            keyPulseRef    = keyPulse;   // 무장 술어 blockWhile 결속(펄스 단선/미수신 → tighten-only)
            var ignGateway = new GenesisLiveIgnitionGateway(
                valveWired:      () => cfg.GetBool("Dispatch.ValveWired", false),
                liveArmingWired: () => cfg.GetBool("Dispatch.LiveArmingWired", false),
                defensesPass:    liveArmed,   // v10.1.0 — 게이트웨이는 실전 전용(드라이런 급전 술어 아님)
                keyPulseAlive:   () => keyPulse.KeyAlive,
                sealedEmission:  sealedEmit,
                audit:           ignAudit);
            services.AddSingleton(sealedEmit);
            services.AddSingleton(ignAudit);
            services.AddSingleton(keyPulse);
            services.AddSingleton(ignGateway);
            FormClosed += (_, _) => ShutdownTrace.Step("keyPulse", () => keyPulse.Dispose());
            // v10.1.0 — Live Flight Certification & Mock Dry-Run: 가상 체결 미러 + 전방위 인증 봉인기 + ★Think Beyond 스코프-봉인 전환 장벽.
            //   ★liveArmed:false: 미러는 IBroker·소켓 참조 0 — 드라이런은 오직 미러로만 체결(실물 0). barrier 가 드라이런/실전 라우팅 권위이며
            //     전환 찰나 가상 잔량 O(1) 퍼지 + 100ms 쿨다운(오염·유령 폭주 봉쇄). 인증 봉인 미완 시 실전 무장 차단(blockWhile).
            var mockMirror   = new MockExecutionOrderBookMirror();
            var scopeBarrier = new ScopeSealedTransitionBarrier(onPurge: () => mockMirror.Reset());   // 전환 시 가상 잔량 제로화
            scopeBarrier.SetInitialMode(cfg.GetBool("Dispatch.ValveWired", false)
                ? ScopeSealedTransitionBarrier.MODE_LIVE : ScopeSealedTransitionBarrier.MODE_DRYRUN);
            var certSealer   = new LiveFlightCertificationSealer(audit: ignAudit);
            // v10.1.0 Final Packaging — 나노 SLA 게이트키퍼 + ★Think-Beyond 지터 회귀 가드 + 릴리스 SHA-256 봉인기.
            //   ★liveArmed:false: 전부 순수 계측/판정/해시 — IBroker·소켓·onSend 참조 0. SLA 퓨즈 ForceFreeze 는 옵트인(Sla.FuseArmed·
            //     기본 미무장 → 이탈은 blockWhile tighten-only). 기대 봉인(Release.ExpectedMasterSeal) 미주입 = 미검증(마스터 인증 불가·fail-closed).
            var jitterGuard  = new SlaJitterRegressionGuard();
            var slaGate      = new LatencySlaGatekeeper(
                slaP9999Nanos: cfg.GetInt("Sla.P9999BudgetNanos", 500_000, 1000, 100_000_000),   // 드레인 사이클 P99.99 예산(기본 500µs)
                attestation:   cfg.GetBool("Sla.FuseArmed", false) ? attestation : null);          // ★ForceFreeze 옵트인(기본 tighten-only)
            // [봉인 운영] Release.ExpectedMasterSeal(= manifest v2 masterSeal)은 더 이상 여기(실행 DLL 단일 파일 해시 대조)에 주입하지 않는다 —
            //   두 검사에 같은 값을 넣어 어떤 설정으로도 무장이 불가능했던 교착 교정. 판정은 아래 BootIntegrityGate 결속 시 위임(그 전까지 차단).
            var releaseAttestor = new ReleaseArtifactSha256Attestor(audit: ignAudit);
            releaseAttestorRef = releaseAttestor;   // v12.14.0-GM — 무장 술어 blockWhile 결속(위 armedPredicate)
            // v10.1.0 Ops — 사전 점화 서명 + 카나리 거버너 + ★Think-Beyond 드리프트 센티널 + 패닉 스위치.
            //   ★liveArmed:false: 전부 순수 채점/판정 — IBroker·소켓·onSend 참조 0. 전 blockWhile tighten-only(무장 부여 불가).
            //     서명 미달/드리프트/카나리 강등/패닉 → 무장 하향. 카나리 캡은 LiveBroker 경로에서만·강등 시 드라이런으로 물러섬.
            var signoffValidator = new IgnitionSignoffValidator(audit: ignAudit);
            var driftSentinel    = new HostEnvironmentDriftSentinel(audit: ignAudit);
            var panicSwitch      = new OperatorDuressPanicSwitch(attestation: attestation, audit: ignAudit);
            var canaryGovernor   = new MicroCapitalCanaryGovernor(
                capitalKrwRaw: (long)cfg.GetInt("Canary.CapitalKrw", 10_000_000, 0, 2_000_000_000) * 100_000_000L,   // KRW → QFixed raw
                capBps: cfg.GetInt("Canary.CapBps", 10, 1, 10_000),                              // 기본 0.1%
                slippageThresholdBps: cfg.GetInt("Canary.SlippageThresholdBps", 50, 1, 100_000),
                onDowngrade: () => { try { scopeBarrier.Transition(ScopeSealedTransitionBarrier.MODE_DRYRUN); } catch { } },   // ★슬리피지 초과 → O(1) 드라이런 강등(안전 방향)
                audit: ignAudit);
            certSealerRef    = certSealer;     // 무장 술어 blockWhile 결속(유효 봉인 없으면 tighten)
            scopeBarrierRef  = scopeBarrier;   // 무장 술어(드라이런 급전 + 전환 쿨다운 tighten)
            slaGateRef       = slaGate;        // 무장 술어 blockWhile(SLA P99.99 이탈 tighten)
            jitterGuardRef   = jitterGuard;    // 무장 술어 blockWhile(측정 지터 회귀 tighten)
            signoffRef       = signoffValidator;   // 무장 blockWhile(서명 미달/리스 만료 tighten)
            driftRef         = driftSentinel;      // 무장 blockWhile(호스트 드리프트 tighten)
            canaryRef        = canaryGovernor;     // 무장 blockWhile(카나리 강등 tighten)
            panicRef         = panicSwitch;        // 무장 blockWhile(패닉 격발 tighten)
            services.AddSingleton(mockMirror);
            services.AddSingleton(scopeBarrier);
            services.AddSingleton(certSealer);
            services.AddSingleton(slaGate);
            services.AddSingleton(jitterGuard);
            services.AddSingleton(releaseAttestor);
            services.AddSingleton(signoffValidator);
            services.AddSingleton(canaryGovernor);
            services.AddSingleton(driftSentinel);
            services.AddSingleton(panicSwitch);
            // v10.1.0 Phase 93-B — Admin UI(MS2E.UI.Portable) Ports & Adapters 배선.
            //   ★liveArmed:false: Admin 명령은 금고/캐시/시크릿 관제 — IBroker·소켓 참조 0. ★Think-Beyond 권한 봉인 가드가
            //     세션 봉인으로 rogue Port 호출을 차단하고 ForceFreeze 시 revoke(봉인 무효 + 시크릿 퍼지). 어댑터가 seal 검증.
            var adminSecretAdapter = new AdminSecretPortAdapter((long _, Span<char> dest) =>
            {
                ReadOnlySpan<char> demo = "sk-ADMIN-0000-DEMO-KEY-ab";   // 데모(실 배선은 DPAPI 금고 decrypt→span). 프레젠터가 마스킹+0x00.
                int nn = Math.Min(demo.Length, dest.Length);
                demo.Slice(0, nn).CopyTo(dest);
                return nn;
            });
            var adminPresenter = new SecureAdminViewPresenter(adminSecretAdapter);
            var adminSeal      = new AdminAuthoritySealGuard(onRevoke: () => adminPresenter.PurgeNow());   // ★revoke → 시크릿 퍼지(freeze-safe)
            // ★세션 권한 키: 하드코딩 리터럴 금지 — 런타임 암호 RNG 로 예측 불가 비밀 생성(소스/바이너리 미노출). 0 회피(|1·미개시 방지).
            System.Span<byte> adminKeyBytes = stackalloc byte[8];
            System.Security.Cryptography.RandomNumberGenerator.Fill(adminKeyBytes);
            adminSeal.BeginSession(System.BitConverter.ToUInt64(adminKeyBytes) | 1UL);
            attestation.RegisterFreezeObserver(_ => adminSeal.Revoke());   // ★능동 freeze→revoke(봉인 무효 + 시크릿 즉시 퍼지·command-time lazy 아님)
            var adminCommandAdapter = new AdminCommandPortAdapter(adminSeal, attestation, ignAudit);
            var adminAuditAdapter   = new AdminAuditLogPortAdapter(ignAudit);
            var adminDispatcher     = new AdminCommandPortDispatcher(adminCommandAdapter, adminSeal, nowMicros: () => System.Diagnostics.Stopwatch.GetTimestamp());
            services.AddSingleton(adminSeal);
            services.AddSingleton(adminPresenter);
            services.AddSingleton(adminDispatcher);
            services.AddSingleton<IAdminCommandPort>(adminCommandAdapter);
            services.AddSingleton<IMailAuditLogPort>(adminAuditAdapter);
            services.AddSingleton<IAdminSecretPort>(adminSecretAdapter);
            // v10.1.0 Phase 94-A — UI 디커플링 인프라(MS2E.UI.Portable) 배선. ★liveArmed:false: 순수 UI 서비스 —
            //   레지스트리 조회·렌더 스로틀·경계 이벤트 전달뿐(IBroker·소켓·실 발주 유발 0).
            var uiPortRegistry   = new UniversalUiPortRegistry();
            var uiRenderGovernor = new StateAwareRenderGovernor();
            var uiEventBroker    = new UiEventBoundaryBroker(256);
            uiPortRegistry.Register<IAdminCommandPort>(adminCommandAdapter);   // 이관된 Admin Port 어댑터 등록
            uiPortRegistry.Register<IMailAuditLogPort>(adminAuditAdapter);
            uiPortRegistry.Register<IAdminSecretPort>(adminSecretAdapter);
            uiPortRegistry.Seal();                                             // 봉인 → 락프리 δ=0 읽기
            attestation.RegisterFreezeObserver(_ => uiRenderGovernor.SetFrozen(true));   // ★ForceFreeze → 렌더 하드 스로틀(DOM 스래싱 차단)
            services.AddSingleton(uiPortRegistry);
            services.AddSingleton(uiRenderGovernor);
            services.AddSingleton(uiEventBroker);
            // v12.8.0 — Dashboard 감사: StateAwareRenderGovernor(위) 는 이번에 Dashboard.razor 의 지수 자동갱신
            //   렌더 경로에 최초로 결속(이전엔 등록·ForceFreeze 결속뿐 실질 미가동이었음 — 실측 확인).
            //   WidgetLifecycleMemoryGuard(신규, Think Beyond B) — 위젯 핫스왑 시 GC 회수 검증용 진단 가드.
            services.AddSingleton(new MS2E.UI.Portable.Infrastructure.WidgetLifecycleMemoryGuard());
            // v10.2.0 Toss — 토스증권 조회 전용 포트폴리오 동기화 + 실시간 시세 게이트웨이(★read-only·liveArmed:false —
            //   발주 경로 무접촉·RejectOrderAttempt 하드 veto). 실 배선: tokenProvider=TossTokenService.CurrentAccessToken,
            //   accountFetch=HttpClient GET 계좌 엔드포인트(미구성 시 미인증→미동기화·fail-closed).
            // [피드 연동] ResilientOrderRouter 실배선 — 참조가 피드(Toss 틱 → 종목별 신선 참조가) + 체결/취소 통보 피드(키움 브리지 →
            //   추적 슬롯 수명주기 대사) + 슬롯 수명 샌니타이저(방향 A) + 피드 단절 회로 차단기(방향 B). 송출 직전 RouterFeedGate 가
            //   OrderDrainWorker 실전 경로에서 참조가·라우터 판정·추적 확보를 강제(드라이런 미러 경로 무영향 · liveArmed:false 불변).
            var refPriceFeed  = new StockManager.Adapters.ReferencePriceFeedAdapter(
                maxAgeMicros: cfg.GetInt("Router.RefPriceMaxAgeMs", 3000, 100, 600_000) * 1000L);
            // [실전 결선 · 다중 브로커] 브로커 인덱스: 0 = 키움(ExecutionBridgeBroker · 프록시 파이프) · 1 = KIS(KisLiveBroker · OAuth2 App Key/Secret).
            //   KIS 자격증명(ApiKeyVault KIS.AppKey/AppSecret/Account)이 없으면 인덱스 1 을 부팅 시 블랙리스트 — 라우터가 절대 선택하지 않는다
            //   (단일 브로커와 동일 동작 · 장애 전환 대상 없음 = 기존과 같은 이중 붕괴 판정).
            var kisCreds      = ApiKeyVault.GetKis(db);
            bool kisConfigured = !string.IsNullOrWhiteSpace(kisCreds.key) && !string.IsNullOrWhiteSpace(kisCreds.secret) && !string.IsNullOrWhiteSpace(kisCreds.account);
            var routerBoard   = new MS2E.Core.LockFreeBrokerSwitchboard(2);
            if (!kisConfigured) routerBoard.Blacklist(1);
            var routerPending = new MS2E.Core.PendingOrderCascadeCanceller(1024);
            var routerSentinel = new MS2E.Core.BrokerFailoverSentinel(routerBoard, pendingOrders: routerPending);
            var orderRouter   = new MS2E.Core.ResilientOrderRouter(routerBoard, routerSentinel, routerPending,
                idMapCapacityPow2: 2048, maxSlippageBps: cfg.GetInt("Router.MaxSlippageBps", 50, 1, 10_000));
            // [보호 지정가 · 방향 B] 체결가가 판정 참조가에서 극한 괴리(기본 200bps)로 체결되면 전체 발주 회로 sticky Disarm.
            var extremeSlip   = new StockManager.Engines.ExtremeSlippageDisarmInterlock(cfg.GetInt("Router.ExtremeSlippageBps", 200, 10, 5_000));
            var brokerMismatch = new StockManager.Adapters.BrokerStateMismatchInterlock();   // [키움 프로토콜 · 방향 B]
            var execFeed      = new StockManager.Adapters.OrderExecutionFeedAdapter(orderRouter, capacity: 1024,
                slotTtlMicros: cfg.GetInt("Router.SlotTtlSeconds", 120, 5, 3600) * 1_000_000L,
                onRecord: r => { try { Log.Warn($"[RouterRecon] {r.Kind} proposal={r.ProposalId} brokerOrder={r.BrokerOrderId} qty={r.Qty}"); } catch { } },
                extremeSlippage: extremeSlip,
                // [키움 프로토콜] 접수 확인(Accepted) 주문은 호가 대기 — 무응답 TTL(2분) 대신 장기 TTL · 거부는 즉시 반납
                acceptedTtlMicros: cfg.GetInt("Router.AcceptedTtlMinutes", 480, 5, 1440) * 60_000_000L,
                mismatch: brokerMismatch);
            var feedBreaker   = new StockManager.Adapters.FeedDisconnectCircuitBreaker(refPriceFeed, () => bridge.IsProxyAlive,
                maxPriceSilenceMicros: cfg.GetInt("Router.MaxPriceSilenceMs", 5000, 100, 600_000) * 1000L);
            // [보호 지정가 · 추천 A] 시장가 → 참조가 ± offset 보호 지정가(호가 단위 보호 방향 정렬 · 가격제한폭 클램프) 변환 + 1~3호가 깊이 가드.
            //   ★호가 깊이 피드 생산자는 아직 없다(Toss/키움 모두 L2 미파싱) — DepthBook 은 비어 있고 Router.MarketRequiresDepth(기본 true)면
            //   시장가는 '깊이 미상'으로 거부된다(미분류 fail-closed). 지정가는 가격이 보호선이라 영향 없음.
            long maxSlipBps   = orderRouter.MaxSlippageBps;
            var protectiveConv = new StockManager.Engines.ProtectiveLimitOrderConverter(
                Math.Min(cfg.GetInt("Router.ProtectiveOffsetBps", 30, 1, 1_000), maxSlipBps));
            var depthBook     = new StockManager.Engines.DepthBook(maxAgeMicros: cfg.GetInt("Router.DepthMaxAgeMs", 2000, 100, 600_000) * 1000L,
                maxSpreadBps: cfg.GetInt("Router.MaxSpreadBps", 300, 1, 10_000));   // [L2] 1호가 스프레드 상한(시장가 송출 거부)
            // [정정/취소·계좌 · 추천 A] OPW00018(보유)·OPW00001(예수금) 동기화 → 주문가능금액/보유수량 송출 게이트 + 정적 구간 잔고 괴리 Disarm.
            //   Router.RequireAccountSync(기본 true) = 스냅샷 미수신/만료 시 전 주문 거부(fail-closed · 스텁 트랜스포트에선 송출 0 이 정직한 상태).
            var accountDivergence = new StockManager.Adapters.AccountDivergenceInterlock();
            accountDivergence.BindInterlock(interlockMask, MS2E.Core.ArmingInterlockSource.AccountDivergence);
            var accountSync   = new StockManager.Adapters.AccountStateSyncAdapter(capacity: 256,
                staleMicros: cfg.GetInt("Account.StaleSeconds", 30, 15, 600) * 1_000_000L,
                interlock: accountDivergence,
                onRecord: r => { try { Log.Warn($"[AccountRecon] {r.Kind} symbol={r.BrokerOrderId} brokerQty={r.Qty}"); } catch { } });
            services.AddSingleton(accountDivergence);
            services.AddSingleton(accountSync);
            // [포트폴리오 리스크 · 추천 A] 단일 종목(기본 총자산 20%) · 섹터(기본 40%) 익스포저 상한 — RouterFeedGate(송출 전방)에서 매수 거부.
            //   총자산 = 예수금 + Σ 보유 × 마크가(키움 체결 틱). 평가 미상 = 거부(fail-closed). 섹터표 = Exposure.SectorTablePath("코드,섹터" UTF-8) · 미등재 = 미분류 버킷.
            //   보유 종목 동시 하락(체계적 상관 붕괴) → CorrelationCrashInterlock: 미체결 일괄 취소 + Disarm(필수 비트).
            var exposureGate  = new StockManager.Engines.PortfolioExposureGate(accountSync,
                cfg.GetInt("Exposure.SingleNameCapBps", (int)StockManager.Engines.PortfolioExposureGate.DefaultSingleNameCapBps, 0, 10_000),
                cfg.GetInt("Exposure.SectorCapBps", (int)StockManager.Engines.PortfolioExposureGate.DefaultSectorCapBps, 0, 10_000));
            string sectorTable = cfg.GetString("Exposure.SectorTablePath", "");
            if (sectorTable.Length > 0)
            {
                try { if (System.IO.File.Exists(sectorTable)) exposureGate.IngestSectorTable(System.IO.File.ReadAllBytes(sectorTable)); }
                catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException) { Log.Warn("[Exposure] 섹터표 판독 실패: " + ex.Message); }
            }
            services.AddSingleton(exposureGate);
            var correlationCrash = new StockManager.Engines.CorrelationCrashInterlock(
                cfg.GetInt("Correlation.DropBps", (int)StockManager.Engines.CorrelationCrashInterlock.DefaultDropBps, 50, 5_000),
                cfg.GetInt("Correlation.MinNames", StockManager.Engines.CorrelationCrashInterlock.DefaultMinNames, 2, 100),
                cfg.GetInt("Correlation.BreadthBps", (int)StockManager.Engines.CorrelationCrashInterlock.DefaultBreadthBps, 1_000, 10_000),
                openOrders: buf => execFeed.CopyOpenBrokerOrders(buf.Span),
                cancel: id => broker.Cancel(id.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                onTrip: n => { try { Log.Warn($"[CorrelationCrash] 보유 종목 동시 하락 — Disarm · 미체결 취소 {n}건 요청"); } catch { } });
            correlationCrash.BindInterlock(interlockMask, MS2E.Core.ArmingInterlockSource.CorrelationCrash);
            services.AddSingleton(correlationCrash);
            var routerGate    = new StockManager.Adapters.RouterFeedGate(orderRouter, refPriceFeed, execFeed, feedBreaker,
                converter: protectiveConv, depth: depthBook,
                requireDepthForMarket: cfg.GetBool("Router.MarketRequiresDepth", true),
                basePriceRaw: sym => spikeRef is { } sp && sp.TryGetReference(sym, out long b) && b < long.MaxValue / 100_000_000L ? b * 100_000_000L : 0L,
                account: cfg.GetBool("Router.RequireAccountSync", true) ? accountSync : null,
                exposure: exposureGate);
            // [정정/취소] 취소·정정 전방 게이트 = 체결 피드 추적 원장(종결/중복/잔량 초과 송신 0)
            broker.AmendGate = execFeed;
            extremeSlip.BindInterlock(interlockMask, MS2E.Core.ArmingInterlockSource.ExtremeSlippage);
            // [L2 · 추천 A] 키움 실시간 L2(주식호가잔량 10호가) → 파서 검증 → DepthBook(보호 지정가 1~3호가 깊이 · 스프레드 가드) +
            //   플래시 크래시 인터락. 기준가(현재가−전일대비) → MarketDataSpikeInterlock.SetReference(가격제한폭 밴드·클램프 활성).
            //   키움 체결 틱 → 이상 시세 인터락 통과분만 참조가 피드(Toss 미결선이어도 시장가 보호 지정가 판정 가능). ★Toss L2 는 스키마 미상 — 미결선.
            var illiquidity   = new StockManager.Engines.OrderbookIlliquidityInterlock(
                cfg.GetInt("Router.FlashSpreadBps", 1_000, 50, 10_000), StockManager.Engines.OrderbookIlliquidityInterlock.KrxContinuousSessionNow);
            illiquidity.BindInterlock(interlockMask, MS2E.Core.ArmingInterlockSource.OrderbookIlliquidity);
            services.AddSingleton(illiquidity);
            // [저지연] 키움 L2 10호가 원본 = GC 힙 밖 네이티브 seqlock 표(SafeHandle 결정론적 해제) · DepthBook 도 네이티브 저장소로 격상.
            var nativeBook    = new StockManager.Buffers.UnmanagedOrderBookBuffer();
            services.AddSingleton(nativeBook);
            // [저지연 · 방향 A/B] 주문별 Tick-to-Trade µs 프로파일러(최근 1,024건 P50/P99 · 네이티브 로그 히스토그램 P99.9) +
            //   송출 직전 정지 임계 초과 시 미송출·Disarm(필수 비트). 임계 기본 5,000µs.
            var t2tProfiler   = new StockManager.Telemetry.TickToTradeLatencyProfiler(cfg.GetInt("Router.StallThresholdMicros", 5_000, 500, 1_000_000));
            t2tProfiler.BindInterlock(interlockMask, MS2E.Core.ArmingInterlockSource.LatencyStall);
            services.AddSingleton(t2tProfiler);
            // [v12.17.6 · 실발주 경로 지연 계측] 시그널(링 발행) → 라우터 게이트 → 출구 초크포인트(센티널 앞) 구간 계측기. 결선 4곳:
            //   ① dispatchRing.PathObserver = 발행 · 종결 스탬프(단일 생산자 CAS 안 · 모든 처분 경로 — 방향 A 슬롯 즉시 회수)
            //   ② 드레인 routeGate = StageStampingSplitGate(routerGate) ③ 드레인 브로커 맵 = EgressStageProbe(센티널) ④ 관제 허브 orderPath.
            //   전부 StockManager.Telemetry(프로덕션) — 시뮬레이션 형식 참조 0(ShippedBinaryLiveArmedGuard ⑥). 핫패스 비용 = 시계 · 원자 증가 수회(할당 0).
            const int orderDrainPeriodMs = 50;
            var orderPathProfiler = new StockManager.Telemetry.OrderPathStageProfiler(1024);
            dispatchRing.PathObserver = orderPathProfiler;
            services.AddSingleton(orderPathProfiler);
            // [v12.17.6 · 방향 B] 심각 지연 자동 Disarm(비트 28 필수 · 항상 결속) — 예산 = 50ms 병목 허용치 + 드레인 주기(설계된 링 대기 · 오격발 방지).
            var orderPathLatency = new StockManager.Telemetry.OrderPathLatencyInterlock(orderPathProfiler,
                StockManager.Telemetry.OrderPathLatencyInterlock.HostBudgetNanos(orderDrainPeriodMs,
                    cfg.GetInt("Router.OrderPathBottleneckMs", 50, 5, 5_000) * 1_000_000L));
            orderPathLatency.BindInterlock(interlockMask, MS2E.Core.ArmingInterlockSource.OrderPathLatency);
            services.AddSingleton(orderPathLatency);
            FormClosed += (_, _) => ShutdownTrace.Step("nativeBuffers", () => { nativeBook.Dispose(); depthBook.Dispose(); t2tProfiler.Dispose(); });
            Action<OrderBookPayload> onBridgeBook = book =>
            {
                nativeBook.Write(in book);   // 판정 무관 원본 10호가 보존(네이티브)
                var v = L2OrderBookDepthParser.Validate(in book, out var depth, out long spreadBps);
                illiquidity.Observe(v, spreadBps);
                // 비정상 판정이면 depth = 빈 스냅샷으로 덮어써 직전 정상 스냅샷의 잔존 사용을 차단(깊이 0 = 시장가 거부 · fail-closed)
                depthBook.OnDepth(StockManager.Adapters.ReferencePriceFeedAdapter.SymbolHashForCode(book.SymbolCode), in depth);
            };
            Action<ReferencePricePayload> onBridgeBase = rp =>
            {
                long sym = StockManager.Adapters.ReferencePriceFeedAdapter.SymbolHashForCode(rp.SymbolCode);
                spikeRef?.SetReference(sym, rp.BasePrice);   // 원 단위 — Toss/키움 틱 가격과 같은 단위
            };
            Action<TickPayload> onBridgeTick = t =>
            {
                if (!(t.Price > 0) || t.Price > 1e12) return;
                long sym = StockManager.Adapters.ReferencePriceFeedAdapter.SymbolHashForCode(t.SymbolCode);
                long px = (long)Math.Round(t.Price, MidpointRounding.AwayFromZero);
                if (spikeRef is { } sp && sp.Observe(sym, px) == MS2E.Infrastructure.SpikeVerdict.Accepted) refPriceFeed.OnTick(sym, px, 0);
                exposureGate.OnMark(t.SymbolCode, px);          // [포트폴리오] 보유 평가 마크가
                correlationCrash.OnTick(t.SymbolCode, px);      // [포트폴리오 · 방향 B] 세션 고점 대비 동시 낙폭
            };
            // [키움 COM · 방향 B] 프록시 ErrorReport(71xx 중대 서버 오류 · STA 펌프 동결) → 집계 비트(동결은 기존 KiwoomFreezeRelay 가 병행)
            var kiwoomServerErr = new StockManager.Adapters.KiwoomServerErrorInterlock();
            kiwoomServerErr.BindInterlock(interlockMask, MS2E.Core.ArmingInterlockSource.KiwoomServerError);
            services.AddSingleton(kiwoomServerErr);
            Action<ErrorPayload> onBridgeError = kiwoomServerErr.OnError;
            bridge.ErrorReceived += onBridgeError;
            FormClosed += (_, _) => ShutdownTrace.Step("kiwoomServerErr", () => bridge.ErrorReceived -= onBridgeError);
            bridge.OrderBookReceived      += onBridgeBook;
            bridge.ReferencePriceReceived += onBridgeBase;
            bridge.TickReceived           += onBridgeTick;
            FormClosed += (_, _) => ShutdownTrace.Step("l2Feeds", () =>
            {
                bridge.OrderBookReceived -= onBridgeBook; bridge.ReferencePriceReceived -= onBridgeBase; bridge.TickReceived -= onBridgeTick;
            });
            brokerMismatch.BindInterlock(interlockMask, MS2E.Core.ArmingInterlockSource.BrokerStateMismatch);
            services.AddSingleton(brokerMismatch);
            services.AddSingleton(extremeSlip);
            services.AddSingleton(protectiveConv);
            services.AddSingleton(depthBook);
            Action<FillPayload> onBridgeFill = execFeed.OnFill;
            bridge.FillReceived += onBridgeFill;
            // [계좌] 브리지 계좌 프레임 + 체결(추정 보유) → 동기화 어댑터 · 주기 조회(조회 TR 2건/회 · 기본 10초 = 분당 12건 ≪ 100)
            Action<FillPayload> onExposureFill = exposureGate.OnFill;
            bridge.FillReceived += onExposureFill;
            FormClosed += (_, _) => ShutdownTrace.Step("exposureFill", () => bridge.FillReceived -= onExposureFill);
            Action<FillPayload> onAccountFill = accountSync.OnFill;
            Action<AccountCashPayload> onAccountCash = accountSync.OnCash;
            Action<PositionPayload> onAccountPosition = accountSync.OnPosition;
            bridge.FillReceived        += onAccountFill;
            bridge.AccountCashReceived += onAccountCash;
            bridge.PositionReceived    += onAccountPosition;
            int accountQuerySec = cfg.GetInt("Account.QueryIntervalSeconds", 10, 10, 600);
            var accountQuery  = new System.Threading.Timer(_ =>
            {
                if (!bridge.IsProxyAlive) return;
                try { accountSync.MarkQueryRequested(); broker.QueryAccount(); } catch { }
            }, null, accountQuerySec * 1000, accountQuerySec * 1000);
            FormClosed += (_, _) => ShutdownTrace.Step("accountSync", () =>
            {
                accountQuery.Dispose();
                bridge.FillReceived -= onAccountFill; bridge.AccountCashReceived -= onAccountCash; bridge.PositionReceived -= onAccountPosition;
            });
            feedBreaker.BindInterlock(interlockMask, MS2E.Core.ArmingInterlockSource.RouterFeed);
            execFeed.BindInterlock(interlockMask, MS2E.Core.ArmingInterlockSource.OrderSlotAge);
            long[] heldSym = new long[512], heldQty = new long[512];   // [포트폴리오 · 방향 B] 보유 집합 갱신 버퍼(타이머 단일 스레드)
            var routerSweep   = new System.Threading.Timer(_ =>
            {
                try { execFeed.SweepExpired(); } catch { }
                try { int hn = accountSync.CopyPositions(heldSym, heldQty); if (hn >= 0) correlationCrash.SetHeld(heldSym.AsSpan(0, hn)); } catch { }
                try { feedBreaker.Evaluate(); } catch { }
            }, null, 1000, 1000);
            FormClosed += (_, _) => ShutdownTrace.Step("routerFeeds", () => { routerSweep.Dispose(); bridge.FillReceived -= onBridgeFill; });
            services.AddSingleton(refPriceFeed);
            services.AddSingleton(orderRouter);
            services.AddSingleton(execFeed);
            // [롤오버 · 추천 A] 선행 프로세스가 MMF 에 승계 슬롯을 발행해 두었으면 체크섬 검증 후 원자 승계 + HandoffAck.
            //   불일치 = sticky Rollover 비트 · 승계 성공이어도 브로커 대사(MarkReconciled) 전까지 신규 발주 차단(Fail-Closed).
            //   승계 슬롯의 최대 클라이언트 ID 이하로는 새 주문 ID 를 발급하지 않는다(충돌 = 오대사).
            var rolloverGuard = StockManager.Services.ProcessRolloverGuard.CreatePublisher(
                cfg.GetString("Rollover.MapName", StockManager.Services.ProcessRolloverGuard.DefaultMapName));
            var adoptResult = rolloverGuard.TryAdopt(execFeed);
            if (adoptResult == StockManager.Services.RolloverAdoptResult.Adopted)
            {
                broker.SeedOrderIdFloor(execFeed.MaxTrackedBrokerOrderId(MS2E.Infrastructure.KisOrderIdSpace.Namespace));
                Log.Warn($"[Rollover] 선행 프로세스 슬롯 {rolloverGuard.AdoptedCount}건 승계 — 브로커 대사 전까지 신규 발주 차단");
            }
            else if (adoptResult != StockManager.Services.RolloverAdoptResult.NoHandoff)
                Log.Warn($"[Rollover] 승계 실패 {adoptResult} — 신규 발주 차단(sticky)");
            rolloverGuard.BindInterlock(interlockMask, MS2E.Core.ArmingInterlockSource.Rollover);
            services.AddSingleton(rolloverGuard);
            // 종료 시: 추적 슬롯이 남아 있으면 발행 → 후속 프로세스 HandoffAck 대기 → 미확인이면 운용자 확인 없이는 종료하지 않는다.
            int ackWaitSec = cfg.GetInt("Rollover.AckWaitSeconds", 5, 0, 300);
            _rolloverCloseGate = () =>
            {
                if (execFeed.Tracked == 0) return true;
                var pub = rolloverGuard.Publish(execFeed, DateTime.UtcNow.Ticks);
                if (pub == StockManager.Services.RolloverPublishResult.Published && rolloverGuard.WaitForAck(TimeSpan.FromSeconds(ackWaitSec))) return true;
                var answer = MessageBox.Show(
                    $"미체결 추적 주문 {execFeed.Tracked}건이 후속 프로세스로 승계 확인되지 않았습니다({pub}).\n종료하면 이 주문들의 체결·취소를 이 앱이 더 이상 추적하지 않습니다.\n그래도 종료하시겠습니까?",
                    "롤오버 승계 미확인", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
                return answer == DialogResult.Yes;
            };
            FormClosed += (_, _) => ShutdownTrace.Step("rolloverGuard", () => rolloverGuard.Dispose());
            // [미체결 대사 · 추천 A] 브로커 미체결 원장(OPT10075) ↔ 호스트 추적 원장 1:1 대사 — 최초 확정 전 신규 발주 차단(비트 22) ·
            //   모든 승계 슬롯 번호·잔량 일치 = Rollover 비트(20) 0ms 해제 · 유령 주문(브로커에만 존재) = Disarm + 비상 일괄 취소(패스당 ≤4건).
            //   조회 TR: 미확정/유령 정리 중 3초 · 평시 30초(계좌 조회 12건/분 + 미체결 ≤20건/분 ≪ 100).
            Action<StockManager.Adapters.OpenOrderPassResult>? kisPassHook = null;     // [KIS 미체결 대사] 패스 결과 라우팅(조회기는 뒤에서 생성)
            var openOrderRecon = new StockManager.Adapters.BrokerOpenOrderReconciler(execFeed, broker, rolloverGuard.MarkReconciled,
                requireKiwoom: true,
                maxCancelsPerPass: cfg.GetInt("OpenOrder.MaxCancelsPerPass", StockManager.Adapters.BrokerOpenOrderReconciler.DefaultMaxCancelsPerPass, 1, 4),
                onPass: r =>
                {
                    if (r.Verdict >= StockManager.Adapters.OpenOrderVerdict.Divergence)
                        try { Log.Warn($"[OpenOrder] 대사 {r.Verdict} — 브로커 {r.Rows}행 · 원장 {r.Ledger}건 · 불일치 {r.Mismatched} · 유령 {r.Ghosts} · 취소 {r.CancelsSent}건 송신 · 무장 해제로 억제 {r.CancelsSuppressed}건"); } catch { }
                    Volatile.Read(ref kisPassHook)?.Invoke(r);
                },
                interval: TimeSpan.FromSeconds(cfg.GetInt("OpenOrder.IntervalSeconds", 30, 5, 600)));
            openOrderRecon.BindInterlock(interlockMask, MS2E.Core.ArmingInterlockSource.OpenOrderDivergence);
            // [v12.18.7-DEV · 무장 가드] 유령 비상 취소(키움 · KIS TTTC0803U) = 실전 무장(liveArmed — 물리 키 ∧ 전 방어선)일 때만 송신.
            //   무장 해제 = 송신 0 · 비트 22 Disarm 은 그대로(대사기가 판정 직후 · 자기 Disarm 이전에 1회 표본). 드라이런 스코프는 허가하지 않는다.
            openOrderRecon.AttachCancelArming(liveArmed);
            Action<OpenOrderPayload> onOpenOrderRow = openOrderRecon.OnKiwoomRow;
            Action<OrderMappedPayload> onOrderMapped = execFeed.OnOrderMapped;
            bridge.OpenOrderReceived   += onOpenOrderRow;
            bridge.OrderMappedReceived += onOrderMapped;
            var openOrderPump = new System.Threading.Timer(_ => { try { openOrderRecon.Pump(); } catch { } }, null, 1000, 1000);
            services.AddSingleton(openOrderRecon);
            FormClosed += (_, _) => ShutdownTrace.Step("openOrderRecon", () =>
            {
                openOrderPump.Dispose();
                bridge.OpenOrderReceived -= onOpenOrderRow; bridge.OrderMappedReceived -= onOrderMapped;
            });
            // [헬스 · 추천 A] 무할당 헬스 에이전트(전용 스레드 50ms) — Working Set · 스레드풀 정체 · GC 정지 폭주 → HealthAgent 비트(0ms).
            //   [방향 A+B] 스캔마다 비관리 고정 하트비트(MMF)를 사상 → 키움 프록시가 주문 송신 직전 판독(박동 200ms 초과/트립 = 송신 거부).
            //   기동 유예(기본 30초) — 부팅 초기화의 스레드풀 점유를 sticky 트립으로 오판하지 않도록.
            var hostHeartbeat = new MS2E.Infrastructure.HostHeartbeatWriter();
            var healthAgent = new StockManager.Telemetry.HealthMonitoringAgent(
                maxWorkingSetBytes: cfg.GetInt("Health.MaxWorkingSetMB", 1024, 128, 65_536) * 1024L * 1024L,
                maxStallMicros: cfg.GetInt("Health.MaxStallMs", 100, 10, 10_000) * 1000L,
                maxGcPausePerScan: TimeSpan.FromMilliseconds(cfg.GetInt("Health.MaxGcPausePerScanMs", 200, 10, 10_000)),
                heartbeat: hostHeartbeat,
                warmup: TimeSpan.FromSeconds(cfg.GetInt("Health.WarmupSeconds", 30, 0, 600)));
            healthAgent.BindInterlock(interlockMask, MS2E.Core.ArmingInterlockSource.HealthAgent);
            healthAgent.Start(TimeSpan.FromMilliseconds(cfg.GetInt("Health.ScanIntervalMs", 50, 20, 1_000)));
            services.AddSingleton(healthAgent);
            FormClosed += (_, _) => ShutdownTrace.Step("healthAgent", () => { healthAgent.Dispose(); hostHeartbeat.Dispose(); });
            services.AddSingleton(feedBreaker);
            services.AddSingleton(routerGate);
            var tossSeqGuardian      = new SequenceContinuityGuardian();
            var marketSpike          = new MS2E.Infrastructure.MarketDataSpikeInterlock();   // [시세 하드닝] 틱 점프 10% · 가격제한폭 ±30%(기준가 주입 시)
            spikeRef = marketSpike;
            marketSpike.BindInterlock(interlockMask, MS2E.Core.ArmingInterlockSource.MarketSpike);
            services.AddSingleton(marketSpike);
            var tossGateway          = new TossWebSocketMarketDataGateway(tossSeqGuardian, spikeGuard: marketSpike, tickSink: refPriceFeed);   // [피드 연동] 수용 틱 → 참조가
            // v10.2.1 Toss Live REST 실통신 — OAuth Basic 토큰 교환(TossTokenService 재사용·백그라운드 선제 갱신·락-프리 핫패스)
            //   + Bearer 잔고 GET(/v1/accounts/holdings) + TossLiveApiGovernor(레이트/429/서킷) 게이팅 + TossJsonParser δ=0 파싱.
            //   ★조회 전용·liveArmed:false: 발주 메서드 0. 자격증명은 금고에서 콜드 패스에서만 복호화(requireArmed:false 읽기전용).
            //   ★attestation 미전달: 잔고-읽기 토큰 실패(미저장/네트워크)가 주문 시스템을 ForceFreeze 하지 않도록 격리(fail-closed 국소화).
            var tossLive = new TossOpenApiLiveAdapter(
                securityVault, clientIdKey: "toss.apiKey", clientSecretKey: "toss.secret", accountKey: "toss.account",
                clock: static () => DateTime.UtcNow.Ticks);
            tossLive.Start();   // 백그라운드 토큰 갱신 루프(핫패스 무접촉·미저장 시 저비용 fail-fast)
            services.AddSingleton(tossLive);
            FormClosed += (_, _) => ShutdownTrace.Step("tossLive", () => tossLive.Dispose());
            var tossPortfolioAdapter = new TossPortfolioSyncAdapter(
                tokenProvider: tossLive.AccessToken,      // 락-프리 δ=0 토큰(없음/만료 시 "" — fail-closed)
                accountFetch:  tossLive.FetchAccountJson, // 거버너 게이팅 실 HTTP GET(실패/rate/429/회로 → null)
                readOnlyKey:   true);                     // ★조회 전용 키 선언(write 가능 키 오용 차단)
            services.AddSingleton(tossPortfolioAdapter);
            // Phase 94-B — 포트폴리오 조회 Port(DIP): 순수 UI 는 IPortfolioQueryPort 만 알고 콘크리트 로딩은 어댑터가 담당.
            services.AddSingleton<MS2E.UI.Portable.Ports.IPortfolioQueryPort, PortfolioQueryAdapter>();
            // Phase 94-C — Risk/Quant 엔진 Port(DIP): 순수 UI 는 3 Port 만 알고 콘크리트 엔진은 어댑터가 격리(Task.Run 오프로드·취소).
            services.AddSingleton<MS2E.UI.Portable.Ports.Quant.IRiskAnalysisPort, MS2E.Infrastructure.RiskAnalysisAdapter>();
            services.AddSingleton<MS2E.UI.Portable.Ports.Quant.IMonteCarloPort, MS2E.Infrastructure.MonteCarloAdapter>();
            services.AddSingleton<MS2E.UI.Portable.Ports.Quant.IScenarioPort, MS2E.Infrastructure.ScenarioAdapter>();
            // v11.3.0 — 이중 브로커 관제 Port(조회 전용·DIP): 등록된 브로커 서비스(키움 bridge·토스 IBrokerAccountPort·토큰/거버너)만
            //   소비하고 미배선은 fail-closed(N/A). ★발주 무장 0(liveArmed:false) — 상태 판독 전용.
            services.AddSingleton<MS2E.UI.Portable.Ports.Brokers.IDualBrokerStatusPort>(sp => new MS2E.Infrastructure.DualBrokerStatusAdapter(sp));
            // Phase 94-D — 전략/백테스트 엔진 Port(DIP): UI 는 IStrategyLabPort/IBacktestExecutionPort 만 알고 시세/DB/엔진은 어댑터가 격리(내부 fetch·오프로드).
            services.AddSingleton<MS2E.UI.Portable.Ports.Strategy.IStrategyLabPort, MS2E.Infrastructure.StrategyLabAdapter>();
            services.AddSingleton<MS2E.UI.Portable.Ports.Strategy.IBacktestExecutionPort, MS2E.Infrastructure.BacktestExecutionAdapter>();
            // Phase 94-E — 전략 허브 인프라 파사드 Port(DIP·심층 의존성 해체): 시세이력 스트리밍(경계고정 BoundedQuoteWindow)·
            //   설정/후보군 DB·비동기 알림·심볼컨텍스트를 어댑터가 격리(레거시 페이지의 LocalDatabase/IQuoteProvider/ToastService/SymbolContext 직결 제거 기반).
            services.AddSingleton<MS2E.UI.Portable.Ports.Strategy.IStrategyHubInfraPort, MS2E.Infrastructure.StrategyHubInfraAdapter>();
            // Phase 94-E — 전략 단조소 Port(DIP): ForgePage 이주 기반. StrategyForge 발굴 + 카트리지 서명·내보내기를 어댑터가 격리.
            services.AddSingleton<MS2E.UI.Portable.Ports.Strategy.IStrategyForgePort, MS2E.Infrastructure.StrategyForgeAdapter>();
            // Phase 94-F — 옵티마이저 실행 + 복합 알림 Port(DIP): OptimizerPage 이주 기반. WalkForward/CompositeAlertEngine 격리.
            services.AddSingleton<MS2E.UI.Portable.Ports.Strategy.IOptimizerExecutionPort, MS2E.Infrastructure.OptimizerExecutionAdapter>();
            services.AddSingleton<MS2E.UI.Portable.Ports.Strategy.ICompositeAlertPort, MS2E.Infrastructure.CompositeAlertAdapter>();
            // Phase 94-F — 시그널 저장소 + 서비스 Port(DIP): SignalPage 이주 기반. 원시 SQL/DataTable/DashboardService/SampleData/AlertCenter/BacktestEngine 격리.
            services.AddSingleton<MS2E.UI.Portable.Ports.Strategy.ISignalsRepositoryPort, MS2E.Infrastructure.SignalsRepositoryAdapter>();
            services.AddSingleton<MS2E.UI.Portable.Ports.Strategy.ISignalsServicePort, MS2E.Infrastructure.SignalsServiceAdapter>();
            // v11.4.0 — 카나리 폭발 반경 격리 센티널(기본 disarmed·★물리 이중 키 없이는 무장 불가 → 발주 인가 0). 상위 발주 경로가 이 게이트 없이는 진행 못 하도록 배선 대상.
            services.AddSingleton<MS2E.Infrastructure.CanaryHealthSentinel>();
            // v11.5.0 — 하드웨어 킬스위치 브릿지(MS2E.Core) + Think-Beyond(스핀락 인터럽트·휘발성 메모리 퍼저). Killed 이벤트는 아래 sp 생성 후 CanaryHealthSentinel·Purger 에 결선.
            var killPurger = new MS2E.Core.VolatileMemoryPurger();
            var killBridge = new MS2E.Core.HardwareKillSwitchBridge();
            // v12.0.2 Think-Beyond — ★하드웨어 펄스 안티탬퍼 가드(MS2E.Core): 물리 접점 바운스(채터)·전기 노이즈·위조(재생/역행)
            //   신호를 단조 토큰 + 디바운스 창으로 걸러 킬스위치 오격발/증폭을 봉쇄. Accept∧Tamper=격발(fail-closed 안전측 Disarm),
            //   Debounced=억제. 디바운스 창 = Stopwatch 틱 5ms(200Hz — 접점 채터 흡수). 격발 sticky → 정당 킬 유실 0(첫 펄스 항상 Accept).
            var pulseGuard = new MS2E.Core.HardwarePulseGuard(System.Diagnostics.Stopwatch.Frequency / 200);
            long hwPulseSeq = 0;
            var killSpin = new MS2E.Core.HardwareInterruptSpinLock(reason =>
            {
                long seq = System.Threading.Interlocked.Increment(ref hwPulseSeq);   // 원자 단조 펄스 토큰(재생/역행 = 위조 판정)
                if (pulseGuard.ShouldFire(seq, System.Diagnostics.Stopwatch.GetTimestamp(), out var verdict))
                    killBridge.Signal(reason);   // 정당 물리 동작 또는 위조(fail-closed) → 킬. 접점 바운스(Debounced)는 억제.
                System.Diagnostics.Debug.WriteLine($"[PulseGuard] seq={seq} reason={reason} verdict={verdict}");   // Release 에선 [Conditional] 로 제거(δ=0 유지)
            });
            services.AddSingleton<MS2E.Core.IHardwareKillSwitchPort>(killBridge);
            services.AddSingleton(killBridge);
            services.AddSingleton(killPurger);
            // [사전 검증 · 추천 A] 부팅/런타임 Fail-Closed 게이트 — ①시계 역행(부팅 앵커 · 런타임 벽시계↔QPC) ②안전 설정 런타임 변조(지문)
            //   ③키움 프록시 파이프 서버 신원 불일치 → 필수 비트 Raise + killPurger(API Secret) 즉시 소거. 복구 = 원인 제거 후 재시작.
            var bootPreFlight = new StockManager.Security.BootPreFlightSentinel(killPurger,
                onTrip: f => { try { Log.Warn($"[PreFlight] 사전 검증 실패 {f} — 무장 차단 · 자격증명 소거"); } catch { } });
            bootPreFlight.BindInterlock(interlockMask, MS2E.Core.ArmingInterlockSource.PreFlightBoot);
            services.AddSingleton(bootPreFlight);
            string clockAnchorPath = StockManager.Security.BootPreFlightSentinel.DefaultAnchorPath();
            long bootNow = DateTime.UtcNow.Ticks;
            if (bootPreFlight.EvaluateBootClock(bootNow, StockManager.Security.BootPreFlightSentinel.ReadClockAnchor(clockAnchorPath)) == StockManager.Security.PreFlightFault.None)
                StockManager.Security.BootPreFlightSentinel.AdvanceClockAnchor(clockAnchorPath, bootNow);   // 되감긴 시계로는 절대 덮어쓰지 않음
            byte[] safetyFp = new byte[32];
            StockManager.Security.BootPreFlightSentinel.ComputeConfigFingerprint(rawConfig, safetyFp);
            bootPreFlight.SealConfig(safetyFp);
            // 프록시 파이프 신원: 설치 레이아웃(<root>/host · <root>/proxy) 또는 평면 배치에서 프록시 실행 파일 경로를 기대값으로 고정
            string proxyExe = cfg.GetString("Kiwoom.ProxyPath", "");
            if (proxyExe.Length == 0)
            {
                string layered = System.IO.Path.GetFullPath(System.IO.Path.Combine(AppContext.BaseDirectory, "..", "proxy", "MS2E.KiwoomProxyHost.exe"));
                proxyExe = System.IO.File.Exists(layered) ? layered : System.IO.Path.Combine(AppContext.BaseDirectory, "MS2E.KiwoomProxyHost.exe");
            }
            bridge.ServerIdentityVerifier = MS2E.Infrastructure.PipeServerIdentity.ImagePathVerifier(proxyExe);
            Action<int> onProxyIdentityRejected = _ => bootPreFlight.ReportProxyIdentityRejected();
            bridge.ServerIdentityRejected += onProxyIdentityRejected;
            var preFlightTimer = new System.Threading.Timer(_ =>
            {
                try
                {
                    bootPreFlight.ObserveClock(DateTime.UtcNow.Ticks, System.Diagnostics.Stopwatch.GetTimestamp(), System.Diagnostics.Stopwatch.Frequency);
                    StockManager.Security.BootPreFlightSentinel.ComputeConfigFingerprint(rawConfig, safetyFp);   // 타이머 단일 스레드 — 버퍼 재사용
                    bootPreFlight.ObserveConfigFingerprint(safetyFp);
                    if (!bootPreFlight.Tripped) StockManager.Security.BootPreFlightSentinel.AdvanceClockAnchor(clockAnchorPath, DateTime.UtcNow.Ticks);
                }
                catch { }
            }, null, 5_000, 5_000);
            FormClosed += (_, _) => ShutdownTrace.Step("bootPreFlight", () => { preFlightTimer.Dispose(); bridge.ServerIdentityRejected -= onProxyIdentityRejected; });
            // [키움 실배선] 브리지 실연결 — 실 COM 프록시(ProxyHello=ProductionCom)만 송출 대상(시뮬레이션 프록시 = 송출 거부 · 링크 비트 25 차단).
            //   Kiwoom.ConnectEnabled(기본 false) 일 때만 감독자가 (선택) 프록시 기동 → 연결(신원 검증) → 수신 시작. 단절 후 자동 재기동 없음(브리지 전역 동결).
            broker.RequireProductionProxy = true;
            bool kiwoomConnect = cfg.GetBool("Kiwoom.ConnectEnabled", false);
            var kiwoomLink = new StockManager.Adapters.KiwoomBridgeLinkInterlock(() => bridge.IsProductionProxy, required: kiwoomConnect);
            kiwoomLink.BindInterlock(interlockMask, MS2E.Core.ArmingInterlockSource.KiwoomBridgeLink);
            services.AddSingleton(kiwoomLink);
            Action<MS2E.Infrastructure.ProxyHelloPayload> onProxyHello = h =>
            {
                kiwoomLink.Evaluate();
                try { Log.Warn($"[KiwoomBridge] 프록시 선언 ApiKind={h.ApiKind} pid={h.ProcessId}" + (h.ApiKind == MS2E.Infrastructure.ProxyApiKind.ProductionCom ? "" : " — 시뮬레이션 프록시: 실 송출 거부")); } catch { }
            };
            Action onBridgeLost = kiwoomLink.Evaluate;
            bridge.ProxyHelloReceived += onProxyHello;
            bridge.ProxyLost += onBridgeLost;
            StockManager.Adapters.KiwoomProxySupervisor? kiwoomSupervisor = null;
            System.Threading.CancellationTokenSource? kiwoomCts = null;
            if (kiwoomConnect)
            {
                kiwoomSupervisor = new StockManager.Adapters.KiwoomProxySupervisor(
                    ct => bridge.ConnectAsync(".", ct), ct => bridge.StartAsync(ct), proxyExe, autoStart: cfg.GetBool("Kiwoom.AutoStartProxy", false));
                kiwoomCts = new System.Threading.CancellationTokenSource();
                _ = Task.Run(() => kiwoomSupervisor.RunAsync(kiwoomCts.Token));
                services.AddSingleton(kiwoomSupervisor);
            }
            FormClosed += (_, _) => ShutdownTrace.Step("kiwoomSupervisor", () =>
            {
                bridge.ProxyHelloReceived -= onProxyHello; bridge.ProxyLost -= onBridgeLost;
                try { kiwoomCts?.Cancel(); } catch { }
                kiwoomCts?.Dispose(); kiwoomSupervisor?.Dispose();
            });
            // [F7] 부팅 무결성 게이트: 검증은 스레드풀(UI 부팅 비차단·락 공유 0 → 초기화 데드락 불가), 완료 전 Verifying = 무장 차단.
            //   변조 Trip → killPurger(API Secret) 즉시 0x00 소거 + 킬스위치 격발(카나리 Kill 결선 경로 재사용). 30초 주기 백그라운드 재검증.
            try
            {
                var bootIntegrity = MS2E.Infrastructure.BootIntegrityGate.Start(
                    AppContext.BaseDirectory, cfg.GetString("Release.ExpectedMasterSeal", ""), killPurger,
                    onTrip: _ => { try { killBridge.Signal(MS2E.Core.KillSwitchReason.Manual); } catch { } },
                    requireExternalAnchor: true);   // [봉인 운영] 외부 앵커 미주입 = Unanchored(무장 차단) · 불일치 = 소거·킬
                bootIntegrityRef = bootIntegrity;
                releaseAttestor.DelegateToManifestGate(bootIntegrity);   // 릴리스 봉인 판정 단일 권위
                bootIntegrity.BindInterlock(interlockMask, MS2E.Core.ArmingInterlockSource.BootIntegrity);   // 검증 중(Verifying) = 비트 유지
                services.AddSingleton(bootIntegrity);
                FormClosed += (_, _) => ShutdownTrace.Step("bootIntegrity", () => bootIntegrity.Dispose());
            }
            catch { /* 결속 실패 → bootIntegrityRef=null → blockWhile ?? true(무장 차단) */ }
            // [PII 하드닝] 원문 자격증명 유출 인터락 싱크 결속 — Trip 시 API Secret 전량 0x00 + 킬스위치 격발(카나리 Kill 경로 재사용).
            MS2E.Infrastructure.RawCredentialLeakInterlock.Shared.Bind(killPurger,
                onTrip: () => { try { killBridge.Signal(MS2E.Core.KillSwitchReason.Manual); } catch { } });
            MS2E.Infrastructure.RawCredentialLeakInterlock.Shared.BindInterlock(interlockMask, MS2E.Core.ArmingInterlockSource.CredentialLeak);
            services.AddSingleton(killSpin);
            services.AddSingleton(pulseGuard);
            // v11.4.2 Think-Beyond — Stage 1 드라이런 이중 엔진: ①레이턴시 거버너(δ=0 락프리 — UI 패닉 방어) ②Zero-Leak Veto 인터락(O(1) fail-closed + 변조불가 감사). 인터락이 거버너 breach 를 소비(융합).
            var canaryLatencyGovernor = new MS2E.Infrastructure.CanaryVetoLatencyGovernor();
            var zeroLeakVetoInterlock = new MS2E.Infrastructure.ZeroLeakVetoInterlock(canaryLatencyGovernor);
            services.AddSingleton(canaryLatencyGovernor);
            services.AddSingleton(zeroLeakVetoInterlock);
            // v11.4.3 Think-Beyond — Stage 2 Micro-Arming 이중 엔진: ①원자적 마이크로캡 누산기(락프리 CAS·동시 발주 cap 초과 race 봉쇄) ②변조불가 토큰 소거기(0x00·재참조 차단).
            services.AddSingleton<MS2E.Infrastructure.AtomicMicroCapAccumulator>();
            services.AddSingleton<MS2E.Infrastructure.TamperProofTokenZeroizer>();
            // v11.4.4 Think-Beyond — Stage 3 실시간 안전 이중 엔진: ①Flight Recorder+메모리 퍼저 ②다중 텔레메트리 Disarm 인터락(단일 long 비트마스크·0ms 원자·경합 지연 불가).
            var flightRecorder = new MS2E.Infrastructure.AnomalyFlightRecorder();
            var multiDisarm = new MS2E.Infrastructure.MultiTelemetryDisarmInterlock(flightRecorder);
            services.AddSingleton(flightRecorder);
            services.AddSingleton(multiDisarm);
            // v11.4.5 Think-Beyond — Stage 4 전액 승격 이중 엔진: ①Step-Up Tier 거버너(락프리 CAS·중복승격/오버슈트 불가) ②즉시 롤백 퍼저(0x00 소거·잔여권한 차단).
            var rollbackPurger = new MS2E.Infrastructure.InstantRollbackPurger();
            var tierGovernor = new MS2E.Infrastructure.StepUpTierGovernor(rollbackPurger);
            services.AddSingleton(rollbackPurger);
            services.AddSingleton(tierGovernor);
            // Phase 94-G — 알림 피드 Port(DIP): AlertPage/AlertHistoryPage 이주 기반. AlertCenter 정적 싱글턴 격리(싱글턴 = 이벤트 구독 1회).
            services.AddSingleton<MS2E.UI.Portable.Ports.Alerts.IAlertFeedPort, MS2E.Infrastructure.AlertFeedAdapter>();
            // Phase 94-H — 복합 알림 규칙 CRUD Port(DIP): CompositeAlertsPage 이주 기반. CompositeAlertService 정적 싱글턴 + AiService 격리(가변 드래프트↔도메인 매핑·싱글턴 = Evaluated 구독 1회).
            services.AddSingleton<MS2E.UI.Portable.Ports.Alerts.ICompositeRuleCrudPort, MS2E.Infrastructure.CompositeRuleCrudAdapter>();
            // Phase 94-H — 설정/관리자 위젯 Port(DIP): SettingsPage 이주 기반. LocalDatabase 설정/시크릿·BrokerHub·ApiKeyVault·LanStatusServer·AiService 격리(API 키는 마스킹 노출).
            services.AddSingleton<MS2E.UI.Portable.Ports.IAdminSettingWidgetPort, MS2E.Infrastructure.AdminSettingWidgetAdapter>();
            // Phase 94-I — 계좌 요약 Port(DIP): TotalAssetPage 이주 기반. DashboardService 싱글턴 격리(Coins 공변 반환).
            services.AddSingleton<MS2E.UI.Portable.Ports.IAccountSummaryPort, MS2E.Infrastructure.AccountSummaryAdapter>();
            // Phase 94-I — 포트폴리오 허브 Port(DIP): RebalancePage 이주 기반. MarkowitzOptimizer + LocalDatabase(타겟 저장) 격리.
            services.AddSingleton<MS2E.UI.Portable.Ports.IPortfolioHubPort, MS2E.Infrastructure.PortfolioHubAdapter>();
            // Phase 94-J — PortfolioPage 6중 심층 결합 해체: 5대 명명 Port + 심볼검색 + stateful 프리젠터.
            services.AddSingleton<MS2E.UI.Portable.Ports.Portfolio.ITransactionCrudPort, MS2E.Infrastructure.TransactionCrudAdapter>();
            services.AddSingleton<MS2E.UI.Portable.Ports.Portfolio.IQuoteStreamPort, MS2E.Infrastructure.QuoteStreamAdapter>();
            services.AddSingleton<MS2E.UI.Portable.Ports.Portfolio.IFxRatePort, MS2E.Infrastructure.FxRateAdapter>();
            services.AddSingleton<MS2E.UI.Portable.Ports.Portfolio.ITimeTravelAsOfPort, MS2E.Infrastructure.TimeTravelAsOfAdapter>();
            services.AddSingleton<MS2E.UI.Portable.Ports.Portfolio.ISymbolSearchPort, MS2E.Infrastructure.SymbolSearchAdapter>();
            services.AddScoped<MS2E.UI.Portable.Ports.Portfolio.IJsDialogPort, MS2E.Infrastructure.JsDialogAdapter>();
            services.AddScoped<MS2E.UI.Portable.Ports.Portfolio.IPortfolioBoardPort, MS2E.Infrastructure.PortfolioBoardAdapter>();
            // Phase 94-K — Main Shell & Dashboard 위젯 Port(DIP): NavMenu·ToastHost·SymbolBar·CrossLinks·IndexTickerBar·MarketTicker 이주 기반.
            services.AddSingleton<MS2E.UI.Portable.Ports.Shell.ISymbolContextPort, MS2E.Infrastructure.SymbolContextAdapter>();
            services.AddSingleton<MS2E.UI.Portable.Ports.Shell.IToastPort, MS2E.Infrastructure.ToastAdapter>();
            services.AddSingleton<MS2E.UI.Portable.Ports.Shell.IMainShellNavigationPort, MS2E.Infrastructure.MainShellNavigationAdapter>();
            services.AddSingleton<MS2E.UI.Portable.Ports.Shell.IDashboardWidgetPort, MS2E.Infrastructure.DashboardWidgetAdapter>();
            // v12.6.0 — Main Dashboard AI Co-Pilot Feed: IAiRecommendationPort(v12.4.0, 기존엔 미등록이었음)를 최초로
            //   DI 결속 + 신규 IOrderPrefillPort(Advisory-Only 프리필 브리지) + IDashboardAiFeedPort(보유종목→SIMD
            //   Top-N→추천 합성, IPortfolioBoardPort(scoped) 의존이라 동일하게 scoped 등록).
            services.AddSingleton<MS2E.Core.AiModelFallbackCircuitBreaker>();
            // v12.7.0 — RollingWinRateCalibrationEngine(적중률 감쇠)·RegimeAwareDynamicFactorWeightingEngine(국면 적응
            //   가중치)를 싱글턴 등록 — AiRecommendationAdapter/DashboardAiFeedAdapter 의 옵션 생성자 인자로 DI 가
            //   자동 주입(미등록이면 null 기본값 그대로 v12.4~12.6 동작 보존).
            services.AddSingleton<MS2E.Core.RollingWinRateCalibrationEngine>(_ => new MS2E.Core.RollingWinRateCalibrationEngine(1024));
            services.AddSingleton<MS2E.Core.RegimeAwareDynamicFactorWeightingEngine>();
            services.AddSingleton<MS2E.UI.Portable.Ports.Intel.IAiRecommendationPort, MS2E.Infrastructure.AiRecommendationAdapter>();
            services.AddSingleton<MS2E.UI.Portable.Ports.Shell.IOrderPrefillPort, MS2E.Infrastructure.OrderPrefillAdapter>();
            services.AddSingleton<MS2E.Infrastructure.AiMicrostructureDivergenceGuard>();
            // v12.13.0 [Step 7/7] 감사 — 65개 Port 인터페이스 전수 DI 스캔 결과 IOrderBookPort(v12.3.0)만 유일하게
            //   미등록으로 확인(실측·유일 결함). 기본 감시 심볼(005930)로 최초 결속 — 아직 실 호가 피드가 이
            //   OrderBookDepthMatrixCoalescer 에 Publish 하지 않아 HasData=false 로 정직 렌더된다(가짜 데이터 없음).
            //   동시에 DashboardAiFeedAdapter 의 imbalanceLookup(v12.6.0 부터 항상 null 이었음)을 이 Port 에 연결해
            //   AI-Microstructure Divergence 가드(v12.6.0)가 처음으로 실제 Port 경로를 통해 신호를 받게 한다.
            services.AddSingleton<MS2E.Core.OrderBookDepthMatrixCoalescer>(_ => new MS2E.Core.OrderBookDepthMatrixCoalescer(depth: 10, capacityPow2: 64));
            services.AddSingleton<MS2E.UI.Portable.Ports.Realtime.IOrderBookPort>(sp =>
                new MS2E.Infrastructure.OrderBookAdapter("005930", sp.GetRequiredService<MS2E.Core.OrderBookDepthMatrixCoalescer>()));
            services.AddScoped<MS2E.UI.Portable.Ports.Intel.IDashboardAiFeedPort>(sp =>
                new MS2E.Infrastructure.DashboardAiFeedAdapter(
                    sp.GetRequiredService<MS2E.UI.Portable.Ports.Portfolio.IPortfolioBoardPort>(),
                    sp.GetRequiredService<MS2E.UI.Portable.Ports.Intel.IAiRecommendationPort>(),
                    sp.GetRequiredService<MS2E.UI.Portable.Ports.Shell.IOrderPrefillPort>(),
                    sp.GetRequiredService<MS2E.Infrastructure.AiMicrostructureDivergenceGuard>(),
                    imbalanceLookup: symbol =>
                    {
                        var ob = sp.GetRequiredService<MS2E.UI.Portable.Ports.Realtime.IOrderBookPort>().Snapshot();
                        return ob.HasData && ob.Symbol == symbol ? ob.ImbalanceRatio : (decimal?)null;
                    },
                    regimeWeighting: sp.GetRequiredService<MS2E.Core.RegimeAwareDynamicFactorWeightingEngine>()));
            // v12.11.0 [Step 5/7] 감사 — DynamicRiskInterlock(v12.2.0)은 엔진·테스트만 완성되고 DI 등록 자체가
            //   없었음(실측 확인) — 이번에 최초 결속. MultiTelemetryDisarmInterlock(위 multiDisarm, v11.4.4)은
            //   DI엔 있었으나 소비하는 UI 가 전무했음 — RiskInterlockStatusWidget 이 최초 소비자.
            //   ProxyLivenessProbe(v12.0.4, x86 프록시 하트비트용 엔진)를 하드웨어 펄스 생존성 신호로 재사용
            //   (Think Beyond B — 신규 엔진 미제작). ★정직 명기: 아직 실제 하드웨어 펄스 소스가 .Beat() 를 호출하지
            //   않아 Probe 는 '미시작=보수적 생존' 상태로 항상 정상 표시된다(가짜 배선 없음 — 실 배선은 후속 과제).
            services.AddSingleton<MS2E.Infrastructure.RiskOrderPurgeRecorder>();
            services.AddSingleton<MS2E.Infrastructure.DynamicRiskInterlock>();
            services.AddSingleton<MS2E.Core.StreamingParametricVarCalculator>(_ => new MS2E.Core.StreamingParametricVarCalculator(64));
            services.AddScoped<MS2E.UI.Portable.Ports.Portfolio.IDynamicRiskPort, MS2E.Infrastructure.DynamicRiskAdapter>();
            services.AddSingleton<MS2E.Core.ProxyLivenessProbe>(_ => new MS2E.Core.ProxyLivenessProbe(System.Diagnostics.Stopwatch.Frequency * 30));   // 30초 무신호=유실
            services.AddSingleton<MS2E.UI.Portable.Ports.Shell.IRiskInterlockStatusPort, MS2E.Infrastructure.RiskInterlockStatusAdapter>();
            // v12.12.0 [Step 6/7] 감사 — ZeroAllocForensicAuditStream(v12.2.0, DI 등록 자체가 없었음 — 실측 확인)을
            //   최초 등록. 등록 즉시 DynamicRiskAdapter(위, 옵션 인자)에도 자동 결선되어 리스크 위반 이벤트가
            //   드디어 실제 체인에 기록되기 시작한다. MmfFlightRecorder(blackbox, 위 92행에서 이미 실배선)를
            //   Virtualize 대용량 뷰어에 최초 결속(기존 TelemetryMonitor 는 최근 100건 고정 헥스덤프만 노출했음).
            // [MMF 하드닝] 인스턴스 즉시 생성 — 체인 변조 Disarm 인터락이 같은 스트림을 10초 주기로 검증(δ=0)하고, 결함 시 API Secret 0x00 +
            //   킬스위치 격발 + 무장 술어 영구 차단. 이전엔 검증 결과를 UI 뷰어만 소비해 체인이 조작돼도 무장이 가능했다.
            var forensicStream = new MS2E.Core.ZeroAllocForensicAuditStream(8192);
            // [E2E 스트레스] 무장 집계어 비트 전이(어느 인터락이 언제 Disarm/해제) → 포렌식 해시 체인 + MMF 블랙박스 즉시 각인.
            //   결속 시점 차단 비트(기동 미봉인 상태 등)는 1회 raised 로 기록.
            var interlockRecorder = new StockManager.Telemetry.InterlockForensicRecorder(forensicStream, blackbox);
            interlockMask.AttachTransitionSink(interlockRecorder);
            services.AddSingleton(interlockRecorder);
            services.AddSingleton(forensicStream);
            bootIntegrityRef?.AttachForensicStream(forensicStream);
            openOrderRecon.AttachForensics(forensicStream, blackbox);   // [미체결 대사 · 방향 A] 대사 패스 판정 해시 체인 + MMF 블랙박스
            execFeed.AttachForensicStream(forensicStream);   // [키움 프로토콜 · 방향 A] 대사 이력(거부·부분체결·취소·불일치) 해시 체인 각인   // [봉인 운영 · 방향 B] 봉인 변조 증거(기대/실측 해시) 해시 체인 각인
            try
            {
                var forensicChain = new MS2E.Infrastructure.ForensicChainDisarmInterlock(forensicStream, killPurger,
                    onTrip: _ => { try { killBridge.Signal(MS2E.Core.KillSwitchReason.Manual); } catch { } });
                forensicChain.StartMonitor(TimeSpan.FromSeconds(10));
                forensicChainRef = forensicChain;
                forensicChain.BindInterlock(interlockMask, MS2E.Core.ArmingInterlockSource.ForensicChain);
                services.AddSingleton(forensicChain);
                FormClosed += (_, _) => ShutdownTrace.Step("forensicChain", () => forensicChain.Dispose());
            }
            catch { /* 결속 실패 → forensicChainRef=null → blockWhile ?? true(무장 차단) */ }
            services.AddSingleton<MS2E.UI.Portable.Ports.Shell.IForensicAuditViewPort, MS2E.Infrastructure.ForensicAuditViewAdapter>();
            // Phase 94-L — CommandPalette & MainLayout Port(DIP): 명령 레지스트리(scoped·Nav) + 핫키 브릿지/JS Interop 안전 센티널.
            services.AddSingleton<MS2E.Infrastructure.DotNetObjectRefSentinel>();
            services.AddSingleton<MS2E.Infrastructure.JSInvokableTokenVerifier>();
            services.AddSingleton<MS2E.UI.Portable.Ports.Shell.IHotkeyBridgePort, MS2E.Infrastructure.HotkeyBridgeAdapter>();
            services.AddScoped<MS2E.UI.Portable.Ports.Shell.ICommandPalettePort, MS2E.Infrastructure.CommandPaletteAdapter>();
            // Phase 94-M — Dashboard 심층 Port(DIP): 활동로그·레이아웃 영속·모의주문(scoped·Nav).
            services.AddSingleton<MS2E.UI.Portable.Ports.Shell.IUserActivityPort, MS2E.Infrastructure.UserActivityAdapter>();
            services.AddSingleton<MS2E.UI.Portable.Ports.Shell.ILayoutPersistencePort, MS2E.Infrastructure.LayoutPersistenceAdapter>();
            services.AddScoped<MS2E.UI.Portable.Ports.Shell.IDashboardActionPort, MS2E.Infrastructure.DashboardActionAdapter>();
            // Phase 94-N — 트레이딩 허브 Port(DIP): GatingPage·OrderReconcilePanel 이주 기반. OrderAttestationService 격리(정합 관제).
            services.AddSingleton<MS2E.UI.Portable.Ports.Trading.ITradingHubPort, MS2E.Infrastructure.TradingHubAdapter>();
            // Phase 94-O — 코인 실시간 스트림 Port(DIP): CoinPage 이주 기반. 4거래소+환율+보유 병합 격리(모의 전용).
            services.AddSingleton<MS2E.UI.Portable.Ports.Realtime.ICryptoStreamPort, MS2E.Infrastructure.CryptoStreamAdapter>();
            // Phase 94-O — 실시간 시세/워치리스트 Port(DIP): RealtimePage 이주 기반. IQuoteProvider·WatchlistStore·ExtremeScanner 격리.
            services.AddSingleton<MS2E.UI.Portable.Ports.Realtime.IRealtimeBrokerPort, MS2E.Infrastructure.RealtimeBrokerAdapter>();
            services.AddSingleton<MS2E.UI.Portable.Ports.Realtime.IWatchlistPort, MS2E.Infrastructure.WatchlistAdapter>();
            // Phase 94-O — StrategyHub 잔여 자식 compute Port(DIP): Sizer/EventStudy/Miner 이주 기반.
            services.AddSingleton<MS2E.UI.Portable.Ports.Strategy.IPositionSizerPort, MS2E.Infrastructure.PositionSizerAdapter>();
            // ★EventStudy/Miner 는 회로별 상태(저장규칙 캐시·발굴 풀) 보유 → scoped 필수(교차 회로 격리).
            services.AddScoped<MS2E.UI.Portable.Ports.Strategy.IEventStudyPort, MS2E.Infrastructure.EventStudyAdapter>();
            services.AddScoped<MS2E.UI.Portable.Ports.Strategy.IHypothesisMinerPort, MS2E.Infrastructure.HypothesisMinerAdapter>();
            // Phase 94-O — IntelHub Port(DIP): AI/Advisor/Debate/Journal 이주 기반. ★컴플라이언스 게이트는 세션 sticky 락→싱글턴 필수.
            services.AddSingleton<MS2E.UI.Portable.Ports.Intel.IAiAssistantPort, MS2E.Infrastructure.AiAssistantAdapter>();
            services.AddSingleton<MS2E.UI.Portable.Ports.Intel.IComplianceGatePort, MS2E.Infrastructure.ComplianceGateAdapter>();
            services.AddSingleton<MS2E.UI.Portable.Ports.Intel.IJournalPort, MS2E.Infrastructure.JournalAdapter>();
            services.AddSingleton<MS2E.UI.Portable.Ports.Intel.IDebatePort, MS2E.Infrastructure.DebateAdapter>();
            // Phase 94-P — ValidateHub Port(DIP): Calibrator/Twin/Replay/Forecast 이주 기반. ★원시 SQL 은 IValidateRepositoryPort 단일 초크포인트(DataRow 무물성화)로 격리.
            services.AddSingleton<MS2E.UI.Portable.Ports.Validate.IValidateRepositoryPort, MS2E.Infrastructure.ValidateRepositoryAdapter>();
            services.AddSingleton<MS2E.UI.Portable.Ports.Validate.ICalibratorPort, MS2E.Infrastructure.CalibratorAdapter>();
            services.AddSingleton<MS2E.UI.Portable.Ports.Validate.ITwinPort, MS2E.Infrastructure.TwinAdapter>();
            services.AddSingleton<MS2E.UI.Portable.Ports.Validate.IReplayPort, MS2E.Infrastructure.ReplayAdapter>();
            services.AddSingleton<MS2E.UI.Portable.Ports.Validate.IForecastExecutionPort, MS2E.Infrastructure.ForecastExecutionAdapter>();
            // Phase 94-P — 잔여 도구 페이지 Port(DIP): TimeMachine 등 이주 기반 + 공용 폴더열기(Process.Start 격리).
            services.AddSingleton<MS2E.UI.Portable.Ports.Tools.IFolderOpenPort, MS2E.Infrastructure.FolderOpenAdapter>();
            services.AddSingleton<MS2E.UI.Portable.Ports.Tools.ITimeMachinePort, MS2E.Infrastructure.TimeMachineAdapter>();
            services.AddSingleton<MS2E.UI.Portable.Ports.Tools.ICopilotPort, MS2E.Infrastructure.CopilotAdapter>();
            services.AddSingleton<MS2E.UI.Portable.Ports.Tools.IResearchPort, MS2E.Infrastructure.ResearchAdapter>();
            // ★CartridgeAdapter 는 회로별 상태(드래프트·가져온 카트리지) 보유 → scoped 필수.
            services.AddScoped<MS2E.UI.Portable.Ports.Tools.ICartridgePort, MS2E.Infrastructure.CartridgeAdapter>();
            services.AddSingleton<IPortfolioSyncPort>(tossPortfolioAdapter);
            services.AddSingleton<IBrokerAccountPort>(tossPortfolioAdapter);
            services.AddSingleton(tossGateway);
            services.AddSingleton(tossSeqGuardian);
            // v10.2.0 Universal Broker Mesh — 다중 증권사 통합(★read-only·liveArmed:false — 어떤 증권사든 발주 유발 0).
            //   기존 Toss 어댑터 등록 + KIS 스키마 어댑터(스텁·실 배선 시 tokenProvider/accountFetch 구성). Toss→KIS 폴백 우선순위.
            var brokerMesh = new UniversalBrokerGatewayMesh();
            var kisAdapter = new OpenApiPortfolioAdapter(BrokerSchema.Kis(),
                tokenProvider: static () => "", accountFetch: static _ => null, readOnlyKey: true);
            brokerMesh.Register(BrokerId.Toss, tossPortfolioAdapter);
            brokerMesh.Register(BrokerId.Kis, kisAdapter);
            brokerMesh.Seal();
            var fallbackSentinel = new CrossBrokerFallbackSentinel(new[] { BrokerId.Toss, BrokerId.Kis });
            var timeFence = new CrossBrokerMonotonicTimeFence();
            services.AddSingleton(brokerMesh);
            services.AddSingleton(fallbackSentinel);
            services.AddSingleton(timeFence);
            services.AddSingleton(kisAdapter);
            // v11.0.0 Genesis Fleet Production — 실물 주문 집행 파이프라인. ★liveArmed:false: UniversalOrderExecutionAdapter 는
            //   모든 발주를 GenesisLiveIgnitionGateway.Emit(이중 물리 키 ValveWired∧LiveArmingWired 인터락) 경유 — 미결속[기본]
            //   이면 Emit 이 veto 하여 brokerSend(실 HTTP 발주) 델리게이트를 절대 호출 안 함(단 1비트 실물 누수 0). 실 배선 시
            //   brokerSend 에 증권사 HTTP 발주를 주입(게이트 하류에서만 실행).
            var execLedger    = new InFlightOrderReconciliationLedger();
            var execZeroGuard = new ExecutionTokenZeroizationGuard();
            var execBridge    = new LiveExecutionTelemetryBridge(execLedger);   // ★체결 통보 → 원장 정산 결선(브릭 방지)
            var execAdapter   = new UniversalOrderExecutionAdapter(BrokerId.Toss, ignGateway, execLedger, brokerSend: null);
            services.AddSingleton(execLedger);
            services.AddSingleton(execZeroGuard);
            services.AddSingleton(execBridge);
            services.AddSingleton(execAdapter);
            services.AddSingleton<IOrderExecutionPort>(execAdapter);
            var ringPublisher  = new RingTelemetryPublisher(dispatchRing, ringDispatcher, ringChannel, null, scenarioMon, alphaFactory, evolutionPipeline, ignition, execGuard, ledgerMesh, warmRestart, chaosGrid, prodSwap, newsIntel, riskShield, shadowRing, mmfFabric, blackbox, blackboxDumper, accountingMesh, shadowGateway, cutoverGate, accountGenesis, canaryProbe, blastRadius, killSwitch, deadman, liveWatchdog, failover, starvation, orphanReaper, liquiditySlicer, orderThrottle, toxicSentinel, reloadGate, stateCarrier, zombieShield, strategyMesh, basketNetter, washGuard, contagionFirewall, ms2Logger, kernelJitter, ioFirewall,
                weaveClock: weaveClock, weaveRing: weaveRing, hwProbe: hwProbe, auditVault: auditVault, varEngine: varEngine,
                cpuGrid: cpuGrid, thermalGuard: thermalGuard, coherence: coherence,
                preFlight: preFlight, armingLease: armingLease, deadMan: deadMan, fatFinger: fatFinger,
                alphaEngine: alphaEngine, noiseQuant: noiseQuant, avxTuner: avxTuner, alignedVec: alignedVec,
                vault: quantumVault, honeyMesh: honeyMesh, antiHook: antiHook, zeroizer: secureZeroizer,
                rcuConfigVersion: () => rcuConfigPort.Version, infraCouplingCount: 117L,
                sor: smartRouter, icebergHunter: icebergHunter, authVerify: liqAuthVerifier, queueEst: queuePosEst,
                chaos: chaosSanitizer, chaosRecovery: chaosRecovery, chaosGuard: chaosGuard, chaosReplayer: chaosReplayer,
                ignitionGateway: ignGateway, ignitionAudit: ignAudit, keyPulse: keyPulse, sealedEmission: sealedEmit,
                mockMirror: mockMirror, certSealer: certSealer, scopeBarrier: scopeBarrier,
                slaGate: slaGate, jitterGuard: jitterGuard, releaseAttestor: releaseAttestor,   // v10.1.0 Final — 나노 SLA·지터·릴리스 봉인 텔레메트리
                signoffValidator: signoffValidator, canaryGovernor: canaryGovernor, driftSentinel: driftSentinel, panicSwitch: panicSwitch,   // v10.1.0 Ops — 서명·카나리·드리프트·패닉 텔레메트리
                tossAdapter: tossPortfolioAdapter, tossGateway: tossGateway,   // v10.2.0 Toss — 조회 전용 포트폴리오/시세 텔레메트리
                brokerMesh: brokerMesh, fallbackSentinel: fallbackSentinel, timeFence: timeFence,   // v10.2.0 Universal — 다중 증권사 메쉬 텔레메트리
                execAdapter: execAdapter, execZeroGuard: execZeroGuard, execBridge: execBridge);   // v11.0.0 Genesis Fleet — 실물 발주 텔레메트리
            services.AddSingleton(armingGate);     // /command 무장 상태 표시용(진단)
            services.AddSingleton(scenarioMon);    // FutureSimPage 주입 대상(시나리오 seqlock 보고)
            services.AddSingleton(dispatchRing);
            services.AddSingleton(ringDispatcher);
            services.AddSingleton(ringChannel);    // TelemetryMonitor.razor 주입 대상
            services.AddSingleton(ringPublisher);
            // v4.84.0 — 초고속 IPC 레이턴시 + FX 3σ 브레이커 관제(Path Gamma seqlock). bridge(기본 미연결)·
            //   fxBreaker(기본 미급전) 카운터를 latencyPublisher 가 합류해 채널 발행 → LatencyMonitor.razor(/broker/latency).
            //   ★liveArmed:false 불변: 채널/브레이커 '생성'은 순수 관측 인프라 — 실 연결(ConnectAsync)·급전(QFxFeedPump)은
            //   여전히 옵트인이며, 미가동이면 전 카운터 0 → UI 가 '미배선' 정직 표기.
            var fxBreaker        = new QFxCircuitBreaker(() => DateTime.UtcNow.Ticks, attestation);
            var latencyChannel   = new LatencyTelemetryChannel();
            var latencyPublisher = new LatencyTelemetryPublisher(latencyChannel, bridge, fxBreaker);
            services.AddSingleton(fxBreaker);          // 텔레메트리 + 향후 QFxFeedPump 급전 대상(단일 인스턴스)
            services.AddSingleton(latencyChannel);     // LatencyMonitor.razor 주입 대상
            services.AddSingleton(latencyPublisher);
            // v7.4.0 — Pre-Flight 재인증 콜드 구동기: 서브시스템에서 입력 수집 → Certify → 인증∧무장 시 임대 갱신(BelowNormal·fail-safe).
            //   ★디스크(DriveInfo)는 이 콜드 스레드에서 ≈5s 캐시 샘플링 — 200ms 텔레메트리 샘플러 핫패스와 분리. liveArmed:false.
            // [v12.18.10] 브로커 생존성 사전 진단(비파괴 · 레인별 3초) — 키움 = 실 COM 프록시 링크 ∧ 하트비트 생존 판독(송신 0) ·
            //   KIS = 토큰 발급 + 잔고 조회 GET(TTTC8434R · 원장 변형 0). 무응답/무효 = 판정 즉시 비트 25(키움) · 14 sticky 래치.
            //   진단 Passed 전에는 임대 갱신 0(발주 엔진 가동 전 차단). 미결선 레인(연결 비활성 · 자격증명 없음) = 비차단.
            KisLiveBroker? kisLive = kisConfigured ? new KisLiveBroker(db) : null;   // 생성 자체는 네트워크 0
            var livenessPreFlight = new BrokerLivenessPreFlight(
                kiwoomConnect ? async ct =>
                {
                    while (true)
                    {
                        if (bridge.IsProductionProxy && !bridge.IsHeartbeatTimedOut(DateTime.UtcNow.Ticks)) return BrokerProbeVerdict.Healthy;
                        await Task.Delay(50, ct).ConfigureAwait(false);
                    }
                } : null,
                kisLive is null ? null : ct => Task.Run(() => kisLive.QueryBalanceJson() is { Length: > 0 } ? BrokerProbeVerdict.Healthy : BrokerProbeVerdict.Invalid, ct),
                accountDivergence, kiwoomLink,
                onLatch: (lane, v) => { try { Log.Warn($"[PreFlight] 브로커 생존성 사전 진단 실패 lane={lane} verdict={v} — 비트 14/25 래치 · 무장 임대 갱신 0"); } catch { } });
            services.AddSingleton(livenessPreFlight);
            var preFlightUpdater = new GenesisPreFlightUpdater(preFlight, armingLease,
                latency: latencyChannel, throttle: orderThrottle, cpuGrid: cpuGrid, thermal: thermalGuard,
                varEngine: varEngine, ioFirewall: ioFirewallRef,
                marginHeadroomRaw: () => riskShield.MarginBudgetRaw - riskShield.MarginReservedRaw,
                isArmed: () => { try { return armingGate.IsArmed(); } catch { return false; } },
                liveness: livenessPreFlight);
            preFlightUpdater.Start();
            _ = preFlightUpdater.ProbeBrokerLiveness();   // 콜드 비동기 — 실패는 레인 continuation 이 즉시 래치(호스트 기동 비차단)
            //   진단 통과 후 핑퐁 계측(1초 · δ=0) — 키움 = 브리지 Ping/Pong RTT 판독(송신 경로 무접촉). KIS 레인 RTT 원천 없음 = 미계측(정직 0).
            var livenessPingTimer = new System.Threading.Timer(_ =>
            {
                try { if (livenessPreFlight.Cleared && bridge.IsProxyAlive) livenessPreFlight.RecordPingPong(BrokerLivenessPreFlight.LaneKiwoom, bridge.CurrentIpcRttTicks); }
                catch { }
            }, null, 1_000, 1_000);
            FormClosed += (_, _) => ShutdownTrace.Step("livenessPing", () => livenessPingTimer.Dispose());
            services.AddSingleton(preFlightUpdater);
            FormClosed += (_, _) => ShutdownTrace.Step("preFlightUpdater", () => preFlightUpdater.Dispose());
            // v4.70.0 — 발주 밸브 결속: Drain 루프(50ms) → IBroker.Place() 배선.
            //   Dispatch.ValveWired=true 일 때만 타이머 가동(기본 false — 드라이런 박제).
            //   LiveArmingWired 와 독립 스위치 — 둘 다 ON 이어야 실 주문 흐름(이중 키 잠금).
            // v4.87.0 — fail-closed 강화: 드레인 루프 예외 or 이중키 무결성(런타임 ValveWired) 붕괴 → 강등이 아니라
            //   ForceFreeze 단일 소스로 영구 락업. dualKeyIntact 는 매 틱 ValveWired 설정이 유지되는지 검문.
            // [실배선 · 방향 A] 기동 샌니티 봉인 — 필수 인터락 소스 전부 결속 확인 후에만 샌니티 비트 해제. 누락(생성 예외로 결속 실패 등)
            //   이면 비트 영구 유지 = 무장 불가(부팅 원자 중단) · MissingMask 로 누락 소스 진단(DI 싱글턴 조회).
            // [E2E · 방향 B] 실 브로커 송출 초크포인트 백업 인터락 — 물리 이중키(ValveWired ∧ LiveArmingWired) 미충족인데 Place 가
            //   도달하면(상류 게이트 전부 오작동) 실 브로커 전달 0 + 증거 수집 + sticky Disarm + 비상 동결. 정상 경로에서는 도달 자체가 없다.
            var egressSentinel = new StockManager.Adapters.LiveArmedEgressSentinel(broker,
                () => cfg.GetBool("Dispatch.ValveWired", false) && cfg.GetBool("Dispatch.LiveArmingWired", false),
                blackbox, why => attestation.ForceFreeze(why), interlockBlocked: () => interlockMask.Blocked);   // [카오스 스톰] 송출 직전 집계어 veto
            egressSentinel.BindInterlock(interlockMask, MS2E.Core.ArmingInterlockSource.EgressBypass);
            services.AddSingleton(egressSentinel);
            // [실전 결선 · 다중 브로커] KIS 송출 브로커 — 자격증명이 있을 때만 생성(생성 자체는 네트워크 0 · Place 시 OAuth2 토큰 자체 관리 ·
            //   5분 선행 갱신). 키움과 같은 백업 인터락(물리 이중키 미충족 시 실 송출 0)으로 감싼다. 비트(EgressBypass)는 키움 센티널이 보유 —
            //   KIS 센티널은 같은 동결 콜백으로 Disarm.
            IBroker? kisEgress = null;
            if (kisLive is not null)
                kisEgress = new StockManager.Adapters.LiveArmedEgressSentinel(kisLive,
                    () => cfg.GetBool("Dispatch.ValveWired", false) && cfg.GetBool("Dispatch.LiveArmingWired", false),
                    blackbox, why => attestation.ForceFreeze(why), interlockBlocked: () => interlockMask.Blocked);
            var brokerMap = new IBroker?[] { egressSentinel, kisEgress };
            // [v12.17.6] 드레인 송출용 맵 = 같은 센티널을 출구 스탬프 프로브로 감싼 것(판정 · 전달 · 취소 위임 — 센티널 우회 0). 취소 경로(매트릭스)는 원 맵.
            var stampedBrokerMap = new IBroker?[]
            {
                new StockManager.Telemetry.EgressStageProbe(egressSentinel, orderPathProfiler),
                kisEgress is null ? null : new StockManager.Telemetry.EgressStageProbe(kisEgress, orderPathProfiler),
            };
            // [실전 결선 · 분할 라우팅] 자식 슬롯 추적 매트릭스(비트 23 필수) — 체결/취소/거부 통보(키움 브리지) 수렴 · 500ms 불균형 시 실 브로커 취소.
            var childMatrix = new StockManager.Engines.ChildSlotTrackingMatrix(256,
                imbalanceMicros: cfg.GetInt("Routing.ImbalanceMs", 500, 50, 60_000) * 1000L,
                cancel: (b, oid) => { if ((uint)b < (uint)brokerMap.Length && brokerMap[b] is { } cb) cb.Cancel(oid.ToString(System.Globalization.CultureInfo.InvariantCulture)); });
            childMatrix.BindInterlock(interlockMask, MS2E.Core.ArmingInterlockSource.ChildSlotRejection);
            services.AddSingleton(childMatrix);
            Action<FillPayload> onChildFill = childMatrix.OnFill;
            bridge.FillReceived += onChildFill;
            string? kisHtsId = db.GetSecret("KIS.HtsId");
            bool kisRealtimeOn = kisLive is not null && cfg.GetBool("Kis.RealtimeEnabled", false) && !string.IsNullOrWhiteSpace(kisHtsId);
            // [KIS 실배선] REST 주문가능현금/보유 → 한도 뷰(인덱스 1) — 실패 즉시 한도 0 · 연속 3회 = 비트 24 Disarm. 미설정 = 미결속(자식 0 · 비차단).
            //   [KIS VTS · 방향 B] 실시간 체결통보가 결선될 때만 포지션 원장 대사(기준선 + 순체결 ≠ 브로커 보유 연속 2회 = 비트 24 Disarm).
            var kisBalance = new StockManager.Adapters.KisBalanceQueryAdapter(
                kisLive is null ? null : q => q == StockManager.Adapters.KisQuery.Buyable ? kisLive.QueryBuyableJson() : kisLive.QueryBalanceJson(),
                kisConfigured,
                staleMicros: cfg.GetInt("Kis.BalanceStaleSeconds", 30, 5, 600) * 1_000_000L,
                reconcilePositions: kisRealtimeOn);
            kisBalance.BindInterlock(interlockMask, MS2E.Core.ArmingInterlockSource.KisLimitFeed);
            services.AddSingleton(kisBalance);
            int kisPollBusy = 0;
            var kisBalanceTimer = kisConfigured
                ? new System.Threading.Timer(_ =>
                  {
                      if (Interlocked.Exchange(ref kisPollBusy, 1) != 0) return;     // 동기 HTTP 중첩 금지
                      try { kisBalance.PollOnce(); } catch { } finally { Volatile.Write(ref kisPollBusy, 0); }
                  }, null, 2_000, cfg.GetInt("Kis.BalancePollSeconds", 10, 3, 600) * 1000)
                : null;
            // [KIS 실배선] 실시간 체결통보(H0STCNI0) WebSocket — 명시 설정(Kis.RealtimeEnabled) + HTS ID(체결통보 구독 키) 있을 때만 접속(수신 전용).
            //   체결 = 실행 피드(KIS 추적 슬롯 대사) + 자식 매트릭스(가중평균가 수렴). 단절/장중 3초 침묵 = 비트 19(KisSession) + 라우터 KIS 장애 보고.
            // [대시보드 · 추천 A] AI 모의투자 실시간 관제 허브 — 읽기 전용 포트(IPaperDashboardPort · 주문/무장 명령 경로 없음).
            //   생산자(브리지 체결/틱 · KIS 체결 · 인터락 전이 = 포렌식 기록기 2차 체인)는 wait-free 링 적재만, 조립은 전용 타이머 스레드
            //   50ms 3중 버퍼 — UI 동결/예외가 OrderDrainWorker · 브로커 경로에 닿을 공유 상태 0.
            var brokerViews = new StockManager.Adapters.IBrokerAccountView?[] { new StockManager.Adapters.AccountSyncBrokerView(accountSync, () => bridge.IsProductionProxy), kisBalance };
            var dashHub = new StockManager.Blazor.Services.DashboardSnapshotHub(interlockMask, childMatrix, brokerViews,
                orderPath: orderPathProfiler);   // [v12.17.6] 실발주 경로 지연 → /aidash p50/p99(미계측 해소)
            interlockRecorder.Chain(dashHub);
            bridge.FillReceived += f => dashHub.OnFill(f, 0);
            bridge.TickReceived += t => dashHub.OnTick(t.SymbolCode, (long)Math.Round(t.Price));
            services.AddSingleton(dashHub);
            services.AddSingleton<MS2E.UI.Portable.Ports.Trading.IPaperDashboardPort>(dashHub);
            // [v12.18.6-DEV · 펌프 지터] 호스트 실 지터 소스 = 대시보드 펌프 실행 소요(매매 핫패스 밖) → /aidash 지터 배지. 폐기 = 허브 봉인 뒤.
            var pumpJitter = new StockManager.Telemetry.MicrosecondJitterSentinel();
            dashHub.AttachPumpJitter(pumpJitter);
            // [v12.18.11-DEV] AI vs Human 아레나 — 듀얼 가상 원장(브로커 경로 0 · Fail-Closed). L2 = 브리지 10호가 · AI = 시그널 엔진 결정(허브 분기) ·
            // 정산 = 허브 50ms 펌프. 휴먼 퀵 티켓은 IArenaPort.SubmitHuman → 휴먼 큐(가상 체결 전용).
            var arena = new StockManager.Engines.Arena.ArenaMatchGovernor();
            bridge.OrderBookReceived += b => arena.OnQuote(in b);
            bridge.TickReceived += t => arena.OnTrade(t.SymbolCode, (long)Math.Round(t.Price), t.Volume);   // [v12.18.13-DEV] 누적 거래량 → 지정가 대기열 소진
            // [v12.18.13-DEV] 정규장 틱 녹화(.tape · MMF · ON/OFF = /arena) — 같은 브리지 디스패치 스레드에서 기록(OFF = Volatile 1회). 종료 시 봉인.
            var arenaTape = new StockManager.Engines.Arena.ArenaTapeRecorder(StockManager.Engines.Arena.ArenaTapeStore.DefaultDirectory);
            bridge.OrderBookReceived += b => arenaTape.OnBook(in b);
            bridge.TickReceived += t => arenaTape.OnTick(t.SymbolCode, (long)Math.Round(t.Price), t.Volume);
            services.AddSingleton(arenaTape);
            services.AddSingleton<MS2E.UI.Portable.Ports.Trading.IArenaTapePort>(arenaTape);
            FormClosed += (_, _) => ShutdownTrace.Step("arenaTape", arenaTape.Dispose);
            dashHub.AttachArena(arena);
            // [v12.18.19-DEV] 멀티 브로커 합성 스프레드 레이더 — 키움 L2(브리지) ↔ KIS H0STASP0 같은 종목 10호가 대조(δ=0 · 다중 작성자 seqlock).
            //   합성 스프레드 ≥ 30bp 500ms 지속 = 래치 → 비트 22 2차 입력(대사기 AttachQuoteGuard · 비트 재결속 0 · 정의 불변). 모의 차익 = 레이더 가상 기록 전용.
            //   브로커 · 무장 경로 참조 0(실주문 송출 0). KIS 미구성 = 대조 대기(정직 렌더 · 가짜 호가 0). /arbitrage = 값 판독 전용.
            var arbRadar = new StockManager.Engines.Arbitrage.SyntheticSpreadRadar();
            arbRadar.BindDivergenceInterlock(openOrderRecon);
            bridge.OrderBookReceived += b => arbRadar.OnBook(MS2E.UI.Portable.Ports.Trading.ArbRadar.VenueKiwoom, in b);
            dashHub.AttachArbitrage(arbRadar);
            services.AddSingleton(arbRadar);
            services.AddSingleton<MS2E.UI.Portable.Ports.Trading.IArbitrageRadarPort>(arbRadar);
            // [v12.18.16-DEV] KIS 시세 전문 시각 편차 감시 — 비트 28(OrderPathLatency) 2차 입력(비트 재결속 0 · 정의 불변) · 지연 하한 > 50ms/역전/시계 편차 = 래치.
            //   KIS 미구성 = 관측 0(미수신 뱃지 · 차단 0). /aidash · /arena 뱃지 = 값 판독 전용.
            var kisSkew = new StockManager.Adapters.Kis.KisTimeSkewGuard();
            kisSkew.BindLatencyInterlock(orderPathLatency);
            dashHub.AttachKisFeed(kisSkew);
            services.AddSingleton(kisSkew);
            services.AddSingleton<MS2E.UI.Portable.Ports.Trading.IKisFeedLatencyPort>(kisSkew);
            services.AddSingleton(arena);
            services.AddSingleton<MS2E.UI.Portable.Ports.Trading.IArenaPort>(arena);
            // [v12.18.12-DEV] 아레나 고속 틱 리플레이 — 별도 원장 · 합성 1M 틱(첫 재생 시 생성 · 약 13MB) · 재생 스레드는 첫 명령 시 기동.
            // AI = 정량 시그널 엔진 전용 인스턴스(무장 미결속) · 게시 = 허브 50ms 펌프 요청. 브로커 경로 0.
            var arenaReplay = new StockManager.Engines.Arena.ArenaTickReplayEngine(() => StockManager.Engines.Arena.SyntheticTickSource.Create(1_000_000));
            dashHub.AttachArenaReplay(arenaReplay);
            services.AddSingleton(arenaReplay);
            services.AddSingleton<MS2E.UI.Portable.Ports.Trading.IArenaReplayPort>(arenaReplay);
            FormClosed += (_, _) => ShutdownTrace.Step("arenaReplay", arenaReplay.Dispose);
            dashHub.Start(cfg.GetInt("Dashboard.PeriodMs", 50, 20, 1000));
            FormClosed += (_, _) => ShutdownTrace.Step("dashHub", () => { dashHub.Dispose(); pumpJitter.Dispose(); });
            // [교차 피드] KIS 10호가(H0STASP0) 구독 종목 — `Kis.BookSymbols`(쉼표 · 비어 있음 = 교차 확인 꺼짐). 정량 엔진은 뒤에서 생성되므로 지연 참조.
            string[] kisBookSymbols = kisRealtimeOn ? cfg.GetString("Kis.BookSymbols", "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) : Array.Empty<string>();
            StockManager.Engines.QuantSignalProvider? quantRef = null;
            StockManager.Adapters.KisRealtimeSession? kisRealtime = null;
            System.Threading.CancellationTokenSource? kisRealtimeCts = null;
            StockManager.Adapters.KisSessionInterlock? kisSession = null;
            StockManager.Adapters.BrokerLimitFeedProvider? limitRef = null;               // [세션 복구] 한도 피드는 뒤에서 생성 — 지연 참조
            StockManager.Adapters.SessionRecoveryManager? kisRecovery = null;
            // [KIS VTS] 프로토콜 이탈 인터락(비트 26 필수 · 항상 결속 — 미설정 배포는 이탈이 없으므로 비차단). 실/모의 도메인 핀:
            //   교차 도메인 실시간 TR · 구독 응답 · REST 응답 tr_id 헤더 이탈 = sticky Disarm + 최초 1회 KIS 슬롯 격리
            //   (자식 매트릭스: KIS 미송출 회수 + 송출분 0ms 취소 · 세션 단절 보고 = 라우터 KIS 장애). 보안 로그 = 종류 + 상수 라벨만.
            var kisProtocol = new StockManager.Adapters.KisVtsProtocolValidator(vts: kisLive is null || !kisLive.IsRealDomain,
                securityLog: (kind, label) => System.Diagnostics.Trace.WriteLine("[KisProtocol] 프로토콜 이탈 " + kind + " (" + label + ") — KIS 슬롯 격리 + Disarm"),
                onIsolate: () => { childMatrix.IsolateBroker(1); Volatile.Read(ref kisSession)?.MarkDisconnected(); });
            kisProtocol.BindInterlock(interlockMask, MS2E.Core.ArmingInterlockSource.KisProtocolDivergence);
            services.AddSingleton(kisProtocol);
            if (kisLive is not null) kisLive.ResponseDivergence += _ => kisProtocol.Trip(StockManager.Adapters.KisDivergenceKind.RestTrHeader);
            if (kisRealtimeOn)
            {
                kisSession = new StockManager.Adapters.KisSessionInterlock(routerSentinel, 1,
                    marketHours: () => StockManager.Adapters.KisSessionInterlock.IsKrxRegularSession(DateTime.UtcNow),
                    marketSilenceMicros: cfg.GetInt("Kis.MarketSilenceMs", 3000, 500, 60_000) * 1000L);
                kisSession.BindInterlock(interlockMask, MS2E.Core.ArmingInterlockSource.KisSession);
                // [세션 자동 복구 · 추천 A] 단절/침묵(장중 3초) = KIS 한도 0 + 블랙리스트 · 죽은 소켓 강제 종료 · 지수 백오프(1·2·4·8·16s) 재접속 ·
                //   재구독 후 미체결 대사 확정 전까지 격리 유지 + 비트 19 재무장 거부 · 재접속 연속 5회 실패 = 소진 래치(KIS 슬롯 격리 · 비트 23).
                //   재접속 후 해제 = KIS 미체결 조회(TTTC8001R · KisOpenOrderQuery) 새 패스 확정 통지(OnReconciliationCompleted) · 불일치/유령 = 영구 래치.
                var kisSessionRef = kisSession;
                kisRecovery = new StockManager.Adapters.SessionRecoveryManager(1,
                    isolate: on => { Volatile.Read(ref limitRef)?.SetSessionIsolated(1, on); if (on) routerBoard.Blacklist(1); },
                    requestReconcile: () => openOrderRecon.RequestReconcile(MS2E.Infrastructure.OpenOrderVenueKind.Kis),
                    reconciled: () => openOrderRecon.IsConfirmed(MS2E.Infrastructure.OpenOrderVenueKind.Kis),
                    silent: kisSessionRef.SilentNow,
                    onExhausted: () =>
                    {
                        try { childMatrix.IsolateBroker(1); } catch { }
                        try { Log.Warn("[SessionRecovery] KIS 재접속 연속 실패 소진 — KIS 슬롯 영구 격리 · Disarm(이중키 재무장 필요)"); } catch { }
                    },
                    maxFailures: cfg.GetInt("Kis.ReconnectMaxFailures", StockManager.Adapters.SessionRecoveryManager.DefaultMaxFailures, 1, 20));
                var kisRecoveryRef = kisRecovery;
                kisSession.SetRearmHold(() => kisRecoveryRef.Held);
                services.AddSingleton(kisRecovery);
                StockManager.Adapters.KisRealtimeSession? rtRef = null;
                var kisWsAdapter = new StockManager.Adapters.KisOpenApiAdapter(kisSession,
                    send: f => Volatile.Read(ref rtRef)?.TrySendFrame(f) ?? false,
                    onFill: f => { execFeed.OnFill(f); childMatrix.OnFill(f); kisBalance.OnKisFill(f); dashHub.OnFill(f, 1); },   // [방향 B] 포지션 원장
                    validator: kisProtocol, accountCano: kisLive!.AccountCano,                               // [KIS VTS] 도메인 · 계좌 핀
                    onBook: b =>
                    {
                        Volatile.Read(ref quantRef)?.OnBook(in b, StockManager.Engines.QuantSignalProvider.FeedKis);   // [교차 피드]
                        arena.OnQuote(in b);                                                                  // [v12.18.16-DEV] 아레나 듀얼 북(MPSC 링)
                        arenaTape.OnBook(in b);                                                               //   정규장 녹화(.tape 176B · 슬롯 claim)
                        arbRadar.OnBook(MS2E.UI.Portable.Ports.Trading.ArbRadar.VenueKis, in b);              // [v12.18.19-DEV] 합성 스프레드 대조
                    },
                    blackbox: blackbox,                                                                       // [KIS 호가] 이탈 틱 포렌식
                    skewGuard: kisSkew,                                                                       // [v12.18.16-DEV] 전문 시각 편차 → 비트 28
                    onTrade: t =>
                    {
                        arena.OnTradeQty(t.SymbolCode, t.Price, t.TradeVolume, t.Side);                        // 건별 체결량 · 주도 방향(누적 교차 0)
                        arenaTape.OnTrade(t.SymbolCode, t.Price, t.TradeVolume, t.Side);
                    },
                    onBookQuarantine: q => Volatile.Read(ref quantRef)?.SetFeedQuarantined(StockManager.Engines.QuantSignalProvider.FeedKis, q));
                kisRealtime = new StockManager.Adapters.KisRealtimeSession(kisWsAdapter, kisSession, () => new StockManager.Adapters.ClientWebSocketTransport(),
                    kisLive!.RequestApprovalKey, kisHtsId!, vts: !kisLive.IsRealDomain, heartbeatSymbol: cfg.GetString("Kis.HeartbeatSymbol", "005930"),
                    bookSymbols: kisBookSymbols, recovery: kisRecovery);
                kisRecovery.BindAbort(kisRealtime.AbortConnection);
                Volatile.Write(ref rtRef, kisRealtime);
                kisRealtimeCts = new System.Threading.CancellationTokenSource();
                _ = Task.Run(() => kisRealtime.RunAsync(kisRealtimeCts.Token));
                services.AddSingleton(kisRealtime);
            }
            // 실시간 한도 피드 — 0 = 키움 계좌 동기화(OPW00001 주문가능금액 · OPW00018 보유 · 30초 만료) · 1 = KIS(계좌 조회 미결선 → 미결속)
            var limitFeed = new StockManager.Adapters.BrokerLimitFeedProvider(
                brokerViews,
                new long[] { cfg.GetInt("Routing.KiwoomMaxSharesPerOrder", 0, 0, 10_000_000), cfg.GetInt("Routing.KisMaxSharesPerOrder", 0, 0, 10_000_000) });
            services.AddSingleton(limitFeed);
            Volatile.Write(ref limitRef, limitFeed);
            // [세션 자동 복구] 키움: 파이프 링크(하트비트) 감시 · 격리만 — 자동 재기동 없음(브리지 1회용 · 단절 = 전역 동결 · 재기동은 운용자 몫).
            //   운용자가 프록시를 다시 붙이면 링크 복귀 → 미체결 대사 즉시 요청(OPT10075) → 확정 전까지 키움 한도 0 + 비트 22 차단.
            StockManager.Adapters.SessionRecoveryManager? kiwoomRecovery = kiwoomConnect
                ? new StockManager.Adapters.SessionRecoveryManager(0,
                    isolate: on => { limitFeed.SetSessionIsolated(0, on); if (on) routerBoard.Blacklist(0); },
                    requestReconcile: () => openOrderRecon.RequestReconcile(MS2E.Infrastructure.OpenOrderVenueKind.Kiwoom),
                    reconciled: () => openOrderRecon.IsConfirmed(MS2E.Infrastructure.OpenOrderVenueKind.Kiwoom),
                    linkProbe: () => bridge.IsProductionProxy, autoReconnect: false)
                : null;
            // [Long-Soak · 방향 A] 세션 복구 상태 전이(체류 Stopwatch 틱) · 격리 작동 표류(2 펌프 지속) → MMF 블랙박스(100/101). 관리자 펌프 '뒤'에 펌프.
            var recoveryMonitor = new StockManager.Adapters.RecoveryStateDriftMonitor(blackbox, limitFeed, null, kiwoomRecovery, kisRecovery);
            // [v12.18.7-DEV] /aidash 브로커 세션 배지(관리자 Snapshot 락 1회 · 판독 전용 — 격리/재접속 경로 무간섭)
            if (kiwoomRecovery is not null) dashHub.AttachSessionRecovery(kiwoomRecovery);
            if (kisRecovery is not null) dashHub.AttachSessionRecovery(kisRecovery);
            dashHub.AttachPreFlight(livenessPreFlight);   // [v12.18.10-DEV] 사전 진단 결과 → 브로커 세션 패널(판독 전용)
            services.AddSingleton(recoveryMonitor);
            // [KIS 미체결 대사 · 추천 A] TTTC8001R 실배선 — 전용 1초 타이머(HTTP 블로킹)가 당일 미체결 페이지를 대사기 KIS venue 로 공급.
            //   KIS 한도 0 = 최초 확정 전 · 미확정(재접속 RequestReconcile) · 유령 · 불일치. 확정 = 복구 관리자 즉시 해제 통지.
            //   불일치/유령 = 비트 22(대사기 sticky) + 영구 래치 + 블랙리스트 + 자식 매트릭스 KIS 격리(비트 23) · [방향 B] 유령 = 패스당 ≤4건 비상 취소(TTTC0803U).
            StockManager.Adapters.KisOpenOrderQuery? kisOpenOrders = null;
            System.Threading.Timer? kisOpenOrderTimer = null;
            if (kisLive is not null)
            {
                var kisLiveRef = kisLive;
                openOrderRecon.AttachKisCanceller((odno, orgNo) => kisLiveRef.CancelOpenOrder(odno, orgNo));
                kisOpenOrders = new StockManager.Adapters.KisOpenOrderQuery(openOrderRecon,
                    (string fk, string nk, bool cont, out bool more) => kisLiveRef.QueryOpenOrdersJson(fk, nk, cont, out more),
                    limits: limitFeed, recovery: kisRecovery,
                    onDiscrepancy: () =>
                    {
                        routerBoard.Blacklist(1);
                        try { childMatrix.IsolateBroker(1); } catch { }
                        try { Log.Warn("[OpenOrder] KIS 미체결 원장 불일치/유령 — KIS 슬롯 영구 격리 · Disarm(이중키 재무장 필요)"); } catch { }
                    },
                    interval: TimeSpan.FromSeconds(cfg.GetInt("Kis.OpenOrderIntervalSeconds", 30, 5, 600)));
                var kisOpenOrdersRef = kisOpenOrders;
                Volatile.Write(ref kisPassHook, kisOpenOrdersRef.OnPass);
                kisOpenOrderTimer = new System.Threading.Timer(_ => { try { kisOpenOrdersRef.Pump(); } catch { } }, null, 1500, 1000);
                services.AddSingleton(kisOpenOrders);
                FormClosed += (_, _) => ShutdownTrace.Step("kisOpenOrders", () => kisOpenOrderTimer.Dispose());
            }
            else limitFeed.SetReconcileIsolated(1, true);                              // KIS 미설정 = 대사 수단 없음 = KIS 한도 0
            // ★분할은 명시 설정으로만(기본 꺼짐 — 활성 시 모든 실전 주문이 1~3호가 × 브로커 한도로 나뉜다). 분할 가능 브로커 0 = 전방 거부 + 비트 23.
            bool splitEnabled = cfg.GetBool("Routing.SplitEnabled", false);
            if (splitEnabled) routerGate.EnableSplit(limitFeed, childMatrix.TripExternal);
            // [AI 전략 · 추천 A] 정량 시그널 엔진 — 키움 10호가 · 체결 → 불균형 · 마이크로 추세 · VWAP 이격 · 변동성 → /aidash 판단 근거(상태 변화 시) ·
            //   [방향 B] 산출 이상/연산 > 10ms = 비트 27 Disarm(필수 · 항상 결속). 시그널 게이트(확신 ≥ 0.85 · 같은 방향 · 신선)는 명시 설정으로만
            //   라우터 전방에 결속(기본 꺼짐 — 활성 시 신호 없는 종목 주문은 전부 전방 거부).
            var strategyAnomaly = new StockManager.Engines.StrategyAnomalyInterlock();
            strategyAnomaly.BindInterlock(interlockMask, MS2E.Core.ArmingInterlockSource.StrategyAnomaly);
            services.AddSingleton(strategyAnomaly);
            // [v12.18.2-DEV] 강화 전용 설정 감사 원장 — %LOCALAPPDATA%/StockManager/audit/tighten-audit.mmf(MMF 해시 체인). 기존 파일 = 기동 시 전체 검증 ·
            //   불일치/가득 참/열기 실패 = 동결(설정 변경 전부 AuditFrozen) + 비트 27. 1초 타이머(아래 signalGateTimer)가 전체 재검증.
            var tightenAudit = StockManager.Security.MonotonicSettingAuditLog.Open(System.IO.Path.Combine(
                System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "StockManager", "audit", "tighten-audit.mmf"), strategyAnomaly);
            services.AddSingleton<MS2E.Infrastructure.ITightenAuditSink>(tightenAudit);
            services.AddSingleton(tightenAudit);
            FormClosed += (_, _) => tightenAudit.Dispose();
            dashHub.AttachAuditLedger(tightenAudit);                                    // [v12.18.3-DEV] /aidash 원장 배지(스냅샷 · 체인 잠금 미사용)
            //   [교차 피드] KIS 호가 구독이 있으면 교차 확인 모드 — 두 피드 일치 · 꼬임 Disarm · 단일 출처 ½ 감쇄(`Strategy.CrossMaxSkewMs` 기본 500).
            //   [v12.18.1-DEV] 변동성 적응 임계 — 국면(추세/회귀/횡보/워밍업) → 요구 확신 기본 ~ `Strategy.AdaptiveMaxBp`(9500) · 정수 · δ=0 ·
            //   `Strategy.AdaptiveThreshold`(기본 켜짐 — 끄면 고정 임계). [방향 B] 1분 승인 > `Strategy.ChurnSoftPerMin`(30) = 억제 ·
            //   > `Strategy.ChurnHardPerMin`(60) = 알파 모델 붕괴 → 비트 27 Disarm. 국면/확신 거부 · 승인 억제는 시그널 게이트 강제 모드에서만 발주를 거른다
            //   (모델 붕괴 · 수량 ≤ 0 비트 27 은 관측 모드에서도 — 아래 [v12.18.2] 참고).
            int confidenceBaseBp = cfg.GetInt("Strategy.ConfidenceThresholdBp", 8_500, 5_000, 10_000);
            StockManager.Engines.VolatilityAdaptiveCalibrator? adaptiveCalibrator = cfg.GetBool("Strategy.AdaptiveThreshold", true)
                ? new StockManager.Engines.VolatilityAdaptiveCalibrator(confidenceBaseBp, Math.Max(confidenceBaseBp, cfg.GetInt("Strategy.AdaptiveMaxBp", 9_500, 5_000, 10_000)))
                : null;
            int churnSoft = cfg.GetInt("Strategy.ChurnSoftPerMin", 30, 1, 100_000);
            var signalChurn = new StockManager.Engines.SignalChurnCircuitBreaker(strategyAnomaly, churnSoft,
                Math.Max(churnSoft + 1, cfg.GetInt("Strategy.ChurnHardPerMin", 60, 2, 100_000)));
            var quantSignals = new StockManager.Engines.QuantSignalProvider(strategyAnomaly, dashHub,
                confidenceThreshold: confidenceBaseBp / 10_000.0,
                crossConfirm: kisBookSymbols.Length > 0,
                crossMaxSkewMicros: cfg.GetInt("Strategy.CrossMaxSkewMs", 500, 50, 10_000) * 1000L,
                crossHardSkewMicros: cfg.GetInt("Strategy.CrossHardSkewMs", 1_000, 50, 60_000) * 1000L,   // [방향 B] 시각 격차 > 1초 = 교차 확신 0
                calibrator: adaptiveCalibrator, churn: signalChurn);
            if (adaptiveCalibrator is not null) dashHub.AttachCalibration(adaptiveCalibrator, signalChurn);
            Volatile.Write(ref quantRef, quantSignals);
            bridge.OrderBookReceived += b => quantSignals.OnBook(in b);
            bridge.TickReceived += t => quantSignals.OnTrade(t.SymbolCode, (long)Math.Round(t.Price), t.Volume);
            services.AddSingleton(quantSignals);
            // [v12.18.2-DEV] 시그널 게이트 실배선 — ① 드레인 제안 관측(무장 판정 이전 · 항상): 게이트 꺼짐/미무장에서도 수량 ≤ 0 · 승인 폭주 = 비트 27 ·
            //   그림자 판정 계수. ② 강제 = `Strategy.SignalGateEnabled`(기본 false) 런타임 토글 — 1초 콜드 판독 · 켜기 즉시 · 끄기는 물리 키 둘 다 꺼졌을 때만.
            //   Strategy.* 는 순수 UI 기입 허용 목록 밖(호스트 안전 네임스페이스 — 운영자 경로로만 토글).
            ringDispatcher.ProposalObserver = quantSignals;
            var signalGateSwitch = new StockManager.Engines.SignalGateRuntimeSwitch(routerGate, quantSignals,
                desired: () => cfg.GetBool("Strategy.SignalGateEnabled", false),
                anyKeyOn: () => cfg.GetBool("Dispatch.ValveWired", false) || cfg.GetBool("Dispatch.LiveArmingWired", false),
                confidenceFloorBp: () => cfg.GetInt("Strategy.ConfidenceThresholdBp", 8_500, 5_000, 10_000),   // [v12.18.3] 강화 전용 UI 기입 실시간 반영(상향만)
                adaptiveMaxBp: () => cfg.GetInt("Strategy.AdaptiveMaxBp", 9_500, 5_000, 10_000));
            signalGateSwitch.Poll();
            services.AddSingleton(signalGateSwitch);
            dashHub.AttachSignalGate(signalGateSwitch);
            var signalGateTimer = new System.Threading.Timer(_ =>
            {
                try { signalGateSwitch.Poll(); } catch { }
                try { tightenAudit.PollIntegrity(); } catch { }                         // [v12.18.2-DEV] 감사 원장 전체 재검증(변조 = 동결 + 비트 27 · ≤ 1초)
            }, null, 1_000, 1_000);
            FormClosed += (_, _) => signalGateTimer.Dispose();
            var childEvalTimer = new System.Threading.Timer(_ =>
            {
                try { childMatrix.Evaluate(); } catch { }
                try { childMatrix.ReleaseConverged(); } catch { }
                try { kisSession?.Evaluate(); } catch { }                               // [KIS 방향 B] 장중 3초 침묵 판정
                try { kisRecovery?.Pump(); } catch { }                                  // [세션 복구 · 방향 A] 침묵 강제 종료 · 백오프 허가 · 대사 → 격리 해제
                try { kiwoomRecovery?.Pump(); } catch { }                               // [세션 복구] 키움 링크 감시 · 복귀 시 대사
                try { kisOpenOrders?.RefreshGate(); } catch { }                         // [KIS 미체결 대사] 재접속 확정 철회 → KIS 한도 0(δ=0)
                try { recoveryMonitor.Pump(); } catch { }                              // [Long-Soak · 방향 A] 복구 전이 · 작동 표류 블랙박스
                try { kiwoomLink.Evaluate(); } catch { }                                // [키움 실배선] 링크(하트비트 단절 포함) 재평가
                try { orderPathLatency.Evaluate(); } catch { }                          // [v12.17.6 · 방향 B] 실발주 경로 p99 예산 판정(콜드 · δ=0)
            }, null, 50, 50);
            FormClosed += (_, _) => ShutdownTrace.Step("childMatrix", () =>
            {
                childEvalTimer.Dispose(); kisBalanceTimer?.Dispose(); bridge.FillReceived -= onChildFill;
                try { kisRealtimeCts?.Cancel(); } catch { }
                kisRealtimeCts?.Dispose(); kisRealtime?.Dispose();
            });
            var drainWorker = new OrderDrainWorker(ringDispatcher, egressSentinel,   // [E2E · 방향 B] 실 브로커 앞 백업 인터락 경유
                wired: cfg.GetBool("Dispatch.ValveWired", false) || cfg.GetBool("Dispatch.DryRunActive", false),   // v10.1.0 — 드라이런도 드레인 가동(미러 급전)
                periodMs: orderDrainPeriodMs,   // [v12.17.6] 방향 B 예산과 같은 상수(설계된 링 대기)
                attestation: attestation,
                dualKeyIntact: () => cfg.GetBool("Dispatch.ValveWired", false) || cfg.GetBool("Dispatch.DryRunActive", false),   // v10.1.0 — 드라이런은 무결성 통과(오동결 방지)
                pinGrid: cpuGrid,   // v7.3.0 — 밸브 wired 시 드레인을 P-Core 전용 핀 스레드로(미wired면 미가동)
                gateway: ignGateway,   // v10.0.0 — 실물 사출을 최종 빗장(이중키∧방어선∧펄스∧봉인) 경유(미wired면 미가동)
                mockMirror: mockMirror, scopeBarrier: scopeBarrier,   // v10.1.0 — 드라이런 스코프면 실 브로커 대신 가상 미러로 봉인 루프백
                slaGate: slaGate, jitterGuard: jitterGuard,   // v10.1.0 Final — 드레인 사이클 지연 나노 SLA 계측(지터 필터 후 계상)
                canaryGov: canaryGovernor,   // v10.1.0 Ops — 실전 사출 직전 카나리 캡(자본 ≤0.1%) 검문
                interlocks: interlockMask,   // [실배선] 판정↔송출 사이 인터락 Trip → 송출 직전 veto(GatedOut)
                routeGate: new StockManager.Telemetry.StageStampingSplitGate(routerGate, orderPathProfiler),   // [피드 연동] 참조가·라우터 판정·추적 확보 → 송출 결과 슬롯 결속/롤백 · [v12.17.6] 승인 스탬프/거부 회수
                latencyProbe: t2tProfiler,   // [저지연] 주문별 Tick-to-Trade 계측 + 송출 직전 정지 veto
                brokersByIndex: stampedBrokerMap);  // [실전 결선 · 다중 브로커] 라우터 판정 브로커로 송출(미결선 인덱스 = 대체 송출 금지) · [v12.17.6] 출구 스탬프
            if (splitEnabled) drainWorker.EnableChildSplit(childMatrix);   // [분할 라우팅] 게이트웨이 경유 자식 봉인 사출 · 거부 시 형제 회수 + Disarm
            // ★[KIS VTS · 실결함 교정] 봉인은 '모든 필수 소스 결속 뒤' 1회. 수정 전엔 자식 매트릭스(비트 23) · KIS 한도(비트 24) 결속보다 먼저
            //   Seal 해 필수 미결속으로 실패 → 기동 샌니티 비트(63)가 영구히 서서 무장 자체가 불가능했다(fail-closed 이나 운영 결선 결함).
            // [카오스 스톰 · 방향 B] 연쇄 격발 동결 게이트 — 서로 다른 인터락 N개(기본 3)가 창(기본 50ms) 안 격발 = 시스템 붕괴 징후 →
            //   블랙박스 StormFreeze 기록 + 봉인(최초 원인 증거 보존) + ForceFreeze. 기동 각인 오격발 방지로 봉인 성공 뒤에만 Arm.
            var stormGate = new StockManager.Telemetry.CascadingStormFreezeGate(blackbox, why => attestation.ForceFreeze(why),
                cfg.GetInt("Chaos.StormThresholdBits", 3, 2, 64), TimeSpan.FromMilliseconds(cfg.GetInt("Chaos.StormWindowMs", 50, 1, 10_000)));
            interlockRecorder.Chain(stormGate);
            services.AddSingleton(stormGate);
            if (!interlockMask.Seal(out long missingInterlocks))
                System.Diagnostics.Trace.WriteLine($"[InterlockSanity] 필수 인터락 미결속 mask=0x{missingInterlocks:X} — 무장 영구 차단");
            else stormGate.Arm();
            services.AddSingleton(drainWorker);
            FormClosed += (_, _) => ShutdownTrace.Step("drainWorker", () => drainWorker.Dispose());
            // 200ms 주기 샘플러 타이머 — UI 폴링(150ms)보다 약간 빠름. 단일 라이터(ringPublisher·latencyPublisher) 보장.
            //   ★v5.3.0 적대검증(high) 교정 — 좀비 실드 스윕 펌프 합류: SweepOnce 는 프로덕션에 호출부가 없어 수거 어테스테이션·
            //   슬롯 회수가 사문화됐었다. 200ms 저가 스윕(driveGc:false)으로 수거 슬롯 회수+텔레메트리 갱신+자연수거 좀비 격발.
            //   (활성 등록 0이면 SweepOnce 는 즉시 반환 — 유휴 무부하. 락 범위는 실드 자체 _gate 라 단일 라이터 계약 무간섭.)
            var ringTimer = new System.Threading.Timer(
                _ => { try { ringPublisher.Sample(); } catch { } try { latencyPublisher.Sample(); } catch { } try { mmfFabric?.Beat(); } catch { } try { zombieShield.SweepOnce(); } catch { } }, null, dueTime: 0, period: 200);
            FormClosed += (_, _) => ShutdownTrace.Step("ringTimer", () => ringTimer.Dispose());
            // ★좀비 실드 능동 수거 타이머(콜드·1s < grace 5s) — 수거 가능 ALC 의 WeakReference 는 GC 이후에만 사멸하므로,
            //   grace 판정 전에 driveGc:true 로 배출을 능동 유도해 '게으른 GC' 로 인한 허위 좀비 격발을 봉쇄. 대기 등록이 있을 때만
            //   GC 를 유발(SweepOnce 유휴 스킵). 핫스왑 저빈도라 평시엔 무동작. δ=0 발주 핫패스와 스레드 분리(스레드풀 타이머).
            var zombieSweepTimer = new System.Threading.Timer(
                _ => { try { zombieShield.SweepOnce(driveGc: true); } catch { } }, null, dueTime: 1000, period: 1000);
            FormClosed += (_, _) => ShutdownTrace.Step("zombieSweepTimer", () => zombieSweepTimer.Dispose());

            // v4.76.0 안전 점화 시퀀서 + v4.90.0 웜 스타트 복구 엔진은 위(원장 메시 직후)에서 조립됨
            //   — ringPublisher 가 warmRestart 를 참조하므로 순서를 상향했다(중복 조립 금지).

            // S18 #2 / S19 #4 — 중앙 관제 허브: 5대 핵심 센서를 단일 불변 스냅샷으로 취합
            services.AddSingleton(new SystemStatusAggregator(
                attestation, MemoryLeakGuard.Shared, tuner, bridge, promotionGateway, rejectionForensics));

            // ★ [회원가입·로그인·라이선스 서브시스템] 모니터링/스마트팜/PLAI/UAV와 동일 · 전부 싱글턴(단일 사용자 데스크톱).
            //   LicenseService/StockEmailOtpService 는 LocalDatabase(db) 주입 · 나머지는 파라미터리스. 기존 트레이딩 권한 게이트와 별개.
            services.AddSingleton<StockManager.Common.Account.LicenseService>();
            services.AddSingleton<StockManager.Common.Account.MemberRegistryService>();
            services.AddSingleton<StockManager.Common.Account.StockEmailOtpService>();
            services.AddSingleton<StockManager.Common.Account.InstallTelemetryService>();
            services.AddSingleton<StockManager.Common.Account.SessionState>();

            // v11.4.1 Think-Beyond — 온보딩 헬스 센티널: 부팅 시 환경(런타임/데이터디렉터리/DPAPI/x86 프록시) + ★이관
            //   컴포넌트 Port DI 배선 + ★liveArmed:false 안전 자세를 결정론적으로 진단한다. fail-safe: 진단 자체가
            //   부팅을 깨지 않도록 예외를 삼키고, Blocked 판정은 구체적 해결 힌트와 함께 로그로 투사한다(공유 primary
            //   보호 — 하드킬 대신 정직 표출; 프로덕션 호스트는 Blocked 를 '안전 거부' 신호로 강제).
            var onboardingSentinel = new MS2E.Infrastructure.OnboardingHealthSentinel();
            services.AddSingleton(onboardingSentinel);
            try
            {
                var probe = new MS2E.Infrastructure.DefaultOnboardingProbe(services, canaryArmedAtBoot: false);
                var report = onboardingSentinel.Run(probe);
                System.Diagnostics.Debug.WriteLine(
                    $"[Onboarding] readiness={report.Readiness} total={report.Total} failed={report.Failed} blocking={report.BlockingFailed} firstBlocker={report.FirstBlocker}");
                if (report.Readiness == MS2E.Infrastructure.OnboardingReadiness.Blocked)
                    System.Diagnostics.Debug.WriteLine(
                        $"[Onboarding] ★BLOCKED — {MS2E.Infrastructure.OnboardingHealthSentinel.FixHint(report.FirstBlocker)}");
            }
            catch { /* 온보딩 진단은 부팅을 절대 깨지 않는다(fail-safe) */ }

            var sp = services.BuildServiceProvider();
            // v11.5.0 — ★하드웨어 킬스위치 결선: 격발 시 CanaryHealthSentinel 즉시 Kill(발주 인가 0·토큰 0x00) + API Secret 버퍼 전량 0x00 소거. sticky — 물리 이중 키 재무장 전까지 복구 불가.
            try
            {
                var canaryForKill = sp.GetService<MS2E.Infrastructure.CanaryHealthSentinel>();
                killBridge.Killed += _ =>
                {
                    try { canaryForKill?.Kill(MS2E.Infrastructure.CanaryKillReason.Manual); } catch { }
                    try { killPurger.PurgeAll(); } catch { }
                };
            }
            catch { /* 킬스위치 결선 실패가 부팅을 깨지 않는다(fail-safe) */ }
            // 기동 시 — 관리자(GSLEE) 시드 보장 + 설치 PC 텔레메트리 1회 수집(실패해도 앱 기동 무영향).
            try { sp.GetRequiredService<StockManager.Common.Account.MemberRegistryService>(); } catch { /* 시드 실패 무시 */ }
            try { sp.GetRequiredService<StockManager.Common.Account.InstallTelemetryService>().CaptureAndPersist(); } catch { /* 텔레메트리 실패 무시 */ }

            // v12.0.0 — ★골든마스터 부팅 무결성 센티널(Think-Beyond Engine B): RCL 0 엔진참조·호스트 래칫·liveArmed:false·
            //   물리 이중키 미연결·금고 봉인을 정적 스캔(fail-closed). 1결함이라도 !AllPassed → 로그(골든마스터 strict 빌드는 부팅 거부).
            try
            {
                var canaryForGm = sp.GetService<MS2E.Infrastructure.CanaryHealthSentinel>();
                var probe = new BootGmProbe(canaryForGm);
                var hostAsm = typeof(MS2E.Infrastructure.CanaryHealthSentinel).Assembly;
                var rclAsm = typeof(MS2E.UI.Portable.Ports.Validate.IValidateRepositoryPort).Assembly;
                var gm = MS2E.Infrastructure.GoldenMasterBootSentinel.Scan(hostAsm, rclAsm, probe, out var gmChecks);
                if (!gm.AllPassed)
                    System.Diagnostics.Debug.WriteLine($"[GoldenMaster] ★무결성 결함 {gm.Passed}/{gm.Total} — 골든마스터 strict 빌드는 부팅 거부. 결함: {string.Join(", ", System.Linq.Enumerable.Select(System.Linq.Enumerable.Where(gmChecks, c => !c.Passed), c => c.Id))}");
            }
            catch { /* 골든마스터 스캔 실패가 부팅을 깨지 않는다(fail-safe) */ }

            var view = new BlazorWebView
            {
                Dock = DockStyle.Fill,
                HostPage = "wwwroot/index.html",
                Services = sp
            };
            view.RootComponents.Add<App>("#app");
            Controls.Add(view);
        }

        // v12.0.0 — 골든마스터 부팅 프로브: 카나리 미무장(실측) + 물리 이중키 미연결·금고 봉인(부팅 안전 기본).
        //   ※strict 골든마스터 빌드는 VaultSealed 를 실제 DPAPI 봉인 검사로 대체(런북 참조).
        private sealed class BootGmProbe : MS2E.Infrastructure.IGoldenMasterProbe
        {
            private readonly MS2E.Infrastructure.CanaryHealthSentinel? _canary;
            public BootGmProbe(MS2E.Infrastructure.CanaryHealthSentinel? c) => _canary = c;
            public bool LiveArmedFalse => _canary is null || !_canary.IsArmed;   // 부팅 시 미무장(liveArmed:false)
            public bool DualKeyAbsent => true;   // 소프트웨어 부팅 경로에 물리 이중 키 접점 없음(안전 기본)
            public bool VaultSealed => true;     // API 금고 DPAPI 봉인(평문 시크릿 미배치 — 설치 규약)
        }

        // ★종료 진단 계측 — 종료 지연 원인 특정용. WM_CLOSE(최초 진입)부터 각 단계 소요를 %LOCALAPPDATA%\MS2E.StockSuite\
        //   shutdown_trace.log 에 기록한다. WM_CLOSE→OnFormClosing 갭(WebView2 pre-close)·핸들러 소요를 분리 실측.
        protected override void WndProc(ref System.Windows.Forms.Message m)
        {
            if (m.Msg == 0x0010) ShutdownTrace.Begin("WM_CLOSE");   // WM_CLOSE — 사용자 닫기 최초 수신 시각
            base.WndProc(ref m);
        }

        // [롤오버] 종료 게이트(생성 시 결선) — false = 종료 취소(승계 미확인 · 운용자 거부)
        private Func<bool>? _rolloverCloseGate;

        protected override void OnFormClosing(System.Windows.Forms.FormClosingEventArgs e)
        {
            if (!e.Cancel && _rolloverCloseGate is { } gate && e.CloseReason != System.Windows.Forms.CloseReason.WindowsShutDown)
            {
                bool allow;
                try { allow = gate(); } catch (Exception) { allow = true; }   // 게이트 자체 고장이 종료를 영구히 막지 않게
                if (!allow) { e.Cancel = true; return; }
            }
            ShutdownTrace.Begin("OnFormClosing");   // WM_CLOSE 를 못 거친 경로(코드 종료) 대비 — 이미 시작됐으면 no-op
            ShutdownTrace.Mark("OnFormClosing enter");
            base.OnFormClosing(e);
            ShutdownTrace.Mark("OnFormClosing exit");
        }

        // ★즉시 종료 보장 — 등록된 FormClosed 핸들러(저널·원장·회계 플러시, 상태 스냅샷, 워커 Dispose)가 전부 완료된
        //   직후 프로세스를 즉시 종료한다. WinForms 폼 Dispose 단계의 WebView2(브라우저 프로세스) graceful teardown(수 초)을
        //   건너뛰어 '닫으면 바로 닫힘'을 보장. 모든 워커 스레드는 IsBackground 이라 OS 가 회수하고, 데이터 영속화(SQLite WAL·
        //   저널 플러시·스냅샷)는 base.OnFormClosed 에서 동기 완료되므로 하드 종료가 안전하다.
        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            ShutdownTrace.Mark("OnFormClosed enter — FormClosed 핸들러 실행 시작");
            base.OnFormClosed(e);   // 모든 FormClosed 핸들러 실행 완료(플러시·Join·스냅샷) 후
            ShutdownTrace.Mark("OnFormClosed base done — 전 핸들러 완료");
            ShutdownTrace.Flush();
            try { System.Environment.Exit(0); } catch { }
        }

        // ── v4.69.0: IModuleBridge DI 어댑터 ─────────────────────────────────────
        // ModuleBridge(정적) 에 STA 마샬링이 이미 내장되어 있으므로 단순 위임으로 충분.
        private sealed class HostBoundModuleBridge : IModuleBridge
        {
            public void Open(string key) => ModuleBridge.Open(key);
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
                ShutdownTrace.Step("snapshotTimer", () => _snapshotTimer?.Dispose());
                ShutdownTrace.Step("hydration.Snapshot", () => hydration.Snapshot());   // 종료 시 최종 상태 영속화
            };

            return (hydration, journaler);
        }
    }
}
