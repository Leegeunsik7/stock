// ============================================================================
// AlertPolicyTests - 알림 발동 판정(AlertPolicy) 회귀 테스트
//  코인 지정가 교차 / 주식 규칙 조건 / 5% 급변 (경계 포함).
// ============================================================================
using StockManager.Common;
using Xunit;

namespace StockManager.Tests
{
    public class AlertPolicyTests
    {
        [Theory]
        [InlineData(true, 1000, 1000, true)]    // 이상: 경계 포함
        [InlineData(true, 1000, 1001, true)]
        [InlineData(true, 1000, 999, false)]
        [InlineData(false, 1000, 1000, true)]   // 이하: 경계 포함
        [InlineData(false, 1000, 999, true)]
        [InlineData(false, 1000, 1001, false)]
        public void CoinCross(bool above, decimal target, decimal price, bool expected)
            => Assert.Equal(expected, AlertPolicy.CoinCross(above, target, price));

        [Theory]
        [InlineData(AlertCondition.PriceAbove, 70000, 71000, 0, true)]
        [InlineData(AlertCondition.PriceAbove, 70000, 69000, 0, false)]
        [InlineData(AlertCondition.PriceBelow, 70000, 69000, 0, true)]
        [InlineData(AlertCondition.ChangeRateAbove, 5, 0, 6, true)]
        [InlineData(AlertCondition.ChangeRateAbove, 5, 0, 4, false)]
        [InlineData(AlertCondition.ChangeRateBelow, -5, 0, -6, true)]
        [InlineData(AlertCondition.GoldenCross, 0, 100, 0, false)]   // 미지원 조건 → false
        public void StockRuleHit(AlertCondition cond, decimal target, decimal price, decimal rate, bool expected)
            => Assert.Equal(expected, AlertPolicy.StockRuleHit(cond, target, price, rate));

        [Theory]
        [InlineData(1000, 1050, true)]    // +5% 경계
        [InlineData(1000, 1051, true)]
        [InlineData(1000, 1049, false)]   // +4.9%
        [InlineData(1000, 950, true)]     // -5%
        [InlineData(1000, 1000, false)]   // 변동 없음
        [InlineData(0, 1000, false)]      // prev<=0 가드
        [InlineData(-10, 1000, false)]
        public void IsSurge(decimal prev, decimal price, bool expected)
            => Assert.Equal(expected, AlertPolicy.IsSurge(prev, price));

        [Fact]
        public void IsSurge_CustomThreshold()
        {
            Assert.True(AlertPolicy.IsSurge(1000, 1030, 3m));    // 3% 임계 → +3% 충족
            Assert.False(AlertPolicy.IsSurge(1000, 1020, 3m));   // +2% 미달
        }
    }
}
