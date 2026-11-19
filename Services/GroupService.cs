using System.Text.RegularExpressions;
using ByteBench.Data;
using Group = ByteBench.Data.Group;
using Microsoft.EntityFrameworkCore;

namespace ByteBench.Services;

public sealed record GroupSummary(int Id, string Name, string FullPath, int? ParentId, string? Description, ProjectVisibility Visibility,
    string Color, int SubgroupCount, int ProjectCount)
{
    public int Depth => FullPath.Count(c => c == '/') + 1;
}

public sealed record GroupProjectInfo(int Id, string Key, string Name, string? Description, ProjectVisibility Visibility, int OpenIssues, int OpenPullRequests);

/// <summary>A group with its visible subgroups and projects, recursively.</summary>
public sealed record GroupNode(GroupSummary Group, List<GroupNode> Subgroups, List<GroupProjectInfo> Projects)
{
    public int TotalProjects => Projects.Count + Subgroups.Sum(s => s.TotalProjects);
    public int TotalSubgroups => Subgroups.Count + Subgroups.Sum(s => s.TotalSubgroups);
    public int TotalOpenIssues => Projects.Sum(p => p.OpenIssues) + Subgroups.Sum(s => s.TotalOpenIssues);
}

/// <summary>A group membership as seen from a group or project: direct, or inherited from <see cref="GroupPath"/>.</summary>
public sealed record GroupMemberInfo(User User, ProjectRole Role, int GroupId, string GroupPath, bool Inherited, DateTime Since);

public sealed record GroupCrumb(int Id, string Name, string FullPath);

/// <summary>A group label or milestone, with the group that owns it (the group itself, or an ancestor it inherits from).</summary>
public sealed record GroupOwned<T>(T Item, string OwnerPath, bool Inherited);

/// <summary>Where a group path leads: the group, a redirect to its current path, or nothing.</summary>
public sealed record GroupLookup(Group? Group, string? RedirectTo);

/// <summary>
/// Groups (GitLab-style namespaces): create, rename, move, delete; members; group labels and milestones; aggregated
/// issues and pull requests; project transfers. Permissions come from <see cref="ProjectAccess"/> (ProjectAccess.Groups.cs).
/// Every change records a <c>group.*</c> audit event.
/// </summary>
public partial class GroupService(IDbContextFactory<AppDbContext> dbFactory, ProjectAccess access, IssueService issues, Audit.AuditLog audit)
{
    private static readonly Regex PathRegex = new(Group.PathPattern, RegexOptions.Compiled);

    /// <summary>Group paths that would clash with app routes under /g/{path}/-/….</summary>
    private static readonly HashSet<string> ReservedPaths = ["-", "new", "api", "admin"];

    // ---------- Reading ----------

