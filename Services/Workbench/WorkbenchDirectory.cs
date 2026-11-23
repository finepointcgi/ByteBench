using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;

namespace ByteBench.Services.Workbench;

/// <summary>A Workbench project (<c>GET /api/v1/projects</c>): <see cref="Code"/> is the project code, <see cref="SheetId"/> its plan sheet.</summary>
public sealed record WbProject(Guid Id, string Code, string Name, string? Status, DateOnly? Start, DateOnly? Finish, Guid? SheetId);

/// <summary>A WBS element (<c>GET /api/v1/projects/{id}/wbs</c>); <see cref="ItemId"/> is its plan task.</summary>
public sealed record WbWbs(Guid Id, Guid ProjectId, Guid? ParentId, string Code, string Name, bool IsOpen, bool IsChargeable, Guid? ItemId);

public sealed record WbPlanItem(Guid Id, string Title, DateOnly? Start, DateOnly? Finish, string? Status, decimal? Percent);

/// <summary>A plan sheet's tasks plus what ByteBench needs to write work state: the Status options and whether a % Complete column exists.</summary>
public sealed record WbPlan(Guid SheetId, IReadOnlyDictionary<Guid, WbPlanItem> Items, IReadOnlyList<string> StatusOptions, bool HasStatusColumn, bool HasPercentColumn);

public sealed record WbHours(decimal PlannedHours, decimal ActualHours, decimal EstimateAtCompletionHours);

/// <summary>
/// Cached, read-only views of Workbench data: projects, a project's WBS elements and plan, and per-WBS hours. Results (failures
/// too, for a shorter time) are cached for <c>Workbench:CacheSeconds</c> and concurrent callers share one request, so a slow or
/// down Workbench costs one timeout per window, not one per page view.
/// </summary>
public sealed class WorkbenchDirectory(WorkbenchClient client, Microsoft.Extensions.Options.IOptionsMonitor<WorkbenchOptions> options)
{
    private sealed record Entry(Task Task, DateTime Created, TimeSpan Ttl);
    private readonly ConcurrentDictionary<string, Entry> cache = new();

    private TimeSpan Ttl => TimeSpan.FromSeconds(Math.Max(1, options.CurrentValue.CacheSeconds));

    public void Clear() => cache.Clear();

    private Task<WbResult<T>> Cached<T>(string key, Func<Task<WbResult<T>>> load, bool fresh = false)
    {
        var now = DateTime.UtcNow;
        if (!fresh && cache.TryGetValue(key, out var e) && now - e.Created < e.Ttl) return (Task<WbResult<T>>)e.Task;
        var task = Task.Run(async () =>
        {
            var r = await load();
            // A failure is remembered briefly so a down Workbench isn't hammered; a success for the full TTL.
            cache[key] = new Entry(Task.FromResult(r), DateTime.UtcNow, r.Ok ? Ttl : TimeSpan.FromSeconds(Math.Min(15, Ttl.TotalSeconds)));
            return r;
        });
        cache[key] = new Entry(task, now, Ttl);
        return task;
    }

    public Task<WbResult<IReadOnlyList<WbProject>>> ListProjectsAsync(bool fresh = false) => Cached("projects", async () =>
    {
        var r = await client.GetAsync("/projects");
        if (!r.Ok) return r.As<IReadOnlyList<WbProject>>();
        return WbResult<IReadOnlyList<WbProject>>.Success(Array(r.Value).Select(ParseProject).Where(p => p != null).Select(p => p!).OrderBy(p => p.Code, StringComparer.OrdinalIgnoreCase).ToList());
    }, fresh);

