using ByteBench.Data;
using ByteBench.Services;
using ByteBench.Services.Workbench;
using ByteBench.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ByteBench.Tests;

/// <summary>A site wired to a fake Workbench API (no background workers: tests drive the push and sync themselves).</summary>
public class WbApp : TestApp
{
    public FakeWorkbench Fake { get; } = new();

    protected override void AddSettings(IDictionary<string, string?> settings)
    {
        settings["Workbench:BaseUrl"] = Fake.Url;
        settings["Workbench:PushWorkerEnabled"] = "false";
        settings["Workbench:TimeoutSeconds"] = "1";
        settings["Workbench:MaxTasksPerSync"] = "50";
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) Fake.Dispose();
    }

    /// <summary>Connects the fake as the site admin and resets the fake to its defaults.</summary>
    public async Task ConnectAsync()
    {
        Fake.Scopes = ["projects:read", "sheets:read", "sheets:write"];
        (Fake.FinancialsAllowed, Fake.Delay, Fake.FailStatusWrites) = (true, 0, 0);
        Fake.Tasks = FakeWorkbench.DefaultPlan();
        Fake.State.Clear();
        await using var s = Scope(UserId("alice"));
        await s.Get<WorkbenchConnectionService>().SaveAsync(null, FakeWorkbench.Key);
        Services.GetRequiredService<WorkbenchDirectory>().Clear();
    }

    public async Task<Project> MappedProjectAsync(string? wbs = null)
    {
        var project = await CreateProjectAsync("alice", ProjectVisibility.Private, ("bob", ProjectRole.Developer), ("carol", ProjectRole.Maintainer), ("dan", ProjectRole.Viewer));
        await using var s = Scope(UserId("carol"));
        await s.Get<WorkbenchService>().SetProjectMappingAsync(project.Id, wbs, "WB-1");
        return project;
    }
}

