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
using System;

namespace MS2E.Infrastructure
{
    /// <summary>게이트가 승인한 실제 송출 형태(값 타입 · δ=0). KindByte 0=시장가 · 1=지정가 / PriceRaw·QtyRaw = QPrice·QQty raw(10^8).</summary>
    /// <summary>[저지연] BasisAgeMicros = 판정에 쓴 참조가 틱의 나이(µs · −1 = 미상) — Tick-to-Trade 의 '틱 → 판정' 구간.</summary>
    /// <param name="Broker">[다중 브로커] 라우터가 고른 브로커 인덱스(LockFreeBrokerSwitchboard) — OrderDrainWorker 가 이 인덱스의 IBroker 로 송출.</param>
    public readonly record struct OrderAdmission(byte KindByte, long PriceRaw, long QtyRaw, long BasisAgeMicros = -1, int Broker = 0);

    /// <summary>
    /// [저지연] 주문 1건 Tick-to-Trade 계측 프로브(δ=0). OrderDrainWorker 실전 경로가 호출:
    ///   t0 = Timestamp()(디스패치 진입) → 게이트 판정 → AdmitBeforeSend(t0)(송출 직전 지연 폭주면 false = 미송출 · Disarm)
    ///   → 실 송출 → RecordSent(t0, basisAge)(프로세스 내 구간 + 틱 나이 = Tick-to-Trade 기록).
    /// </summary>
    /// <summary>
    /// [정정/취소] 전방 판정 게이트(δ=0) — ExecutionBridgeBroker 가 취소/정정 요청을 파이프로 보내기 '전' 호출.
    /// 추적 원장에 없거나(이미 체결/취소/거부 종결) 진행 중 취소가 있거나 정정 수량이 잔량을 넘으면 false = 송신 0.
    /// </summary>
    public interface IOrderAmendGate
    {
        bool TryBeginCancel(long brokerOrderId);
        bool TryBeginModify(long brokerOrderId, long newQtyShares, long newPriceWon);
    }

    public interface ITickToTradeProbe
    {
        long Timestamp();
        bool AdmitBeforeSend(long dispatchStartTimestamp);
        void RecordSent(long dispatchStartTimestamp, long basisAgeMicros);
    }

    public interface IOrderRouteGate
    {
        bool TryAdmit(in OrderProposal proposal, out OrderAdmission admission);
        void OnBrokerAccepted(long proposalId, string brokerOrderId);
        void OnNotSent(long proposalId);
    }

    // ════════════════════════════════════════════════════════════════════════
    // [자식 슬롯 분할 라우팅] 부모 제안 1건 → 브로커별 자식 주문 N건(≤ 64) 공용 계약(값 타입 · δ=0)
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>브로커별 분할 한도(호출측 버퍼). MaxShares = 이 부모에서 해당 브로커가 받을 수 있는 최대 주수(신용/명목/레이트 한도 환산).</summary>
    public struct BrokerSplitCap
    {
        public int Broker;
        public long MaxShares;
        public bool Available;
    }

    /// <summary>
    /// 자식 주문 계획 1건. ChildId = 추적/실행 피드 키(64비트 · ChildIdSpace) · TrackingId = 128비트 자식 추적 ID(상위 64 = 부모 · 하위 64 = 자식) ·
    /// Level = 호가 단계(0 = 최우선 · −1 = 깊이 미사용) · LimitRaw = 자식 지정가(QPrice raw · 부모 지정가보다 불리하지 않음).
    /// </summary>
    public struct ChildOrderPlan
    {
        public long ChildId, ParentId, Shares, LimitRaw;
        public int Broker, Level, Index;
        public UInt128 TrackingId => new((ulong)ParentId, (ulong)ChildId);
    }

    /// <summary>
    /// 자식 ID 공간 — 비트 62 표지 | 부모(56비트) &lt;&lt; 6 | 자식 순번(6비트 · 0~63). 일반 제안 ID(&lt; 2^62)와 충돌 0 ·
    /// 부모/순번 복원 O(1). 부모 ID 가 56비트를 넘으면 분할 불가(Compose = 0 · 호출측 거부).
    /// </summary>
    public static class ChildIdSpace
    {
        public const long Marker = 1L << 62;
        public const int MaxChildren = 64;
        private const long ParentMask = (1L << 56) - 1;
        public static long Compose(long parentId, int index)
            => parentId <= 0 || parentId > ParentMask || (uint)index >= MaxChildren ? 0 : Marker | (parentId << 6) | (long)index;
        public static bool IsChild(long id) => id > 0 && (id & Marker) != 0;
        public static long ParentOf(long childId) => IsChild(childId) ? (childId & ~Marker) >> 6 : 0;
        public static int IndexOf(long childId) => IsChild(childId) ? (int)(childId & 63) : -1;
    }

    /// <summary>
    /// [자식 슬롯 분할] 분할 게이트 — 부모 단위 전방 판정(피드·참조가·보호 지정가·깊이·계좌 예약·익스포저) 1회 + 자식별 라우터 키/추적 슬롯 확보.
    /// 자식 하나라도 확보 실패 = 전 자식 원자 롤백 후 false(부분 확보 0). 성공 시 children[0..count) 를 순서대로 송출 · 각 자식은
    /// IOrderRouteGate.OnBrokerAccepted/OnNotSent 를 ChildId 로 호출.
    /// </summary>
    public interface IChildSplitRouteGate : IOrderRouteGate
    {
        bool TryAdmitSplit(in OrderProposal proposal, Span<ChildOrderPlan> children, out int count);
    }

    /// <summary>
    /// [자식 슬롯 분할] 브로커별 실시간 주문 한도 공급(δ=0 · 드레인 스레드). dst[b] 에 브로커 b 의 한도(주)를 채우고 개수 반환.
    /// parentReservedWon = 게이트가 이 부모를 위해 '이미' 계좌에 예약한 매수 명목 — 같은 계좌를 보는 브로커의 여유에 되돌려 더한다(이중 차감 방지).
    /// 미결속·만료·소진 브로커는 Available=false(그 브로커로 자식 0).
    /// </summary>
    public interface IBrokerLimitFeed
    {
        int Fill(Span<BrokerSplitCap> dst, long symbolCode, MS2E.Core.OrderSide side, long limitPriceRaw, long parentReservedWon);
    }

    /// <summary>[자식 슬롯 분할] 자식 수명 원장(추적 매트릭스) — 송출 결과 기록 · 거부 시 미송출 형제 회수 + Disarm.</summary>
    public interface IChildSlotLedger
    {
        bool Open(long parentId, ReadOnlySpan<ChildOrderPlan> children);
        /// <summary>송출 0 상태에서 부모 포기(게이트웨이 veto 등 · 거부 아님) — 미송출 자식 회수 · Disarm 없음.</summary>
        void Abandon(long parentId);
        void MarkSent(long childId, long brokerOrderId);
        /// <summary>자식 송출 실패/거부 — 미송출(Pending) 형제를 회수하고 그 수를 반환 · 분할 인터락 Disarm(sticky).</summary>
        int MarkSendFailed(long childId);
    }
}
