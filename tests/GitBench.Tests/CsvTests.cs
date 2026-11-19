using System.Net;
using System.Text;
using GitBench.Data;
using GitBench.Services;
using GitBench.Services.Query;
using GitBench.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace GitBench.Tests;

public class CsvTests(TestApp app) : IClassFixture<TestApp>
{
    [Fact]
    public void Parse_handles_quotes_line_breaks_and_bom()
    {
        var rows = Csv.Parse("﻿a,b,c\r\n1,\"x, \"\"y\"\"\",\"line\nbreak\"\n\n2,,\r\n");
        Assert.Equal(3, rows.Count);
        Assert.Equal(["a", "b", "c"], rows[0]);
        Assert.Equal(["1", "x, \"y\"", "line\nbreak"], rows[1]);
        Assert.Equal(["2", "", ""], rows[2]);
        Assert.Throws<InvalidOperationException>(() => Csv.Parse("a,\"b\n"));
        Assert.Throws<InvalidOperationException>(() => Csv.Parse("a\n1\n2\n3", maxRows: 2));
    }

    [Fact]
    public void Cells_escape_formulas()
    {
        Assert.Equal("'=SUM(A1)", Csv.Cell("=SUM(A1)"));
        Assert.Equal("\"'=1,2\"", Csv.Cell("=1,2"));
        Assert.Equal("'@x", Csv.Cell("@x"));
        Assert.Equal("plain", Csv.Cell("plain"));
        Assert.Equal("=SUM(A1)", Csv.Unescape(Csv.Cell("=SUM(A1)")));
        Assert.Equal("'quoted", Csv.Unescape("'quoted"));
        Assert.Equal([0xEF, 0xBB, 0xBF], Csv.Utf8WithBom("x").Take(3));
    }

