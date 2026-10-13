// ============================================================================
// MultiBrokerDryRunHarness — [드라이런 · 추천 A] 키움 + KIS 2 브로커 분할 라우팅 · 시그널 게이트 · 불균형 차단기 · 송출 센티널 통합 모의 가동
//
//  ★정직(전제): 실 키움 COM(x86 프록시 · OpenAPI+ 로그인 · 장중)과 KIS VTS(App Key/Secret OAuth2 · 웹소켓)는 이 개발 호스트 · CI 에서 연결할 수
//   없다(자격증명 · 설치 · 장 시간). 실 세션 '결속'을 흉내 내는 가짜 배선은 만들지 않는다. 대신 호스트 BlazorHomeForm 이 실전에서 쓰는 '바로 그'
//   객체 사슬을 같은 순서로 조립하고 IBroker 자리에만 가상 브로커 2개(키움 주문번호 공간 · KIS 주문번호 공간 KisOrderIdSpace)를 꽂는다:
//    10호가(키움 피드) → QuantSignalProvider(확신 게이트 0.85) + L2 깊이(DepthBook) · 참조가 → OrderDispatchRing → GatedOrderDispatcher
//    → OrderDrainWorker(인터락 veto → RouterFeedGate[시그널 게이트 → ChildSlotOrderSplitter 1~3호가 × 브로커 한도 → 자식 라우팅/추적]
//      → TickToTradeLatencyProfiler 송출 직전 veto → 자식 순차 송출) → [브로커 b] LiveArmedEgressSentinel(물리 이중키 ∧ 집계어) → 가상 브로커 b
//    체결 통보 → OrderExecutionFeedAdapter + ChildSlotTrackingMatrix(불균형 차단기 · 취소 → 센티널 → 가상 브로커).
//   호스트와 다른 점(명시): ① 브로커 = 가상(네트워크 0 · IsLive=false) ② 한도 피드 = 고정값(BrokerLimitFeedProvider 대신 · 테스트가 조절)
//   ③ 게이트 승인 · 출구 진입 스탬프 = 호스트와 같은 StockManager.Telemetry 데코레이터 2종(v12.17.6 에 프로덕션 네임스페이스로 이전 ·
//      동작 위임만 · 판정 변경 0) + 링 경로 관측자(발행 · 종결 — 호스트와 동일 결선).
//
//  무장 모델(liveArmed:false 가 기본):
//   · Valve / LiveArming = 물리 이중키 모사(기본 false). 디스패처 무장 술어 = 둘 다 true 일 때만.
//   · UpstreamFault = '상류 게이트 전부 오작동' 주입 — 디스패처 술어만 강제 true(센티널 술어는 여전히 실 이중키). 이 상태에서 이중키가 꺼져 있으면
//     자식 송출이 센티널에 도달해도 가상 브로커 전달 0 + EgressBypass sticky Disarm + 동결 콜백이어야 한다(테스트가 증명).
//   · '승인된 송출'의 정의 = 가상 브로커 Place 도달 시점에 이중키 ∧ 집계어 무차단 ∧ 비동결. 그 밖의 도달 = UnsanctionedPlaces(0 이어야 함).
//  δ=0: 호가 · 시그널 · 제안 발행 · 드레인(게이트 · 분할 · 자식 송출 · 센티널) · 가상 체결 · 대사 · 매트릭스 평가가 정상 상태에서 관리 힙 0B
//   (KseSymbolText 캐시 적중 · 가상 주문번호 문자열 사전 할당 기준). 불균형 취소 콜백의 주문번호 문자열 변환은 콜드(취소 시 1회).
// ============================================================================
using System;
using System.Diagnostics;
using System.Threading;
using MS2E.Core;
using MS2E.Infrastructure;
using StockManager.Adapters;
using StockManager.Engines;
using StockManager.Telemetry;

namespace StockManager.Simulation
{
    /// <summary>
    /// 드라이런 가상 브로커 — 주문번호 공간 지정(키움 = 일반 양수 · KIS = KisOrderIdSpace) · 체결 보류(불균형 리허설) · 취소 기록. 네트워크 0.
    /// Place 시점의 '승인 여부'를 술어로 판정해 미승인 도달을 계수한다(센티널 우회 탐지의 독립 관측점).
    /// </summary>
    public sealed class DryRunVirtualBroker : IBroker
    {
        private struct Pending { public long BrokerId, Symbol, Qty, Price; public int Side; public bool Live; }

