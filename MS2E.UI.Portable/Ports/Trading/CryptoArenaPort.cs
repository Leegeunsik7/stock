// ============================================================================
// CryptoArenaPort — v12.18.66-DEV: 업비트 가상자산 24/7 아레나(KRW 마켓 4종 · 휴먼 vs AI · 설명 가능한 AI 근거) UI 경계(순수 DTO · 엔진 타입 참조 0)
//
//   호스트 어댑터가 UpbitCryptoArenaEngine 의 seqlock 스냅샷/근거 피드를 DTO 로 투영한다. UI 는 엔진을 new 하지 않는다.
//   금액/가격 단위 = 원 raw 10^8(long) · 수량 = 코인 raw 10^8(1 = 0.00000001) — 표시 변환은 UI 몫. 세션 = 24시간 무휴(Tradable = 유효 시각).
//   ★Fail-Closed: 이 포트의 주문 경로는 휴먼 가상 원장 큐(SubmitHuman)뿐 — 업비트 주문/인증 · 무장 메서드 없음(외부 주문 송출 0).
//   ★정직 렌더: Fed=false = 시세 피드 미결선(정산 0회) — UI 는 가짜 손익을 그리지 않는다.
//   [v12.18.68-DEV] 플루토 프로 거버너: SetGovernanceEnabled · Governance(ArenaGovernanceView) · 스냅샷 IsGovernanceEnabled/CurrentRegime ·
//     근거 Governance(ArenaGovernanceCard · 감점 태그 · 자가 회고). 기본 구현 = 미지원(정지 · 판정 전).
//   [v12.18.70-DEV] GetClosedTrades(max) = AI 청산 거래 저널(ArenaClosedTradeView · 진입가 → 청산가 · 실현손익 · 수익률 · 청산 유형). 기본 구현 = 빈 목록.
//   [v12.18.71-DEV] 카피 트레이딩: FollowAiTrade · SetAutoCopyEnabled · CopyTrading(ArenaCopyTradingPort DTO) · 24/7 시장 = 개장 게이트 없음. 기본 구현 = 미지원.
// ============================================================================
using System;
using System.Collections.Generic;

namespace MS2E.UI.Portable.Ports.Trading
{
    /// <summary>크립토 아레나 휴먼 주문 결과 코드(접수 · 체결 단계 공용 · 0 = 없음).</summary>
    public static class CryptoArenaReject
    {
        public const int None = 0, SessionInvalid = 1, Invalid = 2, NoQuote = 3, NoLiquidity = 4, InsufficientKrw = 5,
                         InsufficientCoin = 6, BookFull = 7, QueueFull = 8, StaleQuote = 9, BelowMinimumOrder = 10, PriceInvalid = 11;
        public static string Label(int r) => r switch
        {
            None => "-", SessionInvalid => "세션 시각 무효(fail-closed)", Invalid => "주문 값 오류 · 표 밖 마켓", NoQuote => "호가 미수신",
            NoLiquidity => "호가 잔량 없음", InsufficientKrw => "가상 원화 부족", InsufficientCoin => "보유 코인 부족",
            BookFull => "보유 종목 한도", QueueFull => "주문 큐 포화 — 폐기(재시도 없음)", StaleQuote => "호가 지연(2초 초과) — 스트림 단절 차단",
            BelowMinimumOrder => "최소 주문 금액 5,000원 미만", PriceInvalid => "체결가 산출 불가", _ => "?",
        };
    }

    /// <summary>크립토 아레나 시세 출처.</summary>
    public static class CryptoArenaFeed
    {
        public const int UpbitStream = 1, Synthetic = 2;
        public static string Label(int f) => f switch
        {
            UpbitStream => "업비트 공개 WebSocket(KRW-BTC · ETH · XRP · SOL 호가/체결)", Synthetic => "합성 시세(데모 · 24/7 ×20)", _ => "?",
        };
    }

    /// <summary>시세 공급 상태. Pumped=false = 정산 펌프 미결선 · StreamAttached=false = 업비트 스트림 미결선(설정 꺼짐) · StreamConnected = 현재 연결.</summary>
    public readonly record struct CryptoArenaFeedStatus(int Requested, int Applied, bool Pumped, long Pumps, bool StreamAttached, bool StreamConnected,
        long StreamUpdates, long DroppedQuotes)
    {
        public bool Switching => Requested != Applied;
    }

    /// <summary>수신 종목 최우선 호가(원 raw · 코인 raw). Coin = 수량 단위(BTC 등).</summary>
    public readonly record struct CryptoArenaQuoteView(string Market, string Coin, long BidRaw, long AskRaw, long BidQtyRaw, long AskQtyRaw);

    /// <summary>보유 1종목(원 raw · 코인 raw). Target/StopLoss = AI 진입 계획(휴먼 = 0). EvalRaw = 수량 × 현재가.</summary>
    public readonly record struct CryptoArenaPositionView(string Market, string Coin, long QtyRaw, long AvgPriceRaw, long CostRaw, long MarkRaw, long TargetPriceRaw, long StopLossPriceRaw)
    {
        public long EvalRaw => (long)Int128.Min((Int128)QtyRaw * MarkRaw / CryptoArenaUnits.Scale, long.MaxValue);
        public long PnlRaw => EvalRaw - CostRaw;
    }

    /// <summary>원/코인 raw 스케일(10^8).</summary>
    public static class CryptoArenaUnits
    {
        public const long Scale = 100_000_000L;
    }

    /// <summary>진영 1개 정산 지표(원 raw · bp). FeesRaw = 업비트 수수료 0.05% 누계.</summary>
    public readonly record struct CryptoArenaBookView(
        long CapitalRaw, long CashRaw, long EquityRaw, long RealizedRaw, long UnrealizedRaw, long FeesRaw,
        long ReturnBp, long MddBp, long WinRateBp, long Fills, long Rejects, int Wins, int Losses, int OpenPositions);