public class WorkbenchConnectionTests(WbApp app) : IClassFixture<WbApp>
{
    [Fact]
    public async Task Admin_saves_the_key_encrypted_and_never_gets_it_back_and_it_is_audited()
    {
        await using (var bob = app.Scope(app.UserId("bob")))
            await Assert.ThrowsAsync<ForbiddenException>(() => bob.Get<WorkbenchConnectionService>().SaveAsync(null, FakeWorkbench.Key));
        await app.ConnectAsync();
        await using var s = app.Scope(app.UserId("alice"));
        var svc = s.Get<WorkbenchConnectionService>();
        var info = await svc.GetAsync();
        Assert.True(info.HasKey);
        Assert.Equal(FakeWorkbench.Key[^4..], info.KeyHint);
        Assert.DoesNotContain(FakeWorkbench.Key, System.Text.Json.JsonSerializer.Serialize(info));

        await using var db = app.Db();
        var row = await db.WorkbenchConnections.AsNoTracking().SingleAsync();
        Assert.DoesNotContain("fakekey", row.ApiKeyProtected);
        var audit = await db.AuditEvents.Where(e => e.Action.StartsWith("workbench.")).ToListAsync();
        Assert.Contains(audit, e => e.Action == "workbench.connection_changed");
        Assert.DoesNotContain(audit, e => (e.Details ?? "").Contains("fakekey"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.SaveAsync(null, "not-a-key"));
        await svc.RemoveKeyAsync();
        Assert.False((await svc.GetAsync()).HasKey);
        await app.ConnectAsync();
    }

    [Fact]
    public async Task Test_connection_reports_what_the_key_can_do_by_probing()
    {
        await app.ConnectAsync();
        await using var s = app.Scope(app.UserId("alice"));
        var svc = s.Get<WorkbenchConnectionService>();
        var ok = await svc.TestAsync();
        Assert.True(ok.Ok);
        var sheets = ok.Probes.Single(p => p.Area == "sheets");
        Assert.True(sheets.CanRead);
        Assert.True(sheets.CanWrite);
        Assert.False(ok.Probes.Single(p => p.Area == "time").CanRead);
        Assert.Empty(app.Fake.State); // the write probe changed nothing

        app.Fake.Scopes = ["projects:read", "sheets:read", "time:read"];
        var ro = await svc.TestAsync();
        Assert.False(ro.Probes.Single(p => p.Area == "sheets").CanWrite);
        Assert.True(ro.Probes.Single(p => p.Area == "time").CanRead);
        Assert.Contains("never", ro.Probes.Single(p => p.Area == "time").Purpose);
        Assert.NotNull(ro.Probes.Single(p => p.Area == "time").Note);

        app.Fake.Scopes = ["projects:read"];
        var narrow = await svc.TestAsync();
        Assert.False(narrow.Ok);
        Assert.Contains("sheets", narrow.Summary);
        Assert.Equal(narrow.At, (await svc.GetAsync()).LastTest!.At);
        await app.ConnectAsync();
    }

    [Fact]
    public async Task A_wrong_key_and_a_dead_server_are_reported_not_thrown()
    {
        await app.ConnectAsync();
        await using var s = app.Scope(app.UserId("alice"));
        var svc = s.Get<WorkbenchConnectionService>();
        await svc.SaveAsync(null, "wbk_wrongwrongwrong1");
        var bad = await svc.TestAsync();
        Assert.False(bad.Ok);
        Assert.Contains("rejected", bad.Summary);
        await svc.SaveAsync("http://127.0.0.1:1", null);
        Assert.False((await svc.TestAsync()).Ok);
        await svc.SaveAsync("", FakeWorkbench.Key); // back to Workbench:BaseUrl
        Assert.True((await svc.TestAsync()).Ok);
    }
}

/// <summary>The SSRF guard with private networks off (the default outside Development).</summary>
public class WorkbenchSsrfApp : TestApp
{
    protected override void AddSettings(IDictionary<string, string?> settings)
    {
        settings["Workbench:AllowPrivateNetworks"] = "false";
        settings["Workbench:PushWorkerEnabled"] = "false";
    }
}

public class WorkbenchSsrfTests(WorkbenchSsrfApp app) : IClassFixture<WorkbenchSsrfApp>
{
    [Theory]
    [InlineData("http://127.0.0.1:5000")]
    [InlineData("http://localhost")]
    [InlineData("http://wb.localhost:5327")]
    [InlineData("http://169.254.169.254/latest")]
    [InlineData("http://10.1.2.3")]
    [InlineData("http://[::1]:8080")]
    [InlineData("ftp://workbench.example.com")]
    [InlineData("https://user:pw@workbench.example.com")]
    public async Task Private_and_odd_addresses_are_refused(string url)
    {
        await using var s = app.Scope(app.UserId("alice"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => s.Get<WorkbenchConnectionService>().SaveAsync(url, null));
    }

    [Fact]
    public async Task A_public_address_is_accepted_and_the_client_blocks_a_private_one_at_connect_time()
    {
        await using var s = app.Scope(app.UserId("alice"));
        await s.Get<WorkbenchConnectionService>().SaveAsync("https://workbench.example.com/", FakeWorkbench.Key);
        Assert.Equal("https://workbench.example.com", (await s.Get<WorkbenchConnectionService>().GetAsync()).EffectiveUrl);
        var guard = app.Services.GetRequiredService<WorkbenchUrlGuard>();
        await Assert.ThrowsAsync<WorkbenchBlockedException>(() => guard.ResolveAsync("127.0.0.1", CancellationToken.None));
        await s.Get<WorkbenchConnectionService>().RemoveKeyAsync();
    }
}

public class WorkbenchPickerAndContextTests(WbApp app) : IClassFixture<WbApp>
{
    [Fact]
    public async Task Pickers_list_search_and_validate_the_codes_and_free_text_works_when_not_connected()
    {
        await app.ConnectAsync();
        var dir = app.Services.GetRequiredService<WorkbenchDirectory>();
        var projects = (await dir.ListProjectsAsync()).Value!;
        Assert.Equal("WB-1", Assert.Single(WorkbenchDirectory.SearchProjects(projects, "bench")).Code);
        var wbs = (await dir.ListWbsAsync(projects[0].Id)).Value!;
        Assert.Equal(["1.1.1.1"], WorkbenchDirectory.SearchWbs(wbs, "draft").Select(w => w.Code));

        var project = await app.CreateProjectAsync("alice", ProjectVisibility.Private, ("carol", ProjectRole.Maintainer));
        await using (var s = app.Scope(app.UserId("carol")))
        {
            var wb = s.Get<WorkbenchService>();
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => wb.SetProjectMappingAsync(project.Id, "1.1.1.1", "NOPE"));
            Assert.Contains("NOPE", ex.Message);
            await Assert.ThrowsAsync<InvalidOperationException>(() => wb.SetProjectMappingAsync(project.Id, "9.9.9", "WB-1"));
            await wb.SetProjectMappingAsync(project.Id, "1.1.1.1", "WB-1");
        }

