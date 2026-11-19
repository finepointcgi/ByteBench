using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using ByteBench.Data;
using Microsoft.EntityFrameworkCore;

namespace ByteBench.Services.Flags;

/// <summary>SDK keys: one read-only credential per environment and kind, hashed at rest, rotatable and revocable.</summary>
public partial class FlagService
{
    public const string SdkKeyPrefix = "gbsdk_";
    private const int DisplayPrefixLength = 12; // "gbsdk_c_" + 4 characters

    public static byte[] HashKey(string key) => SHA256.HashData(Encoding.UTF8.GetBytes(key));

    private static string NewSecret(SdkKeyKind kind) =>
        SdkKeyPrefix + (kind == SdkKeyKind.Server ? "s_" : "c_") + Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(24));

    /// <summary>The project's SDK keys (never the secrets), Maintainer+.</summary>
    public async Task<List<FlagSdkKey>> ListKeysAsync(int projectId)
    {
        if (!await access.CanAsync(projectId, Permission.ManageFlags)) return [];
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.FlagSdkKeys.AsNoTracking().Include(k => k.Environment)
            .Where(k => k.Environment.ProjectId == projectId).OrderBy(k => k.Environment.Name).ThenBy(k => k.Id).ToListAsync();
    }

    /// <summary>Creates a key for an environment (Maintainer+). The plaintext is returned once and never stored.</summary>
    public async Task<(FlagSdkKey Key, string Plaintext)> CreateKeyAsync(int projectId, int environmentId, string name, SdkKeyKind kind)
    {
        await access.RequireAsync(projectId, Permission.ManageFlags);
        name = name.Trim();
        if (name.Length is 0 or > 100) throw new InvalidOperationException("The name is required (up to 100 characters).");
        await using var db = await dbFactory.CreateDbContextAsync();
        var env = await db.FlagEnvironments.FirstOrDefaultAsync(e => e.Id == environmentId && e.ProjectId == projectId)
            ?? throw new InvalidOperationException("That environment no longer exists.");
        if (await db.FlagSdkKeys.CountAsync(k => k.EnvironmentId == environmentId && k.RevokedAt == null) >= 20)
            throw new InvalidOperationException("An environment can have at most 20 active SDK keys. Revoke one first.");
        var secret = NewSecret(kind);
        var key = new FlagSdkKey
        {
            EnvironmentId = env.Id, Name = name, Kind = kind, KeyHash = HashKey(secret), Prefix = secret[..DisplayPrefixLength],
            CreatedById = await access.ResolveUserIdAsync(),
        };
        db.FlagSdkKeys.Add(key);
        await db.SaveChangesAsync();
        await audit.RecordAsync("flag.sdk_key_created", "flag_sdk_key", key.Id, projectId, new { name, kind = kind.ToString(), environment = env.Name, prefix = key.Prefix });
        return (key, secret);
    }

    /// <summary>Replaces the key's secret (Maintainer+); the old secret stops working immediately.</summary>
    public async Task<(FlagSdkKey Key, string Plaintext)> RotateKeyAsync(int projectId, int keyId)
    {
        await access.RequireAsync(projectId, Permission.ManageFlags);
        await using var db = await dbFactory.CreateDbContextAsync();
        var key = await db.FlagSdkKeys.Include(k => k.Environment).FirstOrDefaultAsync(k => k.Id == keyId && k.Environment.ProjectId == projectId)
            ?? throw new InvalidOperationException("That SDK key no longer exists.");
        if (!key.IsActive) throw new InvalidOperationException("A revoked key can't be rotated; create a new one.");
        var secret = NewSecret(key.Kind);
        key.KeyHash = HashKey(secret);
        key.Prefix = secret[..DisplayPrefixLength];
        key.RotatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        await audit.RecordAsync("flag.sdk_key_rotated", "flag_sdk_key", key.Id, projectId, new { name = key.Name, environment = key.Environment.Name, prefix = key.Prefix });
        return (key, secret);
    }

    public async Task RevokeKeyAsync(int projectId, int keyId)
    {
        await access.RequireAsync(projectId, Permission.ManageFlags);
        await using var db = await dbFactory.CreateDbContextAsync();
        var key = await db.FlagSdkKeys.Include(k => k.Environment).FirstOrDefaultAsync(k => k.Id == keyId && k.Environment.ProjectId == projectId)
            ?? throw new InvalidOperationException("That SDK key no longer exists.");
        if (!key.IsActive) return;
        key.RevokedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        await audit.RecordAsync("flag.sdk_key_revoked", "flag_sdk_key", key.Id, projectId, new { name = key.Name, environment = key.Environment.Name, prefix = key.Prefix });
    }
}
