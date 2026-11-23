using ByteBench.Data;
using Microsoft.EntityFrameworkCore;

namespace ByteBench.Services;

/// <summary>Issue fields captured before an update, so <see cref="NotificationService.OnIssueUpdatedAsync"/> can tell what changed.</summary>
public readonly record struct IssueSnapshot(IssueStatus Status, int? AssigneeId, string? Description)
{
    public static IssueSnapshot Of(Issue i) => new(i.Status, i.AssigneeId, i.Description);
}

public record NotificationPage(List<Notification> Items, int Total, int Page, int PageSize)
{
    public int Pages => Math.Max(1, (Total + PageSize - 1) / PageSize);
}

/// <summary>
/// Who hears about what: in-app notifications, @mentions and watchers, plus the <see cref="LiveEvents"/> each change
/// publishes. Domain services call one <c>On…Async</c> hook at the end of a mutating method, after saving.
/// Every recipient list goes through <see cref="FilterRecipientsAsync"/>.
/// </summary>
public partial class NotificationService(IDbContextFactory<AppDbContext> dbFactory, LiveEvents live, ProjectAccess access, ILogger<NotificationService> log)
{
    public const int DropdownSize = 10;

    // =====================================================================
    // Recipient filtering
    // =====================================================================

    /// <summary>
    /// The single gate every recipient list passes through: only users who can view <paramref name="projectId"/>
    /// are notified (so a mention or watch can't leak a private project to someone outside it).
    /// </summary>
    public async Task<IReadOnlyCollection<int>> FilterRecipientsAsync(int projectId, IEnumerable<int> userIds)
    {
        var allowed = new List<int>();
        foreach (var id in userIds.Distinct())
            if (await access.CanAsync(projectId, Permission.ViewProject, id)) allowed.Add(id);
        return allowed;
    }

    // =====================================================================
    // Issue hooks
    // =====================================================================

    public async Task OnIssueCreatedAsync(int issueId, int actorId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var issue = await LoadIssueAsync(db, issueId);
        if (issue == null) return;

        await EnsureWatchAsync(db, issue.ReporterId, issueId: issueId);
        if (issue.AssigneeId is { } a) await EnsureWatchAsync(db, a, issueId: issueId);

        var r = new Recipients();
        if (issue.AssigneeId is { } assignee) r.Add(assignee, NotificationKind.Assigned);
        r.Add(await ResolveMentionsAsync(db, issue.Description), NotificationKind.Mentioned);
        await NotifyAsync(db, r, actorId, issue.ProjectId, IssueTitle(issue), IssueUrl(issue), issue.Description);

        live.Publish(new IssueChanged(issue.ProjectId, issueId), new BoardChanged(issue.ProjectId), new ActivityChanged());
    }

    /// <summary>After <see cref="IssueService.UpdateIssueAsync"/>: assignment, status and description (new mentions).</summary>
    public async Task OnIssueUpdatedAsync(int issueId, int actorId, IssueSnapshot before)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var issue = await LoadIssueAsync(db, issueId);
        if (issue == null) return;

        var r = new Recipients();
        if (issue.AssigneeId is { } assignee && assignee != before.AssigneeId)
        {
            await EnsureWatchAsync(db, assignee, issueId: issueId);
            r.Add(assignee, NotificationKind.Assigned);
        }
        if (issue.Description != before.Description)
        {
            var old = Mentions.Extract(before.Description);
            r.Add(await ResolveMentionsAsync(db, issue.Description, except: old), NotificationKind.Mentioned);
        }
        if (issue.Status != before.Status)
        {
            var kind = issue.Status.IsClosed() ? NotificationKind.IssueClosed
                : before.Status.IsClosed() ? NotificationKind.IssueReopened
                : NotificationKind.IssueStatusChanged;
            r.Add(await IssueWatchersAsync(db, issueId), kind);
        }
        await NotifyAsync(db, r, actorId, issue.ProjectId, IssueTitle(issue), IssueUrl(issue));

