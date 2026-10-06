// ============================================================================
// RouterFeedGate — [피드 연동] 발주 송출 경로(OrderDrainWorker) ↔ ResilientOrderRouter 결속(IOrderRouteGate 구현)
// FeedDisconnectCircuitBreaker — [방향 B] 시세 피드 · 체결 통보 피드 단절 원자 회로 차단기
//
//  TryAdmit(p) 판정 순서(첫 실패에서 미송출 · 전부 δ=0):
//    ① 회로 차단기(피드 단절/미수립) → FeedDisconnected
//    ② 종목 참조가(ReferencePriceFeedAdapter) Fresh 아님 → StaleReference(미수신·만료) — ★시장가도 참조가 없으면 송출 불가
//    ③ 라우터 Route(키 = 제안 OrderId 128비트, limit, reference) — Disarm/이중붕괴/블랙리스트/슬리피지/중복/추적 포화
//       · 시장가(Kind=0)는 limit 이 없으므로 limit = 참조가로 판정(슬리피지 0) — 실 체결가 편차는 라우터가 통제 못 함(정직 한계:
//         시장가의 체결 슬리피지 상한은 브로커 측 보호 지정가 전환이 필요). 신선한 참조가 존재가 시장가 송출의 최소 조건.
//       · regimeSignal: 레짐 피드 미결선 → 0.0(유한값) 고정.
//    ④ 수량 정수화(QQty raw 10^8 → 주) — 0 주(소수 수량)면 라우터 Abort · 미송출
//  [보호 지정가 · 추천 A] ②와 ③ 사이 재편(라우터 슬롯 확보 전에 전부 판정 — 거부 시 슬롯 부작용 0):
//    ②-a 시장가 → ProtectiveLimitOrderConverter(참조가 ± offset · 호가 단위 보호 방향 정렬 · 가격제한폭 클램프) → 송출은 '지정가'.
//         변환 불가(0) → ProtectionUnavailable. ★비보호 시장가는 이 게이트를 절대 통과하지 못한다(송출 Kind 는 항상 1).
//    ②-b 호가 깊이(DepthBook · 1~3호가): 시장가(변환분)는 Ok=통과 · Reduced=수량 축소 송출 · Insufficient=거부 ·
//         Missing/Stale = requireDepthForMarket 이면 거부(기본 · 미분류 fail-closed). 지정가는 깊이 미상/부족이어도 통과(가격이 보호선 · 대기 허용).
//    ③ 라우터 슬리피지(MaxSlippageBps) 초과 → 대사 로그(SlippageRejected) 사출 후 거부. 보호 offset 은 라우터 한도 이하로 강제(ctor).
//    ⑤ 체결 피드 원장 추적 확보(OrderExecutionFeedAdapter.TryTrack) — 포화면 라우터 Abort · 미송출(추적 불가 주문 송출 금지)
//
//  FeedDisconnectCircuitBreaker(Evaluate · 주기 + TryAdmit 인라인):
//    healthy = 시세 피드 침묵 ≤ maxPriceSilence ∧ 체결 피드(브리지 프록시) 생존.
//    · 한 번도 healthy 가 아니었던 기동 직후 = 레벨 차단(무장 불가 · 래치 아님).
//    · healthy → 단절 전이 = ★sticky 래치(Tripped) — 피드 복구만으로 자동 해제 금지, 물리 이중키 재무장(TryRearm) 필요.
//    ArmingBlocked = 레벨 차단 ∨ 래치. IArmingBlockSource 로 ArmingInterlockBitmask(RouterFeed) 에 푸시.
// ============================================================================
using System;
using System.Threading;
using MS2E.Core;
using MS2E.Infrastructure;

namespace StockManager.Adapters
{
    public sealed class FeedDisconnectCircuitBreaker : IArmingBlockSource
    {
        public const long DefaultMaxPriceSilenceMicros = 5_000_000;   // 5초

        private readonly ReferencePriceFeedAdapter _price;
        private readonly Func<bool> _execFeedAlive;
        private readonly long _maxSilenceMicros;
        private int _level = 1, _tripped, _everHealthy;
        private long _trips;
        private ArmingInterlockLink? _link;

