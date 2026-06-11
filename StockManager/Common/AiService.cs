// ============================================================================
// AiService - Gemini 기반 분석/비서 (Blazor/WinForms 공용) (v3.8)
//  · API 키는 DPAPI Secret(Gemini.ApiKey)에서 로드.
//  · v3.8: 공용 CompleteAsync + 포트폴리오 자연어 질의(AskAsync)/아침 브리핑(MorningBriefAsync).
//    환각 방지 — 모든 수치는 PortfolioContext가 C#에서 결정적으로 계산해 프롬프트에 주입하고,
//    LLM은 "제공 데이터 내에서만" 답하도록 지시. (function-calling은 후속 업그레이드 후보)
// ============================================================================
using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace StockManager.Common
{
    public class AiService
    {
        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };
        private readonly LocalDatabase _db;
        public AiService(LocalDatabase db) { _db = db; }

        public bool HasKey => !string.IsNullOrWhiteSpace(_db.GetSecret("Gemini.ApiKey"));

        // ===== 단일 종목 분석 (기존) =====
        public Task<string> AnalyzeAsync(string symbol, string market, string model = "gemini-2.0-flash")
        {
            var prompt =
                $"당신은 한국 증권 시장 전문 애널리스트입니다. 종목코드 {symbol} ({market})에 대해 다음을 한국어로 간결하게 작성하세요:\n" +
                "1) 기업 개요 (2~3줄)\n2) 최근 투자 포인트 3가지\n3) 주요 리스크 2가지\n4) 종합 의견 (매수/중립/매도 중 하나와 한 줄 근거)\n" +
                "마크다운 없이 평문으로 작성하세요.";
            return CompleteAsync(prompt, model);
        }

        // ===== 포트폴리오 자연어 질의 — 제공 데이터만 근거(환각 방지) =====
        public Task<string> AskAsync(string question, decimal usdKrw, string model = "gemini-2.0-flash")
        {
            var ctx = PortfolioContext.Build(_db, usdKrw);
            var prompt =
                "당신은 신중한 개인 투자 보조원입니다. 아래 [실제 데이터]에 있는 수치만 근거로 한국어로 간결히 답하세요.\n" +
                "데이터에 없는 가격·수익률은 추측하거나 지어내지 마세요. 모르면 모른다고 하세요.\n" +
                "이는 투자 권유가 아닌 정보 제공이며, 마지막에 한 줄로 그 점을 덧붙이세요.\n\n" +
                $"[실제 데이터]\n{ctx}\n\n[질문]\n{question}";
            return CompleteAsync(prompt, model);
        }

        // ===== 아침 브리핑 — 데이터 요약. pushTelegram이면 텔레그램 전송도 =====
        public async Task<string> MorningBriefAsync(decimal usdKrw, bool pushTelegram = false, string model = "gemini-2.0-flash")
        {
            var ctx = PortfolioContext.Build(_db, usdKrw);
            var prompt =
                "아래 [실제 데이터]로 한국어 '아침 브리핑'을 5~7줄로 작성하세요:\n" +
                "① 자산·손익 한 줄 요약 ② 주목할 시그널/거래 ③ 리스크 한 줄 ④ 오늘 체크포인트.\n" +
                "데이터에 없는 수치는 만들지 마세요. 투자 권유가 아닌 정보 제공입니다. 마크다운 없이 평문으로.\n\n" +
                $"[실제 데이터]\n{ctx}";
            var text = await CompleteAsync(prompt, model);
            if (pushTelegram && !IsError(text))
            {
                try { await TelegramNotifier.SendAsync(_db, "🌅 <b>아침 브리핑</b>\n" + text); } catch { }
            }
            return text;
        }

        // ===== 공용 Gemini 완성 호출 (grounded prompt → text) =====
        public async Task<string> CompleteAsync(string prompt, string model = "gemini-2.0-flash")
        {
            var apiKey = _db.GetSecret("Gemini.ApiKey");
            if (string.IsNullOrWhiteSpace(apiKey))
                return "⚠️ Gemini API 키가 설정되지 않았습니다.\n설정 마법사(/setup) 또는 거래 설정에서 키를 입력하세요.";

            try
            {
                var url = $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent?key={apiKey}";
                var payload = JsonSerializer.Serialize(new
                {
                    contents = new[] { new { parts = new[] { new { text = prompt } } } }
                });
                using var res = await Http.PostAsync(url, new StringContent(payload, Encoding.UTF8, "application/json"));
                var json = await res.Content.ReadAsStringAsync();
                if (!res.IsSuccessStatusCode) return $"분석 실패({(int)res.StatusCode}): {Trunc(json)}";
                using var doc = JsonDocument.Parse(json);
                var text = doc.RootElement
                    .GetProperty("candidates")[0]
                    .GetProperty("content")
                    .GetProperty("parts")[0]
                    .GetProperty("text").GetString();
                return string.IsNullOrWhiteSpace(text) ? "(응답 없음)" : text!;
            }
            catch (Exception ex) { return "분석 오류: " + ex.Message; }
        }

        // ===== 자연어 → 복합 알림 규칙 컴파일 (검증된 CompositeRule만 반환; 환각 안전) =====
        //  LLM은 자유 조언이 아니라 '스키마 JSON'만 생성 → C#에서 파싱·검증 → 사용자 검토 후 저장.
        public async Task<(CompositeRule? rule, string error)> CompileRuleAsync(string nl, string model = "gemini-2.0-flash")
        {
            var apiKey = _db.GetSecret("Gemini.ApiKey");
            if (string.IsNullOrWhiteSpace(apiKey)) return (null, "Gemini API 키가 없습니다.");
            if (string.IsNullOrWhiteSpace(nl)) return (null, "요청을 입력하세요.");

            var prompt =
                "다음 한국어 요청을 '복합 알림 규칙' JSON으로 변환하세요. 아래 스키마를 정확히 따르고 JSON만 출력하세요.\n" +
                "{\"name\":string,\"assetKind\":\"coin|stock\",\"target\":\"코인은 KRW-BTC 형식, 주식은 005930 형식\"," +
                "\"logic\":\"And|Or\",\"cooldownMin\":int,\"conditions\":[{\"metric\":\"Price|ChangePct|MaGapPct|Rsi|VolumeSurgePct\"," +
                "\"param\":int,\"op\":\"Gte|Lte\",\"threshold\":number}]}\n" +
                "metric: Price=현재가(원), ChangePct=당일 등락률(%), MaGapPct=param일 이동평균과의 괴리(%), " +
                "Rsi=param일 RSI(0~100), VolumeSurgePct=전일 대비 거래량 증가율(%). op: Gte=이상, Lte=이하. param=MA/RSI 기간(기본 14).\n" +
                $"요청: {nl}";

            try
            {
                var url = $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent?key={apiKey}";
                var payload = JsonSerializer.Serialize(new
                {
                    contents = new[] { new { parts = new[] { new { text = prompt } } } },
                    generationConfig = new { responseMimeType = "application/json", temperature = 0.1 }
                });
                using var res = await Http.PostAsync(url, new StringContent(payload, Encoding.UTF8, "application/json"));
                var body = await res.Content.ReadAsStringAsync();
                if (!res.IsSuccessStatusCode) return (null, $"생성 실패({(int)res.StatusCode})");
                using var doc = JsonDocument.Parse(body);
                var text = doc.RootElement.GetProperty("candidates")[0].GetProperty("content")
                    .GetProperty("parts")[0].GetProperty("text").GetString() ?? "";
                return ParseRule(text);
            }
            catch (Exception ex) { return (null, "오류: " + ex.Message); }
        }

        private static (CompositeRule? rule, string error) ParseRule(string jsonText)
        {
            try
            {
                using var doc = JsonDocument.Parse(jsonText);
                var r = doc.RootElement;
                var rule = new CompositeRule
                {
                    Name = Str(r, "name", "AI 규칙"),
                    AssetKind = Str(r, "assetKind", "coin").Equals("stock", StringComparison.OrdinalIgnoreCase) ? "stock" : "coin",
                    Target = Str(r, "target", ""),
                    Logic = Str(r, "logic", "And").Equals("Or", StringComparison.OrdinalIgnoreCase) ? RuleLogic.Or : RuleLogic.And,
                    CooldownMin = r.TryGetProperty("cooldownMin", out var cm) && cm.TryGetInt32(out var cmi) ? cmi : 30,
                    Conditions = new()
                };
                if (r.TryGetProperty("conditions", out var conds) && conds.ValueKind == JsonValueKind.Array)
                    foreach (var c in conds.EnumerateArray())
                    {
                        if (!Enum.TryParse<CondMetric>(Str(c, "metric", "Price"), true, out var metric)) metric = CondMetric.Price;
                        if (!Enum.TryParse<CondOp>(Str(c, "op", "Gte"), true, out var op)) op = CondOp.Gte;
                        int param = c.TryGetProperty("param", out var p) && p.TryGetInt32(out var pi) && pi > 0 ? pi : 14;
                        decimal thr = c.TryGetProperty("threshold", out var t) ? Safe.JsonDec(t) : 0m;
                        rule.Conditions.Add(new CompositeCondition { Metric = metric, Param = param, Op = op, Threshold = thr });
                    }
                if (string.IsNullOrWhiteSpace(rule.Target)) return (null, "대상(코인/종목)을 인식하지 못했습니다. 더 구체적으로 입력하세요.");
                if (rule.Conditions.Count == 0) return (null, "조건을 인식하지 못했습니다.");
                return (rule, "");
            }
            catch (Exception ex) { return (null, "규칙 파싱 실패: " + ex.Message); }
        }

        private static string Str(JsonElement e, string prop, string def)
            => e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? (v.GetString() ?? def) : def;

        // 키 미설정·호출 실패 등 비정상 응답 식별 (텔레그램 전송/저장 가드용)
        public static bool IsError(string text) =>
            string.IsNullOrWhiteSpace(text) || text.StartsWith("⚠️") || text.StartsWith("분석 실패") || text.StartsWith("분석 오류") || text == "(응답 없음)";

        private static string Trunc(string s) => s.Length > 200 ? s[..200] : s;
    }
}
