using System.Text.Json.Nodes;

namespace ByteBench.Cli;

/// <summary>
/// Project structure from the command line: <c>projects create|update|delete</c>, <c>members</c>, <c>labels</c>, <c>versions</c>, <c>sprints</c>,
/// <c>repos create|update|archive|unarchive|delete</c>, <c>protection</c> (branch protection rules) and <c>webhooks</c>. Each is one REST call; the
/// server applies the permissions and rules, and its message is printed when it refuses.
/// </summary>
public sealed partial class CliApp
{
    private static readonly string[] Roles = ["viewer", "developer", "maintainer", "owner"];

    private string Role()
    {
        var role = args.Required("role").ToLowerInvariant();
        return Roles.Contains(role) ? role : throw CliException.Usage("--role must be viewer, developer, maintainer or owner.");
    }

    private async Task<int> SendAsync(HttpMethod method, string path, JsonObject? body, Func<JsonNode?, string> message, CancellationToken ct)
    {
        using var api = Connect();
        var r = await api.SendAsync(method, path, body, ct);
        if (AsJson) { if (r.Body != null) Print(r.Body); else Print(new JsonObject { ["ok"] = true }); }
        else env.Out.WriteLine(message(r.Body));
        return ExitCodes.Ok;
    }

    private static void Put(JsonObject o, string name, string? value) { if (value != null) o[name] = value; }

    private static string Seg2(string value) => Uri.EscapeDataString(value.Trim());

    // ---------------------------------------------------------------- projects

    private async Task<int> ProjectsCreateAsync(CancellationToken ct)
    {
        args.Allow("key", "name", "description", "visibility", "group");
        NoExtraWords(2);
        var body = new JsonObject { ["key"] = ProjectKey(args.Required("key")), ["name"] = args.Required("name") };
        Put(body, "description", args.Opt("description"));
        if (args.Opt("visibility") is { } v) body["visibility"] = v.ToLowerInvariant();
        if (args.Opt("group") is { } g) body["groupId"] = int.TryParse(g, out var gid) ? gid : throw CliException.Usage("--group must be a group id.");
        return await SendAsync(HttpMethod.Post, "api/v1/projects", body, r => $"Created project {Text.Line(r.S("key"))}: {Text.Line(r.S("webUrl"))}", ct);
    }

    private async Task<int> ProjectsUpdateAsync(CancellationToken ct)
    {
        args.Allow("name", "description", "visibility");
        var key = ProjectKey(OneWord(2, "the project key"));
        NoExtraWords(3);
        var body = new JsonObject();
        Put(body, "name", args.Opt("name"));
        Put(body, "description", args.Opt("description"));
        if (args.Opt("visibility") is { } v) body["visibility"] = v.ToLowerInvariant();
        if (body.Count == 0) throw CliException.Usage("Nothing to change: pass --name, --description or --visibility.");
        return await SendAsync(HttpMethod.Patch, $"api/v1/projects/{key}", body, r => $"Updated {Text.Line(r.S("key"))}.", ct);
    }

    private async Task<int> ProjectsDeleteAsync(CancellationToken ct)
    {
        args.Allow("confirm");
        var key = ProjectKey(OneWord(2, "the project key"));
        NoExtraWords(3);
        if (!string.Equals(args.Opt("confirm"), key, StringComparison.OrdinalIgnoreCase)) throw CliException.Usage($"This deletes {key} for good. Repeat the key to confirm: --confirm {key}");
        return await SendAsync(HttpMethod.Delete, $"api/v1/projects/{key}?confirm={key}", null, _ => $"Deleted project {key}.", ct);
    }

    // ---------------------------------------------------------------- members

