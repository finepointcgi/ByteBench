using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace ByteBench.Tests.Sso;

/// <summary>A user the mock identity provider signs in.</summary>
public sealed record MockUser(string Sub, string? Email, bool EmailVerified = true, string? Name = null, string? UserName = null,
    string[]? Groups = null, string[]? Amr = null);

/// <summary>
/// A tiny OpenID Connect provider (and GitHub-style OAuth endpoints) on a real Kestrel port, for tests and local manual
/// testing. It implements discovery, JWKS, the authorization code flow with PKCE (S256 only) and nonce, RS256-signed ID
/// tokens and userinfo. Which user "signs in" is <see cref="NextUser"/>; when it's null, /authorize shows a small HTML
/// form to type one in (manual testing).
/// <para>GitHub (Enterprise-style URLs): /login/oauth/authorize, /login/oauth/access_token and /api/v3/user, /user/emails, /user/orgs, /user/teams.</para>
/// </summary>
public sealed class MockOidcProvider : IAsyncDisposable
{
    public const string ClientSecret = "mock-client-secret";

    private readonly RSA rsa = RSA.Create(2048);
    private readonly RsaSecurityKey key;
    private readonly ConcurrentDictionary<string, (MockUser User, string ClientId, string RedirectUri, string? Challenge, string? Nonce)> codes = new();
    private readonly ConcurrentDictionary<string, MockUser> accessTokens = new();
    private WebApplication? web;

    public MockOidcProvider() => key = new RsaSecurityKey(rsa) { KeyId = "mock-key-1" };

    /// <summary>The user the next /authorize signs in (tests). Null: show a form.</summary>
    public MockUser? NextUser { get; set; }
    /// <summary>The query of the last authorization request (to check PKCE, state and nonce were sent).</summary>
    public IReadOnlyDictionary<string, string> LastAuthorize { get; private set; } = new Dictionary<string, string>();
    /// <summary>Token requests whose PKCE verifier didn't match (should stay 0).</summary>
    public int PkceFailures { get; private set; }

    public string BaseUrl { get; private set; } = "";

