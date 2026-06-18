// ============================================================================
// AlphaEngineTests — OLS Beta·알파 분해·Inner Join·결정론 검증
//  외부 의존 0, 결정론 합성 캔들, 네트워크 호출 없음
// ============================================================================
using System;
using System.Collections.Generic;
using System.Linq;
using StockManager.Common;
using Xunit;

namespace StockManager.Tests
{
    public class AlphaEngineTests
    {
        // ── 반환 배열 → Candle 시퀀스 변환 헬퍼 ──
        private static List<Candle> FromReturns(double[] returns, DateTime start)
        {
            var list = new List<Candle>(returns.Length + 1);
            decimal close = 100m;
            list.Add(new Candle { Date = start, Open = close, High = close, Low = close, Close = close, Volume = 1000 });
            foreach (var r in returns)
            {
                close = Math.Max(0.01m, Math.Round(close * (decimal)(1.0 + r), 6));
                list.Add(new Candle { Date = start.AddDays(list.Count), Open = close, High = close, Low = close, Close = close, Volume = 1000 });
            }
            return list;
        }

        // 60쌍 정합 거래일이 보장된 기본 시장 수익률
        private static double[] BaseRm60()
        {
            var base_ = new double[] { 0.005, 0.01, -0.005, 0.015, -0.01,
                                       0.005, 0.01, -0.005, 0.015, -0.01 };
            var arr = new double[60];
            for (int i = 0; i < 60; i++) arr[i] = base_[i % base_.Length];
            return arr;
        }

        private static readonly DateTime T0 = new(2022, 1, 3);

        // ─────────────────────────────────────────────────────────────────
        [Fact]
        public void Compute_TooFewDays_ReturnsInvalid()
        {
            var rm = BaseRm60()[..20];   // 20 returns → 20 aligned days < 30
            var r = AlphaEngine.Compute("AAPL",
                FromReturns(rm, T0), FromReturns(rm, T0), "SPY");
            Assert.False(r.IsValid);
            Assert.Empty(r.Series);
        }

        [Fact]
        public void Compute_BetaFormula_CorrectOLS()
        {
            // rp = 2 × rm → beta 정확히 2.0
            var rm = BaseRm60();
            var rp = rm.Select(x => x * 2.0).ToArray();
            var r = AlphaEngine.Compute("X",
                FromReturns(rp, T0), FromReturns(rm, T0), "BM");
            Assert.True(r.IsValid);
            Assert.Equal(2.0, r.Beta, precision: 4);
        }

        [Fact]
        public void Compute_IdenticalToMarket_BetaOneAlphaNearZero()
        {
            var rm = BaseRm60();
            var r = AlphaEngine.Compute("X",
                FromReturns(rm, T0), FromReturns(rm, T0), "BM");
            Assert.True(r.IsValid);
            Assert.Equal(1.0, r.Beta, precision: 4);
            // 시장과 동일 → 잔여 알파 ≈ 0 (무위험이율 기여 제외 허용)
            Assert.True(Math.Abs(r.TrueAlphaPct) < 1.0,
                $"TrueAlphaPct={r.TrueAlphaPct:0.00}% expected ≈ 0");
        }

        [Fact]
        public void Compute_MarketUncorrelated_BetaNearZero()
        {
            // rp = const drift, rm = alternating sign → 무상관 → beta ≈ 0
            var rm = BaseRm60();
            var rp = Enumerable.Range(0, 60).Select(_ => 0.001).ToArray(); // flat
            var r = AlphaEngine.Compute("FLAT",
                FromReturns(rp, T0), FromReturns(rm, T0), "BM");
            Assert.True(r.IsValid);
            Assert.True(Math.Abs(r.Beta) < 0.1, $"Beta={r.Beta} not near 0");
        }

        [Fact]
        public void Compute_R2_BoundedZeroToHundred()
        {
            var rm = BaseRm60();
            var rp = rm.Select(x => x * 1.5).ToArray();
            var r = AlphaEngine.Compute("X",
                FromReturns(rp, T0), FromReturns(rm, T0), "BM");
            Assert.True(r.IsValid);
            Assert.InRange(r.R2Pct, 0.0, 100.0);
        }

