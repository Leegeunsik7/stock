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
        public event EventHandler<OrderBookEventArgs>? OrderBookReceived;
        public event EventHandler<ServerErrorEventArgs>? ServerErrorReceived;
        public event EventHandler<AccountCashEventArgs>? AccountCashReceived;
        public event EventHandler<PositionEventArgs>? PositionReceived;
        public int ApiKind => ProxyApiKind.Simulation;   // ★주문 무동작 — 호스트가 실 송출 대상으로 인정하지 않도록 선언
        public event EventHandler<OpenOrderEventArgs>? OpenOrderReceived;
        public event EventHandler<OrderMappedEventArgs>? OrderMapped;
        public void QueryOpenOrders() { }
        public void AdoptMapping(long clientId, long venueOrderNo, long symbolCode, int side, long remaining) { }
        public void CancelByVenueNo(long venueOrderNo, long symbolCode, int side, long qty) { }
        public void SimulateOpenOrder(in OpenOrderPayload row) => OpenOrderReceived?.Invoke(this, new OpenOrderEventArgs { Row = row });
        public void SimulateOrderMapped(in OrderMappedPayload map) => OrderMapped?.Invoke(this, new OrderMappedEventArgs { Map = map });
        public void ModifyOrder(long orderId, double price, long qty) { }
        public void SimulateAccountCash(in AccountCashPayload cash) => AccountCashReceived?.Invoke(this, new AccountCashEventArgs { Cash = cash });
        public void SimulatePosition(in PositionPayload row) => PositionReceived?.Invoke(this, new PositionEventArgs { Row = row });
        /// <summary>시뮬레이터 — 기본 정상. 테스트/운영자 드릴이 false 로 STA 동결을 모사.</summary>
        public bool PumpHealthy { get; set; } = true;

        public void SimulateServerError(int code)
            => ServerErrorReceived?.Invoke(this, new ServerErrorEventArgs { Code = code });
        public event EventHandler<ReferencePriceEventArgs>? ReferencePriceReceived;

        public void SimulateOrderBook(in OrderBookPayload book)
            => OrderBookReceived?.Invoke(this, new OrderBookEventArgs { Book = book });

        public void SimulateReferencePrice(long symbolCode, long basePrice)
            => ReferencePriceReceived?.Invoke(this, new ReferencePriceEventArgs { SymbolCode = symbolCode, BasePrice = basePrice });
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

        public void SimulateFill(long orderId, long symbolCode, double fillPrice, long qty, int side,
                                 BridgeFillStatus status = BridgeFillStatus.Legacy, long remainingQty = -1)
            => FillReceived?.Invoke(this, new FillEventArgs
               { OrderId = orderId, SymbolCode = symbolCode, FillPrice = fillPrice, FillQty = qty, Side = side,
                 Status = status, RemainingQty = remainingQty });

        public void SimulateAccount(long deposit, long equity, long unfilledCount = 0)
            => AccountReceived?.Invoke(this, new AccountEventArgs
               { Deposit = deposit, Equity = equity, UnfilledCount = unfilledCount });
    }
}
