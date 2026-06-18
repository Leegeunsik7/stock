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
    }

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

        // ── 터미널 → COM 명령 방향 ────────────────────────────────────────────
        void Connect();
        void SendOrder(long symbolCode, int side, double price, long qty, long orderId);
        void CancelOrder(long orderId);
        void SubscribeTick(long symbolCode);
        void UnsubscribeTick(long symbolCode);
        void QueryAccount();
    }
}
