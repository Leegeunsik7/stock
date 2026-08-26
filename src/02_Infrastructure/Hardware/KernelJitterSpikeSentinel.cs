// ============================================================================
// KernelJitterSpikeSentinel — v5.5.0 과제2: 로우레벨 커널 지터 & NIC 드롭 센티널
//
//  Windows OS 커널 컨텍스트 스위칭 지터·CLR GC Pause 스파이크·NIC 링버퍼 패킷 드롭을 애플리케이션 연산 지연과 ★분리 측정한다.
//  Stopwatch.GetTimestamp(=QueryPerformanceCounter 래퍼)로 기대 폴 간격 대비 실제 지연(스케줄링 지터)을 단측 3σ 추적하고,
//  커널 지터가 타이트 임계(기본 2ms) 또는 3σ 를 초과하면 핫패스 무장을 ★'안전 홀드'로 원자 전환해 지터 유발 시기의 오발주를 선제 차단.
//
//  ── 정직 회계(정찰) ─────────────────────────────────────────────────────────
//  ThreadStarvationSensor 의 단측 3σ 링(_jit[128]/_sum/Int128 _sumSq·ExceedsThreeSigmaUp)·CAS 상태 전이·인프라 격리 계약을
//  미러링. ★신설: 타이트 2ms 임계 전용 링(기존 100ms 임계와 분리), GC Pause 델타(GC.GetTotalPauseDuration), NIC drop
//  (IPv4 IncomingPacketsDiscarded 델타), 그리고 '가역·자기회복' 안전 홀드 술어(SafeHoldActive) — 무장 게이트 blockWhile 합성용.
//
//  ── liveArmed:false / δ=0 / 결정론 ─────────────────────────────────────────
//  ★응답 세만틱: 일시 지터(2ms GC pause 등)엔 ForceFreeze/Downgrade(래칭·수동 해제 요구) 금지 — 가역 홀드(자기 만료)만. 지속
//  지터(연속 임계 초과)만 IgnitionLock.Downgrade 로 에스컬레이션. 홀드는 무장 술어를 '더 안전한 방향(false)'으로만 억제 —
//  실 발주 경로/이중키∧MasterIgnited 우회 신설 0. 결정론: 지터/GC/NIC 는 인프라 관측이라 트레이딩 판정 입력에 절대 무유입.
//  δ=0: 폴은 콜드(20ms Timer)이나 무할당(사전할당 링·Interlocked). SafeHoldActive 독출은 락프리 long 비교.
// ============================================================================
using System;
using System.Diagnostics;
using System.Threading;

namespace StockManager.Common
{
    /// <summary>커널/GC/NIC 지터를 앱 지연과 분리 측정 → 스파이크 시 가역 안전 홀드(무장 억제), 지속 시 Downgrade 에스컬레이션.</summary>
    public sealed class KernelJitterSpikeSentinel : IDisposable
    {
        private const int Ring = 128, MinSamples = 16;
        private readonly long[] _jit = new long[Ring];
        private int  _count, _idx;
        private long _sum;
        private Int128 _sumSq;
        private readonly object _ringLock = new();   // 폴 단일 스레드지만 방어적(통계 일관)

        private readonly Func<long> _nowTicks;        // 측정 시계(QPC) — 트레이딩 판정 아님
        private readonly long _tickFreq;
        private readonly long _hardDeadlineTicks;     // 타이트 임계(기본 2ms)
        private readonly long _holdTicks;             // 안전 홀드 지속(가역·자기 만료)
        private readonly int  _sustainThreshold;      // 연속 스파이크 → Downgrade
        private readonly long _pollTicks;             // 기대 폴 간격
        private readonly IgnitionLock?      _ignition;
        private readonly MmfFlightRecorder? _blackbox;
        private System.Threading.Timer? _timer;

