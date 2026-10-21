// ============================================================================
// AiExecutionSupervisor — [v12.18.47-DEV] L1 호가 · 기준가 컨텍스트 캐시 + 다종목 AI 라우터 + 30대 인터락 → RouterFeedGate 호환 발주 후보(δ=0)
//
//  왜: MultiSymbolAiSignalRouter(§135 · §137)는 참조가 · 기준가를 '인자'로만 받는다 — 실시간 L1 호가/기준가와 묶인 곳이 없어
//   AI 신호 → 주문 후보 종단 경로가 존재하지 않았다. 이 감독자가 시장 컨텍스트를 슬롯에 보관하고 신호 인입 시 결합한다.
//  구조(전부 생성 시 1회 할당 · 핫패스 δ=0):
//   · 컨텍스트 슬롯 64(라우터 MaxSlots 와 동일) = 기준가 · 최우선 매수/매도호가 · 갱신 시각 · 종목별 수량 캡(raw 10^8 · 주).
//     종목 → 슬롯 = 개방 주소 해시(라우터와 같은 방식 · 해제 없음). 슬롯 단위 seqlock — 작성자 = 시세 피드 1개(CAS 방어 · 경합 = 그 갱신 거부),
//     판독자 = 신호 스레드(찢김 감지 시 재시도 · 상한 초과 = Contended 거부).
//   · 갱신 거부(fail-closed): 종목 범위 이탈 · 기준가/호가 ≤0 · 교차/동일가(bid ≥ ask) · 종목 시각 역행 · 슬롯 소진.
//  ProcessSignal 순서:
//   ① 30대 인터락 마스크 ≠ 0(Bit 0 ~ 28 · 63 중 어느 하나) = InterlockBlocked — 라우터 · 슬롯 · 반전 이력 무접촉(1틱 차단).
//   ② 컨텍스트 미수신 = NoMarketContext · 시각 > now = FutureMarketContext · now − 시각 > 허용 나이 = StaleMarketContext (전부 평가 전).
//   ③ 참조가: 확신 > 0(매수) = BestAsk × (1 + 슬리피지) 내림 · 그 외(매도 · 0 · 비유한) = 매도는 BestBid × (1 − 슬리피지) 올림 / 나머지는 BestAsk.
//      (0 · NaN 도 라우터까지 보내야 관문이 판정 · 비트 27 래치를 수행한다 — 감독자가 이상 신호를 삼키지 않음.)
//   ④ 라우터 Route(참조가 · 기준가 · 종목 캡) = 래치 · 관문 · ±30% 클램프 · 호가 격자 · 수량 캡.
//   ⑤ 사후 재검: 인터락 재판독(평가 중 트립 = 후보 폐기) · 격자 · 상·하한가 · 수량 독립 재확인(이탈 = InvariantBreach · 도달 불가 방어).
//   ⑥ OrderProposal 패킹(Kind = 1 지정가 · PriceRaw = 격자 지정가 · QtyRaw = 주 × 10^8 · OrderId 단조 · TimestampTicks = nowMicros).
//  ★정직: 호스트 DI 미결속(실 AI 모델 · L1 호가 · 기준가 피드 부재 — 가짜 배선 금지). 산출 = '후보' — 링 게시/게이트 송출은 호출자 몫이며
//   무장/송출 판정은 기존 디스패처 경로(liveArmed:false 불변 · 이 파일에 브로커 참조 0). 인터락 마스크는 라우터 비트 27 소스가 결속된
//   같은 집계기를 넘겨야 전역 동결이 감독자 차단으로 이어진다(호출자 책임 · 생성 시 검증 불가).
// ============================================================================
using System;
using System.Threading;
using MS2E.Core;
using MS2E.Infrastructure;

namespace StockManager.Engines.Strategy
{
    public enum AiSupervisorVerdict : byte
    {
        Accepted = 0, InterlockBlocked = 1, NoMarketContext = 2, StaleMarketContext = 3, FutureMarketContext = 4,
        RouterRejected = 5, Contended = 6, InvariantBreach = 7,
    }

    public enum MarketContextVerdict : byte
    {
        Ok = 0, InvalidSymbol = 1, InvalidPrice = 2, Crossed = 3, ClockRegressed = 4, SlotsExhausted = 5, Contended = 6,
    }

    /// <summary>슬롯 시장 컨텍스트 판독본(seqlock 일관 사본 · raw 10^8).</summary>
    public readonly record struct MarketContextSnapshot(long SymbolCode, long BasePriceRaw, long BestBidRaw, long BestAskRaw, long UpdatedMicros, long MaxSymbolQty);

