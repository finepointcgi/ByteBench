using System.Net;
using System.Text.Json;
using ByteBench.Data;
using ByteBench.Services;
using ByteBench.Services.Query;
using ByteBench.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace ByteBench.Tests;

public class GqlParserTests
{
    private static GqlNode Where(string q) => GqlParser.Parse(q).Where!;

    private static string Show(GqlNode n) => n switch
    {
        GqlAnd a => $"({Show(a.Left)} AND {Show(a.Right)})",
        GqlOr o => $"({Show(o.Left)} OR {Show(o.Right)})",
        GqlNot x => $"NOT {Show(x.Inner)}",
        GqlClause c => $"{c.Field} {c.Operator.Text()}{(c.Values.Count > 0 ? " " + string.Join("|", c.Values.Select(v => v.ToString())) : "")}",
        _ => "?",
    };

    [Theory]
    [InlineData("a = 1 OR b = 2 AND c = 3", "(a = 1 OR (b = 2 AND c = 3))")]
    [InlineData("a = 1 AND b = 2 OR c = 3", "((a = 1 AND b = 2) OR c = 3)")]
    [InlineData("(a = 1 OR b = 2) AND c = 3", "((a = 1 OR b = 2) AND c = 3)")]
    [InlineData("NOT a = 1 AND b = 2", "(NOT a = 1 AND b = 2)")]
    [InlineData("NOT (a = 1 OR b = 2)", "NOT (a = 1 OR b = 2)")]
    [InlineData("a=1 and b!=2 or not c~x", "((a = 1 AND b != 2) OR NOT c ~ x)")]
    [InlineData("status in (\"In Progress\", Blocked)", "status in \"In Progress\"|Blocked")]
    [InlineData("labels not in (a,b)", "labels not in a|b")]
    [InlineData("assignee is EMPTY", "assignee is")]
    [InlineData("assignee is not null", "assignee is not")]
    [InlineData("assignee = EMPTY", "assignee is")]
    [InlineData("due != empty", "due is not")]
    [InlineData("created >= -14d AND updated < startOfWeek(-1w)", "(created >= -14d AND updated < startOfWeek(-1w))")]
    [InlineData("\"story points\" <= 5", "story points <= 5")]
    public void Parses_with_the_right_precedence(string query, string expected) => Assert.Equal(expected, Show(Where(query)));

    [Fact]
    public void Quoting_escapes_and_functions()
    {
        var c = (GqlClause)Where("summary ~ \"say \\\"hi\\\" now\"");
        Assert.Equal("say \"hi\" now", c.Value!.Text);
        Assert.True(c.Value.Quoted);
        Assert.Equal("it's", ((GqlClause)Where("summary ~ 'it\\'s'")).Value!.Text);
        Assert.Equal("ORDER", ((GqlClause)Where("status = \"ORDER\"")).Value!.Text); // keywords need quotes
        var f = ((GqlClause)Where("assignee = currentUser()")).Value!;
        Assert.True(f.IsFunctionNamed("currentuser"));
        Assert.Empty(f.Args);
        Assert.Equal("WEB-12", ((GqlClause)Where("key = WEB-12")).Value!.Text);
        Assert.Equal("Done", GqlLexer.Quote("Done"));
        Assert.Equal("\"In Progress\"", GqlLexer.Quote("In Progress"));
        Assert.Equal("\"and\"", GqlLexer.Quote("and"));
        Assert.Equal("\"a\\\"b\"", GqlLexer.Quote("a\"b"));
    }

    [Fact]
    public void Order_by_without_conditions()
    {
        var q = GqlParser.Parse("ORDER BY priority DESC, updated");
        Assert.Null(q.Where);
        Assert.Equal([("priority", true), ("updated", false)], q.OrderBy.Select(s => (s.Field, s.Descending)));
        Assert.Null(GqlParser.Parse("  ").Where);
        Assert.Equal(2, GqlParser.Parse("project = WEB order by key asc, created desc").OrderBy.Count);
    }

