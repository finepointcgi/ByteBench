using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

namespace ByteBench.Tests.Infrastructure;

/// <summary>
/// A fake GitLab REST API v4 on a local port (shapes copied from docs.gitlab.com/ee/api: projects, issues, issues_statistics, labels,
/// milestones, notes and issue links; pages come with both <c>Link</c> and <c>X-Next-Page</c> headers). It can sit under a relative
/// URL root, answer 429 once, or turn issues off. Never talks to a real GitLab.
/// </summary>
public sealed class FakeGitLab : IDisposable
{
    public const string Token = "glpat-fake-token-0123456789";
    public const string ProjectPath = "acme/team/widgets";
    public const int ProjectId = 42;

    public sealed record Request(string Path, string Query, string? Token)
    {
        public string Page => System.Web.HttpUtility.ParseQueryString(Query)["page"] ?? "1";
    }

    private readonly HttpListener listener = new();
    public string Origin { get; }
    /// <summary>The address to give the importer (origin plus relative URL root).</summary>
    public string Url => Origin + RootPath;
    public string RootPath { get; set; } = "";
    public string ExpectedToken { get; set; } = Token;
    public bool Public { get; set; }
    public bool IssuesEnabled { get; set; } = true;
    public bool RateLimitOnce { get; set; }
    public int PageSize { get; set; } = 2;
    public JsonArray Issues { get; set; }
    public Dictionary<int, JsonArray> Notes { get; } = [];
    public Dictionary<int, JsonArray> Links { get; } = [];
    public JsonArray Milestones { get; set; }
    public JsonArray Labels { get; set; }
    public List<Request> Requests { get; } = [];
    public int Hits(string pathContains) { lock (Requests) return Requests.Count(r => r.Path.Contains(pathContains, StringComparison.Ordinal)); }

