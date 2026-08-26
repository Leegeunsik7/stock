// ============================================================================
// HealthCheckTests - 종합 건강검진 점수 모듈 검증 (순수·결정론·네트워크 0)
//  점수·등급·권고 + 정직성 가드(데이터 부족 시 중립·등급 보류, 거짓 고득점 방지).
// ============================================================================
using System.Linq;
using Xunit;

namespace StockManager.Tests
{
    public class HealthCheckTests
    {
        // 건강한 포트: 분산 좋음·위기 얕음·손실확률 낮음·편향 없음 (4축 실측)
        private static HealthInputs Healthy() => new(
            Holdings: 10, PricedHoldings: 10, AvgCorrelation: 0.2, TopRiskConcentrationPct: 14, EvenSharePct: 10,
            WorstCrisisMddPct: -20, ProbLossPct: 20, BiasWarnings: 0,
            HasRisk: true, HasCrisis: true, HasFuture: true, HasBehavior: true);

        // 취약 포트: 상관 높음·집중 심함·위기 깊음·손실확률 높음·편향 많음
        private static HealthInputs Fragile() => new(
            Holdings: 2, PricedHoldings: 2, AvgCorrelation: 0.95, TopRiskConcentrationPct: 80, EvenSharePct: 50,
            WorstCrisisMddPct: -55, ProbLossPct: 60, BiasWarnings: 4,
            HasRisk: true, HasCrisis: true, HasFuture: true, HasBehavior: true);

        [Fact]
        public void Healthy_ScoresHigh_GoodGrade()
        {
            var r = HealthCheck.Compute(Healthy());
            Assert.True(r.Overall >= 70, $"overall={r.Overall}");
            Assert.Contains(r.Grade, new[] { "A", "B" });
            Assert.Equal(4, r.RealAxes);
            Assert.All(r.Subs, s => Assert.InRange(s.Score, 0, 100));
        }

        [Fact]
        public void Fragile_ScoresLow_AndRecommends()
        {
            var r = HealthCheck.Compute(Fragile());
            Assert.True(r.Overall < 50, $"overall={r.Overall}");
            Assert.Contains(r.Grade, new[] { "C", "D", "F" });
            Assert.True(r.Recommendations.Count >= 2);
            Assert.DoesNotContain(r.Recommendations, x => x.Contains("뚜렷한 취약 신호가 없"));
        }

        [Fact]
        public void Healthy_HasNoWeaknessRecommendation()
        {
            var r = HealthCheck.Compute(Healthy());
            Assert.Single(r.Recommendations);
            Assert.Contains("뚜렷한 취약 신호가 없", r.Recommendations[0]);
        }

        [Fact]
        public void SingleHolding_DiversificationCapped()
        {
            // 1종목: 분산은 '집중'으로 알려진 저득점 20(중립 아님)
            var r = HealthCheck.Compute(new HealthInputs(1, 1, 0, 0, 100, -10, 10, 0, false, true, true, false));
            var div = r.Subs.First(s => s.Key == "diversification");
            Assert.Equal(20, div.Score, 3);
            Assert.False(div.IsNeutral);
        }

        [Fact]
        public void UnpricedMultiHolding_DiversificationNeutral_NotFakeHigh()
        {
            // 5종목 보유, 시세 1종목만 → 분산 측정 불가 → 중립 50(거짓 고득점 91 아님)
            var r = HealthCheck.Compute(new HealthInputs(5, 1, 0, 0, 100, -10, 10, 0, false, true, true, false));
            var div = r.Subs.First(s => s.Key == "diversification");
            Assert.Equal(50, div.Score, 3);
            Assert.True(div.IsNeutral);
        }

        [Fact]
        public void MissingAxes_UseNeutral50_AndMarkNeutral()
        {
            // 위기/미래/행동 데이터 없음 → 각 중립 50(70 아님)·IsNeutral
            var r = HealthCheck.Compute(new HealthInputs(5, 5, 0.3, 25, 20, 0, 0, 0, true, false, false, false));
            foreach (var key in new[] { "crisis", "future", "behavior" })
            {
                var s = r.Subs.First(x => x.Key == key);
                Assert.Equal(50, s.Score, 3);
                Assert.True(s.IsNeutral);
            }
        }

        [Fact]
        public void DataStarved_NoLetterGrade()
        {
            // 실측 축 < 2 → 문자 등급 없음('—')
            var r = HealthCheck.Compute(new HealthInputs(3, 0, 0, 0, 100, 0, 0, 0, false, false, false, false));
            Assert.Equal("—", r.Grade);
            Assert.True(r.RealAxes < 2);
            Assert.Contains(r.Recommendations, x => x.Contains("데이터가 부족"));
        }

        [Fact]
        public void NeutralAxes_ExcludedFromOverall()
        {
            // 실측 분산 90 + 중립 3축 → 종합은 90(중립 50들이 끌어내리지 않음)
            var r = HealthCheck.Compute(new HealthInputs(8, 8, 0.1, 13, 12.5, 0, 0, 0, true, false, false, false));
            var div = r.Subs.First(s => s.Key == "diversification");
            Assert.False(div.IsNeutral);
            Assert.Equal(div.Score, r.Overall, 1);   // 실측 1축만 평균
        }

        [Fact]
        public void Deterministic()
        {
            var a = HealthCheck.Compute(Healthy());
            var b = HealthCheck.Compute(Healthy());
            Assert.Equal(a.Overall, b.Overall, 9);
            Assert.Equal(a.Grade, b.Grade);
        }

        [Fact]
        public void Scores_ClampedToRange()
        {
            var r = HealthCheck.Compute(new HealthInputs(50, 50, -5, -50, 2, 30, -30, 99, true, true, true, true));
            Assert.InRange(r.Overall, 0, 100);
            Assert.All(r.Subs, s => Assert.InRange(s.Score, 0, 100));
        }
    }
}
