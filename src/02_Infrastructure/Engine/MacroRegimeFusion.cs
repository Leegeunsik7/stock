// ============================================================================
// MacroRegimeFusion — S20 #2: 거시 매크로 레짐 융합 순수 코어
//
//  섀도우 그린하우스의 적응도 평가에 '시장 전체의 눈'을 달아준다. 외부 매크로 변수
//  (지수 변동성 VKOSPI·환율 변동성·국채 금리 스프레드)로 시장 스트레스를 [0,1]로 환산하고,
//  스트레스가 높을수록 FitnessEvaluator 의 곱셈 거부 게이트 허용오차(MddTolPct·ZTol)를
//  실시간으로 '수축'시켜 공격형(고낙폭·고마찰) 게놈을 일제히 사살하고 보수형만 생존시킨다.
//
//  ── 순수 결정론 ────────────────────────────────────────────────────────────
//  외부 의존성 0 · 사이드 이펙트 0 · 초월함수 회피(clamp/min/max/arith) → 플랫폼 무관.
//  외부 매크로 데이터의 '수입(async fetch)'은 본 코어의 책임이 아니다 — 호출자가 MacroSnapshot 을
//  주입한다. 실 데이터 피드 결속은 별도 마일스톤(정직 경계).
//
//  ── 게이트 수축 수식 ───────────────────────────────────────────────────────
//  mult = 1 - stress·(1 - MinToleranceMult)         // stress 0→1.0, stress 1→MinToleranceMult
//  MddTolPct_eff = MddTolPct · mult,  ZTol_eff = ZTol · mult
//  → 위기(stress↑)일수록 허용오차 축소 → gate_R/gate_G 가 같은 낙폭·마찰에 더 가혹.
// ============================================================================
using System;

namespace StockManager.Common
{
    public enum MacroRegime { Calm, Elevated, Turbulent, Crisis }

    /// <summary>외부에서 주입되는 거시 지표 순간값(원시).</summary>
    public readonly record struct MacroSnapshot(
        double IndexVolatility,   // 지수 변동성 (예: VKOSPI 레벨)
        double FxVolatility,      // 환율 실현 변동성 (%)
        double RateSpread);       // 장단기 금리 스프레드 (bp; 낮을수록/역전될수록 스트레스↑)

    /// <summary>레짐 분류·정규화 임계와 게이트 수축 강도(전부 init — 환경별 튜닝).</summary>
    public sealed record MacroRegimeBounds
    {
        // 각 지표의 평온/위기 기준 (정규화용 — 예시 기본값, 운영자 보정 대상)
        public double IndexVolCalm    { get; init; } = 15.0;
        public double IndexVolCrisis  { get; init; } = 40.0;
        public double FxVolCalm       { get; init; } = 5.0;
        public double FxVolCrisis     { get; init; } = 20.0;
        public double RateSpreadCalm  { get; init; } = 100.0;   // +100bp 정상
        public double RateSpreadCrisis{ get; init; } = -20.0;   // 역전 = 위기

        /// <summary>위기(stress=1)에서의 허용오차 최저 배율 — 게이트 수축 한계(기본 0.25 = 4× 가혹).</summary>
        public double MinToleranceMult { get; init; } = 0.25;

        // 레짐 경계 (stress score 컷오프)
        public double ElevatedAt  { get; init; } = 0.25;
        public double TurbulentAt { get; init; } = 0.50;
        public double CrisisAt    { get; init; } = 0.75;

        public static MacroRegimeBounds Default { get; } = new();
    }

    public static class MacroRegimeFusion
    {
        /// <summary>
        /// 매크로 스냅샷을 시장 스트레스 [0,1]로 환산. 세 지표 중 '최악'이 방어를 주도(가장 보수적).
        /// </summary>
        public static double StressScore(MacroSnapshot s, MacroRegimeBounds? bounds = null)
        {
            var b = bounds ?? MacroRegimeBounds.Default;
            double nIndex = Norm(s.IndexVolatility, b.IndexVolCalm, b.IndexVolCrisis);
            double nFx    = Norm(s.FxVolatility,    b.FxVolCalm,    b.FxVolCrisis);
            // 금리 스프레드는 '낮을수록' 스트레스 → 반전 정규화
            double nRate  = Norm(s.RateSpread,      b.RateSpreadCalm, b.RateSpreadCrisis);
            return Max3(nIndex, nFx, nRate);
        }

        /// <summary>스트레스 점수를 4단계 레짐으로 분류.</summary>
        public static MacroRegime Classify(double stress, MacroRegimeBounds? bounds = null)
        {
            var b = bounds ?? MacroRegimeBounds.Default;
            if (stress < b.ElevatedAt)  return MacroRegime.Calm;
            if (stress < b.TurbulentAt) return MacroRegime.Elevated;
            if (stress < b.CrisisAt)    return MacroRegime.Turbulent;
            return MacroRegime.Crisis;
        }

        /// <summary>
        /// 스트레스에 비례해 곱셈 거부 게이트의 허용오차(MddTolPct·ZTol)를 수축한 새 FitnessBounds 반환.
        /// 나머지 보상·가중 상수는 보존. stress=0 이면 원본 그대로.
        /// </summary>
        public static FitnessBounds Modulate(
            FitnessBounds baseBounds, double stress, MacroRegimeBounds? bounds = null)
        {
            if (baseBounds is null) throw new ArgumentNullException(nameof(baseBounds));
            var b = bounds ?? MacroRegimeBounds.Default;

            double s    = Clamp01(stress);
            double mult = 1.0 - s * (1.0 - b.MinToleranceMult);     // [MinToleranceMult, 1]
            if (mult < b.MinToleranceMult) mult = b.MinToleranceMult;
            if (mult > 1.0)                mult = 1.0;

            return baseBounds with
            {
                MddTolPct = baseBounds.MddTolPct * mult,   // 낙폭 게이트 수축
                ZTol      = baseBounds.ZTol      * mult,   // 마찰 게이트 수축
            };
        }

        /// <summary>편의: 스냅샷 → (레짐, 스트레스, 수축된 FitnessBounds) 일괄 산출.</summary>
        public static (MacroRegime Regime, double Stress, FitnessBounds Bounds) Fuse(
            MacroSnapshot snapshot, FitnessBounds baseBounds, MacroRegimeBounds? bounds = null)
        {
            var b      = bounds ?? MacroRegimeBounds.Default;
            double str = StressScore(snapshot, b);
            return (Classify(str, b), str, Modulate(baseBounds, str, b));
        }

        // ── 헬퍼 (결정론) ─────────────────────────────────────────────────────

        // calm↦0, crisis↦1, 그 외 클램프. crisis<calm(반전 지표)도 동일 식으로 처리.
        private static double Norm(double x, double calm, double crisis)
        {
            double denom = crisis - calm;
            if (Math.Abs(denom) < 1e-12) return x == calm ? 0.0 : 1.0;
            return Clamp01((x - calm) / denom);
        }

        private static double Max3(double a, double b, double c)
            => Math.Max(a, Math.Max(b, c));

        private static double Clamp01(double v) => v < 0.0 ? 0.0 : (v > 1.0 ? 1.0 : v);
    }
}
