// ============================================================================
// LatencyHistogram — [v12.17.5-DEV · 방향 A] 무할당 지연 히스토그램 버킷 엔진(1µs ~ 10ms · 고정 인라인 버퍼 · 백분위 도출)
//
//  왜(정직): OrderPathStageProfiler 의 누적 히스토그램은 log2 64 버킷 — 백분위가 버킷 상한이라 최대 2배 과대(16µs 다음이 32µs).
//   관제 배지에 '16µs 인지 31µs 인지'를 보여주려면 더 촘촘한 축이 필요하다. 이 엔진은 log-linear(HDR 방식) 축을 쓴다:
//    버킷 0           = ns < 1,024(≈1µs 미만 · 하한 넘침)
//    버킷 1 .. 106    = 2의 거듭제곱 구간(옥타브 2^10 .. 2^23) 마다 선형 8 등분 — 상대 오차 ≤ 12.5%
//    버킷 107         = ns > 10,485,759(≈10.5ms 초과 · 상한 넘침 — 백분위는 관측 최대로 보고)
//  고정 버퍼: [InlineArray(108)] long — 구조체 안에 108 칸이 연속 배치된다(관리 힙 배열 0 · unsafe fixed 없이 같은 배치 · C# 12).
//   호스트(DashboardSnapshotHub)는 이 구조체를 필드로 들고 펌프마다 Clear → 최근 창 표본 Record → Percentile 로 재계산한다.
//  ★스레드 계약: 단일 작성자 · 단일 판독자(같은 스레드 — 대시보드 펌프). 동시 Record 는 지원하지 않는다(원자 연산 없이 최소 비용).
//  ★축 계약: RCL 관제 라벨(MS2E.UI.Portable.Helpers.SignalLabelMapper.LatencyBucket)이 같은 축을 복제한다 — 엔진 참조 0(RCL 순수성).
//   두 축의 동기화(버킷 수 · 경계 · 라벨 단조)는 호스트 테스트(DashboardLatencyVisualizerTests)가 전 버킷 대조로 강제한다.
//  δ=0: Record · Clear · Percentile · BucketOf · UpperBoundNanos 전부 힙 0.
// ============================================================================
using System;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace StockManager.Telemetry
{
    [InlineArray(LatencyHistogram.Buckets)]
    public struct LatencyBucketArray
    {
        private long _e0;
    }

    public struct LatencyHistogram
    {
        public const int SubBuckets = 8;
        public const int MinOctave = 10;                                            // 2^10 = 1,024ns
        public const int MaxOctave = 23;                                            // 2^23 .. 2^24 옥타브(10ms 포함)
        public const int Buckets = 108;
        public const int Overflow = Buckets - 1;
        /// <summary>마지막 정규 버킷(106) 상한 = 10,485,759ns — 이보다 크면 상한 넘침.</summary>
        public const long MaxTrackedNanos = ((SubBuckets + 2L) << (MaxOctave - 3)) - 1;

        private LatencyBucketArray _b;
        private long _count, _sum, _max;

        public long Count => _count;
        public long MaxNanos => _max;
        public long MeanNanos => _count == 0 ? 0 : _sum / _count;
        public long Underflow => _b[0];
        public long OverflowCount => _b[Overflow];
        public long this[int bucket] => (uint)bucket < Buckets ? _b[bucket] : 0;

        /// <summary>ns → 버킷(0 = 1µs 미만 · 107 = 10.5ms 초과).</summary>
        public static int BucketOf(long nanos)
        {
            if (nanos < (1L << MinOctave)) return 0;
            if (nanos > MaxTrackedNanos) return Overflow;
            int msb = 63 - BitOperations.LeadingZeroCount((ulong)nanos);
            int sub = (int)(nanos >> (msb - 3)) & (SubBuckets - 1);
            return 1 + (msb - MinOctave) * SubBuckets + sub;
        }

        /// <summary>버킷 상한(ns · 포함). 넘침 버킷 = long.MaxValue.</summary>
        public static long UpperBoundNanos(int bucket)
        {
            if (bucket <= 0) return (1L << MinOctave) - 1;
            if (bucket >= Overflow) return long.MaxValue;
            int o = (bucket - 1) / SubBuckets, sub = (bucket - 1) % SubBuckets, msb = o + MinOctave;
            return ((SubBuckets + sub + 1L) << (msb - 3)) - 1;
        }

        public void Clear()
        {
            ((Span<long>)_b).Clear();
            _count = 0; _sum = 0; _max = 0;
        }

        public void Record(long nanos)
        {
            if (nanos < 0) nanos = 0;
            _b[BucketOf(nanos)]++;
            _count++;
            _sum += nanos;
            if (nanos > _max) _max = nanos;
        }

        /// <summary>
        /// 백분위(permille 1..1000 · 500 = p50 · 990 = p99) — 순위 ⌈n·p/1000⌉ 이 속한 버킷의 상한(≤ 관측 최대로 클램프 · 넘침 버킷 = 관측 최대).
        /// 표본 0 = 0.
        /// </summary>
        public long Percentile(int permille)
        {
            if (_count == 0) return 0;
            permille = Math.Clamp(permille, 1, 1000);
            long rank = Math.Max(1, (_count * permille + 999) / 1000);
            long acc = 0;
            for (int b = 0; b < Buckets; b++)
            {
                acc += _b[b];
                if (acc >= rank) return Math.Min(UpperBoundNanos(b), _max);
            }
            return _max;
        }

        /// <summary>표본 일괄 적재(Clear 후 span 전부 Record).</summary>
        public void Load(ReadOnlySpan<long> nanos)
        {
            Clear();
            for (int i = 0; i < nanos.Length; i++) Record(nanos[i]);
        }
    }
}
