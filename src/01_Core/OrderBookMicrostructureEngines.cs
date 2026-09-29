// ============================================================================
// OrderBookDepthMatrixCoalescer + SlippageForensicHeatmapRouter — v12.3.0 Think Beyond Gemini
//
//   위험 가정(제미나이의 '단순 결속'이 다루지 않는 시장 미시구조 파멸 리스크):
//     ① "L2/L3 10~20호가 스냅샷을 매 틱 관리형 배열로 재물성화" — 순진한 List<Level>/배열 재할당은 초당
//        50,000+ 갱신에서 Gen0 폭주 → GC 가 렌더링 프레임을 드롭시켜 '이미 늦은' 호가를 그리게 한다(미시구조
//        분석은 정의상 최신성이 생명). 또한 호가창은 체결 틱과 달리 ★'최신 상태' 만 유효 — 중간 스냅샷 이력은
//        렌더링에 무의미하므로 CrossBrokerAdaptiveRateCoalescer 류의 드레인-큐 모델은 부적합(과설계).
//     ② "체결 슬리피지 이상치가 특정 브로커/체결소에 편중돼도 전역 평균에 희석되어 원인 브로커를 특정 못 함" —
//        단일 스칼라 평균/임계값만으로는 '어느 노드가 이상인지' 를 모른 채 전체를 동일 취급 → 정상 브로커까지
//        페널티를 받거나, 이상 브로커가 계속 우선 라우팅되는 역선택(adverse selection)이 방치된다.
//
//   ── Think Beyond (이중 엔진) ─────────────────────────────────────────────
//     ① Zero-Alloc Unmanaged Order Book Depth Matrix Projector(Idea A) — Pinned Object Heap 링에 호가 매트릭스
//        (bid/ask price·qty × depth)를 seqlock 로 게시. 소비자는 '최신 슬롯만' O(1) Span 프로젝션으로 읽어 DOM
//        가상화 트리에 직접 투사(힙 0) + 동시에 호가 잔량 불균형비(ImbalanceRatioPpm)를 같은 패스에서 산출.
//     ② Real-Time Slippage Forensic Heatmap & Dynamic Penalty Router(Idea B) — 브로커별 슬리피지 표본을 POH
//        히스토그램(버킷)에 락프리 누적. 최상위(이상) 버킷 적중 시 해당 브로커 페널티를 원자적으로 가산(캡 有) →
//        RoutingPriorityPpm 이 즉시 하락해 상위 라우팅 계층이 이상 브로커를 자연 회피할 수 있게 신호를 노출한다.
//
//   불변: 결정론(주입 tick·순수 정수) · δ=0(Publish/TryProjectLatest/RecordSlippage 핫패스 힙 0) · fail-closed
//        (깊이 불일치=예외·범위밖 brokerId=무시·미발행 슬롯=false) · 락프리(Interlocked/Volatile/seqlock).
//   ※ MS2E.Core(최내부·순수) — 상위(MS2E.Infrastructure)가 ExecutionSlippageProfiler 로 결선한다.
// ============================================================================
using System;
using System.Threading;

namespace MS2E.Core
{
    // ════════════════════════════════════════════════════════════════════════
    //  Engine ① (Idea A) — Zero-Alloc Unmanaged Order Book Depth Matrix Projector
    // ════════════════════════════════════════════════════════════════════════
    /// <summary>
    /// 고정 깊이(depth) L2/L3 호가 매트릭스를 Pinned Object Heap 링에 seqlock 게시. 단일 심볼 전용 인스턴스
    /// (심볼별 인스턴스는 상위 어댑터가 구성) — Publish 는 다중 생산자 무차단, TryProjectLatest 는 항상 '최신
    /// 슬롯'만 O(1)·δ=0 프로젝션(중간 이력 드레인 없음 — 호가창은 최신 상태만 유효).
    /// </summary>
    public sealed class OrderBookDepthMatrixCoalescer
    {
        private const long WritingLock = long.MinValue;

        private readonly long[] _bidPx, _bidQty, _askPx, _askQty;   // Pinned: capacity*depth
        private readonly long[] _slotSeq;                            // capacity(-1=미발행)
        private readonly int _depth, _cap, _mask;
        private long _writeCursor;
        private long _publishedCount;

