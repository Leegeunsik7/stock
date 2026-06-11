// ============================================================================
// PortfolioContext - LLM 근거 데이터 어셈블러 (환각 방지의 핵심)
//  로컬 DB에서 포트폴리오·시그널·거래를 C#으로 '정확히' 계산해 텍스트 블록으로 직렬화.
//  AiService.AskAsync / MorningBriefAsync가 이 블록을 프롬프트에 주입하고,
//  LLM은 이 안의 수치만 사용하도록 지시받는다(숫자 환각 차단).
// ============================================================================
using System;
using System.Data;
using System.Linq;
using System.Text;

namespace StockManager.Common
{
    public static class PortfolioContext
    {
        /// <summary>현재 포트폴리오/시그널/거래를 LLM 근거용 한국어 텍스트로 직렬화.</summary>
        public static string Build(LocalDatabase db, decimal usdKrw)
        {
            var sb = new StringBuilder();
            var holdings = db.GetAllHoldings();

            decimal total = 0, cost = 0;
            foreach (var h in holdings)
            {
                var fx = h.Currency == Currency.USD ? usdKrw : 1m;
                total += h.CurrentPrice * h.Quantity * fx;
                cost += h.AvgPrice * h.Quantity * fx;
            }
            var pnl = total - cost;
            var ret = cost > 0 ? pnl / cost * 100m : 0m;

            sb.AppendLine($"[포트폴리오] 환율 USD/KRW {usdKrw:N0}");
            sb.AppendLine($"총 평가액 {total:N0}원 · 평가손익 {pnl:+#,##0;-#,##0;0}원 ({ret:+0.0;-0.0}%) · 보유 {holdings.Count}종목");

            if (holdings.Count > 0)
            {
                sb.AppendLine("보유 종목(평가손익 내림차순):");
                foreach (var h in holdings.OrderByDescending(x => x.UnrealizedPnL))
                {
                    var fx = h.Currency == Currency.USD ? usdKrw : 1m;
                    var pnlKrw = (h.CurrentPrice - h.AvgPrice) * h.Quantity * fx;
                    var hret = h.AvgPrice > 0 ? (h.CurrentPrice - h.AvgPrice) / h.AvgPrice * 100m : 0m;
                    sb.AppendLine($"- {h.Name}({h.Symbol}) {h.Quantity}주 · 평단 {h.AvgPrice:N0} · 현재 {h.CurrentPrice:N0} · 손익 {pnlKrw:+#,##0;-#,##0;0}원({hret:+0.0;-0.0}%)");
                }
            }
            else sb.AppendLine("(보유 종목 없음)");

            // 최근 시그널 (DB Signals)
            try
            {
                var dt = db.Query("SELECT * FROM Signals ORDER BY GeneratedAt DESC LIMIT 8");
                if (dt.Rows.Count > 0)
                {
                    sb.AppendLine("[최근 시그널]");
                    foreach (DataRow r in dt.Rows)
                        sb.AppendLine($"- {Safe.DT(r["GeneratedAt"]):MM-dd HH:mm} {Safe.Str(r["Strategy"])} · {Safe.Str(r["Name"])}({Safe.Str(r["Symbol"])}) · 신뢰 {Safe.Dec(r["Confidence"]):0}");
                }
            }
            catch { }

            // 최근 거래 (DB Transactions)
            try
            {
                var dt = db.Query("SELECT * FROM Transactions ORDER BY ExecutedAt DESC LIMIT 8");
                if (dt.Rows.Count > 0)
                {
                    sb.AppendLine("[최근 거래]");
                    foreach (DataRow r in dt.Rows)
                    {
                        var type = (TransactionType)Safe.Int(r["Type"]);
                        sb.AppendLine($"- {Safe.DT(r["ExecutedAt"]):MM-dd} {type} {Safe.Str(r["Symbol"])} {Safe.Int(r["Quantity"])}주 @{Safe.Dec(r["Price"]):N0}");
                    }
                }
            }
            catch { }

            return sb.ToString();
        }
    }
}
