// ============================================================================
// PaperTradingSimulationHarness — [E2E 스트레스 · 추천 A] 실 발주 사슬 + 가상 브로커 Full-Telemetry 모의 거래
//
//  왜 '실전 경로 + 가상 브로커'인가(정직):
//    출하 드라이런(Dispatch.DryRunActive)은 ScopeSealedTransitionBarrier 가 주문을 MockExecutionOrderBookMirror 로 돌려 보내는데,
//    그 분기는 OrderDrainWorker 안에서 인터락 최종 veto · 라우터 게이트(참조가/깊이/보호 지정가/추적 슬롯) · Tick-to-Trade 프로브
//    '이전'에 있다 — 드라이런은 실전 게이팅 사슬을 한 번도 리허설하지 않는다. 이 하네스는 그 사슬을 그대로 조립하고 IBroker 자리에만
//    VirtualPaperBroker(네트워크 0 · IsLive=false)를 꽂는다. 실 브로커(ExecutionBridgeBroker/KiwoomBridgeClient)는 생성 자체가 없다
//    → 실 주문 유출 0 은 '설정'이 아니라 '구조'로 보증(테스트가 하네스 객체 그래프를 반사 검사).
//
//  사슬: Toss 틱 → MarketDataSpikeInterlock → ReferencePriceFeedAdapter │ 키움 10호가 → L2OrderBookDepthParser →
//        OrderbookIlliquidityInterlock + DepthBook │ 제안 → OrderDispatchRing → GatedOrderDispatcher → OrderDrainWorker
//        (인터락 veto → RouterFeedGate → TickToTradeLatencyProfiler → Place) → VirtualPaperBroker → 체결 통보 →
//        OrderExecutionFeedAdapter(미체결 대사) │ 7대 인터락 → ArmingInterlockBitmask → InterlockForensicRecorder(해시 체인 + MMF).
//  δ=0: 틱·호가·제안 발행·드레인·가상 체결·대사·텔레메트리 게시 전 구간이 정상 상태에서 관리 힙 0B(테스트 실측).
//       단, OrderDrainWorker 의 종목 문자열은 KseSymbolText 캐시 적중 기준(종목 최초 1회 할당).
// ============================================================================
using System;
using System.Threading;
using MS2E.Core;
using MS2E.Infrastructure;
using StockManager.Adapters;
using StockManager.Engines;
using StockManager.Telemetry;

namespace StockManager.Simulation
{
    // ════════════════════════════════════════════════════════════════════════
    // 가상 브로커 — 네트워크 0 · 사전 할당 주문번호 문자열 · 고정 링 체결 큐
    // ════════════════════════════════════════════════════════════════════════

    public sealed class VirtualPaperBroker : IBroker
    {
        private struct Pending { public long BrokerId, SymbolCode, Qty; public double Price; public int Side; }

        private readonly string[] _ids;          // 주문번호 문자열 사전 할당(Place 반환 msg · δ=0)
        private readonly bool[] _inflight;
        private readonly Pending[] _queue;
        private int _head, _tail, _count;
        private long _seq, _placed, _rejected, _filled;

