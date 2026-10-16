// ============================================================================
// DualBookSimulator — [v12.18.11-DEV] AI vs Human 듀얼 가상 원장(단일 작성자 · δ=0)
//
//   ① L2 호가 표: 종목별 최신 10호가 사본(OrderBookPayload 값 복사 · 개방 주소 고정 슬롯). 양측 주문은 같은 참조 호가를 '각자' 소비한다 —
//      한쪽 체결이 공유 호가를 깎지 않는다(동일 조건 · 공정성). 가상 체결은 시장에 영향이 없으므로 잔량 차감은 날조가 된다(정직).
//   ② 시장가 체결: 매수 = 매도호가 1→10 순회 · 매도 = 매수호가 1→10 순회(잔량만큼 IOC · 잔여 수량 미체결). 체결 VWAP 와 최우선호가 차이 = 슬리피지.
//      수수료 0.015%(양방향) · 거래세 0.18%(매도) — 원 미만 절사(Int128 중간값 · 오버플로 0).
//   ③ 현금 부족 = 주문 전체 거부(부분 축소 금지 — 의도하지 않은 수량 집행 0) · 공매도 금지(보유 초과 매도 거부).
//   ④ 평가: 보유 수량 × 최우선 매수호가(청산 가능 가격 · 보수적) — 매수호가 없으면 평단(평가손익 0 · 날조 0).
//   ★브로커/주문 전송 타입 참조 0 — 이 원장에서 외부로 나가는 경로가 존재하지 않는다(Fail-Closed · 테스트가 IL 로 확증).
//   스레드 계약: 모든 변경은 ArenaMatchGovernor.Step(펌프 단일 스레드)에서만. 외부 스레드는 거버너 링에 적재만 한다.
// ============================================================================
using System;
using MS2E.Infrastructure;
using MS2E.UI.Portable.Ports.Trading;

namespace StockManager.Engines.Arena
{
    /// <summary>진영 1개의 가상 원장(고정 용량 포지션 배열 · 할당은 생성 시 1회).</summary>
    public sealed class ArenaBook
    {
        public const int MaxPositions = 32;

        internal readonly long[] PosSym = new long[MaxPositions], PosQty = new long[MaxPositions], PosCost = new long[MaxPositions];
        internal int PosCount;

        public long CashWon { get; internal set; }
        public long RealizedWon { get; internal set; }
        public long FeesWon { get; internal set; }
        public long TaxWon { get; internal set; }
        public long SlippageWon { get; internal set; }
        public long PeakWon { get; internal set; }
        public long MddWon { get; internal set; }
        public long Fills { get; internal set; }
        public long Rejects { get; internal set; }
        public int Wins { get; internal set; }
        public int Losses { get; internal set; }
        public int LastReject { get; internal set; }

        internal void Reset(long capitalWon)
        {
            Array.Clear(PosSym); Array.Clear(PosQty); Array.Clear(PosCost);
            PosCount = 0;
            CashWon = capitalWon; PeakWon = capitalWon;
            RealizedWon = FeesWon = TaxWon = SlippageWon = MddWon = Fills = Rejects = 0;
            Wins = Losses = LastReject = 0;
        }

