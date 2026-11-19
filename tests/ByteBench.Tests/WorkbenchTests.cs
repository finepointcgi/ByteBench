using System.Net;
using System.Text;
using System.Text.Json;
using ByteBench.Data;
using ByteBench.Services;
using ByteBench.Services.Workbench;
using ByteBench.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace ByteBench.Tests;

/// <summary>A site with <c>Workbench:BaseUrl</c> set (with a trailing slash, which is ignored).</summary>
public class WorkbenchApp : TestApp
{
    protected override void AddSettings(IDictionary<string, string?> settings) => settings["Workbench:BaseUrl"] = "https://workbench.example.test/";
}

/// <summary>The Workbench link: the project's WBS mapping, the issue override, link visibility, the API and the audit log.</summary>
public class WorkbenchTests(WorkbenchApp app) : IClassFixture<WorkbenchApp>
{
    private static StringContent Json(object body) => new(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

    [Theory]
    [InlineData(null, null)]
    [InlineData("  ", null)]
    [InlineData("workbench.example.com", null)] // not absolute
    [InlineData("ftp://workbench.example.com", null)]
    [InlineData("https://workbench.example.com/", "https://workbench.example.com")]
    [InlineData("http://wb.local:8080", "http://wb.local:8080")]
    public void Base_url_is_normalised(string? configured, string? expected) => Assert.Equal(expected, WorkbenchService.Normalize(configured));

    [Theory]
    [InlineData("1.2.3", "1.2.3")]
    [InlineData("  ABC-12.4 ", "ABC-12.4")]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Codes_are_trimmed(string? code, string? expected) => Assert.Equal(expected, WorkbenchService.CleanCode(code, "WBS code"));

    [Theory]
    [InlineData("1.2 3")]
    [InlineData("<script>")]
    [InlineData(".1")]
    public void Bad_codes_are_refused(string code) => Assert.Throws<InvalidOperationException>(() => WorkbenchService.CleanCode(code, "WBS code"));

    [Fact]
    public async Task Maintainers_set_the_wbs_mapping_and_it_is_audited()
    {
        var project = await app.CreateProjectAsync("alice", ProjectVisibility.Private, ("bob", ProjectRole.Developer), ("carol", ProjectRole.Maintainer));
        await using (var bob = app.Scope(app.UserId("bob")))
            await Assert.ThrowsAsync<ForbiddenException>(() => bob.Get<WorkbenchService>().SetProjectMappingAsync(project.Id, "1.2.3", null));

        await using var carol = app.Scope(app.UserId("carol"));
        var workbench = carol.Get<WorkbenchService>();
        await workbench.SetProjectMappingAsync(project.Id, " 1.2.3 ", "GB-7");
        await workbench.SetProjectMappingAsync(project.Id, "1.2.3", "GB-7"); // unchanged: no second event
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => workbench.SetProjectMappingAsync(project.Id, "1 2", null));
        await workbench.SetProjectMappingAsync(project.Id, "1.2.4", null);

        await using var db = app.Db();
        var p = await db.Projects.SingleAsync(x => x.Id == project.Id);
        Assert.Equal(("1.2.4", (string?)null), (p.WorkbenchWbs, p.WorkbenchProjectCode));
        var events = await db.AuditEvents.Where(e => e.ProjectId == project.Id && e.Action == "project.workbench_changed").OrderBy(e => e.Id).ToListAsync();
        Assert.Equal(2, events.Count);
        Assert.Equal(app.UserId("carol"), events[0].ActorId);
        Assert.Contains("1.2.3", events[0].Details);
        Assert.Contains("1.2.4", events[1].Details);
    }

    [Fact]
    public async Task The_link_uses_the_issue_override_else_the_projects_code()
    {
        var project = await app.CreateProjectAsync("alice", ProjectVisibility.Private, ("bob", ProjectRole.Developer), ("dan", ProjectRole.Viewer));
        var issue = await app.CreateIssueAsync(project.Id, "bob");
        await using var alice = app.Scope(app.UserId("alice"));
        var workbench = alice.Get<WorkbenchService>();
        Assert.Equal("https://workbench.example.test/time", workbench.TimesheetUrl());
        Assert.Equal("https://workbench.example.test/time?date=2026-11-18", workbench.TimesheetUrl(new DateOnly(2026, 11, 18)));

        async Task<WorkbenchLink?> Link()
        {
            await using var db = app.Db();
            var i = await db.Issues.Include(x => x.Project).SingleAsync(x => x.Id == issue.Id);
            return workbench.LinkFor(i, i.Project);
        }

        Assert.Null(await Link()); // no WBS code yet
        await workbench.SetProjectMappingAsync(project.Id, "1.2.3", "GB-1");
        Assert.Equal(new WorkbenchLink("1.2.3", "GB-1", false, "https://workbench.example.test/time"), await Link());

        await using (var dan = app.Scope(app.UserId("dan")))
            await Assert.ThrowsAsync<ForbiddenException>(() => dan.Get<WorkbenchService>().SetIssueWbsAsync(issue.Id, "9.9"));
        await using (var bob = app.Scope(app.UserId("bob"))) await bob.Get<WorkbenchService>().SetIssueWbsAsync(issue.Id, "9.9");
        Assert.Equal(("9.9", true), ((await Link())!.Wbs, (await Link())!.IsIssueOverride));
        await workbench.SetIssueWbsAsync(issue.Id, " ");
        Assert.Equal("1.2.3", (await Link())!.Wbs);

        await using var check = app.Db();
        Assert.Contains(check.Activities.Where(a => a.IssueId == issue.Id).Select(a => a.Text), t => t == "set the Workbench charge code to **9.9**");
        Assert.Equal(2, await check.AuditEvents.CountAsync(e => e.Action == "issue.workbench_changed" && e.TargetId == issue.Id.ToString()));
    }

