using System.Net;
using System.Text;
using System.Text.Json;
using ByteBench.Data;
using ByteBench.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace ByteBench.Tests;

/// <summary>
/// The management REST endpoints (create/update/delete projects, members, labels, components, versions, custom fields, workflow, groups,
/// repositories, imports, mirrors, branch protection, releases, webhooks, sprints, boards): happy path, permission denied, validation,
/// conflict, unknown properties, scopes and audit entries. Users: alice owns (and is a site admin), bob Developer, carol Maintainer, dan Viewer.
/// </summary>
public class ManagementApiTests(TestApp app) : IClassFixture<TestApp>
{
    private static int counter;
    private static string Unique(string prefix) => prefix + Interlocked.Increment(ref counter);

    private sealed record Reply(HttpStatusCode Status, JsonElement Json, string Text)
    {
        public JsonElement this[string name] => Json.GetProperty(name);
        public string Str(string name) => Json.GetProperty(name).GetString()!;
    }

    private static async Task<Reply> Send(HttpClient c, HttpMethod method, string url, object? body = null, string? rawBody = null)
    {
        using var request = new HttpRequestMessage(method, url);
        if (rawBody != null) request.Content = new StringContent(rawBody, Encoding.UTF8, "application/json");
        else if (body != null) request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var response = await c.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        JsonElement json = default;
        if (text.Length > 0 && response.Content.Headers.ContentType?.MediaType?.Contains("json") == true) json = JsonDocument.Parse(text).RootElement.Clone();
        return new Reply(response.StatusCode, json, text);
    }

    private static Task<Reply> Post(HttpClient c, string url, object? body = null) => Send(c, HttpMethod.Post, url, body ?? new { });
    private static Task<Reply> Patch(HttpClient c, string url, object body) => Send(c, HttpMethod.Patch, url, body);
    private static Task<Reply> Put(HttpClient c, string url, object body) => Send(c, HttpMethod.Put, url, body);
    private static Task<Reply> Get(HttpClient c, string url) => Send(c, HttpMethod.Get, url);
    private static Task<Reply> Delete(HttpClient c, string url) => Send(c, HttpMethod.Delete, url);

    private static void Is(HttpStatusCode expected, Reply r) => Assert.True(r.Status == expected, $"expected {(int)expected}, got {(int)r.Status}: {r.Text}");

    private sealed record World(Project Project, string Key, string P, HttpClient Alice, HttpClient Bob, HttpClient Carol, HttpClient Dan, HttpClient Outsider);

    /// <summary>A fresh private project: alice Owner, carol Maintainer, bob Developer, dan Viewer; "outsider" is not a member.</summary>
    private async Task<World> WorldAsync()
    {
        var project = await app.CreateProjectAsync("alice", ProjectVisibility.Private, ("carol", ProjectRole.Maintainer), ("bob", ProjectRole.Developer), ("dan", ProjectRole.Viewer));
        if (!app.Db().Users.Any(u => u.UserName == "mgmt-outsider"))
            try { await app.CreateUserAsync("mgmt-outsider"); } catch (InvalidOperationException) { /* created by a parallel test */ }
        return new World(project, project.Key, $"/api/v1/projects/{project.Key}", await app.ApiClientAsync("alice"), await app.ApiClientAsync("bob"),
            await app.ApiClientAsync("carol"), await app.ApiClientAsync("dan"), await app.ApiClientAsync("mgmt-outsider"));
    }

    private async Task<List<AuditEvent>> AuditAsync(string action, int projectId)
    {
        await using var db = app.Db();
        return await db.AuditEvents.AsNoTracking().Where(e => e.Action == action && e.ProjectId == projectId).ToListAsync();
    }

    // =============================================================================================== projects

    [Fact]
    public async Task Create_project_makes_the_caller_its_owner_and_audits_it()
    {
        var alice = await app.ApiClientAsync("alice");
        var key = "MG" + Unique("").PadLeft(3, '0');
        var created = await Post(alice, "/api/v1/projects", new { key = key.ToLower(), name = "Managed", description = "Made by API", visibility = "internal" });
        Is(HttpStatusCode.Created, created);
        Assert.Equal(key, created.Str("key"));
        Assert.Equal("internal", created.Str("visibility"));
        Assert.Equal("owner", created.Str("role"));
        var id = app.ProjectId(key);
        Assert.Single(await AuditAsync("project.created", id));
        // Default labels and workflow, like a project made in the UI.
        Assert.Equal(3, (await Get(alice, $"/api/v1/projects/{key}/labels")).Json.GetArrayLength());
        Assert.True((await Get(alice, $"/api/v1/projects/{key}/workflow"))["statuses"].GetArrayLength() >= 3);
    }

    [Fact]
    public async Task Create_project_rejects_duplicates_bad_keys_and_unknown_properties()
    {
        var alice = await app.ApiClientAsync("alice");
        var bob = await app.ApiClientAsync("bob");
        var key = "MD" + Unique("").PadLeft(3, '0');
        Is(HttpStatusCode.Created, await Post(alice, "/api/v1/projects", new { key, name = "First" }));
        var dupe = await Post(bob, "/api/v1/projects", new { key = key.ToLower(), name = "Second" });
        Is(HttpStatusCode.Conflict, dupe);
        Assert.Contains("already exists", dupe.Text);
        Is(HttpStatusCode.UnprocessableEntity, await Post(alice, "/api/v1/projects", new { key = "1bad", name = "x" }));
        Is(HttpStatusCode.UnprocessableEntity, await Post(alice, "/api/v1/projects", new { name = "no key" }));
        Is(HttpStatusCode.UnprocessableEntity, await Post(alice, "/api/v1/projects", new { key = "NOPE" + Unique(""), name = "" }));
        var typo = await Post(alice, "/api/v1/projects", new { key = "TYPO1", name = "x", visibilty = "internal" });
        Is(HttpStatusCode.BadRequest, typo);
        Assert.Contains("visibilty", typo.Text);
        Is(HttpStatusCode.BadRequest, await Send(alice, HttpMethod.Post, "/api/v1/projects", rawBody: "{not json"));
        Is(HttpStatusCode.BadRequest, await Send(alice, HttpMethod.Post, "/api/v1/projects", new { key = "ENUM1", name = "x", visibility = "public" }));
    }

    [Fact]
    public async Task Create_project_needs_the_write_scope()
    {
        var readOnly = await app.ApiClientAsync("alice", TokenScopes.ReadApi);
        Is(HttpStatusCode.Forbidden, await Post(readOnly, "/api/v1/projects", new { key = "RO" + Unique(""), name = "x" }));
    }

    [Fact]
    public async Task Update_project_changes_only_what_is_sent_and_needs_owner()
    {
        var w = await WorldAsync();
        var r = await Patch(w.Alice, w.P, new { name = "Renamed", visibility = "internal" });
        Is(HttpStatusCode.OK, r);
        Assert.Equal("Renamed", r.Str("name"));
        Assert.Equal("internal", r.Str("visibility"));
        var cleared = await Patch(w.Alice, w.P, new { description = "words" });
        Assert.Equal("words", cleared.Str("description"));
        Assert.Equal(JsonValueKind.Null, (await Patch(w.Alice, w.P, new { description = "" }))["description"].ValueKind);
        Assert.NotEmpty(await AuditAsync("project.visibility_changed", w.Project.Id));

        Is(HttpStatusCode.Forbidden, await Patch(w.Carol, w.P, new { name = "Nope" })); // Maintainer lacks ManageProjectSettings
        Is(HttpStatusCode.Forbidden, await Patch(w.Dan, w.P, new { name = "Nope" }));
        Is(HttpStatusCode.UnprocessableEntity, await Patch(w.Alice, w.P, new { name = " " }));
        Is(HttpStatusCode.BadRequest, await Patch(w.Alice, w.P, new { name = "x", nope = 1 }));
    }

    [Fact]
    public async Task Update_and_delete_are_404_for_a_project_you_cannot_see()
    {
        var w = await WorldAsync();
        Is(HttpStatusCode.NotFound, await Patch(w.Outsider, w.P, new { name = "x" }));
        Is(HttpStatusCode.NotFound, await Delete(w.Outsider, w.P + "?confirm=" + w.Key));
        Is(HttpStatusCode.NotFound, await Patch(w.Alice, "/api/v1/projects/NOSUCH", new { name = "x" }));
    }

