// ============================================================================
// HardwareKeyPulseHeartbeatSentinel — v10.0.0 하드웨어 키 펄스 심박 센티널 (Gemini 보강 과제)
//
//  50ms 주기 물리 펄스 신호를 스캔해, 물리 무장 키의 단선/접점 불량을 포착 즉시 무장을 원자적으로 차단하고
//  liveArmed:false 로 수렴시킨다. 키가 물리적으로 살아있을 때만 Pulse() 로 심박을 갱신 — 갱신이 끊기면 KeyAlive=false.
//
//  ★DeadMansSwitchWatchdog(v7.4.0)/AtomicArmingLease 의 주입-시계 심박 패턴 미러: 격리 QPC + Volatile long 만 사용해
//    엔진 락/GC/힙 독립. 두 티어: (가역) 펄스 복원 시 KeyAlive 자동 재개, (하드 래치) 지속 단선(sustainThreshold) 시
//    1회성 CAS 래치 + 주입식 onKeyLost(IgnitionLock.Lock/ForceFreeze) 로 무장 하드 차단(재무장 금지).
//    ★dormant(미수신) 과 loss(살아있다 단선) 를 분별 — dormant 는 SafeHold(tighten)만, loss 만 래치 후보.
//
//  ★불변: δ=0(Pulse/CheckOnce/SafeHoldActive 힙 0·Interlocked/Volatile) · 결정론(주입 시계·tickFrequency 미지정 시
//    고정 1e7) · liveArmed:false(SafeHold tighten-only·소켓/onSend 참조 0·키 미수신 기본 = 무장 불가) · fail-closed
//    (미수신/단선/래치 → KeyAlive=false → 무장 하향).
// ============================================================================
using System;
using System.Diagnostics;
using System.Threading;

namespace StockManager.Common
{
    /// <summary>하드웨어 키 펄스 심박 센티널(δ=0). Pulse(물리 접점 생존)→KeyAlive·단선 지속 시 하드 래치+무장 차단.</summary>
    public sealed class HardwareKeyPulseHeartbeatSentinel : IDisposable
    {
        private readonly Func<long> _nowTicks;
        private readonly long _freq;
        private readonly long _pulseTimeoutTicks;   // 이 시간 내 펄스 없으면 단선 판정
        private readonly long _sustainThreshold;    // 연속 단선 스캔 임계 → 하드 래치
        private readonly Action? _onKeyLost;        // 지속 단선 → 무장 하드 차단(주입식·IgnitionLock.Lock/ForceFreeze)
        private readonly System.Threading.Timer? _timer;
        private long _lastPulseTicks;   // 마지막 물리 펄스 시각(0=미수신·dormant)
        private long _pulses, _losses, _restores, _sustainCount;
        private int  _latched;          // 지속 단선 하드 래치(1회성)
        private int  _disposedLatch;

        public HardwareKeyPulseHeartbeatSentinel(Action? onKeyLost = null, long pulseTimeoutMillis = 150,
                                                 int sustainThreshold = 3, int pollMs = 50,
                                                 Func<long>? nowTicks = null, long tickFrequency = 0, bool startTimer = false)
        {
            _nowTicks = nowTicks ?? Stopwatch.GetTimestamp;
            // ★자체-시계(Stopwatch.GetTimestamp) 소스와 반드시 매칭: freq 폴백을 Stopwatch.Frequency 로(고정 1e7 아님) — 비-1e7 QPC
            //   호스트(HPET 14.318MHz·ACPI PM 3.58MHz·일부 VM)에서 펄스 타임아웃이 왜곡돼 죽은 키를 살아있다 오판(fail-open)하는 결함 봉쇄.
            _freq = tickFrequency > 0 ? tickFrequency : Stopwatch.Frequency;
            _pulseTimeoutTicks = _freq * Math.Max(1L, pulseTimeoutMillis) / 1000L;
            _sustainThreshold = Math.Max(1, sustainThreshold);
            _onKeyLost = onKeyLost;
            // _lastPulseTicks = 0(미수신) → 초기 KeyAlive=false(펄스 없으면 무장 불가·liveArmed:false 안전 기본).
            if (startTimer)
            {
                int pm = Math.Clamp(pollMs, 10, 1000);
                _timer = new System.Threading.Timer(_ => { try { CheckOnce(); } catch { } }, null, pm, pm);
            }
        }

