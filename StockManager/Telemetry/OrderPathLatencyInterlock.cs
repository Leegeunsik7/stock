// ============================================================================
// OrderPathLatencyInterlock — [v12.17.6 · 방향 B] 실발주 경로 심각 지연 자동 Disarm(비트 28 OrderPathLatency · sticky)
//
//  판정: 호스트 콜드 타이머(50ms · 자식 매트릭스 평가와 같은 주기)가 Evaluate() 를 부른다. 새 부모가 출구까지 도달한 회차(fresh)에만
//   OrderPathStageProfiler 최근 창(≤ 1,000 부모)의 시그널 → 출구 p99 를 LatencyHistogram(방향 A 엔진)으로 재계산하고,
//   p99 > 예산이 '연속 3회(fresh 판정 기준)' 이면 이상 병목으로 보고 Trip → 집계어 비트 28 Raise → 무장 술어 차단 · 송출 직전 veto(liveArmed:false).
//   예산 이하 fresh 판정 1회 = 연속 계수 0. 새 표본 없는 회차 = 판정 아님(계수 유지 — 오래된 창으로 연속을 쌓지 않는다).
//
//  ★예산 = 50ms + 드레인 주기(호스트 기본 50 + 50 = 100ms) — 오더의 '50ms' 를 그대로 쓰지 않은 이유(정직):
//   호스트 '시그널' 스탬프 = 링 발행 시각이고 OrderDrainWorker 는 주기(기본 50ms) 드레인이다. 따라서 정상 운용에서도 S2 에는 링 대기
//   0 ~ 50ms 가 균일하게 섞여 p99 ≈ 49.5ms + 처리 시간이 된다. 예산 50ms 그대로면 '설계된 대기'만으로 상시 연속 초과 → 정상 운용 중
//   영구 Disarm(fail-closed 이나 운영 결선 결함 — v12.17.x 에서 봉인 순서 결함과 같은 부류). 그래서 예산 = '병목 허용치 50ms' + 설계 대기(주기).
//   테스트가 두 경우를 모두 실측한다(주기 대기만 = 무격발 · 주기 + 60ms 정체 = 격발 · 예산 50ms 고정 시 주기 대기만으로 오격발).
//  표본 하한(기본 100): p99 순위가 최대값 1개로 정해지지 않도록(창 < 100 이면 p99 = 단일 JIT/콜드 이상치). 100 이상이면 p99 초과 = 최소 2건 지연.
//  재무장: TryRearm(물리 이중키 재확인) — 기준선(baseline)을 현재 누적 수로 옮겨 Trip 이전 표본은 다시 판정하지 않는다(재격발 루프 방지).
//  δ=0: Evaluate · ArmingBlocked 힙 0(버퍼 생성 시 1회). 판정은 드레인 스레드 밖(타이머) — 매매 핫패스 비용 0.
//  [v12.18.16-DEV] 2차 차단 입력(AttachFeedGuard · 1회): KIS 시세 시각 편차 감시(KisTimeSkewGuard)가 같은 비트 28 을 공유한다 — 집계어는 비트당
//   소스 1개만 결속(교체 금지)하므로 비트를 새로 결속하지 않고 이 소스의 차단 술어에 OR 로 합친다(인터락 30종 정의 · 마스크 불변).
//   차단 = 자체 Trip ∨ 2차 입력 차단(판독 예외 = 차단). 각 래치는 독립 — 자체 TryRearm 은 2차 래치를 풀지 않는다(비트는 둘 다 풀려야 내려감).
// ============================================================================
using System;
using System.Threading;
using MS2E.Core;

namespace StockManager.Telemetry
{
    public sealed class OrderPathLatencyInterlock : IArmingBlockSource
    {
        public const long DefaultBottleneckNanos = 50_000_000;                     // 50ms 병목 허용치
        public const int DefaultConsecutive = 3, DefaultMinSamples = 100;

        private readonly OrderPathStageProfiler _profiler;
        private readonly long _budgetNanos;
        private readonly int _consecutive, _minSamples;
        private readonly long[] _s0 = new long[OrderPathStageProfiler.RecentWindow], _s1 = new long[OrderPathStageProfiler.RecentWindow],
                                _s2 = new long[OrderPathStageProfiler.RecentWindow];
        private LatencyHistogram _h;
        private long _seen = -1, _baseline;
        private int _streak, _tripped;
        private long _evaluations, _judgements, _overBudget, _trips, _lastP99;
        private ArmingInterlockLink? _link;
        private IArmingBlockSource? _feed;

