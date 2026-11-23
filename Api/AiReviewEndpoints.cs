using ByteBench.Data;
using ByteBench.Services;
using ByteBench.Services.Ai;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ByteBench.Api;

public record AiReviewAvailabilityDto(bool Available, string? Backend, string? Reason);

/// <summary>A repository's AI review settings plus what the server allows. <c>ignorePaths</c>: gitignore-style patterns never sent to the model.</summary>
public record AiReviewSettingsDto(bool Enabled, AiReviewMode Mode, int MaxDiffChars, IReadOnlyList<string> IgnorePaths, string? FocusPrompt,
    bool BlockOnUnresolvedFindings, DateTimeOffset UpdatedAt, AiReviewAvailabilityDto Availability, int MinDiffChars, int MaxDiffCharsLimit, int MaxReviewsPerHour);

/// <summary>PUT body: omitted fields keep their current value. An empty <c>focusPrompt</c> or <c>ignorePaths</c> clears it.</summary>
public record AiReviewSettingsRequest(bool? Enabled = null, AiReviewMode? Mode = null, int? MaxDiffChars = null, IReadOnlyList<string>? IgnorePaths = null,
    string? FocusPrompt = null, bool? BlockOnUnresolvedFindings = null);

/// <summary>An AI finding: the root of an inline thread the bot posted. <c>severity</c>: bug, security, suggestion or nit.</summary>
public record AiFindingDto(int CommentId, int ReviewId, string? Severity, string? Path, int? Line, int? OldLine, string Body, bool Resolved, bool Dismissed, DateTimeOffset CreatedAt);

public record AiReviewDto(int Id, AiReviewStatus Status, string Trigger, string HeadSha, UserDto? RequestedBy, string? Error, string? Backend,
    int FindingsCount, int InlineCount, int UnplacedCount, int SkippedFilesCount, int BatchCount, int DiffChars,
    DateTimeOffset CreatedAt, DateTimeOffset? StartedAt, DateTimeOffset? CompletedAt, string? Summary, IReadOnlyList<AiFindingDto> Findings);

/// <summary>AI pull request review: per-repository settings, reviews with findings, triggering a review, dismissing findings.</summary>
public static class AiReviewEndpoints
{
    public static void MapAiReviewEndpoints(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/projects/{key}/repos/{slug}").WithTags("AI review");
        g.MapGet("/ai-review/settings", GetSettings).WithName("GetAiReviewSettings").WithSummary("The repository's AI review settings")
            .WithDescription("Needs Maintainer+. Includes whether the server's AI backend is available and the limits.");
        g.MapPut("/ai-review/settings", PutSettings).WithName("SetAiReviewSettings").WithSummary("Change the repository's AI review settings")
            .WithDescription("Needs Maintainer+. Audited. mode: onDemand, onOpen or onPush.").WithJsonBody<AiReviewSettingsRequest>();
        g.MapGet("/pull-requests/{number:int}/ai-reviews", ListReviews).WithName("ListAiReviews").WithSummary("A pull request's AI reviews with their findings, newest first")
            .WithDescription("status: queued, running, completed, failed or skipped. Each review lists the inline findings it posted and its summary comment.");
        g.MapGet("/pull-requests/{number:int}/ai-reviews/{reviewId:int}", GetReview).WithName("GetAiReview").WithSummary("One AI review with its findings");
        g.MapPost("/pull-requests/{number:int}/ai-reviews", Trigger).WithName("RequestAiReview").WithSummary("Ask for an AI review of the pull request")
            .WithDescription("Needs Developer+. The same guards as the \"Review with AI\" button: 202 with the queued review (poll GET until it is completed, failed or skipped); " +
                             "409 when this commit was already reviewed or a review is pending (existingReviewId says which); 429 when the repository's hourly limit is reached; " +
                             "422 when AI review is off, no AI backend is set up or the pull request isn't open.")
            .Produces<AiReviewDto>(StatusCodes.Status202Accepted);
        g.MapPost("/pull-requests/{number:int}/ai-findings/{commentId:int}/dismiss", Dismiss).WithName("DismissAiFinding")
            .WithSummary("Dismiss an AI finding (resolve its thread as not applicable)")
            .WithDescription("Allowed to the pull request's author or a Maintainer. A dismissed finding stops blocking merges. 422 if the thread isn't an AI finding.");
        g.MapPost("/pull-requests/{number:int}/ai-findings/{commentId:int}/undismiss", Undismiss).WithName("UndismissAiFinding")
            .WithSummary("Bring a dismissed AI finding back").WithDescription("Reopens its thread. 422 if the finding isn't dismissed.");
    }

