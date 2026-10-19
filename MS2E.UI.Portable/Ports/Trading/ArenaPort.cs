// ============================================================================
// ArenaPort — [v12.18.11-DEV] AI vs Human 실시간 대결 모드 포트(순수 UI 경계 · 엔진 타입 참조 0)
//
//   호스트 ArenaMatchGovernor(듀얼 가상 원장)가 50ms 허브 펌프마다 정산한 스냅샷을 seqlock 사본으로 읽고(잠금 · 할당 0),
//   휴먼 수동 주문은 시뮬레이터의 휴먼 큐에만 적재한다.
//   ★Fail-Closed: 이 포트에는 브로커 · 무장 · 실주문 경로가 없다 — 수동 주문의 유일한 목적지는 가상 원장(외부 송출 수단 자체가 없음).
// ============================================================================
using System;

namespace MS2E.UI.Portable.Ports.Trading
{
    /// <summary>대결 진영 인덱스.</summary>
    public static class ArenaSide
    {
        public const int Ai = 0, Human = 1, Count = 2;
        public static string Label(int s) => s switch { Ai => "AI", Human => "휴먼", _ => "?" };
    }

    /// <summary>매치 상태(0 대기 · 1 진행 · 2 종료).</summary>
    public static class ArenaState
    {
        public const int Idle = 0, Running = 1, Finished = 2;
        public static string Label(int s) => s switch { Idle => "대기", Running => "진행 중", Finished => "종료", _ => "?" };
    }

    /// <summary>현재 리더(0 동률 · 1 AI · 2 휴먼).</summary>
    public static class ArenaLeader
    {
        public const int Draw = 0, Ai = 1, Human = 2;
        public static string Label(int l) => l switch { Ai => "AI 우세", Human => "휴먼 우세", _ => "동률" };
    }

    /// <summary>주문 결과 코드(접수 거부 · 체결 거부 공용 · 0 = 없음).</summary>
    public static class ArenaReject
    {
        public const int None = 0, NotRunning = 1, Invalid = 2, NoQuote = 3, NoLiquidity = 4, InsufficientCash = 5, InsufficientPosition = 6, BookFull = 7,
                         OrdersFull = 8, CashExhausted = 9, RiskLocked = 10, OppositeExposure = 11;
        public static string Label(int r) => r switch
        {
            None => "-", NotRunning => "매치 미진행", Invalid => "주문 값 오류", NoQuote => "호가 미수신", NoLiquidity => "호가 잔량 없음",
            InsufficientCash => "가상 현금 부족", InsufficientPosition => "보유 수량 부족", BookFull => "보유 종목 한도",
            OrdersFull => "미체결 주문 한도", CashExhausted => "잔여 수량 현금 부족 — 잔량 취소", RiskLocked => "계좌 동결 — 일일 손실 한도 초과",
            OppositeExposure => "반대 방향 진입 주문 진행 중 — 롱/숏 동시 개시 불가", _ => "?",
        };
    }

    /// <summary>[v12.18.23-DEV] 포지션 방향(0 무포지션 · 1 롱 · 2 숏).</summary>
    public static class ArenaPositionSide
    {
        public const int Flat = 0, Long = 1, Short = 2;
        public static int Of(long qty) => qty > 0 ? Long : qty < 0 ? Short : Flat;
        public static string Label(int s) => s switch { Long => "LONG", Short => "SHORT", _ => "FLAT" };
        public static string Css(int s) => s switch { Long => "long", Short => "short", _ => "flat" };
    }

