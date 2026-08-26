// ============================================================================
// TailBudget — 테일 리스크 면역 설계사 (Season 9 #7, v3.70.0)
//  현재 포트폴리오 비중을 그리디 알고리즘으로 조정,
//  4개 역사적 위기 시나리오(BlackSwan.Library)에서의 최악 드로다운을 최소화.
//  순수 결정론 · 외부의존 0 · 매매 지시 없음(판단 보조 정보만)
// ============================================================================
using System;
using System.Collections.Generic;
using System.Linq;

namespace MS2E.Infrastructure
{
    public sealed record TailAllocation(
        string Symbol, string Name, double Beta,
        double OriginalWeight, double OptimalWeight)
    {
        public double Delta => OptimalWeight - OriginalWeight;
    }

    public sealed record TailScenario(
        string CrisisName, double BaselineMdd, double OptimalMdd)
    {
        public double ImprovementPct => BaselineMdd > 0
            ? (BaselineMdd - OptimalMdd) / BaselineMdd * 100.0 : 0.0;
    }

    public sealed record TailBudgetResult(
        bool IsValid,
        IReadOnlyList<TailAllocation> Allocations,
        IReadOnlyList<TailScenario> Scenarios,
        double BaselineWorstMdd,
        double OptimalWorstMdd,
        string WorstCrisis,
        string Note)
    {
        public double ImprovementPct => BaselineWorstMdd > 0
            ? (BaselineWorstMdd - OptimalWorstMdd) / BaselineWorstMdd * 100.0 : 0.0;
    }

    public static class TailBudget
    {
        /// <summary>
        /// 포트폴리오 비중 최적화 — 최악 시나리오 최대 드로다운 최소화.
        /// maxSingleWeight: 단일 종목 최대 비중 (0~1), minSingleWeight: 최소 비중.
        /// maxIterations: 그리디 반복 횟수 상한.
        /// </summary>
        public static TailBudgetResult Optimize(
            IReadOnlyList<BlackSwanHolding> holdings,
            double maxSingleWeight = 0.80,
            double minSingleWeight = 0.00,
            int maxIterations = 50)
        {
            if (holdings == null || holdings.Count == 0)
                return Fail("보유 종목이 없습니다.");
            if (holdings.Count == 1)
                return Fail("종목이 1개이면 비중 재조정이 불가합니다.");

            // 비중 정규화
            double totalW = holdings.Sum(h => h.Weight);
            if (totalW <= 0) return Fail("비중 합산이 0입니다.");
            var initW = holdings.Select(h => h.Weight / totalW).ToArray();

            maxSingleWeight = Math.Clamp(maxSingleWeight, 0.1, 1.0);
            minSingleWeight = Math.Clamp(minSingleWeight, 0.0, 0.1);

            // ── 기준선 평가 ──
            double baselineMdd = EvalWorstMdd(holdings, initW);
            string baselineCrisis = FindWorstCrisis(holdings, initW);

            // ── 그리디 최적화 ──
            var bestW = initW.ToArray();
            double bestMdd = baselineMdd;
            int n = holdings.Count;

            // 위험기여도 내림차순 정렬 인덱스 (변경 후 반복 갱신)
            for (int iter = 0; iter < maxIterations; iter++)
            {
                bool improved = false;

                // 각 hi→lo 쌍 시도 (위험기여도 차이 큰 순)
                var rc = bestW.Select((w, i) => w * Math.Abs(holdings[i].Beta)).ToArray();
                var hiOrder = Enumerable.Range(0, n).OrderByDescending(i => rc[i]).ToArray();
                var loOrder = Enumerable.Range(0, n).OrderBy(i => rc[i]).ToArray();

                foreach (int hi in hiOrder)
                {
                    foreach (int lo in loOrder)
                    {
                        if (hi == lo) continue;
                        double delta = 0.02; // 회당 2%p 이동
                        delta = Math.Min(delta, bestW[hi] - minSingleWeight);
                        delta = Math.Min(delta, maxSingleWeight - bestW[lo]);
                        if (delta < 0.001) continue;

                        var cand = bestW.ToArray();
                        cand[hi] -= delta;
                        cand[lo] += delta;

                        double mdd = EvalWorstMdd(holdings, cand);
                        if (mdd < bestMdd)
                        {
                            bestW = cand;
                            bestMdd = mdd;
                            improved = true;
                            break;
                        }
                    }
                    if (improved) break;
                }
                if (!improved) break; // 수렴
            }

            string worstCrisis = FindWorstCrisis(holdings, bestW);

            // ── 시나리오별 비교 ──
            var scenarios = BlackSwan.Library
                .Select(p =>
                {
                    var base_ = BlackSwan.Inject(p, MkHoldings(holdings, initW));
                    var opt   = BlackSwan.Inject(p, MkHoldings(holdings, bestW));
                    return new TailScenario(p.Name, base_.MaxDrawdownPct, opt.MaxDrawdownPct);
                })
                .OrderByDescending(s => s.BaselineMdd)
                .ToList();

            var allocs = holdings
                .Select((h, i) => new TailAllocation(
                    h.Symbol, h.Name, h.Beta,
                    Math.Round(initW[i], 4),
                    Math.Round(bestW[i], 4)))
                .OrderByDescending(a => Math.Abs(a.Delta))
                .ToList();

            string note = bestMdd < baselineMdd
                ? $"최악 시나리오 '{worstCrisis}' 드로다운 {(baselineMdd - bestMdd):0.0}%p 개선 (그리디 최적화)."
                : $"현재 비중이 이미 최적에 가깝습니다 (그리디 수렴). 개선 여지 없음.";

            return new TailBudgetResult(true, allocs, scenarios,
                baselineMdd, bestMdd, worstCrisis, note);
        }

        // ── 내부 헬퍼 ──
        private static double EvalWorstMdd(IReadOnlyList<BlackSwanHolding> h, double[] w)
            => BlackSwan.Library.Max(p => BlackSwan.Inject(p, MkHoldings(h, w)).MaxDrawdownPct);

        private static string FindWorstCrisis(IReadOnlyList<BlackSwanHolding> h, double[] w)
        {
            var hs = MkHoldings(h, w);
            return BlackSwan.Library
                .OrderByDescending(p => BlackSwan.Inject(p, hs).MaxDrawdownPct)
                .First().Name;
        }

        private static IReadOnlyList<BlackSwanHolding> MkHoldings(
            IReadOnlyList<BlackSwanHolding> orig, double[] weights)
            => orig.Select((h, i) => h with { Weight = weights[i] }).ToArray();

        private static TailBudgetResult Fail(string msg) =>
            new TailBudgetResult(false, Array.Empty<TailAllocation>(),
                Array.Empty<TailScenario>(), 0, 0, "", msg);
    }
}
