// ============================================================================
// CounterfactualTwin - 반사실 디지털 트윈 🄰 (외부 의존 0, 입력은 in-memory) (v3.21.0)
//  "내가 규칙을 지켰다면?" — 같은 종목·같은 창에서 세 트랙의 수익률을 비교해
//  행동 격차(behavior gap)를 정량화한다:
//   · buyhold : 시작 시점에 사서 끝까지 보유
//   · signal  : 내 시그널(Signals)을 그대로 추종(전액 진입/청산)
//   · actual  : 내 실제 거래(Transactions) 그대로
//  ReplayService가 "시그널을 따랐나?"만 봤다면, 이건 또 다른 나의 완전한 자산곡선을 만든다.
//  순수·결정적. 비용은 공용 TradingCost.Default(수수료·세금·슬리피지) 재사용.
//
//  설계 노트 / 숨은 룰:
//   · 세 트랙 모두 % 수익률로 비교(스케일 불변) — actual은 외부 현금유입이 있어 정규화 곡선 대신 %로
//   · 정규화 곡선(시작 100)은 buyhold·signal만 제공(상호 비교 가능)
//   · signal: 같은 날 여러 시그널이면 나중 것이 우선. 매수=StrongBuy/Buy, 매도=Sell/StrongSell
//   · actual: 이동평균 원가 무관(매수원가 합·매도대금 합·잔량 종가평가로 손익률 산출)
//   · 결정적: 동일 입력 → 동일 결과(난수 없음)
// ============================================================================
using System;
using System.Collections.Generic;
using System.Linq;

namespace MS2E.Infrastructure
{
    public class TwinReport
    {
        public bool IsValid { get; set; }
        public string Note { get; set; } = "";

        public decimal BuyHoldReturnPct { get; set; }
        public decimal SignalReturnPct { get; set; }
        public int SignalTrades { get; set; }
        public bool SignalEntered { get; set; }

        public decimal ActualReturnPct { get; set; }
        public int ActualTrades { get; set; }
        public bool HasActual { get; set; }

        public decimal GapVsSignal => HasActual && SignalEntered ? ActualReturnPct - SignalReturnPct : 0;
        public decimal GapVsHold => HasActual ? ActualReturnPct - BuyHoldReturnPct : 0;

        public List<TwinPoint> Curve { get; set; } = new();   // 정규화(시작 100): hold·signal
        public string Verdict { get; set; } = "";
    }

    public record TwinPoint(string Date, decimal Hold, decimal Signal);

    public static class CounterfactualTwin
    {
        public static TwinReport Compare(
            List<(DateTime date, bool isBuy, decimal qty, decimal price)> actualTrades,
            List<(DateTime date, SignalType type)> signals,
            List<Candle> candles, Market market)
        {
            var rep = new TwinReport();
            if (candles == null || candles.Count < 2) { rep.Note = "캔들 부족(2개+)"; return rep; }

            var cs = candles.OrderBy(c => c.Date).ToList();
            var cost = TradingCost.Default;
            decimal close0 = cs[0].Close, closeN = cs[^1].Close;
            if (close0 <= 0) { rep.Note = "시작 종가 오류"; return rep; }

            // ── 시그널 액션 맵(날짜→ +1 매수 / -1 매도), 나중 시그널 우선 ──
            var action = new Dictionary<DateTime, int>();
            foreach (var s in (signals ?? new()).OrderBy(s => s.date))
            {
                int a = s.type is SignalType.StrongBuy or SignalType.Buy ? 1
                      : s.type is SignalType.Sell or SignalType.StrongSell ? -1 : 0;
                if (a != 0) action[s.date.Date] = a;
            }

            // ── 트랙 시뮬: buyhold(정규화) + signal(전액 진입/청산) ──
            decimal seed = 1_000_000m, cash = seed, qty = 0m;
            int sigTrades = 0; bool entered = false;
            foreach (var c in cs)
            {
                if (action.TryGetValue(c.Date.Date, out var act))
                {
                    if (act == 1 && cash > 0)
                    {
                        var exec = c.Close * (1 + cost.SlippageRate);
                        var commission = cost.Commission(market, cash);
                        qty = (cash - commission) / exec; cash = 0; sigTrades++; entered = true;
                    }
                    else if (act == -1 && qty > 0)
                    {
                        var exec = c.Close * (1 - cost.SlippageRate);
                        var amount = qty * exec;
                        cash = amount - cost.Commission(market, amount) - cost.SellTax(market, amount);
                        qty = 0; sigTrades++;
                    }
                }
                decimal sigEquity = cash + qty * c.Close;
                rep.Curve.Add(new TwinPoint(c.Date.ToString("yyyy-MM-dd"),
                    Math.Round(c.Close / close0 * 100m, 2),
                    Math.Round(sigEquity / seed * 100m, 2)));
            }
            decimal finalSig = cash + qty * closeN;
            rep.BuyHoldReturnPct = (closeN - close0) / close0 * 100m;
            rep.SignalReturnPct = (finalSig - seed) / seed * 100m;
            rep.SignalTrades = sigTrades;
            rep.SignalEntered = entered;

            // ── actual: 실제 거래 손익률 ──
            decimal buyCost = 0, sellProceeds = 0, pos = 0;
            foreach (var t in (actualTrades ?? new()).OrderBy(t => t.date))
            {
                if (t.isBuy) { buyCost += t.qty * t.price; pos += t.qty; }
                else { sellProceeds += t.qty * t.price; pos -= t.qty; }
            }
            rep.ActualTrades = (actualTrades?.Count ?? 0);
            if (buyCost > 0)
            {
                rep.HasActual = true;
                decimal endValue = Math.Max(0, pos) * closeN;
                rep.ActualReturnPct = (sellProceeds + endValue - buyCost) / buyCost * 100m;
            }

            rep.IsValid = true;
            rep.Verdict = BuildVerdict(rep);
            rep.Note = $"{cs.Count}봉 · {cs[0].Date:yyyy-MM-dd}~{cs[^1].Date:yyyy-MM-dd}";
            return rep;
        }

        private static string BuildVerdict(TwinReport r)
        {
            if (!r.HasActual)
                return r.SignalEntered
                    ? $"실제 거래 기록이 없어 시그널 추종({r.SignalReturnPct:+0.0;-0.0}%) vs 단순보유({r.BuyHoldReturnPct:+0.0;-0.0}%)만 비교합니다."
                    : "실제 거래·유효 시그널이 모두 없어 단순보유만 표시합니다.";

            if (r.SignalEntered && r.SignalReturnPct > r.ActualReturnPct + 1m)
                return $"🤖 내 시그널을 규칙대로 따랐다면 {r.GapVsSignal:+0.0;-0.0}%p 더 나았습니다 — 행동 격차.";
            if (r.ActualReturnPct > r.BuyHoldReturnPct + 1m)
                return $"👍 단순 보유보다 {r.GapVsHold:+0.0;-0.0}%p 잘했습니다.";
            if (r.ActualReturnPct < r.BuyHoldReturnPct - 1m)
                return $"⚠️ 그냥 보유만 했어도 {(-r.GapVsHold):+0.0;-0.0}%p 더 나았습니다(과잉매매 가능성).";
            return "≈ 단순 보유와 큰 차이가 없습니다.";
        }
    }
}
