using System.Text;
using ByteBench.Data;
using ByteBench.Services;
using ByteBench.Services.Tests;

namespace ByteBench.Api;

public record TestCaseDto(int Number, string Key, string Title, string? Preconditions, string? Steps, string? Expected, string Priority, string Type,
    IReadOnlyList<string> Tags, string? Suite, IReadOnlyList<int> LinkedIssues, string? Requirements, string? AutomatedName, int Version, bool Archived,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, string WebUrl, TestLatestDto? Latest = null);

public record TestLatestDto(string Status, int RunNumber, DateTimeOffset At);

public record TestCaseRevisionDto(int Version, string Title, string? Preconditions, string? Steps, string? Expected, string Priority, string? Suite, DateTimeOffset EditedAt, int? EditedById);

public record TestPlanDto(int Id, string Name, string? Description, int CaseCount, IReadOnlyList<int> CaseNumbers, DateTimeOffset UpdatedAt);

public record TestSummaryDto(int Passed, int Failed, int Blocked, int Skipped, int NotRun, int Total, double? PassRate, double Progress);

public record TestRunDto(int Number, string Name, string Build, string? Tag, string? Ref, string? CommitSha, int? ReleaseId, int? PipelineId, int? PlanId, string Source,
    bool Closed, DateTimeOffset CreatedAt, DateTimeOffset? ClosedAt, TestSummaryDto Summary, string WebUrl);

public record TestEvidenceDto(int Id, string FileName, string ContentType, long Size, string Sha256, string DownloadUrl);

public record TestResultDto(int CaseNumber, string CaseKey, string CaseTitle, string? Suite, string Priority, string Status, int CaseVersion, UserRefDto? ExecutedBy,
    DateTimeOffset? ExecutedAt, string? Comment, string? Error, long? DurationMs, int? IssueNumber, IReadOnlyList<TestEvidenceDto> Evidence);

public record TestRunDetailDto(TestRunDto Run, IReadOnlyList<TestResultDto> Results);

public record TestCompareRowDto(int CaseNumber, string CaseKey, string Title, string? From, string? To, string Change);

public record TestCompareDto(TestRunDto From, TestRunDto To, IReadOnlyDictionary<string, int> Counts, IReadOnlyList<TestCompareRowDto> Rows);

public record TestMatrixRunDto(int Number, string Name, string Build, string? Tag, string? CommitSha, DateTimeOffset CreatedAt);

public record TestMatrixDto(IReadOnlyList<TestMatrixRunDto> Runs, IReadOnlyList<TestMatrixRowDto> Cases);

public record TestMatrixRowDto(int CaseNumber, string CaseKey, string Title, string? Suite, IReadOnlyList<string?> Results);

public record TestIngestResultDto(int RunNumber, string Format, int Total, int Passed, int Failed, int Skipped, int CreatedCases, int Unmapped, bool Truncated, string WebUrl);

/// <summary>POST body for a case. <c>priority</c>: low, medium, high, critical. <c>type</c>: manual, automated.</summary>
public record SaveTestCaseRequest(string? Title = null, string? Preconditions = null, string? Steps = null, string? Expected = null, string? Priority = null,
    string? Type = null, IReadOnlyList<string>? Tags = null, string? Suite = null, IReadOnlyList<int>? LinkedIssues = null, string? Requirements = null,
    string? AutomatedName = null);

public record SaveTestPlanRequest(string Name, string? Description = null, IReadOnlyList<int>? CaseNumbers = null);

/// <summary>POST body for a run. Give <c>tag</c> (must exist in a repository of the project), or <c>commitSha</c>, <c>pipelineId</c> and/or just a <c>build</c> label.</summary>
public record CreateTestRunRequest(string? Name = null, int? PlanId = null, string? Build = null, string? Tag = null, string? Ref = null, string? CommitSha = null,
    string? Repository = null, int? PipelineId = null, IReadOnlyList<int>? CaseNumbers = null);

/// <summary>PUT body for a result. <c>status</c>: passed, failed, blocked, skipped, not_run.</summary>
public record RecordTestResultRequest(string Status, string? Comment = null);

/// <summary>POST body for uploading a report as JSON (a raw XML or JSON report can be posted as the body too, with these as query parameters).</summary>
public record UploadTestReportRequest(string Content, string? Format = null, int? Run = null, string? Name = null, string? Build = null, string? Tag = null,
    string? Ref = null, string? CommitSha = null, string? Repository = null, int? PipelineId = null, bool? AutoCreateCases = null);

public static class TestDtos
{
    public static string Status(TestResultStatus s) => s == TestResultStatus.NotRun ? "not_run" : s.ToString().ToLowerInvariant();

