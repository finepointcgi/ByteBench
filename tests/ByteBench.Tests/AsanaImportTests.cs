using ByteBench.Data;
using ByteBench.Services.TrackerImport;
using ByteBench.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace ByteBench.Tests;

/// <summary>The real reader factory and HTTP client against a local fake Asana; imports are driven through the runner directly.</summary>
public class AsanaImportTests(SaasImportApp app, SaasImportStrictApp strict) : IClassFixture<SaasImportApp>, IClassFixture<SaasImportStrictApp>
{
    private readonly ImportTestKit kit = new(app);

    private static TrackerImportInput Input(FakeAsana fake, string project = FakeAsana.ProjectGid, string? secret = FakeAsana.Token, TrackerImportSettings? settings = null) => new()
    {
        Source = TrackerSourceKind.Asana, BaseUrl = fake.Origin, Project = project, Secret = secret, Settings = settings ?? new(),
    };

    private AsanaReader NewReader(FakeAsana fake) =>
        new(new SourceConnection(TrackerSourceKind.Asana, fake.Origin, FakeAsana.ProjectGid, null, FakeAsana.Token), app.Services.GetRequiredService<TrackerHttp>());

    [Fact]
    public async Task Check_shows_the_project_counts_samples_and_sections()
    {
        using var fake = new FakeAsana();
        var (p, carol) = await kit.NewProjectAsync();
        await using var s = app.Scope(carol);
        var check = await s.Get<TrackerImportService>().CheckAsync(p.Id, Input(fake), carol);

        Assert.Equal("Website relaunch", check.Probe.ProjectName);
        Assert.Equal("Ship the new site", check.Probe.Description);
        Assert.Equal(5, check.Probe.IssueCount); // from task_counts, not by paging every task
        Assert.Equal(["Login fails", "Write docs", "Release milestone"], check.Probe.SampleTitles);
        Assert.Equal(["To Do", "Doing", "Done"], check.SourceStatuses.Select(x => x.Name)); // sections come two per page
        Assert.Equal([IssueStatus.Todo, IssueStatus.InProgress, IssueStatus.Done], check.SourceStatuses.Select(x => x.Category));
        Assert.Equal(0, check.MilestoneCount);
        Assert.Contains(fake.Requests, r => r.Path.EndsWith("/task_counts"));
    }

