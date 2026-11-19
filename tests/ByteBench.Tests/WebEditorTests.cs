using System.Text.Json;
using ByteBench.Data;
using ByteBench.Services;
using ByteBench.Services.Editor;
using ByteBench.Services.Editor.Ai;
using ByteBench.Services.Git;
using ByteBench.Services.Webhooks;
using ByteBench.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ByteBench.Tests;

/// <summary>A scripted <see cref="IEditorChatModel"/>: no network, records what the loop sent back.</summary>
public sealed class FakeEditorChatModel : IEditorChatModel
{
    /// <summary>(turn number from 0, the tool results that answered the previous turn) → the next turn.</summary>
    public Func<int, IReadOnlyList<EditorToolResult>, EditorModelTurn> Script { get; set; } = (_, _) => Text("Hello.");
    public List<EditorChatSetup> Setups { get; } = [];
    public List<IReadOnlyList<EditorToolResult>> Results { get; } = [];
    public int Turns { get; private set; }

    public bool IsConfigured => true;

    public void Reset(Func<int, IReadOnlyList<EditorToolResult>, EditorModelTurn> script)
    {
        Script = script;
        Setups.Clear();
        Results.Clear();
        Turns = 0;
    }

    public IEditorChatConversation Begin(EditorChatSetup setup)
    {
        Setups.Add(setup);
        return new Conversation(this);
    }

    public static EditorModelTurn Text(string text) => new(text, [], "end_turn", 100, 20);

    public static EditorModelTurn Tool(string name, object input, string id = "t1") =>
        new("", [new EditorModelToolCall(id, name, JsonSerializer.SerializeToElement(input))], "tool_use", 100, 20);

    private sealed class Conversation(FakeEditorChatModel model) : IEditorChatConversation
    {
        private IReadOnlyList<EditorToolResult> last = [];

        public async Task<EditorModelTurn> NextAsync(Func<string, Task> onText, CancellationToken ct)
        {
            var turn = model.Script(model.Turns++, last);
            if (turn.Text.Length > 0) await onText(turn.Text);
            return turn;
        }

        public void AddToolResults(IReadOnlyList<EditorToolResult> results)
        {
            last = results;
            model.Results.Add(results);
        }
    }
}

/// <summary>The app with the API backend on a fake model, and a push listener that records calls.</summary>
public class WebEditorApp : TestApp
{
    public FakeEditorChatModel Model { get; } = new();
    public PushRecorder Pushes { get; } = new();

    protected override void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<IEditorChatModel>(Model);
        services.AddSingleton(Pushes);
        services.AddScoped<IGitPushListener, RecordingPushListener>();
    }
}

public sealed class PushRecorder
{
    public List<(int RepositoryId, int UserId, IReadOnlyList<PushedRef> Refs)> Calls { get; } = [];
}

public sealed class RecordingPushListener(PushRecorder recorder, PushCapture capture) : IGitPushListener
{
    public Task OnPushAsync(int repositoryId, int userId, CancellationToken ct)
    {
        lock (recorder) recorder.Calls.Add((repositoryId, userId, capture.Refs.ToList()));
        return Task.CompletedTask;
    }
}

public class WebEditorTests(WebEditorApp app) : IClassFixture<WebEditorApp>
{
    private string RepoPath(Repository repo) => app.Services.GetRequiredService<RepoStorage>().PathFor(app.ProjectKey(repo.ProjectId), repo.Slug);

    private async Task<(Project Project, Repository Repo)> SetupAsync()
    {
        var project = await app.CreateProjectAsync("alice", ProjectVisibility.Private, ("bob", ProjectRole.Developer), ("carol", ProjectRole.Viewer));
        var repo = await app.CreateRepoAsync(project.Id, "alice");
        app.Commit(repo, "main", "src/app.txt", "line 1\nline 2\nline 3\n", message: "add app");
        app.Commit(repo, "main", "old.txt", "old\n", message: "add old");
        return (project, repo);
    }

    private async Task<EditorContext> OpenAsync(Repository repo, string user, string branch = "main")
    {
        await using var s = app.Scope(app.UserId(user));
        return await s.Get<EditorWorkspaceService>().OpenAsync(repo, branch, app.UserId(user));
    }