        live.Publish(new IssueChanged(issue.ProjectId, issueId), new BoardChanged(issue.ProjectId), new ActivityChanged());
    }

    public async Task OnIssueCommentedAsync(int issueId, int commentId, int actorId, string body)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var issue = await LoadIssueAsync(db, issueId);
        if (issue == null) return;

        await EnsureWatchAsync(db, actorId, issueId: issueId);
        var r = new Recipients();
        r.Add(await ResolveMentionsAsync(db, body), NotificationKind.Mentioned);
        r.Add(await IssueWatchersAsync(db, issueId), NotificationKind.IssueCommented);
        await NotifyAsync(db, r, actorId, issue.ProjectId, IssueTitle(issue), IssueUrl(issue), body);

        live.Publish(new IssueChanged(issue.ProjectId, issueId));
    }

    /// <summary>A comment was edited or deleted: live refresh only.</summary>
    public async Task OnIssueCommentChangedAsync(int? issueId)
    {
        if (issueId is not { } id) return;
        await using var db = await dbFactory.CreateDbContextAsync();
        var projectId = await db.Issues.Where(i => i.Id == id).Select(i => (int?)i.ProjectId).FirstOrDefaultAsync();
        if (projectId is { } p) live.Publish(new IssueChanged(p, id));
    }

    /// <summary>A card was reordered on the board (status changes also go through <see cref="OnIssueUpdatedAsync"/>).</summary>
    public async Task OnIssueMovedAsync(int issueId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var projectId = await db.Issues.Where(i => i.Id == issueId).Select(i => (int?)i.ProjectId).FirstOrDefaultAsync();
        if (projectId is { } p) live.Publish(new BoardChanged(p));
    }

    public Task OnIssueDeletedAsync(int projectId, int issueId)
    {
        live.Publish(new IssueChanged(projectId, issueId), new BoardChanged(projectId), new ActivityChanged());
        return Task.CompletedTask;
    }

    // =====================================================================
    // Pull request hooks
    // =====================================================================

    public async Task OnPullRequestCreatedAsync(int prId, int actorId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var pr = await LoadPrAsync(db, prId);
        if (pr == null) return;

        await EnsureWatchAsync(db, pr.AuthorId, pullRequestId: prId);
        var r = new Recipients();
        r.Add(await ResolveMentionsAsync(db, pr.Description), NotificationKind.Mentioned);
        await NotifyAsync(db, r, actorId, pr.Repository.ProjectId, PrTitle(pr), PrUrl(pr), pr.Description);

        live.Publish(new PullRequestChanged(prId, pr.Repository.ProjectId));
    }

    /// <summary>Title/description edited: notifies users newly mentioned in the description.</summary>
    public async Task OnPullRequestUpdatedAsync(int prId, int actorId, string? oldDescription)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var pr = await LoadPrAsync(db, prId);
        if (pr == null) return;

        var r = new Recipients();
        if (pr.Description != oldDescription)
            r.Add(await ResolveMentionsAsync(db, pr.Description, except: Mentions.Extract(oldDescription)), NotificationKind.Mentioned);
        await NotifyAsync(db, r, actorId, pr.Repository.ProjectId, PrTitle(pr), PrUrl(pr));

        live.Publish(new PullRequestChanged(prId, pr.Repository.ProjectId));
    }

    public async Task OnPullRequestCommentedAsync(int prId, int commentId, int actorId, string body)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var pr = await LoadPrAsync(db, prId);
        if (pr == null) return;

        await EnsureWatchAsync(db, actorId, pullRequestId: prId);
        var r = new Recipients();
        r.Add(await ResolveMentionsAsync(db, body), NotificationKind.Mentioned);
        r.Add(await PrWatchersAsync(db, prId), NotificationKind.PullRequestCommented);
        await NotifyAsync(db, r, actorId, pr.Repository.ProjectId, PrTitle(pr), PrUrl(pr), body);

        live.Publish(new PullRequestChanged(prId, pr.Repository.ProjectId));
    }

    /// <summary>A PR comment was edited or deleted: live refresh only.</summary>
    public async Task OnPullRequestCommentChangedAsync(int? prId)
    {
        if (prId is not { } id) return;
        await using var db = await dbFactory.CreateDbContextAsync();
        var projectId = await db.PullRequests.Where(p => p.Id == id).Select(p => (int?)p.Repository.ProjectId).FirstOrDefaultAsync();
        if (projectId is { } p) live.Publish(new PullRequestChanged(id, p));
    }

    /// <summary>A review verdict was set (or withdrawn, with <paramref name="state"/> null: live refresh only).</summary>
    public async Task OnPullRequestReviewedAsync(int prId, int actorId, ReviewState? state)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var pr = await LoadPrAsync(db, prId);
        if (pr == null) return;

        await EnsureWatchAsync(db, actorId, pullRequestId: prId);
        if (state != null)
        {
            var r = new Recipients();
            r.Add(pr.AuthorId, NotificationKind.PullRequestReviewed); // always, even if they unwatched
            r.Add(await PrWatchersAsync(db, prId), NotificationKind.PullRequestReviewed);
            await NotifyAsync(db, r, actorId, pr.Repository.ProjectId, PrTitle(pr), PrUrl(pr));
        }
        live.Publish(new PullRequestChanged(prId, pr.Repository.ProjectId));
    }

    /// <summary>Reviewers were assigned: they start watching the PR and are notified.</summary>
    public async Task OnReviewersAddedAsync(int prId, int actorId, IReadOnlyCollection<int> reviewerIds)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var pr = await LoadPrAsync(db, prId);
        if (pr == null) return;

        foreach (var id in reviewerIds) await EnsureWatchAsync(db, id, pullRequestId: prId);
        var r = new Recipients();
        r.Add(reviewerIds, NotificationKind.ReviewRequested);
        await NotifyAsync(db, r, actorId, pr.Repository.ProjectId, PrTitle(pr), PrUrl(pr));
        live.Publish(new PullRequestChanged(prId, pr.Repository.ProjectId));
    }

    public Task OnPullRequestMergedAsync(int prId, int actorId) =>
        OnPullRequestStateChangedAsync(prId, actorId, NotificationKind.PullRequestMerged);

    public Task OnPullRequestDeclinedAsync(int prId, int actorId) =>
        OnPullRequestStateChangedAsync(prId, actorId, NotificationKind.PullRequestDeclined);

    public Task OnPullRequestReopenedAsync(int prId, int actorId) =>
        OnPullRequestStateChangedAsync(prId, actorId, NotificationKind.PullRequestReopened);

    private async Task OnPullRequestStateChangedAsync(int prId, int actorId, NotificationKind kind)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var pr = await LoadPrAsync(db, prId);
        if (pr == null) return;

        var r = new Recipients();
        r.Add(await PrWatchersAsync(db, prId), kind);
        await NotifyAsync(db, r, actorId, pr.Repository.ProjectId, PrTitle(pr), PrUrl(pr));

        // A merge also writes activity onto linked issues.
        live.Publish(new PullRequestChanged(prId, pr.Repository.ProjectId), new ActivityChanged());
    }

    // =====================================================================
    // Watching
    // =====================================================================

    public async Task<bool> IsWatchingIssueAsync(int userId, int issueId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Watches.AnyAsync(w => w.UserId == userId && w.IssueId == issueId);
    }

    public async Task<bool> IsWatchingPullRequestAsync(int userId, int prId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Watches.AnyAsync(w => w.UserId == userId && w.PullRequestId == prId);
    }

    public async Task<int> CountIssueWatchersAsync(int issueId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Watches.CountAsync(w => w.IssueId == issueId);
    }

    public async Task<int> CountPullRequestWatchersAsync(int prId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Watches.CountAsync(w => w.PullRequestId == prId);
    }

    public async Task SetIssueWatchAsync(int userId, int issueId, bool watch)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        if (watch) await EnsureWatchAsync(db, userId, issueId: issueId);
        else await db.Watches.Where(w => w.UserId == userId && w.IssueId == issueId).ExecuteDeleteAsync();
    }

    public async Task SetPullRequestWatchAsync(int userId, int prId, bool watch)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        if (watch) await EnsureWatchAsync(db, userId, pullRequestId: prId);
        else await db.Watches.Where(w => w.UserId == userId && w.PullRequestId == prId).ExecuteDeleteAsync();
    }

    // =====================================================================
    // Reading notifications
    // =====================================================================

    public async Task<int> GetUnreadCountAsync(int userId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Notifications.CountAsync(n => n.UserId == userId && !n.IsRead);
    }

    public async Task<List<Notification>> GetLatestAsync(int userId, int take = DropdownSize)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Notifications.Include(n => n.Actor)
            .Where(n => n.UserId == userId)
            .OrderByDescending(n => n.CreatedAt).ThenByDescending(n => n.Id)
            .Take(take).ToListAsync();
    }

    public async Task<NotificationPage> GetPageAsync(int userId, bool unreadOnly, int page, int pageSize = 20)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var q = db.Notifications.Where(n => n.UserId == userId);
        if (unreadOnly) q = q.Where(n => !n.IsRead);
        var total = await q.CountAsync();
        page = Math.Clamp(page, 1, Math.Max(1, (total + pageSize - 1) / pageSize));
        var items = await q.Include(n => n.Actor)
            .OrderByDescending(n => n.CreatedAt).ThenByDescending(n => n.Id)
            .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync();
        return new(items, total, page, pageSize);
    }

    public async Task MarkReadAsync(int userId, int notificationId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var n = await db.Notifications.Where(x => x.Id == notificationId && x.UserId == userId && !x.IsRead)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.IsRead, true));
        if (n > 0) live.Publish(new NotificationsChanged(userId));
    }

    public async Task MarkAllReadAsync(int userId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var n = await db.Notifications.Where(x => x.UserId == userId && !x.IsRead)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.IsRead, true));
        if (n > 0) live.Publish(new NotificationsChanged(userId));
    }

    /// <summary>Phrase shown after the actor's name, e.g. "Alice Chen <b>assigned you</b> WEB-12 …".</summary>
    public static string Verb(NotificationKind kind) => kind switch
    {
        NotificationKind.Assigned => "assigned you",
        NotificationKind.Mentioned => "mentioned you in",
        NotificationKind.IssueCommented => "commented on",
        NotificationKind.IssueStatusChanged => "changed the status of",
        NotificationKind.IssueClosed => "closed",
        NotificationKind.IssueReopened => "reopened",
        NotificationKind.PullRequestCommented => "commented on",
        NotificationKind.PullRequestReviewed => "reviewed",
        NotificationKind.PullRequestMerged => "merged",
        NotificationKind.PullRequestDeclined => "declined",
        NotificationKind.PullRequestReopened => "reopened",
        NotificationKind.ReviewRequested => "requested your review on",
        NotificationKind.PipelineFailed => "reports a failed pipeline:",
        NotificationKind.IssueLinked => "changed the links of",
        NotificationKind.IssueHierarchyChanged => "changed the parent of",
        NotificationKind.Automation => "notifies you:",
        NotificationKind.MergeQueueRemoved => "removed from the merge queue:",
        NotificationKind.AutoMergeCancelled => "cancelled auto-merge:",
        NotificationKind.SecurityFindings => "found new security issues:",
        NotificationKind.DeploymentApprovalRequested => "asks you to approve a deployment:",
        NotificationKind.DeploymentApproved => "approved a deployment:",
        NotificationKind.DeploymentRejected => "rejected a deployment:",
        NotificationKind.PagesDeployed => "deployed your Pages site:",
        _ => "updated",
    };

    // =====================================================================
    // Pipelines
    // =====================================================================

    /// <summary>Tells the user who triggered a pipeline that it failed (a system notification, so no actor).</summary>
    public async Task OnPipelineFailedAsync(int projectId, int userId, string title, string url)
    {
        if ((await FilterRecipientsAsync(projectId, [userId])).Count == 0) return;
        await using var db = await dbFactory.CreateDbContextAsync();
        await DeliverAsync(db, [new Notification
        {
            UserId = userId, ProjectId = projectId, ActorId = null, Kind = NotificationKind.PipelineFailed,
            Title = title, Url = url, CreatedAt = DateTime.UtcNow,
        }]);
    }

    // =====================================================================
    // Startup
    // =====================================================================

    /// <summary>
    /// One-off at startup: loads usernames for mention highlighting, and if no watches exist yet (fresh database)
    /// auto-watches existing issues and PRs for their reporter/assignee/commenters and author/reviewers/commenters.
    /// </summary>
    public static void Initialize(AppDbContext db)
    {
        Mentions.AddKnownUsers(db.Users.Select(u => u.UserName).ToList());
        if (db.Watches.Any()) return;

        var issueWatches = db.Issues.Select(i => new { i.Id, U = i.ReporterId }).ToList()
            .Concat(db.Issues.Where(i => i.AssigneeId != null).Select(i => new { i.Id, U = i.AssigneeId!.Value }).ToList())
            .Concat(db.Comments.Select(c => new { Id = c.IssueId, U = c.AuthorId }).ToList())
            .Distinct()
            .Select(x => new Watch { UserId = x.U, IssueId = x.Id });
        var prWatches = db.PullRequests.Select(p => new { p.Id, U = p.AuthorId }).ToList()
            .Concat(db.PullRequestReviews.Select(r => new { Id = r.PullRequestId, U = r.UserId }).ToList())
            .Concat(db.PullRequestComments.Select(c => new { Id = c.PullRequestId, U = c.AuthorId }).ToList())
            .Distinct()
            .Select(x => new Watch { UserId = x.U, PullRequestId = x.Id });
        db.Watches.AddRange(issueWatches.Concat(prWatches));
        db.SaveChanges();
    }

    // =====================================================================
    // Internals
    // =====================================================================

    /// <summary>One notification per user per event; the first kind added for a user wins (assign/mention before watch).</summary>
    private sealed class Recipients
    {
        public readonly Dictionary<int, NotificationKind> ByUser = [];
        /// <summary>Every kind each user qualified for, in priority order (a user who turned one off can still get the next).</summary>
        public readonly Dictionary<int, List<NotificationKind>> AllKinds = [];
        public void Add(int userId, NotificationKind kind)
        {
            ByUser.TryAdd(userId, kind);
            if (!AllKinds.TryGetValue(userId, out var list)) AllKinds[userId] = list = [];
            if (!list.Contains(kind)) list.Add(kind);
        }
        public void Add(IEnumerable<int> userIds, NotificationKind kind)
        {
            foreach (var u in userIds) Add(u, kind);
        }
    }

    private async Task NotifyAsync(AppDbContext db, Recipients r, int actorId, int projectId, string title, string url, string? excerpt = null)
    {
        r.ByUser.Remove(actorId); // never notify people about their own actions
        if (r.ByUser.Count == 0) return;
        var allowed = await FilterRecipientsAsync(projectId, r.ByUser.Keys);
        if (allowed.Count == 0) return;

        var now = DateTime.UtcNow;
        var prefs = await NotificationPreferences.LoadAsync(db, allowed);
        var delivered = await DeliverAsync(db, allowed.Select(userId => new Notification
        {
            UserId = userId, ProjectId = projectId, ActorId = actorId,
            // The highest-priority kind the user hasn't turned off (e.g. a watcher who muted mentions still hears about the comment).
            Kind = r.AllKinds[userId].FirstOrDefault(k => prefs[userId].ChannelFor(k) != NotificationChannel.Off, r.ByUser[userId]),
            Title = title, Url = url, CreatedAt = now, Excerpt = excerpt,
        }).ToList());
        log.LogDebug("Notified {Count} user(s) about {Url}", delivered.Count, url);
    }

    /// <summary>Ids of existing users mentioned in <paramref name="markdown"/>, outside code, ignoring <paramref name="except"/>.</summary>
    private static async Task<List<int>> ResolveMentionsAsync(AppDbContext db, string? markdown, IReadOnlySet<string>? except = null)
    {
        var names = Mentions.Extract(markdown)
            .Where(n => except == null || !except.Contains(n))
            .Select(n => n.ToUpperInvariant()).Distinct().ToList();
        if (names.Count == 0) return [];
        var users = await db.Users.Where(u => u.NormalizedUserName != null && names.Contains(u.NormalizedUserName))
            .Select(u => new { u.Id, u.UserName }).ToListAsync();
        Mentions.AddKnownUsers(users.Select(u => u.UserName));
        return users.Select(u => u.Id).ToList();
    }

    private static async Task EnsureWatchAsync(AppDbContext db, int userId, int? issueId = null, int? pullRequestId = null)
    {
        var exists = issueId != null
            ? await db.Watches.AnyAsync(w => w.UserId == userId && w.IssueId == issueId)
            : await db.Watches.AnyAsync(w => w.UserId == userId && w.PullRequestId == pullRequestId);
        if (exists) return;
        db.Watches.Add(new Watch { UserId = userId, IssueId = issueId, PullRequestId = pullRequestId });
        try { await db.SaveChangesAsync(); }
        catch (DbUpdateException) { db.ChangeTracker.Clear(); /* raced with a concurrent watch: fine */ }
    }

    // Muted watchers stay watchers but hear nothing as watchers.
    private static Task<List<int>> IssueWatchersAsync(AppDbContext db, int issueId) =>
        db.Watches.Where(w => w.IssueId == issueId && !w.Muted).Select(w => w.UserId).ToListAsync();

    private static Task<List<int>> PrWatchersAsync(AppDbContext db, int prId) =>
        db.Watches.Where(w => w.PullRequestId == prId && !w.Muted).Select(w => w.UserId).ToListAsync();

    private static Task<Issue?> LoadIssueAsync(AppDbContext db, int issueId) =>
        db.Issues.AsNoTracking().Include(i => i.Project).FirstOrDefaultAsync(i => i.Id == issueId);

    private static Task<PullRequest?> LoadPrAsync(AppDbContext db, int prId) =>
        db.PullRequests.AsNoTracking().Include(p => p.Repository).ThenInclude(r => r.Project)
            .Include(p => p.SourceRepository).ThenInclude(r => r!.Project).FirstOrDefaultAsync(p => p.Id == prId);

    private static string IssueTitle(Issue i) => $"{i.Project.Key}-{i.Number} {i.Title}";
    private static string IssueUrl(Issue i) => $"/p/{i.Project.Key}/issues/{i.Number}";
    private static string PrTitle(PullRequest p) => $"{p.Repository.Slug}#{p.Number} {p.Title}" + (p.IsCrossFork ? $" (from {p.SourceLabel})" : "");
    private static string PrUrl(PullRequest p) => $"/p/{p.Repository.Project.Key}/repos/{p.Repository.Slug}/pull-requests/{p.Number}";
}