    public static bool TryStatus(string? s, out TestResultStatus status)
    {
        status = TestResultStatus.NotRun;
        var n = (s ?? "").Trim().Replace("_", "").Replace("-", "");
        return Enum.TryParse(n, ignoreCase: true, out status) && Enum.IsDefined(status);
    }

    public static TestSummaryDto Summary(TestRunSummary s) => new(s.Passed, s.Failed, s.Blocked, s.Skipped, s.NotRun, s.Total, s.PassRate, s.Progress);

    public static TestCaseDto Case(TestCase c, string key, string baseUrl, TestLatestDto? latest = null) => new(c.Number, c.Key, c.Title, c.Preconditions, c.Steps, c.Expected,
        c.Priority.ToString().ToLowerInvariant(), c.Type.ToString().ToLowerInvariant(), c.TagList.ToList(), c.Suite, c.LinkedIssueNumbers.ToList(), c.Requirements,
        c.AutomatedName, c.Version, c.IsArchived, Dto.Utc(c.CreatedAt), Dto.Utc(c.UpdatedAt), $"{baseUrl}/p/{key}/tests/cases/{c.Number}", latest);

    public static TestRunDto Run(TestRun r, TestRunSummary s, string key, string baseUrl) => new(r.Number, r.Name, r.Label, r.Tag, r.Ref, r.CommitSha, r.ReleaseId,
        r.PipelineId, r.PlanId, r.Source.ToString().ToLowerInvariant(), r.IsClosed, Dto.Utc(r.CreatedAt), Dto.Utc(r.ClosedAt), Summary(s), $"{baseUrl}/p/{key}/tests/runs/{r.Number}");

    public static bool TryPriority(string? s, out TestPriority p) { p = TestPriority.Medium; return string.IsNullOrWhiteSpace(s) || (Enum.TryParse(s.Trim(), true, out p) && Enum.IsDefined(p)); }
    public static bool TryType(string? s, out TestCaseType t) { t = TestCaseType.Manual; return string.IsNullOrWhiteSpace(s) || (Enum.TryParse(s.Trim(), true, out t) && Enum.IsDefined(t)); }
}

/// <summary>
/// Test management: <c>/projects/{key}/tests/…</c>. Reading needs project access; writing cases, starting runs, recording results and
/// uploading reports needs Developer+; plans, archiving and deleting need Maintainer+.
/// </summary>
public static class TestEndpoints
{
    public const int MaxReportBytes = TestReportParser.MaxBytes;

