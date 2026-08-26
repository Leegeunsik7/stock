// ============================================================================
// MicroLiquiditySlicer — v5.2.0 과제1: 무할당 미시 호가 유동성 슬라이서
//
//  실 증권사 웹소켓 L2 5호가(Bid/Ask depth) 잔량을 힙 할당 0(δ=0)으로 µs 추적하고, 대규모 주문을 5호가 내 즉시체결
//  가능한 실시간 잔량분만 정확히 발라내어 시장 충격을 최소화하는 최경량 미니 TWAP/VWAP 분할 슬라이서. 각 하위 슬라이스는
//  OrderDispatchRing(50ms 드레인 링)에 단일 생산자로 동기 사출된다.
//
//  ── 정직 회계(정찰) ─────────────────────────────────────────────────────────
//  기존 인입 TickPayload 는 best/현재가만 운반(depth 없음), Quote.Bid/AskVolumes 는 관리 힙 배열(δ≠0·미배선)이라 핫패스 부적합
//  → L2Depth 를 ★C#12 InlineArray 기반 unmanaged 값타입으로 신규 도입. 분할 수학은 TwapVwapExecutor.ComputeTwap 개념을
//  raw 정수로 포팅. 명목/수량은 QMath·QQty(소수 코인/정수 주식 공용 10^8 raw). 발행은 OrderDispatchRing.PublishRaw(단일 생산자 CAS 게이트).
//
//  ── liveArmed:false / δ=0 / 결정론 ─────────────────────────────────────────
//  슬라이스는 GatedOrderDispatcher 무장 게이트 하류 — 이중키∧MasterIgnited 없이는 실 송출 0(자율 슬라이싱은 흐름을 '줄이는'
//  방향만). 핫 연산(ComputeImbalance·ComputeFillableQty·SliceInto)은 값타입·고정 InlineArray·스택 스팬만 — 힙 0. 시계 주입.
// ============================================================================
using System;
using System.Runtime.CompilerServices;
using System.Threading;

namespace MS2E.Infrastructure
{
    /// <summary>C#12 InlineArray — 5원소 long 고정 인라인 버퍼(힙 0·blittable). 5호가 사다리 저장용.</summary>
    [InlineArray(5)]
    public struct Fixed5Long { private long _e0; }

    /// <summary>L2 5호가 스냅샷(unmanaged·δ=0). 가격/잔량은 raw(10^8). 지수 0=최우선호가(best). 관리 배열 미사용.</summary>
    public struct L2Depth
    {
        public long SymbolCode;
        public long TimestampTicks;
        public Fixed5Long BidPriceRaw;   // [0]=best bid(최고 매수호가) 내림차순
        public Fixed5Long BidQtyRaw;
        public Fixed5Long AskPriceRaw;   // [0]=best ask(최저 매도호가) 오름차순
        public Fixed5Long AskQtyRaw;
    }

    /// <summary>미시 유동성 슬라이서 — L2 depth 추적 + 즉시체결 잔량 슬라이싱. 핫 연산 δ=0.</summary>
    public sealed class MicroLiquiditySlicer
    {
        private readonly OrderDispatchRing? _ring;
        private readonly ToxicFlowSentinel? _toxic;      // Think Beyond 베토(독성 시 축소/홀드)
        private readonly AdaptiveOrderThrottle? _throttle;
        private readonly MmfFlightRecorder? _blackbox;
        private readonly int _maxSlices;

        private long _slicesEmitted, _ordersSliced, _lastImbalanceRaw, _heldByToxic, _heldByThrottle;
        private long _cumSavedBps;   // 누적 슬리피지 절감 추정(bps) — '전량 덤프 시 북워크 슬리피지'를 슬라이싱으로 회피한 반사실 비용

        public MicroLiquiditySlicer(OrderDispatchRing? ring = null, ToxicFlowSentinel? toxic = null,
                                    AdaptiveOrderThrottle? throttle = null, int maxSlices = 16, MmfFlightRecorder? blackbox = null)
        {
            _ring = ring; _toxic = toxic; _throttle = throttle; _blackbox = blackbox;
            _maxSlices = maxSlices < 1 ? 1 : (maxSlices > 64 ? 64 : maxSlices);
        }

