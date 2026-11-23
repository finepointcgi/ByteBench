using System.Diagnostics;
using ByteBench.Data;
using ByteBench.Services;
using ByteBench.Services.Git;
using ByteBench.Services.Signing;
using ByteBench.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ByteBench.Tests;

/// <summary>Skips a test when <c>ssh-keygen</c> (OpenSSH 8.0+, used to sign and verify) isn't installed.</summary>
public sealed class SshKeygenFactAttribute : FactAttribute
{
    internal static readonly Lazy<bool> Available = new(() =>
    {
        try
        {
            var psi = new ProcessStartInfo("ssh-keygen") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var a in new[] { "-Y", "check-novalidate" }) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            p.WaitForExit(10_000);
            return true; // it ran (a usage error is fine)
        }
        catch (Exception) { return false; }
    });

    public SshKeygenFactAttribute()
    {
        if (!Available.Value) Skip = "ssh-keygen is not installed.";
    }
}

/// <summary>Skips a test when <c>gpg</c> isn't installed.</summary>
public sealed class GpgFactAttribute : FactAttribute
{
    internal static readonly Lazy<bool> Available = new(() =>
    {
        try
        {
            var psi = new ProcessStartInfo("gpg") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            psi.ArgumentList.Add("--version");
            using var p = Process.Start(psi)!;
            p.StandardOutput.ReadToEnd();
            return p.WaitForExit(10_000) && p.ExitCode == 0;
        }
        catch (Exception) { return false; }
    });

    public GpgFactAttribute()
    {
        if (!Available.Value) Skip = "gpg is not installed.";
    }
}

/// <summary>The app with no gpg / ssh-keygen configured (they point nowhere): signed commits can't be checked.</summary>
public class NoSigningToolsApp : KestrelApp
{
    protected override void AddSettings(IDictionary<string, string?> settings)
    {
        settings["Signing:GpgPath"] = "/nonexistent/gpg";
        settings["Signing:SshKeygenPath"] = "/nonexistent/ssh-keygen";
    }
}

/// <summary>Reading signatures out of commit objects, GPG key listings and the sanitising that needs no tools.</summary>
public class SignatureParsingTests
{
    private const string Sig = "-----BEGIN SSH SIGNATURE-----\nU1NIU0lHAAAAAQAAADMAAAALc3NoLWVkMjU1MTkAAAAg\n\nAAAAA2dpdAAAAAAAAAAGc2hhNTEyAAAAUwAAAAtzc2gt\n-----END SSH SIGNATURE-----";

    private static byte[] Commit(string signatureBlock) => System.Text.Encoding.Latin1.GetBytes(
        "tree 4b825dc642cb6eb9a060e54bf8d69288fbee4904\nauthor A <a@example.com> 1700000000 +0000\ncommitter A <A@Example.com> 1700000000 +0000\n"
        + signatureBlock + "\n\nadd thing\n\nbody line\n");

    [Fact]
    public void A_signed_commit_is_split_into_signature_and_the_original_payload()
    {
        var lines = Sig.Split('\n');
        var header = "gpgsig " + lines[0] + "\n" + string.Join("\n", lines.Skip(1).Select(l => " " + l));
        var parsed = CommitSignatureParser.Parse(Commit(header), out var unsupported);
        Assert.NotNull(parsed);
        Assert.Null(unsupported);
        Assert.Equal("ssh", parsed.Format);
        Assert.Equal(Sig + "\n", parsed.Signature);
        Assert.Equal("A@Example.com", parsed.CommitterEmail);
        var payload = System.Text.Encoding.Latin1.GetString(parsed.Payload);
        Assert.DoesNotContain("gpgsig", payload);
        Assert.DoesNotContain("BEGIN SSH", payload);
        Assert.Equal("tree 4b825dc642cb6eb9a060e54bf8d69288fbee4904\nauthor A <a@example.com> 1700000000 +0000\ncommitter A <A@Example.com> 1700000000 +0000\n\nadd thing\n\nbody line\n", payload);
    }

    [Fact]
    public void An_unsigned_commit_has_no_signature_and_a_sha256_header_is_unsupported()
    {
        Assert.Null(CommitSignatureParser.Parse(System.Text.Encoding.Latin1.GetBytes("tree x\ncommitter A <a@example.com> 1 +0000\n\nmsg\n"), out _));
        Assert.Null(CommitSignatureParser.Parse(System.Text.Encoding.Latin1.GetBytes("tree x\ngpgsig-sha256 abc\ncommitter A <a@example.com> 1 +0000\n\nmsg\n"), out var unsupported));
        Assert.Contains("SHA-256", unsupported);
    }

