// ============================================================================
// FutureCone - 확률 미래 엔진 (Season 7 #1) · 순수 결정론 코어 (외부 의존 0)
//  과거 일수익률에서 블록 부트스트랩으로 N개 미래 경로를 시뮬 → 확률 콘(백분위 밴드),
//  목표 달성 확률, 손실 확률, 예상 최대낙폭 분포를 산출한다.
//   · 블록 부트스트랩: 과거의 연속 구간을 재표집 → 자기상관·팻테일·레짐(스트레스 구간)을 암묵적으로 보존.
//   · 시드 고정 → 결정론·재현 가능(단위 테스트). 룩어헤드는 호출측이 as-of 이하 수익률만 넣어 보장.
//  ⚠️ 정보 제공·확률 추정이며 투자 권유/예언이 아님.
// ============================================================================
using System;
using System.Collections.Generic;
using System.Linq;

namespace StockManager.Common
{
    public sealed record ConeBand(int Day, double P5, double P25, double P50, double P75, double P95);

    public sealed record FutureConeResult(
        bool IsValid, int Horizon, int Sims,
        IReadOnlyList<ConeBand> Bands,
        double ExpReturnPct, double MedianReturnPct, double P5ReturnPct, double P95ReturnPct,
        double ProbLossPct, double GoalPct, double GoalHitPct, double MedianMaxDrawdownPct,
        string Note);

    public static class FutureCone
    {
        public static FutureConeResult Project(
            IReadOnlyList<double> dailyReturns, int horizon, int sims, int seed,
            double goalPct = 0, int blockLen = 5)
        {
            if (dailyReturns == null || dailyReturns.Count < 20 || horizon < 1)
                return new FutureConeResult(false, Math.Max(0, horizon), 0, Array.Empty<ConeBand>(),
                    0, 0, 0, 0, 0, goalPct, 0, 0, "수익률 표본 부족(최소 20거래일 필요)");

            horizon = Math.Min(horizon, 504);          // 상한(메모리/연산 가드)
            sims = Math.Clamp(sims, 100, 20000);
            int n = dailyReturns.Count;
            blockLen = Math.Clamp(blockLen, 1, n);
            var rnd = new Random(seed);                // 시드 고정 → 결정론

            var dayValues = new double[horizon][];     // dayValues[d][s] = 경로 s의 d일차 누적가치(시작 1.0)
            for (int d = 0; d < horizon; d++) dayValues[d] = new double[sims];
            var terminal = new double[sims];
            var maxDD = new double[sims];

            for (int s = 0; s < sims; s++)
            {
                double val = 1.0, peak = 1.0, dd = 0;
                int step = 0;
                while (step < horizon)
                {
                    int start = rnd.Next(n);
                    for (int b = 0; b < blockLen && step < horizon; b++, step++)
                    {
                        val *= 1.0 + dailyReturns[(start + b) % n];
                        if (val > peak) peak = val;
                        double curDD = peak > 0 ? (peak - val) / peak : 0;
                        if (curDD > dd) dd = curDD;
                        dayValues[step][s] = val;
                    }
                }
                terminal[s] = val;
                maxDD[s] = dd;
            }

            var bands = new List<ConeBand>(horizon);
            for (int d = 0; d < horizon; d++)
            {
                var arr = dayValues[d];
                Array.Sort(arr);
                bands.Add(new ConeBand(d + 1,
                    PctRet(arr, 0.05), PctRet(arr, 0.25), PctRet(arr, 0.50), PctRet(arr, 0.75), PctRet(arr, 0.95)));
            }

            Array.Sort(terminal);
            Array.Sort(maxDD);
            double expPct = (terminal.Average() - 1.0) * 100.0;
            double probLoss = terminal.Count(v => v < 1.0) / (double)sims * 100.0;
            double goalHit = Math.Abs(goalPct) > 1e-9
                ? terminal.Count(v => (v - 1.0) * 100.0 >= goalPct) / (double)sims * 100.0
                : 0;
            double medDD = Percentile(maxDD, 0.50) * 100.0;

            return new FutureConeResult(true, horizon, sims, bands,
                expPct, PctRet(terminal, 0.50), PctRet(terminal, 0.05), PctRet(terminal, 0.95),
                probLoss, goalPct, goalHit, medDD,
                $"블록 부트스트랩(블록 {blockLen}일 · {sims:N0} 경로) · 과거 {n}일 표본");
        }

        private static double PctRet(double[] sortedValues, double q) => (Percentile(sortedValues, q) - 1.0) * 100.0;

        private static double Percentile(double[] sorted, double q)
        {
            if (sorted.Length == 0) return 0;
            double pos = q * (sorted.Length - 1);
            int lo = (int)Math.Floor(pos), hi = (int)Math.Ceiling(pos);
            if (lo == hi) return sorted[lo];
            return sorted[lo] + (sorted[hi] - sorted[lo]) * (pos - lo);
        }
    }
}
