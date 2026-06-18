// ============================================================================
// LiveTradingModels — S14 #2 실거래 체결 엔진 공유 모델
//
//  PendingOrder  : PlaceOrder 후 체결 대기 중인 주문 상태 레코드.
//  FillAuditResult: ProcessFill() 감사 결과.
// ============================================================================
using System;

namespace StockManager.Common
{
    public enum PendingOrderState
    {
        Pending,
        PartialFill,
        Filled,
        Rejected,
        Cancelled,
    }

    public sealed class PendingOrder
    {
        public long              OrderId       { get; init; }
        public string            Symbol        { get; init; } = "";
        public PaperSide         Side          { get; init; }
        public PaperKind         Kind          { get; init; }
        /// <summary>0.0 = 시장가. 지정가인 경우 원 단위 가격.</summary>
        public double            ExpectedPrice { get; init; }
        public long              Quantity      { get; init; }
        public long              GenomeId      { get; init; }
        public DateTime          SubmittedAt   { get; init; }

        public PendingOrderState State     { get; set; } = PendingOrderState.Pending;
        public long              FilledQty { get; set; }

        public long RemainingQty => Quantity - FilledQty;
    }

    /// <summary>OrderAttestationService.ProcessFill() 반환값.</summary>
    public sealed record FillAuditResult(
        bool    Passed,
        string  Reason,
        double  PriceDeltaPct,   // |fill - expected| / expected (시장가=0)
        long    QtyDelta);       // fillQty - expectedQty (음수=부분체결)
}
