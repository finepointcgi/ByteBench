using System.Net;
using System.Text;
using System.Text.Json;
using System.Web;
using ByteBench.Data;
using ByteBench.Services;
using ByteBench.Services.Workspaces;
using ByteBench.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ByteBench.Tests;

public class WorkspaceUnitsTests
{
    [Theory]
    [InlineData("4g", 4096)]
    [InlineData("512m", 512)]
    [InlineData("10GB", 10240)]
    [InlineData("1t", 1048576)]
    [InlineData("2048k", 2)]
    [InlineData("1073741824", 1024)]
    public void Docker_sizes_become_megabytes(string text, int mb) => Assert.Equal(mb, WorkspaceUnits.ParseMb(text));

    [Theory]
    [InlineData("2", 2000)]
    [InlineData("0.5", 500)]
    [InlineData("1.25", 1250)]
    public void Cpus_become_thousandths(string text, int millis) => Assert.Equal(millis, WorkspaceUnits.ParseCpuMillis(text));

    [Fact]
    public void Nonsense_is_not_a_size_and_amounts_print_readably()
    {
        Assert.Null(WorkspaceUnits.ParseMb("lots"));
        Assert.Null(WorkspaceUnits.ParseMb(null));
        Assert.Null(WorkspaceUnits.ParseCpuMillis("0"));
        Assert.Null(WorkspaceUnits.ParseCpuMillis("-1"));
        Assert.Equal("2 CPUs", WorkspaceUnits.FormatCpu(2000));
        Assert.Equal("1 CPU", WorkspaceUnits.FormatCpu(1000));
        Assert.Equal("4 GB", WorkspaceUnits.FormatMb(4096));
        Assert.Equal("512 MB", WorkspaceUnits.FormatMb(512));
        Assert.Equal("1.5", WorkspaceUnits.CpuArg(1500));
    }
}

/// <summary>Nothing set by an administrator: the limits come from <c>Workspaces:*</c>, so an upgraded server behaves as before.</summary>
public class WorkspaceDefaultLimitsTests(WorkspaceApp app) : IClassFixture<WorkspaceApp>
{
    [Fact]
    public async Task Untouched_servers_keep_their_configured_limits()
    {
        await using var s = app.Scope(app.UserId("alice"));
        var l = await s.Get<WorkspaceLimitService>().GetAsync();
        Assert.Equal(2, l.MaxRunning); // Workspaces:MaxPerUser
        Assert.Equal(2, l.DefaultCpus);
        Assert.Equal(4096, l.DefaultMemoryMb);
        Assert.Equal(10240, l.DefaultDiskMb);
        Assert.Equal(0, l.MaxTotal);
        var (def, max) = await app.Services.GetRequiredService<WorkspaceIdlePolicy>().GetSiteAsync();
        Assert.Equal((30, 480), (def, max));
    }
}

public class WorkspaceLimitsTests(WorkspaceApp app) : IClassFixture<WorkspaceApp>
{
    private static WorkspaceLimitSettings Baseline() => new() { MaxRunning = 5, DefaultCpus = 2, DefaultMemoryMb = 4096, DefaultDiskMb = 10240 };

    private async Task SetLimitsAsync(WorkspaceLimitSettings s)
    {
        await using var scope = app.Scope(app.UserId("alice"));
        await scope.Get<WorkspaceLimitService>().SetAsync(s, app.UserId("alice"));
    }

    private async Task<(string User, Repository Repo)> UserAsync()
    {
        var user = await app.NewUserAsync();
        return (user, await app.RepoWithMembersAsync((user, ProjectRole.Developer)));
    }

