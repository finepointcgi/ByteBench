using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ByteBench.Data;
using ByteBench.Services;
using ByteBench.Tests.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

namespace ByteBench.Tests;

public class ApiTests(TestApp app) : IClassFixture<TestApp>
{
    private static StringContent Json(object body) => new(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

    private static async Task<JsonElement> ReadJson(HttpResponseMessage r) =>
        JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;

    // ---------- Authentication & scopes ----------

    [Fact]
    public async Task Anonymous_requests_get_401_with_a_bearer_challenge()
    {
        var client = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var r = await client.GetAsync("/api/v1/user");
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
        Assert.StartsWith("Bearer", r.Headers.WwwAuthenticate.ToString());
        Assert.Equal("application/problem+json", r.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Invalid_revoked_and_expired_tokens_get_401()
    {
        var client = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        async Task<HttpStatusCode> Get(string token)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "/api/v1/user");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var r = await client.SendAsync(req);
            if (r.StatusCode == HttpStatusCode.Unauthorized) Assert.Contains("invalid_token", r.Headers.WwwAuthenticate.ToString());
            return r.StatusCode;
        }

        Assert.Equal(HttpStatusCode.Unauthorized, await Get("gbp_not-a-real-token"));
        Assert.Equal(HttpStatusCode.Unauthorized, await Get("garbage"));

        var token = await app.CreateTokenAsync("bob", TokenScopes.ReadApi);
        Assert.Equal(HttpStatusCode.OK, await Get(token));

        // Expired: move the expiry into the past.
        await using (var db = app.Db())
        {
            var hash = AccessTokenService.Hash(token);
            await db.PersonalAccessTokens.Where(t => t.TokenHash == hash).ExecuteUpdateAsync(s => s.SetProperty(t => t.ExpiresAt, DateTime.UtcNow.AddMinutes(-1)));
        }
        Assert.Equal(HttpStatusCode.Unauthorized, await Get(token));

        var revoked = await app.CreateTokenAsync("bob", TokenScopes.ReadApi);
        await using (var s = app.Scope())
        {
            var hash = AccessTokenService.Hash(revoked);
            int id;
            await using (var db = app.Db()) id = await db.PersonalAccessTokens.Where(t => t.TokenHash == hash).Select(t => t.Id).FirstAsync();
            await s.Get<AccessTokenService>().RevokeAsync(app.UserId("bob"), id);
        }
        Assert.Equal(HttpStatusCode.Unauthorized, await Get(revoked));
    }

    [Fact]
    public async Task Token_scopes_gate_reads_and_writes()
    {
        var gitOnly = await app.ApiClientAsync("bob", TokenScopes.ReadRepository | TokenScopes.WriteRepository);
        var r = await gitOnly.GetAsync("/api/v1/user");
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);

        var readOnly = await app.ApiClientAsync("bob", TokenScopes.ReadApi);
        Assert.Equal(HttpStatusCode.OK, (await readOnly.GetAsync("/api/v1/projects/WEB/issues")).StatusCode);
        var write = await readOnly.PostAsync("/api/v1/projects/WEB/issues", Json(new { title = "nope" }));
        Assert.Equal(HttpStatusCode.Forbidden, write.StatusCode);
        Assert.Contains("write_api", (await ReadJson(write)).GetProperty("detail").GetString());

        var me = await ReadJson(await readOnly.GetAsync("/api/v1/user"));
        Assert.Equal("bob", me.GetProperty("username").GetString());
        Assert.Equal("read_api", Assert.Single(me.GetProperty("scopes").EnumerateArray()).GetString());
    }

    // ---------- Permissions: 404 vs 403 ----------