        private readonly string[] _idText;
        private readonly long[] _idValue;
        private readonly Pending[] _book;
        private readonly Func<bool> _sanctioned;
        private int _next;
        private long _placed, _unsanctioned, _cancels, _filled, _shares;

        public DryRunVirtualBroker(string name, int index, Func<long, long> idSpace, Func<bool> sanctioned, int capacity = 1024)
        {
            Name = name; Index = index;
            _sanctioned = sanctioned ?? throw new ArgumentNullException(nameof(sanctioned));
            _idText = new string[capacity]; _idValue = new long[capacity]; _book = new Pending[capacity];
            for (int i = 0; i < capacity; i++)
            {
                _idValue[i] = idSpace(i + 1);
                _idText[i] = _idValue[i].ToString(System.Globalization.CultureInfo.InvariantCulture);   // 사전 할당(Place δ=0)
            }
        }

        public string Name { get; }
        public int Index { get; }
        public bool IsLive => false;
        public bool HoldFills { get; set; }
        public long Placed => Interlocked.Read(ref _placed);
        public long UnsanctionedPlaces => Interlocked.Read(ref _unsanctioned);
        public long Cancels => Interlocked.Read(ref _cancels);
        public long Filled => Interlocked.Read(ref _filled);
        public long SharesPlaced => Interlocked.Read(ref _shares);
        /// <summary>Place 성공 직후 훅(리허설 — 예: 형제 송출 사이 인터락 격발 주입).</summary>
        public Action? AfterPlace { get; set; }

        public (bool ok, string msg) Place(PaperAsset asset, string symbol, string name, PaperSide side, PaperKind kind, decimal qty, decimal limitPrice, decimal currentPrice)
        {
            bool ok;
            try { ok = _sanctioned(); } catch { ok = false; }
            if (!ok) Interlocked.Increment(ref _unsanctioned);   // 독립 관측: 승인 없이 브로커까지 왔다(이 시점 = 이미 유출)
            int slot = _next % _book.Length;   // 링(주문번호 재사용 = 이전 주문 종결 후만 — 미종결이면 포화 거부)
            if (!long.TryParse(symbol, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out long code) || qty <= 0 || _book[slot].Live)
                return (false, "가상 브로커 거부(형식/포화)");
            _next++;
            _book[slot] = new Pending { BrokerId = _idValue[slot], Symbol = code, Qty = (long)qty, Price = (long)limitPrice, Side = side == PaperSide.Buy ? 1 : -1, Live = true };
            Interlocked.Increment(ref _placed);
            Interlocked.Add(ref _shares, (long)qty);
            AfterPlace?.Invoke();
            return (true, _idText[slot]);
        }

        /// <summary>보류 해제된 미체결 전량을 체결 통보(전량 · 잔량 0). HoldFills 이면 0.</summary>
        public int DeliverFills(Action<FillPayload> sink, long nowTicks)
        {
            if (HoldFills) return 0;
            int n = 0;
            for (int i = 0; i < _book.Length; i++)
            {
                ref var p = ref _book[i];
                if (!p.Live) continue;
                p.Live = false;
                sink(new FillPayload
                {
                    OrderId = p.BrokerId, SymbolCode = p.Symbol, FillPrice = p.Price, FillQty = p.Qty, Side = p.Side, TimestampTicks = nowTicks,
                    Status = BridgeFillStatusCodec.Encode(BridgeFillStatus.Filled, true), RemainingQty = 0,
                });
                Interlocked.Increment(ref _filled);
                n++;
            }
            return n;
        }

        /// <summary>취소 = 미체결이면 취소 확인 통보 대기열로(다음 DeliverCancels) · 이미 체결 = 무시.</summary>
        public void Cancel(string orderId)
        {
            Interlocked.Increment(ref _cancels);
            if (!long.TryParse(orderId, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out long id)) return;
            for (int i = 0; i < _book.Length; i++)
                if (_book[i].BrokerId == id && _book[i].Live) { _book[i].Live = false; _cancelled[_cancelCount++ % _cancelled.Length] = id; return; }
        }

