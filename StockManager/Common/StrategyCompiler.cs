// ============================================================================
// StrategyCompiler - LLM 전략 컴파일러 (Season 8 #3) · 순수 결정론 코어 (외부 의존 0)
//  자연어 → 선언적 StrategySpec → 결정론 백테스트 → 서명 카트리지.
//  ⚠️ 핵심 원칙(환각 방화벽): LLM은 '스펙 JSON'만 제안할 뿐 신뢰하지 않는다. 이 C# 컴파일러가
//     파싱·검증·실행의 단일 진실 원천이다. LLM이 없어도 순수 규칙 파서(Parse)로 오프라인 동작한다.
//   · Spec→Backtest는 난수·시계 없음(결정론). 비용모델(슬리피지·수수료·매도세)은 TradingCost.Default를 그대로 재현.
//     단 승률·샤프는 '자본곡선' 기준이라 BacktestEngine(거래단위 기준)과 산출 방식이 달라 수치가 다를 수 있음.
//   · 지표는 IndicatorMath(차트 RSI/EMA/MACD/%B)를 사용한다고 스펙에 못박는다(엔진 내부 RSI와 섞지 않음).
//   · Canonical(spec)는 머신 독립 정준 문자열 → 카트리지 CartPrompt에 실어 서명·교환·재실행 검증 가능.
//  정보 제공·과거 백테스트이며 예측/투자 권유가 아님.
// ============================================================================
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace StockManager.Common
{
    public enum SpecKind { Price, Sma, Ema, Rsi, MacdHist, PctB }     // 지표 항(Term)
    public enum SpecOp { CrossAbove, CrossBelow, Gt, Lt }              // 비교 연산
    public enum SpecLogic { And, Or }

    // 한 항: 지표 종류 + 파라미터(기간). Price는 Param 무시.
    public sealed record SpecTerm(SpecKind Kind, int Param)
    {
        public string Describe() => Kind switch
        {
            SpecKind.Price => "종가",
            SpecKind.Sma => $"SMA{Param}",
            SpecKind.Ema => $"EMA{Param}",
            SpecKind.Rsi => $"RSI{Param}",
            SpecKind.MacdHist => "MACD히스토",
            SpecKind.PctB => $"%B{Param}",
            _ => Kind.ToString()
        };
    }

    // 한 조건: Left (op) Right. Right는 상수(RightConst) 또는 다른 항.
    public sealed record SpecCond(SpecTerm Left, SpecOp Op, bool RightIsConst, double Const, SpecTerm? Right)
    {
        public string OpKr => Op switch
        {
            SpecOp.CrossAbove => "상향돌파", SpecOp.CrossBelow => "하향돌파",
            SpecOp.Gt => ">", SpecOp.Lt => "<", _ => Op.ToString()
        };
        public string Describe() =>
            $"{Left.Describe()} {OpKr} {(RightIsConst ? Const.ToString("0.###", CultureInfo.InvariantCulture) : Right!.Describe())}";
    }

    public sealed record StrategySpec(
        string Id, string Title,
        IReadOnlyList<SpecCond> Entry, SpecLogic EntryLogic,
        IReadOnlyList<SpecCond> Exit, SpecLogic ExitLogic,
        double StopLossPct, double TakeProfitPct);

    public sealed record CompileResult(bool IsValid, StrategySpec? Spec, string Source, string Note);

    public static class StrategyCompiler
    {
        public const int MinPeriod = 2, MaxPeriod = 200;
        public const int SeedEquity = 1_000_000;

        // ===== 검증 (LLM·규칙 파서 공통 게이트) =====
        public static (bool ok, string error) Validate(StrategySpec? s)
        {
            if (s == null) return (false, "스펙이 비었습니다.");
            if (s.Entry.Count == 0) return (false, "진입 조건이 최소 1개 필요합니다.");
            if (s.Exit.Count == 0 && s.StopLossPct <= 0 && s.TakeProfitPct <= 0)
                return (false, "청산 조건(또는 손절·익절)이 최소 1개 필요합니다.");
            foreach (var c in s.Entry.Concat(s.Exit))
            {
                var (tok, te) = ValidTerm(c.Left); if (!tok) return (false, te);
                if (!c.RightIsConst)
                {
                    if (c.Right == null) return (false, "비교 대상이 없습니다.");
                    var (rok, re) = ValidTerm(c.Right); if (!rok) return (false, re);
                }
                else if (double.IsNaN(c.Const) || double.IsInfinity(c.Const) || Math.Abs(c.Const) > 1e12) return (false, "상수 값이 올바르지 않습니다.");
            }
            if (s.StopLossPct < 0 || s.StopLossPct > 95) return (false, "손절 % 범위(0~95)를 벗어났습니다.");
            if (s.TakeProfitPct < 0 || s.TakeProfitPct > 1000) return (false, "익절 % 범위(0~1000)를 벗어났습니다.");
            return (true, "");
        }

        private static (bool, string) ValidTerm(SpecTerm t)
        {
            if (t.Kind is SpecKind.Price or SpecKind.MacdHist) return (true, "");
            if (t.Param < MinPeriod || t.Param > MaxPeriod) return (false, $"{t.Describe()} 기간 범위({MinPeriod}~{MaxPeriod})를 벗어났습니다.");
            return (true, "");
        }

        // ===== 결정론 백테스트 (TradingCost.Default 재현 · 난수/시계 없음) =====
        //  long-only, 단일 포지션, all-in(정수 수량), 진입=종가×1.001 / 청산=종가×0.999(역슬리피지),
        //  KR/US 수수료·매도세, 종료 시 강제 청산. EquityCurve는 워밍업 이후 매 봉 평가액.
        public static BacktestSummary Backtest(StrategySpec spec, List<Candle> candles, Market market)
        {
            var sum = new BacktestSummary();
            if (candles == null || candles.Count < 5) return sum;
            var closes = candles.Select(c => c.Close).ToList();

            int maxP = MaxParam(spec);
            int warm = Math.Min(Math.Max(maxP + 1, 1), candles.Count - 1);

            // 필요한 지표 시리즈 사전 계산(결정론·IndicatorMath)
            var series = BuildSeries(spec, candles);

            var cost = TradingCost.Default;
            decimal cash = SeedEquity, qty = 0; bool inPos = false; decimal entryPx = 0;
            int trades = 0, wins = 0; decimal cashBeforeEntry = 0;   // 승패 기준=진입 전 현금(매수·매도 비용 전부 반영)
            var curve = new List<decimal>();

            for (int i = warm; i < candles.Count; i++)
            {
                decimal close = closes[i];
                // 청산 우선(손절·익절·청산조건)
                if (inPos)
                {
                    bool stop = spec.StopLossPct > 0 && close <= entryPx * (1m - (decimal)spec.StopLossPct / 100m);
                    bool take = spec.TakeProfitPct > 0 && close >= entryPx * (1m + (decimal)spec.TakeProfitPct / 100m);
                    bool exitSig = spec.Exit.Count > 0 && EvalAll(spec.Exit, spec.ExitLogic, series, i);
                    if (stop || take || exitSig)
                    {
                        decimal exec = close * 0.999m;                  // 역슬리피지(매도)
                        decimal gross = qty * exec;
                        decimal fee = cost.Commission(market, gross) + cost.SellTax(market, gross);
                        cash += gross - fee;
                        if (cash > cashBeforeEntry) wins++;             // 라운드트립 순손익 기준(전 비용 반영)
                        trades++; inPos = false; qty = 0;
                    }
                }
                // 진입
                if (!inPos && EvalAll(spec.Entry, spec.EntryLogic, series, i))
                {
                    decimal exec = close * 1.001m;                      // 역슬리피지(매수)
                    long n = (long)(cash / exec);                       // all-in, 정수 수량
                    while (n > 0 && n * exec + cost.Commission(market, n * exec) > cash) n--;   // 수수료 포함 현금 음수 방지
                    if (n > 0)
                    {
                        decimal gross = n * exec;
                        decimal fee = cost.Commission(market, gross);
                        cashBeforeEntry = cash;                         // 진입 전 현금 고정
                        cash -= gross + fee; qty = n; inPos = true; entryPx = close;
                    }
                }
                curve.Add(inPos ? cash + qty * close : cash);
            }
            // 종료 시 강제 청산
            if (inPos)
            {
                decimal close = closes[^1], exec = close * 0.999m, gross = qty * exec;
                decimal fee = cost.Commission(market, gross) + cost.SellTax(market, gross);
                cash += gross - fee; if (cash > cashBeforeEntry) wins++; trades++;
                if (curve.Count > 0) curve[^1] = cash;
            }

            sum.EquityCurve = curve;
            sum.TradeCount = trades;
            sum.TotalReturn = (cash / SeedEquity - 1m) * 100m;
            sum.WinRate = trades > 0 ? (decimal)wins / trades * 100m : 0m;
            sum.MaxDrawdown = MaxDrawdown(curve);
            sum.Sharpe = SharpeOf(curve);
            return sum;
        }

        // ===== Spec → 사람이 읽는 설명 =====
        public static string Describe(StrategySpec s)
        {
            string J(SpecLogic l) => l == SpecLogic.And ? " 그리고 " : " 또는 ";
            var sb = new StringBuilder();
            sb.Append("진입: ").Append(string.Join(J(s.EntryLogic), s.Entry.Select(c => c.Describe())));
            if (s.Exit.Count > 0) sb.Append(" / 청산: ").Append(string.Join(J(s.ExitLogic), s.Exit.Select(c => c.Describe())));
            if (s.StopLossPct > 0) sb.Append($" / 손절 -{s.StopLossPct.ToString("0.#", CultureInfo.InvariantCulture)}%");
            if (s.TakeProfitPct > 0) sb.Append($" / 익절 +{s.TakeProfitPct.ToString("0.#", CultureInfo.InvariantCulture)}%");
            return sb.ToString();
        }

        // ===== 정준 직렬화 (머신 독립 · 카트리지 서명용) =====
        public static string Canonical(StrategySpec s)
        {
            string T(SpecTerm t) => $"{(int)t.Kind}:{t.Param}";
            string C(SpecCond c) => $"{T(c.Left)}|{(int)c.Op}|{(c.RightIsConst ? "K" + c.Const.ToString("R", CultureInfo.InvariantCulture) : "T" + T(c.Right!))}";
            var sb = new StringBuilder();
            sb.Append("SPEC1;");
            sb.Append("EN").Append((int)s.EntryLogic).Append('=');
            sb.Append(string.Join(",", s.Entry.Select(C))).Append(';');
            sb.Append("EX").Append((int)s.ExitLogic).Append('=');
            sb.Append(string.Join(",", s.Exit.Select(C))).Append(';');
            sb.Append("SL=").Append(s.StopLossPct.ToString("0.####", CultureInfo.InvariantCulture)).Append(';');
            sb.Append("TP=").Append(s.TakeProfitPct.ToString("0.####", CultureInfo.InvariantCulture));
            return sb.ToString();
        }

        // 스펙이 순수 MA 골든/데드크로스로 환원되면 (short,long) 반환 → CartStrategy 증명 가능
        public static bool ReducesToMaCross(StrategySpec s, out int shortP, out int longP)
        {
            shortP = longP = 0;
            if (s.StopLossPct > 0 || s.TakeProfitPct > 0) return false;
            if (s.Entry.Count != 1 || s.Exit.Count != 1) return false;
            var en = s.Entry[0]; var ex = s.Exit[0];
            if (en.RightIsConst || ex.RightIsConst || en.Right == null || ex.Right == null) return false;
            if (en.Op != SpecOp.CrossAbove || ex.Op != SpecOp.CrossBelow) return false;
            bool maEn = en.Left.Kind == SpecKind.Sma && en.Right.Kind == SpecKind.Sma;
            bool maEx = ex.Left.Kind == SpecKind.Sma && ex.Right.Kind == SpecKind.Sma;
            if (!maEn || !maEx) return false;
            if (en.Left.Param != ex.Left.Param || en.Right.Param != ex.Right.Param) return false;
            if (en.Left.Param >= en.Right.Param) return false;
            shortP = en.Left.Param; longP = en.Right.Param;
            return true;
        }

        // ===== 내부: 지표 시리즈 빌드 + 평가 =====
        private sealed class Series
        {
            public List<decimal> Closes = new();
            public Dictionary<(SpecKind, int), List<decimal>> Cache = new();
        }

        private static Series BuildSeries(StrategySpec spec, List<Candle> candles)
        {
            var s = new Series { Closes = candles.Select(c => c.Close).ToList() };
            foreach (var c in spec.Entry.Concat(spec.Exit))
            {
                Need(s, c.Left, candles);
                if (!c.RightIsConst && c.Right != null) Need(s, c.Right, candles);
            }
            return s;
        }

        private static void Need(Series s, SpecTerm t, List<Candle> candles)
        {
            var key = (t.Kind, t.Param);
            if (t.Kind == SpecKind.Price || s.Cache.ContainsKey(key)) return;
            s.Cache[key] = t.Kind switch
            {
                SpecKind.Sma => Sma(s.Closes, t.Param),
                SpecKind.Ema => IndicatorMath.Ema(s.Closes, t.Param),
                SpecKind.Rsi => IndicatorMath.Rsi(candles, t.Param),
                SpecKind.MacdHist => IndicatorMath.Macd(candles).hist,
                SpecKind.PctB => IndicatorMath.BollingerPctB(candles, t.Param),
                _ => s.Closes
            };
        }

        private static decimal Val(Series s, SpecTerm t, int i)
        {
            if (t.Kind == SpecKind.Price) return s.Closes[i];
            var arr = s.Cache[(t.Kind, t.Param)];
            return i < arr.Count ? arr[i] : (arr.Count > 0 ? arr[^1] : 0m);
        }

        private static bool EvalAll(IReadOnlyList<SpecCond> conds, SpecLogic logic, Series s, int i)
        {
            if (conds.Count == 0) return false;
            bool acc = logic == SpecLogic.And;
            foreach (var c in conds)
            {
                bool r = EvalCond(c, s, i);
                acc = logic == SpecLogic.And ? acc && r : acc || r;
            }
            return acc;
        }

        private static bool EvalCond(SpecCond c, Series s, int i)
        {
            decimal lNow = Val(s, c.Left, i);
            decimal rNow = c.RightIsConst ? (decimal)c.Const : Val(s, c.Right!, i);
            if (c.Op is SpecOp.Gt) return lNow > rNow;
            if (c.Op is SpecOp.Lt) return lNow < rNow;
            if (i < 1) return false;
            decimal lPrev = Val(s, c.Left, i - 1);
            decimal rPrev = c.RightIsConst ? (decimal)c.Const : Val(s, c.Right!, i - 1);
            return c.Op == SpecOp.CrossAbove ? (lPrev <= rPrev && lNow > rNow) : (lPrev >= rPrev && lNow < rNow);
        }

        private static int MaxParam(StrategySpec s)
        {
            int m = 1;
            foreach (var c in s.Entry.Concat(s.Exit))
            {
                if (c.Left.Kind is not (SpecKind.Price or SpecKind.MacdHist)) m = Math.Max(m, c.Left.Param);
                if (!c.RightIsConst && c.Right != null && c.Right.Kind is not (SpecKind.Price or SpecKind.MacdHist)) m = Math.Max(m, c.Right.Param);
                if (c.Left.Kind == SpecKind.MacdHist || (!c.RightIsConst && c.Right?.Kind == SpecKind.MacdHist)) m = Math.Max(m, 45);   // EMA26+signal9 시드 트랜지언트 통과
            }
            return m;
        }

        private static List<decimal> Sma(List<decimal> data, int period)
        {
            var r = new List<decimal>(data.Count);
            decimal run = 0;
            for (int i = 0; i < data.Count; i++)
            {
                run += data[i];
                if (i >= period) run -= data[i - period];
                r.Add(i >= period - 1 ? run / period : data[i]);   // 워밍업 구간은 종가로 시드
            }
            return r;
        }

        private static decimal MaxDrawdown(List<decimal> curve)
        {
            if (curve.Count == 0) return 0m;
            decimal peak = curve[0], mdd = 0;
            foreach (var v in curve) { if (v > peak) peak = v; if (peak > 0) { var dd = (peak - v) / peak * 100m; if (dd > mdd) mdd = dd; } }
            return mdd;
        }

        private static decimal SharpeOf(List<decimal> curve)
        {
            if (curve.Count < 3) return 0m;
            var rets = new List<double>();
            for (int i = 1; i < curve.Count; i++) if (curve[i - 1] != 0) rets.Add((double)((curve[i] - curve[i - 1]) / curve[i - 1]));
            if (rets.Count < 2) return 0m;
            double m = rets.Average(), ss = 0; foreach (var r in rets) ss += (r - m) * (r - m);
            double sd = Math.Sqrt(ss / (rets.Count - 1));
            return sd > 1e-12 ? (decimal)(m / sd * Math.Sqrt(252.0)) : 0m;
        }
    }
}
