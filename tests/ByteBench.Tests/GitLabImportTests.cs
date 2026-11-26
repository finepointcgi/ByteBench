using ByteBench.Data;
using ByteBench.Services.TrackerImport;
using ByteBench.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ByteBench.Tests;

/// <summary>The real reader factory and HTTP client against a local fake GitLab; imports are driven through the runner directly.</summary>
public sealed class GitLabImportApp : TestApp
{
    protected override void AddSettings(IDictionary<string, string?> settings)
    {
        settings["TrackerImport:Enabled"] = "false";
        settings["TrackerImport:AllowPrivateNetworks"] = "true";
    }
}

public sealed class GitLabStrictApp : TestApp
{
    protected override void AddSettings(IDictionary<string, string?> settings)
    {
        settings["TrackerImport:Enabled"] = "false";
        settings["TrackerImport:AllowPrivateNetworks"] = "false";
    }
}

public class GitLabImportTests(GitLabImportApp app, GitLabStrictApp strict) : IClassFixture<GitLabImportApp>, IClassFixture<GitLabStrictApp>
{
    private static TrackerImportInput Input(FakeGitLab fake, string? secret = FakeGitLab.Token, string project = FakeGitLab.ProjectPath, TrackerImportSettings? settings = null) => new()
    {
        Source = TrackerSourceKind.GitLab, BaseUrl = fake.Url, Project = project, Secret = secret, Settings = settings ?? new(),
    };

    private async Task<(Project P, int Carol)> NewProjectAsync()
    {
        var p = await app.CreateProjectAsync("carol", ProjectVisibility.Private, ("bob", ProjectRole.Developer));
        return (p, app.UserId("carol"));
    }

    private async Task<TrackerImportJob> RunAsync(Project p, int actor, TrackerImportInput input)
    {
        await using var s = app.Scope(actor);
        var job = await s.Get<TrackerImportService>().StartAsync(p.Id, input, actor);
        await app.Services.GetRequiredService<TrackerImportRunner>().RunAsync(job.Id);
        await using var db = app.Db();
        return await db.TrackerImportJobs.AsNoTracking().FirstAsync(j => j.Id == job.Id);
    }

    private async Task<string> AuditTextAsync(int projectId)
    {
        await using var db = app.Db();
        return string.Join("|", await db.AuditEvents.Where(a => a.ProjectId == projectId).Select(a => a.Details ?? "").ToListAsync());
    }

    private GitLabReader NewReader(FakeGitLab fake) =>
        new(new SourceConnection(TrackerSourceKind.GitLab, fake.Url, FakeGitLab.ProjectPath, null, FakeGitLab.Token), app.Services.GetRequiredService<TrackerHttp>());

    [Fact]
    public async Task Check_shows_the_project_counts_samples_statuses_labels_and_milestones()
    {
        using var fake = new FakeGitLab();
        var (p, carol) = await NewProjectAsync();
        await using var s = app.Scope(carol);
        var check = await s.Get<TrackerImportService>().CheckAsync(p.Id, Input(fake), carol);

        Assert.Equal("Acme / Team / Widgets", check.Probe.ProjectName);
        Assert.Equal("The widget factory", check.Probe.Description);
        Assert.Equal(5, check.Probe.IssueCount);
        Assert.Equal(["Login fails", "Crash on save", "Secret roadmap"], check.Probe.SampleTitles);
        Assert.Equal(["Open", "Closed"], check.SourceStatuses.Select(x => x.Name));
        Assert.Equal(3, check.LabelCount);
        Assert.Equal(2, check.MilestoneCount);
        Assert.All(fake.Requests, r => Assert.Equal(FakeGitLab.Token, r.Token));
        Assert.Contains(fake.Requests, r => r.Path == "/api/v4/projects/" + FakeGitLab.ProjectPath);
    }

