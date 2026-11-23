using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ByteBench.Tests.Infrastructure;

public enum JiraFlavour { Cloud, Server }

/// <summary>
/// A fake Jira on a local port, with the response shapes of the public REST docs: Cloud (REST v3, Atlassian Document Format, the
/// enhanced <c>search/jql</c> with page tokens, <c>search/approximate-count</c>; the old v2/v3 <c>search</c> is gone) or Server/Data
/// Center (REST v2, wiki markup, <c>search</c> with startAt/total, sprints as the old greenhopper strings, Epic Link custom field).
/// Project PROJ has an epic with children, a sub-task, links of several types (one to another project), versions, components, a
/// sprints (agile API boards and sprints, plus the Sprint field in each flavour's shape), story points, attachments (served with
/// credentials, one redirecting to a second host, one too big, one lying about its size, a blocked type, a deleted one) and comments
/// that need paging. Never talks to a real Jira.
/// </summary>
public sealed class FakeJira : IDisposable
{
    public const string Key = "PROJ";
    public const string Email = "bob.builder@example.com";
    public const string ApiToken = "jira_api_token_0123456789abc";
    public const string Pat = "jira_pat_9876543210zyx";

    private readonly HttpListener listener = new();
    public string Url { get; }
    public JiraFlavour Flavour { get; }
    /// <summary>Everything the fake lives under, e.g. <c>/jira</c> for a Server with a context path.</summary>
    public string ContextPath { get; }
    /// <summary>"Basic" (Email + ApiToken) or "Bearer" (Pat); anything else gets 401.</summary>
    public string AuthMode { get; set; } = "Basic";
    /// <summary>Forbid every request with 403.</summary>
    public bool Forbidden { get; set; }
    /// <summary>The next N requests answer 429 with <c>Retry-After: 1</c>.</summary>
    public int RateLimitNext { get; set; }
    public int PageSize { get; set; } = 2;
    /// <summary>False: the agile API answers 404 (no Jira Software), so sprints must come from the issues' Sprint field.</summary>
    public bool AgileAvailable { get; set; } = true;
    /// <summary>Adds a second ACTIVE sprint ("Parallel sprint", id 4, ended in the past) to the board.</summary>
    public bool ExtraActiveSprint { get; set; }
    /// <summary>PROJ-5 sits in a sprint (id 9) the board doesn't list.</summary>
    public bool OrphanSprint { get; set; }
    /// <summary>When the active sprint ends (Jira timestamp).</summary>
    public string ActiveSprintEnd { get; set; } = "2024-04-28T00:00:00.000Z";
    /// <summary>Where redirected attachment downloads go (the second host); set by the test app.</summary>
    public string CdnUrl { get; set; } = "http://127.0.0.1:1";
    /// <summary>Attachment files by attachment id; served at <see cref="ContentUrl"/> after the Authorization check.</summary>
    public Dictionary<string, FakeFile> Files { get; } = new()
    {
        ["10100"] = new(FakeFiles.Png),
        ["10101"] = new(RedirectTo: "{cdn}/media/spec.pdf?token=signed123"),
        ["10102"] = new(FakeFiles.Filler(10240)),
        ["10103"] = new(FakeFiles.Filler(10240), Chunked: true),
        ["10104"] = new(FakeFiles.Filler(10)),
        ["10105"] = new(Status: 404),
    };
    public List<string> FileRequests { get; } = [];
    private static readonly (string Id, string Name, long Size)[] Attachments =
    [
        ("10100", "screenshot.png", 24), ("10101", "spec.pdf", 23), ("10102", "huge.bin", 10240), ("10103", "liar.bin", 12), ("10104", "run.exe", 10), ("10105", "gone.txt", 5),
    ];
    /// <summary>Where an attachment's file is downloaded: Cloud's content endpoint, Server's secure/attachment path.</summary>
    public string ContentUrl(string id, string name) => Cloud ? $"{Url}/rest/api/3/attachment/content/{id}" : $"{Url}/secure/attachment/{id}/{name}";
    public List<(string Method, string PathAndQuery, string? Auth, string? Body)> Requests { get; } = [];

