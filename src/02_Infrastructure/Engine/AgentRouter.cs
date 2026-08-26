// ============================================================================
// AgentRouter - 로컬 LLM 에이전트 워크벤치 의도 라우터 (Season 9 #3) · 순수 결정론 (외부 의존 0)
//  자연어 한 줄을 '어떤 결정론 도구를 어떤 인자로 부를지'(AgentIntent)로 라우팅한다.
//  ⚠️ 핵심 원칙(환각 방화벽): LLM은 의도 라우팅만 보조하고, 실제 실행·수치는 결정론 도구가 담당한다.
//     이 규칙 라우터는 LLM 없이도 동작하는 기본 경로다. 인식 실패 시 Unknown으로 안전 폴백.
//   · 난수·시계·네트워크 없음 → 단위 테스트 가능.
// ============================================================================
using System;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace StockManager.Common
{
    public enum AgentTool { Unknown, Help, BlackSwan, Future, Health, Compile, Coach, Tournament, Risk }

    // Tool=대상 도구, Symbol=대상 종목(없으면 ""), Strategy=전략 컴파일용 원문, Raw=입력 전체.
    public sealed record AgentIntent(AgentTool Tool, string Symbol, string Strategy, string Raw);

    public static class AgentRouter
    {
        public static AgentIntent Route(string nl)
        {
            if (string.IsNullOrWhiteSpace(nl)) return new AgentIntent(AgentTool.Unknown, "", "", nl ?? "");
            string s = nl.ToLowerInvariant();
            string sym = ExtractSymbol(nl);

            // 전략 정의 신호(골든크로스·RSI 매수 등)가 있으면 컴파일 우선
            bool strategyish = s.Contains("골든크로스") || s.Contains("데드크로스") || s.Contains("전략") || s.Contains("strategy")
                || (s.Contains("rsi") && (s.Contains("매수") || s.Contains("매도") || s.Contains("이하") || s.Contains("이상")))
                || (s.Contains("일선") && (s.Contains("매수") || s.Contains("매도")))
                || s.Contains("손절") || s.Contains("익절");
            if (strategyish && !Has(s, "토너먼트", "대결", "랭킹", "tournament"))
                return new AgentIntent(AgentTool.Compile, sym, nl.Trim(), nl);

            if (Has(s, "위기", "블랙스완", "폭락", "crash", "crisis", "스트레스"))
                return new AgentIntent(AgentTool.BlackSwan, sym, "", nl);
            if (Has(s, "미래", "확률", "전망", "future", "예상 수익"))      // bare "콘" 제거(콘텐츠/아이콘 오인)
                return new AgentIntent(AgentTool.Future, sym, "", nl);
            if (Has(s, "건강", "검진", "종합 점검", "health", "건강검진"))
                return new AgentIntent(AgentTool.Health, sym, "", nl);
            if (Has(s, "회고", "복기", "편향", "coach", "습관", "처분효과"))
                return new AgentIntent(AgentTool.Coach, sym, "", nl);
            if (Has(s, "토너먼트", "대결", "랭킹", "tournament", "전략 비교"))   // bare "리그" 제거(리그 오브 레전드 오인)
                return new AgentIntent(AgentTool.Tournament, sym, "", nl);
            if (Has(s, "리스크", "위험기여", "위험 기여", "종목 집중", "집중도", "패리티", "risk"))   // bare "집중" 제거
                return new AgentIntent(AgentTool.Risk, sym, "", nl);
            if (Has(s, "도움", "도와", "help", "뭐 할", "뭐할", "기능", "사용법", "할 수 있"))
                return new AgentIntent(AgentTool.Help, sym, "", nl);

            return new AgentIntent(AgentTool.Unknown, sym, "", nl);
        }

        private static bool Has(string s, params string[] kws) => kws.Any(k => s.Contains(k, StringComparison.Ordinal));

        // 종목코드 추출: 6자리 숫자(KRX) 또는 '$TICKER'/원문 대문자 티커.
        //  소문자 일반 영단어(the·buy·gold…)는 종목으로 오인하지 않는다(티커는 관례상 대문자). 한국어는 매칭 안 됨.
        private static string ExtractSymbol(string nl)
        {
            var krx = Regex.Match(nl, @"\b(\d{6})\b");        // 6자리(예: 200000원은 \b 경계로 매칭 안 됨)
            if (krx.Success) return krx.Groups[1].Value;
            var dollar = Regex.Match(nl, @"\$([A-Za-z]{1,5})\b");   // $aapl 명시 우선
            if (dollar.Success) { var t = dollar.Groups[1].Value.ToUpperInvariant(); if (!StopWords.Contains(t)) return t; }
            foreach (Match m in Regex.Matches(nl, @"\b([A-Z]{2,5})\b"))   // 원문 대문자 티커만(소문자 영단어 제외)
            {
                var t = m.Groups[1].Value;
                if (!StopWords.Contains(t)) return t;
            }
            return "";
        }

        // 티커로 오인하기 쉬운 대문자 토큰(지표·약어·일반 영단어)
        private static readonly System.Collections.Generic.HashSet<string> StopWords =
            new(StringComparer.OrdinalIgnoreCase)
            { "RSI", "MA", "SMA", "EMA", "MACD", "OOS", "AI", "LLM", "KRW", "USD", "P5", "P95", "VS", "OK", "ETF",
              "BUY", "SELL", "HOLD", "THE", "AND", "OR", "FOR", "NOW", "MDD", "OOS", "KPI", "PNL", "MA5", "MA20" };
    }
}
