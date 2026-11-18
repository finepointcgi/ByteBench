using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GitBench.Data;
using GitBench.Services;
using GitBench.Services.Explore;
using GitBench.Tests.Infrastructure;

namespace GitBench.Tests;

public class ExploreTests(TestApp app) : IClassFixture<TestApp>
{
    // ---------- Search ----------

    [Fact]
    public async Task Search_never_returns_issues_or_projects_from_a_private_project_the_user_cant_see()
    {
        var outsider = await app.CreateUserAsync("searchout1");
        var project = await app.CreateProjectAsync("carol", ProjectVisibility.Private, ("bob", ProjectRole.Developer));
        var issue = await app.CreateIssueAsync(project.Id, "carol", "Zanzibar secret rollout");

        await using (var s = app.Scope(outsider.Id))
        {
            var r = await s.Get<SearchService>().SearchAsync("zanzibar");
            Assert.Empty(r.Issues);
            Assert.Equal(0, r.IssueCount);
            Assert.Empty((await s.Get<SearchService>().SearchAsync(project.Key)).Projects);
            // The issue key doesn't jump (or leak) either.
            var jump = await s.Get<SearchService>().SearchAsync($"{project.Key}-{issue.Number}");
            Assert.Null(jump.JumpUrl);
            Assert.Empty(jump.Issues);
        }

        await using (var s = app.Scope(app.UserId("bob")))
        {
            var r = await s.Get<SearchService>().SearchAsync("zanzibar");
            Assert.Contains(r.Issues, i => i.Id == issue.Id);
            var jump = await s.Get<SearchService>().SearchAsync($"{project.Key.ToLowerInvariant()}-{issue.Number}");
            Assert.Equal($"/p/{project.Key}/issues/{issue.Number}", jump.JumpUrl);
        }

        // Same through the API.
        var client = await app.ApiClientAsync("searchout1");
        var json = await client.GetFromJsonAsync<JsonElement>("/api/v1/search?q=zanzibar");
        Assert.Equal(0, json.GetProperty("issues").GetArrayLength());
        Assert.Equal(0, json.GetProperty("issueCount").GetInt32());
        var bobClient = await app.ApiClientAsync("bob");
        var bobJson = await bobClient.GetFromJsonAsync<JsonElement>("/api/v1/search?q=zanzibar&type=issues");
        Assert.Equal(1, bobJson.GetProperty("issues").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, bobJson.GetProperty("projects").ValueKind);
    }

    [Fact]
    public async Task Search_treats_like_wildcards_literally_and_finds_users_and_pull_requests()
    {
        var project = await app.CreateProjectAsync("alice", ProjectVisibility.Private);
        await app.CreateIssueAsync(project.Id, "alice", "Fix 100% CPU in worker_pool");
        await app.CreateIssueAsync(project.Id, "alice", "Fix 100 CPU in workerXpool");
        await using var s = app.Scope(app.UserId("alice"));
        var search = s.Get<SearchService>();

        var r = await search.SearchAsync("worker_pool");
        Assert.Single(r.Issues, i => i.ProjectKey == project.Key);
        Assert.Single((await search.SearchAsync("100%")).Issues, i => i.ProjectKey == project.Key);
        Assert.Contains((await search.SearchAsync("Okafor")).Users, u => u.User.UserName == "carol");
        Assert.True((await search.SearchAsync("   ")).IsEmpty);
    }

    // ---------- Stars ----------

