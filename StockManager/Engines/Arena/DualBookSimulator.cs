// ============================================================================
// DualBookSimulator — [v12.18.11-DEV] AI vs Human 듀얼 가상 원장(단일 작성자 · δ=0)
//                    [v12.18.13-DEV] 미시구조 체결 모델: 체결 지연 버퍼 · 지정가 대기열 순번 · 호가 소진 충격 · 잔량 이월
//
//   ① L2 호가 표: 종목별 최신 10호가 사본(개방 주소 고정 슬롯 · 갱신 버전). 양측(AI · 휴먼)은 같은 참조 호가를 '각자' 소비한다 —
//      진영 간 간섭 0(공정성). 진영 안에서는 소비량 오버레이(충격)가 같은 호가 스냅샷 동안 유지 → 같은 스냅샷에 두 번 사도 잔량은 한 번만 있다.
//      새 스냅샷 = 오버레이 초기화(가상 체결은 실 시장에 남지 않으므로 다음 실호가가 진실 — 정직한 단순화).
//   ② 체결 지연: 접수 시각 + [LatencyMin, LatencyMax](기본 30 ~ 50ms · 진영별 결정론 난수) 이후에만 체결 판단 — 판단 시점의 호가로 체결(접수 시점 호가 아님).
//      취소도 같은 지연을 거친다(그 사이 체결 가능).
//   ③ 시장가: 호가 1→10 순회(오버레이 차감 잔량) · 잔량 초과분 = 작동 상태로 이월 → 그 종목 다음 호가 갱신마다 재순회(분할 체결 · 슬리피지 = 활성화 시
//      최우선호가 대비 누적). 매수 현금은 조각마다 검사 — 부족 시 살 수 있는 만큼 · 0 이면 잔량 취소(CashExhausted).
//   ④ 지정가: 활성화 시 시장성 부분(매수 = 지정가 이하 매도호가) 즉시 순회 체결 → 잔량은 같은 가격 표시 잔량 뒤(+ 같은 원장의 앞선 내 주문)에 대기.
//      앞 잔량(QueueAhead) 감소 = (a) 그 가격 체결 틱 거래량(매수 대기 = 매도 주도 · 방향 미상은 호가로 추정) 소진 · (b) 표시 잔량이 앞 잔량보다 작아지면
//      min(앞 잔량, 표시 잔량)(취소는 뒤에서 나간다고 가정하지 않는 표준 상한) · (c) 내 가격이 최우선보다 좋아짐 = 0.
//      체결 = 앞 잔량을 넘는 거래량만큼 · 내 가격을 관통한 체결(매수 대기 아래 가격 체결) = 잔량 전부 · 반대 호가가 내 가격까지 내려오면 시장성 체결.
//   ⑤ 예약: 지정가 매수 = 수량 × 지정가 + 수수료 현금 예약 · 매도(시장가 · 지정가) = 수량 예약 — 이중 집행(초과 매수 · 중복 매도) 0.
//   ⑥ 수수료 0.015%(양방향) · 거래세 0.18%(매도) 원 미만 절사(Int128). 공매도 금지 · 현금 부족 = 활성화 시점 전량 거부(축소 0 — 시장가 추정 = 수량 × 최우선).
//   ★브로커/주문 전송 타입 참조 0(테스트가 IL 로 확증). 스레드 계약: 모든 변경은 거버너 정산 스레드(단일 작성자)에서만.
// ============================================================================
using System;
using MS2E.Infrastructure;
using MS2E.UI.Portable.Ports.Trading;

namespace StockManager.Engines.Arena
{
    /// <summary>체결 틱 1건(원 · 주). Side +1 매수 주도 · −1 매도 주도 · 0 미상(시뮬레이터가 최신 호가로 추정).</summary>
    public struct ArenaTrade { public long Symbol, PriceWon, Qty; public int Side; }

    /// <summary>주문 종류(0 시장가 · 1 지정가 · 2 전량 취소).</summary>
    public static class ArenaOrderKind
    {
        public const int Market = 0, Limit = 1, CancelAll = 2;
    }

    /// <summary>작동/지연 대기 주문 1건(고정 풀 슬롯).</summary>
    public struct ArenaWorkingOrder
    {
        public long Symbol, Qty, Filled, LimitPx, QueueAhead, DueTicks, ReservedCash, ReservedQty, RefPx, QuoteVer;
        public int Dir, Kind;
        public bool Active, Done;
    }

