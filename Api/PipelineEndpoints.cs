using System.IO.Compression;
using System.Text;
using ByteBench.Data;
using ByteBench.Services;
using ByteBench.Services.Ci;

namespace ByteBench.Api;

public record PipelineStepDto(int Id, string Name, PipelineStatus Status, int? ExitCode, DateTimeOffset? StartedAt, DateTimeOffset? FinishedAt);

public record PipelineJobDto(int Id, string Name, string Image, PipelineStatus Status, IReadOnlyList<string> Needs, bool Manual, bool AllowFailure,
    string? Environment, bool RequiresApproval, DateTimeOffset? StartedAt, DateTimeOffset? FinishedAt, IReadOnlyList<PipelineStepDto> Steps);

/// <summary>A pipeline. <c>jobs</c> (with steps) is only present when one pipeline is fetched, not in lists.</summary>
public record PipelineDto(int Id, string Ref, string CommitSha, string? CommitMessage, int? PullRequestNumber, PipelineStatus Status,
    PipelineTrigger Trigger, UserDto? TriggeredBy, string? Error, bool ForkPullRequest, bool AwaitingApproval, UserDto? ApprovedBy,
    DateTimeOffset CreatedAt, DateTimeOffset? StartedAt, DateTimeOffset? FinishedAt, string WebUrl, IReadOnlyList<PipelineJobDto>? Jobs);

public record PipelineArtifactDto(int Id, int JobId, string JobName, long SizeBytes, int FileCount, DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt, bool Keep, string DownloadUrl);

public record ArtifactFileDto(string Path, long Size);

public record PipelineArtifactDetailDto(PipelineArtifactDto Artifact, IReadOnlyList<ArtifactFileDto> Files);

/// <summary>A CI variable. The value is never returned. <c>masked</c> is always true: values are scrubbed from job logs.</summary>
public record CiVariableDto(int Id, string Name, bool Protected, bool Masked, string Scope, string? Repository, DateTimeOffset UpdatedAt, string? UpdatedBy);

/// <summary>POST body to run a pipeline for a branch (default: the repository's default branch).</summary>
public record RunPipelineRequest(string? Branch = null);

/// <summary>
/// POST/PUT body for a CI variable. <c>name</c> and <c>value</c> are required on create; on update, omitted fields keep their
/// stored value (a value can be replaced but never read back). <c>protected</c>: only exposed to pipelines on protected branches.
/// </summary>
public record CiVariableRequest(string? Name = null, string? Value = null, bool? Protected = null);

/// <summary>CI pipelines of a repository: list, get, logs, run, cancel, retry, play, artifacts, and CI variables (names only).</summary>
public static class PipelineEndpoints
{
    public const int MaxLogChars = 1_048_576;