    [Fact]
    public async Task Stars_toggle_count_and_list_and_require_view_access()
    {
        var project = await app.CreateProjectAsync("carol", ProjectVisibility.Internal);
        var priv = await app.CreateProjectAsync("carol", ProjectVisibility.Private);
        var dan = app.UserId("dan");
        var bob = app.UserId("bob");

        await using (var s = app.Scope(dan))
        {
            var stars = s.Get<StarService>();
            Assert.Equal(1, await stars.StarAsync(project.Id));
            Assert.Equal(1, await stars.StarAsync(project.Id)); // idempotent
            Assert.True(await stars.IsStarredAsync(project.Id));
            Assert.Contains(project.Id, await stars.StarredProjectIdsAsync());
            await Assert.ThrowsAsync<ForbiddenException>(() => stars.StarAsync(priv.Id)); // can't see it
        }
        await using (var s = app.Scope(bob))
        {
            Assert.Equal(2, await s.Get<StarService>().StarAsync(project.Id));
            var explore = await s.Get<ExploreService>().ListAsync(sort: ExploreSort.Stars, tab: ExploreTab.Starred);
            var row = Assert.Single(explore, p => p.Id == project.Id);
            Assert.Equal(2, row.Stars);
            Assert.True(row.Starred);
            Assert.DoesNotContain(explore, p => p.Id == priv.Id);
        }
        await using (var s = app.Scope(dan))
        {
            Assert.Equal(1, await s.Get<StarService>().UnstarAsync(project.Id));
            Assert.DoesNotContain(project.Id, await s.Get<StarService>().StarredProjectIdsAsync());
        }

        // API: PUT / DELETE /projects/{key}/star and GET /user/starred.
        var client = await app.ApiClientAsync("dan");
        var put = await client.PutAsync($"/api/v1/projects/{project.Key}/star", null);
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var dto = await put.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(dto.GetProperty("starred").GetBoolean());
        Assert.Equal(2, dto.GetProperty("stars").GetInt32());
        var starred = await client.GetFromJsonAsync<JsonElement>("/api/v1/user/starred");
        Assert.Contains(starred.EnumerateArray(), p => p.GetProperty("key").GetString() == project.Key);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PutAsync($"/api/v1/projects/{priv.Key}/star", null)).StatusCode);
        var del = await client.DeleteAsync($"/api/v1/projects/{project.Key}/star");
        Assert.Equal(1, (await del.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("stars").GetInt32());
    }

    // ---------- Profiles ----------

    [Fact]
    public async Task Profile_feed_and_calendar_hide_projects_the_viewer_cant_see()
    {
        var viewer = await app.CreateUserAsync("profileviewer");
        var secret = await app.CreateProjectAsync("carol", ProjectVisibility.Private);
        var open = await app.CreateProjectAsync("carol", ProjectVisibility.Internal);
        var hidden = await app.CreateIssueAsync(secret.Id, "carol", "Hidden work");
        var shown = await app.CreateIssueAsync(open.Id, "carol", "Visible work");
        var carol = app.UserId("carol");
        await using (var s = app.Scope(carol))
        {
            await s.Get<IssueService>().AddCommentAsync(hidden.Id, carol, "secret comment");
            await s.Get<IssueService>().SetStatusAsync(shown.Id, IssueStatus.Done, carol);
        }

        await using (var s = app.Scope(viewer.Id))
        {
            var activity = s.Get<UserActivityService>();
            var profile = await activity.GetProfileAsync("CAROL"); // case-insensitive
            Assert.NotNull(profile);
            Assert.Contains(profile.Projects, p => p.Id == open.Id);
            Assert.DoesNotContain(profile.Projects, p => p.Id == secret.Id);

            var feed = await activity.GetFeedAsync(carol, take: 200);
            Assert.DoesNotContain(feed, f => f.ProjectKey == secret.Key);
            Assert.Contains(feed, f => f.Kind == FeedKind.IssueOpened && f.Url.EndsWith($"/p/{open.Key}/issues/{shown.Number}"));
            Assert.Contains(feed, f => f.Kind == FeedKind.IssueClosed && f.ProjectKey == open.Key);

            var before = (await activity.GetContributionsAsync(carol)).Values.Sum();
            Assert.True(before >= 2); // opened + closed in the visible project
        }

        await using (var s = app.Scope(carol))
        {
            var feed = await s.Get<UserActivityService>().GetFeedAsync(carol, take: 200);
            Assert.Contains(feed, f => f.ProjectKey == secret.Key && f.Kind == FeedKind.IssueComment);
        }

        // The API applies the same filter.
        var client = await app.ApiClientAsync("profileviewer");
        var json = await client.GetFromJsonAsync<JsonElement>("/api/v1/users/carol");
        Assert.DoesNotContain(json.GetProperty("projects").EnumerateArray(), p => p.GetProperty("key").GetString() == secret.Key);
        Assert.DoesNotContain(json.GetProperty("activity").EnumerateArray(), a => a.GetProperty("project").GetString() == secret.Key);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/v1/users/nobody-here")).StatusCode);
    }

    [Fact]
    public async Task Pushes_appear_in_the_feed_and_count_their_commits_in_the_calendar()
    {
        var project = await app.CreateProjectAsync("dan", ProjectVisibility.Private);
        var repo = await app.CreateRepoAsync(project.Id, "dan");
        var dan = app.UserId("dan");
        await using (var db = app.Db())
        {
            db.PushEvents.Add(new PushEvent
            {
                ProjectId = project.Id, RepositoryId = repo.Id, UserId = dan, Ref = "refs/heads/feature/x",
                Before = new string('a', 40), After = new string('b', 40), CommitCount = 3,
            });
            await db.SaveChangesAsync();
        }
        await using var s = app.Scope(dan);
        var activity = s.Get<UserActivityService>();
        var push = Assert.Single(await activity.GetFeedAsync(dan, [project.Id]), f => f.Kind == FeedKind.Push);
        Assert.Equal($"{repo.Slug}:feature/x", push.Title);
        Assert.Equal("3 commits", push.Detail);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        Assert.True((await activity.GetContributionsAsync(dan)).GetValueOrDefault(today) >= 3);

        // Bob can't see the project: no push in his view of Dan's feed.
        await using var b = app.Scope(app.UserId("bob"));
        Assert.DoesNotContain(await b.Get<UserActivityService>().GetFeedAsync(dan, take: 200), f => f.ProjectKey == project.Key);
    }

    [Fact]
    public async Task Push_listener_records_moved_branches_with_new_commit_counts()
    {
        var project = await app.CreateProjectAsync("alice", ProjectVisibility.Private);
        var repo = await app.CreateRepoAsync(project.Id);
        var before = app.Commit(repo, "main", "a.txt", "1");
        app.Commit(repo, "main", "a.txt", "2");
        var after = app.Commit(repo, "main", "a.txt", "3");
        var path = app.Services.GetService(typeof(RepoStorage)) is RepoStorage st ? st.PathFor(project.Key, repo.Slug) : throw new InvalidOperationException();

        var events = PushActivityListener.Describe(path,
        [
            new(before, after, "refs/heads/main"),
            new(before, new string('c', 40), "refs/heads/rejected"), // doesn't point there: skipped
        ]);
        var (pushed, commits) = Assert.Single(events);
        Assert.Equal("refs/heads/main", pushed.Ref);
        Assert.Equal(2, commits);
    }
}
