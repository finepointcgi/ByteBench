using ByteBench.Data;
using ByteBench.Services.Fields;

namespace ByteBench.Components.Fields;

/// <summary>
/// What the issue list's optional columns show, loaded once per list by <see cref="IssueFieldFilters"/> and read by
/// <see cref="IssueFieldCells"/> for each row.
/// </summary>
public sealed class IssueColumnData
{
    public const string ComponentsColumn = "components";
    public const string FixVersionsColumn = "fixVersions";
    public const string EstimateColumn = "estimate";

    public IReadOnlyList<string> Columns { get; set; } = [];
    public List<CustomField> Fields { get; set; } = [];
    public Dictionary<int, Dictionary<int, FieldValue>> Values { get; set; } = [];
    public Dictionary<int, List<string>> Components { get; set; } = [];
    public Dictionary<int, List<string>> FixVersions { get; set; } = [];
    public Dictionary<int, string> UserNames { get; set; } = [];
    public int HoursPerDay { get; set; } = Duration.DefaultHoursPerDay;

    /// <summary>Header text for a column key.</summary>
    public string Title(string column) => column switch
    {
        ComponentsColumn => "Components",
        FixVersionsColumn => "Fix versions",
        EstimateColumn => "Remaining",
        _ => Fields.FirstOrDefault(f => f.Key == column)?.Name ?? column,
    };

    /// <summary>The cell text for an issue, or null when empty.</summary>
    public string? Cell(Issue issue, string column)
    {
        switch (column)
        {
            case ComponentsColumn: return Components.TryGetValue(issue.Id, out var c) && c.Count > 0 ? string.Join(", ", c) : null;
            case FixVersionsColumn: return FixVersions.TryGetValue(issue.Id, out var v) && v.Count > 0 ? string.Join(", ", v) : null;
            case EstimateColumn: return Duration.FormatOrNull(issue.RemainingEstimateMinutes, HoursPerDay);
        }
        if (Fields.FirstOrDefault(f => f.Key == column) is not { } field) return null;
        var value = Values.GetValueOrDefault(issue.Id)?.GetValueOrDefault(field.Id);
        if (value == null || value.IsEmpty) return null;
        var text = CustomFieldRules.ToText(field, value, id => UserNames.GetValueOrDefault(id));
        return text.Length > 40 ? text[..39] + "…" : text;
    }
}
