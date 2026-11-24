using System.Net;
using System.Text;
using ByteBench.Data;
using ByteBench.Services.Email;
using Microsoft.EntityFrameworkCore;

namespace ByteBench.Services.Digests;

/// <summary>One line of a digest: plain text (encoded when rendered) and a site path.</summary>
public sealed record DigestItem(string Text, string Url, string? Note = null);

public sealed record DigestProjectGroup(string ProjectName, int Count, IReadOnlyList<DigestItem> Newest);

/// <summary>What one digest says. Every list holds at most <see cref="DigestBuilder.ItemsPerSection"/> items; the totals count all.</summary>
public sealed record DigestContent(User User, DigestFrequency Frequency,
    IReadOnlyList<DigestProjectGroup> Unread, int UnreadTotal,
    IReadOnlyList<DigestItem> Mentions, int MentionTotal,
    IReadOnlyList<DigestItem> Due, int DueTotal,
    IReadOnlyList<DigestItem> Reviews, int ReviewTotal,
    IReadOnlyList<DigestItem> MyPullRequests, int MyPullRequestTotal)
{
    public bool IsEmpty => UnreadTotal + MentionTotal + DueTotal + ReviewTotal + MyPullRequestTotal == 0;
}

/// <summary>
/// Builds a user's digest from the current state, as that user, at send time:
/// <list type="bullet">
/// <item>Unread notifications grouped by project (count and the newest few), with unread mentions in their own section.</item>
/// <item>Issues assigned to them that are overdue or due within 7 days.</item>
/// <item>Open pull requests waiting for their review (they are a requested reviewer and haven't reviewed it).</item>
/// <item>Their own open pull requests that are ready to merge (approved, nothing blocking or pending) or have a failing check.</item>
/// </list>
/// Only projects the user can view right now (<see cref="ProjectAccess.VisibleProjectIdsAsync"/>) contribute anything.
/// Notification groups set to Off are left out, and watcher notifications about muted issues and pull requests too.
/// </summary>
public sealed class DigestBuilder(IDbContextFactory<AppDbContext> dbFactory, ProjectAccess access, MergeGate mergeGate, ILogger<DigestBuilder> log)
{
    public const int ItemsPerSection = 5;
    public const int MaxOwnPullRequestsChecked = 20;
    public static readonly TimeSpan DueWindow = TimeSpan.FromDays(7);

    private static readonly HashSet<NotificationKind> WatcherKinds =
    [
        NotificationKind.IssueCommented, NotificationKind.IssueStatusChanged, NotificationKind.IssueClosed, NotificationKind.IssueReopened,
        NotificationKind.PullRequestCommented, NotificationKind.PullRequestReviewed, NotificationKind.PullRequestMerged,
        NotificationKind.PullRequestDeclined, NotificationKind.PullRequestReopened, NotificationKind.IssueLinked, NotificationKind.IssueHierarchyChanged,
    ];

    /// <summary>The digest for <paramref name="userId"/>, or null for a missing or deactivated account.</summary>
    public async Task<DigestContent?> BuildAsync(int userId, DigestFrequency frequency, DateTime nowUtc, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user == null || user.DeactivatedAt != null) return null;
        var prefs = (await NotificationPreferences.LoadAsync(db, [userId]))[userId];
        bool On(NotificationGroup g) => prefs.Channels.TryGetValue(g, out var c) && c != NotificationChannel.Off;
        var visible = (await access.VisibleProjectIdsAsync(userId)).ToList();

