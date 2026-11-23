using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ByteBench.Data;
using ByteBench.Services;
using ByteBench.Services.Editor.Ai;
using ByteBench.Services.Releases;
using ByteBench.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ByteBench.Tests;

/// <summary>The app with the editor's AI backend on a scripted fake model (never the network); small item and rate limits.</summary>
public class ReleaseNotesApp : TestApp
{
    public FakeEditorChatModel Model { get; } = new();

    protected override void AddSettings(IDictionary<string, string?> settings)
    {
        settings["Ai:ReleaseNotes:MaxItems"] = "8";
        settings["Ai:ReleaseNotes:MaxPerRepoPerHour"] = "3";
        settings["Ai:ReleaseNotes:MaxOutputTokens"] = "2500";
    }

    protected override void ConfigureServices(IServiceCollection services) => services.AddSingleton<IEditorChatModel>(Model);
}

/// <summary>The same with the release notes feature switched off.</summary>
public class ReleaseNotesOffApp : ReleaseNotesApp
{
    protected override void AddSettings(IDictionary<string, string?> settings) => settings["Ai:ReleaseNotes:Enabled"] = "false";
}

/// <summary>Draft release notes: plain grouping, the AI draft on a fake model, bounds, redaction, sanitising, permissions, audit and REST.</summary>
public class ReleaseNotesTests(ReleaseNotesApp app) : IClassFixture<ReleaseNotesApp>
{
    private sealed record Setup(Project Project, Repository Repo, string Viewer);

    /// <summary>alice Owner, bob Developer, a viewer. The repo has release v1.0.0 on its first commit; later changes are merged pull requests.</summary>
    private async Task<Setup> SetupAsync()
    {
        var viewer = "viewer" + Guid.NewGuid().ToString("N")[..8];
        await app.CreateUserAsync(viewer);
        var project = await app.CreateProjectAsync("alice", ProjectVisibility.Private, ("bob", ProjectRole.Developer), (viewer, ProjectRole.Viewer));
        var repo = await app.CreateRepoAsync(project.Id, "alice");
        await using var s = app.Scope(app.UserId("alice"));
        await s.Get<ReleaseService>().CreateAsync(repo, new ReleaseInput { TagName = "v1.0.0", Target = "main", Title = "One" }, app.UserId("alice"));
        return new Setup(project, repo, viewer);
    }

    private async Task<PullRequest> MergedPrAsync(Setup t, string title, string author = "bob")
    {
        var branch = "b" + Guid.NewGuid().ToString("N")[..8];
        app.Commit(t.Repo, branch, branch + ".txt", title, from: "main", message: "work: " + title);
        await using var s = app.Scope(app.UserId(author));
        var pr = await s.Get<PullRequestService>().CreateAsync(t.Repo, title, null, branch, "main", app.UserId(author));
        await using var m = app.Scope(app.UserId("alice"));
        Assert.True((await m.Get<PullRequestService>().MergeAsync(pr.Id, app.User("alice"), new MergeRequest())).Success);
        return pr;
    }

    private async Task LabelIssueAsync(Setup t, int issueNumber, string label)
    {
        await using var db = app.Db();
        var issue = await db.Issues.Include(i => i.Labels).FirstAsync(i => i.ProjectId == t.Project.Id && i.Number == issueNumber);
        var l = await db.Labels.FirstOrDefaultAsync(x => x.ProjectId == t.Project.Id && x.Name == label) ?? new Label { ProjectId = t.Project.Id, Name = label };
        issue.Labels.Add(l);
        await db.SaveChangesAsync();
    }

    private static string Reply(params (string Id, string Section, string Text)[] items) =>
        JsonSerializer.Serialize(new { items = items.Select(i => new { id = i.Id, section = i.Section, text = i.Text }) });

    private void Script(Func<string> reply) => app.Model.Reset((_, _) => FakeEditorChatModel.Text(reply()));

    private string LastPrompt => app.Model.Setups[^1].UserMessage;

    private async Task<List<AuditEvent>> AuditAsync(Setup t)
    {
        await using var db = app.Db();
        return await db.AuditEvents.AsNoTracking().Where(e => e.Action == "release.ai_notes_generated" && e.TargetId == t.Repo.Id.ToString()).ToListAsync();
    }

