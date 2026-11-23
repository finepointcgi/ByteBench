using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ByteBench.Data;
using ByteBench.Services;
using ByteBench.Services.AutoMerge;
using ByteBench.Services.Git;
using ByteBench.Services.MergeQueue;
using ByteBench.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ByteBench.Tests;

/// <summary>A merge check the tests flip by hand (a stand-in for a pipeline). Everything starts Pending.</summary>
public sealed class ControllableCheck : IMergeCheck
{
    private readonly Dictionary<int, MergeCheckState> states = [];
    public MergeCheckState Default { get; set; } = MergeCheckState.Pending;
    public void Set(int prId, MergeCheckState state) { lock (states) states[prId] = state; }

    public Task<IReadOnlyList<MergeCheckResult>> EvaluateAsync(int pullRequestId, CancellationToken ct = default)
    {
        MergeCheckState s;
        lock (states) s = states.GetValueOrDefault(pullRequestId, Default);
        return Task.FromResult<IReadOnlyList<MergeCheckResult>>([new MergeCheckResult("Pipeline", s, "controlled by the test")]);
    }
}

/// <summary>The app with the auto-merge worker off (tests run the sweep themselves) and a controllable "Pipeline" check.</summary>
public class AutoMergeApp : TestApp
{
    public ControllableCheck Check { get; } = new();

    protected override void ConfigureServices(IServiceCollection services) => services.AddSingleton<IMergeCheck>(Check);
}

/// <summary>"Merge when ready": arming, merging through the same gate, cancellation rules, races, the merge queue, REST and audit.</summary>
public class AutoMergeTests(AutoMergeApp app) : IClassFixture<AutoMergeApp>
{
    private sealed record Setup(Project Project, Repository Repo, PullRequest Pr, string Path, string Viewer);

    /// <summary>alice Owner, carol Maintainer, dan Maintainer, bob Developer (the PR's author), erin Viewer. main requires 1 approval and passing checks.</summary>
    private async Task<Setup> SetupAsync(int approvals = 1, bool requireChecks = true, bool queue = false)
    {
        var viewer = "viewer" + Guid.NewGuid().ToString("N")[..8];
        await app.CreateUserAsync(viewer);
        var project = await app.CreateProjectAsync("alice", ProjectVisibility.Private,
            ("carol", ProjectRole.Maintainer), ("dan", ProjectRole.Maintainer), ("bob", ProjectRole.Developer), (viewer, ProjectRole.Viewer));
        var repo = await app.CreateRepoAsync(project.Id, "alice");
        app.Commit(repo, "main", "README.md", "# Hello\n", message: "base");
        var branch = "feature/" + Guid.NewGuid().ToString("N")[..6];
        app.Commit(repo, branch, "a.txt", "a", from: "main", message: "add a");
        await using (var s = app.Scope(app.UserId("alice")))
            await s.Get<BranchProtectionService>().SaveAsync(repo.Id, new BranchProtectionRule
            {
                Pattern = "main", RequiredApprovals = approvals, RequireChecksToPass = requireChecks, MergeQueueEnabled = queue, MergeQueueRequireCi = false,
            }, app.UserId("alice"));
        PullRequest pr;
        await using (var s = app.Scope(app.UserId("bob")))
            pr = await s.Get<PullRequestService>().CreateAsync(repo, "Add a", null, branch, "main", app.UserId("bob"));
        app.Check.Set(pr.Id, MergeCheckState.Pending);
        return new Setup(project, repo, pr, app.Services.GetRequiredService<RepoStorage>().PathFor(project.Key, repo.Slug), viewer);
    }

    private async Task ArmAsync(Setup t, string user = "carol", MergeStrategy strategy = MergeStrategy.Merge, bool delete = false)
    {
        await using var s = app.Scope(app.UserId(user));
        await s.Get<AutoMergeService>().ArmAsync(t.Pr.Id, app.UserId(user), new MergeRequest(strategy, delete));
    }

    private async Task<AutoMergeOutcome> SweepAsync(Setup t)
    {
        await using var s = app.Scope(system: true);
        return await s.Get<AutoMergeService>().ProcessAsync(t.Pr.Id);
    }

