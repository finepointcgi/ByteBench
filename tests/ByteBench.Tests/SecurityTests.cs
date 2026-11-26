using System.Net;
using System.Text;
using System.Text.Json;
using ByteBench.Data;
using ByteBench.Services;
using ByteBench.Services.Ci;
using ByteBench.Services.Security;
using ByteBench.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ByteBench.Tests;

/// <summary>Fixture reports shaped like Night Owl's real output (ScanReport JSON, SarifWriter SARIF, TriageStore triage.json).</summary>
internal static class SecurityFixtures
{
    public static string Read(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Security", name));

    // Fingerprints computed with Night Owl's Fingerprint scheme (see tests/.../Fixtures/Security).
    public const string Lodash = "c52e02814b743f7d1434619b97e20e3a";
    public const string Secret = "02257fd41e6907613b51c092d1a64f05";
    public const string Sqli = "a06f2fbfa39f1f4a2a210c1e64c0034f";
    public const string Container = "2356495ae6832d54cbdf62a3f84be3ec";
    public const string Quality = "3716fd71a893335bfec443739c070f59";
    public const string ReportedFalsePositive = "74f83d20fa17990c6b5da0916324e011";
    public const string ApiSqli = "89ff3fd191545727b6e5793b1b72b7f8";
    public const string Duplication = "1a5c99c04c4549a25dafd0e7d1c47ac8";
}

public class SecurityReportParserTests
{
    [Fact]
    public void Night_Owl_json_is_parsed_and_deduplicated_by_fingerprint()
    {
        var r = SecurityReportParser.Parse(SecurityFixtures.Read("nightowl-main.json"));
        Assert.Equal("nightowl", r.Format);
        Assert.Equal("Night Owl", r.ToolName);
        Assert.Equal(8, r.Findings.Count); // 9 entries, lodash reported by two engines

        var lodash = r.Findings.Single(f => f.Fingerprint == SecurityFixtures.Lodash);
        Assert.Equal(SecurityCategory.Dependency, lodash.Category);
        Assert.Equal(SecuritySeverity.Critical, lodash.Severity); // the higher of the two engines
        Assert.Equal(["osv-scanner", "trivy"], lodash.Engines);
        Assert.Equal("lodash", lodash.PackageName);
        Assert.Equal("4.17.20", lodash.PackageVersion);
        Assert.Equal(["4.17.21"], lodash.FixedVersions);
        Assert.Contains("CVE-2021-23337", lodash.Aliases);
        Assert.Equal("https://github.com/advisories/GHSA-35jh-r3h4-6jhm", lodash.HelpUrl);

        Assert.Equal(SecurityCategory.Container, r.Findings.Single(f => f.Fingerprint == SecurityFixtures.Container).Category);
        Assert.Equal(SecurityCategory.Secret, r.Findings.Single(f => f.Fingerprint == SecurityFixtures.Secret).Category);
        Assert.Equal(SecurityCategory.Quality, r.Findings.Single(f => f.Fingerprint == SecurityFixtures.Quality).Category);
        Assert.Contains(r.Findings, f => f.Category == SecurityCategory.License && f.PackageName == "left-pad-gpl");
        Assert.Contains(r.Findings, f => f.Category == SecurityCategory.Infrastructure && f.RuleId == "DS002" && f.FilePath == "Dockerfile");
        var sqli = r.Findings.Single(f => f.Fingerprint == SecurityFixtures.Sqli);
        Assert.Equal((SecurityCategory.Code, SecuritySeverity.Medium, "app/db.py", 40), (sqli.Category, sqli.Severity, sqli.FilePath, sqli.Line));
        var fp = r.Findings.Single(f => f.Fingerprint == SecurityFixtures.ReportedFalsePositive);
        Assert.Equal(SecurityTriageState.FalsePositive, fp.ReportedTriage); // from the repository's .nightowl/triage.json at scan time
        Assert.Equal("test", fp.Context);
    }

