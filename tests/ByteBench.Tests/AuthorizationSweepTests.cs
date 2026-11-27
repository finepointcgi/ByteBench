using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using ByteBench.Data;
using ByteBench.Tests.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace ByteBench.Tests;

/// <summary>
/// The seeded WEB project (repos, issues, pull requests, sprints, boards, …) made private, so that a route that forgets a
/// permission check returns real data instead of a coincidental 404.
/// </summary>
public sealed class AuthorizationSweepApp : TestApp
{
    public const string TargetKey = "WEB";

    private readonly SemaphoreSlim gate = new(1, 1);
    private bool ready;

    public async Task EnsureReadyAsync()
    {
        await gate.WaitAsync();
        try
        {
            if (ready) return;
            _ = Server; // boots the host (migrations + seed)
            await using (var db = Db())
            {
                var web = await db.Projects.FirstAsync(p => p.Key == TargetKey);
                web.Visibility = ProjectVisibility.Private;
                await db.SaveChangesAsync();
            }
            await CreateUserAsync("sweep-outsider");
            await SeedNewSurfacesAsync();
            ready = true;
        }
        finally { gate.Release(); }
    }

    /// <summary>
    /// The seed has no test management or release data, so without this the sweep's ids for those routes would all be 404s (a route that
    /// forgot its permission check would look fine). Test cases, a plan, a run with a result and evidence, and a release with an asset and
    /// an auto-attach rule, all in the private WEB project.
    /// </summary>
    private async Task SeedNewSurfacesAsync()
    {
        var alice = UserId("alice");
        await using var s = Scope(alice);
        await using var db = Db();
        var project = await db.Projects.FirstAsync(p => p.Key == TargetKey);
        var repo = await db.Repositories.Where(r => r.ProjectId == project.Id).OrderBy(r => r.Id).FirstAsync();
        var tests = s.Get<ByteBench.Services.Tests.TestService>();
        var c = await tests.CreateCaseAsync(project.Id, new ByteBench.Services.Tests.TestCaseInput { Title = "Sweep case", Steps = "1. open", Expected = "ok" });
        var plan = await tests.SavePlanAsync(project.Id, null, "Sweep plan", null, [c.Number]);
        var run = await tests.CreateRunAsync(project.Id, new ByteBench.Services.Tests.TestRunInput { Name = "Sweep run", PlanId = plan.Id });
        await tests.RecordResultAsync(project.Id, run.Number, c.Number, TestResultStatus.Failed, "sweep");
        await tests.AddEvidenceAsync(project.Id, run.Number, c.Number, "sweep.txt", new MemoryStream("sweep"u8.ToArray()));
        var releases = s.Get<ByteBench.Services.Releases.ReleaseService>();
        var release = await releases.CreateAsync(repo, new ByteBench.Services.Releases.ReleaseInput { TagName = "sweep-v1", Target = "main", Title = "Sweep release" }, alice);
        await releases.UploadAssetAsync(repo, release.Id, "sweep.bin", new MemoryStream("sweep"u8.ToArray()), alice);
        await s.Get<ByteBench.Services.Releases.ReleaseAutoAttachService>().SaveRuleAsync(repo.Id, true, "dist/**");
    }
}

/// <summary>
/// Permanent regression sweep over every mapped <c>/api</c> route (read from the routing endpoint data source, so a new route is
/// covered automatically): (a) no credentials → 401, (b) a full-scope token of a user who is not a member of the private project
/// → 401/403/404, (c) a read-only (read_api) token of the project owner on a write route → 401/403. Routes that are public or not
/// project-scoped must be listed in the allow-lists below, with a reason.
/// </summary>
public class AuthorizationSweepTests(AuthorizationSweepApp app, ITestOutputHelper output) : IClassFixture<AuthorizationSweepApp>
{
    /// <summary>Routes that answer without credentials (pattern → reason).</summary>
    private static readonly Dictionary<string, string> PublicRoutes = new()
    {
        ["/api/{documentName}/openapi.json"] = "the OpenAPI description of the API (no data)",
        ["/api/{**rest}"] = "the JSON 404 for unknown API paths",
        ["/api/v1/server-info"] = "name, version and capabilities for the desktop app (no user data)",
    };

