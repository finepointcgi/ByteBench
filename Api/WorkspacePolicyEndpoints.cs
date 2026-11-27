using ByteBench.Data;
using ByteBench.Services.Workspaces;

namespace ByteBench.Api;

/// <summary>A limit of 0 means no limit. Memory and disk in MB, CPUs as a number of CPUs.</summary>
public record WorkspaceLimitsDto(int MaxRunning, int MaxTotal, double MaxCpus, int MaxMemoryMb, int MaxDiskMb, double DefaultCpus, int DefaultMemoryMb, int DefaultDiskMb);
public record WorkspaceUsageDto(int Running, int Total, double Cpus, int MemoryMb, int DiskMb);
public record WorkspaceLimitsResponse(WorkspaceLimitsDto Limits, WorkspaceUsageDto Usage, int IdleTimeoutMinutes, int IdleDefaultMinutes, int IdleMaxMinutes, int? IdleOverrideMinutes);
/// <summary>PUT body: omitted fields keep their current value.</summary>
public record SetWorkspaceLimitsRequest(int? MaxRunning = null, int? MaxTotal = null, double? MaxCpus = null, int? MaxMemoryMb = null, int? MaxDiskMb = null,
    double? DefaultCpus = null, int? DefaultMemoryMb = null, int? DefaultDiskMb = null, int? IdleDefaultMinutes = null, int? IdleMaxMinutes = null);
/// <summary><c>minutes</c> null goes back to the default.</summary>
public record SetMyIdleRequest(int? Minutes = null);
public record WorkspaceIdleDto(int TimeoutMinutes, DateTimeOffset? StopsAt, bool TaskRunning, bool Warning, int WarnMinutes);

public record PrebuildDto(int Id, string Ref, string? CommitSha, string State, string Trigger, bool Usable, string? Error, DateTimeOffset CreatedAt, DateTimeOffset? StartedAt, DateTimeOffset? FinishedAt, string? Log);
public record PrebuildSettingsDto(bool Enabled, IReadOnlyList<string> Branches, int KeepLast, int RetentionDays, bool ServerEnabled, string DefaultBranch);
/// <summary>PUT body: omitted fields keep their current value. <c>branches</c> empty means the default branch.</summary>
public record SetPrebuildSettingsRequest(bool? Enabled = null, IReadOnlyList<string>? Branches = null, int? KeepLast = null, int? RetentionDays = null);
/// <summary><c>branch</c> defaults to the default branch.</summary>
public record RebuildRequest(string? Branch = null);

/// <summary>Resource limits and idle auto-stop (<c>/workspaces/limits</c>, <c>/workspaces/{id}/idle</c>) and prebuilds (<c>/projects/{key}/repos/{slug}/prebuilds</c>).</summary>
public static class WorkspacePolicyEndpoints
{
    public static void MapWorkspacePolicyEndpoints(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/workspaces").WithTags("Workspaces");
        g.MapGet("/limits", GetLimits).WithName("GetWorkspaceLimits").WithSummary("Your workspace usage next to the limits")
            .WithDescription("Running and total workspaces, CPUs, memory and disk in use against the caps (0 = no limit), what a new workspace is given, and your idle period.");
        g.MapPut("/limits", SetLimits).WithName("SetWorkspaceLimits").WithSummary("Set the workspace limits and idle periods (site administrators)")
            .WithDescription("Applies to the next workspace anyone starts; running ones are untouched. 403 for non-administrators, 422 for numbers that don't fit together. Audited.")
            .WithJsonBody<SetWorkspaceLimitsRequest>();
        g.MapPut("/me/idle", SetMyIdle).WithName("SetMyWorkspaceIdle").WithSummary("Choose your own idle period")
            .WithDescription("Minutes without IDE or preview traffic and without a running task before your workspaces are stopped (they keep their volumes). Up to the administrator's maximum; null goes back to the default.")
            .WithJsonBody<SetMyIdleRequest>();
        g.MapGet("/{id}/idle", GetIdle).WithName("GetWorkspaceIdle").WithSummary("When an idle workspace will be stopped")
            .WithDescription("`warning` is true in the last minutes before it. 404 unless it is your running workspace.");
        g.MapPost("/{id}/keepalive", KeepAlive).WithName("KeepWorkspaceAlive").WithSummary("Count as activity and restart the idle period")
            .WithDescription("404 unless it is your running workspace.");

        var p = api.MapGroup("/projects/{key}/repos/{slug}").WithTags("Workspaces");
        p.MapGet("/prebuild-settings", GetPrebuildSettings).WithName("GetPrebuildSettings").WithSummary("The repository's prebuild settings")
            .WithDescription("Needs Developer+. Prebuilds are off until a Maintainer turns them on.");
        p.MapPut("/prebuild-settings", SetPrebuildSettings).WithName("SetPrebuildSettings").WithSummary("Change the repository's prebuild settings")
            .WithDescription("Needs Maintainer+. Audited. branches: names or patterns such as release/*; empty means the default branch. keepLast: 1-20 per branch; retentionDays: 1-90.")
            .WithJsonBody<SetPrebuildSettingsRequest>();
        p.MapGet("/prebuilds", ListPrebuilds).WithName("ListPrebuilds").WithSummary("The repository's prebuilds, newest first")
            .WithDescription("Needs Developer+. state: Queued, Building, Ready, Failed, Superseded or Expired. `usable` is true when new workspaces on that branch would start from it.");
        p.MapGet("/prebuilds/{id:int}", GetPrebuild).WithName("GetPrebuild").WithSummary("One prebuild with its build log");
        p.MapPost("/prebuilds", Rebuild).WithName("RebuildPrebuild").WithSummary("Rebuild now")
            .WithDescription("Needs Maintainer+. 202 with the queued prebuild; poll it until it is Ready or Failed. 422 when prebuilds are off or the branch has no .bytebench/workspace.yml.")
            .WithJsonBody<RebuildRequest>(optional: true);
        p.MapDelete("/prebuilds/{id:int}", DeletePrebuild).WithName("DeletePrebuild").WithSummary("Delete a prebuild and its image")
            .WithDescription("Needs Maintainer+. 422 while it is building.");
        p.MapPost("/prebuilds/cleanup", CleanupPrebuilds).WithName("CleanupPrebuilds").WithSummary("Clean up old prebuilds now")
            .WithDescription("Needs Maintainer+. Applies keepLast and retentionDays immediately.");
    }