    public static void MapPipelineEndpoints(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/projects/{key}/repos/{slug}").WithTags("Pipelines");
        g.MapGet("/pipelines", ListPipelines).WithName("ListPipelines").WithSummary("List a repository's pipelines, newest first")
            .WithDescription("?ref= (branch name) and ?status= (queued, running, passed, failed, canceled, error, skipped, manual) filter the list.");
        g.MapPost("/pipelines", RunPipeline).WithName("RunPipeline").WithSummary("Run a pipeline for a branch")
            .WithDescription("Needs Developer+. Runs the branch's current tip, ignoring the pipeline file's filters. 422 if CI is off, the branch doesn't exist or has no pipeline file.")
            .WithJsonBody<RunPipelineRequest>(optional: true).Produces<PipelineDto>(StatusCodes.Status201Created);
        g.MapGet("/pipelines/{id:int}", GetPipeline).WithName("GetPipeline").WithSummary("Get a pipeline with its jobs and steps (no logs)");
        g.MapPost("/pipelines/{id:int}/cancel", Cancel).WithName("CancelPipeline").WithSummary("Cancel a queued or running pipeline")
            .WithDescription("Needs Developer+. A running pipeline stops shortly after; poll GET for the final status.");
        g.MapPost("/pipelines/{id:int}/rerun", Rerun).WithName("RerunPipeline").WithSummary("Run a pipeline's commit and branch again")
            .WithDescription("Needs Developer+. Creates a new pipeline (201). A fork pull request's pipeline waits for a Maintainer's approval unless a Maintainer re-runs it.")
            .Produces<PipelineDto>(StatusCodes.Status201Created);
        g.MapPost("/pipelines/{id:int}/approve", Approve).WithName("ApprovePipeline").WithSummary("Approve and run a fork pull request's pipeline")
            .WithDescription("Needs Maintainer+. Fork pipelines never get CI variables, approved or not.");
        g.MapGet("/pipelines/{id:int}/steps/{stepId:int}/log", GetLog).WithName("GetPipelineStepLog").WithSummary("A step's log as plain text")
            .WithDescription("?offset= and ?limit= (characters; limit max 1048576) page through a big log, ?tail=N returns the last N characters. " +
                             "Headers: X-Log-Total-Length, X-Log-Offset, X-Log-Next-Offset (absent at the end), X-Log-Truncated (the runner cut the stored log at its size limit). Secrets are masked.")
            .Produces<string>(StatusCodes.Status200OK, "text/plain");
        g.MapPost("/pipelines/{id:int}/jobs/{jobId:int}/retry", RetryJob).WithName("RetryPipelineJob").WithSummary("Run a finished job again")
            .WithDescription("Needs Developer+ (Maintainer+ for a job that deploys to a protected environment, which also waits for approvals). 422 if the job hasn't finished.");
        g.MapPost("/pipelines/{id:int}/jobs/{jobId:int}/play", PlayJob).WithName("PlayPipelineJob").WithSummary("Run a manual job")
            .WithDescription("Needs Developer+ (a protected environment's job goes through deployment approvals). 422 if the job isn't waiting to be run.");
        g.MapGet("/pipelines/{id:int}/artifacts", ListArtifacts).WithName("ListPipelineArtifacts").WithSummary("A pipeline's artifacts, one per job that saved some");
        g.MapGet("/pipelines/{id:int}/jobs/{jobId:int}/artifacts", GetArtifact).WithName("GetJobArtifacts").WithSummary("A job's artifacts with the files in the zip");
        g.MapGet("/pipelines/{id:int}/jobs/{jobId:int}/artifacts.zip", DownloadArtifact).WithName("DownloadJobArtifacts")
            .WithSummary("Download a job's artifacts as a zip").WithDescription("Streams the zip (range requests work) as an attachment, never rendered.")
            .Produces(StatusCodes.Status200OK, contentType: "application/octet-stream");
        g.MapGet("/pipelines/{id:int}/jobs/{jobId:int}/artifacts/file", DownloadArtifactFile).WithName("DownloadJobArtifactFile")
            .WithSummary("Download one file from a job's artifacts").WithDescription("?path= is the file's path inside the zip.")
            .Produces(StatusCodes.Status200OK, contentType: "application/octet-stream");

        var rv = g.MapGroup("/ci/variables");
        rv.MapGet("", (HttpContext h, RepositoryService r, CiVariableService v, string key, string slug) => ListVariables(r, v, key, slug)).WithName("ListRepositoryCiVariables")
            .WithSummary("A repository's CI variables (names and flags, never values)").WithDescription("Needs Maintainer+.");
        rv.MapPost("", (HttpContext h, RepositoryService r, CiVariableService v, string key, string slug) => SaveVariable(h, r, v, key, slug, null)).WithName("CreateRepositoryCiVariable")
            .WithSummary("Create a repository CI variable").WithDescription("Needs Maintainer+. Overrides a project variable of the same name. The value is encrypted and never returned.")
            .WithJsonBody<CiVariableRequest>().Produces<CiVariableDto>(StatusCodes.Status201Created);
        rv.MapPut("/{id:int}", (HttpContext h, RepositoryService r, CiVariableService v, string key, string slug, int id) => SaveVariable(h, r, v, key, slug, id)).WithName("UpdateRepositoryCiVariable")
            .WithSummary("Update a repository CI variable").WithDescription("Needs Maintainer+. Omitted fields keep their stored value.").WithJsonBody<CiVariableRequest>();
        rv.MapDelete("/{id:int}", (RepositoryService r, CiVariableService v, string key, string slug, int id) => DeleteVariable(r, v, key, slug, id)).WithName("DeleteRepositoryCiVariable")
            .WithSummary("Delete a repository CI variable");

        var pv = api.MapGroup("/projects/{key}/ci/variables").WithTags("Pipelines");
        pv.MapGet("", (IssueService i, CiVariableService v, string key) => ListProjectVariables(i, v, key)).WithName("ListProjectCiVariables")
            .WithSummary("A project's CI variables (names and flags, never values)").WithDescription("Needs Maintainer+. Every repository of the project gets them.");
        pv.MapPost("", (HttpContext h, IssueService i, CiVariableService v, string key) => SaveProjectVariable(h, i, v, key, null)).WithName("CreateProjectCiVariable")
            .WithSummary("Create a project CI variable").WithDescription("Needs Maintainer+. The value is encrypted and never returned.")
            .WithJsonBody<CiVariableRequest>().Produces<CiVariableDto>(StatusCodes.Status201Created);
        pv.MapPut("/{id:int}", (HttpContext h, IssueService i, CiVariableService v, string key, int id) => SaveProjectVariable(h, i, v, key, id)).WithName("UpdateProjectCiVariable")
            .WithSummary("Update a project CI variable").WithDescription("Needs Maintainer+. Omitted fields keep their stored value.").WithJsonBody<CiVariableRequest>();
        pv.MapDelete("/{id:int}", (IssueService i, CiVariableService v, string key, int id) => DeleteProjectVariable(i, v, key, id)).WithName("DeleteProjectCiVariable")
            .WithSummary("Delete a project CI variable");
    }

