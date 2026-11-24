using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ByteBench.Api;
using ByteBench.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ByteBench.Services.Push;

/// <summary>
/// The server's VAPID key pair (RFC 8292): an ECDSA P-256 key generated on first use and kept in two site settings, the
/// public key as base64url and the private key (PKCS#8) encrypted with Data Protection. Browsers subscribe with the public
/// key (<c>GET /push/vapid-public-key</c>); every push request carries an ES256 JWT signed with the private key.
/// </summary>
public sealed class VapidKeys(IDbContextFactory<AppDbContext> dbFactory, IDataProtectionProvider dataProtection,
    IOptionsMonitor<PushOptions> options, PublicBaseUrl publicBaseUrl, ILogger<VapidKeys> log)
{
    public const string PublicKeySetting = "Push.VapidPublicKey";
    public const string PrivateKeySetting = "Push.VapidPrivateKey";
    // Stored identifier: keep "GitBench" (the product was renamed to ByteBench). Changing it would make existing encrypted data/tokens unreadable.
    public const string Purpose = "GitBench.Push.VapidPrivateKey.v1";
    /// <summary>JWT lifetime (RFC 8292 allows at most 24 hours).</summary>
    public static readonly TimeSpan TokenLifetime = TimeSpan.FromHours(12);

    private readonly SemaphoreSlim gate = new(1, 1);
    private (byte[] Public, ECDsa Private)? cached;
    private string? subject;

    /// <summary>The 65-byte public key, base64url (the <c>applicationServerKey</c> browsers subscribe with).</summary>
    public async Task<string> PublicKeyAsync(CancellationToken ct = default) => WebPushCrypto.Base64Url((await GetAsync(ct)).Public);

    /// <summary>The <c>Authorization: vapid t=…, k=…</c> header value for a push to <paramref name="endpoint"/>.</summary>
    public async Task<string> AuthorizationAsync(Uri endpoint, DateTimeOffset now, CancellationToken ct = default)
    {
        var (pub, key) = await GetAsync(ct);
        var jwt = CreateJwt(key, endpoint.GetLeftPart(UriPartial.Authority), now + TokenLifetime, await SubjectAsync(ct));
        return $"vapid t={jwt}, k={WebPushCrypto.Base64Url(pub)}";
    }

    /// <summary>An ES256 JWT with <c>aud</c> (the push service origin), <c>exp</c> and <c>sub</c>, signed (IEEE P1363 r|s) with <paramref name="key"/>.</summary>
    public static string CreateJwt(ECDsa key, string audience, DateTimeOffset expires, string subject)
    {
        var header = WebPushCrypto.Base64Url(Encoding.UTF8.GetBytes("{\"typ\":\"JWT\",\"alg\":\"ES256\"}"));
        var claims = WebPushCrypto.Base64Url(JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
        {
            ["aud"] = audience, ["exp"] = expires.ToUnixTimeSeconds(), ["sub"] = subject,
        }));
        var signingInput = header + "." + claims;
        var signature = key.SignData(Encoding.ASCII.GetBytes(signingInput), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return signingInput + "." + WebPushCrypto.Base64Url(signature);
    }

    private async Task<string> SubjectAsync(CancellationToken ct)
    {
        if (options.CurrentValue.Subject is { Length: > 0 } configured) return configured;
        if (subject != null) return subject;
        if (publicBaseUrl.Value is { } b && b.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return subject = b;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var admin = await db.Users.Where(u => u.IsAdmin && u.Email != null).OrderBy(u => u.Id).Select(u => u.Email).FirstOrDefaultAsync(ct);
        return subject = "mailto:" + (admin ?? "admin@localhost");
    }

    private async Task<(byte[] Public, ECDsa Private)> GetAsync(CancellationToken ct)
    {
        if (cached is { } c) return c;
        await gate.WaitAsync(ct);
        try
        {
            if (cached is { } again) return again;
            var protector = dataProtection.CreateProtector(Purpose);
            for (var attempt = 0; ; attempt++)
            {
                await using var db = await dbFactory.CreateDbContextAsync(ct);
                var rows = await db.SiteSettings.AsNoTracking().Where(s => s.Key == PublicKeySetting || s.Key == PrivateKeySetting)
                    .ToDictionaryAsync(s => s.Key, s => s.Value, ct);
                if (rows.TryGetValue(PublicKeySetting, out var pubText) && rows.TryGetValue(PrivateKeySetting, out var privText))
                {
                    var key = ECDsa.Create();
                    key.ImportPkcs8PrivateKey(Convert.FromBase64String(protector.Unprotect(privText)), out _);
                    cached = (WebPushCrypto.FromBase64Url(pubText), key);
                    return cached.Value;
                }

                // First use: generate and store. Two processes racing both insert; the loser's unique key fails and it reads the winner's.
                using var fresh = ECDsa.Create(ECCurve.NamedCurves.nistP256);
                var q = fresh.ExportParameters(false).Q;
                var pub = new byte[65];
                pub[0] = 0x04; q.X!.CopyTo(pub, 1); q.Y!.CopyTo(pub, 33);
                db.SiteSettings.Add(new SiteSetting { Key = PublicKeySetting, Value = WebPushCrypto.Base64Url(pub) });
                db.SiteSettings.Add(new SiteSetting { Key = PrivateKeySetting, Value = protector.Protect(Convert.ToBase64String(fresh.ExportPkcs8PrivateKey())) });
                try
                {
                    await db.SaveChangesAsync(ct);
                    log.LogInformation("Generated the VAPID key pair for browser push");
                }
                catch (DbUpdateException) when (attempt < 2) { /* another process stored one first: read it */ }
            }
        }
        finally { gate.Release(); }
    }
}
