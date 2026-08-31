// ============================================================================
// TradingPorts — Phase 94-N: 트레이딩 허브(주문 증적/정합) Ports (순수 UI 경계)
//   GatingPage·OrderReconcilePanel 을 엔진 구상(OrderAttestationService·GatingMode·LocalState/BrokerState·
//   AttestationAuditEntry)에서 물리 격리. 읽기(모드/감사이력) + 명령(정합검증/수동해제) 분리.
//   ★liveArmed:false: 정합 검증/동결 해제는 '주문 게이트 관제'일 뿐 실 발주 아님(발주는 물리 이중 키 게이트).
//   ★엔진 타입 참조 0(RCL 순수성) · fail-closed.
// ============================================================================
using System;
using System.Collections.Generic;

namespace MS2E.UI.Portable.Ports.Trading
{
    /// <summary>단일 미스매치 읽기 뷰(AttestationMismatch 추상 — 필드/로컬·브로커 값/부호차).</summary>
    public interface IMismatchView
    {
        string Field { get; }
        decimal LocalValue { get; }
        decimal BrokerValue { get; }
        decimal Delta { get; }   // Local - Broker(부호 있음)
    }

    /// <summary>정합 감사 항목 읽기 뷰(AttestationAuditEntry 추상 — 미스매치 상세 + 개수 투사).</summary>
    public interface IAttestationEntryView
    {
        DateTime Timestamp { get; }
        bool Passed { get; }
        string Signature { get; }
        string Note { get; }
        int MismatchCount { get; }
        IReadOnlyList<IMismatchView> Mismatches { get; }
    }

    /// <summary>트레이딩 허브 Port — 비상 동결/정합 관제(주문 증적). 도메인 구상 미유입.</summary>
    public interface ITradingHubPort
    {
        /// <summary>비상 동결(EmergencyFreeze) 여부.</summary>
        bool IsFrozen { get; }

        /// <summary>정합 감사 이력(읽기 전용 뷰).</summary>
        IReadOnlyList<IAttestationEntryView> AuditLog { get; }

        /// <summary>로컬 원장 ↔ 브로커 잔고 정합 검증(어댑터가 LocalState/BrokerState 조립·Reconcile). 결과 뷰 반환.</summary>
        IAttestationEntryView Reconcile(decimal localCash, IReadOnlyDictionary<string, decimal> localHoldings,
                                        decimal brokerCash, IReadOnlyDictionary<string, decimal> brokerHoldings);

        /// <summary>수동 정합 서명으로 동결 해제.</summary>
        void ManualRelease(string signature, string note);
    }
}
