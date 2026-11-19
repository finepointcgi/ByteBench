using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using GitBench.Data;
using Microsoft.EntityFrameworkCore;

namespace GitBench.Services.Flags;

/// <summary>An authenticated SDK key: which environment it reads, and whether it may download rules.</summary>
public sealed record SdkPrincipal(int KeyId, int EnvironmentId, int ProjectId, string EnvironmentName, SdkKeyKind Kind)
{
    public bool IsServer => Kind == SdkKeyKind.Server;
}

/// <summary>The flags of one environment evaluated for one context.</summary>
public sealed record FlagEvaluation(long Version, List<FlagResult> Results);

/// <summary>
/// Server-side evaluation for SDK clients (scoped; no user, no <see cref="ProjectAccess"/>: access is the SDK key). Evaluates
/// against the live database state, so a change is visible to the next request.
/// </summary>
public class FlagSdkService(IDbContextFactory<AppDbContext> dbFactory)
{
    private static readonly TimeSpan UsedWindow = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan EvaluatedWindow = TimeSpan.FromHours(1);

    /// <summary>The active key matching <paramref name="presented"/>, looked up by its SHA-256 hash; null for anything else (including personal access tokens).</summary>
    public async Task<SdkPrincipal?> AuthenticateAsync(string? presented)
    {
        if (presented == null || !presented.StartsWith(FlagService.SdkKeyPrefix, StringComparison.Ordinal) || presented.Length > 200) return null;
        var hash = FlagService.HashKey(presented);
        await using var db = await dbFactory.CreateDbContextAsync();
        var key = await db.FlagSdkKeys.AsNoTracking().Include(k => k.Environment).FirstOrDefaultAsync(k => k.KeyHash == hash && k.RevokedAt == null);
        if (key == null) return null;
        var now = DateTime.UtcNow;
        if (key.LastUsedAt == null || now - key.LastUsedAt > UsedWindow)
            await db.FlagSdkKeys.Where(k => k.Id == key.Id).ExecuteUpdateAsync(s => s.SetProperty(k => k.LastUsedAt, now));
        return new SdkPrincipal(key.Id, key.EnvironmentId, key.Environment.ProjectId, key.Environment.Name, key.Kind);
    }

    public async Task<long> VersionAsync(int environmentId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.FlagEnvironments.AsNoTracking().Where(e => e.Id == environmentId).Select(e => e.Version).FirstOrDefaultAsync();
    }

    private sealed record Row(FeatureFlag Flag, FlagEnvironmentState State);

    private static async Task<(long Version, List<Row> Rows, Dictionary<string, HashSet<string>> Segments)> LoadAsync(AppDbContext db, SdkPrincipal p, string? key)
    {
        var version = await db.FlagEnvironments.AsNoTracking().Where(e => e.Id == p.EnvironmentId).Select(e => e.Version).FirstOrDefaultAsync();
        var q = db.FlagEnvironmentStates.AsNoTracking().Include(s => s.Flag).Where(s => s.EnvironmentId == p.EnvironmentId);
        if (key != null) q = q.Where(s => s.Flag.Key == key);
        var rows = (await q.OrderBy(s => s.Flag.Key).ToListAsync()).Select(s => new Row(s.Flag, s)).ToList();
        var segments = (await db.FlagSegments.AsNoTracking().Where(s => s.ProjectId == p.ProjectId).ToListAsync())
            .ToDictionary(s => s.Key, s => (JsonSerializer.Deserialize<List<string>>(s.MembersJson) ?? []).ToHashSet(StringComparer.Ordinal));
        return (version, rows, segments);
    }

    private static FlagResult Eval(Row r, FlagContext ctx, Dictionary<string, HashSet<string>> segments)
    {
        if (r.Flag.IsArchived) return new FlagResult(r.Flag.Key, r.Flag.Type, FlagEvaluator.ParseValue(r.Flag.OffValue), "archived");
        return FlagEvaluator.Evaluate(r.Flag.Key, r.Flag.Type, FlagEvaluator.ParseValue(r.Flag.OffValue), r.State.Enabled,
            FlagEvaluator.ParseValue(r.State.DefaultValue), FlagEvaluator.ParseRules(r.State.RulesJson), ctx,
            k => segments.TryGetValue(k, out var m) ? m : null);
    }

