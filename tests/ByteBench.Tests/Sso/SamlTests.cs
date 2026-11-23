using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Web;
using System.Xml;
using ByteBench.Data;
using ByteBench.Services.Saml;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ByteBench.Tests.Sso;

/// <summary>SAML 2.0 SP-initiated sign-in end to end against <see cref="MockSamlIdp"/>, and every validation rule's negative case.</summary>
public class SamlTests(SamlTestApp app) : IClassFixture<SamlTestApp>
{
    private const string Refused = "/account/login?sso_error=failed&provider=saml";
    private static string NewEmail(string domain = "example.org") => $"s{Guid.NewGuid():N}"[..12] + "@" + domain;

    private SamlResponseSpec Spec(string requestId, string? email = null) => new() { InResponseTo = requestId, Email = email ?? NewEmail(), DisplayName = "Sam Saml" };

    private async Task<User?> FindByEmailAsync(string email)
    {
        await using var s = app.Scope();
        return await s.Get<UserManager<User>>().FindByEmailAsync(email);
    }

    /// <summary>The reason code of the latest refused SAML sign-in.</summary>
    private string LastFailure()
    {
        using var db = app.Db();
        var e = db.AuditEvents.Where(e => e.Action == "auth.sso_failed" && e.Details!.Contains("\"provider\":\"saml\"")).OrderByDescending(e => e.Id).First();
        return System.Text.Json.JsonDocument.Parse(e.Details!).RootElement.GetProperty("reason").GetString()!;
    }

    private async Task AssertRefusedAsync(string expectedReason, Func<string, string> response, HttpClient? browser = null)
    {
        browser ??= app.Browser();
        var (id, _) = await app.StartAsync(browser);
        var location = await app.PostAsync(browser, response(id));
        Assert.Equal(Refused, location);
        Assert.Equal(expectedReason, LastFailure());
        // Not signed in.
        var profile = await browser.GetAsync("/account/profile");
        Assert.NotEqual(HttpStatusCode.OK, profile.StatusCode);
    }

    // ------------------------------------------------------------------ happy paths

    [Fact]
    public async Task First_sign_in_creates_the_account_maps_attributes_and_admin_group_and_signs_in()
    {
        var browser = app.Browser();
        var email = NewEmail();
        var (id, redirect) = await app.StartAsync(browser, "/explore");

        // The AuthnRequest: HTTP-Redirect, our ACS and entity id, the RelayState is the same-site return path.
        var q = HttpUtility.ParseQueryString(redirect.Query);
        Assert.Equal("/explore", q["RelayState"]);
        Assert.Null(q["Signature"]);
        var request = MockSamlIdp.ParseRequest(q["SAMLRequest"]!).DocumentElement!;
        Assert.Equal("AuthnRequest", request.LocalName);
        Assert.Equal(SamlTestApp.AcsUrl, request.GetAttribute("AssertionConsumerServiceURL"));
        Assert.Equal(app.Idp.SsoUrl, request.GetAttribute("Destination"));
        Assert.Equal(SamlTestApp.SpEntityId, request["saml:Issuer"]!.InnerText);

        var location = await app.PostAsync(browser, app.Idp.Response(Spec(id, email) with { UserName = "sam.saml", Groups = ["saml-admins", "other"] }), "/explore");
        Assert.Equal("/explore", location);

        var user = (await FindByEmailAsync(email))!;
        Assert.StartsWith("sam.saml", user.UserName);
        Assert.Equal("Sam Saml", user.DisplayName);
        Assert.True(user.EmailConfirmed);
        Assert.Null(user.PasswordHash);
        Assert.True(user.IsAdmin);
        Assert.Equal("saml", user.AdminGrantedBy);
        await using (var s = app.Scope())
        {
            var users = s.Get<UserManager<User>>();
            Assert.Contains(await users.GetLoginsAsync(user), l => l.LoginProvider == "saml");
        }
        using (var db = app.Db())
            Assert.Contains(db.AuditEvents.Where(e => e.Action == "auth.sso_sign_in" && e.TargetId == user.Id.ToString()).ToList(), e => e.Details!.Contains("\"provider\":\"saml\""));

        var profile = await browser.GetAsync("/account/profile");
        Assert.Equal(HttpStatusCode.OK, profile.StatusCode);
    }

