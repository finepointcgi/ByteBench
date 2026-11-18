using System.IO.Compression;
using GitBench.Data;
using GitBench.Services;
using GitBench.Services.Ci;
using GitBench.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace GitBench.Tests;

/// <summary>The .gitbench-ci.yml extensions: artifacts, cache, when/allow_failure, rules/only, environment.</summary>
public class CiFileExtrasTests
{
    private static CiDefinition Ok(string yaml)
    {
        var r = CiFile.Parse(yaml);
        Assert.True(r.Ok, r.Error);
        return r.Definition!;
    }

    private static string Err(string yaml)
    {
        var r = CiFile.Parse(yaml);
        Assert.False(r.Ok, "expected a parse error");
        return r.Error!;
    }

    [Fact]
    public void Old_files_still_parse_with_defaults()
    {
        var d = Ok("""
            jobs:
              build:
                steps: [ { run: make } ]
            """);
        var j = d.Jobs.Single();
        Assert.Equal(CiJobWhen.OnSuccess, j.When);
        Assert.False(j.AllowFailure);
        Assert.Null(j.Artifacts);
        Assert.Null(j.Cache);
        Assert.Null(j.Environment);
        Assert.Empty(j.Rules);
        Assert.Equal(CiJobWhen.OnSuccess, j.WhenFor("any/branch"));
    }

    [Fact]
    public void Parses_every_new_key()
    {
        var d = Ok("""
            jobs:
              build:
                cache: { key: "npm-{{ hashFiles('package-lock.json', \"web/*.json\") }}", paths: [node_modules/] }
                artifacts: { paths: [dist/, "*.log"], expire_in: 7d, when: always }
                steps: [ { run: make } ]
              deploy:
                needs: [build]
                when: manual
                allow_failure: false
                only: [main, "release/*"]
                environment: { name: production, url: "https://example.com/app" }
                steps: [ { run: ./deploy.sh } ]
              lint:
                allow_failure: true
                rules:
                  - branches: [main]
                    when: never
                  - branches: ["feature/**"]
                    when: manual
                  - when: on_success
                steps: [ { run: lint } ]
            """);
        var build = d.Jobs.Single(j => j.Name == "build");
        Assert.Equal(["dist/", "*.log"], build.Artifacts!.Paths);
        Assert.Equal(TimeSpan.FromDays(7), build.Artifacts.ExpireIn);
        Assert.Equal(CiArtifactsWhen.Always, build.Artifacts.When);
        Assert.Equal(["node_modules/"], build.Cache!.Paths);
        Assert.StartsWith("npm-{{", build.Cache.Key);

        var deploy = d.Jobs.Single(j => j.Name == "deploy");
        Assert.Equal(CiJobWhen.Manual, deploy.When);
        Assert.False(deploy.AllowFailure); // explicit false: blocks the pipeline
        Assert.Equal("production", deploy.Environment!.Name);
        Assert.Equal("https://example.com/app", deploy.Environment.Url);
        Assert.Equal(CiJobWhen.Manual, deploy.WhenFor("main"));
        Assert.Equal(CiJobWhen.Manual, deploy.WhenFor("release/1.0"));
        Assert.Null(deploy.WhenFor("feature/x"));

        var lint = d.Jobs.Single(j => j.Name == "lint");
        Assert.True(lint.AllowFailure);
        Assert.Null(lint.WhenFor("main"));
        Assert.Equal(CiJobWhen.Manual, lint.WhenFor("feature/a/b"));
        Assert.Equal(CiJobWhen.OnSuccess, lint.WhenFor("dev"));
    }

    [Fact]
    public void Manual_jobs_default_to_allow_failure_and_expiry_defaults_to_30_days()
    {
        var d = Ok("""
            jobs:
              a:
                when: manual
                artifacts: { paths: [out] }
                steps: [ { run: x } ]
              b:
                artifacts: { paths: [out], expire_in: never }
                steps: [ { run: x } ]
            """);
        Assert.True(d.Jobs[0].AllowFailure);
        Assert.Equal(CiFile.DefaultArtifactExpiry, d.Jobs[0].Artifacts!.ExpireIn);
        Assert.Equal(CiArtifactsWhen.OnSuccess, d.Jobs[0].Artifacts!.When);
        Assert.Null(d.Jobs[1].Artifacts!.ExpireIn);
    }

