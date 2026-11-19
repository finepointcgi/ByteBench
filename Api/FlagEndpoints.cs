using System.Text.Json;
using System.Text.Json.Nodes;
using ByteBench.Data;
using ByteBench.Services;
using ByteBench.Services.Flags;

namespace ByteBench.Api;

public record FlagEnvironmentDto(int Id, string Name);

public record FlagRuleDto(string Id, string? Description, IReadOnlyList<FlagClauseDto> Clauses, double? Percentage, string BucketBy, JsonNode? Value);

public record FlagClauseDto(string? Attribute, string Op, IReadOnlyList<string> Values);

public record FlagStateDto(int EnvironmentId, string Environment, bool Enabled, JsonNode? DefaultValue, IReadOnlyList<FlagRuleDto> Rules,
    DateTimeOffset UpdatedAt, DateTimeOffset? LastEvaluatedAt);

public record FlagDto(int Id, string Key, string Name, string? Description, string Type, JsonNode? OffValue, IReadOnlyList<int> IssueNumbers,
    bool Archived, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, UserDto? CreatedBy, IReadOnlyList<FlagStateDto> Environments, string WebUrl);

public record FlagChangeDto(int Id, string Kind, string? Environment, UserRefDto? Actor, DateTimeOffset At, string? OldValue, string? NewValue);

public record UserRefDto(int? Id, string? UserName);

public record StaleFlagDto(string Key, string Name, DateTimeOffset LastChanged, DateTimeOffset? LastEvaluated, int IdleDays);

public record FlagSegmentDto(string Key, string Name, string? Description, IReadOnlyList<string> Members, DateTimeOffset UpdatedAt);

public record FlagSdkKeyDto(int Id, string Name, string Kind, string Environment, int EnvironmentId, string Prefix, DateTimeOffset CreatedAt,
    DateTimeOffset? RotatedAt, DateTimeOffset? LastUsedAt, DateTimeOffset? RevokedAt);

/// <summary>A new or rotated key: <c>key</c> is the secret, shown only in this response.</summary>
public record FlagSdkKeyCreatedDto(FlagSdkKeyDto SdkKey, string Key);

/// <summary>POST body for a flag. <c>type</c>: boolean, string, number or json. <c>defaultValue</c> is what environments serve when on.</summary>
public record CreateFlagRequest(string Key, string Name, string? Description = null, string? Type = null, JsonNode? DefaultValue = null, JsonNode? OffValue = null);

/// <summary>PATCH body: only the given properties change. <c>issueNumbers</c> replaces the linked issues.</summary>
public record UpdateFlagRequest(string? Name = null, string? Description = null, IReadOnlyList<int>? IssueNumbers = null, JsonNode? OffValue = null);

public record FlagClauseRequest(string? Attribute, string Op, IReadOnlyList<string>? Values = null);

public record FlagRuleRequest(string? Id, string? Description, IReadOnlyList<FlagClauseRequest>? Clauses, double? Percentage, string? BucketBy, JsonNode? Value);

/// <summary>PUT body for a flag's state in one environment: any of the three may be given.</summary>
public record FlagStateRequest(bool? Enabled = null, JsonNode? DefaultValue = null, IReadOnlyList<FlagRuleRequest>? Rules = null);

public record CreateFlagEnvironmentRequest(string Name);

public record SaveSegmentRequest(string? Name = null, string? Description = null, IReadOnlyList<string>? Members = null);

public record CreateSdkKeyRequest(int EnvironmentId, string Name, string? Kind = null);

public static class FlagDtos
{
    public static FlagRuleDto Rule(FlagRule r) => new(r.Id, r.Description,
        r.Clauses.Select(c => new FlagClauseDto(c.Attribute, c.Op, c.Values)).ToList(), r.Percentage, r.BucketBy, r.Value?.DeepClone());

    public static FlagDto Flag(FeatureFlag f, string projectKey, string baseUrl) => new(f.Id, f.Key, f.Name, f.Description, f.Type.ToString().ToLowerInvariant(),
        FlagEvaluator.ParseValue(f.OffValue), f.LinkedIssues, f.IsArchived, Dto.Utc(f.CreatedAt), Dto.Utc(f.UpdatedAt), Dto.UserOrNull(f.CreatedBy),
        f.States.OrderBy(s => s.Environment.Name, StringComparer.OrdinalIgnoreCase).Select(s => new FlagStateDto(s.EnvironmentId, s.Environment.Name, s.Enabled,
            FlagEvaluator.ParseValue(s.DefaultValue), FlagEvaluator.ParseRules(s.RulesJson).Select(Rule).ToList(), Dto.Utc(s.UpdatedAt), Dto.Utc(s.LastEvaluatedAt))).ToList(),
        baseUrl + FlagUrls.Detail(projectKey, f.Key));

