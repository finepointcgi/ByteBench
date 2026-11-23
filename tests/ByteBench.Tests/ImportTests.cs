using System.Net;
using System.Net.Sockets;
using ByteBench.Data;
using ByteBench.Services;
using ByteBench.Services.Git;
using ByteBench.Services.Importing;
using ByteBench.Services.Webhooks;
using ByteBench.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ByteBench.Tests;

/// <summary>Records every push the listeners were told about (to prove an import raises one summary event, not one per ref).</summary>
public sealed class ImportPushLog
{
    public List<(int RepositoryId, string[] Refs)> Pushes { get; } = [];
}

public sealed class ImportPushListener(PushCapture capture, ImportPushLog log) : IGitPushListener
{
    public Task OnPushAsync(int repositoryId, int userId, CancellationToken ct)
    {
        lock (log.Pushes) log.Pushes.Add((repositoryId, capture.Refs.Select(r => r.Ref).ToArray()));
        return Task.CompletedTask;
    }
}

/// <summary>The import worker is off: the tests claim and run each import themselves.</summary>
public class ImportApp : KestrelApp
{
    protected override void AddSettings(IDictionary<string, string?> settings) => settings["Import:Enabled"] = "false";

    protected override void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<ImportPushLog>();
        services.AddScoped<IGitPushListener, ImportPushListener>();
    }
}

/// <summary>Production rules: private networks refused, file:// refused (the test-only switch stays off).</summary>
public class ImportStrictApp : TestApp
{
    protected override void AddSettings(IDictionary<string, string?> settings)
    {
        settings["Import:Enabled"] = "false";
        settings["Mirroring:AllowPrivateNetworks"] = "false";
    }
}

public class ImportTests : IClassFixture<ImportApp>, IDisposable
{
    private readonly ImportApp app;
    private readonly GitCli git = new();

    public ImportTests(ImportApp app)
    {
        this.app = app;
        app.Services.GetRequiredService<ImportTestHooks>().AllowFileUrls = true;
    }

    public void Dispose() => git.Dispose();

    private ImportRunner Runner => app.Services.GetRequiredService<ImportRunner>();
    private ImportPushLog Pushes => app.Services.GetRequiredService<ImportPushLog>();

    private async Task<(Project Project, string Owner)> ProjectAsync(params (string, ProjectRole)[] members)
        => (await app.CreateProjectAsync("carol", ProjectVisibility.Private, members), "carol");

    /// <summary>
    /// A bare repository with main, develop (its HEAD) and feature/x, tags v1 and v2, and reserved refs
    /// (refs/pull/1/head, refs/merge-queue/q) that must not be imported. Returns its file:// URL and path.
    /// </summary>
    private async Task<(string Url, string Path)> CreateSourceAsync()
    {
        var work = git.Dir("work-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(work);
        await git.OkAsync(work, "init", "-b", "main");
        await File.WriteAllTextAsync(System.IO.Path.Combine(work, "README.md"), "# Source\n");
        await git.OkAsync(work, "add", "-A");
        await git.OkAsync(work, "commit", "-m", "Initial commit");
        await git.OkAsync(work, "tag", "v1");
        await git.OkAsync(work, "checkout", "-b", "develop");
        await File.WriteAllTextAsync(System.IO.Path.Combine(work, "dev.txt"), "dev\n");
        await git.OkAsync(work, "add", "-A");
        await git.OkAsync(work, "commit", "-m", "Develop work");
        await git.OkAsync(work, "tag", "-a", "v2", "-m", "Version 2");
        await git.OkAsync(work, "checkout", "-b", "feature/x");
        await File.WriteAllTextAsync(System.IO.Path.Combine(work, "x.txt"), "x\n");
        await git.OkAsync(work, "add", "-A");
        await git.OkAsync(work, "commit", "-m", "Feature x");

        var bare = git.Dir("source-" + Guid.NewGuid().ToString("N")[..8] + ".git");
        await git.OkAsync(null, "init", "--bare", "-b", "develop", bare);
        await git.OkAsync(work, "push", bare, "--all");
        await git.OkAsync(work, "push", bare, "--tags");
        var tip = (await git.OkAsync(null, "--git-dir=" + bare, "rev-parse", "main")).Trim();
        await git.OkAsync(null, "--git-dir=" + bare, "update-ref", "refs/pull/1/head", tip);
        await git.OkAsync(null, "--git-dir=" + bare, "update-ref", "refs/merge-queue/q", tip);
        return (new Uri(bare).AbsoluteUri, bare);
    }

    private async Task<RepositoryImport> StartAsync(Project p, string user, string url, string name = "Imported", Action<ImportInput>? tweak = null)
    {
        var input = new ImportInput { Url = url, Name = name };
        tweak?.Invoke(input);
        await using var s = app.Scope(app.UserId(user));
        return await s.Get<ImportService>().StartAsync(p.Id, input);
    }

    private async Task<RepositoryImport> ReloadAsync(int id)
    {
        await using var db = app.Db();
        return await db.RepositoryImports.AsNoTracking().SingleAsync(i => i.Id == id);
    }

    private async Task<RepositoryImport> RunAsync(Project p, string user, string url, string name = "Imported", Action<ImportInput>? tweak = null)
    {
        var job = await StartAsync(p, user, url, name, tweak);
        Assert.True(await Runner.RunAsync(job.Id));
        return await ReloadAsync(job.Id);
    }

    private string RepoPath(Project p, string slug) => app.Services.GetRequiredService<RepoStorage>().PathFor(p.Key, slug);

    private async Task<Dictionary<string, string>> RefsAsync(string bare) =>
        (await git.OkAsync(null, "--git-dir=" + bare, "for-each-ref", "--format=%(refname) %(objectname)"))
        .Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Split(' ')).ToDictionary(a => a[0], a => a[1]);

