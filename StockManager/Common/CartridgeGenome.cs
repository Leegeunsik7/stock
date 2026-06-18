// ============================================================================
// CartridgeGenome — 전략 유전자 진화소 (Season 9 #6, v3.71.0)
//  기존 카트리지 스펙을 '전략 DNA'로 보고, 교차(Crossover)·돌연변이(Mutation)·
//  워크포워드 선택(Selection)으로 새 전략을 진화시킨다.
//  순수 결정론 · 외부의존 0 · FNV 해시 기반 의사난수(재현 가능)
//
//  ▣ 족보(Lineage) 추적 — 각 개체가 어떤 부모의 DNA를 물려받고 어느 파라미터에
//    변이가 일어났는지 결정론적 문자열로 기록한다. 예:
//      "교차(진입:a1b2c3 + 청산:d4e5f6) -> 변이(진입SMA5->7, 손절%0->2)"
//    동일 입력은 항상 동일 족보를 산출한다(난수·시계 없음).
// ============================================================================
using System;
using System.Collections.Generic;
using System.Linq;

namespace StockManager.Common
{
    public sealed record GenomeEntry(
        StrategySpec Spec, string Origin, string Lineage, int Generation,
        double Sharpe, double TotalReturn, int Trades);

    public sealed record GenomeResult(
        bool IsValid,
        IReadOnlyList<GenomeEntry> Ranked,
        int TotalEvaluated,
        int Generations,
        string Note);

    public static class CartridgeGenome
    {
        // 개체: 스펙 + 출처 카테고리 + 전체 족보 + 세대
        private sealed record Tagged(StrategySpec Spec, string Origin, string Lineage, int Gen);

        // 출처 카테고리(짧은 배지용)
        private const string OrgSeed  = "씨앗";
        private const string OrgMut   = "변이";
        private const string OrgCross = "교차";
        private const string OrgBoth  = "교차+변이";

        // ── 공개 진입점 ──
        public static GenomeResult Evolve(
            IReadOnlyList<StrategySpec> seeds,
            List<Candle> candles,
            Market market,
            int generations = 4,
            int populationSize = 12,
            OrderAttestationService? gating = null)
        {
            // 비상 동결 게이팅 인터셉터 — EmergencyFreeze 시 InvalidOperationException
            gating?.AssertNormal("CartridgeGenome.Evolve");

            generations    = Math.Clamp(generations,    1, 8);
            populationSize = Math.Clamp(populationSize, 4, 20);

            if (seeds == null || seeds.Count == 0)
                return new GenomeResult(false, Array.Empty<GenomeEntry>(), 0, 0,
                    "씨앗 전략이 없습니다. 카트리지를 먼저 저장하세요.");
            if (candles == null || candles.Count < 60)
                return new GenomeResult(false, Array.Empty<GenomeEntry>(), 0, 0,
                    "캔들이 부족합니다 (최소 60봉 필요).");

            // 초기 모집단: 씨앗 + (부족분) 씨앗 변이
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

                // 생존자는 자신의 원래 족보를 유지한 채 세대 태그만 갱신
                var next = survivors.Select(t => t with { Gen = gen }).ToList();
                int s = gen * 997, guard = 0;
                while (next.Count < populationSize && guard++ < populationSize * 20)
                {
                    int ia = PseudoIdx(s++, survivors.Count);
                    int ib = PseudoIdx(s++, survivors.Count);
                    if (ib == ia) ib = (ib + 1) % survivors.Count;       // 자가교차 회피(결정론)
                    var pa = survivors[ia];
                    var pb = survivors[ib];

                    // ① 교차
                    var (child, xLin) = Crossover(pa.Spec, pb.Spec, s++);
                    string origin  = OrgCross;
                    string lineage = xLin;

                    // ② 약 50% 확률로 교차산물에 변이 추가
                    if (PseudoBool(s++))
                    {
                        var (mchild, mdesc) = Mutate(child, s++);
                        child   = mchild;
                        origin  = OrgBoth;
                        lineage = xLin + " -> 변이(" + mdesc + ")";
                    }

                    var (ok, _) = StrategyCompiler.Validate(child);
                    if (ok) next.Add(new Tagged(child, origin, lineage, gen));
                    else s += 3;
                }
                pop = next;
            }

