using System.Security.Cryptography.X509Certificates;
using ByteBench.Services.Sso;

namespace ByteBench.Services.Saml;

/// <summary>SAML attribute names read from the assertion (matched against an attribute's <c>Name</c> or <c>FriendlyName</c>, ignoring case).</summary>
public sealed class SamlAttributeMappings
{
    public string Email { get; set; } = "email";
    public string DisplayName { get; set; } = "displayName";
    public string UserName { get; set; } = "username";
    public string Groups { get; set; } = "groups";
}

/// <summary>
/// SAML 2.0 single sign-on (<c>Saml:*</c> in appsettings, or the copy an admin saved on <c>/admin/authentication</c>, which
/// wins). ByteBench is the service provider (SP); one identity provider (IdP) is supported.
/// </summary>
public sealed class SamlOptions
{
    public const string Section = "Saml";
    /// <summary>The account login provider name (AspNetUserLogins) and the name in sign-in error URLs.</summary>
    public const string ProviderName = "saml";

    public bool Enabled { get; set; }
    /// <summary>"Sign in with {DisplayName}".</summary>
    public string DisplayName { get; set; } = "SAML";

    // ----- Identity provider -----
    /// <summary>The IdP's entity id: the <c>Issuer</c> of its responses and assertions must equal it.</summary>
    public string IdpEntityId { get; set; } = "";
    /// <summary>The IdP's single sign-on URL for the HTTP-Redirect binding.</summary>
    public string IdpSsoUrl { get; set; } = "";
    /// <summary>Optional: the IdP's single logout URL (HTTP-Redirect). Without it, signing out only ends the ByteBench session.</summary>
    public string? IdpSloUrl { get; set; }
    /// <summary>
    /// The IdP's signing certificates (PEM, or base64 DER as in metadata). Responses must be signed by one of them; list
    /// two during a certificate rollover.
    /// </summary>
    public List<string> IdpCertificates { get; set; } = [];
    /// <summary>Optional: the IdP's metadata URL. "Fetch metadata" on the admin page reads the entity id, URLs and certificates from it.</summary>
    public string? IdpMetadataUrl { get; set; }

    // ----- Service provider (ByteBench) -----
    /// <summary>ByteBench's entity id (the audience of assertions). Empty: <c>{site}/saml/metadata</c>.</summary>
    public string? SpEntityId { get; set; }
    /// <summary>The site's public URL, when the request's own scheme and host aren't it (e.g. behind an unconfigured proxy). Empty: from the request.</summary>
    public string? BaseUrl { get; set; }
    public string NameIdFormat { get; set; } = "urn:oasis:names:tc:SAML:1.1:nameid-format:unspecified";
    /// <summary>Sign AuthnRequests and LogoutRequests with the SP key (generate it on the admin page; stored encrypted).</summary>
    public bool SignAuthnRequests { get; set; }
    /// <summary>Require the assertion itself to be signed (a signed response around an unsigned assertion is then refused).</summary>
    public bool WantAssertionsSigned { get; set; } = true;

    // ----- Accounts (the same rules as an OIDC provider) -----
    public SamlAttributeMappings Attributes { get; set; } = new();
    /// <summary>Create a ByteBench account on the first sign-in. Otherwise only existing (linked or matching) accounts can sign in.</summary>
    public bool AutoCreateUsers { get; set; } = true;
    /// <summary>Link to an existing account with the same (confirmed) email on the first sign-in.</summary>
    public bool LinkByEmail { get; set; } = true;
    /// <summary>
    /// SAML has no "email verified" flag: treat the IdP's email as verified (right for your own company IdP). When off, accounts
    /// are neither created nor linked by email.
    /// </summary>
    public bool TrustEmail { get; set; } = true;
    /// <summary>If set, only emails in these domains may sign in.</summary>
    public List<string> AllowedEmailDomains { get; set; } = [];
    /// <summary>Group attribute values that make the user a site admin (and revoke it again if SAML granted it).</summary>
    public List<string> AdminGroups { get; set; } = [];
    public List<ExternalGroupMapping> GroupMappings { get; set; } = [];

    // ----- Validation -----
    /// <summary>Allowed clock difference with the IdP for NotBefore / NotOnOrAfter (0–600 seconds).</summary>
    public int ClockSkewSeconds { get; set; } = 120;
    /// <summary>Largest accepted SAMLResponse (decoded), in KiB.</summary>
    public int MaxResponseKilobytes { get; set; } = 256;
    /// <summary>Accept RSA-SHA1 signatures and SHA-1 digests (old IdPs only). Off: SHA-256 or better is required.</summary>
    public bool AllowSha1 { get; set; }

