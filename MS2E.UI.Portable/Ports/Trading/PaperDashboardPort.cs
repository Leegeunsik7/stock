// ============================================================================
// PaperDashboardPort — [대시보드 · 추천 A] AI 모의투자 실시간 시각화 대시보드 포트(순수 UI 경계)
//
//   호스트 DashboardSnapshotHub(무할당 텔레메트리 수집 + 3중 버퍼 스냅샷)가 50ms 주기로 조립한 프레임을 UI 가 '빌려서(Acquire)'
//   렌더하고 '돌려준다(Release)'. 빌린 프레임은 허브가 덮어쓰지 않는다(리스 중 버퍼 재사용 금지) — UI 가 느려도 찢어진 프레임 0,
//   허브는 남은 버퍼로 계속 조립(코어 매매 경로와 무관). 프레임은 원시 구조체 배열만 담는다(엔진 타입 참조 0 · RCL 순수성).
//   ★Zero UI Interference: 이 포트는 '읽기 전용'이다 — 주문/무장/취소 명령 경로가 없다(대시보드가 브로커에 닿을 수단 자체가 없음).
// ============================================================================
using System;

namespace MS2E.UI.Portable.Ports.Trading
{
    /// <summary>체결 피드 1건(원 · 주). Side +1 매수 · −1 매도. Broker 0 키움 · 1 KIS · 2 모의(가상).</summary>
    public struct DashFill { public long Ticks, Symbol, Qty, PriceWon; public int Side, Broker; }

    /// <summary>[방향 B] AI 판단 근거 1건 — 문자열 없이 코드·수치만(텍스트는 렌더 시 UI 가 조립). bp = 1/10000.</summary>
    public struct DashDecision { public long Ticks, Symbol, ScaleBp, VolatilityBp, Value; public int Kind, Reason; }

    /// <summary>인터락 비트 전이 1건(raised/cleared 비트 · 전이 후 마스크).</summary>
    public struct DashInterlockEvent { public long Ticks, Raised, Cleared, MaskAfter; }

    /// <summary>[방향 A] 자식 슬롯 1개(부모 · 주문/체결 수량 · 브로커 · 상태 코드).</summary>
    public struct DashChild { public long ParentId, OrderQty, FilledQty, AvgPriceWon; public int Index, Broker, State; }

    /// <summary>브로커 한도 지표(결속 여부 · 매수 여력 원).</summary>
    public struct DashBrokerLimit { public int Broker; public bool Bound; public long HeadroomWon; }

    /// <summary>[v12.18.7-DEV] 브로커 세션 복구 상태 1개(DashSession 코드 · 연속 실패/상한 · 단절 · 복구 · 재접속 시도 누적).</summary>
    public struct DashBrokerSession { public int Broker, State, Failures, MaxFailures; public long Losses, Recoveries, Attempts; }

    public struct DashEquityPoint { public long Ticks, EquityWon; }

    /// <summary>[v12.17.5-DEV] 주문 경로 구간 1개의 최근 창(≤ 1,000 부모) 분포 — ns · 백분위 = 지연 버킷 상한(≤ 관측 최대).</summary>
    public struct DashLatencyStage { public long Count, P50Nanos, P99Nanos, MaxNanos; }

    /// <summary>
    /// [v12.17.5-DEV] 주문 경로 지연 등급 — 기준 = 최근 창 시그널 → 발주 p99. 정상 ≤ 1ms · 미세 정체 1ms 초과 · 심각 지연 10ms 초과 ·
    /// 미계측 = 계측기 미결선 또는 표본 0(UI 는 정직하게 '미계측' 렌더 — 숫자 날조 0).
    /// </summary>
    public static class DashLatencyGrade
    {
        public const int Normal = 0, MicroSpike = 1, SevereLag = 2, Unmeasured = 3;
        public const long MicroSpikeNanos = 1_000_000, SevereLagNanos = 10_000_000;
        public const int StageCount = 3;
        /// <summary>구간 인덱스(OrderPathStage 와 같은 순서).</summary>
        public const int SignalToGate = 0, GateToEgress = 1, SignalToEgress = 2;

        public static int Classify(bool bound, long samples, long p99Nanos)
            => !bound || samples <= 0 ? Unmeasured : p99Nanos > SevereLagNanos ? SevereLag : p99Nanos > MicroSpikeNanos ? MicroSpike : Normal;
    }

    /// <summary>[v12.18.1-DEV] 시장 국면 코드(엔진 MicroRegime 과 같은 값 · 4 = 캘리브레이터 미결선).</summary>
    public static class DashRegime
    {
        public const int Warmup = 0, Trend = 1, MeanReverting = 2, Choppy = 3, Unbound = 4;
    }