    [Theory]
    [InlineData("status =", 8, "Expected a value")]
    [InlineData("status In Progress", 10, "Expected '('")]
    [InlineData("(status = Done", 14, "Expected ')'")]
    [InlineData("status = \"Done", 9, "no closing")]
    [InlineData("foo bar", 4, "Expected an operator")]
    [InlineData("status = Done extra", 14, "AND or OR")]
    [InlineData("status ! Done", 7, "'!'")]
    [InlineData("status = Done AND", 17, "Expected a condition")]
    [InlineData("AND status = Done", 0, "Expected a field")]
    [InlineData("status = AND", 9, "keyword")]
    [InlineData("status = Done)", 13, "without a matching")]
    [InlineData("labels in (a, b", 15, "Expected ',' or ')'")]
    [InlineData("assignee is maybe", 12, "Expected EMPTY")]
    [InlineData("status = Done ORDER priority", 20, "Expected BY")]
    [InlineData("()", 0, "Empty parentheses")]
    public void Errors_report_the_position(string query, int position, string message)
    {
        var ex = Assert.Throws<GqlException>(() => GqlParser.Parse(query));
        Assert.Equal(position, ex.Position);
        Assert.Contains(message, ex.Message);
        Assert.Contains($"column {position + 1}", ex.Message);
    }

    [Fact]
    public void Limits_guard_against_huge_queries()
    {
        Assert.Throws<GqlException>(() => GqlParser.Parse(new string('(', 100) + "a = 1" + new string(')', 100)));
        Assert.Throws<GqlException>(() => GqlParser.Parse(string.Join(" OR ", Enumerable.Range(0, 101).Select(i => $"a = {i}"))));
        Assert.Throws<GqlException>(() => GqlParser.Parse("summary ~ \"" + new string('x', 5000) + "\""));
    }
}

public class GqlSearchTests(TestApp app) : IClassFixture<TestApp>
{
    private sealed record Data(Project P, int Login, int Docs, int Story, int Epic, int Sub, int Sprint);

    private static Data? data;
    private static readonly SemaphoreSlim gate = new(1, 1);

    /// <summary>One private project (alice Owner, bob Developer) with five issues, shared by the tests in this class.</summary>
    private async Task<Data> SetupAsync()
    {
        await gate.WaitAsync();
        try
        {
            if (data != null && data.P.Id != 0 && app.Db().Projects.Any(p => p.Id == data.P.Id)) return data;
            var p = await app.CreateProjectAsync("alice", ProjectVisibility.Private, ("bob", ProjectRole.Developer), ("carol", ProjectRole.Viewer));
            var alice = app.UserId("alice");
            var bob = app.UserId("bob");
            await using var s = app.Scope(alice);
            var issues = s.Get<IssueService>();
            int[] Labels(params string[] names)
            {
                using var db = app.Db();
                return db.Labels.Where(l => l.ProjectId == p.Id && names.Contains(l.Name)).Select(l => l.Id).ToArray();
            }
            var epic = await issues.CreateIssueAsync(p.Id, new Issue { Title = "Public launch", Type = IssueType.Epic, Priority = IssuePriority.Highest }, [], alice);
            var login = await issues.CreateIssueAsync(p.Id, new Issue
            {
                Title = "Login crash on Safari", Description = "Steps: open the login page", Type = IssueType.Bug, Priority = IssuePriority.High,
                AssigneeId = bob, StoryPoints = 3, DueDate = new DateTime(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc), ParentId = epic.Id,
            }, Labels("bug"), alice);
            var docs = await issues.CreateIssueAsync(p.Id, new Issue { Title = "Write docs", Type = IssueType.Task, Priority = IssuePriority.Low, ParentId = epic.Id },
                Labels("documentation", "enhancement"), alice);
            var story = await issues.CreateIssueAsync(p.Id, new Issue
            {
                Title = "Profile page", Type = IssueType.Story, Priority = IssuePriority.Medium, Status = IssueStatus.Done, AssigneeId = alice, StoryPoints = 8,
            }, [], alice);
            var sub = await issues.CreateIssueAsync(p.Id, new Issue { Title = "Reproduce crash", Type = IssueType.Subtask, ParentId = login.Id, Priority = IssuePriority.Medium }, [], alice);
            await issues.AddCommentAsync(docs.Id, alice, "Needs screenshots of the settings screen");

            await using var db = app.Db();
            var sprint = new Sprint { ProjectId = p.Id, Name = "Sprint Alpha", State = SprintState.Active };
            db.Sprints.Add(sprint);
            db.Watches.Add(new Watch { UserId = app.UserId("dan"), IssueId = docs.Id });
            await db.SaveChangesAsync();
            await db.Issues.Where(i => i.Id == login.Id || i.Id == sub.Id).ExecuteUpdateAsync(x => x.SetProperty(i => i.SprintId, sprint.Id));
            await db.Issues.Where(i => i.Id == story.Id).ExecuteUpdateAsync(x => x
                .SetProperty(i => i.CreatedAt, new DateTime(2025, 1, 10, 15, 0, 0))
                .SetProperty(i => i.UpdatedAt, new DateTime(2025, 1, 12, 9, 0, 0)));
            return data = new Data(p, login.Id, docs.Id, story.Id, epic.Id, sub.Id, sprint.Id);
        }
        finally { gate.Release(); }
    }

