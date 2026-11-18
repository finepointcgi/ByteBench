using System.Runtime.CompilerServices;
using Anthropic.Models.Beta;
using Anthropic.Models.Beta.Messages;
using GitBench.Data;
using GitBench.Services.Git;
using Microsoft.EntityFrameworkCore;

namespace GitBench.Services.Ai;

/// <summary>
/// AI code review of pull requests: turns a PR's merge-base diff into one or more structured-output calls, validates the
/// findings against the diff, and posts them as the GitBench AI bot (inline comments, a summary comment and, for
/// blocking findings, a "changes requested" review; it never approves). Also writes PR descriptions.
/// Runs from <see cref="AiReviewWorker"/>; the pure parts live in <see cref="AiReviewLogic"/>.
/// </summary>
public class AiReviewService(
    IDbContextFactory<AppDbContext> dbFactory, AiClient ai, PullRequestService prs, RepositoryService repos,
    AiReviewQueue queue, ILogger<AiReviewService> log, Audit.AuditLog audit)
{
    public const string ReviewFeature = "pr-review";
    public const string DescriptionFeature = "pr-description";

    public bool IsConfigured => ai.IsConfigured;

    // ---------- Queries / settings ----------

    public async Task<AiReview?> GetLatestAsync(int pullRequestId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.AiReviews.Where(r => r.PullRequestId == pullRequestId)
            .OrderByDescending(r => r.CreatedAt).ThenByDescending(r => r.Id).FirstOrDefaultAsync();
    }

    public async Task SetAutoReviewAsync(int projectId, bool enabled)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.Projects.Where(p => p.Id == projectId).ExecuteUpdateAsync(s => s.SetProperty(p => p.AiReviewEnabled, enabled));
        await audit.RecordAsync("project.settings_changed", "project", projectId, projectId, new { aiReviewEnabled = enabled });
    }

    // ---------- Queue intake ----------

    /// <summary>
    /// Turns a queued request into a <see cref="AiReviewStatus.Queued"/> row, or returns null when there is nothing to do:
    /// AI not configured, PR closed, automatic review off for the project (automatic triggers only), the head was
    /// already reviewed (automatic triggers only), or a review is already waiting or running.
    /// </summary>
    public async Task<int?> CreateQueuedAsync(int pullRequestId, AiReviewRequest request, CancellationToken ct = default)
    {
        if (!ai.IsConfigured) return null;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var pr = await db.PullRequests.Include(p => p.Repository).ThenInclude(r => r.Project)
            .FirstOrDefaultAsync(p => p.Id == pullRequestId, ct);
        if (pr == null || pr.State != PullRequestState.Open) return null;
        if (!request.IsManual && !pr.Repository.Project.AiReviewEnabled)
        {
            log.LogInformation("AI review of PR {Id} not queued: automatic review is off for {Key}", pr.Id, pr.Repository.Project.Key);
            return null;
        }
        var head = await repos.Git(pr.Repository, path => GitOps.ResolveSha(path, pr.SourceRef));
        if (head == null) return null;

        if (await db.AiReviews.AnyAsync(r => r.PullRequestId == pr.Id && (r.Status == AiReviewStatus.Queued || r.Status == AiReviewStatus.Running), ct))
        {
            log.LogInformation("AI review of PR {Id} not queued: one is already pending", pr.Id);
            return null;
        }
        if (!request.IsManual && await db.AiReviews.AnyAsync(r => r.PullRequestId == pr.Id && r.HeadSha == head, ct))
        {
            log.LogInformation("AI review of PR {Id} skipped: head {Sha} was already reviewed", pr.Id, head[..7]);
            return null;
        }

        var review = new AiReview { PullRequestId = pr.Id, HeadSha = head, Trigger = request.Trigger, RequestedById = request.UserId };
        db.AiReviews.Add(review);
        await db.SaveChangesAsync(ct);
        queue.NotifyChanged(pr.Id);
        return review.Id;
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
        queue.NotifyChanged(r.PullRequestId);
    }

    // ---------- The review ----------

    /// <summary>Runs one queued review end to end. Throws on API or Git errors; the worker records them as Failed.</summary>
    public async Task RunAsync(int reviewId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var review = await db.AiReviews.FindAsync([reviewId], ct);
        if (review == null || review.Status != AiReviewStatus.Queued) return;
        var pr = await db.PullRequests.Include(p => p.Repository).ThenInclude(r => r.Project)
            .FirstOrDefaultAsync(p => p.Id == review.PullRequestId, ct);

        async Task Finish(AiReviewStatus status, string? error = null)
        {
            review.Status = status;
            review.Error = error;
            review.CompletedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(CancellationToken.None);
            queue.NotifyChanged(review.PullRequestId);
        }

        if (pr == null || pr.State != PullRequestState.Open) { await Finish(AiReviewStatus.Skipped, "The pull request is no longer open."); return; }
        if (!ai.IsConfigured) { await Finish(AiReviewStatus.Skipped, "AI review isn't set up (set ANTHROPIC_API_KEY)."); return; }

        var changes = await prs.GetChangesAsync(pr);
        if (changes == null) { await Finish(AiReviewStatus.Failed, "The pull request's branches could not be compared."); return; }

        review.Status = AiReviewStatus.Running;
        review.HeadSha = changes.HeadSha;
        review.StartedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        queue.NotifyChanged(pr.Id);

        var plan = AiReviewLogic.Plan(changes.Files);
        review.SkippedFilesCount = plan.Skipped.Count + plan.Omitted.Count;
        review.BatchCount = plan.Batches.Count;
        if (plan.Batches.Count == 0)
        {
            await Finish(AiReviewStatus.Skipped, $"No reviewable files ({plan.Skipped.Count} skipped: " +
                string.Join(", ", plan.Skipped.Take(5).Select(s => $"{s.Path} ({s.Reason})")) + (plan.Skipped.Count > 5 ? ", …" : "") + ").");
            return;
        }

        // One call per batch; a failed batch is reported in the summary unless every batch failed.
        var outcomes = new List<BatchOutcome>();
        foreach (var batch in plan.Batches)
        {
            var user = AiReviewLogic.BuildUserPrompt(pr.Repository.Slug, pr.Number, pr.Title, pr.Description,
                pr.SourceBranch, pr.TargetBranch, changes.Commits, plan, batch);
            var result = await CallReviewAsync(user, pr.Repository.ProjectId, ct);
            outcomes.Add(new BatchOutcome(batch.Index, result.Value, result.Value == null ? result.Error : null));
        }
        if (outcomes.All(o => o.Response == null))
        {
            await Finish(AiReviewStatus.Failed, string.Join(" ", outcomes.Select(o => o.Error).Distinct()));
            return;
        }

        var responses = outcomes.Where(o => o.Response != null).Select(o => o.Response!).ToList();
        var validation = AiReviewLogic.ValidateFindings(responses.SelectMany(r => r.Findings ?? []), changes.Files);
        var verdict = AiReviewLogic.DecideVerdict(responses, validation);
        var summary = AiReviewLogic.BuildSummary(outcomes, validation, plan, verdict, ai.Model, changes.HeadSha);

        var botId = await AiBot.EnsureAsync(db, ct);
        foreach (var c in AiReviewLogic.ToInlineComments(validation))
            await prs.AddCommentAsync(pr.Id, botId, c.Body, c.Path, c.Line);
        await prs.AddCommentAsync(pr.Id, botId, summary);

        // Only "changes requested" exists besides approval, and the bot never approves. A clean re-review withdraws
        // the bot's earlier request for changes so it doesn't block the PR forever.
        try { await prs.SetReviewAsync(pr.Id, botId, verdict == "request_changes" ? ReviewState.ChangesRequested : null); }
        catch (InvalidOperationException ex) { log.LogInformation("AI review verdict not recorded on PR {Id}: {Message}", pr.Id, ex.Message); }

        review.FindingsCount = validation.Placed.Count + validation.Unplaced.Count;
        review.InlineCount = validation.Placed.Count;
        review.UnplacedCount = validation.Unplaced.Count;
        var failedParts = outcomes.Count(o => o.Response == null);
        await Finish(AiReviewStatus.Completed, failedParts > 0 ? $"{failedParts} of {outcomes.Count} parts could not be reviewed (see the summary comment)." : null);
        log.LogInformation("AI review of {Slug}#{Number} at {Sha}: {Findings} findings, {Inline} inline, verdict {Verdict}",
            pr.Repository.Slug, pr.Number, changes.HeadSha[..7], review.FindingsCount, review.InlineCount, verdict);
    }

    private async Task<AiJsonResult<AiReviewResponse>> CallReviewAsync(string user, int projectId, CancellationToken ct)
    {
        var r = await ai.CompleteJsonAsync<AiReviewResponse>(ReviewFeature, AiReviewLogic.SystemPrompt, user,
            AiReviewLogic.ResponseSchema, Effort.High, maxTokens: 32000, projectId: projectId, ct: ct);
        // Review-specific normalization on top of the generic parse: drops incomplete findings, normalizes severity and verdict.
        return AiReviewLogic.ParseReview(r.Raw, r.StopReason);
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
