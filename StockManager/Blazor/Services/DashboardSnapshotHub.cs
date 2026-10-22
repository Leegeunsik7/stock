// ============================================================================
// DashboardSnapshotHub — [대시보드 · 추천 A] AI 모의투자 실시간 대시보드 텔레메트리 허브(무할당 수집 + 50ms 3중 버퍼 스냅샷)
//
//  ★전제(정직): 렌더 병합기(선행/후행 에지 · 버전 변경 감지 · 부하 적응 창)는 이미 MS2E.UI.Portable.Infrastructure.SnapshotRenderGovernor
//    로 존재한다(시세 하드닝 §2). 이 허브는 그 앞단 — '무엇을 그릴지'를 매매 경로에 간섭 없이 모으고 묶는 부분 — 을 채운다.
//
//  ① 생산자(임의 스레드 · δ=0 · wait-free): OnFill · OnTick · PublishDecision · OnTransition(인터락 비트 전이 · 포렌식 기록기 체인).
//     전부 고정 링(TelemetryRing — 슬롯 claim = Interlocked.Increment 1회 + 시퀀스 게시)에 구조체로 적재하고 즉시 반환한다.
//     락 · 대기 · UI 호출 · 예외 전파 0 — 생산자(브리지 수신 · WS 수신 · 인터락 Trip 스레드)는 대시보드 존재를 느끼지 못한다.
//  ② 펌프(전용 타이머 스레드 · 50ms · 단일 진입): 링 커서부터 소비 → 모의 포지션 원장(가중평단 · 실현손익 · 승/패) → 시가평가 ·
//     자산곡선(1초 표본 · 600점) · MDD → 인터락 마스크/요구/결속 · 자식 슬롯(매트릭스 seqlock 판독) · 브로커 한도 → 3중 버퍼 중
//     '게시본도 리스도 아닌' 버퍼에 조립(재사용 · 할당 0) → 게시 → FrameReady(버전) 구독자 통지(구독자 예외 = 계수).
//  ③ 소비자(UI): Acquire → 렌더 → Release. 리스 중 버퍼는 덮어쓰지 않는다(찢어진 프레임 0). 3버퍼가 모두 묶이면 그 회차 조립 생략(계수).
//  ★Zero UI Interference: 생산자 → 링(wait-free) │ 링 → 펌프(별도 스레드) │ 펌프 → UI(InvokeAsync 는 UI 몫). 어느 화살표도 역방향
//    대기가 없다 — UI 가 멈추거나 예외를 던져도 최악은 '펌프 1회차 지연'이며 OrderDrainWorker · 브로커 경로와 공유 상태 0.
//    링이 렌더보다 빨리 차면 오래된 표시 항목을 덮어쓴다(FillsLost 등 계수) — 표시 전용 유실이며 매매 데이터와 무관.
//  손익은 체결 피드 기준 모의 원장(수수료 · 세금 · 슬리피지 미반영) — 실계좌 평가가 아니다(UI 에 명시).
//  [v12.17.5-DEV · 주문 경로 지연 관제] 선택 결선 OrderPathStageProfiler(orderPath): 펌프가 새 부모가 완료된 회차에만 최근 창(≤ 1,000 부모)을
//   무대기 복사(CopyRecent) → 구간 3개 LatencyHistogram(고정 인라인 버킷 · 방향 A) 재적재 → p50/p99/최대 · 시그널 → 발주 추이 64점을 프레임에 싣는다.
//   계측기는 드레인 스레드가 단일 작성자로 쓰고 펌프는 읽기만 한다 — 드레인이 펌프를 기다리는 경로 0(Zero UI Interference).
//   [방향 B] LatencySpikeDegradationGuard: 창 p99 > 1ms = 게시 주기 50 → 200ms 감속(펌프 타이머 주기도 함께 · 스레드 풀 부하 완화) ·
//   정체 재관측 없이 2초 = 복귀. 감속 중 보류된 변경은 다음 허가 회차에 반드시 게시(유실 0).
//   [v12.17.6] 호스트 결선 완료(BlazorHomeForm — 링 발행/종결 관측자 + 게이트/출구 데코레이터). liveArmed:false 기본 상태에선 디스패처가
//   무장 게이트에서 전부 차단하므로 출구 도달 0 = '계측 대기 · 발행 N · 회수 N' 렌더(숫자 날조 0 · 계측 사슬이 살아 있음을 정직하게 표시).
//  [v12.18.6-DEV] 선택 결선 MicrosecondJitterSentinel(AttachJitter): 펌프가 회차마다 센티널 seqlock 사본(TryReadSnapshot · 잠금/대기/할당 0)을
//   판독 → 임계 초과 누적이 늘면 5초간 스파이크(마지막 스파이크 GC 태그 = GC 유발) 배지 · 이후 정상 복귀. 경합 회차 = 직전 사본 유지(계수).
//   센티널 작성자(측정 스레드)는 허브를 모른다 — 게시는 작성자의 드문 경로에서만 일어나므로 측정 핫패스 추가 비용 0.
//  [v12.18.6-DEV · 펌프 지터] 호스트 실 지터 생산자 = 이 펌프 자신(AttachPumpJitter): 회차 실행 소요(PumpCore 전체 — 링 소비 · 조립 · 게시 · 통지)를
//   Begin/End(Stopwatch.GetTimestamp + Gen0 수집 차 · 할당 0)로 재서 호스트 전용 센티널에 기록 + 같은 센티널을 AttachJitter 결선.
//   작성자 = 펌프(단일 진입 CAS 로 직렬화 — 센티널 단일 작성자 계약 충족). 이번 회차 측정은 다음 회차 판독에 반영(50ms 지연 · 정직).
//   Dispose = 진행 중 회차 종료 대기 후 펌프 영구 봉인 → 그 뒤 센티널 폐기(NativeMemory 해제)와 경합 0.
// ============================================================================
using System;
using System.Diagnostics;
using System.Threading;
using MS2E.Core;
using MS2E.Infrastructure;
using MS2E.UI.Portable.Ports.Trading;
using StockManager.Adapters;
using StockManager.Engines;
using StockManager.Telemetry;

namespace StockManager.Blazor.Services
{
    /// <summary>
    /// 다중 생산자 · 단일 소비자 고정 링(δ=0). Publish = wait-free(슬롯 claim 1회 + 시퀀스 게시). 소비자는 위치 p 의 시퀀스가 p+1 일 때만 채택,
    /// 더 크면 덮어써짐(유실 계수). ★표시 전용: 용량만큼 앞선 작성자가 같은 슬롯을 동시에 쓰는 극단(작성 중 선점 + 링 1바퀴)에선 그 항목이
    /// 유실/찢김으로 보일 수 있다 — 매매 경로 자료구조로 쓰지 않는다.
    /// </summary>
    public sealed class TelemetryRing<T> where T : unmanaged
    {
        private readonly T[] _items;
        private readonly long[] _seq;
        private readonly int _mask;
        private long _head;

        public TelemetryRing(int capacityPow2)
        {
            if (capacityPow2 < 2 || (capacityPow2 & (capacityPow2 - 1)) != 0) throw new ArgumentOutOfRangeException(nameof(capacityPow2));
            _items = new T[capacityPow2]; _seq = new long[capacityPow2]; _mask = capacityPow2 - 1;
        }

        public int Capacity => _items.Length;
        public long Head => Volatile.Read(ref _head);

        public void Publish(in T item)
        {
            long i = Interlocked.Increment(ref _head) - 1;
            int idx = (int)(i & _mask);
            Volatile.Write(ref _seq[idx], 0);                                       // 작성 중
            _items[idx] = item;
            Volatile.Write(ref _seq[idx], i + 1);                                   // 게시
        }

