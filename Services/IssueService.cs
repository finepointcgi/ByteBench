using ByteBench.Data;
using Microsoft.EntityFrameworkCore;

namespace ByteBench.Services;

public partial record IssueFilter
{
    public string? Search { get; set; }
    public IssueStatus? Status { get; set; }
    public bool? Open { get; set; } = true;
    public int? AssigneeId { get; set; }   // 0 = unassigned
    public int? LabelId { get; set; }
    public int? MilestoneId { get; set; }  // 0 = none
    public IssueType? Type { get; set; }
    public IssuePriority? Priority { get; set; }
    public string Sort { get; set; } = "updated";
    public int? SprintId { get; set; }     // 0 = no sprint (backlog)
    /// <summary>One of the project's workflow statuses (Status is the category filter).</summary>
    public int? WorkflowStatusId { get; set; }
}

/// <summary>
/// Projects, issues, comments, labels and milestones. Reads only return projects the acting user can view and
/// writes check <see cref="ProjectAccess"/> first (throwing <see cref="ForbiddenException"/>).
/// </summary>
public partial class IssueService(IDbContextFactory<AppDbContext> dbFactory, ProjectAccess access, NotificationService notifications, Webhooks.WebhookNotifier webhooks,
    Attachments.AttachmentStorage attachmentFiles,
    Audit.AuditLog audit,
    RepoStorage repoStorage)
{
    // ---------- Projects ----------

    public async Task<List<ProjectSummary>> GetProjectsAsync()
    {
        var visible = await access.VisibleProjectIdsAsync();
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Projects.Where(p => visible.Contains(p.Id)).OrderBy(p => p.Name)
            .Select(p => new ProjectSummary(p.Id, p.Key, p.Name, p.Description,
                p.Issues.Count(i => i.Status != IssueStatus.Done),
                p.Issues.Count(i => i.Status == IssueStatus.Done)))
            .ToListAsync();
    }

    /// <summary>The project with this key, or null if it doesn't exist or the acting user can't view it.</summary>
    public async Task<Project?> GetProjectAsync(string key)
    {
        var visible = await access.VisibleProjectIdsAsync();
        await using var db = await dbFactory.CreateDbContextAsync();
        var project = await db.Projects.Where(p => visible.Contains(p.Id))
            .Include(p => p.Labels.OrderBy(l => l.Name))
            .Include(p => p.Milestones.OrderBy(m => m.IsClosed).ThenBy(m => m.DueDate))
            .AsSplitQuery()
            .FirstOrDefaultAsync(p => p.Key == key.ToUpper());
        // Labels and milestones of the project's groups are usable here too (read-only: their ProjectId is null).
        if (project != null) await GroupService.AddInheritedAsync(db, project);
        return project;
    }

    /// <summary>Creates a project; its creator (<paramref name="ownerId"/>, default the acting user) becomes its Owner.</summary>
    public async Task<Project> CreateProjectAsync(Project project, int? ownerId = null)
    {
        var owner = await access.ResolveUserIdAsync(ownerId) ?? throw new ForbiddenException("Sign in to create a project.");
        if (project.GroupId is { } groupId) await access.RequireAsync(groupId, GroupPermission.CreateInGroup, owner);
        project.Members = [new ProjectMember { UserId = owner, Role = ProjectRole.Owner }];
        await using var db = await dbFactory.CreateDbContextAsync();
        project.Key = project.Key.ToUpperInvariant();
        if (await db.Projects.AnyAsync(p => p.Key == project.Key))
            throw new InvalidOperationException($"A project with key {project.Key} already exists.");
        project.Labels =
        [
            new() { Name = "bug", Color = "#dc2626" },
            new() { Name = "enhancement", Color = "#2563eb" },
            new() { Name = "documentation", Color = "#0d9488" },
        ];
        project.WorkflowStatuses = WorkflowService.DefaultStatuses();
        db.Projects.Add(project);
        await db.SaveChangesAsync();
        await audit.RecordAsync("project.created", "project", project.Id, project.Id, new { project.Key, project.Name, visibility = project.Visibility.ToString() }, owner);
        return project;
    }

    public async Task UpdateProjectAsync(int id, string name, string? description)
    {
        await access.RequireAsync(id, Permission.ManageProjectSettings);
        await using var db = await dbFactory.CreateDbContextAsync();
        var p = await db.Projects.FindAsync(id) ?? throw new InvalidOperationException("Project not found.");
        var before = new { p.Name, p.Description };
        p.Name = name;
        p.Description = description;
        await db.SaveChangesAsync();
        if (before.Name != name || before.Description != description) await audit.RecordAsync("project.settings_changed", "project", id, id, new { before, after = new { name, description } });
    }

    /// <summary>Name, description and visibility in one go (requires ManageProjectSettings).</summary>
    public async Task UpdateProjectAsync(int id, string name, string? description, ProjectVisibility visibility)
    {
        await UpdateProjectAsync(id, name, description);
        await using var db = await dbFactory.CreateDbContextAsync();
        var old = await db.Projects.Where(p => p.Id == id).Select(p => p.Visibility).FirstAsync();
        await db.Projects.Where(p => p.Id == id).ExecuteUpdateAsync(s => s.SetProperty(p => p.Visibility, visibility));
        if (old != visibility) await audit.RecordAsync("project.visibility_changed", "project", id, id, new { from = old.ToString(), to = visibility.ToString() });
    }

    public async Task DeleteProjectAsync(int id)
    {
        await access.RequireAsync(id, Permission.DeleteProject);
        await using var db = await dbFactory.CreateDbContextAsync();
        var files = await Attachments.AttachmentService.StoredNamesForProjectAsync(db, id);
        files.AddRange(await Releases.ReleaseService.StoredNamesAsync(db, projectId: id)); // release assets cascade with the project
        var gone = await db.Projects.Where(p => p.Id == id).Select(p => new { p.Key, p.Name, Issues = p.Issues.Count }).FirstOrDefaultAsync();
        await db.Projects.Where(p => p.Id == id).ExecuteDeleteAsync();
        attachmentFiles.Delete(files); // rows first, then files
        // The FK cascade removed the repository rows but not the bare repos: delete {RepositoryRoot}/{KEY} too.
        if (gone != null) repoStorage.DeleteProjectDirectory(gone.Key);
        await audit.RecordAsync("project.deleted", "project", id, id, gone);
    }

    // ---------- Users ----------

    public async Task<List<User>> GetUsersAsync()
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Users.Where(u => u.UserName != Ai.AiBot.UserName && u.UserName != Automation.AutomationBot.UserName && u.UserName != ServiceDesk.ServiceDeskBot.UserName && u.UserName != Workbench.WorkbenchBot.UserName).OrderBy(u => u.DisplayName).ToListAsync(); // bots are not assignable
    }

    // ---------- Issues ----------

    public async Task<List<Issue>> GetIssuesAsync(int projectId, IssueFilter f)
    {
        if (!await access.CanAsync(projectId, Permission.ViewProject)) return [];
        return await QueryIssuesAsync([projectId], f);
    }

    /// <summary>Issues across several projects (e.g. a group's), skipping any the acting user can't view.</summary>
    public async Task<List<Issue>> GetIssuesAsync(IReadOnlyCollection<int> projectIds, IssueFilter f)
    {
        var visible = await access.VisibleProjectIdsAsync();
        return await QueryIssuesAsync(projectIds.Where(visible.Contains).ToList(), f);
    }

    private async Task<List<Issue>> QueryIssuesAsync(List<int> projectIds, IssueFilter f)
    {
        if (projectIds.Count == 0) return [];
        await using var db = await dbFactory.CreateDbContextAsync();
        var q = BaseIssueQuery(db).Where(i => projectIds.Contains(i.ProjectId));

        if (!string.IsNullOrWhiteSpace(f.Search))
        {
            var s = f.Search.Trim();
            if (int.TryParse(s.Split('-').Last(), out var num) && (s.Contains('-') || s.All(char.IsDigit)))
                q = q.Where(i => i.Number == num || i.Title.Contains(s));
            else
                q = q.Where(i => i.Title.Contains(s) || (i.Description != null && i.Description.Contains(s)));
        }
        if (f.Status is { } st) q = q.Where(i => i.Status == st);
        else if (f.Open == true) q = q.Where(i => i.Status != IssueStatus.Done);
        else if (f.Open == false) q = q.Where(i => i.Status == IssueStatus.Done);
        if (f.WorkflowStatusId is { } ws) q = q.Where(i => i.WorkflowStatusId == ws);
        if (f.AssigneeId is { } a) q = a == 0 ? q.Where(i => i.AssigneeId == null) : q.Where(i => i.AssigneeId == a);
        if (f.LabelId is { } l) q = q.Where(i => i.Labels.Any(x => x.Id == l));
        if (f.MilestoneId is { } m) q = m == 0 ? q.Where(i => i.MilestoneId == null) : q.Where(i => i.MilestoneId == m);
        if (f.Type is { } t) q = q.Where(i => i.Type == t);
        if (f.Priority is { } p) q = q.Where(i => i.Priority == p);
        q = f.ApplyLinkedWork(q);
        q = f.ApplyFields(q); // components, versions, custom field
        if (f.SprintId is { } sp) q = sp == 0 ? q.Where(i => i.SprintId == null) : q.Where(i => i.SprintId == sp);

        q = f.Sort switch
        {
            "created" => q.OrderByDescending(i => i.CreatedAt),
            "priority" => q.OrderByDescending(i => i.Priority).ThenByDescending(i => i.UpdatedAt),
            "due" => q.OrderBy(i => i.DueDate == null).ThenBy(i => i.DueDate),
            "number" => q.OrderBy(i => i.Number),
            _ => q.OrderByDescending(i => i.UpdatedAt),
        };
        return await HierarchyRules.AnnotateAsync(db, access, await q.ToListAsync());
    }

    public async Task<(int open, int closed)> CountAsync(int projectId)
    {
        if (!await access.CanAsync(projectId, Permission.ViewProject)) return (0, 0);
        await using var db = await dbFactory.CreateDbContextAsync();
        var open = await db.Issues.CountAsync(i => i.ProjectId == projectId && i.Status != IssueStatus.Done);
        var closed = await db.Issues.CountAsync(i => i.ProjectId == projectId && i.Status == IssueStatus.Done);
        return (open, closed);
    }

    public async Task<List<Issue>> GetBoardAsync(int projectId, int? milestoneId, int? assigneeId)
    {
        if (!await access.CanAsync(projectId, Permission.ViewProject)) return [];
        await using var db = await dbFactory.CreateDbContextAsync();
        var q = BaseIssueQuery(db).Where(i => i.ProjectId == projectId);
        if (milestoneId is { } m) q = q.Where(i => i.MilestoneId == m);
        if (assigneeId is { } a) q = q.Where(i => i.AssigneeId == a);
        return await HierarchyRules.AnnotateAsync(db, access, await q.OrderBy(i => i.SortOrder).ThenBy(i => i.Number).ToListAsync());
    }

    public async Task<List<Issue>> GetAssignedToAsync(int userId)
    {
        var visible = await access.VisibleProjectIdsAsync(userId);
        await using var db = await dbFactory.CreateDbContextAsync();
        return await BaseIssueQuery(db)
            .Where(i => i.AssigneeId == userId && i.Status != IssueStatus.Done && visible.Contains(i.ProjectId))
            .OrderByDescending(i => i.Priority).ThenBy(i => i.DueDate)
            .Take(25).ToListAsync();
    }

    public async Task<Issue?> GetIssueAsync(string projectKey, int number)
    {
        var visible = await access.VisibleProjectIdsAsync();
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Issues.Where(i => visible.Contains(i.ProjectId))
            .Include(i => i.Project)
            .Include(i => i.Assignee).Include(i => i.Reporter).Include(i => i.Milestone)
            .Include(i => i.Labels).Include(i => i.WorkflowStatus)
            .Include(i => i.Comments).ThenInclude(c => c.Author)
            .Include(i => i.Activities).ThenInclude(a => a.Actor)
            .AsSplitQuery()
            .FirstOrDefaultAsync(i => i.Project.Key == projectKey.ToUpper() && i.Number == number);
    }

    public async Task<Issue> CreateIssueAsync(int projectId, Issue issue, IEnumerable<int> labelIds, int actorId)
    {
        await access.RequireAsync(projectId, Permission.EditIssues, actorId);
        await using var db = await dbFactory.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();

        var project = await db.Projects.FindAsync(projectId) ?? throw new InvalidOperationException("Project not found.");
        issue.ProjectId = projectId;
        issue.Number = project.NextIssueNumber++;
        issue.ReporterId = actorId;
        issue.CreatedAt = issue.UpdatedAt = DateTime.UtcNow;
        issue.SortOrder = (await db.Issues.Where(i => i.ProjectId == projectId).MaxAsync(i => (double?)i.SortOrder) ?? 0) + 1;
        var ids = labelIds.ToList();
        issue.Labels = await db.Labels.Where(l => ids.Contains(l.Id)).ToListAsync();
        issue.Activities.Add(new Activity { ActorId = actorId, Text = "created this issue" });
        await HierarchyRules.ValidateAsync(db, issue);
        var hierarchy = await HierarchyRules.RecordAsync(db, issue, null, actorId);
        // A new sub-task joins its parent's sprint unless one was chosen.
        if (issue.Type == IssueType.Subtask && issue.SprintId == null && issue.ParentId is { } parentId)
            issue.SprintId = await db.Issues.Where(i => i.Id == parentId).Select(i => i.SprintId).FirstOrDefaultAsync();
        await ReconcileStatusAsync(issue, null, null); // picks the workflow status (or the category's first one)
        if (issue.Status.IsClosed()) issue.ClosedAt ??= DateTime.UtcNow;
        await Fields.CustomFieldService.PrepareNewIssueAsync(db, access, issue); // custom fields, components, versions, estimates

        db.Issues.Add(issue);
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        issue.Project = project;
        await notifications.OnHierarchyChangedAsync(hierarchy, actorId);
        await notifications.OnIssueCreatedAsync(issue.Id, actorId);
        await webhooks.IssueCreatedAsync(issue.Id, actorId);
        return issue;
    }

    /// <summary>Applies a mutation to an issue and records an activity entry describing it.</summary>
    public async Task UpdateIssueAsync(int issueId, int actorId, Func<Issue, AppDbContext, Task<string?>> change)
    {
        await access.RequireForIssueAsync(issueId, Permission.EditIssues, actorId);
        await using var db = await dbFactory.CreateDbContextAsync();
        var issue = await db.Issues.Include(i => i.Labels).FirstAsync(i => i.Id == issueId);
        var wasClosed = issue.Status.IsClosed();
        var before = IssueSnapshot.Of(issue);
        var fieldsBefore = Automation.IssueFieldState.Of(issue);
        var (parentBefore, typeBefore) = (issue.ParentId, issue.Type);
        var beforeWorkflowStatusId = issue.WorkflowStatusId;
        var text = await change(issue, db);
        await ReconcileStatusAsync(issue, before.Status, beforeWorkflowStatusId); // keeps Status and WorkflowStatusId in step; enforces transitions
        if (text == null) return;
        if (issue.ParentId != parentBefore || issue.Type != typeBefore) await HierarchyRules.ValidateAsync(db, issue);
        var hierarchy = await HierarchyRules.RecordAsync(db, issue, parentBefore, actorId);

        if (!wasClosed && issue.Status.IsClosed()) issue.ClosedAt = DateTime.UtcNow;
        if (wasClosed && !issue.Status.IsClosed()) issue.ClosedAt = null;
        issue.UpdatedAt = DateTime.UtcNow;
        db.Activities.Add(new Activity { IssueId = issueId, ActorId = actorId, Text = text });
        await db.SaveChangesAsync();
        await notifications.OnIssueUpdatedAsync(issueId, actorId, before);
        await notifications.OnHierarchyChangedAsync(hierarchy, actorId);
        await webhooks.IssueUpdatedAsync(issueId, actorId, text, fieldsBefore.Diff(issue));
    }

    /// <summary>Sets the status category: the issue keeps its workflow status if it's in that category, else gets the category's first one.</summary>
    public Task SetStatusAsync(int issueId, IssueStatus status, int actorId) =>
        UpdateIssueAsync(issueId, actorId, async (i, _) =>
        {
            var (from, wf) = (i.Status, i.WorkflowStatusId);
            i.Status = status;
            return (await ReconcileStatusAsync(i, from, wf))?.Describe();
        });

    /// <summary>Moves an issue on the board to the first workflow status of <paramref name="status"/> (see the workflow overload).</summary>
    public async Task MoveOnBoardAsync(int issueId, IssueStatus status, int? beforeIssueId, int actorId)
    {
        var projectId = await access.ProjectOf(d => d.Issues.Where(i => i.Id == issueId).Select(i => (int?)i.ProjectId));
        var target = (await WorkflowService.LoadAsync(dbFactory, projectId)).FirstOf(status)
                     ?? throw new InvalidOperationException($"This project's workflow has no status in the {status.Name()} category.");
        await MoveOnBoardAsync(issueId, target.Id, beforeIssueId, actorId);
    }

    public Task<Comment> AddCommentAsync(int issueId, int authorId, string body) => AddCommentAsync(issueId, authorId, body, null);

    /// <param name="init">Sets extra fields before saving (service desk: requester, public reply).</param>
    public async Task<Comment> AddCommentAsync(int issueId, int authorId, string body, Action<Comment>? init)
    {
        await access.RequireForIssueAsync(issueId, Permission.Comment, authorId);
        await using var db = await dbFactory.CreateDbContextAsync();
        var c = new Comment { IssueId = issueId, AuthorId = authorId, Body = body };
        init?.Invoke(c);
        db.Comments.Add(c);
        await db.Issues.Where(i => i.Id == issueId).ExecuteUpdateAsync(s => s.SetProperty(i => i.UpdatedAt, DateTime.UtcNow));
        await db.SaveChangesAsync();
        await notifications.OnIssueCommentedAsync(issueId, c.Id, authorId, body);
        await webhooks.IssueCommentedAsync(issueId, c.Id, authorId);
        return c;
    }

    /// <summary>Authors (with Comment) can edit their own comments; Maintainers and above can edit anyone's.</summary>
    public async Task EditCommentAsync(int commentId, string body, int? actorId = null)
    {
        await RequireCommentModerationAsync(commentId, "edit other people's comments", actorId);
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.Comments.Where(c => c.Id == commentId).ExecuteUpdateAsync(s => s
            .SetProperty(c => c.Body, body).SetProperty(c => c.EditedAt, DateTime.UtcNow));
        await TouchIssueOfCommentAsync(db, commentId);
        await notifications.OnIssueCommentChangedAsync(await db.Comments.Where(c => c.Id == commentId).Select(c => (int?)c.IssueId).FirstOrDefaultAsync());
    }

    /// <summary>Authors (with Comment) can delete their own comments; Maintainers and above can delete anyone's.</summary>
    public async Task DeleteCommentAsync(int commentId, int? actorId = null)
    {
        await RequireCommentModerationAsync(commentId, "delete other people's comments", actorId);
        await using var db = await dbFactory.CreateDbContextAsync();
        var issueId = await db.Comments.Where(c => c.Id == commentId).Select(c => (int?)c.IssueId).FirstOrDefaultAsync();
        await db.Comments.Where(c => c.Id == commentId).ExecuteDeleteAsync();
        if (issueId != null) await db.Issues.Where(i => i.Id == issueId).ExecuteUpdateAsync(s => s.SetProperty(i => i.UpdatedAt, DateTime.UtcNow));
        await notifications.OnIssueCommentChangedAsync(issueId);
    }

    /// <summary>Bumps the parent issue's UpdatedAt so caches keyed on it (e.g. the AI "catch me up" summary) refresh.</summary>
    private static Task TouchIssueOfCommentAsync(AppDbContext db, int commentId) =>
        db.Issues.Where(i => db.Comments.Any(c => c.Id == commentId && c.IssueId == i.Id))
            .ExecuteUpdateAsync(s => s.SetProperty(i => i.UpdatedAt, DateTime.UtcNow));

    private async Task RequireCommentModerationAsync(int commentId, string action, int? actorId)
    {
        var uid = await access.ResolveUserIdAsync(actorId);
        await using var db = await dbFactory.CreateDbContextAsync();
        var c = await (from x in db.Comments
                       join i in db.Issues on x.IssueId equals i.Id
                       where x.Id == commentId
                       select new { x.AuthorId, i.ProjectId })
            .FirstOrDefaultAsync() ?? throw new InvalidOperationException("Comment not found.");
        if (uid != null && c.AuthorId == uid) await access.RequireAsync(c.ProjectId, Permission.Comment, actorId);
        else await access.RequireRoleAsync(c.ProjectId, ProjectRole.Maintainer, action, actorId);
    }

    /// <summary>Deleting an issue requires Maintainer or above.</summary>
    public async Task DeleteIssueAsync(int issueId)
    {
        await access.RequireRoleAsync(await access.ProjectOf(d => d.Issues.Where(i => i.Id == issueId).Select(i => (int?)i.ProjectId)),
            ProjectRole.Maintainer, "delete issues");
        await using var db = await dbFactory.CreateDbContextAsync();
        var issue = await db.Issues.Include(i => i.Labels).FirstAsync(i => i.Id == issueId);
        var subtaskIds = await db.Issues.Where(i => i.ParentId == issueId && i.Type == IssueType.Subtask).Select(i => i.Id).ToListAsync();
        var files = await Attachments.AttachmentService.StoredNamesForIssuesAsync(db, [issueId, .. subtaskIds]);
        await HierarchyRules.DeleteSubtasksAsync(db, issueId); // an epic's children are unlinked by the FK instead
        db.Issues.Remove(issue);
        await db.SaveChangesAsync();
        attachmentFiles.Delete(files); // rows first, then files
        await audit.RecordAsync("issue.deleted", "issue", issueId, issue.ProjectId, new { issue.Number, issue.Title });
        await notifications.OnIssueDeletedAsync(issue.ProjectId, issueId);
    }

    public async Task<List<Activity>> GetRecentActivityAsync(int take = 20)
    {
        var visible = await access.VisibleProjectIdsAsync();
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Activities.Include(a => a.Actor).Include(a => a.Issue).ThenInclude(i => i.Project)
            .Where(a => visible.Contains(a.Issue.ProjectId))
            .OrderByDescending(a => a.CreatedAt).Take(take).ToListAsync();
    }

    // ---------- Labels ----------

    public async Task<Label> SaveLabelAsync(Label label)
    {
        await access.RequireAsync(label.ProjectId ?? 0, Permission.ManageLabelsAndMilestones); // group labels: GroupService
        label.GroupId = null;
        if (label.Id != 0 && await access.ProjectOf(d => d.Labels.Where(l => l.Id == label.Id).Select(l => (int?)l.ProjectId)) != label.ProjectId)
            throw new ForbiddenException("Label not found in this project.");
        await using var db = await dbFactory.CreateDbContextAsync();
        if (label.Id == 0) db.Labels.Add(label); else db.Labels.Update(label);
        await db.SaveChangesAsync();
        return label;
    }

    public async Task DeleteLabelAsync(int id)
    {
        await access.RequireAsync(await access.ProjectOf(d => d.Labels.Where(l => l.Id == id).Select(l => (int?)l.ProjectId)),
            Permission.ManageLabelsAndMilestones);
        await using var db = await dbFactory.CreateDbContextAsync();
        var l = await db.Labels.Include(x => x.Issues).FirstAsync(x => x.Id == id);
        db.Labels.Remove(l);
        await db.SaveChangesAsync();
    }

    // ---------- Milestones ----------

    public async Task<List<MilestoneSummary>> GetMilestonesAsync(int projectId)
    {
        if (!await access.CanAsync(projectId, Permission.ViewProject)) return [];
        await using var db = await dbFactory.CreateDbContextAsync();
        // Plus the milestones inherited from the project's groups (read-only here); their progress counts this project's issues.
        var groupId = await db.Projects.Where(p => p.Id == projectId).Select(p => p.GroupId).FirstOrDefaultAsync();
        var chain = groupId is { } gid ? await ProjectAccess.GroupChainAsync(db, gid) : [];
        return await db.Milestones.Where(m => m.ProjectId == projectId || (m.GroupId != null && chain.Contains(m.GroupId.Value)))
            .OrderBy(m => m.IsClosed).ThenBy(m => m.DueDate == null).ThenBy(m => m.DueDate)
            .Select(m => new MilestoneSummary(m,
                m.Issues.Count(i => i.ProjectId == projectId && i.Status != IssueStatus.Done),
                m.Issues.Count(i => i.ProjectId == projectId && i.Status == IssueStatus.Done),
                m.Issues.Where(i => i.ProjectId == projectId).Sum(i => i.StoryPoints ?? 0),
                m.Issues.Where(i => i.ProjectId == projectId && i.Status == IssueStatus.Done).Sum(i => i.StoryPoints ?? 0)))
            .ToListAsync();
    }

    public async Task SaveMilestoneAsync(Milestone m)
    {
        await access.RequireAsync(m.ProjectId ?? 0, Permission.ManageLabelsAndMilestones); // group milestones: GroupService
        m.GroupId = null;
        if (m.Id != 0 && await access.ProjectOf(d => d.Milestones.Where(x => x.Id == m.Id).Select(x => (int?)x.ProjectId)) != m.ProjectId)
            throw new ForbiddenException("Milestone not found in this project.");
        await using var db = await dbFactory.CreateDbContextAsync();
        if (m.Id == 0) db.Milestones.Add(m); else db.Milestones.Update(m);
        await db.SaveChangesAsync();
    }

    public async Task DeleteMilestoneAsync(int id)
    {
        await access.RequireAsync(await access.ProjectOf(d => d.Milestones.Where(x => x.Id == id).Select(x => (int?)x.ProjectId)),
            Permission.ManageLabelsAndMilestones);
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.Issues.Where(i => i.MilestoneId == id).ExecuteUpdateAsync(s => s.SetProperty(i => i.MilestoneId, (int?)null));
        await db.Milestones.Where(m => m.Id == id).ExecuteDeleteAsync();
    }

    private static IQueryable<Issue> BaseIssueQuery(AppDbContext db) => db.Issues
        .Include(i => i.Project).Include(i => i.Assignee).Include(i => i.Labels).Include(i => i.Milestone).Include(i => i.WorkflowStatus)
        .AsSplitQuery();
}

public record ProjectSummary(int Id, string Key, string Name, string? Description, int OpenCount, int ClosedCount);
public record MilestoneSummary(Milestone Milestone, int Open, int Closed, int Points, int DonePoints)
{
    public int Total => Open + Closed;
    public int Percent => Total == 0 ? 0 : Closed * 100 / Total;
}
