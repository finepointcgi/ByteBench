using ByteBench.Data;
using Microsoft.EntityFrameworkCore;

namespace ByteBench.Services;

public partial class NotificationService
{
    /// <summary>
    /// After <see cref="IssueService.BulkUpdateAsync"/>: new assignees start watching and every recipient (new assignees,
    /// watchers of issues whose status changed) gets ONE notification per project for the whole batch, not one per issue.
    /// Live events (issue, board, activity and notification changes) go out in a single <see cref="LiveEvents.Publish"/>.
    /// </summary>
    /// <param name="deleted">(projectId, issueId) of deleted issues: live refresh only.</param>
    /// <param name="updated">Changed issues with their state before the change.</param>
    public async Task OnIssuesBulkChangedAsync(int actorId, IReadOnlyList<(int ProjectId, int IssueId)> deleted,
        IReadOnlyList<(int IssueId, IssueSnapshot Before)> updated)
    {
        var events = new List<LiveEvent>();
        foreach (var (p, id) in deleted) events.Add(new IssueChanged(p, id));
        var projects = deleted.Select(d => d.ProjectId).ToHashSet();

        if (updated.Count > 0)
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            var ids = updated.Select(u => u.IssueId).ToList();
            var issues = await db.Issues.AsNoTracking().Include(i => i.Project).Where(i => ids.Contains(i.Id)).ToDictionaryAsync(i => i.Id);
            var watchRows = await db.Watches.Where(w => w.IssueId != null && ids.Contains(w.IssueId.Value))
                    .Select(w => new { w.UserId, IssueId = w.IssueId!.Value, w.Muted }).ToListAsync();
            var watchers = watchRows.ToLookup(w => w.IssueId, w => w.UserId); // everyone, so no duplicate watch rows are added
            var notMuted = watchRows.Where(w => !w.Muted).ToLookup(w => w.IssueId, w => w.UserId);

            // (user, project) -> issues they hear about, and the kind (assignment beats status changes).
            var byRecipient = new Dictionary<(int User, int Project), (NotificationKind Kind, List<Issue> Issues)>();
            void Add(int user, Issue issue, NotificationKind kind)
            {
                if (user == actorId) return;
                var key = (user, issue.ProjectId);
                if (!byRecipient.TryGetValue(key, out var entry)) byRecipient[key] = entry = (kind, []);
                else if (kind == NotificationKind.Assigned && entry.Kind != NotificationKind.Assigned) byRecipient[key] = entry = (kind, entry.Issues);
                if (!entry.Issues.Contains(issue)) entry.Issues.Add(issue);
            }

            var newWatches = new List<Watch>();
            foreach (var (issueId, before) in updated)
            {
                if (!issues.TryGetValue(issueId, out var issue)) continue;
                projects.Add(issue.ProjectId);
                events.Add(new IssueChanged(issue.ProjectId, issueId));
                if (issue.AssigneeId is { } a && a != before.AssigneeId)
                {
                    if (!watchers[issueId].Contains(a)) newWatches.Add(new Watch { UserId = a, IssueId = issueId });
                    Add(a, issue, NotificationKind.Assigned);
                }
                if (issue.Status != before.Status)
                {
                    var kind = issue.Status.IsClosed() ? NotificationKind.IssueClosed
                        : before.Status.IsClosed() ? NotificationKind.IssueReopened
                        : NotificationKind.IssueStatusChanged;
                    foreach (var w in notMuted[issueId]) Add(w, issue, kind);
                }
            }

            if (newWatches.Count > 0)
            {
                db.Watches.AddRange(newWatches);
                try { await db.SaveChangesAsync(); }
                catch (DbUpdateException) { db.ChangeTracker.Clear(); /* raced with a concurrent watch: fine */ }
            }

            var now = DateTime.UtcNow;
            var candidates = new List<Notification>();
            foreach (var group in byRecipient.GroupBy(r => r.Key.Project))
            {
                var allowed = (await FilterRecipientsAsync(group.Key, group.Select(g => g.Key.User))).ToHashSet();
                foreach (var ((user, project), (kind, list)) in group.Select(g => (g.Key, g.Value)))
                {
                    if (!allowed.Contains(user)) continue;
                    var first = list[0];
                    var title = list.Count == 1 ? IssueTitle(first) : $"{list.Count} issues in {first.Project.Key}: {string.Join(", ", list.Take(5).Select(i => i.Key))}{(list.Count > 5 ? ", …" : "")}";
                    var url = list.Count == 1 ? IssueUrl(first) : $"/p/{first.Project.Key}/issues?open=";
                    candidates.Add(new Notification
                    {
                        UserId = user, ProjectId = project, ActorId = actorId, Kind = kind,
                        Title = title, Url = url, CreatedAt = now,
                    });
                }
            }
            var notified = await DeliverAsync(db, candidates); // publishes NotificationsChanged itself
            log.LogDebug("Bulk change of {Count} issue(s) notified {Users} user(s)", updated.Count, notified.Count);
        }

        foreach (var p in projects) events.Add(new BoardChanged(p));
        events.Add(new ActivityChanged());
        live.Publish(events.ToArray());
    }
}