    private async Task<int> MembersAsync(CancellationToken ct)
    {
        var sub = args.Words[1].ToLowerInvariant();
        if (sub is not ("list" or "add" or "role" or "remove")) throw CliException.Usage("members needs a subcommand: list, add, role or remove.");
        var key = ProjectKey(OneWord(2, "the project key"));
        if (sub == "list")
        {
            args.Allow("limit");
            NoExtraWords(3);
            using var api = Connect();
            var list = (await api.GetAsync($"api/v1/projects/{key}/members?perPage={args.Limit(100)}", ct)).Body;
            if (AsJson) { Print(list); return ExitCodes.Ok; }
            Text.PrintTable(env.Out, ["USER", "NAME", "ROLE"], list.Items().Select(m => new[] { Text.Line(m.S("user", "username")), Text.Cut(m.S("user", "displayName"), 40), Text.Line(m.S("role")) }).ToList());
            return ExitCodes.Ok;
        }
        var user = OneWord(3, "the username");
        NoExtraWords(4);
        switch (sub)
        {
            case "add":
                args.Allow("role");
                return await SendAsync(HttpMethod.Post, $"api/v1/projects/{key}/members", new JsonObject { ["user"] = user, ["role"] = Role() }, r => $"Added {Text.Line(user)} to {key} as {Text.Line(r.S("role"))}.", ct);
            case "role":
                args.Allow("role");
                return await SendAsync(HttpMethod.Patch, $"api/v1/projects/{key}/members/{Seg2(user)}", new JsonObject { ["role"] = Role() }, r => $"{Text.Line(user)} is now {Text.Line(r.S("role"))} in {key}.", ct);
            default:
                args.Allow();
                return await SendAsync(HttpMethod.Delete, $"api/v1/projects/{key}/members/{Seg2(user)}", null, _ => $"Removed {Text.Line(user)} from {key}.", ct);
        }
    }

    // ---------------------------------------------------------------- labels, versions, sprints

    private async Task<int> LabelsAsync(CancellationToken ct)
    {
        var sub = args.Words[1].ToLowerInvariant();
        var key = ProjectKey(OneWord(2, "the project key"));
        switch (sub)
        {
            case "list":
            {
                args.Allow("limit");
                NoExtraWords(3);
                using var api = Connect();
                var list = (await api.GetAsync($"api/v1/projects/{key}/labels?perPage={args.Limit(100)}", ct)).Body;
                if (AsJson) { Print(list); return ExitCodes.Ok; }
                Text.PrintTable(env.Out, ["ID", "NAME", "COLOR"], list.Items().Select(l => new[] { l.I("id").ToString(), Text.Line(l.S("name")), Text.Line(l.S("color")) }).ToList());
                return ExitCodes.Ok;
            }
            case "create":
            {
                args.Allow("name", "color");
                NoExtraWords(3);
                var body = new JsonObject { ["name"] = args.Required("name") };
                Put(body, "color", args.Opt("color"));
                return await SendAsync(HttpMethod.Post, $"api/v1/projects/{key}/labels", body, r => $"Created label {Text.Line(r.S("name"))} (id {r.I("id")}) in {key}.", ct);
            }
            case "delete":
            {
                args.Allow();
                var id = IdWord(3, "the label id");
                NoExtraWords(4);
                return await SendAsync(HttpMethod.Delete, $"api/v1/projects/{key}/labels/{id}", null, _ => $"Deleted label {id} from {key}.", ct);
            }
            default: throw CliException.Usage("labels needs a subcommand: list, create or delete.");
        }
    }

    private int IdWord(int index, string what) =>
        int.TryParse(OneWord(index, what), out var id) && id > 0 ? id : throw CliException.Usage($"{char.ToUpperInvariant(what[0])}{what[1..]} must be a positive integer.");

