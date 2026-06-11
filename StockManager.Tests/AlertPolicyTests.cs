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

        // ===== 재무장(re-arm) 스텝 =====
        [Fact]
        public void StepAlert_Enter_Fires()
        {
            var (fired, triggered) = AlertPolicy.StepAlert(above: true, target: 1000, price: 1000, wasTriggered: false);
            Assert.True(fired); Assert.True(triggered);          // 미발동 상태에서 진입 → 발동
        }

        [Fact]
        public void StepAlert_StillCrossing_NoRefire()
        {
            var (fired, triggered) = AlertPolicy.StepAlert(true, 1000, 1100, wasTriggered: true);
            Assert.False(fired); Assert.True(triggered);         // 이미 발동 중 → 재발동 없음, 유지
        }

        [Fact]
        public void StepAlert_Exit_ReArms()
        {
            var (fired, triggered) = AlertPolicy.StepAlert(true, 1000, 900, wasTriggered: true);
            Assert.False(fired); Assert.False(triggered);        // 조건 이탈 → 재무장(발동 안 함)
        }

        [Fact]
        public void StepAlert_BelowAndNotTriggered_Idle()
        {
            var (fired, triggered) = AlertPolicy.StepAlert(true, 1000, 900, wasTriggered: false);
            Assert.False(fired); Assert.False(triggered);
        }

        [Fact]
        public void StepAlert_FullCycle_FiresTwice()
        {
            // 진입→발동, 이탈→재무장, 재진입→재발동 (재무장이 핵심)
            bool trig = false; int fires = 0;
            foreach (var price in new decimal[] { 1100, 1200, 900, 1100 })   // 진입·유지·이탈·재진입
            {
                var (f, nt) = AlertPolicy.StepAlert(true, 1000, price, trig);
                trig = nt; if (f) fires++;
            }
            Assert.Equal(2, fires);   // 첫 진입 + 재무장 후 재진입 = 2회
        }
    }
}
