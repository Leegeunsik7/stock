// ============================================================================
// StrategyCompilerService - LLM 전략 컴파일러 오케스트레이션 (Season 8 #3)
//  선택적 LLM(Gemini/Ollama) → 스펙 JSON 제안 → C# 결정론 검증(ParseJson). 실패/오프라인이면
//  규칙 파서(ParseNl)로 폴백. 항상 StrategyCompiler.Validate를 통과한 스펙만 돌려준다(환각 방화벽).
//  ExportCartridge: 검증된 스펙을 CartPrompt(정준 직렬화, 서명 포함)로, MA크로스면 CartStrategy 증명까지.
// ============================================================================
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace StockManager.Common
{
    public static class StrategyCompilerService
    {
        /// <summary>preferLlm이고 LLM이 설정돼 있으면 LLM 제안→C# 검증, 아니면(또는 실패 시) 규칙 파서.</summary>
        public static async Task<CompileResult> CompileAsync(AiService ai, string nl, bool preferLlm, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(nl)) return new CompileResult(false, null, "규칙", "문장을 입력하세요.");

            if (preferLlm && ai != null && ai.HasLlm)
            {
                string raw;
                try { raw = await ai.CompleteAsync(StrategyParser.BuildPrompt(nl)); }
                catch (Exception ex) { raw = "⚠️ " + ex.Message; }

                if (!AiService.IsError(raw))
                {
                    var r = StrategyParser.ParseJson(raw, "LLM·" + ai.ActiveLlm);
                    if (r.IsValid) return r;
                    var fb1 = StrategyParser.ParseNl(nl);   // LLM 출력이 검증 실패 → 규칙 폴백
                    return fb1.IsValid ? fb1 with { Note = fb1.Note + " · LLM 출력 검증 실패로 규칙 폴백" } : r;
                }
                var fb2 = StrategyParser.ParseNl(nl);       // LLM 미응답/오류 → 규칙 폴백
                return fb2.IsValid ? fb2 with { Note = fb2.Note + " · LLM 미응답으로 규칙 폴백" } : fb2;
            }
            return StrategyParser.ParseNl(nl);
        }

        /// <summary>검증된 스펙 → 서명 카트리지(.ms2cart). 스펙은 CartPrompt(정준), MA크로스면 CartStrategy 증명 첨부. 경로 반환.</summary>
        public static string ExportCartridge(LocalDatabase db, StrategySpec spec, BacktestSummary bt, string symbol)
        {
            var prompts = new List<CartPrompt> { new CartPrompt("strategy-spec:v1:" + spec.Id, StrategyCompiler.Canonical(spec)) };
            var strategies = new List<CartStrategy>();
            bool maCross = StrategyCompiler.ReducesToMaCross(spec, out var sp, out var lp);
            if (maCross) strategies.Add(CartridgeEngine.Attest($"MA {sp}/{lp}", sp, lp, 7));   // ForgePage와 동일 시드

            // 정직성: 임의 스펙은 정준 직렬화가 '무결성'만 보장(역디코더 없음). MA크로스로 환원될 때만 증명서로 재실행 검증 가능.
            string verifyNote = maCross
                ? "MA크로스 증명서가 첨부되어 받는 쪽이 재실행 검증 가능."
                : "스펙은 정준 직렬화되어 서명으로 무결성만 보장(임의 스펙 재실행 디코더는 추후).";
            string desc =
                $"LLM 전략 컴파일러 산출 — {StrategyCompiler.Describe(spec)} · 대상 {symbol} 과거 백테스트 수익 {bt.TotalReturn:0.0}%" +
                $" (실종목 OOS 아님·정보용). {verifyNote}";

            var cart = CartridgeService.BuildSigned(db, $"전략 {spec.Id}", desc, strategies,
                macros: new List<CartMacro>(), prompts: prompts);   // 무관 매크로 유출 방지: 명시적 빈 매크로
            return CartridgeService.ExportToFile(cart);
        }
    }
}
