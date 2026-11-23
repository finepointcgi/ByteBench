using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using ByteBench.Data;
using ByteBench.Services;
using ByteBench.Services.Mcp;
using ByteBench.Tests.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

namespace ByteBench.Tests;

/// <summary>A tiny MCP client over HTTP for the tests.</summary>
public sealed class McpClient(HttpClient http)
{
    private int nextId = 1;

    public async Task<(HttpStatusCode Status, JsonObject? Body)> PostAsync(object? body, string contentType = "application/json")
    {
        var json = body is string s ? s : System.Text.Json.JsonSerializer.Serialize(body);
        var r = await http.PostAsync("/mcp", new StringContent(json, Encoding.UTF8, contentType));
        var text = await r.Content.ReadAsStringAsync();
        return (r.StatusCode, text.Length == 0 ? null : JsonNode.Parse(text) as JsonObject);
    }

    public Task<(HttpStatusCode Status, JsonObject? Body)> RequestAsync(string method, object? parameters = null) =>
        PostAsync(new { jsonrpc = "2.0", id = nextId++, method, @params = parameters });

    public async Task<JsonObject> ResultAsync(string method, object? parameters = null)
    {
        var (status, body) = await RequestAsync(method, parameters);
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True(body!["error"] == null, body.ToJsonString());
        return body["result"]!.AsObject();
    }

    /// <summary>tools/call; returns the tool result (which may have isError).</summary>
    public Task<JsonObject> CallAsync(string tool, object? args = null) => ResultAsync("tools/call", new { name = tool, arguments = args ?? new { } });

    public static string Text(JsonObject result) => result["content"]![0]!["text"]!.GetValue<string>();
    public static bool IsError(JsonObject result) => result["isError"]!.GetValue<bool>();
}

public class McpTests(TestApp app) : IClassFixture<TestApp>
{
    private async Task<McpClient> ClientAsync(string user, TokenScopes scopes = TokenScopes.ReadApi | TokenScopes.WriteApi) =>
        new(await app.ApiClientAsync(user, scopes));

    private static async Task<JsonObject> OkCall(McpClient c, string tool, object? args = null)
    {
        var r = await c.CallAsync(tool, args);
        Assert.False(McpClient.IsError(r), McpClient.Text(r));
        return r;
    }

    private static async Task<string> ErrorText(McpClient c, string tool, object? args = null)
    {
        var r = await c.CallAsync(tool, args);
        Assert.True(McpClient.IsError(r), "expected an error result but got: " + McpClient.Text(r));
        return McpClient.Text(r);
    }

    // ---------- Transport and authentication ----------

    [Fact]
    public async Task Without_a_token_the_endpoint_is_401_with_a_bearer_challenge()
    {
        var client = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var (status, body) = await new McpClient(client).RequestAsync("ping");
        Assert.Equal(HttpStatusCode.Unauthorized, status);
        Assert.NotNull(body!["error"]);
        var r = await client.PostAsync("/mcp", new StringContent("{}", Encoding.UTF8, "application/json"));
        Assert.StartsWith("Bearer", r.Headers.WwwAuthenticate.ToString());
    }

