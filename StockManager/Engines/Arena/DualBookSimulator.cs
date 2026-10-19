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
//   ⑥ 수수료 0.015%(양방향) · 거래세 0.18%(매도) 원 미만 절사(Int128). [v12.18.23-DEV 이전: 공매도 금지 → ⑫] · 현금 부족 = 활성화 시점 전량 거부(축소 0 — 시장가 추정 = 수량 × 최우선).
//   [v12.18.14-DEV] 비상 청산(Flatten) = 지연 1회 후 미체결 전량(지연 대기 포함) 취소 + 보유 전 종목 시장가 매도 동시 활성 · 진영별 사후 분석
//   (ArenaTradeAnalytics — 체결 · 시가평가 · 접수 훅) · DOM 래더 투영(FillDepth — 10호가 + 가격별 내 대기 지정가/앞 잔량).
//   [v12.18.18-DEV] 기관급 집행 · 시장 충격:
//    ⑦ 빙산(Iceberg) 지정가: 노출 수량(Peak)만 대기열에 걸림(Visible) — 노출분이 수동 체결로 소진되면 은닉 잔량에서 min(Peak, 잔량) 리필 →
//       그 가격 대기열 맨 뒤로 재배치(앞 잔량 = 최신 표시 잔량 + 같은 가격 내 다른 대기 주문 노출분 전부 · 리필 직후 남은 체결 거래량은 뒤 주문 몫으로 보수 처리).
//       시장성 부분(활성화 순회 · 반대 호가 하강)과 관통 체결은 은닉분까지 전량 체결(정보 우위 없음). 예약 = 은닉분 포함 전량(이중 집행 0).
//    ⑧ TWAP 시장가: 노출 = 슬라이스 · Interval 마다 min(잔량, 미체결 방출분 + 슬라이스) 방출 → 호가 갱신이 없어도 방출 시점에 재순회.
//    ⑨ 유동성 진공(Liquidity Vacuum): 시장가 순회가 같은 호가 스냅샷의 어느 단 표시 잔량을 70% 이상 소진하면 그 진영 · 종목 · 방향에 진공 발동 →
//       창(500ms) 안 후속 시장가는 단계 k 마다 (2^k − 1) 호가 단위 페널티(매수 +, 매도 −)를 체결가에 얹는다(창 안 재발동 = k+1 · 상한 6 · 창 경과 = 해소).
//       페널티는 슬리피지 · 진공 페널티 누적에 포함. 진영 간 간섭 0(오버레이와 같은 원칙).
//    ⑩ 마진콜(MarginCall): 동결 래치 → 지연 대기(제어 주문 포함) 전부 소거 + 작동 주문 취소 + 보유 전 종목 시장가 매도 즉시 활성(지연 0) — 정산 스레드 1회 호출로 원자.
//       동결 원장 Submit = RiskLocked(취소 · 청산 포함 — 강제 청산 매도 보호). 해제 = 다음 Reset(라운드 경계)뿐.
//   [v12.18.23-DEV] 초현실 2단계:
//    ⑪ 대기열 취소 이탈(Cancel Depletion): 새 호가에서 내 가격 표시 잔량 감소분 ΔD = D(t−1) − D(t) 중 직전 스냅샷 이후 그 가격 체결량 V 를 뺀
//       C = max(0, ΔD − V) 를 취소로 본다. 취소는 대기열 전체에 균등 분포(비례 감축 기대값 · 결정론) → 내 앞 몫 = C × min(앞 잔량, D(t−1)) ÷ D(t−1) 만큼
//       앞 잔량 즉시 단축(체결분은 체결 틱이 이미 소진 · 이중 차감 0). 이후 표시 잔량 상한(min) 은 종전대로. 잔량 증가 = 뒤에 붙은 신규(앞 불변).
//    ⑫ 가상 공매도: 롱 없음(무포지션 · 숏) 매도 = 숏 개시(PosQty < 0). 순매도 대금(대금 − 수수료 − 거래세)은 현금에 들어오되 대금 × (100 + 50)% 가
//       담보로 잠긴다(가용 현금 = 현금 − 예약 − 숏 담보 → 무한 레버리지 차단). 숏 상태 매수 = 숏커버(숏 수량 상한 · 담보 비례 해제 · 실현손익 =
//       비례 순매도 대금 − 커버 비용). 한 주문은 0 을 가로지르지 않는다(롱 청산 ≠ 숏 개시) · 롱 개시와 숏 개시는 같은 종목에 동시 진행 불가(OppositeExposure).
//       시가평가: 숏 = 최우선 매도호가(되사는 값) · 자산 = 현금 + Σ 수량 × 표시가(숏은 음수 부채) — 하드 리스크(−3%) 판정이 숏 평가손을 그대로 반영.
//       비상 청산 · 마진콜 = 롱 시장가 매도 + 숏 시장가 커버 매수(강제 — 현금 한도 무시 · 음수 현금 = 실제 손실) · 대주 수수료 없음(정직한 단순화).
//   ★브로커/주문 전송 타입 참조 0(테스트가 IL 로 확증). 스레드 계약: 모든 변경은 거버너 정산 스레드(단일 작성자)에서만.
// ============================================================================
using System;
using MS2E.Infrastructure;
using MS2E.UI.Portable.Ports.Trading;
using StockManager.Engines.Arena.Strategy;

namespace StockManager.Engines.Arena
{
    /// <summary>체결 틱 1건(원 · 주). Side +1 매수 주도 · −1 매도 주도 · 0 미상(시뮬레이터가 최신 호가로 추정).</summary>
    public struct ArenaTrade { public long Symbol, PriceWon, Qty; public int Side; }

