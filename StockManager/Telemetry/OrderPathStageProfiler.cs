// ============================================================================
// OrderPathStageProfiler — [실발주 경로 · v12.17.6] 시그널(제안 발행) → 라우터 게이트 승인 → 브로커 출구(송출 직전 veto 초크포인트) 구간별 지연 계측(δ=0)
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
//  [v12.17.5-DEV · 관제 결속] 최근 창(RecentWindow = 1,000 부모): EndOrder 가 부모당 S0 · S1 · S2(ns) 를 고정 링에 적재(드레인 단일 작성자 ·
//   wait-free — 점유 카운터 Interlocked.Exchange(전체 펜스) → 값 3개 기록 → 게시 카운터 Volatile.Write). CopyRecent = 대시보드 펌프(다른
//   스레드)의 무대기 판독 — 복사 뒤 점유 카운터를 읽어 그 사이 덮어써졌을(또는 작성 중인) 가장 오래된 표본만 잘라낸다(작성자 대기 0 ·
//   표시 전용 · 찢긴 표본 배제 · 작성 중이 아니면 창 1,000 전부 유효).
//  [v12.17.6 · 실발주 결선] IOrderPathObserver 구현 — 호스트 OrderDispatchRing 발행(OnPublished = BeginOrder · 시그널 시각 = 발행 시각)과
//   GatedOrderDispatcher 종결(OnDispatchEnd)이 수명 경계를 정확히 준다. 게이트/출구 스탬프는 OrderPathStamping 데코레이터(같은 네임스페이스).
//   ★호스트 '시그널' = 주문 제안 발행 시각(전략 메시 · 슬라이서 · 검문 진입로가 모두 링 단일 생산자 CAS 를 지난다). 따라서 S0 에는 링 대기
//   (드레인 주기 ≤ 50ms)가 포함된다 — 운용자가 실제로 겪는 지연이며 숨기지 않는다.
//  [v12.17.6 · 방향 A] 무할당 상관 슬롯 즉시 회수: 종결(OnDispatchEnd) 시 그 주문 슬롯을 CAS(id → 0)로 비운다 — 미무장 차단 · 라우터 거부 ·
//   카나리/인터락 veto 등 출구 미도달 주문이 슬롯을 점유한 채 남아 1,024 주문 뒤 '축출'로 오계수되던 누수를 제거한다. CAS 인 이유: 생산자가
//   같은 슬롯에 새 주문(id + 1,024)을 막 기록했다면 회수가 그것을 지우면 안 된다(태그 불일치 = 회수 무시). 배열은 생성 시 1회 · 각 8KB(LOH 미만).
//   축출(Evicted) = 이제 '진짜로 살아 있는 주문이 덮어써짐'(미종결 > 슬롯 수)만 뜻한다 · Outstanding = 발행 − 완료 − 회수 − 축출.
// ============================================================================
using System;
using System.Diagnostics;
using System.Numerics;
using System.Threading;
using MS2E.Infrastructure;

namespace StockManager.Telemetry
{
    public enum OrderPathStage : byte { SignalToGate = 0, GateToEgress = 1, SignalToEgress = 2 }

    /// <summary>구간 분포(ns · 백분위는 log2 버킷 상한).</summary>
    public readonly record struct StageDistribution(long Count, long MeanNanos, long P50Nanos, long P99Nanos, long MaxNanos);

    public sealed class OrderPathStageProfiler : IOrderPathObserver
    {
        public const int Buckets = 64;
        private const int StageCount = 3;

        private readonly long[] _ids, _signalTs, _gateTs;
        private readonly int _mask;
        private readonly long[] _hist = new long[StageCount * Buckets];
        private readonly long[] _count = new long[StageCount], _sum = new long[StageCount], _max = new long[StageCount];
        private readonly Func<long> _now;
        private readonly long _freq;
        private readonly long _nsPerTick, _fastLimit;   // [v12.17.6] 1e9 % freq == 0(Windows QPC 10MHz = 100) → 정확 정수 곱(Int128 나눗셈 회피)
        private long _activeId, _activeEgressSeen, _lastEgress, _activeFirstEgress;
        public const int RecentWindow = 1000;
        private readonly long[] _recent0 = new long[RecentWindow], _recent1 = new long[RecentWindow], _recent2 = new long[RecentWindow];
        private long _recentN, _recentClaim;
        private long _begun, _evicted, _unknownGate, _egressForwarded, _egressVetoed, _egressOrphans;
        private long _completed, _reclaimed;

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
            _nsPerTick = 1_000_000_000L % _freq == 0 ? 1_000_000_000L / _freq : 0;
            _fastLimit = _nsPerTick > 0 ? long.MaxValue / _nsPerTick : 0;
        }

        public long Timestamp() => _now();
        public long Begun => Interlocked.Read(ref _begun);
        public long Evicted => Interlocked.Read(ref _evicted);
        public long UnknownGate => Interlocked.Read(ref _unknownGate);
        public long EgressForwarded => Interlocked.Read(ref _egressForwarded);
        public long EgressVetoed => Interlocked.Read(ref _egressVetoed);
        /// <summary>현재 부모 없이 출구에 도달한 송출(게이트 승인 스탬프 없음 — 계측 사슬 밖 경로).</summary>
        public long EgressOrphans => Interlocked.Read(ref _egressOrphans);
        /// <summary>[v12.17.6] 출구 판정까지 도달해 S2 가 기록된 부모 수.</summary>
        public long Completed => Interlocked.Read(ref _completed);
        /// <summary>[방향 A] 출구 미도달로 종결돼 슬롯이 즉시 회수된 부모 수(미무장 차단 · 게이트 거부 · 하류 veto).</summary>
        public long Reclaimed => Interlocked.Read(ref _reclaimed);
        /// <summary>미종결 부모(발행 − 완료 − 회수 − 축출 · 0 미만 = 0).</summary>
        public long Outstanding => Math.Max(0, Begun - Completed - Reclaimed - Evicted);

