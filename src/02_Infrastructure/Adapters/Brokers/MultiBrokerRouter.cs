// ============================================================================
// MultiBrokerRouter — S15 #2: 크로스-브로커 탄력 라우터
//
//  · IBroker 미들웨어 — 전략은 일반 IBroker 로 취급.
//  · 정상 상태: _primary(ExecutionBridgeBroker) 로 라우팅.
//  · ProxyLost 또는 게이팅 동결 감지 → _fallback 으로 자동 전환 (단 1회).
//  · HandleProxyLost() : KiwoomBridgeClient.ProxyLost 에 연결하거나 테스트에서 직접 호출.
//  · FailoverOccurred  : Blazor 대시보드 토스트 등 외부 알림용.
//  · 폴백 전환 후 HeldQty = primary + fallback 합산 (부분 포지션 추적).
// ============================================================================
using System;
using System.Threading;

namespace StockManager.Common
{
    public sealed class MultiBrokerRouter : IBroker, IDisposable
    {
        private readonly IBroker _primary;
        private readonly IBroker _fallback;
        private volatile bool    _usingFallback;
        private int              _failoverFired;  // Interlocked CAS 단 1회 보장

        /// <summary>폴백 전환 완료 시 발행 (UI 토스트, 로그 등).</summary>
        public event Action<FailoverEvent>? FailoverOccurred;

        public string Name   => _usingFallback ? $"[FALLBACK] {_fallback.Name}" : _primary.Name;
        public bool   IsLive => _usingFallback ? _fallback.IsLive : _primary.IsLive;

        public bool IsUsingFallback => _usingFallback;
        public int  FailoverCount   => _failoverFired;

        public MultiBrokerRouter(IBroker primary, IBroker fallback)
        {
            _primary  = primary  ?? throw new ArgumentNullException(nameof(primary));
            _fallback = fallback ?? throw new ArgumentNullException(nameof(fallback));
        }

        // ── IBroker ───────────────────────────────────────────────────────────

        public (bool ok, string msg) Place(
            PaperAsset asset, string symbol, string name,
            PaperSide  side,  PaperKind kind,
            decimal    qty,   decimal limitPrice, decimal currentPrice)
        {
            if (_usingFallback)
                return _fallback.Place(asset, symbol, name, side, kind, qty, limitPrice, currentPrice);

            try
            {
                return _primary.Place(asset, symbol, name, side, kind, qty, limitPrice, currentPrice);
            }
            catch (InvalidOperationException ex)
                when (ex.Message.StartsWith("[GATING FREEZE]", StringComparison.Ordinal))
            {
                // 프라이머리 게이팅 동결 → 폴백으로 자동 전환
                TriggerFailover("게이팅 동결 감지 — 폴백 자동 전환");
                return _fallback.Place(asset, symbol, name, side, kind, qty, limitPrice, currentPrice);
            }
        }

        public void Cancel(string orderId)
            => (_usingFallback ? _fallback : _primary).Cancel(orderId);

        public decimal HeldQty(PaperAsset asset, string symbol)
        {
            // 폴백 전환 후: 양쪽 브로커 보유수량 합산 (청산 미완료 포지션 반영)
            if (_usingFallback)
                return _primary.HeldQty(asset, symbol) + _fallback.HeldQty(asset, symbol);
            return _primary.HeldQty(asset, symbol);
        }

        // ── 폴백 전환 진입점 ──────────────────────────────────────────────────

        /// <summary>
        /// KiwoomBridgeClient.ProxyLost 이벤트에 직접 연결:
        ///   bridge.ProxyLost += router.HandleProxyLost;
        /// 테스트에서도 직접 호출 가능.
        /// </summary>
        public void HandleProxyLost()
            => TriggerFailover("KiwoomProxyHost 단절 (하트비트 타임아웃)");

        private void TriggerFailover(string reason)
        {
            // CAS: 폴백 전환은 생애 주기 동안 단 1회
            if (Interlocked.CompareExchange(ref _failoverFired, 1, 0) != 0) return;

            _usingFallback = true;

            FailoverOccurred?.Invoke(new FailoverEvent(
                reason,
                _primary.Name,
                _fallback.Name,
                DateTime.UtcNow));
        }

        // ── IDisposable ───────────────────────────────────────────────────────

        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_primary  is IDisposable d1) d1.Dispose();
            if (_fallback is IDisposable d2) d2.Dispose();
        }
    }
}
