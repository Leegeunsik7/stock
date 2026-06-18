// ============================================================================
// CartridgeGenome — 전략 유전자 진화소 (Season 9 #6, v3.69.0)
//  기존 카트리지 스펙을 '전략 DNA'로 보고, 교차(Crossover)·돌연변이(Mutation)·
//  워크포워드 선택(Selection)으로 새 전략을 진화시킨다.
//  순수 결정론 · 외부의존 0 · FNV 해시 기반 의사난수(재현 가능)
// ============================================================================
using System;
using System.Collections.Generic;
using System.Linq;

namespace StockManager.Common
{
    public sealed record GenomeEntry(
        StrategySpec Spec, string Origin, int Generation,
        double Sharpe, double TotalReturn, int Trades);

    public sealed record GenomeResult(
        bool IsValid,
        IReadOnlyList<GenomeEntry> Ranked,
        int TotalEvaluated,
        int Generations,
        string Note);

    public static class CartridgeGenome
    {
        private sealed record Tagged(StrategySpec Spec, string Origin, int Gen);

        // ── 공개 진입점 ──
        public static GenomeResult Evolve(
            IReadOnlyList<StrategySpec> seeds,
            List<Candle> candles,
            Market market,
            int generations = 4,
            int populationSize = 12)
        {
            generations    = Math.Clamp(generations,    1, 8);
            populationSize = Math.Clamp(populationSize, 4, 20);

            if (seeds == null || seeds.Count == 0)
                return new GenomeResult(false, Array.Empty<GenomeEntry>(), 0, 0,
                    "씨앗 전략이 없습니다. 카트리지를 먼저 저장하세요.");
            if (candles == null || candles.Count < 60)
                return new GenomeResult(false, Array.Empty<GenomeEntry>(), 0, 0,
                    "캔들이 부족합니다 (최소 60봉 필요).");

            // 초기 모집단: 씨앗 (origin = "씨앗")
            var pop = InitPopulation(seeds, populationSize);
            int totalEval = 0;

            for (int gen = 1; gen <= generations; gen++)
            {
                var scored = Evaluate(pop, candles, market);
                totalEval += scored.Count;

                var survivors = scored
                    .OrderByDescending(x => x.sharpe)
                    .Take(Math.Max(2, populationSize / 2))
                    .Select(x => x.tagged)
                    .ToList();
                if (survivors.Count < 2) break;

                var next = survivors.Select(t => t with { Gen = gen }).ToList();
                int s = gen * 997;
                while (next.Count < populationSize)
                {
                    var a = survivors[PseudoIdx(s++, survivors.Count)].Spec;
                    var b = survivors[PseudoIdx(s++, survivors.Count)].Spec;
                    StrategySpec child;
                    string org;
                    if (PseudoBool(s++)) { child = Crossover(a, b, s++); org = "교차"; }
                    else                 { child = Mutate(a, s++);        org = "돌연변이"; }
                    var (ok, _) = StrategyCompiler.Validate(child);
                    if (ok) next.Add(new Tagged(child, org, gen));
                    else s += 3;
                }
                pop = next;
            }

            // 최종 평가
            var final = Evaluate(pop, candles, market);
            totalEval += final.Count;

            var ranked = final
                .OrderByDescending(x => x.sharpe)
                .Select(x => new GenomeEntry(
                    x.tagged.Spec, x.tagged.Origin, x.tagged.Gen,
                    x.sharpe, x.ret, x.trades))
                .ToList();

            return new GenomeResult(true, ranked, totalEval, generations,
                $"씨앗 {seeds.Count}개 → {generations}세대 진화 → {ranked.Count}전략 평가 완료.");
        }

        // ── 초기 모집단: 씨앗 + 돌연변이 보충 ──
        private static List<Tagged> InitPopulation(IReadOnlyList<StrategySpec> seeds, int size)
        {
            var pop = seeds.Take(size).Select(s => new Tagged(s, "씨앗", 0)).ToList();
            int seed = 1;
            while (pop.Count < size && seeds.Count > 0)
            {
                var src = seeds[PseudoIdx(seed++, seeds.Count)];
                var m   = Mutate(src, seed++);
                var (ok, _) = StrategyCompiler.Validate(m);
                if (ok) pop.Add(new Tagged(m, "돌연변이", 0));
                else seed += 3;
                if (seed > 9999) break; // 무한루프 방지
            }
            return pop;
        }