    public static FlagSdkKeyDto Key(FlagSdkKey k) => new(k.Id, k.Name, k.Kind.ToString().ToLowerInvariant(), k.Environment?.Name ?? "", k.EnvironmentId, k.Prefix,
        Dto.Utc(k.CreatedAt), Dto.Utc(k.RotatedAt), Dto.Utc(k.LastUsedAt), Dto.Utc(k.RevokedAt));

    public static List<FlagRule> ToRules(IEnumerable<FlagRuleRequest>? rules) => (rules ?? []).Select(r => new FlagRule
    {
        Id = r.Id ?? "", Description = r.Description, Percentage = r.Percentage, BucketBy = r.BucketBy ?? "userId", Value = r.Value?.DeepClone(),
        Clauses = (r.Clauses ?? []).Select(c => new FlagClause { Attribute = c.Attribute, Op = c.Op, Values = c.Values?.ToList() ?? [] }).ToList(),
    }).ToList();

    public static bool TryParseType(string? s, out FlagType type)
    {
        type = FlagType.Boolean;
        if (string.IsNullOrWhiteSpace(s)) return true;
        return Enum.TryParse(s.Trim(), ignoreCase: true, out type) && Enum.IsDefined(type);
    }
}

/// <summary>
/// Feature flags management: <c>/projects/{key}/flags</c> and friends. Reading needs project access; changing a flag's state needs
/// Developer+; creating, deleting, archiving, environments and SDK keys need Maintainer+. Clients evaluate flags with an SDK key
/// at <c>/api/v1/flags/...</c> instead (see <see cref="FlagSdkEndpoints"/>).
/// </summary>
public static class FlagEndpoints
{
    public static void MapFlagEndpoints(this RouteGroupBuilder api)
    {
        var p = api.MapGroup("/projects/{key}").WithTags("Feature flags");
        p.MapGet("/flags", ListFlags).WithName("ListFlags").WithSummary("List feature flags with their per-environment state")
            .WithDescription("?archived=true includes archived flags.");
        p.MapPost("/flags", CreateFlag).WithName("CreateFlag").WithSummary("Create a flag (Maintainer+)").WithJsonBody<CreateFlagRequest>()
            .Produces<FlagDto>(StatusCodes.Status201Created);
        p.MapGet("/flags/_stale", StaleFlags).WithName("ListStaleFlags").WithSummary("Flags not changed or evaluated for ?days= (default 30)");
        p.MapGet("/flags/{flagKey}", GetFlag).WithName("GetFlag").WithSummary("Get a flag");
        p.MapPatch("/flags/{flagKey}", UpdateFlag).WithName("UpdateFlag").WithSummary("Change name, description, linked issues or the off value (Developer+)")
            .WithJsonBody<UpdateFlagRequest>();
        p.MapDelete("/flags/{flagKey}", DeleteFlag).WithName("DeleteFlag").WithSummary("Delete a flag and its history (Maintainer+)");
        p.MapPost("/flags/{flagKey}/archive", (HttpContext h, FlagService f, IssueService i, PublicBaseUrl u, string key, string flagKey) => Archive(h, f, i, u, key, flagKey, true))
            .WithName("ArchiveFlag").WithSummary("Archive a flag (Maintainer+)");
        p.MapPost("/flags/{flagKey}/unarchive", (HttpContext h, FlagService f, IssueService i, PublicBaseUrl u, string key, string flagKey) => Archive(h, f, i, u, key, flagKey, false))
            .WithName("UnarchiveFlag").WithSummary("Restore an archived flag (Maintainer+)");
        p.MapPut("/flags/{flagKey}/environments/{environmentId:int}", SetState).WithName("SetFlagState")
            .WithSummary("Turn a flag on or off in an environment, and set its default value and targeting rules (Developer+)")
            .WithDescription("Rules are evaluated in order; the first match wins. A rule matches when all its clauses match and, with a percentage, the context's bucket falls inside it.")
            .WithJsonBody<FlagStateRequest>();
        p.MapGet("/flags/{flagKey}/history", History).WithName("GetFlagHistory").WithSummary("Who changed the flag, when, from what to what");

        p.MapGet("/flag-environments", ListEnvironments).WithName("ListFlagEnvironments").WithSummary("Environments flags have a state in");
        p.MapPost("/flag-environments", CreateEnvironment).WithName("CreateFlagEnvironment").WithSummary("Add an environment (Maintainer+)")
            .WithJsonBody<CreateFlagEnvironmentRequest>().Produces<FlagEnvironmentDto>(StatusCodes.Status201Created);
        p.MapDelete("/flag-environments/{environmentId:int}", DeleteEnvironment).WithName("DeleteFlagEnvironment").WithSummary("Remove an environment (Maintainer+)");

        p.MapGet("/flag-segments", ListSegments).WithName("ListFlagSegments").WithSummary("Segments (lists of user ids) rules can target");
        p.MapPut("/flag-segments/{segmentKey}", SaveSegment).WithName("SaveFlagSegment").WithSummary("Create or replace a segment (Developer+)")
            .WithJsonBody<SaveSegmentRequest>();
        p.MapDelete("/flag-segments/{segmentKey}", DeleteSegment).WithName("DeleteFlagSegment").WithSummary("Delete a segment no rule uses (Maintainer+)");

        p.MapGet("/flag-sdk-keys", ListKeys).WithName("ListFlagSdkKeys").WithSummary("SDK keys (never the secrets), Maintainer+");
        p.MapPost("/flag-sdk-keys", CreateKey).WithName("CreateFlagSdkKey").WithSummary("Create an SDK key for an environment (Maintainer+)")
            .WithDescription("kind: client (evaluate only) or server (also downloads rules). The secret is in this response only.")
            .WithJsonBody<CreateSdkKeyRequest>().Produces<FlagSdkKeyCreatedDto>(StatusCodes.Status201Created);
        p.MapPost("/flag-sdk-keys/{id:int}/rotate", RotateKey).WithName("RotateFlagSdkKey").WithSummary("Replace a key's secret (Maintainer+)");
        p.MapDelete("/flag-sdk-keys/{id:int}", RevokeKey).WithName("RevokeFlagSdkKey").WithSummary("Revoke a key (Maintainer+)");
    }

