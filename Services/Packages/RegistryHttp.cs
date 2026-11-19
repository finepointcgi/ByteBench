using System.Text.Json;
using System.Text.Json.Serialization;
using ByteBench.Data;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http.Features;

namespace ByteBench.Services.Packages;

/// <summary>An authorized registry request: who, and in which project.</summary>
public sealed record RegistryRequest(RegistryIdentity Identity, Project Project);

/// <summary>Shared plumbing of the NuGet, npm and OCI endpoints: raw responses, JSON, and authenticate-then-authorize.</summary>
internal static class RegistryHttp
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>No HTML status-code pages, no caching.</summary>
    public static void Prepare(HttpContext ctx)
    {
        if (ctx.Features.Get<IStatusCodePagesFeature>() is { } pages) pages.Enabled = false;
        ctx.Response.Headers.CacheControl = "no-store";
    }

    /// <summary>Lifts Kestrel's 30 MB body limit to the blob limit for an upload.</summary>
    public static void AllowLargeBody(HttpContext ctx, long max)
    {
        if (ctx.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } f) f.MaxRequestBodySize = max + 1;
    }

    public static async Task JsonAsync(HttpContext ctx, int status, object body, string contentType = "application/json")
    {
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = contentType;
        await JsonSerializer.SerializeAsync(ctx.Response.Body, body, body.GetType(), Json, ctx.RequestAborted);
    }

    /// <summary>
    /// Authenticates and authorizes; on failure writes the response with <paramref name="fail"/> (status, message) after
    /// setting <paramref name="challenge"/> as <c>WWW-Authenticate</c> on a 401, and returns null.
    /// </summary>
    public static async Task<RegistryRequest?> GuardAsync(HttpContext ctx, string projectKey, RegistryOp op, string challenge,
        Func<int, string, Task> fail, bool allowCookie = false)
    {
        var auth = ctx.RequestServices.GetRequiredService<RegistryAuth>();
        var (identity, error) = await auth.AuthenticateAsync(ctx, allowCookie);
        var check = await auth.AuthorizeAsync(identity, projectKey, op);
        if (check.Allowed) return new RegistryRequest(identity!, check.Project!);
        var status = check.Decision switch
        {
            RegistryDecision.Unauthenticated => 401,
            RegistryDecision.Forbidden => 403,
            _ => 404,
        };
        if (status == 401) ctx.Response.Headers.WWWAuthenticate = challenge;
        await fail(status, status == 401 ? error ?? check.Message : check.Message);
        return null;
    }
}
