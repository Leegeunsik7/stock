// ============================================================================
// RingTelemetry — S29 Path Gamma: SPMC 주문 디스패치 링 → UI 락-프리 단방향 텔레메트리
//
//  v4.50 OrderDispatchRing/GatedOrderDispatcher 의 실시간 지표(처리량·무장송출·게이트차단·추월드롭·
//  CAS 낙방)를, UI 렌더링 스레드가 가변 주기로 '무할당·락-프리'로 읽어 관제탑에 비춘다. 금융 핫패스
//  (발행/드레인)와 UI 독출을 공간 격리 — UI 는 코어를 절대 블록하지 않고, 경합 시 직전 프레임을 유지한다.
//
//  ── seqlock(단일 라이터) — ForensicTelemetryChannel 검증 idiom 계승 ──────────
//  멀티필드 값 타입은 원자 쓰기가 불가하므로 짝/홀 버전 커서로 보호한다. 라이터(샘플러)는 홀(쓰기 시작)→
//  payload→짝(완료) 순으로 발행하고, 리더(UI)는 v1(짝) 독출→payload 복사→v2 재독출→v1==v2 검증으로
//  torn read 를 격리한다. x64 전용(TSO + Volatile 배리어). 거짓 공유는 Version↔Data 캐시라인 격리로 차단.
//  ★제네릭 미사용 사유: 기존 채널은 [StructLayout(Explicit)] Cell 에 구체 Data 필드를 둔다 — 제네릭
//  타입파라미터엔 FieldOffset 을 못 박으므로, 검증된 idiom 을 링 프레임 전용으로 미러링한다(재발명 아님).
// ============================================================================
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace StockManager.Common
{
    /// <summary>관제탑이 비추는 링 디스패처 계기 스냅샷(블리터블 값 타입·참조 0·힙 0).</summary>
    public readonly struct RingTelemetry
    {
        public readonly long   Published;         // 링 최고 발행 시퀀스(-1 = 미발행)
        public readonly long   Forwarded;         // 무장 상태 실 송출 수(onSend 성공)
        public readonly long   GatedOut;          // 미무장(fail-closed) 차단 수
        public readonly long   Dropped;           // 추월 유실(소비 지연 계측)
        public readonly long   WriterCollisions;  // 단일 생산자 CAS 낙방(동시 발행 위반)
        public readonly double ThroughputPerSec;  // 직전 샘플 대비 발행 처리량(건/초)
        public readonly long   SendErrors;        // Mission A — downstream throw 누적(브로커 단절 등). 0 이 정상.
        // v4.63.0 — 다중 seqlock 통합: 시나리오 변이 seqlock(ScenarioPublisher) 채널. Version 0 = 시뮬 페이지 비활성(미보고).
        public readonly long   ScenarioVersion;          // 시나리오 seqlock 버전(0=미보고)
        public readonly long   ScenarioWriterCollisions; // 시나리오 단일 라이터 경합(슬라이더 변이)
        public readonly long   ScenarioFrozen;           // 시나리오 산출 동결(음수 배수·오버플로 등)
        // v4.73.0 — 자율 알파 공장 카운터 통합. 공장 미배선(null) 시 전부 0 — UI "—" 표시.
        public readonly long   AlphaFactoryCycles;    // 완료 사이클 수
        public readonly long   AlphaFactorySigned;    // ECDsa 서명 완료 전략 수(누적)
        public readonly long   AlphaFactoryRejected;  // 수치 가드(CartridgeNumericsGuard) 거부 수(누적)
        // v4.85.0 — 자기 제어 밸브: 최근 창(≤1000 시행) 롤링 거부율 + 자동/수동 일시정지 상태(Amber Alert).
        public readonly double AlphaFactoryRecentRejRate;   // 0..1 (누적 아님·롤링)
        public readonly long   AlphaFactorySuspended;       // 0=가동, 1=일시정지(밸브 트립)
        // v4.86.0 — 자율 진화 토너먼트 파이프라인 FSM + 승격/서명 지표.
        public readonly long   AlphaPipelineMode;           // TournamentPipelineMode: 0 Idle/1 Testing/2 Signing/3 Deployed/4 Paused
        public readonly long   AlphaPromoted;               // 승격 제출(PendingApproval) 누적
        public readonly double AlphaSigningSuccessRate;     // 서명 성공률 0..1
        // v4.87.0 — 집행 미시구조: IgnitionLock FSM 단계 + 실시간 슬리피지/σ(raw) + 강등 누적.
        public readonly long   IgnitionStageCode;           // 0 Locked/1 ProbeReady/2 TimeBoxOpen/3 MasterIgnited
        public readonly long   SlippageRaw;                 // 최근 슬리피지(raw·10^8)
        public readonly long   SlippageSigmaRaw;            // 롤링 σ(raw·10^8) — 워밍업/퇴화 시 0
        public readonly long   ExecDowngrades;              // FSM 강등(Step-Down) 누적
        // v4.88.0 — 원장 보호 메시: 비동기 큐 깊이/용량 + 평균 디스크 영속화 소요 + 누적 쓰기 실패.
        public readonly long   LedgerQueueDepth;            // 현재 큐 깊이
        public readonly long   LedgerCapacity;              // 큐 용량(UI 포화% 산출)
        public readonly double LedgerAvgPersistMicros;      // 평균 영속화 소요(µs)
        public readonly long   LedgerWriteFailures;         // 누적 쓰기 실패(포화·영속화 예외)
        // v4.90.0 — 결정론적 웜 스타트 복구 엔진: 복구 단계·백로그 잔여·프로브 RTT·프로브 슬리피지σ.
        public readonly long   RecoveryStateCode;           // 0 Idle/1 Validating/2 Draining/3 Escalating/4 Stable/5 Frozen
        public readonly long   RecoveryBacklog;             // 드레인 잔여 백로그 개수
        public readonly long   ProbeRttMs;                  // 최근 프로브 파이프라인 왕복 지연(ms)
        public readonly long   ProbeSlippageSigmaRaw;       // 최근 프로브 관측 슬리피지 σ(raw·10^8)
        // v4.91.0 — 결정론적 카오스 테스팅 그리드: 가상 시간 진행·활성 카오스 플래그·PBT 누적 패스·가상 큐 깊이.
        public readonly long   VirtualTimeTicks;            // 마지막 카오스 실행 경과 가상 tick(1000× 압축 시간선)
        public readonly long   ChaosFlags;                  // 활성 장애 비트마스크(1 Kiwoom/2 Toss/4 FxSpike/8 FxStale)
        public readonly long   PbtPasses;                   // 속성 기반 테스트(PBT) 누적 통과 수
        public readonly long   VirtualQueueDepth;           // 가상 원장 큐 깊이(마지막 실행)
        // v4.92.0 — 프로덕션 자격증명 결속: 브로커 연결상태·토큰 잔여수명·방화벽 차단·실전 엔드포인트 감지.
        public readonly long   KiwoomLinkCode;              // BrokerLinkState 0 Disconnected/1 Connecting/2 Authenticated/3 SessionStale
        public readonly long   TossLinkCode;                // BrokerLinkState
        public readonly long   TossTokenRemainSec;          // 인증 토큰 잔여 수명(초)
        public readonly long   EndpointFirewallBlocks;      // 엔드포인트 변이 방화벽 차단 누적(0=청정)
        public readonly long   ProductionLiveMask;          // 실전 엔드포인트 감지 브로커 비트마스크(0=전부 모의/안전)
        // v4.93.0 — 정보 인텔리전스: 뉴스 인입속도·시장 감성·세맨틱 쇼크·감성 브레이커·미분석 격리·멘토 FSM.
        public readonly long   NewsRatePerMin;              // 롤링 뉴스 인입 속도(개/분)
        public readonly long   MarketSentimentRaw;          // 시장 감성 지표(QFixed -1..+1 × 10^8, 0=미분석)
        public readonly long   SemanticShockActive;         // 세맨틱 쇼크 전파 가동(0=무·비트마스크 쇼크분류)
        public readonly long   SentimentBreakerTripped;     // 감성 속도 브레이커 트립(0=정상·1=강등 트립)
        public readonly long   NewsUnanalyzedCount;         // 미분석(SentimentRaw==0) 격리 건수
        public readonly long   MentorFsmCode;               // AI 멘토 파이프라인 FSM(0 Idle/1 Analyzing/2 Advised/3 Amber)
        // v4.94.0 — 초고속 집행·리스크 실드: 증거금 포화·예약 거부·다중 브로커 링 노드·MMF µs 레이턴시·패브릭 가드 트립.
        public readonly long   MarginUtilizationRaw;        // 증거금 예약/예산 × 10^8(0..10^8) — 포화도
        public readonly long   ReservationRejects;          // 락-프리 예약 거부 누적(경량 필터)
        public readonly long   ShadowRingNodeMask;          // 다중 브로커 분산 링 활성 노드 비트마스크(AssetClass)
        public readonly long   MmfLatencyMicros;            // MMF IPC 프레임 왕복 레이턴시(µs)
        public readonly long   FabricGuardMask;             // ★가드 트립: bit0=리스크 실드 동결·bit1=MMF 오염 동결(오버레이 하드바인딩)
        // v4.95.0 — 무할당 순환 블랙박스: 링 포화도·초당 기록 속도·동결 덤프 상태·봉인 후 유실.
        public readonly long   BlackboxSaturationRaw;       // 순환 링 포화도(0..1 × 10^8)
        public readonly long   BlackboxRatePerSec;          // 초당 이산 사건 기록 속도
        public readonly long   BlackboxDumpState;           // 0 유휴/1 봉인·덤프중/2 완료/3 실패(감사 매그넷 트리거)
        public readonly long   BlackboxDroppedSealed;       // 봉인(동결) 후 드롭 누적
        // v4.96.0 — 실시간 청산 메쉬: 누적 수수료 KRW/USD·정밀 잔여 캐리 오차·청산 래치 큐 깊이·예수금 영점 상태.
        public readonly long   AccFeeKrwRaw;                // 누적 차감 제비용(원화, raw)
        public readonly long   AccFeeUsdRaw;                // 누적 차감 제비용(달러, raw)
        public readonly long   AccCarryErrorRaw;            // 정밀 잔여 캐리 오차(raw·<Scale 정상)
        public readonly long   AccLatchDepth;               // 청산 래치 큐 미소화 깊이
        public readonly long   AccStateCode;                // 0 유휴/1 가동(영점동기)/2 동결(디싱크)
        // v4.97.0 — 실거래 섀도우 인입 & 원자 컷오버: 섀도우모드·가상체결 RTT·컷오버 상태·카나리 편차·듀얼트랙 드리프트.
        public readonly long   ShadowModeActive;            // 1 섀도우(자본 안전)/0 실전 라인
        public readonly long   ShadowFillRttMicros;         // 최근 가상 체결 공급 RTT(µs)
        public readonly long   CutoverStateCode;            // 0 섀도우/1 사전무장/2 검역/3 실전/4 되감김/5 동결
        public readonly long   CanaryDeviationRaw;          // 최근 카나리 RTT 단측 편차(raw·>0 느려짐)
        public readonly long   DualTrackDriftRaw;           // 최근 듀얼트랙(섀도우예측 vs 실체결) 드리프트(raw)
        // v5.0.0 — 실전 총점화: Genesis 상태·실 예수금·Canary net 대조·블래스트 반경·킬스위치·데드맨.
        public readonly long   GenesisStateCode;            // 0 대기/1 동기화완료/2 거부
        public readonly long   GenesisDepositKrwRaw;        // Genesis 시딩된 실 예수금(원화, raw)
        public readonly long   CanaryNetDriftRaw;           // 최근 카나리 건별 net 정산 대조 드리프트(raw)
        public readonly long   BlastRadiusCeilingRaw;       // 카나리 검역창 명목 상한(raw·long.MaxValue=무제한)
        public readonly long   KillSwitchStateCode;         // 0 대기/1 발동됨
        public readonly long   DeadmanStateCode;            // 0 유휴/1 감시중/2 역-컷오버됨
        // v5.1.0 — 자율 라이브 감시망: 세션 지연·Coma·페일오버·쓰레드풀 가용성·블랙스완(고아) 방어선.
        public readonly long   SessionLatencyMicros;        // 최근 세션 왕복 지연(µs)
        public readonly long   SessionHealthCode;           // 0 건강/1 Coma(혼수)
        public readonly long   FailoverActiveChannel;       // 0 Primary/1 Standby/2 None(양채널 다운)
        public readonly long   FailoverCount;               // 자율 페일오버 누적
        public readonly long   ThreadPoolAvailabilityMilli; // ThreadPool 가용성 점수(0..1000·높을수록 건강)
        public readonly long   OrphanInFlight;              // 현재 미해제(in-flight) 주문 수
        public readonly long   BlackSwanDefenseCode;        // 0 정상/1 고아 탐지/2 강제 동결(자본 묶임 방어)
        // v5.2.0 — 미시 유동성 슬라이서 & 스로틀: 5호가 비대칭·슬라이스·스로틀·슬리피지 절감·독약 유동성.
        public readonly long   ImbalanceScoreRaw;           // 5호가 잔량 비대칭 지수([-Scale,+Scale]·+매수우세)
        public readonly long   LiquiditySlicesEmitted;      // 누적 사출 슬라이스 수
        public readonly long   OrderThrottleCount;          // 스로틀(Hold) 작동 누적
        public readonly long   SlippageSavedBps;            // 누적 슬리피지 절감 추정(bps·덤프 대비 회피)
        public readonly long   ToxicityCode;                // 0 Clear/1 Caution/2 Toxic(독약 유동성)
        // v5.3.0 — 무정지 전략 핫패치: 활성 세대·빌드ts·이월 RTT·좀비·고아 이벤트.
        public readonly long   StrategyGeneration;          // 현 활성 전략 세대(핫스왑 카운트)
        public readonly long   StrategyBuildTicks;          // 활성 전략 빌드 타임스탬프(ticks·0=미배선)
        public readonly long   StateCarryRttNs;             // 최근 상태 이월 RTT(ns)
        public readonly long   ZombieAssemblyCount;         // 좀비 어셈블리(언로드 실패) 누적
        public readonly long   OrphanEventsRejected;        // 세대 펜스 거부 고아 이벤트 누적
        // v5.4.0 — 다중 전략 인터리빙 메쉬 & 바스켓 네팅: 활성 전략·봉인·네팅 절감·자전거래·공진·자본 포화.
        public readonly long   ActiveStrategyCount;         // 활성 공존 전략 인스턴스 수
        public readonly long   SealedLaneCount;             // 봉인/진압된 전략 레인 수(방화벽·자전거래)
        public readonly long   NettingSavedCostRaw;         // 누적 네팅 왕복 비용 절감(raw·수수료+세)
        public readonly long   WashTradeRejections;         // 자전거래 차단막 거부 누적
        public readonly long   ResonanceMetric;             // 최대 쌍 공동낙폭 공진 메트릭(위험 수치)
        public readonly long   CapitalSaturationBps;        // 전 레인 자본 포화도(bps·예약/할당)
        // v5.5.0 — 영지식 로깅 패브릭 & 커널 지터 센티널: 로그 영속속도·큐·드롭·압축비·커널지터·안전홀드·NIC드롭·I/O방어상태.
        public readonly long   LogPersistBytesPerSec;       // 초당 이진 로그 영속 바이트(MB/s 파생)
        public readonly long   LogQueueDepth;               // 디스크 I/O 백그라운드 큐 포화도(미소비 이벤트)
        public readonly long   LogDroppedTotal;             // 링 만원 드롭-오래된-것 누적(디스크 stall 징후)
        public readonly long   LogCompressionBps;           // 압축비 bps(압축/원본 ×10000·낮을수록 효율↑)
        public readonly long   KernelJitterMicros;          // OS 커널 정밀 지터(µs·최근)
        public readonly long   KernelSafeHold;              // 커널/페이지 안전 홀드 활성(0/1)
        public readonly long   NicDropsEstimate;            // NIC 링버퍼 패킷 드롭 추정 누적
        public readonly long   IoDefenseStatus;             // OS/HW 방어선 상태(0 정상·1 페이지 홀드·2 디스크 stall 동결)
        // v6.0.0 Path Gamma — RIO 커널 바이패스 + 결정론 플레이백 + ★CausalWeave(인과 워터마크 통합 방어).
        public readonly long   RioBypassActive;             // 1=RIO 커널 바이패스 나노초 수신 중
        public readonly long   RioReceiveLatencyNanos;      // 최근 RIO 수신 지연(ns·dequeue→publish)
        public readonly long   RioFallbackActive;           // 1=RIO 미지원 → 관리형 소켓 안심 강등 수신
        public readonly long   RioFramesReceived;           // 수신 프레임 누적(RIO+폴백)
        public readonly long   PlaybackStateCode;           // PlaybackState 0 Idle/1 Playing/2 Paused/3 Stepping/4 Completed/5 Faulted
        public readonly long   PlaybackPlayed;              // 재생된 이벤트 수
        public readonly long   PlaybackTotalFrames;         // 전체 이벤트 수(진행률 분모)
        public readonly double PlaybackFps;                 // 재생 초당 프레임(FPS)
        public readonly long   PlaybackIntegrityRaw;        // 덤프 디코딩 무결성(0..10^8·CRC/SHA 통과 비율)
        public readonly long   CausalWeaveStateCode;        // CausalWeaveState 0 Nominal/1 Pressure/2 Evicting/3 FuseTripped
        public readonly long   CausalRingEvictions;         // ★오버런 증명 회수(조용한 드롭 0·newest-wins)
        public readonly long   CausalRingSaturationPermille;// 링 포화도(‰·0..1000)
        public readonly long   CausalFuseTripped;           // 1=타임-와프 퓨즈 트립(재생 fail-closed 정지)
        // v7.0.0 Phase 7 — RIO 하드웨어 사전점검 프로브 + 암호학적 감사 금고.
        public readonly long   RioProbeSupported;           // 1=RIO 지원 어댑터 감지
        public readonly long   RioProbeAdapterCount;        // 물리 어댑터 수
        public readonly long   RioProbeMaxRcvBufBytes;      // 최대 실효 수신 소켓버퍼(bytes)
        public readonly long   RioProbeRecommendFallback;   // 1=안심 강등 권고(RIO 미지원)
        public readonly long   AuditLedgerIntegrityCode;    // 0 미검증/1 INTACT/2 CORRUPTED
        public readonly long   AuditLedgerCount;            // 감사 원장 레코드 수
        public readonly long   AuditLedgerSealed;           // 1=트립 포렌식 봉인(비가역 차단)
        public readonly long   AuditLedgerHeadHash;         // 헤드 해시 상위 8B(포렌식 표시)
        // v7.2.0 — 실시간 포트폴리오 VaR & Gershgorin 특이점 방화벽.
        public readonly long   PortfolioVar99Krw;           // 99% parametric VaR 추정 손실액(₩)
        public readonly long   PortfolioStressLossKrw;      // 스트레스(지수/환율/금리 복합) 추정 낙폭(₩)
        public readonly double VarGershgorinBound;          // 공분산 최소 고유값 하한(Gershgorin 원판정리)
        public readonly long   VarRidgeInjections;          // 적응형 리지 주입 누적 횟수
        public readonly long   RiskValveState;              // 0 정상/1 한도초과(신규 발주 tighten-only 조임)
        public readonly long   CovGuardState;               // 0 정상/1 정규화/2 특이점 퓨즈
        // v7.3.0 — 하드웨어 P-Core 격리 & 서멀/주파수 & 캐시라인 코히런스.
        public readonly long   CpuPCoreCount;               // 감지된 P-Core(고성능) 논리프로세서 수(0=미지원/미감지)
        public readonly long   CpuPinnedThreads;            // P-Core 물리 핀 고정 스레드 수
        public readonly long   CpuMigrationEvents;          // 핀 스레드의 P-Core 이탈(OS 강제 이주) 누적
        public readonly long   CpuPinFailures;              // 핀 시도 실패 누적
        public readonly long   ThermalEffectiveFreqPermille;// 유효주파수 ‰(기준선 최고 대비·1000=최고·↓=스로틀)
        public readonly long   ThermalThrottleHolds;        // 서멀 스로틀 안전홀드 발동 누적
        public readonly long   ThermalSafeHold;             // 1=서멀 스로틀 안전홀드 활성(신규발주 tighten-only)
        public readonly long   CacheCrossCoreWrites;        // 캐시라인 슬롯 크로스코어 기록(라인 바운싱/코어 마이그레이션 지표)
        public readonly long   CacheCoherenceFuse;          // 1=아레나 카나리 파열(코히런스 퓨즈 트립·안전홀드)
        // v7.4.0 — Genesis Pre-Flight 인증 & 안전 패브릭(무장 임대·데드맨·fat-finger/폭주 인터록).
        public readonly long   PreFlightScore;              // 종합 점검 총점(0~100)
        public readonly long   PreFlightForensic;           // 실패 서브체크 비트마스크(0=전 항목 적격)
        public readonly long   ArmingLeaseRemainingSec;     // 무장 임대 잔여(초·0=dormant/만료)
        public readonly long   ArmingLeasePermille;         // 무장 임대 잔여 ‰(0~1000·게이지)
        public readonly long   DeadManHeartbeatAgeMs;       // 데드맨 최근 심박 경과(ms)
        public readonly long   DeadManTripped;              // 1=데드맨 스위치 격발(밸브 하드 차단)
        public readonly long   FatFingerFrozen;             // 1=fat-finger/폭주 인터록 동결(사출 차단)
        public readonly long   FatFingerVetoes;             // fat-finger 가격+폭주 veto 누적
        public readonly long   FatFingerForensic;           // 최근 veto 포렌식(가격괴리/명목예산/버스트 비트)
        // v8.0.0 — AVX-512 초저지연 미시 알파 엔진 & SIMD 노이즈/오토튜너.
        public readonly long   AvxAccelTier;                // 활성 SIMD 가속 티어(0 스칼라/1 AVX2/2 AVX-512)
        public readonly long   SimdAlphaLatencyNanos;       // SIMD 알파 연산 지연(선택 폭 op당 ns)
        public readonly long   SimdAlphaMilli;              // 최근 미시 알파×1000
        public readonly long   SnrNoiseBlocks;              // SNR 노이즈 필터 스푸핑 차단 누적
        public readonly long   AvxWidthDowngrades;          // 웜업/다운클럭 실측 → ZMM 강등 누적
        public readonly long   ZmmWarmKeepAlives;           // ZMM hot-ready keep-alive 누적
        public readonly long   AvxCalibrations;             // 오토튜너 실측 조율 누적
        public readonly long   SimdAlignedLoads;            // 64B 정렬 SIMD 로드 누적(벡터라이저)
        // v8.1.0 — 안티-RAM 퀀텀 금고 & Phase 83 인프라 해방.
        public readonly long   QuantumVaultArmed;           // 1=퀀텀 금고 감시 활성
        public readonly long   QuantumVaultBreached;        // 1=비인가 접근 탐지·자가 소멸 격발
        public readonly long   HoneyDecoyTripped;           // 1=허니키 디코이 트랩 접촉(변조/유출)
        public readonly long   AntiHookState;               // 0 clean/1 후킹 변조/2 미지원
        public readonly long   InfraCouplingCount;          // infra-scan 현재 결합도(코어 직결 인프라 참조)
        public readonly long   RcuConfigVersion;            // RCU Config 스냅샷 세대
        public readonly long   SecureZeroVerifyFailures;    // 시큐어 제로화 판독 검증 실패(0=정상)
        public readonly long   VaultZeroizedBuffers;        // 브리치 시 원자 제로화된 시크릿 버퍼 수
        // v9.0.0 — 스마트 오더 라우터 & 빙산호가 포획.
        public readonly long   SorRoutingActive;            // 1=SOR 라우팅 계획 산출 중
        public readonly long   SorRoutes;                   // 실행 가능 라우팅 산출 누적
        public readonly long   SorLastRoutingCode;          // 최근 라우팅 코드(공격/수동/빙산흡수/스푸핑차단 비트)
        public readonly long   IcebergHiddenQtyRaw;         // 포획된 빙산 숨은 물량 추정(raw 10^8)
        public readonly long   IcebergsDetected;            // 빙산 포획 누적
        public readonly long   QueueFillImminencePermille;  // 내 지정가 큐 체결 임접률(‰)
        public readonly long   SorSpoofBlocks;              // 스푸핑 레이어 라우팅 차단 누적
        public readonly long   LiquidityAuthenticityPermille; // 유동성 진위 ‰(1000=진짜·0=가짜 스푸핑)
        // v9.1.0 — 극단적 카오스 결함 주입 샌드박스.
        public readonly long   ChaosSanitizerState;         // 0 standby / 1 injecting
        public readonly long   ChaosFaultMask;              // 현재 주입 결함 비트(1 소켓/2 메모리/4 서멀/8 torn)
        public readonly long   ChaosInjections;             // 카오스 결함 주입 누적
        public readonly long   ChaosLivelockEscapes;        // ★Think-Beyond 락프리 스핀 상한 라이브락 탈출 누적
        public readonly long   ChaosTornStateBlocks;        // ★Think-Beyond torn-state seqlock fail-closed 차단 누적
        public readonly long   ChaosRecoveryState;          // 0 ready / 1 cooldown / 2 frozen(복구 스로틀러)
        public readonly long   ChaosReplaySeed;             // 결정론 재현 시드(하위 32비트·리플레이 재현용)
        public readonly long   ChaosSafeHoldActive;         // 1=카오스가 무장 하향 중(sanitizer∨recovery∨guard)
        // v10.0.0 — Genesis Live Ignition Gateway(실자본 사출 최종 빗장).
        public readonly long   IgnitionDualKeyWired;        // 1=이중 물리 키(ValveWired ∧ LiveArmingWired) 결속
        public readonly long   IgnitionState;               // 0 dormant / 1 armed-open / 2 vetoed
        public readonly long   IgnitionVetoes;              // 점화 거부 누적(방어선/키 실패)
        public readonly long   IgnitionLastVetoCode;        // 최근 veto 포렌식 비트(밸브/무장/방어선/펄스)
        public readonly long   IgnitionAuditRecords;        // 포렌식 감사 기록 누적(µs+해시)
        public readonly long   HardwareKeyPulseAlive;       // 1=물리 키 펄스 생존(50ms 심박)
        public readonly long   IgnitionDoubleDispatchBlocks; // ★Think-Beyond 이중 사출 봉쇄 누적
        public readonly long   IgnitionSealedEmissions;     // 봉인 실물 사출 누적
        // v10.1.0 — Live Flight Certification & Mock Dry-Run Fabric.
        public readonly long   DryRunActive;                // 1=드라이런 스코프(가상 미러 라우팅)
        public readonly long   MockVirtualFills;            // 가상 체결 누적
        public readonly long   MockLastSlippageBps;         // 최근 가상 슬리피지(bps)
        public readonly long   MockLastFillRttNanos;        // 최근 가상 체결 RTT(ns)
        public readonly long   CertScore;                   // 전방위 인증 점수(0~100)
        public readonly long   CertSealValid;               // 1=유효 봉인(100/100·무장 허용)
        public readonly long   CertSeal;                    // 인증 봉인 하위 32비트(포렌식)
        public readonly long   ScopeTransitionBounces;      // ★Think-Beyond 스코프 전환 Bounce+스테일 거부 누적
        // v10.1.0 Final Packaging — Nano SLA & Release Attestation.
        public readonly long   SlaP9999Nanos;              // 나노 SLA P99.99 지연(ns·지터 필터 후)
        public readonly long   SlaGateVeto;                // 1=SLA 게이트키퍼 트립(P99.99 이탈·발주 차단)
        public readonly long   SlaJitterRejects;           // ★Think-Beyond 디스케줄 아티팩트 배제 누적
        public readonly long   JitterRatePermille;         // 측정 지터(배제)율(‰)
        public readonly long   JitterRegressionTripped;    // ★Think-Beyond 1=측정 환경 신뢰불가 회귀 래치
        public readonly long   ReleaseSealValid;           // 1=릴리스 아티팩트 SHA-256 봉인 검증 성공
        public readonly long   ReleaseSealHex;             // 릴리스 봉인 하위 32비트(포렌식 헥스)
        public readonly long   MasterReleaseCertified;     // 1=SLA통과∧봉인유효∧지터정상(마스터 릴리스 인증)
        // v10.1.0 Ops — Operational Runbook & Host Tuning & Canary.
        public readonly long   SignoffPassed;              // 1=사전 점화 서명 전 항목 PASS(신선)
        public readonly long   SignoffFailMask;            // 서명 실패 항목 비트마스크(포렌식)
        public readonly long   CanaryCapUtilPermille;      // 카나리 캡 집행율(‰)
        public readonly long   CanaryDowngraded;           // 1=슬리피지 임계 초과 → 드라이런 강등
        public readonly long   HostTimerResNanos;          // OS 타이머 해상도(ns·튜닝 목표 ≤0.5ms)
        public readonly long   HostDriftTripped;           // ★Think-Beyond 1=호스트 환경 드리프트 확정(안전 강등)
        public readonly long   PanicTripped;               // 1=운용자 비상 탈출(패닉 스위치) 격발
        public readonly long   CanaryStagingReady;         // 1=서명∧호스트튜닝∧무드리프트∧무강등∧무패닉(카나리 준비)
        // v10.2.0 Toss — 토스증권 Open API 포트폴리오 동기화(조회 전용).
        public readonly long   TossConnected;              // 1=REST 동기화∧WS 스트림 정상(read-only)
        public readonly long   TossTotalEvalRaw;           // 총 평가금액(raw)
        public readonly long   TossTotalPnlRaw;            // 총 평가손익(raw)
        public readonly long   TossWsRttMicros;            // WebSocket RTT(µs)
        public readonly long   TossTokenFreshSec;          // access token 유효(1)/만료·갱신중(0) — 신선도 지표
        public readonly long   TossSeqHolesRecovered;      // ★Sequence Hole 확증 복구 횟수
        public readonly long   TossSeqProvisional;         // 1=시퀀스 잠정(hole 미복구·fail-closed)
        public readonly long   TossOrderVetoes;            // ★비인가 발주/변조 시도 하드 거부 카운트(암흑자색)
        // v10.2.0 Universal Broker Mesh — 다중 증권사 통합(1920 셀 여유 슬롯·추가 확장 없음).
        public readonly long   ActiveBrokerId;             // 현재 활성 Primary 증권사(BrokerId)
        public readonly long   CrossBrokerFallbacks;       // 교차-폴백 스위칭 발생 횟수
        public readonly long   UniversalWsRttMicros;       // 활성 브로커 범용 WS RTT(µs)
        public readonly long   MeshTornDrifts;             // ★증권사 간 타임스탬프 찢어짐(torn drift) 탐지 횟수
        // v11.0.0 Genesis Fleet Production — 실물 발주 게이트웨이.
        public readonly long   LiveDispatchArmed;          // 1=실물 발주 락 해제(liveArmed ∧ 실주문 어댑터 결속)
        public readonly long   OrderApiTimeouts;           // 증권사 API 발주 타임아웃 횟수
        public readonly long   LiveFillSlippageBps;        // 실체결 슬리피지(bps)
        public readonly long   ExecSecretHeapTainted;      // ★1=발주 시크릿 힙 오염(소멸 실패·암흑자색)

        public RingTelemetry(long published, long forwarded, long gatedOut, long dropped,
                             long writerCollisions, double throughputPerSec, long sendErrors = 0L,
                             long scenarioVersion = 0L, long scenarioWriterCollisions = 0L, long scenarioFrozen = 0L,
                             long alphaFactoryCycles = 0L, long alphaFactorySigned = 0L, long alphaFactoryRejected = 0L,
                             double alphaFactoryRecentRejRate = 0.0, long alphaFactorySuspended = 0L,
                             long alphaPipelineMode = 0L, long alphaPromoted = 0L, double alphaSigningSuccessRate = 0.0,
                             long ignitionStageCode = 0L, long slippageRaw = 0L, long slippageSigmaRaw = 0L, long execDowngrades = 0L,
                             long ledgerQueueDepth = 0L, long ledgerCapacity = 0L, double ledgerAvgPersistMicros = 0.0, long ledgerWriteFailures = 0L,
                             long recoveryStateCode = 0L, long recoveryBacklog = 0L, long probeRttMs = 0L, long probeSlippageSigmaRaw = 0L,
                             long virtualTimeTicks = 0L, long chaosFlags = 0L, long pbtPasses = 0L, long virtualQueueDepth = 0L,
                             long kiwoomLinkCode = 0L, long tossLinkCode = 0L, long tossTokenRemainSec = 0L,
                             long endpointFirewallBlocks = 0L, long productionLiveMask = 0L,
                             long newsRatePerMin = 0L, long marketSentimentRaw = 0L, long semanticShockActive = 0L,
                             long sentimentBreakerTripped = 0L, long newsUnanalyzedCount = 0L, long mentorFsmCode = 0L,
                             long marginUtilizationRaw = 0L, long reservationRejects = 0L, long shadowRingNodeMask = 0L,
                             long mmfLatencyMicros = 0L, long fabricGuardMask = 0L,
                             long blackboxSaturationRaw = 0L, long blackboxRatePerSec = 0L,
                             long blackboxDumpState = 0L, long blackboxDroppedSealed = 0L,
                             long accFeeKrwRaw = 0L, long accFeeUsdRaw = 0L, long accCarryErrorRaw = 0L,
                             long accLatchDepth = 0L, long accStateCode = 0L,
                             long shadowModeActive = 0L, long shadowFillRttMicros = 0L, long cutoverStateCode = 0L,
                             long canaryDeviationRaw = 0L, long dualTrackDriftRaw = 0L,
                             long genesisStateCode = 0L, long genesisDepositKrwRaw = 0L, long canaryNetDriftRaw = 0L,
                             long blastRadiusCeilingRaw = 0L, long killSwitchStateCode = 0L, long deadmanStateCode = 0L,
                             long sessionLatencyMicros = 0L, long sessionHealthCode = 0L, long failoverActiveChannel = 0L,
                             long failoverCount = 0L, long threadPoolAvailabilityMilli = 0L, long orphanInFlight = 0L,
                             long blackSwanDefenseCode = 0L,
                             long imbalanceScoreRaw = 0L, long liquiditySlicesEmitted = 0L, long orderThrottleCount = 0L,
                             long slippageSavedBps = 0L, long toxicityCode = 0L,
                             long strategyGeneration = 0L, long strategyBuildTicks = 0L, long stateCarryRttNs = 0L,
                             long zombieAssemblyCount = 0L, long orphanEventsRejected = 0L,
                             long activeStrategyCount = 0L, long sealedLaneCount = 0L, long nettingSavedCostRaw = 0L,
                             long washTradeRejections = 0L, long resonanceMetric = 0L, long capitalSaturationBps = 0L,
                             long logPersistBytesPerSec = 0L, long logQueueDepth = 0L, long logDroppedTotal = 0L,
                             long logCompressionBps = 0L, long kernelJitterMicros = 0L, long kernelSafeHold = 0L,
                             long nicDropsEstimate = 0L, long ioDefenseStatus = 0L,
                             long rioBypassActive = 0L, long rioReceiveLatencyNanos = 0L, long rioFallbackActive = 0L,
                             long rioFramesReceived = 0L, long playbackStateCode = 0L, long playbackPlayed = 0L,
                             long playbackTotalFrames = 0L, double playbackFps = 0.0, long playbackIntegrityRaw = 0L,
                             long causalWeaveStateCode = 0L, long causalRingEvictions = 0L,
                             long causalRingSaturationPermille = 0L, long causalFuseTripped = 0L,
                             long rioProbeSupported = 0L, long rioProbeAdapterCount = 0L,
                             long rioProbeMaxRcvBufBytes = 0L, long rioProbeRecommendFallback = 0L,
                             long auditLedgerIntegrityCode = 0L, long auditLedgerCount = 0L,
                             long auditLedgerSealed = 0L, long auditLedgerHeadHash = 0L,
                             long portfolioVar99Krw = 0L, long portfolioStressLossKrw = 0L, double varGershgorinBound = 0.0,
                             long varRidgeInjections = 0L, long riskValveState = 0L, long covGuardState = 0L,
                             long cpuPCoreCount = 0L, long cpuPinnedThreads = 0L, long cpuMigrationEvents = 0L, long cpuPinFailures = 0L,
                             long thermalEffectiveFreqPermille = 0L, long thermalThrottleHolds = 0L, long thermalSafeHold = 0L,
                             long cacheCrossCoreWrites = 0L, long cacheCoherenceFuse = 0L,
                             long preFlightScore = 0L, long preFlightForensic = 0L,
                             long armingLeaseRemainingSec = 0L, long armingLeasePermille = 0L,
                             long deadManHeartbeatAgeMs = 0L, long deadManTripped = 0L,
                             long fatFingerFrozen = 0L, long fatFingerVetoes = 0L, long fatFingerForensic = 0L,
                             long avxAccelTier = 0L, long simdAlphaLatencyNanos = 0L, long simdAlphaMilli = 0L, long snrNoiseBlocks = 0L,
                             long avxWidthDowngrades = 0L, long zmmWarmKeepAlives = 0L, long avxCalibrations = 0L, long simdAlignedLoads = 0L,
                             long quantumVaultArmed = 0L, long quantumVaultBreached = 0L, long honeyDecoyTripped = 0L, long antiHookState = 0L,
                             long infraCouplingCount = 0L, long rcuConfigVersion = 0L, long secureZeroVerifyFailures = 0L, long vaultZeroizedBuffers = 0L,
                             long sorRoutingActive = 0L, long sorRoutes = 0L, long sorLastRoutingCode = 0L, long icebergHiddenQtyRaw = 0L,
                             long icebergsDetected = 0L, long queueFillImminencePermille = 0L, long sorSpoofBlocks = 0L, long liquidityAuthenticityPermille = 0L,
                             long chaosSanitizerState = 0L, long chaosFaultMask = 0L, long chaosInjections = 0L, long chaosLivelockEscapes = 0L,
                             long chaosTornStateBlocks = 0L, long chaosRecoveryState = 0L, long chaosReplaySeed = 0L, long chaosSafeHoldActive = 0L,
                             long ignitionDualKeyWired = 0L, long ignitionState = 0L, long ignitionVetoes = 0L, long ignitionLastVetoCode = 0L,
                             long ignitionAuditRecords = 0L, long hardwareKeyPulseAlive = 0L, long ignitionDoubleDispatchBlocks = 0L, long ignitionSealedEmissions = 0L,
                             long dryRunActive = 0L, long mockVirtualFills = 0L, long mockLastSlippageBps = 0L, long mockLastFillRttNanos = 0L,
                             long certScore = 0L, long certSealValid = 0L, long certSeal = 0L, long scopeTransitionBounces = 0L,
                             long slaP9999Nanos = 0L, long slaGateVeto = 0L, long slaJitterRejects = 0L, long jitterRatePermille = 0L,
                             long jitterRegressionTripped = 0L, long releaseSealValid = 0L, long releaseSealHex = 0L, long masterReleaseCertified = 0L,
                             long signoffPassed = 0L, long signoffFailMask = 0L, long canaryCapUtilPermille = 0L, long canaryDowngraded = 0L,
                             long hostTimerResNanos = 0L, long hostDriftTripped = 0L, long panicTripped = 0L, long canaryStagingReady = 0L,
                             long tossConnected = 0L, long tossTotalEval = 0L, long tossTotalPnl = 0L, long tossWsRtt = 0L, long tossTokenFresh = 0L, long tossSeqRecov = 0L, long tossSeqProv = 0L, long tossVetoes = 0L,
                             long activeBrokerId = 0L, long crossBrokerFallbacks = 0L, long universalWsRtt = 0L, long meshTornDrifts = 0L,
                             long liveDispatchArmed = 0L, long orderApiTimeouts = 0L, long liveFillSlippage = 0L, long execSecretTainted = 0L)
        {
            Published        = published;
            Forwarded        = forwarded;
            GatedOut         = gatedOut;
            Dropped          = dropped;
            WriterCollisions = writerCollisions;
            ThroughputPerSec = throughputPerSec;
            SendErrors       = sendErrors;
            ScenarioVersion          = scenarioVersion;
            ScenarioWriterCollisions = scenarioWriterCollisions;
            ScenarioFrozen           = scenarioFrozen;
            AlphaFactoryCycles   = alphaFactoryCycles;
            AlphaFactorySigned   = alphaFactorySigned;
            AlphaFactoryRejected = alphaFactoryRejected;
            AlphaFactoryRecentRejRate = alphaFactoryRecentRejRate;
            AlphaFactorySuspended     = alphaFactorySuspended;
            AlphaPipelineMode         = alphaPipelineMode;
            AlphaPromoted             = alphaPromoted;
            AlphaSigningSuccessRate   = alphaSigningSuccessRate;
            IgnitionStageCode         = ignitionStageCode;
            SlippageRaw               = slippageRaw;
            SlippageSigmaRaw          = slippageSigmaRaw;
            ExecDowngrades            = execDowngrades;
            LedgerQueueDepth          = ledgerQueueDepth;
            LedgerCapacity            = ledgerCapacity;
            LedgerAvgPersistMicros    = ledgerAvgPersistMicros;
            LedgerWriteFailures       = ledgerWriteFailures;
            RecoveryStateCode         = recoveryStateCode;
            RecoveryBacklog           = recoveryBacklog;
            ProbeRttMs                = probeRttMs;
            ProbeSlippageSigmaRaw     = probeSlippageSigmaRaw;
            VirtualTimeTicks          = virtualTimeTicks;
            ChaosFlags                = chaosFlags;
            PbtPasses                 = pbtPasses;
            VirtualQueueDepth         = virtualQueueDepth;
            KiwoomLinkCode            = kiwoomLinkCode;
            TossLinkCode              = tossLinkCode;
            TossTokenRemainSec        = tossTokenRemainSec;
            EndpointFirewallBlocks    = endpointFirewallBlocks;
            ProductionLiveMask        = productionLiveMask;
            NewsRatePerMin            = newsRatePerMin;
            MarketSentimentRaw        = marketSentimentRaw;
            SemanticShockActive       = semanticShockActive;
            SentimentBreakerTripped   = sentimentBreakerTripped;
            NewsUnanalyzedCount       = newsUnanalyzedCount;
            MentorFsmCode             = mentorFsmCode;
            MarginUtilizationRaw      = marginUtilizationRaw;
            ReservationRejects        = reservationRejects;
            ShadowRingNodeMask        = shadowRingNodeMask;
            MmfLatencyMicros          = mmfLatencyMicros;
            FabricGuardMask           = fabricGuardMask;
            BlackboxSaturationRaw     = blackboxSaturationRaw;
            BlackboxRatePerSec        = blackboxRatePerSec;
            BlackboxDumpState         = blackboxDumpState;
            BlackboxDroppedSealed     = blackboxDroppedSealed;
            AccFeeKrwRaw              = accFeeKrwRaw;
            AccFeeUsdRaw              = accFeeUsdRaw;
            AccCarryErrorRaw          = accCarryErrorRaw;
            AccLatchDepth             = accLatchDepth;
            AccStateCode              = accStateCode;
            ShadowModeActive          = shadowModeActive;
            ShadowFillRttMicros       = shadowFillRttMicros;
            CutoverStateCode          = cutoverStateCode;
            CanaryDeviationRaw        = canaryDeviationRaw;
            DualTrackDriftRaw         = dualTrackDriftRaw;
            GenesisStateCode          = genesisStateCode;
            GenesisDepositKrwRaw      = genesisDepositKrwRaw;
            CanaryNetDriftRaw         = canaryNetDriftRaw;
            BlastRadiusCeilingRaw     = blastRadiusCeilingRaw;
            KillSwitchStateCode       = killSwitchStateCode;
            DeadmanStateCode          = deadmanStateCode;
            SessionLatencyMicros        = sessionLatencyMicros;
            SessionHealthCode           = sessionHealthCode;
            FailoverActiveChannel       = failoverActiveChannel;
            FailoverCount               = failoverCount;
            ThreadPoolAvailabilityMilli = threadPoolAvailabilityMilli;
            OrphanInFlight              = orphanInFlight;
            BlackSwanDefenseCode        = blackSwanDefenseCode;
            ImbalanceScoreRaw           = imbalanceScoreRaw;
            LiquiditySlicesEmitted      = liquiditySlicesEmitted;
            OrderThrottleCount          = orderThrottleCount;
            SlippageSavedBps            = slippageSavedBps;
            ToxicityCode                = toxicityCode;
            StrategyGeneration          = strategyGeneration;
            StrategyBuildTicks          = strategyBuildTicks;
            StateCarryRttNs             = stateCarryRttNs;
            ZombieAssemblyCount         = zombieAssemblyCount;
            OrphanEventsRejected        = orphanEventsRejected;
            ActiveStrategyCount         = activeStrategyCount;
            SealedLaneCount             = sealedLaneCount;
            NettingSavedCostRaw         = nettingSavedCostRaw;
            WashTradeRejections         = washTradeRejections;
            ResonanceMetric             = resonanceMetric;
            CapitalSaturationBps        = capitalSaturationBps;
            LogPersistBytesPerSec       = logPersistBytesPerSec;
            LogQueueDepth               = logQueueDepth;
            LogDroppedTotal             = logDroppedTotal;
            LogCompressionBps           = logCompressionBps;
            KernelJitterMicros          = kernelJitterMicros;
            KernelSafeHold              = kernelSafeHold;
            NicDropsEstimate            = nicDropsEstimate;
            IoDefenseStatus             = ioDefenseStatus;
            RioBypassActive              = rioBypassActive;
            RioReceiveLatencyNanos       = rioReceiveLatencyNanos;
            RioFallbackActive            = rioFallbackActive;
            RioFramesReceived            = rioFramesReceived;
            PlaybackStateCode            = playbackStateCode;
            PlaybackPlayed               = playbackPlayed;
            PlaybackTotalFrames          = playbackTotalFrames;
            PlaybackFps                  = playbackFps;
            PlaybackIntegrityRaw         = playbackIntegrityRaw;
            CausalWeaveStateCode         = causalWeaveStateCode;
            CausalRingEvictions          = causalRingEvictions;
            CausalRingSaturationPermille = causalRingSaturationPermille;
            CausalFuseTripped            = causalFuseTripped;
            RioProbeSupported            = rioProbeSupported;
            RioProbeAdapterCount         = rioProbeAdapterCount;
            RioProbeMaxRcvBufBytes       = rioProbeMaxRcvBufBytes;
            RioProbeRecommendFallback    = rioProbeRecommendFallback;
            AuditLedgerIntegrityCode     = auditLedgerIntegrityCode;
            AuditLedgerCount             = auditLedgerCount;
            AuditLedgerSealed            = auditLedgerSealed;
            AuditLedgerHeadHash          = auditLedgerHeadHash;
            PortfolioVar99Krw            = portfolioVar99Krw;
            PortfolioStressLossKrw       = portfolioStressLossKrw;
            VarGershgorinBound           = varGershgorinBound;
            VarRidgeInjections           = varRidgeInjections;
            RiskValveState               = riskValveState;
            CovGuardState                = covGuardState;
            CpuPCoreCount                = cpuPCoreCount;
            CpuPinnedThreads             = cpuPinnedThreads;
            CpuMigrationEvents           = cpuMigrationEvents;
            CpuPinFailures               = cpuPinFailures;
            ThermalEffectiveFreqPermille = thermalEffectiveFreqPermille;
            ThermalThrottleHolds         = thermalThrottleHolds;
            ThermalSafeHold              = thermalSafeHold;
            CacheCrossCoreWrites         = cacheCrossCoreWrites;
            CacheCoherenceFuse           = cacheCoherenceFuse;
            PreFlightScore               = preFlightScore;
            PreFlightForensic            = preFlightForensic;
            ArmingLeaseRemainingSec      = armingLeaseRemainingSec;
            ArmingLeasePermille          = armingLeasePermille;
            DeadManHeartbeatAgeMs        = deadManHeartbeatAgeMs;
            DeadManTripped               = deadManTripped;
            FatFingerFrozen              = fatFingerFrozen;
            FatFingerVetoes              = fatFingerVetoes;
            FatFingerForensic            = fatFingerForensic;
            AvxAccelTier                 = avxAccelTier;
            SimdAlphaLatencyNanos        = simdAlphaLatencyNanos;
            SimdAlphaMilli               = simdAlphaMilli;
            SnrNoiseBlocks               = snrNoiseBlocks;
            AvxWidthDowngrades           = avxWidthDowngrades;
            ZmmWarmKeepAlives            = zmmWarmKeepAlives;
            AvxCalibrations              = avxCalibrations;
            SimdAlignedLoads             = simdAlignedLoads;
            QuantumVaultArmed            = quantumVaultArmed;
            QuantumVaultBreached         = quantumVaultBreached;
            HoneyDecoyTripped            = honeyDecoyTripped;
            AntiHookState                = antiHookState;
            InfraCouplingCount           = infraCouplingCount;
            RcuConfigVersion             = rcuConfigVersion;
            SecureZeroVerifyFailures     = secureZeroVerifyFailures;
            VaultZeroizedBuffers         = vaultZeroizedBuffers;
            SorRoutingActive             = sorRoutingActive;
            SorRoutes                    = sorRoutes;
            SorLastRoutingCode           = sorLastRoutingCode;
            IcebergHiddenQtyRaw          = icebergHiddenQtyRaw;
            IcebergsDetected             = icebergsDetected;
            QueueFillImminencePermille   = queueFillImminencePermille;
            SorSpoofBlocks               = sorSpoofBlocks;
            LiquidityAuthenticityPermille = liquidityAuthenticityPermille;
            ChaosSanitizerState          = chaosSanitizerState;
            ChaosFaultMask               = chaosFaultMask;
            ChaosInjections              = chaosInjections;
            ChaosLivelockEscapes         = chaosLivelockEscapes;
            ChaosTornStateBlocks         = chaosTornStateBlocks;
            ChaosRecoveryState           = chaosRecoveryState;
            ChaosReplaySeed              = chaosReplaySeed;
            ChaosSafeHoldActive          = chaosSafeHoldActive;
            IgnitionDualKeyWired         = ignitionDualKeyWired;
            IgnitionState                = ignitionState;
            IgnitionVetoes               = ignitionVetoes;
            IgnitionLastVetoCode         = ignitionLastVetoCode;
            IgnitionAuditRecords         = ignitionAuditRecords;
            HardwareKeyPulseAlive        = hardwareKeyPulseAlive;
            IgnitionDoubleDispatchBlocks = ignitionDoubleDispatchBlocks;
            IgnitionSealedEmissions      = ignitionSealedEmissions;
            DryRunActive                 = dryRunActive;
            MockVirtualFills             = mockVirtualFills;
            MockLastSlippageBps          = mockLastSlippageBps;
            MockLastFillRttNanos         = mockLastFillRttNanos;
            CertScore                    = certScore;
            CertSealValid                = certSealValid;
            CertSeal                     = certSeal;
            ScopeTransitionBounces       = scopeTransitionBounces;
            SlaP9999Nanos                = slaP9999Nanos;
            SlaGateVeto                  = slaGateVeto;
            SlaJitterRejects             = slaJitterRejects;
            JitterRatePermille           = jitterRatePermille;
            JitterRegressionTripped      = jitterRegressionTripped;
            ReleaseSealValid             = releaseSealValid;
            ReleaseSealHex               = releaseSealHex;
            MasterReleaseCertified       = masterReleaseCertified;
            SignoffPassed                = signoffPassed;
            SignoffFailMask              = signoffFailMask;
            CanaryCapUtilPermille        = canaryCapUtilPermille;
            CanaryDowngraded             = canaryDowngraded;
            HostTimerResNanos            = hostTimerResNanos;
            HostDriftTripped             = hostDriftTripped;
            PanicTripped                 = panicTripped;
            CanaryStagingReady           = canaryStagingReady;
            TossConnected                = tossConnected;
            TossTotalEvalRaw             = tossTotalEval;
            TossTotalPnlRaw              = tossTotalPnl;
            TossWsRttMicros              = tossWsRtt;
            TossTokenFreshSec            = tossTokenFresh;
            TossSeqHolesRecovered        = tossSeqRecov;
            TossSeqProvisional           = tossSeqProv;
            TossOrderVetoes              = tossVetoes;
            ActiveBrokerId               = activeBrokerId;
            CrossBrokerFallbacks         = crossBrokerFallbacks;
            UniversalWsRttMicros         = universalWsRtt;
            MeshTornDrifts               = meshTornDrifts;
            LiveDispatchArmed            = liveDispatchArmed;
            OrderApiTimeouts             = orderApiTimeouts;
            LiveFillSlippageBps          = liveFillSlippage;
            ExecSecretHeapTainted        = execSecretTainted;
        }

        /// <summary>활동 존재 여부(UI '데이터 없음' 판별). 양의 카운터로 판정 — default(struct)(전부 0)는 false.
        /// ★정직 한계: 정확히 1건만 발행되고 미드레인인 링(Published=0·나머지 0)은 false 로 보인다(극단 엣지·무해).</summary>
        public bool HasActivity => Published > 0 || Forwarded > 0 || GatedOut > 0
                                || Dropped > 0 || WriterCollisions > 0 || SendErrors > 0 || ScenarioVersion > 0;
        /// <summary>비상 경보 여부 — SendErrors/시나리오 동결 양수면 비상 확인 필수.</summary>
        public bool HasAlert    => SendErrors > 0 || ScenarioFrozen > 0;
        /// <summary>시나리오 변이 seqlock 활성(시뮬 페이지가 보고 중).</summary>
        public bool ScenarioActive => ScenarioVersion > 0;
        /// <summary>알파 공장 배선 여부(사이클 1 이상 완료된 경우). 미배선·미시작이면 false.</summary>
        public bool AlphaFactoryActive => AlphaFactoryCycles > 0;
        /// <summary>알파 공장 앰버 경보 — 자동/수동 일시정지 중이거나 최근 거부율 95% 이상(전략 고갈 의심).</summary>
        public bool AlphaFactoryAmber  => AlphaFactorySuspended != 0
                                       || (AlphaFactoryActive && AlphaFactoryRecentRejRate >= 0.95);
        /// <summary>진화 파이프라인이 Paused(동결/밸브 SUSPENDED 동기화) 모드인지.</summary>
        public bool AlphaPipelinePaused => AlphaPipelineMode == (long)TournamentPipelineMode.Paused;
        /// <summary>진화 파이프라인 모드 라벨(UI).</summary>
        public string AlphaPipelineModeName => AlphaPipelineMode switch
        {
            (long)TournamentPipelineMode.Idle     => "Idle",
            (long)TournamentPipelineMode.Testing  => "Testing",
            (long)TournamentPipelineMode.Signing  => "Signing",
            (long)TournamentPipelineMode.Deployed => "Deployed",
            (long)TournamentPipelineMode.Paused   => "Paused",
            _ => "Unknown"
        };
        // v4.87.0 — 집행 미시구조 UI 헬퍼.
        public string  IgnitionStageName    => IgnitionLock.StageLabel((IgnitionStage)IgnitionStageCode);
        public string  IgnitionStageCss     => IgnitionLock.StageCss((IgnitionStage)IgnitionStageCode);
        public bool    ExecMasterIgnited    => IgnitionStageCode == (long)IgnitionStage.MasterIgnited;
        public decimal SlippageDecimal      => SlippageRaw / (decimal)QMath.Scale;
        public decimal SlippageSigmaDecimal => SlippageSigmaRaw / (decimal)QMath.Scale;
        // v4.88.0 — 원장 메시 UI 헬퍼.
        public double  LedgerSaturationPct  => LedgerCapacity > 0 ? (double)LedgerQueueDepth / LedgerCapacity * 100.0 : 0.0;
        /// <summary>원장 오버플로 경보 — 큐 90% 이상 포화 또는 쓰기 실패 발생(전역 동결 신호).</summary>
        public bool    LedgerOverflowAlert  => LedgerSaturationPct >= 90.0 || LedgerWriteFailures > 0;
        // v4.90.0 — 웜 스타트 복구 UI 헬퍼.
        public RecoveryState RecoveryStage => (RecoveryState)RecoveryStateCode;
        public string RecoveryStateName => RecoveryStage switch
        {
            RecoveryState.Idle       => "Idle",
            RecoveryState.Validating => "Validating",
            RecoveryState.Draining   => "Draining",
            RecoveryState.Escalating => "Escalating",
            RecoveryState.Stable     => "Stable",
            RecoveryState.Frozen     => "Frozen",
            _ => "Unknown"
        };
        /// <summary>상태 배지 CSS 색: Stable=녹 / Draining·Escalating=황(점멸) / Frozen·Validating=회·적 / Idle=중립.</summary>
        public string RecoveryStateCss => RecoveryStage switch
        {
            RecoveryState.Stable     => "rec-stable",
            RecoveryState.Draining   => "rec-busy",
            RecoveryState.Escalating => "rec-busy",
            RecoveryState.Frozen     => "rec-frozen",
            RecoveryState.Validating => "rec-validating",
            _ => "rec-idle"
        };
        /// <summary>복구 진행 중(검증/드레인/승격) — 라이브 라우팅 잠금 신호와 동치.</summary>
        public bool RecoveryActive => RecoveryStage is RecoveryState.Validating or RecoveryState.Draining or RecoveryState.Escalating;
        public decimal ProbeSlippageSigmaDecimal => ProbeSlippageSigmaRaw / (decimal)QMath.Scale;
        // v4.91.0 — 카오스 그리드 UI 헬퍼.
        public bool ChaosActive          => VirtualTimeTicks > 0 || ChaosFlags != 0;
        public bool ChaosKiwoomProxyDown => (ChaosFlags & (long)ChaosFault.KiwoomProxyDown) != 0;
        public bool ChaosTossAuth401     => (ChaosFlags & (long)ChaosFault.TossAuth401)     != 0;
        public bool ChaosFxSpike         => (ChaosFlags & (long)ChaosFault.FxSpike)         != 0;
        public bool ChaosFxStale         => (ChaosFlags & (long)ChaosFault.FxStale)         != 0;
        /// <summary>가상 시간 경과를 초 단위(100ns tick → s)로. 1000× 압축 시간선의 진행 표시용.</summary>
        public double VirtualSeconds     => VirtualTimeTicks / (double)TimeSpan.TicksPerSecond;
        // v4.92.0 — 프로덕션 스왑 UI 헬퍼.
        public BrokerLinkState KiwoomLink => (BrokerLinkState)KiwoomLinkCode;
        public BrokerLinkState TossLink   => (BrokerLinkState)TossLinkCode;
        public static string LinkName(BrokerLinkState s) => s switch
        {
            BrokerLinkState.Disconnected => "Disconnected",
            BrokerLinkState.Connecting   => "Connecting",
            BrokerLinkState.Authenticated => "Authenticated",
            BrokerLinkState.SessionStale => "SessionStale",
            _ => "Unknown"
        };
        /// <summary>★실전 엔드포인트 접속 감지 — UI 는 적색 점멸 HIGH SENSITIVITY 고지.</summary>
        public bool ProductionLiveDetected => ProductionLiveMask != 0;
        /// <summary>모의/샌드박스 안전 상태(실전 미감지 ∧ 방화벽 청정).</summary>
        public bool SandboxSafe            => ProductionLiveMask == 0 && EndpointFirewallBlocks == 0;
        public bool EndpointFirewallClean  => EndpointFirewallBlocks == 0;
        /// <summary>연결 텔레메트리 활성(어느 브로커든 Disconnected 초과이거나 방화벽 활동).</summary>
        public bool ProdSwapActive => KiwoomLinkCode > 0 || TossLinkCode > 0 || ProductionLiveMask != 0 || EndpointFirewallBlocks != 0;
        // v4.93.0 — 정보 인텔리전스 UI 헬퍼.
        public decimal MarketSentiment       => MarketSentimentRaw / (decimal)QMath.Scale;
        public bool    SentimentBreakerActive => SentimentBreakerTripped != 0;   // ★Amber 오버레이 트리거(FSM 강등)
        public bool    SemanticShockEngaged    => SemanticShockActive != 0;
        public bool    NewsIntelActive         => NewsRatePerMin > 0 || MarketSentimentRaw != 0 || SemanticShockActive != 0 || SentimentBreakerTripped != 0 || MentorFsmCode > 0;
        /// <summary>시장 감성 방향(한국색상: 호재/상승 빨강 ≥+0.3, 악재/하락 파랑 ≤−0.3, 그 외 중립).</summary>
        public bool    MarketBullish           => MarketSentimentRaw >= 30_000_000L;
        public bool    MarketBearish           => MarketSentimentRaw <= -30_000_000L;
        public string  MentorFsmName => MentorFsmCode switch { 0 => "Idle", 1 => "Analyzing", 2 => "Advised", 3 => "Amber", _ => "Unknown" };
        // v4.94.0 — 초고속 집행·리스크 실드 UI 헬퍼.
        public decimal MarginUtilization    => MarginUtilizationRaw / (decimal)QMath.Scale;              // 0..1
        public double  MarginUtilizationPct => MarginUtilizationRaw / (double)QMath.Scale * 100.0;       // 0..100
        public double  MarginRemainingPct   => Math.Clamp(100.0 - MarginUtilizationPct, 0.0, 100.0);    // 잔여 증거금 비율
        public bool    MarginSaturated      => MarginUtilizationRaw >= 90_000_000L;                      // ≥90% 포화
        public bool    ReservationBreached  => ReservationRejects > 0;
        public bool    ShadowRingActive     => ShadowRingNodeMask != 0;
        public int     ShadowRingNodeCount  => System.Numerics.BitOperations.PopCount((ulong)ShadowRingNodeMask);
        public bool    MmfFabricActive      => MmfLatencyMicros > 0 || FabricGuardMask != 0;
        public bool    RiskShieldFrozen     => (FabricGuardMask & 1L) != 0;
        public bool    MmfFabricCorrupted   => (FabricGuardMask & 2L) != 0;
        /// <summary>★리스크/패브릭 가드 동결 — 화면 전체 적색 경고등 오버레이 트리거.</summary>
        public bool    FabricGuardTripped   => FabricGuardMask != 0;
        /// <summary>동결 사유 코드(오버레이 하드바인딩 고지).</summary>
        public string  FabricGuardReason => FabricGuardMask switch
        {
            0L => "",
            1L => "PRE-TRADE RISK SHIELD — 한도 초과 계좌 잠금",
            2L => "MMF FABRIC CORRUPTED — 공유메모리 오염 격리",
            _  => "RISK SHIELD + MMF FABRIC — 복합 가드 동결",
        };
        /// <summary>초고속 집행·리스크 레이어 활성(어느 지표든 값 존재).</summary>
        public bool    RiskExecActive => MarginUtilizationRaw > 0 || ReservationRejects > 0 || ShadowRingNodeMask != 0 || MmfLatencyMicros > 0 || FabricGuardMask != 0;
        // v4.95.0 — 무할당 블랙박스 UI 헬퍼.
        public decimal BlackboxSaturation    => BlackboxSaturationRaw / (decimal)QMath.Scale;              // 0..1
        public double  BlackboxSaturationPct => Math.Clamp(BlackboxSaturationRaw / (double)QMath.Scale * 100.0, 0.0, 100.0);
        public bool    BlackboxActive        => BlackboxSaturationRaw > 0 || BlackboxRatePerSec > 0 || BlackboxDumpState > 0;
        /// <summary>★감사 오디트 매그넷 활성(동결 봉인·덤프중·완료·실패) — 헥스덤프 암흑적색 감사 모드 트리거.</summary>
        public bool    BlackboxSealed        => BlackboxDumpState >= 1;
        public bool    BlackboxDumpFailed    => BlackboxDumpState == 3;
        public bool    BlackboxDumpDone      => BlackboxDumpState == 2;
        public string  BlackboxDumpStateName => BlackboxDumpState switch { 0 => "ARMED", 1 => "SEALED·DUMPING", 2 => "DUMPED", 3 => "DUMP FAILED", _ => "?" };
        // v4.96.0 — 실시간 청산 메쉬 UI 헬퍼.
        public decimal AccFeeKrw       => AccFeeKrwRaw / (decimal)QMath.Scale;
        public decimal AccFeeUsd       => AccFeeUsdRaw / (decimal)QMath.Scale;
        public decimal AccCarryError   => AccCarryErrorRaw / (decimal)QMath.Scale;
        public bool    AccActive       => AccStateCode >= 1 || AccFeeKrwRaw > 0 || AccFeeUsdRaw > 0 || AccLatchDepth > 0;
        public bool    AccFrozen       => AccStateCode == 2;
        /// <summary>★예수금 영점 조절 녹색 인디케이터 — 가동 ∧ 미동결 ∧ 캐리 오차 &lt;1raw(드리프트 0).</summary>
        public bool    AccZeroSynced   => AccStateCode == 1 && !AccFrozen && AccCarryErrorRaw < QMath.Scale;
        /// <summary>1raw 초과 디싱크/오버플로 동결 — 암흑적색 오버레이 트리거.</summary>
        public bool    AccDesyncFrozen => AccStateCode == 2;
        public string  AccStateName    => AccStateCode switch { 0 => "IDLE", 1 => "ZERO-SYNC", 2 => "DESYNC FROZEN", _ => "?" };
        // v4.97.0 — 섀도우/컷오버 UI 헬퍼(원자 배너 전환용 단일 불리언 게이트).
        public bool    IsShadowFlight  => ShadowModeActive != 0L;                                  // 섀도우(자본 안전)
        public bool    IsCutoverLive   => ShadowModeActive == 0L && CutoverStateCode >= 2L;        // 검역/실전(실 송출 라인)
        public bool    ShadowWired     => IsShadowFlight || IsCutoverLive;                         // 서브시스템 배선 여부
        public string  CutoverStateName => CutoverStateCode switch
        { 0 => "SHADOW", 1 => "ARMED-READY", 2 => "CANARY-QUARANTINE", 3 => "LIVE", 4 => "ROLLED-BACK", 5 => "FROZEN", _ => "?" };
        public double  ShadowFillRttMs => ShadowFillRttMicros / 1000.0;                            // 가상 체결 RTT(ms 표시)
        // v5.0.0 — 실전 총점화 UI 헬퍼.
        public string  GenesisStateName => GenesisStateCode switch { 0 => "PENDING", 1 => "SYNCED", 2 => "REFUSED", _ => "?" };
        public bool    GenesisSynced     => GenesisStateCode == 1L;
        public bool    KillSwitchTripped => KillSwitchStateCode != 0L;
        public string  DeadmanStateName  => DeadmanStateCode switch { 0 => "IDLE", 1 => "WATCHING", 2 => "REVERTED", _ => "?" };
        public bool    DeadmanReverted   => DeadmanStateCode == 2L;
        public bool    BlastRadiusActive => BlastRadiusCeilingRaw > 0L && BlastRadiusCeilingRaw != long.MaxValue;
        public decimal GenesisDepositKrw => GenesisDepositKrwRaw / (decimal)QMath.Scale;
        // v5.1.0 — 자율 라이브 감시망 UI 헬퍼.
        public bool    SessionComa       => SessionHealthCode != 0L;
        public bool    FailoverActive    => FailoverActiveChannel == 1L;                 // Standby 라우팅 중
        public bool    ChannelsDown      => FailoverActiveChannel == 2L;                 // 양채널 다운
        public string  ActiveChannelName => FailoverActiveChannel switch { 0 => "PRIMARY", 1 => "STANDBY", 2 => "NONE", _ => "?" };
        public double  ThreadPoolAvailability => ThreadPoolAvailabilityMilli / 1000.0;   // 0..1
        public bool    BlackSwanFrozen   => BlackSwanDefenseCode == 2L;
        public string  BlackSwanName     => BlackSwanDefenseCode switch { 0 => "NOMINAL", 1 => "ORPHANS", 2 => "FROZEN", _ => "?" };
        /// <summary>자율 감시망 종합 건강(청색 인디케이터 게이트) — Coma/페일오버/양채널다운/블랙스완 중 하나라도 있으면 false.</summary>
        public bool    WatchdogHealthy   => !SessionComa && FailoverActiveChannel == 0L && BlackSwanDefenseCode == 0L;
        // v5.2.0 — 미시 유동성 UI 헬퍼.
        public double  ImbalanceScore    => ImbalanceScoreRaw / (double)QMath.Scale;              // [-1,+1]·+매수우세
        public string  ToxicityName      => ToxicityCode switch { 0 => "CLEAR", 1 => "CAUTION", 2 => "TOXIC", _ => "?" };
        public bool    ToxicFlowActive   => ToxicityCode >= 1L;                                    // Caution 이상
        public bool    ToxicFlowFrozen   => ToxicityCode >= 2L;                                    // Toxic(강등 수렴)
        /// <summary>매수측 잔량 비중(0..100%) — 양방향 게이지 좌측 폭. 중립 50%.</summary>
        public double  BidDepthPercent   => 50.0 + ImbalanceScore * 50.0;
        // v5.3.0 — 무정지 전략 핫패치 UI 헬퍼.
        public bool    ZombieDetected    => ZombieAssemblyCount > 0L;
        public double  StateCarryRttMicros => StateCarryRttNs / 1000.0;   // ns→µs 표시
        // v5.4.0 — 다중 전략 인터리빙 & 네팅 UI 헬퍼.
        public bool    StrategyMuted       => SealedLaneCount > 0L;                       // 봉인/진압 발생 → amber 경고
        public decimal NettingSavedCost    => NettingSavedCostRaw / (decimal)QMath.Scale;  // 절감 비용(통화 단위)
        public double  CapitalSaturationPct => CapitalSaturationBps / 100.0;              // bps→% 표시
        // v5.5.0 — 로깅 패브릭 & 커널 지터 UI 헬퍼.
        public double  LogPersistMbPerSec  => LogPersistBytesPerSec / 1_048_576.0;        // B/s → MB/s
        public double  CompressionPct      => LogCompressionBps / 100.0;                  // bps → %(압축 후 비율)
        public bool    KernelHolding       => KernelSafeHold != 0L;                       // 안전 홀드 → amber
        public bool    IoFrozen            => IoDefenseStatus >= 2L;                       // 디스크 stall 동결 → 자주색
        // v6.0.0 Path Gamma — RIO/플레이백/CausalWeave UI 헬퍼.
        public bool    RioActive               => RioBypassActive != 0L;                    // ★청색 [RIO KERNEL BYPASS ACTIVE]
        public bool    RioFallback             => RioFallbackActive != 0L;                  // ★황색 폴백 강등
        public double  RioReceiveLatencyMicros => RioReceiveLatencyNanos / 1000.0;
        public PlaybackState PlaybackStage      => (PlaybackState)PlaybackStateCode;
        public string  PlaybackStateName        => PlaybackStage switch
        { PlaybackState.Idle => "IDLE", PlaybackState.Playing => "PLAYING", PlaybackState.Paused => "PAUSED",
          PlaybackState.Stepping => "STEPPING", PlaybackState.Completed => "COMPLETED", PlaybackState.Faulted => "FAULTED", _ => "?" };
        public bool    PlaybackRunning          => PlaybackStateCode is 1L or 3L;           // ★보라색 점멸 [DETERMINISTIC PLAYBACK IN PROGRESS]
        public bool    PlaybackFaulted          => PlaybackStateCode == 5L || CausalFuseTripped != 0L;
        public double  PlaybackProgress         => PlaybackTotalFrames > 0L ? (double)PlaybackPlayed / PlaybackTotalFrames : 0.0;
        public double  PlaybackProgressPct      => Math.Clamp(PlaybackProgress * 100.0, 0.0, 100.0);
        public decimal PlaybackIntegrity        => PlaybackIntegrityRaw / (decimal)QMath.Scale;   // 0..1
        public double  PlaybackIntegrityPct     => Math.Clamp(PlaybackIntegrityRaw / (double)QMath.Scale * 100.0, 0.0, 100.0);
        public CausalWeaveState CausalWeaveStatus => (CausalWeaveState)CausalWeaveStateCode;
        public bool    CausalFuse               => CausalFuseTripped != 0L;                 // ★암흑자색 오버레이(타임-와프 봉인)
        public bool    CausalEvicting           => CausalWeaveStateCode == 2L;
        public double  CausalRingSaturationPct  => CausalRingSaturationPermille / 10.0;      // ‰→%
        public string  CausalWeaveStateName      => CausalWeaveStatus switch
        { CausalWeaveState.Nominal => "NOMINAL", CausalWeaveState.Pressure => "PRESSURE", CausalWeaveState.Evicting => "EVICTING", CausalWeaveState.FuseTripped => "FUSE-TRIPPED", _ => "?" };
        /// <summary>Path Gamma 활성(RIO/폴백/플레이백/링 회수 중 하나라도) — UI '데이터 없음' 판별.</summary>
        public bool    PathGammaActive          => RioBypassActive != 0L || RioFallbackActive != 0L
                                                || PlaybackStateCode != 0L || CausalRingEvictions > 0L || RioFramesReceived > 0L;
        // v7.0.0 Phase 7 — 하드웨어 프로브 + 암호학적 감사 금고 UI 헬퍼.
        public bool    RioProbeOk               => RioProbeSupported != 0L;                  // ★청록 네온(RIO 지원)
        public bool    RioProbeFallback         => RioProbeRecommendFallback != 0L;          // 안심 강등 권고
        public double  RioProbeMaxRcvBufMB       => RioProbeMaxRcvBufBytes / (1024.0 * 1024.0);
        public bool    AuditVaultWired           => AuditLedgerCount > 0L || AuditLedgerIntegrityCode != 0L || AuditLedgerSealed != 0L;
        public bool    AuditIntact               => AuditLedgerIntegrityCode == 1L;           // ★청록 네온 [INTACT]
        public bool    AuditCorrupted            => AuditLedgerIntegrityCode == 2L;           // ★크림슨 [CORRUPTED]
        public bool    AuditSealed               => AuditLedgerSealed != 0L;                  // 트립 포렌식 봉인(비가역)
        public string  AuditIntegrityName        => AuditLedgerIntegrityCode switch
        { 1L => "INTACT", 2L => "CORRUPTED", _ => "UNVERIFIED" };
        // v7.2.0 — 실시간 포트폴리오 VaR & Gershgorin 특이점 방화벽 UI 헬퍼(한국색상은 razor 계층).
        public bool    RiskValveThrottling       => RiskValveState != 0L;                       // ★황색 [PORTFOLIO RISK ELEVATED - THROTTLING EXPOSURES]
        public bool    CovSingularityFuse        => CovGuardState == 2L;                         // ★암흑자색 오버레이(공분산 특이점 파열)
        public bool    CovRegularizing           => CovGuardState == 1L;                         // 적응형 리지 개입 중
        public bool    VarActive                 => PortfolioVar99Krw > 0L || VarRidgeInjections > 0L || RiskValveState != 0L;  // 미배선 판별(전부 0=미구동)
        public string  CovGuardStateName         => CovGuardState switch { 1L => "REGULARIZED", 2L => "SINGULAR-FUSE", _ => "NOMINAL" };
        // v7.3.0 — 하드웨어 RIO/P-Core/서멀/코히런스 UI 헬퍼(한국색상은 razor 계층).
        public bool    CpuPinActive              => CpuPinnedThreads > 0L && CpuPCoreCount > 0L;        // P-Core 물리 핀 활성
        public bool    ThermalThrottling         => ThermalSafeHold != 0L;                             // ★황색 [THERMAL/FREQ THROTTLE - SAFE HOLD]
        public bool    CacheCoherenceBreached    => CacheCoherenceFuse != 0L;                          // ★암흑자색 오버레이(캐시 코히런스 파열)
        public bool    HwCoreIsolationActive     => CpuPinActive || CpuPCoreCount > 0L || ThermalEffectiveFreqPermille is > 0L and < 1000L || CacheCrossCoreWrites > 0L;  // 미배선 판별(전부 0/기본=미구동)
        public string  HwPinStateName            => CpuPinActive ? "P-CORE PINNED" : (CpuPCoreCount > 0L ? "P-CORE DETECTED" : "UNPINNED");
        // v7.4.0 — Genesis Pre-Flight & 안전 패브릭 UI 헬퍼(한국색상은 razor 계층).
        public bool    PreFlightCertified        => PreFlightScore >= 100L;                              // ★청/녹 [PRE-FLIGHT CERTIFIED]
        public bool    PreFlightVeto             => PreFlightScore < 100L;                               // 1개라도 부적격 → 무장 거부
        public bool    ArmingLeaseActive         => ArmingLeaseRemainingSec > 0L;                        // 임대 유효
        public bool    ArmingLeaseExpiring       => ArmingLeaseActive && ArmingLeasePermille <= 200L;    // ★황색 점멸(만료 임접·잔여 ≤20%)
        public bool    DeadManTrippedFlag        => DeadManTripped != 0L;                                // ★암흑자색(데드맨 격발)
        public bool    FatFingerFrozenFlag       => FatFingerFrozen != 0L;                               // ★암흑자색(사출 방화벽 동결)
        public bool    SafetyFabricActive        => PreFlightScore > 0L || ArmingLeaseActive || DeadManHeartbeatAgeMs > 0L || FatFingerVetoes > 0L;  // 미배선 판별
        public bool    SafetyFabricBreached      => DeadManTrippedFlag || FatFingerFrozenFlag;           // 하드 차단(전체화면 오버레이)
        // v8.0.0 — AVX-512 SIMD 미시 알파 UI 헬퍼(한국색상은 razor 계층).
        public bool    Avx512Active              => AvxAccelTier >= 2L;                                  // ★청/보라 [AVX-512 ZMM ACTIVE]
        public bool    Avx2Fallback              => AvxAccelTier == 1L;                                  // ★황색 AVX2 강등
        public bool    SimdAlphaActive           => AvxCalibrations > 0L || SimdAlphaMilli != 0L || ZmmWarmKeepAlives > 0L;  // 미배선 판별
        public string  AvxTierName               => AvxAccelTier switch { 2L => "AVX-512 ZMM", 1L => "AVX2 YMM", _ => "SCALAR" };
        // v8.1.0 — 퀀텀 금고 & 인프라 해방 UI 헬퍼(한국색상은 razor 계층).
        public bool    QuantumVaultActive        => QuantumVaultArmed != 0L;                                // 감시 활성
        public bool    VaultBreach               => QuantumVaultBreached != 0L || HoneyDecoyTripped != 0L;  // ★암흑자색(비인가 접근/디코이 접촉)
        public bool    AntiHookTampered          => AntiHookState == 1L;                                    // ★황색(후킹 변조)
        public bool    InfraCouplingClean        => InfraCouplingCount > 0L && InfraCouplingCount <= 120L;  // 결합도 ≤ 120(Phase 84 목표 달성)
        // v9.0.0 — SOR & 빙산호가 UI 헬퍼.
        public bool    SorActive                 => SorRoutingActive != 0L || SorRoutes > 0L || IcebergsDetected > 0L;
        public bool    IcebergHunterActive       => IcebergsDetected > 0L || IcebergHiddenQtyRaw > 0L;
        public bool    SorSpoofing               => SorSpoofBlocks > 0L;                          // ★황색(스푸핑 차단)
        public bool    SorRoutingAnomaly         => SorSpoofBlocks >= 5L;                          // ★암흑자색(지속 스푸핑/슬리피지 비상)
        public bool    SorMarketAggressive       => (SorLastRoutingCode & (1L << 2)) != 0L;       // R_MARKET_AGGRESSIVE
        public bool    SorIcebergAbsorb          => (SorLastRoutingCode & (1L << 4)) != 0L;       // R_ICEBERG_ABSORB
        public bool    QuantumVaultActiveClean   => QuantumVaultActive && AntiHookState != 1L && QuantumVaultBreached == 0L;  // 금고 감시 정상
        // v9.1.0 — 극단적 카오스 결함 주입 샌드박스 UI 헬퍼.
        public bool    ChaosInjecting            => ChaosFaultMask != 0L;                                     // ★황/적 점멸(결함 주입 중)
        public bool    ChaosRecovering           => ChaosRecoveryState == 1L;                                 // 쿨다운·단계 재인증
        public bool    ChaosAnomaly              => ChaosLivelockEscapes > 0L || ChaosTornStateBlocks > 0L;   // ★암흑자색(라이브락/torn fail-closed)
        public bool    ChaosMonitorActive        => ChaosFaultMask == 0L && ChaosSafeHoldActive == 0L && !ChaosAnomaly;  // 청/녹 STANDBY
        public bool    ChaosSafeHolding          => ChaosSafeHoldActive != 0L;                                // 카오스가 무장 하향 중
        // v10.0.0 — Genesis Live Ignition Gateway UI 헬퍼.
        public bool    IgnitionActiveOpen        => IgnitionState == 1L && IgnitionDualKeyWired != 0L;        // 청/녹 [GENESIS IGNITION ACTIVE]
        public bool    IgnitionVetoed            => IgnitionState == 2L || IgnitionLastVetoCode != 0L;        // ★암흑자색(veto)
        public bool    HardwareKeyDisconnected   => (IgnitionLastVetoCode & (1L << 3)) != 0L;                 // 펄스 단선(황색)
        public bool    IgnitionDualKeyArmed      => IgnitionDualKeyWired != 0L && HardwareKeyPulseAlive != 0L; // 이중키+펄스 완전 결속
        public bool    IgnitionDormant           => IgnitionState == 0L && IgnitionVetoes == 0L;              // 미점화(기본)
        // v10.1.0 — Live Flight Certification & Mock Dry-Run UI 헬퍼.
        public bool    DryRunCertified           => DryRunActive != 0L && CertSealValid != 0L;               // 청/보라 [DRY-RUN CERTIFIED]
        public bool    DryRunUncertified         => DryRunActive != 0L && CertSealValid == 0L;               // 황색(드라이런·인증 미완)
        public bool    CertificationFailed       => CertSealValid == 0L && CertScore > 0L && CertScore < 100L; // ★암흑자색(인증 실패)
        public bool    ScopeContamination        => ScopeTransitionBounces > 0L;                             // ★암흑자색(스코프 오염 시도)
        public bool    MockMirrorActive          => MockVirtualFills > 0L;                                   // 가상 미러 가동
        // v10.1.0 Final Packaging — Nano SLA & Release Packaging UI 헬퍼.
        public bool    NanoSlaCertified          => MasterReleaseCertified != 0L;                              // 청/녹 [MASTER RELEASED - NANO SLA CERTIFIED]
        public bool    SlaViolation              => SlaGateVeto != 0L;                                         // ★암흑자색(SLA P99.99 이탈)
        public bool    JitterApproach            => JitterRegressionTripped == 0L && JitterRatePermille >= 50L; // 황색(지터 임접·회귀 전)
        public bool    JitterRegression          => JitterRegressionTripped != 0L;                             // ★암흑자색(측정 신뢰불가 회귀)
        public bool    ReleaseUncertified        => MasterReleaseCertified == 0L && SlaGateVeto == 0L && JitterRegressionTripped == 0L; // 황색(미인증·정상)
        // v10.1.0 Ops — Operational Runbook & Host Tuning & Canary UI 헬퍼.
        public bool    CanaryStaging             => CanaryStagingReady != 0L;                                  // 청/녹 [HOST TUNED - CANARY STAGING READY]
        public bool    CanaryExecuting           => CanaryDowngraded == 0L && CanaryCapUtilPermille > 0L;      // 황색 점멸(카나리 소액 집행 중)
        public bool    HostDrift                 => HostDriftTripped != 0L;                                    // ★암흑자색(호스트 환경 드리프트)
        public bool    OperatorPanic             => PanicTripped != 0L;                                        // ★암흑자색(패닉 스위치 격발)
        public bool    OpsAlarm                  => HostDriftTripped != 0L || PanicTripped != 0L || CanaryDowngraded != 0L; // 운용 비상(암흑자색)
        // v10.2.0 Toss — 관제 배지.
        public bool    TossApiConnected          => TossConnected != 0L;                         // 청/녹 [TOSS OPEN API CONNECTED - READ-ONLY PORTFOLIO SYNC]
        public bool    TossTokenRefreshing       => TossConnected != 0L && TossTokenFreshSec == 0L; // 황색(토큰 갱신/만료)
        public bool    TossUnauthorized          => TossOrderVetoes > 0L;                        // ★암흑자색(비인가 발주 시도 감지)
        public bool    TossSeqProvisionalState   => TossSeqProvisional != 0L;                    // 시퀀스 잠정(hole 미복구)
        // v10.2.0 Universal Broker Mesh — 관제 배지.
        public bool    UniversalMeshActive       => ActiveBrokerId != 0L && TossConnected != 0L;  // 청/녹 [UNIVERSAL MESH ACTIVE - READ-ONLY SYNC]
        public bool    UniversalFallbackActive   => CrossBrokerFallbacks > 0L;                    // 황색(교차-폴백 스위칭)
        public bool    UniversalTornAlarm        => MeshTornDrifts > 0L || TossOrderVetoes > 0L;  // ★암흑자색(타임스탬프 찢어짐/비인가 발주)
        public string  ActiveBrokerName          => ActiveBrokerId switch { 1L => "TOSS", 2L => "KIS", 3L => "LS", _ => "—" };
        // v11.0.0 Genesis Fleet Production — 관제 배지.
        public bool    GenesisFleetLive          => LiveDispatchArmed != 0L;                     // 청/적점멸 [GENESIS FLEET LIVE - REAL-CAPITAL DISPATCH ARMED]
        public bool    ExecSafeHoldAlarm         => OrderApiTimeouts > 0L || ExecSecretHeapTainted != 0L;  // ★암흑자색(API 타임아웃/시크릿 소멸실패 → Safe-Hold)
        public double  HostTimerResMicros        => HostTimerResNanos / 1000.0;                                // 타이머 해상도(µs 표시)
    }

    /// <summary>단일 라이터 seqlock 채널 — 캐시라인 격리. Publish(샘플러 단일 라이터)/TryRead(UI 락-프리·무할당).</summary>
    public sealed class RingTelemetryChannel
    {
        private const int CacheLine = 64;

        // Version(라이터 고빈도)과 Data(리더)를 서로 다른 64B 라인에 두고 앞 패딩+사이 갭으로 거짓 공유 차단.
        //   ★Size 는 Data(@128) 확장에 맞춘 여유값. v5.5.0 기준 필드 101개×8=808B → Data 128..936B ≤ 960(64배수·잔여 3슬롯=24B).
        //   (v5.4.0 93필드→896 잔여 3슬롯 → v5.5.0 로깅/지터 8필드 추가로 101필드 → Size 896→960 상향. ★필드 추가 시 128+N×8 ≤ Size(64배수) 유지 필수.)
        //   (v6.0.0 Path Gamma + v7.0.0 Phase 7: RIO/플레이백/CausalWeave/하드웨어프로브/감사금고 필드 추가로 122필드 → 128+122×8=1104 → Size 960→1152(64×18·잔여 6슬롯=48B).)
        //   (v7.2.0 VaR: 99%VaR·StressVaR·Gershgorin하한·리지주입·밸브·공분산가드 6필드 추가로 128필드 → 128+128×8=1152 → Size 1152→1216(64×19·잔여 8슬롯=64B).)
        //   (v7.3.0 하드웨어: P-Core수·핀스레드·마이그·핀실패·유효주파수·스로틀홀드·서멀홀드·크로스코어·코히런스퓨즈 9필드 추가로 137필드 → 128+137×8=1224 → Size 1216→1280(64×20·잔여 7슬롯=56B).)
        //   (v7.4.0 Genesis: PreFlight점수·포렌식·임대잔여초·임대‰·데드맨심박·데드맨트립·fatfinger동결·veto·포렌식 9필드 추가로 146필드 → 128+146×8=1296 → Size 1280→1344(64×21·잔여 6슬롯=48B).)
        //   (v8.0.0 AVX-512: 가속티어·SIMD알파지연·알파milli·SNR차단·폭강등·ZMM keepalive·조율·정렬로드 8필드 추가로 154필드 → 128+154×8=1360 → Size 1344→1408(64×22·잔여 6슬롯=48B).)
        //   (v8.1.0 퀀텀금고: 금고활성·브리치·디코이·후킹상태·결합도·RCU세대·제로검증실패·제로버퍼 8필드 추가로 162필드 → 128+162×8=1424 → Size 1408→1472(64×23·잔여 6슬롯=48B).)
        //   (v9.0.0 SOR/빙산: 라우팅활성·라우팅수·라우팅코드·빙산숨은물량·빙산포획·큐임접률·스푸핑차단·유동성진위 8필드 추가로 170필드 → 128+170×8=1488 → Size 1472→1536(64×24·잔여 6슬롯=48B).)
        //   (v9.1.0 카오스 샌드박스: 샌드박스상태·결함마스크·주입수·라이브락탈출·torn차단·복구상태·재현시드·SafeHold 8필드 추가로 178필드 → 128+178×8=1552 → Size 1536→1600(64×25·잔여 6슬롯=48B).)
        //   (v10.0.0 Genesis 점화: 이중키결속·점화상태·veto수·veto코드·감사기록·키펄스·이중사출봉쇄·봉인사출 8필드 추가로 186필드 → 128+186×8=1616 → Size 1600→1664(64×26·잔여 6슬롯=48B).)
        //   (v10.1.0 드라이런/인증: 드라이런활성·가상체결·슬리피지·RTT·인증점수·봉인유효·봉인·스코프Bounce 8필드 추가로 194필드 → 128+194×8=1680 → Size 1664→1728(64×27·잔여 6슬롯=48B).)
        //   (v10.1.0 Final Packaging: SLA P99.99·SLA veto·지터배제·지터율·지터회귀·릴리스봉인유효·릴리스봉인헥스·마스터인증 8필드 추가로 202필드 → 128+202×8=1744 → Size 1728→1792(64×28·잔여 6슬롯=48B).)
        //   (v10.1.0 Ops: 서명PASS·서명실패마스크·카나리집행율·카나리강등·타이머해상도·드리프트·패닉·카나리준비 8필드 추가로 210필드 → 128+210×8=1808 → Size 1792→1856(64×29·잔여 6슬롯=48B).)
        //   (v10.2.0 Toss: 연결·총평가·총손익·WS RTT·토큰신선도·SeqHole복구·시퀀스잠정·발주veto 8필드 추가로 218필드 → 128+218×8=1872 → Size 1856→1920(64×30·잔여 6슬롯=48B).)
        //   (v10.2.0 Universal Mesh: 활성브로커·교차폴백·범용RTT·torn drift 4필드 추가로 222필드 → 128+222×8=1904 → 기존 Size 1920 여유 슬롯 사용(64×30 유지·잔여 2슬롯=16B).)
        //   (v11.0.0 Genesis Fleet: 발주락해제·API타임아웃·실체결슬리피지·시크릿힙오염 4필드 추가로 226필드 → 128+226×8=1936 → Size 1920→1984(64×31·잔여 6슬롯=48B).)
        [StructLayout(LayoutKind.Explicit, Size = 1984)]
        private struct Cell
        {
            [FieldOffset(CacheLine)]     public long          Version;
            [FieldOffset(CacheLine * 2)] public RingTelemetry Data;
        }

        private int  _writerTid;   // DEBUG 단일 라이터 회귀 가드(릴리스 비용 0)
        private Cell _cell;

        /// <summary>현재 seqlock 커서(진단). 짝=안정, 홀=발행 중.</summary>
        public long Version => Volatile.Read(ref _cell.Version);

        /// <summary>텔레메트리 발행 — '단일 라이터'(샘플러)에서만. 홀→payload→짝. 무할당(구조체 복사).</summary>
        public void Publish(in RingTelemetry t)
        {
            AssertSingleWriter();
            long ver = _cell.Version;                       // 단일 라이터 → 자기 마지막 값
            Volatile.Write(ref _cell.Version, ver + 1);     // 홀 — 발행 시작(리더 무효화)
            _cell.Data = t;                                  // payload 구조체 복사(x64 store 순서 보존)
            Volatile.Write(ref _cell.Version, ver + 2);     // 짝 — 발행 완료
        }

        /// <summary>락-프리 무할당 독출 — UI 스레드. torn read 면 재시도(최대 8회), 끝내 경합이면 false +
        /// out=default(UI 가 직전 프레임 유지·코어 무블록). out 한정자 → 박싱·힙 0.</summary>
        public bool TryRead(out RingTelemetry t)
        {
            for (int i = 0; i < 8; i++)
            {
                long v1 = Volatile.Read(ref _cell.Version);
                if ((v1 & 1L) == 0L)                         // 짝수 = 안정 스냅샷
                {
                    RingTelemetry local = _cell.Data;        // 구조체 복사(스택)
                    long v2 = Volatile.Read(ref _cell.Version);
                    if (v1 == v2) { t = local; return true; } // 두 독출 사이 발행 없음 → 일관
                }
            }
            t = default;                                     // 경합 지속 → 직전 프레임 유지 신호(비블록)
            return false;
        }

        [Conditional("DEBUG")]
        private void AssertSingleWriter()
        {
            int tid  = Environment.CurrentManagedThreadId;
            int prev = Interlocked.CompareExchange(ref _writerTid, tid, 0);
            if (prev != 0 && prev != tid)
                throw new InvalidOperationException(
                    "RingTelemetryChannel: 단일 라이터 위반 — 둘째 Publish 스레드 감지(seqlock torn write 위험).");
        }
    }

    /// <summary>
    /// 링/디스패처 카운터를 주기 스냅샷해 채널에 발행하는 '단일 라이터' 샘플러. UI 는 채널만 TryRead(단방향).
    /// 처리량은 직전 샘플 대비 발행 델타/경과초로 산출(Stopwatch 단위·주입 가능 — 결정론 테스트). 무할당.
    /// </summary>
    public sealed class RingTelemetryPublisher
    {
        private readonly OrderDispatchRing       _ring;
        private readonly GatedOrderDispatcher    _dispatcher;
        private readonly RingTelemetryChannel    _channel;
        private readonly Func<long>              _nowTicks;   // 기본 Stopwatch.GetTimestamp(테스트는 주입)
        private readonly ScenarioSeqlockMonitor? _scenario;   // v4.63.0 — 시나리오 변이 seqlock 통합(옵션·기본 null)
        private readonly AutonomousAlphaFactory? _alphaFactory; // v4.73.0 — 자율 알파 공장 카운터 통합(옵션·기본 null)
        private readonly StrategyEvolutionPipeline? _pipeline;  // v4.86.0 — 진화 토너먼트 파이프라인(옵션·기본 null)
        private readonly IgnitionLock? _ignition;               // v4.87.0 — 점화 FSM 단계(옵션·기본 null)
        private readonly ExecutionMicrostructureGuard? _execGuard; // v4.87.0 — 슬리피지/σ/강등(옵션·기본 null)
        private readonly LedgerProtectionMesh? _ledger;         // v4.88.0 — 원장 보호 메시(옵션·기본 null)
        private readonly DeterministicWarmRestartEngine? _recovery; // v4.90.0 — 웜 스타트 복구 엔진(옵션·기본 null)
        private readonly DeterministicChaosGrid? _chaosGrid;        // v4.91.0 — 카오스 테스팅 그리드(옵션·기본 null)
        private readonly ProductionSwapController? _prodSwap;       // v4.92.0 — 프로덕션 스왑 컨트롤러(옵션·기본 null)
        private readonly NewsIntelController? _newsIntel;           // v4.93.0 — 뉴스 인텔 컨트롤러(옵션·기본 null)
        private readonly PreTradeRiskShield? _riskShield;           // v4.94.0 — 발주 전 리스크 실드(옵션·기본 null)
        private readonly MultiBrokerShadowRing? _shadowRing;        // v4.94.0 — 다중 브로커 섀도우 링(옵션·기본 null)
        private readonly MmfIpcFabric? _mmfFabric;                  // v4.94.0 — MMF 제로레이턴시 패브릭(옵션·기본 null)
        private readonly MmfFlightRecorder? _blackbox;              // v4.95.0 — 무할당 순환 블랙박스(옵션·기본 null)
        private readonly CrashResilientDumper? _dumper;             // v4.95.0 — 동결 원자 덤퍼(옵션·기본 null)
        private readonly LiveAccountingMesh? _accounting;           // v4.96.0 — 실시간 청산 메쉬(옵션·기본 null)
        private readonly ShadowExecutionGateway? _shadowGateway;    // v4.97.0 — 실거래 섀도우 집행 게이트웨이(옵션·기본 null)
        private readonly AtomicCutoverGate? _cutover;               // v4.97.0 — 원자적 컷오버 게이트(옵션·기본 null)
        private readonly LiveAccountGenesis? _genesis;              // v5.0.0 — 실전 계좌 결속(옵션·기본 null)
        private readonly CanaryLiveProbe? _canaryProbe;            // v5.0.0 — 카나리 종단 검증(옵션·기본 null)
        private readonly BlastRadiusLimiter? _blastRadius;         // v5.0.0 — 블래스트 반경 제한기(옵션·기본 null)
        private readonly RemoteKillSwitch? _killSwitch;            // v5.0.0 — 원격 킬스위치(옵션·기본 null)
        private readonly DeadmanReverseCutover? _deadman;         // v5.0.0 — 데드맨 역-컷오버(옵션·기본 null)
        private readonly LiveSessionWatchdog? _watchdog;          // v5.1.0 — 세션 하트비트 감시견(옵션·기본 null)
        private readonly AutonomousFailoverController? _failover; // v5.1.0 — 자율 채널 페일오버(옵션·기본 null)
        private readonly ThreadStarvationSensor? _starvation;     // v5.1.0 — 쓰레드 아사 센서(옵션·기본 null)
        private readonly AdaptiveOrphanReaper? _reaper;           // v5.1.0 — 적응형 고아주문 사냥꾼(옵션·기본 null)
        private readonly MicroLiquiditySlicer? _slicer;           // v5.2.0 — 미시 유동성 슬라이서(옵션·기본 null)
        private readonly AdaptiveOrderThrottle? _throttle;        // v5.2.0 — 적응형 오더 스로틀(옵션·기본 null)
        private readonly ToxicFlowSentinel? _toxic;               // v5.2.0 — 독약 유동성 센티널(옵션·기본 null)
        private readonly HotStrategyReloadGate? _reloadGate;      // v5.3.0 — 무정지 전략 핫패치 게이트(옵션·기본 null)
        private readonly StrategyStateCarrier? _stateCarrier;     // v5.3.0 — 상태 이월 캐리어(옵션·기본 null)
        private readonly ZombieAssemblyShield? _zombieShield;     // v5.3.0 — 좀비 어셈블리 방어막(옵션·기본 null)
        private readonly MultiStrategyInterleavedMesh? _mesh;     // v5.4.0 — 다중 전략 인터리빙 메쉬(옵션·기본 null)
        private readonly RealTimeBasketNetter? _netter;           // v5.4.0 — 실시간 바스켓 네터(옵션·기본 null)
        private readonly WashTradeGuard? _washGuard;              // v5.4.0 — 자전거래 차단막(옵션·기본 null)
        private readonly CrossStrategyContagionFirewall? _firewall; // v5.4.0 — 교차 전략 전염 방화벽(옵션·기본 null)
        private readonly ZeroAllocAsyncCompressedLogger? _logger;   // v5.5.0 — 무할당 압축 로거(옵션·기본 null)
        private readonly KernelJitterSpikeSentinel? _kernelJitter;  // v5.5.0 — 커널 지터 센티널(옵션·기본 null)
        private readonly IoBackpressureFirewall? _ioFirewall;       // v5.5.0 — I/O 역류 방화벽(옵션·기본 null)
        private readonly StatePlaybackSimulator? _playback;         // v6.0.0 — 결정론 플레이백 시뮬레이터(옵션·기본 null)
        private readonly RioKernelBypassMesh? _rioMesh;             // v6.0.0 — RIO 커널 바이패스 패브릭(옵션·기본 null)
        private readonly CausalWeaveClock? _weaveClock;             // v6.0.0 — CausalWeave 인과 시계·퓨즈(옵션·기본 null)
        private readonly CausalWeaveRing? _weaveRing;               // v6.0.0 — CausalWeave 증명-드롭 링(옵션·기본 null)
        private readonly RioHardwareProbe? _hwProbe;                // v7.0.0 — RIO 하드웨어 사전점검(옵션·기본 null)
        private readonly CausalAuditVault? _auditVault;             // v7.0.0 — 암호학적 감사 금고(옵션·기본 null)
        private readonly PortfolioRiskVaREngine? _varEngine;        // v7.2.0 — 실시간 VaR 엔진(옵션·기본 null·구동기가 갱신)
        private readonly CpuAffinityPinningGrid?      _cpuGrid;      // v7.3.0 — P-Core 핀 그리드(옵션·기본 null)
        private readonly ThermalFrequencyJitterGuard? _thermalGuard;// v7.3.0 — 서멀/주파수 스로틀 가드(옵션·기본 null)
        private readonly CacheLineCoherenceSentinel?  _coherence;   // v7.3.0 — 캐시라인 코히런스 센티널(옵션·기본 null)
        private readonly GenesisPreFlightCertifier?   _preFlight;   // v7.4.0 — 종합 점검 인증(옵션·기본 null·읽기 전용)
        private readonly AtomicArmingLease?           _armingLease; // v7.4.0 — 무장 임대(옵션·읽기 전용)
        private readonly DeadMansSwitchWatchdog?      _deadMan;     // v7.4.0 — 데드맨 왓치독(옵션·Sample 이 Beat)
        private readonly FatFingerVelocityInterlock?  _fatFinger;   // v7.4.0 — fat-finger/폭주 인터록(옵션·읽기 전용)
        private readonly Avx512MicroAlphaEngine?      _alphaEngine; // v8.0.0 — AVX-512 미시 알파 엔진(옵션·읽기)
        private readonly AlphaSignalNoiseQuantizer?   _noiseQuant;  // v8.0.0 — SNR 노이즈 양자화기(옵션·읽기)
        private readonly AvxExecutionWidthAutotuner?  _avxTuner;    // v8.0.0 — 실행폭 오토튜너(옵션·읽기)
        private readonly SimdAlignedRingVectorizer?   _alignedVec;  // v8.0.0 — 64B 정렬 벡터라이저(옵션·읽기)
        private readonly QuantumVaultSentinel?        _vault;       // v8.1.0 — 퀀텀 금고 센티널(옵션·읽기)
        private readonly HoneyKeyDecoyMesh?           _honeyMesh;   // v8.1.0 — 허니키 디코이(옵션·읽기)
        private readonly AntiHookingCodeIntegrityGuard? _antiHook;  // v8.1.0 — anti-hook 가드(옵션·읽기)
        private readonly VerifiedSecureZeroizer?      _zeroizer;    // v8.1.0 — 검증 제로라이저(옵션·읽기)
        private readonly Func<long>?                  _rcuVersion;  // v8.1.0 — RCU Config 스냅샷 세대(옵션)
        private readonly long                         _infraCoupling; // v8.1.0 — infra-scan 결합도(부팅 시점 값)
        private readonly SmartOrderRouterEngine?      _sor;         // v9.0.0 — SOR(옵션·읽기)
        private readonly IcebergHunterSentinel?       _iceberg2;    // v9.0.0 — 빙산 헌터(옵션·읽기)
        private readonly LiquidityAuthenticityVerifier? _authVerify; // v9.0.0 — 유동성 진위(옵션·읽기)
        private readonly QueuePositionEstimator?      _queueEst;    // v9.0.0 — 큐 위치(옵션·읽기)
        private readonly ChaosEngineSanitizer?        _chaos;       // v9.1.0 — 카오스 결함 주입(옵션·읽기)
        private readonly ChaosRecoveryRateLimiter?    _chaosRecov;  // v9.1.0 — 복구 스로틀러(옵션·읽기)
        private readonly ChaosResilientProgressGuard? _chaosGuard;  // v9.1.0 — 카오스 진행 보장 가드(옵션·읽기)
        private readonly DeterministicChaosReplayer?  _chaosReplay; // v9.1.0 — 결정론 재현기(옵션·읽기·시드)
        private readonly GenesisLiveIgnitionGateway?  _ignGateway;  // v10.0.0 — 점화 게이트웨이(옵션·읽기)
        private readonly IgnitionAuditTrailRecorder?  _ignAudit;    // v10.0.0 — 포렌식 감사 기록기(옵션·읽기)
        private readonly HardwareKeyPulseHeartbeatSentinel? _keyPulse; // v10.0.0 — 하드웨어 키 펄스(옵션·읽기)
        private readonly SealedEmissionInterlock?     _sealedEmit;  // v10.0.0 — 봉인 사출 인터락(옵션·읽기)
        private readonly MockExecutionOrderBookMirror? _mockMirror; // v10.1.0 — 가상 체결 미러(옵션·읽기)
        private readonly LiveFlightCertificationSealer? _certSealer; // v10.1.0 — 전방위 인증 봉인기(옵션·읽기·Sample 이 Certify)
        private readonly ScopeSealedTransitionBarrier? _scopeBar;   // v10.1.0 — 스코프 전환 장벽(옵션·읽기)
        private readonly LatencySlaGatekeeper? _slaGate;             // v10.1.0 Final — 나노 SLA 게이트키퍼(옵션·읽기·Sample 이 EvaluateSla)
        private readonly SlaJitterRegressionGuard? _jitterGuard;     // v10.1.0 Final — ★Think-Beyond 측정-무결성 게이트(옵션·읽기)
        private readonly ReleaseArtifactSha256Attestor? _releaseAttestor; // v10.1.0 Final — 릴리스 아티팩트 봉인기(옵션·읽기)
        private readonly IgnitionSignoffValidator? _signoffValidator; // v10.1.0 Ops — 사전 점화 서명(옵션·읽기·Sample 이 Certify)
        private readonly MicroCapitalCanaryGovernor? _canaryGov;      // v10.1.0 Ops — 카나리 소액 집행 거버너(옵션·읽기)
        private readonly HostEnvironmentDriftSentinel? _driftSentinel; // v10.1.0 Ops — ★Think-Beyond 드리프트 센티널(옵션·읽기·Probe)
        private readonly OperatorDuressPanicSwitch? _panicSwitch;     // v10.1.0 Ops — 운용자 비상 탈출 패닉 스위치(옵션·읽기)
        private readonly TossPortfolioSyncAdapter? _tossAdapter;      // v10.2.0 Toss — 포트폴리오 동기화 어댑터(옵션·읽기)
        private readonly TossWebSocketMarketDataGateway? _tossGateway; // v10.2.0 Toss — WS 시세 게이트웨이(옵션·읽기)
        private readonly TossTokenService? _tossToken;               // v10.2.0 Toss — 토큰 서비스(옵션·신선도 지표)
        private readonly UniversalBrokerGatewayMesh? _brokerMesh;    // v10.2.0 Universal — 다중 증권사 메쉬(옵션·읽기)
        private readonly CrossBrokerFallbackSentinel? _fallbackSentinel; // v10.2.0 Universal — 교차-폴백 라우터(옵션·읽기)
        private readonly CrossBrokerMonotonicTimeFence? _timeFence;  // v10.2.0 Universal — ★Think-Beyond 단조 타임펜스(옵션·읽기)
        private readonly UniversalOrderExecutionAdapter? _execAdapter;   // v11.0.0 Genesis Fleet — 실물 주문 어댑터(옵션·읽기)
        private readonly ExecutionTokenZeroizationGuard? _execZeroGuard; // v11.0.0 Genesis Fleet — 발주 시크릿 제로화 가드(옵션·읽기)
        private readonly LiveExecutionTelemetryBridge? _execBridge;      // v11.0.0 Genesis Fleet — 실체결 텔레메트리 브리지(옵션·읽기)
        private RioProbeReport _hwProbeReport;                      // 캐시된 프로브 리포트(콜드 1회)
        private bool _hwProbed;                                     // 프로브 수행 여부
        private long _lastAuditTick, _lastAuditEvict;               // 감사 기록 스로틀(δ=0)
        private readonly long _auditIntervalTicks;                  // 감사 기록 최소 간격(1s)
        private long _lastPublished;
        private long _lastBlackboxTotal;
        private long _lastLogCompBytes;   // v5.5.0 — 로그 영속 rate 델타 기준선
        private long _lastTicks;
        private int  _sampling;   // 단일 라이터 CAS 가드(동시 Sample 금지)

        public RingTelemetryPublisher(OrderDispatchRing ring, GatedOrderDispatcher dispatcher,
                                      RingTelemetryChannel channel, Func<long>? nowTicks = null,
                                      ScenarioSeqlockMonitor? scenario = null,
                                      AutonomousAlphaFactory? alphaFactory = null,
                                      StrategyEvolutionPipeline? pipeline = null,
                                      IgnitionLock? ignition = null,
                                      ExecutionMicrostructureGuard? execGuard = null,
                                      LedgerProtectionMesh? ledger = null,
                                      DeterministicWarmRestartEngine? recovery = null,
                                      DeterministicChaosGrid? chaosGrid = null,
                                      ProductionSwapController? prodSwap = null,
                                      NewsIntelController? newsIntel = null,
                                      PreTradeRiskShield? riskShield = null,
                                      MultiBrokerShadowRing? shadowRing = null,
                                      MmfIpcFabric? mmfFabric = null,
                                      MmfFlightRecorder? blackbox = null,
                                      CrashResilientDumper? dumper = null,
                                      LiveAccountingMesh? accounting = null,
                                      ShadowExecutionGateway? shadowGateway = null,
                                      AtomicCutoverGate? cutover = null,
                                      LiveAccountGenesis? genesis = null,
                                      CanaryLiveProbe? canaryProbe = null,
                                      BlastRadiusLimiter? blastRadius = null,
                                      RemoteKillSwitch? killSwitch = null,
                                      DeadmanReverseCutover? deadman = null,
                                      LiveSessionWatchdog? watchdog = null,
                                      AutonomousFailoverController? failover = null,
                                      ThreadStarvationSensor? starvation = null,
                                      AdaptiveOrphanReaper? reaper = null,
                                      MicroLiquiditySlicer? slicer = null,
                                      AdaptiveOrderThrottle? throttle = null,
                                      ToxicFlowSentinel? toxic = null,
                                      HotStrategyReloadGate? reloadGate = null,
                                      StrategyStateCarrier? stateCarrier = null,
                                      ZombieAssemblyShield? zombieShield = null,
                                      MultiStrategyInterleavedMesh? mesh = null,
                                      RealTimeBasketNetter? netter = null,
                                      WashTradeGuard? washGuard = null,
                                      CrossStrategyContagionFirewall? firewall = null,
                                      ZeroAllocAsyncCompressedLogger? logger = null,
                                      KernelJitterSpikeSentinel? kernelJitter = null,
                                      IoBackpressureFirewall? ioFirewall = null,
                                      StatePlaybackSimulator? playback = null,
                                      RioKernelBypassMesh? rioMesh = null,
                                      CausalWeaveClock? weaveClock = null,
                                      CausalWeaveRing? weaveRing = null,
                                      RioHardwareProbe? hwProbe = null,
                                      CausalAuditVault? auditVault = null,
                                      PortfolioRiskVaREngine? varEngine = null,
                                      CpuAffinityPinningGrid? cpuGrid = null,
                                      ThermalFrequencyJitterGuard? thermalGuard = null,
                                      CacheLineCoherenceSentinel? coherence = null,
                                      GenesisPreFlightCertifier? preFlight = null,
                                      AtomicArmingLease? armingLease = null,
                                      DeadMansSwitchWatchdog? deadMan = null,
                                      FatFingerVelocityInterlock? fatFinger = null,
                                      Avx512MicroAlphaEngine? alphaEngine = null,
                                      AlphaSignalNoiseQuantizer? noiseQuant = null,
                                      AvxExecutionWidthAutotuner? avxTuner = null,
                                      SimdAlignedRingVectorizer? alignedVec = null,
                                      QuantumVaultSentinel? vault = null,
                                      HoneyKeyDecoyMesh? honeyMesh = null,
                                      AntiHookingCodeIntegrityGuard? antiHook = null,
                                      VerifiedSecureZeroizer? zeroizer = null,
                                      Func<long>? rcuConfigVersion = null,
                                      long infraCouplingCount = 0L,
                                      SmartOrderRouterEngine? sor = null,
                                      IcebergHunterSentinel? icebergHunter = null,
                                      LiquidityAuthenticityVerifier? authVerify = null,
                                      QueuePositionEstimator? queueEst = null,
                                      ChaosEngineSanitizer? chaos = null,
                                      ChaosRecoveryRateLimiter? chaosRecovery = null,
                                      ChaosResilientProgressGuard? chaosGuard = null,
                                      DeterministicChaosReplayer? chaosReplayer = null,
                                      GenesisLiveIgnitionGateway? ignitionGateway = null,
                                      IgnitionAuditTrailRecorder? ignitionAudit = null,
                                      HardwareKeyPulseHeartbeatSentinel? keyPulse = null,
                                      SealedEmissionInterlock? sealedEmission = null,
                                      MockExecutionOrderBookMirror? mockMirror = null,
                                      LiveFlightCertificationSealer? certSealer = null,
                                      ScopeSealedTransitionBarrier? scopeBarrier = null,
                                      LatencySlaGatekeeper? slaGate = null,
                                      SlaJitterRegressionGuard? jitterGuard = null,
                                      ReleaseArtifactSha256Attestor? releaseAttestor = null,
                                      IgnitionSignoffValidator? signoffValidator = null,
                                      MicroCapitalCanaryGovernor? canaryGovernor = null,
                                      HostEnvironmentDriftSentinel? driftSentinel = null,
                                      OperatorDuressPanicSwitch? panicSwitch = null,
                                      TossPortfolioSyncAdapter? tossAdapter = null,
                                      TossWebSocketMarketDataGateway? tossGateway = null,
                                      TossTokenService? tossToken = null,
                                      UniversalBrokerGatewayMesh? brokerMesh = null,
                                      CrossBrokerFallbackSentinel? fallbackSentinel = null,
                                      CrossBrokerMonotonicTimeFence? timeFence = null,
                                      UniversalOrderExecutionAdapter? execAdapter = null,
                                      ExecutionTokenZeroizationGuard? execZeroGuard = null,
                                      LiveExecutionTelemetryBridge? execBridge = null)
        {
            _ring         = ring         ?? throw new ArgumentNullException(nameof(ring));
            _dispatcher   = dispatcher   ?? throw new ArgumentNullException(nameof(dispatcher));
            _channel      = channel      ?? throw new ArgumentNullException(nameof(channel));
            _nowTicks     = nowTicks     ?? Stopwatch.GetTimestamp;
            _scenario     = scenario;
            _alphaFactory = alphaFactory;
            _pipeline     = pipeline;
            _ignition     = ignition;
            _execGuard    = execGuard;
            _ledger       = ledger;
            _recovery     = recovery;
            _chaosGrid    = chaosGrid;
            _prodSwap     = prodSwap;
            _newsIntel    = newsIntel;
            _riskShield   = riskShield;
            _shadowRing   = shadowRing;
            _mmfFabric    = mmfFabric;
            _blackbox     = blackbox;
            _dumper       = dumper;
            _accounting   = accounting;
            _shadowGateway = shadowGateway;
            _cutover      = cutover;
            _genesis      = genesis;
            _canaryProbe  = canaryProbe;
            _blastRadius  = blastRadius;
            _killSwitch   = killSwitch;
            _deadman      = deadman;
            _watchdog     = watchdog;
            _failover     = failover;
            _starvation   = starvation;
            _reaper       = reaper;
            _slicer       = slicer;
            _throttle     = throttle;
            _toxic        = toxic;
            _reloadGate   = reloadGate;
            _stateCarrier = stateCarrier;
            _zombieShield = zombieShield;
            _mesh         = mesh;
            _netter       = netter;
            _washGuard    = washGuard;
            _firewall     = firewall;
            _logger       = logger;
            _kernelJitter = kernelJitter;
            _ioFirewall   = ioFirewall;
            _playback     = playback;
            _rioMesh      = rioMesh;
            _weaveClock   = weaveClock;
            _weaveRing    = weaveRing;
            _hwProbe      = hwProbe;
            _auditVault   = auditVault;
            _varEngine    = varEngine;
            _cpuGrid      = cpuGrid;
            _thermalGuard = thermalGuard;
            _coherence    = coherence;
            _preFlight    = preFlight;
            _armingLease  = armingLease;
            _deadMan      = deadMan;
            _fatFinger    = fatFinger;
            _alphaEngine  = alphaEngine;
            _noiseQuant   = noiseQuant;
            _avxTuner     = avxTuner;
            _alignedVec   = alignedVec;
            _vault        = vault;
            _honeyMesh    = honeyMesh;
            _antiHook     = antiHook;
            _zeroizer     = zeroizer;
            _rcuVersion   = rcuConfigVersion;
            _infraCoupling = infraCouplingCount;
            _sor          = sor;
            _iceberg2     = icebergHunter;
            _authVerify   = authVerify;
            _queueEst     = queueEst;
            _chaos        = chaos;
            _chaosRecov   = chaosRecovery;
            _chaosGuard   = chaosGuard;
            _chaosReplay  = chaosReplayer;
            _ignGateway   = ignitionGateway;
            _ignAudit     = ignitionAudit;
            _keyPulse     = keyPulse;
            _sealedEmit   = sealedEmission;
            _mockMirror   = mockMirror;
            _certSealer   = certSealer;
            _scopeBar     = scopeBarrier;
            _slaGate      = slaGate;
            _jitterGuard  = jitterGuard;
            _releaseAttestor = releaseAttestor;
            _signoffValidator = signoffValidator;
            _canaryGov    = canaryGovernor;
            _driftSentinel = driftSentinel;
            _panicSwitch  = panicSwitch;
            _tossAdapter  = tossAdapter;
            _tossGateway  = tossGateway;
            _tossToken    = tossToken;
            _brokerMesh   = brokerMesh;
            _fallbackSentinel = fallbackSentinel;
            _timeFence    = timeFence;
            _execAdapter  = execAdapter;
            _execZeroGuard = execZeroGuard;
            _execBridge   = execBridge;
            _auditIntervalTicks = Stopwatch.Frequency;             // 감사 기록 스로틀 1s
            if (_hwProbe is { } hp0) { _hwProbeReport = hp0.Probe(); _hwProbed = true; }   // ★콜드 1회 하드웨어 사전점검
            _lastBlackboxTotal = _blackbox?.TotalRecorded ?? 0L;   // ★C#11: 첫 Sample rate 델타 기준 초기화(허위 스파이크 제거)
            _lastLogCompBytes  = _logger?.CompBytesCum ?? 0L;      // v5.5.0 — 로그 rate 기준선(첫 샘플 허위 스파이크 제거)
            _lastPublished = _ring.Published;
            _lastTicks     = _nowTicks();
        }

        /// <summary>1회 스냅샷 → 채널 발행(단일 라이터 전용·CAS 가드·무할당). 동시 Sample 은 fail-closed throw.</summary>
        public void Sample()
        {
            if (Interlocked.CompareExchange(ref _sampling, 1, 0) != 0)
                throw new InvalidOperationException("RingTelemetryPublisher: 동시 Sample 위반 — 단일 라이터 전용");
            try
            {
                long published = _ring.Published;
                long now       = _nowTicks();
                long dPub      = published - _lastPublished;
                long dTick     = now - _lastTicks;
                // 음수 델타(역행) 방어 + 0-경과 div 차단. Stopwatch.Frequency = ticks/sec.
                double tps = (dTick > 0 && dPub > 0) ? dPub * (double)Stopwatch.Frequency / dTick : 0.0;
                // v4.63.0 — 시나리오 seqlock 카운터 합류(미배선이면 0). 무할당(long 3개 Volatile 읽기).
                long sVer = 0L, sWc = 0L, sFr = 0L;
                if (_scenario is { } sc) { sVer = sc.Version; sWc = sc.WriterCollisions; sFr = sc.Frozen; }
                // v4.73.0 — 자율 알파 공장 카운터 합류(미배선이면 0). int→long 캐스팅, 힙 할당 0.
                // v4.85.0 — 자기 제어 밸브 상태(롤링 거부율·일시정지) 합류.
                long afCycles = 0L, afSigned = 0L, afRejected = 0L, afSuspended = 0L;
                double afRejRate = 0.0;
                if (_alphaFactory is { } af)
                {
                    afCycles = af.CyclesCompleted; afSigned = af.StrategiesSigned; afRejected = af.GuardRejections;
                    afRejRate = af.RecentRejectionRate; afSuspended = af.IsSuspended ? 1L : 0L;
                }
                // v4.86.0 — 진화 파이프라인 FSM/승격/서명 지표 합류(미배선이면 0).
                long apMode = 0L, apPromoted = 0L;
                double apSignRate = 0.0;
                if (_pipeline is { } pl)
                {
                    apMode = (long)pl.Mode; apPromoted = pl.Promoted; apSignRate = pl.SigningSuccessRate;
                }
                // v4.87.0 — 집행 미시구조(FSM 단계·슬리피지·σ·강등) 합류(미배선이면 0).
                long ignCode = 0L, slipRaw = 0L, sigRaw = 0L, downgrades = 0L;
                if (_ignition is { } ig) ignCode = (long)ig.Stage;
                if (_execGuard is { } eg) { slipRaw = eg.LastSlipRaw; sigRaw = eg.LastSigmaRaw; downgrades = eg.Downgrades; }
                // v4.88.0 — 원장 보호 메시(큐 깊이·용량·평균 영속화 µs·쓰기 실패) 합류(미배선이면 0).
                long ledDepth = 0L, ledCap = 0L, ledFails = 0L;
                double ledMicros = 0.0;
                if (_ledger is { } lm) { ledDepth = lm.QueueDepth; ledCap = lm.Capacity; ledMicros = lm.AvgPersistMicros; ledFails = lm.WriteFailures; }
                // v4.90.0 — 웜 스타트 복구 엔진(복구 단계·백로그 잔여·프로브 RTT·슬리피지σ) 합류(미배선이면 0).
                long rvState = 0L, rvBacklog = 0L, rvRtt = 0L, rvSig = 0L;
                if (_recovery is { } rc) { rvState = rc.RecoveryStateCode; rvBacklog = rc.BacklogRemaining; rvRtt = rc.LastProbeLatencyMs; rvSig = rc.LastProbeSigmaRaw; }
                // v4.91.0 — 카오스 그리드(가상시간·플래그·PBT 패스·가상큐) 합류(미배선이면 0).
                long cgTicks = 0L, cgFlags = 0L, cgPbt = 0L, cgQ = 0L;
                if (_chaosGrid is { } cg) { cgTicks = cg.VirtualTimeTicks; cgFlags = cg.ChaosFlags; cgPbt = cg.PbtPasses; cgQ = cg.VirtualQueueDepth; }
                // v4.92.0 — 프로덕션 스왑(연결상태·토큰수명·방화벽·실전감지) 합류(미배선이면 0).
                long psKiwoom = 0L, psToss = 0L, psTokenSec = 0L, psBlocks = 0L, psProdMask = 0L;
                if (_prodSwap is { } ps)
                { psKiwoom = (long)ps.KiwoomLink; psToss = (long)ps.TossLink; psTokenSec = ps.TossTokenRemainSec; psBlocks = ps.FirewallBlocks; psProdMask = ps.ProductionLiveMask; }
                // v4.93.0 — 뉴스 인텔(인입속도·시장감성·쇼크·브레이커·미분석·멘토 FSM) 합류(미배선이면 0).
                long niRate = 0L, niSent = 0L, niShock = 0L, niTrip = 0L, niUnan = 0L, niFsm = 0L;
                if (_newsIntel is { } ni)
                { niRate = ni.NewsRatePerMin; niSent = ni.MarketSentimentRaw; niShock = ni.SemanticShockActive; niTrip = ni.SentimentBreakerTripped; niUnan = ni.NewsUnanalyzedCount; niFsm = ni.MentorFsmCode; }
                // v4.94.0 — 리스크 실드·다중 브로커 링·MMF 패브릭(증거금 포화·예약 거부·노드 마스크·µs 레이턴시·가드 트립) 합류(미배선이면 0).
                long rsUtil = 0L, rsRej = 0L, mbMask = 0L, mmLat = 0L, guardMask = 0L;
                if (_riskShield is { } rs) { rsUtil = rs.MarginUtilizationRaw; rsRej = rs.RejectCount; if (rs.Escalated) guardMask |= 1L; }
                if (_shadowRing is { } sr) mbMask = sr.ActiveNodeMask;
                if (_mmfFabric  is { } mf) { mmLat = mf.LastLatencyMicros; if (mf.IsCorrupted) guardMask |= 2L; }
                // v4.95.0 — 블랙박스(포화도·초당 기록 속도·덤프 상태·봉인 후 유실) 합류(미배선이면 0).
                long bbSat = 0L, bbRate = 0L, bbDump = 0L, bbDrop = 0L;
                if (_blackbox is { } bb)
                {
                    bbSat = bb.SaturationRaw; bbDrop = bb.DroppedWhileSealed;
                    long total = bb.TotalRecorded; long dBb = total - _lastBlackboxTotal;
                    bbRate = (dTick > 0 && dBb > 0) ? (long)(dBb * (double)Stopwatch.Frequency / dTick) : 0L;
                    _lastBlackboxTotal = total;
                }
                if (_dumper is { } dp) bbDump = (long)dp.State;
                // v4.96.0 — 실시간 청산 메쉬(누적 수수료 KRW/USD·캐리 오차·래치 큐·영점 상태) 합류(미배선이면 0).
                long acFeeKrw = 0L, acFeeUsd = 0L, acCarry = 0L, acLatch = 0L, acState = 0L;
                if (_accounting is { } am)
                { acFeeKrw = am.CumFeeKrwRaw; acFeeUsd = am.CumFeeUsdRaw; acCarry = am.CarryErrorRaw; acLatch = am.LatchQueueDepth; acState = (long)am.State; }
                // v4.97.0 — 섀도우 집행·컷오버(섀도우모드·가상체결 RTT·컷오버 상태·카나리 편차·듀얼트랙 드리프트) 합류(미배선이면 0).
                long sgShadow = 0L, sgRtt = 0L, coState = 0L, coCanary = 0L, coDrift = 0L;
                if (_shadowGateway is { } sg) { sgShadow = sg.IsShadowMode ? 1L : 0L; sgRtt = sg.LastFillRttMicros; }
                if (_cutover is { } co) { coState = (long)co.State; coCanary = co.LastCanaryDeviationRaw; coDrift = co.LastDualTrackDriftRaw; }
                // v5.0.0 — 실전 총점화(Genesis 상태·실 예수금·Canary net 대조·블래스트 반경·킬스위치·데드맨) 합류(미배선이면 0).
                long gnState = 0L, gnDep = 0L, cpDrift = 0L, brCeil = 0L, ksState = 0L, dmState = 0L;
                if (_genesis is { } gn) { gnState = (long)gn.State; gnDep = gn.SyncedDepositKrwRaw; }
                if (_canaryProbe is { } cp) cpDrift = cp.LastNetDriftRaw;
                if (_blastRadius is { } br) brCeil = br.CeilingRaw;
                if (_killSwitch is { } ks) ksState = (long)ks.State;
                if (_deadman is { } dm) dmState = (long)dm.State;
                // v5.1.0 — 자율 라이브 감시망(세션 지연 µs·Coma·페일오버·쓰레드풀 가용성·고아 in-flight·블랙스완 방어선) 합류(미배선이면 0).
                long wdLat = 0L, wdHealth = 0L, foChan = 0L, foCount = 0L, tpAvail = 0L, orInFlight = 0L, bsCode = 0L;
                if (_watchdog is { } wd) { wdLat = wd.LastLatencyTicks / 10L; wdHealth = (long)wd.Health; }   // 100ns ticks→µs(÷10)
                if (_failover is { } fo) { foChan = (long)fo.ResolveChannel(); foCount = fo.Failovers; }
                if (_starvation is { } ts) tpAvail = ts.AvailabilityScoreMilli;
                if (_reaper is { } rp) { orInFlight = rp.InFlightCount; bsCode = rp.Freezes > 0 ? 2L : (rp.Orphans > 0 ? 1L : 0L); }
                // v5.2.0 — 미시 유동성 슬라이서/스로틀/독약 유동성 합류(미배선이면 0).
                long mlImb = 0L, mlSlices = 0L, thrCount = 0L, savedBps = 0L, toxCode = 0L;
                if (_slicer is { } sl) { mlImb = sl.LastImbalanceRaw; mlSlices = sl.SlicesEmitted; savedBps = sl.CumSavedBps; }
                if (_throttle is { } th) thrCount = th.Throttled;
                if (_toxic is { } tox) toxCode = (long)tox.Verdict;
                // v5.3.0 — 무정지 전략 핫패치(세대·빌드ts·이월 RTT·좀비·고아 이벤트) 합류(미배선이면 0).
                long stGen = 0L, stBuild = 0L, carryRtt = 0L, zombie = 0L, orphan = 0L;
                if (_reloadGate is { } rg) { stGen = rg.Generation; stBuild = rg.ActiveBuildTicks; }
                if (_stateCarrier is { } scr) carryRtt = scr.LastHandoverRttNs;
                if (_zombieShield is { } zs) { zombie = zs.ZombieCount; orphan = zs.OrphanEventsRejected; }
                // v5.4.0 — 다중 전략 인터리빙 메쉬·네팅·자전거래·전염 방화벽 합류(미배선이면 0).
                long msActive = 0L, msSealed = 0L, nettSaved = 0L, washRej = 0L, resonance = 0L, capSat = 0L;
                if (_mesh is { } m) { msActive = m.ActiveLaneCount; msSealed = m.SealedLaneCount + m.MutedLaneCount; capSat = m.CapitalSaturationBps; }
                if (_netter is { } nt) nettSaved = nt.SavedCostRawCum;
                if (_washGuard is { } wgd) washRej = wgd.Rejections;
                if (_firewall is { } fw) { resonance = fw.ResonanceMetric; if (msSealed == 0L) msSealed = fw.SealCount; }
                // v5.5.0 — 로깅 패브릭(영속 rate·큐·드롭·압축비)·커널 지터(µs·홀드·NIC drop)·I/O 방어선(상태) 합류(미배선이면 0).
                long logBps = 0L, logQ = 0L, logDrop = 0L, logComp = 10000L, kJit = 0L, kHold = 0L, nicDrop = 0L, ioStat = 0L;
                if (_logger is { } lg)
                {
                    logQ = lg.QueueDepth; logDrop = lg.DroppedTotal; logComp = lg.CompressionRatioBps;
                    long lc = lg.CompBytesCum; long dLc = lc - _lastLogCompBytes;
                    logBps = (dTick > 0 && dLc > 0) ? (long)(dLc * (double)Stopwatch.Frequency / dTick) : 0L;
                    _lastLogCompBytes = lc;
                }
                if (_kernelJitter is { } kj) { kJit = kj.LastJitterMicros; nicDrop = kj.NicDropsEstimate; if (kj.SafeHoldActive) kHold = 1L; }
                if (_ioFirewall is { } iofw) { ioStat = iofw.StatusCode; if (iofw.PageThrashHoldActive) kHold = 1L; }
                // v6.0.0 Path Gamma — RIO 바이패스·플레이백·CausalWeave 합류(미배선이면 0). 무할당(long/double 독출).
                long rioActive = 0L, rioLatNs = 0L, rioFb = 0L, rioFrames = 0L;
                if (_rioMesh is { } rm)
                { rioActive = rm.RioBypassActive ? 1L : 0L; rioLatNs = rm.ReceiveLatencyNanos; rioFb = rm.FallbackActive ? 1L : 0L; rioFrames = rm.FramesReceived; }
                long pbState = 0L, pbPlayed = 0L, pbTotal = 0L, pbIntegrity = 0L; double pbFps = 0.0;
                if (_playback is { } pb)
                { pbState = (long)pb.State; pbPlayed = pb.PlayedEvents; pbTotal = pb.TotalEvents; pbFps = pb.Fps; pbIntegrity = pb.IntegrityScoreRaw; }
                long cwState = 0L, cwEvict = 0L, cwSat = 0L, cwFuse = 0L;
                if (_weaveClock is { } wc) { cwFuse = wc.FuseTripped ? 1L : 0L; }
                if (_weaveRing is { } wr)  { cwEvict = wr.Evictions; cwSat = wr.SaturationPermille; cwState = (long)wr.State; }
                if (cwFuse == 1L) cwState = (long)CausalWeaveState.FuseTripped;   // 퓨즈 트립이 링 상태를 오버라이드(최위중)
                // v7.0.0 Phase 7 — 하드웨어 프로브(캐시된 콜드 리포트) + 암호학적 감사 금고(δ=0 기록·스로틀·트립 봉인) 합류.
                long hwSup = 0L, hwAdp = 0L, hwBuf = 0L, hwFb = 0L;
                if (_hwProbed)
                { hwSup = _hwProbeReport.RioSupported ? 1L : 0L; hwAdp = _hwProbeReport.AdapterCount; hwBuf = _hwProbeReport.MaxReceiveBufferBytes; hwFb = _hwProbeReport.RecommendSafeFallback ? 1L : 0L; }
                long auCode = 0L, auCount = 0L, auSealed = 0L, auHead = 0L;
                if (_auditVault is { } av)
                {
                    if (!av.Sealed)
                    {
                        if (cwFuse == 1L)
                            av.SealTrip(qpcDelta: (_weaveClock?.Watermark ?? now), seqOffset: cwEvict, bufferState: cwSat);   // ★트립 포렌식 비가역 봉인
                        else if (cwEvict != _lastAuditEvict || (now - _lastAuditTick) >= _auditIntervalTicks)
                        {
                            av.TryAppend(_weaveClock?.NowTicks() ?? now, cwEvict, published);   // δ=0 append-only 기록
                            _lastAuditEvict = cwEvict; _lastAuditTick = now;
                        }
                    }
                    auCode = av.PollVerify(now); auCount = av.Count; auSealed = av.Sealed ? 1L : 0L; auHead = av.HeadHash8;
                }
                // v7.2.0 — VaR 엔진 텔레메트리 캐시 풀(δ=0 — 구동기가 갱신한 최근 산출값만 읽음). 미배선이면 0.
                long vVar99 = 0L, vStress = 0L, vRidge = 0L, vValve = 0L, vGuard = 0L; double vGersh = 0.0;
                if (_varEngine is { } ve)
                {
                    vVar99 = ve.LastVar99Krw; vStress = ve.LastStressKrw; vGersh = ve.LastGershgorinBound;
                    vRidge = ve.RidgeInjections; vValve = ve.RiskValveStateCode; vGuard = ve.CovGuardStateCode;
                }
                // v7.3.0 — 하드웨어 P-Core/서멀/코히런스 텔레메트리 풀(δ=0 — long 카운터 독출). 미배선이면 0.
                long hwCore = 0L, hwPinned = 0L, hwMig = 0L, hwPinFail = 0L, hwFreq = 0L, hwThrHold = 0L, hwThrSafe = 0L, hwXcore = 0L, hwCohFuse = 0L;
                if (_cpuGrid is { } cg2) { hwCore = cg2.PCoreCount; hwPinned = cg2.PinnedThreads; hwMig = cg2.MigrationEvents; hwPinFail = cg2.PinFailures; }
                if (_thermalGuard is { } tg) { hwFreq = tg.EffectiveFreqPermille; hwThrHold = tg.ThrottleHolds; if (tg.SafeHoldActive) hwThrSafe = 1L; }
                if (_coherence is { } co2) { hwXcore = co2.CrossCoreWrites; if (co2.CoherenceFuseTripped) hwCohFuse = 1L; }
                // v7.4.0 — Genesis Pre-Flight & 안전 패브릭 텔레메트리 풀(δ=0). ★Sample 이 데드맨 심박(엔진 생존)도 Beat —
                //   이 샘플러 스레드가 행(스레드풀 기아·데드락)에 빠지면 심박이 끊겨 데드맨이 밸브를 하드 차단한다.
                _deadMan?.Beat();
                long pfScore = 0L, pfForensic = 0L, leaseSec = 0L, leasePermille = 0L, dmAge = 0L, dmTrip = 0L, ffFrozen = 0L, ffVetoes = 0L, ffForensic = 0L;
                if (_preFlight is { } pf) { pfScore = pf.Score; pfForensic = pf.ForensicCode; }
                if (_armingLease is { } al) { leaseSec = al.RemainingSeconds; leasePermille = al.RemainingPermille; }
                if (_deadMan is { } dms) { dmAge = dms.HeartbeatAgeMs; dmTrip = dms.Tripped ? 1L : 0L; }
                if (_fatFinger is { } ff2) { ffFrozen = ff2.Frozen ? 1L : 0L; ffVetoes = ff2.PriceVetoes + ff2.VelocityVetoes; ffForensic = ff2.LastForensic; }
                // v8.0.0 — AVX-512 미시 알파 & SIMD 오토튜너 텔레메트리 풀(δ=0).
                long avxTier = 0L, simdLatNs = 0L, simdAlpha = 0L, snrBlocks = 0L, avxDown = 0L, zmmWarm = 0L, avxCalib = 0L, alignedLd = 0L;
                if (_avxTuner is { } at) { avxTier = (long)at.SelectedWidth; simdLatNs = at.SelectedOpLatencyNanos; avxDown = at.WidthDowngrades; zmmWarm = at.WarmKeepAlives; avxCalib = at.Calibrations; }
                if (_alphaEngine is { } ae) { simdAlpha = ae.LastAlphaMilli; if (avxCalib == 0L) avxTier = (long)ae.LastTier; }
                if (_noiseQuant is { } nq) snrBlocks = nq.NoiseBlocks;
                if (_alignedVec is { } av2) alignedLd = av2.AlignedLoads;
                // v8.1.0 — 퀀텀 금고 & 인프라 해방 텔레메트리 풀(δ=0).
                long qvArmed = 0L, qvBreach = 0L, honeyTrip = 0L, hookState = 0L, rcuVer = 0L, zeroFail = 0L, zeroBufs = 0L;
                if (_vault is { } qv) { qvArmed = qv.IsArmed ? 1L : 0L; qvBreach = qv.IsBreached ? 1L : 0L; zeroBufs = qv.ZeroizedBuffers; }
                if (_honeyMesh is { } hm) honeyTrip = hm.IsTripped ? 1L : 0L;
                if (_antiHook is { } ah) hookState = ah.HookStateCode;
                if (_zeroizer is { } zr) zeroFail = zr.VerificationFailures;
                if (_rcuVersion is { } rv) { try { rcuVer = rv(); } catch { } }
                // v9.0.0 — SOR & 빙산호가 텔레메트리 풀(δ=0).
                long sorActive = 0L, sorRoutes = 0L, sorCode = 0L, icebergHidden = 0L, icebergsDet = 0L, queueImm = 0L, sorSpoof = 0L, liqAuth = 1000L;
                if (_sor is { } sre) { sorRoutes = sre.Routes; sorCode = sre.LastRoutingCode; sorSpoof = sre.SpoofBlocks; sorActive = sre.Routes > 0 ? 1L : 0L; }
                if (_iceberg2 is { } ib3) { icebergHidden = ib3.EstimatedHiddenTotalRaw; icebergsDet = ib3.IcebergsDetected; }
                if (_queueEst is { } qe) queueImm = qe.FillImminencePermille;
                if (_authVerify is { } avv) liqAuth = Math.Min(avv.BidAuthenticityPermille, avv.AskAuthenticityPermille);
                // v9.1.0 — 극단적 카오스 결함 주입 샌드박스 텔레메트리 풀(δ=0).
                long chaosState = 0L, chaosMask = 0L, chaosInj = 0L, chaosLive = 0L, chaosTorn = 0L, chaosRecov = 0L, chaosSeed = 0L, chaosHold = 0L;
                if (_chaos is { } cx) { chaosState = cx.StateCode; chaosMask = cx.FaultMask; chaosInj = cx.Injections; if (cx.SafeHoldActive) chaosHold = 1L; }
                if (_chaosGuard is { } cg3) { chaosLive = cg3.LivelockEscapes; chaosTorn = cg3.TornStateBlocks; if (cg3.SafeHoldActive) chaosHold = 1L; }
                if (_chaosRecov is { } cr) { chaosRecov = cr.State; if (cr.RecoveryBlocking) chaosHold = 1L; }
                if (_chaosReplay is { } crp) chaosSeed = (long)(crp.Seed & 0xFFFFFFFFUL);
                // v10.0.0 — Genesis Live Ignition Gateway 텔레메트리 풀(δ=0).
                long ignDualKey = 0L, ignState = 0L, ignVetoes = 0L, ignVetoCode = 0L, ignAudit = 0L, keyAlive = 0L, ignDoubleBlk = 0L, ignSealed = 0L;
                if (_ignGateway is { } iglw) { if (iglw.DualKeyWired) ignDualKey = 1L; ignState = iglw.StateCode; ignVetoes = iglw.Vetoes; ignVetoCode = iglw.LastVetoCode; }
                if (_ignAudit is { } ia) ignAudit = ia.TotalRecorded;
                if (_keyPulse is { } kp && kp.KeyAlive) keyAlive = 1L;
                if (_sealedEmit is { } se2) { ignDoubleBlk = se2.DoubleDispatchBlocks; ignSealed = se2.SealedEmissions; }
                // v10.1.0 — Live Flight Certification & Mock Dry-Run 텔레메트리 풀(δ=0). ★Sample 이 인증 재채점(전역 불변식 총괄).
                long dryRun = 0L, mockFills = 0L, mockSlip = 0L, mockRtt = 0L, certScore = 0L, certValid = 0L, certSeal = 0L, scopeBounce = 0L;
                if (_certSealer is { } cs) { _channel.TryRead(out var lastF); cs.Certify(in lastF); certScore = cs.Score; if (cs.SealValid) certValid = 1L; certSeal = cs.Seal & 0xFFFFFFFFL; }   // ★직전 프레임(N-1) 채점 — 결과는 프레임 N 에 발행
                if (_mockMirror is { } mm) { mockFills = mm.VirtualFills; mockSlip = mm.LastSlippageBps; mockRtt = mm.LastFillRttNanos; }
                if (_scopeBar is { } sb) { if (sb.DryRunActive) dryRun = 1L; scopeBounce = sb.ScopeBounces + sb.StaleRejects; }
                // v10.1.0 Final Packaging — 나노 SLA 평가 + 지터 회귀 + 릴리스 봉인 + 마스터 인증(fail-closed).
                long slaP9999 = 0L, slaVeto = 0L, slaMeasured = 0L, jitterRej = 0L, jitterRate = 0L, jitterRegr = 0L, relSealValid = 0L, relSealHex = 0L, masterCert = 0L;
                if (_slaGate is { } slaG) { bool okSla = slaG.EvaluateSla(); slaP9999 = slaG.LastP9999Nanos; if (!okSla) slaVeto = 1L; if (slaG.Count > 0L) slaMeasured = 1L; }   // ★샘플러(단일 평가자)가 EvaluateSla — 발주 핫패스는 Record 만(δ=0)
                if (_jitterGuard is { } jitG) { jitterRej = jitG.JitterRejects; jitterRate = jitG.JitterRatePermille; if (jitG.RegressionTripped) jitterRegr = 1L; }
                if (_releaseAttestor is { } relA) { if (relA.SealValid) relSealValid = 1L; relSealHex = relA.SealHex64 & 0xFFFFFFFFL; }
                // ★마스터 인증(fail-closed): 실 SLA 측정(≥1표본) ∧ P99.99 미이탈 ∧ 봉인 검증 성공 ∧ 지터 회귀 없음 — 넷 모두. 모듈 미배선/미측정 시 0.
                if (relSealValid == 1L && slaMeasured == 1L && slaVeto == 0L && jitterRegr == 0L) masterCert = 1L;
                // v10.1.0 Ops — 호스트 타이머 프로브 + 드리프트 센티널 + 사전점화 서명 + 카나리 + 패닉(fail-closed).
                long hostTimerRes = HostEnvironmentDriftSentinel.CurrentTimerResolutionNanos();
                long signoffPass = 0L, signoffFail = 0L, canaryUtil = 0L, canaryDown = 0L, driftTrip = 0L, panicTrip = 0L, stagingReady = 0L;
                if (_signoffValidator is { } sofV) { _channel.TryRead(out var lastFS); sofV.Certify(in lastFS); if (sofV.SignoffPassed) signoffPass = 1L; signoffFail = sofV.FailMask; }   // ★직전 프레임(N-1) 채점
                if (_driftSentinel is { } drfS) { if (signoffPass == 1L && !drfS.Sealed) drfS.SealBaseline(hostTimerRes, jitterRate); drfS.Probe(hostTimerRes, jitterRate); if (drfS.DriftTripped) driftTrip = 1L; }   // ★서명 시점 봉인 + 연속 확인 강등
                // ★카나리 슬리피지 피드(강등 경로 발화): 드라이런 리허설은 미러 슬리피지를 RecordSlippage 에 공급 — 실전은 실 체결 소스가 동일 진입점(RecordSlippage).
                if (_canaryGov is { } canG)
                {
                    if (_mockMirror is { } mmSlip && (_scopeBar?.DryRunActive ?? false) && mmSlip.LastSlippageBps > 0L) canG.RecordSlippage(mmSlip.LastSlippageBps);
                    canaryUtil = canG.CapUtilizationPermille;
                    if (canG.Downgraded) canaryDown = 1L;
                }
                if (_panicSwitch is { } pncS) { if (pncS.Tripped) panicTrip = 1L; }
                if (signoffPass == 1L && driftTrip == 0L && canaryDown == 0L && panicTrip == 0L) stagingReady = 1L;   // ★[HOST TUNED - CANARY STAGING READY]
                // v10.2.0 Toss — 포트폴리오/시세 텔레메트리(읽기·δ=0 조회·조회 전용).
                long tossConn = 0L, tossEval = 0L, tossPnl = 0L, tossRtt = 0L, tossTokFresh = 0L, tossRecov = 0L, tossProv = 0L, tossVeto = 0L;
                if (_tossAdapter is { } tossA)
                {
                    tossVeto = tossA.OrderVetoes;
                    if (tossA.TryGetAccount(out var tacc)) { tossEval = tacc.TotalEvalRaw; tossPnl = tacc.TotalPnlRaw; }
                }
                if (_tossGateway is { } tossG)
                {
                    tossRtt = tossG.LastRttMicros;
                    tossRecov = tossG.Guardian.HolesRecovered;
                    tossProv = tossG.Guardian.IsProvisional ? 1L : 0L;
                    tossConn = (tossG.Connected && (_tossAdapter?.Connected ?? false)) ? 1L : 0L;
                }
                if (_tossToken is { } tossT) tossTokFresh = tossT.HasValidToken ? 1L : 0L;
                // v10.2.0 Universal Broker Mesh — 다중 증권사 통합 텔레메트리(읽기).
                long activeBrokerId = 0L, crossFallbacks = 0L, univRtt = 0L, meshTorn = 0L;
                if (_fallbackSentinel is { } fbS) { activeBrokerId = (long)fbS.ActivePrimary; crossFallbacks = fbS.Fallbacks; }
                if (_timeFence is { } tfF) meshTorn = tfF.TornDrifts;
                if (_tossGateway is { } uGw) univRtt = uGw.LastRttMicros;   // 활성 브로커 범용 RTT(현 배선=Toss 게이트웨이)
                // v11.0.0 Genesis Fleet Production — 실물 발주 텔레메트리(읽기).
                long liveDispatchArmed = 0L, orderApiTimeouts = 0L, liveFillSlippage = 0L, execSecretTainted = 0L;
                if (_ignGateway is { } ignGw && ignGw.DualKeyWired && _execAdapter is not null) liveDispatchArmed = 1L;   // ★실물 발주 락 해제(이중 물리 키 ∧ 어댑터 결속)
                if (_execBridge is { } exBr) { orderApiTimeouts = exBr.Timeouts; liveFillSlippage = exBr.LastSlippageBps; }
                if (_execZeroGuard is { } exZg && exZg.HeapContaminated) execSecretTainted = 1L;   // ★발주 시크릿 힙 오염
                _channel.Publish(new RingTelemetry(
                    published, _dispatcher.Forwarded, _dispatcher.GatedOut, _dispatcher.Dropped,
                    _ring.WriterCollisions, tps, _dispatcher.SendErrors, sVer, sWc, sFr,
                    afCycles, afSigned, afRejected, afRejRate, afSuspended,
                    apMode, apPromoted, apSignRate,
                    ignCode, slipRaw, sigRaw, downgrades,
                    ledDepth, ledCap, ledMicros, ledFails,
                    rvState, rvBacklog, rvRtt, rvSig,
                    cgTicks, cgFlags, cgPbt, cgQ,
                    psKiwoom, psToss, psTokenSec, psBlocks, psProdMask,
                    niRate, niSent, niShock, niTrip, niUnan, niFsm,
                    rsUtil, rsRej, mbMask, mmLat, guardMask,
                    bbSat, bbRate, bbDump, bbDrop,
                    acFeeKrw, acFeeUsd, acCarry, acLatch, acState,
                    sgShadow, sgRtt, coState, coCanary, coDrift,
                    gnState, gnDep, cpDrift, brCeil, ksState, dmState,
                    wdLat, wdHealth, foChan, foCount, tpAvail, orInFlight, bsCode,
                    mlImb, mlSlices, thrCount, savedBps, toxCode,
                    stGen, stBuild, carryRtt, zombie, orphan,
                    msActive, msSealed, nettSaved, washRej, resonance, capSat,
                    logBps, logQ, logDrop, logComp, kJit, kHold, nicDrop, ioStat,
                    rioActive, rioLatNs, rioFb, rioFrames, pbState, pbPlayed, pbTotal, pbFps, pbIntegrity,
                    cwState, cwEvict, cwSat, cwFuse,
                    hwSup, hwAdp, hwBuf, hwFb, auCode, auCount, auSealed, auHead,
                    vVar99, vStress, vGersh, vRidge, vValve, vGuard,
                    hwCore, hwPinned, hwMig, hwPinFail, hwFreq, hwThrHold, hwThrSafe, hwXcore, hwCohFuse,
                    pfScore, pfForensic, leaseSec, leasePermille, dmAge, dmTrip, ffFrozen, ffVetoes, ffForensic,
                    avxTier, simdLatNs, simdAlpha, snrBlocks, avxDown, zmmWarm, avxCalib, alignedLd,
                    qvArmed, qvBreach, honeyTrip, hookState, _infraCoupling, rcuVer, zeroFail, zeroBufs,
                    sorActive, sorRoutes, sorCode, icebergHidden, icebergsDet, queueImm, sorSpoof, liqAuth,
                    chaosState, chaosMask, chaosInj, chaosLive, chaosTorn, chaosRecov, chaosSeed, chaosHold,
                    ignDualKey, ignState, ignVetoes, ignVetoCode, ignAudit, keyAlive, ignDoubleBlk, ignSealed,
                    dryRun, mockFills, mockSlip, mockRtt, certScore, certValid, certSeal, scopeBounce,
                    slaP9999, slaVeto, jitterRej, jitterRate, jitterRegr, relSealValid, relSealHex, masterCert,
                    signoffPass, signoffFail, canaryUtil, canaryDown, hostTimerRes, driftTrip, panicTrip, stagingReady,
                    tossConn, tossEval, tossPnl, tossRtt, tossTokFresh, tossRecov, tossProv, tossVeto,
                    activeBrokerId, crossFallbacks, univRtt, meshTorn,
                    liveDispatchArmed, orderApiTimeouts, liveFillSlippage, execSecretTainted));
                _lastPublished = published;
                _lastTicks     = now;
            }
            finally { Volatile.Write(ref _sampling, 0); }
        }
    }
}
