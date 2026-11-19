using System.Text.Json;
using System.Text.Json.Nodes;
using ByteBench.Data;
using ByteBench.Services;
using ByteBench.Services.Fields;
using ByteBench.Services.Workbench;
using Microsoft.EntityFrameworkCore;

namespace ByteBench.Api;

// ----- Custom fields, components, versions, estimates & Workbench wire types -----

public partial record IssueDto
{
    /// <summary>Custom field values by field key: string, number, bool, list of strings, yyyy-MM-dd date, or username; null when unset.</summary>
    public IReadOnlyDictionary<string, object?>? CustomFields { get; init; }
    public IReadOnlyList<string>? Components { get; init; }
    public IReadOnlyList<string>? FixVersions { get; init; }
    public IReadOnlyList<string>? AffectsVersions { get; init; }
    public EstimatesDto? Estimates { get; init; }
    /// <summary>The Workbench charge code to log time to: the issue's override, else the project's WBS code (null when neither is set).</summary>
    public string? WorkbenchWbs { get; init; }
    /// <summary>True when <see cref="WorkbenchWbs"/> is the issue's own override.</summary>
    public bool? WorkbenchWbsOverride { get; init; }
}

/// <summary>Planning estimates, as text (<c>1d 2h</c>, using the project's hours per day) and in minutes. Time spent lives in Workbench.</summary>
public record EstimatesDto(string? OriginalEstimate, string? RemainingEstimate, int? OriginalEstimateMinutes, int? RemainingEstimateMinutes);

/// <summary>A project's Workbench mapping. Blank or null clears a code.</summary>
public record WorkbenchMappingRequest(string? Wbs = null, string? ProjectCode = null);

public record WorkbenchMappingDto(string? Wbs, string? ProjectCode, string? TimesheetUrl);

public partial record CreateIssueRequest
{
    /// <summary>Values by field key (see GET /projects/{key}/fields). Fields left out get their default.</summary>
    public Dictionary<string, JsonElement>? CustomFields { get; init; }
    /// <summary>Component names.</summary>
    public IReadOnlyList<string>? Components { get; init; }
    /// <summary>Version names.</summary>
    public IReadOnlyList<string>? FixVersions { get; init; }
    public IReadOnlyList<string>? AffectsVersions { get; init; }
    /// <summary>A duration such as "2h 30m", "1d" or "45m".</summary>
    public string? OriginalEstimate { get; init; }
    public string? RemainingEstimate { get; init; }
    /// <summary>Workbench charge code override for this issue (default: the project's WBS code).</summary>
    public string? WorkbenchWbs { get; init; }
}

public partial record UpdateIssueRequest
{
    /// <summary>Only the fields named change; null clears one.</summary>
    public Dictionary<string, JsonElement>? CustomFields { get; init; }
    /// <summary>Replaces the component set.</summary>
    public IReadOnlyList<string>? Components { get; init; }
    public IReadOnlyList<string>? FixVersions { get; init; }
    public IReadOnlyList<string>? AffectsVersions { get; init; }
    /// <summary>A duration such as "2h 30m"; null clears it.</summary>
    public string? OriginalEstimate { get; init; }
    public string? RemainingEstimate { get; init; }
    /// <summary>Workbench charge code override; null clears it (the project's WBS code applies).</summary>
    public string? WorkbenchWbs { get; init; }
}

public record CustomFieldDto(int Id, string Key, string Name, string? Description, string Type, bool Required, string? DefaultValue,
    IReadOnlyList<string> Options, IReadOnlyList<IssueType> IssueTypes);

public record ComponentDto(int Id, string Name, string? Description, UserDto? Lead, UserDto? DefaultAssignee, int OpenIssues, int TotalIssues);

public record VersionDto(int Id, string Name, string? Description, DateOnly? StartDate, DateOnly? ReleaseDate, bool Released,
    int OpenIssues, int DoneIssues, int Percent, string? ReleaseUrl);

public record IssueTemplateDto(string Id, string Name, IssueType Type, string? TitlePrefix, string? Body, IReadOnlyList<string> Labels, string? Source);

