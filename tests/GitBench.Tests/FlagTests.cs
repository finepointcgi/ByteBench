using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GitBench.Data;
using GitBench.Services;
using GitBench.Services.Flags;
using GitBench.Services.Webhooks;
using GitBench.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace GitBench.Tests;

/// <summary>Evaluation semantics: no database, no HTTP.</summary>
public class FlagEvaluatorTests
{
    private static readonly Func<string, IReadOnlySet<string>?> NoSegments = _ => null;

    private static FlagContext User(string id, params (string Key, string Value)[] attrs)
    {
        var c = new FlagContext { UserId = id };
        foreach (var (k, v) in attrs) c.Attributes[k] = v;
        return c;
    }

    private static FlagRule Rule(string id, string value, double? pct = null, params FlagClause[] clauses) => new()
    {
        Id = id, Value = JsonValue.Create(value), Percentage = pct, Clauses = [.. clauses],
    };

    private static FlagClause Clause(string attr, string op, params string[] values) => new() { Attribute = attr, Op = op, Values = [.. values] };

    private static string? Eval(FlagContext ctx, bool enabled, IReadOnlyList<FlagRule> rules, Func<string, IReadOnlySet<string>?>? segments = null)
    {
        var r = FlagEvaluator.Evaluate("f", FlagType.String, JsonValue.Create("off"), enabled, JsonValue.Create("default"), rules, ctx, segments ?? NoSegments);
        return r.Value?.GetValue<string>();
    }

    [Fact]
    public void Off_serves_the_off_value_default_serves_default_and_rules_win_in_order()
    {
        var rules = new[]
        {
            Rule("first", "A", null, Clause("plan", "equals", "pro")),
            Rule("second", "B", null, Clause("plan", "in", "pro", "team")),
            Rule("third", "C", null, Clause("country", "startsWith", "N")),
        };
        Assert.Equal("off", Eval(User("1", ("plan", "pro")), enabled: false, rules)); // off ignores rules
        Assert.Equal("A", Eval(User("1", ("plan", "pro")), true, rules)); // both match: the first wins
        Assert.Equal("B", Eval(User("1", ("plan", "team")), true, rules));
        Assert.Equal("C", Eval(User("1", ("country", "NL")), true, rules));
        Assert.Equal("default", Eval(User("1", ("plan", "free")), true, rules));
        Assert.Equal("default", Eval(new FlagContext(), true, rules)); // anonymous

        var reordered = new[] { rules[1], rules[0] };
        Assert.Equal("B", Eval(User("1", ("plan", "pro")), true, reordered));

        var result = FlagEvaluator.Evaluate("f", FlagType.String, JsonValue.Create("off"), true, JsonValue.Create("default"), rules, User("1", ("plan", "team")), NoSegments);
        Assert.Equal(("rule", "second"), (result.Reason, result.RuleId));
        Assert.Equal("off", FlagEvaluator.Evaluate("f", FlagType.String, JsonValue.Create("off"), false, null, rules, User("1"), NoSegments).Reason);
    }

    [Fact]
    public void Operators_compare_case_insensitively_and_missing_attributes_behave()
    {
        Assert.Equal("X", Eval(User("u"), true, [Rule("r", "X", null, Clause("userId", "equals", "U"))]));
        Assert.Equal("X", Eval(User("u", ("email", "Bob@Example.com")), true, [Rule("r", "X", null, Clause("email", "endsWith", "@example.com"))]));
        Assert.Equal("X", Eval(User("u", ("email", "bob@example.com")), true, [Rule("r", "X", null, Clause("email", "contains", "example"))]));
        Assert.Equal("default", Eval(User("u"), true, [Rule("r", "X", null, Clause("email", "contains", "example"))])); // no attribute: no match
        Assert.Equal("X", Eval(User("u"), true, [Rule("r", "X", null, Clause("plan", "notEquals", "pro"))])); // no attribute: not equal
        Assert.Equal("default", Eval(User("u", ("plan", "pro")), true, [Rule("r", "X", null, Clause("plan", "notIn", "pro", "team"))]));
        // Clauses are ANDed.
        var both = Rule("r", "X", null, Clause("plan", "equals", "pro"), Clause("country", "equals", "NL"));
        Assert.Equal("X", Eval(User("u", ("plan", "pro"), ("country", "NL")), true, [both]));
        Assert.Equal("default", Eval(User("u", ("plan", "pro"), ("country", "DE")), true, [both]));
        // No clauses: everyone.
        Assert.Equal("X", Eval(new FlagContext(), true, [Rule("r", "X")]));
    }

    [Fact]
    public void Segment_clauses_use_the_users_membership()
    {
        IReadOnlySet<string>? Segments(string k) => k == "beta" ? new HashSet<string> { "7", "8" } : null;
        var inBeta = Rule("r", "X", null, new FlagClause { Op = "inSegment", Values = ["beta"] });
        var notBeta = Rule("r", "X", null, new FlagClause { Op = "notInSegment", Values = ["beta"] });
        Assert.Equal("X", Eval(User("7"), true, [inBeta], Segments));
        Assert.Equal("default", Eval(User("9"), true, [inBeta], Segments));
        Assert.Equal("default", Eval(new FlagContext(), true, [inBeta], Segments));
        Assert.Equal("X", Eval(User("9"), true, [notBeta], Segments));
        Assert.Equal("default", Eval(User("7"), true, [notBeta], Segments));
        Assert.Equal("default", Eval(User("7"), true, [Rule("r", "X", null, new FlagClause { Op = "inSegment", Values = ["unknown"] })], Segments));
    }