    private async Task<HashSet<int>> Ids(string gql, string user = "alice", bool scoped = true)
    {
        var d = await SetupAsync();
        await using var s = app.Scope(app.UserId(user));
        var result = await s.Get<GqlService>().SearchAsync(gql, scoped ? [d.P.Id] : null, take: 1000);
        return result.Issues.Select(i => i.Id).ToHashSet();
    }

    private async Task<GqlException> Error(string gql, string user = "alice")
    {
        var d = await SetupAsync();
        await using var s = app.Scope(app.UserId(user));
        return await Assert.ThrowsAsync<GqlException>(() => s.Get<GqlService>().SearchAsync(gql, [d.P.Id]));
    }

    private async Task Expect(string gql, params Func<Data, int>[] expected)
    {
        var d = await SetupAsync();
        Assert.Equal(expected.Select(f => f(d)).ToHashSet(), await Ids(gql));
    }

    [Fact]
    public async Task Text_fields()
    {
        await Expect("summary ~ crash", d => d.Login, d => d.Sub);
        await Expect("title ~ \"SAFARI login\"", d => d.Login); // every word, case-insensitive
        await Expect("summary !~ crash AND type != Epic", d => d.Docs, d => d.Story);
        await Expect("summary = \"write DOCS\"", d => d.Docs);
        await Expect("description ~ \"login page\"", d => d.Login);
        await Expect("description is not EMPTY", d => d.Login);
        await Expect("text ~ screenshots", d => d.Docs); // comments count for text
        await Expect("comment ~ \"settings screen\"", d => d.Docs);
        await Expect("summary ~ \"100%\"");
        await Expect("summary ~ \"_\""); // LIKE wildcards are escaped
    }

    [Fact]
    public async Task Choice_fields()
    {
        await Expect("type = Bug", d => d.Login);
        await Expect("type in (bug, \"sub-task\")", d => d.Login, d => d.Sub);
        await Expect("type not in (Epic, Subtask, Bug)", d => d.Docs, d => d.Story);
        await Expect("priority >= High", d => d.Login, d => d.Epic);
        await Expect("priority < Medium", d => d.Docs);
        await Expect("status = Done", d => d.Story);
        await Expect("status in (\"To Do\", Done) AND type = Story", d => d.Story);
        await Expect("statusCategory != \"To Do\"", d => d.Story);
        await Expect("category = done", d => d.Story);
        var e = await Error("type = Banana");
        Assert.Contains("is not a type", e.Message);
        Assert.Equal(7, e.Position);
        Assert.Contains("No project searched has a status named 'Nope'", (await Error("status = Nope")).Message);
    }

