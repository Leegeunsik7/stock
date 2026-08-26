// ============================================================================
// ConnectionTester - 외부 서비스 연결 테스트 (Gemini / KIS / Telegram)
//  WinForms SetupWizard와 Blazor 설정 페이지(/setup)가 공용으로 사용.
//  실제 HTTP 호출로 키 유효성을 검증하고 (성공여부, 메시지)를 반환.
// ============================================================================
using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;

namespace StockManager.Common
{
    public static class ConnectionTester
    {
        public static async Task<(bool ok, string msg)> Gemini(string key)
        {
            if (string.IsNullOrWhiteSpace(key)) return (false, "API Key를 입력하세요");
            var url = $"https://generativelanguage.googleapis.com/v1beta/models?key={Uri.EscapeDataString(key)}";
            using var res = await HttpPool.Shared.GetAsync(url);
            if (res.IsSuccessStatusCode) return (true, "정상 — 모델 목록 조회 성공");
            return (false, $"{(int)res.StatusCode} {res.StatusCode} — 키를 확인하세요");
        }

        public static async Task<(bool ok, string msg)> Kis(string key, string secret, bool paper)
        {
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(secret))
                return (false, "App Key / Secret을 입력하세요");
            var baseUrl = paper
                ? "https://openapivts.koreainvestment.com:29443"
                : "https://openapi.koreainvestment.com:9443";
            var body = new { grant_type = "client_credentials", appkey = key, appsecret = secret };
            using var res = await HttpPool.Shared.PostAsJsonAsync($"{baseUrl}/oauth2/tokenP", body);
            var json = await res.Content.ReadFromJsonAsync<JsonElement>();
            if (res.IsSuccessStatusCode &&
                json.TryGetProperty("access_token", out var t) &&
                !string.IsNullOrEmpty(t.GetString()))
                return (true, $"정상 — 토큰 발급 ({(paper ? "모의투자" : "실전")})");
            string err = json.TryGetProperty("error_description", out var e)
                ? e.GetString() ?? "" : res.StatusCode.ToString();
            return (false, $"실패 — {err}");
        }

        public static async Task<(bool ok, string msg)> Telegram(string token, string chatId)
        {
            if (string.IsNullOrWhiteSpace(token)) return (false, "Bot Token을 입력하세요");
            using var res = await HttpPool.Shared.GetAsync($"https://api.telegram.org/bot{token}/getMe");
            var json = await res.Content.ReadFromJsonAsync<JsonElement>();
            if (!(res.IsSuccessStatusCode && json.TryGetProperty("ok", out var okp) && okp.GetBoolean()))
                return (false, "Bot Token을 확인하세요");
            var uname = json.GetProperty("result").TryGetProperty("username", out var u)
                ? "@" + u.GetString() : "bot";
            if (string.IsNullOrWhiteSpace(chatId))
                return (true, $"정상 — {uname} (Chat ID 미입력 시 알림 비활성)");

            var send = new { chat_id = chatId, text = "✅ MS2-E Stock Suite 설정 — 연결 테스트" };
            using var sres = await HttpPool.Shared.PostAsJsonAsync($"https://api.telegram.org/bot{token}/sendMessage", send);
            if (!sres.IsSuccessStatusCode) return (false, $"{uname} 인증 OK · 전송 실패 (Chat ID 확인)");
            return (true, $"정상 — {uname} · 테스트 메시지 전송됨");
        }
    }
}
