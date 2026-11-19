using System.Diagnostics;
using ByteBench.Data;
using ByteBench.Services.Git;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ByteBench.Services.Automation;

public sealed class AutomationOptions
{
    /// <summary>Turns the queue worker off (events are dropped) — rules can still be edited.</summary>
    public bool Enabled { get; set; } = true;
    /// <summary>Runs the scheduled-trigger timer.</summary>
    public bool SchedulerEnabled { get; set; } = true;
    /// <summary>How often the scheduler checks for due rules.</summary>
    public int SchedulerIntervalSeconds { get; set; } = 60;
    /// <summary>Most runs of one rule in any rolling hour; more are logged once as throttled and skipped.</summary>
    public int MaxRunsPerRulePerHour { get; set; } = 100;
    /// <summary>How many rules deep a chain of rule-triggered rules may go.</summary>
    public int MaxDepth { get; set; } = 3;
    /// <summary>Log entries kept per rule.</summary>
    public int MaxLogEntriesPerRule { get; set; } = 500;
}

/// <summary>A rule's log changed (for the log page to refresh).</summary>
public sealed record AutomationRunLogged(int RuleId) : LiveEvent;

/// <summary>Per-rule sliding-window rate limit (singleton, in memory).</summary>
public sealed class AutomationRateLimiter(TimeProvider clock, IOptionsMonitor<AutomationOptions> options)
{
    private readonly Dictionary<int, Queue<DateTime>> runs = [];
    private readonly HashSet<int> reported = [];
    private readonly Lock gate = new();

    /// <summary>
    /// Records a run of <paramref name="ruleId"/> if it is under the limit. When it isn't, <paramref name="firstRefusal"/> tells
    /// whether this is the first refusal since the rule was last allowed (so the log gets one "throttled" entry, not hundreds).
    /// </summary>
    public bool TryAcquire(int ruleId, out bool firstRefusal)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        lock (gate)
        {
            if (!runs.TryGetValue(ruleId, out var q)) runs[ruleId] = q = new();
            while (q.Count > 0 && now - q.Peek() >= TimeSpan.FromHours(1)) q.Dequeue();
            if (q.Count < Math.Max(1, options.CurrentValue.MaxRunsPerRulePerHour))
            {
                q.Enqueue(now);
                reported.Remove(ruleId);
                firstRefusal = false;
                return true;
            }
            firstRefusal = reported.Add(ruleId);
            return false;
        }
    }

    public void Reset(int ruleId)
    {
        lock (gate) { runs.Remove(ruleId); reported.Remove(ruleId); }
    }
}