    public FakeGitLab()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        Origin = $"http://127.0.0.1:{port}";
        listener.Prefixes.Add(Origin + "/");
        listener.Start();
        Issues = DefaultIssues();
        Milestones = DefaultMilestones();
        Labels = DefaultLabels();
        Notes[1] =
        [
            Note(501, "I can reproduce this", "bob", "Bob Builder", false),
            Note(502, "changed the description", "bob", "Bob Builder", true),
            Note(503, "Fixed, see ![shot](/uploads/ab12/fixed.png)", "stranger", "Some Stranger", false),
        ];
        Links[1] = [Link(1002, 2, ProjectId, "relates_to"), Link(1003, 3, ProjectId, "blocks")];
        Links[2] = [Link(1001, 1, ProjectId, "relates_to")];
        Links[3] = [Link(1001, 1, ProjectId, "is_blocked_by"), Link(5555, 9, 77, "relates_to")];
        _ = Task.Run(Loop);
    }

    public static JsonObject User(string username, string name) => new() { ["id"] = username.Length, ["username"] = username, ["name"] = name, ["state"] = "active" };

    private static JsonObject Note(int id, string body, string user, string name, bool system) =>
        new() { ["id"] = id, ["type"] = null, ["body"] = body, ["author"] = User(user, name), ["created_at"] = "2024-05-02T10:00:00.000Z", ["system"] = system, ["noteable_type"] = "Issue" };

    private static JsonObject Link(int id, int iid, int projectId, string type) =>
        new() { ["id"] = id, ["iid"] = iid, ["project_id"] = projectId, ["title"] = "linked " + iid, ["state"] = "opened", ["link_type"] = type, ["issue_link_id"] = id * 10 };

    private static JsonObject Epic() => new() { ["id"] = 900, ["iid"] = 3, ["title"] = "Auth overhaul", ["url"] = "/groups/acme/-/epics/3", ["group_id"] = 5 };

    private static JsonObject Issue(int id, int iid, string title, Action<JsonObject>? tweak = null)
    {
        var o = new JsonObject
        {
            ["id"] = id, ["iid"] = iid, ["project_id"] = ProjectId, ["title"] = title, ["description"] = "Body of " + title, ["state"] = "opened",
            ["created_at"] = $"2024-05-0{iid}T12:00:00.000Z", ["updated_at"] = $"2024-05-1{iid}T12:00:00.000Z", ["closed_at"] = null,
            ["labels"] = new JsonArray(), ["milestone"] = null, ["assignees"] = new JsonArray(), ["assignee"] = null,
            ["author"] = User("bob", "Bob Builder"), ["type"] = "ISSUE", ["issue_type"] = "issue", ["user_notes_count"] = 0, ["confidential"] = false,
            ["due_date"] = null, ["web_url"] = $"https://gitlab.example.com/acme/team/widgets/-/issues/{iid}", ["weight"] = null,
        };
        tweak?.Invoke(o);
        return o;
    }

    public static JsonArray DefaultIssues() =>
    [
        Issue(1001, 1, "Login fails", o =>
        {
            o["labels"] = new JsonArray("bug", "priority::high", "ui");
            o["assignees"] = new JsonArray(User("bob", "Bob Builder"));
            o["assignee"] = User("bob", "Bob Builder");
            o["description"] = "Steps ![screenshot](/uploads/ab12/shot.png) and [file](/uploads/ab12/log.txt)";
            o["user_notes_count"] = 2;
            o["weight"] = 5;
            o["milestone"] = new JsonObject { ["id"] = 7, ["iid"] = 1, ["project_id"] = ProjectId, ["title"] = "Release 1" };
            o["epic"] = Epic();
            o["epic_iid"] = 3;
        }),
        Issue(1002, 2, "Crash on save", o =>
        {
            o["issue_type"] = "incident"; o["type"] = "INCIDENT";
            o["state"] = "closed"; o["closed_at"] = "2024-06-01T08:30:00.000Z";
            o["due_date"] = "2024-05-30";
            o["milestone"] = new JsonObject { ["id"] = 99, ["iid"] = 2, ["group_id"] = 5, ["title"] = "Group Q2" };
            o["epic"] = Epic();
        }),
        Issue(1003, 3, "Secret roadmap", o => { o["confidential"] = true; o["labels"] = new JsonArray("priority: critical"); o["author"] = User("stranger", "Some Stranger"); }),
        Issue(1004, 4, "Dark mode", o => { o["description"] = null; o["labels"] = new JsonArray("feature", "priority/low"); o["weight"] = 500; }),
        Issue(1005, 5, "Old thing", o => { o["state"] = "closed"; o["closed_at"] = "2024-05-20T00:00:00.000Z"; o["labels"] = new JsonArray("enhancement"); }),
    ];

    public static JsonArray DefaultMilestones() =>
    [
        new JsonObject { ["id"] = 7, ["iid"] = 1, ["project_id"] = ProjectId, ["title"] = "Release 1", ["description"] = "First", ["state"] = "active", ["start_date"] = "2024-05-01", ["due_date"] = "2024-09-01" },
        new JsonObject { ["id"] = 8, ["iid"] = 2, ["project_id"] = ProjectId, ["title"] = "Release 0", ["description"] = null, ["state"] = "closed", ["start_date"] = null, ["due_date"] = null },
    ];

    public static JsonArray DefaultLabels() =>
    [
        new JsonObject { ["id"] = 1, ["name"] = "bug", ["color"] = "#d9534f", ["description"] = null },
        new JsonObject { ["id"] = 2, ["name"] = "priority::high", ["color"] = "#ff0000" },
        new JsonObject { ["id"] = 3, ["name"] = "ui", ["color"] = "#428bca" },
    ];

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
            var raw = ctx.Request.RawUrl ?? "/";
            var q = raw.IndexOf('?');
            var rawPath = q < 0 ? raw : raw[..q];
            var query = q < 0 ? "" : raw[(q + 1)..];
            var token = ctx.Request.Headers["PRIVATE-TOKEN"];
            lock (Requests) Requests.Add(new Request(Uri.UnescapeDataString(rawPath), query, token));
            var (status, json, headers) = Route(rawPath, query, token);
            ctx.Response.StatusCode = status;
            foreach (var (k, v) in headers) ctx.Response.AddHeader(k, v);
            ctx.Response.ContentType = "application/json";
            ctx.Response.OutputStream.Write(Encoding.UTF8.GetBytes(json ?? ""));
        }
        catch { /* the client gave up */ }
        finally { try { ctx.Response.Close(); } catch { } }
    }

    private (int, string?, Dictionary<string, string>) Route(string rawPath, string query, string? token)
    {
        var none = new Dictionary<string, string>();
        var segs = rawPath.Trim('/').Split('/').Select(Uri.UnescapeDataString).ToList();
        var root = RootPath.Trim('/');
        if (root.Length > 0)
        {
            var rootSegs = root.Split('/');
            if (segs.Count < rootSegs.Length || !segs.Take(rootSegs.Length).SequenceEqual(rootSegs)) return (404, "{\"message\":\"404 Not Found\"}", none);
            segs = segs.Skip(rootSegs.Length).ToList();
        }
        if (segs.Count < 3 || segs[0] != "api" || segs[1] != "v4" || segs[2] != "projects" || segs.Count < 4) return (404, "{\"message\":\"404 Not Found\"}", none);
        if (!Public && token != ExpectedToken) return (401, "{\"message\":\"401 Unauthorized\"}", none);
        if (segs[3] != ProjectPath && segs[3] != ProjectId.ToString()) return (404, "{\"message\":\"404 Project Not Found\"}", none);
        var rest = segs.Skip(4).ToList();
        var qs = System.Web.HttpUtility.ParseQueryString(query);
        if (rest.Count == 0)
            return (200, new JsonObject
            {
                ["id"] = ProjectId, ["name"] = "Widgets", ["name_with_namespace"] = "Acme / Team / Widgets", ["path_with_namespace"] = ProjectPath,
                ["description"] = "The widget factory", ["issues_enabled"] = IssuesEnabled, ["web_url"] = "https://gitlab.example.com/acme/team/widgets",
            }.ToJsonString(), none);
        if (rest is ["issues_statistics"])
            return (200, new JsonObject { ["statistics"] = new JsonObject { ["counts"] = new JsonObject { ["all"] = Issues.Count, ["closed"] = Issues.Count(i => (string?)i!["state"] == "closed"), ["opened"] = Issues.Count(i => (string?)i!["state"] == "opened") } } }.ToJsonString(), none);
        if (rest is ["labels"]) return Page(Labels, qs);
        if (rest is ["milestones"]) return Page(Milestones, qs);
        if (rest is ["issues"])
        {
            if (RateLimitOnce) { RateLimitOnce = false; return (429, "{\"message\":\"Retry later\"}", new() { ["Retry-After"] = "1" }); }
            var onlyOpen = qs["state"] == "opened";
            var items = new JsonArray(Issues.Where(i => !onlyOpen || (string?)i!["state"] == "opened").Select(i => (JsonNode?)i!.DeepClone()).ToArray());
            return Page(items, qs);
        }
        if (rest is ["issues", var iidText, var what] && int.TryParse(iidText, out var iid))
        {
            if (what == "notes") return Page(Notes.GetValueOrDefault(iid) ?? [], qs);
            if (what == "links") return Page(Links.GetValueOrDefault(iid) ?? [], qs);
        }
        return (404, "{\"message\":\"404 Not Found\"}", none);
    }

    private (int, string?, Dictionary<string, string>) Page(JsonArray all, System.Collections.Specialized.NameValueCollection qs)
    {
        var page = int.TryParse(qs["page"], out var p) && p > 0 ? p : 1;
        var size = int.TryParse(qs["per_page"], out var pp) && pp > 0 ? (pp < 100 ? pp : PageSize) : PageSize;
        var slice = all.Skip((page - 1) * size).Take(size).Select(i => (JsonNode?)i!.DeepClone()).ToArray();
        var headers = new Dictionary<string, string> { ["X-Total"] = all.Count.ToString(), ["X-Page"] = page.ToString() };
        if (page * size < all.Count)
        {
            headers["X-Next-Page"] = (page + 1).ToString();
            // GitLab builds its Link header from its own configured external URL, which can differ from the address the importer uses.
            headers["Link"] = $"<https://internal.gitlab.invalid/api/v4/projects/{ProjectId}/x?page={page + 1}&per_page=100>; rel=\"next\", <https://internal.gitlab.invalid/x?page=1>; rel=\"first\"";
        }
        return (200, new JsonArray(slice).ToJsonString(), headers);
    }

    public void Dispose() { try { listener.Stop(); listener.Close(); } catch { } }
}