        public long SlicesEmitted   => Interlocked.Read(ref _slicesEmitted);
        public long OrdersSliced    => Interlocked.Read(ref _ordersSliced);
        public long LastImbalanceRaw => Interlocked.Read(ref _lastImbalanceRaw);   // (bidΣ-askΣ)/(bidΣ+askΣ)×Scale ∈ [-Scale,+Scale]
        public long HeldByToxic     => Interlocked.Read(ref _heldByToxic);
        public long HeldByThrottle  => Interlocked.Read(ref _heldByThrottle);
        public long CumSavedBps     => Interlocked.Read(ref _cumSavedBps);   // 누적 슬리피지 절감 추정(bps)

        /// <summary>
        /// ★전량 즉시 덤프 시 예상 북워크 슬리피지(bps) — 반대편 사다리를 totalQty 만큼 걸어 VWAP 체결가를 구해 best 대비 이탈 bps.
        /// 슬라이싱이 즉시체결 잔량분만 취해 이 비용을 회피하므로, 이 값이 곧 '절감 추정'. δ=0(고정 5호가 순회). 북 소진분은 마지막 호가가로 보수 근사.
        /// </summary>
        public static long EstimateDumpSlippageBps(in L2Depth d, OrderSide side, long totalQtyRaw)
        {
            if (totalQtyRaw <= 0) return 0;
            long best = side == OrderSide.Buy ? d.AskPriceRaw[0] : d.BidPriceRaw[0];
            if (best <= 0) return 0;
            long remain = totalQtyRaw, lastP = best;
            Int128 costNum = 0;   // Σ(qty×price)
            long filled = 0;
            for (int i = 0; i < 5 && remain > 0; i++)
            {
                long p = side == OrderSide.Buy ? d.AskPriceRaw[i] : d.BidPriceRaw[i];
                long q = side == OrderSide.Buy ? d.AskQtyRaw[i] : d.BidQtyRaw[i];
                if (p <= 0 || q <= 0) continue;
                long take = q < remain ? q : remain;
                costNum += (Int128)take * p; filled += take; remain -= take; lastP = p;
            }
            if (remain > 0)
            {
                // ★5호가 소진분(가시 잔량 초과) = 유령 깊이. 최후 호가가에 보수 페널티(+0.5%)를 얹어 과소평가 봉쇄(얇은/갭 북에서 remainder를 best로 과소평가하던 결함 교정).
                long phantomP = lastP + lastP / 200;   // +50bps
                costNum += (Int128)remain * phantomP; filled += remain;
            }
            if (filled <= 0) return 0;
            long vwap = (long)(costNum / filled);
            long dev = side == OrderSide.Buy ? vwap - best : best - vwap;   // 불리 이탈(양수=슬리피지)
            if (dev <= 0) return 0;
            return (long)((Int128)dev * 10_000 / best);   // bps
        }

        // ── δ=0 순수 연산(핫패스·힙 0) ─────────────────────────────────────────

        /// <summary>5호가 잔량 비대칭 지수 = (ΣBidQty - ΣAskQty)/(ΣBidQty + ΣAskQty) × Scale ∈ [-Scale,+Scale]. δ=0.</summary>
        public static long ComputeImbalance(in L2Depth d)
        {
            long bid = 0, ask = 0;
            for (int i = 0; i < 5; i++) { long b = d.BidQtyRaw[i]; long a = d.AskQtyRaw[i]; if (b > 0) bid += b; if (a > 0) ask += a; }
            long tot = bid + ask;
            if (tot <= 0) return 0;
            return (long)((Int128)(bid - ask) * QMath.Scale / tot);
        }

        /// <summary>지정가 한도 내 즉시체결 가능 반대편 잔량 합(raw). 매수=ask 사다리에서 price&lt;=limit, 매도=bid 사다리에서 price&gt;=limit. δ=0.</summary>
        public static long ComputeFillableQty(in L2Depth d, OrderSide side, long limitPriceRaw)
        {
            long fill = 0;
            for (int i = 0; i < 5; i++)
            {
                if (side == OrderSide.Buy)
                {
                    long p = d.AskPriceRaw[i]; long q = d.AskQtyRaw[i];
                    if (q > 0 && p > 0 && (limitPriceRaw <= 0 || p <= limitPriceRaw)) fill += q;   // limit<=0=시장가(전 호가)
                }
                else
                {
                    long p = d.BidPriceRaw[i]; long q = d.BidQtyRaw[i];
                    if (q > 0 && p > 0 && (limitPriceRaw <= 0 || p >= limitPriceRaw)) fill += q;
                }
            }
            return fill;
        }

