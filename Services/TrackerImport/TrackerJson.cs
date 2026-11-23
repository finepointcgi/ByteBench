using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using ByteBench.Data;

namespace ByteBench.Services.TrackerImport;

/// <summary>Small JSON and mapping helpers shared by the Trello, Linear and Asana readers (missing or oddly typed fields are null, never an exception).</summary>
internal static partial class TrackerJson
{
    public static JsonElement Prop(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) ? v : default;

    /// <summary>Follows a dotted path (<c>assignee.name</c>); null or missing anywhere gives a default element.</summary>
    public static JsonElement Path(JsonElement e, string path)
    {
        foreach (var part in path.Split('.')) e = Prop(e, part);
        return e;
    }

    public static string? Str(JsonElement e, string path) =>
        Path(e, path) is { ValueKind: JsonValueKind.String } v ? v.GetString() : null;

    public static bool Bool(JsonElement e, string path) => Path(e, path).ValueKind == JsonValueKind.True;

    public static double? Num(JsonElement e, string path) =>
        Path(e, path) is { ValueKind: JsonValueKind.Number } v && v.TryGetDouble(out var d) ? d : null;

    public static IEnumerable<JsonElement> Items(JsonElement e, string path)
    {
        var v = Path(e, path);
        return v.ValueKind == JsonValueKind.Array ? v.EnumerateArray() : [];
    }

    public static DateTime? DateTimeOf(JsonElement e, string path) =>
        Str(e, path) is { } s && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) ? d.UtcDateTime : null;

    /// <summary>A calendar date (<c>2024-05-30</c>, or the date part of a timestamp).</summary>
    public static DateTime? DateOf(JsonElement e, string path) =>
        Str(e, path) is { Length: >= 10 } s && DateTime.TryParseExact(s[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var d) ? d : null;

    public static IssueType TypeFromLabels(IEnumerable<string> labels)
    {
        foreach (var l in labels)
        {
            var n = l.Trim().ToLowerInvariant();
            if (n == "bug") return IssueType.Bug;
            if (n is "feature" or "enhancement") return IssueType.Feature;
            if (n == "epic") return IssueType.Epic;
        }
        return IssueType.Task;
    }

    /// <summary>The category a list or section name suggests: done, in review, in progress, else to do.</summary>
    public static IssueStatus CategoryFromName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return IssueStatus.Todo;
        if (DoneName().IsMatch(name)) return IssueStatus.Done;
        if (ReviewName().IsMatch(name)) return IssueStatus.InReview;
        if (ProgressName().IsMatch(name)) return IssueStatus.InProgress;
        return IssueStatus.Todo;
    }

    [GeneratedRegex(@"\b(done|complete|completed|shipped|released|closed|finished)\b", RegexOptions.IgnoreCase)]
    private static partial Regex DoneName();
    [GeneratedRegex(@"\b(review|reviewing|testing|qa)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ReviewName();
    [GeneratedRegex(@"\b(doing|progress|wip|development|started)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ProgressName();

    public static IssuePriority? PriorityFromWord(string? word) => word?.Trim().ToLowerInvariant() switch
    {
        "highest" or "critical" or "urgent" or "blocker" => IssuePriority.Highest,
        "high" => IssuePriority.High,
        "medium" or "normal" or "med" => IssuePriority.Medium,
        "low" => IssuePriority.Low,
        "lowest" => IssuePriority.Lowest,
        _ => null,
    };

    public static string Clip(string s, int max) => s.Length <= max ? s : s[..max];
}
