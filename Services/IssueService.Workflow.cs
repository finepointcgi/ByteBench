using ByteBench.Data;
using Microsoft.EntityFrameworkCore;

namespace ByteBench.Services;

// Workflow statuses. Issue.Status is the status *category* and Issue.WorkflowStatusId the project's own status;
// ReconcileStatusAsync is the one place that keeps them in step, and every status change goes through it
// (UpdateIssueAsync calls it after every change, so bulk edits and API updates are covered too).
public partial class IssueService
{
    /// <summary>
    /// Brings <paramref name="issue"/>'s <see cref="Issue.Status"/> and <see cref="Issue.WorkflowStatusId"/> in step after a
    /// change, comparing with the values before it (<paramref name="beforeStatus"/> null for a new issue):
    /// <list type="bullet">
    /// <item>WorkflowStatusId changed: it must be a status of the issue's project; Status becomes its category.</item>
    /// <item>Only Status (the category) changed: the current workflow status is kept if it's in that category, else the
    /// category's first status is picked (so callers that only know categories keep working).</item>
    /// <item>Neither changed: an inconsistent or missing workflow status is repaired silently.</item>
    /// </list>
    /// Throws <see cref="InvalidOperationException"/> if the project's transitions don't allow the move. Returns the
    /// change, or null when the status didn't change. Does not save.
    /// </summary>
    public async Task<StatusChange?> ReconcileStatusAsync(Issue issue, IssueStatus? beforeStatus, int? beforeWorkflowStatusId)
    {
        var wf = await WorkflowService.LoadAsync(dbFactory, issue.ProjectId);
        if (wf.Statuses.Count == 0) return null;

        WorkflowStatus to;
        if (issue.WorkflowStatusId is { } id && (beforeStatus == null || id != beforeWorkflowStatusId))
        {
            to = wf.Find(id) ?? throw new InvalidOperationException("That status isn't part of this project's workflow.");
        }
        else if (beforeStatus == null || issue.Status != beforeStatus)
        {
            to = wf.Find(issue.WorkflowStatusId) is { } current && current.Category == issue.Status ? current
                : wf.FirstOf(issue.Status) ?? throw new InvalidOperationException(
                    $"This project's workflow has no status in the {issue.Status.Name()} category. A project Owner can add one in Settings → Workflow.");
        }
        else
        {
            if (wf.Find(issue.WorkflowStatusId) is { } ok && ok.Category == issue.Status) return null;
            issue.WorkflowStatusId = wf.FirstOf(issue.Status)?.Id;
            return null;
        }

        var from = beforeStatus is { } bs ? wf.Effective(bs, beforeWorkflowStatusId) : null;
        if (from != null && !wf.IsAllowed(from.Id, to.Id))
            throw new InvalidOperationException($"This project's workflow doesn't allow moving an issue from \"{from.Name}\" to \"{to.Name}\". " +
                $"Allowed from \"{from.Name}\": {string.Join(", ", wf.AllowedFrom(from).Where(s => s.Id != from.Id).Select(s => s.Name))}.");
        issue.WorkflowStatusId = to.Id;
        issue.Status = to.Category;
        return beforeStatus == null || from?.Id == to.Id ? null : new StatusChange(from, to, beforeStatus);
    }

    /// <summary>Moves an issue to a workflow status of its project (respecting transitions).</summary>
    public Task SetWorkflowStatusAsync(int issueId, int workflowStatusId, int actorId) =>
        UpdateIssueAsync(issueId, actorId, async (i, _) =>
        {
            var (status, wf) = (i.Status, i.WorkflowStatusId);
            i.WorkflowStatusId = workflowStatusId;
            return (await ReconcileStatusAsync(i, status, wf))?.Describe();
        });

    /// <summary>Moves an issue on the board: new workflow status (column) and position between neighbours.</summary>
    public async Task MoveOnBoardAsync(int issueId, int workflowStatusId, int? beforeIssueId, int actorId)
    {
        await access.RequireForIssueAsync(issueId, Permission.EditIssues, actorId);
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            var issue = await db.Issues.AsNoTracking().FirstAsync(i => i.Id == issueId);
            var wf = await WorkflowService.LoadAsync(dbFactory, issue.ProjectId);
            var target = wf.Find(workflowStatusId) ?? throw new InvalidOperationException("That status isn't part of this project's workflow.");
            var from = wf.Effective(issue);
            // Check before touching the sort order, so a refused move changes nothing.
            if (!wf.IsAllowed(from?.Id, target.Id))
                throw new InvalidOperationException($"This project's workflow doesn't allow moving an issue from \"{from!.Name}\" to \"{target.Name}\".");

            var column = (await db.Issues
                .Where(i => i.ProjectId == issue.ProjectId && i.Id != issueId)
                .OrderBy(i => i.SortOrder).Select(i => new { i.Id, i.SortOrder, i.Status, i.WorkflowStatusId }).ToListAsync())
                .Where(i => wf.Effective(i.Status, i.WorkflowStatusId)?.Id == target.Id).ToList();

            double order;
            var idx = beforeIssueId is { } b ? column.FindIndex(c => c.Id == b) : -1;
            if (idx < 0) order = (column.LastOrDefault()?.SortOrder ?? 0) + 1;
            else if (idx == 0) order = column[0].SortOrder - 1;
            else order = (column[idx - 1].SortOrder + column[idx].SortOrder) / 2;

            await db.Issues.Where(i => i.Id == issueId).ExecuteUpdateAsync(s => s.SetProperty(i => i.SortOrder, order));
        }
        await SetWorkflowStatusAsync(issueId, workflowStatusId, actorId);
        await notifications.OnIssueMovedAsync(issueId);
    }
}
