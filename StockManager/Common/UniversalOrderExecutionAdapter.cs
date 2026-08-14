// ============================================================================
// UniversalOrderExecutionAdapter — v11.0.0: 실자본 주문 집행 어댑터 (하드웨어 인터락 절대 결속)
//
//   증권사 API 로 실제 매수/매도/취소를 사출하는 IOrderExecutionPort 구상체(증권사별). ★모든 발주는 반드시
//   GenesisLiveIgnitionGateway.Emit(이중 물리 키 ValveWired∧LiveArmingWired ∧ 방어선 ∧ 키펄스 인터락)을 경유한다 —
//   단 1개의 veto(물리 키 해제·퀀텀 금고 차단 등)라도 있으면 Emit 이 여기서 거부(O(1) fail-closed)하고 실 발주 델리게이트
//   (onSend)를 절대 호출하지 않는다(단 1비트 실물 누수 봉쇄). ★Think-Beyond InFlightOrderReconciliationLedger 로 동일 종목
//   미해소 pending 이 있으면 신규 발주를 차단(타임아웃 중복/유령 재발주 방지).
//
//   ★불변: liveArmed:false(★이중 물리 키 무장 전 100% veto — onSend 미도달·실물 0비트) · fail-closed(veto/pending 충돌 →
//     Accepted=false) · δ=0(SendOrder 핫패스 힙 0 — Interlocked·캐시 델리게이트[무캡처]·ledger lock 무할당).
// ============================================================================
using System;
using System.Threading;

namespace StockManager.Common
{
    public sealed class UniversalOrderExecutionAdapter : IOrderExecutionPort
    {
        private readonly BrokerId _broker;
        private readonly GenesisLiveIgnitionGateway _ignition;
        private readonly InFlightOrderReconciliationLedger _ledger;
        private readonly BrokerOrderSend? _brokerSend;
        private readonly SealedSend _cachedOnSend;   // ★1회 캐시(δ=0·무캡처 — 매 발주 클로저 할당 회피)
        private long _dispatched, _vetoed;

        public UniversalOrderExecutionAdapter(BrokerId broker, GenesisLiveIgnitionGateway ignition,
                                              InFlightOrderReconciliationLedger ledger, BrokerOrderSend? brokerSend = null)
        {
            _broker     = broker;
            _ignition   = ignition ?? throw new ArgumentNullException(nameof(ignition));
            _ledger     = ledger ?? throw new ArgumentNullException(nameof(ledger));
            _brokerSend = brokerSend;
            _cachedOnSend = OnSealedSend;
        }

        public BrokerId Broker  => _broker;
        public long Dispatched  => Interlocked.Read(ref _dispatched);
        public long Vetoed      => Interlocked.Read(ref _vetoed);

        // ★게이트 하류 실 발주(liveArmed 충족 시에만 Emit 이 호출). onSend 미배선이면 no-op(실물 0).
        private void OnSealedSend(in SealedOrderToken tok) => _brokerSend?.Invoke(_broker, in tok);

        public ExecOrderResult SendOrder(in ExecOrderRequest request)
        {
            // ★Think-Beyond: ledger 가 전역 유니크 clOrdId 발급 + 동일 종목 미해소 pending/phantom 이면 -1 거부(타임아웃 중복·
            //   유령 미체결 재발주 봉쇄·다중 어댑터 슬롯 오매칭 봉쇄).
            long clOrd = _ledger.TryBeginOrder(request.SymbolCode, request.QtyRaw);
            if (clOrd < 0L)
            {
                Interlocked.Increment(ref _vetoed);
                return new ExecOrderResult(false, 0L, -409L);
            }

            // ★하드웨어 이중 키 인터락: liveArmed 미충족/veto 면 Emit 이 여기서 거부 → onSend 절대 미호출(실물 0비트).
            bool emitted;
            try
            {
                emitted = _ignition.Emit(clOrd, request.SymbolCode, (byte)request.Side, (byte)request.Kind,
                                         request.PriceRaw, request.QtyRaw, _cachedOnSend);
            }
            catch
            {
                // ★brokerSend 예외(네트워크/타임아웃) — 주문이 서버 도달했을 수 있어 상태 불확정 → phantom 래치(reconcile
                //   전까지 그 종목 신규 발주 차단·fail-closed). clOrd 반환 → 호출자가 정산 조회·Reconcile 로 브릭 방지.
                _ledger.NotePhantom(clOrd);
                Interlocked.Increment(ref _vetoed);
                return new ExecOrderResult(false, clOrd, -500L);
            }
            if (!emitted)
            {
                _ledger.AbortOrder(clOrd);   // veto(미사출 확정) → pending 롤백(다음 발주 허용)
                Interlocked.Increment(ref _vetoed);
                return new ExecOrderResult(false, clOrd, _ignition.LastVetoCode);
            }

            Interlocked.Increment(ref _dispatched);
            return new ExecOrderResult(true, clOrd, 0L);
        }
    }
}
