using GitBench.Data;
using GitBench.Services.Git;
using Microsoft.EntityFrameworkCore;

namespace GitBench.Services.Ci;

/// <summary>A pipeline's status for a commit badge.</summary>
public sealed record PipelineBadgeInfo(int Id, PipelineStatus Status, string Ref, string CommitSha);

public sealed record PipelinePage(List<Pipeline> Items, int Total, int Page, int PageSize)
{
    public int Pages => Math.Max(1, (Total + PageSize - 1) / PageSize);
}

/// <summary>
/// Pipeline reads and actions for the UI (scoped). Reads need ViewProject (otherwise they come back empty);
/// "Run pipeline", "Re-run" and "Cancel" need PushCode.
/// </summary>
public partial class PipelineService(IDbContextFactory<AppDbContext> dbFactory, ProjectAccess access, CiTriggerService triggers,
    CiQueue queue, RepoStorage storage, LiveEvents live)
{
    public bool Enabled => triggers.Enabled;

    public async Task<PipelinePage> ListAsync(Repository repo, int page, int pageSize = 30)
    {
        if (!await access.CanAsync(repo.ProjectId, Permission.ViewProject)) return new([], 0, 1, pageSize);
        await using var db = await dbFactory.CreateDbContextAsync();
        var q = db.Pipelines.AsNoTracking().Where(p => p.RepositoryId == repo.Id);
        var total = await q.CountAsync();
        page = Math.Max(1, page);
        var items = await q.OrderByDescending(p => p.Id).Skip((page - 1) * pageSize).Take(pageSize)
            .Include(p => p.TriggeredBy).ToListAsync();
        return new(items, total, page, pageSize);
    }

    /// <summary>A pipeline with its jobs and steps (no logs), or null if it doesn't exist in this repo or can't be viewed.</summary>
    public async Task<Pipeline?> GetAsync(Repository repo, int pipelineId)
    {
        if (!await access.CanAsync(repo.ProjectId, Permission.ViewProject)) return null;
        await using var db = await dbFactory.CreateDbContextAsync();
        var p = await db.Pipelines.AsNoTracking().Include(x => x.TriggeredBy).Include(x => x.ApprovedBy)
            .Include(x => x.Jobs).ThenInclude(j => j.Steps)
            .FirstOrDefaultAsync(x => x.Id == pipelineId && x.RepositoryId == repo.Id);
        if (p == null) return null;
        p.Repository = repo;
        p.Jobs = [.. p.Jobs.OrderBy(j => j.Order)];
        foreach (var j in p.Jobs) j.Steps = [.. j.Steps.OrderBy(s => s.Order)];
        return p;
    }

    /// <summary>The PR number for a pipeline's PR link, if any.</summary>
    public async Task<int?> GetPullRequestNumberAsync(int? pullRequestId)
    {
        if (pullRequestId == null) return null;
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.PullRequests.Where(p => p.Id == pullRequestId).Select(p => (int?)p.Number).FirstOrDefaultAsync();
    }

    /// <summary>A step's log, if the step belongs to <paramref name="pipelineId"/> in <paramref name="repo"/> and the user can view it.</summary>
    public async Task<PipelineStepLog?> GetLogAsync(Repository repo, int pipelineId, int stepId)
    {
        if (!await access.CanAsync(repo.ProjectId, Permission.ViewProject)) return null;
        await using var db = await dbFactory.CreateDbContextAsync();
        var belongs = await db.PipelineSteps.AnyAsync(s => s.Id == stepId && s.Job.PipelineId == pipelineId && s.Job.Pipeline.RepositoryId == repo.Id);
        if (!belongs) return null;
        return await db.PipelineStepLogs.AsNoTracking().FirstOrDefaultAsync(l => l.StepId == stepId) ?? new PipelineStepLog { StepId = stepId };
    }

    /// <summary>Latest pipeline per commit, for status icons on commit and branch lists. With <paramref name="branch"/>, only that branch's.</summary>
    public async Task<Dictionary<string, PipelineBadgeInfo>> LatestForCommitsAsync(Repository repo, IEnumerable<string> shas, string? branch = null)
    {
        var list = shas.Distinct().ToList();
        if (list.Count == 0 || !await access.CanAsync(repo.ProjectId, Permission.ViewProject)) return [];
        await using var db = await dbFactory.CreateDbContextAsync();
        var q = db.Pipelines.Where(p => p.RepositoryId == repo.Id && list.Contains(p.CommitSha));
        if (branch != null) q = q.Where(p => p.Ref == branch);
        var rows = await q.Select(p => new PipelineBadgeInfo(p.Id, p.Status, p.Ref, p.CommitSha)).ToListAsync();
        return rows.GroupBy(r => r.CommitSha).ToDictionary(g => g.Key, g => g.MaxBy(r => r.Id)!);
    }

    /// <summary>Latest pipeline per branch at each branch's tip.</summary>
    public async Task<Dictionary<string, PipelineBadgeInfo>> LatestForBranchesAsync(Repository repo, IEnumerable<(string Branch, string Sha)> tips)
    {
        var list = tips.ToList();
        if (list.Count == 0 || !await access.CanAsync(repo.ProjectId, Permission.ViewProject)) return [];
        var shas = list.Select(t => t.Sha).Distinct().ToList();
        await using var db = await dbFactory.CreateDbContextAsync();
        var rows = await db.Pipelines.Where(p => p.RepositoryId == repo.Id && shas.Contains(p.CommitSha))
            .Select(p => new PipelineBadgeInfo(p.Id, p.Status, p.Ref, p.CommitSha)).ToListAsync();
        var result = new Dictionary<string, PipelineBadgeInfo>();
        foreach (var (branch, sha) in list)
            if (rows.Where(r => r.Ref == branch && r.CommitSha == sha).MaxBy(r => r.Id) is { } hit) result[branch] = hit;
        return result;
    }

    /// <summary>True if the branch's tip has a pipeline file (for showing the "Run pipeline" button).</summary>
    public async Task<bool> HasPipelineFileAsync(Repository repo, string branch)
    {
        var path = storage.PathFor(repo.Project.Key, repo.Slug);
        return await Task.Run(() => GitOps.GetBlob(path, branch, CiFile.FileName) != null);
    }

    /// <summary>"Run pipeline" for a branch's current tip (ignores the file's filters). Needs PushCode.</summary>
    public async Task<int> RunAsync(Repository repo, string branch)
    {
        await access.RequireAsync(repo.ProjectId, Permission.PushCode);
        EnsureEnabled();
        // Only real branches: a fork PR's refs/pull/{n}/head runs only as a fork pipeline (no CI variables, approval first).
        if (branch.StartsWith("refs/", StringComparison.Ordinal)) throw new InvalidOperationException($"Branch '{branch}' does not exist.");
        var path = storage.PathFor(repo.Project.Key, repo.Slug);
        var sha = await Task.Run(() => GitOps.BranchExists(path, branch) ? GitOps.ResolveSha(path, branch) : null)
            ?? throw new InvalidOperationException($"Branch '{branch}' does not exist.");
        return await CreateManualAsync(repo, branch, sha, PipelineTrigger.Manual);
    }

    /// <summary>Runs a pipeline's commit and branch again, re-reading the pipeline file at that commit. Needs PushCode.</summary>
    public async Task<int> RerunAsync(Repository repo, int pipelineId)
    {
        await access.RequireAsync(repo.ProjectId, Permission.PushCode);
        EnsureEnabled();
        await using var db = await dbFactory.CreateDbContextAsync();
        var old = await db.Pipelines.FirstOrDefaultAsync(p => p.Id == pipelineId && p.RepositoryId == repo.Id)
            ?? throw new InvalidOperationException("Pipeline not found.");
        return await CreateManualAsync(repo, old.Ref, old.CommitSha, PipelineTrigger.Retry, old.PullRequestId, old.IsForkPullRequest);
    }

    /// <summary>Cancels a queued or running pipeline (running containers are killed). Needs PushCode.</summary>
    public async Task CancelAsync(Repository repo, int pipelineId)
    {
        await access.RequireAsync(repo.ProjectId, Permission.PushCode);
        await using var db = await dbFactory.CreateDbContextAsync();
        if (!await db.Pipelines.AnyAsync(p => p.Id == pipelineId && p.RepositoryId == repo.Id))
            throw new InvalidOperationException("Pipeline not found.");

        // Still queued: cancel in the database; the runner skips anything that is no longer Queued.
        var now = DateTime.UtcNow;
        var canceled = await db.Pipelines.Where(p => p.Id == pipelineId && p.Status == PipelineStatus.Queued)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.Status, PipelineStatus.Canceled).SetProperty(p => p.FinishedAt, now));
        if (canceled > 0)
        {
            await db.PipelineSteps.Where(s => s.Job.PipelineId == pipelineId && s.Status == PipelineStatus.Queued)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, PipelineStatus.Canceled).SetProperty(x => x.FinishedAt, now));
            await db.PipelineJobs.Where(j => j.PipelineId == pipelineId && j.Status == PipelineStatus.Queued)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, PipelineStatus.Canceled).SetProperty(x => x.FinishedAt, now));
            live.Publish(new PipelineChanged(pipelineId, repo.Id));
            return;
        }
        if (!queue.TryCancelRunning(pipelineId))
            throw new InvalidOperationException("This pipeline is not running.");
    }

    private async Task<int> CreateManualAsync(Repository repo, string branch, string sha, PipelineTrigger trigger, int? prId = null, bool forkPullRequest = false)
    {
        var def = await triggers.LoadDefinitionAsync(repo, sha)
            ?? throw new InvalidOperationException($"There is no {CiFile.FileName} at the root of '{branch}' ({sha[..7]}).");
        if (prId == null)
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            prId = await db.PullRequests.Where(p => p.RepositoryId == repo.Id && p.State == PullRequestState.Open && !p.IsCrossFork && p.SourceBranch == branch)
                .Select(p => (int?)p.Id).FirstOrDefaultAsync();
        }
        var userId = await access.ResolveUserIdAsync();
        // Re-running a fork PR's pipeline: a Maintainer's re-run counts as their approval; anyone else's waits for one.
        int? approvedBy = forkPullRequest && await access.CanAsync(repo.ProjectId, Permission.MergePullRequest) ? userId : null;
        return await triggers.CreateAsync(repo, branch, sha, trigger, userId, prId, def, dedupe: false, forkPullRequest: forkPullRequest, approvedById: approvedBy)
            ?? throw new InvalidOperationException("The pipeline could not be created.");
    }

    private void EnsureEnabled()
    {
        if (!Enabled) throw new InvalidOperationException("CI pipelines are turned off on this server (Ci:Enabled).");
    }
}