        private readonly long[] _cancelled = new long[256];
        private int _cancelCount, _cancelDelivered;

        public int DeliverCancels(Action<FillPayload> sink, long nowTicks)
        {
            int n = 0;
            while (_cancelDelivered < _cancelCount)
            {
                long id = _cancelled[_cancelDelivered++ % _cancelled.Length];
                sink(new FillPayload { OrderId = id, TimestampTicks = nowTicks, Status = BridgeFillStatusCodec.Encode(BridgeFillStatus.CancelConfirmed, true) });
                n++;
            }
            return n;
        }

        public decimal HeldQty(PaperAsset asset, string symbol) => 0m;
    }

    public sealed class MultiBrokerDryRunHarness : IDisposable
    {
        public const int KiwoomBroker = 0, KisBroker = 1;
        public const long RawScale = 100_000_000L;
        private const long Tick = 100;

        private sealed class AdjustableLimitFeed : IBrokerLimitFeed
        {
            public long Kiwoom = 1_000, Kis = 1_000;
            public bool KiwoomUp = true, KisUp = true;
            public int Fill(Span<BrokerSplitCap> dst, long symbolCode, OrderSide side, long limitPriceRaw, long parentReservedWon)
            {
                dst[0] = new BrokerSplitCap { Broker = KiwoomBroker, MaxShares = Kiwoom, Available = KiwoomUp };
                dst[1] = new BrokerSplitCap { Broker = KisBroker, MaxShares = Kis, Available = KisUp };
                return 2;
            }
        }

        private long _now = 1_000_000;
        private volatile bool _valve, _liveArming, _upstreamFault;
        private long _seq, _freezes;
        private readonly AdjustableLimitFeed _caps = new();
        private readonly ReferencePriceFeedAdapter _price;
        private readonly FeedDisconnectCircuitBreaker _feedBreaker;
        private readonly DepthBook _depth;
        private readonly OrderExecutionFeedAdapter _exec;
        private readonly OrderDispatchRing _ring;
        private readonly Action<FillPayload> _onFill;
        private readonly IBroker?[] _brokerMap;

        public OrderAttestationService Attestation { get; } = new();
        public ArmingInterlockBitmask Interlocks { get; }
        public QuantSignalProvider Signals { get; }
        public StrategyAnomalyInterlock Anomaly { get; } = new();
        public RouterFeedGate Gate { get; }
        public ChildSlotTrackingMatrix Matrix { get; }
        public GatedOrderDispatcher Dispatcher { get; }
        public OrderDrainWorker Worker { get; }
        public TickToTradeLatencyProfiler TickToTrade { get; }
        public OrderPathStageProfiler Stages { get; }
        public LiveArmedEgressSentinel KiwoomSentinel { get; }
        public LiveArmedEgressSentinel KisSentinel { get; }
        public DryRunVirtualBroker Kiwoom { get; }
        public DryRunVirtualBroker Kis { get; }
        public long NowMicros => Volatile.Read(ref _now);
        public long Freezes => Interlocked.Read(ref _freezes);
        public bool PhysicalKeys => _valve && _liveArming;
        public long SignalRejects => Gate.SignalRejects;
        public long UnsanctionedPlaces => Kiwoom.UnsanctionedPlaces + Kis.UnsanctionedPlaces;
        public long InnerPlaces => Kiwoom.Placed + Kis.Placed;

