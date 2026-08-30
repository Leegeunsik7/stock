// ============================================================================
// AlertsPorts — Phase 94-G: 알림 센터 Ports & Adapters 계약 (순수 UI 경계)
//
//   AlertPage / AlertHistoryPage 를 엔진 콘크리트(AlertCenter 정적 싱글턴 · AlertItem/AlertLevel/AlertSource 엔진
//   타입)에서 물리 격리한다. 순수 UI 는 이 인터페이스 + RCL 열거형만 알고, 어댑터(호스트 측)가 엔진에 결속한다.
//   ★엔진 타입 참조 0 (UiAssemblyPuritySentinel 테스트가 CI 로 강제).
//
//   설계 노트 — 무할당(δ=0) 프로젝션:
//     GetRecent 는 IReadOnlyList<IAlertRecord> 를 반환한다. IReadOnlyList<out T> 는 공변이므로, 엔진 AlertItem 이
//     IAlertRecord 를 구현하면 어댑터는 List<AlertItem> 을 '재투영/복사 없이' IReadOnlyList<IAlertRecord> 로 그대로
//     넘길 수 있다(경계당 DTO 사본 0). 이는 기존 Port 계층의 '명시적 공변 구현' 규약을 그대로 계승한다.
//
//   열거형 정수값 정렬(엔진↔RCL): AlertLevel{Info=0,Warning=1,Critical=2} ↔ AlertSeverity(동일),
//                                AlertSource{Price=0,News=1,Signal=2,Dividend=3,System=4} ↔ AlertOrigin(동일).
// ============================================================================
using System;
using System.Collections.Generic;

namespace MS2E.UI.Portable.Ports.Alerts
{
    /// <summary>알림 심각도 — 엔진 AlertLevel 의 순수 UI 대응(정수값 정렬).</summary>
    public enum AlertSeverity { Info = 0, Warning = 1, Critical = 2 }

    /// <summary>알림 출처 — 엔진 AlertSource 의 순수 UI 대응(정수값 정렬).</summary>
    public enum AlertOrigin { Price = 0, News = 1, Signal = 2, Dividend = 3, System = 4 }

    /// <summary>단일 알림 레코드의 읽기 전용 뷰. 엔진 AlertItem 이 이 계약을 구현(공변 투영) — UI 는 엔진 타입 무참조.</summary>
    public interface IAlertRecord
    {
        DateTime Time { get; }
        AlertOrigin Source { get; }
        AlertSeverity Level { get; }
        string Title { get; }
        string Message { get; }
        string Symbol { get; }
        bool IsRead { get; }
    }

    /// <summary>
    /// 알림 피드 Port — 조회/읽음처리/신규발생 구독의 최소 계약.
    /// ★liveArmed:false: 순수 조회·상태 알림 전용. 발주/소켓/브로커 무접촉(fail-closed — 어댑터 예외 시 빈 목록).
    /// </summary>
    public interface IAlertFeedPort
    {
        /// <summary>최근 알림 최대 max 건(공변 반환 — 어댑터가 엔진 리스트를 재투영 없이 그대로 노출).</summary>
        IReadOnlyList<IAlertRecord> GetRecent(int max, bool unreadOnly = false);

        /// <summary>모든 알림을 읽음 처리.</summary>
        void MarkAllRead();

        /// <summary>신규 알림 발생 이벤트(RCL 타입으로 재발화 — UI 는 엔진 EventHandler 무참조).</summary>
        event Action<IAlertRecord>? AlertRaised;
    }
}
