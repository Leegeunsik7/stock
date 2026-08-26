// ============================================================================
// RetroCoach - 회고 코치 (Season 8 #4, 마지막) · 순수 결정론 행동편향 엔진 (외부 의존 0)
//  사용자의 '실제 거래 이력'을 분석해 행동 편향을 정량 탐지하고 사후 복기 코칭의 근거를 만든다.
//   · 라운드트립 페어링: 앱 전체 규약(이동평균)을 그대로 따른다(AsOfPortfolio.Reconstruct와 동일:
//     매수→원가+=p*q·수량+=q, 매도→평단 유지·원가 비례 차감). FIFO 없음.
//   · 통계는 통화 혼합을 피하려 '퍼센트' 기반(승률·평균손익%·손익비). 손익비 ∞는 999 센티넬(SignalEngine 규약).
//   · 탐지: 처분효과(승자 조기청산·패자 보유), 손익비 열위(이익 짧게/손실 길게), 과매매,
//     추격매수(직전 급등 후 매수)·패닉매도(직전 급락 후 매도)·물타기(평단 아래 추가매수).
//  ⚠️ LLM은 이 결정론 수치만 근거로 '서술'만 한다(환각 방화벽). 사후 복기·정보 제공이며 투자 권유가 아님.
// ============================================================================
using System;
using System.Collections.Generic;
using System.Linq;

namespace MS2E.Infrastructure
{
    // 코치 입력 거래(앱 Transactions에서 매핑). PriorRet5dPct = 체결 직전 5거래일 수익률(%) — 추격/패닉 판정용(없으면 null).
    public sealed record CoachTx(string Symbol, TransactionType Type, int Quantity, decimal Price, decimal Fees, DateTime ExecutedAt, double? PriorRet5dPct);

    // 닫힌 라운드트립(매도 1건). HoldingDays = 매도일 − 보유분 가중평균 진입일.
    public sealed record RealizedTrade(string Symbol, DateTime EntryAvgDate, DateTime ExitDate, int Qty,
        decimal AvgCost, decimal ExitPrice, decimal PnlNet, double PnlPct, double HoldingDays, bool IsWin);

    public sealed record CoachStats(
        int ClosedTrades, int Wins, int Losses, double WinRatePct, double ProfitFactor,
        double AvgWinPct, double AvgLossPct, double AvgHoldWinDays, double AvgHoldLossDays,
        double AvgHoldDays, double TradesPerMonth, int AveragingDownCount,
        int ChasingBuys, int ChasingBuysTotal, int PanicSells, int PanicSellsTotal, double SpanDays);

    public sealed record CoachInsight(string Key, string Title, string Severity, string Evidence, string Suggestion);

    public sealed record CoachReport(bool IsValid, CoachStats Stats, IReadOnlyList<RealizedTrade> Trades,
        IReadOnlyList<CoachInsight> Insights, string Note);

    public static class RetroCoach
    {
        public const int MinTradesForBias = 6;       // 편향 단정 최소 표본
        public const double ChaseThresholdPct = 12;  // 직전 5일 +12% 초과 후 매수 = 추격
        public const double PanicThresholdPct = -12;  // 직전 5일 −12% 미만 후 매도 = 패닉

