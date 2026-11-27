using System.Text.RegularExpressions;
using ByteBench.Data;
using ByteBench.Services.Audit;
using ByteBench.Services.Git;
using ByteBench.Services.Webhooks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ByteBench.Services.Workspaces;

/// <summary>A repository's prebuild settings with what the server allows, for the settings form and the API.</summary>
public sealed record PrebuildSettingsView(bool Enabled, IReadOnlyList<string> Branches, string BranchesText, int KeepLast, int RetentionDays, bool ServerEnabled, string DefaultBranch);

/// <summary>
/// Prebuilds for the signed-in user: settings (Maintainer+), history and logs (Developer+), "Rebuild now", deleting one and cleaning up
/// (Maintainer+). The building itself is <see cref="WorkspacePrebuildService"/>.
/// </summary>
public sealed partial class WorkspacePrebuildManager(IDbContextFactory<AppDbContext> dbFactory, WorkspacePrebuildService service, ProjectAccess access, RepositoryService repos,
    RepoStorage storage, IOptionsMonitor<WorkspaceOptions> options, AuditLog audit)
{
    public const int MaxBranchPatterns = 20;

    [GeneratedRegex(@"^[A-Za-z0-9*][A-Za-z0-9._/@+*-]{0,199}$")]
    private static partial Regex BranchPattern();

    /// <summary>The branch names or patterns in the settings text (comma, semicolon, space or newline separated).</summary>
    public static List<string> SplitBranches(string? text) =>
        (text ?? "").Split([',', ';', '\n', '\r', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.Ordinal).ToList();

    /// <summary>Whether a push to <paramref name="branch"/> is prebuilt: it matches one of the configured names (<c>*</c> matches any run of characters), or the default branch when none is configured.</summary>
    public static bool BranchMatches(string? configured, string defaultBranch, string branch)
    {
        var patterns = SplitBranches(configured);
        if (patterns.Count == 0) return branch == defaultBranch;
        return patterns.Any(p => p == branch || (p.Contains('*') && Regex.IsMatch(branch, "^" + Regex.Escape(p).Replace("\\*", ".*") + "$")));
    }

    private async Task<Repository> RepoAsync(string key, string slug, int userId)
    {
        access.ActAs(userId);
        return await repos.GetRepositoryAsync(key, slug) ?? throw new InvalidOperationException("Repository not found, or you don't have access to it.");
    }

    public async Task<PrebuildSettingsView> GetSettingsAsync(string key, string slug, int userId)
    {
        var repo = await RepoAsync(key, slug, userId);
        await access.RequireRoleAsync(repo.ProjectId, ProjectRole.Developer, "see the prebuild settings", userId);
        await using var db = await dbFactory.CreateDbContextAsync();
        var s = await db.RepositoryPrebuildSettings.AsNoTracking().FirstOrDefaultAsync(x => x.RepositoryId == repo.Id) ?? new RepositoryPrebuildSettings { RepositoryId = repo.Id };
        return new PrebuildSettingsView(s.Enabled, SplitBranches(s.Branches), s.Branches, s.KeepLast, s.RetentionDays, options.CurrentValue.Prebuilds.Enabled, repo.DefaultBranch);
    }

    public async Task SetSettingsAsync(string key, string slug, bool enabled, string? branches, int keepLast, int retentionDays, int userId)
    {
        var repo = await RepoAsync(key, slug, userId);
        await access.RequireRoleAsync(repo.ProjectId, ProjectRole.Maintainer, "change the prebuild settings", userId);
        var list = SplitBranches(branches);
        if (list.Count > MaxBranchPatterns) throw new InvalidOperationException($"At most {MaxBranchPatterns} branches.");
        if (list.FirstOrDefault(b => !BranchPattern().IsMatch(b) || b.Contains("..")) is { } bad) throw new InvalidOperationException($"'{bad}' isn't a valid branch name.");
        if (keepLast is < 1 or > 20) throw new InvalidOperationException("Keep between 1 and 20 prebuilds per branch.");
        if (retentionDays is < 1 or > 90) throw new InvalidOperationException("Retention must be between 1 and 90 days.");
        var text = string.Join(", ", list);
        if (text.Length > 500) throw new InvalidOperationException("The branch list is too long.");
        await using var db = await dbFactory.CreateDbContextAsync();
        var row = await db.RepositoryPrebuildSettings.FirstOrDefaultAsync(s => s.RepositoryId == repo.Id);
        if (row == null) db.RepositoryPrebuildSettings.Add(row = new RepositoryPrebuildSettings { RepositoryId = repo.Id });
        row.Enabled = enabled;
        row.Branches = text;
        row.KeepLast = keepLast;
        row.RetentionDays = retentionDays;
        row.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        await audit.RecordAsync("workspace.prebuild_settings", "repository", repo.Id, repo.ProjectId, new { enabled, branches = text, keepLast, retentionDays }, userId);
        if (!enabled)
        {
            try { await service.CleanupAsync(repo.Id, CancellationToken.None); } // nothing will use them now: free the disk
            catch (Exception ex) when (ex is not OutOfMemoryException) { /* the periodic cleanup retries */ }
        }
    }

    /// <summary>The newest prebuilds of the repository, without their logs.</summary>
    public async Task<List<WorkspacePrebuild>> ListAsync(string key, string slug, int userId, int take = 50)
    {
        var repo = await RepoAsync(key, slug, userId);
        await access.RequireRoleAsync(repo.ProjectId, ProjectRole.Developer, "see prebuilds", userId);
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.WorkspacePrebuilds.AsNoTracking().Where(p => p.RepositoryId == repo.Id).OrderByDescending(p => p.Id).Take(Math.Clamp(take, 1, 200))
            .Select(p => new WorkspacePrebuild
            {
                Id = p.Id, RepositoryId = p.RepositoryId, ProjectId = p.ProjectId, Ref = p.Ref, CommitSha = p.CommitSha, State = p.State, Trigger = p.Trigger,
                TriggeredById = p.TriggeredById, ImageTag = p.ImageTag, BaseImage = p.BaseImage, ConfigHash = p.ConfigHash, Error = p.Error,
                CreatedAt = p.CreatedAt, StartedAt = p.StartedAt, FinishedAt = p.FinishedAt,
            }).ToListAsync();
    }

    /// <summary>One prebuild with its log.</summary>
    public async Task<WorkspacePrebuild?> GetAsync(string key, string slug, int id, int userId)
    {
        var repo = await RepoAsync(key, slug, userId);
        await access.RequireRoleAsync(repo.ProjectId, ProjectRole.Developer, "see prebuilds", userId);
        await using var db = await dbFactory.CreateDbContextAsync();
        var p = await db.WorkspacePrebuilds.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && x.RepositoryId == repo.Id);
        if (p != null) p.GitTokenHash = null;
        return p;
    }

    /// <summary>"Rebuild now": queues a prebuild of the branch (default: the repository's default branch) at its current tip, even if that commit was built before.</summary>
    public async Task<WorkspacePrebuild> RebuildAsync(string key, string slug, string? branch, int userId)
    {
        var repo = await RepoAsync(key, slug, userId);
        await access.RequireRoleAsync(repo.ProjectId, ProjectRole.Maintainer, "rebuild prebuilds", userId);
        if (!options.CurrentValue.Prebuilds.Enabled) throw new InvalidOperationException("Prebuilds are turned off on this server.");
        await using (var db = await dbFactory.CreateDbContextAsync())
            if (!(await db.RepositoryPrebuildSettings.AsNoTracking().FirstOrDefaultAsync(s => s.RepositoryId == repo.Id))?.Enabled ?? true)
                throw new InvalidOperationException("Prebuilds are off for this repository. Turn them on in the settings first.");
        var name = string.IsNullOrWhiteSpace(branch) ? repo.DefaultBranch : branch.Trim();
        if (!WorkspaceLifecycle.IsSafeRef(name)) throw new InvalidOperationException("That branch name isn't valid.");
        var path = storage.PathFor(repo.Project.Key, repo.Slug);
        var sha = await Task.Run(() => GitOps.ResolveSha(path, "refs/heads/" + name)) ?? throw new InvalidOperationException($"There is no branch '{name}' in this repository.");
        var pb = await service.EnqueueAsync(repo.Id, name, sha, "manual", userId, force: true)
            ?? throw new InvalidOperationException($"{name} has no environment file (.bytebench/workspace.yml), so there is nothing to prebuild.");
        return pb;
    }

    public async Task DeleteAsync(string key, string slug, int id, int userId)
    {
        var repo = await RepoAsync(key, slug, userId);
        await access.RequireRoleAsync(repo.ProjectId, ProjectRole.Maintainer, "delete prebuilds", userId);
        await using (var db = await dbFactory.CreateDbContextAsync())
            if (!await db.WorkspacePrebuilds.AnyAsync(p => p.Id == id && p.RepositoryId == repo.Id)) throw new InvalidOperationException("Prebuild not found.");
        if (!await service.DeleteAsync(id)) throw new InvalidOperationException("A prebuild that is building can't be deleted. Wait for it to finish.");
        await audit.RecordAsync("workspace.prebuild_deleted", "repository", repo.Id, repo.ProjectId, new { id }, userId);
    }

    /// <summary>"Clean up now": applies the keep-last and retention settings immediately.</summary>
    public async Task CleanupAsync(string key, string slug, int userId)
    {
        var repo = await RepoAsync(key, slug, userId);
        await access.RequireRoleAsync(repo.ProjectId, ProjectRole.Maintainer, "clean up prebuilds", userId);
        await service.CleanupAsync(repo.Id, CancellationToken.None);
    }
}

