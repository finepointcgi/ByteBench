using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using ByteBench.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace ByteBench.Services.Saml;

/// <summary>
/// The effective SAML configuration: the copy an admin saved on <c>/admin/authentication</c> (a <see cref="SiteSetting"/>),
/// else <c>Saml:*</c> from appsettings. Also keeps the optional SP signing key, encrypted with Data Protection. Cached
/// for a few seconds (singleton); a save clears the cache.
/// </summary>
public sealed class SamlConfigStore(IDbContextFactory<AppDbContext> dbFactory, IConfiguration configuration, IDataProtectionProvider dataProtection,
    ILogger<SamlConfigStore> log)
{
    public const string ConfigKey = "saml.config";
    public const string SigningKeyKey = "saml.sp_signing_key";
    // Stored identifier: the purpose string must stay the same, or the saved key becomes unreadable.
    private const string KeyPurpose = "ByteBench.Saml.SpSigningKey.v1";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = false };
    private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(15);

    private (SamlOptions Options, bool Saved, DateTime At)? cached;
    private readonly SemaphoreSlim gate = new(1, 1);

    /// <summary>A copy of the effective configuration, and whether it is the admin-saved one.</summary>
    public async Task<(SamlOptions Options, bool Saved)> GetAsync(CancellationToken ct = default)
    {
        if (cached is { } c && DateTime.UtcNow - c.At < CacheFor) return (c.Options.Clone(), c.Saved);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var json = await db.SiteSettings.AsNoTracking().Where(s => s.Key == ConfigKey).Select(s => s.Value).FirstOrDefaultAsync(ct);
        SamlOptions? options = null;
        if (json != null)
        {
            try { options = JsonSerializer.Deserialize<SamlOptions>(json, Json); }
            catch (JsonException e) { log.LogError(e, "The saved SAML configuration can't be read; using Saml:* from the configuration"); }
        }
        var saved = options != null;
        options ??= configuration.GetSection(SamlOptions.Section).Get<SamlOptions>() ?? new SamlOptions();
        cached = (options, saved, DateTime.UtcNow);
        return (options.Clone(), saved);
    }

    public async Task<SamlOptions> CurrentAsync(CancellationToken ct = default) => (await GetAsync(ct)).Options;

    public async Task SaveAsync(SamlOptions options, int? userId, CancellationToken ct = default)
    {
        await SetAsync(ConfigKey, JsonSerializer.Serialize(options, Json), userId, ct);
        cached = null;
    }

    /// <summary>Forgets the saved copy: <c>Saml:*</c> from appsettings applies again.</summary>
    public async Task ResetAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.SiteSettings.Where(s => s.Key == ConfigKey).ExecuteDeleteAsync(ct);
        cached = null;
    }

    // ------------------------------------------------------------------ SP signing key

    /// <summary>The SP signing certificate with its private key, or null when none was generated.</summary>
    public async Task<X509Certificate2?> GetSigningCertificateAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var stored = await db.SiteSettings.AsNoTracking().Where(s => s.Key == SigningKeyKey).Select(s => s.Value).FirstOrDefaultAsync(ct);
        if (stored == null) return null;
        try
        {
            var parts = dataProtection.CreateProtector(KeyPurpose).Unprotect(stored).Split('.');
            using var cert = X509CertificateLoader.LoadCertificate(Convert.FromBase64String(parts[0]));
            using var rsa = RSA.Create();
            rsa.ImportPkcs8PrivateKey(Convert.FromBase64String(parts[1]), out _);
            return cert.CopyWithPrivateKey(rsa);
        }
        catch (Exception e) when (e is CryptographicException or FormatException or IndexOutOfRangeException)
        {
            log.LogError("The SAML SP signing key can't be decrypted (were the Data Protection keys lost?); generate a new one");
            return null;
        }
    }

    /// <summary>Generates a new RSA-2048 self-signed SP signing certificate (10 years) and stores it encrypted, replacing any old one.</summary>
    public async Task<X509Certificate2> GenerateSigningCertificateAsync(int? userId, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            using var rsa = RSA.Create(2048);
            var req = new CertificateRequest("CN=ByteBench SAML SP", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: true));
            var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(10));
            // "<certificate DER>.<PKCS#8 private key>", both base64, encrypted as one Data Protection payload.
            var payload = Convert.ToBase64String(cert.RawData) + "." + Convert.ToBase64String(rsa.ExportPkcs8PrivateKey());
            await SetAsync(SigningKeyKey, dataProtection.CreateProtector(KeyPurpose).Protect(payload), userId, ct);
            return cert;
        }
        finally { gate.Release(); }
    }

    private Task SetAsync(string key, string value, int? userId, CancellationToken ct) =>
        new Accounts.SiteSettings(dbFactory).SetAsync(key, value, userId, ct);
}
