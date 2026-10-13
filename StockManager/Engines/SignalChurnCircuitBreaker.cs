// ============================================================================
// SignalChurnCircuitBreaker — [v12.18.1-DEV · 방향 B] 고빈도 승인 시그널 서킷 브레이커(알파 모델 붕괴 = 비트 27 Disarm)
//
//  동적 임계 상향(VolatilityAdaptiveCalibrator) 뒤에도 '모델이 승인한' 시그널이 1분에 비정상적으로 많다면 그것은 시장이 아니라 모델이
//   고장 난 것이다(같은 신호의 반복 승인 · 입력 피드 반복 · 국면 오판). 2단 방어:
//   ① 소프트(> Soft/분): 그 회차 승인을 억제(전방 거부 · 계수) — 일시적 몰림은 창이 비면 자동 해소.
//   ② 하드(> Hard/분): StrategyAnomalyInterlock.Trip(ModelBreakdown) — 비트 27 원자 Disarm(sticky · 재무장 = 물리 이중키).
//   ★계수 대상 = '모델 승인'(확신 ≥ 동적 임계 ∧ 방향 일치) 전부 — 소프트 억제된 승인도 센다. 억제분을 빼면 소프트가 하드를 영원히
//     가려 붕괴가 판정되지 않는다(억제 중에도 모델이 계속 고빈도로 승인 = 붕괴 지속).
//  [v12.18.2-DEV · 방향 A] 창 = 1초 버킷 60개 InlineArray(객체 헤더 · 배열 경계 검사 없는 고정 버퍼 · 힙 할당 0) + 러닝 합.
//   판정 O(1): 초가 넘어가면 '지나간 초'의 버킷만 소거하며 합에서 뺀다(최대 60회 — 60초 이상 공백이면 전체 소거 1회로 끝). 같은 초 안의
//   승인은 버킷 1개 증가 + 합 1 증가뿐. 수정 전(v12.18.1)은 매 승인마다 60 버킷 전수 합산.
//  동시성: 호출자 = 드레인 스레드(제안 관측) 또는 라우터 게이트 — 단일. 방어적으로 CAS 진입 플래그 — 경합(동시 호출) = 그 회차 거부(fail-closed).
//  시계: 주입(µs · 단조). 시계 역행 = 그 회차 거부 + 계수(창 스탬프 오염 방지).
// ============================================================================
using System;
using System.Runtime.CompilerServices;
using System.Threading;

namespace StockManager.Engines
{
    public enum ChurnVerdict : byte { Allowed = 0, Suppressed = 1, Breakdown = 2, Contended = 3, ClockRegressed = 4 }

    /// <summary>1초 버킷 60개 고정 버퍼(값 타입 · 인라인).</summary>
    [InlineArray(SignalChurnCircuitBreaker.WindowSeconds)]
    internal struct ChurnBuckets { private int _e0; }

    public sealed class SignalChurnCircuitBreaker
    {
        public const int WindowSeconds = 60;
        public const int DefaultSoftPerMinute = 30, DefaultHardPerMinute = 60;

        private readonly StrategyAnomalyInterlock _anomaly;
        private readonly int _soft, _hard;
        private ChurnBuckets _buckets;
        private long _headSecond = long.MinValue;                                    // 가장 최근 버킷의 초(창 = head − 59 .. head)
        private long _sum;
        private int _busy;
        private long _approved, _allowed, _suppressed, _breakdowns, _contended, _regressed, _lastWindow, _evictions;

        /// <param name="anomaly">비트 27 소스(호스트 결속 · 필수 인터락).</param>
        /// <param name="softPerMinute">1분 승인 수가 이 값을 넘으면 억제.</param>
        /// <param name="hardPerMinute">1분 승인 수가 이 값을 넘으면 모델 붕괴 Disarm(> soft).</param>
        public SignalChurnCircuitBreaker(StrategyAnomalyInterlock anomaly, int softPerMinute = DefaultSoftPerMinute, int hardPerMinute = DefaultHardPerMinute)
        {
            _anomaly = anomaly ?? throw new ArgumentNullException(nameof(anomaly));
            if (softPerMinute < 1) throw new ArgumentOutOfRangeException(nameof(softPerMinute));
            if (hardPerMinute <= softPerMinute) throw new ArgumentOutOfRangeException(nameof(hardPerMinute), "하드 > 소프트");
            _soft = softPerMinute; _hard = hardPerMinute;
        }

