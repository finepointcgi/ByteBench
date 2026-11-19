using ByteBench.Data;
using ByteBench.Services;
using ByteBench.Services.Ci;
using ByteBench.Services.Git;
using ByteBench.Services.Releases;

namespace ByteBench.Api;

public record DeploymentDto(int Id, int EnvironmentId, string? EnvironmentName, string Status, string Action, string Ref, string CommitSha,
    bool IsRollback, int? RedeployOfId, int PipelineId, int JobId, string? JobName, UserDto? User, DateTimeOffset CreatedAt,
    DateTimeOffset? FinishedAt, string PipelineUrl);

public record EnvironmentApproverDto(UserDto? User, int? GroupId, string? GroupPath);

public record EnvironmentDto(int Id, string Name, string Repository, string? ExternalUrl, bool IsProtected, int RequiredApprovals,
    IReadOnlyList<EnvironmentApproverDto> Approvers, string State, DeploymentDto? Current, DeploymentDto? Latest, int DeploymentCount, string WebUrl);

public record DeploymentApprovalDto(UserDto User, string Decision, string? Comment, DateTimeOffset CreatedAt);

public record DeploymentCommitDto(string Sha, string Title, string AuthorName, DateTimeOffset When);

/// <summary>A deployment with its approvals and the commits since the previous successful deployment of its environment.</summary>
public record DeploymentDetailDto(DeploymentDto Deployment, int RequiredApprovals, IReadOnlyList<DeploymentApprovalDto> Approvals,
    int? PreviousDeploymentId, IReadOnlyList<DeploymentCommitDto> Commits, bool CommitsTruncated, bool Backwards);

public record EnvironmentTraceDto(string Repository, int EnvironmentId, string Environment, string? ExternalUrl, DeploymentDto Current,
    DeploymentDto? FirstDeployment, int IncludedCommits, int TotalCommits);

public record DeployFreezeDto(int Id, string? Description, string? CronStart, string? CronEnd, DateTimeOffset? StartsAt, DateTimeOffset? EndsAt, bool Active);

/// <summary>POST body for approving or rejecting a deployment.</summary>
public record DeploymentDecisionRequest(string? Comment = null);

/// <summary>
/// PUT body for an environment's approval rule. Empty <c>userIds</c> and <c>groupIds</c>: Maintainers approve. The rule only
/// applies to a protected environment, so <c>isProtected</c> (optional; omitted leaves it as is) protects or unprotects it in the same call.
/// </summary>
public record ApprovalRulesRequest(int RequiredApprovals = 1, IReadOnlyList<int>? UserIds = null, IReadOnlyList<int>? GroupIds = null, bool? IsProtected = null);

/// <summary>POST body for a deploy freeze: two cron expressions (UTC) or a date range.</summary>
public record CreateDeployFreezeRequest(string? Description = null, string? CronStart = null, string? CronEnd = null,
    DateTimeOffset? StartsAt = null, DateTimeOffset? EndsAt = null);

public static class EnvironmentDtos
{
    public static DeploymentDto Deployment(Repository repo, Deployment d, string baseUrl, string? envName = null) => new(
        d.Id, d.EnvironmentId, envName ?? (d.Environment as CiEnvironment)?.Name, CiDeploymentRecorder.StatusName(d.Status),
        d.Action == DeploymentAction.Stop ? "stop" : "start", d.Ref, d.CommitSha, d.IsRollback, d.RedeployOfId, d.PipelineId, d.JobId,
        (d.Job as PipelineJob)?.Name, Dto.UserOrNull(d.User), Dto.Utc(d.CreatedAt), Dto.Utc(d.FinishedAt), baseUrl + PipelineUrls.Detail(repo, d.PipelineId));