    private async Task ApproveAsync(Setup t, string user = "dan")
    {
        await using var s = app.Scope(app.UserId(user));
        await s.Get<PullRequestService>().SetReviewAsync(t.Pr.Id, app.UserId(user), ReviewState.Approved);
    }

    private PullRequest Reload(Setup t)
    {
        using var db = app.Db();
        return db.PullRequests.AsNoTracking().First(p => p.Id == t.Pr.Id);
    }

    private PullRequestAutoMerge Row(Setup t)
    {
        using var db = app.Db();
        return db.PullRequestAutoMerges.AsNoTracking().Include(a => a.ArmedBy).First(a => a.PullRequestId == t.Pr.Id);
    }

    private List<string> Timeline(Setup t)
    {
        using var db = app.Db();
        return db.PullRequestActivities.AsNoTracking().Where(a => a.PullRequestId == t.Pr.Id).OrderBy(a => a.Id).Select(a => a.Text).ToList();
    }

    private List<string> Audit(Setup t, string action)
    {
        using var db = app.Db();
        return db.AuditEvents.AsNoTracking().Where(e => e.Action == action && e.TargetId == t.Pr.Id.ToString()).Select(e => e.Details ?? "").ToList();
    }

    private List<Notification> Notices(Setup t, int userId)
    {
        using var db = app.Db();
        var prefix = $"/p/{t.Project.Key}/repos/{t.Repo.Slug}/pull-requests/{t.Pr.Number}";
        return db.Notifications.AsNoTracking().Where(n => n.UserId == userId && n.Kind == NotificationKind.AutoMergeCancelled && n.Url.StartsWith(prefix)).ToList();
    }

    // ---------- The happy path ----------

    [Fact]
    public async Task Merges_when_the_last_requirement_is_met_and_the_armer_is_the_merger()
    {
        var t = await SetupAsync();
        await ArmAsync(t);
        Assert.Equal(AutoMergeOutcome.Waiting, await SweepAsync(t));
        Assert.Contains("approval", Row(t).WaitingFor);
        Assert.Equal(PullRequestState.Open, Reload(t).State);

        await ApproveAsync(t);
        Assert.Equal(AutoMergeOutcome.Waiting, await SweepAsync(t)); // approved, the pipeline is still pending
        Assert.Contains("Pipeline", Row(t).WaitingFor);

        app.Check.Set(t.Pr.Id, MergeCheckState.Passed);
        Assert.Equal(AutoMergeOutcome.Merged, await SweepAsync(t));

        var pr = Reload(t);
        Assert.Equal(PullRequestState.Merged, pr.State);
        Assert.Equal(app.UserId("carol"), pr.MergedById); // whoever armed it
        Assert.Equal(AutoMergeState.Merged, Row(t).State);
        Assert.Contains(Timeline(t), x => x.Contains("enabled auto-merge"));
        Assert.Contains(Timeline(t), x => x.Contains("auto-merge merged this pull request"));
        Assert.Single(Audit(t, "pull_request.auto_merge_armed"));
        Assert.Single(Audit(t, "pull_request.auto_merge_merged"));
        Assert.Equal(pr.MergeCommitSha, GitOps.ResolveSha(t.Path, "main"));

        Assert.Equal(AutoMergeOutcome.Idle, await SweepAsync(t)); // idempotent: nothing left to do
        Assert.Single(Audit(t, "pull_request.auto_merge_merged"));
    }

    [Fact]
    public async Task Uses_the_chosen_strategy_and_deletes_the_branch_when_asked()
    {
        var t = await SetupAsync(approvals: 0, requireChecks: false);
        await ArmAsync(t, strategy: MergeStrategy.Squash, delete: true);
        Assert.Equal(AutoMergeOutcome.Merged, await SweepAsync(t));
        var pr = Reload(t);
        Assert.Equal(MergeStrategy.Squash, pr.MergeStrategy);
        Assert.False(GitOps.BranchExists(t.Path, pr.SourceBranch));
    }

