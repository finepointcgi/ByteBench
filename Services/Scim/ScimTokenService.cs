using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using ByteBench.Data;
using Microsoft.EntityFrameworkCore;

namespace ByteBench.Services.Scim;

/// <summary>Configuration section <c>Scim</c>.</summary>
public sealed class ScimOptions
{
    public const string Section = "Scim";
    /// <summary>Largest accepted request body, in KiB.</summary>
    public int MaxBodyKilobytes { get; set; } = 64;
    /// <summary>Failed bearer-token attempts per client IP within <see cref="FailureWindowMinutes"/> before it gets 429s.</summary>
    public int MaxFailuresPerIp { get; set; } = 10;
    public int FailureWindowMinutes { get; set; } = 15;
    /// <summary>Let SCIM change or deactivate site administrators. Off: SCIM writes to an admin account are refused (403).</summary>
    public bool ManageAdmins { get; set; }
    /// <summary>The page size when the IdP doesn't send <c>count</c>, and the largest one allowed.</summary>
    public int DefaultPageSize { get; set; } = 100;
    public int MaxPageSize { get; set; } = 200;
    /// <summary>Visibility of groups SCIM creates (<c>Private</c> or <c>Internal</c>).</summary>
    public Data.ProjectVisibility GroupVisibility { get; set; } = Data.ProjectVisibility.Private;
    /// <summary>The role SCIM gives the members it adds to a group. Capped at Maintainer: SCIM can never grant Owner.</summary>
    public Data.ProjectRole GroupMemberRole { get; set; } = Data.ProjectRole.Developer;
    /// <summary>
    /// Existing ByteBench groups SCIM may adopt: when the IdP creates a group whose displayName matches <c>DisplayName</c>
    /// (case-insensitive), it takes over the group at <c>Group</c> (full path) instead of creating a new one.
    /// </summary>
    public List<ScimGroupMapping> GroupMappings { get; set; } = [];

    /// <summary><see cref="GroupMemberRole"/> with the cap applied.</summary>
    public Data.ProjectRole EffectiveGroupMemberRole => GroupMemberRole >= Data.ProjectRole.Owner ? Data.ProjectRole.Maintainer : GroupMemberRole;
}

/// <summary>One <c>Scim:GroupMappings</c> entry.</summary>
public sealed class ScimGroupMapping
{
    public string DisplayName { get; set; } = "";
    public string Group { get; set; } = "";
}

/// <summary>
/// The SCIM bearer tokens and the on/off switch (<c>/admin/authentication</c>). Tokens look like <c>bbscim_…</c>, are shown
/// once, and only their SHA-256 is stored; checking one compares hashes in constant time. SCIM answers only when the switch
/// is on and at least one token exists. Managing tokens requires a site admin (<see cref="ProjectAccess"/>).
/// </summary>
public sealed class ScimTokenService(IDbContextFactory<AppDbContext> dbFactory, ProjectAccess access, Audit.AuditLog audit)
{
    public const string EnabledKey = "scim.enabled";
    public const string TokenPrefix = "bbscim_";

    public async Task<bool> IsSwitchedOnAsync(CancellationToken ct = default) =>
        await new Accounts.SiteSettings(dbFactory).GetAsync(EnabledKey, ct) == "true";

    /// <summary>On and usable: the switch is on and an unrevoked token exists.</summary>
    public async Task<bool> IsActiveAsync(CancellationToken ct = default)
    {
        if (!await IsSwitchedOnAsync(ct)) return false;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.ScimTokens.AnyAsync(t => t.RevokedAt == null, ct);
    }

    public async Task SetEnabledAsync(bool enabled)
    {
        var me = await RequireAdminAsync();
        await new Accounts.SiteSettings(dbFactory).SetAsync(EnabledKey, enabled ? "true" : "false", me);
        await audit.RecordAsync(enabled ? "scim.enabled" : "scim.disabled", actorId: me);
    }

    public async Task<List<ScimToken>> ListAsync()
    {
        await RequireAdminAsync();
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.ScimTokens.AsNoTracking().OrderBy(t => t.RevokedAt != null).ThenByDescending(t => t.CreatedAt).ToListAsync();
    }

    /// <summary>Creates a token. Returns the row and the plaintext token (shown once).</summary>
    public async Task<(ScimToken Token, string Plaintext)> CreateAsync(string name)
    {
        var me = await RequireAdminAsync();
        name = string.IsNullOrWhiteSpace(name) ? "SCIM token" : name.Trim();
        if (name.Length > 100) throw new InvalidOperationException("The name can be at most 100 characters.");
        var (token, plaintext) = await AddAsync(name, me);
        await audit.RecordAsync("scim.token_created", "scim_token", token.Id, details: new { token.Name, token.Prefix }, actorId: me);
        return (token, plaintext);
    }

