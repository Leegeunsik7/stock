// ============================================================================
// StrategySlot — S19 #3: 락-프리 원자 핫스왑 컨테이너 (인프라 계약)
//
//  달리는 집행 루프의 활성 전략을 단 1ms의 락 경합도 없이 교체하는 격리 제어기.
//  StrategySpec 이 불변 record 이므로, 참조 1개의 원자 교체만으로 무결한 핫스왑이 성립한다.
//
//  ── 락-프리 패턴 ───────────────────────────────────────────────────────────
//  · 독출(집행 루프): Volatile.Read(ref _active) — O(1), 락 없음, 항상 완결된 단일 참조.
//  · 교체: Interlocked.Exchange(ref _active, next) — 단일 버스 명령 수준 원자 스왑.
//  · ⚠️ volatile 키워드는 쓰지 않는다 — volatile 필드를 Interlocked 에 ref 로 넘기면
//    CS0420 경고가 발생하기 때문(TreatWarningsAsErrors 하드 게이트 위반). Volatile.Read +
//    Interlocked.Exchange 조합이 동일한 메모리 배리어 의미론을 제공한다.
//
//  ── 그레이스풀 인수인계 (§3.4) ─────────────────────────────────────────────
//  교체 순간 구 전략의 잔고 포지션이 미궁에 빠지지 않도록 계층 권한을 분리한다:
//    · 신전략(_active)   = 그레이스 중 OpenOnly (신규 진입 + 자기 포지션 관리)
//    · 구전략(_draining) = 그레이스 중 CloseOnly (잔여 포지션 청산만, 신규 진입 차단)
//    · 인수인계 완료 시   = 신전략 Full 승격, 구전략 Disabled
//
//  외부 의존성 0 · 결정론 · 사이드 이펙트는 내부 원자 상태 변경뿐.
// ============================================================================
using System.Threading;

namespace MS2E.Infrastructure
{
    /// <summary>슬롯이 부여하는 집행 권한.</summary>
    public enum SlotPermission
    {
        Disabled,   // 어떤 신호도 권한 없음
        CloseOnly,  // 청산(Exit) 신호만 — 인수인계 중 구 전략
        OpenOnly,   // 진입(Open) + 자기 포지션 관리 — 인수인계 중 신 전략
        Full        // 진입·청산 모두 — 인수인계 완료 후 단독 전략
    }

    public sealed class StrategySlot
    {
        private StrategySpec? _active;     // 현재(신규) 전략 — 신규 진입 담당
        private StrategySpec? _draining;   // 인수인계 중 구 전략 — 청산 전용
        private long          _generation; // ABA 방지 + 감사 카운터
        private int           _grace;      // 0=정상 1=그레이스 윈도우 (Interlocked)

        // ── 락-프리 독출 ──────────────────────────────────────────────────────
        public StrategySpec? Active     => Volatile.Read(ref _active);
        public StrategySpec? Draining   => Volatile.Read(ref _draining);
        public long          Generation => Interlocked.Read(ref _generation);
        public bool          InGraceWindow => Volatile.Read(ref _grace) != 0;

        // ── 원자 핫스왑 ───────────────────────────────────────────────────────

        /// <summary>
        /// 활성 전략을 next 로 원자 교체한다. 직전 전략이 있으면 청산 전용으로 강등하고
        /// 그레이스 윈도우를 연다. 반환값은 직전 활성 전략(없으면 null).
        /// </summary>
        public StrategySpec? Swap(StrategySpec next)
        {
            if (next is null) throw new System.ArgumentNullException(nameof(next));

            // ⚛ 단일 원자 교체 — 독출 스레드는 교체 전/후 항상 완결된 참조를 본다
            var prev = Interlocked.Exchange(ref _active, next);

            // 직전 전략을 청산 전용으로 강등 (없으면 그레이스 불필요)
            Interlocked.Exchange(ref _draining, prev);
            Interlocked.Exchange(ref _grace, prev is null ? 0 : 1);
            Interlocked.Increment(ref _generation);
            return prev;
        }

        /// <summary>
        /// 인수인계 완료 — 구 전략의 잔여 포지션이 모두 청산되어 청산 전용 슬롯을 비운다.
        /// 이후 신전략은 Full 권한으로 승격된다.
        /// </summary>
        public void CompleteHandover()
        {
            Interlocked.Exchange(ref _draining, null);
            Interlocked.Exchange(ref _grace, 0);
        }

        /// <summary>전체 비활성화 — 활성·청산 슬롯을 모두 비운다(긴급 무전략 안착).</summary>
        public StrategySpec? Clear()
        {
            var prev = Interlocked.Exchange(ref _active, null);
            Interlocked.Exchange(ref _draining, null);
            Interlocked.Exchange(ref _grace, 0);
            Interlocked.Increment(ref _generation);
            return prev;
        }

        // ── 계층 권한 판정 ────────────────────────────────────────────────────

        /// <summary>주어진 전략이 현재 슬롯에서 갖는 집행 권한.</summary>
        public SlotPermission PermissionOf(StrategySpec? spec)
        {
            if (spec is null) return SlotPermission.Disabled;
            var a     = Volatile.Read(ref _active);
            var d     = Volatile.Read(ref _draining);
            bool grace = Volatile.Read(ref _grace) != 0;

            if (a is not null && ReferenceEquals(spec, a))
                return grace ? SlotPermission.OpenOnly : SlotPermission.Full;
            if (grace && d is not null && ReferenceEquals(spec, d))
                return SlotPermission.CloseOnly;
            return SlotPermission.Disabled;
        }

        /// <summary>이 전략이 신규 진입(Open) 신호를 낼 권한이 있는가.</summary>
        public bool CanEnter(StrategySpec? spec)
            => PermissionOf(spec) is SlotPermission.OpenOnly or SlotPermission.Full;

        /// <summary>이 전략이 청산(Exit) 신호를 낼 권한이 있는가.</summary>
        public bool CanExit(StrategySpec? spec)
            => PermissionOf(spec) is SlotPermission.OpenOnly or SlotPermission.Full or SlotPermission.CloseOnly;
    }
}
