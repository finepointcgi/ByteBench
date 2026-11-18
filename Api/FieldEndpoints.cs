using System.Text.Json;
using System.Text.Json.Nodes;
using GitBench.Data;
using GitBench.Services;
using GitBench.Services.Fields;
using Microsoft.EntityFrameworkCore;

namespace GitBench.Api;

// ----- Custom fields, components, versions & time tracking wire types -----

public partial record IssueDto
{
    /// <summary>Custom field values by field key: string, number, bool, list of strings, yyyy-MM-dd date, or username; null when unset.</summary>
    public IReadOnlyDictionary<string, object?>? CustomFields { get; init; }
    public IReadOnlyList<string>? Components { get; init; }
    public IReadOnlyList<string>? FixVersions { get; init; }
    public IReadOnlyList<string>? AffectsVersions { get; init; }
    public TimeTrackingDto? TimeTracking { get; init; }
}

/// <summary>Estimates and time spent, as text (<c>1d 2h</c>, using the project's hours per day) and in minutes.</summary>
public record TimeTrackingDto(string? OriginalEstimate, string? RemainingEstimate, string TimeSpent,
    int? OriginalEstimateMinutes, int? RemainingEstimateMinutes, int TimeSpentMinutes);

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
}

public record CustomFieldDto(int Id, string Key, string Name, string? Description, string Type, bool Required, string? DefaultValue,
    IReadOnlyList<string> Options, IReadOnlyList<IssueType> IssueTypes);

public record ComponentDto(int Id, string Name, string? Description, UserDto? Lead, UserDto? DefaultAssignee, int OpenIssues, int TotalIssues);

public record VersionDto(int Id, string Name, string? Description, DateOnly? StartDate, DateOnly? ReleaseDate, bool Released,
    int OpenIssues, int DoneIssues, int Percent, string TimeSpent, string? ReleaseUrl);

public record IssueTemplateDto(string Id, string Name, IssueType Type, string? TitlePrefix, string? Body, IReadOnlyList<string> Labels, string? Source);

public record WorkLogDto(int Id, UserDto User, string TimeSpent, int TimeSpentMinutes, DateOnly Date, string? Comment,
    DateTimeOffset CreatedAt, DateTimeOffset? UpdatedAt);

/// <summary><c>timeSpent</c> is a duration such as "1h 30m" (or send <c>timeSpentMinutes</c>); <c>date</c> defaults to today.</summary>
public record WorkLogRequest(string? TimeSpent = null, int? TimeSpentMinutes = null, DateOnly? Date = null, string? Comment = null);

