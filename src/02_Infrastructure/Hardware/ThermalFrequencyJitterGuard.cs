// ============================================================================
// ThermalFrequencyJitterGuard — v7.3.0 CPU 서멀/주파수 스로틀링 센티널
//
//  장시간 고부하에서 CPU BCLK/Turbo 주파수가 강하하거나 발열 스로틀링이 걸리면, 동일 연산이 '더 느려진다'.
//  이 센티널은 고정 정수 워크로드(데이터 의존 체인)를 QPC(Stopwatch·정속 타이머)로 브래킷해 '유효 주파수'를
//  δ=0 로 실측한다: 스로틀링 시 고정 워크로드의 wall-time(=QPC tick)이 늘어난다 ⇒ 유효주파수 ‰ 하락.
//  유효주파수가 임계 미만으로 지속 강하하면 핫패스 무장을 '가역 안전 홀드'로 전환해 클럭 강하 구간의 오발주·
//  체결 지연 위험을 선제 차단한다(주파수 회복 시 자동 해제 — 자기 만료·가역).
//
//  ── 정직 고지 & 오탐 방어 ────────────────────────────────────────────────
//  · '유효 주파수'는 나노초 절대 클럭이 아니라 '고정 워크로드 처리량 대비 기준선(최고 관측)의 비율'이다 —
//    스로틀링/주파수 강하를 상대적으로 정확히 포착하되, 절대 MHz 를 주장하지 않는다(perf-counter 비의존·이식적).
//  · 버스트 다중 측정의 '최소값'만 채택(OS 선점 노이즈 제거) + GC 가드(측정 중 GC 발생 시 폐기) + 지속 K회
//    요구로 순간 스파이크 오탐을 차단한다. 안전 홀드는 tighten-only(가역)만 — 타이밍 휴리스틱으로 비가역 강등 금지.
//
//  ★불변: δ=0(Sample 핫패스 힙 0·정수/QPC/Volatile) · 결정론(순수 산술·주입 시계) · fail-closed(스로틀→안전 홀드
//    ·무장 술어 blockWhile 억제) · liveArmed:false(순수 관측 — IBroker·소켓·onSend 참조 0·홀드는 '더 안전한 방향'만).
// ============================================================================
using System;
using System.Diagnostics;
using System.Threading;

namespace MS2E.Infrastructure
{
    /// <summary>서멀/주파수 스로틀링 센티널. Start() → 전용 스레드 주기 Sample, Dispose() → 정지. 홀드는 가역.</summary>
    public sealed class ThermalFrequencyJitterGuard : IDisposable
    {
        private readonly int  _iters;              // 버스트당 워크로드 반복(보정)
        private readonly int  _bursts;             // 샘플당 버스트 수(최소 채택)
        private readonly long _throttlePermille;   // 유효주파수 임계‰(미만=스로틀 후보, 예 880=12% 강하)
        private readonly int  _sustainSamples;     // 지속 K회 초과 시 홀드
        private readonly int  _intervalMs;
        private readonly Func<long> _nowTicks;     // QPC(주입·기본 Stopwatch.GetTimestamp)

        private long _baselineTicks;               // 최소 소요(최고 유효주파수 기준선)
        private long _effectiveFreqPermille = 1000;
        private long _throttleHolds;
        private long _samples;
        private int  _consecThrottle;              // 단일 라이터(전용 스레드) — 플레인
        private int  _safeHold;                    // 0/1(가역) — 무장 술어 blockWhile
        private long _sink;                        // DCE 방지(워크로드 결과 저장)

        private Thread? _thread;
        private readonly ManualResetEventSlim _stop = new(false);
        private int _started;
        private int _disposed;

        public ThermalFrequencyJitterGuard(int iters = 20_000, int bursts = 4, long throttlePermille = 880,
                                           int sustainSamples = 3, int intervalMs = 250, Func<long>? nowTicks = null)
        {
            _iters = Math.Max(1000, iters);
            _bursts = Math.Max(1, bursts);
            _throttlePermille = Math.Clamp(throttlePermille, 100, 999);
            _sustainSamples = Math.Max(1, sustainSamples);
            _intervalMs = Math.Max(20, intervalMs);
            _nowTicks = nowTicks ?? Stopwatch.GetTimestamp;
        }