        /// <summary>위치 p 판독: 1 = 채택 · 0 = 아직 미게시(다음 회차) · −1 = 덮어써짐(유실).</summary>
        public int TryRead(long p, out T item)
        {
            int idx = (int)(p & _mask);
            long s1 = Volatile.Read(ref _seq[idx]);
            item = default;
            if (s1 > p + 1) return -1;
            if (s1 != p + 1) return 0;
            item = _items[idx];
            Interlocked.MemoryBarrier();
            return Volatile.Read(ref _seq[idx]) == p + 1 ? 1 : -1;
        }
    }

    public sealed class DashboardSnapshotHub : IPaperDashboardPort, IInterlockTransitionSink, StockManager.Engines.IDecisionSink, IDisposable
    {
        public const int DefaultPeriodMs = 50;
        public const int Brokers = PaperDashboardFrame.MaxBrokers;

        private readonly ArmingInterlockBitmask? _mask;
        private readonly ChildSlotTrackingMatrix? _matrix;
        private readonly IBrokerAccountView?[] _views;
        private readonly Func<long> _nowTicks;
        private readonly int _equityEvery;

        // ── 생산자 링 ──
        private readonly TelemetryRing<DashFill> _fills = new(1024);
        private readonly TelemetryRing<DashDecision> _decisions = new(512);
        private readonly TelemetryRing<DashInterlockEvent> _events = new(256);

        // ── 시가(틱) 표: 개방 주소 · CAS 키 claim · 가격 Volatile ──
        private const int PriceSlots = 512;
        private readonly long[] _pxKey = new long[PriceSlots], _pxVal = new long[PriceSlots];
        private long _ticksSeen, _priceTableFull;

        // ── 펌프 측 상태(단일 스레드) ──
        private long _fillCursor, _decisionCursor, _eventCursor, _fillsLost, _decisionsLost, _eventsLost, _fillsSeen;
        private const int MaxPositions = 256;
        private readonly long[] _posSym = new long[MaxPositions], _posQty = new long[MaxPositions], _posAvg = new long[MaxPositions];
        private int _posCount;
        private long _realized, _peak, _mdd;
        private int _wins, _losses;
        private readonly DashFill[] _recentFills = new DashFill[PaperDashboardFrame.MaxFills];
        private readonly DashDecision[] _recentDecisions = new DashDecision[PaperDashboardFrame.MaxDecisions];
        private readonly DashInterlockEvent[] _recentEvents = new DashInterlockEvent[PaperDashboardFrame.MaxEvents];
        private readonly DashEquityPoint[] _equity = new DashEquityPoint[PaperDashboardFrame.MaxEquity];
        private long _recentFillN, _recentDecisionN, _recentEventN, _equityN, _pumps;
        private readonly StockManager.Engines.ChildSlot[] _childBuf = new StockManager.Engines.ChildSlot[PaperDashboardFrame.MaxChildren];
        private readonly long[] _childParent = new long[PaperDashboardFrame.MaxChildren];
        private long _lastSignature = long.MinValue;
        private bool _pendingDirty;

        // ── 3중 버퍼 ──
        private readonly PaperDashboardFrame[] _frames = new PaperDashboardFrame[3];
        private readonly int[] _leases = new int[3];
        private PaperDashboardFrame? _front;
        private long _version;

        // ── 구독자(고정 배열 · 호출 δ=0) ──
        private readonly object _subGate = new();
        private Action<long>?[] _subs = new Action<long>?[8];
        private int _pumping, _running;
        private long _pumpFaults, _subscriberFaults, _buildSkipped, _published;
        private System.Threading.Timer? _timer;

        // ── [v12.17.5-DEV] 주문 경로 지연(선택 결선 · 펌프 단일 스레드) ──
        private readonly OrderPathStageProfiler? _orderPath;
        private readonly long[] _lat0 = new long[OrderPathStageProfiler.RecentWindow], _lat1 = new long[OrderPathStageProfiler.RecentWindow],
                                _lat2 = new long[OrderPathStageProfiler.RecentWindow];
        private LatencyHistogram _h0, _h1, _h2;
        private readonly DashLatencyStage[] _latStages = new DashLatencyStage[DashLatencyGrade.StageCount];
        private long _latSeen = -1;
        private int _latCount;
        private readonly LatencySpikeDegradationGuard _spikeGuard;
        private int _basePeriodMs = DefaultPeriodMs;

        // ── [v12.18.1-DEV] 변동성 적응 임계(선택 결선) — 생산자 = 시그널 엔진 OnBook 스레드 · 1 원자 long(종목 << 24 | 국면 << 16 | bp) ──
        private StockManager.Engines.VolatilityAdaptiveCalibrator? _calibrator;
        private StockManager.Engines.SignalChurnCircuitBreaker? _churn;
        private long _calPacked, _calUpdates;
        private StockManager.Engines.SignalGateRuntimeSwitch? _gateSwitch;
        private StockManager.Security.MonotonicSettingAuditLog? _auditLedger;

        // ── [v12.18.6-DEV] 마이크로초 지터 센티널(선택 결선 · 펌프 단일 스레드 상태) ──
        private MicrosecondJitterSentinel? _jitter;
        private JitterSnapshot _jitSnap;
        private long _jitSeenSpikes = -1, _jitSpikeAt = long.MinValue, _jitterContended;
        // ── [v12.18.6-DEV · 펌프 지터] 펌프 실행 소요 계측(작성자 = 펌프 · 단일 진입) ──
        /// <summary>호스트 펌프 센티널 표본의 구간 종류(JitterSpike.Kind).</summary>
        public const byte PumpJitterKind = 1;
        private MicrosecondJitterSentinel? _pumpJitter;
        // ── [v12.18.7-DEV] 브로커 세션 복구 관리자(선택 결선 · 인덱스 = 브로커) ──
        private readonly SessionRecoveryManager?[] _sessions = new SessionRecoveryManager?[Brokers];
        private readonly SessionRecoverySnapshot[] _sesSnap = new SessionRecoverySnapshot[Brokers];
        // ── [v12.18.10-DEV] 브로커 생존성 사전 진단(선택 결선 · 펌프가 Interlocked 판독만 · 할당 0) ──
        private BrokerLivenessPreFlight? _preFlight;
        private IKisFeedLatencyPort? _kisFeed;                                     // [v12.18.16-DEV] KIS 시세 지연 관제(값 판독 전용)
        private KisFeedLatencyStatus _kisSnap;
        private StockManager.Engines.Arbitrage.SyntheticSpreadRadar? _arb;          // [v12.18.19-DEV] 멀티 브로커 합성 스프레드(펌프 = 스파크 표본 + 값 판독)
        private ArbitrageRadarStatus _arbSnap;
        // ── [v12.18.21-DEV] 브로커 레이트 게이트(레인 0 키움 · 1 KIS · 판독 전용 · 펌프 δ=0) ──
        public const int RateLaneKiwoom = 0, RateLaneKis = 1;
        private readonly MS2E.Infrastructure.LockFreeTokenBucket?[] _rateGate = new MS2E.Infrastructure.LockFreeTokenBucket?[2];
        private readonly MS2E.Infrastructure.BrokerBackoffController?[] _rateBackoff = new MS2E.Infrastructure.BrokerBackoffController?[2];
        private readonly int[] _rateRemaining = new int[2];
        private readonly long[] _rateRejected = new long[2], _rateConsecutive = new long[2];
        private readonly bool[] _rateLatched = new bool[2];
        // [v12.18.22-DEV] 레인 상태(DashRateGate) · 60초 잔여 · 백오프 남은 ms · 키움 조회 잔여(1초/60초). 키움 레인 = 프록시 MMF 판독자(우선) 또는 동일 프로세스 게이트.
        private readonly int[] _rateState = new int[2], _rateRemaining60 = new int[2];
        private readonly long[] _rateBackoffMs = new long[2];
        private int _rateKiwoomQuery1, _rateKiwoomQuery60;
        private MS2E.Infrastructure.KiwoomRateTelemetryReader? _kiwoomRateReader;
        // ── [v12.18.11-DEV] AI vs Human 아레나(선택 결선 · 펌프가 Step 구동 · 생산자는 결정 분기만) ──
        private StockManager.Engines.Arena.ArenaMatchGovernor? _arena;
        private StockManager.Engines.Arena.ArenaTickReplayEngine? _arenaReplay;
        // [v12.18.60-DEV] 국내 대형주 아레나 시세 주입기(선택 결선 · 펌프가 Pump 구동 = 엔진 단일 작성자)
        private StockManager.Engines.Arena.Domestic.KrxArenaQuoteFeeder? _krxArena;