        public VirtualPaperBroker(int capacity = 8192)
        {
            if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
            _ids = new string[capacity];
            for (int i = 0; i < capacity; i++) _ids[i] = (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
            _inflight = new bool[capacity];
            _queue = new Pending[capacity];
        }

        public string Name => "VirtualPaper(E2E)";
        public bool IsLive => false;
        public long Placed => Interlocked.Read(ref _placed);
        public long Rejected => Interlocked.Read(ref _rejected);
        public long Filled => Interlocked.Read(ref _filled);
        public int PendingFills => Volatile.Read(ref _count);

        public (bool ok, string msg) Place(PaperAsset asset, string symbol, string name, PaperSide side, PaperKind kind, decimal qty, decimal limitPrice, decimal currentPrice)
        {
            if (!long.TryParse(symbol, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out long code) || qty <= 0 || _count == _queue.Length)
            { Interlocked.Increment(ref _rejected); return (false, "가상 브로커 거부(형식/포화)"); }
            int slot = (int)(_seq++ % _ids.Length);
            if (_inflight[slot]) { Interlocked.Increment(ref _rejected); return (false, "가상 주문번호 재사용 충돌"); }
            _inflight[slot] = true;
            _queue[_tail] = new Pending { BrokerId = slot + 1, SymbolCode = code, Qty = (long)qty, Price = (double)limitPrice, Side = side == PaperSide.Buy ? 1 : -1 };
            _tail = (_tail + 1) % _queue.Length; _count++;
            Interlocked.Increment(ref _placed);
            return (true, _ids[slot]);
        }

        /// <summary>대기 체결을 통보로 방출(전량 체결 · 브로커 잔량 0). 반환 = 방출 건수.</summary>
        public int DeliverFills(Action<FillPayload> sink, long nowTicks)
        {
            int n = 0;
            while (_count > 0)
            {
                ref var p = ref _queue[_head];
                var f = new FillPayload
                {
                    OrderId = p.BrokerId, SymbolCode = p.SymbolCode, FillPrice = p.Price, FillQty = p.Qty, Side = p.Side, TimestampTicks = nowTicks,
                    Status = BridgeFillStatusCodec.Encode(BridgeFillStatus.Filled, true), RemainingQty = 0,
                };
                _inflight[(int)(p.BrokerId - 1)] = false;
                _head = (_head + 1) % _queue.Length; _count--;
                sink(f);
                Interlocked.Increment(ref _filled);
                n++;
            }
            return n;
        }

        public void Cancel(string orderId) { }
        public decimal HeldQty(PaperAsset asset, string symbol) => 0m;
    }

    // ════════════════════════════════════════════════════════════════════════
    // 7대 인터락 고의 오류 주입기 — 실 인터락 객체를 '실제 Trip 경로'로 격발(가짜 소스 아님)
    // ════════════════════════════════════════════════════════════════════════

    public enum InterlockLayer : byte { Pii = 0, MmfFabric = 1, MarketDataSpike = 2, ChaosProgress = 3, RouterFeed = 4, OrderbookIlliquidity = 5, KiwoomServerError = 6 }

    public sealed class SevenLayerInterlockInjector : IDisposable
    {
        public static readonly ArmingInterlockSource[] Sources =
        {
            ArmingInterlockSource.CredentialLeak, ArmingInterlockSource.MmfFabric, ArmingInterlockSource.MarketSpike, ArmingInterlockSource.ChaosProgress,
            ArmingInterlockSource.RouterFeed, ArmingInterlockSource.OrderbookIlliquidity, ArmingInterlockSource.KiwoomServerError,
        };

        public const long SpikeProbeSymbol = 999_998;   // 이상 시세 주입 전용 종목(거래 종목 오염 없음)

        private readonly string _poisonedLine;
        private readonly MmfIpcFabric _mmfWriter, _mmfReader;
        private long _mmfWriterClock = 1_000_000, _mmfReaderClock = 1_000_000;
        private readonly byte[] _mmfBuf = new byte[64];
        private volatile bool _execFeedAlive = true;

        public RawCredentialLeakInterlock Pii { get; }
        public MarketDataSpikeInterlock Spike { get; }
        public ChaosResilientProgressGuard Chaos { get; }
        public FeedDisconnectCircuitBreaker FeedBreaker { get; }
        public OrderbookIlliquidityInterlock Illiquidity { get; }
        public KiwoomServerErrorInterlock KiwoomError { get; }
        public bool MmfCorrupted => _mmfReader.IsCorrupted;
        public bool ExecFeedAlive => _execFeedAlive;

        /// <param name="mmfName">프로세스 내 고유 MMF 이름(하네스마다 다르게).</param>
        public SevenLayerInterlockInjector(ReferencePriceFeedAdapter price, string mmfName, long maxPriceSilenceMicros = 5_000_000)
        {
            string secret = "SIM-CANARY-SECRET-" + Guid.NewGuid().ToString("N");
            Pii = new RawCredentialLeakInterlock();
            Pii.RegisterCredential(secret);
            _poisonedLine = "[sim] outbound log appKey=" + secret;
            _mmfWriter = new MmfIpcFabric(mmfName, MmfFabricRole.Writer, 16, 64, null, () => Interlocked.Read(ref _mmfWriterClock), TimeSpan.TicksPerSecond, 20);
            _mmfReader = new MmfIpcFabric(mmfName, MmfFabricRole.Reader, 16, 64, null, () => Interlocked.Read(ref _mmfReaderClock), TimeSpan.TicksPerSecond, 20);
            Spike = new MarketDataSpikeInterlock(64);
            Chaos = new ChaosResilientProgressGuard();
            FeedBreaker = new FeedDisconnectCircuitBreaker(price, () => _execFeedAlive, maxPriceSilenceMicros);
            Illiquidity = new OrderbookIlliquidityInterlock(1_000, static () => true);
            KiwoomError = new KiwoomServerErrorInterlock();
        }