    [Fact]
    public async Task Delete_project_needs_owner_and_the_typed_key()
    {
        var w = await WorldAsync();
        Is(HttpStatusCode.Forbidden, await Delete(w.Carol, $"{w.P}?confirm={w.Key}"));
        Is(HttpStatusCode.UnprocessableEntity, await Delete(w.Alice, w.P));
        Is(HttpStatusCode.UnprocessableEntity, await Delete(w.Alice, $"{w.P}?confirm=WRONG"));
        Is(HttpStatusCode.NoContent, await Delete(w.Alice, $"{w.P}?confirm={w.Key.ToLower()}"));
        Is(HttpStatusCode.NotFound, await Get(w.Alice, w.P));
        Assert.NotEmpty(await AuditAsync("project.deleted", w.Project.Id));
    }

    // =============================================================================================== members

    [Fact]
    public async Task Members_can_be_listed_added_changed_and_removed_by_an_owner()
    {
        var w = await WorldAsync();
        await app.CreateUserAsync(Unique("mgmtnew"));
        var newcomer = app.Db().Users.Where(u => u.UserName!.StartsWith("mgmtnew")).OrderByDescending(u => u.Id).Select(u => u.UserName).First()!;

        var list = await Get(w.Dan, $"{w.P}/members?perPage=2");
        Is(HttpStatusCode.OK, list);
        Assert.Equal(2, list.Json.GetArrayLength());
        Assert.Equal("4", await TotalAsync(w.Dan, $"{w.P}/members?perPage=2"));

        var added = await Post(w.Alice, $"{w.P}/members", new { user = newcomer, role = "developer" });
        Is(HttpStatusCode.Created, added);
        Assert.Equal("developer", added.Str("role"));
        Assert.Equal(newcomer, added["user"].GetProperty("username").GetString());
        Assert.Single(await AuditAsync("member.added", w.Project.Id), e => e.Details!.Contains(newcomer));

        Is(HttpStatusCode.Conflict, await Post(w.Alice, $"{w.P}/members", new { user = newcomer, role = "viewer" }));

        var changed = await Patch(w.Alice, $"{w.P}/members/{newcomer}", new { role = "maintainer" });
        Is(HttpStatusCode.OK, changed);
        Assert.Equal("maintainer", changed.Str("role"));
        Assert.NotEmpty(await AuditAsync("member.role_changed", w.Project.Id));

        Is(HttpStatusCode.NoContent, await Delete(w.Alice, $"{w.P}/members/{newcomer}"));
        Is(HttpStatusCode.NotFound, await Delete(w.Alice, $"{w.P}/members/{newcomer}"));
        Assert.NotEmpty(await AuditAsync("member.removed", w.Project.Id));
    }

    private static async Task<string> TotalAsync(HttpClient c, string url)
    {
        using var response = await c.GetAsync(url);
        return response.Headers.GetValues("X-Total-Count").Single();
    }

    [Fact]
    public async Task Member_changes_are_denied_validated_and_protect_the_last_owner()
    {
        var w = await WorldAsync();
        Is(HttpStatusCode.Forbidden, await Post(w.Carol, $"{w.P}/members", new { user = "mgmt-outsider", role = "viewer" }));
        Is(HttpStatusCode.Forbidden, await Patch(w.Bob, $"{w.P}/members/dan", new { role = "owner" }));
        Is(HttpStatusCode.Forbidden, await Delete(w.Dan, $"{w.P}/members/bob"));
        Is(HttpStatusCode.NotFound, await Post(w.Outsider, $"{w.P}/members", new { user = "bob", role = "viewer" }));
        Is(HttpStatusCode.UnprocessableEntity, await Post(w.Alice, $"{w.P}/members", new { user = "nobody-here", role = "viewer" }));
        Is(HttpStatusCode.UnprocessableEntity, await Post(w.Alice, $"{w.P}/members", new { user = "mgmt-outsider" }));
        Is(HttpStatusCode.UnprocessableEntity, await Post(w.Alice, $"{w.P}/members", new { role = "viewer" }));
        Is(HttpStatusCode.BadRequest, await Post(w.Alice, $"{w.P}/members", new { user = "mgmt-outsider", role = "boss" }));
        Is(HttpStatusCode.BadRequest, await Post(w.Alice, $"{w.P}/members", new { user = "mgmt-outsider", role = "viewer", extra = true }));
        Is(HttpStatusCode.NotFound, await Patch(w.Alice, $"{w.P}/members/mgmt-outsider", new { role = "viewer" })); // not a member
        Is(HttpStatusCode.NotFound, await Patch(w.Alice, $"{w.P}/members/no-such-user", new { role = "viewer" }));
        var last = await Delete(w.Alice, $"{w.P}/members/alice");
        Is(HttpStatusCode.UnprocessableEntity, last);
        Assert.Contains("last Owner", last.Text);
        Is(HttpStatusCode.UnprocessableEntity, await Patch(w.Alice, $"{w.P}/members/alice", new { role = "viewer" }));
    }

    // =============================================================================================== labels

    [Fact]
    public async Task Labels_are_created_edited_and_deleted_by_maintainers()
    {
        var w = await WorldAsync();
        var created = await Post(w.Carol, $"{w.P}/labels", new { name = "triage", color = "#AABBCC" });
        Is(HttpStatusCode.Created, created);
        Assert.Equal("#aabbcc", created.Str("color"));
        var id = created["id"].GetInt32();

        Is(HttpStatusCode.Conflict, await Post(w.Carol, $"{w.P}/labels", new { name = "TRIAGE" }));
        var renamed = await Patch(w.Carol, $"{w.P}/labels/{id}", new { name = "needs-triage" });
        Assert.Equal("needs-triage", renamed.Str("name"));
        Assert.Equal("#aabbcc", renamed.Str("color"));
        Is(HttpStatusCode.Conflict, await Patch(w.Carol, $"{w.P}/labels/{id}", new { name = "bug" })); // a default label
        Is(HttpStatusCode.UnprocessableEntity, await Patch(w.Carol, $"{w.P}/labels/{id}", new { color = "red" }));
        Is(HttpStatusCode.UnprocessableEntity, await Post(w.Carol, $"{w.P}/labels", new { }));
        Is(HttpStatusCode.BadRequest, await Post(w.Carol, $"{w.P}/labels", new { name = "x", colour = "#000000" }));

        Is(HttpStatusCode.Forbidden, await Post(w.Bob, $"{w.P}/labels", new { name = "dev-label" }));
        Is(HttpStatusCode.Forbidden, await Patch(w.Bob, $"{w.P}/labels/{id}", new { name = "dev" }));
        Is(HttpStatusCode.Forbidden, await Delete(w.Bob, $"{w.P}/labels/{id}"));
        Is(HttpStatusCode.NotFound, await Delete(w.Carol, $"{w.P}/labels/999999"));
        Is(HttpStatusCode.NoContent, await Delete(w.Carol, $"{w.P}/labels/{id}"));
        Assert.DoesNotContain("needs-triage", (await Get(w.Carol, $"{w.P}/labels")).Text);
    }

    // =============================================================================================== components, versions, fields

    [Fact]
    public async Task Components_are_managed_with_leads_and_conflicts()
    {
        var w = await WorldAsync();
        var created = await Post(w.Carol, $"{w.P}/components", new { name = "Backend", description = "Services", lead = "bob" });
        Is(HttpStatusCode.Created, created);
        Assert.Equal("bob", created["lead"].GetProperty("username").GetString());
        var id = created["id"].GetInt32();
        Is(HttpStatusCode.Conflict, await Post(w.Carol, $"{w.P}/components", new { name = "backend" }));
        Is(HttpStatusCode.UnprocessableEntity, await Post(w.Carol, $"{w.P}/components", new { name = "Other", lead = "mgmt-outsider" })); // can't see the project
        Is(HttpStatusCode.UnprocessableEntity, await Post(w.Carol, $"{w.P}/components", new { name = "Other", lead = "ghost" }));
        Is(HttpStatusCode.BadRequest, await Post(w.Carol, $"{w.P}/components", new { name = "Other", owner = "bob" }));
        var updated = await Patch(w.Carol, $"{w.P}/components/{id}", new { name = "API", defaultAssignee = "carol", lead = "" });
        Is(HttpStatusCode.OK, updated);
        Assert.Equal("API", updated.Str("name"));
        Assert.Equal(JsonValueKind.Null, updated["lead"].ValueKind);
        Assert.Equal("carol", updated["defaultAssignee"].GetProperty("username").GetString());
        Is(HttpStatusCode.Forbidden, await Post(w.Bob, $"{w.P}/components", new { name = "Nope" }));
        Is(HttpStatusCode.NotFound, await Patch(w.Carol, $"{w.P}/components/999999", new { name = "x" }));
        Is(HttpStatusCode.NoContent, await Delete(w.Carol, $"{w.P}/components/{id}"));
        Assert.Equal(0, (await Get(w.Carol, $"{w.P}/components")).Json.GetArrayLength());
        Assert.NotEmpty(await AuditAsync("component.deleted", w.Project.Id));
    }

