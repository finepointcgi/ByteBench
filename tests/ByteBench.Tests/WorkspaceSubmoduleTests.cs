using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using ByteBench.Data;
using ByteBench.Services;
using ByteBench.Services.Git;
using ByteBench.Services.Workspaces;
using ByteBench.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ByteBench.Tests;

public class SubmoduleParsingTests
{
    [Fact]
    public void Gitmodules_is_read_with_names_paths_and_urls()
    {
        var list = GitmodulesParser.Parse("""
            # comment
            [submodule "libs/one"]
                path = libs/one
                url = ../one.git
            [submodule "two"]
            	path = vendor/two
            	url = https://example.com/two.git
                branch = main
            [submodule "broken"]
                path = nope
            """);
        Assert.Equal([new SubmoduleLink("libs/one", "libs/one", "../one.git"), new SubmoduleLink("two", "vendor/two", "https://example.com/two.git")], list);
        Assert.Empty(GitmodulesParser.Parse(null));
        Assert.Empty(GitmodulesParser.Parse("[submodule \"x\"]\n url = a\n"));
    }

    private static readonly Uri GitBase = new("http://host.docker.internal:5108/");

    [Theory]
    [InlineData("../lib.git", "T001", "super", "T001", "lib")]
    [InlineData("../../T002/lib.git", "T001", "super", "T002", "lib")]
    [InlineData("./sub/../../lib.git", "T001", "super", "T001", "lib")]
    [InlineData("http://host.docker.internal:5108/git/T003/x.git", "T001", "super", "T003", "x")]
    [InlineData("https://bytebench.example.com/git/T004/y.git", "T001", "super", "T004", "y")]
    [InlineData("https://bytebench.example.com/base/git/T004/y.git/", "T001", "super", "T004", "y")]
    public void Urls_that_point_at_this_server_are_mapped(string url, string key, string slug, string expectedKey, string expectedSlug)
    {
        var publicBase = url.Contains("/base/") ? "https://bytebench.example.com/base" : "https://bytebench.example.com";
        Assert.Equal((expectedKey, expectedSlug), SubmoduleResolver.MapUrl(url, key, slug, GitBase, publicBase));
    }

    [Theory]
    [InlineData("https://github.com/org/lib.git")]
    [InlineData("http://host.docker.internal:9999/git/T003/x.git")] // same host, other port: not this server
    [InlineData("https://bytebench.example.com.evil.net/git/T004/y.git")]
    [InlineData("https://bytebench.example.com/other/path.git")]
    [InlineData("git@github.com:org/lib.git")]
    [InlineData("ssh://git@bytebench.example.com/git/T004/y.git")]
    [InlineData("file:///etc")]
    [InlineData("../../../../../etc/passwd")]
    [InlineData("ext::sh -c id")]
    [InlineData("")]
    public void Anything_else_is_a_foreign_url_and_gets_no_credential(string url) =>
        Assert.Null(SubmoduleResolver.MapUrl(url, "T001", "super", GitBase, "https://bytebench.example.com"));
}

/// <summary>
/// Submodules against the running test host over HTTP with the real git client: the clone script that ships in the workspace runs on this machine in
/// place of the container, with the workspace's real <c>gbw_</c> token in a credential store file exactly as in the container.
/// </summary>
public class WorkspaceSubmoduleTests : IClassFixture<WorkspaceApp>, IDisposable
{
    private readonly WorkspaceApp app;
    private readonly GitCli git = new();
    private readonly string home = Path.Combine(Path.GetTempPath(), "bytebench-tests", "ws-" + Guid.NewGuid().ToString("N"));
    private readonly string root;