    /// <summary>주문 종류(0 시장가 · 1 지정가 · 2 전량 취소 · 3 비상 전량 청산).</summary>
    public static class ArenaOrderKind
    {
        public const int Market = 0, Limit = 1, CancelAll = 2, Flatten = 3;
        public static bool IsControl(int kind) => kind is CancelAll or Flatten;
    }

    /// <summary>작동/지연 대기 주문 1건(고정 풀 슬롯). [v12.18.18-DEV] Peak &gt; 0 = 빙산 지정가(노출 상한) 또는 TWAP 시장가(슬라이스 · IntervalTicks &gt; 0).</summary>
    public struct ArenaWorkingOrder
    {
        public long Symbol, Qty, Filled, LimitPx, QueueAhead, DueTicks, ReservedCash, ReservedQty, RefPx, QuoteVer;
        public long Peak, Visible, IntervalTicks, NextReleaseTicks, Refills;
        /// <summary>[v12.18.23-DEV] 대기열 취소 이탈: 직전 스냅샷의 내 가격 표시 잔량(0 = 미상) · 그 이후 내 가격 체결량.</summary>
        public long LastShown, TradedAtPx;
        public int Dir, Kind;
        /// <summary>[v12.18.23-DEV] 0 = 개시(롱 매수 · 숏 매도) · 1 = 청산(롱 매도 · 숏커버 매수) — 활성화 시 확정.</summary>
        public int Mode;
        public bool Active, Done;
        /// <summary>[v12.18.23-DEV] 비상 청산 · 마진콜 생성 주문(숏커버 현금 한도 무시).</summary>
        public bool Forced;
    }

    /// <summary>진영 1개의 가상 원장(고정 용량 배열 · 할당은 생성 시 1회).</summary>
    public sealed class ArenaBook
    {
        public const int MaxPositions = 32, MaxOrders = 64;

        internal readonly long[] PosSym = new long[MaxPositions], PosQty = new long[MaxPositions], PosCost = new long[MaxPositions], PosReserved = new long[MaxPositions];
        /// <summary>[v12.18.23-DEV] 숏 포지션 담보 잠금(원 · 롱 = 0).</summary>
        internal readonly long[] PosLock = new long[MaxPositions];
        internal int PosCount;
        internal readonly ArenaWorkingOrder[] Orders = new ArenaWorkingOrder[MaxOrders];
        internal int OrderCount;
        internal readonly long[] ImpactAsk, ImpactBid;
        /// <summary>[v12.18.18-DEV] 유동성 진공 단계 · 해소 시각(호가 슬롯 × 2 — 0 매수측 소진 · 1 매도측 소진).</summary>
        internal readonly int[] VacLevel;
        internal readonly long[] VacUntil;
        internal ulong Rng;
        private readonly ulong _seed;

        internal ArenaBook(ulong seed, int quoteSlots, int depth)
        {
            _seed = seed | 1UL;
            ImpactAsk = new long[quoteSlots * depth]; ImpactBid = new long[quoteSlots * depth];
            VacLevel = new int[quoteSlots * 2]; VacUntil = new long[quoteSlots * 2];
            Rng = _seed;
        }

        /// <summary>[v12.18.14-DEV] 사후 복기 분석(원장과 같은 단일 작성자).</summary>
        public ArenaTradeAnalytics Analytics { get; } = new();
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
        /// <summary>[v12.18.18-DEV] 유동성 진공 발동 · 진공 페널티(원) · 빙산 리필 누적 · 리스크 동결(마진콜 래치 — Reset 에서만 해제).</summary>
        public long VacuumEvents { get; internal set; }
        public long VacuumPenaltyWon { get; internal set; }
        public long IcebergRefills { get; internal set; }
        public bool Frozen { get; internal set; }
        /// <summary>[v12.18.23-DEV] 숏 담보 잠금 합 · 숏 개시 체결 · 숏커버 체결 · 대기열 취소 이탈로 단축된 앞 잔량 누적.</summary>
        public long ShortLockedWon { get; internal set; }
        public long ShortOpenFills { get; internal set; }
        public long ShortCoverFills { get; internal set; }
        public long QueueCancelDepletedQty { get; internal set; }

        /// <summary>예약 · 숏 담보 제외 가용 현금.</summary>
        public long AvailableCashWon => CashWon - ReservedCashWon - ShortLockedWon;

        /// <summary>롱 · 숏 보유 종목 수.</summary>
        public int LongPositions { get { int n = 0; for (int i = 0; i < PosCount; i++) if (PosQty[i] > 0) n++; return n; } }
        public int ShortPositions { get { int n = 0; for (int i = 0; i < PosCount; i++) if (PosQty[i] < 0) n++; return n; } }

        internal void Reset(long capitalWon)
        {
            Array.Clear(PosSym); Array.Clear(PosQty); Array.Clear(PosCost); Array.Clear(PosReserved); Array.Clear(PosLock);
            PosCount = 0;
            Array.Clear(Orders); OrderCount = 0;
            Array.Clear(ImpactAsk); Array.Clear(ImpactBid);
            Array.Clear(VacLevel); Array.Clear(VacUntil);
            Rng = _seed;
            Analytics.Reset();
            CashWon = capitalWon; PeakWon = capitalWon; ReservedCashWon = 0;
            RealizedWon = FeesWon = TaxWon = SlippageWon = MddWon = Fills = Rejects = Cancels = 0;
            Wins = Losses = LastReject = 0;
            VacuumEvents = VacuumPenaltyWon = IcebergRefills = 0;
            Frozen = false;
            ShortLockedWon = ShortOpenFills = ShortCoverFills = QueueCancelDepletedQty = 0;
        }

