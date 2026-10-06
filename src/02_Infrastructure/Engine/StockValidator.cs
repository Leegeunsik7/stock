// ============================================================================
// StockManager.Common.StockValidator  (v3.65.1)
//
// ── 목적 ────────────────────────────────────────────────────────────────────
//  SQLite REAL(double) ↔ decimal 변환 경계에서 비정상값이 DB에 유입되는 것을 차단.
//  decimal 자체에는 NaN/Infinity가 없지만, 외부 시세 API(Naver/KIS/Yahoo)가
//  반환한 double NaN/Infinity를 파싱하면 Convert.ToDecimal()이 OverflowException을
//  던지거나 silently truncate될 수 있음 → 저장 직전 검증이 필수.
//
// ── 설계 원칙 ────────────────────────────────────────────────────────────────
//  1. 검증은 DB 저장 전에 호출 → 무효 데이터는 절대 디스크에 쓰지 않는다.
//  2. 오류 시 null이 아닌 에러 문자열 반환 → 호출자가 rollback/throw 처리.
//  3. decimal.GetBits()로 내부 비트 패턴 이상 감지 (double→decimal 캐스트 오염).
//  4. TransactionType별 비즈니스 규칙 적용 (Buy/Sell ≠ Dividend).
// ============================================================================
using System;
using System.Collections.Generic;

namespace MS2E.Infrastructure
{
    /// <summary>단일 검증 오류</summary>
    public record ValidationError(string Field, string Message);

    /// <summary>검증 결과 — 통과면 IsValid=true, 실패면 Errors에 내용</summary>
    public class ValidationResult
    {
        public bool IsValid => Errors.Count == 0;
        public List<ValidationError> Errors { get; } = new();

        public void Add(string field, string msg) => Errors.Add(new(field, msg));
        public string Summary() => string.Join("; ", Errors);
    }

    public static class StockValidator
    {
        // ── Transaction 검증 ─────────────────────────────────────────────────

        /// <summary>
        /// Transaction을 DB에 저장하기 전에 호출.
        /// null이 아닌 경우 즉시 rollback/throw.
        /// </summary>
        public static ValidationResult ValidateTransaction(Transaction tx)
        {
            var r = new ValidationResult();

            if (tx is null)                              { r.Add("tx", "null 거래 객체"); return r; }
            if (string.IsNullOrWhiteSpace(tx.Symbol))   r.Add(nameof(tx.Symbol), "종목코드 없음");
            if (tx.ExecutedAt == default)               r.Add(nameof(tx.ExecutedAt), "거래일시 없음");

            switch (tx.Type)
            {
                case TransactionType.Buy:
                case TransactionType.Sell:
                    // 수량 + 단가 둘 다 양수 필수
                    if (tx.Quantity <= 0)
                        r.Add(nameof(tx.Quantity), $"수량은 양수여야 합니다: {tx.Quantity}");
                    if (!IsValidDecimal(tx.Price) || tx.Price <= 0)
                        r.Add(nameof(tx.Price), $"단가가 유효하지 않음: {tx.Price}");
                    break;

                case TransactionType.Dividend:
                    // 주당 배당금 ≥ 0
                    if (!IsValidDecimal(tx.Price) || tx.Price < 0)
                        r.Add(nameof(tx.Price), $"배당금이 음수: {tx.Price}");
                    break;

                case TransactionType.Deposit:
                case TransactionType.Withdraw:
                    // 입출금 금액은 Price에 담겨 있고 양수
                    if (!IsValidDecimal(tx.Price) || tx.Price <= 0)
                        r.Add(nameof(tx.Price), $"입출금 금액 오류: {tx.Price}");
                    break;
            }

            // 공통 — 수수료·세금은 음수 불가
            if (!IsValidDecimal(tx.Commission) || tx.Commission < 0)
                r.Add(nameof(tx.Commission), $"수수료 음수: {tx.Commission}");
            if (!IsValidDecimal(tx.Tax) || tx.Tax < 0)
                r.Add(nameof(tx.Tax), $"세금 음수: {tx.Tax}");

            return r;
        }

        // ── Holding 검증 ─────────────────────────────────────────────────────

        /// <summary>보유 종목을 DB에 저장하기 전에 호출.</summary>
        public static ValidationResult ValidateHolding(Holding h)
        {
            var r = new ValidationResult();

            if (h is null)                             { r.Add("h", "null 보유 객체"); return r; }
            if (string.IsNullOrWhiteSpace(h.Symbol))   r.Add(nameof(h.Symbol), "종목코드 없음");
            if (h.Quantity < 0)                        r.Add(nameof(h.Quantity), $"수량 음수: {h.Quantity}");
            if (!IsValidDecimal(h.AvgPrice) || h.AvgPrice < 0)
                r.Add(nameof(h.AvgPrice), $"평균단가 오류: {h.AvgPrice}");
            if (!IsValidDecimal(h.CurrentPrice) || h.CurrentPrice < 0)
                r.Add(nameof(h.CurrentPrice), $"현재가 오류: {h.CurrentPrice}");

            return r;
        }

        // ── 시세 Quote 핵심 필드 검증 ────────────────────────────────────────

        /// <summary>QuoteProvider가 반환한 시세를 캐시/DB에 쓰기 전에 호출.</summary>
        public static ValidationResult ValidateQuotePrice(Quote q)
        {
            var r = new ValidationResult();
            if (q is null) { r.Add("q", "null 시세 객체"); return r; }

            if (!IsValidDecimal(q.Price) || q.Price < 0)
                r.Add(nameof(q.Price), $"현재가 오류: {q.Price}");
            if (!IsValidDecimal(q.PreviousClose) || q.PreviousClose < 0)
                r.Add(nameof(q.PreviousClose), $"전일종가 오류: {q.PreviousClose}");

            return r;
        }