        // ===== 라운드트립 페어링 (이동평균 · 순수) =====
        public static (List<RealizedTrade> trades, int averagingDown) Pair(IReadOnlyList<CoachTx> txs)
        {
            var trades = new List<RealizedTrade>();
            int avgDown = 0;
            if (txs == null || txs.Count == 0) return (trades, 0);

            // 종목별, 체결시각 순(동시각이면 매수 먼저 — 같은 날 매수 후 매도 가능하도록)
            foreach (var grp in txs.GroupBy(t => t.Symbol, StringComparer.OrdinalIgnoreCase))
            {
                int qty = 0; decimal cost = 0, feesAcc = 0, avgEntryTicks = 0;   // feesAcc=현 포지션 누적 매수수수료
                foreach (var t in grp.OrderBy(t => t.ExecutedAt).ThenBy(t => t.Type == TransactionType.Sell ? 1 : 0))
                {
                    if (t.Type == TransactionType.Buy)
                    {
                        if (t.Quantity <= 0 || t.Price <= 0) continue;
                        if (qty > 0)
                        {
                            decimal avgCost = cost / qty;
                            if (t.Price < avgCost) avgDown++;                       // 평단 아래 추가매수(물타기 후보)
                            avgEntryTicks = (qty * avgEntryTicks + (decimal)t.Quantity * t.ExecutedAt.Ticks) / (qty + t.Quantity);
                        }
                        else avgEntryTicks = t.ExecutedAt.Ticks;
                        cost += t.Price * t.Quantity; feesAcc += t.Fees; qty += t.Quantity;   // 원가는 그로스(앱 정합), 수수료는 별도 누적
                    }
                    else if (t.Type == TransactionType.Sell)
                    {
                        if (qty <= 0 || t.Quantity <= 0 || t.Price <= 0) continue;   // 보유 없는 매도는 무시(앱 규약)
                        int sq = Math.Min(t.Quantity, qty);
                        decimal avgCost = cost / qty;
                        decimal sellFee = t.Quantity > 0 ? t.Fees * sq / t.Quantity : 0m;   // 매도측 비례 수수료
                        decimal buyFee = qty > 0 ? feesAcc * sq / qty : 0m;                 // 매수측 비례 수수료(대칭 반영)
                        decimal proceeds = t.Price * sq - sellFee;
                        decimal basis = avgCost * sq;
                        decimal pnl = proceeds - basis - buyFee;                            // 매수·매도 수수료 모두 차감(순손익)
                        double pnlPct = basis > 0 ? (double)(pnl / basis) * 100.0 : 0;
                        var entryDate = new DateTime((long)Math.Round(avgEntryTicks), DateTimeKind.Unspecified);
                        double hold = (t.ExecutedAt - entryDate).TotalDays;
                        trades.Add(new RealizedTrade(t.Symbol, entryDate, t.ExecutedAt, sq, avgCost, t.Price, pnl, pnlPct,
                            Math.Max(0, hold), pnl > 0));
                        cost = cost * (qty - sq) / qty; feesAcc = feesAcc * (qty - sq) / qty; qty -= sq;
                        if (qty == 0) { cost = 0; feesAcc = 0; avgEntryTicks = 0; }
                    }
                }
            }
            return (trades.OrderBy(t => t.ExitDate).ToList(), avgDown);
        }

        // ===== 전체 분석 → 리포트 =====
        public static CoachReport Compute(IReadOnlyList<CoachTx> txs)
        {
            if (txs == null || txs.Count == 0)
                return new CoachReport(false, Empty(), Array.Empty<RealizedTrade>(), Array.Empty<CoachInsight>(), "거래 이력이 없습니다.");

            var (trades, avgDown) = Pair(txs);

            var actions = txs.Where(t => t.Type is TransactionType.Buy or TransactionType.Sell).ToList();
            double span = actions.Count >= 2 ? Math.Max(1, (actions.Max(a => a.ExecutedAt) - actions.Min(a => a.ExecutedAt)).TotalDays) : 1;
            double perMonth = actions.Count >= 2 ? actions.Count / (span / 30.4) : 0;   // 단일 행동은 월 환산 무의미

            int chaseBuys = actions.Count(a => a.Type == TransactionType.Buy && a.PriorRet5dPct is { } r && r > ChaseThresholdPct);
            int chaseTotal = actions.Count(a => a.Type == TransactionType.Buy && a.PriorRet5dPct.HasValue);
            int panicSells = actions.Count(a => a.Type == TransactionType.Sell && a.PriorRet5dPct is { } r && r < PanicThresholdPct);
            int panicTotal = actions.Count(a => a.Type == TransactionType.Sell && a.PriorRet5dPct.HasValue);

            var wins = trades.Where(t => t.PnlNet > 0).ToList();
            var losses = trades.Where(t => t.PnlNet < 0).ToList();   // 본전(0)은 승/패 어디에도 넣지 않음(∞ 오류 방지)
            double grossWin = wins.Sum(t => t.PnlPct);
            double grossLoss = Math.Abs(losses.Sum(t => t.PnlPct));
            double pf = grossLoss < 1e-9 ? (grossWin > 0 ? 999.0 : 0.0) : grossWin / grossLoss;

            var stats = new CoachStats(
                trades.Count, wins.Count, losses.Count,
                trades.Count > 0 ? (double)wins.Count / trades.Count * 100.0 : 0,
                pf,
                wins.Count > 0 ? wins.Average(t => t.PnlPct) : 0,
                losses.Count > 0 ? Math.Abs(losses.Average(t => t.PnlPct)) : 0,
                wins.Count > 0 ? wins.Average(t => t.HoldingDays) : 0,
                losses.Count > 0 ? losses.Average(t => t.HoldingDays) : 0,
                trades.Count > 0 ? trades.Average(t => t.HoldingDays) : 0,
                perMonth, avgDown, chaseBuys, chaseTotal, panicSells, panicTotal, span);

            var insights = Detect(stats);
            return new CoachReport(true, stats, trades, insights,
                $"닫힌 거래 {trades.Count}건 · 행동 분석은 사후 복기이며 투자 권유가 아님");
        }

