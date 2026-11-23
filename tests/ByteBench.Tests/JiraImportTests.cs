using System.Text;
using ByteBench.Data;
using ByteBench.Services.TrackerImport;
using ByteBench.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ByteBench.Tests;

/// <summary>A site wired to read from a fake Jira (private networks allowed, no background worker: tests drive the runner).</summary>
public abstract class JiraApp(JiraFlavour flavour) : TestApp
{
    public FakeJira Fake { get; } = new(flavour);
    /// <summary>The second host attachment downloads are redirected to (a CDN): it must never see the Jira credentials.</summary>
    public FakeFileHost Cdn { get; } = new();

    protected override void AddSettings(IDictionary<string, string?> settings)
    {
        settings["TrackerImport:AllowPrivateNetworks"] = "true";
        settings["TrackerImport:Enabled"] = "false";
        settings["Attachments:MaxBytes"] = "4096";
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) { Fake.Dispose(); Cdn.Dispose(); }
    }
}

public sealed class JiraCloudApp() : JiraApp(JiraFlavour.Cloud);
public sealed class JiraServerApp() : JiraApp(JiraFlavour.Server);

/// <summary>Both flavours run the same scenarios: the neutral result must not depend on the Jira flavour.</summary>
public abstract class JiraImportScenarios<TApp> : IClassFixture<TApp> where TApp : JiraApp
{
    protected readonly TApp App;
    protected FakeJira Fake => App.Fake;
    protected readonly List<TimeSpan> Delays = [];

    protected JiraImportScenarios(TApp app)
    {
        App = app;
        Fake.AuthMode = "Basic"; Fake.Forbidden = false; Fake.RateLimitNext = 0; Fake.PageSize = 2;
        Fake.AgileAvailable = true; Fake.ExtraActiveSprint = false; Fake.OrphanSprint = false; Fake.ActiveSprintEnd = "2024-04-28T00:00:00.000Z";
        Fake.CdnUrl = app.Cdn.Url;
        lock (app.Cdn.Files) app.Cdn.Files["/media/spec.pdf"] = new FakeFile(FakeFiles.Pdf);
        lock (app.Cdn.Requests) app.Cdn.Requests.Clear();
        lock (Fake.FileRequests) Fake.FileRequests.Clear();
        lock (Fake.Requests) Fake.Requests.Clear();
        app.Services.GetRequiredService<TrackerImportTestHooks>().Delay = (d, _) => { Delays.Add(d); return Task.CompletedTask; };
    }

    protected TrackerImportInput Input(string project = FakeJira.Key) => Fake.AuthMode == "Bearer"
        ? new() { Source = TrackerSourceKind.Jira, BaseUrl = Fake.Url, Project = project, Secret = FakeJira.Pat }
        : new() { Source = TrackerSourceKind.Jira, BaseUrl = Fake.Url, Project = project, Username = FakeJira.Email, Secret = FakeJira.ApiToken };

    protected async Task<(Project P, int Carol)> NewProjectAsync()
    {
        var p = await App.CreateProjectAsync("carol", ProjectVisibility.Private, ("bob", ProjectRole.Developer));
        return (p, App.UserId("carol"));
    }

    protected async Task<TrackerImportJob> RunAsync(Project p, int actor, TrackerImportInput? input = null, TrackerImportSettings? settings = null)
    {
        await using var s = App.Scope(actor);
        input ??= Input();
        input.Settings = settings ?? new();
        var job = await s.Get<TrackerImportService>().StartAsync(p.Id, input, actor);
        await App.Services.GetRequiredService<TrackerImportRunner>().RunAsync(job.Id);
        await using var db = App.Db();
        return await db.TrackerImportJobs.AsNoTracking().FirstAsync(j => j.Id == job.Id);
    }

    private async Task<Dictionary<string, Issue>> IssuesAsync(Project p)
    {
        await using var db = App.Db();
        var all = await db.Issues.AsNoTracking().Include(i => i.Labels).Include(i => i.Comments).Include(i => i.Milestone)
            .Where(i => i.ProjectId == p.Id).ToListAsync();
        return all.ToDictionary(i => i.Title);
    }

    [Fact]
    public async Task Check_reports_the_project_its_size_statuses_and_versions()
    {
        var (p, carol) = await NewProjectAsync();
        await using var s = App.Scope(carol);
        var check = await s.Get<TrackerImportService>().CheckAsync(p.Id, Input(), carol);

        Assert.Equal("Payments Platform", check.Probe.ProjectName);
        Assert.Equal("Everything about taking money.", check.Probe.Description);
        Assert.Equal(5, check.Probe.IssueCount);
        Assert.Equal(["Checkout revamp", "Payment form", "Card declined crash"], check.Probe.SampleTitles);
        var statuses = check.SourceStatuses.ToDictionary(x => x.Name, x => x.Category);
        Assert.Equal(4, statuses.Count);
        Assert.Equal(IssueStatus.Todo, statuses["To Do"]);
        Assert.Equal(IssueStatus.InProgress, statuses["In Progress"]);
        Assert.Equal(IssueStatus.InProgress, statuses["In Review"]);
        Assert.Equal(IssueStatus.Done, statuses["Done"]);
        Assert.Equal(0, check.LabelCount);
        Assert.Equal(2, check.MilestoneCount);
    }

