// ============================================================================
// AiService - Gemini 기반 종목 분석 (Blazor/WinForms 공용) (v3.4)
//  · API 키는 DPAPI Secret(Gemini.ApiKey)에서 로드.
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

        public async Task<string> AnalyzeAsync(string symbol, string market, string model = "gemini-2.0-flash")
        {
            var apiKey = _db.GetSecret("Gemini.ApiKey");
            if (string.IsNullOrWhiteSpace(apiKey))
                return "⚠️ Gemini API 키가 설정되지 않았습니다.\n거래 설정 또는 AI 설정에서 키를 입력하세요.";

            var prompt =
                $"당신은 한국 증권 시장 전문 애널리스트입니다. 종목코드 {symbol} ({market})에 대해 다음을 한국어로 간결하게 작성하세요:\n" +
                "1) 기업 개요 (2~3줄)\n2) 최근 투자 포인트 3가지\n3) 주요 리스크 2가지\n4) 종합 의견 (매수/중립/매도 중 하나와 한 줄 근거)\n" +
                "마크다운 없이 평문으로 작성하세요.";

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

        private static string Trunc(string s) => s.Length > 200 ? s[..200] : s;
    }
}