    // ---------- Plain draft ----------

    [Fact]
    public async Task The_plain_draft_groups_merged_pull_requests_and_commits_by_label_and_title()
    {
        var t = await SetupAsync();
        var crash = await app.CreateIssueAsync(t.Project.Id, "alice", "Crash on save", IssueType.Bug);
        await LabelIssueAsync(t, crash.Number, "bug");
        var feature = await app.CreateIssueAsync(t.Project.Id, "alice", "Dark mode");
        await LabelIssueAsync(t, feature.Number, "enhancement");
        var fix = await MergedPrAsync(t, $"Stop the crash when saving {t.Project.Key}-{crash.Number}");
        var feat = await MergedPrAsync(t, $"Dark mode {t.Project.Key}-{feature.Number}");
        var breaking = await MergedPrAsync(t, "feat!: drop the v1 API");
        var other = await MergedPrAsync(t, "Tidy the build scripts");
        app.Commit(t.Repo, "main", "direct.txt", "d", message: "Direct commit to main");

        await using var s = app.Scope(app.UserId("bob"));
        var draft = await s.Get<ReleaseNotesService>().GeneratePlainAsync(t.Repo, "main");
        Assert.Equal("plain", draft.Mode);
        Assert.Equal("v1.0.0", draft.PreviousTag);
        Assert.Equal(4, draft.PullRequests);
        Assert.Equal(1, draft.Commits);
        var md = draft.Markdown;

        string Section(string name)
        {
            var i = md.IndexOf("### " + name, StringComparison.Ordinal);
            Assert.True(i >= 0, $"missing section {name}:\n{md}");
            var next = md.IndexOf("\n### ", i + 4, StringComparison.Ordinal);
            return md[i..(next < 0 ? md.Length : next)];
        }
        Assert.Contains("drop the v1 API", Section("Breaking changes"));
        Assert.Contains("Dark mode", Section("Features"));
        Assert.Contains("Stop the crash", Section("Fixes"));
        Assert.Contains("Tidy the build scripts", Section("Other"));
        Assert.Contains("Direct commit to main", Section("Other"));
        Assert.True(md.IndexOf("### Breaking changes", StringComparison.Ordinal) < md.IndexOf("### Features", StringComparison.Ordinal));
        Assert.Contains($"[#{fix.Number}](/p/{t.Project.Key}/repos/{t.Repo.Slug}/pull-requests/{fix.Number})", md); // PR numbers link to the pull request
        Assert.Contains("by @bob", md);
        Assert.Equal(1, md.Split($"[#{feat.Number}]").Length - 1);
        Assert.DoesNotContain("work: ", md); // the PRs' own commits are folded into them
        await using var db = app.Db();
        Assert.Equal(1, await db.Releases.CountAsync(r => r.RepositoryId == t.Repo.Id)); // nothing was saved
    }

    [Fact]
    public void Classification_follows_labels_conventional_prefixes_and_breaking_markers()
    {
        static NotesItem Item(string title, params string[] labels) => new("pr-1", true, 1, title, "a", labels, [], "/x", null);
        Assert.Equal(NotesSection.Breaking, ReleaseNotesService.Classify(Item("Remove old endpoint", "Breaking-Change")));
        Assert.Equal(NotesSection.Breaking, ReleaseNotesService.Classify(Item("refactor(api)!: rename things")));
        Assert.Equal(NotesSection.Fixes, ReleaseNotesService.Classify(Item("Handle null", "bug")));
        Assert.Equal(NotesSection.Fixes, ReleaseNotesService.Classify(Item("fix: handle null")));
        Assert.Equal(NotesSection.Fixes, ReleaseNotesService.Classify(Item("Something", "type:bug")));
        Assert.Equal(NotesSection.Features, ReleaseNotesService.Classify(Item("feat(ui): add chart")));
        Assert.Equal(NotesSection.Features, ReleaseNotesService.Classify(Item("Chart", "Enhancement")));
        Assert.Equal(NotesSection.Other, ReleaseNotesService.Classify(Item("Fixture cleanup")));
        Assert.Equal(NotesSection.Other, ReleaseNotesService.Classify(Item("Bump deps", "dependencies")));
    }

