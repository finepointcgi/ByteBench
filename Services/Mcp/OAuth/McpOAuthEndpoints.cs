using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using ByteBench.Api;
using ByteBench.Data;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace ByteBench.Services.Mcp.OAuth;

/// <summary>
/// The OAuth 2.1 authorization server for MCP clients (MCP authorization spec, 2025-06-18):
/// <list type="bullet">
/// <item><c>GET /.well-known/oauth-protected-resource[/mcp]</c> (RFC 9728) and <c>GET /.well-known/oauth-authorization-server</c> (RFC 8414).</item>
/// <item><c>POST /oauth/register</c> (RFC 7591, public clients only).</item>
/// <item><c>GET|POST /oauth/authorize</c>: needs a signed-in browser session; shows a consent page (Approve/Deny, antiforgery) and never approves by itself.</item>
/// <item><c>POST /oauth/token</c> (authorization_code with S256 PKCE, refresh_token with rotation) and <c>POST /oauth/revoke</c> (RFC 7009).</item>
/// </list>
/// The metadata, register, token and revoke endpoints answer CORS requests from any origin (no credentials); authorize never does.
/// </summary>
public static class McpOAuthEndpoints
{
    public const string ProtectedResourcePath = "/.well-known/oauth-protected-resource";
    public const string AuthorizationServerPath = "/.well-known/oauth-authorization-server";
    public static readonly string[] ScopesSupported = ["read", "write"];

    public static IServiceCollection AddMcpOAuth(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<McpOAuthOptions>(configuration.GetSection(McpOAuthOptions.Section));
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<OAuthRegistrationLimiter>();
        services.AddScoped<McpOAuthService>();
        return services;
    }

    public static bool IsEnabled(IServiceProvider sp) =>
        sp.GetRequiredService<IConfiguration>().GetValue("Mcp:Enabled", true) && sp.GetRequiredService<IOptionsMonitor<McpOAuthOptions>>().CurrentValue.Enabled;

    /// <summary>The site's base URL (the issuer) and the MCP resource URL.</summary>
    public static string Issuer(HttpContext http) => http.RequestServices.GetRequiredService<PublicBaseUrl>().For(http.Request);
    public static string Resource(HttpContext http) => Issuer(http) + McpEndpoint.Route;
    public static string ResourceMetadataUrl(HttpContext http) => Issuer(http) + ProtectedResourcePath + McpEndpoint.Route;

