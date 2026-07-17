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
            FormClosed += (_, _) => { try { freezeRelay.Dispose(); } catch { } };
            services.AddSingleton(attestation);            // S12 #2 — 거래 증적 감사 (ForceFreeze 게이트)
            // v4.95.0 — 무할당 순환 커널 블랙박스 + 동결 원자 덤퍼(감사 오디트 매그넷). 조기 생성 — 핫패스 엔진들이 Record 결속.
            //   ★스레드 격리: Record 는 δ=0 락-프리 push(50ms 드레인·MMF IPC 무간섭). 동결 시 덤프는 전용 BelowNormal 스레드(무I/O 핫패스).
            //   ★liveArmed:false: 관측·영속화만 — IBroker/소켓/무장 술어 참조 0. 덤프 파일 생성이 이중키 게이트에 우회 미생성.
            var blackbox = new MmfFlightRecorder();
            services.AddSingleton(blackbox);   // TelemetryMonitor 🗃️ 블랙박스 & 감사 매그넷 패널 주입 대상
            var blackboxDumper = new CrashResilientDumper(blackbox, attestation);
            blackboxDumper.Start();            // att 동결 옵저버 등록(Seal+신호) + 격리 덤프 스레드 기동
            services.AddSingleton(blackboxDumper);
            FormClosed += (_, _) => { try { blackboxDumper.Dispose(); } catch { } };
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
            Func<bool> armedPredicate = DispatchArming.ResolvePredicate(
                cfg.GetBool("Dispatch.LiveArmingWired", false), armingGate,
                blockWhile: () => warmRestartRef?.IsRecovering ?? false);
            // v5.0.0 — 블래스트 반경 제한기: 카나리 검역창 동안 명목 상한을 송출 초크포인트(디스패처)에서 물리 강제(기본 무제한).
            var blastRadius = new BlastRadiusLimiter();
            services.AddSingleton(blastRadius);
            var ringDispatcher = new GatedOrderDispatcher(dispatchRing, armedPredicate, attestation, blackbox, blastRadius);
            var ringChannel    = new RingTelemetryChannel();
            // v4.63.0 — 다중 seqlock 통합: 시나리오 변이 seqlock(ScenarioPublisher·페이지 transient) 카운터를 싱글톤
            //   브리지로 받아 중앙 샘플러가 합류. FutureSimPage 가 매 틱 Report → ringPublisher.Sample 이 Read → 채널 발행.
            var scenarioMon    = new ScenarioSeqlockMonitor();
            // v4.73.0 — 자율 알파 공장: BelowNormal 백그라운드 스레드(24h 주기). EmergencyFreeze → fail-closed 스킵.
            //   외부 시세 API 의존 0(CartridgeEngine.ReferenceSeries(seed=42)). 카운터는 ringPublisher.Sample 이 합류.
            var alphaFactory = new AutonomousAlphaFactory(db, attestation);
            alphaFactory.Start();
            services.AddSingleton(alphaFactory);
            FormClosed += (_, _) => { try { alphaFactory.Dispose(); } catch { } };
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
            FormClosed += (_, _) => { try { ledgerMesh.Dispose(); } catch { } };
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
            FormClosed += (_, _) => { try { accountingMesh.Dispose(); } catch { } };
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
            FormClosed += (_, _) => { try { shadowMesh.Dispose(); } catch { } };
            var shadowGateway = new ShadowExecutionGateway(liveAttestation: attestation, shadowMesh: shadowMesh);
            shadowGateway.StartPump();
            services.AddSingleton(shadowGateway);   // TelemetryMonitor 🕹️ 컷오버 & 섀도우 플라이트 대시보드 주입 대상
            FormClosed += (_, _) => { try { shadowGateway.Dispose(); } catch { } };
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
            FormClosed += (_, _) => { try { deadman.Dispose(); } catch { } };
            // v4.90.0 — 결정론적 웜 스타트 복구 엔진: 동결 해제 후 원장 백로그 소화(동결창 가격정합성 펜스) + 점화 FSM 점진 승격.
            //   ★기존 싱글톤(attestation·securityVault·ledgerMesh·ignitionSeq) 공유 — 새 인스턴스 금지(백로그·봉인 래치가 그 인스턴스에만).
            //   fail-closed(ForceFreeze 단일 소스 + 금고 재봉인)·δ=0(펜스 정수 산술·SecretLease ArrayPool)·결정론.
            var warmRestart = new DeterministicWarmRestartEngine(attestation, securityVault, ledgerMesh, ignitionSeq);
            warmRestartRef = warmRestart;   // 복구기 잠금 펜스 지연 클로저 결속(위 armedPredicate)
            services.AddSingleton(warmRestart);
            FormClosed += (_, _) => { try { warmRestart.Dispose(); } catch { } };
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
            FormClosed += (_, _) => { try { newsIntel.Dispose(); } catch { } };
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
            if (mmfFabric is { }) { services.AddSingleton(mmfFabric); FormClosed += (_, _) => { try { mmfFabric.Dispose(); } catch { } }; }
            var ringPublisher  = new RingTelemetryPublisher(dispatchRing, ringDispatcher, ringChannel, null, scenarioMon, alphaFactory, evolutionPipeline, ignition, execGuard, ledgerMesh, warmRestart, chaosGrid, prodSwap, newsIntel, riskShield, shadowRing, mmfFabric, blackbox, blackboxDumper, accountingMesh, shadowGateway, cutoverGate, accountGenesis, canaryProbe, blastRadius, killSwitch, deadman);
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
            // v4.70.0 — 발주 밸브 결속: Drain 루프(50ms) → IBroker.Place() 배선.
            //   Dispatch.ValveWired=true 일 때만 타이머 가동(기본 false — 드라이런 박제).
            //   LiveArmingWired 와 독립 스위치 — 둘 다 ON 이어야 실 주문 흐름(이중 키 잠금).
            // v4.87.0 — fail-closed 강화: 드레인 루프 예외 or 이중키 무결성(런타임 ValveWired) 붕괴 → 강등이 아니라
            //   ForceFreeze 단일 소스로 영구 락업. dualKeyIntact 는 매 틱 ValveWired 설정이 유지되는지 검문.
            var drainWorker = new OrderDrainWorker(ringDispatcher, broker,
                wired: cfg.GetBool("Dispatch.ValveWired", false),
                periodMs: 50,
                attestation: attestation,
                dualKeyIntact: () => cfg.GetBool("Dispatch.ValveWired", false));
            services.AddSingleton(drainWorker);
            FormClosed += (_, _) => { try { drainWorker.Dispose(); } catch { } };
            // 200ms 주기 샘플러 타이머 — UI 폴링(150ms)보다 약간 빠름. 단일 라이터(ringPublisher·latencyPublisher) 보장.
            var ringTimer = new System.Threading.Timer(
                _ => { try { ringPublisher.Sample(); } catch { } try { latencyPublisher.Sample(); } catch { } try { mmfFabric?.Beat(); } catch { } }, null, dueTime: 0, period: 200);
            FormClosed += (_, _) => { try { ringTimer.Dispose(); } catch { } };

            // v4.76.0 안전 점화 시퀀서 + v4.90.0 웜 스타트 복구 엔진은 위(원장 메시 직후)에서 조립됨
            //   — ringPublisher 가 warmRestart 를 참조하므로 순서를 상향했다(중복 조립 금지).

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
                try { _snapshotTimer?.Dispose(); } catch { }
                try { hydration.Snapshot(); } catch { }   // 종료 시 최종 상태 영속화
            };

            return (hydration, journaler);
        }
    }
}