        // ===== 편향 탐지 (결정론 임계) =====
        private static List<CoachInsight> Detect(CoachStats s)
        {
            var outp = new List<CoachInsight>();
            bool enough = s.ClosedTrades >= MinTradesForBias && s.Wins > 0 && s.Losses > 0;

            // 처분효과: 패자를 승자보다 오래 보유(승자 조기청산)
            if (enough && s.AvgHoldLossDays > s.AvgHoldWinDays * 1.3 && s.AvgHoldLossDays - s.AvgHoldWinDays >= 3)
                outp.Add(new CoachInsight("disposition", "처분효과(승자 조기청산·패자 장기보유)",
                    s.AvgHoldLossDays > s.AvgHoldWinDays * 1.8 ? "high" : "warning",
                    $"승자 평균 보유 {s.AvgHoldWinDays:0}일 vs 패자 {s.AvgHoldLossDays:0}일",
                    "이익은 빨리 실현하고 손실은 오래 끄는 전형적 패턴입니다. 매도 규칙(목표·손절)을 사전에 정하고 보유기간을 대칭적으로 관리해 보세요."));

            // 손익비 열위: 이익은 짧고 손실은 큼
            if (enough && s.AvgWinPct > 0 && s.AvgLossPct > 0 && s.AvgWinPct < s.AvgLossPct && s.ProfitFactor < 1.0)
                outp.Add(new CoachInsight("payoff", "손익비 열위(이익 짧게·손실 크게)", s.ProfitFactor < 0.7 ? "high" : "warning",
                    $"평균 이익 +{s.AvgWinPct:0.0}% vs 평균 손실 -{s.AvgLossPct:0.0}% · 손익비 {Pf(s.ProfitFactor)}",
                    "승률이 높아도 손익비가 1 미만이면 장기적으로 불리합니다. 손절을 더 빨리, 익절을 조금 더 길게 가져가는 비대칭을 검토하세요."));

            // 과매매 — 짧은 관측구간의 연환산 거품을 막기 위해 1개월 이상 관측일 때만 판정
            if (s.SpanDays >= 30 && s.TradesPerMonth > 8)
                outp.Add(new CoachInsight("overtrading", "과매매 경향", s.TradesPerMonth > 16 ? "high" : "warning",
                    $"월 평균 {s.TradesPerMonth:0.0}회 매매(관측 {s.SpanDays:0}일)",
                    "매매 빈도가 높을수록 수수료·세금·실수 비용이 누적됩니다. 진입 기준을 좁혀 거래 수를 줄이는 편이 유리할 수 있습니다."));

            // 추격매수 — 표본 5건 이상일 때만(소표본 오탐 방지)
            if (s.ChasingBuysTotal >= 5 && (double)s.ChasingBuys / s.ChasingBuysTotal > 0.35)
                outp.Add(new CoachInsight("chasing", "추격매수(직전 급등 후 진입)", "warning",
                    $"가격 맥락 있는 매수 {s.ChasingBuysTotal}건 중 {s.ChasingBuys}건이 직전 5일 +{ChaseThresholdPct:0}% 초과 후 진입",
                    "급등 직후 진입은 단기 고점 매수 위험이 큽니다. 분할 진입·되돌림 대기를 고려하세요."));

            // 패닉매도 — 표본 5건 이상일 때만
            if (s.PanicSellsTotal >= 5 && (double)s.PanicSells / s.PanicSellsTotal > 0.35)
                outp.Add(new CoachInsight("panic", "패닉매도(직전 급락 후 청산)", "warning",
                    $"가격 맥락 있는 매도 {s.PanicSellsTotal}건 중 {s.PanicSells}건이 직전 5일 {PanicThresholdPct:0}% 미만 후 청산",
                    "급락 직후 청산은 저점 매도가 되기 쉽습니다. 사전에 정한 손절선이 아니라면 감정적 청산인지 점검하세요."));

            // 물타기(확인 권유 — 의도적 분할매수일 수 있음)
            if (s.AveragingDownCount >= 3)
                outp.Add(new CoachInsight("averaging-down", "평단 아래 추가매수(물타기) 빈번", "info",
                    $"평단 미만 추가매수 {s.AveragingDownCount}회",
                    "계획된 분할매수라면 문제 없지만, 손실 회피를 위한 충동적 물타기라면 손실 확대 위험이 있습니다. 의도가 사전 계획이었는지 확인하세요."));

            // 긍정 강화: 규율 양호
            if (s.ClosedTrades >= MinTradesForBias && outp.Count == 0)
                outp.Add(new CoachInsight("disciplined", "규율 양호", "good",
                    $"승률 {s.WinRatePct:0}% · 손익비 {Pf(s.ProfitFactor)} · 월 {s.TradesPerMonth:0.0}회",
                    "뚜렷한 행동 편향 신호가 보이지 않습니다. 현재의 규칙 기반 매매를 유지하세요."));

            var rank = new Dictionary<string, int> { ["high"] = 0, ["warning"] = 1, ["info"] = 2, ["good"] = 3 };
            return outp.OrderBy(i => rank.TryGetValue(i.Severity, out var r) ? r : 9).ToList();
        }