    [Fact]
    public void Percentage_rollout_is_stable_grows_monotonically_and_is_evenly_distributed()
    {
        var rules25 = new[] { Rule("rollout", "on", 25) };
        var rules50 = new[] { Rule("rollout", "on", 50) };
        const int users = 20000;
        int in25 = 0, in50 = 0;
        for (var i = 0; i < users; i++)
        {
            var ctx = User("user-" + i);
            var a = Eval(ctx, true, rules25) == "on";
            Assert.Equal(a, Eval(ctx, true, rules25) == "on"); // same input, same answer
            var b = Eval(ctx, true, rules50) == "on";
            if (a) Assert.True(b, "raising the percentage must not remove anyone");
            if (a) in25++;
            if (b) in50++;
        }
        Assert.InRange(in25 / (double)users, 0.235, 0.265);
        Assert.InRange(in50 / (double)users, 0.485, 0.515);
        // Edges and special cases.
        Assert.Equal("default", Eval(User("u"), true, [Rule("r", "on", 0)]));
        Assert.Equal("on", Eval(User("u"), true, [Rule("r", "on", 100)]));
        Assert.Equal("default", Eval(new FlagContext(), true, [Rule("r", "on", 100)])); // nothing to bucket by
        // A different rule id or flag key reshuffles; the hash is deterministic across runs.
        Assert.Equal(FlagEvaluator.Bucket("f", "r", "alice"), FlagEvaluator.Bucket("f", "r", "alice"));
        Assert.NotEqual(Enumerable.Range(0, 50).Select(i => FlagEvaluator.Bucket("f", "r1", "u" + i)),
            Enumerable.Range(0, 50).Select(i => FlagEvaluator.Bucket("f", "r2", "u" + i)));
        // The percentage rule combines with clauses: only matching users are bucketed.
        var scoped = Rule("r", "on", 100, Clause("plan", "equals", "pro"));
        Assert.Equal("default", Eval(User("u", ("plan", "free")), true, [scoped]));
        Assert.Equal("on", Eval(User("u", ("plan", "pro")), true, [scoped]));
        // Bucketing by another attribute.
        var byOrg = new FlagRule { Id = "r", Value = JsonValue.Create("on"), Percentage = 50, BucketBy = "org" };
        var org = User("someone", ("org", "acme"));
        var other = User("someone-else", ("org", "acme"));
        Assert.Equal(Eval(org, true, [byOrg]), Eval(other, true, [byOrg])); // same org, same bucket
    }

    [Fact]
    public void Values_and_rules_are_validated_against_the_type()
    {
        Assert.Equal("true", FlagEvaluator.NormalizeValue(FlagType.Boolean, JsonValue.Create(true)));
        Assert.Throws<InvalidOperationException>(() => FlagEvaluator.NormalizeValue(FlagType.Boolean, JsonValue.Create("true")));
        Assert.Throws<InvalidOperationException>(() => FlagEvaluator.NormalizeValue(FlagType.Number, JsonValue.Create("5")));
        Assert.Equal("5", FlagEvaluator.NormalizeValue(FlagType.Number, JsonValue.Create(5)));
        Assert.Equal("{\"a\":1}", FlagEvaluator.NormalizeValue(FlagType.Json, JsonNode.Parse("{ \"a\": 1 }")));
        Assert.Throws<InvalidOperationException>(() => FlagEvaluator.NormalizeValue(FlagType.String, JsonNode.Parse("[1]")));
        Assert.Throws<InvalidOperationException>(() => FlagEvaluator.FromInput(FlagType.Number, "abc"));
        Assert.Equal("hello", FlagEvaluator.FromInput(FlagType.String, "hello")!.GetValue<string>());

        Assert.Throws<InvalidOperationException>(() => FlagEvaluator.ValidateRules(FlagType.String, [Rule("a", "x"), Rule("A", "y")])); // duplicate id
        Assert.Throws<InvalidOperationException>(() => FlagEvaluator.ValidateRules(FlagType.String, [Rule("a", "x", 101)]));
        Assert.Throws<InvalidOperationException>(() => FlagEvaluator.ValidateRules(FlagType.String, [Rule("a", "x", null, Clause("plan", "regex", "x"))]));
        Assert.Throws<InvalidOperationException>(() => FlagEvaluator.ValidateRules(FlagType.String, [Rule("a", "x", null, Clause("", "equals", "x"))]));
        Assert.Throws<InvalidOperationException>(() => FlagEvaluator.ValidateRules(FlagType.Boolean, [Rule("a", "x")])); // string value on a boolean flag
        var ok = FlagEvaluator.ValidateRules(FlagType.String, [new FlagRule { Id = "", Value = JsonValue.Create("x") }]);
        Assert.Matches("^r[0-9a-f]{8}$", ok[0].Id); // generated
    }

    [Fact]
    public void Staleness_counts_changes_and_evaluations()
    {
        var now = new DateTime(2026, 11, 20, 0, 0, 0, DateTimeKind.Utc);
        FeatureFlag Flag(int ageDays, int? evaluatedDaysAgo = null, bool archived = false) => new()
        {
            Key = "k", CreatedAt = now.AddDays(-ageDays), UpdatedAt = now.AddDays(-ageDays), IsArchived = archived,
            States = [new FlagEnvironmentState { UpdatedAt = now.AddDays(-ageDays), LastEvaluatedAt = evaluatedDaysAgo is { } d ? now.AddDays(-d) : null }],
        };
        Assert.Null(FlagService.Staleness(Flag(10), now, 30));
        Assert.Equal(45, FlagService.Staleness(Flag(45), now, 30)!.IdleDays);
        Assert.Null(FlagService.Staleness(Flag(45, evaluatedDaysAgo: 2), now, 30)); // recently evaluated
        Assert.NotNull(FlagService.Staleness(Flag(90, evaluatedDaysAgo: 40), now, 30));
        Assert.Null(FlagService.Staleness(Flag(90, archived: true), now, 30));
    }
}

