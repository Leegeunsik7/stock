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

namespace MS2E.Infrastructure
{
    public static class EmailSender
    {
        public static bool Configured(LocalDatabase db)
        {
            var cfg = new DatabaseConfigAdapter(db);   // Phase 84 — config 포트 경유(직결 해방·콜드 경로)
            return !string.IsNullOrWhiteSpace(cfg.GetString("Email.Smtp", "")) &&
                   !string.IsNullOrWhiteSpace(cfg.GetString("Email.To", ""));
        }

        public static async Task<bool> SendAsync(LocalDatabase db, string subject, string body,
            byte[]? pdf = null, string pdfName = "report.pdf")
        {
            try
            {
                var cfg = new DatabaseConfigAdapter(db);   // Phase 84 — config 포트 경유(직결 해방·콜드 경로)
                var host = cfg.GetString("Email.Smtp", "");
                var to = cfg.GetString("Email.To", "");
                if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(to)) return false;
                int port = int.TryParse(cfg.GetString("Email.Port", ""), out var p) && p > 0 ? p : 587;
                var user = cfg.GetString("Email.User", "");
                var pass = db.GetSecret("Email.Pass");
                var from = string.IsNullOrWhiteSpace(cfg.GetString("Email.From", ""))
                    ? (string.IsNullOrWhiteSpace(user) ? to! : user!) : cfg.GetString("Email.From", "");

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
