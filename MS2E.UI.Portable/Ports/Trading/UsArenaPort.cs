// ============================================================================
// UsArenaPort — v12.18.61-DEV: 미국 테크 아레나(빅테크 5종 · 휴먼 vs AI · 설명 가능한 AI 근거) UI 경계(순수 DTO · 엔진 타입 참조 0)
//
//   호스트 어댑터가 UsTechArenaEngine 의 seqlock 스냅샷/근거 피드를 DTO 로 투영한다. UI 는 엔진을 new 하지 않는다.
//   금액 단위 = USD 센트(long) — 표시 변환은 UI 몫. 세션 = 미국 세션 페이즈(UsArenaPhase) · 체결은 정규 연속매매(Tradable)뿐.
//   ★Fail-Closed: 이 포트의 주문 경로는 휴먼 가상 원장 큐(SubmitHuman)뿐 — 브로커 · 무장 · 실주문 메서드 없음(해외 주문 API 송출 0).
//   ★정직 렌더: Fed=false = 시세 피드 미결선(정산 0회) — UI 는 가짜 손익을 그리지 않는다.
//   [v12.18.68-DEV] 플루토 프로 거버너: SetGovernanceEnabled · Governance(ArenaGovernanceView) · 스냅샷 IsGovernanceEnabled/CurrentRegime ·
//     근거 Governance(ArenaGovernanceCard · 감점 태그 · 자가 회고). 기본 구현 = 미지원(정지 · 판정 전).
// ============================================================================
using System;
using System.Collections.Generic;

namespace MS2E.UI.Portable.Ports.Trading
{
    /// <summary>US 아레나 휴먼 주문 결과 코드(접수 · 체결 단계 공용 · 0 = 없음).</summary>
    public static class UsArenaReject
    {
        public const int None = 0, SessionClosed = 1, Invalid = 2, NoQuote = 3, NoLiquidity = 4, InsufficientCash = 5,
                         InsufficientPosition = 6, BookFull = 7, QueueFull = 8;
        public static string Label(int r) => r switch
        {
            None => "-", SessionClosed => "미국 정규장(연속매매) 외", Invalid => "주문 값 오류 · 표 밖 티커", NoQuote => "호가 미수신",
            NoLiquidity => "호가 잔량 없음", InsufficientCash => "가상 USD 현금 부족", InsufficientPosition => "보유 수량 부족",
            BookFull => "보유 종목 한도", QueueFull => "주문 큐 포화 — 폐기(재시도 없음)", _ => "?",
        };
    }

    /// <summary>미국 세션 페이즈(엔진 UsSessionPhase 와 같은 값).</summary>
    public static class UsArenaPhase
    {
        public const int HolidayOrWeekend = 0, Closed = 1, PreMarket = 2, ContinuousTrading = 3, AfterHours = 4, CalendarUncovered = 5;
        public static string Label(int p) => p switch
        {
            HolidayOrWeekend => "휴장(주말 · NYSE 휴장일)", Closed => "장 종료", PreMarket => "프리마켓(탐색 · 준비)",
            ContinuousTrading => "정규장(체결 가능)", AfterHours => "애프터마켓(체결 차단)", CalendarUncovered => "달력 범위 밖(fail-closed)", _ => "?",
        };
    }

    /// <summary>US 아레나 시세 출처.</summary>
    public static class UsArenaFeed
    {
        public const int KisOverseas = 1, Synthetic = 2;
        public static string Label(int f) => f switch
        {
            KisOverseas => "KIS 해외 현재체결가(HHDFS00000300)", Synthetic => "합성 시세(데모 · 프리/정규 ×20)", _ => "?",
        };
    }

    /// <summary>시세 공급 상태. Pumped=false = 정산 펌프 미결선 · KisUpdates = KIS 시세 수신 누계.</summary>
    public readonly record struct UsArenaFeedStatus(int Requested, int Applied, bool Pumped, long Pumps, long KisUpdates, long DroppedQuotes)
    {
        public bool Switching => Requested != Applied;
    }

    /// <summary>수신 종목 최우선 호가(센트 · 주). Exchange = 거래 계열 거래소 코드(NASD · NYSE).</summary>
    public readonly record struct UsArenaQuoteView(string Ticker, string Exchange, long BidCents, long AskCents, long BidQty, long AskQty);

    /// <summary>보유 1종목(센트 · 주). Target/StopLoss = AI 진입 계획(휴먼 = 0).</summary>
    public readonly record struct UsArenaPositionView(string Ticker, long Qty, long AvgPriceCents, long CostCents, long MarkCents, long TargetPriceCents, long StopLossPriceCents)
    {
        public long EvalCents => Qty * MarkCents;
        public long PnlCents => EvalCents - CostCents;
    }

