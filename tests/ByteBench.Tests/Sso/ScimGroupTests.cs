using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using ByteBench.Data;
using ByteBench.Services;
using ByteBench.Services.Scim;
using Microsoft.EntityFrameworkCore;

namespace ByteBench.Tests.Sso;

/// <summary>Writable SCIM groups: Owner is configured as the member role (to prove the cap), and one mapping adopts an existing group.</summary>
public class ScimGroupTestApp : ScimTestApp
{
    public const string MappedDisplayName = "Mapped Platform Team";
    public const string MappedPath = "scim-mapped-platform";

    protected override void AddSettings(IDictionary<string, string?> s)
    {
        base.AddSettings(s);
        s["Scim:GroupMemberRole"] = "Owner"; // capped to Maintainer
        s["Scim:GroupMappings:0:DisplayName"] = MappedDisplayName;
        s["Scim:GroupMappings:0:Group"] = MappedPath;
    }
}

public class ScimGroupTests(ScimGroupTestApp app) : IClassFixture<ScimGroupTestApp>
{
    private const string PatchSchema = ScimUserService.PatchSchema;

    private static StringContent Body(object o) => new(System.Text.Json.JsonSerializer.Serialize(o), Encoding.UTF8, "application/scim+json");
    private static StringContent Body(string json) => new(json, Encoding.UTF8, "application/scim+json");
    private static async Task<JsonObject> JsonAsync(HttpResponseMessage r) => JsonNode.Parse(await r.Content.ReadAsStringAsync())!.AsObject();
    private async Task<HttpClient> ClientAsync() => app.Scim(await app.EnableAsync());
    private static string Unique(string prefix) => prefix + " " + Guid.NewGuid().ToString("N")[..6];

    private async Task<int> NewUserAsync() => (await app.CreateUserAsync("sg" + Guid.NewGuid().ToString("N")[..8])).Id;

    private static List<int> MemberIds(JsonObject group) => group["members"]!.AsArray().Select(m => int.Parse(m!["value"]!.GetValue<string>())).Order().ToList();

    private Dictionary<int, ProjectRole> Members(int groupId)
    {
        using var db = app.Db();
        return db.GroupMembers.Where(m => m.GroupId == groupId).ToDictionary(m => m.UserId, m => m.Role);
    }

    private async Task<(int Id, JsonObject Body)> CreateGroupAsync(HttpClient c, object body)
    {
        var r = await c.PostAsync("/scim/v2/Groups", Body(body));
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        var json = await JsonAsync(r);
        return (int.Parse(json["id"]!.GetValue<string>()), json);
    }

