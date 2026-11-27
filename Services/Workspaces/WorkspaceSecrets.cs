using ByteBench.Data;
using ByteBench.Services.Audit;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace ByteBench.Services.Workspaces;

/// <summary>A secret as the UI and API see it: its name, never its value.</summary>
public sealed record WorkspaceSecretInfo(int Id, string Name, DateTime UpdatedAt);

/// <summary>
/// Encrypts and decrypts workspace secrets (ASP.NET Core Data Protection, like CI variables). Only the lifecycle calls
/// <see cref="ResolveAsync"/>; nothing returns a value to a page or an API.
/// </summary>
public sealed class WorkspaceSecretStore(IDbContextFactory<AppDbContext> dbFactory, IDataProtectionProvider dataProtection, ILogger<WorkspaceSecretStore> log)
{
    private readonly IDataProtector protector = dataProtection.CreateProtector("ByteBench.WorkspaceSecrets.v1");

    public string Protect(string value) => protector.Protect(value);

    /// <summary>
    /// The secrets a workspace gets: the repository's (only when <paramref name="includeRepository"/>: the owner can push) overridden by the
    /// owner's own with the same name. A value that can't be decrypted (the key ring changed) is skipped with a log line naming only the secret.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, string>> ResolveAsync(int userId, int repositoryId, bool includeRepository, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var rows = await db.WorkspaceSecrets.AsNoTracking()
            .Where(s => s.UserId == userId || (includeRepository && s.RepositoryId == repositoryId))
            .OrderBy(s => s.UserId == null ? 0 : 1) // repository first, the user's override it
            .ToListAsync(ct);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var s in rows)
        {
            try { result[s.Name] = protector.Unprotect(s.EncryptedValue); }
            catch (System.Security.Cryptography.CryptographicException) { log.LogWarning("Workspace secret {Name} (id {Id}) could not be decrypted and was skipped", s.Name, s.Id); }
        }
        return result;
    }
}

/// <summary>
/// Per-user and per-repository workspace secrets. A user manages their own; repository secrets need Maintainer+
/// (<see cref="Permission.ManageRepositories"/>). Values are encrypted at rest, never shown after saving, never logged and not in audit entries
/// (only the name). They reach a workspace as environment variables (see <see cref="WorkspaceLifecycle"/>).
/// </summary>
public sealed class WorkspaceSecretService(IDbContextFactory<AppDbContext> dbFactory, WorkspaceSecretStore store, ProjectAccess access, AuditLog audit)
{
    public const int MaxValueLength = 8000;
    public const int MaxPerScope = 50;

    /// <summary>Null if <paramref name="name"/> can be used as a secret name, else why not.</summary>
    public static string? ValidateName(string name) => WorkspaceEnvFile.ValidateEnvName(name);

    private static void ValidateValue(string? value, bool required)
    {
        if (string.IsNullOrEmpty(value)) { if (required) throw new InvalidOperationException("Enter a value."); return; }
        if (value.Length > MaxValueLength) throw new InvalidOperationException($"The value is too long (max {MaxValueLength} characters).");
        if (value.Contains('\0')) throw new InvalidOperationException("The value can't contain a NUL character.");
    }

    // ----- the user's own -----

    public async Task<List<WorkspaceSecretInfo>> ListMineAsync(int userId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.WorkspaceSecrets.AsNoTracking().Where(s => s.UserId == userId).OrderBy(s => s.Name)
            .Select(s => new WorkspaceSecretInfo(s.Id, s.Name, s.UpdatedAt)).ToListAsync();
    }

    /// <summary>Creates or replaces the user's secret named <paramref name="name"/>.</summary>
    public async Task SaveMineAsync(int userId, string name, string? value)
    {
        name = (name ?? "").Trim();
        if (ValidateName(name) is { } bad) throw new InvalidOperationException(bad);
        await using var db = await dbFactory.CreateDbContextAsync();
        var row = await db.WorkspaceSecrets.FirstOrDefaultAsync(s => s.UserId == userId && s.Name == name);
        var created = row == null;
        ValidateValue(value, required: created);
        if (created)
        {
            if (await db.WorkspaceSecrets.CountAsync(s => s.UserId == userId) >= MaxPerScope) throw new InvalidOperationException($"You can have up to {MaxPerScope} secrets.");
            db.WorkspaceSecrets.Add(row = new WorkspaceSecret { UserId = userId, Name = name });
        }
        if (!string.IsNullOrEmpty(value)) row!.EncryptedValue = store.Protect(value);
        row!.UpdatedAt = DateTime.UtcNow;
        row.UpdatedById = userId;
        await db.SaveChangesAsync();
        await audit.RecordAsync(created ? "workspace_secret.created" : "workspace_secret.updated", "workspace_secret", row.Id, null, new { name, scope = "user" }, userId);
    }

