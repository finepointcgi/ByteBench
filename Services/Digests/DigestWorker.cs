using System.Net;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace ByteBench.Services.Digests;

/// <summary>
/// Sends due digests: every <c>Digest:PollMinutes</c> (default 5) it runs <see cref="DigestService.RunOnceAsync"/> at the
/// <see cref="TimeProvider"/>'s current time. Off with <c>Digest:Enabled=false</c> (tests drive the pass themselves).
/// </summary>
public sealed class DigestWorker(DigestService digests, IOptionsMonitor<DigestOptions> options, TimeProvider clock, ILogger<DigestWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.CurrentValue.Enabled) return;
        await Task.Yield();
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var r = await digests.RunOnceAsync(clock.GetUtcNow().UtcDateTime, stoppingToken);
                if (r.Claimed > 0) log.LogInformation("Digest pass: {Sent} sent, {Empty} with nothing to report, {Failed} failed", r.Sent, r.Empty, r.Failed);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { log.LogError(ex, "Digest pass failed"); }
            try { await Task.Delay(TimeSpan.FromMinutes(Math.Clamp(options.CurrentValue.PollMinutes, 1, 60)), clock, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }
}

public static class DigestServiceCollectionExtensions
{
    /// <summary>Digest emails (<c>Digest</c> section). Needs <c>AddEmail</c> (sender, composer).</summary>
    public static IServiceCollection AddDigests(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<DigestOptions>(config.GetSection("Digest"));
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<DigestUnsubscribeTokens>();
        services.AddSingleton<DigestPreviewLimiter>();
        services.AddSingleton<DigestService>();
        services.AddScoped<DigestBuilder>();
        services.AddHostedService<DigestWorker>();
        return services;
    }

    /// <summary>
    /// <c>GET /account/digest/unsubscribe?token=…</c> shows a confirmation button (no sign-in needed); <c>POST</c> turns digests
    /// off. Mail clients that support RFC 8058 one-click unsubscribe POST directly. The signed token is the only credential.
    /// </summary>
    public static void MapDigestUnsubscribe(this IEndpointRouteBuilder app)
    {
        app.MapGet(DigestService.UnsubscribePath, (string? token, DigestUnsubscribeTokens tokens) =>
        {
            if (tokens.Read(token) == null) return Page("Link expired", "<p>This unsubscribe link is invalid or has expired. Change your digest on the <a href=\"/account/notifications\">notification preferences</a> page instead.</p>", 400);
            return Page("Unsubscribe from digests", $"""
                <p>Stop your ByteBench digest emails? Other notification emails and in-app notifications don't change.</p>
                <form method="post" action="{DigestService.UnsubscribePath}"><input type="hidden" name="token" value="{WebUtility.HtmlEncode(token)}" />
                <button type="submit" style="background:#4f46e5;color:#fff;border:0;border-radius:6px;padding:8px 14px;cursor:pointer;">Unsubscribe</button></form>
                """);
        }).AllowAnonymous();

        app.MapPost(DigestService.UnsubscribePath, async (HttpContext ctx, DigestUnsubscribeTokens tokens, DigestService digests, ILoggerFactory lf) =>
        {
            string? token = ctx.Request.Query["token"];
            if (string.IsNullOrEmpty(token) && ctx.Request.HasFormContentType) token = (await ctx.Request.ReadFormAsync())["token"];
            if (tokens.Read(token) is not { } userId || !await digests.DisableAsync(userId))
                return Page("Link expired", "<p>This unsubscribe link is invalid or has expired.</p>", 400);
            lf.CreateLogger("ByteBench.Digest").LogInformation("User {UserId} unsubscribed from digest emails", userId);
            return Page("Unsubscribed", "<p>You won't get digest emails any more. Turn them back on from your <a href=\"/account/notifications\">notification preferences</a>.</p>");
        }).AllowAnonymous().DisableAntiforgery();
    }

    private static IResult Page(string title, string body, int status = 200) => Results.Content($"""
        <!DOCTYPE html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
        <title>{title} · ByteBench</title><style>:root{"{"}color-scheme:light dark{"}"}</style></head>
        <body style="font-family:-apple-system,Segoe UI,Roboto,Helvetica,Arial,sans-serif;max-width:520px;margin:48px auto;padding:0 16px;">
        <h1 style="font-size:1.4rem;">{title}</h1>{body}</body></html>
        """, "text/html; charset=utf-8", statusCode: status);
}