    private async Task<string> StartErrorAsync(string user, Repository repo)
    {
        await using var s = app.Scope(app.UserId(user));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            s.Get<WorkspaceService>().StartAsync(app.ProjectKey(repo.ProjectId), repo.Slug, "main", null, app.UserId(user)));
        return ex.Message;
    }

    [Fact]
    public async Task The_running_cap_names_the_limit_and_frees_on_stop_and_other_users_are_unaffected()
    {
        var (user, repo) = await UserAsync();
        var (other, otherRepo) = await UserAsync();
        await SetLimitsAsync(new WorkspaceLimitSettings { MaxRunning = 1, DefaultCpus = 2, DefaultMemoryMb = 4096, DefaultDiskMb = 10240 });
        try
        {
            var first = await app.StartAsync(user, repo);
            var msg = await StartErrorAsync(user, repo);
            Assert.Equal("You already have 1 workspace running (the limit is 1). Stop one first.", msg);
            await app.StartAsync(other, otherRepo);
            await using (var s = app.Scope(app.UserId(user))) await s.Get<WorkspaceService>().StopAsync(first.PublicId, app.UserId(user));
            await app.StartAsync(user, repo);
        }
        finally { await SetLimitsAsync(Baseline()); }
    }

    [Fact]
    public async Task The_total_cpu_memory_and_disk_caps_are_enforced_with_their_own_messages()
    {
        var (user, repo) = await UserAsync();
        try
        {
            await SetLimitsAsync(new WorkspaceLimitSettings { MaxRunning = 0, MaxTotal = 1, DefaultCpus = 2, DefaultMemoryMb = 4096, DefaultDiskMb = 10240 });
            var ws = await app.StartAsync(user, repo);
            Assert.Equal("You already have 1 workspace (the limit is 1). Delete one first.", await StartErrorAsync(user, repo));

            await SetLimitsAsync(new WorkspaceLimitSettings { MaxRunning = 9, MaxCpus = 3, DefaultCpus = 2, DefaultMemoryMb = 4096, DefaultDiskMb = 10240 });
            Assert.Equal("A new workspace needs 2 CPUs but you are using 2 CPUs of your 3 CPU limit. Stop a workspace first.", await StartErrorAsync(user, repo));

            await SetLimitsAsync(new WorkspaceLimitSettings { MaxRunning = 9, MaxMemoryMb = 6144, DefaultCpus = 2, DefaultMemoryMb = 4096, DefaultDiskMb = 10240 });
            Assert.Equal("A new workspace needs 4 GB of memory but you are using 4 GB of your 6 GB limit. Stop a workspace first.", await StartErrorAsync(user, repo));

            await SetLimitsAsync(new WorkspaceLimitSettings { MaxRunning = 9, MaxDiskMb = 15360, DefaultCpus = 2, DefaultMemoryMb = 4096, DefaultDiskMb = 10240 });
            Assert.Equal("A new workspace needs 10 GB of disk but you are using 10 GB of your 15 GB limit. Delete a workspace first.", await StartErrorAsync(user, repo));

            var usage = await UsageAsync(user);
            Assert.Equal((1, 1, 2000, 4096, 10240), (usage.Running, usage.Total, usage.CpuMillis, usage.MemoryMb, usage.DiskMb));

            await SetLimitsAsync(Baseline()); // with room again the same user can start another
            Assert.Equal(WorkspaceState.Running, (await app.StartAsync(user, repo)).State);
            Assert.NotNull(ws);
        }
        finally { await SetLimitsAsync(Baseline()); }
    }

    private async Task<WorkspaceUsage> UsageAsync(string user)
    {
        await using var s = app.Scope(app.UserId(user));
        return await s.Get<WorkspaceLimitService>().GetUsageAsync(app.UserId(user));
    }

    [Fact]
    public async Task Workspaces_get_the_default_resources_recorded_and_changes_apply_to_the_next_start_only()
    {
        var (user, repo) = await UserAsync();
        try
        {
            await SetLimitsAsync(new WorkspaceLimitSettings { MaxRunning = 5, DefaultCpus = 1.5, DefaultMemoryMb = 1024, DefaultDiskMb = 2048 });
            var a = await app.StartAsync(user, repo);
            var spec = app.Backend.Started.Last(s => s.WorkspaceId == a.Id);
            Assert.Equal(new WorkspaceLimits("1024m", "1.5", 2048, "2048m"), spec.Limits);

            await SetLimitsAsync(new WorkspaceLimitSettings { MaxRunning = 5, DefaultCpus = 0.5, DefaultMemoryMb = 512, DefaultDiskMb = 0 });
            var b = await app.StartAsync(user, repo);
            Assert.Equal(new WorkspaceLimits("512m", "0.5", 2048, null), app.Backend.Started.Last(s => s.WorkspaceId == b.Id).Limits);

            var usage = await UsageAsync(user);
            Assert.Equal((2, 2000, 1536, 2048), (usage.Running, usage.CpuMillis, usage.MemoryMb, usage.DiskMb)); // each counted with what it was given
            await using var db = app.Db();
            Assert.Equal(1500, (await db.WorkspaceAllocations.FirstAsync(x => x.WorkspaceId == a.Id)).CpuMillis);
        }
        finally { await SetLimitsAsync(Baseline()); }
    }

    [Fact]
    public async Task Only_administrators_set_limits_and_nonsense_is_refused()
    {
        var user = await app.NewUserAsync();
        await using (var s = app.Scope(app.UserId(user)))
        {
            var svc = s.Get<WorkspaceLimitService>();
            await Assert.ThrowsAsync<ForbiddenException>(() => svc.SetAsync(Baseline(), app.UserId(user)));
            await Assert.ThrowsAsync<ForbiddenException>(() => svc.SetIdleAsync(10, 60, app.UserId(user)));
        }
        await using var admin = app.Scope(app.UserId("alice"));
        var limits = admin.Get<WorkspaceLimitService>();
        var id = app.UserId("alice");
        async Task<string> Refused(Action<WorkspaceLimitSettings> change)
        {
            var s = Baseline();
            change(s);
            return (await Assert.ThrowsAsync<InvalidOperationException>(() => limits.SetAsync(s, id))).Message;
        }
        Assert.Contains("negative", await Refused(s => s.MaxRunning = -1));
        Assert.Contains("wouldn't fit in the per-user CPU cap", await Refused(s => s.MaxCpus = 1));
        Assert.Contains("per-user memory cap", await Refused(s => s.MaxMemoryMb = 1024));
        Assert.Contains("per-user disk cap", await Refused(s => s.MaxDiskMb = 1024));
        Assert.Contains("needs a disk limit of its own", await Refused(s => { s.MaxDiskMb = 20480; s.DefaultDiskMb = 0; }));
        Assert.Contains("can't be lower than the running limit", await Refused(s => { s.MaxRunning = 5; s.MaxTotal = 2; }));
        Assert.Contains("at least 256 MB", await Refused(s => s.DefaultMemoryMb = 100));
        Assert.Contains("between 0.25 and 256", await Refused(s => s.DefaultCpus = 0));
        await using var db = app.Db();
        Assert.Contains(db.AuditEvents, a => a.Action == "workspace.admin_limits");
    }

    [Fact]
    public async Task The_rest_api_shows_usage_against_limits_refuses_with_422_and_lets_an_admin_change_them()
    {
        var (user, repo) = await UserAsync();
        using var client = await app.ApiClientAsync(user);
        using var admin = await app.ApiClientAsync("alice");
        client.BaseAddress = admin.BaseAddress = app.ServerAddress;
        static StringContent Json(object o) => new(JsonSerializer.Serialize(o), Encoding.UTF8, "application/json");
        try
        {
            var put = await admin.PutAsync("/api/v1/workspaces/limits", Json(new { maxRunning = 1, maxCpus = 4, idleDefaultMinutes = 20, idleMaxMinutes = 90 }));
            Assert.Equal(HttpStatusCode.OK, put.StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsync("/api/v1/workspaces/limits", Json(new { maxRunning = 99 }))).StatusCode);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, (await admin.PutAsync("/api/v1/workspaces/limits", Json(new { maxCpus = 1 }))).StatusCode); // below the 2 CPU default

            var start = await client.PostAsync($"/api/v1/projects/{app.ProjectKey(repo.ProjectId)}/repos/{repo.Slug}/workspaces", Json(new { @ref = "main" }));
            Assert.Equal(HttpStatusCode.Accepted, start.StatusCode);
            await app.WhenIdleAsync();
            var refused = await client.PostAsync($"/api/v1/projects/{app.ProjectKey(repo.ProjectId)}/repos/{repo.Slug}/workspaces", Json(new { @ref = "main" }));
            Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
            Assert.Contains("You already have 1 workspace running (the limit is 1). Stop one first.", await refused.Content.ReadAsStringAsync());

            var view = JsonDocument.Parse(await client.GetStringAsync("/api/v1/workspaces/limits")).RootElement;
            Assert.Equal(1, view.GetProperty("usage").GetProperty("running").GetInt32());
            Assert.Equal(2.0, view.GetProperty("usage").GetProperty("cpus").GetDouble());
            Assert.Equal(4.0, view.GetProperty("limits").GetProperty("maxCpus").GetDouble());
            Assert.Equal(20, view.GetProperty("idleTimeoutMinutes").GetInt32());
            Assert.Equal(90, view.GetProperty("idleMaxMinutes").GetInt32());
        }
        finally { await SetLimitsAsync(Baseline()); await using var s = app.Scope(app.UserId("alice")); await s.Get<WorkspaceLimitService>().SetIdleAsync(30, 480, app.UserId("alice")); }
    }
}

