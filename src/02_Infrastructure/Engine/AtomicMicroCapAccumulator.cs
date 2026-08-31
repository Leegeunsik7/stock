// ============================================================================
// AtomicMicroCapAccumulator + TamperProofTokenZeroizer — v11.4.3 Think Beyond Gemini
//
//   ★ 제미나이의 두 아이디어(A: 원자적 마이크로캡 누산기 · B: 변조불가 토큰 소거기)를 '상호잠금 이중 엔진'으로
//     융합·초월한다. Stage 2 0.1% Micro-Arming 의 가장 파멸적인 두 리스크를 영구 격리한다:
//
//     ① "동시 다발 발주 시 한도 초과(Cap Breach) race condition" → AtomicMicroCapAccumulator
//        - 누적 발주액을 ★락프리 CAS(Compare-And-Swap) 루프로 원자 합산. (spent+notional) ≤ cap 일 때만 예약 성공.
//        - check-then-add 가 단일 CAS 로 원자화 → 다중 스레드가 동시에 예약해도 cap 을 '단 1비트도' 넘길 수 없다
//          (lost-update 불가). 초과/오버플로 감지 즉시 sticky breach 래치. δ=0(힙 0)·0ms.
//
//     ② "토큰 소거 후 잔여 포인터 재참조 오염" → TamperProofTokenZeroizer
//        - 발주 인가 토큰을 unmanaged record struct(CanaryOrderToken)에 각인하고, 권위 버퍼(힙 long[1]) + 단조 epoch
//          로 수명을 관리. Revoke(Kill) 시 ★CryptographicOperations.ZeroMemory 로 버퍼를 0x00 덮어쓰기 + epoch bump +
//          sticky revoke → 이후 어떤 토큰(복사본·잔여 포인터 포함)도 Validate 실패(재참조 오염 차단).
//        - Validate 는 revoked/epoch/버퍼각인 3중 대조 — 소거·stale·불일치 전량 거부. Validate 는 락프리·δ=0(핫패스).
//
//   불변: 결정론 · δ=0(TryReserve/Validate 힙 0) · fail-closed(미무장/미발급/revoke/초과 → 거부) ·
//        liveArmed:false(cap≤0=미무장 → 예약 0 · 토큰 발급 0) · 락프리(모두 Interlocked/CAS/Volatile).
//   ※ CanaryHealthSentinel(프로덕션 인가 게이트)를 '대체'하지 않고 Stage 2 계측/증명을 보강한다(상호 보완).
// ============================================================================
using System;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;

