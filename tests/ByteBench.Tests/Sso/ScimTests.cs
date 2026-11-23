using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using ByteBench.Data;
using ByteBench.Services;
using ByteBench.Services.Scim;
using ByteBench.Tests.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;

namespace ByteBench.Tests.Sso;

/// <summary>SCIM with a high failure limit (so the negative token tests don't trip the rate limiter for the others).</summary>
public class ScimTestApp : TestApp
{
    protected override void AddSettings(IDictionary<string, string?> s)
    {
        s["Scim:MaxFailuresPerIp"] = "1000";
        s["Scim:MaxBodyKilobytes"] = "8";
    }

    /// <summary>Switches SCIM on and creates a token (as the admin alice). Returns the plaintext token.</summary>
    public async Task<string> EnableAsync()
    {
        await using var s = Scope(UserId("alice"));
        var tokens = s.Get<ScimTokenService>();
        await tokens.SetEnabledAsync(true);
        return (await tokens.CreateAsync("test")).Plaintext;
    }

    public HttpClient Scim(string? token)
    {
        var c = CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });
        if (token != null) c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return c;
    }
}

/// <summary>SCIM 2.0: auth (token, switch, rate limit), Users CRUD, PATCH dialects, deactivation everywhere, read-only Groups.</summary>
public class ScimTests(ScimTestApp app) : IClassFixture<ScimTestApp>
{
    private static StringContent Body(object o) => new(System.Text.Json.JsonSerializer.Serialize(o), Encoding.UTF8, "application/scim+json");
    private static StringContent Body(string json) => new(json, Encoding.UTF8, "application/scim+json");

    private static async Task<JsonObject> JsonAsync(HttpResponseMessage r) => JsonNode.Parse(await r.Content.ReadAsStringAsync())!.AsObject();

    private async Task<HttpClient> ClientAsync() => app.Scim(await app.EnableAsync());

    private static object NewUser(string userName, string? email = null, bool active = true, string? externalId = null) => new
    {
        schemas = new[] { ScimUserService.UserSchema },
        userName,
        externalId = externalId ?? "ext-" + userName,
        name = new { givenName = "Pat", familyName = "Provisioned" },
        emails = new[] { new { value = email ?? userName, type = "work", primary = true } },
        active,
    };

    private static string NewName() => $"p{Guid.NewGuid():N}"[..10] + "@corp.example";

    private static void AssertScimError(JsonObject body, int status, string? scimType = null)
    {
        Assert.Equal(ScimEndpoints.ErrorSchema, body["schemas"]![0]!.GetValue<string>());
        Assert.Equal(status.ToString(), body["status"]!.GetValue<string>());
        if (scimType != null) Assert.Equal(scimType, body["scimType"]!.GetValue<string>());
    }

    // ------------------------------------------------------------------ auth