    [Fact]
    public async Task Versions_are_created_released_and_deleted()
    {
        var w = await WorldAsync();
        var created = await Post(w.Carol, $"{w.P}/versions", new { name = "1.0", description = "First", startDate = "2030-01-01", releaseDate = "2030-02-01" });
        Is(HttpStatusCode.Created, created);
        Assert.Equal("2030-02-01", created.Str("releaseDate"));
        Assert.False(created["released"].GetBoolean());
        var id = created["id"].GetInt32();
        Is(HttpStatusCode.Conflict, await Post(w.Carol, $"{w.P}/versions", new { name = "1.0" }));
        Is(HttpStatusCode.UnprocessableEntity, await Post(w.Carol, $"{w.P}/versions", new { name = "2.0", startDate = "2030-03-01", releaseDate = "2030-02-01" }));
        Is(HttpStatusCode.BadRequest, await Post(w.Carol, $"{w.P}/versions", new { name = "2.0", startDate = "not a date" }));
        Is(HttpStatusCode.BadRequest, await Post(w.Carol, $"{w.P}/versions", new { name = "2.0", date = "2030-03-01" }));

        var released = await Patch(w.Carol, $"{w.P}/versions/{id}", new { released = true });
        Assert.True(released["released"].GetBoolean());
        Assert.Equal("2030-02-01", released.Str("releaseDate"));
        Is(HttpStatusCode.Forbidden, await Patch(w.Bob, $"{w.P}/versions/{id}", new { released = false }));
        Is(HttpStatusCode.NotFound, await Patch(w.Carol, $"{w.P}/versions/999999", new { released = false }));
        Is(HttpStatusCode.NoContent, await Delete(w.Carol, $"{w.P}/versions/{id}"));
        Assert.NotEmpty(await AuditAsync("version.created", w.Project.Id));
        Assert.NotEmpty(await AuditAsync("version.deleted", w.Project.Id));
    }

    [Fact]
    public async Task Custom_fields_are_managed_and_validated()
    {
        var w = await WorldAsync();
        var created = await Post(w.Carol, $"{w.P}/fields", new { key = "severity", name = "Severity", type = "singleSelect", options = new[] { "low", "high" }, issueTypes = new[] { "bug" } });
        Is(HttpStatusCode.Created, created);
        Assert.Equal("singleSelect", created.Str("type"));
        var id = created["id"].GetInt32();
        Is(HttpStatusCode.Conflict, await Post(w.Carol, $"{w.P}/fields", new { key = "severity", name = "Other", type = "text" }));
        Is(HttpStatusCode.Conflict, await Post(w.Carol, $"{w.P}/fields", new { key = "sev2", name = "severity", type = "text" }));
        Is(HttpStatusCode.UnprocessableEntity, await Post(w.Carol, $"{w.P}/fields", new { key = "Bad Key", name = "Bad", type = "text" }));
        Is(HttpStatusCode.UnprocessableEntity, await Post(w.Carol, $"{w.P}/fields", new { key = "nolist", name = "No list", type = "singleSelect" }));
        Is(HttpStatusCode.UnprocessableEntity, await Post(w.Carol, $"{w.P}/fields", new { key = "notype", name = "No type" }));
        Is(HttpStatusCode.BadRequest, await Post(w.Carol, $"{w.P}/fields", new { key = "weird", name = "Weird", type = "hologram" }));
        var changed = await Patch(w.Carol, $"{w.P}/fields/{id}", new { name = "Impact", required = true, options = new[] { "low", "medium", "high" } });
        Is(HttpStatusCode.OK, changed);
        Assert.Equal("Impact", changed.Str("name"));
        Assert.Equal(3, changed["options"].GetArrayLength());
        Is(HttpStatusCode.Forbidden, await Post(w.Bob, $"{w.P}/fields", new { key = "dev", name = "Dev", type = "text" }));
        Is(HttpStatusCode.NotFound, await Delete(w.Carol, $"{w.P}/fields/999999"));
        Is(HttpStatusCode.NoContent, await Delete(w.Carol, $"{w.P}/fields/{id}"));
        Assert.Equal(0, (await Get(w.Carol, $"{w.P}/fields")).Json.GetArrayLength());
        Assert.NotEmpty(await AuditAsync("custom_field.created", w.Project.Id));
    }

    // =============================================================================================== workflow

    [Fact]
    public async Task Workflow_statuses_and_transitions_are_managed_by_an_owner()
    {
        var w = await WorldAsync();
        var created = await Post(w.Alice, $"{w.P}/workflow/statuses", new { name = "Blocked", color = "#ff0000", category = "inProgress" });
        Is(HttpStatusCode.Created, created);
        var id = created["id"].GetInt32();
        Assert.Equal("inProgress", created.Str("category"));
        Is(HttpStatusCode.Conflict, await Post(w.Alice, $"{w.P}/workflow/statuses", new { name = "Blocked", category = "todo" })); // duplicate name
        Is(HttpStatusCode.UnprocessableEntity, await Post(w.Alice, $"{w.P}/workflow/statuses", new { name = "NoCategory" }));
        Is(HttpStatusCode.BadRequest, await Post(w.Alice, $"{w.P}/workflow/statuses", new { name = "X", category = "limbo" }));
        Is(HttpStatusCode.Forbidden, await Post(w.Carol, $"{w.P}/workflow/statuses", new { name = "Nope", category = "todo" }));

        var renamed = await Patch(w.Alice, $"{w.P}/workflow/statuses/{id}", new { name = "On hold" });
        Assert.Equal("On hold", renamed.Str("name"));
        Assert.Equal("#ff0000", renamed.Str("color"));
        var before = (await Get(w.Alice, $"{w.P}/workflow"))["statuses"].EnumerateArray().Select(s => s.GetProperty("id").GetInt32()).ToList();
        var moved = await Post(w.Alice, $"{w.P}/workflow/statuses/{id}/move", new { delta = -1 });
        Is(HttpStatusCode.OK, moved);
        var after = moved["statuses"].EnumerateArray().Select(s => s.GetProperty("id").GetInt32()).ToList();
        Assert.Equal(before.Count, after.Count);
        Assert.True(after.IndexOf(id) < before.IndexOf(id));
        Is(HttpStatusCode.UnprocessableEntity, await Post(w.Alice, $"{w.P}/workflow/statuses/{id}/move", new { delta = 5 }));

        var other = before.First(x => x != id);
        var transitions = await Put(w.Alice, $"{w.P}/workflow/transitions", new { transitions = new[] { new { from = id, to = other } } });
        Is(HttpStatusCode.OK, transitions);
        Assert.Equal(1, transitions["transitions"].GetArrayLength());
        Is(HttpStatusCode.UnprocessableEntity, await Put(w.Alice, $"{w.P}/workflow/transitions", new { transitions = new[] { new { from = id, to = 999999 } } }));
        Is(HttpStatusCode.UnprocessableEntity, await Put(w.Alice, $"{w.P}/workflow/transitions", new { }));
        Is(HttpStatusCode.OK, await Put(w.Alice, $"{w.P}/workflow/transitions", new { transitions = Array.Empty<object>() }));

        Is(HttpStatusCode.NotFound, await Delete(w.Alice, $"{w.P}/workflow/statuses/999999"));
        Is(HttpStatusCode.NoContent, await Delete(w.Alice, $"{w.P}/workflow/statuses/{id}"));
        Assert.NotEmpty(await AuditAsync("workflow.status_added", w.Project.Id));
        Assert.NotEmpty(await AuditAsync("workflow.status_deleted", w.Project.Id));
    }

