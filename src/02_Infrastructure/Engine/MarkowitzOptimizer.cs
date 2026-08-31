// ============================================================================
// MarkowitzOptimizer - MPT 리밸런싱 옵티마이저 🅚 (외부 의존 0) (v3.20.0)
//  현대포트폴리오이론(평균-분산)으로 "그래서 비중을 어떻게?"를 처방한다.
//  일수익률 행렬 → 평균(μ)·공분산(Σ) → GMV(최소분산)와 tangency(최대샤프) 두 펀드를
//  블렌딩해 long-only 효율 프론티어를 추적하고, 목표 변동성에 맞는 비중을 고른다.
//  순수 결정적 계산("Primes 패턴"). 🅓 몬테카를로의 공분산 기계장치를 재사용.
//
//  설계 노트 / 숨은 룰:
//   · 단위: 일수익률 %. 연환산 — 수익 ×252, 변동성 ×√252
//   · 공분산은 표본(T-1). 수치 안정 위해 Σ+ridge·I 후 가우스-조던 역행렬
//   · long-only: GMV·tangency 모두 음수 클립 후 정규화, 블렌딩(볼록결합)으로 음수 불가
//   · 종목 상한 maxWeight 캡(워터필링 반복). n·cap<1이면 cap을 1/n으로 완화
//   · 목표변동성: 프론티어에서 vol≤목표 중 최대수익 선택, 없으면 최소분산점
//   · 목표 미지정: 최대 샤프(rf=0). 결정적 — 동일 입력 동일 비중(난수 없음)
//   · MPT는 추정치(특히 μ)에 민감 — 처방이지 보장이 아님(UI에 정직 표기)
// ============================================================================
using System;
using System.Collections.Generic;
using System.Linq;
using MS2E.UI.Portable.Ports;   // Phase 94-I — IMpoResultView/IFrontierPointView 읽기 전용 계약 구현

namespace MS2E.Infrastructure
{
    public class FrontierPoint : IFrontierPointView   // VolPct/RetPct 암시 구현
    {
        public double VolPct { get; set; }
        public double RetPct { get; set; }
        public double Sharpe { get; set; }
        public double[] Weights { get; set; } = Array.Empty<double>();
    }

    public class MpoResult : IMpoResultView   // Phase 94-I: RebalancePage 가 도메인 무참조로 소비
    {
        public bool IsValid { get; set; }
        public string Note { get; set; } = "";
        public string[] Names { get; set; } = Array.Empty<string>();
        public double[] Weights { get; set; } = Array.Empty<double>();   // 선택된 타겟 비중(합=1)
        public double[] GmvWeights { get; set; } = Array.Empty<double>();
        public double ExpReturnPct { get; set; }   // 연환산
        public double VolPct { get; set; }          // 연환산
        public double Sharpe { get; set; }
        public List<FrontierPoint> Frontier { get; set; } = new();

        // 명시적 공변 구현 — List<FrontierPoint> → IReadOnlyList<IFrontierPointView>(사본 0, IReadOnlyList<out T>).
        IReadOnlyList<IFrontierPointView> IMpoResultView.Frontier => Frontier;
    }

    public static class MarkowitzOptimizer
    {
        private const double TradingDays = 252.0;