    [Fact]
    public async Task Second_sign_in_reuses_the_link_and_an_existing_account_is_linked_by_email()
    {
        // A confirmed local account with an address in an allowed domain (corp.example).
        var local = await app.CreateUserAsync("samllink" + Guid.NewGuid().ToString("N")[..6]);
        await using (var s = app.Scope())
        {
            var users = s.Get<UserManager<User>>();
            var u = (await users.FindByIdAsync(local.Id.ToString()))!;
            await users.SetEmailAsync(u, u.UserName + "@corp.example");
            u.EmailConfirmed = true;
            await users.UpdateAsync(u);
        }
        var email = local.UserName + "@corp.example";
        var nameId = "persistent-" + Guid.NewGuid().ToString("N");
        Assert.Equal("/", await app.SignInAsync(app.Browser(), id => Spec(id, email) with { NameId = nameId }));
        Assert.Equal("/", await app.SignInAsync(app.Browser(), id => Spec(id, email) with { NameId = nameId }));
        using var db = app.Db();
        Assert.Equal(1, db.Users.Count(u => u.Email == email));
        Assert.Single(db.UserLogins.Where(l => l.UserId == local.Id && l.LoginProvider == "saml").ToList());
    }

    [Fact]
    public async Task Login_page_shows_the_saml_button_and_metadata_describes_the_sp()
    {
        var html = await app.CreateClient().GetStringAsync("/account/login");
        Assert.Contains("Sign in with Mock SAML", html);
        Assert.Contains("href=\"saml/login\"", html);

        var r = await app.CreateClient().GetAsync("/saml/metadata");
        Assert.Equal("application/samlmetadata+xml", r.Content.Headers.ContentType!.MediaType);
        var md = MockSamlIdp.Load(await r.Content.ReadAsStringAsync());
        Assert.Equal(SamlTestApp.SpEntityId, md.DocumentElement!.GetAttribute("entityID"));
        Assert.Contains(SamlTestApp.AcsUrl, md.OuterXml);
        Assert.Contains("WantAssertionsSigned=\"true\"", md.OuterXml);
    }

    [Fact]
    public async Task Clock_skew_tolerates_a_slightly_early_NotBefore()
    {
        Assert.Equal("/", await app.SignInAsync(app.Browser(), id => Spec(id) with { NotBefore = DateTime.UtcNow.AddSeconds(60) }));
    }

    // ------------------------------------------------------------------ signatures

    [Fact]
    public async Task Response_signed_with_another_certificate_is_refused() =>
        await AssertRefusedAsync("signature", id => app.Idp.Response(Spec(id) with { SigningCertificate = app.Idp.RogueCertificate }));

    [Fact]
    public async Task Unsigned_response_is_refused() =>
        await AssertRefusedAsync("unsigned", id => app.Idp.Response(Spec(id) with { SignResponse = false, SignAssertion = false }));

    [Fact]
    public async Task Signed_response_around_an_unsigned_assertion_is_refused_when_assertions_must_be_signed() =>
        await AssertRefusedAsync("assertion_unsigned", id => app.Idp.Response(Spec(id) with { SignAssertion = false }));

    [Fact]
    public async Task Assertion_changed_after_signing_is_refused() =>
        await AssertRefusedAsync("signature", id =>
        {
            var spec = Spec(id) with { SignResponse = false };
            var xml = app.Idp.Response(spec);
            return xml.Replace(">" + spec.NameId + "<", ">victim<");
        });

    [Fact]
    public async Task Sha1_signatures_are_refused() =>
        await AssertRefusedAsync("weak_algorithm", id => app.Idp.Response(Spec(id) with { Sha1 = true }));

