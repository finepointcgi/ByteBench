using ByteBench.Data;
using Microsoft.EntityFrameworkCore;

namespace ByteBench.Services.Ssh;

/// <summary>Who an SSH public key authenticates as: a user (with their project permissions) or one repository's deploy key.</summary>
public sealed record SshIdentity(int KeyId, string Fingerprint, int? UserId, string? UserName,
    int? DeployRepositoryId, bool DeployCanWrite, int? DeployCreatorId, string KeyTitle)
{
    public bool IsDeployKey => DeployRepositoryId != null;

    /// <summary>For logs and rate limits: <c>alice</c> or <c>deploy-key:12</c>.</summary>
    public string Name => IsDeployKey ? $"deploy-key:{KeyId}" : UserName ?? $"user:{UserId}";
}

/// <summary>
/// Users' SSH keys (<c>/account/ssh-keys</c>) and repositories' deploy keys (repository settings → Deploy keys), plus the
/// lookup the SSH server authenticates with. A key's fingerprint is unique across all users and deploy keys.
/// </summary>
public class SshKeyService(IDbContextFactory<AppDbContext> dbFactory, ProjectAccess access, Audit.AuditLog audit)
{
    public const int MaxKeysPerOwner = 100;

    // ----- User keys -----

    public async Task<List<SshKey>> ListUserKeysAsync(int userId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.SshKeys.AsNoTracking().Where(k => k.UserId == userId).OrderByDescending(k => k.CreatedAt).ToListAsync();
    }

    /// <summary>Adds a key to the user's account. Throws <see cref="InvalidOperationException"/> with a user-facing message.</summary>
    public async Task<SshKey> AddUserKeyAsync(int userId, string title, string publicKey, DateTime? expiresAtUtc = null)
    {
        var key = Parse(publicKey);
        title = CleanTitle(title, key);
        if (expiresAtUtc != null && expiresAtUtc <= DateTime.UtcNow) throw new InvalidOperationException("The expiry date must be in the future.");

        await using var db = await dbFactory.CreateDbContextAsync();
        if (await db.SshKeys.CountAsync(k => k.UserId == userId) >= MaxKeysPerOwner)
            throw new InvalidOperationException($"You can have at most {MaxKeysPerOwner} SSH keys.");
        var entity = new SshKey
        {
            UserId = userId, Title = title, KeyType = key.KeyType, PublicKey = key.Base64, Fingerprint = key.Fingerprint,
            CreatedById = userId, ExpiresAt = expiresAtUtc,
        };
        await SaveNewAsync(db, entity);
        await audit.RecordAsync("ssh_key.added", "ssh_key", entity.Id,
            details: new { title, fingerprint = entity.Fingerprint, keyType = entity.KeyType, expiresAt = expiresAtUtc }, actorId: userId);
        return entity;
    }

    public async Task DeleteUserKeyAsync(int userId, int keyId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var key = await db.SshKeys.FirstOrDefaultAsync(k => k.Id == keyId && k.UserId == userId);
        if (key == null) return;
        db.SshKeys.Remove(key);
        await db.SaveChangesAsync();
        await audit.RecordAsync("ssh_key.removed", "ssh_key", keyId, details: new { title = key.Title, fingerprint = key.Fingerprint }, actorId: userId);
    }

    // ----- Deploy keys (Maintainer+) -----

    public async Task<List<SshKey>> ListDeployKeysAsync(int repositoryId, int? actorId = null)
    {
        await access.RequireForRepositoryAsync(repositoryId, Permission.ManageRepositories, actorId);
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.SshKeys.AsNoTracking().Include(k => k.CreatedBy)
            .Where(k => k.RepositoryId == repositoryId).OrderByDescending(k => k.CreatedAt).ToListAsync();
    }