        public static MpoResult Optimize(double[][] assetReturnsPctDaily, string[] names,
            double? targetVolAnnualPct = null, double maxWeight = 0.4, double ridge = 1e-4)
        {
            var res = new MpoResult { Names = names ?? Array.Empty<string>() };
            if (assetReturnsPctDaily == null || assetReturnsPctDaily.Length == 0)
            { res.Note = "자산이 없습니다."; return res; }
            int n = assetReturnsPctDaily.Length;

            int t = assetReturnsPctDaily.Min(a => a?.Length ?? 0);
            if (t < 2) { res.Note = "수익률 히스토리가 부족합니다(2일+)."; return res; }

            // 뒤에서 t개로 정렬(최근 공통 구간)
            var R = assetReturnsPctDaily.Select(a => a.Skip(a.Length - t).ToArray()).ToArray();

            if (n == 1)
            {
                var (m1, s1) = MeanStd(R[0]);
                res.IsValid = true; res.Weights = new[] { 1.0 }; res.GmvWeights = new[] { 1.0 };
                res.ExpReturnPct = m1 * TradingDays; res.VolPct = s1 * Math.Sqrt(TradingDays);
                res.Sharpe = res.VolPct > 1e-9 ? res.ExpReturnPct / res.VolPct : 0;
                res.Note = "단일 자산 — 분산 최적화 불가";
                return res;
            }

            var mu = new double[n];
            for (int i = 0; i < n; i++) mu[i] = R[i].Average();
            var cov = Covariance(R);
            var inv = Invert(AddRidge(cov, ridge));
            if (inv == null) { res.Note = "공분산 역행렬 계산 실패(특이행렬)."; return res; }

            double cap = Math.Max(maxWeight, 1.0 / n);

            // 두 펀드: GMV(Σ⁻¹·1)와 tangency(Σ⁻¹·μ)
            var wGmv = CapWeights(ClipRenorm(MatVec(inv, Ones(n))), cap);
            var wTanRaw = ClipRenorm(MatVec(inv, mu));
            var wTan = wTanRaw.Sum() > 0 ? CapWeights(wTanRaw, cap) : OneHotMaxMean(mu);  // 퇴화 시 최고수익 단일자산
            res.GmvWeights = wGmv;

            // 프론티어: GMV ↔ tangency 블렌딩
            var seen = new HashSet<string>();
            for (int k = 0; k <= 50; k++)
            {
                double a = k / 50.0;
                var w = CapWeights(Blend(wGmv, wTan, a), cap);
                var (retD, varD) = PortfolioStats(w, mu, cov);
                double vol = Math.Sqrt(Math.Max(0, varD)) * Math.Sqrt(TradingDays);
                double ret = retD * TradingDays;
                var key = string.Join(",", w.Select(x => Math.Round(x, 3)));
                if (!seen.Add(key)) continue;
                res.Frontier.Add(new FrontierPoint
                {
                    Weights = w, VolPct = vol, RetPct = ret,
                    Sharpe = vol > 1e-9 ? ret / vol : 0
                });
            }
            res.Frontier = res.Frontier.OrderBy(p => p.VolPct).ToList();

            FrontierPoint chosen;
            if (targetVolAnnualPct is { } tv && tv > 0)
            {
                var feasible = res.Frontier.Where(p => p.VolPct <= tv).ToList();
                chosen = feasible.Count > 0
                    ? feasible.OrderByDescending(p => p.RetPct).First()        // 목표 이하 중 최대수익
                    : res.Frontier.First();                                    // 목표가 너무 낮음 → 최소분산
            }
            else chosen = res.Frontier.OrderByDescending(p => p.Sharpe).First();   // 최대 샤프

            res.Weights = chosen.Weights;
            res.ExpReturnPct = chosen.RetPct;
            res.VolPct = chosen.VolPct;
            res.Sharpe = chosen.Sharpe;
            res.IsValid = true;
            res.Note = $"{n}종목 · {t}일 · " + (targetVolAnnualPct is { } tt && tt > 0
                ? $"목표변동성 {tt:0.0}% → 실제 {res.VolPct:0.0}%"
                : $"최대 샤프 {res.Sharpe:0.00}");
            return res;
        }

        // ── 선형대수/통계 헬퍼 (모두 순수) ──
        public static double[][] Covariance(double[][] R)
        {
            int n = R.Length, t = R[0].Length;
            var mean = R.Select(a => a.Average()).ToArray();
            var cov = new double[n][];
            for (int i = 0; i < n; i++) cov[i] = new double[n];
            for (int i = 0; i < n; i++)
                for (int j = i; j < n; j++)
                {
                    double s = 0;
                    for (int k = 0; k < t; k++) s += (R[i][k] - mean[i]) * (R[j][k] - mean[j]);
                    double c = t > 1 ? s / (t - 1) : 0;
                    cov[i][j] = c; cov[j][i] = c;
                }
            return cov;
        }

        private static double[][] AddRidge(double[][] m, double r)
        {
            int n = m.Length;
            var o = new double[n][];
            for (int i = 0; i < n; i++) { o[i] = (double[])m[i].Clone(); o[i][i] += r; }
            return o;
        }