    /// <summary>진영 1개의 가상 원장(고정 용량 배열 · 할당은 생성 시 1회).</summary>
    public sealed class ArenaBook
    {
        public const int MaxPositions = 32, MaxOrders = 64;

        internal readonly long[] PosSym = new long[MaxPositions], PosQty = new long[MaxPositions], PosCost = new long[MaxPositions], PosReserved = new long[MaxPositions];
        internal int PosCount;
        internal readonly ArenaWorkingOrder[] Orders = new ArenaWorkingOrder[MaxOrders];
        internal int OrderCount;
        internal readonly long[] ImpactAsk, ImpactBid;
        internal ulong Rng;
        private readonly ulong _seed;

        internal ArenaBook(ulong seed, int quoteSlots, int depth)
        {
            _seed = seed | 1UL;
            ImpactAsk = new long[quoteSlots * depth]; ImpactBid = new long[quoteSlots * depth];
            Rng = _seed;
        }

        public long CashWon { get; internal set; }
        public long ReservedCashWon { get; internal set; }
        public long RealizedWon { get; internal set; }
        public long FeesWon { get; internal set; }
        public long TaxWon { get; internal set; }
        public long SlippageWon { get; internal set; }
        public long PeakWon { get; internal set; }
        public long MddWon { get; internal set; }
        public long Fills { get; internal set; }
        public long Rejects { get; internal set; }
        public long Cancels { get; internal set; }
        public int Wins { get; internal set; }
        public int Losses { get; internal set; }
        public int LastReject { get; internal set; }

        /// <summary>예약 제외 가용 현금.</summary>
        public long AvailableCashWon => CashWon - ReservedCashWon;

        internal void Reset(long capitalWon)
        {
            Array.Clear(PosSym); Array.Clear(PosQty); Array.Clear(PosCost); Array.Clear(PosReserved);
            PosCount = 0;
            Array.Clear(Orders); OrderCount = 0;
            Array.Clear(ImpactAsk); Array.Clear(ImpactBid);
            Rng = _seed;
            CashWon = capitalWon; PeakWon = capitalWon; ReservedCashWon = 0;
            RealizedWon = FeesWon = TaxWon = SlippageWon = MddWon = Fills = Rejects = Cancels = 0;
            Wins = Losses = LastReject = 0;
        }

        /// <summary>보유 수량(없으면 0).</summary>
        public long Position(long symbol)
        {
            int i = IndexOf(symbol);
            return i < 0 ? 0 : PosQty[i];
        }

        /// <summary>매도 가능 수량(보유 − 매도 예약).</summary>
        public long Sellable(long symbol)
        {
            int i = IndexOf(symbol);
            return i < 0 ? 0 : PosQty[i] - PosReserved[i];
        }

        /// <summary>지연 대기 주문 수 · 작동 중 주문 수.</summary>
        public int PendingCount { get { int n = 0; for (int i = 0; i < OrderCount; i++) if (!Orders[i].Done && !Orders[i].Active) n++; return n; } }
        public int WorkingCount { get { int n = 0; for (int i = 0; i < OrderCount; i++) if (!Orders[i].Done && Orders[i].Active) n++; return n; } }

        /// <summary>가장 오래된 작동 중 지정가의 내 앞 잔량(없으면 −1).</summary>
        public long OldestQueueAhead
        {
            get
            {
                for (int i = 0; i < OrderCount; i++)
                    if (!Orders[i].Done && Orders[i].Active && Orders[i].Kind == ArenaOrderKind.Limit) return Orders[i].QueueAhead;
                return -1;
            }
        }

        /// <summary>그 종목의 미완료 주문(지연 대기 포함) 존재 여부.</summary>
        public bool HasOpenOrder(long symbol)
        {
            for (int i = 0; i < OrderCount; i++) if (!Orders[i].Done && Orders[i].Symbol == symbol && Orders[i].Kind != ArenaOrderKind.CancelAll) return true;
            return false;
        }

