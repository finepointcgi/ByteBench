using System.Text;
using System.Text.Json;
using ByteBench.Data;
using ByteBench.Services;
using ByteBench.Services.Editor.Ai;
using ByteBench.Services.Workspaces;
using ByteBench.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ByteBench.Tests;

public class WorkspaceReaperTests(WorkspaceApp app) : IClassFixture<WorkspaceApp>
{
    private WorkspaceReaper Reaper => app.Services.GetRequiredService<WorkspaceReaper>();

    private async Task<Workspace> RunningAsync()
    {
        var user = await app.NewUserAsync();
        var repo = await app.RepoWithMembersAsync((user, ProjectRole.Developer));
        return await app.StartAsync(user, repo);
    }

    private async Task AgeAsync(string publicId, Action<Workspace> change)
    {
        await using var db = app.Db();
        var w = await db.Workspaces.FirstAsync(x => x.PublicId == publicId);
        change(w);
        await db.SaveChangesAsync();
        app.Runtime.Find(publicId); // (activity lives in memory; a stale in-memory value must not keep it alive)
    }

    [Fact]
    public async Task Idle_workspaces_are_stopped_but_active_ones_are_not()
    {
        var idle = await RunningAsync();
        var busy = await RunningAsync();
        var old = DateTime.UtcNow.AddMinutes(-90);
        await AgeAsync(idle.PublicId, w => w.LastActivityAt = old);
        await AgeAsync(busy.PublicId, w => w.LastActivityAt = old);
        app.Runtime.Find(idle.PublicId)!.SetActivity(old);
        app.Runtime.Find(busy.PublicId)!.SetActivity(old);
        app.Runtime.Find(busy.PublicId)!.Touch(); // a proxied request just now
        await Reaper.SweepAsync(CancellationToken.None);
        var i = await app.ReloadAsync(idle.PublicId);
        Assert.Equal(WorkspaceState.Stopped, i.State);
        Assert.Equal("idle timeout", i.StopReason);
        Assert.Null(i.GitTokenHash);
        Assert.Contains(idle.ContainerId!, app.Backend.Removed);
        Assert.Equal(WorkspaceState.Running, (await app.ReloadAsync(busy.PublicId)).State);
    }

    [Fact]
    public async Task Workspaces_past_their_maximum_lifetime_are_stopped_even_if_active()
    {
        var ws = await RunningAsync();
        app.Runtime.Find(ws.PublicId)!.Touch();
        await AgeAsync(ws.PublicId, w => w.ExpiresAt = DateTime.UtcNow.AddMinutes(-1));
        await Reaper.SweepAsync(CancellationToken.None);
        var after = await app.ReloadAsync(ws.PublicId);
        Assert.Equal(WorkspaceState.Stopped, after.State);
        Assert.Equal("maximum lifetime reached", after.StopReason);
    }

    [Fact]
    public async Task Orphan_containers_are_removed_and_vanished_ones_are_noticed()
    {
        var kept = await RunningAsync();
        var gone = await RunningAsync();
        app.Backend.Extra.Add(new ManagedContainer("orphan-container-id", 999_999));
        app.Backend.Vanish(gone.ContainerId!);
        await Reaper.SweepAsync(CancellationToken.None);
        Assert.Contains("orphan-container-id", app.Backend.Removed);
        Assert.DoesNotContain(kept.ContainerId!, app.Backend.Removed);
        Assert.Equal(WorkspaceState.Running, (await app.ReloadAsync(kept.PublicId)).State);
        var g = await app.ReloadAsync(gone.PublicId);
        Assert.Equal(WorkspaceState.Stopped, g.State);
        Assert.Equal("container disappeared", g.StopReason);
        app.Backend.Extra.Clear();
    }

