using System.Globalization;
using ByteBench.Data;

namespace ByteBench.Services;

/// <summary>Component, version and custom field filters for the issue list, plus its optional columns.</summary>
public partial record IssueFilter
{
    /// <summary>Issues in this component; 0 = in no component.</summary>
    public int? ComponentId { get; set; }
    /// <summary>Issues with this fix version; 0 = no fix version.</summary>
    public int? FixVersionId { get; set; }
    /// <summary>Issues with this affects version; 0 = no affects version.</summary>
    public int? AffectsVersionId { get; set; }
    /// <summary>
    /// One custom field condition, <c>key:value</c>: the field with that key has that value (an option, text, number,
    /// yyyy-MM-dd date, username or true/false; text compares case-insensitively). <c>key:</c> means "has no value".
    /// </summary>
    public string? Cf { get; set; }
    /// <summary>Extra list columns, comma-separated: custom field keys and <c>components</c>, <c>fixVersions</c>, <c>estimate</c>.</summary>
    public string? Columns { get; set; }

    /// <summary>True if any filter in this file is set (columns don't count).</summary>
    public bool HasFieldFilters => ComponentId != null || FixVersionId != null || AffectsVersionId != null || !string.IsNullOrEmpty(Cf);

    public IReadOnlyList<string> ColumnList => (Columns ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    internal IQueryable<Issue> ApplyFields(IQueryable<Issue> q)
    {
        if (ComponentId is { } c) q = c == 0 ? q.Where(i => !i.Components.Any()) : q.Where(i => i.Components.Any(x => x.Id == c));
        if (FixVersionId is { } f)
            q = f == 0 ? q.Where(i => !i.VersionLinks.Any(l => l.Kind == VersionLinkKind.Fix))
                : q.Where(i => i.VersionLinks.Any(l => l.Kind == VersionLinkKind.Fix && l.VersionId == f));
        if (AffectsVersionId is { } a)
            q = a == 0 ? q.Where(i => !i.VersionLinks.Any(l => l.Kind == VersionLinkKind.Affects))
                : q.Where(i => i.VersionLinks.Any(l => l.Kind == VersionLinkKind.Affects && l.VersionId == a));
        if (ParseCf(Cf) is { } cf) q = ApplyCustomField(q, cf.Key, cf.Value);
        return q;
    }

    public static (string Key, string Value)? ParseCf(string? cf)
    {
        if (string.IsNullOrWhiteSpace(cf)) return null;
        var colon = cf.IndexOf(':');
        return colon <= 0 ? null : (cf[..colon].Trim(), cf[(colon + 1)..].Trim());
    }

    /// <summary>
    /// Issues whose custom field <paramref name="key"/> equals <paramref name="raw"/> (empty: has no value). The value is
    /// compared as every type it could be, so the filter needs no field lookup; the key decides which field is meant.
    /// </summary>
    public static IQueryable<Issue> ApplyCustomField(IQueryable<Issue> q, string key, string raw)
    {
        if (raw.Length == 0) return q.Where(i => !i.CustomFieldValues.Any(v => v.Field.Key == key));
        var lower = raw.ToLower();
        double? number = double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : null;
        DateTime? date = DateTime.TryParseExact(raw, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
        bool? flag = lower is "true" or "yes" ? true : lower is "false" or "no" ? false : null;
        return q.Where(i => i.CustomFieldValues.Any(v => v.Field.Key == key && (
            (v.TextValue != null && v.TextValue.ToLower() == lower)
            || (number != null && v.NumberValue == number)
            || (date != null && v.DateValue == date)
            || (flag != null && v.BoolValue == flag)
            || (v.UserId != null && v.User!.UserName == raw))));
    }
}