    [Fact]
    public async Task Nothing_merged_gives_an_empty_draft_without_calling_the_model()
    {
        var t = await SetupAsync();
        Script(() => throw new InvalidOperationException("the model must not be called"));
        await using var s = app.Scope(app.UserId("bob"));
        var draft = await s.Get<ReleaseNotesService>().GenerateWithAiAsync(t.Repo, "main");
        Assert.Contains("No changes since the previous release", draft.Markdown);
        Assert.Empty(app.Model.Setups);
        Assert.Empty(await AuditAsync(t));
    }

    // ---------- AI draft ----------

    [Fact]
    public async Task The_ai_draft_uses_the_models_sections_and_wording_but_our_numbers_links_and_authors()
    {
        var t = await SetupAsync();
        var a = await MergedPrAsync(t, "wip crash thing");
        var b = await MergedPrAsync(t, "new dark mode");
        var c = await MergedPrAsync(t, "drop xml");
        Script(() => Reply(($"pr-{a.Number}", "fixes", "Fixed a crash when saving."), ($"pr-{b.Number}", "features", "Dark mode is here."), ($"pr-{c.Number}", "breaking", "XML export was removed."),
            ("pr-9999", "features", "An invented feature that never happened."), ("c-deadbee", "fixes", "An invented commit.")));

        await using var s = app.Scope(app.UserId("bob"));
        var draft = await s.Get<ReleaseNotesService>().GenerateWithAiAsync(t.Repo, "main");
        Assert.Equal("ai", draft.Mode);
        var md = draft.Markdown;
        Assert.Contains("### Breaking changes\n\n- XML export was removed. ([#" + c.Number + "]", md);
        Assert.Contains("### Features\n\n- Dark mode is here.", md);
        Assert.Contains("### Fixes\n\n- Fixed a crash when saving.", md);
        Assert.Contains("by @bob", md);
        Assert.DoesNotContain("invented", md);
        Assert.DoesNotContain("9999", md);
        Assert.Contains($"(/p/{t.Project.Key}/repos/{t.Repo.Slug}/pull-requests/{a.Number})", md);

        // The fake model saw the three items and the rules, and the call was capped.
        var setup = app.Model.Setups[^1];
        Assert.Equal(2500, setup.MaxTokensPerTurn);
        Assert.Contains("never invent", setup.System, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("features", setup.System);
        Assert.Contains($"[pr-{a.Number}]", setup.UserMessage);
        Assert.Contains("UNTRUSTED", setup.UserMessage);
        Assert.NotNull(setup.OutputSchema);
        await using var db = app.Db();
        Assert.Equal(1, await db.Releases.CountAsync(r => r.RepositoryId == t.Repo.Id)); // never saved
    }

    [Fact]
    public async Task Items_the_model_forgets_land_under_other_and_repeats_are_ignored()
    {
        var t = await SetupAsync();
        var a = await MergedPrAsync(t, "alpha change");
        var b = await MergedPrAsync(t, "beta change");
        Script(() => Reply(($"pr-{a.Number}", "features", "Alpha."), ($"pr-{a.Number}", "fixes", "Alpha again, differently.")));
        await using var s = app.Scope(app.UserId("bob"));
        var draft = await s.Get<ReleaseNotesService>().GenerateWithAiAsync(t.Repo, "main");
        Assert.Contains("### Features\n\n- Alpha.", draft.Markdown);
        Assert.DoesNotContain("differently", draft.Markdown);
        Assert.Contains("### Other\n\n- beta change", draft.Markdown);
        Assert.Contains("1 item(s) the AI skipped", draft.Notice);
        Assert.Contains($"[#{b.Number}]", draft.Markdown);
    }

    [Fact]
    public async Task Model_output_is_stripped_of_html_scripts_and_dangerous_links_and_renders_safely()
    {
        var t = await SetupAsync();
        var a = await MergedPrAsync(t, "one");
        var b = await MergedPrAsync(t, "two");
        var c = await MergedPrAsync(t, "three");
        Script(() => Reply(
            ($"pr-{a.Number}", "features", "<script>alert(1)</script>Add **widget** <img src=x onerror=alert(2)> now"),
            ($"pr-{b.Number}", "fixes", "[click me](javascript:alert(3)) and ![pic](https://evil.example/x.png) <b onclick=steal()>bold</b>"),
            ($"pr-{c.Number}", "other", "<iframe src=//evil.example></iframe>- # not a heading\nsecond line with <style>body{display:none}</style> text")));
        await using var s = app.Scope(app.UserId("bob"));
        var md = (await s.Get<ReleaseNotesService>().GenerateWithAiAsync(t.Repo, "main")).Markdown;
        foreach (var bad in new[] { "<script", "alert(", "<img", "onerror", "javascript:", "<iframe", "<style", "onclick", "evil.example", "![", "display:none" })
            Assert.DoesNotContain(bad, md, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("widget", md);
        Assert.Contains("click me", md);
        var html = Display.Markdown(md).Value!;
        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("onerror", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<img", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Sanitising_model_text_directly()
    {
        Assert.Equal("Hello world", ReleaseNotesService.SanitizeModelText("  <b>Hello</b>\n\n world  "));
        Assert.Equal("", ReleaseNotesService.SanitizeModelText("<script>x</script>"));
        Assert.Equal("link", ReleaseNotesService.SanitizeModelText("[link](data:text/html;base64,AAAA)"));
        Assert.Equal("a < b", ReleaseNotesService.SanitizeModelText("a &lt; b").Replace("&lt;", "<")); // entities are left to the Markdown escaper
        Assert.True(ReleaseNotesService.SanitizeModelText(new string('x', 1000)).Length <= 240);
        Assert.Equal("item", ReleaseNotesService.SanitizeModelText("- item"));
    }

    [Fact]
    public async Task The_prompt_carries_no_secrets_no_emails_and_no_descriptions()
    {
        var t = await SetupAsync();
        const string token = "ghp_0123456789abcdefghijklmnopqrstuvwxyzAB";
        const string aws = "AKIAIOSFODNN7EXAMPLE";
        const string opaque = "Zm9vYmFyYmF6cXV4MTIzNDU2Nzg5MGFiY2RlZmdoaWo";
        var branch = "b" + Guid.NewGuid().ToString("N")[..8];
        app.Commit(t.Repo, branch, "s.txt", "s", from: "main", message: "secret commit password=hunter2hunter2");
        PullRequest pr;
        await using (var s = app.Scope(app.UserId("bob")))
            pr = await s.Get<PullRequestService>().CreateAsync(t.Repo, $"Use {token} and {aws} {opaque} Bearer abcdefghijklmnop1234 postgres://admin:s3cr3t@db/x", "DESCRIPTION-MARKER with bob@example.com inside", branch, "main", app.UserId("bob"));
        await using (var m = app.Scope(app.UserId("alice")))
            Assert.True((await m.Get<PullRequestService>().MergeAsync(pr.Id, app.User("alice"), new MergeRequest())).Success);
        Script(() => Reply(($"pr-{pr.Number}", "other", "Uses credentials.")));

        await using var s2 = app.Scope(app.UserId("bob"));
        var draft = await s2.Get<ReleaseNotesService>().GenerateWithAiAsync(t.Repo, "main");
        var prompt = LastPrompt;
        foreach (var secret in new[] { token, aws, opaque, "abcdefghijklmnop1234", "s3cr3t", "hunter2", "DESCRIPTION-MARKER", "bob@example.com", "@example.com", "Authorization" })
            Assert.DoesNotContain(secret, prompt);
        Assert.Contains("[redacted]", prompt);
        Assert.Contains($"[pr-{pr.Number}]", prompt);
        foreach (var secret in new[] { token, aws, "s3cr3t", "hunter2" }) Assert.DoesNotContain(secret, draft.Markdown);
        Assert.Equal("[redacted]", ReleaseNotesService.Redact("ghp_0123456789abcdefghijklmnopqrstuvwxyzAB"));
        Assert.Equal("see abcdef0123", ReleaseNotesService.Redact("see abcdef0123")); // short hex (a short sha) is kept
    }

    [Fact]
    public async Task Items_are_bounded_and_titles_trimmed()
    {
        var t = await SetupAsync();
        for (var i = 0; i < 10; i++) await MergedPrAsync(t, $"change number {i} " + new string('x', 150));
        Script(() => Reply());
        await using var s = app.Scope(app.UserId("bob"));
        var service = s.Get<ReleaseNotesService>();
        var (items, previous, omitted) = await service.GatherAsync(t.Repo, "main");
        Assert.Equal(8, items.Count); // Ai:ReleaseNotes:MaxItems
        Assert.True(omitted >= 2);
        Assert.Equal("v1.0.0", previous);
        Assert.All(items, i => Assert.True(i.Title.Length <= 200));
        var draft = await service.GenerateWithAiAsync(t.Repo, "main");
        Assert.Contains("Only the newest 8 items were used", draft.Notice);
        Assert.Equal(8, LastPrompt.Split('\n').Count(l => l.StartsWith("[pr-")));
        Assert.Equal("#10", "#" + items.Max(i => i.Number)); // the newest are kept
    }

    [Fact]
    public void A_huge_prompt_stays_small_by_construction()
    {
        var items = Enumerable.Range(1, 300).Select(n => new NotesItem($"pr-{n}", true, n, new string('t', 200), "user" + n, ["a", "b", "c", "d", "e", "f"], ["K-1", "K-2"], "/x", null)).ToList();
        var prompt = ReleaseNotesService.BuildUserPrompt("repo", "v2", "v1", items, "abc123");
        Assert.True(prompt.Length < 120_000);
        Assert.Contains("<<<UNTRUSTED", prompt);
        Assert.Contains("<<<END UNTRUSTED boundary=abc123>>>", prompt);
    }

    [Fact]
    public async Task A_refusal_or_unusable_answer_is_reported_and_not_audited()
    {
        var t = await SetupAsync();
        await MergedPrAsync(t, "something");
        app.Model.Reset((_, _) => new EditorModelTurn("", [], "refusal", 10, 0));
        await using (var s = app.Scope(app.UserId("bob")))
        {
            var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => s.Get<ReleaseNotesService>().GenerateWithAiAsync(t.Repo, "main"));
            Assert.Contains("declined", refused.Message);
        }
        Script(() => "this is not json");
        await using (var s = app.Scope(app.UserId("bob")))
            await Assert.ThrowsAsync<InvalidOperationException>(() => s.Get<ReleaseNotesService>().GenerateWithAiAsync(t.Repo, "main"));
        Assert.Empty(await AuditAsync(t));
    }

    [Fact]
    public async Task Audit_records_counts_but_never_the_content_and_the_hourly_limit_applies_per_repository()
    {
        var t = await SetupAsync();
        var pr = await MergedPrAsync(t, "Rework billing");
        Script(() => Reply(($"pr-{pr.Number}", "features", "SECRET-NOTE-TEXT billing was reworked.")));
        for (var i = 0; i < 3; i++)
        {
            await using var s = app.Scope(app.UserId("bob"));
            await s.Get<ReleaseNotesService>().GenerateWithAiAsync(t.Repo, "main");
        }
        var events = await AuditAsync(t);
        Assert.Equal(3, events.Count);
        var details = events[0].Details!;
        Assert.Contains("\"items\":1", details);
        Assert.DoesNotContain("SECRET-NOTE-TEXT", details);
        Assert.DoesNotContain("Rework billing", details);

        await using (var s = app.Scope(app.UserId("bob")))
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => s.Get<ReleaseNotesService>().GenerateWithAiAsync(t.Repo, "main"));
            Assert.Contains("limit of 3", ex.Message);
            Assert.Equal("plain", (await s.Get<ReleaseNotesService>().GeneratePlainAsync(t.Repo, "main")).Mode); // the plain draft isn't limited
        }
        var other = await SetupAsync(); // another repository has its own budget
        await MergedPrAsync(other, "x");
        Script(() => Reply());
        await using (var s = app.Scope(app.UserId("bob"))) await s.Get<ReleaseNotesService>().GenerateWithAiAsync(other.Repo, "main");
    }

    // ---------- Permissions ----------

    [Fact]
    public async Task Viewers_cannot_draft_notes_and_unknown_tags_are_refused()
    {
        var t = await SetupAsync();
        await MergedPrAsync(t, "x");
        Script(() => Reply());
        await using (var s = app.Scope(app.UserId(t.Viewer)))
        {
            await Assert.ThrowsAsync<ForbiddenException>(() => s.Get<ReleaseNotesService>().GeneratePlainAsync(t.Repo, "main"));
            await Assert.ThrowsAsync<ForbiddenException>(() => s.Get<ReleaseNotesService>().GenerateWithAiAsync(t.Repo, "main"));
        }
        Assert.Empty(app.Model.Setups);
        await using var d = app.Scope(app.UserId("bob"));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => d.Get<ReleaseNotesService>().GeneratePlainAsync(t.Repo, "no-such-tag"));
        Assert.Contains("Unknown tag or branch", ex.Message);
    }

