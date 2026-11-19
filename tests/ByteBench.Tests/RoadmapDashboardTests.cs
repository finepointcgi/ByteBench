using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ByteBench.Data;
using ByteBench.Services;
using ByteBench.Services.Dashboards;
using ByteBench.Services.Roadmap;
using ByteBench.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace ByteBench.Tests;

public class RoadmapDashboardTests(TestApp app) : IClassFixture<TestApp>
{
    private static readonly DateTime D1 = new(2026, 11, 2), D2 = new(2026, 11, 30);

    // ---------- Roadmap ----------

    [Fact]
    public async Task Roadmap_dates_are_saved_with_activity_and_need_EditIssues()
    {
        var project = await app.CreateProjectAsync("carol", ProjectVisibility.Private, ("bob", ProjectRole.Developer), ("dan", ProjectRole.Viewer));
        var epic = await app.CreateIssueAsync(project.Id, "carol", "Launch", IssueType.Epic);

        await using (var bob = app.Scope(app.UserId("bob")))
            await bob.Get<RoadmapService>().SetDatesAsync(epic.Id, D1.AddHours(13), D2, app.UserId("bob"));
        await using (var db = app.Db())
        {
            var saved = await db.Issues.Include(i => i.Activities).FirstAsync(i => i.Id == epic.Id);
            Assert.Equal(D1, saved.StartDate); // whole days
            Assert.Equal(D2, saved.DueDate);
            Assert.Contains(saved.Activities, a => a.Text == "set the start date to **Nov 2, 2026** and set the due date to **Nov 30, 2026**");
        }

        // A Viewer can't change dates; start after due is refused; an outsider can't even see the issue.
        await using (var dan = app.Scope(app.UserId("dan")))
            await Assert.ThrowsAsync<ForbiddenException>(() => dan.Get<RoadmapService>().SetDatesAsync(epic.Id, D1, D2.AddDays(1), app.UserId("dan")));
        await using (var bob = app.Scope(app.UserId("bob")))
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => bob.Get<RoadmapService>().SetDatesAsync(epic.Id, D2, D1, app.UserId("bob")));
            Assert.Contains("on or before", ex.Message);
        }
        var outsider = await app.CreateUserAsync("rm-outsider");
        await using (var o = app.Scope(outsider.Id))
        {
            await Assert.ThrowsAnyAsync<InvalidOperationException>(() => o.Get<RoadmapService>().SetDatesAsync(epic.Id, null, null, outsider.Id));
            Assert.Empty((await o.Get<RoadmapService>().GetAsync([project.Id])).Epics);
        }
        await using (var db = app.Db())
            Assert.Equal(D2, (await db.Issues.FirstAsync(i => i.Id == epic.Id)).DueDate); // unchanged
    }

    [Fact]
    public async Task Roadmap_lists_epics_with_progress_children_dependencies_and_filters()
    {
        var project = await app.CreateProjectAsync("carol", ProjectVisibility.Private, ("bob", ProjectRole.Developer), ("dan", ProjectRole.Viewer));
        var carolId = app.UserId("carol");
        var a = await app.CreateIssueAsync(project.Id, "carol", "Epic A", IssueType.Epic);
        var b = await app.CreateIssueAsync(project.Id, "carol", "Epic B", IssueType.Epic);
        var c1 = await app.CreateIssueAsync(project.Id, "carol", "A1", parentId: a.Id);
        await app.CreateIssueAsync(project.Id, "carol", "A2", parentId: a.Id);
        await app.CreateIssueAsync(project.Id, "carol", "A3", parentId: a.Id);
        await using (var s = app.Scope(carolId))
        {
            var issues = s.Get<IssueService>();
            await issues.SetStatusAsync(c1.Id, IssueStatus.Done, carolId);
            await issues.UpdateIssueAsync(c1.Id, carolId, (i, _) => { i.AssigneeId = app.UserId("bob"); return Task.FromResult<string?>("assigned"); });
            await s.Get<RoadmapService>().SetDatesAsync(a.Id, D1, D2, carolId);
            await s.Get<RoadmapService>().SetDatesAsync(b.Id, D2.AddDays(1), D2.AddDays(20), carolId);
            await s.Get<LinkedWorkService>().AddLinkAsync(a.Id, b.Id, IssueLinkRelation.Blocks, carolId);
        }

        await using var dan = app.Scope(app.UserId("dan"));
        var data = await dan.Get<RoadmapService>().GetAsync([project.Id]);
        Assert.Equal(["Epic A", "Epic B"], data.Epics.Select(e => e.Title));
        var ea = data.Epics[0];
        Assert.Equal((1, 3, 33), (ea.Done, ea.Total, ea.Percent));
        Assert.Equal(3, ea.Children.Count);
        Assert.False(ea.CanEdit); // dan is a Viewer
        Assert.Equal(new RoadmapDependency(a.Id, b.Id), Assert.Single(data.Dependencies));
        Assert.Contains(data.Assignees, u => u.UserName == "bob");

        // Filtering by assignee keeps the epic for its matching child only; progress still counts every child.
        var filtered = await dan.Get<RoadmapService>().GetAsync([project.Id], new RoadmapFilter(AssigneeId: app.UserId("bob")));
        var only = Assert.Single(filtered.Epics);
        Assert.Equal("A1", Assert.Single(only.Children).Title);
        Assert.Equal(3, only.Total);
        Assert.Empty(filtered.Dependencies); // B is filtered out

        await using var bob = app.Scope(app.UserId("bob"));
        Assert.True((await bob.Get<RoadmapService>().GetAsync([project.Id])).Epics[0].CanEdit);
    }

    [Fact]
    public void Drag_and_scale_math()
    {
        Assert.Equal((D1.AddDays(3), D2.AddDays(3)), RoadmapService.ApplyDrag(D1, D2, "move", 3));
        Assert.Equal((D1.AddDays(-2), D2), RoadmapService.ApplyDrag(D1, D2, "start", -2));
        Assert.Equal((D2, D2), RoadmapService.ApplyDrag(D1, D2, "start", 60)); // never past the target
        Assert.Equal((D1, D1), RoadmapService.ApplyDrag(D1, D2, "end", -60));
        Assert.Equal(((DateTime?)null, D2.AddDays(1)), RoadmapService.ApplyDrag(null, D2, "move", 1));
        Assert.Equal((D2.AddDays(-5), D2), RoadmapService.ApplyDrag(null, D2, "start", -5)); // fills in the start
        Assert.Null(RoadmapService.DatesText(D1, D2, D1, D2));
        Assert.Equal("removed the start date", RoadmapService.DatesText(D1, D2, null, D2));

        var scale = RoadmapScale.For(RoadmapZoom.Weeks, [D1, D2], new DateTime(2026, 11, 18));
        Assert.Equal(DayOfWeek.Monday, scale.From.DayOfWeek);
        Assert.True(scale.From < D1 && scale.To > D2);
        Assert.Equal(scale.PxPerDay * 28, scale.X(D2) - scale.X(D1));
        Assert.Equal(2, scale.Days(scale.PxPerDay * 2.4));
        Assert.Equal(new DateTime(2026, 10, 1), RoadmapScale.Snap(RoadmapZoom.Quarters, D2));
        Assert.Equal("Q4 2026", RoadmapScale.For(RoadmapZoom.Quarters, [D1], D1).Ticks().First(t => t.Date == new DateTime(2026, 10, 1)).Label);
    }

    // ---------- Dashboards ----------

    [Fact]
    public async Task Dashboards_are_private_until_shared_and_only_the_owner_edits()
    {
        var project = await app.CreateProjectAsync("carol", ProjectVisibility.Private, ("bob", ProjectRole.Developer), ("dan", ProjectRole.Viewer));
        await using var bob = app.Scope(app.UserId("bob"));
        await using var dan = app.Scope(app.UserId("dan"));
        var d = await bob.Get<DashboardService>().CreateAsync("Team");
        var g = await bob.Get<DashboardService>().AddGadgetAsync(d.Id, GadgetKind.Markdown, config: new GadgetConfig { Markdown = "hi" });

        Assert.Null(await dan.Get<DashboardService>().GetAsync(d.Id));
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => dan.Get<DashboardService>().SetHomeAsync(d.Id));
        // A Viewer can't share with the project; a Developer can.
        await Assert.ThrowsAsync<ForbiddenException>(async () =>
        {
            var mine = await dan.Get<DashboardService>().CreateAsync("Dan's");
            await dan.Get<DashboardService>().ShareAsync(mine.Id, project.Id, null);
        });
        await bob.Get<DashboardService>().ShareAsync(d.Id, project.Id, null);

        var seen = await dan.Get<DashboardService>().GetAsync(d.Id);
        Assert.NotNull(seen);
        Assert.False(seen.CanEdit);
        Assert.Contains(await dan.Get<DashboardService>().ListAsync(), x => x.Id == d.Id && !x.IsMine);
        await Assert.ThrowsAsync<ForbiddenException>(() => dan.Get<DashboardService>().RemoveGadgetAsync(g.Id));
        await Assert.ThrowsAsync<ForbiddenException>(() => dan.Get<DashboardService>().UpdateAsync(d.Id, "Mine now", null));
        await dan.Get<DashboardService>().SetHomeAsync(d.Id); // anyone who sees it may pin it
        Assert.Equal(d.Id, (await dan.Get<DashboardService>().GetHomeAsync())?.Dashboard.Id);

        var outsider = await app.CreateUserAsync("db-outsider");
        await using var o = app.Scope(outsider.Id);
        Assert.Null(await o.Get<DashboardService>().GetAsync(d.Id));
        Assert.DoesNotContain(await o.Get<DashboardService>().ListAsync(), x => x.Id == d.Id);

        // Reordering and removing.
        var g2 = await bob.Get<DashboardService>().AddGadgetAsync(d.Id, GadgetKind.AssignedToMe);
        await bob.Get<DashboardService>().MoveGadgetAsync(g2.Id, 0);
        Assert.Equal([g2.Id, g.Id], (await bob.Get<DashboardService>().GetAsync(d.Id))!.Gadgets.Select(x => x.Id));
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() =>
            bob.Get<DashboardService>().UpdateGadgetAsync(g.Id, null, 6, new GadgetConfig { Markdown = new string('x', GadgetConfig.MaxMarkdown + 1) }));

        // Unsharing hides it again, and the pinned Home falls back to the default.
        await bob.Get<DashboardService>().ShareAsync(d.Id, null, null);
        Assert.Null(await dan.Get<DashboardService>().GetHomeAsync());
    }

    [Fact]
    public async Task Gadget_queries_are_loaded_as_the_viewer()
    {
        var project = await app.CreateProjectAsync("carol", ProjectVisibility.Private, ("bob", ProjectRole.Developer));
        var bug = await app.CreateIssueAsync(project.Id, "carol", "Secret bug", IssueType.Bug);
        var config = new GadgetConfig { ProjectId = project.Id, Query = "type=Bug" };
        var outsider = await app.CreateUserAsync("gadget-outsider");

        await using (var bob = app.Scope(app.UserId("bob")))
        {
            var data = await bob.Get<GadgetDataService>().IssuesAsync(config);
            Assert.Null(data.Problem);
            Assert.Equal(bug.Id, Assert.Single(data.Issues).Id);
            var (slices, _) = await bob.Get<GadgetDataService>().ChartAsync(config with { Field = "type" });
            Assert.Equal(new ChartSlice("Bug", 1, 1), Assert.Single(slices));
        }
        await using (var o = app.Scope(outsider.Id))
        {
            var gd = o.Get<GadgetDataService>();
            var data = await gd.IssuesAsync(config);
            Assert.Empty(data.Issues);
            Assert.NotNull(data.Problem);
            Assert.Null((await gd.BurndownAsync(config)).Project);
            // "All your projects" leaves the private project out: another issue there doesn't change the outsider's count.
            var before = (await gd.CreatedVsResolvedAsync(new GadgetConfig { Days = 7 }))[^1].TotalCreated;
            await app.CreateIssueAsync(project.Id, "carol", "Another secret");
            Assert.Equal(before, (await gd.CreatedVsResolvedAsync(new GadgetConfig { Days = 7 }))[^1].TotalCreated);
            Assert.Equal(0, (await gd.CreatedVsResolvedAsync(config with { Days = 7 }))[^1].TotalCreated);
            Assert.DoesNotContain(await gd.ActivityAsync(new GadgetConfig()), f => f.ProjectKey == project.Key);
        }

        // A private saved filter works for its owner only.
        int filterId;
        await using (var carol = app.Scope(app.UserId("carol")))
        {
            filterId = (await carol.Get<SavedFilterService>().CreateAsync(project.Id, "Bugs", "type=Bug", shared: false)).Id;
            Assert.Single((await carol.Get<GadgetDataService>().IssuesAsync(new GadgetConfig { ProjectId = project.Id, SavedFilterId = filterId })).Issues);
            Assert.True((await carol.Get<GadgetDataService>().CreatedVsResolvedAsync(new GadgetConfig { ProjectId = project.Id, Days = 7 }))[^1].TotalCreated >= 1);
        }
        await using (var bob = app.Scope(app.UserId("bob")))
        {
            var data = await bob.Get<GadgetDataService>().IssuesAsync(new GadgetConfig { ProjectId = project.Id, SavedFilterId = filterId });
            Assert.Empty(data.Issues);
            Assert.Contains("filter", data.Problem);
        }

        // Pipelines of a repository the viewer can't see are hidden.
        var repo = await app.CreateRepoAsync(project.Id, "carol");
        await using (var o = app.Scope(outsider.Id))
            Assert.Null(await o.Get<GadgetDataService>().PipelinesAsync(new GadgetConfig { RepositoryId = repo.Id }));
        await using (var bob = app.Scope(app.UserId("bob")))
            Assert.NotNull(await bob.Get<GadgetDataService>().PipelinesAsync(new GadgetConfig { RepositoryId = repo.Id }));
    }

    [Fact]
    public void Chart_aggregation_counts_correctly()
    {
        User U(string n) => new() { DisplayName = n };
        Label L(string n) => new() { Name = n };
        var ann = U("Ann");
        var issues = new List<Issue>
        {
            new() { Priority = IssuePriority.High, Type = IssueType.Bug, Assignee = ann, Labels = [L("ui"), L("api")] },
            new() { Priority = IssuePriority.High, Type = IssueType.Task, Assignee = ann, Labels = [L("ui")] },
            new() { Priority = IssuePriority.Low, Type = IssueType.Bug, Status = IssueStatus.Done },
        };
        Assert.Equal([new("Ann", 2, 1), new("Unassigned", 1, 2)], GadgetMath.CountBy(issues, "assignee"));
        Assert.Equal([new("High", 2, 1), new("Low", 1, 2)], GadgetMath.CountBy(issues, "priority")); // highest first
        Assert.Equal([new("Task", 1, 1), new("Bug", 2, 2)], GadgetMath.CountBy(issues, "type")); // enum order
        Assert.Equal([new("ui", 2, 1), new("api", 1, 2), new("No label", 1, 3)], GadgetMath.CountBy(issues, "label"));
        Assert.Equal([new(IssueStatus.Todo.Name(), 2, 1), new(IssueStatus.Done.Name(), 1, 2)], GadgetMath.CountBy(issues, "status")); // workflow order

        // More than seven values fold into "Other".
        var many = Enumerable.Range(0, 10).Select(k => new Issue { Assignee = U($"U{k}") }).ToList();
        var folded = GadgetMath.CountBy(many, "assignee");
        Assert.Equal(8, folded.Count);
        Assert.Equal(new ChartSlice("Other", 3, 0), folded[^1]);
        Assert.Equal(10, folded.Sum(s => s.Count));

        var today = new DateTime(2026, 11, 18);
        var days = GadgetMath.CreatedVsResolved(
        [
            (today.AddDays(-2).AddHours(5), today.AddDays(-1)),
            (today, null),
            (today.AddDays(-30), today), // created before the window, resolved inside it
            (today.AddDays(-40), null),
        ], today, 3);
        Assert.Equal(3, days.Count);
        Assert.Equal(today.AddDays(-2), days[0].Date);
        Assert.Equal([1, 0, 1], days.Select(d => d.Created));
        Assert.Equal([0, 1, 1], days.Select(d => d.Resolved));
        Assert.Equal((2, 2), (days[^1].TotalCreated, days[^1].TotalResolved));
    }

    // ---------- API ----------

    [Fact]
    public async Task Api_serves_roadmap_and_dashboards_with_permissions()
    {
        var project = await app.CreateProjectAsync("carol", ProjectVisibility.Private, ("bob", ProjectRole.Developer), ("dan", ProjectRole.Viewer));
        var epic = await app.CreateIssueAsync(project.Id, "carol", "API epic", IssueType.Epic);
        await app.CreateIssueAsync(project.Id, "carol", "Child", parentId: epic.Id);

        var bob = await app.ApiClientAsync("bob");
        var put = await bob.PutAsJsonAsync($"/api/v1/projects/{project.Key}/issues/{epic.Number}/dates", new { startDate = "2026-11-02", dueDate = "2026-11-30" });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var bad = await bob.PutAsJsonAsync($"/api/v1/projects/{project.Key}/issues/{epic.Number}/dates", new { startDate = "2026-12-02", dueDate = "2026-11-30" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, bad.StatusCode);
        var dan = await app.ApiClientAsync("dan");
        var forbidden = await dan.PutAsJsonAsync($"/api/v1/projects/{project.Key}/issues/{epic.Number}/dates", new { startDate = (string?)null, dueDate = (string?)null });
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);

        var json = await dan.GetFromJsonAsync<JsonElement>($"/api/v1/projects/{project.Key}/roadmap");
        var e = json.GetProperty("epics")[0];
        Assert.Equal("API epic", e.GetProperty("title").GetString());
        Assert.Equal("2026-11-02", e.GetProperty("startDate").GetString());
        Assert.Equal("2026-11-30", e.GetProperty("dueDate").GetString());
        Assert.Equal(1, e.GetProperty("childrenTotal").GetInt32());
        Assert.False(e.GetProperty("canEdit").GetBoolean());

        await app.CreateUserAsync("api-outsider");
        var outsider = await app.ApiClientAsync("api-outsider");
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.GetAsync($"/api/v1/projects/{project.Key}/roadmap")).StatusCode);

        int id;
        await using (var s = app.Scope(app.UserId("bob")))
        {
            id = (await s.Get<DashboardService>().CreateAsync("API board")).Id;
            await s.Get<DashboardService>().AddGadgetAsync(id, GadgetKind.IssueChart, config: new GadgetConfig { ProjectId = project.Id, Field = "type", Chart = "bar" });
        }
        var board = await bob.GetFromJsonAsync<JsonElement>($"/api/v1/dashboards/{id}");
        Assert.Equal("API board", board.GetProperty("name").GetString());
        var gadget = board.GetProperty("gadgets")[0];
        Assert.Equal("issueChart", gadget.GetProperty("kind").GetString());
        Assert.Equal("bar", gadget.GetProperty("config").GetProperty("chart").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await dan.GetAsync($"/api/v1/dashboards/{id}")).StatusCode);

        // Group roadmap: Acme/Platform holds WEB and OPS; bob sees only WEB.
        var group = await app.ApiClientAsync("bob");
        var groupJson = await group.GetFromJsonAsync<JsonElement>("/api/v1/groups/acme%2Fplatform/roadmap");
        Assert.All(groupJson.GetProperty("epics").EnumerateArray(), x => Assert.StartsWith("WEB-", x.GetProperty("key").GetString()));
    }
}
