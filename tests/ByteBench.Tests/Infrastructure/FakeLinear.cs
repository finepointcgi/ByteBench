using System.Text.Json.Nodes;

namespace ByteBench.Tests.Infrastructure;

/// <summary>
/// A fake Linear GraphQL API (shapes from developer.linear.app/docs/graphql): <c>POST /graphql</c>, the <c>Authorization</c> header
/// carries the personal key as is, answers are <c>{data}</c> or <c>{errors:[{message, extensions:{code}}]}</c>. Issues are listed newest
/// first and paged backwards (<c>last</c>/<c>before</c>); the fake caps a page at <see cref="PageSize"/>. It recognises the reader's
/// queries by their text; it does not execute GraphQL.
/// </summary>
public sealed class FakeLinear : FakeSaasServer
{
    public const string ApiKey = "lin_api_FAKEKEY0123456789abcdefghijklmnopqrstu";
    public const string TeamId = "11111111-2222-3333-4444-555555555555";
    public const string TeamKey = "ENG";

    public string ExpectedAuthorization { get; set; } = ApiKey;
    public int PageSize { get; set; } = 2;
    public JsonArray Issues { get; set; }
    public Dictionary<string, JsonArray> Comments { get; } = [];
    /// <summary>"errors" in a 200 answer to the next query: a RATELIMITED error, or a plain GraphQL error.</summary>
    public string? FailNextWith { get; set; }
    /// <summary>The next issues query with a page size above 5 is refused with HTTP 400 (as Linear does for a query that is too complex).</summary>
    public bool ComplexityOnce { get; set; }
    public List<int> IssuePageSizes { get; } = [];
    /// <summary>The cycles query answers a GraphQL error (everything else works).</summary>
    public bool FailCycles { get; set; }
    public int CycleQueries { get; private set; }
    /// <summary>The team's cycles, served two per page: two completed, one running now, one planned.</summary>
    public JsonNode[] Cycles { get; set; } =
    [
        new JsonObject { ["id"] = "cy-1", ["number"] = 1, ["name"] = null, ["description"] = null, ["startsAt"] = "2024-04-01T00:00:00.000Z", ["endsAt"] = "2024-04-14T00:00:00.000Z", ["completedAt"] = "2024-04-14T10:00:00.000Z" },
        new JsonObject { ["id"] = "cy-2", ["number"] = 2, ["name"] = "Polish", ["description"] = "Make it shine", ["startsAt"] = "2024-04-15T00:00:00.000Z", ["endsAt"] = "2024-04-28T00:00:00.000Z", ["completedAt"] = "2024-04-28T09:00:00.000Z" },
        new JsonObject { ["id"] = "cy-3", ["number"] = 3, ["name"] = null, ["description"] = "Ship it", ["startsAt"] = DateTime.UtcNow.AddDays(-3).ToString("o"), ["endsAt"] = DateTime.UtcNow.AddDays(11).ToString("o"), ["completedAt"] = null },
        new JsonObject { ["id"] = "cy-4", ["number"] = 4, ["name"] = "Next up", ["description"] = null, ["startsAt"] = DateTime.UtcNow.AddDays(11).ToString("o"), ["endsAt"] = DateTime.UtcNow.AddDays(25).ToString("o"), ["completedAt"] = null },
    ];

    private static JsonObject User(string name, string display, string email) => new() { ["name"] = name, ["displayName"] = display, ["email"] = email };
    private static string Iid(int n) => $"a0000000-0000-0000-0000-{n:D12}";

