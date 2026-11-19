using ByteBench.Data;
using Microsoft.EntityFrameworkCore;

namespace ByteBench.Services;

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

    /// <summary>
    /// After a link between two issues (maybe in different projects) was added or removed. Each side's watchers are told
    /// what changed ("WEB-5 Title · link added: blocks OPS-2"). The other issue is named only to watchers who can view its project;
    /// everyone else reads "an issue in another project", as in the activity text (<see cref="LinkedWorkService"/>).
    /// </summary>
    public async Task OnIssueLinkChangedAsync(int sourceIssueId, int targetIssueId, int actorId, IssueLinkType type, bool added)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var source = await LoadIssueAsync(db, sourceIssueId);
        var target = await LoadIssueAsync(db, targetIssueId);
        var events = new List<LiveEvent>();
        foreach (var (issue, other, outgoing) in new[] { (source, target, true), (target, source, false) })
        {
            if (issue == null) continue;
            var watchers = await IssueWatchersAsync(db, issue.Id);
            var change = $"{(added ? "link added" : "link removed")}: {LinkedWorkService.Phrase(LinkedWorkService.Relation(type, outgoing))}";
            // Split the watchers by whether they may see the other issue's key, then notify each group with its own text.
            var named = new Recipients();
            var generic = new Recipients();
            foreach (var u in watchers)
            {
                var canSee = other != null && (other.ProjectId == issue.ProjectId || await access.CanAsync(other.ProjectId, Permission.ViewProject, u));
                (canSee ? named : generic).Add(u, NotificationKind.IssueLinked);
            }
            if (other != null)
                await NotifyAsync(db, named, actorId, issue.ProjectId, WithSuffix(IssueTitle(issue), $" · {change} {other.Project.Key}-{other.Number}"), IssueUrl(issue));
            await NotifyAsync(db, generic, actorId, issue.ProjectId, WithSuffix(IssueTitle(issue), $" · {change} an issue in another project"), IssueUrl(issue));
            events.Add(new IssueChanged(issue.ProjectId, issue.Id));
            events.Add(new BoardChanged(issue.ProjectId));
        }
        live.Publish([.. events, new ActivityChanged()]);
    }

    /// <summary>Keeps <paramref name="suffix"/> whole within the 200-character title, shortening <paramref name="head"/> instead.</summary>
    private static string WithSuffix(string head, string suffix) =>
        head.Length + suffix.Length <= 200 ? head + suffix : head[..Math.Max(0, 199 - suffix.Length)] + "…" + suffix;
}