    [Fact]
    public void Gpg_key_listings_give_fingerprints_emails_expiry_and_revocation()
    {
        const string ok = """
            tru::1:1700000000:0:3:1:5
            pub:u:255:22:AAAABBBBCCCCDDDD:1700000000:1900000000::u:::scESC:::::ed25519:::0:
            fpr:::::::::1111222233334444555566667777888899990000:
            uid:u::::1700000000::HASH::Alice Chen <Alice@Example.com>::::::::::0:
            uid:u::::1700000000::HASH2::Alice Work <alice@work.example>::::::::::0:
            sub:u:255:18:EEEEFFFFAAAABBBB:1700000000:1900000000:::::e:::::cv25519::
            fpr:::::::::AAAA222233334444555566667777888899990000:
            """;
        var key = GpgKeyParser.Parse(ok)!;
        Assert.Equal("1111222233334444555566667777888899990000", key.Fingerprint);
        Assert.Equal(2, key.Fingerprints.Count);
        Assert.Contains("alice@example.com", key.Emails);
        Assert.Contains("alice@work.example", key.Emails);
        Assert.Equal(1, key.PrimaryKeys);
        Assert.False(key.Revoked);
        Assert.False(key.Expired);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1900000000).UtcDateTime, key.ExpiresAt);
        Assert.Equal("ed25519", key.Algorithm);

        var revoked = GpgKeyParser.Parse("pub:r:255:22:AAAABBBBCCCCDDDD:1700000000:::r:::sc:::::ed25519:::0:\nfpr:::::::::1111222233334444555566667777888899990000:\nuid:r::::1700000000::H::A <a@example.com>::::::::::0:\n")!;
        Assert.True(revoked.Revoked);
        Assert.Empty(revoked.Emails); // a revoked user ID doesn't count
        var expired = GpgKeyParser.Parse("pub:e:3072:1:AAAABBBBCCCCDDDD:1700000000:1700000100::u:::sc:::::::\nfpr:::::::::1111222233334444555566667777888899990000:\n")!;
        Assert.True(expired.Expired);
        Assert.Equal("RSA 3072", expired.Algorithm);
        var two = GpgKeyParser.Parse("pub:u:255:22:A1:1700000000::::::sc:::::::\nfpr:::::::::1111222233334444555566667777888899990000:\npub:u:255:22:B2:1700000000::::::sc:::::::\nfpr:::::::::2222222233334444555566667777888899990000:\n")!;
        Assert.Equal(2, two.PrimaryKeys);
        Assert.Null(GpgKeyParser.Parse("gpg: no valid OpenPGP data found"));
    }
}

/// <summary>Signed commits end to end: real <c>git commit -S</c> with throw-away SSH keys (never <c>~/.ssh</c>), pushed to the app.</summary>
public class SignedCommitTests(KestrelApp app) : IClassFixture<KestrelApp>, IDisposable
{
    private readonly GitCli git = new();
    private const string Password = SeedData.DemoPassword;

    public void Dispose() => git.Dispose();

    private sealed record Setup(Project Project, Repository Repo, string Path);

    /// <summary>alice Owner, bob Developer, carol Maintainer.</summary>
    private async Task<Setup> SetupAsync()
    {
        var project = await app.CreateProjectAsync("alice", ProjectVisibility.Private, ("bob", ProjectRole.Developer), ("carol", ProjectRole.Maintainer));
        var repo = await app.CreateRepoAsync(project.Id, "alice");
        return new Setup(project, repo, app.Services.GetRequiredService<RepoStorage>().PathFor(project.Key, repo.Slug));
    }

