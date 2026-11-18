using System.Net;
using System.Text;
using System.Text.Json;
using GitBench.Data;
using GitBench.Services;
using GitBench.Services.Fields;
using GitBench.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace GitBench.Tests;

/// <summary>Custom fields, components and versions, issue templates and estimates.</summary>
public class FieldsTests(TestApp app) : IClassFixture<TestApp>
{
    // ---------- Durations ----------

    [Theory]
    [InlineData("2h 30m", 8, 150)]
    [InlineData("1d", 8, 480)]
    [InlineData("1d", 6, 360)]
    [InlineData("1w", 8, 2400)]
    [InlineData("1w 2d 3h 4m", 8, 2400 + 960 + 180 + 4)]
    [InlineData("1.5h", 8, 90)]
    [InlineData("1,5h", 8, 90)]
    [InlineData("45m", 8, 45)]
    [InlineData("2", 8, 120)] // a bare number is hours
    [InlineData(" 3H ", 8, 180)]
    [InlineData("2h30m", 8, 150)]
    public void Durations_parse(string text, int hoursPerDay, int minutes) => Assert.Equal(minutes, Duration.Parse(text, hoursPerDay));

    [Theory]
    [InlineData("abc")]
    [InlineData("2x")]
    [InlineData("h")]
    [InlineData("-1h")]
    [InlineData("2h junk")]
    [InlineData("99999h")]
    public void Bad_durations_are_refused(string text)
    {
        Assert.Throws<FormatException>(() => Duration.Parse(text));
        Assert.False(Duration.TryParse(text, 8, out _));
    }

    [Fact]
    public void Durations_format_with_the_projects_day()
    {
        Assert.Null(Duration.Parse("  "));
        Assert.Equal("0m", Duration.Format(0));
        Assert.Equal("2h 30m", Duration.Format(150));
        Assert.Equal("1d 1h", Duration.Format(540));
        Assert.Equal("1d 3h", Duration.Format(540, 6));
        Assert.Equal("5d", Duration.Format(2400));
        Assert.Equal("7.5", Duration.Hours(450));
        foreach (var m in new[] { 1, 59, 61, 480, 1234, 9999 }) Assert.Equal(m, Duration.Parse(Duration.Format(m)));
    }

    // ---------- Custom field rules ----------

    private static CustomField Field(CustomFieldType type, params string[] options) =>
        new() { Id = 1, Name = "F", Key = "f", Type = type, Options = [.. options] };

