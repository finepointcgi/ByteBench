using System.Security.Cryptography;
using System.Text;
using ByteBench.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace ByteBench.Services.Push;

/// <summary>A subscription as the page lists it (no keys, no endpoint).</summary>
public sealed record PushDevice(int Id, string? UserAgent, string Host, DateTime CreatedAt, DateTime? LastUsedAt, int FailureCount);

/// <summary>
/// Users' browser push subscriptions: validation (the endpoint through <see cref="PushEndpointGuard"/>, the keys must be a
/// valid P-256 point and a 16-byte secret), at most <see cref="MaxPerUser"/> per user, keys encrypted with Data Protection,
/// and the per-user "Browser push" settings (on/off and which groups).
/// </summary>
public sealed class PushSubscriptions(IDbContextFactory<AppDbContext> dbFactory, IDataProtectionProvider dataProtection, PushEndpointGuard guard, TimeProvider clock)
{
    public const int MaxPerUser = 10;
    // Stored identifier: keep "GitBench" (the product was renamed to ByteBench). Changing it would make existing encrypted data/tokens unreadable.
    public const string KeyPurpose = "GitBench.Push.SubscriptionKeys.v1";

    private IDataProtector Protector => dataProtection.CreateProtector(KeyPurpose);

    /// <summary>Saves (or refreshes) a subscription for <paramref name="userId"/> and turns browser push on. Returns null or an error message.</summary>
    public async Task<string?> AddAsync(int userId, string? endpoint, string? p256dh, string? auth, string? userAgent, CancellationToken ct = default)
    {
        if (await guard.ValidateResolvedAsync(endpoint, ct) is { } invalid) return invalid;
        byte[] key, secret;
        try { key = WebPushCrypto.FromBase64Url(p256dh ?? ""); secret = WebPushCrypto.FromBase64Url(auth ?? ""); }
        catch (FormatException) { return "The subscription keys aren't valid base64url."; }
        if (secret.Length != 16) return "The subscription's auth secret must be 16 bytes.";
        try { using var _ = WebPushCrypto.ImportPublicKey(key); }
        catch (CryptographicException) { return "The subscription's p256dh key isn't a valid P-256 public key."; }

        var hash = Hash(endpoint!);
        var now = clock.GetUtcNow().UtcDateTime;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var existing = await db.WebPushSubscriptions.FirstOrDefaultAsync(s => s.EndpointHash == hash, ct);
        if (existing != null && existing.UserId != userId)
        {
            // The same browser signed in to another account: it now belongs to this one.
            db.WebPushSubscriptions.Remove(existing);
            existing = null;
        }
        if (existing == null)
        {
            if (await db.WebPushSubscriptions.CountAsync(s => s.UserId == userId, ct) >= MaxPerUser)
                return $"You already have {MaxPerUser} browsers subscribed. Remove one first.";
            db.WebPushSubscriptions.Add(existing = new WebPushSubscription { UserId = userId, Endpoint = endpoint!, EndpointHash = hash, CreatedAt = now });
        }
        existing.EncryptedP256dh = Protector.Protect(WebPushCrypto.Base64Url(key));
        existing.EncryptedAuth = Protector.Protect(WebPushCrypto.Base64Url(secret));
        existing.UserAgent = ShortUserAgent(userAgent);
        existing.FailureCount = 0;
        existing.RetryAfterUntil = null;

        var settings = await db.UserNotificationSettings.FindAsync([userId], ct);
        if (settings == null) db.UserNotificationSettings.Add(new UserNotificationSettings { UserId = userId, PushEnabled = true });
        else settings.PushEnabled = true;
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException) { return "The subscription changed at the same time; try again."; }
        return null;
    }

    /// <summary>Removes the user's subscription with this endpoint (this browser unsubscribed). False if there was none.</summary>
    public async Task<bool> RemoveAsync(int userId, string? endpoint, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(endpoint)) return false;
        var hash = Hash(endpoint);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.WebPushSubscriptions.Where(s => s.UserId == userId && s.EndpointHash == hash).ExecuteDeleteAsync(ct) > 0;
    }

    public async Task<bool> RemoveByIdAsync(int userId, int id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.WebPushSubscriptions.Where(s => s.UserId == userId && s.Id == id).ExecuteDeleteAsync(ct) > 0;
    }

    public async Task<List<PushDevice>> ListAsync(int userId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var rows = await db.WebPushSubscriptions.AsNoTracking().Where(s => s.UserId == userId).OrderBy(s => s.Id).ToListAsync(ct);
        return rows.Select(s => new PushDevice(s.Id, s.UserAgent, Uri.TryCreate(s.Endpoint, UriKind.Absolute, out var u) ? u.Host : "", s.CreatedAt, s.LastUsedAt, s.FailureCount)).ToList();
    }

    /// <summary>The user's browser push switch and groups (defaults when there is no row).</summary>
    public async Task<(bool Enabled, IReadOnlySet<NotificationGroup> Groups)> GetSettingsAsync(int userId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var s = await db.UserNotificationSettings.AsNoTracking().FirstOrDefaultAsync(x => x.UserId == userId, ct);
        return (s?.PushEnabled ?? false, PushGroupMask.ToSet(s?.PushGroups));
    }

    public async Task SaveSettingsAsync(int userId, bool enabled, IEnumerable<NotificationGroup> groups, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var mask = PushGroupMask.ToMask(groups);
        var s = await db.UserNotificationSettings.FindAsync([userId], ct);
        if (s == null) db.UserNotificationSettings.Add(s = new UserNotificationSettings { UserId = userId });
        s.PushEnabled = enabled;
        s.PushGroups = mask == PushGroupMask.ToMask(PushGroupMask.Defaults) ? null : mask;
        if (!enabled)
            await db.Notifications.Where(n => n.UserId == userId && n.PushPending).ExecuteUpdateAsync(x => x.SetProperty(n => n.PushPending, false), ct);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>The decrypted keys of a stored subscription, or null when they can't be read (Data Protection keys lost).</summary>
    internal (byte[] P256dh, byte[] Auth)? Keys(WebPushSubscription s)
    {
        try { return (WebPushCrypto.FromBase64Url(Protector.Unprotect(s.EncryptedP256dh)), WebPushCrypto.FromBase64Url(Protector.Unprotect(s.EncryptedAuth))); }
        catch (CryptographicException) { return null; }
        catch (FormatException) { return null; }
    }

    public static string Hash(string endpoint) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(endpoint)));

    /// <summary>"Firefox on Windows" from a User-Agent header (only browser and OS family are kept).</summary>
    public static string? ShortUserAgent(string? ua)
    {
        if (string.IsNullOrWhiteSpace(ua)) return null;
        var browser = ua.Contains("Edg/") ? "Edge" : ua.Contains("OPR/") ? "Opera" : ua.Contains("Firefox/") ? "Firefox"
            : ua.Contains("Chrome/") || ua.Contains("CriOS/") ? "Chrome" : ua.Contains("Safari/") ? "Safari" : "Browser";
        var os = ua.Contains("iPhone") || ua.Contains("iPad") ? "iOS" : ua.Contains("Android") ? "Android" : ua.Contains("Windows") ? "Windows"
            : ua.Contains("Mac OS X") || ua.Contains("Macintosh") ? "macOS" : ua.Contains("CrOS") ? "ChromeOS" : ua.Contains("Linux") ? "Linux" : null;
        return os == null ? browser : $"{browser} on {os}";
    }
}
