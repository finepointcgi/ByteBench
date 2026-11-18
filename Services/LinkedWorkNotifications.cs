using GitBench.Data;
using Microsoft.EntityFrameworkCore;

namespace GitBench.Services;

/// <summary>Linked work hooks: parent changes and issue links notify the watchers of every issue involved.</summary>
public partial class NotificationService
{
    /// <summary>After an issue's parent changed (or it was created under a parent).</summary>
    public async Task OnHierarchyChangedAsync(HierarchyChange? change, int actorId)
    {
        if (change == null) return;
        await using var db = await dbFactory.CreateDbContextAsync();
        var issue = await LoadIssueAsync(db, change.Issue.Id);
        if (issue == null) return;

        var r = new Recipients();
        r.Add(await IssueWatchersAsync(db, issue.Id), NotificationKind.IssueHierarchyChanged);
        var parents = new[] { change.OldParentId, change.NewParentId }.OfType<int>().ToList();
        foreach (var p in parents) r.Add(await IssueWatchersAsync(db, p), NotificationKind.IssueHierarchyChanged);
        await NotifyAsync(db, r, actorId, issue.ProjectId, IssueTitle(issue), IssueUrl(issue));

        live.Publish([.. parents.Select(p => (LiveEvent)new IssueChanged(issue.ProjectId, p)),
            new IssueChanged(issue.ProjectId, issue.Id), new BoardChanged(issue.ProjectId)]);
    }

    /// <summary>After a link between two issues (maybe in different projects) was added or removed.</summary>
    public async Task OnIssueLinkChangedAsync(int sourceIssueId, int targetIssueId, int actorId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var events = new List<LiveEvent>();
        foreach (var id in new[] { sourceIssueId, targetIssueId })
        {
            var issue = await LoadIssueAsync(db, id);
            if (issue == null) continue;
            var r = new Recipients();
            r.Add(await IssueWatchersAsync(db, id), NotificationKind.IssueLinked);
            await NotifyAsync(db, r, actorId, issue.ProjectId, IssueTitle(issue), IssueUrl(issue));
            events.Add(new IssueChanged(issue.ProjectId, id));
            events.Add(new BoardChanged(issue.ProjectId));
        }
        live.Publish([.. events, new ActivityChanged()]);
    }
}
