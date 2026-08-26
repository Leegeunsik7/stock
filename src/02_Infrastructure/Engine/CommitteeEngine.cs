// ============================================================================
// CommitteeEngine — 가상 퀀트 위원회 팩트 시트 조립·페르소나 프롬프트 생성 (v3.73)
//  · 순수 C# 결정론, 외부 의존 0, LLM 미사용 (프롬프트 생성만).
//  · AlphaReport + TailBudgetResult? + BacktestSummary? → CommitteeFactSheet
//  · Bull / Bear / CRO 프롬프트를 순수 문자열로 생성 → AiService.CompleteAsync에 주입.
//  · 환각 방화벽: 모든 LLM 의견은 string 전용 — C# 상태 변경 불가.
// ============================================================================
using System.Text;

namespace MS2E.Infrastructure
{
    /// <summary>위원회 근거 팩트 시트 (순수 C# 결정론 조립).</summary>
    public sealed record CommitteeFactSheet(
        string Symbol,
        AlphaReport Alpha,
        TailBudgetResult? TailRisk,
        BacktestSummary? Backtest,
        string BacktestLabel = "");   // 전략명 레이블 (Backtest != null 시 표시)

    /// <summary>위원회 보고서 — LLM 의견은 string 전용, C# 상태 불변.</summary>
    public sealed record CommitteeReport(
        bool IsValid,
        string Symbol,
        CommitteeFactSheet FactSheet,
        string BullOpinion,
        string BearOpinion,
        string CroSynthesis,
        string Note);

    public static class CommitteeEngine
    {
        // ── FactSheet 조립 ────────────────────────────────────────────────────
        public static CommitteeFactSheet BuildFactSheet(
            string symbol,
            AlphaReport alpha,
            TailBudgetResult? tailRisk,
            BacktestSummary? backtest,
            string backtestLabel = "") =>
            new(symbol, alpha, tailRisk, backtest, backtestLabel);

        // ── 페르소나 프롬프트 (순수 C# 문자열 조합, 결정론, LLM 없음) ──────────────

        public static string BullPrompt(CommitteeFactSheet fs) =>
            "[역할] 당신은 공격적 성장 추구 펀드매니저 Bull입니다.\n" +
            "아래 [팩트 시트]를 보고, 투자 기회·상승 근거·알파 창출 포인트를 강력히 옹호하세요.\n" +
            "규칙: ① 팩트 시트에 있는 수치만 근거로 하세요. 없는 수치는 추측하지 마세요.\n" +
            "② 한국어로, 200자 이내로, 마크다운 없이 평문으로 답하세요.\n\n" +
            "[팩트 시트]\n" + FormatFactSheet(fs);

        public static string BearPrompt(CommitteeFactSheet fs) =>
            "[역할] 당신은 엄격한 리스크 관리자 Bear입니다.\n" +
            "아래 [팩트 시트]를 보고, 꼬리 리스크·하방 시나리오·시장 베타 의존도를 날카롭게 지적하세요.\n" +
            "규칙: ① 팩트 시트에 있는 수치만 근거로 하세요. 없는 수치는 추측하지 마세요.\n" +
            "② 한국어로, 200자 이내로, 마크다운 없이 평문으로 답하세요.\n\n" +
            "[팩트 시트]\n" + FormatFactSheet(fs);

        public static string CroPrompt(CommitteeFactSheet fs, string bullOpinion, string bearOpinion) =>
            "[역할] 당신은 수석 리스크 책임자 CRO입니다.\n" +
            "아래 [팩트 시트]와 [Bull 의견], [Bear 의견]을 종합해 균형 잡힌 실행 권고를 내리세요.\n" +
            "단계별 안전장치(포지션 한도, 손절 선, 재검토 조건)를 구체적으로 포함하세요.\n" +
            "규칙: ① 팩트 시트에 있는 수치만 근거로 하세요. 없는 수치는 추측하지 마세요.\n" +
            "② 한국어로, 250자 이내로, 마크다운 없이 평문으로 답하세요.\n" +
            "③ 이 답변은 투자 권유가 아닌 내부 검토 의견입니다.\n\n" +
            "[팩트 시트]\n" + FormatFactSheet(fs) + "\n" +
            "[Bull 의견]\n" + bullOpinion + "\n\n" +
            "[Bear 의견]\n" + bearOpinion;

        // ── FactSheet 포맷 (LLM 주입용 + UI 투명 표시용) ─────────────────────────
        public static string FormatFactSheet(CommitteeFactSheet fs)
        {
            var ic = System.Globalization.CultureInfo.InvariantCulture;
            var sb = new StringBuilder();

            sb.AppendLine("─────────────────────────────────");
            sb.AppendLine($"종목: {fs.Symbol}");
            sb.AppendLine("─────────────────────────────────");

            var a = fs.Alpha;
            sb.AppendLine($"[알파 분석]  기간 {a.AlignedDays}일  |  벤치마크: {a.Benchmark}");
            sb.AppendLine($"  총수익률   : {Fmt(a.TotalReturnPct)}%");
            sb.AppendLine($"  시장기여분 : {Fmt(a.MarketAttributedPct)}%   진성 알파: {Fmt(a.TrueAlphaPct)}%  (연환산 {Fmt(a.TrueAlphaAnnualPct)}%)");
            sb.AppendLine($"  베타(β)    : {a.Beta.ToString("0.000", ic)}   R²: {a.R2Pct.ToString("0.0", ic)}%   IR: {a.InformationRatio.ToString("0.00", ic)}");

            if (fs.TailRisk is { IsValid: true } t)
            {
                sb.AppendLine($"[테일 리스크]  최악 위기: {t.WorstCrisis}");
                sb.AppendLine($"  현재 MDD   : {t.BaselineWorstMdd.ToString("+0.00;-0.00;0.00", ic)}%");
                sb.AppendLine($"  최적화 MDD : {t.OptimalWorstMdd.ToString("+0.00;-0.00;0.00", ic)}%   개선: {t.ImprovementPct.ToString("0.0", ic)}%");
            }
            else
            {
                sb.AppendLine("[테일 리스크]  포트폴리오 비중 데이터 없음 — 생략");
            }

            if (fs.Backtest is { } b)
            {
                var label = string.IsNullOrEmpty(fs.BacktestLabel) ? "전략" : fs.BacktestLabel;
                sb.AppendLine($"[백테스트]  {label}");
                sb.AppendLine($"  총수익률 : {b.TotalReturn.ToString("+0.00;-0.00;0.00", ic)}%   MDD: {b.MaxDrawdown.ToString("0.00", ic)}%");
                sb.AppendLine($"  승률 : {b.WinRate.ToString("0.0", ic)}%   샤프: {b.Sharpe.ToString("0.00", ic)}   거래수: {b.TradeCount}");
            }
            else
            {
                sb.AppendLine("[백테스트]  전략 카트리지 데이터 없음 — 생략");
            }

            sb.Append("─────────────────────────────────");
            return sb.ToString();
        }

        private static string Fmt(double v) =>
            v.ToString("+0.00;-0.00;0.00", System.Globalization.CultureInfo.InvariantCulture);
    }
}
