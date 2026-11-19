using System.Security.Cryptography;
using ByteBench.Data;
using Microsoft.AspNetCore.DataProtection;

namespace ByteBench.Services.Webhooks;

/// <summary>
/// Encrypts webhook signing secrets at rest with ASP.NET Core Data Protection. Stored values carry the <see cref="Prefix"/>
/// marker; a value without it is a legacy plaintext secret, which <see cref="Reveal"/> still accepts and
/// <see cref="EncryptLegacy"/> re-encrypts at startup. Losing the Data Protection key ring makes stored secrets
/// unreadable: deliveries then fail with a clear error until someone rotates the secret.
/// </summary>
public sealed class WebhookSecrets(IDataProtectionProvider provider)
{
    // Stored identifier: keep "GitBench" (the product was renamed to ByteBench). Changing it would make existing encrypted data/tokens unreadable.
    public const string Purpose = "GitBench.Webhooks.Secret.v1";
    public const string Prefix = "dp:";

    private readonly IDataProtector protector = provider.CreateProtector(Purpose);

    public static bool IsProtected(string stored) => stored.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>The value to store for <paramref name="secret"/>.</summary>
    public string Protect(string secret) => Prefix + protector.Protect(secret);

    /// <summary>The plaintext secret. Throws <see cref="CryptographicException"/> if it can't be decrypted (e.g. the keys are gone).</summary>
    public string Reveal(string stored) => IsProtected(stored) ? protector.Unprotect(stored[Prefix.Length..]) : stored;

    /// <summary>"••••••••3f9a" for a stored value, or a hint to rotate it when it can't be decrypted.</summary>
    public string Mask(string stored)
    {
        try { return WebhookService.Mask(Reveal(stored)); }
        catch (CryptographicException) { return "(unreadable: rotate it)"; }
    }

    /// <summary>Startup migration: encrypts every secret still stored as plaintext. Returns how many were changed.</summary>
    public int EncryptLegacy(AppDbContext db)
    {
        var legacy = db.Webhooks.Where(h => !h.Secret.StartsWith(Prefix)).ToList();
        foreach (var h in legacy) h.Secret = Protect(h.Secret);
        if (legacy.Count > 0) db.SaveChanges();
        return legacy.Count;
    }
}
