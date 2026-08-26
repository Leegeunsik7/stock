// ============================================================================
// AlphaEngine — 진성 알파 인과 추론 엔진 (Season 10 #1, v3.72.0)
//  OLS 회귀로 베타(시장 민감도)를 추정하고, 반사실(Counterfactual) 시뮬레이션으로
//  시장 기여분(Beta)과 순수 실력(True Alpha)을 분리한다.
//  순수 결정론 · 외부 의존 0 · System.Linq + 기본 수학만 사용
//
//  Beta   = Cov(Rp, Rm) / Var(Rm)          ← OLS 최소제곱 회귀 (편향 추정량)
//  Alpha  = Rp - (β·Rm + Rf)               ← CAPM 잉여 수익 (일간)
//  Resid  = Rp_i - β·Rm_i                  ← 시장 기여 제거된 일간 잉여
//  TrueAlpha% = Π(1 + Resid_i) - 1         ← 복리 누적 (단순합 아님)
// ============================================================================
using System;
using System.Collections.Generic;
using System.Linq;

namespace StockManager.Common
{
    public sealed record AlphaPoint(
        DateTime Date,
        double CumActual,           // 실제 누적 수익률 %
        double CumCounterfactual,   // 시장 기여 제거 후 누적 수익률 %
        double CumBenchmark);       // 벤치마크 누적 수익률 %

    public sealed record AlphaReport(
        bool IsValid,
        string Symbol,
        string Benchmark,
        int AlignedDays,
        // ── OLS 회귀 결과 ──────────────────────────────────────────────────
        double Beta,                // 시장 민감도 (1.0 = 시장과 동일 움직임)
        double R2Pct,               // 회귀 결정계수 (0~100%) — 시장이 설명하는 분산 비율
        // ── 알파 분해 ──────────────────────────────────────────────────────
        double TotalReturnPct,          // 전체 누적 수익률 %
        double MarketAttributedPct,     // 시장 기여분 (Total - TrueAlpha)
        double TrueAlphaPct,            // 순수 알파 % (반사실 — 시장 제거)
        double TrueAlphaAnnualPct,      // 연환산 True Alpha %
        double InformationRatio,        // 잉여 수익의 연환산 Sharpe
        // ── 반사실 계열 ────────────────────────────────────────────────────
        IReadOnlyList<AlphaPoint> Series,
        string Note);

    public static class AlphaEngine
    {
        public static AlphaReport Compute(
            string symbol,
            IReadOnlyList<Candle> portfolioCandles,
            IReadOnlyList<Candle> benchmarkCandles,
            string benchmarkName,
            double annualRiskFreeRate = 0.035)
        {
            // ── 1. 일간 수익률 딕셔너리 구축 ──
            var pfDict = BuildReturnDict(portfolioCandles);
            var bmDict = BuildReturnDict(benchmarkCandles);

            // ── 2. 날짜 Inner Join (휴장일 불일치 자동 제거) ──
            var aligned = pfDict.Keys
                .Where(bmDict.ContainsKey)
                .OrderBy(d => d)
                .ToList();

            if (aligned.Count < 30)
                return Invalid(symbol, benchmarkName, aligned.Count,
                    $"정합 거래일 부족 ({aligned.Count}일, 최소 30일 필요).");

            double[] rp = aligned.Select(d => pfDict[d]).ToArray();
            double[] rm = aligned.Select(d => bmDict[d]).ToArray();
            int n = rp.Length;
            double rfDaily = annualRiskFreeRate / 252.0;

            // ── 3. OLS: Beta = Cov(Rp, Rm) / Var(Rm) ──
            double rpMean = Mean(rp);
            double rmMean = Mean(rm);
            double cov = 0.0, varM = 0.0, varP = 0.0;
            for (int i = 0; i < n; i++)
            {
                double dp = rp[i] - rpMean;
                double dm = rm[i] - rmMean;
                cov  += dp * dm;
                varM += dm * dm;
                varP += dp * dp;
            }
            cov  /= n;
            varM /= n;
            varP /= n;

            double beta = varM > 1e-12 ? cov / varM : 0.0;
            // R² = ρ² = Cov² / (Var(Rp) × Var(Rm))
            double r2 = varP > 1e-12 && varM > 1e-12
                ? Math.Clamp((cov * cov) / (varP * varM), 0.0, 1.0)
                : 0.0;

            // ── 4. 잉여(Residual) 계열 — 시장 기여 제거 ──
            double[] resid = new double[n];
            for (int i = 0; i < n; i++)
                resid[i] = rp[i] - beta * rm[i];

            // ── 5. 연환산 알파 (CAPM) ──
            double alphaDaily  = rpMean - (beta * rmMean + rfDaily);
            double alphaAnnual = alphaDaily * 252.0;

            // ── 6. Information Ratio = 잉여의 연환산 Sharpe ──
            double residMean = Mean(resid);
            double residStd  = StdDev(resid);
            double ir = residStd > 1e-12
                ? (residMean - rfDaily) / residStd * Math.Sqrt(252.0)
                : 0.0;

            // ── 7. 복리 누적 계열 (AlphaPoint 배열) ──
            var series     = new List<AlphaPoint>(n);
            double cumActual = 1.0, cumCf = 1.0, cumBm = 1.0;
            for (int i = 0; i < n; i++)
            {
                cumActual *= 1.0 + rp[i];
                cumCf     *= 1.0 + resid[i];
                cumBm     *= 1.0 + rm[i];
                series.Add(new AlphaPoint(aligned[i],
                    (cumActual - 1.0) * 100.0,
                    (cumCf    - 1.0) * 100.0,
                    (cumBm    - 1.0) * 100.0));
            }

            double totalRetPct  = (cumActual - 1.0) * 100.0;
            double trueAlphaPct = (cumCf     - 1.0) * 100.0;
            double mktAttrPct   = totalRetPct - trueAlphaPct;

            return new AlphaReport(true, symbol, benchmarkName, n,
                Math.Round(beta, 4),
                Math.Round(r2 * 100.0, 2),
                Math.Round(totalRetPct,  2),
                Math.Round(mktAttrPct,   2),
                Math.Round(trueAlphaPct, 2),
                Math.Round(alphaAnnual * 100.0, 2),
                Math.Round(ir, 3),
                series,
                $"{n}일 거래일 기준, {benchmarkName} 벤치마크, 무위험이율 {annualRiskFreeRate * 100:0.#}%.");
        }

        // ── 일간 수익률 딕셔너리 (close→close, prev≤0 skip) ──
        internal static Dictionary<DateTime, double> BuildReturnDict(IReadOnlyList<Candle> candles)
        {
            var d = new Dictionary<DateTime, double>(candles.Count);
            for (int i = 1; i < candles.Count; i++)
            {
                decimal prev = candles[i - 1].Close;
                if (prev <= 0m) continue;
                d[candles[i].Date.Date] = (double)((candles[i].Close - prev) / prev);
            }
            return d;
        }

        // ── 순수 수학 헬퍼 ──
        private static double Mean(double[] xs)
        {
            double s = 0; foreach (var x in xs) s += x; return s / xs.Length;
        }

        private static double StdDev(double[] xs)
        {
            double m = Mean(xs), s = 0;
            foreach (var x in xs) s += (x - m) * (x - m);
            return Math.Sqrt(s / xs.Length);
        }

        private static AlphaReport Invalid(string sym, string bm, int days, string note) =>
            new(false, sym, bm, days, 0, 0, 0, 0, 0, 0, 0, Array.Empty<AlphaPoint>(), note);
    }
}