    // ---------------------------------------------------------------- DTOs

    private static string WebUrl(string baseUrl, Repository repo, int id) => baseUrl + PipelineUrls.Detail(repo, id);

    public static PipelineDto ToDto(Repository repo, Pipeline p, string baseUrl, int? prNumber, bool withJobs) => new(
        p.Id, p.Ref, p.CommitSha, p.CommitMessage, prNumber, p.Status, p.Trigger, Dto.UserOrNull(p.TriggeredBy), p.Error, p.IsForkPullRequest,
        p.AwaitingApproval, Dto.UserOrNull(p.ApprovedBy), Dto.Utc(p.CreatedAt), Dto.Utc(p.StartedAt), Dto.Utc(p.FinishedAt), WebUrl(baseUrl, repo, p.Id),
        withJobs ? p.Jobs.Select(Job).ToList() : null);

    private static PipelineJobDto Job(PipelineJob j) => new(j.Id, j.Name, j.Image, j.Status, j.NeedsList.ToList(), j.IsManual, j.AllowFailure,
        j.EnvironmentName, j.RequiresApproval, Dto.Utc(j.StartedAt), Dto.Utc(j.FinishedAt),
        j.Steps.Select(s => new PipelineStepDto(s.Id, s.Name, s.Status, s.ExitCode, Dto.Utc(s.StartedAt), Dto.Utc(s.FinishedAt))).ToList());

    private static string ArtifactUrl(string baseUrl, string key, string slug, int pipelineId, int jobId) =>
        $"{baseUrl}/api/v1/projects/{key.ToUpperInvariant()}/repos/{slug}/pipelines/{pipelineId}/jobs/{jobId}/artifacts.zip";

    private static PipelineArtifactDto ArtifactDto(PipelineArtifact a, string jobName, string baseUrl, Repository repo, int pipelineId) =>
        new(a.Id, a.JobId, jobName, a.SizeBytes, a.FileCount, Dto.Utc(a.CreatedAt), Dto.Utc(a.ExpiresAt), a.Keep,
            ArtifactUrl(baseUrl, repo.Project.Key, repo.Slug, pipelineId, a.JobId));