    private static async Task<Project> ProjectAsync(IssueService issues, string key) => await IssueEndpoints.RequireProjectAsync(issues, key);

    private static async Task<FeatureFlag> FlagAsync(FlagService flags, Project project, string flagKey) =>
        await flags.GetFlagAsync(project.Id, flagKey) ?? throw ApiException.NotFound($"Flag '{flagKey}' not found.");

    private static async Task<List<FlagDto>> ListFlags(HttpContext http, IssueService issues, FlagService flags, PublicBaseUrl url, string key, bool? archived)
    {
        var project = await ProjectAsync(issues, key);
        var b = url.For(http.Request);
        var all = (await flags.ListFlagsAsync(project.Id, archived == true)).Select(f => FlagDtos.Flag(f, project.Key, b)).ToList();
        return Paging.From(http.Request).Apply(http, all, x => x);
    }

    private static async Task<IResult> CreateFlag(HttpContext http, IssueService issues, FlagService flags, PublicBaseUrl url, string key)
    {
        var project = await ProjectAsync(issues, key);
        var body = await ApiResults.ReadBodyAsync<CreateFlagRequest>(http.Request);
        if (!FlagDtos.TryParseType(body.Type, out var type)) throw ApiException.BadRequest("type must be boolean, string, number or json.");
        var flag = await flags.CreateFlagAsync(project.Id, body.Key ?? "", body.Name ?? "", body.Description, type, body.DefaultValue, body.OffValue);
        var created = await FlagAsync(flags, project, flag.Key);
        var dto = FlagDtos.Flag(created, project.Key, url.For(http.Request));
        return TypedResults.Created(dto.WebUrl, dto);
    }

    private static async Task<List<StaleFlagDto>> StaleFlags(HttpContext http, IssueService issues, FlagService flags, string key, int? days)
    {
        var project = await ProjectAsync(issues, key);
        var d = days ?? FlagService.DefaultStaleDays;
        if (d is < 1 or > 3650) throw ApiException.BadRequest("days must be between 1 and 3650.");
        return (await flags.StaleFlagsAsync(project.Id, d)).Select(s => new StaleFlagDto(s.Flag.Key, s.Flag.Name, Dto.Utc(s.LastChanged), Dto.Utc(s.LastEvaluated), s.IdleDays)).ToList();
    }

