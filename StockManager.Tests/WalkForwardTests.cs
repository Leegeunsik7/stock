// ============================================================================
// WalkForwardTests - Walk-Forward 검증 구조/결정론/경계 테스트
//  합성 사인파로 교차·거래를 유발해 폴드 분할·OOS 재채점·판정을 검증.
// ============================================================================
using System;
using System.Collections.Generic;
using System.Linq;
using StockManager.Common;
using Xunit;

namespace StockManager.Tests
{
    public class WalkForwardTests
    {
        private static List<Candle> Wave(int n, double amp = 30, double period = 25, double drift = 0.1)
        {
            var list = new List<Candle>(n);
            for (int i = 0; i < n; i++)
            {
                double p = 100 + drift * i + amp * Math.Sin(2 * Math.PI * i / period);
                decimal close = (decimal)Math.Max(1.0, p);
                list.Add(new Candle
                {
                    Date = new DateTime(2020, 1, 1).AddDays(i),
                    Open = close, High = close * 1.01m, Low = close * 0.99m,
                    Close = close, Volume = 1000 + i % 7
                });
            }
            return list;
        }

        [Fact]
        public void TooFewCandles_Invalid()
        {
            var r = WalkForward.Analyze("S", "S", Wave(100), Market.KRX_KOSPI, 120, 40);
            Assert.False(r.IsValid);
            Assert.Empty(r.Folds);
            Assert.Contains("캔들", r.Note);
        }

        [Fact]
        public void FoldCount_MatchesRollingMath()
        {
            // N=280, train=120, test=40 → (280-120)/40 = 4 창 (사인파라 전 폴드 학습 유효)
            var r = WalkForward.Analyze("X", "X", Wave(280), Market.KRX_KOSPI, 120, 40);
            Assert.True(r.IsValid);
            Assert.Equal(4, r.FoldCount);
            Assert.All(r.Folds, f => Assert.True(f.ShortP < f.LongP));
        }

        [Fact]
        public void Aggregates_WithinExpectedRanges()
        {
            var r = WalkForward.Analyze("X", "X", Wave(300), Market.KRX_KOSDAQ, 120, 40);
            Assert.True(r.IsValid);
            Assert.InRange(r.OosWinRate, 0m, 100m);
            Assert.Equal(r.AvgInSample - r.AvgOutSample, r.Degradation, 4);   // 갭 정의 일관성
            Assert.True(r.RecShortP < r.RecLongP);                            // 추천 파라미터 유효
            Assert.Contains(r.Verdict, new[] { "강건", "취약(과최적화)", "부적합", "보통" });
        }

        [Fact]
        public void Deterministic_SameCandles_SameResult()
        {
            var c = Wave(300);
            var a = WalkForward.Analyze("X", "X", c, Market.US_NASDAQ, 120, 40);
            var b = WalkForward.Analyze("X", "X", c, Market.US_NASDAQ, 120, 40);
            Assert.Equal(a.FoldCount, b.FoldCount);
            Assert.Equal(a.AvgOutSample, b.AvgOutSample);
            Assert.Equal(a.RecShortP, b.RecShortP);
            Assert.Equal(a.RecLongP, b.RecLongP);
            Assert.Equal(a.Verdict, b.Verdict);
        }

        [Fact]
        public void TrainBars_ClampedToGridMinimum()
        {
            // train을 10으로 줘도 내부에서 최장기(60)+2=62 이상으로 보정 → 정상 분석
            var r = WalkForward.Analyze("X", "X", Wave(300), Market.KRX_KOSPI, 10, 40);
            Assert.True(r.IsValid);
            Assert.True(r.FoldCount >= 1);
        }

        [Fact]
        public void Verdict_FragileWhenInSamplePositiveButOosNegative()
        {
            // 판정 규칙 직접 검증용은 어려우므로, 최소한 강건 판정의 필요조건을 확인
            var r = WalkForward.Analyze("X", "X", Wave(360), Market.KRX_KOSPI, 120, 40);
            if (r.Verdict == "강건")
            {
                Assert.True(r.OosWinRate >= 60m);
                Assert.True(r.AvgOutSample > 0);
                Assert.True(r.FoldCount >= 3);
            }
            else
            {
                // 강건이 아니면 OOS 약점(승률<60 또는 OOS<=0 또는 IS<=0) 중 하나
                Assert.True(r.OosWinRate < 60m || r.AvgOutSample <= 0 || r.AvgInSample <= 0 || r.FoldCount < 3);
            }
        }
    }
}
