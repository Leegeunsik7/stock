// ============================================================================
// DeterministicVirtualTime — v4.91.0: 결정론 가상 시간 패브릭(카오스 그리드 기반)
//
//  실제 벽시계·Task.Delay·Thread.Sleep 없이 가상 시간선을 결정론적으로 전진시키는 이산사건(discrete-event)
//  시뮬레이션 커널. 세 부품:
//    · VirtualClock       — 단조 증가 가상 클럭(100ns tick). ★시간 역전 물리 차단(음수/후퇴 전진 throw).
//    · VirtualScheduler   — ★가상 비동기 지연 큐: 실 대기 0으로 'delay 후 콜백'을 예약(δ=0). ★무한루프 방어(maxSteps).
//    · DeterministicPrng  — SplitMix64 시드 PRNG(무할당 struct). 무작위가 아닌 '재현 가능한' 결함 스트림.
//
//  ── 불변식 ──────────────────────────────────────────────────────────────────
//   결정론: 벽시계/실난수 미사용(시드 PRNG 만). 동일 시드+동일 이벤트 → 동일 전이.
//   δ=0    : 스케줄러는 실 Task.Delay/Thread.Sleep 을 소비하지 않는다(가상 지연=우선순위 큐 정렬).
//   방어   : 시간 역전(AdvanceTo<now) throw · 무한루프(maxSteps 초과) throw — 테스트 러너 행(hang) 원천 차단.
// ============================================================================
using System;
using System.Collections.Generic;

namespace StockManager.Common
{
    /// <summary>가상 스케줄러가 무한루프/행에 빠지는 것을 방어할 때 던지는 예외(테스트 러너 hang 차단).</summary>
    public sealed class ChaosGridHangException : Exception
    {
        public ChaosGridHangException(string message) : base(message) { }
    }

    /// <summary>단조 증가 가상 클럭(100ns DateTime tick). 시간 역전은 물리적으로 불가(throw).</summary>
    public sealed class VirtualClock
    {
        private long _nowTicks;

        /// <summary>가상:현실 시간 압축 배율(정보용·1000× 등). 스케줄러 전진 자체는 이 값과 무관하게 이벤트 due 로 결정.</summary>
        public long ScaleFactor { get; }

        public VirtualClock(long startTicks = 0, long scaleFactor = 1000)
        {
            if (startTicks < 0) throw new ArgumentOutOfRangeException(nameof(startTicks), "가상 시작 tick 은 음수 불가");
            if (scaleFactor < 1) throw new ArgumentOutOfRangeException(nameof(scaleFactor), "압축 배율은 1 이상");
            _nowTicks = startTicks;
            ScaleFactor = scaleFactor;
        }

        public long NowTicks => _nowTicks;
        public DateTime UtcNow => new DateTime(_nowTicks, DateTimeKind.Utc);

        /// <summary>가상 시간 전진(단조). ★음수 전진(시간 역전) throw. long 경계 초과 checked.</summary>
        public void Advance(long deltaTicks)
        {
            if (deltaTicks < 0)
                throw new InvalidOperationException($"VirtualClock 시간 역전 금지 — deltaTicks={deltaTicks} < 0");
            _nowTicks = checked(_nowTicks + deltaTicks);
        }

        /// <summary>가상 시간을 목표로 전진(단조). ★목표가 현재보다 과거면 throw(시간 역전 맹점 차단).</summary>
        public void AdvanceTo(long targetTicks)
        {
            if (targetTicks < _nowTicks)
                throw new InvalidOperationException($"VirtualClock 시간 역전 금지 — target={targetTicks} < now={_nowTicks}");
            _nowTicks = targetTicks;
        }

        /// <summary>주입용 tick 소스(Func&lt;long&gt; 계열 컴포넌트: KiwoomBridgeClient·QFxCircuitBreaker·RingTelemetryPublisher).</summary>
        public Func<long> TickSource => () => _nowTicks;
        /// <summary>주입용 UTC 소스(Func&lt;DateTime&gt; 계열: IgnitionLock·SafeIgnitionSequencer).</summary>
        public Func<DateTime> UtcSource => () => new DateTime(_nowTicks, DateTimeKind.Utc);
    }

