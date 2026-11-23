using System.Buffers.Text;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ByteBench.Data;
using ByteBench.Services.Accounts;
using ByteBench.Services.Mcp.OAuth;
using ByteBench.Tests.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;

namespace ByteBench.Tests;

/// <summary>
/// An OAuth 2.1 public client for the tests, the way an MCP client does it: register, authorize with S256 PKCE through the
/// consent page (in a browser-like cookie client), exchange the code, refresh, revoke. Everything goes over HTTP.
/// </summary>
public sealed partial class TestOAuthClient(TestApp app, string redirectUri = TestOAuthClient.Loopback)
{
    public const string Loopback = "http://127.0.0.1:53682/callback";
    public const string Resource = "http://localhost/mcp";

    public string RedirectUri { get; } = redirectUri;
    public string ClientId { get; private set; } = "";
    public string Verifier { get; private set; } = "";
    public string Challenge => Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(Verifier)));

    public HttpClient Anonymous() => app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });

    public static string NewVerifier() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(48));

    public async Task<TestOAuthClient> RegisterAsync(string name = "Test MCP client", params string[] extraRedirects)
    {
        var r = await Anonymous().PostAsync("/oauth/register", JsonBody(new
        {
            client_name = name, redirect_uris = new[] { RedirectUri }.Concat(extraRedirects).ToArray(),
            token_endpoint_auth_method = "none", grant_types = new[] { "authorization_code", "refresh_token" }, response_types = new[] { "code" },
        }));
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        ClientId = (await JsonAsync(r))["client_id"]!.GetValue<string>();
        return this;
    }

    public static StringContent JsonBody(object o) => new(System.Text.Json.JsonSerializer.Serialize(o), Encoding.UTF8, "application/json");
    public static async Task<JsonObject> JsonAsync(HttpResponseMessage r) => JsonNode.Parse(await r.Content.ReadAsStringAsync())!.AsObject();

    /// <summary>The authorization request query, with overrides (null removes a parameter).</summary>
    public string AuthorizeUrl(Dictionary<string, string?>? overrides = null)
    {
        Verifier = NewVerifier();
        var p = new Dictionary<string, string?>
        {
            ["response_type"] = "code", ["client_id"] = ClientId, ["redirect_uri"] = RedirectUri, ["state"] = "st-" + Guid.NewGuid().ToString("N")[..8],
            ["code_challenge"] = Challenge, ["code_challenge_method"] = "S256", ["scope"] = "read", ["resource"] = Resource,
        };
        foreach (var (k, v) in overrides ?? []) p[k] = v;
        return QueryHelpers.AddQueryString("/oauth/authorize", p.Where(x => x.Value != null).ToDictionary(x => x.Key, x => x.Value)!);
    }

    [GeneratedRegex("<input type=\"hidden\" name=\"([^\"]+)\" value=\"([^\"]*)\">")]
    private static partial Regex HiddenRx();

    public static Dictionary<string, string> HiddenFields(string html) =>
        HiddenRx().Matches(html).ToDictionary(m => WebUtility.HtmlDecode(m.Groups[1].Value), m => WebUtility.HtmlDecode(m.Groups[2].Value));

    /// <summary>Opens the consent page in <paramref name="browser"/> and presses Approve (or Deny). Returns the redirect.</summary>
    public async Task<HttpResponseMessage> ConsentAsync(HttpClient browser, string decision = "approve", Dictionary<string, string?>? overrides = null)
    {
        var page = await browser.GetAsync(AuthorizeUrl(overrides));
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var fields = HiddenFields(await page.Content.ReadAsStringAsync());
        fields["decision"] = decision;
        return await browser.PostAsync("/oauth/authorize", new FormUrlEncodedContent(fields));
    }

    public static Dictionary<string, string> Query(HttpResponseMessage redirect)
    {
        Assert.Equal(HttpStatusCode.Redirect, redirect.StatusCode);
        var location = redirect.Headers.Location!.ToString();
        var q = location.Contains('?') ? location[(location.IndexOf('?') + 1)..] : "";
        return QueryHelpers.ParseQuery(q).ToDictionary(x => x.Key, x => x.Value.ToString());
    }

    /// <summary>Approves in the browser and returns the authorization code.</summary>
    public async Task<string> CodeAsync(HttpClient browser, Dictionary<string, string?>? overrides = null)
    {
        var redirect = await ConsentAsync(browser, overrides: overrides);
        Assert.StartsWith(RedirectUri + "?", redirect.Headers.Location!.ToString());
        return Query(redirect)["code"];
    }

    public Task<HttpResponseMessage> TokenAsync(Dictionary<string, string> form) =>
        Anonymous().PostAsync("/oauth/token", new FormUrlEncodedContent(form));

    public Task<HttpResponseMessage> ExchangeAsync(string code, string? verifier = null, string? redirect = null, string? resource = Resource)
    {
        var f = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code", ["code"] = code, ["client_id"] = ClientId, ["redirect_uri"] = redirect ?? RedirectUri,
            ["code_verifier"] = verifier ?? Verifier,
        };
        if (resource != null) f["resource"] = resource;
        return TokenAsync(f);
    }

    public Task<HttpResponseMessage> RefreshAsync(string refreshToken, string? scope = null)
    {
        var f = new Dictionary<string, string> { ["grant_type"] = "refresh_token", ["refresh_token"] = refreshToken, ["client_id"] = ClientId };
        if (scope != null) f["scope"] = scope;
        return TokenAsync(f);
    }

    /// <summary>The whole flow: returns (access, refresh).</summary>
    public async Task<(string Access, string Refresh)> SignInAsync(HttpClient browser, string scope = "read")
    {
        var code = await CodeAsync(browser, new() { ["scope"] = scope });
        var r = await ExchangeAsync(code);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var json = await JsonAsync(r);
        return (json["access_token"]!.GetValue<string>(), json["refresh_token"]!.GetValue<string>());
    }

    public McpClient Mcp(string accessToken)
    {
        var c = Anonymous();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return new McpClient(c);
    }
}

