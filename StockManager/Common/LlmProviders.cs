// ============================================================================
// LlmProviders - LLM 제공자 추상화 + 로컬 LLM(Ollama) (Season 4 ①) (v3.34.0)
//  AiService의 단일 펀넬(CompleteAsync)이 이 라우터로 백엔드를 고른다:
//   · gemini  — 클라우드(키 필요)
//   · ollama  — 로컬(localhost), 오프라인·무한·완전 프라이빗(데이터가 기기를 안 떠남)
//  순수 결정론 코어 원칙: 라우팅 결정과 요청/응답 직렬화는 테스트 가능한 순수 함수로,
//  네트워크 호출만 어댑터로 분리. 외부 SDK 0(HTTP-JSON).
// ============================================================================
using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace StockManager.Common
{
    /// <summary>설정·가용성으로 사용할 LLM 백엔드를 결정(순수).</summary>
    public static class LlmRouter
    {
        /// <returns>"gemini" | "ollama" | "none"</returns>
        public static string Resolve(string? providerSetting, bool hasGeminiKey, bool ollamaEnabled)
        {
            var p = (providerSetting ?? "auto").Trim().ToLowerInvariant();
            return p switch
            {
                "ollama" => ollamaEnabled ? "ollama" : (hasGeminiKey ? "gemini" : "none"),
                "gemini" => hasGeminiKey ? "gemini" : (ollamaEnabled ? "ollama" : "none"),
                _ => hasGeminiKey ? "gemini" : (ollamaEnabled ? "ollama" : "none"),   // auto
            };
        }
    }

    /// <summary>로컬 Ollama(/api/generate) 어댑터. 페이로드/파싱은 순수, 호출만 비동기.</summary>
    public static class OllamaProvider
    {
        public static string DefaultUrl => "http://localhost:11434";
        public static string DefaultModel => "llama3.1";

        public static string BuildRequest(string model, string prompt) =>
            JsonSerializer.Serialize(new { model, prompt, stream = false });

        /// <summary>/api/generate 응답 JSON → 생성 텍스트(없으면 빈 문자열).</summary>
        public static string ParseResponse(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                return doc.RootElement.TryGetProperty("response", out var r) ? (r.GetString() ?? "") : "";
            }
            catch { return ""; }
        }

        public static async Task<string> GenerateAsync(HttpClient http, string baseUrl, string model, string prompt)
        {
            var url = (string.IsNullOrWhiteSpace(baseUrl) ? DefaultUrl : baseUrl).TrimEnd('/') + "/api/generate";
            try
            {
                using var res = await http.PostAsync(url,
                    new StringContent(BuildRequest(model, prompt), Encoding.UTF8, "application/json"));
                var json = await res.Content.ReadAsStringAsync();
                if (!res.IsSuccessStatusCode)
                    return $"⚠️ 로컬 LLM 호출 실패({(int)res.StatusCode}). Ollama 실행(ollama serve)·모델(ollama pull {model}) 확인.";
                var text = ParseResponse(json);
                return string.IsNullOrWhiteSpace(text) ? "(응답 없음)" : text;
            }
            catch (Exception ex)
            {
                return $"⚠️ 로컬 LLM 연결 실패: {ex.Message} — Ollama가 {baseUrl}에서 실행 중인지 확인하세요.";
            }
        }
    }
}