    // ---------------------------------------------------------------- helpers

    private static async Task<Repository> RequireRepoAsync(RepositoryService repos, string key, string slug) =>
        await repos.GetRepositoryAsync(key, slug) ?? throw ApiException.NotFound($"Repository '{key.ToUpperInvariant()}/{slug}' not found.");

    private static async Task<Pipeline> RequirePipelineAsync(PipelineService pipelines, Repository repo, int id) =>
        await pipelines.GetAsync(repo, id) ?? throw ApiException.NotFound($"Pipeline {id} not found.");

    private static PipelineJob RequireJob(Pipeline p, int jobId) =>
        p.Jobs.FirstOrDefault(j => j.Id == jobId) ?? throw ApiException.NotFound($"Job {jobId} not found in pipeline {p.Id}.");

    private static async Task<PipelineDto> FullAsync(HttpContext http, PipelineService pipelines, PublicBaseUrl url, Repository repo, int id)
    {
        var p = await RequirePipelineAsync(pipelines, repo, id);
        return ToDto(repo, p, url.For(http.Request), await pipelines.GetPullRequestNumberAsync(p.PullRequestId), true);
    }

    // ---------------------------------------------------------------- pipelines

    private static async Task<List<PipelineDto>> ListPipelines(HttpContext http, RepositoryService repos, PipelineService pipelines, PublicBaseUrl url,
        string key, string slug, string? @ref, string? status)
    {
        var paging = Paging.From(http.Request);
        PipelineStatus? st = null;
        if (!string.IsNullOrWhiteSpace(status))
        {
            if (!Enum.TryParse<PipelineStatus>(status.Trim(), true, out var parsed) || !Enum.IsDefined(parsed))
                throw ApiException.BadRequest("status must be one of: " + string.Join(", ", Enum.GetNames<PipelineStatus>().Select(n => n.ToLowerInvariant())) + ".");
            st = parsed;
        }
        var repo = await RequireRepoAsync(repos, key, slug);
        var page = await pipelines.ListAsync(repo, paging.Page, paging.PerPage, string.IsNullOrWhiteSpace(@ref) ? null : @ref.Trim(), st);
        paging.WriteHeaders(http, page.Total);
        var b = url.For(http.Request);
        var numbers = new Dictionary<int, int?>();
        foreach (var prId in page.Items.Where(p => p.PullRequestId != null).Select(p => p.PullRequestId!.Value).Distinct())
            numbers[prId] = await pipelines.GetPullRequestNumberAsync(prId);
        return page.Items.Select(p => ToDto(repo, p, b, p.PullRequestId is { } id ? numbers[id] : null, false)).ToList();
    }

    private static async Task<PipelineDto> GetPipeline(HttpContext http, RepositoryService repos, PipelineService pipelines, PublicBaseUrl url, string key, string slug, int id) =>
        await FullAsync(http, pipelines, url, await RequireRepoAsync(repos, key, slug), id);

    private static async Task<IResult> RunPipeline(HttpContext http, RepositoryService repos, PipelineService pipelines, PublicBaseUrl url, string key, string slug)
    {
        var repo = await RequireRepoAsync(repos, key, slug);
        var body = http.Request.ContentLength is > 0 || ApiResults.IsJson(http.Request) && http.Request.ContentLength != 0
            ? await ApiResults.ReadBodyAsync<RunPipelineRequest>(http.Request) : new RunPipelineRequest();
        var branch = string.IsNullOrWhiteSpace(body.Branch) ? repo.DefaultBranch : body.Branch.Trim();
        var id = await pipelines.RunAsync(repo, branch);
        return TypedResults.Created($"/api/v1/projects/{key.ToUpperInvariant()}/repos/{slug}/pipelines/{id}", await FullAsync(http, pipelines, url, repo, id));
    }