        [Fact]
        public void Compute_R2_PerfectCorrelation_NearHundred()
        {
            // rp = k*rm → 완전 상관 → R² ≈ 100%
            var rm = BaseRm60();
            var rp = rm.Select(x => x * 3.0).ToArray();
            var r = AlphaEngine.Compute("X",
                FromReturns(rp, T0), FromReturns(rm, T0), "BM");
            Assert.True(r.IsValid);
            Assert.True(r.R2Pct > 99.0, $"R²={r.R2Pct:0.0}% expected ≈ 100%");
        }

        [Fact]
        public void Compute_TrueAlphaPlusMarketAttr_EqualsTotalReturn()
        {
            var rm = BaseRm60();
            var rp = rm.Select((x, i) => x * 1.2 + (i % 3 == 0 ? 0.002 : 0)).ToArray();
            var r = AlphaEngine.Compute("X",
                FromReturns(rp, T0), FromReturns(rm, T0), "BM");
            Assert.True(r.IsValid);
            // TrueAlpha + MarketAttributed = TotalReturn (소수점 오차 허용)
            Assert.Equal(r.TotalReturnPct,
                r.TrueAlphaPct + r.MarketAttributedPct, precision: 5);
        }

        [Fact]
        public void Compute_Deterministic_SameResult()
        {
            var rm = BaseRm60();
            var rp = rm.Select(x => x * 1.3 + 0.001).ToArray();
            var r1 = AlphaEngine.Compute("X", FromReturns(rp, T0), FromReturns(rm, T0), "BM");
            var r2 = AlphaEngine.Compute("X", FromReturns(rp, T0), FromReturns(rm, T0), "BM");
            Assert.True(r1.IsValid);
            Assert.Equal(r1.Beta,              r2.Beta);
            Assert.Equal(r1.TrueAlphaPct,      r2.TrueAlphaPct);
            Assert.Equal(r1.InformationRatio,  r2.InformationRatio);
            Assert.Equal(r1.AlignedDays,       r2.AlignedDays);
        }

        [Fact]
        public void Compute_DateMismatch_InnerJoinOnly()
        {
            // portfolio 캔들에 벤치마크에 없는 날짜 10개 추가
            var rm = BaseRm60();
            var pfCandles = FromReturns(rm, T0);
            // 벤치마크는 마지막 10일 없는 짧은 버전
            var bmCandles = FromReturns(rm[..50], T0);
            var r = AlphaEngine.Compute("X", pfCandles, bmCandles, "BM");
            Assert.True(r.IsValid);
            Assert.Equal(50, r.AlignedDays);   // Inner Join → 50일만 사용
        }

        [Fact]
        public void Compute_ZeroClose_Skipped()
        {
            var rm = BaseRm60();
            var pfCandles = FromReturns(rm, T0);
            pfCandles[1].Close = 0m;   // prev=0 → 다음 봉 수익률 skip
            var r = AlphaEngine.Compute("X", pfCandles, FromReturns(rm, T0), "BM");
            Assert.True(r.IsValid);
            // Close=0 이 있어도 예외 없이 처리되어야 함 (정합일 ≥ 30)
        }

        [Fact]
        public void Compute_CumulativeSeries_CompoundingCorrect()
        {
            // 매일 1% 수익 → 60일 누적 = (1.01^60 - 1) * 100%
            var rm = Enumerable.Repeat(0.0, 60).ToArray();  // flat benchmark
            var rp = Enumerable.Repeat(0.01, 60).ToArray();
            var r = AlphaEngine.Compute("X", FromReturns(rp, T0), FromReturns(rm, T0), "BM");
            Assert.True(r.IsValid);
            double expected = (Math.Pow(1.01, 60) - 1.0) * 100.0;
            Assert.Equal(expected, r.Series[^1].CumActual, precision: 4);
        }
    }
}