            // 최종 평가
            var final = Evaluate(pop, candles, market);
            totalEval += final.Count;

            // 샤프 내림차순 + 동일 스펙 중복 제거(리더보드 정합)
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var ranked = final
                .OrderByDescending(x => x.sharpe)
                .Where(x => seen.Add(x.tagged.Spec.Id))
                .Select(x => new GenomeEntry(
                    x.tagged.Spec, x.tagged.Origin, x.tagged.Lineage, x.tagged.Gen,
                    x.sharpe, x.ret, x.trades))
                .ToList();

            return new GenomeResult(true, ranked, totalEval, generations,
                $"씨앗 {seeds.Count}개 -> {generations}세대 진화 -> {ranked.Count}전략 평가 완료.");
        }

        // ── 초기 모집단: 씨앗 + 부족분 변이 보충 ──
        private static List<Tagged> InitPopulation(IReadOnlyList<StrategySpec> seeds, int size)
        {
            var pop = seeds.Take(size)
                .Select(SnapPct)
                .Select(sn => new Tagged(sn, OrgSeed, $"씨앗({Short6(sn.Id)})", 0))
                .ToList();
            int seed = 1, guard = 0;
            while (pop.Count < size && seeds.Count > 0 && guard++ < size * 20)
            {
                var src = SnapPct(seeds[PseudoIdx(seed++, seeds.Count)]);
                var (m, mdesc) = Mutate(src, seed++);
                var (ok, _) = StrategyCompiler.Validate(m);
                if (ok) pop.Add(new Tagged(m, OrgMut, $"변이({Short6(src.Id)}: {mdesc})", 0));
                else seed += 3;
            }
            return pop;
        }

        // ── 교차: 한 부모의 진입 DNA + 다른 부모의 청산 DNA ──
        //  족보에 어느 부모가 진입/청산을 물려줬는지 결정론적으로 기록.
        private static (StrategySpec spec, string lineage) Crossover(StrategySpec a, StrategySpec b, int seed)
        {
            bool swap = PseudoBool(seed);
            // swap=false: 진입=A, 청산=B / swap=true: 진입=B, 청산=A
            var entryPar = swap ? b : a;
            var exitPar  = swap ? a : b;

            // 손절·익절은 정수 퍼센트포인트 격자로 반올림(분수 드리프트 방지 — 0.x% 손절 같은 병리 차단)
            var spec = new StrategySpec("", $"교차 {Short(a.Id, 4)}x{Short(b.Id, 4)}",
                entryPar.Entry, entryPar.EntryLogic,
                exitPar.Exit,  exitPar.ExitLogic,
                Math.Round((a.StopLossPct   + b.StopLossPct)   / 2.0),
                Math.Round((a.TakeProfitPct + b.TakeProfitPct) / 2.0));
            spec = spec with { Id = StrategyCompiler.Id(spec) };

            string lineage = $"교차(진입:{Short6(entryPar.Id)} + 청산:{Short6(exitPar.Id)})";
            return (spec, lineage);
        }

        // ── 돌연변이: 파라미터 근방 탐색 + 변경 항목 결정론적 기록 ──
        private static (StrategySpec spec, string changes) Mutate(StrategySpec s, int seed)
        {
            var changes = new List<string>();
            var entry = MutateTerms(s.Entry, seed,       "진입", changes);
            var exit_ = MutateTerms(s.Exit,  seed + 100, "청산", changes);

            // 손절·익절은 '퍼센트포인트' 단위(0~95 / 0~1000). 분수가 아님에 주의.
            double sl = s.StopLossPct, tp = s.TakeProfitPct;
            double nsl = Math.Round(Math.Clamp(s.StopLossPct   + DeltaPp(seed + 200), 0.0, 50.0));
            if (Math.Abs(nsl - sl) > 1e-9) { changes.Add($"손절%{sl:0.#}->{nsl:0.#}"); sl = nsl; }
            double ntp = Math.Round(Math.Clamp(s.TakeProfitPct + DeltaPp(seed + 300), 0.0, 100.0));
            if (Math.Abs(ntp - tp) > 1e-9) { changes.Add($"익절%{tp:0.#}->{ntp:0.#}"); tp = ntp; }

            var spec = new StrategySpec("", s.Title, entry, s.EntryLogic, exit_, s.ExitLogic, sl, tp);
            spec = spec with { Id = StrategyCompiler.Id(spec) };
            return (spec, changes.Count > 0 ? string.Join(", ", changes) : "무변화");
        }

