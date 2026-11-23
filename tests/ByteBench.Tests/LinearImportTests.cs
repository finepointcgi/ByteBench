using ByteBench.Data;
using ByteBench.Services.TrackerImport;
using ByteBench.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace ByteBench.Tests;

/// <summary>The real reader factory and HTTP client against a local fake Linear GraphQL API; imports are driven through the runner directly.</summary>
public class LinearImportTests(SaasImportApp app, SaasImportStrictApp strict) : IClassFixture<SaasImportApp>, IClassFixture<SaasImportStrictApp>
{
    private readonly ImportTestKit kit = new(app);

    private static TrackerImportInput Input(FakeLinear fake, string project = FakeLinear.TeamKey, string? secret = FakeLinear.ApiKey, TrackerImportSettings? settings = null) => new()
    {
        Source = TrackerSourceKind.Linear, BaseUrl = fake.Origin, Project = project, Secret = secret, Settings = settings ?? new(),
    };

    private LinearReader NewReader(FakeLinear fake, string? secret = FakeLinear.ApiKey) =>
        new(new SourceConnection(TrackerSourceKind.Linear, fake.Origin, FakeLinear.TeamKey, null, secret), app.Services.GetRequiredService<TrackerHttp>());

    [Fact]
    public async Task Check_shows_the_team_counts_samples_states_labels_and_projects()
    {
        using var fake = new FakeLinear();
        var (p, carol) = await kit.NewProjectAsync();
        await using var s = app.Scope(carol);
        var check = await s.Get<TrackerImportService>().CheckAsync(p.Id, Input(fake), carol);

        Assert.Equal("Engineering (ENG)", check.Probe.ProjectName);
        Assert.Equal("The engineers", check.Probe.Description);
        Assert.Equal(6, check.Probe.IssueCount);
        Assert.Equal(["Login fails", "Crash on save", "Dropped idea"], check.Probe.SampleTitles);
        Assert.Equal(["Backlog", "Todo", "In Progress", "Done", "Canceled"], check.SourceStatuses.Select(x => x.Name));
        Assert.Equal([IssueStatus.Todo, IssueStatus.Todo, IssueStatus.InProgress, IssueStatus.Done, IssueStatus.Done], check.SourceStatuses.Select(x => x.Category));
        Assert.Equal(3, check.LabelCount); // paged, two per page
        Assert.Equal(2, check.MilestoneCount);
        Assert.Contains(fake.Requests, r => r.Body.Contains("teams(first:1"));
    }

    [Fact]
    public async Task Imports_everything_end_to_end_across_pages()
    {
        using var fake = new FakeLinear();
        var (p, carol) = await kit.NewProjectAsync();
        var job = await kit.RunAsync(p, carol, Input(fake));

        Assert.Equal(TrackerImportStatus.Done, job.Status);
        Assert.Equal(6, job.IssuesCreated);
        Assert.Equal(12, job.CommentsCreated); // 10 inline plus 2 from the follow-up query
        Assert.Equal(2, job.LinksCreated); // blocks and related; the duplicate repeats a pair that is already linked
        Assert.Equal(2, job.MilestonesCreated);
        Assert.Equal([2, 2, 2], fake.IssuePageSizes.Select(x => Math.Min(x, fake.PageSize))); // three backward pages
        Assert.Contains(fake.Requests, r => r.Body.Contains("issue(id:$id){ comments"));

        var issues = await kit.IssuesAsync(p.Id);
        Assert.Equal(["Login fails", "Crash on save", "Dropped idea", "Big epic", "Child of epic", "Archived thing"], issues.Select(i => i.Title)); // oldest first
        var login = issues[0];
        var crash = issues[1];
        var dropped = issues[2];
        var epic = issues[3];
        var child = issues[4];

        Assert.Equal(IssueType.Bug, login.Type);
        Assert.Equal(IssueStatus.InProgress, login.Status);
        Assert.Equal(IssuePriority.High, login.Priority);
        Assert.Equal(3, login.StoryPoints);
        Assert.Equal(app.UserId("bob"), login.AssigneeId); // matched by the email Linear shows
        Assert.Equal(carol, login.ReporterId); // creator isn't a user here
        Assert.NotNull(login.MilestoneId);
        Assert.Equal(["Area/UI", "bug", "cycle: 4"], login.Labels.Select(l => l.Name).Order(StringComparer.Ordinal).ToList());
        Assert.Contains("Body of Login fails", login.Description);
        Assert.Contains("[Spec](https://example.com/spec.pdf)", login.Description);
        Assert.Contains("[Linear ENG-1](https://linear.app/acme/issue/ENG-1)", login.Description);
        Assert.Contains("Some Stranger (@stranger)", login.Description);

        Assert.Equal(IssueType.Subtask, crash.Type);
        Assert.Equal(login.Id, crash.ParentId);
        Assert.Equal(IssuePriority.Highest, crash.Priority);
        Assert.Equal(IssueStatus.Done, crash.Status);
        Assert.Equal(new DateTime(2024, 6, 1, 8, 30, 0, DateTimeKind.Utc), crash.ClosedAt);
        Assert.Equal(new DateTime(2024, 5, 30), crash.DueDate?.Date);
        Assert.Null(crash.MilestoneId); // a project of another team

        Assert.Equal(IssueStatus.Done, dropped.Status); // canceled
        Assert.Equal(IssuePriority.Medium, dropped.Priority); // 0 = none, the project's default
        Assert.Equal(IssueType.Epic, epic.Type);
        Assert.Equal(IssueType.Feature, child.Type); // under an epic it keeps its own type
        Assert.Equal(epic.Id, child.ParentId);

        var warnings = job.WarningsJson ?? "";
        Assert.Contains("was canceled in Linear", warnings);
        Assert.Contains("project of this team, so no milestone", warnings);

        Assert.Equal(12, login.Comments.Count);
        var stranger = login.Comments.Single(c => c.Body.Contains("Comment 2"));
        Assert.Equal(carol, stranger.AuthorId);
        Assert.Contains("Some Stranger (@stranger)", stranger.Body);
        Assert.Equal(app.UserId("bob"), login.Comments.First(c => c.Body.Contains("Comment 1")).AuthorId);

        await using var db = app.Db();
        var links = db.IssueLinks.Where(l => l.SourceIssue.ProjectId == p.Id).ToList();
        Assert.Contains(links, l => l.SourceIssueId == login.Id && l.TargetIssueId == crash.Id && l.Type == IssueLinkType.Blocks);
        Assert.Contains(links, l => l.SourceIssueId == login.Id && l.TargetIssueId == dropped.Id && l.Type == IssueLinkType.RelatesTo);
    }

