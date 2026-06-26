// ============================================================================
// OrderDrainWorker — v4.70.0: 발주 밸브 결속
//
// GatedOrderDispatcher.Drain() 을 50ms 주기 타이머로 구동해 IBroker.Place() 와
// 결속한다.  Dispatch.ValveWired=true 일 때만 타이머 가동(기본 false — 드라이런 박제).
//
// 안전 원칙:
//   ① broker.Place() ok=false → InvalidOperationException throw.
//      GatedOrderDispatcher Mission A 벽 → ForceFreeze(비상 동결) + 루프 break.
//   ② broker.Place() 예외 → 동일 경로(Mission A 흡수).
//   ③ 타이머 callback 외곽 try/catch — 소실 방지. 지속 오류는 SendErrors 누적.
//   ④ SymbolCode(long) → D6(KSE 6자리). name = symbol(링에 표시명 미포함).
//   ⑤ currentPrice = limitPrice — 브로커 내부 시장가 조회 계약.
//
// 이중 키 잠금:
//   Dispatch.ValveWired(이 클래스)  ∧  Dispatch.LiveArmingWired(armedPredicate)
//   둘 다 true 여야 실 주문 흐름.  어느 하나만 false 여도 실거래 0%.
// ============================================================================
using System;

namespace StockManager.Common
{
    public sealed class OrderDrainWorker : IDisposable
    {
        private readonly GatedOrderDispatcher        _dispatcher;
        private readonly IBroker                     _broker;
        private readonly System.Threading.Timer?     _timer;
        private volatile bool                        _disposed;

        public bool IsWired { get; }

        public OrderDrainWorker(GatedOrderDispatcher dispatcher, IBroker broker,
                                bool wired, int periodMs = 50)
        {
            _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
            _broker     = broker     ?? throw new ArgumentNullException(nameof(broker));
            IsWired     = wired;
            if (wired)
                _timer = new System.Threading.Timer(_ =>
                {
                    if (_disposed) return;   // Dispose 이후 in-flight 콜백 차단
                    try   { _dispatcher.Drain(Dispatch); }
                    catch { }
                }, null, dueTime: 0, period: periodMs);
        }

        private void Dispatch(OrderProposal p)
        {
            // KSE 6자리 범위를 이탈한 코드는 조용히 건너뜀(ForceFreeze 불필요 — 상류 버그).
            if (p.SymbolCode < 1 || p.SymbolCode > 999_999) return;

            var symbol     = p.SymbolCode.ToString("D6");
            var side       = p.Side == OrderSide.Buy ? PaperSide.Buy : PaperSide.Sell;
            var kind       = p.KindByte == 0 ? PaperKind.Market : PaperKind.Limit;
            var qty        = p.Qty.ToDecimal();
            var limitPrice = p.Price.ToDecimal();

            var (ok, msg) = _broker.Place(
                PaperAsset.Stock, symbol, symbol,
                side, kind, qty, limitPrice, limitPrice);

            if (!ok)
                throw new InvalidOperationException($"Broker.Place 거부: {msg}");
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { _timer?.Dispose(); } catch { }
        }
    }
}
