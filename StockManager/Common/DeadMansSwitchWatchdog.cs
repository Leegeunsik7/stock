// ============================================================================
// DeadMansSwitchWatchdog — v7.4.0 데드맨 스위치 왓치독(엔진 행 → 원자 밸브 차단)
//
//  메인 엔진/터미널 UI 가 무장 상태에서 응답 불능(Hang)에 빠지면, 미체결 주문이 시장에 방치되고 리스크 밸브·
//  VaR·서멀 가드가 전부 갱신을 멈춰 '눈먼 무장' 이 된다. 이 왓치독은 메인 엔진과 '완전히 격리된 독립 스레드' 로,
//  엔진이 주기적으로 Beat() 심박을 넣는 한에서만 안심한다. 무장 상태에서 심박이 hangThresholdMs(기본 500ms) 이상
//  끊기면 → onDeadManTrip(미체결 전량 취소 + OrderAttestationService.ForceFreeze 하드 밸브 차단)을 1회 원자 격발한다.
//
//  ★독립성: 왓치독 스레드는 엔진 락/DI/힙과 무관하게 QPC + Volatile long 만으로 판정한다 — 엔진이 GC stall·데드락·
//    스레드풀 기아에 빠져도 왓치독은 살아 판정한다. isArmed 평가가 throw/불능이면 '무장으로 간주'(fail-closed — 못
//    끊는 것보다 끊는 게 안전). ★불변: δ=0(Beat/CheckOnce 힙 0·Volatile) · 결정론(주입 시계·CheckOnce 테스트 가능) ·
//    liveArmed:false(순수 관측 — 트립은 '차단'만·발주 유발 경로 0) · fail-closed(불확실 → 트립).
// ============================================================================
using System;
using System.Diagnostics;
using System.Threading;

namespace StockManager.Common
{
    /// <summary>데드맨 스위치 왓치독(격리 스레드·δ=0 심박). Beat() 심박 끊김+무장 → onDeadManTrip 원자 격발. IDisposable.</summary>
    public sealed class DeadMansSwitchWatchdog : IDisposable
    {
        private readonly Func<bool>  _isArmed;           // 무장 여부(경량 — db I/O 회피 권장)
        private readonly Action      _onDeadManTrip;     // 미체결 전량 취소 + ForceFreeze(밸브 하드 차단)
        private readonly long        _hangTicks;         // 행 임계(QPC ticks)
        private readonly int         _pollMs;
        private readonly Func<long>  _nowTicks;
        private readonly long        _freq;

        private long _lastBeatTicks;
        private long _beats;
        private int  _tripped;                            // 0/1 — 1회 원자 래치
        private long _tripCount;

        private Thread? _thread;
        private readonly ManualResetEventSlim _stop = new(false);
        private int _started, _disposed;

        public DeadMansSwitchWatchdog(Func<bool> isArmed, Action onDeadManTrip,
                                      int hangThresholdMs = 500, int pollMs = 100,
                                      Func<long>? nowTicks = null, long tickFrequency = 0)
        {
            _isArmed       = isArmed       ?? throw new ArgumentNullException(nameof(isArmed));
            _onDeadManTrip = onDeadManTrip ?? throw new ArgumentNullException(nameof(onDeadManTrip));
            _nowTicks      = nowTicks ?? Stopwatch.GetTimestamp;
            _freq          = tickFrequency > 0 ? tickFrequency : Stopwatch.Frequency;
            int hangMs     = hangThresholdMs < 50 ? 50 : hangThresholdMs;
            _hangTicks     = _freq * hangMs / 1000L;
            _pollMs        = Math.Clamp(pollMs, 20, 1000);
            Volatile.Write(ref _lastBeatTicks, _nowTicks());   // 개시 시점 심박(즉시 오탐 방지)
        }

        public bool Tripped     => Volatile.Read(ref _tripped) == 1;
        public long Beats       => Interlocked.Read(ref _beats);
        public long TripCount   => Interlocked.Read(ref _tripCount);
        /// <summary>최근 심박 이후 경과(ms·δ=0·UI). 클수록 위험.</summary>
        public long HeartbeatAgeMs
        {
            get { long age = _nowTicks() - Volatile.Read(ref _lastBeatTicks); return age <= 0L ? 0L : age * 1000L / _freq; }
        }

        /// <summary>엔진/UI 심박(δ=0·원자·핫패스). 살아있는 한 주기적으로 호출 — 끊기면 왓치독이 행으로 판정.</summary>
        public void Beat()
        {
            Volatile.Write(ref _lastBeatTicks, _nowTicks());
            Interlocked.Increment(ref _beats);
        }

        public void Start()
        {
            if (Interlocked.Exchange(ref _started, 1) == 1) return;
            _thread = new Thread(Loop) { IsBackground = true, Name = "DeadMansSwitch", Priority = ThreadPriority.AboveNormal };
            _thread.Start();
        }

        private void Loop()
        {
            // ★종료 경합 하드닝: Dispose 가 Join 타임아웃 후 _stop dispose 해도 ObjectDisposedException 흡수(스레드 크래시 방지).
            try { while (!_stop.Wait(_pollMs)) CheckOnce(); }
            catch (ObjectDisposedException) { /* 종료 경합 — 안전 탈출 */ }
        }

        /// <summary>1회 행 판정(δ=0·독립 스레드 주기 호출·테스트 직접 호출). 무장 ∧ 심박 임계 초과 → 원자 트립.</summary>
        public void CheckOnce()
        {
            if (Volatile.Read(ref _tripped) == 1) return;
            bool armed; try { armed = _isArmed(); } catch { armed = true; }   // 평가 불능 → 무장 간주(fail-closed)
            if (!armed) return;
            long age = _nowTicks() - Volatile.Read(ref _lastBeatTicks);
            if (age > _hangTicks) Trip();
        }

        private void Trip()
        {
            if (Interlocked.CompareExchange(ref _tripped, 1, 0) != 0) return;   // 1회만
            Interlocked.Increment(ref _tripCount);
            try { _onDeadManTrip(); } catch { /* 트립 액션 실패해도 래치 유지 — 상위 프리미티브(ForceFreeze)가 방어 */ }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
            _stop.Set();
            bool exited; try { exited = _thread == null || _thread.Join(2000); } catch { exited = false; }
            if (exited) { try { _stop.Dispose(); } catch { } }   // 종료 입증 후에만 dispose(살아있는 Wait 위 dispose 방지)
        }
    }
}
