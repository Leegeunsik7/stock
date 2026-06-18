// ============================================================================
// NullKiwoomApi — COM 없이 KiwoomProxyHost 로직을 테스트하기 위한 스텁.
//
//  모든 명령은 무동작. SimulateXxx 헬퍼로 테스트 이벤트 인위 발행 가능.
//  프로덕션에서는 ProductionKiwoomApi(axKHOpenAPI COM 래퍼)로 교체.
// ============================================================================
using System;

namespace StockManager.KiwoomProxy
{
    public sealed class NullKiwoomApi : IKiwoomApi
    {
        public event EventHandler<TickEventArgs>?    TickReceived;
        public event EventHandler<FillEventArgs>?    FillReceived;
        public event EventHandler<AccountEventArgs>? AccountReceived;

        public void Connect()                                                        { }
        public void SendOrder(long sym, int side, double price, long qty, long oid) { }
        public void CancelOrder(long orderId)                                        { }
        public void SubscribeTick(long symbolCode)                                   { }
        public void UnsubscribeTick(long symbolCode)                                 { }
        public void QueryAccount()                                                   { }

        // ── 테스트 헬퍼 ──────────────────────────────────────────────────────
        public void SimulateTick(long symbolCode, double price, long volume = 100, double changeRate = 0.5)
            => TickReceived?.Invoke(this, new TickEventArgs
               { SymbolCode = symbolCode, Price = price, Volume = volume, ChangeRate = changeRate });

        public void SimulateFill(long orderId, long symbolCode, double fillPrice, long qty, int side)
            => FillReceived?.Invoke(this, new FillEventArgs
               { OrderId = orderId, SymbolCode = symbolCode, FillPrice = fillPrice, FillQty = qty, Side = side });

        public void SimulateAccount(long deposit, long equity, long unfilledCount = 0)
            => AccountReceived?.Invoke(this, new AccountEventArgs
               { Deposit = deposit, Equity = equity, UnfilledCount = unfilledCount });
    }
}