    [Fact]
    public async Task After_a_restart_every_container_is_removed_and_every_live_row_closed()
    {
        var a = await RunningAsync();
        var b = await RunningAsync();
        await Reaper.CleanAtStartupAsync(CancellationToken.None);
        foreach (var ws in new[] { a, b })
        {
            var after = await app.ReloadAsync(ws.PublicId);
            Assert.Equal(WorkspaceState.Stopped, after.State);
            Assert.Equal("server restarted", after.StopReason);
            Assert.Null(after.GitTokenHash);
            Assert.Contains(ws.ContainerId!, app.Backend.Removed);
        }
    }

    [Fact]
    public async Task A_start_that_hangs_is_closed_by_the_sweep()
    {
        var user = await app.NewUserAsync();
        var repo = await app.RepoWithMembersAsync((user, ProjectRole.Developer));
        await using var db = app.Db();
        var stuck = new Workspace
        {
            PublicId = WorkspaceTokens.NewPublicId(), OwnerId = app.UserId(user), RepositoryId = repo.Id, ProjectId = repo.ProjectId, Ref = "main",
            State = WorkspaceState.Starting, Image = "x", CreatedAt = DateTime.UtcNow.AddHours(-1), ExpiresAt = DateTime.UtcNow.AddHours(8),
            GitTokenHash = WorkspaceTokens.Hash("gbw_x"), ConnectionTokenHash = WorkspaceTokens.Hash("c"),
        };
        db.Workspaces.Add(stuck);
        await db.SaveChangesAsync();
        await Reaper.SweepAsync(CancellationToken.None);
        var after = await app.ReloadAsync(stuck.PublicId);
        Assert.Equal(WorkspaceState.Stopped, after.State);
        Assert.Null(after.GitTokenHash);
    }
}

public class WorkspaceAiToolTests
{
    private static (WorkspaceAiTools Tools, FakeWorkspaceBackend Backend) Make(WorkspaceAiOptions? options = null)
    {
        var backend = new FakeWorkspaceBackend();
        return (new WorkspaceAiTools(backend, "cid", "code", options ?? new WorkspaceAiOptions()), backend);
    }

    private static JsonElement J(object o) => JsonSerializer.SerializeToElement(o);

    [Theory]
    [InlineData("../etc/passwd")]
    [InlineData("a/../../etc/passwd")]
    [InlineData("/etc/passwd")]
    [InlineData("\\windows\\system32")]
    [InlineData("~/.ssh/id_rsa")]
    [InlineData("C:/x")]
    [InlineData(".git/config")]
    [InlineData("src/.GIT/hooks/pre-commit")]
    [InlineData("a\0b")]
    [InlineData("a//b")]
    [InlineData("")]
    [InlineData("./x")]
    public async Task Paths_that_escape_the_checkout_or_touch_git_are_refused_before_anything_runs(string path)
    {
        var (tools, backend) = Make();
        foreach (var tool in new[] { "read_file", "write_file", "edit_file" })
        {
            var (content, isError) = await tools.ExecuteAsync(tool, J(new { path, content = "x", find = "a", replace = "b" }), CancellationToken.None);
            Assert.True(isError, $"{tool} {path}: {content}");
        }
        if (path.Length > 0) Assert.True((await tools.ExecuteAsync("list_files", J(new { path_prefix = path }), CancellationToken.None)).IsError);
        Assert.Empty(backend.Execs); // validation happens in ByteBench, not in the container
    }

    [Fact]
    public async Task File_tools_run_as_the_unprivileged_assistant_user_with_the_path_as_an_argument_not_in_the_script()
    {
        var (tools, backend) = Make();
        backend.ExecScripts.Add((s => s.Command.Contains("sh") && s.Stdin == null, _ => new ExecResult(0, "1\thello")));
        var (read, err) = await tools.ExecuteAsync("read_file", J(new { path = "src/a b;rm -rf x.txt" }), CancellationToken.None);
        Assert.False(err, read);
        Assert.Contains("hello", read);
        Assert.Contains("UNTRUSTED", read); // file content is delimited as data
        var call = backend.Execs.Single();
        Assert.Equal("bbai", call.Spec.User);
        Assert.Equal("/workspace/code", call.Spec.WorkDir);
        Assert.Contains("src/a b;rm -rf x.txt", call.Spec.Command); // passed as a positional argument
        Assert.DoesNotContain(call.Spec.Command.Where(c => c.Contains("realpath")), c => c.Contains("rm -rf"));
        Assert.Equal("/home/bbai", call.Spec.Env!["HOME"]);

        var (w, werr) = await tools.ExecuteAsync("write_file", J(new { path = "out/new.txt", content = "SECRET-CONTENT" }), CancellationToken.None);
        Assert.False(werr, w);
        var write = backend.Execs.Last();
        Assert.Equal("SECRET-CONTENT", write.Spec.Stdin); // content goes through stdin only
        Assert.DoesNotContain(write.Spec.Command, c => c.Contains("SECRET-CONTENT"));
        Assert.Equal("bbai", write.Spec.User);
    }