    /// <summary>Errors that make the configuration unusable (the sign-in button is then hidden).</summary>
    public List<string> Validate()
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(IdpEntityId)) errors.Add("The IdP entity id is missing.");
        if (!IsHttpUrl(IdpSsoUrl)) errors.Add("The IdP SSO URL must be an absolute http(s) URL.");
        if (!string.IsNullOrWhiteSpace(IdpSloUrl) && !IsHttpUrl(IdpSloUrl)) errors.Add("The IdP logout URL must be an absolute http(s) URL.");
        if (IdpCertificates.Count == 0) errors.Add("At least one IdP signing certificate is required.");
        foreach (var (pem, i) in IdpCertificates.Select((c, i) => (c, i)))
            if (SamlCertificates.TryParse(pem, out _) is { } err) errors.Add($"Certificate {i + 1}: {err}");
        if (!string.IsNullOrWhiteSpace(BaseUrl) && !IsHttpUrl(BaseUrl)) errors.Add("The base URL must be an absolute http(s) URL.");
        if (string.IsNullOrWhiteSpace(Attributes.Email)) errors.Add("The email attribute name is missing.");
        return errors;
    }

    public string Title => string.IsNullOrWhiteSpace(DisplayName) ? "SAML" : DisplayName.Trim();

    /// <summary>The OIDC provider settings the shared account logic (<see cref="SsoService"/>) works with.</summary>
    public SsoProviderOptions ToProvider() => new()
    {
        Name = ProviderName,
        DisplayName = Title,
        ClientId = "saml",
        AutoCreateUsers = AutoCreateUsers,
        LinkByVerifiedEmail = LinkByEmail,
        TrustEmailVerified = TrustEmail,
        AllowedEmailDomains = AllowedEmailDomains.Select(d => d.Trim().TrimStart('@').ToLowerInvariant()).Where(d => d.Length > 0).ToList(),
        AdminGroups = AdminGroups.Where(g => !string.IsNullOrWhiteSpace(g)).Select(g => g.Trim()).ToList(),
        GroupMappings = GroupMappings,
        Icon = "key",
        TwoFactor = SsoTwoFactorPolicy.ByteBench,
        SourceOverride = "saml",
    };

    public SamlOptions Clone() => System.Text.Json.JsonSerializer.Deserialize<SamlOptions>(System.Text.Json.JsonSerializer.Serialize(this))!;

    private static bool IsHttpUrl(string? s) =>
        Uri.TryCreate(s?.Trim(), UriKind.Absolute, out var u) && u.Scheme is "http" or "https" && !string.IsNullOrEmpty(u.Host);
}

/// <summary>Parses the pasted IdP certificates.</summary>
public static class SamlCertificates
{
    /// <summary>Null when <paramref name="text"/> is a PEM or base64 DER X.509 certificate, else why not.</summary>
    public static string? TryParse(string? text, out X509Certificate2? cert)
    {
        cert = null;
        if (string.IsNullOrWhiteSpace(text)) return "empty";
        var t = text.Trim();
        try
        {
            if (t.Contains("-----BEGIN", StringComparison.Ordinal)) cert = X509Certificate2.CreateFromPem(t);
            else cert = X509CertificateLoader.LoadCertificate(Convert.FromBase64String(string.Concat(t.Where(c => !char.IsWhiteSpace(c)))));
        }
        catch (Exception e) when (e is FormatException or System.Security.Cryptography.CryptographicException or ArgumentException)
        {
            return "not a valid X.509 certificate (paste the PEM, including the BEGIN/END lines).";
        }
        if (cert.GetRSAPublicKey() == null) { cert.Dispose(); cert = null; return "only RSA certificates are supported."; }
        return null;
    }

    public static List<X509Certificate2> ParseAll(IEnumerable<string> texts) =>
        texts.Select(t => TryParse(t, out var c) == null ? c : null).OfType<X509Certificate2>().ToList();

    /// <summary>The base64 DER body (as in metadata) of a certificate.</summary>
    public static string ToBase64(X509Certificate2 cert) => Convert.ToBase64String(cert.RawData);

    public static string Describe(X509Certificate2 cert) =>
        $"{cert.Subject} · SHA-256 {Convert.ToHexString(cert.GetCertHash(System.Security.Cryptography.HashAlgorithmName.SHA256))[..16]}… · expires {cert.NotAfter:yyyy-MM-dd}";
}
