using ByteBench.Data;
using Microsoft.EntityFrameworkCore;

namespace ByteBench.Services.Fields;

/// <summary>A custom field and its value on one issue.</summary>
public sealed record IssueFieldValue(CustomField Field, FieldValue Value);

/// <summary>
/// Custom fields: definitions (Maintainer+, <see cref="Permission.ManageLabelsAndMilestones"/>) and values on issues
/// (<see cref="Permission.EditIssues"/>). Every rule about values is in <see cref="CustomFieldRules"/>; this class adds
/// permissions, user resolution, required checks and activity (changes go through <see cref="IssueService.UpdateIssueAsync"/>,
/// so they are recorded, notified and sent to webhooks like any other issue change).
/// <para>Reading API for other features (search, import/export): <see cref="GetFieldsAsync"/>, <see cref="GetValuesAsync(IReadOnlyCollection{int})"/>
/// and <see cref="CustomFieldRules.ToText"/>/<see cref="CustomFieldRules.ToJson"/>.</para>
/// </summary>
public class CustomFieldService(IDbContextFactory<AppDbContext> dbFactory, ProjectAccess access, IssueService issues, Audit.AuditLog audit)
{
    // ---------- Definitions ----------

    /// <summary>The project's fields in display order; empty if the acting user can't view the project.</summary>
    public async Task<List<CustomField>> GetFieldsAsync(int projectId)
    {
        if (!await access.CanAsync(projectId, Permission.ViewProject)) return [];
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.CustomFields.AsNoTracking().Where(f => f.ProjectId == projectId).OrderBy(f => f.Order).ThenBy(f => f.Id).ToListAsync();
    }