    public static void MapTestEndpoints(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/projects/{key}/tests").WithTags("Test management");
        g.MapGet("/cases", ListCases).WithName("ListTestCases").WithSummary("List test cases")
            .WithDescription("Filters: ?q= (title, id, automated name), ?suite= (folder, includes sub-folders), ?priority=, ?type=, ?tag=, ?archived=true. Each case carries its newest recorded result.");
        g.MapPost("/cases", CreateCase).WithName("CreateTestCase").WithSummary("Create a test case (Developer+)").WithJsonBody<SaveTestCaseRequest>().Produces<TestCaseDto>(StatusCodes.Status201Created);
        g.MapGet("/cases/{number:int}", GetCase).WithName("GetTestCase").WithSummary("Get a test case");
        g.MapPatch("/cases/{number:int}", UpdateCase).WithName("UpdateTestCase").WithSummary("Edit a test case (Developer+)")
            .WithDescription("Only the given properties change. Editing the text keeps the old text as a revision and bumps the version.").WithJsonBody<SaveTestCaseRequest>();
        g.MapDelete("/cases/{number:int}", DeleteCase).WithName("DeleteTestCase").WithSummary("Delete a test case and its results (Maintainer+)");
        g.MapPost("/cases/{number:int}/archive", (HttpContext h, TestService t, IssueService i, PublicBaseUrl u, string key, int number) => Archive(h, t, i, u, key, number, true))
            .WithName("ArchiveTestCase").WithSummary("Archive a test case (Maintainer+)");
        g.MapPost("/cases/{number:int}/unarchive", (HttpContext h, TestService t, IssueService i, PublicBaseUrl u, string key, int number) => Archive(h, t, i, u, key, number, false))
            .WithName("UnarchiveTestCase").WithSummary("Restore an archived test case (Maintainer+)");
        g.MapGet("/cases/{number:int}/revisions", Revisions).WithName("ListTestCaseRevisions").WithSummary("Earlier versions of a test case");
        g.MapGet("/cases/{number:int}/results", CaseResults).WithName("ListTestCaseResults").WithSummary("Recorded results of one case across runs, newest first");

        g.MapGet("/plans", ListPlans).WithName("ListTestPlans").WithSummary("List test plans");
        g.MapPost("/plans", CreatePlan).WithName("CreateTestPlan").WithSummary("Create a test plan (Maintainer+)").WithJsonBody<SaveTestPlanRequest>().Produces<TestPlanDto>(StatusCodes.Status201Created);
        g.MapGet("/plans/{id:int}", GetPlan).WithName("GetTestPlan").WithSummary("Get a test plan");
        g.MapPut("/plans/{id:int}", UpdatePlan).WithName("UpdateTestPlan").WithSummary("Replace a test plan's name, description and cases (Maintainer+)").WithJsonBody<SaveTestPlanRequest>();
        g.MapDelete("/plans/{id:int}", DeletePlan).WithName("DeleteTestPlan").WithSummary("Delete a test plan (Maintainer+)");

        g.MapGet("/runs", ListRuns).WithName("ListTestRuns").WithSummary("List test runs, newest first")
            .WithDescription("?tag=, ?commit= and ?releaseId= narrow the list to runs of one version.");
        g.MapPost("/runs", CreateRun).WithName("CreateTestRun").WithSummary("Start a test run (Developer+)")
            .WithDescription("Runs the plan's cases, the given caseNumbers, or all active manual cases. A tag must exist in a repository of the project; its commit and release are recorded.")
            .WithJsonBody<CreateTestRunRequest>().Produces<TestRunDto>(StatusCodes.Status201Created);
        g.MapGet("/runs/{number:int}", GetRun).WithName("GetTestRun").WithSummary("Get a run with every result");
        g.MapPost("/runs/{number:int}/close", (HttpContext h, TestService t, IssueService i, PublicBaseUrl u, string key, int number) => Close(h, t, i, u, key, number, true))
            .WithName("CloseTestRun").WithSummary("Close a run (Developer+)");
        g.MapPost("/runs/{number:int}/reopen", (HttpContext h, TestService t, IssueService i, PublicBaseUrl u, string key, int number) => Close(h, t, i, u, key, number, false))
            .WithName("ReopenTestRun").WithSummary("Reopen a closed run (Developer+)");
        g.MapDelete("/runs/{number:int}", DeleteRun).WithName("DeleteTestRun").WithSummary("Delete a run and its results (Maintainer+)");
        g.MapPut("/runs/{number:int}/results/{caseNumber:int}", RecordResult).WithName("RecordTestResult").WithSummary("Record a result (Developer+)")
            .WithJsonBody<RecordTestResultRequest>();
        g.MapPost("/runs/{number:int}/results/{caseNumber:int}/bug", CreateBug).WithName("CreateBugFromTestResult").WithSummary("File a bug for a failed result (Developer+)")
            .WithDescription("The bug is prefilled with the case, run, build and failure. Returns the existing bug when there is one.");
        g.MapPost("/runs/{number:int}/results/{caseNumber:int}/evidence", AddEvidence).WithName("AddTestEvidence").WithSummary("Attach evidence to a result (Developer+)")
            .WithDescription("The request body is the file; ?fileName= names it. Up to 25 MB, 20 per result.");
        g.MapGet("/evidence/{id:int}", DownloadEvidence).WithName("DownloadTestEvidence").WithSummary("Download evidence (always as an attachment)");
        g.MapDelete("/evidence/{id:int}", DeleteEvidence).WithName("DeleteTestEvidence").WithSummary("Remove evidence (Developer+)");

        g.MapPost("/reports", UploadReport).WithName("UploadTestReport").WithSummary("Upload an automated test report (Developer+)")
            .WithDescription("JUnit XML, TRX, xUnit v2 XML or CTRF JSON, up to 16 MB. Send JSON { content, format?, … } or the raw report as the body with ?format=&build=&tag=&commit=&pipelineId=&run=&name=&autoCreate= " +
                             "(format is detected when left out). Tests map to cases by a TC-n token in the name, else by the case's automated name; unmapped tests create automated cases unless autoCreate=false. " +
                             "Documents with a DOCTYPE are refused. Closes the run and raises test_run.completed.")
            .WithJsonBody<UploadTestReportRequest>().Produces<TestIngestResultDto>(StatusCodes.Status201Created);

        g.MapGet("/compare", Compare).WithName("CompareTestRuns").WithSummary("Compare two runs: ?from=<run>&to=<run>")
            .WithDescription("Per case: regressed (passed → failed/blocked), fixed, still failing, newly run, no longer run.");
        g.MapGet("/matrix", Matrix).WithName("GetTestMatrix").WithSummary("Cases against runs")
            .WithDescription("?runs=3,5,8 (run numbers) or ?count= latest runs (default 8); ?problems=true keeps only cases that failed somewhere.");
    }