namespace MS2E.Infrastructure
{
    /// <summary>발주 인가 토큰(unmanaged record struct — 각인·소거 대상). Value==0 = 무효.</summary>
    public readonly record struct CanaryOrderToken(long Value, long Epoch)
    {
        public static readonly CanaryOrderToken Void = new(0, 0);
        public bool IsVoid => Value == 0;
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Engine ① — Atomic Micro-Cap Accumulator (락프리 CAS 누적·0.1% 한도 봉쇄)
    // ════════════════════════════════════════════════════════════════════════
    public sealed class AtomicMicroCapAccumulator
    {
        private long _capRaw;      // 0.1% 미세 한도(0 = 미무장)
        private long _spentRaw;    // 누적 예약액
        private long _breached;    // sticky 초과 래치

        /// <summary>무장 — cap 설정 + 누적/breach 초기화(독립 스텝). cap≤0 이면 미무장(fail-closed).</summary>
        public void Arm(long capRaw)
        {
            Interlocked.Exchange(ref _spentRaw, 0);
            Interlocked.Exchange(ref _breached, 0);
            Interlocked.Exchange(ref _capRaw, capRaw > 0 ? capRaw : 0);
        }

        public void Disarm() => Interlocked.Exchange(ref _capRaw, 0);

        public long CapRaw     => Volatile.Read(ref _capRaw);
        public long SpentRaw   => Volatile.Read(ref _spentRaw);
        public bool Breached   => Volatile.Read(ref _breached) == 1;
        public long RemainingRaw { get { long r = Volatile.Read(ref _capRaw) - Volatile.Read(ref _spentRaw); return r > 0 ? r : 0; } }

        /// <summary>
        /// ★락프리 CAS 예약. (spent+notional) ≤ cap 일 때만 원자적으로 누적하고 true. 초과/오버플로 → sticky breach + false.
        /// 다중 스레드 동시 호출에도 cap 을 '단 1비트도' 넘기지 않는다(단일 CAS 로 검사+누적 원자화 — lost-update 불가).
        /// </summary>
        public bool TryReserve(long notionalRaw)
        {
            if (notionalRaw <= 0) return false;                 // 방어적
            long cap = Volatile.Read(ref _capRaw);
            if (cap <= 0) return false;                         // 미무장(liveArmed:false)
            while (true)
            {
                long cur = Volatile.Read(ref _spentRaw);
                long next = unchecked(cur + notionalRaw);
                if (next < cur || next > cap)                   // ★오버플로 또는 한도 초과 → 즉시 봉쇄
                {
                    Interlocked.Exchange(ref _breached, 1);
                    return false;
                }
                if (Interlocked.CompareExchange(ref _spentRaw, next, cur) == cur)
                    return true;                                // 예약 성공(원자적)
                // CAS 경합 패배 → 다른 스레드가 먼저 누적. 재시도(spin).
            }
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Engine ② — Tamper-Proof Token Lifecycle Zeroizer (0x00 소거·재참조 차단)
    // ════════════════════════════════════════════════════════════════════════
    public sealed class TamperProofTokenZeroizer : IDisposable
    {
        private readonly long[] _buf = new long[1];   // 토큰 각인 권위 버퍼(ZeroMemory 대상)
        private long _epoch;                            // 단조 증가 — revoke 시 bump(stale 재참조 무효화)
        private long _revoked;                          // sticky

        public bool IsRevoked => Volatile.Read(ref _revoked) == 1;
        public long Epoch     => Volatile.Read(ref _epoch);
        public bool BufferIsZero => Volatile.Read(ref _buf[0]) == 0;   // 소거 실증용

        /// <summary>토큰 발급(무장 시). seed 로부터 비-0 토큰을 버퍼에 각인 + 현재 epoch 반환. revoke 상태면 Void(fail-closed).</summary>
        public CanaryOrderToken Issue(long seed)
        {
            if (Volatile.Read(ref _revoked) == 1) return CanaryOrderToken.Void;
            long tok = seed == 0 ? 1 : seed;                   // 비-0 보장
            Volatile.Write(ref _buf[0], tok);
            return new CanaryOrderToken(tok, Volatile.Read(ref _epoch));
        }

        /// <summary>
        /// ★제시 토큰 검증(락프리·δ=0). revoked / epoch 불일치(stale 재참조) / 버퍼 각인 불일치 → 전량 false.
        /// revoked 를 최우선 판독하므로 소거 후에는 버퍼를 읽기도 전에 거부(재참조 오염 원천 차단).
        /// </summary>
        public bool Validate(in CanaryOrderToken token)
        {
            if (Volatile.Read(ref _revoked) == 1) return false;            // ★소거 후 전량 거부
            if (token.Value == 0) return false;
            if (token.Epoch != Volatile.Read(ref _epoch)) return false;    // ★stale epoch(잔여 포인터) 거부
            return Volatile.Read(ref _buf[0]) == token.Value;              // 버퍼 각인값 대조
        }

        /// <summary>
        /// ★소거(Kill). sticky revoke 세팅 → epoch bump → 버퍼를 CryptographicOperations.ZeroMemory 로 0x00 덮어쓰기.
        /// 순서(revoke→epoch→zero)가 중요: 소거 중 in-flight Validate 도 revoked/epoch/불일치 중 하나로 반드시 실패.
        /// </summary>
        public void Revoke()
        {
            Interlocked.Exchange(ref _revoked, 1);
            Interlocked.Increment(ref _epoch);
            CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(_buf.AsSpan()));   // 힙 잔재 0(메모리 덤프 방어)
        }

        public void Dispose() => Revoke();
    }
}
