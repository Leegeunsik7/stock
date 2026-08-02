// ============================================================================
// LatencySlaGatekeeper — v6.2.0 Task 3: ★자율 파괴적 혁신 모듈
//   δ=0 스트리밍 지연 히스토그램 + P99.99 SLA 실시간 이탈 차단기(fail-closed)
//
//  ── 왜 이 모듈인가 ────────────────────────────────────────────────────────
//  나노-호라이즌 파이프라인은 '평균'이 아니라 '꼬리(tail)'에서 죽는다. P99.99 한 건의 지연 폭발이 전체
//  전략을 무너뜨린다. 이 게이트키퍼는 종단 지연(ns)을 δ=0 로그-리니어 히스토그램(HdrHistogram-lite)에
//  무할당 기록하고, P99.99 가 SLA(예: 100ns)를 이탈하면 즉시 '하드웨어 퓨즈'를 비가역 트립해 발주/링 발행을
//  차단한다(옵션 OrderAttestationService.ForceFreeze 이중 봉인). 기존 SIMD Veto·CausalWeave 퓨즈와 동일한
//  fail-closed 철학을, '지연 분포의 꼬리'라는 새로운 축에서 관장한다.
//
//  ── 설계 ──────────────────────────────────────────────────────────────────
//  · 버킷: exponent(floor log2) × 2비트 mantissa = octave 당 4 sub-bucket(192 버킷, ~2^47ns 커버). 무할당.
//  · Record(nanos): 버킷 인덱스(BitOperations) + Interlocked.Increment — δ=0, 락프리 다중 생산자.
//  · Percentile/EvaluateSla: 버킷 누적 스캔(≤192) — δ=0. 상계(upper-bound) 반환이라 SLA 이탈을 보수적으로 포착.
//  · 트립: 1회 래치(비가역·자동복구 없음) — 다른 퓨즈와 동일.
//  ★불변 제약: δ=0 기록 핫패스. 기존 불변 회로 무접촉(신규 파일). liveArmed:false(순수 관측 — Veto 신호만).
// ============================================================================
using System;
using System.Numerics;
using System.Threading;

namespace StockManager.Common
{
    /// <summary>지연 분포 스냅샷(값타입·힙 0). 단위 ns.</summary>
    public readonly struct LatencySnapshotNs
    {
        public readonly long Count, MeanNanos, MaxNanos, P50, P99, P999, P9999;
        public LatencySnapshotNs(long count, long mean, long max, long p50, long p99, long p999, long p9999)
        { Count = count; MeanNanos = mean; MaxNanos = max; P50 = p50; P99 = p99; P999 = p999; P9999 = p9999; }
    }

    /// <summary>P99.99 지연 SLA 실시간 이탈 차단기(δ=0 기록·비가역 트립). 락프리 다중 생산자 + 단일 평가자.</summary>
    public sealed class LatencySlaGatekeeper
    {
        private const int MaxExp = 48;                 // 0..47 octave
        private const int SubBits = 2, SubCount = 1 << SubBits;
        private const int BucketCount = MaxExp * SubCount;   // 192

        private readonly long[] _buckets = new long[BucketCount];   // 사전할당(δ=0)
        private readonly long   _slaP9999Nanos;
        private readonly OrderAttestationService? _attestation;
        private long _count, _sum, _max;
        private int  _breached;                        // 0/1 — 1회 래치(비가역)
        private long _lastP9999;

        public LatencySlaGatekeeper(long slaP9999Nanos, OrderAttestationService? attestation = null)
        {
            _slaP9999Nanos = slaP9999Nanos > 0 ? slaP9999Nanos : 1;
            _attestation   = attestation;
        }

        public long Count            => Interlocked.Read(ref _count);
        public long MaxNanos         => Interlocked.Read(ref _max);
        public long MeanNanos        { get { long c = Interlocked.Read(ref _count); return c > 0 ? Interlocked.Read(ref _sum) / c : 0; } }
        public bool IsBreached       => Volatile.Read(ref _breached) == 1;
        public bool IsWithinSla      => Volatile.Read(ref _breached) == 0;   // ★발주/링 발행 게이트
        public long LastP9999Nanos   => Volatile.Read(ref _lastP9999);
        public long SlaP9999Nanos    => _slaP9999Nanos;

