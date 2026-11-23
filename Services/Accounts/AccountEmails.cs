using System.Net;
using ByteBench.Data;
using ByteBench.Services.Email;

namespace ByteBench.Services.Accounts;

/// <summary>
/// Sends account emails. The default goes through <see cref="EmailQueue"/> (Email:Enabled, Pickup or SMTP sender); tests
/// replace it with an in-memory mailbox so nothing is delivered and nothing is awaited on a background worker.
/// </summary>
public interface IAccountMailer
{
    /// <summary>False when email is switched off (<c>Email:Enabled=false</c>) or the queue dropped the message.</summary>
    bool Send(EmailMessage message);

    /// <summary>Whether <see cref="Send"/> can deliver anything at all.</summary>
    bool IsEnabled { get; }
}

public sealed class QueueAccountMailer(EmailQueue queue) : IAccountMailer
{
    public bool IsEnabled => queue.IsEnabled;
    public bool Send(EmailMessage message) => queue.Enqueue(message);
}

/// <summary>Builds the account emails (reset link, password changed, confirmation). Links are absolute; every value is HTML-encoded.</summary>
public sealed class AccountEmails(IAccountMailer mailer, NotificationEmailComposer links)
{
    public bool IsEnabled => mailer.IsEnabled;

    public string ResetLink(int userId, string token) => links.Absolute($"/account/reset-password?uid={userId}&token={Uri.EscapeDataString(token)}");
    public string ConfirmLink(int userId, string token) => links.Absolute($"/account/confirm-email?uid={userId}&token={Uri.EscapeDataString(token)}");
    public string ChangeLink(int userId, string newEmail, string token) =>
        links.Absolute($"/account/confirm-email-change?uid={userId}&email={Uri.EscapeDataString(newEmail)}&token={Uri.EscapeDataString(token)}");
    public string InviteLink(string token) => links.Absolute($"/account/register?invite={Uri.EscapeDataString(token)}");

    public bool SendPasswordReset(User user, string token, int minutes, bool byAdmin) => Send(user, user.Email!,
        "Reset your ByteBench password",
        byAdmin ? "A site administrator started a password reset for your account." : "Someone (hopefully you) asked to reset your ByteBench password.",
        "Choose a new password",
        ResetLink(user.Id, token),
        $"The link works once and expires in {Describe(minutes)}. If you didn't ask for this, ignore this email: your password stays as it is.");

    public bool SendPasswordChanged(User user, string reason) => Send(user, user.Email!,
        "Your ByteBench password was changed",
        reason, null, null,
        "If this wasn't you, reset your password right away from the sign-in page and tell your site admin. Other sessions were signed out.");

    public bool SendConfirmation(User user, string token, int hours) => Send(user, user.Email!,
        "Confirm your ByteBench email address",
        "Confirm that this email address belongs to you.",
        "Confirm email address",
        ConfirmLink(user.Id, token),
        $"The link expires in {Describe(hours * 60)}. If you didn't create a ByteBench account, ignore this email.");

    public bool SendEmailChange(User user, string newEmail, string token, int hours) => Send(user, newEmail,
        "Confirm your new ByteBench email address",
        $"You asked to change the email address of your ByteBench account to {newEmail}. It only changes once you confirm it here.",
        "Confirm new address",
        ChangeLink(user.Id, newEmail, token),
        $"The link expires in {Describe(hours * 60)}. If you didn't ask for this, ignore this email: the address stays as it is.");

    private bool Send(User user, string to, string subject, string intro, string? action, string? link, string footer)
    {
        static string e(string? s) => WebUtility.HtmlEncode(s ?? "");
        var html = "<!DOCTYPE html><html><body style=\"margin:0;padding:24px;background:#f6f7f9;font-family:-apple-system,Segoe UI,Roboto,Helvetica,Arial,sans-serif;color:#1f2328;\">"
            + "<div style=\"max-width:600px;margin:0 auto;background:#ffffff;border:1px solid #d0d7de;border-radius:8px;padding:20px;\">"
            + "<div style=\"font-weight:700;color:#6366f1;margin-bottom:12px;\">◆ ByteBench</div>"
            + $"<p>Hi {e(user.DisplayName)},</p><p>{e(intro)}</p>"
            + (link != null
                ? $"<p style=\"margin:16px 0;\"><a href=\"{e(link)}\" style=\"display:inline-block;background:#4f46e5;color:#ffffff;padding:8px 14px;border-radius:6px;text-decoration:none;\">{e(action)}</a></p>"
                  + $"<p style=\"color:#656d76;font-size:12px;word-break:break-all;\">{e(link)}</p>"
                : "")
            + $"<p style=\"color:#656d76;font-size:13px;\">{e(footer)}</p></div></body></html>";
        var text = $"Hi {user.DisplayName},\n\n{intro}\n\n" + (link != null ? $"{action}: {link}\n\n" : "") + $"{footer}\n\n-- ByteBench\n";
        return mailer.Send(new EmailMessage(to, user.DisplayName, subject, html, text)
        {
            Headers = new Dictionary<string, string> { ["Auto-Submitted"] = "auto-generated" },
        });
    }

    private static string Describe(int minutes) =>
        minutes >= 120 && minutes % 60 == 0 ? $"{minutes / 60} hours" : minutes == 60 ? "1 hour" : $"{minutes} minutes";
}
