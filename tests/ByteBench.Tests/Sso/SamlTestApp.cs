using System.Net;
using System.Text;
using System.Web;
using ByteBench.Tests.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;

namespace ByteBench.Tests.Sso;

/// <summary>
/// The app with SAML (<c>Saml:*</c>) pointing at a <see cref="MockSamlIdp"/>: "Mock SAML", admin group <c>saml-admins</c>,
/// allowed domains <c>example.org</c> and <c>corp.example</c>, SP entity id <c>https://bytebench.test/sp</c>, ACS
/// <c>http://localhost/saml/acs</c> (the test server's address).
/// </summary>
public class SamlTestApp : TestApp
{
    public const string SpEntityId = "https://bytebench.test/sp";
    public const string AcsUrl = "http://localhost/saml/acs";
    public MockSamlIdp Idp { get; } = new();

    public SamlTestApp()
    {
        Idp.StartAsync().GetAwaiter().GetResult();
        Idp.AcsUrl = AcsUrl;
        Idp.SpEntityId = SpEntityId;
    }

    protected override void AddSettings(IDictionary<string, string?> s)
    {
        s["Saml:Enabled"] = "true";
        s["Saml:DisplayName"] = "Mock SAML";
        s["Saml:IdpEntityId"] = MockSamlIdp.EntityId;
        s["Saml:IdpSsoUrl"] = Idp.SsoUrl;
        s["Saml:IdpSloUrl"] = Idp.SloUrl;
        s["Saml:IdpCertificates:0"] = Idp.CertificatePem;
        s["Saml:SpEntityId"] = SpEntityId;
        s["Saml:BaseUrl"] = "http://localhost";
        s["Saml:AdminGroups:0"] = "saml-admins";
        s["Saml:AllowedEmailDomains:0"] = "example.org";
        s["Saml:AllowedEmailDomains:1"] = "corp.example";
        s["Saml:MaxResponseKilobytes"] = "64";
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) Idp.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    public HttpClient Browser() => CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });

    /// <summary>Starts SP-initiated sign-in in <paramref name="browser"/>. Returns the AuthnRequest id and the redirect URL to the IdP.</summary>
    public async Task<(string RequestId, Uri Redirect)> StartAsync(HttpClient browser, string? returnUrl = null)
    {
        var r = await browser.GetAsync("/saml/login" + (returnUrl == null ? "" : "?returnUrl=" + Uri.EscapeDataString(returnUrl)));
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        var location = r.Headers.Location!;
        Assert.StartsWith(Idp.SsoUrl + "?", location.ToString());
        var q = HttpUtility.ParseQueryString(location.Query);
        var request = MockSamlIdp.ParseRequest(q["SAMLRequest"]!);
        return (request.DocumentElement!.GetAttribute("ID"), location);
    }

    /// <summary>POSTs a response to the ACS like the IdP's auto-posting form. Returns where the app redirects.</summary>
    public async Task<string> PostAsync(HttpClient browser, string responseXml, string? relayState = null)
    {
        var form = new Dictionary<string, string> { ["SAMLResponse"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(responseXml)) };
        if (relayState != null) form["RelayState"] = relayState;
        var r = await browser.PostAsync("/saml/acs", new FormUrlEncodedContent(form));
        Assert.Equal(HttpStatusCode.SeeOther, r.StatusCode);
        return r.Headers.Location!.ToString();
    }

    /// <summary>A whole round trip: start, the IdP answers with <paramref name="spec"/> (given the request id), POST to the ACS.</summary>
    public async Task<string> SignInAsync(HttpClient browser, Func<string, SamlResponseSpec> spec, string? returnUrl = null)
    {
        var (id, _) = await StartAsync(browser, returnUrl);
        return await PostAsync(browser, Idp.Response(spec(id)));
    }
}