    [Fact]
    public async Task Deleting_a_status_that_issues_use_needs_somewhere_to_move_them()
    {
        var w = await WorldAsync();
        var status = await Post(w.Alice, $"{w.P}/workflow/statuses", new { name = "QA", category = "inReview" });
        var id = status["id"].GetInt32();
        var other = (await Get(w.Alice, $"{w.P}/workflow"))["statuses"].EnumerateArray().First(s => s.GetProperty("id").GetInt32() != id).GetProperty("id").GetInt32();
        var issue = await Post(w.Alice, $"{w.P}/issues", new { title = "In QA", workflowStatusId = id });
        Is(HttpStatusCode.Created, issue);
        var refused = await Delete(w.Alice, $"{w.P}/workflow/statuses/{id}");
        Is(HttpStatusCode.UnprocessableEntity, refused);
        Assert.Contains("Choose a status", refused.Text);
        Is(HttpStatusCode.NoContent, await Delete(w.Alice, $"{w.P}/workflow/statuses/{id}?moveIssuesTo={other}"));
        Assert.Equal(other, (await Get(w.Alice, $"{w.P}/issues/{issue["number"].GetInt32()}"))["workflowStatus"].GetProperty("id").GetInt32());
    }

    // =============================================================================================== groups

    [Fact]
    public async Task Groups_can_be_created_updated_and_have_members_managed()
    {
        var alice = await app.ApiClientAsync("alice");
        var bob = await app.ApiClientAsync("bob");
        var path = Unique("mgmtgrp");
        var created = await Post(alice, "/api/v1/groups", new { name = "Managed group", path, visibility = "internal", description = "d" });
        Is(HttpStatusCode.Created, created);
        Assert.Equal(path, created.Str("fullPath"));
        Assert.Equal("owner", created.Str("role"));
        Is(HttpStatusCode.Conflict, await Post(bob, "/api/v1/groups", new { name = "Again", path }));
        Is(HttpStatusCode.UnprocessableEntity, await Post(alice, "/api/v1/groups", new { name = "x", path = "Bad Path!" }));
        Is(HttpStatusCode.UnprocessableEntity, await Post(alice, "/api/v1/groups", new { path = "nameless" + Unique("") }));
        Is(HttpStatusCode.BadRequest, await Post(alice, "/api/v1/groups", new { name = "x", path = "typo" + Unique(""), parent = 1 }));

        var sub = await Post(alice, "/api/v1/groups", new { name = "Sub", path = "sub", parentId = created["id"].GetInt32() });
        Is(HttpStatusCode.Created, sub);
        Assert.Equal($"{path}/sub", sub.Str("fullPath"));
        Is(HttpStatusCode.Forbidden, await Post(bob, "/api/v1/groups", new { name = "Intruder", path = "intruder", parentId = created["id"].GetInt32() }));

        var updated = await Patch(alice, $"/api/v1/groups/{path}", new { name = "Renamed group", color = "#112233" });
        Is(HttpStatusCode.OK, updated);
        Assert.Equal("Renamed group", updated.Str("name"));
        Is(HttpStatusCode.Forbidden, await Patch(bob, $"/api/v1/groups/{path}", new { name = "Hijack" }));
        Is(HttpStatusCode.NotFound, await Patch(alice, "/api/v1/groups/no-such-group-" + Unique(""), new { name = "x" }));

        var members = $"/api/v1/groups/{path}/members";
        var added = await Post(alice, members, new { user = "bob", role = "developer" });
        Is(HttpStatusCode.Created, added);
        Is(HttpStatusCode.Conflict, await Post(alice, members, new { user = "bob", role = "viewer" }));
        Is(HttpStatusCode.Forbidden, await Post(bob, members, new { user = "dan", role = "viewer" }));
        Is(HttpStatusCode.UnprocessableEntity, await Post(alice, members, new { user = "ghost", role = "viewer" }));
        var list = await Get(bob, members);
        Is(HttpStatusCode.OK, list);
        Assert.Contains(list.Json.EnumerateArray(), m => m.GetProperty("user").GetProperty("username").GetString() == "bob");
        Is(HttpStatusCode.OK, await Patch(alice, $"{members}/bob", new { role = "maintainer" }));
        Is(HttpStatusCode.UnprocessableEntity, await Patch(alice, $"{members}/alice", new { role = "viewer" })); // the last Owner
        Is(HttpStatusCode.NoContent, await Delete(alice, $"{members}/bob"));
        Is(HttpStatusCode.NotFound, await Delete(alice, $"{members}/bob"));
    }

    // =============================================================================================== repositories

    private async Task<(World W, string R)> RepoWorldAsync(string slug = "managed")
    {
        var w = await WorldAsync();
        var created = await Post(w.Carol, $"{w.P}/repos", new { name = "Managed repo", slug, description = "From the API" });
        Is(HttpStatusCode.Created, created);
        return (w, $"{w.P}/repos/{slug}");
    }

    [Fact]
    public async Task Create_repository_makes_an_empty_repo_with_a_readme_and_rejects_duplicates()
    {
        var w = await WorldAsync();
        var created = await Post(w.Carol, $"{w.P}/repos", new { name = "My Service" });
        Is(HttpStatusCode.Created, created);
        Assert.Equal("my-service", created.Str("slug"));
        Assert.Equal("main", created.Str("defaultBranch"));
        Assert.False(created["isArchived"].GetBoolean());
        var raw = await Get(w.Carol, $"{w.P}/repos/my-service/raw/README.md");
        Is(HttpStatusCode.OK, raw);
        Assert.Contains("My Service", raw.Text);
        Assert.NotEmpty(await AuditAsync("repository.created", w.Project.Id));

        Is(HttpStatusCode.Conflict, await Post(w.Carol, $"{w.P}/repos", new { name = "Again", slug = "my-service" }));
        Is(HttpStatusCode.Forbidden, await Post(w.Bob, $"{w.P}/repos", new { name = "Dev repo" }));
        Is(HttpStatusCode.NotFound, await Post(w.Outsider, $"{w.P}/repos", new { name = "Spy repo" }));
        Is(HttpStatusCode.UnprocessableEntity, await Post(w.Carol, $"{w.P}/repos", new { name = "" }));
        Is(HttpStatusCode.UnprocessableEntity, await Post(w.Carol, $"{w.P}/repos", new { name = "Fine", slug = "-bad slug" }));
        Is(HttpStatusCode.BadRequest, await Post(w.Carol, $"{w.P}/repos", new { name = "Fine", branch = "dev" }));
    }

    [Fact]
    public async Task Update_repository_changes_details_default_branch_and_slug()
    {
        var (w, r) = await RepoWorldAsync("settings-repo");
        var repo = app.Db().Repositories.First(x => x.ProjectId == w.Project.Id);
        app.Commit(repo, "develop", "a.txt", "a", from: "main");

        var updated = await Patch(w.Carol, r, new { name = "Renamed repo", description = "New words", defaultBranch = "develop" });
        Is(HttpStatusCode.OK, updated);
        Assert.Equal("Renamed repo", updated.Str("name"));
        Assert.Equal("develop", updated.Str("defaultBranch"));
        Assert.NotEmpty(await AuditAsync("repository.default_branch_changed", w.Project.Id));

        Is(HttpStatusCode.UnprocessableEntity, await Patch(w.Carol, r, new { defaultBranch = "no-such-branch" }));
        Is(HttpStatusCode.BadRequest, await Patch(w.Carol, r, new { visibility = "public" }));
        Is(HttpStatusCode.Forbidden, await Patch(w.Bob, r, new { name = "Dev rename" }));
        Is(HttpStatusCode.NotFound, await Patch(w.Outsider, r, new { name = "Spy" }));
        Is(HttpStatusCode.NotFound, await Patch(w.Carol, $"{w.P}/repos/nope", new { name = "x" }));

        var other = await Post(w.Carol, $"{w.P}/repos", new { name = "Other", slug = "other" });
        Is(HttpStatusCode.Created, other);
        Is(HttpStatusCode.Conflict, await Patch(w.Carol, r, new { slug = "other" }));
        var renamed = await Patch(w.Carol, r, new { slug = "fresh-slug" });
        Is(HttpStatusCode.OK, renamed);
        Assert.Equal("fresh-slug", renamed.Str("slug"));
        Is(HttpStatusCode.OK, await Get(w.Carol, $"{w.P}/repos/fresh-slug"));
        Assert.NotEmpty(await AuditAsync("repository.renamed", w.Project.Id));
    }

