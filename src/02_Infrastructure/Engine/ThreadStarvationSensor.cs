// ============================================================================
// ThreadStarvationSensor — v5.1.0 과제3: 쓰레드 아사 방지 및 무할당 실시간 진단 센서
//
//  실전 고빈도 가동 시 .NET ThreadPool 작업 스레드가 순간 I/O 폭주로 아사(Starvation)에 빠져 핫패스 디스패칭이 수십 ms
//  지연되는 참사를 선제 차단한다. ThreadPool 가용성 + CLR 스케줄링 지터(주기 콜백의 기대↔실제 지연 편차)를 무할당으로
//  감시해, 임계 초과 시 (a) ThreadPool.SetMinThreads 로 가용 스레드 강제 프로비저닝, (b) 핫패스 전용 스레드 우선순위를
//  일시 상향(주입 콜백·소유 스레드 한정 try/finally 복원)한다.
//
//  ── ★결정론 격리(정찰 핵심 gotcha) ──────────────────────────────────────────
//  ThreadPool 가용성·지터는 '인프라' 값이다. Interlocked 텔레메트리 카운터 + 인프라 액션(SetMinThreads·우선순위 상향)에만
//  흐르고, QFixed/주문 판정 입력에는 절대 유입되지 않는다. 트레이딩 결정론은 이 센서와 완전 직교. 지터 통계는 단측 3σ 링
//  (SlippageSentinel/CanaryRttSentinel 원형)을 이식 — 지터는 '증가(느려짐)'만 위험하므로 단측.
//
//  δ=0: Observe 는 long 비교 + Interlocked + 고정 long[128] 링(무할당). ThreadPool.GetAvailableThreads 는 무할당.
//  SetMinThreads/우선순위 상향은 콜드 경로(아사 격발 시 1회). 시계 주입(Stopwatch 단조 권장).
// ============================================================================
using System;
using System.Threading;

namespace StockManager.Common
{
    public enum ThreadPoolHealth : byte { Healthy = 0, Starved = 1 }

    /// <summary>ThreadPool 아사 + CLR 지터 무할당 감시 → 프로비저닝/우선순위 상향. 인프라 전용(트레이딩 판정 무관).</summary>
    public sealed class ThreadStarvationSensor : IDisposable
    {
        private const int Capacity = 128;
        private const int MinSamples = 10;

        private readonly Action?            _provisionThreads;  // ThreadPool.SetMinThreads 등(주입)
        private readonly Action?            _boostHotPath;      // 핫패스 전용 스레드 우선순위 상향(소유 스레드 self-boost)
        private readonly Action?            _restoreHotPath;    // 우선순위 복원
        private readonly MmfFlightRecorder? _blackbox;
        private readonly long               _jitterDeadlineTicks; // 스케줄링 지터 임계(ticks)
        private readonly double             _minAvailabilityScore; // 가용성 점수 임계(0..1)

        // δ=0 단측 지터 통계(고정 링·단일 라이터).
        private readonly long[] _jit = new long[Capacity];
        private int   _idx, _count;
        private long  _sum;
        private Int128 _sumSq;

        private int  _state = (int)ThreadPoolHealth.Healthy;
        private long _starvations, _lastJitterTicks, _provisions;
        private long _availabilityScoreMilli;   // 가용성 점수 ×1000(정수 노출·δ=0)
        private int  _measured;                  // ★0=미측정(→1000 표시)·1=측정됨(0 도 진짜 값). 센티넬-0 충돌 봉쇄.
        private System.Threading.Timer? _timer;
        private readonly Func<long>? _nowTicks;
        private long _lastTickTs;
        private readonly long _expectedIntervalTicks;
        private readonly int  _pollMs;           // ★Timer 주기(지터 기대치와 정합 — 하드코딩 50 제거)

        public ThreadStarvationSensor(Action? provisionThreads = null, Action? boostHotPath = null, Action? restoreHotPath = null,
                                      long jitterDeadlineTicks = 0, double minAvailabilityScore = 0.15,
                                      Func<long>? nowTicks = null, int pollMs = 50, MmfFlightRecorder? blackbox = null)
        {
            _provisionThreads = provisionThreads;
            _boostHotPath     = boostHotPath;
            _restoreHotPath   = restoreHotPath;
            _blackbox         = blackbox;
            _nowTicks         = nowTicks ?? System.Diagnostics.Stopwatch.GetTimestamp;
            _pollMs           = pollMs < 1 ? 1 : pollMs;
            long freq = System.Diagnostics.Stopwatch.Frequency;
            // 기본 지터 임계 = pollMs 의 2배(스케줄 지연이 주기의 2배 넘으면 아사 징후).
            _jitterDeadlineTicks = jitterDeadlineTicks > 0 ? jitterDeadlineTicks : (long)(_pollMs * 2 / 1000.0 * freq);
            _minAvailabilityScore = minAvailabilityScore <= 0 ? 0.15 : minAvailabilityScore;
            _expectedIntervalTicks = (long)(_pollMs / 1000.0 * freq);
        }

