using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using ByteBench.Data;
using ByteBench.Services;
using ByteBench.Services.Ai;
using ByteBench.Services.Editor.Ai;
using ByteBench.Services.Workspaces;
using ByteBench.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ByteBench.Tests;

/// <summary>The workspace app (fake Docker, scripted model) with the AI run worker's own polling switched off.</summary>
public class AiRunApp : WorkspaceApp
{
    protected override void AddSettings(IDictionary<string, string?> settings)
    {
        base.AddSettings(settings);
        settings["Workspaces:MaxPerUser"] = "10";
        settings["Workspaces:AiRuns:PollSeconds"] = "3600"; // runs start when they are requested (the queue signals the worker)
        settings["Workspaces:AiRuns:MaxConcurrent"] = "3";
        ExtraSettings(settings);
    }

    protected virtual void ExtraSettings(IDictionary<string, string?> settings) { }

    public AiRunWorker Worker => Services.GetRequiredService<AiRunWorker>();

    public async Task<(Project Project, Repository Repo, Issue Issue)> SetupAsync(bool botMember = true, ProjectRole botRole = ProjectRole.Developer, string title = "Fix the login bug")
    {
        await using (var db = Db()) await AiBot.EnsureAsync(db);
        var project = await CreateProjectAsync("alice", ProjectVisibility.Private, botMember ? [(AiBot.UserName, botRole)] : []);
        var repo = await CreateRepoAsync(project.Id, "alice", "code");
        var issue = await CreateIssueAsync(project.Id, "alice", title);
        return (project, repo, issue);
    }

    public int BotId() { using var db = Db(); return db.Users.Where(u => u.UserName == AiBot.UserName).Select(u => u.Id).First(); }

    public async Task<AiRun> RequestAsync(Issue issue, string trigger = "button")
    {
        await using var s = Scope(UserId("alice"));
        return (await s.Get<AiRunService>().RequestAsync(issue.Id, UserId("alice"), trigger)).Run;
    }

    /// <summary>Waits for the run to reach a final state (the worker runs it in the background).</summary>
    public async Task<AiRun> WaitAsync(string publicId, int seconds = 30)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (true)
        {
            await using var db = Db();
            var run = await db.AiRuns.AsNoTracking().FirstAsync(r => r.PublicId == publicId);
            if (!run.IsActive) { await Worker.WhenIdleAsync(); return run; }
            if (DateTime.UtcNow > until) throw new TimeoutException($"Run {publicId} is still {run.Status}.");
            await Task.Delay(50);
        }
    }

    public async Task WaitForStatusAsync(string publicId, AiRunStatus status, int seconds = 30)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (true)
        {
            await using var db = Db();
            if (await db.AiRuns.AnyAsync(r => r.PublicId == publicId && r.Status == status)) return;
            if (DateTime.UtcNow > until) throw new TimeoutException($"Run {publicId} never became {status}.");
            await Task.Delay(25);
        }
    }

    /// <summary>The fake container: <c>git status</c> reports changes (or not) and a push creates the branch in the bare repository, like a real push would.</summary>
    public List<string> ScriptGit(Repository repo, string status = " M src/a.txt\n", bool pushFails = false)
    {
        Backend.ExecScripts.Clear();
        var pushed = new List<string>();
        Backend.ExecScripts.Add((s => s.Command.SequenceEqual(["git", "status", "--porcelain"]), _ => new ExecResult(0, status)));
        Backend.ExecScripts.Add((s => s.Command.Count >= 4 && s.Command[0] == "git" && s.Command[1] == "push", s =>
        {
            if (pushFails) return new ExecResult(1, "remote: error: protected branch gbw_secrettoken1234 refused");
            var branch = s.Command[3]["HEAD:refs/heads/".Length..];
            Commit(repo, branch, "ai-change.txt", "changed by the AI\n");
            pushed.Add(branch);
            return new ExecResult(0, "");
        }));
        return pushed;
    }

    public async Task<List<string>> CommentsAsync(int issueId)
    {
        await using var db = Db();
        return await db.Comments.Where(c => c.IssueId == issueId && c.Author.UserName == AiBot.UserName).OrderBy(c => c.Id).Select(c => c.Body).ToListAsync();
    }

    public async Task<List<string>> EventsAsync(int runId)
    {
        await using var db = Db();
        return await db.AiRunEvents.Where(e => e.RunId == runId).OrderBy(e => e.Id).Select(e => e.Kind + ": " + e.Message).ToListAsync();
    }

    public async Task<Workspace> WorkspaceOfAsync(AiRun run)
    {
        await using var db = Db();
        return await db.Workspaces.AsNoTracking().FirstAsync(w => w.Id == run.WorkspaceId);
    }
}

