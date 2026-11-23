using System.Text.Json.Nodes;

namespace ByteBench.Tests.Infrastructure;

/// <summary>
/// A fake Asana REST API 1.0 (shapes from developers.asana.com/reference): <c>{data}</c> answers, <c>Authorization: Bearer</c>,
/// lists paged with <c>limit</c>/<c>offset</c> and a <c>next_page</c> whose <c>uri</c> names an internal host (the reader must ignore it),
/// capped to <see cref="PageSize"/>. Covers project, task_counts, sections, tasks, subtasks, stories, dependents and attachments.
/// </summary>
public sealed class FakeAsana : FakeSaasServer
{
    public const string Token = "2/1200000000000000/1200111111111111:fakefakefakefakefakefakefakefake";
    public const string ProjectGid = "1200345678901";

    public string ExpectedToken { get; set; } = Token;
    public int PageSize { get; set; } = 2;
    public JsonArray Tasks { get; set; }
    public Dictionary<string, JsonArray> Subtasks { get; } = [];
    public Dictionary<string, JsonArray> Stories { get; } = [];
    public Dictionary<string, JsonArray> Dependents { get; } = [];
    public Dictionary<string, JsonArray> Attachments { get; } = [];

    private static string G(int n) => (1201000000000L + n).ToString();
    private static JsonObject Person(string name, string? email = null) => new() { ["gid"] = "u-" + name, ["name"] = name, ["email"] = email };

    private static JsonObject Task(int n, string name, string? section, Action<JsonObject>? tweak = null)
    {
        var o = new JsonObject
        {
            ["gid"] = G(n), ["name"] = name, ["notes"] = "Notes of " + name, ["completed"] = false, ["completed_at"] = null,
            ["created_at"] = $"2024-05-0{n}T12:00:00.000Z", ["modified_at"] = $"2024-05-1{n}T12:00:00.000Z", ["due_on"] = null, ["due_at"] = null,
            ["assignee"] = null, ["created_by"] = Person("Bob Builder"), ["tags"] = new JsonArray(), ["num_subtasks"] = 0,
            ["permalink_url"] = $"https://app.asana.com/0/{ProjectGid}/{G(n)}", ["resource_subtype"] = "default_task", ["custom_fields"] = new JsonArray(),
            ["memberships"] = section == null ? new JsonArray() : new JsonArray(new JsonObject { ["project"] = new JsonObject { ["gid"] = ProjectGid }, ["section"] = new JsonObject { ["name"] = section } }),
        };
        tweak?.Invoke(o);
        return o;
    }

    private static JsonObject Tag(string name) => new() { ["gid"] = "t-" + name, ["name"] = name, ["color"] = "dark-pink" };

    private static JsonObject Story(string gid, string type, string text, string by, string at) =>
        new() { ["gid"] = gid, ["type"] = type, ["text"] = text, ["created_by"] = Person(by), ["created_at"] = at };

    public FakeAsana()
    {
        Tasks =
        [
            Task(1, "Login fails", "Doing", o =>
            {
                o["tags"] = new JsonArray(Tag("bug"));
                o["assignee"] = Person("Bob Builder", "bob@example.com");
                o["created_by"] = Person("Some Stranger");
                o["due_on"] = "2024-06-01"; o["num_subtasks"] = 2;
                o["custom_fields"] = new JsonArray(
                    new JsonObject { ["name"] = "Story points", ["type"] = "number", ["number_value"] = 5, ["display_value"] = "5" },
                    new JsonObject { ["name"] = "Priority", ["type"] = "enum", ["enum_value"] = new JsonObject { ["name"] = "High" }, ["display_value"] = "High" });
            }),
            Task(2, "Write docs", "Done", o => { o["completed"] = true; o["completed_at"] = "2024-06-02T09:00:00.000Z"; }),
            Task(3, "Release milestone", "To Do", o => { o["resource_subtype"] = "milestone"; o["tags"] = new JsonArray(Tag("Low priority")); }),
            Task(4, "Big epic", "To Do", o => { o["tags"] = new JsonArray(Tag("epic")); o["num_subtasks"] = 1; }),
            Task(5, "Done in Doing", "Doing", o => { o["completed"] = true; o["completed_at"] = "2024-06-03T09:00:00.000Z"; }),
        ];
        Subtasks[G(1)] = [Task(11, "Reproduce", null, o => o["parent"] = new JsonObject { ["gid"] = G(1) }), Task(12, "Fix it", null, o => { o["parent"] = new JsonObject { ["gid"] = G(1) }; o["completed"] = true; })];
        Subtasks[G(4)] = [Task(13, "Epic child", null, o => o["parent"] = new JsonObject { ["gid"] = G(4) })];
        Stories[G(1)] =
        [
            Story("s1", "comment", "I can reproduce this", "Bob Builder", "2024-06-02T10:00:00.000Z"),
            Story("s2", "system", "changed the due date", "Bob Builder", "2024-06-02T11:00:00.000Z"),
            Story("s3", "comment", "Fixed in the next build", "Some Stranger", "2024-06-03T10:00:00.000Z"),
        ];
        Dependents[G(1)] = [new JsonObject { ["gid"] = G(3) }];
        Attachments[G(1)] = [new JsonObject { ["gid"] = "a1", ["name"] = "log.txt", ["permanent_url"] = "https://app.asana.com/app/asana/-/get_asset?asset_id=1" }];
        Start();
    }