    private async Task<int> VersionsAsync(CancellationToken ct)
    {
        var sub = args.Words[1].ToLowerInvariant();
        var key = ProjectKey(OneWord(2, "the project key"));
        switch (sub)
        {
            case "list":
            {
                args.Allow("limit");
                NoExtraWords(3);
                using var api = Connect();
                var list = (await api.GetAsync($"api/v1/projects/{key}/versions?perPage={args.Limit(100)}", ct)).Body;
                if (AsJson) { Print(list); return ExitCodes.Ok; }
                Text.PrintTable(env.Out, ["ID", "VERSION", "RELEASED", "DATE"], list.Items().Select(v => new[] { v.I("id").ToString(), Text.Line(v.S("name")), v.B("released") ? "yes" : "no", Text.Line(v.S("releaseDate")) }).ToList());
                return ExitCodes.Ok;
            }
            case "create":
            {
                args.Allow("name", "description", "start", "end");
                NoExtraWords(3);
                var body = new JsonObject { ["name"] = args.Required("name") };
                Put(body, "description", args.Opt("description"));
                Put(body, "startDate", args.Opt("start"));
                Put(body, "releaseDate", args.Opt("end"));
                return await SendAsync(HttpMethod.Post, $"api/v1/projects/{key}/versions", body, r => $"Created version {Text.Line(r.S("name"))} (id {r.I("id")}) in {key}.", ct);
            }
            case "release" or "unrelease":
            {
                args.Allow();
                var id = IdWord(3, "the version id");
                NoExtraWords(4);
                return await SendAsync(HttpMethod.Patch, $"api/v1/projects/{key}/versions/{id}", new JsonObject { ["released"] = sub == "release" },
                    r => $"Version {Text.Line(r.S("name"))} is {(sub == "release" ? "released" : "unreleased")}.", ct);
            }
            default: throw CliException.Usage("versions needs a subcommand: list, create, release or unrelease.");
        }
    }

    private async Task<int> SprintsAsync(CancellationToken ct)
    {
        var sub = args.Words[1].ToLowerInvariant();
        var key = ProjectKey(OneWord(2, "the project key"));
        switch (sub)
        {
            case "list":
            {
                args.Allow("state", "limit");
                NoExtraWords(3);
                using var api = Connect();
                var list = (await api.GetAsync($"api/v1/projects/{key}/sprints?perPage={args.Limit(100)}" + (args.Opt("state") is { } s ? "&state=" + Seg(s) : ""), ct)).Body;
                if (AsJson) { Print(list); return ExitCodes.Ok; }
                Text.PrintTable(env.Out, ["ID", "NAME", "STATE", "ISSUES"], list.Items().Select(x => new[] { x.I("id").ToString(), Text.Cut(x.S("name"), 40), Text.Line(x.S("state")), x.I("issueCount").ToString() }).ToList());
                return ExitCodes.Ok;
            }
            case "create":
            {
                args.Allow("name", "goal", "start", "end");
                NoExtraWords(3);
                var body = new JsonObject();
                Put(body, "name", args.Opt("name"));
                Put(body, "goal", args.Opt("goal"));
                Put(body, "startDate", args.Opt("start"));
                Put(body, "endDate", args.Opt("end"));
                return await SendAsync(HttpMethod.Post, $"api/v1/projects/{key}/sprints", body, r => $"Created sprint {Text.Line(r.S("name"))} (id {r.I("id")}).", ct);
            }
            case "start":
            {
                args.Allow("start", "end", "goal");
                var id = IdWord(3, "the sprint id");
                NoExtraWords(4);
                var body = new JsonObject { ["startDate"] = args.Required("start"), ["endDate"] = args.Required("end") };
                Put(body, "goal", args.Opt("goal"));
                return await SendAsync(HttpMethod.Post, $"api/v1/projects/{key}/sprints/{id}/start", body, r => $"Started {Text.Line(r.S("name"))}.", ct);
            }
            case "complete":
            {
                args.Allow("to");
                var id = IdWord(3, "the sprint id");
                NoExtraWords(4);
                var body = new JsonObject();
                // --to backlog (default) | new | <planned sprint id>
                var to = args.Opt("to")?.ToLowerInvariant();
                if (to == "new") body["carryOver"] = "newSprint";
                else if (to is not (null or "backlog"))
                {
                    body["carryOver"] = "existingSprint";
                    body["targetSprintId"] = int.TryParse(to, out var target) ? target : throw CliException.Usage("--to must be backlog, new or a planned sprint id.");
                }
                return await SendAsync(HttpMethod.Post, $"api/v1/projects/{key}/sprints/{id}/complete", body,
                    r => $"Completed {Text.Line(r?["sprint"].S("name"))}." + (r?["carriedTo"] is { } c && c.S("name") is { Length: > 0 } n ? $" Open issues moved to {Text.Line(n)}." : " Open issues went to the backlog."), ct);
            }
            case "delete":
            {
                args.Allow();
                var id = IdWord(3, "the sprint id");
                NoExtraWords(4);
                return await SendAsync(HttpMethod.Delete, $"api/v1/projects/{key}/sprints/{id}", null, _ => $"Deleted sprint {id}.", ct);
            }
            default: throw CliException.Usage("sprints needs a subcommand: list, create, start, complete or delete.");
        }
    }