    [Fact]
    public async Task Archive_unarchive_and_delete_repository()
    {
        var (w, r) = await RepoWorldAsync("lifecycle");
        var archived = await Post(w.Carol, $"{r}/archive");
        Is(HttpStatusCode.OK, archived);
        Assert.True(archived["isArchived"].GetBoolean());
        Assert.NotEmpty(await AuditAsync("repository.archived", w.Project.Id));
        Is(HttpStatusCode.Forbidden, await Post(w.Bob, $"{r}/unarchive"));
        var restored = await Post(w.Carol, $"{r}/unarchive");
        Assert.False(restored["isArchived"].GetBoolean());

        Is(HttpStatusCode.Forbidden, await Delete(w.Bob, $"{r}?confirm=Managed%20repo"));
        Is(HttpStatusCode.UnprocessableEntity, await Delete(w.Carol, r)); // no confirmation
        Is(HttpStatusCode.UnprocessableEntity, await Delete(w.Carol, $"{r}?confirm=wrong"));
        Is(HttpStatusCode.NoContent, await Delete(w.Carol, $"{r}?confirm=Managed%20repo"));
        Is(HttpStatusCode.NotFound, await Get(w.Carol, r));
        Assert.NotEmpty(await AuditAsync("repository.deleted", w.Project.Id));
        Is(HttpStatusCode.NotFound, await Post(w.Carol, $"{r}/archive"));
    }

    [Fact]
    public async Task Importing_a_repository_queues_a_job_and_validates_the_source()
    {
        var w = await WorldAsync();
        var imports = $"{w.P}/repo-imports";
        Is(HttpStatusCode.UnprocessableEntity, await Post(w.Carol, imports, new { url = "ftp://example.com/x.git", name = "Bad" }));
        Is(HttpStatusCode.UnprocessableEntity, await Post(w.Carol, imports, new { name = "No url" }));
        Is(HttpStatusCode.BadRequest, await Post(w.Carol, imports, new { url = "https://example.com/x.git", name = "Typo", mirror = true }));
        Is(HttpStatusCode.Forbidden, await Post(w.Bob, imports, new { url = "https://example.com/x.git", name = "Dev" }));
        Is(HttpStatusCode.NotFound, await Post(w.Outsider, imports, new { url = "https://example.com/x.git", name = "Spy" }));

        var accepted = await Post(w.Carol, imports, new { url = "https://example.invalid/team/thing.git", name = "Imported thing", slug = "thing" });
        Is(HttpStatusCode.Accepted, accepted);
        Assert.Equal("thing", accepted.Str("slug"));
        Assert.Contains(accepted.Str("status"), new[] { "queued", "cloning", "failed" });
        var id = accepted["id"].GetInt32();
        Assert.NotEmpty(await AuditAsync("repository.import_requested", w.Project.Id));
        Assert.DoesNotContain("secret", accepted.Text.ToLowerInvariant().Replace("keepasmirror", ""));

        Is(HttpStatusCode.OK, await Get(w.Carol, $"{imports}/{id}"));
        Assert.Equal(1, (await Get(w.Carol, imports)).Json.GetArrayLength());
        Is(HttpStatusCode.Forbidden, await Get(w.Bob, $"{imports}/{id}"));
        Is(HttpStatusCode.NotFound, await Get(w.Carol, $"{imports}/999999"));
        Is(HttpStatusCode.OK, await Post(w.Carol, $"{imports}/{id}/cancel"));
    }

    [Fact]
    public async Task Mirrors_are_configured_without_ever_returning_the_secret()
    {
        var (w, r) = await RepoWorldAsync("mirrored");
        var pull = await Put(w.Carol, $"{r}/mirrors/pull", new { url = "https://example.com/upstream.git", username = "u", secret = "s3cret-token", intervalMinutes = 120 });
        Is(HttpStatusCode.OK, pull);
        Assert.Equal("pull", pull.Str("direction"));
        Assert.True(pull["hasSecret"].GetBoolean());
        Assert.DoesNotContain("s3cret-token", pull.Text);
        Assert.Equal(120, pull["intervalMinutes"].GetInt32());

        var push = await Post(w.Carol, $"{r}/mirrors/push", new { url = "https://example.com/backup.git" });
        Is(HttpStatusCode.Created, push);
        var pushId = push["id"].GetInt32();
        var changed = await Put(w.Carol, $"{r}/mirrors/push/{pushId}", new { url = "https://example.com/backup2.git", enabled = false });
        Is(HttpStatusCode.OK, changed);
        Assert.False(changed["enabled"].GetBoolean());
        Is(HttpStatusCode.NotFound, await Put(w.Carol, $"{r}/mirrors/push/{pull["id"].GetInt32()}", new { url = "https://example.com/x.git" })); // not a push mirror

        var list = await Get(w.Carol, $"{r}/mirrors");
        Assert.Equal(2, list.Json.GetArrayLength());
        Assert.DoesNotContain("s3cret-token", list.Text);
        Is(HttpStatusCode.UnprocessableEntity, await Put(w.Carol, $"{r}/mirrors/pull", new { url = "file:///etc/passwd" }));
        Is(HttpStatusCode.UnprocessableEntity, await Put(w.Carol, $"{r}/mirrors/pull", new { }));
        Is(HttpStatusCode.UnprocessableEntity, await Put(w.Carol, $"{r}/mirrors/pull", new { url = "https://example.com/u.git", intervalMinutes = 1 }));
        Is(HttpStatusCode.BadRequest, await Put(w.Carol, $"{r}/mirrors/pull", new { url = "https://example.com/u.git", token = "x" }));
        Is(HttpStatusCode.Forbidden, await Get(w.Bob, $"{r}/mirrors"));
        Is(HttpStatusCode.Forbidden, await Post(w.Bob, $"{r}/mirrors/push", new { url = "https://example.com/b.git" }));
        Is(HttpStatusCode.NotFound, await Get(w.Outsider, $"{r}/mirrors"));
        Is(HttpStatusCode.NoContent, await Delete(w.Carol, $"{r}/mirrors/{pushId}"));
        Is(HttpStatusCode.NotFound, await Delete(w.Carol, $"{r}/mirrors/{pushId}"));
    }

    // =============================================================================================== branch protection

    [Fact]
    public async Task Branch_protection_rules_are_managed_with_conflicts_and_permissions()
    {
        var (w, r) = await RepoWorldAsync("protected");
        var rules = $"{r}/branch-protection";
        var created = await Post(w.Carol, rules, new { pattern = "main", requiredApprovals = 2, blockDirectPushes = true, requireChecksToPass = true });
        Is(HttpStatusCode.Created, created);
        Assert.Equal(2, created["requiredApprovals"].GetInt32());
        Assert.True(created["blockForcePushes"].GetBoolean()); // the UI's default
        Assert.True(created["blockDeletion"].GetBoolean());
        var id = created["id"].GetInt32();
        Assert.NotEmpty(await AuditAsync("branch_protection.created", w.Project.Id));

        Is(HttpStatusCode.Conflict, await Post(w.Carol, rules, new { pattern = "main" }));
        Is(HttpStatusCode.UnprocessableEntity, await Post(w.Carol, rules, new { pattern = "release/*", requiredApprovals = 11 }));
        Is(HttpStatusCode.UnprocessableEntity, await Post(w.Carol, rules, new { requiredApprovals = 1 }));
        Is(HttpStatusCode.UnprocessableEntity, await Post(w.Carol, rules, new { pattern = "bad..name" }));
        Is(HttpStatusCode.BadRequest, await Post(w.Carol, rules, new { pattern = "dev", approvals = 1 }));

        var updated = await Patch(w.Carol, $"{rules}/{id}", new { requiredApprovals = 0, blockDirectPushes = false });
        Is(HttpStatusCode.OK, updated);
        Assert.Equal(0, updated["requiredApprovals"].GetInt32());
        Assert.True(updated["requireChecksToPass"].GetBoolean()); // untouched
        var second = await Post(w.Carol, rules, new { pattern = "release/*" });
        Is(HttpStatusCode.Conflict, await Patch(w.Carol, $"{rules}/{second["id"].GetInt32()}", new { pattern = "main" }));

        var list = await Get(w.Dan, rules); // viewing needs only project access
        Is(HttpStatusCode.OK, list);
        Assert.Equal(2, list.Json.GetArrayLength());
        Is(HttpStatusCode.NotFound, await Get(w.Outsider, rules));
        Is(HttpStatusCode.Forbidden, await Post(w.Bob, rules, new { pattern = "dev" }));
        Is(HttpStatusCode.Forbidden, await Patch(w.Bob, $"{rules}/{id}", new { requiredApprovals = 5 }));
        Is(HttpStatusCode.Forbidden, await Delete(w.Bob, $"{rules}/{id}"));
        Is(HttpStatusCode.NotFound, await Patch(w.Carol, $"{rules}/999999", new { requiredApprovals = 1 }));
        Is(HttpStatusCode.NoContent, await Delete(w.Carol, $"{rules}/{id}"));
        Is(HttpStatusCode.NotFound, await Delete(w.Carol, $"{rules}/{id}"));
        Assert.NotEmpty(await AuditAsync("branch_protection.deleted", w.Project.Id));
    }