    private static async Task<Repository> RequireRepoAsync(RepositoryService repos, string key, string slug) =>
        await repos.GetRepositoryAsync(key, slug) ?? throw ApiException.NotFound($"Repository '{key.ToUpperInvariant()}/{slug}' not found.");

    private static async Task<PullRequest> RequirePullRequestAsync(PullRequestService prs, string key, string slug, int number) =>
        await prs.GetAsync(key, slug, number) ?? throw ApiException.NotFound($"Pull request {slug}#{number} not found.");

    // ---------------------------------------------------------------- settings

    private static async Task<AiReviewSettingsDto> SettingsDtoAsync(AiReviewSettingsService settings, IOptions<AiReviewOptions> options, RepositoryAiReviewSettings s)
    {
        var a = await settings.GetAvailabilityAsync();
        return new AiReviewSettingsDto(s.Enabled, s.Mode, s.MaxDiffChars, (s.IgnorePaths ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries), s.FocusPrompt,
            s.BlockOnUnresolvedFindings, Dto.Utc(s.UpdatedAt), new AiReviewAvailabilityDto(a.Available, a.BackendName, a.Reason),
            AiReviewSettingsService.MinDiffChars, Math.Max(AiReviewSettingsService.MinDiffChars, options.Value.MaxDiffCharsLimit), Math.Max(1, options.Value.MaxReviewsPerRepoPerHour));
    }

    private static async Task<AiReviewSettingsDto> GetSettings(RepositoryService repos, AiReviewSettingsService settings, ProjectAccess access, IOptions<AiReviewOptions> options,
        string key, string slug)
    {
        var repo = await RequireRepoAsync(repos, key, slug);
        await access.RequireRoleAsync(repo.ProjectId, ProjectRole.Maintainer, "view AI review settings");
        return await SettingsDtoAsync(settings, options, await settings.GetAsync(repo.Id));
    }

    private static async Task<AiReviewSettingsDto> PutSettings(HttpContext http, RepositoryService repos, AiReviewSettingsService settings, ApiCaller caller,
        IOptions<AiReviewOptions> options, string key, string slug)
    {
        var repo = await RequireRepoAsync(repos, key, slug);
        var body = await ApiResults.ReadBodyAsync<AiReviewSettingsRequest>(http.Request);
        var cur = await settings.GetAsync(repo.Id);
        var input = new AiReviewSettingsInput(body.Enabled ?? cur.Enabled, body.Mode ?? cur.Mode, body.MaxDiffChars ?? cur.MaxDiffChars,
            body.IgnorePaths != null ? string.Join('\n', body.IgnorePaths) : cur.IgnorePaths, body.FocusPrompt ?? cur.FocusPrompt,
            body.BlockOnUnresolvedFindings ?? cur.BlockOnUnresolvedFindings);
        return await SettingsDtoAsync(settings, options, await settings.SaveAsync(repo.Id, input, caller.Id));
    }

    // ---------------------------------------------------------------- reviews

    private static AiFindingDto Finding(PullRequestComment c) => new(c.Id, c.AiReviewId ?? 0, c.AiSeverity, c.FilePath, c.Line, c.OldLine, c.Body,
        c.IsResolved, c.AiDismissed, Dto.Utc(c.CreatedAt));