    // ---------------------------------------------------------------- repositories

    private async Task<int> ReposCreateAsync(CancellationToken ct)
    {
        args.Allow("project", "name", "slug", "description");
        NoExtraWords(2);
        var key = ProjectKey(args.Required("project"));
        var body = new JsonObject { ["name"] = args.Required("name") };
        Put(body, "slug", args.Opt("slug"));
        Put(body, "description", args.Opt("description"));
        return await SendAsync(HttpMethod.Post, $"api/v1/projects/{key}/repos", body, r => $"Created {key}/{Text.Line(r.S("slug"))}: {Text.Line(r.S("cloneUrl"))}", ct);
    }

    private async Task<int> ReposUpdateAsync(CancellationToken ct)
    {
        args.Allow("name", "description", "branch", "slug");
        var (key, slug) = RepoName(OneWord(2, "the repository (PROJECT/slug)"));
        NoExtraWords(3);
        var body = new JsonObject();
        Put(body, "name", args.Opt("name"));
        Put(body, "description", args.Opt("description"));
        Put(body, "defaultBranch", args.Opt("branch"));
        Put(body, "slug", args.Opt("slug"));
        if (body.Count == 0) throw CliException.Usage("Nothing to change: pass --name, --description, --branch (the default branch) or --slug.");
        return await SendAsync(HttpMethod.Patch, $"api/v1/projects/{key}/repos/{Seg(slug)}", body, r => $"Updated {key}/{Text.Line(r.S("slug"))} (default branch {Text.Line(r.S("defaultBranch"))}).", ct);
    }

    private async Task<int> ReposArchiveAsync(bool archive, CancellationToken ct)
    {
        args.Allow();
        var (key, slug) = RepoName(OneWord(2, "the repository (PROJECT/slug)"));
        NoExtraWords(3);
        return await SendAsync(HttpMethod.Post, $"api/v1/projects/{key}/repos/{Seg(slug)}/{(archive ? "archive" : "unarchive")}", new JsonObject(),
            _ => $"{key}/{slug} is {(archive ? "archived (read-only)" : "writable again")}.", ct);
    }

    private async Task<int> ReposDeleteAsync(CancellationToken ct)
    {
        args.Allow("confirm");
        var (key, slug) = RepoName(OneWord(2, "the repository (PROJECT/slug)"));
        NoExtraWords(3);
        var confirm = args.Opt("confirm") ?? throw CliException.Usage("This deletes the repository for good. Repeat its name to confirm: --confirm \"<repository name>\"");
        return await SendAsync(HttpMethod.Delete, $"api/v1/projects/{key}/repos/{Seg(slug)}?confirm={Seg(confirm)}", null, _ => $"Deleted {key}/{slug}.", ct);
    }

    // ---------------------------------------------------------------- branch protection, webhooks

