using System.Collections.Concurrent;
using System.Diagnostics;
using GitBench.Data;
using GitBench.Services;
using GitBench.Services.Git;
using GitBench.Services.Ssh;
using GitBench.Services.Webhooks;
using GitBench.Tests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GitBench.Tests;

/// <summary>A test that needs the OpenSSH client (<c>ssh</c>, <c>ssh-keygen</c>); it is skipped when they aren't installed.</summary>
public sealed class SshFactAttribute : FactAttribute
{
    internal static readonly Lazy<bool> Available = new(() =>
    {
        foreach (var (tool, arg) in new[] { ("ssh", "-V"), ("ssh-keygen", "-?") })
        {
            try
            {
                var psi = new ProcessStartInfo(tool) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
                psi.ArgumentList.Add(arg);
                using var p = Process.Start(psi)!;
                p.StandardError.ReadToEnd();
                if (!p.WaitForExit(10_000)) { p.Kill(); return false; }
            }
            catch (Exception) { return false; } // not installed
        }
        return true;
    });

    public SshFactAttribute()
    {
        if (!Available.Value) Skip = "The OpenSSH client (ssh, ssh-keygen) is not installed.";
    }
}

/// <summary>The app on Kestrel with the SSH server on a free port, plus a push listener that records what it was told.</summary>
public class SshApp : KestrelApp
{
    public static readonly ConcurrentQueue<(int RepositoryId, int UserId, IReadOnlyList<PushedRef> Refs)> Pushes = new();

    protected override void AddSettings(IDictionary<string, string?> settings)
    {
        settings["Ssh:Enabled"] = "true";
        settings["Ssh:Port"] = "0";
        settings["Ssh:ListenAddress"] = "127.0.0.1";
        settings["Ssh:HostKeyPath"] = Path.Combine(DataRoot, "keys", "ssh_host_rsa_key");
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureServices(s => s.AddScoped<IGitPushListener, RecordingListener>());
    }

    public int SshPort => Services.GetRequiredService<SshGitServer>().BoundPort ?? throw new InvalidOperationException("SSH server not running");

    public string SshUrl(string key, string slug) => $"ssh://git@127.0.0.1:{SshPort}/{key}/{slug}.git";

    private sealed class RecordingListener(PushCapture capture) : IGitPushListener
    {
        public Task OnPushAsync(int repositoryId, int userId, CancellationToken ct)
        {
            Pushes.Enqueue((repositoryId, userId, capture.Refs));
            return Task.CompletedTask;
        }
    }
}

/// <summary>Real <c>git</c> and <c>ssh</c> against the embedded SSH server: keys, deploy keys, permissions and branch protection.</summary>
[Trait("Category", "Ssh")]
public class SshTests(SshApp app) : IClassFixture<SshApp>, IDisposable
{
    private readonly GitCli git = new();

    public void Dispose() => git.Dispose();

    // ---------- helpers ----------

    private sealed record Key(string PrivatePath, string Public);

    private async Task<Key> NewKeyAsync(string type = "ed25519", int? bits = null)
    {
        var path = git.Dir("key-" + Guid.NewGuid().ToString("N")[..8]);
        var args = new List<string> { "-q", "-t", type, "-N", "", "-C", "test@gitbench", "-f", path };
        if (bits != null) args.InsertRange(0, ["-b", bits.ToString()!]);
        var (code, output) = await RunAsync("ssh-keygen", args);
        Assert.True(code == 0, output);
        return new Key(path, (await File.ReadAllTextAsync(path + ".pub")).Trim());
    }

    private string SshCommand(Key key) => string.Join(' ', SshArgs(key).Select(a => a.Contains(' ') ? $"'{a}'" : a));

    private IEnumerable<string> SshArgs(Key key) =>
    [
        "ssh", "-F", "/dev/null", "-i", key.PrivatePath, "-o", "IdentitiesOnly=yes", "-o", "IdentityAgent=none", "-o", "BatchMode=yes",
        "-o", "UserKnownHostsFile=" + git.Dir("known_hosts"), "-o", "GlobalKnownHostsFile=/dev/null",
        "-o", "StrictHostKeyChecking=accept-new", "-o", "LogLevel=ERROR", "-p", app.SshPort.ToString(),
    ];

