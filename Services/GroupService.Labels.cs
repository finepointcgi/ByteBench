using GitBench.Data;
using Microsoft.EntityFrameworkCore;

namespace GitBench.Services;

/// <summary>
/// Group labels and milestones: defined once on a group, inherited (read-only) by every subgroup and project below it.
/// <see cref="IssueService.GetProjectAsync"/> merges them into the project's label and milestone lists for the pickers.
/// </summary>
public partial class GroupService
{
    /// <summary>The group's labels and those it inherits from its ancestors.</summary>
    public async Task<List<GroupOwned<Label>>> GetLabelsAsync(int groupId)
    {
        if (!await access.CanAsync(groupId, GroupPermission.ViewGroup)) return [];
        await using var db = await dbFactory.CreateDbContextAsync();
        var chain = await ProjectAccess.GroupChainAsync(db, groupId);
        var paths = await db.Groups.Where(g => chain.Contains(g.Id)).ToDictionaryAsync(g => g.Id, g => g.FullPath);
        var labels = await db.Labels.AsNoTracking().Where(l => l.GroupId != null && chain.Contains(l.GroupId.Value)).OrderBy(l => l.Name).ToListAsync();
        return labels.Select(l => new GroupOwned<Label>(l, paths[l.GroupId!.Value], l.GroupId != groupId)).ToList();
    }

    public async Task<Label> SaveLabelAsync(int groupId, int labelId, string name, string color)
    {
        await access.RequireAsync(groupId, GroupPermission.ManageGroupLabelsAndMilestones);
        name = name.Trim();
        if (name.Length is 0 or > 40) throw new InvalidOperationException("Enter a label name (at most 40 characters).");
        await using var db = await dbFactory.CreateDbContextAsync();
        if (await db.Labels.AnyAsync(l => l.GroupId == groupId && l.Id != labelId && l.Name == name))
            throw new InvalidOperationException("A label with that name already exists in this group.");
        Label label;
        if (labelId == 0) db.Labels.Add(label = new Label { GroupId = groupId });
        else label = await db.Labels.FirstOrDefaultAsync(l => l.Id == labelId && l.GroupId == groupId) ?? throw new ForbiddenException("Label not found in this group.");
        var created = labelId == 0;
        label.Name = name;
        label.Color = color;
        await db.SaveChangesAsync();
        await audit.RecordAsync(created ? "group.label_created" : "group.label_updated", "label", label.Id, details: new { group = await PathOf(db, groupId), name });
        return label;
    }

    public async Task DeleteLabelAsync(int groupId, int labelId)
    {
        await access.RequireAsync(groupId, GroupPermission.ManageGroupLabelsAndMilestones);
        await using var db = await dbFactory.CreateDbContextAsync();
        var label = await db.Labels.Include(l => l.Issues).FirstOrDefaultAsync(l => l.Id == labelId && l.GroupId == groupId)
                    ?? throw new ForbiddenException("Label not found in this group.");
        db.Labels.Remove(label);
        await db.SaveChangesAsync();
        await audit.RecordAsync("group.label_deleted", "label", labelId, details: new { group = await PathOf(db, groupId), label.Name });
    }

    /// <summary>The group's milestones and inherited ones, with progress across the issues the user can see.</summary>
    public async Task<List<GroupOwned<MilestoneSummary>>> GetMilestonesAsync(int groupId)
    {
        if (!await access.CanAsync(groupId, GroupPermission.ViewGroup)) return [];
        var visible = await access.VisibleProjectIdsAsync();
        await using var db = await dbFactory.CreateDbContextAsync();
        var chain = await ProjectAccess.GroupChainAsync(db, groupId);
        var paths = await db.Groups.Where(g => chain.Contains(g.Id)).ToDictionaryAsync(g => g.Id, g => g.FullPath);
        var list = await db.Milestones.AsNoTracking().Where(m => m.GroupId != null && chain.Contains(m.GroupId.Value))
            .OrderBy(m => m.IsClosed).ThenBy(m => m.DueDate == null).ThenBy(m => m.DueDate)
            .Select(m => new MilestoneSummary(m,
                m.Issues.Count(i => visible.Contains(i.ProjectId) && i.Status != IssueStatus.Done),
                m.Issues.Count(i => visible.Contains(i.ProjectId) && i.Status == IssueStatus.Done),
                m.Issues.Where(i => visible.Contains(i.ProjectId)).Sum(i => i.StoryPoints ?? 0),
                m.Issues.Where(i => visible.Contains(i.ProjectId) && i.Status == IssueStatus.Done).Sum(i => i.StoryPoints ?? 0)))
            .ToListAsync();
        return list.Select(s => new GroupOwned<MilestoneSummary>(s, paths[s.Milestone.GroupId!.Value], s.Milestone.GroupId != groupId)).ToList();
    }