    [Fact]
    public async Task Signature_wrapping_with_a_second_assertion_is_refused() =>
        await AssertRefusedAsync("assertion_count", id =>
        {
            // A validly signed response… plus an attacker's unsigned assertion for someone else.
            var signed = MockSamlIdp.Load(app.Idp.Response(Spec(id) with { SignResponse = false }));
            var evil = MockSamlIdp.Load(app.Idp.UnsignedXml(Spec(id, NewEmail()) with { AssertionId = "_evil", NameId = "admin" }));
            var evilAssertion = evil.GetElementsByTagName("Assertion", SamlXml.Assertion)[0]!;
            signed.DocumentElement!.InsertBefore(signed.ImportNode(evilAssertion, true), signed.GetElementsByTagName("Assertion", SamlXml.Assertion)[0]);
            return signed.OuterXml;
        });

    [Fact]
    public async Task Signature_wrapping_that_hides_the_signed_assertion_is_refused() =>
        await AssertRefusedAsync("assertion_count", id =>
        {
            // Move the signed assertion into Extensions and put a forged one where the SP reads.
            var signed = MockSamlIdp.Load(app.Idp.Response(Spec(id) with { SignResponse = false }));
            var original = (XmlElement)signed.GetElementsByTagName("Assertion", SamlXml.Assertion)[0]!;
            var forged = (XmlElement)original.CloneNode(true);
            forged.GetElementsByTagName("NameID", SamlXml.Assertion)[0]!.InnerText = "admin";
            var extensions = signed.CreateElement("samlp", "Extensions", SamlXml.Protocol);
            signed.DocumentElement!.ReplaceChild(forged, original);
            extensions.AppendChild(original);
            signed.DocumentElement.InsertBefore(extensions, forged);
            return signed.OuterXml;
        });

    [Fact]
    public async Task Duplicate_ids_are_refused() =>
        await AssertRefusedAsync("duplicate_id", id =>
        {
            var spec = Spec(id) with { SignResponse = false };
            var doc = MockSamlIdp.Load(app.Idp.Response(spec));
            var extensions = doc.CreateElement("samlp", "Extensions", SamlXml.Protocol);
            var decoy = doc.CreateElement("x", "Decoy", "urn:decoy");
            decoy.SetAttribute("ID", spec.AssertionId);
            extensions.AppendChild(decoy);
            doc.DocumentElement!.InsertAfter(extensions, doc.DocumentElement.FirstChild);
            return doc.OuterXml;
        });

    [Fact]
    public async Task A_signature_that_references_a_different_element_is_refused() =>
        await AssertRefusedAsync("wrapping", id =>
        {
            // The assertion carries a valid signature, but over another element (an Extensions block with its own ID).
            var doc = MockSamlIdp.Load(app.Idp.UnsignedXml(Spec(id)));
            var extensions = doc.CreateElement("samlp", "Extensions", SamlXml.Protocol);
            extensions.SetAttribute("ID", "_ext");
            doc.DocumentElement!.InsertAfter(extensions, doc.DocumentElement.FirstChild);
            MockSamlIdp.Sign(doc, extensions, app.Idp.Certificate);
            var signature = extensions.GetElementsByTagName("Signature", SamlXml.DSig)[0]!;
            var assertion = (XmlElement)doc.GetElementsByTagName("Assertion", SamlXml.Assertion)[0]!;
            assertion.InsertAfter(signature, assertion.FirstChild);
            return doc.OuterXml;
        });

    // ------------------------------------------------------------------ issuer, audience, destination, recipient, time

    [Fact]
    public async Task Wrong_issuer_is_refused() =>
        await AssertRefusedAsync("issuer", id => app.Idp.Response(Spec(id) with { Issuer = "https://evil.example/idp" }));

    [Fact]
    public async Task Wrong_audience_is_refused() =>
        await AssertRefusedAsync("audience", id => app.Idp.Response(Spec(id) with { Audience = "https://other-sp.example" }));

    [Fact]
    public async Task Wrong_destination_is_refused() =>
        await AssertRefusedAsync("destination", id => app.Idp.Response(Spec(id) with { Destination = "https://other-sp.example/acs" }));