    private static WorkspaceLimitsDto ToDto(WorkspaceLimitSettings s) => new(s.MaxRunning, s.MaxTotal, s.MaxCpus, s.MaxMemoryMb, s.MaxDiskMb, s.DefaultCpus, s.DefaultMemoryMb, s.DefaultDiskMb);

    private static WorkspaceLimitsResponse ToDto(WorkspaceLimitsView v) => new(ToDto(v.Limits),
        new WorkspaceUsageDto(v.Usage.Running, v.Usage.Total, v.Usage.CpuMillis / 1000.0, v.Usage.MemoryMb, v.Usage.DiskMb), v.IdleTimeoutMinutes, v.IdleDefaultMinutes, v.IdleMaxMinutes, v.IdleOverrideMinutes);

    private static async Task<WorkspaceLimitsResponse> GetLimits(ApiCaller caller, WorkspaceLimitService limits) => ToDto(await limits.GetViewAsync(caller.Id));

    private static async Task<WorkspaceLimitsResponse> SetLimits(HttpContext http, ApiCaller caller, WorkspaceLimitService limits, WorkspaceIdlePolicy idle)
    {
        var body = await ApiResults.ReadBodyAsync<SetWorkspaceLimitsRequest>(http.Request);
        var cur = await limits.GetAsync();
        var next = new WorkspaceLimitSettings
        {
            MaxRunning = body.MaxRunning ?? cur.MaxRunning, MaxTotal = body.MaxTotal ?? cur.MaxTotal, MaxCpus = body.MaxCpus ?? cur.MaxCpus,
            MaxMemoryMb = body.MaxMemoryMb ?? cur.MaxMemoryMb, MaxDiskMb = body.MaxDiskMb ?? cur.MaxDiskMb, DefaultCpus = body.DefaultCpus ?? cur.DefaultCpus,
            DefaultMemoryMb = body.DefaultMemoryMb ?? cur.DefaultMemoryMb, DefaultDiskMb = body.DefaultDiskMb ?? cur.DefaultDiskMb,
        };
        var (def, max) = await idle.GetSiteAsync();
        await limits.SetAsync(next, caller.Id);
        if (body.IdleDefaultMinutes != null || body.IdleMaxMinutes != null) await limits.SetIdleAsync(body.IdleDefaultMinutes ?? def, body.IdleMaxMinutes ?? max, caller.Id);
        return ToDto(await limits.GetViewAsync(caller.Id));
    }

    private static async Task<WorkspaceLimitsResponse> SetMyIdle(HttpContext http, ApiCaller caller, WorkspaceLimitService limits)
    {
        var body = http.Request.ContentLength is > 0 ? await ApiResults.ReadBodyAsync<SetMyIdleRequest>(http.Request) : new SetMyIdleRequest();
        await limits.SetMyIdleAsync(caller.Id, body.Minutes);
        return ToDto(await limits.GetViewAsync(caller.Id));
    }

    private static async Task<WorkspaceIdleDto> GetIdle(ApiCaller caller, WorkspaceIdlePolicy idle, string id)
    {
        var s = await idle.StatusAsync(id, caller.Id) ?? throw ApiException.NotFound("Workspace not found, or it isn't running.");
        return new WorkspaceIdleDto(s.TimeoutMinutes, Dto.Utc(s.StopsAt), s.TaskRunning, s.Warning, s.WarnMinutes);
    }

