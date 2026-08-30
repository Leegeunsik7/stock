// ============================================================================
// CanaryHealthSentinel — v11.4.0 Think Beyond Gemini
//   ★카나리(0.1% 소액) 실매매 운용의 가장 파멸적 리스크 "주문 폭발 반경(Blast Radius) 누출"을 0ms 원자 킬스위치로
//     영구 격리한다. 미인가 한도 초과·네트워크 이상(429/지연)·슬리피지 오차 감지 즉시 무장 해제 + 발주 토큰을
//     0x00 으로 소거(메모리 잔재 0) + sticky 락아웃(재무장 전까지 발주 0).
//
//   ── ★liveArmed:false 물리 봉인(절대 불변) ──────────────────────────────────
//     Arm 은 물리 이중 키(ValveWired ∧ LiveArmingWired)가 '둘 다' 배선됐을 때만 성립한다. 하나라도 결여 시 Arm 은
//     no-op(무장 실패) → TryAuthorize 는 항상 0(거부). 즉 물리 무장 열쇠 없이는 단 1비트의 발주도 인가되지 않는다.
//
//   ── 폭발 반경 상한(Circuit Breaker) ─────────────────────────────────────────
//     Arm(capRaw) 로 카나리 명목 한도(호출부가 총자산 0.1% 로 산정)를 봉인. TryAuthorize 는 누적 인가액이 cap 을
//     초과하면 '단 1회'라도 즉시 Kill(정직한 fail-closed — 초과분을 통과시키지 않는다). 락-프리 CAS 로 경합 하 누출 0.
//
//   불변: δ=0(TryAuthorize/Kill 핫패스 힙 0 — Interlocked/Volatile·고정 버퍼 ZeroMemory) · 결정론(단조 시퀀스) ·
//        fail-closed(미무장/초과/이상 → 0·Kill) · 스레드 안전(다중 발주 스레드 ↔ 워치독). ★발주/소켓 자체 접촉 0
//        (이 센티널은 '인가 게이트'일 뿐 — 실 주문 송출은 상위가 이 토큰 없이는 절대 진행 못 하도록 배선).
// ============================================================================
using System;
using System.Security.Cryptography;
using System.Threading;

namespace MS2E.Infrastructure
{
    /// <summary>카나리 킬 사유(진단·감사).</summary>
    public enum CanaryKillReason : byte
    {
        None = 0,
        BudgetBreach   = 1,   // 명목 한도 초과(미인가)
        NetworkAnomaly = 2,   // API 429/타임아웃/단선
        LatencyBreach  = 3,   // 틱 레이턴시 임계 초과
        SlippageBreach = 4,   // 슬리피지 오차 초과
        Manual         = 5,   // 운영자 수동 정지
    }

    public sealed class CanaryHealthSentinel
    {
        private const int TokenLen = 32;

        private int  _armed;                 // 0=disarmed, 1=armed
        private int  _killLatch;             // 0=clear, 1=killed(sticky·재무장 전까지 유지)
        private long _capRaw;                // 카나리 명목 한도(QFixed raw)
        private long _spentRaw;              // 누적 인가 명목액
        private long _authSeq;               // 인가 토큰 단조 시퀀스
        private long _authorized, _blocked, _kills;
        private int  _lastKillReason;        // CanaryKillReason
        private readonly byte[] _token = new byte[TokenLen];   // 발주 무장 토큰(Kill 시 0x00 소거)

        // ── 상태(관제) ───────────────────────────────────────────────────────
        public bool IsArmed  => Volatile.Read(ref _armed) == 1;
        public bool IsKilled => Volatile.Read(ref _killLatch) == 1;
        public long CapRaw       => Interlocked.Read(ref _capRaw);
        public long SpentRaw     => Interlocked.Read(ref _spentRaw);
        public long RemainingRaw => Math.Max(0, CapRaw - SpentRaw);
        public long Authorized   => Interlocked.Read(ref _authorized);
        public long Blocked      => Interlocked.Read(ref _blocked);
        public long Kills        => Interlocked.Read(ref _kills);
        public CanaryKillReason LastKillReason => (CanaryKillReason)Volatile.Read(ref _lastKillReason);

