// ============================================================================
// QuantSignalProvider — [AI 전략 · 추천 A] 정량 시그널 엔진(호가 불균형 · 마이크로 추세 · VWAP 이격 · 변동성) + 신뢰도 게이트
//                       + StrategyAnomalyInterlock — [방향 B] 전략 산출 이상 즉시 Disarm(비트 27)
//
//  ★정직: 이 엔진은 '알파 모델'이 아니다 — 공개된 미시구조 지표를 결정론으로 합성한 규칙 기반 신호이며 백테스트 · 실거래 성과 검증 이력이
//    없다. 가중치 · 임계는 보수적 기본값(설정으로 조정). 목적은 ① 판단 근거를 관제(/aidash)에 실시간 노출 ② 확신 낮은 주문의 전방 거부.
//
//  입력(δ=0 · 임의 스레드): OnBook(10호가 스냅샷 · 원/주) · OnBookFrame(브리지 원시 바이트 ReadOnlySpan<byte>) · OnTrade(체결가 · 누적 거래량).
//  산출(종목별 고정 슬롯 · 단일 작성 잠금 + seqlock 판독):
//    imb5  = (Σ매수잔량1~5 − Σ매도잔량1~5) / 합 ∈ [−1, 1]
//    [방향 A] micro = EWMA(α 0.3)(Δimb10 × 10) — 체결(틱) 없이 호가 잔량만 기울 때 선행 신호(10호가 전체 불균형의 스냅샷 간 변화율).
//    vol   = √EWMA(λ 0.94)(ln(mid/mid₋₁)²) × 10⁴ bp(호가 갱신 간 중간가 수익률 변동성)
//    vwap  = Σ(체결가 × 체결량) / Σ체결량(세션) — 누적 거래량 증분 사용 · 누적 감소 = 새 거래일 → 세션 VWAP 리셋. 이격 = (mid − vwap)/vwap bp.
//    score = 0.6·imb5 + 0.4·micro · side = sign(score) · confidence = |score|
//      · 변동성 스파이크(vol > VolSpikeBp) = 확신 0(사유 VolatilitySpikeVeto) · VWAP 과이격 방향 추격(매수인데 mid ≫ vwap) = 확신 ½.
//  게이트(IQuantSignalGate · 라우터 전방 · δ=0): 확신 ≥ 0.85 ∧ 같은 방향 ∧ 신선(≤ MaxSignalAge) ∧ 이상 인터락 미격발일 때만 통과.
//    신호 없음/만료/확신 미달/방향 불일치 = 거부(fail-closed). 제안 수량 ≤ 0 · 가격 음수 = 전략 이상 → 비트 27 Disarm.
//  [방향 B] 산출값 NaN/∞ · 불균형 범위 이탈 · 1회 연산 > 10ms(StallMicros) = StrategyAnomalyInterlock sticky Disarm(재무장 = 물리 이중키).
//  관제: 판단 상태가 바뀌거나(방향/등급/사유) PublishIntervalMicros 경과 시에만 IDecisionSink(DashboardSnapshotHub)로 사출 —
//    호가마다 사출하면 관제 링(512)을 덮는다. 문자열 0(코드 · 수치만 — 텍스트는 UI 렌더 시 조립).
//  [v12.18.1-DEV] 변동성 적응 임계(VolatilityAdaptiveCalibrator · 선택 결속): 피드별 호가로 국면(추세/회귀/횡보/워밍업)을 정수 판독 →
//    종목 요구 확신 = max(고정 임계, 국면 임계 8500/9000/9500bp). 교차 모드 = 수신 피드 중 가장 높은 임계(보수). 고정 임계는 통과했지만
//    국면 임계에 미달한 신호 = 사유 RegimeThresholdNotMet(관망) · 게이트 거부 계수 RejectedRegime. 국면/임계가 바뀔 때만 PublishCalibration 사출.
//  [v12.18.2-DEV] 호스트 실배선: IOrderProposalObserver — GatedOrderDispatcher 드레인이 '무장 판정 이전'에 매 제안을 넘긴다. 게이트 꺼짐 ·
//    미무장에서도 수량 ≤ 0 = 비트 27 · 모델 승인 계수(승인 서킷 브레이커 → 하드 초과 = 비트 27) · 그림자 판정(꺼짐이면 '켰다면 거부' 계수)이
//    작동한다. 강제(SetGateEnforced true · 라우터 게이트 결속)일 때 AdmitSignal 은 같은 제안의 관측 판정(브레이커 판정 포함)을 재사용 —
//    이중 계수 0. 국면 임계 거부는 관제 판단 근거로 사출(강제 = 거부 · 관측 = 관망 · 100ms 간격 상한 — 관제 링 덮어쓰기 방지).
//    [방향 B] SignalChurnCircuitBreaker(선택 결속): 게이트가 승인한 시그널을 1분 창으로 세어 소프트 초과 = 억제 · 하드 초과 = 비트 27 Disarm.
// ============================================================================
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using MS2E.Core;
using MS2E.Infrastructure;

namespace StockManager.Engines
{
    /// <summary>판단 근거 사출 대상(DashboardSnapshotHub.PublishDecision 과 같은 시그니처 · δ=0).</summary>
    public interface IDecisionSink
    {
        void PublishDecision(int kind, long symbol, long scaleBp, long volatilityBp, long value, int reason);

        /// <summary>[v12.18.1-DEV] 종목 국면 · 요구 확신 변경 통지(변경 시에만 · δ=0). 기본 = 무시(기존 싱크 호환).</summary>
        void PublishCalibration(long symbol, int regime, int requiredBp) { }
    }

    /// <summary>라우터 전방 시그널 게이트(δ=0 · 예외 = 거부).</summary>
    public interface IQuantSignalGate
    {
        bool AdmitSignal(in OrderProposal proposal);
    }