        // ── [v12.17.6] IOrderPathObserver(호스트 링 · 디스패처 결선) ──

        /// <summary>제안 발행(생산자 스레드) = 시그널 시각 스탬프.</summary>
        public void OnPublished(long orderId) => BeginOrder(orderId, _now());

        /// <summary>
        /// 처분 종결(드레인 스레드 · 정확히 1회). 게이트를 지난 현재 부모면 EndOrder(S2 · 창 적재 · 슬롯 회수), 아니면 슬롯 즉시 회수(Abandon).
        /// </summary>
        public void OnDispatchEnd(long orderId, bool forwarded)
        {
            if (orderId <= 0) return;
            if (Volatile.Read(ref _activeId) == orderId) EndOrder();
            else Abandon(orderId);
        }

        /// <summary>[방향 A] 출구 미도달 주문의 상관 슬롯 즉시 회수(CAS · 태그 불일치 = 무시 · 멱등). 반환 = 회수 여부.</summary>
        public bool Abandon(long orderId)
        {
            if (orderId <= 0) return false;
            if (Volatile.Read(ref _activeId) == orderId) Volatile.Write(ref _activeId, 0);
            int s = (int)(orderId & _mask);
            if (Interlocked.CompareExchange(ref _ids[s], 0, orderId) != orderId) return false;
            Interlocked.Increment(ref _reclaimed);
            return true;
        }

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
                _activeFirstEgress = entryTimestamp;
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
            if (Volatile.Read(ref _activeEgressSeen) == 1)
            {
                long sig = _signalTs[s], gate = _gateTs[s];
                Record(OrderPathStage.SignalToEgress, _lastEgress - sig);
                long n = _recentN;                                                  // 단일 작성자(드레인) — 점유 → 값 기록 → 카운터 게시
                Interlocked.Exchange(ref _recentClaim, n + 1);                      // 전체 펜스: 판독자가 '작성 중'을 값보다 먼저 본다
                int slot = (int)(n % RecentWindow);
                _recent0[slot] = ToNanos(gate - sig);
                _recent1[slot] = ToNanos(_activeFirstEgress - gate);
                _recent2[slot] = ToNanos(_lastEgress - sig);
                Volatile.Write(ref _recentN, n + 1);
                if (Interlocked.CompareExchange(ref _ids[s], 0, id) == id) Interlocked.Increment(ref _completed);
                return;
            }
            // 게이트는 지났으나 출구 미도달(지연 정지 veto · 브로커 인덱스 veto 등) = 회수
            if (Interlocked.CompareExchange(ref _ids[s], 0, id) == id) Interlocked.Increment(ref _reclaimed);
        }

        /// <summary>최근 창에 적재된 누적 부모 수(창 크기 무관 · 단조 증가).</summary>
        public long RecentTotal => Volatile.Read(ref _recentN);

        /// <summary>
        /// 최근 부모 구간 지연(ns · 과거 → 최신) 복사(δ=0 · 작성자 대기 0). 반환 = 유효 표본 수(≤ RecentWindow · ≤ 가장 짧은 대상 길이).
        /// 판독 도중 작성자가 링을 돌아 덮어썼을 수 있는 가장 오래된 표본은 잘라낸다(앞쪽 시프트) — 남은 표본은 전부 온전한 부모 1건씩.
        /// </summary>
        public int CopyRecent(Span<long> signalToGate, Span<long> gateToEgress, Span<long> signalToEgress)
        {
            int cap = Math.Min(RecentWindow, Math.Min(signalToGate.Length, Math.Min(gateToEgress.Length, signalToEgress.Length)));
            long n1 = Volatile.Read(ref _recentN);
            int count = (int)Math.Min(n1, cap);
            long start = n1 - count;
            for (int k = 0; k < count; k++)
            {
                int slot = (int)((start + k) % RecentWindow);
                signalToGate[k] = _recent0[slot]; gateToEgress[k] = _recent1[slot]; signalToEgress[k] = _recent2[slot];
            }
            Interlocked.MemoryBarrier();
            long claimed = Volatile.Read(ref _recentClaim);                        // 위치 < claimed 는 작성 시작됨 → 슬롯 재사용 = 위치 < claimed − W 불신
            long firstSafe = claimed - RecentWindow;
            if (start >= firstSafe) return count;
            int drop = (int)Math.Min(count, firstSafe - start);
            int keep = count - drop;
            signalToGate.Slice(drop, keep).CopyTo(signalToGate);
            gateToEgress.Slice(drop, keep).CopyTo(gateToEgress);
            signalToEgress.Slice(drop, keep).CopyTo(signalToEgress);
            return keep;
        }

        // 핫패스(부모당 6회): 주파수가 1e9 의 약수면 정확한 정수 곱 · 아니면 Int128(정확 · 느림 ~수십 ns)
        private long ToNanos(long ticks)
            => ticks <= 0 ? 0 : ticks < _fastLimit ? ticks * _nsPerTick : (long)((Int128)ticks * 1_000_000_000L / _freq);

        private void Record(OrderPathStage stage, long ticks)
        {
            int st = (int)stage;
            long ns = ToNanos(ticks);
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