/// <summary>The service, the management API, the SDK API (keys, ETag, CORS, SSE), history, audit and webhooks.</summary>
public class FlagServiceTests(TestApp app) : IClassFixture<TestApp>
{
    private static StringContent Json(object body) => new(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
    private static async Task<JsonElement> ReadJson(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;

    private async Task<Project> ProjectAsync() => await app.CreateProjectAsync("alice", ProjectVisibility.Private,
        ("bob", ProjectRole.Developer), ("carol", ProjectRole.Maintainer), ("dan", ProjectRole.Viewer));

    /// <summary>A project with a "prod" and a "staging" environment and a boolean flag "checkout".</summary>
    private async Task<(Project P, int Prod, int Staging)> SetupAsync(string flagKey = "checkout", FlagType type = FlagType.Boolean)
    {
        var p = await ProjectAsync();
        await using var s = app.Scope(app.UserId("carol"));
        var svc = s.Get<FlagService>();
        var prod = await svc.CreateEnvironmentAsync(p.Id, "prod");
        var staging = await svc.CreateEnvironmentAsync(p.Id, "staging");
        await svc.CreateFlagAsync(p.Id, flagKey, "Checkout", "New checkout", type);
        return (p, prod.Id, staging.Id);
    }

    private async Task<string> KeyAsync(Project p, int env, SdkKeyKind kind = SdkKeyKind.Client)
    {
        await using var s = app.Scope(app.UserId("carol"));
        return (await s.Get<FlagService>().CreateKeyAsync(p.Id, env, "test " + Guid.NewGuid().ToString("N")[..5], kind)).Plaintext;
    }

    private HttpClient Sdk(string? key)
    {
        var c = app.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });
        if (key != null) c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        return c;
    }

    // ----- Permissions -----

    [Fact]
    public async Task Developers_toggle_maintainers_create_viewers_only_read()
    {
        var (p, prod, _) = await SetupAsync();
        await using (var s = app.Scope(app.UserId("bob")))
        {
            var svc = s.Get<FlagService>();
            await svc.SetEnabledAsync(p.Id, "checkout", prod, true); // Developer: toggle
            await svc.SetRulesAsync(p.Id, "checkout", prod, [new FlagRule { Id = "r1", Value = JsonValue.Create(true) }]);
            await Assert.ThrowsAsync<ForbiddenException>(() => svc.CreateFlagAsync(p.Id, "another", "Another", null, FlagType.Boolean));
            await Assert.ThrowsAsync<ForbiddenException>(() => svc.ArchiveAsync(p.Id, "checkout", true));
            await Assert.ThrowsAsync<ForbiddenException>(() => svc.DeleteFlagAsync(p.Id, "checkout"));
            await Assert.ThrowsAsync<ForbiddenException>(() => svc.CreateKeyAsync(p.Id, prod, "x", SdkKeyKind.Client));
            await Assert.ThrowsAsync<ForbiddenException>(() => svc.CreateEnvironmentAsync(p.Id, "qa"));
            Assert.Empty(await svc.ListKeysAsync(p.Id)); // key list is Maintainer+
        }
        await using (var s = app.Scope(app.UserId("dan")))
        {
            var svc = s.Get<FlagService>();
            Assert.Single(await svc.ListFlagsAsync(p.Id)); // Viewer: read
            await Assert.ThrowsAsync<ForbiddenException>(() => svc.SetEnabledAsync(p.Id, "checkout", prod, false));
            await Assert.ThrowsAsync<ForbiddenException>(() => svc.SetDefaultValueAsync(p.Id, "checkout", prod, JsonValue.Create(false)));
            await Assert.ThrowsAsync<ForbiddenException>(() => svc.SaveSegmentAsync(p.Id, "s", "S", null, ["1"]));
        }
        var outsider = await app.CreateUserAsync("outsider" + Guid.NewGuid().ToString("N")[..6]);
        await using (var s = app.Scope(outsider.Id))
        {
            var svc = s.Get<FlagService>();
            Assert.Empty(await svc.ListFlagsAsync(p.Id)); // no access: nothing visible
            Assert.Null(await svc.GetFlagAsync(p.Id, "checkout"));
            await Assert.ThrowsAsync<ForbiddenException>(() => svc.SetEnabledAsync(p.Id, "checkout", prod, true));
        }
        await using (var s = app.Scope(app.UserId("carol")))
        {
            var svc = s.Get<FlagService>();
            await svc.ArchiveAsync(p.Id, "checkout", true);
            await Assert.ThrowsAsync<InvalidOperationException>(() => svc.SetEnabledAsync(p.Id, "checkout", prod, false)); // archived flags are frozen
            await svc.ArchiveAsync(p.Id, "checkout", false);
            await svc.DeleteFlagAsync(p.Id, "checkout");
            Assert.Empty(await svc.ListFlagsAsync(p.Id, includeArchived: true));
        }
    }