    [Fact]
    public void Values_parse_by_type()
    {
        Assert.Equal("hi", CustomFieldRules.Parse(Field(CustomFieldType.Text), "  hi ").Text);
        Assert.True(CustomFieldRules.Parse(Field(CustomFieldType.Text), "   ").IsEmpty);
        Assert.Throws<InvalidOperationException>(() => CustomFieldRules.Parse(Field(CustomFieldType.Text), "two\nlines"));
        Assert.Throws<InvalidOperationException>(() => CustomFieldRules.Parse(Field(CustomFieldType.Text), new string('x', 256)));
        Assert.Equal("two\nlines", CustomFieldRules.Parse(Field(CustomFieldType.LongText), "two\nlines").Text);

        Assert.Equal(3.5, CustomFieldRules.Parse(Field(CustomFieldType.Number), "3.5").Number);
        Assert.Equal(7, CustomFieldRules.Parse(Field(CustomFieldType.Number), 7).Number);
        Assert.Throws<InvalidOperationException>(() => CustomFieldRules.Parse(Field(CustomFieldType.Number), "seven"));
        Assert.Throws<InvalidOperationException>(() => CustomFieldRules.Parse(Field(CustomFieldType.Number), double.NaN));

        Assert.Equal(new DateTime(2026, 3, 4), CustomFieldRules.Parse(Field(CustomFieldType.Date), "2026-03-04").Date);
        Assert.Equal(new DateTime(2026, 3, 4), CustomFieldRules.Parse(Field(CustomFieldType.Date), new DateTime(2026, 3, 4, 15, 0, 0)).Date);
        Assert.Throws<InvalidOperationException>(() => CustomFieldRules.Parse(Field(CustomFieldType.Date), "03/04/2026"));

        var select = Field(CustomFieldType.SingleSelect, "Low", "High");
        Assert.Equal("High", CustomFieldRules.Parse(select, "high").Text); // canonical spelling
        Assert.Throws<InvalidOperationException>(() => CustomFieldRules.Parse(select, "Medium"));

        var multi = Field(CustomFieldType.MultiSelect, "A", "B", "C");
        Assert.Equal(["A", "C"], CustomFieldRules.Parse(multi, new List<string> { "c", "a", "C" }).Options); // field order, no duplicates
        Assert.Equal(["A", "B"], CustomFieldRules.Parse(multi, "B, A").Options);
        Assert.True(CustomFieldRules.Parse(multi, new List<string>()).IsEmpty);
        Assert.Throws<InvalidOperationException>(() => CustomFieldRules.Parse(multi, new List<string> { "D" }));

        Assert.Equal("bob", CustomFieldRules.Parse(Field(CustomFieldType.User), "@bob").UserName);
        Assert.Equal(4, CustomFieldRules.Parse(Field(CustomFieldType.User), 4).UserId);

        Assert.True(CustomFieldRules.Parse(Field(CustomFieldType.Checkbox), "yes").Bool);
        Assert.False(CustomFieldRules.Parse(Field(CustomFieldType.Checkbox), false).Bool);
        Assert.Throws<InvalidOperationException>(() => CustomFieldRules.Parse(Field(CustomFieldType.Checkbox), "maybe"));

        Assert.Equal("https://example.com/x", CustomFieldRules.Parse(Field(CustomFieldType.Url), "https://example.com/x").Text);
        Assert.Throws<InvalidOperationException>(() => CustomFieldRules.Parse(Field(CustomFieldType.Url), "javascript:alert(1)"));
        Assert.Throws<InvalidOperationException>(() => CustomFieldRules.Parse(Field(CustomFieldType.Url), "not a url"));

        // JSON input (the API): numbers, booleans, arrays and null.
        var json = JsonDocument.Parse("""{"n":5,"b":true,"a":["B"],"z":null}""").RootElement;
        Assert.Equal(5, CustomFieldRules.Parse(Field(CustomFieldType.Number), json.GetProperty("n")).Number);
        Assert.True(CustomFieldRules.Parse(Field(CustomFieldType.Checkbox), json.GetProperty("b")).Bool);
        Assert.Equal(["B"], CustomFieldRules.Parse(multi, json.GetProperty("a")).Options);
        Assert.True(CustomFieldRules.Parse(Field(CustomFieldType.Text), json.GetProperty("z")).IsEmpty);
        Assert.Throws<InvalidOperationException>(() => CustomFieldRules.Parse(Field(CustomFieldType.Text), json.GetProperty("n")));
    }

    [Fact]
    public void Values_round_trip_through_rows()
    {
        var multi = Field(CustomFieldType.MultiSelect, "A", "B", "C");
        var v = CustomFieldRules.Parse(multi, "C,A");
        var rows = CustomFieldRules.ToRows(multi, 9, v);
        Assert.Equal(2, rows.Count);
        Assert.True(v.Same(CustomFieldRules.FromRows(multi, rows)));
        foreach (var (f, input) in new (CustomField, object)[]
                 {
                     (Field(CustomFieldType.Number), 2.25), (Field(CustomFieldType.Date), "2026-01-31"), (Field(CustomFieldType.Checkbox), true),
                     (Field(CustomFieldType.User), 3), (Field(CustomFieldType.Url), "http://x.test"),
                 })
        {
            var value = CustomFieldRules.Parse(f, input);
            Assert.True(value.Same(CustomFieldRules.FromRows(f, CustomFieldRules.ToRows(f, 1, value))), f.Type.ToString());
        }
        Assert.Empty(CustomFieldRules.ToRows(multi, 9, FieldValue.Empty));
    }

