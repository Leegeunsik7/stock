// ============================================================================
// AgentService - 에이전트 워크벤치 디스패치 (Season 9 #3)
//  AgentRouter가 라우팅한 의도를 실제 결정론 도구로 실행한다. 가벼운 것(전략 컴파일·확률 미래·블랙스완)은
//  인라인 실행해 결정론 요약을 주고, 무거운 것(건강검진·회고·토너먼트·리스크)은 한 줄 안내 + 해당 페이지 딥링크.
//  ⚠️ 모든 수치는 결정론 도구가 산출(환각 방화벽). 선택적 LLM 서술은 NarrateAsync로 별도 제공.
// ============================================================================
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MS2E.Infrastructure
{
    public sealed record AgentReply(bool Ok, AgentTool Tool, string Title, string Summary, string Link, string Hint);

    public static class AgentService
    {
        public static async Task<AgentReply> RespondAsync(LocalDatabase db, IQuoteProvider provider, FxConverter fx, AiService ai,
            AgentIntent intent, string fallbackSymbol, CancellationToken ct = default)
        {
            string sym = !string.IsNullOrWhiteSpace(intent.Symbol) ? intent.Symbol.ToUpperInvariant()
                : (fallbackSymbol ?? "").Trim().ToUpperInvariant();

            switch (intent.Tool)
            {
                case AgentTool.Help:
                    return new AgentReply(true, AgentTool.Help, "할 수 있는 일",
                        "· 전략 컴파일(\"5일선 20일선 골든크로스 매수, 손절 8%\")\n· 확률 미래(\"005930 미래 전망\")\n· 블랙스완 위기(\"포트 위기 시 어떻게 돼?\")\n· 종합 건강검진 · 회고 코치 · 전략 토너먼트 · 리스크 기여\n자연어로 물어보면 알맞은 결정론 도구로 실행하거나 안내합니다.", "", "");

                case AgentTool.Compile:
                {
                    var r = await StrategyCompilerService.CompileAsync(ai, intent.Strategy, preferLlm: ai.HasLlm, ct);
                    if (!r.IsValid || r.Spec is null)
                        return new AgentReply(false, AgentTool.Compile, "전략 컴파일", "이해하지 못했습니다: " + r.Note, "/compiler", "예: 5일선이 20일선을 골든크로스하면 매수, 데드크로스하면 매도, 손절 8%");
                    return new AgentReply(true, AgentTool.Compile, $"전략 컴파일됨 ({r.Source})",
                        StrategyCompiler.Describe(r.Spec), "/compiler", "컴파일러 페이지에서 종목 백테스트·카트리지 서명 가능");
                }

                case AgentTool.Future:
                {
                    if (string.IsNullOrWhiteSpace(sym)) return Need(AgentTool.Future, "확률 미래", "/future");
                    try
                    {
                        var candles = (await provider.GetHistoryAsync(sym, Detect(sym), DateTime.Now.AddYears(-2), DateTime.Now, ct))
                            .OrderBy(c => c.Date).ToList();
                        var rets = NightlyResearch.DailyReturns(candles);
                        if (rets.Length < 20) return new AgentReply(false, AgentTool.Future, "확률 미래", $"{sym} 수익률 표본 부족.", "/future", "");
                        var cone = FutureCone.Project(rets, 21, 500, 12345, goalPct: 0);
                        return new AgentReply(true, AgentTool.Future, $"확률 미래 · {sym}",
                            $"1개월 기대 {cone.ExpReturnPct:+0.0;-0.0}% (중앙값 {cone.MedianReturnPct:+0.0;-0.0}%), 손실확률 {cone.ProbLossPct:0}%, P5 {cone.P5ReturnPct:+0.0;-0.0}%~P95 {cone.P95ReturnPct:+0.0;-0.0}%, 예상 최대낙폭 {cone.MedianMaxDrawdownPct:0.0}%",
                            "/future", "확률 추정이며 예측이 아닙니다.");
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { return new AgentReply(false, AgentTool.Future, "확률 미래", $"{sym} 조회 실패: {ex.Message}", "/future", ""); }
                }

                case AgentTool.BlackSwan:
                {
                    decimal usdKrw = 1380m; try { usdKrw = await fx.GetUsdKrwAsync(); } catch { }
                    var hs = db.GetAllHoldings().Where(h => h.Market != Market.CRYPTO && !string.IsNullOrWhiteSpace(h.Symbol) && h.Quantity > 0)
                        .GroupBy(h => h.Symbol, StringComparer.OrdinalIgnoreCase)
                        .Select(g => { var f = g.First(); return new Holding { Symbol = f.Symbol, Name = f.Name, Market = f.Market, Currency = f.Currency, CurrentPrice = f.CurrentPrice, Quantity = g.Sum(x => x.Quantity) }; }).ToList();
                    if (hs.Count == 0) return new AgentReply(false, AgentTool.BlackSwan, "블랙스완", "보유 종목이 없습니다.", "/blackswan", "");
                    decimal Mv(Holding h) => h.CurrentPrice * h.Quantity * (h.Currency == Currency.USD ? usdKrw : 1m);
                    double tot = (double)hs.Sum(Mv);
                    var bsh = hs.Select(h => new BlackSwanHolding(h.Symbol, string.IsNullOrWhiteSpace(h.Name) ? h.Symbol : h.Name,
                        tot > 0 ? (double)Mv(h) / tot : 1.0 / hs.Count, 1.0)).ToList();
                    BlackSwanResult? worst = null; string wn = "";
                    foreach (var p in BlackSwan.Library)
                    {
                        var rr = BlackSwan.Inject(p, bsh);
                        if (rr.IsValid && (worst == null || rr.MaxDrawdownPct < worst.MaxDrawdownPct)) { worst = rr; wn = p.Name; }
                    }
                    if (worst == null) return new AgentReply(false, AgentTool.BlackSwan, "블랙스완", "시뮬레이션 실패.", "/blackswan", "");
                    return new AgentReply(true, AgentTool.BlackSwan, $"최악 위기 · {wn}",
                        $"보유 {hs.Count}종목 · 최종 {worst.TerminalPct:+0.0;-0.0}%, 최대 낙폭 {worst.MaxDrawdownPct:0.0}% (포트 β {worst.PortfolioBeta:0.00}, 상관 붕괴 가정)",
                        "/blackswan", "역사적 위기 대표 경로 주입 · 스트레스 테스트");
                }

                case AgentTool.Health: return Link(AgentTool.Health, "종합 건강검진", "포트폴리오 4축(분산·위기·미래·행동) 종합 점수를 산출합니다.", "/health");
                case AgentTool.Coach: return Link(AgentTool.Coach, "회고 코치", "실제 거래 이력의 행동 편향을 복기합니다.", "/coach");
                case AgentTool.Tournament: return Link(AgentTool.Tournament, "전략 토너먼트", "저장 전략들을 워크포워드로 맞붙여 랭킹합니다.", "/tournament");
                case AgentTool.Risk: return Link(AgentTool.Risk, "리스크 기여", "위험이 어디서 나오는지 분해합니다.", "/portfolio/risk");

                default:
                    return new AgentReply(false, AgentTool.Unknown, "이해하지 못했어요",
                        "예시: \"005930 미래 전망\", \"포트 위기 시뮬\", \"5일선 20일선 골든크로스 매수 손절 8%\", \"종합 건강검진\", \"회고\", \"도움말\"", "", "");
            }
        }

        /// <summary>선택적 LLM 서술 — 결정론 요약을 근거로만 친절히 설명(환각 방화벽). 미설정 시 경고 문자열.</summary>
        public static async Task<string> NarrateAsync(AiService ai, AgentReply reply)
        {
            if (ai == null || !ai.HasLlm) return "⚠️ LLM이 설정되지 않았습니다(요약은 위 결정론 결과 그대로입니다).";
            string prompt = "아래 결정론 분석 결과만 근거로, 사용자에게 1~3문장으로 친절히 설명하세요. " +
                "수치를 새로 지어내지 말고 주어진 값만 사용하며, 투자 권유가 아닌 정보 제공으로 끝맺으세요.\n\n" +
                $"[{reply.Title}] {reply.Summary}";
            return await ai.CompleteAsync(prompt);
        }

        private static AgentReply Need(AgentTool t, string title, string link) =>
            new AgentReply(false, t, title, "종목코드를 알려주세요(예: 005930 또는 AAPL).", link, "");
        private static AgentReply Link(AgentTool t, string title, string summary, string link) =>
            new AgentReply(true, t, title, summary + " → 아래 링크에서 실행하세요.", link, "");
        private static Market Detect(string s) => s.Length == 6 && char.IsDigit(s[0]) ? Market.KRX_KOSPI : Market.US_NASDAQ;
    }
}
