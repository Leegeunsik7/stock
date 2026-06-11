// ============================================================================
// AlertPolicy - 알림 발동 판정 순수 함수 (AlertEngineService에서 분리, 테스트 대상)
//  · 코인 지정가 교차, 주식 규칙 조건, 5% 급변 — I/O 없는 결정적 로직만 모음.
//  · AlertEngineService가 이 함수를 호출하므로 "테스트한 코드 == 실제 사용 코드".
//  ※ 정책 비대칭(코인 재무장 vs 주식 one-shot)은 호출부의 상태 처리에 있고,
//    여기서는 '지금 조건을 만족하는가'(cross/hit)만 판정한다.
// ============================================================================
using System;

namespace StockManager.Common
{
    public static class AlertPolicy
    {
        /// <summary>코인 지정가 교차: Above면 현재가 ≥ 목표, 아니면 현재가 ≤ 목표.</summary>
        public static bool CoinCross(bool above, decimal target, decimal price)
            => above ? price >= target : price <= target;

        /// <summary>주식 알림 규칙 조건 충족 여부. 정의되지 않은 조건은 false.</summary>
        public static bool StockRuleHit(AlertCondition cond, decimal target, decimal price, decimal changeRate)
            => cond switch
            {
                AlertCondition.PriceAbove => price >= target,
                AlertCondition.PriceBelow => price <= target,
                AlertCondition.ChangeRateAbove => changeRate >= target,
                AlertCondition.ChangeRateBelow => changeRate <= target,
                _ => false
            };

        /// <summary>직전가 대비 급변 여부. prev>0 가드, 변동 없음 제외, |변동률| ≥ 임계(기본 5%).</summary>
        public static bool IsSurge(decimal prev, decimal price, decimal thresholdPct = 5m)
        {
            if (prev <= 0 || prev == price) return false;
            return Math.Abs((price - prev) / prev * 100m) >= thresholdPct;
        }
    }
}