public class AiRunApp1 : AiRunApp { protected override void ExtraSettings(IDictionary<string, string?> s) => s["Workspaces:AiRuns:MaxConcurrent"] = "1"; }
public class AiRunStepsApp : AiRunApp { protected override void ExtraSettings(IDictionary<string, string?> s) => s["Workspaces:AiRuns:MaxSteps"] = "2"; }
public class AiRunTokensApp : AiRunApp { protected override void ExtraSettings(IDictionary<string, string?> s) => s["Workspaces:AiRuns:MaxTokens"] = "250"; }
public class AiRunCostApp : AiRunApp { protected override void ExtraSettings(IDictionary<string, string?> s) => s["Workspaces:AiRuns:MaxCostUsd"] = "0.0005"; }
public class AiRunToolsApp : AiRunApp { protected override void ExtraSettings(IDictionary<string, string?> s) => s["Workspaces:AiRuns:MaxToolCalls"] = "2"; }
public class AiRunTimeApp : AiRunApp { protected override void ExtraSettings(IDictionary<string, string?> s) => s["Workspaces:AiRuns:MaxDurationMinutes"] = "0.02"; }
public class AiRunDailyApp : AiRunApp { protected override void ExtraSettings(IDictionary<string, string?> s) => s["Workspaces:AiRuns:MaxRunsPerProjectPerDay"] = "2"; }
public class AiRunQueueApp : AiRunApp
{
    protected override void ExtraSettings(IDictionary<string, string?> s) { s["Workspaces:AiRuns:MaxConcurrent"] = "1"; s["Workspaces:AiRuns:MaxQueued"] = "1"; }
}

public class AiRunTests(AiRunApp app) : IClassFixture<AiRunApp>
{
    private static EditorModelTurn Finish(string text) => FakeEditorChatModel.Text(text);

    /// <summary>A model that writes a file, runs the tests and then writes its summary.</summary>
    private void ScriptWorker(string summary = "Fixed the bug in `src/a.txt`; `dotnet test` passes.")
    {
        app.Model.Reset((turn, _) => turn switch
        {
            0 => FakeEditorChatModel.Tool("write_file", new { path = "src/a.txt", content = "fixed\n" }, "a"),
            1 => FakeEditorChatModel.Tool("run_command", new { command = "dotnet test" }, "b"),
            _ => Finish(summary),
        });
    }