        /// <summary>
        /// ★즉시체결 가능 잔량분만 발라 미니 TWAP 균등 분할(δ=0·힙 0). dst 스팬에 슬라이스 수량(raw)을 채우고 개수 반환.
        /// 총량이 즉시체결 잔량을 초과하면 잔량까지만(시장 충격 최소화). 독성 베토(fillableCap)로 상한을 축소할 수 있음.
        /// </summary>
        public static int SliceInto(Span<long> dst, long totalQtyRaw, long fillableRaw, int maxSlices)
        {
            if (totalQtyRaw <= 0 || fillableRaw <= 0 || dst.Length == 0) return 0;
            long cap = totalQtyRaw < fillableRaw ? totalQtyRaw : fillableRaw;   // 즉시체결 가능분까지만
            int n = maxSlices; if (n > dst.Length) n = dst.Length;
            if (n < 1) n = 1;
            long per = cap / n;
            if (per <= 0) { dst[0] = cap; return 1; }                          // 너무 작으면 단일 슬라이스
            long acc = 0; int k = 0;
            for (int i = 0; i < n; i++)
            {
                long slice = (i == n - 1) ? (cap - acc) : per;                 // 마지막에 잔여 집중
                if (slice <= 0) break;
                dst[k++] = slice; acc += slice;
            }
            return k;
        }

        // ── 상태 인입 + 슬라이스 사출(콜드 경로) ────────────────────────────────

        /// <summary>L2 depth 인입 — 비대칭 지수 갱신 + 독성 센티널 급전(관측). δ=0(값 복사·Interlocked).</summary>
        public void Ingest(in L2Depth d, long quoteRttTicks = 0)
        {
            long imb = ComputeImbalance(in d);
            Interlocked.Exchange(ref _lastImbalanceRaw, imb);
            _toxic?.Ingest(in d, imb, quoteRttTicks);
        }

        /// <summary>
        /// 대규모 주문을 즉시체결 잔량분으로 슬라이싱해 OrderDispatchRing 에 동기 사출. 독성 Toxic→홀드(0), Caution→잔량 축소.
        /// 스로틀 90% 도달→해당 슬라이스 홀드. 반환=사출된 슬라이스 수. ★실 송출은 하류 무장 게이트 지배(liveArmed:false).
        /// </summary>
        public int SliceAndDispatch(in L2Depth d, long symbolCode, long genomeId, OrderSide side, long totalQtyRaw, long limitPriceRaw, long nowTicks)
        {
            // 독성 베토 — Toxic 이면 전량 홀드.
            long fillable = ComputeFillableQty(in d, side, limitPriceRaw);
            if (_toxic is { } tx)
            {
                long adj = tx.ToxicityAdjustedFillable(fillable);
                if (adj <= 0) { Interlocked.Increment(ref _heldByToxic); return 0; }   // Toxic 홀드
                fillable = adj;                                                        // Caution 축소
            }

            Span<long> slices = stackalloc long[_maxSlices];
            int n = SliceInto(slices, totalQtyRaw, fillable, _maxSlices);
            if (n == 0) return 0;

            byte kind = limitPriceRaw > 0 ? (byte)1 : (byte)0;   // 1=Limit·0=Market
            int emitted = 0;
            for (int i = 0; i < n; i++)
            {
                // 스로틀 90% 도달 시 이후 슬라이스 홀드(초당 주문 한도 초과 방지).
                if (_throttle is { } th && !th.TryAcquire(nowTicks)) { Interlocked.Increment(ref _heldByThrottle); break; }
                var qty = new QQty(slices[i]);
                var px  = new QPrice(limitPriceRaw > 0 ? limitPriceRaw : 0);
                try { _ring?.PublishRaw(new OrderProposal(0, genomeId, symbolCode, side, kind, px, qty, nowTicks)); }
                catch { break; }   // 링 단일생산자 위반 등 — fail-closed 중단
                emitted++;
            }
            if (emitted > 0)
            {
                Interlocked.Add(ref _slicesEmitted, emitted);
                Interlocked.Increment(ref _ordersSliced);
                long savedBps = EstimateDumpSlippageBps(in d, side, totalQtyRaw);   // 전량 덤프 회피 반사실 비용 = 절감 추정
                if (savedBps > 0) Interlocked.Add(ref _cumSavedBps, savedBps);
                try { _blackbox?.Record(FlightEventKind.LiquiditySlice, symbolCode, totalQtyRaw, fillable, emitted, 70); } catch { }
            }
            return emitted;
        }
    }
}
