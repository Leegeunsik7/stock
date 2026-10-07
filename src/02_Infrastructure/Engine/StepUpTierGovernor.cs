// ============================================================================
// StepUpTierGovernor + InstantRollbackPurger — v11.4.5 Think Beyond Gemini
//
//   ★ 제미나이의 두 아이디어(A: 락프리 Tier 승격 거버너 · B: 즉시 롤백 메모리 퍼저)를 '상호잠금 이중 엔진'으로
//     융합·초월한다. Stage 4 전액 승격(0.1%→1%→5%→25%→100%)의 두 파멸적 리스크를 영구 격리한다:
//
//     ① "한도 승격(Tier Step-Up) 시의 경쟁 상태(Race Condition)" → StepUpTierGovernor
//        - 단계 상향을 ★단일 CAS(CompareExchange(_tier, toTier, fromTier))로 원자 처리.
//        - 다중 스레드가 동시에 같은 단계에서 승격을 시도해도 CAS 는 하나만 성공 → 중복 승격/한도 오버슈트 불가.
//        - 물리 이중 키(ValveWired ∧ LiveArmingWired) 없이는 Arm 실패 → 어떤 단계도 진입 못 함(liveArmed:false).
//
//     ② "부분 롤백 시 잔여 권한 메모리 오염" → InstantRollbackPurger
//        - 롤백 격발 시 ★O(1) 원자 CAS 로 전역 한도를 0.1% 안전지대(또는 0% Disarm)로 즉시 복귀 + 승격 권한 토큰
//          버퍼를 CryptographicOperations.ZeroMemory 로 0x00 소거 + epoch bump. 이후 잔여 권한 참조는 전량 무효.
//        - ValidAuthority 는 purged/토큰일치/비-0 3중 대조 — 소거 후 어떤 잔여 포인터·복사본도 통과 못 함.
//
//   불변: 결정론 · δ=0(TryStepUp/ValidAuthority 힙 0) · fail-closed(미무장/오단계 → 거부) · liveArmed:false(물리
//        이중 키 없이 승격 0) · 락프리(Interlocked/CAS/Volatile). ※ CanaryHealthSentinel 위에 '단계적 승격' 계층 보강.
// ============================================================================
using System;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;

namespace MS2E.Infrastructure
{
    /// <summary>단계적 전개 티어 — 0.1% → 1% → 5% → 25% → 100%.</summary>
    public enum RolloutTier { Micro01 = 0, One = 1, Five = 2, TwentyFive = 3, Full = 4 }

    // ════════════════════════════════════════════════════════════════════════
    //  Engine ② — Instant Rollback Memory Purger
    // ════════════════════════════════════════════════════════════════════════
    public sealed class InstantRollbackPurger
    {
        private readonly long[] _authority = new long[8];   // 승격 권한 토큰 버퍼(소거 대상)
        private long _purged;

        /// <summary>승격 권한 토큰 부여(무장/승격 시). 비-0 이어야 유효.</summary>
        public void Grant(int slot, long token)
        {
            if ((uint)slot < (uint)_authority.Length) Volatile.Write(ref _authority[slot], token);
            Interlocked.Exchange(ref _purged, 0);
        }

        /// <summary>★롤백 격발 — 권한 버퍼 전량 0x00 소거 + sticky purged. 잔여 참조/복사본 무효화.</summary>
        public void Purge()
        {
            Interlocked.Exchange(ref _purged, 1);
            CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(_authority.AsSpan()));
        }

        public bool IsPurged => Volatile.Read(ref _purged) == 1;

        public bool BufferIsZero
        {
            get { for (int i = 0; i < _authority.Length; i++) if (Volatile.Read(ref _authority[i]) != 0) return false; return true; }
        }

        /// <summary>권한 검증(락프리·δ=0). purged / 토큰 불일치 / 0 → 전량 false(잔여 오염 차단).</summary>
        public bool ValidAuthority(int slot, long token)
            => Volatile.Read(ref _purged) == 0 && token != 0 && (uint)slot < (uint)_authority.Length
               && Volatile.Read(ref _authority[slot]) == token;
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Engine ① — Step-Up Tier Governor (락프리 CAS 단계 승격)
    // ════════════════════════════════════════════════════════════════════════
    public sealed class StepUpTierGovernor
    {
        // 단계별 한도 비율(백만분율): 0.1%=1000 · 1%=10000 · 5%=50000 · 25%=250000 · 100%=1000000.
        private static readonly long[] TierPpm = { 1_000, 10_000, 50_000, 250_000, 1_000_000 };
        public const int MaxTier = 4;   // Full

