using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using ByteBench.Data;

namespace ByteBench.Services.Flags;

/// <summary>Who a flag is evaluated for: a user id plus free-form string attributes (plan, country, email, ...).</summary>
public sealed class FlagContext
{
    public string? UserId { get; set; }
    public Dictionary<string, string> Attributes { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary><c>userId</c> (also <c>key</c> and <c>user</c>) is the user id; anything else is looked up in the attributes.</summary>
    public string? Get(string? attribute)
    {
        if (string.IsNullOrWhiteSpace(attribute)) return null;
        if (attribute.Equals("userId", StringComparison.OrdinalIgnoreCase) || attribute.Equals("key", StringComparison.OrdinalIgnoreCase)
            || attribute.Equals("user", StringComparison.OrdinalIgnoreCase)) return UserId;
        return Attributes.TryGetValue(attribute, out var v) ? v : null;
    }

    /// <summary>A stable text form (for ETags).</summary>
    public string Canonical() => (UserId ?? "") + "\n" + string.Join("\n", Attributes.OrderBy(a => a.Key, StringComparer.OrdinalIgnoreCase)
        .Select(a => a.Key.ToLowerInvariant() + "=" + a.Value));
}

/// <summary>One condition of a rule: <c>attribute op values</c>, or a segment test (<c>inSegment</c>, <c>notInSegment</c>; values are segment keys).</summary>
public sealed class FlagClause
{
    public string? Attribute { get; set; }
    public string Op { get; set; } = "equals";
    public List<string> Values { get; set; } = [];
}

/// <summary>
/// A targeting rule. It matches when every clause matches (no clauses: everyone) and, if <see cref="Percentage"/> is set, the
/// context falls in the first <c>Percentage</c> percent of the stable hash bucket of its <see cref="BucketBy"/> attribute.
/// The first matching rule's <see cref="Value"/> is served.
/// </summary>
public sealed class FlagRule
{
    public string Id { get; set; } = "";
    public string? Description { get; set; }
    public List<FlagClause> Clauses { get; set; } = [];
    /// <summary>0-100 (two decimals are honoured); null: no rollout, the clauses alone decide.</summary>
    public double? Percentage { get; set; }
    public string BucketBy { get; set; } = "userId";
    public JsonNode? Value { get; set; }
}

public sealed record FlagResult(string Key, FlagType Type, JsonNode? Value, string Reason, string? RuleId = null);

/// <summary>
/// The evaluation semantics, with no I/O: flag off gives the off value; otherwise the first matching rule in order; otherwise
/// the environment's default value. Percentage rollouts hash <c>flagKey:ruleId:bucketValue</c> (SHA-256) into 10,000 buckets,
/// so a user stays in or out as long as the rule id and bucket attribute stay, and raising the percentage only adds users.
/// </summary>
public static class FlagEvaluator
{
    public static readonly string[] Operators = ["equals", "notEquals", "in", "notIn", "contains", "startsWith", "endsWith", "inSegment", "notInSegment"];

    public static readonly JsonSerializerOptions RuleJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static List<FlagRule> ParseRules(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return JsonSerializer.Deserialize<List<FlagRule>>(json, RuleJson) ?? []; }
        catch (JsonException) { return []; }
    }

    public static string SerializeRules(IEnumerable<FlagRule> rules) => JsonSerializer.Serialize(rules, RuleJson);

