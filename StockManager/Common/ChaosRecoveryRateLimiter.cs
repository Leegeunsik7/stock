// ============================================================================
// ChaosRecoveryRateLimiter — v9.1.0 단계별 복구 스로틀러 (Gemini 보강 과제)
//
//  카오스 상태가 해제된 직후, 억눌렸던 주문·재무장 요청이 한꺼번에 몰려드는 2차 충격(Thundering Herd)을 막는다.
//  카오스 해제 → 즉시 재무장 금지. 설정된 쿨다운 기간 동안 '단계별 재인증(staged re-authentication)'을 시간 간격을
//  두고 통과해야만 무장 가동을 재개한다. 너무 이른 재인증(Herd 쇄도)은 거부(Reject)로 계수·차단한다.
//
//  상태: Frozen(카오스 활성) → Cooldown(해제·단계 재인증 진행) → Ready(완전 복구·무장 재개 허용).
//
//  ★불변: δ=0(전 메서드 힙 0·Interlocked/Volatile) · 결정론(주입 ticks·벽시계 무참조·tickFrequency 미지정 시
//    고정 1e7[QPC 관례·호스트 무관·리플레이 재현]) · liveArmed:false(RecoveryBlocking tighten-only·소켓 미접촉).
// ============================================================================
using System;
using System.Threading;

namespace StockManager.Common
{
    /// <summary>단계별 복구 스로틀러(δ=0). EnterChaos→BeginRecovery→AttemptReauth×N(간격 강제)→Ready. Herd 쇄도 거부.</summary>
    public sealed class ChaosRecoveryRateLimiter
    {
        public const long S_READY    = 0;   // 완전 복구 — 무장 재개 허용
        public const long S_COOLDOWN = 1;   // 쿨다운·단계별 재인증 진행
        public const long S_FROZEN   = 2;   // 카오스 활성(복구 미개시)

        private readonly long _cooldownTicks;    // 총 쿨다운 기간(틱)
        private readonly int  _requiredStages;   // 통과해야 할 재인증 단계 수
        private long _state = S_READY;
        private long _recoveryStartTicks;
        private long _stagesPassed;
        private long _rejects, _completions, _chaosEntries;

        public ChaosRecoveryRateLimiter(int requiredStages = 3, long cooldownMillis = 3000, long tickFrequency = 0)
        {
            long freq = tickFrequency > 0 ? tickFrequency : 10_000_000L;   // 고정 기준(호스트 무관·리플레이 재현)
            _cooldownTicks   = Math.Max(1L, cooldownMillis) * freq / 1000L;
            _requiredStages  = Math.Max(1, requiredStages);
        }

        // ── 읽기(δ=0·락프리) ──
        public long State        => Interlocked.Read(ref _state);
        public long StagesPassed => Interlocked.Read(ref _stagesPassed);
        public long Rejects      => Interlocked.Read(ref _rejects);
        public long Completions  => Interlocked.Read(ref _completions);
        public long ChaosEntries => Interlocked.Read(ref _chaosEntries);
        public long CooldownTicks => _cooldownTicks;
        public int  RequiredStages => _requiredStages;
        /// <summary>★blockWhile — 복구 미완(Frozen/Cooldown)이면 무장 차단(Thundering Herd 방지·tighten-only).</summary>
        public bool RecoveryBlocking => Interlocked.Read(ref _state) != S_READY;

        /// <summary>카오스 활성 진입(δ=0) — 복구 동결(무장 차단). 카오스 결함 주입 시 호출.</summary>
        public void EnterChaos()
        {
            Volatile.Write(ref _stagesPassed, 0L);
            if (Interlocked.Exchange(ref _state, S_FROZEN) != S_FROZEN)
                Interlocked.Increment(ref _chaosEntries);
        }

        /// <summary>카오스 해제 → 쿨다운 개시(δ=0). Frozen 에서만 전이. 즉시 재무장 금지 — 단계별 재인증 요구.</summary>
        public void BeginRecovery(long ticks)
        {
            // ★publish-before-init 방어: 의존 데이터(start·stages)를 먼저 기록한 뒤 상태(S_COOLDOWN)를 release 발행 —
            //   COOLDOWN 을 관측한 herd 독자는 반드시 올바른 start 도 관측(stale 0 baseline 으로 쿨다운 우회 봉쇄).
            if (Interlocked.Read(ref _state) != S_FROZEN) return;   // Frozen 아니면 무전이(live start 클로버 방지)
            Volatile.Write(ref _recoveryStartTicks, ticks);
            Volatile.Write(ref _stagesPassed, 0L);
            Interlocked.CompareExchange(ref _state, S_COOLDOWN, S_FROZEN);   // 그 다음 상태 발행
        }

        /// <summary>
        /// 단계별 재인증 시도(δ=0). 각 단계는 (쿨다운/단계수) 이상 시간 간격 후에만 통과 — 너무 이르면 거부(Herd 억제).
        /// 마지막 단계 통과 시 Ready 로 승격(무장 재개 허용). Cooldown 상태에서만 유효.
        /// </summary>
        public bool AttemptReauth(long ticks)
        {
            if (Interlocked.Read(ref _state) != S_COOLDOWN) return false;
            long start   = Volatile.Read(ref _recoveryStartTicks);
            long passed  = Interlocked.Read(ref _stagesPassed);
            long stageInterval = _cooldownTicks / _requiredStages;
            long earliest = start + (passed + 1L) * stageInterval;   // 이 단계가 허용되는 최소 시각
            if (passed >= _requiredStages)                            // ★오버슛 가드: 최종단계 도달 후엔 전진 금지(state=READY 발행 창 레이스 방어)
            {
                Interlocked.Increment(ref _rejects);
                return false;
            }
            if (ticks < earliest)                                     // 너무 이른 재인증 = Herd 쇄도 → 거부
            {
                Interlocked.Increment(ref _rejects);
                return false;
            }
            // ★herd TOCTOU 방어: 게이트가 사용한 정확한 passed 에서만 원자 전진(CAS). 동시 herd 는 CAS 패배로 거부 —
            //   재시도 시 높아진 passed 의 earliest=(passed+1)*stageInterval 가 다음 인터벌 전까지 시간 게이트를 재차단.
            //   ∴ 한 인터벌당 정확히 1단계만 전진(N개 동시 쇄도에도 조기 재무장 물리 봉쇄).
            if (Interlocked.CompareExchange(ref _stagesPassed, passed + 1L, passed) != passed)
            {
                Interlocked.Increment(ref _rejects);
                return false;
            }
            long now = passed + 1L;
            if (now >= _requiredStages)
            {
                // ★단발 완료: COOLDOWN→READY 상태 CAS 를 이긴 스레드만 Completions 증가 — 한 복구당 정확히 1회(중복 봉쇄).
                if (Interlocked.CompareExchange(ref _state, S_READY, S_COOLDOWN) == S_COOLDOWN)
                    Interlocked.Increment(ref _completions);
            }
            return true;
        }

        /// <summary>강제 리셋(테스트·세션 전환) — Ready 로 환원(δ=0).</summary>
        public void Reset()
        {
            Volatile.Write(ref _stagesPassed, 0L);
            Interlocked.Exchange(ref _state, S_READY);
        }
    }
}
