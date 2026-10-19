// ============================================================================
// VirtualTimeProvider — [v12.18.17-DEV] 결정론 가상 시계(BCL TimeProvider 파생 · 신규 NuGet 0)
//
//  시각은 Advance 로만 흐른다(벽시계 · 스레드풀 부하 무관). 타이머(CancellationTokenSource(TimeSpan, TimeProvider) · Task.WaitAsync(TimeSpan, TimeProvider))는
//  만기 시각 순으로 Advance 호출 스레드에서 발화한다 — 시한 직전(−1틱)에는 절대 발화하지 않고, 시한 도달 시 반드시 발화한다.
//  콜백은 잠금 밖에서 호출(콜백이 타이머를 만들거나 Dispose 해도 교착 0). 주기 타이머 = 다음 만기로 재무장.
// ============================================================================
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace StockManager.Tests
{
    internal sealed class VirtualTimeProvider : TimeProvider
    {
        private static readonly DateTimeOffset Epoch = new(2026, 10, 18, 0, 0, 0, TimeSpan.Zero);
        private readonly object _gate = new();
        private readonly List<VirtualTimer> _timers = new();
        private long _now;                                                         // 경과 틱(100ns)

        public TimeSpan Elapsed { get { lock (_gate) return new TimeSpan(_now); } }
        public override DateTimeOffset GetUtcNow() => Epoch + Elapsed;
        public override long GetTimestamp() { lock (_gate) return _now; }
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        /// <summary>무장된(만기 대기) 타이머 수.</summary>
        public int ArmedTimers { get { lock (_gate) { int n = 0; foreach (var t in _timers) if (t.Due >= 0) n++; return n; } } }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            ArgumentNullException.ThrowIfNull(callback);
            var t = new VirtualTimer(this, callback, state);
            lock (_gate) _timers.Add(t);
            t.Change(dueTime, period);
            return t;
        }

        /// <summary>시각을 by 만큼 진행 — 그 사이 만기 타이머를 만기 순으로 발화(같은 만기 = 생성 순).</summary>
        public void Advance(TimeSpan by)
        {
            if (by < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(by));
            long target;
            lock (_gate) target = _now + by.Ticks;
            while (true)
            {
                VirtualTimer? next = null;
                lock (_gate)
                {
                    foreach (var t in _timers)
                        if (t.Due >= 0 && t.Due <= target && (next is null || t.Due < next.Due)) next = t;
                    if (next is null) { _now = target; return; }
                    if (next.Due > _now) _now = next.Due;
                    next.Due = next.Period > 0 ? next.Due + next.Period : -1;
                }
                next.Callback(next.State);
            }
        }

        private void Arm(VirtualTimer t, TimeSpan due, TimeSpan period)
        {
            lock (_gate)
            {
                t.Due = due == Timeout.InfiniteTimeSpan ? -1 : _now + Math.Max(0, due.Ticks);
                t.Period = period == Timeout.InfiniteTimeSpan || period <= TimeSpan.Zero ? 0 : period.Ticks;
            }
        }

        private void Remove(VirtualTimer t) { lock (_gate) _timers.Remove(t); }

        private sealed class VirtualTimer : ITimer
        {
            private readonly VirtualTimeProvider _owner;
            public readonly TimerCallback Callback;
            public readonly object? State;
            public long Due = -1, Period;
            private int _disposed;

            public VirtualTimer(VirtualTimeProvider owner, TimerCallback callback, object? state) { _owner = owner; Callback = callback; State = state; }

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                if (Volatile.Read(ref _disposed) != 0) return false;
                _owner.Arm(this, dueTime, period);
                return true;
            }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0) _owner.Remove(this);
            }

            public ValueTask DisposeAsync() { Dispose(); return default; }
        }
    }
}