    // ---------- REST ----------

    [Fact]
    public async Task Rest_returns_the_draft_and_enforces_permissions()
    {
        var t = await SetupAsync();
        var pr = await MergedPrAsync(t, "Add exports");
        Script(() => Reply(($"pr-{pr.Number}", "features", "You can now export.")));
        var url = $"/api/v1/projects/{t.Project.Key}/repos/{t.Repo.Slug}/releases/notes:generate";
        var bob = await app.ApiClientAsync("bob");
        var viewer = await app.ApiClientAsync(t.Viewer);

        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsJsonAsync(url, new { tag = "main" })).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await bob.PostAsJsonAsync(url, new { tag = "" })).StatusCode);

        var ai = await bob.PostAsJsonAsync(url, new { tag = "main" });
        Assert.Equal(HttpStatusCode.OK, ai.StatusCode);
        var json = JsonDocument.Parse(await ai.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("ai", json.GetProperty("mode").GetString());
        Assert.Contains("You can now export.", json.GetProperty("markdown").GetString());
        Assert.Equal("v1.0.0", json.GetProperty("previousTag").GetString());

        var plain = await bob.PostAsJsonAsync(url, new { tag = "main", ai = false });
        var plainJson = JsonDocument.Parse(await plain.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("plain", plainJson.GetProperty("mode").GetString());
        Assert.Contains("Add exports", plainJson.GetProperty("markdown").GetString());
    }
}