    [Fact]
    public async Task Imports_hierarchy_fields_links_milestones_labels_points_comments_and_attachments()
    {
        var (p, carol) = await NewProjectAsync();
        var job = await RunAsync(p, carol);

        Assert.Equal(TrackerImportStatus.Done, job.Status);
        Assert.Null(job.Error);
        Assert.Equal(5, job.IssuesCreated);
        Assert.Equal(4, job.CommentsCreated);
        Assert.Equal(4, job.LinksCreated);
        Assert.Equal(2, job.MilestonesCreated);
        var issues = await IssuesAsync(p);
        var epic = issues["Checkout revamp"];
        var story = issues["Payment form"];
        var bug = issues["Card declined crash"];
        var sub = issues["Validate card number"];
        var task = issues["Write the docs"];

        // types, hierarchy
        Assert.Equal(IssueType.Epic, epic.Type);
        Assert.Equal(IssueType.Story, story.Type);
        Assert.Equal(IssueType.Bug, bug.Type);
        Assert.Equal(IssueType.Subtask, sub.Type);
        Assert.Equal(IssueType.Task, task.Type);
        Assert.Equal(epic.Id, story.ParentId);
        Assert.Equal(story.Id, sub.ParentId);
        Assert.Null(epic.ParentId);

        // statuses by category and name, priorities, points, dates
        Assert.Equal(IssueStatus.InProgress, epic.Status);
        Assert.Equal(IssueStatus.InReview, story.Status);
        Assert.Equal(IssueStatus.Done, bug.Status);
        Assert.Equal(IssueStatus.Todo, task.Status);
        Assert.Equal(IssuePriority.High, epic.Priority);
        Assert.Equal(IssuePriority.Medium, story.Priority);
        Assert.Equal(IssuePriority.Highest, bug.Priority);   // Critical
        Assert.Equal(IssuePriority.Low, sub.Priority);       // Minor
        Assert.Equal(IssuePriority.Lowest, task.Priority);
        Assert.Equal(5, story.StoryPoints);                   // 4.6 rounds
        Assert.Equal(2, sub.StoryPoints);
        Assert.Equal(0, task.StoryPoints);
        Assert.Null(epic.StoryPoints);
        Assert.Equal(new DateTime(2024, 9, 30), epic.DueDate?.Date);
        Assert.Equal(new DateTime(2024, 5, 1, 10, 0, 0, DateTimeKind.Utc), epic.CreatedAt);       // +0200 -> UTC
        Assert.Equal(new DateTime(2024, 5, 10, 21, 45, 0, DateTimeKind.Utc), bug.ClosedAt);        // -0500 -> UTC
        Assert.Null(epic.ClosedAt);

        // labels, components, sprint
        Assert.Equal(["component: Backend", "component: Frontend", "payments"], story.Labels.Select(l => l.Name).Order(StringComparer.Ordinal).ToList());
        Assert.Equal(["component: Backend", "frontend"], epic.Labels.Select(l => l.Name).Order(StringComparer.Ordinal).ToList());

        // versions -> milestones
        Assert.Equal("Release 1.0", epic.Milestone?.Title);
        Assert.Equal("Release 1.0", story.Milestone?.Title);
        Assert.Equal("Release 2.0", bug.Milestone?.Title);
        Assert.Null(task.MilestoneId);
        await using var db = App.Db();
        var ms = await db.Milestones.AsNoTracking().Where(m => m.ProjectId == p.Id).ToListAsync();
        var one = ms.Single(m => m.Title == "Release 1.0");
        Assert.True(one.IsClosed);
        Assert.Equal("First public release", one.Description);
        Assert.Equal(new DateTime(2024, 6, 30), one.DueDate?.Date);
        Assert.False(ms.Single(m => m.Title == "Release 2.0").IsClosed);

        // links: Blocks (outward only), Clones, Duplicates, RelatesTo; the link to another project is dropped
        var links = await db.IssueLinks.AsNoTracking().Where(l => l.SourceIssue.ProjectId == p.Id).ToListAsync();
        Assert.Equal(4, links.Count);
        Assert.Contains(links, l => l.Type == IssueLinkType.Blocks && l.SourceIssueId == story.Id && l.TargetIssueId == bug.Id);
        Assert.Contains(links, l => l.Type == IssueLinkType.Clones && l.SourceIssueId == bug.Id && l.TargetIssueId == task.Id);
        Assert.Contains(links, l => l.Type == IssueLinkType.Duplicates && l.SourceIssueId == sub.Id && l.TargetIssueId == bug.Id);
        Assert.Contains(links, l => l.Type == IssueLinkType.RelatesTo && l.SourceIssueId == sub.Id && l.TargetIssueId == epic.Id);

        // comments, including the ones that needed a second request
        Assert.Contains(story.Comments, c => c.Body.Contains("First comment"));
        Assert.Contains(story.Comments, c => c.Body.Contains("Second comment"));
        Assert.Contains(story.Comments, c => c.Body.Contains("Third comment, from the second page"));
        Assert.Equal(3, story.Comments.Count);
        Assert.Single(bug.Comments);
        Assert.True(Fake.Hits("/issue/PROJ-2/comment") >= 1);
        Assert.Equal(0, Fake.Hits("/issue/PROJ-1/comment"));

        // descriptions: converted to markdown, source link, attachments listed
        Assert.Contains("## Goal", story.Description);
        Assert.Contains("*card*", story.Description);
        Assert.Contains("- one", story.Description);
        Assert.Contains("- two", story.Description);
        Assert.Contains("```js", story.Description);
        Assert.Contains("pay(card);", story.Description);
        Assert.Contains($"Imported from [Jira PROJ-2]({Fake.Url}/browse/PROJ-2)", story.Description);
        Assert.Contains($"[screenshot.png]({Fake.ContentUrl("10100", "screenshot.png")})", story.Description);
        Assert.Contains("Rebuild the checkout **flow**.", epic.Description);

        // warnings: the dropped cross-project link; sprints are real sprints now, not labels
        var warnings = TrackerImportService.WarningsOf(job);
        Assert.Contains(warnings, w => w.Contains("PROJ-2") && w.Contains("OTHER-9") && w.Contains("other Jira projects"));
        Assert.DoesNotContain(warnings, w => w.Contains("sprint"));
        Assert.DoesNotContain(await db.Labels.Where(l => l.ProjectId == p.Id).Select(l => l.Name).ToListAsync(), n => n.StartsWith("sprint:"));
        Assert.Equal(3, await db.Sprints.CountAsync(x => x.ProjectId == p.Id));
        Assert.Equal(await db.Sprints.Where(x => x.ProjectId == p.Id && x.Name == "Sprint 2").Select(x => x.Id).SingleAsync(), story.SprintId);
    }

