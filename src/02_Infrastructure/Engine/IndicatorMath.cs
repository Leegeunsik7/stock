// ============================================================================
// IndicatorMath - 차트 보조지표 계산 (Blazor/WinForms 공용) (v3.4.2)
//  원본 01_RealtimeWatcher.ChartForm의 계산 룰을 그대로 이식 — 숨은 시드값 보존:
//   · RSI: 워밍업 구간(기간 미만) = 50 · avgL==0 → rs=100 공식 유지
//   · Stochastic: 워밍업 %K = 50 · 범위 0 → 50 · %D 초기 구간 = %K 값
//   · 볼린저 %B: 워밍업 = 0.5 · 밴드폭 0 → 0.5 (표시 클램프 [-0.5, 1.5]는 호출측)
//   · EMA: 첫 값 = data[0], k = 2/(period+1)
//  ⚠️ 백테스트(BacktestEngine)의 RSI는 l==0 → 100 반환으로 공식이 다름 — 섞지 말 것.
// ============================================================================
using System;
using System.Collections.Generic;
using System.Linq;

namespace MS2E.Infrastructure
{
    public static class IndicatorMath
    {
        public static List<decimal> Rsi(List<Candle> candles, int period = 14)
        {
            var result = new List<decimal>();
            var gains = new List<decimal>();
            var losses = new List<decimal>();
            for (int i = 0; i < candles.Count; i++)
            {
                if (i == 0) { gains.Add(0); losses.Add(0); result.Add(50); continue; }
                var diff = candles[i].Close - candles[i - 1].Close;
                gains.Add(Math.Max(0, diff));
                losses.Add(Math.Max(0, -diff));
                if (i < period) { result.Add(50); continue; }
                var avgG = gains.GetRange(i - period + 1, period).Average();
                var avgL = losses.GetRange(i - period + 1, period).Average();
                var rs = avgL == 0 ? 100 : avgG / avgL;   // 원본: 0 분모 → rs=100 (RSI ≈ 99)
                result.Add(100 - 100 / (1 + rs));
            }
            return result;
        }

        public static List<decimal> Ema(List<decimal> data, int period)
        {
            var result = new List<decimal>(data.Count);
            if (data.Count == 0) return result;
            decimal k = 2m / (period + 1);
            result.Add(data[0]);
            for (int i = 1; i < data.Count; i++)
                result.Add(data[i] * k + result[i - 1] * (1 - k));
            return result;
        }

        public static (List<decimal> macd, List<decimal> signal, List<decimal> hist) Macd(
            List<Candle> candles, int fast = 12, int slow = 26, int sig = 9)
        {
            var closes = candles.Select(c => c.Close).ToList();
            var emaF = Ema(closes, fast);
            var emaS = Ema(closes, slow);
            var macd = new List<decimal>();
            for (int i = 0; i < closes.Count; i++) macd.Add(emaF[i] - emaS[i]);
            var signal = Ema(macd, sig);
            var hist = new List<decimal>();
            for (int i = 0; i < macd.Count; i++) hist.Add(macd[i] - signal[i]);
            return (macd, signal, hist);
        }

        public static (List<decimal> k, List<decimal> d) Stochastic(
            List<Candle> candles, int period = 14, int smooth = 3)
        {
            var k = new List<decimal>();
            for (int i = 0; i < candles.Count; i++)
            {
                if (i < period - 1) { k.Add(50); continue; }
                decimal hh = decimal.MinValue, ll = decimal.MaxValue;
                for (int j = i - period + 1; j <= i; j++)
                {
                    if (candles[j].High > hh) hh = candles[j].High;
                    if (candles[j].Low < ll) ll = candles[j].Low;
                }
                var rng = hh - ll;
                k.Add(rng == 0 ? 50 : (candles[i].Close - ll) / rng * 100m);
            }
            // %D = %K의 smooth 기간 단순이동평균 (초기 구간은 %K 그대로 — 원본 룰)
            var d = new List<decimal>();
            for (int i = 0; i < k.Count; i++)
            {
                if (i < smooth - 1) { d.Add(k[i]); continue; }
                decimal sum = 0;
                for (int j = 0; j < smooth; j++) sum += k[i - j];
                d.Add(sum / smooth);
            }
            return (k, d);
        }

        public static List<decimal> BollingerPctB(List<Candle> candles, int period = 20, decimal mult = 2m)
        {
            var closes = candles.Select(c => c.Close).ToList();
            var result = new List<decimal>();
            for (int i = 0; i < closes.Count; i++)
            {
                if (i < period - 1) { result.Add(0.5m); continue; }
                decimal sum = 0;
                for (int j = i - period + 1; j <= i; j++) sum += closes[j];
                var ma = sum / period;
                double varSum = 0;
                for (int j = i - period + 1; j <= i; j++)
                {
                    var diff = (double)(closes[j] - ma);
                    varSum += diff * diff;
                }
                var std = (decimal)Math.Sqrt(varSum / period);
                var upper = ma + mult * std;
                var lower = ma - mult * std;
                var band = upper - lower;
                result.Add(band == 0 ? 0.5m : (closes[i] - lower) / band);
            }
            return result;
        }
    }
}