    public async Task<SshKey> AddDeployKeyAsync(int repositoryId, string title, string publicKey, bool canWrite, int? actorId = null)
    {
        await access.RequireForRepositoryAsync(repositoryId, Permission.ManageRepositories, actorId);
        var uid = await access.ResolveUserIdAsync(actorId);
        var key = Parse(publicKey);
        title = CleanTitle(title, key);

        await using var db = await dbFactory.CreateDbContextAsync();
        var repo = await db.Repositories.AsNoTracking().FirstAsync(r => r.Id == repositoryId);
        if (await db.SshKeys.CountAsync(k => k.RepositoryId == repositoryId) >= MaxKeysPerOwner)
            throw new InvalidOperationException($"A repository can have at most {MaxKeysPerOwner} deploy keys.");
        var entity = new SshKey
        {
            RepositoryId = repositoryId, CanWrite = canWrite, Title = title, KeyType = key.KeyType, PublicKey = key.Base64,
            Fingerprint = key.Fingerprint, CreatedById = uid,
        };
        await SaveNewAsync(db, entity);
        await audit.RecordAsync("deploy_key.added", "deploy_key", entity.Id, repo.ProjectId,
            new { repository = repo.Slug, title, fingerprint = entity.Fingerprint, canWrite }, uid);
        return entity;
    }

    public async Task DeleteDeployKeyAsync(int repositoryId, int keyId, int? actorId = null)
    {
        await access.RequireForRepositoryAsync(repositoryId, Permission.ManageRepositories, actorId);
        await using var db = await dbFactory.CreateDbContextAsync();
        var key = await db.SshKeys.Include(k => k.Repository).FirstOrDefaultAsync(k => k.Id == keyId && k.RepositoryId == repositoryId);
        if (key == null) return;
        db.SshKeys.Remove(key);
        await db.SaveChangesAsync();
        await audit.RecordAsync("deploy_key.removed", "deploy_key", keyId, key.Repository!.ProjectId,
            new { repository = key.Repository.Slug, title = key.Title, fingerprint = key.Fingerprint }, await access.ResolveUserIdAsync(actorId));
    }

    // ----- Authentication -----

    /// <summary>
    /// The identity for a presented public key, or null if the key is unknown, expired, or belongs to a locked or
    /// deactivated user. With <paramref name="recordUse"/> (a verified signature, not just a query) Last used is updated.
    /// </summary>
    public async Task<SshIdentity?> AuthenticateAsync(string fingerprint, bool recordUse, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var key = await db.SshKeys.Include(k => k.User).FirstOrDefaultAsync(k => k.Fingerprint == fingerprint, ct);
        if (key == null || key.IsExpired(now)) return null;
        if (key.User is { } user && (user.LockoutEnd > now || user.DeactivatedAt != null)) return null;
        if (key.UserId == null && key.RepositoryId == null) return null;

        if (recordUse && (key.LastUsedAt == null || now - key.LastUsedAt > TimeSpan.FromSeconds(30)))
        {
            key.LastUsedAt = now;
            await db.SaveChangesAsync(ct);
        }
        return new SshIdentity(key.Id, key.Fingerprint, key.UserId, key.User?.UserName, key.RepositoryId, key.CanWrite, key.CreatedById, key.Title);
    }

    // ----- helpers -----

    private static SshPublicKey Parse(string publicKey)
    {
        try { return SshPublicKey.Parse(publicKey); }
        catch (FormatException ex) { throw new InvalidOperationException(ex.Message); }
    }

    /// <summary>The title, else the key's comment (often <c>you@laptop</c>), else its type.</summary>
    private static string CleanTitle(string? title, SshPublicKey key)
    {
        var t = (title ?? "").Trim();
        if (t.Length == 0) t = key.Comment ?? SshPublicKey.ShortType(key.KeyType) + " key";
        return t.Length > 100 ? t[..100] : t;
    }

    private static async Task SaveNewAsync(AppDbContext db, SshKey entity)
    {
        if (await db.SshKeys.AnyAsync(k => k.Fingerprint == entity.Fingerprint)) throw DuplicateKey();
        db.SshKeys.Add(entity);
        try { await db.SaveChangesAsync(); }
        catch (DbUpdateException) { throw DuplicateKey(); } // lost a race with the same key: the unique index caught it
    }

    private static InvalidOperationException DuplicateKey() =>
        new("This key is already in use, by your account, another account or a deploy key. Each SSH key can be added only once; generate a new key for this use.");
}