    [Fact]
    public async Task A_protection_rule_created_over_the_api_is_enforced_by_the_services()
    {
        var (w, r) = await RepoWorldAsync("enforced");
        Is(HttpStatusCode.Created, await Post(w.Carol, $"{r}/branch-protection", new { pattern = "locked-*", blockDirectPushes = true }));
        await using var s = app.Scope(app.UserId("carol"));
        var repos = s.Get<ByteBench.Services.RepositoryService>();
        var repo = (await repos.GetRepositoryAsync(w.Key, "enforced"))!;
        await Assert.ThrowsAsync<ByteBench.Services.BranchProtectedException>(() => repos.CreateBranchAsync(repo, "locked-1", "main", app.UserId("carol")));
        await repos.CreateBranchAsync(repo, "open-1", "main", app.UserId("carol")); // not matched by the rule
    }

    // =============================================================================================== releases

    [Fact]
    public async Task Releases_can_be_edited_published_and_deleted()
    {
        var (w, r) = await RepoWorldAsync("shipping");
        var created = await Post(w.Carol, $"{r}/releases", new { tagName = "v1.0.0", target = "main", title = "One", draft = true });
        Is(HttpStatusCode.Created, created);
        var id = created["id"].GetInt32();
        Assert.True(created["draft"].GetBoolean());

        var edited = await Patch(w.Bob, $"{r}/releases/{id}", new { title = "One point oh", notes = "Notes", prerelease = true });
        Is(HttpStatusCode.OK, edited);
        Assert.Equal("One point oh", edited.Str("title"));
        Assert.True(edited["prerelease"].GetBoolean());
        Assert.Equal("v1.0.0", edited.Str("tagName"));
        Is(HttpStatusCode.Forbidden, await Patch(w.Dan, $"{r}/releases/{id}", new { title = "Viewer edit" }));
        Is(HttpStatusCode.BadRequest, await Patch(w.Bob, $"{r}/releases/{id}", new { tagName = "v2" }));
        Is(HttpStatusCode.NotFound, await Patch(w.Bob, $"{r}/releases/999999", new { title = "x" }));

        var published = await Post(w.Bob, $"{r}/releases/{id}/publish");
        Is(HttpStatusCode.OK, published);
        Assert.False(published["draft"].GetBoolean());
        Is(HttpStatusCode.Conflict, await Post(w.Bob, $"{r}/releases/{id}/publish")); // already published

        Is(HttpStatusCode.Forbidden, await Delete(w.Bob, $"{r}/releases/{id}")); // Maintainer+
        Is(HttpStatusCode.NoContent, await Delete(w.Carol, $"{r}/releases/{id}?deleteTag=true"));
        Is(HttpStatusCode.NotFound, await Get(w.Carol, $"{r}/releases/{id}"));
        Assert.NotEmpty(await AuditAsync("release.deleted", w.Project.Id));
    }

    // =============================================================================================== webhooks

    [Fact]
    public async Task Webhooks_are_managed_and_the_secret_is_shown_once()
    {
        var w = await WorldAsync();
        var hooks = $"{w.P}/webhooks";
        var created = await Post(w.Carol, hooks, new { url = "https://example.com/hook", events = new[] { "push", "issue.created" } });
        Is(HttpStatusCode.Created, created);
        var secret = created.Str("secret");
        Assert.True(secret.Length >= 32);
        Assert.True(created["active"].GetBoolean());
        var id = created["id"].GetInt32();
        Assert.NotEmpty(await AuditAsync("webhook.created", w.Project.Id));

        var listed = await Get(w.Carol, hooks);
        Assert.Equal(1, listed.Json.GetArrayLength());
        Assert.DoesNotContain(secret, listed.Text);
        Assert.StartsWith("••••••••", listed.Json[0].GetProperty("secret").GetString());

        var updated = await Patch(w.Carol, $"{hooks}/{id}", new { active = false, events = new[] { "push" } });
        Is(HttpStatusCode.OK, updated);
        Assert.False(updated["active"].GetBoolean());
        Assert.Equal(1, updated["events"].GetArrayLength());
        Assert.Equal("https://example.com/hook", updated.Str("url"));
        Assert.DoesNotContain(secret, updated.Text);

        var rotated = await Post(w.Carol, $"{hooks}/{id}/rotate-secret");
        Is(HttpStatusCode.OK, rotated);
        Assert.NotEqual(secret, rotated.Str("secret"));
        Assert.NotEmpty(await AuditAsync("webhook.secret_rotated", w.Project.Id));

        Is(HttpStatusCode.Accepted, await Post(w.Carol, $"{hooks}/{id}/ping"));
        Is(HttpStatusCode.OK, await Get(w.Carol, $"{hooks}/{id}/deliveries"));

        Is(HttpStatusCode.Forbidden, await Get(w.Bob, hooks));
        Is(HttpStatusCode.Forbidden, await Post(w.Bob, hooks, new { url = "https://example.com/x", events = new[] { "push" } }));
        Is(HttpStatusCode.NotFound, await Get(w.Outsider, hooks));
        Is(HttpStatusCode.NotFound, await Patch(w.Carol, $"{hooks}/999999", new { active = true }));
        Is(HttpStatusCode.NoContent, await Delete(w.Carol, $"{hooks}/{id}"));
        Is(HttpStatusCode.NotFound, await Delete(w.Carol, $"{hooks}/{id}"));
        Assert.NotEmpty(await AuditAsync("webhook.deleted", w.Project.Id));
    }

    [Fact]
    public async Task Webhooks_reject_bad_urls_events_and_unknown_properties()
    {
        var w = await WorldAsync();
        var hooks = $"{w.P}/webhooks";
        Is(HttpStatusCode.UnprocessableEntity, await Post(w.Carol, hooks, new { url = "ftp://example.com/hook", events = new[] { "push" } }));
        Is(HttpStatusCode.UnprocessableEntity, await Post(w.Carol, hooks, new { url = "not a url", events = new[] { "push" } }));
        Is(HttpStatusCode.UnprocessableEntity, await Post(w.Carol, hooks, new { url = "https://example.com/h", events = new[] { "nonsense" } }));
        Is(HttpStatusCode.UnprocessableEntity, await Post(w.Carol, hooks, new { url = "https://example.com/h", events = Array.Empty<string>() }));
        Is(HttpStatusCode.UnprocessableEntity, await Post(w.Carol, hooks, new { url = "https://example.com/h", events = new[] { "push" }, secret = "short" }));
        Is(HttpStatusCode.UnprocessableEntity, await Post(w.Carol, hooks, new { events = new[] { "push" } }));
        Is(HttpStatusCode.UnprocessableEntity, await Post(w.Carol, hooks, new { url = "https://example.com/h" }));
        Is(HttpStatusCode.BadRequest, await Post(w.Carol, hooks, new { url = "https://example.com/h", events = new[] { "push" }, header = "x" }));
        Assert.Equal(0, (await Get(w.Carol, hooks)).Json.GetArrayLength());
    }

    // =============================================================================================== sprints

