using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography.Xml;
using System.Text;
using System.Xml;
using ByteBench.Services.Saml;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ByteBench.Tests.Sso;

/// <summary>Everything a mock SAML response can say (and get wrong).</summary>
public sealed record SamlResponseSpec
{
    public required string InResponseTo { get; init; }
    public string NameId { get; init; } = "user-" + Guid.NewGuid().ToString("N")[..8];
    public string NameIdFormat { get; init; } = "urn:oasis:names:tc:SAML:2.0:nameid-format:persistent";
    public string? Email { get; init; }
    public string? DisplayName { get; init; }
    public string? UserName { get; init; }
    public string[] Groups { get; init; } = [];
    public string? Issuer { get; init; }
    public string? AssertionIssuer { get; init; }
    public string? Audience { get; init; }
    public string? Destination { get; init; }
    public bool OmitDestination { get; init; }
    public string? Recipient { get; init; }
    public DateTime? NotBefore { get; init; }
    public DateTime? NotOnOrAfter { get; init; }
    public string Status { get; init; } = "urn:oasis:names:tc:SAML:2.0:status:Success";
    public bool SignResponse { get; init; } = true;
    public bool SignAssertion { get; init; } = true;
    public bool Sha1 { get; init; }
    public string AssertionId { get; init; } = "_a" + Guid.NewGuid().ToString("N");
    public string ResponseId { get; init; } = "_r" + Guid.NewGuid().ToString("N");
    /// <summary>Signs with this certificate instead of the IdP's.</summary>
    public X509Certificate2? SigningCertificate { get; init; }
    public bool OmitInResponseTo { get; init; }
}

/// <summary>
/// A tiny SAML 2.0 identity provider on a real Kestrel port, for tests and local manual testing. It signs responses with a
/// throw-away self-signed certificate generated at start-up (and has a second, untrusted "rogue" certificate for negative
/// tests). <c>GET /sso</c> takes an HTTP-Redirect AuthnRequest and answers with an auto-posting form to the ACS for
/// <see cref="NextUser"/> (or, when that's null, a small form to type a user in); <c>GET /metadata</c> serves its metadata;
/// <c>GET /slo</c> records a LogoutRequest and sends the browser back with a LogoutResponse.
/// </summary>
public sealed class MockSamlIdp : IAsyncDisposable
{
    public const string EntityId = "https://idp.mock.test/saml";
    private WebApplication? web;

    public X509Certificate2 Certificate { get; } = CreateCertificate("CN=Mock SAML IdP");
    public X509Certificate2 RogueCertificate { get; } = CreateCertificate("CN=Rogue IdP");
    public string BaseUrl { get; private set; } = "";
    public string SsoUrl => BaseUrl + "/sso";
    public string SloUrl => BaseUrl + "/slo";
    /// <summary>The SP's ACS URL and entity id the browser flow posts to (set by the fixture).</summary>
    public string AcsUrl { get; set; } = "http://localhost/saml/acs";
    public string SpEntityId { get; set; } = "https://bytebench.test/sp";
    public Func<string, SamlResponseSpec>? NextUser { get; set; }
    public ConcurrentQueue<string> LogoutRequests { get; } = new();

    public string CertificatePem => Certificate.ExportCertificatePem();

    public static X509Certificate2 CreateCertificate(string subject)
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest(subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
    }

    public async Task StartAsync(int port = 0)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrel(o => o.Listen(IPAddress.Loopback, port));
        builder.Services.AddRouting();
        web = builder.Build();
        web.MapGet("/metadata", () => Results.Text(MetadataXml(), "application/samlmetadata+xml"));
        web.MapGet("/sso", (HttpContext ctx) =>
        {
            var request = ParseRequest(ctx.Request.Query["SAMLRequest"].ToString());
            var relay = ctx.Request.Query["RelayState"].ToString();
            var id = request.DocumentElement!.GetAttribute("ID");
            if (NextUser is { } next) return AutoPost(Response(next(id)), relay);
            var html = $"""
                <!doctype html><meta charset="utf-8"><title>Mock SAML IdP</title>
                <body style="font-family:sans-serif;max-width:420px;margin:40px auto">
                <h2>Mock SAML identity provider</h2><p>Sign in to <b>{WebUtility.HtmlEncode(SpEntityId)}</b> as:</p>
                <form method="post" action="/sso/approve">
                  <input type="hidden" name="id" value="{WebUtility.HtmlEncode(id)}"><input type="hidden" name="relay" value="{WebUtility.HtmlEncode(relay)}">
                  <p><label>NameID <input name="nameid" value="saml-{Guid.NewGuid():N}"></label></p>
                  <p><label>Email <input name="email" value="saml-user@example.org"></label></p>
                  <p><label>Name <input name="name" value="Sam Saml"></label></p>
                  <p><label>Username <input name="username" value="sam.saml"></label></p>
                  <p><label>Groups (comma-separated) <input name="groups" value=""></label></p>
                  <button type="submit">Sign in</button>
                </form></body>
                """;
            return Results.Content(html, "text/html; charset=utf-8");
        });
        web.MapPost("/sso/approve", async (HttpRequest req) =>
        {
            var f = await req.ReadFormAsync();
            string? N(string k) => string.IsNullOrWhiteSpace(f[k]) ? null : f[k].ToString().Trim();
            return AutoPost(Response(new SamlResponseSpec
            {
                InResponseTo = f["id"]!, NameId = N("nameid") ?? "x", Email = N("email"), DisplayName = N("name"), UserName = N("username"),
                Groups = (N("groups") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            }), f["relay"].ToString());
        });
        web.MapGet("/slo", (HttpContext ctx) =>
        {
            LogoutRequests.Enqueue(Encoding.UTF8.GetString(SamlXml.InflateBase64(ctx.Request.Query["SAMLRequest"].ToString(), 1 << 20)));
            return Results.Redirect(AcsUrl.Replace("/saml/acs", "/saml/logout") + "?SAMLResponse=done");
        });
        await web.StartAsync();
        var address = web.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        BaseUrl = address.Replace("[::1]", "127.0.0.1").TrimEnd('/');
    }

