using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using ByteBench.Data;
using ByteBench.Services;
using ByteBench.Services.Releases;
using ByteBench.Services.Tests;
using ByteBench.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace ByteBench.Tests;

/// <summary>Test cases, plans, runs and results: permissions, versioning, runs against tags, bugs from failures, automated ingestion, compare, REST.</summary>
public class TestManagementTests(TestApp app) : IClassFixture<TestApp>
{
    private sealed record Setup(Project Project, Repository Repo, string Dev, string Maintainer, string Viewer, string Outsider);

    private async Task<Setup> SetupAsync()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        string dev = "tdev" + suffix, maint = "tmaint" + suffix, viewer = "tview" + suffix, outsider = "tout" + suffix;
        foreach (var u in new[] { dev, maint, viewer, outsider }) await app.CreateUserAsync(u);
        var project = await app.CreateProjectAsync("alice", ProjectVisibility.Private,
            (dev, ProjectRole.Developer), (maint, ProjectRole.Maintainer), (viewer, ProjectRole.Viewer));
        var repo = await app.CreateRepoAsync(project.Id, "alice");
        return new Setup(project, repo, dev, maint, viewer, outsider);
    }

    private static TestCaseInput Case(string title, string? name = null, TestCaseType type = TestCaseType.Manual) =>
        new() { Title = title, Steps = "1. open\n2. click", Expected = "it works", Type = type, AutomatedName = name, Tags = "Smoke, UI", Suite = "Checkout/Cart" };

    private async Task<TestCase> NewCaseAsync(Setup t, string title, string? name = null, TestCaseType type = TestCaseType.Manual)
    {
        await using var s = app.Scope(app.UserId(t.Dev));
        return await s.Get<TestService>().CreateCaseAsync(t.Project.Id, Case(title, name, type));
    }

    // ---------------------------------------------------------------- permissions

    [Fact]
    public async Task Viewers_read_developers_write_maintainers_manage_outsiders_see_nothing()
    {
        var t = await SetupAsync();
        var c = await NewCaseAsync(t, "Login works");

        await using (var v = app.Scope(app.UserId(t.Viewer)))
        {
            var svc = v.Get<TestService>();
            Assert.Single(await svc.ListCasesAsync(t.Project.Id));
            await Assert.ThrowsAsync<ForbiddenException>(() => svc.CreateCaseAsync(t.Project.Id, Case("nope")));
            await Assert.ThrowsAsync<ForbiddenException>(() => svc.CreateRunAsync(t.Project.Id, new TestRunInput { Name = "r" }));
            await Assert.ThrowsAsync<ForbiddenException>(() => svc.IngestAsync(t.Project.Id, new TestIngestInput { Content = "<testsuite/>" }));
        }
        await using (var d = app.Scope(app.UserId(t.Dev)))
        {
            var svc = d.Get<TestService>();
            await Assert.ThrowsAsync<ForbiddenException>(() => svc.SavePlanAsync(t.Project.Id, null, "Plan", null, [c.Number]));
            await Assert.ThrowsAsync<ForbiddenException>(() => svc.DeleteCaseAsync(t.Project.Id, c.Number));
            await Assert.ThrowsAsync<ForbiddenException>(() => svc.SetArchivedAsync(t.Project.Id, c.Number, true));
        }
        await using (var m = app.Scope(app.UserId(t.Maintainer)))
        {
            var svc = m.Get<TestService>();
            var plan = await svc.SavePlanAsync(t.Project.Id, null, "Smoke", null, [c.Number]);
            Assert.Single((await svc.GetPlanAsync(t.Project.Id, plan.Id))!.Cases);
            await svc.SetArchivedAsync(t.Project.Id, c.Number, true);
            Assert.Empty(await svc.ListCasesAsync(t.Project.Id));
        }
        await using (var o = app.Scope(app.UserId(t.Outsider)))
            await Assert.ThrowsAsync<ForbiddenException>(() => o.Get<TestService>().ListCasesAsync(t.Project.Id));
    }

    // ---------------------------------------------------------------- cases

    [Fact]
    public async Task Editing_the_text_keeps_a_revision_and_bumps_the_version_but_other_edits_do_not()
    {
        var t = await SetupAsync();
        var c = await NewCaseAsync(t, "Original");
        await using var s = app.Scope(app.UserId(t.Dev));
        var svc = s.Get<TestService>();
        var input = Case("Original");
        input.Requirements = "REQ-1"; // not part of the versioned text
        var same = await svc.UpdateCaseAsync(t.Project.Id, c.Number, input);
        Assert.Equal(1, same.Version);

        input.Title = "Renamed";
        input.Steps = "new steps";
        var edited = await svc.UpdateCaseAsync(t.Project.Id, c.Number, input);
        Assert.Equal(2, edited.Version);
        var revisions = await svc.RevisionsAsync(t.Project.Id, c.Number);
        var old = Assert.Single(revisions);
        Assert.Equal(("Original", 1), (old.Title, old.Version));
        Assert.Contains("click", old.Steps);
        Assert.Equal("smoke,ui", edited.Tags);
        Assert.Equal("Checkout/Cart", edited.Suite);
    }

    [Fact]
    public async Task Case_input_is_validated()
    {
        var t = await SetupAsync();
        await using var s = app.Scope(app.UserId(t.Dev));
        var svc = s.Get<TestService>();
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.CreateCaseAsync(t.Project.Id, Case("  ")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.CreateCaseAsync(t.Project.Id, Case(new string('x', 201))));
        var bad = Case("ok");
        bad.LinkedIssues = "abc";
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.CreateCaseAsync(t.Project.Id, bad));
        var good = Case("ok");
        good.LinkedIssues = $"{t.Project.Key}-3, 1";
        Assert.Equal("1,3", (await svc.CreateCaseAsync(t.Project.Id, good)).LinkedIssues);
    }

    // ---------------------------------------------------------------- runs

    [Fact]
    public async Task A_run_for_a_tag_records_its_commit_and_release_and_unknown_tags_are_refused()
    {
        var t = await SetupAsync();
        await NewCaseAsync(t, "One");
        await NewCaseAsync(t, "Two");
        await using var s = app.Scope(app.UserId("alice"));
        await s.Get<ReleaseService>().CreateAsync(t.Repo, new ReleaseInput { TagName = "v1.0.0", Target = "main", Title = "One" }, app.UserId("alice"));
        var svc = s.Get<TestService>();
        var run = await svc.CreateRunAsync(t.Project.Id, new TestRunInput { Name = "RC", Tag = "v1.0.0" });
        Assert.Equal(40, run.CommitSha!.Length);
        Assert.NotNull(run.ReleaseId);
        var detail = (await svc.GetRunAsync(t.Project.Id, run.Number))!;
        Assert.Equal(2, detail.Results.Count);
        Assert.Equal(2, detail.Summary.NotRun);

        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.CreateRunAsync(t.Project.Id, new TestRunInput { Tag = "v9.9.9" }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.CreateRunAsync(t.Project.Id, new TestRunInput { CommitSha = "not-hex!" }));

        var rows = await svc.RunsForVersionAsync(t.Project.Id, run.CommitSha, null, null);
        Assert.Single(rows);
    }

    [Fact]
    public async Task Recording_results_tracks_progress_and_pass_rate_and_closed_runs_are_read_only()
    {
        var t = await SetupAsync();
        var cases = new[] { await NewCaseAsync(t, "A"), await NewCaseAsync(t, "B"), await NewCaseAsync(t, "C"), await NewCaseAsync(t, "D") };
        await using var s = app.Scope(app.UserId(t.Dev));
        var svc = s.Get<TestService>();
        var run = await svc.CreateRunAsync(t.Project.Id, new TestRunInput { Name = "Manual", Build = "build-42" });
        await svc.RecordResultAsync(t.Project.Id, run.Number, cases[0].Number, TestResultStatus.Passed, null);
        await svc.RecordResultAsync(t.Project.Id, run.Number, cases[1].Number, TestResultStatus.Failed, "broken");
        await svc.RecordResultAsync(t.Project.Id, run.Number, cases[2].Number, TestResultStatus.Skipped, null);
        var sum = (await svc.GetRunAsync(t.Project.Id, run.Number))!.Summary;
        Assert.Equal((1, 1, 0, 1, 1), (sum.Passed, sum.Failed, sum.Blocked, sum.Skipped, sum.NotRun));
        Assert.Equal(50.0, sum.PassRate);
        Assert.Equal(75.0, sum.Progress);

        await svc.SetRunClosedAsync(t.Project.Id, run.Number, true);
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.RecordResultAsync(t.Project.Id, run.Number, cases[3].Number, TestResultStatus.Passed, null));
        await svc.SetRunClosedAsync(t.Project.Id, run.Number, false);
        await svc.RecordResultAsync(t.Project.Id, run.Number, cases[3].Number, TestResultStatus.Passed, null);

        var other = await app.CreateProjectAsync("alice");
        await using var a = app.Scope(app.UserId("alice"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => a.Get<TestService>().RecordResultAsync(other.Id, 1, 1, TestResultStatus.Passed, null)); // ids from another project mean nothing
    }

    [Fact]
    public async Task A_bug_from_a_failure_is_prefilled_linked_and_not_duplicated()
    {
        var t = await SetupAsync();
        var c = await NewCaseAsync(t, "Pay by card");
        await using var s = app.Scope(app.UserId(t.Dev));
        var svc = s.Get<TestService>();
        var run = await svc.CreateRunAsync(t.Project.Id, new TestRunInput { Name = "Nightly", Build = "2026.11.1" });
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.CreateBugAsync(t.Project.Id, run.Number, c.Number)); // still not run
        await svc.RecordResultAsync(t.Project.Id, run.Number, c.Number, TestResultStatus.Failed, "card form 500s");

        var n = await svc.CreateBugAsync(t.Project.Id, run.Number, c.Number);
        Assert.Equal(n, await svc.CreateBugAsync(t.Project.Id, run.Number, c.Number));
        await using var db = app.Db();
        var issue = await db.Issues.FirstAsync(i => i.ProjectId == t.Project.Id && i.Number == n);
        Assert.Equal(IssueType.Bug, issue.Type);
        Assert.Contains("Pay by card", issue.Title);
        Assert.Contains("2026.11.1", issue.Description);
        Assert.Contains("card form 500s", issue.Description);
        Assert.Contains($"/tests/runs/{run.Number}", issue.Description);

        await using var v = app.Scope(app.UserId(t.Viewer));
        await Assert.ThrowsAsync<ForbiddenException>(() => v.Get<TestService>().CreateBugAsync(t.Project.Id, run.Number, c.Number));
    }

    // ---------------------------------------------------------------- ingestion

    private const string Report = """
        <testsuites><testsuite name="s">
          <testcase classname="shop.CartTests" name="adds_item" time="0.1"/>
          <testcase classname="shop.CartTests" name="removes_item" time="0.2"><failure message="bad"/></testcase>
          <testcase classname="shop.Other" name="TC-%N% maps by id"/>
        </testsuite></testsuites>
        """;

    [Fact]
    public async Task Ingesting_maps_by_automated_name_and_id_creates_missing_cases_and_reruns_update_in_place()
    {
        var t = await SetupAsync();
        var byName = await NewCaseAsync(t, "Add item", "shop.CartTests.adds_item", TestCaseType.Automated);
        var byId = await NewCaseAsync(t, "Mapped by id");
        await using var s = app.Scope(app.UserId(t.Dev));
        var svc = s.Get<TestService>();
        var xml = Report.Replace("%N%", byId.Number.ToString());
        var r = await svc.IngestAsync(t.Project.Id, new TestIngestInput { Content = xml, Build = "ci-1", Name = "CI one" });
        Assert.Equal((3, 2, 1, 0), (r.Total, r.Passed, r.Failed, r.Skipped));
        Assert.Equal(1, r.Created); // removes_item had no case
        var detail = (await svc.GetRunAsync(t.Project.Id, r.RunNumber))!;
        Assert.True(detail.Run.IsClosed);
        Assert.Equal(3, detail.Results.Count);
        Assert.Equal(TestResultStatus.Passed, detail.Results.First(x => x.TestCaseId == byName.Id).Status);
        Assert.Equal(TestResultStatus.Passed, detail.Results.First(x => x.TestCaseId == byId.Id).Status);
        var created = detail.Results.First(x => x.TestCase.AutomatedName == "shop.CartTests.removes_item");
        Assert.Equal(TestResultStatus.Failed, created.Status);
        Assert.Equal(TestCaseType.Automated, created.TestCase.Type);
        Assert.Equal("bad", created.ErrorMessage);

        // The same report into the same run again: no new cases, still three results.
        var again = await svc.IngestAsync(t.Project.Id, new TestIngestInput { Content = xml, RunNumber = r.RunNumber });
        Assert.Equal(0, again.Created);
        Assert.Equal(3, (await svc.GetRunAsync(t.Project.Id, r.RunNumber))!.Results.Count);

        // With auto-create off, unknown tests are counted as unmapped instead.
        var strict = await svc.IngestAsync(t.Project.Id, new TestIngestInput
        {
            Content = """<testsuite><testcase classname="x.Y" name="brand_new"/></testsuite>""", AutoCreateCases = false,
        });
        Assert.Equal((0, 1), (strict.Created, strict.Unmatched));
    }

    [Fact]
    public async Task Jobs_of_one_pipeline_share_a_run_and_the_worst_duplicate_wins()
    {
        var t = await SetupAsync();
        int pipelineId;
        await using (var db = app.Db())
        {
            var p = new Pipeline { RepositoryId = t.Repo.Id, Ref = "main", CommitSha = new string('a', 40), Status = PipelineStatus.Passed };
            db.Pipelines.Add(p);
            await db.SaveChangesAsync();
            pipelineId = p.Id;
        }
        await using var s = app.Scope(app.UserId(t.Dev));
        var svc = s.Get<TestService>();
        var a = await svc.IngestAsync(t.Project.Id, new TestIngestInput { PipelineId = pipelineId, Content = """<testsuite><testcase name="dup"/><testcase name="dup"><failure/></testcase></testsuite>""" });
        var b = await svc.IngestAsync(t.Project.Id, new TestIngestInput { PipelineId = pipelineId, Content = """<testsuite><testcase name="other"/></testsuite>""" });
        Assert.Equal(a.RunNumber, b.RunNumber);
        var detail = (await svc.GetRunAsync(t.Project.Id, a.RunNumber))!;
        Assert.Equal(new string('a', 40), detail.Run.CommitSha);
        Assert.Equal(2, detail.Results.Count);
        Assert.Equal(TestResultStatus.Failed, detail.Results.First(x => x.TestCase.Title == "dup").Status);
    }

    [Fact]
    public async Task Hostile_reports_are_refused_by_the_service()
    {
        var t = await SetupAsync();
        await using var s = app.Scope(app.UserId(t.Dev));
        var svc = s.Get<TestService>();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => svc.IngestAsync(t.Project.Id, new TestIngestInput
        {
            Content = """<?xml version="1.0"?><!DOCTYPE x [<!ENTITY e SYSTEM "file:///etc/passwd">]><testsuite><testcase name="&e;"/></testsuite>""",
        }));
        Assert.Contains("DOCTYPE", ex.Message);
        await using var db = app.Db();
        Assert.False(await db.TestRuns.AnyAsync(r => r.ProjectId == t.Project.Id)); // a refused report leaves no half-made run
    }

    // ---------------------------------------------------------------- compare, matrix

    [Fact]
    public async Task Compare_finds_regressions_fixes_and_the_matrix_lines_runs_up()
    {
        var t = await SetupAsync();
        var a = await NewCaseAsync(t, "Stays green");
        var b = await NewCaseAsync(t, "Regresses");
        var c = await NewCaseAsync(t, "Gets fixed");
        await using var s = app.Scope(app.UserId(t.Dev));
        var svc = s.Get<TestService>();
        var r1 = await svc.CreateRunAsync(t.Project.Id, new TestRunInput { Name = "v1", Build = "1.0" });
        var r2 = await svc.CreateRunAsync(t.Project.Id, new TestRunInput { Name = "v2", Build = "1.1" });
        foreach (var (run, results) in new[]
                 {
                     (r1, new[] { TestResultStatus.Passed, TestResultStatus.Passed, TestResultStatus.Failed }),
                     (r2, new[] { TestResultStatus.Passed, TestResultStatus.Failed, TestResultStatus.Passed }),
                 })
            for (var i = 0; i < 3; i++)
                await svc.RecordResultAsync(t.Project.Id, run.Number, new[] { a, b, c }[i].Number, results[i], null);

        var cmp = (await svc.CompareAsync(t.Project.Id, r1.Number, r2.Number))!;
        Assert.Equal(TestChange.Regressed, cmp.Rows.Single(x => x.Case.Id == b.Id).Change);
        Assert.Equal(TestChange.Fixed, cmp.Rows.Single(x => x.Case.Id == c.Id).Change);
        Assert.Equal(TestChange.Unchanged, cmp.Rows.Single(x => x.Case.Id == a.Id).Change);
        Assert.Equal(TestChange.Regressed, cmp.Rows[0].Change); // regressions first

        var m = await svc.MatrixAsync(t.Project.Id, [r1.Number, r2.Number]);
        Assert.Equal(2, m.Runs.Count);
        Assert.Equal(TestResultStatus.Failed, m.Cells[(b.Id, r2.Id)]);
        Assert.Equal(2, (await svc.MatrixAsync(t.Project.Id, [r1.Number, r2.Number], onlyProblems: true)).Cases.Count);
    }

    [Theory]
    [InlineData(TestResultStatus.Passed, TestResultStatus.Failed, TestChange.Regressed)]
    [InlineData(TestResultStatus.Passed, TestResultStatus.Blocked, TestChange.Regressed)]
    [InlineData(TestResultStatus.Failed, TestResultStatus.Passed, TestChange.Fixed)]
    [InlineData(TestResultStatus.Failed, TestResultStatus.Failed, TestChange.StillFailing)]
    [InlineData(TestResultStatus.Passed, TestResultStatus.Skipped, TestChange.Unchanged)]
    [InlineData(TestResultStatus.NotRun, TestResultStatus.Passed, TestChange.NewlyRun)]
    [InlineData(TestResultStatus.Passed, TestResultStatus.NotRun, TestChange.NoLongerRun)]
    public void Classification(TestResultStatus a, TestResultStatus b, TestChange expected) => Assert.Equal(expected, TestService.Classify(a, b));

    // ---------------------------------------------------------------- REST

    private static StringContent Json(object body) => new(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

    private static async Task<JsonElement> Read(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;

    [Fact]
    public async Task Rest_flow_case_plan_run_result_bug_evidence_report_compare()
    {
        var t = await SetupAsync();
        var dev = await app.ApiClientAsync(t.Dev);
        var maint = await app.ApiClientAsync(t.Maintainer);
        var viewer = await app.ApiClientAsync(t.Viewer);
        var outsider = await app.ApiClientAsync(t.Outsider);
        var root = $"/api/v1/projects/{t.Project.Key}/tests";

        Assert.Equal(HttpStatusCode.NotFound, (await outsider.GetAsync(root + "/cases")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync(root + "/cases")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsync(root + "/cases", Json(new { title = "x" }))).StatusCode);

        var created = await dev.PostAsync(root + "/cases", Json(new { title = "Checkout", steps = "go", expected = "ok", priority = "high", tags = new[] { "smoke" }, suite = "Shop" }));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var caseNo = (await Read(created)).GetProperty("number").GetInt32();
        Assert.Equal(HttpStatusCode.BadRequest, (await dev.PostAsync(root + "/cases", Json(new { title = "x", priority = "urgent" }))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await dev.PostAsync(root + "/cases", Json(new { title = "" }))).StatusCode);

        var patched = await dev.SendAsync(new HttpRequestMessage(HttpMethod.Patch, $"{root}/cases/{caseNo}") { Content = Json(new { steps = "go, then pay" }) });
        Assert.Equal(2, (await Read(patched)).GetProperty("version").GetInt32());
        Assert.Equal("Checkout", (await Read(patched)).GetProperty("title").GetString());
        Assert.Equal(1, (await Read(await dev.GetAsync($"{root}/cases/{caseNo}/revisions"))).GetArrayLength());

        Assert.Equal(HttpStatusCode.Forbidden, (await dev.PostAsync(root + "/plans", Json(new { name = "P", caseNumbers = new[] { caseNo } }))).StatusCode);
        var plan = await maint.PostAsync(root + "/plans", Json(new { name = "Smoke", caseNumbers = new[] { caseNo } }));
        Assert.Equal(HttpStatusCode.Created, plan.StatusCode);
        var planId = (await Read(plan)).GetProperty("id").GetInt32();

        var run = await dev.PostAsync(root + "/runs", Json(new { name = "Run 1", planId, build = "b1" }));
        Assert.Equal(HttpStatusCode.Created, run.StatusCode);
        var runNo = (await Read(run)).GetProperty("number").GetInt32();
        Assert.Equal(HttpStatusCode.BadRequest, (await dev.PutAsync($"{root}/runs/{runNo}/results/{caseNo}", Json(new { status = "maybe" }))).StatusCode);
        var rec = await dev.PutAsync($"{root}/runs/{runNo}/results/{caseNo}", Json(new { status = "failed", comment = "nope" }));
        Assert.Equal("failed", (await Read(rec)).GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PutAsync($"{root}/runs/{runNo}/results/{caseNo}", Json(new { status = "passed" }))).StatusCode);

        var bug = await dev.PostAsync($"{root}/runs/{runNo}/results/{caseNo}/bug", Json(new { }));
        Assert.Equal(HttpStatusCode.OK, bug.StatusCode);
        Assert.StartsWith(t.Project.Key + "-", (await Read(bug)).GetProperty("key").GetString());

        // Evidence is stored and served only as an attachment.
        var ev = await dev.PostAsync($"{root}/runs/{runNo}/results/{caseNo}/evidence?fileName=shot.html", new ByteArrayContent(Encoding.UTF8.GetBytes("<script>alert(1)</script>")));
        Assert.Equal(HttpStatusCode.Created, ev.StatusCode);
        var download = await viewer.GetAsync((await Read(ev)).GetProperty("downloadUrl").GetString());
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal("application/octet-stream", download.Content.Headers.ContentType!.MediaType);
        Assert.Equal("attachment", download.Content.Headers.ContentDisposition!.DispositionType);
        Assert.Equal("nosniff", download.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.GetAsync((await Read(ev)).GetProperty("downloadUrl").GetString())).StatusCode);

        // Raw XML upload, with and without a JSON envelope.
        var xml = new StringContent("""<testsuite><testcase classname="a.B" name="c"/><testcase classname="a.B" name="d"><failure message="x"/></testcase></testsuite>""", Encoding.UTF8, "application/xml");
        var up = await dev.PostAsync(root + "/reports?build=ci-9&name=Nightly", xml);
        Assert.Equal(HttpStatusCode.Created, up.StatusCode);
        var body = await Read(up);
        Assert.Equal((2, 1, 1), (body.GetProperty("total").GetInt32(), body.GetProperty("passed").GetInt32(), body.GetProperty("failed").GetInt32()));
        var envelope = await dev.PostAsync(root + "/reports", Json(new { content = "<testsuite><testcase classname=\"a.B\" name=\"c\"/><testcase classname=\"a.B\" name=\"d\"/></testsuite>", build = "ci-10" }));
        Assert.Equal(HttpStatusCode.Created, envelope.StatusCode);
        var ctrf = await dev.PostAsync(root + "/reports?build=ci-11", new StringContent("""{"results":{"tests":[{"name":"a.B.c","status":"passed"}]}}""", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Created, ctrf.StatusCode);

        var hostile = await dev.PostAsync(root + "/reports", new StringContent("""<!DOCTYPE a [<!ENTITY x SYSTEM "file:///etc/hosts">]><testsuite><testcase name="&x;"/></testsuite>""", Encoding.UTF8, "application/xml"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, hostile.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsync(root + "/reports", xml)).StatusCode);

        var firstCi = body.GetProperty("runNumber").GetInt32();
        var secondCi = (await Read(envelope)).GetProperty("runNumber").GetInt32();
        var cmp = await Read(await viewer.GetAsync($"{root}/compare?a={firstCi}&b={secondCi}"));
        Assert.Equal(1, cmp.GetProperty("counts").GetProperty("fixed").GetInt32());
        var matrix = await Read(await viewer.GetAsync($"{root}/matrix?runs={firstCi},{secondCi}&problems=true"));
        Assert.Equal(2, matrix.GetProperty("runs").GetArrayLength());
        Assert.Equal(1, matrix.GetProperty("cases").GetArrayLength());

        var list = await Read(await viewer.GetAsync(root + "/cases?q=checkout"));
        Assert.Equal("failed", list[0].GetProperty("latest").GetProperty("status").GetString());

        Assert.Equal(HttpStatusCode.Forbidden, (await dev.DeleteAsync($"{root}/runs/{runNo}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await maint.DeleteAsync($"{root}/runs/{runNo}")).StatusCode);
    }

    [Fact]
    public async Task Oversized_report_bodies_are_refused_with_413()
    {
        var t = await SetupAsync();
        var dev = await app.ApiClientAsync(t.Dev);
        var big = new StringContent("<testsuite>" + new string('a', TestReportParser.MaxBytes + 10) + "</testsuite>", Encoding.UTF8, "application/xml");
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await dev.PostAsync($"/api/v1/projects/{t.Project.Key}/tests/reports", big)).StatusCode);
    }

    [Fact]
    public async Task Deleting_a_project_removes_its_test_data_and_evidence_files()
    {
        var t = await SetupAsync();
        var c = await NewCaseAsync(t, "Has evidence");
        await using (var s = app.Scope(app.UserId(t.Dev)))
        {
            var svc = s.Get<TestService>();
            var run = await svc.CreateRunAsync(t.Project.Id, new TestRunInput { Name = "r" });
            await svc.RecordResultAsync(t.Project.Id, run.Number, c.Number, TestResultStatus.Failed, null);
            await svc.AddEvidenceAsync(t.Project.Id, run.Number, c.Number, "log.txt", new MemoryStream("data"u8.ToArray()));
        }
        string stored;
        await using (var db = app.Db()) stored = await db.TestEvidence.Where(e => e.Result.Run.ProjectId == t.Project.Id).Select(e => e.StoredName).SingleAsync();
        var file = Path.Combine(app.AttachmentRoot, stored);
        Assert.True(File.Exists(file));
        await using (var s = app.Scope(app.UserId("alice"))) await s.Get<IssueService>().DeleteProjectAsync(t.Project.Id);
        Assert.False(File.Exists(file));
        await using var after = app.Db();
        Assert.False(await after.TestCases.AnyAsync(x => x.ProjectId == t.Project.Id));
    }
}
