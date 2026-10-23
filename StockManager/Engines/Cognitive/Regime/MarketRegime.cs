// ============================================================================
// MarketRegime — v12.18.67-DEV: 플루토 브레인 고도화 — 시장 국면 4종(+ 판정 전)
//
//   TrendingBull     : 이동평균 정배열(중간가 ≥ 빠른 EWMA > 느린 EWMA · 이격 ≥ 15bp) ∧ 호가 불균형 지속 매도 압력 아님.
//   TrendingBear     : 역배열(중간가 ≤ 빠른 EWMA < 느린 EWMA · 이격 ≤ −15bp) ∧ 지속 매수 압력 아님.
//   ChoppyRange      : 위 둘 · 패닉 어느 것도 아님(정배열/역배열 미성립 · 이격 미달 · 불균형 상충).
//   PanicLiquidation : 고변동성 투매(ATR 급등 ∧ 느린 EWMA 하회) 또는 지속 매도 불균형 ∧ 역배열 ∧ 준고변동성 → 신규 매수 100% 차단(fail-closed).
//   판정 = PlutoRegimeClassifier(종목별 상태 · 값 타입 · δ=0).
// ============================================================================
namespace StockManager.Engines.Cognitive.Regime
{
    public enum MarketRegime : byte
    {
        Unknown = 0,
        TrendingBull = 1,
        TrendingBear = 2,
        ChoppyRange = 3,
        PanicLiquidation = 4,
    }

    public static class MarketRegimeText
    {
        public static bool IsDefined(MarketRegime r) => r is >= MarketRegime.Unknown and <= MarketRegime.PanicLiquidation;

        /// <summary>패닉 국면 = 신규 매수 전면 차단.</summary>
        public static bool BlocksEntry(MarketRegime r) => r == MarketRegime.PanicLiquidation;

        public static string Label(MarketRegime r) => r switch
        {
            MarketRegime.TrendingBull => "상승 추세",
            MarketRegime.TrendingBear => "하락 추세",
            MarketRegime.ChoppyRange => "박스권 횡보",
            MarketRegime.PanicLiquidation => "패닉 투매",
            _ => "판정 전",
        };

        /// <summary>근거 카드 국면 뱃지(짧은 영문 코드).</summary>
        public static string Badge(MarketRegime r) => r switch
        {
            MarketRegime.TrendingBull => "BULL",
            MarketRegime.TrendingBear => "BEAR",
            MarketRegime.ChoppyRange => "RANGE",
            MarketRegime.PanicLiquidation => "PANIC",
            _ => "N/A",
        };
    }
}