    private static Task<Project> ProjectAsync(IssueService issues, string key) => IssueEndpoints.RequireProjectAsync(issues, key);

    private static async Task<TestRunDto> RunDtoAsync(TestService tests, Project project, int number, PublicBaseUrl url, HttpContext http)
    {
        var d = await tests.GetRunAsync(project.Id, number) ?? throw ApiException.NotFound($"Run #{number} not found.");
        return TestDtos.Run(d.Run, d.Summary, project.Key, url.For(http.Request));
    }

    private static async Task<List<TestCaseDto>> ListCases(HttpContext http, IssueService issues, TestService tests, PublicBaseUrl url, string key, string? q, string? suite,
        string? priority, string? type, string? tag, bool? archived)
    {
        var project = await ProjectAsync(issues, key);
        if (!TestDtos.TryPriority(priority, out var pr)) throw ApiException.BadRequest("priority must be low, medium, high or critical.");
        if (!TestDtos.TryType(type, out var ty)) throw ApiException.BadRequest("type must be manual or automated.");
        var list = await tests.ListCasesAsync(project.Id, new TestService.CaseFilter(q, suite, string.IsNullOrWhiteSpace(priority) ? null : pr,
            string.IsNullOrWhiteSpace(type) ? null : ty, tag, archived == true));
        var latest = await tests.LatestStatusesAsync(project.Id);
        var b = url.For(http.Request);
        return Paging.From(http.Request).Apply(http, list, c => TestDtos.Case(c, project.Key, b,
            latest.TryGetValue(c.Number, out var l) ? new TestLatestDto(TestDtos.Status(l.Status), l.RunNumber, Dto.Utc(l.At)) : null));
    }

    private static TestCaseInput ToInput(SaveTestCaseRequest r, TestCase? current)
    {
        if (!TestDtos.TryPriority(r.Priority, out var priority)) throw ApiException.BadRequest("priority must be low, medium, high or critical.");
        if (!TestDtos.TryType(r.Type, out var type)) throw ApiException.BadRequest("type must be manual or automated.");
        return new TestCaseInput
        {
            Title = r.Title ?? current?.Title ?? "", Preconditions = r.Preconditions ?? current?.Preconditions, Steps = r.Steps ?? current?.Steps, Expected = r.Expected ?? current?.Expected,
            Priority = string.IsNullOrWhiteSpace(r.Priority) ? current?.Priority ?? TestPriority.Medium : priority,
            Type = string.IsNullOrWhiteSpace(r.Type) ? current?.Type ?? TestCaseType.Manual : type,
            Tags = r.Tags != null ? string.Join(",", r.Tags) : current?.Tags, Suite = r.Suite ?? current?.Suite,
            LinkedIssues = r.LinkedIssues != null ? string.Join(",", r.LinkedIssues) : current?.LinkedIssues,
            Requirements = r.Requirements ?? current?.Requirements, AutomatedName = r.AutomatedName ?? current?.AutomatedName,
        };
    }

    private static async Task<IResult> CreateCase(HttpContext http, IssueService issues, TestService tests, PublicBaseUrl url, string key)
    {
        var project = await ProjectAsync(issues, key);
        var body = await ApiResults.ReadBodyAsync<SaveTestCaseRequest>(http.Request);
        var c = await tests.CreateCaseAsync(project.Id, ToInput(body, null));
        var dto = TestDtos.Case(c, project.Key, url.For(http.Request));
        return TypedResults.Created(dto.WebUrl, dto);
    }

    private static async Task<TestCase> CaseAsync(TestService tests, Project project, int number) =>
        await tests.GetCaseAsync(project.Id, number) ?? throw ApiException.NotFound($"Test case TC-{number} not found.");

    private static async Task<TestCaseDto> GetCase(HttpContext http, IssueService issues, TestService tests, PublicBaseUrl url, string key, int number)
    {
        var project = await ProjectAsync(issues, key);
        var c = await CaseAsync(tests, project, number);
        var latest = await tests.LatestStatusesAsync(project.Id);
        return TestDtos.Case(c, project.Key, url.For(http.Request), latest.TryGetValue(c.Number, out var l) ? new TestLatestDto(TestDtos.Status(l.Status), l.RunNumber, Dto.Utc(l.At)) : null);
    }