    /// <summary>
    /// ★가상 비동기 지연 큐 — 실 물리 대기(Task.Delay/Thread.Sleep) 없이 'delayTicks 후 콜백'을 예약·순차 실행하는
    /// 이산사건 스케줄러. 네트워크/IPC 레이턴시 지터를 δ=0·결정론으로 모사한다. 동일 가상시각 이벤트는 삽입 seq 로
    /// 결정론 순서. 콜백은 추가 이벤트를 예약할 수 있으며, 무한 재예약은 maxSteps 로 방어(행 차단).
    /// </summary>
    public sealed class VirtualScheduler
    {
        private readonly VirtualClock _clock;
        // (due, seq) 튜플 우선순위 — 기본 튜플 비교자가 사전식(due→seq)이라 동일 시각 결정론 순서 보장.
        private readonly PriorityQueue<Action, (long due, long seq)> _q = new();
        private long _seq;

        public VirtualScheduler(VirtualClock clock)
            => _clock = clock ?? throw new ArgumentNullException(nameof(clock));

        public int PendingCount => _q.Count;

        /// <summary>delayTicks 후(가상) 콜백 예약. ★음수 지연 throw. 실 대기 0(δ=0).</summary>
        public void Schedule(long delayTicks, Action callback)
        {
            if (delayTicks < 0) throw new InvalidOperationException($"VirtualScheduler 음수 지연 금지 — {delayTicks}");
            if (callback is null) throw new ArgumentNullException(nameof(callback));
            long due = checked(_clock.NowTicks + delayTicks);
            _q.Enqueue(callback, (due, _seq++));
        }

        /// <summary>
        /// endTicks 까지의 이벤트를 due 오름차순으로 실행하며 가상 시간을 전진. 반환: 실행한 이벤트 수.
        /// ★maxSteps 초과 시 ChaosGridHangException(무한 재예약·행 차단). ★AdvanceTo 가 시간 역전을 throw.
        /// </summary>
        public int RunUntil(long endTicks, int maxSteps)
        {
            if (maxSteps <= 0) throw new ArgumentOutOfRangeException(nameof(maxSteps));
            int steps = 0;
            while (_q.TryPeek(out _, out var pri))
            {
                if (pri.due > endTicks) break;
                if (++steps > maxSteps)
                    throw new ChaosGridHangException(
                        $"VirtualScheduler 무한루프 방어 — maxSteps({maxSteps}) 초과 @ virtualTicks={_clock.NowTicks} (재예약 폭주 의심)");
                var cb = _q.Dequeue();
                _clock.AdvanceTo(pri.due);   // ★단조 전진(과거 due 면 clock 이 throw)
                cb();                         // 콜백이 추가 이벤트 예약 가능(가상 비동기 지연 연쇄)
            }
            return steps;
        }

        /// <summary>예약된 모든 이벤트를 소진(maxSteps 방어). 유한 이벤트 시나리오 종료용.</summary>
        public int DrainAll(int maxSteps) => RunUntil(long.MaxValue, maxSteps);
    }

    /// <summary>
    /// SplitMix64 결정론 PRNG(무할당 값 타입). System.Random·Math.Random 을 쓰지 않아 크로스-런타임 재현 보장.
    /// 시드 하나로 '무작위처럼 보이나 완전 재현 가능한' 결함 스트림을 생성한다.
    /// </summary>
    public struct DeterministicPrng
    {
        private ulong _state;
        public DeterministicPrng(ulong seed) => _state = seed;

        public ulong NextULong()
        {
            unchecked
            {
                _state += 0x9E3779B97F4A7C15UL;
                ulong z = _state;
                z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
                z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
                return z ^ (z >> 31);
            }
        }

        /// <summary>[0, maxExclusive) 정수(균등 근사). maxExclusive&gt;0 필수.</summary>
        public long NextLong(long maxExclusive)
        {
            if (maxExclusive <= 0) throw new ArgumentOutOfRangeException(nameof(maxExclusive));
            return (long)(NextULong() % (ulong)maxExclusive);
        }

        public int NextInt(int maxExclusive) => (int)NextLong(maxExclusive);

        /// <summary>[0.0, 1.0) 실수(53비트 정밀).</summary>
        public double NextUnit() => (NextULong() >> 11) * (1.0 / (1UL << 53));
    }
}