        /// <summary>지연 1건 기록(δ=0·락프리). 음수는 0 클램프.</summary>
        public void Record(long nanos)
        {
            if (nanos < 0) nanos = 0;
            Interlocked.Increment(ref _buckets[BucketIndex(nanos)]);
            Interlocked.Increment(ref _count);
            Interlocked.Add(ref _sum, nanos);
            long cur;                                   // max CAS 루프(δ=0)
            while (nanos > (cur = Interlocked.Read(ref _max)) && Interlocked.CompareExchange(ref _max, nanos, cur) != cur) { }
        }

        /// <summary>P99.99 를 산출해 SLA 이탈 시 비가역 트립(fail-closed). SLA 이내면 true. δ=0.</summary>
        public bool EvaluateSla()
        {
            long p = Percentile(0.9999);
            Volatile.Write(ref _lastP9999, p);
            if (p > _slaP9999Nanos && Interlocked.CompareExchange(ref _breached, 1, 0) == 0)
            {
                try { _attestation?.ForceFreeze($"P99.99 지연 SLA 이탈 — {p}ns > {_slaP9999Nanos}ns. 발주 회로 비가역 차단."); } catch { }
            }
            return Volatile.Read(ref _breached) == 0;
        }

        /// <summary>백분위 지연(ns·상계 추정). p∈[0,1]. δ=0(스택 스냅샷·힙 0).</summary>
        public long Percentile(double p)
        {
            if (p < 0) p = 0; else if (p > 1) p = 1;
            // ★일관 스냅샷(δ=0·stackalloc·힙 0): 버킷당 1회 캡처해 total 과 스캔을 '동일 스냅샷'에서 산출.
            //   2패스(total=pass1·scan=pass2)는 버킷 단조증가로 pass2 누적 ≥ pass1 → 크로싱 조기도달=체계적 과소보고(fail-open·SLA
            //   미트립)였음. 단일 스냅샷은 total 과 scan 이 같은 값이라 그 편향을 제거(잔차는 캡처 순간차뿐·비체계적·미소).
            Span<long> snap = stackalloc long[BucketCount];
            long total = 0;
            for (int i = 0; i < BucketCount; i++) { long c = Volatile.Read(ref _buckets[i]); snap[i] = c; total += c; }
            if (total == 0) return 0;
            // ★보수적 nearest-rank = ceil(p·N)(floor 아님). floor((long)total*p)는 p·N 비정수 시 랭크가 1 낮아 꼬리가 버킷
            //   경계를 넘는 정확한 케이스(모듈 존재 이유)에서 하위 octave 로 떨어져 과소보고(fail-open·SLA 미트립)였음.
            long target = (long)Math.Ceiling(total * (decimal)p);
            if (target < 1) target = 1;
            long cum = 0;
            for (int i = 0; i < BucketCount; i++)
            {
                cum += snap[i];
                if (cum >= target) return BucketUpperBound(i);
            }
            return BucketUpperBound(BucketCount - 1);
        }

        /// <summary>분포 스냅샷 캡처(δ=0·텔레메트리/UI).</summary>
        public LatencySnapshotNs CaptureSnapshot()
            => new(Count, MeanNanos, MaxNanos, Percentile(0.50), Percentile(0.99), Percentile(0.999), Percentile(0.9999));

        // 값 → 버킷(exponent×4 + 2비트 mantissa). δ=0.
        private static int BucketIndex(long v)
        {
            if (v <= 1) return 0;
            int exp = 63 - BitOperations.LeadingZeroCount((ulong)v);          // floor(log2 v), ≥1
            int sub = exp >= SubBits ? (int)((v >> (exp - SubBits)) & (SubCount - 1)) : 0;
            int idx = exp * SubCount + sub;
            return idx >= BucketCount ? BucketCount - 1 : idx;
        }

        // 버킷 상계(대표값). 단조 증가.
        private static long BucketUpperBound(int idx)
        {
            int exp = idx >> SubBits, sub = idx & (SubCount - 1);
            if (exp < SubBits) return (1L << (exp + 1)) - 1;   // ★off-by-one 교정: exp<SubBits 버킷 상계 = 2^(exp+1)-1(하계 2^exp 아님)
            return (((long)(SubCount + sub + 1)) << (exp - SubBits)) - 1;
        }
    }
}
