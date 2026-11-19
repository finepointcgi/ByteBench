using System.Net;
using ByteBench.Data;
using Microsoft.AspNetCore.Identity;

namespace ByteBench.Services.Email;

/// <summary>
/// ASP.NET Core Identity's account emails (confirmation, password reset) through the same queue and sender as
/// notifications. Registered as <see cref="IEmailSender{TUser}"/>, so any Identity flow that sends mail uses it.
/// Identity passes links already HTML-encoded; they're decoded once and re-encoded for each body.
/// </summary>
public sealed class IdentityEmailSender(EmailQueue queue) : IEmailSender<User>
{
    public Task SendConfirmationLinkAsync(User user, string email, string confirmationLink) =>
        Send(user, email, "Confirm your ByteBench email", "Confirm your email address by opening this link:", confirmationLink);

    public Task SendPasswordResetLinkAsync(User user, string email, string resetLink) =>
        Send(user, email, "Reset your ByteBench password", "Reset your password by opening this link. If you didn't ask for this, ignore this email.", resetLink);

    public Task SendPasswordResetCodeAsync(User user, string email, string resetCode) =>
        Send(user, email, "Your ByteBench password reset code", "Your password reset code is:", null, resetCode);

    private Task Send(User user, string email, string subject, string intro, string? link, string? code = null)
    {
        var raw = link != null ? WebUtility.HtmlDecode(link) : null;
        var e = (string s) => WebUtility.HtmlEncode(s);
        var html = $"<!DOCTYPE html><html><body style=\"font-family:-apple-system,Segoe UI,Roboto,Helvetica,Arial,sans-serif;color:#1f2328;\">"
            + $"<p>Hi {e(user.DisplayName)},</p><p>{e(intro)}</p>"
            + (raw != null ? $"<p><a href=\"{e(raw)}\">{e(raw)}</a></p>" : "")
            + (code != null ? $"<p style=\"font-size:20px;font-family:monospace;\"><b>{e(code)}</b></p>" : "")
            + "<p style=\"color:#656d76;font-size:12px;\">ByteBench</p></body></html>";
        var text = $"Hi {user.DisplayName},\n\n{intro}\n\n{raw ?? code}\n\n-- ByteBench\n";
        queue.Enqueue(new EmailMessage(email, user.DisplayName, subject, html, text));
        return Task.CompletedTask;
    }
}