        /// <summary>가우스-조던 역행렬(부분 피벗). 특이 시 null.</summary>
        public static double[][]? Invert(double[][] m)
        {
            int n = m.Length;
            var a = new double[n][];
            var inv = new double[n][];
            for (int i = 0; i < n; i++)
            {
                a[i] = (double[])m[i].Clone();
                inv[i] = new double[n];
                inv[i][i] = 1.0;
            }
            for (int col = 0; col < n; col++)
            {
                int piv = col;
                for (int r = col + 1; r < n; r++) if (Math.Abs(a[r][col]) > Math.Abs(a[piv][col])) piv = r;
                if (Math.Abs(a[piv][col]) < 1e-12) return null;
                (a[col], a[piv]) = (a[piv], a[col]);
                (inv[col], inv[piv]) = (inv[piv], inv[col]);
                double d = a[col][col];
                for (int j = 0; j < n; j++) { a[col][j] /= d; inv[col][j] /= d; }
                for (int r = 0; r < n; r++)
                {
                    if (r == col) continue;
                    double f = a[r][col];
                    if (f == 0) continue;
                    for (int j = 0; j < n; j++) { a[r][j] -= f * a[col][j]; inv[r][j] -= f * inv[col][j]; }
                }
            }
            return inv;
        }

        private static double[] MatVec(double[][] m, double[] v)
        {
            int n = m.Length;
            var o = new double[n];
            for (int i = 0; i < n; i++) { double s = 0; for (int j = 0; j < n; j++) s += m[i][j] * v[j]; o[i] = s; }
            return o;
        }

        private static double[] Ones(int n) { var o = new double[n]; for (int i = 0; i < n; i++) o[i] = 1.0; return o; }

        private static double[] ClipRenorm(double[] w)
        {
            var c = w.Select(x => Math.Max(0, x)).ToArray();
            double s = c.Sum();
            if (s <= 1e-12) return Enumerable.Repeat(1.0 / w.Length, w.Length).ToArray();
            return c.Select(x => x / s).ToArray();
        }

        // 워터필링: 상한 초과 자산을 cap에 고정하고 남은 예산을 자유 자산에 비례 재분배.
        // clip-후-정규화는 정규화가 다시 상한을 넘겨 수렴하지 않으므로 사용하지 않는다.
        private static double[] CapWeights(double[] w, double cap)
        {
            int n = w.Length;
            cap = Math.Max(cap, 1.0 / n);          // n·cap<1이면 실현 불가 → 완화
            var x = ClipRenorm(w);                  // ≥0, 합=1
            var fixedAt = new bool[n];
            for (int iter = 0; iter <= n; iter++)
            {
                var over = new List<int>();
                for (int i = 0; i < n; i++) if (!fixedAt[i] && x[i] > cap + 1e-12) over.Add(i);
                if (over.Count == 0) break;
                foreach (var i in over) { fixedAt[i] = true; x[i] = cap; }

                double fixedSum = 0; int freeCnt = 0; double freeSum = 0;
                for (int i = 0; i < n; i++)
                    if (fixedAt[i]) fixedSum += cap; else { freeCnt++; freeSum += x[i]; }
                double remaining = 1 - fixedSum;
                if (freeCnt == 0) break;
                if (remaining <= 0) { for (int i = 0; i < n; i++) if (!fixedAt[i]) x[i] = 0; }
                else if (freeSum <= 1e-12) { for (int i = 0; i < n; i++) if (!fixedAt[i]) x[i] = remaining / freeCnt; }
                else { double sc = remaining / freeSum; for (int i = 0; i < n; i++) if (!fixedAt[i]) x[i] *= sc; }
            }
            return x;
        }

        private static double[] Blend(double[] a, double[] b, double t)
        {
            var o = new double[a.Length];
            for (int i = 0; i < a.Length; i++) o[i] = (1 - t) * a[i] + t * b[i];
            return o;
        }

        private static double[] OneHotMaxMean(double[] mu)
        {
            int best = 0;
            for (int i = 1; i < mu.Length; i++) if (mu[i] > mu[best]) best = i;
            var o = new double[mu.Length]; o[best] = 1.0; return o;
        }

        private static (double retDaily, double varDaily) PortfolioStats(double[] w, double[] mu, double[][] cov)
        {
            int n = w.Length;
            double ret = 0; for (int i = 0; i < n; i++) ret += w[i] * mu[i];
            double var = 0;
            for (int i = 0; i < n; i++)
                for (int j = 0; j < n; j++) var += w[i] * w[j] * cov[i][j];
            return (ret, var);
        }

        private static (double mean, double std) MeanStd(double[] v)
        {
            if (v.Length == 0) return (0, 0);
            double m = v.Average();
            if (v.Length < 2) return (m, 0);
            double ss = 0; foreach (var x in v) ss += (x - m) * (x - m);
            return (m, Math.Sqrt(ss / (v.Length - 1)));
        }
    }
}
