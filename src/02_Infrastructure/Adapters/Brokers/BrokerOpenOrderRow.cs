// ============================================================================
// BrokerOpenOrderRow — [미체결 대사 · 추천 A] 브로커 미체결 원장 1행(비관리 고정 레이아웃) · 대사 송신 포트
//
//  키움 OPT10075(프록시 OpenOrder 프레임 80B)와 KIS TTTC8001R(output1 JSON 배열)을 같은 행 구조로 정규화한다 —
//  대사기(BrokerOpenOrderReconciler)는 이 행과 호스트 원장(OrderExecutionFeedAdapter)만 본다.
//  IOpenOrderVenue = 대사기가 브로커에 내리는 명령 3종(미체결 조회 · 승계 사상 재등록 · 원장 번호 직접 취소).
// ============================================================================
using System.Runtime.InteropServices;

namespace MS2E.Infrastructure
{
    public static class OpenOrderVenueKind
    {
        public const int Kiwoom = 1, Kis = 2;
    }

    /// <summary>브로커 미체결 1행(원 · 주 · 64B).</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct BrokerOpenOrderRow
    {
        public long VenueOrderNo;       // 브로커 주문번호(키움 주문번호 · KIS ODNO)
        public long OrigVenueOrderNo;   // 원주문번호(정정/취소 주문 · 신규 = 0)
        public long SymbolCode;
        public long OrderQty;
        public long RemainingQty;       // 미체결수량
        public long PriceWon;           // 주문가격(시장가 = 0)
        public int  Side;               // +1 매수 · −1 매도
        public int  Venue;              // OpenOrderVenueKind
        public long Reserved;

        /// <summary>키움 OPT10075 프레임 행 → 정규화 행(형식 검증 포함). RowCount 0(빈 결과)은 호출측이 별도 처리.</summary>
        public static bool TryFromKiwoom(in OpenOrderPayload p, out BrokerOpenOrderRow row)
        {
            row = default;
            if (p.VenueOrderNo <= 0 || p.SymbolCode <= 0 || p.SymbolCode > 999_999 || p.RemainingQty < 0 || p.OrderQty < 0
                || p.OrderPriceWon < 0 || (p.Side != 1 && p.Side != -1)) return false;
            row = new BrokerOpenOrderRow
            {
                VenueOrderNo = p.VenueOrderNo, OrigVenueOrderNo = p.OrigVenueOrderNo > 0 ? p.OrigVenueOrderNo : 0, SymbolCode = p.SymbolCode,
                OrderQty = p.OrderQty, RemainingQty = p.RemainingQty, PriceWon = p.OrderPriceWon, Side = p.Side, Venue = OpenOrderVenueKind.Kiwoom,
            };
            return true;
        }
    }

    /// <summary>대사기 → 브로커 명령 포트(키움 = ExecutionBridgeBroker). 반환 = 송신 여부(미연결 = false).</summary>
    public interface IOpenOrderVenue
    {
        int VenueKind { get; }
        bool RequestOpenOrders();
        bool AdoptMapping(long clientId, long venueOrderNo, long symbolCode, int side, long remaining, long priceWon);
        bool CancelByVenueNo(long venueOrderNo, long symbolCode, int side, long qty);
    }
}
