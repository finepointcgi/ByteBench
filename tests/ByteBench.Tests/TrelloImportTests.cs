using ByteBench.Data;
using ByteBench.Services.TrackerImport;
using ByteBench.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ByteBench.Tests;

/// <summary>The real reader factory and HTTP client against a local fake Trello; imports are driven through the runner directly.</summary>
public class TrelloImportTests(SaasImportApp app, SaasImportStrictApp strict) : IClassFixture<SaasImportApp>, IClassFixture<SaasImportStrictApp>
{
    private readonly ImportTestKit kit = new(app);

    private static TrackerImportInput Input(FakeTrello fake, string project = FakeTrello.BoardId, string? key = FakeTrello.Key, string? token = FakeTrello.Token, TrackerImportSettings? settings = null) => new()
    {
        Source = TrackerSourceKind.Trello, BaseUrl = fake.Origin + "/1", Project = project, Username = key, Secret = token, Settings = settings ?? new(),
    };

    private TrelloReader NewReader(FakeTrello fake) =>
        new(new SourceConnection(TrackerSourceKind.Trello, fake.Origin, FakeTrello.BoardId, FakeTrello.Key, FakeTrello.Token), app.Services.GetRequiredService<TrackerHttp>());

    [Fact]
    public async Task Check_shows_the_board_counts_samples_lists_and_labels()
    {
        using var fake = new FakeTrello();
        var (p, carol) = await kit.NewProjectAsync();
        await using var s = app.Scope(carol);
        var check = await s.Get<TrackerImportService>().CheckAsync(p.Id, Input(fake), carol);

        Assert.Equal("Roadmap board", check.Probe.ProjectName);
        Assert.Equal("What we build", check.Probe.Description);
        Assert.Equal(6, check.Probe.IssueCount); // all cards, archived ones too
        Assert.Equal(["Fix login", "Write docs", "Closed card"], check.Probe.SampleTitles);
        Assert.Equal(["To Do", "Doing", "Done", "Old stuff"], check.SourceStatuses.Select(x => x.Name));
        Assert.Equal([IssueStatus.Todo, IssueStatus.InProgress, IssueStatus.Done, IssueStatus.Todo], check.SourceStatuses.Select(x => x.Category));
        Assert.Equal(3, check.LabelCount);
        Assert.Equal(0, check.MilestoneCount);
    }

    [Fact]
    public async Task Imports_everything_end_to_end_across_pages()
    {
        using var fake = new FakeTrello();
        var (p, carol) = await kit.NewProjectAsync();
        var job = await kit.RunAsync(p, carol, Input(fake));

        Assert.Equal(TrackerImportStatus.Done, job.Status);
        Assert.Equal(6, job.IssuesCreated);
        Assert.Equal(4, job.CommentsCreated);
        Assert.Equal(0, job.LinksCreated);
        Assert.Equal(0, job.MilestonesCreated);
        Assert.Contains(fake.Requests, r => r.Path.EndsWith("/cards/all") && r.Param("before") != null); // 2 cards per page
        Assert.Contains(fake.Requests, r => r.Path.EndsWith("/actions") && r.Param("before") != null);

        var issues = await kit.IssuesAsync(p.Id);
        var login = issues.Single(i => i.Title == "Fix login");
        var docs = issues.Single(i => i.Title == "Write docs");
        var closed = issues.Single(i => i.Title == "Closed card");
        var dueDone = issues.Single(i => i.Title == "Due done");
        var shiny = issues.Single(i => i.Title == "Shiny feature");
        var old = issues.Single(i => i.Title == "Old archived");

        Assert.Equal(IssueType.Bug, login.Type);
        Assert.Equal(IssueStatus.InProgress, login.Status);
        Assert.Equal(app.UserId("bob"), login.AssigneeId);
        Assert.Equal(new DateTime(2024, 6, 1), login.DueDate?.Date);
        Assert.Equal(2020, login.CreatedAt.Year); // from the card id's timestamp
        Assert.Equal(["bug", "green"], login.Labels.Select(l => l.Name).Order(StringComparer.Ordinal).ToList()); // the unnamed label is known by its colour
        Assert.Contains("Steps to reproduce", login.Description);
        Assert.Contains("**Release steps**\n- [x] Build\n- [ ] Ship", login.Description);
        Assert.Contains("[log.txt](https://trello.com/1/cards/x/attachments/att1/download/log.txt)", login.Description);
        Assert.Contains("[Trello #1](https://trello.com/c/card1)", login.Description);

        Assert.Equal(IssueStatus.Done, docs.Status);
        Assert.Equal(IssueStatus.Done, closed.Status); // closed card
        Assert.NotNull(closed.ClosedAt);
        Assert.Equal(IssueStatus.Done, dueDone.Status); // dueComplete, though its list is "Doing"
        Assert.Equal(IssueStatus.Done, old.Status); // card of an archived list
        Assert.Equal(IssueType.Feature, shiny.Type);
        Assert.Equal(IssueStatus.Todo, shiny.Status);
        Assert.All(issues, i => Assert.Null(i.ParentId));

        Assert.Equal(3, login.Comments.Count);
        Assert.Equal(["I can reproduce this", "Fixed in the next build", "Thanks"], login.Comments.OrderBy(c => c.CreatedAt).Select(c => c.Body.Split('\n').Last()).ToList());
        var stranger = login.Comments.Single(c => c.Body.Contains("Fixed in the next build"));
        Assert.Equal(carol, stranger.AuthorId);
        Assert.Contains("Some Stranger (@stranger)", stranger.Body);
        Assert.Equal(app.UserId("bob"), login.Comments.First(c => c.Body.Contains("I can reproduce")).AuthorId);

        // Re-running creates nothing new and adds no comments twice.
        var again = await kit.RunAsync(p, carol, Input(fake));
        Assert.Equal(TrackerImportStatus.Done, again.Status);
        Assert.Equal(0, again.IssuesCreated);
        Assert.Equal(6, again.IssuesSkipped);
        Assert.Equal(0, again.CommentsCreated);
    }