    /// <summary>
    /// [v12.18.3-DEV] 강화 전용 설정 감사 원장 상태(호스트 AuditLedgerState 와 같은 값 · 0 = 미결선) · 동결 원인(AuditFreezeCause 와 같은 값).
    /// </summary>
    public static class DashAuditLedger
    {
        public const int Unbound = 0, Normal = 1, Saturating = 2, Frozen = 3;
        public const int CauseNone = 0, CauseChainBroken = 1, CauseFull = 2, CauseUnavailable = 3;
    }

    /// <summary>
    /// [v12.18.6-DEV] 마이크로초 지터 센티널 배지(0 미결선 · 1 정상 · 2 스파이크 · 3 GC 유발 스파이크).
    /// 스파이크/GC = 최근 HoldTicks(5초) 안에 임계(기본 500µs) 초과 표본이 새로 들어옴 — 그 뒤엔 누적 수만 남기고 정상 복귀.
    /// </summary>
    /// <summary>
    /// [v12.18.7-DEV] 브로커 세션 배지 — 0 미결선(관리자 없음) · 1 연결 · 2 접속 대기(최초 미연결) · 3 재접속 대기(백오프) · 4 재접속 중 ·
    /// 5 대사 중(재접속 후 미체결 대사 확정 전) · 6 소진 격리(sticky) · 7 원장 불일치 격리(sticky). 래치 = 상태보다 우선.
    /// 2~5 = 신규 발주 격리(한도 0 · 블랙리스트 — 세션 수립 이력이 있을 때) · 6/7 = 물리 이중키 재설정 전 격리 유지.
    /// </summary>
    public static class DashSession
    {
        public const int Unbound = 0, Connected = 1, Waiting = 2, Backoff = 3, Reconnecting = 4, Reconciling = 5, Exhausted = 6, Discrepancy = 7;

        /// <param name="state">SessionRecoveryState 원값(0 Connecting · 1 Connected · 2 Backoff · 3 Reconciling).</param>
        public static int Classify(bool bound, int state, bool everConnected, bool exhausted, bool discrepancy)
            => !bound ? Unbound : discrepancy ? Discrepancy : exhausted ? Exhausted
             : state switch { 1 => Connected, 2 => Backoff, 3 => Reconciling, 0 => everConnected ? Reconnecting : Waiting, _ => Unbound };
    }

    /// <summary>
    /// [v12.18.10-DEV] 브로커 생존성 사전 진단 — 상태(0 미결선 · 1 진단 중 · 2 통과 · 3 실패 래치) · 레인 판정(호스트 BrokerProbeVerdict 와 같은 값:
    /// 0 미결선 · 1 정상 · 2 시한 초과 · 3 무효 · 4 예외 · 5 판정 중). 실패 래치 = 비트 14/25 sticky(물리 이중키 재무장).
    /// </summary>
    public static class DashPreFlight
    {
        public const int Unbound = 0, Probing = 1, Passed = 2, Failed = 3;
        public const int LaneUnbound = 0, LaneHealthy = 1, LaneTimeout = 2, LaneInvalid = 3, LaneFault = 4, LanePending = 5;

        /// <param name="state">PreFlightLivenessState 원값(0 Pending · 1 Passed · 2 Failed).</param>
        public static int Classify(bool bound, int state) => !bound ? Unbound : state switch { 1 => Passed, 2 => Failed, _ => Probing };
    }

    public static class DashJitter
    {
        public const int Unbound = 0, Normal = 1, Spike = 2, GcSpike = 3;
        public const long HoldTicks = 5 * 10_000_000L;

        public static int Classify(bool bound, bool recentSpike, bool lastSpikeGc)
            => !bound ? Unbound : !recentSpike ? Normal : lastSpikeGc ? GcSpike : Spike;
    }

    /// <summary>[v12.18.2-DEV] 시그널 게이트 모드(0 미결선 · 1 관측 · 2 강제).</summary>
    public static class DashGateMode
    {
        public const int Unbound = 0, Observe = 1, Enforce = 2;
    }

    public static class DashDecisionKind
    {
        public const int Entry = 1, Exit = 2, Scale = 3, Hold = 4, Veto = 5;
        public static string Label(int k) => k switch { Entry => "진입", Exit => "청산", Scale => "비중", Hold => "관망", Veto => "거부", _ => "?" };
    }