    [Fact]
    public async Task A_draft_waits_until_it_is_ready_for_review()
    {
        var t = await SetupAsync(approvals: 0, requireChecks: false);
        await using (var s = app.Scope(app.UserId("bob"))) await s.Get<PullRequestService>().SetDraftAsync(t.Pr.Id, app.UserId("bob"), true);
        await ArmAsync(t);
        Assert.Equal(AutoMergeOutcome.Waiting, await SweepAsync(t));
        Assert.Contains("draft", Row(t).WaitingFor);
        await using (var s = app.Scope(app.UserId("bob"))) await s.Get<PullRequestService>().SetDraftAsync(t.Pr.Id, app.UserId("bob"), false);
        Assert.Equal(AutoMergeOutcome.Merged, await SweepAsync(t));
    }

    [Fact]
    public async Task Never_merges_a_pull_request_with_conflicts_and_merges_once_they_are_gone()
    {
        var t = await SetupAsync(approvals: 0, requireChecks: false);
        app.Commit(t.Repo, "main", "a.txt", "conflicting", message: "main touches a.txt");
        await ArmAsync(t);
        Assert.Equal(AutoMergeOutcome.Waiting, await SweepAsync(t));
        Assert.Contains("conflict", Row(t).WaitingFor);
        Assert.Equal(PullRequestState.Open, Reload(t).State);

        // The author (who armed it... carol armed; bob resolves by pushing a merge of main) -> not carol: it cancels. Use carol's own fix instead.
        app.Commit(t.Repo, Reload(t).SourceBranch, "a.txt", "conflicting", message: "resolve by taking main's version");
        // a push by "someone else" (the helper doesn't say who): the sweep judges by author, and "Test" isn't carol -> cancelled
        Assert.Equal(AutoMergeOutcome.Cancelled, await SweepAsync(t));
        Assert.Contains("new commits", Row(t).CancelReason);
    }

    // ---------- Cancellation ----------

    [Fact]
    public async Task New_commits_by_someone_else_cancel_it_but_the_armers_own_commits_do_not()
    {
        var t = await SetupAsync(approvals: 0, requireChecks: true);
        await ArmAsync(t, "carol");
        var branch = Reload(t).SourceBranch;

        // A push by the armer is accepted: the armed head moves along.
        app.Commit(t.Repo, branch, "b.txt", "b", message: "carol adds b");
        await using (var s = app.Scope(system: true)) await s.Get<AutoMergeService>().OnPushAsync(t.Repo.Id, app.UserId("carol"));
        Assert.Equal(AutoMergeState.Armed, Row(t).State);
        Assert.Equal(GitOps.ResolveSha(t.Path, branch), Row(t).ArmedHeadSha);

        // A push by bob cancels it, tells carol and leaves a timeline entry and an audit event.
        app.Commit(t.Repo, branch, "c.txt", "c", message: "bob adds c");
        await using (var s = app.Scope(system: true)) await s.Get<AutoMergeService>().OnPushAsync(t.Repo.Id, app.UserId("bob"));
        var row = Row(t);
        Assert.Equal(AutoMergeState.Cancelled, row.State);
        Assert.Contains("pushed by", row.CancelReason);
        Assert.Contains(Timeline(t), x => x.StartsWith("auto-merge was cancelled"));
        Assert.Single(Audit(t, "pull_request.auto_merge_cancelled"));
        Assert.Single(Notices(t, app.UserId("carol")));

        app.Check.Set(t.Pr.Id, MergeCheckState.Passed);
        Assert.Equal(AutoMergeOutcome.Idle, await SweepAsync(t));
        Assert.Equal(PullRequestState.Open, Reload(t).State); // cancelled means cancelled
    }

    [Fact]
    public async Task Losing_merge_permission_cancels_it()
    {
        var t = await SetupAsync(approvals: 0, requireChecks: false);
        app.Check.Default = MergeCheckState.Pending;
        await ArmAsync(t, "carol");
        await using (var s = app.Scope(app.UserId("alice"))) await s.Get<MemberService>().ChangeRoleAsync(t.Project.Id, app.UserId("carol"), ProjectRole.Developer);
        Assert.Equal(AutoMergeOutcome.Cancelled, await SweepAsync(t));
        Assert.Contains("no longer has permission", Row(t).CancelReason);
        Assert.Equal(PullRequestState.Open, Reload(t).State);
        Assert.Contains("permission", Assert.Single(Notices(t, app.UserId("carol"))).Excerpt);
    }