/// <summary>AI turned off (the default app: no model, AI disabled) or the feature switched off: the AI draft is refused, the plain one works.</summary>
public class ReleaseNotesAiOffTests(TestApp app) : IClassFixture<TestApp>
{
    [Fact]
    public async Task With_ai_off_the_ai_draft_is_refused_and_the_plain_draft_still_works()
    {
        var project = await app.CreateProjectAsync("alice", ProjectVisibility.Private, ("bob", ProjectRole.Developer));
        var repo = await app.CreateRepoAsync(project.Id, "alice");
        await using var s = app.Scope(app.UserId("bob"));
        var notes = s.Get<ReleaseNotesService>();
        var (available, reason) = await notes.GetAiAvailabilityAsync();
        Assert.False(available);
        Assert.Contains("isn't set up", reason);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => notes.GenerateWithAiAsync(repo, "main"));
        Assert.Contains("isn't set up", ex.Message);
        Assert.Equal("plain", (await notes.GeneratePlainAsync(repo, "main")).Mode);

        var client = await app.ApiClientAsync("bob");
        var url = $"/api/v1/projects/{project.Key}/repos/{repo.Slug}/releases/notes:generate";
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.PostAsJsonAsync(url, new { tag = "main" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync(url, new { tag = "main", ai = false })).StatusCode);
    }
}

public class ReleaseNotesSwitchedOffTests(ReleaseNotesOffApp app) : IClassFixture<ReleaseNotesOffApp>
{
    [Fact]
    public async Task The_feature_switch_turns_the_ai_draft_off_even_with_a_working_model()
    {
        var project = await app.CreateProjectAsync("alice", ProjectVisibility.Private, ("bob", ProjectRole.Developer));
        var repo = await app.CreateRepoAsync(project.Id, "alice");
        await using var s = app.Scope(app.UserId("bob"));
        var (available, reason) = await s.Get<ReleaseNotesService>().GetAiAvailabilityAsync();
        Assert.False(available);
        Assert.Contains("Ai:ReleaseNotes:Enabled", reason);
        await Assert.ThrowsAsync<InvalidOperationException>(() => s.Get<ReleaseNotesService>().GenerateWithAiAsync(repo, "main"));
        Assert.Empty(app.Model.Setups);
    }
}
