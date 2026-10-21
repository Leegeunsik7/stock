// ============================================================================
// AiSignalGovernor — [v12.18.43-DEV] AI 자율 신호 정제 · 플립플롭 가드 · 알파 시효성 가드 · 확신도 비례 정수 사이저(비트 27 결속)
//
//  AI 모델 산출(AiSignal)이 주문 제안으로 넘어가기 직전의 단일 관문. 판정 순서(앞 단계 거부 = 뒤 단계 미실행 · 전 과정 δ=0):
//   ⓪ 래치: StrategyAnomalyInterlock 격발 중(어느 원인이든) = 전 신호 거부(Latched · sticky · 재무장 = 물리 이중키).
//   ① 정제: Confidence ∈ [−1, +1] · Position ∈ [−1, +1] · Urgency ∈ [0, 1].
//      NaN/∞ = Trip(NonFinite) · 범위 이탈 = Trip(OutOfRange) — 그 회차부터 비트 27 원자 Disarm(수치 붕괴 모델 = 고장, 시장 아님).
//   ② 시계: now 역행 = 거부(ClockRegressed · 창 스탬프 오염 방지) · InferenceMicros > now = 거부(FutureTimestamp · 시계 영역 불일치).
//   ③ 알파 시효성: now − InferenceMicros > MaxLatency(기본 30ms) = 폐기(Stale · 인터락 무관 — 전달 지연은 모델 고장이 아니다).
//   ④ 플립플롭: |Confidence| ≥ FlipExtreme(기본 0.5)인 '극단' 신호의 방향이 직전 극단과 반대 = 반전 1건. 반전 스탬프를 고정 링(MaxFlips)에
//      기록하고, 링이 차고 가장 오래된 반전이 FlipWindow(기본 100ms) 안이면 = Trip(SignalFlipFlop) — 임계에 도달한 '그 회차'에 래치(1틱).
//      반전 판정 O(1): 링 = InlineArray(힙 0) · 최근 MaxFlips 개 반전만 보유(가장 오래된 것 = 다음 쓰기 위치).
//   ⑤ 사이징: confBp = ⌊|Confidence| × 10⁴⌋(절단 = 보수) · confBp < MinConfidenceBp = 거부(BelowThreshold) ·
//      수량 = ⌊MaxQuantity × confBp / 10⁴⌋ 을 LotSize 배수로 내림 → 0 = 거부. MaxQuantity ≤ 10⁹ 상한 → 곱 ≤ 10¹³(long 오버플로 0).
//  ★정직: 이 관문은 모델의 '품질'을 판정하지 않는다 — 수치 · 시효 · 방향 안정성의 하한 위생만 강제한다. 호스트 DI 미결속(신규 엔진 ·
//    실 AI 모델 피드 부재) — 가짜 배선 금지. liveArmed:false 불변: 승인(Accepted)은 '주문 후보'일 뿐 무장 판정은 기존 디스패처 경로가 한다.
//  동시성: 호출자 = 단일(모델 스트림 1개 = 인스턴스 1개). 방어적 CAS 진입 플래그 — 경합 = 그 회차 거부(Contended · fail-closed).
//  시계: 주입(µs · 단조 · 호출자 now 와 InferenceMicros 는 같은 시계 영역).
// ============================================================================
using System;
using System.Runtime.CompilerServices;
using System.Threading;

namespace StockManager.Engines.Strategy
{
    public enum AiSignalVerdict : byte
    {
        Accepted = 0, Latched = 1, NonFinite = 2, OutOfRange = 3, ClockRegressed = 4, FutureTimestamp = 5,
        Stale = 6, FlipFlop = 7, BelowThreshold = 8, Contended = 9,
    }

    /// <summary>AI 모델 산출 1건. Confidence 부호 = 방향(+ 매수 / − 매도) · 크기 = 확신.</summary>
    public readonly record struct AiSignal(long SymbolCode, double Confidence, double Position, double Urgency, long InferenceMicros);

    /// <summary>승인된 정수 주문 후보(Side +1 매수 / −1 매도 · Quantity ≥ LotSize).</summary>
    public readonly record struct AiSizedOrder(long SymbolCode, int Side, long Quantity, int ConfidenceBp);

    /// <summary>최근 반전 스탬프 고정 링(값 타입 · 인라인).</summary>
    [InlineArray(AiSignalGovernor.MaxFlipCapacity)]
    internal struct FlipStamps { private long _e0; }