    [Fact]
    public void Definitions_are_validated_and_normalized()
    {
        var f = new CustomField { Name = " Customer impact ", Type = CustomFieldType.SingleSelect, Options = ["Low", " High ", ""], DefaultValue = "high" };
        CustomFieldRules.ValidateDefinition(f);
        Assert.Equal("customer_impact", f.Key);
        Assert.Equal(["Low", "High"], f.Options);
        Assert.Equal("High", f.DefaultValue);
        Assert.Equal("f_2nd_try", CustomFieldRules.KeyFromName("2nd try!"));

        Assert.Throws<InvalidOperationException>(() => CustomFieldRules.ValidateDefinition(new CustomField { Name = "S", Type = CustomFieldType.SingleSelect }));
        Assert.Throws<InvalidOperationException>(() => CustomFieldRules.ValidateDefinition(new CustomField { Name = "S", Type = CustomFieldType.MultiSelect, Options = ["a", "A"] }));
        Assert.Throws<InvalidOperationException>(() => CustomFieldRules.ValidateDefinition(new CustomField { Name = "S", Type = CustomFieldType.MultiSelect, Options = ["a,b"] }));
        Assert.Throws<InvalidOperationException>(() => CustomFieldRules.ValidateDefinition(new CustomField { Name = "N", Type = CustomFieldType.Number, DefaultValue = "x" }));
        Assert.Throws<InvalidOperationException>(() => CustomFieldRules.ValidateDefinition(new CustomField { Name = "N", Key = "Bad Key", Type = CustomFieldType.Text }));
        Assert.Throws<InvalidOperationException>(() => CustomFieldRules.ValidateDefinition(new CustomField { Name = "", Type = CustomFieldType.Text }));

        var all = new CustomField { Name = "T", Type = CustomFieldType.Text, IssueTypes = [.. Enum.GetValues<IssueType>()] };
        CustomFieldRules.ValidateDefinition(all);
        Assert.Empty(all.IssueTypes); // every type = no restriction
    }

    // ---------- Services ----------

    private async Task<(Project Project, CustomField Severity, CustomField Reviewer)> ProjectWithFieldsAsync()
    {
        var project = await app.CreateProjectAsync("carol", ProjectVisibility.Private, ("bob", ProjectRole.Developer), ("dan", ProjectRole.Viewer));
        await using var carol = app.Scope(app.UserId("carol"));
        var fields = carol.Get<CustomFieldService>();
        var severity = await fields.SaveFieldAsync(new CustomField
        {
            ProjectId = project.Id, Name = "Severity", Type = CustomFieldType.SingleSelect, Options = ["Minor", "Major"], Required = true,
            DefaultValue = "Minor", IssueTypes = [IssueType.Bug],
        });
        var reviewer = await fields.SaveFieldAsync(new CustomField { ProjectId = project.Id, Name = "Reviewer", Type = CustomFieldType.User });
        return (project, severity, reviewer);
    }