    [Theory]
    [InlineData("artifacts: { paths: [dist], expire: 7d }", "Property 'expire' not found")]
    [InlineData("artifacts: { paths: [] }", "needs at least one path")]
    [InlineData("artifacts: { paths: [../secret] }", "without '.', '..'")]
    [InlineData("artifacts: { paths: [/etc/passwd] }", "relative to the workspace")]
    [InlineData("artifacts: { paths: [dist], expire_in: soon }", "expire_in: soon")]
    [InlineData("artifacts: { paths: [dist], when: sometimes }", "on_success, on_failure or always")]
    [InlineData("cache: { key: 'a b', paths: [x] }", "letters, digits")]
    [InlineData("cache: { key: \"{{ branch }}\", paths: [x] }", "only {{ hashFiles")]
    [InlineData("cache: { key: \"{{ hashFiles(lock) }}\", paths: [x] }", "quoted file patterns")]
    [InlineData("cache: { key: \"{{ hashFiles('../x') }}\", paths: [x] }", "hashFiles pattern")]
    [InlineData("cache: { key: k }", "cache 'paths'")]
    [InlineData("when: later", "on_success or manual")]
    [InlineData("allow_failure: maybe", "")]
    [InlineData("only: [main]\n    rules: [ { when: never } ]", "either 'only' or 'rules'")]
    [InlineData("rules: [ { when: sometimes } ]", "on_success, manual or never")]
    [InlineData("rules: [ { branch: main } ]", "Property 'branch' not found")]
    [InlineData("environment: { name: '-prod' }", "environment name")]
    [InlineData("environment: { name: prod, url: 'ftp://x' }", "absolute http or https")]
    [InlineData("environment: { name: prod, token: x }", "Property 'token' not found")]
    public void Rejects_invalid_new_keys(string jobYaml, string expected)
    {
        var error = Err($$"""
            jobs:
              j:
                {{jobYaml}}
                steps: [ { run: x } ]
            """);
        Assert.Contains(expected, error);
    }

    [Fact]
    public void ForBranch_drops_excluded_jobs_and_rejects_needs_on_them()
    {
        var d = Ok("""
            jobs:
              build: { steps: [ { run: b } ] }
              deploy:
                needs: [build]
                only: [main]
                steps: [ { run: d } ]
            """);
        Assert.Equal(["build", "deploy"], d.ForBranch("main").Jobs.Select(j => j.Name));
        Assert.Equal(["build"], d.ForBranch("dev").Jobs.Select(j => j.Name));

        var bad = Ok("""
            jobs:
              build: { only: [main], steps: [ { run: b } ] }
              test: { needs: [build], steps: [ { run: t } ] }
            """);
        Assert.Contains("needs 'build', which doesn't run on branch 'dev'", bad.ForBranch("dev").Error);
        Assert.Null(bad.ForBranch("main").Error);
    }

    [Theory]
    [InlineData("30m", 30.0 / 60 / 24)]
    [InlineData("12h", 0.5)]
    [InlineData("7d", 7)]
    [InlineData("2 weeks", 14)]
    [InlineData("3mo", 90)]
    [InlineData("1y", 365)]
    public void Parses_durations(string text, double days)
    {
        Assert.True(CiFile.TryParseDuration(text, out var v));
        Assert.Equal(days, v!.Value.TotalDays, 3);
    }

    [Fact]
    public void Rejects_bad_durations()
    {
        Assert.False(CiFile.TryParseDuration("0d", out _));
        Assert.False(CiFile.TryParseDuration("20y", out _));
        Assert.False(CiFile.TryParseDuration("7 parsecs", out _));
        Assert.True(CiFile.TryParseDuration("never", out var never));
        Assert.Null(never);
    }
}