    /// <summary>git with this key as its SSH identity.</summary>
    private Task<GitCli.Result> GitAsync(Key key, string? dir, params string[] args) =>
        git.RunAsync(dir, ["-c", "core.sshCommand=" + SshCommand(key), .. args]);

    private async Task<string> GitOkAsync(Key key, string? dir, params string[] args)
    {
        var r = await GitAsync(key, dir, args);
        Assert.True(r.Ok, $"git {string.Join(' ', args)} failed ({r.ExitCode}):\n{r.Output}");
        return r.Output;
    }

    private async Task<string> CloneAsync(Key key, Project p, Repository r)
    {
        var dir = git.Dir("clone-" + Guid.NewGuid().ToString("N")[..8]);
        await GitOkAsync(key, null, "clone", app.SshUrl(p.Key, r.Slug), dir);
        return dir;
    }

    private async Task CommitAsync(string dir, string file, string content, string? message = null)
    {
        await File.WriteAllTextAsync(Path.Combine(dir, file), content);
        await git.OkAsync(dir, "add", file);
        await git.OkAsync(dir, "commit", "-m", message ?? "edit " + file);
    }

    /// <summary>Runs ssh itself (no git) with <paramref name="remoteCommand"/>, or no command for a shell.</summary>
    private Task<(int Code, string Output)> SshAsync(Key key, params string[] remoteCommand) =>
        RunAsync("ssh", [.. SshArgs(key).Skip(1), "-T", "git@127.0.0.1", .. remoteCommand]);

    private static async Task<(int Code, string Output)> RunAsync(string exe, IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment.Remove("SSH_AUTH_SOCK");
        using var p = Process.Start(psi)!;
        p.StandardInput.Close();
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await p.WaitForExitAsync(cts.Token);
        return (p.ExitCode, await stdout + await stderr);
    }

    private async Task<Key> UserKeyAsync(string user, string type = "ed25519", int? bits = null)
    {
        var key = await NewKeyAsync(type, bits);
        await using var s = app.Scope();
        await s.Get<SshKeyService>().AddUserKeyAsync(app.UserId(user), "test", key.Public);
        return key;
    }

    private async Task<Key> DeployKeyAsync(Repository repo, bool canWrite)
    {
        var key = await NewKeyAsync();
        await using var s = app.Scope(app.UserId("carol"));
        await s.Get<SshKeyService>().AddDeployKeyAsync(repo.Id, "deploy", key.Public, canWrite);
        return key;
    }

    private async Task<(Project Project, Repository Repo)> SetupAsync()
    {
        var project = await app.CreateProjectAsync("carol", ProjectVisibility.Private,
            ("bob", ProjectRole.Developer), ("dan", ProjectRole.Maintainer));
        return (project, await app.CreateRepoAsync(project.Id, "carol"));
    }

    private async Task<string> HeadAsync(Repository r, string branch = "main")
    {
        await using var s = app.Scope(system: true);
        var log = await s.Get<RepositoryService>().Git(r, path => GitOps.Log(path, branch, null, 0, 1));
        return log.Items[0].MessageShort;
    }

    // ---------- tests ----------

    [SshFact]
    public async Task Clone_with_user_keys_of_every_type_and_record_last_use()
    {
        var (p, r) = await SetupAsync();
        foreach (var (type, bits) in new (string, int?)[] { ("ed25519", null), ("ecdsa", 256), ("rsa", 3072) })
        {
            var key = await UserKeyAsync("bob", type, bits);
            var dir = await CloneAsync(key, p, r);
            Assert.True(File.Exists(Path.Combine(dir, "README.md")), type);
        }
        await using var db = app.Db();
        Assert.All(await db.SshKeys.Where(k => k.UserId == app.UserId("bob")).ToListAsync(), k => Assert.NotNull(k.LastUsedAt));
    }

