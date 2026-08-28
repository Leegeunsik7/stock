// ============================================================================
// BacktestEngine - 전략 백테스팅 엔진 (Blazor/WinForms 공용) (v3.4.2)
//  원본 05_SignalEngine.MainForm.RunStrategyWithCost 이식 — 숨은 룰 보존:
//   · 슬리피지 0.1%를 "불리한 방향"으로 적용 (매수 +, 매도 −)
//   · 수수료 양방향 (KR 0.015% · US 건당 $0.99 정액), 매도세는 매도 시만
//     (KOSPI 0.18% / KOSDAQ 0.23% / US 0) — TradingCost.Default
//   · 기간 종료 시 강제 청산 + Equity Curve 마지막 점 덮어쓰기
//   · MDD: running-max 방식 / Sharpe: 거래 2건 미만 → 0, ×√252 연환산
//   · 백테스트용 RSI는 l==0 → 100 (차트 RSI와 공식 다름 — 원본 그대로)
//  전략 인덱스: 0 이평 골든/데드크로스 · 1 RSI 역추세 · 2 볼린저밴드 돌파
//              3 MACD 시그널 · 4 볼륨 스파이크 추세
// ============================================================================
using System;
using System.Collections.Generic;
using System.Linq;

namespace MS2E.Infrastructure
{
    public class BacktestSummary : MS2E.UI.Portable.Ports.Strategy.IBacktestSummaryView
    {
        public decimal TotalReturn { get; set; }
        public decimal MaxDrawdown { get; set; }
        public decimal WinRate { get; set; }
        public int TradeCount { get; set; }
        public decimal Sharpe { get; set; }
        public List<decimal> EquityCurve { get; set; } = new();

        // Phase 94-F: 읽기 전용 계약 — List<decimal> → IReadOnlyList<decimal>.
        IReadOnlyList<decimal> MS2E.UI.Portable.Ports.Strategy.IBacktestSummaryView.EquityCurve => EquityCurve;
    }

    public static class BacktestEngine
    {
        public static readonly string[] StrategyNames =
        {
            "이동평균 골든/데드크로스", "RSI 역추세", "볼린저밴드 돌파",
            "MACD 시그널", "볼륨 스파이크 추세"
        };

        /// <summary>시드 1,000,000원 · 수수료/세금/슬리피지 반영 백테스트 (캔들 30개 이상 권장).</summary>
        public static BacktestSummary Run(List<Candle> candles, int strategy, Market market)
        {
            var closes = candles.Select(c => c.Close).ToList();
            var ema12 = EmaSeries(closes, 12);
            var ema26 = EmaSeries(closes, 26);
            return RunCore(candles, market, i => GetSignal(candles, closes, ema12, ema26, i, strategy));
        }

        /// <summary>MA 골든/데드크로스 — 단·장기 기간을 파라미터화(옵티마이저 격자탐색용).</summary>
        public static BacktestSummary RunMaCross(List<Candle> candles, Market market, int shortP, int longP)
        {
            if (shortP < 1 || longP <= shortP) return new BacktestSummary();
            var closes = candles.Select(c => c.Close).ToList();
            return RunCore(candles, market, i => MaCrossSig(closes, i, shortP, longP), warmup: longP + 1);
        }

        private static int MaCrossSig(List<decimal> closes, int i, int s, int l)
        {
            if (i < l) return 0;
            var ms = Avg(closes, i - s + 1, s);
            var ml = Avg(closes, i - l + 1, l);
            var msP = Avg(closes, i - s, s);
            var mlP = Avg(closes, i - l, l);
            if (msP < mlP && ms > ml) return 1;
            if (msP > mlP && ms < ml) return -1;
            return 0;
        }

        // 공통 시뮬 코어 — 신호 함수만 주입(Run/RunMaCross 공유). 비용모델·강제청산·MDD/Sharpe 동일.
        private static BacktestSummary RunCore(List<Candle> candles, Market market, Func<int, int> signalAt, int warmup = 30)
        {
            var cost = TradingCost.Default;
            decimal equity = 1_000_000m, initial = equity;
            int qty = 0;
            decimal entryPrice = 0;
            var equityCurve = new List<decimal>();
            var tradeReturns = new List<decimal>();

            for (int i = warmup; i < candles.Count; i++)
            {
                var sig = signalAt(i);
                var price = candles[i].Close;
                var execPrice = sig switch
                {
                    1 => price * (1 + cost.SlippageRate),
                    -1 => price * (1 - cost.SlippageRate),
                    _ => price
                };

                if (sig == 1 && qty == 0)
                {
                    qty = (int)(equity / execPrice);
                    var amount = qty * execPrice;
                    equity -= amount + cost.Commission(market, amount);
                    entryPrice = execPrice;
                }
                else if (sig == -1 && qty > 0)
                {
                    var amount = qty * execPrice;
                    equity += amount - cost.Commission(market, amount) - cost.SellTax(market, amount);
                    if (entryPrice > 0) tradeReturns.Add((execPrice - entryPrice) / entryPrice * 100m);
                    qty = 0;
                }

                equityCurve.Add(equity + qty * price);
            }

            if (qty > 0 && candles.Count > 0 && equityCurve.Count > 0)   // 마감 강제 청산 + 마지막 점 덮어쓰기
            {
                var lastP = candles.Last().Close * (1 - cost.SlippageRate);
                var amount = qty * lastP;
                equity += amount - cost.Commission(market, amount) - cost.SellTax(market, amount);
                if (entryPrice > 0) tradeReturns.Add((lastP - entryPrice) / entryPrice * 100m);
                equityCurve[^1] = equity;
            }

            var totalReturn = (equity - initial) / initial * 100m;
            decimal runningMax = equityCurve.Count > 0 ? equityCurve[0] : initial, maxDD = 0m;
            foreach (var e in equityCurve)
            {
                if (e > runningMax) runningMax = e;
                if (runningMax == 0) continue;
                var dd = (runningMax - e) / runningMax * 100m;
                if (dd > maxDD) maxDD = dd;
            }

            return new BacktestSummary
            {
                TotalReturn = totalReturn,
                MaxDrawdown = maxDD,
                WinRate = tradeReturns.Count == 0 ? 0 :
                    (decimal)tradeReturns.Count(x => x > 0) / tradeReturns.Count * 100m,
                TradeCount = tradeReturns.Count,
                Sharpe = tradeReturns.Count < 2 ? 0 :
                    (decimal)(((double)tradeReturns.Average()) /
                        Math.Max(0.0001, StdDev(tradeReturns.Select(x => (double)x).ToList()))) * (decimal)Math.Sqrt(252),
                EquityCurve = equityCurve
            };
        }

