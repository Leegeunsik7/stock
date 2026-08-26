using System;
using System.Net;
using System.Net.Mail;
using System.Threading.Tasks;

namespace StockManager.Common.Account;

/// <summary>이메일 본인인증 발송 결과. Sent=실발송 성공 · Demo=SMTP 미구성 데모모드(코드 인라인 노출) · Error=실패사유.</summary>
public sealed record OtpSendResult(bool Sent, bool Demo, string? DemoCode, string? Error);

/// <summary>
/// [MS2E StockSuite] 회원가입 이메일 본인인증 OTP 발송 — BCL System.Net.Mail.SmtpClient 만 사용(외부 NuGet 0).
///   앱의 기존 이메일 설정(DB: Email.Smtp/Email.Port/Email.User/Email.From + DPAPI 비밀 Email.Pass · 야간 리포트와 동일 키)을
///   재사용해 구성 시 실발송, 미구성이면 <b>데모 모드</b>로 코드를 화면에 노출해 폐쇄망에서도 인증을 완료한다. 모니터링/스마트팜/PLAI/UAV와 동일 설계.
/// </summary>
public sealed class StockEmailOtpService
{
    private readonly LocalDatabase _db;
    public StockEmailOtpService(LocalDatabase db) => _db = db;

    /// <summary>DB 이메일 설정으로 실발송 가능 여부. false 면 데모 모드.</summary>
    public bool SmtpConfigured =>
        !string.IsNullOrWhiteSpace(_db.GetSetting("Email.Smtp"))
        && !string.IsNullOrWhiteSpace(_db.GetSetting("Email.From"))
        && !string.IsNullOrWhiteSpace(_db.GetSecret("Email.Pass"));

    /// <summary>인증번호 메일 발송. 실발송 성공→Sent · 미구성→Demo(코드 노출) · 실패→Error.</summary>
    public async Task<OtpSendResult> SendOtpAsync(string to, string code)
    {
        if (string.IsNullOrWhiteSpace(to)) return new OtpSendResult(false, false, null, "수신 이메일이 없습니다.");
        if (!SmtpConfigured) return new OtpSendResult(false, true, code, null);   // 데모 모드 — 코드 인라인 노출
        try
        {
            var host = (_db.GetSetting("Email.Smtp") ?? "").Trim();
            var port = int.TryParse(_db.GetSetting("Email.Port"), out var p) ? p : 587;
            var from = (_db.GetSetting("Email.From") ?? "").Trim();
            var authUser = string.IsNullOrWhiteSpace(_db.GetSetting("Email.User")) ? from : _db.GetSetting("Email.User")!.Trim();
            var pass = (_db.GetSecret("Email.Pass") ?? "").Replace(" ", "");

            using var msg = BuildOtpMessage(from, to.Trim(), code);
            using var client = new SmtpClient(host, port)
            {
                EnableSsl = true,
                DeliveryMethod = SmtpDeliveryMethod.Network,
                Credentials = new NetworkCredential(authUser, pass),
                Timeout = 20000,
            };
            await client.SendMailAsync(msg).ConfigureAwait(false);
            return new OtpSendResult(true, false, null, null);
        }
        catch (Exception ex)
        {
            return new OtpSendResult(false, false, null, ex.Message);
        }
    }

    private static MailMessage BuildOtpMessage(string from, string to, string code)
    {
        var body =
            "<div style='font-family:Pretendard,Segoe UI,sans-serif;max-width:480px'>" +
            "<h2 style='color:#4f5bd5;margin:0 0 12px'>📈 MS2E StockSuite 이메일 본인인증</h2>" +
            "<p>요청하신 인증번호는 아래와 같습니다. <b>3분 이내</b>에 입력해 주세요.</p>" +
            $"<div style='font-size:2rem;font-weight:800;letter-spacing:.3em;color:#111;background:#eef0ff;border:1px solid #cfd4ff;border-radius:10px;padding:16px;text-align:center;margin:12px 0'>{code}</div>" +
            "<p style='color:#888;font-size:.8rem'>본인이 요청하지 않았다면 이 메일을 무시하세요. · MS2E StockSuite</p></div>";
        return new MailMessage(from, to, "[MS2E StockSuite] 이메일 본인인증 번호", body) { IsBodyHtml = true };
    }
}