        /// <param name="imbalanceMicros">체결 불균형 차단기 임계(호스트 Routing.ImbalanceMs · 하한 50ms · 기본 500ms).</param>
        /// <param name="stallThresholdMicros">송출 직전 정지 임계(시뮬레이션 기본 1초 — 테스트 호스트 스케줄 지터 오격발 방지 · 호스트 기본 5ms).</param>
        public MultiBrokerDryRunHarness(long imbalanceMicros = 50_000, long stallThresholdMicros = 1_000_000, double confidenceThreshold = QuantSignalProvider.DefaultConfidenceThreshold)
        {
            Func<long> clock = () => Volatile.Read(ref _now);
            _price = new ReferencePriceFeedAdapter(capacityPow2: 256, maxAgeMicros: 3_000_000, nowMicros: clock);
            _depth = new DepthBook(capacityPow2: 256, maxAgeMicros: 2_000_000, nowMicros: clock, maxSpreadBps: 300);
            var board = new LockFreeBrokerSwitchboard(2, initialActive: 0);
            var pending = new PendingOrderCascadeCanceller(4096);
            var failover = new BrokerFailoverSentinel(board, pendingOrders: pending);
            var router = new ResilientOrderRouter(board, failover, pending, idMapCapacityPow2: 8192, maxSlippageBps: 50);
            _exec = new OrderExecutionFeedAdapter(router, 4096, 120_000_000, clock);
            _feedBreaker = new FeedDisconnectCircuitBreaker(_price, () => true);
            Gate = new RouterFeedGate(router, _price, _exec, _feedBreaker, depth: _depth, requireDepthForMarket: true);

            Signals = new QuantSignalProvider(Anomaly, nowMicros: clock, capacityPow2: 64, confidenceThreshold: confidenceThreshold);
            Gate.EnableSignalGate(Signals);

            // ★센티널 술어 = 물리 이중키만(UpstreamFault 와 무관) · 집계어 차단이면 무장이어도 전달 0
            Func<bool> keys = () => _valve && _liveArming;
            Interlocks = new ArmingInterlockBitmask(ArmingInterlockSource.ChildSlotRejection, ArmingInterlockSource.StrategyAnomaly,
                                                    ArmingInterlockSource.EgressBypass, ArmingInterlockSource.LatencyStall);
            Func<bool> sanctioned = () => _valve && _liveArming && Interlocks.Mask == 0 && Attestation.Mode == GatingMode.Normal;
            Kiwoom = new DryRunVirtualBroker("Kiwoom(DryRun)", KiwoomBroker, i => 117_000 + i, sanctioned);
            Kis = new DryRunVirtualBroker("KIS-VTS(DryRun)", KisBroker, i => KisOrderIdSpace.Encode(5_000 + i), sanctioned);
            Action<string> freeze = why => { Interlocked.Increment(ref _freezes); Attestation.ForceFreeze(why); };
            KiwoomSentinel = new LiveArmedEgressSentinel(Kiwoom, keys, onBypass: freeze, interlockBlocked: () => Interlocks.Blocked);
            KisSentinel = new LiveArmedEgressSentinel(Kis, keys, onBypass: freeze, interlockBlocked: () => Interlocks.Blocked);
            Stages = new OrderPathStageProfiler(1024);
            _brokerMap = new IBroker?[] { new EgressStageProbe(KiwoomSentinel, Stages), new EgressStageProbe(KisSentinel, Stages) };

            Matrix = new ChildSlotTrackingMatrix(256, clock, imbalanceMicros,
                cancel: (b, oid) => { if ((uint)b < (uint)_brokerMap.Length && _brokerMap[b] is { } cb) cb.Cancel(oid.ToString(System.Globalization.CultureInfo.InvariantCulture)); });
            TickToTrade = new TickToTradeLatencyProfiler(stallThresholdMicros);

            Matrix.BindInterlock(Interlocks, ArmingInterlockSource.ChildSlotRejection);
            Anomaly.BindInterlock(Interlocks, ArmingInterlockSource.StrategyAnomaly);
            KiwoomSentinel.BindInterlock(Interlocks, ArmingInterlockSource.EgressBypass);   // 호스트와 동일: 비트는 키움 센티널 · KIS 는 같은 동결 콜백
            TickToTrade.BindInterlock(Interlocks, ArmingInterlockSource.LatencyStall);
            if (!Interlocks.Seal(out long missing)) throw new InvalidOperationException($"드라이런 인터락 미결속 0x{missing:X}");

            Gate.EnableSplit(_caps, Matrix.TripExternal);
            _ring = new OrderDispatchRing(4096);
            Dispatcher = new GatedOrderDispatcher(_ring, () => _upstreamFault || (_valve && _liveArming), Attestation);
            Worker = new OrderDrainWorker(Dispatcher, _brokerMap[0]!, wired: false, attestation: Attestation, interlocks: Interlocks,
                                          routeGate: new StageStampingSplitGate(Gate, Stages), latencyProbe: TickToTrade, brokersByIndex: _brokerMap);
            Worker.EnableChildSplit(Matrix);
            _ring.PathObserver = Stages;                                            // [v12.17.6] 호스트와 동일: 발행 · 종결 스탬프(슬롯 즉시 회수)
            _onFill = f => { _exec.OnFill(f); Matrix.OnFill(f); };
        }

