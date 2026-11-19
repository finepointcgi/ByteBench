using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using ByteBench.Api;
using ByteBench.Data;
using ByteBench.Services.Audit;
using ByteBench.Services.Ci;
using ByteBench.Services.Webhooks;
using Microsoft.EntityFrameworkCore;

namespace ByteBench.Services.Flags;

/// <summary>A flag that nobody changed or evaluated for a while.</summary>
public sealed record FlagStaleness(FeatureFlag Flag, DateTime LastChanged, DateTime? LastEvaluated, int IdleDays);

/// <summary>
/// Wakes live-update streams (SSE) when an environment's flags change (singleton). Each subscriber holds a one-slot
/// channel: a burst of changes becomes one wake-up, and the reader re-reads the environment's current version.
/// </summary>
public sealed class FlagFeed
{
    private readonly ConcurrentDictionary<int, ConcurrentDictionary<Guid, Channel<long>>> subscribers = new();

    public IDisposable Subscribe(int environmentId, out ChannelReader<long> reader)
    {
        var id = Guid.NewGuid();
        var channel = Channel.CreateBounded<long>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest });
        subscribers.GetOrAdd(environmentId, _ => new()).TryAdd(id, channel);
        reader = channel.Reader;
        return new Subscription(() =>
        {
            if (subscribers.TryGetValue(environmentId, out var map)) map.TryRemove(id, out _);
            channel.Writer.TryComplete();
        });
    }

    public void Publish(int environmentId, long version)
    {
        if (subscribers.TryGetValue(environmentId, out var map))
            foreach (var c in map.Values) c.Writer.TryWrite(version);
    }

    private sealed class Subscription(Action dispose) : IDisposable
    {
        private int disposed;
        public void Dispose() { if (Interlocked.Exchange(ref disposed, 1) == 0) dispose(); }
    }
}

