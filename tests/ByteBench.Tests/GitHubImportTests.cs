using ByteBench.Data;
using ByteBench.Services.TrackerImport;
using ByteBench.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ByteBench.Tests;

public sealed class GitHubImportApp : TestApp
{
    protected override void AddSettings(IDictionary<string, string?> settings)
    {
        settings["TrackerImport:AllowPrivateNetworks"] = "true";
        settings["TrackerImport:Enabled"] = "false";
    }
}

/// <summary>Same app, but sources on local addresses are refused (the default for real installs).</summary>
public sealed class GitHubImportStrictApp : TestApp
{
    protected override void AddSettings(IDictionary<string, string?> settings)
    {
        settings["TrackerImport:AllowPrivateNetworks"] = "false";
        settings["TrackerImport:Enabled"] = "false";
    }
}

public class GitHubImportTests(GitHubImportApp app, GitHubImportStrictApp strict) : IClassFixture<GitHubImportApp>, IClassFixture<GitHubImportStrictApp>, IDisposable
{
    private readonly FakeGitHub gh = new();

    public void Dispose()
    {
        gh.Dispose();
        app.Services.GetRequiredService<TrackerImportTestHooks>().Delay = null;
    }

    private TrackerImportInput Input(string? secret = FakeGitHub.Token, TrackerImportSettings? settings = null) => new()
    {
        Source = TrackerSourceKind.GitHub, BaseUrl = gh.ApiRoot, Project = FakeGitHub.Repo, Secret = secret, Settings = settings ?? new(),
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

    private async Task<TrackerImportCheck> CheckAsync(Project p, int actor, TrackerImportInput input)
    {
        await using var s = app.Scope(actor);
        return await s.Get<TrackerImportService>().CheckAsync(p.Id, input, actor);
    }

    [Fact]
    public async Task Check_reports_the_repository_sample_labels_milestones_and_statuses()
    {
        var (p, carol) = await NewProjectAsync();
        var check = await CheckAsync(p, carol, Input());
        Assert.Equal("acme/widgets", check.Probe.ProjectName);
        Assert.Equal("All the widgets", check.Probe.Description);
        Assert.Equal(6, check.Probe.IssueCount);
        Assert.Equal(["Crash on save", "Add dark mode"], check.Probe.SampleTitles); // the pull request among the first three is left out
        Assert.Contains("about 6 open", check.Probe.Warning);
        Assert.DoesNotContain("60 per hour", check.Probe.Warning);
        Assert.Equal(4, check.LabelCount);
        Assert.Equal(2, check.MilestoneCount);
        Assert.Equal(["Open", "Closed"], check.SourceStatuses.Select(s => s.Name));
        Assert.Equal([IssueStatus.Todo, IssueStatus.Done], check.SourceStatuses.Select(s => s.Category));
    }

    [Fact]
    public async Task Check_without_a_token_warns_about_the_rate_limit_and_sends_no_authorization()
    {
        var (p, carol) = await NewProjectAsync();
        var check = await CheckAsync(p, carol, Input(secret: null));
        Assert.Contains("60 per hour", check.Probe.Warning);
        lock (gh.Requests) Assert.All(gh.Requests, r => Assert.Null(r.Auth));
    }

    [Fact]
    public async Task Imports_issues_with_types_priorities_labels_milestones_parents_and_comments()
    {
        var (p, carol) = await NewProjectAsync();
        var job = await RunAsync(p, carol, Input());

        Assert.Equal(TrackerImportStatus.Done, job.Status);
        Assert.Null(job.Error);
        Assert.Equal(7, job.IssuesCreated); // the pull request (#2) is skipped
        Assert.Equal(2, job.CommentsCreated);
        await using var db = app.Db();
        var issues = await db.Issues.AsNoTracking().Include(i => i.Labels).Include(i => i.Comments).Include(i => i.Milestone)
            .Where(i => i.ProjectId == p.Id).ToDictionaryAsync(i => i.Title);
        Assert.DoesNotContain("Fix crash on save", issues.Keys);

        var crash = issues["Crash on save"];
        Assert.Equal(IssueType.Bug, crash.Type);
        Assert.Equal(IssuePriority.High, crash.Priority);
        Assert.Equal(["bug", "priority: high"], crash.Labels.Select(l => l.Name).Order(StringComparer.Ordinal).ToList());
        Assert.Equal("v1.0", crash.Milestone!.Title);
        Assert.Equal("First release", crash.Milestone.Description);
        Assert.Equal(new DateTime(2026, 9, 1), crash.Milestone.DueDate!.Value.Date);
        Assert.False(crash.Milestone.IsClosed);
        Assert.Equal(app.UserId("bob"), crash.AssigneeId);
        Assert.Equal(app.UserId("bob"), crash.ReporterId);
        Assert.Equal(new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc), crash.CreatedAt);
        Assert.Contains("Body of Crash on save", crash.Description);
        Assert.Contains("https://github.com/acme/widgets/issues/1", crash.Description);
        Assert.Equal(IssueStatus.Todo, crash.Status);

        var dark = issues["Add dark mode"];
        Assert.Equal(IssueType.Feature, dark.Type);
        Assert.Equal(IssuePriority.Medium, dark.Priority); // p2
        Assert.Equal(IssueStatus.Done, dark.Status);
        Assert.Equal(new DateTime(2026, 2, 1, 10, 0, 0, DateTimeKind.Utc), dark.ClosedAt);
        Assert.Equal(carol, dark.AssigneeId); // from "assignees" when "assignee" is empty

        var epic = issues["Roadmap"];
        Assert.Equal(IssueType.Epic, epic.Type);
        Assert.Equal(IssuePriority.Low, epic.Priority); // priority/low

        var docs = issues["Write the docs"];
        Assert.Equal(IssueType.Task, docs.Type);
        Assert.Equal(IssuePriority.Highest, docs.Priority); // P0
        Assert.Equal(epic.Id, docs.ParentId);

        Assert.Equal(IssueType.Feature, issues["Late parent"].Type); // "feature request"
        Assert.Equal(IssueStatus.Done, issues["Never mind"].Status);
        Assert.Null(issues["Early child"].ParentId); // its parent #8 comes later in the stream

        var warnings = TrackerImportService.WarningsOf(job);
        Assert.Contains(warnings, w => w.Contains("#5") && w.Contains("closed as not planned"));
        Assert.Contains(warnings, w => w.Contains("#7") && w.Contains("parent #8"));

        var bob = crash.Comments.Single(c => c.Body == "I'll look");
        Assert.Equal(app.UserId("bob"), bob.AuthorId);
        var stranger = crash.Comments.Single(c => c.Body.Contains("Same here"));
        Assert.Equal(carol, stranger.AuthorId); // no matching account: the person who ran the import, named in the text
        Assert.Contains("@stranger", stranger.Body);
        Assert.Equal(1, await db.Milestones.CountAsync(m => m.ProjectId == p.Id && m.Title == "v0.9" && m.IsClosed));

        // Paging: 8 issues at 3 per page is three requests; comments were only fetched for the issue that has some.
        Assert.Equal(3, gh.Requests.Count(r => r.PathAndQuery.StartsWith("/repos/acme/widgets/issues?") && r.PathAndQuery.Contains("sort=created")) - 1);
        Assert.Equal(1, gh.Hits("/repos/acme/widgets/issues/1/comments"));
        Assert.Equal(1, gh.Requests.Count(r => r.PathAndQuery.Contains("/comments")));
    }