        private static int GetSignal(List<Candle> candles, List<decimal> closes,
            List<decimal> ema12, List<decimal> ema26, int i, int strategy)
        {
            switch (strategy)
            {
                case 0:  // 골든/데드크로스
                    var ma5 = Avg(closes, i - 4, 5);
                    var ma20 = Avg(closes, i - 19, 20);
                    var ma5P = Avg(closes, i - 5, 5);
                    var ma20P = Avg(closes, i - 20, 20);
                    if (ma5P < ma20P && ma5 > ma20) return 1;
                    if (ma5P > ma20P && ma5 < ma20) return -1;
                    return 0;
                case 1:  // RSI 역추세
                    var rsi = CalcRsi(closes, i, 14);
                    if (rsi < 30) return 1;
                    if (rsi > 70) return -1;
                    return 0;
                case 2:  // 볼린저밴드 돌파
                    var ma = Avg(closes, i - 19, 20);
                    double variance = 0;
                    for (int j = i - 19; j <= i; j++)
                    {
                        var diff = (double)(closes[j] - ma);
                        variance += diff * diff;
                    }
                    variance /= 20;
                    var std = (decimal)Math.Sqrt(variance);
                    if (candles[i].Close < ma - 2 * std) return 1;
                    if (candles[i].Close > ma + 2 * std) return -1;
                    return 0;
                case 3:  // MACD 시그널 (사전계산 EMA)
                    var macd = ema12[i] - ema26[i];
                    var macdP = ema12[i - 1] - ema26[i - 1];
                    if (macdP < 0 && macd > 0) return 1;
                    if (macdP > 0 && macd < 0) return -1;
                    return 0;
                case 4:  // 볼륨 스파이크 추세
                    decimal volSum = 0, high20 = decimal.MinValue, low20 = decimal.MaxValue;
                    for (int j = i - 20; j < i; j++)
                    {
                        volSum += candles[j].Volume;
                        if (candles[j].High > high20) high20 = candles[j].High;
                        if (candles[j].Low < low20) low20 = candles[j].Low;
                    }
                    var avgVol = volSum / 20m;
                    if (candles[i].Volume > avgVol * 2 && candles[i].Close > high20) return 1;
                    if (candles[i].Volume > avgVol * 2 && candles[i].Close < low20) return -1;
                    return 0;
            }
            return 0;
        }

        private static decimal Avg(List<decimal> data, int start, int count)
        {
            if (start < 0) { count += start; start = 0; }   // 음수 시작 클램프 (원본)
            if (count <= 0) return 0;
            decimal sum = 0;
            for (int j = start; j < start + count && j < data.Count; j++) sum += data[j];
            return sum / count;
        }

        private static List<decimal> EmaSeries(List<decimal> data, int period)
        {
            var result = new List<decimal>(data.Count);
            if (data.Count == 0) return result;
            decimal k = 2m / (period + 1);
            result.Add(data[0]);
            for (int i = 1; i < data.Count; i++)
                result.Add(data[i] * k + result[i - 1] * (1 - k));
            return result;
        }

        private static decimal CalcRsi(List<decimal> closes, int i, int p)
        {
            decimal g = 0, l = 0;
            for (int j = i - p + 1; j <= i; j++)
            {
                var d = closes[j] - closes[j - 1];
                if (d > 0) g += d; else l -= d;
            }
            if (l == 0) return 100;   // 백테스트 RSI: 0 분모 → 100 (차트 RSI와 다름 — 원본 그대로)
            var rs = g / p / (l / p);
            return 100 - 100 / (1 + rs);
        }

        private static double StdDev(List<double> v)
        {
            if (v.Count < 2) return 0;
            var avg = v.Average();
            return Math.Sqrt(v.Select(x => (x - avg) * (x - avg)).Sum() / v.Count);
        }
    }
}
