using System.Net;
using System.Net.Http.Json;
using ByteBench.Data;
using ByteBench.Services;
using ByteBench.Services.Explore;
using ByteBench.Services.Git;
using ByteBench.Services.Snippets;
using ByteBench.Services.Wiki;
using ByteBench.Tests.Infrastructure;

namespace ByteBench.Tests;

/// <summary>Project wikis (git-backed pages, conflicts, permissions, links, search) and snippets (visibility, revisions, API).</summary>
public class WikiSnippetTests(TestApp app) : IClassFixture<TestApp>
{
    private async Task<Project> ProjectAsync(ProjectVisibility visibility = ProjectVisibility.Private) =>
        await app.CreateProjectAsync("carol", visibility, ("bob", ProjectRole.Developer), ("dan", ProjectRole.Viewer));

    private async Task<WikiInfo> WikiAsync(TestScope s, Project p) => (await s.Get<WikiService>().GetWikiAsync(p.Key))!;

    // ---------- Wiki ----------

    [Fact]
    public async Task Saving_a_page_creates_a_commit_by_the_user()
    {
        var p = await ProjectAsync();
        await using var s = app.Scope(app.UserId("bob"));
        var wikis = s.Get<WikiService>();
        var w = await WikiAsync(s, p);
        var saved = await wikis.SavePageAsync(w, null, "guides/Getting started", "# Hello", "Add getting started", null);
        Assert.Equal("guides/Getting-started", saved);

        var page = await wikis.GetPageAsync(w, saved);
        Assert.NotNull(page);
        Assert.Equal("# Hello", page.Content);
        Assert.Equal("Getting started", page.Title);
        Assert.Equal("Add getting started", page.LastCommit!.MessageShort);
        Assert.Equal(app.User("bob").DisplayName, page.LastCommit.AuthorName);

        // An update without a message gets a default one, and the history has both commits.
        await wikis.SavePageAsync(w, saved, saved, "# Hello again", null, page.BlobSha);
        var (history, _) = await wikis.HistoryAsync(w, saved);
        Assert.Equal(["Update Getting started", "Add getting started"], history.Select(c => c.MessageShort));
        Assert.True(GitOps.Exists(wikis.RepoPathFor(p.Key)));

        // Revert restores the old content as a new commit; the diff shows the change.
        await wikis.RevertPageAsync(w, saved, history[1].Sha);
        Assert.Equal("# Hello", (await wikis.GetPageAsync(w, saved))!.Content);
        var diff = Assert.Single(await wikis.DiffAsync(w, saved, null, history[0].Sha));
        Assert.Contains(diff.Hunks.SelectMany(h => h.Lines), l => l.Kind == DiffLineKind.Added && l.Text == "# Hello again");
    }

    [Fact]
    public async Task A_page_changed_since_it_was_opened_is_a_conflict()
    {
        var p = await ProjectAsync();
        await using var s = app.Scope(app.UserId("bob"));
        var wikis = s.Get<WikiService>();
        var w = await WikiAsync(s, p);
        await wikis.SavePageAsync(w, null, "Notes", "v1", null, null);
        var opened = (await wikis.GetPageAsync(w, "Notes"))!;

        // Someone else saves first...
        await using (var other = app.Scope(app.UserId("carol")))
            await other.Get<WikiService>().SavePageAsync(await WikiAsync(other, p), "Notes", "Notes", "v2 by carol", null, opened.BlobSha);

        // ...so Bob's save from the old version is refused, with what's there now.
        var ex = await Assert.ThrowsAsync<WikiConflictException>(() => wikis.SavePageAsync(w, "Notes", "Notes", "v2 by bob", null, opened.BlobSha));
        Assert.Equal("v2 by carol", ex.CurrentContent);
        Assert.Equal("v2 by carol", (await wikis.GetPageAsync(w, "Notes"))!.Content);

        // Creating a page that already exists, or moving onto one, is a conflict too.
        await Assert.ThrowsAsync<WikiConflictException>(() => wikis.SavePageAsync(w, null, "Notes", "x", null, null));
        await wikis.SavePageAsync(w, null, "Other", "o", null, null);
        var otherPage = (await wikis.GetPageAsync(w, "Other"))!;
        await Assert.ThrowsAsync<WikiConflictException>(() => wikis.SavePageAsync(w, "Other", "Notes", "o", null, otherPage.BlobSha));

        // With the current sha it saves (the "save mine anyway" path).
        await wikis.SavePageAsync(w, "Notes", "Notes", "v3 by bob", null, ex.CurrentSha);
        Assert.Equal("v3 by bob", (await wikis.GetPageAsync(w, "Notes"))!.Content);
    }