/// <summary>Idle auto-stop: admin default and maximum, the user's own period, running tasks, proxy traffic, the warning and "keep it running".</summary>
public class WorkspaceIdleTests(WorkspaceApp app) : IClassFixture<WorkspaceApp>
{
    private WorkspaceReaper Reaper => app.Services.GetRequiredService<WorkspaceReaper>();
    private WorkspaceIdlePolicy Policy => app.Services.GetRequiredService<WorkspaceIdlePolicy>();

    private async Task<(string User, Workspace Ws)> RunningAsync()
    {
        var user = await app.NewUserAsync();
        var repo = await app.RepoWithMembersAsync((user, ProjectRole.Developer));
        return (user, await app.StartAsync(user, repo));
    }

    private async Task AgeAsync(Workspace ws, TimeSpan idleFor)
    {
        var at = DateTime.UtcNow - idleFor;
        await using var db = app.Db();
        await db.Workspaces.Where(w => w.Id == ws.Id).ExecuteUpdateAsync(u => u.SetProperty(w => w.LastActivityAt, at));
        app.Runtime.Find(ws.PublicId)!.SetActivity(at);
    }

    private async Task SetSiteAsync(int def, int max)
    {
        await using var s = app.Scope(app.UserId("alice"));
        await s.Get<WorkspaceLimitService>().SetIdleAsync(def, max, app.UserId("alice"));
    }