        // ----- Unread notifications (and mentions) -----
        var muted = await MutedUrlsAsync(db, userId, ct);
        var unread = (await db.Notifications.AsNoTracking().Include(n => n.Actor)
                .Where(n => n.UserId == userId && !n.IsRead && visible.Contains(n.ProjectId))
                .OrderByDescending(n => n.CreatedAt).ThenByDescending(n => n.Id).Take(1000).ToListAsync(ct))
            .Where(n => prefs.ChannelFor(n.Kind) != NotificationChannel.Off && !(WatcherKinds.Contains(n.Kind) && muted.Contains(n.Url)))
            .ToList();
        var mentions = unread.Where(n => n.Kind == NotificationKind.Mentioned).ToList();
        var others = unread.Where(n => n.Kind != NotificationKind.Mentioned).ToList();
        var projectIds = others.Select(n => n.ProjectId).Distinct().ToList();
        var projectNames = await db.Projects.AsNoTracking().Where(p => projectIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => $"{p.Name} ({p.Key})", ct);
        var groups = others.GroupBy(n => n.ProjectId)
            .Select(g => new DigestProjectGroup(projectNames.GetValueOrDefault(g.Key, "Project"), g.Count(),
                g.Take(ItemsPerSection).Select(n => new DigestItem(NotificationEmailComposer.Sentence(n), n.Url, n.CreatedAt.ToString("yyyy-MM-dd HH:mm") + " UTC")).ToList()))
            .OrderByDescending(g => g.Count).ToList();

        // ----- Assigned issues due soon or overdue -----
        List<DigestItem> due = [];
        var dueTotal = 0;
        if (On(NotificationGroup.Assigned))
        {
            var limit = nowUtc + DueWindow;
            var q = db.Issues.AsNoTracking().Where(i => i.AssigneeId == userId && i.Status != IssueStatus.Done && i.DueDate != null
                && i.DueDate <= limit && visible.Contains(i.ProjectId));
            dueTotal = await q.CountAsync(ct);
            due = (await q.OrderBy(i => i.DueDate).Take(ItemsPerSection)
                    .Select(i => new { i.Number, i.Title, i.DueDate, i.Project.Key }).ToListAsync(ct))
                .Select(i => new DigestItem($"{i.Key}-{i.Number} {i.Title}", $"/p/{i.Key}/issues/{i.Number}",
                    i.DueDate < nowUtc.Date ? $"overdue (due {i.DueDate:yyyy-MM-dd})" : $"due {i.DueDate:yyyy-MM-dd}"))
                .ToList();
        }

        // ----- Pull requests waiting for my review -----
        List<DigestItem> reviews = [];
        var reviewTotal = 0;
        if (On(NotificationGroup.ReviewRequested))
        {
            var q = db.PullRequests.AsNoTracking().Where(p => p.State == PullRequestState.Open && !p.IsDraft && p.AuthorId != userId
                && visible.Contains(p.Repository.ProjectId)
                && db.PullRequestReviewers.Any(r => r.PullRequestId == p.Id && r.UserId == userId)
                && !p.Reviews.Any(r => r.UserId == userId));
            reviewTotal = await q.CountAsync(ct);
            reviews = (await q.OrderBy(p => p.CreatedAt).Take(ItemsPerSection)
                    .Select(p => new { p.Number, p.Title, p.Repository.Slug, p.Repository.Project.Key, Author = p.Author.DisplayName }).ToListAsync(ct))
                .Select(p => new DigestItem($"{p.Slug}#{p.Number} {p.Title}", $"/p/{p.Key}/repos/{p.Slug}/pull-requests/{p.Number}", "by " + p.Author))
                .ToList();
        }

        // ----- My pull requests: ready to merge, or a failing check -----
        List<DigestItem> mine = [];
        if (On(NotificationGroup.Reviews) || On(NotificationGroup.Pipelines))
        {
            var open = await db.PullRequests.AsNoTracking().Where(p => p.State == PullRequestState.Open && p.AuthorId == userId
                    && visible.Contains(p.Repository.ProjectId))
                .OrderByDescending(p => p.UpdatedAt).Take(MaxOwnPullRequestsChecked)
                .Select(p => new { p.Id, p.Number, p.Title, p.IsDraft, p.Repository.Slug, p.Repository.Project.Key,
                    Approved = p.Reviews.Any(r => r.UserId != userId && r.State == ReviewState.Approved) })
                .ToListAsync(ct);
            foreach (var p in open)
            {
                MergeGateResult gate;
                try { gate = await mergeGate.EvaluateAsync(p.Id, null, ct); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    log.LogDebug(ex, "Digest: could not evaluate pull request {Id}", p.Id);
                    continue;
                }
                var failing = gate.External.Where(c => c.State == MergeCheckState.Failed).Select(c => c.Name).ToList();
                var ready = !p.IsDraft && !gate.Blocked && p.Approved && gate.External.All(c => c.State is MergeCheckState.Passed or MergeCheckState.Neutral);
                string? note = failing.Count > 0 && On(NotificationGroup.Pipelines) ? "failing: " + string.Join(", ", failing)
                    : ready && On(NotificationGroup.Reviews) ? "ready to merge" : null;
                if (note != null) mine.Add(new DigestItem($"{p.Slug}#{p.Number} {p.Title}", $"/p/{p.Key}/repos/{p.Slug}/pull-requests/{p.Number}", note));
            }
        }

