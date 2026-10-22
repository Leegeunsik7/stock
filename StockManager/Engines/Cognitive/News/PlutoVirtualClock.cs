// ============================================================================
// PlutoVirtualClock — v12.18.64-DEV: 플루토 브레인 2단계 — 주입 가능한 가상 시계(IVirtualClock · 판독 δ=0)
//
//   감쇄(DecayedScore) · 5분 미래 필터 · 24h 과거 필터 · 버퍼 우세 비교의 '지금'을 고정 DateTime.UtcNow 대신 이 시계에서 읽는다.
//   ① SystemVirtualClock: 실거래/실시세 모드 — 시스템 UTC 벽시계(싱글턴 · 상태 0).
//   ② TickSyncedVirtualClock: 합성 시세 · 리플레이 모드 — 시세 주입기가 틱 타임스탬프(UTC)를 Observe → 단조 최댓값 래치(CAS · 역행 무시).
//     미동기(Observe 0회 · Reset 후) = 대체 시계(기본 시스템) 판독 → 실시세 복귀 시 Reset 한 번으로 벽시계 도메인 복원.
//     ★두 아레나가 서로 다른 합성 도메인을 동시에 공급하면 앞선 도메인이 채택된다 — 뒤처진 도메인의 판독은 '미래 뉴스'가 되어 감쇄 0(fail-closed).
//   ③ ManualVirtualClock: 백테스트 · 단위 시험 — Set/Advance 로 결정론 시간 가속(10분/30분/60분 경과 재현).
//   ★δ=0: UtcNowTicks 판독 = Volatile/Interlocked 정수 판독뿐(할당 · 잠금 · 박싱 0) — 아레나 정산 · 분석기 워커 어디서 불러도 무할당.
// ============================================================================
using System;
using System.Threading;

namespace StockManager.Engines.Cognitive.News
{
    /// <summary>플루토 뉴스 시간 도메인(UTC ticks). 구현은 판독 할당 0 · 스레드 안전이어야 한다.</summary>
    public interface IVirtualClock
    {
        /// <summary>현재 UTC 시각(100ns ticks · 양수).</summary>
        long UtcNowTicks { get; }

        /// <summary>true = 합성/리플레이 틱 도메인(벽시계 아님).</summary>
        bool IsSynthetic { get; }
    }

    /// <summary>실거래 모드 — 시스템 UTC 벽시계.</summary>
    public sealed class SystemVirtualClock : IVirtualClock
    {
        public static readonly SystemVirtualClock Instance = new();

        private SystemVirtualClock() { }

        public long UtcNowTicks => DateTime.UtcNow.Ticks;
        public bool IsSynthetic => false;
    }

    /// <summary>합성 시세 · 리플레이 — 틱 타임스탬프 단조 래치(미동기 = 대체 시계).</summary>
    public sealed class TickSyncedVirtualClock : IVirtualClock
    {
        private readonly IVirtualClock _fallback;
        private long _synced, _observed, _ignored;

        public TickSyncedVirtualClock(IVirtualClock? fallback = null)
        {
            _fallback = fallback ?? SystemVirtualClock.Instance;
        }

        public long UtcNowTicks
        {
            get
            {
                long t = Volatile.Read(ref _synced);
                return t > 0 ? t : _fallback.UtcNowTicks;
            }
        }

        public bool IsSynthetic => Volatile.Read(ref _synced) > 0;
        /// <summary>반영된 틱 수(전진 또는 동일 시각).</summary>
        public long Observed => Interlocked.Read(ref _observed);
        /// <summary>무시된 틱 수(무효 · 역행).</summary>
        public long Ignored => Interlocked.Read(ref _ignored);

        /// <summary>틱 타임스탬프(UTC) 반영. 무효(≤0 · 범위 밖) · 역행 = 무시(false · 래치 유지).</summary>
        public bool Observe(long utcTicks)
        {
            if (utcTicks <= 0 || utcTicks > DateTime.MaxValue.Ticks)
            {
                Interlocked.Increment(ref _ignored);
                return false;
            }
            long cur = Volatile.Read(ref _synced);
            while (utcTicks > cur)
            {
                long prev = Interlocked.CompareExchange(ref _synced, utcTicks, cur);
                if (prev == cur) { Interlocked.Increment(ref _observed); return true; }
                cur = prev;
            }
            if (utcTicks == cur) { Interlocked.Increment(ref _observed); return true; }
            Interlocked.Increment(ref _ignored);
            return false;
        }

        /// <summary>동기 해제(실시세 복귀 · 합성 재시작) → 대체 시계 판독.</summary>
        public void Reset() => Volatile.Write(ref _synced, 0);
    }

    /// <summary>백테스트 · 단위 시험 — 수동 시계(Set · Advance · 역행 금지).</summary>
    public sealed class ManualVirtualClock : IVirtualClock
    {
        private long _now;

        public ManualVirtualClock(long startUtcTicks)
        {
            if (startUtcTicks <= 0 || startUtcTicks > DateTime.MaxValue.Ticks) throw new ArgumentOutOfRangeException(nameof(startUtcTicks));
            _now = startUtcTicks;
        }

        public long UtcNowTicks => Interlocked.Read(ref _now);
        public bool IsSynthetic => true;

        /// <summary>시간 전진(음수 = ArgumentOutOfRange · 상한 초과 = 포화).</summary>
        public long Advance(TimeSpan delta)
        {
            if (delta < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(delta));
            long cur = Interlocked.Read(ref _now);
            long next = delta.Ticks > DateTime.MaxValue.Ticks - cur ? DateTime.MaxValue.Ticks : cur + delta.Ticks;
            Interlocked.Exchange(ref _now, next);
            return next;
        }

        /// <summary>절대 시각 지정(역행 = ArgumentOutOfRange).</summary>
        public void Set(long utcTicks)
        {
            if (utcTicks < Interlocked.Read(ref _now) || utcTicks > DateTime.MaxValue.Ticks) throw new ArgumentOutOfRangeException(nameof(utcTicks));
            Interlocked.Exchange(ref _now, utcTicks);
        }
    }

    /// <summary>1단계 호환(Func&lt;long&gt; 주입) 어댑터.</summary>
    internal sealed class DelegateVirtualClock : IVirtualClock
    {
        private readonly Func<long> _now;

        public DelegateVirtualClock(Func<long> now) => _now = now ?? throw new ArgumentNullException(nameof(now));

        public long UtcNowTicks => _now();
        public bool IsSynthetic => true;
    }
}