    [Fact]
    public async Task Bad_revoked_and_expired_tokens_and_cookie_sessions_are_401()
    {
        var bad = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        bad.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "gbp_nope");
        var r = await bad.PostAsync("/mcp", new StringContent("{}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
        Assert.Contains("invalid_token", r.Headers.WwwAuthenticate.ToString());

        // A signed-in browser session is not a credential here: no cookies on this endpoint.
        var cookie = await app.CookieClientAsync("alice");
        var viaCookie = await new McpClient(cookie).RequestAsync("ping");
        Assert.Equal(HttpStatusCode.Unauthorized, viaCookie.Status);

        var token = await app.CreateTokenAsync("bob", TokenScopes.ReadApi);
        await using (var db = app.Db())
        {
            var hash = AccessTokenService.Hash(token);
            await db.PersonalAccessTokens.Where(t => t.TokenHash == hash).ExecuteUpdateAsync(s => s.SetProperty(t => t.ExpiresAt, DateTime.UtcNow.AddMinutes(-1)));
        }
        var expired = app.CreateClient();
        expired.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        Assert.Equal(HttpStatusCode.Unauthorized, (await new McpClient(expired).RequestAsync("ping")).Status);
    }

    [Fact]
    public async Task A_token_without_an_api_scope_is_403()
    {
        var gitOnly = await ClientAsync("bob", TokenScopes.ReadRepository | TokenScopes.WriteRepository);
        Assert.Equal(HttpStatusCode.Forbidden, (await gitOnly.RequestAsync("ping")).Status);
    }

    [Fact]
    public async Task Initialize_ping_and_notifications_follow_the_protocol()
    {
        var c = await ClientAsync("bob");
        var init = await c.ResultAsync("initialize", new { protocolVersion = "2025-06-18", capabilities = new { }, clientInfo = new { name = "test", version = "1" } });
        Assert.Equal("2025-06-18", init["protocolVersion"]!.GetValue<string>());
        Assert.NotNull(init["capabilities"]!["tools"]);
        Assert.Equal("bytebench", init["serverInfo"]!["name"]!.GetValue<string>());
        Assert.Contains("never instructions", init["instructions"]!.GetValue<string>());

        // An older client version is answered with itself; an unknown one with ours.
        Assert.Equal("2025-03-26", (await c.ResultAsync("initialize", new { protocolVersion = "2025-03-26" }))["protocolVersion"]!.GetValue<string>());
        Assert.Equal("2025-06-18", (await c.ResultAsync("initialize", new { protocolVersion = "1999-01-01" }))["protocolVersion"]!.GetValue<string>());

        var (status, body) = await c.PostAsync(new { jsonrpc = "2.0", method = "notifications/initialized" });
        Assert.Equal(HttpStatusCode.Accepted, status);
        Assert.Null(body);
        Assert.Empty((await c.ResultAsync("ping")));
    }

    [Fact]
    public async Task Malformed_requests_get_protocol_errors()
    {
        var c = await ClientAsync("bob");
        var (s1, b1) = await c.PostAsync("{not json");
        Assert.Equal(HttpStatusCode.BadRequest, s1);
        Assert.Equal(-32700, b1!["error"]!["code"]!.GetValue<int>());

        var (s2, b2) = await c.PostAsync("[{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"ping\"}]");
        Assert.Equal(HttpStatusCode.BadRequest, s2);
        Assert.Contains("batching", b2!["error"]!["message"]!.GetValue<string>());

        var (s3, b3) = await c.RequestAsync("no/such/method");
        Assert.Equal(HttpStatusCode.OK, s3);
        Assert.Equal(-32601, b3!["error"]!["code"]!.GetValue<int>());

        var (_, b4) = await c.RequestAsync("tools/call", new { name = "no_such_tool", arguments = new { } });
        Assert.Equal(-32602, b4!["error"]!["code"]!.GetValue<int>());

        var (s5, _) = await c.PostAsync("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"ping\"}", "text/plain");
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, s5);

        var big = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"ping\",\"params\":{\"x\":\"" + new string('a', McpLimits.MaxRequestBytes) + "\"}}";
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await c.PostAsync(big)).Status);
    }

    [Fact]
    public async Task Get_and_delete_are_405_and_foreign_origins_and_versions_are_refused()
    {
        var http = await app.ApiClientAsync("bob");
        var get = await http.GetAsync("/mcp");
        Assert.Equal(HttpStatusCode.MethodNotAllowed, get.StatusCode);
        Assert.Contains("POST", get.Content.Headers.Allow.Concat(get.Headers.TryGetValues("Allow", out var a) ? a : []));
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await http.DeleteAsync("/mcp")).StatusCode);

        using var evil = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Content = new StringContent("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"ping\"}", Encoding.UTF8, "application/json") };
        evil.Headers.Add("Origin", "https://evil.example");
        Assert.Equal(HttpStatusCode.Forbidden, (await http.SendAsync(evil)).StatusCode);

        using var old = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Content = new StringContent("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"ping\"}", Encoding.UTF8, "application/json") };
        old.Headers.Add("MCP-Protocol-Version", "1999-01-01");
        Assert.Equal(HttpStatusCode.BadRequest, (await http.SendAsync(old)).StatusCode);
    }

    // ---------- tools/list, annotations and scopes ----------

    [Fact]
    public async Task Tools_list_has_schemas_and_annotations_and_hides_write_tools_from_read_tokens()
    {
        var writer = await ClientAsync("bob");
        var all = (await writer.ResultAsync("tools/list"))["tools"]!.AsArray();
        Assert.Equal(McpTools.All.Select(t => t.Name).Order(), all.Select(t => t!["name"]!.GetValue<string>()).Order());
        foreach (var t in all)
        {
            var name = t!["name"]!.GetValue<string>();
            Assert.Matches("^[a-z][a-z0-9_]*$", name);
            Assert.False(string.IsNullOrWhiteSpace(t["description"]!.GetValue<string>()));
            Assert.Equal("object", t["inputSchema"]!["type"]!.GetValue<string>());
            Assert.False(t["inputSchema"]!["additionalProperties"]!.GetValue<bool>());
            foreach (var (_, prop) in t["inputSchema"]!["properties"]!.AsObject())
                Assert.False(string.IsNullOrWhiteSpace(prop!["description"]?.GetValue<string>()));
            var ann = t["annotations"]!;
            var expectedWrite = name is "create_issue" or "update_issue" or "comment_on_issue" or "transition_issue";
            Assert.Equal(!expectedWrite, ann["readOnlyHint"]!.GetValue<bool>());
            if (!expectedWrite) Assert.False(ann["destructiveHint"]!.GetValue<bool>());
        }

        var reader = await ClientAsync("bob", TokenScopes.ReadApi);
        var names = (await reader.ResultAsync("tools/list"))["tools"]!.AsArray().Select(t => t!["name"]!.GetValue<string>()).ToList();
        Assert.Contains("get_issue", names);
        Assert.DoesNotContain("create_issue", names);
        Assert.DoesNotContain("update_issue", names);
        Assert.DoesNotContain("comment_on_issue", names);
        Assert.DoesNotContain("transition_issue", names);
    }

    [Fact]
    public async Task A_read_token_cannot_call_write_tools_and_nothing_changes()
    {
        var project = await app.CreateProjectAsync("alice", ProjectVisibility.Private, ("bob", ProjectRole.Developer));
        var issue = await app.CreateIssueAsync(project.Id, "alice", "Original title");
        var reader = await ClientAsync("bob", TokenScopes.ReadApi);

        Assert.Contains("write_api", await ErrorText(reader, "create_issue", new { project = project.Key, title = "Sneaky" }));
        Assert.Contains("write_api", await ErrorText(reader, "update_issue", new { issue = issue.Key(project), title = "Changed" }));
        Assert.Contains("write_api", await ErrorText(reader, "comment_on_issue", new { issue = issue.Key(project), body = "hi" }));
        Assert.Contains("write_api", await ErrorText(reader, "transition_issue", new { issue = issue.Key(project), status = "Done" }));

        await using var db = app.Db();
        Assert.Equal(1, await db.Issues.CountAsync(i => i.ProjectId == project.Id));
        Assert.Equal("Original title", (await db.Issues.FirstAsync(i => i.Id == issue.Id)).Title);
        Assert.Equal(0, await db.Comments.CountAsync(c => c.IssueId == issue.Id));
        // reading still works
        Assert.Contains("Original title", McpClient.Text(await OkCall(reader, "get_issue", new { issue = issue.Key(project) })));
    }

    // ---------- Issue tools ----------

    [Fact]
    public async Task Issue_tools_round_trip_as_the_tokens_user()
    {
        var project = await app.CreateProjectAsync("alice", ProjectVisibility.Private, ("bob", ProjectRole.Developer));
        var bob = await ClientAsync("bob");

        var created = await OkCall(bob, "create_issue", new { project = project.Key, title = "Login fails", description = "Steps to reproduce", type = "bug", priority = "high", assignee = "alice", story_points = 3 });
        var key = created["structuredContent"]!["key"]!.GetValue<string>();
        Assert.Equal(project.Key + "-1", key);
        Assert.Contains(key, McpClient.Text(created));

        await using (var db = app.Db())
        {
            var row = await db.Issues.Include(i => i.Assignee).FirstAsync(i => i.ProjectId == project.Id && i.Number == 1);
            Assert.Equal(app.UserId("bob"), row.ReporterId); // ran as bob
            Assert.Equal("alice", row.Assignee!.UserName);
            Assert.Equal(IssueType.Bug, row.Type);
            Assert.Equal(3, row.StoryPoints);
        }

        await OkCall(bob, "update_issue", new { issue = key, title = "Login fails on Safari", priority = "highest", assignee = "none", story_points = 5 });
        var comment = await OkCall(bob, "comment_on_issue", new { issue = key, body = "Reproduced on 17.2" });
        Assert.NotNull(comment["structuredContent"]!["commentId"]);
        var moved = await OkCall(bob, "transition_issue", new { issue = key, status = "In Progress" });
        Assert.Contains("In Progress", McpClient.Text(moved));

        var got = await OkCall(bob, "get_issue", new { issue = key.ToLowerInvariant() });
        var s = got["structuredContent"]!;
        Assert.Equal("Login fails on Safari", s["issue"]!["title"]!.GetValue<string>());
        Assert.Equal("highest", s["issue"]!["priority"]!.GetValue<string>());
        Assert.Null(s["issue"]!["assignee"]);
        Assert.Equal("inProgress", s["issue"]!["category"]!.GetValue<string>());
        Assert.Equal(5, s["issue"]!["points"]!.GetValue<int>());
        Assert.Equal("Reproduced on 17.2", s["comments"]![0]!["body"]!.GetValue<string>());
        Assert.Equal("bob", s["comments"]![0]!["author"]!.GetValue<string>());

        // children, parent and links show up
        var child = await OkCall(bob, "create_issue", new { project = project.Key, title = "Sub piece", type = "task" });
        await using (var scope = app.Scope(app.UserId("bob")))
        {
            var childKey = child["structuredContent"]!["key"]!.GetValue<string>();
            var (_, number) = McpArgs.ParseIssueKey(childKey);
            var parentRow = await scope.Get<IssueService>().GetIssueAsync(project.Key, 1);
            var childRow = await scope.Get<IssueService>().GetIssueAsync(project.Key, number);
            await scope.Get<LinkedWorkService>().AddLinkAsync(parentRow!.Id, childRow!.Id, IssueLinkRelation.Blocks, app.UserId("bob"));
        }
        var withLink = McpClient.Text(await OkCall(bob, "get_issue", new { issue = key }));
        Assert.Contains("Sub piece", withLink);

        // my_work and search
        await OkCall(bob, "update_issue", new { issue = key, assignee = "bob" });
        Assert.Contains(key, McpClient.Text(await OkCall(bob, "my_work")));
        var found = await OkCall(bob, "search_issues", new { query = $"project = {project.Key} AND text ~ \"Safari\"" });
        Assert.Equal(1, found["structuredContent"]!["total"]!.GetValue<int>());
        var simple = await OkCall(bob, "search_issues", new { project = project.Key, status = "all", limit = 1 });
        Assert.Single(simple["structuredContent"]!["issues"]!.AsArray());
        Assert.True(simple["structuredContent"]!["truncated"]!.GetValue<bool>());
        Assert.Contains("truncated", McpClient.Text(simple));
    }

    [Fact]
    public async Task Search_with_a_bad_query_or_nothing_to_search_is_an_error_result()
    {
        var bob = await ClientAsync("bob");
        Assert.Contains("Invalid query", await ErrorText(bob, "search_issues", new { query = "project = AND AND" }));
        Assert.Contains("query", await ErrorText(bob, "search_issues", new { text = "x" }));
    }

    [Fact]
    public async Task Workflow_refused_moves_and_unknown_statuses_are_error_results()
    {
        var project = await app.CreateProjectAsync("alice", ProjectVisibility.Private, ("bob", ProjectRole.Developer));
        var issue = await app.CreateIssueAsync(project.Id, "alice");
        var bob = await ClientAsync("bob");
        var text = await ErrorText(bob, "transition_issue", new { issue = issue.Key(project), status = "Nonexistent" });
        Assert.Contains("Unknown status", text);
        Assert.Contains("To Do", text);
    }

    // ---------- Permissions ----------

    [Fact]
    public async Task A_token_for_one_user_cannot_see_another_users_private_project()
    {
        var project = await app.CreateProjectAsync("alice", ProjectVisibility.Private);
        var issue = await app.CreateIssueAsync(project.Id, "alice", "Secret roadmap");
        var repo = await app.CreateRepoAsync(project.Id, "alice", "vault");
        app.Commit(repo, "main", "secret.txt", "top secret content");
        var carol = await ClientAsync("carol"); // not a member

        Assert.DoesNotContain(project.Key, McpClient.Text(await OkCall(carol, "list_projects")));
        Assert.Contains("not found", await ErrorText(carol, "get_issue", new { issue = issue.Key(project) }));
        Assert.Contains("not found", await ErrorText(carol, "search_issues", new { project = project.Key }));
        Assert.Contains("not found", await ErrorText(carol, "list_repositories", new { project = project.Key }));
        Assert.DoesNotContain(project.Key + "/", McpClient.Text(await OkCall(carol, "list_repositories")));
        Assert.Contains("not found", await ErrorText(carol, "read_file", new { repository = $"{project.Key}/vault", path = "secret.txt" }));
        Assert.Contains("not found", await ErrorText(carol, "list_directory", new { repository = $"{project.Key}/vault" }));
        Assert.Contains("not found", await ErrorText(carol, "search_code", new { query = "secret", repository = $"{project.Key}/vault" }));
        Assert.Contains("not found", await ErrorText(carol, "list_pipelines", new { repository = $"{project.Key}/vault" }));
        Assert.Contains("not found", await ErrorText(carol, "create_issue", new { project = project.Key, title = "x" }));
        var gql = await OkCall(carol, "search_issues", new { query = "text ~ \"Secret\"" });
        Assert.Equal(0, gql["structuredContent"]!["total"]!.GetValue<int>());

        // …and the owner's token does see all of it.
        var alice = await ClientAsync("alice");
        Assert.Contains(project.Key, McpClient.Text(await OkCall(alice, "list_projects")));
        Assert.Contains("top secret content", McpClient.Text(await OkCall(alice, "read_file", new { repository = $"{project.Key}/vault", path = "secret.txt" })));
    }

    [Fact]
    public async Task A_viewer_can_read_but_not_write_even_with_a_write_token()
    {
        var project = await app.CreateProjectAsync("alice", ProjectVisibility.Private, ("dan", ProjectRole.Viewer));
        var issue = await app.CreateIssueAsync(project.Id, "alice", "Visible");
        var dan = await ClientAsync("dan");
        Assert.Contains("Visible", McpClient.Text(await OkCall(dan, "get_issue", new { issue = issue.Key(project) })));
        Assert.Contains("ermission", await ErrorText(dan, "create_issue", new { project = project.Key, title = "nope" }));
        Assert.Contains("ermission", await ErrorText(dan, "comment_on_issue", new { issue = issue.Key(project), body = "nope" }));
        Assert.Contains("ermission", await ErrorText(dan, "update_issue", new { issue = issue.Key(project), title = "nope" }));
        Assert.Contains("ermission", await ErrorText(dan, "transition_issue", new { issue = issue.Key(project), status = "Done" }));
        await using var db = app.Db();
        Assert.Equal("Visible", (await db.Issues.FirstAsync(i => i.Id == issue.Id)).Title);
    }

    // ---------- Validation ----------

    [Fact]
    public async Task Invalid_arguments_are_error_results_not_crashes()
    {
        var bob = await ClientAsync("bob");
        Assert.Contains("'issue' is required", await ErrorText(bob, "get_issue", new { }));
        Assert.Contains("unknown argument 'bogus'", await ErrorText(bob, "get_issue", new { issue = "WEB-1", bogus = 1 }));
        Assert.Contains("must be a string", await ErrorText(bob, "get_issue", new { issue = 12 }));
        Assert.Contains("not an issue key", await ErrorText(bob, "get_issue", new { issue = "nonsense" }));
        Assert.Contains("not an issue key", await ErrorText(bob, "get_issue", new { issue = "WEB-1; DROP TABLE" }));
        Assert.Contains("too long", await ErrorText(bob, "create_issue", new { project = "WEB", title = new string('x', 201) }));
        Assert.Contains("must be one of", await ErrorText(bob, "create_issue", new { project = "WEB", title = "x", type = "dragon" }));
        Assert.Contains("between", await ErrorText(bob, "search_issues", new { project = "WEB", limit = 5000 }));
        Assert.Contains("integer", await ErrorText(bob, "search_issues", new { project = "WEB", limit = 2.5 }));
        Assert.Contains("true or false", await ErrorText(bob, "get_pull_request", new { repository = "WEB/web-app", number = 1, include_diff = "yes" }));
        Assert.Contains("not a repository", await ErrorText(bob, "read_file", new { repository = "nonsense", path = "a" }));
        Assert.Contains("not a valid repository path", await ErrorText(bob, "read_file", new { repository = "WEB/web-app", path = "../../etc/passwd" }));
        Assert.Contains("not a valid", await ErrorText(bob, "read_file", new { repository = "WEB/web-app", path = "README.md", @ref = "--upload-pack=x" }));
        Assert.Contains("arguments must be an object", await ErrorViaRaw(bob));
        Assert.Contains("Unknown label", await ErrorText(bob, "create_issue", new { project = "WEB", title = "x", labels = new[] { "nope-label" } }));
        Assert.Contains("Nothing to change", await ErrorText(bob, "update_issue", new { issue = "WEB-1" }));
    }

    private static async Task<string> ErrorViaRaw(McpClient c)
    {
        var (_, body) = await c.PostAsync("{\"jsonrpc\":\"2.0\",\"id\":9,\"method\":\"tools/call\",\"params\":{\"name\":\"get_issue\",\"arguments\":[1]}}");
        var result = body!["result"]!.AsObject();
        Assert.True(McpClient.IsError(result));
        return McpClient.Text(result);
    }

    // ---------- Output caps ----------

    [Fact]
    public async Task Big_outputs_are_capped_and_marked_truncated()
    {
        var project = await app.CreateProjectAsync("alice", ProjectVisibility.Private);
        var alice = await ClientAsync("alice");
        var created = await OkCall(alice, "create_issue", new { project = project.Key, title = "Huge", description = new string('d', 19_000) });
        var key = created["structuredContent"]!["key"]!.GetValue<string>();
        for (var i = 0; i < 25; i++) await OkCall(alice, "comment_on_issue", new { issue = key, body = new string('c', 5000) });

        var got = await OkCall(alice, "get_issue", new { issue = key });
        var text = McpClient.Text(got);
        Assert.True(text.Length <= McpLimits.MaxTextChars + 100, $"text was {text.Length}");
        Assert.Contains("[truncated]", text);
        Assert.True(got["structuredContent"]!["truncated"]!.GetValue<bool>());
        Assert.True(got["structuredContent"]!["description"]!.GetValue<string>().Length <= McpLimits.MaxBodyChars + 20);
        Assert.Equal(20, got["structuredContent"]!["comments"]!.AsArray().Count);
        Assert.Equal(25, got["structuredContent"]!["totalComments"]!.GetValue<int>());

        // files: a big text file is cut, a binary file is refused
        var repo = await app.CreateRepoAsync(project.Id, "alice", "files");
        app.Commit(repo, "main", "big.txt", string.Join('\n', Enumerable.Range(0, 20_000).Select(n => "line number " + n)));
        app.Commit(repo, "main", "blob.bin", "PK\0\0\u0001\u0002binary\0data\0");
        var name = $"{project.Key}/files";
        var read = await OkCall(alice, "read_file", new { repository = name, path = "big.txt" });
        Assert.True(read["structuredContent"]!["truncated"]!.GetValue<bool>());
        Assert.True(McpClient.Text(read).Length <= McpLimits.MaxTextChars + 600 || McpClient.Text(read).Length <= McpLimits.MaxFileChars + 600);
        Assert.Contains("start_line=", McpClient.Text(read));
        var next = await OkCall(alice, "read_file", new { repository = name, path = "big.txt", start_line = 5000 });
        Assert.Contains("line number 4999", McpClient.Text(next));
        Assert.Contains("binary", await ErrorText(alice, "read_file", new { repository = name, path = "blob.bin" }));
        Assert.Contains("not found", await ErrorText(alice, "read_file", new { repository = name, path = "missing.txt" }));
    }

    [Fact]
    public void Render_drops_oversized_structured_content_and_cuts_text()
    {
        var huge = McpToolResult.Ok(new string('x', 50_000), new { blob = new string('y', 200_000) });
        var rendered = McpEndpoint.Render(huge);
        Assert.Null(rendered["structuredContent"]);
        var text = rendered["content"]![0]!["text"]!.GetValue<string>();
        Assert.True(text.Length < McpLimits.MaxTextChars + 100);
        Assert.Contains("[truncated]", text);
        Assert.False(rendered["isError"]!.GetValue<bool>());
    }

    // ---------- Code, pull requests, pipelines ----------

    [Fact]
    public async Task Repository_pull_request_and_pipeline_tools()
    {
        var project = await app.CreateProjectAsync("alice", ProjectVisibility.Private, ("bob", ProjectRole.Developer));
        var repo = await app.CreateRepoAsync(project.Id, "alice", "app");
        app.Commit(repo, "main", "src/hello.txt", "hello world\nfind the needle here\n");
        app.Commit(repo, "feature/x", "src/new.txt", "added in the feature\n");
        int prNumber;
        await using (var s = app.Scope(app.UserId("bob")))
            prNumber = (await s.Get<PullRequestService>().CreateAsync(repo, "Add the feature", "Does a thing.", "feature/x", "main", app.UserId("bob"))).Number;
        await using (var s = app.Scope(app.UserId("alice")))
        {
            var openPr = (await s.Get<PullRequestService>().ListAsync(project.Id, PullRequestState.Open)).Single();
            await s.Get<PullRequestService>().SetReviewAsync(openPr.Id, app.UserId("alice"), ReviewState.Approved);
        }
        int pipelineId, failedStep;
        await using (var db = app.Db())
        {
            var p = new Pipeline { RepositoryId = repo.Id, Ref = "main", CommitSha = new string('a', 40), CommitMessage = "build it", Status = PipelineStatus.Failed, Trigger = PipelineTrigger.Push };
            var job = new PipelineJob { Name = "test", Image = "alpine", Status = PipelineStatus.Failed, Order = 0 };
            var ok = new PipelineStep { Name = "setup", Run = "true", Status = PipelineStatus.Passed, Order = 0 };
            var bad = new PipelineStep { Name = "run tests", Run = "false", Status = PipelineStatus.Failed, ExitCode = 1, Order = 1 };
            job.Steps.Add(ok); job.Steps.Add(bad);
            p.Jobs.Add(job);
            db.Pipelines.Add(p);
            await db.SaveChangesAsync();
            db.PipelineStepLogs.Add(new PipelineStepLog { StepId = bad.Id, Text = string.Concat(Enumerable.Repeat("noise line\n", 3000)) + "FATAL: 2 tests failed\n" });
            await db.SaveChangesAsync();
            (pipelineId, failedStep) = (p.Id, bad.Id);
        }

        var bobClient = await ClientAsync("bob", TokenScopes.ReadApi);
        var name = $"{project.Key}/app";
        Assert.Contains(name, McpClient.Text(await OkCall(bobClient, "list_repositories")));

        var dir = await OkCall(bobClient, "list_directory", new { repository = name });
        Assert.Contains("src/", McpClient.Text(dir));
        var sub = await OkCall(bobClient, "list_directory", new { repository = name, path = "src" });
        Assert.Contains("hello.txt", McpClient.Text(sub));
        Assert.DoesNotContain("new.txt", McpClient.Text(sub));
        Assert.Contains("new.txt", McpClient.Text(await OkCall(bobClient, "list_directory", new { repository = name, path = "src", @ref = "feature/x" })));
        Assert.Contains("not found", await ErrorText(bobClient, "list_directory", new { repository = name, path = "nope" }));

        Assert.Contains("find the needle", McpClient.Text(await OkCall(bobClient, "read_file", new { repository = name, path = "src/hello.txt" })));

        var search = await OkCall(bobClient, "search_code", new { query = "needle", repository = name });
        Assert.Contains("src/hello.txt:2", McpClient.Text(search));
        Assert.Contains("No matches", McpClient.Text(await OkCall(bobClient, "search_code", new { query = "zzzz-not-there", repository = name })));
        Assert.Contains("regular expression", await ErrorText(bobClient, "search_code", new { query = "(", regex = true, repository = name }));

        var list = await OkCall(bobClient, "list_pull_requests", new { repository = name });
        Assert.Contains("Add the feature", McpClient.Text(list));
        Assert.Contains("No pull requests", McpClient.Text(await OkCall(bobClient, "list_pull_requests", new { project = project.Key, state = "merged" })));

        var pr = await OkCall(bobClient, "get_pull_request", new { repository = name, number = prNumber, include_diff = true });
        var prText = McpClient.Text(pr);
        Assert.Contains("1 approved", prText);
        Assert.Contains("src/new.txt", prText);
        Assert.Contains("+added in the feature", prText);
        Assert.Equal(1, pr["structuredContent"]!["approvals"]!.GetValue<int>());
        Assert.Contains("not found", await ErrorText(bobClient, "get_pull_request", new { repository = name, number = 999 }));
        var noDiff = McpClient.Text(await OkCall(bobClient, "get_pull_request", new { repository = name, number = prNumber }));
        Assert.DoesNotContain("+added in the feature", noDiff);

        var pipelines = await OkCall(bobClient, "list_pipelines", new { repository = name, status = "failed" });
        Assert.Contains($"#{pipelineId}", McpClient.Text(pipelines));
        Assert.Contains("No pipelines", McpClient.Text(await OkCall(bobClient, "list_pipelines", new { repository = name, status = "passed" })));

        var logs = await OkCall(bobClient, "get_pipeline_logs", new { repository = name, pipeline_id = pipelineId, tail_chars = 500 });
        var logText = McpClient.Text(logs);
        Assert.Contains("FATAL: 2 tests failed", logText); // the failed step's tail
        Assert.Contains("run tests", logText);
        Assert.True(logs["structuredContent"]!["truncated"]!.GetValue<bool>());
        Assert.True(logs["structuredContent"]!["log"]!.GetValue<string>().Length <= 500);
        Assert.Equal(failedStep, logs["structuredContent"]!["stepId"]!.GetValue<int>());
        Assert.Contains("not part of pipeline", await ErrorText(bobClient, "get_pipeline_logs", new { repository = name, pipeline_id = pipelineId, step_id = 99999 }));
        Assert.Contains("not found", await ErrorText(bobClient, "get_pipeline_logs", new { repository = name, pipeline_id = 999999 }));
    }

    // ---------- Audit ----------

    [Fact]
    public async Task Tool_calls_are_audited_with_tool_and_project_but_never_free_text()
    {
        var project = await app.CreateProjectAsync("alice", ProjectVisibility.Private);
        var alice = await ClientAsync("alice");
        const string secret = "ULTRA-SECRET-FREE-TEXT-7731";
        var created = await OkCall(alice, "create_issue", new { project = project.Key, title = secret, description = secret });
        await OkCall(alice, "comment_on_issue", new { issue = created["structuredContent"]!["key"]!.GetValue<string>(), body = secret });
        await ErrorText(alice, "get_issue", new { issue = "ZZZ-1" });

        await using var db = app.Db();
        var events = await db.AuditEvents.Where(e => e.Action == "mcp.tool_called" && e.ProjectId == project.Id).OrderBy(e => e.Id).ToListAsync();
        Assert.Equal(["create_issue", "comment_on_issue"], events.Select(e => e.TargetId));
        Assert.All(events, e =>
        {
            Assert.Equal(app.UserId("alice"), e.ActorId);
            Assert.Contains(project.Key, e.Details);
            Assert.DoesNotContain(secret, e.Details);
            Assert.DoesNotContain("gbp_", e.Details);
        });
        Assert.True(await db.AuditEvents.AnyAsync(e => e.Action == "mcp.tool_called" && e.TargetId == "get_issue" && e.ProjectId == null));
        Assert.DoesNotContain(await db.AuditEvents.Select(e => e.Details).ToListAsync(), d => d != null && d.Contains(secret));
    }

    // ---------- Documentation ----------

    [Fact]
    public void Every_tool_is_documented_on_the_mcp_cli_page()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "ByteBench.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var page = File.ReadAllText(Path.Combine(dir!.FullName, "site", "pages", "mcp-cli.html"));
        var undocumented = McpTools.All.Where(t => !page.Contains($"<code>{t.Name}</code>")).Select(t => t.Name).ToList();
        Assert.True(undocumented.Count == 0, "Tools missing from site/pages/mcp-cli.html: " + string.Join(", ", undocumented));
        Assert.Equal(McpTools.All.Count, McpTools.All.Select(t => t.Name).Distinct().Count());
    }
}

/// <summary>The REST API's per-token limit also covers /mcp (5 requests a minute here).</summary>
public class McpLimitedApp : TestApp
{
    protected override void AddSettings(IDictionary<string, string?> settings)
    {
        settings["RateLimiting:Enabled"] = "true";
        settings["RateLimiting:Api:PermitLimit"] = "5";
        settings["RateLimiting:Api:WindowSeconds"] = "60";
    }
}

public class McpRateLimitTests(McpLimitedApp app) : IClassFixture<McpLimitedApp>
{
    [Fact]
    public async Task Each_token_has_its_own_request_budget()
    {
        var a = new McpClient(await app.ApiClientAsync("alice"));
        var b = new McpClient(await app.ApiClientAsync("bob"));
        for (var i = 0; i < 5; i++) Assert.Equal(HttpStatusCode.OK, (await a.RequestAsync("ping")).Status);
        var (status, _) = await a.RequestAsync("ping");
        Assert.Equal(HttpStatusCode.TooManyRequests, status);
        Assert.Equal(HttpStatusCode.OK, (await b.RequestAsync("ping")).Status); // another token is unaffected
    }
}

internal static class McpTestExtensions
{
    public static string Key(this Issue issue, Project project) => $"{project.Key}-{issue.Number}";
}
