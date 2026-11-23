using ByteBench.Data;
using ByteBench.Services.Audit;
using ByteBench.Services.Git;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ByteBench.Services.DependencyBot;

/// <summary>
/// The permission-checked face of the dependency bot for the settings page: settings, "Check now" and "Create update PR".
/// Everything needs <see cref="Permission.ManageRepositories"/> (Maintainer and Owner).
/// </summary>
public sealed class DependencyBotService(IDbContextFactory<AppDbContext> dbFactory, ProjectAccess access, DependencyBotRunner runner, AuditLog audit,
    IOptionsMonitor<DependencyBotOptions> options, RepoStorage storage)
{
    public const int MaxIgnoreLines = 200;

    public bool WorkerEnabled => options.CurrentValue.Enabled;

    public async Task<DependencyBotSettings> GetAsync(Repository repo)
    {
        await access.RequireAsync(repo.ProjectId, Permission.ManageRepositories);
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.DependencyBotSettings.AsNoTracking().FirstOrDefaultAsync(s => s.RepositoryId == repo.Id)
               ?? new DependencyBotSettings { RepositoryId = repo.Id };
    }

    public static DependencyReport? Report(DependencyBotSettings settings) => DependencyBotRunner.ReadReport(settings.LastReportJson);

    public async Task SaveAsync(Repository repo, DependencyBotInput input, int? actorId = null)
    {
        await access.RequireAsync(repo.ProjectId, Permission.ManageRepositories);
        var ecosystems = input.Ecosystems.Select(e => e.Trim().ToLowerInvariant()).Distinct().ToList();
        if (ecosystems.Count == 0 || ecosystems.Any(e => !Ecosystems.All.Contains(e))) throw new InvalidOperationException("Pick at least one ecosystem (npm, nuget, pip or go).");
        if (input.MaxOpenPullRequests is < 1 or > 50) throw new InvalidOperationException("The open pull request limit must be between 1 and 50.");
        var ignoreLines = (input.IgnoreList ?? "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && l[0] != '#').ToList();
        if (ignoreLines.Count > MaxIgnoreLines) throw new InvalidOperationException($"The ignore list can have at most {MaxIgnoreLines} lines.");
        if ((input.IgnoreList ?? "").Length > 4000) throw new InvalidOperationException("The ignore list is too long.");
        foreach (var rule in IgnoreRule.ParseAll(input.IgnoreList))
            if (rule.Name.Length == 0 || rule.Name.Any(char.IsControl) || (rule.Range is { IsValid: false }))
                throw new InvalidOperationException($"Can't read the ignore rule for '{rule.Name}': use name or name@range, e.g. left-pad@>=2.");

        var target = string.IsNullOrWhiteSpace(input.TargetBranch) ? null : input.TargetBranch.Trim();
        if (target != null && target != repo.DefaultBranch)
        {
            var exists = await Task.Run(() => GitOps.BranchExists(StoragePath(repo), target));
            if (!exists) throw new InvalidOperationException($"Branch '{target}' doesn't exist.");
        }
        var labels = (input.Labels ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        if (labels.Count > 20 || labels.Any(l => l.Length > 50 || l.Any(char.IsControl))) throw new InvalidOperationException("Use at most 20 labels of up to 50 characters.");

        await using var db = await dbFactory.CreateDbContextAsync();
        var reviewers = (input.Reviewers ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(r => r.TrimStart('@')).ToList();
        foreach (var name in reviewers)
        {
            var normalized = name.ToUpperInvariant();
            var ok = await db.Users.AnyAsync(u => u.NormalizedUserName == normalized
                && db.ProjectMembers.Any(m => m.ProjectId == repo.ProjectId && m.UserId == u.Id && m.Role >= ProjectRole.Developer));
            if (!ok) throw new InvalidOperationException($"Reviewer '{name}' must be a project member who can review pull requests.");
        }

        var s = await db.DependencyBotSettings.FirstOrDefaultAsync(x => x.RepositoryId == repo.Id);
        if (s == null) db.DependencyBotSettings.Add(s = new DependencyBotSettings { RepositoryId = repo.Id });
        var wasEnabled = s.Enabled;
        s.Enabled = input.Enabled; s.Schedule = input.Schedule; s.Ecosystems = string.Join(',', ecosystems); s.Strategy = input.Strategy;
        s.Grouped = input.Grouped; s.MaxOpenPullRequests = input.MaxOpenPullRequests;
        s.IgnoreList = string.IsNullOrWhiteSpace(input.IgnoreList) ? null : input.IgnoreList.Trim();
        s.TargetBranch = target == repo.DefaultBranch ? null : target;
        s.Labels = labels.Count == 0 ? null : string.Join(", ", labels);
        s.Reviewers = reviewers.Count == 0 ? null : string.Join(", ", reviewers);
        if (!s.Enabled) s.NextRunAt = null;
        else if (!wasEnabled || s.NextRunAt == null) s.NextRunAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        await audit.RecordAsync("repository.dependency_bot_updated", "repository", repo.Id, repo.ProjectId,
            new { repository = repo.Slug, enabled = s.Enabled, schedule = s.Schedule.ToString().ToLowerInvariant(), ecosystems = s.Ecosystems, strategy = s.Strategy.ToString().ToLowerInvariant(), grouped = s.Grouped }, actorId);
    }

    /// <summary>Checks the repository now (registry calls happen here) and stores the table.</summary>
    public async Task<DependencyReport> CheckNowAsync(Repository repo, CancellationToken ct = default)
    {
        await access.RequireAsync(repo.ProjectId, Permission.ManageRepositories);
        return await runner.CheckAsync(repo.Id, ct);
    }

    /// <summary>Opens the pull request for one row (<see cref="OutdatedItem.Key"/>) of the last check; with <paramref name="itemKey"/> null, for every row that can be updated.</summary>
    public async Task<ApplyResult> CreatePullRequestAsync(Repository repo, string? itemKey, int actorId, CancellationToken ct = default)
    {
        await access.RequireAsync(repo.ProjectId, Permission.ManageRepositories);
        return await runner.ApplyAsync(repo.Id, itemKey == null ? null : [itemKey], actorId, ct);
    }

    private string StoragePath(Repository repo) => storage.PathFor(repo.Project.Key, repo.Slug);
}
