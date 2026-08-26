// ============================================================================
// RiskAttribution - 리스크 기여도 분해 + 리스크 패리티 🄳 (외부 의존 0) (v3.25.0)
//  "내 리스크가 실제로 어디서 나오는가"를 공분산으로 분해한다(기관급 시각).
//  비중으로는 분산돼 보여도 변동성의 대부분이 소수 종목에서 나올 수 있다 — 이를 폭로.
//   · 한계 리스크 기여 MCTR_i = (Σw)_i / σ_p
//   · 위험 비중 CCTR_i = w_i·(Σw)_i / σ_p  (Euler 분해: Σ CCTR_i = σ_p)
//   · 위험 비중% = w_i(Σw)_i / σ²_p  (합=100%)
//  추가로 수익이 아닌 '리스크를 균등화'하는 리스크 패리티 비중을 대안 타겟으로 제시.
//  순수·결정적. 공분산은 🅚 MarkowitzOptimizer.Covariance 재사용.
//
//  설계 노트 / 숨은 룰:
//   · 단위: 일수익률 %, 포트 변동성 연환산(×√252). 비중은 합=1로 정규화
//   · 리스크 패리티: 동일 위험예산(1/n)으로 가는 곱셈 업데이트(롱온리·정규화) 반복
//   · 공분산은 ridge 없이도 분해는 가능하나 패리티 수렴 위해 작은 ridge 사용
// ============================================================================
using System;
using System.Collections.Generic;
using System.Linq;
using MS2E.UI.Portable.Ports.Quant;   // Phase 94-C — 읽기 전용 결과 계약 구현

namespace MS2E.Infrastructure
{
    public class RiskContribution : IRiskContribution
    {
        public string Name { get; set; } = "";
        public double WeightPct { get; set; }
        public double RiskContribPct { get; set; }   // 위험 비중 (합 100%)
        public double Mctr { get; set; }              // 한계 기여(연환산 %)
        public double Ratio => WeightPct > 1e-9 ? RiskContribPct / WeightPct : 0;  // >1이면 비중보다 리스크가 큼
    }

    public class RiskReport : IRiskReport
    {
        public bool IsValid { get; set; }
        public string Note { get; set; } = "";
        public double PortfolioVolPct { get; set; }   // 연환산
        public List<RiskContribution> Contributions { get; set; } = new();
        public double TopConcentrationPct { get; set; }
        public string TopName { get; set; } = "";
        public string[] Names { get; set; } = Array.Empty<string>();
        public double[] CurrentWeights { get; set; } = Array.Empty<double>();
        public double[] RiskParityWeights { get; set; } = Array.Empty<double>();
        public string Insight { get; set; } = "";
        // ★covariant 명시 구현: List<RiskContribution>(RiskContribution:IRiskContribution) → IReadOnlyList<IRiskContribution>
        IReadOnlyList<IRiskContribution> IRiskReport.Contributions => Contributions;
    }

    public static class RiskAttribution
    {
        private const double TradingDays = 252.0;

        public static RiskReport Analyze(double[][] assetReturns, string[] names, double[] weights)
        {
            var res = new RiskReport { Names = names ?? Array.Empty<string>() };
            if (assetReturns == null || assetReturns.Length == 0) { res.Note = "자산이 없습니다."; return res; }
            int n = assetReturns.Length;
            int t = assetReturns.Min(a => a?.Length ?? 0);
            if (t < 2) { res.Note = "수익률 히스토리가 부족합니다(2일+)."; return res; }

            var R = assetReturns.Select(a => a.Skip(a.Length - t).ToArray()).ToArray();
            var w = Normalize(weights, n);
            var cov = MarkowitzOptimizer.Covariance(R);

            var sw = MatVec(cov, w);                         // Σw (daily %²/%)
            double var = 0; for (int i = 0; i < n; i++) var += w[i] * sw[i];
            double volDaily = Math.Sqrt(Math.Max(0, var));
            res.PortfolioVolPct = volDaily * Math.Sqrt(TradingDays);

            for (int i = 0; i < n; i++)
            {
                double cctr = volDaily > 1e-12 ? w[i] * sw[i] / volDaily : 0;        // 위험 기여(일)
                double rcPct = var > 1e-12 ? w[i] * sw[i] / var * 100.0 : 100.0 / n; // 위험 비중%
                res.Contributions.Add(new RiskContribution
                {
                    Name = names != null && i < names.Length ? names[i] : $"#{i + 1}",
                    WeightPct = w[i] * 100.0,
                    RiskContribPct = rcPct,
                    Mctr = (volDaily > 1e-12 ? sw[i] / volDaily : 0) * Math.Sqrt(TradingDays)
                });
            }
            res.Contributions = res.Contributions.OrderByDescending(c => c.RiskContribPct).ToList();
            res.CurrentWeights = w;
            res.RiskParityWeights = RiskParityWeights(R);

            var top = res.Contributions.First();
            res.TopConcentrationPct = top.RiskContribPct;
            res.TopName = top.Name;
            res.Insight = BuildInsight(res, n);
            res.IsValid = true;
            res.Note = $"{n}종목 · {t}일 · 포트 변동성 {res.PortfolioVolPct:0.0}%(연)";
            return res;
        }