    private static JsonObject Issue(int n, string title, Action<JsonObject>? tweak = null)
    {
        var o = new JsonObject
        {
            ["id"] = Iid(n), ["identifier"] = $"{TeamKey}-{n}", ["url"] = $"https://linear.app/acme/issue/{TeamKey}-{n}", ["title"] = title, ["description"] = "Body of " + title,
            ["priority"] = 0, ["estimate"] = null, ["dueDate"] = null, ["createdAt"] = $"2024-05-0{n}T12:00:00.000Z", ["updatedAt"] = $"2024-05-1{n}T12:00:00.000Z",
            ["completedAt"] = null, ["canceledAt"] = null, ["archivedAt"] = null,
            ["creator"] = User("Bob Builder", "bob", "bob@example.com"), ["assignee"] = null,
            ["state"] = new JsonObject { ["name"] = "Todo", ["type"] = "unstarted" },
            ["labels"] = new JsonObject { ["nodes"] = new JsonArray() }, ["parent"] = null, ["project"] = null, ["cycle"] = null,
            ["attachments"] = new JsonObject { ["nodes"] = new JsonArray() }, ["relations"] = new JsonObject { ["nodes"] = new JsonArray() },
        };
        tweak?.Invoke(o);
        return o;
    }

    private static JsonObject Lbl(string name, string? group = null) => new() { ["name"] = name, ["parent"] = group == null ? null : new JsonObject { ["name"] = group } };
    private static JsonObject Nodes(params JsonNode[] n) => new() { ["nodes"] = new JsonArray(n) };

    public FakeLinear()
    {
        Issues =
        [
            Issue(6, "Archived thing", o => { o["cycle"] = new JsonObject { ["id"] = "cy-2", ["number"] = 2, ["name"] = "Polish" }; o["archivedAt"] = "2024-06-01T00:00:00.000Z"; o["state"] = State("Done", "completed"); o["completedAt"] = "2024-05-30T00:00:00.000Z"; }),
            Issue(5, "Child of epic", o =>
            {
                o["parent"] = new JsonObject { ["id"] = Iid(4), ["parent"] = null, ["labels"] = Nodes(Lbl("epic")) };
                o["labels"] = Nodes(Lbl("feature"));
                o["cycle"] = new JsonObject { ["id"] = "cy-4", ["number"] = 4, ["name"] = "Next up" };
            }),
            Issue(4, "Big epic", o =>
            {
                o["labels"] = Nodes(Lbl("epic"));
                o["cycle"] = new JsonObject { ["id"] = "cy-gone", ["number"] = 9, ["name"] = "Ghost" }; // a cycle that isn't in the list
            }),
            Issue(3, "Dropped idea", o =>
            {
                o["state"] = State("Canceled", "canceled"); o["canceledAt"] = "2024-05-20T09:00:00.000Z";
                o["cycle"] = new JsonObject { ["id"] = "cy-1", ["number"] = 1, ["name"] = null };
                o["relations"] = Nodes(new JsonObject { ["type"] = "duplicate", ["relatedIssue"] = new JsonObject { ["id"] = Iid(1) } });
            }),
            Issue(2, "Crash on save", o =>
            {
                o["state"] = State("Done", "completed"); o["completedAt"] = "2024-06-01T08:30:00.000Z"; o["dueDate"] = "2024-05-30"; o["priority"] = 1;
                o["cycle"] = new JsonObject { ["id"] = "cy-1", ["number"] = 1, ["name"] = null };
                o["parent"] = new JsonObject { ["id"] = Iid(1), ["parent"] = null, ["labels"] = Nodes() };
                o["project"] = new JsonObject { ["id"] = "p-other", ["name"] = "Other team's project" };
            }),
            Issue(1, "Login fails", o =>
            {
                o["priority"] = 2; o["estimate"] = 3;
                o["state"] = State("In Progress", "started");
                o["assignee"] = User("Bob Builder", "bob", "bob@example.com");
                o["creator"] = User("Some Stranger", "stranger", "stranger@elsewhere.example");
                o["labels"] = Nodes(Lbl("bug"), Lbl("UI", "Area"));
                o["project"] = new JsonObject { ["id"] = "p1", ["name"] = "Release 1" };
                o["cycle"] = new JsonObject { ["id"] = "cy-3", ["number"] = 3, ["name"] = null };
                o["attachments"] = Nodes(new JsonObject { ["title"] = "Spec", ["url"] = "https://example.com/spec.pdf" });
                o["relations"] = Nodes(new JsonObject { ["type"] = "blocks", ["relatedIssue"] = new JsonObject { ["id"] = Iid(2) } },
                    new JsonObject { ["type"] = "related", ["relatedIssue"] = new JsonObject { ["id"] = Iid(3) } });
            }),
        ];
        Comments[Iid(1)] = new JsonArray(Enumerable.Range(1, 12).Select(n => (JsonNode?)new JsonObject
        {
            ["id"] = "c" + n.ToString("D2"), ["body"] = "Comment " + n, ["createdAt"] = $"2024-05-{n + 10:D2}T10:00:00.000Z",
            ["user"] = n == 2 ? User("Some Stranger", "stranger", "stranger@elsewhere.example") : User("Bob Builder", "bob", "bob@example.com"),
        }).ToArray());
        Start();
    }