        await using (var s = app.Scope(app.UserId("alice")))
            await s.Get<WorkbenchConnectionService>().RemoveKeyAsync();
        await using (var s = app.Scope(app.UserId("carol")))
            await s.Get<WorkbenchService>().SetProjectMappingAsync(project.Id, "anything-typed", "NOPE"); // free text: nothing to check against
        await app.ConnectAsync();
    }

    [Fact]
    public async Task Context_shows_task_status_percent_dates_and_hours_and_hides_hours_on_403()
    {
        await app.ConnectAsync();
        app.Fake.State[4] = ("In Progress", 40);
        var project = await app.MappedProjectAsync("1.1.1.1");
        await using var s = app.Scope(app.UserId("dan")); // a Viewer
        var ctx = (await s.Get<WorkbenchContextService>().GetAsync(project.Id))!;
        Assert.True(ctx.Found);
        Assert.Equal(("Draft spec", "In Progress", 40m), (ctx.Title, ctx.Status, ctx.Percent));
        Assert.Equal(new DateOnly(2026, 1, 16), ctx.Finish);
        Assert.Equal((16m, 4m), (ctx.Hours!.PlannedHours, ctx.Hours.ActualHours));
        Assert.Contains("/projects/", ctx.ProjectUrl);

        app.Fake.FinancialsAllowed = false;
        app.Services.GetRequiredService<WorkbenchDirectory>().Clear();
        var hidden = (await s.Get<WorkbenchContextService>().GetAsync(project.Id))!;
        Assert.True(hidden.Found);
        Assert.Null(hidden.Hours);
        Assert.True(hidden.HoursHidden);
        app.Fake.FinancialsAllowed = true;
    }

    [Fact]
    public async Task Context_needs_view_access_and_is_cached()
    {
        await app.ConnectAsync();
        var project = await app.MappedProjectAsync("1.1.1.1");
        var outsider = await app.CreateUserAsync("wb_outsider");
        await using (var o = app.Scope(outsider.Id))
            await Assert.ThrowsAsync<ForbiddenException>(() => o.Get<WorkbenchContextService>().GetAsync(project.Id));
        await using var s = app.Scope(app.UserId("bob"));
        await s.Get<WorkbenchContextService>().GetAsync(project.Id);
        var before = app.Fake.Hits("/api/v1/sheets/");
        await s.Get<WorkbenchContextService>().GetAsync(project.Id);
        await s.Get<WorkbenchContextService>().GetAsync(project.Id);
        Assert.Equal(before, app.Fake.Hits("/api/v1/sheets/")); // served from the cache
    }