    /// <summary>Number of issues with a value, per field id (for the settings page).</summary>
    public async Task<Dictionary<int, int>> UsageAsync(int projectId)
    {
        if (!await access.CanAsync(projectId, Permission.ViewProject)) return [];
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.CustomFieldValues.Where(v => v.Field.ProjectId == projectId)
            .GroupBy(v => v.FieldId).Select(g => new { g.Key, Count = g.Select(v => v.IssueId).Distinct().Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count);
    }

    /// <summary>Creates (Id 0) or updates a field. The type can't change once issues have values for it.</summary>
    public async Task<CustomField> SaveFieldAsync(CustomField field)
    {
        await access.RequireAsync(field.ProjectId, Permission.ManageLabelsAndMilestones);
        CustomFieldRules.ValidateDefinition(field);
        await using var db = await dbFactory.CreateDbContextAsync();
        if (field.Type == CustomFieldType.User && field.DefaultValue != null)
        {
            var uid = await db.Users.Where(u => u.UserName == field.DefaultValue).Select(u => (int?)u.Id).FirstOrDefaultAsync();
            if (uid == null || !await access.CanAsync(field.ProjectId, Permission.ViewProject, uid))
                throw new InvalidOperationException($"Default value: '{field.DefaultValue}' is not a user who can see this project.");
        }
        if (await db.CustomFields.AnyAsync(f => f.ProjectId == field.ProjectId && f.Id != field.Id && f.Key == field.Key))
            throw new InvalidOperationException($"Another field already uses the key '{field.Key}'.");
        if (await db.CustomFields.AnyAsync(f => f.ProjectId == field.ProjectId && f.Id != field.Id && f.Name.ToLower() == field.Name.ToLower()))
            throw new InvalidOperationException($"Another field is already called '{field.Name}'.");

        string action;
        if (field.Id == 0)
        {
            field.Order = (await db.CustomFields.Where(f => f.ProjectId == field.ProjectId).MaxAsync(f => (int?)f.Order) ?? 0) + 1;
            field.CreatedAt = DateTime.UtcNow;
            db.CustomFields.Add(field);
            action = "custom_field.created";
        }
        else
        {
            var existing = await db.CustomFields.FirstOrDefaultAsync(f => f.Id == field.Id && f.ProjectId == field.ProjectId)
                           ?? throw new ForbiddenException("Field not found in this project.");
            if (existing.Type != field.Type && await db.CustomFieldValues.AnyAsync(v => v.FieldId == field.Id))
                throw new InvalidOperationException("The type can't change while issues have values for this field. Delete the field and create a new one instead.");
            existing.Key = field.Key;
            existing.Name = field.Name;
            existing.Description = field.Description;
            existing.Type = field.Type;
            existing.Required = field.Required;
            existing.DefaultValue = field.DefaultValue;
            existing.Options = field.Options;
            existing.IssueTypes = field.IssueTypes;
            action = "custom_field.updated";
        }
        await db.SaveChangesAsync();
        await audit.RecordAsync(action, "custom_field", field.Id, field.ProjectId,
            new { field.Key, field.Name, type = field.Type.ToString(), field.Required, field.Options, issueTypes = field.IssueTypes.Select(t => t.ToString()) });
        return field;
    }

    /// <summary>Deletes a field and every value of it.</summary>
    public async Task DeleteFieldAsync(int fieldId)
    {
        var projectId = await access.ProjectOf(d => d.CustomFields.Where(f => f.Id == fieldId).Select(f => (int?)f.ProjectId));
        await access.RequireAsync(projectId, Permission.ManageLabelsAndMilestones);
        await using var db = await dbFactory.CreateDbContextAsync();
        var gone = await db.CustomFields.Where(f => f.Id == fieldId).Select(f => new { f.Key, f.Name }).FirstOrDefaultAsync();
        await db.CustomFields.Where(f => f.Id == fieldId).ExecuteDeleteAsync(); // values cascade
        await audit.RecordAsync("custom_field.deleted", "custom_field", fieldId, projectId, gone);
    }

    /// <summary>Moves a field one place up (-1) or down (+1) in the display order.</summary>
    public async Task MoveFieldAsync(int fieldId, int delta)
    {
        var projectId = await access.ProjectOf(d => d.CustomFields.Where(f => f.Id == fieldId).Select(f => (int?)f.ProjectId));
        await access.RequireAsync(projectId, Permission.ManageLabelsAndMilestones);
        await using var db = await dbFactory.CreateDbContextAsync();
        var list = await db.CustomFields.Where(f => f.ProjectId == projectId).OrderBy(f => f.Order).ThenBy(f => f.Id).ToListAsync();
        var idx = list.FindIndex(f => f.Id == fieldId);
        var to = idx + Math.Sign(delta);
        if (idx < 0 || to < 0 || to >= list.Count) return;
        (list[idx], list[to]) = (list[to], list[idx]);
        for (var i = 0; i < list.Count; i++) list[i].Order = i + 1;
        await db.SaveChangesAsync();
    }

    // ---------- Values ----------

    /// <summary>The fields that apply to the issue's type, each with its value (empty when unset). Empty if the issue isn't visible.</summary>
    public async Task<List<IssueFieldValue>> GetIssueValuesAsync(int issueId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var issue = await db.Issues.Where(i => i.Id == issueId).Select(i => new { i.ProjectId, i.Type }).FirstOrDefaultAsync();
        if (issue == null || !await access.CanAsync(issue.ProjectId, Permission.ViewProject)) return [];
        var fields = await db.CustomFields.AsNoTracking().Where(f => f.ProjectId == issue.ProjectId).OrderBy(f => f.Order).ThenBy(f => f.Id).ToListAsync();
        var rows = await db.CustomFieldValues.AsNoTracking().Where(v => v.IssueId == issueId).ToListAsync();
        return fields.Where(f => f.AppliesTo(issue.Type) || rows.Any(r => r.FieldId == f.Id))
            .Select(f => new IssueFieldValue(f, CustomFieldRules.FromRows(f, rows.Where(r => r.FieldId == f.Id)))).ToList();
    }

    /// <summary>
    /// Values of many issues at once, by issue id then field id (only non-empty values; issues the acting user can't view
    /// are left out). Look the fields up with <see cref="GetFieldsAsync"/>.
    /// </summary>
    public async Task<Dictionary<int, Dictionary<int, FieldValue>>> GetValuesAsync(IReadOnlyCollection<int> issueIds)
    {
        if (issueIds.Count == 0) return [];
        var visible = await access.VisibleProjectIdsAsync();
        await using var db = await dbFactory.CreateDbContextAsync();
        return await LoadValuesAsync(db, issueIds, visible);
    }

    /// <summary>As <see cref="GetValuesAsync(IReadOnlyCollection{int})"/>, inside an existing context and without a permission check.</summary>
    public static async Task<Dictionary<int, Dictionary<int, FieldValue>>> LoadValuesAsync(AppDbContext db, IReadOnlyCollection<int> issueIds,
        IReadOnlyList<int>? visibleProjects = null)
    {
        var ids = issueIds.Distinct().ToList();
        var q = db.CustomFieldValues.AsNoTracking().Include(v => v.Field).Where(v => ids.Contains(v.IssueId));
        if (visibleProjects != null) q = q.Where(v => visibleProjects.Contains(v.Field.ProjectId));
        var rows = await q.ToListAsync();
        return rows.GroupBy(r => r.IssueId).ToDictionary(g => g.Key,
            g => g.GroupBy(r => r.FieldId).ToDictionary(fg => fg.Key, fg => CustomFieldRules.FromRows(fg.First().Field, fg)));
    }

    /// <summary>Sets one field on an issue (null or empty clears it). Records "set **Field** to **value**" in the activity.</summary>
    public Task SetValueAsync(int issueId, int fieldId, object? input, int? actorId = null) =>
        SetValuesAsync(issueId, new Dictionary<int, object?> { [fieldId] = input }, actorId);

    /// <summary>Sets several fields in one change (one activity entry). All values are checked before anything is saved.</summary>
    public async Task SetValuesAsync(int issueId, IReadOnlyDictionary<int, object?> inputs, int? actorId = null)
    {
        var actor = await access.ResolveUserIdAsync(actorId) ?? throw new ForbiddenException("Sign in to edit issues.");
        await access.RequireForIssueAsync(issueId, Permission.EditIssues, actor);
        await issues.UpdateIssueAsync(issueId, actor, async (issue, db) =>
        {
            var fields = await db.CustomFields.AsNoTracking().Where(f => f.ProjectId == issue.ProjectId).ToDictionaryAsync(f => f.Id);
            var rows = await db.CustomFieldValues.Where(v => v.IssueId == issueId).ToListAsync();
            var changes = new List<string>();
            foreach (var (fieldId, input) in inputs)
            {
                var field = fields.GetValueOrDefault(fieldId) ?? throw new InvalidOperationException("That field doesn't exist in this project.");
                var current = rows.Where(r => r.FieldId == fieldId).ToList();
                var before = CustomFieldRules.FromRows(field, current);
                var after = await ResolveAsync(db, access, issue.ProjectId, field, CustomFieldRules.Parse(field, input));
                if (!field.AppliesTo(issue.Type) && !after.IsEmpty)
                    throw new InvalidOperationException($"{field.Name} doesn't apply to {issue.Type} issues.");
                if (field.AppliesTo(issue.Type)) CustomFieldRules.RequireValue(field, after);
                if (before.Same(after)) continue;
                db.CustomFieldValues.RemoveRange(current);
                db.CustomFieldValues.AddRange(CustomFieldRules.ToRows(field, issueId, after));
                var names = after.UserId is { } uid ? await db.Users.Where(u => u.Id == uid).ToDictionaryAsync(u => u.Id, u => u.DisplayName) : [];
                changes.Add(CustomFieldRules.ChangeText(field, before, after, id => names.GetValueOrDefault(id)));
            }
            return changes.Count == 0 ? null : string.Join(", ", changes);
        });
    }

    /// <summary>Resolves a username to a user id and checks the user can see the project.</summary>
    private static async Task<FieldValue> ResolveAsync(AppDbContext db, ProjectAccess access, int projectId, CustomField field, FieldValue v)
    {
        if (field.Type != CustomFieldType.User || v.IsEmpty) return v;
        var id = v.UserId ?? await db.Users.Where(u => u.UserName == v.UserName).Select(u => (int?)u.Id).FirstOrDefaultAsync();
        if (id == null || !await access.CanAsync(projectId, Permission.ViewProject, id))
            throw new InvalidOperationException($"{field.Name}: '{v.UserName ?? v.UserId?.ToString()}' is not a user who can see this project.");
        return new FieldValue { UserId = id };
    }

    /// <summary>
    /// Called by <see cref="IssueService.CreateIssueAsync"/> before a new issue is saved: applies <see cref="Issue.FieldDraft"/>
    /// (custom field values, components, versions) and defaults, checks required fields, sets the assignee from a component's
    /// default assignee and the remaining estimate from the original one. Throws <see cref="InvalidOperationException"/>.
    /// </summary>
    public static async Task PrepareNewIssueAsync(AppDbContext db, ProjectAccess access, Issue issue)
    {
        var draft = issue.FieldDraft ?? new IssueFieldDraft();
        if (draft.Error != null) throw new InvalidOperationException(draft.Error);
        var fields = await db.CustomFields.AsNoTracking().Where(f => f.ProjectId == issue.ProjectId).OrderBy(f => f.Order).ToListAsync();
        foreach (var id in draft.Values.Keys.Where(id => fields.All(f => f.Id != id)))
            throw new InvalidOperationException($"Custom field {id} doesn't exist in this project.");
        foreach (var field in fields)
        {
            var given = draft.Values.TryGetValue(field.Id, out var input);
            if (!field.AppliesTo(issue.Type))
            {
                if (given && !CustomFieldRules.Parse(field, input).IsEmpty) throw new InvalidOperationException($"{field.Name} doesn't apply to {issue.Type} issues.");
                continue;
            }
            var value = given ? CustomFieldRules.Parse(field, input) : CustomFieldRules.Parse(field, field.DefaultValue);
            value = await ResolveAsync(db, access, issue.ProjectId, field, value);
            CustomFieldRules.RequireValue(field, value);
            foreach (var row in CustomFieldRules.ToRows(field, 0, value)) issue.CustomFieldValues.Add(row);
        }

        if (draft.ComponentIds.Count > 0)
        {
            var ids = draft.ComponentIds.Distinct().ToList();
            var components = await db.ProjectComponents.Where(c => c.ProjectId == issue.ProjectId && ids.Contains(c.Id)).ToListAsync();
            if (components.Count != ids.Count) throw new InvalidOperationException("A chosen component doesn't exist in this project.");
            issue.Components = components;
            issue.AssigneeId ??= components.OrderBy(c => ids.IndexOf(c.Id)).Select(c => c.DefaultAssigneeId).FirstOrDefault(a => a != null);
        }
        foreach (var (kind, list) in new[] { (VersionLinkKind.Fix, draft.FixVersionIds), (VersionLinkKind.Affects, draft.AffectsVersionIds) })
        {
            var ids = list.Distinct().ToList();
            if (ids.Count == 0) continue;
            if (await db.ProjectVersions.CountAsync(v => v.ProjectId == issue.ProjectId && ids.Contains(v.Id)) != ids.Count)
                throw new InvalidOperationException("A chosen version doesn't exist in this project.");
            foreach (var id in ids) issue.VersionLinks.Add(new IssueVersion { VersionId = id, Kind = kind });
        }

        if (issue.OriginalEstimateMinutes is < 0 || issue.RemainingEstimateMinutes is < 0) throw new InvalidOperationException("Estimates can't be negative.");
        issue.RemainingEstimateMinutes ??= issue.OriginalEstimateMinutes;
    }
}