    [Fact]
    public async Task People()
    {
        await Expect("assignee = bob", d => d.Login);
        await Expect("assignee = currentUser()", d => d.Story);
        await Expect("assignee in (bob@example.com, \"Alice Chen\")", d => d.Login, d => d.Story);
        await Expect("assignee is EMPTY", d => d.Epic, d => d.Docs, d => d.Sub);
        await Expect("assignee != bob AND type != Epic", d => d.Docs, d => d.Story, d => d.Sub); // != includes unassigned
        await Expect("reporter = alice AND type = Bug", d => d.Login);
        await Expect("watcher = dan", d => d.Docs);
        var e = await Error("assignee = nobody");
        Assert.Contains("There is no user 'nobody'", e.Message);
        Assert.Equal(11, e.Position);
    }

    [Fact]
    public async Task Planning_fields()
    {
        await Expect("labels = bug", d => d.Login);
        await Expect("labels in (documentation, nothing)", d => d.Docs);
        await Expect("labels is EMPTY AND type = Story", d => d.Story);
        await Expect("labels != bug AND type = Task", d => d.Docs);
        await Expect("sprint = openSprints()", d => d.Login, d => d.Sub);
        await Expect("sprint = \"sprint alpha\"", d => d.Login, d => d.Sub);
        await Expect("sprint is EMPTY AND type = Task", d => d.Docs);
        await Expect("milestone is EMPTY AND type = Bug", d => d.Login);
        await Expect("storyPoints > 3", d => d.Story);
        await Expect("\"story points\" in (3, 8)", d => d.Login, d => d.Story);
        await Expect("points is EMPTY AND priority = Low", d => d.Docs);
        var d = await SetupAsync();
        await Expect($"parent = {d.P.Key}-{Num(d.Login)}", x => x.Sub);
        await Expect($"epic = {d.P.Key}-{Num(d.Epic)}", x => x.Login, x => x.Docs, x => x.Sub); // children and their sub-tasks
        await Expect("epic is EMPTY", x => x.Epic, x => x.Story);
        await Expect("parent is EMPTY AND type = Story", x => x.Story);
        await Expect($"key = {d.P.Key}-{Num(d.Docs)}", x => x.Docs);
        await Expect($"key in ({d.P.Key}-{Num(d.Docs)}, {d.P.Key}-{Num(d.Story)})", x => x.Docs, x => x.Story);
        await Expect($"key > {d.P.Key}-{Num(d.Story)}", x => x.Sub);
        await Expect($"project = {d.P.Key.ToLowerInvariant()} AND type = Epic", x => x.Epic);
        Assert.Contains("no issue", (await Error($"parent = {d.P.Key}-999")).Message);
        Assert.Contains("Unknown sprint function", (await Error("sprint = later()")).Message);
    }

    private int Num(int issueId)
    {
        using var db = app.Db();
        return db.Issues.Where(i => i.Id == issueId).Select(i => i.Number).First();
    }

    [Fact]
    public async Task Dates()
    {
        await Expect("created = 2025-01-10", d => d.Story); // a date matches the whole day
        await Expect("created <= 2025-01-10", d => d.Story);
        await Expect("created < 2025-01-10");
        await Expect("created > 2025-01-10 AND type = Story");
        await Expect("created < \"2025-01-10 16:00\"", d => d.Story);
        await Expect("created < \"2025-01-10 14:00\"");
        await Expect("updated < -30d", d => d.Story);
        await Expect("created >= -1d AND type = Story");
        await Expect("created >= startOfYear() AND type = Bug", d => d.Login);
        await Expect("due = 2026-01-15", d => d.Login);
        await Expect("due < endOfDay() AND due > \"2026/01/01\"", d => d.Login);
        await Expect("due is EMPTY AND type = Bug");
        await Expect("resolved is not EMPTY", d => d.Story);
        await Expect("resolved >= -1h", d => d.Story);
        Assert.Contains("is not a date", (await Error("created > yesterday")).Message);
        Assert.Contains("Unknown date function", (await Error("created > someday()")).Message);
    }

