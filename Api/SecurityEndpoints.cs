using GitBench.Data;
using GitBench.Services;
using GitBench.Services.Security;

namespace GitBench.Api;

public record SecurityPackageDto(string Name, string? Version, string? Ecosystem, IReadOnlyList<string> FixedVersions);

public record SecurityTriageDto(string State, string? Reason, DateTimeOffset? Expires, bool Active, UserDto? User, string? By, DateTimeOffset At);

/// <summary>
/// A finding. <c>fingerprint</c> is Night Owl's stable id; <c>category</c> is sca, license, sast, secret, iac, container, dast
/// or quality; <c>state</c> the effective triage (new, confirmed, false-positive, accepted-risk, fixed).
/// </summary>
public record SecurityFindingDto(string Fingerprint, string Severity, string Category, string RuleId, string Title, string? Message,
    SecurityPackageDto? Package, string? File, int? Line, IReadOnlyList<string> Engines, IReadOnlyList<string> Aliases, string? HelpUrl,
    string? Context, string State, bool Open, SecurityTriageDto? Triage, int ScanId, string WebUrl);

public record SecurityScanDto(int Id, int PipelineId, int JobId, string Ref, string CommitSha, string Format, string? Tool, string? ToolVersion,
    int Critical, int High, int Medium, int Low, int Other, int Dismissed, DateTimeOffset CreatedAt);

public record SecurityTrendPointDto(int PipelineId, DateTimeOffset At, string CommitSha, int Critical, int High, int Medium, int Low, int Other, int Dismissed, int Total);

public record SecurityPullRequestDto(string? HeadSha, int? SourcePipelineId, int? TargetPipelineId, bool ScanPending,
    IReadOnlyList<SecurityFindingDto> New, IReadOnlyList<SecurityFindingDto> Fixed, int Unchanged);

/// <summary>POST body for triage. state: false-positive, accepted-risk (both need a reason), confirmed, fixed, or new (reopen).</summary>
public record TriageFindingRequest(string? State, string? Reason = null, DateTimeOffset? Expires = null);

/// <summary>/api/v1/projects/{key}/repos/{slug}/security/… and …/pull-requests/{number}/security.</summary>
public static class SecurityEndpoints
{
    public static void MapSecurityEndpoints(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/projects/{key}/repos/{slug}").WithTags("Security");
        g.MapGet("/security/findings", ListFindings).WithName("ListSecurityFindings").WithSummary("List security findings")
            .WithDescription("Findings of the latest scan of ?branch= (default: the default branch). Filters: ?state=open|dismissed|all (default open), " +
                             "?severity= (minimum: critical, high, medium, low, info), ?category= (sca, license, sast, secret, iac, container, dast, quality), ?q= (text).");
        g.MapGet("/security/findings/{fingerprint}", GetFinding).WithName("GetSecurityFinding").WithSummary("Get a security finding by fingerprint");
        g.MapPost("/security/findings/{fingerprint}/triage", Triage).WithName("TriageSecurityFinding").WithSummary("Triage a security finding")
            .WithDescription("Needs Developer+. Keyed by fingerprint, so the decision sticks across scans. state 'new' reopens it.")
            .WithJsonBody<TriageFindingRequest>();
        g.MapGet("/security/scans", ListScans).WithName("ListSecurityScans").WithSummary("List security scans").WithDescription("Newest first; ?branch= filters.");
        g.MapGet("/security/trend", Trend).WithName("GetSecurityTrend").WithSummary("Open findings by severity per pipeline")
            .WithDescription("Oldest first, up to ?take= (default 30) pipelines of ?branch= (default: the default branch).");
        g.MapGet("/security/triage", ExportTriage).WithName("ExportSecurityTriage").WithSummary("Triage decisions as Night Owl's .nightowl/triage.json");
        g.MapPost("/security/triage", ImportTriage).WithName("ImportSecurityTriage").WithSummary("Import a Night Owl triage file")
            .WithDescription("Needs Maintainer+. The body is the JSON array of .nightowl/triage.json; each decision replaces GitBench's for that fingerprint.")
            .WithJsonBody<List<NightOwlTriageFile.Decision>>();
        g.MapGet("/pull-requests/{number:int}/security", PullRequest).WithName("GetPullRequestSecurity")
            .WithSummary("New and fixed security findings of a pull request").WithDescription("The head's latest scan compared with the target branch's latest scan.");
    }

