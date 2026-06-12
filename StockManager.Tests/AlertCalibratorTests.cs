// ============================================================================
// AlertCalibratorTests - 알림 신뢰도 채점 정밀도/집계/경계 테스트 (순수·결정적)
// ============================================================================
using System;
using System.Collections.Generic;
using System.Linq;
using StockManager.Common;
using Xunit;

namespace StockManager.Tests
{
    public class AlertCalibratorTests
    {
        private static List<Candle> Candles(params double[] closes)
        {
            var list = new List<Candle>();
            for (int i = 0; i < closes.Length; i++)
                list.Add(new Candle { Date = new DateTime(2025, 1, 1).AddDays(i), Close = (decimal)closes[i] });
            return list;
        }
        private static DateTime D(int i) => new DateTime(2025, 1, 1).AddDays(i);

        [Fact]
        public void Hit_WhenBigMoveFollows()
        {
            // day0 알림 → +5일 종가 100→110 (+10% ≥ 3%) → 적중
            var cs = Candles(100, 100, 100, 100, 100, 110, 110);
            var alerts = new[] { new AlertRecord(D(0), "가격", "AAA") };
            var rep = AlertCalibrator.Score(alerts, new Dictionary<string, List<Candle>> { ["AAA"] = cs }, 5, 3.0);
            Assert.True(rep.IsValid);
            var g = rep.Grades.Single();
            Assert.Equal(1, g.Evaluable);
            Assert.Equal(1, g.Hits);
            Assert.Equal(100, g.HitRatePct, 1);
        }

        [Fact]
        public void Miss_WhenFlat()
        {
            var cs = Candles(100, 100, 100, 100, 100, 100.5, 100);
            var alerts = new[] { new AlertRecord(D(0), "가격", "AAA") };
            var rep = AlertCalibrator.Score(alerts, new Dictionary<string, List<Candle>> { ["AAA"] = cs }, 5, 3.0);
            var g = rep.Grades.Single();
            Assert.Equal(1, g.Evaluable);
            Assert.Equal(0, g.Hits);
        }

        [Fact]
        public void NotEvaluable_WhenTooRecent()
        {
            // horizon 5인데 알림 후 캔들이 2개뿐 → 평가 불가
            var cs = Candles(100, 101, 102);
            var alerts = new[] { new AlertRecord(D(0), "가격", "AAA") };
            var rep = AlertCalibrator.Score(alerts, new Dictionary<string, List<Candle>> { ["AAA"] = cs }, 5, 3.0);
            Assert.Equal(1, rep.Total);
            Assert.Equal(0, rep.Evaluable);
            Assert.Equal(1, rep.Grades.Single().Count);
        }

        [Fact]
        public void NotEvaluable_WhenNoSymbolOrCandles()
        {
            var alerts = new[]
            {
                new AlertRecord(D(0), "시스템", ""),                       // 심볼 없음
                new AlertRecord(D(0), "가격", "ZZZ"),                      // 캔들 없음
            };
            var rep = AlertCalibrator.Score(alerts, new Dictionary<string, List<Candle>>(), 5, 3.0);
            Assert.Equal(2, rep.Total);
            Assert.Equal(0, rep.Evaluable);
        }

        [Fact]
        public void PerCategory_Aggregation_AndOverall()
        {
            var up = Candles(100, 100, 100, 100, 100, 120, 120);     // +20% → hit
            var flat = Candles(100, 100, 100, 100, 100, 100, 100);   // 0% → miss
            var alerts = new[]
            {
                new AlertRecord(D(0), "시그널", "UP"),
                new AlertRecord(D(0), "시그널", "FLAT"),
                new AlertRecord(D(0), "뉴스", "FLAT"),
            };
            var map = new Dictionary<string, List<Candle>> { ["UP"] = up, ["FLAT"] = flat };
            var rep = AlertCalibrator.Score(alerts, map, 5, 3.0);
            var sig = rep.Grades.Single(g => g.Category == "시그널");
            Assert.Equal(2, sig.Evaluable);
            Assert.Equal(1, sig.Hits);
            Assert.Equal(50, sig.HitRatePct, 1);
            Assert.Equal(3, rep.Evaluable);
            Assert.Equal(1.0 / 3 * 100, rep.OverallHitRatePct, 1);   // 3건 중 UP만 hit → 1/3 ≈ 33%
        }

        [Fact]
        public void Recommendation_SampleTooSmall()
        {
            var cs = Candles(100, 100, 100, 100, 100, 120, 120);
            var alerts = new[] { new AlertRecord(D(0), "가격", "AAA") };
            var rep = AlertCalibrator.Score(alerts, new Dictionary<string, List<Candle>> { ["AAA"] = cs }, 5, 3.0);
            Assert.Equal("표본 부족", rep.Grades.Single().Recommendation);   // 평가 1건 < 5
        }

        [Fact]
        public void Empty_Invalid()
        {
            var rep = AlertCalibrator.Score(Array.Empty<AlertRecord>(), new Dictionary<string, List<Candle>>(), 5, 3.0);
            Assert.False(rep.IsValid);
            Assert.Equal(0, rep.Total);
        }

        [Fact]
        public void Deterministic_SameInput_SameReport()
        {
            var cs = Candles(100, 102, 98, 105, 95, 130, 120);
            var alerts = new[] { new AlertRecord(D(0), "가격", "A"), new AlertRecord(D(1), "가격", "A") };
            var map = new Dictionary<string, List<Candle>> { ["A"] = cs };
            var a = AlertCalibrator.Score(alerts, map, 5, 3.0);
            var b = AlertCalibrator.Score(alerts, map, 5, 3.0);
            Assert.Equal(a.OverallHitRatePct, b.OverallHitRatePct, 9);
            Assert.Equal(a.Grades.Single().HitRatePct, b.Grades.Single().HitRatePct, 9);
        }
    }
}
