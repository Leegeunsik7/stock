// ============================================================================
// AlertPolicy - 알림 발동 판정 순수 함수 (AlertEngineService에서 분리, 테스트 대상)
//  · 코인 지정가 교차, 주식 규칙 조건, 5% 급변 — I/O 없는 결정적 로직만 모음.
//  · AlertEngineService가 이 함수를 호출하므로 "테스트한 코드 == 실제 사용 코드".
//  ※ 정책 비대칭(코인 재무장 vs 주식 one-shot)은 호출부의 상태 처리에 있고,
//    여기서는 '지금 조건을 만족하는가'(cross/hit)만 판정한다.
// ============================================================================
using System;

namespace MS2E.Infrastructure
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

        /// <summary>
        /// 코인 지정가 알림 1스텝 — 재무장(re-arm) 정책.
        /// 조건 진입(미발동→교차) 시 fired=true·triggered=true(발동), 조건 이탈(발동→비교차) 시
        /// triggered=false(재무장, 발동 안 함), 그 외 상태 유지(이미 발동 중이면 재발동 없음).
        /// 반환: (이번에 발동했는가, 갱신된 triggered).
        /// </summary>
        public static (bool fired, bool triggered) StepAlert(bool above, decimal target, decimal price, bool wasTriggered)
        {
            bool cross = CoinCross(above, target, price);
            if (cross && !wasTriggered) return (true, true);     // 진입 → 발동
            if (!cross && wasTriggered) return (false, false);   // 이탈 → 재무장
            return (false, wasTriggered);                        // 유지(발동 중 재발동 없음)
        }
    }
}