/// <summary>
/// /api/v1/projects/{key}/fields, /components, /versions, /issue-templates and /workbench, plus the custom field,
/// component, version, estimate and Workbench parts of the issue endpoints and webhook payloads.
/// </summary>
public static class FieldEndpoints
{
    public static void MapFieldEndpoints(this RouteGroupBuilder api)
    {
        var p = api.MapGroup("/projects/{key}").WithTags("Projects");
        p.MapGet("/fields", ListFields).WithName("ListCustomFields").WithSummary("List custom fields")
            .WithDescription("Types: text, longText, number, date, singleSelect, multiSelect, user, checkbox, url. issueTypes is empty when a field applies to every type.");
        p.MapGet("/components", ListComponents).WithName("ListComponents").WithSummary("List components");
        p.MapGet("/versions", ListVersions).WithName("ListVersions").WithSummary("List versions")
            .WithDescription("Unreleased first. Issue counts and progress are for issues with the version as fix version.");
        p.MapGet("/issue-templates", ListTemplates).WithName("ListIssueTemplates").WithSummary("List issue templates")
            .WithDescription("Project templates, then .bytebench/issue_templates/*.md from the default branch of the project's first repository.");

        p.MapGet("/workbench", GetWorkbench).WithName("GetWorkbenchMapping").WithSummary("Get the project's Workbench charge code")
            .WithDescription("Time is recorded in Workbench timesheets. timesheetUrl is null when Workbench:BaseUrl isn't configured.");
        p.MapPut("/workbench", SetWorkbench).WithName("SetWorkbenchMapping").WithSummary("Set the project's Workbench charge code (Maintainer+)")
            .WithJsonBody<WorkbenchMappingRequest>();
    }

    // ----- Issue DTOs -----

    public static async Task<IssueDto> EnrichAsync(IDbContextFactory<AppDbContext> dbFactory, IssueDto dto) =>
        (await EnrichAsync(dbFactory, [dto]))[0];