    private async Task SetMineAsync(string user, int? minutes)
    {
        await using var s = app.Scope(app.UserId(user));
        await s.Get<WorkspaceLimitService>().SetMyIdleAsync(app.UserId(user), minutes);
    }

    [Fact]
    public async Task The_admin_default_applies_a_users_own_period_overrides_it_within_the_maximum_and_a_new_maximum_clamps_it()
    {
        var (user, _) = await RunningAsync();
        try
        {
            Assert.Equal(30, await Policy.EffectiveMinutesAsync(app.UserId(user))); // Workspaces:IdleTimeoutMinutes until an admin says otherwise
            await SetSiteAsync(10, 60);
            Assert.Equal(10, await Policy.EffectiveMinutesAsync(app.UserId(user)));
            await SetMineAsync(user, 45);
            Assert.Equal(45, await Policy.EffectiveMinutesAsync(app.UserId(user)));
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => SetMineAsync(user, 61));
            Assert.Contains("can't be more than 60 minutes", ex.Message);
            await Assert.ThrowsAsync<InvalidOperationException>(() => SetMineAsync(user, 0));
            await SetSiteAsync(10, 30); // the maximum comes down below the user's choice
            Assert.Equal(30, await Policy.EffectiveMinutesAsync(app.UserId(user)));
            await SetMineAsync(user, null);
            Assert.Equal(10, await Policy.EffectiveMinutesAsync(app.UserId(user)));
            await using var s = app.Scope(app.UserId("alice"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => s.Get<WorkspaceLimitService>().SetIdleAsync(90, 60, app.UserId("alice")));
        }
        finally { await SetSiteAsync(30, 480); }
    }

