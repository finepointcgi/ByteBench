using System.Text;
using System.Text.Json;
using GitBench.Data;
using GitBench.Services.Webhooks;
using Microsoft.EntityFrameworkCore;

namespace GitBench.Services.Automation;

/// <summary>
/// Performs a rule's actions through the normal services of one DI scope (whose <see cref="ProjectAccess"/> already acts
/// as the bot or the rule's author), so permissions, workflow transitions, activity, notifications, webhooks and live
/// updates all apply. Each method returns a line for the run log, or throws <see cref="InvalidOperationException"/> to fail.
/// </summary>
public sealed class AutomationActions(IServiceProvider sp, IDbContextFactory<AppDbContext> dbFactory, TimeProvider clock)
{
    private IssueService Issues => sp.GetRequiredService<IssueService>();
    private LinkedWorkService LinkedWork => sp.GetRequiredService<LinkedWorkService>();
    private ProjectAccess Access => sp.GetRequiredService<ProjectAccess>();

    public static string Describe(AutomationActionType t) => t switch
    {
        AutomationActionType.Transition => "Transition issue",
        AutomationActionType.Assign => "Assign issue",
        AutomationActionType.SetPriority => "Set priority",
        AutomationActionType.AddLabel => "Add label",
        AutomationActionType.RemoveLabel => "Remove label",
        AutomationActionType.Comment => "Add comment",
        AutomationActionType.CreateSubtask => "Create sub-task",
        AutomationActionType.LinkIssue => "Link issue",
        AutomationActionType.TransitionParent => "Transition parent when all children are done",
        AutomationActionType.Notify => "Send notification",
        AutomationActionType.SendWebhook => "Send webhook",
        _ => t.ToString(),
    };

    /// <summary>Whether the action works on the current issue (and so needs one).</summary>
    public static bool NeedsIssue(AutomationActionType t) => t is not (AutomationActionType.Notify or AutomationActionType.SendWebhook);

    public static Task<Issue?> LoadIssueAsync(AppDbContext db, int issueId, CancellationToken ct = default) =>
        db.Issues.AsNoTracking().Include(i => i.Project).Include(i => i.Assignee).Include(i => i.Reporter)
            .Include(i => i.Labels).Include(i => i.WorkflowStatus).Include(i => i.Parent)
            .AsSplitQuery().FirstOrDefaultAsync(i => i.Id == issueId, ct);

