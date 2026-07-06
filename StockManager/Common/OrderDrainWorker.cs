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
        private readonly OrderAttestationService?    _att;          // v4.87.0 fail-closed 단일 소스
        private readonly Func<bool>?                 _dualKeyIntact; // v4.87.0 이중키 무결성 런타임 검문(옵션)
        private readonly System.Threading.Timer?     _timer;
        private volatile bool                        _disposed;

        public bool IsWired { get; }

        /// <param name="attestation">v4.87.0 — 드레인 루프 예외·이중키 무결성 붕괴 시 ForceFreeze 격발(옵션·null 시 무동작).</param>
        /// <param name="dualKeyIntact">v4.87.0 — 매 드레인 전 이중키(ValveWired ∧ LiveArmingWired) 무결성 검문(옵션).
        /// false 반환 또는 throw 시 강등이 아니라 ForceFreeze 로 파이프라인 영구 락업.</param>
        public OrderDrainWorker(GatedOrderDispatcher dispatcher, IBroker broker,
                                bool wired, int periodMs = 50,
                                OrderAttestationService? attestation = null,
                                Func<bool>? dualKeyIntact = null)
        {
            _dispatcher    = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
            _broker        = broker     ?? throw new ArgumentNullException(nameof(broker));
            _att           = attestation;
            _dualKeyIntact = dualKeyIntact;
            IsWired        = wired;
            if (wired)
                _timer = new System.Threading.Timer(_ => DrainOnce(), null, dueTime: 0, period: periodMs);
        }

        /// <summary>
        /// 1회 드레인 사이클(프로덕션은 50ms 타이머 구동·테스트는 직접 호출). ★fail-closed 단일 소스:
        /// 이중키 무결성 검문 실패/예외 또는 드레인 루프 예외 시 강등이 아니라 ForceFreeze 로 영구 락업.
        /// </summary>
        public void DrainOnce()
        {
            if (_disposed) return;   // Dispose 이후 in-flight 콜백 차단
            // ★이중키 무결성 검문(fail-closed): 무결성이 깨졌거나 검문 자체가 throw 하면 즉시 영구 락업.
            if (_dualKeyIntact is not null)
            {
                bool intact;
                try { intact = _dualKeyIntact(); }
                catch (Exception ex) { Freeze("이중키 무결성 검문 예외 — 영구 락업: " + ex.Message); return; }
                if (!intact) { Freeze("이중키(ValveWired ∧ LiveArmingWired) 무결성 붕괴 — 영구 락업"); return; }
            }
            // ★드레인 루프 예외 = 강등 아님. 즉시 ForceFreeze 단일 소스로 파이프라인 영구 락업(모호한 지속 금지).
            try   { _dispatcher.Drain(Dispatch); }
            catch (Exception ex) { Freeze("드레인 루프 예외 — 영구 락업: " + ex.Message); }
        }

        private void Freeze(string reason)
        {
            try { _att?.ForceFreeze("OrderDrainWorker " + reason); } catch { /* 동결 실패해도 진행 오염 금지 */ }
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
