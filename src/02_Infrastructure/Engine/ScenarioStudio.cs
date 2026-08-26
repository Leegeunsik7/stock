// ============================================================================
// ScenarioStudio - 시나리오 스튜디오 🅩 (외부 의존 0) (v3.24.0)
//  몬테카를로(무조건 분포)를 넘어 "조건부 스트레스 테스트"를 한다.
//  "시장 -10%", "환율 +5%" 같은 충격을 정의하면, 각 보유 종목의 과거 베타로
//  포트폴리오 영향을 단일팩터 선형모델로 전파한다. 순수·결정적.
//
//  설계 노트 / 숨은 룰:
//   · 시장 팩터 = 보유 종목의 가중 합성 일수익률(외부 인덱스 없이 자기완결)
//   · 베타 = cov(자산, 팩터)/var(팩터), R² = corr² (팩터 분산 0이면 0으로 가드)
//   · 예측 이동 = 베타 × 시장충격%, 포트폴리오 이동 = Σ 비중 × 예측이동
//   · 환율 충격은 호출측(페이지)에서 USD 비중에 직접 가산(별도 축)
//   · 단일팩터 선형 근사 · 과거 베타 기반 — 처방/추정이지 보장이 아님(UI 표기)
//   · 결정적: 동일 입력 → 동일 결과(난수 없음)
// ============================================================================
using System;
using System.Collections.Generic;
using System.Linq;

namespace MS2E.Infrastructure
{
    public class AssetSensitivity
    {
        public string Name { get; set; } = "";
        public double Weight { get; set; }
        public double Beta { get; set; }
        public double R2 { get; set; }
        public double PredictedMovePct { get; set; }
    }

    public class ScenarioResult
    {
        public bool IsValid { get; set; }
        public string Note { get; set; } = "";
        public double FactorShockPct { get; set; }
        public double PortfolioMovePct { get; set; }     // 시장 팩터 영향만(환율 제외)
        public List<AssetSensitivity> Assets { get; set; } = new();
    }

    public static class ScenarioStudio
    {
        /// <summary>보유 종목 가중 합성 시장 팩터(시점별 가중 일수익률).</summary>
        public static double[] BuildMarketFactor(double[][] assetReturns, double[] weights)
        {
            if (assetReturns == null || assetReturns.Length == 0) return Array.Empty<double>();
            int t = assetReturns.Min(a => a?.Length ?? 0);
            if (t < 1) return Array.Empty<double>();
            var w = Normalize(weights, assetReturns.Length);
            var aligned = assetReturns.Select(a => a.Skip(a.Length - t).ToArray()).ToArray();
            var f = new double[t];
            for (int k = 0; k < t; k++)
            {
                double s = 0;
                for (int i = 0; i < aligned.Length; i++) s += w[i] * aligned[i][k];
                f[k] = s;
            }
            return f;
        }

        /// <summary>자산의 팩터 대비 베타와 결정계수(R²).</summary>
        public static (double beta, double r2) Beta(double[] asset, double[] factor)
        {
            if (asset == null || factor == null) return (0, 0);
            int n = Math.Min(asset.Length, factor.Length);
            if (n < 2) return (0, 0);
            var a = asset.Skip(asset.Length - n).ToArray();
            var f = factor.Skip(factor.Length - n).ToArray();
            double ma = a.Average(), mf = f.Average();
            double cov = 0, vf = 0, va = 0;
            for (int i = 0; i < n; i++)
            {
                double da = a[i] - ma, df = f[i] - mf;
                cov += da * df; vf += df * df; va += da * da;
            }
            if (vf <= 1e-12) return (0, 0);
            double beta = cov / vf;
            double r2 = va <= 1e-12 ? 0 : (cov * cov) / (va * vf);
            return (beta, r2);
        }

        /// <summary>시장 충격%를 베타로 전파해 포트폴리오 영향 산출.</summary>
        public static ScenarioResult ApplyShock(double[][] assetReturns, string[] names,
            double[] weights, double factorShockPct, double[]? factor = null)
        {
            var res = new ScenarioResult { FactorShockPct = factorShockPct };
            if (assetReturns == null || assetReturns.Length == 0) { res.Note = "자산이 없습니다."; return res; }
            int n = assetReturns.Length;
            int t = assetReturns.Min(a => a?.Length ?? 0);
            if (t < 2) { res.Note = "수익률 히스토리가 부족합니다(2일+)."; return res; }

            var w = Normalize(weights, n);
            factor ??= BuildMarketFactor(assetReturns, w);
            if (factor.Length < 2) { res.Note = "시장 팩터를 구성할 수 없습니다."; return res; }

            double port = 0;
            for (int i = 0; i < n; i++)
            {
                var (beta, r2) = Beta(assetReturns[i], factor);
                double pred = beta * factorShockPct;
                res.Assets.Add(new AssetSensitivity
                {
                    Name = names != null && i < names.Length ? names[i] : $"#{i + 1}",
                    Weight = w[i], Beta = beta, R2 = r2, PredictedMovePct = pred
                });
                port += w[i] * pred;
            }
            res.PortfolioMovePct = port;
            res.IsValid = true;
            res.Note = $"{n}종목 · {t}일 · 시장 {factorShockPct:+0.0;-0.0}% 충격 → 포트폴리오 {port:+0.0;-0.0}%";
            return res;
        }

        private static double[] Normalize(double[] w, int n)
        {
            if (w == null || w.Length != n) return Enumerable.Repeat(1.0 / n, n).ToArray();
            double s = w.Sum();
            return s > 1e-12 ? w.Select(x => x / s).ToArray() : Enumerable.Repeat(1.0 / n, n).ToArray();
        }
    }
}
