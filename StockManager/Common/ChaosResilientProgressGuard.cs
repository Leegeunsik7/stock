// ============================================================================
// ChaosResilientProgressGuard — v9.1.0 ★Think Beyond Gemini: 카오스 내성 진행 보장 가드
//   "락프리 상태 전이는 카오스 하에서도 '유한 스텝 내 원자적 완료' 또는 'fail-closed 무효화' — 부분·무한 금지."
//
//  ── 왜 제미나이의 A/B 를 초월하는가 ────────────────────────────────────────
//  극단적 결함 주입 시 락프리 핫패스의 두 파멸적 리스크:
//    · 라이브락(Chaos-Induced Spin-Wait Livelock) — CAS/스핀 loop 가 영원히 재시도(진행 0).
//    · 찢어진 상태(Torn State under Partial Fault) — 다필드 쓰기가 결함으로 중단 → 부분 상태 가시화.
//  제미나이는 이를 각각 막자 제안했다(A: 스핀 경계 센티널, B: 이중버퍼 체크포인트). 그러나 이 둘은 사실
//  '하나의 불변식'의 두 얼굴이다: **bounded progress** — "진행은 유한 스텝 내에 원자적으로 완료되거나, 그 자리에서
//  fail-closed 로 무효화된다." 이 가드는 단일 메커니즘으로 둘을 동시에 봉쇄한다:
//    ① ChaosSpinFence(값타입·δ=0) — 모든 락프리 재시도에 하드 상한. 초과 시 O(1) 탈출·fail-closed(라이브락 격리).
//    ② 원자 이중버퍼 에폭 체크포인트 — 쓰기는 '비활성 그림자' 슬롯에서만 일어나고, 오직 Interlocked.Exchange 인덱스
//       스왑이 새 에폭을 원자적으로 발행. ∴ 부분 쓰기는 구조적으로 절대 가시화되지 않는다(torn 을 '탐지'가 아니라 '불가능'化).
//  ★핵심 통합: 이중버퍼 독자의 seqlock 재시도조차 '같은 스핀 펜스'로 상한을 받는다 — 즉 bounded-progress 펜스가
//    라이브락과 torn-read 재시도를 '한 메커니즘'으로 동시에 봉쇄한다. A(라이브락)와 B(torn)를 각각이 아니라 하나로 초월.
//
//  ★불변: δ=0(펜스=값타입·TryCommit/TryRead 힙 0·Interlocked/Volatile·이중버퍼 사전할당) · 결정론(순수 CAS·
//    벽시계 무참조) · liveArmed:false(SafeHold tighten-only·소켓 미접촉) · fail-closed(상한 초과/torn → false·무장 하향).
//    ★단일 라이터 커밋(SPSC 계약·코어 발행 규율) · 다중 독자 안전.
// ============================================================================
using System;
using System.Threading;

namespace StockManager.Common
{
    /// <summary>카오스 스핀 펜스(δ=0 값타입). 락프리 재시도에 하드 상한 — 초과 시 Retry()=false 로 O(1) fail-closed 탈출.</summary>
    public struct ChaosSpinFence
    {
        private int _spins;
        private readonly int _ceiling;
        public ChaosSpinFence(int ceiling) { _spins = 0; _ceiling = ceiling <= 0 ? 1 : ceiling; }
        public int Spins => _spins;
        public int Ceiling => _ceiling;
        /// <summary>재시도 허용 여부(δ=0). 정확히 _ceiling 회만 true, 그 후 false — 라이브락 O(1) 탈출점.</summary>
        public bool Retry()
        {
            if (_spins >= _ceiling) return false;   // 상한 도달 → 즉시 fail-closed 탈출(_spins 는 _ceiling 에 고정)
            _spins++;
            return true;
        }
    }

    /// <summary>블리터블 체크포인트 상태(이중버퍼 원자 발행 단위·δ=0).</summary>
    public struct ChaosCheckpoint
    {
        public long Epoch;
        public long PayloadA;
        public long PayloadB;
    }

    /// <summary>카오스 내성 진행 보장 가드(δ=0·bounded-progress). 라이브락+torn-state 를 단일 불변식으로 봉쇄·fail-closed.</summary>
    public sealed class ChaosResilientProgressGuard
    {
        private readonly int _spinCeiling;
        private readonly ChaosCheckpoint[] _slots = new ChaosCheckpoint[2];  // 이중버퍼(사전할당·δ=0 핫패스)
        private readonly long[] _vers = new long[2];                          // 슬롯별 seqlock(홀=쓰는 중·짝=안정)
        private int  _active;               // 활성(독자 대상) 슬롯 인덱스
        private long _livelockEscapes, _tornStateBlocks, _commits, _holdActive;