    [Fact]
    public async Task Open_only_import_leaves_out_done_issues_and_says_so_in_the_query()
    {
        var (p, carol) = await NewProjectAsync();
        var job = await RunAsync(p, carol, settings: new TrackerImportSettings { IncludeClosed = false });

        Assert.Equal(TrackerImportStatus.Done, job.Status);
        Assert.Equal(4, job.IssuesCreated);
        Assert.DoesNotContain("Card declined crash", (await IssuesAsync(p)).Keys);
        Assert.True(Fake.Hits(Uri.EscapeDataString("statusCategory != Done")) >= 1);
    }

    [Fact]
    public async Task Without_comments_no_comment_requests_are_made()
    {
        var (p, carol) = await NewProjectAsync();
        var job = await RunAsync(p, carol, settings: new TrackerImportSettings { IncludeComments = false });

        Assert.Equal(TrackerImportStatus.Done, job.Status);
        Assert.Equal(0, job.CommentsCreated);
        Assert.Equal(0, Fake.Hits("/comment"));
    }

    [Fact]
    public async Task Search_results_are_paged_to_the_end()
    {
        var (p, carol) = await NewProjectAsync();
        var job = await RunAsync(p, carol);

        Assert.Equal(5, job.IssuesCreated);
        // 5 issues at 2 per page: three pages of the import (plus the check's sample page).
        lock (Fake.Requests)
        {
            var pages = Fake.Requests.Count(r => r.Method == "GET" && r.PathAndQuery.Contains("/search") && r.PathAndQuery.Contains("issuetype"));
            Assert.Equal(3, pages);
        }
    }

    [Fact]
    public async Task Running_again_adds_nothing()
    {
        var (p, carol) = await NewProjectAsync();
        await RunAsync(p, carol);
        var again = await RunAsync(p, carol);

        Assert.Equal(TrackerImportStatus.Done, again.Status);
        Assert.Equal(0, again.IssuesCreated);
        Assert.Equal(5, again.IssuesSkipped);
        await using var db = App.Db();
        Assert.Equal(5, await db.Issues.CountAsync(i => i.ProjectId == p.Id));
        Assert.Equal(4, await db.IssueLinks.CountAsync(l => l.SourceIssue.ProjectId == p.Id));
    }

    [Fact]
    public async Task Basic_auth_is_used_when_there_is_a_user_name_and_credentials_only_travel_in_the_header()
    {
        var (p, carol) = await NewProjectAsync();
        var job = await RunAsync(p, carol);

        Assert.Equal(TrackerImportStatus.Done, job.Status);
        lock (Fake.Requests)
        {
            Assert.NotEmpty(Fake.Requests);
            Assert.All(Fake.Requests, r => Assert.Equal(Fake.ExpectedAuth, r.Auth));
            foreach (var secret in Fake.Secrets.Append(FakeJira.Email))
                Assert.All(Fake.Requests, r => { Assert.DoesNotContain(secret, r.PathAndQuery); Assert.DoesNotContain(secret, r.Body ?? ""); });
        }
        await AssertNoSecretsStoredAsync(p, job);
    }

    [Fact]
    public async Task A_token_alone_is_sent_as_a_bearer_token()
    {
        Fake.AuthMode = "Bearer";
        var (p, carol) = await NewProjectAsync();
        var job = await RunAsync(p, carol);

        Assert.Equal(TrackerImportStatus.Done, job.Status);
        Assert.Equal(5, job.IssuesCreated);
        lock (Fake.Requests) Assert.All(Fake.Requests, r => Assert.Equal("Bearer " + FakeJira.Pat, r.Auth));
        await AssertNoSecretsStoredAsync(p, job);
    }