    [Fact]
    public async Task Imports_everything_end_to_end_across_pages()
    {
        using var fake = new FakeAsana();
        var (p, carol) = await kit.NewProjectAsync();
        var job = await kit.RunAsync(p, carol, Input(fake));

        Assert.Equal(TrackerImportStatus.Done, job.Status);
        Assert.Equal(8, job.IssuesCreated); // 5 tasks and 3 subtasks
        Assert.Equal(2, job.CommentsCreated); // the system story is skipped
        Assert.Equal(1, job.LinksCreated);
        Assert.Contains(fake.Requests, r => r.Path.EndsWith("/tasks") && r.Param("offset") == "4"); // 5 tasks at 2 per page
        Assert.DoesNotContain(fake.Requests, r => r.Path.Contains("internal.asana.invalid"));

        var issues = await kit.IssuesAsync(p.Id);
        var login = issues.Single(i => i.Title == "Login fails");
        var reproduce = issues.Single(i => i.Title == "Reproduce");
        var fix = issues.Single(i => i.Title == "Fix it");
        var docs = issues.Single(i => i.Title == "Write docs");
        var milestone = issues.Single(i => i.Title == "Release milestone");
        var epic = issues.Single(i => i.Title == "Big epic");
        var epicChild = issues.Single(i => i.Title == "Epic child");
        var doneInDoing = issues.Single(i => i.Title == "Done in Doing");

        Assert.Equal(IssueType.Bug, login.Type);
        Assert.Equal(IssueStatus.InProgress, login.Status);
        Assert.Equal(IssuePriority.High, login.Priority);
        Assert.Equal(5, login.StoryPoints);
        Assert.Equal(app.UserId("bob"), login.AssigneeId);
        Assert.Equal(new DateTime(2024, 6, 1), login.DueDate?.Date);
        Assert.Equal(["bug"], login.Labels.Select(l => l.Name));
        Assert.Contains("Notes of Login fails", login.Description);
        Assert.Contains("[log.txt](https://app.asana.com/app/asana/-/get_asset?asset_id=1)", login.Description);
        Assert.Contains($"[Asana #000001](https://app.asana.com/0/{FakeAsana.ProjectGid}/1201000000001)", login.Description);
        Assert.Contains("Some Stranger", login.Description);

        Assert.Equal(IssueType.Subtask, reproduce.Type);
        Assert.Equal(login.Id, reproduce.ParentId);
        Assert.Equal(IssueStatus.Done, fix.Status);
        Assert.Equal(login.Id, fix.ParentId);
        Assert.Equal(IssueStatus.Done, docs.Status);
        Assert.NotNull(docs.ClosedAt);
        Assert.Equal(IssueStatus.Done, doneInDoing.Status); // completed task of the "Doing" section
        Assert.Equal(IssuePriority.Low, milestone.Priority); // from the "Low priority" tag
        Assert.Contains(milestone.Labels, l => l.Name == "milestone");
        Assert.Equal(IssueType.Epic, epic.Type);
        Assert.Equal(IssueType.Task, epicChild.Type); // a sub-task can't sit under an epic
        Assert.Equal(epic.Id, epicChild.ParentId);

        Assert.Equal(2, login.Comments.Count);
        var stranger = login.Comments.Single(c => c.Body.Contains("Fixed in the next build"));
        Assert.Equal(carol, stranger.AuthorId);
        Assert.Contains("Some Stranger", stranger.Body);
        Assert.DoesNotContain(login.Comments, c => c.Body.Contains("changed the due date"));

        await using var db = app.Db();
        Assert.Single(db.IssueLinks.Where(l => l.SourceIssueId == login.Id && l.TargetIssueId == milestone.Id && l.Type == IssueLinkType.Blocks).ToList());
    }

    [Fact]
    public async Task The_token_is_sent_as_a_Bearer_header_and_never_leaks()
    {
        using var fake = new FakeAsana();
        var (p, carol) = await kit.NewProjectAsync();
        var job = await kit.RunAsync(p, carol, Input(fake));
        Assert.Equal(TrackerImportStatus.Done, job.Status);
        Assert.All(fake.Requests, r =>
        {
            Assert.Equal("Bearer " + FakeAsana.Token, r.Authorization);
            Assert.DoesNotContain(FakeAsana.Token, r.Path + r.Query);
        });
        Assert.DoesNotContain(FakeAsana.Token, await kit.EverywhereAsync(p, job));

        fake.ExpectedToken = "1/999/someone-else-token";
        var bad = await kit.RunAsync(p, carol, Input(fake));
        Assert.Equal(TrackerImportStatus.Failed, bad.Status);
        Assert.Contains("rejected the credentials", bad.Error);
        var everywhere = await kit.EverywhereAsync(p, bad);
        Assert.DoesNotContain(FakeAsana.Token, everywhere);
        Assert.DoesNotContain("someone-else", everywhere);
    }

    [Fact]
    public async Task Problems_are_reported_in_words()
    {
        using var fake = new FakeAsana();
        var (p, carol) = await kit.NewProjectAsync();
        await using var s = app.Scope(carol);
        var service = s.Get<TrackerImportService>();

        var missing = await Assert.ThrowsAsync<InvalidOperationException>(() => service.CheckAsync(p.Id, Input(fake, project: "1299999999999"), carol));
        Assert.Contains("doesn't exist", missing.Message);
        var shape = await Assert.ThrowsAsync<InvalidOperationException>(() => service.CheckAsync(p.Id, Input(fake, project: "my-project"), carol));
        Assert.Contains("numeric id", shape.Message);
        var noToken = await Assert.ThrowsAsync<InvalidOperationException>(() => service.CheckAsync(p.Id, Input(fake, secret: null), carol));
        Assert.Contains("personal access token", noToken.Message);
    }

