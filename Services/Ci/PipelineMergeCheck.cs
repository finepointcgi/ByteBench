using ByteBench.Data;
using ByteBench.Services.Git;
using Microsoft.EntityFrameworkCore;

namespace ByteBench.Services.Ci;

/// <summary>
/// Merge check "Pipeline": the latest pipeline on the PR's current source head. Passed → Passed; Queued/Running,
/// blocked on a manual job, or no pipeline yet → Pending; Failed/Canceled/Error → Failed. Reports nothing when the head has no pipeline file.
/// Enforcement (branch protection) lives elsewhere; this only reports.
/// </summary>
public class PipelineMergeCheck(IDbContextFactory<AppDbContext> dbFactory, RepoStorage storage, CiRemoteIncludes remoteIncludes) : IMergeCheck
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
        if (await Task.Run(() => CiFile.GetBlob(path, head), ct) == null) return [];

        // A pipeline file that would start nothing for this PR (jobs' rules leave every job out, or `on:` excludes the branch and
        // pull requests) must not leave the check Pending forever: there is nothing to wait for.
        if (await WouldStartNothingAsync(pr, path, head, ct)) return [];

        // Tag pipelines on the same commit belong to the tag, not to the PR.
        var latest = await db.Pipelines.AsNoTracking()
            .Where(p => p.RepositoryId == pr.RepositoryId && p.CommitSha == head && !p.Ref.StartsWith(CiRef.TagPrefix))
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
            PipelineStatus.Manual => [new(Name, MergeCheckState.Pending, $"Pipeline #{latest.Id} is waiting for a manual job.", url)],
            PipelineStatus.Canceled => [new(Name, MergeCheckState.Failed, $"Pipeline #{latest.Id} was canceled.", url)],
            PipelineStatus.Error => [new(Name, MergeCheckState.Failed, $"Pipeline #{latest.Id} could not run: {latest.Error}", url)],
            _ => [new(Name, MergeCheckState.Failed, $"Pipeline #{latest.Id} failed.", url)],
        };
    }

    /// <summary>True when no pipeline exists for the head and none would be created for it, so the check has nothing to report.</summary>
    private async Task<bool> WouldStartNothingAsync(PullRequest pr, string path, string head, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (await db.Pipelines.AnyAsync(p => p.RepositoryId == pr.RepositoryId && p.CommitSha == head && !p.Ref.StartsWith(CiRef.TagPrefix), ct)) return false;
        var blob = await Task.Run(() => CiFile.GetBlob(path, head), ct);
        if (blob is not { Text: { } text } || blob.TooLarge || blob.IsBinary) return false;
        if (await Task.Run(() => CiFile.Parse(text, new CiIncludeProvider(path, head, remoteIncludes, allowRemote: !pr.IsCrossFork)), ct) is not { Definition: { } def }) return false; // an invalid file creates an Error pipeline: report that
        if (pr.IsCrossFork) return !def.On.PullRequests || def.ForBranch(pr.SourceBranch).Jobs.Count == 0;
        if (def.ForBranch(pr.SourceBranch) is { Error: null, Jobs.Count: 0 }) return true;
        return !def.On.PullRequests && !def.On.MatchesBranch(pr.SourceBranch);
    }
}