    [Fact]
    public async Task Ordering_and_operator_checks()
    {
        var d = await SetupAsync();
        await using var s = app.Scope(app.UserId("alice"));
        var gql = s.Get<GqlService>();
        var byPriority = (await gql.SearchAsync("ORDER BY priority DESC, key ASC", [d.P.Id])).Issues.Select(i => i.Id).ToList();
        Assert.Equal([d.Epic, d.Login, d.Story, d.Sub, d.Docs], byPriority);
        var byKey = (await gql.SearchAsync("type != Epic ORDER BY key DESC", [d.P.Id])).Issues.Select(i => i.Id).ToList();
        Assert.Equal([d.Sub, d.Story, d.Docs, d.Login], byKey);
        var page = await gql.SearchAsync("ORDER BY key", [d.P.Id], skip: 1, take: 2);
        Assert.Equal(5, page.Total);
        Assert.Equal([d.Login, d.Docs], page.Issues.Select(i => i.Id));

        var e = await Error("summary < 3");
        Assert.Contains("summary doesn't support '<'", e.Message);
        Assert.Equal(8, e.Position);
        var unknown = await Error("statsu = Done");
        Assert.Contains("Did you mean status?", unknown.Message);
        Assert.Contains("can't be sorted", (await Error("ORDER BY text")).Message);
        Assert.Null(await gql.ValidateAsync("project = OPS"));
        Assert.NotNull(await gql.ValidateAsync("project = NOPE"));
    }

    [Fact]
    public async Task Queries_never_reach_projects_the_user_cannot_view()
    {
        var d = await SetupAsync();
        // dan has no role in the test project; bob has none in OPS.
        await using (var s = app.Scope(app.UserId("dan")))
        {
            var gql = s.Get<GqlService>();
            var e = await Assert.ThrowsAsync<GqlException>(() => gql.SearchAsync($"project = {d.P.Key}"));
            Assert.Contains($"no project '{d.P.Key}' that you can see", e.Message);
            var all = await gql.SearchAsync("summary ~ crash OR summary ~ docs OR type = Epic", take: 1000);
            Assert.DoesNotContain(all.Issues, i => i.ProjectId == d.P.Id);
            Assert.DoesNotContain(all.Issues, i => i.Id == d.Login);
            Assert.Equal(0, (await gql.SearchAsync($"key = {d.P.Key}-1")).Total);
            // Even scoped to the project explicitly, nothing comes back.
            Assert.Equal(0, (await gql.SearchAsync("ORDER BY key", [d.P.Id])).Total);
            Assert.False(await gql.MatchesAsync(d.Login, "type = Bug"));
        }
        await using (var s = app.Scope(app.UserId("bob")))
        {
            var gql = s.Get<GqlService>();
            await Assert.ThrowsAsync<GqlException>(() => gql.SearchAsync("project = OPS"));
            var everything = await gql.SearchAsync("ORDER BY key", take: 1000);
            Assert.DoesNotContain(everything.Issues, i => i.ProjectId == app.ProjectId("OPS"));
            Assert.Contains(everything.Issues, i => i.ProjectId == d.P.Id);
            Assert.True(await gql.MatchesAsync(d.Login, "type = Bug AND assignee = currentUser()"));
            Assert.False(await gql.MatchesAsync(d.Login, "type = Task"));
        }
    }

    [Fact]
    public async Task Basic_filters_convert_to_equivalent_gql()
    {
        var d = await SetupAsync();
        await using var s = app.Scope(app.UserId("alice"));
        var gql = s.Get<GqlService>();
        var issues = s.Get<IssueService>();
        IssueFilter[] filters =
        [
            new(),
            new() { Open = null, Sort = "priority" },
            new() { Open = false },
            new() { Type = IssueType.Bug, AssigneeId = app.UserId("bob") },
            new() { AssigneeId = 0, Priority = IssuePriority.Medium },
            new() { Search = "crash" },
            new() { EpicId = d.Epic, HideSubtasks = true },
            new() { SprintId = d.Sprint, Sort = "number" },
            new() { SprintId = 0, Open = null },
            new() { Status = IssueStatus.Done },
        ];
        foreach (var f in filters)
        {
            var text = await gql.FromFilterAsync(d.P.Id, f);
            var expected = (await issues.GetIssuesAsync(d.P.Id, f)).Select(i => i.Id).ToList();
            var actual = (await gql.SearchAsync(text, [d.P.Id])).Issues.Select(i => i.Id).ToList();
            if (f.Sort is "number" or "priority") Assert.Equal(expected, actual);
            else Assert.Equal(expected.ToHashSet(), actual.ToHashSet());
        }
        Assert.StartsWith($"project = {d.P.Key} AND statusCategory != Done", await gql.FromFilterAsync(d.P.Id, new IssueFilter()));
    }

