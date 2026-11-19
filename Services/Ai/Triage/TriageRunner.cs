using System.Text;
using GitBench.Data;
using GitBench.Services.Git;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GitBench.Services.Ai.Triage;

/// <summary>
/// Runs one queued triage: gathers the allowed vocabulary (labels, components, Developer+ members, duplicate candidates),
/// asks the model, validates the answer against that vocabulary and stores the suggestions. Trusted system work, used
/// only by <see cref="TriageWorker"/>; people accept suggestions through <see cref="TriageService"/>.
/// </summary>
public class TriageRunner(IDbContextFactory<AppDbContext> dbFactory, ProjectAccess access, IssueService issues, ITriageModel model,
    RepoStorage storage, Audit.AuditLog audit, LiveEvents live, IOptions<TriageOptions> options, ILogger<TriageRunner> log)
{
    private readonly TriageOptions opts = options.Value;

    public async Task RunAsync(int runId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var botId = await AiBot.EnsureAsync(db, ct);
        access.ActAsSystem(botId); // trusted system work; vouches only for the bot account as the actor of auto-applied labels
        var run = await db.IssueTriageRuns.Include(r => r.Issue).ThenInclude(i => i.Project).FirstOrDefaultAsync(r => r.Id == runId, ct);
        if (run == null || run.Status != TriageRunStatus.Queued) return; // taken already, or the issue is gone
        var issue = run.Issue;
        var project = issue.Project;

        if (!project.AiTriageEnabled) { await FinishAsync(db, run, TriageRunStatus.Skipped, "AI triage was turned off for this project.", ct); return; }
        if (!model.IsAvailable) { await FinishAsync(db, run, TriageRunStatus.Skipped, "AI isn't set up on this server.", ct); return; }

        run.Status = TriageRunStatus.Running;
        await db.SaveChangesAsync(ct);
        live.Publish(new TriageChanged(project.Id, issue.Id));

        try
        {
            var ctx = await BuildContextAsync(db, project, issue, ct);
            var schema = TriageLogic.BuildSchema(ctx, opts);
            var prompt = TriageLogic.BuildPrompt(ctx, opts);
            var answer = await model.SuggestAsync(IssueAiLogic.SystemPrompt, prompt, schema, run.RequestedById, project.Id, ct);

            if (!answer.Ok || answer.Value == null)
            {
                await FinishAsync(db, run, TriageRunStatus.Failed, answer.Refused ? "The model declined to triage this issue." : "The model's answer was unusable. Try again.", ct);
                return;
            }
            if (answer.Raw.Length > opts.MaxResponseChars)
            {
                await FinishAsync(db, run, TriageRunStatus.Failed, "The model's answer was too large to use.", ct);
                return;
            }

            var result = TriageLogic.Validate(answer.Value, ctx);
            run.Summary = result.Summary;
            foreach (var s in result.Suggestions)
                run.Items.Add(new IssueTriageItem { Kind = s.Kind, TargetId = s.TargetId, Value = s.Value, Reason = s.Reason });
            run.Status = TriageRunStatus.Completed;
            run.CompletedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);

            await audit.RecordAsync("ai.triage.completed", "issue", issue.Id, project.Id,
                new { runId = run.Id, run.Trigger, suggestions = result.Suggestions.Count, kinds = result.Suggestions.Select(s => s.Kind.ToString()).Distinct() }, botId);

            if (project.AiTriageAutoApplyLabels) await AutoApplyLabelsAsync(db, run, botId, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            log.LogError(ex, "AI triage of issue {IssueId} failed", issue.Id);
            await FinishAsync(db, run, TriageRunStatus.Failed, "The AI request failed. Please try again in a moment.", CancellationToken.None);
            return;
        }
        live.Publish(new TriageChanged(project.Id, issue.Id));
    }

    private async Task FinishAsync(AppDbContext db, IssueTriageRun run, TriageRunStatus status, string message, CancellationToken ct)
    {
        run.Status = status;
        run.Error = message;
        run.CompletedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        live.Publish(new TriageChanged(run.Issue.ProjectId, run.IssueId));
    }

    // ---------------------------------------------------------------- auto-apply (labels only)

    private async Task AutoApplyLabelsAsync(AppDbContext db, IssueTriageRun run, int botId, CancellationToken ct)
    {
        foreach (var item in run.Items.Where(i => i.Kind == TriageItemKind.Label && i.State == TriageItemState.Pending).ToList())
        {
            try
            {
                await TriageService.ApplyLabelAsync(issues, run.IssueId, item.TargetId!.Value, botId, "AI triage");
                item.State = TriageItemState.AutoApplied;
                item.ResolvedAt = DateTime.UtcNow;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogWarning(ex, "Could not auto-apply label {Label} to issue {IssueId}", item.Value, run.IssueId);
            }
        }
        await db.SaveChangesAsync(ct);
        var applied = run.Items.Where(i => i.State == TriageItemState.AutoApplied).Select(i => i.Value).ToList();
        if (applied.Count > 0)
            await audit.RecordAsync("ai.triage.auto_applied", "issue", run.IssueId, run.Issue.ProjectId, new { runId = run.Id, labels = applied }, botId);
    }

    // ---------------------------------------------------------------- context

    public async Task<TriageContext> BuildContextAsync(AppDbContext db, Project project, Issue issue, CancellationToken ct)
    {
        var vocab = await TriageData.VocabularyAsync(db, project, ct);
        var currentLabels = await db.Issues.Where(i => i.Id == issue.Id).SelectMany(i => i.Labels.Select(l => l.Id)).ToListAsync(ct);
        var currentComponents = await db.Issues.Where(i => i.Id == issue.Id).SelectMany(i => i.Components.Select(c => c.Id)).ToListAsync(ct);
        var comments = await db.Comments.Where(c => c.IssueId == issue.Id && c.ServiceDeskContactId == null)
            .OrderByDescending(c => c.CreatedAt).Take(opts.MaxComments).Select(c => c.Body).ToListAsync(ct);

        // Other issues of the project, most recently touched first: the pool for duplicate candidates and for "who handled similar things".
        var pool = await db.Issues.AsNoTracking().Where(i => i.ProjectId == project.Id && i.Id != issue.Id)
            .OrderByDescending(i => i.UpdatedAt).Take(TriageData.PoolSize).ToListAsync(ct);
        var candidates = IssueAiLogic.RankCandidates(issue.Title, issue.Description, pool, opts.MaxCandidates);

        var people = await PeopleAsync(db, project, issue, pool, ct);
        return new TriageContext(project.Key, project.Name, issue.Title, issue.Description, issue.Type, issue.Priority, issue.AssigneeId,
            vocab.Labels, currentLabels.ToHashSet(), vocab.Components, currentComponents.ToHashSet(),
            people, candidates, comments, issue.Number);
    }

    /// <summary>Developer+ members (direct, or inherited from the project's groups), each with the evidence the model may weigh.</summary>
    private async Task<List<TriagePerson>> PeopleAsync(AppDbContext db, Project project, Issue issue, List<Issue> pool, CancellationToken ct)
    {
        var users = await TriageData.AssignableUsersAsync(db, project, ct);
        if (users.Count == 0) return [];

        var commitCounts = await Task.Run(() => CommitsOnRelatedPaths(db, project, issue, users), ct);
        var since = DateTime.UtcNow.AddDays(-180);
        var result = new List<TriagePerson>();
        foreach (var u in users.Take(opts.MaxPeopleInPrompt))
        {
            var theirs = pool.Where(i => i.AssigneeId == u.Id).ToList();
            var open = theirs.Count(i => !i.Status.IsClosed());
            var related = IssueAiLogic.RankCandidates(issue.Title, issue.Description, theirs.Where(i => i.UpdatedAt >= since), 3);
            var sb = new StringBuilder();
            sb.Append($"Open assigned issues: {open}.");
            if (related.Count > 0)
            {
                sb.Append(" Recent related issues: ");
                sb.Append(string.Join("; ", related.Select(r => $"{project.Key}-{r.Issue.Number} {TriageLogic.Truncate(r.Issue.Title, 80)} ({(r.Issue.Status.IsClosed() ? "closed" : "open")})")));
                sb.Append('.');
            }
            if (commitCounts.TryGetValue(u.Id, out var commits) && commits > 0)
                sb.Append($" Recent commits on files mentioned in the issue: {commits}.");
            result.Add(new TriagePerson(u.Id, u.UserName!, u.DisplayName, sb.ToString()));
        }
        return result;
    }

    /// <summary>Counts, per candidate user, recent commits on files the issue text mentions (matched by commit e-mail). Best effort.</summary>
    private Dictionary<int, int> CommitsOnRelatedPaths(AppDbContext db, Project project, Issue issue, List<User> users)
    {
        var counts = new Dictionary<int, int>();
        var paths = TriageLogic.ExtractPaths(issue.Title + "\n" + issue.Description);
        if (paths.Count == 0) return counts;
        var byEmail = users.Where(u => !string.IsNullOrEmpty(u.Email)).GroupBy(u => u.Email!.ToLowerInvariant()).ToDictionary(g => g.Key, g => g.First().Id);
        foreach (var repo in db.Repositories.AsNoTracking().Where(r => r.ProjectId == project.Id).Take(3).ToList())
        {
            var dir = storage.PathFor(project.Key, repo.Slug);
            foreach (var path in paths)
            {
                try
                {
                    foreach (var c in GitOps.Log(dir, repo.DefaultBranch, path, 0, 10).Items)
                        if (byEmail.TryGetValue(c.AuthorEmail.ToLowerInvariant(), out var uid))
                            counts[uid] = counts.GetValueOrDefault(uid) + 1;
                }
                catch (Exception ex) { log.LogDebug(ex, "No history for {Path} in {Repo}", path, repo.Slug); }
            }
        }
        return counts;
    }
}

