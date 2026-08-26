// ============================================================================
// LlmProviders - LLM 제공자 추상화 + 로컬 LLM(Ollama) (Season 4 ①) (v3.34.0)
//  AiService의 단일 펀넬(CompleteAsync)이 이 라우터로 백엔드를 고른다:
//   · gemini  — 클라우드(키 필요)
//   · ollama  — 로컬(localhost), 오프라인·무한·완전 프라이빗(데이터가 기기를 안 떠남)
//  순수 결정론 코어 원칙: 라우팅 결정과 요청/응답 직렬화는 테스트 가능한 순수 함수로,
//  네트워크 호출만 어댑터로 분리. 외부 SDK 0(HTTP-JSON).
// ============================================================================
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MS2E.Infrastructure
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
        public const int DefaultTimeoutSec = 180;   // 로컬 생성은 느릴 수 있어 넉넉히(설정 가능)

        // 자체 HttpClient — 무한 타임아웃 + 호출마다 CTS로 제어(긴 로컬 생성/대용량 모델 대응)
        private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

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

        /// <summary>연결 점검 — /api/tags로 설치 모델 목록 조회(진단용). 실패 시 ⚠️ 메시지.</summary>
        public static async Task<string> PingAsync(string baseUrl)
        {
            var url = (string.IsNullOrWhiteSpace(baseUrl) ? DefaultUrl : baseUrl).TrimEnd('/') + "/api/tags";
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                var json = await Http.GetStringAsync(url, cts.Token);
                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("models", out var models)) return "✅ 연결됨 (모델 정보 없음)";
                var names = new List<string>();
                foreach (var m in models.EnumerateArray())
                    if (m.TryGetProperty("name", out var n)) names.Add(n.GetString() ?? "");
                return names.Count > 0 ? "✅ 연결됨 · 모델: " + string.Join(", ", names) : "✅ 연결됨 (설치된 모델 없음 — ollama pull 필요)";
            }
            catch (OperationCanceledException) { return "⚠️ 응답 없음(5초) — ollama serve 확인"; }
            catch (Exception ex) { return "⚠️ 연결 실패: " + ex.Message; }
        }

        public static async Task<string> GenerateAsync(string baseUrl, string model, string prompt, int timeoutSec = DefaultTimeoutSec)
        {
            var url = (string.IsNullOrWhiteSpace(baseUrl) ? DefaultUrl : baseUrl).TrimEnd('/') + "/api/generate";
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Clamp(timeoutSec, 10, 1200)));
            try
            {
                using var res = await Http.PostAsync(url,
                    new StringContent(BuildRequest(model, prompt), Encoding.UTF8, "application/json"), cts.Token);
                var json = await res.Content.ReadAsStringAsync(cts.Token);
                if (!res.IsSuccessStatusCode)
                    return $"⚠️ 로컬 LLM 호출 실패({(int)res.StatusCode}). Ollama 실행(ollama serve)·모델(ollama pull {model}) 확인.";
                var text = ParseResponse(json);
                return string.IsNullOrWhiteSpace(text) ? "(응답 없음)" : text;
            }
            catch (OperationCanceledException)
            {
                return $"⚠️ 로컬 LLM 타임아웃({timeoutSec}초 초과) — 더 작은 모델을 쓰거나 설정에서 타임아웃을 늘리세요.";
            }
            catch (Exception ex)
            {
                return $"⚠️ 로컬 LLM 연결 실패: {ex.Message} — Ollama가 {baseUrl}에서 실행 중인지 확인하세요.";
            }
        }
    }
}