    public enum QuantReason : int
    {
        None = 0, ImbalanceBuy = 1, ImbalanceSell = 2, MicroTrendBuy = 3, MicroTrendSell = 4, VwapStretch = 5,
        VolatilitySpikeVeto = 6, StaleSignal = 7, LowConfidence = 8, SideMismatch = 9, Anomaly = 10, NoSignal = 11,
        CrossedFeed = 12,     // [교차 피드] 키움 ↔ KIS 호가 꼬임(같은 호가창 — 한쪽 노후/오염)
        CrossDisagree = 13,   // [교차 피드] 두 피드 신호 방향 불일치
        SingleSource = 14,    // [교차 피드] 한 피드만 신선(다른 피드 미수신 · 시각 이탈) — 확신 ½
        CrossSkewVeto = 15,   // [교차 피드 · 방향 B] 두 피드 수신 시각 격차 > 하드 임계(기본 1초) = 동시성 붕괴 — 확신 0
        RegimeThresholdNotMet = 16,   // [v12.18.1-DEV] 고정 임계는 넘었지만 국면(횡보/회귀/워밍업) 상향 임계 미달 — 관망/전방 거부
    }

    public enum StrategyAnomalyKind : byte { None = 0, NonFinite = 1, OutOfRange = 2, NonPositiveQty = 3, ComputeStall = 4, CrossFeedCrossed = 5,
                                             ModelBreakdown = 6,    // [v12.18.1-DEV · 방향 B] 1분 승인 시그널 > 하드 한도(SignalChurnCircuitBreaker)
                                             AuditChainBroken = 7,  // [v12.18.2-DEV] 강화 전용 감사 원장 해시 체인 불일치 · 손상 · 열기 실패(MonotonicSettingAuditLog)
                                             AuditLedgerFull = 8 }  // [v12.18.2-DEV] 감사 원장 가득 참 — 기록 없는 설정 변경 금지 → 동결

    /// <summary>종목 시그널 판독본(seqlock 일관 사본).</summary>
    public readonly record struct QuantSignal(long SymbolCode, double Imbalance5, double MicroTrend, double VwapDeviationBp, double VolatilityBp,
                                              double Score, double Confidence, int Side, int Reason, long UpdatedMicros, long Books,
                                              long CrossSkewMicros = -1, int Sources = 0, long KiwoomBid = 0, long KiwoomAsk = 0, long KisBid = 0, long KisAsk = 0,
                                              int RequiredBp = 0, MicroRegime Regime = MicroRegime.Warmup);

    // ════════════════════════════════════════════════════════════════════════
    // [방향 B] 전략 이상 인터락
    // ════════════════════════════════════════════════════════════════════════
    public sealed class StrategyAnomalyInterlock : IArmingBlockSource
    {
        private int _tripped, _first;
        private long _trips, _nonFinite, _outOfRange, _nonPositive, _stalls;
        private ArmingInterlockLink? _link;

        public bool Tripped => Volatile.Read(ref _tripped) == 1;
        public bool ArmingBlocked => Tripped;
        public StrategyAnomalyKind FirstCause => (StrategyAnomalyKind)Volatile.Read(ref _first);
        public long Trips => Interlocked.Read(ref _trips);
        public long NonFinite => Interlocked.Read(ref _nonFinite);
        public long OutOfRange => Interlocked.Read(ref _outOfRange);
        public long NonPositive => Interlocked.Read(ref _nonPositive);
        public long Stalls => Interlocked.Read(ref _stalls);
        private long _crossed, _breakdown;
        public long CrossedFeeds => Interlocked.Read(ref _crossed);
        public long ModelBreakdowns => Interlocked.Read(ref _breakdown);
        private long _auditBroken, _auditFull;
        public long AuditChainBreaks => Interlocked.Read(ref _auditBroken);
        public long AuditLedgerFulls => Interlocked.Read(ref _auditFull);

        public void BindInterlock(ArmingInterlockBitmask aggregator, ArmingInterlockSource bit)
        {
            var link = aggregator.Bind(bit, this);
            Volatile.Write(ref _link, link);
            link.Resync();
        }

        /// <summary>이상 1건(δ=0 · 임의 스레드). 최초 원인만 각인 · 차단은 sticky.</summary>
        public void Trip(StrategyAnomalyKind kind)
        {
            switch (kind)
            {
                case StrategyAnomalyKind.NonFinite: Interlocked.Increment(ref _nonFinite); break;
                case StrategyAnomalyKind.OutOfRange: Interlocked.Increment(ref _outOfRange); break;
                case StrategyAnomalyKind.NonPositiveQty: Interlocked.Increment(ref _nonPositive); break;
                case StrategyAnomalyKind.ComputeStall: Interlocked.Increment(ref _stalls); break;
                case StrategyAnomalyKind.CrossFeedCrossed: Interlocked.Increment(ref _crossed); break;
                case StrategyAnomalyKind.ModelBreakdown: Interlocked.Increment(ref _breakdown); break;
                case StrategyAnomalyKind.AuditChainBroken: Interlocked.Increment(ref _auditBroken); break;
                case StrategyAnomalyKind.AuditLedgerFull: Interlocked.Increment(ref _auditFull); break;
            }
            if (Interlocked.CompareExchange(ref _tripped, 1, 0) != 0) return;
            Volatile.Write(ref _first, (int)kind);
            Interlocked.Increment(ref _trips);
            Volatile.Read(ref _link)?.Resync();                                     // ★원자 Disarm(비트 27)
        }

