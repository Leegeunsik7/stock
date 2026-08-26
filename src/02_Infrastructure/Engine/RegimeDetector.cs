// ============================================================================
// RegimeDetector - 레짐 변화 이상탐지 🅘 (외부 의존 0) (v3.19.0)
//  고정 임계값(가격 N 도달) 알림을 넘어, 종목의 "평소 성격"에서 벗어나는 순간을
//  온라인 통계로 감지한다:
//   · 수익률 z-score      — 최근 창 대비 오늘 수익률이 몇 σ인가 (이상치)
//   · EWMA 변동성 비율     — |오늘 수익률| / 최근 EWMA 변동성 (변동성 급등)
//   · CUSUM               — 표준화 수익률 누적합으로 평균 레짐 이동(상/하) 감지
//   · 평균 페어 상관       — 포트폴리오 분산효과 붕괴(상관 급등) 감시
//  순수 계산("Primes 패턴"). AlertEngineService가 이 신호로 경보를 띄운다.
//
//  설계 노트 / 숨은 룰:
//   · 수익률은 단순수익률 %, z는 표본표준편차(n-1) 기준, std≈0이면 0으로 가드
//   · CUSUM은 전체 평균/표준편차로 표준화 후 k=0.5, h=5(기본) 양방향 누적 — 최종 상태로 판정
//   · 우선순위: 이상치(z) → 변동성급등 → 평균이동 → 정상. 데이터 부족 시 None
//   · 결정적: 동일 입력 → 동일 신호 (난수 없음)
// ============================================================================
using System;
using System.Collections.Generic;
using System.Linq;

namespace StockManager.Common
{
    public enum RegimeKind { None, ReturnOutlier, VolatilitySpike, MeanShiftUp, MeanShiftDown }

    public class RegimeSignal
    {
        public bool IsAnomaly { get; set; }
        public RegimeKind Kind { get; set; } = RegimeKind.None;
        public double ZScore { get; set; }
        public double VolRatio { get; set; }
        public double LatestReturnPct { get; set; }

        public string Describe() => Kind switch
        {
            RegimeKind.ReturnOutlier => $"수익률 이상치 {LatestReturnPct:+0.0;-0.0}% (평소의 {ZScore:0.0}σ)",
            RegimeKind.VolatilitySpike => $"변동성 급등 — 오늘 변동이 최근 평균의 {VolRatio:0.0}배",
            RegimeKind.MeanShiftUp => "추세 레짐 상향 전환 신호(평균 상승 이동)",
            RegimeKind.MeanShiftDown => "추세 레짐 하향 전환 신호(평균 하락 이동)",
            _ => "정상 범위"
        };
    }

    public static class RegimeDetector
    {
        /// <summary>종가 시계열을 받아 레짐 이상 신호를 산출.</summary>
        public static RegimeSignal Analyze(IReadOnlyList<decimal> closes, int window = 20,
            double zThreshold = 3.0, double volRatioThreshold = 2.5, double ewmaLambda = 0.94)
        {
            var sig = new RegimeSignal();
            var rets = ToReturns(closes);
            if (rets.Length < window + 1) return sig;   // 데이터 부족 → 정상(None)

            sig.LatestReturnPct = rets[^1];
            sig.ZScore = LatestZScore(rets, window);
            sig.VolRatio = EwmaVolRatio(rets, ewmaLambda);
            var (shiftUp, shiftDown, _, _) = Cusum(rets, 0.5, 5.0);

            if (Math.Abs(sig.ZScore) >= zThreshold) sig.Kind = RegimeKind.ReturnOutlier;
            else if (sig.VolRatio >= volRatioThreshold) sig.Kind = RegimeKind.VolatilitySpike;
            else if (shiftUp) sig.Kind = RegimeKind.MeanShiftUp;
            else if (shiftDown) sig.Kind = RegimeKind.MeanShiftDown;
            else sig.Kind = RegimeKind.None;

            sig.IsAnomaly = sig.Kind != RegimeKind.None;
            return sig;
        }