        /// <summary>보유 수량(없으면 0 · [v12.18.23-DEV] 숏 = 음수).</summary>
        public long Position(long symbol)
        {
            int i = IndexOf(symbol);
            return i < 0 ? 0 : PosQty[i];
        }

        /// <summary>롱 매도(청산) 가능 수량(롱 보유 − 매도 예약 · 숏/무포지션 = 0).</summary>
        public long Sellable(long symbol)
        {
            int i = IndexOf(symbol);
            return i < 0 || PosQty[i] <= 0 ? 0 : PosQty[i] - PosReserved[i];
        }

        /// <summary>[v12.18.23-DEV] 숏커버 가능 수량(숏 − 커버 예약 · 롱/무포지션 = 0).</summary>
        public long Coverable(long symbol)
        {
            int i = IndexOf(symbol);
            return i < 0 || PosQty[i] >= 0 ? 0 : -PosQty[i] - PosReserved[i];
        }

        /// <summary>[v12.18.23-DEV] 그 종목에 dir 방향 '개시' 작동 주문이 있는가(롱/숏 동시 개시 차단).</summary>
        internal bool HasActiveOpen(long symbol, int dir)
        {
            for (int i = 0; i < OrderCount; i++)
            {
                ref readonly var o = ref Orders[i];
                if (!o.Done && o.Active && o.Mode == 0 && o.Dir == dir && o.Symbol == symbol && !ArenaOrderKind.IsControl(o.Kind)) return true;
            }
            return false;
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
            for (int i = 0; i < OrderCount; i++) if (!Orders[i].Done && Orders[i].Symbol == symbol && !ArenaOrderKind.IsControl(Orders[i].Kind)) return true;
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
        /// <summary>[v12.18.18-DEV] 유동성 진공: 단일 스냅샷 단 소진 임계(%) · 창(500ms) · 단계 상한(페널티 2^6 − 1 = 63 호가 단위).</summary>
        public const int VacuumThresholdPct = 70, MaxVacuumLevel = 6;
        /// <summary>[v12.18.23-DEV] 숏 증거금(대금 대비 % · 담보 잠금 = 대금 × (100 + 이 값)%).</summary>
        public const int ShortMarginPct = 50;
        private const long ForcedBudget = long.MaxValue / 4;
        public const long VacuumWindowTicks = 5_000_000;

        private readonly long[] _qSym = new long[QuoteSlots], _qVer = new long[QuoteSlots];
        private readonly OrderBookPayload[] _q = new OrderBookPayload[QuoteSlots];
        private int _quoteCount;
        private long _quoteTableFull, _now;
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

        /// <summary>[v12.18.14-DEV] 시장 시각(체결 틱 처리 전 거버너가 설정 · Process 가 갱신) — 사후 분석 보유 시간 기준.</summary>
        public void SetClock(long nowTicks) => _now = nowTicks;
        public long Clock => _now;
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
        /// <param name="peakQty">[v12.18.18-DEV] 지정가 = 빙산 노출 수량 · 시장가 = TWAP 슬라이스(intervalTicks &gt; 0 필수) · 0 = 일반. peak ≥ qty = 일반.</param>
        /// <param name="intervalTicks">TWAP 방출 간격(시장가 전용 · UTC ticks).</param>
        public int Submit(int side, long symbol, int dir, long qty, int kind, long limitPx, long nowTicks, long peakQty = 0, long intervalTicks = 0)
        {
            var book = Book(side);
            if (book.Frozen) { book.Rejects++; book.LastReject = ArenaReject.RiskLocked; return ArenaReject.RiskLocked; }
            if (peakQty < 0 || intervalTicks < 0 || (intervalTicks > 0 && (kind != ArenaOrderKind.Market || peakQty <= 0))
                || (peakQty > 0 && kind == ArenaOrderKind.Market && intervalTicks == 0)) { book.Rejects++; book.LastReject = ArenaReject.Invalid; return ArenaReject.Invalid; }
            if (peakQty >= qty) { peakQty = 0; intervalTicks = 0; }
            int r = kind switch
            {
                ArenaOrderKind.CancelAll or ArenaOrderKind.Flatten => ArenaReject.None,
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
            o.Peak = peakQty; o.IntervalTicks = intervalTicks;
            o.DueTicks = nowTicks + NextLatency(book);
            if (!ArenaOrderKind.IsControl(kind))
            {
                long refPx = kind == ArenaOrderKind.Limit ? limitPx : dir > 0 ? BestAsk(symbol) : BestBid(symbol);
                book.Analytics.OnSubmit(nowTicks, refPx > 0 ? qty * refPx : 0);     // 틸트 기준선/경보(취소 · 청산 = 보호 행동 → 제외)
            }
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
            _now = nowTicks;
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
                    bool release = o.IntervalTicks > 0 && nowTicks >= o.NextReleaseTicks;
                    if (release)                                                    // TWAP 슬라이스 방출(미체결 방출분 누적 · 잔량 상한)
                    {
                        o.Visible = Math.Min(o.Qty, o.Visible + o.Peak);
                        o.NextReleaseTicks = nowTicks + o.IntervalTicks;
                    }
                    int slot = QuoteIndex(o.Symbol);
                    if (slot < 0 || (_qVer[slot] == o.QuoteVer && !release)) continue;
                    o.QuoteVer = _qVer[slot];
                    Walk(book, ref o, slot);
                    if (!o.Done && o.Kind == ArenaOrderKind.Limit)
                    {
                        if (o.Peak > 0 && o.Visible == 0) Requeue(book, i, slot);       // 반대 호가 하강으로 노출분 소진 = 리필 · 맨 뒤
                        else RefreshQueue(book, ref o, slot);
                    }
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
            if (o.Kind == ArenaOrderKind.Flatten) { Flatten(book, index); return; }
            int slot = QuoteIndex(o.Symbol);
            if (slot < 0) { Reject(book, ref o, ArenaReject.NoQuote); return; }
            ref readonly var q = ref _q[slot];
            int pi = book.IndexOf(o.Symbol);
            long pos = pi < 0 ? 0 : book.PosQty[pi];
            if (o.Dir > 0)
            {
                long best = q.AskPrice[0];
                if (o.Kind == ArenaOrderKind.Market && best <= 0) { Reject(book, ref o, ArenaReject.NoLiquidity); return; }
                long refPx = o.Kind == ArenaOrderKind.Limit ? o.LimitPx : best;
                long est = o.Qty * refPx;
                long need = est + Fee(est);
                if (pos < 0)                                                        // [v12.18.23-DEV] 숏커버 — 숏 수량 상한 · 담보 비례 해제분까지 가용
                {
                    if (book.Coverable(o.Symbol) < o.Qty) { Reject(book, ref o, ArenaReject.InsufficientPosition); return; }
                    long lockRel = (long)((Int128)book.PosLock[pi] * o.Qty / -pos);
                    if (!o.Forced && need > book.AvailableCashWon + lockRel) { Reject(book, ref o, ArenaReject.InsufficientCash); return; }
                    book.PosReserved[pi] += o.Qty;
                    o.ReservedQty = o.Qty;
                    o.Mode = 1;
                    if (o.Kind == ArenaOrderKind.Limit && need > lockRel) { o.ReservedCash = need - lockRel; book.ReservedCashWon += o.ReservedCash; }
                }
                else
                {
                    if (book.HasActiveOpen(o.Symbol, -1)) { Reject(book, ref o, ArenaReject.OppositeExposure); return; }
                    if (pi < 0 && book.PosCount >= ArenaBook.MaxPositions) { Reject(book, ref o, ArenaReject.BookFull); return; }
                    if (need > book.AvailableCashWon) { Reject(book, ref o, ArenaReject.InsufficientCash); return; }
                    if (o.Kind == ArenaOrderKind.Limit) { o.ReservedCash = need; book.ReservedCashWon += need; }
                }
                o.RefPx = best;
            }
            else if (pos > 0)                                                       // 롱 청산(0 을 가로지르지 않음 — 초과 = 거부)
            {
                if (book.Sellable(o.Symbol) < o.Qty) { Reject(book, ref o, ArenaReject.InsufficientPosition); return; }
                book.PosReserved[pi] += o.Qty;
                o.ReservedQty = o.Qty;
                o.Mode = 1;
                o.RefPx = q.BidPrice[0];
            }
            else                                                                    // [v12.18.23-DEV] 숏 개시 — 증거금(대금 50%) + 수수료 + 세금 현금 예약
            {
                long best = q.BidPrice[0];
                if (o.Kind == ArenaOrderKind.Market && best <= 0) { Reject(book, ref o, ArenaReject.NoLiquidity); return; }
                if (book.HasActiveOpen(o.Symbol, 1)) { Reject(book, ref o, ArenaReject.OppositeExposure); return; }
                if (pi < 0 && book.PosCount >= ArenaBook.MaxPositions) { Reject(book, ref o, ArenaReject.BookFull); return; }
                long estPx = o.Kind == ArenaOrderKind.Limit ? Math.Max(o.LimitPx, best) : best;   // 매도 체결가 상한(순회 = 최우선 이하 · 수동 = 지정가)
                long est = o.Qty * estPx;
                long need = (long)((Int128)est * ShortMarginPct / 100) + Fee(est) + Tax(est);
                if (need > book.AvailableCashWon) { Reject(book, ref o, ArenaReject.InsufficientCash); return; }
                o.ReservedCash = need; book.ReservedCashWon += need;
                o.RefPx = best;
            }
            o.Active = true;
            o.QuoteVer = _qVer[slot];
            if (o.IntervalTicks > 0) { o.Visible = Math.Min(o.Peak, o.Qty); o.NextReleaseTicks = _now + o.IntervalTicks; }
            Walk(book, ref o, slot);
            if (!o.Done && o.Kind == ArenaOrderKind.Limit)
            {
                if (o.Peak > 0) o.Visible = Math.Min(o.Peak, o.Qty);                // 시장성 순회는 은닉분까지 · 잔량부터 노출 상한
                o.QueueAhead = InitialQueueAhead(book, index, slot, behindAll: false);
                o.LastShown = ShownAt(slot, o.Dir, o.LimitPx); o.TradedAtPx = 0;
            }
        }

        /// <summary>
        /// [v12.18.18-DEV] 마진콜(정산 스레드 · δ=0 · 원자): 동결 래치 → 지연 대기 제어 주문(취소 · 청산) 소거 → 비상 청산(미체결 전량 취소 + 보유 전 종목 시장가 매도 즉시 활성).
        /// 호가 잔량 초과분은 작동 매도로 남아 다음 호가마다 재순회(전량 청산까지). 이미 동결 = 무동작(반환 false).
        /// </summary>
        public bool MarginCall(int side)
        {
            var book = Book(side);
            if (book.Frozen) return false;
            book.Frozen = true;
            for (int k = 0; k < book.OrderCount; k++)
            {
                ref var x = ref book.Orders[k];
                if (!x.Done && !x.Active && ArenaOrderKind.IsControl(x.Kind)) x.Done = true;   // 지연 중 취소가 강제 청산 매도를 지우는 경로 차단
            }
            Flatten(book, -1);
            Compact(book);
            return true;
        }

        // 비상 청산: 다른 미체결 전부(지연 대기 포함) 취소 → 보유 종목마다 시장가 매도 슬롯(취소로 빈 슬롯 재사용 · 포지션 ≤ 32 < 슬롯 64) 즉시 활성 · 순회
        // index = 발동한 제어 주문 슬롯(−1 = 마진콜 · 제어 슬롯 없음)
        private void Flatten(ArenaBook book, int index)
        {
            for (int k = 0; k < book.OrderCount; k++)
            {
                if (k == index) continue;
                ref var x = ref book.Orders[k];
                if (x.Done || ArenaOrderKind.IsControl(x.Kind)) continue;
                if (x.Active) Cancel(book, ref x);
                else { x.Done = true; book.Cancels++; }
            }
            if (index >= 0) book.Orders[index].Done = true;
            int free = 0;
            for (int p = book.PosCount - 1; p >= 0; p--)                         // 역순 — 전량 체결 시 포지션 교체 제거(마지막 → p)가 미처리 항목을 건너뛰지 않게
            {
                if (p >= book.PosCount) continue;                                   // 직전 순회 체결로 꼬리 포지션이 제거된 경우
                long sym = book.PosSym[p], held = book.PosQty[p];
                int dir = held > 0 ? -1 : 1;                                        // 롱 = 시장가 매도 · [v12.18.23-DEV] 숏 = 시장가 숏커버 매수
                long qty = (held > 0 ? held : -held) - book.PosReserved[p];
                if (qty <= 0) continue;
                while (free < book.OrderCount && (free == index || !book.Orders[free].Done)) free++;
                if (free >= book.OrderCount) { if (book.OrderCount >= ArenaBook.MaxOrders) break; free = book.OrderCount++; }
                ref var o = ref book.Orders[free];
                o = default;
                o.Symbol = sym; o.Dir = dir; o.Qty = qty; o.Kind = ArenaOrderKind.Market; o.DueTicks = _now;
                o.ReservedQty = qty; book.PosReserved[p] += qty;
                o.Mode = 1; o.Forced = true;
                int slot = QuoteIndex(sym);
                o.RefPx = slot < 0 ? 0 : dir < 0 ? _q[slot].BidPrice[0] : _q[slot].AskPrice[0];
                o.Active = true;
                if (slot >= 0) { o.QuoteVer = _qVer[slot]; Walk(book, ref o, slot); }      // 호가 없으면 다음 호가 갱신 때 재순회
                free++;
            }
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
        // [v12.18.18-DEV] TWAP = 방출분(Visible)까지만 · 시장가 = 진공 페널티(체결가 가산) 적용 + 단 소진 70% 이상 = 진공 발동/가중
        private void Walk(ArenaBook book, ref ArenaWorkingOrder o, int slot)
        {
            ref readonly var q = ref _q[slot];
            var impact = (o.Dir > 0 ? book.ImpactAsk : book.ImpactBid).AsSpan(slot * Depth, Depth);
            long cap = o.IntervalTicks > 0 ? o.Visible : o.Qty;
            long filled = 0, notional = 0, penaltyWon = 0;
            bool cashOut = false, market = o.Kind == ArenaOrderKind.Market, vacuum = false;
            int vi = slot * 2 + (o.Dir > 0 ? 0 : 1);
            long penalty = 0;
            if (market && book.VacLevel[vi] > 0)
            {
                if (_now >= book.VacUntil[vi]) book.VacLevel[vi] = 0;                // 창 경과 = 유동성 복원
                else penalty = ((1L << book.VacLevel[vi]) - 1) * ArenaBookMath.TickOf(in q);
            }
            long budget = o.Dir <= 0 ? 0 : o.Forced ? ForcedBudget : book.AvailableCashWon + o.ReservedCash + CoverLockRelease(book, ref o, cap);
            for (int l = 0; l < Depth && filled < cap; l++)
            {
                long px = o.Dir > 0 ? q.AskPrice[l] : q.BidPrice[l];
                if (px <= 0 || px > MaxPriceWon) break;
                if (o.Kind == ArenaOrderKind.Limit && (o.Dir > 0 ? px > o.LimitPx : px < o.LimitPx)) break;
                long shown = o.Dir > 0 ? q.AskQty[l] : q.BidQty[l];
                long lq = shown - impact[l];
                if (lq <= 0) continue;
                long fpx = o.Dir > 0 ? px + penalty : Math.Max(1, px - penalty);   // 진공 체결가(매도 하한 1원)
                long take = Math.Min(cap - filled, lq);
                if (o.Dir > 0)
                {
                    long room = budget - notional - Fee(notional);
                    long afford = room <= 0 ? 0 : (long)((Int128)room * 1_000_000 / ((Int128)fpx * (1_000_000 + FeePpm)));
                    while (afford > 0 && notional + afford * fpx + Fee(notional + afford * fpx) > budget) afford--;
                    if (afford < take) { take = afford; cashOut = true; }
                    if (take <= 0) break;
                }
                impact[l] += take;
                if (market && impact[l] * 100 >= shown * VacuumThresholdPct) vacuum = true;
                filled += take;
                notional += take * fpx;
                penaltyWon += take * (o.Dir > 0 ? fpx - px : px - fpx);
                if (cashOut) break;
            }
            if (vacuum)
            {
                int k = book.VacLevel[vi];
                book.VacLevel[vi] = k > 0 && _now < book.VacUntil[vi] ? Math.Min(MaxVacuumLevel, k + 1) : 1;
                book.VacUntil[vi] = _now + VacuumWindowTicks;
                book.VacuumEvents++;
            }
            book.VacuumPenaltyWon += penaltyWon;
            if (filled > 0) Fill(book, ref o, filled, notional, aggressive: true, _now);
            if (cashOut && !o.Done)
            {
                Release(book, ref o);
                o.Done = true;
                book.LastReject = ArenaReject.CashExhausted;
                book.Cancels++;
            }
        }

        /// <summary>[v12.18.18-DEV] 그 진영 · 종목 · 방향(+1 매수 = 매도호가 소진측)의 현재 진공 단계(창 경과 = 0).</summary>
        public int VacuumLevel(int side, long symbol, int dir)
        {
            int slot = QuoteIndex(symbol);
            if (slot < 0) return 0;
            var book = Book(side);
            int vi = slot * 2 + (dir > 0 ? 0 : 1);
            return _now < book.VacUntil[vi] ? book.VacLevel[vi] : 0;
        }

        // 체결 1건 반영(현금 · 포지션 · 수수료 · 세금 · 실현손익 · 예약 해제)
        private static void Fill(ArenaBook book, ref ArenaWorkingOrder o, long qty, long notional, bool aggressive, long now)
        {
            long fee = Fee(notional);
            long slipBefore = book.SlippageWon, posBefore = book.Position(o.Symbol), realizedNow = 0;
            if (o.Dir > 0 && o.Mode == 1)                                           // [v12.18.23-DEV] 숏커버
            {
                long cost = notional + fee;
                int pi = book.IndexOf(o.Symbol);
                long held = -book.PosQty[pi];
                long basis = (long)((Int128)book.PosCost[pi] * qty / held);          // 비례 순매도 대금(절사 — 잔여는 남은 숏이 보유)
                long lockRel = (long)((Int128)book.PosLock[pi] * qty / held);
                long realized = basis - cost;
                book.CashWon -= cost;
                book.RealizedWon += realized;
                realizedNow = realized;
                if (realized > 0) book.Wins++; else book.Losses++;
                book.ShortLockedWon -= lockRel; book.PosLock[pi] -= lockRel;
                book.PosReserved[pi] -= qty;
                o.ReservedQty -= qty;
                if (o.ReservedCash > 0)
                {
                    long rel = qty >= o.Qty ? o.ReservedCash : (long)((Int128)o.ReservedCash * qty / o.Qty);
                    o.ReservedCash -= rel; book.ReservedCashWon -= rel;
                }
                if (aggressive && o.Kind == ArenaOrderKind.Market && o.RefPx > 0) book.SlippageWon += notional - qty * o.RefPx;
                book.ShortCoverFills++;
                if (held == qty) RemovePosition(book, pi);
                else { book.PosQty[pi] += qty; book.PosCost[pi] -= basis; }
            }
            else if (o.Dir < 0 && o.Mode == 0)                                      // [v12.18.23-DEV] 숏 개시
            {
                long tax = Tax(notional);
                long proceeds = notional - fee - tax;
                long lockAdd = (long)((Int128)notional * (100 + ShortMarginPct) / 100);
                int pi = book.IndexOf(o.Symbol);
                if (pi < 0) { pi = book.PosCount++; book.PosSym[pi] = o.Symbol; book.PosQty[pi] = 0; book.PosCost[pi] = 0; book.PosReserved[pi] = 0; book.PosLock[pi] = 0; }
                book.CashWon += proceeds;
                book.PosQty[pi] -= qty;
                book.PosCost[pi] += proceeds;
                book.PosLock[pi] += lockAdd; book.ShortLockedWon += lockAdd;
                book.TaxWon += tax;
                if (o.ReservedCash > 0)
                {
                    long rel = qty >= o.Qty ? o.ReservedCash : (long)((Int128)o.ReservedCash * qty / o.Qty);
                    o.ReservedCash -= rel; book.ReservedCashWon -= rel;
                }
                if (aggressive && o.Kind == ArenaOrderKind.Market && o.RefPx > 0) book.SlippageWon += qty * o.RefPx - notional;
                book.ShortOpenFills++;
            }
            else if (o.Dir > 0)
            {
                long cost = notional + fee;
                int pi = book.IndexOf(o.Symbol);
                if (pi < 0) { pi = book.PosCount++; book.PosSym[pi] = o.Symbol; book.PosQty[pi] = 0; book.PosCost[pi] = 0; book.PosReserved[pi] = 0; book.PosLock[pi] = 0; }
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
                realizedNow = realized;
                if (realized > 0) book.Wins++; else book.Losses++;
                book.PosReserved[pi] -= qty;
                o.ReservedQty -= qty;
                if (aggressive && o.Kind == ArenaOrderKind.Market && o.RefPx > 0) book.SlippageWon += qty * o.RefPx - notional;
                if (held == qty) RemovePosition(book, pi);
                else { book.PosQty[pi] = held - qty; book.PosCost[pi] -= basis; }
            }
            book.FeesWon += fee;
            book.Fills++;
            if (o.Peak > 0) o.Visible = Math.Max(0, o.Visible - qty);
            book.Analytics.OnFill(o.Symbol, o.Dir, posBefore, book.Position(o.Symbol), realizedNow, now, slipBefore, book.SlippageWon);
            o.Qty -= qty;
            o.Filled += qty;
            if (o.Qty == 0) { Release(book, ref o); o.Done = true; }
        }

        private static void RemovePosition(ArenaBook book, int i)
        {
            int last = --book.PosCount;
            book.PosSym[i] = book.PosSym[last]; book.PosQty[i] = book.PosQty[last]; book.PosCost[i] = book.PosCost[last]; book.PosReserved[i] = book.PosReserved[last];
            book.PosLock[i] = book.PosLock[last];
            book.PosSym[last] = book.PosQty[last] = book.PosCost[last] = book.PosReserved[last] = book.PosLock[last] = 0;
        }

        // [v12.18.23-DEV] 숏커버 순회 예산에 더할 담보 해제분(남은 커버 수량 비례 · 롱 청산/개시 = 0)
        private static long CoverLockRelease(ArenaBook book, ref ArenaWorkingOrder o, long cap)
        {
            if (o.Mode != 1) return 0;
            int pi = book.IndexOf(o.Symbol);
            if (pi < 0 || book.PosQty[pi] >= 0) return 0;
            long held = -book.PosQty[pi];
            return (long)((Int128)book.PosLock[pi] * Math.Min(cap, held) / held);
        }

        // ── 대기열 ──

        // 진입 시 앞 잔량 = 같은 가격 표시 잔량(+ 같은 원장의 앞선 내 대기 주문) · 최우선보다 좋은 가격/호가 사이 빈 가격 = 0 · 10호가 밖 = 10번째 잔량(보수 추정)
        // [v12.18.18-DEV] behindAll = 리필 재배치(같은 가격 내 다른 대기 주문 전부 앞 · 접수 순서 무관) · 빙산은 노출분만 대기열에 존재
        private long InitialQueueAhead(ArenaBook book, int index, int slot, bool behindAll)
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
            int end = behindAll ? book.OrderCount : index;
            for (int k = 0; k < end; k++)
            {
                if (k == index) continue;
                ref readonly var x = ref book.Orders[k];
                if (!x.Done && x.Active && x.Kind == ArenaOrderKind.Limit && x.Symbol == o.Symbol && x.Dir == o.Dir && x.LimitPx == o.LimitPx) ahead += x.Peak > 0 ? x.Visible : x.Qty;
            }
            return ahead;
        }

        // 빙산 리필: 은닉 잔량에서 min(Peak, 잔량) 노출 → 그 가격 대기열 맨 뒤(L2 순번 재할당)
        private void Requeue(ArenaBook book, int index, int slot)
        {
            ref var o = ref book.Orders[index];
            o.Visible = Math.Min(o.Peak, o.Qty);
            o.Refills++;
            book.IcebergRefills++;
            o.QueueAhead = InitialQueueAhead(book, index, slot, behindAll: true);
            o.LastShown = ShownAt(slot, o.Dir, o.LimitPx); o.TradedAtPx = 0;
        }

        // [v12.18.23-DEV] 그 가격의 현재 표시 잔량(같은 쪽 · 10호가 밖/없음 = 0 = 미상)
        private long ShownAt(int slot, int dir, long px)
        {
            ref readonly var q = ref _q[slot];
            for (int l = 0; l < Depth; l++)
            {
                long p = dir > 0 ? q.BidPrice[l] : q.AskPrice[l];
                if (p <= 0) return 0;
                if (p == px) return Math.Max(0, dir > 0 ? q.BidQty[l] : q.AskQty[l]);
            }
            return 0;
        }

        // 새 호가: [v12.18.23-DEV] 취소 이탈 비례 감축(⑪) → 표시 잔량이 앞 잔량보다 작으면 축소 · 내 가격이 최우선보다 좋아지면 0
        private void RefreshQueue(ArenaBook book, ref ArenaWorkingOrder o, int slot)
        {
            ref readonly var q = ref _q[slot];
            long best = o.Dir > 0 ? q.BidPrice[0] : q.AskPrice[0];
            if (best <= 0 || (o.Dir > 0 ? best < o.LimitPx : best > o.LimitPx)) { o.QueueAhead = 0; o.LastShown = 0; o.TradedAtPx = 0; return; }
            for (int l = 0; l < Depth; l++)
            {
                long px = o.Dir > 0 ? q.BidPrice[l] : q.AskPrice[l];
                if (px <= 0) break;
                if (px == o.LimitPx)
                {
                    long shown = Math.Max(0, o.Dir > 0 ? q.BidQty[l] : q.AskQty[l]);
                    o.QueueAhead -= CancelDepletion(book, o.QueueAhead, o.LastShown, shown, o.TradedAtPx);
                    if (shown < o.QueueAhead) o.QueueAhead = shown;
                    o.LastShown = shown; o.TradedAtPx = 0;
                    return;
                }
            }
            o.LastShown = 0; o.TradedAtPx = 0;                                       // 10호가 밖/미표시 = 미상(다음 관측부터 재기준)
        }

        /// <summary>
        /// [v12.18.23-DEV] ⑪ 취소 이탈 비례 감축량(δ=0 · 순수): C = max(0, (prevShown − shown) − traded) · 앞 몫 = C × min(ahead, prevShown) ÷ prevShown(절사).
        /// prevShown ≤ 0(미상) · ahead ≤ 0 = 0. 반환 ≤ ahead 보장.
        /// </summary>
        public static long CancelDepletion(long ahead, long prevShown, long shown, long traded)
        {
            if (ahead <= 0 || prevShown <= 0) return 0;
            long cancels = prevShown - shown - Math.Max(0, traded);
            if (cancels <= 0) return 0;
            long front = Math.Min(ahead, prevShown);
            return Math.Min(ahead, (long)((Int128)cancels * front / prevShown));
        }

        private static long CancelDepletion(ArenaBook book, long ahead, long prevShown, long shown, long traded)
        {
            long r = CancelDepletion(ahead, prevShown, shown, traded);
            book.QueueCancelDepletedQty += r;
            return r;
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
                    if (o.Dir > 0 ? t.PriceWon < o.LimitPx : t.PriceWon > o.LimitPx) fill = o.Qty;                // 관통(빙산 은닉분 포함 전량)
                    else if (t.PriceWon == o.LimitPx && (o.Dir > 0 ? side <= 0 : side >= 0))
                    {
                        long v = t.Qty, used = Math.Min(v, o.QueueAhead);
                        o.QueueAhead -= used;
                        o.TradedAtPx += v;                                           // ⑪ 다음 스냅샷의 잔량 감소 중 체결분(취소 아님)
                        fill = Math.Min(v - used, o.Peak > 0 ? o.Visible : o.Qty);      // 빙산 = 노출분까지만
                    }
                    if (fill <= 0) continue;
                    if (o.Mode == 1) fill = Math.Min(fill, Math.Abs(book.Position(o.Symbol)));   // 청산(롱 매도 · 숏커버) = 보유 상한 · 숏 개시 = 무상한
                    if (fill > 0) Fill(book, ref o, fill, fill * o.LimitPx, aggressive: false, _now);
                    if (!o.Done && o.Peak > 0 && o.Visible == 0)
                    {
                        int slot = QuoteIndex(o.Symbol);
                        if (slot >= 0) Requeue(book, i, slot);
                        else { o.Visible = Math.Min(o.Peak, o.Qty); o.Refills++; book.IcebergRefills++; }
                    }
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

        /// <summary>
        /// [v12.18.14-DEV] DOM 래더 투영(δ=0): 그 종목 10호가 + 휴먼 원장의 작동 지정가를 가격별로 합산(수량 · 맨 앞 내 주문의 앞 잔량) + 보유/매도 가능/원가.
        /// 매수 지정가 = 매수호가 열 · 매도 지정가 = 매도호가 열 · 래더 밖 가격 = MyOffLadderQty.
        /// </summary>
        public void FillDepth(long symbol, ref ArenaDepth d)
        {
            d = default;
            d.Symbol = symbol;
            for (int l = 0; l < ArenaDepth.Levels; l++) { d.MyAskAhead[l] = -1; d.MyBidAhead[l] = -1; }
            var h = Book(ArenaSide.Human);
            int pi = h.IndexOf(symbol);
            if (pi >= 0)
            {
                d.PositionQty = h.PosQty[pi]; d.PositionCostWon = h.PosCost[pi];
                if (h.PosQty[pi] > 0) d.Sellable = h.PosQty[pi] - h.PosReserved[pi];
                else d.Coverable = -h.PosQty[pi] - h.PosReserved[pi];
            }
            int slot = QuoteIndex(symbol);
            if (slot >= 0)
            {
                ref readonly var q = ref _q[slot];
                d.HasBook = true; d.BookVersion = _qVer[slot];
                for (int l = 0; l < ArenaDepth.Levels; l++)
                {
                    d.AskPx[l] = q.AskPrice[l]; d.AskQty[l] = q.AskQty[l];
                    d.BidPx[l] = q.BidPrice[l]; d.BidQty[l] = q.BidQty[l];
                }
            }
            for (int k = 0; k < h.OrderCount; k++)
            {
                ref readonly var o = ref h.Orders[k];
                if (o.Done || !o.Active || o.Kind != ArenaOrderKind.Limit || o.Symbol != symbol) continue;
                bool placed = false;
                for (int l = 0; l < ArenaDepth.Levels && slot >= 0; l++)
                {
                    if ((o.Dir > 0 ? d.BidPx[l] : d.AskPx[l]) != o.LimitPx) continue;
                    long shownQty = o.Peak > 0 ? o.Visible : o.Qty;
                    if (o.Dir > 0) { d.MyBidQty[l] += shownQty; if (d.MyBidAhead[l] < 0) d.MyBidAhead[l] = o.QueueAhead; }
                    else { d.MyAskQty[l] += shownQty; if (d.MyAskAhead[l] < 0) d.MyAskAhead[l] = o.QueueAhead; }
                    d.MyHiddenQty += o.Qty - shownQty;
                    placed = true;
                    break;
                }
                if (!placed) d.MyOffLadderQty += o.Qty;
            }
        }

        /// <summary>
        /// 시가평가(롱 = 보유 × 최우선 매수호가 · [v12.18.23-DEV] 숏 = −수량 × 최우선 매도호가(되사는 값 · 부채) · 호가 없음 = 원가) →
        /// 자산(현금 + Σ 표시가) · 미실현 · 고점 · MDD 갱신. 반환 = 자산(원). 숏 평가손은 자산에 그대로 반영(하드 리스크 판정 포함).
        /// </summary>
        public long MarkToMarket(int side, out long unrealizedWon)
        {
            var book = Book(side);
            long value = 0, cost = 0;
            for (int i = 0; i < book.PosCount; i++)
            {
                long q = book.PosQty[i];
                if (q > 0)
                {
                    long bid = BestBid(book.PosSym[i]);
                    value += bid > 0 ? q * bid : book.PosCost[i];
                    cost += book.PosCost[i];
                    if (bid > 0) book.Analytics.Mark(book.PosSym[i], q * bid - book.PosCost[i]);   // MAE/MFE 편차
                }
                else if (q < 0)
                {
                    long ask = BestAsk(book.PosSym[i]);
                    value += ask > 0 ? q * ask : -book.PosCost[i];
                    cost -= book.PosCost[i];
                    if (ask > 0) book.Analytics.Mark(book.PosSym[i], book.PosCost[i] + q * ask);
                }
            }
            unrealizedWon = value - cost;
            long equity = book.CashWon + value;
            if (equity > book.PeakWon) book.PeakWon = equity;
            if (book.PeakWon - equity > book.MddWon) book.MddWon = book.PeakWon - equity;
            return equity;
        }
    }
}