    [Fact]
    public async Task Viewers_read_developers_edit_outsiders_see_nothing()
    {
        var p = await ProjectAsync();
        await using (var s = app.Scope(app.UserId("bob")))
            await s.Get<WikiService>().SavePageAsync(await WikiAsync(s, p), null, "home", "Welcome", null, null);

        await using (var viewer = app.Scope(app.UserId("dan")))
        {
            var wikis = viewer.Get<WikiService>();
            var w = await WikiAsync(viewer, p);
            Assert.False(w.CanEdit);
            Assert.Equal("Welcome", (await wikis.GetPageAsync(w, "home"))!.Content);
            await Assert.ThrowsAsync<ForbiddenException>(() => wikis.SavePageAsync(w, null, "Mine", "x", null, null));
        }

        await app.CreateUserAsync("wikioutsider");
        await using (var outsider = app.Scope(app.UserId("wikioutsider")))
            Assert.Null(await outsider.Get<WikiService>().GetWikiAsync(p.Key));

        // Turning the wiki off (Owner) hides it, even from Developers.
        await using (var owner = app.Scope(app.UserId("carol")))
            await owner.Get<WikiService>().SetEnabledAsync(p.Id, false);
        await using (var dev = app.Scope(app.UserId("bob")))
        {
            var w = await WikiAsync(dev, p);
            Assert.False(w.Enabled);
            await Assert.ThrowsAsync<ForbiddenException>(() => dev.Get<WikiService>().GetPageAsync(w, "home"));
            await Assert.ThrowsAsync<ForbiddenException>(() => dev.Get<WikiService>().SetEnabledAsync(p.Id, true));
        }
    }

    [Fact]
    public void Wiki_links_relative_links_and_issue_keys_render()
    {
        var ctx = new WikiRenderContext("WEB", "guides/Setup", ["home", "guides/Setup", "guides/Install", "Release-notes"]);
        var html = WikiMarkdown.RenderHtml(
            "See [[Install]], [[Release notes|the notes]], [[Missing page]] and WEB-12.\n\n" +
            "[next](Install) ![shot](uploads/a.png) [bad](javascript:alert(1)) `WEB-3` [WEB-4](https://example.com)", ctx);

        Assert.Contains("<a href=\"/p/WEB/wiki/guides/Install\">Install</a>", html);
        Assert.Contains("<a href=\"/p/WEB/wiki/Release-notes\">the notes</a>", html);
        Assert.Contains("class=\"wiki-missing\"", html);
        Assert.Contains("href=\"/p/WEB/wiki/Missing-page?action=edit\"", html);
        Assert.Contains("<a href=\"/p/WEB/issues/12\" class=\"issue-key-link\">WEB-12</a>", html);
        Assert.Contains("<a href=\"/p/WEB/wiki/guides/Install\">next</a>", html);
        Assert.Contains("src=\"/wiki-files/WEB/uploads/a.png\"", html);
        Assert.DoesNotContain("javascript:", html);
        Assert.Contains("<code>WEB-3</code>", html); // not inside code
        Assert.Contains("<a href=\"https://example.com\">WEB-4</a>", html); // nor inside an existing link
    }

    [Fact]
    public void Page_paths_are_validated()
    {
        Assert.Equal("guides/Getting-started", WikiPaths.Normalize(" /guides/Getting started.md "));
        Assert.Null(WikiPaths.Normalize("../etc/passwd"));
        Assert.Null(WikiPaths.Normalize("uploads/x"));
        Assert.Null(WikiPaths.Normalize("a/<b>"));
        Assert.True(WikiService.IsWikiSlug("WEB", "wiki"));
        Assert.True(WikiService.IsWikiSlug("WEB", "web.wiki"));
        Assert.False(WikiService.IsWikiSlug("WEB", "ops.wiki"));
    }

