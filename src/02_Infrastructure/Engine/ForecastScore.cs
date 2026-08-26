// ============================================================================
// ForecastScore - 예측 스코어보드 🄵 (외부 의존 0) (v3.26.0)
//  엔진이 쏟아내는 '정량 예측'(구간·점추정)을 실현치와 대조해 보정도(calibration)를 채점한다.
//  🄱 알림 캘리브레이터가 이산 알림을 채점했다면, 이건 연속 확률예측을 채점하는 상위 버전 —
//  엔진 전체의 '정직성'을 감사한다.
//   · 구간 커버리지: 실현이 [Low,High]에 든 비율 vs 명목(예 90%) — 미달이면 과신(구간 좁음)
//   · 편향(ME): mean(실현 − 예측) — 양수면 낙관 편향
//   · MAE: mean|실현 − 예측| · 방향 적중 · 하단 위반율(VaR 성격)
//  CalibrationBacktest: 과거 각 시점에 '그때까지'의 추세통계로 구간예측 발행 → 미래 실현으로 즉시 채점
//  (며칠 기다리지 않고 보정도를 바로 본다). 순수·결정적.
// ============================================================================
using System;
using System.Collections.Generic;
using System.Linq;

namespace MS2E.Infrastructure
{
    public class ResolvedForecast
    {
        public string Kind { get; set; } = "StatBand";
        public int HorizonDays { get; set; }
        public double PredictedPct { get; set; }   // 점추정(평균/P50)
        public double LowPct { get; set; }          // 구간 하단(예 P5)
        public double HighPct { get; set; }         // 구간 상단(예 P95)
        public double NominalCoverage { get; set; } // 구간 명목 커버리지(0~1, 예 0.90)
        public double RealizedPct { get; set; }
        public bool InBand => RealizedPct >= LowPct && RealizedPct <= HighPct;
    }

    public class KindScore
    {
        public string Kind { get; set; } = "";
        public int HorizonDays { get; set; }
        public int N { get; set; }
        public double CoveragePct { get; set; }
        public double NominalPct { get; set; }
        public double BiasPct { get; set; }
        public double MaePct { get; set; }
        public double DirHitPct { get; set; }
        public double LowBreachPct { get; set; }
        public string Verdict { get; set; } = "";
    }

    public class ForecastScoreReport
    {
        public bool IsValid { get; set; }
        public string Note { get; set; } = "";
        public int Resolved { get; set; }
        public List<KindScore> Scores { get; set; } = new();
    }

    public static class ForecastScore
    {
        public static ForecastScoreReport Score(IEnumerable<ResolvedForecast> forecasts)
        {
            var list = (forecasts ?? Enumerable.Empty<ResolvedForecast>()).ToList();
            var rep = new ForecastScoreReport { Resolved = list.Count };
            if (list.Count == 0) { rep.Note = "채점할(만기된) 예측이 없습니다."; return rep; }

            foreach (var g in list.GroupBy(f => (f.Kind, f.HorizonDays)).OrderByDescending(g => g.Count()))
            {
                var fs = g.ToList();
                int n = fs.Count;
                double cover = (double)fs.Count(f => f.InBand) / n * 100.0;
                double nominal = fs.Average(f => f.NominalCoverage) * 100.0;
                double bias = fs.Average(f => f.RealizedPct - f.PredictedPct);
                double mae = fs.Average(f => Math.Abs(f.RealizedPct - f.PredictedPct));
                double dir = (double)fs.Count(f => Math.Sign(f.PredictedPct) == Math.Sign(f.RealizedPct)) / n * 100.0;
                double breach = (double)fs.Count(f => f.RealizedPct < f.LowPct) / n * 100.0;
                rep.Scores.Add(new KindScore
                {
                    Kind = g.Key.Kind, HorizonDays = g.Key.HorizonDays, N = n,
                    CoveragePct = cover, NominalPct = nominal, BiasPct = bias,
                    MaePct = mae, DirHitPct = dir, LowBreachPct = breach,
                    Verdict = Verdict(cover, nominal, bias, n)
                });
            }
            rep.IsValid = true;
            rep.Note = $"{rep.Resolved}건 채점 · 유형 {rep.Scores.Count}종";
            return rep;
        }

        private static string Verdict(double cover, double nominal, double bias, int n)
        {
            if (n < 10) return "표본 부족";
            string c = cover < nominal - 7 ? "과신(구간 좁음)"
                     : cover > nominal + 7 ? "과소(구간 넓음)"
                     : "구간 양호";
            string b = bias > 0.7 ? " · 낙관 편향" : bias < -0.7 ? " · 비관 편향" : "";
            return c + b;
        }

        /// <summary>과거 각 시점에 trailing 통계로 구간예측을 발행하고 실현으로 채점(보정도 백테스트).</summary>
        public static List<ResolvedForecast> CalibrationBacktest(
            IReadOnlyList<decimal> closes, int horizon, int lookback, double zHalf, double nominal)
        {
            var outp = new List<ResolvedForecast>();
            if (closes == null || closes.Count < lookback + horizon + 1) return outp;

            var rets = new double[closes.Count];
            for (int i = 1; i < closes.Count; i++)
            {
                var p = (double)closes[i - 1];
                rets[i] = p != 0 ? ((double)closes[i] - p) / p * 100.0 : 0;
            }

            for (int i = lookback; i + horizon < closes.Count; i++)
            {
                // i 시점까지의 직전 lookback개 일수익률 통계
                double mean = 0; for (int k = i - lookback + 1; k <= i; k++) mean += rets[k];
                mean /= lookback;
                double ss = 0; for (int k = i - lookback + 1; k <= i; k++) ss += (rets[k] - mean) * (rets[k] - mean);
                double std = Math.Sqrt(ss / Math.Max(1, lookback - 1));

                double hMean = mean * horizon;
                double hStd = std * Math.Sqrt(horizon);
                double pred = hMean;
                double realized = (double)closes[i] > 0 ? ((double)closes[i + horizon] - (double)closes[i]) / (double)closes[i] * 100.0 : 0;

                outp.Add(new ResolvedForecast
                {
                    Kind = "StatBand", HorizonDays = horizon,
                    PredictedPct = pred, LowPct = pred - zHalf * hStd, HighPct = pred + zHalf * hStd,
                    NominalCoverage = nominal, RealizedPct = realized
                });
            }
            return outp;
        }
    }
}
