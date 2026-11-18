using GitBench.Data;
using Microsoft.EntityFrameworkCore;

namespace GitBench.Services.Ci;

/// <summary>Pipelines of pull requests from forks wait for a Maintainer's "Approve and run".</summary>
public partial class PipelineService
{
    /// <summary>Whether the acting user may approve fork PR pipelines in the repository (MergePullRequest: Maintainer+).</summary>
    public Task<bool> CanApproveAsync(Repository repo) => access.CanAsync(repo.ProjectId, Permission.MergePullRequest);

    /// <summary>
    /// "Approve and run": releases one fork PR pipeline that is waiting for approval (Maintainer+). It still gets no CI
    /// variables. Approval covers this pipeline only; the next push to the fork waits again.
    /// </summary>
    public async Task ApproveAsync(Repository repo, int pipelineId)
    {
        await access.RequireAsync(repo.ProjectId, Permission.MergePullRequest);
        EnsureEnabled();
        var userId = await access.ResolveUserIdAsync();
        await using var db = await dbFactory.CreateDbContextAsync();
        var approved = await db.Pipelines
            .Where(p => p.Id == pipelineId && p.RepositoryId == repo.Id && p.AwaitingApproval && p.Status == PipelineStatus.Queued)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.AwaitingApproval, false)
                .SetProperty(p => p.ApprovedById, userId).SetProperty(p => p.ApprovedAt, DateTime.UtcNow));
        if (approved == 0) throw new InvalidOperationException("This pipeline isn't waiting for approval.");
        live.Publish(new PipelineChanged(pipelineId, repo.Id));
        queue.Enqueue(pipelineId);
    }
}
