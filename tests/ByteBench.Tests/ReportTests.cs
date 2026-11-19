using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ByteBench.Data;
using ByteBench.Services;
using ByteBench.Services.Git;
using ByteBench.Services.Query;
using ByteBench.Services.Reports;
using ByteBench.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ByteBench.Tests;

/// <summary>Pure parts: percentiles, timelines, buckets and DORA bands.</summary>
public class ReportMathTests
{
    [Fact]
    public void Percentiles_interpolate_between_ranks()
    {
        Assert.Equal(0, ReportMath.Percentile([], 50));
        Assert.Equal(7, ReportMath.Percentile([7], 95));
        Assert.Equal(3, ReportMath.Percentile([10, 1, 3], 50));
        Assert.Equal(7.9, ReportMath.Percentile([10, 1, 3], 85), 6);
        Assert.Equal(9.3, ReportMath.Percentile([10, 1, 3], 95), 6);
        var s = ReportMath.Summarize([1, 2, 3, 4])!;
        Assert.Equal((4, 2.5, 2.5, 4.0), (s.Count, s.Average, s.P50, s.Max));
        Assert.Null(ReportMath.Summarize([]));
    }

    [Fact]
    public void Status_changes_parse_and_build_a_timeline()
    {
        Assert.True(ReportMath.TryParseStatusChange("changed status from **To Do** to **In Progress**", out var from, out var to));
        Assert.Equal(("To Do", "In Progress"), (from, to));
        Assert.True(ReportMath.TryParseStatusChange("changed status from **A** to **B** (the status \"A\" was removed from the workflow)", out _, out _));
        Assert.False(ReportMath.TryParseStatusChange("estimated **5** story points", out _, out _));

        var t0 = new DateTime(2026, 11, 1);
        IssueStatus Cat(string n) => n switch { "Done" => IssueStatus.Done, "In Progress" => IssueStatus.InProgress, "QA" => IssueStatus.InReview, _ => IssueStatus.Todo };
        var tl = ReportMath.BuildTimeline(t0, "Done", [(t0.AddDays(3), "In Progress", "QA"), (t0.AddDays(1), "To Do", "In Progress"), (t0.AddDays(5), "QA", "Done")], Cat);
        Assert.Equal(["To Do", "In Progress", "QA", "Done"], tl.Segments.Select(s => s.Status));
        Assert.Equal(t0.AddDays(1), tl.StartedAt);
        Assert.Equal(t0.AddDays(5), tl.DoneAt);
        Assert.Null(tl.Segments[^1].End);

        // No changes: it has always been in its current status.
        var plain = ReportMath.BuildTimeline(t0, "To Do", [], Cat);
        Assert.Single(plain.Segments);
        Assert.Null(plain.StartedAt);
        Assert.Null(plain.DoneAt);
    }

    [Fact]
    public void Buckets_periods_and_bands()
    {
        Assert.Equal(0, ReportMath.BucketOf(0.5));
        Assert.Equal(1, ReportMath.BucketOf(1));
        Assert.Equal(ReportMath.BucketBounds.Length, ReportMath.BucketOf(400));
        Assert.Equal("< 1d", ReportMath.BucketLabel(0));
        Assert.Equal("55d+", ReportMath.BucketLabel(ReportMath.BucketBounds.Length));
        Assert.Equal(new DateTime(2026, 11, 2), ReportMath.PeriodStart(new DateTime(2026, 11, 8, 15, 0, 0), false)); // Sunday → previous Monday
        Assert.Equal(new DateTime(2026, 11, 1), ReportMath.PeriodStart(new DateTime(2026, 11, 20), true));
        Assert.Equal(4, ReportMath.Periods(new DateTime(2026, 11, 1), new DateTime(2026, 11, 16), false).Count); // weeks of Oct 26, Nov 2, 9 and 16

        Assert.Equal(["Elite", "High", "Medium", "Low"], new[] { ReportMath.FrequencyBand(2), ReportMath.FrequencyBand(0.2), ReportMath.FrequencyBand(0.05), ReportMath.FrequencyBand(0.01) });
        Assert.Equal(["Elite", "High", "Medium", "Low"], new[] { ReportMath.LeadTimeBand(5), ReportMath.LeadTimeBand(48), ReportMath.LeadTimeBand(24 * 10), ReportMath.LeadTimeBand(24 * 60) });
        Assert.Equal(["Elite", "High", "Medium", "Low"], new[] { ReportMath.FailureRateBand(0.03), ReportMath.FailureRateBand(0.08), ReportMath.FailureRateBand(0.12), ReportMath.FailureRateBand(0.5) });
        Assert.Equal(["Elite", "High", "Medium", "Low"], new[] { ReportMath.RestoreBand(0.5), ReportMath.RestoreBand(5), ReportMath.RestoreBand(48), ReportMath.RestoreBand(24 * 8) });
        Assert.Equal(4, ReportMath.Bands["frequency"].Count);
    }

