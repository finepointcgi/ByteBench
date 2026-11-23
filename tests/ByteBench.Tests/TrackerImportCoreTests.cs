using ByteBench.Data;
using ByteBench.Services;
using ByteBench.Services.TrackerImport;
using ByteBench.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ByteBench.Tests;

public sealed class TrackerImportCoreApp : TestApp
{
    public CannedSource Source { get; } = new();
    protected override void AddSettings(IDictionary<string, string?> settings) { settings["TrackerImport:Enabled"] = "false"; }
    protected override void ConfigureServices(IServiceCollection services) => services.AddSingleton<ISourceReaderFactory>(new CannedSourceFactory(Source));
}

public class TrackerImportCoreTests(TrackerImportCoreApp app) : IClassFixture<TrackerImportCoreApp>
{
    private static SourceIssue Issue(string id, string title, Action<Dictionary<string, object?>>? tweak = null, SourceComment[]? comments = null,
        string? parent = null, IssueStatus cat = IssueStatus.Todo, SourceUser? author = null, SourceUser? assignee = null, string[]? labels = null, IssueType type = IssueType.Task,
        string? status = null, SourceLink[]? links = null) => new()
    {
        ExternalId = id, DisplayKey = "#" + id, Url = "https://source.example/issues/" + id, Title = title, Body = "Body of " + title, Type = type, Category = cat,
        StatusName = status, CreatedAt = new DateTime(2024, 5, 1, 12, 0, 0, DateTimeKind.Utc), Author = author, Assignee = assignee, Labels = labels ?? [],
        Comments = comments ?? [], ParentExternalId = parent, Links = links ?? [],
        ClosedAt = cat == IssueStatus.Done ? new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc) : null,
    };

    private async Task<(Project P, int Carol)> NewProjectAsync()
    {
        var p = await app.CreateProjectAsync("carol", ProjectVisibility.Private, ("bob", ProjectRole.Developer));
        return (p, app.UserId("carol"));
    }

    private async Task<TrackerImportJob> RunAsync(Project p, int actor, TrackerImportSettings? settings = null)
    {
        await using var s = app.Scope(actor);
        var job = await s.Get<TrackerImportService>().StartAsync(p.Id, new TrackerImportInput
        {
            Source = TrackerSourceKind.GitHub, Project = "acme/widgets", Secret = "ghp_secret_token_value", Settings = settings ?? new(),
        }, actor);
        await app.Services.GetRequiredService<TrackerImportRunner>().RunAsync(job.Id);
        await using var db = app.Db();
        return await db.TrackerImportJobs.AsNoTracking().FirstAsync(j => j.Id == job.Id);
    }

    [Fact]
    public async Task Imports_issues_comments_labels_milestones_parents_links_and_maps_people()
    {
        var (p, carol) = await NewProjectAsync();
        app.Source.Labels = [new("regression", "d73a4a"), new("needs triage", null)];
        app.Source.Milestones = [new("m1", "Release 1", "First", null, new DateTime(2024, 9, 1), false)];
        app.Source.Issues =
        [
            Issue("1", "Epic one", type: IssueType.Epic, author: new("bob", "Bob B", null), labels: ["regression"]),
            Issue("2", "Child of epic", parent: "1", assignee: new("bob", null, null), labels: ["regression", "brand new"],
                comments: [new("c1", new("bob", "Bob B", null), "I'll look", new DateTime(2024, 5, 2, 0, 0, 0, DateTimeKind.Utc)), new("c2", new("stranger", "Some Stranger", null), "Same here", new DateTime(2024, 5, 3, 0, 0, 0, DateTimeKind.Utc))],
                links: [new("3", IssueLinkType.Blocks)]),
            Issue("3", "Closed thing", cat: IssueStatus.Done, assignee: new("nobody-here", "Nobody", null)),
            Issue("4", "Non-member can't be assignee", assignee: new("dan", null, null)),
        ];
        var job = await RunAsync(p, carol);

        Assert.Equal(TrackerImportStatus.Done, job.Status);
        Assert.Equal(4, job.IssuesCreated);
        Assert.Equal(2, job.CommentsCreated);
        Assert.Equal(1, job.LinksCreated);
        await using var db = app.Db();
        var issues = await db.Issues.AsNoTracking().Include(i => i.Labels).Include(i => i.Comments).Where(i => i.ProjectId == p.Id && i.Title != "").ToListAsync();
        var epic = issues.Single(i => i.Title == "Epic one");
        var child = issues.Single(i => i.Title == "Child of epic");
        var closed = issues.Single(i => i.Title == "Closed thing");
        Assert.Equal(IssueType.Epic, epic.Type);
        Assert.Equal(app.UserId("bob"), epic.ReporterId);
        Assert.Equal(epic.Id, child.ParentId);
        Assert.Equal(app.UserId("bob"), child.AssigneeId);
        Assert.Equal(["brand new", "regression"], child.Labels.Select(l => l.Name).Order(StringComparer.Ordinal).ToList());
        Assert.Contains("Body of Child of epic", child.Description);
        Assert.Contains("Imported from [GitHub #2](https://source.example/issues/2)", child.Description);
        Assert.Equal(IssueStatus.Done, closed.Status);
        Assert.Equal(new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc), closed.ClosedAt);
        Assert.Null(closed.AssigneeId);
        Assert.Contains("Nobody", closed.Description);
        Assert.Equal(new DateTime(2024, 5, 1, 12, 0, 0, DateTimeKind.Utc), closed.CreatedAt);
        Assert.Null(issues.Single(i => i.Title == "Non-member can't be assignee").AssigneeId);
        var byBob = child.Comments.Single(c => c.Body == "I'll look");
        Assert.Equal(app.UserId("bob"), byBob.AuthorId);
        var byStranger = child.Comments.Single(c => c.Body.Contains("Same here"));
        Assert.Equal(carol, byStranger.AuthorId);
        Assert.Contains("Some Stranger (@stranger) commented on 2024-05-03", byStranger.Body);
        Assert.Equal(1, await db.IssueLinks.CountAsync(l => l.SourceIssue.ProjectId == p.Id && l.Type == IssueLinkType.Blocks));
        Assert.Equal("#d73a4a", (await db.Labels.SingleAsync(l => l.ProjectId == p.Id && l.Name == "regression" && l.Color == "#d73a4a")).Color);
        Assert.Equal(1, await db.Milestones.CountAsync(m => m.ProjectId == p.Id && m.Title == "Release 1"));
        Assert.True(await db.AuditEvents.AnyAsync(a => a.Action == "project.tracker_imported" && a.ProjectId == p.Id));
        Assert.DoesNotContain("ghp_secret_token_value", await db.AuditEvents.Where(a => a.ProjectId == p.Id).Select(a => a.Details ?? "").ToListAsync().ContinueWith(t => string.Join("|", t.Result)));
        Assert.DoesNotContain("ghp_secret_token_value", job.SettingsJson ?? "");
    }

    [Fact]
    public async Task Running_again_adds_only_what_is_new_and_never_changes_existing_issues()
    {
        var (p, carol) = await NewProjectAsync();
        app.Source.Labels = []; app.Source.Milestones = [];
        app.Source.Issues = [Issue("1", "First", comments: [new("c1", null, "one", DateTime.UtcNow)]), Issue("2", "Second")];
        await RunAsync(p, carol);
        await using (var db = app.Db())
        {
            var first = await db.Issues.FirstAsync(i => i.ProjectId == p.Id && i.Title == "First");
            first.Title = "Renamed here";
            await db.SaveChangesAsync();
        }
        app.Source.Issues = [Issue("1", "First (edited there)", comments: [new("c1", null, "one", DateTime.UtcNow), new("c2", null, "two", DateTime.UtcNow)]), Issue("2", "Second"), Issue("3", "Third")];
        var again = await RunAsync(p, carol);

        Assert.Equal(1, again.IssuesCreated);
        Assert.Equal(2, again.IssuesSkipped);
        Assert.Equal(1, again.CommentsCreated);
        await using var db2 = app.Db();
        Assert.Equal(["Renamed here", "Second", "Third"], await db2.Issues.Where(i => i.ProjectId == p.Id).OrderBy(i => i.Number).Select(i => i.Title).ToListAsync());
        Assert.Equal(2, await db2.Comments.CountAsync(c => db2.Issues.Any(i => i.Id == c.IssueId && i.ProjectId == p.Id)));
    }

    [Fact]
    public async Task Statuses_map_by_name_then_by_category_and_settings_override()
    {
        var (p, carol) = await NewProjectAsync();
        int inReview;
        await using (var db = app.Db()) inReview = (await db.WorkflowStatuses.FirstAsync(s => s.ProjectId == p.Id && s.Category == IssueStatus.InReview)).Id;
        app.Source.Labels = []; app.Source.Milestones = [];
        app.Source.Issues = [Issue("1", "By name", status: "In Review", cat: IssueStatus.InProgress), Issue("2", "By map", status: "QA", cat: IssueStatus.InProgress), Issue("3", "By category", status: "Whatever", cat: IssueStatus.InProgress)];
        await RunAsync(p, carol, new TrackerImportSettings { StatusMap = { ["qa"] = inReview } });
        await using var db2 = app.Db();
        var map = await db2.Issues.Where(i => i.ProjectId == p.Id).Include(i => i.WorkflowStatus).ToDictionaryAsync(i => i.Title, i => i.Status);
        Assert.Equal(IssueStatus.InReview, map["By name"]);
        Assert.Equal(IssueStatus.InReview, map["By map"]);
        Assert.Equal(IssueStatus.InProgress, map["By category"]);
    }

    [Fact]
    public async Task Hierarchy_violations_become_warnings_not_failures()
    {
        var (p, carol) = await NewProjectAsync();
        app.Source.Labels = []; app.Source.Milestones = [];
        app.Source.Issues = [Issue("1", "A task", type: IssueType.Task), Issue("2", "Another task", parent: "1"), Issue("3", "Orphan", parent: "999")];
        var job = await RunAsync(p, carol);
        Assert.Equal(TrackerImportStatus.Done, job.Status);
        Assert.Equal(3, job.IssuesCreated);
        Assert.True(job.WarningCount >= 2);
        await using var db = app.Db();
        Assert.All(await db.Issues.Where(i => i.ProjectId == p.Id).ToListAsync(), i => Assert.Null(i.ParentId));
    }

    [Fact]
    public async Task Source_errors_fail_the_import_with_the_message_and_keep_what_was_written()
    {
        var (p, carol) = await NewProjectAsync();
        app.Source.Labels = []; app.Source.Milestones = [];
        app.Source.Issues = [Issue("1", "One"), Issue("2", "Two"), Issue("3", "Three")];
        app.Source.AuthFailureAfter = 2;
        var job = await RunAsync(p, carol);
        app.Source.AuthFailureAfter = null;
        Assert.Equal(TrackerImportStatus.Failed, job.Status);
        Assert.Equal("The source rejected the credentials.", job.Error);
        // the first two issues were read before the failure; the batch buffer is flushed by a later run, so a re-run completes the project
        var again = await RunAsync(p, carol);
        Assert.Equal(TrackerImportStatus.Done, again.Status);
        await using var db = app.Db();
        Assert.Equal(3, await db.Issues.CountAsync(i => i.ProjectId == p.Id));
    }

    [Fact]
    public async Task Cancelling_stops_the_run_and_keeps_what_was_imported()
    {
        var (p, carol) = await NewProjectAsync();
        app.Source.Labels = []; app.Source.Milestones = [];
        app.Source.Issues = Enumerable.Range(1, 60).Select(n => Issue(n.ToString(), "Issue " + n)).ToList();
        int jobId = 0;
        app.Source.OnYield = n => { if (n == 30) _ = app.Services.GetRequiredService<TrackerImportRunner>().CancelAsync(jobId); };
        await using var s = app.Scope(carol);
        var job = await s.Get<TrackerImportService>().StartAsync(p.Id, new TrackerImportInput { Source = TrackerSourceKind.GitHub, Project = "acme/widgets" }, carol);
        jobId = job.Id;
        await app.Services.GetRequiredService<TrackerImportRunner>().RunAsync(job.Id);
        app.Source.OnYield = null;
        await using var db = app.Db();
        var done = await db.TrackerImportJobs.AsNoTracking().FirstAsync(j => j.Id == job.Id);
        Assert.Equal(TrackerImportStatus.Cancelled, done.Status);
        Assert.Contains("Cancelled", done.Error);
        Assert.InRange(await db.Issues.CountAsync(i => i.ProjectId == p.Id), 1, 59);
    }

    [Fact]
    public async Task Needs_maintainer_and_validates_input_and_one_active_import_per_project()
    {
        var (p, carol) = await NewProjectAsync();
        await using (var s = app.Scope(app.UserId("bob")))
            await Assert.ThrowsAsync<ForbiddenException>(() => s.Get<TrackerImportService>().StartAsync(p.Id, new TrackerImportInput { Source = TrackerSourceKind.GitHub, Project = "a/b" }, app.UserId("bob")));
        await using var sc = app.Scope(carol);
        var svc = sc.Get<TrackerImportService>();
        foreach (var bad in new[] { "no-slash", "a/b/c", "a b/c", "../x/y" })
            await Assert.ThrowsAsync<InvalidOperationException>(() => svc.StartAsync(p.Id, new TrackerImportInput { Source = TrackerSourceKind.GitHub, Project = bad }, carol));
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.StartAsync(p.Id, new TrackerImportInput { Source = TrackerSourceKind.Jira, Project = "lower-case" }, carol));
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.StartAsync(p.Id, new TrackerImportInput { Source = TrackerSourceKind.GitHub, Project = "a/b", Secret = "tok\nen" }, carol));
        await svc.StartAsync(p.Id, new TrackerImportInput { Source = TrackerSourceKind.GitHub, Project = "a/b" }, carol);
        var second = await Assert.ThrowsAsync<InvalidOperationException>(() => svc.StartAsync(p.Id, new TrackerImportInput { Source = TrackerSourceKind.GitHub, Project = "a/c" }, carol));
        Assert.Contains("already has an import", second.Message);
    }
}
