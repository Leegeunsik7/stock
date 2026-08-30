// ============================================================================
// AlertFeedAdapter — Phase 94-G: 알림 피드 Port 호스트 어댑터 (DIP 구현)
//   순수 UI 의 IAlertFeedPort 를 엔진 콘크리트(AlertCenter 정적 싱글턴)에 결속한다.
//
//   ★무할당(δ=0) 공변 반환: GetRecent 는 AlertCenter.GetFromDb 가 돌려준 List<AlertItem> 을 재투영/복사 없이
//     IReadOnlyList<IAlertRecord> 로 그대로 노출한다(AlertItem : IAlertRecord + IReadOnlyList 공변성).
//   ★이벤트 브리지: AlertCenter.AlertRaised(EventHandler<AlertItem>) 를 구독해 RCL Action<IAlertRecord> 로 재발화 —
//     UI 는 엔진 EventHandler/AlertItem 타입을 전혀 참조하지 않는다.
//   ★수명: AlertCenter.Instance 는 프로세스 전역 싱글턴이므로 어댑터도 싱글턴으로 등록(구독 1회, 누수 없음).
//   ★liveArmed:false / fail-closed: 순수 조회·읽음처리 전용(발주·소켓·브로커 무접촉). 하위 예외는 AlertCenter 가
//     내부에서 삼키므로(try/catch) 조회 실패 시 빈 목록이 되어 UI 가 정직 렌더한다(가짜 데이터 0).
// ============================================================================
using System;
using System.Collections.Generic;
using MS2E.UI.Portable.Ports.Alerts;

namespace MS2E.Infrastructure
{
    public sealed class AlertFeedAdapter : IAlertFeedPort
    {
        public AlertFeedAdapter()
        {
            // 엔진 이벤트 → 포트 이벤트 재발화(싱글턴 수명 동안 1회 구독).
            AlertCenter.Instance.AlertRaised += OnHostAlert;
        }

        // AlertItem → IAlertRecord 업캐스트(무할당). 구독자 없으면 no-op.
        private void OnHostAlert(object? _, AlertItem item) => AlertRaised?.Invoke(item);

        // List<AlertItem> → IReadOnlyList<IAlertRecord> : 공변 참조 변환(사본 0).
        public IReadOnlyList<IAlertRecord> GetRecent(int max, bool unreadOnly = false)
            => AlertCenter.Instance.GetFromDb(max, unreadOnly);

        public void MarkAllRead() => AlertCenter.Instance.MarkAllRead();

        public event Action<IAlertRecord>? AlertRaised;
    }
}
