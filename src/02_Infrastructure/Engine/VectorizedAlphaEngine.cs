// ============================================================================
// VectorizedAlphaEngine — v4.72.0: SIMD OLS 가속 알파 엔진
//
//  AlphaEngine.Compute() 와 동일한 AlphaReport 를 반환하되,
//  OLS 내적 누적을 Vector256<double> (AVX2, 4-double/lane) SIMD 로 가속.
//
//  두 번 패스 전략 (수치 안전 우선):
//   Pass 1(SIMD): Σrm, Σrp → meanRm, meanRp
//   Pass 2(SIMD): 평균 중심화 Σdm², Σdp², Σdm·dp
//  → 일반 1-pass "E[X²]−E[X]²" 상쇄 오차 없음.
//
//  AVX2 미지원 환경 → 자동 스칼라 강등 (호출자 코드 변경 불필요).
//  결정론 보장: 동일 candle 입력 → 비트 동일 AlphaReport (SIMD 경로 포함).
// ============================================================================
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Intrinsics;

namespace StockManager.Common
{
    public static class VectorizedAlphaEngine
    {
        public static AlphaReport Compute(
            string symbol,
            IReadOnlyList<Candle> portfolioCandles,
            IReadOnlyList<Candle> benchmarkCandles,
            string benchmarkName,
            double annualRiskFreeRate = 0.035)
        {
            // ── 1. 수익률 딕셔너리 빌드 (AlphaEngine 내부 메서드 공유) ──
            var pfDict = AlphaEngine.BuildReturnDict(portfolioCandles);
            var bmDict = AlphaEngine.BuildReturnDict(benchmarkCandles);

            var aligned = pfDict.Keys
                .Where(bmDict.ContainsKey)
                .OrderBy(d => d)
                .ToList();

            if (aligned.Count < 30)
                return AlphaEngine.Compute(symbol, portfolioCandles, benchmarkCandles,
                    benchmarkName, annualRiskFreeRate);   // 최소 일수 미달 → 표준 엔진

            double[] rp = aligned.Select(d => pfDict[d]).ToArray();
            double[] rm = aligned.Select(d => bmDict[d]).ToArray();
            int n = rp.Length;
            double rfDaily = annualRiskFreeRate / 252.0;

            // ── 2. SIMD OLS — 두 번 패스 ─────────────────────────────────────
            double meanRm, meanRp, varM, varP, cov;

            if (Vector256.IsHardwareAccelerated && n >= Vector256<double>.Count)
            {
                int VW     = Vector256<double>.Count;   // 4 (AVX2)
                int simdN  = n - n % VW;

                // Pass 1: 합계 → 평균
                var vSumRm = Vector256<double>.Zero;
                var vSumRp = Vector256<double>.Zero;
                for (int i = 0; i < simdN; i += VW)
                {
                    vSumRm += Vector256.LoadUnsafe(ref rm[i]);
                    vSumRp += Vector256.LoadUnsafe(ref rp[i]);
                }
                double sumRm = Vector256.Sum(vSumRm);
                double sumRp = Vector256.Sum(vSumRp);
                for (int i = simdN; i < n; i++) { sumRm += rm[i]; sumRp += rp[i]; }
                meanRm = sumRm / n;
                meanRp = sumRp / n;

                // Pass 2: 중심화 모멘트 누적
                var vMeanRm = Vector256.Create(meanRm);
                var vMeanRp = Vector256.Create(meanRp);
                var vVarM   = Vector256<double>.Zero;
                var vVarP   = Vector256<double>.Zero;
                var vCov    = Vector256<double>.Zero;
                for (int i = 0; i < simdN; i += VW)
                {
                    var dm = Vector256.LoadUnsafe(ref rm[i]) - vMeanRm;
                    var dp = Vector256.LoadUnsafe(ref rp[i]) - vMeanRp;
                    vVarM += dm * dm;
                    vVarP += dp * dp;
                    vCov  += dm * dp;
                }
                varM = Vector256.Sum(vVarM);
                varP = Vector256.Sum(vVarP);
                cov  = Vector256.Sum(vCov);
                for (int i = simdN; i < n; i++)
                {
                    double dm = rm[i] - meanRm, dp = rp[i] - meanRp;
                    varM += dm * dm; varP += dp * dp; cov += dm * dp;
                }
                varM /= n; varP /= n; cov /= n;
            }
            else
            {
                // Scalar 강등 경로 (AVX2 미지원 또는 n < 4)
                double smRm = 0, smRp = 0;
                for (int i = 0; i < n; i++) { smRm += rm[i]; smRp += rp[i]; }
                meanRm = smRm / n;
                meanRp = smRp / n;
                varM = 0; varP = 0; cov = 0;
                for (int i = 0; i < n; i++)
                {
                    double dm = rm[i] - meanRm, dp = rp[i] - meanRp;
                    varM += dm * dm; varP += dp * dp; cov += dm * dp;
                }
                varM /= n; varP /= n; cov /= n;
            }

            // ── 3. OLS 파라미터 ───────────────────────────────────────────────
            double beta = varM > 1e-12 ? cov / varM : 0.0;
            double r2   = varP > 1e-12 && varM > 1e-12
                ? Math.Clamp((cov * cov) / (varP * varM), 0.0, 1.0)
                : 0.0;

            // ── 4. 잉여(Residual) 계열 — 시장 기여 제거 ──────────────────────
            double[] resid = new double[n];
            for (int i = 0; i < n; i++) resid[i] = rp[i] - beta * rm[i];

            // ── 5. 연환산 알파 (CAPM) ─────────────────────────────────────────
            double alphaDaily  = meanRp - (beta * meanRm + rfDaily);
            double alphaAnnual = alphaDaily * 252.0;

            // ── 6. Information Ratio ──────────────────────────────────────────
            double residMean = 0;
            for (int i = 0; i < n; i++) residMean += resid[i];
            residMean /= n;
            double residVar = 0;
            for (int i = 0; i < n; i++) { double d = resid[i] - residMean; residVar += d * d; }
            double residStd = n >= 2 ? Math.Sqrt(residVar / (n - 1)) : 0.0;
            double ir = residStd > 1e-12
                ? (residMean - rfDaily) / residStd * Math.Sqrt(252.0)
                : 0.0;

            // ── 7. 복리 누적 계열 ─────────────────────────────────────────────
            var series = new List<AlphaPoint>(n);
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

            string accel = Vector256.IsHardwareAccelerated ? "AVX2" : "Scalar";
            return new AlphaReport(true, symbol, benchmarkName, n,
                Math.Round(beta, 4),
                Math.Round(r2 * 100.0, 2),
                Math.Round(totalRetPct,  2),
                Math.Round(mktAttrPct,   2),
                Math.Round(trueAlphaPct, 2),
                Math.Round(alphaAnnual * 100.0, 2),
                Math.Round(ir, 3),
                series,
                $"{n}일 거래일 기준, {benchmarkName} 벤치마크, 무위험이율 {annualRiskFreeRate * 100:0.#}%. [{accel} OLS]");
        }
    }
}