        /// <summary>종가 → 단순 일수익률(%) 배열.</summary>
        public static double[] ToReturns(IReadOnlyList<decimal> closes)
        {
            if (closes == null || closes.Count < 2) return Array.Empty<double>();
            var r = new double[closes.Count - 1];
            for (int i = 1; i < closes.Count; i++)
            {
                var prev = (double)closes[i - 1];
                r[i - 1] = prev != 0 ? ((double)closes[i] - prev) / prev * 100.0 : 0.0;
            }
            return r;
        }

        /// <summary>최신 수익률이 직전 window개 분포 대비 몇 σ인지.</summary>
        public static double LatestZScore(double[] rets, int window)
        {
            int n = rets.Length;
            if (n < window + 1) return 0;
            var win = new double[window];
            Array.Copy(rets, n - 1 - window, win, 0, window);   // 최신값 직전 window개
            var (mean, std) = MeanStd(win);
            return std > 1e-9 ? (rets[n - 1] - mean) / std : 0;
        }

        /// <summary>|최신 수익률| / 최신값 직전까지의 EWMA 변동성.</summary>
        public static double EwmaVolRatio(double[] rets, double lambda)
        {
            int n = rets.Length;
            if (n < 3) return 0;
            double var = rets[0] * rets[0];
            for (int i = 1; i < n - 1; i++) var = lambda * var + (1 - lambda) * rets[i] * rets[i];
            double vol = Math.Sqrt(var);
            return vol > 1e-9 ? Math.Abs(rets[n - 1]) / vol : 0;
        }

        /// <summary>표준화 수익률의 양방향 CUSUM. 최종 누적이 h를 넘으면 레짐 이동.</summary>
        public static (bool shiftUp, bool shiftDown, double sHi, double sLo) Cusum(double[] rets, double k, double h)
        {
            if (rets.Length < 4) return (false, false, 0, 0);
            var (mean, std) = MeanStd(rets);
            if (std <= 1e-9) return (false, false, 0, 0);
            double sHi = 0, sLo = 0;
            foreach (var r in rets)
            {
                double z = (r - mean) / std;
                sHi = Math.Max(0, sHi + z - k);
                sLo = Math.Max(0, sLo - z - k);
            }
            return (sHi > h, sLo > h, sHi, sLo);
        }

        /// <summary>여러 자산 일수익률의 평균 페어 상관(분산효과 붕괴 감시). 자산&lt;2면 0.</summary>
        public static double AvgPairwiseCorrelation(IReadOnlyList<double[]> assetReturns)
        {
            if (assetReturns == null || assetReturns.Count < 2) return 0;
            int len = assetReturns.Min(a => a?.Length ?? 0);
            if (len < 2) return 0;
            // 뒤에서 len개로 정렬(최근 구간)
            var aligned = assetReturns.Select(a => a.Skip(a.Length - len).ToArray()).ToList();

            double sum = 0; int pairs = 0;
            for (int i = 0; i < aligned.Count; i++)
                for (int j = i + 1; j < aligned.Count; j++)
                {
                    sum += Pearson(aligned[i], aligned[j]);
                    pairs++;
                }
            return pairs > 0 ? sum / pairs : 0;
        }

        public static (double mean, double std) MeanStd(IReadOnlyList<double> v)
        {
            if (v == null || v.Count == 0) return (0, 0);
            double mean = v.Average();
            if (v.Count < 2) return (mean, 0);
            double ss = 0;
            foreach (var x in v) ss += (x - mean) * (x - mean);
            return (mean, Math.Sqrt(ss / (v.Count - 1)));   // 표본표준편차
        }

        private static double Pearson(double[] a, double[] b)
        {
            int n = Math.Min(a.Length, b.Length);
            if (n < 2) return 0;
            double ma = a.Take(n).Average(), mb = b.Take(n).Average();
            double cov = 0, va = 0, vb = 0;
            for (int i = 0; i < n; i++)
            {
                double da = a[i] - ma, db = b[i] - mb;
                cov += da * db; va += da * da; vb += db * db;
            }
            double denom = Math.Sqrt(va * vb);
            return denom > 1e-12 ? cov / denom : 0;
        }
    }
}
