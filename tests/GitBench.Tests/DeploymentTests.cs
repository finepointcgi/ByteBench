using System.Net;
using System.Text;
using System.Text.Json;
using GitBench.Data;
using GitBench.Services;
using GitBench.Services.Ci;
using GitBench.Services.Git;
using GitBench.Services.Webhooks;
using GitBench.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace GitBench.Tests;

/// <summary>Pure parts: <c>environment.action</c>, deploy freeze windows.</summary>
public class DeploymentUnitTests
{
    [Fact]
    public void Environment_action_stop_is_parsed_and_others_rejected()
    {
        var r = CiFile.Parse("""
            jobs:
              deploy: { environment: { name: review }, steps: [ { run: up } ] }
              teardown: { when: manual, environment: { name: review, action: stop }, steps: [ { run: down } ] }
            """);
        Assert.True(r.Ok, r.Error);
        Assert.False(r.Definition!.Jobs[0].Environment!.IsStop);
        Assert.True(r.Definition.Jobs[1].Environment!.IsStop);
        Assert.Equal("stop", r.Definition.Jobs[1].Environment!.Action);

        var bad = CiFile.Parse("""
            jobs:
              x: { environment: { name: review, action: destroy }, steps: [ { run: down } ] }
            """);
        Assert.False(bad.Ok);
        Assert.Contains("environment action", bad.Error);
    }

    [Fact]
    public void Freeze_windows_by_dates_and_by_cron()
    {
        var now = new DateTime(2026, 11, 21, 12, 0, 0, DateTimeKind.Utc); // a Saturday
        Assert.True(DeployFreezeService.IsActive(new DeployFreezeWindow { StartsAt = now.AddHours(-1), EndsAt = now.AddHours(1) }, now));
        Assert.False(DeployFreezeService.IsActive(new DeployFreezeWindow { StartsAt = now.AddHours(1) }, now));
        Assert.False(DeployFreezeService.IsActive(new DeployFreezeWindow { EndsAt = now }, now)); // the end is exclusive
        Assert.True(DeployFreezeService.IsActive(new DeployFreezeWindow { EndsAt = now.AddMinutes(1) }, now));

        var weekend = new DeployFreezeWindow { CronStart = "0 18 * * 5", CronEnd = "0 8 * * 1" };
        Assert.True(DeployFreezeService.IsActive(weekend, now));
        Assert.True(DeployFreezeService.IsActive(weekend, new DateTime(2026, 11, 20, 18, 0, 0, DateTimeKind.Utc))); // Friday 18:00 on the dot
        Assert.False(DeployFreezeService.IsActive(weekend, new DateTime(2026, 11, 20, 17, 59, 0, DateTimeKind.Utc)));
        Assert.False(DeployFreezeService.IsActive(weekend, new DateTime(2026, 11, 23, 8, 0, 0, DateTimeKind.Utc))); // Monday 08:00
        Assert.False(DeployFreezeService.IsActive(weekend, new DateTime(2026, 11, 25, 12, 0, 0, DateTimeKind.Utc)));
    }

    [Fact]
    public void Deployment_status_webhook_is_subscribable()
    {
        Assert.True(WebhookEventNames.IsSubscribable(WebhookEventNames.DeploymentStatus));
        Assert.Equal("deployment.status", WebhookEventNames.DeploymentStatus);
    }
}