    private static async Task<Repository> RequireRepoAsync(RepositoryService repos, SecurityService security, string key, string slug)
    {
        var repo = await repos.GetRepositoryAsync(key, slug);
        if (repo == null || !await security.CanViewAsync(repo)) throw ApiException.NotFound($"Repository '{key.ToUpperInvariant()}/{slug}' not found.");
        return repo;
    }

    public static SecurityFindingDto Finding(Repository repo, SecurityFindingView v, string baseUrl)
    {
        var f = v.Finding;
        static IReadOnlyList<string> Split(string? s) => s == null ? [] : s.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var t = v.Triage;
        return new(f.Fingerprint, f.Severity.ToString().ToLowerInvariant(), SecurityLabels.CategoryKey(f.Category), f.RuleId, f.Title, f.Message,
            f.PackageName == null ? null : new SecurityPackageDto(f.PackageName, f.PackageVersion, f.Ecosystem, Split(f.FixedVersions)),
            f.FilePath, f.Line, Split(f.Engines), Split(f.Aliases), f.HelpUrl, f.Context, SecurityReportParser.TriageStateName(v.State), v.IsOpen,
            t == null ? null : new SecurityTriageDto(SecurityReportParser.TriageStateName(t.State), t.Reason, Dto.Utc(t.ExpiresAt), t.IsActive(DateTime.UtcNow),
                t.User == null ? null : Dto.User(t.User), t.By, Dto.Utc(t.At)),
            f.ScanId, baseUrl + SecurityUrls.Finding(repo, f.Fingerprint));
    }

    private static SecurityScanDto Scan(SecurityScan s) => new(s.Id, s.PipelineId, s.JobId, s.Ref, s.CommitSha, s.Format, s.ToolName, s.ToolVersion,
        s.Critical, s.High, s.Medium, s.Low, s.Other, s.Dismissed, Dto.Utc(s.CreatedAt));

    private static async Task<List<SecurityFindingDto>> ListFindings(HttpContext http, RepositoryService repos, SecurityService security, PublicBaseUrl url,
        string key, string slug, string? branch = null, string? state = null, string? severity = null, string? category = null, string? q = null)
    {
        var paging = Paging.From(http.Request);
        var repo = await RequireRepoAsync(repos, security, key, slug);
        SecuritySeverity? min = null;
        if (!string.IsNullOrEmpty(severity))
        {
            min = SecurityReportParser.ParseSeverity(severity);
            if (min == SecuritySeverity.Unknown) throw ApiException.BadRequest("severity must be critical, high, medium, low or info.");
        }
        SecurityCategory? cat = null;
        if (!string.IsNullOrEmpty(category))
            cat = SecurityLabels.ParseCategory(category) ?? throw ApiException.BadRequest("category must be sca, license, sast, secret, iac, container, dast or quality.");
        if (state is not (null or "open" or "dismissed" or "all")) throw ApiException.BadRequest("state must be open, dismissed or all.");
        var (_, list) = await security.ListAsync(repo, new SecurityFindingFilter(branch, min, cat, state ?? "open", q));
        var baseUrl = url.For(http.Request);
        return paging.Apply(http, list, v => Finding(repo, v, baseUrl));
    }

    private static async Task<SecurityFindingDto> GetFinding(HttpContext http, RepositoryService repos, SecurityService security, PublicBaseUrl url,
        string key, string slug, string fingerprint, string? branch = null)
    {
        var repo = await RequireRepoAsync(repos, security, key, slug);
        var detail = await security.GetAsync(repo, fingerprint, branch) ?? throw ApiException.NotFound($"Finding {fingerprint} not found.");
        return Finding(repo, detail.View, url.For(http.Request));
    }