    [Fact]
    public async Task A_slow_workbench_gives_up_quickly_and_a_down_one_is_remembered()
    {
        await app.ConnectAsync();
        var project = await app.MappedProjectAsync("1.1.1.1");
        app.Services.GetRequiredService<WorkbenchDirectory>().Clear();
        app.Fake.Delay = 3000; // longer than Workbench:TimeoutSeconds (1)
        await using var s = app.Scope(app.UserId("bob"));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var ctx = (await s.Get<WorkbenchContextService>().GetAsync(project.Id))!;
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2.5), sw.Elapsed.ToString());
        Assert.False(ctx.Found);
        Assert.Equal(WbStatus.Unreachable, ctx.State);
        var hits = app.Fake.Requests.Count;
        sw.Restart();
        await s.Get<WorkbenchContextService>().GetAsync(project.Id); // negative-cached: no second wait
        Assert.True(sw.Elapsed < TimeSpan.FromMilliseconds(500));
        Assert.Equal(hits, app.Fake.Requests.Count);
        app.Fake.Delay = 0;
    }

    [Fact]
    public async Task Pages_render_without_waiting_for_workbench_and_never_show_the_key()
    {
        await app.ConnectAsync();
        var project = await app.MappedProjectAsync("1.1.1.1");
        var issue = await app.CreateIssueAsync(project.Id, "bob");
        app.Fake.Delay = 3000;
        var client = await app.CookieClientAsync("bob");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var page = await client.GetStringAsync($"/p/{project.Key}/issues/{issue.Number}");
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2.5), sw.Elapsed.ToString());
        Assert.Contains("Log time in Workbench", page);
        Assert.DoesNotContain(FakeWorkbench.Key, page);
        app.Fake.Delay = 0;
        var admin = await (await app.CookieClientAsync("alice")).GetStringAsync("/admin/workbench");
        Assert.DoesNotContain(FakeWorkbench.Key, admin);
        Assert.Equal(System.Net.HttpStatusCode.OK, (await client.GetAsync("/admin/workbench")).StatusCode); // renders the "only administrators" message
        Assert.DoesNotContain("wb-url", await client.GetStringAsync("/admin/workbench"));
    }

    [Fact]
    public async Task The_timesheet_link_opens_the_period_of_the_day_the_work_was_closed()
    {
        await app.ConnectAsync();
        var project = await app.MappedProjectAsync("1.1.1.1");
        var issue = await app.CreateIssueAsync(project.Id, "bob");
        await using var s = app.Scope(app.UserId("bob"));
        await s.Get<IssueService>().SetStatusAsync(issue.Id, IssueStatus.Done, app.UserId("bob"));
        await using var db = app.Db();
        var i = await db.Issues.Include(x => x.Project).SingleAsync(x => x.Id == issue.Id);
        var link = s.Get<WorkbenchService>().LinkFor(i, i.Project)!;
        Assert.EndsWith($"/time?date={DateOnly.FromDateTime(i.ClosedAt!.Value):yyyy-MM-dd}", link.TimesheetUrl);
    }
}

public class WorkbenchPushTests(WbApp app) : IClassFixture<WbApp>
{
    private async Task<(Project Project, Issue A, Issue B)> SetupAsync()
    {
        await app.ConnectAsync();
        var project = await app.MappedProjectAsync("1.1.1.1");
        var a = await app.CreateIssueAsync(project.Id, "bob", "first");
        var b = await app.CreateIssueAsync(project.Id, "bob", "second");
        return (project, a, b);
    }

    private static WorkbenchPushEngine Engine(WbApp app) => app.Services.GetRequiredService<WorkbenchPushEngine>();

    [Fact]
    public async Task Push_is_off_by_default_and_opt_in_is_maintainer_only_and_audited()
    {
        var (project, _, _) = await SetupAsync();
        Assert.Equal(0, await Engine(app).ReconcileAsync(project.Id));
        await using (var bob = app.Scope(app.UserId("bob")))
            await Assert.ThrowsAsync<ForbiddenException>(() => bob.Get<WorkbenchPushService>().SetEnabledAsync(project.Id, true));
        await using (var carol = app.Scope(app.UserId("carol")))
            await carol.Get<WorkbenchPushService>().SetEnabledAsync(project.Id, true);
        await using var db = app.Db();
        Assert.Equal(1, await db.AuditEvents.CountAsync(e => e.Action == "project.workbench_push_changed" && e.ProjectId == project.Id));
    }