    public static EnvironmentDto Environment(Repository repo, CiEnvironmentInfo i, string baseUrl)
    {
        var e = i.Environment;
        return new(e.Id, e.Name, repo.Slug, e.ExternalUrl, e.IsProtected, DeploymentApprovalRules.Required(e),
            e.Approvers.Select(a => new EnvironmentApproverDto(Dto.UserOrNull(a.User), a.GroupId, a.Group?.FullPath)).ToList(),
            i.IsStopped ? "stopped" : "available",
            i.Current is { } c ? Deployment(repo, c, baseUrl, e.Name) : null, i.Latest is { } l ? Deployment(repo, l, baseUrl, e.Name) : null,
            i.Count, baseUrl + PipelineUrls.Environments(repo));
    }

    public static EnvironmentTraceDto Trace(EnvironmentTrace t, string baseUrl) => new(
        t.Repository.Slug, t.Environment.Id, t.Environment.Name, t.Environment.ExternalUrl,
        Deployment(t.Repository, t.Current, baseUrl, t.Environment.Name),
        t.FirstDeployment is { } f ? Deployment(t.Repository, f, baseUrl, t.Environment.Name) : null, t.Included, t.Total);

    public static DeployFreezeDto Freeze(DeployFreezeWindow w) => new(w.Id, w.Description, w.CronStart, w.CronEnd,
        Dto.Utc(w.StartsAt), Dto.Utc(w.EndsAt), DeployFreezeService.IsActive(w, DateTime.UtcNow));
}

/// <summary>Environments, deployments, approvals, rollbacks, traceability and deploy freezes.</summary>
public static class EnvironmentEndpoints
{
    public static void MapEnvironmentEndpoints(this RouteGroupBuilder api)
    {
        var p = api.MapGroup("/projects/{key}").WithTags("Environments");
        p.MapGet("/environments", ListProjectEnvironments).WithName("ListProjectEnvironments").WithSummary("Environments of every repository in the project");
        p.MapGet("/deployments/waiting", ListWaiting).WithName("ListWaitingDeployments").WithSummary("Deployments waiting for approval");
        p.MapGet("/issues/{number:int}/deployments", IssueDeployments).WithName("GetIssueDeployments")
            .WithSummary("Environments an issue's commits are deployed to")
            .WithDescription("Commits mentioning the issue key, and merge commits of merged pull requests whose title or branch mentions it.");
        p.MapGet("/deploy-freezes", ListFreezes).WithName("ListDeployFreezes").WithSummary("Deploy freeze windows");
        p.MapPost("/deploy-freezes", CreateFreeze).WithName("CreateDeployFreeze").WithSummary("Add a deploy freeze window")
            .WithDescription("Needs Maintainer+. Either cronStart and cronEnd (UTC), or startsAt and/or endsAt. While active, deploys to protected environments are blocked.")
            .WithJsonBody<CreateDeployFreezeRequest>().Produces<DeployFreezeDto>(StatusCodes.Status201Created);
        p.MapDelete("/deploy-freezes/{id:int}", DeleteFreeze).WithName("DeleteDeployFreeze").WithSummary("Remove a deploy freeze window");

        var g = api.MapGroup("/projects/{key}/repos/{slug}").WithTags("Environments");
        g.MapGet("/environments", ListEnvironments).WithName("ListEnvironments").WithSummary("List a repository's environments");
        g.MapGet("/environments/{id:int}", GetEnvironment).WithName("GetEnvironment").WithSummary("Get an environment");
        g.MapPut("/environments/{id:int}/approval-rules", SetApprovalRules).WithName("SetEnvironmentApprovalRules")
            .WithSummary("Set how many approvals deploys need and who gives them").WithDescription("Needs Maintainer+.")
            .WithJsonBody<ApprovalRulesRequest>();
        g.MapPost("/environments/{id:int}/stop", StopEnvironment).WithName("StopEnvironment")
            .WithSummary("Stop an environment").WithDescription("Runs the stop job (environment action: stop) of the pipeline that deployed it; without one, a Maintainer marks it stopped.");
        g.MapGet("/environments/{id:int}/deployments", ListDeployments).WithName("ListDeployments").WithSummary("An environment's deployments, newest first");
        g.MapGet("/deployments/{id:int}", GetDeployment).WithName("GetDeployment").WithSummary("A deployment with approvals and the commits it shipped");
        g.MapPost("/deployments/{id:int}/approve", Approve).WithName("ApproveDeployment").WithSummary("Approve a deployment waiting for approval")
            .WithJsonBody<DeploymentDecisionRequest>(optional: true);
        g.MapPost("/deployments/{id:int}/reject", Reject).WithName("RejectDeployment").WithSummary("Reject a deployment waiting for approval")
            .WithJsonBody<DeploymentDecisionRequest>(optional: true);
        g.MapPost("/deployments/{id:int}/rollback", Rollback).WithName("RollbackDeployment")
            .WithSummary("Redeploy a previous successful deployment").WithDescription("Re-runs its job. Protected environments need approval again.")
            .Produces(StatusCodes.Status202Accepted);
        g.MapGet("/deployed", Deployed).WithName("GetDeployedEnvironments").WithSummary("Environments a commit, branch or tag (rev) is deployed to");
        g.MapGet("/pull-requests/{number:int}/deployments", PullRequestDeployments).WithName("GetPullRequestDeployments")
            .WithSummary("Environments a merged pull request is deployed to");
        g.MapGet("/releases/{id:int}/deployments", ReleaseDeployments).WithName("GetReleaseDeployments")
            .WithSummary("Environments a release's tag is deployed to");
    }