    [Fact]
    public async Task Assigning_an_issue_to_the_ai_user_runs_it_end_to_end_and_opens_a_linked_pull_request()
    {
        var (project, repo, issue) = await app.SetupAsync();
        var pushed = app.ScriptGit(repo);
        ScriptWorker();
        var alice = app.UserId("alice");
        await using (var s = app.Scope(alice))
            await s.Get<IssueService>().UpdateIssueAsync(issue.Id, alice, (i, _) => { i.AssigneeId = app.BotId(); return Task.FromResult<string?>("assigned this to ByteBench AI"); });

        await using var db = app.Db();
        var queued = await db.AiRuns.AsNoTracking().SingleAsync(r => r.IssueId == issue.Id);
        Assert.Equal("assignment", queued.Trigger);
        var run = await app.WaitAsync(queued.PublicId);

        Assert.Equal((AiRunStatus.Succeeded, null), (run.Status, run.StopReason));
        Assert.Equal($"ai/{project.Key.ToLowerInvariant()}-{issue.Number}-fix-the-login-bug", run.Branch);
        Assert.Equal([run.Branch!], pushed);
        Assert.Equal(3, run.Steps);
        Assert.Equal(2, run.ToolCalls);
        Assert.True(run.InputTokens > 0 && run.OutputTokens > 0 && run.CostUsd > 0);
        Assert.Contains("dotnet test", run.Summary);

        // The pull request: from the AI's branch into main, by the AI user, linked to the issue.
        var pr = await db.PullRequests.AsNoTracking().SingleAsync(p => p.Id == run.PullRequestId);
        Assert.Equal((run.Branch, "main", app.BotId(), false), (pr.SourceBranch, pr.TargetBranch, pr.AuthorId, pr.IsDraft));
        Assert.Contains($"Fixes {project.Key}-{issue.Number}", pr.Description);
        Assert.Contains("dotnet test", pr.Description);

        // The comment on the issue, written by the AI user.
        var comment = Assert.Single(await app.CommentsAsync(issue.Id));
        Assert.Contains($"pull request #{pr.Number}", comment);
        Assert.Contains($"/ai-runs/{run.PublicId}", comment);

        // The workspace was the AI user's, ran the assistant's tools as bbai, committed and pushed as the workspace user, and was stopped.
        var ws = await app.WorkspaceOfAsync(run);
        Assert.Equal((app.BotId(), WorkspaceState.Stopped, "ai run finished"), (ws.OwnerId, ws.State, ws.StopReason));
        Assert.Contains(ws.ContainerId!, app.Backend.Removed);
        Assert.Null(ws.GitTokenHash);
        var execs = app.Backend.Execs.Where(e => e.ContainerId == ws.ContainerId).Select(e => e.Spec).ToList();
        Assert.Contains(execs, e => e.User == "bbai" && e.Stdin == "fixed\n");
        Assert.DoesNotContain(execs.Where(e => e.User == "bbai"), e => e.Command.Contains("push") || e.Command.Any(c => c.Contains("git commit")));
        Assert.Contains(execs, e => e.User == "coder" && e.Command.SequenceEqual(["git", "checkout", "-q", "-b", run.Branch!]));
        Assert.Contains(execs, e => e.User == "coder" && e.Command.Count >= 3 && e.Command[0] == "git" && e.Command[1] == "push");

        // Nothing secret reached the model, the log has the tools, usage and audit were recorded.
        Assert.DoesNotContain("gbw_", string.Join('\n', app.Model.Setups.SelectMany(x => new[] { x.System, x.UserMessage })));
        Assert.Contains("Fix the login bug", app.Model.Setups.Last().UserMessage);
        Assert.Contains("UNTRUSTED ISSUE", app.Model.Setups.Last().UserMessage);
        var events = await app.EventsAsync(run.Id);
        Assert.Contains("tool: Writing src/a.txt", events);
        Assert.Contains(events, e => e.StartsWith("tool: Running: dotnet test"));
        Assert.Contains($"info: Opened pull request #{pr.Number}", events);
        Assert.NotEmpty(await db.AiUsages.Where(u => u.Feature == "workspace.run" && u.UserId == alice).ToListAsync());
        var audit = await db.AuditEvents.Where(a => a.TargetId == run.PublicId).Select(a => a.Action).ToListAsync();
        Assert.Contains("ai_run.requested", audit);
        Assert.Contains("ai_run.finished", audit);
    }