    [Fact]
    public async Task Issue_state_becomes_percent_and_status_in_workbench_and_is_idempotent()
    {
        var (project, a, b) = await SetupAsync();
        await using (var carol = app.Scope(app.UserId("carol")))
            await carol.Get<WorkbenchPushService>().SetEnabledAsync(project.Id, true);
        await using (var bob = app.Scope(app.UserId("bob")))
            await bob.Get<IssueService>().SetStatusAsync(a.Id, IssueStatus.Done, app.UserId("bob"));

        Assert.Equal(1, await Engine(app).ReconcileAsync(project.Id));
        Assert.Equal(0, await Engine(app).ReconcileAsync(project.Id)); // same desired state: not queued twice
        Assert.Equal(1, await Engine(app).DeliverDueAsync());
        Assert.Equal(("In Progress", 50m), app.Fake.State[4]);

        Assert.Equal(0, await Engine(app).ReconcileAsync(project.Id)); // synced: nothing to do
        var writes = app.Fake.Requests.Count(r => r.Method == "POST" && r.Path.EndsWith("/status"));
        await Engine(app).RunOnceAsync();
        Assert.Equal(writes, app.Fake.Requests.Count(r => r.Method == "POST" && r.Path.EndsWith("/status")));

        await using (var bob = app.Scope(app.UserId("bob")))
            await bob.Get<IssueService>().SetStatusAsync(b.Id, IssueStatus.Done, app.UserId("bob"));
        await Engine(app).RunOnceAsync();
        Assert.Equal(("Done", 100m), app.Fake.State[4]);
        Assert.DoesNotContain(app.Fake.Requests, r => r.Path.Contains("/time")); // never touches time

        await using var db = app.Db();
        Assert.True(await db.AuditEvents.AnyAsync(e => e.Action == "workbench.push" && e.ProjectId == project.Id));
        Assert.DoesNotContain(await db.AuditEvents.Select(e => e.Details ?? "").ToListAsync(), d => d.Contains("fakekey"));
    }

    [Fact]
    public async Task Transient_failures_retry_with_backoff_then_succeed_and_refusals_fail_with_the_reason()
    {
        var (project, a, _) = await SetupAsync();
        await using (var carol = app.Scope(app.UserId("carol")))
            await carol.Get<WorkbenchPushService>().SetEnabledAsync(project.Id, true);
        await using (var bob = app.Scope(app.UserId("bob")))
            await bob.Get<IssueService>().SetStatusAsync(a.Id, IssueStatus.Done, app.UserId("bob"));
        app.Fake.FailStatusWrites = 1;
        await Engine(app).RunOnceAsync();
        await using (var db = app.Db())
        {
            var row = await db.WorkbenchOutbox.SingleAsync(o => o.ProjectId == project.Id);
            Assert.Equal((WorkbenchOutboxState.Pending, 1), (row.State, row.Attempts));
            Assert.True(row.NextAttemptAt > DateTime.UtcNow.AddSeconds(30));
            Assert.False(app.Fake.State.ContainsKey(4));
            Assert.Equal(0, await Engine(app).DeliverDueAsync()); // not due yet
            row.NextAttemptAt = DateTime.UtcNow.AddSeconds(-1);
            await db.SaveChangesAsync();
        }
        Assert.Equal(1, await Engine(app).DeliverDueAsync());
        await using (var db = app.Db())
            Assert.Equal((WorkbenchOutboxState.Done, 2), await db.WorkbenchOutbox.Where(o => o.ProjectId == project.Id).Select(o => new { o.State, o.Attempts }).ToListAsync().ContinueWith(t => (t.Result[0].State, t.Result[0].Attempts)));

        // A key without sheets:write is refused: failed at once, with an explanation, no retries.
        await using (var bob = app.Scope(app.UserId("bob")))
            await bob.Get<IssueService>().SetStatusAsync((await app.CreateIssueAsync(project.Id, "bob", "third")).Id, IssueStatus.Done, app.UserId("bob"));
        app.Fake.Scopes = ["projects:read", "sheets:read"];
        await Engine(app).RunOnceAsync();
        await using var db2 = app.Db();
        var failed = await db2.WorkbenchOutbox.Where(o => o.ProjectId == project.Id && o.State == WorkbenchOutboxState.Failed).SingleAsync();
        Assert.Contains("sheets:write", failed.LastError);
        Assert.Equal(1, failed.Attempts);
        Assert.True(await db2.AuditEvents.AnyAsync(e => e.Action == "workbench.push_failed"));
        app.Fake.Scopes = ["projects:read", "sheets:read", "sheets:write"];
    }