        public OrderBookDepthMatrixCoalescer(int depth, int capacityPow2)
        {
            if (depth <= 0) throw new ArgumentOutOfRangeException(nameof(depth));
            if (capacityPow2 < 2 || (capacityPow2 & (capacityPow2 - 1)) != 0)
                throw new ArgumentException("용량은 2의 거듭제곱(≥2)이어야 합니다.", nameof(capacityPow2));
            _depth = depth; _cap = capacityPow2; _mask = capacityPow2 - 1;
            int len = capacityPow2 * depth;
            _bidPx = GC.AllocateArray<long>(len, pinned: true);
            _bidQty = GC.AllocateArray<long>(len, pinned: true);
            _askPx = GC.AllocateArray<long>(len, pinned: true);
            _askQty = GC.AllocateArray<long>(len, pinned: true);
            _slotSeq = new long[capacityPow2];
            for (int i = 0; i < capacityPow2; i++) _slotSeq[i] = -1;
        }

        public int Depth => _depth;
        public int Capacity => _cap;
        public long Published => Volatile.Read(ref _publishedCount);

        /// <summary>
        /// 호가 매트릭스 1건 게시(★다중 생산자·비차단·δ=0). 각 span 길이는 반드시 depth 와 일치(불일치 시 예외 —
        /// fail-closed: 부분 갱신 대신 즉시 거부). seqlock: 쓰기잠금(MinValue) → 4개 배열 기입 → 시퀀스 발행(release).
        /// </summary>
        public long Publish(ReadOnlySpan<long> bidPx, ReadOnlySpan<long> bidQty, ReadOnlySpan<long> askPx, ReadOnlySpan<long> askQty)
        {
            if (bidPx.Length != _depth || bidQty.Length != _depth || askPx.Length != _depth || askQty.Length != _depth)
                throw new ArgumentException($"모든 span 길이는 depth({_depth})와 일치해야 합니다.");

            long w = Interlocked.Increment(ref _writeCursor) - 1;   // ★유일 단조 슬롯(생산자 무차단)
            int idx = (int)(w & _mask);
            int off = idx * _depth;
            Volatile.Write(ref _slotSeq[idx], WritingLock);          // 쓰기잠금(overwrite 중 torn-read 차단)
            bidPx.CopyTo(_bidPx.AsSpan(off, _depth));
            bidQty.CopyTo(_bidQty.AsSpan(off, _depth));
            askPx.CopyTo(_askPx.AsSpan(off, _depth));
            askQty.CopyTo(_askQty.AsSpan(off, _depth));
            Volatile.Write(ref _slotSeq[idx], w);                    // 발행(release)
            Interlocked.Increment(ref _publishedCount);
            return w;
        }

