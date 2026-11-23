using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ByteBench.Data;
using ByteBench.Services.Accounts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Group = ByteBench.Data.Group;

namespace ByteBench.Services.Scim;

/// <summary>
/// SCIM 2.0 Groups on ByteBench groups. Every group can be read (its full path, or the IdP's displayName for a group SCIM
/// manages, and its direct members). Writes are conservative:
/// <list type="bullet">
/// <item><c>POST</c> creates a top-level group (displayName → name; a unique path derived from it; visibility
/// <c>Scim:GroupVisibility</c>) with no Owner, or adopts the existing group a <c>Scim:GroupMappings</c> entry names.</item>
/// <item><c>PUT</c>/<c>PATCH</c>/<c>DELETE</c> only touch groups SCIM manages (<see cref="ScimGroup"/>); any other group is 403.</item>
/// <item>Members SCIM adds get <c>Scim:GroupMemberRole</c> (capped at Maintainer, never Owner). SCIM never removes a member
/// whose role is above that, never adds deactivated or system accounts, and never adds or removes a site admin unless
/// <c>Scim:ManageAdmins</c>. A request that would break a rule is refused as a whole.</item>
/// <item><c>DELETE</c> of a created group is 409 while it has projects or subgroups; otherwise the group, its memberships and the
/// association go. For an adopted group only the association goes.</item>
/// </list>
/// Every write is audited as <c>scim.group_*</c> with the actor "SCIM".
/// </summary>
public sealed partial class ScimGroupService(IDbContextFactory<AppDbContext> dbFactory, Audit.AuditLog audit, IOptionsMonitor<ScimOptions> options)
{
    private const string Actor = ScimUserService.Actor;
    private const int MaxOperations = 100;

    // ------------------------------------------------------------------ reading

    private sealed record Row(int Id, string FullPath, string Name, string? ScimDisplayName, string? ExternalId, bool Managed,
        DateTime CreatedAt, DateTime? Modified, List<(int UserId, string UserName)> Members)
    {
        public string DisplayName => ScimDisplayName ?? FullPath;
    }