    [SshFact]
    public async Task Developer_can_push_and_push_listeners_run_with_the_ref_updates()
    {
        var (p, r) = await SetupAsync();
        var issue = await app.CreateIssueAsync(p.Id, "carol", "Track SSH pushes");
        var key = await UserKeyAsync("bob");
        var dir = await CloneAsync(key, p, r);
        await CommitAsync(dir, "a.txt", "a", $"{p.Key}-{issue.Number}: push over ssh");
        await GitOkAsync(key, dir, "push", "origin", "main");
        Assert.Equal($"{p.Key}-{issue.Number}: push over ssh", await HeadAsync(r));

        // Listeners run once the client is answered: wait for them.
        var bob = app.UserId("bob");
        (int RepositoryId, int UserId, IReadOnlyList<PushedRef> Refs) push = default;
        for (var i = 0; i < 50 && !SshApp.Pushes.Any(x => x.RepositoryId == r.Id); i++) await Task.Delay(100);
        push = SshApp.Pushes.First(x => x.RepositoryId == r.Id);
        Assert.Equal(bob, push.UserId);
        Assert.Equal("refs/heads/main", Assert.Single(push.Refs).Ref);

        // Smart commits (IssueCommitPushListener) linked the commit to the issue.
        await using var db = app.Db();
        for (var i = 0; i < 50 && !await db.Activities.AnyAsync(a => a.IssueId == issue.Id && a.Text.Contains("push over ssh")); i++) await Task.Delay(100);
        Assert.True(await db.Activities.AnyAsync(a => a.IssueId == issue.Id && a.Text.Contains("push over ssh")));
    }

    [SshFact]
    public async Task Viewer_cannot_push()
    {
        var (p, r) = await SetupAsync();
        await app.CreateUserAsync("sshviewer");
        await using (var s = app.Scope(app.UserId("carol")))
            await s.Get<MemberService>().AddMemberAsync(p.Id, "sshviewer", ProjectRole.Viewer);
        var key = await UserKeyAsync("sshviewer");
        var dir = await CloneAsync(key, p, r);
        await CommitAsync(dir, "v.txt", "v");
        var push = await GitAsync(key, dir, "push", "origin", "main");
        Assert.False(push.Ok);
        Assert.Contains("requires Developer; you are Viewer", push.Output);
    }

    [SshFact]
    public async Task Non_members_get_repository_not_found()
    {
        var (p, r) = await SetupAsync();
        await app.CreateUserAsync("sshoutsider");
        var key = await UserKeyAsync("sshoutsider");
        var hidden = await GitAsync(key, null, "ls-remote", app.SshUrl(p.Key, r.Slug));
        var missing = await GitAsync(key, null, "ls-remote", app.SshUrl(p.Key, "no-such-repo"));
        Assert.False(hidden.Ok);
        Assert.Contains("Repository not found", hidden.Output);
        Assert.Equal(missing.Output, hidden.Output);
    }

    [SshFact]
    public async Task Protected_branches_refuse_direct_and_force_pushes_with_a_message()
    {
        var (p, r) = await SetupAsync();
        await using (var s = app.Scope(app.UserId("carol")))
        {
            await s.Get<BranchProtectionService>().SaveAsync(r.Id, new BranchProtectionRule { Pattern = "main", BlockDirectPushes = true });
            await s.Get<BranchProtectionService>().SaveAsync(r.Id, new BranchProtectionRule { Pattern = "release/*", BlockForcePushes = true });
        }
        var key = await UserKeyAsync("dan");
        var dir = await CloneAsync(key, p, r);
        await CommitAsync(dir, "c.txt", "c");
        var push = await GitAsync(key, dir, "push", "origin", "main");
        Assert.False(push.Ok);
        Assert.Contains("[remote rejected] main -> main", push.Output);
        Assert.Contains("changes must go through a pull request", push.Output);
        Assert.Equal("Initial commit", await HeadAsync(r)); // nothing was written

        // Fast-forwards to release/* are fine; rewriting it is caught by the pre-receive hook.
        await GitOkAsync(key, dir, "push", "origin", "HEAD:refs/heads/release/1.0");
        await CommitAsync(dir, "d.txt", "d");
        await GitOkAsync(key, dir, "push", "origin", "HEAD:refs/heads/release/1.0");
        await git.OkAsync(dir, "commit", "--amend", "-m", "rewritten");
        var force = await GitAsync(key, dir, "push", "--force", "origin", "HEAD:refs/heads/release/1.0");
        Assert.False(force.Ok);
        Assert.Contains("force pushes are not allowed", force.Output);
    }