    private static async Task<FlagDto> GetFlag(HttpContext http, IssueService issues, FlagService flags, PublicBaseUrl url, string key, string flagKey)
    {
        var project = await ProjectAsync(issues, key);
        return FlagDtos.Flag(await FlagAsync(flags, project, flagKey), project.Key, url.For(http.Request));
    }

    private static async Task<FlagDto> UpdateFlag(HttpContext http, IssueService issues, FlagService flags, PublicBaseUrl url, string key, string flagKey)
    {
        var project = await ProjectAsync(issues, key);
        var current = await FlagAsync(flags, project, flagKey);
        var body = await ApiResults.ReadBodyAsync<UpdateFlagRequest>(http.Request);
        await flags.UpdateDetailsAsync(project.Id, current.Key, body.Name ?? current.Name, body.Description ?? current.Description, body.IssueNumbers ?? current.LinkedIssues);
        if (body.OffValue != null) await flags.SetOffValueAsync(project.Id, current.Key, body.OffValue);
        return FlagDtos.Flag(await FlagAsync(flags, project, flagKey), project.Key, url.For(http.Request));
    }

    private static async Task<IResult> DeleteFlag(IssueService issues, FlagService flags, string key, string flagKey)
    {
        var project = await ProjectAsync(issues, key);
        await FlagAsync(flags, project, flagKey);
        await flags.DeleteFlagAsync(project.Id, flagKey);
        return TypedResults.NoContent();
    }

    private static async Task<FlagDto> Archive(HttpContext http, FlagService flags, IssueService issues, PublicBaseUrl url, string key, string flagKey, bool archive)
    {
        var project = await ProjectAsync(issues, key);
        await FlagAsync(flags, project, flagKey);
        await flags.ArchiveAsync(project.Id, flagKey, archive);
        return FlagDtos.Flag(await FlagAsync(flags, project, flagKey), project.Key, url.For(http.Request));
    }

    private static async Task<FlagDto> SetState(HttpContext http, IssueService issues, FlagService flags, PublicBaseUrl url, string key, string flagKey, int environmentId)
    {
        var project = await ProjectAsync(issues, key);
        var current = await FlagAsync(flags, project, flagKey);
        var body = await ApiResults.ReadBodyAsync<FlagStateRequest>(http.Request);
        if (current.States.All(s => s.EnvironmentId != environmentId)) throw ApiException.NotFound($"Environment {environmentId} not found.");
        // Validate everything first so a bad rule doesn't leave the toggle half applied.
        if (body.DefaultValue != null) FlagEvaluator.NormalizeValue(current.Type, body.DefaultValue);
        if (body.Rules != null) FlagEvaluator.ValidateRules(current.Type, FlagDtos.ToRules(body.Rules));
        if (body.Rules != null) await flags.SetRulesAsync(project.Id, flagKey, environmentId, FlagDtos.ToRules(body.Rules));
        if (body.DefaultValue != null) await flags.SetDefaultValueAsync(project.Id, flagKey, environmentId, body.DefaultValue);
        if (body.Enabled is { } enabled) await flags.SetEnabledAsync(project.Id, flagKey, environmentId, enabled);
        return FlagDtos.Flag(await FlagAsync(flags, project, flagKey), project.Key, url.For(http.Request));
    }

    private static async Task<List<FlagChangeDto>> History(HttpContext http, IssueService issues, FlagService flags, string key, string flagKey)
    {
        var project = await ProjectAsync(issues, key);
        await FlagAsync(flags, project, flagKey);
        var paging = Paging.From(http.Request);
        var (changes, total) = await flags.HistoryAsync(project.Id, flagKey, paging.PerPage, paging.Skip);
        paging.WriteHeaders(http, total);
        return changes.Select(c => new FlagChangeDto(c.Id, c.Kind, c.EnvironmentName, new UserRefDto(c.ActorId, c.ActorName), Dto.Utc(c.At), c.OldValue, c.NewValue)).ToList();
    }

    private static async Task<List<FlagEnvironmentDto>> ListEnvironments(IssueService issues, FlagService flags, string key)
    {
        var project = await ProjectAsync(issues, key);
        return (await flags.ListEnvironmentsAsync(project.Id)).Select(e => new FlagEnvironmentDto(e.Id, e.Name)).ToList();
    }