    [Fact]
    public async Task A_value_changed_in_workbench_is_replaced_with_a_conflict_note()
    {
        var (project, a, _) = await SetupAsync();
        await using (var carol = app.Scope(app.UserId("carol")))
            await carol.Get<WorkbenchPushService>().SetEnabledAsync(project.Id, true);
        await using (var bob = app.Scope(app.UserId("bob")))
            await bob.Get<IssueService>().SetStatusAsync(a.Id, IssueStatus.Done, app.UserId("bob"));
        await Engine(app).RunOnceAsync();
        app.Fake.State[4] = ("In Progress", 80); // someone edited it in Workbench
        await using (var bob = app.Scope(app.UserId("bob")))
            await bob.Get<IssueService>().SetStatusAsync((await app.CreateIssueAsync(project.Id, "bob", "extra")).Id, IssueStatus.Done, app.UserId("bob"));
        await Engine(app).RunOnceAsync();
        await using var db = app.Db();
        var row = await db.WorkbenchOutbox.Where(o => o.ProjectId == project.Id).OrderByDescending(o => o.Id).FirstAsync();
        Assert.Contains("80", row.Note);
        Assert.Contains("owned by ByteBench", row.Note);
        Assert.Equal(66.67m, app.Fake.State[4].Percent);
    }
}

public class WorkbenchPlanSyncTests(WbApp app) : IClassFixture<WbApp>
{
    private static WorkbenchPlanSyncService Svc(TestScope s) => s.Get<WorkbenchPlanSyncService>();

    private async Task<Project> SetupAsync()
    {
        await app.ConnectAsync();
        return await app.MappedProjectAsync();
    }

    private async Task<Dictionary<string, Issue>> IssuesAsync(int projectId)
    {
        await using var db = app.Db();
        return (await db.Issues.AsNoTracking().Where(i => i.ProjectId == projectId).ToListAsync()).ToDictionary(i => i.Title);
    }

    [Fact]
    public async Task Preview_writes_nothing_and_run_creates_epics_tasks_and_subtasks_idempotently()
    {
        var project = await SetupAsync();
        await using var s = app.Scope(app.UserId("carol"));
        var preview = await Svc(s).PreviewAsync(project.Id);
        Assert.Null(preview.Error);
        Assert.Equal((7, 0), (preview.ToCreate, preview.ToUpdate));
        Assert.Equal(["ghost@nowhere.test"], preview.UnmatchedPeople);
        Assert.Empty(await IssuesAsync(project.Id));

        var report = await Svc(s).RunNowAsync(project.Id);
        Assert.Null(report.Error);
        Assert.Equal((7, 0, 1), (report.Created, report.Updated, report.Links));
        var issues = await IssuesAsync(project.Id);
        Assert.Equal(IssueType.Epic, issues["Phase 1 Design"].Type);
        Assert.Equal(IssueType.Epic, issues["Phase 2 Build"].Type);
        Assert.Equal((IssueType.Task, issues["Phase 1 Design"].Id), (issues["Sub phase"].Type, issues["Sub phase"].ParentId!.Value));
        Assert.Equal((IssueType.Subtask, issues["Sub phase"].Id), (issues["Draft spec"].Type, issues["Draft spec"].ParentId!.Value));
        Assert.Equal(issues["Phase 2 Build"].Id, issues["Implement"].ParentId);
        var draft = issues["Draft spec"];
        Assert.Equal("1.1.1.1", draft.WorkbenchWbs);
        Assert.Equal(new DateTime(2026, 1, 16), draft.DueDate);
        Assert.Equal(new DateTime(2026, 1, 5), draft.StartDate);
        Assert.Equal(960, draft.OriginalEstimateMinutes);
        Assert.Equal(app.UserId("alice"), draft.AssigneeId);
        Assert.Contains("Write the first draft.", draft.Description);
        Assert.Contains(WorkbenchPlanMapper.BlockStart, draft.Description);
        Assert.Equal(WorkbenchBot.UserName, (await app.Db().Users.FindAsync(draft.ReporterId))!.UserName);

        await using var db = app.Db();
        Assert.True(await db.IssueLinks.AnyAsync(l => l.Type == IssueLinkType.Blocks && l.SourceIssueId == issues["Prepare kickoff"].Id && l.TargetIssueId == issues["Implement"].Id));

        var again = await Svc(s).RunNowAsync(project.Id);
        Assert.Equal((0, 0, 7, 0, 0), (again.Created, again.Updated, again.Unchanged, again.Flagged, again.Links));
        Assert.Equal(7, (await IssuesAsync(project.Id)).Count);
        Assert.Equal(7, await db.WorkbenchIssueLinks.CountAsync(l => l.ProjectId == project.Id));
        Assert.Contains(await db.AuditEvents.Where(e => e.ProjectId == project.Id).Select(e => e.Action).ToListAsync(), a => a == "workbench.plan_sync");
    }

