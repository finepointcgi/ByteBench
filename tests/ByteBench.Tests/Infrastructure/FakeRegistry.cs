using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

namespace ByteBench.Tests.Infrastructure;

/// <summary>
/// A fake package registry on a local port serving the four public APIs the dependency bot reads (npm packuments, NuGet v3 flat
/// container, PyPI JSON, Go proxy <c>@v/list</c>). Never talks to a real registry. Unknown paths answer 404.
/// </summary>
public sealed class FakeRegistry : IDisposable
{
    private readonly HttpListener listener = new();
    private readonly Dictionary<string, (int Status, string Body)> routes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> redirects = new(StringComparer.Ordinal);
    private readonly List<(string Prefix, string Header)> authRules = [];
    public string Url { get; }
    public string Npm => Url + "/npm/";
    public string NuGet => Url + "/nuget/";
    public string PyPI => Url + "/pypi/";
    public string Go => Url + "/go/";
    public List<string> Requests { get; } = [];
    /// <summary>Every request's path and the Authorization header it carried (null when none), in arrival order.</summary>
    public List<(string Path, string? Authorization)> Auths { get; } = [];
    public int Count { get { lock (Requests) return Requests.Count; } }

    public FakeRegistry()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        Url = $"http://127.0.0.1:{port}";
        listener.Prefixes.Add(Url + "/");
        listener.Start();
        _ = Task.Run(Loop);
    }

    /// <summary>Serves <paramref name="body"/> at the raw request path (e.g. <c>/npm/left-pad</c>).</summary>
    public FakeRegistry Serve(string rawPath, string body, int status = 200) { lock (routes) routes[rawPath] = (status, body); return this; }

    /// <summary>Requests whose path starts with <paramref name="prefix"/> must carry exactly this Authorization header, else 401.</summary>
    public FakeRegistry RequireAuth(string prefix, string header) { lock (routes) authRules.Add((prefix, header)); return this; }
    public FakeRegistry RequireBasic(string prefix, string user, string password) => RequireAuth(prefix, "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(user + ":" + password)));
    public FakeRegistry RequireBearer(string prefix, string token) => RequireAuth(prefix, "Bearer " + token);

    /// <summary>Answers 302 with a Location header at the raw request path.</summary>
    public FakeRegistry Redirect(string rawPath, string location) { lock (routes) redirects[rawPath] = location; return this; }

    /// <summary>A NuGet v3 service index at <paramref name="rawPath"/> whose PackageBaseAddress/3.0.0 resource is <paramref name="packageBase"/>.</summary>
    public FakeRegistry NuGetServiceIndex(string rawPath, string packageBase) => Serve(rawPath, new JsonObject
    {
        ["version"] = "3.0.0",
        ["resources"] = new JsonArray(
            new JsonObject { ["@id"] = packageBase, ["@type"] = "PackageBaseAddress/3.0.0" },
            new JsonObject { ["@id"] = Url + "/search/query", ["@type"] = "SearchQueryService" }),
    }.ToJsonString());

    /// <summary>A PEP 691 simple project page at <c>{simplePath}/{name}/</c>.</summary>
    public FakeRegistry PySimple(string simplePath, string name, params string[] versions) => Serve($"{simplePath.TrimEnd('/')}/{name.ToLowerInvariant()}/", new JsonObject
    {
        ["name"] = name.ToLowerInvariant(),
        ["files"] = new JsonArray(versions.Select(v => (JsonNode)new JsonObject { ["filename"] = $"{name.ToLowerInvariant()}-{v}.tar.gz", ["url"] = "https://files.example.invalid/x.tar.gz" }).ToArray()),
    }.ToJsonString());

    public FakeRegistry NpmPackage(string name, string[] versions, string? deprecated = null, string? homepage = null)
    {
        var v = new JsonObject();
        foreach (var ver in versions) v[ver] = new JsonObject { ["name"] = name, ["version"] = ver };
        if (deprecated != null) v[versions[^1]]!["deprecated"] = deprecated;
        var doc = new JsonObject { ["name"] = name, ["versions"] = v };
        if (homepage != null) doc["homepage"] = homepage;
        return Serve("/npm/" + name.Replace("/", "%2F"), doc.ToJsonString());
    }

    public FakeRegistry NuGetPackage(string id, params string[] versions) =>
        Serve($"/nuget/{id.ToLowerInvariant()}/index.json", new JsonObject { ["versions"] = new JsonArray(versions.Select(v => (JsonNode)v).ToArray()) }.ToJsonString());

    public FakeRegistry PyPiPackage(string name, params string[] versions)
    {
        var releases = new JsonObject();
        foreach (var v in versions) releases[v] = new JsonArray(new JsonObject { ["filename"] = $"{name}-{v}.tar.gz", ["yanked"] = false });
        return Serve($"/pypi/{name.ToLowerInvariant()}/json", new JsonObject
        {
            ["info"] = new JsonObject { ["name"] = name, ["version"] = versions[^1], ["project_urls"] = new JsonObject { ["Changelog"] = $"https://example.test/{name}/changes" } },
            ["releases"] = releases,
        }.ToJsonString());
    }

    public FakeRegistry GoModule(string module, params string[] versions) => Serve($"/go/{module}/@v/list", string.Join("\n", versions) + "\n");

    private async Task Loop()
    {
        while (listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await listener.GetContextAsync(); }
            catch (Exception) { return; }
            var path = ctx.Request.RawUrl!.Split('?')[0];
            string? authorization = ctx.Request.Headers["Authorization"];
            lock (Requests) { Requests.Add(path); Auths.Add((path, authorization)); }
            (int Status, string Body) answer;
            string? location = null;
            lock (routes)
            {
                answer = routes.TryGetValue(path, out var r) ? r : (404, "{}");
                if (redirects.TryGetValue(path, out var loc)) { location = loc; answer = (302, ""); }
                var rule = authRules.FirstOrDefault(a => path.StartsWith(a.Prefix, StringComparison.Ordinal));
                if (rule.Prefix != null && authorization != rule.Header) { answer = (401, "{}"); location = null; }
            }
            try
            {
                var bytes = Encoding.UTF8.GetBytes(answer.Body);
                ctx.Response.StatusCode = answer.Status;
                if (location != null) ctx.Response.RedirectLocation = location;
                ctx.Response.ContentType = path.EndsWith("/@v/list", StringComparison.Ordinal) ? "text/plain" : "application/json";
                ctx.Response.ContentLength64 = bytes.Length;
                await ctx.Response.OutputStream.WriteAsync(bytes);
            }
            catch (Exception) { /* client went away */ }
            finally { ctx.Response.Close(); }
        }
    }

    public void Dispose() { try { listener.Close(); } catch (ObjectDisposedException) { } }
}