    /// <summary>크립토 아레나 스냅샷 투영. Tradable = 24/7 연속매매(유효 시각).</summary>
    public readonly record struct CryptoArenaView(
        long Version, long Ticks, long Trades, bool Tradable, int KimchiPremiumBp, int GlobalChangeBp, long RationaleCount, DateTime? SessionKst,
        long DroppedQuotes, CryptoArenaBookView Ai, CryptoArenaBookView Human, bool IsGovernanceEnabled = false, int CurrentRegime = ArenaRegimeCode.Unknown)
    {
        public CryptoArenaBookView Side(int side) => side == ArenaSide.Human ? Human : Ai;
        public int Leader => Ai.EquityRaw > Human.EquityRaw ? ArenaLeader.Ai : Human.EquityRaw > Ai.EquityRaw ? ArenaLeader.Human : ArenaLeader.Draw;
    }

    /// <summary>AI 근거 1건 투영(가격 원 raw · 수량 코인 raw · Summary = 한국어 설명 요약 · BidDepthWon/AskDepthWon = 오더북 두께).</summary>
    public sealed record CryptoRationaleView(
        long Seq, string Market, string Coin, int Side, long QtyRaw, long PriceRaw, DateTime AtKst, string Trigger, int SentimentScore, int ConfidenceScore,
        long TargetPriceRaw, long StopLossPriceRaw, double RiskRewardRatio, long RealizedPnlRaw,
        int ImbalanceBp, int MomentumBp, int FlowBp, int KimchiVolBp, long BidDepthWon, long AskDepthWon, string Summary, ArenaGovernanceCard? Governance = null);

    public interface ICryptoArenaPort
    {
        /// <summary>모드 표시명.</summary>
        string ModeName { get; }
        /// <summary>시세 수신 이력(false = 피드 미결선 · 정산 0회).</summary>
        bool Fed { get; }
        /// <summary>최신 게시 버전(렌더 폴링 · 변경 시에만 다시 그린다).</summary>
        long Version { get; }
        /// <summary>대결 마켓 코드(표 순서).</summary>
        IReadOnlyList<string> Markets { get; }
        /// <summary>최신 스냅샷(경합 상한 초과 = false).</summary>
        bool TryRead(out CryptoArenaView view);
        /// <summary>최근 AI 근거(최신 → 과거 · 최대 max건). UI 콜드 경로.</summary>
        IReadOnlyList<CryptoRationaleView> RecentRationales(int max);
        /// <summary>휴먼 시장가 주문 → 휴먼 가상 원장 큐(side +1 매수 · −1 매도 · 수량 = 코인 raw 10^8). 반환 = CryptoArenaReject(접수 단계).</summary>
        int SubmitHuman(string market, int side, long qtyRaw);
        /// <summary>시세 공급 상태.</summary>
        CryptoArenaFeedStatus Feed { get; }
        /// <summary>시세 출처 선택(CryptoArenaFeed) — 다음 정산에서 양 진영 원장 초기화 후 적용. 범위 밖 = false.</summary>
        bool SelectSource(int source);
        /// <summary>수신 종목 최우선 호가(표 순서).</summary>
        IReadOnlyList<CryptoArenaQuoteView> Quotes();
        /// <summary>진영(ArenaSide) 보유 종목(표 순서).</summary>
        IReadOnlyList<CryptoArenaPositionView> Positions(int side);
        /// <summary>[v12.18.68-DEV] 플루토 프로 거버너 가동/정지(다음 정산부터 · 미지원 = false). 기본 구현 = false.</summary>
        bool SetGovernanceEnabled(bool enabled) => false;
        /// <summary>[v12.18.68-DEV] 거버너 상태(가동 · 시장 대표 국면 · 연패/쿨다운 · 계수). 기본 구현 = default(미지원 = 정지 · 판정 전).</summary>
        ArenaGovernanceView Governance => default;
        /// <summary>[v12.18.70-DEV] AI 청산 거래 저널(최신 → 과거 · 최대 max건 · 기본 50). UI 콜드 경로. 기본 구현 = 빈 목록(미지원).</summary>
        IReadOnlyList<ArenaClosedTradeView> GetClosedTrades(int max = 50) => Array.Empty<ArenaClosedTradeView>();
        /// <summary>[v12.18.71-DEV] AI 근거 1건(rationaleSeq) 1-클릭 복제 → 휴먼 가상 원장 큐(비례 수량 × 배율 · 슬리피지 20bp 보호 · 체결 = 다음 정산 회차). 기본 구현 = 미지원.</summary>
        ArenaCopyResult FollowAiTrade(long rationaleSeq, decimal copyRatio = 1.0m) => new(ArenaCopyCode.Unsupported, 0, 0, rationaleSeq);
        /// <summary>[v12.18.71-DEV] 플루토 자동 미러링 가동/정지(배율 0.1x~3.0x · 다음 정산부터 · 범위 밖/미지원 = false). 기본 구현 = false.</summary>
        bool SetAutoCopyEnabled(bool enabled, decimal ratio = 1.0m) => false;
        /// <summary>[v12.18.71-DEV] 자동 미러링 상태 · 복제 계수 · 마지막 결과. 기본 구현 = default(미지원).</summary>
        ArenaCopyTradingView CopyTrading => default;
        /// <summary>[v12.18.88-GM] 리스크 통제관 비토 기각 로그(최신 → 과거 · 최대 max건 · UI 콜드 경로). 기본 구현 = 미결속(Unwired).</summary>
        ArenaCouncilVetoFeed CouncilVetoes(int max = 20) => ArenaCouncilVetoFeed.Unwired;
    }
}