    public static class DashChildState
    {
        // ChildSlotState 와 같은 코드(0 빈 · 1 대기 · 2 송출 · 3 접수 · 4 체결 · 5 취소 · 6 거부 · 7 회수)
        public static string Label(int s) => s switch { 1 => "대기", 2 => "송출", 3 => "접수", 4 => "체결", 5 => "취소", 6 => "거부", 7 => "회수", _ => "-" };
    }

    /// <summary>대시보드 1프레임 — 고정 용량 배열 + 유효 개수. 허브가 재사용(할당 0) · UI 는 리스 동안만 읽는다.</summary>
    public sealed class PaperDashboardFrame
    {
        public const int MaxFills = 32, MaxDecisions = 48, MaxEvents = 32, MaxEquity = 600, MaxChildren = 64, MaxBrokers = 4, MaxBits = 64;
        public const int MaxLatencyTrend = 64;

        public long Version, BuiltTicks;
        /// <summary>허브 3중 버퍼 슬롯 번호(리스 반납 식별 · UI 는 쓰지 않는다).</summary>
        public int BufferSlot;
        // 인터락
        public long Mask, RequiredMask, BoundMask;
        public bool Sealed;
        // 성과(원) — 수수료/세금 미반영(정직)
        public long RealizedPnlWon, UnrealizedPnlWon, EquityWon, PeakEquityWon, MaxDrawdownWon;
        public int Wins, Losses, OpenPositions;
        // 처리량 · 유실(링 덮어쓰기 = 렌더보다 빠른 유입 — 표시 전용 데이터 유실이며 매매 경로와 무관)
        public long TicksSeen, FillsSeen, FillsLost, DecisionsLost, EventsLost, BuildMicros;

        public readonly DashFill[] Fills = new DashFill[MaxFills];
        public readonly DashDecision[] Decisions = new DashDecision[MaxDecisions];
        public readonly DashInterlockEvent[] Events = new DashInterlockEvent[MaxEvents];
        public readonly DashEquityPoint[] Equity = new DashEquityPoint[MaxEquity];
        public readonly DashChild[] Children = new DashChild[MaxChildren];
        public readonly DashBrokerLimit[] Limits = new DashBrokerLimit[MaxBrokers];
        public int FillCount, DecisionCount, EventCount, EquityCount, ChildCount, LimitCount;

        // [v12.17.5-DEV] 주문 경로 지연(OrderPathStageProfiler 최근 창 · 허브가 펌프마다 재계산) — 미결선이면 LatencyBound = false · 등급 미계측.
        public bool LatencyBound;
        public int LatencyGrade = DashLatencyGrade.Unmeasured;
        /// <summary>계측 누적 부모 수 · 출구 전달/거부(veto) 판정 수(누적).</summary>
        public long LatencyOrders, LatencyForwarded, LatencyVetoed;
        /// <summary>[v12.17.6] 발행 스탬프 누적 · 출구 미도달 종결로 슬롯 회수된 누적(미무장 차단 · 게이트 거부 · veto — liveArmed:false 정상 상태의 정직한 관측).</summary>
        public long LatencyPublished, LatencyReclaimed;
        /// <summary>[방향 B] 주문 경로 미세 정체로 관제 게시 주기가 감속(50 → 200ms)된 상태인가 · 현재 게시 주기(ms).</summary>
        public bool LatencyThrottled;
        public int PublishPeriodMs;
        /// <summary>구간별 최근 창 분포(인덱스 = DashLatencyGrade.SignalToGate · GateToEgress · SignalToEgress).</summary>
        public readonly DashLatencyStage[] LatencyStages = new DashLatencyStage[DashLatencyGrade.StageCount];
        /// <summary>최근 부모 시그널 → 발주 지연(ns · 과거 → 최신 · 최대 64) — 스파크라인 원천.</summary>
        public readonly long[] LatencyTrend = new long[MaxLatencyTrend];
        public int LatencyTrendCount;
        /// <summary>유효 추이 구간(복사 0 · 리스 동안만 유효).</summary>
        public ReadOnlySpan<long> LatencyTrendSpan => LatencyTrend.AsSpan(0, LatencyTrendCount);
        public ref readonly DashLatencyStage Latency(int stage) => ref LatencyStages[(uint)stage < (uint)LatencyStages.Length ? stage : DashLatencyGrade.SignalToEgress];

