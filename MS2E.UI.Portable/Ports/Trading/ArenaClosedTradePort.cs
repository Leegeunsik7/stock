// ============================================================================
// ArenaClosedTradePort — v12.18.70-DEV: AI 실현손익 체결 내역(청산 거래 저널) UI 경계(순수 DTO · 엔진 타입 참조 0)
//
//   호스트 어댑터가 AI 근거 링(최대 128건)에서 매도(청산) 근거만 골라 최신순 ArenaClosedTradeView 로 투영한다(콜드 경로 · 할당 허용).
//   가격/손익 단위 = 아레나 원장 단위 그대로(국내 = 원 · 미국 = 센트 · 코인 = 원 raw 10^8 · 코인 수량 = raw 10^8) — 표시 변환은 페이지 몫.
//   ★정직 렌더: 진입가 미기록(EntryPrice ≤ 0) = 수익률 산출 불가(HasReturn=false · '-' 표시 · 가짜 0% 금지).
//   ★liveArmed:false — 기록 판독일 뿐 주문 수단이 아니다.
// ============================================================================
using System;

namespace MS2E.UI.Portable.Ports.Trading
{
    /// <summary>청산 유형(엔진 청산 사유의 UI 분류 · 0 = 미분류).</summary>
    public static class ArenaExitKind
    {
        public const int Other = 0, TakeProfit = 1, StopLoss = 2, SessionClose = 3, SignalReversal = 4, NewsShock = 5, ValuationBubble = 6;

        public static string Label(int k) => k switch
        {
            TakeProfit => "익절", StopLoss => "손절", SessionClose => "장마감", SignalReversal => "시그널 반전",
            NewsShock => "악재 긴급 청산", ValuationBubble => "과열 조기 청산", _ => "기타",
        };

        public static string Css(int k) => k switch
        {
            TakeProfit => "tp", StopLoss => "sl", SessionClose => "close", SignalReversal => "rev", NewsShock => "shock", ValuationBubble => "bubble", _ => "etc",
        };
    }

    /// <summary>
    /// AI 청산 1건(Seq = 해당 매도 근거 순번 — 근거 카드와 1:1). Code = 표시 종목 코드(000660 · NVDA · KRW-BTC) · Name = 한글 종목명(미등록 = 코드).
    /// RealizedPnl = 수수료 · 세금 차감 후 실현손익 · ReturnBp = 실현손익 ÷ (진입가 × 체결수량) 만분율.
    /// </summary>
    public sealed record ArenaClosedTradeView(
        long Seq, DateTime AtKst, string Code, string Name, long Qty, long EntryPrice, long ExitPrice, long RealizedPnl, long ReturnBp,
        int ExitKind, string Reason, string Summary)
    {
        public bool HasReturn => EntryPrice > 0;
        public string ExitLabel => ArenaExitKind.Label(ExitKind);
    }

    /// <summary>청산 저널 산식(순수 · 오버플로 안전).</summary>
    public static class ArenaClosedTradeMath
    {
        /// <summary>실현손익 ÷ 진입 원가(진입가 × 수량 ÷ qtyScale) → bp(0 방향 절단 · ±10^9 bp 포화). 진입가/수량/스케일 ≤ 0 = 0.</summary>
        public static long ReturnBp(long entryPrice, long qty, long realizedPnl, long qtyScale = 1)
        {
            if (entryPrice <= 0 || qty <= 0 || qtyScale <= 0) return 0;
            Int128 basis = (Int128)entryPrice * qty / qtyScale;
            if (basis <= 0) return 0;
            Int128 bp = (Int128)realizedPnl * 10_000 / basis;
            return (long)Int128.Clamp(bp, -1_000_000_000, 1_000_000_000);
        }
    }
}
