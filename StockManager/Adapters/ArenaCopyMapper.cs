// ============================================================================
// ArenaCopyMapper — v12.18.71-DEV: 카피 트레이딩 · 시초가 개장 게이트 엔진 계수 → 포트 DTO 투영(3대 아레나 어댑터 공용 · 콜드 경로)
//
//   FollowAiTrade 공용 절차: 배율 검증 → 근거 링에서 순번 탐색(만료 = NotFound) → 스냅샷 평가자산 비례 수량(0 = ZeroQty) → 엔진 SubmitFollow(접수).
//   ★정직 렌더: 근거 링 밖 · 스냅샷 경합 = 복제 거부(가짜 수량 0). ★liveArmed:false — 목적지 = 엔진 휴먼 가상 원장 큐뿐.
// ============================================================================
using System;
using MS2E.UI.Portable.Ports.Trading;
using StockManager.Engines.Arena.Common;

namespace StockManager.Adapters
{
    internal static class ArenaCopyMapper
    {
        public delegate int SubmitFollow(long symbol, int side, long qty, long refPx);

        /// <summary>근거 1건 복제 접수(비례 수량 · 엔진 거부 코드 → ArenaCopyCode).</summary>
        public static ArenaCopyResult Follow(ReadOnlySpan<AiTradeRationale> ring, long seq, decimal ratio, bool snapOk, in DuelBookStats ai, in DuelBookStats human,
            SubmitFollow submit, Func<int, int> copyCode)
        {
            if (!ArenaCopyMath.TryRatioBp(ratio, out int ratioBp)) return new(ArenaCopyCode.Invalid, 0, 0, seq);
            int at = -1;
            for (int i = 0; i < ring.Length; i++) if (ring[i].Seq == seq) { at = i; break; }
            if (at < 0) return new(ArenaCopyCode.NotFound, 0, 0, seq);
            ref readonly var r = ref ring[at];
            if (!snapOk) return new(ArenaCopyCode.Rejected, r.Side, 0, seq);
            long qty = ArenaCopyMath.ScaleQty(r.Qty, ai.EquityWon, human.EquityWon, ratioBp);
            if (qty <= 0) return new(ArenaCopyCode.ZeroQty, r.Side, 0, seq);
            return new(copyCode(submit(r.Symbol, r.Side, qty, r.PriceWon)), r.Side, qty, seq);
        }

        public static ArenaCopyTradingView View(bool ok, in ArenaCopyLedger l, bool auto, int ratioBp, Func<long, string> code)
            => new(true, auto, ratioBp, ok ? l.Mirrored : 0, ok ? l.Followed : 0, ok ? l.CompanionCloses : 0, ok ? l.SlippageCancels : 0, ok ? l.CashRejects : 0,
                ok ? l.OtherRejects : 0, ok ? l.LastEvent : 0, (int)l.LastKind, l.LastCode, l.LastSide, l.LastQty, ok && l.LastEvent > 0 ? code(l.LastSymbol) : null);

        public static ArenaOpeningAuctionView Auction(bool ok, in ArenaAuctionStats a)
            => ok ? new(true, a.PreOpen, a.Parked, a.Fills, a.Rejects, a.GapVetoes, a.GapFillEntries) : new(true, false, 0, 0, 0, 0, 0);

        /// <summary>개장 갭 1종목(전일 종가 없음 ∧ 미개장 = 생략 false).</summary>
        public static bool TryGap(string code, in OpenGapState g, out ArenaOpenGapView view)
        {
            if (g.PrevCloseX2 <= 0 && !g.Opened) { view = default; return false; }
            view = new ArenaOpenGapView(code, g.PrevCloseX2 / 2, g.OpenX2 / 2, g.GapBp, (int)g.Action, MarketOpenAuctionGate.InOpeningWindow(in g));
            return true;
        }
    }
}
