// ============================================================================
// TickToTradeLatencyProfiler — [저지연 · 추천 A] 주문별 Tick-to-Trade µs 계측 · 최근 1,024건 분포 · 네이티브 로그 히스토그램(방향 A)
//                              + 지연 폭주/스레드 정지 Disarm 인터락(방향 B)
//
//  정의(정직): Tick-to-Trade = ① 판정에 쓴 참조가 틱의 나이(ReferencePriceFeedAdapter 수신 시각 기준 · RouterFeedGate 가 동봉)
//                             + ② 프로세스 내 구간(OrderDrainWorker 실전 경로 진입 → 실 송출 반환).
//   ①은 시장 활동에 좌우된다(한산한 종목은 마지막 틱이 수 초 전일 수 있고 3초 초과면 이미 Stale 로 거부). ②가 우리 코드가
//   통제하는 지연이며 정지 판정은 ②로만 한다(①로 판정하면 한산한 종목 주문이 전부 차단된다).
//   ★전략이 어떤 틱에 반응해 제안을 냈는지(틱 → 제안 발행 구간)는 링에 인과 타임스탬프가 없어 측정하지 않는다.
//
//  기록 경로(δ=0): Stopwatch 단조 tick → µs(Int128) · 링 슬롯 Interlocked 인덱스 · 히스토그램 버킷 Interlocked.Increment(네이티브 메모리)
//   · 최대값 CAS. 분포 조회(Snapshot)는 콜드 — 사전할당 스크래치에 링 복사 후 제자리 정렬(힙 0).
//  [방향 A] 로그-선형 히스토그램: 0~15µs 는 1µs 해상도, 그 위는 2의 거듭제곱 구간당 8 하위 버킷(상대 오차 ≤ 12.5%) · 512 버킷.
//   백분위는 버킷 상한(보수적)으로 보고 — P99.9 같은 꼬리를 표본 보관 없이 O(버킷) 로 산출.
//  [방향 B] AdmitBeforeSend: 진입 → 송출 직전 경과 > 임계(기본 5,000µs) 이면 그 주문 미송출 + sticky Disarm(물리 이중키 재무장).
//   RecordSent: 송출까지 포함한 ② 가 임계 초과면 Disarm 만(이미 나간 주문의 자동 취소는 하지 않는다 — 체결 경합 위험 · 추적/대사가 관리).
// ============================================================================
using System;
using System.Diagnostics;
using System.Numerics;
using System.Threading;
using MS2E.Core;
using MS2E.Infrastructure;
using StockManager.Buffers;

namespace StockManager.Telemetry
{
    public readonly record struct LatencyDistribution(long Count, long MeanMicros, long P50Micros, long P99Micros, long P999Micros, long MaxMicros);

    public sealed unsafe class TickToTradeLatencyProfiler : ITickToTradeProbe, IArmingBlockSource, IDisposable
    {
        public const int RingSize = 1024;
        public const int HistogramBuckets = 512;
        public const long DefaultStallThresholdMicros = 5_000;

        private readonly Func<long> _now;
        private readonly long _freq;
        private readonly long _stallMicros;
        private readonly long[] _ringInProc = new long[RingSize], _ringT2T = new long[RingSize];
        private readonly long[] _scratch = new long[RingSize];
        private readonly NativeMemoryHandle _hist;   // [방향 A] 2 × 512 long (프로세스 내 · Tick-to-Trade)
        private readonly object _snapshotGate = new();
        private long _idx, _count, _sumInProc, _sumT2T, _maxInProc, _maxT2T;
        private long _preSendStalls, _postSendStalls;
        private int _tripped;
        private ArmingInterlockLink? _link;

        public TickToTradeLatencyProfiler(long stallThresholdMicros = DefaultStallThresholdMicros, Func<long>? nowTimestamp = null, long timestampFrequency = 0)
        {
            if (stallThresholdMicros < 1) throw new ArgumentOutOfRangeException(nameof(stallThresholdMicros));
            _stallMicros = stallThresholdMicros;
            _now = nowTimestamp ?? Stopwatch.GetTimestamp;
            _freq = timestampFrequency > 0 ? timestampFrequency : Stopwatch.Frequency;
            _hist = new NativeMemoryHandle((nuint)(2 * HistogramBuckets * sizeof(long)));
        }

        public long StallThresholdMicros => _stallMicros;
        public long Count => Interlocked.Read(ref _count);
        public long PreSendStalls => Interlocked.Read(ref _preSendStalls);
        public long PostSendStalls => Interlocked.Read(ref _postSendStalls);
        public bool Tripped => Volatile.Read(ref _tripped) == 1;
        public bool ArmingBlocked => Tripped;

        public void BindInterlock(ArmingInterlockBitmask aggregator, ArmingInterlockSource bit)
        {
            var link = aggregator.Bind(bit, this);
            Volatile.Write(ref _link, link);
            link.Resync();
        }

        public bool TryRearm(bool valveWired, bool liveArmingWired)
        {
            if (!(valveWired && liveArmingWired)) return false;
            Volatile.Write(ref _tripped, 0);
            Volatile.Read(ref _link)?.Resync();
            return true;
        }

        // ── ITickToTradeProbe(δ=0) ────────────────────────────────────────────

        public long Timestamp() => _now();

        public bool AdmitBeforeSend(long dispatchStartTimestamp)
        {
            long elapsed = ToMicros(_now() - dispatchStartTimestamp);
            if (elapsed <= _stallMicros) return true;
            Interlocked.Increment(ref _preSendStalls);
            Trip();
            return false;
        }

