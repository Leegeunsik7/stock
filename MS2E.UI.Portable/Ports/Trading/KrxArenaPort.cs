// ============================================================================
// KrxArenaPort — v12.18.59-DEV: 국내 대형주 아레나(휴먼 vs AI · 설명 가능한 AI 근거) UI 경계(순수 DTO · 엔진 타입 참조 0)
//
//   호스트 어댑터가 KrxArenaEngine 의 seqlock 스냅샷/근거 피드를 DTO 로 투영한다. UI 는 엔진을 new 하지 않는다.
//   ★Fail-Closed: 이 포트의 주문 경로는 휴먼 가상 원장 큐(SubmitHuman)뿐 — 브로커 · 무장 · 실주문 메서드 없음.
//   ★정직 렌더: Fed=false = 시세 피드 미결선(정산 0회) — UI 는 가짜 손익을 그리지 않는다.
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

    /// <summary>진영 1개 정산 지표(원 · bp).</summary>
    public readonly record struct KrxArenaBookView(
        long CapitalWon, long CashWon, long EquityWon, long RealizedWon, long UnrealizedWon, long FeesWon, long TaxWon,
        long ReturnBp, long MddBp, long WinRateBp, long Fills, long Rejects, int Wins, int Losses, int OpenPositions);

    /// <summary>아레나 스냅샷 투영.</summary>
    public readonly record struct KrxArenaView(
        long Version, long Ticks, bool SessionOpen, int MacroBp, long RationaleCount, KrxArenaBookView Ai, KrxArenaBookView Human)
    {
        public int Leader => Ai.EquityWon > Human.EquityWon ? ArenaLeader.Ai : Human.EquityWon > Ai.EquityWon ? ArenaLeader.Human : ArenaLeader.Draw;
    }

    /// <summary>AI 근거 1건 투영(Summary = 한국어 설명 요약).</summary>
    public sealed record KrxRationaleView(
        long Seq, long Symbol, int Side, long Qty, long PriceWon, DateTime AtKst, string Trigger, int SentimentScore, int ConfidenceScore,
        long TargetPriceWon, long StopLossPriceWon, double RiskRewardRatio, long RealizedPnlWon, string Summary);

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
    }
}