    [Fact]
    public async Task Scim_is_closed_until_switched_on_with_a_token_and_rejects_missing_wrong_and_revoked_tokens()
    {
        // A fresh fixture state can't be assumed (other tests enable it); check the switch explicitly.
        await using (var s = app.Scope(app.UserId("alice")))
            await s.Get<ScimTokenService>().SetEnabledAsync(false);
        var off = await app.Scim("bbscim_whatever").GetAsync("/scim/v2/Users");
        Assert.Equal(HttpStatusCode.Forbidden, off.StatusCode);
        Assert.Equal("application/scim+json", off.Content.Headers.ContentType!.MediaType);
        AssertScimError(await JsonAsync(off), 403);

        var token = await app.EnableAsync();
        Assert.Equal(HttpStatusCode.OK, (await app.Scim(token).GetAsync("/scim/v2/Users")).StatusCode);

        var missing = await app.Scim(null).GetAsync("/scim/v2/Users");
        Assert.Equal(HttpStatusCode.Unauthorized, missing.StatusCode);
        Assert.Contains("Bearer", missing.Headers.WwwAuthenticate.ToString());
        AssertScimError(await JsonAsync(missing), 401);
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.Scim(token + "x").GetAsync("/scim/v2/Users")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.Scim("gbp_not_a_scim_token").GetAsync("/scim/v2/Users")).StatusCode);

        // Only the hash is stored.
        using (var db = app.Db())
        {
            Assert.DoesNotContain(db.ScimTokens.ToList(), t => t.TokenHash == token || t.Prefix == token);
            Assert.Contains(db.ScimTokens.ToList(), t => t.TokenHash == ScimTokenService.Hash(token));
        }

        // Rotate: the old token stops working, the new one works. Revoke: it stops working.
        int id;
        using (var db = app.Db()) id = db.ScimTokens.Single(t => t.TokenHash == ScimTokenService.Hash(token)).Id;
        string rotated;
        await using (var s = app.Scope(app.UserId("alice"))) rotated = (await s.Get<ScimTokenService>().RotateAsync(id)).Plaintext;
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.Scim(token).GetAsync("/scim/v2/Users")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await app.Scim(rotated).GetAsync("/scim/v2/Users")).StatusCode);
        int rotatedId;
        using (var db = app.Db()) rotatedId = db.ScimTokens.Single(t => t.TokenHash == ScimTokenService.Hash(rotated)).Id;
        await using (var s = app.Scope(app.UserId("alice"))) await s.Get<ScimTokenService>().RevokeAsync(rotatedId);
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.Scim(rotated).GetAsync("/scim/v2/Users")).StatusCode);

        using (var db = app.Db())
            foreach (var action in new[] { "scim.enabled", "scim.disabled", "scim.token_created", "scim.token_rotated", "scim.token_revoked" })
                Assert.Contains(db.AuditEvents.ToList(), e => e.Action == action);
    }

    [Fact]
    public async Task Only_site_admins_manage_scim_tokens()
    {
        await using var s = app.Scope(app.UserId("bob"));
        var tokens = s.Get<ScimTokenService>();
        await Assert.ThrowsAsync<ForbiddenException>(() => tokens.CreateAsync("nope"));
        await Assert.ThrowsAsync<ForbiddenException>(() => tokens.SetEnabledAsync(true));
        await Assert.ThrowsAsync<ForbiddenException>(() => tokens.ListAsync());
    }

    // ------------------------------------------------------------------ discovery

    [Fact]
    public async Task Discovery_endpoints_describe_the_server()
    {
        var c = await ClientAsync();
        var spc = await JsonAsync(await c.GetAsync("/scim/v2/ServiceProviderConfig"));
        Assert.True(spc["patch"]!["supported"]!.GetValue<bool>());
        Assert.False(spc["bulk"]!["supported"]!.GetValue<bool>());
        Assert.True(spc["filter"]!["supported"]!.GetValue<bool>());
        var types = await JsonAsync(await c.GetAsync("/scim/v2/ResourceTypes"));
        Assert.Equal(2, types["totalResults"]!.GetValue<int>());
        var schemas = await JsonAsync(await c.GetAsync("/scim/v2/Schemas"));
        Assert.Contains(schemas["Resources"]!.AsArray(), r => r!["id"]!.GetValue<string>() == ScimUserService.UserSchema);
        var user = await c.GetAsync("/scim/v2/Schemas/" + ScimUserService.UserSchema);
        Assert.Equal(HttpStatusCode.OK, user.StatusCode);
        var unknown = await c.GetAsync("/scim/v2/Nope");
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        AssertScimError(await JsonAsync(unknown), 404);
    }

    // ------------------------------------------------------------------ users

    [Fact]
    public async Task Create_get_filter_page_and_replace_users()
    {
        var c = await ClientAsync();
        var userName = NewName();
        var created = await c.PostAsync("/scim/v2/Users", Body(NewUser(userName)));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal("application/scim+json", created.Content.Headers.ContentType!.MediaType);
        var body = await JsonAsync(created);
        var id = body["id"]!.GetValue<string>();
        Assert.Equal(userName, body["userName"]!.GetValue<string>());
        Assert.Equal("Pat Provisioned", body["displayName"]!.GetValue<string>());
        Assert.True(body["active"]!.GetValue<bool>());
        Assert.EndsWith("/scim/v2/Users/" + id, created.Headers.Location!.ToString());

        // The ByteBench account: a valid username from the email's local part, confirmed email, no password.
        var user = app.User(app.Db().Users.Single(u => u.Id == int.Parse(id)).UserName!);
        Assert.DoesNotContain("@", user.UserName);
        Assert.True(user.EmailConfirmed);
        Assert.Null(user.PasswordHash);

        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/scim/v2/Users/" + id)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync("/scim/v2/Users/999999")).StatusCode);

        var byName = await JsonAsync(await c.GetAsync($"/scim/v2/Users?filter={Uri.EscapeDataString($"userName eq \"{userName.ToUpperInvariant()}\"")}"));
        Assert.Equal(1, byName["totalResults"]!.GetValue<int>());
        Assert.Equal(id, byName["Resources"]![0]!["id"]!.GetValue<string>());
        var byEmail = await JsonAsync(await c.GetAsync($"/scim/v2/Users?filter={Uri.EscapeDataString($"emails.value eq \"{userName}\"")}"));
        Assert.Equal(1, byEmail["totalResults"]!.GetValue<int>());
        var byExternal = await JsonAsync(await c.GetAsync($"/scim/v2/Users?filter={Uri.EscapeDataString($"externalId eq \"ext-{userName}\"")}"));
        Assert.Equal(1, byExternal["totalResults"]!.GetValue<int>());
        var none = await JsonAsync(await c.GetAsync($"/scim/v2/Users?filter={Uri.EscapeDataString("userName eq \"nobody@nowhere\"")}"));
        Assert.Equal(0, none["totalResults"]!.GetValue<int>());
        Assert.Empty(none["Resources"]!.AsArray());

        // Existing (seed) accounts answer to their username too, so an IdP can match them.
        var alice = await JsonAsync(await c.GetAsync($"/scim/v2/Users?filter={Uri.EscapeDataString("userName eq \"alice\"")}"));
        Assert.Equal(1, alice["totalResults"]!.GetValue<int>());

        // Paging: startIndex is 1-based, count caps the page.
        var all = await JsonAsync(await c.GetAsync("/scim/v2/Users?startIndex=1&count=2"));
        Assert.Equal(ScimUserService.ListSchema, all["schemas"]![0]!.GetValue<string>());
        Assert.Equal(2, all["itemsPerPage"]!.GetValue<int>());
        var total = all["totalResults"]!.GetValue<int>();
        Assert.True(total > 2);
        var second = await JsonAsync(await c.GetAsync("/scim/v2/Users?startIndex=2&count=1"));
        Assert.Equal(all["Resources"]![1]!["id"]!.GetValue<string>(), second["Resources"]![0]!["id"]!.GetValue<string>());
        var countOnly = await JsonAsync(await c.GetAsync("/scim/v2/Users?count=0"));
        Assert.Equal(total, countOnly["totalResults"]!.GetValue<int>());
        Assert.Empty(countOnly["Resources"]!.AsArray());
        // System bot accounts aren't listed.
        Assert.DoesNotContain((await JsonAsync(await c.GetAsync("/scim/v2/Users?count=200")))["Resources"]!.AsArray(),
            r => ByteBench.Services.Accounts.SystemAccounts.IsSystem(r!["userName"]!.GetValue<string>()));

        // Duplicate userName: 409 uniqueness.
        var dup = await c.PostAsync("/scim/v2/Users", Body(NewUser(userName, NewName())));
        Assert.Equal(HttpStatusCode.Conflict, dup.StatusCode);
        AssertScimError(await JsonAsync(dup), 409, "uniqueness");

        // PUT replaces.
        var newEmail = NewName();
        var put = await c.PutAsync("/scim/v2/Users/" + id, Body(new { schemas = new[] { ScimUserService.UserSchema }, userName, displayName = "Renamed Person", emails = new[] { new { value = newEmail, primary = true } }, active = true }));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var replaced = await JsonAsync(put);
        Assert.Equal("Renamed Person", replaced["displayName"]!.GetValue<string>());
        Assert.Equal(newEmail, replaced["emails"]![0]!["value"]!.GetValue<string>());
        Assert.Null(replaced["externalId"]); // not in the PUT body: cleared

        using var db = app.Db();
        var events = db.AuditEvents.Where(e => e.TargetId == id).ToList();
        Assert.Contains(events, e => e.Action == "scim.user_created" && e.ActorName == "SCIM" && e.ActorId == null);
        Assert.Contains(events, e => e.Action == "scim.user_updated" && e.ActorName == "SCIM");
    }

    [Theory]
    [InlineData("userName eq")]
    [InlineData("userName eq \"x\" or userName eq \"y\"")]
    [InlineData("password eq \"x\"")]
    [InlineData("userName zz \"x\"")]
    [InlineData("(userName eq \"x\")")]
    [InlineData("userName eq \"unterminated")]
    [InlineData("emails[type eq \"work\"]")]
    public async Task Malformed_or_unsupported_filters_are_400_invalidFilter(string filter)
    {
        var r = await (await ClientAsync()).GetAsync("/scim/v2/Users?filter=" + Uri.EscapeDataString(filter));
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        AssertScimError(await JsonAsync(r), 400, "invalidFilter");
    }

    [Fact]
    public async Task Bad_bodies_are_rejected()
    {
        var c = await ClientAsync();
        var invalid = await c.PostAsync("/scim/v2/Users", Body("{not json"));
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        AssertScimError(await JsonAsync(invalid), 400, "invalidSyntax");
        var noUserName = await c.PostAsync("/scim/v2/Users", Body(new { schemas = new[] { ScimUserService.UserSchema }, emails = new[] { new { value = NewName() } } }));
        Assert.Equal(HttpStatusCode.BadRequest, noUserName.StatusCode);

        // Over Scim:MaxBodyKilobytes (8 KiB in this fixture): 413 before anything is parsed.
        var big = await c.PostAsync("/scim/v2/Users", Body(new { userName = NewName(), pad = new string('x', 20_000) }));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, big.StatusCode);
        AssertScimError(await JsonAsync(big), 413);
    }

    [Fact]
    public async Task Patch_supports_path_ops_okta_and_entra_styles_and_deactivation_blocks_everything()
    {
        var c = await ClientAsync();
        // A local account with a password and an access token, adopted by SCIM through its email.
        var local = await app.CreateUserAsync("scimp" + Guid.NewGuid().ToString("N")[..6]);
        var pat = await app.CreateTokenAsync(local.UserName!, TokenScopes.ReadApi);
        var adopt = await c.PostAsync("/scim/v2/Users", Body(NewUser(local.Email!, local.Email)));
        Assert.Equal(HttpStatusCode.Created, adopt.StatusCode);
        var id = (await JsonAsync(adopt))["id"]!.GetValue<string>();
        Assert.Equal(local.Id.ToString(), id);
        var stampBefore = app.User(local.UserName!).SecurityStamp;

        // Path ops: replace displayName, add name parts are ignored once displayName is set, replace an email by value filter.
        var newEmail = NewName();
        var r = await c.PatchAsync("/scim/v2/Users/" + id, Body(new
        {
            schemas = new[] { ScimUserService.PatchSchema },
            Operations = new object[]
            {
                new { op = "replace", path = "displayName", value = "Patched Name" },
                new { op = "replace", path = "emails[type eq \"work\"].value", value = newEmail },
                new { op = "add", path = "title", value = "ignored" },
            },
        }));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var patched = await JsonAsync(r);
        Assert.Equal("Patched Name", patched["displayName"]!.GetValue<string>());
        Assert.Equal(newEmail, patched["emails"]![0]!["value"]!.GetValue<string>());

        // Okta: a path-less value object.
        r = await c.PatchAsync("/scim/v2/Users/" + id, Body(new { schemas = new[] { ScimUserService.PatchSchema }, Operations = new object[] { new { op = "replace", value = new { active = false } } } }));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.False((await JsonAsync(r))["active"]!.GetValue<bool>());

        // Deactivated: signed out everywhere (new security stamp), access token revoked, password sign-in refused.
        var after = app.User(local.UserName!);
        Assert.NotNull(after.DeactivatedAt);
        Assert.NotEqual(stampBefore, after.SecurityStamp);
        var api = app.CreateClient();
        api.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", pat);
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.GetAsync("/api/v1/user")).StatusCode);
        await Assert.ThrowsAsync<InvalidOperationException>(() => app.CookieClientAsync(local.UserName!));
        await using (var s = app.Scope())
        {
            var users = s.Get<UserManager<User>>();
            Assert.True(await users.IsLockedOutAsync((await users.FindByIdAsync(id))!));
        }

        // Entra: "Replace" with a string boolean reactivates.
        r = await c.PatchAsync("/scim/v2/Users/" + id, Body("""{"schemas":["urn:ietf:params:scim:api:messages:2.0:PatchOp"],"Operations":[{"op":"Replace","path":"active","value":"True"}]}"""));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.True((await JsonAsync(r))["active"]!.GetValue<bool>());
        Assert.Null(app.User(local.UserName!).DeactivatedAt);
        await app.CookieClientAsync(local.UserName!); // password sign-in works again
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.GetAsync("/api/v1/user")).StatusCode); // the revoked token stays revoked

        // remove externalId; removing userName is refused; unknown op is refused.
        r = await c.PatchAsync("/scim/v2/Users/" + id, Body(new { schemas = new[] { ScimUserService.PatchSchema }, Operations = new object[] { new { op = "remove", path = "externalId" } } }));
        Assert.Null((await JsonAsync(r))["externalId"]);
        r = await c.PatchAsync("/scim/v2/Users/" + id, Body(new { schemas = new[] { ScimUserService.PatchSchema }, Operations = new object[] { new { op = "remove", path = "userName" } } }));
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        r = await c.PatchAsync("/scim/v2/Users/" + id, Body(new { schemas = new[] { ScimUserService.PatchSchema }, Operations = new object[] { new { op = "move", path = "userName" } } }));
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        AssertScimError(await JsonAsync(r), 400, "invalidSyntax");

        using var db = app.Db();
        var events = db.AuditEvents.Where(e => e.TargetId == id && e.ActorName == "SCIM").Select(e => e.Action).ToList();
        Assert.Contains("scim.user_deactivated", events);
        Assert.Contains("scim.user_reactivated", events);
        Assert.Contains("scim.user_updated", events);
    }

    [Fact]
    public async Task Delete_deactivates_and_never_deletes()
    {
        var c = await ClientAsync();
        var created = await JsonAsync(await c.PostAsync("/scim/v2/Users", Body(NewUser(NewName()))));
        var id = created["id"]!.GetValue<string>();
        var del = await c.DeleteAsync("/scim/v2/Users/" + id);
        Assert.Equal(HttpStatusCode.NoContent, del.StatusCode);
        using var db = app.Db();
        var user = db.Users.Single(u => u.Id == int.Parse(id));
        Assert.NotNull(user.DeactivatedAt);
        Assert.False((await JsonAsync(await c.GetAsync("/scim/v2/Users/" + id)))["active"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Scim_cannot_change_or_deactivate_a_site_admin()
    {
        var c = await ClientAsync();
        var alice = app.UserId("alice");
        var patch = await c.PatchAsync($"/scim/v2/Users/{alice}", Body(new { schemas = new[] { ScimUserService.PatchSchema }, Operations = new object[] { new { op = "replace", path = "active", value = false } } }));
        Assert.Equal(HttpStatusCode.Forbidden, patch.StatusCode);
        AssertScimError(await JsonAsync(patch), 403);
        Assert.Equal(HttpStatusCode.Forbidden, (await c.DeleteAsync($"/scim/v2/Users/{alice}")).StatusCode);
        var put = await c.PutAsync($"/scim/v2/Users/{alice}", Body(new { userName = "alice", displayName = "Hijacked", emails = new[] { new { value = "attacker@corp.example" } } }));
        Assert.Equal(HttpStatusCode.Forbidden, put.StatusCode);
        // Adopting the admin through a POST with the admin's email is refused too.
        var adminEmail = app.User("alice").Email!;
        var adopt = await c.PostAsync("/scim/v2/Users", Body(NewUser("alice-idp@corp.example", adminEmail)));
        Assert.Equal(HttpStatusCode.Forbidden, adopt.StatusCode);
        var a = app.User("alice");
        Assert.Null(a.DeactivatedAt);
        Assert.NotEqual("Hijacked", a.DisplayName);
    }

    [Fact]
    public async Task Groups_made_in_ByteBench_are_listed_with_members_but_not_writable()
    {
        var c = await ClientAsync();
        string path;
        await using (var s = app.Scope(app.UserId("alice")))
        {
            path = "scimg" + Guid.NewGuid().ToString("N")[..6];
            var g = await s.Get<GroupService>().CreateAsync("SCIM group", path, null, ProjectVisibility.Private);
            await s.Get<GroupService>().AddMemberAsync(g.Id, "bob", ProjectRole.Developer);
        }
        var list = await JsonAsync(await c.GetAsync($"/scim/v2/Groups?filter={Uri.EscapeDataString($"displayName eq \"{path}\"")}"));
        Assert.Equal(1, list["totalResults"]!.GetValue<int>());
        var group = list["Resources"]![0]!;
        Assert.Contains(group["members"]!.AsArray(), m => m!["display"]!.GetValue<string>() == "bob");
        var id = group["id"]!.GetValue<string>();
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/scim/v2/Groups/" + id)).StatusCode);

        // Not created by SCIM and not mapped: SCIM can't change or delete it (ScimGroupTests covers writable groups).
        var patch = await c.PatchAsync("/scim/v2/Groups/" + id, Body(new { Operations = new object[] { new { op = "replace", path = "displayName", value = "x" } } }));
        Assert.Equal(HttpStatusCode.Forbidden, patch.StatusCode);
        AssertScimError(await JsonAsync(patch), 403);
        Assert.Equal(HttpStatusCode.Forbidden, (await c.DeleteAsync("/scim/v2/Groups/" + id)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await c.PutAsync("/scim/v2/Groups/" + id, Body(new { displayName = "x" }))).StatusCode);
    }
}

/// <summary>Per-IP failure limiting, in its own fixture (it blocks the test server's client address).</summary>
public class ScimRateLimitApp : ScimTestApp
{
    protected override void AddSettings(IDictionary<string, string?> s)
    {
        base.AddSettings(s);
        s["Scim:MaxFailuresPerIp"] = "3";
    }
}

public class ScimRateLimitTests(ScimRateLimitApp app) : IClassFixture<ScimRateLimitApp>
{
    [Fact]
    public async Task Repeated_bad_tokens_from_one_address_get_429_even_with_a_good_token_afterwards()
    {
        var good = await app.EnableAsync();
        for (var i = 0; i < 3; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await app.Scim("bbscim_wrong" + i).GetAsync("/scim/v2/Users")).StatusCode);
        var blocked = await app.Scim("bbscim_wrong").GetAsync("/scim/v2/Users");
        Assert.Equal(HttpStatusCode.TooManyRequests, blocked.StatusCode);
        Assert.NotNull(blocked.Headers.RetryAfter);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await app.Scim(good).GetAsync("/scim/v2/Users")).StatusCode);
    }
}