    private static async Task<PipelineDto> Cancel(HttpContext http, RepositoryService repos, PipelineService pipelines, PublicBaseUrl url, string key, string slug, int id)
    {
        var repo = await RequireRepoAsync(repos, key, slug);
        await RequirePipelineAsync(pipelines, repo, id);
        await pipelines.CancelAsync(repo, id);
        return await FullAsync(http, pipelines, url, repo, id);
    }

    private static async Task<IResult> Rerun(HttpContext http, RepositoryService repos, PipelineService pipelines, PublicBaseUrl url, string key, string slug, int id)
    {
        var repo = await RequireRepoAsync(repos, key, slug);
        await RequirePipelineAsync(pipelines, repo, id);
        var newId = await pipelines.RerunAsync(repo, id);
        return TypedResults.Created($"/api/v1/projects/{key.ToUpperInvariant()}/repos/{slug}/pipelines/{newId}", await FullAsync(http, pipelines, url, repo, newId));
    }

    private static async Task<PipelineDto> Approve(HttpContext http, RepositoryService repos, PipelineService pipelines, PublicBaseUrl url, string key, string slug, int id)
    {
        var repo = await RequireRepoAsync(repos, key, slug);
        await RequirePipelineAsync(pipelines, repo, id);
        await pipelines.ApproveAsync(repo, id);
        return await FullAsync(http, pipelines, url, repo, id);
    }

    private static async Task<PipelineJobDto> RetryJob(RepositoryService repos, PipelineService pipelines, string key, string slug, int id, int jobId)
    {
        var repo = await RequireRepoAsync(repos, key, slug);
        RequireJob(await RequirePipelineAsync(pipelines, repo, id), jobId);
        await pipelines.RetryJobAsync(repo, id, jobId);
        return Job(RequireJob(await RequirePipelineAsync(pipelines, repo, id), jobId));
    }

    private static async Task<PipelineJobDto> PlayJob(RepositoryService repos, PipelineService pipelines, string key, string slug, int id, int jobId)
    {
        var repo = await RequireRepoAsync(repos, key, slug);
        RequireJob(await RequirePipelineAsync(pipelines, repo, id), jobId);
        await pipelines.PlayJobAsync(repo, id, jobId);
        return Job(RequireJob(await RequirePipelineAsync(pipelines, repo, id), jobId));
    }

    // ---------------------------------------------------------------- logs

    private static long QueryNumber(HttpRequest request, string name, long min, long max)
    {
        var raw = request.Query[name].ToString();
        if (!long.TryParse(raw, out var n) || n < min || n > max) throw ApiException.BadRequest($"{name} must be a number between {min} and {max}.");
        return n;
    }

    private static async Task<IResult> GetLog(HttpContext http, RepositoryService repos, PipelineService pipelines, string key, string slug, int id, int stepId)
    {
        var q = http.Request.Query;
        if (q.ContainsKey("tail") && (q.ContainsKey("offset") || q.ContainsKey("limit"))) throw ApiException.BadRequest("Use either tail, or offset and limit.");
        var tail = q.ContainsKey("tail") ? QueryNumber(http.Request, "tail", 1, MaxLogChars) : (long?)null;
        var offset = q.ContainsKey("offset") ? QueryNumber(http.Request, "offset", 0, int.MaxValue) : 0;
        var limit = q.ContainsKey("limit") ? QueryNumber(http.Request, "limit", 1, MaxLogChars) : MaxLogChars;

        var repo = await RequireRepoAsync(repos, key, slug);
        var log = await pipelines.GetLogAsync(repo, id, stepId) ?? throw ApiException.NotFound($"Step {stepId} not found in pipeline {id}.");
        var text = log.Text;
        var total = text.Length;
        var start = tail is { } t ? Math.Max(0, total - (int)t) : (int)Math.Min(offset, total);
        var end = tail != null ? total : (int)Math.Min((long)start + limit, total);
        // Never cut a surrogate pair in half.
        if (start > 0 && start < total && char.IsLowSurrogate(text[start])) start--;
        if (end < total && end > start && char.IsHighSurrogate(text[end - 1])) end--;
        var h = http.Response.Headers;
        h["X-Log-Total-Length"] = total.ToString();
        h["X-Log-Offset"] = start.ToString();
        if (end < total) h["X-Log-Next-Offset"] = end.ToString();
        h["X-Log-Truncated"] = log.Truncated ? "true" : "false";
        return Results.Text(text[start..end], "text/plain; charset=utf-8");
    }