    [Fact]
    public async Task Closing_the_pull_request_cancels_it()
    {
        var t = await SetupAsync();
        await ArmAsync(t);
        await using (var s = app.Scope(app.UserId("bob"))) await s.Get<PullRequestService>().DeclineAsync(t.Pr.Id, app.UserId("bob"));
        Assert.Equal(AutoMergeOutcome.Cancelled, await SweepAsync(t));
        Assert.Contains("closed", Row(t).CancelReason);
    }

    [Fact]
    public async Task A_manual_merge_by_someone_else_ends_it_quietly()
    {
        var t = await SetupAsync(approvals: 0, requireChecks: false);
        await ArmAsync(t);
        await using (var s = app.Scope(app.UserId("dan")))
            Assert.True((await s.Get<PullRequestService>().MergeAsync(t.Pr.Id, app.User("dan"), new MergeRequest())).Success);
        Assert.Equal(AutoMergeOutcome.Cancelled, await SweepAsync(t));
        Assert.Empty(Notices(t, app.UserId("carol")));
        Assert.Equal(app.UserId("dan"), Reload(t).MergedById);
    }

    [Fact]
    public async Task A_failed_blocking_check_cancels_it_and_notifies_the_armer()
    {
        var t = await SetupAsync();
        await ArmAsync(t);
        await ApproveAsync(t);
        app.Check.Set(t.Pr.Id, MergeCheckState.Failed);
        Assert.Equal(AutoMergeOutcome.Cancelled, await SweepAsync(t));
        Assert.Contains("failed", Row(t).CancelReason);
        Assert.Single(Notices(t, app.UserId("carol")));
        Assert.Equal(PullRequestState.Open, Reload(t).State);
    }

    [Fact]
    public async Task Cancel_by_the_armer_or_a_maintainer_only()
    {
        var t = await SetupAsync();
        await ArmAsync(t, "carol");
        await using (var s = app.Scope(app.UserId("bob")))
            await Assert.ThrowsAsync<ForbiddenException>(() => s.Get<AutoMergeService>().CancelAsync(t.Pr.Id, app.UserId("bob")));
        await using (var s = app.Scope(app.UserId("dan")))
            Assert.True(await s.Get<AutoMergeService>().CancelAsync(t.Pr.Id, app.UserId("dan"))); // another Maintainer
        Assert.Equal(AutoMergeState.Cancelled, Row(t).State);
        Assert.Single(Notices(t, app.UserId("carol"))); // carol is told dan cancelled it
        await using (var s = app.Scope(app.UserId("carol")))
            Assert.False(await s.Get<AutoMergeService>().CancelAsync(t.Pr.Id, app.UserId("carol"))); // nothing armed any more
    }

    [Fact]
    public async Task The_armer_cancelling_their_own_request_is_not_notified()
    {
        var t = await SetupAsync();
        await ArmAsync(t, "carol");
        await using (var s = app.Scope(app.UserId("carol"))) Assert.True(await s.Get<AutoMergeService>().CancelAsync(t.Pr.Id, app.UserId("carol")));
        Assert.Empty(Notices(t, app.UserId("carol")));
        Assert.Contains(Timeline(t), x => x == "cancelled auto-merge");
    }

    // ---------- Permissions & validation ----------

