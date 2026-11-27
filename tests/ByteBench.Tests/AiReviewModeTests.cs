using System.Net;
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

public class LineDiffTests
{
    private static string Join(params string[] lines) => string.Concat(lines.Select(l => l + "\n"));

    [Fact]
    public void Applying_every_hunk_gives_the_new_text_and_none_gives_the_original()
    {
        var a = Join("one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten");
        var b = Join("ONE", "two", "three", "four", "five", "six", "seven", "eight", "nine", "TEN", "eleven");
        var d = LineDiff.Compute(a, b);
        Assert.Equal(2, d.Hunks.Count);
        Assert.Equal(b, d.Apply(new HashSet<int> { 0, 1 }));
        Assert.Equal(a, d.Apply(new HashSet<int>()));
        Assert.Equal(Join("ONE", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten"), d.Apply(new HashSet<int> { 0 }));
        Assert.Equal(Join("one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "TEN", "eleven"), d.Apply(new HashSet<int> { 1 }));
        Assert.Equal(("ONE\n", "one\n"), (d.Hunks[0].NewLines[0], d.Hunks[0].OldLines[0]));
        Assert.Equal(3, d.Hunks[1].ContextBefore.Count); // context for display
    }

    [Theory]
    [InlineData("", "a\nb\n")]
    [InlineData("a\nb\n", "")]
    [InlineData("a\nb", "a\nb\n")] // a missing final newline is a change
    [InlineData("a\r\nb\r\n", "a\r\nc\r\n")]
    [InlineData("x\n", "x\n")]
    public void Edge_cases_round_trip(string original, string updated)
    {
        var d = LineDiff.Compute(original, updated);
        Assert.Equal(updated, d.Apply(Enumerable.Range(0, d.Hunks.Count).ToHashSet()));
        Assert.Equal(original, d.Apply(new HashSet<int>()));
        Assert.Equal(original == updated, d.Hunks.Count == 0);
    }

    [Fact]
    public void Inserted_and_moved_lines_form_separate_hunks_and_huge_inputs_fall_back_to_one_hunk()
    {
        var a = string.Concat(Enumerable.Range(0, 30).Select(i => $"line {i}\n"));
        var b = a.Replace("line 5\n", "line 5\nnew line\n").Replace("line 25\n", "");
        var d = LineDiff.Compute(a, b);
        Assert.Equal(2, d.Hunks.Count);
        Assert.Equal(b, d.Apply(new HashSet<int> { 0, 1 }));

        var big1 = string.Concat(Enumerable.Range(0, 3000).Select(i => $"a{i}\n"));
        var big2 = string.Concat(Enumerable.Range(0, 3000).Select(i => $"b{i}\n"));
        var bigDiff = LineDiff.Compute(big1, big2);
        Assert.Single(bigDiff.Hunks);
        Assert.Equal(big2, bigDiff.Apply(new HashSet<int> { 0 }));
    }

    [Fact]
    public void The_proposal_collector_ignores_a_file_edited_back_and_enforces_its_limits()
    {
        var c = new AiProposalCollector(maxFiles: 2, maxBytes: 100);
        Assert.Null(c.Set("a.txt", "x\n", "y\n"));
        Assert.Null(c.Set("a.txt", "ignored\n", "x\n")); // the first original wins; edited back to it
        Assert.False(c.HasChanges);
        Assert.Null(c.Set("b.txt", null, "new\n"));
        Assert.Contains("Too many files", c.Set("c.txt", null, "z"));
        Assert.Contains("too large", c.Set("b.txt", null, new string('x', 200)));
        Assert.Single(c.Changes);
        Assert.Equal("b.txt", c.Changes[0].Path);
    }
}

/// <summary>An in-memory file system behind the fake Docker backend: answers the assistant's read, write and delete scripts.</summary>
public sealed class FakeFs
{
    public Dictionary<string, string> Files { get; } = new();
    public List<string> Writes { get; } = [];
    public List<string> Deletes { get; } = [];

    public void Attach(FakeWorkspaceBackend backend)
    {
        backend.ExecScripts.Clear();
        backend.ExecScripts.Add((s => Script(s, "cat -- \"$t\""), s => Files.TryGetValue(s.Command[^1], out var t) ? new ExecResult(0, t) : new ExecResult(3, "No such file")));
        backend.ExecScripts.Add((s => Script(s, "cat > \"$t\""), s => { Files[s.Command[^1]] = s.Stdin ?? ""; Writes.Add(s.Command[^1]); return new ExecResult(0, ""); }));
        backend.ExecScripts.Add((s => Script(s, "rm -f -- \"$t\""), s => { Files.Remove(s.Command[^1]); Deletes.Add(s.Command[^1]); return new ExecResult(0, ""); }));
    }

    private static bool Script(ExecSpec s, string marker) => s.User == "bbai" && s.Command.Count > 3 && s.Command[0] == "sh" && s.Command.Any(c => c.Contains(marker));
}

public class AiReviewModeTests(AiRunApp app) : IClassFixture<AiRunApp>
{
    private readonly FakeFs fs = new();

    private async Task<(string User, Workspace Ws)> RunningAsync()
    {
        var user = await app.NewUserAsync();
        var repo = await app.RepoWithMembersAsync((user, ProjectRole.Developer));
        var ws = await app.StartAsync(user, repo);
        fs.Attach(app.Backend);
        return (user, ws);
    }

    private async Task<WorkspaceAiResult> AskAsync(string user, string publicId, string message = "change things")
    {
        await using var s = app.Scope(app.UserId(user));
        return await s.Get<WorkspaceAiService>().RunAsync(publicId, app.UserId(user), message, [], NullEditorAiSink.Instance, CancellationToken.None);
    }

    private static string Lines(params string[] l) => string.Concat(l.Select(x => x + "\n"));

    private void ScriptEdits(params (string Tool, object Input)[] calls) =>
        app.Model.Reset((turn, _) => turn < calls.Length ? FakeEditorChatModel.Tool(calls[turn].Tool, calls[turn].Input, "t" + turn) : FakeEditorChatModel.Text("Done."));

    private async Task<AiChangeSetView> PendingAsync(string user, string publicId)
    {
        await using var s = app.Scope(app.UserId(user));
        return (await s.Get<AiChangeService>().ListAsync(publicId, app.UserId(user), pendingOnly: true)).Single();
    }

    private async Task<List<string>> AuditActionsAsync(string publicId)
    {
        await using var db = app.Db();
        return await db.AuditEvents.Where(a => a.TargetId == publicId && a.Action.StartsWith("workspace.ai_")).OrderBy(a => a.Id).Select(a => a.Action).ToListAsync();
    }

    [Fact]
    public async Task The_default_is_review_first_edits_become_a_pending_change_set_and_the_workspace_is_untouched()
    {
        var (user, ws) = await RunningAsync();
        fs.Files["src/a.txt"] = Lines("one", "two", "three");
        ScriptEdits(("write_file", new { path = "src/a.txt", content = Lines("one", "TWO", "three") }), ("write_file", new { path = "src/new.txt", content = "hello\n" }));
        var result = await AskAsync(user, ws.PublicId, "tweak it");
        Assert.Null(result.Error);
        Assert.Equal(AiEditMode.ReviewFirst, result.Mode);
        Assert.NotNull(result.ChangeSetId);
        Assert.Empty(fs.Writes);                                   // nothing was written
        Assert.Equal(Lines("one", "two", "three"), fs.Files["src/a.txt"]);
        Assert.Contains("Proposed", app.Model.Results[0][0].Content);
        Assert.Contains("ReviewFirst", string.Join("", await AuditToolDetailsAsync(ws.PublicId)));
        Assert.Contains("developer reviews first", app.Model.Setups.Last().System);

        var set = await PendingAsync(user, ws.PublicId);
        Assert.Equal("tweak it", set.Prompt);
        Assert.Equal(2, set.Files.Count);
        var modify = set.Files.Single(f => f.Path == "src/a.txt");
        Assert.Equal(AiChangeKind.Modify, modify.Kind);
        Assert.Equal(("two", "TWO"), (modify.Hunks.Single().Removed.Single(), modify.Hunks.Single().Added.Single()));
        Assert.Equal(AiChangeKind.Create, set.Files.Single(f => f.Path == "src/new.txt").Kind);
        Assert.All(set.Files, f => Assert.Equal(AiChangeDecision.Pending, f.Decision));
        Assert.Contains("workspace.ai_changes_proposed", await AuditActionsAsync(ws.PublicId));
    }

    private async Task<List<string>> AuditToolDetailsAsync(string publicId)
    {
        await using var db = app.Db();
        return await db.AuditEvents.Where(a => a.TargetId == publicId && a.Action == "workspace.ai_tools").Select(a => a.Details ?? "").ToListAsync();
    }

    [Fact]
    public async Task Edits_and_reads_in_one_request_see_the_assistants_own_proposal_and_collapse_into_one_change()
    {
        var (user, ws) = await RunningAsync();
        fs.Files["a.txt"] = Lines("alpha", "beta");
        ScriptEdits(("write_file", new { path = "a.txt", content = Lines("alpha", "beta", "gamma") }),
            ("edit_file", new { path = "a.txt", find = "gamma", replace = "GAMMA" }),
            ("read_file", new { path = "a.txt" }));
        var result = await AskAsync(user, ws.PublicId);
        Assert.Null(result.Error);
        Assert.Contains("GAMMA", app.Model.Results[2][0].Content);
        Assert.Contains("proposed, not applied yet", app.Model.Results[2][0].Content);
        var set = await PendingAsync(user, ws.PublicId);
        var file = Assert.Single(set.Files);
        Assert.Equal(1, file.AddedLines);
        Assert.Empty(fs.Writes);
    }

    [Fact]
    public async Task Accept_all_writes_every_file_through_the_assistants_checked_script_and_resolves_the_set()
    {
        var (user, ws) = await RunningAsync();
        fs.Files["a.txt"] = Lines("one", "two");
        fs.Files["gone.txt"] = "bye\n";
        ScriptEdits(("write_file", new { path = "a.txt", content = Lines("one", "2") }), ("write_file", new { path = "b/new.txt", content = "n\n" }), ("delete_file", new { path = "gone.txt" }));
        await AskAsync(user, ws.PublicId);
        var set = await PendingAsync(user, ws.PublicId);
        Assert.Equal(3, set.Files.Count);

        await using (var s = app.Scope(app.UserId(user)))
        {
            var r = await s.Get<AiChangeService>().AcceptAllAsync(ws.PublicId, app.UserId(user), set.Id);
            Assert.Equal((3, 0, 0), (r.Applied, r.Rejected, r.Conflicts));
            Assert.Equal(AiChangeSetState.Resolved, r.ChangeSet.State);
        }
        Assert.Equal(Lines("one", "2"), fs.Files["a.txt"]);
        Assert.Equal("n\n", fs.Files["b/new.txt"]);
        Assert.False(fs.Files.ContainsKey("gone.txt"));
        Assert.Equal(["gone.txt"], fs.Deletes);
        var writes = app.Backend.Execs.Where(e => e.Spec.Stdin is "n\n").ToList();
        Assert.Single(writes);
        Assert.Equal("bbai", writes[0].Spec.User); // the same unprivileged user and path checks as the assistant's own tools
        Assert.Contains(writes[0].Spec.Command, c => c.Contains("Path escapes the workspace"));
        var actions = await AuditActionsAsync(ws.PublicId);
        Assert.Equal(3, actions.Count(a => a == "workspace.ai_change_applied"));
        Assert.Contains("workspace.ai_changeset_resolved", actions);
        Assert.Empty(await ListPendingAsync(user, ws.PublicId));
    }

    private async Task<List<AiChangeSetView>> ListPendingAsync(string user, string publicId)
    {
        await using var s = app.Scope(app.UserId(user));
        return await s.Get<AiChangeService>().ListAsync(publicId, app.UserId(user), pendingOnly: true);
    }

    [Fact]
    public async Task Rejected_changes_are_discarded_without_touching_the_workspace()
    {
        var (user, ws) = await RunningAsync();
        fs.Files["a.txt"] = "keep\n";
        ScriptEdits(("write_file", new { path = "a.txt", content = "changed\n" }));
        await AskAsync(user, ws.PublicId);
        var set = await PendingAsync(user, ws.PublicId);
        await using (var s = app.Scope(app.UserId(user)))
        {
            var r = await s.Get<AiChangeService>().RejectAllAsync(ws.PublicId, app.UserId(user), set.Id);
            Assert.Equal((0, 1, 0), (r.Applied, r.Rejected, r.Conflicts));
            Assert.Equal(AiChangeDecision.Rejected, r.ChangeSet.Files.Single().Decision);
        }
        Assert.Empty(fs.Writes);
        Assert.Equal("keep\n", fs.Files["a.txt"]);
        Assert.Contains("workspace.ai_change_rejected", await AuditActionsAsync(ws.PublicId));
        // A decided file can't be decided again.
        await using var again = app.Scope(app.UserId(user));
        await Assert.ThrowsAsync<InvalidOperationException>(() => again.Get<AiChangeService>().DecideFileAsync(ws.PublicId, app.UserId(user), set.Id, set.Files[0].Id, null));
    }

    [Fact]
    public async Task Hunks_can_be_accepted_one_by_one_and_only_those_are_written()
    {
        var (user, ws) = await RunningAsync();
        var original = Lines(Enumerable.Range(1, 12).Select(i => $"line{i}").ToArray());
        fs.Files["f.txt"] = original;
        ScriptEdits(("write_file", new { path = "f.txt", content = original.Replace("line2\n", "LINE2\n").Replace("line11\n", "LINE11\n") }));
        await AskAsync(user, ws.PublicId);
        var set = await PendingAsync(user, ws.PublicId);
        var file = set.Files.Single();
        Assert.Equal(2, file.Hunks.Count);

        await using (var s = app.Scope(app.UserId(user)))
        {
            var r = await s.Get<AiChangeService>().DecideFileAsync(ws.PublicId, app.UserId(user), set.Id, file.Id, [1]);
            var decided = r.ChangeSet.Files.Single();
            Assert.Equal(AiChangeDecision.Partial, decided.Decision);
            Assert.Equal([1], decided.AcceptedHunks);
            Assert.Equal(AiChangeSetState.Resolved, r.ChangeSet.State);
        }
        Assert.Equal(original.Replace("line11\n", "LINE11\n"), fs.Files["f.txt"]);
        Assert.Single(fs.Writes);
    }

    [Fact]
    public async Task A_file_that_changed_since_the_proposal_is_a_conflict_and_nothing_is_written()
    {
        var (user, ws) = await RunningAsync();
        fs.Files["a.txt"] = "v1\n";
        ScriptEdits(("write_file", new { path = "a.txt", content = "v2\n" }), ("write_file", new { path = "exists.txt", content = "mine\n" }));
        await AskAsync(user, ws.PublicId);
        var set = await PendingAsync(user, ws.PublicId);
        fs.Files["a.txt"] = "edited by the developer\n";
        fs.Files["exists.txt"] = "someone else's\n";
        await using var s = app.Scope(app.UserId(user));
        var r = await s.Get<AiChangeService>().AcceptAllAsync(ws.PublicId, app.UserId(user), set.Id);
        Assert.Equal((0, 0, 2), (r.Applied, r.Rejected, r.Conflicts));
        Assert.All(r.ChangeSet.Files, f => Assert.Equal(AiChangeDecision.Conflict, f.Decision));
        Assert.Empty(fs.Writes);
        Assert.Equal("edited by the developer\n", fs.Files["a.txt"]);
        Assert.Contains("workspace.ai_change_conflict", await AuditActionsAsync(ws.PublicId));
    }

    [Fact]
    public async Task Apply_directly_writes_at_once_audits_the_files_and_leaves_no_change_set()
    {
        var (user, ws) = await RunningAsync();
        await using (var s = app.Scope(app.UserId(user)))
            Assert.Equal(AiEditMode.ApplyDirectly, (await s.Get<AiChangeService>().SetModeAsync(ws.PublicId, app.UserId(user), AiEditMode.ApplyDirectly, forAllWorkspaces: false)).Effective);
        ScriptEdits(("write_file", new { path = "direct.txt", content = "now\n" }), ("delete_file", new { path = "direct.txt" }));
        var result = await AskAsync(user, ws.PublicId);
        Assert.Equal(AiEditMode.ApplyDirectly, result.Mode);
        Assert.Null(result.ChangeSetId);
        Assert.Equal(["direct.txt"], fs.Writes);
        Assert.Equal(["direct.txt"], fs.Deletes);
        Assert.Empty(await ListPendingAsync(user, ws.PublicId));
        var details = string.Join("", await AuditToolDetailsAsync(ws.PublicId));
        Assert.Contains("ApplyDirectly", details);
        Assert.Contains("direct.txt", details);
        Assert.Contains("change the working tree right away", app.Model.Setups.Last().System);
    }

    [Fact]
    public async Task The_workspace_choice_beats_the_users_default_and_clearing_it_falls_back()
    {
        var (user, ws) = await RunningAsync();
        var uid = app.UserId(user);
        await using var s = app.Scope(uid);
        var svc = s.Get<AiChangeService>();
        Assert.Equal(AiEditMode.ReviewFirst, (await svc.GetModeInfoAsync(ws.PublicId, uid)).Effective);
        var info = await svc.SetModeAsync(ws.PublicId, uid, AiEditMode.ApplyDirectly, forAllWorkspaces: true);
        Assert.Equal((AiEditMode.ApplyDirectly, null, AiEditMode.ApplyDirectly), (info.Effective, info.WorkspaceMode, info.DefaultMode));
        info = await svc.SetModeAsync(ws.PublicId, uid, AiEditMode.ReviewFirst, forAllWorkspaces: false);
        Assert.Equal((AiEditMode.ReviewFirst, AiEditMode.ReviewFirst, AiEditMode.ApplyDirectly), (info.Effective, info.WorkspaceMode, info.DefaultMode));
        info = await svc.SetModeAsync(ws.PublicId, uid, null, forAllWorkspaces: false);
        Assert.Equal(AiEditMode.ApplyDirectly, info.Effective);
        // The default applies to another workspace of the same user too.
        var repo = await app.RepoWithMembersAsync((user, ProjectRole.Developer));
        var other = await app.StartAsync(user, repo);
        Assert.Equal(AiEditMode.ApplyDirectly, (await svc.GetModeInfoAsync(other.PublicId, uid)).Effective);
        Assert.Contains("workspace.ai_mode", await AuditActionsAsync(ws.PublicId));
        await svc.SetModeAsync(ws.PublicId, uid, null, forAllWorkspaces: true);
    }

    [Fact]
    public async Task Only_the_owner_sees_or_decides_a_change_set()
    {
        var (user, ws) = await RunningAsync();
        fs.Files["a.txt"] = "x\n";
        ScriptEdits(("write_file", new { path = "a.txt", content = "y\n" }));
        await AskAsync(user, ws.PublicId);
        var set = await PendingAsync(user, ws.PublicId);
        var other = await app.NewUserAsync();
        await using var s = app.Scope(app.UserId(other));
        var svc = s.Get<AiChangeService>();
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.ListAsync(ws.PublicId, app.UserId(other)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.AcceptAllAsync(ws.PublicId, app.UserId(other), set.Id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.SetModeAsync(ws.PublicId, app.UserId(other), AiEditMode.ApplyDirectly, false));
        Assert.Empty(fs.Writes);
    }

    [Fact]
    public async Task Proposals_for_paths_outside_the_checkout_are_refused_before_anything_is_stored()
    {
        var (user, ws) = await RunningAsync();
        ScriptEdits(("write_file", new { path = "../../etc/passwd", content = "x" }), ("write_file", new { path = ".git/config", content = "x" }), ("delete_file", new { path = "/etc/hosts" }));
        var result = await AskAsync(user, ws.PublicId);
        Assert.Null(result.ChangeSetId);
        Assert.All(app.Model.Results.Take(3), r => Assert.True(r[0].IsError));
        Assert.Empty(await ListPendingAsync(user, ws.PublicId));
    }

    [Fact]
    public async Task The_rest_api_lists_decides_and_sets_the_mode_and_hides_other_peoples_workspaces()
    {
        var (user, ws) = await RunningAsync();
        fs.Files["a.txt"] = Lines("one", "two");
        ScriptEdits(("write_file", new { path = "a.txt", content = Lines("one", "2") }));
        await AskAsync(user, ws.PublicId);
        using var client = await app.ApiClientAsync(user);
        client.BaseAddress = app.ServerAddress;
        var json = (string body) => new StringContent(body, Encoding.UTF8, "application/json");

        var settings = JsonDocument.Parse(await client.GetStringAsync($"/api/v1/workspaces/{ws.PublicId}/ai/settings")).RootElement;
        Assert.Equal("reviewFirst", settings.GetProperty("mode").GetString());
        var list = JsonDocument.Parse(await client.GetStringAsync($"/api/v1/workspaces/{ws.PublicId}/ai/changes?pending=true")).RootElement;
        var set = list.EnumerateArray().Single();
        var file = set.GetProperty("files").EnumerateArray().Single();
        Assert.Equal("a.txt", file.GetProperty("path").GetString());
        Assert.Equal("two", file.GetProperty("hunks")[0].GetProperty("removed")[0].GetString());
        var setId = set.GetProperty("id").GetInt32();

        var accept = await client.PostAsync($"/api/v1/workspaces/{ws.PublicId}/ai/changes/{setId}/files/{file.GetProperty("id").GetInt32()}/accept", json("""{"hunks":[0]}"""));
        Assert.Equal(HttpStatusCode.OK, accept.StatusCode);
        var decided = JsonDocument.Parse(await accept.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(1, decided.GetProperty("applied").GetInt32());
        Assert.Equal("resolved", decided.GetProperty("changeSet").GetProperty("state").GetString());
        Assert.Equal(Lines("one", "2"), fs.Files["a.txt"]);

        var put = await client.PutAsync($"/api/v1/workspaces/{ws.PublicId}/ai/settings", json("""{"mode":"applyDirectly","scope":"workspace"}"""));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.Equal("applyDirectly", JsonDocument.Parse(await put.Content.ReadAsStringAsync()).RootElement.GetProperty("mode").GetString());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.PutAsync($"/api/v1/workspaces/{ws.PublicId}/ai/settings", json("""{"mode":"applyDirectly","scope":"nope"}"""))).StatusCode);

        var stranger = await app.NewUserAsync();
        using var other = await app.ApiClientAsync(stranger);
        other.BaseAddress = app.ServerAddress;
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/v1/workspaces/{ws.PublicId}/ai/changes")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsync($"/api/v1/workspaces/{ws.PublicId}/ai/changes/{setId}/accept-all", json("{}"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/v1/workspaces/{ws.PublicId}/ai/settings")).StatusCode);
    }

    [Fact]
    public async Task Accepting_needs_a_running_workspace()
    {
        var (user, ws) = await RunningAsync();
        fs.Files["a.txt"] = "x\n";
        ScriptEdits(("write_file", new { path = "a.txt", content = "y\n" }));
        await AskAsync(user, ws.PublicId);
        var set = await PendingAsync(user, ws.PublicId);
        await using var s = app.Scope(app.UserId(user));
        await s.Get<WorkspaceService>().StopAsync(ws.PublicId, app.UserId(user));
        // Rejecting needs no container, accepting does.
        await Assert.ThrowsAsync<InvalidOperationException>(() => s.Get<AiChangeService>().AcceptAllAsync(ws.PublicId, app.UserId(user), set.Id));
        Assert.Empty(fs.Writes);
    }
}
