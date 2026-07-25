// ============================================================================
// SubMicrosecondJitterAnalyzer — v6.3.0 Task 4: ★자율 파괴적 혁신
//   IPC 지연 '지터(Jitter)' 실시간 히스토그램 프로파일러 (δ=0 · 커널 메모리 집계)
//
//  ── 왜 이 모듈인가 ────────────────────────────────────────────────────────
//  HFT 결정론은 '평균 지연'이 아니라 '지연의 흔들림(jitter)'에서 무너진다. 도달 지연 자체가 낮아도, 연속
//  프레임 간 지연 변동(jitter = |lat[i] - lat[i-1]|)이 크면 파이프라인 예측이 깨지고 큐가 요동친다. 이 분석기는
//  IPC 도달 지연 표본열을 받아 '인접 표본 간 지터'를 로그-리니어 히스토그램에 δ=0 무할당 집계하고, 특히
//  P99.9 '꼬리 지터'(worst-case 타이밍 불안정)를 산출한다 — 평균이 숨기는 미세 스파이크를 드러낸다.
//
//  ── 설계 ──────────────────────────────────────────────────────────────────
//  · 버킷: exponent(floor log2) × 2비트 mantissa = octave 당 4 sub-bucket(160 버킷, ~2^40ns 커버). 무할당.
//  · Record(latencyNs): 직전 지연과의 절대차(지터)를 버킷팅(Interlocked) — δ=0, 락프리 다중 생산자.
//  · Percentile/MeanJitter/MaxJitter: 버킷 누적 스캔(≤160) — δ=0. IsStable(threshold)로 안정성 분류.
//  ★불변 제약: δ=0 기록 핫패스. 기존 불변 회로 무접촉(신규 파일). liveArmed:false(순수 관측 집계).
// ============================================================================
using System.Numerics;
using System.Threading;

namespace StockManager.Common
{
    /// <summary>IPC 지연 지터 분포 스냅샷(값타입·힙 0). 단위 ns.</summary>
    public readonly struct JitterSnapshotNs
    {
        public readonly long Samples, MeanNanos, MaxNanos, P50, P99, P999;
        public JitterSnapshotNs(long samples, long mean, long max, long p50, long p99, long p999)
        { Samples = samples; MeanNanos = mean; MaxNanos = max; P50 = p50; P99 = p99; P999 = p999; }
    }

    /// <summary>IPC 지연 지터 실시간 프로파일러(δ=0 기록·락프리). 인접 표본 간 |Δ지연| 분포 집계.</summary>
    public sealed class SubMicrosecondJitterAnalyzer
    {
        private const int MaxExp = 40, SubBits = 2, SubCount = 1 << SubBits;   // 4
        private const int BucketCount = MaxExp * SubCount;                     // 160

        private readonly long[] _buckets = new long[BucketCount];   // 사전할당(δ=0)
        private long _lastLatency = -1L, _count, _sumJitter, _maxJitter;

        public long JitterSamples  => Interlocked.Read(ref _count);
        public long MaxJitterNanos => Interlocked.Read(ref _maxJitter);
        public long MeanJitterNanos { get { long c = Interlocked.Read(ref _count); return c > 0 ? Interlocked.Read(ref _sumJitter) / c : 0; } }

        /// <summary>지연 표본 1건 기록(δ=0·락프리). 직전 표본과의 절대차를 지터로 버킷팅한다.</summary>
        public void Record(long latencyNanos)
        {
            if (latencyNanos < 0) latencyNanos = 0;
            long last = Interlocked.Exchange(ref _lastLatency, latencyNanos);
            if (last < 0L) return;                                            // 첫 표본 — 지터 미정
            long jitter = latencyNanos >= last ? latencyNanos - last : last - latencyNanos;
            Interlocked.Increment(ref _buckets[BucketIndex(jitter)]);
            Interlocked.Increment(ref _count);
            Interlocked.Add(ref _sumJitter, jitter);
            long cur;                                                          // max CAS 루프(δ=0)
            while (jitter > (cur = Interlocked.Read(ref _maxJitter)) && Interlocked.CompareExchange(ref _maxJitter, jitter, cur) != cur) { }
        }

        /// <summary>지터 백분위(ns·상계 추정). p∈[0,1]. δ=0(버킷 누적 스캔).</summary>
        public long PercentileNanos(double p)
        {
            long total = Interlocked.Read(ref _count);
            if (total == 0) return 0;
            if (p < 0) p = 0; else if (p > 1) p = 1;
            long target = (long)(total * p); if (target < 1) target = 1;
            long cum = 0;
            for (int i = 0; i < BucketCount; i++)
            {
                cum += Volatile.Read(ref _buckets[i]);
                if (cum >= target) return BucketUpperBound(i);
            }
            return BucketUpperBound(BucketCount - 1);
        }

        /// <summary>P99.9 꼬리 지터가 임계(ns) 이내면 안정. 미세 스파이크 조기 감지.</summary>
        public bool IsStable(long p999ThresholdNanos) => PercentileNanos(0.999) <= p999ThresholdNanos;

        /// <summary>지터 분포 스냅샷 캡처(δ=0·텔레메트리).</summary>
        public JitterSnapshotNs CaptureSnapshot()
            => new(JitterSamples, MeanJitterNanos, MaxJitterNanos, PercentileNanos(0.50), PercentileNanos(0.99), PercentileNanos(0.999));

        private static int BucketIndex(long v)
        {
            if (v <= 1) return 0;
            int exp = 63 - BitOperations.LeadingZeroCount((ulong)v);
            int sub = exp >= SubBits ? (int)((v >> (exp - SubBits)) & (SubCount - 1)) : 0;
            int idx = exp * SubCount + sub;
            return idx >= BucketCount ? BucketCount - 1 : idx;
        }

        private static long BucketUpperBound(int idx)
        {
            int exp = idx >> SubBits, sub = idx & (SubCount - 1);
            // exp < SubBits: 선두비트 아래 mantissa 2비트를 뽑을 수 없어 octave [2^exp, 2^(exp+1)-1] 전체가
            // 단일 버킷으로 접힌다 → 포함 상계는 2^(exp+1)-1 (기존 1<<exp 는 exp=1[값2,3]에서 3 대신 2로 과소보고).
            if (exp < SubBits) return (1L << (exp + 1)) - 1;
            return (((long)(SubCount + sub + 1)) << (exp - SubBits)) - 1;
        }
    }
}