    // ---------------------------------------------------------------- artifacts

    private static async Task<List<PipelineArtifactDto>> ListArtifacts(HttpContext http, RepositoryService repos, PipelineService pipelines, PublicBaseUrl url, string key, string slug, int id)
    {
        var repo = await RequireRepoAsync(repos, key, slug);
        await RequirePipelineAsync(pipelines, repo, id);
        var b = url.For(http.Request);
        return (await pipelines.ListArtifactsAsync(repo, id)).Select(a => ArtifactDto(a.Artifact, a.JobName, b, repo, id)).ToList();
    }

    private static async Task<PipelineArtifactDetailDto> GetArtifact(HttpContext http, RepositoryService repos, PipelineService pipelines, PublicBaseUrl url, string key, string slug, int id, int jobId)
    {
        var repo = await RequireRepoAsync(repos, key, slug);
        var a = await pipelines.GetArtifactAsync(repo, id, jobId) ?? throw ApiException.NotFound($"Job {jobId} has no artifacts (none saved, or they expired).");
        var files = await pipelines.ListArtifactFilesAsync(repo, id, jobId);
        return new PipelineArtifactDetailDto(ArtifactDto(a.Artifact, a.JobName, url.For(http.Request), repo, id), files.Select(f => new ArtifactFileDto(f.Path, f.Size)).ToList());
    }

    private static async Task<IResult> DownloadArtifact(HttpContext http, RepositoryService repos, PipelineService pipelines, string key, string slug, int id, int jobId)
    {
        var repo = await RequireRepoAsync(repos, key, slug);
        var a = await pipelines.GetArtifactAsync(repo, id, jobId) ?? throw ApiException.NotFound($"Job {jobId} has no artifacts (none saved, or they expired).");
        CiServiceCollectionExtensions.SafeDownloadHeaders(http, $"{repo.Slug}-{id}-{a.JobName}-artifacts.zip");
        return Results.File(new FileStream(a.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true),
            "application/octet-stream", enableRangeProcessing: true);
    }

    private static async Task<IResult> DownloadArtifactFile(HttpContext http, RepositoryService repos, PipelineService pipelines, string key, string slug, int id, int jobId, string? path)
    {
        if (string.IsNullOrEmpty(path)) throw ApiException.BadRequest("path is required.");
        var repo = await RequireRepoAsync(repos, key, slug);
        var a = await pipelines.GetArtifactAsync(repo, id, jobId) ?? throw ApiException.NotFound($"Job {jobId} has no artifacts (none saved, or they expired).");
        var zip = ZipFile.OpenRead(a.Path);
        var entry = zip.Entries.FirstOrDefault(e => e.FullName.Replace('\\', '/') == path && !e.FullName.EndsWith('/'));
        if (entry == null) { zip.Dispose(); throw ApiException.NotFound($"'{path}' is not in the artifacts of job {jobId}."); }
        CiServiceCollectionExtensions.SafeDownloadHeaders(http, path.Split('/')[^1]);
        http.Response.RegisterForDispose(zip);
        http.Response.ContentLength = entry.Length;
        return Results.Stream(entry.Open(), "application/octet-stream");
    }

    // ---------------------------------------------------------------- CI variables

    private static CiVariableDto VariableDto(CiVariableInfo v, string? repoSlug) =>
        new(v.Id, v.Name, v.IsProtected, true, v.RepositoryId == null ? "project" : "repository", repoSlug, Dto.Utc(v.UpdatedAt), v.UpdatedBy);