    [Fact]
    public async Task Imports_everything_end_to_end_across_pages()
    {
        using var fake = new FakeGitLab();
        var (p, carol) = await NewProjectAsync();
        var job = await RunAsync(p, carol, Input(fake));

        Assert.Equal(TrackerImportStatus.Done, job.Status);
        Assert.Equal(6, job.IssuesCreated); // 5 issues + the epic, once
        Assert.Equal(2, job.CommentsCreated); // the system note is skipped
        Assert.Equal(2, job.LinksCreated); // relates_to (reported by both sides) and blocks
        Assert.Equal(2, job.MilestonesCreated);
        Assert.Contains(fake.Requests, r => r.Path.EndsWith("/issues") && r.Page == "3"); // 5 issues at 2 per page
        Assert.DoesNotContain(fake.Requests, r => r.Path.Contains("internal.gitlab.invalid"));

        await using var db = app.Db();
        var issues = await db.Issues.AsNoTracking().Include(i => i.Labels).Include(i => i.Comments).Where(i => i.ProjectId == p.Id).ToListAsync();
        var epic = Assert.Single(issues, i => i.Type == IssueType.Epic);
        Assert.Equal("Auth overhaul", epic.Title);
        var login = issues.Single(i => i.Title == "Login fails");
        var crash = issues.Single(i => i.Title == "Crash on save");
        var dark = issues.Single(i => i.Title == "Dark mode");
        Assert.Equal(epic.Id, login.ParentId);
        Assert.Equal(epic.Id, crash.ParentId);
        Assert.Null(dark.ParentId);

        Assert.Equal(IssueType.Bug, login.Type);
        Assert.Equal(IssuePriority.High, login.Priority);
        Assert.Equal(5, login.StoryPoints);
        Assert.Equal(app.UserId("bob"), login.AssigneeId);
        Assert.Equal(app.UserId("bob"), login.ReporterId);
        Assert.Equal(["bug", "priority::high", "ui"], login.Labels.Select(l => l.Name).Order(StringComparer.Ordinal).ToList());
        Assert.NotNull(login.MilestoneId);
        Assert.Contains("](https://gitlab.example.com/acme/team/widgets/uploads/ab12/shot.png)", login.Description);
        Assert.Contains("](https://gitlab.example.com/acme/team/widgets/uploads/ab12/log.txt)", login.Description);
        Assert.DoesNotContain("](/uploads/", login.Description);

        Assert.Equal(IssueType.Bug, crash.Type); // incident
        Assert.Equal(IssueStatus.Done, crash.Status);
        Assert.Equal(new DateTime(2024, 6, 1, 8, 30, 0, DateTimeKind.Utc), crash.ClosedAt);
        Assert.Equal(new DateTime(2024, 5, 30), crash.DueDate?.Date);
        Assert.Null(crash.MilestoneId); // a group milestone
        Assert.Equal(100, dark.StoryPoints);
        Assert.Equal(IssueType.Feature, dark.Type);
        Assert.Equal(IssuePriority.Low, dark.Priority);
        Assert.Equal(IssuePriority.Highest, issues.Single(i => i.Title == "Secret roadmap").Priority);
        Assert.Equal(IssueType.Feature, issues.Single(i => i.Title == "Old thing").Type);

        Assert.Equal(2, login.Comments.Count);
        Assert.DoesNotContain(login.Comments, c => c.Body.Contains("changed the description"));
        Assert.Equal(app.UserId("bob"), login.Comments.Single(c => c.Body.Contains("I can reproduce")).AuthorId);
        var stranger = login.Comments.Single(c => c.Body.Contains("Fixed"));
        Assert.Equal(carol, stranger.AuthorId);
        Assert.Contains("Some Stranger (@stranger)", stranger.Body);
        Assert.Contains("](https://gitlab.example.com/acme/team/widgets/uploads/ab12/fixed.png)", stranger.Body);

        var secret = issues.Single(i => i.Title == "Secret roadmap");
        Assert.Equal(1, await db.IssueLinks.CountAsync(l => l.SourceIssueId == login.Id && l.TargetIssueId == secret.Id && l.Type == IssueLinkType.Blocks));
        Assert.Equal(1, await db.IssueLinks.CountAsync(l => (l.SourceIssueId == login.Id || l.TargetIssueId == login.Id) && l.Type == IssueLinkType.RelatesTo));
        Assert.Equal(2, await db.IssueLinks.CountAsync(l => l.SourceIssue.ProjectId == p.Id));

        var warnings = job.WarningsJson ?? "";
        Assert.Contains("Group Q2", warnings);
        Assert.Contains("was confidential in GitLab; it is visible to everyone who can see this project", warnings);
        Assert.Contains("other GitLab projects", warnings);
        Assert.Equal(0, fake.Hits("/epics"));
    }

    [Fact]
    public async Task The_token_is_sent_as_PRIVATE_TOKEN_and_never_leaks()
    {
        using var fake = new FakeGitLab();
        var (p, carol) = await NewProjectAsync();
        var job = await RunAsync(p, carol, Input(fake));
        Assert.All(fake.Requests, r => Assert.Equal(FakeGitLab.Token, r.Token));
        Assert.DoesNotContain(FakeGitLab.Token, job.Error ?? "");
        Assert.DoesNotContain(FakeGitLab.Token, job.WarningsJson ?? "");
        Assert.DoesNotContain(FakeGitLab.Token, job.SettingsJson ?? "");
        Assert.DoesNotContain(FakeGitLab.Token, await AuditTextAsync(p.Id));

        // A rejected token: the message says so and doesn't echo either token.
        fake.ExpectedToken = "glpat-someone-else";
        var bad = await RunAsync(p, carol, Input(fake));
        Assert.Equal(TrackerImportStatus.Failed, bad.Status);
        Assert.Contains("rejected the credentials", bad.Error);
        Assert.DoesNotContain(FakeGitLab.Token, bad.Error ?? "");
        Assert.DoesNotContain("someone-else", bad.Error ?? "");
        Assert.DoesNotContain(FakeGitLab.Token, bad.WarningsJson ?? "");
        Assert.DoesNotContain(FakeGitLab.Token, await AuditTextAsync(p.Id));
    }