    [Fact]
    public async Task Invisible_project_is_404_and_missing_permission_is_403()
    {
        var bob = await app.ApiClientAsync("bob");
        // OPS is Private and bob isn't a member: indistinguishable from a project that doesn't exist.
        var hidden = await bob.GetAsync("/api/v1/projects/OPS");
        var missing = await bob.GetAsync("/api/v1/projects/NOPE");
        Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await bob.GetAsync("/api/v1/projects/OPS/issues/1")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await bob.PostAsync("/api/v1/projects/OPS/issues", Json(new { title = "x" }))).StatusCode);

        // An Internal project gives an outsider Viewer: reading works, writing is 403.
        await app.CreateUserAsync("apiviewer");
        var viewer = await app.ApiClientAsync("apiviewer");
        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync("/api/v1/projects/WEB")).StatusCode);
        var create = await viewer.PostAsync("/api/v1/projects/WEB/issues", Json(new { title = "x" }));
        Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);
    }

    // ---------- CSRF rule for cookie sessions ----------

    [Fact]
    public async Task Cookie_writes_need_json_or_x_requested_with()
    {
        var project = await app.CreateProjectAsync("carol");
        var client = await app.CookieClientAsync("carol");
        var url = $"/api/v1/projects/{project.Key}/issues";

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(url)).StatusCode); // reads are fine

        // What a cross-site HTML form can send: refused before anything else.
        var form = await client.PostAsync(url, new FormUrlEncodedContent(new Dictionary<string, string> { ["title"] = "csrf" }));
        Assert.Equal(HttpStatusCode.Forbidden, form.StatusCode);
        Assert.Contains("Cookie-authenticated writes", (await ReadJson(form)).GetProperty("detail").GetString());

        var ok = await client.PostAsync(url, Json(new { title = "from the browser" }));
        Assert.Equal(HttpStatusCode.Created, ok.StatusCode);

        // A custom header passes the CSRF check (and the non-JSON body is then a 415, not a 403).
        using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent("title=x", Encoding.UTF8, "text/plain") };
        req.Headers.Add("X-Requested-With", "XMLHttpRequest");
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await client.SendAsync(req)).StatusCode);

        // A bearer token is not subject to the rule (it can't be sent by a cross-site form).
        await using var db = app.Db();
        Assert.Equal(0, await db.Issues.CountAsync(i => i.ProjectId == project.Id && i.Title == "csrf"));
    }

    // ---------- Pagination ----------

    private static Dictionary<string, string> ParseLinks(string header) =>
        System.Text.RegularExpressions.Regex.Matches(header, "<([^>]+)>; rel=\"([a-z]+)\"")
            .ToDictionary(m => m.Groups[2].Value, m => m.Groups[1].Value);

    private static (string Page, string PerPage) PageOf(string url)
    {
        var q = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(new Uri(url).Query);
        return (q["page"].ToString(), q["perPage"].ToString());
    }

    [Fact]
    public async Task Lists_are_paginated_with_total_count_and_link_headers()
    {
        var project = await app.CreateProjectAsync("carol");
        for (var i = 1; i <= 5; i++) await app.CreateIssueAsync(project.Id, "carol", $"Issue {i}");
        var client = await app.ApiClientAsync("carol");

        var r = await client.GetAsync($"/api/v1/projects/{project.Key}/issues?perPage=2&page=2&sort=number");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("5", r.Headers.GetValues("X-Total-Count").Single());
        var links = ParseLinks(r.Headers.GetValues("Link").Single());
        Assert.Equal(("3", "2"), PageOf(links["next"]));
        Assert.Equal(("1", "2"), PageOf(links["prev"]));
        Assert.Equal(("1", "2"), PageOf(links["first"]));
        Assert.Equal(("3", "2"), PageOf(links["last"]));
        Assert.Contains("sort=number", links["next"]); // other query parameters are kept
        Assert.Equal(2, (await ReadJson(r)).GetArrayLength());

        var last = await client.GetAsync($"/api/v1/projects/{project.Key}/issues?perPage=2&page=3");
        Assert.Single((await ReadJson(last)).EnumerateArray());
        Assert.DoesNotContain("rel=\"next\"", last.Headers.GetValues("Link").Single());

        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"/api/v1/projects/{project.Key}/issues?perPage=101")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"/api/v1/projects/{project.Key}/issues?page=0")).StatusCode);
    }

    // ---------- Issue CRUD ----------

    [Fact]
    public async Task Issue_create_read_update_and_comment()
    {
        var project = await app.CreateProjectAsync("carol", ProjectVisibility.Private, ("dan", ProjectRole.Developer));
        var client = await app.ApiClientAsync("dan");
        var baseUrl = $"/api/v1/projects/{project.Key}/issues";

        var created = await client.PostAsync(baseUrl, Json(new
        {
            title = "Crash on save", description = "Steps…", type = "bug", priority = "high",
            assignee = "carol", labels = new[] { "bug" }, storyPoints = 3, dueDate = "2030-01-31",
        }));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var issue = await ReadJson(created);
        var number = issue.GetProperty("number").GetInt32();
        Assert.Equal($"{project.Key}-{number}", issue.GetProperty("key").GetString());
        Assert.Equal("bug", issue.GetProperty("type").GetString());
        Assert.Equal("carol", issue.GetProperty("assignee").GetProperty("username").GetString());
        Assert.Equal("2030-01-31", issue.GetProperty("dueDate").GetString());
        Assert.EndsWith($"{baseUrl}/{number}", created.Headers.Location!.ToString());

        // PATCH changes only what is sent; null clears.
        var patched = await client.PatchAsync($"{baseUrl}/{number}", Json(new { title = "Crash on save (Safari)", assignee = (string?)null }));
        Assert.Equal(HttpStatusCode.OK, patched.StatusCode);
        var after = await ReadJson(await client.GetAsync($"{baseUrl}/{number}"));
        Assert.Equal("Crash on save (Safari)", after.GetProperty("title").GetString());
        Assert.Equal(JsonValueKind.Null, after.GetProperty("assignee").ValueKind);
        Assert.Equal(3, after.GetProperty("storyPoints").GetInt32());

        var comment = await client.PostAsync($"{baseUrl}/{number}/comments", Json(new { body = "On it." }));
        Assert.Equal(HttpStatusCode.Created, comment.StatusCode);
        var comments = await ReadJson(await client.GetAsync($"{baseUrl}/{number}/comments"));
        Assert.Equal("On it.", Assert.Single(comments.EnumerateArray()).GetProperty("body").GetString());

        // Validation errors are problem details.
        var bad = await client.PostAsync(baseUrl, Json(new { title = "" }));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, bad.StatusCode);
        var badJson = await client.PostAsync(baseUrl, new StringContent("{not json", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, badJson.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"{baseUrl}/9999")).StatusCode);
    }

    // ---------- Pull requests and the merge gate ----------

    [Fact]
    public async Task Pull_request_merge_is_blocked_by_branch_protection_until_approved()
    {
        var project = await app.CreateProjectAsync("carol", ProjectVisibility.Private,
            ("bob", ProjectRole.Developer), ("dan", ProjectRole.Maintainer));
        var repo = await app.CreateRepoAsync(project.Id, "carol");
        app.Commit(repo, "feature/x", "x.txt", "hello");
        await using (var s = app.Scope(app.UserId("carol")))
            await s.Get<BranchProtectionService>().SaveAsync(repo.Id, new BranchProtectionRule { Pattern = "main", RequiredApprovals = 1 });

        var bob = await app.ApiClientAsync("bob");
        var prs = $"/api/v1/projects/{project.Key}/repos/{repo.Slug}/pull-requests";
        var created = await bob.PostAsync(prs, Json(new { title = "Add x", sourceBranch = "feature/x", targetBranch = "main" }));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var number = (await ReadJson(created)).GetProperty("number").GetInt32();

        // A Developer may not merge at all.
        Assert.Equal(HttpStatusCode.Forbidden, (await bob.PostAsync($"{prs}/{number}/merge", Json(new { }))).StatusCode);

        // A Maintainer is blocked by the rule: 1 approval required, 0 given.
        var dan = await app.ApiClientAsync("dan");
        var blocked = await dan.PostAsync($"{prs}/{number}/merge", Json(new { }));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, blocked.StatusCode);
        Assert.Contains("needs 1 approval", (await ReadJson(blocked)).GetProperty("detail").GetString());

        // The author's own approval doesn't count…
        await using (var s = app.Scope(app.UserId("bob")))
        {
            var prId = (await s.Get<PullRequestService>().GetAsync(project.Key, repo.Slug, number))!.Id;
            await Assert.ThrowsAnyAsync<InvalidOperationException>(() => s.Get<PullRequestService>().SetReviewAsync(prId, app.UserId("bob"), ReviewState.Approved));
        }
        // …someone else's does.
        await using (var s = app.Scope(app.UserId("carol")))
        {
            var prId = (await s.Get<PullRequestService>().GetAsync(project.Key, repo.Slug, number))!.Id;
            await s.Get<PullRequestService>().SetReviewAsync(prId, app.UserId("carol"), ReviewState.Approved);
        }
        var merged = await dan.PostAsync($"{prs}/{number}/merge", Json(new { }));
        Assert.Equal(HttpStatusCode.OK, merged.StatusCode);
        var result = await ReadJson(merged);
        Assert.True(result.GetProperty("merged").GetBoolean());
        Assert.Equal("merged", result.GetProperty("pullRequest").GetProperty("state").GetString());

        // Merging again is a 409.
        Assert.Equal(HttpStatusCode.Conflict, (await dan.PostAsync($"{prs}/{number}/merge", Json(new { }))).StatusCode);
    }

    [Fact]
    public async Task Changes_requested_blocks_merge_even_for_an_admin_without_bypass()
    {
        var project = await app.CreateProjectAsync("carol", ProjectVisibility.Private, ("bob", ProjectRole.Developer), ("dan", ProjectRole.Developer));
        var repo = await app.CreateRepoAsync(project.Id, "carol");
        app.Commit(repo, "feature/y", "y.txt", "y");
        int prId;
        await using (var s = app.Scope(app.UserId("carol")))
        {
            await s.Get<BranchProtectionService>().SaveAsync(repo.Id, new BranchProtectionRule { Pattern = "main" });
            var pr = await s.Get<PullRequestService>().CreateAsync(repo, "Y", null, "feature/y", "main", app.UserId("bob"));
            prId = pr.Id;
            await s.Get<PullRequestService>().SetReviewAsync(prId, app.UserId("dan"), ReviewState.ChangesRequested);
        }
        var alice = await app.ApiClientAsync("alice");
        var r = await alice.PostAsync($"/api/v1/projects/{project.Key}/repos/{repo.Slug}/pull-requests/1/merge", Json(new { }));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode);
        Assert.Contains("changes were requested", (await ReadJson(r)).GetProperty("detail").GetString());
    }
}