    public sealed class AiExecutionSupervisor
    {
        public const int MaxSlots = MultiSymbolAiSignalRouter.MaxSlots;
        public const long DefaultMaxContextAgeMicros = 3_000_000;   // ReferencePriceFeedAdapter 기본(3초)과 같은 신선도
        public const int MaxSlippageBps = 1_000;
        private const long QtyScale = 100_000_000L;
        private const int MaxReadRetries = 64;

        private readonly MultiSymbolAiSignalRouter _router;
        private readonly ArmingInterlockBitmask _interlocks;
        private readonly long _maxAge, _genomeId;
        private readonly int _slippageBps;

        // 슬롯 컨텍스트(seqlock: 홀수 = 쓰는 중)
        private readonly long[] _seq, _sym, _base, _bid, _ask, _ts, _cap;
        private readonly long[] _keys;
        private readonly int[] _vals;
        private readonly int _mask, _shift;
        private int _used, _writer;

        private long _nextOrderId;
        private long _processed, _accepted, _interlockBlocked, _noContext, _stale, _future, _routerRejected, _contended, _invariant;
        private long _updates, _updateRejects;

        /// <param name="router">다종목 AI 라우터(관문 · 클램프 · 격자 · 캡).</param>
        /// <param name="interlocks">30대 인터락 집계기 — 라우터 비트 27 소스가 결속된 같은 인스턴스여야 전역 동결이 이어진다.</param>
        /// <param name="slippageBps">참조가 슬리피지(0 ~ 1000bps · 매수 = ask 위 · 매도 = bid 아래 · 이후 ±30% 클램프가 상한).</param>
        /// <param name="genomeId">후보 계보(OrderProposal.GenomeId).</param>
        public AiExecutionSupervisor(MultiSymbolAiSignalRouter router, ArmingInterlockBitmask interlocks, long maxContextAgeMicros = DefaultMaxContextAgeMicros,
                                     int slippageBps = 0, long genomeId = 0)
        {
            _router = router ?? throw new ArgumentNullException(nameof(router));
            _interlocks = interlocks ?? throw new ArgumentNullException(nameof(interlocks));
            if (maxContextAgeMicros < 1) throw new ArgumentOutOfRangeException(nameof(maxContextAgeMicros));
            if (slippageBps < 0 || slippageBps > MaxSlippageBps) throw new ArgumentOutOfRangeException(nameof(slippageBps), "0~1000bps");
            _maxAge = maxContextAgeMicros; _slippageBps = slippageBps; _genomeId = genomeId;
            _seq = new long[MaxSlots]; _sym = new long[MaxSlots]; _base = new long[MaxSlots]; _bid = new long[MaxSlots];
            _ask = new long[MaxSlots]; _ts = new long[MaxSlots]; _cap = new long[MaxSlots];
            const int bits = 7;                                                             // 128 ≥ 2 × 64
            _keys = new long[1 << bits];
            _vals = new int[1 << bits];
            _mask = (1 << bits) - 1;
            _shift = 64 - bits;
        }

        public MultiSymbolAiSignalRouter Router => _router;
        public ArmingInterlockBitmask Interlocks => _interlocks;
        public int SymbolsWithContext => Volatile.Read(ref _used);
        public long Processed => Interlocked.Read(ref _processed);
        public long AcceptedCount => Interlocked.Read(ref _accepted);
        public long InterlockBlockedRejects => Interlocked.Read(ref _interlockBlocked);
        public long NoContextRejects => Interlocked.Read(ref _noContext);
        public long StaleContextRejects => Interlocked.Read(ref _stale);
        public long FutureContextRejects => Interlocked.Read(ref _future);
        public long RouterRejects => Interlocked.Read(ref _routerRejected);
        public long Contended => Interlocked.Read(ref _contended);
        public long InvariantBreaches => Interlocked.Read(ref _invariant);
        public long ContextUpdates => Interlocked.Read(ref _updates);
        public long ContextUpdateRejects => Interlocked.Read(ref _updateRejects);

        /// <summary>시장 컨텍스트 갱신(티커 "005930" · "A005930"). δ=0.</summary>
        public MarketContextVerdict UpdateMarketContext(ReadOnlySpan<char> ticker, long basePriceRaw, long bestBidRaw, long bestAskRaw, long timestampMicros,
                                                        long maxSymbolQty = long.MaxValue)
            => UpdateMarketContext(MultiSymbolAiSignalRouter.ParseTicker(ticker), basePriceRaw, bestBidRaw, bestAskRaw, timestampMicros, maxSymbolQty);

