// ============================================================================
// EventStudyTests - 이벤트 스터디 전수 스캔/분포/엣지/경계 테스트 (순수·결정적)
//  CompositeAlertEngine.Evaluate를 재사용하므로 합성 캔들로 패턴 발생을 유도해 검증.
// ============================================================================
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace StockManager.Tests
{
    public class EventStudyTests
    {
        private static List<Candle> Candles(IEnumerable<double> closes, long vol = 1000)
        {
            var list = new List<Candle>();
            int i = 0;
            foreach (var c in closes)
                list.Add(new Candle { Date = new DateTime(2020, 1, 1).AddDays(i++), Close = (decimal)c, Volume = vol });
            return list;
        }

        private static CompositeRule Rule(CondMetric m, int param, CondOp op, decimal thr) => new()
        {
            Name = "T", Logic = RuleLogic.And,
            Conditions = new() { new CompositeCondition { Metric = m, Param = param, Op = op, Threshold = thr } }
        };

        // 완만한 상승 추세 → "가격 ≥ 0" 같은 항상참 조건으로 전수 발생 유도
        private static List<Candle> Trend(int n, double start = 100, double step = 1.0)
            => Candles(Enumerable.Range(0, n).Select(i => start + step * i));

        [Fact]
        public void AlwaysTrue_Pattern_FiresEveryBar_PositiveEdgeZero()
        {
            // 가격 ≥ 0 (항상 참) → 모든 스캔봉에서 발생 → 조건부=무조건 → 엣지 ≈ 0
            var res = EventStudy.Analyze(Rule(CondMetric.Price, 1, CondOp.Gte, 0), Trend(120), new[] { 5, 10 });
            Assert.True(res.IsValid);
            Assert.True(res.Occurrences > 0);
            Assert.Equal(res.Scanned, res.Occurrences);          // 항상참 → 스캔=발생
            var s5 = res.Stats.Single(s => s.Days == 5);
            Assert.Equal(0m, s5.EdgePct, 1);                     // 조건부 평균 == 베이스라인
            Assert.Equal(100m, s5.WinRatePct, 1);                // 상승추세 → 5일 뒤 항상 +
        }

        [Fact]
        public void NeverTrue_Pattern_ZeroOccurrences_StillValid()
        {
            var res = EventStudy.Analyze(Rule(CondMetric.Price, 1, CondOp.Gte, 1_000_000), Trend(120), new[] { 5 });
            Assert.True(res.IsValid);
            Assert.Equal(0, res.Occurrences);
            Assert.Contains("발생 0회", res.Note);
            Assert.All(res.Stats, s => Assert.Equal(0, s.N));
        }

        [Fact]
        public void ForwardReturns_MatchKnownTrend()
        {
            // step=1, start=100 → +5일 수익률은 시점마다 다르지만 전부 양(+) · 평균>0
            var res = EventStudy.Analyze(Rule(CondMetric.Price, 1, CondOp.Gte, 0), Trend(80), new[] { 5 });
            var s5 = res.Stats.Single(s => s.Days == 5);
            Assert.True(s5.MeanPct > 0);
            Assert.True(s5.P5 <= s5.MedianPct && s5.MedianPct <= s5.P95);   // 백분위 순서
        }

        [Fact]
        public void OversoldRsi_FiresOnDips()
        {
            // 급락 구간을 포함한 시계열에서 RSI<30 패턴이 한 번 이상 발생
            var closes = Enumerable.Repeat(100.0, 20)
                .Concat(Enumerable.Range(1, 15).Select(i => 100.0 - i * 4))   // 급락 → RSI 하락
                .Concat(Enumerable.Range(1, 25).Select(i => 40.0 + i))        // 회복
                .ToList();
            var res = EventStudy.Analyze(Rule(CondMetric.Rsi, 14, CondOp.Lte, 30), Candles(closes), new[] { 5, 10 });
            Assert.True(res.IsValid);
            Assert.True(res.Occurrences >= 1);
        }

        [Fact]
        public void InsufficientCandles_Invalid()
        {
            Assert.False(EventStudy.Analyze(Rule(CondMetric.Price, 1, CondOp.Gte, 0), Candles(new[] { 100.0, 101, 102 }), new[] { 5 }).IsValid);
        }

        [Fact]
        public void EmptyRule_Invalid()
        {
            var empty = new CompositeRule { Name = "E", Conditions = new() };
            Assert.False(EventStudy.Analyze(empty, Trend(120), new[] { 5 }).IsValid);
        }

        [Fact]
        public void Deterministic_SameInput_SameResult()
        {
            var c = Trend(150);
            var r1 = EventStudy.Analyze(Rule(CondMetric.MaGapPct, 20, CondOp.Gte, 0), c, new[] { 5, 10, 20 });
            var r2 = EventStudy.Analyze(Rule(CondMetric.MaGapPct, 20, CondOp.Gte, 0), c, new[] { 5, 10, 20 });
            Assert.Equal(r1.Occurrences, r2.Occurrences);
            Assert.Equal(r1.Stats.Single(s => s.Days == 10).MeanPct, r2.Stats.Single(s => s.Days == 10).MeanPct, 9);
        }
    }
}