    private static async Task<Repository> RequireRepoAsync(RepositoryService repos, string key, string slug) =>
        await repos.GetRepositoryAsync(key, slug) ?? throw ApiException.NotFound($"Repository '{key.ToUpperInvariant()}/{slug}' not found.");

    private static async Task<List<EnvironmentDto>> ListProjectEnvironments(HttpContext http, IssueService issues, CiEnvironmentService envs, PublicBaseUrl url, string key)
    {
        var project = await IssueEndpoints.RequireProjectAsync(issues, key);
        var b = url.For(http.Request);
        return (await envs.ListProjectAsync(project.Id)).SelectMany(r => r.Environments.Select(i => EnvironmentDtos.Environment(r.Repository, i, b))).ToList();
    }

    private static async Task<List<DeploymentDetailDto>> ListWaiting(HttpContext http, IssueService issues, CiEnvironmentService envs, PublicBaseUrl url, string key)
    {
        var project = await IssueEndpoints.RequireProjectAsync(issues, key);
        var b = url.For(http.Request);
        return (await envs.WaitingAsync(project.Id)).Select(w => new DeploymentDetailDto(
            EnvironmentDtos.Deployment(w.Repository, w.Deployment, b), w.Required, w.Approvals.Select(Approval).ToList(), null, [], false, false)).ToList();
    }

    private static async Task<List<EnvironmentTraceDto>> IssueDeployments(HttpContext http, IssueService issues, DeploymentTraceService trace, PublicBaseUrl url, string key, int number)
    {
        var issue = await issues.GetIssueAsync(key, number) ?? throw ApiException.NotFound($"Issue {key.ToUpperInvariant()}-{number} not found.");
        var b = url.For(http.Request);
        return (await trace.ForIssueAsync(issue.ProjectId, key.ToUpperInvariant(), number)).Select(t => EnvironmentDtos.Trace(t, b)).ToList();
    }

    private static async Task<List<DeployFreezeDto>> ListFreezes(IssueService issues, DeployFreezeService freezes, string key)
    {
        var project = await IssueEndpoints.RequireProjectAsync(issues, key);
        return (await freezes.ListAsync(project.Id)).Select(EnvironmentDtos.Freeze).ToList();
    }

    private static async Task<IResult> CreateFreeze(HttpContext http, IssueService issues, DeployFreezeService freezes, string key)
    {
        var project = await IssueEndpoints.RequireProjectAsync(issues, key);
        var body = await ApiResults.ReadBodyAsync<CreateDeployFreezeRequest>(http.Request);
        var w = await freezes.CreateAsync(project.Id, body.Description, body.CronStart, body.CronEnd, body.StartsAt?.UtcDateTime, body.EndsAt?.UtcDateTime);
        return Results.Json(EnvironmentDtos.Freeze(w), statusCode: StatusCodes.Status201Created);
    }