    /// <summary>Revokes the token and creates a replacement with the same name. Returns the new plaintext token.</summary>
    public async Task<(ScimToken Token, string Plaintext)> RotateAsync(int id)
    {
        var me = await RequireAdminAsync();
        await using var db = await dbFactory.CreateDbContextAsync();
        var old = await db.ScimTokens.FirstOrDefaultAsync(t => t.Id == id && t.RevokedAt == null) ?? throw new InvalidOperationException("That token no longer exists or was revoked.");
        old.RevokedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        var (token, plaintext) = await AddAsync(old.Name, me);
        await audit.RecordAsync("scim.token_rotated", "scim_token", token.Id, details: new { token.Name, oldPrefix = old.Prefix, token.Prefix }, actorId: me);
        return (token, plaintext);
    }

    public async Task RevokeAsync(int id)
    {
        var me = await RequireAdminAsync();
        await using var db = await dbFactory.CreateDbContextAsync();
        var token = await db.ScimTokens.FirstOrDefaultAsync(t => t.Id == id && t.RevokedAt == null) ?? throw new InvalidOperationException("That token no longer exists or was revoked.");
        token.RevokedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        await audit.RecordAsync("scim.token_revoked", "scim_token", token.Id, details: new { token.Name, token.Prefix }, actorId: me);
    }

    /// <summary>The unrevoked token <paramref name="plaintext"/> names, or null. No permission check: this is the SCIM API's authentication.</summary>
    public async Task<ScimToken?> AuthenticateAsync(string? plaintext, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(plaintext) || plaintext.Length > 200 || !plaintext.StartsWith(TokenPrefix, StringComparison.Ordinal)) return null;
        var hash = Hash(plaintext);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var token = await db.ScimTokens.FirstOrDefaultAsync(t => t.TokenHash == hash, ct);
        // The lookup is by hash (an index); compare again in constant time so the check never depends on string equality shortcuts.
        if (token == null || !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(token.TokenHash), Encoding.ASCII.GetBytes(hash))) return null;
        if (token.RevokedAt != null) return null;
        if (token.LastUsedAt == null || DateTime.UtcNow - token.LastUsedAt > TimeSpan.FromMinutes(1))
        {
            token.LastUsedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
        }
        return token;
    }

    public static string Hash(string plaintext) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(plaintext))).ToLowerInvariant();

    private async Task<(ScimToken, string)> AddAsync(string name, int me)
    {
        var plaintext = TokenPrefix + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var token = new ScimToken { Name = name, TokenHash = Hash(plaintext), Prefix = plaintext[..12], CreatedById = me };
        await using var db = await dbFactory.CreateDbContextAsync();
        db.ScimTokens.Add(token);
        await db.SaveChangesAsync();
        return (token, plaintext);
    }

    private async Task<int> RequireAdminAsync()
    {
        if (await access.ResolveUserIdAsync() is not { } me || !await access.IsAdminAsync(me))
            throw new ForbiddenException("Only site administrators can manage SCIM provisioning.");
        return me;
    }
}

/// <summary>Failed SCIM token attempts per client IP (in memory, sliding window). Over the limit, the IP gets 429 until old failures age out.</summary>
public sealed class ScimAuthLimiter(TimeProvider clock, Microsoft.Extensions.Options.IOptionsMonitor<ScimOptions> options)
{
    private readonly ConcurrentDictionary<string, Queue<DateTimeOffset>> failures = new();

    private TimeSpan Window => TimeSpan.FromMinutes(Math.Max(1, options.CurrentValue.FailureWindowMinutes));

    public bool IsBlocked(string ip)
    {
        if (!failures.TryGetValue(ip, out var q)) return false;
        lock (q)
        {
            Trim(q);
            return q.Count >= Math.Max(1, options.CurrentValue.MaxFailuresPerIp);
        }
    }

    public void RecordFailure(string ip)
    {
        if (failures.Count > 10_000)
            foreach (var (key, old) in failures) lock (old) { Trim(old); if (old.Count == 0) failures.TryRemove(key, out _); }
        var q = failures.GetOrAdd(ip, _ => new Queue<DateTimeOffset>());
        lock (q)
        {
            Trim(q);
            q.Enqueue(clock.GetUtcNow());
        }
    }

    private void Trim(Queue<DateTimeOffset> q)
    {
        var now = clock.GetUtcNow();
        while (q.Count > 0 && now - q.Peek() >= Window) q.Dequeue();
    }
}