        /// <param name="mask">무장 집계어(인터락 LED · null = 미표시).</param>
        /// <param name="matrix">자식 슬롯 매트릭스(분할 시각화 · null = 미표시).</param>
        /// <param name="views">브로커별 계좌 뷰(인덱스 = 브로커 · null 슬롯 = 미결선).</param>
        /// <param name="equitySampleEveryPumps">자산곡선 표본 간격(펌프 회수 · 기본 20 = 1초).</param>
        /// <param name="orderPath">[v12.17.5-DEV] 주문 경로 구간 계측기(null = 지연 미계측 렌더).</param>
        /// <param name="spikeGuard">[방향 B] 미세 정체 감속 가드(null = 기본 1ms · 50/200ms · 복귀 2초).</param>
        public DashboardSnapshotHub(ArmingInterlockBitmask? mask = null, ChildSlotTrackingMatrix? matrix = null, IBrokerAccountView?[]? views = null,
                                    Func<long>? nowTicks = null, int equitySampleEveryPumps = 20,
                                    OrderPathStageProfiler? orderPath = null, LatencySpikeDegradationGuard? spikeGuard = null)
        {
            _mask = mask; _matrix = matrix; _views = views ?? Array.Empty<IBrokerAccountView?>();
            _orderPath = orderPath;
            _spikeGuard = spikeGuard ?? new LatencySpikeDegradationGuard();
            _nowTicks = nowTicks ?? (static () => DateTime.UtcNow.Ticks);
            _equityEvery = Math.Max(1, equitySampleEveryPumps);
            var names = new string[PaperDashboardFrame.MaxBits];                   // 비트 이름(콜드 1회 · 프레임 공유 · 불변)
            foreach (ArmingInterlockSource s in Enum.GetValues<ArmingInterlockSource>())
                if ((int)s < names.Length) names[(int)s] = s.ToString();
            for (int k = 0; k < 3; k++) _frames[k] = new PaperDashboardFrame { BufferSlot = k, BitNames = names };
        }

        // ════════════ ① 생산자(δ=0 · wait-free) ════════════

        /// <summary>체결 1건(체결량 > 0 만 · 접수/취소/거부 통보 무시). broker 0 키움 · 1 KIS · 2 모의.</summary>
        public void OnFill(in FillPayload f, int broker)
        {
            if (f.FillQty <= 0 || f.Side is not (1 or -1) || !double.IsFinite(f.FillPrice) || f.FillPrice <= 0) return;
            _fills.Publish(new DashFill { Ticks = _nowTicks(), Symbol = f.SymbolCode, Qty = f.FillQty, PriceWon = (long)Math.Round(f.FillPrice), Side = f.Side, Broker = broker });
        }

        /// <summary>시세 1틱(시가평가용 최신가). 표가 차면 신규 종목은 미반영(계수 · 생산자 무대기).</summary>
        public void OnTick(long symbol, long priceWon)
        {
            Interlocked.Increment(ref _ticksSeen);
            if (symbol <= 0 || priceWon <= 0) return;
            int start = (int)((ulong)(symbol * unchecked((long)0x9E3779B97F4A7C15)) >> 55);   // 상위 9비트 = 0..511
            for (int probe = 0; probe < PriceSlots; probe++)
            {
                int i = (start + probe) & (PriceSlots - 1);
                long k = Volatile.Read(ref _pxKey[i]);
                if (k == symbol) { Volatile.Write(ref _pxVal[i], priceWon); return; }
                if (k == 0)
                {
                    long prev = Interlocked.CompareExchange(ref _pxKey[i], symbol, 0);
                    if (prev == 0 || prev == symbol) { Volatile.Write(ref _pxVal[i], priceWon); return; }
                }
            }
            Interlocked.Increment(ref _priceTableFull);
        }

        /// <summary>[방향 B] AI 판단 근거 1건(코드 · 수치만 — 문자열 0). kind = DashDecisionKind · reason = 전략 정의 코드(또는 인터락 비트).</summary>
        public void PublishDecision(int kind, long symbol, long scaleBp, long volatilityBp, long value, int reason)
        {
            _decisions.Publish(new DashDecision { Ticks = _nowTicks(), Kind = kind, Symbol = symbol, ScaleBp = scaleBp, VolatilityBp = volatilityBp, Value = value, Reason = reason });
            Volatile.Read(ref _arena)?.OnAiDecision(kind, symbol);                 // [v12.18.11-DEV] 아레나 AI 진영(링 적재만 · δ=0)
        }

        /// <summary>[v12.18.1-DEV] 캘리브레이터 · 승인 서킷 브레이커 결선(호스트 조립 1회 — 허브가 시그널 엔진보다 먼저 생성되므로 사후 결선).</summary>
        public void AttachCalibration(StockManager.Engines.VolatilityAdaptiveCalibrator calibrator, StockManager.Engines.SignalChurnCircuitBreaker? churn = null)
        {
            Volatile.Write(ref _churn, churn);
            Volatile.Write(ref _calibrator, calibrator ?? throw new ArgumentNullException(nameof(calibrator)));
        }

        /// <summary>[v12.18.2-DEV] 시그널 게이트 런타임 토글 결선(모드 · 거부 계수 관제).</summary>
        public void AttachSignalGate(StockManager.Engines.SignalGateRuntimeSwitch gateSwitch)
            => Volatile.Write(ref _gateSwitch, gateSwitch ?? throw new ArgumentNullException(nameof(gateSwitch)));

        /// <summary>[v12.18.3-DEV] 감사 원장 관제 결선 — 프레임마다 seqlock 스냅샷(원장 체인 잠금 미사용 · 원장 영역 미접근 · 할당 0).</summary>
        public void AttachAuditLedger(StockManager.Security.MonotonicSettingAuditLog ledger)
            => Volatile.Write(ref _auditLedger, ledger ?? throw new ArgumentNullException(nameof(ledger)));

        /// <summary>
        /// [v12.18.6-DEV] 마이크로초 지터 센티널 관제 결선 — 펌프는 seqlock 사본만 읽는다(작성자 스레드 · 링 · 꼬리 미접근 · 할당 0).
        /// 결선 시점에 이미 쌓인 스파이크는 '최근'으로 보지 않는다(첫 판독 = 기준선).
        /// </summary>
        public void AttachJitter(MicrosecondJitterSentinel sentinel)
            => Volatile.Write(ref _jitter, sentinel ?? throw new ArgumentNullException(nameof(sentinel)));

        /// <summary>
        /// [v12.18.6-DEV · 펌프 지터] 펌프 실행 소요를 센티널에 기록(작성자 = 펌프) + 같은 센티널을 AttachJitter 결선(호스트 실 지터 소스).
        /// ★센티널은 이 허브 전용이어야 한다(다른 작성자 금지 — 단일 작성자 계약). 폐기는 허브 Dispose 뒤.
        /// </summary>
        public void AttachPumpJitter(MicrosecondJitterSentinel sentinel)
        {
            Volatile.Write(ref _pumpJitter, sentinel ?? throw new ArgumentNullException(nameof(sentinel)));
            AttachJitter(sentinel);
        }