    private static async Task<IResult> CreateEnvironment(HttpContext http, IssueService issues, FlagService flags, string key)
    {
        var project = await ProjectAsync(issues, key);
        var body = await ApiResults.ReadBodyAsync<CreateFlagEnvironmentRequest>(http.Request);
        var env = await flags.CreateEnvironmentAsync(project.Id, body.Name ?? "");
        return TypedResults.Created($"/api/v1/projects/{project.Key}/flag-environments", new FlagEnvironmentDto(env.Id, env.Name));
    }

    private static async Task<IResult> DeleteEnvironment(IssueService issues, FlagService flags, string key, int environmentId)
    {
        var project = await ProjectAsync(issues, key);
        if ((await flags.ListEnvironmentsAsync(project.Id)).All(e => e.Id != environmentId)) throw ApiException.NotFound("Environment not found.");
        await flags.DeleteEnvironmentAsync(project.Id, environmentId);
        return TypedResults.NoContent();
    }

    private static FlagSegmentDto Segment(FlagSegment s) => new(s.Key, s.Name, s.Description, FlagService.SegmentMembers(s), Dto.Utc(s.UpdatedAt));

    private static async Task<List<FlagSegmentDto>> ListSegments(IssueService issues, FlagService flags, string key)
    {
        var project = await ProjectAsync(issues, key);
        return (await flags.ListSegmentsAsync(project.Id)).Select(Segment).ToList();
    }

    private static async Task<FlagSegmentDto> SaveSegment(HttpContext http, IssueService issues, FlagService flags, string key, string segmentKey)
    {
        var project = await ProjectAsync(issues, key);
        var body = await ApiResults.ReadBodyAsync<SaveSegmentRequest>(http.Request);
        return Segment(await flags.SaveSegmentAsync(project.Id, segmentKey, body.Name ?? segmentKey, body.Description, body.Members ?? []));
    }

    private static async Task<IResult> DeleteSegment(IssueService issues, FlagService flags, string key, string segmentKey)
    {
        var project = await ProjectAsync(issues, key);
        if ((await flags.ListSegmentsAsync(project.Id)).All(s => s.Key != segmentKey.ToLowerInvariant())) throw ApiException.NotFound("Segment not found.");
        await flags.DeleteSegmentAsync(project.Id, segmentKey);
        return TypedResults.NoContent();
    }

    private static async Task<List<FlagSdkKeyDto>> ListKeys(IssueService issues, FlagService flags, ProjectAccess access, string key)
    {
        var project = await ProjectAsync(issues, key);
        await access.RequireAsync(project.Id, Permission.ManageFlags);
        return (await flags.ListKeysAsync(project.Id)).Select(FlagDtos.Key).ToList();
    }

    private static async Task<IResult> CreateKey(HttpContext http, IssueService issues, FlagService flags, string key)
    {
        var project = await ProjectAsync(issues, key);
        var body = await ApiResults.ReadBodyAsync<CreateSdkKeyRequest>(http.Request);
        var kind = SdkKeyKind.Client;
        if (!string.IsNullOrWhiteSpace(body.Kind) && !(Enum.TryParse(body.Kind, true, out kind) && Enum.IsDefined(kind)))
            throw ApiException.BadRequest("kind must be client or server.");
        var (k, secret) = await flags.CreateKeyAsync(project.Id, body.EnvironmentId, body.Name ?? "", kind);
        var env = (await flags.ListEnvironmentsAsync(project.Id)).FirstOrDefault(e => e.Id == k.EnvironmentId);
        k.Environment = env!;
        return TypedResults.Created($"/api/v1/projects/{project.Key}/flag-sdk-keys", new FlagSdkKeyCreatedDto(FlagDtos.Key(k), secret));
    }

    private static async Task<FlagSdkKeyCreatedDto> RotateKey(IssueService issues, FlagService flags, string key, int id)
    {
        var project = await ProjectAsync(issues, key);
        var (k, secret) = await flags.RotateKeyAsync(project.Id, id);
        return new FlagSdkKeyCreatedDto(FlagDtos.Key(k), secret);
    }

    private static async Task<IResult> RevokeKey(IssueService issues, FlagService flags, string key, int id)
    {
        var project = await ProjectAsync(issues, key);
        await flags.RevokeKeyAsync(project.Id, id);
        return TypedResults.NoContent();
    }
}