    /// <summary>Routes a signed-in user who is not a member of the private project may get a 2xx from (pattern → reason).</summary>
    /// <summary>
    /// GET routes in this list are also checked by <see cref="Listings_hide_the_private_project"/>: their 2xx body must not mention it.
    /// </summary>
    private static readonly Dictionary<string, string> NotProjectScoped = new()
    {
        ["GET /api/v1/user"] = "the caller's own account",
        ["GET /api/v1/user/starred"] = "the caller's own stars",
        ["GET /api/v1/projects"] = "lists only visible projects",
        ["GET /api/v1/search"] = "global search, filtered by visibility",
        ["GET /api/v1/search/issues"] = "GQL search, filtered by visibility",
        ["GET /api/v1/users/{username}"] = "public profile of another user, filtered by visibility",
        ["GET /api/v1/groups/"] = "lists only visible groups",
        ["GET /api/v1/groups/{group}"] = "an internal group (the seed's Acme)",
        ["GET /api/v1/groups/{group}/issues"] = "an internal group's issues, filtered by project visibility",
        ["GET /api/v1/groups/{group}/projects"] = "an internal group's projects, filtered by visibility",
        ["GET /api/v1/groups/{group}/roadmap"] = "an internal group's roadmap, filtered by project visibility",
        ["GET /api/v1/groups/{group}/subgroups"] = "an internal group's subgroups",
        ["GET /api/v1/dashboards"] = "the caller's own and shared dashboards",
        ["GET /api/v1/workspaces/"] = "the caller's own workspaces (never anyone else's)",
        ["GET /api/v1/workspaces/shared"] = "workspaces the caller was invited to (a non-member has none)",
        ["GET /api/v1/notifications/"] = "the caller's own notifications",
        ["POST /api/v1/notifications/{id:int}/read"] = "marks the caller's own notification read; anyone else's id is a no-op",
        ["GET /api/v1/snippets/"] = "the caller's own and visible snippets",
        ["POST /api/v1/snippets/"] = "creates a personal snippet (the empty test body is refused with 422)",
        ["POST /api/v1/projects/{key}/repos/{slug}/pull-requests/{number:int}/apply-suggestions"] =
            "validates the body (422) before looking anything up, so nothing about the project is revealed",
    };

    /// <summary>Query parameters every swept request carries, so required-parameter checks (400) do not hide what follows them.</summary>
    private const string Query = "?q=deploy&ref=main&path=README.md&base=main&head=main&from=2020-01-01&to=2030-01-01";

    private static readonly Regex Parameter = new(@"\{\**(?<name>[A-Za-z_][A-Za-z0-9_]*)[^}]*\}", RegexOptions.Compiled);

    private sealed record Route(string Method, string Pattern)
    {
        public string Name => $"{Method} {Pattern}";
    }

    private List<Route> ApiRoutes()
    {
        var sources = app.Services.GetRequiredService<EndpointDataSource>();
        var routes = new List<Route>();
        foreach (var endpoint in sources.Endpoints.OfType<RouteEndpoint>())
        {
            var pattern = "/" + (endpoint.RoutePattern.RawText ?? "").TrimStart('/');
            if (!pattern.StartsWith("/api/", StringComparison.Ordinal)) continue;
            var methods = endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods;
            foreach (var m in methods is { Count: > 0 } ? methods : ["GET", "POST"])
                if (m is not ("OPTIONS" or "HEAD")) routes.Add(new Route(m, pattern));
        }
        return routes.DistinctBy(r => r.Name).OrderBy(r => r.Pattern).ThenBy(r => r.Method).ToList();
    }

    private sealed record Values(string Key, string Slug, string Sha, List<int> Ids, List<int> Numbers);

    private Task<Values> TargetValuesAsync() => ValuesForAsync(AuthorizationSweepApp.TargetKey);

