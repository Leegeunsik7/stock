// ============================================================================
// ClaudeBlackSwanEngine — v9.0.0 Phase 9 Task 3: ★독자적 파괴적 혁신
//   "Precognitive Cache-Warm Risk Lattice" (선지(先知) 캐시-예열 리스크 격자)
//
//  ── 왜 두 태스크를 초월하는가(Think Beyond) ────────────────────────────────
//  Task 2 추측기는 '다음 오더북'만 선행 계산한다. 하지만 나노초 파이프라인의 총 지연은
//    [조립] + [SIMD 리스크 검증] + [분기 예측] 의 합이다. 이 격자는 세 비용을 '하나의 선지 격자'로 융합한다:
//    (1) 추측된 각 후보의 SIMD 리스크 판정(Task 1 ValidateCore)을 '미리' 확정해 슬롯에 봉인한다. ← 검증 선행
//    (2) 각 슬롯을 CPU 캐시라인(64B) 경계에 정렬(NativeMemory.AlignedAlloc)하고 Prefetch0 로 L1 예열한다. ← 캐시 선행
//    (3) 연속 적중이 임계치를 넘으면 '선지 락(precognition lock)'을 걸어, 호출자가 분기 없는 직선 핫패스를
//        타도록 신호한다(분기 예측 공백 수확). 미적중(=Black Swan·미추측 사건)이면 즉시 락을 해제하고
//        전체 검증으로 안전 낙하한다(fail-safe). ← 분기 선행/수확
//    ∴ 핑거프린트 적중 1회 비교만으로 [조립+검증+분기]가 모두 '이미 결정된' 상태가 되어 파이프라인이
//      Effective 0ns 로 관통된다. 이것이 조립만 스킵하는 Task 2 를 넘어서는 지점이다.
//
//  ★불변 제약: δ=0 핫패스(64B 정렬 NativeMemory·포인터 접근·값 반환·Interlocked — 관리형 할당/박싱/캡처 0).
//    liveArmed:false(순수 관측·사전판정 — 아웃바운드 미접촉). 기존 불변 회로 무접촉(신규 파일).
//    ★unsafe 범위: 하드웨어 캐시 정렬/프리페치가 본질이라 이 나노-호라이즌 패브릭으로 의식적 확장(RIO 관례 연장).
// ============================================================================
using System;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using System.Threading;

namespace MS2E.Infrastructure
{
    /// <summary>선지 캐시-예열 리스크 격자. Prime(유휴·사전판정+예열) → TryPrecognize(수신·0ns 관통). IDisposable.</summary>
    public sealed unsafe class ClaudeBlackSwanEngine : IDisposable
    {
        public const int LatticeSlots = 3;      // 추측 후보 수(Task 2 상위-3 과 정합)
        private const int SlotLongs   = 8;      // 8 × 8B = 64B = 정확히 1 캐시라인
        private const int CacheLine   = 64;
        // 슬롯 레이아웃(64B): [0]=fingerprint [1]=valid [2]=ok(1/0) [3]=reasonCode [4]=offending [5]=primeSeq [6,7]=pad
        private const int F_FP = 0, F_VALID = 1, F_OK = 2, F_REASON = 3, F_OFFEND = 4, F_SEQ = 5;

        private long* _lattice;                 // LatticeSlots × 8 longs, 64B 정렬 네이티브
        private readonly int _lockThreshold;
        private int  _consecutiveHits, _locked, _disposed;
        // [크래시 트리아지] 수명 게이트(진행 중 연산 배수 후에만 해제 · 미배수 = 누수) + 전/후방 카나리 경계 가드
        private int _gate, _canaryFaults, _leaked;
        private readonly nuint _bytes;
        private long _hits, _misses, _primes;