/// <summary>Approvals, rollback, stop jobs, traceability and the environments API, against the database and git.</summary>
public class DeploymentServiceTests(CiNoDockerApp app) : IClassFixture<CiNoDockerApp>
{
    private static StringContent Json(object body) => new(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
    private static async Task<JsonElement> ReadJson(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;

    private CiDeploymentRecorder Recorder => (CiDeploymentRecorder)app.Services.GetService(typeof(CiDeploymentRecorder))!;

    private async Task<(Project Project, Repository Repo)> SetupAsync()
    {
        var project = await app.CreateProjectAsync("alice", ProjectVisibility.Private,
            ("bob", ProjectRole.Developer), ("carol", ProjectRole.Maintainer), ("dan", ProjectRole.Developer));
        var repo = await app.CreateRepoAsync(project.Id, "alice");
        return (project, repo);
    }

    /// <summary>A pipeline straight in the database: build passed, deploy waiting (or finished, with <paramref name="deployStatus"/>).</summary>
    private async Task<(Pipeline P, PipelineJob Deploy)> PipelineAsync(Repository repo, string sha, string env = "production",
        PipelineStatus deployStatus = PipelineStatus.Manual, bool requiresApproval = true, bool stop = false, int? pipelineId = null)
    {
        await using var db = app.Db();
        Pipeline p;
        if (pipelineId is { } id) p = await db.Pipelines.FirstAsync(x => x.Id == id);
        else
        {
            p = new Pipeline
            {
                RepositoryId = repo.Id, Ref = "main", CommitSha = sha, Status = deployStatus == PipelineStatus.Manual ? PipelineStatus.Manual : PipelineStatus.Passed,
                Trigger = PipelineTrigger.Push, TriggeredById = app.UserId("alice"),
            };
            var build = new PipelineJob { Name = "build", Image = "alpine:3.20", Status = PipelineStatus.Passed, Order = 0 };
            build.Steps.Add(new PipelineStep { Name = "b", Run = "true", Status = PipelineStatus.Passed });
            p.Jobs.Add(build);
            db.Pipelines.Add(p);
        }
        var deploy = new PipelineJob
        {
            Name = stop ? "teardown" : "deploy", Image = "alpine:3.20", Needs = "build", Order = stop ? 2 : 1, Status = deployStatus,
            IsManual = stop, EnvironmentName = env, RequiresApproval = requiresApproval, EnvironmentAction = stop ? "stop" : null,
            FinishedAt = deployStatus == PipelineStatus.Manual ? null : DateTime.UtcNow,
        };
        deploy.Steps.Add(new PipelineStep { Name = "d", Run = "true", Status = deployStatus == PipelineStatus.Manual ? PipelineStatus.Queued : deployStatus });
        p.Jobs.Add(deploy);
        await db.SaveChangesAsync();
        return (p, deploy);
    }

    private async Task<CiEnvironment> ProtectedEnvironmentAsync(Repository repo, int required, params string[] approvers)
    {
        await using var carol = app.Scope(app.UserId("carol"));
        var env = await carol.Get<CiEnvironmentService>().CreateAsync(repo, "production", true);
        await carol.Get<CiEnvironmentService>().SetApprovalRulesAsync(repo, env.Id, required, approvers.Select(app.UserId).ToList(), []);
        return env;
    }

    /// <summary>A deployment recorded as the runner would: started and finished.</summary>
    private async Task<int> DeployAsync(Pipeline p, PipelineJob job, string user, PipelineStatus result = PipelineStatus.Passed)
    {
        var id = await Recorder.StartAsync(p, job, app.UserId(user));
        await Recorder.FinishAsync(id!.Value, result);
        return id.Value;
    }

    [Fact]
    public async Task Protected_environments_need_the_required_approvals_from_listed_approvers()
    {
        var (project, repo) = await SetupAsync();
        await ProtectedEnvironmentAsync(repo, 2, "bob", "dan");
        var (p, deploy) = await PipelineAsync(repo, new string('a', 40));
        var waitingId = await Recorder.BlockAsync(p, deploy);
        Assert.NotNull(waitingId);
        Assert.Equal(waitingId, await Recorder.BlockAsync(p, deploy)); // once per run

        await using (var db = app.Db())
        {
            var d = await db.Deployments.AsNoTracking().FirstAsync(x => x.Id == waitingId);
            Assert.Equal(PipelineStatus.Manual, d.Status);
            // Both listed approvers are asked; the Maintainer who isn't listed isn't.
            var asked = await db.Notifications.Where(n => n.Kind == NotificationKind.DeploymentApprovalRequested && n.ProjectId == project.Id).Select(n => n.UserId).ToListAsync();
            Assert.Equal(new[] { app.UserId("bob"), app.UserId("dan") }.Order(), asked.Order());
        }

        // A Maintainer who isn't listed can't approve; neither can a Developer play the job.
        await using (var carol = app.Scope(app.UserId("carol")))
        {
            var ex = await Assert.ThrowsAsync<ForbiddenException>(() => carol.Get<PipelineService>().ApproveJobAsync(repo, p.Id, deploy.Id, null));
            Assert.Contains("protected environment 'production'", ex.Message);
            var gate = await carol.Get<PipelineService>().GetGateAsync(repo, deploy);
            Assert.False(gate!.CanApprove);
            Assert.Equal(2, gate.Required);
        }

        await using (var bob = app.Scope(app.UserId("bob")))
        {
            Assert.False(await bob.Get<PipelineService>().ApproveJobAsync(repo, p.Id, deploy.Id, "Looks good"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => bob.Get<PipelineService>().ApproveJobAsync(repo, p.Id, deploy.Id, null));
            // Playing the job is an approval too, and bob already gave his.
            await Assert.ThrowsAsync<InvalidOperationException>(() => bob.Get<PipelineService>().PlayJobAsync(repo, p.Id, deploy.Id));
        }
        await using (var db = app.Db())
            Assert.Equal(PipelineStatus.Manual, (await db.PipelineJobs.AsNoTracking().FirstAsync(j => j.Id == deploy.Id)).Status);

        // A deploy freeze blocks the second approval until it is removed.
        DeployFreezeWindow freeze;
        await using (var carol = app.Scope(app.UserId("carol")))
            freeze = await carol.Get<DeployFreezeService>().CreateAsync(project.Id, "Release week", null, null, DateTime.UtcNow.AddHours(-1), DateTime.UtcNow.AddHours(1));
        await using (var dan = app.Scope(app.UserId("dan")))
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => dan.Get<PipelineService>().ApproveJobAsync(repo, p.Id, deploy.Id, null));
            Assert.Contains("frozen", ex.Message);
            Assert.Contains("Release week", ex.Message);
        }
        await using (var bob = app.Scope(app.UserId("bob")))
            await Assert.ThrowsAsync<ForbiddenException>(() => bob.Get<DeployFreezeService>().DeleteAsync(project.Id, freeze.Id));
        await using (var carol = app.Scope(app.UserId("carol")))
            await carol.Get<DeployFreezeService>().DeleteAsync(project.Id, freeze.Id);

        await using (var dan = app.Scope(app.UserId("dan")))
            Assert.True(await dan.Get<PipelineService>().ApproveJobAsync(repo, p.Id, deploy.Id, "Ship it"));

        await using (var db = app.Db())
        {
            var job = await db.PipelineJobs.AsNoTracking().FirstAsync(j => j.Id == deploy.Id);
            Assert.Equal(app.UserId("dan"), job.PlayedById);
            Assert.NotNull(job.PlayedAt);
            var approvals = await db.DeploymentApprovals.AsNoTracking().Where(a => a.JobId == deploy.Id).OrderBy(a => a.Id).ToListAsync();
            Assert.Equal(["Looks good", "Ship it"], approvals.Select(a => a.Comment));
            Assert.All(approvals, a => Assert.Equal(waitingId, a.DeploymentId));
            Assert.Equal(2, await db.AuditEvents.CountAsync(e => e.Action == "deployment.approved" && e.ProjectId == project.Id));
            Assert.True(await db.AuditEvents.AnyAsync(e => e.Action == "deploy_freeze.created" && e.ProjectId == project.Id));
            Assert.True(await db.Notifications.AnyAsync(n => n.Kind == NotificationKind.DeploymentApproved && n.UserId == app.UserId("alice") && n.ProjectId == project.Id));
        }

        // When the runner starts a waiting job, its waiting deployment becomes the running one (a second run, so this
        // test's runner, which stops at the missing docker, can't race it).
        var (p2, deploy2) = await PipelineAsync(repo, new string('c', 40));
        var waiting2 = await Recorder.BlockAsync(p2, deploy2);
        Assert.Equal(waiting2, await Recorder.StartAsync(p2, deploy2, app.UserId("dan")));
        // A run that never starts (canceled pipeline, missing docker) ends its waiting deployment.
        var (p3, deploy3) = await PipelineAsync(repo, new string('d', 40));
        var waiting3 = await Recorder.BlockAsync(p3, deploy3);
        await Recorder.CancelWaitingAsync(deploy3.Id);
        await using (var db = app.Db())
            Assert.Equal(PipelineStatus.Canceled, (await db.Deployments.AsNoTracking().FirstAsync(x => x.Id == waiting3)).Status);
    }

    [Fact]
    public async Task Group_members_can_approve_and_one_rejection_cancels_the_deployment()
    {
        var (project, repo) = await SetupAsync();
        var env = await ProtectedEnvironmentAsync(repo, 1);
        await using (var db = app.Db())
        {
            var group = new Group { Name = "Release managers " + project.Key, Path = "rm-" + project.Key.ToLowerInvariant(), FullPath = "rm-" + project.Key.ToLowerInvariant() };
            group.Members.Add(new GroupMember { UserId = app.UserId("dan"), Role = ProjectRole.Developer });
            db.Groups.Add(group);
            await db.SaveChangesAsync();
            await using var carol = app.Scope(app.UserId("carol"));
            await carol.Get<CiEnvironmentService>().SetApprovalRulesAsync(repo, env.Id, 1, [], [group.Id]);
        }
        var (p, deploy) = await PipelineAsync(repo, new string('b', 40));
        var waitingId = await Recorder.BlockAsync(p, deploy);

        await using (var bob = app.Scope(app.UserId("bob")))
            await Assert.ThrowsAsync<ForbiddenException>(() => bob.Get<PipelineService>().RejectJobAsync(repo, p.Id, deploy.Id, "no"));
        await using (var dan = app.Scope(app.UserId("dan")))
        {
            var waiting = await dan.Get<CiEnvironmentService>().WaitingAsync(project.Id);
            Assert.True(Assert.Single(waiting).CanApprove);
            await dan.Get<PipelineService>().RejectJobAsync(repo, p.Id, deploy.Id, "Not during the incident");
            Assert.Empty(await dan.Get<CiEnvironmentService>().WaitingAsync(project.Id));
        }

        await using var check = app.Db();
        Assert.Equal(PipelineStatus.Canceled, (await check.PipelineJobs.AsNoTracking().FirstAsync(j => j.Id == deploy.Id)).Status);
        Assert.Equal(PipelineStatus.Canceled, (await check.Deployments.AsNoTracking().FirstAsync(d => d.Id == waitingId)).Status);
        var rejection = await check.DeploymentApprovals.AsNoTracking().SingleAsync(a => a.JobId == deploy.Id);
        Assert.False(rejection.Approved);
        Assert.Equal("Not during the incident", rejection.Comment);
        Assert.True(await check.AuditEvents.AnyAsync(e => e.Action == "deployment.rejected" && e.ProjectId == project.Id));
        Assert.True(await check.Notifications.AnyAsync(n => n.Kind == NotificationKind.DeploymentRejected && n.UserId == app.UserId("alice") && n.ProjectId == project.Id));
    }

    [Fact]
    public async Task Rollback_reruns_an_older_deployment_and_asks_for_approval_again_on_protected_environments()
    {
        var (_, repo) = await SetupAsync();
        var sha1 = app.Commit(repo, "main", "app.txt", "v1", message: "one");
        var sha2 = app.Commit(repo, "main", "app.txt", "v2", message: "two");
        var (p1, job1) = await PipelineAsync(repo, sha1, deployStatus: PipelineStatus.Passed, requiresApproval: false);
        var (p2, job2) = await PipelineAsync(repo, sha2, deployStatus: PipelineStatus.Passed, requiresApproval: false);
        var d1 = await DeployAsync(p1, job1, "bob");
        var d2 = await DeployAsync(p2, job2, "bob");
        var failed = await DeployAsync(p2, job2, "bob", PipelineStatus.Failed);

        await using (var bob = app.Scope(app.UserId("bob")))
        {
            var envs = bob.Get<CiEnvironmentService>();
            Assert.Equal(d2, Assert.Single(await envs.ListAsync(repo)).Current!.Id);
            var changes = await envs.ChangesAsync(repo, d2);
            Assert.Equal(d1, changes!.Previous!.Id);
            Assert.Equal([sha2], changes.Commits.Select(c => c.Sha));
            await Assert.ThrowsAsync<InvalidOperationException>(() => envs.RollbackAsync(repo, failed));

            // Unprotected: a Developer rolls back; the job of the old pipeline runs again right away.
            await envs.RollbackAsync(repo, d1);
        }
        await using (var db = app.Db())
            Assert.Equal(app.UserId("bob"), (await db.PipelineJobs.AsNoTracking().FirstAsync(j => j.Id == job1.Id)).PlayedById);

        var d3 = await DeployAsync(p1, job1, "bob");
        await using (var db = app.Db())
        {
            var row = await db.Deployments.AsNoTracking().FirstAsync(d => d.Id == d3);
            Assert.True(row.IsRollback);
            Assert.Equal(d1, row.RedeployOfId);
        }
        await using (var bob = app.Scope(app.UserId("bob")))
        {
            var envs = bob.Get<CiEnvironmentService>();
            var info = Assert.Single(await envs.ListAsync(repo));
            Assert.Equal(d3, info.Current!.Id);
            var back = await envs.ChangesAsync(repo, d3);
            Assert.True(back!.Backwards);
            Assert.Equal([sha2], back.Commits.Select(c => c.Sha));

            // Protect it with two approvals: a Developer can't roll back any more; a Maintainer's request is the first approval.
            await using var carol = app.Scope(app.UserId("carol"));
            await carol.Get<CiEnvironmentService>().SetProtectedAsync(repo, info.Environment.Id, true);
            await carol.Get<CiEnvironmentService>().SetApprovalRulesAsync(repo, info.Environment.Id, 2, [], []);
            await Assert.ThrowsAsync<ForbiddenException>(() => envs.RollbackAsync(repo, d2));
            await carol.Get<CiEnvironmentService>().RollbackAsync(repo, d2);
        }
        await using (var db = app.Db())
        {
            var job = await db.PipelineJobs.AsNoTracking().FirstAsync(j => j.Id == job2.Id);
            Assert.Null(job.PlayedAt); // waits for the second approval
            var current = await db.DeploymentApprovals.AsNoTracking().Where(a => a.JobId == job2.Id && !a.IsStale).ToListAsync();
            Assert.Equal(app.UserId("carol"), Assert.Single(current).UserId);
            Assert.True(await db.AuditEvents.AnyAsync(e => e.Action == "deployment.redeployed" && e.TargetId == d2.ToString()));
        }
    }

    [Fact]
    public async Task Stop_jobs_stop_the_environment_and_a_new_deployment_starts_it()
    {
        var (_, repo) = await SetupAsync();
        var sha = app.Commit(repo, "main", "app.txt", "v1");
        var (p, deploy) = await PipelineAsync(repo, sha, env: "review", deployStatus: PipelineStatus.Passed, requiresApproval: false);
        await DeployAsync(p, deploy, "bob");
        var (_, teardown) = await PipelineAsync(repo, sha, env: "review", requiresApproval: false, stop: true, pipelineId: p.Id);

        await using (var bob = app.Scope(app.UserId("bob")))
        {
            var envs = bob.Get<CiEnvironmentService>();
            var info = Assert.Single(await envs.ListAsync(repo));
            Assert.True(await envs.HasStopJobAsync(info));
            Assert.True(await envs.StopAsync(repo, info.Environment.Id));
        }
        await using (var db = app.Db())
            Assert.Equal(app.UserId("bob"), (await db.PipelineJobs.AsNoTracking().FirstAsync(j => j.Id == teardown.Id)).PlayedById);

        var stopId = await DeployAsync(p, teardown, "bob");
        await using (var bob = app.Scope(app.UserId("bob")))
        {
            var info = Assert.Single(await bob.Get<CiEnvironmentService>().ListAsync(repo));
            Assert.True(info.IsStopped);
            Assert.NotEqual(stopId, info.Current!.Id); // the current deployment is the last one that started it
        }
        await DeployAsync(p, deploy, "bob");
        await using (var bob = app.Scope(app.UserId("bob")))
            Assert.False(Assert.Single(await bob.Get<CiEnvironmentService>().ListAsync(repo)).IsStopped);
    }

    [Fact]
    public async Task Issues_pull_requests_and_releases_show_where_their_commits_are_deployed()
    {
        var (project, repo) = await SetupAsync();
        var key = app.ProjectKey(project.Id);
        var c1 = app.Commit(repo, "main", "a.txt", "1", message: $"{key}-1 first part");
        var c2 = app.Commit(repo, "main", "b.txt", "2", message: "unrelated");
        var c3 = app.Commit(repo, "main", "c.txt", "3", message: $"{key}-1 second part");
        var path = app.Services.GetService(typeof(RepoStorage)) is RepoStorage storage ? storage.PathFor(key, repo.Slug) : throw new InvalidOperationException();
        GitOps.CreateTag(path, "v1.0", c1, null, new GitIdentity("Test", "test@example.com"));

        var (p, staging) = await PipelineAsync(repo, c2, env: "staging", deployStatus: PipelineStatus.Passed, requiresApproval: false);
        var first = await DeployAsync(p, staging, "bob");
        var (p3, staging3) = await PipelineAsync(repo, c3, env: "staging", deployStatus: PipelineStatus.Passed, requiresApproval: false);
        var (_, prod) = await PipelineAsync(repo, c2, env: "production", deployStatus: PipelineStatus.Passed, requiresApproval: false, pipelineId: p.Id);
        await DeployAsync(p, prod, "carol");

        await using (var db = app.Db())
        {
            db.PullRequests.Add(new PullRequest
            {
                RepositoryId = repo.Id, Number = 1, Title = "Third part", SourceBranch = "feature/x", TargetBranch = "main",
                AuthorId = app.UserId("bob"), State = PullRequestState.Merged, MergeCommitSha = c3,
            });
            await db.SaveChangesAsync();
        }

        await using var s = app.Scope(app.UserId("bob"));
        var trace = s.Get<DeploymentTraceService>();

        // Issue KEY-1: c1 and c3 mention it. Both environments have c1 but not c3.
        var issue = await trace.ForIssueAsync(project.Id, key, 1);
        Assert.Equal(["production", "staging"], issue.Select(t => t.Environment.Name).Order());
        Assert.All(issue, t => { Assert.Equal(1, t.Included); Assert.Equal(2, t.Total); Assert.False(t.All); });
        Assert.Equal(first, issue.Single(t => t.Environment.Name == "staging").FirstDeployment!.Id);

        // The PR's merge commit (c3) isn't deployed yet; then staging gets it.
        var pr = await s.Get<PullRequestService>().GetAsync(key, repo.Slug, 1);
        Assert.Empty(await trace.ForPullRequestAsync(repo, pr!));
        await DeployAsync(p3, staging3, "bob");
        var prTrace = Assert.Single(await trace.ForPullRequestAsync(repo, pr!));
        Assert.Equal("staging", prTrace.Environment.Name);
        Assert.True(prTrace.All);
        Assert.True((await trace.ForIssueAsync(project.Id, key, 1)).Single(t => t.Environment.Name == "staging").All);

        // The release tag (at c1) is in both; a commit nobody deployed is nowhere.
        Assert.Equal(2, (await trace.ForTagAsync(repo, "v1.0")).Count);
        Assert.Empty(await trace.ForTagAsync(repo, "no-such-tag"));
        Assert.Empty(await trace.ForIssueAsync(project.Id, key, 99));

        // The version of a deployed commit: the nearest tag and the distance.
        Assert.Equal("v1.0+1", (await s.Get<CiEnvironmentService>().VersionAsync(repo, c2))!.ToString());
        Assert.Equal("v1.0", (await s.Get<CiEnvironmentService>().VersionAsync(repo, c1))!.ToString());

        // Someone outside the project sees nothing.
        var outsider = await app.CreateUserAsync("tr" + Guid.NewGuid().ToString("N")[..6]);
        await using var o = app.Scope(outsider.Id);
        Assert.Empty(await o.Get<DeploymentTraceService>().ForIssueAsync(project.Id, key, 1));
    }

    [Fact]
    public async Task Api_lists_environments_and_approves_rolls_back_and_traces_deployments()
    {
        var (project, repo) = await SetupAsync();
        var key = app.ProjectKey(project.Id);
        var issueRow = await app.CreateIssueAsync(project.Id, "alice", "API work");
        var sha = app.Commit(repo, "main", "a.txt", "1", message: $"{key}-{issueRow.Number} api work");
        await ProtectedEnvironmentAsync(repo, 1, "bob");
        var (p, deploy) = await PipelineAsync(repo, sha);
        var waitingId = await Recorder.BlockAsync(p, deploy);

        var bob = await app.ApiClientAsync("bob");
        var carol = await app.ApiClientAsync("carol");
        var dan = await app.ApiClientAsync("dan");

        var list = await ReadJson(await bob.GetAsync($"/api/v1/projects/{key}/environments"));
        var env = list.EnumerateArray().Single();
        Assert.Equal("production", env.GetProperty("name").GetString());
        Assert.True(env.GetProperty("isProtected").GetBoolean());
        Assert.Equal("bob", env.GetProperty("approvers")[0].GetProperty("user").GetProperty("username").GetString());
        Assert.Equal("waiting_for_approval", env.GetProperty("latest").GetProperty("status").GetString());

        var waiting = await ReadJson(await bob.GetAsync($"/api/v1/projects/{key}/deployments/waiting"));
        Assert.Equal(waitingId, waiting[0].GetProperty("deployment").GetProperty("id").GetInt32());

        // Dan isn't an approver; bob approves with a comment.
        var denied = await dan.PostAsync($"/api/v1/projects/{key}/repos/{repo.Slug}/deployments/{waitingId}/approve", Json(new { comment = "me too" }));
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        var approved = await bob.PostAsync($"/api/v1/projects/{key}/repos/{repo.Slug}/deployments/{waitingId}/approve", Json(new { comment = "LGTM" }));
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
        var detail = await ReadJson(approved);
        Assert.Equal("approved", detail.GetProperty("approvals")[0].GetProperty("decision").GetString());
        Assert.Equal("LGTM", detail.GetProperty("approvals")[0].GetProperty("comment").GetString());
        var again = await bob.PostAsync($"/api/v1/projects/{key}/repos/{repo.Slug}/deployments/{waitingId}/approve", Json(new { }));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, again.StatusCode);

        // The runner deploys it (here: simulated; this test app's runner stops at the missing docker and ends the waiting row).
        var ran = await Recorder.StartAsync(p, deploy, app.UserId("bob"));
        await Recorder.FinishAsync(ran!.Value, PipelineStatus.Passed);
        waitingId = ran;

        var envId = env.GetProperty("id").GetInt32();
        var history = await ReadJson(await bob.GetAsync($"/api/v1/projects/{key}/repos/{repo.Slug}/environments/{envId}/deployments"));
        Assert.Equal("success", history[0].GetProperty("status").GetString());
        var one = await ReadJson(await bob.GetAsync($"/api/v1/projects/{key}/repos/{repo.Slug}/deployments/{waitingId}"));
        Assert.Equal(sha, one.GetProperty("commits")[0].GetProperty("sha").GetString());

        var deployed = await ReadJson(await bob.GetAsync($"/api/v1/projects/{key}/repos/{repo.Slug}/deployed?rev={sha}"));
        Assert.Equal("production", deployed[0].GetProperty("environment").GetString());
        var issue = await ReadJson(await bob.GetAsync($"/api/v1/projects/{key}/issues/{issueRow.Number}/deployments"));
        Assert.Equal("production", issue[0].GetProperty("environment").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await bob.GetAsync($"/api/v1/projects/{key}/issues/99999/deployments")).StatusCode);