    private static async Task<TestCaseDto> UpdateCase(HttpContext http, IssueService issues, TestService tests, PublicBaseUrl url, string key, int number)
    {
        var project = await ProjectAsync(issues, key);
        var current = await CaseAsync(tests, project, number);
        var body = await ApiResults.ReadBodyAsync<SaveTestCaseRequest>(http.Request);
        var c = await tests.UpdateCaseAsync(project.Id, number, ToInput(body, current));
        return TestDtos.Case(c, project.Key, url.For(http.Request));
    }

    private static async Task<IResult> DeleteCase(IssueService issues, TestService tests, string key, int number)
    {
        var project = await ProjectAsync(issues, key);
        await CaseAsync(tests, project, number);
        await tests.DeleteCaseAsync(project.Id, number);
        return TypedResults.NoContent();
    }

    private static async Task<TestCaseDto> Archive(HttpContext http, TestService tests, IssueService issues, PublicBaseUrl url, string key, int number, bool archive)
    {
        var project = await ProjectAsync(issues, key);
        await CaseAsync(tests, project, number);
        await tests.SetArchivedAsync(project.Id, number, archive);
        return TestDtos.Case(await CaseAsync(tests, project, number), project.Key, url.For(http.Request));
    }

    private static async Task<List<TestCaseRevisionDto>> Revisions(IssueService issues, TestService tests, string key, int number)
    {
        var project = await ProjectAsync(issues, key);
        await CaseAsync(tests, project, number);
        return (await tests.RevisionsAsync(project.Id, number)).Select(r => new TestCaseRevisionDto(r.Version, r.Title, r.Preconditions, r.Steps, r.Expected,
            r.Priority.ToString().ToLowerInvariant(), r.Suite, Dto.Utc(r.EditedAt), r.EditedById)).ToList();
    }

    private static async Task<List<object>> CaseResults(HttpContext http, IssueService issues, TestService tests, PublicBaseUrl url, string key, int number)
    {
        var project = await ProjectAsync(issues, key);
        await CaseAsync(tests, project, number);
        var b = url.For(http.Request);
        return (await tests.CaseHistoryAsync(project.Id, number)).Select(h => (object)new
        {
            run = h.Run.Number, runName = h.Run.Name, build = h.Run.Label, tag = h.Run.Tag, commitSha = h.Run.CommitSha, status = TestDtos.Status(h.Result.Status),
            at = Dto.Utc(h.Result.ExecutedAt ?? h.Run.CreatedAt), comment = h.Result.Comment, error = h.Result.ErrorMessage, issue = h.Result.IssueNumber,
            webUrl = $"{b}/p/{project.Key}/tests/runs/{h.Run.Number}",
        }).ToList();
    }

    // ----- plans

    private static TestPlanDto PlanDto(TestPlan p) =>
        new(p.Id, p.Name, p.Description, p.Cases.Count, p.Cases.OrderBy(c => c.Order).Select(c => c.TestCase.Number).ToList(), Dto.Utc(p.UpdatedAt));

    private static async Task<List<TestPlanDto>> ListPlans(HttpContext http, IssueService issues, TestService tests, string key)
    {
        var project = await ProjectAsync(issues, key);
        var plans = await tests.ListPlansAsync(project.Id);
        return Paging.From(http.Request).Apply(http, plans, p => new TestPlanDto(p.Plan.Id, p.Plan.Name, p.Plan.Description, p.CaseCount, [], Dto.Utc(p.Plan.UpdatedAt)));
    }

    private static async Task<TestPlanDto> GetPlan(IssueService issues, TestService tests, string key, int id)
    {
        var project = await ProjectAsync(issues, key);
        return PlanDto(await tests.GetPlanAsync(project.Id, id) ?? throw ApiException.NotFound($"Plan {id} not found."));
    }

    private static async Task<IResult> CreatePlan(HttpContext http, IssueService issues, TestService tests, string key)
    {
        var project = await ProjectAsync(issues, key);
        var body = await ApiResults.ReadBodyAsync<SaveTestPlanRequest>(http.Request);
        var p = await tests.SavePlanAsync(project.Id, null, body.Name ?? "", body.Description, body.CaseNumbers ?? []);
        var dto = PlanDto(await tests.GetPlanAsync(project.Id, p.Id) ?? throw ApiException.NotFound("Plan not found."));
        return TypedResults.Created($"/api/v1/projects/{project.Key}/tests/plans/{p.Id}", dto);
    }

    private static async Task<TestPlanDto> UpdatePlan(HttpContext http, IssueService issues, TestService tests, string key, int id)
    {
        var project = await ProjectAsync(issues, key);
        if (await tests.GetPlanAsync(project.Id, id) == null) throw ApiException.NotFound($"Plan {id} not found.");
        var body = await ApiResults.ReadBodyAsync<SaveTestPlanRequest>(http.Request);
        await tests.SavePlanAsync(project.Id, id, body.Name ?? "", body.Description, body.CaseNumbers ?? []);
        return PlanDto(await tests.GetPlanAsync(project.Id, id) ?? throw ApiException.NotFound($"Plan {id} not found."));
    }