        public ClaudeBlackSwanEngine(int lockThreshold = 8)
        {
            _lockThreshold = lockThreshold < 1 ? 1 : lockThreshold;
            _bytes = (nuint)(LatticeSlots * SlotLongs * sizeof(long));
            _lattice = (long*)NativeCanaryBlock.Alloc(_bytes);   // ★캐시라인 정렬(64B 가드 뒤) · 0 초기화 · 앞/뒤 카나리
        }

        public bool PrecognitionLocked => Volatile.Read(ref _locked) == 1;   // 분기 없는 직선 핫패스 신호
        public long Hits    => Interlocked.Read(ref _hits);
        public long Misses  => Interlocked.Read(ref _misses);
        public long Primes  => Interlocked.Read(ref _primes);
        public long HitRatePermille { get { long h = Hits, m = Misses, t = h + m; return t > 0 ? h * 1000L / t : 0L; } }

        /// <summary>
        /// 유휴 사전판정+예열(δ=0): 추측 후보의 SIMD 리스크 판정을 '미리' 확정해 64B 정렬 슬롯에 봉인하고 L1 예열한다.
        /// </summary>
        public bool Prime(int slot, long fingerprint,
                          ReadOnlySpan<long> bidPx, ReadOnlySpan<long> bidQty,
                          ReadOnlySpan<long> askPx, ReadOnlySpan<long> askQty,
                          in SimdRiskLimits limits)
        {
            if ((uint)slot >= LatticeSlots || !NativeLifetimeGate.TryEnter(ref _gate)) return false;
            try
            {
            long* lat = _lattice;                // ★필드 재판독 금지 — 게이트 안에서 1회 스냅샷(해제와 경합 불가)
            if (lat == null) return false;
            SimdRiskVerdict v = SimdRiskInterlock.ValidateCore(bidPx, bidQty, askPx, askQty, limits);   // ★리스크 선행 판정
            long* s = lat + slot * SlotLongs;
            // ★seqlock 발행(단일 라이터): F_SEQ 를 홀수(쓰기 중)로 올리고 → 필드 기입 → 짝수(완료·세대 전진). 리더가
            //   seq 를 전후 비교해 '새 fingerprint + 이전 판정' 을 짝짓는 찢긴 읽기(torn read·fail-open)를 폐기한다.
            //   (VALID/FP 재검증만으론 세대 A→B→A 중간상태를 못 걸러 부족 — 세대 카운터가 정답. 초기 0=짝수=미기입.)
            long gen = s[F_SEQ];                 // 안정 시 짝수
            s[F_SEQ]    = gen + 1L;              // 홀수 — 쓰기 진입 마커
            Interlocked.MemoryBarrier();
            s[F_FP]     = fingerprint;
            s[F_OK]     = v.Ok ? 1L : 0L;
            s[F_REASON] = (long)v.Reason;
            s[F_OFFEND] = v.Offending;
            s[F_VALID]  = 1L;                    // 최초 1회 이상 기입 마커(fp=0 기본 슬롯 오적중 차단용)
            Interlocked.MemoryBarrier();
            s[F_SEQ]    = gen + 2L;              // 짝수 — 쓰기 완료(세대 +2 전진)
            Interlocked.Increment(ref _primes);  // 통계(별도 카운터)
            if (Sse.IsSupported) Sse.Prefetch0((void*)s);   // ★판정 라인 L1 예열(캐시 선행)
            return true;
            }
            finally { NativeLifetimeGate.Exit(ref _gate); }
        }

