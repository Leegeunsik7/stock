// ============================================================================
// StrategyOptimizer - 오버나잇 전략 옵티마이저 🅔 (Blazor/WinForms 공용) (v3.15.0)
//  유휴 시간에 종목별 MA 골든/데드크로스의 단·장기 기간 격자를 전수 백테스트해
//  "그 종목에 가장 잘 맞았던" 파라미터를 발굴한다. 외부 의존성 없는 순수 계산
//  ("Primes 패턴"): BacktestEngine.RunMaCross만 호출.
//
//  설계 노트 / 숨은 룰:
//   · 격자 = short∈{5,8,10,12} × long∈{20,26,30,60}, short<long 조합만 (총 13개)
//   · 점수 = 총수익률(TotalReturn). 동점이면 MDD 작은 쪽, 그래도 동점이면 단기↑
//   · 거래 0건 결과는 "신호 없음"으로 후보 제외(과최적화/무거래 방지)
//   · 캔들 부족(longest long + 2 미만)·전 조합 무거래 → IsValid=false
//   · 결과는 결정적: 동일 캔들 → 동일 우승자 (시드/난수 없음)
//   · 이 모듈은 "발굴"만 한다. 자동 주문/DCA 주입은 호출측이 게이팅(실거래 보호).
// ============================================================================
using System;
using System.Collections.Generic;
using System.Linq;

namespace StockManager.Common
{
    /// <summary>한 (short,long) 조합의 백테스트 점수.</summary>
    public class OptCandidate
    {
        public int ShortP { get; set; }
        public int LongP { get; set; }
        public decimal TotalReturn { get; set; }
        public decimal MaxDrawdown { get; set; }
        public decimal WinRate { get; set; }
        public int TradeCount { get; set; }
        public decimal Sharpe { get; set; }
        public string Label => $"MA{ShortP}/{LongP}";
    }

    /// <summary>설정(Optimizer.Results)에 저장하는 경량 행 — EquityCurve/전체후보 제외.</summary>
    public class OptRow
    {
        public string Symbol { get; set; } = "";
        public string Name { get; set; } = "";
        public bool IsValid { get; set; }
        public int ShortP { get; set; }
        public int LongP { get; set; }
        public decimal TotalReturn { get; set; }
        public decimal MaxDrawdown { get; set; }
        public decimal WinRate { get; set; }
        public int TradeCount { get; set; }
        public decimal Sharpe { get; set; }
        public string Note { get; set; } = "";
        public string Label => IsValid ? $"MA{ShortP}/{LongP}" : "-";
    }

    /// <summary>밤사이 탐색 스냅샷(설정 JSON 1건).</summary>
    public class OptSnapshot
    {
        public string GeneratedAt { get; set; } = "";
        public List<OptRow> Rows { get; set; } = new();
    }

    /// <summary>한 종목의 격자탐색 결과(우승자 + 전체 후보).</summary>
    public class OptResult
    {
        public string Symbol { get; set; } = "";
        public string Name { get; set; } = "";
        public bool IsValid { get; set; }
        public string Note { get; set; } = "";
        public OptCandidate? Best { get; set; }
        public List<OptCandidate> Candidates { get; set; } = new();
        public int CandlesUsed { get; set; }
    }

    public static class StrategyOptimizer
    {
        public static readonly int[] ShortGrid = { 5, 8, 10, 12 };
        public static readonly int[] LongGrid = { 20, 26, 30, 60 };

        /// <summary>유효한 (short&lt;long) 조합 수 — UI 진행률·테스트용.</summary>
        public static int GridSize =>
            ShortGrid.SelectMany(_ => LongGrid).Count() == 0 ? 0 :
            (from s in ShortGrid from l in LongGrid where s < l select 1).Count();

        /// <summary>한 종목 격자탐색. 캔들·마켓을 받아 결정적으로 우승 파라미터를 고른다.</summary>
        public static OptResult Optimize(string symbol, string name, List<Candle> candles, Market market)
        {
            var res = new OptResult { Symbol = symbol, Name = name, CandlesUsed = candles?.Count ?? 0 };
            int maxLong = LongGrid.Max();
            if (candles == null || candles.Count < maxLong + 2)
            {
                res.Note = $"캔들 부족(필요 {maxLong + 2}+, 현재 {candles?.Count ?? 0})";
                return res;
            }

            foreach (var s in ShortGrid)
                foreach (var l in LongGrid)
                {
                    if (s >= l) continue;
                    var bs = BacktestEngine.RunMaCross(candles, market, s, l);
                    res.Candidates.Add(new OptCandidate
                    {
                        ShortP = s, LongP = l,
                        TotalReturn = bs.TotalReturn, MaxDrawdown = bs.MaxDrawdown,
                        WinRate = bs.WinRate, TradeCount = bs.TradeCount, Sharpe = bs.Sharpe
                    });
                }

            // 무거래(신호 없음) 후보 제외 → 과최적화/우연 방지
            var traded = res.Candidates.Where(c => c.TradeCount > 0).ToList();
            if (traded.Count == 0)
            {
                res.Note = "유효 신호 없음(전 조합 무거래)";
                return res;
            }

            // 점수: 총수익률 ↓, 동점 MDD ↑(작은 쪽 우선), 동점 단기 기간 ↓(짧은 쪽 우선)
            res.Best = traded
                .OrderByDescending(c => c.TotalReturn)
                .ThenBy(c => c.MaxDrawdown)
                .ThenBy(c => c.ShortP)
                .First();
            res.IsValid = true;
            res.Note = $"{traded.Count}/{res.Candidates.Count} 조합 거래발생 · 우승 {res.Best.Label} ({res.Best.TotalReturn:+0.0;-0.0}%)";
            return res;
        }

        /// <summary>OptResult → 저장용 경량 행.</summary>
        public static OptRow ToRow(OptResult r) => new()
        {
            Symbol = r.Symbol, Name = r.Name, IsValid = r.IsValid, Note = r.Note,
            ShortP = r.Best?.ShortP ?? 0, LongP = r.Best?.LongP ?? 0,
            TotalReturn = r.Best?.TotalReturn ?? 0, MaxDrawdown = r.Best?.MaxDrawdown ?? 0,
            WinRate = r.Best?.WinRate ?? 0, TradeCount = r.Best?.TradeCount ?? 0, Sharpe = r.Best?.Sharpe ?? 0
        };
    }
}
