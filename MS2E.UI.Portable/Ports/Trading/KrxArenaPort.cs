// ============================================================================
// KrxArenaPort — v12.18.59-DEV: 국내 대형주 아레나(휴먼 vs AI · 설명 가능한 AI 근거) UI 경계(순수 DTO · 엔진 타입 참조 0)
//
//   호스트 어댑터가 KrxArenaEngine 의 seqlock 스냅샷/근거 피드를 DTO 로 투영한다. UI 는 엔진을 new 하지 않는다.
//   ★Fail-Closed: 이 포트의 주문 경로는 휴먼 가상 원장 큐(SubmitHuman)뿐 — 브로커 · 무장 · 실주문 메서드 없음.
//   ★정직 렌더: Fed=false = 시세 피드 미결선(정산 0회) — UI 는 가짜 손익을 그리지 않는다.
//   [v12.18.60-DEV] 시세 출처(KrxArenaFeed): 키움 브리지 실시간 호가 ↔ 합성 데모 시세(가상 세션 ×20 · UI 가 '합성'으로 명시) 선택 ·
//     수신 종목 호가 · 진영별 보유 · 게시 버전(렌더 폴링) 판독. 출처 전환 = 양 진영 원장 초기화(출처 간 오염 0).
//   [v12.18.68-DEV] 플루토 프로 거버너: SetGovernanceEnabled · Governance(ArenaGovernanceView) · 스냅샷 IsGovernanceEnabled/CurrentRegime ·
//     근거 Governance(ArenaGovernanceCard · 감점 태그 · 자가 회고). 기본 구현 = 미지원(정지 · 판정 전).
// ============================================================================
using System;
using System.Collections.Generic;

namespace MS2E.UI.Portable.Ports.Trading
{
    /// <summary>KRX 아레나 휴먼 주문 결과 코드(접수 · 체결 단계 공용 · 0 = 없음).</summary>
    public static class KrxArenaReject
    {
        public const int None = 0, SessionClosed = 1, Invalid = 2, NoQuote = 3, NoLiquidity = 4, InsufficientCash = 5,
                         InsufficientPosition = 6, BookFull = 7, QueueFull = 8;
        public static string Label(int r) => r switch
        {
            None => "-", SessionClosed => "정규장(09:00~15:30) 외", Invalid => "주문 값 오류", NoQuote => "호가 미수신",
            NoLiquidity => "호가 잔량 없음", InsufficientCash => "가상 현금 부족", InsufficientPosition => "보유 수량 부족",
            BookFull => "보유 종목 한도", QueueFull => "주문 큐 포화 — 폐기(재시도 없음)", _ => "?",
        };
    }

    /// <summary>[v12.18.60-DEV] 아레나 시세 출처.</summary>
    public static class KrxArenaFeed
    {
        public const int Bridge = 1, Synthetic = 2;
        public static string Label(int f) => f switch { Bridge => "키움 브리지 실시간 호가", Synthetic => "합성 시세(데모 · 가상 세션 ×20)", _ => "?" };
    }

    /// <summary>[v12.18.60-DEV] 시세 공급 상태. Pumped=false = 정산 펌프 미결선 · BridgeUpdates = 브리지 호가 수신 누계.</summary>
    public readonly record struct KrxArenaFeedStatus(int Requested, int Applied, bool Pumped, long Pumps, long BridgeUpdates, long DroppedBooks)
    {
        public bool Switching => Requested != Applied;
    }

    /// <summary>[v12.18.60-DEV] 수신 종목 최우선 호가(원 · 주).</summary>
    public readonly record struct KrxArenaQuoteView(long Symbol, long BidWon, long AskWon, long BidQty, long AskQty);

    /// <summary>[v12.18.60-DEV] 보유 1종목(원 · 주). Target/StopLoss = AI 진입 계획(휴먼 = 0).</summary>
    public readonly record struct KrxArenaPositionView(long Symbol, long Qty, long AvgPriceWon, long CostWon, long MarkWon, long TargetPriceWon, long StopLossPriceWon)
    {
        public long EvalWon => Qty * MarkWon;
        public long PnlWon => EvalWon - CostWon;
    }

