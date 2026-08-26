// ============================================================================
// LiveAuditService — S14 #2 실거래 체결 감사 원장
//
//  · ConcurrentDictionary<long, PendingOrder> 로 주문 원장 관리 (스레드 세이프).
//  · RegisterOrder  — Place() 직후 PendingOrder 등록.
//  · ProcessFill    — FillPayload 수신 시 StockValidator → OrderAttestationService 2단계 감사.
//  · CancelOrder    — 취소 확인 시 상태 전환.
// ============================================================================
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace StockManager.Common
{
    public sealed class LiveAuditService
    {
        private readonly ConcurrentDictionary<long, PendingOrder> _pending = new();
        private readonly OrderAttestationService                   _attestation;

        /// <summary>체결가 허용 오차 비율 (기본 0.5 %). 시장가 주문 시 무시.</summary>
        public double PriceTolerance { get; init; } = 0.005;

        public LiveAuditService(OrderAttestationService attestation)
            => _attestation = attestation ?? throw new ArgumentNullException(nameof(attestation));

        // ── 주문 등록 ─────────────────────────────────────────────────────────

        public PendingOrder RegisterOrder(
            long      orderId,
            string    symbol,
            PaperSide side,
            PaperKind kind,
            double    expectedPrice,
            long      quantity,
            long      genomeId = 0L)
        {
            var order = new PendingOrder
            {
                OrderId       = orderId,
                Symbol        = symbol,
                Side          = side,
                Kind          = kind,
                ExpectedPrice = kind == PaperKind.Market ? 0.0 : expectedPrice,
                Quantity      = quantity,
                GenomeId      = genomeId,
                SubmittedAt   = DateTime.UtcNow,
            };
            _pending[orderId] = order;
            return order;
        }

        // ── 체결 처리 ─────────────────────────────────────────────────────────

        public FillAuditResult ProcessFill(FillPayload fill)
        {
            // 1차: StockValidator 게이트 (형식 오류는 attestation 없이도 차단)
            var v = StockValidator.ValidateFill(fill);
            if (!v.IsValid)
            {
                var reason = $"StockValidator 거부: {v.Summary()}";
                _attestation.ForceFreeze(reason);
                return new FillAuditResult(false, reason, 0.0, 0L);
            }

            // 대응 PendingOrder 탐색
            if (!_pending.TryGetValue(fill.OrderId, out var pending))
                return new FillAuditResult(true, $"알 수 없는 주문ID({fill.OrderId}) — 무시", 0.0, 0L);

            // 2차: OrderAttestationService 비교 감사
            var result = _attestation.ProcessFill(fill, pending, PriceTolerance);

            // 상태 갱신
            if (result.Passed)
            {
                pending.FilledQty += fill.FillQty;
                pending.State = pending.FilledQty >= pending.Quantity
                    ? PendingOrderState.Filled
                    : PendingOrderState.PartialFill;
            }
            else
            {
                pending.State = PendingOrderState.Rejected;
            }

            return result;
        }

        // ── 취소 ─────────────────────────────────────────────────────────────

        /// <summary>Pending 상태인 주문을 Cancelled로 전환. 이미 체결된 주문은 변경하지 않음.</summary>
        public bool CancelOrder(long orderId)
        {
            if (_pending.TryGetValue(orderId, out var order) &&
                order.State == PendingOrderState.Pending)
            {
                order.State = PendingOrderState.Cancelled;
                return true;
            }
            return false;
        }

        // ── 조회 ─────────────────────────────────────────────────────────────

        public PendingOrder? TryGetPending(long orderId)
            => _pending.TryGetValue(orderId, out var o) ? o : null;

        public IReadOnlyList<PendingOrder> GetAllOrders()
            => _pending.Values.ToArray();

        /// <summary>체결 대기 중(Pending + PartialFill) 주문 수.</summary>
        public int ActiveCount
            => _pending.Count(kvp =>
                   kvp.Value.State == PendingOrderState.Pending ||
                   kvp.Value.State == PendingOrderState.PartialFill);
    }
}