    public async ValueTask DisposeAsync()
    {
        if (web != null) await web.DisposeAsync();
        Certificate.Dispose();
        RogueCertificate.Dispose();
    }

    public static XmlDocument ParseRequest(string samlRequest)
    {
        var doc = new XmlDocument { PreserveWhitespace = true, XmlResolver = null };
        doc.LoadXml(Encoding.UTF8.GetString(SamlXml.InflateBase64(samlRequest, 1 << 20)));
        return doc;
    }

    private IResult AutoPost(string responseXml, string relay)
    {
        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(responseXml));
        var html = $"""
            <!doctype html><meta charset="utf-8"><title>Posting to ByteBench</title>
            <body onload="document.forms[0].submit()"><form method="post" action="{WebUtility.HtmlEncode(AcsUrl)}">
            <input type="hidden" name="SAMLResponse" value="{b64}">{(relay.Length > 0 ? $"<input type=\"hidden\" name=\"RelayState\" value=\"{WebUtility.HtmlEncode(relay)}\">" : "")}
            <noscript><button type="submit">Continue</button></noscript></form></body>
            """;
        return Results.Content(html, "text/html; charset=utf-8");
    }

    public string MetadataXml() => $"""
        <?xml version="1.0"?>
        <md:EntityDescriptor xmlns:md="urn:oasis:names:tc:SAML:2.0:metadata" xmlns:ds="http://www.w3.org/2000/09/xmldsig#" entityID="{EntityId}">
          <md:IDPSSODescriptor protocolSupportEnumeration="urn:oasis:names:tc:SAML:2.0:protocol">
            <md:KeyDescriptor use="signing"><ds:KeyInfo><ds:X509Data><ds:X509Certificate>{Convert.ToBase64String(Certificate.RawData)}</ds:X509Certificate></ds:X509Data></ds:KeyInfo></md:KeyDescriptor>
            <md:KeyDescriptor use="encryption"><ds:KeyInfo><ds:X509Data><ds:X509Certificate>{Convert.ToBase64String(RogueCertificate.RawData)}</ds:X509Certificate></ds:X509Data></ds:KeyInfo></md:KeyDescriptor>
            <md:SingleLogoutService Binding="urn:oasis:names:tc:SAML:2.0:bindings:HTTP-Redirect" Location="{SloUrl}"/>
            <md:SingleSignOnService Binding="urn:oasis:names:tc:SAML:2.0:bindings:HTTP-POST" Location="{SsoUrl}/post"/>
            <md:SingleSignOnService Binding="urn:oasis:names:tc:SAML:2.0:bindings:HTTP-Redirect" Location="{SsoUrl}"/>
          </md:IDPSSODescriptor>
        </md:EntityDescriptor>
        """;

    /// <summary>The unsigned XML of a response (before <see cref="Sign"/>).</summary>
    public string UnsignedXml(SamlResponseSpec s)
    {
        var now = DateTime.UtcNow;
        string T(DateTime t) => SamlXml.Instant(t);
        var attrs = new StringBuilder();
        void Attr(string name, IEnumerable<string> values)
        {
            var v = values.ToList();
            if (v.Count == 0) return;
            attrs.Append($"<saml:Attribute Name=\"{name}\">");
            foreach (var x in v) attrs.Append($"<saml:AttributeValue>{SamlXml.Escape(x)}</saml:AttributeValue>");
            attrs.Append("</saml:Attribute>");
        }
        if (s.Email != null) Attr("email", [s.Email]);
        if (s.DisplayName != null) Attr("displayName", [s.DisplayName]);
        if (s.UserName != null) Attr("username", [s.UserName]);
        Attr("groups", s.Groups);
        var inResponseTo = s.OmitInResponseTo ? "" : $" InResponseTo=\"{s.InResponseTo}\"";
        var destination = s.OmitDestination ? "" : $" Destination=\"{SamlXml.Escape(s.Destination ?? AcsUrl)}\"";
        return $"<samlp:Response xmlns:samlp=\"urn:oasis:names:tc:SAML:2.0:protocol\" xmlns:saml=\"urn:oasis:names:tc:SAML:2.0:assertion\" ID=\"{s.ResponseId}\" Version=\"2.0\" IssueInstant=\"{T(now)}\"{destination}{inResponseTo}>" +
               $"<saml:Issuer>{SamlXml.Escape(s.Issuer ?? EntityId)}</saml:Issuer>" +
               $"<samlp:Status><samlp:StatusCode Value=\"{s.Status}\"/></samlp:Status>" +
               $"<saml:Assertion ID=\"{s.AssertionId}\" Version=\"2.0\" IssueInstant=\"{T(now)}\">" +
               $"<saml:Issuer>{SamlXml.Escape(s.AssertionIssuer ?? s.Issuer ?? EntityId)}</saml:Issuer>" +
               $"<saml:Subject><saml:NameID Format=\"{s.NameIdFormat}\">{SamlXml.Escape(s.NameId)}</saml:NameID>" +
               $"<saml:SubjectConfirmation Method=\"urn:oasis:names:tc:SAML:2.0:cm:bearer\"><saml:SubjectConfirmationData{inResponseTo} Recipient=\"{SamlXml.Escape(s.Recipient ?? AcsUrl)}\" NotOnOrAfter=\"{T(s.NotOnOrAfter ?? now.AddMinutes(5))}\"/></saml:SubjectConfirmation></saml:Subject>" +
               $"<saml:Conditions NotBefore=\"{T(s.NotBefore ?? now.AddSeconds(-30))}\" NotOnOrAfter=\"{T(s.NotOnOrAfter ?? now.AddMinutes(5))}\"><saml:AudienceRestriction><saml:Audience>{SamlXml.Escape(s.Audience ?? SpEntityId)}</saml:Audience></saml:AudienceRestriction></saml:Conditions>" +
               $"<saml:AuthnStatement AuthnInstant=\"{T(now)}\" SessionIndex=\"_s{Guid.NewGuid():N}\"><saml:AuthnContext><saml:AuthnContextClassRef>urn:oasis:names:tc:SAML:2.0:ac:classes:PasswordProtectedTransport</saml:AuthnContextClassRef></saml:AuthnContext></saml:AuthnStatement>" +
               (attrs.Length > 0 ? $"<saml:AttributeStatement>{attrs}</saml:AttributeStatement>" : "") +
               "</saml:Assertion></samlp:Response>";
    }

    /// <summary>A complete response: the XML from <see cref="UnsignedXml"/>, signed as the spec says.</summary>
    public string Response(SamlResponseSpec s)
    {
        var doc = Load(UnsignedXml(s));
        var cert = s.SigningCertificate ?? Certificate;
        var assertion = (XmlElement)doc.GetElementsByTagName("Assertion", "urn:oasis:names:tc:SAML:2.0:assertion")[0]!;
        if (s.SignAssertion) Sign(doc, assertion, cert, s.Sha1);
        if (s.SignResponse) Sign(doc, doc.DocumentElement!, cert, s.Sha1);
        return doc.OuterXml;
    }

    public static XmlDocument Load(string xml)
    {
        var doc = new XmlDocument { PreserveWhitespace = true, XmlResolver = null };
        doc.LoadXml(xml);
        return doc;
    }

    /// <summary>An enveloped signature of <paramref name="element"/> (by its ID), inserted after its Issuer.</summary>
    public static void Sign(XmlDocument doc, XmlElement element, X509Certificate2 cert, bool sha1 = false, string? referenceId = null)
    {
        using var key = cert.GetRSAPrivateKey()!;
        var signed = new SignedXml(doc) { SigningKey = key };
        signed.SignedInfo!.CanonicalizationMethod = SignedXml.XmlDsigExcC14NTransformUrl;
        signed.SignedInfo.SignatureMethod = sha1 ? SignedXml.XmlDsigRSASHA1Url : SignedXml.XmlDsigRSASHA256Url;
        var reference = new Reference("#" + (referenceId ?? element.GetAttribute("ID"))) { DigestMethod = sha1 ? SignedXml.XmlDsigSHA1Url : SignedXml.XmlDsigSHA256Url };
        reference.AddTransform(new XmlDsigEnvelopedSignatureTransform());
        reference.AddTransform(new XmlDsigExcC14NTransform());
        signed.AddReference(reference);
        signed.KeyInfo = new KeyInfo();
        signed.KeyInfo.AddClause(new KeyInfoX509Data(cert));
        signed.ComputeSignature();
        var signature = doc.ImportNode(signed.GetXml(), true);
        var issuer = element.ChildNodes.OfType<XmlElement>().FirstOrDefault(e => e.LocalName == "Issuer");
        if (issuer != null) element.InsertAfter(signature, issuer);
        else element.PrependChild(signature);
    }
}
