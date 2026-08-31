// ============================================================================
// TradingHubAdapter — Phase 94-N: 트레이딩 허브 Port 호스트 어댑터 (DIP 구현)
//   순수 UI(GatingPage·OrderReconcilePanel)를 엔진 구상(OrderAttestationService·GatingMode·LocalState/BrokerState)에 결속.
//   ★liveArmed:false / fail-closed: 주문 증적/정합 관제 전용(실 발주·소켓 무접촉).
// ============================================================================
using System.Collections.Generic;
using MS2E.UI.Portable.Ports.Trading;

namespace MS2E.Infrastructure
{
    public sealed class TradingHubAdapter : ITradingHubPort
    {
        private readonly OrderAttestationService _att;
        public TradingHubAdapter(OrderAttestationService att) => _att = att;

        public bool IsFrozen => _att.Mode == GatingMode.EmergencyFreeze;

        // List<AttestationAuditEntry> → IReadOnlyList<IAttestationEntryView> : 공변(사본 0).
        public IReadOnlyList<IAttestationEntryView> AuditLog => _att.AuditLog;

        public IAttestationEntryView Reconcile(decimal localCash, IReadOnlyDictionary<string, decimal> localHoldings,
                                               decimal brokerCash, IReadOnlyDictionary<string, decimal> brokerHoldings)
            => _att.Reconcile(new LocalState(localCash, localHoldings), new BrokerState(brokerCash, brokerHoldings));

        public void ManualRelease(string signature, string note) => _att.ManualRelease(signature, note);
    }
}