    [Fact]
    public async Task Missing_destination_on_a_signed_response_is_refused() =>
        await AssertRefusedAsync("destination", id => app.Idp.Response(Spec(id) with { OmitDestination = true }));

    [Fact]
    public async Task Wrong_recipient_is_refused() =>
        await AssertRefusedAsync("subject_confirmation", id => app.Idp.Response(Spec(id) with { Recipient = "https://other-sp.example/acs" }));

    [Fact]
    public async Task Expired_assertion_is_refused() =>
        await AssertRefusedAsync("subject_confirmation", id => app.Idp.Response(Spec(id) with { NotOnOrAfter = DateTime.UtcNow.AddMinutes(-10) }));

    [Fact]
    public async Task Not_yet_valid_assertion_is_refused() =>
        await AssertRefusedAsync("not_yet_valid", id => app.Idp.Response(Spec(id) with { NotBefore = DateTime.UtcNow.AddMinutes(10) }));

    // ------------------------------------------------------------------ InResponseTo, replay, browser binding

    [Fact]
    public async Task Response_to_a_request_this_server_never_made_is_refused() =>
        await AssertRefusedAsync("browser_mismatch", _ => app.Idp.Response(Spec("_made_up_request")));

    [Fact]
    public async Task Unsolicited_idp_initiated_response_is_refused() =>
        await AssertRefusedAsync("unsolicited", id => app.Idp.Response(Spec(id) with { OmitInResponseTo = true }));

    [Fact]
    public async Task Response_posted_from_another_browser_is_refused()
    {
        var (id, _) = await app.StartAsync(app.Browser());
        var location = await app.PostAsync(app.Browser(), app.Idp.Response(Spec(id))); // a different cookie jar (login CSRF)
        Assert.Equal(Refused, location);
        Assert.Equal("browser_mismatch", LastFailure());
    }

    [Fact]
    public async Task Replaying_the_same_response_is_refused()
    {
        var browser = app.Browser();
        var (id, _) = await app.StartAsync(browser);
        var xml = app.Idp.Response(Spec(id));
        Assert.Equal("/", await app.PostAsync(browser, xml));
        // The request is marked answered (one-time use) and the browser's request cookie is cleared.
        using (var db = app.Db())
            Assert.NotNull(db.SamlMessages.Single(m => m.Id == id).UsedAt);
        Assert.Equal(Refused, await app.PostAsync(browser, xml));
        Assert.Contains(LastFailure(), new[] { "browser_mismatch", "replay" });
    }

    [Fact]
    public async Task Replaying_an_assertion_id_in_a_new_sign_in_is_refused()
    {
        var assertionId = "_replayed" + Guid.NewGuid().ToString("N");
        var email = NewEmail();
        Assert.Equal("/", await app.SignInAsync(app.Browser(), id => Spec(id, email) with { AssertionId = assertionId }));
        await AssertRefusedAsync("replay", id => app.Idp.Response(Spec(id, email) with { AssertionId = assertionId }));
    }

    // ------------------------------------------------------------------ parsing, status, size, redirects, accounts

    [Fact]
    public async Task Dtd_and_external_entities_are_refused() =>
        await AssertRefusedAsync("malformed", id =>
            "<?xml version=\"1.0\"?><!DOCTYPE r [<!ENTITY x SYSTEM \"file:///etc/passwd\">]>" + app.Idp.Response(Spec(id)).Replace("Sam Saml", "&x;"));

    [Fact]
    public async Task Error_status_signs_nobody_in() =>
        await AssertRefusedAsync("status", id => app.Idp.Response(Spec(id) with { Status = "urn:oasis:names:tc:SAML:2.0:status:Responder" }));

    [Fact]
    public async Task Oversized_response_is_refused()
    {
        var browser = app.Browser();
        var (id, _) = await app.StartAsync(browser);
        var huge = app.Idp.Response(Spec(id) with { DisplayName = new string('x', 100_000) }); // cap is 64 KiB in this fixture
        Assert.Equal(Refused, await app.PostAsync(browser, huge));
        Assert.Equal("too_large", LastFailure());
    }