    [Fact]
    public async Task Sprints_can_be_created_started_completed_and_deleted()
    {
        var w = await WorldAsync();
        var sprints = $"{w.P}/sprints";
        var first = await Post(w.Carol, sprints, new { goal = "Ship it" });
        Is(HttpStatusCode.Created, first);
        Assert.Equal($"{w.Key} Sprint 1", first.Str("name"));
        Assert.Equal("planned", first.Str("state"));
        var firstId = first["id"].GetInt32();
        var second = await Post(w.Carol, sprints, new { name = "Next", startDate = "2031-01-01", endDate = "2031-01-14" });
        Assert.Equal("2031-01-14", second.Str("endDate"));
        var secondId = second["id"].GetInt32();

        var issue = await app.CreateIssueAsync(w.Project.Id, "alice", "Sprinted", storyPoints: 3, sprintId: firstId);
        var renamed = await Patch(w.Carol, $"{sprints}/{firstId}", new { name = "Alpha", goal = "" });
        Assert.Equal("Alpha", renamed.Str("name"));
        Assert.Equal(JsonValueKind.Null, renamed["goal"].ValueKind);

        Is(HttpStatusCode.UnprocessableEntity, await Post(w.Carol, $"{sprints}/{firstId}/start", new { }));
        Is(HttpStatusCode.UnprocessableEntity, await Post(w.Carol, $"{sprints}/{firstId}/start", new { startDate = "2031-02-10", endDate = "2031-02-01" }));
        var started = await Post(w.Carol, $"{sprints}/{firstId}/start", new { startDate = "2031-01-01", endDate = "2031-01-14", goal = "Go" });
        Is(HttpStatusCode.OK, started);
        Assert.Equal("active", started.Str("state"));
        Assert.Equal(1, started["committedCount"].GetInt32());
        Assert.Equal(3, started["committedPoints"].GetInt32());
        var again = await Post(w.Carol, $"{sprints}/{secondId}/start", new { startDate = "2031-02-01", endDate = "2031-02-14" });
        Is(HttpStatusCode.Conflict, again);
        Assert.Contains("already active", again.Text);
        Is(HttpStatusCode.UnprocessableEntity, await Post(w.Carol, $"{sprints}/{firstId}/start", new { startDate = "2031-01-01", endDate = "2031-01-14" })); // not planned any more

        Is(HttpStatusCode.UnprocessableEntity, await Post(w.Carol, $"{sprints}/{firstId}/complete", new { carryOver = "existingSprint" })); // no target
        Is(HttpStatusCode.BadRequest, await Post(w.Carol, $"{sprints}/{firstId}/complete", new { carryOver = "sideways" }));
        var done = await Post(w.Carol, $"{sprints}/{firstId}/complete", new { carryOver = "existingSprint", targetSprintId = secondId });
        Is(HttpStatusCode.OK, done);
        Assert.Equal("completed", done["sprint"].GetProperty("state").GetString());
        Assert.Equal(secondId, done["carriedTo"].GetProperty("id").GetInt32());
        Assert.Equal(secondId, (await Get(w.Carol, $"{w.P}/issues/{issue.Number}"))["sprintId"].GetInt32());
        Is(HttpStatusCode.UnprocessableEntity, await Post(w.Carol, $"{sprints}/{firstId}/complete")); // already completed
        Is(HttpStatusCode.UnprocessableEntity, await Delete(w.Carol, $"{sprints}/{firstId}")); // completed sprints are kept

        Is(HttpStatusCode.NoContent, await Delete(w.Carol, $"{sprints}/{secondId}"));
        Is(HttpStatusCode.NotFound, await Delete(w.Carol, $"{sprints}/{secondId}"));
        Assert.Equal(JsonValueKind.Null, (await Get(w.Carol, $"{w.P}/issues/{issue.Number}"))["sprintId"].ValueKind); // back in the backlog
    }

    [Fact]
    public async Task Completing_a_sprint_can_carry_open_issues_to_a_new_sprint()
    {
        var w = await WorldAsync();
        var sprint = await Post(w.Carol, $"{w.P}/sprints", new { name = "Solo" });
        var id = sprint["id"].GetInt32();
        await app.CreateIssueAsync(w.Project.Id, "alice", "Unfinished", sprintId: id);
        Is(HttpStatusCode.OK, await Post(w.Carol, $"{w.P}/sprints/{id}/start", new { startDate = "2031-05-01", endDate = "2031-05-14" }));
        var done = await Post(w.Carol, $"{w.P}/sprints/{id}/complete", new { carryOver = "newSprint", newSprintName = "Carried" });
        Is(HttpStatusCode.OK, done);
        Assert.Equal("Carried", done["carriedTo"].GetProperty("name").GetString());
        Assert.Equal(1, done["carriedTo"].GetProperty("issueCount").GetInt32());
        var plain = await Post(w.Carol, $"{w.P}/sprints", new { name = "Backlog bound" });
        Is(HttpStatusCode.OK, await Post(w.Carol, $"{w.P}/sprints/{plain["id"].GetInt32()}/start", new { startDate = "2031-06-01", endDate = "2031-06-14" }));
        var toBacklog = await Send(w.Carol, HttpMethod.Post, $"{w.P}/sprints/{plain["id"].GetInt32()}/complete"); // no body at all
        Is(HttpStatusCode.OK, toBacklog);
        Assert.Equal(JsonValueKind.Null, toBacklog["carriedTo"].ValueKind);
    }

    [Fact]
    public async Task Sprint_management_is_denied_validated_and_hidden()
    {
        var w = await WorldAsync();
        Is(HttpStatusCode.Forbidden, await Post(w.Bob, $"{w.P}/sprints", new { name = "Dev sprint" }));
        Is(HttpStatusCode.Forbidden, await Post(w.Dan, $"{w.P}/sprints", new { name = "Viewer sprint" }));
        Is(HttpStatusCode.NotFound, await Post(w.Outsider, $"{w.P}/sprints", new { name = "Spy sprint" }));
        Is(HttpStatusCode.UnprocessableEntity, await Post(w.Carol, $"{w.P}/sprints", new { name = "Backwards", startDate = "2031-02-10", endDate = "2031-02-01" }));
        Is(HttpStatusCode.BadRequest, await Post(w.Carol, $"{w.P}/sprints", new { name = "Typo", goals = "x" }));
        var s = await Post(w.Carol, $"{w.P}/sprints", new { name = "Mine" });
        var id = s["id"].GetInt32();
        Is(HttpStatusCode.Forbidden, await Patch(w.Bob, $"{w.P}/sprints/{id}", new { name = "Hijack" }));
        Is(HttpStatusCode.Forbidden, await Post(w.Bob, $"{w.P}/sprints/{id}/start", new { startDate = "2031-02-01", endDate = "2031-02-14" }));
        Is(HttpStatusCode.Forbidden, await Delete(w.Bob, $"{w.P}/sprints/{id}"));
        Is(HttpStatusCode.NotFound, await Patch(w.Carol, $"{w.P}/sprints/999999", new { name = "x" }));
        Is(HttpStatusCode.NotFound, await Post(w.Carol, $"{w.P}/sprints/999999/complete"));
        // Another project's sprint id is not reachable through this project.
        var other = await WorldAsync();
        var foreign = await Post(other.Carol, $"{other.P}/sprints", new { name = "Foreign" });
        Is(HttpStatusCode.NotFound, await Patch(w.Carol, $"{w.P}/sprints/{foreign["id"].GetInt32()}", new { name = "Stolen" }));
        Is(HttpStatusCode.NotFound, await Delete(w.Carol, $"{w.P}/sprints/{foreign["id"].GetInt32()}"));
    }

    // =============================================================================================== boards