    [Fact]
    public async Task Management_api_enforces_scopes_and_roles()
    {
        var (p, prod, _) = await SetupAsync();
        var maintainer = await app.ApiClientAsync("carol");
        var developer = await app.ApiClientAsync("bob");
        var viewer = await app.ApiClientAsync("dan");
        var readOnly = await app.ApiClientAsync("carol", TokenScopes.ReadApi);
        var url = $"/api/v1/projects/{p.Key}/flags";

        var created = await maintainer.PostAsync(url, Json(new { key = "dark-mode", name = "Dark mode", type = "string", defaultValue = "dark" }));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var flag = await ReadJson(created);
        Assert.Equal("string", flag.GetProperty("type").GetString());
        Assert.All(flag.GetProperty("environments").EnumerateArray(), e => Assert.False(e.GetProperty("enabled").GetBoolean())); // new flags start off
        Assert.Equal(HttpStatusCode.Forbidden, (await developer.PostAsync(url, Json(new { key = "x", name = "X" }))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await readOnly.PostAsync(url, Json(new { key = "x", name = "X" }))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await maintainer.PostAsync(url, Json(new { key = "dark-mode", name = "Again" }))).StatusCode); // duplicate
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await maintainer.PostAsync(url, Json(new { key = "Bad Key", name = "X" }))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await maintainer.PostAsync(url, Json(new { key = "x", name = "X", type = "float" }))).StatusCode);

        var stateUrl = $"{url}/dark-mode/environments/{prod}";
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PutAsync(stateUrl, Json(new { enabled = true }))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await readOnly.PutAsync(stateUrl, Json(new { enabled = true }))).StatusCode);
        var set = await developer.PutAsync(stateUrl, Json(new
        {
            enabled = true, defaultValue = "dark",
            rules = new[] { new { id = "vip", clauses = new[] { new { attribute = "plan", op = "equals", values = new[] { "pro" } } }, value = "midnight" } },
        }));
        Assert.Equal(HttpStatusCode.OK, set.StatusCode);
        var state = (await ReadJson(set)).GetProperty("environments").EnumerateArray().First(e => e.GetProperty("environmentId").GetInt32() == prod);
        Assert.True(state.GetProperty("enabled").GetBoolean());
        Assert.Equal("vip", state.GetProperty("rules")[0].GetProperty("id").GetString());
        // A bad rule fails as a whole and leaves the flag as it was.
        var bad = await developer.PutAsync(stateUrl, Json(new { enabled = false, rules = new[] { new { id = "x", value = 5 } } }));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, bad.StatusCode);
        await using (var db = app.Db())
            Assert.True(await db.FlagEnvironmentStates.AnyAsync(s => s.Environment.Id == prod && s.Flag.Key == "dark-mode" && s.Enabled));

        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync($"{url}/dark-mode")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync($"/api/v1/projects/{p.Key}/flag-sdk-keys")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await developer.DeleteAsync($"{url}/dark-mode")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await maintainer.DeleteAsync($"{url}/dark-mode")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await maintainer.GetAsync($"{url}/dark-mode")).StatusCode);
        // The listing carries pagination headers like the rest of the API.
        var list = await maintainer.GetAsync(url);
        Assert.True(list.Headers.Contains("X-Total-Count"));
    }

    // ----- SDK keys -----

    [Fact]
    public async Task Sdk_keys_are_hashed_scoped_rotatable_and_never_personal_tokens()
    {
        var (p, prod, staging) = await SetupAsync();
        await using (var s = app.Scope(app.UserId("carol")))
            await s.Get<FlagService>().SetEnabledAsync(p.Id, "checkout", prod, true);
        var client = await KeyAsync(p, prod);
        var server = await KeyAsync(p, prod, SdkKeyKind.Server);
        var stagingKey = await KeyAsync(p, staging);

        // Only the hash is stored.
        await using (var db = app.Db())
        {
            var rows = await db.FlagSdkKeys.AsNoTracking().ToListAsync();
            Assert.DoesNotContain(rows, r => Encoding.UTF8.GetString(r.KeyHash).Contains(client));
            Assert.Contains(rows, r => r.KeyHash.SequenceEqual(FlagService.HashKey(client)));
            Assert.All(rows, r => Assert.True(r.Prefix.Length <= 12 && client != r.Prefix));
        }

        var url = "/api/v1/flags/evaluate?flag=checkout&user=u1";
        Assert.Equal(HttpStatusCode.Unauthorized, (await Sdk(null).GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Sdk("gbsdk_c_nonsense").GetAsync(url)).StatusCode);
        // A personal access token is not an SDK key, even a write_api one.
        var pat = await app.CreateTokenAsync("carol", TokenScopes.ReadApi | TokenScopes.WriteApi);
        var patResponse = await Sdk(pat).GetAsync(url);
        Assert.Equal(HttpStatusCode.Unauthorized, patResponse.StatusCode);
        Assert.Contains("personal access tokens", await patResponse.Content.ReadAsStringAsync());
        // Cookie sessions don't authenticate here either.
        var cookie = await app.CookieClientAsync("carol");
        Assert.Equal(HttpStatusCode.Unauthorized, (await cookie.GetAsync(url)).StatusCode);
        // SDK keys don't open the user API.
        Assert.Equal(HttpStatusCode.Unauthorized, (await Sdk(client).GetAsync($"/api/v1/projects/{p.Key}/flags")).StatusCode);

        var ok = await Sdk(client).GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var body = await ReadJson(ok);
        Assert.True(body.GetProperty("value").GetBoolean());
        Assert.Equal("default", body.GetProperty("reason").GetString());
        // Read-only: nothing but GET/POST evaluation exists; writes to management paths are refused.
        Assert.Equal(HttpStatusCode.Unauthorized, (await Sdk(client).PutAsync($"/api/v1/projects/{p.Key}/flags/checkout/environments/{prod}", Json(new { enabled = false }))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Sdk(client).GetAsync("/api/v1/flags/evaluate?flag=nope")).StatusCode);

        // The key is bound to its environment: the staging key sees staging state (off).
        Assert.False((await ReadJson(await Sdk(stagingKey).GetAsync(url))).GetProperty("value").GetBoolean());

        // Client keys never see rules; server keys do.
        Assert.Equal(HttpStatusCode.Forbidden, (await Sdk(client).GetAsync("/api/v1/flags/definitions")).StatusCode);
        var defs = await Sdk(server).GetAsync("/api/v1/flags/definitions");
        Assert.Equal(HttpStatusCode.OK, defs.StatusCode);
        Assert.Equal("checkout", (await ReadJson(defs)).GetProperty("flags")[0].GetProperty("key").GetString());
        // Server keys must not travel in URLs.
        Assert.Equal(HttpStatusCode.Forbidden, (await Sdk(null).GetAsync($"{url}&sdkKey={server}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Sdk(null).GetAsync($"{url}&sdkKey={client}")).StatusCode); // client keys may (EventSource)
        var viaHeader = Sdk(null);
        viaHeader.DefaultRequestHeaders.Add("X-GitBench-SDK-Key", client);
        Assert.Equal(HttpStatusCode.OK, (await viaHeader.GetAsync(url)).StatusCode);

        // Rotation: the old secret dies, the new one works, the audit log has it without secrets.
        int keyId;
        await using (var db = app.Db()) keyId = await db.FlagSdkKeys.Where(k => k.KeyHash == FlagService.HashKey(client)).Select(k => k.Id).SingleAsync();
        string rotated;
        await using (var s = app.Scope(app.UserId("carol")))
            rotated = (await s.Get<FlagService>().RotateKeyAsync(p.Id, keyId)).Plaintext;
        Assert.NotEqual(client, rotated);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Sdk(client).GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Sdk(rotated).GetAsync(url)).StatusCode);

        await using (var s = app.Scope(app.UserId("carol")))
            await s.Get<FlagService>().RevokeKeyAsync(p.Id, keyId);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Sdk(rotated).GetAsync(url)).StatusCode);
        await using (var s = app.Scope(app.UserId("carol")))
            await Assert.ThrowsAsync<InvalidOperationException>(() => s.Get<FlagService>().RotateKeyAsync(p.Id, keyId));

        await using (var db = app.Db())
        {
            var audit = await db.AuditEvents.AsNoTracking().Where(e => e.ProjectId == p.Id && e.Action.StartsWith("flag.sdk_key")).ToListAsync();
            Assert.Contains(audit, e => e.Action == "flag.sdk_key_created");
            Assert.Contains(audit, e => e.Action == "flag.sdk_key_rotated");
            Assert.Contains(audit, e => e.Action == "flag.sdk_key_revoked");
            Assert.All(audit, e => Assert.DoesNotContain("gbsdk_c_" + rotated[8..], e.Details ?? ""));
        }
    }

    // ----- Evaluation endpoints, ETag, CORS -----

    [Fact]
    public async Task Evaluation_endpoints_serve_values_with_etags_and_cors()
    {
        var (p, prod, _) = await SetupAsync();
        await using (var s = app.Scope(app.UserId("carol")))
        {
            var svc = s.Get<FlagService>();
            await svc.CreateFlagAsync(p.Id, "banner", "Banner", null, FlagType.String, JsonValue.Create("hello"));
            await svc.SetEnabledAsync(p.Id, "checkout", prod, true);
            await svc.SetEnabledAsync(p.Id, "banner", prod, true);
            await svc.SetRulesAsync(p.Id, "banner", prod, [new FlagRule
            {
                Id = "pros", Value = JsonValue.Create("pro-banner"),
                Clauses = [new FlagClause { Attribute = "plan", Op = "equals", Values = ["pro"] }],
            }]);
        }
        var key = await KeyAsync(p, prod);
        var http = Sdk(key);

        var one = await http.GetAsync("/api/v1/flags/evaluate?flag=banner&user=u1&attr.plan=pro");
        Assert.Equal("*", one.Headers.GetValues("Access-Control-Allow-Origin").Single());
        Assert.Contains("ETag", one.Headers.GetValues("Access-Control-Expose-Headers").Single());
        var oneBody = await ReadJson(one);
        Assert.Equal(("pro-banner", "rule"), (oneBody.GetProperty("value").GetString(), oneBody.GetProperty("reason").GetString()));
        Assert.False(oneBody.TryGetProperty("ruleId", out var rid) && rid.ValueKind != JsonValueKind.Null); // client keys don't learn rule ids
        Assert.Equal("hello", (await ReadJson(await http.GetAsync("/api/v1/flags/evaluate?flag=banner&user=u1&attr.plan=free"))).GetProperty("value").GetString());

        // Bulk, GET and POST agree.
        var all = await ReadJson(await http.GetAsync("/api/v1/flags/evaluate-all?user=u1&attr.plan=pro"));
        Assert.Equal("pro-banner", all.GetProperty("flags").GetProperty("banner").GetProperty("value").GetString());
        Assert.True(all.GetProperty("flags").GetProperty("checkout").GetProperty("value").GetBoolean());
        var posted = await ReadJson(await http.PostAsync("/api/v1/flags/evaluate-all", Json(new { userId = "u1", attributes = new { plan = "pro" } })));
        Assert.Equal(all.GetProperty("flags").GetRawText(), posted.GetProperty("flags").GetRawText());
        var postedOne = await ReadJson(await http.PostAsync("/api/v1/flags/evaluate", Json(new { flag = "banner", userId = "u1", attributes = new { plan = "pro" } })));
        Assert.Equal("pro-banner", postedOne.GetProperty("value").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, (await http.GetAsync("/api/v1/flags/evaluate")).StatusCode); // flag is required

        // ETag / If-None-Match.
        var first = await http.GetAsync("/api/v1/flags/evaluate-all?user=u1");
        var etag = first.Headers.ETag!.Tag;
        var req = new HttpRequestMessage(HttpMethod.Get, "/api/v1/flags/evaluate-all?user=u1");
        req.Headers.TryAddWithoutValidation("If-None-Match", etag);
        var notModified = await http.SendAsync(req);
        Assert.Equal(HttpStatusCode.NotModified, notModified.StatusCode);
        Assert.Equal(etag, notModified.Headers.ETag!.Tag);
        Assert.Equal("", await notModified.Content.ReadAsStringAsync());
        // A different context is a different representation.
        var other = new HttpRequestMessage(HttpMethod.Get, "/api/v1/flags/evaluate-all?user=u1&attr.plan=pro");
        other.Headers.TryAddWithoutValidation("If-None-Match", etag);
        Assert.Equal(HttpStatusCode.OK, (await http.SendAsync(other)).StatusCode);
        // After a change the old ETag no longer matches.
        await using (var s = app.Scope(app.UserId("bob")))
            await s.Get<FlagService>().SetEnabledAsync(p.Id, "checkout", prod, false);
        var after = new HttpRequestMessage(HttpMethod.Get, "/api/v1/flags/evaluate-all?user=u1");
        after.Headers.TryAddWithoutValidation("If-None-Match", etag);
        var changed = await http.SendAsync(after);
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        Assert.NotEqual(etag, changed.Headers.ETag!.Tag);
        Assert.False((await ReadJson(changed)).GetProperty("flags").GetProperty("checkout").GetProperty("value").GetBoolean());
        // Weak validators and lists are honoured too.
        var weak = new HttpRequestMessage(HttpMethod.Get, "/api/v1/flags/evaluate-all?user=u1");
        weak.Headers.TryAddWithoutValidation("If-None-Match", $"\"zzz\", W/{changed.Headers.ETag.Tag}");
        Assert.Equal(HttpStatusCode.NotModified, (await http.SendAsync(weak)).StatusCode);

        // Preflight: no credentials needed, the SDK headers are allowed.
        var preflight = await http.SendAsync(new HttpRequestMessage(HttpMethod.Options, "/api/v1/flags/evaluate-all")
        {
            Headers = { { "Origin", "https://app.example.com" }, { "Access-Control-Request-Method", "GET" }, { "Access-Control-Request-Headers", "authorization,if-none-match" } },
        });
        Assert.Equal(HttpStatusCode.NoContent, preflight.StatusCode);
        Assert.Equal("*", preflight.Headers.GetValues("Access-Control-Allow-Origin").Single());
        Assert.Contains("If-None-Match", preflight.Headers.GetValues("Access-Control-Allow-Headers").Single());
        Assert.False(preflight.Headers.Contains("Access-Control-Allow-Credentials"));
        // Even errors are readable cross-origin.
        Assert.Equal("*", (await Sdk("gbsdk_c_bad").GetAsync("/api/v1/flags/evaluate-all")).Headers.GetValues("Access-Control-Allow-Origin").Single());

        // Archived flags drop out of bulk responses; a single evaluation serves the off value.
        await using (var s = app.Scope(app.UserId("carol")))
            await s.Get<FlagService>().ArchiveAsync(p.Id, "banner", true);
        var afterArchive = await ReadJson(await http.GetAsync("/api/v1/flags/evaluate-all?user=u1"));
        Assert.False(afterArchive.GetProperty("flags").TryGetProperty("banner", out _));
        Assert.Equal("archived", (await ReadJson(await http.GetAsync("/api/v1/flags/evaluate?flag=banner"))).GetProperty("reason").GetString());

        // Evaluations are recorded for stale detection.
        await using var db = app.Db();
        Assert.True(await db.FlagEnvironmentStates.AnyAsync(s => s.Flag.Key == "checkout" && s.LastEvaluatedAt != null));
    }

    [Fact]
    public async Task Server_key_definitions_let_clients_evaluate_locally_with_the_same_result()
    {
        var (p, prod, _) = await SetupAsync();
        await using (var s = app.Scope(app.UserId("carol")))
        {
            var svc = s.Get<FlagService>();
            await svc.SaveSegmentAsync(p.Id, "beta", "Beta", null, ["u7"]);
            await svc.SetEnabledAsync(p.Id, "checkout", prod, true);
            await svc.SetDefaultValueAsync(p.Id, "checkout", prod, JsonValue.Create(false));
            await svc.SetRulesAsync(p.Id, "checkout", prod,
            [
                new FlagRule { Id = "beta", Value = JsonValue.Create(true), Clauses = [new FlagClause { Op = "inSegment", Values = ["beta"] }] },
                new FlagRule { Id = "half", Value = JsonValue.Create(true), Percentage = 50 },
            ]);
            await Assert.ThrowsAsync<InvalidOperationException>(() => svc.SetRulesAsync(p.Id, "checkout", prod,
                [new FlagRule { Id = "x", Value = JsonValue.Create(true), Clauses = [new FlagClause { Op = "inSegment", Values = ["missing"] }] }]));
            await Assert.ThrowsAsync<InvalidOperationException>(() => svc.DeleteSegmentAsync(p.Id, "beta")); // in use
        }
        var server = Sdk(await KeyAsync(p, prod, SdkKeyKind.Server));
        var client = Sdk(await KeyAsync(p, prod));
        var defs = await ReadJson(await server.GetAsync("/api/v1/flags/definitions"));
        var def = defs.GetProperty("flags")[0];
        var rules = FlagEvaluator.ParseRules(def.GetProperty("rules").GetRawText());
        var members = defs.GetProperty("segments").GetProperty("beta").EnumerateArray().Select(x => x.GetString()!).ToHashSet();
        Assert.Equal(["u7"], members);
        for (var i = 0; i < 30; i++)
        {
            var user = i == 7 ? "u7" : "user" + i;
            var local = FlagEvaluator.Evaluate("checkout", FlagType.Boolean, JsonValue.Create(false), def.GetProperty("enabled").GetBoolean(),
                JsonValue.Create(false), rules, new FlagContext { UserId = user }, k => k == "beta" ? members : null);
            var remote = await ReadJson(await client.GetAsync($"/api/v1/flags/evaluate?flag=checkout&user={user}"));
            Assert.Equal(local.Value!.GetValue<bool>(), remote.GetProperty("value").GetBoolean());
        }
        Assert.Equal("beta", (await ReadJson(await server.GetAsync("/api/v1/flags/evaluate?flag=checkout&user=u7"))).GetProperty("ruleId").GetString()); // server keys may see rule ids
    }

    [Fact]
    public async Task Sse_stream_sends_the_initial_flags_and_a_new_event_when_a_flag_changes()
    {
        var (p, prod, _) = await SetupAsync();
        var key = await KeyAsync(p, prod);
        var http = Sdk(key);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var response = await http.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/api/v1/flags/stream?user=u1"), HttpCompletionOption.ResponseHeadersRead, cts.Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("*", response.Headers.GetValues("Access-Control-Allow-Origin").Single());
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(cts.Token));

        async Task<JsonElement> NextFlagsEventAsync()
        {
            while (true)
            {
                var line = await reader.ReadLineAsync(cts.Token) ?? throw new InvalidOperationException("stream ended");
                if (line.StartsWith("data: ", StringComparison.Ordinal)) return JsonDocument.Parse(line[6..]).RootElement;
            }
        }

        var first = await NextFlagsEventAsync();
        Assert.False(first.GetProperty("flags").GetProperty("checkout").GetProperty("value").GetBoolean());
        await using (var s = app.Scope(app.UserId("bob")))
            await s.Get<FlagService>().SetEnabledAsync(p.Id, "checkout", prod, true);
        var second = await NextFlagsEventAsync();
        Assert.True(second.GetProperty("flags").GetProperty("checkout").GetProperty("value").GetBoolean());
        Assert.True(second.GetProperty("version").GetInt64() > first.GetProperty("version").GetInt64());

        // A bad key never opens a stream.
        Assert.Equal(HttpStatusCode.Unauthorized, (await Sdk("gbsdk_c_wrong").GetAsync("/api/v1/flags/stream", HttpCompletionOption.ResponseHeadersRead)).StatusCode);
        cts.Cancel();
    }

    // ----- History, audit, webhooks -----

    [Fact]
    public async Task Changes_are_in_the_history_the_audit_log_and_the_flag_changed_webhook()
    {
        using var receiver = new WebhookReceiver();
        var (p, prod, _) = await SetupAsync();
        await using (var s = app.Scope(app.UserId("carol")))
            await s.Get<WebhookService>().CreateAsync(p.Id, receiver.Url, "a-test-secret-of-sufficient-length", [WebhookEventNames.FlagChanged], active: true);
        await using (var s = app.Scope(app.UserId("bob")))
        {
            var svc = s.Get<FlagService>();
            await svc.SetEnabledAsync(p.Id, "checkout", prod, true);
            await svc.SetEnabledAsync(p.Id, "checkout", prod, true); // unchanged: no record
            await svc.SetDefaultValueAsync(p.Id, "checkout", prod, JsonValue.Create(false));
            await svc.SetRulesAsync(p.Id, "checkout", prod, [new FlagRule { Id = "r1", Value = JsonValue.Create(true), Percentage = 10 }]);
            await svc.UpdateDetailsAsync(p.Id, "checkout", "Checkout v2", "Redesign", []);
        }
        await using (var s = app.Scope(app.UserId("carol")))
            await s.Get<FlagService>().ArchiveAsync(p.Id, "checkout", true);

        await using (var s = app.Scope(app.UserId("dan")))
        {
            var (changes, total) = await s.Get<FlagService>().HistoryAsync(p.Id, "checkout");
            Assert.Equal(total, changes.Count);
            Assert.Equal("archived", changes[0].Kind);
            Assert.Equal("created", changes[^1].Kind);
            Assert.Equal(["archived", "created", "default_value", "details", "rules", "toggled"], changes.Select(c => c.Kind).Distinct().Order().ToArray());
            Assert.Equal(1, changes.Count(c => c.Kind == "toggled")); // the repeated toggle changed nothing
            var toggled = changes.Single(c => c.Kind == "toggled");
            Assert.Equal(("bob", "prod", "off", "on"), (toggled.ActorName, toggled.EnvironmentName, toggled.OldValue, toggled.NewValue));
            var dv = changes.Single(c => c.Kind == "default_value");
            Assert.Equal(("true", "false"), (dv.OldValue, dv.NewValue));
            Assert.All(changes.Where(c => c.Kind == "created"), c => Assert.Equal("carol", c.ActorName));
        }

        await using (var db = app.Db())
        {
            var audit = await db.AuditEvents.AsNoTracking().Where(e => e.ProjectId == p.Id && e.Action.StartsWith("flag.")).OrderBy(e => e.Id).ToListAsync();
            var actions = audit.Select(e => e.Action).ToList();
            Assert.Contains("flag.created", actions);
            Assert.Contains("flag.toggled", actions);
            Assert.Contains("flag.default_changed", actions);
            Assert.Contains("flag.rules_changed", actions);
            Assert.Contains("flag.updated", actions);
            Assert.Contains("flag.archived", actions);
            Assert.Equal(1, actions.Count(a => a == "flag.toggled")); // the no-op wasn't recorded
            Assert.Equal("bob", audit.Single(e => e.Action == "flag.toggled").ActorName);
        }

        var toggledHook = JsonDocument.Parse((await receiver.WaitForAsync(WebhookEventNames.FlagChanged)).Body).RootElement;
        Assert.Equal("flag.changed", toggledHook.GetProperty("event").GetString());
        Assert.Equal(p.Key, toggledHook.GetProperty("project").GetProperty("key").GetString());
        Assert.Equal("checkout", toggledHook.GetProperty("flag").GetProperty("key").GetString());
        // Deliveries can arrive in any order, so check what every payload has in common.
        Assert.Contains(toggledHook.GetProperty("change").GetProperty("kind").GetString(), new[] { "toggled", "default_value", "rules", "details", "archived" });
        Assert.Contains(toggledHook.GetProperty("sender").GetProperty("username").GetString(), new[] { "bob", "carol" });
        Assert.Equal("prod", toggledHook.GetProperty("environment").ValueKind == JsonValueKind.Null ? "prod" : toggledHook.GetProperty("environment").GetProperty("name").GetString());
        Assert.EndsWith($"/p/{p.Key}/flags/checkout", toggledHook.GetProperty("url").GetString());
    }

    [Fact]
    public async Task Flag_changed_is_a_subscribable_event_with_a_chat_message()
    {
        Assert.True(WebhookEventNames.IsSubscribable(WebhookEventNames.FlagChanged));
        Assert.Contains(WebhookEventNames.FlagChanged, GitBench.Services.ChatIntegrations.ChatEvents.SourcesOf([GitBench.Services.ChatIntegrations.ChatEvents.FlagChanged]));
        var env = JsonNode.Parse("""
            { "event": "flag.changed", "project": { "key": "ABC" }, "sender": { "username": "bob", "displayName": "Bob" },
              "flag": { "key": "checkout", "name": "Checkout" }, "environment": { "name": "prod" },
              "change": { "kind": "toggled", "oldValue": "off", "newValue": "on" }, "url": "https://x.test/p/ABC/flags/checkout" }
            """)!.AsObject();
        await using var db = app.Db();
        var messages = await GitBench.Services.ChatIntegrations.ChatEventClassifier.ClassifyAsync(db, WebhookEventNames.FlagChanged, env);
        var m = Assert.Single(messages);
        Assert.Equal(GitBench.Services.ChatIntegrations.ChatEvents.FlagChanged, m.Event);
        Assert.Contains("checkout", m.Summary);
        Assert.Contains("prod", m.Summary);
        Assert.Contains("turned on", m.Summary);
    }

    // ----- Environments, stale detection, linking -----

    [Fact]
    public async Task Ci_environments_appear_and_new_environments_get_every_flag_off()
    {
        var p = await ProjectAsync();
        var repo = await app.CreateRepoAsync(p.Id, "alice");
        await using (var db = app.Db())
        {
            db.CiEnvironments.Add(new CiEnvironment { RepositoryId = repo.Id, Name = "production" });
            await db.SaveChangesAsync();
        }
        await using var s = app.Scope(app.UserId("carol"));
        var svc = s.Get<FlagService>();
        var envs = await svc.ListEnvironmentsAsync(p.Id);
        Assert.Equal(["production"], envs.Select(e => e.Name));
        await svc.CreateFlagAsync(p.Id, "f1", "F1", null, FlagType.Number, JsonValue.Create(5));
        var qa = await svc.CreateEnvironmentAsync(p.Id, "qa");
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.CreateEnvironmentAsync(p.Id, "qa"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.CreateEnvironmentAsync(p.Id, "-bad"));
        var flag = (await svc.GetFlagAsync(p.Id, "f1"))!;
        Assert.Equal(2, flag.States.Count);
        Assert.All(flag.States, st => Assert.False(st.Enabled));
        Assert.Equal("5", flag.States.First(st => st.EnvironmentId == qa.Id).DefaultValue);
        Assert.Equal("0", flag.OffValue);
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.SetDefaultValueAsync(p.Id, "f1", qa.Id, JsonValue.Create("seven")));
    }

    [Fact]
    public async Task Stale_flags_are_found_by_age_and_an_sdk_evaluation_revives_them()
    {
        var (p, prod, _) = await SetupAsync();
        await using (var db = app.Db())
        {
            var old = DateTime.UtcNow.AddDays(-60);
            await db.FeatureFlags.Where(f => f.ProjectId == p.Id).ExecuteUpdateAsync(s => s.SetProperty(f => f.UpdatedAt, old).SetProperty(f => f.CreatedAt, old));
            await db.FlagEnvironmentStates.Where(s => s.Flag.ProjectId == p.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.UpdatedAt, old));
        }
        await using (var s = app.Scope(app.UserId("carol")))
            await s.Get<FlagService>().CreateFlagAsync(p.Id, "fresh", "Fresh", null, FlagType.Boolean);
        await using (var s = app.Scope(app.UserId("dan")))
        {
            var stale = await s.Get<FlagService>().StaleFlagsAsync(p.Id, 30);
            Assert.Equal(["checkout"], stale.Select(x => x.Flag.Key));
            Assert.InRange(stale[0].IdleDays, 59, 61);
            Assert.Empty(await s.Get<FlagService>().StaleFlagsAsync(p.Id, 90));
        }
        var maintainer = await app.ApiClientAsync("carol");
        var api = await ReadJson(await maintainer.GetAsync($"/api/v1/projects/{p.Key}/flags/_stale?days=30"));
        Assert.Equal("checkout", api[0].GetProperty("key").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, (await maintainer.GetAsync($"/api/v1/projects/{p.Key}/flags/_stale?days=0")).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await Sdk(await KeyAsync(p, prod)).GetAsync("/api/v1/flags/evaluate?flag=checkout&user=u")).StatusCode);
        await using (var s = app.Scope(app.UserId("dan")))
            Assert.Empty(await s.Get<FlagService>().StaleFlagsAsync(p.Id, 30)); // evaluated just now
    }

    [Fact]
    public async Task Flags_link_to_issues_of_the_project()
    {
        var (p, _, _) = await SetupAsync();
        var issue = await app.CreateIssueAsync(p.Id, "alice");
        await using var s = app.Scope(app.UserId("bob"));
        var svc = s.Get<FlagService>();
        await svc.UpdateDetailsAsync(p.Id, "checkout", "Checkout", null, [issue.Number, issue.Number]);
        Assert.Equal([issue.Number], (await svc.GetFlagAsync(p.Id, "checkout"))!.LinkedIssues);
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.UpdateDetailsAsync(p.Id, "checkout", "Checkout", null, [9999]));
    }
}
