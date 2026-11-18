using GitBench.Data;
using GitBench.Services;
using GitBench.Tests.Infrastructure;

namespace GitBench.Tests;

/// <summary>Real <c>git</c> against the app on a Kestrel port: Smart HTTP, credentials, permissions and branch protection.</summary>
public class GitHttpTests(KestrelApp app) : IClassFixture<KestrelApp>, IDisposable
{
    private readonly GitCli git = new();
    private const string Password = SeedData.DemoPassword;

    public void Dispose() => git.Dispose();

    private async Task<(Project Project, Repository Repo)> SetupAsync()
    {
        var project = await app.CreateProjectAsync("carol", ProjectVisibility.Private,
            ("bob", ProjectRole.Developer), ("dan", ProjectRole.Maintainer));
        var repo = await app.CreateRepoAsync(project.Id, "carol");
        return (project, repo);
    }

    private async Task<string> CloneAsync(Project p, Repository r, string user, string secret, string? slug = null)
    {
        var dir = git.Dir("clone-" + Guid.NewGuid().ToString("N")[..8]);
        await git.OkAsync(null, "clone", app.GitUrl(p.Key, slug ?? r.Slug, user, secret), dir);
        return dir;
    }

    private async Task CommitAsync(string dir, string file, string content)
    {
        await File.WriteAllTextAsync(Path.Combine(dir, file), content);
        await git.OkAsync(dir, "add", file);
        await git.OkAsync(dir, "commit", "-m", "edit " + file);
    }

    [Fact]
    public async Task Clone_and_push_with_password()
    {
        var (p, r) = await SetupAsync();
        var dir = await CloneAsync(p, r, "bob", Password);
        Assert.True(File.Exists(Path.Combine(dir, "README.md")));

        await CommitAsync(dir, "a.txt", "a");
        await git.OkAsync(dir, "push", "origin", "main");
        await using var s = app.Scope(app.UserId("bob"));
        var branches = await s.Get<RepositoryService>().Git(r, path => GitBench.Services.Git.GitOps.ListBranches(path, "main"));
        Assert.Contains(branches, b => b.Name == "main");
        var log = await s.Get<RepositoryService>().Git(r, path => GitBench.Services.Git.GitOps.Log(path, "main", null, 0, 5));
        Assert.Equal("edit a.txt", log.Items[0].MessageShort);
    }