    [Fact]
    public async Task Pull_requests_are_imported_as_labelled_issues_when_asked()
    {
        var (p, carol) = await NewProjectAsync();
        var job = await RunAsync(p, carol, Input(settings: new() { IncludePullRequests = true }));
        Assert.Equal(8, job.IssuesCreated);
        await using var db = app.Db();
        var pr = await db.Issues.Include(i => i.Labels).SingleAsync(i => i.ProjectId == p.Id && i.Title == "Fix crash on save");
        Assert.Contains("pull-request", pr.Labels.Select(l => l.Name));
        Assert.Contains(TrackerImportService.WarningsOf(job), w => w.Contains("#2") && w.Contains("pull request"));
    }

    [Fact]
    public async Task Leaving_out_closed_issues_asks_GitHub_for_open_ones_only()
    {
        var (p, carol) = await NewProjectAsync();
        var job = await RunAsync(p, carol, Input(settings: new() { IncludeClosed = false }));
        Assert.Equal(5, job.IssuesCreated); // #1, #4, #6, #7, #8 (the open pull request is skipped)
        await using var db = app.Db();
        Assert.DoesNotContain(IssueStatus.Done, await db.Issues.Where(i => i.ProjectId == p.Id).Select(i => i.Status).ToListAsync());
        lock (gh.Requests)
        {
            var reads = gh.Requests.Where(r => r.PathAndQuery.Contains("/issues?") && r.PathAndQuery.Contains("per_page=100") || r.PathAndQuery.Contains("/issues?state=open")).ToList();
            Assert.NotEmpty(reads);
            Assert.All(reads, r => Assert.Contains("state=open", r.PathAndQuery));
        }
    }

    [Fact]
    public async Task Comments_are_left_out_when_not_wanted()
    {
        var (p, carol) = await NewProjectAsync();
        var job = await RunAsync(p, carol, Input(settings: new() { IncludeComments = false }));
        Assert.Equal(0, job.CommentsCreated);
        Assert.Equal(0, gh.Hits("/repos/acme/widgets/issues/1/comments"));
    }