        /// <summary>미완료 주문 사본(테스트 · 진단 · 반환 = 복사 수).</summary>
        public int CopyOrders(Span<ArenaWorkingOrder> destination)
        {
            int n = 0;
            for (int i = 0; i < OrderCount && n < destination.Length; i++) if (!Orders[i].Done) destination[n++] = Orders[i];
            return n;
        }

        internal int IndexOf(long symbol)
        {
            for (int i = 0; i < PosCount; i++) if (PosSym[i] == symbol) return i;
            return -1;
        }
    }

    public sealed class DualBookSimulator
    {
        /// <summary>수수료 0.015%(ppm = 1/1,000,000) · 거래세 0.18%(매도).</summary>
        public const long FeePpm = 150, TaxPpm = 1_800;
        public const int QuoteSlots = 128, Depth = 10;
        /// <summary>주문 1건 상한(가상이라도 비현실 값 거부 — 오버플로 방지 겸).</summary>
        public const long MaxOrderQty = 1_000_000, MaxPriceWon = 100_000_000;
        /// <summary>기본 체결 지연 30 ~ 50ms(UTC ticks).</summary>
        public const long DefaultLatencyMinTicks = 300_000, DefaultLatencyMaxTicks = 500_000;

        private readonly long[] _qSym = new long[QuoteSlots], _qVer = new long[QuoteSlots];
        private readonly OrderBookPayload[] _q = new OrderBookPayload[QuoteSlots];
        private int _quoteCount;
        private long _quoteTableFull;
        private readonly ArenaBook[] _books;

        public long LatencyMinTicks { get; }
        public long LatencyMaxTicks { get; }

        public DualBookSimulator(long latencyMinTicks = DefaultLatencyMinTicks, long latencyMaxTicks = DefaultLatencyMaxTicks)
        {
            if (latencyMinTicks < 0 || latencyMaxTicks < latencyMinTicks) throw new ArgumentOutOfRangeException(nameof(latencyMaxTicks));
            LatencyMinTicks = latencyMinTicks; LatencyMaxTicks = latencyMaxTicks;
            _books = new[] { new ArenaBook(0xA1, QuoteSlots, Depth), new ArenaBook(0xB2, QuoteSlots, Depth) };
        }

        public ArenaBook Book(int side) => _books[side == ArenaSide.Human ? ArenaSide.Human : ArenaSide.Ai];
        public int QuoteSymbols => _quoteCount;
        public long QuoteTableFull => _quoteTableFull;

        /// <summary>양측 동일 자본금으로 원장 초기화(호가 표는 유지 — 장중 개시 즉시 체결 가능 · 지연 난수 시드 복원 = 매치 결정론).</summary>
        public void Reset(long capitalWon)
        {
            _books[ArenaSide.Ai].Reset(capitalWon);
            _books[ArenaSide.Human].Reset(capitalWon);
        }

        /// <summary>최신 10호가 반영(종목당 1슬롯 · 버전 증가 · 양 진영 충격 오버레이 초기화 · 표 가득 = 신규 종목 무시 계수).</summary>
        public void OnQuote(in OrderBookPayload b)
        {
            if (b.SymbolCode <= 0) return;
            int i = QuoteIndex(b.SymbolCode);
            if (i < 0)
            {
                if (_quoteCount >= QuoteSlots) { _quoteTableFull++; return; }
                i = _quoteCount++;
                _qSym[i] = b.SymbolCode;
            }
            _q[i] = b;
            _qVer[i]++;
            for (int s = 0; s < _books.Length; s++)
            {
                _books[s].ImpactAsk.AsSpan(i * Depth, Depth).Clear();
                _books[s].ImpactBid.AsSpan(i * Depth, Depth).Clear();
            }
        }

        public long BestAsk(long symbol) { int i = QuoteIndex(symbol); return i < 0 ? 0 : _q[i].AskPrice[0]; }
        public long BestBid(long symbol) { int i = QuoteIndex(symbol); return i < 0 ? 0 : _q[i].BidPrice[0]; }

        private int QuoteIndex(long symbol)
        {
            for (int i = 0; i < _quoteCount; i++) if (_qSym[i] == symbol) return i;
            return -1;
        }

        public static long Fee(long notionalWon) => (long)((Int128)notionalWon * FeePpm / 1_000_000);
        public static long Tax(long notionalWon) => (long)((Int128)notionalWon * TaxPpm / 1_000_000);

