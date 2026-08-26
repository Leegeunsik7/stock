// ============================================================================
// TelegramNotifier - 텔레그램 봇 알림 (기능 J)
// ============================================================================
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;

namespace MS2E.Infrastructure
{
    public static class TelegramNotifier
    {
        public static async Task<bool> SendAsync(LocalDatabase db, string message)
        {
            var token = db.GetSecret("Telegram.Token");
            var chatId = db.GetSetting("Telegram.ChatId");
            if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(chatId))
            {
                Log.Warn("[Telegram] 토큰/ChatId 미설정");
                return false;
            }

            try
            {
                var url = $"https://api.telegram.org/bot{token}/sendMessage";
                var body = new
                {
                    chat_id = chatId,
                    text = message,
                    parse_mode = "HTML",
                    disable_web_page_preview = true
                };
                var res = await HttpPool.Shared.PostAsJsonAsync(url, body);
                if (!res.IsSuccessStatusCode)
                {
                    var err = await res.Content.ReadAsStringAsync();
                    Log.Warn($"[Telegram] 전송 실패: {res.StatusCode} · {err}");
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                Log.Error("[Telegram] 예외", ex);
                return false;
            }
        }

        /// <summary>AlertCenter와 연동해 Critical 레벨 알림 자동 전송</summary>
        public static void AttachToAlertCenter(LocalDatabase db)
        {
            AlertCenter.Instance.AlertRaised += async (s, a) =>
            {
                if (a.Level < AlertLevel.Critical) return;
                var enabled = db.GetSetting("Telegram.Enabled") == "true";
                if (!enabled) return;
                var icon = a.Source switch
                {
                    AlertSource.Price => "💹",
                    AlertSource.Signal => "⚡",
                    AlertSource.News => "📰",
                    AlertSource.Dividend => "💸",
                    _ => "🔔"
                };
                var msg = $"{icon} <b>{a.Title}</b>\n{a.Message}";
                await SendAsync(db, msg);
            };
        }
    }
}
