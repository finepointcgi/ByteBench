using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;

namespace ByteBench.Services.Saml;

/// <summary>
/// The SAML section of <c>/admin/authentication</c>: save or reset the configuration, fetch IdP metadata, generate the SP
/// signing key. Site admins only (<see cref="ProjectAccess"/>); audited as <c>admin.saml_*</c> (never with secrets: the
/// configuration holds no secrets, and the signing key is never shown or logged).
/// </summary>
public sealed partial class SamlAdminService(SamlConfigStore store, SamlMetadataClient metadata, ProjectAccess access, Audit.AuditLog audit)
{
    public async Task<(SamlOptions Options, bool Saved)> GetAsync()
    {
        await RequireAdminAsync();
        return await store.GetAsync();
    }

    /// <summary>Validates and saves. Returns the validation errors when SAML is enabled with an unusable configuration (nothing is saved then).</summary>
    public async Task<List<string>> SaveAsync(SamlOptions options)
    {
        var me = await RequireAdminAsync();
        Normalize(options);
        var errors = options.Enabled ? options.Validate() : [];
        if (!options.Enabled && !string.IsNullOrWhiteSpace(options.IdpMetadataUrl) && metadata.Validate(options.IdpMetadataUrl) is { } bad) errors.Add(bad);
        if (errors.Count > 0) return errors;
        await store.SaveAsync(options, me);
        await audit.RecordAsync("admin.saml_settings_saved", details: new
        {
            options.Enabled, options.IdpEntityId, options.IdpSsoUrl, certificates = options.IdpCertificates.Count, options.SignAuthnRequests,
            options.WantAssertionsSigned, options.AutoCreateUsers, domains = options.AllowedEmailDomains, adminGroups = options.AdminGroups,
        }, actorId: me);
        return [];
    }

    public async Task ResetAsync()
    {
        var me = await RequireAdminAsync();
        await store.ResetAsync();
        await audit.RecordAsync("admin.saml_settings_reset", actorId: me);
    }

    /// <summary>Reads the IdP's metadata into <paramref name="options"/> (entity id, SSO/SLO URLs, certificates). Not saved.</summary>
    public async Task<SamlIdpMetadata> FetchMetadataAsync(SamlOptions options)
    {
        await RequireAdminAsync();
        var md = await metadata.FetchAsync(options.IdpMetadataUrl ?? "");
        if (md.EntityId.Length > 0) options.IdpEntityId = md.EntityId;
        if (md.SsoUrl != null) options.IdpSsoUrl = md.SsoUrl;
        if (md.SloUrl != null) options.IdpSloUrl = md.SloUrl;
        if (md.Certificates.Count > 0) options.IdpCertificates = md.Certificates;
        return md;
    }

    public async Task<X509Certificate2?> SigningCertificateAsync()
    {
        await RequireAdminAsync();
        return await store.GetSigningCertificateAsync();
    }

    public async Task<X509Certificate2> GenerateSigningKeyAsync()
    {
        var me = await RequireAdminAsync();
        var cert = await store.GenerateSigningCertificateAsync(me);
        await audit.RecordAsync("admin.saml_signing_key_generated", details: new { thumbprint = cert.Thumbprint }, actorId: me);
        return cert;
    }

    /// <summary>Splits a text box of certificates: PEM blocks, or base64 bodies separated by blank lines.</summary>
    public static List<string> SplitCertificates(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        var pems = PemBlock().Matches(text).Select(m => m.Value).ToList();
        if (pems.Count > 0) return pems;
        return Regex.Split(text.Trim(), @"\r?\n\s*\r?\n").Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
    }

    public static List<string> SplitList(string? text) =>
        (text ?? "").Split([',', '\n', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    private static void Normalize(SamlOptions o)
    {
        o.DisplayName = string.IsNullOrWhiteSpace(o.DisplayName) ? "SAML" : o.DisplayName.Trim();
        o.IdpEntityId = o.IdpEntityId?.Trim() ?? "";
        o.IdpSsoUrl = o.IdpSsoUrl?.Trim() ?? "";
        o.IdpSloUrl = string.IsNullOrWhiteSpace(o.IdpSloUrl) ? null : o.IdpSloUrl.Trim();
        o.IdpMetadataUrl = string.IsNullOrWhiteSpace(o.IdpMetadataUrl) ? null : o.IdpMetadataUrl.Trim();
        o.SpEntityId = string.IsNullOrWhiteSpace(o.SpEntityId) ? null : o.SpEntityId.Trim();
        o.BaseUrl = string.IsNullOrWhiteSpace(o.BaseUrl) ? null : o.BaseUrl.Trim();
        o.ClockSkewSeconds = Math.Clamp(o.ClockSkewSeconds, 0, 600);
        o.MaxResponseKilobytes = Math.Clamp(o.MaxResponseKilobytes, 16, 4096);
        o.AllowedEmailDomains = o.AllowedEmailDomains.Select(d => d.Trim().TrimStart('@').ToLowerInvariant()).Where(d => d.Length > 0).Distinct().ToList();
    }

    private async Task<int> RequireAdminAsync()
    {
        if (await access.ResolveUserIdAsync() is not { } me || !await access.IsAdminAsync(me))
            throw new ForbiddenException("Only site administrators can change the SAML configuration.");
        return me;
    }

    [GeneratedRegex("-----BEGIN CERTIFICATE-----[\\s\\S]*?-----END CERTIFICATE-----")]
    private static partial Regex PemBlock();
}