    private static async Task<List<AiReviewDto>> BuildAsync(IDbContextFactory<AppDbContext> dbFactory, List<AiReview> reviews)
    {
        if (reviews.Count == 0) return [];
        await using var db = await dbFactory.CreateDbContextAsync();
        var ids = reviews.Select(r => r.Id).ToList();
        var comments = await db.PullRequestComments.AsNoTracking()
            .Where(c => c.AiReviewId != null && ids.Contains(c.AiReviewId.Value) && c.ParentId == null).OrderBy(c => c.Id).ToListAsync();
        var userIds = reviews.Where(r => r.RequestedById != null).Select(r => r.RequestedById!.Value).Distinct().ToList();
        var users = await db.Users.AsNoTracking().Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id);
        return reviews.Select(r =>
        {
            var mine = comments.Where(c => c.AiReviewId == r.Id).ToList();
            return new AiReviewDto(r.Id, r.Status, r.Trigger, r.HeadSha, r.RequestedById is { } u && users.TryGetValue(u, out var user) ? Dto.User(user) : null,
                r.Error, r.Backend, r.FindingsCount, r.InlineCount, r.UnplacedCount, r.SkippedFilesCount, r.BatchCount, r.DiffChars,
                Dto.Utc(r.CreatedAt), Dto.Utc(r.StartedAt), Dto.Utc(r.CompletedAt), mine.FirstOrDefault(c => c.FilePath == null)?.Body,
                mine.Where(c => c.FilePath != null && c.AiFingerprint != null).Select(Finding).ToList());
        }).ToList();
    }

    private static async Task<List<AiReviewDto>> ListReviews(HttpContext http, PullRequestService prs, IDbContextFactory<AppDbContext> dbFactory, string key, string slug, int number)
    {
        var paging = Paging.From(http.Request);
        var pr = await RequirePullRequestAsync(prs, key, slug, number);
        await using var db = await dbFactory.CreateDbContextAsync();
        var q = db.AiReviews.AsNoTracking().Where(r => r.PullRequestId == pr.Id);
        var total = await q.CountAsync();
        var page = await q.OrderByDescending(r => r.CreatedAt).ThenByDescending(r => r.Id).Skip(paging.Skip).Take(paging.PerPage).ToListAsync();
        paging.WriteHeaders(http, total);
        return await BuildAsync(dbFactory, page);
    }

    private static async Task<AiReviewDto> GetReview(PullRequestService prs, IDbContextFactory<AppDbContext> dbFactory, string key, string slug, int number, int reviewId)
    {
        var pr = await RequirePullRequestAsync(prs, key, slug, number);
        await using var db = await dbFactory.CreateDbContextAsync();
        var review = await db.AiReviews.AsNoTracking().FirstOrDefaultAsync(r => r.Id == reviewId && r.PullRequestId == pr.Id)
                     ?? throw ApiException.NotFound($"AI review {reviewId} not found on {slug}#{number}.");
        return (await BuildAsync(dbFactory, [review]))[0];
    }

    private static async Task<IResult> Trigger(PullRequestService prs, AiReviewService reviews, ApiCaller caller, IDbContextFactory<AppDbContext> dbFactory,
        string key, string slug, int number)
    {
        var pr = await RequirePullRequestAsync(prs, key, slug, number);
        var intake = await reviews.RequestManualAsync(pr.Id, caller.Id);
        if (intake.ReviewId is { } id)
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            var review = await db.AiReviews.AsNoTracking().FirstAsync(r => r.Id == id);
            return TypedResults.Json((await BuildAsync(dbFactory, [review]))[0], statusCode: StatusCodes.Status202Accepted);
        }
        if (intake.RateLimited) throw new ApiException(StatusCodes.Status429TooManyRequests, "Too Many Requests", intake.Reason);
        if (intake.ExistingReviewId is { } existing)
            throw new ApiException(StatusCodes.Status409Conflict, "Conflict", intake.Reason, new Dictionary<string, object?> { ["existingReviewId"] = existing });
        throw ApiException.Unprocessable(intake.Reason ?? "The review could not be queued.");
    }

    // ---------------------------------------------------------------- findings

    private static (PullRequestComment Comment, PullRequestComment Root) Locate(PullRequest pr, int commentId, string slug, int number)
    {
        var c = pr.Comments.FirstOrDefault(x => x.Id == commentId) ?? throw ApiException.NotFound($"Comment {commentId} not found on {slug}#{number}.");
        return (c, c.ParentId is { } pid ? pr.Comments.FirstOrDefault(x => x.Id == pid) ?? c : c);
    }

    private static async Task<AiFindingDto> Dismiss(PullRequestService prs, AiReviewService reviews, ApiCaller caller, string key, string slug, int number, int commentId)
    {
        var (c, root) = Locate(await RequirePullRequestAsync(prs, key, slug, number), commentId, slug, number);
        await reviews.DismissFindingAsync(c.Id, caller.Id);
        return Finding(Locate(await RequirePullRequestAsync(prs, key, slug, number), root.Id, slug, number).Root);
    }

    private static async Task<AiFindingDto> Undismiss(PullRequestService prs, ApiCaller caller, string key, string slug, int number, int commentId)
    {
        var (c, root) = Locate(await RequirePullRequestAsync(prs, key, slug, number), commentId, slug, number);
        if (root.AiFingerprint == null || !root.IsInline) throw ApiException.Unprocessable("Only AI review findings can be dismissed or brought back.");
        if (!root.AiDismissed) throw ApiException.Unprocessable("This finding isn't dismissed.");
        await prs.SetThreadResolvedAsync(c.Id, caller.Id, false);
        return Finding(Locate(await RequirePullRequestAsync(prs, key, slug, number), root.Id, slug, number).Root);
    }
}