        /// <summary>동일 위험기여(리스크 패리티) 롱온리 비중. 곱셈 업데이트 반복.</summary>
        public static double[] RiskParityWeights(double[][] R, int iters = 300)
        {
            int n = R.Length;
            if (n == 1) return new[] { 1.0 };
            var cov = MarkowitzOptimizer.Covariance(R);
            for (int i = 0; i < n; i++) cov[i][i] += 1e-8;     // 수치 안정

            var w = Enumerable.Repeat(1.0 / n, n).ToArray();
            for (int it = 0; it < iters; it++)
            {
                var sw = MatVec(cov, w);
                double total = 0; for (int i = 0; i < n; i++) total += w[i] * sw[i];
                if (total <= 1e-18) break;
                double target = total / n;
                double sum = 0;
                var nw = new double[n];
                for (int i = 0; i < n; i++)
                {
                    double rc = w[i] * sw[i];
                    double factor = rc > 1e-18 ? target / rc : 2.0;          // 과(過)기여↓ · 부족↑
                    nw[i] = Math.Max(1e-12, w[i] * Math.Sqrt(factor));        // 완만한 스텝(√)으로 안정
                    sum += nw[i];
                }
                for (int i = 0; i < n; i++) nw[i] /= sum;
                double maxMove = 0; for (int i = 0; i < n; i++) maxMove = Math.Max(maxMove, Math.Abs(nw[i] - w[i]));
                w = nw;
                if (maxMove < 1e-9) break;
            }
            return w;
        }

        private static string BuildInsight(RiskReport r, int n)
        {
            var top = r.Contributions.First();
            double evenShare = 100.0 / n;
            if (top.RiskContribPct >= evenShare * 1.8 && top.Ratio > 1.3)
                return $"⚠️ 리스크가 {top.Name}에 집중 — 비중 {top.WeightPct:0.0}%인데 위험은 {top.RiskContribPct:0.0}% 차지(비중의 {top.Ratio:0.0}배).";
            if (top.RiskContribPct >= evenShare * 1.4)
                return $"리스크 상위 {top.Name}가 {top.RiskContribPct:0.0}%를 차지합니다(균등 시 {evenShare:0.0}%). 리스크 패리티로 분산을 고려해 보세요.";
            return "리스크가 비교적 고르게 분산되어 있습니다.";
        }

        private static double[] MatVec(double[][] m, double[] v)
        {
            int n = m.Length; var o = new double[n];
            for (int i = 0; i < n; i++) { double s = 0; for (int j = 0; j < n; j++) s += m[i][j] * v[j]; o[i] = s; }
            return o;
        }

        private static double[] Normalize(double[] w, int n)
        {
            if (w == null || w.Length != n) return Enumerable.Repeat(1.0 / n, n).ToArray();
            var c = w.Select(x => Math.Max(0, x)).ToArray();
            double s = c.Sum();
            return s > 1e-12 ? c.Select(x => x / s).ToArray() : Enumerable.Repeat(1.0 / n, n).ToArray();
        }
    }
}