        private long _lastPollTicks;
        private long _safeHoldUntilTicks;             // now < this → 홀드 활성
        private long _lastJitterTicks, _jitterSpikes, _gcPauseSpikes, _escalations;
        private int  _sustainCount, _escalated;
        private double _lastGcPauseMs;                // GC pause 누적 기준선(델타 산출)
        private long _lastGcPauseSpikeMicros;
        private long _nicDropsCum, _lastNicSampleTicks; private long _lastNicDiscarded = -1;

        public KernelJitterSpikeSentinel(IgnitionLock? ignition = null, MmfFlightRecorder? blackbox = null,
            double hardDeadlineMs = 2.0, double holdMs = 200.0, int sustainThreshold = 5, int pollMs = 20,
            Func<long>? nowTicks = null, long tickFrequency = 0)
        {
            _ignition = ignition; _blackbox = blackbox;
            _nowTicks = nowTicks ?? Stopwatch.GetTimestamp;
            _tickFreq = tickFrequency > 0 ? tickFrequency : Stopwatch.Frequency;
            _hardDeadlineTicks = (long)(hardDeadlineMs / 1000.0 * _tickFreq);
            _holdTicks         = (long)(Math.Max(1.0, holdMs) / 1000.0 * _tickFreq);
            _sustainThreshold  = sustainThreshold < 1 ? 1 : sustainThreshold;
            _pollTicks         = (long)(Math.Max(1, pollMs) / 1000.0 * _tickFreq);
            _lastPollTicks     = _nowTicks();
            try { _lastGcPauseMs = GC.GetTotalPauseDuration().TotalMilliseconds; } catch { _lastGcPauseMs = 0; }   // 기준선(첫 샘플 허위 스파이크 방지)
            int pm = pollMs < 1 ? 1 : pollMs;
            _timer = new System.Threading.Timer(_ => { try { Poll(); } catch { } }, null, pm, pm);
        }

        // ── 락프리 텔레메트리 독출(Sample() 이 pull) ──
        public bool SafeHoldActive          => _nowTicks() < Interlocked.Read(ref _safeHoldUntilTicks);
        public long LastJitterMicros        => Interlocked.Read(ref _lastJitterTicks) * 1_000_000L / _tickFreq;
        public long JitterSpikes            => Interlocked.Read(ref _jitterSpikes);
        public long GcPauseSpikes           => Interlocked.Read(ref _gcPauseSpikes);
        public long LastGcPauseSpikeMicros  => Interlocked.Read(ref _lastGcPauseSpikeMicros);
        public long NicDropsEstimate        => Interlocked.Read(ref _nicDropsCum);
        public long Escalations             => Interlocked.Read(ref _escalations);

        /// <summary>주기 폴(콜드·20ms) — 스케줄링 지터·GC pause·NIC drop 측정 후 스파이크 시 가역 안전 홀드 격발.</summary>
        private void Poll()
        {
            long now = _nowTicks();
            long actual = now - Interlocked.Exchange(ref _lastPollTicks, now);
            long jitter = actual - _pollTicks;   // 기대 대비 초과 = 스케줄링 지터
            if (jitter < 0) jitter = 0;
            Interlocked.Exchange(ref _lastJitterTicks, jitter);

            bool jitterBreach = jitter > _hardDeadlineTicks || ExceedsThreeSigmaUp(jitter);
            Push(jitter);

            // GC Pause 델타(누적 → 이번 폴 구간 증가분). 큰 폭이면 GC pause 스파이크.
            bool gcBreach = false;
            try
            {
                double tot = GC.GetTotalPauseDuration().TotalMilliseconds;
                double d = tot - _lastGcPauseMs; _lastGcPauseMs = tot;
                if (d > 0)
                {
                    long micros = (long)(d * 1000.0);
                    if (micros * _tickFreq / 1_000_000L > _hardDeadlineTicks)   // GC pause > 하드 임계
                    { gcBreach = true; Interlocked.Increment(ref _gcPauseSpikes); Interlocked.Exchange(ref _lastGcPauseSpikeMicros, micros); }
                }
            }
            catch { }

            SampleNicDrops(now);
            ApplyVerdict(jitterBreach || gcBreach, jitter, gcBreach, now);
        }

