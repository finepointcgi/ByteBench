using ByteBench.Data;
using Microsoft.EntityFrameworkCore;

namespace ByteBench.Services;

/// <summary>
/// One bulk edit from the issue list. Null fields are left alone; <see cref="SetAssignee"/>/<see cref="SetMilestone"/>
/// say whether <see cref="AssigneeId"/>/<see cref="MilestoneId"/> apply (null then clears them).
/// </summary>
public sealed record BulkIssueChange
{
    public IssueStatus? Status { get; init; }
    /// <summary>One of the project's workflow statuses; wins over <see cref="Status"/> if both are set.</summary>
    public int? WorkflowStatusId { get; init; }
    public IssuePriority? Priority { get; init; }
    public bool SetAssignee { get; init; }
    public int? AssigneeId { get; init; }
    public bool SetMilestone { get; init; }
    public int? MilestoneId { get; init; }
    public int? AddLabelId { get; init; }
    public int? RemoveLabelId { get; init; }
    /// <summary>Deletes the issues (Maintainer+). Other fields are ignored.</summary>
    public bool Delete { get; init; }
}

/// <summary>What a bulk edit did.</summary>
public sealed record BulkResult(int Changed, int Unchanged, int Deleted);

public partial class IssueService
{
    public const int MaxBulkIssues = 500;

