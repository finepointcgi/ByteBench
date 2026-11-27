using System.Net;
using System.Text;
using System.Text.Json;
using ByteBench.Data;
using ByteBench.Services;
using ByteBench.Services.Git;
using ByteBench.Services.Workspaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ByteBench.Tests;

/// <summary>Ports for the desktop apps, the git bar (commit, push, open a pull request) and starting workspaces from issues and pull requests. All on the fake Docker backend.</summary>
public class WorkspaceExtrasTests(WorkspaceApp app) : IClassFixture<WorkspaceApp>
{
    private static StringContent Json(object? o = null) => new(JsonSerializer.Serialize(o ?? new { }), Encoding.UTF8, "application/json");

    private async Task<HttpClient> ClientAsync(string user)
    {
        var c = await app.ApiClientAsync(user);
        c.BaseAddress = app.ServerAddress;
        return c;
    }

    private static string StatusOutput(string branch, int changed, int ahead = 0, bool remote = true, string subject = "Add things")
    {
        var sb = new StringBuilder($"## {branch}\n");
        for (var i = 0; i < changed; i++) sb.Append($" M src/file{i}.cs\n");
        sb.Append($"@@BB\nbranch={branch}\nremote={(remote ? 1 : 0)}\n");
        if (remote) sb.Append($"ahead={ahead}\nbehind=0\n");
        sb.Append($"subject={subject}\n");
        return sb.ToString();
    }

    /// <summary>Scripts the container's git answers: status output and the current branch.</summary>
    private void ScriptGit(string branch, int changed, int ahead = 0, bool remote = true)
    {
        app.Backend.ExecScripts.Add((s => s.Command.Any(a => a.Contains("@@BB")), _ => new ExecResult(0, StatusOutput(branch, changed, ahead, remote))));
        app.Backend.ExecScripts.Add((s => s.Command.SequenceEqual(["git", "symbolic-ref", "--short", "-q", "HEAD"]), _ => new ExecResult(0, branch + "\n")));
    }

    private List<string> Pushes(Workspace ws) =>
        app.Backend.Execs.Where(e => e.ContainerId == ws.ContainerId && e.Spec.Command.Take(2).SequenceEqual(["git", "push"])).Select(e => string.Join(' ', e.Spec.Command)).ToList();

    // ---------------------------------------------------------------- ports

    private const string ProcNetTcp = """
          sl  local_address rem_address   st tx_queue rx_queue tr tm->when retrnsmt   uid  timeout inode
           0: 00000000:0BB8 00000000:0000 0A 00000000:00000000 00:00000000 00000000  1000        0 11111 1 0000000000000000 100 0 0 10 0
           1: 0100007F:1435 00000000:0000 0A 00000000:00000000 00:00000000 00000000  1000        0 22222 1 0000000000000000 100 0 0 10 0
           2: 0100007F:1F90 00000000:0000 0A 00000000:00000000 00:00000000 00000000  1000        0 33333 1 0000000000000000 100 0 0 10 0
           3: 0100007F:9C40 0100007F:1435 01 00000000:00000000 00:00000000 00000000  1000        0 44444 1 0000000000000000 100 0 0 10 0
        """;