    [Fact]
    public async Task Schedule_edits_are_put_back_noted_and_work_state_is_never_overwritten()
    {
        var project = await SetupAsync();
        await using var carol = app.Scope(app.UserId("carol"));
        await Svc(carol).RunNowAsync(project.Id);
        var draft = (await IssuesAsync(project.Id))["Draft spec"];

        await using (var bob = app.Scope(app.UserId("bob")))
        {
            var issues = bob.Get<IssueService>();
            await issues.SetStatusAsync(draft.Id, IssueStatus.InProgress, app.UserId("bob"));
            await issues.UpdateIssueAsync(draft.Id, app.UserId("bob"), (i, _) => { i.DueDate = new DateTime(2026, 6, 1); i.Title = "Renamed here"; return Task.FromResult<string?>("edited"); });
        }
        var report = await Svc(carol).RunNowAsync(project.Id);
        Assert.Equal(1, report.Updated);
        var after = (await IssuesAsync(project.Id))["Renamed here"];
        Assert.Equal(new DateTime(2026, 1, 16), after.DueDate); // put back
        Assert.Equal(IssueStatus.InProgress, after.Status);       // work state untouched
        Assert.Contains(report.Notes, n => n.Contains("Kept the title"));
        await using var db = app.Db();
        var activity = await db.Activities.Where(a => a.IssueId == draft.Id).Select(a => a.Text).ToListAsync();
        Assert.Contains(activity, t => t.StartsWith("Workbench sync: put back the due date"));

        // A title changed in Workbench lands in ByteBench; status still isn't touched.
        app.Fake.Tasks = app.Fake.Tasks.Select(t => t.N == 4 ? t with { Title = "Draft the spec", Finish = "2026-01-20" } : t).ToList();
        await Svc(carol).RunNowAsync(project.Id);
        var changed = (await IssuesAsync(project.Id))["Draft the spec"];
        Assert.Equal((new DateTime(2026, 1, 20), IssueStatus.InProgress), (changed.DueDate, changed.Status));
    }

    [Fact]
    public async Task A_task_removed_in_workbench_is_flagged_not_deleted_and_new_tasks_appear()
    {
        var project = await SetupAsync();
        await using var carol = app.Scope(app.UserId("carol"));
        await Svc(carol).RunNowAsync(project.Id);
        app.Fake.Tasks = app.Fake.Tasks.Where(t => t.N != 3).Append(new(8, 7, "Release", "1.2.2", "2026-04-01", "2026-04-10", 2)).ToList();
        var report = await Svc(carol).RunNowAsync(project.Id);
        Assert.Equal((1, 1), (report.Created, report.Flagged));
        var issues = await IssuesAsync(project.Id);
        Assert.True(issues.ContainsKey("Review"));
        Assert.Equal(issues["Phase 2 Build"].Id, issues["Release"].ParentId);
        Assert.Equal("Review", (await Svc(carol).FlaggedAsync(project.Id)).Single().Title);
        await using var db = app.Db();
        Assert.Contains(await db.Activities.Where(a => a.IssueId == issues["Review"].Id).Select(a => a.Text).ToListAsync(), t => t.Contains("no longer lists this task"));
        Assert.Equal(0, (await Svc(carol).RunNowAsync(project.Id)).Flagged); // flagged once, not every run
    }