    public Task<WbResult<IReadOnlyList<WbWbs>>> ListWbsAsync(Guid projectId, bool fresh = false) => Cached($"wbs:{projectId}", async () =>
    {
        var r = await client.GetAsync($"/projects/{projectId}/wbs");
        if (!r.Ok) return r.As<IReadOnlyList<WbWbs>>();
        return WbResult<IReadOnlyList<WbWbs>>.Success(Array(r.Value).Select(ParseWbs).Where(w => w != null).Select(w => w!).OrderBy(w => w.Code, StringComparer.OrdinalIgnoreCase).ToList());
    }, fresh);

    /// <summary>The plan sheet's tasks (title, dates, status, % complete) and its Status options.</summary>
    public Task<WbResult<WbPlan>> GetPlanAsync(Guid sheetId, bool fresh = false) => Cached($"plan:{sheetId}", async () =>
    {
        var tasks = await client.GetAsync($"/sheets/{sheetId}/tasks");
        if (!tasks.Ok) return tasks.As<WbPlan>();
        var items = await client.GetAsync($"/sheets/{sheetId}/items");
        if (!items.Ok) return items.As<WbPlan>();
        var (statusId, options, percentId) = (Prop(tasks.Value, "statusColumn") is { ValueKind: JsonValueKind.Object } s ? (Guid?)GuidOf(s, "id") : null,
            (IReadOnlyList<string>)(Prop(tasks.Value, "statusColumn") is { ValueKind: JsonValueKind.Object } so ? Array(Prop(so, "options")).Select(o => o.GetString() ?? "").Where(o => o.Length > 0).ToList() : []),
            Prop(tasks.Value, "percentColumn") is { ValueKind: JsonValueKind.Object } p ? (Guid?)GuidOf(p, "id") : null);
        var map = new Dictionary<Guid, WbPlanItem>();
        foreach (var it in Array(items.Value))
        {
            if (GuidOf(it, "id") is not { } id) continue;
            var values = Prop(it, "values");
            string? Val(Guid? col) => col is { } c && values.ValueKind == JsonValueKind.Object && values.TryGetProperty(c.ToString(), out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            var pct = Val(percentId) is { } ps && decimal.TryParse(ps.TrimEnd('%'), NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : (decimal?)null;
            map[id] = new WbPlanItem(id, Str(it, "title") ?? "", Date(it, "start"), Date(it, "finish"), Val(statusId), pct);
        }
        return WbResult<WbPlan>.Success(new WbPlan(sheetId, map, options, statusId != null, percentId != null));
    }, fresh);

    /// <summary>Planned and charged hours per WBS element (needs the key's roles to allow project financials; otherwise Forbidden).</summary>
    public Task<WbResult<IReadOnlyDictionary<Guid, WbHours>>> GetHoursAsync(Guid projectId, bool fresh = false) => Cached($"hours:{projectId}", async () =>
    {
        var r = await client.GetAsync($"/projects/{projectId}/financials/wbs");
        if (!r.Ok) return r.As<IReadOnlyDictionary<Guid, WbHours>>();
        var map = new Dictionary<Guid, WbHours>();
        foreach (var row in Array(r.Value))
        {
            if (GuidOf(row, "wbsElementId") is not { } id) continue;
            var f = Prop(row, "figures");
            map[id] = new WbHours(Num(f, "budgetHours"), Num(f, "actualHours"), Num(f, "eacHours"));
        }
        return WbResult<IReadOnlyDictionary<Guid, WbHours>>.Success(map);
    }, fresh);

    // ---------- Lookups ----------

    /// <summary>Projects whose code or name contains <paramref name="query"/>, best first (code prefix), at most <paramref name="max"/>.</summary>
    public static IReadOnlyList<WbProject> SearchProjects(IEnumerable<WbProject> all, string? query, int max = 25)
    {
        var q = (query ?? "").Trim();
        return all.Where(p => q.Length == 0 || p.Code.Contains(q, StringComparison.OrdinalIgnoreCase) || p.Name.Contains(q, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(p => p.Code.StartsWith(q, StringComparison.OrdinalIgnoreCase)).ThenBy(p => p.Code, StringComparer.OrdinalIgnoreCase).Take(max).ToList();
    }

    public static IReadOnlyList<WbWbs> SearchWbs(IEnumerable<WbWbs> all, string? query, int max = 40)
    {
        var q = (query ?? "").Trim();
        return all.Where(w => q.Length == 0 || w.Code.Contains(q, StringComparison.OrdinalIgnoreCase) || w.Name.Contains(q, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(w => w.Code.StartsWith(q, StringComparison.OrdinalIgnoreCase)).ThenBy(w => w.Code, StringComparer.OrdinalIgnoreCase).Take(max).ToList();
    }

    public async Task<WbResult<WbProject>> FindProjectAsync(string code)
    {
        var all = await ListProjectsAsync();
        if (!all.Ok) return all.As<WbProject>();
        return all.Value!.FirstOrDefault(p => p.Code.Equals(code, StringComparison.OrdinalIgnoreCase)) is { } p
            ? WbResult<WbProject>.Success(p) : WbResult<WbProject>.Fail(WbStatus.NotFound, $"Workbench has no project with code {code}.");
    }

    public async Task<WbResult<WbWbs>> FindWbsAsync(WbProject project, string wbsCode, bool fresh = false)
    {
        var all = await ListWbsAsync(project.Id, fresh);
        if (!all.Ok) return all.As<WbWbs>();
        return all.Value!.FirstOrDefault(w => w.Code.Equals(wbsCode, StringComparison.OrdinalIgnoreCase)) is { } w
            ? WbResult<WbWbs>.Success(w) : WbResult<WbWbs>.Fail(WbStatus.NotFound, $"Workbench project {project.Code} has no WBS element {wbsCode}.");
    }

    // ---------- Tolerant JSON helpers (camelCase, enums as numbers or names) ----------

    private static JsonElement Prop(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) ? v : default;

    private static IEnumerable<JsonElement> Array(JsonElement e) => e.ValueKind == JsonValueKind.Array ? e.EnumerateArray() : [];

    private static string? Str(JsonElement e, string name) => Prop(e, name) is { ValueKind: JsonValueKind.String } v ? v.GetString() : null;

    private static Guid? GuidOf(JsonElement e, string name) => Str(e, name) is { } s && System.Guid.TryParse(s, out var g) ? g : null;

    private static DateOnly? Date(JsonElement e, string name) => Str(e, name) is { } s && DateOnly.TryParse(s, CultureInfo.InvariantCulture, out var d) ? d : null;

    private static decimal Num(JsonElement e, string name) => Prop(e, name) is { ValueKind: JsonValueKind.Number } v && v.TryGetDecimal(out var d) ? d : 0;

    private static readonly string[] ProjectStates = ["Planning", "Active", "On hold", "Closed"];

    private static WbProject? ParseProject(JsonElement e)
    {
        if (GuidOf(e, "id") is not { } id || Str(e, "code") is not { } code) return null;
        var st = Prop(e, "status");
        var status = st.ValueKind == JsonValueKind.Number && st.TryGetInt32(out var n) && n >= 0 && n < ProjectStates.Length ? ProjectStates[n] : st.ValueKind == JsonValueKind.String ? st.GetString() : null;
        return new WbProject(id, code, Str(e, "name") ?? code, status, Date(e, "start"), Date(e, "finish"), GuidOf(e, "sheetId"));
    }

    private static WbWbs? ParseWbs(JsonElement e)
    {
        if (GuidOf(e, "id") is not { } id || Str(e, "code") is not { } code) return null;
        return new WbWbs(id, GuidOf(e, "projectId") ?? System.Guid.Empty, GuidOf(e, "parentId"), code, Str(e, "name") ?? code,
            Prop(e, "isOpen").ValueKind != JsonValueKind.False, Prop(e, "isChargeable").ValueKind != JsonValueKind.False, GuidOf(e, "itemId"));
    }
}