        public void BindAll(ArmingInterlockBitmask mask)
        {
            Pii.BindInterlock(mask, ArmingInterlockSource.CredentialLeak);
            _mmfReader.BindInterlock(mask, ArmingInterlockSource.MmfFabric);
            Spike.BindInterlock(mask, ArmingInterlockSource.MarketSpike);
            Chaos.BindInterlock(mask, ArmingInterlockSource.ChaosProgress);
            FeedBreaker.BindInterlock(mask, ArmingInterlockSource.RouterFeed);
            Illiquidity.BindInterlock(mask, ArmingInterlockSource.OrderbookIlliquidity);
            KiwoomError.BindInterlock(mask, ArmingInterlockSource.KiwoomServerError);
        }

        /// <summary>계층 1개를 실제 Trip 경로로 격발(스레드 안전 — 각 엔진의 원자 Trip 사용).</summary>
        public void Inject(InterlockLayer layer)
        {
            switch (layer)
            {
                case InterlockLayer.Pii:
                    Pii.Inspect(_poisonedLine);                                         // 원문 자격증명이 로그 경로에 등장
                    break;
                case InterlockLayer.MmfFabric:
                    lock (_mmfBuf)
                    {
                        _mmfWriter.TryPublish(_mmfBuf.AsSpan(0, 8));
                        Interlocked.Add(ref _mmfReaderClock, TimeSpan.TicksPerSecond);   // 라이터 하트비트 1초 침묵(> 20ms) → 리더 부패 판정
                        _mmfReader.TryConsume(_mmfBuf, out _);
                    }
                    break;
                case InterlockLayer.MarketDataSpike:
                    long h = ReferencePriceFeedAdapter.SymbolHashForCode(SpikeProbeSymbol);
                    Spike.Observe(h, 10_000);
                    Spike.Observe(h, 15_000);                                           // +50% 틱 점프(> 10%)
                    break;
                case InterlockLayer.ChaosProgress:
                    Chaos.OnLivelockEscape();                                           // 스핀 상한 소진 = 안전 홀드
                    break;
                case InterlockLayer.RouterFeed:
                    _execFeedAlive = false;                                             // 체결 피드(프록시) 단절
                    FeedBreaker.Evaluate();
                    break;
                case InterlockLayer.OrderbookIlliquidity:
                    Illiquidity.Observe(L2BookVerdict.Crossed, 0);                      // 연속매매 중 호가 교차
                    break;
                case InterlockLayer.KiwoomServerError:
                    KiwoomError.OnError(new ErrorPayload { ErrorCode = KiwoomServerErrorCodes.Password, TimestampTicks = 1 });
                    break;
            }
        }

        /// <summary>계층이 현재 차단 상태인지(엔진 자체 판독).</summary>
        public bool IsBlocked(InterlockLayer layer) => layer switch
        {
            InterlockLayer.Pii => Pii.ArmingBlocked,
            InterlockLayer.MmfFabric => _mmfReader.ArmingBlocked,
            InterlockLayer.MarketDataSpike => Spike.ArmingBlocked,
            InterlockLayer.ChaosProgress => ((IArmingBlockSource)Chaos).ArmingBlocked,
            InterlockLayer.RouterFeed => FeedBreaker.ArmingBlocked,
            InterlockLayer.OrderbookIlliquidity => Illiquidity.ArmingBlocked,
            InterlockLayer.KiwoomServerError => KiwoomError.ArmingBlocked,
            _ => true,
        };