        // ════════════ 접수(지연 버퍼 적재) ════════════

        /// <summary>
        /// 주문 접수 — 체결 판단은 now + 지연 이후 Process 에서. kind = ArenaOrderKind · limitPx(지정가만 · 원). 반환 = ArenaReject(접수 단계 · 거부는 원장 계수).
        /// </summary>
        public int Submit(int side, long symbol, int dir, long qty, int kind, long limitPx, long nowTicks)
        {
            var book = Book(side);
            int r = kind switch
            {
                ArenaOrderKind.CancelAll => ArenaReject.None,
                ArenaOrderKind.Market => symbol <= 0 || qty <= 0 || qty > MaxOrderQty || dir is not (1 or -1) ? ArenaReject.Invalid : ArenaReject.None,
                ArenaOrderKind.Limit => symbol <= 0 || qty <= 0 || qty > MaxOrderQty || dir is not (1 or -1) || limitPx <= 0 || limitPx > MaxPriceWon ? ArenaReject.Invalid : ArenaReject.None,
                _ => ArenaReject.Invalid,
            };
            if (r == ArenaReject.None && book.OrderCount >= ArenaBook.MaxOrders) Compact(book);
            if (r == ArenaReject.None && book.OrderCount >= ArenaBook.MaxOrders) r = ArenaReject.OrdersFull;
            if (r != ArenaReject.None) { book.Rejects++; book.LastReject = r; return r; }
            ref var o = ref book.Orders[book.OrderCount++];
            o = default;
            o.Symbol = symbol; o.Dir = dir; o.Qty = qty; o.Kind = kind; o.LimitPx = kind == ArenaOrderKind.Limit ? limitPx : 0;
            o.DueTicks = nowTicks + NextLatency(book);
            return ArenaReject.None;
        }

        private long NextLatency(ArenaBook book)
        {
            long span = LatencyMaxTicks - LatencyMinTicks;
            if (span == 0) return LatencyMinTicks;
            ulong x = book.Rng;
            x ^= x << 13; x ^= x >> 7; x ^= x << 17;
            book.Rng = x;
            return LatencyMinTicks + (long)(x % (ulong)(span + 1));
        }

        // ════════════ 정산(지연 경과 활성화 · 작동 주문 재평가) ════════════

        /// <summary>지연이 끝난 주문 활성화(접수 순서) → 새 호가가 들어온 종목의 작동 주문 재평가(시장가 잔량 재순회 · 지정가 시장성/대기열). δ=0.</summary>
        public void Process(long nowTicks)
        {
            for (int s = 0; s < _books.Length; s++)
            {
                var book = _books[s];
                for (int i = 0; i < book.OrderCount; i++)
                {
                    ref var o = ref book.Orders[i];
                    if (o.Done || o.Active || o.DueTicks > nowTicks) continue;
                    Activate(book, i);
                }
                for (int i = 0; i < book.OrderCount; i++)
                {
                    ref var o = ref book.Orders[i];
                    if (o.Done || !o.Active) continue;
                    int slot = QuoteIndex(o.Symbol);
                    if (slot < 0 || _qVer[slot] == o.QuoteVer) continue;
                    o.QuoteVer = _qVer[slot];
                    Walk(book, ref o, slot);
                    if (!o.Done && o.Kind == ArenaOrderKind.Limit) RefreshQueue(ref o, slot);
                }
                Compact(book);
            }
        }