/// <summary>The flow tests register many clients from the one test address: lift the per-address cap (McpOAuthLimitsTests tests it).</summary>
public class McpOAuthTestApp : TestApp
{
    protected override void AddSettings(IDictionary<string, string?> s) => s["Mcp:OAuth:MaxRegistrationsPerIpPerHour"] = "100000";
}

public class McpOAuthTests(McpOAuthTestApp app) : IClassFixture<McpOAuthTestApp>
{
    private async Task<(User User, HttpClient Browser)> NewUserAsync()
    {
        var user = await app.CreateUserAsync("oa" + Guid.NewGuid().ToString("N")[..8]);
        return (user, await app.CookieClientAsync(user.UserName!));
    }

    private static async Task AssertOAuthError(HttpResponseMessage r, string error, HttpStatusCode status = HttpStatusCode.BadRequest)
    {
        Assert.Equal(status, r.StatusCode);
        Assert.Equal(error, (await TestOAuthClient.JsonAsync(r))["error"]!.GetValue<string>());
        Assert.Contains("no-store", r.Headers.CacheControl!.ToString());
    }

    private static async Task<HttpStatusCode> McpStatusAsync(TestOAuthClient c, string access) => (await c.Mcp(access).RequestAsync("ping")).Status;

    // ------------------------------------------------------------------ discovery and the happy path

