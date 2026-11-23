using System.Text.Json;
using System.Text.Json.Nodes;
using ByteBench.Data;
using ByteBench.Services.Accounts;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ByteBench.Services.Scim;

/// <summary>A ByteBench account as SCIM sees it.</summary>
public sealed record ScimUserView(int Id, string UserName, string LocalUserName, string DisplayName, string? Email, bool Active, bool IsAdmin,
    string? ExternalId, DateTime? Created, DateTime? Modified);

/// <summary>
/// SCIM 2.0 Users (read and write) and Groups (read-only) on ByteBench accounts. Users map to <see cref="User"/> plus a
/// <see cref="ScimIdentity"/> row for the IdP's userName and externalId; <c>active=false</c> and DELETE deactivate the
/// account with the shared mechanism (<see cref="AccountDeactivation"/>: signed out everywhere, tokens revoked, every
/// sign-in refused). Accounts are never deleted. System bot accounts are invisible; site admins are read-only unless
/// <c>Scim:ManageAdmins</c>. Every write is audited as <c>scim.user_*</c> with the actor "SCIM".
/// <para>
/// Groups in ByteBench are project namespaces with roles, not directory groups, so SCIM Groups are read-only: each group's
/// full path and its direct members.
/// </para>
/// </summary>
public sealed class ScimUserService(IDbContextFactory<AppDbContext> dbFactory, UserManager<User> users, Sso.SsoService sso,
    Audit.AuditLog audit, IOptionsMonitor<ScimOptions> options)
{
    public const string UserSchema = "urn:ietf:params:scim:schemas:core:2.0:User";
    public const string GroupSchema = "urn:ietf:params:scim:schemas:core:2.0:Group";
    public const string ListSchema = "urn:ietf:params:scim:api:messages:2.0:ListResponse";
    public const string PatchSchema = "urn:ietf:params:scim:api:messages:2.0:PatchOp";
    public const string Actor = "SCIM";

    public static readonly IReadOnlySet<string> UserFilterAttributes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "userName", "emails.value", "emails", "externalId", "id", "displayName", "active", "name.formatted" };
    public static readonly IReadOnlySet<string> GroupFilterAttributes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "displayName", "id", "externalId" };

    // ------------------------------------------------------------------ reading

    private async Task<List<ScimUserView>> LoadAsync(int? id = null)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var q = db.Users.AsNoTracking().AsQueryable();
        if (id != null) q = q.Where(u => u.Id == id);
        var rows = await q.Select(u => new
        {
            u.Id, u.UserName, u.DisplayName, u.Email, u.DeactivatedAt, u.IsAdmin,
            Scim = db.ScimIdentities.Where(s => s.UserId == u.Id).Select(s => new { s.UserName, s.ExternalId, s.CreatedAt, s.UpdatedAt }).FirstOrDefault(),
        }).ToListAsync();
        return rows.Where(r => !SystemAccounts.IsSystem(r.UserName)).OrderBy(r => r.Id)
            .Select(r => new ScimUserView(r.Id, r.Scim?.UserName ?? r.UserName ?? "", r.UserName ?? "", r.DisplayName, r.Email, r.DeactivatedAt == null, r.IsAdmin,
                r.Scim?.ExternalId, r.Scim?.CreatedAt, r.Scim?.UpdatedAt)).ToList();
    }

    public async Task<ScimUserView?> GetAsync(int id) => (await LoadAsync(id)).FirstOrDefault();

    public async Task<(List<ScimUserView> Page, int Total)> ListAsync(string? filter, int startIndex, int count)
    {
        var all = await LoadAsync();
        if (!string.IsNullOrWhiteSpace(filter))
        {
            var comparisons = ScimFilter.Parse(filter, UserFilterAttributes);
            all = all.Where(u => comparisons.All(c => ScimFilter.Matches(c, UserValues(u, c.Attribute)))).ToList();
        }
        return (all.Skip(startIndex - 1).Take(count).ToList(), all.Count);
    }

    /// <summary>
    /// An account that SCIM never touched answers to its ByteBench username and its email as userName, so an IdP can find
    /// and adopt existing accounts before it creates new ones.
    /// </summary>
    private static IEnumerable<string?> UserValues(ScimUserView u, string attribute) => attribute switch
    {
        "userName" => u.ExternalId == null && u.Created == null ? [u.UserName, u.Email] : [u.UserName],
        "emails.value" or "emails" => [u.Email],
        "externalId" => [u.ExternalId],
        "id" => [u.Id.ToString()],
        "displayName" or "name.formatted" => [u.DisplayName],
        "active" => [u.Active ? "true" : "false"],
        _ => [],
    };

    public static JsonObject ToJson(ScimUserView u, string baseUrl)
    {
        var o = new JsonObject
        {
            ["schemas"] = new JsonArray(UserSchema),
            ["id"] = u.Id.ToString(),
            ["userName"] = u.UserName,
            ["displayName"] = u.DisplayName,
            ["name"] = new JsonObject { ["formatted"] = u.DisplayName },
            ["active"] = u.Active,
        };
        if (u.ExternalId != null) o["externalId"] = u.ExternalId;
        if (u.Email != null) o["emails"] = new JsonArray(new JsonObject { ["value"] = u.Email, ["type"] = "work", ["primary"] = true });
        var meta = new JsonObject { ["resourceType"] = "User", ["location"] = $"{baseUrl}/Users/{u.Id}" };
        if (u.Created is { } c) meta["created"] = DateTime.SpecifyKind(c, DateTimeKind.Utc).ToString("O");
        if (u.Modified is { } m) meta["lastModified"] = DateTime.SpecifyKind(m, DateTimeKind.Utc).ToString("O");
        o["meta"] = meta;
        return o;
    }

    // ------------------------------------------------------------------ writing

    /// <summary>The attributes a create, replace or patch sets. Null: not given.</summary>
    private sealed class Changes
    {
        public string? UserName, DisplayName, Email, ExternalId, GivenName, FamilyName;
        public bool? Active;
        public bool ClearExternalId;
    }

    public async Task<(ScimUserView User, bool Created)> CreateAsync(JsonObject body)
    {
        var c = new Changes();
        ApplyResource(body, c);
        if (string.IsNullOrWhiteSpace(c.UserName)) throw new ScimError(400, "userName is required.", "invalidValue");
        var email = c.Email ?? (c.UserName.Contains('@') ? c.UserName : null);
        if (string.IsNullOrWhiteSpace(email)) throw new ScimError(400, "An email address (emails[].value, or a userName that is an email) is required.", "invalidValue");
        await EnsureUserNameFreeAsync(c.UserName, null);

        // Adopt an existing account with this email that SCIM doesn't manage yet (never an admin unless allowed).
        var existing = await users.FindByEmailAsync(email);
        if (existing != null)
        {
            await using var db0 = await dbFactory.CreateDbContextAsync();
            if (SystemAccounts.IsSystem(existing) || await db0.ScimIdentities.AnyAsync(s => s.UserId == existing.Id))
                throw new ScimError(409, "A user with this email already exists.", "uniqueness");
            RequireMayChange(existing);
            await ApplyAsync(existing, c);
            await audit.RecordAsync("scim.user_updated", "user", existing.Id, details: new { user = existing.UserName, adopted = true }, anonymous: true, actorName: Actor);
            return ((await GetAsync(existing.Id))!, true);
        }

        var display = DisplayOf(c) ?? c.UserName;
        var user = new User
        {
            UserName = await sso.UniqueUserNameAsync(c.UserName, email),
            Email = email.Trim(),
            EmailConfirmed = true, // the IdP manages the address
            DisplayName = display.Length > 80 ? display[..80] : display,
            Color = Components.Account.AvatarPalette.Random(),
            LockoutEnabled = true,
        };
        var r = await users.CreateAsync(user); // no password: sign-in through SSO; git over HTTP uses access tokens
        if (!r.Succeeded) throw new ScimError(400, string.Join(" ", r.Errors.Select(e => e.Description)), "invalidValue");
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            db.ScimIdentities.Add(new ScimIdentity { UserId = user.Id, UserName = c.UserName.Trim(), NormalizedUserName = c.UserName.Trim().ToUpperInvariant(), ExternalId = c.ExternalId });
            await db.SaveChangesAsync();
        }
        await audit.RecordAsync("scim.user_created", "user", user.Id, details: new { user = user.UserName, scimUserName = c.UserName }, anonymous: true, actorName: Actor);
        if (c.Active == false)
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            await AccountDeactivation.DeactivateAsync(users, db, user);
            await audit.RecordAsync("scim.user_deactivated", "user", user.Id, details: new { user = user.UserName }, anonymous: true, actorName: Actor);
        }
        return ((await GetAsync(user.Id))!, true);
    }

    public async Task<ScimUserView> ReplaceAsync(int id, JsonObject body)
    {
        var user = await FindAsync(id);
        var c = new Changes();
        ApplyResource(body, c);
        if (string.IsNullOrWhiteSpace(c.UserName)) throw new ScimError(400, "userName is required.", "invalidValue");
        if (c.ExternalId == null) c.ClearExternalId = true;
        c.Active ??= true; // PUT replaces: a missing "active" means the default (true)
        await ApplyAsync(user, c);
        return (await GetAsync(id))!;
    }

    public async Task<ScimUserView> PatchAsync(int id, JsonObject body)
    {
        var user = await FindAsync(id);
        if (body["Operations"] is not JsonArray ops || ops.Count == 0) throw new ScimError(400, "The PATCH request has no Operations.", "invalidSyntax");
        if (ops.Count > 100) throw new ScimError(400, "Too many operations.", "invalidSyntax");
        var c = new Changes();
        foreach (var node in ops)
        {
            if (node is not JsonObject op) throw new ScimError(400, "Each operation must be an object.", "invalidSyntax");
            var name = (op["op"] as JsonValue)?.GetValue<object>()?.ToString()?.ToLowerInvariant();
            var path = (op["path"] as JsonValue)?.GetValue<object>()?.ToString();
            var value = op["value"];
            switch (name)
            {
                case "add" or "replace":
                    if (string.IsNullOrWhiteSpace(path))
                    {
                        if (value is not JsonObject obj) throw new ScimError(400, "An operation without a path needs an object value.", "invalidValue");
                        ApplyResource(obj, c);
                    }
                    else ApplyPath(path, value, c);
                    break;
                case "remove":
                    if (string.IsNullOrWhiteSpace(path)) throw new ScimError(400, "remove needs a path.", "noTarget");
                    RemovePath(path, c);
                    break;
                default:
                    throw new ScimError(400, $"Unknown PATCH op '{name}'.", "invalidSyntax");
            }
        }
        await ApplyAsync(user, c);
        return (await GetAsync(id))!;
    }

    /// <summary>DELETE: deactivates (never deletes) the account.</summary>
    public async Task DeactivateAsync(int id)
    {
        var user = await FindAsync(id);
        await ApplyAsync(user, new Changes { Active = false });
    }

    private async Task<User> FindAsync(int id)
    {
        var user = await users.FindByIdAsync(id.ToString());
        if (user == null || SystemAccounts.IsSystem(user)) throw new ScimError(404, $"User {id} not found.");
        RequireMayChange(user);
        return user;
    }

    private void RequireMayChange(User user)
    {
        if (user.IsAdmin && !options.CurrentValue.ManageAdmins)
            throw new ScimError(403, "This account is a ByteBench site administrator; SCIM may not change it (Scim:ManageAdmins is off).");
    }

    private async Task EnsureUserNameFreeAsync(string scimUserName, int? userId)
    {
        var norm = scimUserName.Trim().ToUpperInvariant();
        await using var db = await dbFactory.CreateDbContextAsync();
        if (await db.ScimIdentities.AnyAsync(s => s.NormalizedUserName == norm && s.UserId != userId))
            throw new ScimError(409, "A user with this userName already exists.", "uniqueness");
    }

    private async Task ApplyAsync(User user, Changes c)
    {
        var changed = new List<string>();
        await using var db = await dbFactory.CreateDbContextAsync();
        var identity = await db.ScimIdentities.FirstOrDefaultAsync(s => s.UserId == user.Id);
        if (identity == null)
        {
            var name = c.UserName?.Trim() ?? user.UserName!;
            await EnsureUserNameFreeAsync(name, user.Id);
            identity = new ScimIdentity { UserId = user.Id, UserName = name, NormalizedUserName = name.ToUpperInvariant() };
            db.ScimIdentities.Add(identity);
        }
        if (c.UserName is { } un && un.Trim() != identity.UserName)
        {
            await EnsureUserNameFreeAsync(un, user.Id);
            identity.UserName = un.Trim();
            identity.NormalizedUserName = identity.UserName.ToUpperInvariant();
            changed.Add("userName");
        }
        if (c.ExternalId != null && c.ExternalId != identity.ExternalId) { identity.ExternalId = c.ExternalId; changed.Add("externalId"); }
        else if (c.ClearExternalId && identity.ExternalId != null) { identity.ExternalId = null; changed.Add("externalId"); }
        identity.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        var profileChanged = false;
        if (DisplayOf(c) is { } display && display != user.DisplayName)
        {
            user.DisplayName = display.Length > 80 ? display[..80] : display;
            profileChanged = true;
            changed.Add("displayName");
        }
        if (c.Email is { } email && !string.Equals(email.Trim(), user.Email, StringComparison.OrdinalIgnoreCase))
        {
            if (await users.FindByEmailAsync(email.Trim()) is { } other && other.Id != user.Id) throw new ScimError(409, "Another user has this email.", "uniqueness");
            var r = await users.SetEmailAsync(user, email.Trim());
            if (!r.Succeeded) throw new ScimError(400, string.Join(" ", r.Errors.Select(e => e.Description)), "invalidValue");
            user.EmailConfirmed = true; // the IdP manages the address
            profileChanged = true;
            changed.Add("email");
        }
        if (profileChanged)
        {
            var r = await users.UpdateAsync(user);
            if (!r.Succeeded) throw new ScimError(400, string.Join(" ", r.Errors.Select(e => e.Description)), "invalidValue");
        }
        if (changed.Count > 0)
            await audit.RecordAsync("scim.user_updated", "user", user.Id, details: new { user = user.UserName, changed }, anonymous: true, actorName: Actor);

        if (c.Active == false && user.DeactivatedAt == null)
        {
            var revoked = await AccountDeactivation.DeactivateAsync(users, db, user);
            await audit.RecordAsync("scim.user_deactivated", "user", user.Id, details: new { user = user.UserName, tokensRevoked = revoked }, anonymous: true, actorName: Actor);
        }
        else if (c.Active == true && user.DeactivatedAt != null)
        {
            await AccountDeactivation.ReactivateAsync(users, user);
            await audit.RecordAsync("scim.user_reactivated", "user", user.Id, details: new { user = user.UserName }, anonymous: true, actorName: Actor);
        }
    }

    private static string? DisplayOf(Changes c)
    {
        if (!string.IsNullOrWhiteSpace(c.DisplayName)) return c.DisplayName.Trim();
        var full = string.Join(' ', new[] { c.GivenName, c.FamilyName }.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!.Trim()));
        return full.Length > 0 ? full : null;
    }

    // ------------------------------------------------------------------ JSON → changes

    /// <summary>A User resource (or a path-less PATCH value object, as Okta and Entra send).</summary>
    private static void ApplyResource(JsonObject o, Changes c)
    {
        foreach (var (key, value) in o)
        {
            if (key.StartsWith(UserSchema + ":", StringComparison.OrdinalIgnoreCase)) ApplyPath(key[(UserSchema.Length + 1)..], value, c);
            else if (key.StartsWith("urn:", StringComparison.OrdinalIgnoreCase)) continue; // extension schemas: not stored
            else if (key is "schemas" or "id" or "meta" or "groups" or "password") continue;
            else ApplyPath(key, value, c);
        }
    }

    private static void ApplyPath(string path, JsonNode? value, Changes c)
    {
        var p = path.Trim();
        if (p.StartsWith(UserSchema + ":", StringComparison.OrdinalIgnoreCase)) p = p[(UserSchema.Length + 1)..];
        var lower = p.ToLowerInvariant();
        switch (lower)
        {
            case "username": c.UserName = Str(value, p) ?? throw new ScimError(400, "userName can't be empty.", "invalidValue"); break;
            case "displayname": c.DisplayName = Str(value, p); break;
            case "externalid": c.ExternalId = Str(value, p); if (c.ExternalId == null) c.ClearExternalId = true; break;
            case "active": c.Active = Bool(value); break;
            case "name.formatted": c.DisplayName ??= Str(value, p); break;
            case "name.givenname": c.GivenName = Str(value, p); break;
            case "name.familyname": c.FamilyName = Str(value, p); break;
            case "name":
                if (value is JsonObject name)
                    foreach (var (k, v) in name) ApplyPath("name." + k, v, c);
                break;
            case "emails":
                c.Email = PrimaryEmail(value) ?? c.Email;
                break;
            default:
                // emails[type eq "work"].value, emails[primary eq true].value, emails.value
                if (lower.StartsWith("emails[", StringComparison.Ordinal) && lower.EndsWith("].value", StringComparison.Ordinal) || lower == "emails.value")
                    c.Email = Str(value, p);
                // Anything else (title, phone numbers, addresses, enterprise extension…) isn't stored by ByteBench: ignored.
                break;
        }
    }

    private static void RemovePath(string path, Changes c)
    {
        switch (path.Trim().ToLowerInvariant())
        {
            case "externalid": c.ClearExternalId = true; break;
            case "username" or "active" or "emails" or "emails.value":
                throw new ScimError(400, $"'{path}' can't be removed.", "mutability");
            default: break; // display name and name parts: ByteBench keeps the current display name
        }
    }

    private static string? PrimaryEmail(JsonNode? value)
    {
        if (value is JsonArray arr)
        {
            var emails = arr.OfType<JsonObject>().ToList();
            var pick = emails.FirstOrDefault(e => Bool(e["primary"]) == true) ?? emails.FirstOrDefault(e => (Str(e["type"], "type") ?? "") == "work") ?? emails.FirstOrDefault();
            return pick == null ? null : Str(pick["value"], "emails.value");
        }
        if (value is JsonObject one) return Str(one["value"], "emails.value");
        return null;
    }

    private static string? Str(JsonNode? v, string what)
    {
        if (v == null) return null;
        if (v is JsonValue jv && jv.TryGetValue<string>(out var s)) return string.IsNullOrWhiteSpace(s) ? null : s.Trim().Length > 256 ? throw new ScimError(400, $"{what} is too long.", "invalidValue") : s.Trim();
        if (v is JsonValue num && num.GetValueKind() is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False) return num.ToJsonString();
        throw new ScimError(400, $"{what} must be a string.", "invalidValue");
    }

    /// <summary>JSON booleans, and the "True"/"False" strings Entra ID sends.</summary>
    private static bool? Bool(JsonNode? v)
    {
        if (v == null) return null;
        if (v is JsonValue jv)
        {
            if (jv.GetValueKind() == JsonValueKind.True) return true;
            if (jv.GetValueKind() == JsonValueKind.False) return false;
            if (jv.TryGetValue<string>(out var s) && bool.TryParse(s.Trim(), out var b)) return b;
        }
        throw new ScimError(400, "Expected a boolean.", "invalidValue");
    }

    // ------------------------------------------------------------------ groups (read-only)

    public async Task<(List<JsonObject> Page, int Total)> ListGroupsAsync(string? filter, int startIndex, int count, string baseUrl)
    {
        var all = await LoadGroupsAsync(null, baseUrl);
        if (!string.IsNullOrWhiteSpace(filter))
        {
            var comparisons = ScimFilter.Parse(filter, GroupFilterAttributes);
            all = all.Where(g => comparisons.All(c => ScimFilter.Matches(c, c.Attribute switch
            {
                "displayName" => [g["displayName"]!.GetValue<string>()],
                "id" => [g["id"]!.GetValue<string>()],
                _ => Array.Empty<string?>(),
            }))).ToList();
        }
        return (all.Skip(startIndex - 1).Take(count).ToList(), all.Count);
    }

    public async Task<JsonObject?> GetGroupAsync(int id, string baseUrl) => (await LoadGroupsAsync(id, baseUrl)).FirstOrDefault();

    private async Task<List<JsonObject>> LoadGroupsAsync(int? id, string baseUrl)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var q = db.Groups.AsNoTracking().AsQueryable();
        if (id != null) q = q.Where(g => g.Id == id);
        var groups = await q.OrderBy(g => g.Id).Select(g => new
        {
            g.Id, g.FullPath, g.Name,
            Members = g.Members.Select(m => new { m.UserId, m.User.UserName }).ToList(),
        }).ToListAsync();
        return groups.Select(g => new JsonObject
        {
            ["schemas"] = new JsonArray(GroupSchema),
            ["id"] = g.Id.ToString(),
            ["displayName"] = g.FullPath,
            ["members"] = new JsonArray(g.Members.Where(m => !SystemAccounts.IsSystem(m.UserName)).Select(m => (JsonNode)new JsonObject
            {
                ["value"] = m.UserId.ToString(), ["display"] = m.UserName, ["$ref"] = $"{baseUrl}/Users/{m.UserId}", ["type"] = "User",
            }).ToArray()),
            ["meta"] = new JsonObject { ["resourceType"] = "Group", ["location"] = $"{baseUrl}/Groups/{g.Id}" },
        }).ToList();
    }
}
