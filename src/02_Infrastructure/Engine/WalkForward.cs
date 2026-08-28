// ============================================================================
// WalkForward - 자기진화 전략 랩: Walk-Forward 검증 🅖 (외부 의존 0) (v3.17.0)
//  옵티마이저의 약점(전체기간 in-sample 최적화 → 과최적화)을 잡는다.
//  히스토리를 롤링 학습/검증 창으로 쪼개:
//    · train 창에서 StrategyOptimizer로 최적 MA 파라미터를 뽑고(in-sample)
//    · 바로 뒤 test 창에서 그 파라미터를 재채점한다(out-of-sample)
//  OOS에서도 살아남는 전략만 "강건(robust)". in/out 성과 갭으로 과최적화를 정량화.
//
//  설계 노트 / 숨은 룰:
//   · test 창 백테스트는 앞에 longP만큼 lead-in을 붙여 MA 워밍업을 확보 → 거래는 test 구간부터
//   · train 창에서 Optimize가 무효(무거래)면 그 폴드는 건너뜀
//   · 추천 파라미터 = 폴드별 우승값의 최빈값(동률이면 OOS 평균 높은 쪽)
//   · 결정적: 동일 캔들 → 동일 결과 (난수 없음). BacktestEngine/StrategyOptimizer만 사용.
//   · 이 모듈은 "검증"만 한다. 강건 전략의 모의 집행/주입은 호출측 게이팅(실거래 보호).
// ============================================================================
using System;
using System.Collections.Generic;
using System.Linq;
using MS2E.UI.Portable.Ports.Strategy;   // Phase 94-F: 읽기 전용 워크포워드 결과 계약

namespace MS2E.Infrastructure
{
    /// <summary>한 폴드(train→test)의 in/out 성과.</summary>
    public class WalkForwardFold : IWalkForwardFold
    {
        public int Index { get; set; }
        public int ShortP { get; set; }
        public int LongP { get; set; }
        public decimal InSampleReturn { get; set; }    // train 우승 총수익률
        public decimal OutSampleReturn { get; set; }   // 그 파라미터의 test 총수익률
        public int OosTradeCount { get; set; }
        public string Label => $"MA{ShortP}/{LongP}";
        public bool OosWin => OutSampleReturn > 0;
    }

    /// <summary>한 종목의 Walk-Forward 결과(폴드 + 집계 + 강건성 판정).</summary>
    public class WalkForwardResult : IWalkForwardReport
    {
        // Phase 94-F: 공변 업캐스트 — List<WalkForwardFold> → IReadOnlyList<IWalkForwardFold>.
        IReadOnlyList<IWalkForwardFold> IWalkForwardReport.Folds => Folds;

        public string Symbol { get; set; } = "";
        public string Name { get; set; } = "";
        public bool IsValid { get; set; }
        public string Note { get; set; } = "";
        public List<WalkForwardFold> Folds { get; set; } = new();
        public int FoldCount => Folds.Count;

        public decimal AvgInSample { get; set; }
        public decimal AvgOutSample { get; set; }
        public decimal OosWinRate { get; set; }     // OOS 양(+) 폴드 비율 %
        public decimal Degradation { get; set; }    // AvgInSample - AvgOutSample (과최적화 갭)
        public decimal Efficiency { get; set; }     // Walk-Forward Efficiency: OOS/IS %

        public int RecShortP { get; set; }           // 추천(최빈) 파라미터
        public int RecLongP { get; set; }
        public string RecLabel => RecShortP > 0 ? $"MA{RecShortP}/{RecLongP}" : "-";

        public bool IsRobust { get; set; }
        public string Verdict { get; set; } = "";    // 강건 / 취약(과최적화) / 부적합 / 보통
    }

    public static class WalkForward
    {
        /// <summary>롤링 Walk-Forward 분석. trainBars는 격자 최장기(60)+2 이상이어야 함.</summary>
        public static WalkForwardResult Analyze(string symbol, string name, List<Candle> candles,
            Market market, int trainBars = 120, int testBars = 40)
        {
            var res = new WalkForwardResult { Symbol = symbol, Name = name };
            int minTrain = StrategyOptimizer.LongGrid.Max() + 2;
            if (trainBars < minTrain) trainBars = minTrain;
            if (testBars < 5) testBars = 5;

            int n = candles?.Count ?? 0;
            if (candles == null || n < trainBars + testBars)
            {
                res.Note = $"캔들 부족(필요 {trainBars + testBars}+, 현재 {n})";
                return res;
            }

            for (int trainStart = 0; ; trainStart += testBars)
            {
                int trainEnd = trainStart + trainBars;
                int testEnd = trainEnd + testBars;
                if (testEnd > n) break;

                var trainSlice = candles.GetRange(trainStart, trainBars);
                var opt = StrategyOptimizer.Optimize(symbol, name, trainSlice, market);
                if (!opt.IsValid || opt.Best == null) continue;   // train 무효 폴드는 건너뜀

                int lead = opt.Best.LongP;                         // MA 워밍업 확보용 lead-in
                int from = trainEnd - lead;
                var testSlice = candles.GetRange(from, testEnd - from);
                var oos = BacktestEngine.RunMaCross(testSlice, market, opt.Best.ShortP, opt.Best.LongP);

                res.Folds.Add(new WalkForwardFold
                {
                    Index = res.Folds.Count,
                    ShortP = opt.Best.ShortP, LongP = opt.Best.LongP,
                    InSampleReturn = opt.Best.TotalReturn,
                    OutSampleReturn = oos.TotalReturn,
                    OosTradeCount = oos.TradeCount
                });
            }

            if (res.Folds.Count == 0)
            {
                res.Note = "유효 폴드 없음(전 구간 무거래)";
                return res;
            }

            res.AvgInSample = res.Folds.Average(f => f.InSampleReturn);
            res.AvgOutSample = res.Folds.Average(f => f.OutSampleReturn);
            res.OosWinRate = (decimal)res.Folds.Count(f => f.OosWin) / res.Folds.Count * 100m;
            res.Degradation = res.AvgInSample - res.AvgOutSample;
            res.Efficiency = res.AvgInSample > 0 ? res.AvgOutSample / res.AvgInSample * 100m
                                                 : (res.AvgOutSample >= 0 ? 100m : 0m);

            // 추천 파라미터 = 최빈 (동률이면 OOS 평균 높은 조합)
            var rec = res.Folds.GroupBy(f => (f.ShortP, f.LongP))
                .OrderByDescending(g => g.Count())
                .ThenByDescending(g => g.Average(x => x.OutSampleReturn))
                .First().Key;
            res.RecShortP = rec.ShortP; res.RecLongP = rec.LongP;

            res.IsValid = true;
            (res.IsRobust, res.Verdict) = Judge(res);
            res.Note = $"{res.FoldCount}폴드 · OOS승률 {res.OosWinRate:0}% · 효율 {res.Efficiency:0}% · {res.Verdict}";
            return res;
        }

        // 강건성 판정: OOS가 실제로 돈을 벌고 폴드 다수가 양(+)이어야 robust.
        private static (bool robust, string verdict) Judge(WalkForwardResult r)
        {
            if (r.AvgInSample <= 0) return (false, "부적합");                       // in-sample도 못 벎
            if (r.AvgOutSample <= 0) return (false, "취약(과최적화)");              // IS만 좋고 OOS 실패
            if (r.FoldCount >= 3 && r.OosWinRate >= 60m && r.AvgOutSample > 0)
                return (true, "강건");
            return (false, "보통");
        }
    }
}
