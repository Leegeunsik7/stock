// ============================================================================
// ArenaCatalystMapper — v12.18.64-DEV: 플루토 버퍼 판정 → ArenaCatalystView 투영(아레나 포트 어댑터 공용 · 콜드 경로)
//
//   기준 시각 = 아레나 마지막 정산 KST − 9h(UTC) — 엔진이 그 정산에서 본 감쇄 점수와 같은 값(합성/리플레이 세션 시각 그대로 · 벽시계 무관).
//   정산 0회(기준 시각 없음) · 버퍼 미결속 · 미기록 · 노이즈 · 실효 0 = null(배지 생략).
// ============================================================================
using System;
using MS2E.UI.Portable.Ports.Trading;
using StockManager.Engines.Cognitive.News;

namespace StockManager.Adapters
{
    public static class ArenaCatalystMapper
    {
        /// <summary>종목 촉매 투영(lastKstTicks = 아레나 마지막 정산 KST).</summary>
        public static ArenaCatalystView? Project(PlutoSentimentBuffer? buffer, long symbol, long lastKstTicks)
        {
            if (buffer is null || symbol <= 0 || lastKstTicks <= PlutoClock.KstOffsetTicks) return null;
            if (!buffer.TryRead(symbol, out var f)) return null;
            var tag = new PlutoNewsTag(in f, lastKstTicks - PlutoClock.KstOffsetTicks);
            if (!tag.HasCatalyst) return null;
            int kind = tag.IsShock ? ArenaCatalystKind.Shock : tag.EffectiveScore > 0 ? ArenaCatalystKind.Positive : ArenaCatalystKind.Negative;
            long hlTicks = CatalystText.HalfLifeTicks(f.Category);
            int pct = hlTicks <= 0 ? 0 : (int)Math.Min(100, tag.AgeTicks * 100 / hlTicks);
            return new ArenaCatalystView(kind, tag.EffectiveScore, f.Score, CatalystText.Label(f.Category), CatalystText.HalfLifeMinutes(f.Category),
                tag.AgeMinutes, pct, f.Headline, CatalystText.SourceLabel(f.Source));
        }
    }
}
