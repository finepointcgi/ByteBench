using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

namespace ByteBench.Tests.Infrastructure;

/// <summary>
/// A fake GitHub REST v3 API on a local port for one repository (shapes copied from the public REST documentation: repository,
/// issues with pull requests mixed in, labels, milestones and comments, Link headers for paging). Never talks to the real GitHub.
/// </summary>
public sealed class FakeGitHub : IDisposable
{
    public const string Token = "ghp_FakeTokenForTests0123456789abcdef";
    public const string Repo = "acme/widgets";

    private readonly HttpListener listener = new();
    public string Url { get; }
    /// <summary>The API root to give the importer (this fake serves it at the root, like github.com's api host).</summary>
    public string ApiRoot => Url;
    public bool HasIssues { get; set; } = true;
    public bool RepoExists { get; set; } = true;
    /// <summary>When set, requests need <c>Authorization: Bearer Token</c> (else 401).</summary>
    public bool RequireToken { get; set; }
    public int PageSize { get; set; } = 3;
    /// <summary>The next N requests to /issues answer 403 with <c>X-RateLimit-Remaining: 0</c> and <c>Retry-After</c>.</summary>
    public int RateLimitIssueRequests { get; set; }
    public List<JsonObject> Issues { get; set; } = DefaultIssues();
    public Dictionary<int, List<JsonObject>> Comments { get; set; } = DefaultComments();
    public List<(string Method, string PathAndQuery, string? Auth, string? Accept, string? ApiVersion)> Requests { get; } = [];
    public int Hits(string pathPrefix) { lock (Requests) return Requests.Count(r => r.PathAndQuery.StartsWith(pathPrefix, StringComparison.Ordinal)); }

    public static JsonObject User(string login, int id) => new() { ["login"] = login, ["id"] = id, ["type"] = "User", ["site_admin"] = false };

    private static JsonObject Label(string name) => new() { ["id"] = Math.Abs(name.GetHashCode()) & 0xffff, ["name"] = name, ["color"] = "d73a4a", ["default"] = false };

    public static JsonObject Milestone1() => new() { ["number"] = 1, ["title"] = "v1.0", ["state"] = "open", ["description"] = "First release", ["due_on"] = "2026-09-01T07:00:00Z" };

    public static JsonObject Issue(int number, string title, string state = "open", string[]? labels = null, string created = "2026-01-01T10:00:00Z", Action<JsonObject>? tweak = null)
    {
        var j = new JsonObject
        {
            ["id"] = 1000 + number, ["number"] = number, ["title"] = title, ["state"] = state, ["state_reason"] = state == "closed" ? "completed" : null,
            ["html_url"] = $"https://github.com/{Repo}/issues/{number}", ["body"] = "Body of " + title, ["user"] = User("bob", 11),
            ["labels"] = new JsonArray((labels ?? []).Select(l => (JsonNode)Label(l)).ToArray()), ["assignee"] = null, ["assignees"] = new JsonArray(),
            ["milestone"] = null, ["comments"] = 0, ["created_at"] = created, ["updated_at"] = created, ["closed_at"] = state == "closed" ? "2026-02-01T10:00:00Z" : null,
        };
        tweak?.Invoke(j);
        return j;
    }

    /// <summary>8 items, oldest first: #1 bug+priority with a milestone, assignee and 2 comments; #2 a pull request; #3 closed feature; #4 epic; #5 closed as not planned;
    /// #6 sub-issue of #4; #7 sub-issue of the newer #8; #8 a plain issue.</summary>
    public static List<JsonObject> DefaultIssues() =>
    [
        Issue(1, "Crash on save", labels: ["bug", "priority: high"], created: "2026-01-01T10:00:00Z", tweak: j =>
        {
            j["milestone"] = Milestone1(); j["assignee"] = User("bob", 11); j["assignees"] = new JsonArray(User("bob", 11)); j["comments"] = 2;
        }),
        Issue(2, "Fix crash on save", created: "2026-01-02T10:00:00Z", tweak: j => j["pull_request"] = new JsonObject { ["url"] = $"https://api.github.com/repos/{Repo}/pulls/2" }),
        Issue(3, "Add dark mode", state: "closed", labels: ["enhancement", "p2"], created: "2026-01-03T10:00:00Z", tweak: j => j["assignees"] = new JsonArray(User("carol", 12))),
        Issue(4, "Roadmap", labels: ["epic", "priority/low"], created: "2026-01-04T10:00:00Z"),
        Issue(5, "Never mind", state: "closed", labels: ["question"], created: "2026-01-05T10:00:00Z", tweak: j => j["state_reason"] = "not_planned"),
        Issue(6, "Write the docs", labels: ["documentation", "P0"], created: "2026-01-06T10:00:00Z",
            tweak: j => j["parent_issue_url"] = $"https://api.github.com/repos/{Repo}/issues/4"),
        Issue(7, "Early child", created: "2026-01-07T10:00:00Z", tweak: j => j["parent_issue_url"] = $"https://api.github.com/repos/{Repo}/issues/8"),
        Issue(8, "Late parent", labels: ["feature request"], created: "2026-01-08T10:00:00Z"),
    ];

