// ============================================================================
// OrderPathStageProfiler — [드라이런 · 방향 A] 시그널 판정 → 라우터 게이트 승인 → 브로커 출구(송출 직전 veto 초크포인트) 구간별 지연 계측(δ=0)
//
//  왜 필요한가(정직): TickToTradeLatencyProfiler 는 '참조가 틱 나이 + 드레인 진입 → 송출 반환'을 잰다. 전략이 어떤 시그널을 보고 제안을
//   냈는지(시그널 → 제안 발행)는 링에 인과 타임스탬프가 없어 측정하지 않는다고 명시돼 있다. 이 계측기는 그 앞단을 채운다:
//    S0 SignalToGate  = 전략이 시그널을 읽고 제안을 낸 시각 → 라우터 게이트(확신 게이트 · 분할 · 추적 확보) 승인 반환
//    S1 GateToEgress  = 게이트 승인 → 첫 자식이 브로커 출구 초크포인트(LiveArmedEgressSentinel '앞')에 도달
//    S2 SignalToEgress = 시그널 → 마지막 자식의 출구 판정(전달/거부) 반환 — 분할 부모 전체가 출구를 통과하는 데 걸린 시간
//   출구 판정 결과(전달 · veto)를 별도 계수 — veto 도 '판정까지의 시간'은 의미가 있다(물리 키 미충족 시 거부까지 몇 µs 인가).
//
//  스탬프 표: 주문 ID & mask 슬롯(고정 배열 · 태그 = 주문 ID). 충돌(같은 슬롯에 다른 ID) = 덮어쓰기 + Evicted 계수 — 오래된 미완 주문은 버린다.
//  ★스레드 계약: BeginOrder = 제안 발행 스레드(단일 생산자 링과 같은 스레드) · MarkGate/MarkEgress = 드레인 단일 스레드. 드레인은 부모를 순차
//   처리하므로 '현재 부모'(MarkGate 가 기록)를 출구 데코레이터가 읽는다 — 다중 드레인 스레드에는 쓰지 않는다(OrderDrainWorker 계약과 동일).
//  히스토그램: 구간별 log2(ns) 64 버킷 · 백분위 = 버킷 상한(보수적 · 최대 2배 과대). 시계 = Stopwatch 단조 tick(주입 가능).
//  δ=0: BeginOrder · MarkGate · MarkEgress · 판독(Stage) 전부 힙 0 — 배열은 생성 시 1회.
// ============================================================================
using System;
using System.Diagnostics;
using System.Numerics;
using System.Threading;

namespace StockManager.Telemetry
{
    public enum OrderPathStage : byte { SignalToGate = 0, GateToEgress = 1, SignalToEgress = 2 }

    /// <summary>구간 분포(ns · 백분위는 log2 버킷 상한).</summary>
    public readonly record struct StageDistribution(long Count, long MeanNanos, long P50Nanos, long P99Nanos, long MaxNanos);

    public sealed class OrderPathStageProfiler
    {
        public const int Buckets = 64;
        private const int StageCount = 3;

        private readonly long[] _ids, _signalTs, _gateTs;
        private readonly int _mask;
        private readonly long[] _hist = new long[StageCount * Buckets];
        private readonly long[] _count = new long[StageCount], _sum = new long[StageCount], _max = new long[StageCount];
        private readonly Func<long> _now;
        private readonly long _freq;
        private long _activeId, _activeEgressSeen, _lastEgress;
        private long _begun, _evicted, _unknownGate, _egressForwarded, _egressVetoed, _egressOrphans;

        /// <param name="capacityPow2">동시 미완 주문 슬롯(2의 거듭제곱 ≥ 16).</param>
        /// <param name="nowTimestamp">단조 시계(기본 Stopwatch.GetTimestamp).</param>
        /// <param name="timestampFrequency">시계 주파수(기본 Stopwatch.Frequency).</param>
        public OrderPathStageProfiler(int capacityPow2 = 1024, Func<long>? nowTimestamp = null, long timestampFrequency = 0)
        {
            if (capacityPow2 < 16 || (capacityPow2 & (capacityPow2 - 1)) != 0) throw new ArgumentOutOfRangeException(nameof(capacityPow2));
            _ids = new long[capacityPow2]; _signalTs = new long[capacityPow2]; _gateTs = new long[capacityPow2];
            _mask = capacityPow2 - 1;
            _now = nowTimestamp ?? Stopwatch.GetTimestamp;
            _freq = timestampFrequency > 0 ? timestampFrequency : Stopwatch.Frequency;
        }

        public long Timestamp() => _now();
        public long Begun => Interlocked.Read(ref _begun);
        public long Evicted => Interlocked.Read(ref _evicted);
        public long UnknownGate => Interlocked.Read(ref _unknownGate);
        public long EgressForwarded => Interlocked.Read(ref _egressForwarded);
        public long EgressVetoed => Interlocked.Read(ref _egressVetoed);
        /// <summary>현재 부모 없이 출구에 도달한 송출(게이트 승인 스탬프 없음 — 계측 사슬 밖 경로).</summary>
        public long EgressOrphans => Interlocked.Read(ref _egressOrphans);