    public async Task StartAsync(int port = 0)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrel(o => o.Listen(IPAddress.Loopback, port));
        builder.Services.AddRouting();
        web = builder.Build();
        Map(web);
        await web.StartAsync();
        var address = web.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        BaseUrl = address.Replace("[::1]", "127.0.0.1").TrimEnd('/');
    }

    public async ValueTask DisposeAsync()
    {
        if (web != null) await web.DisposeAsync();
        rsa.Dispose();
    }

    private void Map(WebApplication app)
    {
        app.MapGet("/.well-known/openid-configuration", () => Results.Json(new Dictionary<string, object>
        {
            ["issuer"] = BaseUrl,
            ["authorization_endpoint"] = BaseUrl + "/authorize",
            ["token_endpoint"] = BaseUrl + "/token",
            ["userinfo_endpoint"] = BaseUrl + "/userinfo",
            ["jwks_uri"] = BaseUrl + "/jwks",
            ["response_types_supported"] = new[] { "code" },
            ["subject_types_supported"] = new[] { "public" },
            ["id_token_signing_alg_values_supported"] = new[] { "RS256" },
            ["code_challenge_methods_supported"] = new[] { "S256" },
            ["scopes_supported"] = new[] { "openid", "email", "profile" },
        }));

        app.MapGet("/jwks", () =>
        {
            var p = rsa.ExportParameters(false);
            return Results.Json(new { keys = new[] { new { kty = "RSA", use = "sig", alg = "RS256", kid = key.KeyId, n = Base64UrlEncoder.Encode(p.Modulus), e = Base64UrlEncoder.Encode(p.Exponent) } } });
        });

        app.MapGet("/authorize", (HttpContext ctx) => Authorize(ctx));
        app.MapGet("/login/oauth/authorize", (HttpContext ctx) => Authorize(ctx));
        app.MapPost("/authorize/approve", async (HttpRequest req) =>
        {
            var f = await req.ReadFormAsync();
            var user = new MockUser(f["sub"]!, NullIfEmpty(f["email"]), f["verified"] == "on", NullIfEmpty(f["name"]), NullIfEmpty(f["username"]),
                Split(f["groups"]), Split(f["amr"]));
            var q = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(f["query"]!)!;
            return IssueCode(user, q);
        });

        app.MapPost("/token", (HttpRequest req) => Token(req.HttpContext)); // not (HttpContext): that binds as a RequestDelegate and drops the result
        app.MapPost("/login/oauth/access_token", (HttpRequest req) => Token(req.HttpContext));

        app.MapGet("/userinfo", (HttpContext ctx) => UserOf(ctx) is { } u ? Results.Json(Claims(u, null)) : Results.Unauthorized());

        // GitHub API (Enterprise layout: {server}/api/v3).
        app.MapGet("/api/v3/user", (HttpContext ctx) => UserOf(ctx) is { } u
            ? Results.Json(new { id = long.Parse(u.Sub), login = u.UserName ?? "octo", name = u.Name, email = (string?)null })
            : Results.Unauthorized());
        app.MapGet("/api/v3/user/emails", (HttpContext ctx) => UserOf(ctx) is { } u
            ? Results.Json(u.Email == null ? [] : new[] { new { email = u.Email, primary = true, verified = u.EmailVerified, visibility = "private" } })
            : Results.Unauthorized());
        app.MapGet("/api/v3/user/orgs", (HttpContext ctx) => UserOf(ctx) is { } u
            ? Results.Json((u.Groups ?? []).Where(g => !g.Contains('/')).Select(g => new { login = g }))
            : Results.Unauthorized());
        app.MapGet("/api/v3/user/teams", (HttpContext ctx) => UserOf(ctx) is { } u
            ? Results.Json((u.Groups ?? []).Where(g => g.Contains('/')).Select(g => new { slug = g.Split('/')[1], organization = new { login = g.Split('/')[0] } }))
            : Results.Unauthorized());
    }

    private IResult Authorize(HttpContext ctx)
    {
        var q = ctx.Request.Query.ToDictionary(kv => kv.Key, kv => kv.Value.ToString());
        LastAuthorize = q;
        if (!q.TryGetValue("redirect_uri", out var redirect) || !q.ContainsKey("client_id") || q.GetValueOrDefault("response_type", "code") != "code")
            return Results.BadRequest("invalid_request");
        if (q.TryGetValue("code_challenge_method", out var method) && method != "S256") return Results.BadRequest("only S256");
        if (NextUser is { } user) return IssueCode(user, q);

        // Manual testing: a form to choose who signs in.
        var json = WebUtility.HtmlEncode(System.Text.Json.JsonSerializer.Serialize(q));
        var html = $"""
            <!doctype html><meta charset="utf-8"><title>Mock IdP</title>
            <body style="font-family:sans-serif;max-width:420px;margin:40px auto">
            <h2>Mock identity provider</h2><p>Sign in to <b>{WebUtility.HtmlEncode(q["client_id"])}</b> as:</p>
            <form method="post" action="/authorize/approve">
              <input type="hidden" name="query" value="{json}">
              <p><label>Subject <input name="sub" value="mock-{Guid.NewGuid():N}"></label></p>
              <p><label>Email <input name="email" value="sso-user@example.org"></label></p>
              <p><label><input type="checkbox" name="verified" checked> Email verified</label></p>
              <p><label>Name <input name="name" value="Sam Sso"></label></p>
              <p><label>Username <input name="username" value="sam.sso"></label></p>
              <p><label>Groups (comma-separated) <input name="groups" value=""></label></p>
              <p><label>amr (comma-separated) <input name="amr" value="pwd"></label></p>
              <button type="submit">Sign in</button>
            </form></body>
            """;
        return Results.Content(html, "text/html; charset=utf-8");
    }

    private IResult IssueCode(MockUser user, IReadOnlyDictionary<string, string> q)
    {
        var code = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(24));
        codes[code] = (user, q["client_id"], q["redirect_uri"], q.GetValueOrDefault("code_challenge"), q.GetValueOrDefault("nonce"));
        var sep = q["redirect_uri"].Contains('?') ? '&' : '?';
        var location = $"{q["redirect_uri"]}{sep}code={Uri.EscapeDataString(code)}";
        if (q.TryGetValue("state", out var state)) location += "&state=" + Uri.EscapeDataString(state);
        return Results.Redirect(location);
    }

    private async Task<IResult> Token(HttpContext ctx)
    {
        var f = await ctx.Request.ReadFormAsync();
        var (clientId, secret) = (f["client_id"].ToString(), f["client_secret"].ToString());
        var basic = ctx.Request.Headers.Authorization.ToString();
        if (basic.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            var parts = Encoding.UTF8.GetString(Convert.FromBase64String(basic[6..])).Split(':', 2);
            (clientId, secret) = (Uri.UnescapeDataString(parts[0]), Uri.UnescapeDataString(parts[1]));
        }
        if (!codes.TryRemove(f["code"].ToString(), out var grant)) return Results.Json(new { error = "invalid_grant" }, statusCode: 400);
        if (grant.ClientId != clientId || grant.RedirectUri != f["redirect_uri"].ToString()) return Results.Json(new { error = "invalid_grant" }, statusCode: 400);
        if (secret.Length > 0 && secret != ClientSecret) return Results.Json(new { error = "invalid_client" }, statusCode: 401);
        if (grant.Challenge != null)
        {
            var verifier = f["code_verifier"].ToString();
            var computed = Base64UrlEncoder.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
            if (computed != grant.Challenge)
            {
                PkceFailures++;
                return Results.Json(new { error = "invalid_grant", error_description = "PKCE verification failed" }, statusCode: 400);
            }
        }
        var accessToken = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(24));
        accessTokens[accessToken] = grant.User;
        var idToken = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = BaseUrl,
            Audience = clientId,
            IssuedAt = DateTime.UtcNow,
            NotBefore = DateTime.UtcNow.AddSeconds(-5),
            Expires = DateTime.UtcNow.AddMinutes(5),
            Claims = Claims(grant.User, grant.Nonce),
            SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.RsaSha256),
        });
        return Results.Json(new { access_token = accessToken, token_type = "Bearer", expires_in = 300, id_token = idToken, scope = "openid email profile" });
    }

    private MockUser? UserOf(HttpContext ctx)
    {
        var auth = ctx.Request.Headers.Authorization.ToString();
        var token = auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? auth[7..].Trim() : auth.StartsWith("token ", StringComparison.OrdinalIgnoreCase) ? auth[6..].Trim() : "";
        return accessTokens.TryGetValue(token, out var u) ? u : null;
    }

    private static Dictionary<string, object> Claims(MockUser u, string? nonce)
    {
        var c = new Dictionary<string, object> { ["sub"] = u.Sub };
        if (u.Email != null) { c["email"] = u.Email; c["email_verified"] = u.EmailVerified; }
        if (u.Name != null) c["name"] = u.Name;
        if (u.UserName != null) c["preferred_username"] = u.UserName;
        if (u.Groups is { Length: > 0 }) c["groups"] = u.Groups;
        if (u.Amr is { Length: > 0 }) c["amr"] = u.Amr;
        if (nonce != null) c["nonce"] = nonce;
        return c;
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    private static string[]? Split(string? s) =>
        string.IsNullOrWhiteSpace(s) ? null : s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