    /// <summary>진영 1개 정산 지표(원 · bp).</summary>
    public readonly record struct KrxArenaBookView(
        long CapitalWon, long CashWon, long EquityWon, long RealizedWon, long UnrealizedWon, long FeesWon, long TaxWon,
        long ReturnBp, long MddBp, long WinRateBp, long Fills, long Rejects, int Wins, int Losses, int OpenPositions);

    /// <summary>아레나 스냅샷 투영.</summary>
    public readonly record struct KrxArenaView(
        long Version, long Ticks, bool SessionOpen, int MacroBp, long RationaleCount, DateTime? SessionKst, long DroppedQuotes, KrxArenaBookView Ai, KrxArenaBookView Human,
        bool IsGovernanceEnabled = false, int CurrentRegime = ArenaRegimeCode.Unknown)
    {
        public KrxArenaBookView Side(int side) => side == ArenaSide.Human ? Human : Ai;
        public int Leader => Ai.EquityWon > Human.EquityWon ? ArenaLeader.Ai : Human.EquityWon > Ai.EquityWon ? ArenaLeader.Human : ArenaLeader.Draw;
    }

    /// <summary>AI 근거 1건 투영(Summary = 한국어 설명 요약).</summary>
    public sealed record KrxRationaleView(
        long Seq, long Symbol, int Side, long Qty, long PriceWon, DateTime AtKst, string Trigger, int SentimentScore, int ConfidenceScore,
        long TargetPriceWon, long StopLossPriceWon, double RiskRewardRatio, long RealizedPnlWon, string Summary, ArenaGovernanceCard? Governance = null);

    public interface IKrxArenaPort
    {
        /// <summary>모드 표시명.</summary>
        string ModeName { get; }
        /// <summary>시세 수신 이력(false = 피드 미결선 · 정산 0회).</summary>
        bool Fed { get; }
        /// <summary>최신 스냅샷(경합 상한 초과 = false).</summary>
        bool TryRead(out KrxArenaView view);
        /// <summary>최근 AI 근거(최신 → 과거 · 최대 max건). UI 콜드 경로.</summary>
        IReadOnlyList<KrxRationaleView> RecentRationales(int max);
        /// <summary>휴먼 시장가 주문 → 휴먼 가상 원장 큐(side +1 매수 · −1 매도). 반환 = KrxArenaReject(접수 단계).</summary>
        int SubmitHuman(long symbol, int side, long qty);
        /// <summary>[v12.18.60-DEV] 최신 게시 버전(렌더 폴링 · 변경 시에만 다시 그린다).</summary>
        long Version { get; }
        /// <summary>[v12.18.60-DEV] 시세 공급 상태.</summary>
        KrxArenaFeedStatus Feed { get; }
        /// <summary>[v12.18.60-DEV] 시세 출처 선택(KrxArenaFeed) — 다음 정산에서 양 진영 원장 초기화 후 적용. 범위 밖 = false.</summary>
        bool SelectSource(int source);
        /// <summary>[v12.18.60-DEV] 수신 종목 최우선 호가(종목코드 오름차순).</summary>
        IReadOnlyList<KrxArenaQuoteView> Quotes();
        /// <summary>[v12.18.60-DEV] 진영(ArenaSide) 보유 종목.</summary>
        IReadOnlyList<KrxArenaPositionView> Positions(int side);
        /// <summary>[v12.18.64-DEV] 종목 실시간 뉴스 촉매(아레나 정산 시각 기준 감쇄 · 노이즈/중립/미결속 = null). 기본 구현 = null.</summary>
        ArenaCatalystView? Catalyst(long symbol) => null;
        /// <summary>[v12.18.68-DEV] 플루토 프로 거버너 가동/정지(다음 정산부터 · 미지원 = false). 기본 구현 = false.</summary>
        bool SetGovernanceEnabled(bool enabled) => false;
        /// <summary>[v12.18.68-DEV] 거버너 상태(가동 · 시장 대표 국면 · 연패/쿨다운 · 계수). 기본 구현 = default(미지원 = 정지 · 판정 전).</summary>
        ArenaGovernanceView Governance => default;
    }
}
