using System.Globalization;
using System.Text.Json;

namespace ByteBench.Services.Workbench;

/// <summary>One task of a Workbench plan as the sync sees it. <see cref="Depth"/> is 0 for top-level tasks.</summary>
public sealed record PlanNode(Guid Id, Guid? ParentId, int Order, string Title, DateOnly? Start, DateOnly? Finish, string? WbsCode, string? Outline, string? Notes,
    decimal? Hours, bool IsSummary, int Depth, IReadOnlyList<string> Emails);

public sealed record PlanSnapshot(WbProject Project, IReadOnlyList<PlanNode> Nodes, IReadOnlyList<(Guid Predecessor, Guid Successor)> Dependencies, bool HoursHidden);

/// <summary>
/// Reads a project's whole plan from the Workbench API, uncached (a sync wants the truth): the plan sheet's tasks and columns
/// (<c>GET /sheets/{id}</c>, <c>/items</c>, <c>/plan</c>, <c>/tasks</c>, <c>/dependencies</c>), the WBS elements (<c>/projects/{id}/wbs</c>) for
/// WBS codes and, when the key's roles allow, planned hours (<c>/projects/{id}/financials/wbs</c>). Read-only.
/// </summary>
public sealed class WorkbenchPlanReader(WorkbenchClient client, WorkbenchDirectory directory)
{
    private static readonly string[] HoursColumns = ["Planned hours", "Hours", "Work", "Estimate", "Budget hours"];

    public async Task<WbResult<PlanSnapshot>> ReadAsync(string projectCode, CancellationToken ct = default)
    {
        var found = await directory.FindProjectAsync(projectCode);
        if (!found.Ok) return found.As<PlanSnapshot>();
        var project = found.Value!;
        if (project.SheetId is not { } sheetId) return WbResult<PlanSnapshot>.Fail(WbStatus.NotFound, $"Workbench project {project.Code} has no plan sheet.");

        var sheet = await client.GetAsync($"/sheets/{sheetId}", ct);
        if (!sheet.Ok) return sheet.As<PlanSnapshot>();
        var items = await client.GetAsync($"/sheets/{sheetId}/items", ct);
        if (!items.Ok) return items.As<PlanSnapshot>();
        var plan = await client.GetAsync($"/sheets/{sheetId}/plan", ct);
        var tasks = await client.GetAsync($"/sheets/{sheetId}/tasks", ct);
        var deps = await client.GetAsync($"/sheets/{sheetId}/dependencies", ct);
        var wbs = await directory.ListWbsAsync(project.Id, fresh: true);
        var hours = await directory.GetHoursAsync(project.Id, fresh: true);

        Guid? ColumnId(params string[] names) => Arr(Prop(sheet.Value, "columns")).Select(c => (Id: Id(c, "id"), Name: Str(c, "name"))).FirstOrDefault(c => c.Id != null && names.Any(n => string.Equals(c.Name, n, StringComparison.OrdinalIgnoreCase))).Id;
        var notesCol = ColumnId("Notes", "Description");
        var hoursCol = ColumnId(HoursColumns);

        var summary = new Dictionary<Guid, string?>();
        if (plan.Ok) foreach (var t in Arr(Prop(plan.Value, "tasks"))) if (Id(t, "itemId") is { } id) summary[id] = Prop(t, "isSummary").ValueKind == JsonValueKind.True ? "S" + Str(t, "outlineNumber") : Str(t, "outlineNumber");
        var codeByItem = wbs.Ok ? wbs.Value!.Where(w => w.ItemId != null).GroupBy(w => w.ItemId!.Value).ToDictionary(g => g.Key, g => g.First()) : [];
        var emails = new Dictionary<Guid, List<string>>();
        if (tasks.Ok && Prop(tasks.Value, "assignees") is { ValueKind: JsonValueKind.Object } a)
            foreach (var p in a.EnumerateObject()) if (Guid.TryParse(p.Name, out var id)) emails[id] = Arr(p.Value).Select(x => Str(x, "email")).Where(e => e != null).Select(e => e!).ToList();

        var raw = Arr(items.Value).Select(i => (Id: Id(i, "id"), Parent: Id(i, "parentId"), Order: (int)Num(i, "order"), Title: Str(i, "title") ?? "", Start: Date(i, "start"), Finish: Date(i, "finish"), Values: Prop(i, "values")))
            .Where(i => i.Id != null).ToList();
        var children = raw.ToLookup(i => i.Parent);
        var ids = raw.Select(i => i.Id!.Value).ToHashSet();
        var nodes = new List<PlanNode>();
        void Walk(Guid? parent, int depth)
        {
            foreach (var i in children[parent].OrderBy(x => x.Order))
            {
                var id = i.Id!.Value;
                var isSummary = children[id].Any() || (summary.TryGetValue(id, out var s) && s != null && s.StartsWith('S'));
                string? Val(Guid? col) => col is { } c && i.Values.ValueKind == JsonValueKind.Object && i.Values.TryGetProperty(c.ToString(), out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                codeByItem.TryGetValue(id, out var w);
                decimal? h = !isSummary && w != null && hours.Ok && hours.Value!.TryGetValue(w.Id, out var wh) && wh.PlannedHours > 0 ? wh.PlannedHours
                    : Val(hoursCol) is { } hs && decimal.TryParse(hs, NumberStyles.Any, CultureInfo.InvariantCulture, out var hv) && hv > 0 ? hv : null;
                var outline = summary.TryGetValue(id, out var o) ? o?.TrimStart('S') : null;
                nodes.Add(new PlanNode(id, i.Parent, i.Order, i.Title, i.Start, i.Finish, w?.Code, outline, Val(notesCol), h, isSummary, depth, emails.GetValueOrDefault(id) ?? []));
                Walk(id, depth + 1);
            }
        }
        Walk(null, 0);
        // Items whose parent isn't on the sheet (shouldn't happen) are treated as top-level.
        foreach (var orphan in raw.Where(i => i.Parent != null && !ids.Contains(i.Parent.Value)))
            if (nodes.All(n => n.Id != orphan.Id)) nodes.Add(new PlanNode(orphan.Id!.Value, null, orphan.Order, orphan.Title, orphan.Start, orphan.Finish, null, null, null, null, false, 0, []));

        var links = deps.Ok ? Arr(deps.Value).Select(d => (Id(d, "predecessorId"), Id(d, "successorId"))).Where(d => d.Item1 != null && d.Item2 != null).Select(d => (d.Item1!.Value, d.Item2!.Value)).ToList() : [];
        return WbResult<PlanSnapshot>.Success(new PlanSnapshot(project, nodes, links, !hours.Ok));
    }

    private static JsonElement Prop(JsonElement e, string n) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(n, out var v) ? v : default;
    private static IEnumerable<JsonElement> Arr(JsonElement e) => e.ValueKind == JsonValueKind.Array ? e.EnumerateArray() : [];
    private static string? Str(JsonElement e, string n) => Prop(e, n) is { ValueKind: JsonValueKind.String } v ? v.GetString() : null;
    private static Guid? Id(JsonElement e, string n) => Str(e, n) is { } s && Guid.TryParse(s, out var g) ? g : null;
    private static DateOnly? Date(JsonElement e, string n) => Str(e, n) is { } s && DateOnly.TryParse(s, CultureInfo.InvariantCulture, out var d) ? d : null;
    private static double Num(JsonElement e, string n) => Prop(e, n) is { ValueKind: JsonValueKind.Number } v && v.TryGetDouble(out var d) ? d : 0;
}
