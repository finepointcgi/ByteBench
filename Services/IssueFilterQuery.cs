using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;

namespace ByteBench.Services;

/// <summary>
/// Converts an <see cref="IssueFilter"/> to and from the issue list's URL query string, so a filter is addressable and a
/// saved filter is just a stored query string. Every public read/write property of <see cref="IssueFilter"/> is a
/// parameter named in camelCase (<c>assigneeId</c>, <c>milestoneId</c>, …); properties at their default value are left
/// out. Supported property types: string, bool, int, long, enums (by name) and their nullable forms, and
/// <c>List&lt;int&gt;</c>/<c>List&lt;string&gt;</c> (comma-separated). A nullable property whose default isn't null is
/// written as an empty value when null (<c>open=</c> means "open or closed").
/// <para>To add a filter field, add a property to <see cref="IssueFilter"/>; nothing here needs to change.</para>
/// </summary>
public static class IssueFilterQuery
{
    private static readonly IssueFilter Defaults = new();
    private static readonly PropertyInfo[] Props = typeof(IssueFilter)
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(p => p.CanRead && p.CanWrite && p.GetIndexParameters().Length == 0 && IsSupported(p.PropertyType))
        .ToArray();

    private static string ParamName(PropertyInfo p) => JsonNamingPolicy.CamelCase.ConvertName(p.Name);

    /// <summary>The query string (without '?') for <paramref name="filter"/>; empty for the default filter.</summary>
    public static string ToQueryString(IssueFilter filter)
    {
        var parts = new List<string>();
        foreach (var p in Props)
        {
            var value = p.GetValue(filter);
            if (ValueEquals(value, p.GetValue(Defaults))) continue;
            parts.Add($"{ParamName(p)}={Uri.EscapeDataString(Format(value))}");
        }
        return string.Join("&", parts);
    }

    /// <summary>A filter from a query string (with or without '?'). Unknown parameters and invalid values are ignored.</summary>
    public static IssueFilter Parse(string? query)
    {
        var filter = new IssueFilter();
        if (string.IsNullOrWhiteSpace(query)) return filter;
        var values = QueryHelpers.ParseQuery(query.StartsWith('?') ? query : "?" + query);
        foreach (var p in Props)
        {
            if (!values.TryGetValue(ParamName(p), out var raw)) continue;
            if (TryConvert(raw.ToString(), p.PropertyType, out var v)) p.SetValue(filter, v);
        }
        return filter;
    }

    /// <summary>Parses the query part of an absolute or relative URI.</summary>
    public static IssueFilter FromUri(string uri)
    {
        var q = uri.IndexOf('?');
        if (q < 0) return new IssueFilter();
        var hash = uri.IndexOf('#', q);
        return Parse(hash < 0 ? uri[q..] : uri[q..hash]);
    }

    /// <summary>Canonical form of a query string (parse, then format): drops unknown parameters and defaults.</summary>
    public static string Normalize(string? query) => ToQueryString(Parse(query));

    // ----- Internals -----

    private static bool IsSupported(Type t)
    {
        var u = Nullable.GetUnderlyingType(t) ?? t;
        return u == typeof(string) || u == typeof(bool) || u == typeof(int) || u == typeof(long) || u.IsEnum
               || t == typeof(List<int>) || t == typeof(List<string>);
    }

    private static bool ValueEquals(object? a, object? b) => (a, b) switch
    {
        (List<int> x, List<int> y) => x.SequenceEqual(y),
        (List<string> x, List<string> y) => x.SequenceEqual(y),
        (List<int> x, null) => x.Count == 0,
        (List<string> x, null) => x.Count == 0,
        (string x, null) => x.Length == 0,
        (null, string y) => y.Length == 0,
        _ => Equals(a, b),
    };

    private static string Format(object? value) => value switch
    {
        null => "",
        bool b => b ? "true" : "false",
        Enum e => e.ToString(),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        List<int> l => string.Join(",", l),
        List<string> l => string.Join(",", l),
        _ => value.ToString() ?? "",
    };

    private static bool TryConvert(string raw, Type t, out object? value)
    {
        value = null;
        var underlying = Nullable.GetUnderlyingType(t);
        var nullable = underlying != null || !t.IsValueType;
        var u = underlying ?? t;
        if (raw.Length == 0 && u != typeof(string))
        {
            if (t == typeof(List<int>)) { value = new List<int>(); return true; }
            if (t == typeof(List<string>)) { value = new List<string>(); return true; }
            return nullable; // empty means null
        }
        if (u == typeof(string)) { value = raw; return true; }
        if (u == typeof(bool) && bool.TryParse(raw, out var b)) { value = b; return true; }
        if (u == typeof(int) && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i)) { value = i; return true; }
        if (u == typeof(long) && long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l)) { value = l; return true; }
        if (u.IsEnum && !int.TryParse(raw, out _) && Enum.TryParse(u, raw, ignoreCase: true, out var e)) { value = e; return true; }
        if (t == typeof(List<int>))
        {
            var ints = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(x => int.TryParse(x, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? (int?)n : null).ToList();
            if (ints.Any(x => x == null)) return false;
            value = ints.Select(x => x!.Value).ToList();
            return true;
        }
        if (t == typeof(List<string>)) { value = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(); return true; }
        return false;
    }
}