        public FeedDisconnectCircuitBreaker(ReferencePriceFeedAdapter price, Func<bool> execFeedAlive, long maxPriceSilenceMicros = DefaultMaxPriceSilenceMicros)
        {
            _price = price ?? throw new ArgumentNullException(nameof(price));
            _execFeedAlive = execFeedAlive ?? throw new ArgumentNullException(nameof(execFeedAlive));
            if (maxPriceSilenceMicros <= 0) throw new ArgumentOutOfRangeException(nameof(maxPriceSilenceMicros));
            _maxSilenceMicros = maxPriceSilenceMicros;
        }

        public bool Tripped => Volatile.Read(ref _tripped) == 1;
        public long Trips => Interlocked.Read(ref _trips);
        public bool ArmingBlocked => Volatile.Read(ref _level) == 1 || Volatile.Read(ref _tripped) == 1;

        public void BindInterlock(ArmingInterlockBitmask aggregator, ArmingInterlockSource bit)
        {
            var link = aggregator.Bind(bit, this);
            Volatile.Write(ref _link, link);
            link.Resync();
        }

        /// <summary>피드 건강도 평가(δ=0). 반환 = 현재 차단 여부.</summary>
        public bool Evaluate()
        {
            bool execOk;
            try { execOk = _execFeedAlive(); } catch { execOk = false; }   // 판독 불가 = 단절(fail-closed)
            bool healthy = execOk && _price.FeedSilenceMicros(_price.Now()) <= _maxSilenceMicros;
            int prevLevel = Volatile.Read(ref _level);
            if (healthy)
            {
                Volatile.Write(ref _everHealthy, 1);
                Volatile.Write(ref _level, 0);
            }
            else
            {
                Volatile.Write(ref _level, 1);
                if (Volatile.Read(ref _everHealthy) == 1 && Interlocked.CompareExchange(ref _tripped, 1, 0) == 0)
                    Interlocked.Increment(ref _trips);                       // healthy → 단절 전이 = 래치
            }
            if (prevLevel != Volatile.Read(ref _level) || !healthy) Volatile.Read(ref _link)?.Resync();
            return ArmingBlocked;
        }

        public bool TryRearm(bool valveWired, bool liveArmingWired)
        {
            if (!(valveWired && liveArmingWired)) return false;
            Volatile.Write(ref _tripped, 0);
            Evaluate();
            Volatile.Read(ref _link)?.Resync();
            return true;
        }
    }

    public sealed class RouterFeedGate : IOrderRouteGate
    {
        private const long QtyScale = 100_000_000L;   // QQty raw(10^8) → 주

        private readonly ResilientOrderRouter _router;
        private readonly ReferencePriceFeedAdapter _price;
        private readonly OrderExecutionFeedAdapter _exec;
        private readonly FeedDisconnectCircuitBreaker _breaker;
        private readonly StockManager.Engines.ProtectiveLimitOrderConverter _converter;
        private readonly StockManager.Engines.DepthBook? _depth;
        private readonly bool _requireDepthForMarket;
        private readonly Func<long, long>? _basePriceRaw;
        private readonly AccountStateSyncAdapter? _account;
        private long _accountRejects;
        private long _admitted, _staleRejects, _feedRejects, _routeRejects, _fractionalRejects, _trackRejects, _notSent;
        private long _marketConverted, _depthRejects, _depthReduced, _protectionRejects, _slippageRejects;
        private int _lastVerdict;

        /// <param name="converter">시장가 보호 변환기(null = offset min(30, 라우터 한도)bps 기본). offset &gt; 라우터 한도면 예외(전 시장가 자가 거부 방지).</param>
        /// <param name="depth">호가 깊이 표(null = 깊이 피드 미결선).</param>
        /// <param name="requireDepthForMarket">시장가는 신선한 깊이가 있어야만 송출(기본 true · 미분류 fail-closed).</param>
        /// <param name="basePriceRaw">종목 키 → 기준가 raw(가격제한폭 클램프 · 0 = 미상 → 클램프 생략).</param>
        public RouterFeedGate(ResilientOrderRouter router, ReferencePriceFeedAdapter price, OrderExecutionFeedAdapter exec, FeedDisconnectCircuitBreaker breaker,
                              StockManager.Engines.ProtectiveLimitOrderConverter? converter = null, StockManager.Engines.DepthBook? depth = null,
                              bool requireDepthForMarket = true, Func<long, long>? basePriceRaw = null,
                              AccountStateSyncAdapter? account = null)
        {
            _router = router ?? throw new ArgumentNullException(nameof(router));
            _price = price ?? throw new ArgumentNullException(nameof(price));
            _exec = exec ?? throw new ArgumentNullException(nameof(exec));
            _breaker = breaker ?? throw new ArgumentNullException(nameof(breaker));
            _converter = converter ?? new StockManager.Engines.ProtectiveLimitOrderConverter(Math.Max(1, Math.Min(30, router.MaxSlippageBps)));
            if (_converter.OffsetBps > router.MaxSlippageBps)
                throw new ArgumentException($"보호 offset {_converter.OffsetBps}bps > 라우터 슬리피지 한도 {router.MaxSlippageBps}bps — 모든 시장가가 자가 거부됨", nameof(converter));
            _depth = depth; _requireDepthForMarket = requireDepthForMarket; _basePriceRaw = basePriceRaw;
            _account = account;
        }

