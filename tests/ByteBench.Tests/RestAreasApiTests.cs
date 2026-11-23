using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using ByteBench.Data;
using ByteBench.Services;
using ByteBench.Services.Ai;
using ByteBench.Services.Ci;
using ByteBench.Services.Editor.Ai;
using ByteBench.Services.Pages;
using ByteBench.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ByteBench.Tests;

/// <summary>CI on without a background runner, Pages on, AI on with a fake model and no review worker: queued rows stay queued.</summary>
public class RestAreasApp : CiNoRunnerApp
{
    public FakeEditorChatModel Model { get; } = new();

    protected override void AddSettings(IDictionary<string, string?> settings)
    {
        base.AddSettings(settings);
        settings["Pages:Root"] = Path.Combine(DataRoot, "pages");
        settings["Pages:BaseDomain"] = "pages.example.test";
        settings["Ai:Review:MaxReviewsPerRepoPerHour"] = "4";
        settings["PublicBaseUrl"] = "http://bytebench.test";
    }

    protected override void ConfigureServices(IServiceCollection services)
    {
        base.ConfigureServices(services);
        services.AddSingleton<IEditorChatModel>(Model);
        foreach (var d in services.Where(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(AiReviewWorker)).ToList())
            services.Remove(d);
    }
}

/// <summary>The REST endpoints for pipelines, CI variables, packages, Pages and AI review: permissions, scopes, happy paths, limits.</summary>
public class RestAreasApiTests(RestAreasApp app) : IClassFixture<RestAreasApp>, IDisposable
{
    private readonly string tmp = Path.Combine(Path.GetTempPath(), "bytebench-restareas-tests", Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(tmp, true); } catch (IOException) { } }

    private const string PipelineYaml = "jobs:\n  build: { steps: [ { run: echo build } ] }\n";

    private static readonly SemaphoreSlim OutsiderLock = new(1);

    private sealed record World(Project Project, Repository Repo, string Key, string Base);

    /// <summary>alice owns; bob Developer, carol Maintainer, dan Viewer. Project is private.</summary>
    private async Task<World> WorldAsync(bool withPipelineFile = true)
    {
        var project = await app.CreateProjectAsync("alice", ProjectVisibility.Private,
            ("bob", ProjectRole.Developer), ("carol", ProjectRole.Maintainer), ("dan", ProjectRole.Viewer));
        var repo = await app.CreateRepoAsync(project.Id, "alice");
        app.Commit(repo, "main", "README.md", "# hi\n", from: null, message: "init");
        if (withPipelineFile) app.Commit(repo, "main", CiFile.FileName, PipelineYaml, message: "ci");
        return new World(project, repo, project.Key, $"/api/v1/projects/{project.Key}/repos/{repo.Slug}");
    }

    private async Task<HttpClient> OutsiderAsync()
    {
        await OutsiderLock.WaitAsync();
        try { if (!app.Db().Users.Any(u => u.UserName == "restoutsider")) await app.CreateUserAsync("restoutsider"); }
        finally { OutsiderLock.Release(); }
        return await app.ApiClientAsync("restoutsider");
    }

    private static StringContent Json(object body) => new(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
    private static async Task<JsonElement> Read(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;

    private static async Task<JsonElement> Ok(HttpResponseMessage r, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var text = await r.Content.ReadAsStringAsync();
        Assert.True(r.StatusCode == expected, $"expected {expected}, got {(int)r.StatusCode}: {text}");
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement;
    }

    private static async Task Problem(HttpResponseMessage r, HttpStatusCode expected, string? contains = null)
    {
        var text = await r.Content.ReadAsStringAsync();
        Assert.True(r.StatusCode == expected, $"expected {expected}, got {(int)r.StatusCode}: {text}");
        if (contains != null) Assert.Contains(contains, text, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<List<AuditEvent>> AuditAsync(string action, int projectId)
    {
        await using var db = app.Db();
        return await db.AuditEvents.AsNoTracking().Where(e => e.Action == action && e.ProjectId == projectId).ToListAsync();
    }

    // ===================================================================== pipelines

    private async Task<Pipeline> SeedPipelineAsync(World w, string branch = "main", PipelineStatus status = PipelineStatus.Passed, string log = "hello\nworld\n",
        bool fork = false, bool awaiting = false, string? sha = null)
    {
        await using var db = app.Db();
        var p = new Pipeline
        {
            RepositoryId = w.Repo.Id, Ref = branch, CommitSha = sha ?? new string('a', 40), CommitMessage = "msg", Status = status, Trigger = PipelineTrigger.Push,
            TriggeredById = app.UserId("alice"), IsForkPullRequest = fork, AwaitingApproval = awaiting,
            FinishedAt = status is PipelineStatus.Queued or PipelineStatus.Running ? null : DateTime.UtcNow,
        };
        var build = new PipelineJob { Name = "build", Image = "alpine:3.20", Status = status, Order = 0, FinishedAt = p.FinishedAt };
        build.Steps.Add(new PipelineStep { Name = "compile", Run = "echo hi", Status = status, ExitCode = status == PipelineStatus.Passed ? 0 : null,
            Log = new PipelineStepLog { Text = log } });
        p.Jobs.Add(build);
        db.Pipelines.Add(p);
        await db.SaveChangesAsync();
        return p;
    }

    [Fact]
    public async Task Pipelines_list_filter_and_get_with_jobs()
    {
        var w = await WorldAsync();
        var passed = await SeedPipelineAsync(w, "main", PipelineStatus.Passed);
        var failed = await SeedPipelineAsync(w, "dev", PipelineStatus.Failed);
        var dan = await app.ApiClientAsync("dan");

        var list = await Ok(await dan.GetAsync($"{w.Base}/pipelines"));
        Assert.Equal(2, list.GetArrayLength());
        Assert.Equal(failed.Id, list[0].GetProperty("id").GetInt32()); // newest first
        Assert.Equal(JsonValueKind.Null, list[0].GetProperty("jobs").ValueKind);
        var r = await dan.GetAsync($"{w.Base}/pipelines?perPage=1");
        Assert.Equal("2", r.Headers.GetValues("X-Total-Count").Single());

        var byStatus = await Ok(await dan.GetAsync($"{w.Base}/pipelines?status=Failed"));
        Assert.Equal([failed.Id], byStatus.EnumerateArray().Select(e => e.GetProperty("id").GetInt32()));
        var byRef = await Ok(await dan.GetAsync($"{w.Base}/pipelines?ref=main"));
        Assert.Equal([passed.Id], byRef.EnumerateArray().Select(e => e.GetProperty("id").GetInt32()));
        await Problem(await dan.GetAsync($"{w.Base}/pipelines?status=bogus"), HttpStatusCode.BadRequest);

        var one = await Ok(await dan.GetAsync($"{w.Base}/pipelines/{passed.Id}"));
        Assert.Equal("passed", one.GetProperty("status").GetString());
        Assert.Equal("push", one.GetProperty("trigger").GetString());
        var job = one.GetProperty("jobs")[0];
        Assert.Equal("build", job.GetProperty("name").GetString());
        Assert.Equal("compile", job.GetProperty("steps")[0].GetProperty("name").GetString());
        Assert.Equal("passed", job.GetProperty("steps")[0].GetProperty("status").GetString());
        Assert.StartsWith("http://bytebench.test/p/", one.GetProperty("webUrl").GetString());

        await Problem(await dan.GetAsync($"{w.Base}/pipelines/999999"), HttpStatusCode.NotFound);
        // Another repository's pipeline id is not found here.
        var other = await WorldAsync();
        var foreign = await SeedPipelineAsync(other);
        await Problem(await dan.GetAsync($"{w.Base}/pipelines/{foreign.Id}"), HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Invisible_projects_are_404_and_scopes_are_enforced()
    {
        var w = await WorldAsync();
        var p = await SeedPipelineAsync(w);
        var outsider = await OutsiderAsync();
        foreach (var path in new[] { "/pipelines", $"/pipelines/{p.Id}", $"/pipelines/{p.Id}/artifacts", "/ci/variables" })
            await Problem(await outsider.GetAsync(w.Base + path), HttpStatusCode.NotFound);
        await Problem(await outsider.PostAsync($"{w.Base}/pipelines", Json(new { branch = "main" })), HttpStatusCode.NotFound);
        await Problem(await outsider.GetAsync($"/api/v1/projects/{w.Key}/packages"), HttpStatusCode.NotFound);
        await Problem(await outsider.GetAsync($"{w.Base}/pages"), HttpStatusCode.NotFound);
        await Problem(await outsider.GetAsync($"{w.Base}/ai-review/settings"), HttpStatusCode.NotFound);

        // A read-only token reads but can't mutate; a git-only token can't use the API at all.
        var readOnly = await app.ApiClientAsync("bob", TokenScopes.ReadApi);
        await Ok(await readOnly.GetAsync($"{w.Base}/pipelines"));
        await Problem(await readOnly.PostAsync($"{w.Base}/pipelines", Json(new { branch = "main" })), HttpStatusCode.Forbidden, "write_api");
        await Problem(await readOnly.PostAsync($"{w.Base}/pipelines/{p.Id}/cancel", null), HttpStatusCode.Forbidden, "write_api");
        var gitOnly = await app.ApiClientAsync("bob", TokenScopes.ReadRepository | TokenScopes.WriteRepository);
        await Problem(await gitOnly.GetAsync($"{w.Base}/pipelines"), HttpStatusCode.Forbidden);
        using var anon = app.CreateClient();
        await Problem(await anon.GetAsync($"{w.Base}/pipelines"), HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Step_log_is_text_paged_and_tailed()
    {
        var w = await WorldAsync();
        var text = string.Concat(Enumerable.Range(0, 100).Select(i => $"line {i:D3}\n")); // 900 chars
        var p = await SeedPipelineAsync(w, log: text);
        var stepId = (await Ok(await (await app.ApiClientAsync("dan")).GetAsync($"{w.Base}/pipelines/{p.Id}")))
            .GetProperty("jobs")[0].GetProperty("steps")[0].GetProperty("id").GetInt32();
        var dan = await app.ApiClientAsync("dan");
        var url = $"{w.Base}/pipelines/{p.Id}/steps/{stepId}/log";

        var all = await dan.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, all.StatusCode);
        Assert.StartsWith("text/plain", all.Content.Headers.ContentType!.ToString());
        Assert.Equal(text, await all.Content.ReadAsStringAsync());
        Assert.Equal("900", all.Headers.GetValues("X-Log-Total-Length").Single());
        Assert.False(all.Headers.Contains("X-Log-Next-Offset"));

        var page = await dan.GetAsync($"{url}?offset=0&limit=100");
        Assert.Equal(text[..100], await page.Content.ReadAsStringAsync());
        Assert.Equal("100", page.Headers.GetValues("X-Log-Next-Offset").Single());
        var next = await dan.GetAsync($"{url}?offset=100&limit=100");
        Assert.Equal(text[100..200], await next.Content.ReadAsStringAsync());

        var tail = await dan.GetAsync($"{url}?tail=18");
        Assert.Equal(text[^18..], await tail.Content.ReadAsStringAsync());
        Assert.Equal("882", tail.Headers.GetValues("X-Log-Offset").Single());

        await Problem(await dan.GetAsync($"{url}?tail=0"), HttpStatusCode.BadRequest);
        await Problem(await dan.GetAsync($"{url}?tail=5&offset=1"), HttpStatusCode.BadRequest);
        await Problem(await dan.GetAsync($"{w.Base}/pipelines/{p.Id}/steps/999999/log"), HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Run_pipeline_needs_developer_and_a_real_branch()
    {
        var w = await WorldAsync();
        var bob = await app.ApiClientAsync("bob");
        var created = await bob.PostAsync($"{w.Base}/pipelines", Json(new { branch = "main" }));
        var body = await Ok(created, HttpStatusCode.Created);
        Assert.Equal("main", body.GetProperty("ref").GetString());
        Assert.Equal("manual", body.GetProperty("trigger").GetString());
        Assert.Equal("bob", body.GetProperty("triggeredBy").GetProperty("username").GetString());
        Assert.EndsWith($"/pipelines/{body.GetProperty("id").GetInt32()}", created.Headers.Location!.ToString());
        Assert.Equal("build", body.GetProperty("jobs")[0].GetProperty("name").GetString());

        // No body: the default branch.
        await Ok(await bob.PostAsync($"{w.Base}/pipelines", null), HttpStatusCode.Created);

        await Problem(await (await app.ApiClientAsync("dan")).PostAsync($"{w.Base}/pipelines", Json(new { branch = "main" })), HttpStatusCode.Forbidden);
        await Problem(await bob.PostAsync($"{w.Base}/pipelines", Json(new { branch = "nope" })), HttpStatusCode.UnprocessableEntity, "does not exist");
        await Problem(await bob.PostAsync($"{w.Base}/pipelines", Json(new { branch = "refs/pull/1/head" })), HttpStatusCode.UnprocessableEntity);

        var noFile = await WorldAsync(withPipelineFile: false);
        await Problem(await bob.PostAsync($"{noFile.Base}/pipelines", Json(new { branch = "main" })), HttpStatusCode.UnprocessableEntity, CiFile.FileName);
    }

    [Fact]
    public async Task Cancel_and_rerun()
    {
        var w = await WorldAsync();
        var queued = await SeedPipelineAsync(w, status: PipelineStatus.Queued, sha: app.Commit(w.Repo, "main", "x.txt", "x"));
        var bob = await app.ApiClientAsync("bob");
        await Problem(await (await app.ApiClientAsync("dan")).PostAsync($"{w.Base}/pipelines/{queued.Id}/cancel", null), HttpStatusCode.Forbidden);

        var canceled = await Ok(await bob.PostAsync($"{w.Base}/pipelines/{queued.Id}/cancel", null));
        Assert.Equal("canceled", canceled.GetProperty("status").GetString());
        Assert.Equal("canceled", canceled.GetProperty("jobs")[0].GetProperty("status").GetString());
        await Problem(await bob.PostAsync($"{w.Base}/pipelines/{queued.Id}/cancel", null), HttpStatusCode.UnprocessableEntity, "not running");
        await Problem(await bob.PostAsync($"{w.Base}/pipelines/999999/cancel", null), HttpStatusCode.NotFound);

        var again = await Ok(await bob.PostAsync($"{w.Base}/pipelines/{queued.Id}/rerun", null), HttpStatusCode.Created);
        Assert.NotEqual(queued.Id, again.GetProperty("id").GetInt32());
        Assert.Equal("retry", again.GetProperty("trigger").GetString());
        await Problem(await (await app.ApiClientAsync("dan")).PostAsync($"{w.Base}/pipelines/{queued.Id}/rerun", null), HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Fork_pipelines_wait_for_a_maintainer_to_approve()
    {
        var w = await WorldAsync();
        var p = await SeedPipelineAsync(w, status: PipelineStatus.Queued, fork: true, awaiting: true);
        await Problem(await (await app.ApiClientAsync("bob")).PostAsync($"{w.Base}/pipelines/{p.Id}/approve", null), HttpStatusCode.Forbidden);
        var approved = await Ok(await (await app.ApiClientAsync("carol")).PostAsync($"{w.Base}/pipelines/{p.Id}/approve", null));
        Assert.False(approved.GetProperty("awaitingApproval").GetBoolean());
        Assert.True(approved.GetProperty("forkPullRequest").GetBoolean());
        Assert.Equal("carol", approved.GetProperty("approvedBy").GetProperty("username").GetString());
        await Problem(await (await app.ApiClientAsync("carol")).PostAsync($"{w.Base}/pipelines/{p.Id}/approve", null), HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task Retry_and_play_jobs()
    {
        var w = await WorldAsync();
        Pipeline p;
        await using (var db = app.Db())
        {
            p = new Pipeline { RepositoryId = w.Repo.Id, Ref = "main", CommitSha = new string('b', 40), Status = PipelineStatus.Manual, Trigger = PipelineTrigger.Push };
            var build = new PipelineJob { Name = "build", Image = "alpine:3.20", Status = PipelineStatus.Passed, Order = 0, FinishedAt = DateTime.UtcNow };
            var deploy = new PipelineJob { Name = "deploy", Image = "alpine:3.20", Needs = "build", Order = 1, Status = PipelineStatus.Manual, IsManual = true };
            build.Steps.Add(new PipelineStep { Name = "b", Run = "true", Status = PipelineStatus.Passed });
            deploy.Steps.Add(new PipelineStep { Name = "d", Run = "true" });
            p.Jobs.Add(build);
            p.Jobs.Add(deploy);
            db.Pipelines.Add(p);
            await db.SaveChangesAsync();
        }
        var buildId = p.Jobs.First(j => j.Name == "build").Id;
        var deployId = p.Jobs.First(j => j.Name == "deploy").Id;
        var bob = await app.ApiClientAsync("bob");
        var dan = await app.ApiClientAsync("dan");

        await Problem(await dan.PostAsync($"{w.Base}/pipelines/{p.Id}/jobs/{deployId}/play", null), HttpStatusCode.Forbidden);
        await Problem(await bob.PostAsync($"{w.Base}/pipelines/{p.Id}/jobs/{buildId}/play", null), HttpStatusCode.UnprocessableEntity, "isn't waiting");
        await Problem(await bob.PostAsync($"{w.Base}/pipelines/{p.Id}/jobs/999999/play", null), HttpStatusCode.NotFound);
        var played = await Ok(await bob.PostAsync($"{w.Base}/pipelines/{p.Id}/jobs/{deployId}/play", null));
        Assert.Equal("queued", played.GetProperty("status").GetString());

        // The queued deploy job hasn't finished: can't retry it; the passed build can be retried.
        await Problem(await bob.PostAsync($"{w.Base}/pipelines/{p.Id}/jobs/{deployId}/retry", null), HttpStatusCode.UnprocessableEntity, "hasn't finished");
        await Problem(await dan.PostAsync($"{w.Base}/pipelines/{p.Id}/jobs/{buildId}/retry", null), HttpStatusCode.Forbidden);
        var retried = await Ok(await bob.PostAsync($"{w.Base}/pipelines/{p.Id}/jobs/{buildId}/retry", null));
        Assert.Equal("queued", retried.GetProperty("status").GetString());
        Assert.Equal("queued", retried.GetProperty("steps")[0].GetProperty("status").GetString());
    }

    [Fact]
    public async Task Artifacts_list_inspect_and_download()
    {
        var w = await WorldAsync();
        var p = await SeedPipelineAsync(w);
        int jobId;
        await using (var db = app.Db())
        {
            jobId = await db.PipelineJobs.Where(j => j.PipelineId == p.Id).Select(j => j.Id).FirstAsync();
            var stored = CiStorage.NewStoredName(w.Repo.Id);
            var path = app.Services.GetRequiredService<CiStorage>().ArtifactPath(stored);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
            {
                foreach (var (name, content) in new[] { ("out/app.txt", "artifact body"), ("readme.md", "# r") })
                {
                    using var s = new StreamWriter(zip.CreateEntry(name).Open());
                    s.Write(content);
                }
            }
            db.PipelineArtifacts.Add(new PipelineArtifact { JobId = jobId, PipelineId = p.Id, RepositoryId = w.Repo.Id, StoredName = stored, SizeBytes = new FileInfo(path).Length, FileCount = 2 });
            await db.SaveChangesAsync();
        }
        var dan = await app.ApiClientAsync("dan");

        var list = await Ok(await dan.GetAsync($"{w.Base}/pipelines/{p.Id}/artifacts"));
        Assert.Equal(jobId, list[0].GetProperty("jobId").GetInt32());
        Assert.Equal("build", list[0].GetProperty("jobName").GetString());
        Assert.Equal(2, list[0].GetProperty("fileCount").GetInt32());

        var detail = await Ok(await dan.GetAsync($"{w.Base}/pipelines/{p.Id}/jobs/{jobId}/artifacts"));
        Assert.Equal(["out/app.txt", "readme.md"], detail.GetProperty("files").EnumerateArray().Select(f => f.GetProperty("path").GetString()!).Order());

        var zipResponse = await dan.GetAsync($"{w.Base}/pipelines/{p.Id}/jobs/{jobId}/artifacts.zip");
        Assert.Equal(HttpStatusCode.OK, zipResponse.StatusCode);
        Assert.Equal("attachment", zipResponse.Content.Headers.ContentDisposition!.DispositionType);
        Assert.Equal("nosniff", zipResponse.Headers.GetValues("X-Content-Type-Options").Single());
        using (var archive = new ZipArchive(await zipResponse.Content.ReadAsStreamAsync()))
            Assert.Equal(2, archive.Entries.Count);

        var file = await dan.GetAsync($"{w.Base}/pipelines/{p.Id}/jobs/{jobId}/artifacts/file?path=out/app.txt");
        Assert.Equal("artifact body", await file.Content.ReadAsStringAsync());
        Assert.Equal(13, file.Content.Headers.ContentLength);
        await Problem(await dan.GetAsync($"{w.Base}/pipelines/{p.Id}/jobs/{jobId}/artifacts/file?path=missing.txt"), HttpStatusCode.NotFound);
        await Problem(await dan.GetAsync($"{w.Base}/pipelines/{p.Id}/jobs/{jobId}/artifacts/file?path=../../etc/passwd"), HttpStatusCode.NotFound);
        await Problem(await dan.GetAsync($"{w.Base}/pipelines/{p.Id}/jobs/{jobId}/artifacts/file"), HttpStatusCode.BadRequest);

        var outsider = await OutsiderAsync();
        await Problem(await outsider.GetAsync($"{w.Base}/pipelines/{p.Id}/jobs/{jobId}/artifacts.zip"), HttpStatusCode.NotFound);
        await Problem(await dan.GetAsync($"{w.Base}/pipelines/{p.Id}/jobs/999999/artifacts.zip"), HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Ci_variables_are_maintainer_only_and_never_return_values()
    {
        var w = await WorldAsync();
        const string Secret = "s3cr3t-value-123";
        var carol = await app.ApiClientAsync("carol");
        var bob = await app.ApiClientAsync("bob");
        var url = $"{w.Base}/ci/variables";

        await Problem(await bob.GetAsync(url), HttpStatusCode.Forbidden);
        await Problem(await bob.PostAsync(url, Json(new { name = "TOKEN", value = Secret })), HttpStatusCode.Forbidden);
        await Problem(await (await app.ApiClientAsync("dan")).GetAsync(url), HttpStatusCode.Forbidden);

        var created = await carol.PostAsync(url, Json(new { name = "DEPLOY_TOKEN", value = Secret, @protected = true }));
        var body = await Ok(created, HttpStatusCode.Created);
        Assert.DoesNotContain(Secret, await Task.FromResult(body.GetRawText()));
        Assert.Equal("DEPLOY_TOKEN", body.GetProperty("name").GetString());
        Assert.True(body.GetProperty("protected").GetBoolean());
        Assert.True(body.GetProperty("masked").GetBoolean());
        Assert.Equal("repository", body.GetProperty("scope").GetString());
        var id = body.GetProperty("id").GetInt32();

        var listText = await (await carol.GetAsync(url)).Content.ReadAsStringAsync();
        Assert.DoesNotContain(Secret, listText);
        Assert.DoesNotContain("value", listText, StringComparison.OrdinalIgnoreCase);
        await using (var db = app.Db())
        {
            var row = await db.CiVariables.AsNoTracking().FirstAsync(v => v.Id == id);
            Assert.DoesNotContain(Secret, row.EncryptedValue); // stored encrypted
        }
        Assert.Single(await AuditAsync("ci_variable.created", w.Project.Id));

        // Update: flags only (value kept), then value replaced; neither echoes it.
        var flags = await Ok(await carol.PutAsync($"{url}/{id}", Json(new { @protected = false })));
        Assert.False(flags.GetProperty("protected").GetBoolean());
        Assert.Equal("DEPLOY_TOKEN", flags.GetProperty("name").GetString());
        var renamed = await carol.PutAsync($"{url}/{id}", Json(new { name = "DEPLOY_KEY", value = "another-secret-9" }));
        Assert.DoesNotContain("another-secret-9", await renamed.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);
        Assert.Equal(2, (await AuditAsync("ci_variable.updated", w.Project.Id)).Count);

        await Problem(await carol.PostAsync(url, Json(new { name = "DEPLOY_KEY", value = "x" })), HttpStatusCode.UnprocessableEntity, "already exists");
        await Problem(await carol.PostAsync(url, Json(new { name = "1bad", value = "x" })), HttpStatusCode.UnprocessableEntity);
        await Problem(await carol.PostAsync(url, Json(new { name = "BYTEBENCH_X", value = "x" })), HttpStatusCode.UnprocessableEntity);
        await Problem(await carol.PostAsync(url, Json(new { name = "NOVALUE" })), HttpStatusCode.UnprocessableEntity, "value is required");
        await Problem(await carol.PostAsync(url, Json(new { name = "MULTI", value = "a\nb" })), HttpStatusCode.UnprocessableEntity);
        await Problem(await carol.PutAsync($"{url}/999999", Json(new { value = "x" })), HttpStatusCode.NotFound);

        await Problem(await bob.DeleteAsync($"{url}/{id}"), HttpStatusCode.Forbidden);
        Assert.Equal(HttpStatusCode.NoContent, (await carol.DeleteAsync($"{url}/{id}")).StatusCode);
        await Problem(await carol.DeleteAsync($"{url}/{id}"), HttpStatusCode.NotFound);
        Assert.Single(await AuditAsync("ci_variable.deleted", w.Project.Id));

        // Project-level variables live under the project.
        var purl = $"/api/v1/projects/{w.Key}/ci/variables";
        await Problem(await bob.GetAsync(purl), HttpStatusCode.Forbidden);
        var pc = await Ok(await carol.PostAsync(purl, Json(new { name = "ORG_VAR", value = Secret })), HttpStatusCode.Created);
        Assert.Equal("project", pc.GetProperty("scope").GetString());
        Assert.DoesNotContain(Secret, await (await carol.GetAsync(purl)).Content.ReadAsStringAsync());
        Assert.Empty((await Read(await carol.GetAsync(url))).EnumerateArray()); // not in the repository list
    }

    // ===================================================================== packages

    private async Task<(int PackageId, int OldVersion, int NewVersion)> SeedPackageAsync(World w, PackageType type = PackageType.Npm, string name = "@acme/widget")
    {
        await using var db = app.Db();
        var pkg = new Package { ProjectId = w.Project.Id, Type = type, Name = name, NormalizedName = type == PackageType.Npm ? name : name.ToLowerInvariant() };
        var old = new PackageVersion { Version = "1.0.0", NormalizedVersion = "1.0.0", Size = 10, UpdatedAt = DateTime.UtcNow.AddDays(-2), PublishedById = app.UserId("alice") };
        old.Files.Add(new PackageFile { FileName = "widget-1.0.0.tgz", Sha256 = new string('1', 64), Size = 10 });
        var fresh = new PackageVersion { Version = "2.0.0", NormalizedVersion = "2.0.0", Size = 20, UpdatedAt = DateTime.UtcNow };
        fresh.Files.Add(new PackageFile { FileName = "widget-2.0.0.tgz", Sha256 = new string('2', 64), Size = 20 });
        pkg.Versions.Add(old);
        pkg.Versions.Add(fresh);
        db.Packages.Add(pkg);
        await db.SaveChangesAsync();
        db.PackageDistTags.Add(new PackageDistTag { PackageId = pkg.Id, Tag = "latest", Version = "2.0.0" });
        await db.SaveChangesAsync();
        return (pkg.Id, old.Id, fresh.Id);
    }

    [Fact]
    public async Task Packages_list_filter_get_and_delete()
    {
        var w = await WorldAsync();
        var (npmId, oldVersion, _) = await SeedPackageAsync(w);
        var (nugetId, _, _) = await SeedPackageAsync(w, PackageType.NuGet, "Acme.Lib");
        var dan = await app.ApiClientAsync("dan");
        var bob = await app.ApiClientAsync("bob");
        var carol = await app.ApiClientAsync("carol");
        var url = $"/api/v1/projects/{w.Key}/packages";

        var all = await Ok(await dan.GetAsync(url));
        Assert.Equal(2, all.GetArrayLength());
        Assert.Equal("2", (await dan.GetAsync(url)).Headers.GetValues("X-Total-Count").Single());
        var npm = await Ok(await dan.GetAsync($"{url}?type=npm"));
        Assert.Equal(npmId, Assert.Single(npm.EnumerateArray()).GetProperty("id").GetInt32());
        Assert.Equal("npm", npm[0].GetProperty("type").GetString());
        Assert.Equal("2.0.0", npm[0].GetProperty("latestVersion").GetString());
        Assert.Equal(nugetId, Assert.Single((await Ok(await dan.GetAsync($"{url}?q=acme.l"))).EnumerateArray()).GetProperty("id").GetInt32());
        await Problem(await dan.GetAsync($"{url}?type=gem"), HttpStatusCode.BadRequest);

        var one = await Ok(await dan.GetAsync($"{url}/{npmId}"));
        Assert.Equal("2.0.0", one.GetProperty("versions")[0].GetProperty("version").GetString()); // newest first
        var file = one.GetProperty("versions")[0].GetProperty("files")[0];
        Assert.Equal("widget-2.0.0.tgz", file.GetProperty("fileName").GetString());
        Assert.Contains("/packages/", file.GetProperty("downloadUrl").GetString());
        Assert.Equal("2.0.0", one.GetProperty("distTags").GetProperty("latest").GetString());
        Assert.StartsWith("npm install", one.GetProperty("versions")[0].GetProperty("installCommand").GetString());
        await Problem(await dan.GetAsync($"{url}/999999"), HttpStatusCode.NotFound);

        // Delete: Maintainer only.
        await Problem(await bob.DeleteAsync($"{url}/{npmId}/versions/{oldVersion}"), HttpStatusCode.Forbidden);
        await Problem(await dan.DeleteAsync($"{url}/{npmId}"), HttpStatusCode.Forbidden);
        await Problem(await carol.DeleteAsync($"{url}/{npmId}/versions/999999"), HttpStatusCode.NotFound);
        Assert.Equal(HttpStatusCode.NoContent, (await carol.DeleteAsync($"{url}/{npmId}/versions/{oldVersion}")).StatusCode);
        Assert.Equal(1, (await Ok(await dan.GetAsync($"{url}/{npmId}"))).GetProperty("versions").GetArrayLength());
        Assert.Single(await AuditAsync("package.version_deleted", w.Project.Id));
        Assert.Equal(HttpStatusCode.NoContent, (await carol.DeleteAsync($"{url}/{npmId}")).StatusCode);
        Assert.Single(await AuditAsync("package.deleted", w.Project.Id));
        await Problem(await dan.GetAsync($"{url}/{npmId}"), HttpStatusCode.NotFound);
        await Problem(await carol.DeleteAsync($"{url}/{npmId}"), HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Package_settings_and_cleanup_policy_are_maintainer_only()
    {
        var w = await WorldAsync();
        var (pkgId, _, _) = await SeedPackageAsync(w);
        var url = $"/api/v1/projects/{w.Key}/packages/settings";
        var bob = await app.ApiClientAsync("bob");
        var carol = await app.ApiClientAsync("carol");

        await Problem(await bob.GetAsync(url), HttpStatusCode.Forbidden);
        await Problem(await bob.PutAsync(url, Json(new { enabled = true })), HttpStatusCode.Forbidden);
        var s = await Ok(await carol.GetAsync(url));
        Assert.True(s.GetProperty("enabled").GetBoolean());
        Assert.Equal(JsonValueKind.Null, s.GetProperty("keepLatest").ValueKind);
        Assert.EndsWith($"/packages/{w.Key}/nuget/index.json", s.GetProperty("nuGetSource").GetString());

        await Problem(await carol.PutAsync(url, Json(new { keepLatest = 3 })), HttpStatusCode.UnprocessableEntity, "enabled is required");
        await Problem(await carol.PutAsync(url, Json(new { enabled = true, keepLatest = 0 })), HttpStatusCode.UnprocessableEntity);
        await Problem(await carol.PutAsync(url, Json(new { enabled = true, keepLatest = 5000 })), HttpStatusCode.UnprocessableEntity);

        var saved = await Ok(await carol.PutAsync(url, Json(new { enabled = true, keepLatest = 1 })));
        Assert.Equal(1, saved.GetProperty("keepLatest").GetInt32());
        // The policy ran: only the newest version of the package is left.
        Assert.Equal(1, (await Ok(await carol.GetAsync($"/api/v1/projects/{w.Key}/packages/{pkgId}"))).GetProperty("versions").GetArrayLength());
        Assert.Single(await AuditAsync("package.settings_changed", w.Project.Id));

        var off = await Ok(await carol.PutAsync(url, Json(new { enabled = false })));
        Assert.False(off.GetProperty("enabled").GetBoolean());
        Assert.Equal(JsonValueKind.Null, off.GetProperty("keepLatest").ValueKind);
    }

    // ===================================================================== pages

    private async Task<PagesDeployment> DeployPageAsync(World w, string text, string slot = "", string branch = "main")
    {
        var zip = PagesZip.Make(tmp, ("index.html", $"<h1>{text}</h1>"));
        return await app.Services.GetRequiredService<PagesDeployer>().DeployZipAsync(
            new PagesDeployRequest(w.Repo.Id, slot, branch, new string('c', 40), zip, 1, 1, app.UserId("alice")));
    }

    private async Task EnablePagesAsync(World w, PagesVisibility visibility = PagesVisibility.Members)
    {
        await using var s = app.Scope(app.UserId("alice"));
        await s.Get<PagesService>().UpdateSettingsAsync(w.Repo, new PagesSettingsInput(true, visibility, PagesNotFoundMode.Site404, true, 7));
    }

    [Fact]
    public async Task Pages_status_history_and_rollback()
    {
        var w = await WorldAsync();
        var dan = await app.ApiClientAsync("dan");
        var empty = await Ok(await dan.GetAsync($"{w.Base}/pages"));
        Assert.False(empty.GetProperty("exists").GetBoolean());
        Assert.Equal(JsonValueKind.Null, empty.GetProperty("current").ValueKind);

        await EnablePagesAsync(w);
        var v1 = await DeployPageAsync(w, "one");
        var v2 = await DeployPageAsync(w, "two");
        var review = await DeployPageAsync(w, "feature", slot: "feat-x", branch: "feat/x");

        var site = await Ok(await dan.GetAsync($"{w.Base}/pages"));
        Assert.True(site.GetProperty("exists").GetBoolean());
        Assert.Equal("members", site.GetProperty("settings").GetProperty("visibility").GetString());
        Assert.Equal(2, site.GetProperty("current").GetProperty("version").GetInt32());
        Assert.Equal(v2.Id, site.GetProperty("current").GetProperty("id").GetInt32());
        Assert.Contains(w.Repo.Slug + "." + w.Key.ToLowerInvariant() + ".pages.example.test", site.GetProperty("url").GetString());
        Assert.Equal("feat-x", site.GetProperty("reviewSites")[0].GetProperty("slot").GetString());
        Assert.Equal(review.Id, site.GetProperty("reviewSites")[0].GetProperty("id").GetInt32());

        var history = await Ok(await dan.GetAsync($"{w.Base}/pages/deployments"));
        Assert.Equal(3, history.GetArrayLength());
        var main = await Ok(await dan.GetAsync($"{w.Base}/pages/deployments?slot="));
        Assert.Equal(2, main.GetArrayLength());

        // Rollback: Maintainer only.
        await Problem(await (await app.ApiClientAsync("bob")).PostAsync($"{w.Base}/pages/deployments/{v1.Id}/rollback", null), HttpStatusCode.Forbidden);
        var carol = await app.ApiClientAsync("carol");
        await Problem(await carol.PostAsync($"{w.Base}/pages/deployments/{v2.Id}/rollback", null), HttpStatusCode.UnprocessableEntity, "already live");
        await Problem(await carol.PostAsync($"{w.Base}/pages/deployments/999999/rollback", null), HttpStatusCode.NotFound);
        var rolled = await Ok(await carol.PostAsync($"{w.Base}/pages/deployments/{v1.Id}/rollback", null));
        Assert.Equal(v1.Id, rolled.GetProperty("current").GetProperty("id").GetInt32());
        Assert.True(rolled.GetProperty("current").GetProperty("isRollback").GetBoolean());
    }

    [Fact]
    public async Task Pages_settings_and_delete_need_a_maintainer_and_public_needs_an_owner()
    {
        var w = await WorldAsync();
        await EnablePagesAsync(w);
        await DeployPageAsync(w, "one");
        var url = $"{w.Base}/pages/settings";
        var bob = await app.ApiClientAsync("bob");
        var carol = await app.ApiClientAsync("carol");
        var alice = await app.ApiClientAsync("alice");

        await Problem(await bob.GetAsync(url), HttpStatusCode.Forbidden);
        await Problem(await bob.PutAsync(url, Json(new { enabled = false })), HttpStatusCode.Forbidden);
        Assert.Equal("site404", (await Ok(await carol.GetAsync(url))).GetProperty("notFound").GetString());

        var changed = await Ok(await carol.PutAsync(url, Json(new { notFound = "spaFallback", reviewExpireDays = 3 })));
        Assert.Equal("spaFallback", changed.GetProperty("notFound").GetString());
        Assert.Equal(3, changed.GetProperty("reviewExpireDays").GetInt32());
        Assert.True(changed.GetProperty("enabled").GetBoolean()); // omitted: kept
        Assert.Equal(2, (await AuditAsync("pages.settings_updated", w.Project.Id)).Count); // alice's setup, then this change
        await Problem(await carol.PutAsync(url, Json(new { reviewExpireDays = 0 })), HttpStatusCode.UnprocessableEntity, "1 to 365");
        await Problem(await carol.PutAsync(url, Json(new { visibility = "everyone" })), HttpStatusCode.BadRequest);

        await Problem(await carol.PutAsync(url, Json(new { visibility = "public" })), HttpStatusCode.Forbidden);
        Assert.Equal("public", (await Ok(await alice.PutAsync(url, Json(new { visibility = "public" })))).GetProperty("visibility").GetString());

        await Problem(await bob.DeleteAsync($"{w.Base}/pages"), HttpStatusCode.Forbidden);
        Assert.Equal(HttpStatusCode.NoContent, (await carol.DeleteAsync($"{w.Base}/pages")).StatusCode);
        var after = await Ok(await carol.GetAsync($"{w.Base}/pages"));
        Assert.Equal(JsonValueKind.Null, after.GetProperty("current").ValueKind);
        Assert.Equal("public", after.GetProperty("settings").GetProperty("visibility").GetString()); // settings stay
        Assert.Single(await AuditAsync("pages.deleted", w.Project.Id));
        await Problem(await carol.DeleteAsync($"{w.Base}/pages?slot=nope"), HttpStatusCode.NotFound);
    }

    // ===================================================================== AI review

    private async Task<(PullRequest Pr, string Base)> PullRequestAsync(World w)
    {
        app.Commit(w.Repo, "feature/x", "src/app.cs", "class A {}\n", from: "main", message: "add");
        var bob = app.UserId("bob");
        await using var s = app.Scope(bob);
        var pr = await s.Get<PullRequestService>().CreateAsync(w.Repo, "Add app", "desc", "feature/x", "main", bob, []);
        return (pr, $"{w.Base}/pull-requests/{pr.Number}");
    }

    [Fact]
    public async Task Ai_review_settings_are_maintainer_only_and_validated()
    {
        var w = await WorldAsync();
        var url = $"{w.Base}/ai-review/settings";
        var bob = await app.ApiClientAsync("bob");
        var carol = await app.ApiClientAsync("carol");

        await Problem(await bob.GetAsync(url), HttpStatusCode.Forbidden);
        await Problem(await bob.PutAsync(url, Json(new { enabled = true })), HttpStatusCode.Forbidden);
        var def = await Ok(await carol.GetAsync(url));
        Assert.False(def.GetProperty("enabled").GetBoolean());
        Assert.Equal("onDemand", def.GetProperty("mode").GetString());
        Assert.True(def.GetProperty("availability").GetProperty("available").GetBoolean());
        Assert.Equal(4, def.GetProperty("maxReviewsPerHour").GetInt32());

        var saved = await Ok(await carol.PutAsync(url, Json(new
        {
            enabled = true, mode = "onPush", blockOnUnresolvedFindings = true, focusPrompt = "watch SQL", ignorePaths = new[] { "*.lock", "dist/" },
        })));
        Assert.Equal("onPush", saved.GetProperty("mode").GetString());
        Assert.Equal(["*.lock", "dist/"], saved.GetProperty("ignorePaths").EnumerateArray().Select(e => e.GetString()!));
        Assert.True(saved.GetProperty("blockOnUnresolvedFindings").GetBoolean());
        Assert.Single(await AuditAsync("repository.ai_review_settings_changed", w.Project.Id));
        // Omitted fields are kept.
        var again = await Ok(await carol.PutAsync(url, Json(new { mode = "onDemand" })));
        Assert.True(again.GetProperty("enabled").GetBoolean());
        Assert.Equal("watch SQL", again.GetProperty("focusPrompt").GetString());

        await Problem(await carol.PutAsync(url, Json(new { maxDiffChars = 100 })), HttpStatusCode.UnprocessableEntity, "Max diff size");
        await Problem(await carol.PutAsync(url, Json(new { ignorePaths = new[] { "has space" } })), HttpStatusCode.UnprocessableEntity);
        await Problem(await carol.PutAsync(url, Json(new { mode = "always" })), HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Ai_review_trigger_follows_the_button_rules_and_limits()
    {
        var w = await WorldAsync();
        var (_, url) = await PullRequestAsync(w);
        await using (var db = app.Db()) await AiBot.EnsureAsync(db);
        var bob = await app.ApiClientAsync("bob");
        var dan = await app.ApiClientAsync("dan");

        await Problem(await bob.PostAsync($"{url}/ai-reviews", null), HttpStatusCode.UnprocessableEntity, "turned off");
        await using (var s = app.Scope(app.UserId("alice")))
            await s.Get<AiReviewSettingsService>().SaveAsync(w.Repo.Id, new AiReviewSettingsInput(true, AiReviewMode.OnDemand, 240_000, null, null, false), app.UserId("alice"));

        await Problem(await dan.PostAsync($"{url}/ai-reviews", null), HttpStatusCode.Forbidden);
        var queued = await Ok(await bob.PostAsync($"{url}/ai-reviews", null), HttpStatusCode.Accepted);
        Assert.Equal("queued", queued.GetProperty("status").GetString());
        Assert.Equal("manual", queued.GetProperty("trigger").GetString());
        Assert.Equal("bob", queued.GetProperty("requestedBy").GetProperty("username").GetString());
        var reviewId = queued.GetProperty("id").GetInt32();
        Assert.Single(await AuditAsync("ai_review.queued", w.Project.Id));

        // The same head again: refused with the pending review's id.
        var dup = await bob.PostAsync($"{url}/ai-reviews", null);
        await Problem(dup, HttpStatusCode.Conflict, "already queued");
        Assert.Equal(reviewId, (await Read(dup)).GetProperty("existingReviewId").GetInt32());

        var list = await Ok(await dan.GetAsync($"{url}/ai-reviews"));
        Assert.Equal(reviewId, list[0].GetProperty("id").GetInt32());
        Assert.Equal(0, list[0].GetProperty("findings").GetArrayLength());
        Assert.Equal("queued", (await Ok(await dan.GetAsync($"{url}/ai-reviews/{reviewId}"))).GetProperty("status").GetString());
        await Problem(await dan.GetAsync($"{url}/ai-reviews/999999"), HttpStatusCode.NotFound);

        // Hourly limit (4 here): failed reviews count towards it, and a failed head may be reviewed again.
        await using (var s = app.Scope())
            await s.Get<AiReviewService>().MarkFailedAsync(reviewId, "boom");
        for (var i = 0; i < 3; i++)
        {
            var q = await Ok(await bob.PostAsync($"{url}/ai-reviews", null), HttpStatusCode.Accepted);
            await using var s = app.Scope();
            await s.Get<AiReviewService>().MarkFailedAsync(q.GetProperty("id").GetInt32(), "boom");
        }
        await Problem(await bob.PostAsync($"{url}/ai-reviews", null), HttpStatusCode.TooManyRequests, "limit");

        await Problem(await (await OutsiderAsync()).PostAsync($"{url}/ai-reviews", null), HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Ai_findings_can_be_dismissed_and_brought_back()
    {
        var w = await WorldAsync();
        var (pr, url) = await PullRequestAsync(w);
        int botId, reviewId, findingId, humanId;
        await using (var db = app.Db())
        {
            botId = await AiBot.EnsureAsync(db);
            var review = new AiReview { PullRequestId = pr.Id, HeadSha = new string('d', 40), Status = AiReviewStatus.Completed, Trigger = "manual", FindingsCount = 1, InlineCount = 1,
                RequestedById = app.UserId("bob"), CompletedAt = DateTime.UtcNow };
            db.AiReviews.Add(review);
            await db.SaveChangesAsync();
            reviewId = review.Id;
            var finding = new PullRequestComment { PullRequestId = pr.Id, AuthorId = botId, Body = "**bug**: unchecked input", FilePath = "src/app.cs", Line = 1,
                AiReviewId = reviewId, AiSeverity = "bug", AiFingerprint = new string('f', 40) };
            var human = new PullRequestComment { PullRequestId = pr.Id, AuthorId = app.UserId("bob"), Body = "my note", FilePath = "src/app.cs", Line = 1 };
            var summary = new PullRequestComment { PullRequestId = pr.Id, AuthorId = botId, Body = "Summary text", AiReviewId = reviewId };
            db.PullRequestComments.AddRange(finding, human, summary);
            await db.SaveChangesAsync();
            findingId = finding.Id;
            humanId = human.Id;
        }
        var bob = await app.ApiClientAsync("bob");
        var dan = await app.ApiClientAsync("dan");

        var list = await Ok(await dan.GetAsync($"{url}/ai-reviews"));
        Assert.Equal("Summary text", list[0].GetProperty("summary").GetString());
        var f = list[0].GetProperty("findings")[0];
        Assert.Equal(findingId, f.GetProperty("commentId").GetInt32());
        Assert.Equal("bug", f.GetProperty("severity").GetString());
        Assert.False(f.GetProperty("dismissed").GetBoolean());

        await Problem(await dan.PostAsync($"{url}/ai-findings/{findingId}/dismiss", null), HttpStatusCode.Forbidden);
        await Problem(await bob.PostAsync($"{url}/ai-findings/{humanId}/dismiss", null), HttpStatusCode.UnprocessableEntity, "AI review findings");
        await Problem(await bob.PostAsync($"{url}/ai-findings/999999/dismiss", null), HttpStatusCode.NotFound);
        await Problem(await bob.PostAsync($"{url}/ai-findings/{findingId}/undismiss", null), HttpStatusCode.UnprocessableEntity, "isn't dismissed");

        var dismissed = await Ok(await bob.PostAsync($"{url}/ai-findings/{findingId}/dismiss", null));
        Assert.True(dismissed.GetProperty("dismissed").GetBoolean());
        Assert.True(dismissed.GetProperty("resolved").GetBoolean());
        Assert.Single(await AuditAsync("ai_review.finding_dismissed", w.Project.Id));
        Assert.True((await Ok(await dan.GetAsync($"{url}/ai-reviews/{reviewId}"))).GetProperty("findings")[0].GetProperty("dismissed").GetBoolean());

        await Problem(await dan.PostAsync($"{url}/ai-findings/{findingId}/undismiss", null), HttpStatusCode.Forbidden);
        var back = await Ok(await bob.PostAsync($"{url}/ai-findings/{findingId}/undismiss", null));
        Assert.False(back.GetProperty("dismissed").GetBoolean());
        Assert.False(back.GetProperty("resolved").GetBoolean());
        await Problem(await bob.PostAsync($"{url}/ai-findings/{findingId}/undismiss", null), HttpStatusCode.UnprocessableEntity);
    }

    // ===================================================================== OpenAPI

    [Fact]
    public async Task Every_new_operation_is_documented_in_openapi()
    {
        using var client = app.CreateClient();
        var doc = await Read(await client.GetAsync("/api/v1/openapi.json"));
        var ops = doc.GetProperty("paths").EnumerateObject()
            .SelectMany(p => p.Value.EnumerateObject().Where(m => m.Value.ValueKind == JsonValueKind.Object).Select(m => (Path: p.Name, Method: m.Name, Op: m.Value))).ToList();
        string[] expected =
        [
            "ListPipelines", "RunPipeline", "GetPipeline", "CancelPipeline", "RerunPipeline", "ApprovePipeline", "GetPipelineStepLog", "RetryPipelineJob", "PlayPipelineJob",
            "ListPipelineArtifacts", "GetJobArtifacts", "DownloadJobArtifacts", "DownloadJobArtifactFile",
            "ListRepositoryCiVariables", "CreateRepositoryCiVariable", "UpdateRepositoryCiVariable", "DeleteRepositoryCiVariable",
            "ListProjectCiVariables", "CreateProjectCiVariable", "UpdateProjectCiVariable", "DeleteProjectCiVariable",
            "ListPackages", "GetPackage", "DeletePackage", "DeletePackageVersion", "GetPackageSettings", "SetPackageSettings",
            "GetPagesSite", "DeletePagesSite", "ListPagesDeployments", "RollbackPagesDeployment", "GetPagesSettings", "SetPagesSettings",
            "GetAiReviewSettings", "SetAiReviewSettings", "ListAiReviews", "GetAiReview", "RequestAiReview", "DismissAiFinding", "UndismissAiFinding",
        ];
        foreach (var name in expected)
        {
            var hit = ops.SingleOrDefault(o => o.Op.TryGetProperty("operationId", out var id) && id.GetString() == name);
            Assert.True(hit.Op.ValueKind == JsonValueKind.Object, $"{name} is missing from the OpenAPI document");
            Assert.False(string.IsNullOrWhiteSpace(hit.Op.GetProperty("summary").GetString()), $"{name} has no summary");
        }
    }
}