/// <summary>
/// After a push: queues a prebuild for every pushed branch that the repository's prebuild settings name (the default branch unless a list is configured)
/// and whose commit has a workspace environment file. Never fails the push.
/// </summary>
public sealed class PrebuildPushListener(IDbContextFactory<AppDbContext> dbFactory, PushCapture capture, WorkspacePrebuildService prebuilds,
    IOptionsMonitor<WorkspaceOptions> options, ILogger<PrebuildPushListener> log) : IGitPushListener
{
    public async Task OnPushAsync(int repositoryId, int userId, CancellationToken ct)
    {
        try
        {
            if (!options.CurrentValue.Prebuilds.Enabled) return;
            var pushed = capture.Refs.Where(r => r.Ref.StartsWith("refs/heads/", StringComparison.Ordinal) && !r.Deleted).ToList();
            if (pushed.Count == 0) return;
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var settings = await db.RepositoryPrebuildSettings.AsNoTracking().FirstOrDefaultAsync(s => s.RepositoryId == repositoryId, ct);
            if (settings is not { Enabled: true }) return;
            var repo = await db.Repositories.AsNoTracking().Include(r => r.Project).FirstOrDefaultAsync(r => r.Id == repositoryId, ct);
            if (repo == null) return;
            if (await db.SiteSettings.AsNoTracking().Where(s => s.Key == WorkspaceService.EnabledKey).Select(s => s.Value).FirstOrDefaultAsync(ct) == "false") return;
            if ((await db.ProjectWorkspaceSettings.AsNoTracking().FirstOrDefaultAsync(s => s.ProjectId == repo.ProjectId, ct)) is { Enabled: false }) return;

            foreach (var r in pushed)
            {
                var branch = r.Ref["refs/heads/".Length..];
                if (!WorkspacePrebuildManager.BranchMatches(settings.Branches, repo.DefaultBranch, branch)) continue;
                await prebuilds.EnqueueAsync(repositoryId, branch, r.After, "push", userId);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            log.LogWarning(ex, "Queuing prebuilds after a push to repository {Id} failed", repositoryId);
        }
    }
}