    [Fact]
    public async Task The_key_and_token_travel_only_in_the_query_and_never_leak()
    {
        using var fake = new FakeTrello();
        var (p, carol) = await kit.NewProjectAsync();
        var job = await kit.RunAsync(p, carol, Input(fake));
        Assert.Equal(TrackerImportStatus.Done, job.Status);
        Assert.NotEmpty(fake.Requests);
        Assert.All(fake.Requests, r =>
        {
            Assert.Equal(FakeTrello.Key, r.Param("key"));
            Assert.Equal(FakeTrello.Token, r.Param("token"));
            Assert.DoesNotContain(FakeTrello.Token, r.Path);
            Assert.Null(r.Authorization);
        });
        var everywhere = await kit.EverywhereAsync(p, job);
        Assert.DoesNotContain(FakeTrello.Key, everywhere);
        Assert.DoesNotContain(FakeTrello.Token, everywhere);

        // Warnings (unmatched people) and every failure message stay free of the credentials too.
        fake.ExpectedToken = "someone-elses-token-98765";
        var bad = await kit.RunAsync(p, carol, Input(fake));
        Assert.Equal(TrackerImportStatus.Failed, bad.Status);
        Assert.Contains("rejected the credentials", bad.Error);
        var failed = await kit.EverywhereAsync(p, bad);
        Assert.DoesNotContain(FakeTrello.Key, failed);
        Assert.DoesNotContain(FakeTrello.Token, failed);
        Assert.DoesNotContain("someone-elses", failed);
    }