        // [v12.18.10-DEV] 사전 진단 투영(δ=0 — 원시 정수만 · 진단 중 = 레인 '판정 중' 그대로 렌더).
        private void ProjectPreFlight(PaperDashboardFrame f)
        {
            var pf = Volatile.Read(ref _preFlight);
            f.PreFlightState = DashPreFlight.Classify(pf is not null, pf is null ? 0 : (int)pf.State);
            if (pf is null)
            {
                f.PreFlightKiwoom = f.PreFlightKis = DashPreFlight.LaneUnbound;
                f.PreFlightElapsedMicros = f.PreFlightLatches = f.PreFlightPingKiwoomMicros = f.PreFlightPingKisMicros = f.PreFlightPingMaxMicros = 0;
                return;
            }
            f.PreFlightKiwoom = (int)pf.Verdict(BrokerLivenessPreFlight.LaneKiwoom);
            f.PreFlightKis = (int)pf.Verdict(BrokerLivenessPreFlight.LaneKis);
            f.PreFlightElapsedMicros = pf.ElapsedMicros;
            f.PreFlightLatches = pf.Latches;
            f.PreFlightPingKiwoomMicros = pf.PingLastMicros(BrokerLivenessPreFlight.LaneKiwoom);
            f.PreFlightPingKisMicros = pf.PingLastMicros(BrokerLivenessPreFlight.LaneKis);
            f.PreFlightPingMaxMicros = Math.Max(pf.PingMaxMicros(BrokerLivenessPreFlight.LaneKiwoom), pf.PingMaxMicros(BrokerLivenessPreFlight.LaneKis));
        }

        /// <summary>[v12.18.7-DEV] 브로커 세션 복구 관리자 관제 결선(인덱스 = manager.Broker · 펌프가 Snapshot() 락 1회 판독 · 할당 0).</summary>
        public void AttachSessionRecovery(SessionRecoveryManager manager)
        {
            ArgumentNullException.ThrowIfNull(manager);
            if ((uint)manager.Broker >= (uint)Brokers) throw new ArgumentOutOfRangeException(nameof(manager), "브로커 인덱스 > " + (Brokers - 1));
            Volatile.Write(ref _sessions[manager.Broker], manager);
        }

        /// <summary>[v12.18.10-DEV] 브로커 생존성 사전 진단 관제 결선(판독 전용 — 진단/래치 경로 무간섭 · 펌프 무할당 투영).</summary>
        public void AttachPreFlight(BrokerLivenessPreFlight preFlight)
            => Volatile.Write(ref _preFlight, preFlight ?? throw new ArgumentNullException(nameof(preFlight)));

        /// <summary>[v12.18.16-DEV] KIS 시세 지연 관제 결선 — 펌프는 값 구조체 사본만 읽는다(래치/재무장 경로 무간섭 · 할당 0).</summary>
        public void AttachKisFeed(IKisFeedLatencyPort feed)
            => Volatile.Write(ref _kisFeed, feed ?? throw new ArgumentNullException(nameof(feed)));

        /// <summary>[v12.18.19-DEV] 아비트라지 레이더 결선 — 펌프가 회차마다 스파크라인 1표본(δ=0) + 상태 값 사본 투영(래치/재무장 경로 무간섭).</summary>
        public void AttachArbitrage(StockManager.Engines.Arbitrage.SyntheticSpreadRadar radar)
            => Volatile.Write(ref _arb, radar ?? throw new ArgumentNullException(nameof(radar)));

        /// <summary>[v12.18.21-DEV] 브로커 레이트 게이트 관제 결선(레인 0 키움 · 1 KIS) — 펌프는 잔여 허가 · 거부 누계 · 백오프 상태만 판독(게이트 경로 무간섭).</summary>
        public void AttachRateGate(int lane, MS2E.Infrastructure.LockFreeTokenBucket gate, MS2E.Infrastructure.BrokerBackoffController? backoff)
        {
            if ((uint)lane > RateLaneKis) throw new ArgumentOutOfRangeException(nameof(lane));
            Volatile.Write(ref _rateBackoff[lane], backoff);
            Volatile.Write(ref _rateGate[lane], gate ?? throw new ArgumentNullException(nameof(gate)));
        }

        // 키움 MMF 관제 1회 판독(δ=0 · 블로킹 0). Live 만 값을 갱신 — 부재/찢김/노후는 상태만 바꾸고 잔여량을 0 으로 비운다(낡은 '여유' 표시 금지).
        private void PumpKiwoomRateTelemetry(MS2E.Infrastructure.KiwoomRateTelemetryReader r)
        {
            const int K = RateLaneKiwoom;
            var res = r.TryRead(out var s);
            if (res == MS2E.Infrastructure.KiwoomRateTelemetryRead.Live)
            {
                _rateRemaining[K] = s.OrderRemaining1s; _rateRemaining60[K] = s.OrderRemaining60s;
                _rateKiwoomQuery1 = s.QueryRemaining1s; _rateKiwoomQuery60 = s.QueryRemaining60s;
                _rateRejected[K] = s.Rejected; _rateConsecutive[K] = s.Consecutive; _rateLatched[K] = s.Latched;
                _rateBackoffMs[K] = s.BackoffMicros / 1000;
                _rateState[K] = !s.GateAttached ? DashRateGate.Waiting : s.Latched ? DashRateGate.Latched : _rateBackoffMs[K] > 0 ? DashRateGate.Backoff : DashRateGate.Live;
                return;
            }
            _rateRemaining[K] = 0; _rateRemaining60[K] = 0; _rateKiwoomQuery1 = 0; _rateKiwoomQuery60 = 0; _rateBackoffMs[K] = 0;
            _rateState[K] = res == MS2E.Infrastructure.KiwoomRateTelemetryRead.Stale ? DashRateGate.Stale
                          : res == MS2E.Infrastructure.KiwoomRateTelemetryRead.Torn ? DashRateGate.Stale : DashRateGate.Waiting;
            if (res == MS2E.Infrastructure.KiwoomRateTelemetryRead.Stale) { _rateRejected[K] = s.Rejected; _rateLatched[K] = s.Latched; _rateConsecutive[K] = s.Consecutive; }   // 누계 · 래치는 마지막 값 유지(노후 표기)
        }

        /// <summary>[v12.18.22-DEV] 키움 레인 = x86 프록시 MMF 관제 판독자(펌프 회차마다 1회 판독 · 블로킹 0 · 부재/찢김/노후 = 상태로 정직 렌더).</summary>
        public void AttachKiwoomRateTelemetry(MS2E.Infrastructure.KiwoomRateTelemetryReader reader)
            => Volatile.Write(ref _kiwoomRateReader, reader ?? throw new ArgumentNullException(nameof(reader)));

        /// <summary>
        /// [v12.18.11-DEV] AI vs Human 아레나 결선 — 펌프가 회차마다 Step(가상 체결 · 정산 · seqlock 게시 · δ=0)을 구동하고
        /// 시그널 엔진 결정(진입/청산)을 AI 진영 링에 분기한다. 아레나는 브로커 경로가 없다(가상 원장 전용 · 대시보드 프레임 무간섭).
        /// </summary>
        public void AttachArena(StockManager.Engines.Arena.ArenaMatchGovernor arena)
            => Volatile.Write(ref _arena, arena ?? throw new ArgumentNullException(nameof(arena)));

        /// <summary>
        /// [v12.18.12-DEV] 아레나 틱 리플레이 결선 — 펌프는 50ms 마다 게시 요청 플래그만 세운다(원장 작성자 = 재생 스레드 단일 · 이중 작성 0).
        /// 재생 스레드가 다음 회차에 seqlock 스냅샷을 게시 → 배속과 무관하게 UI 공급 주기 = 허브 펌프 주기.
        /// </summary>
        public void AttachArenaReplay(StockManager.Engines.Arena.ArenaTickReplayEngine replay)
            => Volatile.Write(ref _arenaReplay, replay ?? throw new ArgumentNullException(nameof(replay)));

        /// <summary>
        /// [v12.18.60-DEV] 국내 대형주 아레나 결선 — 펌프가 회차마다 주입기 Pump(브리지 최신 호가 슬롯 → 엔진 OnQuote · 합성 시세 1스텝 · 휴먼 큐 드레인)를
        /// 구동한다(엔진 작성자 = 펌프 단일 · δ=0). 가상 원장 전용 · 브로커 경로 0.
        /// </summary>
        public void AttachKrxArena(StockManager.Engines.Arena.Domestic.KrxArenaQuoteFeeder feeder)
            => Volatile.Write(ref _krxArena, feeder ?? throw new ArgumentNullException(nameof(feeder)));