    private static async Task<IResult> DeleteFreeze(IssueService issues, DeployFreezeService freezes, string key, int id)
    {
        var project = await IssueEndpoints.RequireProjectAsync(issues, key);
        await freezes.DeleteAsync(project.Id, id);
        return Results.NoContent();
    }

    private static async Task<List<EnvironmentDto>> ListEnvironments(HttpContext http, RepositoryService repos, CiEnvironmentService envs, PublicBaseUrl url, string key, string slug)
    {
        var repo = await RequireRepoAsync(repos, key, slug);
        var b = url.For(http.Request);
        return (await envs.ListAsync(repo)).Select(i => EnvironmentDtos.Environment(repo, i, b)).ToList();
    }

    private static async Task<EnvironmentDto> GetEnvironment(HttpContext http, RepositoryService repos, CiEnvironmentService envs, PublicBaseUrl url, string key, string slug, int id)
    {
        var repo = await RequireRepoAsync(repos, key, slug);
        var info = (await envs.ListAsync(repo)).FirstOrDefault(i => i.Environment.Id == id) ?? throw ApiException.NotFound($"Environment {id} not found.");
        return EnvironmentDtos.Environment(repo, info, url.For(http.Request));
    }

    private static async Task<EnvironmentDto> SetApprovalRules(HttpContext http, RepositoryService repos, CiEnvironmentService envs, PublicBaseUrl url, string key, string slug, int id)
    {
        var repo = await RequireRepoAsync(repos, key, slug);
        var body = await ApiResults.ReadBodyAsync<ApprovalRulesRequest>(http.Request);
        await envs.SetApprovalRulesAsync(repo, id, body.RequiredApprovals, body.UserIds ?? [], body.GroupIds ?? []);
        if (body.IsProtected is { } protect) await envs.SetProtectedAsync(repo, id, protect);
        return await GetEnvironment(http, repos, envs, url, key, slug, id);
    }

    private static async Task<IResult> StopEnvironment(RepositoryService repos, CiEnvironmentService envs, string key, string slug, int id)
    {
        var repo = await RequireRepoAsync(repos, key, slug);
        var started = await envs.StopAsync(repo, id);
        return Results.Json(new { stopJobStarted = started }, statusCode: StatusCodes.Status202Accepted);
    }

    private static async Task<List<DeploymentDto>> ListDeployments(HttpContext http, RepositoryService repos, CiEnvironmentService envs, PublicBaseUrl url, string key, string slug, int id)
    {
        var paging = Paging.From(http.Request);
        var repo = await RequireRepoAsync(repos, key, slug);
        var env = await envs.GetEnvironmentAsync(repo, id) ?? throw ApiException.NotFound($"Environment {id} not found.");
        var b = url.For(http.Request);
        var rows = await envs.HistoryAsync(repo, id, take: 500);
        return paging.Apply(http, rows, d => EnvironmentDtos.Deployment(repo, d, b, env.Name));
    }

    private static async Task<DeploymentDetailDto> GetDeployment(HttpContext http, RepositoryService repos, CiEnvironmentService envs, PublicBaseUrl url, string key, string slug, int id)
    {
        var repo = await RequireRepoAsync(repos, key, slug);
        var d = await envs.GetDeploymentAsync(repo, id) ?? throw ApiException.NotFound($"Deployment {id} not found.");
        var env = await envs.GetEnvironmentAsync(repo, d.EnvironmentId);
        var approvals = await envs.ApprovalsAsync(repo, d);
        var changes = await envs.ChangesAsync(repo, id);
        return new DeploymentDetailDto(EnvironmentDtos.Deployment(repo, d, url.For(http.Request)), DeploymentApprovalRules.Required(env),
            approvals.Select(Approval).ToList(), changes?.Previous?.Id,
            changes?.Commits.Select(c => new DeploymentCommitDto(c.Sha, c.MessageShort, c.AuthorName, c.When.ToUniversalTime())).ToList() ?? [],
            changes?.Truncated ?? false, changes?.Backwards ?? false);
    }

