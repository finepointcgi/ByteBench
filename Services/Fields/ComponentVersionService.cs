using ByteBench.Data;
using Microsoft.EntityFrameworkCore;

namespace ByteBench.Services.Fields;

public sealed record ComponentSummary(ProjectComponent Component, int OpenIssues, int TotalIssues);

/// <summary>A published release whose tag matches a version's name (<c>1.2</c> or <c>v1.2</c>).</summary>
public sealed record ReleaseLink(int Id, string Title, string TagName, string Url);

/// <summary>A version with the progress of the issues that have it as a fix version.</summary>
public sealed record VersionSummary(ProjectVersion Version, int Open, int Done, int AffectsCount, int Points, int DonePoints,
    ReleaseLink? Release)
{
    public int Total => Open + Done;
    public int Percent => Total == 0 ? 0 : Done * 100 / Total;
}

/// <summary>
/// Components and versions (fix versions / affects versions) of a project. Defining them needs
/// <see cref="Permission.ManageLabelsAndMilestones"/> (Maintainer+); putting issues in them needs <see cref="Permission.EditIssues"/>.
/// </summary>
public class ComponentVersionService(IDbContextFactory<AppDbContext> dbFactory, ProjectAccess access, IssueService issues, Audit.AuditLog audit)
{
    // ---------- Components ----------

    public async Task<List<ComponentSummary>> GetComponentsAsync(int projectId)
    {
        if (!await access.CanAsync(projectId, Permission.ViewProject)) return [];
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.ProjectComponents.AsNoTracking().Where(c => c.ProjectId == projectId).OrderBy(c => c.Name)
            .Include(c => c.Lead).Include(c => c.DefaultAssignee)
            .Select(c => new ComponentSummary(c, c.Issues.Count(i => i.Status != IssueStatus.Done), c.Issues.Count))
            .ToListAsync();
    }

    public async Task<ProjectComponent> SaveComponentAsync(ProjectComponent c)
    {
        await access.RequireAsync(c.ProjectId, Permission.ManageLabelsAndMilestones);
        c.Name = (c.Name ?? "").Trim();
        if (c.Name.Length is 0 or > 60) throw new InvalidOperationException("A component needs a name (at most 60 characters).");
        c.Description = string.IsNullOrWhiteSpace(c.Description) ? null : c.Description.Trim();
        if (c.Description?.Length > 300) throw new InvalidOperationException("The description can be at most 300 characters.");
        foreach (var uid in new[] { c.LeadId, c.DefaultAssigneeId }.OfType<int>())
            if (!await access.CanAsync(c.ProjectId, Permission.ViewProject, uid)) throw new InvalidOperationException("The lead and default assignee must be able to see this project.");
        await using var db = await dbFactory.CreateDbContextAsync();
        if (await db.ProjectComponents.AnyAsync(x => x.ProjectId == c.ProjectId && x.Id != c.Id && x.Name.ToLower() == c.Name.ToLower()))
            throw new InvalidOperationException($"A component called '{c.Name}' already exists.");
        ProjectComponent row;
        if (c.Id == 0)
        {
            row = new ProjectComponent { ProjectId = c.ProjectId };
            db.ProjectComponents.Add(row);
        }
        else row = await db.ProjectComponents.FirstOrDefaultAsync(x => x.Id == c.Id && x.ProjectId == c.ProjectId)
                   ?? throw new ForbiddenException("Component not found in this project.");
        var action = c.Id == 0 ? "component.created" : "component.updated";
        (row.Name, row.Description, row.LeadId, row.DefaultAssigneeId) = (c.Name, c.Description, c.LeadId, c.DefaultAssigneeId);
        await db.SaveChangesAsync();
        c.Id = row.Id;
        await audit.RecordAsync(action, "component", c.Id, c.ProjectId, new { c.Name, c.LeadId, c.DefaultAssigneeId });
        return c;
    }

    public async Task DeleteComponentAsync(int componentId)
    {
        var projectId = await access.ProjectOf(d => d.ProjectComponents.Where(c => c.Id == componentId).Select(c => (int?)c.ProjectId));
        await access.RequireAsync(projectId, Permission.ManageLabelsAndMilestones);
        await using var db = await dbFactory.CreateDbContextAsync();
        var c = await db.ProjectComponents.Include(x => x.Issues).FirstAsync(x => x.Id == componentId);
        db.ProjectComponents.Remove(c); // the IssueComponents join rows go with it
        await db.SaveChangesAsync();
        await audit.RecordAsync("component.deleted", "component", componentId, projectId, new { c.Name });
    }

