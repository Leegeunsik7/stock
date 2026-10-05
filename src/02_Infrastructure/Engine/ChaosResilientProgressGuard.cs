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

namespace MS2E.Infrastructure
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
    public sealed class ChaosResilientProgressGuard : MS2E.Core.IArmingBlockSource
    {
        private readonly int _spinCeiling;
        private readonly ChaosCheckpoint[] _slots = new ChaosCheckpoint[2];  // 이중버퍼(사전할당·δ=0 핫패스)
        private readonly long[] _vers = new long[2];                          // 슬롯별 seqlock(홀=쓰는 중·짝=안정)
        private int  _active;               // 활성(독자 대상) 슬롯 인덱스
        private long _livelockEscapes, _tornStateBlocks, _commits, _holdActive, _readRetries, _staleIndexRedirects;

        public ChaosResilientProgressGuard(int spinCeiling = 1024)
        {
            _spinCeiling = spinCeiling <= 0 ? 1024 : spinCeiling;
        }

        // ── 읽기(δ=0·락프리) ──
        public int  SpinCeiling     => _spinCeiling;
        public long LivelockEscapes => Interlocked.Read(ref _livelockEscapes);
        public long TornStateBlocks => Interlocked.Read(ref _tornStateBlocks);
        public long Commits         => Interlocked.Read(ref _commits);
        /// <summary>seqlock 재시도 누계(쓰기 개입·발행 인덱스 이동) — 관측 전용. 재시도는 정상 경합이며 홀드 사유가 아니다.</summary>
        public long ReadRetries     => Interlocked.Read(ref _readRetries);
        /// <summary>[방향 B] 독자가 쥔 인덱스가 발행 인덱스와 달라(stale) 현재 활성 슬롯으로 재추적한 누계.</summary>
        public long StaleIndexRedirects => Interlocked.Read(ref _staleIndexRedirects);
        /// <summary>★blockWhile — 라이브락/torn 감지 시 무장 하향(가역 홀드·Reset 로 해제). 카운터는 누적 유지.</summary>
        public bool SafeHoldActive  => Interlocked.Read(ref _holdActive) == 1L;
        bool MS2E.Core.IArmingBlockSource.ArmingBlocked => SafeHoldActive;

        // [실배선 · 방향 B] 원자 인터락 집계기 푸시 결속(ArmingInterlockBitmask) — 상태 전이 직후 Resync(δ=0).
        private MS2E.Core.ArmingInterlockLink? _interlockLink;
        /// <summary>집계기에 이 엔진을 고정 비트로 결속 — 결속 즉시 현재 상태 동기화(이미 차단이면 비트 유지).</summary>
        public void BindInterlock(MS2E.Core.ArmingInterlockBitmask aggregator, MS2E.Core.ArmingInterlockSource bit)
        {
            ArgumentNullException.ThrowIfNull(aggregator);
            var link = aggregator.Bind(bit, this);
            Volatile.Write(ref _interlockLink, link);
            link.Resync();   // Bind ~ 링크 가시화 사이의 전이 흡수
        }
        private void SignalInterlock() => Volatile.Read(ref _interlockLink)?.Resync();

        /// <summary>새 스핀 펜스(δ=0). 락프리 loop 에서 while(fence.Retry()) 로 상한 봉쇄 — 초과 시 OnLivelockEscape.</summary>
        public ChaosSpinFence NewFence() => new ChaosSpinFence(_spinCeiling);

        /// <summary>라이브락 탈출 신호(δ=0). 스핀 상한 초과 시 호출 — 계수 + 가역 홀드(fail-closed).</summary>
        public void OnLivelockEscape()
        {
            Interlocked.Increment(ref _livelockEscapes);
            Interlocked.Exchange(ref _holdActive, 1L);
            SignalInterlock();
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
                if (!TryReadSlot(ref act, out ChaosCheckpoint cur))   // 활성 일관 스냅샷(torn 상한 초과 → fail-closed) · act = 실제 판독한 발행 슬롯
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
        public bool TryRead(out ChaosCheckpoint state)
        {
            int slot = Volatile.Read(ref _active);
            return TryReadSlot(ref slot, out state);
        }

        /// <summary>★테스트 전용 seam — "독자가 _active 를 읽은 직후 선점돼 그 인덱스가 stale 해진" 상태를 결정론 재현(hint = 선점 전 읽은 인덱스).</summary>
        internal bool TryReadFromStaleHint(int hintSlot, out ChaosCheckpoint state)
        {
            int slot = hintSlot & 1;
            return TryReadSlot(ref slot, out state);
        }

        /// <summary>
        /// [방향 B · Memory-Barrier Lock-Free Seqlock Verifier] 발행 인덱스 검증형 seqlock 독출(δ=0·펜스 상한).
        ///
        ///  수정 전 결함(실측 · 결정론 seam 으로 Red 재현):
        ///   독자가 _active 를 읽은 뒤 선점되면 그 인덱스는 stale 해진다. 라이터가 그 사이 두 번 커밋하면 독자가 쥔 슬롯은
        ///   '다음 커밋의 그림자'가 된다. 이때 수정 전 독출은 그 슬롯 하나만 고집했다:
        ///    ① 그림자 기록이 끝났지만 CAS 발행 전이면 → 짝수 버전·v1==v2 로 통과해 **미발행 상태(E+1)** 를 반환하고,
        ///       다음 TryRead 는 실제 활성(E)을 돌려줘 에폭이 **역행**했다("에폭 단조" 계약 위반 · 선행 가시화).
        ///    ② 라이터가 그림자 기록 도중 선점되면(버전 홀수 고착) → 이미 발행된 안정 슬롯이 옆에 있는데도 같은 슬롯에서
        ///       스핀 상한을 소진 → TornStateBlocks++ · **SafeHoldActive 오발(무장 하향 false-positive)**.
        ///
        ///  교정 — 세 겹 판정(전부 무할당):
        ///   (a) v1 = Volatile.Read(acquire) → 이후 페이로드 읽기가 v1 앞으로 당겨지지 않음.
        ///   (b) 페이로드 3필드 복사 → Interlocked.MemoryBarrier(full) → v2 재독: 페이로드 load 가 v2 뒤로 밀리는 load-load
        ///       재배치(ARM64 등 약한 메모리 모델 · JIT 스케줄링)를 봉쇄. v1==v2 ∧ 짝수 = 복사 도중 그 슬롯 쓰기 미개입.
        ///   (c) ★발행 인덱스 재검증: 판독 후 _active 가 여전히 이 슬롯인가. 아니면(stale) 현재 활성 슬롯으로 재추적 · 재시도.
        ///       a==a' ∧ v1==v2 이면 선형화 가능 — a→a^1→a 왕복(ABA)은 두 번째 전이가 이 슬롯을 다시 써야 하므로 버전 변동으로 걸린다.
        ///       ∴ 반환 상태는 판독 구간 안 어느 순간의 '발행 상태'와 정확히 같다(미발행/찢김/역행 불가능).
        ///  재시도는 발행 인덱스를 따라가므로 그림자 기록 도중 선점된 라이터가 독자를 굶기지 않는다(②의 false-positive 홀드 제거).
        ///  스핀 상한 초과(진짜 라이브락급 경합)만 fail-closed 홀드.
        /// </summary>
        private bool TryReadSlot(ref int slot, out ChaosCheckpoint state)
        {
            var fence = NewFence();
            while (true)
            {
                long v1 = Volatile.Read(ref _vers[slot]);                 // (a) acquire
                if ((v1 & 1L) == 0L)                                       // 짝수 = 안정(쓰는 중 아님)
                {
                    ChaosCheckpoint snap = _slots[slot];                   // 3필드 값 복사(스택·δ=0)
                    Interlocked.MemoryBarrier();                           // (b) payload load → v2 load 순서 고정
                    long v2 = Volatile.Read(ref _vers[slot]);
                    if (v1 == v2)
                    {
                        int published = Volatile.Read(ref _active);        // (c) 발행 인덱스 재검증
                        if (published == slot) { state = snap; return true; }
                        Interlocked.Increment(ref _staleIndexRedirects);
                        slot = published;                                  // stale → 현재 활성으로 재추적
                        Interlocked.Increment(ref _readRetries);
                        if (!fence.Retry()) break;
                        continue;
                    }
                }
                Interlocked.Increment(ref _readRetries);
                if (!fence.Retry()) break;                                 // torn 경합이 상한 초과 → fail-closed
                slot = Volatile.Read(ref _active);                         // 쓰기 개입 → 발행 인덱스를 따라 재시도
            }
            Interlocked.Increment(ref _tornStateBlocks);
            Interlocked.Exchange(ref _holdActive, 1L);
            SignalInterlock();
            state = default;
            return false;
        }

        /// <summary>가역 홀드 해제(복구 스로틀러 Ready 시·세션 전환). 누적 카운터는 유지(δ=0).</summary>
        public void Reset() { Interlocked.Exchange(ref _holdActive, 0L); SignalInterlock(); }
    }
}