        private void Activate(ArenaBook book, int index)
        {
            ref var o = ref book.Orders[index];
            if (o.Kind == ArenaOrderKind.CancelAll)
            {
                for (int k = 0; k < book.OrderCount; k++)
                {
                    ref var x = ref book.Orders[k];
                    if (!x.Done && x.Active && x.Kind != ArenaOrderKind.CancelAll) Cancel(book, ref x);
                }
                o.Done = true;
                return;
            }
            int slot = QuoteIndex(o.Symbol);
            if (slot < 0) { Reject(book, ref o, ArenaReject.NoQuote); return; }
            ref readonly var q = ref _q[slot];
            if (o.Dir > 0)
            {
                long best = q.AskPrice[0];
                if (o.Kind == ArenaOrderKind.Market && best <= 0) { Reject(book, ref o, ArenaReject.NoLiquidity); return; }
                if (book.IndexOf(o.Symbol) < 0 && book.PosCount >= ArenaBook.MaxPositions) { Reject(book, ref o, ArenaReject.BookFull); return; }
                long refPx = o.Kind == ArenaOrderKind.Limit ? o.LimitPx : best;
                long est = o.Qty * refPx;
                long need = est + Fee(est);
                if (need > book.AvailableCashWon) { Reject(book, ref o, ArenaReject.InsufficientCash); return; }
                if (o.Kind == ArenaOrderKind.Limit) { o.ReservedCash = need; book.ReservedCashWon += need; }
                o.RefPx = best;
            }
            else
            {
                if (book.Sellable(o.Symbol) < o.Qty) { Reject(book, ref o, ArenaReject.InsufficientPosition); return; }
                book.PosReserved[book.IndexOf(o.Symbol)] += o.Qty;
                o.ReservedQty = o.Qty;
                o.RefPx = q.BidPrice[0];
            }
            o.Active = true;
            o.QuoteVer = _qVer[slot];
            Walk(book, ref o, slot);
            if (!o.Done && o.Kind == ArenaOrderKind.Limit) o.QueueAhead = InitialQueueAhead(book, index, slot);
        }

        private static void Reject(ArenaBook book, ref ArenaWorkingOrder o, int code)
        {
            o.Done = true;
            book.Rejects++; book.LastReject = code;
        }

        private static void Cancel(ArenaBook book, ref ArenaWorkingOrder o)
        {
            Release(book, ref o);
            o.Done = true;
            book.Cancels++;
        }

        private static void Release(ArenaBook book, ref ArenaWorkingOrder o)
        {
            if (o.ReservedCash > 0) { book.ReservedCashWon -= o.ReservedCash; o.ReservedCash = 0; }
            if (o.ReservedQty > 0)
            {
                int pi = book.IndexOf(o.Symbol);
                if (pi >= 0) book.PosReserved[pi] -= o.ReservedQty;
                o.ReservedQty = 0;
            }
        }

        // 반대 호가 순회(오버레이 차감 · 지정가 = 가격 한도) → 집계 1회 체결
        private void Walk(ArenaBook book, ref ArenaWorkingOrder o, int slot)
        {
            ref readonly var q = ref _q[slot];
            var impact = (o.Dir > 0 ? book.ImpactAsk : book.ImpactBid).AsSpan(slot * Depth, Depth);
            long filled = 0, notional = 0;
            bool cashOut = false;
            long budget = o.Dir > 0 ? book.AvailableCashWon + o.ReservedCash : 0;
            for (int l = 0; l < Depth && filled < o.Qty; l++)
            {
                long px = o.Dir > 0 ? q.AskPrice[l] : q.BidPrice[l];
                if (px <= 0 || px > MaxPriceWon) break;
                if (o.Kind == ArenaOrderKind.Limit && (o.Dir > 0 ? px > o.LimitPx : px < o.LimitPx)) break;
                long lq = (o.Dir > 0 ? q.AskQty[l] : q.BidQty[l]) - impact[l];
                if (lq <= 0) continue;
                long take = Math.Min(o.Qty - filled, lq);
                if (o.Dir > 0)
                {
                    long room = budget - notional - Fee(notional);
                    long afford = room <= 0 ? 0 : (long)((Int128)room * 1_000_000 / ((Int128)px * (1_000_000 + FeePpm)));
                    while (afford > 0 && notional + afford * px + Fee(notional + afford * px) > budget) afford--;
                    if (afford < take) { take = afford; cashOut = true; }
                    if (take <= 0) break;
                }
                impact[l] += take;
                filled += take;
                notional += take * px;
                if (cashOut) break;
            }
            if (filled > 0) Fill(book, ref o, filled, notional, aggressive: true);
            if (cashOut && !o.Done)
            {
                Release(book, ref o);
                o.Done = true;
                book.LastReject = ArenaReject.CashExhausted;
                book.Cancels++;
            }
        }