    /// <summary>
    /// Applies <paramref name="change"/> to every issue in <paramref name="issueIds"/> in one transaction. Permissions are
    /// checked per project before anything changes (EditIssues; Maintainer+ to delete), values are validated against each
    /// issue's project, each changed issue gets its own activity entry, live events are published once, and watchers get
    /// at most one notification each for the whole batch.
    /// </summary>
    public async Task<BulkResult> BulkUpdateAsync(IReadOnlyCollection<int> issueIds, BulkIssueChange change, int actorId)
    {
        var ids = issueIds.Distinct().ToList();
        if (ids.Count == 0) return new(0, 0, 0);
        if (ids.Count > MaxBulkIssues) throw new InvalidOperationException($"Select at most {MaxBulkIssues} issues at a time.");

        await using var db = await dbFactory.CreateDbContextAsync();
        var issues = await db.Issues.Include(i => i.Labels).Include(i => i.Project)
            .Where(i => ids.Contains(i.Id)).AsSplitQuery().ToListAsync();
        if (issues.Count != ids.Count) throw new InvalidOperationException("Some of the selected issues no longer exist. Reload and try again.");

        // ----- Check everything before changing anything -----
        var projectIds = issues.Select(i => i.ProjectId).Distinct().ToList();
        foreach (var pid in projectIds)
        {
            if (change.Delete) await access.RequireRoleAsync(pid, ProjectRole.Maintainer, "delete issues", actorId);
            else await access.RequireAsync(pid, Permission.EditIssues, actorId);
        }

        if (change.Delete)
        {
            // Sub-tasks go with their parent (as in DeleteIssueAsync); an epic's children are unlinked by the FK.
            var subtaskIds = await db.Issues.Where(i => i.ParentId != null && ids.Contains(i.ParentId.Value) && i.Type == IssueType.Subtask && !ids.Contains(i.Id))
                .Select(i => i.Id).ToListAsync();
            var files = await Attachments.AttachmentService.StoredNamesForIssuesAsync(db, [.. ids, .. subtaskIds]);
            await using (var tx = await db.Database.BeginTransactionAsync())
            {
                if (subtaskIds.Count > 0) await db.Issues.Where(i => subtaskIds.Contains(i.Id)).ExecuteDeleteAsync();
                db.Issues.RemoveRange(issues);
                await db.SaveChangesAsync();
                await tx.CommitAsync();
            }
            attachmentFiles.Delete(files); // rows first, then files
            await notifications.OnIssuesBulkChangedAsync(actorId, issues.Select(i => (i.ProjectId, i.Id)).ToList(), []);
            await audit.RecordAsync("issue.bulk_deleted", "issue", null, projectIds.Count == 1 ? projectIds[0] : null,
                new { Issues = issues.Select(i => i.Key).ToList(), Subtasks = subtaskIds.Count });
            return new(0, 0, issues.Count);
        }

        User? assignee = null;
        if (change.SetAssignee && change.AssigneeId is { } aid)
        {
            assignee = await db.Users.FindAsync(aid) ?? throw new InvalidOperationException("That user doesn't exist.");
            foreach (var pid in projectIds)
                if (!await access.CanAsync(pid, Permission.ViewProject, aid))
                    throw new InvalidOperationException($"{assignee.DisplayName} can't see every selected issue's project.");
        }
        Milestone? milestone = null;
        if (change.SetMilestone && change.MilestoneId is { } mid)
        {
            milestone = await db.Milestones.FindAsync(mid) ?? throw new InvalidOperationException("That milestone doesn't exist.");
            if (!await GroupService.UsableInAllAsync(db, projectIds, milestone.ProjectId, milestone.GroupId)) throw new InvalidOperationException("That milestone belongs to another project.");
        }
        if (change.WorkflowStatusId is { } wsId)
        {
            if (projectIds.Count != 1) throw new InvalidOperationException("Workflow statuses belong to one project. Select issues from a single project.");
            if ((await WorkflowService.LoadAsync(dbFactory, projectIds[0])).Find(wsId) == null)
                throw new InvalidOperationException("That status isn't part of this project's workflow.");
        }
        Label? addLabel = null, removeLabel = null;
        if (change.AddLabelId is { } al)
        {
            addLabel = await db.Labels.FindAsync(al) ?? throw new InvalidOperationException("That label doesn't exist.");
            if (!await GroupService.UsableInAllAsync(db, projectIds, addLabel.ProjectId, addLabel.GroupId)) throw new InvalidOperationException("That label belongs to another project.");
        }
        if (change.RemoveLabelId is { } rl)
        {
            removeLabel = await db.Labels.FindAsync(rl) ?? throw new InvalidOperationException("That label doesn't exist.");
            if (!await GroupService.UsableInAllAsync(db, projectIds, removeLabel.ProjectId, removeLabel.GroupId)) throw new InvalidOperationException("That label belongs to another project.");
        }

        // ----- Apply -----
        var now = DateTime.UtcNow;
        var changed = new List<(Issue Issue, IssueSnapshot Before, string Text, Automation.IssueFieldState Fields)>();
        await using (var tx = await db.Database.BeginTransactionAsync())
        {
            foreach (var i in issues)
            {
                var before = IssueSnapshot.Of(i);
                var fieldsBefore = Automation.IssueFieldState.Of(i);
                var wasClosed = i.Status.IsClosed();
                var parts = new List<string>();
                // Through the project's workflow: a category picks its first status (unless the issue is already in it), and
                // transitions are enforced. A refusal throws here, before the commit, so the whole batch rolls back.
                var (fromStatus, fromWorkflow) = (i.Status, i.WorkflowStatusId);
                var statusChanged = false;
                if (change.WorkflowStatusId is { } ws) { if (i.WorkflowStatusId != ws) { i.WorkflowStatusId = ws; statusChanged = true; } }
                else if (change.Status is { } s && i.Status != s) { i.Status = s; statusChanged = true; }
                if (statusChanged)
                {
                    StatusChange? sc;
                    try { sc = await ReconcileStatusAsync(i, fromStatus, fromWorkflow); }
                    catch (InvalidOperationException ex) { throw new InvalidOperationException($"{i.Key}: {ex.Message} No issues were changed.", ex); }
                    if (sc != null) parts.Add(sc.Describe());
                }
                if (change.Priority is { } p && i.Priority != p) { i.Priority = p; parts.Add($"set priority to **{p}**"); }
                if (change.SetAssignee && i.AssigneeId != assignee?.Id)
                {
                    i.AssigneeId = assignee?.Id;
                    parts.Add(assignee == null ? "removed the assignee" : assignee.Id == actorId ? "self-assigned this issue" : $"assigned to **{assignee.DisplayName}**");
                }
                if (change.SetMilestone && i.MilestoneId != milestone?.Id)
                {
                    i.MilestoneId = milestone?.Id;
                    parts.Add(milestone == null ? "removed the milestone" : $"added to milestone **{milestone.Title}**");
                }
                if (addLabel != null && i.Labels.All(l => l.Id != addLabel.Id)) { i.Labels.Add(addLabel); parts.Add($"added label **{addLabel.Name}**"); }
                if (removeLabel != null && i.Labels.FirstOrDefault(l => l.Id == removeLabel.Id) is { } existing)
                {
                    i.Labels.Remove(existing);
                    parts.Add($"removed label **{removeLabel.Name}**");
                }
                if (parts.Count == 0) continue;

                if (!wasClosed && i.Status.IsClosed()) i.ClosedAt = now;
                if (wasClosed && !i.Status.IsClosed()) i.ClosedAt = null;
                i.UpdatedAt = now;
                var text = string.Join(", ", parts);
                db.Activities.Add(new Activity { IssueId = i.Id, ActorId = actorId, Text = text, CreatedAt = now });
                changed.Add((i, before, text, fieldsBefore));
            }
            await db.SaveChangesAsync();
            await tx.CommitAsync();
        }

        if (changed.Count > 0)
        {
            await notifications.OnIssuesBulkChangedAsync(actorId, [], changed.Select(c => (c.Issue.Id, c.Before)).ToList());
            foreach (var c in changed) await webhooks.IssueUpdatedAsync(c.Issue.Id, actorId, c.Text, c.Fields.Diff(c.Issue));
        }
        return new(changed.Count, issues.Count - changed.Count, 0);
    }
}