        /// <summary>[v12.18.1-DEV] 국면 · 요구 확신 변경(시그널 엔진 · 변경 시에만 · δ=0). 종목 코드 &gt; 2^39 = 0 으로 표시(KRX 6자리는 항상 안).</summary>
        public void PublishCalibration(long symbol, int regime, int requiredBp)
        {
            long sym = (ulong)symbol < (1UL << 39) ? symbol : 0;
            Interlocked.Exchange(ref _calPacked, (sym << 24) | ((long)(regime & 0xFF) << 16) | (long)(requiredBp & 0xFFFF));
            Interlocked.Increment(ref _calUpdates);
        }

        /// <summary>인터락 비트 전이(포렌식 기록기 체인 · Trip 스레드 · δ=0). 차단은 이미 반영된 뒤다(관측이 차단을 늦추지 못함).</summary>
        public void OnTransition(long raisedBits, long clearedBits, long maskAfter)
            => _events.Publish(new DashInterlockEvent { Ticks = _nowTicks(), Raised = raisedBits, Cleared = clearedBits, MaskAfter = maskAfter });

        // ════════════ ② 펌프(전용 스레드 · 단일 진입) ════════════

        public bool Running => Volatile.Read(ref _running) == 1;
        public long Pumps => Interlocked.Read(ref _pumps);
        public long PumpFaults => Interlocked.Read(ref _pumpFaults);
        public long SubscriberFaults => Interlocked.Read(ref _subscriberFaults);
        public long BuildSkipped => Interlocked.Read(ref _buildSkipped);
        public long Published => Interlocked.Read(ref _published);
        public long TicksSeen => Interlocked.Read(ref _ticksSeen);
        public long PriceTableFull => Interlocked.Read(ref _priceTableFull);
        public bool OrderPathBound => _orderPath is not null;
        /// <summary>[v12.18.6-DEV] 지터 사본 판독 경합(직전 사본 유지) 누적.</summary>
        public long JitterContended => Interlocked.Read(ref _jitterContended);
        /// <summary>[방향 B] 감속 가드(판독 전용 용도 · 상태 변경은 펌프만).</summary>
        public LatencySpikeDegradationGuard LatencyGuard => _spikeGuard;
        /// <summary>현재 게시 주기(ms) — 감속 중 = max(기본 주기, 감속 주기).</summary>
        public int CurrentPeriodMs => _spikeGuard.Degraded ? Math.Max(_basePeriodMs, _spikeGuard.DegradedPeriodMs) : _basePeriodMs;

        public void Start(int periodMs = DefaultPeriodMs)
        {
            if (Interlocked.Exchange(ref _running, 1) == 1) return;
            _basePeriodMs = periodMs;
            _timer = new System.Threading.Timer(static s => ((DashboardSnapshotHub)s!).Pump(), this, periodMs, periodMs);
        }

        /// <summary>1회차 조립 + 게시(타이머 · 테스트 직접 호출). 반환 = 새 프레임 게시 여부. 예외는 계수만(호출자 전파 0).</summary>
        public bool Pump()
        {
            if (Interlocked.CompareExchange(ref _pumping, 1, 0) != 0) return false;   // 1 = 진행 중 · 2 = 폐기 봉인
            var pj = Volatile.Read(ref _pumpJitter);
            long t0 = MicrosecondJitterSentinel.Begin(out int g0);
            bool published = false;
            try { published = PumpCore(); }
            catch (Exception) { Interlocked.Increment(ref _pumpFaults); }
            finally
            {
                try { pj?.End(t0, g0, PumpJitterKind, 0); } catch (ObjectDisposedException) { Interlocked.Increment(ref _pumpFaults); }
                Volatile.Write(ref _pumping, 0);
            }
            return published;
        }