    [Fact]
    public async Task The_container_scripts_check_symlinks_and_git_after_resolving_the_path()
    {
        var (tools, backend) = Make();
        await tools.ExecuteAsync("write_file", J(new { path = "x.txt", content = "c" }), CancellationToken.None);
        var script = backend.Execs.Single().Spec.Command.First(c => c.Contains("realpath"));
        Assert.Contains("realpath -m", script);
        Assert.Contains("Path escapes the workspace", script);
        Assert.Contains(".git", script);
    }

    [Fact]
    public async Task Writes_over_the_size_limit_are_refused()
    {
        var (tools, backend) = Make(new WorkspaceAiOptions { MaxFileWriteBytes = 100 });
        var (msg, err) = await tools.ExecuteAsync("write_file", J(new { path = "big.txt", content = new string('x', 101) }), CancellationToken.None);
        Assert.True(err);
        Assert.Contains("limit", msg);
        Assert.Empty(backend.Execs);
    }

    [Fact]
    public async Task Command_output_is_capped_timeouts_are_clamped_and_results_are_delimited()
    {
        var (tools, backend) = Make(new WorkspaceAiOptions { MaxOutputBytes = 1000, CommandTimeoutSeconds = 20 });
        backend.ExecScripts.Add((_ => true, _ => new ExecResult(0, new string('y', 1000), Truncated: true)));
        var (content, err) = await tools.ExecuteAsync("run_command", J(new { command = "npm test", timeout_seconds = 100000 }), CancellationToken.None);
        Assert.False(err);
        Assert.Contains("Output truncated at 1,000 bytes", content.Replace(" ", " "));
        Assert.Contains("UNTRUSTED COMMAND OUTPUT", content);
        var spec = backend.Execs.Single().Spec;
        Assert.Equal(1000, spec.MaxOutputBytes);
        Assert.Equal(TimeSpan.FromSeconds(20), spec.Timeout); // never longer than the configured maximum
        Assert.Equal("bbai", spec.User);

        backend.ExecScripts.Clear();
        backend.ExecScripts.Add((_ => true, _ => new ExecResult(124, "partial", TimedOut: true)));
        var (timedOut, terr) = await tools.ExecuteAsync("run_command", J(new { command = "sleep 999" }), CancellationToken.None);
        Assert.True(terr);
        Assert.Contains("Timed out after 20 s", timedOut);
    }

    [Theory]
    [InlineData("git commit -am x")]
    [InlineData("git push origin main")]
    [InlineData("cd src && git push")]
    [InlineData("echo hi; /usr/bin/git commit -m x")]
    [InlineData("git -c user.name=a commit -m x")]
    [InlineData("GIT_TERMINAL_PROMPT=0 git fetch")]
    [InlineData("git remote add x http://evil")]
    [InlineData("git config --global credential.helper store")]
    [InlineData("(git push)")]
    [InlineData("git  merge main")]
    public async Task The_assistant_cannot_commit_or_push_commands_that_try_are_refused_without_running(string command)
    {
        var (tools, backend) = Make();
        Assert.True(WorkspaceAiTools.IsForbiddenGitCommand(command), command);
        var (msg, err) = await tools.ExecuteAsync("run_command", J(new { command }), CancellationToken.None);
        Assert.True(err);
        Assert.Contains("not available to you", msg);
        Assert.Empty(backend.Execs);
    }