    public async Task DeleteMineAsync(int userId, int id)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var row = await db.WorkspaceSecrets.FirstOrDefaultAsync(s => s.Id == id && s.UserId == userId);
        if (row == null) return;
        db.WorkspaceSecrets.Remove(row);
        await db.SaveChangesAsync();
        await audit.RecordAsync("workspace_secret.deleted", "workspace_secret", id, null, new { name = row.Name, scope = "user" }, userId);
    }

    // ----- a repository's -----

    private async Task<Repository> RequireManageAsync(AppDbContext db, int repositoryId, int userId)
    {
        var repo = await db.Repositories.AsNoTracking().FirstOrDefaultAsync(r => r.Id == repositoryId) ?? throw new InvalidOperationException("Repository not found.");
        // Not a member: the repository's existence isn't revealed.
        if (await access.GetRoleAsync(repo.ProjectId, userId) == null) throw new InvalidOperationException("Repository not found.");
        if (!await access.CanAsync(repo.ProjectId, Permission.ManageRepositories, userId)) throw new ForbiddenException("Only Maintainers and Owners can manage repository secrets.");
        return repo;
    }

    public async Task<List<WorkspaceSecretInfo>> ListForRepositoryAsync(int repositoryId, int userId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        await RequireManageAsync(db, repositoryId, userId);
        return await db.WorkspaceSecrets.AsNoTracking().Where(s => s.RepositoryId == repositoryId).OrderBy(s => s.Name)
            .Select(s => new WorkspaceSecretInfo(s.Id, s.Name, s.UpdatedAt)).ToListAsync();
    }

    public async Task SaveForRepositoryAsync(int repositoryId, int userId, string name, string? value)
    {
        name = (name ?? "").Trim();
        if (ValidateName(name) is { } bad) throw new InvalidOperationException(bad);
        await using var db = await dbFactory.CreateDbContextAsync();
        var repo = await RequireManageAsync(db, repositoryId, userId);
        var row = await db.WorkspaceSecrets.FirstOrDefaultAsync(s => s.RepositoryId == repositoryId && s.Name == name);
        var created = row == null;
        ValidateValue(value, required: created);
        if (created)
        {
            if (await db.WorkspaceSecrets.CountAsync(s => s.RepositoryId == repositoryId) >= MaxPerScope) throw new InvalidOperationException($"A repository can have up to {MaxPerScope} secrets.");
            db.WorkspaceSecrets.Add(row = new WorkspaceSecret { RepositoryId = repositoryId, Name = name });
        }
        if (!string.IsNullOrEmpty(value)) row!.EncryptedValue = store.Protect(value);
        row!.UpdatedAt = DateTime.UtcNow;
        row.UpdatedById = userId;
        await db.SaveChangesAsync();
        await audit.RecordAsync(created ? "workspace_secret.created" : "workspace_secret.updated", "workspace_secret", row.Id, repo.ProjectId,
            new { name, scope = "repository", repositoryId }, userId);
    }

    public async Task DeleteForRepositoryAsync(int repositoryId, int userId, int id)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var repo = await RequireManageAsync(db, repositoryId, userId);
        var row = await db.WorkspaceSecrets.FirstOrDefaultAsync(s => s.Id == id && s.RepositoryId == repositoryId);
        if (row == null) return;
        db.WorkspaceSecrets.Remove(row);
        await db.SaveChangesAsync();
        await audit.RecordAsync("workspace_secret.deleted", "workspace_secret", id, repo.ProjectId, new { name = row.Name, scope = "repository", repositoryId }, userId);
    }
}

/// <summary>A user's workspace preferences: VS Code settings and a shell snippet.</summary>
public sealed class UserWorkspaceSettingsService(IDbContextFactory<AppDbContext> dbFactory, AuditLog audit)
{
    public const int MaxSettingsChars = 16 * 1024, MaxSnippetChars = 8 * 1024;

    public async Task<UserWorkspaceSettings> GetAsync(int userId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.UserWorkspaceSettings.AsNoTracking().FirstOrDefaultAsync(s => s.UserId == userId) ?? new UserWorkspaceSettings { UserId = userId };
    }

    /// <summary>Null if <paramref name="json"/> is empty or a JSON object (comments and trailing commas allowed, as in settings.json), else why not.</summary>
    public static string? ValidateSettings(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        if (json.Length > MaxSettingsChars) return $"The settings are larger than {MaxSettingsChars / 1024} KB.";
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json, new System.Text.Json.JsonDocumentOptions
            { CommentHandling = System.Text.Json.JsonCommentHandling.Skip, AllowTrailingCommas = true, MaxDepth = 16 });
            return doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object ? null : "The settings must be a JSON object, like { \"editor.fontSize\": 14 }.";
        }
        catch (System.Text.Json.JsonException ex) { return "The settings aren't valid JSON: " + ex.Message; }
    }

    public async Task SaveAsync(int userId, string? settingsJson, string? shellSnippet)
    {
        settingsJson = (settingsJson ?? "").Replace("\r\n", "\n");
        shellSnippet = (shellSnippet ?? "").Replace("\r\n", "\n");
        if (ValidateSettings(settingsJson) is { } bad) throw new InvalidOperationException(bad);
        if (shellSnippet.Length > MaxSnippetChars) throw new InvalidOperationException($"The shell snippet is larger than {MaxSnippetChars / 1024} KB.");
        if (settingsJson.Contains('\0') || shellSnippet.Contains('\0')) throw new InvalidOperationException("NUL characters aren't allowed.");
        await using var db = await dbFactory.CreateDbContextAsync();
        var row = await db.UserWorkspaceSettings.FirstOrDefaultAsync(s => s.UserId == userId);
        if (row == null) db.UserWorkspaceSettings.Add(row = new UserWorkspaceSettings { UserId = userId });
        row.SettingsJson = settingsJson;
        row.ShellSnippet = shellSnippet;
        row.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        await audit.RecordAsync("workspace.user_settings", "user", userId, null, new { settings = settingsJson.Length, snippet = shellSnippet.Length }, userId);
    }
}