        // ── 읽기(δ=0·락프리) ──
        public long Pulses   => Interlocked.Read(ref _pulses);
        public long Losses   => Interlocked.Read(ref _losses);
        public long Restores => Interlocked.Read(ref _restores);
        public bool Latched  => Volatile.Read(ref _latched) == 1;
        public long PulseAgeMs
        {
            get { long lp = Volatile.Read(ref _lastPulseTicks); if (lp == 0L) return -1L; long age = _nowTicks() - lp; return age <= 0L ? 0L : age * 1000L / _freq; }
        }
        /// <summary>키 생존: 최근 펄스(타임아웃 내) ∧ 미래치. 미수신(dormant)·단선·래치 시 false.</summary>
        public bool KeyAlive
        {
            get { long lp = Volatile.Read(ref _lastPulseTicks); return lp != 0L && (_nowTicks() - lp) <= _pulseTimeoutTicks && Volatile.Read(ref _latched) == 0; }
        }
        /// <summary>★blockWhile — 키 단선/미수신/래치 시 무장 하향(tighten-only). 가역: 펄스 복원 시 자동 재개(래치 전까지).</summary>
        public bool SafeHoldActive => !KeyAlive;

        /// <summary>물리 키 펄스 수신(δ=0). 하드웨어 접점 생존 시에만 호출 — 심박 시각 스탬프 + 연속 단선 카운터 리셋.</summary>
        public void Pulse()
        {
            if (Volatile.Read(ref _latched) == 1) return;   // 하드 래치 후엔 펄스 무시(재무장 금지·fail-closed)
            long now = _nowTicks();
            long prev = Volatile.Read(ref _lastPulseTicks);
            Volatile.Write(ref _lastPulseTicks, now <= 0L ? 1L : now);   // 0 은 미수신 예약 — 최소 1
            Interlocked.Increment(ref _pulses);
            Volatile.Write(ref _sustainCount, 0L);
            if (prev != 0L && (now - prev) > _pulseTimeoutTicks) Interlocked.Increment(ref _restores);  // 단선 후 복원
        }

        /// <summary>
        /// 주기 단선 평가(δ=0·50ms 타이머 or 테스트 직접 호출 = EvaluateKeyPulseForTest). 살아있다 단선(dormant 아님)이
        /// sustainThreshold 연속이면 1회성 하드 래치 + onKeyLost(무장 하드 차단). dormant(미수신)는 카운트 안 함.
        /// </summary>
        public void CheckOnce()
        {
            if (Volatile.Read(ref _latched) == 1) return;
            long lp = Volatile.Read(ref _lastPulseTicks);
            if (lp == 0L) return;   // 미수신(dormant) — 단선 아님(SafeHold 는 KeyAlive=false 로 이미 tighten)
            bool alive = (_nowTicks() - lp) <= _pulseTimeoutTicks;
            if (alive) { Volatile.Write(ref _sustainCount, 0L); return; }
            // 살아있다가 단선 — 실제 접점 불량.
            Interlocked.Increment(ref _losses);
            long sustain = Interlocked.Increment(ref _sustainCount);
            if (sustain >= _sustainThreshold)
            {
                if (Interlocked.CompareExchange(ref _latched, 1, 0) == 0)   // 1회성 하드 래치
                { try { _onKeyLost?.Invoke(); } catch { /* 래치는 유지(action 예외 무해) */ } }
            }
        }

        /// <summary>결정론 테스트 훅 — 주입 시각으로 단선 평가 1회(타이머 우회).</summary>
        public void EvaluateKeyPulseForTest() => CheckOnce();

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposedLatch, 1) == 1) return;
            try { _timer?.Dispose(); } catch { }
        }
    }
}
