// ============================================================================
// ArenaClosedTradeMapper — v12.18.70-DEV: AI 근거 링 → 청산 거래 저널(ArenaClosedTradeView) 투영(아레나 포트 어댑터 공용 · 콜드 경로)
//
//   근거 링(오래된 → 최신)에서 매도(Side −1) 근거만 최신순으로 골라 최대 max건. 진입가 = 청산 직전 평균단가(AiTradeRationale.EntryPrice) ·
//   수익률 = 실현손익 ÷ (진입가 × 체결수량 ÷ qtyScale). 종목명 = StockDisplayNameResolver(미등록 = 코드).
//   ★엔진 핫패스 무변경 — 판독은 기존 CopyRationales 스냅샷 복사본만 사용.
// ============================================================================
using System;
using System.Collections.Generic;
using MS2E.UI.Portable.Helpers;
using MS2E.UI.Portable.Ports.Trading;
using StockManager.Engines.Arena.Common;

namespace StockManager.Adapters
{
    public static class ArenaClosedTradeMapper
    {
        /// <summary>buf[0..n) = 오래된 → 최신 근거 · code = 엔진 종목 키 → 표시 코드 · qtyScale = 수량 스케일(코인 = 10^8 · 주식 = 1).</summary>
        public static IReadOnlyList<ArenaClosedTradeView> Collect(ReadOnlySpan<AiTradeRationale> buf, int max, Func<long, string> code, long qtyScale)
        {
            if (max <= 0 || buf.IsEmpty) return Array.Empty<ArenaClosedTradeView>();
            var list = new List<ArenaClosedTradeView>(Math.Min(max, buf.Length));
            for (int i = buf.Length - 1; i >= 0 && list.Count < max; i--)
            {
                ref readonly var r = ref buf[i];
                if (r.Side >= 0) continue;
                string c = code(r.Symbol);
                list.Add(new ArenaClosedTradeView(r.Seq, r.AtKst, c, StockDisplayNameResolver.NameOrCode(c), r.Qty, r.EntryPrice, r.PriceWon,
                    r.RealizedPnlWon, ArenaClosedTradeMath.ReturnBp(r.EntryPrice, r.Qty, r.RealizedPnlWon, qtyScale), Kind(r.TriggerType),
                    ArenaTriggerText.Label(r.TriggerType), r.HumanReadableSummary));
            }
            return list;
        }

        /// <summary>엔진 청산 사유 → UI 청산 유형(진입 사유 · 미분류 = Other).</summary>
        public static int Kind(ArenaTrigger t) => t switch
        {
            ArenaTrigger.TakeProfit => ArenaExitKind.TakeProfit,
            ArenaTrigger.StopLoss => ArenaExitKind.StopLoss,
            ArenaTrigger.SessionClose => ArenaExitKind.SessionClose,
            ArenaTrigger.SignalReversal => ArenaExitKind.SignalReversal,
            ArenaTrigger.NewsShock => ArenaExitKind.NewsShock,
            ArenaTrigger.ValuationBubble => ArenaExitKind.ValuationBubble,
            _ => ArenaExitKind.Other,
        };
    }
}