    private async Task<(Project Project, List<int> Ids)> SourceProjectAsync()
    {
        var p = await app.CreateProjectAsync("carol", ProjectVisibility.Private, ("bob", ProjectRole.Developer));
        var carol = app.UserId("carol");
        await using var s = app.Scope(carol);
        var issues = s.Get<IssueService>();
        int[] labels;
        await using (var db = app.Db()) labels = db.Labels.Where(l => l.ProjectId == p.Id && l.Name != "documentation").Select(l => l.Id).ToArray();
        var epic = await issues.CreateIssueAsync(p.Id, new Issue { Title = "=HYPERLINK(\"evil\")", Type = IssueType.Epic, Priority = IssuePriority.High }, [], carol);
        var story = await issues.CreateIssueAsync(p.Id, new Issue
        {
            Title = "Story, with \"quotes\"", Description = "Multi\nline", Type = IssueType.Story, AssigneeId = app.UserId("bob"), StoryPoints = 5,
            DueDate = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc), ParentId = epic.Id, Status = IssueStatus.InProgress,
        }, labels, carol);
        var sub = await issues.CreateIssueAsync(p.Id, new Issue { Title = "Ünïcødé sub-task ✓", Type = IssueType.Subtask, ParentId = story.Id, Status = IssueStatus.Done }, [], carol);
        return (p, [epic.Id, story.Id, sub.Id]);
    }

    private static readonly string AllImportable = "key,title,description,type,priority,status,assignee,labels,storyPoints,due,parent";

    [Fact]
    public async Task Export_then_import_round_trips_every_field()
    {
        var (source, ids) = await SourceProjectAsync();
        var target = await app.CreateProjectAsync("carol", ProjectVisibility.Private, ("bob", ProjectRole.Developer));
        await using var s = app.Scope(app.UserId("carol"));
        var csv = await s.Get<IssueCsvExporter>().ExportAsync(ids, IssueCsvExporter.Pick(AllImportable));
        Assert.Contains("'=HYPERLINK", csv);

        var importer = s.Get<CsvImportService>();
        var table = importer.Read(Encoding.UTF8.GetString(Csv.Utf8WithBom(csv)));
        var map = CsvImportService.GuessMapping(table.Headers);
        Assert.Equal(11, map.Count);
        var preview = await importer.PreviewAsync(target.Id, table, map);
        Assert.Equal(0, preview.ErrorRows);
        var result = await importer.ImportAsync(target.Id, table, map, "round-trip.csv");
        Assert.Equal(3, result.Count);

        await using var db = app.Db();
        Issue[] Load(int projectId) => db.Issues.AsNoTracking().Include(i => i.Labels).Include(i => i.WorkflowStatus).Include(i => i.Parent)
            .Where(i => i.ProjectId == projectId).OrderBy(i => i.Number).ToArray();
        var before = Load(source.Id);
        var after = Load(target.Id);
        Assert.Equal(before.Length, after.Length);
        for (var i = 0; i < before.Length; i++)
        {
            var (a, b) = (before[i], after[i]);
            Assert.Equal(a.Title, b.Title);
            Assert.Equal(a.Description, b.Description);
            Assert.Equal(a.Type, b.Type);
            Assert.Equal(a.Priority, b.Priority);
            Assert.Equal(a.Status, b.Status);
            Assert.Equal(a.WorkflowStatus!.Name, b.WorkflowStatus!.Name);
            Assert.Equal(a.AssigneeId, b.AssigneeId);
            Assert.Equal(a.StoryPoints, b.StoryPoints);
            Assert.Equal(a.DueDate?.Date, b.DueDate?.Date);
            Assert.Equal(a.Labels.Select(l => l.Name).Order(), b.Labels.Select(l => l.Name).Order());
            Assert.Equal(a.Parent?.Number, b.Parent?.Number); // in-file parents are linked to the new issues
            Assert.Equal(b.Status == IssueStatus.Done, b.ClosedAt != null);
        }
        Assert.True(db.Activities.Any(x => x.IssueId == after[0].Id && x.Text == "imported from CSV"));
        Assert.True(db.AuditEvents.Any(e => e.Action == "issue.imported" && e.ProjectId == target.Id));
        Assert.Equal(4, db.Projects.Where(p => p.Id == target.Id).Select(p => p.NextIssueNumber).First());
    }

    [Fact]
    public async Task Preview_reports_every_row_error_and_import_refuses()
    {
        var (source, _) = await SourceProjectAsync();
        await using var s = app.Scope(app.UserId("carol"));
        var importer = s.Get<CsvImportService>();
        var table = importer.Read(
            "Title,Type,Priority,Status,Assignee,Labels,Story points,Due date,Parent,Key\n" +
            "Fine,Bug,High,In Progress,bob@example.com,bug,3,2026-01-02,,A\n" +
            ",Bug,,,,,,,,\n" +
            "Bad type,Banana,Urgent,Nowhere,nobody,nolabel,x,tomorrow,,\n" +
            "Orphan,Subtask,,,,,,,,\n" +
            "Bad parent,Task,,,,,,,ZZZ-1,\n" +
            "Child,Epic,,,,,,,A,\n" +
            "Dup,Task,,,dan,,101,,,A\n");
        var map = CsvImportService.GuessMapping(table.Headers);
        var preview = await importer.PreviewAsync(source.Id, table, map);
        string[] Errors(int line) => preview.Rows.Single(r => r.Line == line).Errors.ToArray();
        Assert.Empty(Errors(2));
        Assert.Contains("Title is empty.", Errors(3));
        Assert.Equal(7, Errors(4).Length); // type, priority, status, assignee, label, points, due
        Assert.Contains(Errors(4), e => e.Contains("Unknown status 'Nowhere'"));
        Assert.Contains("A sub-task needs a parent.", Errors(5));
        Assert.Contains(Errors(6), e => e.Contains("ZZZ-1"));
        Assert.Contains("An epic can't have a parent.", Errors(7));
        Assert.Contains(Errors(8), e => e.Contains("appears more than once"));
        Assert.Contains(Errors(8), e => e.Contains("'dan' is not a user who can see"));
        Assert.Contains(Errors(8), e => e.Contains("0 to 100"));

        var before = app.Db().Issues.Count(i => i.ProjectId == source.Id);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => importer.ImportAsync(source.Id, table, map));
        Assert.Contains("nothing was imported", ex.Message);
        Assert.Equal(before, app.Db().Issues.Count(i => i.ProjectId == source.Id));
    }

    [Fact]
    public async Task Import_needs_maintainer()
    {
        var (source, _) = await SourceProjectAsync();
        await using var s = app.Scope(app.UserId("bob"));
        var importer = s.Get<CsvImportService>();
        var table = importer.Read("Title\nHello\n");
        await Assert.ThrowsAsync<ForbiddenException>(() => importer.PreviewAsync(source.Id, table, CsvImportService.GuessMapping(table.Headers)));
        await Assert.ThrowsAsync<ForbiddenException>(() => importer.ImportAsync(source.Id, table, CsvImportService.GuessMapping(table.Headers)));
    }

    [Fact]
    public async Task A_failure_during_the_write_rolls_everything_back()
    {
        var (source, _) = await SourceProjectAsync();
        await using (var db = app.Db())
            await db.Database.ExecuteSqlRawAsync(TestApp.UsingPostgres
                ? "CREATE FUNCTION csv_test_fail_fn() RETURNS trigger AS $$ BEGIN IF NEW.\"Title\" = 'boom' THEN RAISE EXCEPTION 'boom'; END IF; RETURN NEW; END $$ LANGUAGE plpgsql; " +
                  "CREATE TRIGGER csv_test_fail BEFORE INSERT ON \"Issues\" FOR EACH ROW EXECUTE FUNCTION csv_test_fail_fn();"
                : "CREATE TRIGGER csv_test_fail BEFORE INSERT ON Issues WHEN NEW.Title = 'boom' BEGIN SELECT RAISE(ABORT, 'boom'); END;");
        try
        {
            await using var s = app.Scope(app.UserId("carol"));
            var importer = s.Get<CsvImportService>();
            var table = importer.Read("Title\nFirst\nSecond\nboom\nFourth\n");
            int Count() { using var db = app.Db(); return db.Issues.Count(i => i.ProjectId == source.Id); }
            int Next() { using var db = app.Db(); return db.Projects.Where(p => p.Id == source.Id).Select(p => p.NextIssueNumber).First(); }
            var (count, next) = (Count(), Next());
            await Assert.ThrowsAnyAsync<Exception>(() => importer.ImportAsync(source.Id, table, CsvImportService.GuessMapping(table.Headers)));
            Assert.Equal(count, Count());
            Assert.Equal(next, Next());
            await using var check = app.Db();
            Assert.False(check.Issues.Any(i => i.ProjectId == source.Id && i.Title == "First"));
        }
        finally
        {
            await using var db = app.Db();
            await db.Database.ExecuteSqlRawAsync(TestApp.UsingPostgres
                ? "DROP TRIGGER csv_test_fail ON \"Issues\"; DROP FUNCTION csv_test_fail_fn();"
                : "DROP TRIGGER csv_test_fail;");
        }
    }

    [Fact]
    public async Task Limits_are_enforced()
    {
        await using var s = app.Scope(app.UserId("carol"));
        var importer = s.Get<CsvImportService>();
        var rows = "Title\n" + string.Concat(Enumerable.Range(0, importer.Limits.MaxRows + 1).Select(i => $"Issue {i}\n"));
        Assert.Contains("more than", Assert.Throws<InvalidOperationException>(() => importer.Read(rows)).Message);
        var big = new MemoryStream(Encoding.UTF8.GetBytes("Title\n" + new string('x', (int)importer.Limits.MaxBytes)));
        Assert.Contains("larger than", (await Assert.ThrowsAsync<InvalidOperationException>(() => importer.ReadAsync(big))).Message);
        Assert.Throws<InvalidOperationException>(() => importer.Read("Title\n"));
    }

    [Fact]
    public async Task Export_endpoint_serves_the_list_or_gql_result()
    {
        var (source, _) = await SourceProjectAsync();
        var bob = await app.CookieClientAsync("bob");
        var r = await bob.GetAsync($"/p/{source.Key}/issues/export.csv?gql={Uri.EscapeDataString("type != Epic ORDER BY key")}&columns=key,title,assignee");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("text/csv", r.Content.Headers.ContentType?.MediaType);
        var bytes = await r.Content.ReadAsByteArrayAsync();
        Assert.Equal([0xEF, 0xBB, 0xBF], bytes.Take(3));
        var lines = Csv.Parse(Encoding.UTF8.GetString(bytes));
        Assert.Equal(["Key", "Title", "Assignee"], lines[0]);
        Assert.Equal([$"{source.Key}-2", "Story, with \"quotes\"", "bob"], lines[1]);
        Assert.Equal(3, lines.Count);

        var basic = Csv.Parse(await bob.GetStringAsync($"/p/{source.Key}/issues/export.csv?open="));
        Assert.Equal(4, basic.Count); // header + every issue, default columns
        Assert.Equal(IssueCsvExporter.DefaultColumns.Count, basic[0].Count);

        Assert.Equal(HttpStatusCode.NotFound, (await bob.GetAsync("/p/OPS/issues/export.csv")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await bob.GetAsync($"/issues/export.csv?gql={Uri.EscapeDataString("project = OPS")}")).StatusCode);
        var global = Csv.Parse(await bob.GetStringAsync($"/issues/export.csv?gql={Uri.EscapeDataString("ORDER BY key")}&columns=project"));
        Assert.DoesNotContain(global.Skip(1), row => row[0] == "OPS");
    }
}