    private static async Task<WorkspaceIdleDto> KeepAlive(ApiCaller caller, WorkspaceIdlePolicy idle, string id)
    {
        if (!await idle.KeepAliveAsync(id, caller.Id)) throw ApiException.NotFound("Workspace not found, or it isn't running.");
        return await GetIdle(caller, idle, id);
    }

    // ---------------------------------------------------------------- prebuilds

    private static async Task RequireRepoAsync(ByteBench.Services.RepositoryService repos, string key, string slug)
    {
        if (await repos.GetRepositoryAsync(key, slug) == null) throw ApiException.NotFound($"Repository '{key.ToUpperInvariant()}/{slug}' not found.");
    }

    private static PrebuildDto ToDto(WorkspacePrebuild p, bool includeLog) => new(p.Id, p.Ref, p.CommitSha, p.State.ToString(), p.Trigger, p.State == PrebuildState.Ready && p.ImageTag != null,
        p.Error, Dto.Utc(p.CreatedAt), Dto.Utc(p.StartedAt), Dto.Utc(p.FinishedAt), includeLog ? p.Log : null);

    private static PrebuildSettingsDto ToDto(PrebuildSettingsView v) => new(v.Enabled, v.Branches, v.KeepLast, v.RetentionDays, v.ServerEnabled, v.DefaultBranch);

    private static async Task<PrebuildSettingsDto> GetPrebuildSettings(ApiCaller caller, ByteBench.Services.RepositoryService repos, WorkspacePrebuildManager prebuilds, string key, string slug)
    {
        await RequireRepoAsync(repos, key, slug);
        return ToDto(await prebuilds.GetSettingsAsync(key, slug, caller.Id));
    }

    private static async Task<PrebuildSettingsDto> SetPrebuildSettings(HttpContext http, ApiCaller caller, ByteBench.Services.RepositoryService repos, WorkspacePrebuildManager prebuilds, string key, string slug)
    {
        await RequireRepoAsync(repos, key, slug);
        var body = await ApiResults.ReadBodyAsync<SetPrebuildSettingsRequest>(http.Request);
        var cur = await prebuilds.GetSettingsAsync(key, slug, caller.Id);
        await prebuilds.SetSettingsAsync(key, slug, body.Enabled ?? cur.Enabled, body.Branches != null ? string.Join(',', body.Branches) : cur.BranchesText,
            body.KeepLast ?? cur.KeepLast, body.RetentionDays ?? cur.RetentionDays, caller.Id);
        return ToDto(await prebuilds.GetSettingsAsync(key, slug, caller.Id));
    }

    private static async Task<List<PrebuildDto>> ListPrebuilds(ApiCaller caller, ByteBench.Services.RepositoryService repos, WorkspacePrebuildManager prebuilds, string key, string slug)
    {
        await RequireRepoAsync(repos, key, slug);
        return (await prebuilds.ListAsync(key, slug, caller.Id)).Select(p => ToDto(p, false)).ToList();
    }

    private static async Task<PrebuildDto> GetPrebuild(ApiCaller caller, ByteBench.Services.RepositoryService repos, WorkspacePrebuildManager prebuilds, string key, string slug, int id)
    {
        await RequireRepoAsync(repos, key, slug);
        return ToDto(await prebuilds.GetAsync(key, slug, id, caller.Id) ?? throw ApiException.NotFound("Prebuild not found."), true);
    }

    private static async Task<IResult> Rebuild(HttpContext http, ApiCaller caller, ByteBench.Services.RepositoryService repos, WorkspacePrebuildManager prebuilds, string key, string slug)
    {
        await RequireRepoAsync(repos, key, slug);
        var body = http.Request.ContentLength is > 0 ? await ApiResults.ReadBodyAsync<RebuildRequest>(http.Request) : new RebuildRequest();
        var pb = await prebuilds.RebuildAsync(key, slug, body.Branch, caller.Id);
        return TypedResults.Json(ToDto(pb, false), statusCode: StatusCodes.Status202Accepted);
    }

    private static async Task<IResult> DeletePrebuild(ApiCaller caller, ByteBench.Services.RepositoryService repos, WorkspacePrebuildManager prebuilds, string key, string slug, int id)
    {
        await RequireRepoAsync(repos, key, slug);
        await prebuilds.DeleteAsync(key, slug, id, caller.Id);
        return TypedResults.NoContent();
    }

    private static async Task<IResult> CleanupPrebuilds(ApiCaller caller, ByteBench.Services.RepositoryService repos, WorkspacePrebuildManager prebuilds, string key, string slug)
    {
        await RequireRepoAsync(repos, key, slug);
        await prebuilds.CleanupAsync(key, slug, caller.Id);
        return TypedResults.NoContent();
    }
}
