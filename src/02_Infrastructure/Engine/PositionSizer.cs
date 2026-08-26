// ============================================================================
// PositionSizer - 엣지 인지 포지션 사이저 🄷 (외부 의존 0) (v3.28.0)
//  "무엇을(MPT)·어느 비중(리스크)"을 넘어 "그래서 이번 셋업에 얼마를 걸까"를 답한다.
//  엣지(승률·손익비)와 변동성을 결합해 켈리 + 변동성 타깃 비중을 산출하고 보수적 쪽을 권장.
//   · 켈리 f* = p − (1−p)/b  (p=승률, b=손익비). 음수면 엣지 없음 → 0
//   · 분할 켈리(fraction·기본 0.5)와 상한(maxWeight)으로 과대 베팅 방지
//   · 변동성 타깃 w = targetVol / assetVol  (상한 적용)
//   · 권장 = min(켈리, 변동성타깃), 기대값≤0이면 진입 보류
//  순수·결정적. EdgeFromReturns는 🄲 이벤트스터디/백테스트의 미래수익 표본에서 엣지 추정.
//
//  ⚠️ 켈리는 정상성·독립성을 가정 — 분할 켈리 권장, 정보 제공이며 투자 권유가 아님(UI 표기).
// ============================================================================
using System;
using System.Collections.Generic;
using System.Linq;

namespace MS2E.Infrastructure
{
    public class SizeResult
    {
        public bool IsValid { get; set; }
        public string Note { get; set; } = "";
        public double WinProbPct { get; set; }
        public double PayoffRatio { get; set; }
        public double ExpectancyPct { get; set; }
        public double KellyFull { get; set; }       // 전체 켈리 f*
        public double KellyFraction { get; set; }   // 적용 분할(예 0.5)
        public double KellyWeight { get; set; }     // 분할·상한 적용 켈리 비중
        public double VolWeight { get; set; }       // 변동성 타깃 비중
        public double RecommendedWeight { get; set; }
        public double AssetVolAnnualPct { get; set; }
        public string Verdict { get; set; } = "";
    }

    public static class PositionSizer
    {
        /// <summary>미래수익 표본 → (승률, 손익비, 기대값%).</summary>
        public static (double winProb, double payoff, double expectancy) EdgeFromReturns(IReadOnlyList<double> fwd)
        {
            if (fwd == null || fwd.Count == 0) return (0, 0, 0);
            int n = fwd.Count;
            var wins = fwd.Where(r => r > 0).ToList();
            var losses = fwd.Where(r => r < 0).Select(r => -r).ToList();
            double p = (double)wins.Count / n;
            double avgWin = wins.Count > 0 ? wins.Average() : 0;
            double avgLoss = losses.Count > 0 ? losses.Average() : 0;
            double payoff = avgLoss > 1e-9 ? avgWin / avgLoss : (avgWin > 0 ? 10.0 : 0);  // 무손실 → 손익비 상한
            return (p, payoff, fwd.Average());
        }

        /// <summary>켈리 비중 = clamp(fraction · (p − (1−p)/b), 0, cap). 엣지 없으면 0.</summary>
        public static double Kelly(double p, double b, double fraction = 0.5, double cap = 0.25)
        {
            if (b <= 1e-9) return 0;
            double f = p - (1 - p) / b;
            if (f <= 0) return 0;
            return Math.Clamp(fraction * f, 0, cap);
        }

        /// <summary>변동성 타깃 비중 = clamp(targetVol/assetVol, 0, cap).</summary>
        public static double VolTarget(double assetVolAnnualPct, double targetVolAnnualPct, double cap = 1.0)
        {
            if (assetVolAnnualPct <= 1e-9) return cap;
            return Math.Clamp(targetVolAnnualPct / assetVolAnnualPct, 0, cap);
        }

        public static SizeResult Suggest(IReadOnlyList<double> forwardReturns, double assetVolAnnualPct,
            double targetVolAnnualPct = 15, double kellyFraction = 0.5, double maxWeight = 0.25)
        {
            var res = new SizeResult { AssetVolAnnualPct = assetVolAnnualPct, KellyFraction = kellyFraction };
            if (forwardReturns == null || forwardReturns.Count < 5) { res.Note = "표본 부족(5+)."; return res; }

            var (p, b, exp) = EdgeFromReturns(forwardReturns);
            res.WinProbPct = p * 100; res.PayoffRatio = b; res.ExpectancyPct = exp;
            double fStar = b > 1e-9 ? p - (1 - p) / b : 0;
            res.KellyFull = Math.Max(0, fStar);
            res.KellyWeight = Kelly(p, b, kellyFraction, maxWeight);
            res.VolWeight = VolTarget(assetVolAnnualPct, targetVolAnnualPct, maxWeight);
            res.RecommendedWeight = (exp <= 0 || fStar <= 0) ? 0 : Math.Min(res.KellyWeight, res.VolWeight);
            res.IsValid = true;
            res.Verdict = res.RecommendedWeight <= 0
                ? "엣지 없음 — 진입 보류"
                : $"권장 비중 {res.RecommendedWeight * 100:0.0}% (켈리·변동성 중 보수적)";
            res.Note = $"{forwardReturns.Count}표본 · 승률 {res.WinProbPct:0}% · 손익비 {b:0.00} · 기대값 {exp:+0.0;-0.0}%";
            return res;
        }
    }
}