    [Theory]
    [InlineData("https://evil.example/phish")]
    [InlineData("//evil.example/phish")]
    [InlineData("/\\evil.example")]
    public async Task RelayState_is_only_used_for_same_site_paths(string relay)
    {
        var browser = app.Browser();
        var (id, _) = await app.StartAsync(browser, "/explore");
        var location = await app.PostAsync(browser, app.Idp.Response(Spec(id)), relay);
        Assert.Equal("/explore", location); // the stored return URL, not the attacker's
    }

    [Fact]
    public async Task Disallowed_email_domain_is_refused()
    {
        var location = await app.SignInAsync(app.Browser(), id => Spec(id, NewEmail("evil.example")));
        Assert.Equal("/account/login?sso_error=domain&provider=saml", location);
    }

    [Fact]
    public async Task Transient_nameid_is_refused() =>
        await AssertRefusedAsync("transient_nameid", id => app.Idp.Response(Spec(id) with { NameIdFormat = SamlService.TransientFormat }));

    [Fact]
    public async Task Deactivated_account_cannot_sign_in_with_saml()
    {
        var email = NewEmail();
        Assert.Equal("/", await app.SignInAsync(app.Browser(), id => Spec(id, email) with { NameId = "deact-" + email }));
        var user = (await FindByEmailAsync(email))!;
        await using (var s = app.Scope(app.UserId("alice")))
            await s.Get<ByteBench.Services.AdminUserService>().DeactivateAsync(user.Id);
        var location = await app.SignInAsync(app.Browser(), id => Spec(id, email) with { NameId = "deact-" + email });
        Assert.Equal("/account/login?sso_error=locked&provider=saml", location);
    }

    // ------------------------------------------------------------------ logout, request signing, metadata