    [SshFact]
    public async Task Archived_repository_refuses_pushes_but_can_be_cloned()
    {
        var (p, r) = await SetupAsync();
        var key = await UserKeyAsync("dan");
        var dir = await CloneAsync(key, p, r);
        await using (var s = app.Scope(app.UserId("carol")))
            await s.Get<RepositorySettingsService>().SetArchivedAsync(r.Id, true);
        await CommitAsync(dir, "x.txt", "x");
        var push = await GitAsync(key, dir, "push", "origin", "main");
        Assert.False(push.Ok);
        Assert.Contains("archived", push.Output);
        await CloneAsync(key, p, r);
    }

    [SshFact]
    public async Task Protected_tags_and_pull_mirrors_refuse_pushes()
    {
        var (p, r) = await SetupAsync();
        await using (var s = app.Scope(app.UserId("carol")))
            await s.Get<ProtectedTagService>().SaveAsync(r.Id, new ProtectedTagRule { Pattern = "v*", CreateRole = ProjectRole.Maintainer });
        var key = await UserKeyAsync("dan");
        var dir = await CloneAsync(key, p, r);
        await GitOkAsync(key, dir, "push", "origin", "HEAD:refs/tags/v1"); // a Maintainer may create it
        var delete = await GitAsync(key, dir, "push", "origin", ":refs/tags/v1");
        Assert.False(delete.Ok);
        Assert.Contains("tag 'v1' is protected and can't be deleted", delete.Output);

        await using (var s = app.Scope(app.UserId("carol")))
            await s.Get<GitBench.Services.Mirroring.MirrorService>().SavePullAsync(r.Id,
                new GitBench.Services.Mirroring.MirrorInput { Url = "https://upstream.invalid/upstream.git", Enabled = true });
        await CommitAsync(dir, "m.txt", "m");
        var push = await GitAsync(key, dir, "push", "origin", "main");
        Assert.False(push.Ok);
        Assert.Contains("pull mirror", push.Output);
    }

    [SshFact]
    public async Task Deploy_keys_are_limited_to_their_repository_and_read_only_by_default()
    {
        var (p, r) = await SetupAsync();
        var other = await app.CreateRepoAsync(p.Id, "carol", "other");
        var readOnly = await DeployKeyAsync(r, canWrite: false);

        var dir = await CloneAsync(readOnly, p, r);
        await CommitAsync(dir, "ro.txt", "ro");
        var push = await GitAsync(readOnly, dir, "push", "origin", "main");
        Assert.False(push.Ok);
        Assert.Contains("deploy key is read-only", push.Output);

        var elsewhere = await GitAsync(readOnly, null, "ls-remote", app.SshUrl(p.Key, other.Slug));
        Assert.False(elsewhere.Ok);
        Assert.Contains("Repository not found", elsewhere.Output);

        // A write key pushes like a Developer: branch protection still applies, and admins' bypass doesn't.
        var writer = await DeployKeyAsync(r, canWrite: true);
        await GitOkAsync(writer, dir, "push", "origin", "HEAD:refs/heads/deploy");
        await using (var s = app.Scope(app.UserId("carol")))
            await s.Get<BranchProtectionService>().SaveAsync(r.Id, new BranchProtectionRule { Pattern = "main", BlockDirectPushes = true, AllowAdminBypass = true });
        var protectedPush = await GitAsync(writer, dir, "push", "origin", "main");
        Assert.False(protectedPush.Ok);
        Assert.Contains("[remote rejected] main -> main", protectedPush.Output);
    }

