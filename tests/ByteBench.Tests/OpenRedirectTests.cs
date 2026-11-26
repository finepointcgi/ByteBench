using System.Net;
using System.Text.RegularExpressions;
using ByteBench.Data;
using ByteBench.Services.Saml;
using ByteBench.Services.Sso;
using ByteBench.Tests.Infrastructure;
using ByteBench.Tests.Sso;
using Microsoft.AspNetCore.Mvc.Testing;

namespace ByteBench.Tests;

/// <summary>
/// Return URLs that look local to a "starts with / but not // or /\" check but leave the site in a browser: the URL parser
/// drops tab, CR and LF anywhere in a URL (WHATWG URL standard), so <c>/&lt;TAB&gt;/evil.example</c> is <c>//evil.example</c>.
/// </summary>
public static class OpenRedirectCases
{
    public static readonly TheoryData<string> Tricks = new()
    {
        "/\t/evil.example/phish",
        "/\n/evil.example/phish",
        "/\r\n/evil.example/phish",
        "/\t\\evil.example/phish",
        "/\t/\tevil.example",
        "/\\evil.example",
        "/a\\..\\..\\evil.example",
        "//evil.example",
        "/%2F/evil.example",
        "/%2f/evil.example",
        "/%5Cevil.example",
        "/%09/evil.example",
        "/\u2028/evil.example",
    };

    public static readonly TheoryData<string?> Rejected = new()
    {
        null, "", "evil.example", "http://evil.example", "https://evil.example/x", "javascript:alert(1)", "\\evil.example",
        " /evil.example", "\t/evil.example", "/\0/evil.example", "/\u007f", "/ok\r\nSet-Cookie: x=1",
        "/\t/evil.example/phish", "/\n/evil.example", "/\r/evil.example", "/\\evil.example", "//evil.example", "///evil.example",
        "/%2F/evil.example", "/%5c/evil.example", "/x\\evil.example",
    };

    /// <summary>What a browser does with a Location value: strips ASCII tab/newline and resolves it against the site.</summary>
    public static Uri BrowserResolve(string location) =>
        new(new Uri("http://localhost/"), Regex.Replace(location, "[\t\r\n]", "").Replace('\\', '/'));
}

public class PasswordLoginOpenRedirectTests(TestApp app) : IClassFixture<TestApp>
{
    [Theory]
    [MemberData(nameof(OpenRedirectCases.Tricks), MemberType = typeof(OpenRedirectCases))]
    public async Task Sign_in_never_redirects_off_site(string returnUrl)
    {
        var client = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });
        var url = "/account/login?returnUrl=" + Uri.EscapeDataString(returnUrl);
        var page = await client.GetStringAsync(url);
        var token = Regex.Match(page, "name=\"__RequestVerificationToken\" value=\"([^\"]+)\"").Groups[1].Value;
        var response = await client.PostAsync(url, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["_handler"] = "login",
            ["__RequestVerificationToken"] = token,
            ["Input.UserNameOrEmail"] = "carol",
            ["Input.Password"] = SeedData.DemoPassword,
        }));
        Assert.True((int)response.StatusCode is 302 or 303, $"sign-in failed: {(int)response.StatusCode}");
        var location = response.Headers.Location!.OriginalString;
        Assert.Equal("localhost", OpenRedirectCases.BrowserResolve(location).Host);
    }
}

public class SamlOpenRedirectTests(SamlTestApp app) : IClassFixture<SamlTestApp>
{
    private static string NewEmail() => $"r{Guid.NewGuid():N}"[..12] + "@example.org";

    [Theory]
    [MemberData(nameof(OpenRedirectCases.Tricks), MemberType = typeof(OpenRedirectCases))]
    public async Task Saml_sign_in_never_redirects_off_site(string returnUrl)
    {
        var location = await app.SignInAsync(app.Browser(), id => new SamlResponseSpec { InResponseTo = id, Email = NewEmail(), DisplayName = "Rita" }, returnUrl);
        Assert.Equal("localhost", OpenRedirectCases.BrowserResolve(location).Host);
    }

    [Theory]
    [MemberData(nameof(OpenRedirectCases.Tricks), MemberType = typeof(OpenRedirectCases))]
    public void Sso_return_url_guard_rejects_control_characters(string returnUrl) =>
        Assert.Equal("/", SsoService.SafeReturnUrl(returnUrl));
}


public class ReturnUrlGuardTests
{
    [Theory]
    [MemberData(nameof(OpenRedirectCases.Rejected), MemberType = typeof(OpenRedirectCases))]
    public void Open_redirect_payloads_are_not_local(string? url)
    {
        Assert.False(ByteBench.Components.Account.AccountUrls.IsLocal(url));
        Assert.Equal("/", ByteBench.Components.Account.AccountUrls.SafeReturnUrl(url));
        Assert.Equal("/", SsoService.SafeReturnUrl(url));
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/projects/alice/repo")]
    [InlineData("/projects/a/b/pulls/3?tab=files&x=%20y#c1")]
    [InlineData("/oauth/authorize?client_id=x&redirect_uri=https%3A%2F%2Fa.example%2Fcb")]
    public void Plain_local_paths_are_kept(string url)
    {
        Assert.True(ByteBench.Components.Account.AccountUrls.IsLocal(url));
        Assert.Equal(url, SsoService.SafeReturnUrl(url));
    }
}