    // ================================================================ the happy path

    [Fact]
    public async Task Imports_all_branches_and_tags_but_not_reserved_refs_and_creates_the_repository_normally()
    {
        var (p, _) = await ProjectAsync();
        var (url, src) = await CreateSourceAsync();
        var sourceRefs = await RefsAsync(src);

        var before = Pushes.Pushes.Count;
        var done = await RunAsync(p, "carol", url, "My Import", i => i.Description = "From elsewhere");

        Assert.Equal(ImportStatus.Done, done.Status);
        Assert.Null(done.Error);
        Assert.Equal("my-import", done.Slug);
        Assert.Equal(3, done.BranchCount);
        Assert.Equal(2, done.TagCount);
        Assert.NotNull(done.RepositoryId);

        var refs = await RefsAsync(RepoPath(p, "my-import"));
        foreach (var r in new[] { "refs/heads/main", "refs/heads/develop", "refs/heads/feature/x", "refs/tags/v1", "refs/tags/v2" })
            Assert.Equal(sourceRefs[r], refs[r]);
        Assert.DoesNotContain(refs.Keys, k => k.StartsWith("refs/pull/") || k.StartsWith("refs/merge-queue/"));
        Assert.Equal(5, refs.Count);

        // The default branch is the source's HEAD (develop), not the usual "main".
        await using var db = app.Db();
        var repo = await db.Repositories.SingleAsync(r => r.Id == done.RepositoryId);
        Assert.Equal("develop", repo.DefaultBranch);
        Assert.Equal("From elsewhere", repo.Description);
        Assert.Equal("ref: refs/heads/develop", (await File.ReadAllTextAsync(System.IO.Path.Combine(RepoPath(p, "my-import"), "HEAD"))).Trim());

        // Audit: the normal creation event, plus the import's.
        var actions = await db.AuditEvents.Where(e => e.ProjectId == p.Id).Select(e => e.Action).ToListAsync();
        Assert.Contains("repository.created", actions);
        Assert.Contains("repository.imported", actions);
        Assert.Contains("repository.import_requested", actions);

        // Push listeners (webhooks, CI detection, smart commits...) hear about it exactly once, as one ref.
        var pushes = Pushes.Pushes.Skip(before).Where(x => x.RepositoryId == repo.Id).ToList();
        var push = Assert.Single(pushes);
        Assert.Equal(["refs/heads/develop"], push.Refs);

        // Nothing is left in the temp area.
        var temp = Runner.TempRoot;
        Assert.Empty(Directory.Exists(temp) ? Directory.GetDirectories(temp) : []);

        // It is a normal repository: the code browser's git layer reads it.
        Assert.Equal(3, GitOps.ListBranches(RepoPath(p, "my-import"), "develop").Count);
    }

