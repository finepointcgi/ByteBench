using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ByteBench.Tests.Infrastructure;

/// <summary>
/// A fake Workbench REST API on a local port (shapes copied from the Workbench source: projects, WBS elements, plan sheet items,
/// tasks, dependencies, financials, and the task status write). Keys map to scopes like Workbench's API keys: a request outside the
/// key's scopes gets 403 before anything else. Never talks to a real Workbench.
/// </summary>
public sealed class FakeWorkbench : IDisposable
{
    public const string Key = "wbk_fakekey0123456789";
    public static readonly Guid ProjectId = Guid.Parse("00000000-0000-0000-0000-0000000000a1");
    public static readonly Guid SheetId = Guid.Parse("00000000-0000-0000-0000-0000000000b1");
    public static readonly Guid StatusCol = Guid.Parse("00000000-0000-0000-0000-0000000000c1");
    public static readonly Guid PercentCol = Guid.Parse("00000000-0000-0000-0000-0000000000c2");
    public static readonly Guid NotesCol = Guid.Parse("00000000-0000-0000-0000-0000000000c3");
    public static Guid Item(int n) => Guid.Parse($"00000000-0000-0000-0000-{n:D12}");

    public record PlanTask(int N, int? Parent, string Title, string? Wbs, string? Start, string? Finish, decimal? Hours = null, string[]? Emails = null, string? Notes = null, bool Summary = false);

    private readonly HttpListener listener = new();
    public string Url { get; }
    public HashSet<string> Scopes { get; set; } = ["projects:read", "sheets:read", "sheets:write"];
    public bool FinancialsAllowed { get; set; } = true;
    public int Delay { get; set; }
    public int FailStatusWrites { get; set; }
    public string[] StatusOptions { get; set; } = ["Not Started", "In Progress", "Done"];
    public List<PlanTask> Tasks { get; set; } = DefaultPlan();
    public List<(int From, int To)> Dependencies { get; set; } = [(5, 6)];
    public Dictionary<int, (string? Status, decimal? Percent)> State { get; } = [];
    public List<(string Method, string Path, string? Auth, string? Body)> Requests { get; } = [];
    public int Hits(string pathPrefix) { lock (Requests) return Requests.Count(r => r.Path.StartsWith(pathPrefix, StringComparison.Ordinal)); }

    /// <summary>Phase 1 (summary 1) { Sub phase (2) { Draft spec (4) }, Review (3) }, Phase 2 (summary 7) { Implement (6) }; item 5 is a leaf under Phase 1.</summary>
    public static List<PlanTask> DefaultPlan() =>
    [
         new(1, null, "Phase 1 Design", "1.1", "2026-01-05", "2026-02-27", Summary: true),
         new(2, 1, "Sub phase", "1.1.1", "2026-01-05", "2026-01-30", Summary: true),
         new(4, 2, "Draft spec", "1.1.1.1", "2026-01-05", "2026-01-16", 16, ["alice@example.com", "ghost@nowhere.test"], "Write the first draft."),
         new(3, 1, "Review", "1.1.2", "2026-02-02", "2026-02-13", 8),
         new(5, 1, "Prepare kickoff", "1.1.3", "2026-02-16", "2026-02-27", 4),
         new(7, null, "Phase 2 Build", "1.2", "2026-03-02", "2026-04-30", Summary: true),
         new(6, 7, "Implement", "1.2.1", "2026-03-02", "2026-04-30", 120),
    ];

    public FakeWorkbench()
    {
        var port = FreePort();
        Url = $"http://127.0.0.1:{port}";
        listener.Prefixes.Add(Url + "/");
        listener.Start();
        _ = System.Threading.Tasks.Task.Run(Loop);
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private async System.Threading.Tasks.Task Loop()
    {
        while (listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await listener.GetContextAsync(); } catch { return; }
            _ = System.Threading.Tasks.Task.Run(() => Handle(ctx));
        }
    }

    private void Handle(HttpListenerContext ctx)
    {
        try
        {
            var path = ctx.Request.Url!.AbsolutePath;
            string? body = null;
            if (ctx.Request.HasEntityBody) body = new StreamReader(ctx.Request.InputStream, Encoding.UTF8).ReadToEnd();
            var auth = ctx.Request.Headers["Authorization"];
            lock (Requests) Requests.Add((ctx.Request.HttpMethod, path, auth, body));
            if (Delay > 0) Thread.Sleep(Delay);
            var (status, json) = Route(ctx.Request.HttpMethod, path, auth, body);
            var bytes = Encoding.UTF8.GetBytes(json ?? "");
            ctx.Response.StatusCode = status;
            ctx.Response.ContentType = "application/json";
            ctx.Response.OutputStream.Write(bytes);
        }
        catch { /* the client gave up (timeout test) */ }
        finally { try { ctx.Response.Close(); } catch { } }
    }