    [Fact]
    public async Task The_personal_key_is_sent_as_is_and_never_leaks()
    {
        using var fake = new FakeLinear();
        var (p, carol) = await kit.NewProjectAsync();
        var job = await kit.RunAsync(p, carol, Input(fake));
        Assert.Equal(TrackerImportStatus.Done, job.Status);
        Assert.All(fake.Requests, r => Assert.Equal(FakeLinear.ApiKey, r.Authorization)); // no "Bearer " for a personal key
        Assert.All(fake.Requests, r => Assert.DoesNotContain(FakeLinear.ApiKey, r.Path + r.Query + r.Body));
        Assert.DoesNotContain(FakeLinear.ApiKey, await kit.EverywhereAsync(p, job));

        fake.ExpectedAuthorization = "lin_api_someone-else";
        var bad = await kit.RunAsync(p, carol, Input(fake));
        Assert.Equal(TrackerImportStatus.Failed, bad.Status);
        Assert.Contains("rejected the credentials", bad.Error);
        var everywhere = await kit.EverywhereAsync(p, bad);
        Assert.DoesNotContain(FakeLinear.ApiKey, everywhere);
        Assert.DoesNotContain("someone-else", everywhere);
    }

    [Fact]
    public async Task An_OAuth_token_is_sent_as_a_Bearer_token()
    {
        using var fake = new FakeLinear { ExpectedAuthorization = "Bearer lin_oauth_FAKE0123456789" };
        using var reader = NewReader(fake, "lin_oauth_FAKE0123456789");
        var probe = await reader.ProbeAsync(CancellationToken.None);
        Assert.Equal("Engineering (ENG)", probe.ProjectName);
    }

    [Fact]
    public async Task GraphQL_errors_become_readable_messages()
    {
        using var fake = new FakeLinear();
        var (p, carol) = await kit.NewProjectAsync();
        await using var s = app.Scope(carol);
        var service = s.Get<TrackerImportService>();

        var missing = await Assert.ThrowsAsync<InvalidOperationException>(() => service.CheckAsync(p.Id, Input(fake, project: "NOPE"), carol));
        Assert.Contains("doesn't exist", missing.Message);
        var byId = await Assert.ThrowsAsync<InvalidOperationException>(() => service.CheckAsync(p.Id, Input(fake, project: "99999999-2222-3333-4444-555555555555"), carol));
        Assert.Contains("doesn't exist", byId.Message);
        Assert.Equal("Engineering (ENG)", (await service.CheckAsync(p.Id, Input(fake, project: FakeLinear.TeamId), carol)).Probe.ProjectName);

        fake.FailNextWith = "RATELIMITED";
        var limited = await Assert.ThrowsAsync<InvalidOperationException>(() => service.CheckAsync(p.Id, Input(fake), carol));
        Assert.Contains("rate limiting the import", limited.Message);

        fake.FailNextWith = "INVALID";
        var generic = await Assert.ThrowsAsync<InvalidOperationException>(() => service.CheckAsync(p.Id, Input(fake), carol));
        Assert.Contains("Something is off with the query", generic.Message);

        var noKey = await Assert.ThrowsAsync<InvalidOperationException>(() => service.CheckAsync(p.Id, Input(fake, secret: null), carol));
        Assert.Contains("personal API key", noKey.Message);
        var shape = await Assert.ThrowsAsync<InvalidOperationException>(() => service.CheckAsync(p.Id, Input(fake, project: "e n g"), carol));
        Assert.Contains("team key", shape.Message);
    }

