// ============================================================================
// EmailSender - SMTP 메일 발송 (Season 4 ③) (v3.36.0)
//  외부 패키지 없이 BCL System.Net.Mail로 야간 리포트(본문+PDF 첨부)를 발송.
//  자격증명: SMTP 호스트/포트/계정 + 비밀번호는 DPAPI Secret(Email.Pass)에 보관.
//  설정 미비 시 조용히 false 반환(헤드리스에서 예외 전파 방지).
// ============================================================================
using System;
using System.IO;
using System.Net;
using System.Net.Mail;
using System.Threading.Tasks;

namespace StockManager.Common
{
    public static class EmailSender
    {
        public static bool Configured(LocalDatabase db) =>
            !string.IsNullOrWhiteSpace(db.GetSetting("Email.Smtp")) &&
            !string.IsNullOrWhiteSpace(db.GetSetting("Email.To"));

        public static async Task<bool> SendAsync(LocalDatabase db, string subject, string body,
            byte[]? pdf = null, string pdfName = "report.pdf")
        {
            try
            {
                var host = db.GetSetting("Email.Smtp");
                var to = db.GetSetting("Email.To");
                if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(to)) return false;
                int port = int.TryParse(db.GetSetting("Email.Port"), out var p) && p > 0 ? p : 587;
                var user = db.GetSetting("Email.User");
                var pass = db.GetSecret("Email.Pass");
                var from = string.IsNullOrWhiteSpace(db.GetSetting("Email.From"))
                    ? (string.IsNullOrWhiteSpace(user) ? to! : user!) : db.GetSetting("Email.From")!;

                using var msg = new MailMessage(from, to!) { Subject = subject, Body = body };
                if (pdf is { Length: > 0 })
                    msg.Attachments.Add(new Attachment(new MemoryStream(pdf), pdfName, "application/pdf"));

                using var client = new SmtpClient(host, port) { EnableSsl = true };
                if (!string.IsNullOrWhiteSpace(user))
                    client.Credentials = new NetworkCredential(user, pass ?? "");
                await client.SendMailAsync(msg);
                return true;
            }
            catch (Exception ex) { Log.Warn($"[Email] 발송 실패: {ex.Message}"); return false; }
        }
    }
}