    // ---------- Versions ----------

    /// <summary>Unreleased versions first (by release date, undated last), then released ones (newest first).</summary>
    public async Task<List<VersionSummary>> GetVersionsAsync(int projectId)
    {
        if (!await access.CanAsync(projectId, Permission.ViewProject)) return [];
        await using var db = await dbFactory.CreateDbContextAsync();
        var versions = await db.ProjectVersions.AsNoTracking().Where(v => v.ProjectId == projectId).ToListAsync();
        var links = await (from l in db.IssueVersions
                           join i in db.Issues on l.IssueId equals i.Id
                           where i.ProjectId == projectId
                           select new
                           {
                               l.VersionId, l.Kind, l.IssueId, Done = i.Status == IssueStatus.Done,
                               Points = i.StoryPoints ?? 0,
                           }).ToListAsync();
        var releases = await FindReleasesAsync(db, projectId, versions);
        return Sort(versions).Select(v =>
        {
            var fix = links.Where(l => l.VersionId == v.Id && l.Kind == VersionLinkKind.Fix).ToList();
            return new VersionSummary(v, fix.Count(l => !l.Done), fix.Count(l => l.Done),
                links.Count(l => l.VersionId == v.Id && l.Kind == VersionLinkKind.Affects),
                fix.Sum(l => l.Points), fix.Where(l => l.Done).Sum(l => l.Points),
                releases.GetValueOrDefault(v.Id));
        }).ToList();
    }

    /// <summary>Versions of a project in display order, without counts (for pickers).</summary>
    public async Task<List<ProjectVersion>> GetVersionListAsync(int projectId)
    {
        if (!await access.CanAsync(projectId, Permission.ViewProject)) return [];
        await using var db = await dbFactory.CreateDbContextAsync();
        return Sort(await db.ProjectVersions.AsNoTracking().Where(v => v.ProjectId == projectId).ToListAsync()).ToList();
    }

    public static IEnumerable<ProjectVersion> Sort(IEnumerable<ProjectVersion> versions) => versions
        .OrderBy(v => v.IsReleased)
        .ThenBy(v => v.IsReleased ? 0 : v.ReleaseDate == null ? 1 : 0)
        .ThenBy(v => v.IsReleased ? -(v.ReleaseDate ?? v.CreatedAt).Ticks : (v.ReleaseDate ?? DateTime.MaxValue).Ticks)
        .ThenBy(v => v.Name);

    private static async Task<Dictionary<int, ReleaseLink>> FindReleasesAsync(AppDbContext db, int projectId, List<ProjectVersion> versions)
    {
        if (versions.Count == 0) return [];
        var tags = versions.SelectMany(TagCandidates).Distinct().ToList();
        var releases = await db.Releases.AsNoTracking().Include(r => r.Repository).ThenInclude(r => r.Project)
            .Where(r => r.Repository.ProjectId == projectId && !r.IsDraft && tags.Contains(r.TagName))
            .OrderBy(r => r.RepositoryId).ToListAsync();
        var result = new Dictionary<int, ReleaseLink>();
        foreach (var v in versions)
        {
            var candidates = TagCandidates(v).ToList();
            if (releases.FirstOrDefault(r => candidates.Contains(r.TagName)) is { } r)
                result[v.Id] = new ReleaseLink(r.Id, r.Title, r.TagName, Releases.ReleaseUrls.Detail(r.Repository, r.Id));
        }
        return result;
    }

    private static IEnumerable<string> TagCandidates(ProjectVersion v)
    {
        yield return v.Name;
        if (v.Name.StartsWith('v') || v.Name.StartsWith('V')) yield return v.Name[1..];
        else yield return "v" + v.Name;
    }