    /// <summary>One flag for a context, or null when the environment has no such flag. Archived flags serve their off value.</summary>
    public async Task<FlagEvaluation?> EvaluateAsync(SdkPrincipal p, string flagKey, FlagContext ctx)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var (version, rows, segments) = await LoadAsync(db, p, flagKey.ToLowerInvariant());
        if (rows.Count == 0) return null;
        await MarkEvaluatedAsync(db, rows);
        return new FlagEvaluation(version, [Eval(rows[0], ctx, segments)]);
    }

    /// <summary>Every active flag of the environment for a context.</summary>
    public async Task<FlagEvaluation> EvaluateAllAsync(SdkPrincipal p, FlagContext ctx)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var (version, rows, segments) = await LoadAsync(db, p, null);
        rows = rows.Where(r => !r.Flag.IsArchived).ToList();
        await MarkEvaluatedAsync(db, rows);
        return new FlagEvaluation(version, rows.Select(r => Eval(r, ctx, segments)).ToList());
    }

    /// <summary>Rules and segments for local evaluation (server keys only).</summary>
    public async Task<JsonObject> DefinitionsAsync(SdkPrincipal p)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var (version, rows, segments) = await LoadAsync(db, p, null);
        var flags = new JsonArray();
        foreach (var r in rows.Where(r => !r.Flag.IsArchived))
            flags.Add(new JsonObject
            {
                ["key"] = r.Flag.Key,
                ["type"] = r.Flag.Type.ToString().ToLowerInvariant(),
                ["enabled"] = r.State.Enabled,
                ["offValue"] = FlagEvaluator.ParseValue(r.Flag.OffValue),
                ["defaultValue"] = FlagEvaluator.ParseValue(r.State.DefaultValue),
                ["rules"] = JsonSerializer.SerializeToNode(FlagEvaluator.ParseRules(r.State.RulesJson), FlagEvaluator.RuleJson),
            });
        var segs = new JsonObject();
        foreach (var (k, members) in segments.OrderBy(s => s.Key, StringComparer.Ordinal))
            segs[k] = new JsonArray(members.OrderBy(m => m, StringComparer.Ordinal).Select(m => (JsonNode?)JsonValue.Create(m)).ToArray());
        return new JsonObject
        {
            ["version"] = version, ["environment"] = p.EnvironmentName, ["flags"] = flags, ["segments"] = segs,
            ["hashing"] = "sha256(flagKey:ruleId:bucketValue) first 8 bytes little-endian unsigned, modulo 10000 < percentage*100",
        };
    }

    /// <summary>Records that the flags were evaluated (at most about once an hour per flag), for stale-flag detection.</summary>
    private static async Task MarkEvaluatedAsync(AppDbContext db, List<Row> rows)
    {
        var now = DateTime.UtcNow;
        var cutoff = now - EvaluatedWindow;
        var due = rows.Where(r => r.State.LastEvaluatedAt == null || r.State.LastEvaluatedAt < cutoff).Select(r => r.State.Id).ToList();
        if (due.Count == 0) return;
        await db.FlagEnvironmentStates.Where(s => due.Contains(s.Id)).ExecuteUpdateAsync(s => s.SetProperty(x => x.LastEvaluatedAt, now));
    }

    // ----- Responses -----

    public static JsonObject ToJson(FlagResult r, bool includeRule) => new()
    {
        ["key"] = r.Key,
        ["type"] = r.Type.ToString().ToLowerInvariant(),
        ["value"] = r.Value?.DeepClone(),
        ["reason"] = r.Reason,
        ["ruleId"] = includeRule ? r.RuleId : null,
    };

    /// <summary>A strong ETag for a response body.</summary>
    public static string ETag(string body) => "\"" + Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(body)))[..32].ToLowerInvariant() + "\"";
}