    public static IEndpointRouteBuilder MapMcpOAuth(this IEndpointRouteBuilder app)
    {
        if (!app.ServiceProvider.GetRequiredService<IOptionsMonitor<McpOAuthOptions>>().CurrentValue.Enabled) return app;

        // ----- metadata
        IResult ProtectedResource(HttpContext http)
        {
            Cors(http);
            return Json(new JsonObject
            {
                ["resource"] = Resource(http),
                ["authorization_servers"] = new JsonArray(Issuer(http)),
                ["scopes_supported"] = new JsonArray(ScopesSupported.Select(s => (JsonNode)s).ToArray()),
                ["bearer_methods_supported"] = new JsonArray("header"),
                ["resource_name"] = "ByteBench",
            }, 200, cacheable: true);
        }
        app.MapGet(ProtectedResourcePath, ProtectedResource).AllowAnonymous().ExcludeFromDescription();
        app.MapGet(ProtectedResourcePath + McpEndpoint.Route, ProtectedResource).AllowAnonymous().ExcludeFromDescription();
        app.MapGet(AuthorizationServerPath, (HttpContext http) =>
        {
            Cors(http);
            var issuer = Issuer(http);
            return Json(new JsonObject
            {
                ["issuer"] = issuer,
                ["authorization_endpoint"] = issuer + "/oauth/authorize",
                ["token_endpoint"] = issuer + "/oauth/token",
                ["registration_endpoint"] = issuer + "/oauth/register",
                ["revocation_endpoint"] = issuer + "/oauth/revoke",
                ["response_types_supported"] = new JsonArray("code"),
                ["response_modes_supported"] = new JsonArray("query"),
                ["grant_types_supported"] = new JsonArray("authorization_code", "refresh_token"),
                ["code_challenge_methods_supported"] = new JsonArray("S256"),
                ["token_endpoint_auth_methods_supported"] = new JsonArray("none"),
                ["revocation_endpoint_auth_methods_supported"] = new JsonArray("none"),
                ["scopes_supported"] = new JsonArray(ScopesSupported.Select(s => (JsonNode)s).ToArray()),
                ["authorization_response_iss_parameter_supported"] = true,
            }, 200, cacheable: true);
        }).AllowAnonymous().ExcludeFromDescription();

        // CORS preflight for the endpoints browser-based clients call.
        app.MapMethods("/oauth/{endpoint:regex(^(register|token|revoke)$)}", ["OPTIONS"], (HttpContext http) => { Cors(http); return Results.NoContent(); })
            .AllowAnonymous().DisableAntiforgery().ExcludeFromDescription();
        app.MapMethods(ProtectedResourcePath + "/{**rest}", ["OPTIONS"], (HttpContext http) => { Cors(http); return Results.NoContent(); }).AllowAnonymous().ExcludeFromDescription();
        app.MapMethods(ProtectedResourcePath, ["OPTIONS"], (HttpContext http) => { Cors(http); return Results.NoContent(); }).AllowAnonymous().ExcludeFromDescription();
        app.MapMethods(AuthorizationServerPath, ["OPTIONS"], (HttpContext http) => { Cors(http); return Results.NoContent(); }).AllowAnonymous().ExcludeFromDescription();

        // ----- registration
        app.MapPost("/oauth/register", async (HttpContext http, McpOAuthService oauth) =>
        {
            Cors(http);
            if (!ApiResults.IsJson(http.Request)) return Error(400, "invalid_client_metadata", "Send the registration as application/json.");
            JsonObject? body;
            try
            {
                if (http.Request.ContentLength > 64 * 1024) return Error(413, "invalid_client_metadata", "The registration is too large.");
                using var reader = new StreamReader(http.Request.Body);
                var text = await ReadLimitedAsync(reader, 64 * 1024);
                if (text == null) return Error(413, "invalid_client_metadata", "The registration is too large.");
                body = JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { MaxDepth = 8 }) as JsonObject;
            }
            catch (JsonException) { body = null; }
            catch (InvalidOperationException) { body = null; } // wrong JSON value types
            if (body == null) return Error(400, "invalid_client_metadata", "The body must be a JSON object.");
            try
            {
                var c = await oauth.RegisterAsync(body, http.Connection.RemoteIpAddress?.ToString() ?? "unknown");
                return Json(new JsonObject
                {
                    ["client_id"] = c.ClientId,
                    ["client_id_issued_at"] = new DateTimeOffset(DateTime.SpecifyKind(c.CreatedAt, DateTimeKind.Utc)).ToUnixTimeSeconds(),
                    ["client_name"] = c.ClientName,
                    ["redirect_uris"] = new JsonArray(c.RedirectUriList.Select(u => (JsonNode)u).ToArray()),
                    ["grant_types"] = new JsonArray("authorization_code", "refresh_token"),
                    ["response_types"] = new JsonArray("code"),
                    ["token_endpoint_auth_method"] = "none",
                    ["scope"] = "read write",
                }, 201);
            }
            catch (OAuthError e) { return Error(e.Status, e.Error, e.Description); }
            catch (InvalidOperationException) { return Error(400, "invalid_client_metadata", "A field has the wrong type."); }
        }).AllowAnonymous().DisableAntiforgery().ExcludeFromDescription();

        // ----- authorization (browser): no CORS
        app.MapGet("/oauth/authorize", (Delegate)AuthorizeGetAsync).DisableAntiforgery().ExcludeFromDescription();
        app.MapPost("/oauth/authorize", (Delegate)AuthorizePostAsync).DisableAntiforgery().ExcludeFromDescription();