    public async Task<ProjectVersion> SaveVersionAsync(ProjectVersion v)
    {
        await access.RequireAsync(v.ProjectId, Permission.ManageLabelsAndMilestones);
        v.Name = (v.Name ?? "").Trim();
        if (v.Name.Length is 0 or > 60) throw new InvalidOperationException("A version needs a name (at most 60 characters).");
        v.Description = string.IsNullOrWhiteSpace(v.Description) ? null : v.Description.Trim();
        if (v.Description?.Length > 300) throw new InvalidOperationException("The description can be at most 300 characters.");
        if (v.StartDate is { } s && v.ReleaseDate is { } r && s.Date > r.Date) throw new InvalidOperationException("The start date must be before the release date.");
        await using var db = await dbFactory.CreateDbContextAsync();
        if (await db.ProjectVersions.AnyAsync(x => x.ProjectId == v.ProjectId && x.Id != v.Id && x.Name.ToLower() == v.Name.ToLower()))
            throw new InvalidOperationException($"A version called '{v.Name}' already exists.");
        ProjectVersion row;
        if (v.Id == 0)
        {
            row = new ProjectVersion { ProjectId = v.ProjectId };
            db.ProjectVersions.Add(row);
        }
        else row = await db.ProjectVersions.FirstOrDefaultAsync(x => x.Id == v.Id && x.ProjectId == v.ProjectId)
                   ?? throw new ForbiddenException("Version not found in this project.");
        var wasReleased = v.Id != 0 && row.IsReleased;
        row.Name = v.Name;
        row.Description = v.Description;
        row.StartDate = v.StartDate?.Date;
        row.ReleaseDate = v.ReleaseDate?.Date;
        row.IsReleased = v.IsReleased;
        if (v.IsReleased && !wasReleased) row.ReleaseDate ??= DateTime.UtcNow.Date;
        await db.SaveChangesAsync();
        await audit.RecordAsync(v.Id == 0 ? "version.created" : "version.updated", "version", row.Id, v.ProjectId,
            new { row.Name, releaseDate = row.ReleaseDate?.ToString("yyyy-MM-dd"), row.IsReleased });
        v.Id = row.Id;
        v.ReleaseDate = row.ReleaseDate;
        return row;
    }