/// <summary>
/// /api/v1/projects/{key}/fields, /components, /versions, /issue-templates and /issues/{number}/worklogs, plus the
/// custom field, component, version and estimate parts of the issue endpoints and webhook payloads.
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
            .WithDescription("Project templates, then .gitbench/issue_templates/*.md from the default branch of the project's first repository.");

        var w = api.MapGroup("/projects/{key}/issues/{number:int}/worklogs").WithTags("Issues");
        w.MapGet("", ListWorkLogs).WithName("ListWorkLogs").WithSummary("List an issue's work logs");
        w.MapPost("", AddWorkLog).WithName("AddWorkLog").WithSummary("Log work on an issue")
            .WithDescription("The remaining estimate goes down by the time spent.")
            .WithJsonBody<WorkLogRequest>().Produces<WorkLogDto>(StatusCodes.Status201Created);
        w.MapPatch("/{id:int}", UpdateWorkLog).WithName("UpdateWorkLog").WithSummary("Edit a work log (its author only)").WithJsonBody<WorkLogRequest>();
        w.MapDelete("/{id:int}", DeleteWorkLog).WithName("DeleteWorkLog").WithSummary("Delete a work log (its author or a Maintainer)")
            .Produces(StatusCodes.Status204NoContent);
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

    /// <summary>Adds customFields, components, fixVersions, affectsVersions and timeTracking. No permission check: callers already showed the issues.</summary>
    public static async Task<List<IssueDto>> EnrichAsync(AppDbContext db, IReadOnlyList<IssueDto> dtos)
    {
        if (dtos.Count == 0) return [];
        var ids = dtos.Select(d => d.Id).Distinct().ToList();
        var info = await db.Issues.AsNoTracking().Where(i => ids.Contains(i.Id))
            .Select(i => new { i.Id, i.ProjectId, i.Type, i.OriginalEstimateMinutes, i.RemainingEstimateMinutes, i.Project.HoursPerDay })
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
        var spent = await db.WorkLogs.Where(w => ids.Contains(w.IssueId)).GroupBy(w => w.IssueId)
            .Select(g => new { g.Key, Minutes = g.Sum(w => w.Minutes) }).ToDictionaryAsync(x => x.Key, x => x.Minutes);

        return dtos.Select(dto =>
        {
            if (!info.TryGetValue(dto.Id, out var i)) return dto;
            var mine = values.GetValueOrDefault(dto.Id) ?? [];
            var cf = new Dictionary<string, object?>();
            foreach (var f in fields.Where(f => f.ProjectId == i.ProjectId && (f.AppliesTo(i.Type) || mine.ContainsKey(f.Id))))
                cf[f.Key] = CustomFieldRules.ToJson(f, mine.GetValueOrDefault(f.Id) ?? FieldValue.Empty, id => userNames.GetValueOrDefault(id));
            var s = spent.GetValueOrDefault(dto.Id);
            return dto with
            {
                CustomFields = cf,
                Components = components[dto.Id].Order(StringComparer.OrdinalIgnoreCase).ToList(),
                FixVersions = ComponentVersionService.Sort(versions[(dto.Id, VersionLinkKind.Fix)]).Select(v => v.Name).ToList(),
                AffectsVersions = ComponentVersionService.Sort(versions[(dto.Id, VersionLinkKind.Affects)]).Select(v => v.Name).ToList(),
                TimeTracking = new TimeTrackingDto(Duration.FormatOrNull(i.OriginalEstimateMinutes, i.HoursPerDay),
                    Duration.FormatOrNull(i.RemainingEstimateMinutes, i.HoursPerDay), Duration.Format(s, i.HoursPerDay),
                    i.OriginalEstimateMinutes, i.RemainingEstimateMinutes, s),
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
    }

    /// <summary>A validated PATCH of the field parts; call <see cref="FieldUpdate.ApplyAsync"/> after the main update.</summary>
    public sealed class FieldUpdate(IServiceProvider services)
    {
        internal Dictionary<int, object?>? Values;
        internal List<int>? Components, FixVersions, AffectsVersions;
        internal bool SetOriginal, SetRemaining;
        internal int? Original, Remaining;

        public async Task ApplyAsync(int issueId, int actorId)
        {
            if (Values is { Count: > 0 }) await services.GetRequiredService<CustomFieldService>().SetValuesAsync(issueId, Values, actorId);
            var cv = services.GetRequiredService<ComponentVersionService>();
            if (Components != null) await cv.SetComponentsAsync(issueId, Components, actorId);
            if (FixVersions != null) await cv.SetVersionsAsync(issueId, VersionLinkKind.Fix, FixVersions, actorId);
            if (AffectsVersions != null) await cv.SetVersionsAsync(issueId, VersionLinkKind.Affects, AffectsVersions, actorId);
            if (SetOriginal || SetRemaining)
                await services.GetRequiredService<TimeTrackingService>().SetEstimatesAsync(issueId, SetOriginal, Original, SetRemaining, Remaining, actorId);
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
            Duration.Format(v.TimeSpentMinutes, project.HoursPerDay), v.Release == null ? null : baseUrl + v.Release.Url));
    }

    private static async Task<List<IssueTemplateDto>> ListTemplates(HttpContext http, IssueService issues, IssueTemplateService templates, string key)
    {
        var paging = Paging.From(http.Request);
        var project = await IssueEndpoints.RequireProjectAsync(issues, key);
        return paging.Apply(http, await templates.GetChoicesAsync(project), t => new IssueTemplateDto(t.Id, t.Name, t.Type, t.TitlePrefix, t.Body,
            project.Labels.Where(l => t.LabelIds.Contains(l.Id)).Select(l => l.Name).ToList(), t.Source));
    }

    // ----- Work logs -----

    private static async Task<(Project Project, Issue Issue)> RequireIssueAsync(IssueService issues, string key, int number)
    {
        var project = await IssueEndpoints.RequireProjectAsync(issues, key);
        var issue = await issues.GetIssueAsync(key, number) ?? throw ApiException.NotFound($"Issue {key.ToUpperInvariant()}-{number} not found.");
        return (project, issue);
    }

    private static WorkLogDto ToDto(WorkLog w, int hoursPerDay) => new(w.Id, Dto.User(w.User), Duration.Format(w.Minutes, hoursPerDay), w.Minutes,
        DateOnly.FromDateTime(w.Date), w.Comment, Dto.Utc(w.CreatedAt), Dto.Utc(w.UpdatedAt));

    private static async Task<List<WorkLogDto>> ListWorkLogs(HttpContext http, IssueService issues, TimeTrackingService time, string key, int number)
    {
        var paging = Paging.From(http.Request);
        var (project, issue) = await RequireIssueAsync(issues, key, number);
        return paging.Apply(http, await time.GetWorkLogsAsync(issue.Id), w => ToDto(w, project.HoursPerDay));
    }

    private static int Minutes(WorkLogRequest body, Project project)
    {
        if (body.TimeSpentMinutes is { } m) return m;
        return ParseDuration(body.TimeSpent, project, "timeSpent") ?? throw ApiException.Unprocessable("timeSpent (e.g. \"1h 30m\") or timeSpentMinutes is required.");
    }

    private static async Task<IResult> AddWorkLog(HttpContext http, IssueService issues, TimeTrackingService time, ApiCaller caller, PublicBaseUrl url, string key, int number)
    {
        var (project, issue) = await RequireIssueAsync(issues, key, number);
        var body = await ApiResults.ReadBodyAsync<WorkLogRequest>(http.Request);
        var log = await time.AddWorkLogAsync(issue.Id, Minutes(body, project), body.Date?.ToDateTime(TimeOnly.MinValue) ?? DateTime.UtcNow.Date, body.Comment, caller.Id);
        log.User = caller.User;
        return TypedResults.Created($"{url.For(http.Request)}/api/v1/projects/{project.Key}/issues/{number}/worklogs/{log.Id}", ToDto(log, project.HoursPerDay));
    }

    private static async Task<WorkLog> RequireLogAsync(TimeTrackingService time, Issue issue, int id) =>
        await time.GetWorkLogAsync(id) is { } log && log.IssueId == issue.Id ? log : throw ApiException.NotFound($"Work log {id} not found.");

    private static async Task<WorkLogDto> UpdateWorkLog(HttpContext http, IssueService issues, TimeTrackingService time, ApiCaller caller, string key, int number, int id)
    {
        var (project, issue) = await RequireIssueAsync(issues, key, number);
        var log = await RequireLogAsync(time, issue, id);
        var node = await ApiResults.ReadBodyAsync<JsonObject>(http.Request);
        WorkLogRequest body;
        try { body = node.Deserialize<WorkLogRequest>(Dto.Json) ?? new(); }
        catch (JsonException ex) { throw ApiException.BadRequest(ApiResults.InvalidJson(ex)); }
        var minutes = node.ContainsKey("timeSpent") || node.ContainsKey("timeSpentMinutes") ? Minutes(body, project) : log.Minutes;
        var date = node.ContainsKey("date") ? body.Date?.ToDateTime(TimeOnly.MinValue) ?? throw ApiException.Unprocessable("date can't be null.") : log.Date;
        var comment = node.ContainsKey("comment") ? body.Comment : log.Comment;
        await time.UpdateWorkLogAsync(id, minutes, date, comment, caller.Id);
        return ToDto((await time.GetWorkLogAsync(id))!, project.HoursPerDay);
    }

    private static async Task<IResult> DeleteWorkLog(IssueService issues, TimeTrackingService time, ApiCaller caller, string key, int number, int id)
    {
        var (_, issue) = await RequireIssueAsync(issues, key, number);
        await RequireLogAsync(time, issue, id);
        await time.DeleteWorkLogAsync(id, caller.Id);
        return TypedResults.NoContent();
    }
}