        public ChaosResilientProgressGuard(int spinCeiling = 1024)
        {
            _spinCeiling = spinCeiling <= 0 ? 1024 : spinCeiling;
        }

        // ── 읽기(δ=0·락프리) ──
        public int  SpinCeiling     => _spinCeiling;
        public long LivelockEscapes => Interlocked.Read(ref _livelockEscapes);
        public long TornStateBlocks => Interlocked.Read(ref _tornStateBlocks);
        public long Commits         => Interlocked.Read(ref _commits);
        /// <summary>★blockWhile — 라이브락/torn 감지 시 무장 하향(가역 홀드·Reset 로 해제). 카운터는 누적 유지.</summary>
        public bool SafeHoldActive  => Interlocked.Read(ref _holdActive) == 1L;

        /// <summary>새 스핀 펜스(δ=0). 락프리 loop 에서 while(fence.Retry()) 로 상한 봉쇄 — 초과 시 OnLivelockEscape.</summary>
        public ChaosSpinFence NewFence() => new ChaosSpinFence(_spinCeiling);

        /// <summary>라이브락 탈출 신호(δ=0). 스핀 상한 초과 시 호출 — 계수 + 가역 홀드(fail-closed).</summary>
        public void OnLivelockEscape()
        {
            Interlocked.Increment(ref _livelockEscapes);
            Interlocked.Exchange(ref _holdActive, 1L);
        }

        /// <summary>
        /// 원자 이중버퍼 커밋(δ=0·단일 라이터). 활성 슬롯의 일관 스냅샷을 읽어 delta 적용분을 '비활성 그림자' 슬롯에
        /// 기록한 뒤, Interlocked.Exchange 인덱스 스왑으로 새 에폭을 원자 발행. 부분 쓰기는 비활성이라 구조적으로 불가시.
        /// 스왑 경합이 스핀 상한을 넘으면(라이브락) O(1) 탈출·fail-closed(부분 커밋 없음).
        /// </summary>
        public bool TryCommit(long epochDelta, long payloadA, long payloadB)
        {
            var fence = NewFence();
            while (true)
            {
                int act = Volatile.Read(ref _active);
                if (!TryReadSlot(act, out ChaosCheckpoint cur))   // 활성 일관 스냅샷(torn 상한 초과 → fail-closed)
                    return false;
                int shadow = act ^ 1;
                // 그림자(비활성) 슬롯에 새 상태 기록(seqlock 홀→짝). 독자는 활성만 보므로 이 쓰기는 불가시.
                Interlocked.Increment(ref _vers[shadow]);          // 홀 — 쓰는 중
                _slots[shadow].Epoch    = cur.Epoch + epochDelta;
                _slots[shadow].PayloadA = payloadA;
                _slots[shadow].PayloadB = payloadB;
                Interlocked.Increment(ref _vers[shadow]);          // 짝 — 완료(일관)
                // 원자 발행: active 를 shadow 로 스왑. 성공 시 새 에폭이 원자적으로 가시화(부분 상태 절대 노출 없음).
                if (Interlocked.CompareExchange(ref _active, shadow, act) == act)
                {
                    Interlocked.Increment(ref _commits);
                    return true;
                }
                if (!fence.Retry())                                // 스왑 경합 지속 = 라이브락 → O(1) 탈출·fail-closed
                {
                    OnLivelockEscape();
                    return false;
                }
            }
        }

        /// <summary>활성 체크포인트 일관 독출(δ=0·seqlock·펜스 상한). torn 이 상한 초과면 false(fail-closed·계수).</summary>
        public bool TryRead(out ChaosCheckpoint state) => TryReadSlot(Volatile.Read(ref _active), out state);

        private bool TryReadSlot(int slot, out ChaosCheckpoint state)
        {
            var fence = NewFence();
            while (true)
            {
                long v1 = Interlocked.Read(ref _vers[slot]);
                if ((v1 & 1L) == 0L)                    // 짝수 = 안정(쓰는 중 아님)
                {
                    ChaosCheckpoint snap = _slots[slot]; // 3필드 값 복사(스택·δ=0)
                    Interlocked.MemoryBarrier();
                    long v2 = Interlocked.Read(ref _vers[slot]);
                    if (v1 == v2) { state = snap; return true; }   // 일관 스냅샷(쓰기 미개입)
                }
                if (!fence.Retry())                     // torn 경합이 상한 초과 → fail-closed
                {
                    Interlocked.Increment(ref _tornStateBlocks);
                    Interlocked.Exchange(ref _holdActive, 1L);
                    state = default;
                    return false;
                }
            }
        }

        /// <summary>가역 홀드 해제(복구 스로틀러 Ready 시·세션 전환). 누적 카운터는 유지(δ=0).</summary>
        public void Reset() => Interlocked.Exchange(ref _holdActive, 0L);
    }
}
