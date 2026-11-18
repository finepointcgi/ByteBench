using GitBench.Data;
using GitBench.Services.Git;
using Microsoft.EntityFrameworkCore;

namespace GitBench.Services.Ci;

/// <summary>
/// Merge check "Pipeline": the latest pipeline on the PR's current source head. Passed → Passed; Queued/Running or
/// no pipeline yet → Pending; Failed/Canceled/Error → Failed. Reports nothing when the head has no pipeline file.
/// Enforcement (branch protection) lives elsewhere; this only reports.
/// </summary>
public class PipelineMergeCheck(IDbContextFactory<AppDbContext> dbFactory, RepoStorage storage) : IMergeCheck
{
    public const string Name = "Pipeline";

    public async Task<IReadOnlyList<MergeCheckResult>> EvaluateAsync(int pullRequestId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var pr = await db.PullRequests.AsNoTracking().Include(p => p.Repository).ThenInclude(r => r.Project)
            .FirstOrDefaultAsync(p => p.Id == pullRequestId, ct);
        if (pr == null) return [];

        var path = storage.PathFor(pr.Repository.Project.Key, pr.Repository.Slug);
        var head = pr.State == PullRequestState.Open || pr.SourceCommitSha == null
            ? await Task.Run(() => GitOps.BranchExists(path, pr.SourceRef) ? GitOps.ResolveSha(path, pr.SourceRef) : null, ct)
            : pr.SourceCommitSha;
        if (head == null) return [];
        if (await Task.Run(() => GitOps.GetBlob(path, head, CiFile.FileName), ct) == null) return [];

        var latest = await db.Pipelines.AsNoTracking()
            .Where(p => p.RepositoryId == pr.RepositoryId && p.CommitSha == head)
            .OrderByDescending(p => p.Ref == pr.SourceRef).ThenByDescending(p => p.Id)
            .FirstOrDefaultAsync(ct);
        if (latest == null)
            return [new(Name, MergeCheckState.Pending, $"No pipeline has run for {head[..7]} yet.", PipelineUrls.List(pr.Repository))];

        var url = PipelineUrls.Detail(pr.Repository, latest.Id);
        return latest.Status switch
        {
            PipelineStatus.Passed => [new(Name, MergeCheckState.Passed, $"Pipeline #{latest.Id} passed.", url)],
            PipelineStatus.Queued when latest.AwaitingApproval => [new(Name, MergeCheckState.Pending, $"Pipeline #{latest.Id} is waiting for a Maintainer to approve and run it (pull request from a fork).", url)],
            PipelineStatus.Queued => [new(Name, MergeCheckState.Pending, $"Pipeline #{latest.Id} is queued.", url)],
            PipelineStatus.Running => [new(Name, MergeCheckState.Pending, $"Pipeline #{latest.Id} is running.", url)],
            PipelineStatus.Canceled => [new(Name, MergeCheckState.Failed, $"Pipeline #{latest.Id} was canceled.", url)],
            PipelineStatus.Error => [new(Name, MergeCheckState.Failed, $"Pipeline #{latest.Id} could not run: {latest.Error}", url)],
            _ => [new(Name, MergeCheckState.Failed, $"Pipeline #{latest.Id} failed.", url)],
        };
    }
}