    [Fact]
    public void Pull_request_sizes_are_classified()
    {
        Assert.Equal(["XS", "S", "M", "L", "XL"], new[] { 3, 11, 250, 251, 5000 }.Select(PullRequestReportService.SizeClass));
    }
}

/// <summary>Reports over seeded issue history, deployments and pull requests, through the services, the API and the CSV download.</summary>
public class ReportTests(TestApp app) : IClassFixture<TestApp>
{
    private static readonly DateTime Now = new(2026, 11, 30, 12, 0, 0, DateTimeKind.Utc);
    private static DateTime Nov(int day, int hour = 9) => new(2026, 11, day, hour, 0, 0, DateTimeKind.Utc);
    private static readonly ReportFilter Range = new() { From = new DateTime(2026, 11, 1), To = new DateTime(2026, 11, 30), Now = Now };

    // ---------- seeding ----------

    /// <summary>An issue whose created time and status history are written directly (as IssueService would have, but in the past).</summary>
    private async Task<Issue> SeedAsync(Project project, string title, DateTime created, IssueType type = IssueType.Task, string? assignee = null,
        int? points = null, string? currentStatus = null, params (DateTime At, string From, string To)[] steps)
    {
        var issue = await app.CreateIssueAsync(project.Id, "alice", title, type, storyPoints: points);
        var finalStatus = steps.Length > 0 ? steps.OrderBy(s => s.At).Last().To : currentStatus ?? "To Do";
        await using var db = app.Db();
        var ws = await db.WorkflowStatuses.Where(w => w.ProjectId == project.Id).ToListAsync();
        var target = ws.FirstOrDefault(w => w.Name == finalStatus) ?? throw new InvalidOperationException($"No workflow status '{finalStatus}'.");
        var i = await db.Issues.FirstAsync(x => x.Id == issue.Id);
        i.CreatedAt = created;
        i.Status = target.Category;
        i.WorkflowStatusId = target.Id;
        i.ClosedAt = target.Category == IssueStatus.Done ? steps.Max(s => s.At) : null;
        i.AssigneeId = assignee == null ? null : app.UserId(assignee);
        foreach (var s in steps)
            db.Activities.Add(new Activity { IssueId = issue.Id, ActorId = app.UserId("alice"), CreatedAt = s.At, Text = $"changed status from **{s.From}** to **{s.To}**" });
        await db.SaveChangesAsync();
        return i;
    }

    /// <summary>A project with the issue history the flow tests use.</summary>
    private async Task<Project> FlowProjectAsync()
    {
        var project = await app.CreateProjectAsync("alice", ProjectVisibility.Private, ("bob", ProjectRole.Developer), ("dan", ProjectRole.Viewer));
        await SeedAsync(project, "A", Nov(1), points: 3, steps: [(Nov(2), "To Do", "In Progress"), (Nov(4), "In Progress", "Done")]);
        await SeedAsync(project, "B", Nov(1), IssueType.Bug, "bob", 5,
            steps: [(Nov(3), "To Do", "In Progress"), (Nov(5), "In Progress", "In Review"), (Nov(11), "In Review", "Done")]);
        await SeedAsync(project, "=C", Nov(10), steps: [(Nov(11), "To Do", "Done")]); // straight to done
        await SeedAsync(project, "D", Nov(8), steps: [(Nov(9), "To Do", "In Progress"), (Nov(10), "In Progress", "In Review")]); // still in review
        await SeedAsync(project, "E", new DateTime(2026, 10, 10, 9, 0, 0, DateTimeKind.Utc),
            steps: [(new DateTime(2026, 10, 12, 9, 0, 0, DateTimeKind.Utc), "To Do", "In Progress"), (new DateTime(2026, 10, 20, 9, 0, 0, DateTimeKind.Utc), "In Progress", "Done")]);
        await SeedAsync(project, "F", Nov(20)); // untouched, To Do
        return project;
    }

    // ---------- cycle & lead time ----------