        // 체결 1건 반영(현금 · 포지션 · 수수료 · 세금 · 실현손익 · 예약 해제)
        private static void Fill(ArenaBook book, ref ArenaWorkingOrder o, long qty, long notional, bool aggressive)
        {
            long fee = Fee(notional);
            if (o.Dir > 0)
            {
                long cost = notional + fee;
                int pi = book.IndexOf(o.Symbol);
                if (pi < 0) { pi = book.PosCount++; book.PosSym[pi] = o.Symbol; book.PosQty[pi] = 0; book.PosCost[pi] = 0; book.PosReserved[pi] = 0; }
                book.CashWon -= cost;
                book.PosQty[pi] += qty;
                book.PosCost[pi] += cost;
                if (o.ReservedCash > 0)
                {
                    long rel = qty >= o.Qty ? o.ReservedCash : (long)((Int128)o.ReservedCash * qty / o.Qty);
                    o.ReservedCash -= rel; book.ReservedCashWon -= rel;
                }
                if (aggressive && o.Kind == ArenaOrderKind.Market && o.RefPx > 0) book.SlippageWon += notional - qty * o.RefPx;
            }
            else
            {
                long tax = Tax(notional);
                int pi = book.IndexOf(o.Symbol);
                long held = book.PosQty[pi];
                long basis = (long)((Int128)book.PosCost[pi] * qty / held);              // 비례 원가(절사 — 잔여 원가는 남은 수량이 보유)
                long realized = notional - fee - tax - basis;
                book.CashWon += notional - fee - tax;
                book.TaxWon += tax;
                book.RealizedWon += realized;
                if (realized > 0) book.Wins++; else book.Losses++;
                book.PosReserved[pi] -= qty;
                o.ReservedQty -= qty;
                if (aggressive && o.Kind == ArenaOrderKind.Market && o.RefPx > 0) book.SlippageWon += qty * o.RefPx - notional;
                if (held == qty) RemovePosition(book, pi);
                else { book.PosQty[pi] = held - qty; book.PosCost[pi] -= basis; }
            }
            book.FeesWon += fee;
            book.Fills++;
            o.Qty -= qty;
            o.Filled += qty;
            if (o.Qty == 0) { Release(book, ref o); o.Done = true; }
        }

        private static void RemovePosition(ArenaBook book, int i)
        {
            int last = --book.PosCount;
            book.PosSym[i] = book.PosSym[last]; book.PosQty[i] = book.PosQty[last]; book.PosCost[i] = book.PosCost[last]; book.PosReserved[i] = book.PosReserved[last];
            book.PosSym[last] = book.PosQty[last] = book.PosCost[last] = book.PosReserved[last] = 0;
        }

        // ── 대기열 ──

        // 진입 시 앞 잔량 = 같은 가격 표시 잔량(+ 같은 원장의 앞선 내 대기 주문) · 최우선보다 좋은 가격/호가 사이 빈 가격 = 0 · 10호가 밖 = 10번째 잔량(보수 추정)
        private long InitialQueueAhead(ArenaBook book, int index, int slot)
        {
            ref readonly var o = ref book.Orders[index];
            ref readonly var q = ref _q[slot];
            long ahead = 0;
            bool found = false, beyond = true;
            for (int l = 0; l < Depth; l++)
            {
                long px = o.Dir > 0 ? q.BidPrice[l] : q.AskPrice[l];
                if (px <= 0) { beyond = false; break; }
                if (px == o.LimitPx) { ahead = Math.Max(0, o.Dir > 0 ? q.BidQty[l] : q.AskQty[l]); found = true; beyond = false; break; }
                if (o.Dir > 0 ? px < o.LimitPx : px > o.LimitPx) { beyond = false; break; }   // 내 가격이 이 호가보다 좋음 = 빈 가격(앞 0)
            }
            if (!found && beyond) ahead = Math.Max(0, o.Dir > 0 ? q.BidQty[Depth - 1] : q.AskQty[Depth - 1]);
            for (int k = 0; k < index; k++)
            {
                ref readonly var x = ref book.Orders[k];
                if (!x.Done && x.Active && x.Kind == ArenaOrderKind.Limit && x.Symbol == o.Symbol && x.Dir == o.Dir && x.LimitPx == o.LimitPx) ahead += x.Qty;
            }
            return ahead;
        }

