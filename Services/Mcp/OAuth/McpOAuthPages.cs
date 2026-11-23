using System.Text;
using ByteBench.Api;
using ByteBench.Data;
using Microsoft.AspNetCore.Antiforgery;
using static ByteBench.Services.Mcp.OAuth.McpOAuthEndpoints;

namespace ByteBench.Services.Mcp.OAuth;

/// <summary>
/// The consent and error pages of <c>/oauth/authorize</c>: plain server-rendered HTML with no script. Everything the client
/// supplied (its name, the redirect host) is HTML-encoded. Each page sends its own Content-Security-Policy; the consent
/// page's form-action also allows the client's redirect origin, because browsers apply form-action to the redirect that
/// follows the form.
/// </summary>
internal static class McpOAuthPages
{
    private const string Style = """
        body{font-family:system-ui,-apple-system,"Segoe UI",Roboto,sans-serif;background:#f5f5f7;color:#1f2328;margin:0;padding:24px 16px}
        main{max-width:460px;margin:40px auto;background:#fff;border:1px solid #d0d7de;border-radius:10px;padding:24px}
        h1{font-size:1.25rem;margin:0 0 12px}
        p,li{line-height:1.5}
        .app{font-weight:600;word-break:break-word}
        .muted{color:#59636e;font-size:.9rem}
        .warn{background:#fff8c5;border:1px solid #d4a72c;border-radius:6px;padding:8px 10px;font-size:.9rem}
        .host{font-family:ui-monospace,SFMono-Regular,Menlo,monospace;word-break:break-all}
        .actions{display:flex;gap:12px;margin-top:20px}
        button{font:inherit;padding:8px 16px;border-radius:6px;border:1px solid #d0d7de;background:#f6f8fa;cursor:pointer}
        button.primary{background:#1f6feb;border-color:#1f6feb;color:#fff}
        @media (prefers-color-scheme: dark){body{background:#0d1117;color:#e6edf3}main{background:#161b22;border-color:#30363d}.muted{color:#9198a1}
        .warn{background:#3b2e00;border-color:#9e6a03}button{background:#21262d;border-color:#30363d;color:#e6edf3}}
        """;

    private static IResult Page(HttpContext http, string title, string body, int status, string formAction = "'self'")
    {
        ApiResults.PrepareResponse(http);
        http.Response.Headers.ContentSecurityPolicy =
            $"default-src 'none'; style-src 'unsafe-inline'; img-src 'self'; form-action {formAction}; frame-ancestors 'none'; base-uri 'none'";
        var html = $"""
            <!DOCTYPE html>
            <html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
            <meta name="referrer" content="no-referrer"><title>{Html(title)} · ByteBench</title><style>{Style}</style></head>
            <body><main>{body}</main></body></html>
            """;
        return Results.Content(html, "text/html; charset=utf-8", Encoding.UTF8, status);
    }

    public static IResult Error(HttpContext http, string message, int status = 400) =>
        Page(http, "Can't connect the application", $"""
            <h1>Can't connect the application</h1>
            <p>{Html(message)}</p>
            <p class="muted">Nothing was shared with the application. You can close this page.</p>
            """, status);

    public static string ScopeDescription(TokenScopes scopes) => scopes.HasFlag(TokenScopes.WriteApi)
        ? "<li>Read your projects, issues, pull requests, code and CI runs</li><li><b>Create and change</b> issues and comments as you</li>"
        : "<li>Read your projects, issues, pull requests, code and CI runs</li>";

    public static IResult Consent(HttpContext http, AuthorizeRequest r, User user, AntiforgeryTokenSet tokens)
    {
        var host = McpOAuthService.RedirectDisplay(r.RedirectUri);
        var fields = new Dictionary<string, string?>
        {
            ["client_id"] = r.Client.ClientId, ["redirect_uri"] = r.RedirectUri, ["state"] = r.State, ["response_type"] = "code",
            ["code_challenge"] = r.CodeChallenge, ["code_challenge_method"] = "S256", ["scope"] = r.ScopeText, ["resource"] = r.Resource,
        };
        var hidden = new StringBuilder();
        foreach (var (k, v) in fields.Where(f => f.Value != null))
            hidden.Append($"<input type=\"hidden\" name=\"{Html(k)}\" value=\"{Html(v)}\">");
        hidden.Append($"<input type=\"hidden\" name=\"{Html(tokens.FormFieldName)}\" value=\"{Html(tokens.RequestToken)}\">");
        var action = http.Request.PathBase + "/oauth/authorize";

        return Page(http, "Authorize application", $"""
            <h1>Allow <span class="app">{Html(r.Client.ClientName)}</span> to use ByteBench as you?</h1>
            <p class="muted">Signed in as <b>{Html(user.DisplayName)}</b> (@{Html(user.UserName)})</p>
            <p>The application will be able to:</p>
            <ul>{ScopeDescription(r.Scopes)}</ul>
            <p>After you approve, ByteBench sends you back to <span class="host">{Html(host)}</span>.</p>
            <p class="warn">The name is chosen by the application itself and isn't verified. Only approve if you just started connecting this application. Its access ends when you revoke it under Profile, Use with AI tools / CLI, or when your account is deactivated.</p>
            <form method="post" action="{Html(action)}">
            {hidden}
            <div class="actions">
            <button class="primary" type="submit" name="decision" value="approve">Approve</button>
            <button type="submit" name="decision" value="deny">Deny</button>
            </div>
            </form>
            """, 200, "'self' " + CspSource(r.RedirectUri));
    }
}