    [Fact]
    public async Task Cycle_and_lead_time_percentiles_histogram_trend_and_drill_down()
    {
        var project = await FlowProjectAsync();
        await using var dan = app.Scope(app.UserId("dan")); // a Viewer may read reports
        var r = await dan.Get<DeliveryReportService>().CycleTimeAsync(project.Id, Range);

        // Lead: A 3d, B 10d, C 1d. Cycle: A 2d, B 8d (C went straight to Done).
        Assert.Equal(3, r.Lead!.Count);
        Assert.Equal((3.0, 7.9, 9.3, 10.0), (r.Lead.P50, r.Lead.P85, r.Lead.P95, r.Lead.Max));
        Assert.Equal(2, r.Cycle!.Count);
        Assert.Equal((5.0, 7.1), (r.Cycle.P50, r.Cycle.P85));
        Assert.Equal(1, r.WithoutStart);
        Assert.Equal(3, r.LeadHistogram.Sum(b => b.Count));
        Assert.Equal(1, r.LeadHistogram.Single(b => b.Label == "1–2d").Count); // C
        Assert.Equal(1, r.LeadHistogram.Single(b => b.Label == "3–5d").Count); // A
        Assert.Equal(1, r.LeadHistogram.Single(b => b.Label == "8–13d").Count); // B

        // Drill-down rows, newest finish first; the week of Nov 9 holds B and C.
        Assert.Equal(["B", "=C", "A"], r.Issues.Select(i => i.Title).Where(t => t != "E"));
        Assert.DoesNotContain(r.Issues, i => i.Title == "E"); // finished in October
        var week = r.Trend.Single(t => t.Period == new DateTime(2026, 11, 9));
        Assert.Equal(2, week.Count);
        Assert.Equal(8, week.CycleP50);
        Assert.Equal(5.5, week.LeadP50);
        Assert.Equal($"/p/{project.Key}/issues/{r.Issues[0].Key.Split('-')[1]}", r.Issues[0].Url);

        // Filters narrow the issues before anything is measured.
        var bugs = await dan.Get<DeliveryReportService>().CycleTimeAsync(project.Id, Range with { Type = IssueType.Bug });
        Assert.Equal(["B"], bugs.Issues.Select(i => i.Title));
        var bob = await dan.Get<DeliveryReportService>().CycleTimeAsync(project.Id, Range with { AssigneeId = app.UserId("bob") });
        Assert.Equal(["B"], bob.Issues.Select(i => i.Title));
        var unassigned = await dan.Get<DeliveryReportService>().CycleTimeAsync(project.Id, Range with { AssigneeId = 0 });
        Assert.Equal(["=C", "A"], unassigned.Issues.Select(i => i.Title));
        var late = await dan.Get<DeliveryReportService>().CycleTimeAsync(project.Id, Range with { From = new DateTime(2026, 11, 5) });
        Assert.Equal(["B", "=C"], late.Issues.Select(i => i.Title));
        var none = await dan.Get<DeliveryReportService>().CycleTimeAsync(project.Id, Range with { Type = IssueType.Epic });
        Assert.Null(none.Lead);
        Assert.Empty(none.Issues);
    }

    [Fact]
    public async Task Label_and_component_filters_apply()
    {
        var project = await FlowProjectAsync();
        await using (var db = app.Db())
        {
            var label = new Label { ProjectId = project.Id, Name = "backend" };
            var comp = new ProjectComponent { ProjectId = project.Id, Name = "api" };
            db.Labels.Add(label);
            db.ProjectComponents.Add(comp);
            await db.SaveChangesAsync();
            var a = await db.Issues.Include(i => i.Labels).Include(i => i.Components).FirstAsync(i => i.ProjectId == project.Id && i.Title == "A");
            a.Labels.Add(label);
            a.Components.Add(comp);
            await db.SaveChangesAsync();
        }
        await using var s = app.Scope(app.UserId("bob"));
        var reports = s.Get<ReportService>();
        var filter = await reports.ParseFilterAsync(project.Id, n => n switch { "label" => "backend", "component" => "api", "from" => "2026-11-01", "to" => "2026-11-30", _ => null });
        Assert.NotNull(filter.LabelId);
        Assert.NotNull(filter.ComponentId);
        var r = await s.Get<DeliveryReportService>().CycleTimeAsync(project.Id, filter with { Now = Now });
        Assert.Equal(["A"], r.Issues.Select(i => i.Title));
        await Assert.ThrowsAsync<InvalidOperationException>(() => reports.ParseFilterAsync(project.Id, n => n == "label" ? "nope" : null));
        await Assert.ThrowsAsync<InvalidOperationException>(() => reports.ParseFilterAsync(project.Id, n => n == "from" ? "yesterday" : null));
    }

    // ---------- time in status, CFD, throughput, WIP ----------

    [Fact]
    public async Task Time_in_status_flags_the_bottleneck_and_lists_issues()
    {
        var project = await FlowProjectAsync();
        await using var s = app.Scope(app.UserId("bob"));
        var r = await s.Get<DeliveryReportService>().TimeInStatusAsync(project.Id, Range);

        // In Review: B 6d, D from Nov 10 09:00 to now (Nov 30 12:00) 20d3h. In Progress: A 2d, B 2d, D 1d.
        var review = r.Statuses.Single(x => x.Status == "In Review");
        Assert.Equal(2, review.Issues);
        Assert.Equal(13.06, review.AvgDays, 2);
        Assert.True(review.Bottleneck);
        Assert.Equal(1.67, r.Statuses.Single(x => x.Status == "In Progress").AvgDays, 2);
        Assert.Equal(["In Review"], r.Statuses.Where(x => x.Bottleneck).Select(x => x.Status));
        Assert.DoesNotContain(r.Statuses, x => x.Category == IssueStatus.Done); // finished work is not "time in status"

        var top = r.Issues[0]; // longest first
        Assert.Equal("D", top.Title);
        Assert.Equal("In Review", top.LongestStatus);
        Assert.Equal(20.125, top.Days["In Review"], 1);
        Assert.Equal(100, r.Statuses.Sum(x => x.SharePercent), 0);
    }