        public ThreadPoolHealth Health => (ThreadPoolHealth)Volatile.Read(ref _state);
        public long Starvations        => Interlocked.Read(ref _starvations);
        public long LastJitterTicks    => Interlocked.Read(ref _lastJitterTicks);
        public long Provisions         => Interlocked.Read(ref _provisions);
        /// <summary>ThreadPool 가용성 점수(0..1000, 높을수록 건강). ★미측정 시에만 1000(측정된 0=완전 아사는 그대로 0 노출).</summary>
        public long AvailabilityScoreMilli => Volatile.Read(ref _measured) == 0 ? 1000 : Interlocked.Read(ref _availabilityScoreMilli);

        public void Start()
        {
            if (_timer is not null) return;
            _lastTickTs = _nowTicks!();
            _timer = new System.Threading.Timer(_ => { try { PollOnce(); } catch { } }, null, 0, _pollMs);   // ★ctor pollMs 사용(지터 기대치 정합)
        }

        private void PollOnce()
        {
            long now = _nowTicks!();
            long delta = now - Interlocked.Exchange(ref _lastTickTs, now);
            long jitter = delta - _expectedIntervalTicks; if (jitter < 0) jitter = 0;   // 기대 초과분 = 스케줄 지연
            ThreadPool.GetAvailableThreads(out int availW, out _);
            ThreadPool.GetMaxThreads(out int maxW, out _);
            double score = maxW > 0 ? (double)availW / maxW : 1.0;
            Observe(jitter, score);
        }

        /// <summary>
        /// ★핫 판정(δ=0) — 지터 관측 + 가용성 점수 평가. 지터 3σ 상향 이탈 또는 가용성 점수 임계 미만이면 아사 격발
        /// (프로비저닝 + 우선순위 상향). 회복 시 복원. 반환=이번 관측으로 아사 진단됐는지.
        /// </summary>
        public bool Observe(long jitterTicks, double availabilityScore)
        {
            if (jitterTicks < 0) jitterTicks = 0;
            Interlocked.Exchange(ref _lastJitterTicks, jitterTicks);
            Interlocked.Exchange(ref _availabilityScoreMilli, (long)(Math.Clamp(availabilityScore, 0.0, 1.0) * 1000));
            Volatile.Write(ref _measured, 1);   // ★측정 완료 표식 — 이후 0 은 진짜 완전 아사(1000 오표시 안 함)

            bool jitterBreach = ExceedsThreeSigmaUp(jitterTicks) || jitterTicks > _jitterDeadlineTicks;
            bool availBreach  = availabilityScore < _minAvailabilityScore;
            Push(jitterTicks);

            if (!(jitterBreach || availBreach))
            {
                // 회복 — Starved→Healthy 전이 시 우선순위 복원.
                if (Interlocked.CompareExchange(ref _state, (int)ThreadPoolHealth.Healthy, (int)ThreadPoolHealth.Starved) == (int)ThreadPoolHealth.Starved)
                { try { _restoreHotPath?.Invoke(); } catch { } }
                return false;
            }

            // 아사 격발 — CAS 로 Healthy→Starved 1회 전이 시 프로비저닝+상향.
            if (Interlocked.CompareExchange(ref _state, (int)ThreadPoolHealth.Starved, (int)ThreadPoolHealth.Healthy) != (int)ThreadPoolHealth.Healthy)
                return false;
            Interlocked.Increment(ref _starvations);
            try { _blackbox?.Record(FlightEventKind.ThreadStarvation, symbolCode: 0L, arg0: jitterTicks, arg1: (long)(availabilityScore * 1000), arg2: _jitterDeadlineTicks, sourceCode: 62); } catch { }
            try { _provisionThreads?.Invoke(); Interlocked.Increment(ref _provisions); } catch { }
            try { _boostHotPath?.Invoke(); } catch { }
            return true;
        }

        private bool ExceedsThreeSigmaUp(long jitter)
        {
            int n = _count;
            if (n < MinSamples) return false;
            long mean = _sum / n;
            long dev = jitter - mean;
            if (dev <= 0) return false;   // 단측 — 느려짐만
            Int128 var = _sumSq / n - (Int128)mean * mean;
            if (var <= 0) return false;
            return (Int128)dev * dev > (Int128)9 * var;
        }

        private void Push(long jitter)
        {
            if (_count == Capacity) { long old = _jit[_idx]; _sum -= old; _sumSq -= (Int128)old * old; }
            else _count++;
            _jit[_idx] = jitter; _sum += jitter; _sumSq += (Int128)jitter * jitter;
            _idx = (_idx + 1) % Capacity;
        }

        public void Dispose() { try { _timer?.Dispose(); } catch { } _timer = null; }
    }
}