    public static Dictionary<int, List<JsonObject>> DefaultComments() => new()
    {
        [1] =
        [
            new() { ["id"] = 5001, ["body"] = "I'll look", ["user"] = User("bob", 11), ["created_at"] = "2026-01-01T12:00:00Z" },
            new() { ["id"] = 5002, ["body"] = "Same here", ["user"] = User("stranger", 99), ["created_at"] = "2026-01-01T13:00:00Z" },
        ],
    };

    public FakeGitHub()
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

    private async Task Loop()
    {
        while (listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await listener.GetContextAsync(); } catch { return; }
            _ = Task.Run(() => Handle(ctx));
        }
    }

    private void Handle(HttpListenerContext ctx)
    {
        try
        {
            var req = ctx.Request;
            var auth = req.Headers["Authorization"];
            lock (Requests) Requests.Add((req.HttpMethod, req.Url!.PathAndQuery, auth, req.Headers["Accept"], req.Headers["X-GitHub-Api-Version"]));
            var (status, json) = Route(req, auth, ctx.Response);
            var bytes = Encoding.UTF8.GetBytes(json ?? "");
            ctx.Response.StatusCode = status;
            ctx.Response.ContentType = "application/json; charset=utf-8";
            ctx.Response.OutputStream.Write(bytes);
        }
        catch { /* client gave up */ }
        finally { try { ctx.Response.Close(); } catch { } }
    }

    private (int, string?) Route(HttpListenerRequest req, string? auth, HttpListenerResponse res)
    {
        if (RequireToken && auth != "Bearer " + Token) return (401, "{\"message\":\"Bad credentials\"}");
        var path = req.Url!.AbsolutePath.TrimEnd('/');
        var query = req.QueryString;
        var prefix = "/repos/" + Repo;
        if (!RepoExists || !path.StartsWith(prefix, StringComparison.Ordinal)) return (404, "{\"message\":\"Not Found\"}");
        var rest = path[prefix.Length..];
        if (rest == "")
            return (200, new JsonObject
            {
                ["id"] = 1, ["full_name"] = Repo, ["name"] = "widgets", ["description"] = "All the widgets", ["private"] = RequireToken, ["has_issues"] = HasIssues,
                ["open_issues_count"] = Issues.Count(i => i["state"]!.GetValue<string>() == "open"),
            }.ToJsonString());
        if (rest == "/labels") return Page(req, res, new[] { "bug", "enhancement", "epic", "question" }.Select(Label).ToList());
        if (rest == "/milestones")
        {
            var closed = Milestone1(); closed["number"] = 2; closed["title"] = "v0.9"; closed["state"] = "closed"; closed["description"] = null; closed["due_on"] = null;
            return Page(req, res, [Milestone1(), closed]);
        }
        if (rest == "/issues")
        {
            if (RateLimitIssueRequests > 0)
            {
                RateLimitIssueRequests--;
                res.Headers["Retry-After"] = "1";
                res.Headers["X-RateLimit-Remaining"] = "0";
                return (403, "{\"message\":\"API rate limit exceeded\"}");
            }
            var state = query["state"] ?? "open";
            var items = Issues.Where(i => state == "all" || i["state"]!.GetValue<string>() == state).ToList();
            if (query["direction"] == "desc") items.Reverse();
            return Page(req, res, items);
        }
        var parts = rest.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 3 && parts[0] == "issues" && parts[2] == "comments" && int.TryParse(parts[1], out var n))
            return Page(req, res, Comments.GetValueOrDefault(n) ?? []);
        return (404, "{\"message\":\"Not Found\"}");
    }

    private (int, string?) Page(HttpListenerRequest req, HttpListenerResponse res, List<JsonObject> items)
    {
        var page = int.TryParse(req.QueryString["page"], out var p) && p > 0 ? p : 1;
        var size = Math.Min(PageSize, int.TryParse(req.QueryString["per_page"], out var pp) && pp > 0 ? pp : PageSize);
        var slice = items.Skip((page - 1) * size).Take(size).Select(i => JsonNode.Parse(i.ToJsonString())).ToArray();
        if (page * size < items.Count)
        {
            var q = System.Web.HttpUtility.ParseQueryString(req.Url!.Query);
            q["page"] = (page + 1).ToString();
            q["per_page"] = size.ToString();
            res.Headers["Link"] = $"<{Url}{req.Url.AbsolutePath}?{q}>; rel=\"next\", <{Url}{req.Url.AbsolutePath}?page={(items.Count + size - 1) / size}>; rel=\"last\"";
        }
        return (200, new JsonArray(slice).ToJsonString());
    }

    public void Dispose() { try { listener.Stop(); listener.Close(); } catch { } }
}