        /// <summary>제안 발행 직후 — 시그널 판정 시각(signalTimestamp)을 주문 ID 에 결속.</summary>
        public void BeginOrder(long orderId, long signalTimestamp)
        {
            if (orderId <= 0) return;
            int s = (int)(orderId & _mask);
            long prev = Volatile.Read(ref _ids[s]);
            if (prev != 0 && prev != orderId) Interlocked.Increment(ref _evicted);
            _signalTs[s] = signalTimestamp;
            _gateTs[s] = 0;
            Volatile.Write(ref _ids[s], orderId);
            Interlocked.Increment(ref _begun);
        }

        /// <summary>라우터 게이트 승인 반환 직후(드레인 스레드) — S0 기록 + 현재 부모 지정.</summary>
        public void MarkGate(long orderId)
        {
            long now = _now();
            int s = (int)(orderId & _mask);
            if (Volatile.Read(ref _ids[s]) != orderId) { Interlocked.Increment(ref _unknownGate); Volatile.Write(ref _activeId, 0); return; }
            _gateTs[s] = now;
            Record(OrderPathStage.SignalToGate, now - _signalTs[s]);
            Volatile.Write(ref _activeEgressSeen, 0);
            Volatile.Write(ref _activeId, orderId);
        }

        /// <summary>
        /// 출구 초크포인트 판정 반환 직후(드레인 스레드 · 자식마다). entryTimestamp = 출구 진입 시각(판정 전). 첫 자식이면 S1 기록 ·
        /// 마지막 판정 시각을 갱신(S2 는 부모당 1회 — EndOrder).
        /// </summary>
        public void MarkEgress(long entryTimestamp, bool forwarded)
        {
            long now = _now();
            if (forwarded) Interlocked.Increment(ref _egressForwarded); else Interlocked.Increment(ref _egressVetoed);
            long id = Volatile.Read(ref _activeId);
            if (id == 0) { Interlocked.Increment(ref _egressOrphans); return; }
            int s = (int)(id & _mask);
            if (Volatile.Read(ref _ids[s]) != id) { Interlocked.Increment(ref _egressOrphans); return; }
            if (Volatile.Read(ref _activeEgressSeen) == 0)
            {
                Volatile.Write(ref _activeEgressSeen, 1);
                Record(OrderPathStage.GateToEgress, entryTimestamp - _gateTs[s]);
            }
            _lastEgress = now;   // 마지막 자식의 출구 판정 시각 — S2 는 EndOrder 가 부모당 1회 확정
        }

        /// <summary>부모 처리 종료(드레인 스레드 · 게이트 데코레이터가 다음 부모 전 또는 사이클 끝에 호출) — S2 1회 기록 후 슬롯 해제.</summary>
        public void EndOrder()
        {
            long id = Volatile.Read(ref _activeId);
            if (id == 0) return;
            Volatile.Write(ref _activeId, 0);
            int s = (int)(id & _mask);
            if (Volatile.Read(ref _ids[s]) != id) return;
            if (Volatile.Read(ref _activeEgressSeen) == 1) Record(OrderPathStage.SignalToEgress, _lastEgress - _signalTs[s]);
            Volatile.Write(ref _ids[s], 0);
        }

        private void Record(OrderPathStage stage, long ticks)
        {
            int st = (int)stage;
            if (ticks < 0) ticks = 0;
            long ns = (long)((Int128)ticks * 1_000_000_000L / _freq);
            int b = ns <= 0 ? 0 : Math.Min(Buckets - 1, 64 - BitOperations.LeadingZeroCount((ulong)ns));
            Interlocked.Increment(ref _hist[st * Buckets + b]);
            Interlocked.Increment(ref _count[st]);
            Interlocked.Add(ref _sum[st], ns);
            long m;
            while (ns > (m = Interlocked.Read(ref _max[st])) && Interlocked.CompareExchange(ref _max[st], ns, m) != m) { }
        }

        /// <summary>구간 분포(δ=0 · 버킷 상한 = 2^b − 1 ns).</summary>
        public StageDistribution Stage(OrderPathStage stage)
        {
            int st = (int)stage;
            long n = Interlocked.Read(ref _count[st]);
            if (n == 0) return default;
            long p50 = 0, p99 = 0, acc = 0;
            long r50 = (n + 1) / 2, r99 = (n * 99 + 99) / 100;
            for (int b = 0; b < Buckets; b++)
            {
                acc += Interlocked.Read(ref _hist[st * Buckets + b]);
                long upper = b == 0 ? 0 : (b >= 63 ? long.MaxValue : (1L << b) - 1);
                if (p50 == 0 && acc >= r50) p50 = upper;
                if (acc >= r99) { p99 = upper; break; }
            }
            long max = Interlocked.Read(ref _max[st]);
            return new StageDistribution(n, Interlocked.Read(ref _sum[st]) / n, Math.Min(p50, max), Math.Min(p99, max), max);
        }
    }
}