    [Fact]
    public async Task Epic_levels_and_a_subtree_choose_what_is_synced_and_nested_epics_flatten_with_a_note()
    {
        var project = await SetupAsync();
        await using var carol = app.Scope(app.UserId("carol"));
        await Svc(carol).SaveSettingsAsync(project.Id, null, 2, false);
        var report = await Svc(carol).RunNowAsync(project.Id);
        Assert.Contains(report.Notes, n => n.Contains("flattened"));
        var issues = await IssuesAsync(project.Id);
        Assert.Equal(IssueType.Epic, issues["Sub phase"].Type);
        Assert.Null(issues["Sub phase"].ParentId);

        var p2 = await SetupAsync();
        await Svc(carol).SaveSettingsAsync(p2.Id, "1.1", 0, false);
        await Svc(carol).RunNowAsync(p2.Id);
        var sub = await IssuesAsync(p2.Id);
        Assert.False(sub.ContainsKey("Phase 1 Design"));
        Assert.False(sub.ContainsKey("Implement"));
        Assert.Equal(IssueType.Task, sub["Sub phase"].Type);
        Assert.Equal(IssueType.Subtask, sub["Draft spec"].Type);
    }

    [Fact]
    public async Task Limits_permissions_and_missing_wbs_are_enforced()
    {
        var project = await SetupAsync();
        await using (var bob = app.Scope(app.UserId("bob")))
        {
            await Assert.ThrowsAsync<ForbiddenException>(() => Svc(bob).PreviewAsync(project.Id));
            await Assert.ThrowsAsync<ForbiddenException>(() => Svc(bob).RunNowAsync(project.Id));
            await Assert.ThrowsAsync<ForbiddenException>(() => Svc(bob).QueueAsync(project.Id));
        }
        await using var carol = app.Scope(app.UserId("carol"));
        app.Fake.Tasks = Enumerable.Range(100, 60).Select(n => new FakeWorkbench.PlanTask(n, null, $"T{n}", $"9.{n}", null, null)).ToList();
        var preview = await Svc(carol).PreviewAsync(project.Id);
        Assert.Contains("limit is 50", preview.Error);
        var report = await Svc(carol).RunNowAsync(project.Id);
        Assert.Contains("limit is 50", report.Error);
        Assert.Empty(await IssuesAsync(project.Id));
        app.Fake.Tasks = FakeWorkbench.DefaultPlan();
        await Svc(carol).SaveSettingsAsync(project.Id, "7.7.7", 1, false);
        Assert.Contains("no WBS element 7.7.7", (await Svc(carol).PreviewAsync(project.Id)).Error);
    }

    [Fact]
    public async Task Queued_syncs_run_and_a_stale_running_one_is_resumed_by_the_same_idempotent_run()
    {
        var project = await SetupAsync();
        await using var carol = app.Scope(app.UserId("carol"));
        await Svc(carol).QueueAsync(project.Id);
        Assert.Equal(WorkbenchPlanSyncState.Queued, (await Svc(carol).GetAsync(project.Id)).State);
        await Svc(carol).RunAsync(project.Id);
        var s = await Svc(carol).GetAsync(project.Id);
        Assert.Equal((WorkbenchPlanSyncState.Idle, 7), (s.State, s.Total));
        Assert.Equal(7, WorkbenchPlanSyncService.ReportOf(s)!.Created);
        await Svc(carol).RunAsync(project.Id); // "resumed" after a restart: nothing duplicated
        Assert.Equal(7, (await IssuesAsync(project.Id)).Count);
    }
}
