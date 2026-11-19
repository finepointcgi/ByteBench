using GitBench.Data;
using GitBench.Services;
using GitBench.Services.MergeQueue;

namespace GitBench.Api;

/// <summary>One merge queue entry. <c>state</c>: queued, testing, passed, merged, failed or removed.</summary>
public record MergeQueueEntryDto(int Id, int PullRequestNumber, string PullRequestTitle, string TargetBranch, string State, int? Position,
    string Strategy, UserDto EnqueuedBy, string HeadSha, string? BaseSha, string? SpeculativeSha, int? PipelineId, string? PipelineStatus,
    string? Message, DateTimeOffset CreatedAt, DateTimeOffset? FinishedAt);

/// <summary>A branch's merge queue: settings, active entries in order and recently finished ones.</summary>
public record MergeQueueDto(string Branch, bool Enabled, int? MaxBatch, bool? RequireCi, string? Strategy, int? TimeoutMinutes,
    IReadOnlyList<MergeQueueEntryDto> Entries, IReadOnlyList<MergeQueueEntryDto> Recent);

/// <summary>Merge queue: view a branch's queue, add/remove a pull request, move it to the front.</summary>
public static class MergeQueueEndpoints
{
    public static void MapMergeQueueEndpoints(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/projects/{key}/repos/{slug}").WithTags("Merge queue");
        g.MapGet("/merge-queue", GetQueue).WithName("GetMergeQueue")
            .WithSummary("Get a branch's merge queue").WithDescription("?branch= (default: the repository's default branch).");
        g.MapGet("/pull-requests/{number:int}/merge-queue", GetEntry).WithName("GetMergeQueueEntry")
            .WithSummary("A pull request's merge queue entry").WithDescription("Its active entry (with position), else its latest finished one. 404 if it was never queued.");
        g.MapPost("/pull-requests/{number:int}/merge-queue", Enqueue).WithName("EnqueuePullRequest")
            .WithSummary("Add a pull request to the merge queue").WithDescription("Requires Maintainer; every other merge check must pass. 422 with the reason otherwise.")
            .Produces<MergeQueueEntryDto>(StatusCodes.Status201Created);
        g.MapDelete("/pull-requests/{number:int}/merge-queue", Dequeue).WithName("DequeuePullRequest")
            .WithSummary("Remove a pull request from the merge queue").WithDescription("Its author or a Maintainer.");
        g.MapPost("/pull-requests/{number:int}/merge-queue/move-to-front", MoveToFront).WithName("MoveMergeQueueEntryToFront")
            .WithSummary("Move a queued pull request to the front").WithDescription("Maintainers only. Everything behind it is tested again.");
    }

    private static async Task<PullRequest> RequirePullRequestAsync(PullRequestService prs, string key, string slug, int number) =>
        await prs.GetAsync(key, slug, number) ?? throw ApiException.NotFound($"Pull request {slug}#{number} not found.");

    private static async Task<MergeQueueDto> GetQueue(RepositoryService repos, MergeQueueService queues, string key, string slug, string? branch)
    {
        var repo = await repos.GetRepositoryAsync(key, slug) ?? throw ApiException.NotFound($"Repository '{key.ToUpperInvariant()}/{slug}' not found.");
        var view = await queues.ViewAsync(repo, string.IsNullOrWhiteSpace(branch) ? repo.DefaultBranch : branch.Trim())
                   ?? throw ApiException.NotFound($"Repository '{key.ToUpperInvariant()}/{slug}' not found.");
        var c = view.Config;
        return new MergeQueueDto(view.Branch, c != null, c?.MaxBatch, c?.RequireCi, c == null ? null : Name(c.StrategyFor(repo)), c?.TimeoutMinutes,
            view.Active.Select((e, i) => Entry(e, i + 1)).ToList(), view.Recent.Select(e => Entry(e, null)).ToList());
    }

    private static async Task<MergeQueueEntryDto> GetEntry(PullRequestService prs, MergeQueueService queues, ApiCaller caller, string key, string slug, int number)
    {
        var pr = await RequirePullRequestAsync(prs, key, slug, number);
        var status = await queues.StatusAsync(pr.Id, caller.Id);
        var entry = status.Active ?? status.Last ?? throw ApiException.NotFound($"Pull request {slug}#{number} has never been in a merge queue.");
        entry.PullRequest = pr;
        return Entry(entry, status.Active != null ? status.Position : null);
    }

    private static async Task<IResult> Enqueue(PullRequestService prs, MergeQueueService queues, ApiCaller caller, string key, string slug, int number)
    {
        var pr = await RequirePullRequestAsync(prs, key, slug, number);
        await queues.EnqueueAsync(pr.Id, caller.Id);
        return TypedResults.Created($"/api/v1/projects/{key}/repos/{slug}/pull-requests/{number}/merge-queue",
            await GetEntry(prs, queues, caller, key, slug, number));
    }

    private static async Task<IResult> Dequeue(PullRequestService prs, MergeQueueService queues, ApiCaller caller, string key, string slug, int number)
    {
        var pr = await RequirePullRequestAsync(prs, key, slug, number);
        await queues.DequeueAsync(pr.Id, caller.Id);
        return TypedResults.NoContent();
    }

    private static async Task<MergeQueueEntryDto> MoveToFront(PullRequestService prs, MergeQueueService queues, ApiCaller caller, string key, string slug, int number)
    {
        var pr = await RequirePullRequestAsync(prs, key, slug, number);
        await queues.MoveToFrontAsync(pr.Id, caller.Id);
        return await GetEntry(prs, queues, caller, key, slug, number);
    }

    private static string Name(Enum value) => value.ToString().ToLowerInvariant();

    private static MergeQueueEntryDto Entry(MergeQueueEntry e, int? position) => new(
        e.Id, e.PullRequest.Number, e.PullRequest.Title, e.TargetBranch, Name(e.State), position, Name(e.Strategy), Dto.User(e.EnqueuedBy),
        e.HeadSha, e.BaseSha, e.SpeculativeSha, e.PipelineId, e.Pipeline == null ? null : Name(e.Pipeline.Status), e.Message,
        Dto.Utc(e.CreatedAt), Dto.Utc(e.FinishedAt));
}