    private async Task<Values> ValuesForAsync(string key)
    {
        await using var db = app.Db();
        var project = await db.Projects.AsNoTracking().FirstAsync(p => p.Key == key);
        var repo = await db.Repositories.AsNoTracking().Where(r => r.ProjectId == project.Id).OrderBy(r => r.Id).FirstAsync();
        var repoIds = await db.Repositories.Where(r => r.ProjectId == project.Id).Select(r => r.Id).ToListAsync();
        var path = app.Services.GetRequiredService<ByteBench.Services.RepoStorage>().PathFor(project.Key, repo.Slug);
        var sha = ByteBench.Services.Git.GitOps.ResolveSha(path, "HEAD") ?? "0000000000000000000000000000000000000000";

        // A couple of ids from every table that belongs to the project (directly or through one of its repositories).
        var ids = new SortedSet<int> { project.Id };
        var connection = db.Database.GetDbConnection();
        await connection.OpenAsync();
        foreach (var entity in db.Model.GetEntityTypes())
        {
            var table = entity.GetTableName();
            var id = entity.FindProperty("Id");
            if (table == null || id?.ClrType != typeof(int) || entity.GetViewName() != null) continue;
            var store = Microsoft.EntityFrameworkCore.Metadata.StoreObjectIdentifier.Table(table, entity.GetSchema());
            string? filter = null;
            if (entity.FindProperty("ProjectId") is { } pid) filter = $"\"{pid.GetColumnName(store)}\" = {project.Id}";
            else if (entity.FindProperty("RepositoryId") is { } rid) filter = $"\"{rid.GetColumnName(store)}\" IN ({string.Join(",", repoIds)})";
            if (filter == null) continue;
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = $"SELECT \"{id.GetColumnName(store)}\" FROM \"{table}\" WHERE {filter} ORDER BY 1 LIMIT 2";
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync()) ids.Add(Convert.ToInt32(reader.GetValue(0)));
        }
        var numbers = await db.Issues.Where(i => i.ProjectId == project.Id).OrderBy(i => i.Number).Select(i => i.Number).Take(3).ToListAsync();
        numbers.AddRange(await db.PullRequests.Where(p => repoIds.Contains(p.RepositoryId)).OrderBy(p => p.Number).Select(p => p.Number).Take(3).ToListAsync());
        return new Values(project.Key, repo.Slug, sha, ids.ToList(), numbers.Distinct().ToList());
    }

    /// <summary>URLs for a route: one per candidate id/number when the route has integer parameters.</summary>
    private static IEnumerable<string> Urls(Route route, Values v)
    {
        var names = Parameter.Matches(route.Pattern).Select(m => m.Groups["name"].Value).ToList();
        var numeric = names.Any(n => n.Equals("number", StringComparison.OrdinalIgnoreCase) || n.Equals("id", StringComparison.OrdinalIgnoreCase) || n.EndsWith("Id", StringComparison.Ordinal)
                                     || n.EndsWith("Number", StringComparison.Ordinal));
        var count = numeric ? Math.Max(v.Ids.Count, v.Numbers.Count) : 1;
        for (var i = 0; i < count; i++)
        {
            var index = i;
            yield return Parameter.Replace(route.Pattern, m => m.Groups["name"].Value switch
            {
                "key" or "projectKey" => v.Key,
                "slug" or "repo" => v.Slug,
                "number" or "caseNumber" => v.Numbers[index % v.Numbers.Count].ToString(), // int-constrained: a made-up value would fall through to the catch-all 404
                var n when n == "id" || n.EndsWith("Id", StringComparison.Ordinal) => v.Ids[index % v.Ids.Count].ToString(),
                "sha" => v.Sha,
                "documentName" => "v1",
                "username" => "alice",
                "group" => "acme",
                "path" or "file" or "fileName" or "tag" => "README.md",
                "rest" => "nothing-here",
                _ => "x",
            }) + Query + (route.Pattern.Contains("/snippets") ? "" : "&rev=main"); // a snippet's ?rev= is a number
        }
    }

    private HttpClient Client(string? token)
    {
        var client = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });
        if (token != null) client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static async Task<HttpStatusCode> SendAsync(HttpClient client, string method, string url)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), url);
        if (method is not ("GET" or "DELETE")) request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
        using var response = await client.SendAsync(request);
        return response.StatusCode;
    }

    [Fact]
    public async Task Every_api_route_requires_credentials()
    {
        await app.EnsureReadyAsync();
        var values = await TargetValuesAsync();
        var client = Client(null);
        var failures = new List<string>();
        foreach (var route in ApiRoutes().Where(r => !PublicRoutes.ContainsKey(r.Pattern)))
        {
            var url = Urls(route, values).First();
            var status = await SendAsync(client, route.Method, url);
            if (status != HttpStatusCode.Unauthorized) failures.Add($"{route.Name} ({url}) → {(int)status}");
        }
        foreach (var f in failures) output.WriteLine(f);
        Assert.True(failures.Count == 0, "Routes that answered without credentials (add real ones to PublicRoutes with a reason):\n" + string.Join("\n", failures));
    }

    [Fact]
    public async Task Non_members_get_nothing_from_a_private_project()
    {
        await app.EnsureReadyAsync();
        var values = await TargetValuesAsync();
        var client = Client(await app.CreateTokenAsync("sweep-outsider",
            TokenScopes.ReadApi | TokenScopes.WriteApi | TokenScopes.ReadRepository | TokenScopes.WriteRepository));
        var failures = new List<string>();
        var routes = ApiRoutes().Where(r => !PublicRoutes.ContainsKey(r.Pattern) && !NotProjectScoped.ContainsKey(r.Name)).ToList();
        foreach (var route in routes)
            foreach (var url in Urls(route, values))
            {
                var status = await SendAsync(client, route.Method, url);
                if (status is not (HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound))
                {
                    failures.Add($"{route.Name} ({url}) → {(int)status}");
                    break;
                }
            }
        foreach (var f in failures) output.WriteLine(f);
        Assert.True(failures.Count == 0, $"Routes ({routes.Count} swept) that did not refuse a non-member of a private project:\n" + string.Join("\n", failures));
    }

    [Fact]
    public async Task Read_only_tokens_cannot_call_write_routes()
    {
        await app.EnsureReadyAsync();
        var values = await TargetValuesAsync();
        var client = Client(await app.CreateTokenAsync("alice", TokenScopes.ReadApi | TokenScopes.ReadRepository));
        var failures = new List<string>();
        foreach (var route in ApiRoutes().Where(r => r.Method != "GET" && !PublicRoutes.ContainsKey(r.Pattern)))
        {
            var url = Urls(route, values).First();
            var status = await SendAsync(client, route.Method, url);
            if (status is not (HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)) failures.Add($"{route.Name} ({url}) → {(int)status}");
        }
        foreach (var f in failures) output.WriteLine(f);
        Assert.True(failures.Count == 0, "Write routes that a read-only token was not refused on:\n" + string.Join("\n", failures));
    }

    [Fact]
    public async Task Listings_hide_the_private_project()
    {
        await app.EnsureReadyAsync();
        var values = await TargetValuesAsync();
        // Search terms that match the seeded private project's issues and repository.
        var client = Client(await app.CreateTokenAsync("sweep-outsider", TokenScopes.ReadApi));
        var failures = new List<string>();
        foreach (var route in ApiRoutes().Where(r => r.Method == "GET" && NotProjectScoped.ContainsKey(r.Name)))
            foreach (var q in new[] { "deploy", "WEB", "project = WEB", "web-app" })
            {
                var url = Urls(route, values).First().Replace("q=deploy", "q=" + Uri.EscapeDataString(q));
                using var response = await client.GetAsync(url);
                var body = Regex.Replace(await response.Content.ReadAsStringAsync(), "\"query\":\"[^\"]*\"", ""); // the echoed search term
                if (response.IsSuccessStatusCode && (body.Contains("\"WEB\"") || body.Contains("WEB-") || body.Contains("/WEB/") || body.Contains("web-app")))
                    failures.Add($"{route.Name} ({url}) mentions the private project: {body[..Math.Min(body.Length, 500)]}");
            }
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    /// <summary>
    /// Control: the same URLs work for the owner, so the refusals above come from permission checks and not from made-up ids.
    /// </summary>
    [Fact]
    public async Task Owner_gets_data_from_the_swept_urls()
    {
        await app.EnsureReadyAsync();
        var values = await TargetValuesAsync();
        var client = Client(await app.CreateTokenAsync("alice", TokenScopes.ReadApi));
        var routes = ApiRoutes().Where(r => r.Method == "GET" && r.Pattern.StartsWith("/api/v1/projects/{key}", StringComparison.Ordinal)).ToList();
        var hits = new List<string>();
        var misses = new List<string>();
        foreach (var route in routes)
        {
            var ok = false;
            foreach (var url in Urls(route, values))
                if ((int)await SendAsync(client, "GET", url) is >= 200 and < 300) { ok = true; break; }
            (ok ? hits : misses).Add(route.Name);
        }
        output.WriteLine($"{hits.Count}/{routes.Count} project GET routes answered the owner. Misses:\n" + string.Join("\n", misses));
        Assert.True(hits.Count * 100 / routes.Count >= 60, $"Only {hits.Count} of {routes.Count} project GET routes answered the owner; the sweep's sample values no longer reach real data.");
    }

    /// <summary>
    /// IDOR: a member of the target project calls its routes with ids taken from another private project they can't see
    /// (OPS), and nothing of that project may come back. Judged by content, not status: ids are per table, so an id can
    /// legitimately exist in both projects.
    /// </summary>
    [Fact]
    public async Task Ids_from_another_project_return_nothing_of_it()
    {
        await app.EnsureReadyAsync();
        var target = await TargetValuesAsync();
        var other = await ValuesForAsync("OPS");
        string[] markers;
        await using (var db = app.Db())
        {
            var ops = await db.Projects.FirstAsync(p => p.Key == "OPS");
            markers = ["OPS-", "\"OPS\"", "/OPS/", "infra.git", // not the project name: WEB has a component called "Infrastructure"
                .. await db.Issues.Where(i => i.ProjectId == ops.Id).Select(i => i.Title).ToListAsync()];
        }
        var outsider = Client(await app.CreateTokenAsync("bob", TokenScopes.ReadApi)); // WEB developer, not in OPS
        var mixed = target with { Ids = other.Ids };
        var failures = new List<string>();
        foreach (var route in ApiRoutes().Where(r => r.Method == "GET" && r.Pattern.StartsWith("/api/v1/projects/{key}", StringComparison.Ordinal)))
            foreach (var url in Urls(route, mixed))
            {
                using var response = await outsider.GetAsync(url);
                if (!response.IsSuccessStatusCode) continue;
                var body = await response.Content.ReadAsStringAsync();
                if (markers.FirstOrDefault(m => body.Contains(m, StringComparison.Ordinal)) is { } hit)
                {
                    failures.Add($"{route.Name} ({url}) returned OPS data ({hit})");
                    break;
                }
            }
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    /// <summary>
    /// The browser download routes outside /api (source archives, release assets): anonymous callers are refused and a signed-in
    /// non-member gets nothing, while the owner gets the files (the control that proves the URLs are real).
    /// </summary>
    [Fact]
    public async Task Web_downloads_refuse_anonymous_callers_and_non_members()
    {
        await app.EnsureReadyAsync();
        var values = await TargetValuesAsync();
        int assetId;
        await using (var db = app.Db())
        {
            var repoIds = await db.Repositories.Where(r => r.Project.Key == AuthorizationSweepApp.TargetKey).Select(r => r.Id).ToListAsync();
            assetId = await db.ReleaseAssets.Where(a => repoIds.Contains(a.Release.RepositoryId)).Select(a => a.Id).FirstAsync();
        }
        var urls = new[]
        {
            $"/p/{values.Key}/repos/{values.Slug}/archive/main.zip",
            $"/p/{values.Key}/repos/{values.Slug}/archive/main.tar.gz",
            $"/release-assets/{assetId}/sweep.bin",
        };
        var anonymous = Client(null);
        var outsider = Client(await app.CreateTokenAsync("sweep-outsider", TokenScopes.ReadApi | TokenScopes.ReadRepository));
        var owner = Client(await app.CreateTokenAsync("alice", TokenScopes.ReadApi | TokenScopes.ReadRepository));
        foreach (var url in urls)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(url)).StatusCode);
            var status = (await outsider.GetAsync(url)).StatusCode;
            Assert.True(status is HttpStatusCode.NotFound or HttpStatusCode.Forbidden, $"{url} gave a non-member {(int)status}");
            Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync(url)).StatusCode);
        }
    }

    [Fact]
    public async Task Sweep_sees_the_api()
    {
        await app.EnsureReadyAsync();
        var routes = ApiRoutes();
        output.WriteLine(string.Join("\n", routes.Select(r => r.Name)));
        Assert.True(routes.Count > 150, $"Only {routes.Count} /api routes found: the sweep is not seeing the endpoint data source.");
        // Every allow-list entry must still exist, so the lists do not rot.
        foreach (var name in NotProjectScoped.Keys)
            Assert.Contains(routes, r => r.Name == name);
    }
}