        /// <summary>보유 수량(없으면 0).</summary>
        public long Position(long symbol)
        {
            int i = IndexOf(symbol);
            return i < 0 ? 0 : PosQty[i];
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

        private readonly long[] _qSym = new long[QuoteSlots];
        private readonly OrderBookPayload[] _q = new OrderBookPayload[QuoteSlots];
        private int _quoteCount;
        private long _quoteTableFull;

        private readonly ArenaBook[] _books = { new ArenaBook(), new ArenaBook() };

        public ArenaBook Book(int side) => _books[side == ArenaSide.Human ? ArenaSide.Human : ArenaSide.Ai];
        public int QuoteSymbols => _quoteCount;
        public long QuoteTableFull => _quoteTableFull;

        /// <summary>양측 동일 자본금으로 원장 초기화(호가 표는 유지 — 장중 개시 즉시 체결 가능).</summary>
        public void Reset(long capitalWon)
        {
            _books[ArenaSide.Ai].Reset(capitalWon);
            _books[ArenaSide.Human].Reset(capitalWon);
        }

        /// <summary>최신 10호가 반영(종목당 1슬롯 · 표 가득 = 신규 종목 무시 계수).</summary>
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

        /// <summary>시장가 가상 체결 1건. 반환 = ArenaReject(None = 체결 · 잔량만큼 부분 체결 포함). 거부도 원장에 계수.</summary>
        public int ExecuteMarket(int side, long symbol, int dir, long qty)
        {
            var book = Book(side);
            int r = Execute(book, symbol, dir, qty);
            if (r != ArenaReject.None) { book.Rejects++; book.LastReject = r; }
            return r;
        }

        private int Execute(ArenaBook book, long symbol, int dir, long qty)
        {
            if (symbol <= 0 || qty <= 0 || qty > MaxOrderQty || dir is not (1 or -1)) return ArenaReject.Invalid;
            int qi = QuoteIndex(symbol);
            if (qi < 0) return ArenaReject.NoQuote;
            ref readonly var q = ref _q[qi];
            int pi = book.IndexOf(symbol);
            if (dir < 0 && (pi < 0 || book.PosQty[pi] < qty)) return ArenaReject.InsufficientPosition;
            if (dir > 0 && pi < 0 && book.PosCount >= ArenaBook.MaxPositions) return ArenaReject.BookFull;

            // 호가 순회(잔량만큼) — 매수 = 매도호가 · 매도 = 매수호가
            long filled = 0, notional = 0, best = 0;
            for (int l = 0; l < Depth && filled < qty; l++)
            {
                long px = dir > 0 ? q.AskPrice[l] : q.BidPrice[l];
                long lq = dir > 0 ? q.AskQty[l] : q.BidQty[l];
                if (px <= 0 || px > MaxPriceWon || lq <= 0) break;
                if (best == 0) best = px;
                long take = Math.Min(qty - filled, lq);
                filled += take;
                notional += take * px;
            }
            if (filled == 0) return ArenaReject.NoLiquidity;

            long fee = Fee(notional);
            if (dir > 0)
            {
                long cost = notional + fee;
                if (cost > book.CashWon) return ArenaReject.InsufficientCash;
                if (pi < 0) { pi = book.PosCount++; book.PosSym[pi] = symbol; book.PosQty[pi] = 0; book.PosCost[pi] = 0; }
                book.CashWon -= cost;
                book.PosQty[pi] += filled;
                book.PosCost[pi] += cost;                                            // 원가 = 체결대금 + 매수 수수료
                book.SlippageWon += notional - filled * best;
            }
            else
            {
                long tax = Tax(notional);
                long held = book.PosQty[pi];
                long basis = (long)((Int128)book.PosCost[pi] * filled / held);        // 비례 원가(절사 — 잔여 원가는 남은 수량이 보유)
                long realized = notional - fee - tax - basis;
                book.CashWon += notional - fee - tax;
                book.TaxWon += tax;
                book.RealizedWon += realized;
                book.SlippageWon += filled * best - notional;
                if (realized > 0) book.Wins++; else book.Losses++;
                if (held == filled) RemovePosition(book, pi);
                else { book.PosQty[pi] = held - filled; book.PosCost[pi] -= basis; }
            }
            book.FeesWon += fee;
            book.Fills++;
            return ArenaReject.None;
        }

        private static void RemovePosition(ArenaBook book, int i)
        {
            int last = --book.PosCount;
            book.PosSym[i] = book.PosSym[last]; book.PosQty[i] = book.PosQty[last]; book.PosCost[i] = book.PosCost[last];
            book.PosSym[last] = book.PosQty[last] = book.PosCost[last] = 0;
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
