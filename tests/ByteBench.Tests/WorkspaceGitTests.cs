using System.Net;
using System.Net.Http.Headers;
using System.Text;
using ByteBench.Data;
using ByteBench.Services;
using ByteBench.Services.Workspaces;
using ByteBench.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ByteBench.Tests;

/// <summary>The workspace's git credential against the real git endpoint (real git client, real Kestrel).</summary>
public class WorkspaceGitTests(WorkspaceApp app) : IClassFixture<WorkspaceApp>, IDisposable
{
    private readonly GitCli git = new();

    public void Dispose() => git.Dispose();

    private async Task<(string User, Workspace Ws, string Token, Repository Repo)> StartAsync(ProjectRole role)
    {
        var user = await app.NewUserAsync();
        var repo = await app.RepoWithMembersAsync((user, role));
        // Each start writes its own credential line; take the newest.
        var ws = await app.StartAsync(user, repo);
        var line = app.Backend.Execs.Where(e => e.ContainerId == ws.ContainerId && e.Spec.Stdin?.Contains("gbw_") == true).Select(e => e.Spec.Stdin!.Trim()).Single();
        var afterScheme = line[(line.IndexOf("://", StringComparison.Ordinal) + 3)..];
        var token = afterScheme[(afterScheme.IndexOf(':') + 1)..afterScheme.IndexOf('@')];
        return (user, ws, token, repo);
    }

    private string Url(Repository repo, string user, string token) => app.GitUrl(app.ProjectKey(repo.ProjectId), repo.Slug, user, token);

    [Fact]
    public async Task A_workspace_token_clones_its_repository_and_a_developer_can_push_a_branch()
    {
        var (user, _, token, repo) = await StartAsync(ProjectRole.Developer);
        var dir = git.Dir("dev-clone");
        await git.OkAsync(null, "clone", Url(repo, user, token), dir);
        await File.WriteAllTextAsync(Path.Combine(dir, "from-workspace.txt"), "hi");
        await git.OkAsync(dir, "add", "-A");
        await git.OkAsync(dir, "commit", "-m", "from a workspace");
        await git.OkAsync(dir, "push", "origin", "HEAD:refs/heads/ws-feature");
    }

    [Fact]
    public async Task A_read_only_workspace_can_clone_but_not_push()
    {
        var (user, ws, token, repo) = await StartAsync(ProjectRole.Viewer);
        Assert.False(ws.CanPush);
        var dir = git.Dir("viewer-clone");
        await git.OkAsync(null, "clone", Url(repo, user, token), dir);
        await File.WriteAllTextAsync(Path.Combine(dir, "x.txt"), "x");
        await git.OkAsync(dir, "add", "-A");
        await git.OkAsync(dir, "commit", "-m", "nope");
        var push = await git.RunAsync(dir, "push", "origin", "HEAD:refs/heads/viewer-branch");
        Assert.False(push.Ok, push.Output);
    }

    [Fact]
    public async Task A_role_lost_after_the_start_stops_pushing_immediately()
    {
        var (user, ws, token, repo) = await StartAsync(ProjectRole.Developer);
        Assert.True(ws.CanPush);
        var dir = git.Dir("demoted");
        await git.OkAsync(null, "clone", Url(repo, user, token), dir);
        await using (var s = app.Scope(app.UserId("alice")))
            await s.Get<MemberService>().RemoveMemberAsync(repo.ProjectId, app.UserId(user));
        await File.WriteAllTextAsync(Path.Combine(dir, "late.txt"), "x");
        await git.OkAsync(dir, "add", "-A");
        await git.OkAsync(dir, "commit", "-m", "late");
        Assert.False((await git.RunAsync(dir, "push", "origin", "HEAD:refs/heads/late")).Ok);
        Assert.False((await git.RunAsync(null, "clone", Url(repo, user, token), git.Dir("late-clone"))).Ok); // no access at all any more
    }

    [Fact]
    public async Task Branch_protection_still_applies_to_a_workspace_with_write_access()
    {
        var (user, _, token, repo) = await StartAsync(ProjectRole.Developer);
        await using (var s = app.Scope(app.UserId("alice")))
            await s.Get<BranchProtectionService>().SaveAsync(repo.Id, new BranchProtectionRule { Pattern = "main", BlockDirectPushes = true });
        var dir = git.Dir("protected");
        await git.OkAsync(null, "clone", Url(repo, user, token), dir);
        await File.WriteAllTextAsync(Path.Combine(dir, "p.txt"), "x");
        await git.OkAsync(dir, "add", "-A");
        await git.OkAsync(dir, "commit", "-m", "to main");
        var main = await git.RunAsync(dir, "push", "origin", "HEAD:refs/heads/main");
        Assert.False(main.Ok, main.Output);
        await git.OkAsync(dir, "push", "origin", "HEAD:refs/heads/side-branch"); // a new branch is fine
    }

    [Fact]
    public async Task The_token_is_limited_to_its_one_repository()
    {
        var (user, _, token, repo) = await StartAsync(ProjectRole.Developer);
        // A second repository the same user can access.
        var second = await app.CreateRepoAsync(repo.ProjectId, "alice", "second");
        var r = await git.RunAsync(null, "clone", Url(second, user, token), git.Dir("wrong-repo"));
        Assert.False(r.Ok, r.Output);
        await git.OkAsync(null, "clone", Url(repo, user, token), git.Dir("right-repo"));
    }

    [Fact]
    public async Task The_token_is_not_accepted_for_another_user_or_for_the_api()
    {
        var (user, _, token, repo) = await StartAsync(ProjectRole.Developer);
        var other = await app.NewUserAsync();
        await using (var s = app.Scope(app.UserId("alice"))) await s.Get<MemberService>().AddMemberAsync(repo.ProjectId, other, ProjectRole.Developer);
        Assert.False((await git.RunAsync(null, "clone", Url(repo, other, token), git.Dir("other-user"))).Ok);

        using var http = app.Http();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync("/api/v1/user")).StatusCode);
        var mcp = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
        mcp.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        Assert.Contains((int)(await http.SendAsync(mcp)).StatusCode, new[] { 401, 403, 404 });
        Assert.NotNull(user);
    }

    [Fact]
    public async Task The_token_is_revoked_when_the_workspace_stops_fails_or_the_server_restarts()
    {
        var (user, ws, token, repo) = await StartAsync(ProjectRole.Developer);
        await git.OkAsync(null, "clone", Url(repo, user, token), git.Dir("before-stop"));
        await using (var s = app.Scope(app.UserId(user))) await s.Get<WorkspaceService>().StopAsync(ws.PublicId, app.UserId(user));
        Assert.False((await git.RunAsync(null, "clone", Url(repo, user, token), git.Dir("after-stop"))).Ok);

        var (user2, ws2, token2, repo2) = await StartAsync(ProjectRole.Developer);
        await app.Services.GetRequiredService<WorkspaceReaper>().CleanAtStartupAsync(CancellationToken.None); // "restart"
        Assert.Equal(WorkspaceState.Stopped, (await app.ReloadAsync(ws2.PublicId)).State);
        Assert.False((await git.RunAsync(null, "clone", Url(repo2, user2, token2), git.Dir("after-restart"))).Ok);
    }
}