    /// <summary>Marks a version released (today, unless it has a release date) or unreleased.</summary>
    public async Task SetReleasedAsync(int versionId, bool released)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var v = await db.ProjectVersions.AsNoTracking().FirstOrDefaultAsync(x => x.Id == versionId) ?? throw new InvalidOperationException("Version not found.");
        v.IsReleased = released;
        await SaveVersionAsync(v);
    }

    public async Task DeleteVersionAsync(int versionId)
    {
        var projectId = await access.ProjectOf(d => d.ProjectVersions.Where(v => v.Id == versionId).Select(v => (int?)v.ProjectId));
        await access.RequireAsync(projectId, Permission.ManageLabelsAndMilestones);
        await using var db = await dbFactory.CreateDbContextAsync();
        var name = await db.ProjectVersions.Where(v => v.Id == versionId).Select(v => v.Name).FirstOrDefaultAsync();
        await db.ProjectVersions.Where(v => v.Id == versionId).ExecuteDeleteAsync(); // issue links cascade
        await audit.RecordAsync("version.deleted", "version", versionId, projectId, new { name });
    }

    /// <summary>Issues with this version as fix (or affects) version, open first, then by number.</summary>
    public async Task<List<Issue>> GetVersionIssuesAsync(int versionId, VersionLinkKind kind = VersionLinkKind.Fix)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var projectId = await db.ProjectVersions.Where(v => v.Id == versionId).Select(v => (int?)v.ProjectId).FirstOrDefaultAsync();
        if (projectId == null || !await access.CanAsync(projectId.Value, Permission.ViewProject)) return [];
        return await db.Issues.AsNoTracking().Where(i => i.VersionLinks.Any(l => l.VersionId == versionId && l.Kind == kind))
            .Include(i => i.Project).Include(i => i.Assignee).Include(i => i.Labels).Include(i => i.Milestone).Include(i => i.WorkflowStatus)
            .AsSplitQuery()
            .OrderBy(i => i.Status == IssueStatus.Done).ThenBy(i => i.Number).ToListAsync();
    }

    // ---------- On issues ----------

    /// <summary>An issue's components and versions (empty if the issue isn't visible).</summary>
    public async Task<(List<ProjectComponent> Components, List<ProjectVersion> FixVersions, List<ProjectVersion> AffectsVersions)> GetForIssueAsync(int issueId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var projectId = await db.Issues.Where(i => i.Id == issueId).Select(i => (int?)i.ProjectId).FirstOrDefaultAsync();
        if (projectId == null || !await access.CanAsync(projectId.Value, Permission.ViewProject)) return ([], [], []);
        var components = await db.ProjectComponents.AsNoTracking().Where(c => c.Issues.Any(i => i.Id == issueId)).OrderBy(c => c.Name).ToListAsync();
        var links = await db.IssueVersions.AsNoTracking().Include(l => l.Version).Where(l => l.IssueId == issueId).ToListAsync();
        return (components, Sort(links.Where(l => l.Kind == VersionLinkKind.Fix).Select(l => l.Version)).ToList(),
            Sort(links.Where(l => l.Kind == VersionLinkKind.Affects).Select(l => l.Version)).ToList());
    }

    /// <summary>Component and fix version names per issue (for list columns); issues in projects the acting user can't view are left out.</summary>
    public async Task<(Dictionary<int, List<string>> Components, Dictionary<int, List<string>> FixVersions)> GetNamesForIssuesAsync(IReadOnlyCollection<int> issueIds)
    {
        if (issueIds.Count == 0) return ([], []);
        var visible = await access.VisibleProjectIdsAsync();
        await using var db = await dbFactory.CreateDbContextAsync();
        var ids = issueIds.Distinct().ToList();
        var components = await db.Issues.Where(i => ids.Contains(i.Id) && visible.Contains(i.ProjectId))
            .SelectMany(i => i.Components, (i, c) => new { IssueId = i.Id, c.Name }).ToListAsync();
        var fix = await db.IssueVersions.Where(l => ids.Contains(l.IssueId) && l.Kind == VersionLinkKind.Fix && visible.Contains(l.Version.ProjectId))
            .Select(l => new { l.IssueId, l.Version }).ToListAsync();
        return (components.GroupBy(x => x.IssueId).ToDictionary(g => g.Key, g => g.Select(x => x.Name).Order().ToList()),
            fix.GroupBy(x => x.IssueId).ToDictionary(g => g.Key, g => Sort(g.Select(x => x.Version)).Select(v => v.Name).ToList()));
    }

    /// <summary>Replaces an issue's components ("added component **Frontend**").</summary>
    public async Task SetComponentsAsync(int issueId, IEnumerable<int> componentIds, int? actorId = null)
    {
        var actor = await access.ResolveUserIdAsync(actorId) ?? throw new ForbiddenException("Sign in to edit issues.");
        var want = componentIds.Distinct().ToList();
        await issues.UpdateIssueAsync(issueId, actor, async (issue, db) =>
        {
            await db.Entry(issue).Collection(i => i.Components).LoadAsync();
            var chosen = await db.ProjectComponents.Where(c => c.ProjectId == issue.ProjectId && want.Contains(c.Id)).ToListAsync();
            if (chosen.Count != want.Count) throw new InvalidOperationException("A chosen component doesn't exist in this project.");
            var changes = new List<string>();
            foreach (var removed in issue.Components.Where(c => !want.Contains(c.Id)).ToList())
            {
                issue.Components.Remove(removed);
                changes.Add($"removed component **{removed.Name}**");
            }
            foreach (var added in chosen.Where(c => issue.Components.All(x => x.Id != c.Id)))
            {
                issue.Components.Add(added);
                changes.Add($"added component **{added.Name}**");
            }
            return changes.Count == 0 ? null : string.Join(", ", changes);
        });
    }

    /// <summary>Replaces an issue's fix versions or affects versions.</summary>
    public async Task SetVersionsAsync(int issueId, VersionLinkKind kind, IEnumerable<int> versionIds, int? actorId = null)
    {
        var actor = await access.ResolveUserIdAsync(actorId) ?? throw new ForbiddenException("Sign in to edit issues.");
        var want = versionIds.Distinct().ToList();
        var label = kind == VersionLinkKind.Fix ? "fix version" : "affects version";
        await issues.UpdateIssueAsync(issueId, actor, async (issue, db) =>
        {
            var chosen = await db.ProjectVersions.Where(v => v.ProjectId == issue.ProjectId && want.Contains(v.Id)).ToListAsync();
            if (chosen.Count != want.Count) throw new InvalidOperationException("A chosen version doesn't exist in this project.");
            var current = await db.IssueVersions.Include(l => l.Version).Where(l => l.IssueId == issueId && l.Kind == kind).ToListAsync();
            var changes = new List<string>();
            foreach (var removed in current.Where(l => !want.Contains(l.VersionId)))
            {
                db.IssueVersions.Remove(removed);
                changes.Add($"removed {label} **{removed.Version.Name}**");
            }
            foreach (var added in chosen.Where(v => current.All(l => l.VersionId != v.Id)))
            {
                db.IssueVersions.Add(new IssueVersion { IssueId = issueId, VersionId = added.Id, Kind = kind });
                changes.Add($"added {label} **{added.Name}**");
            }
            return changes.Count == 0 ? null : string.Join(", ", changes);
        });
    }
}
