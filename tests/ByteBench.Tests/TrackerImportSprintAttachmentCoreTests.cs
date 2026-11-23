using ByteBench.Data;
using ByteBench.Services.TrackerImport;
using ByteBench.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ByteBench.Tests;

/// <summary>The writer's sprint and attachment rules, fed by a canned reader (no HTTP): states, one active sprint, counts, rewriting, limits.</summary>
public class TrackerImportSprintAttachmentCoreTests(TrackerImportCoreApp app) : IClassFixture<TrackerImportCoreApp>
{
    private static readonly DateTime May = new(2024, 5, 1, 0, 0, 0, DateTimeKind.Utc);

    private static SourceIssue Issue(string id, string title, string? sprint = null, IssueStatus cat = IssueStatus.Todo, int? points = null,
        string? body = null, SourceAttachment[]? files = null, SourceComment[]? comments = null) => new()
    {
        ExternalId = id, DisplayKey = "#" + id, Title = title, Body = body ?? "Body of " + title, Category = cat, StoryPoints = points, SprintId = sprint,
        CreatedAt = May, Attachments = files ?? [], Comments = comments ?? [],
        ClosedAt = cat == IssueStatus.Done ? May.AddDays(10) : null,
    };

    private async Task<(Project P, int Carol)> NewProjectAsync()
    {
        app.Source.Sprints = [];
        var p = await app.CreateProjectAsync("carol", ProjectVisibility.Private, ("bob", ProjectRole.Developer));
        return (p, app.UserId("carol"));
    }

    private async Task<TrackerImportJob> RunAsync(Project p, int actor, TrackerImportSettings? settings = null)
    {
        await using var s = app.Scope(actor);
        var job = await s.Get<TrackerImportService>().StartAsync(p.Id, new TrackerImportInput { Source = TrackerSourceKind.GitHub, Project = "acme/widgets", Secret = "tok", Settings = settings ?? new() }, actor);
        await app.Services.GetRequiredService<TrackerImportRunner>().RunAsync(job.Id);
        await using var db = app.Db();
        return await db.TrackerImportJobs.AsNoTracking().FirstAsync(j => j.Id == job.Id);
    }

    [Fact]
    public async Task Sprint_states_obey_the_one_active_rule_and_completed_sprints_get_report_counts()
    {
        var (p, carol) = await NewProjectAsync();
        app.Source.Sprints =
        [
            new("a", "Alpha", "Goal A", May, new DateTime(2099, 1, 1), SprintState.Active),
            new("b", "Beta", null, May.AddDays(9), May.AddDays(19), SprintState.Active),                          // ended long ago: completed
            new("c", "Gamma", null, May.AddDays(30), new DateTime(2099, 2, 1), SprintState.Active),                // still running: planned
            new("d", "Delta", null, May.AddDays(40), May.AddDays(30), SprintState.Completed),                      // ends before it starts
            new("e", "Epsilon", new string('g', 700), null, null, SprintState.Planned),
        ];
        app.Source.Issues =
        [
            Issue("1", "In alpha", "a", points: 3),
            Issue("2", "Done in delta", "d", IssueStatus.Done, points: 5),
            Issue("3", "Open in delta", "d", points: 2),
            Issue("4", "Unknown sprint", "nope"),
            Issue("5", "No sprint"),
        ];
        var job = await RunAsync(p, carol);

        Assert.Equal(TrackerImportStatus.Done, job.Status);
        await using var db = app.Db();
        var sprints = await db.Sprints.AsNoTracking().Where(s => s.ProjectId == p.Id).ToDictionaryAsync(s => s.Name);
        Assert.Equal(5, sprints.Count);
        Assert.Equal(SprintState.Active, sprints["Alpha"].State);
        Assert.Equal(1, sprints.Values.Count(s => s.State == SprintState.Active));
        Assert.Equal("Goal A", sprints["Alpha"].Goal);
        Assert.Equal((1, 3), (sprints["Alpha"].CommittedCount, sprints["Alpha"].CommittedPoints));
        Assert.Null(sprints["Alpha"].CompletedCount);
        Assert.Equal(SprintState.Completed, sprints["Beta"].State);
        Assert.Equal(May.AddDays(19), sprints["Beta"].CompletedAt);
        Assert.Equal(SprintState.Planned, sprints["Gamma"].State);
        Assert.Null(sprints["Gamma"].StartedAt);
        Assert.Equal(SprintState.Completed, sprints["Delta"].State);
        Assert.Null(sprints["Delta"].EndDate);                                                                  // dropped: it ended before it started
        Assert.Equal((2, 7, 1, 5), (sprints["Delta"].CommittedCount, sprints["Delta"].CommittedPoints, sprints["Delta"].CompletedCount, sprints["Delta"].CompletedPoints));
        Assert.Equal(500, sprints["Epsilon"].Goal!.Length);

        var issues = await db.Issues.AsNoTracking().Where(i => i.ProjectId == p.Id).ToDictionaryAsync(i => i.Title);
        Assert.Equal(sprints["Alpha"].Id, issues["In alpha"].SprintId);
        Assert.Equal(sprints["Delta"].Id, issues["Open in delta"].SprintId);
        Assert.Null(issues["Unknown sprint"].SprintId);
        Assert.Null(issues["No sprint"].SprintId);
        var warnings = TrackerImportService.WarningsOf(job);
        Assert.Contains(warnings, w => w.Contains("Beta") && w.Contains("completed"));
        Assert.Contains(warnings, w => w.Contains("Gamma") && w.Contains("planned"));
        Assert.Contains(warnings, w => w.Contains("Delta") && w.Contains("ends before it starts"));
        Assert.Equal(1, await db.Sprints.CountAsync(s => s.ProjectId == p.Id && s.State == SprintState.Active));
    }