    [Fact]
    public async Task Autocomplete_suggests_fields_operators_and_values()
    {
        var d = await SetupAsync();
        await using var s = app.Scope(app.UserId("alice"));
        var gql = s.Get<GqlService>();
        async Task<List<string>> Labels(string text) => (await gql.CompleteAsync(text, text.Length, [d.P.Id])).Items.Select(i => i.Insert).ToList();

        Assert.Contains("status", await Labels(""));
        var sta = await gql.CompleteAsync("project = X AND sta", 19, [d.P.Id]);
        Assert.Equal(16, sta.Start);
        Assert.Equal(["status", "statusCategory"], sta.Items.Select(i => i.Insert));
        Assert.Contains("in (", await Labels("status "));
        Assert.DoesNotContain("~", await Labels("status "));
        Assert.Contains("\"In Progress\"", await Labels("status = "));
        Assert.Contains("\"In Progress\"", await Labels("status = \"in"));
        Assert.Contains("currentUser()", await Labels("assignee in (bob, "));
        Assert.Contains("bob", await Labels("assignee = b"));
        Assert.Contains("openSprints()", await Labels("sprint = "));
        Assert.Contains("bug", await Labels("labels = "));
        Assert.Contains("AND", await Labels("status = Done "));
        Assert.Contains("AND", await Labels("sprint in (openSprints()) "));
        Assert.Contains("BY", await Labels("type = Bug ORDER "));
        Assert.Contains("priority", await Labels("type = Bug ORDER BY pri"));
        Assert.Contains("DESC", await Labels("ORDER BY priority "));
        Assert.Contains("EMPTY", await Labels("assignee is "));
        Assert.Contains("-7d", await Labels("created > "));
        Assert.Empty(await Labels("status = \"Done\""));
    }

    [Fact]
    public async Task Api_search_paginates_and_reports_errors()
    {
        var d = await SetupAsync();
        var client = await app.ApiClientAsync("bob", TokenScopes.ReadApi);
        var r = await client.GetAsync($"/api/v1/search/issues?gql={Uri.EscapeDataString($"project = {d.P.Key} ORDER BY key")}&perPage=2&page=2");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("5", r.Headers.GetValues("X-Total-Count").Single());
        Assert.Contains("rel=\"next\"", r.Headers.GetValues("Link").Single());
        var body = JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(2, body.GetArrayLength());
        Assert.Equal($"{d.P.Key}-3", body[0].GetProperty("key").GetString());
        Assert.Equal("alice", body[0].GetProperty("reporter").GetProperty("username").GetString());

        var bad = await client.GetAsync($"/api/v1/search/issues?gql={Uri.EscapeDataString("status = (")}");
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        var problem = JsonDocument.Parse(await bad.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(9, problem.GetProperty("position").GetInt32());

        var hidden = await client.GetAsync($"/api/v1/search/issues?gql={Uri.EscapeDataString("project = OPS")}");
        Assert.Equal(HttpStatusCode.BadRequest, hidden.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/v1/search/issues")).StatusCode);
    }

    [Fact]
    public async Task Gql_round_trips_through_the_list_url_and_saved_filters()
    {
        var d = await SetupAsync();
        var filter = new IssueFilter { Gql = "status in (\"In Progress\", Done) & labels = bug" };
        var query = IssueFilterQuery.ToQueryString(filter);
        Assert.StartsWith("gql=", query);
        Assert.Equal(filter.Gql, IssueFilterQuery.Parse(query).Gql);
        await using var s = app.Scope(app.UserId("alice"));
        var saved = await s.Get<SavedFilterService>().CreateAsync(d.P.Id, "GQL bugs", query, false);
        Assert.Equal(filter.Gql, IssueFilterQuery.Parse(saved.Query).Gql);
    }
}