        // ── 무장(콜드 · 물리 이중 키 게이트) ──────────────────────────────────
        /// <summary>
        /// 카나리 무장. ★valveWired ∧ liveArmingWired 둘 다 true 일 때만 성립(물리 이중 키). 하나라도 결여 →
        /// 무장 실패(disarmed 유지) 후 false. capRaw ≤ 0 도 무장 거부. 성공 시 킬 래치 해제 + 한도/스펜트 초기화.
        /// </summary>
        public bool Arm(long capRaw, bool valveWired, bool liveArmingWired)
        {
            // ★liveArmed:false 봉인 — 물리 키 없이는 절대 무장 불가.
            if (!valveWired || !liveArmingWired) { Disarm(); return false; }
            if (capRaw <= 0) { Disarm(); return false; }

            Interlocked.Exchange(ref _capRaw, capRaw);
            Interlocked.Exchange(ref _spentRaw, 0);
            Volatile.Write(ref _lastKillReason, (int)CanaryKillReason.None);
            RandomNumberGenerator.Fill(_token);      // 무장 토큰 시딩(비영)
            Volatile.Write(ref _killLatch, 0);
            Volatile.Write(ref _armed, 1);
            return true;
        }

        /// <summary>무장 해제(킬 아님 — 정상 종료). 토큰 소거.</summary>
        public void Disarm()
        {
            Volatile.Write(ref _armed, 0);
            CryptographicOperations.ZeroMemory(_token);
        }

        // ── 인가(핫패스 · δ=0 · 락-프리) ──────────────────────────────────────
        /// <summary>
        /// 카나리 주문 명목액 인가. 미무장/이상 → 0(거부·fail-closed). 누적+명목 ≤ cap 이면 인가(단조 토큰>0 반환),
        /// 초과면 '즉시 Kill' 후 0. 락-프리 CAS 로 경합 하에서도 cap 누출 0. 힙 0.
        /// </summary>
        public long TryAuthorize(long notionalRaw)
        {
            if (Volatile.Read(ref _armed) != 1) { Interlocked.Increment(ref _blocked); return 0; }
            if (notionalRaw <= 0) { Interlocked.Increment(ref _blocked); return 0; }

            long cap = Interlocked.Read(ref _capRaw);
            var sw = new SpinWait();
            while (true)
            {
                long cur = Interlocked.Read(ref _spentRaw);
                long next = cur + notionalRaw;
                if (next < cur || next > cap)   // 오버플로 또는 한도 초과 → 폭발 반경 차단
                {
                    Kill(CanaryKillReason.BudgetBreach);
                    Interlocked.Increment(ref _blocked);
                    return 0;
                }
                if (Interlocked.CompareExchange(ref _spentRaw, next, cur) == cur)
                {
                    Interlocked.Increment(ref _authorized);
                    return Interlocked.Increment(ref _authSeq);   // 비영 단조 인가 토큰
                }
                sw.SpinOnce();   // 경합 재시도
            }
        }

        // ── 워치독(50ms 내 자동 무장 해제 트리거) ─────────────────────────────
        /// <summary>네트워크 429/타임아웃, 레이턴시·슬리피지 임계 초과 등 이상 감지 시 즉시 Kill(0ms 원자).</summary>
        public void NoteAnomaly(CanaryKillReason reason) => Kill(reason == CanaryKillReason.None ? CanaryKillReason.NetworkAnomaly : reason);

        /// <summary>0ms 원자 킬스위치 — 무장 해제 + 발주 토큰 0x00 소거 + sticky 락아웃(재무장 전까지 발주 0).</summary>
        public void Kill(CanaryKillReason reason = CanaryKillReason.Manual)
        {
            Volatile.Write(ref _armed, 0);                       // 즉시 무장 해제
            CryptographicOperations.ZeroMemory(_token);          // ★발주 토큰 0x00 소거(메모리 잔재 0)
            if (Interlocked.Exchange(ref _killLatch, 1) == 0)    // sticky 1회 계상
            {
                Volatile.Write(ref _lastKillReason, (int)reason);
                Interlocked.Increment(ref _kills);
            }
        }

        /// <summary>재초기화(테스트/세션 리셋) — 전 상태·지표·토큰 소거.</summary>
        public void Reset()
        {
            Volatile.Write(ref _armed, 0);
            Volatile.Write(ref _killLatch, 0);
            Interlocked.Exchange(ref _capRaw, 0);
            Interlocked.Exchange(ref _spentRaw, 0);
            Interlocked.Exchange(ref _authSeq, 0);
            Interlocked.Exchange(ref _authorized, 0);
            Interlocked.Exchange(ref _blocked, 0);
            Interlocked.Exchange(ref _kills, 0);
            Volatile.Write(ref _lastKillReason, (int)CanaryKillReason.None);
            CryptographicOperations.ZeroMemory(_token);
        }
    }
}