        public bool TryRearm(bool valveWired, bool liveArmingWired)
        {
            if (!(valveWired && liveArmingWired)) return false;
            Volatile.Write(ref _tripped, 0);
            Volatile.Write(ref _first, 0);
            Volatile.Read(ref _link)?.Resync();
            return true;
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    // 시그널 엔진
    // ════════════════════════════════════════════════════════════════════════
    public sealed class QuantSignalProvider : IQuantSignalGate, IOrderProposalObserver
    {
        public const double DefaultConfidenceThreshold = 0.85;
        public const long DefaultMaxSignalAgeMicros = 1_000_000;
        public const long DefaultStallMicros = 10_000;
        public const double DefaultVolSpikeBp = 30.0;
        public const double DefaultVwapStretchBp = 150.0;
        public const long DefaultPublishIntervalMicros = 250_000;
        private const double Lambda = 0.94, MicroAlpha = 0.3, ImbalanceWeight = 0.6, MicroWeight = 0.4;

        /// <summary>피드(키움/KIS)별 호가 상태 — 불균형 · 마이크로 추세 · 변동성은 피드마다 따로(두 피드 교대 갱신이 가짜 수익률을 만들지 않도록).</summary>
        private struct FeedState
        {
            public long Bid, Ask, RxMicros, Books;
            public double Imb5, Imb10Prev, Micro, Var, LastMid;
            public int HasImb10;
            public CalibrationState Cal;                                         // [v12.18.1-DEV] 피드별 국면(피드 교대가 가짜 수익률을 만들지 않게)
        }

        private struct Slot
        {
            public long Symbol, Seq, Books, UpdatedMicros, LastPublishMicros, LastCumVolume, CrossSkewMicros;
            public double Imb5, Micro, VwapNum, VwapDen, VwapDevBp, VolBp, Score, Conf;
            public int Lock, Side, Reason, LastPubKind, LastPubReason, LastPubSide, CrossedStreak, Sources;
            public int RequiredBp, LastPubRequiredBp;
            public MicroRegime Regime, LastPubRegime;
            public FeedState F0, F1;
        }

        private readonly Slot[] _slots;
        private readonly int _mask;
        private readonly StrategyAnomalyInterlock _anomaly;
        private readonly IDecisionSink? _sink;
        private readonly Func<long> _nowMicros;
        private readonly double _threshold, _volSpikeBp, _vwapStretchBp;
        private readonly long _maxAgeMicros, _stallMicros, _publishIntervalMicros;
        private long _books, _trades, _invalidBooks, _tableFull, _published, _admitted, _rejStale, _rejLow, _rejSide, _rejNone, _rejAnomaly;
        private readonly VolatilityAdaptiveCalibrator? _calibrator;
        private readonly SignalChurnCircuitBreaker? _churn;
        private long _rejRegime, _rejChurn, _calPublished;
        private int _floorBp;                                                         // [v12.18.3] 런타임 확신 하한(올리기만 · 0 = 없음)
        // [교차 피드] 키움 + KIS 호가 교차 확인
        public const long DefaultCrossMaxSkewMicros = 500_000;
        public const int DefaultCrossedStreakToTrip = 3;
        public const double SingleSourceDecay = 0.5;
        public const long DefaultCrossHardSkewMicros = 1_000_000;
        private readonly long _crossHardSkewMicros;
        private int _quarantine0, _quarantine1;
        private long _skewVetoes, _quarantinedInputs;
        public long CrossSkewVetoes => Interlocked.Read(ref _skewVetoes);
        public long QuarantinedInputs => Interlocked.Read(ref _quarantinedInputs);
        public bool IsFeedQuarantined(int feed) => feed == FeedKiwoom ? Volatile.Read(ref _quarantine0) == 1 : Volatile.Read(ref _quarantine1) == 1;

        /// <summary>[KIS 호가 경계 방어] 피드 격리/복귀(어댑터 연속 이탈 통지). 격리 피드 = 입력 무시 · 교차 판정에서 제외(→ 남은 피드 단일 출처 ½).</summary>
        public void SetFeedQuarantined(int feed, bool quarantined)
        {
            if (feed == FeedKiwoom) Volatile.Write(ref _quarantine0, quarantined ? 1 : 0);
            else if (feed == FeedKis) Volatile.Write(ref _quarantine1, quarantined ? 1 : 0);
        }
        private readonly bool _crossConfirm;
        private readonly long _crossMaxSkewMicros;
        private readonly int _crossedStreakToTrip;
        private long _kisBooks, _crossedBooks, _crossDisagree;
        public bool CrossConfirm => _crossConfirm;
        public long KisBooks => Interlocked.Read(ref _kisBooks);
        public long CrossedBooks => Interlocked.Read(ref _crossedBooks);
        public long CrossDisagreements => Interlocked.Read(ref _crossDisagree);

        public QuantSignalProvider(StrategyAnomalyInterlock anomaly, IDecisionSink? sink = null, Func<long>? nowMicros = null, int capacityPow2 = 512,
                                   double confidenceThreshold = DefaultConfidenceThreshold, long maxSignalAgeMicros = DefaultMaxSignalAgeMicros,
                                   long stallMicros = DefaultStallMicros, double volSpikeBp = DefaultVolSpikeBp, double vwapStretchBp = DefaultVwapStretchBp,
                                   long publishIntervalMicros = DefaultPublishIntervalMicros,
                                   bool crossConfirm = false, long crossMaxSkewMicros = DefaultCrossMaxSkewMicros, int crossedStreakToTrip = DefaultCrossedStreakToTrip,
                                   long crossHardSkewMicros = DefaultCrossHardSkewMicros,
                                   VolatilityAdaptiveCalibrator? calibrator = null, SignalChurnCircuitBreaker? churn = null)
        {
            _calibrator = calibrator; _churn = churn;
            if (crossHardSkewMicros < crossMaxSkewMicros) throw new ArgumentOutOfRangeException(nameof(crossHardSkewMicros), "하드 임계 ≥ 신선 임계");
            _crossHardSkewMicros = crossHardSkewMicros;
            if (crossMaxSkewMicros <= 0) throw new ArgumentOutOfRangeException(nameof(crossMaxSkewMicros));
            if (crossedStreakToTrip < 1) throw new ArgumentOutOfRangeException(nameof(crossedStreakToTrip));
            _crossConfirm = crossConfirm; _crossMaxSkewMicros = crossMaxSkewMicros; _crossedStreakToTrip = crossedStreakToTrip;
            if (capacityPow2 < 2 || (capacityPow2 & (capacityPow2 - 1)) != 0) throw new ArgumentOutOfRangeException(nameof(capacityPow2));
            if (!(confidenceThreshold > 0 && confidenceThreshold <= 1)) throw new ArgumentOutOfRangeException(nameof(confidenceThreshold));
            _anomaly = anomaly ?? throw new ArgumentNullException(nameof(anomaly));
            _sink = sink;
            _nowMicros = nowMicros ?? StockManager.Adapters.ReferencePriceFeedAdapter.MonotonicMicros;
            _slots = new Slot[capacityPow2]; _mask = capacityPow2 - 1;
            _threshold = confidenceThreshold; _maxAgeMicros = maxSignalAgeMicros; _stallMicros = stallMicros;
            _volSpikeBp = volSpikeBp; _vwapStretchBp = vwapStretchBp; _publishIntervalMicros = publishIntervalMicros;
        }

        public StrategyAnomalyInterlock Anomaly => _anomaly;
        public double ConfidenceThreshold => _threshold;
        public long Books => Interlocked.Read(ref _books);
        public long Trades => Interlocked.Read(ref _trades);
        public long InvalidBooks => Interlocked.Read(ref _invalidBooks);
        public long TableFull => Interlocked.Read(ref _tableFull);
        public long Published => Interlocked.Read(ref _published);
        public long Admitted => Interlocked.Read(ref _admitted);
        public long RejectedStale => Interlocked.Read(ref _rejStale);
        public long RejectedLowConfidence => Interlocked.Read(ref _rejLow);
        public long RejectedSideMismatch => Interlocked.Read(ref _rejSide);
        public long RejectedNoSignal => Interlocked.Read(ref _rejNone);
        public long RejectedAnomaly => Interlocked.Read(ref _rejAnomaly);
        /// <summary>고정 임계는 넘었지만 국면 상향 임계 미달로 게이트 거부(횡보 휩소 차단 실측).</summary>
        public long RejectedRegime => Interlocked.Read(ref _rejRegime);
        /// <summary>[방향 B] 승인됐으나 1분 승인 한도로 억제/붕괴 판정되어 거부.</summary>
        public long RejectedChurn => Interlocked.Read(ref _rejChurn);
        public long CalibrationsPublished => Interlocked.Read(ref _calPublished);
        public VolatilityAdaptiveCalibrator? Calibrator => _calibrator;
        /// <summary>[v12.18.3-DEV] 런타임 확신 하한(bp · 0 = 생성 시 임계만).</summary>
        public int ConfidenceFloorBp => Volatile.Read(ref _floorBp);

        /// <summary>
        /// [v12.18.3-DEV] 확신 하한 런타임 상향 — 올리기만(낮은 값 무시 · 상한 10,000). 게이트 · 관제 판정 = max(생성 임계, 하한, 국면 임계).
        /// 하향은 재기동으로만. 반환 = 상향했는가.
        /// </summary>
        public bool RaiseConfidenceFloorBp(int bp)
        {
            if (bp > 10_000) bp = 10_000;
            while (true)
            {
                int cur = Volatile.Read(ref _floorBp);
                if (bp <= cur) return false;
                if (Interlocked.CompareExchange(ref _floorBp, bp, cur) == cur) return true;
            }
        }
        public SignalChurnCircuitBreaker? Churn => _churn;

        // ── 입력 ──

        /// <summary>브리지 원시 프레임(OrderBookPayload 바이트) — 길이 미달 = 무효 계수.</summary>
        public void OnBookFrame(ReadOnlySpan<byte> payload)
        {
            if (payload.Length < Marshal.SizeOf<OrderBookPayload>()) { Interlocked.Increment(ref _invalidBooks); return; }
            var b = MemoryMarshal.Read<OrderBookPayload>(payload);
            OnBook(in b);
        }

        /// <summary>10호가 스냅샷 1건(키움 = 피드 0 · δ=0). 최우선 호가 결측/교차 · 잔량 0 = 무효(신호 갱신 없음 — 신선도 만료로 게이트 자연 차단).</summary>
        public void OnBook(in OrderBookPayload b) => OnBook(in b, FeedKiwoom);

        public const int FeedKiwoom = 0, FeedKis = 1;

        /// <summary>
        /// 10호가 스냅샷 1건 — feed 0 키움 · 1 KIS(H0STASP0). 교차 모드가 아니면 피드 0 만 신호를 만든다(피드 1 은 상태만 갱신).
        /// [교차 모드] 두 피드가 모두 신선(≤ CrossMaxSkew)하면: 피드 간 꼬임(한쪽 매도 &lt; 다른 쪽 매수 — 같은 KRX 호가창이므로 불가능 =
        /// 한쪽 노후/오염) = 확신 0 + 연속 CrossedStreakToTrip 회 = 전략 이상 Disarm · 방향 불일치 = 확신 0 · 일치 = min(|점수₀|, |점수₁|).
        /// 한 피드만 신선 = 단일 출처 확신 × ½(Zero Single-Source Bias · 시각 이탈 > 임계 포함).
        /// </summary>
        public void OnBook(in OrderBookPayload b, int feed)
        {
            long t0 = _nowMicros();
            if ((uint)feed > 1u) { Interlocked.Increment(ref _invalidBooks); return; }
            if (IsFeedQuarantined(feed)) { Interlocked.Increment(ref _quarantinedInputs); return; }   // 격리 피드 입력 = 상태 갱신 0
            long bestBid = b.BidPrice[0], bestAsk = b.AskPrice[0];
            if (b.SymbolCode <= 0 || bestBid <= 0 || bestAsk <= 0 || bestAsk <= bestBid) { Interlocked.Increment(ref _invalidBooks); return; }
            long bid5 = 0, ask5 = 0, bid10 = 0, ask10 = 0;
            for (int i = 0; i < 10; i++)
            {
                long bq = b.BidQty[i], aq = b.AskQty[i];
                if (bq < 0 || aq < 0) { Interlocked.Increment(ref _invalidBooks); return; }
                bid10 += bq; ask10 += aq;
                if (i < 5) { bid5 += bq; ask5 += aq; }
            }
            if (bid5 + ask5 == 0 || bid10 + ask10 == 0) { Interlocked.Increment(ref _invalidBooks); return; }
            int s = Acquire(b.SymbolCode);
            if (s < 0) { Interlocked.Increment(ref _tableFull); return; }
            ref var x = ref _slots[s];
            int kind = 0, reason = 0, side = 0, requiredBp = 0; long scaleBp = 0, volBpL = 0, value = 0; bool publish = false, publishCal = false, signal;
            MicroRegime regime = MicroRegime.Warmup;
            BeginWrite(ref x);
            try
            {
                ref var src = ref (feed == FeedKiwoom ? ref x.F0 : ref x.F1);
                double imb5 = (double)(bid5 - ask5) / (bid5 + ask5);
                double imb10 = (double)(bid10 - ask10) / (bid10 + ask10);
                if (src.HasImb10 != 0) src.Micro = (1 - MicroAlpha) * src.Micro + MicroAlpha * Math.Clamp((imb10 - src.Imb10Prev) * 10.0, -1.0, 1.0);
                src.Imb10Prev = imb10; src.HasImb10 = 1;
                double mid = (bestBid + bestAsk) * 0.5;
                if (src.LastMid > 0) { double r = Math.Log(mid / src.LastMid); src.Var = Lambda * src.Var + (1 - Lambda) * r * r; }   // 피드별 변동성(피드 교대 잡음 배제)
                src.LastMid = mid;
                src.Imb5 = imb5; src.Bid = bestBid; src.Ask = bestAsk;
                long nowRx = _nowMicros();
                src.RxMicros = nowRx == 0 ? 1 : nowRx;
                src.Books++;
                _calibrator?.Observe(ref src.Cal, bestBid, bestAsk, bid5, ask5);    // 정수 · δ=0 · 이 슬롯 작성 잠금 아래 단일 작성자
                signal = _crossConfirm || feed == FeedKiwoom;
                if (signal)
                {
                    double vol0 = Math.Sqrt(x.F0.Var) * 10_000.0, vol1 = Math.Sqrt(x.F1.Var) * 10_000.0;
                    x.VolBp = _crossConfirm ? Math.Max(vol0, vol1) : vol0;
                    x.VwapDevBp = x.VwapDen > 0 ? (mid - x.VwapNum / x.VwapDen) / (x.VwapNum / x.VwapDen) * 10_000.0 : 0.0;
                    double score, conf, micro;
                    double s0 = ImbalanceWeight * x.F0.Imb5 + MicroWeight * x.F0.Micro, s1 = ImbalanceWeight * x.F1.Imb5 + MicroWeight * x.F1.Micro;
                    if (!_crossConfirm)
                    {
                        score = s0; conf = Math.Abs(score); micro = x.F0.Micro; x.Imb5 = x.F0.Imb5;
                        side = score > 0 ? 1 : score < 0 ? -1 : 0;
                        reason = ReasonOf(x.Imb5, micro, side);
                    }
                    else
                    {
                        bool has0 = x.F0.RxMicros != 0 && !IsFeedQuarantined(FeedKiwoom), has1 = x.F1.RxMicros != 0 && !IsFeedQuarantined(FeedKis);
                        bool fresh0 = has0 && nowRx - x.F0.RxMicros <= _crossMaxSkewMicros;
                        bool fresh1 = has1 && nowRx - x.F1.RxMicros <= _crossMaxSkewMicros;
                        x.CrossSkewMicros = has0 && has1 ? Math.Abs(x.F0.RxMicros - x.F1.RxMicros) : -1;
                        x.Sources = (fresh0 ? 1 : 0) + (fresh1 ? 1 : 0);
                        if (x.CrossSkewMicros > _crossHardSkewMicros)
                        {
                            // ★[방향 B] 두 피드 모두 살아 있는데 수신 시각 격차 > 하드 임계 = 동시성 붕괴 → 교차 확신 원자적 0(격리된 피드는 제외 — 단일 출처 경로)
                            score = 0; conf = 0; side = 0; micro = 0; x.Imb5 = 0; reason = (int)QuantReason.CrossSkewVeto;
                            x.CrossedStreak = 0;
                            Interlocked.Increment(ref _skewVetoes);
                        }
                        else if (fresh0 && fresh1)
                        {
                            x.Imb5 = (x.F0.Imb5 + x.F1.Imb5) * 0.5; micro = (x.F0.Micro + x.F1.Micro) * 0.5;
                            if (x.F0.Ask < x.F1.Bid || x.F1.Ask < x.F0.Bid)
                            {
                                // ★[방향 B] 피드 간 꼬임 — 같은 호가창에서는 불가능(한쪽 노후/오염) → 확신 0 · 지속 = Disarm
                                score = 0; conf = 0; side = 0; reason = (int)QuantReason.CrossedFeed;
                                if (++x.CrossedStreak >= _crossedStreakToTrip) _anomaly.Trip(StrategyAnomalyKind.CrossFeedCrossed);
                                Interlocked.Increment(ref _crossedBooks);
                            }
                            else
                            {
                                x.CrossedStreak = 0;
                                int d0 = Math.Sign(s0), d1 = Math.Sign(s1);
                                if (d0 != 0 && d0 == d1) { score = (s0 + s1) * 0.5; conf = Math.Min(Math.Abs(s0), Math.Abs(s1)); side = d0; reason = ReasonOf(x.Imb5, micro, side); }
                                else { score = 0; conf = 0; side = 0; reason = (int)QuantReason.CrossDisagree; Interlocked.Increment(ref _crossDisagree); }
                            }
                        }
                        else if (fresh0 || fresh1)
                        {
                            // Zero Single-Source Bias — 다른 피드 미수신/노후(시각 이탈 > 임계) = 보수적 감쇄
                            score = fresh0 ? s0 : s1; micro = fresh0 ? x.F0.Micro : x.F1.Micro; x.Imb5 = fresh0 ? x.F0.Imb5 : x.F1.Imb5;
                            conf = Math.Abs(score) * SingleSourceDecay; side = score > 0 ? 1 : score < 0 ? -1 : 0;
                            reason = (int)QuantReason.SingleSource;
                            x.CrossedStreak = 0;
                        }
                        else { score = 0; conf = 0; side = 0; micro = 0; reason = (int)QuantReason.NoSignal; }
                    }
                    x.Micro = micro;
                    if (side * x.VwapDevBp > _vwapStretchBp && conf > 0) { conf *= 0.5; reason = (int)QuantReason.VwapStretch; }   // 이미 멀리 간 방향 추격
                    if (x.VolBp > _volSpikeBp) { conf = 0; reason = (int)QuantReason.VolatilitySpikeVeto; }
                    // [방향 B] 산출값 무결성
                    if (!double.IsFinite(x.Imb5) || !double.IsFinite(x.Micro) || !double.IsFinite(x.VolBp) || !double.IsFinite(x.VwapDevBp) || !double.IsFinite(score) || !double.IsFinite(conf))
                    { _anomaly.Trip(StrategyAnomalyKind.NonFinite); conf = 0; score = 0; side = 0; reason = (int)QuantReason.Anomaly; }
                    else if (Math.Abs(imb5) > 1.0 + 1e-12 || Math.Abs(imb10) > 1.0 + 1e-12 || conf > 1.0 + 1e-12)
                    { _anomaly.Trip(StrategyAnomalyKind.OutOfRange); conf = 0; reason = (int)QuantReason.Anomaly; }
                    // [v12.18.1-DEV] 국면 임계 — 교차 모드 = 수신 피드 중 최고(보수) · 캘리브레이터 미결속 = 고정 임계 그대로
                    double threshold = Math.Max(_threshold, Volatile.Read(ref _floorBp) / 10_000.0);
                    if (_calibrator is { } cal)
                    {
                        if (!_crossConfirm) { requiredBp = x.F0.Cal.RequiredBp; regime = x.F0.Cal.Regime; }
                        else
                        {
                            bool use0 = x.F0.Books > 0 && !IsFeedQuarantined(FeedKiwoom), use1 = x.F1.Books > 0 && !IsFeedQuarantined(FeedKis);
                            if (use0) { requiredBp = x.F0.Cal.RequiredBp; regime = x.F0.Cal.Regime; }
                            if (use1 && x.F1.Cal.RequiredBp > requiredBp) { requiredBp = x.F1.Cal.RequiredBp; regime = x.F1.Cal.Regime; }
                        }
                        if (requiredBp <= 0) { requiredBp = cal.MaxBp; regime = MicroRegime.Warmup; }   // 관측 없음 = 최대(fail-closed)
                        threshold = Math.Max(threshold, requiredBp / 10_000.0);
                        if (conf >= _threshold && conf < threshold && reason is not ((int)QuantReason.VolatilitySpikeVeto or (int)QuantReason.Anomaly or (int)QuantReason.CrossedFeed))
                            reason = (int)QuantReason.RegimeThresholdNotMet;
                    }
                    x.RequiredBp = requiredBp; x.Regime = regime;
                    x.Score = score; x.Conf = conf; x.Side = side; x.Reason = reason;
                    long now = _nowMicros();
                    x.UpdatedMicros = now == 0 ? 1 : now;
                    x.Books++;
                    kind = conf >= threshold ? (side > 0 ? 1 : 2) : reason is (int)QuantReason.VolatilitySpikeVeto or (int)QuantReason.Anomaly or (int)QuantReason.CrossedFeed ? 5 : 4;   // DashDecisionKind
                    publishCal = _calibrator is not null && (regime != x.LastPubRegime || requiredBp != x.LastPubRequiredBp);
                    if (publishCal) { x.LastPubRegime = regime; x.LastPubRequiredBp = requiredBp; }
                    publish = kind != x.LastPubKind || reason != x.LastPubReason || side != x.LastPubSide || now - x.LastPublishMicros >= _publishIntervalMicros;
                    if (publish) { x.LastPubKind = kind; x.LastPubReason = reason; x.LastPubSide = side; x.LastPublishMicros = now; }
                    scaleBp = double.IsFinite(conf) ? (long)(conf * 10_000) : 0;
                    volBpL = double.IsFinite(x.VolBp) ? (long)x.VolBp : 0;
                    value = (long)mid;
                    // [방향 B] 1회 연산 지연(> 10ms) = 낡은 판단 위 발주 위험 → Disarm
                    if (now - t0 > _stallMicros) { _anomaly.Trip(StrategyAnomalyKind.ComputeStall); x.Conf = 0; }
                }
            }
            finally { EndWrite(ref x); }
            if (feed == FeedKis) Interlocked.Increment(ref _kisBooks);
            if (!signal) return;
            Interlocked.Increment(ref _books);
            if (publish && _sink is { } sink)
            {
                try { sink.PublishDecision(kind, b.SymbolCode, scaleBp, volBpL, value, reason); Interlocked.Increment(ref _published); } catch (Exception) { }
            }
            if (publishCal && _sink is { } calSink)
            {
                try { calSink.PublishCalibration(b.SymbolCode, (int)regime, requiredBp); Interlocked.Increment(ref _calPublished); } catch (Exception) { }
            }
        }

        private static int ReasonOf(double imb5, double micro, int side)
            => Math.Abs(imb5) >= Math.Abs(micro) ? (side >= 0 ? (int)QuantReason.ImbalanceBuy : (int)QuantReason.ImbalanceSell)
                                                 : (side >= 0 ? (int)QuantReason.MicroTrendBuy : (int)QuantReason.MicroTrendSell);

        /// <summary>체결 1건(가격 원 · 누적 거래량). 증분 = 누적 − 직전 누적(감소 시 값 자체를 증분으로 — 일 리셋/건별 거래량 의미).</summary>
        public void OnTrade(long symbol, long priceWon, long cumulativeVolume)
        {
            if (symbol <= 0 || priceWon <= 0 || cumulativeVolume <= 0) return;
            int s = Acquire(symbol);
            if (s < 0) { Interlocked.Increment(ref _tableFull); return; }
            ref var x = ref _slots[s];
            BeginWrite(ref x);
            try
            {
                long inc;
                if (x.LastCumVolume == 0) inc = 0;                                  // 최초 1건은 기준선(누적 전체를 1틱 체결로 오인 방지)
                else if (cumulativeVolume >= x.LastCumVolume) inc = cumulativeVolume - x.LastCumVolume;
                else
                {
                    // ★[Long-Soak · 실결함 교정] 누적 거래량 감소 = 새 거래일(세션) — 수정 전엔 VWAP 누적을 이어 가 하루를 넘기면 '여러 날 평균'이 됐다.
                    x.VwapNum = 0; x.VwapDen = 0;
                    inc = cumulativeVolume;
                }
                x.LastCumVolume = cumulativeVolume;
                if (inc > 0) { x.VwapNum += (double)priceWon * inc; x.VwapDen += inc; }
            }
            finally { EndWrite(ref x); }
            Interlocked.Increment(ref _trades);
        }

        // ── 판독 · 게이트 ──

        public bool TryGetSignal(long symbol, out QuantSignal signal)
        {
            signal = default;
            int s = Find(symbol);
            if (s < 0) return false;
            ref var x = ref _slots[s];
            for (int spin = 0; spin < 64; spin++)
            {
                long s1 = Volatile.Read(ref x.Seq);
                if ((s1 & 1) != 0) { Thread.SpinWait(4); continue; }
                var copy = new QuantSignal(x.Symbol, x.Imb5, x.Micro, x.VwapDevBp, x.VolBp, x.Score, x.Conf, x.Side, x.Reason, x.UpdatedMicros, x.Books,
                                           x.CrossSkewMicros, x.Sources, x.F0.Bid, x.F0.Ask, x.F1.Bid, x.F1.Ask, x.RequiredBp, x.Regime);
                Interlocked.MemoryBarrier();
                if (Volatile.Read(ref x.Seq) == s1) { signal = copy; return copy.UpdatedMicros != 0; }
            }
            return false;
        }

        private enum Judgement : byte { Approve = 0, NoSignal = 1, Stale = 2, Low = 3, Regime = 4, Side = 5 }

        // 판정 코어(부작용 0 · δ=0) — 관측(OnProposal)과 강제(AdmitSignal)가 같은 규칙을 쓴다.
        private Judgement Judge(in OrderProposal p, out long confBp, out long volBp)
        {
            confBp = 0; volBp = 0;
            if (!TryGetSignal(p.SymbolCode, out var sig)) return Judgement.NoSignal;
            confBp = double.IsFinite(sig.Confidence) ? (long)(sig.Confidence * 10_000) : 0;
            volBp = double.IsFinite(sig.VolatilityBp) ? (long)sig.VolatilityBp : 0;
            if (_nowMicros() - sig.UpdatedMicros > _maxAgeMicros) return Judgement.Stale;
            if (!(sig.Confidence >= Math.Max(_threshold, Volatile.Read(ref _floorBp) / 10_000.0))) return Judgement.Low;   // NaN 도 거부 · 런타임 하한 포함
            if (_calibrator is { } cal)
            {
                // ★Zero Whipsaw Bleed — 국면 임계(관측 없음 = 최대) 미달 = 거부. 판독본의 RequiredBp 는 확신과 같은 seqlock 사본(같은 호가 기준).
                int req = sig.RequiredBp > 0 ? Math.Max(sig.RequiredBp, cal.ThresholdFor(sig.Regime)) : cal.MaxBp;   // 런타임 상향(RaiseMaxBp) 즉시 반영
                if (!(sig.Confidence >= req / 10_000.0)) return Judgement.Regime;
            }
            int want = p.SideByte == 0 ? 1 : -1;
            return sig.Side == want ? Judgement.Approve : Judgement.Side;
        }

        // ── [v12.18.2-DEV] 제안 관측(드레인 스레드 · 무장 판정 이전) ──
        private long _observed, _shadowApproved, _shadowRejects, _shadowRegime, _gateDecisions, _lastGateDecisionMicros = long.MinValue;
        private long _obsOrderId;
        private int _obsValid, _enforced;
        private ChurnVerdict _obsVerdict;
        public long ObservedProposals => Interlocked.Read(ref _observed);
        /// <summary>관측 판정 = 승인(브레이커 계수 대상).</summary>
        public long ShadowApproved => Interlocked.Read(ref _shadowApproved);
        /// <summary>관측 판정 = 거부(신호 없음 · 만료 · 확신/국면 미달 · 방향) — 게이트 꺼짐이면 '켰다면 거부됐을' 제안 수.</summary>
        public long ShadowRejects => Interlocked.Read(ref _shadowRejects);
        public long ShadowRegimeRejects => Interlocked.Read(ref _shadowRegime);
        /// <summary>국면 임계 거부로 관제에 사출한 판단 근거 수.</summary>
        public long GateDecisionsPublished => Interlocked.Read(ref _gateDecisions);
        /// <summary>라우터 게이트 강제 여부(호스트 SignalGateRuntimeSwitch 가 라우터와 함께 갱신 — 관제 사출 종류 결정용).</summary>
        public bool GateEnforced => Volatile.Read(ref _enforced) == 1;
        public void SetGateEnforced(bool enforced) => Volatile.Write(ref _enforced, enforced ? 1 : 0);

        /// <summary>
        /// 드레인 제안 관측(δ=0 · 단일 드레인 스레드). 차단하지 않는다 — 수량 ≤ 0 · 승인 폭주는 비트 27(인터락 차단)로 반영되고,
        /// 브레이커 판정은 같은 제안의 강제 판정(AdmitSignal)이 재사용한다.
        /// </summary>
        public void OnProposal(in OrderProposal p)
        {
            Interlocked.Increment(ref _observed);
            Volatile.Write(ref _obsValid, 0);
            if (p.QtyRaw <= 0 || p.PriceRaw < 0) { _anomaly.Trip(StrategyAnomalyKind.NonPositiveQty); Interlocked.Increment(ref _shadowRejects); return; }
            var j = Judge(in p, out long confBp, out long volBp);
            ChurnVerdict verdict = ChurnVerdict.Allowed;
            if (j == Judgement.Approve)
            {
                Interlocked.Increment(ref _shadowApproved);
                if (_churn is { } churn) verdict = churn.OnApproved(_nowMicros());       // ★하드 초과 = 비트 27(게이트 꺼짐 · 미무장에서도)
            }
            else
            {
                Interlocked.Increment(ref _shadowRejects);
                if (j == Judgement.Regime)
                {
                    Interlocked.Increment(ref _shadowRegime);
                    if (!GateEnforced) PublishGateDecision(p.SymbolCode, 4, confBp, volBp);   // 관측 = 관망(DashDecisionKind.Hold)
                }
            }
            _obsOrderId = p.OrderId; _obsVerdict = verdict;
            Volatile.Write(ref _obsValid, 1);
        }

        // 국면 임계 거부 판단 근거 사출 — 전 종목 공통 100ms 간격 상한(CAS · 실패 = 생략 · δ=0)
        private void PublishGateDecision(long symbol, int kind, long confBp, long volBp)
        {
            if (_sink is not { } sink) return;
            long now = _nowMicros(), last = Interlocked.Read(ref _lastGateDecisionMicros);
            if (last != long.MinValue && now - last < 100_000) return;
            if (Interlocked.CompareExchange(ref _lastGateDecisionMicros, now, last) != last) return;
            try { sink.PublishDecision(kind, symbol, confBp, volBp, 0, (int)QuantReason.RegimeThresholdNotMet); Interlocked.Increment(ref _gateDecisions); } catch (Exception) { }
        }

        /// <summary>
        /// 라우터 전방 게이트(δ=0): 이상 미격발 ∧ 수량 &gt; 0 ∧ 신호 신선 ∧ 확신 ≥ 임계(캘리브레이터 결속 시 max(고정, 국면 임계)) ∧ 같은 방향
        /// ∧ [방향 B] 1분 승인 한도 이내. 거부는 라우터 · 분할기 진입 '전'(예약 · 자식 슬롯 · 대사 원장 기입 0).
        /// 같은 제안을 드레인이 방금 관측했으면 그 브레이커 판정을 재사용(이중 계수 0) · 관측 없음(라우터 단독 사용) = 여기서 계수.
        /// </summary>
        public bool AdmitSignal(in OrderProposal p)
        {
            bool observed = Volatile.Read(ref _obsValid) == 1 && _obsOrderId == p.OrderId;
            if (p.QtyRaw <= 0 || p.PriceRaw < 0)
            {
                if (!observed) _anomaly.Trip(StrategyAnomalyKind.NonPositiveQty);
                Interlocked.Increment(ref _rejAnomaly); return false;
            }
            if (_anomaly.Tripped) { Interlocked.Increment(ref _rejAnomaly); return false; }
            switch (Judge(in p, out long confBp, out long volBp))
            {
                case Judgement.NoSignal: Interlocked.Increment(ref _rejNone); return false;
                case Judgement.Stale: Interlocked.Increment(ref _rejStale); return false;
                case Judgement.Low: Interlocked.Increment(ref _rejLow); return false;
                case Judgement.Regime:
                    Interlocked.Increment(ref _rejRegime);
                    PublishGateDecision(p.SymbolCode, 5, confBp, volBp);                    // 강제 = 거부(DashDecisionKind.Veto)
                    return false;
                case Judgement.Side: Interlocked.Increment(ref _rejSide); return false;
            }
            if (_churn is { } churn)
            {
                ChurnVerdict v = observed ? _obsVerdict : churn.OnApproved(_nowMicros());
                if (v != ChurnVerdict.Allowed) { Interlocked.Increment(ref _rejChurn); return false; }
            }
            Interlocked.Increment(ref _admitted);
            return true;
        }

        // ── 슬롯 ──

        private static int Hash(long symbol) => (int)((ulong)(symbol * unchecked((long)0x9E3779B97F4A7C15)) >> 40);

        private int Find(long symbol)
        {
            int h = Hash(symbol);
            for (int i = 0; i <= _mask; i++)
            {
                int s = (h + i) & _mask;
                long k = Volatile.Read(ref _slots[s].Symbol);
                if (k == symbol) return s;
                if (k == 0) return -1;
            }
            return -1;
        }

        private int Acquire(long symbol)
        {
            int h = Hash(symbol);
            for (int i = 0; i <= _mask; i++)
            {
                int s = (h + i) & _mask;
                long k = Volatile.Read(ref _slots[s].Symbol);
                if (k == symbol) return s;
                if (k == 0)
                {
                    long prev = Interlocked.CompareExchange(ref _slots[s].Symbol, symbol, 0);
                    if (prev == 0 || prev == symbol) return s;
                }
            }
            return -1;
        }

        // 슬롯 단위 작성 잠금(다중 피드 스레드) + seqlock(판독자 무대기 · 일관 사본)
        private static void BeginWrite(ref Slot x)
        {
            var sw = new SpinWait();
            while (Interlocked.CompareExchange(ref x.Lock, 1, 0) != 0) sw.SpinOnce(sleep1Threshold: -1);
            Interlocked.Increment(ref x.Seq);
        }

        private static void EndWrite(ref Slot x)
        {
            Interlocked.Increment(ref x.Seq);
            Volatile.Write(ref x.Lock, 0);
        }
    }
}
