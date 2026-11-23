using System.Text.Json.Nodes;

namespace ByteBench.Tests.Infrastructure;

/// <summary>
/// A fake Trello REST API 1.0 (shapes from developer.atlassian.com/cloud/trello/rest): board, lists, labels, members, cards (paged
/// with <c>before=&lt;id&gt;</c> and capped to <see cref="PageSize"/> like a server cap), checklists and comment actions (newest
/// first, paged the same way). Credentials are the <c>key</c> and <c>token</c> query parameters; a wrong pair answers 401 with
/// Trello's plain-text body.
/// </summary>
public sealed class FakeTrello : FakeSaasServer
{
    public const string Key = "trellokey0123456789abcdef01234567";
    public const string Token = "trellotoken-0123456789abcdef0123456789abcdef0123456789abcdef01234567";
    public const string BoardId = "5f1e2d3c4b5a69788796a5b4";
    public const string ShortLink = "Xy12AbCd";

    public string ExpectedKey { get; set; } = Key;
    public string ExpectedToken { get; set; } = Token;
    public int PageSize { get; set; } = 2;
    public JsonArray Cards { get; set; }
    public JsonArray Actions { get; set; }
    public JsonArray Lists { get; set; }

    public static string Id(int n) => (0x5f000000 + n).ToString("x8") + "0000000000000000";

    private static JsonObject Label(string id, string name, string color) => new() { ["id"] = id, ["name"] = name, ["color"] = color };
    private static JsonObject Member(string id, string name, string user) => new() { ["id"] = id, ["fullName"] = name, ["username"] = user };

    private static JsonObject Card(int n, string name, string list, Action<JsonObject>? tweak = null)
    {
        var o = new JsonObject
        {
            ["id"] = Id(n), ["idShort"] = n, ["name"] = name, ["desc"] = "Body of " + name, ["closed"] = false, ["idList"] = list,
            ["idMembers"] = new JsonArray(), ["labels"] = new JsonArray(), ["due"] = null, ["dueComplete"] = false,
            ["shortUrl"] = "https://trello.com/c/card" + n, ["dateLastActivity"] = "2024-06-0" + n + "T10:00:00.000Z", ["attachments"] = new JsonArray(),
        };
        tweak?.Invoke(o);
        return o;
    }

    private static JsonObject Comment(int n, string cardNumber, string text, string user, string name, string date) => new()
    {
        ["id"] = Id(n), ["type"] = "commentCard", ["date"] = date,
        ["data"] = new JsonObject { ["text"] = text, ["card"] = new JsonObject { ["id"] = Id(int.Parse(cardNumber)), ["name"] = "x" } },
        ["memberCreator"] = Member("m-" + user, name, user),
    };

    public FakeTrello()
    {
        Lists =
        [
            new JsonObject { ["id"] = "l-todo", ["name"] = "To Do", ["closed"] = false, ["pos"] = 1 },
            new JsonObject { ["id"] = "l-doing", ["name"] = "Doing", ["closed"] = false, ["pos"] = 2 },
            new JsonObject { ["id"] = "l-done", ["name"] = "Done", ["closed"] = false, ["pos"] = 3 },
            new JsonObject { ["id"] = "l-old", ["name"] = "Old stuff", ["closed"] = true, ["pos"] = 4 },
        ];
        Cards =
        [
            Card(1, "Fix login", "l-doing", o =>
            {
                o["labels"] = new JsonArray(Label("a", "bug", "red"), Label("b", "", "green"));
                o["idMembers"] = new JsonArray("m-bob");
                o["due"] = "2024-06-01T12:00:00.000Z";
                o["desc"] = "Steps to reproduce";
                o["attachments"] = new JsonArray(new JsonObject { ["id"] = "att1", ["name"] = "log.txt", ["url"] = "https://trello.com/1/cards/x/attachments/att1/download/log.txt" });
            }),
            Card(2, "Write docs", "l-done"),
            Card(3, "Closed card", "l-todo", o => o["closed"] = true),
            Card(4, "Due done", "l-doing", o => o["dueComplete"] = true),
            Card(5, "Shiny feature", "l-todo", o => { o["labels"] = new JsonArray(Label("c", "Feature", "blue")); o["desc"] = ""; }),
            Card(6, "Old archived", "l-old"),
        ];
        Actions =
        [
            Comment(101, "1", "I can reproduce this", "bob", "Bob Builder", "2024-06-02T10:00:00.000Z"),
            Comment(102, "1", "Fixed in the next build", "stranger", "Some Stranger", "2024-06-03T10:00:00.000Z"),
            Comment(103, "1", "Thanks", "bob", "Bob Builder", "2024-06-04T10:00:00.000Z"),
            Comment(104, "2", "Docs are live", "bob", "Bob Builder", "2024-06-05T10:00:00.000Z"),
        ];
        Start();
    }

    private JsonArray Checklists() =>
    [
        new JsonObject
        {
            ["id"] = "cl1", ["name"] = "Release steps", ["idCard"] = Id(1),
            ["checkItems"] = new JsonArray(
                new JsonObject { ["name"] = "Build", ["state"] = "complete", ["pos"] = 1 },
                new JsonObject { ["name"] = "Ship", ["state"] = "incomplete", ["pos"] = 2 }),
        },
    ];

    private JsonArray PageDesc(JsonArray all, Request r)
    {
        var before = r.Param("before");
        var limit = Math.Min(int.TryParse(r.Param("limit"), out var l) ? l : PageSize, PageSize);
        var items = all.Where(i => before == null || string.CompareOrdinal((string?)i!["id"], before) < 0)
            .OrderByDescending(i => (string?)i!["id"], StringComparer.Ordinal).Take(limit).Select(i => (JsonNode?)i!.DeepClone()).ToArray();
        return new JsonArray(items);
    }

    protected override Reply Route(Request r)
    {
        var segs = r.Path.Trim('/').Split('/');
        if (segs.Length < 2 || segs[0] != "1" || segs[1] != "boards") return new Reply(404, "not found");
        if (r.Param("key") != ExpectedKey || r.Param("token") != ExpectedToken) return new Reply(401, "invalid token");
        if (RateLimitOnce) { RateLimitOnce = false; return new Reply(429, "{}", new() { ["Retry-After"] = "1" }); }
        if (segs.Length < 3 || (segs[2] != BoardId && segs[2] != ShortLink)) return new Reply(404, "The requested resource was not found.");
        if (segs.Length == 3)
            return Json(new JsonObject { ["id"] = BoardId, ["name"] = "Roadmap board", ["desc"] = "What we build", ["closed"] = false, ["shortLink"] = ShortLink, ["url"] = "https://trello.com/b/" + ShortLink });
        switch (segs[3])
        {
            case "lists": return Json(Lists.DeepClone());
            case "labels": return Json(new JsonArray(Label("a", "bug", "red"), Label("b", "", "green"), Label("c", "Feature", "blue")));
            case "members": return Json(new JsonArray(Member("m-bob", "Bob Builder", "bob"), Member("m-stranger", "Some Stranger", "stranger")));
            case "checklists": return Json(Checklists());
            case "actions": return Json(PageDesc(Actions, r));
            case "cards":
                var filter = segs.Length > 4 ? segs[4] : "open";
                var cards = new JsonArray(Cards.Where(c => filter == "all" || (!(bool)c!["closed"]! && (string?)c["idList"] != "l-old")).Select(c => (JsonNode?)c!.DeepClone()).ToArray());
                return Json(PageDesc(cards, r));
        }
        return new Reply(404, "not found");
    }
}