    [Fact]
    public async Task Discovery_metadata_and_the_401_challenge_point_clients_at_the_authorization_server()
    {
        var anon = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        foreach (var path in new[] { "/.well-known/oauth-protected-resource", "/.well-known/oauth-protected-resource/mcp" })
        {
            var r = await anon.GetAsync(path);
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            Assert.Equal("*", r.Headers.GetValues("Access-Control-Allow-Origin").Single());
            var prm = await TestOAuthClient.JsonAsync(r);
            Assert.Equal("http://localhost/mcp", prm["resource"]!.GetValue<string>());
            Assert.Equal("http://localhost", prm["authorization_servers"]![0]!.GetValue<string>());
        }
        var asm = await TestOAuthClient.JsonAsync(await anon.GetAsync("/.well-known/oauth-authorization-server"));
        Assert.Equal("http://localhost", asm["issuer"]!.GetValue<string>());
        Assert.Equal("http://localhost/oauth/token", asm["token_endpoint"]!.GetValue<string>());
        Assert.Equal("http://localhost/oauth/register", asm["registration_endpoint"]!.GetValue<string>());
        Assert.Equal(new[] { "S256" }, asm["code_challenge_methods_supported"]!.AsArray().Select(x => x!.GetValue<string>()));
        Assert.Equal(new[] { "none" }, asm["token_endpoint_auth_methods_supported"]!.AsArray().Select(x => x!.GetValue<string>()));

        var unauthorized = await anon.PostAsync("/mcp", new StringContent("{}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
        Assert.Contains("resource_metadata=\"http://localhost/.well-known/oauth-protected-resource/mcp\"", unauthorized.Headers.WwwAuthenticate.ToString());

        // CORS preflight on token; never on authorize.
        var preflight = await anon.SendAsync(new HttpRequestMessage(HttpMethod.Options, "/oauth/token"));
        Assert.Equal("*", preflight.Headers.GetValues("Access-Control-Allow-Origin").Single());
        var authorize = await anon.GetAsync("/oauth/authorize");
        Assert.False(authorize.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task Sign_in_flow_issues_tokens_that_work_on_mcp_as_the_user_with_the_granted_scope()
    {
        var (user, browser) = await NewUserAsync();
        var c = await new TestOAuthClient(app).RegisterAsync("Claude Code");

        // Not signed in: the authorize page sends the browser to the login page and back.
        var url = c.AuthorizeUrl();
        var toLogin = await c.Anonymous().GetAsync(url);
        Assert.Equal(HttpStatusCode.Redirect, toLogin.StatusCode);
        var login = toLogin.Headers.Location!.ToString();
        Assert.StartsWith("/account/login?returnUrl=", login);
        Assert.Equal(url, Uri.UnescapeDataString(login["/account/login?returnUrl=".Length..]));

        // The consent page names the client, where it returns to and what it may do; it never approves by itself.
        var page = await browser.GetAsync(c.AuthorizeUrl());
        var html = await page.Content.ReadAsStringAsync();
        Assert.Contains("Claude Code", html);
        Assert.Contains("127.0.0.1:53682", html);
        Assert.Contains("Read your projects", html);
        Assert.DoesNotContain("Create and change", html);
        Assert.Contains("form-action 'self' http://127.0.0.1:53682", page.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Equal("DENY", page.Headers.GetValues("X-Frame-Options").Single());
        using (var db = app.Db()) Assert.False(db.OAuthCodes.Any(x => x.UserId == user.Id));

        var (access, refresh) = await c.SignInAsync(browser);
        Assert.StartsWith("bbo_", access);
        Assert.StartsWith("bbr_", refresh);
        using (var db = app.Db())
        {
            // Only hashes are stored.
            Assert.True(db.OAuthTokens.Any(t => t.TokenHash == McpOAuthService.Hash(access)));
            Assert.DoesNotContain(db.OAuthTokens.ToList(), t => Encoding.UTF8.GetString(t.TokenHash).Contains(access[4..10]));
        }

        var mcp = c.Mcp(access);
        var whoami = await mcp.CallAsync("my_work");
        Assert.False(McpClient.IsError(whoami), McpClient.Text(whoami));
        var tools = (await mcp.ResultAsync("tools/list"))["tools"]!.AsArray().Select(t => t!["name"]!.GetValue<string>()).ToList();
        Assert.Contains("list_projects", tools);
        Assert.DoesNotContain("create_issue", tools); // read only
        var write = await mcp.CallAsync("create_issue", new { project = "WEB", title = "x" });
        Assert.True(McpClient.IsError(write));
        Assert.Contains("write_api", McpClient.Text(write));

        using (var db = app.Db())
        {
            var actions = await db.AuditEvents.Where(e => e.ActorId == user.Id || e.TargetId == c.ClientId).Select(e => e.Action).ToListAsync();
            Assert.Contains("oauth.consent_granted", actions);
            Assert.Contains("oauth.token_issued", actions);
            Assert.Contains("oauth.client_registered", actions);
            var call = await db.AuditEvents.Where(e => e.Action == "mcp.tool_called" && e.ActorId == user.Id).FirstAsync();
            Assert.Contains("oauthGrantId", call.Details);
        }
    }

    [Fact]
    public async Task Write_scope_needs_consent_and_a_refresh_cannot_escalate()
    {
        var (_, browser) = await NewUserAsync();
        var c = await new TestOAuthClient(app).RegisterAsync();
        var page = await (await browser.GetAsync(c.AuthorizeUrl(new() { ["scope"] = "read write" }))).Content.ReadAsStringAsync();
        Assert.Contains("Create and change", page);

        var (writeAccess, _) = await c.SignInAsync(browser, "read write");
        var tools = (await c.Mcp(writeAccess).ResultAsync("tools/list"))["tools"]!.AsArray().Select(t => t!["name"]!.GetValue<string>()).ToList();
        Assert.Contains("create_issue", tools);

        // A read grant can't be refreshed into write.
        var (_, readRefresh) = await c.SignInAsync(browser, "read");
        await AssertOAuthError(await c.RefreshAsync(readRefresh, "read write"), "invalid_scope");
        // Unknown scopes are refused at the authorization request.
        var bad = await browser.GetAsync(c.AuthorizeUrl(new() { ["scope"] = "admin" }));
        Assert.Equal("invalid_scope", TestOAuthClient.Query(bad)["error"]);
    }

    // ------------------------------------------------------------------ PKCE, codes, redirect URIs, state

    [Fact]
    public async Task Pkce_is_mandatory_S256_only_and_the_verifier_must_match()
    {
        var (_, browser) = await NewUserAsync();
        var c = await new TestOAuthClient(app).RegisterAsync();

        var missing = TestOAuthClient.Query(await browser.GetAsync(c.AuthorizeUrl(new() { ["code_challenge"] = null, ["code_challenge_method"] = null })));
        Assert.Equal("invalid_request", missing["error"]);
        Assert.StartsWith("st-", missing["state"]);
        var plain = TestOAuthClient.Query(await browser.GetAsync(c.AuthorizeUrl(new() { ["code_challenge_method"] = "plain" })));
        Assert.Equal("invalid_request", plain["error"]);
        var noMethod = TestOAuthClient.Query(await browser.GetAsync(c.AuthorizeUrl(new() { ["code_challenge_method"] = null })));
        Assert.Equal("invalid_request", noMethod["error"]);

        var code = await c.CodeAsync(browser);
        await AssertOAuthError(await c.ExchangeAsync(code, verifier: TestOAuthClient.NewVerifier()), "invalid_grant");
        var noVerifier = await c.TokenAsync(new() { ["grant_type"] = "authorization_code", ["code"] = code, ["client_id"] = c.ClientId, ["redirect_uri"] = c.RedirectUri });
        await AssertOAuthError(noVerifier, "invalid_request");
        // The plain "challenge = verifier" trick doesn't work either.
        var code2 = await c.CodeAsync(browser);
        await AssertOAuthError(await c.ExchangeAsync(code2, verifier: c.Challenge), "invalid_grant");
    }

    [Fact]
    public async Task A_code_is_single_use_and_reusing_it_revokes_what_it_was_exchanged_for()
    {
        var (_, browser) = await NewUserAsync();
        var c = await new TestOAuthClient(app).RegisterAsync();
        var code = await c.CodeAsync(browser);
        var first = await c.ExchangeAsync(code);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var access = (await TestOAuthClient.JsonAsync(first))["access_token"]!.GetValue<string>();
        Assert.Equal(HttpStatusCode.OK, await McpStatusAsync(c, access));

        await AssertOAuthError(await c.ExchangeAsync(code), "invalid_grant");
        Assert.Equal(HttpStatusCode.Unauthorized, await McpStatusAsync(c, access));
        using var db = app.Db();
        Assert.Contains(db.OAuthGrants.ToList(), g => g.RevokedReason == "code_reuse");
    }

    [Fact]
    public async Task Expired_codes_wrong_client_and_wrong_redirect_uri_are_invalid_grant()
    {
        var (_, browser) = await NewUserAsync();
        var c = await new TestOAuthClient(app).RegisterAsync(extraRedirects: "http://127.0.0.1:53682/other");

        var code = await c.CodeAsync(browser);
        using (var db = app.Db())
        {
            var hash = McpOAuthService.Hash(code);
            await db.OAuthCodes.Where(x => x.CodeHash == hash).ExecuteUpdateAsync(s => s.SetProperty(x => x.ExpiresAt, DateTime.UtcNow.AddSeconds(-1)));
        }
        await AssertOAuthError(await c.ExchangeAsync(code), "invalid_grant");

        // A code issued for one registered redirect URI can't be redeemed with another.
        code = await c.CodeAsync(browser);
        await AssertOAuthError(await c.ExchangeAsync(code, redirect: "http://127.0.0.1:53682/other"), "invalid_grant");

        // Another client can't redeem it.
        var other = await new TestOAuthClient(app).RegisterAsync();
        code = await c.CodeAsync(browser);
        var stolen = await c.TokenAsync(new()
        {
            ["grant_type"] = "authorization_code", ["code"] = code, ["client_id"] = other.ClientId, ["redirect_uri"] = c.RedirectUri, ["code_verifier"] = c.Verifier,
        });
        await AssertOAuthError(stolen, "invalid_grant");
        await AssertOAuthError(await c.TokenAsync(new() { ["grant_type"] = "password", ["username"] = "x" }), "unsupported_grant_type");
        await AssertOAuthError(await c.TokenAsync(new() { ["grant_type"] = "authorization_code", ["code"] = "x", ["client_id"] = "bbc_unknown", ["redirect_uri"] = c.RedirectUri, ["code_verifier"] = c.Verifier }),
            "invalid_client", HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData("https://evil.example/callback")]
    [InlineData("http://127.0.0.1:53682/callback/")]
    [InlineData("http://127.0.0.1:53683/callback")]
    [InlineData("http://127.0.0.1:53682/CALLBACK")]
    [InlineData("http://127.0.0.1:53682/callback?x=1")]
    [InlineData("//evil.example/callback")]
    public async Task Unregistered_redirect_uris_are_never_redirected_to(string redirect)
    {
        var (_, browser) = await NewUserAsync();
        var c = await new TestOAuthClient(app).RegisterAsync();
        var r = await browser.GetAsync(c.AuthorizeUrl(new() { ["redirect_uri"] = redirect }));
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Null(r.Headers.Location);
        Assert.Contains("isn't one the application registered", WebUtility.HtmlDecode(await r.Content.ReadAsStringAsync()));
        // Unknown client: also an error page, never a redirect.
        var unknown = await browser.GetAsync(c.AuthorizeUrl(new() { ["client_id"] = "bbc_nope" }));
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        Assert.Null(unknown.Headers.Location);
    }

    [Theory]
    [InlineData("http://evil.example/callback")]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html,hi")]
    [InlineData("https://ok.example/cb#frag")]
    [InlineData("https://user:pw@ok.example/cb")]
    [InlineData("relative/path")]
    [InlineData("file:///etc/passwd")]
    public async Task Registration_refuses_unsafe_redirect_uris(string redirect)
    {
        var r = await app.CreateClient().PostAsync("/oauth/register", TestOAuthClient.JsonBody(new { client_name = "x", redirect_uris = new[] { redirect } }));
        await AssertOAuthError(r, "invalid_redirect_uri");
    }

    [Fact]
    public async Task Registration_accepts_https_loopback_and_native_schemes_and_refuses_confidential_clients()
    {
        var anon = app.CreateClient();
        foreach (var uri in new[] { "https://claude.ai/api/mcp/auth_callback", "http://localhost:3000/cb", "http://[::1]:8080/cb", "cursor://anysphere.cursor-mcp/oauth/callback" })
            Assert.Equal(HttpStatusCode.Created, (await anon.PostAsync("/oauth/register", TestOAuthClient.JsonBody(new { client_name = "ok", redirect_uris = new[] { uri } }))).StatusCode);
        await AssertOAuthError(await anon.PostAsync("/oauth/register", TestOAuthClient.JsonBody(new { redirect_uris = new[] { "https://a.example/cb" }, token_endpoint_auth_method = "client_secret_basic" })), "invalid_client_metadata");
        await AssertOAuthError(await anon.PostAsync("/oauth/register", TestOAuthClient.JsonBody(new { redirect_uris = new[] { "https://a.example/cb" }, grant_types = new[] { "client_credentials" } })), "invalid_client_metadata");
        await AssertOAuthError(await anon.PostAsync("/oauth/register", TestOAuthClient.JsonBody(new { client_name = "no uris" })), "invalid_redirect_uri");
        await AssertOAuthError(await anon.PostAsync("/oauth/register", new StringContent("not json", Encoding.UTF8, "application/json")), "invalid_client_metadata");
    }

    [Fact]
    public async Task A_missing_state_is_an_error_page_and_deny_returns_access_denied()
    {
        var (user, browser) = await NewUserAsync();
        var c = await new TestOAuthClient(app).RegisterAsync();
        var noState = await browser.GetAsync(c.AuthorizeUrl(new() { ["state"] = null }));
        Assert.Equal(HttpStatusCode.BadRequest, noState.StatusCode);
        Assert.Null(noState.Headers.Location);

        var denied = await c.ConsentAsync(browser, "deny");
        var q = TestOAuthClient.Query(denied);
        Assert.Equal("access_denied", q["error"]);
        Assert.False(q.ContainsKey("code"));
        using var db = app.Db();
        Assert.True(db.AuditEvents.Any(e => e.Action == "oauth.consent_denied" && e.ActorId == user.Id));
    }

    [Fact]
    public async Task Client_names_are_escaped_on_the_consent_page()
    {
        var (_, browser) = await NewUserAsync();
        var c = await new TestOAuthClient(app).RegisterAsync("<script>alert('x')</script><b>Evil</b>");
        var html = await (await browser.GetAsync(c.AuthorizeUrl())).Content.ReadAsStringAsync();
        Assert.DoesNotContain("<script>alert", html);
        Assert.DoesNotContain("<b>Evil</b>", html);
        Assert.Contains("&lt;script&gt;", html);
    }

    // ------------------------------------------------------------------ consent CSRF

    [Fact]
    public async Task Consent_needs_the_antiforgery_token_of_the_signed_in_session()
    {
        var (_, browser) = await NewUserAsync();
        var (_, other) = await NewUserAsync();
        var c = await new TestOAuthClient(app).RegisterAsync();
        var html = await (await browser.GetAsync(c.AuthorizeUrl())).Content.ReadAsStringAsync();
        var fields = TestOAuthClient.HiddenFields(html);
        fields["decision"] = "approve";

        // A cross-site form can't know the token.
        var withoutToken = fields.Where(f => !f.Key.StartsWith("__RequestVerificationToken")).ToDictionary();
        var r = await browser.PostAsync("/oauth/authorize", new FormUrlEncodedContent(withoutToken));
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Null(r.Headers.Location);

        // The token of one session doesn't approve in another user's session.
        r = await other.PostAsync("/oauth/authorize", new FormUrlEncodedContent(fields));
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Null(r.Headers.Location);

        // Not signed in at all.
        r = await c.Anonymous().PostAsync("/oauth/authorize", new FormUrlEncodedContent(fields));
        Assert.Null(r.Headers.Location);
        Assert.NotEqual(HttpStatusCode.OK, r.StatusCode);

        // A tampered redirect_uri in the posted form is validated again.
        var tampered = new Dictionary<string, string>(fields) { ["redirect_uri"] = "https://evil.example/cb" };
        r = await browser.PostAsync("/oauth/authorize", new FormUrlEncodedContent(tampered));
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Null(r.Headers.Location);

        // The genuine form works.
        r = await browser.PostAsync("/oauth/authorize", new FormUrlEncodedContent(fields));
        Assert.True(TestOAuthClient.Query(r).ContainsKey("code"));
    }

    // ------------------------------------------------------------------ resource indicators

    [Fact]
    public async Task The_resource_parameter_must_name_this_servers_mcp_endpoint()
    {
        var (_, browser) = await NewUserAsync();
        var c = await new TestOAuthClient(app).RegisterAsync();
        var bad = TestOAuthClient.Query(await browser.GetAsync(c.AuthorizeUrl(new() { ["resource"] = "https://other.example/mcp" })));
        Assert.Equal("invalid_target", bad["error"]);

        var code = await c.CodeAsync(browser);
        await AssertOAuthError(await c.ExchangeAsync(code, resource: "http://localhost/api/v1"), "invalid_target");
        // Without a resource, or with the right one, it works.
        var ok = await c.ExchangeAsync(await c.CodeAsync(browser, new() { ["resource"] = null }), resource: null);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
    }

    // ------------------------------------------------------------------ refresh rotation and reuse

    [Fact]
    public async Task Refresh_tokens_rotate_and_reusing_one_revokes_the_whole_grant()
    {
        var (user, browser) = await NewUserAsync();
        var c = await new TestOAuthClient(app).RegisterAsync();
        var (access1, refresh1) = await c.SignInAsync(browser);

        var r = await c.RefreshAsync(refresh1);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var json = await TestOAuthClient.JsonAsync(r);
        var access2 = json["access_token"]!.GetValue<string>();
        var refresh2 = json["refresh_token"]!.GetValue<string>();
        Assert.NotEqual(refresh1, refresh2);
        Assert.Equal(HttpStatusCode.OK, await McpStatusAsync(c, access2));

        // The old refresh token again: someone copied it. Everything of the grant dies.
        await AssertOAuthError(await c.RefreshAsync(refresh1), "invalid_grant");
        Assert.Equal(HttpStatusCode.Unauthorized, await McpStatusAsync(c, access1));
        Assert.Equal(HttpStatusCode.Unauthorized, await McpStatusAsync(c, access2));
        await AssertOAuthError(await c.RefreshAsync(refresh2), "invalid_grant");
        using var db = app.Db();
        var actions = db.AuditEvents.Where(e => e.TargetType == "oauth_grant").Select(e => e.Action).ToList();
        Assert.Contains("oauth.token_refreshed", actions);
        Assert.Contains("oauth.refresh_reuse_detected", actions);
        Assert.Contains("oauth.grant_revoked", actions);
        Assert.Contains(db.OAuthGrants.Where(g => g.UserId == user.Id).ToList(), g => g.RevokedReason == "refresh_reuse");
    }

    // ------------------------------------------------------------------ where tokens work, and when they stop

    [Fact]
    public async Task OAuth_tokens_are_refused_by_the_rest_api_and_git()
    {
        var (_, browser) = await NewUserAsync();
        var c = await new TestOAuthClient(app).RegisterAsync();
        var (access, refresh) = await c.SignInAsync(browser, "read write");
        foreach (var token in new[] { access, refresh })
        {
            var api = c.Anonymous();
            api.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            Assert.Equal(HttpStatusCode.Unauthorized, (await api.GetAsync("/api/v1/user")).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await api.GetAsync("/api/v1/projects")).StatusCode);
        }
        // The refresh token isn't an access token at /mcp either.
        Assert.Equal(HttpStatusCode.Unauthorized, await McpStatusAsync(c, refresh));
    }

    [Fact]
    public async Task Tokens_die_when_the_grant_is_revoked_or_the_user_is_deactivated_or_locked()
    {
        // Revoked from the profile (the service the Connected apps list uses).
        var (u1, b1) = await NewUserAsync();
        var c = await new TestOAuthClient(app).RegisterAsync("Revoke me");
        var (a1, r1) = await c.SignInAsync(b1);
        await using (var s = app.Scope())
        {
            var oauth = s.Get<McpOAuthService>();
            var apps = await oauth.ListConnectedAsync(u1.Id);
            Assert.Equal("Revoke me", Assert.Single(apps).ClientName);
            Assert.False(await oauth.RevokeGrantAsync(app.UserId("alice"), apps[0].GrantId)); // not alice's grant
            Assert.True(await oauth.RevokeGrantAsync(u1.Id, apps[0].GrantId));
            Assert.Empty(await oauth.ListConnectedAsync(u1.Id));
        }
        Assert.Equal(HttpStatusCode.Unauthorized, await McpStatusAsync(c, a1));
        await AssertOAuthError(await c.RefreshAsync(r1), "invalid_grant");

        // Revoked by the client (RFC 7009): a refresh token takes the grant with it.
        var (_, b2) = await NewUserAsync();
        var (a2, r2) = await c.SignInAsync(b2);
        var revoke = await c.Anonymous().PostAsync("/oauth/revoke", new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = r2, ["client_id"] = c.ClientId }));
        Assert.Equal(HttpStatusCode.OK, revoke.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, await McpStatusAsync(c, a2));
        // Unknown tokens: still 200.
        Assert.Equal(HttpStatusCode.OK, (await c.Anonymous().PostAsync("/oauth/revoke", new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = "nope", ["client_id"] = c.ClientId }))).StatusCode);

        // Deactivated user.
        var (u3, b3) = await NewUserAsync();
        var (a3, r3) = await c.SignInAsync(b3);
        Assert.Equal(HttpStatusCode.OK, await McpStatusAsync(c, a3));
        await using (var s = app.Scope())
        {
            var users = s.Get<UserManager<User>>();
            await using var db = app.Db();
            await AccountDeactivation.DeactivateAsync(users, db, (await users.FindByIdAsync(u3.Id.ToString()))!);
        }
        Assert.Equal(HttpStatusCode.Unauthorized, await McpStatusAsync(c, a3));
        await AssertOAuthError(await c.RefreshAsync(r3), "invalid_grant");

        // Locked user.
        var (u4, b4) = await NewUserAsync();
        var (a4, _) = await c.SignInAsync(b4);
        await using (var s = app.Scope())
        {
            var users = s.Get<UserManager<User>>();
            await users.SetLockoutEndDateAsync((await users.FindByIdAsync(u4.Id.ToString()))!, DateTimeOffset.UtcNow.AddHours(1));
        }
        Assert.Equal(HttpStatusCode.Unauthorized, await McpStatusAsync(c, a4));

        // Expired access token.
        var (_, b5) = await NewUserAsync();
        var (a5, _) = await c.SignInAsync(b5);
        using (var db = app.Db())
        {
            var hash = McpOAuthService.Hash(a5);
            await db.OAuthTokens.Where(t => t.TokenHash == hash).ExecuteUpdateAsync(x => x.SetProperty(t => t.ExpiresAt, DateTime.UtcNow.AddSeconds(-1)));
        }
        Assert.Equal(HttpStatusCode.Unauthorized, await McpStatusAsync(c, a5));
    }

    [Fact]
    public async Task The_profile_lists_connected_apps_with_a_revoke_button()
    {
        var (_, browser) = await NewUserAsync();
        var c = await new TestOAuthClient(app).RegisterAsync("Listed <App>");
        var (access, _) = await c.SignInAsync(browser);
        var html = await browser.GetStringAsync("/account/profile");
        Assert.Contains("Connected apps", html);
        Assert.Contains("Listed &lt;App&gt;", html);
        var grantId = Regex.Match(html, "name=\"RevokeGrantId\" value=\"(\\d+)\"").Groups[1].Value;
        var token = Regex.Matches(html, "name=\"__RequestVerificationToken\" value=\"([^\"]+)\"")[0].Groups[1].Value;
        var r = await browser.PostAsync("/account/profile", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["_handler"] = "revoke-oauth-grant", ["__RequestVerificationToken"] = token, ["RevokeGrantId"] = grantId,
        }));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Contains("was disconnected", WebUtility.HtmlDecode(await r.Content.ReadAsStringAsync()));
        Assert.Equal(HttpStatusCode.Unauthorized, await McpStatusAsync(c, access));
    }
}

