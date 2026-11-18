using GitBench.Data;
using Microsoft.EntityFrameworkCore;

namespace GitBench.Services;

public partial class RepositorySettingsService
{
    /// <summary>Sets which merge strategies pull requests may use and which one the merge box preselects.</summary>
    public async Task SetMergeStrategiesAsync(int repositoryId, MergeStrategies allowed, MergeStrategy defaultStrategy, int? actorId = null)
    {
        await access.RequireForRepositoryAsync(repositoryId, Permission.ManageRepositories, actorId);
        allowed &= MergeStrategies.All;
        if (allowed == MergeStrategies.None) throw new InvalidOperationException("Allow at least one merge strategy.");
        if (!allowed.HasFlag(MergeStrategySettings.Flag(defaultStrategy)))
            throw new InvalidOperationException("The default merge strategy must be one of the allowed strategies.");
        await using var db = await dbFactory.CreateDbContextAsync();
        var repo = await db.Repositories.FirstOrDefaultAsync(r => r.Id == repositoryId) ?? throw new InvalidOperationException("Repository not found.");
        repo.AllowedMergeStrategies = allowed;
        repo.DefaultMergeStrategy = defaultStrategy;
        await db.SaveChangesAsync();
        await audit.RecordAsync("repository.merge_strategies_changed", "repository", repo.Id, repo.ProjectId,
            new { repository = repo.Slug, allowed = MergeStrategySettings.AllowedList(repo).Select(s => s.ToString().ToLowerInvariant()), @default = defaultStrategy.ToString().ToLowerInvariant() }, actorId);
    }
}
