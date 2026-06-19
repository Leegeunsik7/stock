// ============================================================================
// StockManager.Common.FraudAlert  (v3.92.0)
//
// 거래 검증 실패(부정거래 시도) 및 외부 경보 소스 기록 모델.
// LocalDatabase._WriteFraudAlert() — 트랜잭션 검증 실패 자동 기록
// LocalDatabase.AddFraudAlert()    — SlippageSentinel 등 외부 경보 소스 직접 기록
// ============================================================================
namespace StockManager.Common
{
    /// <summary>감사 원장 1행 (FraudAlerts 테이블)</summary>
    public class FraudAlert
    {
        public int       Id              { get; set; }
        public DateTime  AttemptedAt     { get; set; }
        public string    Symbol          { get; set; } = "";
        public string    TransactionType { get; set; } = "";  // 경보 소스 or 거래 유형
        public string    Reason          { get; set; } = "";  // 상세 메시지
        public string    RawJson         { get; set; } = "";  // 거래 원본 직렬화 (내부 경보)
        public bool      IsReviewed      { get; set; }
        public string    Severity        { get; set; } = "HIGH";   // HIGH / MEDIUM / LOW
        public DateTime? ReviewedAt      { get; set; }             // 검토 완료 시각 (UTC)
    }
}
