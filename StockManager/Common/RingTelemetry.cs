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
                             long slippageSavedBps = 0L, long toxicityCode = 0L)
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
    }

    /// <summary>단일 라이터 seqlock 채널 — 캐시라인 격리. Publish(샘플러 단일 라이터)/TryRead(UI 락-프리·무할당).</summary>
    public sealed class RingTelemetryChannel
    {
        private const int CacheLine = 64;

        // Version(라이터 고빈도)과 Data(리더)를 서로 다른 64B 라인에 두고 앞 패딩+사이 갭으로 거짓 공유 차단.
        //   ★Size 는 Data(@128) 확장에 맞춘 여유값. v5.2.0 기준 필드 82개×8=656B → Data 128..784B ≤ 832(64배수·잔여 6슬롯=48B).
        //   (v5.1.0 77필드=616B→768 잔여 3슬롯 → v5.2.0 미시 유동성 5필드 추가로 82필드 → 768 초과 → 832 로 상향·헤드룸 6슬롯.)
        [StructLayout(LayoutKind.Explicit, Size = 832)]
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
        private long _lastPublished;
        private long _lastBlackboxTotal;
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
                                      ToxicFlowSentinel? toxic = null)
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
            _lastBlackboxTotal = _blackbox?.TotalRecorded ?? 0L;   // ★C#11: 첫 Sample rate 델타 기준 초기화(허위 스파이크 제거)
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
                    mlImb, mlSlices, thrCount, savedBps, toxCode));
                _lastPublished = published;
                _lastTicks     = now;
            }
            finally { Volatile.Write(ref _sampling, 0); }
        }
    }
}