    [Fact]
    public void Night_Owl_sarif_keeps_the_same_fingerprints_and_severities_as_its_json()
    {
        var json = SecurityReportParser.Parse(SecurityFixtures.Read("nightowl-pr.json"));
        var sarif = SecurityReportParser.Parse(SecurityFixtures.Read("nightowl-pr.sarif"));
        Assert.Equal("sarif", sarif.Format);
        Assert.Equal(("Night Owl", "0.1.1"), (sarif.ToolName, sarif.ToolVersion));
        Assert.Equal(json.Findings.Select(f => f.Fingerprint).Order(), sarif.Findings.Select(f => f.Fingerprint).Order());
        foreach (var f in json.Findings)
        {
            var s = sarif.Findings.Single(x => x.Fingerprint == f.Fingerprint);
            Assert.Equal(f.Severity, s.Severity);
            // SARIF can't tell a container image vulnerability from a package one; everything else maps the same.
            if (f.Category != SecurityCategory.Container) Assert.Equal(f.Category, s.Category);
        }
        var api = sarif.Findings.Single(f => f.Fingerprint == SecurityFixtures.ApiSqli);
        Assert.Equal(("app/api.py", 88, SecuritySeverity.Critical), (api.FilePath, api.Line, api.Severity));
        Assert.Equal(["opengrep"], api.Engines);
    }

    [Fact]
    public void Generic_sarif_gets_derived_fingerprints_and_cvss_severities()
    {
        var r = SecurityReportParser.Parse(SecurityFixtures.Read("generic-tool.sarif"));
        Assert.Equal("ExampleScanner", r.ToolName);
        Assert.Equal(2, r.Findings.Count); // the duplicated EX002 result merges
        var pw = r.Findings.Single(f => f.RuleId == "EX001");
        Assert.Equal(SecuritySeverity.Critical, pw.Severity); // security-severity 9.1
        Assert.Equal(SecurityCategory.Secret, pw.Category);
        Assert.Equal("src/Config.cs", pw.FilePath); // file:///workspace/ prefix stripped
        Assert.Equal("Hard-coded password", pw.Title);
        Assert.Equal("https://example.com/rules/EX001", pw.HelpUrl);
        Assert.Equal(SecurityReportParser.CodeFingerprint("EX001", "src/Config.cs", "var dbPassword = \"hunter2\";", 7), pw.Fingerprint);
        var md5 = r.Findings.Single(f => f.RuleId == "EX002");
        Assert.Equal(SecuritySeverity.Medium, md5.Severity); // rule's default level: warning
        Assert.Equal(SecurityCategory.Code, md5.Category);
        // Stable across parses (so triage sticks for tools without fingerprints too).
        Assert.Equal(md5.Fingerprint, SecurityReportParser.Parse(SecurityFixtures.Read("generic-tool.sarif")).Findings.Single(f => f.RuleId == "EX002").Fingerprint);
    }

    [Fact]
    public void Code_fingerprints_match_night_owls_scheme()
    {
        // Fingerprint.ForCode in NightOwl.Core: rule, path without leading ./, snippet with whitespace collapsed.
        Assert.Equal(SecurityFixtures.Sqli, SecurityReportParser.CodeFingerprint("python.lang.security.audit.formatted-sql-query", "./app/db.py",
            "cursor.execute(\"SELECT *  FROM users WHERE id=%s\"   % uid)", 99));
        Assert.Equal(SecurityFixtures.Quality, SecurityReportParser.CodeFingerprint("complexity", "app/views.py", null, 10));
    }

    [Fact]
    public void Bad_reports_are_rejected_with_a_reason()
    {
        Assert.Contains("not valid JSON", Assert.Throws<InvalidDataException>(() => SecurityReportParser.Parse("{ nope")).Message);
        Assert.Contains("Unrecognised", Assert.Throws<InvalidDataException>(() => SecurityReportParser.Parse("{\"hello\": 1}")).Message);
        Assert.Contains("SARIF", Assert.Throws<InvalidDataException>(() => SecurityReportParser.Parse("{\"findings\": []}", "sarif")).Message);
    }

    [Fact]
    public void Triage_file_round_trips_in_night_owls_format()
    {
        var read = NightOwlTriageFile.Read(SecurityFixtures.Read("triage.json"));
        Assert.Equal([SecurityTriageState.AcceptedRisk, SecurityTriageState.FalsePositive], read.Select(r => r.State));
        var written = NightOwlTriageFile.Write([
            new SecurityTriage { Fingerprint = "b", State = SecurityTriageState.FalsePositive, Reason = "test fixture", By = "ci", At = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc) },
            new SecurityTriage { Fingerprint = "a", State = SecurityTriageState.AcceptedRisk, Reason = "r", ExpiresAt = new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc), At = DateTime.UtcNow },
        ]);
        using var doc = JsonDocument.Parse(written);
        var arr = doc.RootElement.EnumerateArray().ToList();
        Assert.Equal(["a", "b"], arr.Select(e => e.GetProperty("findingId").GetString())); // sorted, like TriageStore.Save
        Assert.Equal("accepted-risk", arr[0].GetProperty("state").GetString());
        Assert.Equal("false-positive", arr[1].GetProperty("state").GetString());
        Assert.False(arr[1].TryGetProperty("expires", out _)); // nulls left out
        Assert.Throws<InvalidDataException>(() => NightOwlTriageFile.Read("""[{"findingId":"x","state":"maybe","at":"2026-01-01T00:00:00Z"}]"""));
    }
}