    [Fact]
    public async Task Wrong_password_and_non_members_are_refused()
    {
        var (p, r) = await SetupAsync();
        var bad = await git.RunAsync(null, "ls-remote", app.GitUrl(p.Key, r.Slug, "bob", "wrong-password"));
        Assert.False(bad.Ok);
        Assert.Contains("Authentication failed", bad.Output);

        // A user who can't view the private project gets the same answer as for a missing repository.
        await app.CreateUserAsync("gitoutsider");
        var hidden = await git.RunAsync(null, "ls-remote", app.GitUrl(p.Key, r.Slug, "gitoutsider", Password));
        var missing = await git.RunAsync(null, "ls-remote", app.GitUrl(p.Key, "no-such-repo", "gitoutsider", Password));
        Assert.False(hidden.Ok);
        Assert.Contains("not found", hidden.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(missing.Output.Replace("no-such-repo", r.Slug), hidden.Output);
    }

    [Fact]
    public async Task Clone_with_a_token_and_scopes_apply_to_push()
    {
        var (p, r) = await SetupAsync();
        var readToken = await app.CreateTokenAsync("bob", TokenScopes.ReadRepository);
        var dir = await CloneAsync(p, r, "bob", readToken);
        await CommitAsync(dir, "b.txt", "b");
        var push = await git.RunAsync(dir, "push", "origin", "main");
        Assert.False(push.Ok);
        Assert.Contains("403", push.Output);

        var writeToken = await app.CreateTokenAsync("bob", TokenScopes.WriteRepository);
        await git.OkAsync(dir, "remote", "set-url", "origin", app.GitUrl(p.Key, r.Slug, "bob", writeToken));
        await git.OkAsync(dir, "push", "origin", "main");

        // An API-only token is no good for git.
        var apiToken = await app.CreateTokenAsync("bob", TokenScopes.ReadApi | TokenScopes.WriteApi);
        Assert.False((await git.RunAsync(null, "ls-remote", app.GitUrl(p.Key, r.Slug, "bob", apiToken))).Ok);
    }

    [Fact]
    public async Task Viewer_cannot_push()
    {
        var (p, r) = await SetupAsync();
        await app.CreateUserAsync("gitviewer");
        await using (var s = app.Scope(app.UserId("carol")))
            await s.Get<MemberService>().AddMemberAsync(p.Id, "gitviewer", ProjectRole.Viewer);
        var dir = await CloneAsync(p, r, "gitviewer", Password);
        await CommitAsync(dir, "v.txt", "v");
        var push = await git.RunAsync(dir, "push", "origin", "main");
        Assert.False(push.Ok);
        Assert.Contains("403", push.Output);
    }

    [Fact]
    public async Task Push_to_a_protected_branch_is_rejected()
    {
        var (p, r) = await SetupAsync();
        await using (var s = app.Scope(app.UserId("carol")))
            await s.Get<BranchProtectionService>().SaveAsync(r.Id, new BranchProtectionRule { Pattern = "main", BlockDirectPushes = true });

        var dir = await CloneAsync(p, r, "dan", Password);
        await CommitAsync(dir, "c.txt", "c");
        var push = await git.RunAsync(dir, "push", "origin", "main");
        Assert.False(push.Ok);
        Assert.Contains("[remote rejected] main -> main", push.Output);
        Assert.Contains("changes must go through a pull request", push.Output);

        // Other branches are unaffected.
        await git.OkAsync(dir, "push", "origin", "HEAD:refs/heads/feature/c");
    }

    [Fact]
    public async Task Force_push_to_a_protected_branch_is_rejected()
    {
        var (p, r) = await SetupAsync();
        await using (var s = app.Scope(app.UserId("carol")))
            await s.Get<BranchProtectionService>().SaveAsync(r.Id, new BranchProtectionRule { Pattern = "release/*", BlockForcePushes = true, BlockDeletion = true });

        var dir = await CloneAsync(p, r, "dan", Password);
        await CommitAsync(dir, "r.txt", "one");
        await git.OkAsync(dir, "push", "origin", "HEAD:refs/heads/release/1.0");
        await CommitAsync(dir, "r.txt", "two");
        await git.OkAsync(dir, "push", "origin", "HEAD:refs/heads/release/1.0"); // fast-forward is fine

        await git.OkAsync(dir, "reset", "--hard", "HEAD~2");
        await CommitAsync(dir, "r.txt", "rewritten");
        var force = await git.RunAsync(dir, "push", "--force", "origin", "HEAD:refs/heads/release/1.0");
        Assert.False(force.Ok);
        Assert.Contains("force pushes are not allowed", force.Output);

        var delete = await git.RunAsync(dir, "push", "origin", "--delete", "release/1.0");
        Assert.False(delete.Ok);
        Assert.Contains("can't be deleted", delete.Output);

        // A force push to an unprotected branch still works.
        await git.OkAsync(dir, "push", "origin", "HEAD:refs/heads/scratch");
        await git.OkAsync(dir, "commit", "--amend", "-m", "amended");
        await git.OkAsync(dir, "push", "--force", "origin", "HEAD:refs/heads/scratch");
    }

    [Fact]
    public async Task Push_to_an_archived_repository_is_rejected_but_clone_works()
    {
        var (p, r) = await SetupAsync();
        var dir = await CloneAsync(p, r, "dan", Password);
        await using (var s = app.Scope(app.UserId("carol")))
            await s.Get<RepositorySettingsService>().SetArchivedAsync(r.Id, true);

        await CommitAsync(dir, "d.txt", "d");
        var push = await git.RunAsync(dir, "push", "origin", "main");
        Assert.False(push.Ok);
        Assert.Contains("archived", push.Output);

        await CloneAsync(p, r, "bob", Password); // still readable
    }

    [Fact]
    public async Task Old_slug_keeps_working_after_a_rename()
    {
        var (p, r) = await SetupAsync();
        var dir = await CloneAsync(p, r, "dan", Password);
        await using (var s = app.Scope(app.UserId("carol")))
            Assert.Equal("renamed-code", await s.Get<RepositorySettingsService>().RenameAsync(r.Id, "renamed-code"));

        // The existing clone (old URL) can still fetch and push.
        await git.OkAsync(dir, "fetch", "origin");
        await CommitAsync(dir, "e.txt", "e");
        await git.OkAsync(dir, "push", "origin", "main");

        // A fresh clone of the new slug sees the push; the old slug can be cloned too.
        var fresh = await CloneAsync(p, r, "bob", Password, slug: "renamed-code");
        Assert.True(File.Exists(Path.Combine(fresh, "e.txt")));
        var viaOld = await CloneAsync(p, r, "bob", Password, slug: r.Slug);
        Assert.True(File.Exists(Path.Combine(viaOld, "e.txt")));
    }
}