/// <summary>
/// Feature flags for the UI and the management API (scoped). Reading needs ViewProject; toggling, editing values, rules and
/// segments needs <see cref="Permission.ToggleFlags"/> (Developer+); creating, deleting and archiving flags and managing
/// environments and SDK keys needs <see cref="Permission.ManageFlags"/> (Maintainer+). Every change is written to the
/// flag's history, the audit log, and raised as the <c>flag.changed</c> webhook.
/// </summary>
public partial class FlagService(IDbContextFactory<AppDbContext> dbFactory, ProjectAccess access, AuditLog audit,
    WebhookDispatcher webhooks, FlagFeed feed, PublicBaseUrl baseUrl)
{
    public const int DefaultStaleDays = 30;

    private static readonly Regex KeyPattern = new("^[a-z0-9][a-z0-9._-]{0,63}$", RegexOptions.Compiled);

    public static bool IsValidKey(string key) => KeyPattern.IsMatch(key);

    // ----- Environments -----

    /// <summary>The project's flag environments by name; environments of the project's CI pipelines are added on the way.</summary>
    public async Task<List<FlagEnvironment>> ListEnvironmentsAsync(int projectId)
    {
        if (!await access.CanAsync(projectId, Permission.ViewProject)) return [];
        await using var db = await dbFactory.CreateDbContextAsync();
        await SyncCiEnvironmentsAsync(db, projectId);
        return await db.FlagEnvironments.AsNoTracking().Where(e => e.ProjectId == projectId).OrderBy(e => e.Name).ToListAsync();
    }

    private static async Task SyncCiEnvironmentsAsync(AppDbContext db, int projectId)
    {
        var ci = await db.CiEnvironments.AsNoTracking().Where(e => db.Repositories.Any(r => r.Id == e.RepositoryId && r.ProjectId == projectId))
            .Select(e => e.Name).Distinct().ToListAsync();
        if (ci.Count == 0) return;
        var have = await db.FlagEnvironments.Where(e => e.ProjectId == projectId).Select(e => e.Name).ToListAsync();
        var missing = ci.Where(n => !have.Contains(n, StringComparer.OrdinalIgnoreCase) && n.Length <= 64).ToList();
        if (missing.Count == 0) return;
        try
        {
            foreach (var name in missing) await AddEnvironmentAsync(db, projectId, name);
            await db.SaveChangesAsync();
        }
        catch (DbUpdateException) { db.ChangeTracker.Clear(); } // another request added it first
    }

    private static async Task<FlagEnvironment> AddEnvironmentAsync(AppDbContext db, int projectId, string name)
    {
        var env = new FlagEnvironment { ProjectId = projectId, Name = name };
        db.FlagEnvironments.Add(env);
        // A new environment starts every flag off, with the default value the flag was created with (its oldest state's).
        var flags = await db.FeatureFlags.Include(f => f.States).Where(f => f.ProjectId == projectId).ToListAsync();
        foreach (var f in flags)
            db.FlagEnvironmentStates.Add(new FlagEnvironmentState
            {
                Flag = f, Environment = env, Enabled = false,
                DefaultValue = f.States.OrderBy(x => x.Id).Select(x => x.DefaultValue).FirstOrDefault() ?? FlagEvaluator.DefaultValueText(f.Type, true),
            });
        return env;
    }

    /// <summary>Adds an environment (Maintainer+); every flag starts off in it.</summary>
    public async Task<FlagEnvironment> CreateEnvironmentAsync(int projectId, string name)
    {
        await access.RequireAsync(projectId, Permission.ManageFlags);
        name = name.Trim();
        if (!CiFile.IsValidEnvironmentName(name))
            throw new InvalidOperationException("Use letters, digits, spaces, '_', '.', '/' or '-' (up to 64 characters), starting with a letter or digit.");
        await using var db = await dbFactory.CreateDbContextAsync();
        if (await db.FlagEnvironments.AnyAsync(e => e.ProjectId == projectId && e.Name == name))
            throw new InvalidOperationException($"The environment '{name}' already exists.");
        var env = await AddEnvironmentAsync(db, projectId, name);
        await db.SaveChangesAsync();
        await audit.RecordAsync("flag.environment_created", "flag_environment", env.Id, projectId, new { name });
        return env;
    }

    /// <summary>Removes an environment with its flag states and SDK keys (Maintainer+).</summary>
    public async Task DeleteEnvironmentAsync(int projectId, int environmentId)
    {
        await access.RequireAsync(projectId, Permission.ManageFlags);
        await using var db = await dbFactory.CreateDbContextAsync();
        var env = await db.FlagEnvironments.FirstOrDefaultAsync(e => e.Id == environmentId && e.ProjectId == projectId)
            ?? throw new InvalidOperationException("That environment no longer exists.");
        db.FlagEnvironments.Remove(env);
        await db.SaveChangesAsync();
        await audit.RecordAsync("flag.environment_deleted", "flag_environment", env.Id, projectId, new { name = env.Name });
    }

    // ----- Flags: reading -----

    public async Task<List<FeatureFlag>> ListFlagsAsync(int projectId, bool includeArchived = false)
    {
        if (!await access.CanAsync(projectId, Permission.ViewProject)) return [];
        await using var db = await dbFactory.CreateDbContextAsync();
        await SyncCiEnvironmentsAsync(db, projectId);
        var q = db.FeatureFlags.AsNoTracking().Include(f => f.States).ThenInclude(s => s.Environment).Include(f => f.CreatedBy)
            .Where(f => f.ProjectId == projectId);
        if (!includeArchived) q = q.Where(f => !f.IsArchived);
        return await q.OrderBy(f => f.Key).AsSplitQuery().ToListAsync();
    }

    public async Task<FeatureFlag?> GetFlagAsync(int projectId, string key)
    {
        if (!await access.CanAsync(projectId, Permission.ViewProject)) return null;
        await using var db = await dbFactory.CreateDbContextAsync();
        await SyncCiEnvironmentsAsync(db, projectId);
        return await db.FeatureFlags.AsNoTracking().Include(f => f.States).ThenInclude(s => s.Environment).Include(f => f.CreatedBy)
            .AsSplitQuery().FirstOrDefaultAsync(f => f.ProjectId == projectId && f.Key == key.ToLowerInvariant());
    }

    /// <summary>The flag's changes, newest first, with the total.</summary>
    public async Task<(List<FlagChange> Changes, int Total)> HistoryAsync(int projectId, string key, int take = 50, int skip = 0)
    {
        if (!await access.CanAsync(projectId, Permission.ViewProject)) return ([], 0);
        await using var db = await dbFactory.CreateDbContextAsync();
        var flagId = await db.FeatureFlags.Where(f => f.ProjectId == projectId && f.Key == key.ToLowerInvariant()).Select(f => (int?)f.Id).FirstOrDefaultAsync();
        if (flagId == null) return ([], 0);
        var q = db.FlagChanges.AsNoTracking().Where(c => c.FlagId == flagId);
        var total = await q.CountAsync();
        return (await q.OrderByDescending(c => c.At).ThenByDescending(c => c.Id).Skip(skip).Take(Math.Clamp(take, 1, 200)).ToListAsync(), total);
    }

    /// <summary>
    /// Active flags that were neither changed (the definition or any environment) nor evaluated by an SDK for
    /// <paramref name="days"/> days, oldest activity first.
    /// </summary>
    public async Task<List<FlagStaleness>> StaleFlagsAsync(int projectId, int days = DefaultStaleDays)
    {
        var flags = await ListFlagsAsync(projectId);
        return flags.Select(f => Staleness(f, DateTime.UtcNow, days)).Where(s => s != null).Select(s => s!).OrderByDescending(s => s.IdleDays).ToList();
    }

    public static FlagStaleness? Staleness(FeatureFlag f, DateTime now, int days)
    {
        if (f.IsArchived) return null;
        var changed = new[] { f.UpdatedAt, f.CreatedAt }.Concat(f.States.Select(s => s.UpdatedAt)).Max();
        var evaluated = f.States.Where(s => s.LastEvaluatedAt != null).Select(s => s.LastEvaluatedAt).DefaultIfEmpty().Max();
        var last = evaluated is { } e && e > changed ? e : changed;
        var idle = (int)(now - last).TotalDays;
        return idle >= days ? new FlagStaleness(f, changed, evaluated, idle) : null;
    }

    public async Task<List<FlagSegment>> ListSegmentsAsync(int projectId)
    {
        if (!await access.CanAsync(projectId, Permission.ViewProject)) return [];
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.FlagSegments.AsNoTracking().Where(s => s.ProjectId == projectId).OrderBy(s => s.Key).ToListAsync();
    }

    // ----- Flags: writing -----

    /// <summary>Creates a flag (Maintainer+), off in every environment. <paramref name="defaultValue"/> is what environments serve when turned on.</summary>
    public async Task<FeatureFlag> CreateFlagAsync(int projectId, string key, string name, string? description, FlagType type,
        System.Text.Json.Nodes.JsonNode? defaultValue = null, System.Text.Json.Nodes.JsonNode? offValue = null)
    {
        await access.RequireAsync(projectId, Permission.ManageFlags);
        key = key.Trim().ToLowerInvariant();
        name = name.Trim();
        if (!IsValidKey(key)) throw new InvalidOperationException("The key may contain lowercase letters, digits, '.', '_' and '-' (up to 64), starting with a letter or digit.");
        if (name.Length is 0 or > 120) throw new InvalidOperationException("The name is required (up to 120 characters).");
        if (description?.Length > 2000) throw new InvalidOperationException("The description can be at most 2000 characters.");
        var on = defaultValue == null ? FlagEvaluator.DefaultValueText(type, true) : FlagEvaluator.NormalizeValue(type, defaultValue);
        var off = offValue == null ? FlagEvaluator.DefaultValueText(type, false) : FlagEvaluator.NormalizeValue(type, offValue);
        var actor = await access.ResolveUserIdAsync();

        await using var db = await dbFactory.CreateDbContextAsync();
        if (await db.FeatureFlags.AnyAsync(f => f.ProjectId == projectId && f.Key == key))
            throw new InvalidOperationException($"A flag with the key '{key}' already exists.");
        await SyncCiEnvironmentsAsync(db, projectId);
        var flag = new FeatureFlag
        {
            ProjectId = projectId, Key = key, Name = name, Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim(),
            Type = type, OffValue = off, CreatedById = actor,
        };
        db.FeatureFlags.Add(flag);
        var envs = await db.FlagEnvironments.Where(e => e.ProjectId == projectId).ToListAsync();
        foreach (var e in envs)
            flag.States.Add(new FlagEnvironmentState { Environment = e, Enabled = false, DefaultValue = on, UpdatedById = actor });
        await db.SaveChangesAsync();
        await RecordAsync(db, flag, null, "created", null, $"{type}", "flag.created", actor);
        return flag;
    }

    /// <summary>Changes name, description and linked issue numbers (Developer+).</summary>
    public async Task UpdateDetailsAsync(int projectId, string key, string name, string? description, IEnumerable<int>? issueNumbers)
    {
        await access.RequireAsync(projectId, Permission.ToggleFlags);
        name = name.Trim();
        if (name.Length is 0 or > 120) throw new InvalidOperationException("The name is required (up to 120 characters).");
        if (description?.Length > 2000) throw new InvalidOperationException("The description can be at most 2000 characters.");
        await using var db = await dbFactory.CreateDbContextAsync();
        var flag = await RequireFlagAsync(db, projectId, key);
        var numbers = (issueNumbers ?? []).Distinct().Where(n => n > 0).Order().ToList();
        if (numbers.Count > 0)
        {
            var found = await db.Issues.Where(i => i.ProjectId == projectId && numbers.Contains(i.Number)).Select(i => i.Number).ToListAsync();
            var missing = numbers.Except(found).ToList();
            if (missing.Count > 0) throw new InvalidOperationException($"No such issue in this project: {string.Join(", ", missing)}.");
        }
        var issues = numbers.Count == 0 ? null : string.Join(",", numbers);
        var desc = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        var actor = await access.ResolveUserIdAsync();
        var changes = new List<(string Kind, string? Old, string? New)>();
        if (flag.Name != name) changes.Add(("details", $"name: {flag.Name}", $"name: {name}"));
        if (flag.Description != desc) changes.Add(("details", $"description: {flag.Description}", $"description: {desc}"));
        if (flag.IssueNumbers != issues) changes.Add(("issues", flag.IssueNumbers, issues));
        if (changes.Count == 0) return;
        flag.Name = name;
        flag.Description = desc;
        flag.IssueNumbers = issues;
        flag.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        foreach (var c in changes) await RecordAsync(db, flag, null, c.Kind, c.Old, c.New, "flag.updated", actor, bump: false);
    }

    /// <summary>Turns the flag on or off in an environment (Developer+).</summary>
    public async Task SetEnabledAsync(int projectId, string key, int environmentId, bool enabled)
    {
        await access.RequireAsync(projectId, Permission.ToggleFlags);
        await using var db = await dbFactory.CreateDbContextAsync();
        var (flag, state) = await RequireStateAsync(db, projectId, key, environmentId);
        if (flag.IsArchived) throw new InvalidOperationException("An archived flag can't be changed. Restore it first.");
        if (state.Enabled == enabled) return;
        var actor = await access.ResolveUserIdAsync();
        state.Enabled = enabled;
        Touch(flag, state, actor);
        await db.SaveChangesAsync();
        await RecordAsync(db, flag, state.Environment, "toggled", enabled ? "off" : "on", enabled ? "on" : "off", "flag.toggled", actor);
    }

    /// <summary>The value an environment serves when the flag is on and no rule matches (Developer+).</summary>
    public async Task SetDefaultValueAsync(int projectId, string key, int environmentId, System.Text.Json.Nodes.JsonNode? value)
    {
        await access.RequireAsync(projectId, Permission.ToggleFlags);
        await using var db = await dbFactory.CreateDbContextAsync();
        var (flag, state) = await RequireStateAsync(db, projectId, key, environmentId);
        if (flag.IsArchived) throw new InvalidOperationException("An archived flag can't be changed. Restore it first.");
        var text = FlagEvaluator.NormalizeValue(flag.Type, value);
        if (text == state.DefaultValue) return;
        var actor = await access.ResolveUserIdAsync();
        var old = state.DefaultValue;
        state.DefaultValue = text;
        Touch(flag, state, actor);
        await db.SaveChangesAsync();
        await RecordAsync(db, flag, state.Environment, "default_value", old, text, "flag.default_changed", actor);
    }

    /// <summary>The value served while the flag is off, in every environment (Developer+).</summary>
    public async Task SetOffValueAsync(int projectId, string key, System.Text.Json.Nodes.JsonNode? value)
    {
        await access.RequireAsync(projectId, Permission.ToggleFlags);
        await using var db = await dbFactory.CreateDbContextAsync();
        var flag = await RequireFlagAsync(db, projectId, key);
        if (flag.IsArchived) throw new InvalidOperationException("An archived flag can't be changed. Restore it first.");
        var text = FlagEvaluator.NormalizeValue(flag.Type, value);
        if (text == flag.OffValue) return;
        var actor = await access.ResolveUserIdAsync();
        var old = flag.OffValue;
        flag.OffValue = text;
        flag.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        await RecordAsync(db, flag, null, "off_value", old, text, "flag.off_value_changed", actor);
    }

    /// <summary>Replaces an environment's targeting rules (Developer+). Rules are checked in order; the first match wins.</summary>
    public async Task<List<FlagRule>> SetRulesAsync(int projectId, string key, int environmentId, IEnumerable<FlagRule> rules)
    {
        await access.RequireAsync(projectId, Permission.ToggleFlags);
        await using var db = await dbFactory.CreateDbContextAsync();
        var (flag, state) = await RequireStateAsync(db, projectId, key, environmentId);
        if (flag.IsArchived) throw new InvalidOperationException("An archived flag can't be changed. Restore it first.");
        var valid = FlagEvaluator.ValidateRules(flag.Type, rules);
        var segmentKeys = valid.SelectMany(r => r.Clauses).Where(c => c.Op is "inSegment" or "notInSegment").SelectMany(c => c.Values).Distinct().ToList();
        if (segmentKeys.Count > 0)
        {
            var known = await db.FlagSegments.Where(s => s.ProjectId == projectId && segmentKeys.Contains(s.Key)).Select(s => s.Key).ToListAsync();
            var unknown = segmentKeys.Except(known).ToList();
            if (unknown.Count > 0) throw new InvalidOperationException($"Unknown segment: {string.Join(", ", unknown)}.");
        }
        var text = FlagEvaluator.SerializeRules(valid);
        if (text == state.RulesJson) return valid;
        var actor = await access.ResolveUserIdAsync();
        var old = state.RulesJson;
        state.RulesJson = text;
        Touch(flag, state, actor);
        await db.SaveChangesAsync();
        await RecordAsync(db, flag, state.Environment, "rules", old, text, "flag.rules_changed", actor);
        return valid;
    }

    public async Task ArchiveAsync(int projectId, string key, bool archive)
    {
        await access.RequireAsync(projectId, Permission.ManageFlags);
        await using var db = await dbFactory.CreateDbContextAsync();
        var flag = await RequireFlagAsync(db, projectId, key);
        if (flag.IsArchived == archive) return;
        var actor = await access.ResolveUserIdAsync();
        flag.IsArchived = archive;
        flag.ArchivedAt = archive ? DateTime.UtcNow : null;
        flag.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        await RecordAsync(db, flag, null, archive ? "archived" : "unarchived", null, null, archive ? "flag.archived" : "flag.unarchived", actor);
    }

    /// <summary>Deletes a flag, its states and its history (Maintainer+). The audit log keeps the record.</summary>
    public async Task DeleteFlagAsync(int projectId, string key)
    {
        await access.RequireAsync(projectId, Permission.ManageFlags);
        await using var db = await dbFactory.CreateDbContextAsync();
        var flag = await RequireFlagAsync(db, projectId, key);
        var actor = await access.ResolveUserIdAsync();
        db.FeatureFlags.Remove(flag);
        await db.SaveChangesAsync();
        await audit.RecordAsync("flag.deleted", "flag", flag.Id, projectId, new { key = flag.Key, name = flag.Name }, actor);
        Raise(projectId, flag, null, "deleted", null, null, actor, await ProjectKeyAsync(db, projectId));
        await BumpAsync(db, projectId, null);
    }

    // ----- Segments -----

    /// <summary>Creates or replaces a segment: a named list of user ids (Developer+ to edit; deleting needs Maintainer+).</summary>
    public async Task<FlagSegment> SaveSegmentAsync(int projectId, string key, string name, string? description, IEnumerable<string> members)
    {
        await access.RequireAsync(projectId, Permission.ToggleFlags);
        key = key.Trim().ToLowerInvariant();
        name = name.Trim();
        if (!IsValidKey(key)) throw new InvalidOperationException("The segment key may contain lowercase letters, digits, '.', '_' and '-' (up to 64), starting with a letter or digit.");
        if (name.Length is 0 or > 120) throw new InvalidOperationException("The name is required (up to 120 characters).");
        var list = members.Select(m => m.Trim()).Where(m => m.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        if (list.Count > 10000) throw new InvalidOperationException("A segment can have at most 10,000 members.");
        if (list.Any(m => m.Length > 200)) throw new InvalidOperationException("A user id can be at most 200 characters.");
        await using var db = await dbFactory.CreateDbContextAsync();
        var seg = await db.FlagSegments.FirstOrDefaultAsync(s => s.ProjectId == projectId && s.Key == key);
        var created = seg == null;
        if (seg == null) db.FlagSegments.Add(seg = new FlagSegment { ProjectId = projectId, Key = key });
        seg.Name = name;
        seg.Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        seg.MembersJson = JsonSerializer.Serialize(list);
        seg.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        await audit.RecordAsync(created ? "flag.segment_created" : "flag.segment_updated", "flag_segment", seg.Id, projectId, new { key, name, members = list.Count });
        await BumpAsync(db, projectId, null);
        return seg;
    }

    public async Task DeleteSegmentAsync(int projectId, string key)
    {
        await access.RequireAsync(projectId, Permission.ManageFlags);
        await using var db = await dbFactory.CreateDbContextAsync();
        var seg = await db.FlagSegments.FirstOrDefaultAsync(s => s.ProjectId == projectId && s.Key == key.ToLowerInvariant())
            ?? throw new InvalidOperationException("That segment no longer exists.");
        var inUse = await db.FlagEnvironmentStates.Where(s => s.Flag.ProjectId == projectId && s.RulesJson.Contains("\"" + seg.Key + "\""))
            .Select(s => s.Flag.Key).Distinct().ToListAsync();
        if (inUse.Count > 0) throw new InvalidOperationException($"The segment is used by {string.Join(", ", inUse)}. Remove it from their rules first.");
        db.FlagSegments.Remove(seg);
        await db.SaveChangesAsync();
        await audit.RecordAsync("flag.segment_deleted", "flag_segment", seg.Id, projectId, new { key = seg.Key });
    }

    public static List<string> SegmentMembers(FlagSegment s) => JsonSerializer.Deserialize<List<string>>(s.MembersJson) ?? [];

    // ----- Internals -----

    private static async Task<FeatureFlag> RequireFlagAsync(AppDbContext db, int projectId, string key) =>
        await db.FeatureFlags.FirstOrDefaultAsync(f => f.ProjectId == projectId && f.Key == key.ToLowerInvariant())
        ?? throw new InvalidOperationException($"The flag '{key}' no longer exists.");

    private static async Task<(FeatureFlag, FlagEnvironmentState)> RequireStateAsync(AppDbContext db, int projectId, string key, int environmentId)
    {
        var flag = await RequireFlagAsync(db, projectId, key);
        var state = await db.FlagEnvironmentStates.Include(s => s.Environment)
            .FirstOrDefaultAsync(s => s.FlagId == flag.Id && s.EnvironmentId == environmentId && s.Environment.ProjectId == projectId)
            ?? throw new InvalidOperationException("That environment no longer exists.");
        return (flag, state);
    }

    private static void Touch(FeatureFlag flag, FlagEnvironmentState state, int? actor)
    {
        var now = DateTime.UtcNow;
        state.UpdatedAt = now;
        state.UpdatedById = actor;
        flag.UpdatedAt = now;
    }

    private static Task<string> ProjectKeyAsync(AppDbContext db, int projectId) =>
        db.Projects.Where(p => p.Id == projectId).Select(p => p.Key).FirstAsync();

    /// <summary>History row, audit event, webhook and live-update wake-up for one change.</summary>
    private async Task RecordAsync(AppDbContext db, FeatureFlag flag, FlagEnvironment? env, string kind, string? oldValue, string? newValue,
        string auditAction, int? actor, bool bump = true)
    {
        var actorName = actor == null ? null : await db.Users.Where(u => u.Id == actor).Select(u => u.UserName).FirstOrDefaultAsync();
        db.FlagChanges.Add(new FlagChange
        {
            ProjectId = flag.ProjectId, FlagId = flag.Id, EnvironmentId = env?.Id, EnvironmentName = env?.Name, ActorId = actor, ActorName = actorName,
            Kind = kind, OldValue = Clip(oldValue), NewValue = Clip(newValue),
        });
        await db.SaveChangesAsync();
        // Rules can be long; the audit event carries a summary, the history row the full value.
        await audit.RecordAsync(auditAction, "flag", flag.Id, flag.ProjectId, new
        {
            key = flag.Key, environment = env?.Name, kind,
            oldValue = kind == "rules" ? null : Clip(oldValue, 500), newValue = kind == "rules" ? null : Clip(newValue, 500),
        }, actor);
        Raise(flag.ProjectId, flag, env, kind, oldValue, newValue, actor, await ProjectKeyAsync(db, flag.ProjectId));
        if (bump) await BumpAsync(db, flag.ProjectId, env?.Id);
    }

    private static string? Clip(string? s, int max = 8000) => s == null || s.Length <= max ? s : s[..max];

    private void Raise(int projectId, FeatureFlag flag, FlagEnvironment? env, string kind, string? oldValue, string? newValue, int? actor, string projectKey)
    {
        webhooks.Enqueue(projectId, WebhookEventNames.FlagChanged, new
        {
            flag = new { id = flag.Id, key = flag.Key, name = flag.Name, type = flag.Type.ToString().ToLowerInvariant(), archived = flag.IsArchived },
            environment = env == null ? null : new { id = env.Id, name = env.Name },
            change = new { kind, oldValue = Clip(oldValue, 2000), newValue = Clip(newValue, 2000) },
            url = baseUrl.Value + FlagUrls.Detail(projectKey, flag.Key),
        }, actor);
    }

    /// <summary>Raises the environment's version (all of the project's when <paramref name="environmentId"/> is null) and wakes live streams.</summary>
    private async Task BumpAsync(AppDbContext db, int projectId, int? environmentId)
    {
        await db.FlagEnvironments.Where(e => e.ProjectId == projectId && (environmentId == null || e.Id == environmentId))
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.Version, e => e.Version + 1));
        var versions = await db.FlagEnvironments.AsNoTracking().Where(e => e.ProjectId == projectId && (environmentId == null || e.Id == environmentId))
            .Select(e => new { e.Id, e.Version }).ToListAsync();
        foreach (var v in versions) feed.Publish(v.Id, v.Version);
    }
}

public static class FlagUrls
{
    public static string List(string projectKey) => $"/p/{projectKey}/flags";
    public static string Detail(string projectKey, string flagKey) => $"/p/{projectKey}/flags/{flagKey}";
}