    private static JsonObject State(string name, string type) => new() { ["name"] = name, ["type"] = type };

    private static Reply Data(JsonNode data) => Json(new JsonObject { ["data"] = data });

    private static Reply Error(string message, string code, int status = 200) =>
        Json(new JsonObject { ["errors"] = new JsonArray(new JsonObject { ["message"] = message, ["extensions"] = new JsonObject { ["code"] = code, ["type"] = code } }) }, status);

    private JsonObject TeamNode() => new() { ["id"] = TeamId, ["key"] = TeamKey, ["name"] = "Engineering", ["description"] = "The engineers" };

    protected override Reply Route(Request r)
    {
        if (r.Method != "POST" || r.Path != "/graphql") return new Reply(404, "{}");
        if (r.Authorization != ExpectedAuthorization) return Error("Authentication required, not authenticated", "AUTHENTICATION_ERROR", 401);
        if (RateLimitOnce) { RateLimitOnce = false; return new Reply(429, "{}", new() { ["Retry-After"] = "1" }); }
        if (FailNextWith is { } fail)
        {
            FailNextWith = null;
            return fail == "RATELIMITED" ? Error("Rate limit exceeded", "RATELIMITED") : Error("Something is off with the query", "INVALID");
        }
        var body = JsonNode.Parse(r.Body)!;
        var q = (string)body["query"]!;
        var v = body["variables"] ?? new JsonObject();

        if (q.Contains("teams(first:1"))
        {
            JsonNode[] found = (string?)v["key"] == TeamKey ? [TeamNode()] : [];
            return Data(new JsonObject { ["teams"] = Nodes(found) });
        }
        if (q.Contains("team(id:$id){ id key name description }"))
            return (string?)v["id"] == TeamId ? Data(new JsonObject { ["team"] = TeamNode() }) : Error("Entity not found: Team", "INVALID_INPUT");
        if (q.Contains("issueCount")) return Data(new JsonObject { ["team"] = new JsonObject { ["issueCount"] = Issues.Count } });
        if (q.Contains("issues(last:3")) return Data(new JsonObject { ["team"] = new JsonObject { ["issues"] = Nodes(Issues.Skip(Issues.Count - 3).Select(i => i!.DeepClone()).ToArray()) } });
        if (q.Contains("states(first:100"))
            return Data(new JsonObject { ["team"] = new JsonObject { ["states"] = Nodes(
                new JsonObject { ["name"] = "Done", ["type"] = "completed", ["position"] = 4 }, new JsonObject { ["name"] = "Backlog", ["type"] = "backlog", ["position"] = 0 },
                new JsonObject { ["name"] = "Todo", ["type"] = "unstarted", ["position"] = 1 }, new JsonObject { ["name"] = "In Progress", ["type"] = "started", ["position"] = 2 },
                new JsonObject { ["name"] = "Canceled", ["type"] = "canceled", ["position"] = 5 }) } });
        if (q.Contains("labels(first:100"))
            return Forward("labels", [Lbl("bug"), Lbl("UI", "Area"), Lbl("epic")], (string?)v["after"]);
        if (q.Contains("cycles(first:50"))
        {
            CycleQueries++;
            return FailCycles ? Error("Cycles are not available for this team", "INVALID") : Forward("cycles", Cycles, (string?)v["after"]);
        }
        if (q.Contains("projects(first:50"))
            return Forward("projects",
            [
                new JsonObject { ["id"] = "p1", ["name"] = "Release 1", ["description"] = "First", ["startDate"] = "2024-05-01", ["targetDate"] = "2024-09-01", ["state"] = "started" },
                new JsonObject { ["id"] = "p0", ["name"] = "Release 0", ["description"] = null, ["startDate"] = null, ["targetDate"] = null, ["state"] = "completed" },
            ], (string?)v["after"]);
        if (q.Contains("issues(last:$last"))
        {
            var last = (int)v["last"]!;
            lock (IssuePageSizes) IssuePageSizes.Add(last);
            if (ComplexityOnce && last > 5) { ComplexityOnce = false; return new Reply(400, "{\"errors\":[{\"message\":\"query too complex\"}]}"); }
            var hideClosed = v["filter"] is JsonObject;
            var withComments = q.Contains("comments(first:10)");
            var all = Issues.Where(i => (bool?)(v["archived"]?.GetValue<bool>()) == true || i!["archivedAt"] is null)
                .Where(i => !hideClosed || (string?)i!["state"]!["type"] is not ("completed" or "canceled")).ToList();
            var end = int.TryParse((string?)v["before"], out var e) ? e : all.Count;
            var take = Math.Min(last, PageSize);
            var start = Math.Max(0, end - take);
            var nodes = all.Skip(start).Take(end - start).Select(i =>
            {
                var c = (JsonObject)i!.DeepClone();
                if (withComments)
                {
                    var list = Comments.GetValueOrDefault((string)c["id"]!) ?? [];
                    c["comments"] = new JsonObject
                    {
                        ["nodes"] = new JsonArray(list.Take(10).Select(x => x!.DeepClone()).ToArray()),
                        ["pageInfo"] = new JsonObject { ["hasNextPage"] = list.Count > 10, ["endCursor"] = "10" },
                    };
                }
                return (JsonNode)c;
            }).ToArray();
            return Data(new JsonObject { ["team"] = new JsonObject { ["issues"] = new JsonObject
            {
                ["nodes"] = new JsonArray(nodes), ["pageInfo"] = new JsonObject { ["hasPreviousPage"] = start > 0, ["startCursor"] = start.ToString() },
            } } });
        }
        if (q.Contains("issue(id:$id){ comments"))
        {
            var list = Comments.GetValueOrDefault((string)v["id"]!) ?? [];
            var from = int.TryParse((string?)v["after"], out var a) ? a : 0;
            var page = list.Skip(from).Take(50).Select(x => x!.DeepClone()).ToArray();
            return Data(new JsonObject { ["issue"] = new JsonObject { ["comments"] = new JsonObject
            {
                ["nodes"] = new JsonArray(page), ["pageInfo"] = new JsonObject { ["hasNextPage"] = from + 50 < list.Count, ["endCursor"] = (from + 50).ToString() },
            } } });
        }
        return Error("Unrecognised query", "INVALID");
    }

    /// <summary>Forward cursor paging, one node per page, so the reader's loop is exercised.</summary>
    private static Reply Forward(string field, JsonNode[] all, string? after)
    {
        var from = int.TryParse(after, out var a) ? a : 0;
        var page = all.Skip(from).Take(2).Select(x => x.DeepClone()).ToArray();
        return Data(new JsonObject { ["team"] = new JsonObject { [field] = new JsonObject
        {
            ["nodes"] = new JsonArray(page), ["pageInfo"] = new JsonObject { ["hasNextPage"] = from + 2 < all.Length, ["endCursor"] = (from + 2).ToString() },
        } } });
    }
}