    [Fact]
    public async Task Repository_slugs_that_shadow_the_wiki_are_reserved()
    {
        var p = await ProjectAsync();
        await using var s = app.Scope(app.UserId("carol"));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => s.Get<RepositoryService>().CreateAsync(p.Id, "Wiki", "wiki", null, app.User("carol")));
        Assert.Contains("reserved", ex.Message);
    }

    [Fact]
    public async Task Wiki_pages_are_found_by_global_search()
    {
        var p = await ProjectAsync();
        await using (var s = app.Scope(app.UserId("bob")))
            await s.Get<WikiService>().SavePageAsync(await WikiAsync(s, p), null, "Runbook", "Restart the flux capacitor gently.", null, null);

        await using (var dan = app.Scope(app.UserId("dan")))
        {
            var r = await dan.Get<SearchService>().SearchAsync("flux capacitor");
            var hit = Assert.Single(r.Wiki);
            Assert.Equal("Runbook", hit.Title);
            Assert.Equal($"/p/{p.Key}/wiki/Runbook", hit.Url);
            Assert.Contains("flux capacitor", hit.Excerpt);
        }
        await app.CreateUserAsync("wikisearcher");
        await using (var other = app.Scope(app.UserId("wikisearcher")))
            Assert.Empty((await other.Get<SearchService>().SearchAsync("flux capacitor")).Wiki);
    }

    [Fact]
    public async Task Wiki_api_crud_and_conflict()
    {
        var p = await ProjectAsync();
        using var bob = await app.ApiClientAsync("bob");
        var created = await bob.PostAsJsonAsync($"/api/v1/projects/{p.Key}/wiki/pages", new { path = "Api page", content = "one" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var page = (await created.Content.ReadFromJsonAsync<WikiPageDtoLite>())!;
        Assert.Equal("Api-page", page.Path);

        var ok = await bob.PutAsJsonAsync($"/api/v1/projects/{p.Key}/wiki/pages/Api-page", new { content = "two", sha = page.Sha });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var stale = await bob.PutAsJsonAsync($"/api/v1/projects/{p.Key}/wiki/pages/Api-page", new { content = "three", sha = page.Sha });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);

        using var dan = await app.ApiClientAsync("dan");
        Assert.Equal("two", (await dan.GetFromJsonAsync<WikiPageDtoLite>($"/api/v1/projects/{p.Key}/wiki/pages/Api-page"))!.Content);
        Assert.Equal(HttpStatusCode.Forbidden, (await dan.DeleteAsync($"/api/v1/projects/{p.Key}/wiki/pages/Api-page")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await bob.DeleteAsync($"/api/v1/projects/{p.Key}/wiki/pages/Api-page")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await dan.GetAsync($"/api/v1/projects/{p.Key}/wiki/pages/Api-page")).StatusCode);
    }

    private sealed record WikiPageDtoLite(string Path, string Content, string Sha);

    // ---------- Snippets ----------

    [Fact]
    public void Snippet_visibility_rule()
    {
        var personalPrivate = new Snippet { AuthorId = 1, Visibility = SnippetVisibility.Private };
        var personalPublic = new Snippet { AuthorId = 1, Visibility = SnippetVisibility.Public };
        var project = new Snippet { AuthorId = 1, ProjectId = 5, Visibility = SnippetVisibility.Project };
        var projectPrivate = new Snippet { AuthorId = 1, ProjectId = 5, Visibility = SnippetVisibility.Private };

        Assert.True(SnippetService.CanView(personalPrivate, 1, false, false));
        Assert.False(SnippetService.CanView(personalPrivate, 2, false, true));
        Assert.True(SnippetService.CanView(personalPrivate, 2, true, false)); // admin
        Assert.True(SnippetService.CanView(personalPublic, 2, false, false));
        Assert.False(SnippetService.CanView(personalPublic, null, false, false)); // signed-in users only
        Assert.True(SnippetService.CanView(project, 2, false, true));
        Assert.False(SnippetService.CanView(project, 2, false, false));
        Assert.False(SnippetService.CanView(projectPrivate, 2, false, true));
    }

    private static SnippetInput Input(string title, SnippetVisibility v, string content = "echo hi") =>
        new(title, null, v, [new SnippetFileInput("run.sh", content)]);

    [Fact]
    public async Task Snippet_visibility_in_lists_and_reads()
    {
        var p = await ProjectAsync();
        await app.CreateUserAsync("snipoutsider");
        int priv, pub, proj;
        await using (var bob = app.Scope(app.UserId("bob")))
        {
            var svc = bob.Get<SnippetService>();
            priv = (await svc.CreateAsync(Input("secret", SnippetVisibility.Private))).Id;
            pub = (await svc.CreateAsync(Input("shared", SnippetVisibility.Public))).Id;
            proj = (await svc.CreateAsync(Input("team", SnippetVisibility.Project), p.Id)).Id;
            await Assert.ThrowsAsync<InvalidOperationException>(() => svc.CreateAsync(Input("bad", SnippetVisibility.Project)));
            await Assert.ThrowsAsync<InvalidOperationException>(() => svc.CreateAsync(Input("bad", SnippetVisibility.Public), p.Id));
            Assert.Equal(3, (await svc.ListMineAsync()).Count(s => s.Snippet.Id is var id && (id == priv || id == pub || id == proj)));
        }
        await using (var dan = app.Scope(app.UserId("dan"))) // project Viewer
        {
            var svc = dan.Get<SnippetService>();
            Assert.Null(await svc.GetAsync(priv));
            Assert.NotNull(await svc.GetAsync(pub));
            Assert.NotNull(await svc.GetAsync(proj));
            Assert.Contains(await svc.ListForProjectAsync(p.Id), s => s.Snippet.Id == proj);
            var explore = await svc.ExploreAsync();
            Assert.Contains(explore, s => s.Snippet.Id == pub);
            Assert.DoesNotContain(explore, s => s.Snippet.Id == priv);
            // Viewers can't create project snippets, and nobody but the author edits.
            await Assert.ThrowsAsync<ForbiddenException>(() => svc.CreateAsync(Input("x", SnippetVisibility.Project), p.Id));
            await Assert.ThrowsAsync<ForbiddenException>(() => svc.UpdateAsync(pub, Input("hijack", SnippetVisibility.Public)));
            await svc.AddCommentAsync(pub, "Nice one");
        }
        await using (var outsider = app.Scope(app.UserId("snipoutsider")))
        {
            var svc = outsider.Get<SnippetService>();
            Assert.Null(await svc.GetAsync(proj));
            Assert.Null(await svc.GetAsync(priv));
            Assert.NotNull(await svc.GetAsync(pub));
            Assert.DoesNotContain(await svc.ExploreAsync(), s => s.Snippet.Id == proj);
        }
        await using (var carol = app.Scope(app.UserId("carol"))) // project Owner may delete the project snippet
            await carol.Get<SnippetService>().DeleteAsync(proj);
    }

    [Fact]
    public async Task Changing_files_adds_a_revision()
    {
        await using var bob = app.Scope(app.UserId("bob"));
        var svc = bob.Get<SnippetService>();
        var s = await svc.CreateAsync(Input("rev", SnippetVisibility.Private, "v1"));
        await svc.UpdateAsync(s.Id, Input("renamed only", SnippetVisibility.Private, "v1"));
        Assert.Equal(1, (await svc.GetAsync(s.Id))!.Snippet.CurrentRevision);
        await svc.UpdateAsync(s.Id, Input("renamed only", SnippetVisibility.Private, "v2"));
        var view = (await svc.GetAsync(s.Id))!;
        Assert.Equal(2, view.Snippet.CurrentRevision);
        Assert.Equal("v2", view.Revision.Files[0].Content);
        Assert.Equal("v1", (await svc.GetAsync(s.Id, 1))!.Revision.Files[0].Content);
    }

    [Fact]
    public async Task Snippet_api_and_raw_file_are_plain_text()
    {
        using var bob = await app.ApiClientAsync("bob");
        var created = await bob.PostAsJsonAsync("/api/v1/snippets", new
        {
            title = "Html", visibility = "public", files = new[] { new { fileName = "x.html", content = "<script>alert(1)</script>" } },
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await created.Content.ReadFromJsonAsync<SnippetIdLite>())!.Id;

        using var dan = await app.CookieClientAsync("dan");
        var raw = await dan.GetAsync($"/snippets/{id}/raw/x.html");
        Assert.Equal(HttpStatusCode.OK, raw.StatusCode);
        Assert.Equal("text/plain", raw.Content.Headers.ContentType!.MediaType);
        Assert.Equal("nosniff", raw.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("<script>alert(1)</script>", await raw.Content.ReadAsStringAsync());

        var embed = await dan.GetAsync($"/snippets/{id}/embed");
        Assert.Equal(HttpStatusCode.OK, embed.StatusCode);
        Assert.Equal("SAMEORIGIN", embed.Headers.GetValues("X-Frame-Options").Single());
        Assert.DoesNotContain("<script>alert", await embed.Content.ReadAsStringAsync());

        using var carolApi = await app.ApiClientAsync("carol");
        Assert.Equal(HttpStatusCode.Forbidden, (await carolApi.PutAsJsonAsync($"/api/v1/snippets/{id}", new { title = "mine now" })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await carolApi.PostAsJsonAsync($"/api/v1/snippets/{id}/comments", new { body = "hi" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await bob.DeleteAsync($"/api/v1/snippets/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await dan.GetAsync($"/snippets/{id}/raw/x.html")).StatusCode);
    }

    private sealed record SnippetIdLite(int Id);
}

/// <summary>The wiki repository over real git: clone as KEY/wiki.git, push pages, permissions.</summary>
public class WikiGitTests(KestrelApp app) : IClassFixture<KestrelApp>, IDisposable
{
    private readonly GitCli git = new();
    public void Dispose() => git.Dispose();

    [Fact]
    public async Task Wiki_can_be_cloned_and_pushed_over_http()
    {
        var p = await app.CreateProjectAsync("carol", ProjectVisibility.Private, ("bob", ProjectRole.Developer), ("dan", ProjectRole.Viewer));
        await using (var s = app.Scope(app.UserId("carol")))
        {
            var w = (await s.Get<WikiService>().GetWikiAsync(p.Key))!;
            await s.Get<WikiService>().SavePageAsync(w, null, "home", "Hello from the web", null, null);
        }

        var dir = git.Dir("wiki-" + Guid.NewGuid().ToString("N")[..8]);
        await git.OkAsync(null, "clone", app.GitUrl(p.Key, "wiki", "bob", SeedData.DemoPassword), dir);
        Assert.Equal("Hello from the web", (await File.ReadAllTextAsync(Path.Combine(dir, "home.md"))).Trim());

        await File.WriteAllTextAsync(Path.Combine(dir, "Pushed.md"), "Pushed with zebra stripes");
        await git.OkAsync(dir, "add", "Pushed.md");
        await git.OkAsync(dir, "commit", "-m", "Add a page from git");
        await git.OkAsync(dir, "push", "origin", "HEAD:main");

        await using (var s = app.Scope(app.UserId("dan")))
        {
            var w = (await s.Get<WikiService>().GetWikiAsync(p.Key))!;
            Assert.Equal("Pushed with zebra stripes", (await s.Get<WikiService>().GetPageAsync(w, "Pushed"))!.Content);
            Assert.Single((await s.Get<SearchService>().SearchAsync("zebra stripes")).Wiki); // reindexed after the push
        }

        // The {key}.wiki.git spelling works too; a Viewer can clone but not push.
        var viewerDir = git.Dir("wiki-viewer-" + Guid.NewGuid().ToString("N")[..8]);
        await git.OkAsync(null, "clone", app.GitUrl(p.Key, p.Key.ToLowerInvariant() + ".wiki", "dan", SeedData.DemoPassword), viewerDir);
        await File.WriteAllTextAsync(Path.Combine(viewerDir, "Nope.md"), "x");
        await git.OkAsync(viewerDir, "add", "Nope.md");
        await git.OkAsync(viewerDir, "commit", "-m", "nope");
        var push = await git.RunAsync(viewerDir, "push", "origin", "HEAD:main");
        Assert.False(push.Ok);
        Assert.Contains("403", push.Output);

        // A disabled wiki is not found.
        await using (var s = app.Scope(app.UserId("carol")))
            await s.Get<WikiService>().SetEnabledAsync(p.Id, false);
        Assert.False((await git.RunAsync(null, "ls-remote", app.GitUrl(p.Key, "wiki", "bob", SeedData.DemoPassword))).Ok);
    }
}