        /// <summary>
        /// ★최신 스냅샷을 호출자 제공 span 에 프로젝션(δ=0·O(1)) + 같은 패스에서 호가 잔량 불균형비(ppm,
        /// -1,000,000..+1,000,000·양수=매수 우위) 산출. seqlock 이중확인으로 진행 중 덮어쓰기(torn) 거부 —
        /// 거부/미발행 시 false(호출자는 직전 프레임 유지 — 렌더 프리즈 없이 다음 폴에서 재시도).
        /// </summary>
        public bool TryProjectLatest(Span<long> bidPxOut, Span<long> bidQtyOut, Span<long> askPxOut, Span<long> askQtyOut,
            out long sequence, out long imbalancePpm)
        {
            sequence = 0; imbalancePpm = 0;
            if (bidPxOut.Length != _depth || bidQtyOut.Length != _depth || askPxOut.Length != _depth || askQtyOut.Length != _depth)
                throw new ArgumentException($"모든 출력 span 길이는 depth({_depth})와 일치해야 합니다.");

            long w = Volatile.Read(ref _writeCursor);
            if (w == 0) return false;
            long target = w - 1;
            int idx = (int)(target & _mask);
            if (Volatile.Read(ref _slotSeq[idx]) != target) return false;   // 미발행/쓰기중/랩됨

            int off = idx * _depth;
            _bidPx.AsSpan(off, _depth).CopyTo(bidPxOut);
            _bidQty.AsSpan(off, _depth).CopyTo(bidQtyOut);
            _askPx.AsSpan(off, _depth).CopyTo(askPxOut);
            _askQty.AsSpan(off, _depth).CopyTo(askQtyOut);

            if (Volatile.Read(ref _slotSeq[idx]) != target) return false;   // ★사후 재검 — torn 판독 거부

            long sumBid = 0, sumAsk = 0;
            for (int i = 0; i < _depth; i++) { sumBid += bidQtyOut[i]; sumAsk += askQtyOut[i]; }
            long total = sumBid + sumAsk;
            imbalancePpm = total > 0 ? (sumBid - sumAsk) * 1_000_000 / total : 0;
            sequence = target;
            return true;
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Engine ② (Idea B) — Real-Time Slippage Forensic Heatmap & Dynamic Penalty Router
    // ════════════════════════════════════════════════════════════════════════
    /// <summary>
    /// 브로커별 슬리피지(bps) 분포를 POH 히스토그램(고정 버킷)에 락프리 누적. 최상위(이상) 버킷 적중이 누적되면
    /// 해당 브로커 페널티(ppm)를 원자적으로 가산(캡 1,000,000) — RoutingPriorityPpm 이 하락해 상위 라우팅
    /// 계층(예: LockFreeBrokerSwitchboard)이 소비할 수 있는 '회피 신호'를 노출한다(직접 배선은 상위 책임).
    /// </summary>
    public sealed class SlippageForensicHeatmapRouter
    {
        private const long PenaltyStepPpm = 10_000;   // 이상 버킷 1건당 +1%p
        private const long PenaltyCapPpm = 1_000_000;  // 100%

        private readonly long[] _histogram;   // Pinned: brokers*buckets
        private readonly long[] _penaltyPpm;
        private readonly long[] _sampleCount;
        private readonly int _brokers, _buckets;
        private readonly long _bucketWidthBps;

        public SlippageForensicHeatmapRouter(int brokerCount, int bucketCount, long bucketWidthBps)
        {
            if (brokerCount <= 0) throw new ArgumentOutOfRangeException(nameof(brokerCount));
            if (bucketCount <= 0) throw new ArgumentOutOfRangeException(nameof(bucketCount));
            _brokers = brokerCount; _buckets = bucketCount;
            _bucketWidthBps = bucketWidthBps > 0 ? bucketWidthBps : 1;
            _histogram = GC.AllocateArray<long>(brokerCount * bucketCount, pinned: true);
            _penaltyPpm = new long[brokerCount];
            _sampleCount = new long[brokerCount];
        }

        public int BrokerCount => _brokers;
        public int BucketCount => _buckets;

        /// <summary>슬리피지 표본 1건 반영(★락프리·δ=0). 범위 밖 brokerId 는 무시(fail-closed). 최상위 버킷 = 이상치.</summary>
        public void RecordSlippage(int brokerId, long slippageBps)
        {
            if ((uint)brokerId >= (uint)_brokers) return;
            int bucket = (int)(slippageBps / _bucketWidthBps);
            if (bucket < 0) bucket = 0;
            if (bucket >= _buckets) bucket = _buckets - 1;
            Interlocked.Increment(ref _histogram[brokerId * _buckets + bucket]);
            Interlocked.Increment(ref _sampleCount[brokerId]);

            if (bucket == _buckets - 1)   // 이상(최상위) 버킷 적중 → 페널티 원자 가산(캡, CAS 재시도로 확정 클램프)
            {
                long observed = Interlocked.Add(ref _penaltyPpm[brokerId], PenaltyStepPpm);
                while (observed > PenaltyCapPpm)
                {
                    long prior = Interlocked.CompareExchange(ref _penaltyPpm[brokerId], PenaltyCapPpm, observed);
                    if (prior == observed) break;   // 성공적으로 캡 고정
                    observed = prior;               // 경합 — 최신값으로 재시도(다른 스레드가 이미 캡 이하로 내렸으면 루프 종료)
                }
            }
        }

        public long BucketHits(int brokerId, int bucket)
            => (uint)brokerId < (uint)_brokers && (uint)bucket < (uint)_buckets
                ? Volatile.Read(ref _histogram[brokerId * _buckets + bucket]) : 0;

        public long SampleCount(int brokerId) => (uint)brokerId < (uint)_brokers ? Volatile.Read(ref _sampleCount[brokerId]) : 0;
        public long PenaltyPpm(int brokerId) => (uint)brokerId < (uint)_brokers ? Volatile.Read(ref _penaltyPpm[brokerId]) : 0;

        /// <summary>라우팅 우선순위(ppm, 클수록 우선). 페널티가 클수록 하락(1,000,000 만점에서 차감).</summary>
        public long RoutingPriorityPpm(int brokerId) => PenaltyCapPpm - PenaltyPpm(brokerId);

        public void ResetPenalty(int brokerId)
        {
            if ((uint)brokerId < (uint)_brokers) Interlocked.Exchange(ref _penaltyPpm[brokerId], 0);
        }
    }
}