    [Fact]
    public async Task Issue_page_shows_log_time_in_workbench_only_with_a_wbs_code()
    {
        var project = await app.CreateProjectAsync("alice", ProjectVisibility.Private, ("bob", ProjectRole.Developer));
        var issue = await app.CreateIssueAsync(project.Id, "bob");
        var url = $"/p/{project.Key}/issues/{issue.Number}";

        var bob = await app.CookieClientAsync("bob");
        var alice = await app.CookieClientAsync("alice");
        var page = await bob.GetStringAsync(url);
        Assert.DoesNotContain("workbench-log-time", page);
        Assert.DoesNotContain("workbench-hint", page); // Developers get no hint
        Assert.Contains("workbench-hint", await alice.GetStringAsync(url)); // Maintainers+ do

        await using (var s = app.Scope(app.UserId("alice"))) await s.Get<WorkbenchService>().SetProjectMappingAsync(project.Id, "1.2.3", null);
        page = await bob.GetStringAsync(url);
        Assert.Contains("Log time in Workbench", page);
        Assert.Matches("href=\"https://workbench.example.test/time\"[^>]*target=\"_blank\"|target=\"_blank\"[^>]*href=\"https://workbench.example.test/time\"", page);
        Assert.Contains("rel=\"noopener noreferrer\"", page);
        Assert.Contains(">1.2.3</code>", page);
    }

    [Fact]
    public async Task Api_exposes_and_sets_the_wbs_code()
    {
        var project = await app.CreateProjectAsync("alice", ProjectVisibility.Private, ("bob", ProjectRole.Developer), ("carol", ProjectRole.Maintainer));
        var carol = await app.ApiClientAsync("carol");
        var bob = await app.ApiClientAsync("bob");

        Assert.Equal(HttpStatusCode.Forbidden, (await bob.PutAsync($"/api/v1/projects/{project.Key}/workbench", Json(new { wbs = "1.2.3" }))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await carol.PutAsync($"/api/v1/projects/{project.Key}/workbench", Json(new { wbs = "1 2" }))).StatusCode);
        var r = await carol.PutAsync($"/api/v1/projects/{project.Key}/workbench", Json(new { wbs = "1.2.3", projectCode = "GB-1" }));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var mapping = JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("https://workbench.example.test/time", mapping.GetProperty("timesheetUrl").GetString());

        var p = JsonDocument.Parse(await bob.GetStringAsync($"/api/v1/projects/{project.Key}")).RootElement;
        Assert.Equal(("1.2.3", "GB-1"), (p.GetProperty("workbenchWbs").GetString(), p.GetProperty("workbenchProjectCode").GetString()));

        // Issues carry the effective code; an override can be set on create and changed with PATCH.
        r = await bob.PostAsync($"/api/v1/projects/{project.Key}/issues", Json(new { title = "Charged elsewhere", workbenchWbs = "4.5" }));
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        var issue = JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(("4.5", true), (issue.GetProperty("workbenchWbs").GetString(), issue.GetProperty("workbenchWbsOverride").GetBoolean()));
        var number = issue.GetProperty("number").GetInt32();
        r = await bob.PatchAsync($"/api/v1/projects/{project.Key}/issues/{number}", Json(new { workbenchWbs = (string?)null }));
        issue = JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(("1.2.3", false), (issue.GetProperty("workbenchWbs").GetString(), issue.GetProperty("workbenchWbsOverride").GetBoolean()));
        Assert.Equal(HttpStatusCode.UnprocessableEntity,
            (await bob.PatchAsync($"/api/v1/projects/{project.Key}/issues/{number}", Json(new { workbenchWbs = "bad code" }))).StatusCode);

        // GQL over the API.
        var found = JsonDocument.Parse(await bob.GetStringAsync($"/api/v1/search/issues?gql={Uri.EscapeDataString($"project = {project.Key} AND wbs = \"1.2.3\"")}")).RootElement;
        Assert.Contains("Charged elsewhere", found.ToString());
    }
}

/// <summary>Without <c>Workbench:BaseUrl</c> nothing about Workbench shows, even with a WBS code.</summary>
public class WorkbenchOffTests(TestApp app) : IClassFixture<TestApp>
{
    [Fact]
    public async Task Without_a_base_url_the_link_and_hint_are_hidden()
    {
        var project = await app.CreateProjectAsync("alice");
        var issue = await app.CreateIssueAsync(project.Id, "alice");
        await using (var s = app.Scope(app.UserId("alice")))
        {
            var workbench = s.Get<WorkbenchService>();
            Assert.False(workbench.IsConfigured);
            Assert.Null(workbench.TimesheetUrl());
            await workbench.SetProjectMappingAsync(project.Id, "1.2.3", null); // can still be set up ahead of time
        }
        var page = await (await app.CookieClientAsync("alice")).GetStringAsync($"/p/{project.Key}/issues/{issue.Number}");
        Assert.DoesNotContain("Log time in Workbench", page);
        Assert.DoesNotContain("workbench-hint", page);
    }
}