    [Fact]
    public async Task Wrong_credentials_fail_clearly_without_echoing_them()
    {
        var (p, carol) = await NewProjectAsync();
        TrackerImportInput Wrong() { var i = Input(); i.Secret = "jira_wrong_token_zzz"; return i; }
        await using (var s = App.Scope(carol))
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => s.Get<TrackerImportService>().CheckAsync(p.Id, Wrong(), carol));
            Assert.Contains("rejected the credentials", ex.Message);
            Assert.DoesNotContain("jira_wrong_token_zzz", ex.Message);
        }
        var job = await RunAsync(p, carol, Wrong());
        Assert.Equal(TrackerImportStatus.Failed, job.Status);
        Assert.Contains("rejected the credentials", job.Error);
        Assert.DoesNotContain("jira_wrong_token_zzz", job.Error + job.WarningsJson + job.SettingsJson);
        await AssertNoSecretsStoredAsync(p, job, "jira_wrong_token_zzz");
    }

    [Fact]
    public async Task A_token_without_access_is_reported_as_such()
    {
        Fake.Forbidden = true;
        var (p, carol) = await NewProjectAsync();
        await using var s = App.Scope(carol);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => s.Get<TrackerImportService>().CheckAsync(p.Id, Input(), carol));
        Assert.Contains("isn't allowed", ex.Message);
        Assert.DoesNotContain(FakeJira.ApiToken, ex.Message);
    }

    [Fact]
    public async Task An_unknown_project_key_is_reported()
    {
        var (p, carol) = await NewProjectAsync();
        await using var s = App.Scope(carol);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => s.Get<TrackerImportService>().CheckAsync(p.Id, Input("NOPE"), carol));
        Assert.Contains("doesn't exist", ex.Message);
        var job = await RunAsync(p, carol, Input("NOPE"));
        Assert.Equal(TrackerImportStatus.Failed, job.Status);
        Assert.Contains("doesn't exist", job.Error);
    }

    [Fact]
    public async Task A_rate_limit_is_waited_out_and_the_import_goes_on()
    {
        Fake.RateLimitNext = 2;
        var (p, carol) = await NewProjectAsync();
        var job = await RunAsync(p, carol);

        Assert.Equal(TrackerImportStatus.Done, job.Status);
        Assert.Equal(5, job.IssuesCreated);
        Assert.Equal(2, Delays.Count);
        Assert.All(Delays, d => Assert.True(d >= TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task The_reader_stops_at_the_issue_limit()
    {
        var http = App.Services.GetRequiredService<TrackerHttp>();
        using var reader = new JiraReader(new SourceConnection(TrackerSourceKind.Jira, Fake.Url, FakeJira.Key, FakeJira.Email, FakeJira.ApiToken), http);
        var keys = new List<string>();
        await foreach (var i in reader.ReadIssuesAsync(new SourceReadOptions(true, false, false, 3), CancellationToken.None)) keys.Add(i.ExternalId);
        Assert.Equal(["PROJ-1", "PROJ-2", "PROJ-3"], keys);
    }


    // ------------------------------------------------------------ sprints

    private async Task<Dictionary<string, Sprint>> SprintsAsync(Project p)
    {
        await using var db = App.Db();
        return (await db.Sprints.AsNoTracking().Where(x => x.ProjectId == p.Id).ToListAsync()).ToDictionary(x => x.Name);
    }

    [Fact]
    public async Task Sprints_become_real_sprints_with_goals_dates_states_and_report_counts()
    {
        var (p, carol) = await NewProjectAsync();
        var job = await RunAsync(p, carol);

        Assert.Equal(TrackerImportStatus.Done, job.Status);
        var sprints = await SprintsAsync(p);
        Assert.Equal(["Sprint 1", "Sprint 2", "Sprint 3"], sprints.Keys.Order().ToList());
        var one = sprints["Sprint 1"];
        Assert.Equal(SprintState.Completed, one.State);
        Assert.Equal("Ship the form", one.Goal);
        Assert.Equal(new DateTime(2024, 4, 1), one.StartDate);
        Assert.Equal(new DateTime(2024, 4, 14), one.EndDate);
        Assert.Equal(new DateTime(2024, 4, 14, 10, 0, 0, DateTimeKind.Utc), one.CompletedAt);
        Assert.NotNull(one.StartedAt);
        // The bug (3 points, Done) is the only issue left in the closed sprint: committed and completed come from the imported issues.
        Assert.Equal((1, 3, 1, 3), (one.CommittedCount, one.CommittedPoints, one.CompletedCount, one.CompletedPoints));
        var two = sprints["Sprint 2"];
        Assert.Equal(SprintState.Active, two.State);
        Assert.Equal("Card payments", two.Goal);
        Assert.Null(two.CompletedAt);
        Assert.Equal((1, 5), (two.CommittedCount, two.CommittedPoints));   // the story, 4.6 points rounds to 5
        Assert.Equal(SprintState.Planned, sprints["Sprint 3"].State);
        Assert.Null(sprints["Sprint 3"].StartedAt);

        var issues = await IssuesAsync(p);
        Assert.Equal(two.Id, issues["Payment form"].SprintId);              // in sprints 1 and 2: the latest
        Assert.Equal(one.Id, issues["Card declined crash"].SprintId);
        Assert.Equal(sprints["Sprint 3"].Id, issues["Validate card number"].SprintId);
        Assert.Null(issues["Checkout revamp"].SprintId);
        await using var db = App.Db();
        Assert.Equal(1, await db.Sprints.CountAsync(x => x.ProjectId == p.Id && x.State == SprintState.Active));
        Assert.Equal(3, await db.ImportedItems.CountAsync(i => i.ProjectId == p.Id && i.Kind == ImportedKind.Sprint));
        Assert.Contains(Fake.Requests.ToList(), r => r.PathAndQuery.StartsWith("/rest/agile/1.0/board/1/sprint?startAt=2"));   // the second page of sprints
    }

    [Fact]
    public async Task A_second_active_sprint_is_imported_as_completed_when_it_ended_or_planned_when_it_didnt()
    {
        Fake.ExtraActiveSprint = true;
        var (p, carol) = await NewProjectAsync();
        var job = await RunAsync(p, carol);

        var sprints = await SprintsAsync(p);
        Assert.Equal(SprintState.Active, sprints["Sprint 2"].State);          // ordered by start date, then id: the first active one keeps it
        Assert.Equal(SprintState.Completed, sprints["Parallel sprint"].State); // its end date is in the past
        Assert.NotNull(sprints["Parallel sprint"].CompletedAt);
        Assert.Contains(TrackerImportService.WarningsOf(job), w => w.Contains("Parallel sprint") && w.Contains("only one active sprint") && w.Contains("completed"));

        Fake.ActiveSprintEnd = "2099-01-01T00:00:00.000Z";
        Fake.ExtraActiveSprint = true;
        var (p2, _) = await NewProjectAsync();
        await RunAsync(p2, carol);
        var second = await SprintsAsync(p2);
        Assert.Equal(1, second.Values.Count(x => x.State == SprintState.Active));
    }

    [Fact]
    public async Task An_active_sprint_the_project_already_has_keeps_the_only_active_slot()
    {
        var (p, carol) = await NewProjectAsync();
        await using (var db = App.Db())
        {
            db.Sprints.Add(new Sprint { ProjectId = p.Id, Name = "Ours", State = SprintState.Active, StartDate = DateTime.UtcNow.Date, StartedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }
        Fake.ActiveSprintEnd = "2099-01-01T00:00:00.000Z";
        var job = await RunAsync(p, carol);

        Assert.Equal(TrackerImportStatus.Done, job.Status);
        var sprints = await SprintsAsync(p);
        Assert.Equal(SprintState.Active, sprints["Ours"].State);
        Assert.Equal(SprintState.Planned, sprints["Sprint 2"].State);          // still running in Jira, so planned here
        Assert.Contains(TrackerImportService.WarningsOf(job), w => w.Contains("Sprint 2") && w.Contains("only one active sprint") && w.Contains("planned"));
        await using var db2 = App.Db();
        Assert.Equal(1, await db2.Sprints.CountAsync(x => x.ProjectId == p.Id && x.State == SprintState.Active));
    }

    [Fact]
    public async Task Running_again_adds_no_sprints_and_leaves_changed_ones_alone()
    {
        var (p, carol) = await NewProjectAsync();
        await RunAsync(p, carol);
        await using (var db = App.Db())
        {
            var s2 = await db.Sprints.SingleAsync(x => x.ProjectId == p.Id && x.Name == "Sprint 2");
            s2.Goal = "Edited here";
            s2.State = SprintState.Completed; s2.CompletedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }
        var again = await RunAsync(p, carol);

        Assert.Equal(TrackerImportStatus.Done, again.Status);
        var sprints = await SprintsAsync(p);
        Assert.Equal(3, sprints.Count);
        Assert.Equal("Edited here", sprints["Sprint 2"].Goal);
        Assert.Equal(SprintState.Completed, sprints["Sprint 2"].State);
        await using var db2 = App.Db();
        Assert.Equal(3, await db2.ImportedItems.CountAsync(i => i.ProjectId == p.Id && i.Kind == ImportedKind.Sprint));
    }

    [Fact]
    public async Task Without_the_agile_api_sprints_come_from_the_issue_field()
    {
        Fake.AgileAvailable = false;
        var (p, carol) = await NewProjectAsync();
        var job = await RunAsync(p, carol);

        Assert.Equal(TrackerImportStatus.Done, job.Status);
        var sprints = await SprintsAsync(p);
        Assert.Equal(SprintState.Completed, sprints["Sprint 1"].State);
        Assert.Equal(new DateTime(2024, 4, 14), sprints["Sprint 1"].EndDate);
        Assert.Equal(SprintState.Active, sprints["Sprint 2"].State);
        Assert.Equal(SprintState.Planned, sprints["Sprint 3"].State);
        var issues = await IssuesAsync(p);
        Assert.Equal(sprints["Sprint 2"].Id, issues["Payment form"].SprintId);
        await using var db = App.Db();
        Assert.DoesNotContain(await db.Labels.Where(l => l.ProjectId == p.Id).Select(l => l.Name).ToListAsync(), n => n.StartsWith("sprint:"));
    }

    [Fact]
    public async Task A_sprint_that_cannot_be_listed_falls_back_to_a_label_with_a_note()
    {
        Fake.OrphanSprint = true;
        var (p, carol) = await NewProjectAsync();
        var job = await RunAsync(p, carol);

        Assert.Equal(TrackerImportStatus.Done, job.Status);
        var task = (await IssuesAsync(p))["Write the docs"];
        Assert.Null(task.SprintId);
        Assert.Contains("sprint: Ghost sprint", task.Labels.Select(l => l.Name));
        Assert.Contains(TrackerImportService.WarningsOf(job), w => w.Contains("PROJ-5") && w.Contains("Ghost sprint"));
        Assert.Equal(3, (await SprintsAsync(p)).Count);
    }

    // ------------------------------------------------------------ attachment files

    private async Task<Dictionary<string, Attachment>> AttachmentsOfAsync(Project p, string title)
    {
        await using var db = App.Db();
        var issue = await db.Issues.AsNoTracking().SingleAsync(i => i.ProjectId == p.Id && i.Title == title);
        return (await db.Attachments.AsNoTracking().Where(a => a.IssueId == issue.Id).ToListAsync()).ToDictionary(a => a.FileName);
    }

    [Fact]
    public async Task Without_the_option_no_attachment_file_is_requested()
    {
        var (p, carol) = await NewProjectAsync();
        var job = await RunAsync(p, carol);

        Assert.Equal(TrackerImportStatus.Done, job.Status);
        lock (Fake.FileRequests) Assert.Empty(Fake.FileRequests);
        Assert.Empty(await AttachmentsOfAsync(p, "Payment form"));
    }

    [Fact]
    public async Task Attachment_files_are_copied_with_credentials_redirects_limits_and_a_warning_per_failure()
    {
        var (p, carol) = await NewProjectAsync();
        var job = await RunAsync(p, carol, settings: new TrackerImportSettings { IncludeAttachments = true });

        Assert.Equal(TrackerImportStatus.Done, job.Status);
        Assert.Null(job.Error);
        var files = await AttachmentsOfAsync(p, "Payment form");
        Assert.Equal(["screenshot.png", "spec.pdf"], files.Keys.Order().ToList());

        var png = files["screenshot.png"];
        Assert.Equal("image/png", png.ContentType);                      // sniffed from the bytes
        Assert.Equal(FakeFiles.Png.Length, png.Size);
        Assert.Equal(FakeFiles.Sha256(FakeFiles.Png), png.Sha256);
        Assert.Equal(carol, png.UploaderId);
        Assert.False(png.FromComment);
        Assert.Equal(FakeFiles.Png, await File.ReadAllBytesAsync(Path.Combine(App.AttachmentRoot, png.StoredName)));
        var pdf = files["spec.pdf"];                                       // came through a redirect to the CDN
        Assert.Equal(FakeFiles.Sha256(FakeFiles.Pdf), pdf.Sha256);
        Assert.Equal(FakeFiles.Pdf, await File.ReadAllBytesAsync(Path.Combine(App.AttachmentRoot, pdf.StoredName)));

        // the credentials went to Jira, and never to the second host
        lock (App.Cdn.Requests)
        {
            Assert.Contains(App.Cdn.Requests, r => r.PathAndQuery == "/media/spec.pdf?token=signed123");
            Assert.All(App.Cdn.Requests, r => { Assert.Null(r.Authorization); Assert.Null(r.PrivateToken); });
        }
        lock (Fake.Requests) Assert.All(Fake.Requests, r => Assert.Equal(Fake.ExpectedAuth, r.Auth));

        // each failure is one warning that names the file and the reason
        var warnings = TrackerImportService.WarningsOf(job);
        Assert.Contains(warnings, w => w.Contains("PROJ-2") && w.Contains("huge.bin") && w.Contains("over the limit of 4 KB"));
        Assert.Contains(warnings, w => w.Contains("liar.bin") && w.Contains("over the limit"));       // its declared size was a lie; the stream stopped at the limit
        Assert.Contains(warnings, w => w.Contains("run.exe") && w.Contains("BlockedAttachmentExtensions"));
        Assert.Contains(warnings, w => w.Contains("gone.txt") && w.Contains("no longer has the file"));
        Assert.DoesNotContain(warnings, w => w.Contains("screenshot.png") || w.Contains("spec.pdf"));
        lock (Fake.FileRequests) { Assert.DoesNotContain("10102", Fake.FileRequests); Assert.DoesNotContain("10104", Fake.FileRequests); }   // known too big / blocked: never downloaded

        // the issue lists only what could not be copied
        var story = (await IssuesAsync(p))["Payment form"];
        Assert.DoesNotContain("[screenshot.png]", story.Description);
        Assert.Contains("(not copied)", story.Description);
        Assert.Contains("[huge.bin]", story.Description);

        // nothing leaked: not into warnings, the job, nor the audit trail
        await AssertNoSecretsStoredAsync(p, job, "signed123");
        await using var db = App.Db();
        var audit = await db.AuditEvents.Where(a => a.ProjectId == p.Id && a.Action == "project.tracker_imported").Select(a => a.Details).SingleAsync();
        Assert.Contains("\"attachments\":2", audit);
        Assert.DoesNotContain(App.Cdn.Url, string.Join("|", warnings));
        Assert.Equal(2, await db.ImportedItems.CountAsync(i => i.ProjectId == p.Id && i.Kind == ImportedKind.Attachment));
    }

    [Fact]
    public async Task Running_again_does_not_copy_a_file_twice()
    {
        var (p, carol) = await NewProjectAsync();
        var settings = new TrackerImportSettings { IncludeAttachments = true };
        await RunAsync(p, carol, settings: settings);
        var again = await RunAsync(p, carol, settings: settings);

        Assert.Equal(TrackerImportStatus.Done, again.Status);
        Assert.Equal(2, (await AttachmentsOfAsync(p, "Payment form")).Count);
        lock (Fake.FileRequests)
        {
            // the two copied files aren't fetched again; the failed ones are tried again (the 404 and the liar)
            Assert.Equal(1, Fake.FileRequests.Count(id => id == "10100"));
            Assert.Equal(1, Fake.FileRequests.Count(id => id == "10101"));
            Assert.Equal(2, Fake.FileRequests.Count(id => id == "10105"));
        }
    }

    [Fact]
    public async Task The_whole_import_stops_copying_at_its_attachment_budget()
    {
        var options = App.Services.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<TrackerImportOptions>>().CurrentValue;
        var before = options.MaxAttachmentBytes;
        options.MaxAttachmentBytes = 30;   // the png (24 bytes) fits; the pdf (23 more) doesn't
        try
        {
            var (p, carol) = await NewProjectAsync();
            var job = await RunAsync(p, carol, settings: new TrackerImportSettings { IncludeAttachments = true });

            Assert.Equal(TrackerImportStatus.Done, job.Status);
            Assert.Equal(["screenshot.png"], (await AttachmentsOfAsync(p, "Payment form")).Keys.ToList());
            Assert.Contains(TrackerImportService.WarningsOf(job), w => w.Contains("spec.pdf") && w.Contains("MaxAttachmentBytes"));
        }
        finally { options.MaxAttachmentBytes = before; }
    }

    [Fact]
    public async Task An_issue_that_is_full_gets_a_warning_and_existing_issues_are_never_edited()
    {
        var (p, carol) = await NewProjectAsync();
        await RunAsync(p, carol);                                   // the issues exist, without files
        int storyId;
        string description;
        await using (var db = App.Db())
        {
            var story = await db.Issues.AsNoTracking().SingleAsync(i => i.ProjectId == p.Id && i.Title == "Payment form");
            (storyId, description) = (story.Id, story.Description!);
            for (var i = 0; i < 49; i++)
                db.Attachments.Add(new Attachment { IssueId = storyId, StoredName = Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)), FileName = $"old{i}.txt", Size = 1, Sha256 = "x", UploaderId = carol });
            await db.SaveChangesAsync();
        }
        var job = await RunAsync(p, carol, settings: new TrackerImportSettings { IncludeAttachments = true });

        Assert.Equal(TrackerImportStatus.Done, job.Status);
        await using var db2 = App.Db();
        Assert.Equal(50, await db2.Attachments.CountAsync(a => a.IssueId == storyId));   // one real copy filled it up
        Assert.Contains(TrackerImportService.WarningsOf(job), w => w.Contains("at most 50 attachments"));
        Assert.Equal(description, (await db2.Issues.AsNoTracking().SingleAsync(i => i.Id == storyId)).Description);
    }

    private async Task AssertNoSecretsStoredAsync(Project p, TrackerImportJob job, params string[] extra)
    {
        var secrets = Fake.Secrets.Concat(extra).ToArray();
        await using var db = App.Db();
        var audit = string.Join("|", await db.AuditEvents.Where(a => a.ProjectId == p.Id).Select(a => a.Details ?? "").ToListAsync());
        var stored = string.Join("|", job.Error, job.WarningsJson, job.SettingsJson, job.SourceUrl, job.SourceProject, audit);
        foreach (var secret in secrets) Assert.DoesNotContain(secret, stored);
        Assert.DoesNotContain(FakeJira.Email, stored);
    }
}

public class JiraCloudImportTests(JiraCloudApp app) : JiraImportScenarios<JiraCloudApp>(app)
{
    [Fact]
    public async Task Cloud_uses_rest_v3_enhanced_search_and_approximate_count_only()
    {
        var (p, carol) = await NewProjectAsync();
        await RunAsync(p, carol);

        lock (Fake.Requests)
        {
            Assert.Contains(Fake.Requests, r => r.PathAndQuery == "/rest/api/2/serverInfo");
            Assert.DoesNotContain(Fake.Requests, r => r.PathAndQuery.StartsWith("/rest/api/2/") && r.PathAndQuery != "/rest/api/2/serverInfo");
            Assert.Equal(1, Fake.Requests.Count(r => r.PathAndQuery == "/rest/api/2/serverInfo")); // flavour detected once
            Assert.Contains(Fake.Requests, r => r.PathAndQuery.StartsWith("/rest/api/3/search/jql?") && r.PathAndQuery.Contains("nextPageToken="));
            Assert.DoesNotContain(Fake.Requests, r => r.PathAndQuery.StartsWith("/rest/api/3/search?"));
            var count = Fake.Requests.Single(r => r.Method == "POST");
            Assert.Equal("/rest/api/3/search/approximate-count", count.PathAndQuery);
            Assert.Equal("project = \"PROJ\"", System.Text.Json.Nodes.JsonNode.Parse(count.Body!)!["jql"]!.GetValue<string>());
            var issueRequest = Fake.Requests.First(r => r.PathAndQuery.Contains("issuetype"));
            Assert.Contains("customfield_10016", issueRequest.PathAndQuery);   // story point estimate
            Assert.Contains("customfield_10020", issueRequest.PathAndQuery);   // sprint
            Assert.Contains("customfield_10014", issueRequest.PathAndQuery);   // epic link
            Assert.DoesNotContain("customfield_10099", issueRequest.PathAndQuery);
            Assert.Contains("ORDER%20BY%20created%20ASC", issueRequest.PathAndQuery);
        }
    }

    [Fact]
    public async Task Cloud_people_are_matched_by_name_only_because_emails_are_hidden()
    {
        var (p, carol) = await NewProjectAsync();
        var job = await RunAsync(p, carol);

        Assert.Equal(TrackerImportStatus.Done, job.Status);
        await using var db = App.Db();
        var epic = await db.Issues.AsNoTracking().SingleAsync(i => i.ProjectId == p.Id && i.Title == "Checkout revamp");
        Assert.Equal(carol, epic.ReporterId); // falls back to whoever ran the import
        Assert.Contains("Bob Builder", epic.Description);
        Assert.Contains(TrackerImportService.WarningsOf(job), w => w.Contains("no account"));
    }

    [Fact]
    public async Task Cloud_adf_mentions_become_text()
    {
        var (p, carol) = await NewProjectAsync();
        await RunAsync(p, carol);
        await using var db = App.Db();
        var story = await db.Issues.AsNoTracking().SingleAsync(i => i.ProjectId == p.Id && i.Title == "Payment form");
        Assert.Contains("@Bob Builder", story.Description);
    }
}

public class JiraServerImportTests(JiraServerApp app) : JiraImportScenarios<JiraServerApp>(app)
{
    [Fact]
    public async Task Server_uses_rest_v2_search_with_start_at_and_the_epic_link_field()
    {
        var (p, carol) = await NewProjectAsync();
        await RunAsync(p, carol);

        lock (Fake.Requests)
        {
            Assert.DoesNotContain(Fake.Requests, r => r.PathAndQuery.StartsWith("/rest/api/3/"));
            Assert.Contains(Fake.Requests, r => r.PathAndQuery.StartsWith("/rest/api/2/search?") && r.PathAndQuery.Contains("startAt=2"));
            Assert.Contains(Fake.Requests, r => r.PathAndQuery.StartsWith("/rest/api/2/search?") && r.PathAndQuery.Contains("startAt=4"));
            Assert.DoesNotContain(Fake.Requests, r => r.Method == "POST");
            var issueRequest = Fake.Requests.First(r => r.PathAndQuery.Contains("issuetype"));
            Assert.Contains("customfield_10002", issueRequest.PathAndQuery);   // Story Points
            Assert.Contains("customfield_10004", issueRequest.PathAndQuery);   // Sprint
            Assert.Contains("customfield_10014", issueRequest.PathAndQuery);   // Epic Link
        }
    }

    [Fact]
    public async Task Server_user_names_match_site_users_and_wiki_markup_is_converted()
    {
        var (p, carol) = await NewProjectAsync();
        var job = await RunAsync(p, carol);

        Assert.Equal(TrackerImportStatus.Done, job.Status);
        await using var db = App.Db();
        var epic = await db.Issues.AsNoTracking().SingleAsync(i => i.ProjectId == p.Id && i.Title == "Checkout revamp");
        var bug = await db.Issues.AsNoTracking().SingleAsync(i => i.ProjectId == p.Id && i.Title == "Card declined crash");
        var story = await db.Issues.AsNoTracking().SingleAsync(i => i.ProjectId == p.Id && i.Title == "Payment form");
        Assert.Equal(App.UserId("bob"), epic.ReporterId);
        Assert.Equal(App.UserId("bob"), bug.AssigneeId);
        Assert.Contains("@bob", story.Description);
        Assert.Contains("Rebuild the checkout **flow**.", epic.Description);
    }

    [Fact]
    public async Task A_context_path_is_kept_in_every_request()
    {
        using var fake = new FakeJira(JiraFlavour.Server, "/jira");
        var (p, carol) = await NewProjectAsync();
        var input = new TrackerImportInput { Source = TrackerSourceKind.Jira, BaseUrl = fake.Url + "/", Project = FakeJira.Key, Username = FakeJira.Email, Secret = FakeJira.ApiToken };
        var job = await RunAsync(p, carol, input);

        Assert.Equal(TrackerImportStatus.Done, job.Status);
        Assert.Equal(5, job.IssuesCreated);
        lock (fake.Requests) Assert.All(fake.Requests, r => Assert.StartsWith("/jira/rest/", r.PathAndQuery));
        await using var db = App.Db();
        Assert.Contains(await db.Issues.AsNoTracking().Where(i => i.ProjectId == p.Id).Select(i => i.Description).ToListAsync(), d => d != null && d.Contains(fake.Url + "/browse/PROJ-1"));
    }
}

/// <summary>The SSRF guard with private networks off (the default outside Development): a loopback Jira is refused.</summary>
public class JiraSsrfApp : TestApp
{
    public FakeJira Fake { get; } = new(JiraFlavour.Server);
    protected override void AddSettings(IDictionary<string, string?> settings)
    {
        settings["TrackerImport:AllowPrivateNetworks"] = "false";
        settings["TrackerImport:Enabled"] = "false";
    }
    protected override void Dispose(bool disposing) { base.Dispose(disposing); if (disposing) Fake.Dispose(); }
}

public class JiraSsrfTests(JiraSsrfApp app) : IClassFixture<JiraSsrfApp>
{
    [Fact]
    public async Task A_loopback_jira_is_refused_before_any_request()
    {
        var p = await app.CreateProjectAsync("carol");
        var carol = app.UserId("carol");
        var input = new TrackerImportInput { Source = TrackerSourceKind.Jira, BaseUrl = app.Fake.Url, Project = "PROJ", Username = FakeJira.Email, Secret = FakeJira.ApiToken };
        await using var s = app.Scope(carol);
        var check = await Assert.ThrowsAsync<InvalidOperationException>(() => s.Get<TrackerImportService>().CheckAsync(p.Id, input, carol));
        Assert.Contains("can't be on this server", check.Message);
        await Assert.ThrowsAsync<InvalidOperationException>(() => s.Get<TrackerImportService>().StartAsync(p.Id, input, carol));
        lock (app.Fake.Requests) Assert.Empty(app.Fake.Requests);
    }

    [Fact]
    public async Task An_address_is_required_for_jira()
    {
        var p = await app.CreateProjectAsync("carol");
        var carol = app.UserId("carol");
        await using var s = app.Scope(carol);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => s.Get<TrackerImportService>().CheckAsync(p.Id,
            new TrackerImportInput { Source = TrackerSourceKind.Jira, Project = "PROJ", Secret = "x" }, carol));
        Assert.Contains("Jira address", ex.Message);
    }
}