    private static async Task<IResult> DeletePlan(IssueService issues, TestService tests, string key, int id)
    {
        var project = await ProjectAsync(issues, key);
        if (await tests.GetPlanAsync(project.Id, id) == null) throw ApiException.NotFound($"Plan {id} not found.");
        await tests.DeletePlanAsync(project.Id, id);
        return TypedResults.NoContent();
    }

    // ----- runs

    private static async Task<List<TestRunDto>> ListRuns(HttpContext http, IssueService issues, TestService tests, PublicBaseUrl url, string key, string? tag, string? commit, int? releaseId)
    {
        var project = await ProjectAsync(issues, key);
        var rows = await tests.ListRunsAsync(project.Id, 500, tag, commit, releaseId);
        var b = url.For(http.Request);
        return Paging.From(http.Request).Apply(http, rows, r => TestDtos.Run(r.Run, r.Summary, project.Key, b));
    }

    private static async Task<IResult> CreateRun(HttpContext http, IssueService issues, TestService tests, PublicBaseUrl url, string key)
    {
        var project = await ProjectAsync(issues, key);
        var body = await ApiResults.ReadBodyAsync<CreateTestRunRequest>(http.Request);
        var run = await tests.CreateRunAsync(project.Id, new TestRunInput
        {
            Name = body.Name ?? "", PlanId = body.PlanId, Build = body.Build, Tag = body.Tag, Ref = body.Ref, CommitSha = body.CommitSha, Repository = body.Repository,
            PipelineId = body.PipelineId, CaseNumbers = body.CaseNumbers,
        }, TestRunSource.Api);
        var dto = await RunDtoAsync(tests, project, run.Number, url, http);
        return TypedResults.Created(dto.WebUrl, dto);
    }

    private static async Task<TestRunDetailDto> GetRun(HttpContext http, IssueService issues, TestService tests, PublicBaseUrl url, string key, int number)
    {
        var project = await ProjectAsync(issues, key);
        var d = await tests.GetRunAsync(project.Id, number) ?? throw ApiException.NotFound($"Run #{number} not found.");
        var names = await tests.UserNamesAsync(d.Results.Select(r => r.ExecutedById));
        var b = url.For(http.Request);
        return new TestRunDetailDto(TestDtos.Run(d.Run, d.Summary, project.Key, b), d.Results.Select(r => new TestResultDto(r.TestCase.Number, r.TestCase.Key, r.TestCase.Title,
            r.TestCase.Suite, r.TestCase.Priority.ToString().ToLowerInvariant(), TestDtos.Status(r.Status), r.CaseVersion,
            r.ExecutedById is { } id ? new UserRefDto(id, names.GetValueOrDefault(id)) : null, Dto.Utc(r.ExecutedAt), r.Comment, r.ErrorMessage, r.DurationMs, r.IssueNumber,
            r.Evidence.Select(e => new TestEvidenceDto(e.Id, e.FileName, e.ContentType, e.Size, e.Sha256, $"{b}/api/v1/projects/{project.Key}/tests/evidence/{e.Id}")).ToList())).ToList());
    }

    private static async Task<TestRunDto> Close(HttpContext http, TestService tests, IssueService issues, PublicBaseUrl url, string key, int number, bool close)
    {
        var project = await ProjectAsync(issues, key);
        await RunDtoAsync(tests, project, number, url, http);
        await tests.SetRunClosedAsync(project.Id, number, close);
        return await RunDtoAsync(tests, project, number, url, http);
    }

    private static async Task<IResult> DeleteRun(HttpContext http, IssueService issues, TestService tests, PublicBaseUrl url, string key, int number)
    {
        var project = await ProjectAsync(issues, key);
        await RunDtoAsync(tests, project, number, url, http);
        await tests.DeleteRunAsync(project.Id, number);
        return TypedResults.NoContent();
    }

    private static async Task<TestResultDto> RecordResult(HttpContext http, IssueService issues, TestService tests, PublicBaseUrl url, string key, int number, int caseNumber)
    {
        var project = await ProjectAsync(issues, key);
        var body = await ApiResults.ReadBodyAsync<RecordTestResultRequest>(http.Request);
        if (!TestDtos.TryStatus(body.Status, out var status)) throw ApiException.BadRequest("status must be passed, failed, blocked, skipped or not_run.");
        await RunDtoAsync(tests, project, number, url, http);
        await tests.RecordResultAsync(project.Id, number, caseNumber, status, body.Comment);
        return (await GetRun(http, issues, tests, url, key, number)).Results.First(r => r.CaseNumber == caseNumber);
    }