    [Fact]
    public async Task The_button_and_the_rest_api_start_a_run_and_a_second_request_returns_the_one_in_flight()
    {
        var (project, repo, issue) = await app.SetupAsync(title: "Add a footer");
        app.ScriptGit(repo, status: "");
        var gate = new ManualResetEventSlim();
        app.Model.Reset((turn, _) => { gate.Wait(TimeSpan.FromSeconds(20)); return Finish("Nothing to do: the footer already exists."); });
        using var client = await app.ApiClientAsync("alice");
        client.BaseAddress = app.ServerAddress;
        var url = $"/api/v1/projects/{project.Key}/issues/{issue.Number}/ai-runs";
        var first = await client.PostAsync(url, new StringContent("{}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        var dto = JsonDocument.Parse(await first.Content.ReadAsStringAsync()).RootElement;
        var id = dto.GetProperty("id").GetString()!;
        Assert.Equal($"{project.Key}-{issue.Number}", dto.GetProperty("issue").GetString());
        await app.WaitForStatusAsync(id, AiRunStatus.Running);

        var second = await client.PostAsync(url, null);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode); // the same run, not a second one
        Assert.Equal(id, JsonDocument.Parse(await second.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetString());
        var list = JsonDocument.Parse(await client.GetStringAsync(url)).RootElement;
        Assert.Single(list.EnumerateArray());

        gate.Set();
        var run = await app.WaitAsync(id);
        Assert.Equal(AiRunStatus.Succeeded, run.Status);
        Assert.Null(run.PullRequestId); // no change, no pull request
        Assert.Contains("didn't change anything", Assert.Single(await app.CommentsAsync(issue.Id)));
        var done = JsonDocument.Parse(await client.GetStringAsync($"/api/v1/ai-runs/{id}")).RootElement;
        Assert.Equal("succeeded", done.GetProperty("status").GetString());
        var events = JsonDocument.Parse(await client.GetStringAsync($"/api/v1/ai-runs/{id}/events")).RootElement;
        Assert.Contains(events.EnumerateArray(), e => e.GetProperty("message").GetString()!.StartsWith("Queued"));
        var all = JsonDocument.Parse(await client.GetStringAsync($"/api/v1/ai-runs?project={project.Key}")).RootElement;
        Assert.Contains(all.EnumerateArray(), e => e.GetProperty("id").GetString() == id);
    }

    [Fact]
    public async Task Only_people_who_can_see_a_project_see_its_runs_and_only_editors_start_or_cancel_them()
    {
        var (project, repo, issue) = await app.SetupAsync();
        app.ScriptGit(repo, status: "");
        app.Model.Reset((_, _) => Finish("ok"));
        var run = await app.RequestAsync(issue);
        await app.WaitAsync(run.PublicId);

        var stranger = await app.NewUserAsync();
        using var other = await app.ApiClientAsync(stranger);
        other.BaseAddress = app.ServerAddress;
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/v1/ai-runs/{run.PublicId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/v1/ai-runs/{run.PublicId}/events")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsync($"/api/v1/ai-runs/{run.PublicId}/cancel", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsync($"/api/v1/projects/{project.Key}/issues/{issue.Number}/ai-runs", null)).StatusCode);
        Assert.DoesNotContain(run.PublicId, await other.GetStringAsync("/api/v1/ai-runs"));

        // A viewer reads but doesn't start.
        var viewer = await app.NewUserAsync();
        await using (var s = app.Scope(app.UserId("alice"))) await s.Get<MemberService>().AddMemberAsync(project.Id, viewer, ProjectRole.Viewer);
        using var v = await app.ApiClientAsync(viewer);
        v.BaseAddress = app.ServerAddress;
        Assert.Equal(HttpStatusCode.OK, (await v.GetAsync($"/api/v1/ai-runs/{run.PublicId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await v.PostAsync($"/api/v1/projects/{project.Key}/issues/{issue.Number}/ai-runs", null)).StatusCode);
    }

    [Fact]
    public async Task A_request_is_refused_when_the_ai_user_isnt_a_developer_in_the_project_and_an_assignment_leaves_an_audit_entry()
    {
        var (_, _, notMember) = await app.SetupAsync(botMember: false);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => app.RequestAsync(notMember));
        Assert.Contains("isn't a member", ex.Message);

        var (_, _, viewerOnly) = await app.SetupAsync(botRole: ProjectRole.Viewer);
        ex = await Assert.ThrowsAsync<InvalidOperationException>(() => app.RequestAsync(viewerOnly));
        Assert.Contains("can't push branches", ex.Message);

        // Assigning (REST can assign a bot that can see the project) never fails because the run was refused; the reason is audited.
        var alice = app.UserId("alice");
        await using (var s = app.Scope(alice))
            await s.Get<IssueService>().UpdateIssueAsync(viewerOnly.Id, alice, (i, _) => { i.AssigneeId = app.BotId(); return Task.FromResult<string?>("assigned"); });
        await using var db = app.Db();
        Assert.Empty(await db.AiRuns.Where(r => r.IssueId == viewerOnly.Id).ToListAsync());
        var refused = await db.AuditEvents.SingleAsync(a => a.Action == "ai_run.refused" && a.TargetId == viewerOnly.Id.ToString());
        Assert.Contains("can't push branches", refused.Details);
    }

    [Fact]
    public async Task The_site_switch_and_a_closed_issue_stop_a_request()
    {
        var (_, _, issue) = await app.SetupAsync();
        var admin = app.UserId("alice");
        await using (var s = app.Scope(admin)) await s.Get<AiRunService>().SetEnabledSiteAsync(false, admin);
        Assert.Contains("site administrator", (await Assert.ThrowsAsync<InvalidOperationException>(() => app.RequestAsync(issue))).Message);
        await using (var s = app.Scope(admin)) await s.Get<AiRunService>().SetEnabledSiteAsync(true, admin);

        await using (var s = app.Scope(admin)) await s.Get<IssueService>().SetStatusAsync(issue.Id, IssueStatus.Done, admin);
        Assert.Contains("closed", (await Assert.ThrowsAsync<InvalidOperationException>(() => app.RequestAsync(issue))).Message);
    }

    [Fact]
    public async Task A_model_failure_ends_the_run_failed_comments_on_the_issue_and_still_stops_the_workspace()
    {
        var (_, repo, issue) = await app.SetupAsync();
        app.ScriptGit(repo);
        app.Model.Reset((turn, _) => turn == 0 ? FakeEditorChatModel.Tool("write_file", new { path = "a.txt", content = "x\n" }) : throw new InvalidOperationException("the model service is down"));
        var run = await app.WaitAsync((await app.RequestAsync(issue)).PublicId);
        Assert.Equal((AiRunStatus.Failed, "error"), (run.Status, run.StopReason));
        Assert.Contains("the model service is down", run.Error);
        Assert.Null(run.PullRequestId);
        var comment = Assert.Single(await app.CommentsAsync(issue.Id));
        Assert.Contains("couldn't finish", comment);
        Assert.Contains("the model service is down", comment);
        var ws = await app.WorkspaceOfAsync(run);
        Assert.Equal(WorkspaceState.Stopped, ws.State);
        Assert.Contains("failed", string.Join("\n", await app.EventsAsync(run.Id)).ToLowerInvariant());
    }

    [Fact]
    public async Task A_refused_push_fails_the_run_with_the_reason_and_masks_credentials()
    {
        var (_, repo, issue) = await app.SetupAsync();
        app.ScriptGit(repo, pushFails: true);
        ScriptWorker();
        var run = await app.WaitAsync((await app.RequestAsync(issue)).PublicId);
        Assert.Equal((AiRunStatus.Failed, "push failed"), (run.Status, run.StopReason));
        Assert.Contains("protected branch", run.Error);
        Assert.DoesNotContain("gbw_secrettoken1234", run.Error);
        var comment = Assert.Single(await app.CommentsAsync(issue.Id));
        Assert.DoesNotContain("gbw_secrettoken1234", comment);
        Assert.Contains("couldn't finish", comment);
    }

    [Fact]
    public async Task Cancelling_a_queued_run_is_immediate_and_a_running_run_stops_pushes_nothing_and_stops_its_workspace()
    {
        var (_, repo, issue) = await app.SetupAsync();
        var pushed = app.ScriptGit(repo);
        string? id = null;
        app.Model.Reset((turn, _) =>
        {
            if (turn == 1) { var s = app.Scope(app.UserId("alice")); s.Get<AiRunService>().CancelAsync(id!, app.UserId("alice")).GetAwaiter().GetResult(); s.DisposeAsync().AsTask().GetAwaiter().GetResult(); } // the user presses cancel mid-run
            return FakeEditorChatModel.Tool("write_file", new { path = $"f{turn}.txt", content = "x\n" }, "t" + turn);
        });
        var run = await app.RequestAsync(issue);
        id = run.PublicId;
        var done = await app.WaitAsync(id);
        Assert.Equal((AiRunStatus.Cancelled, "cancelled"), (done.Status, done.StopReason));
        Assert.Empty(pushed);
        Assert.Null(done.PullRequestId);
        Assert.Equal(WorkspaceState.Stopped, (await app.WorkspaceOfAsync(done)).State);
        Assert.Contains("cancelled", Assert.Single(await app.CommentsAsync(issue.Id)));
        Assert.True(done.Steps <= 2);

        // A finished run can't be cancelled again.
        await using var s2 = app.Scope(app.UserId("alice"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => s2.Get<AiRunService>().CancelAsync(id, app.UserId("alice")));
    }

    [Fact]
    public async Task Restarting_the_server_fails_the_runs_that_were_in_flight_and_says_so_on_the_issue()
    {
        var (project, repo, issue) = await app.SetupAsync();
        await using (var db = app.Db())
        {
            db.AiRuns.Add(new AiRun { PublicId = WorkspaceTokens.NewPublicId(), ProjectId = project.Id, RepositoryId = repo.Id, IssueId = issue.Id, RequestedById = app.UserId("alice"), BaseBranch = "main", Status = AiRunStatus.Running, StartedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }
        await app.Services.GetRequiredService<AiRunExecutor>().RecoverAsync(CancellationToken.None);
        await using var db2 = app.Db();
        var run = await db2.AiRuns.AsNoTracking().SingleAsync(r => r.IssueId == issue.Id);
        Assert.Equal((AiRunStatus.Failed, "restart"), (run.Status, run.StopReason));
        Assert.Contains("restart", Assert.Single(await app.CommentsAsync(issue.Id)));
    }

    [Fact]
    public async Task Tool_output_and_the_log_never_carry_workspace_credentials()
    {
        var (_, repo, issue) = await app.SetupAsync();
        app.ScriptGit(repo, status: "");
        app.Backend.ExecScripts.Insert(0, (s => s.Command.Any(c => c.Contains("printenv")), _ => new ExecResult(0, "TOKEN=gbw_AbCdEfGhIjKlMnOp url=http://bytebench-ai:gbw_AbCdEfGhIjKlMnOp@host/x key=sk-ant-api03-abcdefghijkl")));
        app.Model.Reset((turn, _) => turn == 0 ? FakeEditorChatModel.Tool("run_command", new { command = "printenv; echo gbw_AbCdEfGhIjKlMnOp" }) : Finish("Token was gbw_AbCdEfGhIjKlMnOp."));
        var run = await app.WaitAsync((await app.RequestAsync(issue)).PublicId);
        var toModel = app.Model.Results[0][0].Content;
        Assert.DoesNotContain("gbw_AbCdEf", toModel);
        Assert.DoesNotContain("sk-ant-api03", toModel);
        Assert.Contains("***", toModel);
        var everything = string.Join('\n', await app.EventsAsync(run.Id)) + run.Summary + string.Join('\n', await app.CommentsAsync(issue.Id));
        Assert.DoesNotContain("gbw_AbCdEf", everything);
    }

    [Fact]
    public void Branch_names_come_from_the_issue_and_are_safe()
    {
        Assert.Equal("ai/web-12-fix-the-login-bug", AiRunExecutor.BranchName("ai/", "WEB", 12, "Fix the login bug!"));
        Assert.Equal("ai/web-3", AiRunExecutor.BranchName("ai/", "WEB", 3, "???"));
        Assert.Equal("ai/web-5-" + new string('a', 40), AiRunExecutor.BranchName("ai/", "WEB", 5, new string('a', 100)));
        Assert.Equal("ai/web-1-x", AiRunExecutor.BranchName("../evil", "WEB", 1, "x")); // an invalid prefix falls back
        Assert.All(new[] { "Ünïcode título", "a/../b", "--force", "x.lock" }, t => Assert.True(WorkspaceLifecycle.IsSafeRef(AiRunExecutor.BranchName("ai/", "T", 1, t)), t));
    }

    [Fact]
    public void The_meter_prices_tokens_and_the_masker_cleans_credentials()
    {
        var m = new AiRunMeter();
        m.Add(new EditorModelTurn("", [], "end_turn", 1_000_000, 100_000, 2_000_000, 0));
        var o = new AiRunOptions();
        Assert.Equal(3m + 1.5m + 0.6m, m.Cost(o));
        Assert.Equal(1_000_000 + 100_000 + 200_000, m.WeightedTokens);
        Assert.Equal("a *** b ://***@h *** c", AiRunMasking.Mask("a gbw_abcdefgh1234 b ://u:p@h sk-ant-abcdefgh1234 c"));
    }
}

public class AiRunConcurrencyTests(AiRunApp1 app) : IClassFixture<AiRunApp1>
{
    [Fact]
    public async Task Only_the_configured_number_of_runs_work_at_once_and_the_rest_wait_their_turn()
    {
        var (_, repoA, issueA) = await app.SetupAsync(title: "First");
        var (_, _, issueB) = await app.SetupAsync(title: "Second");
        app.ScriptGit(repoA, status: "");
        var gate = new ManualResetEventSlim();
        app.Model.Reset((turn, _) => { if (turn == 0) gate.Wait(TimeSpan.FromSeconds(20)); return FakeEditorChatModel.Text("done " + turn); });
        var a = await app.RequestAsync(issueA);
        await app.WaitForStatusAsync(a.PublicId, AiRunStatus.Running);
        var b = await app.RequestAsync(issueB);
        await Task.Delay(300);
        await using (var db = app.Db())
            Assert.Equal(AiRunStatus.Queued, (await db.AiRuns.AsNoTracking().SingleAsync(r => r.PublicId == b.PublicId)).Status); // the cap is 1
        gate.Set();
        Assert.Equal(AiRunStatus.Succeeded, (await app.WaitAsync(a.PublicId)).Status);
        Assert.Equal(AiRunStatus.Succeeded, (await app.WaitAsync(b.PublicId)).Status); // started as soon as the first one finished
    }
}

public class AiRunQueueLimitTests(AiRunQueueApp app) : IClassFixture<AiRunQueueApp>
{
    [Fact]
    public async Task A_full_queue_refuses_new_requests()
    {
        var (_, repo, first) = await app.SetupAsync(title: "One");
        var (_, _, second) = await app.SetupAsync(title: "Two");
        var (_, _, third) = await app.SetupAsync(title: "Three");
        app.ScriptGit(repo, status: "");
        var gate = new ManualResetEventSlim();
        app.Model.Reset((turn, _) => { if (turn == 0) gate.Wait(TimeSpan.FromSeconds(20)); return FakeEditorChatModel.Text("ok"); });
        var a = await app.RequestAsync(first);
        await app.WaitForStatusAsync(a.PublicId, AiRunStatus.Running);
        var b = await app.RequestAsync(second); // waits in the queue (1 allowed)
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => app.RequestAsync(third));
        Assert.Contains("queue is full", ex.Message);
        gate.Set();
        await app.WaitAsync(a.PublicId);
        await app.WaitAsync(b.PublicId);
    }
}

public class AiRunDailyLimitTests(AiRunDailyApp app) : IClassFixture<AiRunDailyApp>
{
    [Fact]
    public async Task A_project_can_only_start_so_many_runs_a_day()
    {
        var (project, repo, a) = await app.SetupAsync();
        app.ScriptGit(repo, status: "");
        app.Model.Reset((_, _) => FakeEditorChatModel.Text("ok"));
        var b = await app.CreateIssueAsync(project.Id, "alice", "b");
        var c = await app.CreateIssueAsync(project.Id, "alice", "c");
        await app.WaitAsync((await app.RequestAsync(a)).PublicId);
        await app.WaitAsync((await app.RequestAsync(b)).PublicId);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => app.RequestAsync(c));
        Assert.Contains("limit of 2 AI runs per day", ex.Message);
    }
}

public class AiRunStepLimitTests(AiRunStepsApp app) : IClassFixture<AiRunStepsApp>
{
    [Fact]
    public async Task The_step_limit_stops_the_run_comments_and_opens_a_draft_pull_request_with_the_work_so_far()
    {
        var (_, repo, issue) = await app.SetupAsync();
        app.ScriptGit(repo);
        app.Model.Reset((turn, _) => FakeEditorChatModel.Tool("write_file", new { path = $"f{turn}.txt", content = "x\n" }, "t" + turn));
        var run = await app.WaitAsync((await app.RequestAsync(issue)).PublicId);
        Assert.Equal((AiRunStatus.Failed, "step limit", 2), (run.Status, run.StopReason, run.Steps));
        await using var db = app.Db();
        var pr = await db.PullRequests.AsNoTracking().SingleAsync(p => p.Id == run.PullRequestId);
        Assert.True(pr.IsDraft);
        Assert.Contains("incomplete", pr.Description);
        var comment = Assert.Single(await app.CommentsAsync(issue.Id));
        Assert.Contains("step limit", comment);
        Assert.Contains("draft", comment);
        Assert.Equal(WorkspaceState.Stopped, (await app.WorkspaceOfAsync(run)).State);
    }
}

public class AiRunTokenBudgetTests(AiRunTokensApp app) : IClassFixture<AiRunTokensApp>
{
    [Fact]
    public async Task The_token_budget_stops_the_run()
    {
        var (_, repo, issue) = await app.SetupAsync();
        app.ScriptGit(repo, status: "");
        app.Model.Reset((turn, _) => FakeEditorChatModel.Tool("list_files", new { }, "t" + turn)); // 120 tokens a turn, budget 250
        var run = await app.WaitAsync((await app.RequestAsync(issue)).PublicId);
        Assert.Equal((AiRunStatus.Failed, "token budget", 3), (run.Status, run.StopReason, run.Steps));
        Assert.Contains("token budget", Assert.Single(await app.CommentsAsync(issue.Id)));
        Assert.Contains("before making any change", run.Error);
    }
}

public class AiRunCostBudgetTests(AiRunCostApp app) : IClassFixture<AiRunCostApp>
{
    [Fact]
    public async Task The_cost_budget_stops_the_run()
    {
        var (_, repo, issue) = await app.SetupAsync();
        app.ScriptGit(repo, status: "");
        app.Model.Reset((turn, _) => FakeEditorChatModel.Tool("list_files", new { }, "t" + turn)); // $0.0006 a turn, budget $0.0005
        var run = await app.WaitAsync((await app.RequestAsync(issue)).PublicId);
        Assert.Equal((AiRunStatus.Failed, "cost budget", 1), (run.Status, run.StopReason, run.Steps));
        Assert.True(run.CostUsd >= 0.0005m);
    }
}

public class AiRunToolLimitTests(AiRunToolsApp app) : IClassFixture<AiRunToolsApp>
{
    [Fact]
    public async Task The_tool_call_limit_stops_the_run()
    {
        var (_, repo, issue) = await app.SetupAsync();
        app.ScriptGit(repo, status: "");
        app.Model.Reset((turn, _) => FakeEditorChatModel.Tool("list_files", new { }, "t" + turn));
        var run = await app.WaitAsync((await app.RequestAsync(issue)).PublicId);
        Assert.Equal((AiRunStatus.Failed, "tool call limit", 2), (run.Status, run.StopReason, run.ToolCalls));
    }
}

public class AiRunTimeLimitTests(AiRunTimeApp app) : IClassFixture<AiRunTimeApp>
{
    [Fact]
    public async Task The_time_limit_stops_a_run_that_takes_too_long()
    {
        var (_, repo, issue) = await app.SetupAsync();
        app.ScriptGit(repo, status: "");
        app.Model.Reset((turn, _) => { Thread.Sleep(1800); return FakeEditorChatModel.Tool("list_files", new { }, "t" + turn); }); // the limit is 1.2 s
        var run = await app.WaitAsync((await app.RequestAsync(issue)).PublicId);
        Assert.Equal((AiRunStatus.Failed, "time limit"), (run.Status, run.StopReason));
        Assert.Contains("time limit", Assert.Single(await app.CommentsAsync(issue.Id)));
        Assert.Equal(WorkspaceState.Stopped, (await app.WorkspaceOfAsync(run)).State);
    }
}

public class AiBotGitAccessTests(AiRunApp app) : IClassFixture<AiRunApp>
{
    private static HttpClient Client(string baseAddress, string user, string password) => new() { BaseAddress = new Uri(baseAddress), DefaultRequestHeaders = { Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}"))) } };

    [Fact]
    public async Task The_locked_out_ai_user_can_use_only_its_own_workspace_git_token_and_only_for_that_repository()
    {
        var (project, repo, _) = await app.SetupAsync();
        var other = await app.CreateRepoAsync(project.Id, "alice", "other");
        app.Backend.Execs.Clear();
        await using (var s = app.Scope(app.BotId()))
        {
            await s.Get<WorkspaceService>().StartAsync(project.Key, repo.Slug, "main", "test", app.BotId());
            await app.WhenIdleAsync();
        }
        var token = app.Backend.GitToken()!;
        Assert.StartsWith("gbw_", token);
        var info = (string slug) => $"git/{project.Key}/{slug}.git/info/refs?service=git-upload-pack";
        using var good = Client(app.ServerAddress.ToString(), AiBot.UserName, token);
        Assert.Equal(HttpStatusCode.OK, (await good.GetAsync(info(repo.Slug))).StatusCode);
        Assert.NotEqual(HttpStatusCode.OK, (await good.GetAsync(info(other.Slug))).StatusCode);

        // Anything else is still locked out: a password, a made-up workspace token.
        using var password = Client(app.ServerAddress.ToString(), AiBot.UserName, SeedData.DemoPassword);
        Assert.Equal(HttpStatusCode.Unauthorized, (await password.GetAsync(info(repo.Slug))).StatusCode);
        using var made = Client(app.ServerAddress.ToString(), AiBot.UserName, "gbw_notarealtoken00000000");
        Assert.Equal(HttpStatusCode.Unauthorized, (await made.GetAsync(info(repo.Slug))).StatusCode);
    }
}

public class AiRunOffApp : AiRunApp { protected override void ExtraSettings(IDictionary<string, string?> s) => s["Workspaces:AiRuns:Enabled"] = "false"; }

public class AiRunCancelQueuedTests(AiRunOffApp app) : IClassFixture<AiRunOffApp>
{
    [Fact]
    public async Task A_queued_run_is_cancelled_at_once_through_the_rest_api_and_never_starts_a_workspace()
    {
        var (project, repo, issue) = await app.SetupAsync();
        // With runs switched off in the configuration the worker starts nothing, so the queued row stays put.
        await using (var db = app.Db())
        {
            db.AiRuns.Add(new AiRun { PublicId = WorkspaceTokens.NewPublicId(), ProjectId = project.Id, RepositoryId = repo.Id, IssueId = issue.Id, RequestedById = app.UserId("alice"), BaseBranch = "main" });
            await db.SaveChangesAsync();
        }
        Assert.Equal(0, await app.Worker.TickAsync(CancellationToken.None));
        string publicId;
        await using (var db = app.Db()) publicId = (await db.AiRuns.SingleAsync(r => r.IssueId == issue.Id)).PublicId;
        using var client = await app.ApiClientAsync("alice");
        client.BaseAddress = app.ServerAddress;
        var response = await client.PostAsync($"/api/v1/ai-runs/{publicId}/cancel", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("cancelled", JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.PostAsync($"/api/v1/ai-runs/{publicId}/cancel", null)).StatusCode);
        await using var db2 = app.Db();
        var run = await db2.AiRuns.SingleAsync(r => r.PublicId == publicId);
        Assert.Null(run.WorkspaceId);
        Assert.Empty(app.Backend.Started);
        // And with them off nobody can ask for a new one.
        Assert.Contains("configuration", (await Assert.ThrowsAsync<InvalidOperationException>(() => app.RequestAsync(issue))).Message);
    }
}