    [SshFact]
    public async Task Unknown_expired_and_deactivated_users_keys_are_refused()
    {
        var (p, r) = await SetupAsync();
        var unknown = await NewKeyAsync();
        var refused = await GitAsync(unknown, null, "ls-remote", app.SshUrl(p.Key, r.Slug));
        Assert.False(refused.Ok);
        Assert.Contains("Permission denied (publickey)", refused.Output);

        await app.CreateUserAsync("sshleaver");
        await using (var s = app.Scope(app.UserId("carol")))
            await s.Get<MemberService>().AddMemberAsync(p.Id, "sshleaver", ProjectRole.Developer);
        var key = await UserKeyAsync("sshleaver");
        await GitOkAsync(key, null, "ls-remote", app.SshUrl(p.Key, r.Slug));
        await using (var db = app.Db())
            await db.Users.Where(u => u.UserName == "sshleaver").ExecuteUpdateAsync(u => u.SetProperty(x => x.DeactivatedAt, DateTime.UtcNow));
        Assert.Contains("Permission denied (publickey)", (await GitAsync(key, null, "ls-remote", app.SshUrl(p.Key, r.Slug))).Output);

        var expiring = await UserKeyAsync("bob");
        await using (var db = app.Db())
            await db.SshKeys.Where(k => k.PublicKey == expiring.Public.Split(' ')[1]).ExecuteUpdateAsync(u => u.SetProperty(x => x.ExpiresAt, DateTime.UtcNow.AddMinutes(-1)));
        Assert.Contains("Permission denied (publickey)", (await GitAsync(expiring, null, "ls-remote", app.SshUrl(p.Key, r.Slug))).Output);
    }

    [SshFact]
    public async Task Shells_and_other_commands_are_refused_with_a_friendly_message()
    {
        var key = await UserKeyAsync("alice");
        var (shellCode, shell) = await SshAsync(key);
        Assert.Equal(1, shellCode);
        Assert.Contains("Hi alice! You've successfully authenticated, but GitBench does not provide shell access.", shell);

        var (lsCode, ls) = await SshAsync(key, "ls", "-la");
        Assert.NotEqual(0, lsCode);
        Assert.Contains("GitBench only runs git over SSH", ls);

        // A path outside KEY/slug.git is not a repository.
        var (_, escape) = await SshAsync(key, "git-upload-pack", "'../../etc'");
        Assert.Contains("only runs git over SSH", escape);
    }

    [SshFact]
    public async Task A_client_that_closes_its_input_ends_the_session()
    {
        var (p, r) = await SetupAsync();
        var key = await UserKeyAsync("bob");
        foreach (var service in new[] { "git-receive-pack", "git-upload-pack" })
        {
            // stdin is closed right away: git ends (upload-pack with "hung up", exit 128) instead of the session hanging.
            var (_, output) = await SshAsync(key, service, $"'{p.Key}/{r.Slug}.git'");
            Assert.Contains("refs/heads/main", output);
        }
    }

    [SshFact]
    public async Task Port_forwarding_is_refused()
    {
        var key = await UserKeyAsync("alice");
        var (code, output) = await RunAsync("ssh", [.. SshArgs(key).Skip(1), "-o", "ExitOnForwardFailure=yes", "-N", "-W", "127.0.0.1:22", "git@127.0.0.1"]);
        Assert.NotEqual(0, code);
        Assert.Contains("forwarding failed", output);
    }
}

/// <summary>Key parsing, uniqueness and command parsing, without the ssh client.</summary>
public class SshKeyTests(TestApp app) : IClassFixture<TestApp>
{
    private const string Ed25519 = "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIOMqqnkVzrm0SdG6UOoqKLsabgH5C9okWi0dh2l9GKJl laptop";

    [Fact]
    public void Parses_and_fingerprints_an_ed25519_key()
    {
        var key = SshPublicKey.Parse(Ed25519 + "\n");
        Assert.Equal("ssh-ed25519", key.KeyType);
        Assert.Equal("laptop", key.Comment);
        Assert.Equal(256, key.Bits);
        Assert.StartsWith("SHA256:", key.Fingerprint);
        Assert.Equal(50, key.Fingerprint.Length); // "SHA256:" + 43 unpadded base64 characters
    }

    [Theory]
    [InlineData("", "Paste a public key")]
    [InlineData("ssh-dss AAAAB3NzaC1kc3MAAACBAP", "Unsupported key type")]
    [InlineData("sk-ssh-ed25519@openssh.com AAAA", "Security-key")]
    [InlineData("ssh-ed25519 not-base64!", "base64")]
    [InlineData("ssh-rsa AAAAC3NzaC1lZDI1NTE5AAAAIOMqqnkVzrm0SdG6UOoqKLsabgH5C9okWi0dh2l9GKJl", "doesn't match its type")]
    [InlineData("-----BEGIN OPENSSH PRIVATE KEY-----", "private key")]
    public void Rejects_bad_keys(string text, string message) =>
        Assert.Contains(message, Assert.Throws<FormatException>(() => SshPublicKey.Parse(text)).Message);