    public async Task<string> ExecuteAsync(AutomationContext ctx, ActionDef a, CancellationToken ct)
    {
        if (NeedsIssue(a.Type) && ctx.Issue == null) throw new InvalidOperationException("this action needs an issue, and the trigger has none.");
        var result = a.Type switch
        {
            AutomationActionType.Transition => await TransitionAsync(ctx, ctx.Issue!, a.Status, ct),
            AutomationActionType.Assign => await AssignAsync(ctx, a, ct),
            AutomationActionType.SetPriority => await SetPriorityAsync(ctx, a),
            AutomationActionType.AddLabel or AutomationActionType.RemoveLabel => await LabelAsync(ctx, a, ct),
            AutomationActionType.Comment => await CommentAsync(ctx, a),
            AutomationActionType.CreateSubtask => await CreateSubtaskAsync(ctx, a),
            AutomationActionType.LinkIssue => await LinkAsync(ctx, a),
            AutomationActionType.TransitionParent => await TransitionParentAsync(ctx, a, ct),
            AutomationActionType.Notify => await NotifyAsync(ctx, a),
            AutomationActionType.SendWebhook => await WebhookAsync(ctx, a, ct),
            _ => throw new InvalidOperationException("unknown action."),
        };
        // Later actions (and smart values) see the issue as it is now.
        if (ctx.Issue is { } issue)
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            ctx.Issue = await LoadIssueAsync(db, issue.Id, ct) ?? issue;
        }
        return result;
    }

    private string Render(AutomationContext ctx, string? text) => SmartValues.Render(text, ctx, clock.GetUtcNow().UtcDateTime);

    private static string Key(AutomationContext ctx, Issue i) => $"{ctx.Project.Key}-{i.Number}";

    // ----- Workflow -----

    private async Task<string> TransitionAsync(AutomationContext ctx, Issue issue, string? statusName, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(statusName)) throw new InvalidOperationException("no target status chosen.");
        var wf = await WorkflowService.LoadAsync(dbFactory, issue.ProjectId);
        var name = statusName.Trim();
        var target = wf.Statuses.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase))
                     ?? (Enum.TryParse<IssueStatus>(name.Replace(" ", ""), true, out var cat) ? wf.FirstOf(cat) : null)
                     ?? throw new InvalidOperationException($"this project's workflow has no status \"{name}\".");
        if (wf.Effective(issue)?.Id == target.Id) return $"{Key(ctx, issue)} is already in {target.Name}";
        await Issues.SetWorkflowStatusAsync(issue.Id, target.Id, ctx.ActorId); // enforces the workflow's transitions
        return $"Transitioned {Key(ctx, issue)} to {target.Name}";
    }

    private async Task<string> TransitionParentAsync(AutomationContext ctx, ActionDef a, CancellationToken ct)
    {
        var issue = ctx.Issue!;
        if (issue.ParentId is not { } parentId) return $"{Key(ctx, issue)} has no parent";
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var children = await db.Issues.Where(i => i.ParentId == parentId).Select(i => i.Status).ToListAsync(ct);
        var parent = await LoadIssueAsync(db, parentId, ct) ?? throw new InvalidOperationException("the parent issue no longer exists.");
        if (children.Any(s => s != IssueStatus.Done)) return $"Not all children of {Key(ctx, parent)} are done ({children.Count(s => s == IssueStatus.Done)}/{children.Count})";
        return await TransitionAsync(ctx, parent, a.Status, ct);
    }

    // ----- Fields -----

    private async Task<string> AssignAsync(AutomationContext ctx, ActionDef a, CancellationToken ct)
    {
        var issue = ctx.Issue!;
        int? userId = a.Assign switch
        {
            AssignMode.User => a.UserId ?? throw new InvalidOperationException("no user chosen."),
            AssignMode.Reporter => issue.ReporterId,
            AssignMode.TriggerUser => ctx.Initiator?.Id ?? throw new InvalidOperationException("nobody triggered this rule."),
            AssignMode.RoundRobin => await NextRoundRobinAsync(ctx, ct),
            _ => null,
        };
        string? name = null;
        if (userId is { } uid)
        {
            if (!await Access.CanAsync(issue.ProjectId, Permission.ViewProject, uid))
                throw new InvalidOperationException("that user can't view this project.");
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            name = await db.Users.Where(u => u.Id == uid).Select(u => u.DisplayName).FirstOrDefaultAsync(ct)
                   ?? throw new InvalidOperationException("that user no longer exists.");
        }
        if (issue.AssigneeId == userId) return name == null ? $"{Key(ctx, issue)} is already unassigned" : $"{Key(ctx, issue)} is already assigned to {name}";
        await Issues.UpdateIssueAsync(issue.Id, ctx.ActorId, (i, _) =>
        {
            i.AssigneeId = userId;
            return Task.FromResult<string?>(name == null ? "removed the assignee" : $"assigned to **{name}**");
        });
        return name == null ? $"Unassigned {Key(ctx, issue)}" : $"Assigned {Key(ctx, issue)} to {name}";
    }

    /// <summary>The next Developer+ of the project (direct or through its groups) after the rule's last pick, by user id.</summary>
    private async Task<int> NextRoundRobinAsync(AutomationContext ctx, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var projectId = ctx.Project.Id;
        var chain = ctx.Project.GroupId is { } gid ? await ProjectAccess.GroupChainAsync(db, gid) : [];
        var ids = await db.ProjectMembers.Where(m => m.ProjectId == projectId && m.Role >= ProjectRole.Developer).Select(m => m.UserId)
            .Concat(db.GroupMembers.Where(m => chain.Contains(m.GroupId) && m.Role >= ProjectRole.Developer).Select(m => m.UserId))
            .Distinct().ToListAsync(ct);
        var bots = new[] { AutomationBot.UserName, Ai.AiBot.UserName };
        var now = clock.GetUtcNow();
        var candidates = (await db.Users.Where(u => ids.Contains(u.Id) && !bots.Contains(u.UserName!))
                .Select(u => new { u.Id, u.LockoutEnd }).ToListAsync(ct)) // SQLite can't compare DateTimeOffset
            .Where(u => u.LockoutEnd == null || u.LockoutEnd < now).Select(u => u.Id).Order().ToList();
        if (candidates.Count == 0) throw new InvalidOperationException("the project has no developers to assign to.");
        var last = await db.AutomationRules.Where(r => r.Id == ctx.Rule.Id).Select(r => r.RoundRobinLastUserId).FirstOrDefaultAsync(ct);
        var next = candidates.FirstOrDefault(id => id > (last ?? 0), candidates[0]);
        await db.AutomationRules.Where(r => r.Id == ctx.Rule.Id).ExecuteUpdateAsync(s => s.SetProperty(r => r.RoundRobinLastUserId, next), ct);
        return next;
    }

    private async Task<string> SetPriorityAsync(AutomationContext ctx, ActionDef a)
    {
        var issue = ctx.Issue!;
        var p = a.Priority ?? throw new InvalidOperationException("no priority chosen.");
        if (issue.Priority == p) return $"{Key(ctx, issue)} already has priority {p}";
        await Issues.UpdateIssueAsync(issue.Id, ctx.ActorId, (i, _) =>
        {
            i.Priority = p;
            return Task.FromResult<string?>($"set priority to **{p}**");
        });
        return $"Set priority of {Key(ctx, issue)} to {p}";
    }

    private async Task<string> LabelAsync(AutomationContext ctx, ActionDef a, CancellationToken ct)
    {
        var issue = ctx.Issue!;
        var name = Render(ctx, a.Label).Trim();
        if (name.Length == 0) throw new InvalidOperationException("no label chosen.");
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var chain = ctx.Project.GroupId is { } gid ? await ProjectAccess.GroupChainAsync(db, gid) : [];
        var label = (await db.Labels.AsNoTracking()
                .Where(l => l.ProjectId == issue.ProjectId || (l.GroupId != null && chain.Contains(l.GroupId.Value))).ToListAsync(ct))
            .OrderBy(l => l.ProjectId == null) // the project's own label wins over a group label of the same name
            .FirstOrDefault(l => string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"this project has no label \"{name}\".");
        var add = a.Type == AutomationActionType.AddLabel;
        var has = issue.Labels.Any(l => l.Id == label.Id);
        if (add == has) return $"{Key(ctx, issue)} {(add ? "already has" : "doesn't have")} label {label.Name}";
        await Issues.UpdateIssueAsync(issue.Id, ctx.ActorId, async (i, d) =>
        {
            if (add) i.Labels.Add(await d.Labels.FirstAsync(l => l.Id == label.Id));
            else i.Labels.RemoveAll(l => l.Id == label.Id);
            return $"{(add ? "added" : "removed")} label **{label.Name}**";
        });
        return $"{(add ? "Added" : "Removed")} label {label.Name} {(add ? "to" : "from")} {Key(ctx, issue)}";
    }

    // ----- Comments, sub-tasks, links -----

    private async Task<string> CommentAsync(AutomationContext ctx, ActionDef a)
    {
        var body = Render(ctx, a.Text).Trim();
        if (body.Length == 0) throw new InvalidOperationException("the comment is empty.");
        await Issues.AddCommentAsync(ctx.Issue!.Id, ctx.ActorId, body);
        return $"Commented on {Key(ctx, ctx.Issue!)}";
    }

    private async Task<string> CreateSubtaskAsync(AutomationContext ctx, ActionDef a)
    {
        var title = Render(ctx, a.Text).Trim();
        if (title.Length == 0) throw new InvalidOperationException("the sub-task needs a title.");
        if (title.Length > 200) title = title[..200];
        var child = await LinkedWork.CreateChildAsync(ctx.Issue!.Id, title, IssueType.Subtask, ctx.ActorId); // hierarchy rules apply
        return $"Created sub-task {ctx.Project.Key}-{child.Number} under {Key(ctx, ctx.Issue!)}";
    }

    private async Task<string> LinkAsync(AutomationContext ctx, ActionDef a)
    {
        var key = Render(ctx, a.IssueKey).Trim();
        if (key.Length == 0) throw new InvalidOperationException("no issue key given.");
        var other = await LinkedWork.FindByKeyAsync(key) ?? throw new InvalidOperationException($"issue {key} doesn't exist.");
        // The bot is trusted only inside the rule's project; a rule author's own access decides otherwise.
        if (ctx.Rule.RunAs == AutomationRunAs.AutomationBot && other.ProjectId != ctx.Project.Id)
            throw new InvalidOperationException($"{key} is in another project; rules running as the automation bot only link issues of their own project.");
        await LinkedWork.AddLinkAsync(ctx.Issue!.Id, other.Id, a.LinkType, ctx.ActorId);
        return $"Linked {Key(ctx, ctx.Issue!)} {LinkedWorkService.Phrase(a.LinkType)} {other.Key}";
    }

    // ----- Outgoing -----

    private async Task<string> NotifyAsync(AutomationContext ctx, ActionDef a)
    {
        var notifications = sp.GetRequiredService<NotificationService>();
        var issue = ctx.Issue;
        List<int> recipients = a.Notify switch
        {
            NotifyTarget.User => a.UserId is { } u ? [u] : throw new InvalidOperationException("no user chosen."),
            NotifyTarget.Assignee => issue?.AssigneeId is { } asg ? [asg] : [],
            NotifyTarget.Reporter => issue != null ? [issue.ReporterId] : [],
            NotifyTarget.Watchers => issue != null ? await notifications.GetIssueWatcherIdsAsync(issue.Id) : [],
            NotifyTarget.TriggerUser => ctx.Initiator is { } i ? [i.Id] : [],
            _ => [],
        };
        var title = Render(ctx, string.IsNullOrWhiteSpace(a.Text) ? (issue != null ? "{{issue.key}} {{issue.title}}" : "{{rule.name}}") : a.Text).Trim();
        if (title.Length == 0) title = ctx.Rule.Name;
        var url = issue != null ? $"/p/{ctx.Project.Key}/issues/{issue.Number}"
            : ctx.PullRequest is { } pr ? $"/p/{ctx.Project.Key}/repos/{pr.Repository.Slug}/pull-requests/{pr.Number}"
            : $"/p/{ctx.Project.Key}";
        var sent = await notifications.NotifyFromAutomationAsync(ctx.Project.Id, recipients, ctx.ActorId, title, url);
        return sent.Count == 0 ? "Notification had no recipients" : $"Notified {sent.Count} user{(sent.Count == 1 ? "" : "s")}";
    }

    private async Task<string> WebhookAsync(AutomationContext ctx, ActionDef a, CancellationToken ct)
    {
        var sender = sp.GetRequiredService<WebhookSender>();
        var url = (a.Url ?? "").Trim();
        if (sender.ValidateUrl(url) is { } invalid) throw new InvalidOperationException(invalid);
        var baseUrl = sp.GetRequiredService<Api.PublicBaseUrl>().Value;
        var issue = ctx.Issue;
        var payload = new
        {
            @event = "automation",
            deliveryId = Guid.NewGuid().ToString(),
            timestamp = clock.GetUtcNow(),
            rule = new { id = ctx.Rule.Id, name = ctx.Rule.Name, trigger = ctx.TriggerLabel },
            project = new { id = ctx.Project.Id, key = ctx.Project.Key, name = ctx.Project.Name },
            issue = issue == null ? null : new
            {
                id = issue.Id, key = Key(ctx, issue), title = issue.Title, type = issue.Type.ToString(), priority = issue.Priority.ToString(),
                status = Display.StatusName(issue), assignee = issue.Assignee?.UserName, labels = issue.Labels.Select(l => l.Name),
                webUrl = $"{baseUrl}/p/{ctx.Project.Key}/issues/{issue.Number}",
            },
            pullRequest = ctx.PullRequest is { } pr ? new { id = pr.Id, number = pr.Number, title = pr.Title, sourceBranch = pr.SourceBranch, targetBranch = pr.TargetBranch, state = pr.State.ToString() } : null,
            pipeline = ctx.Pipeline is { } pl ? new { id = pl.Id, status = pl.Status.ToString().ToLowerInvariant(), @ref = pl.Ref, commitSha = pl.CommitSha } : null,
            initiator = ctx.Initiator is { } u ? new { id = u.Id, username = u.UserName, name = u.DisplayName } : null,
        };
        var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload, RuleDefinition.Json));
        // Unsigned (no per-rule secret); same SSRF guard, timeout and no-redirect policy as project webhooks.
        var attempt = await sender.SendAsync(url, "automation", payload.deliveryId, "", body, redelivery: false, webhookId: 0, ct);
        if (!attempt.Success) throw new InvalidOperationException($"webhook to {new Uri(url).Host} failed: {attempt.Error}");
        return $"Sent webhook to {new Uri(url).Host} ({attempt.StatusCode}, {attempt.DurationMs} ms)";
    }
}