    [Fact]
    public async Task Probe_detects_the_default_branch_and_counts_refs()
    {
        var (p, _) = await ProjectAsync();
        var (url, _) = await CreateSourceAsync();
        await using var s = app.Scope(app.UserId("carol"));
        var probe = await s.Get<ImportService>().ProbeAsync(p.Id, url, null, null);
        Assert.Equal("develop", probe.DefaultBranch);
        Assert.Equal(["develop", "feature/x", "main"], probe.Branches);
        Assert.Equal(2, probe.TagCount);
        Assert.StartsWith("source-", probe.SuggestedName);

        var empty = git.Dir("empty.git");
        await git.OkAsync(null, "init", "--bare", empty);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => s.Get<ImportService>().ProbeAsync(p.Id, new Uri(empty).AbsoluteUri, null, null));
        Assert.Contains("no branches", ex.Message);
    }

    [Fact]
    public async Task A_chosen_default_branch_is_used_and_a_vanished_one_falls_back()
    {
        var (p, _) = await ProjectAsync();
        var (url, _) = await CreateSourceAsync();
        var a = await RunAsync(p, "carol", url, "Chosen", i => i.DefaultBranch = "main");
        await using var db = app.Db();
        Assert.Equal("main", (await db.Repositories.SingleAsync(r => r.Id == a.RepositoryId)).DefaultBranch);

        var b = await RunAsync(p, "carol", url, "Fallback", i => i.DefaultBranch = "gone-branch");
        Assert.Equal("main", (await db.Repositories.SingleAsync(r => r.Id == b.RepositoryId)).DefaultBranch); // main exists: preferred
    }

    [Fact]
    public async Task Credentials_are_never_stored_unless_kept_as_a_pull_mirror_and_then_encrypted()
    {
        var (p, _) = await ProjectAsync();
        var (url, _) = await CreateSourceAsync();

        var plain = await RunAsync(p, "carol", url, "No Mirror", i => { i.Username = "bob"; i.Secret = "hunter2-token"; });
        Assert.Equal(ImportStatus.Done, plain.Status);
        await using (var db = app.Db())
        {
            Assert.False(await db.RepositoryMirrors.AnyAsync(m => m.RepositoryId == plain.RepositoryId));
            // The secret is in no column of the import row or the audit log.
            Assert.DoesNotContain("hunter2-token", string.Join("|", await db.RepositoryImports.Select(i => i.SourceUrl + i.Error + i.Name).ToListAsync()));
            Assert.DoesNotContain(await db.AuditEvents.Select(e => e.Details).ToListAsync(), d => d != null && d.Contains("hunter2-token"));
        }

        // A file:// source can't be a mirror (mirrors are http/https/ssh): the import still succeeds and says why.
        var kept = await RunAsync(p, "carol", url, "As Mirror", i => { i.Username = "bob"; i.Secret = "hunter2-token"; i.KeepAsMirror = true; });
        Assert.Equal(ImportStatus.Done, kept.Status);
        Assert.Contains("couldn't be kept as a pull mirror", kept.Error);
        await using var db2 = app.Db();
        Assert.False(await db2.RepositoryMirrors.AnyAsync(m => m.RepositoryId == kept.RepositoryId));
    }

    [Fact]
    public async Task Imports_over_http_with_credentials_and_keeps_a_pull_mirror_with_encrypted_credentials()
    {
        var srcProject = await app.CreateProjectAsync("carol", ProjectVisibility.Private, ("bob", ProjectRole.Developer));
        var upstream = await app.CreateRepoAsync(srcProject.Id, "carol", "upstream");
        app.Commit(upstream, "main", "lib.txt", "library", message: "Add lib");
        app.Commit(upstream, "release/1", "r.txt", "release", message: "Release branch");
        var (dest, _) = await ProjectAsync();
        var plainUrl = new Uri(app.ServerAddress, $"/git/{srcProject.Key}/{upstream.Slug}.git").ToString();

        // Without credentials the private upstream refuses, with a message.
        var denied = await RunAsync(dest, "carol", plainUrl, "Denied");
        Assert.Equal(ImportStatus.Failed, denied.Status);
        Assert.False(string.IsNullOrEmpty(denied.Error));

        var done = await RunAsync(dest, "carol", plainUrl, "From Http", i => { i.Username = "bob"; i.Secret = SeedData.DemoPassword; i.KeepAsMirror = true; });
        Assert.Equal(ImportStatus.Done, done.Status);
        Assert.Null(done.Error);
        var refs = await RefsAsync(RepoPath(dest, "from-http"));
        Assert.Contains("refs/heads/main", refs.Keys);
        Assert.Contains("refs/heads/release/1", refs.Keys);

        await using var db = app.Db();
        var mirror = await db.RepositoryMirrors.SingleAsync(m => m.RepositoryId == done.RepositoryId);
        Assert.Equal(MirrorDirection.Pull, mirror.Direction);
        Assert.Equal("bob", mirror.Username);
        Assert.NotNull(mirror.ProtectedSecret);
        Assert.DoesNotContain(SeedData.DemoPassword, mirror.ProtectedSecret);
        Assert.Equal(plainUrl, mirror.Url);
        // The import row itself holds neither the secret nor any user info.
        Assert.DoesNotContain(SeedData.DemoPassword, (await db.RepositoryImports.SingleAsync(i => i.Id == done.Id)).SourceUrl);
        Assert.DoesNotContain(await db.AuditEvents.Select(e => e.Details).ToListAsync(), d => d != null && d.Contains(SeedData.DemoPassword));
    }

    // ================================================================ failures

    [Fact]
    public async Task Unreachable_source_fails_the_import_with_git_s_message_and_leaves_nothing_behind()
    {
        var (p, _) = await ProjectAsync();
        var missing = new Uri(git.Dir("does-not-exist.git")).AbsoluteUri;
        var failed = await RunAsync(p, "carol", missing, "Broken");
        Assert.Equal(ImportStatus.Failed, failed.Status);
        Assert.Contains("git fetch failed", failed.Error);
        Assert.Null(failed.RepositoryId);
        Assert.False(Directory.Exists(RepoPath(p, "broken")));
        Assert.Empty(Directory.Exists(Runner.TempRoot) ? Directory.GetDirectories(Runner.TempRoot) : []);
        await using var db = app.Db();
        Assert.False(await db.Repositories.AnyAsync(r => r.ProjectId == p.Id && r.Slug == "broken"));
        Assert.Contains("repository.import_failed", await db.AuditEvents.Where(e => e.ProjectId == p.Id).Select(e => e.Action).ToListAsync());
    }

    [Fact]
    public async Task Bad_urls_and_names_are_rejected_before_anything_is_queued()
    {
        var (p, _) = await ProjectAsync();
        await using var s = app.Scope(app.UserId("carol"));
        var svc = s.Get<ImportService>();
        foreach (var bad in new[] { "", "not a url", "ftp://example.com/x.git", "git@github.com:org/repo.git", "https://example.com", "-oProxyCommand=x", "https://user:pw@example.com/x.git" })
            await Assert.ThrowsAsync<InvalidOperationException>(() => svc.StartAsync(p.Id, new ImportInput { Url = bad, Name = "X" }));

        var (url, _) = await CreateSourceAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.StartAsync(p.Id, new ImportInput { Url = url, Name = "" }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.StartAsync(p.Id, new ImportInput { Url = url, Name = "Bad", Slug = "Bad Slug!" }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.StartAsync(p.Id, new ImportInput { Url = url, Name = "Import" })); // reserved slug
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.StartAsync(p.Id, new ImportInput { Url = url, Name = "Ok", DefaultBranch = "bad..branch" }));

        await using var db = app.Db();
        Assert.False(await db.RepositoryImports.AnyAsync(i => i.ProjectId == p.Id));
    }

    [Fact]
    public async Task Slug_conflicts_with_a_repository_or_an_active_import_are_refused()
    {
        var (p, _) = await ProjectAsync();
        await app.CreateRepoAsync(p.Id, "carol", "taken");
        var (url, _) = await CreateSourceAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => StartAsync(p, "carol", url, "Taken"));

        await StartAsync(p, "carol", url, "Pending"); // queued: the worker is off
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => StartAsync(p, "carol", url, "Pending"));
        Assert.Contains("already in progress", ex.Message);

        // At most MaxActivePerProject at once.
        await StartAsync(p, "carol", url, "Second");
        await StartAsync(p, "carol", url, "Third");
        var limit = await Assert.ThrowsAsync<InvalidOperationException>(() => StartAsync(p, "carol", url, "Fourth"));
        Assert.Contains("imports in progress", limit.Message);
    }

    [Fact]
    public async Task A_repository_over_the_size_limit_fails_and_is_cleaned_up()
    {
        var (p, _) = await ProjectAsync();
        var (url, _) = await CreateSourceAsync();
        var limits = app.Services.GetRequiredService<IOptionsMonitor<ImportOptions>>().CurrentValue;
        var old = limits.MaxRepoBytes;
        limits.MaxRepoBytes = 1;
        try
        {
            var failed = await RunAsync(p, "carol", url, "Huge");
            Assert.Equal(ImportStatus.Failed, failed.Status);
            Assert.Contains("larger than the limit", failed.Error);
            Assert.Contains("Import:MaxRepoBytes", failed.Error);
            Assert.False(Directory.Exists(RepoPath(p, "huge")));
            Assert.Empty(Directory.Exists(Runner.TempRoot) ? Directory.GetDirectories(Runner.TempRoot) : []);
        }
        finally { limits.MaxRepoBytes = old; }
    }

    // ================================================================ cancel and restart

    [Fact]
    public async Task A_queued_import_can_be_cancelled_and_then_never_runs()
    {
        var (p, _) = await ProjectAsync();
        var (url, _) = await CreateSourceAsync();
        var job = await StartAsync(p, "carol", url, "Cancel Me");
        await using (var s = app.Scope(app.UserId("carol")))
            Assert.True(await s.Get<ImportService>().CancelAsync(job.Id));
        Assert.Equal(ImportStatus.Cancelled, (await ReloadAsync(job.Id)).Status);
        Assert.False(await Runner.RunAsync(job.Id)); // not queued any more: nothing happens
        Assert.False(Directory.Exists(RepoPath(p, "cancel-me")));
        await using (var s = app.Scope(app.UserId("carol")))
            Assert.False(await s.Get<ImportService>().CancelAsync(job.Id)); // already finished
    }

    [Fact]
    public async Task A_running_import_can_be_cancelled_and_its_partial_data_is_removed()
    {
        var (p, _) = await ProjectAsync();
        // A server that accepts the connection and never answers, so the clone hangs until it is cancelled.
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var open = new List<TcpClient>();
        var accepting = Task.Run(async () =>
        {
            try { while (true) open.Add(await listener.AcceptTcpClientAsync()); } catch (Exception ex) when (ex is ObjectDisposedException or SocketException or InvalidOperationException) { }
        });
        var url = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/hang.git";

        var job = await StartAsync(p, "carol", url, "Hanging");
        var run = Runner.RunAsync(job.Id);
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline && !(open.Count > 0)) await Task.Delay(50); // git connected: the clone is in flight
        Assert.True(open.Count > 0, "git never connected to the test server");
        Assert.Equal(ImportStatus.Cloning, (await ReloadAsync(job.Id)).Status);

        await using (var s = app.Scope(app.UserId("carol")))
            Assert.True(await s.Get<ImportService>().CancelAsync(job.Id));
        Assert.True(await run.WaitAsync(TimeSpan.FromSeconds(30)));

        var after = await ReloadAsync(job.Id);
        Assert.Equal(ImportStatus.Cancelled, after.Status);
        Assert.Null(after.RepositoryId);
        Assert.False(Directory.Exists(RepoPath(p, "hanging")));
        Assert.Empty(Directory.Exists(Runner.TempRoot) ? Directory.GetDirectories(Runner.TempRoot) : []);
        listener.Stop();
        foreach (var c in open) c.Dispose();
        await accepting;
    }

    [Fact]
    public async Task Imports_interrupted_by_a_restart_are_marked_failed_and_their_temp_data_removed()
    {
        var (p, _) = await ProjectAsync();
        int queuedId, cloningId, liveId;
        await using (var db = app.Db())
        {
            RepositoryImport Row(string slug, ImportStatus s) => new()
            {
                ProjectId = p.Id, RequestedById = app.UserId("carol"), SourceUrl = "https://example.com/x.git", Name = slug, Slug = slug, Status = s,
            };
            var queued = Row("was-queued", ImportStatus.Queued);
            var cloning = Row("was-cloning", ImportStatus.Cloning);
            var done = Row("was-done", ImportStatus.Done);
            db.RepositoryImports.AddRange(queued, cloning, done);
            await db.SaveChangesAsync();
            queuedId = queued.Id; cloningId = cloning.Id;
            var (url, _) = await CreateSourceAsync();
            liveId = (await StartAsync(p, "carol", url, "Still Mine")).Id; // known to this process: must be left alone
        }
        var leftover = System.IO.Path.Combine(Runner.TempRoot, $"{cloningId}-deadbeef");
        Directory.CreateDirectory(System.IO.Path.Combine(leftover, "objects"));
        await File.WriteAllTextAsync(System.IO.Path.Combine(leftover, "objects", "pack"), "partial");

        await Runner.RecoverInterruptedAsync();

        foreach (var id in new[] { queuedId, cloningId })
        {
            var row = await ReloadAsync(id);
            Assert.Equal(ImportStatus.Failed, row.Status);
            Assert.Equal(ImportRunner.InterruptedMessage, row.Error);
            Assert.NotNull(row.FinishedAt);
        }
        Assert.Equal(ImportStatus.Queued, (await ReloadAsync(liveId)).Status);
        Assert.False(Directory.Exists(leftover));
        await using var db2 = app.Db();
        Assert.Equal(ImportStatus.Done, (await db2.RepositoryImports.SingleAsync(i => i.Slug == "was-done")).Status);
    }

    // ================================================================ permissions

    [Fact]
    public async Task Only_maintainers_can_import_and_only_they_can_see_or_cancel_an_import()
    {
        var (p, _) = await ProjectAsync(("bob", ProjectRole.Developer), ("dan", ProjectRole.Maintainer));
        var (url, _) = await CreateSourceAsync();

        await using (var s = app.Scope(app.UserId("bob")))
        {
            await Assert.ThrowsAsync<ForbiddenException>(() => s.Get<ImportService>().ProbeAsync(p.Id, url, null, null));
            await Assert.ThrowsAsync<ForbiddenException>(() => s.Get<ImportService>().StartAsync(p.Id, new ImportInput { Url = url, Name = "Nope" }));
        }
        var other = await app.CreateProjectAsync("alice");
        await using (var s = app.Scope(app.UserId("dan")))
            await Assert.ThrowsAsync<ForbiddenException>(() => s.Get<ImportService>().StartAsync(other.Id, new ImportInput { Url = url, Name = "Nope" }));

        var job = await StartAsync(p, "dan", url, "By Dan");
        await using (var s = app.Scope(app.UserId("bob")))
        {
            Assert.Null(await s.Get<ImportService>().GetAsync(job.Id));
            await Assert.ThrowsAsync<InvalidOperationException>(() => s.Get<ImportService>().CancelAsync(job.Id));
            await Assert.ThrowsAsync<ForbiddenException>(() => s.Get<ImportService>().ListAsync(p.Id));
        }
        await using (var s = app.Scope(app.UserId("carol")))
            Assert.NotNull(await s.Get<ImportService>().GetAsync(job.Id)); // the owner sees it
        Assert.True(await Runner.RunAsync(job.Id));

        // The import runs as the person who started it: it is Dan's audit trail, and the repository is created by Dan's permission.
        await using var db = app.Db();
        var created = await db.AuditEvents.Where(e => e.ProjectId == p.Id && e.Action == "repository.created").OrderByDescending(e => e.Id).FirstAsync();
        Assert.Equal(app.UserId("dan"), created.ActorId);
    }
}