    [Theory]
    [InlineData("git status")]
    [InlineData("git diff HEAD")]
    [InlineData("git log --oneline -5")]
    [InlineData("npm run commit-lint")]
    [InlineData("dotnet test")]
    public void Ordinary_commands_are_allowed(string command) => Assert.False(WorkspaceAiTools.IsForbiddenGitCommand(command), command);

    [Fact]
    public async Task Tool_input_errors_come_back_as_tool_errors_not_exceptions()
    {
        var (tools, _) = Make();
        Assert.True((await tools.ExecuteAsync("read_file", J(new { }), CancellationToken.None)).IsError);
        Assert.True((await tools.ExecuteAsync("read_file", J(new { path = 5 }), CancellationToken.None)).IsError);
        Assert.True((await tools.ExecuteAsync("run_command", J(new { command = new string('x', 5000) }), CancellationToken.None)).IsError);
        Assert.True((await tools.ExecuteAsync("nope", J(new { }), CancellationToken.None)).IsError);
        Assert.True((await tools.ExecuteAsync("read_file", JsonSerializer.SerializeToElement("not an object"), CancellationToken.None)).IsError);
        Assert.True((await tools.ExecuteAsync("search_code", J(new { query = new string('q', 201) }), CancellationToken.None)).IsError);
    }
}

public class WorkspaceAiServiceTests(WorkspaceApp app) : IClassFixture<WorkspaceApp>
{
    private async Task<(string User, Workspace Ws, Repository Repo)> RunningAsync()
    {
        var user = await app.NewUserAsync();
        var repo = await app.RepoWithMembersAsync((user, ProjectRole.Developer));
        return (user, await app.StartAsync(user, repo), repo);
    }

    private async Task<WorkspaceAiResult> AskAsync(string user, string publicId, string message = "list the files")
    {
        await using var s = app.Scope(app.UserId(user));
        return await s.Get<WorkspaceAiService>().RunAsync(publicId, app.UserId(user), message, [], NullEditorAiSink.Instance, CancellationToken.None);
    }

    [Fact]
    public async Task The_loop_runs_tools_in_the_container_records_usage_and_audits_a_summary_without_contents()
    {
        var (user, ws, repo) = await RunningAsync();
        app.Backend.ExecScripts.Add((s => s.Command.Contains("git") && s.Command.Contains("ls-files"), _ => new ExecResult(0, "README.md\nsrc/a.txt")));
        app.Backend.ExecScripts.Add((s => s.Command.Any(c => c.Contains("npm test")), _ => new ExecResult(0, "all green")));
        app.Model.Reset((turn, _) => turn switch
        {
            0 => FakeEditorChatModel.Tool("list_files", new { }, "a"),
            1 => FakeEditorChatModel.Tool("run_command", new { command = "npm test" }, "b"),
            _ => FakeEditorChatModel.Text("Done: tests pass."),
        });
        var result = await AskAsync(user, ws.PublicId, "run the tests");
        Assert.Null(result.Error);
        Assert.Equal(2, result.ToolCalls);
        Assert.Equal("Done: tests pass.", result.Text);
        Assert.Contains("README.md", app.Model.Results[0][0].Content);
        Assert.Contains("all green", app.Model.Results[1][0].Content);
        Assert.All(app.Backend.Execs.Where(e => e.ContainerId == ws.ContainerId && e.Spec.User == "bbai"), e => Assert.Equal("/workspace/code", e.Spec.WorkDir));
        // No git credential or token ever reached the model's context.
        Assert.DoesNotContain("gbw_", string.Join('\n', app.Model.Setups.SelectMany(x => new[] { x.System, x.UserMessage })));

        await using var db = app.Db();
        var audit = (await db.AuditEvents.Where(a => a.Action == "workspace.ai_tools" && a.TargetId == ws.PublicId).ToListAsync()).Single();
        Assert.Contains("run_command", audit.Details);
        Assert.Contains("npm", audit.Details);
        Assert.DoesNotContain("all green", audit.Details);
        Assert.NotEmpty(await db.AiUsages.Where(u => u.Feature == "workspace.chat").ToListAsync());
    }