        /// <summary>시장 컨텍스트 갱신(δ=0 · 단일 작성자 = 시세 피드). Ok 외 = 기존 컨텍스트 무변경.</summary>
        public MarketContextVerdict UpdateMarketContext(long symbolCode, long basePriceRaw, long bestBidRaw, long bestAskRaw, long timestampMicros,
                                                        long maxSymbolQty = long.MaxValue)
        {
            if (symbolCode < 1 || symbolCode > MultiSymbolAiSignalRouter.MaxSymbolCode) return RejectUpdate(MarketContextVerdict.InvalidSymbol);
            if (basePriceRaw <= 0 || bestBidRaw <= 0 || bestAskRaw <= 0) return RejectUpdate(MarketContextVerdict.InvalidPrice);
            if (bestBidRaw >= bestAskRaw) return RejectUpdate(MarketContextVerdict.Crossed);
            if (Interlocked.CompareExchange(ref _writer, 1, 0) != 0) return RejectUpdate(MarketContextVerdict.Contended);
            try
            {
                int s = Find(symbolCode);
                if (s >= 0)
                {
                    if (timestampMicros < Volatile.Read(ref _ts[s])) return RejectUpdate(MarketContextVerdict.ClockRegressed);
                    Interlocked.Increment(ref _seq[s]);                                    // 홀수 = 쓰는 중(전체 펜스)
                    Write(s, basePriceRaw, bestBidRaw, bestAskRaw, timestampMicros, maxSymbolQty);
                    Volatile.Write(ref _seq[s], _seq[s] + 1);                               // 짝수 = 공개(해제)
                }
                else
                {
                    s = Claim(symbolCode, basePriceRaw, bestBidRaw, bestAskRaw, timestampMicros, maxSymbolQty);
                    if (s < 0) return RejectUpdate(MarketContextVerdict.SlotsExhausted);
                }
                Interlocked.Increment(ref _updates);
                return MarketContextVerdict.Ok;
            }
            finally { Volatile.Write(ref _writer, 0); }
        }

        /// <summary>종목 컨텍스트 판독(관제 · δ=0). 미수신/경합 = false.</summary>
        public bool TryGetMarketContext(long symbolCode, out MarketContextSnapshot snapshot)
        {
            snapshot = default;
            int s = Find(symbolCode);
            return s >= 0 && TryRead(s, out snapshot);
        }

        /// <summary>
        /// AI 신호 1건 → 발주 후보(δ=0). Accepted 일 때만 candidate 유효(그 외 default · QtyRaw 0).
        /// routeVerdict = 라우터 판정(라우터 미도달 = Accepted 기본값 아님에 주의 → verdict 로 판별).
        /// </summary>
        public AiSupervisorVerdict ProcessSignal(in AiSignal signal, long nowMicros, out OrderProposal candidate, out AiRouteVerdict routeVerdict)
        {
            candidate = default;
            routeVerdict = AiRouteVerdict.Latched;
            Interlocked.Increment(ref _processed);
            if (_interlocks.Mask != 0) return Count(AiSupervisorVerdict.InterlockBlocked, ref _interlockBlocked);   // ① 평가 전 차단

            int s = Find(signal.SymbolCode);                                                // ② 컨텍스트
            if (s < 0) return Count(AiSupervisorVerdict.NoMarketContext, ref _noContext);
            if (!TryRead(s, out var ctx)) return Count(AiSupervisorVerdict.Contended, ref _contended);
            if (ctx.UpdatedMicros > nowMicros) return Count(AiSupervisorVerdict.FutureMarketContext, ref _future);
            if (nowMicros - ctx.UpdatedMicros > _maxAge) return Count(AiSupervisorVerdict.StaleMarketContext, ref _stale);

            bool sell = signal.Confidence < 0.0;                                            // ③ 참조가(0 · NaN = ask → 라우터 관문이 판정)
            long reference = sell ? SlipDown(ctx.BestBidRaw, _slippageBps) : SlipUp(ctx.BestAskRaw, _slippageBps);

            routeVerdict = _router.Route(in signal, reference, ctx.BasePriceRaw, nowMicros, out var o, ctx.MaxSymbolQty);   // ④
            if (routeVerdict != AiRouteVerdict.Accepted) return Count(AiSupervisorVerdict.RouterRejected, ref _routerRejected);

            if (_interlocks.Mask != 0) return Count(AiSupervisorVerdict.InterlockBlocked, ref _interlockBlocked);   // ⑤ 평가 중 트립 = 폐기
            if (!ProtectiveLimitOrderConverter.TryPriceBand(ctx.BasePriceRaw, out long lower, out long upper)
                || !KrxTickAligner.IsAligned(o.LimitRaw) || o.LimitRaw < lower || o.LimitRaw > upper
                || o.Quantity <= 0 || o.Quantity > ctx.MaxSymbolQty || o.Quantity > long.MaxValue / QtyScale
                || o.SymbolCode != signal.SymbolCode || (sell ? o.Side != -1 : o.Side != 1))
                return Count(AiSupervisorVerdict.InvariantBreach, ref _invariant);

            candidate = new OrderProposal(Interlocked.Increment(ref _nextOrderId), _genomeId, o.SymbolCode, sell ? OrderSide.Sell : OrderSide.Buy,
                                          1, new QPrice(o.LimitRaw), new QQty(o.Quantity * QtyScale), nowMicros);   // ⑥ 지정가 후보
            Interlocked.Increment(ref _accepted);
            return AiSupervisorVerdict.Accepted;
        }

