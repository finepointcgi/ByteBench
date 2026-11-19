using System.Runtime.CompilerServices;
using Anthropic.Models.Beta;
using Anthropic.Models.Beta.Messages;
using ByteBench.Data;
using ByteBench.Services.Editor.Ai;
using ByteBench.Services.Git;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ByteBench.Services.Ai;

/// <summary>
/// What happened to a request for a review: a queued <see cref="AiReview"/> (<see cref="ReviewId"/>), or why nothing was queued
/// (<see cref="Reason"/>). <see cref="ExistingReviewId"/> is the review that already covers this commit or is in progress.
/// </summary>
public sealed record AiReviewIntake(int? ReviewId, string? Reason, int? ExistingReviewId = null, bool RateLimited = false)
{
    public bool Queued => ReviewId != null;
}

/// <summary>
/// AI code review of pull requests: turns a PR's merge-base diff into one or more structured-output calls to the server's AI
/// backend (the web editor's: <c>Ai:Editor:Backend</c>, API or Claude Code, no tools), validates the findings against the
/// diff, and posts them as the ByteBench AI bot (inline comments with a severity, one summary comment). It never approves and
/// never requests changes; an optional per-repository merge check (<see cref="AiReviewMergeCheck"/>) blocks on unresolved
/// blocking findings. Per-repository settings (off by default), rate limit, size guardrails and idempotency per head commit
/// are enforced here. Also writes PR descriptions. Runs from <see cref="AiReviewWorker"/>; the pure parts live in
/// <see cref="AiReviewLogic"/>.
/// </summary>
public class AiReviewService(
    IDbContextFactory<AppDbContext> dbFactory, AiClient ai, PullRequestService prs, RepositoryService repos,
    AiReviewQueue queue, AiReviewSettingsService settings, IEnumerable<IEditorAiBackend> backends,
    IOptions<EditorAiOptions> editorOptions, IOptions<AiReviewOptions> options, ProjectAccess access,
    ILogger<AiReviewService> log, Audit.AuditLog audit)
{
    public const string ReviewFeature = "pr-review";
    public const string DescriptionFeature = "pr-description";

    /// <summary>Whether the Anthropic API is configured (used by "Write description with AI", which streams from the API).</summary>
    public bool IsConfigured => ai.IsConfigured;

    // ---------- Queries ----------

    public async Task<AiReview?> GetLatestAsync(int pullRequestId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.AiReviews.Where(r => r.PullRequestId == pullRequestId)
            .OrderByDescending(r => r.CreatedAt).ThenByDescending(r => r.Id).FirstOrDefaultAsync();
    }

    // ---------- Queue intake ----------

    /// <summary>
    /// "Review with AI": Developers+ (review permission). Creates the queued review right away (so the caller can say why not)
    /// and hands it to the worker. Reviewing a commit that already has a completed or pending review is refused, not repeated.
    /// </summary>
    public async Task<AiReviewIntake> RequestManualAsync(int pullRequestId, int userId, CancellationToken ct = default)
    {
        await access.RequireForPullRequestAsync(pullRequestId, Permission.ReviewPullRequest, userId);
        var intake = await CreateQueuedAsync(pullRequestId, new AiReviewRequest(AiReviewRequest.Manual, userId), ct);
        if (intake.ReviewId is { } id) queue.Ready(id);
        return intake;
    }

    /// <summary>
    /// Turns a request into a <see cref="AiReviewStatus.Queued"/> row, or says why not: AI review off for the repository or the
    /// trigger doesn't match its mode (automatic triggers), no AI backend available, PR not open, this head already reviewed
    /// or being reviewed, or the repository's hourly limit reached.
    /// </summary>
    public async Task<AiReviewIntake> CreateQueuedAsync(int pullRequestId, AiReviewRequest request, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var pr = await db.PullRequests.Include(p => p.Repository).ThenInclude(r => r.Project)
            .FirstOrDefaultAsync(p => p.Id == pullRequestId, ct);
        if (pr == null || pr.State != PullRequestState.Open) return new(null, "The pull request isn't open.");

        var cfg = await settings.GetAsync(pr.RepositoryId, ct);
        if (!cfg.Enabled) return new(null, "AI review is turned off for this repository.");
        if (!request.IsManual)
        {
            var wanted = request.Trigger switch
            {
                AiReviewRequest.Created => cfg.Mode is AiReviewMode.OnOpen or AiReviewMode.OnPush,
                AiReviewRequest.Push => cfg.Mode == AiReviewMode.OnPush,
                _ => false,
            };
            if (!wanted) return new(null, "This repository doesn't review automatically on that event.");
        }
        var availability = await settings.GetAvailabilityAsync(ct);
        if (!availability.Available) return new(null, availability.Reason);

        var head = await repos.Git(pr.Repository, path => GitOps.ResolveSha(path, pr.SourceRef));
        if (head == null) return new(null, "The source branch could not be read.");

        var existing = await db.AiReviews.Where(r => r.PullRequestId == pr.Id && (r.HeadSha == head || r.Status == AiReviewStatus.Queued || r.Status == AiReviewStatus.Running)
                && r.Status != AiReviewStatus.Failed && r.Status != AiReviewStatus.Skipped)
            .OrderByDescending(r => r.Id).FirstOrDefaultAsync(ct);
        if (existing != null)
        {
            var pending = existing.Status is AiReviewStatus.Queued or AiReviewStatus.Running;
            log.LogInformation("AI review of PR {Id} not queued: {Why}", pr.Id, pending ? "one is already pending" : $"head {head[..7]} was already reviewed");
            return new(null, pending ? "A review of this pull request is already queued or running." : $"This commit ({head[..7]}) was already reviewed.", existing.Id);
        }

        var since = DateTime.UtcNow.AddHours(-1);
        var limit = Math.Max(1, options.Value.MaxReviewsPerRepoPerHour);
        var recent = await db.AiReviews.CountAsync(r => r.CreatedAt > since && r.Status != AiReviewStatus.Skipped
            && db.PullRequests.Any(p => p.Id == r.PullRequestId && p.RepositoryId == pr.RepositoryId), ct);
        if (recent >= limit)
        {
            await audit.RecordAsync("ai_review.rate_limited", "pull_request", pr.Id, pr.Repository.ProjectId,
                new { repository = pr.Repository.Slug, number = pr.Number, trigger = request.Trigger, limitPerHour = limit }, request.UserId);
            return new(null, $"This repository reached its limit of {limit} AI reviews per hour. Try again later.", RateLimited: true);
        }

        var review = new AiReview { PullRequestId = pr.Id, HeadSha = head, Trigger = request.Trigger, RequestedById = request.UserId };
        db.AiReviews.Add(review);
        await db.SaveChangesAsync(ct);
        await audit.RecordAsync("ai_review.queued", "pull_request", pr.Id, pr.Repository.ProjectId,
            new { reviewId = review.Id, repository = pr.Repository.Slug, number = pr.Number, trigger = request.Trigger, head = head[..7] }, request.UserId);
        queue.NotifyChanged(pr.Id);
        return new(review.Id, null);
    }

    public async Task MarkFailedAsync(int reviewId, string error)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var r = await db.AiReviews.FindAsync(reviewId);
        if (r == null) return;
        r.Status = AiReviewStatus.Failed;
        r.Error = error.Length > 1000 ? error[..1000] + "…" : error;
        r.CompletedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        await RecordOutcomeAsync(db, r, "ai_review.failed", new { error = r.Error });
        queue.NotifyChanged(r.PullRequestId);
    }

    private async Task RecordOutcomeAsync(AppDbContext db, AiReview r, string action, object details)
    {
        var projectId = await db.PullRequests.Where(p => p.Id == r.PullRequestId).Select(p => (int?)p.Repository.ProjectId).FirstOrDefaultAsync();
        await audit.RecordAsync(action, "pull_request", r.PullRequestId, projectId, new { reviewId = r.Id, head = r.HeadSha.Length >= 7 ? r.HeadSha[..7] : r.HeadSha, details }, r.RequestedById);
    }

    // ---------- The review ----------

    /// <summary>Runs one queued review end to end. Throws on backend or Git errors; the worker records them as Failed.</summary>
    public async Task RunAsync(int reviewId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        // Claim the review atomically: the background worker and a manual run can race for the same queued review.
        var claimed = await db.AiReviews.Where(r => r.Id == reviewId && r.Status == AiReviewStatus.Queued)
            .ExecuteUpdateAsync(u => u.SetProperty(r => r.Status, AiReviewStatus.Running).SetProperty(r => r.StartedAt, DateTime.UtcNow), ct);
        if (claimed == 0) return;
        var review = await db.AiReviews.FindAsync([reviewId], ct);
        if (review == null) return;
        var pr = await db.PullRequests.Include(p => p.Repository).ThenInclude(r => r.Project)
            .FirstOrDefaultAsync(p => p.Id == review.PullRequestId, ct);

        async Task Finish(AiReviewStatus status, string? error = null, string? action = null, object? details = null)
        {
            review.Status = status;
            review.Error = error;
            review.CompletedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(CancellationToken.None);
            await RecordOutcomeAsync(db, review, action ?? (status == AiReviewStatus.Skipped ? "ai_review.skipped" : "ai_review.completed"), details ?? new { error });
            queue.NotifyChanged(review.PullRequestId);
        }

        if (pr == null || pr.State != PullRequestState.Open) { await Finish(AiReviewStatus.Skipped, "The pull request is no longer open."); return; }
        var cfg = await settings.GetAsync(pr.RepositoryId, ct);
        if (!cfg.Enabled) { await Finish(AiReviewStatus.Skipped, "AI review was turned off for this repository."); return; }
        var backend = await EditorAiBackendSelector.SelectAsync(backends, editorOptions.Value.Backend, ct);
        if (backend is not IStructuredAiBackend structured) { await Finish(AiReviewStatus.Skipped, "AI review isn't set up on this server."); return; }

        var changes = await prs.GetChangesAsync(pr);
        if (changes == null) { await Finish(AiReviewStatus.Failed, "The pull request's branches could not be compared.", "ai_review.failed"); return; }
        if (await db.AiReviews.AnyAsync(r => r.Id != review.Id && r.PullRequestId == pr.Id && r.HeadSha == changes.HeadSha && r.Status == AiReviewStatus.Completed, ct))
        {
            await Finish(AiReviewStatus.Skipped, $"Commit {changes.HeadSha[..7]} was already reviewed.");
            return;
        }

        review.Status = AiReviewStatus.Running;
        review.HeadSha = changes.HeadSha;
        review.Backend = backend.DisplayName;
        review.StartedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        queue.NotifyChanged(pr.Id);

        // Size guardrail: at most MaxDiffChars of diff go to the model; patterns the maintainers ignore never do.
        var budget = Math.Clamp(cfg.MaxDiffChars, AiReviewSettingsService.MinDiffChars, AiReviewLogic.DefaultBatchBudget);
        var plan = AiReviewLogic.Plan(changes.Files, budget, AiReviewLogic.MaxBatches, AiReviewSettingsService.IgnoreMatcher(cfg.IgnorePaths), cfg.MaxDiffChars);
        review.SkippedFilesCount = plan.Skipped.Count + plan.Omitted.Count;
        review.BatchCount = plan.Batches.Count;
        review.DiffChars = plan.Batches.Sum(b => b.DiffText.Length);
        if (plan.Batches.Count == 0)
        {
            await Finish(AiReviewStatus.Skipped, $"No reviewable files ({plan.Skipped.Count + plan.Omitted.Count} skipped: " +
                string.Join(", ", plan.Skipped.Concat(plan.Omitted).Take(5).Select(s => $"{s.Path} ({s.Reason})")) + (plan.Skipped.Count + plan.Omitted.Count > 5 ? ", …" : "") + ").");
            return;
        }
        await RecordOutcomeAsync(db, review, "ai_review.started", new { backend = backend.DisplayName, batches = plan.Batches.Count, diffChars = review.DiffChars });

        // One call per batch; a failed batch is reported in the summary unless every batch failed.
        var boundary = EditorAiPrompts.NewBoundary();
        var outcomes = new List<BatchOutcome>();
        foreach (var batch in plan.Batches)
        {
            var user = AiReviewLogic.BuildUserPrompt(pr.Repository.Slug, pr.Number, pr.Title, pr.Description,
                pr.SourceBranch, pr.TargetBranch, changes.Commits, plan, batch, cfg.FocusPrompt, boundary);
            var raw = await structured.CompleteJsonAsync<AiReviewResponse>(new StructuredAiRequest(ReviewFeature, AiReviewLogic.SystemPrompt, user,
                AiReviewLogic.ResponseSchema, review.RequestedById, pr.Repository.ProjectId, 32000), ct);
            var result = AiReviewLogic.NormalizeReview(raw);
            outcomes.Add(new BatchOutcome(batch.Index, result.Value, result.Value == null ? result.Error : null));
        }
        if (outcomes.All(o => o.Response == null))
        {
            await Finish(AiReviewStatus.Failed, string.Join(" ", outcomes.Select(o => o.Error).Distinct()), "ai_review.failed");
            return;
        }

        var responses = outcomes.Where(o => o.Response != null).Select(o => o.Response!).ToList();
        var validation = AiReviewLogic.ValidateFindings(responses.SelectMany(r => r.Findings ?? []), changes.Files);

        // Cost/noise guardrail: keep the most severe findings; skip ones an earlier review already posted (open or dismissed).
        var maxFindings = Math.Max(1, options.Value.MaxFindings);
        var ranked = validation.Placed.OrderBy(p => Array.IndexOf(AiReviewLogic.Severities, p.Finding.Severity)).ThenBy(p => p.Path, StringComparer.Ordinal).ThenBy(p => p.Line).ToList();
        var kept = ranked.Take(maxFindings).ToList();
        var dropped = ranked.Count - kept.Count;
        var known = (await db.PullRequestComments.Where(c => c.PullRequestId == pr.Id && c.AiFingerprint != null && c.ParentId == null)
            .Select(c => c.AiFingerprint!).ToListAsync(ct)).ToHashSet();
        var repeated = kept.Where(p => known.Contains(AiReviewLogic.Fingerprint(p.Path, p.Finding.Title))).ToList();
        var fresh = new FindingValidation(kept.Except(repeated).ToList(), validation.Unplaced);
        var verdict = AiReviewLogic.DecideVerdict(responses, new FindingValidation(kept, validation.Unplaced));
        var summary = AiReviewLogic.BuildSummary(outcomes, fresh, plan, verdict, backend.DisplayName, changes.HeadSha, dropped, repeated.Count);

        var botId = await AiBot.EnsureAsync(db, ct);
        foreach (var c in AiReviewLogic.ToInlineComments(fresh))
        {
            var posted = await prs.AddCommentAsync(pr.Id, botId, c.Body, c.Path, c.Line);
            await db.PullRequestComments.Where(x => x.Id == posted.Id).ExecuteUpdateAsync(s => s
                .SetProperty(x => x.AiReviewId, review.Id).SetProperty(x => x.AiSeverity, c.Severity).SetProperty(x => x.AiFingerprint, c.Fingerprint), ct);
        }
        var summaryComment = await prs.AddCommentAsync(pr.Id, botId, summary);
        await db.PullRequestComments.Where(x => x.Id == summaryComment.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.AiReviewId, review.Id), ct);

        // The bot only comments. Earlier versions also recorded "changes requested"; withdraw it so it doesn't block merging.
        try { await prs.SetReviewAsync(pr.Id, botId, null); }
        catch (InvalidOperationException ex) { log.LogInformation("AI review verdict not withdrawn on PR {Id}: {Message}", pr.Id, ex.Message); }

        review.FindingsCount = fresh.Placed.Count + fresh.Unplaced.Count;
        review.InlineCount = fresh.Placed.Count;
        review.UnplacedCount = fresh.Unplaced.Count;
        var failedParts = outcomes.Count(o => o.Response == null);
        await Finish(AiReviewStatus.Completed, failedParts > 0 ? $"{failedParts} of {outcomes.Count} parts could not be reviewed (see the summary comment)." : null,
            "ai_review.completed", new { findings = review.FindingsCount, inline = review.InlineCount, unplaced = review.UnplacedCount, repeated = repeated.Count,
                dropped, backend = backend.DisplayName, diffChars = review.DiffChars, skippedFiles = review.SkippedFilesCount });
        log.LogInformation("AI review of {Slug}#{Number} at {Sha}: {Findings} findings, {Inline} inline",
            pr.Repository.Slug, pr.Number, changes.HeadSha[..7], review.FindingsCount, review.InlineCount);
    }

    // ---------- Findings ----------

    /// <summary>
    /// Dismisses an AI finding: resolves its thread as "not applicable" (so it stops blocking merges). Allowed to whoever may
    /// resolve a thread (the PR's author, a Maintainer, the comment's author). Un-resolving the thread brings it back.
    /// </summary>
    public async Task DismissFindingAsync(int commentId, int actorId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var c = await db.PullRequestComments.FirstOrDefaultAsync(x => x.Id == commentId) ?? throw new InvalidOperationException("Comment not found.");
        var root = c.ParentId is { } pid ? await db.PullRequestComments.FirstAsync(x => x.Id == pid) : c;
        if (root.AiFingerprint == null || !root.IsInline) throw new InvalidOperationException("Only AI review findings can be dismissed.");
        var pr = await db.PullRequests.Include(p => p.Repository).FirstAsync(p => p.Id == root.PullRequestId);
        var perms = await access.GetPermissionsAsync(pr.Repository.ProjectId, actorId);
        if (!PullRequestService.CanResolve(pr, root, actorId, perms))
            throw new ForbiddenException("Only the pull request's author or a Maintainer can dismiss this finding.");
        if (root.IsResolved && root.AiDismissed) return;
        root.IsResolved = true;
        root.AiDismissed = true;
        root.ResolvedById = actorId;
        root.ResolvedAt = DateTime.UtcNow;
        db.PullRequestActivities.Add(new PullRequestActivity
        {
            PullRequestId = pr.Id, ActorId = actorId, Text = $"dismissed an AI finding on `{root.FilePath}:{root.Line ?? root.OldLine}`",
        });
        pr.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        await audit.RecordAsync("ai_review.finding_dismissed", "pull_request", pr.Id, pr.Repository.ProjectId,
            new { commentId = root.Id, severity = root.AiSeverity, file = root.FilePath, line = root.Line }, actorId);
    }

    /// <summary>Unresolved blocking (bug or security) AI findings on the pull request.</summary>
    public async Task<int> CountBlockersAsync(int pullRequestId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await AiReviewMergeCheck.CountBlockersAsync(db, pullRequestId);
    }

    // ---------- PR description ----------

    /// <summary>Streams a PR description for the given comparison. Throws <see cref="InvalidOperationException"/> on refusal.</summary>
    public async IAsyncEnumerable<string> StreamDescriptionAsync(Repository repo, CompareResult compare, string source, string target,
        string? title, int userId, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var prompt = AiReviewLogic.BuildDescriptionPrompt(source, target, title, compare.Commits, compare.Files);
        long input = 0, output = 0, cacheRead = 0, cacheWrite = 0;
        string? stop = null;
        try
        {
            await foreach (var ev in ai.Client.Beta.Messages.CreateStreaming(new MessageCreateParams
            {
                Model = ai.Model,
                MaxTokens = 4000,
                Betas = [AnthropicBeta.ServerSideFallback2026_07_01],
                Fallbacks = new Default(),
                OutputConfig = new BetaOutputConfig { Effort = Effort.Medium },
                System = new List<BetaTextBlockParam> { new() { Text = AiReviewLogic.DescriptionSystemPrompt, CacheControl = new BetaCacheControlEphemeral() } },
                Messages = [new BetaUserMessageParam { Content = prompt }],
            }, ct))
            {
                if (ev.TryPickStart(out var start))
                {
                    input = start.Message.Usage.InputTokens;
                    cacheRead = start.Message.Usage.CacheReadInputTokens ?? 0;
                    cacheWrite = start.Message.Usage.CacheCreationInputTokens ?? 0;
                }
                else if (ev.TryPickContentBlockDelta(out var delta) && delta.Delta.TryPickText(out var text))
                {
                    yield return text.Text;
                }
                else if (ev.TryPickDelta(out var md))
                {
                    output = md.Usage.OutputTokens;
                    stop = md.Delta.StopReason?.Raw();
                }
            }
        }
        finally
        {
            await ai.RecordUsageAsync(DescriptionFeature, userId, repo.ProjectId, input, output, cacheRead, cacheWrite, CancellationToken.None);
        }
        if (stop == "refusal") throw new InvalidOperationException("Claude declined to write this description.");
        if (stop == "max_tokens") yield return "\n\n_(The description reached the length limit and was cut off.)_";
    }
}
