using System.Net;
using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;

namespace ByteBench.Services.Email;

/// <summary>Signed, expiring tokens for the one-click unsubscribe link in notification emails (no sign-in needed).</summary>
public sealed class UnsubscribeTokens(IDataProtectionProvider provider)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(90);

    private readonly ITimeLimitedDataProtector protector =
        // Stored identifier: keep "GitBench" (the product was renamed to ByteBench). Changing it would make existing encrypted data/tokens unreadable.
        provider.CreateProtector("GitBench.Email.Unsubscribe.v1").ToTimeLimitedDataProtector();

    public string Create(int userId) => protector.Protect(userId.ToString(System.Globalization.CultureInfo.InvariantCulture), Lifetime);

    /// <summary>The user id in <paramref name="token"/>, or null if it is invalid or expired.</summary>
    public int? Read(string? token)
    {
        if (string.IsNullOrEmpty(token)) return null;
        try { return int.TryParse(protector.Unprotect(token), out var id) ? id : null; }
        catch (CryptographicException) { return null; }
    }
}

/// <summary>
/// <c>GET /email/unsubscribe?t=…</c> shows a confirmation button; <c>POST</c> turns notification emails off. Mail
/// clients that support RFC 8058 one-click unsubscribe POST directly. The signed token is the only credential.
/// </summary>
public static class UnsubscribeEndpoints
{
    public static void MapEmailUnsubscribe(this IEndpointRouteBuilder app)
    {
        app.MapGet("/email/unsubscribe", (string? t, UnsubscribeTokens tokens) =>
        {
            if (tokens.Read(t) == null) return Page("Link expired", "<p>This unsubscribe link is invalid or has expired. Change your settings on the <a href=\"/account/notifications\">notification preferences</a> page instead.</p>", 400);
            var token = WebUtility.HtmlEncode(t);
            return Page("Unsubscribe", $"""
                <p>Stop all ByteBench notification emails? You'll still see notifications in the app.</p>
                <form method="post" action="/email/unsubscribe"><input type="hidden" name="t" value="{token}" />
                <button type="submit" style="background:#4f46e5;color:#fff;border:0;border-radius:6px;padding:8px 14px;cursor:pointer;">Unsubscribe</button></form>
                """);
        }).AllowAnonymous();

        app.MapPost("/email/unsubscribe", async (HttpContext ctx, UnsubscribeTokens tokens, NotificationPreferences prefs, ILoggerFactory lf) =>
        {
            string? t = ctx.Request.Query["t"];
            if (string.IsNullOrEmpty(t) && ctx.Request.HasFormContentType) t = (await ctx.Request.ReadFormAsync())["t"];
            if (tokens.Read(t) is not { } userId) return Page("Link expired", "<p>This unsubscribe link is invalid or has expired.</p>", 400);
            await prefs.SetEmailEnabledAsync(userId, false);
            lf.CreateLogger("ByteBench.Email").LogInformation("User {UserId} unsubscribed from notification emails", userId);
            return Page("Unsubscribed", "<p>You won't get notification emails any more. Turn them back on from your <a href=\"/account/notifications\">notification preferences</a>.</p>");
        }).AllowAnonymous().DisableAntiforgery();
    }

    private static IResult Page(string title, string body, int status = 200) => Results.Content($"""
        <!DOCTYPE html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
        <title>{title} · ByteBench</title><style>:root{"{"}color-scheme:light dark{"}"}</style></head>
        <body style="font-family:-apple-system,Segoe UI,Roboto,Helvetica,Arial,sans-serif;max-width:520px;margin:48px auto;padding:0 16px;">
        <h1 style="font-size:1.4rem;">{title}</h1>{body}</body></html>
        """, "text/html; charset=utf-8", statusCode: status);
}