    [Fact]
    public async Task The_ports_endpoint_lists_forwarded_ports_with_their_own_origin_and_opening_one_gives_a_single_use_ticket_url()
    {
        var user = await app.NewUserAsync();
        var repo = await app.RepoWithMembersAsync((user, ProjectRole.Developer));
        var ws = await app.StartAsync(user, repo);
        app.Backend.ExecScripts.Add((s => s.Command.Any(a => a.Contains("/proc/net/tcp")), _ => new ExecResult(0, ProcNetTcp)));
        try
        {
            using var client = await ClientAsync(user);
            var ports = JsonDocument.Parse(await client.GetStringAsync($"/api/v1/workspaces/{ws.PublicId}/ports")).RootElement.EnumerateArray().ToList();
            Assert.Equal([5173, 8080], ports.Select(p => p.GetProperty("port").GetInt32()).ToArray()); // not the IDE's 3000, not the ephemeral 40000
            Assert.Equal($"http://p5173-{ws.PublicId}.localhost:{app.ServerAddress.Port}", ports[0].GetProperty("origin").GetString());

            var opened = await client.PostAsync($"/api/v1/workspaces/{ws.PublicId}/ports/5173/open", Json());
            Assert.Equal(HttpStatusCode.OK, opened.StatusCode);
            var body = JsonDocument.Parse(await opened.Content.ReadAsStringAsync()).RootElement;
            var url = new Uri(body.GetProperty("url").GetString()!);
            Assert.Equal($"p5173-{ws.PublicId}.localhost", url.Host);
            Assert.Equal("/_bb/enter", url.AbsolutePath);
            Assert.Equal(60, body.GetProperty("expiresInSeconds").GetInt32());

            // The ticket is real: the port's origin redeems it for a session cookie, exactly once.
            using var http = app.Http();
            async Task<HttpResponseMessage> Redeem()
            {
                var req = new HttpRequestMessage(HttpMethod.Get, url.PathAndQuery);
                req.Headers.Host = url.Authority;
                return await http.SendAsync(req);
            }
            var first = await Redeem();
            Assert.Equal(HttpStatusCode.Redirect, first.StatusCode);
            Assert.Contains(first.Headers.GetValues("Set-Cookie"), c => c.StartsWith("bb_ws="));
            Assert.Equal(HttpStatusCode.Unauthorized, (await Redeem()).StatusCode);

            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync($"/api/v1/workspaces/{ws.PublicId}/ports/70000/open", Json())).StatusCode);

