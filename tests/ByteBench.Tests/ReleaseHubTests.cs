using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ByteBench.Data;
using ByteBench.Services;
using ByteBench.Services.Ci;
using ByteBench.Services.Releases;
using ByteBench.Services.Tests;
using ByteBench.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace ByteBench.Tests;

/// <summary>The release hub: what is gathered for a tag, CI artifact auto-attach, checksums, permissions, and CI test report ingestion.</summary>
public class ReleaseHubTests(TestApp app) : IClassFixture<TestApp>
{
    private sealed record Setup(Project Project, Repository Repo, string Dev, string Maintainer, string Viewer, string Outsider, int ReleaseId, string Sha);

    private async Task<Setup> SetupAsync(string tag = "v1.2.0")
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        string dev = "hdev" + suffix, maint = "hmaint" + suffix, viewer = "hview" + suffix, outsider = "hout" + suffix;
        foreach (var u in new[] { dev, maint, viewer, outsider }) await app.CreateUserAsync(u);
        var project = await app.CreateProjectAsync("alice", ProjectVisibility.Private, (dev, ProjectRole.Developer), (maint, ProjectRole.Maintainer), (viewer, ProjectRole.Viewer));
        var repo = await app.CreateRepoAsync(project.Id, "alice");
        await using var s = app.Scope(app.UserId("alice"));
        var release = await s.Get<ReleaseService>().CreateAsync(repo, new ReleaseInput { TagName = tag, Target = "main", Title = "Release " + tag }, app.UserId("alice"));
        var sha = (await s.Get<ReleaseAutoAttachService>().TagShaAsync(repo, tag))!;
        return new Setup(project, repo, dev, maint, viewer, outsider, release.Id, sha);
    }

    /// <summary>A passed pipeline for the commit with one job whose artifact zip holds <paramref name="entries"/>.</summary>
    private async Task<int> PipelineWithArtifactAsync(Setup t, params (string Name, string Content)[] entries)
    {
        await using var db = app.Db();
        var p = new Pipeline { RepositoryId = t.Repo.Id, Ref = "main", CommitSha = t.Sha, Status = PipelineStatus.Passed };
        p.Jobs.Add(new PipelineJob { Name = "build", Image = "x", Status = PipelineStatus.Passed });
        db.Pipelines.Add(p);
        await db.SaveChangesAsync();
        var stored = CiStorage.NewStoredName(t.Repo.Id);
        var path = Path.Combine(app.DataRoot, "ci-artifacts", stored);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
            foreach (var (name, content) in entries)
                using (var w = new StreamWriter(zip.CreateEntry(name).Open())) w.Write(content);
        db.PipelineArtifacts.Add(new PipelineArtifact { JobId = p.Jobs[0].Id, PipelineId = p.Id, RepositoryId = t.Repo.Id, StoredName = stored, SizeBytes = new FileInfo(path).Length, FileCount = entries.Length });
        await db.SaveChangesAsync();
        return p.Id;
    }

    [Fact]
    public async Task Auto_attach_copies_only_matching_files_skips_duplicates_and_needs_the_rule()
    {
        var t = await SetupAsync();
        await PipelineWithArtifactAsync(t, ("dist/app-1.2.0.zip", "zipdata"), ("dist/notes.txt", "n"), ("bom/app.cdx.json", "{}"), ("../evil.zip", "x"), ("src/deep/other.zip", "o"));
        await using var s = app.Scope(app.UserId(t.Dev));
        var hub = s.Get<ReleaseHubService>();

        // Without a rule nothing is attached by the publish path; the manual button reports there are no patterns.
        Assert.Equal(0, (await hub.AttachNowAsync(t.Repo, t.ReleaseId)).Attached);
        await Assert.ThrowsAsync<ForbiddenException>(() => hub.SaveAutoAttachAsync(t.Repo, true, "dist/*.zip")); // Developer cannot manage repositories

        await using (var m = app.Scope(app.UserId(t.Maintainer)))
        {
            var mh = m.Get<ReleaseHubService>();
            await Assert.ThrowsAsync<InvalidOperationException>(() => mh.SaveAutoAttachAsync(t.Repo, true, "../etc/*"));
            await mh.SaveAutoAttachAsync(t.Repo, true, "dist/*.zip\n**/*.cdx.json");
        }
        var r = await hub.AttachNowAsync(t.Repo, t.ReleaseId);
        Assert.Equal(2, r.Attached);
        Assert.Equivalent(new[] { "app-1.2.0.zip", "app.cdx.json" }, r.Names);
        Assert.Equal(0, (await hub.AttachNowAsync(t.Repo, t.ReleaseId)).Attached); // second time: already there

        var view = (await hub.GetAsync(t.Repo, t.ReleaseId))!;
        Assert.Equal(t.Sha, view.CommitSha);
        Assert.Contains(view.Release.Assets, a => a.FileName == "app-1.2.0.zip" && a.Sha256 == Convert.ToHexStringLower(SHA256.HashData("zipdata"u8)));
        Assert.Single(view.Sboms);
        Assert.Single(view.Pipelines);
        Assert.Equal("build", view.Pipelines[0].Artifacts.Single().JobName);

        var sums = await hub.ChecksumsAsync(t.Repo, t.ReleaseId);
        Assert.Contains("  app-1.2.0.zip\n", sums);
        Assert.Contains(Convert.ToHexStringLower(SHA256.HashData("zipdata"u8)), sums);
    }

    [Fact]
    public async Task Publishing_a_draft_attaches_artifacts_and_a_later_passing_pipeline_does_too()
    {
        var t = await SetupAsync("v2.0.0");
        await using (var m = app.Scope(app.UserId(t.Maintainer))) await m.Get<ReleaseHubService>().SaveAutoAttachAsync(t.Repo, true, "*.nupkg");
        await PipelineWithArtifactAsync(t, ("App.2.0.0.nupkg", "pkg"));

        await using var s = app.Scope(app.UserId("alice"));
        var releases = s.Get<ReleaseService>();
        var draft = await releases.CreateAsync(t.Repo, new ReleaseInput { TagName = "v2.0.0-draft", Target = "main", Title = "Draft", IsDraft = true }, app.UserId("alice"));
        Assert.Empty((await releases.GetAsync(t.Repo, draft.Id))!.Assets); // drafts don't attach until published
        await releases.PublishAsync(t.Repo, draft.Id, app.UserId("alice"));
        Assert.Contains((await releases.GetAsync(t.Repo, draft.Id))!.Assets, a => a.FileName == "App.2.0.0.nupkg");

        // A pipeline that passes after the release exists.
        var late = await PipelineWithArtifactAsync(t, ("Late.nupkg", "late"));
        await s.Get<ReleaseAutoAttachService>().OnPipelineFinishedAsync(late);
        Assert.Contains((await releases.GetAsync(t.Repo, t.ReleaseId))!.Assets, a => a.FileName == "Late.nupkg");
    }

    [Fact]
    public async Task Hub_gathers_packages_issues_tests_and_security_for_the_tag()
    {
        var t = await SetupAsync("v3.0.0");
        int pipelineId = await PipelineWithArtifactAsync(t, ("x.txt", "x"));
        await using (var db = app.Db())
        {
            var pkg = new Package { ProjectId = t.Project.Id, Type = PackageType.NuGet, Name = "App", NormalizedName = "app" };
            pkg.Versions.Add(new PackageVersion { Version = "3.0.0", NormalizedVersion = "3.0.0" });
            pkg.Versions.Add(new PackageVersion { Version = "9.9.9", NormalizedVersion = "9.9.9", PipelineId = pipelineId });
            var other = new Package { ProjectId = t.Project.Id, Type = PackageType.Npm, Name = "unrelated", NormalizedName = "unrelated" };
            other.Versions.Add(new PackageVersion { Version = "0.0.1", NormalizedVersion = "0.0.1" });
            db.Packages.AddRange(pkg, other);
            var version = new ProjectVersion { ProjectId = t.Project.Id, Name = "3.0.0" };
            db.ProjectVersions.Add(version);
            await db.SaveChangesAsync();
            var issue = await app.CreateIssueAsync(t.Project.Id, "alice", "Fixed in three");
            db.IssueVersions.Add(new IssueVersion { IssueId = issue.Id, VersionId = version.Id, Kind = VersionLinkKind.Fix });
            db.SecurityScans.Add(new SecurityScan { RepositoryId = t.Repo.Id, PipelineId = pipelineId, JobId = 1, Ref = "main", CommitSha = t.Sha, High = 0, Critical = 0 });
            await db.SaveChangesAsync();
        }
        await using (var s = app.Scope(app.UserId(t.Dev)))
        {
            var tests = s.Get<TestService>();
            await tests.IngestAsync(t.Project.Id, new TestIngestInput { Content = """<testsuite><testcase name="a"/><testcase name="b"><failure/></testcase></testsuite>""", Tag = "v3.0.0" });
        }

        await using var v = app.Scope(app.UserId(t.Viewer));
        var hub = (await v.Get<ReleaseHubService>().GetAsync(t.Repo, t.ReleaseId))!;
        Assert.Equal(new[] { "3.0.0", "9.9.9" }, hub.Packages.Select(p => p.Version).Order());
        Assert.DoesNotContain(hub.Packages, p => p.Name == "unrelated");
        Assert.Contains(hub.Issues, i => i.Title == "Fixed in three" && i.Via == "fix version");
        Assert.NotNull(hub.Security);
        var run = Assert.Single(hub.TestRuns);
        Assert.Equal((1, 1), (run.Summary.Passed, run.Summary.Failed));
    }

    [Fact]
    public async Task Rest_hub_checksums_settings_and_permissions()
    {
        var t = await SetupAsync("v4.0.0");
        await PipelineWithArtifactAsync(t, ("dist/a.zip", "a"));
        var dev = await app.ApiClientAsync(t.Dev);
        var maint = await app.ApiClientAsync(t.Maintainer);
        var viewer = await app.ApiClientAsync(t.Viewer);
        var outsider = await app.ApiClientAsync(t.Outsider);
        var repoRoot = $"/api/v1/projects/{t.Project.Key}/repos/{t.Repo.Slug}";

        Assert.Equal(HttpStatusCode.NotFound, (await outsider.GetAsync($"{repoRoot}/releases/{t.ReleaseId}/hub")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.GetAsync($"/api/v1/projects/{t.Project.Key}/releases")).StatusCode);
        var hub = JsonDocument.Parse(await (await viewer.GetAsync($"{repoRoot}/releases/{t.ReleaseId}/hub")).Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(t.Sha, hub.GetProperty("commitSha").GetString());
        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync($"{repoRoot}/releases/{t.ReleaseId}/checksums")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync($"/api/v1/projects/{t.Project.Key}/releases")).StatusCode);

        var put = new StringContent("""{"autoAttachEnabled":true,"patterns":["dist/*.zip"]}""", Encoding.UTF8, "application/json");
        Assert.Equal(HttpStatusCode.Forbidden, (await dev.PutAsync($"{repoRoot}/release-settings", put)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await maint.PutAsync($"{repoRoot}/release-settings",
            new StringContent("""{"autoAttachEnabled":true,"patterns":["dist/*.zip"]}""", Encoding.UTF8, "application/json"))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await maint.PutAsync($"{repoRoot}/release-settings",
            new StringContent("""{"autoAttachEnabled":true,"patterns":["/etc/passwd"]}""", Encoding.UTF8, "application/json"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsync($"{repoRoot}/releases/{t.ReleaseId}/attach-artifacts", new StringContent("{}", Encoding.UTF8, "application/json"))).StatusCode);
        var attached = await dev.PostAsync($"{repoRoot}/releases/{t.ReleaseId}/attach-artifacts", new StringContent("{}", Encoding.UTF8, "application/json"));
        Assert.Equal(1, JsonDocument.Parse(await attached.Content.ReadAsStringAsync()).RootElement.GetProperty("attached").GetInt32());
    }

    // ---------------------------------------------------------------- CI test reports

    [Fact]
    public async Task Ci_test_reports_are_read_from_the_workspace_by_name_or_pattern_and_links_are_refused()
    {
        var t = await SetupAsync("v5.0.0");
        var workspace = Path.Combine(app.DataRoot, "ws-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(workspace, "out"));
        File.WriteAllText(Path.Combine(workspace, "out", "a.xml"), """<testsuite><testcase classname="X" name="one"/></testsuite>""");
        File.WriteAllText(Path.Combine(workspace, "out", "b.xml"), """<testsuite><testcase classname="X" name="two"><failure message="no"/></testcase></testsuite>""");
        var outside = Path.Combine(app.DataRoot, "outside.xml");
        File.WriteAllText(outside, """<testsuite><testcase name="leak"/></testsuite>""");
        File.CreateSymbolicLink(Path.Combine(workspace, "link.xml"), outside);

        Pipeline p;
        await using (var db = app.Db())
        {
            p = new Pipeline { RepositoryId = t.Repo.Id, Ref = "main", CommitSha = t.Sha, Status = PipelineStatus.Failed };
            p.Jobs.Add(new PipelineJob { Name = "test", Image = "x" });
            db.Pipelines.Add(p);
            await db.SaveChangesAsync();
        }
        await using var s = app.Scope();
        var notes = await s.Get<TestReportIngestor>().IngestWorkspaceAsync(p, p.Jobs[0], workspace,
            new Dictionary<string, string> { ["junit"] = "out/*.xml", ["trx"] = "link.xml", ["xunit"] = "missing.xml" });
        Assert.Contains(notes, n => n.Contains("out/a.xml") && n.Contains("1 passed"));
        Assert.Contains(notes, n => n.Contains("out/b.xml") && n.Contains("1 failed"));
        Assert.Contains(notes, n => n.Contains("link.xml") && n.Contains("symbolic link"));
        Assert.Contains(notes, n => n.Contains("missing.xml"));

        await using var check = app.Db();
        var run = await check.TestRuns.SingleAsync(r => r.PipelineId == p.Id);
        Assert.Equal((TestRunSource.Ci, t.Sha, true), (run.Source, run.CommitSha, run.IsClosed));
        Assert.Equal(2, await check.TestResults.CountAsync(r => r.RunId == run.Id));
        Assert.False(await check.TestCases.AnyAsync(c => c.ProjectId == t.Project.Id && c.Title == "leak"));
    }

    [Fact]
    public void Ci_file_accepts_test_report_types_with_patterns_but_not_for_security_reports()
    {
        const string ok = """
            jobs:
              test:
                image: mcr.microsoft.com/dotnet/sdk
                steps: [ { run: "dotnet test" } ]
                artifacts:
                  paths: [ out/ ]
                  reports: { junit: "out/**/*.xml", trx: "TestResults/a.trx" }
            """;
        var parsed = CiFile.Parse(ok);
        Assert.True(parsed.Ok, parsed.Error);
        var bad = ok.Replace("reports: { junit: \"out/**/*.xml\", trx: \"TestResults/a.trx\" }", "reports: { sarif: \"*.sarif\" }");
        Assert.False(CiFile.Parse(bad).Ok);
        Assert.False(CiFile.Parse(ok.Replace("junit:", "tap:")).Ok);
    }
}