public class ImportProductionRulesTests(ImportStrictApp app) : IClassFixture<ImportStrictApp>
{
    [Fact]
    public async Task Production_refuses_file_urls_private_networks_and_metadata_addresses()
    {
        var p = await app.CreateProjectAsync("carol");
        await using var s = app.Scope(app.UserId("carol"));
        var svc = s.Get<ImportService>();
        foreach (var url in new[]
                 {
                     "file:///etc", "file:///tmp/some.git",
                     "http://127.0.0.1/x.git", "https://localhost/x.git", "http://[::1]/x.git", "http://10.0.0.5/x.git",
                     "http://192.168.1.10/x.git", "http://169.254.169.254/latest/meta-data", "ssh://git@127.0.0.1/x.git", "http://[::ffff:127.0.0.1]/x.git",
                 })
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => svc.ProbeAsync(p.Id, url, null, null));
            await Assert.ThrowsAsync<InvalidOperationException>(() => svc.StartAsync(p.Id, new ImportInput { Url = url, Name = "Blocked" }));
        }
        await using var db = app.Db();
        Assert.False(await db.RepositoryImports.AnyAsync());
    }

    [Fact]
    public async Task Pages_follow_the_same_permission()
    {
        var p = await app.CreateProjectAsync("carol", ProjectVisibility.Private, ("bob", ProjectRole.Developer), ("dan", ProjectRole.Maintainer));
        var dan = await app.CookieClientAsync("dan");
        var wizard = await dan.GetStringAsync($"/p/{p.Key}/repos/import");
        Assert.Contains("Where is the repository?", wizard);
        Assert.Contains("Import repository", await dan.GetStringAsync($"/p/{p.Key}/repos"));

        var bob = await app.CookieClientAsync("bob");
        var denied = await bob.GetStringAsync($"/p/{p.Key}/repos/import");
        Assert.DoesNotContain("Where is the repository?", denied);
        Assert.DoesNotContain("Import repository</", await bob.GetStringAsync($"/p/{p.Key}/repos"));
    }
}