    /// <summary>진영 1개의 정산 지표(원 · 주 · bp = 1/10000). 손익 = 수수료 0.015% · 거래세 0.18% · 호가 슬리피지 반영 후.</summary>
    public readonly struct ArenaSideStats
    {
        public readonly long EquityWon, CashWon, RealizedWon, UnrealizedWon, FeesWon, TaxWon, SlippageWon, PeakWon, MddWon;
        public readonly long Fills, Rejects;
        public readonly int Wins, Losses, OpenPositions, LastReject, MddBp;
        /// <summary>[v12.18.13-DEV] 지연 대기(접수 → 체결 판단 전) · 작동 중(지정가 대기열 · 시장가 잔량) 주문 수 · 가장 오래된 지정가의 내 앞 잔량 · 취소 누적 · 예약 현금.</summary>
        public readonly int PendingOrders, WorkingOrders;
        public readonly long QueueAheadQty, Cancels, ReservedCashWon;
        /// <summary>[v12.18.18-DEV] 유동성 진공 발동 누적 · 진공 페널티 누적(원 · 슬리피지에 포함) · 빙산 리필 누적.</summary>
        public readonly long VacuumEvents, VacuumPenaltyWon, IcebergRefills;
        /// <summary>[v12.18.23-DEV] 롱/숏 보유 종목 수 · 숏 담보 잠금(원 · 매도 대금 + 증거금 50%) · 대기열 취소 이탈로 단축된 앞 잔량 누적(주).</summary>
        public readonly int LongPositions, ShortPositions;
        public readonly long ShortLockedWon, QueueCancelDepletedQty;

        public ArenaSideStats(long equityWon, long cashWon, long realizedWon, long unrealizedWon, long feesWon, long taxWon, long slippageWon,
                              long peakWon, long mddWon, int mddBp, long fills, long rejects, int wins, int losses, int openPositions, int lastReject,
                              int pendingOrders = 0, int workingOrders = 0, long queueAheadQty = -1, long cancels = 0, long reservedCashWon = 0,
                              long vacuumEvents = 0, long vacuumPenaltyWon = 0, long icebergRefills = 0,
                              int longPositions = 0, int shortPositions = 0, long shortLockedWon = 0, long queueCancelDepletedQty = 0)
        {
            LongPositions = longPositions; ShortPositions = shortPositions; ShortLockedWon = shortLockedWon; QueueCancelDepletedQty = queueCancelDepletedQty;
            VacuumEvents = vacuumEvents; VacuumPenaltyWon = vacuumPenaltyWon; IcebergRefills = icebergRefills;
            EquityWon = equityWon; CashWon = cashWon; RealizedWon = realizedWon; UnrealizedWon = unrealizedWon;
            FeesWon = feesWon; TaxWon = taxWon; SlippageWon = slippageWon; PeakWon = peakWon; MddWon = mddWon; MddBp = mddBp;
            Fills = fills; Rejects = rejects; Wins = wins; Losses = losses; OpenPositions = openPositions; LastReject = lastReject;
            PendingOrders = pendingOrders; WorkingOrders = workingOrders; QueueAheadQty = queueAheadQty; Cancels = cancels; ReservedCashWon = reservedCashWon;
        }

        public double WinRate => Wins + Losses == 0 ? 0 : (double)Wins / (Wins + Losses);
        /// <summary>자본금 대비 손익(원) — 자본금은 스냅샷이 보유.</summary>
        public long PnlWon(long capitalWon) => EquityWon - capitalWon;
    }

    /// <summary>대결 1프레임(읽기 전용 값 · seqlock 사본 — 리스/반납 없음).</summary>
    public readonly struct ArenaSnapshot
    {
        public readonly long Version, CapitalWon, StartedTicks, UpdatedTicks, QuotesSeen, QuotesLost, OrdersLost, AiSignals;
        public readonly int State, Leader, QuoteSymbols;
        public readonly long LeadWon;
        public readonly ArenaSideStats Ai, Human;
        /// <summary>[v12.18.15-DEV] AI 페르소나 의사결정 HUD(같은 seqlock 창에서 게시).</summary>
        public readonly ArenaAiHud AiHud;
        /// <summary>[v12.18.18-DEV] 휴먼 하드 리스크 동결(일일 손실 한도 도달 → 시장가 강제 청산 + 이번 라운드 발주 전면 차단) · 한도(bp) · 발동 시각 · 발동 시 자산.</summary>
        public readonly bool HumanRiskLocked;
        public readonly int RiskLimitBp;
        public readonly long RiskTripTicks, RiskTripEquityWon;
        /// <summary>[v12.18.23-DEV] AI 대상 종목 포지션 방향(ArenaPositionSide) · 휴먼 포커스 종목 포지션 방향.</summary>
        public readonly int AiTargetSide, HumanFocusSide;

        public ArenaSnapshot(long version, int state, long capitalWon, long startedTicks, long updatedTicks, long quotesSeen, long quotesLost,
                             int quoteSymbols, long ordersLost, long aiSignals, int leader, long leadWon, in ArenaSideStats ai, in ArenaSideStats human,
                             in ArenaAiHud aiHud = default, bool humanRiskLocked = false, int riskLimitBp = 0, long riskTripTicks = 0, long riskTripEquityWon = 0,
                             int aiTargetSide = ArenaPositionSide.Flat, int humanFocusSide = ArenaPositionSide.Flat)
        {
            AiTargetSide = aiTargetSide; HumanFocusSide = humanFocusSide;
            HumanRiskLocked = humanRiskLocked; RiskLimitBp = riskLimitBp; RiskTripTicks = riskTripTicks; RiskTripEquityWon = riskTripEquityWon;
            Version = version; State = state; CapitalWon = capitalWon; StartedTicks = startedTicks; UpdatedTicks = updatedTicks;
            QuotesSeen = quotesSeen; QuotesLost = quotesLost; QuoteSymbols = quoteSymbols; OrdersLost = ordersLost; AiSignals = aiSignals;
            Leader = leader; LeadWon = leadWon; Ai = ai; Human = human; AiHud = aiHud;
        }

        public ArenaSideStats Side(int side) => side == ArenaSide.Human ? Human : Ai;
    }