    [Fact]
    public async Task A_public_project_needs_no_token()
    {
        using var fake = new FakeGitLab { Public = true };
        var (p, carol) = await NewProjectAsync();
        var job = await RunAsync(p, carol, Input(fake, secret: null));
        Assert.Equal(TrackerImportStatus.Done, job.Status);
        Assert.All(fake.Requests, r => Assert.Null(r.Token));
    }

    [Fact]
    public async Task A_public_project_whose_labels_comments_and_links_need_a_token_still_imports_its_issues()
    {
        // gitlab.com answers 401 for these four lists without a token even on public projects; that used to fail the whole import.
        using var fake = new FakeGitLab { Public = true, SignedInOnlyExtras = true };
        var (p, carol) = await NewProjectAsync();
        await using (var s = app.Scope(carol))
        {
            var check = await s.Get<TrackerImportService>().CheckAsync(p.Id, Input(fake, secret: null), carol);
            Assert.Contains("No token", check.Probe.Warning);
        }

        var job = await RunAsync(p, carol, Input(fake, secret: null));
        Assert.Equal(TrackerImportStatus.Done, job.Status);
        Assert.True(job.IssuesCreated > 0);
        Assert.Equal(0, job.CommentsCreated);
        Assert.Contains("comments were not imported", job.WarningsJson);
        Assert.Contains("links between issues were not imported", job.WarningsJson);
        Assert.True(fake.Hits("/notes") <= 1, "comments are not requested again for every issue once gitlab.com refused them");
    }

    [Fact]
    public async Task A_numeric_project_id_and_a_relative_url_root_work()
    {
        using var fake = new FakeGitLab { RootPath = "/gitlab" };
        var (p, carol) = await NewProjectAsync();
        var job = await RunAsync(p, carol, Input(fake, project: "42"));
        Assert.Equal(TrackerImportStatus.Done, job.Status);
        Assert.Equal(6, job.IssuesCreated);
        Assert.Contains(fake.Requests, r => r.Path == "/gitlab/api/v4/projects/42/issues_statistics");
    }

    [Fact]
    public async Task Closed_issues_can_be_left_out()
    {
        using var fake = new FakeGitLab();
        var (p, carol) = await NewProjectAsync();
        var job = await RunAsync(p, carol, Input(fake, settings: new TrackerImportSettings { IncludeClosed = false }));
        Assert.Equal(TrackerImportStatus.Done, job.Status);
        Assert.Equal(4, job.IssuesCreated); // Login fails (+ its epic), Secret roadmap, Dark mode
        Assert.Contains(fake.Requests, r => r.Path.EndsWith("/issues") && r.Query.Contains("state=opened"));
        await using var db = app.Db();
        Assert.DoesNotContain(await db.Issues.Where(i => i.ProjectId == p.Id).Select(i => i.Title).ToListAsync(), t => t is "Crash on save" or "Old thing");
    }

    [Fact]
    public async Task Without_comments_no_notes_or_links_are_requested()
    {
        using var fake = new FakeGitLab();
        var (p, carol) = await NewProjectAsync();
        var job = await RunAsync(p, carol, Input(fake, settings: new TrackerImportSettings { IncludeComments = false }));
        Assert.Equal(TrackerImportStatus.Done, job.Status);
        Assert.Equal(0, job.CommentsCreated);
        Assert.Equal(0, job.LinksCreated);
        Assert.Equal(0, fake.Hits("/notes"));
        Assert.Equal(0, fake.Hits("/links"));
    }

    [Fact]
    public async Task MaxIssues_stops_reading_early()
    {
        using var fake = new FakeGitLab();
        using var reader = NewReader(fake);
        var got = new List<SourceIssue>();
        await foreach (var i in reader.ReadIssuesAsync(new SourceReadOptions(true, false, false, 3), CancellationToken.None)) got.Add(i);
        Assert.Equal(3, got.Count(i => i.Type != IssueType.Epic));
        Assert.DoesNotContain(fake.Requests, r => r.Path.EndsWith("/issues") && r.Page == "3");
    }