    private static async Task<List<CiVariableDto>> ListVariables(RepositoryService repos, CiVariableService vars, string key, string slug)
    {
        var repo = await RequireRepoAsync(repos, key, slug);
        return (await vars.ListAsync(repo.ProjectId, repo.Id)).Select(v => VariableDto(v, repo.Slug)).ToList();
    }

    private static async Task<List<CiVariableDto>> ListProjectVariables(IssueService issues, CiVariableService vars, string key)
    {
        var project = await IssueEndpoints.RequireProjectAsync(issues, key);
        return (await vars.ListAsync(project.Id, null)).Select(v => VariableDto(v, null)).ToList();
    }

    private static async Task<IResult> SaveVariable(HttpContext http, RepositoryService repos, CiVariableService vars, string key, string slug, int? id)
    {
        var repo = await RequireRepoAsync(repos, key, slug);
        var result = await SaveAsync(http, vars, repo.ProjectId, repo.Id, repo.Slug, id);
        return id == null ? TypedResults.Created($"/api/v1/projects/{key.ToUpperInvariant()}/repos/{slug}/ci/variables", result) : TypedResults.Ok(result);
    }

    private static async Task<IResult> SaveProjectVariable(HttpContext http, IssueService issues, CiVariableService vars, string key, int? id)
    {
        var project = await IssueEndpoints.RequireProjectAsync(issues, key);
        var result = await SaveAsync(http, vars, project.Id, null, null, id);
        return id == null ? TypedResults.Created($"/api/v1/projects/{key.ToUpperInvariant()}/ci/variables", result) : TypedResults.Ok(result);
    }

    private static async Task<CiVariableDto> SaveAsync(HttpContext http, CiVariableService vars, int projectId, int? repositoryId, string? repoSlug, int? id)
    {
        // Listing needs Maintainer+, the same permission as saving: a lower role gets 403 before anything else.
        var existing = await vars.ListAsync(projectId, repositoryId);
        var body = await ApiResults.ReadBodyAsync<CiVariableRequest>(http.Request);
        string name;
        bool isProtected;
        if (id == null)
        {
            if (string.IsNullOrWhiteSpace(body.Name)) throw ApiException.Unprocessable("name is required.");
            if (string.IsNullOrEmpty(body.Value)) throw ApiException.Unprocessable("value is required.");
            name = body.Name.Trim();
            isProtected = body.Protected ?? false;
        }
        else
        {
            var current = existing.FirstOrDefault(v => v.Id == id) ?? throw ApiException.NotFound($"CI variable {id} not found.");
            name = string.IsNullOrWhiteSpace(body.Name) ? current.Name : body.Name.Trim();
            isProtected = body.Protected ?? current.IsProtected;
        }
        await vars.SaveAsync(projectId, repositoryId, id, name, string.IsNullOrEmpty(body.Value) ? null : body.Value, isProtected);
        var saved = (await vars.ListAsync(projectId, repositoryId)).First(v => id != null ? v.Id == id : v.Name == name);
        return VariableDto(saved, repoSlug);
    }

    private static async Task<IResult> DeleteVariable(RepositoryService repos, CiVariableService vars, string key, string slug, int id)
    {
        var repo = await RequireRepoAsync(repos, key, slug);
        return await DeleteAsync(vars, repo.ProjectId, repo.Id, id);
    }

    private static async Task<IResult> DeleteProjectVariable(IssueService issues, CiVariableService vars, string key, int id)
    {
        var project = await IssueEndpoints.RequireProjectAsync(issues, key);
        return await DeleteAsync(vars, project.Id, null, id);
    }

    private static async Task<IResult> DeleteAsync(CiVariableService vars, int projectId, int? repositoryId, int id)
    {
        if ((await vars.ListAsync(projectId, repositoryId)).All(v => v.Id != id)) throw ApiException.NotFound($"CI variable {id} not found.");
        await vars.DeleteAsync(projectId, repositoryId, id);
        return TypedResults.NoContent();
    }
}
