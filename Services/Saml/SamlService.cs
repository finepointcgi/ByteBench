using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using ByteBench.Data;
using ByteBench.Services.Sso;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;

namespace ByteBench.Services.Saml;

/// <summary>
/// SP-initiated SAML 2.0 Web Browser SSO: an AuthnRequest over HTTP-Redirect, the Response over HTTP-POST to the ACS. The
/// response is validated by <see cref="SamlResponseValidator"/>; this class adds the checks that need state:
/// <list type="bullet">
/// <item>InResponseTo must be an AuthnRequest this server sent in the last 10 minutes, answered only once, from the same
/// browser that started it (a short-lived, Data Protection-encrypted cookie holds the request id).</item>
/// <item>An assertion ID is accepted once (replay protection until the assertion expires).</item>
/// </list>
/// The account is then found, linked or created by the same code as OIDC sign-in (<see cref="SsoService"/>): email-domain
/// rules, auto-create, link by email, admin group, group mappings, lockout/deactivation and ByteBench two-factor.
/// </summary>
public sealed class SamlService(SamlConfigStore store, IDbContextFactory<AppDbContext> dbFactory, SsoService sso, IDataProtectionProvider dataProtection,
    Audit.AuditLog audit, ILogger<SamlService> log, TimeProvider clock)
{
    public const string RequestCookie = "bytebench.saml.req";
    public const string SessionCookie = "bytebench.saml.session";
    private const string RequestPurpose = "ByteBench.Saml.Request.v1";
    private const string SessionPurpose = "ByteBench.Saml.Session.v1";
    public static readonly TimeSpan RequestLifetime = TimeSpan.FromMinutes(10);
    public const string TransientFormat = "urn:oasis:names:tc:SAML:2.0:nameid-format:transient";

    // ------------------------------------------------------------------ configuration helpers

    /// <summary>The configuration when SAML is on and usable, else null.</summary>
    public async Task<SamlOptions?> ActiveAsync(CancellationToken ct = default)
    {
        var o = await store.CurrentAsync(ct);
        return o.Enabled && o.Validate().Count == 0 ? o : null;
    }

    public static string BaseUrl(SamlOptions o, HttpRequest req) =>
        (string.IsNullOrWhiteSpace(o.BaseUrl) ? $"{req.Scheme}://{req.Host}{req.PathBase}" : o.BaseUrl.Trim()).TrimEnd('/');
    public static string AcsUrl(SamlOptions o, HttpRequest req) => BaseUrl(o, req) + "/saml/acs";
    public static string SloUrl(SamlOptions o, HttpRequest req) => BaseUrl(o, req) + "/saml/logout";
    public static string MetadataUrl(SamlOptions o, HttpRequest req) => BaseUrl(o, req) + "/saml/metadata";
    public static string SpEntityId(SamlOptions o, HttpRequest req) => string.IsNullOrWhiteSpace(o.SpEntityId) ? MetadataUrl(o, req) : o.SpEntityId.Trim();

    // ------------------------------------------------------------------ SP metadata

    public async Task<string> SpMetadataAsync(SamlOptions o, HttpRequest req)
    {
        using var cert = o.SignAuthnRequests ? await store.GetSigningCertificateAsync() : null;
        var sb = new StringBuilder();
        sb.Append($"<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<md:EntityDescriptor xmlns:md=\"{SamlXml.Metadata}\" entityID=\"{SamlXml.Escape(SpEntityId(o, req))}\">\n");
        sb.Append($"  <md:SPSSODescriptor AuthnRequestsSigned=\"{(cert != null ? "true" : "false")}\" WantAssertionsSigned=\"{(o.WantAssertionsSigned ? "true" : "false")}\" protocolSupportEnumeration=\"{SamlXml.Protocol}\">\n");
        if (cert != null)
            sb.Append($"    <md:KeyDescriptor use=\"signing\"><ds:KeyInfo xmlns:ds=\"{SamlXml.DSig}\"><ds:X509Data><ds:X509Certificate>{SamlCertificates.ToBase64(cert)}</ds:X509Certificate></ds:X509Data></ds:KeyInfo></md:KeyDescriptor>\n");
        sb.Append($"    <md:SingleLogoutService Binding=\"{SamlXml.RedirectBinding}\" Location=\"{SamlXml.Escape(SloUrl(o, req))}\"/>\n");
        sb.Append($"    <md:NameIDFormat>{SamlXml.Escape(o.NameIdFormat)}</md:NameIDFormat>\n");
        sb.Append($"    <md:AssertionConsumerService Binding=\"{SamlXml.PostBinding}\" Location=\"{SamlXml.Escape(AcsUrl(o, req))}\" index=\"0\" isDefault=\"true\"/>\n");
        sb.Append("  </md:SPSSODescriptor>\n</md:EntityDescriptor>\n");
        return sb.ToString();
    }

    // ------------------------------------------------------------------ sign-in

    /// <summary>Stores a new AuthnRequest and returns the IdP URL to redirect the browser to.</summary>
    public async Task<string> StartLoginAsync(SamlOptions o, HttpContext http, string? returnUrl)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var id = SamlXml.NewId();
        var safeReturn = SsoService.SafeReturnUrl(returnUrl);
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            await db.SamlMessages.Where(m => m.ExpiresAt < now).ExecuteDeleteAsync();
            db.SamlMessages.Add(new SamlMessageRecord { Id = id, Kind = SamlMessageKind.AuthnRequest, ReturnUrl = safeReturn, CreatedAt = now, ExpiresAt = now + RequestLifetime });
            await db.SaveChangesAsync();
        }
        var xml = $"<samlp:AuthnRequest xmlns:samlp=\"{SamlXml.Protocol}\" xmlns:saml=\"{SamlXml.Assertion}\" ID=\"{id}\" Version=\"2.0\" " +
                  $"IssueInstant=\"{SamlXml.Instant(now)}\" Destination=\"{SamlXml.Escape(o.IdpSsoUrl.Trim())}\" " +
                  $"AssertionConsumerServiceURL=\"{SamlXml.Escape(AcsUrl(o, http.Request))}\" ProtocolBinding=\"{SamlXml.PostBinding}\">" +
                  $"<saml:Issuer>{SamlXml.Escape(SpEntityId(o, http.Request))}</saml:Issuer>" +
                  $"<samlp:NameIDPolicy Format=\"{SamlXml.Escape(o.NameIdFormat)}\" AllowCreate=\"true\"/></samlp:AuthnRequest>";
        using var cert = o.SignAuthnRequests ? await store.GetSigningCertificateAsync() : null;
        if (o.SignAuthnRequests && cert == null) log.LogWarning("Saml:SignAuthnRequests is on but no SP signing key was generated; sending the request unsigned");
        using var key = cert?.GetRSAPrivateKey();
        // RelayState: only ever a same-site path (it comes back from the IdP and is checked again there).
        var query = SamlXml.RedirectQuery("SAMLRequest", xml, safeReturn == "/" ? null : safeReturn, key);

        // Ties the response to this browser: the ACS only accepts an InResponseTo this cookie names (login CSRF defence).
        // The IdP POSTs cross-site, so the cookie must be SameSite=None (which browsers only accept when Secure).
        var secure = http.Request.IsHttps;
        http.Response.Cookies.Append(RequestCookie, dataProtection.CreateProtector(RequestPurpose).ToTimeLimitedDataProtector().Protect(id, RequestLifetime),
            new CookieOptions { HttpOnly = true, Secure = secure, SameSite = secure ? SameSiteMode.None : SameSiteMode.Lax, Path = "/saml", MaxAge = RequestLifetime, IsEssential = true });
        var sso = o.IdpSsoUrl.Trim();
        return sso + (sso.Contains('?') ? "&" : "?") + query;
    }

    /// <summary>Handles a POST to the ACS. Returns the URL to redirect to (the return URL, the 2FA step, or the sign-in page with an error).</summary>
    public async Task<string> HandleAcsAsync(SamlOptions o, HttpContext http)
    {
        var provider = o.ToProvider();
        var maxBytes = Math.Clamp(o.MaxResponseKilobytes, 16, 4096) * 1024;
        try
        {
            var samlResponse = await ReadResponseFieldAsync(http, maxBytes);
            byte[] xml;
            try { xml = Convert.FromBase64String(samlResponse.Trim()); }
            catch (FormatException) { throw new SamlException("malformed", "SAMLResponse isn't valid base64."); }
            var doc = SamlXml.Load(xml, maxBytes);

            var now = clock.GetUtcNow().UtcDateTime;
            var expectations = new SamlExpectations(o.IdpEntityId.Trim(), SpEntityId(o, http.Request), AcsUrl(o, http.Request),
                SamlCertificates.ParseAll(o.IdpCertificates), o.WantAssertionsSigned, TimeSpan.FromSeconds(Math.Clamp(o.ClockSkewSeconds, 0, 600)), o.AllowSha1, now);
            SamlAssertion assertion;
            try { assertion = SamlResponseValidator.Validate(doc, expectations); }
            finally { foreach (var c in expectations.Certificates) c.Dispose(); }

            var returnUrl = await ConsumeRequestAsync(http, assertion.InResponseTo, now);
            await RememberAssertionAsync(assertion, now);
            if (assertion.NameIdFormat == TransientFormat)
                throw new SamlException("transient_nameid", "The IdP sent a transient NameID, which changes every sign-in; configure a persistent or email NameID.");

            var relay = http.Request.Form["RelayState"].ToString();
            if (relay.Length > 0 && SsoService.SafeReturnUrl(relay) == relay) returnUrl = relay; // only ever a same-site path

            var id = Map(o, assertion);
            var next = await sso.SignInExternalAsync(provider, id, returnUrl);
            http.Response.Cookies.Delete(RequestCookie, new CookieOptions { Path = "/saml" });
            if (!next.StartsWith("/account/login?sso_error", StringComparison.Ordinal))
            {
                var session = JsonSerializer.Serialize(new SamlSession(assertion.NameId, assertion.NameIdFormat, assertion.SessionIndex));
                http.Response.Cookies.Append(SessionCookie, dataProtection.CreateProtector(SessionPurpose).Protect(session),
                    new CookieOptions { HttpOnly = true, Secure = http.Request.IsHttps, SameSite = SameSiteMode.Lax, Path = "/", IsEssential = true });
            }
            return next;
        }
        catch (SamlException e)
        {
            log.LogWarning("SAML response refused ({Code}): {Reason}", e.Code, e.Message);
            await audit.RecordAsync("auth.sso_failed", details: new { provider = SamlOptions.ProviderName, reason = e.Code, error = e.Message }, anonymous: true);
            return SsoService.LoginError(SsoErrors.Failed, SamlOptions.ProviderName);
        }
    }

    private static async Task<string> ReadResponseFieldAsync(HttpContext http, int maxBytes)
    {
        // base64 of the cap, plus RelayState and the form encoding.
        long limit = maxBytes / 3 * 4 * 3 + 16 * 1024;
        if (http.Request.ContentLength > limit) throw new SamlException("too_large", "The SAML response is too large.");
        if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } size) size.MaxRequestBodySize = limit;
        if (!http.Request.HasFormContentType) throw new SamlException("malformed", "The ACS expects an HTML form POST (HTTP-POST binding).");
        IFormCollection form;
        try { form = await http.Request.ReadFormAsync(new FormOptions { ValueLengthLimit = (int)limit, ValueCountLimit = 20, MultipartBodyLengthLimit = limit }, http.RequestAborted); }
        catch (Exception e) when (e is InvalidDataException or BadHttpRequestException or IOException)
        {
            throw new SamlException("too_large", "The SAML response is too large or malformed.");
        }
        var value = form["SAMLResponse"].ToString();
        if (value.Length == 0) throw new SamlException("missing", "The POST has no SAMLResponse.");
        if (value.Length > maxBytes / 3 * 4 + 8) throw new SamlException("too_large", "The SAML response is too large.");
        return value;
    }

    /// <summary>InResponseTo must name an unexpired, unanswered request this server sent to this browser. Marks it answered.</summary>
    private async Task<string> ConsumeRequestAsync(HttpContext http, string inResponseTo, DateTime now)
    {
        var cookie = http.Request.Cookies[RequestCookie];
        string? bound = null;
        if (!string.IsNullOrEmpty(cookie))
        {
            try { bound = dataProtection.CreateProtector(RequestPurpose).ToTimeLimitedDataProtector().Unprotect(cookie); }
            catch (System.Security.Cryptography.CryptographicException) { bound = null; }
        }
        if (bound == null || !string.Equals(bound, inResponseTo, StringComparison.Ordinal))
            throw new SamlException("browser_mismatch", "The response doesn't answer a sign-in started in this browser (or it took longer than 10 minutes).");

        await using var db = await dbFactory.CreateDbContextAsync();
        var record = await db.SamlMessages.AsNoTracking().FirstOrDefaultAsync(m => m.Id == inResponseTo && m.Kind == SamlMessageKind.AuthnRequest);
        if (record == null || record.ExpiresAt < now) throw new SamlException("in_response_to", "The response answers a request this server didn't make, or that expired.");
        var claimed = await db.SamlMessages.Where(m => m.Id == inResponseTo && m.UsedAt == null).ExecuteUpdateAsync(s => s.SetProperty(m => m.UsedAt, now));
        if (claimed != 1) throw new SamlException("replay", "This sign-in request was already answered (replay).");
        return record.ReturnUrl ?? "/";
    }

    /// <summary>Records the assertion id; a second use is a replay.</summary>
    private async Task RememberAssertionAsync(SamlAssertion a, DateTime now)
    {
        if (a.AssertionId.Length > 140) throw new SamlException("structure", "The assertion ID is too long.");
        var key = "assertion:" + a.AssertionId;
        await using var db = await dbFactory.CreateDbContextAsync();
        if (await db.SamlMessages.AnyAsync(m => m.Id == key)) throw new SamlException("replay", "This assertion was already used (replay).");
        db.SamlMessages.Add(new SamlMessageRecord { Id = key, Kind = SamlMessageKind.Assertion, CreatedAt = now, ExpiresAt = a.ExpiresAt > now ? a.ExpiresAt : now.AddMinutes(5), UsedAt = now });
        try { await db.SaveChangesAsync(); }
        catch (DbUpdateException) { throw new SamlException("replay", "This assertion was already used (replay)."); }
    }

    /// <summary>The attribute mapping: the assertion as the identity the shared account logic understands.</summary>
    public static ExternalIdentity Map(SamlOptions o, SamlAssertion a)
    {
        var m = o.Attributes;
        var email = a.First(m.Email);
        if (email == null && a.NameId.Contains('@') && !a.NameId.Contains(' ')) email = a.NameId; // an email NameID
        var groups = a.Values(m.Groups).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return new ExternalIdentity(SamlOptions.ProviderName, a.NameId, email, o.TrustEmail, a.First(m.UserName), a.First(m.DisplayName), groups, []);
    }

    // ------------------------------------------------------------------ single logout (SP-initiated)

    private sealed record SamlSession(string NameId, string? Format, string? SessionIndex);

    /// <summary>
    /// After the local sign-out: when the user signed in with SAML and the IdP has a logout URL, where to continue
    /// (<c>/saml/logout</c>), else null.
    /// </summary>
    public async Task<string?> AfterLocalSignOutAsync(HttpContext http)
    {
        if (string.IsNullOrEmpty(http.Request.Cookies[SessionCookie])) return null;
        var o = await ActiveAsync();
        if (o == null || string.IsNullOrWhiteSpace(o.IdpSloUrl))
        {
            http.Response.Cookies.Delete(SessionCookie, new CookieOptions { Path = "/" });
            return null;
        }
        return "/saml/logout";
    }

    /// <summary>The IdP logout URL (a signed-if-configured LogoutRequest) for the browser's SAML session; clears the session cookie.</summary>
    public async Task<string?> BuildLogoutRedirectAsync(SamlOptions o, HttpContext http)
    {
        var cookie = http.Request.Cookies[SessionCookie];
        http.Response.Cookies.Delete(SessionCookie, new CookieOptions { Path = "/" });
        if (string.IsNullOrEmpty(cookie) || string.IsNullOrWhiteSpace(o.IdpSloUrl)) return null;
        SamlSession? session;
        try { session = JsonSerializer.Deserialize<SamlSession>(dataProtection.CreateProtector(SessionPurpose).Unprotect(cookie)); }
        catch (Exception e) when (e is System.Security.Cryptography.CryptographicException or JsonException) { return null; }
        if (session == null) return null;

        var now = clock.GetUtcNow().UtcDateTime;
        var id = SamlXml.NewId();
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            db.SamlMessages.Add(new SamlMessageRecord { Id = id, Kind = SamlMessageKind.LogoutRequest, CreatedAt = now, ExpiresAt = now + RequestLifetime });
            await db.SaveChangesAsync();
        }
        var slo = o.IdpSloUrl.Trim();
        var xml = $"<samlp:LogoutRequest xmlns:samlp=\"{SamlXml.Protocol}\" xmlns:saml=\"{SamlXml.Assertion}\" ID=\"{id}\" Version=\"2.0\" " +
                  $"IssueInstant=\"{SamlXml.Instant(now)}\" Destination=\"{SamlXml.Escape(slo)}\">" +
                  $"<saml:Issuer>{SamlXml.Escape(SpEntityId(o, http.Request))}</saml:Issuer>" +
                  $"<saml:NameID{(session.Format == null ? "" : $" Format=\"{SamlXml.Escape(session.Format)}\"")}>{SamlXml.Escape(session.NameId)}</saml:NameID>" +
                  (session.SessionIndex == null ? "" : $"<samlp:SessionIndex>{SamlXml.Escape(session.SessionIndex)}</samlp:SessionIndex>") +
                  "</samlp:LogoutRequest>";
        using var cert = o.SignAuthnRequests ? await store.GetSigningCertificateAsync() : null;
        using var key = cert?.GetRSAPrivateKey();
        await audit.RecordAsync("auth.saml_logout", details: new { provider = SamlOptions.ProviderName }, anonymous: true);
        return slo + (slo.Contains('?') ? "&" : "?") + SamlXml.RedirectQuery("SAMLRequest", xml, null, key);
    }
}