        // ===== LLM 근거용 한국어 사실 직렬화 (서술은 LLM, 수치는 여기서만) =====
        public static string FactsText(CoachReport rep)
        {
            if (!rep.IsValid) return rep.Note;
            var s = rep.Stats; var sb = new System.Text.StringBuilder();
            sb.AppendLine("[거래 행동 통계]");
            sb.AppendLine($"- 닫힌 거래 {s.ClosedTrades}건(승 {s.Wins}/패 {s.Losses}), 승률 {s.WinRatePct:0.0}%");
            sb.AppendLine($"- 평균 이익 +{s.AvgWinPct:0.0}% / 평균 손실 -{s.AvgLossPct:0.0}% / 손익비 {Pf(s.ProfitFactor)}");
            sb.AppendLine($"- 평균 보유 {s.AvgHoldDays:0}일(승자 {s.AvgHoldWinDays:0}일 / 패자 {s.AvgHoldLossDays:0}일)");
            sb.AppendLine($"- 매매 빈도 월 {s.TradesPerMonth:0.0}회(관측 {s.SpanDays:0}일), 평단 아래 추가매수 {s.AveragingDownCount}회");
            if (s.ChasingBuysTotal > 0) sb.AppendLine($"- 직전 급등 후 매수 {s.ChasingBuys}/{s.ChasingBuysTotal}건, 직전 급락 후 매도 {s.PanicSells}/{s.PanicSellsTotal}건");
            sb.AppendLine("[탐지된 패턴]");
            foreach (var i in rep.Insights) sb.AppendLine($"- ({i.Severity}) {i.Title} — {i.Evidence}");
            return sb.ToString();
        }

        public static string Pf(double pf) => pf >= 999 ? "∞" : pf.ToString("0.00");
        private static CoachStats Empty() => new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
    }
}
