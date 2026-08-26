// ============================================================================
// ScenarioTrackerEngine — S11 #2 시나리오 실현 추적기
//  · 순수 C# 결정론 모듈 (외부 의존성 0, BCL만 사용)
//  · 환각 방화벽: LLM 텍스트/시나리오 레이블은 keyword→enum 사후 매핑만 수행,
//                핵심 수치 연산을 오염시키지 않음
// ============================================================================
using System;
using System.Collections.Generic;
using System.Linq;

namespace MS2E.Infrastructure
{
    // ── 데이터 계약 ────────────────────────────────────────────────────────────

    public sealed record PortfolioHoldingCapture(
        string Symbol, double WeightPct, decimal CapturePrice,
        decimal? ActualPrice, Market AssetMarket);

    public sealed record PortfolioNavResult(
        bool IsValid, double PredictedNavPct, double ActualNavPct,
        double NavError, string Note);

    public enum CommitteeSignal { Unknown = -99, Bearish = -1, Neutral = 0, Bullish = 1 }

    public sealed record CommitteeAlignmentResult(
        bool IsValid, CommitteeSignal Signal, double ActualReturnPct,
        bool IsAligned, double AlignmentScore);

    public sealed record ScenarioCalibrationRow(
        string ScenarioTag, int TotalCount, int EvaluatedCount,
        double AvgMaterializationScore, double HitRatePct,
        double P25Score, double P50Score, double P75Score);

    public sealed record ScenarioCalibrationTable(
        IReadOnlyList<ScenarioCalibrationRow> Rows, DateTime AsOf, string Note);

    // ── 순수 수학 엔진 ─────────────────────────────────────────────────────────

    public static class ScenarioTrackerEngine
    {
        private const double DefaultRfDaily = 0.035 / 252.0;  // 무위험 수익률 연 3.5%

        // ── 1. 코사인 유사도 ───────────────────────────────────────────────────

        public static double CosineSimilarity(double[] a, double[] b)
        {
            if (a == null || b == null || a.Length == 0 || a.Length != b.Length)
                return 0.0;
            double dot = 0.0, magA = 0.0, magB = 0.0;
            for (int i = 0; i < a.Length; i++)
            {
                dot  += a[i] * b[i];
                magA += a[i] * a[i];
                magB += b[i] * b[i];
            }
            double denom = Math.Sqrt(magA) * Math.Sqrt(magB);
            if (denom < 1e-12) return 0.0;  // 영벡터 → 정의 불가 → 중립 반환
            return Math.Clamp(dot / denom, -1.0, 1.0);
        }

        // cosine [-1, 1] → MaterializationScore [0, 100]: 완전일치=100, 직교=50, 완전반대=0
        public static double MaterializationScore(double cosine)
            => Math.Clamp((cosine + 1.0) * 50.0, 0.0, 100.0);

        // ── 2. 레짐 벡터 빌더 ─────────────────────────────────────────────────

        // 예측 레짐 벡터 [returnPct, context]. context=1.0 은 중립 앙커 (영벡터 방지)
        public static double[] BuildPredictedVector(double returnPct, double context = 1.0)
            => new[] { returnPct, context };

        // 실제 일별 수익률 시계열 → [연환산수익률%, 연환산샤프] 레짐 벡터 추정
        public static double[] BuildActualVector(
            IList<double> dailyReturns, double rfDaily = DefaultRfDaily)
        {
            if (dailyReturns == null || dailyReturns.Count < 2)
                return new[] { 0.0, 1.0 };
            double mu    = Mean(dailyReturns);
            double sigma = StdDev(dailyReturns, mu);
            double annualReturn = (Math.Pow(1.0 + mu, 252.0) - 1.0) * 100.0;
            double sharpe       = sigma > 1e-10
                ? (mu - rfDaily) / sigma * Math.Sqrt(252.0)
                : 0.0;
            return new[] { annualReturn, sharpe };
        }

        // ── 3. PORTFOLIO NAV 오차 분석 ────────────────────────────────────────

        // 보유 종목 캡처가→실현가 가중 NAV 수익률 계산; ActualPrice 없는 종목 제외 후 재정규화
        public static PortfolioNavResult EvaluatePortfolioNav(
            IReadOnlyList<PortfolioHoldingCapture> holdings, double predictedNavPct)
        {
            if (holdings == null || holdings.Count == 0)
                return new(false, predictedNavPct, 0.0, 0.0, "보유 종목 없음");

            var valid = holdings
                .Where(h => h.ActualPrice.HasValue && (double)h.CapturePrice > 0)
                .ToList();
            if (valid.Count == 0)
                return new(false, predictedNavPct, 0.0, 0.0,
                    "실현가 없음 — 정직한 검증 경계: PORTFOLIO 평가 미완료");

            double totalWeight = valid.Sum(h => h.WeightPct);
            if (totalWeight <= 0.0)
                return new(false, predictedNavPct, 0.0, 0.0, "비중 합계 0");

            double actualNav = 0.0;
            foreach (var h in valid)
            {
                double ret = ((double)h.ActualPrice!.Value - (double)h.CapturePrice)
                           / (double)h.CapturePrice * 100.0;
                actualNav += (h.WeightPct / totalWeight) * ret;
            }

            double navError = predictedNavPct - actualNav;
            return new(true, predictedNavPct, actualNav, navError,
                $"{valid.Count}/{holdings.Count}개 종목 실현");
        }