    [Fact]
    public async Task Cumulative_flow_counts_issues_per_status_per_day()
    {
        var project = await FlowProjectAsync();
        await using var s = app.Scope(app.UserId("bob"));
        var r = await s.Get<DeliveryReportService>().CumulativeFlowAsync(project.Id, Range with { To = new DateTime(2026, 11, 12) });
        Assert.Equal(12, r.Days.Count);
        Assert.Equal("Done", r.Series[0].Status); // Done is the bottom band
        int At(int day, string status) => r.Days[day - 1].Counts[r.Series.ToList().FindIndex(x => x.Status == status)];

        Assert.Equal((1, 2), (At(1, "Done"), At(1, "To Do"))); // E was finished in October; A and B are new
        Assert.Equal(2, At(3, "In Progress")); // A (since Nov 2) and B (since Nov 3)
        Assert.Equal(1, At(5, "In Review"));
        Assert.Equal(2, At(5, "Done")); // E and A
        Assert.Equal(2, At(10, "In Review")); // B still, and D moves in on Nov 10
        Assert.Equal(4, At(12, "Done")); // E, A, B and C
        Assert.Equal(1, At(12, "In Review")); // D
        // Every day accounts for every issue created by then.
        Assert.All(r.Days, d => Assert.True(d.Counts.Sum() >= 3));
    }

    [Fact]
    public async Task Throughput_per_week_and_sprint_and_current_wip()
    {
        var project = await FlowProjectAsync();
        await using (var db = app.Db())
        {
            db.Sprints.Add(new Sprint { ProjectId = project.Id, Name = "Sprint 1", State = SprintState.Completed, CompletedAt = Nov(14), CommittedCount = 5, CompletedCount = 4, CommittedPoints = 13, CompletedPoints = 8 });
            await db.SaveChangesAsync();
        }
        await using var s = app.Scope(app.UserId("bob"));
        var delivery = s.Get<DeliveryReportService>();
        var t = await delivery.ThroughputAsync(project.Id, Range);
        var w1 = t.Periods.Single(p => p.Period == new DateTime(2026, 11, 2));
        var w2 = t.Periods.Single(p => p.Period == new DateTime(2026, 11, 9));
        Assert.Equal((1, 3), (w1.Done, w1.Points)); // A on Nov 4
        Assert.Equal((2, 5), (w2.Done, w2.Points)); // B and C on Nov 11
        Assert.Equal(3, t.Periods.Sum(p => p.Done));
        var sprint = Assert.Single(t.Sprints);
        Assert.Equal(("Sprint 1", 4, 8), (sprint.Name, sprint.CompletedCount, sprint.CompletedPoints));
        var monthly = await delivery.ThroughputAsync(project.Id, Range with { Monthly = true });
        Assert.Equal(3, monthly.Periods.Sum(p => p.Done));
        Assert.Equal(new DateTime(2026, 11, 1), Assert.Single(monthly.Periods).Period);

        var wip = await delivery.WipAsync(project.Id, new ReportFilter());
        Assert.Equal(2, wip.Total); // D (In Review) and F (To Do)
        Assert.Equal(1, wip.InProgress);
        Assert.Equal(new[] { "To Do", "In Review" }.OrderBy(x => x), wip.Rows.Select(x => x.Status).OrderBy(x => x));
    }

    [Fact]
    public async Task Reports_need_ViewProject_and_hide_other_projects()
    {
        var project = await FlowProjectAsync();
        var outsider = await app.CreateUserAsync("report-outsider");
        await using var s = app.Scope(outsider.Id);
        await Assert.ThrowsAsync<ForbiddenException>(() => s.Get<DeliveryReportService>().CycleTimeAsync(project.Id, Range));
        await Assert.ThrowsAsync<ForbiddenException>(() => s.Get<DeliveryReportService>().TimeInStatusAsync(project.Id, Range));
        await Assert.ThrowsAsync<ForbiddenException>(() => s.Get<DeliveryReportService>().CumulativeFlowAsync(project.Id, Range));
        await Assert.ThrowsAsync<ForbiddenException>(() => s.Get<DeliveryReportService>().ThroughputAsync(project.Id, Range));
        await Assert.ThrowsAsync<ForbiddenException>(() => s.Get<DeliveryReportService>().WipAsync(project.Id, Range));
        await Assert.ThrowsAsync<ForbiddenException>(() => s.Get<DoraReportService>().ReportAsync(project.Id, Range));
        await Assert.ThrowsAsync<ForbiddenException>(() => s.Get<PullRequestReportService>().ReportAsync(project.Id, Range));
    }