    // ---------- Workspace & commit ----------

    [Fact]
    public async Task Workspace_add_modify_delete_rename_then_commit_writes_the_right_tree()
    {
        var (_, repo) = await SetupAsync();
        var bob = app.UserId("bob");
        var ctx = await OpenAsync(repo, "bob");
        Assert.True(ctx.CanEdit);
        var before = ctx.BaseSha;

        await using (var s = app.Scope(bob))
        {
            var ws = s.Get<EditorWorkspaceService>();
            await ws.SaveFileAsync(ctx.WorkspaceId, bob, "src/app.txt", "line 1\nline two\nline 3\n");
            await ws.CreateFileAsync(ctx.WorkspaceId, bob, "docs/new.md", "# New\n");
            await ws.DeleteFileAsync(ctx.WorkspaceId, bob, "old.txt");
            await ws.RenameFileAsync(ctx.WorkspaceId, bob, "README.md", "READ_ME.md");
            await ws.CreateFileAsync(ctx.WorkspaceId, bob, "tmp.txt", "x");
            await ws.DiscardAsync(ctx.WorkspaceId, bob, "tmp.txt");

            var changes = await ws.GetChangesAsync(ctx.WorkspaceId, bob);
            Assert.Equal(["READ_ME.md", "docs/new.md", "old.txt", "src/app.txt"], changes.Select(c => c.Path).ToArray());
            Assert.Equal(EditorChangeKind.Renamed, changes[0].Kind);
            Assert.Equal("README.md", changes[0].OriginalPath);
            Assert.Equal("line 1\nline 2\nline 3\n", changes.Single(c => c.Path == "src/app.txt").BaseText);

            // Uncommitted: the branch hasn't moved, and the changes survive a fresh service (they're in the database).
            Assert.Equal(before, GitOps.ResolveSha(RepoPath(repo), "main"));
        }

        await using (var s = app.Scope(bob))
        {
            var ws = s.Get<EditorWorkspaceService>();
            var root = await ws.ListDirectoryAsync(ctx.WorkspaceId, bob, "");
            Assert.Contains(root, e => e.Name == "READ_ME.md" && e.Status == EditorFileStatus.Renamed);
            Assert.DoesNotContain(root, e => e.Name == "README.md" || e.Name == "old.txt");
            Assert.Contains(root, e => e.Name == "docs" && e.IsDirectory);
            Assert.Equal("line 1\nline two\nline 3\n", (await ws.ReadFileAsync(ctx.WorkspaceId, bob, "src/app.txt")).Text);

            var result = await ws.CommitAsync(ctx.WorkspaceId, bob, "Edit from the web editor");
            var path = RepoPath(repo);
            Assert.Equal(result.Sha, GitOps.ResolveSha(path, "main"));
            var files = GitOps.ListFiles(path, "main")!.Select(f => f.Path).ToArray();
            Assert.Equal(["READ_ME.md", "docs/new.md", "src/app.txt"], files);
            Assert.Equal("line 1\nline two\nline 3\n", GitOps.GetBlob(path, "main", "src/app.txt")!.Text);
            Assert.Equal("# New\n", GitOps.GetBlob(path, "main", "docs/new.md")!.Text);
            Assert.StartsWith("# code", GitOps.GetBlob(path, "main", "READ_ME.md")!.Text);
            var commit = GitOps.GetCommit(path, "main")!;
            Assert.Equal("Edit from the web editor", commit.MessageShort);
            Assert.Equal(app.User("bob").Email, commit.AuthorEmail);
            Assert.Equal([before], commit.ParentShas);

            var after = await ws.GetContextAsync(ctx.WorkspaceId, bob);
            Assert.Equal(0, after.ChangeCount);
            Assert.Equal(result.Sha, after.BaseSha);
        }
    }