        // Approval rules and freezes need Maintainer+.
        var rules = await bob.PutAsync($"/api/v1/projects/{key}/repos/{repo.Slug}/environments/{envId}/approval-rules", Json(new { requiredApprovals = 2 }));
        Assert.Equal(HttpStatusCode.Forbidden, rules.StatusCode);
        rules = await carol.PutAsync($"/api/v1/projects/{key}/repos/{repo.Slug}/environments/{envId}/approval-rules", Json(new { requiredApprovals = 2 }));
        Assert.Equal(HttpStatusCode.OK, rules.StatusCode);
        Assert.Equal(2, (await ReadJson(rules)).GetProperty("requiredApprovals").GetInt32());
        Assert.Equal(0, (await ReadJson(rules)).GetProperty("approvers").GetArrayLength());
        // The rule only bites on a protected environment, so the same call can protect it (an API caller has no other way to).
        rules = await carol.PutAsync($"/api/v1/projects/{key}/repos/{repo.Slug}/environments/{envId}/approval-rules", Json(new { requiredApprovals = 2, isProtected = false }));
        Assert.False((await ReadJson(rules)).GetProperty("isProtected").GetBoolean());
        rules = await carol.PutAsync($"/api/v1/projects/{key}/repos/{repo.Slug}/environments/{envId}/approval-rules", Json(new { requiredApprovals = 2, isProtected = true }));
        Assert.Equal(HttpStatusCode.OK, rules.StatusCode);
        Assert.True((await ReadJson(rules)).GetProperty("isProtected").GetBoolean());