        private long _tier = -1;         // -1 = 미무장(liveArmed:false), 0..4 = 활성 단계
        private long _totalAssetsRaw;
        private long _epoch;
        private long _armedBothKeys;     // 물리 이중 키 게이트
        private readonly InstantRollbackPurger _purger;

        public StepUpTierGovernor(InstantRollbackPurger purger) => _purger = purger;

        /// <summary>★물리 이중 키 무장 — 둘 다 true 여야 성립(liveArmed:false). 성공 시 0.1% 마이크로 단계로 시작.</summary>
        public bool Arm(long totalAssetsRaw, bool valveWired, bool liveArmingWired)
        {
            if (!valveWired || !liveArmingWired || totalAssetsRaw <= 0) { Rollback(); return false; }
            Interlocked.Exchange(ref _totalAssetsRaw, totalAssetsRaw);
            Interlocked.Exchange(ref _armedBothKeys, 1);
            Interlocked.Exchange(ref _tier, 0);
            Interlocked.Increment(ref _epoch);
            _purger.Grant(0, _epoch);   // 승격 권한 토큰 부여
            return true;
        }

        private long ComputeCap(int tier)
            => (long)((decimal)Volatile.Read(ref _totalAssetsRaw) * TierPpm[tier] / 1_000_000m);

        /// <summary>
        /// ★락프리 CAS 단계 승격. 현재 단계가 정확히 fromTier 일 때만 fromTier→fromTier+1 로 원자 전이(성공 true).
        /// 다중 스레드 동시 호출에도 하나만 성공 → 중복 승격/오버슈트 불가. Full 도달/미무장 시 false.
        /// </summary>
        public bool TryStepUp(int fromTier)
        {
            if (Volatile.Read(ref _armedBothKeys) != 1) return false;   // 미무장(fail-closed)
            if (fromTier < 0 || fromTier >= MaxTier) return false;      // 이미 Full 또는 무효
            int toTier = fromTier + 1;
            if (Interlocked.CompareExchange(ref _tier, toTier, fromTier) == fromTier)
            {
                long ep = Interlocked.Increment(ref _epoch);
                _purger.Grant(0, ep);   // 새 단계 권한 토큰(이전 토큰 무효화)
                return true;
            }
            return false;   // CAS 패배 — 다른 스레드가 먼저 승격(중복 방지)
        }

        public int CurrentTier => (int)Volatile.Read(ref _tier);
        /// <summary>
        /// 현재 단계 한도 — ★단계에서 직접 산출(별도 필드 없음). 수정 전엔 단계 CAS 뒤 한도를 따로 썼다: CAS 후 선점된 승격 스레드가
        /// 늦게 깨어 이전 단계 한도로 덮거나(Full 인데 50% · 전역 스위트 간헐 실패로 실측), Rollback(안전지대/완전 Disarm)과 겹치면
        /// '단계 0/−1 인데 높은 한도'가 남을 수 있었다(fail-open). 단계 → 한도가 순수 함수면 경합 순서와 무관하게 항상 정합.
        /// </summary>
        public long CapRaw { get { long t = Volatile.Read(ref _tier); return t < 0 ? 0 : ComputeCap((int)t); } }
        public long Epoch => Volatile.Read(ref _epoch);
        public bool IsArmed => Volatile.Read(ref _tier) >= 0 && Volatile.Read(ref _armedBothKeys) == 1;

        /// <summary>
        /// ★0ms 원자 롤백. toSafeZone: 0.1% 안전지대 복귀(무장 유지) · 아니면 0% 완전 Disarm(무장 해제).
        /// 두 경우 모두 승격 권한 토큰을 즉시 0x00 소거(잔여 권한 오염 차단) + epoch bump.
        /// </summary>
        public void Rollback(bool toSafeZone = false)
        {
            if (toSafeZone && Volatile.Read(ref _armedBothKeys) == 1)
            {
                Interlocked.Exchange(ref _tier, 0);                     // 0.1% 소액 안전지대(한도 = 단계 산출)
            }
            else
            {
                Interlocked.Exchange(ref _tier, -1);                    // 완전 Disarm(한도 = 0)
                Interlocked.Exchange(ref _armedBothKeys, 0);
            }
            Interlocked.Increment(ref _epoch);
            _purger.Purge();   // ★승격 권한 토큰 0x00 소거
        }
    }
}