    /// <summary>진영 1개 정산 지표(센트 · bp). FeesCents = 위탁 수수료 · SecFeeCents = 매도 SEC 수수료.</summary>
    public readonly record struct UsArenaBookView(
        long CapitalCents, long CashCents, long EquityCents, long RealizedCents, long UnrealizedCents, long FeesCents, long SecFeeCents,
        long ReturnBp, long MddBp, long WinRateBp, long Fills, long Rejects, int Wins, int Losses, int OpenPositions);

    /// <summary>US 아레나 스냅샷 투영. Phase = UsArenaPhase · Tradable = 정규 연속매매.</summary>
    public readonly record struct UsArenaView(
        long Version, long Ticks, int Phase, bool Tradable, int QqqBp, int SoxxBp, int Us10yChangeBp, long RationaleCount, DateTime? SessionKst,
        long DroppedQuotes, UsArenaBookView Ai, UsArenaBookView Human, bool IsGovernanceEnabled = false, int CurrentRegime = ArenaRegimeCode.Unknown)
    {
        public UsArenaBookView Side(int side) => side == ArenaSide.Human ? Human : Ai;
        public int Leader => Ai.EquityCents > Human.EquityCents ? ArenaLeader.Ai : Human.EquityCents > Ai.EquityCents ? ArenaLeader.Human : ArenaLeader.Draw;
    }

    /// <summary>AI 근거 1건 투영(가격 센트 · Summary = 한국어 설명 요약).</summary>
    public sealed record UsRationaleView(
        long Seq, string Ticker, int Side, long Qty, long PriceCents, DateTime AtKst, string Trigger, int SentimentScore, int ConfidenceScore,
        long TargetPriceCents, long StopLossPriceCents, double RiskRewardRatio, long RealizedPnlCents,
        int EtfBetaBp, int RateBp, int GapFlowBp, int MomentumBp, string Summary, ArenaGovernanceCard? Governance = null);

    public interface IUsArenaPort
    {
        /// <summary>모드 표시명.</summary>
        string ModeName { get; }
        /// <summary>시세 수신 이력(false = 피드 미결선 · 정산 0회).</summary>
        bool Fed { get; }
        /// <summary>최신 게시 버전(렌더 폴링 · 변경 시에만 다시 그린다).</summary>
        long Version { get; }
        /// <summary>대결 종목 티커(표 순서).</summary>
        IReadOnlyList<string> Tickers { get; }
        /// <summary>최신 스냅샷(경합 상한 초과 = false).</summary>
        bool TryRead(out UsArenaView view);
        /// <summary>최근 AI 근거(최신 → 과거 · 최대 max건). UI 콜드 경로.</summary>
        IReadOnlyList<UsRationaleView> RecentRationales(int max);
        /// <summary>휴먼 시장가 주문 → 휴먼 가상 원장 큐(side +1 매수 · −1 매도). 반환 = UsArenaReject(접수 단계).</summary>
        int SubmitHuman(string ticker, int side, long qty);
        /// <summary>시세 공급 상태.</summary>
        UsArenaFeedStatus Feed { get; }
        /// <summary>시세 출처 선택(UsArenaFeed) — 다음 정산에서 양 진영 원장 초기화 후 적용. 범위 밖 = false.</summary>
        bool SelectSource(int source);
        /// <summary>수신 종목 최우선 호가(표 순서).</summary>
        IReadOnlyList<UsArenaQuoteView> Quotes();
        /// <summary>진영(ArenaSide) 보유 종목(표 순서).</summary>
        IReadOnlyList<UsArenaPositionView> Positions(int side);
        /// <summary>[v12.18.64-DEV] 티커 실시간 뉴스 촉매(아레나 정산 시각 기준 감쇄 · 노이즈/중립/미결속 = null). 기본 구현 = null.</summary>
        ArenaCatalystView? Catalyst(string ticker) => null;
        /// <summary>[v12.18.68-DEV] 플루토 프로 거버너 가동/정지(다음 정산부터 · 미지원 = false). 기본 구현 = false.</summary>
        bool SetGovernanceEnabled(bool enabled) => false;
        /// <summary>[v12.18.68-DEV] 거버너 상태(가동 · 시장 대표 국면 · 연패/쿨다운 · 계수). 기본 구현 = default(미지원 = 정지 · 판정 전).</summary>
        ArenaGovernanceView Governance => default;
    }
}