    private static DeploymentApprovalDto Approval(DeploymentApproval a) =>
        new(Dto.User(a.User), a.Approved ? "approved" : "rejected", a.Comment, Dto.Utc(a.CreatedAt));

    private static async Task<DeploymentDecisionRequest> DecisionAsync(HttpContext http) =>
        http.Request.ContentLength is > 0 || ApiResults.IsJson(http.Request) && http.Request.ContentLength != 0
            ? await ApiResults.ReadBodyAsync<DeploymentDecisionRequest>(http.Request)
            : new DeploymentDecisionRequest();

    private static async Task<Deployment> RequireWaitingAsync(CiEnvironmentService envs, Repository repo, int id)
    {
        var d = await envs.GetDeploymentAsync(repo, id) ?? throw ApiException.NotFound($"Deployment {id} not found.");
        if (d.Status != PipelineStatus.Manual) throw ApiException.Unprocessable("This deployment isn't waiting for approval.");
        return d;
    }

    private static async Task<DeploymentDetailDto> Approve(HttpContext http, RepositoryService repos, CiEnvironmentService envs, PipelineService pipelines,
        PublicBaseUrl url, string key, string slug, int id)
    {
        var repo = await RequireRepoAsync(repos, key, slug);
        var body = await DecisionAsync(http);
        var d = await RequireWaitingAsync(envs, repo, id);
        await pipelines.ApproveJobAsync(repo, d.PipelineId, d.JobId, body.Comment);
        return await GetDeployment(http, repos, envs, url, key, slug, id);
    }

    private static async Task<DeploymentDetailDto> Reject(HttpContext http, RepositoryService repos, CiEnvironmentService envs, PipelineService pipelines,
        PublicBaseUrl url, string key, string slug, int id)
    {
        var repo = await RequireRepoAsync(repos, key, slug);
        var body = await DecisionAsync(http);
        var d = await RequireWaitingAsync(envs, repo, id);
        await pipelines.RejectJobAsync(repo, d.PipelineId, d.JobId, body.Comment);
        return await GetDeployment(http, repos, envs, url, key, slug, id);
    }

    private static async Task<IResult> Rollback(RepositoryService repos, CiEnvironmentService envs, string key, string slug, int id)
    {
        var repo = await RequireRepoAsync(repos, key, slug);
        await envs.RollbackAsync(repo, id);
        return Results.Json(new { queued = true }, statusCode: StatusCodes.Status202Accepted);
    }

    private static async Task<List<EnvironmentTraceDto>> Deployed(HttpContext http, RepositoryService repos, DeploymentTraceService trace, PublicBaseUrl url,
        string key, string slug, string rev)
    {
        var repo = await RequireRepoAsync(repos, key, slug);
        if (string.IsNullOrWhiteSpace(rev)) throw ApiException.BadRequest("rev is required.");
        var b = url.For(http.Request);
        return (await trace.ForRevisionsAsync(repo, [rev.Trim()])).Select(t => EnvironmentDtos.Trace(t, b)).ToList();
    }

    private static async Task<List<EnvironmentTraceDto>> PullRequestDeployments(HttpContext http, PullRequestService prs, DeploymentTraceService trace,
        PublicBaseUrl url, string key, string slug, int number)
    {
        var pr = await prs.GetAsync(key, slug, number) ?? throw ApiException.NotFound($"Pull request #{number} not found.");
        var b = url.For(http.Request);
        return (await trace.ForPullRequestAsync(pr.Repository, pr)).Select(t => EnvironmentDtos.Trace(t, b)).ToList();
    }

    private static async Task<List<EnvironmentTraceDto>> ReleaseDeployments(HttpContext http, RepositoryService repos, ReleaseService releases,
        DeploymentTraceService trace, PublicBaseUrl url, string key, string slug, int id)
    {
        var repo = await RequireRepoAsync(repos, key, slug);
        var r = await releases.GetAsync(repo, id) ?? throw ApiException.NotFound($"Release {id} not found.");
        var b = url.For(http.Request);
        return (await trace.ForTagAsync(repo, r.TagName)).Select(t => EnvironmentDtos.Trace(t, b)).ToList();
    }
}