        public int SoftPerMinute => _soft;
        public int HardPerMinute => _hard;
        public long Approved => Interlocked.Read(ref _approved);
        public long Allowed => Interlocked.Read(ref _allowed);
        public long Suppressed => Interlocked.Read(ref _suppressed);
        public long Breakdowns => Interlocked.Read(ref _breakdowns);
        public long Contended => Interlocked.Read(ref _contended);
        public long ClockRegressions => Interlocked.Read(ref _regressed);
        /// <summary>직전 판정 시점의 1분 승인 수(관제).</summary>
        public long LastWindowCount => Interlocked.Read(ref _lastWindow);
        /// <summary>[방향 A] 초 경계에서 소거한 버킷 수(누적 · O(1) 상각 검증용).</summary>
        public long BucketEvictions => Interlocked.Read(ref _evictions);

        /// <summary>모델 승인 1건 기록 + 판정(δ=0 · O(1) 상각). Allowed 만 통과.</summary>
        public ChurnVerdict OnApproved(long nowMicros)
        {
            if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) { Interlocked.Increment(ref _contended); return ChurnVerdict.Contended; }
            try
            {
                long sec = nowMicros / 1_000_000;
                if (sec < _headSecond) { Interlocked.Increment(ref _regressed); return ChurnVerdict.ClockRegressed; }
                Advance(sec);
                _buckets[(int)(sec % WindowSeconds)]++;
                long total = ++_sum;
                Interlocked.Increment(ref _approved);
                Interlocked.Exchange(ref _lastWindow, total);
                if (total > _hard)
                {
                    Interlocked.Increment(ref _breakdowns);
                    _anomaly.Trip(StrategyAnomalyKind.ModelBreakdown);                 // ★비트 27 원자 Disarm(최초 원인만 각인 · sticky)
                    return ChurnVerdict.Breakdown;
                }
                if (total > _soft) { Interlocked.Increment(ref _suppressed); return ChurnVerdict.Suppressed; }
                Interlocked.Increment(ref _allowed);
                return ChurnVerdict.Allowed;
            }
            finally { Volatile.Write(ref _busy, 0); }
        }

        /// <summary>현재 창의 승인 수(판독 · 경합 시 직전 판정값). 소프트 초과 여부 = 보류 판정 근거.</summary>
        public long WindowCount(long nowMicros)
        {
            if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) return LastWindowCount;
            try
            {
                long sec = nowMicros / 1_000_000;
                if (sec >= _headSecond) Advance(sec);
                Interlocked.Exchange(ref _lastWindow, _sum);
                return _sum;
            }
            finally { Volatile.Write(ref _busy, 0); }
        }

        // head → sec 로 창 이동: 새로 창에 들어오는 초(head+1 .. sec)의 버킷 = 60초 전 값이므로 합에서 빼고 0(최대 60회).
        private void Advance(long sec)
        {
            if (_headSecond == long.MinValue || sec - _headSecond >= WindowSeconds)
            {
                if (_sum != 0) Interlocked.Increment(ref _evictions);
                ((Span<int>)_buckets).Clear();
                _sum = 0;
            }
            else
            {
                for (long s = _headSecond + 1; s <= sec; s++)
                {
                    ref int b = ref _buckets[(int)(s % WindowSeconds)];
                    if (b != 0) { _sum -= b; b = 0; Interlocked.Increment(ref _evictions); }
                }
            }
            _headSecond = sec;
        }

        /// <summary>재무장(물리 이중키) — 창 소거 + 전략 이상 인터락 재무장. 창을 비우지 않으면 재무장 직후 첫 승인이 즉시 재격발한다.</summary>
        public bool TryRearm(bool valveWired, bool liveArmingWired)
        {
            if (!(valveWired && liveArmingWired)) return false;
            while (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) Thread.SpinWait(8);
            try { ((Span<int>)_buckets).Clear(); _sum = 0; Interlocked.Exchange(ref _lastWindow, 0); }
            finally { Volatile.Write(ref _busy, 0); }
            return _anomaly.TryRearm(valveWired, liveArmingWired);
        }
    }
}