    /// <summary>A new ed25519 key pair in the test's own temp folder. Returns the private key path.</summary>
    private async Task<string> NewKeyAsync(string name)
    {
        var dir = git.Dir("keys");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name);
        var psi = new ProcessStartInfo("ssh-keygen") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in new[] { "-q", "-t", "ed25519", "-N", "", "-C", name, "-f", path }) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        await p.WaitForExitAsync();
        Assert.Equal(0, p.ExitCode);
        return path;
    }

    private async Task RegisterAsync(string user, string keyPath, DateTime? expires = null)
    {
        await using var s = app.Scope(app.UserId(user));
        await s.Get<ByteBench.Services.Ssh.SshKeyService>().AddUserKeyAsync(app.UserId(user), "test key", await File.ReadAllTextAsync(keyPath + ".pub"), expires);
    }

    private string Email(string user) => user + "@example.com";

    private async Task<string> CloneAsync(Setup t, string user)
    {
        var dir = git.Dir("clone-" + Guid.NewGuid().ToString("N")[..8]);
        await git.OkAsync(null, "clone", app.GitUrl(t.Project.Key, t.Repo.Slug, user, Password), dir);
        return dir;
    }

    /// <summary>Commits a file; signed with <paramref name="key"/> (SSH) when given. Returns the new commit's sha.</summary>
    private async Task<string> CommitAsync(string dir, string file, string content, string email, string? key, string message = "edit")
    {
        await File.WriteAllTextAsync(Path.Combine(dir, file), content);
        await git.OkAsync(dir, "add", file);
        string[] sign = key == null ? ["-c", "commit.gpgsign=false"] : ["-c", "gpg.format=ssh", "-c", "user.signingkey=" + key, "-c", "commit.gpgsign=true"];
        await git.OkAsync(dir, [.. sign, "-c", "user.email=" + email, "commit", "-m", message]);
        return (await git.OkAsync(dir, "rev-parse", "HEAD")).Trim();
    }

    private async Task<GitCli.Result> PushAsync(string dir, string refName) => await git.RunAsync(dir, "push", "origin", "HEAD:refs/heads/" + refName);

    private async Task<CommitSignatureResult> VerifyAsync(Setup t, string sha)
    {
        await using var s = app.Scope();
        return await s.Get<CommitSignatureService>().GetAsync(t.Repo, sha);
    }

    // ---------- Verification ----------

    [SshKeygenFact]
    public async Task A_good_signature_by_a_registered_key_with_a_matching_email_is_verified_and_cached()
    {
        var t = await SetupAsync();
        var key = await NewKeyAsync("alice");
        await RegisterAsync("alice", key);
        var dir = await CloneAsync(t, "alice");
        var sha = await CommitAsync(dir, "a.txt", "a", Email("alice"), key);
        Assert.True((await PushAsync(dir, "feature")).Ok);

        var result = await VerifyAsync(t, sha);
        Assert.Equal(CommitSignatureStatus.Verified, result.Status);
        Assert.Equal("ssh", result.Format);
        Assert.Equal(app.UserId("alice"), result.SignerUserId);
        Assert.StartsWith("Verified", result.Tooltip);
        Assert.Contains("confirmed email", result.Tooltip);

        await using var db = app.Db();
        var cached = await db.CommitSignatures.AsNoTracking().SingleAsync(c => c.Sha == sha);
        Assert.Equal(CommitSignatureStatus.Verified, cached.Status);
        Assert.Equal(CommitSignatureStatus.Verified, (await VerifyAsync(t, sha)).Status); // second call: from the cache
        Assert.Equal(1, await db.CommitSignatures.CountAsync(c => c.Sha == sha));
    }

    [SshKeygenFact]
    public async Task An_unsigned_commit_is_unsigned_and_ByteBench_s_own_merge_commit_counts_as_verified()
    {
        var t = await SetupAsync();
        var dir = await CloneAsync(t, "bob");
        await git.OkAsync(dir, "checkout", "-b", "feat");
        var sha = await CommitAsync(dir, "u.txt", "u", Email("bob"), null);
        Assert.True((await PushAsync(dir, "feat")).Ok);
        var result = await VerifyAsync(t, sha);
        Assert.Equal(CommitSignatureStatus.Unsigned, result.Status);
        Assert.Equal("Not signed", result.Tooltip);

        PullRequest pr;
        await using (var s = app.Scope(app.UserId("bob")))
            pr = await s.Get<PullRequestService>().CreateAsync(t.Repo, "Add u", null, "feat", "main", app.UserId("bob"));
        await using (var s = app.Scope(app.UserId("carol")))
            Assert.True((await s.Get<PullRequestService>().MergeAsync(pr.Id, app.User("carol"), new MergeRequest())).Success);
        string mergeSha;
        await using (var db = app.Db()) mergeSha = (await db.PullRequests.AsNoTracking().FirstAsync(p => p.Id == pr.Id)).MergeCommitSha!;
        var merged = await VerifyAsync(t, mergeSha);
        Assert.Equal(CommitSignatureStatus.Verified, merged.Status);
        Assert.Equal("bytebench", merged.Format);
        Assert.Contains("ByteBench's own merge", merged.Tooltip);
    }

    [SshKeygenFact]
    public async Task A_forged_signature_is_unverified()
    {
        var t = await SetupAsync();
        var key = await NewKeyAsync("alice");
        await RegisterAsync("alice", key);
        var dir = await CloneAsync(t, "alice");
        var good = await CommitAsync(dir, "a.txt", "a", Email("alice"), key, "the honest message");

        // Same signature header, different content: build the commit object by hand and give it a branch.
        var raw = await git.OkAsync(dir, "cat-file", "commit", good);
        var tampered = raw.Replace("the honest message", "a forged message!!");
        var file = git.Dir("forged.txt");
        await File.WriteAllTextAsync(file, tampered);
        var forged = (await git.OkAsync(dir, "hash-object", "-t", "commit", "-w", file)).Trim();
        Assert.NotEqual(good, forged);
        Assert.True((await git.RunAsync(dir, "push", "origin", forged + ":refs/heads/forged")).Ok);

        var result = await VerifyAsync(t, forged);
        Assert.Equal(CommitSignatureStatus.Unverified, result.Status);
        Assert.Contains("doesn't match", result.Reason);
        Assert.Equal("Unverified (" + result.Reason + ")", result.Tooltip);
    }

    [SshKeygenFact]
    public async Task A_key_nobody_registered_is_unverified()
    {
        var t = await SetupAsync();
        var stranger = await NewKeyAsync("stranger");
        var dir = await CloneAsync(t, "alice");
        var sha = await CommitAsync(dir, "s.txt", "s", Email("alice"), stranger);
        Assert.True((await PushAsync(dir, "feature")).Ok);
        var result = await VerifyAsync(t, sha);
        Assert.Equal(CommitSignatureStatus.Unverified, result.Status);
        Assert.Contains("isn't registered", result.Reason);
    }

    [SshKeygenFact]
    public async Task The_committer_email_must_be_a_confirmed_email_of_the_keys_owner()
    {
        var t = await SetupAsync();
        var key = await NewKeyAsync("alice");
        await RegisterAsync("alice", key);
        var dir = await CloneAsync(t, "alice");

        var other = await CommitAsync(dir, "w.txt", "w", "someone.else@example.com", key, "wrong email");
        Assert.True((await PushAsync(dir, "wrong-email")).Ok);
        var result = await VerifyAsync(t, other);
        Assert.Equal(CommitSignatureStatus.Unverified, result.Status);
        Assert.Contains("isn't a confirmed email of", result.Reason);

        // Bob's email on a commit signed with ALICE's key: the key doesn't belong to bob.
        var bobs = await CommitAsync(dir, "b.txt", "b", Email("bob"), key, "bob's email, alice's key");
        Assert.True((await PushAsync(dir, "bobs-email")).Ok);
        Assert.Equal(CommitSignatureStatus.Unverified, (await VerifyAsync(t, bobs)).Status);

        // An unconfirmed email never verifies.
        await using (var db = app.Db()) await db.Users.Where(u => u.UserName == "alice").ExecuteUpdateAsync(s => s.SetProperty(u => u.EmailConfirmed, false));
        try
        {
            var ok = await CommitAsync(dir, "c.txt", "c", Email("alice"), key, "right email, unconfirmed");
            Assert.True((await PushAsync(dir, "unconfirmed")).Ok);
            Assert.Equal(CommitSignatureStatus.Unverified, (await VerifyAsync(t, ok)).Status);
        }
        finally { await using var db = app.Db(); await db.Users.Where(u => u.UserName == "alice").ExecuteUpdateAsync(s => s.SetProperty(u => u.EmailConfirmed, true)); }
    }

    [SshKeygenFact]
    public async Task Removing_a_key_revokes_it_and_an_expired_key_stops_verifying()
    {
        var t = await SetupAsync();
        var key = await NewKeyAsync("carol");
        await RegisterAsync("carol", key, expires: DateTime.UtcNow.AddDays(30));
        var dir = await CloneAsync(t, "carol");
        var sha = await CommitAsync(dir, "k.txt", "k", Email("carol"), key);
        Assert.True((await PushAsync(dir, "feature")).Ok);
        Assert.Equal(CommitSignatureStatus.Verified, (await VerifyAsync(t, sha)).Status);

        // Time passes: the key's expiry date is now in the past and the cached "good until" has run out.
        await using (var db = app.Db())
        {
            await db.SshKeys.Where(k => k.UserId == app.UserId("carol")).ExecuteUpdateAsync(s => s.SetProperty(k => k.ExpiresAt, DateTime.UtcNow.AddDays(-1)));
            await db.CommitSignatures.Where(c => c.Sha == sha).ExecuteUpdateAsync(s => s.SetProperty(c => c.ValidUntil, DateTime.UtcNow.AddDays(-1)));
        }
        var expired = await VerifyAsync(t, sha);
        Assert.Equal(CommitSignatureStatus.Unverified, expired.Status);
        Assert.Contains("expired", expired.Reason);

        // Re-register a fresh key and verify, then delete it: the cached result is dropped because the key set changed.
        var key2 = await NewKeyAsync("carol2");
        await RegisterAsync("carol", key2);
        var sha2 = await CommitAsync(dir, "k2.txt", "k2", Email("carol"), key2);
        Assert.True((await PushAsync(dir, "feature2")).Ok);
        Assert.Equal(CommitSignatureStatus.Verified, (await VerifyAsync(t, sha2)).Status);
        await using (var s = app.Scope(app.UserId("carol")))
        {
            var keys = await s.Get<ByteBench.Services.Ssh.SshKeyService>().ListUserKeysAsync(app.UserId("carol"));
            foreach (var k in keys.Where(k => k.Title == "test key" && k.ExpiresAt == null)) await s.Get<ByteBench.Services.Ssh.SshKeyService>().DeleteUserKeyAsync(app.UserId("carol"), k.Id);
        }
        var revoked = await VerifyAsync(t, sha2);
        Assert.Equal(CommitSignatureStatus.Unverified, revoked.Status);
        Assert.Contains("isn't registered", revoked.Reason);
    }

    [SshKeygenFact]
    public async Task A_deploy_key_cannot_sign_for_anyone()
    {
        var t = await SetupAsync();
        var key = await NewKeyAsync("deploy");
        await using (var s = app.Scope(app.UserId("alice")))
            await s.Get<ByteBench.Services.Ssh.SshKeyService>().AddDeployKeyAsync(t.Repo.Id, "ci", await File.ReadAllTextAsync(key + ".pub"), false, app.UserId("alice"));
        var dir = await CloneAsync(t, "alice");
        var sha = await CommitAsync(dir, "d.txt", "d", Email("alice"), key);
        Assert.True((await PushAsync(dir, "feature")).Ok);
        var result = await VerifyAsync(t, sha);
        Assert.Equal(CommitSignatureStatus.Unverified, result.Status);
        Assert.Contains("deploy key", result.Reason);
    }

    // ---------- Require signed commits ----------

    private async Task RequireSignedAsync(Setup t, string pattern = "main")
    {
        await using var s = app.Scope(app.UserId("alice"));
        await s.Get<BranchProtectionService>().SaveAsync(t.Repo.Id, new BranchProtectionRule { Pattern = pattern, RequireSignedCommits = true, BlockForcePushes = false, BlockDeletion = false }, app.UserId("alice"));
    }

    [SshKeygenFact]
    public async Task Pushes_to_a_branch_that_requires_signed_commits_are_checked_on_the_server()
    {
        var t = await SetupAsync();
        await RequireSignedAsync(t);
        var bobKey = await NewKeyAsync("bob");
        await RegisterAsync("bob", bobKey);
        var stranger = await NewKeyAsync("stranger");
        var dir = await CloneAsync(t, "bob");

        // Unsigned: refused with a clear message, and nothing moved.
        await CommitAsync(dir, "1.txt", "1", Email("bob"), null, "unsigned");
        var before = GitOps.ResolveSha(t.Path, "main");
        var unsigned = await git.RunAsync(dir, "push", "origin", "HEAD:main");
        Assert.False(unsigned.Ok);
        Assert.Contains("is not verified", unsigned.Output);
        Assert.Contains("it isn't signed", unsigned.Output);
        Assert.Equal(before, GitOps.ResolveSha(t.Path, "main"));

        // Signed with a key nobody registered, or with bob's key but another committer email: refused.
        await git.OkAsync(dir, "reset", "--hard", "origin/main");
        await CommitAsync(dir, "2.txt", "2", Email("bob"), stranger, "strange key");
        var strange = await git.RunAsync(dir, "push", "origin", "HEAD:main");
        Assert.False(strange.Ok);
        Assert.Contains("is not verified", strange.Output);
        await git.OkAsync(dir, "reset", "--hard", "origin/main");
        await CommitAsync(dir, "3.txt", "3", "mallory@example.com", bobKey, "wrong email");
        var wrongEmail = await git.RunAsync(dir, "push", "origin", "HEAD:main");
        Assert.False(wrongEmail.Ok);
        Assert.Contains("committer email", wrongEmail.Output);

        // Signed by a registered key with the matching email: accepted, including a series of commits and a new branch off main.
        await git.OkAsync(dir, "reset", "--hard", "origin/main");
        await CommitAsync(dir, "4.txt", "4", Email("bob"), bobKey, "signed one");
        var tip = await CommitAsync(dir, "5.txt", "5", Email("bob"), bobKey, "signed two");
        var ok = await git.RunAsync(dir, "push", "origin", "HEAD:main");
        Assert.True(ok.Ok, ok.Output);
        Assert.Equal(tip, GitOps.ResolveSha(t.Path, "main"));

        // One unsigned commit hidden in a series is enough to refuse the push.
        await CommitAsync(dir, "6.txt", "6", Email("bob"), bobKey, "signed three");
        await CommitAsync(dir, "7.txt", "7", Email("bob"), null, "sneaky");
        await CommitAsync(dir, "8.txt", "8", Email("bob"), bobKey, "signed four");
        var series = await git.RunAsync(dir, "push", "origin", "HEAD:main");
        Assert.False(series.Ok);
        Assert.Equal(tip, GitOps.ResolveSha(t.Path, "main"));

        // Branches without the rule are unaffected.
        Assert.True((await git.RunAsync(dir, "push", "origin", "HEAD:refs/heads/scratch")).Ok);
    }

    [SshKeygenFact]
    public async Task An_admin_bypass_rule_lets_the_owner_push_unsigned_commits()
    {
        var t = await SetupAsync();
        await using (var s = app.Scope(app.UserId("alice")))
            await s.Get<BranchProtectionService>().SaveAsync(t.Repo.Id, new BranchProtectionRule { Pattern = "main", RequireSignedCommits = true, AllowAdminBypass = true, BlockForcePushes = false }, app.UserId("alice"));
        var dir = await CloneAsync(t, "alice");
        await CommitAsync(dir, "x.txt", "x", Email("alice"), null);
        Assert.True((await git.RunAsync(dir, "push", "origin", "HEAD:main")).Ok);

        var bob = await CloneAsync(t, "bob");
        await CommitAsync(bob, "y.txt", "y", Email("bob"), null);
        Assert.False((await git.RunAsync(bob, "push", "origin", "HEAD:main")).Ok);
    }

    [SshKeygenFact]
    public async Task A_pull_request_with_unverified_commits_cannot_be_merged_until_every_commit_is_verified()
    {
        var t = await SetupAsync();
        await RequireSignedAsync(t);
        var bobKey = await NewKeyAsync("bob");
        await RegisterAsync("bob", bobKey);
        var dir = await CloneAsync(t, "bob");
        await git.OkAsync(dir, "checkout", "-b", "feat");
        await CommitAsync(dir, "p.txt", "p", Email("bob"), null, "unsigned work");
        Assert.True((await PushAsync(dir, "feat")).Ok);
        PullRequest pr;
        await using (var s = app.Scope(app.UserId("bob")))
            pr = await s.Get<PullRequestService>().CreateAsync(t.Repo, "Work", null, "feat", "main", app.UserId("bob"));

        await using (var s = app.Scope(app.UserId("carol")))
        {
            var gate = await s.Get<MergeGate>().EvaluateAsync(pr.Id, app.UserId("carol"));
            Assert.True(gate.Blocked);
            Assert.Contains(gate.Checks, c => c.Name == "Signed commits" && c.State == MergeCheckState.Failed);
            Assert.Contains(gate.Blockers, b => b.Contains("verified"));
            var ex = await Assert.ThrowsAsync<BranchProtectedException>(() => s.Get<PullRequestService>().MergeAsync(pr.Id, app.User("carol"), new MergeRequest()));
            Assert.Contains("verified", ex.Message);
        }

        // Replace the unsigned commit by a signed one (force-push to the unprotected feature branch).
        await git.OkAsync(dir, "reset", "--hard", "origin/main");
        await CommitAsync(dir, "p.txt", "p", Email("bob"), bobKey, "signed work");
        Assert.True((await git.RunAsync(dir, "push", "--force", "origin", "HEAD:refs/heads/feat")).Ok);
        await using (var s = app.Scope(app.UserId("carol")))
        {
            var gate = await s.Get<MergeGate>().EvaluateAsync(pr.Id, app.UserId("carol"));
            Assert.False(gate.Blocked, string.Join("; ", gate.Blockers));
            Assert.Contains(gate.Checks, c => c.Name == "Signed commits" && c.State == MergeCheckState.Passed);
            Assert.True((await s.Get<PullRequestService>().MergeAsync(pr.Id, app.User("carol"), new MergeRequest(MergeStrategy.Squash))).Success);
        }
    }

    [Fact]
    public async Task The_rule_is_saved_audited_and_shown_in_the_effective_protection()
    {
        var t = await SetupAsync();
        await RequireSignedAsync(t, "release/*");
        await using var s = app.Scope(app.UserId("alice"));
        var rules = await s.Get<BranchProtectionService>().ListAsync(t.Repo.Id);
        Assert.True(rules.Single().RequireSignedCommits);
        Assert.True((await s.Get<BranchProtectionService>().ForBranchAsync(t.Repo.Id, "release/1.0")).RequireSignedCommits);
        Assert.False((await s.Get<BranchProtectionService>().ForBranchAsync(t.Repo.Id, "main")).RequireSignedCommits);
        await using var db = app.Db();
        Assert.Contains("requiresignedcommits", (await db.AuditEvents.AsNoTracking().Where(e => e.Action == "branch_protection.created" && e.ProjectId == t.Project.Id).Select(e => e.Details).FirstAsync())!.ToLowerInvariant());
    }

    // ---------- GPG keys ----------

    [Fact]
    public async Task Uploading_a_private_key_or_junk_is_refused_before_gpg_is_even_consulted()
    {
        await using var s = app.Scope(app.UserId("alice"));
        var keys = s.Get<GpgKeyService>();
        var priv = await Assert.ThrowsAsync<InvalidOperationException>(() => keys.AddAsync(app.UserId("alice"), "x", "-----BEGIN PGP PRIVATE KEY BLOCK-----\nabc\n-----END PGP PRIVATE KEY BLOCK-----"));
        Assert.Contains("PRIVATE", priv.Message);
        await Assert.ThrowsAsync<InvalidOperationException>(() => keys.AddAsync(app.UserId("alice"), "x", "not a key"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => keys.AddAsync(app.UserId("alice"), "x", ""));
        await Assert.ThrowsAsync<InvalidOperationException>(() => keys.AddAsync(app.UserId("alice"), "x", "-----BEGIN PGP PUBLIC KEY BLOCK-----\n" + new string('A', 70_000) + "\n-----END PGP PUBLIC KEY BLOCK-----"));
    }

    [GpgFact]
    public async Task A_gpg_signed_commit_is_verified_and_stops_when_the_key_is_revoked()
    {
        var t = await SetupAsync();
        var home = git.Dir("gnupg");
        Directory.CreateDirectory(home);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(home, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        async Task<string> GpgAsync(string? stdin, params string[] args)
        {
            var psi = new ProcessStartInfo("gpg") { RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, UseShellExecute = false };
            foreach (var a in new[] { "--homedir", home, "--batch", "--no-tty", "--pinentry-mode", "loopback", "--passphrase", "" }.Concat(args)) psi.ArgumentList.Add(a);
            psi.Environment["GNUPGHOME"] = home;
            using var p = Process.Start(psi)!;
            if (stdin != null) await p.StandardInput.WriteAsync(stdin);
            p.StandardInput.Close();
            var output = await p.StandardOutput.ReadToEndAsync() + await p.StandardError.ReadToEndAsync();
            await p.WaitForExitAsync();
            Assert.True(p.ExitCode == 0, output);
            return output;
        }
        await GpgAsync(null, "--quick-generate-key", "Alice <alice@example.com>", "ed25519", "sign", "1y");
        var armored = await GpgAsync(null, "--armor", "--export", "alice@example.com");
        var fpr = (await GpgAsync(null, "--with-colons", "--list-keys", "alice@example.com")).Split('\n').First(l => l.StartsWith("fpr:")).Split(':')[9];

        GpgKey key;
        await using (var s = app.Scope(app.UserId("alice"))) key = await s.Get<GpgKeyService>().AddAsync(app.UserId("alice"), "laptop", armored);
        Assert.Equal(fpr, key.Fingerprint);
        // The same key can't be registered twice, and a key without the account's email is refused.
        await using (var s = app.Scope(app.UserId("bob")))
            await Assert.ThrowsAsync<InvalidOperationException>(() => s.Get<GpgKeyService>().AddAsync(app.UserId("bob"), "stolen", armored));

        var dir = await CloneAsync(t, "alice");
        await File.WriteAllTextAsync(Path.Combine(dir, "g.txt"), "g");
        await git.OkAsync(dir, "add", "g.txt");
        var psi = new ProcessStartInfo("git") { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in new[] { "-c", "user.name=Alice", "-c", "user.email=alice@example.com", "-c", "gpg.program=gpg", "-c", "user.signingkey=" + fpr, "commit", "-S", "-m", "gpg signed" }) psi.ArgumentList.Add(a);
        psi.Environment["GNUPGHOME"] = home;
        psi.Environment["HOME"] = git.Home;
        using (var p = Process.Start(psi)!) { await p.WaitForExitAsync(); Assert.Equal(0, p.ExitCode); }
        var sha = (await git.OkAsync(dir, "rev-parse", "HEAD")).Trim();
        Assert.True((await PushAsync(dir, "gpg-branch")).Ok);

        Assert.Equal(CommitSignatureStatus.Verified, (await VerifyAsync(t, sha)).Status);
        await using (var s = app.Scope(app.UserId("alice"))) Assert.True(await s.Get<GpgKeyService>().RevokeAsync(app.UserId("alice"), key.Id));
        var revoked = await VerifyAsync(t, sha);
        Assert.Equal(CommitSignatureStatus.Unverified, revoked.Status);
        Assert.Contains("revoked", revoked.Reason);
    }
}

/// <summary>Without gpg / ssh-keygen a signed commit is "unverifiable", not "unverified"; a rule that needs signatures refuses pushes with an explanation.</summary>
public class SigningWithoutToolsTests(NoSigningToolsApp app) : IClassFixture<NoSigningToolsApp>, IDisposable
{
    private readonly GitCli git = new();
    public void Dispose() => git.Dispose();

    [SshKeygenFact]
    public async Task A_signed_commit_is_unverifiable_and_pushes_to_a_signed_only_branch_explain_why()
    {
        var project = await app.CreateProjectAsync("alice", ProjectVisibility.Private, ("bob", ProjectRole.Developer));
        var repo = await app.CreateRepoAsync(project.Id, "alice");

        // A signed commit made with the real tools in the test, pushed to an unprotected branch.
        var keyDir = git.Dir("keys");
        Directory.CreateDirectory(keyDir);
        var key = Path.Combine(keyDir, "k");
        var psi = new ProcessStartInfo("ssh-keygen") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in new[] { "-q", "-t", "ed25519", "-N", "", "-f", key }) psi.ArgumentList.Add(a);
        using (var p = Process.Start(psi)!) await p.WaitForExitAsync();
        var dir = git.Dir("clone");
        await git.OkAsync(null, "clone", app.GitUrl(project.Key, repo.Slug, "bob", SeedData.DemoPassword), dir);
        await File.WriteAllTextAsync(Path.Combine(dir, "f.txt"), "f");
        await git.OkAsync(dir, "add", "f.txt");
        await git.OkAsync(dir, "-c", "gpg.format=ssh", "-c", "user.signingkey=" + key, "-c", "user.email=bob@example.com", "commit", "-S", "-m", "signed");
        var sha = (await git.OkAsync(dir, "rev-parse", "HEAD")).Trim();
        Assert.True((await git.RunAsync(dir, "push", "origin", "HEAD:refs/heads/feature")).Ok);

        await using (var s = app.Scope())
        {
            var result = await s.Get<CommitSignatureService>().GetAsync(repo, sha);
            Assert.Equal(CommitSignatureStatus.Unverifiable, result.Status);
            Assert.Contains("ssh-keygen isn't installed", result.Reason);
            Assert.StartsWith("Unverifiable", result.Tooltip);
            Assert.False(s.Get<CommitSignatureService>().SshAvailable);
            Assert.False(s.Get<CommitSignatureService>().GpgAvailable);
        }

        await using (var s = app.Scope(app.UserId("alice")))
            await s.Get<BranchProtectionService>().SaveAsync(repo.Id, new BranchProtectionRule { Pattern = "main", RequireSignedCommits = true, BlockForcePushes = false }, app.UserId("alice"));
        var push = await git.RunAsync(dir, "push", "origin", "HEAD:main");
        Assert.False(push.Ok);
        Assert.Contains("can't verify signatures", push.Output);
    }
}