    private static async Task<IResult> CreateBug(HttpContext http, IssueService issues, TestService tests, PublicBaseUrl url, string key, int number, int caseNumber)
    {
        var project = await ProjectAsync(issues, key);
        await RunDtoAsync(tests, project, number, url, http);
        var issue = await tests.CreateBugAsync(project.Id, number, caseNumber);
        return TypedResults.Ok(new { issueNumber = issue, key = $"{project.Key}-{issue}", webUrl = $"{url.For(http.Request)}/p/{project.Key}/issues/{issue}" });
    }

    private static async Task<IResult> AddEvidence(HttpContext http, IssueService issues, TestService tests, PublicBaseUrl url, string key, int number, int caseNumber, string? fileName)
    {
        var project = await ProjectAsync(issues, key);
        await RunDtoAsync(tests, project, number, url, http);
        try
        {
            var e = await tests.AddEvidenceAsync(project.Id, number, caseNumber, fileName ?? "evidence", http.Request.Body, http.RequestAborted);
            return TypedResults.Created($"/api/v1/projects/{project.Key}/tests/evidence/{e.Id}",
                new TestEvidenceDto(e.Id, e.FileName, e.ContentType, e.Size, e.Sha256, $"{url.For(http.Request)}/api/v1/projects/{project.Key}/tests/evidence/{e.Id}"));
        }
        catch (Services.Attachments.AttachmentTooLargeException)
        {
            throw new ApiException(StatusCodes.Status413PayloadTooLarge, "Payload Too Large", "Evidence can be at most 25 MB.");
        }
    }

    private static async Task<IResult> DownloadEvidence(HttpContext http, IssueService issues, TestService tests, string key, int id)
    {
        var project = await ProjectAsync(issues, key);
        if (await tests.OpenEvidenceAsync(project.Id, id) is not { } found || !File.Exists(found.Path)) throw ApiException.NotFound("Evidence not found.");
        http.Response.Headers["X-Content-Type-Options"] = "nosniff";
        http.Response.Headers["Content-Security-Policy"] = "sandbox; default-src 'none'";
        return TypedResults.PhysicalFile(found.Path, "application/octet-stream", found.Evidence.FileName); // never inline: evidence is user content
    }

    private static async Task<IResult> DeleteEvidence(IssueService issues, TestService tests, string key, int id)
    {
        var project = await ProjectAsync(issues, key);
        await tests.DeleteEvidenceAsync(project.Id, id);
        return TypedResults.NoContent();
    }

    // ----- reports

    private static async Task<string> ReadCappedAsync(HttpRequest request)
    {
        if (request.ContentLength > MaxReportBytes) throw new ApiException(StatusCodes.Status413PayloadTooLarge, "Payload Too Large", "A report can be at most 16 MB.");
        using var ms = new MemoryStream();
        var buffer = new byte[81920];
        int n;
        while ((n = await request.Body.ReadAsync(buffer, request.HttpContext.RequestAborted)) > 0)
        {
            ms.Write(buffer, 0, n);
            if (ms.Length > MaxReportBytes) throw new ApiException(StatusCodes.Status413PayloadTooLarge, "Payload Too Large", "A report can be at most 16 MB.");
        }
        return new UTF8Encoding(false, false).GetString(ms.GetBuffer(), 0, (int)ms.Length);
    }

    private static async Task<IResult> UploadReport(HttpContext http, IssueService issues, TestService tests, PublicBaseUrl url, string key, string? format, string? build,
        string? tag, string? commit, int? pipelineId, int? run, string? name, string? @ref, string? repository, bool? autoCreate)
    {
        var project = await ProjectAsync(issues, key);
        TestIngestInput input;
        if (ApiResults.IsJson(http.Request) && await PeekIsUploadEnvelopeAsync(http))
        {
            var body = await ReadEnvelopeAsync(http);
            input = new TestIngestInput
            {
                Content = body.Content ?? "", Format = body.Format ?? format, RunNumber = body.Run ?? run, Name = body.Name ?? name, Build = body.Build ?? build,
                Tag = body.Tag ?? tag, Ref = body.Ref ?? @ref, CommitSha = body.CommitSha ?? commit, Repository = body.Repository ?? repository,
                PipelineId = body.PipelineId ?? pipelineId, AutoCreateCases = body.AutoCreateCases ?? autoCreate ?? true,
            };
        }
        else
        {
            input = new TestIngestInput
            {
                Content = await ReadCappedAsync(http.Request), Format = format, RunNumber = run, Name = name, Build = build, Tag = tag, Ref = @ref, CommitSha = commit,
                Repository = repository, PipelineId = pipelineId, AutoCreateCases = autoCreate ?? true,
            };
        }
        if (string.IsNullOrWhiteSpace(input.Content)) throw ApiException.BadRequest("The report is empty.");
        var r = await tests.IngestAsync(project.Id, input);
        var dto = new TestIngestResultDto(r.RunNumber, r.Format, r.Total, r.Passed, r.Failed, r.Skipped, r.Created, r.Unmatched, r.Truncated,
            $"{url.For(http.Request)}/p/{project.Key}/tests/runs/{r.RunNumber}");
        return TypedResults.Created(dto.WebUrl, dto);
    }