    [Fact]
    public async Task The_tool_call_limit_stops_a_runaway_loop()
    {
        var (user, ws, _) = await RunningAsync();
        app.Model.Reset((_, _) => FakeEditorChatModel.Tool("list_files", new { }));
        var result = await AskAsync(user, ws.PublicId);
        Assert.NotNull(result.StopNote);
        Assert.Contains("tool calls", result.StopNote);
        Assert.True(result.ToolCalls <= new WorkspaceAiOptions().MaxToolCalls);
    }

    [Fact]
    public async Task Only_the_owner_can_use_the_assistant_on_a_workspace()
    {
        var (_, ws, repo) = await RunningAsync();
        var other = await app.NewUserAsync();
        await using (var s = app.Scope(app.UserId("alice"))) await s.Get<MemberService>().AddMemberAsync(repo.ProjectId, other, ProjectRole.Developer);
        var before = app.Model.Turns;
        await Assert.ThrowsAsync<InvalidOperationException>(() => AskAsync(other, ws.PublicId));
        Assert.Equal(before, app.Model.Turns);
    }

    [Fact]
    public async Task The_project_switch_the_site_switch_and_the_workspaces_switch_each_turn_the_assistant_off()
    {
        var (user, ws, repo) = await RunningAsync();
        var admin = app.UserId("alice");
        var before = app.Model.Turns;

        await using (var s = app.Scope(admin)) await s.Get<WorkspaceService>().SetProjectSettingsAsync(repo.ProjectId, true, false, admin);
        var r = await AskAsync(user, ws.PublicId);
        Assert.Contains("project", r.Error);
        await using (var s = app.Scope(admin)) await s.Get<WorkspaceService>().SetProjectSettingsAsync(repo.ProjectId, true, true, admin);

        await using (var s = app.Scope(admin)) await s.Get<WorkspaceService>().SetAiEnabledSiteAsync(false, admin);
        r = await AskAsync(user, ws.PublicId);
        Assert.Contains("site administrator", r.Error);
        await using (var s = app.Scope(admin)) await s.Get<WorkspaceService>().SetAiEnabledSiteAsync(true, admin);

        await using (var s = app.Scope(admin)) await s.Get<WorkspaceService>().SetEnabledAsync(false, admin);
        r = await AskAsync(user, ws.PublicId);
        Assert.Contains("turned off", r.Error);
        await using (var s = app.Scope(admin)) await s.Get<WorkspaceService>().SetEnabledAsync(true, admin);

        Assert.Equal(before, app.Model.Turns); // the model was never called
    }

    [Fact]
    public async Task The_per_user_rate_limit_applies_to_workspace_requests()
    {
        var limited = new WorkspaceLimitedApp();
        try
        {
            var user = await limited.NewUserAsync();
            var repo = await limited.RepoWithMembersAsync((user, ProjectRole.Developer));
            var ws = await limited.StartAsync(user, repo);
            limited.Model.Reset((_, _) => FakeEditorChatModel.Text("ok"));
            WorkspaceAiResult? last = null;
            for (var i = 0; i < 3; i++)
            {
                await using var s = limited.Scope(limited.UserId(user));
                last = await s.Get<WorkspaceAiService>().RunAsync(ws.PublicId, limited.UserId(user), "hi", [], NullEditorAiSink.Instance, CancellationToken.None);
            }
            Assert.Contains("too quickly", last!.Error);
            Assert.Equal(2, limited.Model.Turns);
        }
        finally { limited.Dispose(); }
    }
}

public class WorkspaceLimitedApp : WorkspaceApp
{
    protected override void AddSettings(IDictionary<string, string?> settings)
    {
        base.AddSettings(settings);
        settings["Workspaces:Ai:RequestsPerMinute"] = "2";
    }
}