        // ----- token & revoke
        app.MapPost("/oauth/token", async (HttpContext http, McpOAuthService oauth) =>
        {
            Cors(http);
            if (!http.Request.HasFormContentType) return Error(400, "invalid_request", "Send application/x-www-form-urlencoded.");
            var form = await http.Request.ReadFormAsync(http.RequestAborted);
            try
            {
                var r = form["grant_type"].ToString() switch
                {
                    "authorization_code" => await oauth.ExchangeCodeAsync(form, Resource(http)),
                    "refresh_token" => await oauth.RefreshAsync(form, Resource(http)),
                    "" => throw new OAuthError(400, "invalid_request", "grant_type is required."),
                    _ => throw new OAuthError(400, "unsupported_grant_type", "Use authorization_code or refresh_token."),
                };
                return Json(new JsonObject
                {
                    ["access_token"] = r.AccessToken, ["token_type"] = "Bearer", ["expires_in"] = r.ExpiresIn,
                    ["refresh_token"] = r.RefreshToken, ["scope"] = r.Scope,
                });
            }
            catch (OAuthError e) { return Error(e.Status, e.Error, e.Description); }
        }).AllowAnonymous().DisableAntiforgery().ExcludeFromDescription();

        app.MapPost("/oauth/revoke", async (HttpContext http, McpOAuthService oauth) =>
        {
            Cors(http);
            if (!http.Request.HasFormContentType) return Error(400, "invalid_request", "Send application/x-www-form-urlencoded.");
            var form = await http.Request.ReadFormAsync(http.RequestAborted);
            await oauth.RevokeTokenAsync(form["token"].ToString(), form["client_id"].ToString());
            ApiResults.PrepareResponse(http);
            return Results.Ok();
        }).AllowAnonymous().DisableAntiforgery().ExcludeFromDescription();
        return app;
    }

    // ------------------------------------------------------------------ authorize

    private static async Task<IResult> AuthorizeGetAsync(HttpContext http, McpOAuthService oauth, UserManager<User> users, IAntiforgery antiforgery)
    {
        var q = http.Request.Query;
        AuthorizeRequest request;
        try { request = await oauth.ValidateAuthorizeAsync(k => q[k].Count == 1 ? q[k].ToString() : null, Resource(http)); }
        catch (AuthorizeFatal e) { return McpOAuthPages.Error(http, e.Message); }
        catch (AuthorizeRedirectError e) { return RedirectWithError(http, e); }

        var user = await SignedInUserAsync(http, users);
        if (user == null)
        {
            // Sign in first, then come back here (a local path: the login page only follows local return URLs).
            var returnUrl = http.Request.PathBase + http.Request.Path + http.Request.QueryString;
            return Results.Redirect($"{http.Request.PathBase}/account/login?returnUrl={Uri.EscapeDataString(returnUrl)}");
        }
        var tokens = antiforgery.GetAndStoreTokens(http);
        return McpOAuthPages.Consent(http, request, user, tokens);
    }

    private static async Task<IResult> AuthorizePostAsync(HttpContext http, McpOAuthService oauth, UserManager<User> users, IAntiforgery antiforgery)
    {
        var user = await SignedInUserAsync(http, users);
        if (user == null) return McpOAuthPages.Error(http, "Your session has ended. Start signing in from the application again.", 401);
        if (!http.Request.HasFormContentType) return McpOAuthPages.Error(http, "The consent form wasn't sent correctly.");
        try { await antiforgery.ValidateRequestAsync(http); }
        catch (AntiforgeryValidationException) { return McpOAuthPages.Error(http, "The consent form expired or didn't come from ByteBench. Start again from the application."); }

        var form = await http.Request.ReadFormAsync(http.RequestAborted);
        AuthorizeRequest request;
        // Every parameter is validated again: the hidden fields are only a carrier.
        try { request = await oauth.ValidateAuthorizeAsync(k => form[k].Count == 1 ? form[k].ToString() : null, Resource(http)); }
        catch (AuthorizeFatal e) { return McpOAuthPages.Error(http, e.Message); }
        catch (AuthorizeRedirectError e) { return RedirectWithError(http, e); }

        var decision = form["decision"].ToString();
        if (decision == "approve")
        {
            var code = await oauth.ApproveAsync(request, user.Id);
            return RedirectToClient(http, request.RedirectUri, new() { ["code"] = code, ["state"] = request.State, ["iss"] = Issuer(http) });
        }
        await oauth.DenyAsync(request, user.Id);
        return RedirectToClient(http, request.RedirectUri, new()
        {
            ["error"] = "access_denied", ["error_description"] = "The user denied the request.", ["state"] = request.State, ["iss"] = Issuer(http),
        });
    }

    /// <summary>The cookie session's user, when the account can sign in.</summary>
    private static async Task<User?> SignedInUserAsync(HttpContext http, UserManager<User> users)
    {
        if (http.User.Identity?.IsAuthenticated != true) return null;
        var user = await users.GetUserAsync(http.User);
        if (user == null || user.DeactivatedAt != null || await users.IsLockedOutAsync(user)) return null;
        return user;
    }

    private static IResult RedirectWithError(HttpContext http, AuthorizeRedirectError e)
    {
        var p = new Dictionary<string, string?> { ["error"] = e.Error, ["error_description"] = e.Message, ["iss"] = Issuer(http) };
        if (e.State != null) p["state"] = e.State;
        return RedirectToClient(http, e.RedirectUri, p);
    }

    /// <summary>A 302 to the client's (registered, exactly matched) redirect URI.</summary>
    private static IResult RedirectToClient(HttpContext http, string redirectUri, Dictionary<string, string?> parameters)
    {
        ApiResults.PrepareResponse(http);
        return Results.Redirect(QueryHelpers.AddQueryString(redirectUri, parameters));
    }

    /// <summary>The CSP source for a redirect URI: <c>https://host:port</c>, or <c>scheme:</c> for a native app.</summary>
    public static string CspSource(string redirectUri)
    {
        var u = new Uri(redirectUri);
        return u.Scheme is "http" or "https" ? $"{u.Scheme}://{u.Authority}" : u.Scheme + ":";
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>CORS for the endpoints a browser-based MCP client may call: any origin, never credentials.</summary>
    private static void Cors(HttpContext http)
    {
        var h = http.Response.Headers;
        h.AccessControlAllowOrigin = "*";
        h.AccessControlAllowMethods = "GET, POST, OPTIONS";
        h.AccessControlAllowHeaders = "Content-Type, Authorization, MCP-Protocol-Version";
        h.AccessControlMaxAge = "600";
    }

    private static IResult Json(JsonObject body, int status = 200, bool cacheable = false)
    {
        return new JsonResult(body.ToJsonString(), status, cacheable);
    }

    private static IResult Error(int status, string error, string description) =>
        Json(new JsonObject { ["error"] = error, ["error_description"] = description }, status);

    private static async Task<string?> ReadLimitedAsync(StreamReader reader, int max)
    {
        var buffer = new char[4096];
        var sb = new System.Text.StringBuilder();
        int n;
        while ((n = await reader.ReadAsync(buffer)) > 0)
        {
            sb.Append(buffer, 0, n);
            if (sb.Length > max) return null;
        }
        return sb.ToString();
    }

    /// <summary>A JSON response with OAuth's caching rules: no-store, except the public metadata documents.</summary>
    private sealed class JsonResult(string json, int status, bool cacheable) : IResult
    {
        public async Task ExecuteAsync(HttpContext http)
        {
            if (http.Features.Get<Microsoft.AspNetCore.Diagnostics.IStatusCodePagesFeature>() is { } pages) pages.Enabled = false;
            http.Response.StatusCode = status;
            http.Response.ContentType = "application/json; charset=utf-8";
            http.Response.Headers.XContentTypeOptions = "nosniff";
            if (cacheable) http.Response.Headers.CacheControl = "public, max-age=300";
            else
            {
                http.Response.Headers.CacheControl = "no-store";
                http.Response.Headers.Pragma = "no-cache";
            }
            await http.Response.WriteAsync(json);
        }
    }

    internal static string Html(string? s) => HtmlEncoder.Default.Encode(s ?? "");
}
