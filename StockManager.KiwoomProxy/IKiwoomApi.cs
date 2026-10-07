// ============================================================================
// IKiwoomApi — COM axKHOpenAPI+ 추상화 인터페이스
//
//  실제 COM 구현체(ProductionKiwoomApi)를 교체 플러그인 지점으로 격리.
//  NullKiwoomApi(테스트 스텁) / ProductionKiwoomApi(COM 래퍼)가 구현.
// ============================================================================
using System;

namespace StockManager.KiwoomProxy
{
    // ── 이벤트 인자 ──────────────────────────────────────────────────────────

    public sealed class TickEventArgs : EventArgs
    {
        public long   SymbolCode;   // KSE 6자리 코드 (005930 → 5930)
        public double Price;
        public long   Volume;
        public double ChangeRate;   // 전일 대비 (%)
    }

    public sealed class FillEventArgs : EventArgs
    {
        public long   OrderId;
        public long   SymbolCode;
        public double FillPrice;
        public long   FillQty;
        public int    Side;         // +1 = 매수, -1 = 매도
        /// <summary>[키움 프로토콜] 통보 상태(접수/거부/취소확인/체결). 미설정 = Legacy(구 동작).</summary>
        public BridgeFillStatus Status;
        /// <summary>[키움 프로토콜] 브로커 미체결 잔량(902 FID) · −1 = 미상.</summary>
        public long   RemainingQty = -1;
    }

    /// <summary>[L2] 10호가 스냅샷(가격 원 · 잔량 주).</summary>
    public sealed class OrderBookEventArgs : EventArgs
    {
        public OrderBookPayload Book;
    }

    /// <summary>[L2] 기준가(전일종가 · 원).</summary>
    public sealed class ReferencePriceEventArgs : EventArgs
    {
        public long SymbolCode;
        public long BasePrice;
    }

    /// <summary>[키움 COM] 키움 서버 중대 오류(KiwoomServerErrorCodes 71xx) — 프록시가 ErrorReport 로 송신.</summary>
    public sealed class ServerErrorEventArgs : EventArgs
    {
        public int Code;
    }

    /// <summary>[계좌] OPW00001 예수금상세.</summary>
    public sealed class AccountCashEventArgs : EventArgs { public AccountCashPayload Cash; }

    /// <summary>[계좌] OPW00018 보유 1행.</summary>
    public sealed class PositionEventArgs : EventArgs { public PositionPayload Row; }

    public sealed class AccountEventArgs : EventArgs
    {
        public long Deposit;        // 예수금 (원)
        public long Equity;         // 평가금액 (원)
        public long UnfilledCount;  // 미체결 주문 수
    }

    // ── COM 추상화 인터페이스 ─────────────────────────────────────────────────

    public interface IKiwoomApi
    {
        // ── COM 이벤트 → 터미널 방향 ──────────────────────────────────────────
        event EventHandler<TickEventArgs>    TickReceived;
        event EventHandler<FillEventArgs>    FillReceived;
        event EventHandler<AccountEventArgs> AccountReceived;
        event EventHandler<OrderBookEventArgs> OrderBookReceived;            // [L2]
        event EventHandler<ReferencePriceEventArgs> ReferencePriceReceived;  // [L2]
        event EventHandler<ServerErrorEventArgs> ServerErrorReceived;        // [키움 COM] 중대 서버 오류
        event EventHandler<AccountCashEventArgs> AccountCashReceived;        // [계좌] OPW00001
        event EventHandler<PositionEventArgs> PositionReceived;              // [계좌] OPW00018 행
        event EventHandler<OpenOrderEventArgs> OpenOrderReceived;            // [미체결 대사] OPT10075 행
        event EventHandler<OrderMappedEventArgs> OrderMapped;                // [미체결 대사] 클라이언트 ID ↔ 키움 번호

        /// <summary>[키움 COM · 방향 A] COM STA 메시지 펌프 정상 여부 — false 면 프록시가 Pong 을 억제(호스트 하트비트 단절 → 동결).</summary>
        bool PumpHealthy { get; }
        /// <summary>[키움 실배선] API 종류(ProxyApiKind) — 연결 직후 ProxyHello 로 호스트에 선언(시뮬레이션은 실 송출 불인정).</summary>
        int ApiKind { get; }

        // ── 터미널 → COM 명령 방향 ────────────────────────────────────────────
        void Connect();
        void SendOrder(long symbolCode, int side, double price, long qty, long orderId);
        void CancelOrder(long orderId);
        /// <summary>[정정] 지정가 정정(price = 정정가 원 · qty = 정정수량 주).</summary>
        void ModifyOrder(long orderId, double price, long qty);
        void SubscribeTick(long symbolCode);
        void UnsubscribeTick(long symbolCode);
        void QueryAccount();
        /// <summary>[미체결 대사] OPT10075 미체결 조회.</summary>
        void QueryOpenOrders();
        /// <summary>[미체결 대사] 승계 확정 주문 사상 재등록(이후 체결 통보가 호스트 클라이언트 ID 로 사상).</summary>
        void AdoptMapping(long clientId, long venueOrderNo, long symbolCode, int side, long remaining);
        /// <summary>[미체결 대사 · 방향 B] 키움 번호로 직접 전량 취소(유령 주문 비상 취소).</summary>
        void CancelByVenueNo(long venueOrderNo, long symbolCode, int side, long qty);
    }

    public sealed class OpenOrderEventArgs : EventArgs { public OpenOrderPayload Row; }
    public sealed class OrderMappedEventArgs : EventArgs { public OrderMappedPayload Map; }
}
