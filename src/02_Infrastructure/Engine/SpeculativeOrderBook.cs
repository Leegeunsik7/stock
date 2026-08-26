// ============================================================================
// SpeculativeOrderBook — v9.0.0 Phase 9 Task 2: 추측적 제로-지연 선행 시뮬레이터
//
//  패킷 간 미세 유휴 시간(Idle micro-loop)에, 직전 추세에 기반한 '다음 도착 확률 상위 3종' L3 호가 델타 상태를
//  사전 조립·핑거프린트해 캐시한다. 신규 UDP 패킷 도착 시 64비트 FNV-1a 핑거프린트만 비교(1-사이클) —
//  사전 계산 캐시에 적중(Hit)하면 L3 조립 파이프라인을 건너뛰고 캐시된 오더북 상태를 즉시 적용(Effective 0ns).
//
//  ★불변 제약: δ=0(사전할당 _store/_fp/_valid 재사용·스팬 복사·값 반환 — new/box/람다캡처/LINQ 0). 결정론(순수 해시).
//    liveArmed:false(순수 관측·조립 — 아웃바운드 미접촉). 기존 불변 회로 무접촉(신규 파일). unsafe 미사용(관리형 배킹).
// ============================================================================
using System;
using System.Runtime.CompilerServices;

namespace StockManager.Common
{
    /// <summary>추측 L3 오더북 캐시(상위 3후보·1-사이클 핑거프린트 적중). 단일 라이터(유휴) + 단일 리더(수신).</summary>
    public sealed class SpeculativeOrderBook
    {
        public const int Candidates = 3;
        public const int Levels     = 10;
        private const int Stride    = Levels * 4;   // [bidPx|bidQty|askPx|askQty] × 10
        private const ulong FnvOffset = 14695981039346656037UL;
        private const ulong FnvPrime  = 1099511628211UL;

        private readonly long[] _store = new long[Candidates * Stride];   // 사전할당 후보 사다리 배킹(δ=0)
        private readonly long[] _fp    = new long[Candidates];            // 후보 핑거프린트
        private readonly bool[] _valid = new bool[Candidates];
        private long _primed, _hits, _misses;
        private int  _lastHitSlot = -1;

        public long Primed  => _primed;
        public long Hits    => _hits;
        public long Misses  => _misses;
        public int  LastHitSlot => _lastHitSlot;
        public long HitRatePermille { get { long h = _hits, m = _misses, t = h + m; return t > 0 ? h * 1000L / t : 0L; } }