        /// <param name="profiler">실발주 경로 계측기(호스트 링 · 디스패처 · 게이트 · 출구 결선).</param>
        /// <param name="budgetNanos">p99 예산(ns). 호스트 = 50ms + 드레인 주기.</param>
        /// <param name="consecutive">연속 초과 판정 수(≥ 1).</param>
        /// <param name="minSamples">판정 표본 하한(≥ 1).</param>
        public OrderPathLatencyInterlock(OrderPathStageProfiler profiler, long budgetNanos = DefaultBottleneckNanos,
                                         int consecutive = DefaultConsecutive, int minSamples = DefaultMinSamples)
        {
            _profiler = profiler ?? throw new ArgumentNullException(nameof(profiler));
            if (budgetNanos <= 0) throw new ArgumentOutOfRangeException(nameof(budgetNanos));
            if (consecutive < 1) throw new ArgumentOutOfRangeException(nameof(consecutive));
            if (minSamples < 1 || minSamples > OrderPathStageProfiler.RecentWindow) throw new ArgumentOutOfRangeException(nameof(minSamples));
            _budgetNanos = budgetNanos; _consecutive = consecutive; _minSamples = minSamples;
        }

        /// <summary>호스트 예산 = 병목 허용치 + 드레인 주기(설계된 링 대기).</summary>
        public static long HostBudgetNanos(int drainPeriodMs, long bottleneckNanos = DefaultBottleneckNanos)
            => bottleneckNanos + Math.Max(0, drainPeriodMs) * 1_000_000L;

        public long BudgetNanos => _budgetNanos;
        public bool Tripped => Volatile.Read(ref _tripped) == 1;
        public bool ArmingBlocked => Tripped || FeedBlocked;

        /// <summary>[v12.18.16-DEV] 2차 입력(KIS 시세 편차) 차단 여부 — 미결선 = false · 판독 예외 = true(fail-closed).</summary>
        public bool FeedBlocked
        {
            get
            {
                var f = Volatile.Read(ref _feed);
                if (f is null) return false;
                try { return f.ArmingBlocked; }
                catch { return true; }
            }
        }

        /// <summary>[v12.18.16-DEV] 2차 차단 입력 결선(콜드 1회 · 교체 금지). 2차 입력은 상태 전이 직후 Resync() 를 호출한다.</summary>
        public void AttachFeedGuard(IArmingBlockSource feed)
        {
            ArgumentNullException.ThrowIfNull(feed);
            if (Interlocked.CompareExchange(ref _feed, feed, null) is not null)
                throw new InvalidOperationException("비트 28 2차 입력 이미 결선됨 — 교체 금지(fail-closed)");
            Volatile.Read(ref _link)?.Resync();
        }

        /// <summary>현재 차단 술어를 비트 28 에 반영(δ=0 · 미결속 = 무동작).</summary>
        public void Resync() => Volatile.Read(ref _link)?.Resync();
        public int Streak => Volatile.Read(ref _streak);
        public long Evaluations => Interlocked.Read(ref _evaluations);
        /// <summary>표본 하한을 충족한 fresh 판정 수.</summary>
        public long Judgements => Interlocked.Read(ref _judgements);
        public long OverBudget => Interlocked.Read(ref _overBudget);
        public long Trips => Interlocked.Read(ref _trips);
        public long LastP99Nanos => Interlocked.Read(ref _lastP99);

        public void BindInterlock(ArmingInterlockBitmask aggregator, ArmingInterlockSource bit)
        {
            var link = aggregator.Bind(bit, this);
            Volatile.Write(ref _link, link);
            link.Resync();
        }

        /// <summary>콜드 타이머 1회 판정(단일 호출자). 반환 = 이번 판정으로 Trip 했는가.</summary>
        public bool Evaluate()
        {
            Interlocked.Increment(ref _evaluations);
            if (Tripped) return false;
            long total = _profiler.RecentTotal;
            if (total == _seen) return false;                                       // 새 부모 0 = 판정 아님
            _seen = total;
            int n = _profiler.CopyRecent(_s0, _s1, _s2);
            int fresh = (int)Math.Min(n, Math.Max(0, total - _baseline));           // 재무장 이전 표본 제외(최신 쪽만)
            if (fresh < _minSamples) return false;
            _h.Load(_s2.AsSpan(n - fresh, fresh));
            long p99 = _h.Percentile(990);
            Interlocked.Exchange(ref _lastP99, p99);
            Interlocked.Increment(ref _judgements);
            if (p99 <= _budgetNanos) { Volatile.Write(ref _streak, 0); return false; }
            Interlocked.Increment(ref _overBudget);
            int st = _streak + 1;
            Volatile.Write(ref _streak, st);
            if (st < _consecutive) return false;
            if (Interlocked.CompareExchange(ref _tripped, 1, 0) != 0) return false;
            Interlocked.Increment(ref _trips);
            Volatile.Read(ref _link)?.Resync();                                     // ★비트 28 Raise — 무장 차단(liveArmed:false)
            return true;
        }

        /// <summary>재무장(물리 이중키 재확인 필수) — 연속 계수 0 · 기준선 = 현재 누적(이전 표본 재판정 0) · 비트 해제.</summary>
        public bool TryRearm(bool valveWired, bool liveArmingWired)
        {
            if (!(valveWired && liveArmingWired)) return false;
            _baseline = _profiler.RecentTotal;
            _seen = _baseline;
            Volatile.Write(ref _streak, 0);
            Volatile.Write(ref _tripped, 0);
            Volatile.Read(ref _link)?.Resync();
            return true;
        }
    }
}