        var freeze = await bob.PostAsync($"/api/v1/projects/{key}/deploy-freezes", Json(new { cronStart = "0 18 * * 5", cronEnd = "0 8 * * 1" }));
        Assert.Equal(HttpStatusCode.Forbidden, freeze.StatusCode);
        freeze = await carol.PostAsync($"/api/v1/projects/{key}/deploy-freezes", Json(new { description = "Weekends", cronStart = "0 18 * * 5", cronEnd = "0 8 * * 1" }));
        Assert.Equal(HttpStatusCode.Created, freeze.StatusCode);
        var bad = await carol.PostAsync($"/api/v1/projects/{key}/deploy-freezes", Json(new { cronStart = "nope", cronEnd = "0 8 * * 1" }));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, bad.StatusCode);
        Assert.Single((await ReadJson(await bob.GetAsync($"/api/v1/projects/{key}/deploy-freezes"))).EnumerateArray());

        // Rollback (redeploy) of the current deployment by a Maintainer: accepted, waits for a second approval.
        var rollback = await carol.PostAsync($"/api/v1/projects/{key}/repos/{repo.Slug}/deployments/{waitingId}/rollback", Json(new { }));
        if (DeployFreezeService.IsActive(new DeployFreezeWindow { CronStart = "0 18 * * 5", CronEnd = "0 8 * * 1" }, DateTime.UtcNow))
            Assert.Equal(HttpStatusCode.UnprocessableEntity, rollback.StatusCode); // it's the weekend: frozen
        else
            Assert.Equal(HttpStatusCode.Accepted, rollback.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await bob.GetAsync($"/api/v1/projects/{key}/repos/{repo.Slug}/deployments/999999")).StatusCode);
    }
}