    [Fact]
    public async Task Reports_are_efficient_on_thousands_of_issues()
    {
        var project = await app.CreateProjectAsync("alice");
        await using (var db = app.Db())
        {
            var ws = await db.WorkflowStatuses.Where(w => w.ProjectId == project.Id).ToListAsync();
            var todo = ws.First(w => w.Category == IssueStatus.Todo);
            var progress = ws.First(w => w.Category == IssueStatus.InProgress);
            var done = ws.First(w => w.Category == IssueStatus.Done);
            var alice = app.UserId("alice");
            for (var k = 0; k < 3000; k++)
            {
                var created = Nov(1).AddHours(k % 400);
                var issue = new Issue { ProjectId = project.Id, Number = 1000 + k, Title = "Bulk " + k, ReporterId = alice, CreatedAt = created, Status = IssueStatus.Done,
                    WorkflowStatusId = done.Id, ClosedAt = created.AddDays(2 + k % 5) };
                issue.Activities.Add(new Activity { ActorId = alice, CreatedAt = created.AddDays(1), Text = $"changed status from **{todo.Name}** to **{progress.Name}**" });
                issue.Activities.Add(new Activity { ActorId = alice, CreatedAt = issue.ClosedAt.Value, Text = $"changed status from **{progress.Name}** to **{done.Name}**" });
                db.Issues.Add(issue);
            }
            await db.SaveChangesAsync();
        }
        await using var s = app.Scope(app.UserId("alice"));
        var delivery = s.Get<DeliveryReportService>();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var cycle = await delivery.CycleTimeAsync(project.Id, Range);
        var cfd = await delivery.CumulativeFlowAsync(project.Id, Range);
        var tis = await delivery.TimeInStatusAsync(project.Id, Range);
        watch.Stop();
        Assert.Equal(3000, cycle.Lead!.Count);
        Assert.Equal(3000, cfd.Days[^1].Counts.Sum());
        Assert.Equal(3000, tis.IssueCount);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20), $"took {watch.Elapsed}");
    }

    // ---------- DORA ----------

    private string Path(Repository repo) => app.Services.GetRequiredService<RepoStorage>().PathFor(app.ProjectKey(repo.ProjectId), repo.Slug);

    private string CommitAt(Repository repo, string file, DateTime when, string branch = "main") =>
        GitOps.CommitFiles(Path(repo), branch, new Dictionary<string, string?> { [file] = file + "\n" }, "add " + file, new GitIdentity("Dev", "dev@example.com"),
            new DateTimeOffset(DateTime.SpecifyKind(when, DateTimeKind.Utc)), "main");

    private async Task<int> DeployAsync(Repository repo, CiEnvironment env, string sha, DateTime finished, PipelineStatus status = PipelineStatus.Passed, bool rollback = false, DateTime? created = null)
    {
        await using var db = app.Db();
        var p = new Pipeline { RepositoryId = repo.Id, Ref = "main", CommitSha = sha, Status = PipelineStatus.Passed, CreatedAt = created ?? finished };
        var job = new PipelineJob { Pipeline = p, Name = "deploy", Image = "alpine", Status = status, EnvironmentName = env.Name };
        var d = new Deployment { EnvironmentId = env.Id, Job = job, PipelineId = 0, CommitSha = sha, Ref = "main", Status = status, CreatedAt = created ?? finished,
            FinishedAt = finished, IsRollback = rollback };
        db.Pipelines.Add(p);
        db.PipelineJobs.Add(job);
        await db.SaveChangesAsync();
        d.PipelineId = p.Id;
        db.Deployments.Add(d);
        await db.SaveChangesAsync();
        return d.Id;
    }

    [Fact]
    public async Task Dora_metrics_from_production_deployments()
    {
        var project = await app.CreateProjectAsync("alice", ProjectVisibility.Private, ("bob", ProjectRole.Developer), ("dan", ProjectRole.Viewer));
        var repo = await app.CreateRepoAsync(project.Id, "alice", "svc");
        var c1 = CommitAt(repo, "c1", Nov(3, 8));
        var c2 = CommitAt(repo, "c2", Nov(5, 6));
        var c3 = CommitAt(repo, "c3", Nov(5, 9));
        var c4 = CommitAt(repo, "c4", Nov(8, 10));
        CommitAt(repo, "c5", Nov(12, 10));
        var c6 = CommitAt(repo, "c6", Nov(12, 11));
        CiEnvironment prod, staging;
        await using (var db = app.Db())
        {
            prod = new CiEnvironment { RepositoryId = repo.Id, Name = "production" };
            staging = new CiEnvironment { RepositoryId = repo.Id, Name = "staging" };
            db.CiEnvironments.AddRange(prod, staging);
            await db.SaveChangesAsync();
        }
        await DeployAsync(repo, prod, c1, Nov(3, 12));                                         // baseline: no previous deployment, so no lead time
        await DeployAsync(repo, prod, c3, Nov(5, 12));                                         // ships c2 (6h) and c3 (3h)
        var d3 = await DeployAsync(repo, prod, c4, Nov(8, 12));                                // ships c4 (2h); rolled back below
        await DeployAsync(repo, prod, c3, Nov(8, 15) + TimeSpan.FromMinutes(30), rollback: true, created: Nov(8, 15)); // rollback: restores 3.5h later
        await DeployAsync(repo, prod, c6, Nov(12, 12));                                        // ships c4 again (98h), c5 (2h), c6 (1h)
        await DeployAsync(repo, prod, c6, Nov(12, 18), PipelineStatus.Failed);                 // a failed deploy within 24h: the previous one failed
        await DeployAsync(repo, prod, c6, Nov(13, 8));                                         // the fix; restores 20h after Nov 12 12:00
        var d8 = await DeployAsync(repo, prod, c6, Nov(20, 12));                               // followed by a hotfix issue, never restored
        await DeployAsync(repo, staging, c6, Nov(21, 12));                                     // other environments don't count
        await using (var db = app.Db())
        {
            var label = new Label { ProjectId = project.Id, Name = "Hotfix" };
            db.Labels.Add(label);
            await db.SaveChangesAsync();
            var hot = await app.CreateIssueAsync(project.Id, "alice", "Fix checkout", IssueType.Bug);
            var i = await db.Issues.Include(x => x.Labels).FirstAsync(x => x.Id == hot.Id);
            i.CreatedAt = Nov(20, 14);
            i.Labels.Add(label);
            await db.SaveChangesAsync();
        }

        await using var dan = app.Scope(app.UserId("dan"));
        var dora = dan.Get<DoraReportService>();
        var r = await dora.ReportAsync(project.Id, Range); // production not configured: found by its name
        Assert.Equal("production", r.ProductionEnvironment);
        Assert.False(r.Configured);
        Assert.Null(r.Notice);
        Assert.Equal(24, r.FailureWindowHours);

        // Frequency: D1, D2, D3, D5, D7, D8 (the rollback and the failed deployment are not deployments).
        Assert.Equal(6, r.Deployments);
        Assert.Equal(0.2, r.PerDay, 3);
        Assert.Equal("High", r.FrequencyBand);

        // Lead time: c2 6h, c3 3h, c4 2h, c4 98h, c5 2h, c6 1h.
        Assert.Equal(6, r.CommitsMeasured);
        Assert.Equal(2.5, r.LeadTimeHours!.P50);
        Assert.Equal(98, r.LeadTimeHours.Max);
        Assert.Equal("Elite", r.LeadTimeBand);

        // Failures: the rolled-back deployment, the one followed by a failed deploy, and the one followed by a hotfix issue.
        Assert.Equal(3, r.Failures);
        Assert.Equal(0.5, r.FailureRate);
        Assert.Equal("Low", r.FailureBand);
        var failed = r.Rows.Where(x => x.Failed).OrderBy(x => x.FinishedAt).ToList();
        Assert.Equal(["rollback", "failed deployment", "hotfix issue"], failed.Select(x => x.FailureSignal));
        Assert.Equal(d3, failed[0].Id);
        Assert.Equal(d8, failed[2].Id);
        Assert.Equal(3.5, failed[0].RestoreHours);
        Assert.Equal(20, failed[1].RestoreHours);
        Assert.Null(failed[2].RestoreHours);
        Assert.Equal((2, 1), (r.Restored, r.Unrestored));
        Assert.Equal(11.75, r.RestoreMedianHours);
        Assert.Equal("High", r.RestoreBand);
        Assert.Equal(4, r.Bands.Count);
        Assert.Equal(6, r.Frequency.Sum(f => f.Deployments));

        // A shorter window (4h) keeps the rollback (3h after) and the hotfix (2h after) but drops the failed deploy (6h after).
        // Setting it needs ManageProjectSettings.
        await Assert.ThrowsAsync<ForbiddenException>(() => dora.SetSettingsAsync(project.Id, "production", 4));
        await using var alice = app.Scope(app.UserId("alice"));
        await alice.Get<DoraReportService>().SetSettingsAsync(project.Id, "production", 4);
        var shorter = await alice.Get<DoraReportService>().ReportAsync(project.Id, Range);
        Assert.True(shorter.Configured);
        Assert.Equal(4, shorter.FailureWindowHours);
        Assert.Equal(2, shorter.Failures);
        Assert.Equal((1, 1), (shorter.Restored, shorter.Unrestored));
    }

    [Fact]
    public async Task Dora_without_a_production_environment_explains_why()
    {
        var project = await app.CreateProjectAsync("alice");
        await using var s = app.Scope(app.UserId("alice"));
        var dora = s.Get<DoraReportService>();
        var r = await dora.ReportAsync(project.Id, Range);
        Assert.NotNull(r.Notice);
        Assert.Equal(0, r.Deployments);
        Assert.Null(r.FrequencyBand);
        await dora.SetSettingsAsync(project.Id, "prod-eu", null);
        var r2 = await dora.ReportAsync(project.Id, Range);
        Assert.True(r2.Configured);
        Assert.Contains("prod-eu", r2.Notice);
        await Assert.ThrowsAsync<InvalidOperationException>(() => dora.SetSettingsAsync(project.Id, "bad name!", null));
        await Assert.ThrowsAsync<InvalidOperationException>(() => dora.SetSettingsAsync(project.Id, null, 0));
        await using var db = app.Db();
        Assert.Contains(await db.AuditEvents.Where(a => a.ProjectId == project.Id).Select(a => a.Action).ToListAsync(), a => a == "project.dora_settings_changed");
    }

    // ---------- pull requests ----------

    [Fact]
    public async Task Pull_request_metrics()
    {
        var project = await app.CreateProjectAsync("alice", ProjectVisibility.Private, ("bob", ProjectRole.Developer), ("carol", ProjectRole.Developer), ("dan", ProjectRole.Viewer));
        var repo = await app.CreateRepoAsync(project.Id, "alice", "web");
        app.Commit(repo, "main", "base.txt", "x\n");
        app.Commit(repo, "feature", "a.txt", "1\n2\n3\n", from: "main");
        var bob = app.UserId("bob");
        await using (var db = app.Db())
        {
            var merged = new PullRequest { RepositoryId = repo.Id, Number = 1, Title = "Merged one", SourceBranch = "gone", TargetBranch = "main", AuthorId = bob, State = PullRequestState.Merged,
                CreatedAt = Nov(10), ClosedAt = Nov(12) };
            merged.Reviews.Add(new PullRequestReview { UserId = app.UserId("carol"), State = ReviewState.Approved, CreatedAt = Nov(10, 11) });
            merged.Reviews.Add(new PullRequestReview { UserId = bob, State = ReviewState.Approved, CreatedAt = Nov(10, 9) }); // the author's own doesn't count
            var open = new PullRequest { RepositoryId = repo.Id, Number = 2, Title = "Open one", SourceBranch = "feature", TargetBranch = "main", AuthorId = bob, CreatedAt = Nov(11, 8) };
            open.Comments.Add(new PullRequestComment { AuthorId = bob, Body = "self", CreatedAt = Nov(11, 8) + TimeSpan.FromMinutes(30) });
            open.Comments.Add(new PullRequestComment { AuthorId = app.UserId("dan"), Body = "looks odd", CreatedAt = Nov(11, 20) });
            var old = new PullRequest { RepositoryId = repo.Id, Number = 3, Title = "Old", SourceBranch = "old", TargetBranch = "main", AuthorId = bob, CreatedAt = new DateTime(2026, 9, 1), State = PullRequestState.Declined };
            db.PullRequests.AddRange(merged, open, old);
            await db.SaveChangesAsync();
        }

        await using var s = app.Scope(app.UserId("dan"));
        var r = await s.Get<PullRequestReportService>().ReportAsync(project.Id, Range);
        Assert.Equal((2, 1, 2), (r.PullRequests, r.Merged, r.Reviewed));
        Assert.Equal(7, r.TimeToFirstReviewHours!.P50); // 2h and 12h
        Assert.Equal(48, r.TimeToMergeHours!.P50);
        var open2 = r.Rows.Single(x => x.Number == 2);
        Assert.Equal(12, open2.HoursToFirstReview);
        Assert.Equal((3, 1, "XS"), (open2.Size, open2.Files, open2.SizeClass));
        Assert.Null(r.Rows.Single(x => x.Number == 1).Size); // its branch is gone and no tips were recorded
        Assert.Equal(1, r.SizeBuckets.Single(b => b.Name == "XS").Count);
        Assert.DoesNotContain(r.Rows, x => x.Number == 3);
        Assert.Equal($"/p/{project.Key}/repos/web/pull-requests/2", open2.Url(project.Key));
    }

    // ---------- CSV and REST ----------

    [Fact]
    public async Task Csv_of_every_report_is_well_formed_and_escaped()
    {
        var project = await FlowProjectAsync();
        await using var s = app.Scope(app.UserId("bob"));
        var reports = s.Get<ReportService>();
        foreach (var name in ReportService.CsvNames)
        {
            var csv = await reports.CsvAsync(name, project.Id, Range);
            var rows = Csv.Parse(csv);
            Assert.NotEmpty(rows);
            Assert.All(rows, row => Assert.Equal(rows[0].Count, row.Count));
        }
        var cycle = Csv.Parse(await reports.CsvAsync("cycle-time", project.Id, Range));
        Assert.Equal(["key", "title", "type", "assignee", "created", "started", "done", "lead_time_days", "cycle_time_days"], cycle[0]);
        Assert.Equal(4, cycle.Count); // header + A, B, C
        Assert.Contains(cycle, r => r[1] == "'=C"); // formula injection is neutralised
        var c = cycle.Single(r => r[1] == "'=C");
        Assert.Equal(("", "1"), (c[5], c[7]));
        var tis = Csv.Parse(await reports.CsvAsync("time-in-status-issues", project.Id, Range));
        Assert.Contains("days_in_In Review", tis[0]);
        var cfd = Csv.Parse(await reports.CsvAsync("cumulative-flow", project.Id, Range));
        Assert.Equal("date", cfd[0][0]);
        Assert.Equal(30, cfd.Count - 1); // one row per day, Nov 1–30
    }

    [Fact]
    public async Task Rest_api_serves_each_report_as_json_or_csv_and_respects_access()
    {
        var project = await FlowProjectAsync();
        var q = "?from=2026-11-01&to=2026-11-30";
        using var bob = await app.ApiClientAsync("bob");
        foreach (var name in ReportService.Names)
        {
            var res = await bob.GetAsync($"/api/v1/projects/{project.Key}/reports/{name}{q}");
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            using var json = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            Assert.Equal(JsonValueKind.Object, json.RootElement.ValueKind);
        }
        using (var json = JsonDocument.Parse(await bob.GetStringAsync($"/api/v1/projects/{project.Key}/reports/cycle-time{q}&type=bug")))
        {
            Assert.Equal(1, json.RootElement.GetProperty("issues").GetArrayLength());
            Assert.Equal("b", json.RootElement.GetProperty("issues")[0].GetProperty("title").GetString()!.ToLowerInvariant());
        }
        var csv = await bob.GetAsync($"/api/v1/projects/{project.Key}/reports/cycle-time{q}&format=csv");
        Assert.Equal("text/csv", csv.Content.Headers.ContentType?.MediaType);
        Assert.StartsWith("key,title,type", Csv.Parse(await csv.Content.ReadAsStringAsync())[0].Aggregate("", (a, b) => a + (a == "" ? "" : ",") + b));
        var sprints = await bob.GetAsync($"/api/v1/projects/{project.Key}/reports/throughput{q}&format=csv&view=sprints");
        Assert.Equal(HttpStatusCode.OK, sprints.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await bob.GetAsync($"/api/v1/projects/{project.Key}/reports/cycle-time?type=nonsense")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await bob.GetAsync($"/api/v1/projects/{project.Key}/reports/cycle-time?view=issues")).StatusCode);

        var outsider = await app.CreateUserAsync("report-api-outsider");
        using var o = await app.ApiClientAsync(outsider.UserName!);
        Assert.Equal(HttpStatusCode.NotFound, (await o.GetAsync($"/api/v1/projects/{project.Key}/reports/dora")).StatusCode);

        // DORA settings: readable by a Viewer, writable by a Maintainer+.
        using var dan = await app.ApiClientAsync("dan");
        Assert.Equal(HttpStatusCode.OK, (await dan.GetAsync($"/api/v1/projects/{project.Key}/reports/dora/settings")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await dan.PutAsJsonAsync($"/api/v1/projects/{project.Key}/reports/dora/settings", new { productionEnvironment = "production" })).StatusCode);
        using var alice = await app.ApiClientAsync("alice");
        var set = await alice.PutAsJsonAsync($"/api/v1/projects/{project.Key}/reports/dora/settings", new { productionEnvironment = "production", failureWindowHours = 12 });
        Assert.Equal(HttpStatusCode.OK, set.StatusCode);
        using var body = JsonDocument.Parse(await set.Content.ReadAsStringAsync());
        Assert.Equal(("production", 12), (body.RootElement.GetProperty("productionEnvironment").GetString(), body.RootElement.GetProperty("failureWindowHours").GetInt32()));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await alice.PutAsJsonAsync($"/api/v1/projects/{project.Key}/reports/dora/settings", new { failureWindowHours = 5000 })).StatusCode);
    }

    [Fact]
    public async Task Browser_csv_download_uses_the_session_and_access()
    {
        var project = await FlowProjectAsync();
        using var bob = await app.CookieClientAsync("bob");
        var r = await bob.GetAsync($"/p/{project.Key}/reports/cycle-time.csv?from=2026-11-01&to=2026-11-30");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("text/csv", r.Content.Headers.ContentType?.MediaType);
        Assert.Equal(4, Csv.Parse(await r.Content.ReadAsStringAsync()).Count);
        Assert.Equal(HttpStatusCode.NotFound, (await bob.GetAsync($"/p/{project.Key}/reports/nope.csv")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await bob.GetAsync($"/p/{project.Key}/reports/cycle-time.csv?from=soon")).StatusCode);
        var outsider = await app.CreateUserAsync("report-csv-outsider");
        using var o = await app.CookieClientAsync(outsider.UserName!);
        Assert.Equal(HttpStatusCode.NotFound, (await o.GetAsync($"/p/{project.Key}/reports/cycle-time.csv")).StatusCode);
    }
}