        public void RecordSent(long dispatchStartTimestamp, long basisAgeMicros)
        {
            long inProc = ToMicros(_now() - dispatchStartTimestamp);
            long t2t = basisAgeMicros >= 0 ? SatAdd(basisAgeMicros, inProc) : inProc;
            long slot = (Interlocked.Increment(ref _idx) - 1) & (RingSize - 1);
            Volatile.Write(ref _ringInProc[slot], inProc);
            Volatile.Write(ref _ringT2T[slot], t2t);
            Interlocked.Increment(ref _count);
            Interlocked.Add(ref _sumInProc, inProc);
            Interlocked.Add(ref _sumT2T, t2t);
            AtomicMax(ref _maxInProc, inProc);
            AtomicMax(ref _maxT2T, t2t);
            Bump(0, inProc);
            Bump(1, t2t);
            if (inProc > _stallMicros) { Interlocked.Increment(ref _postSendStalls); Trip(); }
        }

        // ── 분포(콜드 · 힙 0) ─────────────────────────────────────────────────

        /// <summary>프로세스 내 구간(진입 → 송출) 분포. P50/P99 = 최근 1,024건 정확값 · P99.9 = 전 기간 히스토그램(버킷 상한).</summary>
        public LatencyDistribution InProcess() => Snapshot(_ringInProc, 0, ref _sumInProc, ref _maxInProc);

        /// <summary>Tick-to-Trade(참조가 틱 나이 + 프로세스 내 구간) 분포.</summary>
        public LatencyDistribution TickToTrade() => Snapshot(_ringT2T, 1, ref _sumT2T, ref _maxT2T);

        private LatencyDistribution Snapshot(long[] ring, int hist, ref long sum, ref long max)
        {
            lock (_snapshotGate)
            {
                long count = Interlocked.Read(ref _count);
                if (count == 0) return default;
                int n = (int)Math.Min(count, RingSize);
                long w = Interlocked.Read(ref _idx);
                for (int k = 0; k < n; k++) _scratch[k] = Volatile.Read(ref ring[(w - n + k) & (RingSize - 1)]);
                var span = _scratch.AsSpan(0, n);
                span.Sort();
                long p50 = span[Math.Min(n - 1, (n * 50 + 99) / 100 - 1)];
                long p99 = span[Math.Min(n - 1, Math.Max(0, (n * 99 + 99) / 100 - 1))];
                return new LatencyDistribution(count, Interlocked.Read(ref sum) / count, p50, p99,
                                               HistogramPercentile(hist, 999, 1000), Interlocked.Read(ref max));
            }
        }

        /// <summary>[방향 A] 히스토그램 백분위(num/den) — 해당 버킷 상한(보수적). 표본 0 = 0.</summary>
        public long HistogramPercentile(int hist, long num, long den)
        {
            bool added = false;
            try
            {
                _hist.DangerousAddRef(ref added);
                long* h = (long*)_hist.Pointer + hist * HistogramBuckets;
                long total = 0;
                for (int i = 0; i < HistogramBuckets; i++) total += Volatile.Read(ref h[i]);
                if (total == 0) return 0;
                long target = (long)(((Int128)total * num + den - 1) / den);
                long cum = 0;
                for (int i = 0; i < HistogramBuckets; i++)
                {
                    cum += Volatile.Read(ref h[i]);
                    if (cum >= target) return BucketUpper(i);
                }
                return BucketUpper(HistogramBuckets - 1);
            }
            catch (ObjectDisposedException) { return 0; }
            finally { if (added) _hist.DangerousRelease(); }
        }

        /// <summary>µs → 로그-선형 버킷(0~15 = 1µs · 이후 2^m 구간당 8 하위 버킷).</summary>
        public static int BucketOf(long micros)
        {
            if (micros < 16) return micros < 0 ? 0 : (int)micros;
            int msb = 63 - BitOperations.LeadingZeroCount((ulong)micros);
            int sub = (int)((micros >> (msb - 3)) & 7);
            int idx = 16 + (msb - 4) * 8 + sub;
            return idx < HistogramBuckets ? idx : HistogramBuckets - 1;
        }

        public static long BucketUpper(int idx)
        {
            if (idx < 16) return idx;
            int msb = (idx - 16) / 8 + 4, sub = (idx - 16) % 8;
            long width = 1L << (msb - 3);
            long low = (8L + sub) << (msb - 3);
            return low + width - 1;
        }

        // ── 내부 ──────────────────────────────────────────────────────────────

        private void Bump(int hist, long micros)
        {
            bool added = false;
            try
            {
                _hist.DangerousAddRef(ref added);
                long* h = (long*)_hist.Pointer + hist * HistogramBuckets;
                Interlocked.Increment(ref h[BucketOf(micros)]);
            }
            catch (ObjectDisposedException) { }
            finally { if (added) _hist.DangerousRelease(); }
        }

        private void Trip()
        {
            if (Interlocked.CompareExchange(ref _tripped, 1, 0) == 0) Volatile.Read(ref _link)?.Resync();
        }

        private long ToMicros(long ticks)
        {
            if (ticks <= 0) return 0;
            Int128 us = (Int128)ticks * 1_000_000 / _freq;
            return us > long.MaxValue ? long.MaxValue : (long)us;
        }

        private static long SatAdd(long a, long b) => a > long.MaxValue - b ? long.MaxValue : a + b;

        private static void AtomicMax(ref long target, long v)
        {
            long cur = Volatile.Read(ref target);
            while (v > cur)
            {
                long prev = Interlocked.CompareExchange(ref target, v, cur);
                if (prev == cur) return;
                cur = prev;
            }
        }

        public void Dispose() => _hist.Dispose();
    }
}