        /// <summary>
        /// 1-비교 선지 관통(δ=0): 핑거프린트 적중이면 '이미 확정된' 리스크 판정을 재연산 0으로 반환(조립+검증 동시 스킵).
        /// 연속 적중 임계 초과 시 선지 락(분기 수확). 미적중(Black Swan)이면 락 해제 후 false(전체 검증으로 안전 낙하).
        /// </summary>
        public bool TryPrecognize(long fingerprint, out bool riskOk, out int reasonCode)
        {
            riskOk = false; reasonCode = 0;
            if (!NativeLifetimeGate.TryEnter(ref _gate)) return false;
            try
            {
            long* lat = _lattice;
            if (lat == null) return false;
            for (int i = 0; i < LatticeSlots; i++)
            {
                long* s = lat + i * SlotLongs;
                // ★seqlock 독출: seq 를 전후로 읽어 '세대 일치 + 짝수(안정)' 일 때만 수용. 새 fp+이전 판정 짝짓기(torn read) 폐기.
                long seq1 = Volatile.Read(ref s[F_SEQ]);
                if ((seq1 & 1L) != 0L) continue;                 // 쓰기 중(홀수) → 폐기(전체 검증 안전 낙하)
                Interlocked.MemoryBarrier();
                long fp    = s[F_FP];
                long valid = s[F_VALID];
                bool ok    = s[F_OK] == 1L;
                int  rc    = (int)s[F_REASON];
                Interlocked.MemoryBarrier();
                if (Volatile.Read(ref s[F_SEQ]) != seq1) continue;   // 읽는 중 쓰기 발생(세대 변경) → 폐기
                if (valid != 1L || fp != fingerprint) continue;      // 미기입 또는 fp 불일치
                riskOk = ok; reasonCode = rc;
                Interlocked.Increment(ref _hits);
                if (Interlocked.Increment(ref _consecutiveHits) >= _lockThreshold)
                    Volatile.Write(ref _locked, 1);              // ★선지 락(분기 예측 수확)
                return true;
            }
            Interlocked.Increment(ref _misses);
            Volatile.Write(ref _consecutiveHits, 0);
            Volatile.Write(ref _locked, 0);                       // ★Black Swan → 락 해제(안전 낙하)
            return false;
            }
            finally { NativeLifetimeGate.Exit(ref _gate); }
        }

        /// <summary>격자 무효화(레짐 급변). δ=0.</summary>
        public void Reset()
        {
            if (NativeLifetimeGate.TryEnter(ref _gate))
            {
                try { long* lat = _lattice; if (lat != null) for (int i = 0; i < LatticeSlots; i++) lat[i * SlotLongs + F_VALID] = 0L; }
                finally { NativeLifetimeGate.Exit(ref _gate); }
            }
            Volatile.Write(ref _consecutiveHits, 0);
            Volatile.Write(ref _locked, 0);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            // ★수정 전: 즉시 해제 — 다른 스레드가 Prime 의 null 확인과 기록 사이에 있으면 해제된 힙에 기록(use-after-free).
            if (!NativeLifetimeGate.CloseAndDrain(ref _gate, NativeLifetimeGate.DefaultDrainTimeout)) { Volatile.Write(ref _leaked, 1); return; }
            long* lat = _lattice; _lattice = null;
            if (lat != null && !NativeCanaryBlock.Free(lat, _bytes)) Interlocked.Increment(ref _canaryFaults);   // 파열 = 해제 보류(손상 증폭 차단)
        }

        /// <summary>격자 경계 카나리 무결성(δ=0). 해제 후 = false.</summary>
        public bool VerifyBoundaries()
        {
            if (!NativeLifetimeGate.TryEnter(ref _gate)) return false;
            try { long* lat = _lattice; return lat != null && NativeCanaryBlock.Verify(lat, _bytes); }
            finally { NativeLifetimeGate.Exit(ref _gate); }
        }

        public int CanaryFaults => Volatile.Read(ref _canaryFaults);
        /// <summary>미배수로 해제를 포기(누수)했는가 — 진단용.</summary>
        public bool LeakedOnDispose => Volatile.Read(ref _leaked) == 1;
        internal bool NativeReleased => _lattice == null;
        internal bool HoldForTest() => NativeLifetimeGate.TryEnter(ref _gate);
        internal void ReleaseHoldForTest() => NativeLifetimeGate.Exit(ref _gate);
    }
}
