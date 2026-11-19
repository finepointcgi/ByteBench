using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GitBench.Api;
using GitBench.Data;
using Microsoft.Extensions.Primitives;

namespace GitBench.Services.Flags;

/// <summary>
/// The client API under <c>/api/v1/flags</c>. Authenticated only by an SDK key (<c>Authorization: Bearer gbsdk_...</c>, the
/// <c>X-GitBench-SDK-Key</c> header, or <c>?sdkKey=</c> for client keys, since browsers' EventSource can't set headers): never by a
/// personal access token or a browser session, so the endpoints are CORS-open (<c>Access-Control-Allow-Origin: *</c>, no credentials).
/// Evaluation happens on the server: client keys get values, server keys can also download rules (<c>/definitions</c>).
/// <list type="bullet">
/// <item><c>GET /evaluate?flag=KEY&amp;user=ID&amp;attr.plan=pro</c>: one flag.</item>
/// <item><c>GET /evaluate-all</c> and <c>POST /evaluate-all</c> (JSON body <c>{ "userId": "...", "attributes": { ... } }</c>): all flags of the environment.</item>
/// <item><c>GET /stream</c>: server-sent events with the evaluated flags, re-sent whenever something changes.</item>
/// <item><c>GET /definitions</c>: rules and segments, server keys only.</item>
/// </list>
/// Responses carry an <c>ETag</c>; send it back as <c>If-None-Match</c> to get <c>304</c> while nothing changed.
/// </summary>
public static class FlagSdkEndpoints
{
    private const string PrincipalKey = "gitbench.flags.principal";
    private const string KeyTextKey = "gitbench.flags.key";
    private static int openStreams;
    private const int MaxStreams = 1000;

    public static IEndpointRouteBuilder MapFlagSdk(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/v1/flags").WithTags("Flags SDK");
        g.DisableAntiforgery();
        g.AddEndpointFilter<SdkEndpointFilter>();

        g.MapMethods("/{**rest}", ["OPTIONS"], (HttpContext http) => Results.NoContent()).ExcludeFromDescription();
        g.MapGet("/evaluate", (Delegate)EvaluateOne).WithName("SdkEvaluateFlag").WithSummary("Evaluate one flag for a context")
            .WithDescription("Query: flag (key), user (id), attr.NAME=value. SDK key auth. ETag / If-None-Match supported.");
        g.MapPost("/evaluate", (Delegate)EvaluateOnePost).WithName("SdkEvaluateFlagPost").WithSummary("Evaluate one flag; the context is a JSON body");
        g.MapGet("/evaluate-all", (Delegate)EvaluateAll).WithName("SdkEvaluateAllFlags").WithSummary("Evaluate every flag of the environment for a context");
        g.MapPost("/evaluate-all", (Delegate)EvaluateAllPost).WithName("SdkEvaluateAllFlagsPost").WithSummary("Evaluate every flag; the context is a JSON body");
        g.MapGet("/stream", Stream).WithName("SdkFlagStream").WithSummary("Server-sent events: evaluated flags, re-sent on every change");
        g.MapGet("/definitions", (Delegate)Definitions).WithName("SdkFlagDefinitions").WithSummary("Rules and segments for local evaluation (server key)");
        return app;
    }

    // ----- Filter: CORS, SDK key auth, errors -----

    private sealed class SdkEndpointFilter : IEndpointFilter
    {
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
    {
        var http = ctx.HttpContext;
        ApiResults.PrepareResponse(http);
        var h = http.Response.Headers;
        h["Access-Control-Allow-Origin"] = "*"; // never with credentials: nothing here reads cookies
        h["Access-Control-Expose-Headers"] = "ETag";
        h.Vary = "Authorization, X-GitBench-SDK-Key";
        if (HttpMethods.IsOptions(http.Request.Method))
        {
            h["Access-Control-Allow-Methods"] = "GET, POST, OPTIONS";
            h["Access-Control-Allow-Headers"] = "Authorization, Content-Type, If-None-Match, X-GitBench-SDK-Key";
            h["Access-Control-Max-Age"] = "86400";
            return await next(ctx);
        }

        var presented = KeyFrom(http.Request, out var viaQuery);
        if (presented == null)
            return Unauthorized(http, "Send an SDK key: 'Authorization: Bearer gbsdk_...' or the X-GitBench-SDK-Key header.");
        if (!presented.StartsWith(FlagService.SdkKeyPrefix, StringComparison.Ordinal))
            return Unauthorized(http, "These endpoints only accept SDK keys (gbsdk_...), not personal access tokens. Create an SDK key on the project's Flags page.");
        var sdk = http.RequestServices.GetRequiredService<FlagSdkService>();
        var principal = await sdk.AuthenticateAsync(presented);
        if (principal == null) return Unauthorized(http, "The SDK key is invalid or revoked.");
        if (viaQuery && principal.IsServer)
            return ApiResults.Problem(StatusCodes.Status403Forbidden, "Forbidden", "Server SDK keys must not be sent in the URL; use the Authorization header.");
        http.Items[PrincipalKey] = principal;
        http.Items[KeyTextKey] = presented;
        try
        {
            return await next(ctx);
        }
        catch (ApiException ex)
        {
            return ApiResults.Problem(ex.Status, ex.Title, ex.Detail, ex.Extensions);
        }
        catch (OperationCanceledException) when (http.RequestAborted.IsCancellationRequested)
        {
            return Results.Empty;
        }
        catch (Exception ex)
        {
            http.RequestServices.GetRequiredService<ILogger<FlagSdkService>>().LogError(ex, "Flag SDK error on {Path}", http.Request.Path);
            return ApiResults.Problem(StatusCodes.Status500InternalServerError, "Internal Server Error", "An unexpected error occurred.");
        }
    }
    }

