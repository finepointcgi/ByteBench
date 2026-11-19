using ByteBench.Data;
using ByteBench.Services.Audit;
using Microsoft.EntityFrameworkCore;

namespace ByteBench.Services.ServiceDesk;

public enum ServiceDeskQueueFilter { WaitingOnUs, Unanswered, Open, Closed, All }

/// <summary>One row of the service desk queue.</summary>
public sealed record ServiceDeskQueueRow(int IssueId, string Key, int Number, string Title, string Status, bool IsClosed, User? Assignee,
    string RequesterName, string RequesterEmail, ServiceDeskSource Source, DateTime CreatedAt, DateTime LastRequesterActivityAt,
    DateTime? LastAgentReplyAt, bool WaitingOnUs, bool Overdue);

/// <summary>The service desk side of an issue, for the issue page: who asked, and which comments came from them.</summary>
public sealed record ServiceDeskIssueInfo(ServiceDeskTicket Ticket, IReadOnlyDictionary<int, string> RequesterNames, bool CanSeeEmail, bool EmailEnabled);

/// <summary>What the tracking page shows: status and the public conversation only (never internal comments or activity).</summary>
public sealed record TrackingView(string ProjectName, string ProjectKey, string IssueKey, string Title, string Status, bool IsClosed, DateTime CreatedAt,
    string RequesterName, string? InitialMessage, IReadOnlyList<TrackingEntry> Entries, bool CanReply);

public sealed record TrackingEntry(bool FromRequester, string Author, string Body, DateTime CreatedAt);

