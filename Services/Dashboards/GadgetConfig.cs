using System.Text.Json;
using ByteBench.Data;

namespace ByteBench.Services.Dashboards;

/// <summary>
/// A gadget's settings, stored as JSON in <see cref="DashboardGadget.Config"/>. One shape for every kind; each kind reads
/// the fields it needs. Ids name things (projects, filters, sprints, repositories) that are looked up as the viewer.
/// </summary>
public sealed record GadgetConfig
{
    /// <summary>Issue gadgets, burndown, activity (optional) and created-vs-resolved (optional: all your projects).</summary>
    public int? ProjectId { get; init; }
    /// <summary>A saved filter of <see cref="ProjectId"/>; it must be the viewer's own or shared.</summary>
    public int? SavedFilterId { get; init; }
    /// <summary>An issue-list query string (<see cref="IssueFilterQuery"/>) used when no saved filter is chosen.</summary>
    public string? Query { get; init; }
    /// <summary>Filter results: which columns to show (see <see cref="GadgetMath.Columns"/>).</summary>
    public List<string>? Columns { get; init; }
    /// <summary>Issue chart: <c>pie</c> or <c>bar</c>.</summary>
    public string? Chart { get; init; }
    /// <summary>Issue chart: <c>status</c>, <c>assignee</c>, <c>priority</c>, <c>type</c> or <c>label</c>.</summary>
    public string? Field { get; init; }
    /// <summary>Created vs resolved: the window in days.</summary>
    public int? Days { get; init; }
    /// <summary>Burndown: the sprint (null: the project's active sprint).</summary>
    public int? SprintId { get; init; }
    /// <summary>Pipeline status: the repository.</summary>
    public int? RepositoryId { get; init; }
    /// <summary>Markdown note.</summary>
    public string? Markdown { get; init; }
    /// <summary>How many rows a list gadget shows.</summary>
    public int? Limit { get; init; }

    public const int MaxMarkdown = 5000;

    public IReadOnlyList<string> ColumnsOrDefault => Columns is { Count: > 0 } c ? c : GadgetMath.DefaultColumns;
    public string ChartOrDefault => Chart == "bar" ? "bar" : "pie";
    public string FieldOrDefault => GadgetMath.Fields.Contains(Field ?? "") ? Field! : "status";
    public int DaysOrDefault => Math.Clamp(Days ?? 30, 7, 365);
    public int LimitOrDefault => Math.Clamp(Limit ?? 10, 1, 50);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };

    public static GadgetConfig Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new();
        try { return JsonSerializer.Deserialize<GadgetConfig>(json, Json) ?? new(); }
        catch (JsonException) { return new(); }
    }

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>Checks the values (throws <see cref="InvalidOperationException"/> with a readable message) and drops unknown columns.</summary>
    public GadgetConfig Validated()
    {
        if (Markdown is { Length: > MaxMarkdown }) throw new InvalidOperationException($"The note can be at most {MaxMarkdown} characters.");
        if (Query is { Length: > 2000 }) throw new InvalidOperationException("That filter is too long.");
        if (Days is < 1 or > 365) throw new InvalidOperationException("Choose between 1 and 365 days.");
        if (Limit is < 1 or > 50) throw new InvalidOperationException("Show between 1 and 50 rows.");
        return this with
        {
            Query = Query?.TrimStart('?'),
            Columns = Columns?.Where(GadgetMath.Columns.Contains).Distinct().ToList(),
            Chart = Chart is null ? null : ChartOrDefault,
            Field = Field is null ? null : FieldOrDefault,
        };
    }
}