        private bool PumpCore()
        {
            long t0 = Stopwatch.GetTimestamp();
            _pumps++;
            bool dirty = false;
            // 링 소비
            dirty |= DrainFills();
            dirty |= DrainDecisions();
            dirty |= DrainEvents();
            long nowT = _nowTicks();
            Volatile.Read(ref _arena)?.Step(nowT);                                  // [v12.18.11-DEV] 아레나 정산(자체 seqlock 게시 · 프레임 서명 무관)
            Volatile.Read(ref _arenaReplay)?.OnHubPump();                           // [v12.18.12-DEV] 리플레이 게시 요청(플래그 1워드 · δ=0)
            Volatile.Read(ref _krxArena)?.Pump(nowT);                              // [v12.18.60-DEV] KRX 아레나 시세 주입 · 정산(자체 seqlock 게시)
            dirty |= SampleOrderPath(nowT);
            // 시가평가 · 자산 · MDD
            long unreal = 0; int open = 0;
            for (int i = 0; i < _posCount; i++)
            {
                if (_posQty[i] == 0) continue;
                open++;
                long px = PriceOf(_posSym[i]);
                if (px > 0) unreal += _posQty[i] * (px - _posAvg[i]);
            }
            long equity = _realized + unreal;
            if (equity > _peak) _peak = equity;
            if (_peak - equity > _mdd) _mdd = _peak - equity;
            if (_pumps % _equityEvery == 0) { _equity[_equityN++ % _equity.Length] = new DashEquityPoint { Ticks = _nowTicks(), EquityWon = equity }; dirty = true; }
            // 자식 슬롯 · 한도 · 인터락 서명(변경 감지)
            int childN = _matrix?.CopyChildren(_childBuf, _childParent) ?? 0;
            long sig = childN;
            for (int i = 0; i < childN; i++) sig = sig * 31 + _childBuf[i].FilledQty * 7 + (int)_childBuf[i].State + _childBuf[i].OrderQty;
            long mask = _mask?.Mask ?? 0;
            sig = sig * 31 + mask;
            sig = sig * 31 + (_mask?.BoundMask ?? 0);
            sig = sig * 31 + Interlocked.Read(ref _ticksSeen);
            if (_orderPath is { } opSig) sig = sig * 31 + opSig.EgressForwarded + opSig.EgressVetoed * 7 + opSig.Begun * 13 + opSig.Reclaimed * 17;
            var churn = Volatile.Read(ref _churn);
            sig = sig * 31 + Interlocked.Read(ref _calPacked) + Interlocked.Read(ref _calUpdates) * 7;
            if (churn is not null) sig = sig * 31 + churn.LastWindowCount + churn.Suppressed * 7 + churn.Breakdowns * 13;
            var gs = Volatile.Read(ref _gateSwitch);
            if (gs is not null) sig = sig * 31 + gs.Mode + gs.Signals.ObservedProposals * 7 + gs.Signals.RejectedRegime * 13 + gs.DisableRefused * 17;
            var al = Volatile.Read(ref _auditLedger);
            var aSnap = al?.Snapshot() ?? default;
            if (al is not null) sig = sig * 31 + aSnap.Version + (int)aSnap.FreezeCause * 7 + (aSnap.Contended ? 13 : 0);   // 기록 · 동결 = 즉시 재게시
            var jit = Volatile.Read(ref _jitter);
            bool jitHot = false;
            if (jit is not null)
            {
                if (jit.TryReadSnapshot(out var js)) _jitSnap = js; else Interlocked.Increment(ref _jitterContended);
                long spikes = _jitSnap.SpikeCountOver500Us;
                if (_jitSeenSpikes >= 0 && spikes > _jitSeenSpikes) _jitSpikeAt = nowT;      // 새 스파이크 = 즉시 재게시(서명 변경)
                _jitSeenSpikes = spikes;
                jitHot = _jitSpikeAt != long.MinValue && nowT - _jitSpikeAt < DashJitter.HoldTicks;
                sig = sig * 31 + _jitSnap.Version + (jitHot ? 7 : 0);                        // 유지 만료 = 정상 복귀 재게시
            }
            for (int b = 0; b < _views.Length && b < Brokers; b++)
            {
                var v = _views[b];
                bool bound = false; long room = 0;
                try { bound = v is not null && v.Bound; if (bound) v!.TryGetBuyHeadroomWon(out room); } catch (Exception) { bound = false; }
                sig = sig * 31 + (bound ? room + 1 : 0);
            }
            for (int b = 0; b < Brokers; b++)
            {
                var sm = Volatile.Read(ref _sessions[b]);
                if (sm is null) continue;
                var ss = _sesSnap[b] = sm.Snapshot();                                 // 결선 표지(+1 · 인덱스) = 초기 상태 0 이어도 결선 즉시 재게시
                sig = sig * 31 + 1 + b * 3 + (int)ss.State * 7 + (ss.Exhausted ? 11 : 0) + (ss.Discrepancy ? 13 : 0) + ss.ConsecutiveFailures * 17 + ss.Losses * 19 + ss.Recoveries * 23;
            }
            var kf = Volatile.Read(ref _kisFeed);
            if (kf is not null)
            {
                _kisSnap = kf.Status;                                                 // 상태 · 격발 = 즉시 재게시 · 지연 값은 100ms 단위 변화만(재게시 폭주 방지)
                sig = sig * 31 + 1 + _kisSnap.State * 7 + _kisSnap.Trips * 11 + _kisSnap.LagUpperMs / 100 * 13;
            }
            var ar = Volatile.Read(ref _arb);
            if (ar is not null)
            {
                ar.Sample();                                                          // 50ms 스파크 표본(고정 링 · 할당 0)
                _arbSnap = ar.Status;                                                 // 상태 · 래치 · 기회 = 즉시 재게시 · 스프레드는 1bp 단위 변화만
                sig = sig * 31 + 1 + _arbSnap.State * 7 + _arbSnap.Trips * 11 + _arbSnap.Opportunities * 13 + _arbSnap.SpreadCbp / 100 * 17;
            }
            var krr = Volatile.Read(ref _kiwoomRateReader);
            for (int lane = 0; lane < 2; lane++)
            {
                if (lane == RateLaneKiwoom && krr is not null) { PumpKiwoomRateTelemetry(krr); }
                else
                {
                    var rg = Volatile.Read(ref _rateGate[lane]);
                    if (rg is null) continue;
                    var bo = Volatile.Read(ref _rateBackoff[lane]);
                    _rateRemaining[lane] = rg.RemainingInWindow(0);                   // 최근 N 슬롯 판독(O(N) · 할당 0)
                    _rateRemaining60[lane] = rg.RemainingInWindow(1);
                    _rateRejected[lane] = rg.Denied + (bo is null ? 0 : bo.BackoffDenied + bo.LatchDenied);
                    _rateConsecutive[lane] = bo?.Consecutive ?? 0;
                    _rateLatched[lane] = bo?.Latched ?? false;
                    _rateBackoffMs[lane] = (bo?.RemainingBackoffMicros() ?? 0) / 1000;
                    _rateState[lane] = _rateLatched[lane] ? DashRateGate.Latched : _rateBackoffMs[lane] > 0 ? DashRateGate.Backoff : DashRateGate.Live;
                }
                sig = sig * 31 + 1 + lane * 3 + _rateState[lane] * 5 + _rateRemaining[lane] * 7 + _rateRemaining60[lane] * 19 + _rateRejected[lane] * 11
                    + _rateConsecutive[lane] * 13 + (_rateLatched[lane] ? 17 : 0) + _rateBackoffMs[lane] / 100 * 23;
            }
            // ★서명은 '게시 성공 시에만' 확정 — 조립 생략(3버퍼 리스) 회차의 변경이 소비돼 화면에 영영 반영되지 않는 유실 방지
            if (sig != _lastSignature || _pendingDirty) dirty = true;
            if (!dirty && _front is not null) return false;
            // [방향 B] 미세 정체 감속 중 = 마지막 게시 + 200ms 전에는 보류(변경은 _pendingDirty 로 이월 — 다음 허가 회차에 게시)
            if (_front is not null && !_spikeGuard.AllowPublish(nowT)) { _pendingDirty = true; return false; }

            // 조립 버퍼 선택: 게시본 · 리스 중이 아닌 것
            var front = Volatile.Read(ref _front);
            PaperDashboardFrame? f = null;
            for (int k = 0; k < 3; k++)
                if (!ReferenceEquals(_frames[k], front) && Volatile.Read(ref _leases[k]) == 0) { f = _frames[k]; break; }
            if (f is null) { Interlocked.Increment(ref _buildSkipped); _pendingDirty = true; return false; }

            f.BuiltTicks = _nowTicks();
            f.Mask = mask; f.RequiredMask = _mask?.RequiredMask ?? 0; f.BoundMask = _mask?.BoundMask ?? 0; f.Sealed = _mask?.IsSealed ?? false;
            f.RealizedPnlWon = _realized; f.UnrealizedPnlWon = unreal; f.EquityWon = equity; f.PeakEquityWon = _peak; f.MaxDrawdownWon = _mdd;
            f.Wins = _wins; f.Losses = _losses; f.OpenPositions = open;
            f.TicksSeen = Interlocked.Read(ref _ticksSeen); f.FillsSeen = _fillsSeen; f.FillsLost = _fillsLost; f.DecisionsLost = _decisionsLost; f.EventsLost = _eventsLost;
            f.FillCount = CopyRecent(_recentFills, _recentFillN, f.Fills);
            f.DecisionCount = CopyRecent(_recentDecisions, _recentDecisionN, f.Decisions);
            f.EventCount = CopyRecent(_recentEvents, _recentEventN, f.Events);
            f.EquityCount = CopyChronological(_equity, _equityN, f.Equity);
            f.ChildCount = childN;
            for (int i = 0; i < childN; i++)
            {
                ref readonly var c = ref _childBuf[i];
                f.Children[i] = new DashChild
                {
                    ParentId = _childParent[i], Index = ChildIdSpace.IndexOf(c.ChildId), Broker = c.Broker, State = (int)c.State,
                    OrderQty = c.OrderQty, FilledQty = c.FilledQty, AvgPriceWon = c.FilledQty > 0 ? c.FillValueWon / c.FilledQty : 0,
                };
            }
            int lc = 0;
            for (int b = 0; b < _views.Length && b < Brokers; b++)
            {
                var v = _views[b];
                bool bound = false; long room = 0;
                try { bound = v is not null && v.Bound; if (bound) v!.TryGetBuyHeadroomWon(out room); } catch (Exception) { bound = false; }
                f.Limits[lc++] = new DashBrokerLimit { Broker = b, Bound = bound, HeadroomWon = bound ? room : 0 };
            }
            f.LimitCount = lc;
            int sc = 0;
            for (int b = 0; b < Brokers; b++)
            {
                if (Volatile.Read(ref _sessions[b]) is null) continue;
                ref readonly var ss = ref _sesSnap[b];
                f.Sessions[sc++] = new DashBrokerSession
                {
                    Broker = b, State = DashSession.Classify(true, (int)ss.State, ss.EverConnected, ss.Exhausted, ss.Discrepancy),
                    Failures = ss.ConsecutiveFailures, MaxFailures = ss.MaxFailures, Losses = ss.Losses, Recoveries = ss.Recoveries, Attempts = ss.Attempts,
                };
            }
            f.SessionCount = sc;
            ProjectPreFlight(f);
            var op = _orderPath;
            f.LatencyBound = op is not null;
            for (int k = 0; k < DashLatencyGrade.StageCount; k++) f.LatencyStages[k] = _latStages[k];
            ref readonly var e2e = ref _latStages[DashLatencyGrade.SignalToEgress];
            f.LatencyGrade = DashLatencyGrade.Classify(op is not null, e2e.Count, e2e.P99Nanos);
            f.LatencyOrders = op?.RecentTotal ?? 0; f.LatencyForwarded = op?.EgressForwarded ?? 0; f.LatencyVetoed = op?.EgressVetoed ?? 0;
            f.LatencyPublished = op?.Begun ?? 0; f.LatencyReclaimed = op?.Reclaimed ?? 0;
            int tc = Math.Min(_latCount, PaperDashboardFrame.MaxLatencyTrend);
            Array.Copy(_lat2, _latCount - tc, f.LatencyTrend, 0, tc);
            f.LatencyTrendCount = tc;
            f.LatencyThrottled = _spikeGuard.Degraded; f.PublishPeriodMs = CurrentPeriodMs;
            var cal = Volatile.Read(ref _calibrator);
            long packed = Interlocked.Read(ref _calPacked);
            f.CalibrationBound = cal is not null;
            f.CalibrationUpdates = Interlocked.Read(ref _calUpdates);
            f.BaseConfidenceBp = cal?.BaseBp ?? 0;
            if (cal is null) { f.Regime = DashRegime.Unbound; f.RequiredConfidenceBp = 0; f.CalibrationSymbol = 0; }
            else if (f.CalibrationUpdates == 0) { f.Regime = DashRegime.Warmup; f.RequiredConfidenceBp = cal.MaxBp; f.CalibrationSymbol = 0; }   // 관측 전 = 최대(엔진과 같은 fail-closed)
            else { f.Regime = (int)((packed >> 16) & 0xFF); f.RequiredConfidenceBp = (int)(packed & 0xFFFF); f.CalibrationSymbol = packed >> 24; }
            f.ChurnBound = churn is not null;
            f.ChurnWindow = churn?.LastWindowCount ?? 0; f.ChurnSuppressed = churn?.Suppressed ?? 0; f.ChurnBreakdowns = churn?.Breakdowns ?? 0;
            f.ChurnSoft = churn?.SoftPerMinute ?? 0; f.ChurnHard = churn?.HardPerMinute ?? 0;
            f.GateMode = gs is null ? DashGateMode.Unbound : gs.Mode;
            f.GateObserved = gs?.Signals.ObservedProposals ?? 0; f.GateRegimeRejects = gs?.Signals.RejectedRegime ?? 0;
            f.GateShadowRejects = gs?.Signals.ShadowRejects ?? 0; f.GateShadowRegime = gs?.Signals.ShadowRegimeRejects ?? 0;
            f.GateChurnRejects = gs?.Signals.RejectedChurn ?? 0; f.GateDisableRefused = gs?.DisableRefused ?? 0;
            f.AuditState = al is null ? DashAuditLedger.Unbound : (int)aSnap.State;
            f.AuditCount = aSnap.Count; f.AuditCapacity = aSnap.Capacity; f.AuditUsedPercent = aSnap.UsedPercent;
            f.AuditFreezeCause = aSnap.Contended ? DashAuditLedger.CauseNone : (int)aSnap.FreezeCause; f.AuditHeadPrefix = aSnap.HeadPrefix;
            ref readonly var jv = ref _jitSnap;
            f.JitterState = DashJitter.Classify(jit is not null, jitHot, jv.LastSpikeGcTagged);
            f.JitterSpikes = jv.SpikeCountOver500Us; f.JitterGcSpikes = jv.GcSpikeCount; f.JitterMaxNanos = jv.MaxJitterNanos;
            f.JitterLastSpikeNanos = jv.LastSpikeNanos; f.JitterWorstCount = jv.WorstSampleCount;
            f.JitterWorstFloorNanos = jv.WorstSampleFloorNanos; f.JitterWorstGcCount = jv.WorstSampleGcCount;
            ref readonly var ks = ref _kisSnap;
            f.KisFeedState = kf is null ? DashKisFeed.Unbound : ks.State;
            f.KisLagLowerMs = ks.LagLowerMs; f.KisLagUpperMs = ks.LagUpperMs; f.KisLagMaxMs = ks.MaxLagLowerMs;
            f.KisFeedViolations = ks.Violations; f.KisFeedTrips = ks.Trips; f.KisFeedTripReason = ks.LastTripReason;
            ref readonly var abs = ref _arbSnap;
            f.ArbState = ar is null ? ArbRadar.Unbound : abs.State;
            f.ArbSpreadCbp = abs.SpreadCbp; f.ArbCrossCbp = abs.CrossCbp; f.ArbOpportunities = abs.Opportunities; f.ArbTrips = abs.Trips;
            f.RateKiwoomBound = Volatile.Read(ref _rateGate[RateLaneKiwoom]) is not null; f.RateKisBound = Volatile.Read(ref _rateGate[RateLaneKis]) is not null;
            f.RateKiwoomRemaining = _rateRemaining[RateLaneKiwoom]; f.RateKisRemaining = _rateRemaining[RateLaneKis];
            f.RateKiwoomRejected = _rateRejected[RateLaneKiwoom]; f.RateKisRejected = _rateRejected[RateLaneKis];
            f.RateKiwoomConsecutive = _rateConsecutive[RateLaneKiwoom]; f.RateKisConsecutive = _rateConsecutive[RateLaneKis];
            f.RateKiwoomLatched = _rateLatched[RateLaneKiwoom]; f.RateKisLatched = _rateLatched[RateLaneKis];
            f.RateKiwoomBound |= krr is not null;
            f.RateKiwoomState = f.RateKiwoomBound ? _rateState[RateLaneKiwoom] : DashRateGate.Unbound;
            f.RateKisState = f.RateKisBound ? _rateState[RateLaneKis] : DashRateGate.Unbound;
            f.RateKiwoomRemaining60 = _rateRemaining60[RateLaneKiwoom]; f.RateKisRemaining60 = _rateRemaining60[RateLaneKis];
            f.RateKiwoomBackoffMs = _rateBackoffMs[RateLaneKiwoom]; f.RateKisBackoffMs = _rateBackoffMs[RateLaneKis];
            f.RateKiwoomQueryRemaining = _rateKiwoomQuery1; f.RateKiwoomQueryRemaining60 = _rateKiwoomQuery60;
            f.BuildMicros = (Stopwatch.GetTimestamp() - t0) * 1_000_000 / Stopwatch.Frequency;
            long ver = Interlocked.Increment(ref _version);
            f.Version = ver;
            Volatile.Write(ref _front, f);                                          // ★게시(원자 참조 교체)
            _lastSignature = sig; _pendingDirty = false;
            _spikeGuard.MarkPublished(nowT);
            Interlocked.Increment(ref _published);
            Notify(ver);
            return true;
        }