    [Fact]
    public async Task A_second_run_adds_only_new_sprints_and_puts_new_issues_in_known_ones()
    {
        var (p, carol) = await NewProjectAsync();
        app.Source.Sprints = [new("a", "Alpha", null, May, new DateTime(2099, 1, 1), SprintState.Active)];
        app.Source.Issues = [Issue("1", "First", "a")];
        await RunAsync(p, carol);
        await using (var db = app.Db())
        {
            var alpha = await db.Sprints.SingleAsync(s => s.ProjectId == p.Id);
            alpha.Name = "Renamed here";
            await db.SaveChangesAsync();
        }
        app.Source.Sprints = [.. app.Source.Sprints, new("z", "Zeta", null, May.AddDays(60), null, SprintState.Planned)];
        app.Source.Issues = [Issue("1", "First", "a"), Issue("2", "Second", "z")];
        var again = await RunAsync(p, carol);

        Assert.Equal(TrackerImportStatus.Done, again.Status);
        await using var db2 = app.Db();
        var sprints = await db2.Sprints.AsNoTracking().Where(s => s.ProjectId == p.Id).OrderBy(s => s.Id).ToListAsync();
        Assert.Equal(["Renamed here", "Zeta"], sprints.Select(s => s.Name));
        Assert.Equal(sprints[1].Id, (await db2.Issues.AsNoTracking().SingleAsync(i => i.ProjectId == p.Id && i.Title == "Second")).SprintId);
    }

    [Fact]
    public async Task Copied_files_replace_their_links_only_where_the_whole_address_matches()
    {
        var (p, carol) = await NewProjectAsync();
        const string Url = "https://files.example/u/a.png";
        app.Source.Files[Url] = FakeFiles.Png;
        app.Source.Files["https://files.example/u/doc.pdf"] = FakeFiles.Pdf;
        app.Source.Issues =
        [
            Issue("1", "With files", body: $"![a]({Url}) and a backup {Url}.bak and (see {Url}).",
                files: [new("a.png", Url, Url, "u/a.png", Url), new("doc.pdf", "https://files.example/u/doc.pdf", "https://files.example/u/doc.pdf", "u/doc.pdf", null), new("missing.txt", "https://files.example/u/missing.txt", "https://files.example/u/missing.txt", "u/missing.txt", null)],
                comments: [new("c1", null, $"again ![a]({Url})", May)]),
        ];
        var job = await RunAsync(p, carol, new TrackerImportSettings { IncludeAttachments = true });

        Assert.Equal(TrackerImportStatus.Done, job.Status);
        await using var db = app.Db();
        var issue = await db.Issues.AsNoTracking().Include(i => i.Comments).SingleAsync(i => i.ProjectId == p.Id);
        var files = await db.Attachments.AsNoTracking().Where(a => a.IssueId == issue.Id).ToDictionaryAsync(a => a.FileName);
        Assert.Equal(["a.png", "doc.pdf"], files.Keys.Order().ToList());
        var local = $"/attachments/{files["a.png"].Id}/a.png";
        Assert.Contains($"![a]({local}) and a backup {Url}.bak and (see {local}).", issue.Description);
        Assert.Contains($"again ![a]({local})", issue.Comments.Single().Body);
        Assert.Contains("[missing.txt](https://files.example/u/missing.txt)", issue.Description);     // listed: not copied
        Assert.DoesNotContain("[doc.pdf]", issue.Description);                                         // copied, so not listed
        Assert.Contains(TrackerImportService.WarningsOf(job), w => w.Contains("missing.txt") && w.Contains("no longer has the file"));
        Assert.Equal(FakeFiles.Sha256(FakeFiles.Pdf), files["doc.pdf"].Sha256);
    }

    [Fact]
    public async Task Attachment_copying_is_off_by_default_and_copied_files_use_the_attachment_service_rules()
    {
        var (p, carol) = await NewProjectAsync();
        const string Url = "https://files.example/off/a.png";
        app.Source.Files[Url] = FakeFiles.Png;
        app.Source.Issues = [Issue("1", "Off", files: [new("a.png", Url, Url, "off/a.png")])];
        var job = await RunAsync(p, carol);

        Assert.Equal(TrackerImportStatus.Done, job.Status);
        await using var db = app.Db();
        Assert.Equal(0, await db.Attachments.CountAsync(a => a.Issue.ProjectId == p.Id));
        var issue = await db.Issues.AsNoTracking().SingleAsync(i => i.ProjectId == p.Id);
        Assert.Contains($"[a.png]({Url})", issue.Description);
        Assert.Contains("(not copied)", issue.Description);
    }
}