    [Fact]
    public void Rejects_short_rsa_keys()
    {
        using var rsa = System.Security.Cryptography.RSA.Create(1024);
        var p = rsa.ExportParameters(false);
        var blob = new MemoryStream();
        void Write(byte[] b) { blob.Write([(byte)(b.Length >> 24), (byte)(b.Length >> 16), (byte)(b.Length >> 8), (byte)b.Length]); blob.Write(b); }
        Write("ssh-rsa"u8.ToArray());
        Write(p.Exponent!);
        Write([0, .. p.Modulus!]);
        var ex = Assert.Throws<FormatException>(() => SshPublicKey.Parse("ssh-rsa " + Convert.ToBase64String(blob.ToArray())));
        Assert.Contains("at least 2048 bits", ex.Message);
    }

    [Fact]
    public async Task A_key_can_only_be_added_once_across_users_and_deploy_keys()
    {
        var project = await app.CreateProjectAsync("carol", ProjectVisibility.Private, ("bob", ProjectRole.Developer));
        var repo = await app.CreateRepoAsync(project.Id, "carol");
        await using var s = app.Scope(app.UserId("carol"));
        var keys = s.Get<SshKeyService>();
        var added = await keys.AddUserKeyAsync(app.UserId("bob"), "", Ed25519);
        Assert.Equal("laptop", added.Title); // the comment is the default title

        var dup = await Assert.ThrowsAsync<InvalidOperationException>(() => keys.AddUserKeyAsync(app.UserId("alice"), "again", Ed25519));
        Assert.Contains("already in use", dup.Message);
        await Assert.ThrowsAsync<InvalidOperationException>(() => keys.AddDeployKeyAsync(repo.Id, "deploy", Ed25519, false));

        // Developers can't manage deploy keys.
        await using (var bob = app.Scope(app.UserId("bob")))
            await Assert.ThrowsAsync<ForbiddenException>(() => bob.Get<SshKeyService>().ListDeployKeysAsync(repo.Id));

        await keys.DeleteUserKeyAsync(app.UserId("bob"), added.Id);
        var deploy = await keys.AddDeployKeyAsync(repo.Id, "deploy", Ed25519, true);
        Assert.True(deploy.CanWrite);

        await using var db = app.Db();
        var actions = await db.AuditEvents.Where(e => e.TargetType == "ssh_key" || e.TargetType == "deploy_key").Select(e => e.Action).ToListAsync();
        Assert.Contains("ssh_key.added", actions);
        Assert.Contains("ssh_key.removed", actions);
        Assert.Contains("deploy_key.added", actions);
    }

    [Theory]
    [InlineData("git-upload-pack '/WEB/web-app.git'", "git-upload-pack", "WEB", "web-app")]
    [InlineData("git-receive-pack 'WEB/web-app.git'", "git-receive-pack", "WEB", "web-app")]
    [InlineData("git upload-pack 'WEB/web-app'", "git-upload-pack", "WEB", "web-app")]
    [InlineData("git-upload-pack /web/app.git/", "git-upload-pack", "web", "app")]
    public void Parses_git_commands(string command, string service, string key, string slug)
    {
        var parsed = GitBench.Services.Ssh.SshGitCommandParser.Parse(command);
        Assert.NotNull(parsed);
        Assert.Equal((service, key, slug), (parsed.Service, parsed.ProjectKey, parsed.Slug));
    }

    [Theory]
    [InlineData("ls")]
    [InlineData("git-upload-archive 'WEB/web-app.git'")]
    [InlineData("git-upload-pack '../../etc/passwd'")]
    [InlineData("git-upload-pack 'WEB/web-app.git'; rm -rf /")]
    [InlineData("git-upload-pack 'a/b/c.git'")]
    public void Rejects_other_commands(string command) => Assert.Null(GitBench.Services.Ssh.SshGitCommandParser.Parse(command));
}