        // [v12.18.1-DEV] 변동성 적응 임계 — 가장 최근 국면/임계가 바뀐 종목 1개(종목별 전체는 엔진 판독) · 미결선 = Regime Unbound.
        public bool CalibrationBound;
        public int Regime = DashRegime.Unbound, RequiredConfidenceBp, BaseConfidenceBp;
        public long CalibrationSymbol, CalibrationUpdates;
        /// <summary>[방향 B] 1분 승인 시그널 수 · 소프트/하드 한도 · 억제/붕괴 누적(미결선 = ChurnBound false).</summary>
        public bool ChurnBound;
        public long ChurnWindow, ChurnSuppressed, ChurnBreakdowns;
        public int ChurnSoft, ChurnHard;

        // [v12.18.2-DEV] 호스트 시그널 게이트 — 모드 · 관측 제안 수 · 국면 임계 거부(강제) · 그림자 거부(관측 판정) · 승인 억제 거부 · 끄기 거부(키 켜짐).
        public int GateMode = DashGateMode.Unbound;
        public long GateObserved, GateRegimeRejects, GateShadowRejects, GateShadowRegime, GateChurnRejects, GateDisableRefused;

        // [v12.18.3-DEV] 감사 원장 — 상태(DashAuditLedger) · 레코드 수/용량 · 사용률 · 동결 원인 · 머리 해시 상위 8바이트(빅 엔디언).
        public int AuditState = DashAuditLedger.Unbound, AuditCount, AuditCapacity, AuditUsedPercent, AuditFreezeCause;
        public ulong AuditHeadPrefix;

        // [v12.18.6-DEV] 마이크로초 지터 센티널 — 상태(DashJitter) · 임계 초과 누적/GC 누적 · 최대 · 마지막 스파이크 · 최악 K 표본 요약(개수 · K 번째 · GC 태그).
        public int JitterState = DashJitter.Unbound, JitterWorstCount, JitterWorstGcCount;
        public long JitterSpikes, JitterGcSpikes, JitterMaxNanos, JitterLastSpikeNanos, JitterWorstFloorNanos;

        // [v12.18.7-DEV] 브로커 세션 복구 상태(결선된 관리자만 · 브로커 인덱스 순).
        public readonly DashBrokerSession[] Sessions = new DashBrokerSession[MaxBrokers];
        public int SessionCount;

        // [v12.18.10-DEV] 브로커 생존성 사전 진단 — 상태(DashPreFlight) · 레인 판정(키움 · KIS) · 소요 µs · 래치 누계 · 진단 후 핑퐁 최근/최대 µs(키움 · KIS).
        public int PreFlightState = DashPreFlight.Unbound, PreFlightKiwoom, PreFlightKis;
        public long PreFlightElapsedMicros, PreFlightLatches, PreFlightPingKiwoomMicros, PreFlightPingKisMicros, PreFlightPingMaxMicros;

        // [v12.18.16-DEV] KIS 시세 지연 관제 — 상태(DashKisFeed) · 지연 하한/상한/최대 하한(ms) · 위반/격발 누계 · 마지막 격발 사유.
        public int KisFeedState = DashKisFeed.Unbound, KisFeedTripReason;
        public long KisLagLowerMs, KisLagUpperMs, KisLagMaxMs, KisFeedViolations, KisFeedTrips;

        /// <summary>비트 이름(허브가 기동 시 1회 채움 · 이후 불변).</summary>
        public string[] BitNames = Array.Empty<string>();

        public double WinRate => Wins + Losses == 0 ? 0 : (double)Wins / (Wins + Losses);
        public bool IsRaised(int bit) => (uint)bit < 64 && (Mask & (1L << bit)) != 0;
        public bool IsRequired(int bit) => (uint)bit < 64 && (RequiredMask & (1L << bit)) != 0;
        public bool IsBound(int bit) => (uint)bit < 64 && (BoundMask & (1L << bit)) != 0;
        public string BitName(int bit) => (uint)bit < (uint)BitNames.Length && BitNames[bit] is { Length: > 0 } n ? n : "bit" + bit;
    }

    public interface IPaperDashboardPort
    {
        /// <summary>최신 게시 프레임 버전(0 = 아직 없음).</summary>
        long Version { get; }
        /// <summary>최신 프레임 리스(없으면 null). 반드시 Release 로 반납 — 리스 중 허브는 그 버퍼를 덮어쓰지 않는다.</summary>
        PaperDashboardFrame? Acquire();
        void Release(PaperDashboardFrame frame);
        /// <summary>새 프레임 게시(허브 펌프 스레드 · 인자 = 버전). 구독자 예외는 허브가 삼켜 계수(펌프 무영향).</summary>
        event Action<long>? FrameReady;
        /// <summary>허브 가동 여부(미가동 = 피드 미결선 · UI 는 정직하게 '미연결' 렌더).</summary>
        bool Running { get; }
    }
}