/// <summary>
/// Service desk settings (Maintainer+), the queue and requester details (Developer+, i.e. <see cref="Permission.Comment"/>),
/// and "reply to requester" comments, which are the only comments ever emailed to a requester. All checks go through
/// <see cref="ProjectAccess"/>.
/// </summary>
public sealed class ServiceDeskService(IDbContextFactory<AppDbContext> dbFactory, ProjectAccess access, IssueService issues, ServiceDeskMailer mailer,
    ServiceDeskAddresses addresses, AuditLog audit, LiveEvents live)
{
    public ServiceDeskAddresses Addresses => addresses;

    // ----- Settings -----

    /// <summary>The project's settings (a disabled default if none were saved). Maintainer+.</summary>
    public async Task<ServiceDeskSettings> GetSettingsAsync(int projectId)
    {
        await access.RequireRoleAsync(projectId, ProjectRole.Maintainer, "configure the service desk");
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.ServiceDeskSettings.AsNoTracking().FirstOrDefaultAsync(s => s.ProjectId == projectId) ?? await DefaultsAsync(db, projectId);
    }

    private static async Task<ServiceDeskSettings> DefaultsAsync(AppDbContext db, int projectId) => new()
    {
        ProjectId = projectId,
        // Tell requesters when their request is done.
        NotifyStatusIds = await db.WorkflowStatuses.Where(s => s.ProjectId == projectId && s.Category == IssueStatus.Done).Select(s => s.Id).ToListAsync(),
    };

    /// <summary>Whether the project accepts requests (for links and the public pages). Anyone may ask.</summary>
    public async Task<bool> IsEnabledAsync(int projectId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.ServiceDeskSettings.AnyAsync(s => s.ProjectId == projectId && s.Enabled);
    }

    public async Task SaveSettingsAsync(ServiceDeskSettings s)
    {
        await access.RequireRoleAsync(s.ProjectId, ProjectRole.Maintainer, "configure the service desk");
        var custom = string.IsNullOrWhiteSpace(s.CustomAddress) ? null : s.CustomAddress.Trim().ToLowerInvariant();
        if (custom != null)
        {
            if (!System.Net.Mail.MailAddress.TryCreate(custom, out var parsed) || parsed.Address != custom)
                throw new InvalidOperationException("The custom address isn't a valid email address.");
            if (addresses.IsOwn(custom) || !addresses.Parse(custom).IsNone)
                throw new InvalidOperationException("The custom address can't be the service desk inbox itself.");
        }
        if (s.DefaultIssueType == IssueType.Subtask) throw new InvalidOperationException("Sub-tasks need a parent, so they can't be the default type.");
        if (s.ResponseTargetHours is < 1 or > 720) throw new InvalidOperationException("The response target must be between 1 and 720 hours.");
        if (s.DefaultAssigneeId is { } a && !await access.CanAsync(s.ProjectId, Permission.EditIssues, a))
            throw new InvalidOperationException("The default assignee must be a Developer or above in this project.");

        await using var db = await dbFactory.CreateDbContextAsync();
        if (custom != null && await db.ServiceDeskSettings.AnyAsync(x => x.CustomAddress == custom && x.ProjectId != s.ProjectId))
            throw new InvalidOperationException("Another project already uses that address.");
        var validLabels = await db.Labels.Where(l => s.DefaultLabelIds.Contains(l.Id) && l.ProjectId == s.ProjectId).Select(l => l.Id).ToListAsync();
        var validStatuses = await db.WorkflowStatuses.Where(w => s.NotifyStatusIds.Contains(w.Id) && w.ProjectId == s.ProjectId).Select(w => w.Id).ToListAsync();

        var row = await db.ServiceDeskSettings.FirstOrDefaultAsync(x => x.ProjectId == s.ProjectId);
        var wasEnabled = row?.Enabled ?? false;
        if (row == null) db.ServiceDeskSettings.Add(row = new ServiceDeskSettings { ProjectId = s.ProjectId });
        row.Enabled = s.Enabled;
        row.PublicFormEnabled = s.PublicFormEnabled;
        row.CustomAddress = custom;
        row.DefaultIssueType = s.DefaultIssueType;
        row.DefaultLabelIds = validLabels;
        row.DefaultAssigneeId = s.DefaultAssigneeId;
        row.IssueTemplate = Blank(s.IssueTemplate);
        row.AutoReplyEnabled = s.AutoReplyEnabled;
        row.AutoReplyTemplate = Blank(s.AutoReplyTemplate);
        row.NotifyStatusIds = validStatuses;
        row.ResponseTargetHours = s.ResponseTargetHours;
        row.FormIntro = Blank(s.FormIntro);
        row.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        await audit.RecordAsync(wasEnabled == row.Enabled ? "service_desk.settings_updated" : row.Enabled ? "service_desk.enabled" : "service_desk.disabled",
            "project", s.ProjectId, s.ProjectId, new { row.Enabled, row.PublicFormEnabled, customAddress = row.CustomAddress, row.AutoReplyEnabled });
    }

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    // ----- Queue -----

    public async Task<List<ServiceDeskQueueRow>> GetQueueAsync(int projectId, ServiceDeskQueueFilter filter)
    {
        await access.RequireAsync(projectId, Permission.Comment);
        await using var db = await dbFactory.CreateDbContextAsync();
        var target = await db.ServiceDeskSettings.Where(s => s.ProjectId == projectId).Select(s => (int?)s.ResponseTargetHours).FirstOrDefaultAsync() ?? 24;
        var q = db.ServiceDeskTickets.AsNoTracking().Where(t => t.ProjectId == projectId);
        // "Waiting on us": nobody has answered, or the requester spoke last; for a closed issue, only if they wrote after it closed.
        q = filter switch
        {
            ServiceDeskQueueFilter.WaitingOnUs => q.Where(t => (t.LastAgentReplyAt == null || t.LastRequesterActivityAt > t.LastAgentReplyAt)
                && (t.Issue.ClosedAt == null || t.LastRequesterActivityAt > t.Issue.ClosedAt)),
            ServiceDeskQueueFilter.Unanswered => q.Where(t => t.LastAgentReplyAt == null && t.Issue.ClosedAt == null),
            ServiceDeskQueueFilter.Open => q.Where(t => t.Issue.ClosedAt == null),
            ServiceDeskQueueFilter.Closed => q.Where(t => t.Issue.ClosedAt != null),
            _ => q,
        };
        var rows = await q.Include(t => t.Contact).Include(t => t.Issue).ThenInclude(i => i.Assignee).Include(t => t.Issue).ThenInclude(i => i.WorkflowStatus)
            .OrderBy(t => t.LastRequesterActivityAt).Take(500).ToListAsync();
        var key = await db.Projects.Where(p => p.Id == projectId).Select(p => p.Key).FirstAsync();
        var now = DateTime.UtcNow;
        var list = rows.Select(t =>
        {
            var closed = t.Issue.Status.IsClosed();
            var waiting = t.WaitingOnUs && (!closed || t.Issue.ClosedAt == null || t.LastRequesterActivityAt > t.Issue.ClosedAt);
            return new ServiceDeskQueueRow(t.IssueId, $"{key}-{t.Issue.Number}", t.Issue.Number, t.Issue.Title, t.Issue.WorkflowStatus?.Name ?? t.Issue.Status.Name(),
                closed, t.Issue.Assignee, t.Contact.DisplayName, t.Contact.Email, t.Source, t.CreatedAt, t.LastRequesterActivityAt, t.LastAgentReplyAt,
                waiting, waiting && now - t.LastRequesterActivityAt > TimeSpan.FromHours(target));
        }).ToList();
        // Waiting the longest first; then most recent activity.
        return list.OrderByDescending(r => r.WaitingOnUs).ThenBy(r => r.WaitingOnUs ? r.LastRequesterActivityAt : DateTime.MaxValue)
            .ThenByDescending(r => r.LastAgentReplyAt ?? r.CreatedAt).ToList();
    }

    public async Task<int> CountWaitingAsync(int projectId)
    {
        if (!await access.CanAsync(projectId, Permission.Comment)) return 0;
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.ServiceDeskTickets.CountAsync(t => t.ProjectId == projectId
            && (t.LastAgentReplyAt == null || t.LastRequesterActivityAt > t.LastAgentReplyAt)
            && (t.Issue.ClosedAt == null || t.LastRequesterActivityAt > t.Issue.ClosedAt));
    }

    // ----- Issue page -----

    /// <summary>The ticket behind an issue, or null if it wasn't filed through the service desk (or the user can't view it).</summary>
    public async Task<ServiceDeskIssueInfo?> GetIssueInfoAsync(int issueId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var ticket = await db.ServiceDeskTickets.AsNoTracking().Include(t => t.Contact).FirstOrDefaultAsync(t => t.IssueId == issueId);
        if (ticket == null || !await access.CanAsync(ticket.ProjectId, Permission.ViewProject)) return null;
        var names = await (from c in db.Comments
                           join k in db.ServiceDeskContacts on c.ServiceDeskContactId equals k.Id
                           where c.IssueId == issueId
                           select new { c.Id, k.Name, k.Email }).ToDictionaryAsync(x => x.Id, x => string.IsNullOrWhiteSpace(x.Name) ? x.Email : x.Name!);
        var canSeeEmail = await access.CanAsync(ticket.ProjectId, Permission.Comment);
        if (!canSeeEmail) ticket.Contact.Email = ""; // requester addresses are for the team (Developer+)
        return new ServiceDeskIssueInfo(ticket, names, canSeeEmail, mailer.Enabled);
    }

    /// <summary>
    /// A "reply to requester": a comment marked public, emailed to the requester (with the reply token, so their answer
    /// threads back) and shown on the tracking page. Needs <see cref="Permission.Comment"/>. Returns whether an email was queued.
    /// </summary>
    public async Task<(Comment Comment, bool Emailed)> ReplyToRequesterAsync(int issueId, string body, int? actorId = null)
    {
        var uid = await access.ResolveUserIdAsync(actorId) ?? throw new ForbiddenException("Sign in to reply.");
        body = (body ?? "").Trim();
        if (body.Length == 0) throw new InvalidOperationException("Write a reply first.");
        await using var db = await dbFactory.CreateDbContextAsync();
        var ticket = await db.ServiceDeskTickets.Include(t => t.Contact).Include(t => t.Issue).ThenInclude(i => i.Project)
            .FirstOrDefaultAsync(t => t.IssueId == issueId) ?? throw new InvalidOperationException("This issue wasn't filed through the service desk.");
        await access.RequireAsync(ticket.ProjectId, Permission.Comment, uid);

        var comment = await issues.AddCommentAsync(issueId, uid, body, c => c.IsPublicReply = true);
        var now = DateTime.UtcNow;
        ticket.LastAgentReplyAt = now;
        ticket.FirstResponseAt ??= now;
        await db.SaveChangesAsync();
        var agent = await db.Users.Where(u => u.Id == uid).Select(u => u.DisplayName).FirstOrDefaultAsync() ?? "Support";
        var emailed = await mailer.SendReplyAsync(db, ticket, ticket.Contact, $"{ticket.Issue.Project.Key}-{ticket.Issue.Number}", comment, agent);
        await audit.RecordAsync("service_desk.reply_sent", "issue", issueId, ticket.ProjectId, new { commentId = comment.Id, to = ticket.Contact.Email, emailed }, actorId: uid);
        live.Publish(new IssueChanged(ticket.ProjectId, issueId));
        return (comment, emailed);
    }

    // ----- Tracking page (anonymous; the token is the credential) -----

    public async Task<TrackingView?> GetTrackingViewAsync(string? token)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 64) return null;
        await using var db = await dbFactory.CreateDbContextAsync();
        var t = await db.ServiceDeskTickets.AsNoTracking().Include(x => x.Contact)
            .Include(x => x.Issue).ThenInclude(i => i.Project).Include(x => x.Issue).ThenInclude(i => i.WorkflowStatus)
            .FirstOrDefaultAsync(x => x.TrackingToken == token);
        if (t == null) return null;
        var enabled = await db.ServiceDeskSettings.AnyAsync(s => s.ProjectId == t.ProjectId && s.Enabled);
        var entries = await db.Comments.AsNoTracking().Where(c => c.IssueId == t.IssueId && (c.IsPublicReply || c.ServiceDeskContactId != null))
            .OrderBy(c => c.CreatedAt).ThenBy(c => c.Id)
            .Select(c => new
            {
                c.Body, c.CreatedAt, c.IsPublicReply,
                Agent = c.Author.DisplayName,
                Requester = db.ServiceDeskContacts.Where(k => k.Id == c.ServiceDeskContactId).Select(k => k.Name ?? k.Email).FirstOrDefault(),
            }).ToListAsync();
        var i = t.Issue;
        return new TrackingView(i.Project.Name, i.Project.Key, $"{i.Project.Key}-{i.Number}", i.Title, i.WorkflowStatus?.Name ?? i.Status.Name(),
            i.Status.IsClosed(), t.CreatedAt, t.Contact.DisplayName, t.InitialMessage,
            entries.Select(e => new TrackingEntry(!e.IsPublicReply, e.IsPublicReply ? e.Agent : e.Requester ?? t.Contact.DisplayName, e.Body, e.CreatedAt)).ToList(),
            enabled);
    }
}