    [Fact]
    public async Task The_reader_stops_after_MaxIssues_and_streams_across_pages()
    {
        using var reader = app.Services.GetRequiredService<ISourceReaderFactory>().Create(new SourceConnection(TrackerSourceKind.GitHub, gh.ApiRoot, FakeGitHub.Repo, null, null));
        var titles = new List<string>();
        await foreach (var i in reader.ReadIssuesAsync(new SourceReadOptions(true, false, false, 4), CancellationToken.None)) titles.Add(i.Title);
        Assert.Equal(["Crash on save", "Add dark mode", "Roadmap", "Never mind"], titles);
        Assert.Equal(2, gh.Requests.Count(r => r.PathAndQuery.Contains("/issues?")));  // the 3rd page was never fetched
    }

    [Fact]
    public async Task Requests_carry_the_token_and_GitHub_headers_and_the_token_is_never_stored_or_shown()
    {
        gh.RequireToken = true;
        var (p, carol) = await NewProjectAsync();
        var job = await RunAsync(p, carol, Input());
        Assert.Equal(TrackerImportStatus.Done, job.Status);
        lock (gh.Requests)
        {
            Assert.NotEmpty(gh.Requests);
            Assert.All(gh.Requests, r =>
            {
                Assert.Equal("Bearer " + FakeGitHub.Token, r.Auth);
                Assert.Equal("application/vnd.github+json", r.Accept);
                Assert.Equal("2022-11-28", r.ApiVersion);
            });
        }
        await AssertNoTokenAnywhereAsync(p, job);
    }

    private async Task AssertNoTokenAnywhereAsync(Project p, TrackerImportJob job)
    {
        await using var db = app.Db();
        var audit = string.Join("|", await db.AuditEvents.Where(a => a.ProjectId == p.Id).Select(a => a.Details ?? "").ToListAsync());
        Assert.NotEmpty(audit);
        foreach (var text in new[] { job.Error ?? "", job.WarningsJson ?? "", job.SettingsJson ?? "", job.SourceUrl, job.SourceProject, audit })
            Assert.DoesNotContain(FakeGitHub.Token, text);
    }

    [Fact]
    public async Task A_rate_limited_request_waits_for_Retry_After_then_succeeds()
    {
        var waits = new List<TimeSpan>();
        app.Services.GetRequiredService<TrackerImportTestHooks>().Delay = (t, _) => { lock (waits) waits.Add(t); return Task.CompletedTask; };
        gh.RateLimitIssueRequests = 1;
        var (p, carol) = await NewProjectAsync();
        var job = await RunAsync(p, carol, Input());
        Assert.Equal(TrackerImportStatus.Done, job.Status);
        Assert.Equal(7, job.IssuesCreated);
        lock (waits) { Assert.Single(waits); Assert.True(waits[0] >= TimeSpan.FromSeconds(1)); }
    }

    [Fact]
    public async Task A_bad_token_is_reported_without_echoing_it()
    {
        gh.RequireToken = true;
        var (p, carol) = await NewProjectAsync();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => CheckAsync(p, carol, Input(secret: "ghp_WrongTokenValue")));
        Assert.Contains("rejected the credentials", ex.Message);
        Assert.DoesNotContain("ghp_WrongTokenValue", ex.Message);

        var job = await RunAsync(p, carol, Input(secret: "ghp_WrongTokenValue"));
        Assert.Equal(TrackerImportStatus.Failed, job.Status);
        Assert.Contains("rejected the credentials", job.Error);
        Assert.DoesNotContain("ghp_WrongTokenValue", job.Error);
    }

    [Fact]
    public async Task A_missing_repository_says_so()
    {
        gh.RepoExists = false;
        var (p, carol) = await NewProjectAsync();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => CheckAsync(p, carol, Input()));
        Assert.Contains("doesn't exist", ex.Message);
    }

    [Fact]
    public async Task A_repository_with_issues_turned_off_says_so()
    {
        gh.HasIssues = false;
        var (p, carol) = await NewProjectAsync();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => CheckAsync(p, carol, Input()));
        Assert.Contains("Issues are turned off", ex.Message);
        Assert.Contains("acme/widgets", ex.Message);
    }

    [Fact]
    public async Task A_loopback_address_is_refused_unless_private_networks_are_allowed()
    {
        var p = await strict.CreateProjectAsync("carol", ProjectVisibility.Private);
        var carol = strict.UserId("carol");
        await using var s = strict.Scope(carol);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => s.Get<TrackerImportService>().CheckAsync(p.Id, Input(), carol));
        Assert.Contains("can't be on this server", ex.Message);
        Assert.Empty(gh.Requests);
        var start = await Assert.ThrowsAsync<InvalidOperationException>(() => s.Get<TrackerImportService>().StartAsync(p.Id, Input(), carol));
        Assert.Contains("can't be on this server", start.Message);
    }
}