public class SecurityCiFileTests
{
    [Fact]
    public void Include_nightowl_adds_a_job_that_publishes_both_reports()
    {
        var r = CiFile.Parse("""
            include: [nightowl]
            jobs:
              build: { steps: [ { run: make } ] }
            """);
        Assert.True(r.Ok, r.Error);
        var job = r.Definition!.Jobs.Single(j => j.Name == "nightowl");
        Assert.Equal("nightowl.json", job.Artifacts!.Reports!["nightowl"]);
        Assert.Equal("nightowl.sarif", job.Artifacts.Reports["sarif"]);
        Assert.Contains("nightowl.json", job.Artifacts.Paths);
        Assert.Contains("nightowl.md", job.Artifacts.Paths);
        Assert.Equal(CiArtifactsWhen.Always, job.Artifacts.When); // a failed gate still publishes its report
        var run = job.Steps.Single().Run;
        Assert.Contains("nightowl scan \"${NIGHTOWL_PATH:-.}\"", run);
        Assert.Contains("--format json --output nightowl.json", run);
        Assert.Contains("--sarif-output nightowl.sarif", run);
        Assert.Contains("--fail-on $NIGHTOWL_FAIL_ON", run);

        // Round-trips through the job's stored extras (what the runner reads).
        var extras = CiJobExtras.Parse(CiJobExtras.Serialize(job));
        Assert.Equal("nightowl.json", extras.Artifacts!.Reports!["nightowl"]);
    }

