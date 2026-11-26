using ByteBench.Data;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace ByteBench.Services.Push;

/// <summary>The JSON a browser's <c>PushSubscription.toJSON()</c> produces (only what is used).</summary>
public sealed record PushSubscriptionDto(string? Endpoint, PushSubscriptionKeysDto? Keys);
public sealed record PushSubscriptionKeysDto(string? P256dh, string? Auth);
/// <summary>The body of <c>POST /push/resubscribe</c>: the replaced endpoint (if the browser still knew it) and the new subscription.</summary>
public sealed record ResubscribeDto(string? OldEndpoint, PushSubscriptionDto? Subscription);

/// <summary>
/// Browser push (Web Push, RFC 8030/8291/8292): services and endpoints.
/// <list type="bullet">
/// <item><c>GET /push/vapid-public-key</c>: the server's VAPID public key (base64url); anonymous, it is public.</item>
/// <item><c>POST /push/subscriptions</c>: save this browser's subscription for the signed-in user (cookie + antiforgery header).</item>
/// <item><c>POST /push/resubscribe</c>: the service worker swaps a browser-replaced subscription (cookie + custom header, no antiforgery token).</item>
/// <item><c>DELETE /push/subscriptions</c>: remove it (body: the subscription or <c>{ "endpoint": … }</c>).</item>
/// </list>
/// The service worker (<c>wwwroot/sw.js</c>) and the page script (<c>wwwroot/js/push.js</c>) are static files.
/// </summary>
public static class PushServiceCollectionExtensions
{
    public static IServiceCollection AddWebPush(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<PushOptions>(config.GetSection("Push"));
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<Api.PublicBaseUrl>();
        services.AddSingleton<PushEndpointGuard>();
        services.AddSingleton<VapidKeys>();
        services.AddSingleton<PushSender>();
        services.AddSingleton<PushSubscriptions>();
        services.AddSingleton<PushWorker>();
        services.AddHostedService(sp => sp.GetRequiredService<PushWorker>());
        return services;
    }

    public static void MapWebPush(this IEndpointRouteBuilder app)
    {
        app.MapGet("/push/vapid-public-key", async (IOptionsMonitor<PushOptions> options, VapidKeys vapid, HttpContext http) =>
        {
            if (!options.CurrentValue.Enabled) return Results.NotFound();
            http.Response.Headers.CacheControl = "no-store";
            return Results.Json(new { publicKey = await vapid.PublicKeyAsync(http.RequestAborted) });
        }).AllowAnonymous();

        app.MapPost("/push/subscriptions", async (HttpContext http, [Microsoft.AspNetCore.Mvc.FromBody] PushSubscriptionDto? body, PushSubscriptions subs, UserManager<User> users,
            IAntiforgery antiforgery, IOptionsMonitor<PushOptions> options) =>
        {
            if (await CheckAsync(http, users, antiforgery) is { } fail) return fail.Error;
            if (!options.CurrentValue.Enabled) return Problem("Browser push is turned off on this server.", 404);
            var userId = (await users.GetUserAsync(http.User))!.Id;
            var error = await subs.AddAsync(userId, body?.Endpoint, body?.Keys?.P256dh, body?.Keys?.Auth, http.Request.Headers.UserAgent.ToString(), http.RequestAborted);
            return error == null ? Results.Ok(new { subscribed = true }) : Problem(error, 400);
        }).AllowAnonymous().DisableAntiforgery(); // auth and antiforgery are checked by hand, so failures are JSON (not a login redirect)

        // The service worker's pushsubscriptionchange handler (wwwroot/sw.js) calls this: it has the session cookie but no page
        // to read an antiforgery token from. Instead the request must be same-site JSON with a custom header, which a cross-site
        // page can't send without a CORS preflight (this app answers none).
        app.MapPost("/push/resubscribe", async (HttpContext http, [Microsoft.AspNetCore.Mvc.FromBody] ResubscribeDto? body, PushSubscriptions subs, UserManager<User> users,
            IOptionsMonitor<PushOptions> options) =>
        {
            if (http.User.Identity?.IsAuthenticated != true) return Problem("Sign in first.", 401);
            var user = await users.GetUserAsync(http.User);
            if (user == null || user.DeactivatedAt != null) return Problem("Sign in first.", 401);
            if (http.Request.Headers["X-ByteBench-Push"] != "resubscribe"
                || string.Equals(http.Request.Headers["Sec-Fetch-Site"], "cross-site", StringComparison.OrdinalIgnoreCase))
                return Problem("Not a service worker resubscribe request.", 400);
            if (!options.CurrentValue.Enabled) return Problem("Browser push is turned off on this server.", 404);
            var error = await subs.ReplaceAsync(user.Id, body?.OldEndpoint, body?.Subscription?.Endpoint, body?.Subscription?.Keys?.P256dh, body?.Subscription?.Keys?.Auth,
                http.Request.Headers.UserAgent.ToString(), http.RequestAborted);
            return error == null ? Results.Ok(new { subscribed = true }) : Problem(error, 400);
        }).AllowAnonymous().DisableAntiforgery();

        app.MapDelete("/push/subscriptions", async (HttpContext http, [Microsoft.AspNetCore.Mvc.FromBody] PushSubscriptionDto? body, PushSubscriptions subs, UserManager<User> users, IAntiforgery antiforgery) =>
        {
            if (await CheckAsync(http, users, antiforgery) is { } fail) return fail.Error;
            var userId = (await users.GetUserAsync(http.User))!.Id;
            await subs.RemoveAsync(userId, body?.Endpoint, http.RequestAborted);
            return Results.NoContent();
        }).AllowAnonymous().DisableAntiforgery();
    }

    private sealed record Failure(IResult Error);

    /// <summary>A signed-in, active user (browser cookie) and a valid antiforgery token in the <c>RequestVerificationToken</c> header.</summary>
    private static async Task<Failure?> CheckAsync(HttpContext http, UserManager<User> users, IAntiforgery antiforgery)
    {
        if (http.User.Identity?.IsAuthenticated != true) return new(Problem("Sign in first.", 401));
        var user = await users.GetUserAsync(http.User);
        if (user == null || user.DeactivatedAt != null) return new(Problem("Sign in first.", 401));
        try { await antiforgery.ValidateRequestAsync(http); }
        catch (AntiforgeryValidationException) { return new(Problem("The request didn't carry a valid antiforgery token. Reload the page.", 400)); }
        return null;
    }

    private static IResult Problem(string message, int status) => Results.Json(new { error = message }, statusCode: status);
}
