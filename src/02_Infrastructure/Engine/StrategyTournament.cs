// ============================================================================
// StrategyTournament - 전략 토너먼트 (Season 9 #2) · 순수 결정론 워크포워드 리그 (외부 의존 0)
//  저장된 카트리지 전략(MA크로스)·컴파일러 스펙·내장 벤치마크를 한 종목 히스토리에서 동일 조건으로
//  여러 OOS 폴드에 맞붙여 강건성으로 랭킹한다(StrategyForge와 같은 평가 철학: 점수=OOS평균−λ·표준편차).
//   · 결정론: 난수·시계 없음. MA크로스는 BacktestEngine.RunMaCross, 스펙은 StrategyCompiler.Backtest로 평가.
//   · 폴드가 너무 짧으면(워밍업 미만) 해당 폴드 제외; 유효 폴드 2개 미만이면 출전 무효.
//  ⚠️ 과거 백테스트 비교이며 미래 성과 보장·투자 권유가 아님.
// ============================================================================
using System;
using System.Collections.Generic;
using System.Linq;

namespace MS2E.Infrastructure
{
    public enum EntryKind { MaCross, Spec, BuiltIn }

    public sealed record Entrant(string Name, string Source, EntryKind Kind, int Short, int Long, int BuiltInIndex, StrategySpec? Spec);

    public sealed record LeagueRow(string Name, string Source, double OosMeanPct, double OosStdPct, double Score,
        double FullReturnPct, int Trades, int ValidFolds, int Rank, bool Valid, string Note);

    public sealed record LeagueResult(bool IsValid, IReadOnlyList<LeagueRow> Rows, int Folds, int Entrants, string Note);

    public static class StrategyTournament
    {
        public const int MinFoldCandles = 40;

        public static LeagueResult Run(IReadOnlyList<Entrant> entrants, List<Candle> candles, Market market,
            int folds = 4, double lambda = 0.5)
        {
            if (entrants == null || entrants.Count == 0) return new(false, Array.Empty<LeagueRow>(), 0, 0, "출전 전략이 없습니다.");
            if (candles == null || candles.Count < 120) return new(false, Array.Empty<LeagueRow>(), 0, entrants.Count, "캔들 부족(최소 120거래일 필요).");
            folds = Math.Clamp(folds, 2, 8);

            var segs = Split(candles, folds);
            var rows = new List<LeagueRow>();
            foreach (var e in entrants)
            {
                var foldRets = new List<double>();
                foreach (var seg in segs)
                {
                    if (seg.Count < MinFoldCandles) continue;
                    var bt = Evaluate(e, seg, market);
                    if (bt.valid) foldRets.Add(bt.ret);
                }
                var full = Evaluate(e, candles, market);
                if (foldRets.Count < 2)
                {
                    rows.Add(new LeagueRow(e.Name, e.Source, 0, 0, double.NegativeInfinity, full.valid ? full.ret : 0,
                        full.trades, foldRets.Count, 0, false, "유효 폴드 부족"));
                    continue;
                }
                double mean = foldRets.Average();
                double std = Std(foldRets);
                double score = mean - lambda * std;
                rows.Add(new LeagueRow(e.Name, e.Source, mean, std, score, full.valid ? full.ret : 0,
                    full.trades, foldRets.Count, 0, true, ""));
            }

            // 랭킹: 유효 출전만 점수 내림차순, 동점은 OOS평균 → 이름
            var ranked = rows.OrderByDescending(r => r.Valid)
                .ThenByDescending(r => r.Score)
                .ThenByDescending(r => r.OosMeanPct)
                .ThenBy(r => r.Name, StringComparer.Ordinal)
                .ToList();
            int rank = 0;
            for (int i = 0; i < ranked.Count; i++)
                ranked[i] = ranked[i] with { Rank = ranked[i].Valid ? ++rank : 0 };

            int validN = ranked.Count(r => r.Valid);
            return new LeagueResult(validN > 0, ranked, folds, entrants.Count,
                $"{entrants.Count}개 출전 · {validN}개 유효 · {folds}폴드 OOS · 점수=OOS평균−{lambda:0.#}·표준편차");
        }

        private static (bool valid, double ret, int trades) Evaluate(Entrant e, List<Candle> seg, Market market)
        {
            // 워밍업 미만 폴드는 엔진과 무관하게 동일 기준으로 제외(공정성: 모든 출전 동일 폴드).
            int warm = e.Kind switch
            {
                EntryKind.MaCross => e.Long + 1,
                EntryKind.BuiltIn => 30,
                EntryKind.Spec when e.Spec != null => StrategyCompiler.Warmup(e.Spec),
                _ => int.MaxValue
            };
            if (seg.Count <= warm) return (false, 0, 0);
            try
            {
                BacktestSummary s = e.Kind switch
                {
                    EntryKind.MaCross => BacktestEngine.RunMaCross(seg, market, e.Short, e.Long),
                    EntryKind.BuiltIn => BacktestEngine.Run(seg, e.BuiltInIndex, market),
                    EntryKind.Spec when e.Spec != null => StrategyCompiler.Backtest(e.Spec, seg, market),
                    _ => new BacktestSummary()
                };
                bool valid = s.EquityCurve.Count > 0 || s.TradeCount > 0;
                return (valid, (double)s.TotalReturn, s.TradeCount);
            }
            catch { return (false, 0, 0); }
        }

        // 연속 등분할(StrategyForge와 동일한 OOS 폴드 철학)
        private static List<List<Candle>> Split(List<Candle> candles, int folds)
        {
            var segs = new List<List<Candle>>(folds);
            int n = candles.Count, size = n / folds;
            for (int f = 0; f < folds; f++)
            {
                int start = f * size;
                int len = (f == folds - 1) ? n - start : size;
                segs.Add(candles.GetRange(start, len));
            }
            return segs;
        }

        private static double Std(List<double> xs)
        {
            if (xs.Count < 2) return 0;
            double m = xs.Average(), ss = 0;
            foreach (var x in xs) ss += (x - m) * (x - m);
            return Math.Sqrt(ss / (xs.Count - 1));
        }
    }
}