        return new DigestContent(user, frequency,
            groups, others.Count,
            mentions.Take(ItemsPerSection).Select(n => new DigestItem(NotificationEmailComposer.Sentence(n), n.Url, n.CreatedAt.ToString("yyyy-MM-dd HH:mm") + " UTC")).ToList(), mentions.Count,
            due, dueTotal, reviews, reviewTotal, mine.Take(ItemsPerSection).ToList(), mine.Count);
    }

    /// <summary>Paths of the issues and pull requests the user muted (as <c>NotificationService</c> writes notification URLs).</summary>
    private static async Task<HashSet<string>> MutedUrlsAsync(AppDbContext db, int userId, CancellationToken ct)
    {
        var issues = await db.Watches.AsNoTracking().Where(w => w.UserId == userId && w.Muted && w.IssueId != null)
            .Join(db.Issues, w => w.IssueId, i => i.Id, (w, i) => new { i.Project.Key, i.Number }).ToListAsync(ct);
        var prs = await db.Watches.AsNoTracking().Where(w => w.UserId == userId && w.Muted && w.PullRequestId != null)
            .Join(db.PullRequests, w => w.PullRequestId, p => p.Id, (w, p) => new { p.Repository.Project.Key, p.Repository.Slug, p.Number }).ToListAsync(ct);
        return issues.Select(i => $"/p/{i.Key}/issues/{i.Number}")
            .Concat(prs.Select(p => $"/p/{p.Key}/repos/{p.Slug}/pull-requests/{p.Number}")).ToHashSet(StringComparer.Ordinal);
    }

    // =====================================================================
    // Email
    // =====================================================================

    /// <summary>
    /// The digest email: HTML and text parts, every piece of user content HTML-encoded (no Markdown, no excerpts), absolute
    /// links from <paramref name="absolute"/>, and a one-click unsubscribe (<c>List-Unsubscribe</c> + <c>List-Unsubscribe-Post</c>
    /// headers and a footer link) that turns off digests only.
    /// </summary>
    public static EmailMessage Compose(DigestContent d, Func<string, string> absolute, string unsubscribeUrl)
    {
        var period = d.Frequency == DigestFrequency.Weekly ? "weekly" : "daily";
        var counts = new List<string>();
        if (d.UnreadTotal + d.MentionTotal > 0) counts.Add($"{d.UnreadTotal + d.MentionTotal} unread");
        if (d.DueTotal > 0) counts.Add($"{d.DueTotal} due soon");
        if (d.ReviewTotal > 0) counts.Add($"{d.ReviewTotal} to review");
        var subject = $"[ByteBench] Your {period} digest" + (counts.Count > 0 ? ": " + string.Join(", ", counts) : "");

        var html = new StringBuilder();
        var text = new StringBuilder();
        html.Append("<!DOCTYPE html><html><body style=\"margin:0;padding:24px;background:#f6f7f9;font-family:-apple-system,Segoe UI,Roboto,Helvetica,Arial,sans-serif;color:#1f2328;\">");
        html.Append("<div style=\"max-width:600px;margin:0 auto;background:#ffffff;border:1px solid #d0d7de;border-radius:8px;padding:20px;\">");
        html.Append("<div style=\"font-weight:700;color:#6366f1;margin-bottom:12px;\">◆ ByteBench</div>");
        html.Append($"<p style=\"margin:0 0 12px;\">Hi {E(d.User.DisplayName)}, here is your {period} digest.</p>");
        text.Append($"Hi {d.User.DisplayName}, here is your {period} digest.\n\n");

        if (d.UnreadTotal > 0)
        {
            Heading(html, text, $"Unread notifications ({d.UnreadTotal})");
            foreach (var g in d.Unread)
            {
                html.Append($"<div style=\"margin:10px 0 4px;font-weight:600;\">{E(g.ProjectName)} · {g.Count}</div>");
                text.Append($"{g.ProjectName} ({g.Count})\n");
                Items(html, text, g.Newest, g.Count, absolute);
            }
        }
        Section(html, text, "Mentions", d.Mentions, d.MentionTotal, absolute);
        Section(html, text, "Assigned to you: overdue or due within 7 days", d.Due, d.DueTotal, absolute);
        Section(html, text, "Pull requests waiting for your review", d.Reviews, d.ReviewTotal, absolute);
        Section(html, text, "Your pull requests", d.MyPullRequests, d.MyPullRequestTotal, absolute);

        var prefs = absolute(NotificationEmailComposer.PreferencesPath);
        html.Append($"<p style=\"margin:16px 0 0;\"><a href=\"{E(absolute("/notifications"))}\" style=\"color:#4f46e5;\">Open your notifications</a></p>");
        html.Append("</div>");
        html.Append("<p style=\"max-width:600px;margin:12px auto 0;color:#656d76;font-size:12px;\">");
        html.Append($"You get this {period} digest because you turned it on in your <a href=\"{E(prefs)}\" style=\"color:#656d76;\">notification preferences</a>. ");
        html.Append($"<a href=\"{E(unsubscribeUrl)}\" style=\"color:#656d76;\">Unsubscribe from digest emails</a>.</p>");
        html.Append("</body></html>");
        text.Append($"Notifications: {absolute("/notifications")}\n\n--\n");
        text.Append($"Notification preferences: {prefs}\n");
        text.Append($"Unsubscribe from digest emails: {unsubscribeUrl}\n");

        return new EmailMessage(d.User.Email!, d.User.DisplayName, subject, html.ToString(), text.ToString())
        {
            Headers = new Dictionary<string, string>
            {
                ["List-Unsubscribe"] = $"<{unsubscribeUrl}>",
                ["List-Unsubscribe-Post"] = "List-Unsubscribe=One-Click",
                ["Auto-Submitted"] = "auto-generated",
            },
        };
    }

    private static void Heading(StringBuilder html, StringBuilder text, string title)
    {
        html.Append($"<h2 style=\"font-size:15px;margin:18px 0 6px;padding-top:12px;border-top:1px solid #eaeef2;\">{E(title)}</h2>");
        text.Append($"== {title} ==\n");
    }

    private static void Section(StringBuilder html, StringBuilder text, string title, IReadOnlyList<DigestItem> items, int total, Func<string, string> absolute)
    {
        if (total == 0) return;
        Heading(html, text, $"{title} ({total})");
        Items(html, text, items, total, absolute);
    }

    private static void Items(StringBuilder html, StringBuilder text, IReadOnlyList<DigestItem> items, int total, Func<string, string> absolute)
    {
        html.Append("<ul style=\"margin:0;padding-left:18px;\">");
        foreach (var i in items)
        {
            var url = absolute(i.Url);
            html.Append($"<li style=\"margin:4px 0;\"><a href=\"{E(url)}\" style=\"color:#4f46e5;\">{E(i.Text)}</a>");
            if (i.Note != null) html.Append($" <span style=\"color:#656d76;font-size:12px;\">{E(i.Note)}</span>");
            html.Append("</li>");
            text.Append($"- {i.Text}{(i.Note != null ? " (" + i.Note + ")" : "")}\n  {url}\n");
        }
        if (total > items.Count)
        {
            html.Append($"<li style=\"margin:4px 0;color:#656d76;\">and {total - items.Count} more</li>");
            text.Append($"- and {total - items.Count} more\n");
        }
        html.Append("</ul>");
        text.Append('\n');
    }

    private static string E(string? s) => WebUtility.HtmlEncode(s ?? "");
}