    private async Task<int> ProtectionAsync(CancellationToken ct)
    {
        var sub = args.Words[1].ToLowerInvariant();
        var (key, slug) = RepoName(OneWord(2, "the repository (PROJECT/slug)"));
        var path = $"api/v1/projects/{key}/repos/{Seg(slug)}/branch-protection";
        switch (sub)
        {
            case "list":
            {
                args.Allow();
                NoExtraWords(3);
                using var api = Connect();
                var list = (await api.GetAsync(path + "?perPage=100", ct)).Body;
                if (AsJson) { Print(list); return ExitCodes.Ok; }
                Text.PrintTable(env.Out, ["ID", "PATTERN", "APPROVALS", "NO PUSH", "CHECKS"], list.Items().Select(r => new[]
                    { r.I("id").ToString(), Text.Line(r.S("pattern")), r.I("requiredApprovals").ToString(), r.B("blockDirectPushes") ? "yes" : "no", r.B("requireChecksToPass") ? "yes" : "no" }).ToList());
                return ExitCodes.Ok;
            }
            case "set":
            {
                // Creates the rule for the pattern, or updates it when one exists.
                args.Allow("pattern", "approvals", "type", "status");
                NoExtraWords(3);
                var pattern = args.Required("pattern");
                var body = new JsonObject { ["pattern"] = pattern };
                if (args.Opt("approvals") is { } a) body["requiredApprovals"] = int.TryParse(a, out var n) ? n : throw CliException.Usage("--approvals must be a number from 0 to 10.");
                // --type push-blocked | checks | signed (repeatable flags in one option each): kept small on purpose; the API has the full set.
                foreach (var flag in args.All("type"))
                    switch (flag.ToLowerInvariant())
                    {
                        case "block-pushes": body["blockDirectPushes"] = true; break;
                        case "require-checks": body["requireChecksToPass"] = true; break;
                        case "require-signed": body["requireSignedCommits"] = true; break;
                        case "allow-force-push": body["blockForcePushes"] = false; break;
                        case "allow-delete": body["blockDeletion"] = false; break;
                        default: throw CliException.Usage("--type must be block-pushes, require-checks, require-signed, allow-force-push or allow-delete.");
                    }
                using var api = Connect();
                var existing = (await api.GetAsync(path + "?perPage=100", ct)).Body.Items().FirstOrDefault(r => r.S("pattern") == pattern);
                var r = existing == null ? (await api.PostAsync(path, body, ct)).Body : (await api.PatchAsync($"{path}/{existing.I("id")}", body, ct)).Body;
                if (AsJson) Print(r);
                else env.Out.WriteLine($"{(existing == null ? "Created" : "Updated")} the rule for {Text.Line(pattern)} (id {r.I("id")}).");
                return ExitCodes.Ok;
            }
            case "delete":
            {
                args.Allow();
                var id = IdWord(3, "the rule id");
                NoExtraWords(4);
                return await SendAsync(HttpMethod.Delete, $"{path}/{id}", null, _ => $"Deleted rule {id}.", ct);
            }
            default: throw CliException.Usage("protection needs a subcommand: list, set or delete.");
        }
    }

    private async Task<int> WebhooksAsync(CancellationToken ct)
    {
        var sub = args.Words[1].ToLowerInvariant();
        var key = ProjectKey(OneWord(2, "the project key"));
        var path = $"api/v1/projects/{key}/webhooks";
        switch (sub)
        {
            case "list":
            {
                args.Allow();
                NoExtraWords(3);
                using var api = Connect();
                var list = (await api.GetAsync(path + "?perPage=100", ct)).Body;
                if (AsJson) { Print(list); return ExitCodes.Ok; }
                Text.PrintTable(env.Out, ["ID", "ACTIVE", "URL", "EVENTS"], list.Items().Select(h => new[]
                    { h.I("id").ToString(), h.B("active") ? "yes" : "no", Text.Cut(h.S("url"), 60), Text.Cut(string.Join(",", h?["events"].Items().Select(e => e?.ToString() ?? "") ?? []), 40) }).ToList());
                return ExitCodes.Ok;
            }
            case "create":
            {
                args.Allow("url", "type");
                NoExtraWords(3);
                var events = args.All("type");
                if (events.Count == 0) throw CliException.Usage("Name at least one event with --type, for example --type push --type issue.created.");
                var body = new JsonObject { ["url"] = args.Required("url"), ["events"] = new JsonArray(events.Select(e => (JsonNode)e).ToArray()) };
                return await SendAsync(HttpMethod.Post, path, body,
                    r => $"Created webhook {r.I("id")}. Signing secret (shown once): {Text.Line(r.S("secret"))}", ct);
            }
            case "delete":
            {
                args.Allow();
                var id = IdWord(3, "the webhook id");
                NoExtraWords(4);
                return await SendAsync(HttpMethod.Delete, $"{path}/{id}", null, _ => $"Deleted webhook {id}.", ct);
            }
            default: throw CliException.Usage("webhooks needs a subcommand: list, create or delete.");
        }
    }
}
