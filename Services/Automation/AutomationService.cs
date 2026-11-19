using ByteBench.Data;
using ByteBench.Services.Audit;
using ByteBench.Services.Webhooks;
using Microsoft.EntityFrameworkCore;

namespace ByteBench.Services.Automation;

/// <summary>Where a rule lives: one project, or a group (and so every project below it).</summary>
public readonly record struct RuleOwner(int? ProjectId, int? GroupId)
{
    public static RuleOwner Project(int id) => new(id, null);
    public static RuleOwner Group(int id) => new(null, id);
}

public sealed record RuleSummary(AutomationRule Rule, RuleDefinition Definition, AutomationRun? LastRun);

public sealed record EditorUser(int Id, string Name, string UserName);

/// <summary>Choices for the rule editor: status and label names, and the users of the project or group.</summary>
public sealed record EditorOptions(IReadOnlyList<string> Statuses, IReadOnlyList<string> DoneStatuses, IReadOnlyList<string> Labels, IReadOnlyList<EditorUser> Users);

/// <summary>A ready-made rule the editor can start from.</summary>
public sealed record RuleTemplate(string Name, string Description, AutomationTrigger Trigger, Func<EditorOptions, RuleDefinition> Build);

/// <summary>
/// Managing automation rules (Maintainer+ of the project, or of the group for group rules), their log, the editor's choices,
/// templates and manual runs. Rule changes are audited (<c>automation.rule_*</c>).
/// </summary>
public sealed class AutomationService(IDbContextFactory<AppDbContext> dbFactory, ProjectAccess access, AuditLog audit, AutomationConditions conditions,
    AutomationQueue queue, AutomationRateLimiter limiter, WebhookSender webhookSender)
{
    public const int MaxRulesPerOwner = 100;

    // ----- Permissions -----

    public async Task<bool> CanManageAsync(RuleOwner owner) => owner.ProjectId is { } pid
        ? await access.GetRoleAsync(pid) >= ProjectRole.Maintainer
        : owner.GroupId is { } gid && await access.CanAsync(gid, GroupPermission.ManageGroupLabelsAndMilestones);

    private async Task RequireManageAsync(RuleOwner owner)
    {
        if (owner.ProjectId is { } pid) await access.RequireRoleAsync(pid, ProjectRole.Maintainer, "manage automation rules");
        else if (owner.GroupId is { } gid) await access.RequireAsync(gid, GroupPermission.ManageGroupLabelsAndMilestones);
        else throw new ForbiddenException("A rule belongs to a project or a group.");
    }

    private static RuleOwner OwnerOf(AutomationRule r) => new(r.ProjectId, r.GroupId);

    private async Task<AutomationRule> LoadForManageAsync(AppDbContext db, int ruleId)
    {
        var rule = await db.AutomationRules.FirstOrDefaultAsync(r => r.Id == ruleId) ?? throw new InvalidOperationException("Rule not found.");
        await RequireManageAsync(OwnerOf(rule));
        return rule;
    }

    // ----- Queries -----

    public async Task<List<RuleSummary>> ListAsync(RuleOwner owner)
    {
        await RequireManageAsync(owner);
        await using var db = await dbFactory.CreateDbContextAsync();
        var rules = await db.AutomationRules.AsNoTracking().Include(r => r.Author)
            .Where(r => owner.ProjectId != null ? r.ProjectId == owner.ProjectId : r.GroupId == owner.GroupId)
            .OrderBy(r => r.Name).ToListAsync();
        var ids = rules.Select(r => r.Id).ToList();
        var lastIds = await db.AutomationRuns.Where(r => ids.Contains(r.RuleId)).GroupBy(r => r.RuleId).Select(g => g.Max(r => r.Id)).ToListAsync();
        var last = await db.AutomationRuns.AsNoTracking().Where(r => lastIds.Contains(r.Id)).ToDictionaryAsync(r => r.RuleId);
        return rules.Select(r => new RuleSummary(r, RuleDefinition.Parse(r.Definition), last.GetValueOrDefault(r.Id))).ToList();
    }

    /// <summary>Rules of the group chain above a project (read-only in the project's list).</summary>
    public async Task<List<AutomationRule>> InheritedAsync(int projectId)
    {
        await RequireManageAsync(RuleOwner.Project(projectId));
        await using var db = await dbFactory.CreateDbContextAsync();
        var groupId = await db.Projects.Where(p => p.Id == projectId).Select(p => p.GroupId).FirstOrDefaultAsync();
        if (groupId is not { } gid) return [];
        var chain = await ProjectAccess.GroupChainAsync(db, gid);
        return await db.AutomationRules.AsNoTracking().Include(r => r.Group)
            .Where(r => r.GroupId != null && chain.Contains(r.GroupId.Value)).OrderBy(r => r.Name).ToListAsync();
    }

    public async Task<AutomationRule> GetAsync(int ruleId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var rule = await LoadForManageAsync(db, ruleId);
        db.Entry(rule).State = EntityState.Detached;
        return rule;
    }

    public async Task<List<AutomationRun>> GetRunsAsync(int ruleId, int take = 100)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        await LoadForManageAsync(db, ruleId);
        return await db.AutomationRuns.AsNoTracking().Where(r => r.RuleId == ruleId)
            .OrderByDescending(r => r.Id).Take(Math.Clamp(take, 1, 500)).ToListAsync();
    }

    public async Task<EditorOptions> GetEditorOptionsAsync(RuleOwner owner)
    {
        await RequireManageAsync(owner);
        await using var db = await dbFactory.CreateDbContextAsync();
        List<int> projectIds;
        List<int> groupIds;
        if (owner.ProjectId is { } pid)
        {
            projectIds = [pid];
            var gid = await db.Projects.Where(p => p.Id == pid).Select(p => p.GroupId).FirstOrDefaultAsync();
            groupIds = gid is { } g ? await ProjectAccess.GroupChainAsync(db, g) : [];
        }
        else
        {
            var root = owner.GroupId!.Value;
            var all = await db.Projects.Where(p => p.GroupId != null).Select(p => new { p.Id, GroupId = p.GroupId!.Value }).ToListAsync();
            projectIds = [];
            foreach (var p in all)
                if ((await ProjectAccess.GroupChainAsync(db, p.GroupId)).Contains(root)) projectIds.Add(p.Id);
            groupIds = await ProjectAccess.GroupChainAsync(db, root);
        }
        var statuses = await db.WorkflowStatuses.Where(s => projectIds.Contains(s.ProjectId)).OrderBy(s => s.Order)
            .Select(s => new { s.Name, s.Category }).ToListAsync();
        if (statuses.Count == 0) statuses = WorkflowService.DefaultStatuses().Select(s => new { s.Name, s.Category }).ToList();
        var labels = await db.Labels.Where(l => (l.ProjectId != null && projectIds.Contains(l.ProjectId.Value)) || (l.GroupId != null && groupIds.Contains(l.GroupId.Value)))
            .Select(l => l.Name).ToListAsync();
        var memberIds = await db.ProjectMembers.Where(m => projectIds.Contains(m.ProjectId)).Select(m => m.UserId)
            .Concat(db.GroupMembers.Where(m => groupIds.Contains(m.GroupId)).Select(m => m.UserId)).Distinct().ToListAsync();
        var bots = new[] { AutomationBot.UserName, Ai.AiBot.UserName };
        var users = await db.Users.Where(u => memberIds.Contains(u.Id) && !bots.Contains(u.UserName!)).OrderBy(u => u.DisplayName)
            .Select(u => new EditorUser(u.Id, u.DisplayName, u.UserName!)).ToListAsync();
        var cmp = StringComparer.OrdinalIgnoreCase;
        return new EditorOptions(
            statuses.Select(s => s.Name).Distinct(cmp).ToList(),
            statuses.Where(s => s.Category == IssueStatus.Done).Select(s => s.Name).Distinct(cmp).ToList(),
            labels.Distinct(cmp).Order(cmp).ToList(),
            users);
    }

    // ----- Changes -----

    /// <summary>Creates (<paramref name="ruleId"/> null) or updates a rule after validating it. The acting user becomes its author.</summary>
    public async Task<AutomationRule> SaveAsync(RuleOwner owner, int? ruleId, string name, bool enabled, AutomationTrigger trigger, RuleDefinition def,
        AutomationRunAs runAs, bool allowRuleTriggers)
    {
        var actor = await access.ResolveUserIdAsync() ?? throw new ForbiddenException("Sign in to manage automation rules.");
        name = (name ?? "").Trim();
        Validate(name, trigger, def);
        await using var db = await dbFactory.CreateDbContextAsync();
        AutomationRule rule;
        var now = DateTime.UtcNow;
        if (ruleId is { } id)
        {
            rule = await LoadForManageAsync(db, id);
        }
        else
        {
            await RequireManageAsync(owner);
            var count = await db.AutomationRules.CountAsync(r => owner.ProjectId != null ? r.ProjectId == owner.ProjectId : r.GroupId == owner.GroupId);
            if (count >= MaxRulesPerOwner) throw new InvalidOperationException($"A project or group can have at most {MaxRulesPerOwner} automation rules.");
            rule = new AutomationRule { ProjectId = owner.ProjectId, GroupId = owner.GroupId, CreatedAt = now };
            db.AutomationRules.Add(rule);
        }
        var scheduleChanged = rule.Trigger != trigger || RuleDefinition.Parse(rule.Definition).Trigger is var old
            && (old.Schedule, old.TimeOfDay, old.DayOfWeek, old.Hours) != (def.Trigger.Schedule, def.Trigger.TimeOfDay, def.Trigger.DayOfWeek, def.Trigger.Hours);
        rule.Name = name;
        rule.IsEnabled = enabled;
        rule.Trigger = trigger;
        rule.Definition = def.ToJson();
        rule.RunAs = runAs;
        rule.AllowRuleTriggers = allowRuleTriggers;
        rule.AuthorId = actor;
        rule.UpdatedAt = now;
        if (scheduleChanged) rule.LastScheduledRunAt = null; // the new schedule counts from now (UpdatedAt)
        await db.SaveChangesAsync();
        limiter.Reset(rule.Id);
        await audit.RecordAsync(ruleId == null ? "automation.rule_created" : "automation.rule_updated", "automation_rule", rule.Id, rule.ProjectId,
            new { rule.Name, trigger = trigger.ToString(), enabled, runAs = runAs.ToString(), allowRuleTriggers, groupId = rule.GroupId,
                actions = def.Actions.Select(a => a.Type.ToString()) }, actor);
        return rule;
    }

    public async Task SetEnabledAsync(int ruleId, bool enabled)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var rule = await LoadForManageAsync(db, ruleId);
        if (rule.IsEnabled == enabled) return;
        rule.IsEnabled = enabled;
        rule.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        await audit.RecordAsync(enabled ? "automation.rule_enabled" : "automation.rule_disabled", "automation_rule", rule.Id, rule.ProjectId,
            new { rule.Name, groupId = rule.GroupId });
    }

    public async Task DeleteAsync(int ruleId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var rule = await LoadForManageAsync(db, ruleId);
        db.AutomationRules.Remove(rule);
        await db.SaveChangesAsync();
        limiter.Reset(ruleId);
        await audit.RecordAsync("automation.rule_deleted", "automation_rule", rule.Id, rule.ProjectId,
            new { rule.Name, trigger = rule.Trigger.ToString(), groupId = rule.GroupId });
    }

    /// <summary>Throws <see cref="InvalidOperationException"/> with the first problem found.</summary>
    public void Validate(string name, AutomationTrigger trigger, RuleDefinition def)
    {
        if (name.Length is 0 or > 100) throw new InvalidOperationException("Give the rule a name (at most 100 characters).");
        if (trigger == AutomationTrigger.Scheduled && AutomationSchedule.Validate(def.Trigger) is { } scheduleError)
            throw new InvalidOperationException(scheduleError);
        if (def.Conditions.Count > RuleDefinition.MaxConditions) throw new InvalidOperationException($"A rule can have at most {RuleDefinition.MaxConditions} conditions.");
        foreach (var c in def.Conditions)
            if (conditions.Validate(c) is { } error) throw new InvalidOperationException(error);
        if (def.Actions.Count == 0) throw new InvalidOperationException("Add at least one action.");
        if (def.Actions.Count > RuleDefinition.MaxActions) throw new InvalidOperationException($"A rule can have at most {RuleDefinition.MaxActions} actions.");
        foreach (var a in def.Actions)
        {
            var what = AutomationActions.Describe(a.Type);
            string? problem = a.Type switch
            {
                AutomationActionType.Transition or AutomationActionType.TransitionParent when string.IsNullOrWhiteSpace(a.Status) => "pick a status.",
                AutomationActionType.Assign when a.Assign == AssignMode.User && a.UserId == null => "pick a user.",
                AutomationActionType.SetPriority when a.Priority == null => "pick a priority.",
                AutomationActionType.AddLabel or AutomationActionType.RemoveLabel when string.IsNullOrWhiteSpace(a.Label) => "pick a label.",
                AutomationActionType.Comment when string.IsNullOrWhiteSpace(a.Text) => "write the comment.",
                AutomationActionType.CreateSubtask when string.IsNullOrWhiteSpace(a.Text) => "give the sub-task a title.",
                AutomationActionType.LinkIssue when string.IsNullOrWhiteSpace(a.IssueKey) => "enter the issue key to link to.",
                AutomationActionType.Notify when a.Notify == NotifyTarget.User && a.UserId == null => "pick a user.",
                AutomationActionType.SendWebhook => webhookSender.ValidateUrl((a.Url ?? "").Trim()),
                _ => null,
            };
            if (problem != null) throw new InvalidOperationException($"{what}: {char.ToLowerInvariant(problem[0])}{problem[1..]}");
            if (a.Text is { Length: > 4000 }) throw new InvalidOperationException($"{what}: the text is too long (4000 characters at most).");
        }
    }

    // ----- Manual runs -----

    /// <summary>Enabled manual-trigger rules that apply to the issue's project, if the acting user may run them (EditIssues).</summary>
    public async Task<List<AutomationRule>> ManualRulesForIssueAsync(int issueId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var project = await db.Issues.Where(i => i.Id == issueId).Select(i => new { i.ProjectId, i.Project.GroupId }).FirstOrDefaultAsync();
        if (project == null || !await access.CanAsync(project.ProjectId, Permission.EditIssues)) return [];
        var chain = project.GroupId is { } gid ? await ProjectAccess.GroupChainAsync(db, gid) : [];
        return await db.AutomationRules.AsNoTracking()
            .Where(r => r.IsEnabled && r.Trigger == AutomationTrigger.Manual
                && (r.ProjectId == project.ProjectId || (r.GroupId != null && chain.Contains(r.GroupId.Value))))
            .OrderBy(r => r.Name).ToListAsync();
    }

    /// <summary>Queues a manual rule on an issue and waits (up to <paramref name="wait"/>) for its log entry; null if still running.</summary>
    public async Task<AutomationRun?> RunManualAsync(int ruleId, int issueId, TimeSpan? wait = null)
    {
        var actor = await access.ResolveUserIdAsync() ?? throw new ForbiddenException("Sign in to run automation rules.");
        if ((await ManualRulesForIssueAsync(issueId)).All(r => r.Id != ruleId))
            throw new InvalidOperationException("That rule can't be run on this issue (it may be disabled, or you can't edit the issue).");
        var done = new TaskCompletionSource<AutomationRun?>(TaskCreationOptions.RunContinuationsAsynchronously);
        queue.Enqueue(new AutomationEvent(AutomationEventKind.Manual, actor) { IssueId = issueId, RuleId = ruleId, Completion = done });
        var finished = await Task.WhenAny(done.Task, Task.Delay(wait ?? TimeSpan.FromSeconds(15)));
        return finished == done.Task ? await done.Task : null;
    }

    // ----- Templates -----

    public static readonly IReadOnlyList<RuleTemplate> Templates =
    [
        new("When a PR is merged → move linked issues to Done",
            "Issues whose keys are in the pull request's title or source branch move to the first Done status.",
            AutomationTrigger.PullRequestMerged,
            o => new RuleDefinition
            {
                Conditions = [new ConditionDef { Field = "statusCategory", Operator = "isNot", Value = nameof(IssueStatus.Done) }],
                Actions = [new ActionDef { Type = AutomationActionType.Transition, Status = o.DoneStatuses.FirstOrDefault() ?? "Done" }],
            }),
        new("When all sub-tasks are done → close the parent",
            "When a sub-task reaches Done and all its siblings are done, the parent moves to Done too.",
            AutomationTrigger.IssueTransitioned,
            o => new RuleDefinition
            {
                Trigger = new TriggerOptions { ToStatus = o.DoneStatuses.FirstOrDefault() ?? "Done" },
                Conditions = [new ConditionDef { Field = "type", Operator = "is", Value = nameof(IssueType.Subtask) }],
                Actions = [new ActionDef { Type = AutomationActionType.TransitionParent, Status = o.DoneStatuses.FirstOrDefault() ?? "Done" }],
            }),
        new("Auto-assign new bugs round-robin",
            "New unassigned bugs are assigned to the project's developers in turn.",
            AutomationTrigger.IssueCreated,
            _ => new RuleDefinition
            {
                Conditions =
                [
                    new ConditionDef { Field = "type", Operator = "is", Value = nameof(IssueType.Bug) },
                    new ConditionDef { Field = "assignee", Operator = "empty" },
                ],
                Actions = [new ActionDef { Type = AutomationActionType.Assign, Assign = AssignMode.RoundRobin }],
            }),
        new("Comment when a pipeline fails",
            "Adds a comment to the issues linked to the failing branch.",
            AutomationTrigger.PipelineFailed,
            _ => new RuleDefinition
            {
                Actions = [new ActionDef { Type = AutomationActionType.Comment, Text = "⚠️ Pipeline #{{pipeline.id}} on `{{pipeline.ref}}` failed." }],
            }),
    ];
}