    /// <summary>Every group the acting user can view, ordered by full path (so parents come before children).</summary>
    public async Task<List<GroupSummary>> ListAsync()
    {
        var visible = await access.VisibleGroupIdsAsync();
        if (visible.Count == 0) return [];
        var visibleProjects = await access.VisibleProjectIdsAsync();
        await using var db = await dbFactory.CreateDbContextAsync();
        var groups = await db.Groups.AsNoTracking().Where(g => visible.Contains(g.Id)).OrderBy(g => g.FullPath).ToListAsync();
        var projectCounts = await db.Projects.Where(p => p.GroupId != null && visibleProjects.Contains(p.Id))
            .GroupBy(p => p.GroupId!.Value).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count);
        return groups.Select(g => Summary(g, groups.Count(c => c.ParentId == g.Id), projectCounts.GetValueOrDefault(g.Id))).ToList();
    }

    private static GroupSummary Summary(Group g, int subgroups, int projects) =>
        new(g.Id, g.Name, g.FullPath, g.ParentId, g.Description, g.Visibility, g.Color, subgroups, projects);

    /// <summary>
    /// The group at <paramref name="path"/> if the acting user can view it; otherwise, if the path is an old path of a
    /// group they can view, where it lives now.
    /// </summary>
    public async Task<GroupLookup> FindAsync(string path)
    {
        path = NormalizePath(path);
        await using var db = await dbFactory.CreateDbContextAsync();
        var group = await db.Groups.AsNoTracking().Include(g => g.Parent).FirstOrDefaultAsync(g => g.FullPath == path);
        if (group != null) return (await access.VisibleGroupIdsAsync()).Contains(group.Id) ? new(group, null) : new(null, null);
        var target = await db.GroupRedirects.Where(r => r.OldPath == path).Select(r => new { r.GroupId, r.Group.FullPath }).FirstOrDefaultAsync();
        if (target != null && (await access.VisibleGroupIdsAsync()).Contains(target.GroupId)) return new(null, target.FullPath);
        return new(null, null);
    }

    public async Task<Group?> GetAsync(int id)
    {
        if (!(await access.VisibleGroupIdsAsync()).Contains(id)) return null;
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Groups.AsNoTracking().Include(g => g.Parent).FirstOrDefaultAsync(g => g.Id == id);
    }

    /// <summary>The group's ancestors and itself, outermost first, for breadcrumbs (only the ones the user can view are linked by the UI).</summary>
    public async Task<List<GroupCrumb>> CrumbsAsync(int groupId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var chain = await ProjectAccess.GroupChainAsync(db, groupId);
        var groups = await db.Groups.Where(g => chain.Contains(g.Id)).Select(g => new GroupCrumb(g.Id, g.Name, g.FullPath)).ToListAsync();
        return groups.OrderBy(g => g.FullPath.Length).ToList();
    }

    /// <summary>Breadcrumbs for a project page: its group chain (empty for a top-level project or one the user can't view).</summary>
    public async Task<List<GroupCrumb>> ProjectCrumbsAsync(string projectKey)
    {
        var key = projectKey.ToUpperInvariant();
        int? groupId;
        await using (var db = await dbFactory.CreateDbContextAsync())
            groupId = await db.Projects.Where(p => p.Key == key).Select(p => p.GroupId).FirstOrDefaultAsync();
        if (groupId == null) return [];
        var visible = await access.VisibleProjectIdsAsync();
        await using (var db = await dbFactory.CreateDbContextAsync())
            if (!await db.Projects.AnyAsync(p => p.Key == key && visible.Contains(p.Id))) return [];
        return await CrumbsAsync(groupId.Value);
    }

    /// <summary>The group with its visible subgroups and projects as a tree, with open issue and pull request counts.</summary>
    public async Task<GroupNode?> GetTreeAsync(int groupId)
    {
        var visibleGroups = (await access.VisibleGroupIdsAsync()).ToHashSet();
        if (!visibleGroups.Contains(groupId)) return null;
        var visibleProjects = await access.VisibleProjectIdsAsync();
        await using var db = await dbFactory.CreateDbContextAsync();
        var root = await db.Groups.AsNoTracking().FirstOrDefaultAsync(g => g.Id == groupId);
        if (root == null) return null;
        var prefix = root.FullPath + "/";
        var groups = await db.Groups.AsNoTracking().Where(g => g.Id == groupId || g.FullPath.StartsWith(prefix)).ToListAsync();
        groups = groups.Where(g => visibleGroups.Contains(g.Id)).ToList();
        var ids = groups.Select(g => g.Id).ToList();
        var projects = await db.Projects.Where(p => p.GroupId != null && ids.Contains(p.GroupId.Value) && visibleProjects.Contains(p.Id))
            .OrderBy(p => p.Name)
            .Select(p => new
            {
                GroupId = p.GroupId!.Value,
                Info = new GroupProjectInfo(p.Id, p.Key, p.Name, p.Description, p.Visibility,
                    p.Issues.Count(i => i.Status != IssueStatus.Done),
                    db.PullRequests.Count(pr => pr.Repository.ProjectId == p.Id && pr.State == PullRequestState.Open)),
            }).ToListAsync();

        GroupNode Build(Group g)
        {
            var children = groups.Where(c => c.ParentId == g.Id).OrderBy(c => c.Name).Select(Build).ToList();
            var own = projects.Where(p => p.GroupId == g.Id).Select(p => p.Info).ToList();
            return new GroupNode(Summary(g, children.Count, own.Count), children, own);
        }
        return Build(root);
    }

    /// <summary>Ids of the visible projects in the group and all its subgroups.</summary>
    public async Task<List<int>> ProjectIdsAsync(int groupId)
    {
        var visible = await access.VisibleProjectIdsAsync();
        await using var db = await dbFactory.CreateDbContextAsync();
        var groups = await SubtreeIdsAsync(db, groupId);
        return await db.Projects.Where(p => p.GroupId != null && groups.Contains(p.GroupId.Value) && visible.Contains(p.Id)).Select(p => p.Id).ToListAsync();
    }

    /// <summary>The group and all its descendants' ids (descendants share its path prefix).</summary>
    private static async Task<List<int>> SubtreeIdsAsync(AppDbContext db, int groupId)
    {
        var path = await db.Groups.Where(g => g.Id == groupId).Select(g => g.FullPath).FirstOrDefaultAsync();
        if (path == null) return [];
        var prefix = path + "/";
        return await db.Groups.Where(g => g.Id == groupId || g.FullPath.StartsWith(prefix)).Select(g => g.Id).ToListAsync();
    }

    /// <summary>Groups the acting user may create projects or subgroups in (Maintainer or higher), for pickers.</summary>
    public async Task<List<GroupSummary>> CreatableInAsync()
    {
        var all = await ListAsync();
        var uid = await access.ResolveUserIdAsync();
        if (await access.IsAdminAsync()) return all;
        if (uid == null) return [];
        await using var db = await dbFactory.CreateDbContextAsync();
        var roles = await db.GroupMembers.Where(m => m.UserId == uid).ToDictionaryAsync(m => m.GroupId, m => m.Role);
        var byId = all.ToDictionary(g => g.Id);
        bool Can(GroupSummary g)
        {
            // Walk up the visible ancestors; a Maintainer+ membership anywhere on the chain counts.
            for (GroupSummary? cur = g; cur != null; cur = cur.ParentId is { } p && byId.TryGetValue(p, out var parent) ? parent : null)
                if (roles.TryGetValue(cur.Id, out var r) && r >= ProjectAccess.MinimumRole(GroupPermission.CreateInGroup)) return true;
            return false;
        }
        return all.Where(Can).ToList();
    }

    // ---------- Aggregated issues & pull requests ----------

    /// <summary>Issues in every visible project of the group and its subgroups, filtered like a project's issue list.</summary>
    public async Task<List<Issue>> GetIssuesAsync(int groupId, IssueFilter filter, int? projectId = null)
    {
        var ids = await ProjectIdsAsync(groupId);
        if (projectId is { } only) ids = ids.Where(i => i == only).ToList();
        return ids.Count == 0 ? [] : await issues.GetIssuesAsync(ids, filter);
    }

    /// <summary>Pull requests in every visible project of the group and its subgroups, newest activity first.</summary>
    public async Task<List<PullRequest>> GetPullRequestsAsync(int groupId, PullRequestState? state)
    {
        var ids = await ProjectIdsAsync(groupId);
        if (ids.Count == 0) return [];
        await using var db = await dbFactory.CreateDbContextAsync();
        var q = db.PullRequests.AsNoTracking().Include(p => p.Repository).ThenInclude(r => r.Project).Include(p => p.Author).Include(p => p.SourceRepository).ThenInclude(r => r!.Project)
            .Where(p => ids.Contains(p.Repository.ProjectId));
        if (state is { } s) q = q.Where(p => p.State == s);
        return await q.OrderByDescending(p => p.UpdatedAt).Take(500).ToListAsync();
    }

    public async Task<Dictionary<PullRequestState, int>> CountPullRequestsAsync(int groupId)
    {
        var ids = await ProjectIdsAsync(groupId);
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.PullRequests.Where(p => ids.Contains(p.Repository.ProjectId)).GroupBy(p => p.State)
            .Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count);
    }

    // ---------- Create, edit, move, delete ----------

    /// <summary>Creates a group (top-level, or a subgroup of <paramref name="parentId"/>, which needs Maintainer there). Its creator becomes its Owner.</summary>
    public async Task<Group> CreateAsync(string name, string path, string? description, ProjectVisibility visibility, string? color = null,
        int? parentId = null, int? actorId = null)
    {
        var uid = await access.ResolveUserIdAsync(actorId) ?? throw new ForbiddenException("Sign in to create a group.");
        name = name.Trim();
        if (name.Length is 0 or > 100) throw new InvalidOperationException("Enter a name (at most 100 characters).");
        path = ValidatePath(path);
        await using var db = await dbFactory.CreateDbContextAsync();
        Group? parent = null;
        if (parentId is { } pid)
        {
            await access.RequireAsync(pid, GroupPermission.CreateInGroup, uid);
            parent = await db.Groups.FirstOrDefaultAsync(g => g.Id == pid) ?? throw new InvalidOperationException("Parent group not found.");
            if (parent.Depth >= Group.MaxDepth) throw new InvalidOperationException($"Groups can be nested at most {Group.MaxDepth} levels deep.");
            if (parent.Visibility == ProjectVisibility.Private && visibility == ProjectVisibility.Internal)
                throw new InvalidOperationException("A subgroup can't be more visible than its parent group (the parent is Private).");
        }
        var fullPath = parent == null ? path : parent.FullPath + "/" + path;
        if (await db.Groups.AnyAsync(g => g.FullPath == fullPath)) throw new InvalidOperationException($"The path '{fullPath}' is already taken.");
        await db.GroupRedirects.Where(r => r.OldPath == fullPath).ExecuteDeleteAsync(); // a new group reclaims an old path

        var group = new Group
        {
            Name = name, Path = path, FullPath = fullPath, Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim(),
            Visibility = visibility, Color = ValidColor(color), ParentId = parent?.Id,
            Members = [new GroupMember { UserId = uid, Role = ProjectRole.Owner }],
        };
        db.Groups.Add(group);
        await db.SaveChangesAsync();
        await audit.RecordAsync("group.created", "group", group.Id, details: new { path = fullPath, name, visibility = visibility.ToString() }, actorId: uid);
        return group;
    }

    /// <summary>Name, description, visibility and avatar colour (Owner).</summary>
    public async Task UpdateAsync(int groupId, string name, string? description, ProjectVisibility visibility, string? color)
    {
        await access.RequireAsync(groupId, GroupPermission.ManageGroupSettings);
        name = name.Trim();
        if (name.Length is 0 or > 100) throw new InvalidOperationException("Enter a name (at most 100 characters).");
        await using var db = await dbFactory.CreateDbContextAsync();
        var g = await db.Groups.Include(x => x.Parent).FirstAsync(x => x.Id == groupId);
        if (visibility == ProjectVisibility.Internal && g.Parent?.Visibility == ProjectVisibility.Private)
            throw new InvalidOperationException("A subgroup can't be more visible than its parent group (the parent is Private).");
        if (visibility == ProjectVisibility.Private && await db.Groups.AnyAsync(c => c.ParentId == groupId && c.Visibility == ProjectVisibility.Internal))
            throw new InvalidOperationException("Make the Internal subgroups Private first: a subgroup can't be more visible than its parent.");
        var before = new { g.Name, g.Description, visibility = g.Visibility.ToString(), g.Color };
        g.Name = name;
        g.Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        g.Visibility = visibility;
        g.Color = ValidColor(color ?? g.Color);
        await db.SaveChangesAsync();
        var after = new { g.Name, g.Description, visibility = g.Visibility.ToString(), g.Color };
        if (!before.Equals(after)) await audit.RecordAsync("group.settings_changed", "group", groupId, details: new { path = g.FullPath, before, after });
    }

    /// <summary>Changes the group's own path segment (Owner). Old paths of the group and its subgroups keep redirecting.</summary>
    public async Task ChangePathAsync(int groupId, string newPath)
    {
        await access.RequireAsync(groupId, GroupPermission.ManageGroupSettings);
        newPath = ValidatePath(newPath);
        await using var db = await dbFactory.CreateDbContextAsync();
        var g = await db.Groups.Include(x => x.Parent).FirstAsync(x => x.Id == groupId);
        if (g.Path == newPath) return;
        var old = g.FullPath;
        var full = g.Parent == null ? newPath : g.Parent.FullPath + "/" + newPath;
        await RepathAsync(db, g, full);
        g.Path = newPath;
        await db.SaveChangesAsync();
        await audit.RecordAsync("group.path_changed", "group", groupId, details: new { from = old, to = full });
    }

    /// <summary>
    /// Moves the group under <paramref name="newParentId"/> (null: top level). Needs Owner of the group and Maintainer in the
    /// new parent. Old paths redirect; group labels and milestones the projects lose access to are copied into those projects.
    /// </summary>
    public async Task MoveAsync(int groupId, int? newParentId)
    {
        await access.RequireAsync(groupId, GroupPermission.ManageGroupSettings);
        if (newParentId is { } np) await access.RequireAsync(np, GroupPermission.CreateInGroup);
        await using var db = await dbFactory.CreateDbContextAsync();
        var g = await db.Groups.FirstAsync(x => x.Id == groupId);
        if (g.ParentId == newParentId) return;
        var subtree = await SubtreeIdsAsync(db, groupId);
        Group? parent = null;
        if (newParentId is { } pid)
        {
            if (subtree.Contains(pid)) throw new InvalidOperationException("A group can't be moved into itself or one of its subgroups.");
            parent = await db.Groups.FirstAsync(x => x.Id == pid);
            var height = (await db.Groups.Where(x => subtree.Contains(x.Id)).Select(x => x.FullPath).ToListAsync())
                .Max(p => p.Count(c => c == '/')) - (g.Depth - 1) + 1;
            if (parent.Depth + height > Group.MaxDepth)
                throw new InvalidOperationException($"That would nest groups more than {Group.MaxDepth} levels deep.");
            if (parent.Visibility == ProjectVisibility.Private
                && await db.Groups.AnyAsync(x => subtree.Contains(x.Id) && x.Visibility == ProjectVisibility.Internal))
                throw new InvalidOperationException("The target group is Private, but this group or one of its subgroups is Internal. Make them Private first.");
        }
        var old = g.FullPath;
        var full = parent == null ? g.Path : parent.FullPath + "/" + g.Path;
        await RepathAsync(db, g, full);
        g.ParentId = parent?.Id;
        await db.SaveChangesAsync();
        var projectIds = await db.Projects.Where(p => p.GroupId != null && subtree.Contains(p.GroupId.Value)).Select(p => p.Id).ToListAsync();
        await DetachInheritedAsync(db, projectIds);
        await audit.RecordAsync("group.moved", "group", groupId, details: new { from = old, to = full });
    }

    /// <summary>Gives the group and its descendants new full paths (tracked; caller saves), leaving redirects at the old ones.</summary>
    private static async Task RepathAsync(AppDbContext db, Group g, string newFull)
    {
        if (await db.Groups.AnyAsync(x => x.FullPath == newFull && x.Id != g.Id)) throw new InvalidOperationException($"The path '{newFull}' is already taken.");
        if (newFull.Count(c => c == '/') >= Group.MaxDepth) throw new InvalidOperationException($"Groups can be nested at most {Group.MaxDepth} levels deep.");
        var oldFull = g.FullPath;
        var prefix = oldFull + "/";
        var descendants = await db.Groups.Where(x => x.FullPath.StartsWith(prefix)).ToListAsync();
        var moves = new List<(Group Group, string Old, string New)> { (g, oldFull, newFull) };
        moves.AddRange(descendants.Select(d => (d, d.FullPath, newFull + d.FullPath[oldFull.Length..])));
        var newPaths = moves.Select(m => m.New).ToList();
        var oldPaths = moves.Select(m => m.Old).ToList();
        // New paths are no longer old paths of anything; old paths now redirect here (replacing older redirects at the same path).
        await db.GroupRedirects.Where(r => newPaths.Contains(r.OldPath) || oldPaths.Contains(r.OldPath)).ExecuteDeleteAsync();
        foreach (var (group, old, @new) in moves)
        {
            group.FullPath = @new;
            db.GroupRedirects.Add(new GroupRedirect { OldPath = old, GroupId = group.Id });
        }
    }

    /// <summary>
    /// Deletes the group (Owner). An empty group just goes; a group with subgroups or projects needs <paramref name="cascade"/>
    /// and its full path typed as <paramref name="confirmPath"/>, and then takes all of them (and their issues and repositories) with it.
    /// </summary>
    public async Task DeleteAsync(int groupId, bool cascade = false, string? confirmPath = null)
    {
        await access.RequireAsync(groupId, GroupPermission.DeleteGroup);
        await using var db = await dbFactory.CreateDbContextAsync();
        var g = await db.Groups.AsNoTracking().FirstAsync(x => x.Id == groupId);
        var subtree = await SubtreeIdsAsync(db, groupId);
        var projectIds = await db.Projects.Where(p => p.GroupId != null && subtree.Contains(p.GroupId.Value)).Select(p => p.Id).ToListAsync();
        var empty = subtree.Count == 1 && projectIds.Count == 0;
        if (!empty)
        {
            if (!cascade) throw new InvalidOperationException("This group still has subgroups or projects. Delete or move them first, or delete everything.");
            if (confirmPath?.Trim() != g.FullPath) throw new InvalidOperationException($"Type the group's path ({g.FullPath}) to confirm.");
        }
        foreach (var pid in projectIds) await issues.DeleteProjectAsync(pid); // also removes repositories and files; checks DeleteProject
        // Deepest first: the parent link is Restrict.
        var depths = await db.Groups.Where(x => subtree.Contains(x.Id)).Select(x => new { x.Id, x.FullPath }).ToListAsync();
        foreach (var level in depths.GroupBy(x => x.FullPath.Count(c => c == '/')).OrderByDescending(l => l.Key))
        {
            var ids = level.Select(x => x.Id).ToList();
            await db.Groups.Where(x => ids.Contains(x.Id)).ExecuteDeleteAsync();
        }
        await audit.RecordAsync("group.deleted", "group", groupId, details: new { path = g.FullPath, g.Name, subgroups = subtree.Count - 1, projects = projectIds.Count });
    }

    // ---------- Projects ----------

    /// <summary>
    /// Moves a project into <paramref name="targetGroupId"/> (null: top level). Needs Owner of the project and Maintainer in
    /// the target group. Its key and URLs don't change; group labels and milestones it loses are copied into it.
    /// </summary>
    public async Task TransferProjectAsync(int projectId, int? targetGroupId)
    {
        await access.RequireAsync(projectId, Permission.ManageProjectSettings);
        if (targetGroupId is { } tg) await access.RequireAsync(tg, GroupPermission.CreateInGroup);
        await using var db = await dbFactory.CreateDbContextAsync();
        var p = await db.Projects.Include(x => x.Group).FirstAsync(x => x.Id == projectId);
        if (p.GroupId == targetGroupId) return;
        var from = p.Group?.FullPath;
        var to = targetGroupId is { } t ? await db.Groups.Where(g => g.Id == t).Select(g => g.FullPath).FirstAsync() : null;
        p.GroupId = targetGroupId;
        await db.SaveChangesAsync();
        await DetachInheritedAsync(db, [projectId]);
        await audit.RecordAsync("project.transferred", "project", projectId, projectId, new { p.Key, from, to });
    }

    /// <summary>
    /// After a transfer or move: issues in these projects that use a group label or milestone from a group that is no longer
    /// one of the project's ancestors get a project copy of it instead (reusing a project label with the same name).
    /// </summary>
    private static async Task DetachInheritedAsync(AppDbContext db, List<int> projectIds)
    {
        foreach (var projectId in projectIds)
        {
            var groupId = await db.Projects.Where(p => p.Id == projectId).Select(p => p.GroupId).FirstAsync();
            var chain = groupId is { } gid ? await ProjectAccess.GroupChainAsync(db, gid) : [];

            var stray = await db.Issues.Include(i => i.Labels)
                .Where(i => i.ProjectId == projectId && i.Labels.Any(l => l.GroupId != null && !chain.Contains(l.GroupId.Value)))
                .ToListAsync();
            if (stray.Count > 0)
            {
                var own = await db.Labels.Where(l => l.ProjectId == projectId).ToListAsync();
                foreach (var issue in stray)
                    foreach (var label in issue.Labels.Where(l => l.GroupId != null && !chain.Contains(l.GroupId.Value)).ToList())
                    {
                        var copy = own.FirstOrDefault(l => l.Name.Equals(label.Name, StringComparison.OrdinalIgnoreCase));
                        if (copy == null)
                        {
                            copy = new Label { ProjectId = projectId, Name = label.Name, Color = label.Color };
                            db.Labels.Add(copy);
                            own.Add(copy);
                        }
                        issue.Labels.Remove(label);
                        issue.Labels.Add(copy);
                    }
                await db.SaveChangesAsync();
            }

            var milestones = await db.Issues.Where(i => i.ProjectId == projectId && i.Milestone != null && i.Milestone.GroupId != null && !chain.Contains(i.Milestone.GroupId.Value))
                .Select(i => i.Milestone!).Distinct().AsNoTracking().ToListAsync();
            foreach (var m in milestones)
            {
                var copy = new Milestone { ProjectId = projectId, Title = m.Title, Description = m.Description, StartDate = m.StartDate, DueDate = m.DueDate, IsClosed = m.IsClosed };
                db.Milestones.Add(copy);
                await db.SaveChangesAsync();
                await db.Issues.Where(i => i.ProjectId == projectId && i.MilestoneId == m.Id).ExecuteUpdateAsync(s => s.SetProperty(i => i.MilestoneId, copy.Id));
            }
        }
    }

    // ---------- Members ----------

    /// <summary>The group's direct members, then members inherited from its ancestors (each marked with its source group).</summary>
    public async Task<List<GroupMemberInfo>> GetMembersAsync(int groupId)
    {
        if (!await access.CanAsync(groupId, GroupPermission.ViewGroup)) return [];
        await using var db = await dbFactory.CreateDbContextAsync();
        var chain = await ProjectAccess.GroupChainAsync(db, groupId);
        return await MembersOfChainAsync(db, chain, groupId);
    }

    /// <summary>Members a project inherits from its group and the group's ancestors (read-only on the project's Members page).</summary>
    public async Task<List<GroupMemberInfo>> GetInheritedProjectMembersAsync(int projectId)
    {
        if (!await access.CanAsync(projectId, Permission.ViewProject)) return [];
        await using var db = await dbFactory.CreateDbContextAsync();
        var groupId = await db.Projects.Where(p => p.Id == projectId).Select(p => p.GroupId).FirstOrDefaultAsync();
        if (groupId == null) return [];
        return await MembersOfChainAsync(db, await ProjectAccess.GroupChainAsync(db, groupId.Value), null);
    }

    private static async Task<List<GroupMemberInfo>> MembersOfChainAsync(AppDbContext db, List<int> chain, int? directGroupId)
    {
        var rows = await db.GroupMembers.AsNoTracking().Include(m => m.User).Include(m => m.Group)
            .Where(m => chain.Contains(m.GroupId)).ToListAsync();
        return rows.Select(m => new GroupMemberInfo(m.User, m.Role, m.GroupId, m.Group.FullPath, m.GroupId != directGroupId, m.CreatedAt))
            .OrderBy(m => m.Inherited).ThenByDescending(m => m.Role).ThenBy(m => m.User.DisplayName).ToList();
    }

    /// <summary>Adds the user with this username or e-mail address as a direct member (Owner).</summary>
    public async Task<GroupMember> AddMemberAsync(int groupId, string userNameOrEmail, ProjectRole role)
    {
        await access.RequireAsync(groupId, GroupPermission.ManageGroupMembers);
        var login = userNameOrEmail.Trim().ToUpperInvariant();
        if (login.Length == 0) throw new InvalidOperationException("Enter a username or e-mail address.");
        await using var db = await dbFactory.CreateDbContextAsync();
        var user = await db.Users.FirstOrDefaultAsync(u => u.NormalizedUserName == login || u.NormalizedEmail == login)
                   ?? throw new InvalidOperationException($"No user '{userNameOrEmail.Trim()}' was found.");
        if (await db.GroupMembers.AnyAsync(m => m.GroupId == groupId && m.UserId == user.Id))
            throw new InvalidOperationException($"{user.DisplayName} is already a member of this group.");
        var member = new GroupMember { GroupId = groupId, UserId = user.Id, Role = role };
        db.GroupMembers.Add(member);
        await db.SaveChangesAsync();
        await audit.RecordAsync("group.member_added", "user", user.Id, details: new { group = await PathOf(db, groupId), user = user.UserName, role = role.ToString() });
        member.User = user;
        return member;
    }

    public async Task ChangeMemberRoleAsync(int groupId, int memberUserId, ProjectRole role)
    {
        await access.RequireAsync(groupId, GroupPermission.ManageGroupMembers);
        await using var db = await dbFactory.CreateDbContextAsync();
        var m = await db.GroupMembers.FirstOrDefaultAsync(x => x.GroupId == groupId && x.UserId == memberUserId)
                ?? throw new InvalidOperationException("That user is not a direct member of this group.");
        if (m.Role == role) return;
        if (m.Role == ProjectRole.Owner && await IsLastOwnerAsync(db, groupId))
            throw new InvalidOperationException("The last Owner of a top-level group can't be demoted. Make someone else an Owner first.");
        var old = m.Role;
        m.Role = role;
        await db.SaveChangesAsync();
        await audit.RecordAsync("group.member_role_changed", "user", memberUserId, details: new { group = await PathOf(db, groupId), from = old.ToString(), to = role.ToString() });
    }

    public async Task RemoveMemberAsync(int groupId, int memberUserId)
    {
        await access.RequireAsync(groupId, GroupPermission.ManageGroupMembers);
        await using var db = await dbFactory.CreateDbContextAsync();
        var m = await db.GroupMembers.FirstOrDefaultAsync(x => x.GroupId == groupId && x.UserId == memberUserId)
                ?? throw new InvalidOperationException("That user is not a direct member of this group.");
        if (m.Role == ProjectRole.Owner && await IsLastOwnerAsync(db, groupId))
            throw new InvalidOperationException("The last Owner of a top-level group can't be removed. Make someone else an Owner first.");
        db.GroupMembers.Remove(m);
        await db.SaveChangesAsync();
        await audit.RecordAsync("group.member_removed", "user", memberUserId, details: new { group = await PathOf(db, groupId), role = m.Role.ToString() });
    }

    /// <summary>Subgroups inherit their parents' Owners, so only a top-level group must keep a direct Owner.</summary>
    private static async Task<bool> IsLastOwnerAsync(AppDbContext db, int groupId) =>
        await db.Groups.AnyAsync(g => g.Id == groupId && g.ParentId == null)
        && await db.GroupMembers.CountAsync(x => x.GroupId == groupId && x.Role == ProjectRole.Owner) <= 1;

    private static Task<string> PathOf(AppDbContext db, int groupId) => db.Groups.Where(g => g.Id == groupId).Select(g => g.FullPath).FirstAsync();

    // ---------- Helpers ----------

    public static string NormalizePath(string path) => path.Trim().Trim('/').ToLowerInvariant();

    private static string ValidatePath(string path)
    {
        path = path.Trim().ToLowerInvariant();
        if (!PathRegex.IsMatch(path)) throw new InvalidOperationException("The path must be 1-50 lowercase letters, digits, '.', '_' or '-', starting with a letter or digit.");
        if (ReservedPaths.Contains(path)) throw new InvalidOperationException($"'{path}' is reserved. Choose another path.");
        return path;
    }

    private static string ValidColor(string? color) =>
        color is { Length: 7 } c && c[0] == '#' && c[1..].All(Uri.IsHexDigit) ? c.ToLowerInvariant() : "#6366f1";

    /// <summary>"Platform Team" → "platform-team".</summary>
    public static string SuggestPath(string name)
    {
        var slug = Regex.Replace(name.Trim().ToLowerInvariant(), "[^a-z0-9_.]+", "-").Trim('-', '.', '_');
        return slug.Length > 50 ? slug[..50].TrimEnd('-') : slug;
    }
}