        // ── 조작 ──

        public void SetPhysicalKeys(bool valve, bool liveArming) { _valve = valve; _liveArming = liveArming; }
        /// <summary>상류 게이트 오작동 주입(디스패처 무장 술어 강제 true · 센티널은 실 이중키).</summary>
        public void InjectUpstreamFault(bool on) => _upstreamFault = on;
        public void SetCaps(long kiwoom, long kis, bool kiwoomUp = true, bool kisUp = true) { _caps.Kiwoom = kiwoom; _caps.Kis = kis; _caps.KiwoomUp = kiwoomUp; _caps.KisUp = kisUp; }
        public void Advance(long micros) => Interlocked.Add(ref _now, micros);

        // ── 시세(δ=0) ──

        /// <summary>
        /// 키움 10호가 1장(원 · 주): 매수 잔량 bidQty(1~5) · 매도 askQty(1~5) · 6~10호가 deep. 시그널 엔진 + L2 깊이 + 참조가(중간가 근사 = 매도1호가).
        /// </summary>
        public void OnBook(long symbol, long midWon, long bidQty, long askQty, long deepBid = -1, long deepAsk = -1)
        {
            var b = new OrderBookPayload { SymbolCode = symbol, TimestampTicks = NowMicros * 10 };
            for (int i = 0; i < 10; i++)
            {
                b.AskPrice[i] = midWon + Tick * (i + 1); b.BidPrice[i] = midWon - Tick * i;
                b.AskQty[i] = i < 5 || deepAsk < 0 ? askQty : deepAsk;
                b.BidQty[i] = i < 5 || deepBid < 0 ? bidQty : deepBid;
            }
            OnBook(in b);
        }

        public void OnBook(in OrderBookPayload b)
        {
            Signals.OnBook(in b);
            L2OrderBookDepthParser.Validate(in b, out var depth, out _);
            long h = ReferencePriceFeedAdapter.SymbolHashForCode(b.SymbolCode);
            _depth.OnDepth(h, in depth);
            _price.OnTick(h, b.AskPrice[0], ++_seq);
        }

        /// <summary>
        /// 전략 1회: 시그널을 읽고(판정 시각 = 스탬프) 방향이 맞으면 지정가 제안 발행. 반환 = 주문 ID(0 = 시그널 없음 · 미발행).
        /// force = 시그널과 무관하게 발행(확신 게이트 전방 거부 리허설).
        /// </summary>
        public long SubmitFromSignal(long symbol, OrderSide side, long shares, long limitWon, bool force = false)
        {
            if (!force && !(Signals.TryGetSignal(symbol, out var s) && s.Side == (side == OrderSide.Buy ? 1 : -1))) return 0;
            return _ring.Publish(0, symbol, side, 1, new QPrice(limitWon * RawScale), new QQty(shares * RawScale), NowMicros);   // 발행 스탬프 = 링 관측자(호스트와 동일)
        }

        /// <summary>1 사이클: 피드 판정 → 드레인(실전 사슬) → 부모 스탬프 종료 → 체결/취소 통보 → 매트릭스 평가 · 수렴 반납.</summary>
        public int Pump()
        {
            _feedBreaker.Evaluate();
            Worker.DrainOnce();
            Stages.EndOrder();
            long ticks = NowMicros * 10;
            int n = Kiwoom.DeliverFills(_onFill, ticks) + Kis.DeliverFills(_onFill, ticks);
            Matrix.Evaluate();
            n += Kiwoom.DeliverCancels(_onFill, ticks) + Kis.DeliverCancels(_onFill, ticks);
            Matrix.ReleaseConverged();
            return n;
        }

        public void Dispose()
        {
            Worker.Dispose();
            _depth.Dispose();
            TickToTrade.Dispose();
        }
    }
}