/// <summary>Registration limits, in their own fixture.</summary>
public class McpOAuthLimitsApp : TestApp
{
    protected override void AddSettings(IDictionary<string, string?> s)
    {
        s["Mcp:OAuth:MaxRegistrationsPerIpPerHour"] = "2";
        s["Mcp:OAuth:MaxClients"] = "4";
    }
}

public class McpOAuthLimitsTests(McpOAuthLimitsApp app) : IClassFixture<McpOAuthLimitsApp>
{
    [Fact]
    public async Task Registrations_are_capped_per_address_and_in_total()
    {
        var anon = app.CreateClient();
        StringContent Body() => TestOAuthClient.JsonBody(new { client_name = "c", redirect_uris = new[] { "http://127.0.0.1:1/cb" } });
        Assert.Equal(HttpStatusCode.Created, (await anon.PostAsync("/oauth/register", Body())).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await anon.PostAsync("/oauth/register", Body())).StatusCode);
        var limited = await anon.PostAsync("/oauth/register", Body());
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.Equal("too_many_requests", (await TestOAuthClient.JsonAsync(limited))["error"]!.GetValue<string>());

        // From other addresses: two more fit under MaxClients = 4, then the total cap applies.
        await using var s = app.Scope();
        var oauth = s.Get<McpOAuthService>();
        var body = JsonNode.Parse("""{"client_name":"c","redirect_uris":["https://a.example/cb"]}""")!.AsObject();
        await oauth.RegisterAsync(body, "10.0.0.1");
        await oauth.RegisterAsync(body, "10.0.0.2");
        var full = await Assert.ThrowsAsync<OAuthError>(() => oauth.RegisterAsync(body, "10.0.0.3"));
        Assert.Equal(403, full.Status);
    }
}

/// <summary>Mcp:OAuth:Enabled = false: no OAuth endpoints, and /mcp takes only personal access tokens.</summary>
public class McpOAuthOffApp : TestApp
{
    protected override void AddSettings(IDictionary<string, string?> s) => s["Mcp:OAuth:Enabled"] = "false";
}

public class McpOAuthOffTests(McpOAuthOffApp app) : IClassFixture<McpOAuthOffApp>
{
    [Fact]
    public async Task Switched_off_there_is_no_metadata_registration_or_resource_metadata_challenge()
    {
        var anon = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        Assert.Equal(HttpStatusCode.NotFound, (await anon.GetAsync("/.well-known/oauth-authorization-server")).StatusCode);
        Assert.NotEqual(HttpStatusCode.Created, (await anon.PostAsync("/oauth/register", TestOAuthClient.JsonBody(new { redirect_uris = new[] { "http://127.0.0.1:1/cb" } }))).StatusCode);
        var r = await anon.PostAsync("/mcp", new StringContent("{}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
        Assert.DoesNotContain("resource_metadata", r.Headers.WwwAuthenticate.ToString());
    }
}
