// ============================================================================
// TimeMachine - 시점 고정(point-in-time) 리플레이 샌드박스 (Season 4 🅱) (v3.38.0)
//  "2024-03-15에 내 터미널은 뭐라 했을까?" — 그 날짜까지의 데이터만(look-ahead 금지)으로
//  레짐·RSI·옵티마이저 우승값을 재현하고, '빨리감기'로 실제 결과를 드러내 채점한다.
//  자기감사 시스템의 캡스톤. 모든 분석 모듈이 순수(배열 입력)라 as-of 슬라이스만 먹이면 됨.
//  순수·결정론: AsOfSlice·Replay 모두 테스트 가능(네트워크는 페이지가 캔들만 공급).
// ============================================================================
using System;
using System.Collections.Generic;
using System.Linq;

namespace MS2E.Infrastructure
{
    public class TimeMachineResult
    {
        public bool Valid { get; set; }
        public string Note { get; set; } = "";
        public DateTime AsOf { get; set; }
        public decimal AsOfClose { get; set; }
        public int AsOfBars { get; set; }
        public double RsiAsOf { get; set; }
        public double MaGapPct { get; set; }
        public string Regime { get; set; } = "";
        public string OptimizerWinner { get; set; } = "";
        public double OptimizerReturnPct { get; set; }
        public int Stance { get; set; }   // +1 강세 / 0 중립 / -1 약세 (as-of 휴리스틱)
        public string StanceLabel => Stance > 0 ? "강세 우위" : Stance < 0 ? "약세 우위" : "중립";
        public bool Graded { get; set; }
        public double RealizedPct { get; set; }
        public int FwdBars { get; set; }
        public bool Hit { get; set; }
    }

    public static class TimeMachine
    {
        /// <summary>as-of 날짜 이하 캔들만(미래 누수 차단). 순수.</summary>
        public static List<Candle> AsOfSlice(IEnumerable<Candle> candles, DateTime asOf) =>
            (candles ?? Enumerable.Empty<Candle>()).Where(c => c.Date.Date <= asOf.Date)
                .OrderBy(c => c.Date).ToList();

        /// <summary>as-of 시점 분석 재현 + horizon봉 뒤 실제 결과 채점. 순수·결정론.</summary>
        public static TimeMachineResult Replay(List<Candle> candles, DateTime asOf, Market market, int horizonBars = 10, string symbol = "")
        {
            var res = new TimeMachineResult { AsOf = asOf.Date };
            if (candles == null || candles.Count == 0) { res.Note = "캔들이 없습니다."; return res; }
            var full = candles.OrderBy(c => c.Date).ToList();

            int asOfIdx = -1;
            for (int i = 0; i < full.Count; i++) if (full[i].Date.Date <= asOf.Date) asOfIdx = i; else break;
            if (asOfIdx < 29) { res.Note = "그 시점까지 데이터가 부족합니다(30봉+)."; return res; }

            var slice = full.Take(asOfIdx + 1).ToList();
            var closes = slice.Select(c => (double)c.Close).ToList();
            res.AsOfClose = slice[^1].Close;
            res.AsOfBars = slice.Count;
            res.RsiAsOf = Rsi(closes, 14);
            double ma20 = closes.Skip(closes.Count - 20).Average();
            res.MaGapPct = ma20 != 0 ? (closes[^1] - ma20) / ma20 * 100 : 0;

            var reg = RegimeDetector.Analyze(slice.Select(c => c.Close).ToList());
            res.Regime = reg.Describe();

            var opt = StrategyOptimizer.Optimize(symbol, symbol, slice, market);
            if (opt.IsValid && opt.Best != null) { res.OptimizerWinner = opt.Best.Label; res.OptimizerReturnPct = (double)opt.Best.TotalReturn; }

            // as-of 휴리스틱 스탠스
            res.Stance = res.RsiAsOf < 35 || reg.Kind == RegimeKind.MeanShiftUp ? 1
                       : res.RsiAsOf > 65 || reg.Kind == RegimeKind.MeanShiftDown ? -1 : 0;

            // 빨리감기: horizon봉 뒤 실현
            int exit = asOfIdx + Math.Max(1, horizonBars);
            if (exit < full.Count && res.AsOfClose > 0)
            {
                res.RealizedPct = (double)((full[exit].Close - res.AsOfClose) / res.AsOfClose * 100m);
                res.FwdBars = horizonBars;
                res.Graded = true;
                res.Hit = (res.Stance > 0 && res.RealizedPct > 0) || (res.Stance < 0 && res.RealizedPct < 0)
                          || (res.Stance == 0 && Math.Abs(res.RealizedPct) < 3);
            }

            res.Valid = true;
            res.Note = res.Graded
                ? $"{slice.Count}봉(≤{asOf:yyyy-MM-dd}) · +{horizonBars}봉 실현 {res.RealizedPct:+0.0;-0.0}%"
                : $"{slice.Count}봉 · 아직 +{horizonBars}봉이 지나지 않아 채점 불가";
            return res;
        }

        private static double Rsi(List<double> c, int p)
        {
            if (c.Count <= p) return 50;
            double g = 0, l = 0;
            for (int i = c.Count - p; i < c.Count; i++) { var d = c[i] - c[i - 1]; if (d > 0) g += d; else l -= d; }
            if (l == 0) return 100;
            var rs = (g / p) / (l / p);
            return 100 - 100 / (1 + rs);
        }
    }
}