    [Fact]
    public async Task Entra_style_create_add_remove_rename_and_filter()
    {
        var c = await ClientAsync();
        var name = Unique("Entra Engineering");
        var r = await c.PostAsync("/scim/v2/Groups", Body(new { schemas = new[] { ScimUserService.GroupSchema }, displayName = name, externalId = "entra-" + name, members = Array.Empty<object>() }));
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        Assert.Equal("application/scim+json", r.Content.Headers.ContentType!.MediaType);
        var created = await JsonAsync(r);
        var id = int.Parse(created["id"]!.GetValue<string>());
        Assert.EndsWith("/scim/v2/Groups/" + id, r.Headers.Location!.ToString());
        Assert.Equal(name, created["displayName"]!.GetValue<string>());
        Assert.Equal("entra-" + name, created["externalId"]!.GetValue<string>());

        // A top-level, Private group with a derived path and nobody as Owner.
        using (var db = app.Db())
        {
            var g = db.Groups.Single(x => x.Id == id);
            Assert.Null(g.ParentId);
            Assert.Equal(ProjectVisibility.Private, g.Visibility);
            Assert.StartsWith("entra-engineering-", g.FullPath);
            Assert.Equal(name, g.Name);
            Assert.Empty(db.GroupMembers.Where(m => m.GroupId == id));
        }

        var u1 = await NewUserAsync();
        var u2 = await NewUserAsync();
        // Entra: "Add" members with a value list (capitalised op).
        r = await c.PatchAsync($"/scim/v2/Groups/{id}", Body($$"""{"schemas":["{{PatchSchema}}"],"Operations":[{"op":"Add","path":"members","value":[{"value":"{{u1}}"},{"value":"{{u2}}"}]}]}"""));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal(new[] { u1, u2 }.Order().ToList(), MemberIds(await JsonAsync(r)));
        // Scim:GroupMemberRole is Owner in this fixture, but SCIM never grants more than Maintainer.
        Assert.All(Members(id), m => Assert.Equal(ProjectRole.Maintainer, m.Value));

        // Entra: "Remove" with a value list; "Replace" displayName.
        r = await c.PatchAsync($"/scim/v2/Groups/{id}", Body($$"""{"schemas":["{{PatchSchema}}"],"Operations":[{"op":"Remove","path":"members","value":[{"value":"{{u1}}"}]},{"op":"Replace","path":"displayName","value":"{{name}} Renamed"}]}"""));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var patched = await JsonAsync(r);
        Assert.Equal(new List<int> { u2 }, MemberIds(patched));
        Assert.Equal(name + " Renamed", patched["displayName"]!.GetValue<string>());
        using (var db = app.Db()) Assert.Equal(name + " Renamed", db.Groups.Single(x => x.Id == id).Name);

        // Filters: displayName eq (case-insensitive) and externalId eq.
        var byName = await JsonAsync(await c.GetAsync("/scim/v2/Groups?filter=" + Uri.EscapeDataString($"displayName eq \"{(name + " Renamed").ToUpperInvariant()}\"")));
        Assert.Equal(1, byName["totalResults"]!.GetValue<int>());
        Assert.Equal(id.ToString(), byName["Resources"]![0]!["id"]!.GetValue<string>());
        var byExternal = await JsonAsync(await c.GetAsync("/scim/v2/Groups?filter=" + Uri.EscapeDataString($"externalId eq \"entra-{name}\"")));
        Assert.Equal(1, byExternal["totalResults"]!.GetValue<int>());
        var page = await JsonAsync(await c.GetAsync("/scim/v2/Groups?startIndex=1&count=1"));
        Assert.Equal(1, page["itemsPerPage"]!.GetValue<int>());
        Assert.True(page["totalResults"]!.GetValue<int>() >= 1);

        using (var db = app.Db())
        {
            var events = await db.AuditEvents.Where(e => e.ActorName == "SCIM").ToListAsync();
            Assert.Contains(events, e => e.Action == "scim.group_created" && e.TargetId == id.ToString() && e.ActorId == null);
            Assert.Contains(events, e => e.Action == "scim.group_updated" && e.TargetId == id.ToString());
            Assert.Contains(events, e => e.Action == "scim.group_member_added" && e.TargetId == u1.ToString());
            Assert.Contains(events, e => e.Action == "scim.group_member_removed" && e.TargetId == u1.ToString());
        }
    }