        /// <summary>[계좌] 주문가능금액/보유수량/스냅샷 만료로 거부한 누계.</summary>
        public long AccountRejects => Interlocked.Read(ref _accountRejects);

        public long MarketConverted => Interlocked.Read(ref _marketConverted);
        public long DepthRejects => Interlocked.Read(ref _depthRejects);
        public long DepthReduced => Interlocked.Read(ref _depthReduced);
        public long ProtectionRejects => Interlocked.Read(ref _protectionRejects);
        public long SlippageRejects => Interlocked.Read(ref _slippageRejects);

        public long Admitted => Interlocked.Read(ref _admitted);
        public long StaleRejects => Interlocked.Read(ref _staleRejects);
        public long FeedRejects => Interlocked.Read(ref _feedRejects);
        public long RouteRejects => Interlocked.Read(ref _routeRejects);
        public long FractionalRejects => Interlocked.Read(ref _fractionalRejects);
        public long TrackRejects => Interlocked.Read(ref _trackRejects);
        public long NotSentRollbacks => Interlocked.Read(ref _notSent);
        public OrderRouteVerdict LastVerdict => (OrderRouteVerdict)Volatile.Read(ref _lastVerdict);

        public static UInt128 KeyFor(long proposalId) => new(0UL, (ulong)proposalId);

        /// <summary>편의 오버로드(송출 형태 폐기).</summary>
        public bool TryAdmit(in OrderProposal p) => TryAdmit(in p, out _);

