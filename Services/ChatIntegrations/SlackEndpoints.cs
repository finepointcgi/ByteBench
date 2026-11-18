using System.Text;
using System.Text.Json.Nodes;
using GitBench.Api;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;

namespace GitBench.Services.ChatIntegrations;

/// <summary>
/// The Slack app's request URLs. Configure them in the Slack app as
/// <c>{PublicBaseUrl}/integrations/slack/commands</c> (slash command <c>/gitbench</c>),
/// <c>…/interactivity</c> (Interactivity &amp; Shortcuts) and <c>…/events</c> (Event Subscriptions, bot event
/// <c>link_shared</c>, with the GitBench domain under App unfurl domains). Every request must carry a valid Slack
/// signature (<see cref="SlackSignature"/>); without <c>Slack:SigningSecret</c> the endpoints answer 404.
/// </summary>
public static class SlackEndpoints
{
    public const int MaxBodyBytes = 64 * 1024;

    public static IEndpointRouteBuilder MapSlackIntegration(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/integrations/slack").DisableAntiforgery().AllowAnonymous()
            .AddEndpointFilter((ctx, next) =>
            {
                // Slack wants plain status codes, never the HTML status page (re-executing a POST there would turn 401 into 400).
                if (ctx.HttpContext.Features.Get<Microsoft.AspNetCore.Diagnostics.IStatusCodePagesFeature>() is { } pages) pages.Enabled = false;
                return next(ctx);
            });

        group.MapPost("/commands", async (HttpContext ctx, SlackCommandService commands, PublicBaseUrl baseUrl) =>
        {
            var (body, failure) = await ReadVerifiedAsync(ctx);
            if (failure != null) return failure;
            var form = QueryHelpers.ParseQuery(Encoding.UTF8.GetString(body)).ToDictionary(kv => kv.Key, kv => kv.Value.ToString());
            return Results.Json(await commands.HandleCommandAsync(form, baseUrl.For(ctx.Request)));
        });

        group.MapPost("/interactivity", async (HttpContext ctx, SlackCommandService commands, PublicBaseUrl baseUrl) =>
        {
            var (body, failure) = await ReadVerifiedAsync(ctx);
            if (failure != null) return failure;
            var form = QueryHelpers.ParseQuery(Encoding.UTF8.GetString(body));
            if (!form.TryGetValue("payload", out var raw) || ParseObject(raw.ToString()) is not { } payload) return Results.BadRequest();
            await commands.HandleInteractionAsync(payload, baseUrl.For(ctx.Request));
            return Results.Ok();
        });

        group.MapPost("/events", async (HttpContext ctx, SlackCommandService commands, PublicBaseUrl baseUrl) =>
        {
            var (body, failure) = await ReadVerifiedAsync(ctx);
            if (failure != null) return failure;
            if (ParseObject(Encoding.UTF8.GetString(body)) is not { } envelope) return Results.BadRequest();
            if (envelope["type"]?.GetValue<string>() == "url_verification")
                return Results.Json(new { challenge = envelope["challenge"]?.GetValue<string>() ?? "" });
            // Slack retries events it thinks we missed; we answered the first one already.
            if (ctx.Request.Headers.ContainsKey("X-Slack-Retry-Num")) return Results.Ok();
            await commands.HandleEventAsync(envelope, baseUrl.For(ctx.Request));
            return Results.Ok();
        });
        return app;
    }

    /// <summary>Reads the raw body (capped) and checks the Slack signature; on failure the result to return.</summary>
    private static async Task<(byte[] Body, IResult? Failure)> ReadVerifiedAsync(HttpContext ctx)
    {
        var slack = ctx.RequestServices.GetRequiredService<IOptionsMonitor<SlackOptions>>().CurrentValue;
        if (!slack.IsConfigured) return ([], Results.NotFound());
        if (ctx.Request.ContentLength > MaxBodyBytes) return ([], Results.StatusCode(StatusCodes.Status413PayloadTooLarge));
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int n;
        while ((n = await ctx.Request.Body.ReadAsync(chunk, ctx.RequestAborted)) > 0)
        {
            if (buffer.Length + n > MaxBodyBytes) return ([], Results.StatusCode(StatusCodes.Status413PayloadTooLarge));
            buffer.Write(chunk, 0, n);
        }
        var body = buffer.ToArray();
        var result = ctx.RequestServices.GetRequiredService<SlackSignature>().Verify(
            ctx.Request.Headers[SlackSignature.TimestampHeader].ToString(), ctx.Request.Headers[SlackSignature.SignatureHeader].ToString(), body);
        if (result != SlackSignatureResult.Valid)
        {
            ctx.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("GitBench.Slack")
                .LogWarning("Rejected a Slack request to {Path}: {Result}", ctx.Request.Path, result);
            return (body, Results.Unauthorized());
        }
        return (body, null);
    }

    private static JsonObject? ParseObject(string json)
    {
        try { return JsonNode.Parse(json) as JsonObject; }
        catch (System.Text.Json.JsonException) { return null; }
    }
}
