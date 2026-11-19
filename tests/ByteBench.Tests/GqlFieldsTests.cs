using ByteBench.Data;
using ByteBench.Services;
using ByteBench.Services.Automation;
using ByteBench.Services.Fields;
using ByteBench.Services.Query;
using ByteBench.Services.Workbench;
using ByteBench.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace ByteBench.Tests;

/// <summary>GQL fields from custom fields, components, versions, estimates and the Workbench charge code (<see cref="IssueFieldQueryFields"/>).</summary>
public class GqlFieldsTests(TestApp app) : IClassFixture<TestApp>
{
    private sealed record Data(Project P, Project Secret, int A, int B, int C, int X, int UiComponent);

    private static Data? data;
    private static readonly SemaphoreSlim gate = new(1, 1);

    /// <summary>
    /// Project P (alice Owner, bob Developer; 6-hour days, WBS 1.2.3) with custom fields, components, versions and estimates
    /// on issues A and B (C has nothing; B's WBS is overridden to 7.7), and a private project only alice can see with fields
    /// "severity" and "secret_code" on issue X.
    /// </summary>
    private async Task<Data> SetupAsync()
    {
        await gate.WaitAsync();
        try
        {
            if (data != null && app.Db().Projects.Any(p => p.Id == data.P.Id)) return data;
            var alice = app.UserId("alice");
            var p = await app.CreateProjectAsync("alice", ProjectVisibility.Private, ("bob", ProjectRole.Developer));
            var secret = await app.CreateProjectAsync("alice", ProjectVisibility.Private);
            await using (var db = app.Db())
                await db.Projects.Where(x => x.Id == p.Id).ExecuteUpdateAsync(x => x.SetProperty(y => y.HoursPerDay, 6));

            await using var s = app.Scope(alice);
            var fields = s.Get<CustomFieldService>();
            var cv = s.Get<ComponentVersionService>();
            var time = s.Get<EstimateService>();
            var workbench = s.Get<WorkbenchService>();
            var issues = s.Get<IssueService>();

            async Task<int> Field(int projectId, string key, CustomFieldType type, params string[] options) =>
                (await fields.SaveFieldAsync(new CustomField { ProjectId = projectId, Key = key, Name = key, Type = type, Options = [.. options] })).Id;
            var severity = await Field(p.Id, "severity", CustomFieldType.SingleSelect, "Minor", "Major", "Critical");
            var tags = await Field(p.Id, "tags", CustomFieldType.MultiSelect, "ui", "backend", "perf");
            var notes = await Field(p.Id, "notes", CustomFieldType.Text);
            var score = await Field(p.Id, "score", CustomFieldType.Number);
            var deadline = await Field(p.Id, "deadline", CustomFieldType.Date);
            var reviewer = await Field(p.Id, "reviewer", CustomFieldType.User);
            var approved = await Field(p.Id, "approved", CustomFieldType.Checkbox);
            var secretSeverity = await Field(secret.Id, "severity", CustomFieldType.SingleSelect, "Major", "Catastrophic");
            var secretCode = await Field(secret.Id, "secret_code", CustomFieldType.Text);

            var ui = await cv.SaveComponentAsync(new ProjectComponent { ProjectId = p.Id, Name = "UI" });
            var api = await cv.SaveComponentAsync(new ProjectComponent { ProjectId = p.Id, Name = "API" });
            var v1 = await cv.SaveVersionAsync(new ProjectVersion { ProjectId = p.Id, Name = "1.0" });
            var v2 = await cv.SaveVersionAsync(new ProjectVersion { ProjectId = p.Id, Name = "2.0" });

            var a = await issues.CreateIssueAsync(p.Id, new Issue { Title = "A" }, [], alice);
            var b = await issues.CreateIssueAsync(p.Id, new Issue { Title = "B" }, [], alice);
            var c = await issues.CreateIssueAsync(p.Id, new Issue { Title = "C" }, [], alice);
            var x = await issues.CreateIssueAsync(secret.Id, new Issue { Title = "X" }, [], alice);

            await fields.SetValuesAsync(a.Id, new Dictionary<int, object?>
            {
                [severity] = "Major", [tags] = new List<string> { "ui", "perf" }, [notes] = "Customer reported crash", [score] = 7.5,
                [deadline] = "2026-03-01", [reviewer] = "bob", [approved] = true,
            }, alice);
            await fields.SetValuesAsync(b.Id, new Dictionary<int, object?>
            {
                [severity] = "Minor", [tags] = "backend", [notes] = "internal", [score] = 2, [deadline] = "2026-05-10", [reviewer] = "alice", [approved] = false,
            }, alice);
            await fields.SetValuesAsync(x.Id, new Dictionary<int, object?> { [secretSeverity] = "Major", [secretCode] = "xyz" }, alice);

            await cv.SetComponentsAsync(a.Id, [ui.Id]);
            await cv.SetComponentsAsync(b.Id, [api.Id]);
            await cv.SetVersionsAsync(a.Id, VersionLinkKind.Fix, [v1.Id]);
            await cv.SetVersionsAsync(a.Id, VersionLinkKind.Affects, [v2.Id]);
            await cv.SetVersionsAsync(b.Id, VersionLinkKind.Fix, [v2.Id]);

            await time.SetEstimatesAsync(a.Id, true, 360, true, 30, alice); // 1d in a 6-hour project
            await workbench.SetProjectMappingAsync(p.Id, "1.2.3", "GB-1", alice);
            await workbench.SetIssueWbsAsync(b.Id, "7.7", alice);
            return data = new Data(p, secret, a.Id, b.Id, c.Id, x.Id, ui.Id);
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

    private async Task Expect(string gql, params Func<Data, int>[] expected)
    {
        var d = await SetupAsync();
        Assert.Equal(expected.Select(f => f(d)).ToHashSet(), await Ids(gql));
    }

    private async Task<GqlException> Error(string gql, string user = "alice", bool scoped = true)
    {
        var d = await SetupAsync();
        await using var s = app.Scope(app.UserId(user));
        return await Assert.ThrowsAsync<GqlException>(() => s.Get<GqlService>().SearchAsync(gql, scoped ? [d.P.Id] : null));
    }

    private async Task<List<string>> Suggest(string text, string user = "alice", bool scoped = true)
    {
        var d = await SetupAsync();
        await using var s = app.Scope(app.UserId(user));
        return (await s.Get<GqlService>().CompleteAsync(text, text.Length, scoped ? [d.P.Id] : null)).Items.Select(i => i.Insert).ToList();
    }

    [Fact]
    public async Task Components_by_name_or_id()
    {
        var d = await SetupAsync();
        await Expect("component = ui", x => x.A);
        await Expect($"component = {d.UiComponent}", x => x.A);
        await Expect("component in (API, UI)", x => x.A, x => x.B);
        await Expect("component != UI", x => x.B, x => x.C);
        await Expect("components not in (UI, API)", x => x.C);
        await Expect("component is EMPTY", x => x.C);
        await Expect("component is not EMPTY", x => x.A, x => x.B);
        Assert.Contains("No project searched has a component 'Nope'", (await Error("component = Nope")).Message);
        Assert.Contains("UI", await Suggest("component = "));
    }

    [Fact]
    public async Task Fix_and_affects_versions()
    {
        await Expect("fixVersion = \"1.0\"", x => x.A);
        await Expect("fixVersion = 2.0", x => x.B);
        await Expect("fixVersion in (1.0, 2.0)", x => x.A, x => x.B);
        await Expect("fixVersion not in (1.0)", x => x.B, x => x.C);
        await Expect("fixVersion is EMPTY", x => x.C);
        await Expect("affectsVersion = 2.0", x => x.A);
        await Expect("affectsVersion != 2.0", x => x.B, x => x.C);
        await Expect("affectsVersion is EMPTY", x => x.B, x => x.C);
        await Expect("milestone is EMPTY", x => x.A, x => x.B, x => x.C); // milestone no longer answers to fixVersion
        Assert.Contains("version '3.0'", (await Error("fixVersion = 3.0")).Message);
        var versions = await Suggest("fixVersion = ");
        Assert.Contains("1.0", versions);
        Assert.Contains("2.0", versions);
    }

    [Fact]
    public async Task Estimates_take_durations()
    {
        await Expect("originalEstimate = 1d", x => x.A); // the project's day is 6 hours
        await Expect("originalEstimate = 6h", x => x.A);
        await Expect("originalEstimate > 5", x => x.A); // a bare number is hours
        await Expect("originalEstimate < \"1d 1h\"", x => x.A);
        await Expect("originalEstimate is EMPTY", x => x.B, x => x.C);
        await Expect("remainingEstimate = 30m", x => x.A);
        await Expect("remainingEstimate < 30m");
        await Expect("remainingEstimate is not EMPTY", x => x.A);
        Assert.Contains("isn't a duration", (await Error("originalEstimate > soon")).Message);
        Assert.Contains("doesn't support '~'", (await Error("originalEstimate ~ 1h")).Message);
        Assert.Contains("timeSpent", (await Error("timeSpent > 1h")).Message); // time is tracked in Workbench now
        var d = await SetupAsync();
        await using var s = app.Scope(app.UserId("alice"));
        var ordered = (await s.Get<GqlService>().SearchAsync("ORDER BY originalEstimate DESC, key", [d.P.Id])).Issues.Select(i => i.Id).ToList();
        Assert.Equal(d.A, ordered[0]);
    }

    [Fact]
    public async Task Wbs_matches_the_issue_override_or_the_projects_code()
    {
        await Expect("wbs = \"1.2.3\"", x => x.A, x => x.C);
        await Expect("wbs = 7.7", x => x.B);
        await Expect("chargeCode in (\"1.2.3\", \"7.7\")", x => x.A, x => x.B, x => x.C);
        await Expect("wbs != \"1.2.3\"", x => x.B);
        await Expect("wbs is EMPTY");
        await Expect("wbs = 4.5.6");
        Assert.Contains("doesn't support '~'", (await Error("wbs ~ 1")).Message);
        var suggestions = await Suggest("wbs = ");
        Assert.Contains(suggestions, x => x.Contains("1.2.3"));
        Assert.Contains(suggestions, x => x.Contains("7.7"));
    }

    [Fact]
    public async Task Custom_fields_by_type()
    {
        // select and multi-select
        await Expect("cf[severity] = Major", x => x.A);
        await Expect("CF[Severity] = major", x => x.A);
        await Expect("\"cf[severity]\" = Major", x => x.A);
        await Expect("cf[severity] in (major, minor)", x => x.A, x => x.B);
        await Expect("cf[severity] != Major", x => x.B, x => x.C);
        await Expect("cf[severity] is EMPTY", x => x.C);
        await Expect("cf[tags] = perf", x => x.A);
        await Expect("cf[tags] in (backend, perf)", x => x.A, x => x.B);
        await Expect("cf[tags] not in (ui)", x => x.B, x => x.C);
        // text
        await Expect("cf[notes] ~ crash", x => x.A);
        await Expect("cf[notes] ~ \"customer CRASH\"", x => x.A);
        await Expect("cf[notes] = INTERNAL", x => x.B);
        await Expect("cf[notes] is EMPTY", x => x.C);
        // number and date
        await Expect("cf[score] > 5", x => x.A);
        await Expect("cf[score] <= 2", x => x.B);
        await Expect("cf[score] = 7.5", x => x.A);
        await Expect("cf[deadline] < 2026-04-01", x => x.A);
        await Expect("cf[deadline] = 2026-05-10", x => x.B);
        await Expect("cf[deadline] >= 2026-03-01", x => x.A, x => x.B);
        await Expect("cf[deadline] <= 2026-03-01", x => x.A);
        // user
        await Expect("cf[reviewer] = currentUser()", x => x.B);
        await Expect("cf[reviewer] = bob", x => x.A);
        await Expect("cf[reviewer] is EMPTY", x => x.C);
        // checkbox
        await Expect("cf[approved] = true", x => x.A);
        await Expect("cf[approved] = false", x => x.B, x => x.C);
        await Expect("cf[approved] != true", x => x.B, x => x.C);
        // combined with built-ins
        await Expect("cf[severity] = Major AND component = UI AND originalEstimate > 1h", x => x.A);

        var op = await Error("cf[severity] ~ Major");
        Assert.Contains("cf[severity] (Single select) doesn't support '~'", op.Message);
        Assert.Equal(13, op.Position);
        Assert.Contains("Use one of: Minor, Major, Critical", (await Error("cf[severity] = Huge")).Message);
        Assert.Contains("is not a number", (await Error("cf[score] > lots")).Message);
        Assert.Contains("is not a date", (await Error("cf[deadline] > someday")).Message);
        Assert.Contains("checkbox", (await Error("cf[approved] = maybe")).Message);
        Assert.Contains("doesn't support '<'", (await Error("cf[approved] < true")).Message);
        Assert.Contains("Name the custom field", (await Error("cf = Major")).Message);
        Assert.Contains("No project searched has a custom field 'nope'", (await Error("cf[nope] = 1")).Message);
        Assert.Contains("Unknown field", (await Error("cf[bad-key] = 1")).Message);
        Assert.Contains("can't be sorted", (await Error("ORDER BY cf[score]")).Message);
    }

    [Fact]
    public async Task Custom_field_autocomplete()
    {
        Assert.Contains("cf[", await Suggest("cf"));
        var keys = await Suggest("cf[");
        Assert.Contains("cf[severity]", keys);
        Assert.Contains("cf[approved]", keys);
        Assert.Equal(["cf[severity]"], await Suggest("project = X AND cf[sev"));
        Assert.Contains("~", await Suggest("cf[notes] "));
        Assert.Contains("Major", await Suggest("cf[severity] = "));
        Assert.Contains("Critical", await Suggest("cf[severity] in (Major, "));
        Assert.Contains("true", await Suggest("cf[approved] = "));
        Assert.Contains("currentUser()", await Suggest("cf[reviewer] = "));
    }

    [Fact]
    public async Task Custom_fields_of_private_projects_cannot_be_probed()
    {
        var d = await SetupAsync();
        // Positive control: alice can see the private project and its field.
        Assert.Equal([d.X], await Ids("cf[secret_code] = xyz", scoped: false));

        // bob is a member of P only: the private project's key reads as if it didn't exist, in every form.
        var probe = await Error("cf[secret_code] = xyz", "bob", scoped: false);
        var missing = await Error("cf[no_such_key] = xyz", "bob", scoped: false);
        Assert.Equal(probe.Message.Replace("secret_code", "KEY"), missing.Message.Replace("no_such_key", "KEY"));
        Assert.Contains("No project searched has a custom field 'secret_code'", (await Error("cf[secret_code] is EMPTY", "bob", scoped: false)).Message);
        Assert.Contains("No project searched has a custom field", (await Error("cf[secret_code] ~ x", "bob", scoped: false)).Message);
        // Even scoped to the private project explicitly.
        await using (var s = app.Scope(app.UserId("bob")))
            await Assert.ThrowsAsync<GqlException>(() => s.Get<GqlService>().SearchAsync("cf[secret_code] = xyz", [d.Secret.Id]));

        // A key both projects use only reaches bob's project, and options of the private field aren't valid or listed.
        var major = await Ids("cf[severity] = Major", "bob", scoped: false);
        Assert.Contains(d.A, major);
        Assert.DoesNotContain(d.X, major);
        var catastrophic = await Error("cf[severity] = Catastrophic", "bob", scoped: false);
        var listed = catastrophic.Message[catastrophic.Message.IndexOf("Use one of:", StringComparison.Ordinal)..];
        Assert.Contains("Critical", listed);
        Assert.DoesNotContain("Catastrophic", listed);
        Assert.DoesNotContain("Catastrophic", await Suggest("cf[severity] = ", "bob", scoped: false));
        Assert.DoesNotContain("cf[secret_code]", await Suggest("cf[", "bob", scoped: false));
        Assert.Contains("cf[severity]", await Suggest("cf[", "bob", scoped: false));

        // dan sees neither project (only seeded ones, which may have their own "severity").
        Assert.Contains("No project searched has a custom field 'secret_code'", (await Error("cf[secret_code] = xyz", "dan", scoped: false)).Message);
        await using (var s = app.Scope(app.UserId("dan")))
        {
            var gql = s.Get<GqlService>();
            var seen = (await gql.SearchAsync("cf[severity] is not EMPTY", take: 1000)).Issues.Select(i => i.Id).ToList();
            Assert.DoesNotContain(d.A, seen);
            Assert.DoesNotContain(d.X, seen);
            await Assert.ThrowsAsync<GqlException>(() => gql.MatchesAsync(d.X, "cf[secret_code] = xyz"));
        }
    }

    [Fact]
    public async Task New_fields_are_registered_without_clashing_with_built_ins()
    {
        await using var s = app.Scope(app.UserId("alice"));
        var registry = s.Get<GqlService>().Fields;
        Assert.Contains("version", registry.Find("fixVersion")!.Description);
        Assert.Equal("milestone", registry.Find("milestone")!.Name);
        Assert.Equal("cf[severity]", registry.Find("cf[severity]")!.Name);
        Assert.Null(registry.Find("status[severity]")); // only parameterized fields take brackets
        Assert.Null(registry.Find("cf[]"));
    }
}

/// <summary>The "Matches GQL query" automation condition (<see cref="GqlCondition"/>).</summary>
public class GqlAutomationConditionTests(TestApp app) : AutomationTestBase(app), IClassFixture<TestApp>
{
    private Task<Project> ProjectAsync() =>
        App.CreateProjectAsync("alice", ProjectVisibility.Private, ("bob", ProjectRole.Developer), ("carol", ProjectRole.Maintainer));

    private static RuleDefinition Raise(string op, string gql)
    {
        var def = Def(new ActionDef { Type = AutomationActionType.SetPriority, Priority = IssuePriority.Highest });
        def.Conditions = [new ConditionDef { Field = "gql", Operator = op, Value = gql }];
        return def;
    }

    private async Task CommentAsync(params int[] issueIds)
    {
        var bob = App.UserId("bob");
        foreach (var id in issueIds) await As("bob", s => s.Get<IssueService>().AddCommentAsync(id, bob, "ping"));
        await IdleAsync();
    }

    [Fact]
    public async Task Matches_and_does_not_match()
    {
        var project = await ProjectAsync();
        var rule = await RuleAsync(project.Id, AutomationTrigger.CommentAdded, Raise("matches", "type = Bug AND summary ~ crash"));
        var crash = await App.CreateIssueAsync(project.Id, "bob", "Crash on start", IssueType.Bug);
        var task = await App.CreateIssueAsync(project.Id, "bob", "Crash in docs", IssueType.Task);
        await CommentAsync(crash.Id, task.Id);
        Assert.Equal(IssuePriority.Highest, Load(crash.Id).Priority);
        Assert.Equal(IssuePriority.Medium, Load(task.Id).Priority);
        Assert.Contains(Runs(rule.Id), r => r.Status == AutomationRunStatus.NoActions && r.IssueId == task.Id);

        var project2 = await ProjectAsync();
        await RuleAsync(project2.Id, AutomationTrigger.CommentAdded, Raise("notMatches", "type = Bug"));
        var bug = await App.CreateIssueAsync(project2.Id, "bob", "A bug", IssueType.Bug);
        var story = await App.CreateIssueAsync(project2.Id, "bob", "A story", IssueType.Story);
        await CommentAsync(bug.Id, story.Id);
        Assert.Equal(IssuePriority.Medium, Load(bug.Id).Priority);
        Assert.Equal(IssuePriority.Highest, Load(story.Id).Priority);
    }

    [Fact]
    public async Task Runs_as_the_author_and_stays_in_the_rules_project()
    {
        var project = await ProjectAsync();
        var carol = App.UserId("carol");
        // As its author, currentUser() is the author.
        await RuleAsync(project.Id, AutomationTrigger.CommentAdded, Raise("matches", "assignee = currentUser()"), actor: "carol", runAs: AutomationRunAs.RuleAuthor);
        var mine = await As("alice", s => s.Get<IssueService>().CreateIssueAsync(project.Id, new Issue { Title = "Mine", AssigneeId = carol }, [], App.UserId("alice")));
        var other = await App.CreateIssueAsync(project.Id, "bob", "Unassigned");
        await CommentAsync(mine.Id, other.Id);
        Assert.Equal(IssuePriority.Highest, Load(mine.Id).Priority);
        Assert.Equal(IssuePriority.Medium, Load(other.Id).Priority);

        // As the bot (system access), names still resolve only in the rule's project: another project's key fails to resolve,
        // so the condition is false rather than matching through it.
        var elsewhere = await ProjectAsync();
        var project2 = await ProjectAsync();
        var rule = await RuleAsync(project2.Id, AutomationTrigger.CommentAdded, Raise("notMatches", $"project = {elsewhere.Key}"));
        var issue = await App.CreateIssueAsync(project2.Id, "bob", "Here");
        await CommentAsync(issue.Id);
        Assert.Equal(IssuePriority.Medium, Load(issue.Id).Priority);
        Assert.Single(Runs(rule.Id), r => r.Status == AutomationRunStatus.NoActions);
    }

    [Fact]
    public async Task Queries_are_checked_on_save_and_the_editor_shows_a_text_input()
    {
        var project = await ProjectAsync();
        var bad = await Assert.ThrowsAnyAsync<InvalidOperationException>(() => RuleAsync(project.Id, AutomationTrigger.CommentAdded, Raise("matches", "type = = Bug")));
        Assert.Contains("GQL condition", bad.Message);
        var unknown = await Assert.ThrowsAnyAsync<InvalidOperationException>(() => RuleAsync(project.Id, AutomationTrigger.CommentAdded, Raise("matches", "colour = red")));
        Assert.Contains("unknown field 'colour'", unknown.Message);
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => RuleAsync(project.Id, AutomationTrigger.CommentAdded, Raise("matches", " ")));
        Assert.NotNull(await RuleAsync(project.Id, AutomationTrigger.CommentAdded, Raise("matches", "cf[severity] = Major OR wbs = \"1.2.3\"")));

        var registry = App.Services.GetService(typeof(AutomationConditions)) as AutomationConditions;
        var condition = registry!.Find("gql");
        Assert.NotNull(condition);
        Assert.Equal("Matches GQL query", condition.Label);
        Assert.Equal(ConditionValueKind.Text, condition.ValueKind);
        Assert.All(condition.Operators, o => Assert.True(o.NeedsValue));
    }
}
