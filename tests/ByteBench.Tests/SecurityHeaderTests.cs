using System.Net;
using System.Text.RegularExpressions;
using ByteBench.Tests.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;

namespace ByteBench.Tests;

/// <summary>Response headers that every page, API response and static file carries (what a ZAP baseline scan looks at).</summary>
public class SecurityHeaderTests(TestApp app) : IClassFixture<TestApp>
{
    private HttpClient Client() => app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });

    [Theory]
    [InlineData("/account/login")]          // HTML page
    [InlineData("/app.css")]                // static file (ZAP: "Cross-Origin-Resource-Policy Header Missing or Invalid")
    [InlineData("/_content/MudBlazor/MudBlazor.min.css")] // static web asset from a package
    [InlineData("/api/v1/user")]            // API (a 401 here: error responses carry it too)
    [InlineData("/does-not-exist")]
    public async Task Responses_cannot_be_embedded_by_another_site(string url)
    {
        using var response = await Client().GetAsync(url);
        Assert.Equal("same-origin", response.Headers.GetValues("Cross-Origin-Resource-Policy").Single());
        Assert.Equal("same-origin", response.Headers.GetValues("Cross-Origin-Opener-Policy").Single());
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
    }

    [Fact]
    public async Task Csp_needs_no_unsafe_hashes_because_the_app_has_no_inline_handlers()
    {
        using var response = await Client().GetAsync("/account/login");
        var csp = response.Headers.GetValues("Content-Security-Policy").Single();
        Assert.DoesNotContain("unsafe-hashes", csp);
        Assert.DoesNotContain("unsafe-inline'", csp[csp.IndexOf("script-src", StringComparison.Ordinal)..csp.IndexOf("style-src", StringComparison.Ordinal)]);
        // The behaviour that replaced the inline handlers lives in a served file.
        var forms = await Client().GetStringAsync(Regex.Match(await response.Content.ReadAsStringAsync(), "src=\"(js/forms[^\"]*\\.js)\"").Groups[1].Value);
        Assert.Contains("data-copy-from", forms);
        Assert.Contains("data-confirm", forms);
    }

    [Fact]
    public async Task Cors_open_endpoints_keep_their_own_policy()
    {
        // The flag SDK is CORS-open on purpose (browser SDKs): it must not be narrowed by accident.
        using var response = await Client().SendAsync(new HttpRequestMessage(HttpMethod.Options, "/api/v1/flags/evaluate"));
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal("*", response.Headers.GetValues("Access-Control-Allow-Origin").Single());
        Assert.False(response.Headers.Contains("Cross-Origin-Resource-Policy"));
    }
}