        /// <summary>스파이크 판정 적용 — 가역 안전 홀드(자기 만료) + 지속 시 1회 래치 Downgrade. 회복 시 지속 카운터 리셋.</summary>
        private void ApplyVerdict(bool breach, long jitter, bool gcBreach, long now)
        {
            if (breach)
            {
                Interlocked.Exchange(ref _safeHoldUntilTicks, now + _holdTicks);   // 무장 술어가 이 창 동안 false 로 억제
                Interlocked.Increment(ref _jitterSpikes);
                try { _blackbox?.Record(FlightEventKind.KernelJitterHold, 0L, jitter, gcBreach ? 1L : 0L, _hardDeadlineTicks, 89); } catch { }
                int sc = Interlocked.Increment(ref _sustainCount);
                if (sc >= _sustainThreshold && Interlocked.CompareExchange(ref _escalated, 1, 0) == 0)
                {
                    Interlocked.Increment(ref _escalations);
                    try { _ignition?.Downgrade("KernelJitterSpikeSentinel: 지속 커널 지터 3σ — 핫패스 강등"); } catch { }
                }
            }
            else
            {
                Interlocked.Exchange(ref _sustainCount, 0);   // 회복 — 지속 카운터 리셋(홀드는 자연 만료)
                Interlocked.Exchange(ref _escalated, 0);
            }
        }

        /// <summary>테스트 전용(결정론·타이머 우회) — 지터 샘플 하나를 3σ 링·안전홀드 로직에 투입.</summary>
        public void EvaluateJitterForTest(long jitterTicks)
        {
            long now = _nowTicks();
            bool breach = jitterTicks > _hardDeadlineTicks || ExceedsThreeSigmaUp(jitterTicks);
            Push(jitterTicks);
            ApplyVerdict(breach, jitterTicks, false, now);
        }

        // NIC 링버퍼 패킷 드롭 — 콜드 샘플(3s 간격·비용 큼). IPv4 IncomingPacketsDiscarded 누적 델타.
        private void SampleNicDrops(long now)
        {
            if (now - Interlocked.Read(ref _lastNicSampleTicks) < _tickFreq * 3) return;
            Interlocked.Exchange(ref _lastNicSampleTicks, now);
            try
            {
                long discarded = 0;
                foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                    try { discarded += ni.GetIPv4Statistics().IncomingPacketsDiscarded; } catch { }
                }
                long prev = Interlocked.Exchange(ref _lastNicDiscarded, discarded);
                if (prev >= 0 && discarded > prev) Interlocked.Add(ref _nicDropsCum, discarded - prev);
            }
            catch { }
        }

        // ── 단측 3σ 링(ThreadStarvationSensor 미러·무sqrt 비교: dev²>9·var) ──
        private void Push(long jitter)
        {
            lock (_ringLock)
            {
                if (_count == Ring) { long old = _jit[_idx]; _sum -= old; _sumSq -= (Int128)old * old; }
                else _count++;
                _jit[_idx] = jitter; _sum += jitter; _sumSq += (Int128)jitter * jitter;
                _idx = (_idx + 1) & (Ring - 1);
            }
        }

        private bool ExceedsThreeSigmaUp(long jitter)
        {
            lock (_ringLock)
            {
                if (_count < MinSamples) return false;
                long mean = _sum / _count;
                long dev = jitter - mean;
                if (dev <= 0) return false;                          // 단측 — 느려짐만
                Int128 var = _sumSq / _count - (Int128)mean * mean;
                if (var < 0) var = 0;
                return (Int128)dev * dev > 9 * var;                  // dev > 3σ ⟺ dev² > 9·var
            }
        }

        public void Dispose() { try { _timer?.Dispose(); } catch { } _timer = null; }
    }
}