    [Fact]
    public async Task Saving_the_base_content_drops_the_change_and_paths_are_validated()
    {
        var (_, repo) = await SetupAsync();
        var bob = app.UserId("bob");
        var ctx = await OpenAsync(repo, "bob");
        await using var s = app.Scope(bob);
        var ws = s.Get<EditorWorkspaceService>();
        await ws.SaveFileAsync(ctx.WorkspaceId, bob, "old.txt", "changed\n");
        await ws.SaveFileAsync(ctx.WorkspaceId, bob, "old.txt", "old\n");
        Assert.Empty(await ws.GetChangesAsync(ctx.WorkspaceId, bob));

        foreach (var bad in new[] { "../x", "a/../../b", ".git/config", "/", "a//b" })
            await Assert.ThrowsAnyAsync<InvalidOperationException>(() => ws.SaveFileAsync(ctx.WorkspaceId, bob, bad, "x"));
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() =>
            ws.SaveFileAsync(ctx.WorkspaceId, bob, "big.txt", new string('x', (int)GitOps.MaxBlobBytes + 1)));
        // Someone else's workspace is invisible.
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => ws.ReadFileAsync(ctx.WorkspaceId, app.UserId("alice"), "old.txt"));
    }

    [Fact]
    public async Task Push_listeners_run_after_a_commit_like_after_a_git_push()
    {
        var (_, repo) = await SetupAsync();
        var bob = app.UserId("bob");
        var ctx = await OpenAsync(repo, "bob");
        await using var s = app.Scope(bob);
        var ws = s.Get<EditorWorkspaceService>();
        await ws.SaveFileAsync(ctx.WorkspaceId, bob, "src/app.txt", "changed\n");
        var result = await ws.CommitAsync(ctx.WorkspaceId, bob, "Change app");

        var call = Assert.Single(app.Pushes.Calls, c => c.RepositoryId == repo.Id);
        Assert.Equal(bob, call.UserId);
        var pushed = Assert.Single(call.Refs);
        Assert.Equal("refs/heads/main", pushed.Ref);
        Assert.Equal(ctx.BaseSha, pushed.Before);
        Assert.Equal(result.Sha, pushed.After);
    }

    // ---------- Permissions & protection ----------

    [Fact]
    public async Task Protected_branch_refuses_commits_and_offers_a_new_branch_with_a_pull_request()
    {
        var (project, repo) = await SetupAsync();
        await using (var s = app.Scope(app.UserId("alice")))
            await s.Get<BranchProtectionService>().SaveAsync(repo.Id, new BranchProtectionRule { Pattern = "main", BlockDirectPushes = true }, app.UserId("alice"));

        var bob = app.UserId("bob");
        var ctx = await OpenAsync(repo, "bob");
        Assert.True(ctx.CanEdit);
        Assert.NotNull(ctx.ProtectionNote);
        Assert.False(ctx.CanCommitToBranch);
        var tip = GitOps.ResolveSha(RepoPath(repo), "main");

        await using (var s = app.Scope(bob))
        {
            var ws = s.Get<EditorWorkspaceService>();
            await ws.SaveFileAsync(ctx.WorkspaceId, bob, "src/app.txt", "protected change\n");
            await Assert.ThrowsAsync<BranchProtectedException>(() => ws.CommitAsync(ctx.WorkspaceId, bob, "Direct"));
            Assert.Equal(tip, GitOps.ResolveSha(RepoPath(repo), "main"));

            var result = await ws.CommitToNewBranchAsync(ctx.WorkspaceId, bob, "bob/edit", "Change app", "Change app from the editor");
            Assert.NotNull(result.PullRequest);
            Assert.Equal("bob/edit", result.PullRequest!.SourceBranch);
            Assert.Equal("main", result.PullRequest.TargetBranch);
            Assert.Equal(tip, GitOps.ResolveSha(RepoPath(repo), "main"));
            Assert.Equal("protected change\n", GitOps.GetBlob(RepoPath(repo), "bob/edit", "src/app.txt")!.Text);
            Assert.Empty(await ws.GetChangesAsync(ctx.WorkspaceId, bob));
        }
        await using var db = app.Db();
        Assert.True(await db.PullRequests.AnyAsync(p => p.RepositoryId == repo.Id && p.SourceBranch == "bob/edit" && p.AuthorId == bob));
        _ = project;
    }

    [Fact]
    public async Task Viewer_can_browse_but_not_edit_and_archived_repos_are_read_only()
    {
        var (_, repo) = await SetupAsync();
        var carol = app.UserId("carol");
        var ctx = await OpenAsync(repo, "carol");
        Assert.False(ctx.CanEdit);
        Assert.NotNull(ctx.ReadOnlyReason);
        await using (var s = app.Scope(carol))
        {
            var ws = s.Get<EditorWorkspaceService>();
            Assert.Equal("old\n", (await ws.ReadFileAsync(ctx.WorkspaceId, carol, "old.txt")).Text);
            await Assert.ThrowsAsync<ForbiddenException>(() => ws.SaveFileAsync(ctx.WorkspaceId, carol, "old.txt", "hacked"));
            await Assert.ThrowsAsync<ForbiddenException>(() => ws.CommitAsync(ctx.WorkspaceId, carol, "nope"));
        }

        // Outsiders can't even open it.
        var dan = app.UserId("dan");
        await using (var s = app.Scope(dan))
            await Assert.ThrowsAsync<ForbiddenException>(() => s.Get<EditorWorkspaceService>().OpenAsync(repo, "main", dan));

        // Archived: read-only for everyone.
        await using (var db = app.Db())
        {
            await db.Repositories.Where(r => r.Id == repo.Id).ExecuteUpdateAsync(u => u.SetProperty(r => r.IsArchived, true));
        }
        var bob = app.UserId("bob");
        var bobCtx = await OpenAsync(repo, "bob");
        Assert.False(bobCtx.CanEdit);
        await using (var s = app.Scope(bob))
            await Assert.ThrowsAsync<ForbiddenException>(() => s.Get<EditorWorkspaceService>().SaveFileAsync(bobCtx.WorkspaceId, bob, "old.txt", "x"));
    }

    // ---------- Branch moved ----------

    [Fact]
    public async Task Conflicts_are_detected_when_the_branch_moved_and_rebase_resolves_them()
    {
        var (_, repo) = await SetupAsync();
        var bob = app.UserId("bob");
        var ctx = await OpenAsync(repo, "bob");
        await using var s = app.Scope(bob);
        var ws = s.Get<EditorWorkspaceService>();
        await ws.SaveFileAsync(ctx.WorkspaceId, bob, "src/app.txt", "line 1\nmine\nline 3\n");
        await ws.SaveFileAsync(ctx.WorkspaceId, bob, "old.txt", "old\nmore\n");

        // Someone pushes: a conflicting change to src/app.txt and an unrelated new file.
        app.Commit(repo, "main", "src/app.txt", "line 1\ntheirs\nline 3\n", message: "theirs");
        app.Commit(repo, "main", "other.txt", "other\n", message: "unrelated");

        var status = await ws.GetStatusAsync(ctx.WorkspaceId, bob);
        Assert.True(status.BranchMoved);
        Assert.True(status.NeedsRebase);
        Assert.Equal(["src/app.txt"], status.Overlapping);
        Assert.Equal(["src/app.txt"], status.Conflicts);
        await Assert.ThrowsAsync<GitConflictException>(() => ws.CommitAsync(ctx.WorkspaceId, bob, "stale"));

        // Rebase without a decision changes nothing; keeping mine moves the workspace onto the new tip.
        var refused = await ws.RebaseAsync(ctx.WorkspaceId, bob);
        Assert.Equal(["src/app.txt"], refused.Conflicts);
        Assert.Equal(ctx.BaseSha, (await ws.GetContextAsync(ctx.WorkspaceId, bob)).BaseSha);
        await ws.RebaseAsync(ctx.WorkspaceId, bob, new Dictionary<string, ConflictResolution> { ["src/app.txt"] = ConflictResolution.KeepMine });
        var tip = GitOps.ResolveSha(RepoPath(repo), "main");
        Assert.Equal(tip, (await ws.GetContextAsync(ctx.WorkspaceId, bob)).BaseSha);
        Assert.Equal("old\nmore\n", (await ws.ReadFileAsync(ctx.WorkspaceId, bob, "old.txt")).Text);

        var result = await ws.CommitAsync(ctx.WorkspaceId, bob, "After rebase");
        var path = RepoPath(repo);
        Assert.Equal("line 1\nmine\nline 3\n", GitOps.GetBlob(path, result.Sha, "src/app.txt")!.Text);
        Assert.Equal("other\n", GitOps.GetBlob(path, result.Sha, "other.txt")!.Text);
    }

    [Fact]
    public async Task Non_overlapping_upstream_changes_commit_directly_and_clean_merges_rebase_automatically()
    {
        var (_, repo) = await SetupAsync();
        app.Commit(repo, "main", "long.txt", string.Join("\n", Enumerable.Range(1, 20).Select(i => "l" + i)) + "\n", message: "long");
        var bob = app.UserId("bob");
        var ctx = await OpenAsync(repo, "bob");
        await using var s = app.Scope(bob);
        var ws = s.Get<EditorWorkspaceService>();

        await ws.SaveFileAsync(ctx.WorkspaceId, bob, "old.txt", "edited\n");
        app.Commit(repo, "main", "other.txt", "other\n", message: "unrelated");
        var status = await ws.GetStatusAsync(ctx.WorkspaceId, bob);
        Assert.True(status.BranchMoved);
        Assert.False(status.NeedsRebase);
        var first = await ws.CommitAsync(ctx.WorkspaceId, bob, "No overlap");
        Assert.Equal("other\n", GitOps.GetBlob(RepoPath(repo), first.Sha, "other.txt")!.Text);

        // Same file, different lines: a three-way merge.
        var lines = Enumerable.Range(1, 20).Select(i => "l" + i).ToArray();
        var mine = (string[])lines.Clone(); mine[1] = "MINE";
        var theirs = (string[])lines.Clone(); theirs[17] = "THEIRS";
        await ws.SaveFileAsync(ctx.WorkspaceId, bob, "long.txt", string.Join("\n", mine) + "\n");
        app.Commit(repo, "main", "long.txt", string.Join("\n", theirs) + "\n", message: "theirs");
        status = await ws.GetStatusAsync(ctx.WorkspaceId, bob);
        Assert.True(status.NeedsRebase);
        Assert.Empty(status.Conflicts);
        var rebased = await ws.RebaseAsync(ctx.WorkspaceId, bob);
        Assert.Empty(rebased.Conflicts);
        var merged = (await ws.ReadFileAsync(ctx.WorkspaceId, bob, "long.txt")).Text!;
        Assert.Contains("MINE", merged);
        Assert.Contains("THEIRS", merged);
    }

    // ---------- AI ----------

    private static string Str(EditorToolResult r) => r.Content;

    [Fact]
    public async Task Ai_tool_loop_reads_files_and_proposes_an_edit_that_stays_pending_until_accepted()
    {
        var (project, repo) = await SetupAsync();
        var bob = app.UserId("bob");
        var ctx = await OpenAsync(repo, "bob");
        app.Model.Reset((turn, _) => turn switch
        {
            0 => FakeEditorChatModel.Tool("read_file", new { path = "src/app.txt" }),
            1 => FakeEditorChatModel.Tool("search_code", new { query = "line 2" }, "t2"),
            2 => FakeEditorChatModel.Tool("propose_edit", new { path = "src/app.txt", find = "line 2", replace = "line two", summary = "Spell out two" }, "t3"),
            _ => FakeEditorChatModel.Text("I proposed a change to src/app.txt."),
        });

        await using var s = app.Scope(bob);
        var ai = s.Get<EditorAiService>();
        var sink = new RecordingSink();
        var result = await ai.RunAsync(ctx.WorkspaceId, bob, new EditorAiInput("Make line 2 nicer") { OpenFile = "src/app.txt", Selection = "line 2" }, sink, CancellationToken.None);

        Assert.Null(result.Error);
        Assert.Equal(3, result.ToolCalls);
        Assert.Contains("I proposed a change", result.Text);
        // The loop sent the file back as an untrusted block, and search results from git grep.
        Assert.Contains("line 2", Str(app.Model.Results[0][0]));
        Assert.Contains("UNTRUSTED", Str(app.Model.Results[0][0]));
        Assert.False(app.Model.Results[0][0].IsError);
        Assert.Contains("src/app.txt:2", Str(app.Model.Results[1][0]));
        // The system prompt says file content is data; the user turn carries the selection in a delimited block.
        Assert.Contains("untrusted data", app.Model.Setups[0].System);
        Assert.Contains("<<<UNTRUSTED SELECTION", app.Model.Setups[0].UserMessage);

        var proposal = Assert.Single(result.Proposals);
        Assert.Equal(ProposalState.Pending, proposal.State);
        Assert.Equal("line 1\nline two\nline 3\n", proposal.NewText);
        Assert.Single(sink.Proposals);
        Assert.Contains(sink.Tools, t => t.Contains("Reading src/app.txt"));

        // Pending: nothing in the workspace yet.
        var ws = s.Get<EditorWorkspaceService>();
        Assert.Empty(await ws.GetChangesAsync(ctx.WorkspaceId, bob));
        await ai.AcceptAsync(ctx.WorkspaceId, bob, proposal);
        Assert.Equal(ProposalState.Accepted, proposal.State);
        Assert.Equal("line 1\nline two\nline 3\n", (await ws.ReadFileAsync(ctx.WorkspaceId, bob, "src/app.txt")).Text);
        // Accepted edits are working changes, not commits.
        Assert.Equal(ctx.BaseSha, GitOps.ResolveSha(RepoPath(repo), "main"));
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => ai.AcceptAsync(ctx.WorkspaceId, bob, proposal));

        await using var db = app.Db();
        Assert.True(await db.AiUsages.CountAsync(u => u.Feature == "editor.chat" && u.ProjectId == project.Id && u.UserId == bob) >= 4);
    }

    [Fact]
    public async Task Ai_tools_cannot_reach_another_repository()
    {
        var (_, repo) = await SetupAsync();
        var other = await app.CreateProjectAsync("alice", ProjectVisibility.Private, ("bob", ProjectRole.Developer));
        var otherRepo = await app.CreateRepoAsync(other.Id, "alice", "secret");
        app.Commit(otherRepo, "main", "secret.txt", "TOP SECRET\n", message: "secret");
        var bob = app.UserId("bob");
        var ctx = await OpenAsync(repo, "bob");
        var escapes = new object[]
        {
            new { path = "secret.txt" },
            new { path = $"../../{other.Key}/secret.git/HEAD" },
            new { path = "/etc/passwd" },
            new { path = ".git/config" },
        };
        app.Model.Reset((turn, _) => turn < escapes.Length ? FakeEditorChatModel.Tool("read_file", escapes[turn], "t" + turn) : FakeEditorChatModel.Text("done"));

        await using var s = app.Scope(bob);
        var result = await s.Get<EditorAiService>().RunAsync(ctx.WorkspaceId, bob, new EditorAiInput("Read the secret"), NullEditorAiSink.Instance, CancellationToken.None);
        Assert.Null(result.Error);
        Assert.Equal(escapes.Length, app.Model.Results.Count);
        Assert.All(app.Model.Results, r => Assert.True(r[0].IsError));
        Assert.DoesNotContain(app.Model.Results, r => r[0].Content.Contains("TOP SECRET"));

        // A proposal can't write outside the workspace either.
        app.Model.Reset((turn, _) => turn == 0 ? FakeEditorChatModel.Tool("propose_edit", new { path = "../escape.txt", content = "x" }) : FakeEditorChatModel.Text("done"));
        result = await s.Get<EditorAiService>().RunAsync(ctx.WorkspaceId, bob, new EditorAiInput("Write outside"), NullEditorAiSink.Instance, CancellationToken.None);
        Assert.Empty(result.Proposals);
        Assert.True(app.Model.Results[0][0].IsError);
    }

    [Fact]
    public async Task Ai_loop_stops_at_the_tool_call_cap()
    {
        var (_, repo) = await SetupAsync();
        var bob = app.UserId("bob");
        var ctx = await OpenAsync(repo, "bob");
        app.Model.Reset((turn, _) => FakeEditorChatModel.Tool("list_files", new { }, "t" + turn));
        await using var s = app.Scope(bob);
        var result = await s.Get<EditorAiService>().RunAsync(ctx.WorkspaceId, bob, new EditorAiInput("Loop forever"), NullEditorAiSink.Instance, CancellationToken.None);
        Assert.Equal(15, result.ToolCalls);
        Assert.Equal(15, app.Model.Results.Count);
        Assert.Equal(16, app.Model.Turns); // the 16th turn asked for one call too many and was not run
        Assert.Contains("15 tool calls", result.StopNote);
    }

    [Fact]
    public async Task Viewers_can_chat_but_cannot_accept_proposals()
    {
        var (_, repo) = await SetupAsync();
        var carol = app.UserId("carol");
        var ctx = await OpenAsync(repo, "carol");
        app.Model.Reset((turn, _) => turn == 0
            ? FakeEditorChatModel.Tool("propose_edit", new { path = "old.txt", content = "new\n" })
            : FakeEditorChatModel.Text("Proposed."));
        await using var s = app.Scope(carol);
        var ai = s.Get<EditorAiService>();
        var result = await ai.RunAsync(ctx.WorkspaceId, carol, new EditorAiInput("Change old.txt"), NullEditorAiSink.Instance, CancellationToken.None);
        var proposal = Assert.Single(result.Proposals);
        await Assert.ThrowsAsync<ForbiddenException>(() => ai.AcceptAsync(ctx.WorkspaceId, carol, proposal));
    }

    [Fact]
    public async Task Project_setting_turns_the_editor_ai_off_and_only_maintainers_can_change_it()
    {
        var (project, repo) = await SetupAsync();
        var bob = app.UserId("bob");
        var ctx = await OpenAsync(repo, "bob");
        await using var s = app.Scope(bob);
        var ai = s.Get<EditorAiService>();
        Assert.True((await ai.GetStatusAsync(project.Id)).Enabled);
        Assert.Equal(EditorAiBackendKind.Api, (await ai.GetStatusAsync(project.Id)).Backend);

        await Assert.ThrowsAsync<ForbiddenException>(() => ai.SetProjectEnabledAsync(project.Id, false, bob));
        await ai.SetProjectEnabledAsync(project.Id, false, app.UserId("alice"));
        var status = await ai.GetStatusAsync(project.Id);
        Assert.False(status.Enabled);
        Assert.Contains("turned off", status.DisabledReason);
        app.Model.Reset((_, _) => throw new InvalidOperationException("the model must not be called"));
        var result = await ai.RunAsync(ctx.WorkspaceId, bob, new EditorAiInput("hi"), NullEditorAiSink.Instance, CancellationToken.None);
        Assert.NotNull(result.Error);
        Assert.Equal(0, app.Model.Turns);
    }

    private sealed class RecordingSink : IEditorAiSink
    {
        public List<string> Text { get; } = [];
        public List<string> Tools { get; } = [];
        public List<ProposedEdit> Proposals { get; } = [];
        public Task TextAsync(string delta) { Text.Add(delta); return Task.CompletedTask; }
        public Task ToolAsync(string tool, string detail) { Tools.Add(detail); return Task.CompletedTask; }
        public Task ProposalAsync(ProposedEdit edit) { Proposals.Add(edit); return Task.CompletedTask; }
    }
}

/// <summary>The default test app: no API key and no Claude Code, so the editor's AI is "not set up".</summary>
public class WebEditorAiDisabledTests(TestApp app) : IClassFixture<TestApp>
{
    [Fact]
    public async Task Without_an_api_key_or_claude_code_the_editor_ai_is_not_set_up()
    {
        var project = await app.CreateProjectAsync("alice");
        var repo = await app.CreateRepoAsync(project.Id, "alice");
        var alice = app.UserId("alice");
        await using var s = app.Scope(alice);
        var ctx = await s.Get<EditorWorkspaceService>().OpenAsync(repo, "main", alice);
        var ai = s.Get<EditorAiService>();
        var status = await ai.GetStatusAsync(project.Id);
        Assert.False(status.Enabled);
        Assert.False(status.CompletionsAvailable);
        Assert.StartsWith("AI isn't set up", status.DisabledReason);

        var result = await ai.RunAsync(ctx.WorkspaceId, alice, new EditorAiInput("hello"), NullEditorAiSink.Instance, CancellationToken.None);
        Assert.StartsWith("AI isn't set up", result.Error);
        Assert.Null(await ai.CompleteAsync(ctx.WorkspaceId, alice, "README.md", "markdown", "# co", "", CancellationToken.None));
    }
}