        public bool TryAdmit(in OrderProposal p, out OrderAdmission admission)
        {
            admission = default;
            if (_breaker.Evaluate()) return Reject(OrderRouteVerdict.FeedDisconnected, ref _feedRejects);
            long symHash = ReferencePriceFeedAdapter.SymbolHashForCode(p.SymbolCode);
            if (_price.TryGetReference(symHash, out long reference, out long basisAgeMicros) != ReferencePriceVerdict.Fresh)
                return Reject(OrderRouteVerdict.StaleReference, ref _staleRejects);

            long shares = p.QtyRaw / QtyScale;
            if (shares <= 0) { Interlocked.Increment(ref _fractionalRejects); return false; }   // 소수 수량(0 주) — 라우터 슬롯 확보 전 거부(부작용 0)

            var side = p.SideByte == 0 ? OrderSide.Buy : OrderSide.Sell;
            bool market = p.KindByte == 0;
            long limit = p.PriceRaw;
            if (market)
            {
                // ②-a 보호 지정가 변환 — 이후 송출은 지정가
                long basePx = 0;
                if (_basePriceRaw is { } bp) { try { basePx = bp(symHash); } catch { basePx = 0; } }
                limit = _converter.Convert(side, reference, basePx);
                if (limit <= 0) return Reject(OrderRouteVerdict.ProtectionUnavailable, ref _protectionRejects);
                Interlocked.Increment(ref _marketConverted);
            }

            // ②-b 호가 깊이(1~3호가)
            if (market)
            {
                if (_depth is null)
                {
                    if (_requireDepthForMarket) return DepthReject(p.OrderId, shares);
                }
                else
                {
                    var dv = _depth.Evaluate(symHash, side, limit, shares, out long allowed);
                    switch (dv)
                    {
                        case StockManager.Engines.DepthVerdict.Ok: break;
                        case StockManager.Engines.DepthVerdict.Reduced:
                            _exec.ReportGateEvent(ReconciliationKind.DepthReduced, p.OrderId, allowed);
                            Interlocked.Increment(ref _depthReduced);
                            shares = allowed;
                            break;
                        case StockManager.Engines.DepthVerdict.Insufficient:
                            return DepthReject(p.OrderId, shares);
                        case StockManager.Engines.DepthVerdict.SpreadTooWide:
                            _exec.ReportGateEvent(ReconciliationKind.SpreadRejected, p.OrderId, shares);
                            return Reject(OrderRouteVerdict.SpreadTooWide, ref _depthRejects);
                        default:   // Missing · Stale
                            if (_requireDepthForMarket) return DepthReject(p.OrderId, shares);
                            break;
                    }
                }
            }

            // ②-c [계좌] 주문가능금액(매수 · 예약) / 추정 보유수량(매도) — 스냅샷 부재/만료 = 거부
            long reservedWon = 0;
            if (_account is { } acct)
            {
                if (side == OrderSide.Buy)
                {
                    reservedWon = NotionalWonCeil(limit, shares);
                    var av = acct.TryReserveBuy(reservedWon);
                    if (av != AccountVerdict.Ok) { reservedWon = 0; return AccountReject(p.OrderId, shares, av == AccountVerdict.Stale ? OrderRouteVerdict.AccountStale : OrderRouteVerdict.InsufficientCash); }
                }
                else
                {
                    var av = acct.CheckSell(p.SymbolCode, shares);
                    if (av != AccountVerdict.Ok) return AccountReject(p.OrderId, shares, av == AccountVerdict.Stale ? OrderRouteVerdict.AccountStale : OrderRouteVerdict.InsufficientPosition);
                }
            }

            // ③ 라우터(슬리피지 · Disarm · 블랙리스트 · 추적)
            UInt128 key = KeyFor(p.OrderId);
            var r = _router.Route(key, limit, reference, 0.0);
            Volatile.Write(ref _lastVerdict, (int)r.Verdict);
            if (!r.Accepted)
            {
                if (reservedWon != 0) _account!.ReleaseBuy(reservedWon);   // 미송출 — 예약 반환
                Interlocked.Increment(ref _routeRejects);
                if (r.Verdict == OrderRouteVerdict.SlippageExceeded)
                {
                    Interlocked.Increment(ref _slippageRejects);
                    _exec.ReportGateEvent(ReconciliationKind.SlippageRejected, p.OrderId, shares);
                }
                return false;
            }
            if (!_exec.TryTrack(p.OrderId, key, r.Broker, shares, side == OrderSide.Buy, reference))
            {
                if (reservedWon != 0) _account!.ReleaseBuy(reservedWon);
                _router.Abort(key);
                Interlocked.Increment(ref _trackRejects);
                return false;
            }
            Interlocked.Increment(ref _admitted);
            admission = new OrderAdmission(1, limit, shares * QtyScale, basisAgeMicros);   // ★송출 Kind 는 항상 지정가 · 참조가 틱 나이 동봉
            return true;
        }

        private bool AccountReject(long proposalId, long shares, OrderRouteVerdict v)
        {
            _exec.ReportGateEvent(ReconciliationKind.AccountRejected, proposalId, shares);
            return Reject(v, ref _accountRejects);
        }

        // 명목(원) = ⌈지정가 raw(10^8) × 주 / 10^8⌉ — Int128 곱(오버플로 차단) · 상한 포화
        internal static long NotionalWonCeil(long limitRaw, long shares)
        {
            if (limitRaw <= 0 || shares <= 0) return 0;
            Int128 n = ((Int128)limitRaw * shares + (100_000_000 - 1)) / 100_000_000;
            return n > long.MaxValue ? long.MaxValue : (long)n;
        }

        private bool DepthReject(long proposalId, long shares)
        {
            _exec.ReportGateEvent(ReconciliationKind.DepthRejected, proposalId, shares);
            return Reject(OrderRouteVerdict.DepthInsufficient, ref _depthRejects);
        }

        public void OnBrokerAccepted(long proposalId, string brokerOrderId)
        {
            if (long.TryParse(brokerOrderId, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out long bid) && bid != 0)
                _exec.BindBrokerOrder(proposalId, bid);
            else
                _exec.ReportUnparsedBrokerId(proposalId);   // 추적 유지 → 체결 매핑 불가분은 TTL 회수 + 차단
        }

        public void OnNotSent(long proposalId)
        {
            if (_exec.Untrack(proposalId, out UInt128 key) && _router.Abort(key))
                Interlocked.Increment(ref _notSent);
        }

        private bool Reject(OrderRouteVerdict v, ref long counter)
        {
            _router.RejectExternal(v);
            Volatile.Write(ref _lastVerdict, (int)v);
            Interlocked.Increment(ref counter);
            return false;
        }
    }
}