    private (int, string?) Route(string method, string path, string? auth, string? body)
    {
        if (auth != "Bearer " + Key) return (401, null);
        var segs = path.Trim('/').Split('/');
        if (segs.Length < 3 || segs[0] != "api" || segs[1] != "v1") return (404, null);
        var area = segs[2] switch { "projects" => "projects", "sheets" => "sheets", "time" => "time", _ => segs[2] };
        var read = method is "GET" or "HEAD";
        if (!(Scopes.Contains(area + ":write") || (read && Scopes.Contains(area + ":read")))) return (403, "{\"error\":\"scope\"}");
        var rest = segs[3..];
        string[] r = rest;
        if (r.Length == 0 && segs[2] == "projects") return (200, Projects());
        if (segs[2] == "projects" && r.Length == 2 && r[1] == "wbs") return (200, Wbs());
        if (segs[2] == "projects" && r.Length == 3 && r[1] == "financials" && r[2] == "access") return (200, FinancialsAllowed ? "true" : "false");
        if (segs[2] == "projects" && r.Length == 3 && r[1] == "financials" && r[2] == "wbs") return FinancialsAllowed ? (200, Hours()) : (403, null);
        if (segs[2] == "sheets" && r.Length == 0) return (200, "[]");
        if (segs[2] == "sheets" && r.Length >= 1 && r[0] != SheetId.ToString()) return (404, null);
        if (segs[2] == "sheets" && r.Length == 1) return (200, SheetJson());
        if (segs[2] == "sheets" && r.Length == 2 && r[1] == "items") return (200, Items());
        if (segs[2] == "sheets" && r.Length == 2 && r[1] == "plan") return (200, PlanJson());
        if (segs[2] == "sheets" && r.Length == 2 && r[1] == "tasks") return (200, TasksJson());
        if (segs[2] == "sheets" && r.Length == 2 && r[1] == "dependencies") return (200, Deps());
        if (segs[2] == "sheets" && r.Length == 4 && r[1] == "tasks" && r[3] == "status" && method == "POST")
        {
            if (FailStatusWrites > 0) { FailStatusWrites--; return (503, null); }
            var n = Tasks.FirstOrDefault(t => Item(t.N).ToString() == r[2])?.N;
            if (n == null) return (404, null);
            var j = JsonNode.Parse(body ?? "{}")!;
            var cur = State.GetValueOrDefault(n.Value);
            var st = j["status"]?.GetValue<string>() ?? cur.Status;
            if (j["status"] != null && !StatusOptions.Contains(st!)) return (400, "{\"error\":\"option\"}");
            State[n.Value] = (st, j["percentComplete"]?.GetValue<decimal>() ?? cur.Percent);
            return (200, "{}");
        }
        return (404, null);
    }

    private static string J(object o) => JsonSerializer.Serialize(o, new JsonSerializerOptions(JsonSerializerDefaults.Web));

    private string Projects() => J(new[] { new { id = ProjectId, code = "WB-1", name = "Bench Project", status = 1, start = "2026-01-05", finish = "2026-04-30", sheetId = SheetId } });

    private string Wbs() => J(Tasks.Where(t => t.Wbs != null).Select(t => new { id = Guid.Parse($"00000000-0000-0000-1111-{t.N:D12}"), projectId = ProjectId, parentId = (Guid?)null, code = t.Wbs, name = t.Title, isOpen = true, isChargeable = !t.Summary, itemId = Item(t.N) }));

    private string Hours() => J(Tasks.Where(t => t.Wbs != null && t.Hours != null).Select(t => new { wbsElementId = Guid.Parse($"00000000-0000-0000-1111-{t.N:D12}"), code = t.Wbs, figures = new { budgetHours = t.Hours, actualHours = t.Hours / 4, eacHours = t.Hours } }));

    private string SheetJson() => J(new
    {
        summary = new { id = SheetId },
        columns = new object[]
        {
            new { id = StatusCol, name = "Status", type = "Dropdown", options = StatusOptions },
            new { id = PercentCol, name = "% Complete", type = "Number", options = Array.Empty<string>() },
            new { id = NotesCol, name = "Notes", type = "Text", options = Array.Empty<string>() },
        },
    });

    private string Items() => J(Tasks.Select((t, i) =>
    {
        var values = new Dictionary<string, string?>();
        if (State.TryGetValue(t.N, out var s)) { if (s.Status != null) values[StatusCol.ToString()] = s.Status; if (s.Percent != null) values[PercentCol.ToString()] = s.Percent.Value.ToString("0.##"); }
        if (t.Notes != null) values[NotesCol.ToString()] = t.Notes;
        return new { id = Item(t.N), parentId = t.Parent is { } p ? Item(p) : (Guid?)null, order = i, title = t.Title, start = t.Start, finish = t.Finish, values };
    }));

    private string PlanJson() => J(new { sheetId = SheetId, tasks = Tasks.Select(t => new { itemId = Item(t.N), taskNumber = t.N, outlineNumber = t.Wbs, isSummary = t.Summary }) });

    private string TasksJson() => J(new
    {
        sheetId = SheetId,
        statusColumn = new { id = StatusCol, name = "Status", options = StatusOptions },
        percentColumn = new { id = PercentCol, name = "% Complete", options = Array.Empty<string>() },
        assignees = Tasks.Where(t => t.Emails != null).ToDictionary(t => Item(t.N).ToString(), t => t.Emails!.Select(e => new { email = e, displayName = e })),
    });

    private string Deps() => J(Dependencies.Select(d => new { id = Guid.NewGuid(), predecessorId = Item(d.From), successorId = Item(d.To), type = 0, lagDays = 0 }));

    public void Dispose() { try { listener.Stop(); listener.Close(); } catch { } }
}
