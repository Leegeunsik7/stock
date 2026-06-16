// ============================================================================
// StrategyForge - 자가 전략 발견 엔진 (Season 7 #3) · 순수 결정론, 외부 의존 0
//  MA 단·장기 격자를 K개 연속 OOS 폴드로 평가해 '강건한' 전략을 발굴·순위화한다.
//   · 과최적화 방지: 단일 전체구간 수익이 아니라 폴드별 OOS 수익의 평균 − λ·표준편차(일관성 패널티)로 채점.
//   · 거래 빈약(폴드당 minTrades 미만) 조합은 제외 — 우연/과적합 컷.
//   · 결정론(난수 없음) → 동일 캔들 → 동일 순위. 발굴 결과는 #2 카트리지로 서명·증명서 포장 가능.
//  ⚠️ 발굴 성과는 과거 기반 정보 제공이며 미래 수익 보장이 아님.
// ============================================================================
using System;
using System.Collections.Generic;
using System.Linq;

namespace StockManager.Common
{
    public sealed record ForgeCandidate(int Short, int Long, double OosMeanPct, double OosStdPct, double Score, int Trades, double FullReturnPct, bool Valid);

    public sealed record ForgeResult(bool IsValid, IReadOnlyList<ForgeCandidate> Ranked, int Tested, int Folds, string Note);

    public static class StrategyForge
    {
        public static readonly int[] DefaultShorts = { 5, 8, 10, 12, 15, 20 };
        public static readonly int[] DefaultLongs = { 20, 30, 40, 60, 90 };

        public static ForgeResult Run(
            List<Candle> candles, int folds = 4, int minTradesPerFold = 1, double lambda = 0.5,
            int topN = 12, int[]? shorts = null, int[]? longs = null)
        {
            shorts ??= DefaultShorts; longs ??= DefaultLongs;
            if (candles == null || candles.Count < 120)
                return new ForgeResult(false, Array.Empty<ForgeCandidate>(), 0, folds, "캔들 부족(최소 120거래일 필요)");

            folds = Math.Clamp(folds, 2, 8);
            var segs = Split(candles, folds);
            var all = new List<ForgeCandidate>();
            int tested = 0;

            foreach (var s in shorts)
                foreach (var l in longs)
                {
                    if (s >= l) continue;
                    tested++;
                    var oos = new List<double>(folds);
                    int totalTrades = 0; bool ok = true;
                    foreach (var seg in segs)
                    {
                        if (seg.Count < l + 5) { ok = false; break; }
                        var r = BacktestEngine.RunMaCross(seg, Market.KRX_KOSPI, s, l);
                        oos.Add((double)r.TotalReturn);
                        totalTrades += r.TradeCount;
                        if (r.TradeCount < minTradesPerFold) ok = false;
                    }
                    if (!ok || oos.Count < folds) continue;     // 거래/캔들 부족 조합 컷

                    double mean = oos.Average();
                    double std = Math.Sqrt(oos.Select(x => (x - mean) * (x - mean)).Average());
                    double score = mean - lambda * std;          // 일관성 패널티
                    var full = BacktestEngine.RunMaCross(candles, Market.KRX_KOSPI, s, l);
                    all.Add(new ForgeCandidate(s, l, mean, std, score, totalTrades, (double)full.TotalReturn, true));
                }

            var ranked = all.OrderByDescending(c => c.Score).ThenBy(c => c.Short).ThenBy(c => c.Long).Take(Math.Max(1, topN)).ToList();
            return new ForgeResult(ranked.Count > 0, ranked, tested, folds,
                ranked.Count > 0 ? $"{tested}조합 × {folds}폴드 OOS 평가 · 강건 상위 {ranked.Count}" : "유효 전략 없음(거래/캔들 부족)");
        }

        private static List<List<Candle>> Split(List<Candle> c, int k)
        {
            var segs = new List<List<Candle>>(k);
            int n = c.Count, size = n / k;
            for (int i = 0; i < k; i++)
            {
                int start = i * size;
                int len = i == k - 1 ? n - start : size;
                segs.Add(c.GetRange(start, len));
            }
            return segs;
        }
    }
}
