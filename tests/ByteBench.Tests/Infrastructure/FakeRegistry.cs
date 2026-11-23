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
    public string Url { get; }
    public string Npm => Url + "/npm/";
    public string NuGet => Url + "/nuget/";
    public string PyPI => Url + "/pypi/";
    public string Go => Url + "/go/";
    public List<string> Requests { get; } = [];
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
            lock (Requests) Requests.Add(path);
            (int Status, string Body) answer;
            lock (routes) answer = routes.TryGetValue(path, out var r) ? r : (404, "{}");
            try
            {
                var bytes = Encoding.UTF8.GetBytes(answer.Body);
                ctx.Response.StatusCode = answer.Status;
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
