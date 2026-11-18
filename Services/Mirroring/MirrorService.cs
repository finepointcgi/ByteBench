using GitBench.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GitBench.Services.Mirroring;

/// <summary>What the mirroring settings page submits. A blank <see cref="Secret"/> keeps the saved one.</summary>
public sealed class MirrorInput
{
    public string Url { get; set; } = "";
    public string? Username { get; set; }
    public string? Secret { get; set; }
    /// <summary>Removes the saved password or token.</summary>
    public bool ClearSecret { get; set; }
    public int IntervalMinutes { get; set; } = 60;
    public bool OnlyProtectedBranches { get; set; }
    public bool Enabled { get; set; } = true;
}

/// <summary>
/// Pull and push mirrors of a repository (<see cref="RepositoryMirror"/>). Everything here needs
/// <see cref="Permission.ManageRepositories"/> (Maintainer+) and is audited (<c>mirror.*</c>). Syncs run in
/// <see cref="MirrorSyncRunner"/>: pull mirrors on their interval and push mirrors after each push (both from
/// <see cref="MirrorWorker"/>), and either on demand with <see cref="SyncNowAsync"/>.
/// </summary>
public class MirrorService(IDbContextFactory<AppDbContext> dbFactory, ProjectAccess access, Audit.AuditLog audit,
    MirrorUrlGuard guard, MirrorCredentials credentials, MirrorSyncRunner runner, IOptionsMonitor<MirrorOptions> options)
{
    public const int MaxPushMirrors = 10;
    public const int MaxIntervalMinutes = 7 * 24 * 60;
    public const string PullMirrorReadOnlyMessage =
        "this repository is a pull mirror and follows its upstream repository: push there instead (a Maintainer can turn mirroring off in the repository settings)";

    /// <summary>True when the repository has an enabled pull mirror (pushes and tag changes are refused).</summary>
    public static Task<bool> IsPullMirrorAsync(AppDbContext db, int repositoryId) =>
        db.RepositoryMirrors.AnyAsync(m => m.RepositoryId == repositoryId && m.Direction == MirrorDirection.Pull && m.Enabled);

    public int MinIntervalMinutes => Math.Max(1, options.CurrentValue.MinIntervalMinutes);

    public bool IsRunning(int mirrorId) => runner.IsRunning(mirrorId);

    /// <summary>The repository's mirrors (pull first), for Maintainers.</summary>
    public async Task<List<RepositoryMirror>> ListAsync(int repositoryId, int? actorId = null)
    {
        await access.RequireForRepositoryAsync(repositoryId, Permission.ManageRepositories, actorId);
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.RepositoryMirrors.AsNoTracking().Include(m => m.CreatedBy)
            .Where(m => m.RepositoryId == repositoryId).OrderBy(m => m.Direction).ThenBy(m => m.Id).ToListAsync();
    }

    /// <summary>Creates or updates the pull mirror (there is at most one).</summary>
    public Task<RepositoryMirror> SavePullAsync(int repositoryId, MirrorInput input, int? actorId = null) =>
        SaveAsync(repositoryId, null, MirrorDirection.Pull, input, actorId);

    /// <summary>Adds a push mirror (<paramref name="mirrorId"/> null) or updates one.</summary>
    public Task<RepositoryMirror> SavePushAsync(int repositoryId, int? mirrorId, MirrorInput input, int? actorId = null) =>
        SaveAsync(repositoryId, mirrorId, MirrorDirection.Push, input, actorId);

    private async Task<RepositoryMirror> SaveAsync(int repositoryId, int? mirrorId, MirrorDirection direction, MirrorInput input, int? actorId)
    {
        await access.RequireForRepositoryAsync(repositoryId, Permission.ManageRepositories, actorId);
        var uid = await access.ResolveUserIdAsync(actorId);
        var url = (input.Url ?? "").Trim();
        if (guard.Validate(url) is { } invalid) throw new InvalidOperationException(invalid);
        var username = string.IsNullOrWhiteSpace(input.Username) ? null : input.Username.Trim();
        if (username?.Length > 200 || username?.Any(char.IsControl) == true) throw new InvalidOperationException("The username isn't valid.");
        var secret = string.IsNullOrEmpty(input.Secret) ? null : input.Secret.Trim();
        if (secret?.Length > 1000 || secret?.Any(char.IsControl) == true) throw new InvalidOperationException("The password or token isn't valid.");
        if (direction == MirrorDirection.Pull && (input.IntervalMinutes < MinIntervalMinutes || input.IntervalMinutes > MaxIntervalMinutes))
            throw new InvalidOperationException($"The interval must be between {MinIntervalMinutes} minutes and {MaxIntervalMinutes / 1440} days.");

        await using var db = await dbFactory.CreateDbContextAsync();
        var repo = await db.Repositories.AsNoTracking().FirstAsync(r => r.Id == repositoryId);
        RepositoryMirror? mirror = direction == MirrorDirection.Pull
            ? await db.RepositoryMirrors.FirstOrDefaultAsync(m => m.RepositoryId == repositoryId && m.Direction == MirrorDirection.Pull)
            : mirrorId == null ? null
            : await db.RepositoryMirrors.FirstOrDefaultAsync(m => m.Id == mirrorId && m.RepositoryId == repositoryId && m.Direction == MirrorDirection.Push)
              ?? throw new InvalidOperationException("Mirror not found.");
        var created = mirror == null;
        if (mirror == null)
        {
            if (direction == MirrorDirection.Push && await db.RepositoryMirrors.CountAsync(m => m.RepositoryId == repositoryId && m.Direction == MirrorDirection.Push) >= MaxPushMirrors)
                throw new InvalidOperationException($"A repository can have at most {MaxPushMirrors} push mirrors.");
            mirror = new RepositoryMirror { RepositoryId = repositoryId, Direction = direction, CreatedById = uid };
            db.RepositoryMirrors.Add(mirror);
        }
        var urlChanged = mirror.Url != url;
        mirror.Url = url;
        mirror.Username = username;
        if (secret != null) mirror.ProtectedSecret = credentials.Protect(secret);
        else if (input.ClearSecret || (urlChanged && !created)) mirror.ProtectedSecret = null; // a saved token never follows the URL elsewhere
        mirror.IntervalMinutes = direction == MirrorDirection.Pull ? input.IntervalMinutes : mirror.IntervalMinutes;
        mirror.OnlyProtectedBranches = input.OnlyProtectedBranches;
        mirror.Enabled = input.Enabled;
        mirror.UpdatedAt = DateTime.UtcNow;
        if (direction == MirrorDirection.Pull && (created || urlChanged || mirror.NextSyncAt == null)) mirror.NextSyncAt = DateTime.UtcNow; // sync soon
        if (urlChanged) { mirror.LastStatus = MirrorSyncStatus.Never; mirror.LastMessage = null; }
        await db.SaveChangesAsync();

        await audit.RecordAsync(created ? "mirror.created" : "mirror.updated", "repository_mirror", mirror.Id, repo.ProjectId,
            new
            {
                repository = repo.Slug, direction = direction.ToString(), url, username, credentials = mirror.ProtectedSecret != null,
                interval = direction == MirrorDirection.Pull ? (int?)mirror.IntervalMinutes : null, onlyProtected = mirror.OnlyProtectedBranches, mirror.Enabled,
            }, actorId);
        return mirror;
    }

    public async Task DeleteAsync(int repositoryId, int mirrorId, int? actorId = null)
    {
        await access.RequireForRepositoryAsync(repositoryId, Permission.ManageRepositories, actorId);
        await using var db = await dbFactory.CreateDbContextAsync();
        var gone = await db.RepositoryMirrors.Where(m => m.Id == mirrorId && m.RepositoryId == repositoryId)
            .Select(m => new { m.Direction, m.Url, m.Repository.ProjectId, m.Repository.Slug }).FirstOrDefaultAsync();
        if (gone == null) return;
        await db.RepositoryMirrors.Where(m => m.Id == mirrorId).ExecuteDeleteAsync();
        await audit.RecordAsync("mirror.deleted", "repository_mirror", mirrorId, gone.ProjectId,
            new { repository = gone.Slug, direction = gone.Direction.ToString(), url = gone.Url }, actorId);
    }

    /// <summary>
    /// "Sync now": runs the mirror right away and returns the result, or null when a sync of it is already running.
    /// Works for disabled mirrors too (a one-off sync).
    /// </summary>
    public async Task<MirrorSyncResult?> SyncNowAsync(int repositoryId, int mirrorId, int? actorId = null, CancellationToken ct = default)
    {
        await access.RequireForRepositoryAsync(repositoryId, Permission.ManageRepositories, actorId);
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            var m = await db.RepositoryMirrors.Where(x => x.Id == mirrorId && x.RepositoryId == repositoryId)
                .Select(x => new { x.Direction, x.Url, x.Repository.ProjectId, x.Repository.Slug }).FirstOrDefaultAsync(ct)
                ?? throw new InvalidOperationException("Mirror not found.");
            await audit.RecordAsync("mirror.sync_requested", "repository_mirror", mirrorId, m.ProjectId,
                new { repository = m.Slug, direction = m.Direction.ToString(), url = m.Url }, actorId);
        }
        return await runner.SyncAsync(mirrorId, wait: false, ct);
    }
}