    private async Task<List<Row>> LoadAsync(int? id)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var q = db.Groups.AsNoTracking().AsQueryable();
        if (id != null) q = q.Where(g => g.Id == id);
        var groups = await q.OrderBy(g => g.Id).Select(g => new
        {
            g.Id, g.FullPath, g.Name, g.CreatedAt,
            Scim = db.ScimGroups.Where(s => s.GroupId == g.Id).Select(s => new { s.DisplayName, s.ExternalId, s.UpdatedAt }).FirstOrDefault(),
            Members = g.Members.OrderBy(m => m.UserId).Select(m => new { m.UserId, m.User.UserName }).ToList(),
        }).ToListAsync();
        return groups.Select(g => new Row(g.Id, g.FullPath, g.Name, g.Scim?.DisplayName, g.Scim?.ExternalId, g.Scim != null, g.CreatedAt, g.Scim?.UpdatedAt,
            g.Members.Where(m => !SystemAccounts.IsSystem(m.UserName)).Select(m => (m.UserId, m.UserName ?? "")).ToList())).ToList();
    }

    private static JsonObject ToJson(Row g, string baseUrl)
    {
        var o = new JsonObject
        {
            ["schemas"] = new JsonArray(ScimUserService.GroupSchema),
            ["id"] = g.Id.ToString(),
            ["displayName"] = g.DisplayName,
            ["members"] = new JsonArray(g.Members.Select(m => (JsonNode)new JsonObject
            {
                ["value"] = m.UserId.ToString(), ["display"] = m.UserName, ["$ref"] = $"{baseUrl}/Users/{m.UserId}", ["type"] = "User",
            }).ToArray()),
        };
        if (g.ExternalId != null) o["externalId"] = g.ExternalId;
        var meta = new JsonObject
        {
            ["resourceType"] = "Group", ["location"] = $"{baseUrl}/Groups/{g.Id}",
            ["created"] = DateTime.SpecifyKind(g.CreatedAt, DateTimeKind.Utc).ToString("O"),
        };
        if (g.Modified is { } m) meta["lastModified"] = DateTime.SpecifyKind(m, DateTimeKind.Utc).ToString("O");
        o["meta"] = meta;
        return o;
    }

    public async Task<(List<JsonObject> Page, int Total)> ListAsync(string? filter, int startIndex, int count, string baseUrl)
    {
        var all = await LoadAsync(null);
        if (!string.IsNullOrWhiteSpace(filter))
        {
            var comparisons = ScimFilter.Parse(filter, ScimUserService.GroupFilterAttributes);
            all = all.Where(g => comparisons.All(c => ScimFilter.Matches(c, c.Attribute switch
            {
                "displayName" => [g.DisplayName],
                "id" => [g.Id.ToString()],
                "externalId" => [g.ExternalId],
                _ => Array.Empty<string?>(),
            }))).ToList();
        }
        return (all.Skip(startIndex - 1).Take(count).Select(g => ToJson(g, baseUrl)).ToList(), all.Count);
    }

    public async Task<JsonObject?> GetAsync(int id, string baseUrl) => (await LoadAsync(id)).FirstOrDefault() is { } g ? ToJson(g, baseUrl) : null;

    private async Task<JsonObject> GetRequiredAsync(int id, string baseUrl) => await GetAsync(id, baseUrl) ?? throw new ScimError(404, $"Group {id} not found.");

    // ------------------------------------------------------------------ writing

    /// <summary>What a create, replace or patch asks for. Null: not given.</summary>
    private sealed class Changes
    {
        public string? DisplayName, ExternalId;
        public bool ClearExternalId;
        /// <summary>Member edits in order: (op, user ids). op is add, remove, replace or removeAll.</summary>
        public List<(string Op, List<int> Ids)> Members = [];
    }

    public async Task<JsonObject> CreateAsync(JsonObject body, string baseUrl)
    {
        var c = new Changes();
        ApplyResource(body, c, "add");
        if (string.IsNullOrWhiteSpace(c.DisplayName)) throw new ScimError(400, "displayName is required.", "invalidValue");
        var display = c.DisplayName.Trim();
        var normalized = display.ToUpperInvariant();
        var o = options.CurrentValue;

        await using var db = await dbFactory.CreateDbContextAsync();
        if (await db.ScimGroups.AnyAsync(s => s.NormalizedDisplayName == normalized))
            throw new ScimError(409, "A group with this displayName already exists.", "uniqueness");

        Group group;
        var adopted = false;
        var mapping = o.GroupMappings.FirstOrDefault(m => string.Equals(m.DisplayName.Trim(), display, StringComparison.OrdinalIgnoreCase));
        if (mapping != null)
        {
            var path = GroupService.NormalizePath(mapping.Group);
            group = await db.Groups.FirstOrDefaultAsync(g => g.FullPath == path)
                    ?? throw new ScimError(400, $"Scim:GroupMappings maps '{display}' to the group '{path}', which doesn't exist.", "invalidValue");
            if (await db.ScimGroups.AnyAsync(s => s.GroupId == group.Id))
                throw new ScimError(409, "The mapped group is already managed by SCIM under another displayName.", "uniqueness");
            adopted = true;
        }
        else
        {
            var path = await UniquePathAsync(db, display);
            group = new Group
            {
                Name = display.Length > 100 ? display[..100] : display,
                Path = path, FullPath = path,
                Visibility = o.GroupVisibility == ProjectVisibility.Internal ? ProjectVisibility.Internal : ProjectVisibility.Private,
            };
            db.Groups.Add(group);
        }

        var scim = new ScimGroup { Group = group, DisplayName = display, NormalizedDisplayName = normalized, ExternalId = c.ExternalId, Adopted = adopted };
        db.ScimGroups.Add(scim);
        var memberEvents = await PlanMembersAsync(db, group, c, adopted ? await CurrentMembersAsync(db, group.Id) : []);
        try { await db.SaveChangesAsync(); }
        catch (DbUpdateException) { throw new ScimError(409, "A group with this displayName or path already exists.", "uniqueness"); }

        await audit.RecordAsync("scim.group_created", "group", group.Id,
            details: new { group = group.FullPath, displayName = display, adopted, visibility = group.Visibility.ToString() }, anonymous: true, actorName: Actor);
        await AuditMembersAsync(group, memberEvents);
        return await GetRequiredAsync(group.Id, baseUrl);
    }

    public async Task<JsonObject> ReplaceAsync(int id, JsonObject body, string baseUrl)
    {
        var c = new Changes();
        // PUT: "members" replaces the list. A PUT without "members" leaves the members as they are (conservative).
        ApplyResource(body, c, "replace");
        if (string.IsNullOrWhiteSpace(c.DisplayName)) throw new ScimError(400, "displayName is required.", "invalidValue");
        if (c.ExternalId == null) c.ClearExternalId = true;
        return await ApplyAsync(id, c, baseUrl);
    }

    public async Task<JsonObject> PatchAsync(int id, JsonObject body, string baseUrl)
    {
        if (body["Operations"] is not JsonArray ops || ops.Count == 0) throw new ScimError(400, "The PATCH request has no Operations.", "invalidSyntax");
        if (ops.Count > MaxOperations) throw new ScimError(400, "Too many operations.", "invalidSyntax");
        var c = new Changes();
        foreach (var node in ops)
        {
            if (node is not JsonObject op) throw new ScimError(400, "Each operation must be an object.", "invalidSyntax");
            var name = (op["op"] as JsonValue)?.GetValue<object>()?.ToString()?.ToLowerInvariant();
            var path = (op["path"] as JsonValue)?.GetValue<object>()?.ToString()?.Trim();
            var value = op["value"];
            if (name is not ("add" or "replace" or "remove")) throw new ScimError(400, $"Unknown PATCH op '{name}'.", "invalidSyntax");

            if (string.IsNullOrEmpty(path))
            {
                if (name == "remove") throw new ScimError(400, "remove needs a path.", "noTarget");
                if (value is not JsonObject obj) throw new ScimError(400, "An operation without a path needs an object value.", "invalidValue");
                ApplyResource(obj, c, name);
                continue;
            }
            if (path.StartsWith(ScimUserService.GroupSchema + ":", StringComparison.OrdinalIgnoreCase)) path = path[(ScimUserService.GroupSchema.Length + 1)..];

            if (MemberFilterRx().Match(path) is { Success: true } m)
            {
                // members[value eq "12"]: Okta's remove (and replace/add of that one member).
                if (!int.TryParse(m.Groups[1].Value, out var uid)) throw new ScimError(400, $"Unknown member '{m.Groups[1].Value}'.", "invalidValue");
                c.Members.Add((name == "remove" ? "remove" : "add", [uid]));
                continue;
            }
            switch (path.ToLowerInvariant())
            {
                case "members":
                    if (name == "remove")
                        c.Members.Add(value == null ? ("removeAll", []) : ("remove", MemberIds(value)));
                    else c.Members.Add((name, MemberIds(value)));
                    break;
                case "displayname":
                    if (name == "remove") throw new ScimError(400, "displayName can't be removed.", "mutability");
                    c.DisplayName = Str(value, "displayName") ?? throw new ScimError(400, "displayName can't be empty.", "invalidValue");
                    break;
                case "externalid":
                    if (name == "remove") c.ClearExternalId = true;
                    else { c.ExternalId = Str(value, "externalId"); c.ClearExternalId = c.ExternalId == null; }
                    break;
                default:
                    throw new ScimError(400, $"'{path}' isn't a Group attribute ByteBench supports.", "invalidPath");
            }
        }
        return await ApplyAsync(id, c, baseUrl);
    }

    public async Task DeleteAsync(int id)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var (group, scim) = await RequireManagedAsync(db, id);
        if (scim.Adopted)
        {
            db.ScimGroups.Remove(scim);
            await db.SaveChangesAsync();
            await audit.RecordAsync("scim.group_deleted", "group", group.Id, details: new { group = group.FullPath, displayName = scim.DisplayName, adopted = true, groupKept = true },
                anonymous: true, actorName: Actor);
            return;
        }
        if (await db.Groups.AnyAsync(g => g.ParentId == id) || await db.Projects.AnyAsync(p => p.GroupId == id))
            throw new ScimError(409, "This group contains projects or subgroups, so ByteBench keeps it. Move or delete them in ByteBench first.");
        var memberCount = await db.GroupMembers.CountAsync(m => m.GroupId == id);
        await db.GroupMembers.Where(m => m.GroupId == id).ExecuteDeleteAsync();
        await db.ScimGroups.Where(s => s.Id == scim.Id).ExecuteDeleteAsync();
        await db.Groups.Where(g => g.Id == id).ExecuteDeleteAsync(); // labels, milestones, redirects: cascade
        await audit.RecordAsync("scim.group_deleted", "group", id, details: new { group = group.FullPath, displayName = scim.DisplayName, members = memberCount },
            anonymous: true, actorName: Actor);
    }

    private async Task<JsonObject> ApplyAsync(int id, Changes c, string baseUrl)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var (group, scim) = await RequireManagedAsync(db, id);
        var changed = new List<string>();
        if (c.DisplayName is { } d && d.Trim() != scim.DisplayName)
        {
            var display = d.Trim();
            var normalized = display.ToUpperInvariant();
            if (await db.ScimGroups.AnyAsync(s => s.NormalizedDisplayName == normalized && s.Id != scim.Id))
                throw new ScimError(409, "A group with this displayName already exists.", "uniqueness");
            scim.DisplayName = display;
            scim.NormalizedDisplayName = normalized;
            if (!scim.Adopted) group.Name = display.Length > 100 ? display[..100] : display; // an adopted group keeps its ByteBench name; the path never changes
            changed.Add("displayName");
        }
        if (c.ExternalId != null && c.ExternalId != scim.ExternalId) { scim.ExternalId = c.ExternalId; changed.Add("externalId"); }
        else if (c.ClearExternalId && c.ExternalId == null && scim.ExternalId != null) { scim.ExternalId = null; changed.Add("externalId"); }

        var memberEvents = await PlanMembersAsync(db, group, c, await CurrentMembersAsync(db, group.Id));
        if (changed.Count > 0 || memberEvents.Count > 0) scim.UpdatedAt = DateTime.UtcNow;
        try { await db.SaveChangesAsync(); }
        catch (DbUpdateException) { throw new ScimError(409, "A conflicting change was made at the same time. Try again.", "uniqueness"); }

        if (changed.Count > 0)
            await audit.RecordAsync("scim.group_updated", "group", group.Id, details: new { group = group.FullPath, changed }, anonymous: true, actorName: Actor);
        await AuditMembersAsync(group, memberEvents);
        return await GetRequiredAsync(id, baseUrl);
    }

    private static async Task<(Group Group, ScimGroup Scim)> RequireManagedAsync(AppDbContext db, int id)
    {
        var group = await db.Groups.FirstOrDefaultAsync(g => g.Id == id) ?? throw new ScimError(404, $"Group {id} not found.");
        var scim = await db.ScimGroups.FirstOrDefaultAsync(s => s.GroupId == id)
                   ?? throw new ScimError(403, "This group isn't managed by SCIM: it was created in ByteBench. SCIM can change only groups it created or that Scim:GroupMappings maps to it.");
        return (group, scim);
    }

    private static async Task<Dictionary<int, ProjectRole>> CurrentMembersAsync(AppDbContext db, int groupId) =>
        await db.GroupMembers.Where(m => m.GroupId == groupId).ToDictionaryAsync(m => m.UserId, m => m.Role);

    private sealed record MemberEvent(bool Added, int UserId, string? UserName, ProjectRole Role);

    /// <summary>
    /// Works out the final member list from the requested edits, checks every resulting add and remove against the rules
    /// (all or nothing), and stages the changes on <paramref name="db"/>.
    /// </summary>
    private async Task<List<MemberEvent>> PlanMembersAsync(AppDbContext db, Group group, Changes c, Dictionary<int, ProjectRole> current)
    {
        if (c.Members.Count == 0) return [];
        // System accounts are invisible to SCIM: they are never in the working set and never removed.
        var systemIds = current.Count == 0 ? [] : (await db.Users.Where(u => current.Keys.Contains(u.Id)).Select(u => new { u.Id, u.UserName }).ToListAsync())
            .Where(u => SystemAccounts.IsSystem(u.UserName)).Select(u => u.Id).ToHashSet();
        var visible = current.Keys.Where(k => !systemIds.Contains(k)).ToHashSet();
        var working = new HashSet<int>(visible);
        foreach (var (op, ids) in c.Members)
            switch (op)
            {
                case "add": working.UnionWith(ids); break;
                case "remove": working.ExceptWith(ids); break;
                case "replace": working = [.. ids]; break;
                case "removeAll": working.Clear(); break;
            }
        var adds = working.Except(visible).ToList();
        var removes = visible.Except(working).ToList();
        if (adds.Count == 0 && removes.Count == 0) return [];

        var o = options.CurrentValue;
        var role = o.EffectiveGroupMemberRole;
        var touched = adds.Concat(removes).ToList();
        var users = await db.Users.Where(u => touched.Contains(u.Id)).ToDictionaryAsync(u => u.Id);
        var events = new List<MemberEvent>();
        foreach (var uid in adds)
        {
            if (!users.TryGetValue(uid, out var u) || SystemAccounts.IsSystem(u)) throw new ScimError(400, $"User {uid} doesn't exist.", "invalidValue");
            if (u.DeactivatedAt != null) throw new ScimError(400, $"User {uid} is deactivated and can't be added to a group.", "invalidValue");
            if (u.IsAdmin && !o.ManageAdmins) throw new ScimError(403, $"User {uid} is a ByteBench site administrator; SCIM may not change their group memberships (Scim:ManageAdmins is off).");
            db.GroupMembers.Add(new GroupMember { Group = group, UserId = uid, Role = role });
            events.Add(new MemberEvent(true, uid, u.UserName, role));
        }
        foreach (var uid in removes)
        {
            var u = users[uid];
            var r = current[uid];
            if (u.IsAdmin && !o.ManageAdmins) throw new ScimError(403, $"User {uid} is a ByteBench site administrator; SCIM may not change their group memberships (Scim:ManageAdmins is off).");
            if (r > role) throw new ScimError(403, $"User {uid} is a {r} of this group, above the role SCIM manages ({role}); change it in ByteBench.");
            events.Add(new MemberEvent(false, uid, u.UserName, r));
        }
        if (removes.Count > 0)
        {
            var rows = await db.GroupMembers.Where(m => m.GroupId == group.Id && removes.Contains(m.UserId)).ToListAsync();
            db.GroupMembers.RemoveRange(rows);
        }
        return events;
    }

    private async Task AuditMembersAsync(Group group, List<MemberEvent> events)
    {
        foreach (var e in events)
            await audit.RecordAsync(e.Added ? "scim.group_member_added" : "scim.group_member_removed", "user", e.UserId,
                details: new { group = group.FullPath, user = e.UserName, role = e.Role.ToString() }, anonymous: true, actorName: Actor);
    }

    /// <summary>A free top-level path from the displayName: "Platform Team" → <c>platform-team</c>, then <c>-2</c>, <c>-3</c>… if taken (or an old path that redirects).</summary>
    private static async Task<string> UniquePathAsync(AppDbContext db, string displayName)
    {
        var slug = GroupService.SuggestPath(displayName);
        if (slug.Length == 0 || !char.IsAsciiLetterOrDigit(slug[0])) slug = "group";
        if (slug.Length > 44) slug = slug[..44].TrimEnd('-', '.', '_');
        string[] reserved = ["-", "new", "api", "admin"];
        for (var n = 1; n < 1000; n++)
        {
            var candidate = n == 1 ? slug : $"{slug}-{n}";
            if (reserved.Contains(candidate)) continue;
            if (!await db.Groups.AnyAsync(g => g.FullPath == candidate) && !await db.GroupRedirects.AnyAsync(r => r.OldPath == candidate)) return candidate;
        }
        throw new ScimError(409, "No free group path could be derived from this displayName.", "uniqueness");
    }

    // ------------------------------------------------------------------ JSON → changes

    [GeneratedRegex("""^members\[\s*value\s+eq\s+"([^"]*)"\s*\]$""", RegexOptions.IgnoreCase)]
    private static partial Regex MemberFilterRx();

    /// <summary>A Group resource, or a path-less PATCH value object. <paramref name="membersOp"/> is what "members" in it does.</summary>
    private static void ApplyResource(JsonObject o, Changes c, string membersOp)
    {
        foreach (var (key, value) in o)
        {
            var k = key.StartsWith(ScimUserService.GroupSchema + ":", StringComparison.OrdinalIgnoreCase) ? key[(ScimUserService.GroupSchema.Length + 1)..] : key;
            switch (k.ToLowerInvariant())
            {
                case "displayname": c.DisplayName = Str(value, "displayName"); break;
                case "externalid": c.ExternalId = Str(value, "externalId"); c.ClearExternalId = c.ExternalId == null; break;
                case "members": c.Members.Add((membersOp, MemberIds(value))); break;
                default: break; // schemas, id, meta, extension schemas: not stored
            }
        }
    }

    /// <summary>The user ids in a members value: <c>[{"value":"12"}, …]</c> (or one object).</summary>
    private static List<int> MemberIds(JsonNode? value)
    {
        IEnumerable<JsonNode?> items = value switch
        {
            null => [],
            JsonArray a => a,
            JsonObject one => [one],
            _ => throw new ScimError(400, "members must be a list of {\"value\": \"<user id>\"} objects.", "invalidValue"),
        };
        var ids = new List<int>();
        foreach (var item in items)
        {
            if (item is not JsonObject m) throw new ScimError(400, "Each member must be an object with a value.", "invalidValue");
            var v = Str(m["value"], "members.value") ?? throw new ScimError(400, "A member has no value.", "invalidValue");
            if (!int.TryParse(v, out var id) || id <= 0) throw new ScimError(400, $"Unknown member '{v}'.", "invalidValue");
            ids.Add(id);
        }
        if (ids.Count > 1000) throw new ScimError(400, "Too many members in one request.", "invalidValue");
        return ids;
    }

    private static string? Str(JsonNode? v, string what)
    {
        if (v == null) return null;
        if (v is JsonValue jv && jv.TryGetValue<string>(out var s))
            return string.IsNullOrWhiteSpace(s) ? null : s.Trim().Length > 256 ? throw new ScimError(400, $"{what} is too long.", "invalidValue") : s.Trim();
        if (v is JsonValue num && num.GetValueKind() == JsonValueKind.Number) return num.ToJsonString();
        throw new ScimError(400, $"{what} must be a string.", "invalidValue");
    }
}
