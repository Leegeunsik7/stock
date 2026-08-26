// ============================================================================
// MonteCarlo - 포트폴리오 몬테카를로 시뮬레이션 (외부 의존 0, 순수 수학) (v3.14)
//  · 과거 일수익률(Candles)에서 평균·공분산을 추정하고, 공분산의 Cholesky 분해로
//    '상관관계를 보존한' 다변량 정규 충격을 생성 → 포트폴리오의 미래 경로를 수천 번 시뮬.
//  · 결과: 기대수익·P5/P50/P95·VaR(95%)·손실확률 + 분포 히스토그램.
//  · BacktestEngine(단일종목 과거)과 CorrelationAnalyzer(상관)의 포트폴리오 차원 정점.
//  ⚠️ 과거 통계 기반 가정(정규분포·정상성) — 미래 보장 아님, 정보 제공용.
// ============================================================================
using System;
using System.Linq;
using MS2E.UI.Portable.Ports.Quant;   // Phase 94-C — 읽기 전용 결과 계약 구현

namespace MS2E.Infrastructure
{
    // Phase 94-C: 필드 → 자동 프로퍼티 전환(IMcResult 자동 충족). 엔진의 r.X = ... 대입은 프로퍼티 set 으로 그대로 동작.
    public sealed class McResult : IMcResult
    {
        public int Sims { get; set; }
        public int Days { get; set; }
        public int Assets { get; set; }
        public double ExpReturnPct { get; set; }   // 평균 기간수익률 %
        public double P5 { get; set; }
        public double P50 { get; set; }
        public double P95 { get; set; }             // 백분위 기간수익률 %
        public double Var95Pct { get; set; }        // 95% VaR (양수 손실 %)
        public double ProbLossPct { get; set; }     // 손실(<0) 확률 %
        public int[] Histogram { get; set; } = Array.Empty<int>();
        public double HistMin { get; set; }
        public double HistMax { get; set; }
    }

    public static class MonteCarlo
    {
        /// <summary>
        /// dailyReturns[i] = 자산 i의 과거 일수익률(소수, 예: 0.012=+1.2%), weights[i]=비중(합 1).
        /// horizonDays 후의 포트폴리오 수익률 분포를 sims회 시뮬레이션. seed로 재현 가능.
        /// </summary>
        public static McResult Simulate(double[][] dailyReturns, double[] weights, int horizonDays, int sims, int seed = 12345)
        {
            int n = dailyReturns.Length;
            if (n == 0 || weights.Length != n) return new McResult { Sims = 0, Days = horizonDays, Assets = n };
            horizonDays = Math.Max(1, horizonDays);
            sims = Math.Clamp(sims, 100, 200_000);

            int len = dailyReturns.Min(r => r.Length);
            if (len < 2) return new McResult { Sims = 0, Days = horizonDays, Assets = n };

            // 평균 벡터
            var mean = new double[n];
            for (int i = 0; i < n; i++) mean[i] = dailyReturns[i].Take(len).Average();

            // 공분산 행렬 (표본, n-1)
            var cov = new double[n][];
            for (int i = 0; i < n; i++) cov[i] = new double[n];
            for (int i = 0; i < n; i++)
                for (int j = i; j < n; j++)
                {
                    double s = 0;
                    for (int t = 0; t < len; t++) s += (dailyReturns[i][t] - mean[i]) * (dailyReturns[j][t] - mean[j]);
                    double c = s / (len - 1);
                    cov[i][j] = c; cov[j][i] = c;
                }

            var L = Cholesky(cov);
            var rnd = new Random(seed);
            var finals = new double[sims];

            for (int s = 0; s < sims; s++)
            {
                double port = 1.0;
                for (int d = 0; d < horizonDays; d++)
                {
                    var z = new double[n];
                    for (int i = 0; i < n; i++) z[i] = NextNormal(rnd);
                    double dayPort = 0;
                    for (int i = 0; i < n; i++)
                    {
                        double shock = 0;
                        for (int k = 0; k <= i; k++) shock += L[i][k] * z[k];   // 상관 보존
                        dayPort += weights[i] * (mean[i] + shock);
                    }
                    port *= (1.0 + dayPort);
                }
                finals[s] = (port - 1.0) * 100.0;   // 기간수익률 %
            }

            Array.Sort(finals);
            var res = new McResult
            {
                Sims = sims, Days = horizonDays, Assets = n,
                ExpReturnPct = finals.Average(),
                P5 = Percentile(finals, 5), P50 = Percentile(finals, 50), P95 = Percentile(finals, 95),
                ProbLossPct = (double)finals.Count(x => x < 0) / sims * 100.0
            };
            res.Var95Pct = Math.Max(0, -res.P5);   // 95% 신뢰수준 최대손실
            BuildHistogram(finals, res);
            return res;
        }

        private static double[][] Cholesky(double[][] a)
        {
            int n = a.Length;
            var L = new double[n][];
            for (int i = 0; i < n; i++) L[i] = new double[n];
            for (int i = 0; i < n; i++)
                for (int j = 0; j <= i; j++)
                {
                    double sum = a[i][j];
                    for (int k = 0; k < j; k++) sum -= L[i][k] * L[j][k];
                    if (i == j)
                        L[i][j] = Math.Sqrt(sum <= 0 ? 1e-12 : sum);   // 비양정치 보정(지터)
                    else
                        L[i][j] = L[j][j] > 0 ? sum / L[j][j] : 0;
                }
            return L;
        }

        // Box–Muller 표준정규
        private static double NextNormal(Random r)
        {
            double u1 = 1.0 - r.NextDouble();
            double u2 = 1.0 - r.NextDouble();
            return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
        }

        private static double Percentile(double[] sorted, double p)
        {
            if (sorted.Length == 0) return 0;
            int idx = (int)Math.Round(p / 100.0 * (sorted.Length - 1));
            return sorted[Math.Clamp(idx, 0, sorted.Length - 1)];
        }

        private static void BuildHistogram(double[] sorted, McResult res, int buckets = 40)
        {
            res.HistMin = sorted[0];
            res.HistMax = sorted[^1];
            var hist = new int[buckets];
            double range = res.HistMax - res.HistMin;
            if (range <= 0) { hist[0] = sorted.Length; res.Histogram = hist; return; }
            foreach (var v in sorted)
            {
                int b = (int)((v - res.HistMin) / range * (buckets - 1));
                hist[Math.Clamp(b, 0, buckets - 1)]++;
            }
            res.Histogram = hist;
        }
    }
}