    public FakeJira(JiraFlavour flavour, string contextPath = "")
    {
        (Flavour, ContextPath) = (flavour, contextPath);
        var port = FreePort();
        Url = $"http://127.0.0.1:{port}{contextPath}";
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        _ = Task.Run(Loop);
    }

    private bool Cloud => Flavour == JiraFlavour.Cloud;
    private string V => Cloud ? "3" : "2";

    public string ExpectedAuth => AuthMode == "Bearer" ? "Bearer " + Pat : "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(Email + ":" + ApiToken));
    public string[] Secrets => [ApiToken, Pat, Convert.ToBase64String(Encoding.UTF8.GetBytes(Email + ":" + ApiToken))];
    public int Hits(string contains) { lock (Requests) return Requests.Count(r => r.PathAndQuery.Contains(contains, StringComparison.Ordinal)); }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
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
            string? body = null;
            if (ctx.Request.HasEntityBody) body = new StreamReader(ctx.Request.InputStream, Encoding.UTF8).ReadToEnd();
            var auth = ctx.Request.Headers["Authorization"];
            var pathAndQuery = ctx.Request.Url!.PathAndQuery;
            lock (Requests) Requests.Add((ctx.Request.HttpMethod, pathAndQuery, auth, body));
            if (TryServeFile(ctx, auth, pathAndQuery)) return;
            var (status, json) = Route(ctx, auth, body);
            var bytes = Encoding.UTF8.GetBytes(json ?? "");
            ctx.Response.StatusCode = status;
            ctx.Response.ContentType = "application/json";
            ctx.Response.OutputStream.Write(bytes);
        }
        catch { /* the client went away */ }
        finally { try { ctx.Response.Close(); } catch { } }
    }

    private static readonly Regex FilePath = new(@"^(?:/rest/api/3/attachment/content/(?<id>\d+)|/secure/attachment/(?<id>\d+)/[^/]+)$", RegexOptions.Compiled);

    private bool TryServeFile(HttpListenerContext ctx, string? auth, string pathAndQuery)
    {
        var path = ctx.Request.Url!.AbsolutePath;
        if (ContextPath.Length > 0 && path.StartsWith(ContextPath + "/", StringComparison.Ordinal)) path = path[ContextPath.Length..];
        var m = FilePath.Match(path);
        if (!m.Success) return false;
        lock (FileRequests) FileRequests.Add(m.Groups["id"].Value);
        if (auth != ExpectedAuth) { ctx.Response.StatusCode = 401; return true; }
        if (!Files.TryGetValue(m.Groups["id"].Value, out var file)) { ctx.Response.StatusCode = 404; return true; }
        FakeFiles.Write(ctx.Response, file with { RedirectTo = file.RedirectTo?.Replace("{cdn}", CdnUrl) });
        return true;
    }

    private (int, string?) AgileRoute(string[] segs, System.Collections.Specialized.NameValueCollection q)
    {
        if (!AgileAvailable) return (404, "{\"message\":\"agile is not available\"}");
        // /rest/agile/1.0/board  and  /rest/agile/1.0/board/{id}/sprint
        switch (segs[3..])
        {
            case ["board"]:
                return (q["projectKeyOrId"] == Key
                    ? (200, new JsonObject { ["maxResults"] = 50, ["startAt"] = 0, ["isLast"] = true, ["values"] = new JsonArray(new JsonObject { ["id"] = 1, ["name"] = "PROJ board", ["type"] = "scrum" }) }.ToJsonString())
                    : (200, new JsonObject { ["isLast"] = true, ["values"] = new JsonArray() }.ToJsonString()));
            case ["board", "1", "sprint"]:
            {
                var all = SprintDefs().Where(d => !d.Orphan).ToList();
                var start = int.TryParse(q["startAt"], out var sa) ? sa : 0;
                var page = all.Skip(start).Take(2).ToList(); // two per page, to exercise paging
                var values = new JsonArray([.. page.Select(d => (JsonNode?)d.Cloud())]);
                return (200, new JsonObject { ["maxResults"] = 2, ["startAt"] = start, ["isLast"] = start + 2 >= all.Count, ["values"] = values }.ToJsonString());
            }
        }
        return (404, null);
    }

    private sealed record SprintDef(int Id, string Name, string State, string? Start, string? End, string? Complete, string? Goal, bool Orphan = false)
    {
        public JsonObject Cloud()
        {
            var o = new JsonObject { ["id"] = Id, ["self"] = "https://x/rest/agile/1.0/sprint/" + Id, ["state"] = State, ["name"] = Name, ["originBoardId"] = 1 };
            if (Start != null) o["startDate"] = Start;
            if (End != null) o["endDate"] = End;
            if (Complete != null) o["completeDate"] = Complete;
            if (Goal != null) o["goal"] = Goal;
            return o;
        }

        public string Server() =>
            $"com.atlassian.greenhopper.service.sprint.Sprint@{Id:x}[id={Id},rapidViewId=1,state={State.ToUpperInvariant()},name={Name},goal={Goal ?? "<null>"},startDate={Start ?? "<null>"},endDate={End ?? "<null>"},completeDate={Complete ?? "<null>"},sequence={Id}]";
    }

    private List<SprintDef> SprintDefs()
    {
        var list = new List<SprintDef>
        {
            new(1, "Sprint 1", "closed", "2024-04-01T00:00:00.000Z", "2024-04-14T00:00:00.000Z", "2024-04-14T10:00:00.000Z", "Ship the form"),
            new(2, "Sprint 2", "active", "2024-04-15T00:00:00.000Z", ActiveSprintEnd, null, "Card payments"),
            new(3, "Sprint 3", "future", null, null, null, null),
        };
        if (ExtraActiveSprint) list.Add(new(4, "Parallel sprint", "active", "2024-04-15T00:00:00.000Z", "2024-05-01T00:00:00.000Z", null, null));
        if (OrphanSprint) list.Add(new(9, "Ghost sprint", "active", "2024-04-20T00:00:00.000Z", "2024-05-02T00:00:00.000Z", null, null, Orphan: true));
        return list;
    }

    private (int, string?) Route(HttpListenerContext ctx, string? auth, string? body)
    {
        lock (this)
            if (RateLimitNext > 0)
            {
                RateLimitNext--;
                ctx.Response.Headers["Retry-After"] = "1";
                return (429, "{\"message\":\"rate limited\"}");
            }
        var path = ctx.Request.Url!.AbsolutePath;
        if (ContextPath.Length > 0)
        {
            if (!path.StartsWith(ContextPath + "/", StringComparison.Ordinal)) return (404, null);
            path = path[ContextPath.Length..];
        }
        if (Forbidden) return (403, "{\"errorMessages\":[\"no\"]}");
        if (auth != ExpectedAuth) return (401, "{\"errorMessages\":[\"You are not authenticated.\"]}");
        var q = ctx.Request.QueryString;
        var segs = path.Trim('/').Split('/');
        if (segs.Length >= 4 && segs[0] == "rest" && segs[1] == "agile" && segs[2] == "1.0") return AgileRoute(segs, q);
        if (path == "/rest/api/2/serverInfo")
            return (200, new JsonObject { ["baseUrl"] = Url, ["version"] = Cloud ? "1001.0.0-SNAPSHOT" : "9.12.4", ["deploymentType"] = Cloud ? "Cloud" : "Server", ["serverTitle"] = "Fake Jira" }.ToJsonString());
        if (segs.Length < 4 || segs[0] != "rest" || segs[1] != "api" || segs[2] != V) return (404, null); // Cloud: v3 only; Server: v2 only
        var rest = segs[3..];
        switch (rest)
        {
            case ["field"]: return (200, Fields().ToJsonString());
            case ["project", var key] when key == Key:
                return (200, new JsonObject { ["id"] = "10000", ["key"] = Key, ["name"] = "Payments Platform", ["description"] = "Everything about taking money." }.ToJsonString());
            case ["project", _]: return (404, "{\"errorMessages\":[\"No project could be found with key.\"]}");
            case ["project", Key, "statuses"]: return (200, Statuses().ToJsonString());
            case ["project", Key, "versions"]: return (200, Versions().ToJsonString());
            case ["search", "jql"] when Cloud: return (200, SearchCloud(q["jql"] ?? "", q["fields"], int.TryParse(q["maxResults"], out var mr) ? mr : 50, q["nextPageToken"]));
            case ["search", "approximate-count"] when Cloud && ctx.Request.HttpMethod == "POST":
                return (200, new JsonObject { ["count"] = Filter(JsonNode.Parse(body ?? "{}")?["jql"]?.GetValue<string>() ?? "").Count }.ToJsonString());
            case ["search"] when !Cloud:
                return (200, SearchServer(q["jql"] ?? "", q["fields"], int.TryParse(q["startAt"], out var sa) ? sa : 0, int.TryParse(q["maxResults"], out var m2) ? m2 : 50));
            case ["issue", var issueKey, "comment"]:
            {
                var comments = CommentsOf(issueKey);
                var start = int.TryParse(q["startAt"], out var s) ? s : 0;
                var max = int.TryParse(q["maxResults"], out var mm) ? mm : 50;
                return (200, new JsonObject { ["startAt"] = start, ["maxResults"] = max, ["total"] = comments.Count, ["comments"] = new JsonArray([.. comments.Skip(start).Take(max).Select(c => (JsonNode?)c)]) }.ToJsonString());
            }
        }
        return (404, null);
    }

    // ------------------------------------------------------------ project metadata

    private static JsonObject Cat(string key) => new() { ["key"] = key, ["name"] = key };

    private JsonArray Statuses() => new(
        new JsonObject
        {
            ["id"] = "10001", ["name"] = "Story",
            ["statuses"] = new JsonArray(
                new JsonObject { ["id"] = "1", ["name"] = "To Do", ["statusCategory"] = Cat("new") },
                new JsonObject { ["id"] = "2", ["name"] = "In Progress", ["statusCategory"] = Cat("indeterminate") },
                new JsonObject { ["id"] = "3", ["name"] = "In Review", ["statusCategory"] = Cat("indeterminate") },
                new JsonObject { ["id"] = "4", ["name"] = "Done", ["statusCategory"] = Cat("done") }),
        },
        new JsonObject
        {
            ["id"] = "10002", ["name"] = "Bug",
            ["statuses"] = new JsonArray(
                new JsonObject { ["id"] = "1", ["name"] = "To Do", ["statusCategory"] = Cat("new") },
                new JsonObject { ["id"] = "4", ["name"] = "Done", ["statusCategory"] = Cat("done") }),
        });

    private static JsonArray Versions() => new(
        new JsonObject { ["id"] = "10001", ["name"] = "Release 1.0", ["description"] = "First public release", ["archived"] = false, ["released"] = true, ["startDate"] = "2024-04-01", ["releaseDate"] = "2024-06-30", ["projectId"] = 10000 },
        new JsonObject { ["id"] = "10002", ["name"] = "Release 2.0", ["archived"] = false, ["released"] = false, ["releaseDate"] = "2024-12-01", ["projectId"] = 10000 });

    private JsonArray Fields() => new(
        new JsonObject { ["id"] = "summary", ["name"] = "Summary", ["custom"] = false },
        new JsonObject { ["id"] = Cloud ? "customfield_10016" : "customfield_10002", ["name"] = Cloud ? "Story point estimate" : "Story Points", ["custom"] = true, ["schema"] = new JsonObject { ["type"] = "number", ["custom"] = "com.atlassian.jira.plugin.system.customfieldtypes:float" } },
        new JsonObject { ["id"] = Cloud ? "customfield_10020" : "customfield_10004", ["name"] = "Sprint", ["custom"] = true, ["schema"] = new JsonObject { ["type"] = "array", ["custom"] = "com.pyxis.greenhopper.jira:gh-sprint" } },
        new JsonObject { ["id"] = "customfield_10014", ["name"] = "Epic Link", ["custom"] = true, ["schema"] = new JsonObject { ["type"] = "any", ["custom"] = "com.pyxis.greenhopper.jira:gh-epic-link" } },
        new JsonObject { ["id"] = "customfield_10099", ["name"] = "Team Mood", ["custom"] = true });

    // ------------------------------------------------------------ issues

    private record Row(string Key, string Id, string Type, bool SubTask, string Summary, string Status, string StatusCat, string? Priority, string Created, string? Resolved,
        string[] Labels, string[] Components, string? FixVersion, string? Parent, double? Points, string? DueDate, bool Sprint, int Comments, bool Attach, string? Reporter, string? Assignee);

    private static readonly Row[] Rows =
    [
        new("PROJ-1", "10101", "Epic", false, "Checkout revamp", "In Progress", "indeterminate", "High", "2024-05-01T12:00:00.000+0200", null, ["frontend"], ["Backend"], "10001", null, null, "2024-09-30", false, 0, false, "bob", "bob"),
        new("PROJ-2", "10102", "Story", false, "Payment form", "In Review", "indeterminate", "Medium", "2024-05-02T09:30:00.000+0000", null, ["payments"], ["Frontend", "Backend"], "10001", "PROJ-1", 4.6, null, true, 3, true, "bob", null),
        new("PROJ-3", "10103", "Bug", false, "Card declined crash", "Done", "done", "Critical", "2024-05-03T08:00:00.000+0000", "2024-05-10T16:45:00.000-0500", ["crash"], [], "10002", null, 3, null, false, 1, false, null, "bob"),
        new("PROJ-4", "10104", "Sub-task", true, "Validate card number", "To Do", "new", "Minor", "2024-05-04T08:00:00.000+0000", null, [], [], null, "PROJ-2", 2, null, false, 0, false, "bob", null),
        new("PROJ-5", "10105", "Task", false, "Write the docs", "To Do", "new", "Lowest", "2024-05-05T08:00:00.000+0000", null, [], [], null, null, 0, "2024-07-01", false, 0, false, null, null),
    ];

    private static List<Row> Filter(string jql) =>
        jql.Contains($"project = \"{Key}\"", StringComparison.Ordinal)
            ? [.. Rows.Where(r => !jql.Contains("statusCategory != Done", StringComparison.Ordinal) || r.StatusCat != "done")]
            : [];

    private string SearchCloud(string jql, string? fields, int max, string? token)
    {
        var rows = Filter(jql);
        var start = token != null && token.StartsWith('t') && int.TryParse(token[1..], out var t) ? t : 0;
        var take = max <= 3 ? max : Math.Min(max, PageSize);
        var page = rows.Skip(start).Take(take).ToList();
        var result = new JsonObject { ["issues"] = Issues(page, fields) };
        if (start + take < rows.Count) result["nextPageToken"] = "t" + (start + take); else result["isLast"] = true;
        return result.ToJsonString();
    }

    private string SearchServer(string jql, string? fields, int startAt, int max)
    {
        var rows = Filter(jql);
        var take = max <= 3 ? max : Math.Min(max, PageSize);
        var page = rows.Skip(startAt).Take(take).ToList();
        return new JsonObject { ["expand"] = "schema,names", ["startAt"] = startAt, ["maxResults"] = take, ["total"] = rows.Count, ["issues"] = Issues(page, fields) }.ToJsonString();
    }

    private JsonArray Issues(List<Row> rows, string? fields)
    {
        var wanted = fields?.Split(',').ToHashSet();
        var array = new JsonArray();
        foreach (var r in rows)
        {
            var f = Fields(r);
            if (wanted != null && !wanted.Contains("*all"))
                foreach (var k in f.Select(p => p.Key).ToList()) if (!wanted.Contains(k)) f.Remove(k);
            array.Add(new JsonObject { ["id"] = r.Id, ["key"] = r.Key, ["self"] = $"{Url}/rest/api/{V}/issue/{r.Id}", ["fields"] = f });
        }
        return array;
    }

    private JsonObject User(string? login) => login == null ? null! : Cloud
        ? new JsonObject { ["accountId"] = "5b10a2844c20165700ede21g", ["displayName"] = "Bob Builder", ["active"] = true }
        : new JsonObject { ["name"] = login, ["key"] = "JIRAUSER10000", ["displayName"] = "Bob Builder", ["emailAddress"] = Email, ["active"] = true };

    private JsonObject Fields(Row r)
    {
        var f = new JsonObject
        {
            ["summary"] = r.Summary,
            ["issuetype"] = new JsonObject { ["id"] = "1", ["name"] = r.Type, ["subtask"] = r.SubTask },
            ["status"] = new JsonObject { ["name"] = r.Status, ["statusCategory"] = Cat(r.StatusCat) },
            ["priority"] = r.Priority == null ? null : new JsonObject { ["name"] = r.Priority },
            ["labels"] = new JsonArray([.. r.Labels.Select(l => (JsonNode?)JsonValue.Create(l))]),
            ["components"] = new JsonArray([.. r.Components.Select(c => (JsonNode?)new JsonObject { ["id"] = "1", ["name"] = c })]),
            ["fixVersions"] = new JsonArray([.. (r.FixVersion == null ? [] : new[] { r.FixVersion }).Select(v => (JsonNode?)new JsonObject { ["id"] = v, ["name"] = "v" + v })]),
            ["duedate"] = r.DueDate,
            ["created"] = r.Created,
            ["updated"] = r.Created,
            ["resolutiondate"] = r.Resolved,
            ["reporter"] = r.Reporter == null ? null : User(r.Reporter),
            ["assignee"] = r.Assignee == null ? null : User(r.Assignee),
            ["description"] = Description(r),
            ["issuelinks"] = Links(r),
            ["subtasks"] = new JsonArray(),
            ["attachment"] = r.Attach
                ? new JsonArray([.. Attachments.Select(a => (JsonNode?)new JsonObject { ["id"] = a.Id, ["filename"] = a.Name, ["size"] = a.Size, ["content"] = ContentUrl(a.Id, a.Name) })])
                : new JsonArray(),
        };
        if (r.Parent != null)
        {
            if (Cloud || r.SubTask) f["parent"] = new JsonObject { ["id"] = "1", ["key"] = r.Parent, ["fields"] = new JsonObject { ["summary"] = "parent" } };
            else f["customfield_10014"] = r.Parent; // Server: stories point at their epic through Epic Link
        }
        else if (!Cloud) f["customfield_10014"] = null;
        f[Cloud ? "customfield_10016" : "customfield_10002"] = r.Points == null ? null : JsonValue.Create(r.Points.Value);
        // PROJ-2 was in sprints 1 and 2 (latest: 2), PROJ-3 in 1, PROJ-4 in 3 (not started); PROJ-5 in the unlisted sprint when asked.
        int[] inSprints = r.Key switch { "PROJ-2" => [1, 2], "PROJ-3" => [1], "PROJ-4" => [3], "PROJ-5" when OrphanSprint => [9], _ => [] };
        var defs = SprintDefs();
        f[Cloud ? "customfield_10020" : "customfield_10004"] = inSprints.Length == 0 ? null
            : Cloud ? new JsonArray([.. inSprints.Select(id => (JsonNode?)defs.First(d => d.Id == id).Cloud())])
            : new JsonArray([.. inSprints.Select(id => (JsonNode?)JsonValue.Create(defs.First(d => d.Id == id).Server()))]);
        var comments = CommentsOf(r.Key);
        f["comment"] = new JsonObject
        {
            ["startAt"] = 0, ["maxResults"] = 2, ["total"] = comments.Count,
            ["comments"] = new JsonArray([.. comments.Take(2).Select(c => (JsonNode?)JsonNode.Parse(c.ToJsonString()))]),
        };
        f["customfield_10099"] = "ignored";
        return f;
    }

    private JsonArray Links(Row r)
    {
        JsonObject Issue(string key) => new() { ["id"] = "9", ["key"] = key, ["fields"] = new JsonObject { ["summary"] = "x" } };
        JsonObject Type(string name, string inward, string outward) => new() { ["name"] = name, ["inward"] = inward, ["outward"] = outward };
        var a = new JsonArray();
        switch (r.Key)
        {
            case "PROJ-2":
                a.Add(new JsonObject { ["id"] = "1", ["type"] = Type("Blocks", "is blocked by", "blocks"), ["outwardIssue"] = Issue("PROJ-3") });
                a.Add(new JsonObject { ["id"] = "2", ["type"] = Type("Relates", "relates to", "relates to"), ["outwardIssue"] = Issue("OTHER-9") });
                break;
            case "PROJ-3":
                a.Add(new JsonObject { ["id"] = "1", ["type"] = Type("Blocks", "is blocked by", "blocks"), ["inwardIssue"] = Issue("PROJ-2") });
                a.Add(new JsonObject { ["id"] = "3", ["type"] = Type("Cloners", "is cloned by", "clones"), ["outwardIssue"] = Issue("PROJ-5") });
                break;
            case "PROJ-4":
                a.Add(new JsonObject { ["id"] = "4", ["type"] = Type("Duplicate", "is duplicated by", "duplicates"), ["outwardIssue"] = Issue("PROJ-3") });
                a.Add(new JsonObject { ["id"] = "5", ["type"] = Type("Relates", "relates to", "relates to"), ["outwardIssue"] = Issue("PROJ-1") });
                break;
        }
        return a;
    }

    // ------------------------------------------------------------ text, in the format of the flavour

    private static JsonObject Doc(params JsonNode[] content) => new() { ["version"] = 1, ["type"] = "doc", ["content"] = new JsonArray(content) };
    private static JsonObject P(params JsonNode[] content) => new() { ["type"] = "paragraph", ["content"] = new JsonArray(content) };
    private static JsonObject T(string text, params string[] marks) => new()
    {
        ["type"] = "text", ["text"] = text,
        ["marks"] = marks.Length == 0 ? null : new JsonArray([.. marks.Select(m => (JsonNode?)new JsonObject { ["type"] = m })]),
    };

    private JsonNode? Description(Row r) => r.Key switch
    {
        "PROJ-1" => Cloud
            ? Doc(P(T("Rebuild the checkout "), T("flow", "strong"), T(".")))
            : JsonValue.Create("Rebuild the checkout *flow*."),
        "PROJ-2" => Cloud
            ? Doc(
                new JsonObject { ["type"] = "heading", ["attrs"] = new JsonObject { ["level"] = 2 }, ["content"] = new JsonArray(T("Goal")) },
                P(T("Pay with a "), T("card", "em"), T(", ask "), new JsonObject { ["type"] = "mention", ["attrs"] = new JsonObject { ["id"] = "5b10", ["text"] = "@Bob Builder" } }),
                new JsonObject { ["type"] = "bulletList", ["content"] = new JsonArray(
                    new JsonObject { ["type"] = "listItem", ["content"] = new JsonArray(P(T("one"))) },
                    new JsonObject { ["type"] = "listItem", ["content"] = new JsonArray(P(T("two"))) }) },
                new JsonObject { ["type"] = "codeBlock", ["attrs"] = new JsonObject { ["language"] = "js" }, ["content"] = new JsonArray(T("pay(card);")) })
            : JsonValue.Create("h2. Goal\nPay with a _card_, ask [~bob]\n* one\n* two\n{code:js}\npay(card);\n{code}"),
        "PROJ-3" => Cloud ? Doc(P(T("Crashes when the card is declined."))) : JsonValue.Create("Crashes when the card is declined."),
        "PROJ-4" => Cloud ? Doc(P(T("Luhn check."))) : JsonValue.Create("Luhn check."),
        _ => null,
    };

    private List<JsonObject> CommentsOf(string key)
    {
        JsonNode Body(string text) => Cloud ? Doc(P(T(text))) : JsonValue.Create(text)!;
        var list = new List<JsonObject>();
        void Add(string id, string text, string created) => list.Add(new JsonObject
        {
            ["id"] = id, ["author"] = User("bob"), ["body"] = Body(text), ["created"] = created, ["updated"] = created,
        });
        if (key == "PROJ-2")
        {
            Add("20001", "First comment", "2024-05-06T10:00:00.000+0000");
            Add("20002", "Second comment", "2024-05-06T11:00:00.000+0000");
            Add("20003", "Third comment, from the second page", "2024-05-06T12:00:00.000+0000");
        }
        if (key == "PROJ-3") Add("20004", "Reproduced on staging", "2024-05-04T10:00:00.000+0000");
        return list;
    }

    public void Dispose() { try { listener.Stop(); listener.Close(); } catch { } }
}
