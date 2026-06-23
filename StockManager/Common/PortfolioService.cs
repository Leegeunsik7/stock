// ============================================================================
// PortfolioService - 거래 기록 → 보유 평단 재계산 (Blazor/WinForms 공용) (v3.4.2)
//  원본 02_Portfolio.MainForm.RecomputeAverage 이식 — 숨은 룰 보존:
//   · 전체 거래 이력을 시간순으로 재집계하는 이동평균법 (매도 시 평단 불변·원가 비례 차감)
//   · 보유 0 상태의 매도 거래는 무시 (이상 데이터 가드)
//   · 전량 매도 시 cost=0, qty=0 (0/0 방지) → 평단 0
//   · 기존 Holding의 Name/Market/Currency/BrokerAccount 보존
// ============================================================================
using System;

namespace StockManager.Common
{
    public static class PortfolioService
    {
        /// <summary>거래 추가 후 반드시 호출 — 거래 이력 전체로 해당 종목의 수량·평단을 재계산해 저장.</summary>
        public static void RecomputeAverage(LocalDatabase db, string symbol, string? fallbackName = null,
            Market? marketHint = null, Currency? currencyHint = null)
        {
            var dt = db.Query(
                "SELECT * FROM Transactions WHERE Symbol=@s ORDER BY ExecutedAt",
                new() { ["@s"] = symbol });

            decimal qty = 0;          // v4.30.0 int→decimal (코인 소수점 수량)
            decimal cost = 0;
            foreach (System.Data.DataRow r in dt.Rows)
            {
                var t = (TransactionType)Safe.Int(r["Type"]);
                var q = Safe.Dec(r["Quantity"]);
                var p = Safe.Dec(r["Price"]);
                if (t == TransactionType.Buy)
                {
                    cost += p * q;
                    qty += q;
                }
                else if (t == TransactionType.Sell)
                {
                    if (qty <= 0) continue;               // 매도할 수량 없음 (이상)
                    // v4.30.0 decimal — 전량(또는 초과) 매도 시 평탄. '==' 정확일치는 double 라운드트립 후
                    //   비트 불일치로 dust 잔량(예: 1e-16)을 남겨 'Quantity>0' 보유 목록에 유령 행을 띄울 수 있어
                    //   '>=' 로 견고화(AsOfPortfolio.Reconstruct 와 동일 규약).
                    if (q >= qty) { cost = 0; qty = 0; }
                    else
                    {
                        cost = cost * (qty - q) / qty;    // 이동평균법: 평단 유지, 원가 비례 차감
                        qty -= q;
                    }
                }
            }

            var avg = qty > 0 ? cost / qty : 0;
            var dtH = db.Query("SELECT * FROM Holdings WHERE Symbol=@s LIMIT 1", new() { ["@s"] = symbol });
            bool has = dtH.Rows.Count > 0;
            // 새 보유의 시장/통화: 픽커가 준 힌트(권위값) 우선, 없으면 심볼로 추론.
            //   코인('KRW-' 접두, 예 KRW-BTC) → CRYPTO/KRW. 6자리 숫자 → 코스피/KRW. 그 외 → 미국/USD.
            //   (이전엔 'KRW-BTC'가 6자리 아님→US_NASDAQ로 오분류돼 코인이 미국주식으로 저장되던 버그를 제거)
            bool crypto = symbol.StartsWith("KRW-", StringComparison.OrdinalIgnoreCase);
            bool krx = !crypto && symbol.Length == 6 && char.IsDigit(symbol[0]);
            Market   inferMkt = crypto ? Market.CRYPTO : krx ? Market.KRX_KOSPI : Market.US_NASDAQ;
            Currency inferCcy = (crypto || krx) ? Currency.KRW : Currency.USD;

            var h = new Holding
            {
                Symbol = symbol,
                Name = has ? Safe.Str(dtH.Rows[0]["Name"])
                           : (string.IsNullOrWhiteSpace(fallbackName) ? symbol : fallbackName!.Trim()),
                Market = has ? (Market)Safe.Int(dtH.Rows[0]["Market"])
                             : (marketHint ?? inferMkt),
                Currency = has ? (Currency)Safe.Int(dtH.Rows[0]["Currency"])
                               : (currencyHint ?? inferCcy),
                Quantity = qty,
                AvgPrice = avg,
                // 원본은 CurrentPrice를 0으로 덮고 직후 시세 갱신에 의존했음 —
                // 시세 조회 실패 시 평가액이 0이 되는 회귀를 막기 위해 저장된 현재가를 보존
                CurrentPrice = has ? Safe.Dec(dtH.Rows[0]["CurrentPrice"]) : 0m,
                BrokerAccount = has ? Safe.Str(dtH.Rows[0]["BrokerAccount"]) : "",
            };
            db.SaveHolding(h);
        }
    }
}
