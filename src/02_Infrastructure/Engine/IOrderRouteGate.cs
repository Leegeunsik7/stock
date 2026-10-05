// ============================================================================
// IOrderRouteGate — [피드 연동] 발주 송출 경로 ↔ 라우터(참조가·추적 슬롯) 결속 계약
//
//  OrderDrainWorker 실전 경로가 송출 직전/직후에 호출한다(드라이런 미러 경로 미적용):
//   ① TryAdmit(p)                 — 송출 직전: 참조가 신선도·피드 회로·라우터 판정(슬리피지·블랙리스트·Disarm) + 추적 슬롯 확보.
//                                   false = 미송출(GatedOut). 추적 불가 주문은 절대 송출하지 않는다.
//   ② OnBrokerAccepted(id, bid)   — 브로커 접수 성공: 브로커 주문번호 ↔ 추적 슬롯 결속(체결/취소 피드가 이 번호로 도착).
//   ③ OnNotSent(id)               — ①은 통과했으나 실 브로커에 도달하지 못함(하류 veto · Place ok=false): 슬롯 원자 롤백.
//                                   ※ Place 가 예외로 끝난 경우는 도달 여부가 모호하므로 호출하지 않는다(추적 유지 → 타임아웃 회수).
//  구현은 핫패스 δ=0 이어야 한다(브로커 주문번호 문자열은 브로커가 이미 할당한 값을 파싱만).
//  [보호 지정가] ①은 실제 송출할 주문 형태(OrderAdmission)를 돌려준다 — 시장가는 보호 지정가로 변환되고, 호가 잔량 부족 시 수량이
//   축소될 수 있다. OrderDrainWorker 는 원 제안(p)이 아니라 이 값으로 송출한다(비보호 시장가 유출 0).
// ============================================================================
namespace MS2E.Infrastructure
{
    /// <summary>게이트가 승인한 실제 송출 형태(값 타입 · δ=0). KindByte 0=시장가 · 1=지정가 / PriceRaw·QtyRaw = QPrice·QQty raw(10^8).</summary>
    public readonly record struct OrderAdmission(byte KindByte, long PriceRaw, long QtyRaw);

    public interface IOrderRouteGate
    {
        bool TryAdmit(in OrderProposal proposal, out OrderAdmission admission);
        void OnBrokerAccepted(long proposalId, string brokerOrderId);
        void OnNotSent(long proposalId);
    }
}