    [Fact]
    public async Task The_reaper_stops_a_workspace_idle_longer_than_its_owners_period_and_keeps_the_rest()
    {
        var (a, wa) = await RunningAsync();
        var (b, wb) = await RunningAsync();
        try
        {
            await SetSiteAsync(10, 120);
            await SetMineAsync(b, 60);
            await AgeAsync(wa, TimeSpan.FromMinutes(15));
            await AgeAsync(wb, TimeSpan.FromMinutes(15));
            await Reaper.SweepAsync(CancellationToken.None);
            var stopped = await app.ReloadAsync(wa.PublicId);
            Assert.Equal(WorkspaceState.Stopped, stopped.State);
            Assert.Equal("idle timeout", stopped.StopReason);
            var kept = await app.ReloadAsync(wb.PublicId);
            Assert.Equal(WorkspaceState.Running, kept.State); // its owner chose 60 minutes
            Assert.Equal(60, kept.IdleTimeoutMinutes);
            Assert.NotNull(a);
        }
        finally { await SetMineAsync(b, null); await SetSiteAsync(30, 480); }
    }

    [Fact]
    public async Task A_running_task_keeps_a_workspace_awake_and_its_end_starts_the_idle_period()
    {
        var (_, ws) = await RunningAsync();
        await AgeAsync(ws, TimeSpan.FromHours(3));
        using (var task = app.Runtime.BeginTask(ws.Id))
        {
            await Reaper.SweepAsync(CancellationToken.None);
            Assert.Equal(WorkspaceState.Running, (await app.ReloadAsync(ws.PublicId)).State);
            await Reaper.SweepAsync(CancellationToken.None);
            Assert.NotNull(task);
        }
        await Reaper.SweepAsync(CancellationToken.None);
        Assert.Equal(WorkspaceState.Running, (await app.ReloadAsync(ws.PublicId)).State); // the task just ended: idle starts now, not hours ago
        await AgeAsync(ws, TimeSpan.FromHours(3));
        await Reaper.SweepAsync(CancellationToken.None);
        Assert.Equal(WorkspaceState.Stopped, (await app.ReloadAsync(ws.PublicId)).State);
    }

    [Fact]
    public async Task Proxy_traffic_counts_as_activity()
    {
        var (user, ws) = await RunningAsync();
        await AgeAsync(ws, TimeSpan.FromMinutes(29));
        var url = await app.TicketUrlAsync(user, ws.PublicId);
        var host = new Uri(url).Authority;
        using var http = app.Http();
        using var enter = new HttpRequestMessage(HttpMethod.Get, new Uri(url).PathAndQuery);
        enter.Headers.Host = host;
        var redeemed = await http.SendAsync(enter);
        var cookie = redeemed.Headers.GetValues("Set-Cookie").Single().Split(';')[0];
        await AgeAsync(ws, TimeSpan.FromMinutes(29));
        using var echo = new HttpRequestMessage(HttpMethod.Get, "/echo");
        echo.Headers.Host = host;
        echo.Headers.TryAddWithoutValidation("Cookie", cookie);
        Assert.Equal(HttpStatusCode.OK, (await http.SendAsync(echo)).StatusCode);
        Assert.True(app.Runtime.Find(ws.PublicId)!.LastActivity > DateTime.UtcNow.AddMinutes(-1));
        Assert.NotNull(HttpUtility.ParseQueryString(new Uri(url).Query)["ticket"]);
    }