        private static IReadOnlyList<SpecCond> MutateTerms(
            IReadOnlyList<SpecCond> conds, int seed, string side, List<string> changes)
        {
            var result = new List<SpecCond>(conds.Count);
            int s = seed;
            foreach (var c in conds)
            {
                var left  = MutateTerm(c.Left, s++, side, changes);
                var right = c.Right != null ? MutateTerm(c.Right, s++, side, changes) : c.Right;
                double k  = c.Const;
                if (c.RightIsConst)
                {
                    double nk = Math.Clamp(c.Const + DeltaConst(s++), 0.0, 200.0);
                    if (Math.Abs(nk - c.Const) > 1e-9) { changes.Add($"{side}임계{c.Const:0.#}->{nk:0.#}"); k = nk; }
                }
                result.Add(new SpecCond(left, c.Op, c.RightIsConst, k, right));
            }
            return result;
        }

        private static SpecTerm MutateTerm(SpecTerm t, int seed, string side, List<string> changes)
        {
            // 파라미터 없는 항(종가·MACD히스토)은 변이 대상 아님
            if (t.Kind is SpecKind.Price or SpecKind.MacdHist) return t;
            int[] deltas = { -5, -2, -1, 0, 1, 2, 5 };
            int delta = deltas[PseudoIdx(seed, deltas.Length)];
            int np = Math.Clamp(t.Param + delta, StrategyCompiler.MinPeriod, StrategyCompiler.MaxPeriod);
            if (np != t.Param) changes.Add($"{side}{KindTag(t.Kind)}{t.Param}->{np}");
            return t with { Param = np };
        }

        private static string KindTag(SpecKind k) => k switch
        {
            SpecKind.Sma => "SMA", SpecKind.Ema => "EMA", SpecKind.Rsi => "RSI",
            SpecKind.PctB => "%B", SpecKind.MacdHist => "MACD", SpecKind.Price => "종가",
            _ => k.ToString()
        };

        private static double DeltaPp(int seed)      // 손절·익절 퍼센트포인트 변이
        {
            double[] d = { -4, -2, 0, 2, 4 };
            return d[PseudoIdx(seed, d.Length)];
        }
        private static double DeltaConst(int seed)   // 임계(RSI 등) 변이
        {
            double[] d = { -5, -2, 0, 2, 5 };
            return d[PseudoIdx(seed, d.Length)];
        }

        // ── 평가: StrategyCompiler.Backtest (결정론) ──
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

        // ── ID 단축 헬퍼(족보 표기용) ──
        private static string Short(string id, int n) =>
            string.IsNullOrEmpty(id) ? new string('?', n) : (id.Length >= n ? id[..n] : id);
        private static string Short6(string id) => Short(id, 6);

        // 씨앗 손절·익절을 정수 퍼센트포인트 격자로 스냅 (분수 씨앗의 유전자 풀 유입 차단)
        private static StrategySpec SnapPct(StrategySpec s)
        {
            double sl = Math.Round(s.StopLossPct);
            double tp = Math.Round(s.TakeProfitPct);
            if (Math.Abs(sl - s.StopLossPct) < 1e-9 && Math.Abs(tp - s.TakeProfitPct) < 1e-9)
                return s;
            var snap = s with { Id = "", StopLossPct = sl, TakeProfitPct = tp };
            return snap with { Id = StrategyCompiler.Id(snap) };
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
