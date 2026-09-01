// ============================================================================
// DebateService - 멀티에이전트 토론 (강세 vs 약세 vs 심판) (Season 4 ②) (v3.35.0)
//  단일 LLM의 한쪽 편향을 깨기 위해 3에이전트가 '결정론 데이터'를 근거로 토론한다:
//   · 강세론자(Bull) — 상승 논거   · 약세론자(Bear) — 하락 논거
//   · 심판(Judge)    — 양측 + '과거 판정 적중률(승률 가중)'을 보고 균형 판정
//  그라운딩 데이터(BuildContext)와 승률 채점(DebateScore)은 순수·테스트 가능,
//  LLM 호출(RunAsync)만 AiService 어댑터 경유(로컬 Ollama면 무료·프라이빗).
// ============================================================================
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace MS2E.Infrastructure
{
    public class DebateResult : MS2E.UI.Portable.Ports.Intel.IDebateResultView   // Phase 94-O: 공변(전 멤버 암시·StanceLabel 계산)
    {
        public bool Ok { get; set; }
        public string Error { get; set; } = "";
        public string Context { get; set; } = "";
        public string BullText { get; set; } = "";
        public string BearText { get; set; } = "";
        public string JudgeText { get; set; } = "";
        public int Stance { get; set; }   // +1 매수 / 0 중립 / -1 매도 (심판 판정)
        public string StanceLabel => Stance > 0 ? "매수" : Stance < 0 ? "매도" : "중립";
    }

    public static class DebateService
    {
        /// <summary>캔들에서 결정론 그라운딩 텍스트 생성(레짐·RSI·MA이격·수익률). 순수.</summary>
        public static string BuildContext(string symbol, IReadOnlyList<Candle> candles)
        {
            if (candles == null || candles.Count < 21) return $"[종목 {symbol}] 데이터 부족(21봉+ 필요)";
            var c = candles.OrderBy(x => x.Date).Select(x => (double)x.Close).ToList();
            double last = c[^1];
            double R(int n) => c.Count > n && c[^(n + 1)] != 0 ? (last - c[^(n + 1)]) / c[^(n + 1)] * 100 : 0;
            double ma20 = c.Skip(c.Count - 20).Average();
            double gap = ma20 != 0 ? (last - ma20) / ma20 * 100 : 0;
            double rsi = Rsi(c, 14);
            var reg = RegimeDetector.Analyze(candles.Select(x => x.Close).ToList());

            return
                $"[종목 {symbol} · 결정론 그라운딩]\n" +
                $"현재가 {last:N0} · 5일 {R(5):+0.0;-0.0}% · 20일 {R(20):+0.0;-0.0}%\n" +
                $"RSI(14) {rsi:0} · MA20 이격 {gap:+0.0;-0.0}%\n" +
                $"레짐: {reg.Describe()} (z {reg.ZScore:0.0}, 변동성비 {reg.VolRatio:0.0})";
        }

        /// <summary>3에이전트 토론 실행(LLM). record로 심판에 승률 가중을 주입.</summary>
        public static async Task<DebateResult> RunAsync(AiService ai, string symbol,
            List<Candle> candles, BullBearRecord record, string model = "gemini-2.0-flash")
        {
            var res = new DebateResult { Context = BuildContext(symbol, candles) };
            if (!ai.HasLlm) { res.Error = "LLM 미설정 — Gemini 키 또는 로컬 LLM(Ollama)을 켜세요."; return res; }

            res.BullText = await ai.CompleteAsync(
                "당신은 강세론자입니다. 아래 [데이터]만 근거로 이 종목의 상승 논거 3가지와 한 줄 결론을 한국어로. 데이터에 없는 외부 사건은 지어내지 마세요. 평문.\n\n[데이터]\n" + res.Context, model);
            if (AiService.IsError(res.BullText)) { res.Error = res.BullText; return res; }

            res.BearText = await ai.CompleteAsync(
                "당신은 약세론자입니다. 아래 [데이터]만 근거로 이 종목의 하락·리스크 논거 3가지와 한 줄 결론을 한국어로. 없는 사건은 금지. 평문.\n\n[데이터]\n" + res.Context, model);
            if (AiService.IsError(res.BearText)) { res.Error = res.BearText; return res; }

            var rec = $"과거 강세 판정 적중률 {record.BullishHitPct:0}%({record.BullishN}건) · 약세 판정 {record.BearishHitPct:0}%({record.BearishN}건)";
            res.JudgeText = await ai.CompleteAsync(
                "당신은 중립적 심판입니다. 아래 강세론·약세론과 '나의 과거 판정 적중률'을 보고 균형 잡힌 판정을 내리세요.\n" +
                "규칙: ① 첫 줄을 정확히 `[판정: 매수]` 또는 `[판정: 중립]` 또는 `[판정: 매도]` 로 시작. ② 이후 2~3줄 근거 + 신뢰도(0~100). " +
                "③ 적중률이 낮은 쪽 주장은 신중히 가중. ④ 투자 권유가 아닌 정보 제공. 평문.\n\n" +
                $"[나의 과거 적중률]\n{rec}\n\n[강세론]\n{res.BullText}\n\n[약세론]\n{res.BearText}", model);
            if (AiService.IsError(res.JudgeText)) { res.Error = res.JudgeText; return res; }

            res.Stance = ParseStance(res.JudgeText);
            res.Ok = true;
            return res;
        }

        /// <summary>심판 텍스트 첫 부분에서 판정 파싱(매수+1/중립0/매도-1).</summary>
        public static int ParseStance(string judge)
        {
            if (string.IsNullOrEmpty(judge)) return 0;
            var head = judge.Length > 40 ? judge[..40] : judge;
            if (head.Contains("매수")) return 1;
            if (head.Contains("매도")) return -1;
            return 0;
        }

        private static double Rsi(List<double> c, int p)
        {
            if (c.Count <= p) return 50;
            double g = 0, l = 0;
            for (int i = c.Count - p; i < c.Count; i++) { var d = c[i] - c[i - 1]; if (d > 0) g += d; else l -= d; }
            if (l == 0) return 100;
            var rs = (g / p) / (l / p);
            return 100 - 100 / (1 + rs);
        }
    }
}