/// <summary>
/// Runs automation rules (singleton). <see cref="ProcessAsync"/> handles one queued event: finds the enabled rules of the
/// event's project (and of its groups), checks each rule's trigger and the loop and rate limits, then runs it on the subject
/// issues in a fresh DI scope whose <see cref="ProjectAccess"/> acts as the automation bot or the rule's author, so every
/// action goes through the normal services (permissions, workflow transitions, notifications, webhooks). Each run is
/// logged as an <see cref="AutomationRun"/>. <see cref="RunDueScheduledAsync"/> is the scheduled trigger.
/// </summary>
public sealed partial class AutomationEngine(IServiceScopeFactory scopes, IDbContextFactory<AppDbContext> dbFactory, AutomationQueue queue,
    AutomationConditions conditions, AutomationRateLimiter limiter, TimeProvider clock, IOptionsMonitor<AutomationOptions> options,
    LiveEvents live, ILogger<AutomationEngine> log)
{
    public static string TriggerLabel(AutomationTrigger t) => t switch
    {
        AutomationTrigger.IssueCreated => "Issue created",
        AutomationTrigger.IssueTransitioned => "Issue transitioned",
        AutomationTrigger.FieldChanged => "Field changed",
        AutomationTrigger.CommentAdded => "Comment added",
        AutomationTrigger.PullRequestOpened => "Pull request opened",
        AutomationTrigger.PullRequestMerged => "Pull request merged",
        AutomationTrigger.PullRequestDeclined => "Pull request declined",
        AutomationTrigger.PipelineFailed => "Pipeline failed",
        AutomationTrigger.PipelinePassed => "Pipeline passed",
        AutomationTrigger.Scheduled => "Scheduled",
        _ => "Manual",
    };

    /// <summary>The triggers an event can start (before checking each rule's options).</summary>
    public static AutomationTrigger[] CandidateTriggers(AutomationEvent e) => e.Kind switch
    {
        AutomationEventKind.IssueCreated => [AutomationTrigger.IssueCreated],
        AutomationEventKind.IssueUpdated => [AutomationTrigger.IssueTransitioned, AutomationTrigger.FieldChanged],
        AutomationEventKind.IssueCommented => [AutomationTrigger.CommentAdded],
        AutomationEventKind.PullRequestOpened => [AutomationTrigger.PullRequestOpened],
        AutomationEventKind.PullRequestMerged => [AutomationTrigger.PullRequestMerged],
        AutomationEventKind.PullRequestDeclined => [AutomationTrigger.PullRequestDeclined],
        AutomationEventKind.PipelineCompleted => e.PipelinePassed == true ? [AutomationTrigger.PipelinePassed] : [AutomationTrigger.PipelineFailed],
        AutomationEventKind.Manual => [AutomationTrigger.Manual],
        _ => [],
    };

    /// <summary>
    /// Whether <paramref name="trigger"/> with <paramref name="options"/> fires for <paramref name="e"/>. Status names come
    /// from <paramref name="statusName"/> (workflow status id → name).
    /// </summary>
    public static bool TriggerMatches(AutomationTrigger trigger, TriggerOptions options, AutomationEvent e, Func<int?, string?> statusName)
    {
        if (!CandidateTriggers(e).Contains(trigger)) return false;
        switch (trigger)
        {
            case AutomationTrigger.IssueTransitioned:
                if (e.Changes is not { } c || !c.Fields.HasFlag(IssueField.Status)) return false;
                return NameMatches(options.FromStatus, statusName(c.FromStatusId)) && NameMatches(options.ToStatus, statusName(c.ToStatusId));
            case AutomationTrigger.FieldChanged:
                if (e.Changes is not { } fc) return false;
                var watched = options.Fields == IssueField.None ? IssueField.Assignee | IssueField.Priority | IssueField.Labels | IssueField.Sprint : options.Fields;
                return (fc.Fields & watched) != 0;
            default:
                return true;
        }
    }

    private static bool NameMatches(string? wanted, string? actual) =>
        string.IsNullOrWhiteSpace(wanted) || string.Equals(wanted.Trim(), actual, StringComparison.OrdinalIgnoreCase);

    // =====================================================================
    // Events
    // =====================================================================

    public async Task ProcessAsync(AutomationEvent e, CancellationToken ct = default)
    {
        AutomationRun? manualResult = null;
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var projectId = await ProjectOfAsync(db, e, ct);
            if (projectId is not { } pid) return;
            var project = await db.Projects.AsNoTracking().FirstAsync(p => p.Id == pid, ct);
            var rules = await RulesForAsync(db, project, CandidateTriggers(e), ct);
            if (e.Kind == AutomationEventKind.Manual) rules = rules.Where(r => r.Id == e.RuleId).ToList();
            if (rules.Count == 0) return;

            var wf = await WorkflowService.LoadAsync(dbFactory, pid);
            string? StatusName(int? id) => wf.Find(id)?.Name;

            List<int?>? subjects = null;
            foreach (var rule in rules)
            {
                var def = RuleDefinition.Parse(rule.Definition);
                if (!TriggerMatches(rule.Trigger, def.Trigger, e, StatusName)) continue;
                if (e.Depth > 0 && !rule.AllowRuleTriggers && e.Kind != AutomationEventKind.Manual) continue; // changes made by automation don't re-trigger rules
                if (e.Depth > options.CurrentValue.MaxDepth)
                {
                    await LogAsync(new AutomationRun
                    {
                        RuleId = rule.Id, ProjectId = pid, Trigger = TriggerLabel(rule.Trigger), IssueId = e.IssueId, ActorId = e.ActorId,
                        Depth = e.Depth, Status = AutomationRunStatus.LoopPrevented, StartedAt = clock.GetUtcNow().UtcDateTime,
                        Message = $"Stopped: this change came from a chain of {e.Depth} rules (the limit is {options.CurrentValue.MaxDepth}).",
                    }, ct);
                    continue;
                }
                if (!limiter.TryAcquire(rule.Id, out var firstRefusal))
                {
                    if (firstRefusal)
                        await LogAsync(new AutomationRun
                        {
                            RuleId = rule.Id, ProjectId = pid, Trigger = TriggerLabel(rule.Trigger), IssueId = e.IssueId, ActorId = e.ActorId,
                            Depth = e.Depth, Status = AutomationRunStatus.Throttled, StartedAt = clock.GetUtcNow().UtcDateTime,
                            Message = $"Skipped: this rule ran {options.CurrentValue.MaxRunsPerRulePerHour} times in the last hour. Further runs are skipped until the hour passes.",
                        }, ct);
                    continue;
                }
                subjects ??= await SubjectsAsync(db, project, e, ct);
                var run = await RunAsync(rule, def, e, project, subjects, TriggerLabel(rule.Trigger), ct);
                if (e.Kind == AutomationEventKind.Manual) manualResult = run;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            log.LogError(ex, "Automation failed for {Kind} event", e.Kind);
        }
        finally
        {
            e.Completion?.TrySetResult(manualResult);
        }
    }

    private static async Task<int?> ProjectOfAsync(AppDbContext db, AutomationEvent e, CancellationToken ct)
    {
        if (e.IssueId is { } iid) return await db.Issues.Where(i => i.Id == iid).Select(i => (int?)i.ProjectId).FirstOrDefaultAsync(ct);
        if (e.PullRequestId is { } prid) return await db.PullRequests.Where(p => p.Id == prid).Select(p => (int?)p.Repository.ProjectId).FirstOrDefaultAsync(ct);
        if (e.PipelineId is { } plid) return await db.Pipelines.Where(p => p.Id == plid).Select(p => (int?)p.Repository.ProjectId).FirstOrDefaultAsync(ct);
        return null;
    }

    /// <summary>Enabled rules of the project and of the groups above it, with one of <paramref name="triggers"/>.</summary>
    private static async Task<List<AutomationRule>> RulesForAsync(AppDbContext db, Project project, AutomationTrigger[] triggers, CancellationToken ct)
    {
        var chain = project.GroupId is { } gid ? await ProjectAccess.GroupChainAsync(db, gid) : [];
        return await db.AutomationRules.AsNoTracking()
            .Where(r => r.IsEnabled && triggers.Contains(r.Trigger)
                && (r.ProjectId == project.Id || (r.GroupId != null && chain.Contains(r.GroupId.Value))))
            .OrderBy(r => r.Id).ToListAsync(ct);
    }

    /// <summary>
    /// The issues a run acts on: the event's issue; for pull requests and pipelines every issue of the project whose key is in
    /// the PR title or source branch (pipelines: its branch, and its PR's title); a single null when there are none.
    /// </summary>
    private static async Task<List<int?>> SubjectsAsync(AppDbContext db, Project project, AutomationEvent e, CancellationToken ct)
    {
        if (e.IssueId is { } iid) return [iid];
        var texts = new List<string?>();
        if (e.PullRequestId is { } prid)
            texts.AddRange(await db.PullRequests.Where(p => p.Id == prid).Select(p => new[] { p.Title, p.SourceBranch }).FirstOrDefaultAsync(ct) ?? []);
        if (e.PipelineId is { } plid && await db.Pipelines.AsNoTracking().FirstOrDefaultAsync(p => p.Id == plid, ct) is { } pl)
        {
            texts.Add(pl.Ref);
            if (pl.PullRequestId is { } plpr)
                texts.AddRange(await db.PullRequests.Where(p => p.Id == plpr).Select(p => new[] { p.Title, p.SourceBranch }).FirstOrDefaultAsync(ct) ?? []);
        }
        var numbers = IssueKeys.Numbers(project.Key, texts).ToList();
        if (numbers.Count == 0) return [null];
        var ids = await db.Issues.Where(i => i.ProjectId == project.Id && numbers.Contains(i.Number)).OrderBy(i => i.Number).Select(i => (int?)i.Id).ToListAsync(ct);
        return ids.Count == 0 ? [null] : ids;
    }

    // =====================================================================
    // Scheduled trigger
    // =====================================================================

    /// <summary>Runs every enabled scheduled rule that is due at <paramref name="now"/> (UTC). Returns how many rules ran.</summary>
    public async Task<int> RunDueScheduledAsync(DateTime now, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var rules = await db.AutomationRules.Where(r => r.IsEnabled && r.Trigger == AutomationTrigger.Scheduled).ToListAsync(ct);
        var ran = 0;
        foreach (var rule in rules)
        {
            var def = RuleDefinition.Parse(rule.Definition);
            if (!AutomationSchedule.IsDue(def.Trigger, rule.LastScheduledRunAt, rule.UpdatedAt, now)) continue;
            // Claim the slot first, so a crash mid-run doesn't repeat it on every tick.
            rule.LastScheduledRunAt = now;
            await db.SaveChangesAsync(ct);
            ran++;
            try { await RunScheduledRuleAsync(db, rule, def, now, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { log.LogError(ex, "Scheduled automation rule {Rule} failed", rule.Id); }
        }
        return ran;
    }

    private async Task RunScheduledRuleAsync(AppDbContext db, AutomationRule rule, RuleDefinition def, DateTime now, CancellationToken ct)
    {
        List<Project> projects;
        if (rule.ProjectId is { } pid) projects = await db.Projects.AsNoTracking().Where(p => p.Id == pid).ToListAsync(ct);
        else
        {
            var all = await db.Projects.AsNoTracking().Where(p => p.GroupId != null).ToListAsync(ct);
            projects = [];
            foreach (var p in all)
                if ((await ProjectAccess.GroupChainAsync(db, p.GroupId!.Value)).Contains(rule.GroupId!.Value)) projects.Add(p);
        }
        var e = new AutomationEvent(AutomationEventKind.Manual, 0); // no person triggered it
        foreach (var project in projects)
        {
            if (!limiter.TryAcquire(rule.Id, out _)) return;
            List<int?> subjects = def.Trigger.Scope == ScheduleScope.NoIssue ? [null]
                : await db.Issues.Where(i => i.ProjectId == project.Id && i.Status != IssueStatus.Done)
                    .OrderBy(i => i.Number).Take(1000).Select(i => (int?)i.Id).ToListAsync(ct);
            if (subjects.Count == 0) continue;
            await RunAsync(rule, def, e, project, subjects, $"Scheduled ({AutomationSchedule.Describe(def.Trigger)})", ct,
                maxMatches: TriggerOptions.MaxScheduledIssues, startedAt: now);
        }
    }

    // =====================================================================
    // One run
    // =====================================================================

    /// <summary>
    /// Runs <paramref name="rule"/> for one event in one project: for each subject issue (or none), loads the context,
    /// checks the conditions and runs the actions in order, stopping at the first failing action. Logs one entry.
    /// </summary>
    private async Task<AutomationRun> RunAsync(AutomationRule rule, RuleDefinition def, AutomationEvent e, Project project, List<int?> subjects,
        string triggerLabel, CancellationToken ct, int maxMatches = int.MaxValue, DateTime? startedAt = null)
    {
        var sw = Stopwatch.StartNew();
        var run = new AutomationRun
        {
            RuleId = rule.Id, ProjectId = project.Id, Trigger = triggerLabel, ActorId = e.ActorId == 0 ? null : e.ActorId,
            PullRequestId = e.PullRequestId, Depth = e.Depth, StartedAt = startedAt ?? clock.GetUtcNow().UtcDateTime,
        };
        var lines = new List<string>();
        var errors = new List<string>();
        var matchedKeys = new List<string>();
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var sp = scope.ServiceProvider;
            var access = sp.GetRequiredService<ProjectAccess>();
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var botId = await BotIdAsync(db, ct);
            int actorId;
            if (rule.RunAs == AutomationRunAs.RuleAuthor)
            {
                if (rule.AuthorId is not { } author || !await access.CanAsync(project.Id, Permission.EditIssues, author))
                    throw new ForbiddenException("The rule runs as its author, who can no longer edit issues in this project.");
                actorId = author;
                access.ActAs(author);
            }
            else
            {
                actorId = botId;
                access.ActAsSystem(botId); // the bot is trusted, but each action stays inside the rule's project
            }

            var initiator = e.ActorId == 0 ? null : await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == e.ActorId, ct);
            var pr = e.PullRequestId is { } prid ? await db.PullRequests.AsNoTracking().Include(p => p.Repository).Include(p => p.Author).FirstOrDefaultAsync(p => p.Id == prid, ct) : null;
            var pipeline = e.PipelineId is { } plid ? await db.Pipelines.AsNoTracking().FirstOrDefaultAsync(p => p.Id == plid, ct) : null;
            var comment = e.CommentId is { } cid ? await db.Comments.AsNoTracking().FirstOrDefaultAsync(c => c.Id == cid, ct) : null;
            var actions = new AutomationActions(sp, dbFactory, clock);
            var matches = 0;

            foreach (var subject in subjects)
            {
                var ctx = new AutomationContext
                {
                    Rule = rule, Definition = def, Event = e, Project = project, PullRequest = pr, Pipeline = pipeline, Comment = comment,
                    Initiator = initiator, ActorId = actorId, TriggerLabel = triggerLabel,
                    Issue = subject is { } sid ? await AutomationActions.LoadIssueAsync(db, sid, ct) : null,
                };
                if (subject != null && ctx.Issue == null) continue; // deleted meanwhile
                if (!await conditions.EvaluateAsync(ctx, def.MatchAll, def.Conditions, ct)) continue;
                if (++matches > maxMatches) { lines.Add($"Stopped after {maxMatches} issues."); break; }
                var key = ctx.Issue == null ? null : $"{project.Key}-{ctx.Issue.Number}";
                if (key != null) matchedKeys.Add(key);
                var prefix = subjects.Count > 1 && key != null ? key + ": " : "";

                using (AutomationScope.Enter(e.Depth + 1, rule.Id))
                {
                    foreach (var action in def.Actions)
                    {
                        try
                        {
                            var text = await actions.ExecuteAsync(ctx, action, ct);
                            lines.Add(prefix + text);
                        }
                        catch (InvalidOperationException ex) // includes ForbiddenException and workflow refusals
                        {
                            errors.Add($"{prefix}{AutomationActions.Describe(action.Type)}: {ex.Message}");
                            break;
                        }
                    }
                }
            }

            if (matchedKeys.Count == 1) { run.IssueKey = matchedKeys[0]; run.IssueId = subjects.Count == 1 ? subjects[0] : null; }
            else if (subjects.Count == 1) run.IssueId = subjects[0];
            if (run.IssueId != null && run.IssueKey == null)
                run.IssueKey = await db.Issues.Where(i => i.Id == run.IssueId).Select(i => project.Key + "-" + i.Number).FirstOrDefaultAsync(ct);

            if (matches == 0)
            {
                run.Status = AutomationRunStatus.NoActions;
                run.Message = "Conditions not met; no actions performed.";
            }
            else if (errors.Count > 0)
            {
                run.Status = AutomationRunStatus.Failure;
                run.Message = string.Join(" ", errors);
            }
            else
            {
                run.Status = AutomationRunStatus.Success;
                if (matchedKeys.Count > 1) run.Message = $"Ran on {matchedKeys.Count} issues: {string.Join(", ", matchedKeys)}.";
            }
        }
        catch (InvalidOperationException ex)
        {
            run.Status = AutomationRunStatus.Failure;
            run.Message = ex.Message;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogError(ex, "Automation rule {Rule} failed", rule.Id);
            run.Status = AutomationRunStatus.Failure;
            run.Message = "Unexpected error: " + ex.Message;
        }
        run.Actions = lines.Count == 0 ? null : string.Join("\n", lines);
        if (run.Message is { Length: > 1000 } m) run.Message = m[..999] + "…";
        run.DurationMs = (int)sw.ElapsedMilliseconds;
        await LogAsync(run, ct);
        return run;
    }

    /// <summary>The automation bot's id (created on first use).</summary>
    public async Task<int> BotIdAsync(AppDbContext db, CancellationToken ct = default)
    {
        if (queue.BotId is { } id) return id;
        var created = await AutomationBot.EnsureAsync(db, ct);
        queue.BotId = created;
        return created;
    }

    private async Task LogAsync(AutomationRun run, CancellationToken ct)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            db.AutomationRuns.Add(run);
            await db.SaveChangesAsync(ct);
            var keep = Math.Max(10, options.CurrentValue.MaxLogEntriesPerRule);
            var cutoff = await db.AutomationRuns.Where(r => r.RuleId == run.RuleId).OrderByDescending(r => r.Id).Skip(keep)
                .Select(r => (long?)r.Id).FirstOrDefaultAsync(ct);
            if (cutoff is { } c) await db.AutomationRuns.Where(r => r.RuleId == run.RuleId && r.Id <= c).ExecuteDeleteAsync(ct);
            live.Publish(new AutomationRunLogged(run.RuleId));
        }
        catch (DbUpdateException ex) { log.LogWarning(ex, "Could not log automation run of rule {Rule} (deleted?)", run.RuleId); }
    }
}