        // [v12.17.5-DEV] 새 부모가 완료된 회차에만 최근 창 재계산(δ=0) · 감속 가드 관측 · 모드 전환 시 타이머 주기 변경. 반환 = 프레임 갱신 필요.
        private bool SampleOrderPath(long now)
        {
            var op = _orderPath;
            if (op is null) return false;
            long total = op.RecentTotal;
            bool fresh = total != _latSeen;
            if (fresh)
            {
                _latSeen = total;
                int n = op.CopyRecent(_lat0, _lat1, _lat2);
                _latCount = n;
                _latStages[DashLatencyGrade.SignalToGate] = Summarize(ref _h0, _lat0.AsSpan(0, n));
                _latStages[DashLatencyGrade.GateToEgress] = Summarize(ref _h1, _lat1.AsSpan(0, n));
                _latStages[DashLatencyGrade.SignalToEgress] = Summarize(ref _h2, _lat2.AsSpan(0, n));
            }
            ref readonly var e2e = ref _latStages[DashLatencyGrade.SignalToEgress];
            if (_spikeGuard.Observe(fresh, e2e.P99Nanos, e2e.Count, now))
            {
                int period = CurrentPeriodMs;
                try { _timer?.Change(period, period); } catch (ObjectDisposedException) { }
                return true;                                                        // 감속/복귀 표시 갱신
            }
            return fresh;
        }