    [Fact]
    public async Task Only_maintainers_define_fields_and_keys_are_unique()
    {
        var (project, severity, _) = await ProjectWithFieldsAsync();
        Assert.Equal("severity", severity.Key);
        await using var bob = app.Scope(app.UserId("bob"));
        await Assert.ThrowsAsync<ForbiddenException>(() => bob.Get<CustomFieldService>().SaveFieldAsync(new CustomField { ProjectId = project.Id, Name = "X", Type = CustomFieldType.Text }));
        await using var carol = app.Scope(app.UserId("carol"));
        var ex = await Assert.ThrowsAnyAsync<InvalidOperationException>(() => carol.Get<CustomFieldService>()
            .SaveFieldAsync(new CustomField { ProjectId = project.Id, Name = "Other", Key = "severity", Type = CustomFieldType.Text }));
        Assert.Contains("key", ex.Message);
        // A user default must be someone who can see the project.
        var outsider = await app.CreateUserAsync("fields_outsider");
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => carol.Get<CustomFieldService>()
            .SaveFieldAsync(new CustomField { ProjectId = project.Id, Name = "QA", Type = CustomFieldType.User, DefaultValue = outsider.UserName }));
    }

    [Fact]
    public async Task New_issues_get_defaults_and_required_fields_are_enforced()
    {
        var (project, severity, reviewer) = await ProjectWithFieldsAsync();
        var bobId = app.UserId("bob");
        await using var bob = app.Scope(bobId);
        var issues = bob.Get<IssueService>();
        var fields = bob.Get<CustomFieldService>();

        // Default applied to a bug; not to a task (the field doesn't apply).
        var bug = await issues.CreateIssueAsync(project.Id, new Issue { Title = "Crash", Type = IssueType.Bug }, [], bobId);
        Assert.Equal("Minor", (await fields.GetIssueValuesAsync(bug.Id)).Single(v => v.Field.Id == severity.Id).Value.Text);
        var task = await issues.CreateIssueAsync(project.Id, new Issue { Title = "Chore" }, [], bobId);
        Assert.DoesNotContain(await fields.GetIssueValuesAsync(task.Id), v => v.Field.Id == severity.Id);

        // Explicit values, and refusals: empty required value, a field that doesn't apply, a bad option, an outsider.
        var draft = new IssueFieldDraft();
        draft.Values[severity.Id] = "major";
        draft.Values[reviewer.Id] = "carol";
        var bug2 = await issues.CreateIssueAsync(project.Id, new Issue { Title = "Bug 2", Type = IssueType.Bug, FieldDraft = draft }, [], bobId);
        var values = await fields.GetIssueValuesAsync(bug2.Id);
        Assert.Equal("Major", values.Single(v => v.Field.Id == severity.Id).Value.Text);
        Assert.Equal(app.UserId("carol"), values.Single(v => v.Field.Id == reviewer.Id).Value.UserId);

        async Task Refused(IssueType type, int fieldId, object? value)
        {
            var d = new IssueFieldDraft();
            d.Values[fieldId] = value;
            await Assert.ThrowsAnyAsync<InvalidOperationException>(() => issues.CreateIssueAsync(project.Id, new Issue { Title = "x", Type = type, FieldDraft = d }, [], bobId));
        }
        await Refused(IssueType.Bug, severity.Id, "");
        await Refused(IssueType.Task, severity.Id, "Major");
        await Refused(IssueType.Bug, severity.Id, "Huge");
        await Refused(IssueType.Task, reviewer.Id, (await app.CreateUserAsync("fields_stranger")).UserName);

        // Editing: activity is recorded; clearing a required field is refused; viewers can't edit.
        await fields.SetValueAsync(bug.Id, severity.Id, "Major");
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => fields.SetValueAsync(bug.Id, severity.Id, null));
        await using var db = app.Db();
        Assert.Contains(db.Activities.Where(a => a.IssueId == bug.Id).Select(a => a.Text), t => t == "changed **Severity** to **Major**");
        await using var dan = app.Scope(app.UserId("dan"));
        await Assert.ThrowsAsync<ForbiddenException>(() => dan.Get<CustomFieldService>().SetValueAsync(bug.Id, severity.Id, "Minor"));
    }

    [Fact]
    public async Task Component_version_and_custom_field_filters_work_and_round_trip()
    {
        var (project, severity, _) = await ProjectWithFieldsAsync();
        var bobId = app.UserId("bob");
        await using var carol = app.Scope(app.UserId("carol"));
        var cv = carol.Get<ComponentVersionService>();
        var ui = await cv.SaveComponentAsync(new ProjectComponent { ProjectId = project.Id, Name = "UI", DefaultAssigneeId = app.UserId("carol") });
        var api = await cv.SaveComponentAsync(new ProjectComponent { ProjectId = project.Id, Name = "API" });
        var v1 = await cv.SaveVersionAsync(new ProjectVersion { ProjectId = project.Id, Name = "1.0" });
        var v2 = await cv.SaveVersionAsync(new ProjectVersion { ProjectId = project.Id, Name = "2.0" });
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => cv.SaveVersionAsync(new ProjectVersion { ProjectId = project.Id, Name = "1.0" }));

        await using var bob = app.Scope(bobId);
        var issues = bob.Get<IssueService>();
        var draft = new IssueFieldDraft { ComponentIds = [ui.Id], FixVersionIds = [v1.Id], AffectsVersionIds = [v2.Id] };
        draft.Values[severity.Id] = "Major";
        var a = await issues.CreateIssueAsync(project.Id, new Issue { Title = "A", Type = IssueType.Bug, FieldDraft = draft }, [], bobId);
        Assert.Equal(app.UserId("carol"), a.AssigneeId); // the component's default assignee
        var b = await issues.CreateIssueAsync(project.Id, new Issue { Title = "B" }, [], bobId);
        await bob.Get<ComponentVersionService>().SetComponentsAsync(b.Id, [api.Id]);
        await bob.Get<ComponentVersionService>().SetVersionsAsync(b.Id, VersionLinkKind.Fix, [v2.Id]);
        var c = await issues.CreateIssueAsync(project.Id, new Issue { Title = "C", Type = IssueType.Bug }, [], bobId); // severity defaults to Minor

        async Task<string[]> Titles(IssueFilter f)
        {
            // Every filter goes through the URL form first, as saved filters do.
            var parsed = IssueFilterQuery.Parse(IssueFilterQuery.ToQueryString(f));
            Assert.Equal(f, parsed);
            return (await issues.GetIssuesAsync(project.Id, parsed)).Select(i => i.Title).Order().ToArray();
        }
        Assert.Equal(["A"], await Titles(new IssueFilter { ComponentId = ui.Id }));
        Assert.Equal(["C"], await Titles(new IssueFilter { ComponentId = 0 }));
        Assert.Equal(["A"], await Titles(new IssueFilter { FixVersionId = v1.Id }));
        Assert.Equal(["B"], await Titles(new IssueFilter { FixVersionId = v2.Id }));
        Assert.Equal(["C"], await Titles(new IssueFilter { FixVersionId = 0 }));
        Assert.Equal(["A"], await Titles(new IssueFilter { AffectsVersionId = v2.Id }));
        Assert.Equal(["A"], await Titles(new IssueFilter { Cf = "severity:major" }));
        Assert.Equal(["C"], await Titles(new IssueFilter { Cf = "severity:Minor" }));
        Assert.Equal(["B"], await Titles(new IssueFilter { Cf = "severity:" }));
        Assert.Equal(["A"], await Titles(new IssueFilter { ComponentId = ui.Id, FixVersionId = v1.Id, Cf = "severity:Major" }));

        var summary = (await cv.GetVersionsAsync(project.Id)).Single(v => v.Version.Id == v2.Id);
        Assert.Equal((1, 0, 1), (summary.Open, summary.Done, summary.AffectsCount));
        await using var db = app.Db();
        Assert.Contains(db.Activities.Where(x => x.IssueId == b.Id).Select(x => x.Text), t => t == "added component **API**");
    }

    [Fact]
    public async Task Estimates_are_plain_editable_fields()
    {
        var project = await app.CreateProjectAsync("alice", ProjectVisibility.Private, ("bob", ProjectRole.Developer));
        var issue = await app.CreateIssueAsync(project.Id, "bob");
        await using var bob = app.Scope(app.UserId("bob"));
        var estimates = bob.Get<EstimateService>();

        // Setting only the original estimate starts the remaining one at the same value.
        await estimates.SetEstimatesAsync(issue.Id, true, 600, false, null);
        Assert.Equal((600, 600), Tuple(await estimates.GetSummaryAsync(issue.Id)));
        // After that, each is edited on its own: nothing adjusts the remaining estimate.
        await estimates.SetEstimatesAsync(issue.Id, false, null, true, 90);
        await estimates.SetEstimatesAsync(issue.Id, true, 720, false, null);
        Assert.Equal((720, 90), Tuple(await estimates.GetSummaryAsync(issue.Id)));
        await estimates.SetEstimatesAsync(issue.Id, false, null, true, null);
        Assert.Equal((720, null), Tuple(await estimates.GetSummaryAsync(issue.Id)));
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => estimates.SetEstimatesAsync(issue.Id, true, -1, false, null));

        await using var db = app.Db();
        Assert.Contains(db.Activities.Where(x => x.IssueId == issue.Id).Select(x => x.Text), t => t == "set the remaining estimate to **1h 30m**");

        // Viewers can't change estimates.
        var viewer = await app.CreateUserAsync("fields_viewer");
        await using (var owner = app.Scope(app.UserId("alice"))) await owner.Get<MemberService>().AddMemberAsync(project.Id, viewer.UserName!, ProjectRole.Viewer);
        await using var v = app.Scope(viewer.Id);
        await Assert.ThrowsAsync<ForbiddenException>(() => v.Get<EstimateService>().SetEstimatesAsync(issue.Id, true, 60, false, null));
    }

    private static (int?, int?) Tuple(EstimateSummary? s) => (s!.OriginalMinutes, s.RemainingMinutes);

    [Fact]
    public void Repository_templates_parse_front_matter()
    {
        var t = IssueTemplateService.ParseRepoTemplate("bug_report.md",
            "---\nname: Bug report\nabout: Something broke\ntitle: \"[Bug] \"\nlabels: [bug, \"ui\"]\ntype: bug\n---\n### Steps\n1.\n");
        Assert.Equal(("Bug report", IssueType.Bug, "[Bug] "), (t.Name, t.Type, t.Title));
        Assert.Equal(["bug", "ui"], t.Labels);
        Assert.Equal("### Steps\n1.", t.Body);
        var plain = IssueTemplateService.ParseRepoTemplate("feature-request.md", "Describe it.");
        Assert.Equal(("Feature request", IssueType.Task, "Describe it."), (plain.Name, plain.Type, plain.Body));
    }

    [Fact]
    public async Task Repository_templates_are_offered_with_project_ones()
    {
        var project = await app.CreateProjectAsync("alice");
        var repo = await app.CreateRepoAsync(project.Id);
        app.Commit(repo, "main", ".gitbench/issue_templates/bug.md", "---\nname: Repo bug\nlabels: bug\ntype: bug\n---\nBody");
        await using var alice = app.Scope(app.UserId("alice"));
        await alice.Get<IssueTemplateService>().SaveAsync(new IssueTemplate { ProjectId = project.Id, Name = "Story", Type = IssueType.Story, TitlePrefix = "As a " });
        var full = await alice.Get<IssueService>().GetProjectAsync(project.Key);
        var choices = await alice.Get<IssueTemplateService>().GetChoicesAsync(full!);
        Assert.Equal(["Story", "Repo bug"], choices.Select(c => c.Name));
        var fromRepo = choices[1];
        Assert.Equal(IssueType.Bug, fromRepo.Type);
        Assert.Single(fromRepo.LabelIds); // matched to the project's "bug" label
    }

    // ---------- API ----------

    private static StringContent Json(object body) => new(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

    [Fact]
    public async Task Api_reads_and_writes_custom_fields_versions_and_estimates()
    {
        var (project, _, _) = await ProjectWithFieldsAsync();
        await using (var carol = app.Scope(app.UserId("carol")))
        {
            await carol.Get<ComponentVersionService>().SaveComponentAsync(new ProjectComponent { ProjectId = project.Id, Name = "UI" });
            await carol.Get<ComponentVersionService>().SaveVersionAsync(new ProjectVersion { ProjectId = project.Id, Name = "1.0" });
        }
        var client = await app.ApiClientAsync("bob");
        var r = await client.PostAsync($"/api/v1/projects/{project.Key}/issues", Json(new
        {
            title = "Via API", type = "bug", customFields = new Dictionary<string, object?> { ["severity"] = "major", ["reviewer"] = "carol" },
            components = new[] { "ui" }, fixVersions = new[] { "1.0" }, originalEstimate = "1d",
        }));
        Assert.True(r.StatusCode == HttpStatusCode.Created, await r.Content.ReadAsStringAsync());
        var issue = JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("Major", issue.GetProperty("customFields").GetProperty("severity").GetString());
        Assert.Equal("carol", issue.GetProperty("customFields").GetProperty("reviewer").GetString());
        Assert.Equal("UI", issue.GetProperty("components")[0].GetString());
        Assert.Equal("1.0", issue.GetProperty("fixVersions")[0].GetString());
        Assert.Equal(480, issue.GetProperty("estimates").GetProperty("remainingEstimateMinutes").GetInt32());
        Assert.Equal("1d", issue.GetProperty("estimates").GetProperty("originalEstimate").GetString());
        Assert.False(issue.TryGetProperty("timeTracking", out _));
        var number = issue.GetProperty("number").GetInt32();

        // Unknown keys and bad values are 422; PATCH changes only the named fields.
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.PatchAsync($"/api/v1/projects/{project.Key}/issues/{number}",
            Json(new { customFields = new { nope = 1 } }))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.PatchAsync($"/api/v1/projects/{project.Key}/issues/{number}",
            Json(new { customFields = new { severity = "Huge" } }))).StatusCode);
        r = await client.PatchAsync($"/api/v1/projects/{project.Key}/issues/{number}", Json(new { customFields = new { reviewer = (string?)null }, components = Array.Empty<string>() }));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        issue = JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(JsonValueKind.Null, issue.GetProperty("customFields").GetProperty("reviewer").ValueKind);
        Assert.Equal("Major", issue.GetProperty("customFields").GetProperty("severity").GetString());
        Assert.Equal(0, issue.GetProperty("components").GetArrayLength());

        // Filters.
        var list = JsonDocument.Parse(await client.GetStringAsync($"/api/v1/projects/{project.Key}/issues?fixVersion=1.0&cf=severity:Major")).RootElement;
        Assert.Equal(1, list.GetArrayLength());

        // Estimates are plain fields; the work log endpoints are gone.
        r = await client.PatchAsync($"/api/v1/projects/{project.Key}/issues/{number}", Json(new { remainingEstimate = "2h" }));
        issue = JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal((480, 120), (issue.GetProperty("estimates").GetProperty("originalEstimateMinutes").GetInt32(),
            issue.GetProperty("estimates").GetProperty("remainingEstimateMinutes").GetInt32()));
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/v1/projects/{project.Key}/issues/{number}/worklogs")).StatusCode);

        var fields = JsonDocument.Parse(await client.GetStringAsync($"/api/v1/projects/{project.Key}/fields")).RootElement;
        Assert.Equal("singleSelect", fields[0].GetProperty("type").GetString());
    }

    [Fact]
    public async Task Demo_seed_has_fields_components_and_versions_on_web()
    {
        await using var db = app.Db();
        var web = app.ProjectId("WEB");
        Assert.True(await db.CustomFields.CountAsync(f => f.ProjectId == web) >= 2);
        Assert.True(await db.ProjectComponents.CountAsync(c => c.ProjectId == web) >= 2);
        Assert.True(await db.ProjectVersions.CountAsync(v => v.ProjectId == web) >= 2);
        Assert.True(await db.Issues.AnyAsync(i => i.ProjectId == web && i.OriginalEstimateMinutes != null));
    }
}