    [Fact]
    public async Task The_reader_maps_fields_of_the_neutral_model()
    {
        using var fake = new FakeGitLab();
        using var reader = NewReader(fake);
        var all = new List<SourceIssue>();
        await foreach (var i in reader.ReadIssuesAsync(new SourceReadOptions(true, true, false, 100), CancellationToken.None)) all.Add(i);

        // The epic comes once, before its first child, and has the neutral shape.
        Assert.Equal("epic:900", all[0].ExternalId);
        Assert.Equal("&3", all[0].DisplayKey);
        Assert.Equal(IssueType.Epic, all[0].Type);
        Assert.Null(all[0].Body);
        Assert.Equal(fake.Origin + "/groups/acme/-/epics/3", all[0].Url);
        Assert.Equal(1, all.Count(i => i.ExternalId == "epic:900"));
        Assert.Equal("epic:900", all[1].ParentExternalId);

        var login = all.Single(i => i.ExternalId == "1001");
        Assert.Equal("#1", login.DisplayKey);
        Assert.Equal("https://gitlab.example.com/acme/team/widgets/-/issues/1", login.Url);
        Assert.Equal("bob", login.Author?.Login);
        Assert.Equal("bob", login.Assignee?.Login);
        Assert.Equal("7", login.MilestoneId);
        Assert.Equal(2, login.Links.Count);
        Assert.Contains(new SourceLink("1002", IssueLinkType.RelatesTo), login.Links);
        Assert.Contains(new SourceLink("1003", IssueLinkType.Blocks), login.Links);
        // is_blocked_by is reported as "blocks" by the other issue, so it isn't recorded here; other-project links are dropped.
        var secret = all.Single(i => i.ExternalId == "1003");
        Assert.Empty(secret.Links);
        Assert.Contains(secret.Notes, n => n.Contains("other GitLab projects"));
        Assert.Contains(secret.Notes, n => n.Contains("confidential"));
        Assert.Null(all.Single(i => i.ExternalId == "1004").Body);
        Assert.Equal(IssueStatus.Done, all.Single(i => i.ExternalId == "1005").Category);
        Assert.Contains(all.Single(i => i.ExternalId == "1002").Notes, n => n.Contains("Group Q2"));

        var milestones = await reader.ReadMilestonesAsync(CancellationToken.None);
        Assert.Equal(["7", "8"], milestones.Select(m => m.ExternalId));
        Assert.True(milestones[1].Closed);
        Assert.Equal(new DateTime(2024, 9, 1), milestones[0].DueDate);
        var labels = await reader.ReadLabelsAsync(CancellationToken.None);
        Assert.Equal(["bug", "priority::high", "ui"], labels.Select(l => l.Name));
    }

    [Fact]
    public async Task A_rate_limit_is_waited_out_then_the_import_succeeds()
    {
        using var fake = new FakeGitLab { RateLimitOnce = true };
        var hooks = app.Services.GetRequiredService<TrackerImportTestHooks>();
        var waits = new List<TimeSpan>();
        hooks.Delay = (t, _) => { lock (waits) waits.Add(t); return Task.CompletedTask; };
        try
        {
            var (p, carol) = await NewProjectAsync();
            var job = await RunAsync(p, carol, Input(fake));
            Assert.Equal(TrackerImportStatus.Done, job.Status);
            Assert.Equal(6, job.IssuesCreated);
            lock (waits) Assert.Contains(waits, w => w >= TimeSpan.FromSeconds(1));
        }
        finally { hooks.Delay = null; }
    }

    [Fact]
    public async Task Problems_are_reported_in_words()
    {
        using var fake = new FakeGitLab();
        var (p, carol) = await NewProjectAsync();
        await using var s = app.Scope(carol);
        var service = s.Get<TrackerImportService>();

        var missing = await Assert.ThrowsAsync<InvalidOperationException>(() => service.CheckAsync(p.Id, Input(fake, project: "acme/nothing"), carol));
        Assert.Contains("doesn't exist", missing.Message);

        fake.ExpectedToken = "glpat-another";
        var rejected = await Assert.ThrowsAsync<InvalidOperationException>(() => service.CheckAsync(p.Id, Input(fake), carol));
        Assert.Contains("rejected the credentials", rejected.Message);
        Assert.DoesNotContain(FakeGitLab.Token, rejected.Message);
        fake.ExpectedToken = FakeGitLab.Token;

        fake.IssuesEnabled = false;
        var off = await Assert.ThrowsAsync<InvalidOperationException>(() => service.CheckAsync(p.Id, Input(fake), carol));
        Assert.Contains("issues turned off", off.Message);
    }

    [Fact]
    public async Task A_loopback_address_is_refused_unless_private_networks_are_allowed()
    {
        using var fake = new FakeGitLab();
        var p = await strict.CreateProjectAsync("carol", ProjectVisibility.Private);
        var carol = strict.UserId("carol");
        await using var s = strict.Scope(carol);
        var service = s.Get<TrackerImportService>();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.CheckAsync(p.Id, Input(fake), carol));
        Assert.Contains("can't be on", ex.Message);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.StartAsync(p.Id, Input(fake), carol));
        Assert.Empty(fake.Requests);
    }
}
