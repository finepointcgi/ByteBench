using System.Net;
using System.Text.RegularExpressions;
using GitBench.Data;
using GitBench.Services;
using GitBench.Tests.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;

namespace GitBench.Tests.Sso;

/// <summary>
/// The app with SSO providers pointing at a <see cref="MockOidcProvider"/> on a real port:
/// <c>mock</c> (OIDC: auto-create, admin group <c>gb-admins</c>, groups <c>engineering</c>/<c>leads</c> → <c>sso-team</c>, trusts the IdP's MFA),
/// <c>corp</c> (OIDC: only <c>corp.example</c> emails), <c>invite</c> (OIDC: no auto-create) and <c>gh</c> (GitHub OAuth, Enterprise-style URLs).
/// </summary>
public class SsoTestApp : TestApp
{
    public const string TeamGroup = "sso-team";
    public MockOidcProvider Idp { get; } = new();

    public SsoTestApp() => Idp.StartAsync().GetAwaiter().GetResult();

    protected override void AddSettings(IDictionary<string, string?> s)
    {
        void Provider(int i, string name, string type, params (string Key, string Value)[] extra)
        {
            var p = $"Authentication:Oidc:Providers:{i}:";
            s[p + "Name"] = name;
            s[p + "Type"] = type;
            s[p + "Authority"] = Idp.BaseUrl;
            s[p + "ClientId"] = "gitbench-" + name;
            s[p + "ClientSecret"] = MockOidcProvider.ClientSecret;
            s[p + "RequireHttpsMetadata"] = "false";
            foreach (var (k, v) in extra) s[p + k] = v;
        }
        Provider(0, "mock", "Oidc", ("DisplayName", "Mock IdP"), ("AdminGroups:0", "gb-admins"),
            ("GroupMappings:0:ExternalGroup", "engineering"), ("GroupMappings:0:Group", TeamGroup), ("GroupMappings:0:Role", "Developer"),
            ("GroupMappings:1:ExternalGroup", "leads"), ("GroupMappings:1:Group", TeamGroup), ("GroupMappings:1:Role", "Maintainer"),
            ("TwoFactor", "TrustIdp"));
        Provider(1, "corp", "Oidc", ("DisplayName", "Corp"), ("AllowedEmailDomains:0", "corp.example"));
        Provider(2, "invite", "Oidc", ("AutoCreateUsers", "false"));
        Provider(3, "gh", "GitHub", ("ApiUrl", Idp.BaseUrl + "/api/v3"), ("GroupMappings:0:ExternalGroup", "acme/devs"),
            ("GroupMappings:0:Group", TeamGroup), ("GroupMappings:0:Role", "Developer"));
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) Idp.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    public HttpClient Browser() => CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });

    /// <summary>
    /// Runs the browser side of an SSO round trip: <paramref name="start"/> (e.g. <c>/account/sso/mock</c>) → the IdP's
    /// authorize endpoint (signs in <paramref name="user"/> at once) → the app's callback → the completion endpoint.
    /// Returns where the app finally redirects.
    /// </summary>
    public async Task<string> SsoAsync(HttpClient browser, string start, MockUser user)
    {
        Idp.NextUser = user;
        var toIdp = await browser.GetAsync(start);
        Assert.Equal(HttpStatusCode.Redirect, toIdp.StatusCode);
        var authorize = toIdp.Headers.Location!;
        Assert.StartsWith(Idp.BaseUrl, authorize.ToString());

        using var plain = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        var back = await plain.GetAsync(authorize);
        Assert.Equal(HttpStatusCode.Redirect, back.StatusCode);
        var callback = back.Headers.Location!;

        var afterCallback = await browser.GetAsync(callback.PathAndQuery);
        Assert.Equal(HttpStatusCode.Redirect, afterCallback.StatusCode);
        var next = afterCallback.Headers.Location!.ToString();
        if (!next.StartsWith("/account/sso/complete", StringComparison.Ordinal)) return next;
        var done = await browser.GetAsync(next);
        Assert.Equal(HttpStatusCode.Redirect, done.StatusCode);
        return done.Headers.Location!.ToString();
    }

    public async Task<GitBench.Data.Group> EnsureTeamGroupAsync()
    {
        using (var db = Db())
        {
            if (db.Groups.FirstOrDefault(g => g.FullPath == TeamGroup) is { } existing) return existing;
        }
        await using var s = Scope(UserId("alice"));
        return await s.Get<GroupService>().CreateAsync("SSO team", TeamGroup, null, ProjectVisibility.Private);
    }

    public static string Token(string html) => Regex.Match(html, "name=\"__RequestVerificationToken\" value=\"([^\"]+)\"").Groups[1].Value;
}