    public static async Task<List<IssueDto>> EnrichAsync(IDbContextFactory<AppDbContext> dbFactory, IReadOnlyList<IssueDto> dtos)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await EnrichAsync(db, dtos);
    }

    public static async Task<IssueDto> EnrichAsync(AppDbContext db, IssueDto dto) => (await EnrichAsync(db, [dto]))[0];

    /// <summary>Adds customFields, components, fixVersions, affectsVersions, estimates and workbenchWbs. No permission check: callers already showed the issues.</summary>
    public static async Task<List<IssueDto>> EnrichAsync(AppDbContext db, IReadOnlyList<IssueDto> dtos)
    {
        if (dtos.Count == 0) return [];
        var ids = dtos.Select(d => d.Id).Distinct().ToList();
        var info = await db.Issues.AsNoTracking().Where(i => ids.Contains(i.Id))
            .Select(i => new { i.Id, i.ProjectId, i.Type, i.OriginalEstimateMinutes, i.RemainingEstimateMinutes, i.Project.HoursPerDay, i.WorkbenchWbs, ProjectWbs = i.Project.WorkbenchWbs })
            .ToDictionaryAsync(i => i.Id);
        var projectIds = info.Values.Select(i => i.ProjectId).Distinct().ToList();
        var fields = await db.CustomFields.AsNoTracking().Where(f => projectIds.Contains(f.ProjectId)).OrderBy(f => f.Order).ThenBy(f => f.Id).ToListAsync();
        var values = await CustomFieldService.LoadValuesAsync(db, ids);
        var userIds = values.Values.SelectMany(v => v.Values).Where(v => v.UserId != null).Select(v => v.UserId!.Value).Distinct().ToList();
        var userNames = await db.Users.Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.UserName);
        var components = (await db.Issues.Where(i => ids.Contains(i.Id)).SelectMany(i => i.Components, (i, c) => new { IssueId = i.Id, c.Name }).ToListAsync())
            .ToLookup(x => x.IssueId, x => x.Name);
        var versions = (await db.IssueVersions.Where(l => ids.Contains(l.IssueId)).Select(l => new { l.IssueId, l.Kind, l.Version }).ToListAsync())
            .ToLookup(x => (x.IssueId, x.Kind), x => x.Version);

        return dtos.Select(dto =>
        {
            if (!info.TryGetValue(dto.Id, out var i)) return dto;
            var mine = values.GetValueOrDefault(dto.Id) ?? [];
            var cf = new Dictionary<string, object?>();
            foreach (var f in fields.Where(f => f.ProjectId == i.ProjectId && (f.AppliesTo(i.Type) || mine.ContainsKey(f.Id))))
                cf[f.Key] = CustomFieldRules.ToJson(f, mine.GetValueOrDefault(f.Id) ?? FieldValue.Empty, id => userNames.GetValueOrDefault(id));
            return dto with
            {
                CustomFields = cf,
                Components = components[dto.Id].Order(StringComparer.OrdinalIgnoreCase).ToList(),
                FixVersions = ComponentVersionService.Sort(versions[(dto.Id, VersionLinkKind.Fix)]).Select(v => v.Name).ToList(),
                AffectsVersions = ComponentVersionService.Sort(versions[(dto.Id, VersionLinkKind.Affects)]).Select(v => v.Name).ToList(),
                Estimates = new EstimatesDto(Duration.FormatOrNull(i.OriginalEstimateMinutes, i.HoursPerDay),
                    Duration.FormatOrNull(i.RemainingEstimateMinutes, i.HoursPerDay), i.OriginalEstimateMinutes, i.RemainingEstimateMinutes),
                WorkbenchWbs = WorkbenchService.EffectiveWbs(i.WorkbenchWbs, i.ProjectWbs),
                WorkbenchWbsOverride = i.WorkbenchWbs != null,
            };
        }).ToList();
    }

    // ----- Issue list filters: component, fixVersion, affectsVersion (name or 'none'), cf (key:value) -----

    public static async Task ApplyListFiltersAsync(HttpContext http, IDbContextFactory<AppDbContext> dbFactory, Project project, IssueFilter filter)
    {
        var q = http.Request.Query;
        await using var db = await dbFactory.CreateDbContextAsync();
        if (q["component"].ToString() is { Length: > 0 } c)
            filter.ComponentId = c.Equals("none", StringComparison.OrdinalIgnoreCase) ? 0
                : await db.ProjectComponents.Where(x => x.ProjectId == project.Id && x.Name.ToLower() == c.ToLower()).Select(x => (int?)x.Id).FirstOrDefaultAsync() ?? -1;
        async Task<int?> Version(string name) => name.Length == 0 ? null : name.Equals("none", StringComparison.OrdinalIgnoreCase) ? 0
            : await db.ProjectVersions.Where(x => x.ProjectId == project.Id && x.Name.ToLower() == name.ToLower()).Select(x => (int?)x.Id).FirstOrDefaultAsync() ?? -1;
        filter.FixVersionId = await Version(q["fixVersion"].ToString()) ?? filter.FixVersionId;
        filter.AffectsVersionId = await Version(q["affectsVersion"].ToString()) ?? filter.AffectsVersionId;
        if (q["cf"].ToString() is { Length: > 0 } cf)
            filter.Cf = IssueFilter.ParseCf(cf) != null ? cf : throw ApiException.BadRequest("cf must be key:value (key: for 'no value').");
    }

    // ----- Create & update -----

    /// <summary>Puts the request's custom fields, components, versions and estimates on the new issue (applied by IssueService).</summary>
    public static async Task ApplyCreateAsync(IDbContextFactory<AppDbContext> dbFactory, Project project, CreateIssueRequest body, Issue issue)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var draft = new IssueFieldDraft();
        if (body.CustomFields is { Count: > 0 } cf)
        {
            var fields = await db.CustomFields.AsNoTracking().Where(f => f.ProjectId == project.Id).ToDictionaryAsync(f => f.Key);
            foreach (var (key, value) in cf)
                draft.Values[(fields.GetValueOrDefault(key) ?? throw UnknownField(project, key)).Id] = value;
        }
        draft.ComponentIds = await ComponentIdsAsync(db, project, body.Components);
        draft.FixVersionIds = await VersionIdsAsync(db, project, body.FixVersions);
        draft.AffectsVersionIds = await VersionIdsAsync(db, project, body.AffectsVersions);
        issue.FieldDraft = draft;
        issue.OriginalEstimateMinutes = ParseDuration(body.OriginalEstimate, project, "originalEstimate");
        issue.RemainingEstimateMinutes = ParseDuration(body.RemainingEstimate, project, "remainingEstimate");
        issue.WorkbenchWbs = Code(body.WorkbenchWbs, "workbenchWbs");
    }

    private static string? Code(string? code, string name)
    {
        try { return WorkbenchService.CleanCode(code, "WBS code"); }
        catch (InvalidOperationException ex) { throw ApiException.Unprocessable($"{name}: {ex.Message}"); }
    }

    /// <summary>A validated PATCH of the field parts; call <see cref="FieldUpdate.ApplyAsync"/> after the main update.</summary>
    public sealed class FieldUpdate(IServiceProvider services)
    {
        internal Dictionary<int, object?>? Values;
        internal List<int>? Components, FixVersions, AffectsVersions;
        internal bool SetOriginal, SetRemaining, SetWbs;
        internal int? Original, Remaining;
        internal string? Wbs;

        public async Task ApplyAsync(int issueId, int actorId)
        {
            if (Values is { Count: > 0 }) await services.GetRequiredService<CustomFieldService>().SetValuesAsync(issueId, Values, actorId);
            var cv = services.GetRequiredService<ComponentVersionService>();
            if (Components != null) await cv.SetComponentsAsync(issueId, Components, actorId);
            if (FixVersions != null) await cv.SetVersionsAsync(issueId, VersionLinkKind.Fix, FixVersions, actorId);
            if (AffectsVersions != null) await cv.SetVersionsAsync(issueId, VersionLinkKind.Affects, AffectsVersions, actorId);
            if (SetOriginal || SetRemaining)
                await services.GetRequiredService<EstimateService>().SetEstimatesAsync(issueId, SetOriginal, Original, SetRemaining, Remaining, actorId);
            if (SetWbs) await services.GetRequiredService<WorkbenchService>().SetIssueWbsAsync(issueId, Wbs, actorId);
        }
    }

    public static async Task<FieldUpdate> PrepareUpdateAsync(IServiceProvider services, Project project, JsonObject node, UpdateIssueRequest body)
    {
        var update = new FieldUpdate(services);
        await using var db = await services.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContextAsync();
        if (body.CustomFields is { Count: > 0 } cf)
        {
            var fields = await db.CustomFields.AsNoTracking().Where(f => f.ProjectId == project.Id).ToDictionaryAsync(f => f.Key);
            update.Values = [];
            foreach (var (key, value) in cf)
            {
                var field = fields.GetValueOrDefault(key) ?? throw UnknownField(project, key);
                CustomFieldRules.Parse(field, value); // type errors now, before anything changes
                update.Values[field.Id] = value;
            }
        }
        if (node.ContainsKey("components")) update.Components = await ComponentIdsAsync(db, project, body.Components);
        if (node.ContainsKey("fixVersions")) update.FixVersions = await VersionIdsAsync(db, project, body.FixVersions);
        if (node.ContainsKey("affectsVersions")) update.AffectsVersions = await VersionIdsAsync(db, project, body.AffectsVersions);
        if (node.ContainsKey("originalEstimate")) (update.SetOriginal, update.Original) = (true, ParseDuration(body.OriginalEstimate, project, "originalEstimate"));
        if (node.ContainsKey("remainingEstimate")) (update.SetRemaining, update.Remaining) = (true, ParseDuration(body.RemainingEstimate, project, "remainingEstimate"));
        if (node.ContainsKey("workbenchWbs")) (update.SetWbs, update.Wbs) = (true, Code(body.WorkbenchWbs, "workbenchWbs"));
        return update;
    }

    private static ApiException UnknownField(Project project, string key) =>
        ApiException.Unprocessable($"Unknown custom field '{key}'. See GET /api/v1/projects/{project.Key}/fields.");

    private static async Task<List<int>> ComponentIdsAsync(AppDbContext db, Project project, IReadOnlyList<string>? names)
    {
        var result = new List<int>();
        foreach (var name in (names ?? []).Distinct(StringComparer.OrdinalIgnoreCase))
            result.Add(await db.ProjectComponents.Where(c => c.ProjectId == project.Id && c.Name.ToLower() == name.ToLower()).Select(c => (int?)c.Id).FirstOrDefaultAsync()
                       ?? throw ApiException.Unprocessable($"Unknown component '{name}'."));
        return result;
    }

    private static async Task<List<int>> VersionIdsAsync(AppDbContext db, Project project, IReadOnlyList<string>? names)
    {
        var result = new List<int>();
        foreach (var name in (names ?? []).Distinct(StringComparer.OrdinalIgnoreCase))
            result.Add(await db.ProjectVersions.Where(v => v.ProjectId == project.Id && v.Name.ToLower() == name.ToLower()).Select(v => (int?)v.Id).FirstOrDefaultAsync()
                       ?? throw ApiException.Unprocessable($"Unknown version '{name}'."));
        return result;
    }

    private static int? ParseDuration(string? text, Project project, string name)
    {
        try { return Duration.Parse(text, project.HoursPerDay); }
        catch (FormatException ex) { throw ApiException.Unprocessable($"{name}: {ex.Message}"); }
    }

    // ----- Project lists -----

    private static async Task<List<CustomFieldDto>> ListFields(HttpContext http, IssueService issues, CustomFieldService fields, string key)
    {
        var paging = Paging.From(http.Request);
        var project = await IssueEndpoints.RequireProjectAsync(issues, key);
        return paging.Apply(http, await fields.GetFieldsAsync(project.Id), f => new CustomFieldDto(f.Id, f.Key, f.Name, f.Description,
            JsonNamingPolicy.CamelCase.ConvertName(f.Type.ToString()), f.Required, f.DefaultValue, f.Options, f.IssueTypes));
    }

    private static async Task<List<ComponentDto>> ListComponents(HttpContext http, IssueService issues, ComponentVersionService cv, string key)
    {
        var paging = Paging.From(http.Request);
        var project = await IssueEndpoints.RequireProjectAsync(issues, key);
        return paging.Apply(http, await cv.GetComponentsAsync(project.Id), c => new ComponentDto(c.Component.Id, c.Component.Name, c.Component.Description,
            Dto.UserOrNull(c.Component.Lead), Dto.UserOrNull(c.Component.DefaultAssignee), c.OpenIssues, c.TotalIssues));
    }

    private static async Task<List<VersionDto>> ListVersions(HttpContext http, IssueService issues, ComponentVersionService cv, PublicBaseUrl url, string key)
    {
        var paging = Paging.From(http.Request);
        var project = await IssueEndpoints.RequireProjectAsync(issues, key);
        var baseUrl = url.For(http.Request);
        return paging.Apply(http, await cv.GetVersionsAsync(project.Id), v => new VersionDto(v.Version.Id, v.Version.Name, v.Version.Description,
            Dto.Date(v.Version.StartDate), Dto.Date(v.Version.ReleaseDate), v.Version.IsReleased, v.Open, v.Done, v.Percent,
            v.Release == null ? null : baseUrl + v.Release.Url));
    }

    private static async Task<List<IssueTemplateDto>> ListTemplates(HttpContext http, IssueService issues, IssueTemplateService templates, string key)
    {
        var paging = Paging.From(http.Request);
        var project = await IssueEndpoints.RequireProjectAsync(issues, key);
        return paging.Apply(http, await templates.GetChoicesAsync(project), t => new IssueTemplateDto(t.Id, t.Name, t.Type, t.TitlePrefix, t.Body,
            project.Labels.Where(l => t.LabelIds.Contains(l.Id)).Select(l => l.Name).ToList(), t.Source));
    }

    // ----- Workbench -----

    private static async Task<WorkbenchMappingDto> GetWorkbench(IssueService issues, WorkbenchService workbench, string key)
    {
        var project = await IssueEndpoints.RequireProjectAsync(issues, key);
        return new WorkbenchMappingDto(project.WorkbenchWbs, project.WorkbenchProjectCode, workbench.TimesheetUrl());
    }

    private static async Task<WorkbenchMappingDto> SetWorkbench(HttpContext http, IssueService issues, WorkbenchService workbench, ApiCaller caller, string key)
    {
        var project = await IssueEndpoints.RequireProjectAsync(issues, key);
        var body = await ApiResults.ReadBodyAsync<WorkbenchMappingRequest>(http.Request);
        await workbench.SetProjectMappingAsync(project.Id, body.Wbs, body.ProjectCode, caller.Id); // 403 / 422 from the API filter
        project = await IssueEndpoints.RequireProjectAsync(issues, key);
        return new WorkbenchMappingDto(project.WorkbenchWbs, project.WorkbenchProjectCode, workbench.TimesheetUrl());
    }
}