        // 새 호가: 표시 잔량이 앞 잔량보다 작으면 축소 · 내 가격이 최우선보다 좋아지면 0
        private void RefreshQueue(ref ArenaWorkingOrder o, int slot)
        {
            ref readonly var q = ref _q[slot];
            long best = o.Dir > 0 ? q.BidPrice[0] : q.AskPrice[0];
            if (best <= 0 || (o.Dir > 0 ? best < o.LimitPx : best > o.LimitPx)) { o.QueueAhead = 0; return; }
            for (int l = 0; l < Depth; l++)
            {
                long px = o.Dir > 0 ? q.BidPrice[l] : q.AskPrice[l];
                if (px <= 0) return;
                if (px == o.LimitPx)
                {
                    long shown = Math.Max(0, o.Dir > 0 ? q.BidQty[l] : q.AskQty[l]);
                    if (shown < o.QueueAhead) o.QueueAhead = shown;
                    return;
                }
            }
        }

        /// <summary>
        /// 체결 틱 1건 → 양 진영의 같은 종목 작동 지정가 평가. 방향 미상 = 최신 호가로 추정(≥ 매도 1호가 = 매수 주도 · ≤ 매수 1호가 = 매도 주도).
        /// 매수 대기 P: 체결가 &lt; P = 잔량 전부(관통) · 체결가 = P 이고 매도 주도/미상 = 거래량이 앞 잔량을 먼저 소진 → 남는 만큼 체결. 매도 대기 대칭. δ=0.
        /// </summary>
        public void OnTrade(in ArenaTrade t)
        {
            if (t.Symbol <= 0 || t.PriceWon <= 0 || t.Qty <= 0) return;
            int side = t.Side;
            if (side == 0)
            {
                int slot = QuoteIndex(t.Symbol);
                if (slot >= 0)
                {
                    long ask = _q[slot].AskPrice[0], bid = _q[slot].BidPrice[0];
                    side = ask > 0 && t.PriceWon >= ask ? 1 : bid > 0 && t.PriceWon <= bid ? -1 : 0;
                }
            }
            for (int s = 0; s < _books.Length; s++)
            {
                var book = _books[s];
                for (int i = 0; i < book.OrderCount; i++)
                {
                    ref var o = ref book.Orders[i];
                    if (o.Done || !o.Active || o.Kind != ArenaOrderKind.Limit || o.Symbol != t.Symbol) continue;
                    long fill = 0;
                    if (o.Dir > 0 ? t.PriceWon < o.LimitPx : t.PriceWon > o.LimitPx) fill = o.Qty;                // 관통
                    else if (t.PriceWon == o.LimitPx && (o.Dir > 0 ? side <= 0 : side >= 0))
                    {
                        long v = t.Qty, used = Math.Min(v, o.QueueAhead);
                        o.QueueAhead -= used;
                        fill = Math.Min(v - used, o.Qty);
                    }
                    if (fill <= 0) continue;
                    if (o.Dir < 0) fill = Math.Min(fill, book.Position(o.Symbol));
                    if (fill > 0) Fill(book, ref o, fill, fill * o.LimitPx, aggressive: false);
                }
                Compact(book);
            }
        }

        // 완료 주문 제거(접수 순서 보존 · 안정 압축)
        private static void Compact(ArenaBook book)
        {
            int w = 0;
            for (int r = 0; r < book.OrderCount; r++)
            {
                if (book.Orders[r].Done) continue;
                if (w != r) book.Orders[w] = book.Orders[r];
                w++;
            }
            for (int k = w; k < book.OrderCount; k++) book.Orders[k] = default;
            book.OrderCount = w;
        }

        /// <summary>시가평가(보유 × 최우선 매수호가 · 없으면 원가) → 자산 · 미실현 · 고점 · MDD 갱신. 반환 = 자산(원).</summary>
        public long MarkToMarket(int side, out long unrealizedWon)
        {
            var book = Book(side);
            long value = 0, cost = 0;
            for (int i = 0; i < book.PosCount; i++)
            {
                long bid = BestBid(book.PosSym[i]);
                value += bid > 0 ? book.PosQty[i] * bid : book.PosCost[i];
                cost += book.PosCost[i];
            }
            unrealizedWon = value - cost;
            long equity = book.CashWon + value;
            if (equity > book.PeakWon) book.PeakWon = equity;
            if (book.PeakWon - equity > book.MddWon) book.MddWon = book.PeakWon - equity;
            return equity;
        }
    }
}