    [Fact]
    public async Task Okta_style_create_with_members_pathless_replace_value_filter_remove_and_put()
    {
        var c = await ClientAsync();
        var (a, b, d) = (await NewUserAsync(), await NewUserAsync(), await NewUserAsync());
        var name = Unique("Okta Ops");
        var (id, created) = await CreateGroupAsync(c, new { schemas = new[] { ScimUserService.GroupSchema }, displayName = name, members = new[] { new { value = a.ToString(), display = "a" }, new { value = b.ToString(), display = "b" } } });
        Assert.Equal(2, MemberIds(created).Count);

        // Duplicate displayName: 409 uniqueness.
        var dup = await c.PostAsync("/scim/v2/Groups", Body(new { displayName = name.ToLowerInvariant() }));
        Assert.Equal(HttpStatusCode.Conflict, dup.StatusCode);
        Assert.Equal("uniqueness", (await JsonAsync(dup))["scimType"]!.GetValue<string>());

        // Okta: path-less replace of id + displayName.
        var r = await c.PatchAsync($"/scim/v2/Groups/{id}", Body(new { schemas = new[] { PatchSchema }, Operations = new object[] { new { op = "replace", value = new { id = id.ToString(), displayName = name + " 2" } } } }));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal(name + " 2", (await JsonAsync(r))["displayName"]!.GetValue<string>());

        // Okta: remove one member by value filter.
        r = await c.PatchAsync($"/scim/v2/Groups/{id}", Body(new { schemas = new[] { PatchSchema }, Operations = new object[] { new { op = "remove", path = $"members[value eq \"{a}\"]" } } }));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal(new List<int> { b }, MemberIds(await JsonAsync(r)));

        // PUT replaces the member list.
        r = await c.PutAsync($"/scim/v2/Groups/{id}", Body(new { schemas = new[] { ScimUserService.GroupSchema }, displayName = name + " 2", members = new[] { new { value = d.ToString() } } }));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal(new List<int> { d }, MemberIds(await JsonAsync(r)));
        Assert.Equal(new List<int> { d }, Members(id).Keys.ToList());

        // Malformed ops are refused.
        r = await c.PatchAsync($"/scim/v2/Groups/{id}", Body(new { schemas = new[] { PatchSchema }, Operations = new object[] { new { op = "add", path = "members", value = new[] { new { value = "not-an-id" } } } } }));
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        r = await c.PatchAsync($"/scim/v2/Groups/{id}", Body(new { schemas = new[] { PatchSchema }, Operations = new object[] { new { op = "add", path = "members", value = new[] { new { value = "999999" } } } } }));
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        r = await c.PatchAsync($"/scim/v2/Groups/{id}", Body(new { schemas = new[] { PatchSchema }, Operations = new object[] { new { op = "move", path = "members" } } }));
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.PatchAsync("/scim/v2/Groups/999999", Body(new { Operations = new object[] { new { op = "remove", path = "members" } } }))).StatusCode);
        var noName = await c.PostAsync("/scim/v2/Groups", Body(new { externalId = "x" }));
        Assert.Equal(HttpStatusCode.BadRequest, noName.StatusCode);
    }

    [Fact]
    public async Task Scim_cannot_escalate_roles_or_remove_members_above_its_role()
    {
        var c = await ClientAsync();
        var (id, _) = await CreateGroupAsync(c, new { displayName = Unique("Escalation") });
        var viewer = await NewUserAsync();
        var owner = await NewUserAsync();
        // An administrator adds a Viewer and an Owner in ByteBench.
        await using (var s = app.Scope(app.UserId("alice")))
        {
            var groups = s.Get<GroupService>();
            await groups.AddMemberAsync(id, app.Db().Users.Single(u => u.Id == viewer).UserName!, ProjectRole.Viewer);
            await groups.AddMemberAsync(id, app.Db().Users.Single(u => u.Id == owner).UserName!, ProjectRole.Owner);
        }

        // Adding an existing member again doesn't change their role.
        var r = await c.PatchAsync($"/scim/v2/Groups/{id}", Body(new { schemas = new[] { PatchSchema }, Operations = new object[] { new { op = "add", path = "members", value = new[] { new { value = viewer.ToString() } } } } }));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal(ProjectRole.Viewer, Members(id)[viewer]);

        // SCIM may not remove an Owner, neither explicitly nor by a replace that leaves them out.
        r = await c.PatchAsync($"/scim/v2/Groups/{id}", Body(new { schemas = new[] { PatchSchema }, Operations = new object[] { new { op = "remove", path = $"members[value eq \"{owner}\"]" } } }));
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        r = await c.PutAsync($"/scim/v2/Groups/{id}", Body(new { displayName = "Escalation replaced", members = new[] { new { value = viewer.ToString() } } }));
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        Assert.Equal(ProjectRole.Owner, Members(id)[owner]);
        // Refused as a whole: the displayName in the refused PUT wasn't applied.
        Assert.NotEqual("Escalation replaced", (await JsonAsync(await c.GetAsync($"/scim/v2/Groups/{id}")))["displayName"]!.GetValue<string>());

        // The Viewer can be removed (at or below SCIM's role).
        r = await c.PatchAsync($"/scim/v2/Groups/{id}", Body(new { schemas = new[] { PatchSchema }, Operations = new object[] { new { op = "remove", path = $"members[value eq \"{viewer}\"]" } } }));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.False(Members(id).ContainsKey(viewer));
    }

    [Fact]
    public async Task Site_admins_and_deactivated_users_are_protected_and_requests_are_all_or_nothing()
    {
        var c = await ClientAsync();
        var (id, _) = await CreateGroupAsync(c, new { displayName = Unique("Protected") });
        var alice = app.UserId("alice"); // a site admin
        var plain = await NewUserAsync();

        var r = await c.PatchAsync($"/scim/v2/Groups/{id}", Body(new { schemas = new[] { PatchSchema }, Operations = new object[] { new { op = "add", path = "members", value = new[] { new { value = plain.ToString() }, new { value = alice.ToString() } } } } }));
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        Assert.Empty(Members(id)); // the plain user wasn't added either

        // An admin added in ByteBench can't be removed by SCIM.
        await using (var s = app.Scope(alice)) await s.Get<GroupService>().AddMemberAsync(id, "alice", ProjectRole.Developer);
        r = await c.PatchAsync($"/scim/v2/Groups/{id}", Body(new { schemas = new[] { PatchSchema }, Operations = new object[] { new { op = "remove", path = "members" } } }));
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        Assert.True(Members(id).ContainsKey(alice));

        // A deactivated account can't be added (and a create that names one creates nothing).
        var gone = await NewUserAsync();
        Assert.Equal(HttpStatusCode.NoContent, (await c.DeleteAsync($"/scim/v2/Users/{gone}")).StatusCode);
        r = await c.PatchAsync($"/scim/v2/Groups/{id}", Body(new { schemas = new[] { PatchSchema }, Operations = new object[] { new { op = "add", path = "members", value = new[] { new { value = gone.ToString() } } } } }));
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Equal("invalidValue", (await JsonAsync(r))["scimType"]!.GetValue<string>());
        var name = Unique("Never made");
        var post = await c.PostAsync("/scim/v2/Groups", Body(new { displayName = name, members = new[] { new { value = gone.ToString() } } }));
        Assert.Equal(HttpStatusCode.BadRequest, post.StatusCode);
        using var db = app.Db();
        Assert.False(db.ScimGroups.Any(g => g.DisplayName == name));
        Assert.False(db.Groups.Any(g => g.Name == name));
    }

    [Fact]
    public async Task Delete_refuses_groups_with_projects_or_subgroups_and_removes_empty_ones()
    {
        var c = await ClientAsync();
        var alice = app.UserId("alice");

        var (withSub, _) = await CreateGroupAsync(c, new { displayName = Unique("Has subgroup") });
        await using (var s = app.Scope(alice))
            await s.Get<GroupService>().CreateAsync("Sub", "sub", null, ProjectVisibility.Private, parentId: withSub);
        var del = await c.DeleteAsync($"/scim/v2/Groups/{withSub}");
        Assert.Equal(HttpStatusCode.Conflict, del.StatusCode);
        Assert.Equal(ScimEndpoints.ErrorSchema, (await JsonAsync(del))["schemas"]![0]!.GetValue<string>());

        var (withProject, _) = await CreateGroupAsync(c, new { displayName = Unique("Has project") });
        var project = await app.CreateProjectAsync("alice");
        await using (var s = app.Scope(alice)) await s.Get<GroupService>().TransferProjectAsync(project.Id, withProject);
        Assert.Equal(HttpStatusCode.Conflict, (await c.DeleteAsync($"/scim/v2/Groups/{withProject}")).StatusCode);
        using (var db = app.Db()) Assert.True(db.Groups.Any(g => g.Id == withProject));

        var member = await NewUserAsync();
        var (empty, _) = await CreateGroupAsync(c, new { displayName = Unique("Empty"), members = new[] { new { value = member.ToString() } } });
        Assert.Equal(HttpStatusCode.NoContent, (await c.DeleteAsync($"/scim/v2/Groups/{empty}")).StatusCode);
        using (var db = app.Db())
        {
            Assert.False(db.Groups.Any(g => g.Id == empty));
            Assert.False(db.GroupMembers.Any(m => m.GroupId == empty));
            Assert.False(db.ScimGroups.Any(g => g.GroupId == empty));
            Assert.True(db.AuditEvents.Any(e => e.Action == "scim.group_deleted" && e.TargetId == empty.ToString() && e.ActorName == "SCIM"));
        }
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync($"/scim/v2/Groups/{empty}")).StatusCode);
    }

    [Fact]
    public async Task A_mapped_group_is_adopted_and_delete_only_drops_the_association()
    {
        var c = await ClientAsync();
        var alice = app.UserId("alice");
        int groupId;
        await using (var s = app.Scope(alice))
            groupId = (await s.Get<GroupService>().CreateAsync("Platform", ScimGroupTestApp.MappedPath, null, ProjectVisibility.Private)).Id;

        // Unmapped and not yet adopted: not writable.
        Assert.Equal(HttpStatusCode.Forbidden, (await c.PatchAsync($"/scim/v2/Groups/{groupId}", Body(new { Operations = new object[] { new { op = "remove", path = "members" } } }))).StatusCode);

        var member = await NewUserAsync();
        var (id, body) = await CreateGroupAsync(c, new { displayName = ScimGroupTestApp.MappedDisplayName.ToUpperInvariant(), members = new[] { new { value = member.ToString() } } });
        Assert.Equal(groupId, id);
        var members = Members(groupId);
        Assert.Equal(ProjectRole.Owner, members[alice]); // the creator's membership is untouched
        Assert.Equal(ProjectRole.Maintainer, members[member]);
        Assert.Contains(alice, MemberIds(body));

        // Renaming through SCIM changes only what SCIM sees: the ByteBench group keeps its name and path.
        var r = await c.PatchAsync($"/scim/v2/Groups/{id}", Body(new { Operations = new object[] { new { op = "replace", path = "displayName", value = "Platform (IdP)" } } }));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        using (var db = app.Db()) Assert.Equal("Platform", db.Groups.Single(g => g.Id == groupId).Name);

        Assert.Equal(HttpStatusCode.NoContent, (await c.DeleteAsync($"/scim/v2/Groups/{id}")).StatusCode);
        using (var db = app.Db())
        {
            Assert.True(db.Groups.Any(g => g.Id == groupId));
            Assert.False(db.ScimGroups.Any(g => g.GroupId == groupId));
            Assert.True(db.GroupMembers.Any(m => m.GroupId == groupId && m.UserId == member));
        }
        Assert.Equal(HttpStatusCode.Forbidden, (await c.PatchAsync($"/scim/v2/Groups/{groupId}", Body(new { Operations = new object[] { new { op = "remove", path = "members" } } }))).StatusCode);
    }
}