    public async Task SaveMilestoneAsync(int groupId, Milestone input)
    {
        await access.RequireAsync(groupId, GroupPermission.ManageGroupLabelsAndMilestones);
        if (string.IsNullOrWhiteSpace(input.Title)) throw new InvalidOperationException("Enter a title.");
        await using var db = await dbFactory.CreateDbContextAsync();
        Milestone m;
        if (input.Id == 0) db.Milestones.Add(m = new Milestone { GroupId = groupId });
        else m = await db.Milestones.FirstOrDefaultAsync(x => x.Id == input.Id && x.GroupId == groupId) ?? throw new ForbiddenException("Milestone not found in this group.");
        var created = input.Id == 0;
        m.Title = input.Title.Trim();
        m.Description = input.Description;
        m.StartDate = input.StartDate;
        m.DueDate = input.DueDate;
        m.IsClosed = input.IsClosed;
        await db.SaveChangesAsync();
        await audit.RecordAsync(created ? "group.milestone_created" : "group.milestone_updated", "milestone", m.Id, details: new { group = await PathOf(db, groupId), m.Title });
    }

    public async Task DeleteMilestoneAsync(int groupId, int milestoneId)
    {
        await access.RequireAsync(groupId, GroupPermission.ManageGroupLabelsAndMilestones);
        await using var db = await dbFactory.CreateDbContextAsync();
        var title = await db.Milestones.Where(m => m.Id == milestoneId && m.GroupId == groupId).Select(m => m.Title).FirstOrDefaultAsync()
                    ?? throw new ForbiddenException("Milestone not found in this group.");
        await db.Issues.Where(i => i.MilestoneId == milestoneId).ExecuteUpdateAsync(s => s.SetProperty(i => i.MilestoneId, (int?)null));
        await db.Milestones.Where(m => m.Id == milestoneId).ExecuteDeleteAsync();
        await audit.RecordAsync("group.milestone_deleted", "milestone", milestoneId, details: new { group = await PathOf(db, groupId), title });
    }

    // ---------- Used by IssueService ----------

    /// <summary>Adds the labels and milestones the project inherits from its group chain to its (loaded) lists, sorted like its own.</summary>
    internal static async Task AddInheritedAsync(AppDbContext db, Project project)
    {
        if (project.GroupId is not { } gid) return;
        var chain = await ProjectAccess.GroupChainAsync(db, gid);
        var labels = await db.Labels.AsNoTracking().Where(l => l.GroupId != null && chain.Contains(l.GroupId.Value)).ToListAsync();
        var milestones = await db.Milestones.AsNoTracking().Where(m => m.GroupId != null && chain.Contains(m.GroupId.Value)).ToListAsync();
        if (labels.Count > 0) project.Labels = [.. project.Labels.Concat(labels).OrderBy(l => l.Name)];
        if (milestones.Count > 0) project.Milestones = [.. project.Milestones.Concat(milestones).OrderBy(m => m.IsClosed).ThenBy(m => m.DueDate)];
    }

    /// <summary>True if a label or milestone owned by this project or group can be used in every one of <paramref name="projectIds"/>.</summary>
    internal static async Task<bool> UsableInAllAsync(AppDbContext db, IEnumerable<int> projectIds, int? ownerProjectId, int? ownerGroupId)
    {
        foreach (var pid in projectIds)
        {
            if (ownerProjectId == pid) continue;
            if (ownerGroupId is not { } gid) return false;
            var projectGroup = await db.Projects.Where(p => p.Id == pid).Select(p => p.GroupId).FirstOrDefaultAsync();
            if (projectGroup == null || !(await ProjectAccess.GroupChainAsync(db, projectGroup.Value)).Contains(gid)) return false;
        }
        return true;
    }
}