    public sealed class AiSignalGovernor
    {
        public const int MaxFlipCapacity = 16;
        public const long DefaultFlipWindowMicros = 100_000, DefaultMaxLatencyMicros = 30_000;
        public const int DefaultMaxFlips = 3, DefaultMinConfidenceBp = 1_000;
        public const double DefaultFlipExtreme = 0.5;
        public const long MaxQuantityCap = 1_000_000_000;
        private const int BpScale = 10_000;

        private readonly StrategyAnomalyInterlock _anomaly;
        private readonly long _maxQty, _lot, _flipWindow, _maxLatency;
        private readonly int _maxFlips, _minConfBp;
        private readonly double _flipExtreme;
        private FlipStamps _stamps;
        private int _head, _filled, _lastExtremeSide, _busy;
        private long _lastNow = long.MinValue;
        private long _evaluated, _accepted, _latched, _nonFinite, _outOfRange, _regressed, _future, _stale, _flipFlops, _reversals, _below, _contended;

        /// <param name="anomaly">비트 27 소스(필수 인터락).</param>
        /// <param name="maxQuantity">확신 1.0 일 때 수량(상한 · ≤ 10⁹).</param>
        /// <param name="lotSize">수량 스냅 단위(1 ≤ lot ≤ maxQuantity).</param>
        /// <param name="maxFlips">FlipWindow 안 반전이 이 수에 도달하면 래치(1 ~ 16).</param>
        public AiSignalGovernor(StrategyAnomalyInterlock anomaly, long maxQuantity, long lotSize = 1,
                                long flipWindowMicros = DefaultFlipWindowMicros, int maxFlips = DefaultMaxFlips,
                                long maxLatencyMicros = DefaultMaxLatencyMicros, double flipExtreme = DefaultFlipExtreme,
                                int minConfidenceBp = DefaultMinConfidenceBp)
        {
            _anomaly = anomaly ?? throw new ArgumentNullException(nameof(anomaly));
            if (maxQuantity < 1 || maxQuantity > MaxQuantityCap) throw new ArgumentOutOfRangeException(nameof(maxQuantity));
            if (lotSize < 1 || lotSize > maxQuantity) throw new ArgumentOutOfRangeException(nameof(lotSize));
            if (flipWindowMicros < 1) throw new ArgumentOutOfRangeException(nameof(flipWindowMicros));
            if (maxFlips < 1 || maxFlips > MaxFlipCapacity) throw new ArgumentOutOfRangeException(nameof(maxFlips));
            if (maxLatencyMicros < 0) throw new ArgumentOutOfRangeException(nameof(maxLatencyMicros));
            if (!(flipExtreme > 0.0 && flipExtreme <= 1.0)) throw new ArgumentOutOfRangeException(nameof(flipExtreme));
            if (minConfidenceBp < 1 || minConfidenceBp > BpScale) throw new ArgumentOutOfRangeException(nameof(minConfidenceBp));
            _maxQty = maxQuantity; _lot = lotSize; _flipWindow = flipWindowMicros; _maxFlips = maxFlips;
            _maxLatency = maxLatencyMicros; _flipExtreme = flipExtreme; _minConfBp = minConfidenceBp;
        }

        public StrategyAnomalyInterlock Anomaly => _anomaly;
        public long Evaluated => Interlocked.Read(ref _evaluated);
        public long AcceptedCount => Interlocked.Read(ref _accepted);
        public long LatchedRejects => Interlocked.Read(ref _latched);
        public long NonFiniteRejects => Interlocked.Read(ref _nonFinite);
        public long OutOfRangeRejects => Interlocked.Read(ref _outOfRange);
        public long ClockRegressions => Interlocked.Read(ref _regressed);
        public long FutureRejects => Interlocked.Read(ref _future);
        public long StaleDrops => Interlocked.Read(ref _stale);
        public long FlipFlopTrips => Interlocked.Read(ref _flipFlops);
        /// <summary>극단 신호 방향 반전 누적(래치 이전 반전 포함 · 관제).</summary>
        public long Reversals => Interlocked.Read(ref _reversals);
        public long BelowThresholdRejects => Interlocked.Read(ref _below);
        public long Contended => Interlocked.Read(ref _contended);

