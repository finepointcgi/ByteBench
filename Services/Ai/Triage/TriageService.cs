using GitBench.Data;
using GitBench.Services.Fields;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GitBench.Services.Ai.Triage;

/// <summary>Thrown when a manual request exceeds a rate limit (the API answers 429).</summary>
public class TriageLimitException(string message) : InvalidOperationException(message);

/// <summary>What the "AI suggestions" panel shows for one issue.</summary>
public sealed record TriageView(IssueTriageRun? Run, bool Enabled, bool Available, bool CanEdit)
{
    public bool InProgress => Run is { Status: TriageRunStatus.Queued or TriageRunStatus.Running };
    public IEnumerable<IssueTriageItem> Pending => Run?.Items.Where(i => i.State == TriageItemState.Pending) ?? [];
}

/// <summary>
/// The user-facing side of AI triage: settings, requesting suggestions, reading them, and accepting or dismissing each one.
/// Accepting always goes through <see cref="IssueService"/> (and the component and link services), so permissions, activity,
/// notifications, workflow rules, webhooks and automation behave exactly as if the person had made the change by hand.
/// The model call itself happens in <see cref="TriageRunner"/> on a background queue.
/// </summary>
public class TriageService(IDbContextFactory<AppDbContext> dbFactory, ProjectAccess access, IssueService issues, ComponentVersionService components,
    LinkedWorkService links, Audit.AuditLog audit, ITriageModel model, TriageQueue queue, LiveEvents live, IOptions<TriageOptions> options)
{
    private readonly TriageOptions opts = options.Value;

    /// <summary>False when no model is configured; the UI hides every triage control.</summary>
    public bool IsAvailable => model.IsAvailable;

    // ---------------------------------------------------------------- settings

    /// <summary>Turns triage on or off for a project and sets whether suggested labels apply themselves. Needs Maintainer.</summary>
    public async Task SetSettingsAsync(int projectId, bool enabled, bool autoApplyLabels, int? actorId = null)
    {
        await access.RequireAsync(projectId, Permission.ManageLabelsAndMilestones, actorId);
        await using var db = await dbFactory.CreateDbContextAsync();
        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == projectId) ?? throw new InvalidOperationException("Project not found.");
        if (project.AiTriageEnabled == enabled && project.AiTriageAutoApplyLabels == autoApplyLabels) return;
        var before = new { aiTriageEnabled = project.AiTriageEnabled, autoApplyLabels = project.AiTriageAutoApplyLabels };
        project.AiTriageEnabled = enabled;
        project.AiTriageAutoApplyLabels = autoApplyLabels;
        await db.SaveChangesAsync();
        await audit.RecordAsync("project.settings_changed", "project", projectId, projectId,
            new { before, after = new { aiTriageEnabled = enabled, autoApplyLabels } }, actorId);
    }

    // ---------------------------------------------------------------- reading

    public async Task<TriageView> GetViewAsync(int issueId)
    {
        var projectId = await access.ProjectOf(d => d.Issues.Where(i => i.Id == issueId).Select(i => (int?)i.ProjectId));
        var perms = await access.GetPermissionsAsync(projectId);
        if (!perms.CanView) throw new ForbiddenException("Project not found, or you don't have access to it.");
        await using var db = await dbFactory.CreateDbContextAsync();
        var enabled = await db.Projects.Where(p => p.Id == projectId).Select(p => p.AiTriageEnabled).FirstAsync();
        var run = await db.IssueTriageRuns.AsNoTracking().Include(r => r.Items.OrderBy(i => i.Id))
            .Where(r => r.IssueId == issueId).OrderByDescending(r => r.CreatedAt).ThenByDescending(r => r.Id).FirstOrDefaultAsync();
        return new TriageView(run, enabled, model.IsAvailable, perms.Can(Permission.EditIssues));
    }

    // ---------------------------------------------------------------- requesting

    /// <summary>
    /// "Suggest": queues a manual run for the issue (Developer+). Returns the run, which is the existing one when a run is already
    /// waiting or running. Throws <see cref="TriageLimitException"/> when a rate limit is hit.
    /// </summary>
    public async Task<IssueTriageRun> RequestAsync(int issueId, int? actorId = null)
    {
        await access.RequireForIssueAsync(issueId, Permission.EditIssues, actorId);
        var actor = await access.ResolveUserIdAsync(actorId) ?? throw new ForbiddenException("Sign in to ask for suggestions.");
        await using var db = await dbFactory.CreateDbContextAsync();
        var issue = await db.Issues.Include(i => i.Project).FirstAsync(i => i.Id == issueId);
        if (!issue.Project.AiTriageEnabled) throw new InvalidOperationException("AI triage is turned off for this project.");
        if (!model.IsAvailable) throw new InvalidOperationException("AI isn't set up on this server.");

        var open = await db.IssueTriageRuns.Where(r => r.IssueId == issueId && (r.Status == TriageRunStatus.Queued || r.Status == TriageRunStatus.Running))
            .OrderByDescending(r => r.Id).FirstOrDefaultAsync();
        if (open != null) return open;

        var now = DateTime.UtcNow;
        var last = await db.IssueTriageRuns.Where(r => r.IssueId == issueId).MaxAsync(r => (DateTime?)r.CreatedAt);
        if (last is { } l && (now - l).TotalSeconds < opts.IssueCooldownSeconds)
            throw new TriageLimitException($"Suggestions for this issue were just requested. Wait {Math.Max(1, (int)Math.Ceiling(opts.IssueCooldownSeconds - (now - l).TotalSeconds))} seconds and try again.");
        var hour = now.AddHours(-1);
        if (await db.IssueTriageRuns.CountAsync(r => r.RequestedById == actor && r.Trigger == "manual" && r.CreatedAt > hour) >= opts.MaxManualRunsPerUserPerHour)
            throw new TriageLimitException($"You can ask for at most {opts.MaxManualRunsPerUserPerHour} AI suggestions per hour.");
        if (await db.IssueTriageRuns.CountAsync(r => r.Issue.ProjectId == issue.ProjectId && r.Status != TriageRunStatus.Skipped && r.CreatedAt > hour) >= opts.MaxRunsPerProjectPerHour)
            throw new TriageLimitException("This project has reached its hourly limit of AI suggestions. Try again later.");

        var run = new IssueTriageRun { IssueId = issueId, Trigger = "manual", RequestedById = actor };
        db.IssueTriageRuns.Add(run);
        await db.SaveChangesAsync();
        await audit.RecordAsync("ai.triage.requested", "issue", issueId, issue.ProjectId, new { runId = run.Id, trigger = "manual" }, actor);
        queue.Enqueue(run.Id);
        live.Publish(new TriageChanged(issue.ProjectId, issueId));
        return run;
    }

    /// <summary>
    /// Automatic trigger for a just-created issue (system work, see <see cref="TriageCreationListener"/>): queues a run when the
    /// project has triage on, a model is available, the issue is younger than <paramref name="freshFor"/> and has no run yet.
    /// A project over its hourly limit gets a Skipped run instead, so the panel can say why nothing was suggested.
    /// </summary>
    public async Task<bool> QueueForNewIssueAsync(int issueId, TimeSpan freshFor)
    {
        if (!model.IsAvailable) return false;
        await using var db = await dbFactory.CreateDbContextAsync();
        var issue = await db.Issues.AsNoTracking().Where(i => i.Id == issueId)
            .Select(i => new { i.Id, i.ProjectId, i.CreatedAt, i.Project.AiTriageEnabled }).FirstOrDefaultAsync();
        if (issue == null || !issue.AiTriageEnabled || issue.CreatedAt < DateTime.UtcNow - freshFor) return false;
        if (await db.IssueTriageRuns.AnyAsync(r => r.IssueId == issueId)) return false;

        var hour = DateTime.UtcNow.AddHours(-1);
        var limited = await db.IssueTriageRuns.CountAsync(r => r.Issue.ProjectId == issue.ProjectId && r.Status != TriageRunStatus.Skipped && r.CreatedAt > hour) >= opts.MaxRunsPerProjectPerHour;
        var run = new IssueTriageRun
        {
            IssueId = issueId, Trigger = "created",
            Status = limited ? TriageRunStatus.Skipped : TriageRunStatus.Queued,
            Error = limited ? "Skipped: this project reached its hourly limit of AI suggestions." : null,
            CompletedAt = limited ? DateTime.UtcNow : null,
        };
        db.IssueTriageRuns.Add(run);
        await db.SaveChangesAsync();
        await audit.RecordAsync(limited ? "ai.triage.rate_limited" : "ai.triage.requested", "issue", issueId, issue.ProjectId, new { runId = run.Id, trigger = "created" }, anonymous: true);
        if (!limited) queue.Enqueue(run.Id);
        live.Publish(new TriageChanged(issue.ProjectId, issueId));
        return true;
    }

    // ---------------------------------------------------------------- accept / dismiss

    /// <summary>Applies one suggestion through the normal services as the acting user (Developer+), then marks it accepted.</summary>
    public async Task AcceptAsync(int itemId, int? actorId = null)
    {
        var (item, issue) = await LoadPendingAsync(itemId);
        await access.RequireAsync(issue.ProjectId, Permission.EditIssues, actorId);
        var actor = await access.ResolveUserIdAsync(actorId) ?? throw new ForbiddenException("Sign in to apply suggestions.");

        switch (item.Kind)
        {
            case TriageItemKind.Label:
                await ApplyLabelAsync(issues, issue.Id, item.TargetId ?? 0, actor, "AI suggestion");
                break;
            case TriageItemKind.Component:
                {
                    var current = (await components.GetForIssueAsync(issue.Id)).Components.Select(c => c.Id).ToList();
                    if (!current.Contains(item.TargetId ?? 0)) current.Add(item.TargetId ?? 0);
                    await components.SetComponentsAsync(issue.Id, current, actor);
                    break;
                }
            case TriageItemKind.Priority:
                {
                    var p = IssueAiLogic.ParsePriority(item.Value) ?? throw new InvalidOperationException("That priority is no longer valid.");
                    await issues.UpdateIssueAsync(issue.Id, actor, (i, _) =>
                    {
                        if (i.Priority == p) return Task.FromResult<string?>(null);
                        i.Priority = p;
                        return Task.FromResult<string?>($"set priority to **{p}** (AI suggestion)");
                    });
                    break;
                }
            case TriageItemKind.Type:
                {
                    var t = IssueAiLogic.ParseType(item.Value);
                    if (t is not { } type || !TriageLogic.SuggestableTypes.Contains(type)) throw new InvalidOperationException("That issue type is no longer valid.");
                    await issues.UpdateIssueAsync(issue.Id, actor, (i, _) =>
                    {
                        if (i.Type == type) return Task.FromResult<string?>(null);
                        i.Type = type;
                        return Task.FromResult<string?>($"changed type to **{type}** (AI suggestion)");
                    });
                    break;
                }
            case TriageItemKind.Assignee:
                {
                    var userId = item.TargetId ?? 0;
                    await using var db = await dbFactory.CreateDbContextAsync();
                    var project = await db.Projects.AsNoTracking().FirstAsync(p => p.Id == issue.ProjectId);
                    // Re-check now: the person must still be a Developer+ member.
                    var user = (await TriageData.AssignableUsersAsync(db, project)).FirstOrDefault(u => u.Id == userId)
                        ?? throw new InvalidOperationException("That person is no longer a Developer or higher in this project.");
                    await issues.UpdateIssueAsync(issue.Id, actor, (i, _) =>
                    {
                        if (i.AssigneeId == user.Id) return Task.FromResult<string?>(null);
                        i.AssigneeId = user.Id;
                        return Task.FromResult<string?>(user.Id == actor ? "self-assigned this issue" : $"assigned to **{user.DisplayName}** (AI suggestion)");
                    });
                    break;
                }
            case TriageItemKind.Duplicate:
                await links.AddLinkAsync(issue.Id, item.TargetId ?? 0, IssueLinkRelation.Duplicates, actor);
                break;
        }

        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            await db.IssueTriageItems.Where(i => i.Id == itemId)
                .ExecuteUpdateAsync(s => s.SetProperty(i => i.State, TriageItemState.Accepted).SetProperty(i => i.ResolvedById, actor).SetProperty(i => i.ResolvedAt, DateTime.UtcNow));
        }
        await audit.RecordAsync("ai.triage.accepted", "issue", issue.Id, issue.ProjectId, new { itemId, kind = item.Kind.ToString(), value = item.Value }, actor);
        live.Publish(new TriageChanged(issue.ProjectId, issue.Id));
    }

    public async Task DismissAsync(int itemId, int? actorId = null)
    {
        var (item, issue) = await LoadPendingAsync(itemId);
        await access.RequireAsync(issue.ProjectId, Permission.EditIssues, actorId);
        var actor = await access.ResolveUserIdAsync(actorId) ?? throw new ForbiddenException("Sign in to dismiss suggestions.");
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            await db.IssueTriageItems.Where(i => i.Id == itemId)
                .ExecuteUpdateAsync(s => s.SetProperty(i => i.State, TriageItemState.Dismissed).SetProperty(i => i.ResolvedById, actor).SetProperty(i => i.ResolvedAt, DateTime.UtcNow));
        }
        await audit.RecordAsync("ai.triage.dismissed", "issue", issue.Id, issue.ProjectId, new { itemId, kind = item.Kind.ToString(), value = item.Value }, actor);
        live.Publish(new TriageChanged(issue.ProjectId, issue.Id));
    }

    private async Task<(IssueTriageItem Item, Issue Issue)> LoadPendingAsync(int itemId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var item = await db.IssueTriageItems.AsNoTracking().Include(i => i.Run).ThenInclude(r => r.Issue)
            .FirstOrDefaultAsync(i => i.Id == itemId) ?? throw new InvalidOperationException("Suggestion not found.");
        // Don't reveal suggestions of projects the caller can't see.
        if (!await access.CanAsync(item.Run.Issue.ProjectId, Permission.ViewProject)) throw new InvalidOperationException("Suggestion not found.");
        if (item.State != TriageItemState.Pending) throw new InvalidOperationException("That suggestion was already handled.");
        return (item, item.Run.Issue);
    }

    /// <summary>Adds a label of the issue's project (or its groups) to the issue; refuses anything else. Used for accepting and auto-applying.</summary>
    internal static async Task ApplyLabelAsync(IssueService issues, int issueId, int labelId, int actorId, string source)
    {
        await issues.UpdateIssueAsync(issueId, actorId, async (i, db) =>
        {
            var project = await db.Projects.AsNoTracking().FirstAsync(p => p.Id == i.ProjectId);
            var vocab = await TriageData.VocabularyAsync(db, project);
            if (vocab.Labels.FirstOrDefault(l => l.Id == labelId) is not { } allowed)
                throw new InvalidOperationException("That label doesn't exist in this project any more.");
            if (i.Labels.Any(l => l.Id == labelId)) return null;
            i.Labels.Add(await db.Labels.FirstAsync(l => l.Id == labelId));
            return $"added label **{allowed.Name}** ({source})";
        });
    }

    // ---------------------------------------------------------------- lexical duplicate hint (no AI)

    /// <summary>
    /// "Possible duplicates" while typing a title: keyword overlap with the project's issues, nothing else. No model is called
    /// and nothing is stored, so it is safe to run on every pause in typing. Only projects the caller can view.
    /// </summary>
    public async Task<List<DuplicateHint>> LexicalDuplicatesAsync(int projectId, string? title, string? description = null, int take = 5)
    {
        if (IssueAiLogic.Tokens(title).Count < 2) return [];
        if (!await access.CanAsync(projectId, Permission.ViewProject)) return [];
        await using var db = await dbFactory.CreateDbContextAsync();
        var key = await db.Projects.Where(p => p.Id == projectId).Select(p => p.Key).FirstAsync();
        var pool = await db.Issues.AsNoTracking().Where(i => i.ProjectId == projectId)
            .OrderByDescending(i => i.UpdatedAt).Take(TriageData.PoolSize).ToListAsync();
        return IssueAiLogic.RankCandidates(TriageLogic.Truncate(title, opts.MaxTitleChars), TriageLogic.Truncate(description, 1000), pool, take)
            .Where(c => c.Score >= opts.MinHintScore)
            .Select(c => new DuplicateHint(c.Issue.Number, $"{key}-{c.Issue.Number}", c.Issue.Title, c.Issue.Status.IsClosed(),
                "Similar wording"))
            .ToList();
    }
}