    public interface IArenaPort
    {
        /// <summary>정산 펌프 결선 여부(false = 허브 미결선 · UI 는 정직하게 '정산 펌프 미결선' 렌더).</summary>
        bool Pumped { get; }
        /// <summary>최신 스냅샷 버전(0 = 아직 없음).</summary>
        long Version { get; }
        /// <summary>최신 스냅샷 seqlock 사본(경합 재시도 상한 초과 = false · 할당 0).</summary>
        bool TryRead(out ArenaSnapshot snapshot);
        /// <summary>진영 자산곡선(원 · 과거 → 최신) 사본 — 반환 = 복사 개수(경합 상한 초과 = 0 · 할당 0).</summary>
        int CopyEquity(int side, Span<long> destination);
        /// <summary>매치 개시 요청(양측 동일 자본금 · 다음 펌프 회차 반영). 범위 밖 자본금 = false.</summary>
        bool Start(long capitalWon);
        /// <summary>매치 종료 요청(다음 펌프 회차 반영 · 최종 스냅샷 동결).</summary>
        void Stop();
        /// <summary>
        /// 휴먼 시장가 주문 → 시뮬레이터 휴먼 큐(가상 체결 전용). side +1 매수 · −1 매도. 반환 = ArenaReject(접수 단계).
        /// [v12.18.23-DEV] 무포지션/숏 상태 매도 = 가상 공매도 진입(담보 = 매도 대금 + 50% 증거금 잠금) · 숏 상태 매수 = 숏커버(보유 숏 수량 상한).
        /// </summary>
        int SubmitHuman(long symbol, int side, long qty);
        /// <summary>[v12.18.13-DEV] 휴먼 지정가 주문(체결 지연 후 시장성 부분 즉시 체결 · 잔량은 호가 대기열 뒤에 배치 → 체결 틱 거래량이 앞 잔량을 소진해야 체결).</summary>
        int SubmitHumanLimit(long symbol, int side, long qty, long priceWon);
        /// <summary>
        /// [v12.18.18-DEV] 휴먼 빙산(Iceberg) 지정가 — 대기열에는 노출 수량(peakQty)만 걸리고, 노출분이 체결로 소진되면 은닉 잔량에서 리필되어
        /// 그 가격 대기열 맨 뒤로 재배치된다(시장성 부분은 전량 즉시 순회). peakQty ≥ qty = 일반 지정가와 동일.
        /// </summary>
        int SubmitHumanIceberg(long symbol, int side, long qty, long priceWon, long peakQty);
        /// <summary>[v12.18.13-DEV] 휴먼 미체결 전량 취소(취소도 체결 지연을 거친다 — 그 사이 체결 가능). [v12.18.18-DEV] 리스크 동결 중 = RiskLocked(강제 청산 매도 보호).</summary>
        int CancelHuman();
        /// <summary>[v12.18.14-DEV] 휴먼 비상 전량 청산 — 지연 1회 후 미체결(지연 대기 포함) 전량 취소 + 롱 시장가 매도 · [v12.18.23-DEV] 숏 시장가 숏커버 매수(잔량 이월).</summary>
        int FlattenHuman();
        /// <summary>[v12.18.14-DEV] DOM 래더 포커스 종목(다음 게시부터 투영 · 0 = 해제).</summary>
        void SetFocus(long symbol);
        /// <summary>[v12.18.14-DEV] 포커스 종목 래더 사본(seqlock · 할당 0 · 없으면 false).</summary>
        bool TryReadDepth(out ArenaDepth depth);
        /// <summary>[v12.18.14-DEV] 진영 사후 복기 사본(seqlock · 할당 0).</summary>
        bool TryReadDebrief(int side, out ArenaDebrief debrief);
        /// <summary>[v12.18.15-DEV] AI 페르소나 교체(ArenaPersona · 다음 정산 회차 반영 — 진행 중이면 AI 원장 비상 전량 청산 후 교체). 범위 밖 = false.</summary>
        bool SetPersona(int persona);
    }
}
