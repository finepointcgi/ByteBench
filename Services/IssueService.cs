using GitBench.Data;
using Microsoft.EntityFrameworkCore;

namespace GitBench.Services;

public class IssueFilter
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

public class IssueService(IDbContextFactory<AppDbContext> dbFactory)
{
    // ---------- Projects ----------

    public async Task<List<ProjectSummary>> GetProjectsAsync()
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Projects.OrderBy(p => p.Name)
            .Select(p => new ProjectSummary(p.Id, p.Key, p.Name, p.Description,
                p.Issues.Count(i => i.Status != IssueStatus.Done),
                p.Issues.Count(i => i.Status == IssueStatus.Done)))
            .ToListAsync();
    }

    public async Task<Project?> GetProjectAsync(string key)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Projects
            .Include(p => p.Labels.OrderBy(l => l.Name))
            .Include(p => p.Milestones.OrderBy(m => m.IsClosed).ThenBy(m => m.DueDate))
            .AsSplitQuery()
            .FirstOrDefaultAsync(p => p.Key == key.ToUpper());
    }

    public async Task<Project> CreateProjectAsync(Project project)
    {
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
        await using var db = await dbFactory.CreateDbContextAsync();
        var p = await db.Projects.FindAsync(id) ?? throw new InvalidOperationException("Project not found.");
        p.Name = name;
        p.Description = description;
        await db.SaveChangesAsync();
    }

    public async Task DeleteProjectAsync(int id)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.Projects.Where(p => p.Id == id).ExecuteDeleteAsync();
    }

    // ---------- Users ----------

    public async Task<List<User>> GetUsersAsync()
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Users.OrderBy(u => u.DisplayName).ToListAsync();
    }

    // ---------- Issues ----------

    public async Task<List<Issue>> GetIssuesAsync(int projectId, IssueFilter f)
    {
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

        q = f.Sort switch
        {
            "created" => q.OrderByDescending(i => i.CreatedAt),
            "priority" => q.OrderByDescending(i => i.Priority).ThenByDescending(i => i.UpdatedAt),
            "due" => q.OrderBy(i => i.DueDate == null).ThenBy(i => i.DueDate),
            "number" => q.OrderBy(i => i.Number),
            _ => q.OrderByDescending(i => i.UpdatedAt),
        };
        return await q.ToListAsync();
    }

    public async Task<(int open, int closed)> CountAsync(int projectId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var open = await db.Issues.CountAsync(i => i.ProjectId == projectId && i.Status != IssueStatus.Done);
        var closed = await db.Issues.CountAsync(i => i.ProjectId == projectId && i.Status == IssueStatus.Done);
        return (open, closed);
    }

    public async Task<List<Issue>> GetBoardAsync(int projectId, int? milestoneId, int? assigneeId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var q = BaseIssueQuery(db).Where(i => i.ProjectId == projectId);
        if (milestoneId is { } m) q = q.Where(i => i.MilestoneId == m);
        if (assigneeId is { } a) q = q.Where(i => i.AssigneeId == a);
        return await q.OrderBy(i => i.SortOrder).ThenBy(i => i.Number).ToListAsync();
    }

    public async Task<List<Issue>> GetAssignedToAsync(int userId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await BaseIssueQuery(db)
            .Where(i => i.AssigneeId == userId && i.Status != IssueStatus.Done)
            .OrderByDescending(i => i.Priority).ThenBy(i => i.DueDate)
            .Take(25).ToListAsync();
    }

    public async Task<Issue?> GetIssueAsync(string projectKey, int number)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Issues
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

        db.Issues.Add(issue);
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        issue.Project = project;
        return issue;
    }

    /// <summary>Applies a mutation to an issue and records an activity entry describing it.</summary>
    public async Task UpdateIssueAsync(int issueId, int actorId, Func<Issue, AppDbContext, Task<string?>> change)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var issue = await db.Issues.Include(i => i.Labels).FirstAsync(i => i.Id == issueId);
        var wasClosed = issue.Status.IsClosed();
        var text = await change(issue, db);
        if (text == null) return;

        if (!wasClosed && issue.Status.IsClosed()) issue.ClosedAt = DateTime.UtcNow;
        if (wasClosed && !issue.Status.IsClosed()) issue.ClosedAt = null;
        issue.UpdatedAt = DateTime.UtcNow;
        db.Activities.Add(new Activity { IssueId = issueId, ActorId = actorId, Text = text });
        await db.SaveChangesAsync();
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
    }

    public async Task<Comment> AddCommentAsync(int issueId, int authorId, string body)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var c = new Comment { IssueId = issueId, AuthorId = authorId, Body = body };
        db.Comments.Add(c);
        await db.Issues.Where(i => i.Id == issueId).ExecuteUpdateAsync(s => s.SetProperty(i => i.UpdatedAt, DateTime.UtcNow));
        await db.SaveChangesAsync();
        return c;
    }

    public async Task EditCommentAsync(int commentId, string body)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.Comments.Where(c => c.Id == commentId).ExecuteUpdateAsync(s => s
            .SetProperty(c => c.Body, body).SetProperty(c => c.EditedAt, DateTime.UtcNow));
    }

    public async Task DeleteCommentAsync(int commentId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.Comments.Where(c => c.Id == commentId).ExecuteDeleteAsync();
    }

    public async Task DeleteIssueAsync(int issueId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var issue = await db.Issues.Include(i => i.Labels).FirstAsync(i => i.Id == issueId);
        db.Issues.Remove(issue);
        await db.SaveChangesAsync();
    }

    public async Task<List<Activity>> GetRecentActivityAsync(int take = 20)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Activities.Include(a => a.Actor).Include(a => a.Issue).ThenInclude(i => i.Project)
            .OrderByDescending(a => a.CreatedAt).Take(take).ToListAsync();
    }

    // ---------- Labels ----------

    public async Task<Label> SaveLabelAsync(Label label)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        if (label.Id == 0) db.Labels.Add(label); else db.Labels.Update(label);
        await db.SaveChangesAsync();
        return label;
    }

    public async Task DeleteLabelAsync(int id)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var l = await db.Labels.Include(x => x.Issues).FirstAsync(x => x.Id == id);
        db.Labels.Remove(l);
        await db.SaveChangesAsync();
    }

    // ---------- Milestones ----------

    public async Task<List<MilestoneSummary>> GetMilestonesAsync(int projectId)
    {
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
        await using var db = await dbFactory.CreateDbContextAsync();
        if (m.Id == 0) db.Milestones.Add(m); else db.Milestones.Update(m);
        await db.SaveChangesAsync();
    }

    public async Task DeleteMilestoneAsync(int id)
    {
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