    private static async Task<SecurityFindingDto> Triage(HttpContext http, RepositoryService repos, SecurityService security, PublicBaseUrl url, ApiCaller caller,
        string key, string slug, string fingerprint)
    {
        var repo = await RequireRepoAsync(repos, security, key, slug);
        var body = await ApiResults.ReadBodyAsync<TriageFindingRequest>(http.Request);
        var state = SecurityReportParser.ParseTriageState(body.State) is { } s && !string.IsNullOrWhiteSpace(body.State) ? s
            : throw ApiException.Unprocessable("state must be false-positive, accepted-risk, confirmed, fixed or new.");
        await security.TriageAsync(repo, fingerprint, state, body.Reason, body.Expires?.UtcDateTime, caller.Id);
        var detail = await security.GetAsync(repo, fingerprint) ?? throw ApiException.NotFound($"Finding {fingerprint} not found.");
        return Finding(repo, detail.View, url.For(http.Request));
    }

    private static async Task<List<SecurityScanDto>> ListScans(HttpContext http, RepositoryService repos, SecurityService security, string key, string slug, string? branch = null)
    {
        var paging = Paging.From(http.Request);
        var repo = await RequireRepoAsync(repos, security, key, slug);
        return paging.Apply(http, await security.ScansAsync(repo, branch, 200), Scan);
    }

    private static async Task<List<SecurityTrendPointDto>> Trend(RepositoryService repos, SecurityService security, string key, string slug, string? branch = null, int take = 30)
    {
        var repo = await RequireRepoAsync(repos, security, key, slug);
        return (await security.TrendAsync(repo, branch, take))
            .Select(p => new SecurityTrendPointDto(p.PipelineId, Dto.Utc(p.At), p.CommitSha, p.Critical, p.High, p.Medium, p.Low, p.Other, p.Dismissed, p.Total)).ToList();
    }

    private static async Task<IResult> ExportTriage(RepositoryService repos, SecurityService security, string key, string slug)
    {
        var repo = await RequireRepoAsync(repos, security, key, slug);
        return Results.Content(await security.ExportTriageAsync(repo), "application/json");
    }

    private static async Task<object> ImportTriage(HttpContext http, RepositoryService repos, SecurityService security, ApiCaller caller, string key, string slug)
    {
        var repo = await RequireRepoAsync(repos, security, key, slug);
        if (!ApiResults.IsJson(http.Request))
            throw new ApiException(StatusCodes.Status415UnsupportedMediaType, "Unsupported Media Type", "Send the request body as application/json.");
        using var reader = new StreamReader(http.Request.Body);
        var json = await reader.ReadToEndAsync(http.RequestAborted);
        var applied = await security.ImportTriageAsync(repo, json, caller.Id, "api");
        return new { applied };
    }

    private static async Task<SecurityPullRequestDto> PullRequest(HttpContext http, RepositoryService repos, SecurityService security, PullRequestService prs,
        PublicBaseUrl url, string key, string slug, int number)
    {
        var repo = await RequireRepoAsync(repos, security, key, slug);
        var pr = await prs.GetAsync(key, slug, number) ?? throw ApiException.NotFound($"Pull request {slug}#{number} not found.");
        var diff = await security.PullRequestAsync(pr) ?? throw ApiException.NotFound($"Pull request {slug}#{number} not found.");
        var baseUrl = url.For(http.Request);
        return new(diff.HeadSha, diff.Source?.PipelineId, diff.Target?.PipelineId, diff.ScanPending,
            diff.New.Select(v => Finding(repo, v, baseUrl)).ToList(), diff.Fixed.Select(v => Finding(repo, v, baseUrl)).ToList(), diff.Unchanged);
    }
}