        /// <summary>신호 1건 판정(δ=0 · O(1)). Accepted 일 때만 order 유효 — 그 외 order = default(수량 0).</summary>
        public AiSignalVerdict Evaluate(in AiSignal signal, long nowMicros, out AiSizedOrder order)
        {
            order = default;
            if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) { Interlocked.Increment(ref _contended); return AiSignalVerdict.Contended; }
            try
            {
                Interlocked.Increment(ref _evaluated);
                if (_anomaly.Tripped) { Interlocked.Increment(ref _latched); return AiSignalVerdict.Latched; }

                double c = signal.Confidence, p = signal.Position, u = signal.Urgency;
                if (!double.IsFinite(c) || !double.IsFinite(p) || !double.IsFinite(u))
                {
                    Interlocked.Increment(ref _nonFinite);
                    _anomaly.Trip(StrategyAnomalyKind.NonFinite);                          // ★비트 27 원자 Disarm
                    return AiSignalVerdict.NonFinite;
                }
                if (c < -1.0 || c > 1.0 || p < -1.0 || p > 1.0 || u < 0.0 || u > 1.0)
                {
                    Interlocked.Increment(ref _outOfRange);
                    _anomaly.Trip(StrategyAnomalyKind.OutOfRange);                         // ★비트 27 원자 Disarm
                    return AiSignalVerdict.OutOfRange;
                }

                if (nowMicros < _lastNow) { Interlocked.Increment(ref _regressed); return AiSignalVerdict.ClockRegressed; }
                _lastNow = nowMicros;
                if (signal.InferenceMicros > nowMicros) { Interlocked.Increment(ref _future); return AiSignalVerdict.FutureTimestamp; }
                if (nowMicros - signal.InferenceMicros > _maxLatency) { Interlocked.Increment(ref _stale); return AiSignalVerdict.Stale; }

                int side = c > 0.0 ? 1 : c < 0.0 ? -1 : 0;
                if (side != 0 && Math.Abs(c) >= _flipExtreme)
                {
                    int last = _lastExtremeSide;
                    _lastExtremeSide = side;
                    if (last != 0 && last != side && RecordReversal(nowMicros))
                    {
                        Interlocked.Increment(ref _flipFlops);
                        _anomaly.Trip(StrategyAnomalyKind.SignalFlipFlop);                 // ★임계 도달 회차 = 래치(1틱)
                        return AiSignalVerdict.FlipFlop;
                    }
                }

                int confBp = (int)(Math.Abs(c) * BpScale);                                  // 절단(보수) · |c| ≤ 1 → ≤ 10⁴
                long qty = side == 0 || confBp < _minConfBp ? 0 : SizeQuantity(_maxQty, _lot, confBp);
                if (qty <= 0) { Interlocked.Increment(ref _below); return AiSignalVerdict.BelowThreshold; }

                order = new AiSizedOrder(signal.SymbolCode, side, qty, confBp);
                Interlocked.Increment(ref _accepted);
                return AiSignalVerdict.Accepted;
            }
            finally { Volatile.Write(ref _busy, 0); }
        }

        /// <summary>확신도 비례 정수 수량(δ=0 · 순수): ⌊maxQty × confBp / 10⁴⌋ 을 lot 배수로 내림. 입력 이탈 = 0(fail-closed).</summary>
        public static long SizeQuantity(long maxQuantity, long lotSize, int confidenceBp)
        {
            if (maxQuantity < 1 || maxQuantity > MaxQuantityCap || lotSize < 1 || confidenceBp <= 0 || confidenceBp > BpScale) return 0;
            long raw = maxQuantity * confidenceBp / BpScale;                                // ≤ 10⁹ × 10⁴ = 10¹³ — 오버플로 0
            return raw - raw % lotSize;
        }

        // 반전 1건 기록 → 링이 찼고 가장 오래된 반전(= 다음 쓰기 위치)이 창 안이면 true.
        private bool RecordReversal(long nowMicros)
        {
            Interlocked.Increment(ref _reversals);
            _stamps[_head] = nowMicros;
            _head = _head + 1 == _maxFlips ? 0 : _head + 1;
            if (_filled < _maxFlips) _filled++;
            return _filled == _maxFlips && nowMicros - _stamps[_head] <= _flipWindow;
        }

        /// <summary>재무장(물리 이중키) — 반전 링 · 직전 극단 방향 소거 + 전략 이상 인터락 재무장. 링을 비우지 않으면 재무장 직후 첫 반전이 즉시 재격발한다.</summary>
        public bool TryRearm(bool valveWired, bool liveArmingWired)
        {
            if (!(valveWired && liveArmingWired)) return false;
            while (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) Thread.SpinWait(8);
            try { ((Span<long>)_stamps).Clear(); _head = 0; _filled = 0; _lastExtremeSide = 0; }
            finally { Volatile.Write(ref _busy, 0); }
            return _anomaly.TryRearm(valveWired, liveArmingWired);
        }
    }
}