    /// <summary>The bucket (0-9999) of a bucket value for a flag and rule.</summary>
    public static int Bucket(string flagKey, string ruleId, string bucketValue)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{flagKey}:{ruleId}:{bucketValue}"));
        return (int)(BitConverter.ToUInt64(hash, 0) % 10000UL);
    }

    public static FlagResult Evaluate(string flagKey, FlagType type, JsonNode? offValue, bool enabled, JsonNode? defaultValue,
        IReadOnlyList<FlagRule> rules, FlagContext context, Func<string, IReadOnlySet<string>?> segments)
    {
        if (!enabled) return new FlagResult(flagKey, type, offValue?.DeepClone(), "off");
        foreach (var rule in rules)
            if (Matches(flagKey, rule, context, segments))
                return new FlagResult(flagKey, type, rule.Value?.DeepClone(), "rule", rule.Id);
        return new FlagResult(flagKey, type, defaultValue?.DeepClone(), "default");
    }

    public static bool Matches(string flagKey, FlagRule rule, FlagContext context, Func<string, IReadOnlySet<string>?> segments)
    {
        foreach (var clause in rule.Clauses)
            if (!ClauseMatches(clause, context, segments)) return false;
        if (rule.Percentage is not { } pct) return true;
        var by = context.Get(rule.BucketBy);
        if (string.IsNullOrEmpty(by)) return false; // cannot bucket an anonymous context
        return Bucket(flagKey, rule.Id, by) < (int)Math.Round(Math.Clamp(pct, 0, 100) * 100);
    }

    private static bool ClauseMatches(FlagClause c, FlagContext context, Func<string, IReadOnlySet<string>?> segments)
    {
        switch (c.Op)
        {
            case "inSegment": return InAnySegment(c, context, segments);
            case "notInSegment": return !InAnySegment(c, context, segments);
        }
        var actual = context.Get(c.Attribute);
        var values = c.Values;
        switch (c.Op)
        {
            case "equals": return actual != null && values.Count > 0 && Eq(actual, values[0]);
            case "notEquals": return actual == null || values.Count == 0 || !Eq(actual, values[0]);
            case "in": return actual != null && values.Any(v => Eq(actual, v));
            case "notIn": return actual == null || !values.Any(v => Eq(actual, v));
            case "contains": return actual != null && values.Any(v => v.Length > 0 && actual.Contains(v, StringComparison.OrdinalIgnoreCase));
            case "startsWith": return actual != null && values.Any(v => actual.StartsWith(v, StringComparison.OrdinalIgnoreCase));
            case "endsWith": return actual != null && values.Any(v => actual.EndsWith(v, StringComparison.OrdinalIgnoreCase));
            default: return false;
        }
    }

    private static bool Eq(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static bool InAnySegment(FlagClause c, FlagContext context, Func<string, IReadOnlySet<string>?> segments)
    {
        if (string.IsNullOrEmpty(context.UserId)) return false;
        foreach (var key in c.Values)
            if (segments(key) is { } members && members.Contains(context.UserId)) return true;
        return false;
    }

    // ----- Values -----

    public static string DefaultValueText(FlagType type, bool on) => type switch
    {
        FlagType.Boolean => on ? "true" : "false",
        FlagType.String => "\"\"",
        FlagType.Number => "0",
        _ => "{}",
    };

    /// <summary>Checks a JSON value against the flag's type and returns it in compact form.</summary>
    public static string NormalizeValue(FlagType type, JsonNode? value)
    {
        if (value != null) value = JsonNode.Parse(value.ToJsonString()); // a canonical, JsonElement-backed copy (JsonValue.Create(5) is an int, not a double)
        var ok = type switch
        {
            FlagType.Boolean => value is JsonValue b && b.TryGetValue<bool>(out _),
            FlagType.String => value is JsonValue s && s.TryGetValue<string>(out _),
            FlagType.Number => value is JsonValue n && n.TryGetValue<double>(out var d) && double.IsFinite(d),
            _ => value != null,
        };
        if (!ok) throw new InvalidOperationException($"The value must be a {TypeName(type)}.");
        var text = value!.ToJsonString();
        if (text.Length > 4000) throw new InvalidOperationException("A value can be at most 4000 characters.");
        return text;
    }

    public static string TypeName(FlagType t) => t switch
    {
        FlagType.Boolean => "boolean (true or false)",
        FlagType.String => "string",
        FlagType.Number => "number",
        _ => "JSON value",
    };

    /// <summary>Value typed in a form: for strings the text itself, for the others JSON ("true", "12.5", "{\"a\":1}").</summary>
    public static JsonNode? FromInput(FlagType type, string? raw)
    {
        raw ??= "";
        if (type == FlagType.String) return JsonValue.Create(raw);
        try { return JsonNode.Parse(raw.Trim()); }
        catch (JsonException) { throw new InvalidOperationException($"'{raw}' is not valid JSON; the value must be a {TypeName(type)}."); }
    }

    /// <summary>The value as shown in a form: strings without quotes, others as compact JSON.</summary>
    public static string ToInput(FlagType type, string? json)
    {
        if (json == null) return "";
        if (type == FlagType.String)
        {
            try { return JsonNode.Parse(json) is JsonValue v && v.TryGetValue<string>(out var s) ? s : json; }
            catch (JsonException) { return json; }
        }
        return json;
    }

    public static JsonNode? ParseValue(string? json)
    {
        if (json == null) return null;
        try { return JsonNode.Parse(json); }
        catch (JsonException) { return null; }
    }

    /// <summary>Validates and normalises a rule list (ids filled in, values checked against the type); throws with a message for the user.</summary>
    public static List<FlagRule> ValidateRules(FlagType type, IEnumerable<FlagRule>? rules)
    {
        var list = (rules ?? []).ToList();
        if (list.Count > 50) throw new InvalidOperationException("A flag can have at most 50 rules per environment.");
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var n = 0;
        foreach (var r in list)
        {
            n++;
            if (string.IsNullOrWhiteSpace(r.Id)) r.Id = "r" + Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();
            r.Id = r.Id.Trim();
            if (r.Id.Length > 40 || !r.Id.All(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.'))
                throw new InvalidOperationException($"Rule {n}: the id may only contain letters, digits, '-', '_' and '.' (up to 40).");
            if (!ids.Add(r.Id)) throw new InvalidOperationException($"Rule {n}: the id '{r.Id}' is used twice.");
            if (r.Description?.Length > 200) throw new InvalidOperationException($"Rule {n}: the description is too long.");
            if (r.Clauses.Count > 20) throw new InvalidOperationException($"Rule {n}: at most 20 conditions.");
            foreach (var c in r.Clauses)
            {
                if (!Operators.Contains(c.Op)) throw new InvalidOperationException($"Rule {n}: unknown operator '{c.Op}'.");
                var segment = c.Op is "inSegment" or "notInSegment";
                if (!segment && string.IsNullOrWhiteSpace(c.Attribute)) throw new InvalidOperationException($"Rule {n}: a condition needs an attribute.");
                if (c.Values.Count is 0 or > 500) throw new InvalidOperationException($"Rule {n}: a condition needs between 1 and 500 values.");
                c.Values = c.Values.Select(v => v.Trim()).Where(v => v.Length > 0).ToList();
                if (c.Values.Count == 0) throw new InvalidOperationException($"Rule {n}: a condition needs at least one non-empty value.");
                c.Attribute = segment ? null : c.Attribute!.Trim();
            }
            if (r.Percentage is { } p && (double.IsNaN(p) || p < 0 || p > 100)) throw new InvalidOperationException($"Rule {n}: the percentage must be between 0 and 100.");
            if (r.Percentage == null || string.IsNullOrWhiteSpace(r.BucketBy)) r.BucketBy = "userId";
            r.Value = ParseValue(NormalizeValue(type, r.Value));
        }
        return list;
    }
}