    [Fact]
    public async Task An_unreachable_server_message_has_no_credentials()
    {
        var (p, carol) = await kit.NewProjectAsync();
        await using var s = app.Scope(carol);
        var input = new TrackerImportInput { Source = TrackerSourceKind.Trello, BaseUrl = "http://127.0.0.1:1/1", Project = FakeTrello.BoardId, Username = FakeTrello.Key, Secret = FakeTrello.Token };
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => s.Get<TrackerImportService>().CheckAsync(p.Id, input, carol));
        Assert.Contains("Couldn't reach Trello", ex.Message);
        Assert.DoesNotContain(FakeTrello.Key, ex.Message);
        Assert.DoesNotContain(FakeTrello.Token, ex.Message);
        Assert.DoesNotContain("key=", ex.Message);
    }

    [Fact]
    public async Task A_board_address_or_short_link_works_and_problems_are_reported_in_words()
    {
        using var fake = new FakeTrello();
        var (p, carol) = await kit.NewProjectAsync();
        await using var s = app.Scope(carol);
        var service = s.Get<TrackerImportService>();

        var byUrl = await service.CheckAsync(p.Id, Input(fake, project: "https://trello.com/b/" + FakeTrello.ShortLink + "/roadmap-board"), carol);
        Assert.Equal("Roadmap board", byUrl.Probe.ProjectName);
        Assert.Contains(fake.Requests, r => r.Path == "/1/boards/" + FakeTrello.ShortLink);

        var missing = await Assert.ThrowsAsync<InvalidOperationException>(() => service.CheckAsync(p.Id, Input(fake, project: "AbCdEfGh"), carol));
        Assert.Contains("doesn't exist", missing.Message);
        var shape = await Assert.ThrowsAsync<InvalidOperationException>(() => service.CheckAsync(p.Id, Input(fake, project: "no spaces/here"), carol));
        Assert.Contains("short link", shape.Message);
        var noKey = await Assert.ThrowsAsync<InvalidOperationException>(() => service.CheckAsync(p.Id, Input(fake, key: null), carol));
        Assert.Contains("API key", noKey.Message);
    }

    [Fact]
    public async Task Closed_cards_and_cards_of_archived_lists_can_be_left_out()
    {
        using var fake = new FakeTrello();
        var (p, carol) = await kit.NewProjectAsync();
        var job = await kit.RunAsync(p, carol, Input(fake, settings: new TrackerImportSettings { IncludeClosed = false }));
        Assert.Equal(TrackerImportStatus.Done, job.Status);
        Assert.Contains(fake.Requests, r => r.Path.EndsWith("/cards/open"));
        var titles = (await kit.IssuesAsync(p.Id)).Select(i => i.Title).ToList();
        Assert.DoesNotContain("Closed card", titles);
        Assert.DoesNotContain("Old archived", titles);
        Assert.Equal(4, titles.Count);
    }

    [Fact]
    public async Task Without_comments_no_actions_are_requested_and_MaxIssues_stops_early()
    {
        using var fake = new FakeTrello();
        var (p, carol) = await kit.NewProjectAsync();
        var job = await kit.RunAsync(p, carol, Input(fake, settings: new TrackerImportSettings { IncludeComments = false }));
        Assert.Equal(0, job.CommentsCreated);
        Assert.Equal(0, fake.Hits("/actions"));

        using var reader = NewReader(fake);
        var got = new List<SourceIssue>();
        await foreach (var i in reader.ReadIssuesAsync(new SourceReadOptions(true, false, false, 3), CancellationToken.None)) got.Add(i);
        Assert.Equal(3, got.Count);
        Assert.Equal(["Fix login", "Write docs", "Closed card"], got.Select(i => i.Title)); // oldest first
    }

    [Fact]
    public async Task The_reader_maps_fields_of_the_neutral_model()
    {
        using var fake = new FakeTrello();
        using var reader = NewReader(fake);
        var all = new List<SourceIssue>();
        await foreach (var i in reader.ReadIssuesAsync(new SourceReadOptions(true, true, false, 100), CancellationToken.None)) all.Add(i);

        var login = all[0];
        Assert.Equal(FakeTrello.Id(1), login.ExternalId);
        Assert.Equal("#1", login.DisplayKey);
        Assert.Equal("https://trello.com/c/card1", login.Url);
        Assert.Equal("Doing", login.StatusName);
        Assert.Equal(IssueStatus.InProgress, login.Category);
        Assert.Equal("bob", login.Assignee?.Login);
        Assert.Equal("Bob Builder", login.Assignee?.Name);
        Assert.Equal(["bug", "green"], login.Labels);
        Assert.Single(login.Attachments);
        Assert.Equal(3, login.Comments.Count);
        Assert.Empty(login.Links);
        Assert.Null(login.ParentExternalId);
        Assert.Null(login.MilestoneId);
        // A card done by itself while its list is not a done list has no status name, so its category decides.
        var dueDone = all.Single(i => i.Title == "Due done");
        Assert.Null(dueDone.StatusName);
        Assert.Equal(IssueStatus.Done, dueDone.Category);
        Assert.Equal("Done", all.Single(i => i.Title == "Write docs").StatusName);
        Assert.Null(all.Single(i => i.Title == "Shiny feature").Body);

        var labels = await reader.ReadLabelsAsync(CancellationToken.None);
        Assert.Equal(["bug", "green", "Feature"], labels.Select(l => l.Name));
        Assert.Equal("#eb5a46", labels[0].Color);
    }

    [Fact]
    public async Task A_rate_limit_is_waited_out_then_the_import_succeeds()
    {
        using var fake = new FakeTrello { RateLimitOnce = true };
        var hooks = app.Services.GetRequiredService<TrackerImportTestHooks>();
        var waits = new List<TimeSpan>();
        hooks.Delay = (t, _) => { lock (waits) waits.Add(t); return Task.CompletedTask; };
        try
        {
            var (p, carol) = await kit.NewProjectAsync();
            var job = await kit.RunAsync(p, carol, Input(fake));
            Assert.Equal(TrackerImportStatus.Done, job.Status);
            Assert.Equal(6, job.IssuesCreated);
            lock (waits) Assert.Contains(waits, w => w >= TimeSpan.FromSeconds(1));
        }
        finally { hooks.Delay = null; }
    }

    [Fact]
    public async Task A_loopback_address_is_refused_unless_private_networks_are_allowed()
    {
        using var fake = new FakeTrello();
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