        // ── 교차: A의 진입 + B의 청산 (또는 역) ──
        private static StrategySpec Crossover(StrategySpec a, StrategySpec b, int seed)
        {
            bool swap   = PseudoBool(seed);
            var entry   = swap ? b.Entry      : a.Entry;
            var enLogic = swap ? b.EntryLogic : a.EntryLogic;
            var exit_   = swap ? a.Exit       : b.Exit;
            var exLogic = swap ? a.ExitLogic  : b.ExitLogic;
            var sl      = (a.StopLossPct   + b.StopLossPct)   / 2.0;
            var tp      = (a.TakeProfitPct + b.TakeProfitPct) / 2.0;
            var spec    = new StrategySpec("", $"교차({a.Id[..4]}×{b.Id[..4]})",
                entry, enLogic, exit_, exLogic, sl, tp);
            return spec with { Id = StrategyCompiler.Id(spec) };
        }

        // ── 돌연변이: 파라미터 근방 탐색 ──
        private static StrategySpec Mutate(StrategySpec s, int seed)
        {
            var entry  = MutateTerms(s.Entry, seed);
            var exit_  = MutateTerms(s.Exit,  seed + 100);
            var sl     = Math.Clamp(s.StopLossPct   + DeltaPct(seed + 200), 0.01, 0.30);
            var tp     = Math.Clamp(s.TakeProfitPct + DeltaPct(seed + 300), 0.01, 0.50);
            var spec   = new StrategySpec("", $"돌연변이({s.Id[..4]})",
                entry, s.EntryLogic, exit_, s.ExitLogic, sl, tp);
            return spec with { Id = StrategyCompiler.Id(spec) };
        }

        private static IReadOnlyList<SpecCond> MutateTerms(IReadOnlyList<SpecCond> conds, int seed)
        {
            var result = new List<SpecCond>(conds.Count);
            int s = seed;
            foreach (var c in conds)
            {
                var left  = MutateTerm(c.Left, s++);
                var right = c.Right != null ? MutateTerm(c.Right, s++) : c.Right;
                var k     = c.RightIsConst
                    ? Math.Clamp(c.Const + DeltaConst(s++), 0.0, 200.0)
                    : c.Const;
                result.Add(new SpecCond(left, c.Op, c.RightIsConst, k, right));
            }
            return result;
        }

        private static SpecTerm MutateTerm(SpecTerm t, int seed)
        {
            int[] deltas = { -5, -2, -1, 0, 1, 2, 5 };
            int delta    = deltas[PseudoIdx(seed, deltas.Length)];
            return t with { Param = Math.Max(1, t.Param + delta) };
        }

        private static double DeltaPct(int seed)
        {
            double[] d = { -0.04, -0.02, 0.0, 0.02, 0.04 };
            return d[PseudoIdx(seed, d.Length)];
        }
        private static double DeltaConst(int seed)
        {
            double[] d = { -5, -2, 0, 2, 5 };
            return d[PseudoIdx(seed, d.Length)];
        }

        // ── 평가: StrategyCompiler.Backtest ──
        private static List<(Tagged tagged, double sharpe, double ret, int trades)> Evaluate(
            IEnumerable<Tagged> pop, List<Candle> candles, Market market)
        {
            var result = new List<(Tagged, double, double, int)>();
            foreach (var t in pop)
            {
                try
                {
                    var bt = StrategyCompiler.Backtest(t.Spec, candles, market);
                    result.Add((t, (double)bt.Sharpe, (double)bt.TotalReturn, bt.TradeCount));
                }
                catch { /* 유효하지 않은 스펙은 스킵 */ }
            }
            return result;
        }

        // ── 결정론 의사난수 (FNV-1a 해시) ──
        private static uint Fnv(int seed)
        {
            uint h = 2166136261u;
            h ^= (uint)(seed         & 0xFF); h *= 16777619;
            h ^= (uint)((seed >>  8) & 0xFF); h *= 16777619;
            h ^= (uint)((seed >> 16) & 0xFF); h *= 16777619;
            return h;
        }
        private static int  PseudoIdx(int seed, int n) => n > 0 ? (int)(Fnv(seed) % (uint)n) : 0;
        private static bool PseudoBool(int seed)       => (Fnv(seed) & 1u) == 1u;
    }
}