    public WorkspaceSubmoduleTests(WorkspaceApp app)
    {
        this.app = app;
        root = Path.Combine(home, "workspace");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.Combine(home, "home"));
        // Containers reach ByteBench on its real address in this test.
        app.Services.GetRequiredService<IOptionsMonitor<WorkspaceOptions>>().CurrentValue.GitBaseUrl = app.Origin.TrimEnd('/');
        app.Backend.ExecScripts.Insert(0, (s => RunsLocally(s), s => RunLocal(s)));
    }

    public void Dispose()
    {
        git.Dispose();
        try { Directory.Delete(home, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    // ---------- the fake container: git work runs on this machine ----------

    private string Home => Path.Combine(home, "home");

    private static bool RunsLocally(ExecSpec s) =>
        s.Command is ["git", "config", "--global", ..] ||
        (s.Command is ["sh", "-c", var script, ..] && (script == WorkspaceLifecycle.CloneScript || script.Contains(".git-credentials")));

    private ExecResult RunLocal(ExecSpec spec)
    {
        string Map(string a) => a.Replace("/workspace", root);
        var psi = new ProcessStartInfo(spec.Command[0]) { RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = spec.Stdin != null, UseShellExecute = false, WorkingDirectory = spec.WorkDir != null && Directory.Exists(Map(spec.WorkDir)) ? Map(spec.WorkDir) : root };
        foreach (var a in spec.Command.Skip(1)) psi.ArgumentList.Add(a.Length > 0 && a[0] == '/' ? Map(a) : a);
        psi.Environment["HOME"] = Home;
        psi.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        psi.Environment.Remove("GIT_DIR");
        psi.Environment["GIT_ASKPASS"] = "";
        foreach (var (k, v) in spec.Env ?? new Dictionary<string, string>()) psi.Environment[k] = v;
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        if (spec.Stdin != null) { p.StandardInput.Write(spec.Stdin); p.StandardInput.Close(); }
        if (!p.WaitForExit(120_000)) { p.Kill(true); return new ExecResult(124, "timeout", true); }
        return new ExecResult(p.ExitCode, stdout.Result + stderr.Result);
    }

    // ---------- fixtures ----------

    private string RepoPath(Repository repo) => app.Services.GetRequiredService<RepoStorage>().PathFor(app.ProjectKey(repo.ProjectId), repo.Slug);

    private string AliceUrl(Repository repo) => app.GitUrl(app.ProjectKey(repo.ProjectId), repo.Slug, "alice", SeedData.DemoPassword);

    /// <summary>Pushes a superproject whose .gitmodules is <paramref name="modules"/> and whose gitlinks point at the given submodule commits.</summary>
    private async Task MakeSuperAsync(Repository super, string modules, params (string Path, Repository Lib)[] links)
    {
        var dir = git.Dir("super-" + Guid.NewGuid().ToString("N")[..6]);
        await git.OkAsync(null, "clone", AliceUrl(super), dir);
        await File.WriteAllTextAsync(Path.Combine(dir, ".gitmodules"), modules);
        foreach (var (path, lib) in links)
        {
            var sha = (await git.OkAsync(null, "ls-remote", AliceUrl(lib), "refs/heads/main")).Split('\t')[0];
            await git.OkAsync(dir, "update-index", "--add", "--cacheinfo", $"160000,{sha},{path}");
        }
        await git.OkAsync(dir, "add", ".gitmodules");
        await git.OkAsync(dir, "commit", "-m", "add submodules");
        await git.OkAsync(dir, "push", "origin", "HEAD:main");
    }

    private async Task<(string User, Repository Super, Repository Lib, Project Project)> SetupAsync(string user, bool withLib = true)
    {
        var project = await app.CreateProjectAsync("alice", ProjectVisibility.Private, (user, ProjectRole.Developer));
        var super = await app.CreateRepoAsync(project.Id, "alice", "super");
        var lib = await app.CreateRepoAsync(project.Id, "alice", "lib");
        app.Commit(lib, "main", "lib.txt", "library content\n");
        return (user, super, lib, project);
    }

    /// <summary>The <c>gbw_</c> token this workspace's container was given (read from the credential line written to its stdin).</summary>
    private string TokenOf(Workspace ws)
    {
        var line = app.Backend.Execs.Where(e => e.ContainerId == ws.ContainerId && e.Spec.Stdin?.Contains("gbw_") == true).Select(e => e.Spec.Stdin!.Trim()).First();
        var afterScheme = line[(line.IndexOf("://", StringComparison.Ordinal) + 3)..];
        var userAndToken = afterScheme[..afterScheme.IndexOf('@')];
        return userAndToken[(userAndToken.IndexOf(':') + 1)..];
    }

    private IEnumerable<string> AllFiles(string dir) => Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories);

    // ---------- tests ----------

    [Fact]
    public async Task A_submodule_on_the_same_server_is_fetched_with_the_workspace_token_and_the_token_stays_out_of_the_checkout()
    {
        var user = await app.NewUserAsync();
        var (_, super, lib, project) = await SetupAsync(user);
        await MakeSuperAsync(super, "[submodule \"libs/lib\"]\n\tpath = libs/lib\n\turl = ../lib.git\n", ("libs/lib", lib));

        var ws = await app.StartAsync(user, super);
        Assert.Equal(WorkspaceState.Running, ws.State);
        Assert.Equal("library content\n", await File.ReadAllTextAsync(Path.Combine(root, "super", "libs", "lib", "lib.txt")));
        Assert.Contains($"[{lib.Id}]", ws.SubmoduleRepoIdsJson); // the token may read this one repository besides its own

        var session = app.Runtime.Session(ws.Id);
        Assert.Empty(session.Warnings);
        Assert.Contains("Fetching submodule libs/lib", session.ProvisionLog.Text());

        // The token was never written anywhere but the credential store, and is not in .gitmodules or any git config of the checkout.
        var token = TokenOf(ws);
        Assert.StartsWith("gbw_", token);
        var credentialFile = Path.Combine(Home, ".git-credentials");
        foreach (var file in AllFiles(home).Where(f => f != credentialFile))
        {
            var bytes = await File.ReadAllBytesAsync(file);
            Assert.False(Encoding.Latin1.GetString(bytes).Contains(token), $"the token is in {file}");
        }
        Assert.Contains(token, await File.ReadAllTextAsync(credentialFile));
        if (!OperatingSystem.IsWindows()) Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(credentialFile)); // mode 600
        // ...and never in a command line or an environment variable of any exec.
        Assert.DoesNotContain(app.Backend.Execs, e => e.Spec.Command.Any(a => a.Contains(token)) || e.Spec.Env?.Values.Any(v => v.Contains(token)) == true);
        Assert.DoesNotContain(app.Backend.Streams, e => e.Spec.Command.Any(a => a.Contains(token)) || e.Spec.Env?.Values.Any(v => v.Contains(token)) == true);
    }

    [Fact]
    public async Task The_token_can_read_a_submodule_repository_but_never_write_it_and_cannot_read_unrelated_ones()
    {
        var user = await app.NewUserAsync();
        var (_, super, lib, project) = await SetupAsync(user);
        var unrelated = await app.CreateRepoAsync(project.Id, "alice", "unrelated");
        await MakeSuperAsync(super, "[submodule \"libs/lib\"]\n\tpath = libs/lib\n\turl = ../lib.git\n", ("libs/lib", lib));
        var ws = await app.StartAsync(user, super);
        var token = TokenOf(ws);
        var key = app.ProjectKey(project.Id);

        await git.OkAsync(null, "clone", app.GitUrl(key, "lib", user, token), git.Dir("read-lib")); // read: yes
        var push = git.Dir("push-lib");
        await git.OkAsync(null, "clone", app.GitUrl(key, "lib", user, token), push);
        await File.WriteAllTextAsync(Path.Combine(push, "x.txt"), "x");
        await git.OkAsync(push, "add", "-A");
        await git.OkAsync(push, "commit", "-m", "try");
        Assert.False((await git.RunAsync(push, "push", "origin", "HEAD:refs/heads/hack")).Ok); // write: no, although the user is a Developer there
        Assert.False((await git.RunAsync(null, "clone", app.GitUrl(key, "unrelated", user, token), git.Dir("other"))).Ok); // another repository: no
        await git.OkAsync(null, "clone", app.GitUrl(key, "super", user, token), git.Dir("own")); // its own: yes
        Assert.NotNull(ws);
    }

    [Fact]
    public async Task A_submodule_the_owner_cannot_view_fails_by_name_with_the_reason_and_the_rest_carries_on()
    {
        var user = await app.NewUserAsync();
        var (_, super, lib, project) = await SetupAsync(user);
        var secretProject = await app.CreateProjectAsync("alice", ProjectVisibility.Private); // the user is not a member
        var secret = await app.CreateRepoAsync(secretProject.Id, "alice", "secret");
        app.Commit(secret, "main", "secret.txt", "classified\n");
        var secretUrl = $"{app.Origin}git/{app.ProjectKey(secretProject.Id)}/secret.git";
        await MakeSuperAsync(super, $"[submodule \"libs/lib\"]\n\tpath = libs/lib\n\turl = ../lib.git\n[submodule \"vendor/secret\"]\n\tpath = vendor/secret\n\turl = {secretUrl}\n",
            ("libs/lib", lib), ("vendor/secret", secret));

        var ws = await app.StartAsync(user, super);
        Assert.Equal(WorkspaceState.Running, ws.State); // the workspace still opens
        Assert.True(File.Exists(Path.Combine(root, "super", "libs", "lib", "lib.txt")));
        Assert.False(File.Exists(Path.Combine(root, "super", "vendor", "secret", "secret.txt")));
        var warnings = app.Runtime.Session(ws.Id).Warnings;
        Assert.Contains(warnings, w => w.Contains("vendor/secret") && w.Contains("you don't have access"));            // the server-side check, by name
        Assert.Contains(warnings, w => w.Contains("submodule 'vendor/secret' could not be fetched"));                       // what git said, by name
        Assert.DoesNotContain(warnings, w => w.Contains("libs/lib"));
        Assert.DoesNotContain(ws.SubmoduleRepoIdsJson, $"{secret.Id}]");
    }

    [Fact]
    public async Task A_foreign_host_gets_no_credential_and_its_failure_is_reported_without_hanging()
    {
        using var foreign = new TcpListener(IPAddress.Loopback, 0);
        foreign.Start();
        var seen = new List<string>();
        var serving = Task.Run(async () =>
        {
            while (true)
            {
                TcpClient c;
                try { c = await foreign.AcceptTcpClientAsync(); } catch (Exception) { return; }
                _ = Task.Run(async () =>
                {
                    using var _ = c;
                    var stream = c.GetStream();
                    var buf = new byte[8192];
                    var n = await stream.ReadAsync(buf);
                    lock (seen) seen.Add(Encoding.ASCII.GetString(buf, 0, n));
                    await stream.WriteAsync("HTTP/1.1 401 Unauthorized\r\nWWW-Authenticate: Basic realm=\"x\"\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray());
                });
            }
        });
        var port = ((IPEndPoint)foreign.LocalEndpoint).Port;

        var user = await app.NewUserAsync();
        var (_, super, lib, _) = await SetupAsync(user);
        await MakeSuperAsync(super, $"[submodule \"libs/lib\"]\n\tpath = libs/lib\n\turl = ../lib.git\n[submodule \"ext/private\"]\n\tpath = ext/private\n\turl = http://127.0.0.1:{port}/org/private.git\n",
            ("libs/lib", lib), ("ext/private", lib));
        var started = DateTime.UtcNow;
        var ws = await app.StartAsync(user, super);
        foreign.Stop();

        Assert.Equal(WorkspaceState.Running, ws.State);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(60), "a failing submodule must not hang the start");
        Assert.True(File.Exists(Path.Combine(root, "super", "libs", "lib", "lib.txt")));
        var warning = Assert.Single(app.Runtime.Session(ws.Id).Warnings, w => w.Contains("ext/private"));
        Assert.Contains("could not be fetched", warning);
        Assert.Contains("needs credentials", warning); // the hint says what to do
        lock (seen)
        {
            Assert.NotEmpty(seen); // it did try the foreign host, anonymously
            Assert.All(seen, request => Assert.DoesNotContain("Authorization", request, StringComparison.OrdinalIgnoreCase));
        }
        await serving.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Submodules_can_be_switched_off_in_the_environment_file()
    {
        var user = await app.NewUserAsync();
        var (_, super, lib, _) = await SetupAsync(user);
        app.Commit(super, "main", ".bytebench/workspace.yml", "submodules: false\n");
        await MakeSuperAsync(super, "[submodule \"libs/lib\"]\n\tpath = libs/lib\n\turl = ../lib.git\n", ("libs/lib", lib));
        var ws = await app.StartAsync(user, super);
        Assert.Equal(WorkspaceState.Running, ws.State);
        Assert.False(File.Exists(Path.Combine(root, "super", "libs", "lib", "lib.txt")));
        Assert.Equal("[]", ws.SubmoduleRepoIdsJson);
        Assert.DoesNotContain("Fetching submodule", app.Runtime.Session(ws.Id).ProvisionLog.Text());
    }

    [Fact]
    public async Task Only_http_transports_are_allowed_for_submodules()
    {
        var user = await app.NewUserAsync();
        var (_, super, lib, _) = await SetupAsync(user);
        var local = git.Dir("local-evil");
        Directory.CreateDirectory(local);
        await git.OkAsync(local, "init", "-q");
        await File.WriteAllTextAsync(Path.Combine(local, "leak.txt"), "from the host's disk");
        await git.OkAsync(local, "add", "-A");
        await git.OkAsync(local, "commit", "-q", "-m", "x");
        var sha = (await git.OkAsync(local, "rev-parse", "HEAD")).Trim();
        var dir = git.Dir("super-file");
        await git.OkAsync(null, "clone", AliceUrl(super), dir);
        await File.WriteAllTextAsync(Path.Combine(dir, ".gitmodules"), $"[submodule \"evil\"]\n\tpath = evil\n\turl = file://{local}\n");
        await git.OkAsync(dir, "update-index", "--add", "--cacheinfo", $"160000,{sha},evil");
        await git.OkAsync(dir, "add", ".gitmodules");
        await git.OkAsync(dir, "commit", "-m", "file submodule");
        await git.OkAsync(dir, "push", "origin", "HEAD:main");

        var ws = await app.StartAsync(user, super);
        Assert.Equal(WorkspaceState.Running, ws.State);
        Assert.False(File.Exists(Path.Combine(root, "super", "evil", "leak.txt")));
        Assert.Contains(app.Runtime.Session(ws.Id).Warnings, w => w.Contains("evil") && w.Contains("could not be fetched"));
        Assert.NotNull(lib);
    }

    [Fact]
    public async Task A_resumed_workspace_fetches_instead_of_cloning_again()
    {
        var user = await app.NewUserAsync();
        var (_, super, lib, _) = await SetupAsync(user);
        await MakeSuperAsync(super, "[submodule \"libs/lib\"]\n\tpath = libs/lib\n\turl = ../lib.git\n", ("libs/lib", lib));
        var ws = await app.StartAsync(user, super);
        var marker = Path.Combine(root, "super", "local-edit.txt");
        await File.WriteAllTextAsync(marker, "unpushed work");
        await using (var s = app.Scope(app.UserId(user))) await s.Get<WorkspaceService>().StopAsync(ws.PublicId, app.UserId(user));
        await using (var s = app.Scope(app.UserId(user))) await s.Get<WorkspaceService>().ResumeAsync(ws.PublicId, app.UserId(user));
        await app.WhenIdleAsync();
        Assert.Equal(WorkspaceState.Running, (await app.ReloadAsync(ws.PublicId)).State);
        Assert.Equal("unpushed work", await File.ReadAllTextAsync(marker)); // the volume's checkout was kept
        Assert.True(File.Exists(Path.Combine(root, "super", "libs", "lib", "lib.txt")));
    }
}