    [Fact]
    public async Task The_page_is_warned_shortly_before_the_stop_and_keep_running_resets_the_clock()
    {
        var (user, ws) = await RunningAsync();
        var id = app.UserId(user);
        var fresh = await Policy.StatusAsync(ws.PublicId, id);
        Assert.NotNull(fresh);
        Assert.False(fresh!.Warning);
        Assert.Equal(30, fresh.TimeoutMinutes);

        await AgeAsync(ws, TimeSpan.FromMinutes(27)); // three minutes left
        var warned = await Policy.StatusAsync(ws.PublicId, id);
        Assert.True(warned!.Warning);
        Assert.InRange((warned.StopsAt!.Value - DateTime.UtcNow).TotalMinutes, 2, 3.1);

        Assert.True(await Policy.KeepAliveAsync(ws.PublicId, id));
        var after = await Policy.StatusAsync(ws.PublicId, id);
        Assert.False(after!.Warning);
        Assert.InRange((after.StopsAt!.Value - DateTime.UtcNow).TotalMinutes, 29, 30.1);

        using (app.Runtime.BeginTask(ws.Id))
        {
            await AgeAsync(ws, TimeSpan.FromMinutes(29));
            var busy = await Policy.StatusAsync(ws.PublicId, id);
            Assert.True(busy!.TaskRunning);
            Assert.Null(busy.StopsAt);
            Assert.False(busy.Warning);
        }

        var other = await app.NewUserAsync();
        Assert.Null(await Policy.StatusAsync(ws.PublicId, app.UserId(other))); // not theirs
        Assert.False(await Policy.KeepAliveAsync(ws.PublicId, app.UserId(other)));
    }

    [Fact]
    public async Task The_rest_api_reports_the_idle_status_keeps_alive_and_lets_a_user_choose_their_period()
    {
        var (user, ws) = await RunningAsync();
        using var client = await app.ApiClientAsync(user);
        client.BaseAddress = app.ServerAddress;
        await AgeAsync(ws, TimeSpan.FromMinutes(28));
        var status = JsonDocument.Parse(await client.GetStringAsync($"/api/v1/workspaces/{ws.PublicId}/idle")).RootElement;
        Assert.True(status.GetProperty("warning").GetBoolean());
        Assert.Equal(30, status.GetProperty("timeoutMinutes").GetInt32());
        var keep = await client.PostAsync($"/api/v1/workspaces/{ws.PublicId}/keepalive", new StringContent("{}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, keep.StatusCode);
        Assert.False(JsonDocument.Parse(await keep.Content.ReadAsStringAsync()).RootElement.GetProperty("warning").GetBoolean());

        var mine = await client.PutAsync("/api/v1/workspaces/me/idle", new StringContent("{\"minutes\":15}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, mine.StatusCode);
        Assert.Equal(15, JsonDocument.Parse(await mine.Content.ReadAsStringAsync()).RootElement.GetProperty("idleTimeoutMinutes").GetInt32());
        var tooLong = await client.PutAsync("/api/v1/workspaces/me/idle", new StringContent("{\"minutes\":100000}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, tooLong.StatusCode);

        using var other = await app.ApiClientAsync(await app.NewUserAsync());
        other.BaseAddress = app.ServerAddress;
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/v1/workspaces/{ws.PublicId}/idle")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsync($"/api/v1/workspaces/{ws.PublicId}/keepalive", new StringContent("{}", Encoding.UTF8, "application/json"))).StatusCode);
        await SetMineAsync(user, null);
    }

    [Fact]
    public async Task Stopping_keeps_working_as_before_after_an_idle_stop_the_workspace_is_stopped_with_its_token_revoked()
    {
        var (_, ws) = await RunningAsync();
        await AgeAsync(ws, TimeSpan.FromHours(1));
        await Reaper.SweepAsync(CancellationToken.None);
        var after = await app.ReloadAsync(ws.PublicId);
        Assert.Equal(WorkspaceState.Stopped, after.State);
        Assert.Null(after.GitTokenHash);
        Assert.Contains(ws.ContainerId!, app.Backend.Removed);
        Assert.Null(app.Runtime.Find(ws.PublicId));
    }
}