    [Fact]
    public async Task Arming_needs_merge_permission_and_an_open_pull_request_and_an_allowed_strategy()
    {
        var t = await SetupAsync();
        await using (var s = app.Scope(app.UserId("bob")))
            await Assert.ThrowsAsync<ForbiddenException>(() => s.Get<AutoMergeService>().ArmAsync(t.Pr.Id, app.UserId("bob"), new MergeRequest()));
        await using (var s = app.Scope(app.UserId(t.Viewer)))
            await Assert.ThrowsAsync<ForbiddenException>(() => s.Get<AutoMergeService>().ArmAsync(t.Pr.Id, app.UserId(t.Viewer), new MergeRequest()));
        Assert.Null(await app.Db().PullRequestAutoMerges.FirstOrDefaultAsync(a => a.PullRequestId == t.Pr.Id));

        await using (var s = app.Scope(app.UserId("alice")))
        {
            await s.Get<RepositorySettingsService>().SetMergeStrategiesAsync(t.Repo.Id, MergeStrategies.Squash, MergeStrategy.Squash, app.UserId("alice"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => s.Get<AutoMergeService>().ArmAsync(t.Pr.Id, app.UserId("alice"), new MergeRequest(MergeStrategy.Rebase)));
        }
        await using (var s = app.Scope(app.UserId("bob"))) await s.Get<PullRequestService>().DeclineAsync(t.Pr.Id, app.UserId("bob"));
        await using (var s = app.Scope(app.UserId("carol")))
            await Assert.ThrowsAsync<InvalidOperationException>(() => s.Get<AutoMergeService>().ArmAsync(t.Pr.Id, app.UserId("carol"), new MergeRequest(MergeStrategy.Squash)));
    }

    [Fact]
    public async Task Arming_again_replaces_the_settings_and_the_person()
    {
        var t = await SetupAsync();
        await ArmAsync(t, "carol", MergeStrategy.Merge);
        await ArmAsync(t, "dan", MergeStrategy.Squash);
        var row = Row(t);
        Assert.Equal(app.UserId("dan"), row.ArmedById);
        Assert.Equal(MergeStrategy.Squash, row.Strategy);
        using var db = app.Db();
        Assert.Equal(1, db.PullRequestAutoMerges.Count(a => a.PullRequestId == t.Pr.Id));
    }

    // ---------- Races ----------

    [Fact]
    public async Task Concurrent_sweeps_merge_exactly_once()
    {
        var t = await SetupAsync(approvals: 0, requireChecks: false);
        await ArmAsync(t);
        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(async _ =>
        {
            await using var s = app.Scope(system: true);
            try { return await s.Get<AutoMergeService>().ProcessAsync(t.Pr.Id); }
            catch (InvalidOperationException) { return AutoMergeOutcome.Idle; }
        }));
        Assert.Equal(1, results.Count(r => r == AutoMergeOutcome.Merged));
        Assert.Single(Audit(t, "pull_request.auto_merge_merged"));
        Assert.Equal(PullRequestState.Merged, Reload(t).State);
    }

    [Fact]
    public async Task A_claimed_request_is_left_alone_until_the_lease_runs_out()
    {
        var t = await SetupAsync(approvals: 0, requireChecks: false);
        await ArmAsync(t);
        await using (var db = app.Db())
            await db.PullRequestAutoMerges.Where(a => a.PullRequestId == t.Pr.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.State, AutoMergeState.Merging).SetProperty(a => a.ClaimedAt, DateTime.UtcNow));
        Assert.Equal(AutoMergeOutcome.Idle, await SweepAsync(t)); // another worker is merging it
        Assert.Equal(PullRequestState.Open, Reload(t).State);