        /// <summary>★안전 홀드 활성 — 무장 술어 blockWhile 이 읽어 신규 발주를 tighten-only 억제(가역).</summary>
        public bool SafeHoldActive         => Volatile.Read(ref _safeHold) == 1;
        public long EffectiveFreqPermille  => Volatile.Read(ref _effectiveFreqPermille);
        public long ThrottleHolds          => Interlocked.Read(ref _throttleHolds);
        public long Samples                => Interlocked.Read(ref _samples);
        public long BaselineTicks          => Volatile.Read(ref _baselineTicks);

        public void Start()
        {
            if (Interlocked.Exchange(ref _started, 1) == 1) return;
            _thread = new Thread(Loop) { IsBackground = true, Name = "ThermalFreqGuard", Priority = ThreadPriority.BelowNormal };
            _thread.Start();
        }

        private void Loop()
        {
            // ★종료 경합 하드닝: 고부하 기아로 Dispose 가 Join 타임아웃 후 _stop 을 dispose 하더라도(사실상 방지되나
            //   벨트&멜빵) 살아난 Wait() 의 ObjectDisposedException 을 삼켜 백그라운드 스레드 크래시 대신 안전 탈출.
            try { while (!_stop.Wait(_intervalMs)) Sample(); }
            catch (ObjectDisposedException) { /* 종료 경합 — 안전 탈출 */ }
        }

        /// <summary>1회 유효주파수 실측 + 스로틀 판정(δ=0). 전용 스레드가 주기 호출(단일 라이터).</summary>
        public void Sample()
        {
            int gc0 = GC.CollectionCount(0);
            long best = long.MaxValue;
            for (int b = 0; b < _bursts; b++)
            {
                long t0 = _nowTicks();
                long r  = CalibratedWorkload(_iters);
                long dt = _nowTicks() - t0;
                _sink = r;                          // DCE 방지(결과 소비)
                if (dt > 0 && dt < best) best = dt;
            }
            if (GC.CollectionCount(0) != gc0 || best == long.MaxValue) return;   // GC 개입/이상 → 폐기(오탐 방지)
            Interlocked.Increment(ref _samples);

            long baseline = Volatile.Read(ref _baselineTicks);
            if (baseline == 0 || best < baseline) { Volatile.Write(ref _baselineTicks, best); baseline = best; }
            long freqPermille = best > 0 ? baseline * 1000L / best : 1000L;   // 스로틀 시 best↑ → ‰↓
            Volatile.Write(ref _effectiveFreqPermille, freqPermille);

            if (freqPermille < _throttlePermille)
            {
                if (++_consecThrottle >= _sustainSamples && Volatile.Read(ref _safeHold) == 0)
                { Volatile.Write(ref _safeHold, 1); Interlocked.Increment(ref _throttleHolds); }
            }
            else { _consecThrottle = 0; Volatile.Write(ref _safeHold, 0); }   // 회복 → 홀드 해제(가역·자기 만료)
        }

        // 고정 데이터 의존 정수 체인(병렬/스킵 불가) — 소요 QPC ∝ 1/유효주파수. δ=0(무할당).
        private static long CalibratedWorkload(int iters)
        {
            ulong x = 0x9E3779B97F4A7C15UL;
            for (int i = 0; i < iters; i++)
            {
                x ^= (ulong)i;
                x *= 0xBF58476D1CE4E5B9UL;   // 곱셈 지연 체인(데이터 의존 — CPU 가 건너뛸 수 없음)
                x ^= x >> 29;
            }
            return (long)x;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1) return;   // ★멱등(다중 FormClosed/재진입 안전)
            _stop.Set();
            // ★스레드가 '입증되게' 종료된 경우에만 _stop dispose. Join 타임아웃(가드가 도는 고부하 기아 상황)이면 살아있는
            //   Wait() 위에서 dispose 시 백그라운드 ObjectDisposedException→프로세스 크래시 → 안전하게 누수 허용(단일 인스턴스·경미).
            bool exited;
            try { exited = _thread == null || _thread.Join(2000); } catch { exited = false; }
            if (exited) { try { _stop.Dispose(); } catch { } }
        }
    }
}