    [Fact]
    public async Task A_query_Linear_calls_too_complex_is_retried_with_smaller_pages()
    {
        using var fake = new FakeLinear { ComplexityOnce = true, PageSize = 100 };
        var (p, carol) = await kit.NewProjectAsync();
        var job = await kit.RunAsync(p, carol, Input(fake));
        Assert.Equal(TrackerImportStatus.Done, job.Status);
        Assert.Equal(6, job.IssuesCreated);
        Assert.Equal([50, 25], fake.IssuePageSizes.Take(2));
    }

    [Fact]
    public async Task A_lowercase_team_key_works_and_a_rate_limit_is_waited_out()
    {
        using var fake = new FakeLinear { RateLimitOnce = true };
        var hooks = app.Services.GetRequiredService<TrackerImportTestHooks>();
        var waits = new List<TimeSpan>();
        hooks.Delay = (t, _) => { lock (waits) waits.Add(t); return Task.CompletedTask; };
        try
        {
            var (p, carol) = await kit.NewProjectAsync();
            var job = await kit.RunAsync(p, carol, Input(fake, project: "eng"));
            Assert.Equal(TrackerImportStatus.Done, job.Status);
            Assert.Equal("ENG", job.SourceProject);
            lock (waits) Assert.Contains(waits, w => w >= TimeSpan.FromSeconds(1));
        }
        finally { hooks.Delay = null; }
    }

    [Fact]
    public async Task Closed_and_archived_issues_can_be_left_out_and_comments_skipped()
    {
        using var fake = new FakeLinear();
        var (p, carol) = await kit.NewProjectAsync();
        var job = await kit.RunAsync(p, carol, Input(fake, settings: new TrackerImportSettings { IncludeClosed = false, IncludeComments = false }));
        Assert.Equal(TrackerImportStatus.Done, job.Status);
        Assert.Equal(["Login fails", "Big epic", "Child of epic"], (await kit.IssuesAsync(p.Id)).Select(i => i.Title));
        Assert.Equal(0, job.CommentsCreated);
        Assert.DoesNotContain(fake.Requests, r => r.Body.Contains("comments(first"));
    }

    [Fact]
    public async Task The_reader_maps_fields_of_the_neutral_model_and_stops_at_MaxIssues()
    {
        using var fake = new FakeLinear();
        using var reader = NewReader(fake);
        var all = new List<SourceIssue>();
        await foreach (var i in reader.ReadIssuesAsync(new SourceReadOptions(true, true, false, 100), CancellationToken.None)) all.Add(i);

        var login = all[0];
        Assert.Equal("a0000000-0000-0000-0000-000000000001", login.ExternalId);
        Assert.Equal("ENG-1", login.DisplayKey);
        Assert.Equal("In Progress", login.StatusName);
        Assert.Equal("bob@example.com", login.Assignee?.Email);
        Assert.Equal("Bob Builder", login.Assignee?.Name);
        Assert.Equal("p1", login.MilestoneId);
        Assert.Contains(new SourceLink("a0000000-0000-0000-0000-000000000002", IssueLinkType.Blocks), login.Links);
        Assert.Contains(new SourceLink("a0000000-0000-0000-0000-000000000003", IssueLinkType.RelatesTo), login.Links);
        Assert.Contains(all.Single(i => i.DisplayKey == "ENG-3").Links, l => l.Type == IssueLinkType.Duplicates);
        Assert.Null(all.Single(i => i.DisplayKey == "ENG-3").Priority);
        Assert.Equal(DateTime.Parse("2024-05-20T09:00:00Z").ToUniversalTime(), all.Single(i => i.DisplayKey == "ENG-3").ClosedAt);

        var milestones = await reader.ReadMilestonesAsync(CancellationToken.None);
        Assert.Equal(["p1", "p0"], milestones.Select(m => m.ExternalId));
        Assert.True(milestones[1].Closed);
        Assert.Equal(new DateTime(2024, 9, 1), milestones[0].DueDate);
        Assert.Equal(["bug", "Area/UI", "epic"], (await reader.ReadLabelsAsync(CancellationToken.None)).Select(l => l.Name));

        var firstThree = new List<SourceIssue>();
        await foreach (var i in reader.ReadIssuesAsync(new SourceReadOptions(true, false, false, 3), CancellationToken.None)) firstThree.Add(i);
        Assert.Equal(["ENG-1", "ENG-2", "ENG-3"], firstThree.Select(i => i.DisplayKey));
    }

    [Fact]
    public async Task A_loopback_address_is_refused_unless_private_networks_are_allowed()
    {
        using var fake = new FakeLinear();
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