        /// <summary>편의 오버로드(라우터 판정 폐기).</summary>
        public AiSupervisorVerdict ProcessSignal(in AiSignal signal, long nowMicros, out OrderProposal candidate)
            => ProcessSignal(in signal, nowMicros, out candidate, out _);

        private static AiSupervisorVerdict Count(AiSupervisorVerdict v, ref long counter) { Interlocked.Increment(ref counter); return v; }

        private MarketContextVerdict RejectUpdate(MarketContextVerdict v) { Interlocked.Increment(ref _updateRejects); return v; }

        // 매수 참조가: ask × (1 + bps) 내림(보호 = 낮게) · 포화 = 0 → 라우터 InvalidPrice
        private static long SlipUp(long ask, int bps)
        {
            if (bps == 0) return ask;
            Int128 v = (Int128)ask * (10_000 + bps) / 10_000;
            return v > long.MaxValue ? 0 : (long)v;
        }

        // 매도 참조가: bid × (1 − bps) 올림(보호 = 높게)
        private static long SlipDown(long bid, int bps)
        {
            if (bps == 0) return bid;
            return (long)(((Int128)bid * (10_000 - bps) + 9_999) / 10_000);
        }

        private void Write(int s, long basePx, long bid, long ask, long ts, long cap)
        {
            Volatile.Write(ref _base[s], basePx);
            Volatile.Write(ref _bid[s], bid);
            Volatile.Write(ref _ask[s], ask);
            Volatile.Write(ref _ts[s], ts);
            Volatile.Write(ref _cap[s], cap);
        }

        private bool TryRead(int s, out MarketContextSnapshot snapshot)
        {
            for (int i = 0; i < MaxReadRetries; i++)
            {
                long q = Volatile.Read(ref _seq[s]);
                if ((q & 1) != 0) { Thread.SpinWait(4); continue; }
                long sym = Volatile.Read(ref _sym[s]), basePx = Volatile.Read(ref _base[s]), bid = Volatile.Read(ref _bid[s]);
                long ask = Volatile.Read(ref _ask[s]), ts = Volatile.Read(ref _ts[s]), cap = Volatile.Read(ref _cap[s]);
                if (Volatile.Read(ref _seq[s]) != q) continue;
                snapshot = new MarketContextSnapshot(sym, basePx, bid, ask, ts, cap);
                return true;
            }
            snapshot = default;
            return false;
        }

        private int Home(long sym) => (int)((ulong)(sym * unchecked((long)0x9E3779B97F4A7C15UL)) >> _shift) & _mask;

        private int Find(long sym)
        {
            if (sym < 1 || sym > MultiSymbolAiSignalRouter.MaxSymbolCode) return -1;
            for (int h = Home(sym), n = 0; n <= _mask; h = (h + 1) & _mask, n++)
            {
                long k = Volatile.Read(ref _keys[h]);
                if (k == sym) return _vals[h];
                if (k == 0) return -1;
            }
            return -1;
        }

        // 단일 작성자(_writer 보유) — 슬롯 값 먼저 기록 후 키 공개(판독자는 키가 보이면 컨텍스트도 유효).
        private int Claim(long sym, long basePx, long bid, long ask, long ts, long cap)
        {
            for (int h = Home(sym), n = 0; n <= _mask; h = (h + 1) & _mask, n++)
            {
                if (_keys[h] != 0) continue;
                if (_used >= MaxSlots) return -1;
                int s = _used;
                Volatile.Write(ref _sym[s], sym);
                Write(s, basePx, bid, ask, ts, cap);
                _vals[h] = s;
                Volatile.Write(ref _keys[h], sym);
                Volatile.Write(ref _used, s + 1);
                return s;
            }
            return -1;
        }
    }
}