/// <summary>Reads shared by the runner and the service: what a suggestion may refer to.</summary>
public static class TriageData
{
    /// <summary>How many recent issues are scanned for duplicate candidates and assignee evidence.</summary>
    public const int PoolSize = 1500;

    public sealed record Vocabulary(List<NamedId> Labels, List<NamedId> Components);

    /// <summary>The project's labels (including those inherited from its groups) and components.</summary>
    public static async Task<Vocabulary> VocabularyAsync(AppDbContext db, Project project, CancellationToken ct = default)
    {
        var labels = await db.Labels.AsNoTracking().Where(l => l.ProjectId == project.Id).ToListAsync(ct);
        if (project.GroupId is { } gid)
        {
            var chain = await ProjectAccess.GroupChainAsync(db, gid);
            labels.AddRange(await db.Labels.AsNoTracking().Where(l => l.GroupId != null && chain.Contains(l.GroupId.Value)).ToListAsync(ct));
        }
        var components = await db.ProjectComponents.AsNoTracking().Where(c => c.ProjectId == project.Id).OrderBy(c => c.Name).ToListAsync(ct);
        return new(labels.OrderBy(l => l.Name).Select(l => new NamedId(l.Id, l.Name)).ToList(),
            components.Select(c => new NamedId(c.Id, c.Name)).ToList());
    }

    /// <summary>Members with the Developer role or higher: directly, or through the project's group and its ancestors. Never the AI bot.</summary>
    public static async Task<List<User>> AssignableUsersAsync(AppDbContext db, Project project, CancellationToken ct = default)
    {
        var ids = await db.ProjectMembers.Where(m => m.ProjectId == project.Id && m.Role >= ProjectRole.Developer).Select(m => m.UserId).ToListAsync(ct);
        if (project.GroupId is { } gid)
        {
            var chain = await ProjectAccess.GroupChainAsync(db, gid);
            ids.AddRange(await db.GroupMembers.Where(m => chain.Contains(m.GroupId) && m.Role >= ProjectRole.Developer).Select(m => m.UserId).ToListAsync(ct));
        }
        var distinct = ids.Distinct().ToList();
        return (await db.Users.AsNoTracking().Where(u => distinct.Contains(u.Id) && u.UserName != null).OrderBy(u => u.UserName).ToListAsync(ct))
            .Where(u => !AiBot.IsBot(u)).ToList();
    }
}