    [Fact]
    public async Task Boards_are_created_configured_and_deleted_by_maintainers()
    {
        var w = await WorldAsync();
        var boards = $"{w.P}/boards";
        var created = await Post(w.Carol, boards, new { name = "Team board", type = "scrum" });
        Is(HttpStatusCode.Created, created);
        Assert.Equal("scrum", created.Str("type"));
        Assert.False(created["isDefault"].GetBoolean());
        var id = created["id"].GetInt32();

        var statuses = (await Get(w.Carol, $"{w.P}/workflow"))["statuses"].EnumerateArray().Select(s => s.GetProperty("id").GetInt32()).ToList();
        var updated = await Patch(w.Carol, $"{boards}/{id}", new { name = "Renamed board", swimlane = "assignee", blockOverWip = true, cardFields = new[] { "key", "assignee" }, filterQuery = "type=bug" });
        Is(HttpStatusCode.OK, updated);
        Assert.Equal("Renamed board", updated.Str("name"));
        Assert.Equal("assignee", updated.Str("swimlane"));
        var full = await Get(w.Carol, $"{boards}/{id}");
        Assert.Equal("type=bug", full.Str("filterQuery"));
        Assert.True(full["blockOverWip"].GetBoolean());
        Is(HttpStatusCode.UnprocessableEntity, await Patch(w.Carol, $"{boards}/{id}", new { cardFields = new[] { "sparkles" } }));
        Is(HttpStatusCode.UnprocessableEntity, await Patch(w.Carol, $"{boards}/{id}", new { doneDays = 0 }));
        Is(HttpStatusCode.BadRequest, await Patch(w.Carol, $"{boards}/{id}", new { colour = "red" }));

        var columns = await Put(w.Carol, $"{boards}/{id}/columns", new
        {
            columns = new object[] { new { name = "Waiting", wipLimit = 3, statusIds = statuses.Take(1) }, new { name = "Rest", statusIds = statuses.Skip(1) } },
        });
        Is(HttpStatusCode.OK, columns);
        var shown = await Get(w.Carol, $"{boards}/{id}");
        Assert.False(shown["followsWorkflow"].GetBoolean());
        Assert.Equal(2, shown["columns"].GetArrayLength());
        Assert.Equal(3, shown["columns"][0].GetProperty("wipLimit").GetInt32());
        Is(HttpStatusCode.UnprocessableEntity, await Put(w.Carol, $"{boards}/{id}/columns", new { columns = new[] { new { name = "Dup", statusIds = statuses }, new { name = "Dup2", statusIds = statuses } } }));
        Is(HttpStatusCode.UnprocessableEntity, await Put(w.Carol, $"{boards}/{id}/columns", new { columns = Array.Empty<object>() }));
        Is(HttpStatusCode.UnprocessableEntity, await Put(w.Carol, $"{boards}/{id}/columns", new { columns = new[] { new { name = "Ghost", statusIds = new[] { 999999 } } } }));
        Is(HttpStatusCode.UnprocessableEntity, await Put(w.Carol, $"{boards}/{id}/columns", new { }));

        Is(HttpStatusCode.OK, await Post(w.Carol, $"{boards}/{id}/reset-columns"));
        Assert.True((await Get(w.Carol, $"{boards}/{id}"))["followsWorkflow"].GetBoolean());

        var quick = await Put(w.Carol, $"{boards}/{id}/quick-filters", new { quickFilters = new[] { new { name = "Mine", query = "assigneeId=me" } } });
        Is(HttpStatusCode.OK, quick);
        Assert.Equal(1, (await Get(w.Carol, $"{boards}/{id}"))["quickFilters"].GetArrayLength());
        Is(HttpStatusCode.UnprocessableEntity, await Put(w.Carol, $"{boards}/{id}/quick-filters", new { quickFilters = new[] { new { name = "No query" } } }));

        var made = await Post(w.Carol, $"{boards}/{id}/make-default");
        Assert.True(made["isDefault"].GetBoolean());
        var defaultOnes = (await Get(w.Carol, boards)).Json.EnumerateArray().Count(b => b.GetProperty("isDefault").GetBoolean());
        Assert.Equal(1, defaultOnes);
        var refused = await Delete(w.Carol, $"{boards}/{id}");
        Is(HttpStatusCode.UnprocessableEntity, refused);
        Assert.Contains("default board", refused.Text);
        var oldDefault = (await Get(w.Carol, boards)).Json.EnumerateArray().First(b => !b.GetProperty("isDefault").GetBoolean()).GetProperty("id").GetInt32();
        Is(HttpStatusCode.NoContent, await Delete(w.Carol, $"{boards}/{oldDefault}"));
        Assert.NotEmpty(await AuditAsync("board.created", w.Project.Id));
        Assert.NotEmpty(await AuditAsync("board.deleted", w.Project.Id));
    }

    [Fact]
    public async Task Board_management_is_denied_hidden_and_validated()
    {
        var w = await WorldAsync();
        var boards = $"{w.P}/boards";
        Is(HttpStatusCode.Forbidden, await Post(w.Bob, boards, new { name = "Dev board" }));
        Is(HttpStatusCode.NotFound, await Post(w.Outsider, boards, new { name = "Spy board" }));
        Is(HttpStatusCode.UnprocessableEntity, await Post(w.Carol, boards, new { }));
        Is(HttpStatusCode.UnprocessableEntity, await Post(w.Carol, boards, new { name = new string('x', 81) }));
        Is(HttpStatusCode.BadRequest, await Post(w.Carol, boards, new { name = "Typo", kind = "scrum" }));
        var b = await Post(w.Carol, boards, new { name = "Plain" });
        var id = b["id"].GetInt32();
        Is(HttpStatusCode.Forbidden, await Patch(w.Dan, $"{boards}/{id}", new { name = "Viewer" }));
        Is(HttpStatusCode.Forbidden, await Delete(w.Bob, $"{boards}/{id}"));
        Is(HttpStatusCode.Forbidden, await Post(w.Bob, $"{boards}/{id}/make-default"));
        Is(HttpStatusCode.NotFound, await Patch(w.Carol, $"{boards}/999999", new { name = "x" }));
        var foreign = await WorldAsync();
        var fb = await Post(foreign.Carol, $"{foreign.P}/boards", new { name = "Foreign" });
        Is(HttpStatusCode.NotFound, await Delete(w.Carol, $"{boards}/{fb["id"].GetInt32()}"));
        Is(HttpStatusCode.NoContent, await Delete(w.Carol, $"{boards}/{id}"));
    }

    // =============================================================================================== cross-cutting

    [Fact]
    public async Task Every_management_write_route_refuses_a_read_only_token()
    {
        var w = await WorldAsync();
        var readOnly = await app.ApiClientAsync("alice", TokenScopes.ReadApi | TokenScopes.ReadRepository);
        var calls = new (HttpMethod Method, string Url)[]
        {
            (HttpMethod.Patch, w.P), (HttpMethod.Delete, $"{w.P}?confirm={w.Key}"), (HttpMethod.Post, $"{w.P}/members"), (HttpMethod.Patch, $"{w.P}/members/bob"),
            (HttpMethod.Delete, $"{w.P}/members/bob"), (HttpMethod.Post, $"{w.P}/labels"), (HttpMethod.Post, $"{w.P}/components"), (HttpMethod.Post, $"{w.P}/versions"),
            (HttpMethod.Post, $"{w.P}/fields"), (HttpMethod.Post, $"{w.P}/workflow/statuses"), (HttpMethod.Put, $"{w.P}/workflow/transitions"),
            (HttpMethod.Post, $"{w.P}/repos"), (HttpMethod.Post, $"{w.P}/repo-imports"), (HttpMethod.Post, $"{w.P}/webhooks"), (HttpMethod.Post, $"{w.P}/sprints"),
            (HttpMethod.Post, $"{w.P}/boards"), (HttpMethod.Post, "/api/v1/groups"), (HttpMethod.Post, $"{w.P}/repos/x/branch-protection"),
            (HttpMethod.Put, $"{w.P}/repos/x/mirrors/pull"), (HttpMethod.Delete, $"{w.P}/repos/x?confirm=x"),
        };
        foreach (var (method, url) in calls)
        {
            var r = await Send(readOnly, method, url, method == HttpMethod.Delete ? null : new { });
            Assert.True(r.Status == HttpStatusCode.Forbidden && r.Text.Contains("write_api"), $"{method} {url} → {(int)r.Status}: {r.Text}");
        }
    }

    [Fact]
    public async Task Management_routes_reject_unauthenticated_callers_and_wrong_content_types()
    {
        var w = await WorldAsync();
        var anonymous = app.CreateClient();
        Is(HttpStatusCode.Unauthorized, await Post(anonymous, "/api/v1/projects", new { key = "ANON1", name = "x" }));
        Is(HttpStatusCode.Unauthorized, await Post(anonymous, $"{w.P}/sprints", new { }));
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{w.P}/labels") { Content = new StringContent("name=x", Encoding.UTF8, "application/x-www-form-urlencoded") };
        using var response = await w.Carol.SendAsync(request);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
    }
}