/// <summary>Collecting and extracting artifacts without leaving the workspace; cache keys.</summary>
public sealed class CiArtifactFileTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "gitbench-ci-tests", Guid.NewGuid().ToString("N"));
    private string Ws => Path.Combine(root, "ws");
    private string Outside => Path.Combine(root, "outside");

    public CiArtifactFileTests()
    {
        Directory.CreateDirectory(Ws);
        Directory.CreateDirectory(Outside);
        File.WriteAllText(Path.Combine(Outside, "secret.txt"), "host secret");
    }

    public void Dispose() => TestApp.DeleteDirectory(root);

    private void Write(string rel, string text)
    {
        var p = Path.Combine(Ws, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, text);
    }

    [Theory]
    [InlineData("dist/", "dist/a.js", true)]
    [InlineData("dist/", "dist/sub/b.js", true)]
    [InlineData("dist/", "dist", false)]
    [InlineData("dist", "dist/a.js", true)]
    [InlineData("*.log", "build.log", true)]
    [InlineData("*.log", "logs/build.log", false)]
    [InlineData("**/*.log", "logs/build.log", true)]
    [InlineData("out/*.txt", "out/x/y.txt", false)]
    public void Matches_patterns(string pattern, string path, bool expected) => Assert.Equal(expected, CiArtifacts.Matches(pattern, path));

    [Fact]
    public void Collect_never_follows_symlinks_out_of_the_workspace()
    {
        Write("dist/app.js", "ok");
        Write("build.log", "log");
        File.CreateSymbolicLink(Path.Combine(Ws, "dist", "leak.txt"), Path.Combine(Outside, "secret.txt"));
        Directory.CreateSymbolicLink(Path.Combine(Ws, "dist", "hostdir"), Outside);
        File.CreateSymbolicLink(Path.Combine(Ws, "linked"), Outside); // a top-level link matched by a pattern

        var zip = Path.Combine(root, "a.zip");
        var r = CiArtifacts.CollectToZip(Ws, ["dist/", "*.log", "linked/"], zip, 1024 * 1024);

        Assert.True(r.Ok, r.Error);
        Assert.Equal(["build.log", "dist/app.js"], CiArtifacts.ListZip(zip).Select(f => f.Path).Order(StringComparer.Ordinal));
        Assert.Contains(r.Notes, n => n.Contains("dist/leak.txt"));
        Assert.Contains(r.Notes, n => n.Contains("dist/hostdir"));
    }

    [Fact]
    public void Collect_enforces_the_size_limit()
    {
        Write("big.bin", new string('x', 5000));
        var zip = Path.Combine(root, "big.zip");
        var r = CiArtifacts.CollectToZip(Ws, ["big.bin"], zip, 1000);
        Assert.False(r.Ok);
        Assert.Contains("over the", r.Error);
        Assert.False(File.Exists(zip));
    }

    [Fact]
    public void Extract_refuses_traversal_and_writing_through_symlinks()
    {
        var zip = Path.Combine(root, "evil.zip");
        using (var z = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            foreach (var name in new[] { "../escaped.txt", "/abs.txt", "dist/secret.txt", "linkfile", "good/ok.txt" })
            {
                using var w = new StreamWriter(z.CreateEntry(name).Open());
                w.Write("pwned " + name);
            }
        }
        // The repository (or an earlier step) planted links into the host.
        Directory.CreateSymbolicLink(Path.Combine(Ws, "dist"), Outside);
        File.CreateSymbolicLink(Path.Combine(Ws, "linkfile"), Path.Combine(Outside, "secret.txt"));

        var r = CiArtifacts.ExtractSafely(zip, Ws, 1024 * 1024);

        Assert.Equal(1, r.FileCount);
        Assert.Equal("pwned good/ok.txt", File.ReadAllText(Path.Combine(Ws, "good", "ok.txt")));
        Assert.Equal("host secret", File.ReadAllText(Path.Combine(Outside, "secret.txt")));
        Assert.False(File.Exists(Path.Combine(root, "escaped.txt")));
        Assert.Equal(4, r.Notes.Count);
    }

    [Fact]
    public void Collect_and_extract_round_trip_keeps_the_executable_bit()
    {
        if (OperatingSystem.IsWindows()) return;
        Write("bin/tool", "#!/bin/sh");
        File.SetUnixFileMode(Path.Combine(Ws, "bin/tool"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var zip = Path.Combine(root, "x.zip");
        Assert.True(CiArtifacts.CollectToZip(Ws, ["bin/"], zip, 1 << 20).Ok);
        var target = Path.Combine(root, "ws2");
        Directory.CreateDirectory(target);
        CiArtifacts.ExtractSafely(zip, target, 1 << 20);
        Assert.True(File.GetUnixFileMode(Path.Combine(target, "bin/tool")).HasFlag(UnixFileMode.UserExecute));
    }

    [Fact]
    public void Cache_keys_hash_the_matching_files()
    {
        Write("package-lock.json", "{ \"v\": 1 }");
        const string template = "npm-{{ hashFiles('package-lock.json') }}";
        Assert.Null(CiCacheKey.Validate(template));
        var k1 = CiCacheKey.Resolve(template, Ws);
        Assert.Matches("^npm-[0-9a-f]{20}$", k1);
        Assert.Equal(k1, CiCacheKey.Resolve(template, Ws));
        Write("package-lock.json", "{ \"v\": 2 }");
        Assert.NotEqual(k1, CiCacheKey.Resolve(template, Ws));
        Assert.Equal("plain-key", CiCacheKey.Resolve("plain-key", Ws));
        Assert.NotNull(CiCacheKey.Validate("bad key"));
    }
}

/// <summary>The cron parser.</summary>
public class CiCronTests
{
    private static DateTime T(string s) => DateTime.SpecifyKind(DateTime.Parse(s, System.Globalization.CultureInfo.InvariantCulture), DateTimeKind.Utc);

    [Theory]
    [InlineData("* * * * *", "2026-01-01 10:00", "2026-01-01 10:01")]
    [InlineData("*/15 * * * *", "2026-01-01 10:07", "2026-01-01 10:15")]
    [InlineData("0 3 * * *", "2026-01-01 03:00", "2026-01-02 03:00")]
    [InlineData("30 2 1 * *", "2026-01-15 00:00", "2026-02-01 02:30")]
    [InlineData("0 9 * * mon-fri", "2026-01-02 10:00", "2026-01-05 09:00")] // Fri → Mon
    [InlineData("0 0 * * 7", "2026-01-01 00:00", "2026-01-04 00:00")] // 7 = Sunday
    [InlineData("0 0 29 2 *", "2026-01-01 00:00", "2028-02-29 00:00")]
    [InlineData("0 0 13 * fri", "2026-01-01 00:00", "2026-01-02 00:00")] // dom OR dow
    [InlineData("@hourly", "2026-01-01 10:30", "2026-01-01 11:00")]
    [InlineData("@weekly", "2026-01-01 00:00", "2026-01-04 00:00")]
    [InlineData("5,35 1-2 * jan,dec *", "2026-01-01 02:40", "2026-01-02 01:05")]
    [InlineData("5,35 1-2 * jan,dec *", "2026-01-31 02:40", "2026-12-01 01:05")]
    [InlineData("5/20 * * * *", "2026-01-01 10:06", "2026-01-01 10:25")]
    public void Next_finds_the_following_match(string expr, string after, string expected) =>
        Assert.Equal(T(expected), CiCron.Parse(expr).Next(T(after)));

    [Theory]
    [InlineData("* * * *", "5 fields")]
    [InlineData("60 * * * *", "out of range")]
    [InlineData("* 24 * * *", "out of range")]
    [InlineData("* * 0 * *", "out of range")]
    [InlineData("* * * 13 *", "out of range")]
    [InlineData("*/0 * * * *", "invalid step")]
    [InlineData("a * * * *", "not a valid minute")]
    [InlineData("5-1 * * * *", "out of range")]
    [InlineData("0 0 31 2 *", "never matches")]
    [InlineData("1,,2 * * * *", "empty list item")]
    public void Rejects_bad_expressions(string expr, string expected)
    {
        Assert.Null(CiCron.TryParse(expr, out var error));
        Assert.Contains(expected, error);
    }
}

/// <summary>The manual-job state machine.</summary>
public class CiJobGraphTests
{
    private static CiJobState J(string name, PipelineStatus s = PipelineStatus.Queued, string[]? needs = null, bool manual = false,
        bool allowFailure = false, bool approval = false, bool played = false) =>
        new(name, needs ?? [], s, manual, allowFailure, approval, played);

    private static Dictionary<string, CiJobAction> Next(params CiJobState[] jobs) =>
        CiJobGraph.Next(jobs, canceled: false).ToDictionary(a => a.Job, a => a.Action);

    [Fact]
    public void Jobs_start_when_needs_pass_and_skip_when_they_fail()
    {
        var a = Next(J("build", PipelineStatus.Passed), J("test", needs: ["build"]), J("lint", PipelineStatus.Running), J("pkg", needs: ["lint"]));
        Assert.Equal(CiJobAction.Start, a["test"]);
        Assert.False(a.ContainsKey("pkg")); // waits for lint

        var b = Next(J("build", PipelineStatus.Failed), J("test", needs: ["build"]));
        Assert.Equal(CiJobAction.Skip, b["test"]);

        var c = Next(J("flaky", PipelineStatus.Failed, allowFailure: true), J("test", needs: ["flaky"]));
        Assert.Equal(CiJobAction.Start, c["test"]); // allow_failure satisfies needs
    }

    [Fact]
    public void Manual_jobs_wait_until_played_and_their_dependents_wait_too()
    {
        var a = Next(J("build", PipelineStatus.Passed), J("deploy", needs: ["build"], manual: true), J("smoke", needs: ["deploy"]));
        Assert.Equal(CiJobAction.WaitForManual, a["deploy"]);
        Assert.False(a.ContainsKey("smoke"));

        var waiting = Next(J("build", PipelineStatus.Passed), J("deploy", PipelineStatus.Manual, ["build"], manual: true), J("smoke", needs: ["deploy"]));
        Assert.Empty(waiting);

        var played = Next(J("build", PipelineStatus.Passed), J("deploy", needs: ["build"], manual: true, played: true));
        Assert.Equal(CiJobAction.Start, played["deploy"]);
    }

    [Fact]
    public void Protected_environment_jobs_wait_for_approval()
    {
        var a = Next(J("deploy", approval: true));
        Assert.Equal(CiJobAction.WaitForManual, a["deploy"]);
        Assert.Equal(CiJobAction.Start, Next(J("deploy", approval: true, played: true))["deploy"]);
    }

    [Fact]
    public void Cancel_cancels_queued_and_manual_jobs()
    {
        var a = CiJobGraph.Next([J("a"), J("b", PipelineStatus.Manual, manual: true), J("c", PipelineStatus.Passed)], canceled: true)
            .ToDictionary(x => x.Job, x => x.Action);
        Assert.Equal(CiJobAction.Cancel, a["a"]);
        Assert.Equal(CiJobAction.Cancel, a["b"]);
        Assert.False(a.ContainsKey("c"));
    }

    [Fact]
    public void Pipeline_result_blocks_only_on_blocking_manual_jobs()
    {
        // manual with allow_failure (the default for manual jobs): the pipeline passes; its dependents just wait
        Assert.Equal(PipelineStatus.Passed, CiJobGraph.Result(
            [J("build", PipelineStatus.Passed), J("deploy", PipelineStatus.Manual, manual: true, allowFailure: true), J("smoke", needs: ["deploy"])], false));
        // allow_failure: false → blocked
        Assert.Equal(PipelineStatus.Manual, CiJobGraph.Result([J("build", PipelineStatus.Passed), J("deploy", PipelineStatus.Manual, manual: true)], false));
        // protected environment → always blocks
        Assert.Equal(PipelineStatus.Manual, CiJobGraph.Result([J("deploy", PipelineStatus.Manual, allowFailure: true, approval: true)], false));
        // a real failure wins over waiting
        Assert.Equal(PipelineStatus.Failed, CiJobGraph.Result([J("x", PipelineStatus.Failed), J("deploy", PipelineStatus.Manual, manual: true)], false));
        Assert.Equal(PipelineStatus.Passed, CiJobGraph.Result([J("x", PipelineStatus.Failed, allowFailure: true)], false));
        Assert.Equal(PipelineStatus.Canceled, CiJobGraph.Result([J("x", PipelineStatus.Passed)], true));
    }

    [Fact]
    public void Stages_follow_needs_depth()
    {
        var s = CiJobGraph.Stages([("a", []), ("b", ["a"]), ("c", ["a", "b"]), ("d", [])]);
        Assert.Equal(0, s["a"]);
        Assert.Equal(1, s["b"]);
        Assert.Equal(2, s["c"]);
        Assert.Equal(0, s["d"]);
    }
}

/// <summary>CI turned on, but with a docker CLI that doesn't exist, so queued pipelines end in Error at once.</summary>
public class CiNoDockerApp : TestApp
{
    protected override void AddSettings(IDictionary<string, string?> settings)
    {
        settings["Ci:Enabled"] = "true";
        settings["Ci:DockerExecutable"] = "/nonexistent/docker-for-tests";
        settings["Ci:SchedulesEnabled"] = "false"; // tests call RunDueAsync themselves
    }
}

/// <summary>Cache scopes, deployments, manual-job permissions and schedules, against the database.</summary>
public class CiExtrasServiceTests(CiNoDockerApp app) : IClassFixture<CiNoDockerApp>
{
    private const string PipelineYaml = """
        jobs:
          build: { steps: [ { run: echo build } ] }
          deploy:
            needs: [build]
            when: manual
            environment: { name: production, url: "https://prod.example.com" }
            steps: [ { run: echo deploy } ]
        """;

    private async Task<(Project Project, Repository Repo)> SetupAsync()
    {
        var project = await app.CreateProjectAsync("alice", ProjectVisibility.Private, ("bob", ProjectRole.Developer), ("carol", ProjectRole.Maintainer));
        var repo = await app.CreateRepoAsync(project.Id, "alice");
        app.Commit(repo, "main", CiFile.FileName, PipelineYaml);
        return (project, repo);
    }

    /// <summary>A pipeline straight in the database: build passed, deploy waiting (Manual).</summary>
    private async Task<(Pipeline P, PipelineJob Build, PipelineJob Deploy)> BlockedPipelineAsync(Repository repo, bool requiresApproval = false)
    {
        await using var db = app.Db();
        var p = new Pipeline { RepositoryId = repo.Id, Ref = "main", CommitSha = new string('a', 40), Status = PipelineStatus.Manual, Trigger = PipelineTrigger.Push };
        var build = new PipelineJob { Name = "build", Image = "alpine:3.20", Status = PipelineStatus.Passed, Order = 0 };
        var deploy = new PipelineJob
        {
            Name = "deploy", Image = "alpine:3.20", Needs = "build", Order = 1, Status = PipelineStatus.Manual, IsManual = true,
            EnvironmentName = "production", RequiresApproval = requiresApproval,
        };
        build.Steps.Add(new PipelineStep { Name = "b", Run = "true", Status = PipelineStatus.Passed });
        deploy.Steps.Add(new PipelineStep { Name = "d", Run = "true" });
        p.Jobs.Add(build);
        p.Jobs.Add(deploy);
        db.Pipelines.Add(p);
        await db.SaveChangesAsync();
        return (p, build, deploy);
    }

    [Fact]
    public async Task Caches_of_protected_branches_are_not_visible_to_unprotected_ones()
    {
        var (_, repo) = await SetupAsync();
        var ws = Path.Combine(app.DataRoot, "cache-ws-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(ws, "deps"));
        File.WriteAllText(Path.Combine(ws, "deps", "lib.txt"), "trusted");
        var store = app.Services.GetService(typeof(CiCacheStore)) as CiCacheStore;
        Assert.NotNull(store);

        await store.SaveAsync(repo.Id, "deps", protectedBranch: true, ws, ["deps/"]);
        Assert.NotNull(await store.FindAsync(repo.Id, "deps", protectedBranch: true));
        Assert.Null(await store.FindAsync(repo.Id, "deps", protectedBranch: false));

        // An unprotected branch writes its own cache under the same key; the protected one is untouched.
        File.WriteAllText(Path.Combine(ws, "deps", "lib.txt"), "poisoned");
        await store.SaveAsync(repo.Id, "deps", protectedBranch: false, ws, ["deps/"]);
        var target = Path.Combine(app.DataRoot, "cache-restore-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(target);
        await store.RestoreAsync(repo.Id, "deps", protectedBranch: true, target);
        Assert.Equal("trusted", File.ReadAllText(Path.Combine(target, "deps", "lib.txt")));
    }

    [Fact]
    public async Task Cache_eviction_is_least_recently_used()
    {
        var (_, repo) = await SetupAsync();
        await using (var db = app.Db())
        {
            var now = DateTime.UtcNow;
            db.CiCacheEntries.AddRange(
                new CiCacheEntry { RepositoryId = repo.Id, Key = "old", StoredName = $"{repo.Id}/{Guid.NewGuid():N}.zip", SizeBytes = 1L << 30, LastUsedAt = now.AddDays(-2) },
                new CiCacheEntry { RepositoryId = repo.Id, Key = "new", StoredName = $"{repo.Id}/{Guid.NewGuid():N}.zip", SizeBytes = 1L << 30, LastUsedAt = now },
                new CiCacheEntry { RepositoryId = repo.Id, Key = "mid", StoredName = $"{repo.Id}/{Guid.NewGuid():N}.zip", SizeBytes = 1L << 29, LastUsedAt = now.AddDays(-1) });
            await db.SaveChangesAsync();
        }
        var store = (CiCacheStore)app.Services.GetService(typeof(CiCacheStore))!;
        Assert.Equal(1, await store.TrimAsync(repo.Id)); // 2.5 GB > 2 GB: "old" goes
        await using var check = app.Db();
        Assert.Equal(["mid", "new"], await check.CiCacheEntries.Where(c => c.RepositoryId == repo.Id).OrderBy(c => c.Key).Select(c => c.Key).ToListAsync());
    }

    [Fact]
    public async Task Deployments_are_recorded_and_the_environment_shows_the_current_one()
    {
        var (_, repo) = await SetupAsync();
        var (p, _, deploy) = await BlockedPipelineAsync(repo);
        var recorder = (CiDeploymentRecorder)app.Services.GetService(typeof(CiDeploymentRecorder))!;
        deploy.EnvironmentUrl = "https://prod.example.com";

        var first = await recorder.StartAsync(p, deploy, app.UserId("carol"));
        await recorder.FinishAsync(first!.Value, PipelineStatus.Passed);
        var second = await recorder.StartAsync(p, deploy, app.UserId("bob"));
        await recorder.FinishAsync(second!.Value, PipelineStatus.Failed);

        await using var s = app.Scope(app.UserId("bob"));
        var envs = await s.Get<CiEnvironmentService>().ListAsync(repo);
        var env = Assert.Single(envs);
        Assert.Equal("production", env.Environment.Name);
        Assert.Equal("https://prod.example.com", env.Environment.ExternalUrl);
        Assert.Equal(2, env.Count);
        Assert.Equal(first, env.Current!.Id);
        Assert.Equal("carol", env.Current.User!.UserName);
        Assert.Equal(second, env.Latest!.Id);
        Assert.Equal(PipelineStatus.Failed, env.Latest.Status);
        Assert.Equal(2, (await s.Get<CiEnvironmentService>().HistoryAsync(repo, env.Environment.Id)).Count);

        // Only Maintainers can protect an environment.
        await Assert.ThrowsAsync<ForbiddenException>(() => s.Get<CiEnvironmentService>().SetProtectedAsync(repo, env.Environment.Id, true));
        await using var carol = app.Scope(app.UserId("carol"));
        await carol.Get<CiEnvironmentService>().SetProtectedAsync(repo, env.Environment.Id, true);
        Assert.Contains("production", await recorder.ProtectedNamesAsync(repo.Id));

        // Environments can be added (and protected) before their first deployment.
        await Assert.ThrowsAsync<ForbiddenException>(() => s.Get<CiEnvironmentService>().CreateAsync(repo, "qa", true));
        await carol.Get<CiEnvironmentService>().CreateAsync(repo, "qa", true);
        await Assert.ThrowsAsync<InvalidOperationException>(() => carol.Get<CiEnvironmentService>().CreateAsync(repo, "qa", true));
        await Assert.ThrowsAsync<InvalidOperationException>(() => carol.Get<CiEnvironmentService>().CreateAsync(repo, "-bad", true));
        Assert.Contains("qa", await recorder.ProtectedNamesAsync(repo.Id));
    }

    [Fact]
    public async Task Developers_can_play_manual_jobs_but_only_maintainers_approve_protected_environments()
    {
        var (_, repo) = await SetupAsync();
        var (p, _, deploy) = await BlockedPipelineAsync(repo);
        await using (var bob = app.Scope(app.UserId("bob")))
            await bob.Get<PipelineService>().PlayJobAsync(repo, p.Id, deploy.Id);
        await using (var db = app.Db())
        {
            var j = await db.PipelineJobs.AsNoTracking().FirstAsync(x => x.Id == deploy.Id);
            Assert.Equal(app.UserId("bob"), j.PlayedById);
            Assert.NotNull(j.PlayedAt);
            Assert.NotEqual(PipelineStatus.Manual, j.Status);
        }

        var (p2, _, deploy2) = await BlockedPipelineAsync(repo, requiresApproval: true);
        await using (var bob = app.Scope(app.UserId("bob")))
        {
            var ex = await Assert.ThrowsAsync<ForbiddenException>(() => bob.Get<PipelineService>().PlayJobAsync(repo, p2.Id, deploy2.Id));
            Assert.Contains("protected environment 'production'", ex.Message);
        }
        await using (var carol = app.Scope(app.UserId("carol")))
        {
            await carol.Get<PipelineService>().PlayJobAsync(repo, p2.Id, deploy2.Id);
            // A job that isn't waiting can't be played again.
            await Assert.ThrowsAsync<InvalidOperationException>(() => carol.Get<PipelineService>().PlayJobAsync(repo, p2.Id, deploy2.Id));
        }
    }

    [Fact]
    public async Task Pipelines_from_files_with_rules_get_job_settings()
    {
        var (_, repo) = await SetupAsync();
        int id;
        await using (var s = app.Scope(app.UserId("bob")))
            id = await s.Get<PipelineService>().RunAsync(repo, "main");
        await using var db = app.Db();
        var jobs = await db.PipelineJobs.AsNoTracking().Where(j => j.PipelineId == id).OrderBy(j => j.Order).ToListAsync();
        Assert.Equal(["build", "deploy"], jobs.Select(j => j.Name));
        Assert.True(jobs[1].IsManual);
        Assert.True(jobs[1].AllowFailure);
        Assert.Equal("production", jobs[1].EnvironmentName);
        Assert.Equal("https://prod.example.com", jobs[1].EnvironmentUrl);
    }

    [Fact]
    public async Task Schedules_are_managed_by_maintainers_and_run_as_their_owner()
    {
        var (_, repo) = await SetupAsync();
        await using (var bob = app.Scope(app.UserId("bob")))
            await Assert.ThrowsAsync<ForbiddenException>(() => bob.Get<PipelineScheduleService>().SaveAsync(repo, null, "Nightly", "0 3 * * *", "main", null, true));

        PipelineSchedule schedule;
        await using (var carol = app.Scope(app.UserId("carol")))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => carol.Get<PipelineScheduleService>().SaveAsync(repo, null, "Bad", "61 * * * *", "main", null, true));
            await Assert.ThrowsAsync<InvalidOperationException>(() => carol.Get<PipelineScheduleService>().SaveAsync(repo, null, "Bad", "* * * * *", "nope", null, true));
            await Assert.ThrowsAsync<InvalidOperationException>(() => carol.Get<PipelineScheduleService>().SaveAsync(repo, null, "Bad", "* * * * *", "main",
                new Dictionary<string, string> { ["GITBENCH_X"] = "1" }, true));
            schedule = await carol.Get<PipelineScheduleService>().SaveAsync(repo, null, "Nightly", "*/5 * * * *", "main",
                new Dictionary<string, string> { ["MODE"] = "nightly" }, true);
        }
        Assert.Equal(app.UserId("carol"), schedule.OwnerId);
        Assert.NotNull(schedule.NextRunAt);

        await using (var system = app.Scope(system: true))
        {
            var svc = system.Get<PipelineScheduleService>();
            Assert.Equal(0, await svc.RunDueAsync(schedule.NextRunAt!.Value.AddMinutes(-1))); // not due yet
            Assert.Equal(1, await svc.RunDueAsync(schedule.NextRunAt!.Value));
            Assert.Equal(0, await svc.RunDueAsync(schedule.NextRunAt!.Value)); // already moved on
        }

        await using (var db = app.Db())
        {
            var s = await db.PipelineSchedules.AsNoTracking().FirstAsync(x => x.Id == schedule.Id);
            Assert.Null(s.LastError);
            Assert.Equal(schedule.NextRunAt!.Value.AddMinutes(5), s.NextRunAt);
            var p = await db.Pipelines.AsNoTracking().FirstAsync(x => x.Id == s.LastPipelineId);
            Assert.Equal(PipelineTrigger.Schedule, p.Trigger);
            Assert.Equal(schedule.Id, p.ScheduleId);
            Assert.Equal(app.UserId("carol"), p.TriggeredById);
            Assert.Equal("nightly", CiJobExtras.ParseVariables(p.VariablesJson)["MODE"]);

            // The owner leaves the project: the schedule stops starting pipelines.
            await db.PipelineSchedules.Where(x => x.Id == schedule.Id).ExecuteUpdateAsync(u => u.SetProperty(x => x.OwnerId, app.UserId("dan")));
        }
        await using (var system = app.Scope(system: true))
        {
            var due = (await app.Db().PipelineSchedules.AsNoTracking().FirstAsync(x => x.Id == schedule.Id)).NextRunAt!.Value;
            Assert.Equal(0, await system.Get<PipelineScheduleService>().RunDueAsync(due));
        }
        await using (var db = app.Db())
            Assert.Contains("owner can no longer push", (await db.PipelineSchedules.AsNoTracking().FirstAsync(x => x.Id == schedule.Id)).LastError);
    }
}