    // A JSON request is either our envelope ({ "content": "..." }) or a CTRF report posted as the body; buffer it once to tell.
    private static async Task<bool> PeekIsUploadEnvelopeAsync(HttpContext http)
    {
        http.Request.EnableBuffering(MaxReportBytes + 1024);
        using var ms = new MemoryStream();
        var buffer = new byte[81920];
        int n;
        while ((n = await http.Request.Body.ReadAsync(buffer, http.RequestAborted)) > 0)
        {
            ms.Write(buffer, 0, n);
            if (ms.Length > MaxReportBytes + 1024) throw new ApiException(StatusCodes.Status413PayloadTooLarge, "Payload Too Large", "A report can be at most 16 MB.");
        }
        http.Request.Body.Position = 0;
        // An envelope has a top-level string "content"; a CTRF report has "results".
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(ms.ToArray(), new System.Text.Json.JsonDocumentOptions { MaxDepth = 64 });
            return doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object && doc.RootElement.TryGetProperty("content", out var c) && c.ValueKind == System.Text.Json.JsonValueKind.String;
        }
        catch (System.Text.Json.JsonException) { return false; }
    }

    private static Task<UploadTestReportRequest> ReadEnvelopeAsync(HttpContext http) => ApiResults.ReadBodyAsync<UploadTestReportRequest>(http.Request);

    // ----- compare & matrix

    private static async Task<TestCompareDto> Compare(HttpContext http, IssueService issues, TestService tests, PublicBaseUrl url, string key, int? from, int? to)
    {
        var project = await ProjectAsync(issues, key);
        if (from == null || to == null) throw ApiException.BadRequest("Give ?from=<run number>&to=<run number>.");
        var c = await tests.CompareAsync(project.Id, from.Value, to.Value) ?? throw ApiException.NotFound("One of the runs was not found.");
        var b = url.For(http.Request);
        var sa = (await tests.GetRunAsync(project.Id, from.Value))!.Summary;
        var sb = (await tests.GetRunAsync(project.Id, to.Value))!.Summary;
        return new TestCompareDto(TestDtos.Run(c.A, sa, project.Key, b), TestDtos.Run(c.B, sb, project.Key, b),
            Enum.GetValues<TestChange>().ToDictionary(x => x.ToString().ToLowerInvariant(), x => c.Count(x)),
            c.Rows.Where(r => r.Change != TestChange.Unchanged).Select(r => new TestCompareRowDto(r.Case.Number, r.Case.Key, r.Case.Title,
                r.A is { } a ? TestDtos.Status(a) : null, r.B is { } bb ? TestDtos.Status(bb) : null, r.Change.ToString().ToLowerInvariant())).ToList());
    }

    private static async Task<TestMatrixDto> Matrix(HttpContext http, IssueService issues, TestService tests, PublicBaseUrl url, string key, string? runs, int? count, bool? problems)
    {
        var project = await ProjectAsync(issues, key);
        List<int>? numbers = null;
        if (!string.IsNullOrWhiteSpace(runs))
        {
            numbers = [];
            foreach (var part in runs.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                numbers.Add(int.TryParse(part, out var n) ? n : throw ApiException.BadRequest("runs must be a comma-separated list of run numbers."));
        }
        var m = await tests.MatrixAsync(project.Id, numbers, count ?? 8, problems == true);
        return new TestMatrixDto(m.Runs.Select(r => new TestMatrixRunDto(r.Number, r.Name, r.Label, r.Tag, r.CommitSha, Dto.Utc(r.CreatedAt))).ToList(),
            m.Cases.Select(c => new TestMatrixRowDto(c.Number, c.Key, c.Title, c.Suite,
                m.Runs.Select(r => m.Cells.TryGetValue((c.Id, r.Id), out var s) ? TestDtos.Status(s) : null).ToList())).ToList());
    }
}