    [Fact]
    public async Task Signing_out_continues_with_saml_single_logout()
    {
        var browser = app.Browser();
        var nameId = "slo-" + Guid.NewGuid().ToString("N");
        Assert.Equal("/", await app.SignInAsync(browser, id => Spec(id) with { NameId = nameId }));

        var page = await browser.GetStringAsync("/account/profile");
        var token = SsoTestApp.Token(page);
        var logout = await browser.PostAsync("/account/logout", new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = token }));
        Assert.Equal("/saml/logout", logout.Headers.Location!.ToString());

        var slo = await browser.GetAsync("/saml/logout");
        Assert.Equal(HttpStatusCode.OK, slo.StatusCode);
        var html = await slo.Content.ReadAsStringAsync();
        var url = WebUtility.HtmlDecode(System.Text.RegularExpressions.Regex.Match(html, "url=([^\"]+)\"").Groups[1].Value);
        Assert.StartsWith(app.Idp.SloUrl + "?SAMLRequest=", url);
        var request = MockSamlIdp.ParseRequest(HttpUtility.ParseQueryString(new Uri(url).Query)["SAMLRequest"]!);
        Assert.Equal("LogoutRequest", request.DocumentElement!.LocalName);
        Assert.Equal(nameId, request.GetElementsByTagName("NameID", SamlXml.Assertion)[0]!.InnerText);

        // The session cookie is gone: a second visit doesn't log out again, and a signed-in user can't be logged out by a link.
        Assert.Equal("/account/login", (await browser.GetAsync("/saml/logout")).Headers.Location!.ToString());
    }

    [Fact]
    public async Task Signed_authn_requests_verify_with_the_certificate_in_sp_metadata()
    {
        await using var s = app.Scope(app.UserId("alice"));
        var admin = s.Get<SamlAdminService>();
        var store = s.Get<SamlConfigStore>();
        try
        {
            await admin.GenerateSigningKeyAsync();
            var (o, _) = await admin.GetAsync();
            o.SignAuthnRequests = true;
            Assert.Empty(await admin.SaveAsync(o));

            var md = MockSamlIdp.Load(await app.CreateClient().GetStringAsync("/saml/metadata"));
            var certB64 = md.GetElementsByTagName("X509Certificate", SamlXml.DSig)[0]!.InnerText;
            using var cert = X509CertificateLoader.LoadCertificate(Convert.FromBase64String(certB64));

            var (_, redirect) = await app.StartAsync(app.Browser(), "/explore");
            var raw = redirect.Query.TrimStart('?');
            var signedPart = raw[..raw.IndexOf("&Signature=", StringComparison.Ordinal)];
            var signature = Convert.FromBase64String(Uri.UnescapeDataString(raw[(raw.IndexOf("&Signature=", StringComparison.Ordinal) + 11)..]));
            Assert.Contains("SigAlg=" + Uri.EscapeDataString(SamlXml.RsaSha256), signedPart);
            using var rsa = cert.GetRSAPublicKey()!;
            Assert.True(rsa.VerifyData(Encoding.UTF8.GetBytes(signedPart), signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        }
        finally { await store.ResetAsync(); }
    }

    [Fact]
    public async Task Admin_settings_require_a_site_admin_and_saved_settings_override_appsettings()
    {
        await using (var s = app.Scope(app.UserId("bob")))
            await Assert.ThrowsAsync<ByteBench.Services.ForbiddenException>(() => s.Get<SamlAdminService>().GetAsync());

        await using var a = app.Scope(app.UserId("alice"));
        var admin = a.Get<SamlAdminService>();
        try
        {
            var (o, saved) = await admin.GetAsync();
            Assert.False(saved);
            o.IdpCertificates = ["not a certificate"];
            Assert.Contains(await admin.SaveAsync(o), e => e.Contains("Certificate 1"));
            o.IdpCertificates = [app.Idp.CertificatePem];
            o.DisplayName = "Corp SSO";
            Assert.Empty(await admin.SaveAsync(o));
            Assert.Contains("Sign in with Corp SSO", await app.CreateClient().GetStringAsync("/account/login"));
            using var db = app.Db();
            Assert.Contains(db.AuditEvents.ToList(), e => e.Action == "admin.saml_settings_saved");
        }
        finally { await a.Get<SamlConfigStore>().ResetAsync(); }
    }

    [Fact]
    public async Task Idp_metadata_is_parsed_and_its_url_goes_through_the_ssrf_guard()
    {
        var md = SamlMetadataClient.Parse(Encoding.UTF8.GetBytes(app.Idp.MetadataXml()));
        Assert.Equal(MockSamlIdp.EntityId, md.EntityId);
        Assert.Equal(app.Idp.SsoUrl, md.SsoUrl);       // the HTTP-Redirect binding, not the POST one
        Assert.Equal(app.Idp.SloUrl, md.SloUrl);
        Assert.Equal([Convert.ToBase64String(app.Idp.Certificate.RawData)], md.Certificates); // signing keys only

        // This fixture allows private networks (Webhooks:AllowPrivateNetworks), so the mock's metadata can be fetched…
        var fetched = await app.Services.GetRequiredService<SamlMetadataClient>().FetchAsync(app.Idp.BaseUrl + "/metadata");
        Assert.Equal(MockSamlIdp.EntityId, fetched.EntityId);

        // …but by default loopback, private and metadata addresses are refused.
        var strict = new SamlMetadataClient(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Webhooks:AllowPrivateNetworks"] = "false" }).Build());
        Assert.NotNull(strict.Validate(app.Idp.BaseUrl + "/metadata"));
        Assert.NotNull(strict.Validate("http://169.254.169.254/latest/meta-data"));
        Assert.NotNull(strict.Validate("https://user:pw@idp.example/metadata"));
        await Assert.ThrowsAsync<SamlException>(() => strict.FetchAsync(app.Idp.BaseUrl + "/metadata"));
        Assert.Null(strict.Validate("https://idp.example/metadata"));
    }

    [Fact]
    public void Metadata_with_a_dtd_is_refused() =>
        Assert.Throws<SamlException>(() => SamlMetadataClient.Parse(Encoding.UTF8.GetBytes("<?xml version=\"1.0\"?><!DOCTYPE a [<!ENTITY b \"c\">]><a>&b;</a>")));
}
