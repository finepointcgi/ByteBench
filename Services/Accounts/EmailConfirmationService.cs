using System.Diagnostics;
using ByteBench.Data;
using ByteBench.Services.Audit;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace ByteBench.Services.Accounts;

/// <summary>
/// Email confirmation. With <c>Account:RequireEmailConfirmation</c> a password sign-in needs <see cref="User.EmailConfirmed"/>.
/// <para>
/// <b>The exact rule.</b> With the switch off (default) nothing here changes sign-in and <c>EmailConfirmed</c> is only used as
/// before (SSO account linking). With it on, a user can sign in with a password only if <c>EmailConfirmed</c> is true. Seeded
/// demo users, SSO/LDAP-created users, system accounts and users who reset their password by email are confirmed; accounts
/// without a local password are never blocked; every other existing account (a self-registered user who never confirmed) is
/// blocked at sign-in until they confirm through the resend page or an admin marks the address confirmed.
/// </para>
/// </summary>
public class EmailConfirmationService(UserManager<User> users, AccountEmails emails, AccountThrottle throttle, IOptionsMonitor<AccountOptions> options, AuditLog audit)
{
    public bool Required => options.CurrentValue.RequireEmailConfirmation;

    /// <summary>Emails the confirmation link of <paramref name="user"/>'s current address (registration, admin resend).</summary>
    public async Task<bool> SendAsync(User user)
    {
        var token = await users.GenerateEmailConfirmationTokenAsync(user);
        return emails.SendConfirmation(user, token, options.CurrentValue.EmailConfirmationHours);
    }

    /// <summary>The resend form: neutral answer, rate limited per IP (visible) and per account (silent).</summary>
    public async Task<bool> RequestResendAsync(string? email, string? ip)
    {
        var started = Stopwatch.GetTimestamp();
        var o = options.CurrentValue;
        try
        {
            if (!throttle.TryHit("confirm:ip:" + (ip ?? "?"), o.MaxRequestsPerIp, o.ThrottleWindow)) return false;
            email = email?.Trim();
            var user = string.IsNullOrEmpty(email) ? null : await users.FindByEmailAsync(email);
            if (user == null || user.EmailConfirmed || user.PasswordHash == null || SystemAccounts.IsSystem(user) || user.DeactivatedAt != null) return true;
            if (!throttle.TryHit("confirm:acct:" + user.Id, o.MaxRequestsPerAccount, o.ThrottleWindow)) return true;
            await SendAsync(user);
            await audit.RecordAsync("auth.email_confirmation_sent", "user", user.Id, actorId: user.Id);
            return true;
        }
        finally { await PasswordResetService.PadAsync(started, o.ResponseFloorMs); }
    }

    public async Task<bool> ConfirmAsync(int userId, string? token)
    {
        if (string.IsNullOrEmpty(token) || await users.FindByIdAsync(userId.ToString()) is not { } user || SystemAccounts.IsSystem(user)) return false;
        if (user.EmailConfirmed) return await users.VerifyUserTokenAsync(user, users.Options.Tokens.EmailConfirmationTokenProvider, "EmailConfirmation", token);
        var result = await users.ConfirmEmailAsync(user, token);
        if (!result.Succeeded) return false;
        await audit.RecordAsync("auth.email_confirmed", "user", user.Id, actorId: user.Id);
        return true;
    }

    /// <summary>
    /// Starts an email change for the signed-in user: the new address gets a link and the old one stays until it is used.
    /// Returns an error message, or null when the link was queued.
    /// </summary>
    public async Task<string?> RequestChangeAsync(User user, string newEmail)
    {
        newEmail = newEmail.Trim();
        if (await users.FindByEmailAsync(newEmail) is { } other && other.Id != user.Id) return $"Email '{newEmail}' is already in use by another account.";
        var o = options.CurrentValue;
        if (!throttle.TryHit("change:acct:" + user.Id, o.MaxRequestsPerAccount, o.ThrottleWindow))
            return "Too many confirmation emails were requested. Wait a few minutes and try again.";
        if (!emails.IsEnabled) return "Email is switched off on this server (Email:Enabled), so the new address can't be confirmed. Ask a site admin.";
        var token = await users.GenerateChangeEmailTokenAsync(user, newEmail);
        if (!emails.SendEmailChange(user, newEmail, token, o.EmailConfirmationHours)) return "The confirmation email couldn't be queued. Try again in a moment.";
        await audit.RecordAsync("user.email_change_requested", "user", user.Id, details: new { newEmail }, actorId: user.Id);
        return null;
    }

    /// <summary>Applies the change when the link's token is valid for this user and address. Other sessions end (security stamp).</summary>
    public async Task<bool> ConfirmChangeAsync(int userId, string? newEmail, string? token)
    {
        if (string.IsNullOrEmpty(token) || string.IsNullOrWhiteSpace(newEmail) || await users.FindByIdAsync(userId.ToString()) is not { } user || SystemAccounts.IsSystem(user)) return false;
        var old = user.Email;
        var result = await users.ChangeEmailAsync(user, newEmail.Trim(), token);
        if (!result.Succeeded) return false;
        await audit.RecordAsync("user.email_changed", "user", user.Id, details: new { from = old, to = newEmail.Trim() }, actorId: user.Id);
        return true;
    }
}