        // ── 64비트 FNV-1a 핑거프린트(δ=0) ──
        public static long Fingerprint(ReadOnlySpan<byte> data)
        {
            ulong h = FnvOffset;
            for (int i = 0; i < data.Length; i++) { h ^= data[i]; h *= FnvPrime; }
            return unchecked((long)h);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void Fold(ref ulong h, long value)
        {
            ulong u = unchecked((ulong)value);
            for (int b = 0; b < 8; b++) { h ^= (byte)(u >> (b * 8)); h *= FnvPrime; }
        }

        /// <summary>사다리 4수열의 64비트 핑거프린트(δ=0). 수신 패킷 조립 결과와 동일 순서로 폴딩.</summary>
        public static long FingerprintLadder(ReadOnlySpan<long> bidPx, ReadOnlySpan<long> bidQty,
                                             ReadOnlySpan<long> askPx, ReadOnlySpan<long> askQty)
        {
            ulong h = FnvOffset;
            for (int i = 0; i < bidPx.Length;  i++) Fold(ref h, bidPx[i]);
            for (int i = 0; i < bidQty.Length; i++) Fold(ref h, bidQty[i]);
            for (int i = 0; i < askPx.Length;  i++) Fold(ref h, askPx[i]);
            for (int i = 0; i < askQty.Length; i++) Fold(ref h, askQty[i]);
            return unchecked((long)h);
        }

        /// <summary>후보 슬롯에 추측 사다리를 사전 조립·핑거프린트(유휴 시간·δ=0). 형식 불량이면 false.</summary>
        public bool Prime(int slot, ReadOnlySpan<long> bidPx, ReadOnlySpan<long> bidQty,
                          ReadOnlySpan<long> askPx, ReadOnlySpan<long> askQty)
        {
            if ((uint)slot >= Candidates) return false;
            if (bidPx.Length != Levels || bidQty.Length != Levels || askPx.Length != Levels || askQty.Length != Levels) return false;
            int b = slot * Stride;
            var dst = _store.AsSpan(b, Stride);
            bidPx.CopyTo(dst.Slice(0, Levels));
            bidQty.CopyTo(dst.Slice(Levels, Levels));
            askPx.CopyTo(dst.Slice(Levels * 2, Levels));
            askQty.CopyTo(dst.Slice(Levels * 3, Levels));
            _fp[slot] = FingerprintLadder(bidPx, bidQty, askPx, askQty);
            _valid[slot] = true;
            _primed++;
            return true;
        }

        /// <summary>
        /// 추세 기반 상위-3 델타 사전연산(유휴·δ=0): 후보0=+tickDelta(상승), 1=-tickDelta(하락), 2=불변. 잔량은 유지.
        /// baseline 사다리를 받아 세 후보를 조립·핑거프린트한다.
        /// </summary>
        public bool PrimeFromTrend(ReadOnlySpan<long> baseBidPx, ReadOnlySpan<long> baseBidQty,
                                   ReadOnlySpan<long> baseAskPx, ReadOnlySpan<long> baseAskQty, long tickDelta)
        {
            if (baseBidPx.Length != Levels || baseBidQty.Length != Levels || baseAskPx.Length != Levels || baseAskQty.Length != Levels) return false;
            for (int c = 0; c < Candidates; c++)
            {
                long d = c == 0 ? tickDelta : c == 1 ? -tickDelta : 0L;   // 상승/하락/불변
                int b = c * Stride;
                var bp = _store.AsSpan(b, Levels);
                var bq = _store.AsSpan(b + Levels, Levels);
                var ap = _store.AsSpan(b + Levels * 2, Levels);
                var aq = _store.AsSpan(b + Levels * 3, Levels);
                for (int i = 0; i < Levels; i++)
                {
                    bp[i] = baseBidPx[i] + d;   // 편측 동시 시프트(추세 델타)
                    ap[i] = baseAskPx[i] + d;
                    bq[i] = baseBidQty[i];
                    aq[i] = baseAskQty[i];
                }
                _fp[c] = FingerprintLadder(bp, bq, ap, aq);
                _valid[c] = true;
            }
            _primed += Candidates;
            return true;
        }

        /// <summary>1-사이클 핑거프린트 적중(δ=0). 캐시된 후보와 일치하면 슬롯 반환(조립 스킵), 미적중이면 false.</summary>
        public bool TryResolve(long fingerprint, out int hitSlot)
        {
            for (int c = 0; c < Candidates; c++)
            {
                if (_valid[c] && _fp[c] == fingerprint)   // 64비트 비교(사실상 1-사이클)
                {
                    hitSlot = c; _lastHitSlot = c; _hits++;
                    return true;
                }
            }
            hitSlot = -1; _misses++;
            return false;
        }

        /// <summary>적중 슬롯의 캐시된 사다리를 호출자 버퍼로 복사(δ=0·조립 파이프라인 대체 적용).</summary>
        public bool CopyCandidate(int slot, Span<long> bidPx, Span<long> bidQty, Span<long> askPx, Span<long> askQty)
        {
            if ((uint)slot >= Candidates || !_valid[slot]) return false;
            if (bidPx.Length != Levels || bidQty.Length != Levels || askPx.Length != Levels || askQty.Length != Levels) return false;
            int b = slot * Stride;
            _store.AsSpan(b, Levels).CopyTo(bidPx);
            _store.AsSpan(b + Levels, Levels).CopyTo(bidQty);
            _store.AsSpan(b + Levels * 2, Levels).CopyTo(askPx);
            _store.AsSpan(b + Levels * 3, Levels).CopyTo(askQty);
            return true;
        }

        /// <summary>추측 캐시 무효화(레짐 급변 시). δ=0.</summary>
        public void Invalidate()
        {
            for (int c = 0; c < Candidates; c++) _valid[c] = false;
            _lastHitSlot = -1;
        }
    }
}