    private Reply Page(JsonArray all, Request r, Func<JsonNode, bool>? keep = null)
    {
        var items = all.Where(i => keep == null || keep(i!)).ToList();
        var from = int.TryParse(r.Param("offset"), out var o) ? o : 0;
        var take = int.TryParse(r.Param("limit"), out var l) && l < 100 ? l : PageSize; // a small limit is honoured; the full 100 is capped like a busy server
        var page = items.Skip(from).Take(take).Select(i => i!.DeepClone()).ToArray();
        var body = new JsonObject { ["data"] = new JsonArray(page) };
        body["next_page"] = from + take < items.Count
            ? new JsonObject { ["offset"] = (from + take).ToString(), ["path"] = "/x", ["uri"] = "https://internal.asana.invalid/api/1.0/x?offset=" + (from + take) }
            : null;
        return Json(body);
    }

    protected override Reply Route(Request r)
    {
        var segs = r.Path.Trim('/').Split('/');
        if (segs.Length < 3 || segs[0] != "api" || segs[1] != "1.0") return new Reply(404, "{}");
        if (r.Authorization != "Bearer " + ExpectedToken) return Json(new JsonObject { ["errors"] = new JsonArray(new JsonObject { ["message"] = "Not Authorized" }) }, 401);
        if (RateLimitOnce) { RateLimitOnce = false; return new Reply(429, "{\"errors\":[{\"message\":\"slow down\"}]}", new() { ["Retry-After"] = "1" }); }
        var rest = segs.Skip(2).ToList();
        switch (rest)
        {
            case ["projects", var gid] when gid == ProjectGid:
                return Json(new JsonObject { ["data"] = new JsonObject { ["gid"] = ProjectGid, ["name"] = "Website relaunch", ["notes"] = "Ship the new site", ["archived"] = false } });
            case ["projects", var gid, "task_counts"] when gid == ProjectGid:
                return Json(new JsonObject { ["data"] = new JsonObject { ["num_tasks"] = Tasks.Count, ["num_completed_tasks"] = 2 } });
            case ["projects", var gid, "sections"] when gid == ProjectGid:
                return Page([new JsonObject { ["gid"] = "sec1", ["name"] = "To Do" }, new JsonObject { ["gid"] = "sec2", ["name"] = "Doing" }, new JsonObject { ["gid"] = "sec3", ["name"] = "Done" }], r);
            case ["projects", var gid, "tasks"] when gid == ProjectGid:
                return Page(Tasks, r, t => r.Param("completed_since") == null || !(bool)t["completed"]!);
            case ["tasks", var tg, "subtasks"]: return Page(Subtasks.GetValueOrDefault(tg) ?? [], r);
            case ["tasks", var tg, "stories"]: return Page(Stories.GetValueOrDefault(tg) ?? [], r);
            case ["tasks", var tg, "dependents"]: return Page(Dependents.GetValueOrDefault(tg) ?? [], r);
            case ["attachments"]: return Page(Attachments.GetValueOrDefault(r.Param("parent") ?? "") ?? [], r);
        }
        return Json(new JsonObject { ["errors"] = new JsonArray(new JsonObject { ["message"] = "project: Unknown object: " + string.Join("/", rest) }) }, 404);
    }
}