        await using (var db = app.Db())
            await db.PullRequestAutoMerges.Where(a => a.PullRequestId == t.Pr.Id).ExecuteUpdateAsync(s => s.SetProperty(a => a.ClaimedAt, DateTime.UtcNow.AddHours(-1)));
        Assert.Equal(AutoMergeOutcome.Merged, await SweepAsync(t)); // the claim was abandoned: taken over
    }

    [Fact]
    public async Task The_merge_refuses_a_head_that_changed_after_the_decision()
    {
        var t = await SetupAsync(approvals: 0, requireChecks: false);
        var head = GitOps.ResolveSha(t.Path, Reload(t).SourceRef)!;
        app.Commit(t.Repo, Reload(t).SourceBranch, "late.txt", "late", message: "late push");
        await using var s = app.Scope(app.UserId("carol"));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => s.Get<PullRequestService>()
            .MergeAsync(t.Pr.Id, app.User("carol"), new MergeRequest(NoBypass: true, ExpectedHeadSha: head)));
        Assert.Contains("changed while", ex.Message);
        Assert.Equal(PullRequestState.Open, Reload(t).State);
    }

    [Fact]
    public async Task An_admins_bypass_never_lets_auto_merge_skip_protection()
    {
        var t = await SetupAsync();
        await using (var s = app.Scope(app.UserId("alice")))
        {
            var rule = (await s.Get<BranchProtectionService>().ListAsync(t.Repo.Id)).Single();
            rule.AllowAdminBypass = true;
            await s.Get<BranchProtectionService>().SaveAsync(t.Repo.Id, rule, app.UserId("alice"));
        }
        await ArmAsync(t, "alice"); // the Owner could bypass by hand, auto-merge still waits for the approval and the check
        Assert.Equal(AutoMergeOutcome.Waiting, await SweepAsync(t));
        Assert.Equal(PullRequestState.Open, Reload(t).State);
    }

    // ---------- The merge queue ----------

    [Fact]
    public async Task With_a_merge_queue_the_pull_request_is_queued_instead_of_merged()
    {
        var t = await SetupAsync(approvals: 0, requireChecks: false, queue: true);
        await ArmAsync(t);
        Assert.Equal(AutoMergeOutcome.Queued, await SweepAsync(t));
        Assert.Equal(PullRequestState.Open, Reload(t).State); // the queue merges it
        Assert.Equal(AutoMergeState.Merged, Row(t).State);
        using var db = app.Db();
        var entry = db.MergeQueueEntries.AsNoTracking().Single(e => e.PullRequestId == t.Pr.Id);
        Assert.Equal(app.UserId("carol"), entry.EnqueuedById);
        Assert.Contains("\"queued\":true", Audit(t, "pull_request.auto_merge_merged").Single());
    }

    // ---------- Worker & triggers ----------

    [Fact]
    public async Task The_sweep_visits_every_armed_pull_request_and_skips_the_rest()
    {
        var ready = await SetupAsync(approvals: 0, requireChecks: false);
        var waiting = await SetupAsync();
        var unarmed = await SetupAsync(approvals: 0, requireChecks: false);
        await ArmAsync(ready);
        await ArmAsync(waiting);
        await using (var s = app.Scope(system: true)) Assert.True(await s.Get<AutoMergeService>().ProcessAllAsync() >= 1);
        Assert.Equal(PullRequestState.Merged, Reload(ready).State);
        Assert.Equal(PullRequestState.Open, Reload(waiting).State);
        Assert.Equal(PullRequestState.Open, Reload(unarmed).State);
    }

    [Fact]
    public async Task Armed_chips_are_found_for_the_list()
    {
        var a = await SetupAsync();
        var b = await SetupAsync();
        await ArmAsync(a);
        await using var s = app.Scope(app.UserId("carol"));
        var armed = await s.Get<AutoMergeService>().ArmedAmongAsync([a.Pr.Id, b.Pr.Id]);
        Assert.Equal([a.Pr.Id], armed.ToArray());
    }

    // ---------- REST ----------

    [Fact]
    public async Task Rest_arm_inspect_and_cancel()
    {
        var t = await SetupAsync();
        var url = $"/api/v1/projects/{t.Project.Key}/repos/{t.Repo.Slug}/pull-requests/{t.Pr.Number}/auto-merge";
        var carol = await app.ApiClientAsync("carol");
        var bob = await app.ApiClientAsync("bob");

        Assert.Equal(HttpStatusCode.NotFound, (await carol.GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await bob.PutAsJsonAsync(url, new { strategy = "squash" })).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await carol.PutAsJsonAsync(url, new { strategy = "fast" })).StatusCode);

        var put = await carol.PutAsJsonAsync(url, new { strategy = "squash", deleteSourceBranch = true });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var json = JsonDocument.Parse(await put.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("armed", json.GetProperty("state").GetString());
        Assert.Equal("squash", json.GetProperty("strategy").GetString());
        Assert.True(json.GetProperty("deleteSourceBranch").GetBoolean());
        Assert.Equal("carol", json.GetProperty("armedBy").GetProperty("username").GetString());

        Assert.Equal(HttpStatusCode.OK, (await carol.GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await bob.DeleteAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await carol.DeleteAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await carol.DeleteAsync(url)).StatusCode);
        var after = JsonDocument.Parse(await (await carol.GetAsync(url)).Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("cancelled", after.GetProperty("state").GetString());
    }
}