        // ── 4. Committee 포지션 일치도 뼈대 (환각 방화벽 엄수) ─────────────────
        // LLM 텍스트는 keyword→enum 매핑만 수행; 코어 수치 연산과 완전 격리

        public static CommitteeSignal ParseCommitteeSignal(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return CommitteeSignal.Unknown;
            var lower = text.ToLowerInvariant();
            int bull = 0, bear = 0;
            foreach (var kw in _bullKeywords) if (lower.Contains(kw)) bull++;
            foreach (var kw in _bearKeywords) if (lower.Contains(kw)) bear++;
            if (bull == 0 && bear == 0) return CommitteeSignal.Unknown;  // 관련 키워드 없음
            if (bull == bear) return CommitteeSignal.Neutral;
            return bull > bear ? CommitteeSignal.Bullish : CommitteeSignal.Bearish;
        }

        public static CommitteeAlignmentResult ScoreCommitteeAlignment(
            CommitteeSignal signal, double actualReturnPct)
        {
            if (signal == CommitteeSignal.Unknown)
                return new(false, signal, actualReturnPct, false, 0.0);

            bool isAligned = (signal == CommitteeSignal.Bullish && actualReturnPct > 0.0)
                          || (signal == CommitteeSignal.Bearish && actualReturnPct < 0.0)
                          || (signal == CommitteeSignal.Neutral  && Math.Abs(actualReturnPct) < 2.0);

            // 일치 방향의 강도로 점수 조정
            double score = isAligned
                ? Math.Min(100.0, 60.0 + Math.Abs(actualReturnPct) * 2.0)
                : Math.Max(0.0,   40.0 - Math.Abs(actualReturnPct) * 2.0);

            return new(true, signal, actualReturnPct, isAligned, score);
        }

        // ── 5. 시나리오 캘리브레이션 테이블 ──────────────────────────────────────

        // ForecastRecord 목록에서 Multiverse 시나리오별 코사인 유사도 기반 실현 점수 집계
        public static ScenarioCalibrationTable BuildCalibrationTable(
            IReadOnlyList<ForecastRecord> records, DateTime asOf)
        {
            const string note = "Multiverse 시나리오별 코사인 유사도 기반 실현 점수";

            if (records == null || records.Count == 0)
                return new(Array.Empty<ScenarioCalibrationRow>(), asOf, note);

            var mv = records
                .Where(r => r.EngineType == ForecastEngineType.Multiverse
                         && !string.IsNullOrEmpty(r.ScenarioTag))
                .ToList();
            if (mv.Count == 0)
                return new(Array.Empty<ScenarioCalibrationRow>(), asOf, note);

            var groups = mv.GroupBy(r => r.ScenarioTag).OrderBy(g => g.Key).ToList();
            var rows   = new List<ScenarioCalibrationRow>(groups.Count);

            foreach (var grp in groups)
            {
                var all  = grp.ToList();
                var eval = all.Where(r => r.ActualValue.HasValue).ToList();

                // 코사인 유사도 기반 실현 점수 (evaluated 레코드만)
                var scores = eval
                    .Select(r =>
                    {
                        double[] vPred = BuildPredictedVector(r.PredictedValue);
                        double[] vAct  = BuildPredictedVector(r.ActualValue!.Value);
                        return MaterializationScore(CosineSimilarity(vPred, vAct));
                    })
                    .OrderBy(s => s)
                    .ToList();

                int hits = eval.Count(r =>
                    r.ActualValue.HasValue &&
                    Math.Sign(r.PredictedValue) == Math.Sign(r.ActualValue!.Value));
                double hitRate = eval.Count > 0 ? hits * 100.0 / eval.Count : 0.0;

                rows.Add(new ScenarioCalibrationRow(
                    ScenarioTag:             grp.Key,
                    TotalCount:              all.Count,
                    EvaluatedCount:          eval.Count,
                    AvgMaterializationScore: scores.Count > 0 ? scores.Average() : 0.0,
                    HitRatePct:              hitRate,
                    P25Score:                Percentile(scores, 25),
                    P50Score:                Percentile(scores, 50),
                    P75Score:                Percentile(scores, 75)
                ));
            }

            return new(rows, asOf, note);
        }

        // ── 내부 헬퍼 ──────────────────────────────────────────────────────────

        private static readonly string[] _bullKeywords =
            { "강세", "상승", "매수", "낙관", "긍정", "bull", "bullish", "growth", "optimistic" };

        private static readonly string[] _bearKeywords =
            { "약세", "하락", "매도", "비관", "부정", "bear", "bearish", "recession", "pessimistic" };

        private static double Mean(IList<double> xs)
        {
            if (xs.Count == 0) return 0.0;
            double s = 0.0;
            for (int i = 0; i < xs.Count; i++) s += xs[i];
            return s / xs.Count;
        }

        private static double StdDev(IList<double> xs, double mean)
        {
            if (xs.Count < 2) return 0.0;
            double s = 0.0;
            for (int i = 0; i < xs.Count; i++) { double d = xs[i] - mean; s += d * d; }
            return Math.Sqrt(s / (xs.Count - 1));
        }

        private static double Percentile(List<double> sortedAsc, double p)
        {
            if (sortedAsc.Count == 0) return 0.0;
            if (sortedAsc.Count == 1) return sortedAsc[0];
            double idx = (sortedAsc.Count - 1) * p / 100.0;
            int lo = (int)idx;
            int hi = Math.Min(lo + 1, sortedAsc.Count - 1);
            return sortedAsc[lo] + (idx - lo) * (sortedAsc[hi] - sortedAsc[lo]);
        }
    }
}