        // ── 일괄 검증 ─────────────────────────────────────────────────────────

        /// <summary>
        /// 여러 Transaction을 한 번에 검증.
        /// 오류가 하나라도 있으면 전체 배치를 거부 (원자성 보장).
        /// </summary>
        public static ValidationResult ValidateBatch(IEnumerable<Transaction> txList)
        {
            var r = new ValidationResult();
            int idx = 0;
            foreach (var tx in txList)
            {
                var sub = ValidateTransaction(tx);
                if (!sub.IsValid)
                    foreach (var e in sub.Errors)
                        r.Add($"[{idx}].{e.Field}", e.Message);
                idx++;
            }
            return r;
        }

        // ── decimal 비정상값 감지 ─────────────────────────────────────────────
        //
        //  double.NaN/Infinity를 (decimal)로 캐스트하면:
        //   · 명시적 캐스트 → OverflowException 발생 (잡힌 경우 기본값 0이 돼 무음 오염 발생)
        //   · 이미 오염된 decimal이 DB에서 읽혀왔을 때 비트 패턴 검증
        //
        //  GetBits()는 [0]=Lo, [1]=Mid, [2]=Hi, [3]=Flags(sign+scale) 를 반환.
        //  정상 decimal은 Flags의 상위 1비트(sign)와 scale(bits 16-23)만 사용.
        //  scale > 28이면 비정상 (decimal 표준 최대 28).
        //
        private static bool IsValidDecimal(decimal d)
        {
            try
            {
                int[] bits = decimal.GetBits(d);
                int flags  = bits[3];
                int scale  = (flags >> 16) & 0xFF;
                return scale <= 28;
            }
            catch
            {
                return false;
            }
        }

        // ── 브릿지 페이로드 검증 (S14 #1) ────────────────────────────────────

        /// <summary>KiwoomBridgeClient 수신 실시간 틱 검증.</summary>
        public static ValidationResult ValidateTick(TickPayload tick)
        {
            var r = new ValidationResult();
            if (tick.SymbolCode <= 0 || tick.SymbolCode > 999_999)
                r.Add(nameof(tick.SymbolCode), $"KSE 코드 범위 오류: {tick.SymbolCode}");
            if (tick.Price <= 0)
                r.Add(nameof(tick.Price), $"현재가는 양수여야 합니다: {tick.Price}");
            if (tick.Volume < 0)
                r.Add(nameof(tick.Volume), $"거래량 음수: {tick.Volume}");
            return r;
        }

        /// <summary>KiwoomBridgeClient 수신 체결 검증.</summary>
        /// <summary>[키움 프로토콜] 체결량 0 주문 상태 통보(접수/거부/취소확인) 형식 검증 — 체결가 0 허용 · 수량은 정확히 0.</summary>
        public static ValidationResult ValidateOrderNotice(FillPayload fill)
        {
            var r = new ValidationResult();
            if (fill.SymbolCode <= 0 || fill.SymbolCode > 999_999)
                r.Add(nameof(fill.SymbolCode), $"KSE 코드 범위 오류: {fill.SymbolCode}");
            if (fill.FillQty != 0)
                r.Add(nameof(fill.FillQty), $"상태 통보 수량은 0 이어야 합니다: {fill.FillQty}");
            if (!double.IsFinite(fill.FillPrice) || fill.FillPrice < 0)
                r.Add(nameof(fill.FillPrice), $"상태 통보 가격 비정상: {fill.FillPrice}");
            if (fill.Side != 1 && fill.Side != -1)
                r.Add(nameof(fill.Side), $"매매방향은 +1(매수)/-1(매도)이어야 합니다: {fill.Side}");
            return r;
        }

        public static ValidationResult ValidateFill(FillPayload fill)
        {
            var r = new ValidationResult();
            if (fill.SymbolCode <= 0 || fill.SymbolCode > 999_999)
                r.Add(nameof(fill.SymbolCode), $"KSE 코드 범위 오류: {fill.SymbolCode}");
            if (fill.FillPrice <= 0)
                r.Add(nameof(fill.FillPrice), $"체결가는 양수여야 합니다: {fill.FillPrice}");
            if (fill.FillQty < 0)
                r.Add(nameof(fill.FillQty), $"체결수량 음수: {fill.FillQty}");
            if (fill.Side != 1 && fill.Side != -1)
                r.Add(nameof(fill.Side), $"매매방향은 +1(매수)/-1(매도)이어야 합니다: {fill.Side}");
            return r;
        }

        // ── 사용 패턴 (Database.cs 통합 예시) ──────────────────────────────
        //
        //  // 기존 SaveTransaction() 메서드 앞에 삽입:
        //  public int SaveTransaction(Transaction tx)
        //  {
        //      var v = StockValidator.ValidateTransaction(tx);
        //      if (!v.IsValid)
        //          throw new ArgumentException($"거래 검증 실패: {v.Summary()}");
        //
        //      return Execute(
        //          "INSERT INTO Transactions (Symbol,Type,Quantity,Price,Commission,Tax,ExecutedAt,BrokerAccount,Memo) " +
        //          "VALUES (@sym,@type,@qty,@price,@comm,@tax,@at,@acct,@memo)",
        //          new() { ... });
        //  }
    }
}