        private static DashLatencyStage Summarize(ref LatencyHistogram h, ReadOnlySpan<long> nanos)
        {
            h.Load(nanos);
            return new DashLatencyStage { Count = h.Count, P50Nanos = h.Percentile(500), P99Nanos = h.Percentile(990), MaxNanos = h.MaxNanos };
        }

        private bool DrainFills()
        {
            long head = _fills.Head;
            bool any = false;
            if (head - _fillCursor > _fills.Capacity) { _fillsLost += head - _fills.Capacity - _fillCursor; _fillCursor = head - _fills.Capacity; }
            for (; _fillCursor < head; _fillCursor++)
            {
                int r = _fills.TryRead(_fillCursor, out var f);
                if (r == 0) break;
                if (r < 0) { _fillsLost++; continue; }
                _fillsSeen++; any = true;
                Apply(in f);
                _recentFills[_recentFillN++ % _recentFills.Length] = f;
            }
            return any;
        }

        private bool DrainDecisions()
        {
            long head = _decisions.Head;
            bool any = false;
            if (head - _decisionCursor > _decisions.Capacity) { _decisionsLost += head - _decisions.Capacity - _decisionCursor; _decisionCursor = head - _decisions.Capacity; }
            for (; _decisionCursor < head; _decisionCursor++)
            {
                int r = _decisions.TryRead(_decisionCursor, out var d);
                if (r == 0) break;
                if (r < 0) { _decisionsLost++; continue; }
                any = true;
                _recentDecisions[_recentDecisionN++ % _recentDecisions.Length] = d;
            }
            return any;
        }

        private bool DrainEvents()
        {
            long head = _events.Head;
            bool any = false;
            if (head - _eventCursor > _events.Capacity) { _eventsLost += head - _events.Capacity - _eventCursor; _eventCursor = head - _events.Capacity; }
            for (; _eventCursor < head; _eventCursor++)
            {
                int r = _events.TryRead(_eventCursor, out var e);
                if (r == 0) break;
                if (r < 0) { _eventsLost++; continue; }
                any = true;
                _recentEvents[_recentEventN++ % _recentEvents.Length] = e;
            }
            return any;
        }

        // 모의 포지션 원장: 같은 방향 = 가중평단 · 반대 방향 = 청산분 실현손익(체결 1건 단위 승/패) · 초과분 = 반대 포지션 개시
        private void Apply(in DashFill f)
        {
            int i = 0;
            for (; i < _posCount; i++) if (_posSym[i] == f.Symbol) break;
            if (i == _posCount)
            {
                if (_posCount == MaxPositions) return;                              // 원장 초과 = 손익 미반영(표시 전용)
                _posSym[i] = f.Symbol; _posQty[i] = 0; _posAvg[i] = 0; _posCount++;
            }
            long q = _posQty[i], signed = f.Side * f.Qty;
            if (q == 0 || Math.Sign(q) == f.Side)
            {
                long absQ = Math.Abs(q);
                _posAvg[i] = (_posAvg[i] * absQ + f.PriceWon * f.Qty) / (absQ + f.Qty);
                _posQty[i] = q + signed;
                return;
            }
            long close = Math.Min(f.Qty, Math.Abs(q));
            long pnl = close * (f.PriceWon - _posAvg[i]) * Math.Sign(q);
            _realized += pnl;
            if (pnl > 0) _wins++; else if (pnl < 0) _losses++;
            long rest = q + signed;
            _posQty[i] = rest;
            if (rest == 0) _posAvg[i] = 0;
            else if (Math.Sign(rest) != Math.Sign(q)) _posAvg[i] = f.PriceWon;     // 반대 포지션 개시분 = 이번 체결가
        }

        private long PriceOf(long symbol)
        {
            int start = (int)((ulong)(symbol * unchecked((long)0x9E3779B97F4A7C15)) >> 55);
            for (int probe = 0; probe < PriceSlots; probe++)
            {
                int i = (start + probe) & (PriceSlots - 1);
                long k = Volatile.Read(ref _pxKey[i]);
                if (k == symbol) return Volatile.Read(ref _pxVal[i]);
                if (k == 0) return 0;
            }
            return 0;
        }

        // 최신 → 과거 순(최근 항목이 위)
        private static int CopyRecent<T>(T[] ring, long n, T[] dst)
        {
            int count = (int)Math.Min(n, Math.Min(ring.Length, dst.Length));
            for (int k = 0; k < count; k++) dst[k] = ring[(int)((n - 1 - k) % ring.Length)];
            return count;
        }

        // 과거 → 최신 순(곡선)
        private static int CopyChronological<T>(T[] ring, long n, T[] dst)
        {
            int count = (int)Math.Min(n, Math.Min(ring.Length, dst.Length));
            long start = n - count;
            for (int k = 0; k < count; k++) dst[k] = ring[(int)((start + k) % ring.Length)];
            return count;
        }

        // ════════════ ③ 소비자(UI) ════════════

        public long Version => Interlocked.Read(ref _version);

        public PaperDashboardFrame? Acquire()
        {
            for (int spin = 0; spin < 16; spin++)
            {
                var f = Volatile.Read(ref _front);
                if (f is null) return null;
                Interlocked.Increment(ref _leases[f.BufferSlot]);
                if (ReferenceEquals(Volatile.Read(ref _front), f)) return f;        // 리스 후 재확인(그 사이 교체되면 반납 · 재시도)
                Interlocked.Decrement(ref _leases[f.BufferSlot]);
            }
            return null;
        }

        public void Release(PaperDashboardFrame frame)
        {
            if (frame is null || (uint)frame.BufferSlot >= 3u || !ReferenceEquals(_frames[frame.BufferSlot], frame)) return;   // 남의 프레임 무시
            if (Interlocked.Decrement(ref _leases[frame.BufferSlot]) < 0) Interlocked.Increment(ref _leases[frame.BufferSlot]);
        }

        public int LeaseCount(int slot) => (uint)slot < 3u ? Volatile.Read(ref _leases[slot]) : 0;

        /// <summary>[v12.18.8-DEV] 등록된 FrameReady 구독자 수(누수 점검 · 콜드 판독).</summary>
        public int FrameReadySubscribers
        {
            get { var cur = Volatile.Read(ref _subs); int n = 0; for (int i = 0; i < cur.Length; i++) if (cur[i] is not null) n++; return n; }
        }

        public event Action<long>? FrameReady
        {
            add
            {
                if (value is null) return;
                lock (_subGate)
                {
                    var cur = _subs;
                    for (int i = 0; i < cur.Length; i++) if (cur[i] is null) { var next = (Action<long>?[])cur.Clone(); next[i] = value; Volatile.Write(ref _subs, next); return; }
                    var grown = new Action<long>?[cur.Length * 2];
                    Array.Copy(cur, grown, cur.Length); grown[cur.Length] = value;
                    Volatile.Write(ref _subs, grown);
                }
            }
            remove
            {
                if (value is null) return;
                lock (_subGate)
                {
                    var cur = _subs;
                    for (int i = 0; i < cur.Length; i++)
                        if (cur[i] == value) { var next = (Action<long>?[])cur.Clone(); next[i] = null; Volatile.Write(ref _subs, next); return; }
                }
            }
        }

        private void Notify(long version)
        {
            var subs = Volatile.Read(ref _subs);                                     // 복사-교체 배열 — 호출 중 등록/해지와 무경합 · 할당 0
            for (int i = 0; i < subs.Length; i++)
            {
                var s = subs[i];
                if (s is null) continue;
                try { s(version); } catch (Exception) { Interlocked.Increment(ref _subscriberFaults); }
            }
        }

        public void Dispose()
        {
            Volatile.Write(ref _running, 0);
            Interlocked.Exchange(ref _timer, null)?.Dispose();
            // 진행 중 회차 종료 대기 → 영구 봉인(2): 이후 Pump = false · 결선 센티널 폐기와 경합 0(구독자는 비동기 게시뿐 — 역대기 없음)
            var spin = new SpinWait();
            while (Interlocked.CompareExchange(ref _pumping, 2, 0) == 1) spin.SpinOnce();
        }
    }
}