            await using (var s = app.Scope(app.UserId(user))) await s.Get<WorkspaceService>().StopAsync(ws.PublicId, app.UserId(user));
            Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.GetAsync($"/api/v1/workspaces/{ws.PublicId}/ports")).StatusCode);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.PostAsync($"/api/v1/workspaces/{ws.PublicId}/ports/5173/open", Json())).StatusCode);
        }
        finally { app.Backend.ExecScripts.Clear(); }
    }

    [Fact]
    public async Task Ports_are_private_to_the_workspaces_owner()
    {
        var user = await app.NewUserAsync();
        var stranger = await app.NewUserAsync();
        var repo = await app.RepoWithMembersAsync((user, ProjectRole.Developer), (stranger, ProjectRole.Developer));
        var ws = await app.StartAsync(user, repo);
        using var client = await ClientAsync(stranger);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/v1/workspaces/{ws.PublicId}/ports")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync($"/api/v1/workspaces/{ws.PublicId}/ports/5173/open", Json())).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/v1/workspaces/{ws.PublicId}/git")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/v1/workspaces/{ws.PublicId}/links")).StatusCode);
    }

    [Fact]
    public async Task Server_info_advertises_workspace_ports_for_the_desktop_apps()
    {
        using var http = app.Http();
        var info = JsonDocument.Parse(await http.GetStringAsync("/api/v1/server-info")).RootElement;
        Assert.Contains(info.GetProperty("capabilities").EnumerateArray(), c => c.GetString() == "workspace-ports");
    }

    // ---------------------------------------------------------------- git bar

    [Fact]
    public void Git_status_output_is_parsed_into_branch_changes_and_sync_state()
    {
        var s = WorkspaceGitService.Parse(StatusOutput("feature-x", 2, ahead: 3), canPush: true, "WEB-4", "Fix it");
        Assert.Equal("feature-x", s.Branch);
        Assert.False(s.Detached);
        Assert.Equal(2, s.ChangedFiles);
        Assert.Equal(["src/file0.cs", "src/file1.cs"], s.Changes.Select(c => c.Path).ToArray());
        Assert.True(s.RemoteBranchExists);
        Assert.Equal(3, s.Ahead);
        Assert.Equal("Add things", s.LastSubject);
        Assert.Equal("WEB-4", s.IssueKey);

        var detached = WorkspaceGitService.Parse("## HEAD (no branch)\n?? x.txt\n@@BB\nbranch=\nremote=0\nsubject=\n", false);
        Assert.True(detached.Detached);
        Assert.Null(detached.Branch);
        Assert.Equal(1, detached.ChangedFiles);
        Assert.Equal("??", detached.Changes[0].Status);
        Assert.False(detached.RemoteBranchExists);

        var many = WorkspaceGitService.Parse("## a\n" + string.Concat(Enumerable.Range(0, 500).Select(i => $" M f{i}\n")) + "@@BB\nbranch=a\nremote=0\n", true);
        Assert.Equal(500, many.ChangedFiles);
        Assert.Equal(WorkspaceGitService.MaxListedChanges, many.Changes.Count);
        Assert.True(many.ChangesTruncated);
        Assert.Equal(0, WorkspaceGitService.Parse("garbage", true).ChangedFiles);
    }

    [Fact]
    public async Task The_git_status_endpoint_shows_branch_and_changed_files()
    {
        var user = await app.NewUserAsync();
        var repo = await app.RepoWithMembersAsync((user, ProjectRole.Developer));
        var ws = await app.StartAsync(user, repo);
        ScriptGit("feature-x", 2, ahead: 1);
        try
        {
            using var client = await ClientAsync(user);
            var s = JsonDocument.Parse(await client.GetStringAsync($"/api/v1/workspaces/{ws.PublicId}/git")).RootElement;
            Assert.Equal("feature-x", s.GetProperty("branch").GetString());
            Assert.Equal(2, s.GetProperty("changedFiles").GetInt32());
            Assert.Equal(1, s.GetProperty("ahead").GetInt32());
            Assert.True(s.GetProperty("canPush").GetBoolean());
        }
        finally { app.Backend.ExecScripts.Clear(); }
    }

    [Fact]
    public async Task Commit_and_push_through_the_api_use_the_workspace_token_and_respect_read_only_workspaces()
    {
        var dev = await app.NewUserAsync();
        var viewer = await app.NewUserAsync();
        var repo = await app.RepoWithMembersAsync((dev, ProjectRole.Developer), (viewer, ProjectRole.Viewer));
        var devWs = await app.StartAsync(dev, repo);
        var viewerWs = await app.StartAsync(viewer, repo);
        ScriptGit("feature-x", 1);
        try
        {
            using var client = await ClientAsync(dev);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.PostAsync($"/api/v1/workspaces/{devWs.PublicId}/git/commit", Json(new { message = "" }))).StatusCode);
            var ok = await client.PostAsync($"/api/v1/workspaces/{devWs.PublicId}/git/commit", Json(new { message = "Fix the thing", push = true, branch = "feature-x" }));
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
            Assert.Contains("git push origin HEAD:refs/heads/feature-x", Pushes(devWs));
            var commit = app.Backend.Execs.Last(e => e.ContainerId == devWs.ContainerId && e.Spec.Command.Contains("sh") && e.Spec.Command.Any(a => a.Contains("git commit")));
            Assert.Contains("Fix the thing", commit.Spec.Command); // the message is an argument, never spliced into the script
            Assert.DoesNotContain("Fix the thing", commit.Spec.Command[2]);

            var push = await client.PostAsync($"/api/v1/workspaces/{devWs.PublicId}/git/push", Json());
            Assert.Equal(HttpStatusCode.OK, push.StatusCode);
            Assert.Equal(2, Pushes(devWs).Count);

            // A failing push (branch protection, say) is reported with the server's words and no token.
            app.Backend.ExecScripts.Insert(0, (s => s.Command.Take(2).SequenceEqual(["git", "push"]),
                _ => new ExecResult(1, "remote: error: branch is protected https://u:gbw_secretsecret@host/x.git")));
            var refused = await client.PostAsync($"/api/v1/workspaces/{devWs.PublicId}/git/push", Json());
            Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
            var text = await refused.Content.ReadAsStringAsync();
            Assert.Contains("protected", text);
            Assert.DoesNotContain("gbw_secretsecret", text);
            app.Backend.ExecScripts.RemoveAt(0);

            // Read-only workspaces commit locally at most.
            using var viewerClient = await ClientAsync(viewer);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, (await viewerClient.PostAsync($"/api/v1/workspaces/{viewerWs.PublicId}/git/push", Json())).StatusCode);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, (await viewerClient.PostAsync($"/api/v1/workspaces/{viewerWs.PublicId}/git/commit", Json(new { message = "x", push = true }))).StatusCode);
            Assert.Empty(Pushes(viewerWs));
        }
        finally { app.Backend.ExecScripts.Clear(); }
    }

    [Fact]
    public async Task Open_pr_pushes_the_branch_and_creates_a_pull_request_once()
    {
        var user = await app.NewUserAsync();
        var repo = await app.RepoWithMembersAsync((user, ProjectRole.Developer));
        app.Commit(repo, "feature-x", "x.txt", "hello");
        var ws = await app.StartAsync(user, repo, "feature-x");
        ScriptGit("feature-x", 0, ahead: 1);
        try
        {
            using var client = await ClientAsync(user);
            var created = await client.PostAsync($"/api/v1/workspaces/{ws.PublicId}/git/pull-request", Json(new { title = "Add x", description = "Adds x." }));
            Assert.Equal(HttpStatusCode.OK, created.StatusCode);
            var pr = JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement;
            Assert.False(pr.GetProperty("alreadyExisted").GetBoolean());
            Assert.Equal("feature-x", pr.GetProperty("sourceBranch").GetString());
            Assert.Equal("main", pr.GetProperty("targetBranch").GetString());
            Assert.StartsWith($"/p/{app.ProjectKey(repo.ProjectId)}/repos/{repo.Slug}/pull-requests/", pr.GetProperty("url").GetString());
            Assert.Contains("git push origin HEAD:refs/heads/feature-x", Pushes(ws)); // pushed first
            await using (var db = app.Db())
            {
                var row = await db.PullRequests.AsNoTracking().SingleAsync(p => p.RepositoryId == repo.Id && p.SourceBranch == "feature-x");
                Assert.Equal("Add x", row.Title);
                Assert.Equal(pr.GetProperty("number").GetInt32(), row.Number);
                Assert.True(await db.WorkspaceLinks.AnyAsync(l => l.WorkspaceId == ws.Id && l.PullRequestId == row.Id));
                Assert.Contains(await db.AuditEvents.Where(a => a.Action == "workspace.pull_request_opened").ToListAsync(), a => a.TargetId == ws.PublicId);
            }

            var again = await client.PostAsync($"/api/v1/workspaces/{ws.PublicId}/git/pull-request", Json());
            var second = JsonDocument.Parse(await again.Content.ReadAsStringAsync()).RootElement;
            Assert.True(second.GetProperty("alreadyExisted").GetBoolean());
            Assert.Equal(pr.GetProperty("number").GetInt32(), second.GetProperty("number").GetInt32());
        }
        finally { app.Backend.ExecScripts.Clear(); }
    }

    [Fact]
    public async Task Open_pr_refuses_uncommitted_changes_the_target_branch_detached_heads_and_read_only_workspaces()
    {
        var user = await app.NewUserAsync();
        var viewer = await app.NewUserAsync();
        var repo = await app.RepoWithMembersAsync((user, ProjectRole.Developer), (viewer, ProjectRole.Viewer));
        app.Commit(repo, "feature-y", "y.txt", "y");
        var ws = await app.StartAsync(user, repo, "feature-y");
        var viewerWs = await app.StartAsync(viewer, repo, "feature-y");
        using var client = await ClientAsync(user);
        try
        {
            ScriptGit("feature-y", 3);
            var dirty = await client.PostAsync($"/api/v1/workspaces/{ws.PublicId}/git/pull-request", Json());
            Assert.Equal(HttpStatusCode.UnprocessableEntity, dirty.StatusCode);
            Assert.Contains("uncommitted", await dirty.Content.ReadAsStringAsync());
            Assert.Empty(Pushes(ws)); // nothing was pushed for a refused request

            app.Backend.ExecScripts.Clear();
            ScriptGit("main", 0);
            var onMain = await client.PostAsync($"/api/v1/workspaces/{ws.PublicId}/git/pull-request", Json());
            Assert.Equal(HttpStatusCode.UnprocessableEntity, onMain.StatusCode);

            app.Backend.ExecScripts.Clear();
            app.Backend.ExecScripts.Add((s => s.Command.SequenceEqual(["git", "symbolic-ref", "--short", "-q", "HEAD"]), _ => new ExecResult(1, "")));
            var detached = await client.PostAsync($"/api/v1/workspaces/{ws.PublicId}/git/pull-request", Json());
            Assert.Equal(HttpStatusCode.UnprocessableEntity, detached.StatusCode);
            Assert.Contains("isn't on a branch", await detached.Content.ReadAsStringAsync());

            app.Backend.ExecScripts.Clear();
            ScriptGit("feature-y", 0);
            using var viewerClient = await ClientAsync(viewer);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, (await viewerClient.PostAsync($"/api/v1/workspaces/{viewerWs.PublicId}/git/pull-request", Json())).StatusCode);
            await using var db = app.Db();
            Assert.False(await db.PullRequests.AnyAsync(p => p.RepositoryId == repo.Id));
        }
        finally { app.Backend.ExecScripts.Clear(); }
    }

    // ---------------------------------------------------------------- start from an issue or a pull request

    [Theory]
    [InlineData("KEY", 12, "Fix login", "KEY-12-fix-login")]
    [InlineData("key", 3, "  Crash on   startup!!  ", "KEY-3-crash-on-startup")]
    [InlineData("WEB", 7, "Ünïcode café — naïve résumé", "WEB-7-unicode-cafe-naive-resume")]
    [InlineData("WEB", 8, "!!!", "WEB-8")]
    [InlineData("WEB", 9, "../../etc/passwd; rm -rf /", "WEB-9-etc-passwd-rm-rf")]
    [InlineData("WEB", 10, "A very long title that goes on and on and on forever and ever amen", "WEB-10-a-very-long-title-that-goes-on-and-on")]
    public void Branch_names_come_from_the_issue_key_and_title(string key, int number, string title, string expected)
    {
        var name = WorkspaceLaunchService.BranchNameFor(key, number, title);
        Assert.Equal(expected, name);
        Assert.True(WorkspaceLifecycle.IsSafeRef(name));
        Assert.True(GitOps.IsValidBranchName(name));
    }

    [Fact]
    public async Task Start_from_an_issue_creates_the_branch_starts_a_workspace_on_it_links_it_and_resumes_instead_of_duplicating()
    {
        var user = await app.NewUserAsync();
        var repo = await app.RepoWithMembersAsync((user, ProjectRole.Developer));
        var key = app.ProjectKey(repo.ProjectId);
        var issue = await app.CreateIssueAsync(repo.ProjectId, user, "Fix the Login bug!");
        var path = app.Services.GetRequiredService<RepoStorage>().PathFor(key, repo.Slug);
        var branch = $"{key}-{issue.Number}-fix-the-login-bug";
        Assert.False(GitOps.BranchExists(path, branch));

        LaunchResult first;
        await using (var s = app.Scope(app.UserId(user)))
            first = await s.Get<WorkspaceLaunchService>().StartForIssueAsync(key, issue.Number, null, app.UserId(user));
        await app.WhenIdleAsync();
        Assert.Equal(branch, first.Branch);
        Assert.True(first.BranchCreated);
        Assert.False(first.Reused);
        Assert.True(GitOps.BranchExists(path, branch));
        Assert.Equal(GitOps.ResolveSha(path, "main"), GitOps.ResolveSha(path, branch)); // created from the default branch
        var ws = await app.ReloadAsync(first.Workspace.PublicId);
        Assert.Equal(WorkspaceState.Running, ws.State);
        Assert.Equal(branch, ws.Ref);
        Assert.Equal($"issue:{key}-{issue.Number}", ws.Source);
        await using (var db = app.Db()) Assert.True(await db.WorkspaceLinks.AnyAsync(l => l.WorkspaceId == ws.Id && l.IssueId == issue.Id));

        // Starting again resumes the live one: the same workspace, no new branch.
        await using (var s = app.Scope(app.UserId(user)))
        {
            var again = await s.Get<WorkspaceLaunchService>().StartForIssueAsync(key, issue.Number, null, app.UserId(user));
            Assert.True(again.Reused);
            Assert.False(again.BranchCreated);
            Assert.Equal(ws.PublicId, again.Workspace.PublicId);
            var linked = await s.Get<WorkspaceLaunchService>().ListForIssueAsync(issue.Id, app.UserId(user));
            Assert.Equal([ws.PublicId], linked.Select(w => w.Workspace.PublicId).ToArray());
            var links = await s.Get<WorkspaceLaunchService>().GetLinksAsync(ws.PublicId, app.UserId(user));
            Assert.Equal($"{key}-{issue.Number}", links!.IssueKey);
            Assert.Equal($"/p/{key}/issues/{issue.Number}", links.IssueUrl);
        }
        await using var db2 = app.Db();
        Assert.Equal(1, await db2.Workspaces.CountAsync(w => w.RepositoryId == repo.Id && w.Ref == branch));
    }

    [Fact]
    public async Task Start_from_an_issue_needs_to_know_the_repository_when_there_are_several_and_push_rights_to_create_the_branch()
    {
        var dev = await app.NewUserAsync();
        var viewer = await app.NewUserAsync();
        var repo = await app.RepoWithMembersAsync((dev, ProjectRole.Developer), (viewer, ProjectRole.Viewer));
        var second = await app.CreateRepoAsync(repo.ProjectId, "alice", "second");
        var key = app.ProjectKey(repo.ProjectId);
        var issue = await app.CreateIssueAsync(repo.ProjectId, dev, "Two repos");

        await using (var s = app.Scope(app.UserId(dev)))
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => s.Get<WorkspaceLaunchService>().StartForIssueAsync(key, issue.Number, null, app.UserId(dev)));
            Assert.Contains("several repositories", ex.Message);
            var picked = await s.Get<WorkspaceLaunchService>().StartForIssueAsync(key, issue.Number, second.Slug, app.UserId(dev));
            await app.WhenIdleAsync();
            Assert.Equal(second.Id, picked.Workspace.RepositoryId);
        }

        // A viewer can't create the branch; once someone with push rights has, the viewer can open it (read-only).
        await using (var s = app.Scope(app.UserId(viewer)))
        {
            await Assert.ThrowsAsync<ForbiddenException>(() => s.Get<WorkspaceLaunchService>().StartForIssueAsync(key, issue.Number, repo.Slug, app.UserId(viewer)));
            var path = app.Services.GetRequiredService<RepoStorage>().PathFor(key, repo.Slug);
            Assert.False(GitOps.BranchExists(path, WorkspaceLaunchService.BranchNameFor(key, issue.Number, issue.Title)));
        }
        await using (var s = app.Scope(app.UserId(dev)))
            await s.Get<WorkspaceLaunchService>().StartForIssueAsync(key, issue.Number, repo.Slug, app.UserId(dev));
        await app.WhenIdleAsync();
        await using (var s = app.Scope(app.UserId(viewer)))
        {
            var r = await s.Get<WorkspaceLaunchService>().StartForIssueAsync(key, issue.Number, repo.Slug, app.UserId(viewer));
            await app.WhenIdleAsync();
            Assert.False(r.BranchCreated);
            Assert.False((await app.ReloadAsync(r.Workspace.PublicId)).CanPush);
        }
    }

    [Fact]
    public async Task The_issue_and_pull_request_launch_endpoints_start_workspaces_and_hide_what_you_cannot_see()
    {
        var dev = await app.NewUserAsync();
        var stranger = await app.NewUserAsync();
        var repo = await app.RepoWithMembersAsync((dev, ProjectRole.Developer));
        var key = app.ProjectKey(repo.ProjectId);
        var issue = await app.CreateIssueAsync(repo.ProjectId, dev, "From the API");
        app.Commit(repo, "feature-pr", "p.txt", "p");

        using var client = await ClientAsync(dev);
        var started = await client.PostAsync($"/api/v1/projects/{key}/issues/{issue.Number}/workspace", Json());
        Assert.Equal(HttpStatusCode.Accepted, started.StatusCode);
        var body = JsonDocument.Parse(await started.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal($"{key}-{issue.Number}-from-the-api", body.GetProperty("branch").GetString());
        Assert.True(body.GetProperty("branchCreated").GetBoolean());
        Assert.Equal($"issue:{key}-{issue.Number}", body.GetProperty("workspace").GetProperty("source").GetString());
        var id = body.GetProperty("workspace").GetProperty("id").GetString()!;
        await app.WhenIdleAsync();
        var links = JsonDocument.Parse(await client.GetStringAsync($"/api/v1/workspaces/{id}/links")).RootElement;
        Assert.Equal($"{key}-{issue.Number}", links.GetProperty("issueKey").GetString());
        await using var linkDb = app.Db();
        Assert.True(await linkDb.WorkspaceLinks.AnyAsync(l => l.Issue!.Number == issue.Number));

        // A pull request: its source branch.
        PullRequest pr;
        await using (var s = app.Scope(app.UserId(dev))) pr = await s.Get<PullRequestService>().CreateAsync((await s.Get<RepositoryService>().GetRepositoryAsync(key, repo.Slug))!, "PR", null, "feature-pr", "main", app.UserId(dev));
        var viaPr = await client.PostAsync($"/api/v1/projects/{key}/repos/{repo.Slug}/pull-requests/{pr.Number}/workspace", Json());
        Assert.Equal(HttpStatusCode.Accepted, viaPr.StatusCode);
        var prBody = JsonDocument.Parse(await viaPr.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("feature-pr", prBody.GetProperty("branch").GetString());
        Assert.False(prBody.GetProperty("branchCreated").GetBoolean());
        Assert.Equal($"pr:{pr.Number}", prBody.GetProperty("workspace").GetProperty("source").GetString());
        await app.WhenIdleAsync();
        var prLinks = JsonDocument.Parse(await client.GetStringAsync($"/api/v1/workspaces/{prBody.GetProperty("workspace").GetProperty("id").GetString()}/links")).RootElement;
        Assert.Equal(pr.Number, prLinks.GetProperty("pullRequestNumber").GetInt32());

        // Someone who can't see the private project learns nothing, and nothing starts.
        using var other = await ClientAsync(stranger);
        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsync($"/api/v1/projects/{key}/issues/{issue.Number}/workspace", Json())).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsync($"/api/v1/projects/{key}/repos/{repo.Slug}/pull-requests/{pr.Number}/workspace", Json())).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync($"/api/v1/projects/{key}/issues/9999/workspace", Json())).StatusCode);
    }

    [Fact]
    public async Task A_workspace_started_for_an_issue_opens_a_pull_request_that_names_the_issue()
    {
        var user = await app.NewUserAsync();
        var repo = await app.RepoWithMembersAsync((user, ProjectRole.Developer));
        var key = app.ProjectKey(repo.ProjectId);
        var issue = await app.CreateIssueAsync(repo.ProjectId, user, "Add dark mode");
        LaunchResult launch;
        await using (var s = app.Scope(app.UserId(user)))
            launch = await s.Get<WorkspaceLaunchService>().StartForIssueAsync(key, issue.Number, null, app.UserId(user));
        await app.WhenIdleAsync();
        app.Commit(repo, launch.Branch, "dark.css", "body{}", from: launch.Branch); // the developer's commit, pushed from the workspace
        ScriptGit(launch.Branch, 0, ahead: 1);
        try
        {
            using var client = await ClientAsync(user);
            var status = JsonDocument.Parse(await client.GetStringAsync($"/api/v1/workspaces/{launch.Workspace.PublicId}/git")).RootElement;
            Assert.Equal($"{key}-{issue.Number}", status.GetProperty("issueKey").GetString());

            var res = await client.PostAsync($"/api/v1/workspaces/{launch.Workspace.PublicId}/git/pull-request", Json());
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            await using var db = app.Db();
            var pr = await db.PullRequests.AsNoTracking().SingleAsync(p => p.RepositoryId == repo.Id && p.SourceBranch == launch.Branch);
            Assert.Equal($"{key}-{issue.Number}: Add dark mode", pr.Title);
            Assert.Contains($"Fixes {key}-{issue.Number}", pr.Description);
            var links = await db.WorkspaceLinks.AsNoTracking().Where(l => l.WorkspaceId == launch.Workspace.Id).ToListAsync();
            Assert.Single(links); // the one row now carries both the issue and the pull request
            Assert.Equal(issue.Id, links[0].IssueId);
            Assert.Equal(pr.Id, links[0].PullRequestId);

            // The PR page's linked-issue detection finds it (branch name, title and description all carry the key).
            await using var s2 = app.Scope(app.UserId(user));
            var full = await s2.Get<PullRequestService>().GetAsync(key, repo.Slug, pr.Number);
            Assert.Contains(await s2.Get<PullRequestService>().GetLinkedIssuesAsync(full!), i => i.Id == issue.Id);
        }
        finally { app.Backend.ExecScripts.Clear(); }
    }
}
