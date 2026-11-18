using GitBench.Data;
using Microsoft.EntityFrameworkCore;

namespace GitBench.Services;

public partial class IssueFilter
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
}

/// <summary>
/// Projects, issues, comments, labels and milestones. Reads only return projects the acting user can view and
/// writes check <see cref="ProjectAccess"/> first (throwing <see cref="ForbiddenException"/>).
/// </summary>
public class IssueService(IDbContextFactory<AppDbContext> dbFactory, ProjectAccess access, NotificationService notifications, Webhooks.WebhookNotifier webhooks)
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
        return await db.Projects.Where(p => visible.Contains(p.Id))
            .Include(p => p.Labels.OrderBy(l => l.Name))
            .Include(p => p.Milestones.OrderBy(m => m.IsClosed).ThenBy(m => m.DueDate))
            .AsSplitQuery()
            .FirstOrDefaultAsync(p => p.Key == key.ToUpper());
    }

    /// <summary>Creates a project; its creator (<paramref name="ownerId"/>, default the acting user) becomes its Owner.</summary>
    public async Task<Project> CreateProjectAsync(Project project, int? ownerId = null)
    {
        var owner = await access.ResolveUserIdAsync(ownerId) ?? throw new ForbiddenException("Sign in to create a project.");
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
        db.Projects.Add(project);
        await db.SaveChangesAsync();
        return project;
    }

    public async Task UpdateProjectAsync(int id, string name, string? description)
    {
        await access.RequireAsync(id, Permission.ManageProjectSettings);
        await using var db = await dbFactory.CreateDbContextAsync();
        var p = await db.Projects.FindAsync(id) ?? throw new InvalidOperationException("Project not found.");
        p.Name = name;
        p.Description = description;
        await db.SaveChangesAsync();
    }

    /// <summary>Name, description and visibility in one go (requires ManageProjectSettings).</summary>
    public async Task UpdateProjectAsync(int id, string name, string? description, ProjectVisibility visibility)
    {
        await UpdateProjectAsync(id, name, description);
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.Projects.Where(p => p.Id == id).ExecuteUpdateAsync(s => s.SetProperty(p => p.Visibility, visibility));
    }

    public async Task DeleteProjectAsync(int id)
    {
        await access.RequireAsync(id, Permission.DeleteProject);
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.Projects.Where(p => p.Id == id).ExecuteDeleteAsync();
    }

    // ---------- Users ----------

    public async Task<List<User>> GetUsersAsync()
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Users.Where(u => u.UserName != Ai.AiBot.UserName).OrderBy(u => u.DisplayName).ToListAsync(); // the AI bot is not assignable
    }

    // ---------- Issues ----------

    public async Task<List<Issue>> GetIssuesAsync(int projectId, IssueFilter f)
    {
        if (!await access.CanAsync(projectId, Permission.ViewProject)) return [];
        await using var db = await dbFactory.CreateDbContextAsync();
        var q = BaseIssueQuery(db).Where(i => i.ProjectId == projectId);

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
        if (f.AssigneeId is { } a) q = a == 0 ? q.Where(i => i.AssigneeId == null) : q.Where(i => i.AssigneeId == a);
        if (f.LabelId is { } l) q = q.Where(i => i.Labels.Any(x => x.Id == l));
        if (f.MilestoneId is { } m) q = m == 0 ? q.Where(i => i.MilestoneId == null) : q.Where(i => i.MilestoneId == m);
        if (f.Type is { } t) q = q.Where(i => i.Type == t);
        if (f.Priority is { } p) q = q.Where(i => i.Priority == p);
        q = f.ApplyLinkedWork(q);

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
            .Include(i => i.Labels)
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
        var (parentBefore, typeBefore) = (issue.ParentId, issue.Type);
        var text = await change(issue, db);
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
        await webhooks.IssueUpdatedAsync(issueId, actorId, text);
    }

    public Task SetStatusAsync(int issueId, IssueStatus status, int actorId) =>
        UpdateIssueAsync(issueId, actorId, (i, _) =>
        {
            if (i.Status == status) return Task.FromResult<string?>(null);
            var from = i.Status;
            i.Status = status;
            return Task.FromResult<string?>($"changed status from **{from.Name()}** to **{status.Name()}**");
        });

    /// <summary>Moves an issue on the board: new column and position between neighbours.</summary>
    public async Task MoveOnBoardAsync(int issueId, IssueStatus status, int? beforeIssueId, int actorId)
    {
        await access.RequireForIssueAsync(issueId, Permission.EditIssues, actorId);
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            var projectId = await db.Issues.Where(i => i.Id == issueId).Select(i => i.ProjectId).FirstAsync();
            var column = await db.Issues
                .Where(i => i.ProjectId == projectId && i.Status == status && i.Id != issueId)
                .OrderBy(i => i.SortOrder).Select(i => new { i.Id, i.SortOrder }).ToListAsync();

            double order;
            var idx = beforeIssueId is { } b ? column.FindIndex(c => c.Id == b) : -1;
            if (idx < 0) order = (column.LastOrDefault()?.SortOrder ?? 0) + 1;
            else if (idx == 0) order = column[0].SortOrder - 1;
            else order = (column[idx - 1].SortOrder + column[idx].SortOrder) / 2;

            await db.Issues.Where(i => i.Id == issueId).ExecuteUpdateAsync(s => s.SetProperty(i => i.SortOrder, order));
        }
        await SetStatusAsync(issueId, status, actorId);
        await notifications.OnIssueMovedAsync(issueId);
    }

    public async Task<Comment> AddCommentAsync(int issueId, int authorId, string body)
    {
        await access.RequireForIssueAsync(issueId, Permission.Comment, authorId);
        await using var db = await dbFactory.CreateDbContextAsync();
        var c = new Comment { IssueId = issueId, AuthorId = authorId, Body = body };
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
        await HierarchyRules.DeleteSubtasksAsync(db, issueId); // an epic's children are unlinked by the FK instead
        db.Issues.Remove(issue);
        await db.SaveChangesAsync();
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
        await access.RequireAsync(label.ProjectId, Permission.ManageLabelsAndMilestones);
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
        return await db.Milestones.Where(m => m.ProjectId == projectId)
            .OrderBy(m => m.IsClosed).ThenBy(m => m.DueDate == null).ThenBy(m => m.DueDate)
            .Select(m => new MilestoneSummary(m,
                m.Issues.Count(i => i.Status != IssueStatus.Done),
                m.Issues.Count(i => i.Status == IssueStatus.Done),
                m.Issues.Sum(i => i.StoryPoints ?? 0),
                m.Issues.Where(i => i.Status == IssueStatus.Done).Sum(i => i.StoryPoints ?? 0)))
            .ToListAsync();
    }

    public async Task SaveMilestoneAsync(Milestone m)
    {
        await access.RequireAsync(m.ProjectId, Permission.ManageLabelsAndMilestones);
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
        .Include(i => i.Project).Include(i => i.Assignee).Include(i => i.Labels).Include(i => i.Milestone)
        .AsSplitQuery();
}

public record ProjectSummary(int Id, string Key, string Name, string? Description, int OpenCount, int ClosedCount);
public record MilestoneSummary(Milestone Milestone, int Open, int Closed, int Points, int DonePoints)
{
    public int Total => Open + Closed;
    public int Percent => Total == 0 ? 0 : Closed * 100 / Total;
}