    [Fact]
    public async Task Completed_tasks_can_be_left_out_and_comments_skipped()
    {
        using var fake = new FakeAsana();
        var (p, carol) = await kit.NewProjectAsync();
        var job = await kit.RunAsync(p, carol, Input(fake, settings: new TrackerImportSettings { IncludeClosed = false, IncludeComments = false }));
        Assert.Equal(TrackerImportStatus.Done, job.Status);
        Assert.Contains(fake.Requests, r => r.Path.EndsWith("/tasks") && r.Param("completed_since") == "now");
        Assert.Equal(["Login fails", "Reproduce", "Release milestone", "Big epic", "Epic child"], (await kit.IssuesAsync(p.Id)).Select(i => i.Title));
        Assert.Equal(0, job.CommentsCreated);
        Assert.Equal(0, job.LinksCreated);
        Assert.Equal(0, fake.Hits("/stories"));
        Assert.Equal(0, fake.Hits("/dependents"));
    }

    [Fact]
    public async Task The_reader_maps_fields_of_the_neutral_model_and_stops_at_MaxIssues()
    {
        using var fake = new FakeAsana();
        using var reader = NewReader(fake);
        var all = new List<SourceIssue>();
        await foreach (var i in reader.ReadIssuesAsync(new SourceReadOptions(true, true, false, 100), CancellationToken.None)) all.Add(i);

        Assert.Equal(["Login fails", "Reproduce", "Fix it", "Write docs", "Release milestone", "Big epic", "Epic child", "Done in Doing"], all.Select(i => i.Title));
        var login = all[0];
        Assert.Equal("1201000000001", login.ExternalId);
        Assert.Equal("#000001", login.DisplayKey);
        Assert.Equal("Doing", login.StatusName);
        Assert.Equal("bob@example.com", login.Assignee?.Email);
        Assert.Equal("Some Stranger", login.Author?.Name);
        Assert.Contains(new SourceLink("1201000000003", IssueLinkType.Blocks), login.Links);
        Assert.Single(login.Attachments);
        Assert.Equal("1201000000001", all[1].ParentExternalId);
        Assert.Null(all[1].StatusName); // sub-tasks have no section
        var doneInDoing = all.Single(i => i.Title == "Done in Doing");
        Assert.Null(doneInDoing.StatusName); // done by itself while its section is not a done section
        Assert.Equal(IssueStatus.Done, doneInDoing.Category);
        Assert.Equal("Done", all.Single(i => i.Title == "Write docs").StatusName);
        Assert.Empty(await reader.ReadLabelsAsync(CancellationToken.None));

        var firstThree = new List<SourceIssue>();
        await foreach (var i in reader.ReadIssuesAsync(new SourceReadOptions(true, false, false, 3), CancellationToken.None)) firstThree.Add(i);
        Assert.Equal(3, firstThree.Count);
    }

    [Fact]
    public async Task A_rate_limit_is_waited_out_then_the_import_succeeds()
    {
        using var fake = new FakeAsana { RateLimitOnce = true };
        var hooks = app.Services.GetRequiredService<TrackerImportTestHooks>();
        var waits = new List<TimeSpan>();
        hooks.Delay = (t, _) => { lock (waits) waits.Add(t); return Task.CompletedTask; };
        try
        {
            var (p, carol) = await kit.NewProjectAsync();
            var job = await kit.RunAsync(p, carol, Input(fake));
            Assert.Equal(TrackerImportStatus.Done, job.Status);
            Assert.Equal(8, job.IssuesCreated);
            lock (waits) Assert.Contains(waits, w => w >= TimeSpan.FromSeconds(1));
        }
        finally { hooks.Delay = null; }
    }

    [Fact]
    public async Task A_loopback_address_is_refused_unless_private_networks_are_allowed()
    {
        using var fake = new FakeAsana();
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