        public void Dispose()
        {
            _mmfReader.Dispose();
            _mmfWriter.Dispose();
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    // [방향 A] 무할당 시뮬레이션 텔레메트리 — 단일 라이터 seqlock 스냅샷 + 렌더 거버너 신호
    // ════════════════════════════════════════════════════════════════════════

    public struct SimulationTelemetry
    {
        public long Version, Ticks, Books, Submitted, Forwarded, GatedOut, InterlockVetoes, RouteVetoes, LatencyVetoes;
        public long VirtualPlaced, VirtualFilled, OpenTracked, InterlockMask, ForensicHead;
        public long T2TCount, T2TP50Micros, T2TP99Micros, T2TP999Micros, T2TMaxMicros;
        public long InProcP99Micros, InProcP999Micros;
        public long ProcessAllocatedBytes, Gen0Collections, ManagedHeapBytes;
    }

    /// <summary>
    /// 단일 라이터(펌프 스레드)가 Publish, 임의 스레드(Blazor 렌더)가 TryRead — 버전 홀/짝 seqlock(찢김 재시도).
    /// UI 는 SnapshotRenderGovernor.Signal(version) 로 '바뀐 버전만' 창(기본 100ms)당 1회 렌더 → 초당 1만 틱에도 렌더 스레드 비동결.
    /// </summary>
    public sealed class SimulationTelemetryChannel
    {
        private SimulationTelemetry _slot;
        private long _seq;
        private readonly Action<long>? _signal;
        private long _published, _signalFaults;

        public SimulationTelemetryChannel(Action<long>? renderSignal = null) => _signal = renderSignal;

        public long Published => Interlocked.Read(ref _published);
        public long SignalFaults => Interlocked.Read(ref _signalFaults);

        public void Publish(in SimulationTelemetry t)
        {
            long s = Interlocked.Increment(ref _seq);           // 홀 = 기록 중
            _slot = t;
            _slot.Version = (s + 1) >> 1;
            Interlocked.Increment(ref _seq);                    // 짝 = 발행
            Interlocked.Increment(ref _published);
            if (_signal is { } sig)
            {
                try { sig((s + 1) >> 1); } catch (Exception) { Interlocked.Increment(ref _signalFaults); }
            }
        }

        /// <summary>찢김 없는 사본(δ=0). 기록 중이면 짧게 재시도 · 상한 초과 = false.</summary>
        public bool TryRead(out SimulationTelemetry t)
        {
            for (int spin = 0; spin < 64; spin++)
            {
                long a = Volatile.Read(ref _seq);
                if ((a & 1) != 0) { Thread.SpinWait(4); continue; }
                t = _slot;
                Interlocked.MemoryBarrier();
                if (Volatile.Read(ref _seq) == a) return true;
            }
            t = default;
            return false;
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    // 하네스
    // ════════════════════════════════════════════════════════════════════════

    public sealed class PaperTradingSimulationHarness : IDisposable
    {
        private readonly Func<long> _nowMicros;
        private readonly ReferencePriceFeedAdapter _price;
        private readonly MarketDataSpikeInterlock _feedSpike;   // 거래 종목 시세 검증(주입기 Spike 와 별개 — 주입은 탐침 종목)
        private readonly DepthBook _depth;
        private readonly ResilientOrderRouter _router;
        private readonly OrderExecutionFeedAdapter _exec;
        private readonly RouterFeedGate _gate;
        private readonly TickToTradeLatencyProfiler _profiler;
        private readonly OrderDispatchRing _ring;
        private readonly GatedOrderDispatcher _dispatcher;
        private readonly OrderDrainWorker _worker;
        private readonly VirtualPaperBroker _broker;
        private readonly ZeroAllocForensicAuditStream _forensic;
        private readonly Action<FillPayload> _onFill;
        private const long RawScale = 100_000_000L;   // QPrice/QQty raw(10^8)
        private long _seq, _ticks, _books, _submitted;

        public ArmingInterlockBitmask Interlocks { get; }
        public SevenLayerInterlockInjector Injector { get; }
        public InterlockForensicRecorder Recorder { get; }
        public SimulationTelemetryChannel Telemetry { get; }
        public VirtualPaperBroker Broker => _broker;
        /// <summary>드레인이 실제로 송출하는 브로커(데코레이터 미주입 = Broker).</summary>
        public IBroker SendBroker { get; }
        public OrderExecutionFeedAdapter Execution => _exec;
        public OrderDrainWorker Worker => _worker;
        public GatedOrderDispatcher Dispatcher => _dispatcher;
        public TickToTradeLatencyProfiler Profiler => _profiler;
        public ZeroAllocForensicAuditStream Forensic => _forensic;
        public RouterFeedGate Gate => _gate;

        /// <param name="nowMicros">단조 µs 시계(null = 실시계). 테스트는 결정론 시계 주입.</param>
        /// <param name="stallThresholdMicros">송출 직전 정지 임계(시뮬레이션 기본 1초 — 테스트 호스트 스케줄 지터로 오격발 방지).</param>
        /// <param name="renderSignal">텔레메트리 갱신 신호(예: SnapshotRenderGovernor.Signal).</param>
        /// <param name="fillObserver">[대시보드] 가상 체결 관측(대사 피드 뒤 · 예외는 삼킴 — 체결 대사 무영향).</param>
        public PaperTradingSimulationHarness(Func<long>? nowMicros = null, long stallThresholdMicros = 1_000_000, Action<long>? renderSignal = null,
                                             MmfFlightRecorder? blackbox = null, long maxPriceSilenceMicros = 5_000_000, Action<FillPayload>? fillObserver = null,
                                             Func<IBroker, IBroker>? brokerDecorator = null, IOrderPathObserver? pathObserver = null)
        {
            _nowMicros = nowMicros ?? ReferencePriceFeedAdapter.MonotonicMicros;
            _price = new ReferencePriceFeedAdapter(capacityPow2: 256, maxAgeMicros: 3_000_000, nowMicros: _nowMicros);
            _feedSpike = new MarketDataSpikeInterlock(256);
            _depth = new DepthBook(capacityPow2: 256, maxAgeMicros: 2_000_000, nowMicros: _nowMicros, maxSpreadBps: 300);
            var board = new LockFreeBrokerSwitchboard(1);
            var pending = new PendingOrderCascadeCanceller(4096);
            var sentinel = new BrokerFailoverSentinel(board, pendingOrders: pending);
            _router = new ResilientOrderRouter(board, sentinel, pending, idMapCapacityPow2: 8192, maxSlippageBps: 50);
            _exec = new OrderExecutionFeedAdapter(_router, capacity: 4096, slotTtlMicros: 120_000_000, nowMicros: _nowMicros);
            _profiler = stallThresholdMicros > 0 ? new TickToTradeLatencyProfiler(stallThresholdMicros) : new TickToTradeLatencyProfiler();

            Interlocks = new ArmingInterlockBitmask(SevenLayerInterlockInjector.Sources);
            _forensic = new ZeroAllocForensicAuditStream(4096);
            Recorder = new InterlockForensicRecorder(_forensic, blackbox);
            Interlocks.AttachTransitionSink(Recorder);
            Injector = new SevenLayerInterlockInjector(_price, "MS2E_SIM_" + Environment.ProcessId + "_" + Guid.NewGuid().ToString("N"), maxPriceSilenceMicros);
            Injector.BindAll(Interlocks);
            if (!Interlocks.Seal(out long missing)) throw new InvalidOperationException($"시뮬레이션 인터락 미결속 0x{missing:X}");

            _gate = new RouterFeedGate(_router, _price, _exec, Injector.FeedBreaker, depth: _depth, requireDepthForMarket: true);
            _broker = new VirtualPaperBroker(8192);
            _ring = new OrderDispatchRing(8192);
            _ring.PathObserver = pathObserver;                                      // [v12.18.4-DEV] 호스트와 동일: 발행 · 종결 스탬프(null = 미결선)
            // ★무장 술어 = 가상 브로커 전용 true. 이 디스패처는 실 브로커에 닿는 경로가 없다(IBroker = VirtualPaperBroker).
            _dispatcher = new GatedOrderDispatcher(_ring, static () => true);
            // [카오스 스톰] 송출 데코레이터(예: LiveArmedEgressSentinel) — 가상 브로커 앞 최종 초크포인트 리허설. 미주입 = 가상 브로커 직결.
            SendBroker = brokerDecorator?.Invoke(_broker) ?? _broker;
            _worker = new OrderDrainWorker(_dispatcher, SendBroker, wired: false, interlocks: Interlocks, routeGate: _gate, latencyProbe: _profiler);
            _onFill = fillObserver is null ? _exec.OnFill : f => { _exec.OnFill(f); try { fillObserver(f); } catch (Exception) { } };
            Telemetry = new SimulationTelemetryChannel(renderSignal);
        }

        // ── 시세 입력(δ=0) ──

        /// <summary>Toss WebSocket 틱(원). 이상 시세 인터락 통과분만 참조가로.</summary>
        public void OnTossTick(long symbolCode, long priceWon)
        {
            long h = ReferencePriceFeedAdapter.SymbolHashForCode(symbolCode);
            Interlocked.Increment(ref _ticks);
            if (_feedSpike.Observe(h, priceWon) == SpikeVerdict.Accepted) _price.OnTick(h, priceWon, ++_seq);
        }

        /// <summary>키움 10호가 스냅샷 — 파서 판정 → 비유동성 인터락 + 깊이 표(비정상이면 빈 깊이로 덮어써 시장가 거부).</summary>
        public void OnKiwoomBook(in OrderBookPayload book)
        {
            Interlocked.Increment(ref _books);
            var v = L2OrderBookDepthParser.Validate(in book, out var depth, out long spreadBps);
            Injector.Illiquidity.Observe(v, spreadBps);
            _depth.OnDepth(ReferencePriceFeedAdapter.SymbolHashForCode(book.SymbolCode), in depth);
        }

        // ── 주문(δ=0) ──

        /// <summary>가상 주문 제안 발행(가격·수량 = 원·주). kind 0 = 시장가(보호 지정가 변환) · 1 = 지정가.</summary>
        public long Submit(long symbolCode, OrderSide side, byte kind, long priceWon, long shares)
        {
            Interlocked.Increment(ref _submitted);
            return _ring.Publish(0, symbolCode, side, kind, new QPrice(priceWon * RawScale), new QQty(shares * RawScale), _nowMicros());
        }

        /// <summary>1 사이클: 피드 단절 판정 → 드레인(실전 게이팅 사슬) → 가상 체결 통보 → 대사 → 텔레메트리 게시.</summary>
        public int Pump()
        {
            Injector.FeedBreaker.Evaluate();
            _worker.DrainOnce();
            int fills = _broker.DeliverFills(_onFill, _nowMicros());
            PublishTelemetry();
            return fills;
        }

        public void PublishTelemetry()
        {
            var t2t = _profiler.TickToTrade();
            var inp = _profiler.InProcess();
            var t = new SimulationTelemetry
            {
                Ticks = Interlocked.Read(ref _ticks), Books = Interlocked.Read(ref _books), Submitted = Interlocked.Read(ref _submitted),
                Forwarded = _dispatcher.Forwarded, GatedOut = _dispatcher.GatedOut,
                InterlockVetoes = _worker.InterlockVetoes, RouteVetoes = _worker.RouteVetoes, LatencyVetoes = _worker.LatencyVetoes,
                VirtualPlaced = _broker.Placed, VirtualFilled = _broker.Filled, OpenTracked = _exec.Tracked,
                InterlockMask = Interlocks.Mask, ForensicHead = _forensic.WriteHead,
                T2TCount = t2t.Count, T2TP50Micros = t2t.P50Micros, T2TP99Micros = t2t.P99Micros, T2TP999Micros = t2t.P999Micros, T2TMaxMicros = t2t.MaxMicros,
                InProcP99Micros = inp.P99Micros, InProcP999Micros = inp.P999Micros,
                ProcessAllocatedBytes = GC.GetTotalAllocatedBytes(false), Gen0Collections = GC.CollectionCount(0), ManagedHeapBytes = GC.GetTotalMemory(false),
            };
            Telemetry.Publish(in t);
        }

        public void Dispose()
        {
            _worker.Dispose();
            Injector.Dispose();
            _depth.Dispose();
            _profiler.Dispose();
        }
    }
}