    /// <summary>Runs the template's script with <c>sh</c> and a PATH holding only stubs (no install, no docker).</summary>
    private static (int Exit, string Output, string Args) RunTemplate(params string[] stubs)
    {
        var dir = Directory.CreateTempSubdirectory("nightowl-tpl").FullName;
        try
        {
            var bin = Directory.CreateDirectory(Path.Combine(dir, "bin")).FullName;
            foreach (var stub in stubs)
            {
                var body = stub == "nightowl" ? "#!/bin/sh\necho \"$@\" > \"$PWD/args.txt\"\n" : "#!/bin/sh\n";
                var path = Path.Combine(bin, stub);
                File.WriteAllText(path, body);
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            var run = CiFile.Parse("include: nightowl\n").Definition!.Jobs.Single(j => j.Name == "nightowl").Steps.Single().Run;
            File.WriteAllText(Path.Combine(dir, "step.sh"), run);
            var psi = new System.Diagnostics.ProcessStartInfo("/bin/sh") { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true };
            psi.ArgumentList.Add("-e");
            psi.ArgumentList.Add("step.sh");
            psi.Environment["PATH"] = bin; // only the stubs; /bin/sh builtins do the rest
            using var p = System.Diagnostics.Process.Start(psi)!;
            var output = p.StandardError.ReadToEnd() + p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            var argsFile = Path.Combine(dir, "args.txt");
            return (p.ExitCode, output, File.Exists(argsFile) ? File.ReadAllText(argsFile) : "");
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Template_fails_fast_with_instructions_when_the_image_has_no_engines()
    {
        if (OperatingSystem.IsWindows()) return;
        var (exit, output, args) = RunTemplate("nightowl"); // nightowl present (no install), but no docker and no engines
        Assert.Equal(2, exit);
        Assert.Contains("no scan engines", output);
        Assert.Contains("runner image", output);
        Assert.Equal("", args); // the scan never started
    }

    [Fact]
    public void Template_skips_the_install_and_scans_when_the_image_has_nightowl_and_an_engine()
    {
        if (OperatingSystem.IsWindows()) return;
        var (exit, _, args) = RunTemplate("nightowl", "opengrep");
        Assert.Equal(0, exit);
        Assert.StartsWith("scan .", args);
        Assert.Contains("--no-update", args);
        Assert.Contains("--output nightowl.json", args);
    }

    [Fact]
    public void A_job_of_the_same_name_replaces_the_template_and_reports_need_no_paths()
    {
        var r = CiFile.Parse("""
            include: nightowl
            jobs:
              nightowl:
                image: example.com/nightowl-engines:1
                artifacts: { reports: { nightowl: out/report.json } }
                steps: [ { run: nightowl scan --format json --output out/report.json } ]
            """);
        Assert.True(r.Ok, r.Error);
        var job = r.Definition!.Jobs.Single();
        Assert.Equal("example.com/nightowl-engines:1", job.Image);
        Assert.Equal(["out/report.json"], job.Artifacts!.Paths);
        Assert.Equal(CiArtifactsWhen.OnSuccess, job.Artifacts.When);
    }

    [Theory]
    [InlineData("include: [codeql]\njobs: { a: { steps: [ { run: x } ] } }", "not a built-in template")]
    [InlineData("jobs: { a: { artifacts: { reports: { tap: r.tap } }, steps: [ { run: x } ] } }", "not supported")]
    [InlineData("jobs: { a: { artifacts: { reports: { sarif: \"*.sarif\" } }, steps: [ { run: x } ] } }", "one file")]
    [InlineData("jobs: { a: { artifacts: { reports: { sarif: ../x.sarif } }, steps: [ { run: x } ] } }", "relative")]
    public void Bad_includes_and_reports_are_errors(string yaml, string expected)
    {
        var r = CiFile.Parse(yaml);
        Assert.False(r.Ok);
        Assert.Contains(expected, r.Error);
    }
}

/// <summary>Ingestion, triage, the PR diff, the merge check and the API, against the database (reports come from fixtures).</summary>
public class SecurityServiceTests(TestApp app) : IClassFixture<TestApp>
{
    private async Task<(Project Project, Repository Repo)> SetupAsync()
    {
        var project = await app.CreateProjectAsync("alice", ProjectVisibility.Private, ("bob", ProjectRole.Developer), ("carol", ProjectRole.Maintainer));
        var repo = await app.CreateRepoAsync(project.Id, "alice");
        app.Commit(repo, "main", "README.md", "# app");
        return (project, repo);
    }

    private async Task<(int PipelineId, int JobId)> PipelineAsync(Repository repo, string branch, string sha, int? triggeredBy = null)
    {
        await using var db = app.Db();
        var p = new Pipeline { RepositoryId = repo.Id, Ref = branch, CommitSha = sha, Status = PipelineStatus.Passed, Trigger = PipelineTrigger.Push, TriggeredById = triggeredBy };
        var job = new PipelineJob { Name = "nightowl", Image = "alpine:3.20", Status = PipelineStatus.Passed };
        job.Steps.Add(new PipelineStep { Name = "scan", Run = "nightowl scan", Status = PipelineStatus.Passed });
        p.Jobs.Add(job);
        db.Pipelines.Add(p);
        await db.SaveChangesAsync();
        return (p.Id, job.Id);
    }

    private async Task<int> ScanAsync(Repository repo, string branch, string fixture, string? sha = null, string format = "nightowl")
    {
        var (pid, jid) = await PipelineAsync(repo, branch, sha ?? Guid.NewGuid().ToString("N") + "00000000", app.UserId("bob"));
        var r = await app.Services.GetRequiredService<SecurityIngestor>().IngestAsync(pid, jid, format, SecurityFixtures.Read(fixture));
        Assert.True(r.Ok, r.Error);
        return pid;
    }

    [Fact]
    public async Task Ingested_report_is_the_branch_snapshot_with_open_counts_and_trend()
    {
        var (_, repo) = await SetupAsync();
        await ScanAsync(repo, "main", "nightowl-main.json");
        await using var s = app.Scope(app.UserId("bob"));
        var security = s.Get<SecurityService>();
        var (snap, open) = await security.ListAsync(repo, new SecurityFindingFilter());
        Assert.NotNull(snap);
        Assert.Equal(8, snap.Findings.Count);
        Assert.Equal(7, open.Count); // the eval() finding was a false positive in the report's own triage
        Assert.DoesNotContain(open, f => f.Fingerprint == SecurityFixtures.ReportedFalsePositive);
        Assert.Equal(SecurityFixtures.Lodash, (await security.ListAsync(repo, new SecurityFindingFilter(Category: SecurityCategory.Dependency))).Findings.Single().Fingerprint);
        Assert.Equal(3, (await security.ListAsync(repo, new SecurityFindingFilter(MinSeverity: SecuritySeverity.High))).Findings.Count); // lodash, secret, container
        Assert.Single((await security.ListAsync(repo, new SecurityFindingFilter(State: "dismissed"))).Findings);
        Assert.Single((await security.ListAsync(repo, new SecurityFindingFilter(Text: "CVE-2021-23337"))).Findings);

        await ScanAsync(repo, "main", "nightowl-pr.json");
        var trend = await security.TrendAsync(repo);
        Assert.Equal(2, trend.Count);
        Assert.Equal((2, 1, 2, 2), (trend[0].Critical, trend[0].High, trend[0].Medium, trend[0].Low)); // lodash+secret; container; sqli+gpl; iac+complexity
        Assert.Equal((2, 1, 2, 3), (trend[1].Critical, trend[1].High, trend[1].Medium, trend[1].Low));
        Assert.Equal(["main"], await security.BranchesAsync(repo));
    }

    [Fact]
    public async Task Night_owl_json_and_sarif_from_one_job_are_stored_once()
    {
        var (_, repo) = await SetupAsync();
        var (pid, jid) = await PipelineAsync(repo, "main", new string('c', 40));
        var ws = Path.Combine(app.DataRoot, "ws-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(ws);
        File.WriteAllText(Path.Combine(ws, "nightowl.json"), SecurityFixtures.Read("nightowl-pr.json"));
        File.WriteAllText(Path.Combine(ws, "nightowl.sarif"), SecurityFixtures.Read("nightowl-pr.sarif"));
        var ingestor = app.Services.GetRequiredService<SecurityIngestor>();
        var notes = await ingestor.IngestWorkspaceAsync(pid, jid, ws, new Dictionary<string, string> { ["sarif"] = "nightowl.sarif", ["nightowl"] = "nightowl.json" });
        Assert.Contains(notes, n => n.Contains("nightowl.json (nightowl): 9 finding(s)"));
        Assert.Contains(notes, n => n.Contains("read from the Night Owl JSON"));
        // Retrying the job replaces its report instead of adding another.
        await ingestor.IngestWorkspaceAsync(pid, jid, ws, new Dictionary<string, string> { ["nightowl"] = "nightowl.json" });
        await using var db = app.Db();
        Assert.Equal(1, await db.SecurityScans.CountAsync(x => x.PipelineId == pid));
        Assert.Equal(9, await db.SecurityFindings.CountAsync(f => f.Scan.PipelineId == pid));

        // Missing files and links are reported, never followed.
        var missing = await ingestor.IngestWorkspaceAsync(pid, jid, ws, new Dictionary<string, string> { ["nightowl"] = "nope.json" });
        Assert.Contains("was not written by the job", Assert.Single(missing));
        if (!OperatingSystem.IsWindows())
        {
            File.CreateSymbolicLink(Path.Combine(ws, "link.json"), Path.Combine(ws, "nightowl.json"));
            var link = await ingestor.IngestWorkspaceAsync(pid, jid, ws, new Dictionary<string, string> { ["nightowl"] = "link.json" });
            Assert.Contains("symbolic link", Assert.Single(link));
        }
    }

    [Fact]
    public async Task Triage_sticks_across_scans_by_fingerprint_and_can_be_reopened()
    {
        var (_, repo) = await SetupAsync();
        await ScanAsync(repo, "main", "nightowl-main.json");
        await using var s = app.Scope(app.UserId("bob"));
        var security = s.Get<SecurityService>();

        await Assert.ThrowsAsync<InvalidOperationException>(() => security.TriageAsync(repo, SecurityFixtures.Sqli, SecurityTriageState.AcceptedRisk, null));
        await security.TriageAsync(repo, SecurityFixtures.Sqli, SecurityTriageState.AcceptedRisk, "Admin-only endpoint", DateTime.UtcNow.AddDays(30));
        await security.TriageAsync(repo, SecurityFixtures.Secret, SecurityTriageState.Confirmed, null);

        await ScanAsync(repo, "main", "nightowl-pr.json"); // a new scan of the same code
        var (_, open) = await security.ListAsync(repo, new SecurityFindingFilter());
        Assert.DoesNotContain(open, f => f.Fingerprint == SecurityFixtures.Sqli);
        Assert.Equal(SecurityTriageState.Confirmed, open.Single(f => f.Fingerprint == SecurityFixtures.Secret).State); // confirmed stays open
        var detail = await security.GetAsync(repo, SecurityFixtures.Sqli);
        Assert.Equal(SecurityTriageState.AcceptedRisk, detail!.View.State);
        Assert.Equal("Admin-only endpoint", detail.View.Triage!.Reason);
        Assert.Equal(2, detail.ScanCount);

        // A ByteBench decision overrides the report's own; reopening brings it back.
        await security.TriageAsync(repo, SecurityFixtures.ReportedFalsePositive, SecurityTriageState.Confirmed, null);
        Assert.Contains((await security.ListAsync(repo, new SecurityFindingFilter())).Findings, f => f.Fingerprint == SecurityFixtures.ReportedFalsePositive);
        await security.TriageAsync(repo, SecurityFixtures.Sqli, SecurityTriageState.New, null);
        Assert.Contains((await security.ListAsync(repo, new SecurityFindingFilter())).Findings, f => f.Fingerprint == SecurityFixtures.Sqli);

        // An expired decision no longer dismisses the finding.
        await security.TriageAsync(repo, SecurityFixtures.Container, SecurityTriageState.AcceptedRisk, "Waiting on upstream", DateTime.UtcNow.AddDays(1));
        await using (var db = app.Db())
            await db.SecurityTriages.Where(t => t.RepositoryId == repo.Id && t.Fingerprint == SecurityFixtures.Container)
                .ExecuteUpdateAsync(x => x.SetProperty(t => t.ExpiresAt, DateTime.UtcNow.AddMinutes(-1)));
        Assert.Contains((await security.ListAsync(repo, new SecurityFindingFilter())).Findings, f => f.Fingerprint == SecurityFixtures.Container);

        // Viewers can't triage.
        var viewer = await app.CreateUserAsync("sec-viewer-" + Guid.NewGuid().ToString("N")[..6]);
        await using (var a = app.Scope(app.UserId("alice"))) await a.Get<MemberService>().AddMemberAsync(repo.ProjectId, viewer.UserName!, ProjectRole.Viewer);
        await using var v = app.Scope(viewer.Id);
        await Assert.ThrowsAsync<ForbiddenException>(() => v.Get<SecurityService>().TriageAsync(repo, SecurityFixtures.Lodash, SecurityTriageState.FalsePositive, "no"));
    }

    [Fact]
    public async Task Triage_imports_and_exports_night_owls_triage_json()
    {
        var (_, repo) = await SetupAsync();
        await ScanAsync(repo, "main", "nightowl-main.json");
        await using (var dev = app.Scope(app.UserId("bob")))
            await Assert.ThrowsAsync<ForbiddenException>(() => dev.Get<SecurityService>().ImportTriageAsync(repo, SecurityFixtures.Read("triage.json")));

        await using var s = app.Scope(app.UserId("carol"));
        var security = s.Get<SecurityService>();
        Assert.Equal(2, await security.ImportTriageAsync(repo, SecurityFixtures.Read("triage.json")));
        var (_, open) = await security.ListAsync(repo, new SecurityFindingFilter());
        Assert.DoesNotContain(open, f => f.Fingerprint is SecurityFixtures.Sqli or SecurityFixtures.Quality);
        var sqli = (await security.GetAsync(repo, SecurityFixtures.Sqli))!.View.Triage!;
        Assert.Equal(app.UserId("alice"), sqli.UserId); // "by": "alice" is a ByteBench user
        Assert.Equal("someone-external", (await security.GetAsync(repo, SecurityFixtures.Quality))!.View.Triage!.By);

        var exported = NightOwlTriageFile.Read(await security.ExportTriageAsync(repo));
        Assert.Equal([SecurityFixtures.Quality, SecurityFixtures.Sqli], exported.Select(e => e.Decision.FindingId));
        Assert.Equal("alice", exported.Single(e => e.Decision.FindingId == SecurityFixtures.Sqli).Decision.By);

        // From the repository's own .nightowl/triage.json on the default branch.
        app.Commit(repo, "main", NightOwlTriageFile.RepositoryPath, $$"""[{"findingId":"{{SecurityFixtures.Lodash}}","state":"accepted-risk","reason":"No template() use","at":"2026-11-01T00:00:00Z"}]""");
        Assert.Equal(1, await security.ImportTriageFromRepositoryAsync(repo));
        Assert.DoesNotContain((await security.ListAsync(repo, new SecurityFindingFilter())).Findings, f => f.Fingerprint == SecurityFixtures.Lodash);
    }

    [Fact]
    public async Task Pull_request_shows_new_and_fixed_findings_and_the_merge_check_gates_on_them()
    {
        var (_, repo) = await SetupAsync();
        await ScanAsync(repo, "main", "nightowl-main.json");
        var head = app.Commit(repo, "feature/api", "app/api.py", "print('hi')");
        int prId;
        await using (var s = app.Scope(app.UserId("bob")))
            prId = (await s.Get<PullRequestService>().CreateAsync(repo, "API", null, "feature/api", "main", app.UserId("bob"))).Id;

        async Task<IReadOnlyList<MergeCheckResult>> Check()
        {
            await using var s = app.Scope(app.UserId("carol"));
            return (await s.Get<MergeGate>().EvaluateAsync(prId)).Checks.Where(c => c.Name == SecurityMergeCheck.Name).ToList();
        }

        Assert.Empty(await Check()); // not turned on
        await using (var s = app.Scope(app.UserId("carol")))
            await s.Get<SecurityService>().SaveSettingsAsync(repo, true, SecuritySeverity.High, SecuritySeverity.High);
        Assert.Equal(MergeCheckState.Neutral, Assert.Single(await Check()).State); // no report for the head yet

        // A running pipeline for the head: wait for it.
        await using (var db = app.Db())
        {
            db.Pipelines.Add(new Pipeline { RepositoryId = repo.Id, Ref = "feature/api", CommitSha = head, Status = PipelineStatus.Running, Trigger = PipelineTrigger.PullRequest });
            await db.SaveChangesAsync();
        }
        Assert.Equal(MergeCheckState.Pending, Assert.Single(await Check()).State);

        await ScanAsync(repo, "feature/api", "nightowl-pr.json", head);
        await using (var s = app.Scope(app.UserId("bob")))
        {
            var pr = (await s.Get<PullRequestService>().GetAsync(repo.Project.Key, repo.Slug, 1))!;
            var diff = (await s.Get<SecurityService>().PullRequestAsync(pr))!;
            Assert.Equal([SecurityFixtures.ApiSqli, SecurityFixtures.Duplication], diff.New.Select(f => f.Fingerprint));
            Assert.Equal([SecurityFixtures.Lodash], diff.Fixed.Select(f => f.Fingerprint));
            Assert.Equal(6, diff.Unchanged);
        }
        var failed = Assert.Single(await Check());
        Assert.Equal(MergeCheckState.Failed, failed.State);
        Assert.Contains("1 new finding at or above high", failed.Detail);

        // With branch protection requiring checks, the PR can't be merged.
        await using (var s = app.Scope(app.UserId("carol")))
        {
            await s.Get<BranchProtectionService>().SaveAsync(repo.Id, new BranchProtectionRule { Pattern = "main", RequireChecksToPass = true });
            var gate = await s.Get<MergeGate>().EvaluateAsync(prId);
            Assert.Contains(gate.Blockers, b => b.Contains("\"Security\" is failed"));
        }

        // Dismissing the new critical one passes the check; the new low finding is below the threshold.
        await using (var s = app.Scope(app.UserId("bob")))
            await s.Get<SecurityService>().TriageAsync(repo, SecurityFixtures.ApiSqli, SecurityTriageState.FalsePositive, "Parameter is an int");
        var passed = Assert.Single(await Check());
        Assert.Equal(MergeCheckState.Passed, passed.State);
        Assert.Contains("1 new below it", passed.Detail);
        Assert.Contains("1 fixed", passed.Detail);
    }

    [Fact]
    public async Task New_high_findings_on_the_default_branch_notify_maintainers_once()
    {
        var (project, repo) = await SetupAsync();
        int Count(string user)
        {
            using var db = app.Db();
            var id = app.UserId(user);
            return db.Notifications.Count(n => n.UserId == id && n.ProjectId == project.Id && n.Kind == NotificationKind.SecurityFindings);
        }

        await ScanAsync(repo, "feature/x", "nightowl-main.json"); // not the default branch: no alert
        Assert.Equal(0, Count("carol"));
        await ScanAsync(repo, "main", "nightowl-main.json");
        Assert.Equal(1, Count("carol")); // Maintainer
        Assert.Equal(1, Count("alice")); // Owner
        Assert.Equal(1, Count("bob"));   // triggered the pipeline
        await ScanAsync(repo, "main", "nightowl-main.json"); // nothing new
        Assert.Equal(1, Count("carol"));
        await ScanAsync(repo, "main", "nightowl-pr.json"); // a new critical SQL injection
        Assert.Equal(2, Count("carol"));
        await using var db = app.Db();
        var carol = app.UserId("carol");
        var latest = await db.Notifications.Where(n => n.UserId == carol && n.Kind == NotificationKind.SecurityFindings).OrderByDescending(n => n.Id).FirstAsync();
        Assert.Contains("1 new critical security finding", latest.Title);
        Assert.Equal(SecurityUrls.Repo(repo), latest.Url);
    }

    [Fact]
    public async Task Rest_api_lists_and_triages_findings()
    {
        var (project, repo) = await SetupAsync();
        await ScanAsync(repo, "main", "nightowl-main.json");
        var bob = await app.ApiClientAsync("bob");
        var baseUrl = $"/api/v1/projects/{project.Key}/repos/{repo.Slug}/security";

        var list = await bob.GetAsync(baseUrl + "/findings?severity=high");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        Assert.Equal("3", list.Headers.GetValues("X-Total-Count").Single());
        using (var doc = JsonDocument.Parse(await list.Content.ReadAsStringAsync()))
        {
            var lodash = doc.RootElement.EnumerateArray().Single(e => e.GetProperty("fingerprint").GetString() == SecurityFixtures.Lodash);
            Assert.Equal("critical", lodash.GetProperty("severity").GetString());
            Assert.Equal("sca", lodash.GetProperty("category").GetString());
            Assert.Equal("4.17.21", lodash.GetProperty("package").GetProperty("fixedVersions")[0].GetString());
            Assert.Equal("new", lodash.GetProperty("state").GetString());
        }
        Assert.Equal(HttpStatusCode.BadRequest, (await bob.GetAsync(baseUrl + "/findings?category=nope")).StatusCode);

        var triage = await bob.PostAsync($"{baseUrl}/findings/{SecurityFixtures.Lodash}/triage",
            new StringContent("""{"state":"accepted-risk","reason":"Not reachable","expires":"2099-01-01T00:00:00Z"}""", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, triage.StatusCode);
        using (var doc = JsonDocument.Parse(await triage.Content.ReadAsStringAsync()))
        {
            Assert.Equal("accepted-risk", doc.RootElement.GetProperty("state").GetString());
            Assert.False(doc.RootElement.GetProperty("open").GetBoolean());
            Assert.Equal("bob", doc.RootElement.GetProperty("triage").GetProperty("user").GetProperty("username").GetString());
        }
        var noReason = await bob.PostAsync($"{baseUrl}/findings/{SecurityFixtures.Secret}/triage",
            new StringContent("""{"state":"false-positive"}""", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, noReason.StatusCode);

        var exported = await bob.GetStringAsync(baseUrl + "/triage");
        Assert.Contains("\"state\": \"accepted-risk\"", exported);
        Assert.Equal("2", (await bob.GetAsync(baseUrl + "/findings?severity=high")).Headers.GetValues("X-Total-Count").Single());

        // Outsiders see nothing.
        var outsider = await app.CreateUserAsync("sec-out-" + Guid.NewGuid().ToString("N")[..6]);
        var dan = await app.ApiClientAsync(outsider.UserName!);
        Assert.Equal(HttpStatusCode.NotFound, (await dan.GetAsync(baseUrl + "/findings")).StatusCode);
    }

    [Fact]
    public async Task Security_page_and_settings_page_render()
    {
        var (project, repo) = await SetupAsync();
        await ScanAsync(repo, "main", "nightowl-main.json");
        var client = await app.CookieClientAsync("carol");
        var page = await client.GetStringAsync($"/p/{project.Key}/repos/{repo.Slug}/security");
        Assert.Contains("Command Injection in lodash", page);
        Assert.Contains("SCA vulnerability", page);
        var detail = await client.GetStringAsync($"/p/{project.Key}/repos/{repo.Slug}/security?finding={SecurityFixtures.Lodash}");
        Assert.Contains("4.17.21", detail);
        var settings = await client.GetStringAsync($"/p/{project.Key}/repos/{repo.Slug}/settings/security");
        Assert.Contains("include: [nightowl]", settings);
        var export = await client.GetAsync($"/p/{project.Key}/repos/{repo.Slug}/security/triage.json");
        Assert.Equal(HttpStatusCode.OK, export.StatusCode);
        Assert.Equal("[]\n", await export.Content.ReadAsStringAsync());
    }
}
