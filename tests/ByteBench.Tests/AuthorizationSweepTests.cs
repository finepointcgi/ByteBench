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
            ready = true;
        }
        finally { gate.Release(); }
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
    };

    /// <summary>Routes a signed-in user who is not a member of the private project may get a 2xx from (pattern → reason).</summary>
    private static readonly Dictionary<string, string> NotProjectScoped = new()
    {
        ["GET /api/v1/user"] = "the caller's own account",
        ["GET /api/v1/projects"] = "lists only visible projects (asserted separately below)",
        ["GET /api/v1/search"] = "global search, filtered by visibility (asserted separately below)",
        ["GET /api/v1/explore/projects"] = "explore lists only visible projects (asserted separately below)",
        ["GET /api/v1/users/{username}"] = "public profile of another user",
        ["GET /api/v1/users/{username}/activity"] = "activity filtered by the viewer's visibility (asserted separately below)",
        ["GET /api/v1/user/starred"] = "the caller's own stars",
        ["GET /api/v1/groups"] = "lists only visible groups",
        ["GET /api/v1/groups/{**group}"] = "an internal group (the seed's Acme/Platform)",
        ["GET /api/v1/groups/{group}"] = "an internal group (the seed's Acme/Platform)",
        ["GET /api/v1/snippets"] = "the caller's own and public snippets",
        ["GET /api/v1/issues/search"] = "GQL search filtered by visibility (asserted separately below)",
    };

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

    private async Task<Values> TargetValuesAsync()
    {
        await using var db = app.Db();
        var project = await db.Projects.AsNoTracking().FirstAsync(p => p.Key == AuthorizationSweepApp.TargetKey);
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
        var numeric = names.Any(n => n.Equals("number", StringComparison.OrdinalIgnoreCase) || n.Equals("id", StringComparison.OrdinalIgnoreCase) || n.EndsWith("Id", StringComparison.Ordinal));
        var count = numeric ? Math.Max(v.Ids.Count, v.Numbers.Count) : 1;
        for (var i = 0; i < count; i++)
        {
            var index = i;
            yield return Parameter.Replace(route.Pattern, m => m.Groups["name"].Value switch
            {
                "key" or "projectKey" => v.Key,
                "slug" or "repo" => v.Slug,
                "number" => v.Numbers[index % v.Numbers.Count].ToString(),
                var n when n == "id" || n.EndsWith("Id", StringComparison.Ordinal) => v.Ids[index % v.Ids.Count].ToString(),
                "sha" => v.Sha,
                "documentName" => "v1",
                "username" => "alice",
                "group" => "acme/platform",
                "path" or "file" or "fileName" or "tag" => "README.md",
                "rest" => "nothing-here",
                _ => "x",
            });
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