    private static string? KeyFrom(HttpRequest r, out bool viaQuery)
    {
        viaQuery = false;
        var auth = r.Headers.Authorization.ToString();
        if (auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return auth[7..].Trim();
        if (r.Headers["X-GitBench-SDK-Key"].ToString() is { Length: > 0 } header) return header.Trim();
        if (r.Query["sdkKey"].ToString() is { Length: > 0 } q) { viaQuery = true; return q.Trim(); }
        return null;
    }

    private static IResult Unauthorized(HttpContext http, string detail)
    {
        http.Response.Headers.WWWAuthenticate = "Bearer realm=\"GitBench flags\"";
        return ApiResults.Problem(StatusCodes.Status401Unauthorized, "Unauthorized", detail);
    }

    private static SdkPrincipal Principal(HttpContext http) => (SdkPrincipal)http.Items[PrincipalKey]!;

    // ----- Context -----

    private static FlagContext ContextFromQuery(HttpRequest r)
    {
        var c = new FlagContext { UserId = Clip(FirstNonEmpty(r.Query["user"], r.Query["userId"])) };
        foreach (var (k, v) in r.Query)
        {
            if (!k.StartsWith("attr.", StringComparison.OrdinalIgnoreCase) || k.Length <= 5) continue;
            Add(c, k[5..], v.ToString());
        }
        return c;
    }

    private static string? FirstNonEmpty(StringValues a, StringValues b) => a.ToString() is { Length: > 0 } x ? x : b.ToString() is { Length: > 0 } y ? y : null;

    private static string? Clip(string? s)
    {
        if (s != null && s.Length > 500) throw ApiException.BadRequest("Context values can be at most 500 characters.");
        return s;
    }

    private static void Add(FlagContext c, string name, string value)
    {
        if (c.Attributes.Count >= 50) throw ApiException.BadRequest("A context can have at most 50 attributes.");
        if (name.Length > 100) throw ApiException.BadRequest("Attribute names can be at most 100 characters.");
        c.Attributes[name] = Clip(value) ?? "";
    }

    private sealed record ContextBody(string? Flag = null, string? UserId = null, Dictionary<string, JsonElement>? Attributes = null);

    private static async Task<(FlagContext Context, string? Flag)> ContextFromBody(HttpRequest request)
    {
        var body = await ApiResults.ReadBodyAsync<ContextBody>(request);
        var c = new FlagContext { UserId = Clip(body.UserId) };
        foreach (var (k, v) in body.Attributes ?? [])
            Add(c, k, v.ValueKind switch { JsonValueKind.String => v.GetString() ?? "", JsonValueKind.Null => "", _ => v.GetRawText() });
        return (c, body.Flag);
    }

    // ----- Handlers -----

    private static Task<IResult> EvaluateOne(HttpContext http) =>
        OneAsync(http, ContextFromQuery(http.Request), http.Request.Query["flag"].ToString());

    private static async Task<IResult> EvaluateOnePost(HttpContext http)
    {
        var (ctx, flag) = await ContextFromBody(http.Request);
        return await OneAsync(http, ctx, flag ?? http.Request.Query["flag"].ToString());
    }

    private static async Task<IResult> OneAsync(HttpContext http, FlagContext ctx, string? flag)
    {
        if (string.IsNullOrWhiteSpace(flag)) throw ApiException.BadRequest("Pass the flag key as ?flag=KEY.");
        var p = Principal(http);
        var eval = await http.RequestServices.GetRequiredService<FlagSdkService>().EvaluateAsync(p, flag.Trim(), ctx)
                   ?? throw ApiException.NotFound($"There is no flag '{flag.Trim()}' in this environment.");
        var node = FlagSdkService.ToJson(eval.Results[0], p.IsServer);
        node["version"] = eval.Version;
        return await Conditional(http, node.ToJsonString());
    }

    private static Task<IResult> EvaluateAll(HttpContext http) => AllAsync(http, ContextFromQuery(http.Request));

    private static async Task<IResult> EvaluateAllPost(HttpContext http) => await AllAsync(http, (await ContextFromBody(http.Request)).Context);

    private static async Task<IResult> AllAsync(HttpContext http, FlagContext ctx)
    {
        var p = Principal(http);
        var eval = await http.RequestServices.GetRequiredService<FlagSdkService>().EvaluateAllAsync(p, ctx);
        return await Conditional(http, AllJson(eval, p).ToJsonString());
    }

    private static JsonObject AllJson(FlagEvaluation eval, SdkPrincipal p)
    {
        var flags = new JsonObject();
        foreach (var r in eval.Results) flags[r.Key] = FlagSdkService.ToJson(r, p.IsServer);
        return new JsonObject { ["version"] = eval.Version, ["environment"] = p.EnvironmentName, ["flags"] = flags };
    }

    private static async Task<IResult> Definitions(HttpContext http)
    {
        var p = Principal(http);
        if (!p.IsServer) throw new ApiException(StatusCodes.Status403Forbidden, "Forbidden", "Rules are only available to server SDK keys.");
        var body = await http.RequestServices.GetRequiredService<FlagSdkService>().DefinitionsAsync(p);
        return await Conditional(http, body.ToJsonString());
    }

    /// <summary>
    /// 200 with an ETag, or 304 when the client's If-None-Match matches. Written to the response directly (and <see cref="Results.Empty"/>
    /// returned): a content result returned from a handler behind this filter was not executed.
    /// </summary>
    private static async Task<IResult> Conditional(HttpContext http, string body)
    {
        var etag = FlagSdkService.ETag(body);
        http.Response.Headers.ETag = etag;
        http.Response.Headers.CacheControl = "private, no-cache"; // revalidate every time; a 304 is cheap
        if (Matches(http.Request.Headers.IfNoneMatch, etag))
        {
            http.Response.StatusCode = StatusCodes.Status304NotModified;
            return Results.Empty;
        }
        http.Response.ContentType = "application/json; charset=utf-8";
        await http.Response.WriteAsync(body, Encoding.UTF8, http.RequestAborted);
        return Results.Empty;
    }

    private static bool Matches(StringValues header, string etag)
    {
        foreach (var raw in header)
            foreach (var part in (raw ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                if (part == "*" || (part.StartsWith("W/", StringComparison.Ordinal) ? part[2..] : part) == etag) return true;
        return false;
    }

    // ----- Server-sent events -----

    private static async Task Stream(HttpContext http)
    {
        var p = Principal(http);
        var sdk = http.RequestServices.GetRequiredService<FlagSdkService>();
        var feed = http.RequestServices.GetRequiredService<FlagFeed>();
        var ctx = ContextFromQuery(http.Request);
        var key = (string)http.Items[KeyTextKey]!;
        var ct = http.RequestAborted;
        if (Interlocked.Increment(ref openStreams) > MaxStreams)
        {
            Interlocked.Decrement(ref openStreams);
            await ApiResults.Problem(StatusCodes.Status503ServiceUnavailable, "Service Unavailable", "Too many open flag streams. Try again shortly.").ExecuteAsync(http);
            return;
        }
        try
        {
            using var sub = feed.Subscribe(p.EnvironmentId, out var reader); // subscribe first: no change can slip between the first evaluation and the wait
            http.Response.ContentType = "text/event-stream";
            http.Response.Headers.CacheControl = "no-cache";
            http.Response.Headers["X-Accel-Buffering"] = "no";
            await http.Response.WriteAsync("retry: 3000\n\n", ct);

            string? last = null;
            async Task SendAsync()
            {
                var eval = await sdk.EvaluateAllAsync(p, ctx);
                var body = AllJson(eval, p).ToJsonString();
                if (body == last) return;
                last = body;
                await http.Response.WriteAsync($"id: {eval.Version}\nevent: flags\ndata: {body}\n\n", ct);
                await http.Response.Body.FlushAsync(ct);
            }

            await SendAsync();
            while (!ct.IsCancellationRequested)
            {
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
                wait.CancelAfter(TimeSpan.FromSeconds(20));
                try
                {
                    if (!await reader.WaitToReadAsync(wait.Token)) break;
                    while (reader.TryRead(out _)) { } // coalesce a burst into one update
                    await SendAsync();
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    // Idle: heartbeat, and stop if the key was revoked or rotated meanwhile.
                    if (await sdk.AuthenticateAsync(key) == null) break;
                    await http.Response.WriteAsync(": keep-alive\n\n", ct);
                    await http.Response.Body.FlushAsync(ct);
                }
            }
        }
        catch (OperationCanceledException) { }
        finally { Interlocked.Decrement(ref openStreams); }
    }
}
