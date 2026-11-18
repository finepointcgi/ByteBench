using System.Collections;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using GitBench.Data;

namespace GitBench.Services.Fields;

/// <summary>
/// A custom field's value, whatever its type. Only the member that matches the field's type is set: <see cref="Text"/>
/// (text, long text, URL, single select), <see cref="Options"/> (multi select), <see cref="Number"/>, <see cref="Date"/>,
/// <see cref="UserId"/> (or <see cref="UserName"/> before it is resolved) and <see cref="Bool"/> (checkbox).
/// </summary>
public sealed record FieldValue
{
    public static readonly FieldValue Empty = new();

    public string? Text { get; init; }
    public double? Number { get; init; }
    public DateTime? Date { get; init; }
    public int? UserId { get; init; }
    /// <summary>A username given as input, not yet resolved to <see cref="UserId"/>.</summary>
    public string? UserName { get; init; }
    public bool? Bool { get; init; }
    public IReadOnlyList<string> Options { get; init; } = [];

    public bool IsEmpty => Text == null && Number == null && Date == null && UserId == null && UserName == null && Bool == null && Options.Count == 0;

    /// <summary>A canonical string for change detection and equality.</summary>
    public string Canonical => IsEmpty ? "" : string.Join("\u001f", Text, Number?.ToString("R", CultureInfo.InvariantCulture),
        Date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), UserId, UserName, Bool, string.Join("\u001e", Options));

    public bool Same(FieldValue other) => Canonical == other.Canonical;
}

/// <summary>Values chosen for a new issue before it exists (new-issue form and API); see <see cref="Issue.FieldDraft"/>.</summary>
public sealed class IssueFieldDraft
{
    /// <summary>Raw custom field input by field id (string, number, bool, date, list of strings, user id or username, JsonElement).</summary>
    public Dictionary<int, object?> Values { get; } = [];
    public List<int> ComponentIds { get; set; } = [];
    public List<int> FixVersionIds { get; set; } = [];
    public List<int> AffectsVersionIds { get; set; } = [];
    /// <summary>A problem the form found (e.g. an estimate it couldn't parse); creating the issue fails with it.</summary>
    public string? Error { get; set; }
}

/// <summary>Pure rules for custom field definitions and values: validation, parsing, formatting and storage rows.</summary>
public static partial class CustomFieldRules
{
    public const int MaxText = 255;
    public const int MaxLongText = 10_000;
    public const int MaxUrl = 2000;
    public const int MaxOptions = 100;
    public const int MaxOptionLength = 100;

    [GeneratedRegex(CustomField.KeyPattern)]
    private static partial Regex KeyRegex();

    public static string TypeName(CustomFieldType t) => t switch
    {
        CustomFieldType.Text => "Text",
        CustomFieldType.LongText => "Long text",
        CustomFieldType.Number => "Number",
        CustomFieldType.Date => "Date",
        CustomFieldType.SingleSelect => "Single select",
        CustomFieldType.MultiSelect => "Multi select",
        CustomFieldType.User => "User",
        CustomFieldType.Checkbox => "Checkbox",
        CustomFieldType.Url => "URL",
        _ => t.ToString(),
    };

    public static bool HasOptions(CustomFieldType t) => t is CustomFieldType.SingleSelect or CustomFieldType.MultiSelect;

    /// <summary>A key from a name: "Customer impact" → <c>customer_impact</c>.</summary>
    public static string KeyFromName(string name)
    {
        var chars = name.Trim().ToLowerInvariant().Select(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' ? c : '_').ToArray();
        var key = Regex.Replace(new string(chars), "_+", "_").Trim('_');
        if (key.Length == 0 || !char.IsAsciiLetterLower(key[0])) key = "f_" + key;
        return key.Length > 40 ? key[..40].TrimEnd('_') : key;
    }

    /// <summary>
    /// Normalizes and checks a field definition (name, key, options, default). Throws <see cref="InvalidOperationException"/>
    /// with a readable message. Uniqueness within the project is checked by the service.
    /// </summary>
    public static void ValidateDefinition(CustomField f)
    {
        f.Name = (f.Name ?? "").Trim();
        if (f.Name.Length is 0 or > 60) throw new InvalidOperationException("A field needs a name (at most 60 characters).");
        f.Key = string.IsNullOrWhiteSpace(f.Key) ? KeyFromName(f.Name) : f.Key.Trim();
        if (!KeyRegex().IsMatch(f.Key)) throw new InvalidOperationException($"Key '{f.Key}' must be lowercase letters, digits and '_', starting with a letter (at most 40).");
        if (!Enum.IsDefined(f.Type)) throw new InvalidOperationException("Unknown field type.");
        f.Description = string.IsNullOrWhiteSpace(f.Description) ? null : f.Description.Trim();
        if (f.Description?.Length > 300) throw new InvalidOperationException("The description can be at most 300 characters.");
        if (HasOptions(f.Type))
        {
            var options = f.Options.Select(o => (o ?? "").Trim()).Where(o => o.Length > 0).ToList();
            if (options.Count == 0) throw new InvalidOperationException($"{TypeName(f.Type)} fields need at least one option.");
            if (options.Count > MaxOptions) throw new InvalidOperationException($"At most {MaxOptions} options.");
            if (options.FirstOrDefault(o => o.Length > MaxOptionLength || o.Contains(',')) is { } bad)
                throw new InvalidOperationException($"Option '{bad}' is too long or contains a comma.");
            if (options.GroupBy(o => o, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1) is { } dup)
                throw new InvalidOperationException($"Option '{dup.Key}' appears twice.");
            f.Options = options;
        }
        else f.Options = [];
        f.IssueTypes = f.IssueTypes.Distinct().Order().ToList();
        if (f.IssueTypes.Count == Enum.GetValues<IssueType>().Length) f.IssueTypes = [];
        f.DefaultValue = string.IsNullOrWhiteSpace(f.DefaultValue) ? null : f.DefaultValue.Trim();
        if (f.DefaultValue != null)
        {
            try { Parse(f, f.DefaultValue); }
            catch (InvalidOperationException ex) { throw new InvalidOperationException($"Default value: {ex.Message}"); }
            // Store the canonical form (option spelling, yyyy-MM-dd, invariant numbers).
            if (f.Type != CustomFieldType.User) f.DefaultValue = ToText(f, Parse(f, f.DefaultValue));
        }
    }

    /// <summary>
    /// Parses raw input for <paramref name="f"/>: null, a string, a number, a bool, a DateTime/DateOnly, a list of strings,
    /// an int user id, or a <see cref="JsonElement"/>. Empty input gives <see cref="FieldValue.Empty"/>. Throws
    /// <see cref="InvalidOperationException"/> for invalid values. User names are left in <see cref="FieldValue.UserName"/>.
    /// </summary>
    public static FieldValue Parse(CustomField f, object? input)
    {
        input = Unwrap(input);
        if (input is null || input is string { Length: 0 } || (input is string s0 && string.IsNullOrWhiteSpace(s0))) return FieldValue.Empty;
        var name = f.Name;
        switch (f.Type)
        {
            case CustomFieldType.Text:
            case CustomFieldType.LongText:
            {
                var text = input as string ?? throw Bad(name, "must be text");
                text = f.Type == CustomFieldType.Text ? text.Trim() : text.TrimEnd();
                var max = f.Type == CustomFieldType.Text ? MaxText : MaxLongText;
                if (text.Length > max) throw Bad(name, $"can be at most {max} characters");
                if (f.Type == CustomFieldType.Text && text.Contains('\n')) throw Bad(name, "must be a single line");
                return text.Length == 0 ? FieldValue.Empty : new FieldValue { Text = text };
            }
            case CustomFieldType.Number:
            {
                double n = input switch
                {
                    double d => d,
                    float x => x,
                    int i => i,
                    long l => l,
                    decimal m => (double)m,
                    string str when double.TryParse(str.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var p) => p,
                    _ => throw Bad(name, "must be a number"),
                };
                if (double.IsNaN(n) || double.IsInfinity(n) || Math.Abs(n) > 1e15) throw Bad(name, "must be a finite number");
                return new FieldValue { Number = n };
            }
            case CustomFieldType.Date:
            {
                DateTime d = input switch
                {
                    DateTime dt => dt.Date,
                    DateOnly od => od.ToDateTime(TimeOnly.MinValue),
                    DateTimeOffset dto => dto.Date,
                    string str when DateTime.TryParseExact(str.Trim(), ["yyyy-MM-dd", "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-ddTHH:mm:ssZ", "o"],
                        CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var p) => p.Date,
                    _ => throw Bad(name, "must be a date (yyyy-MM-dd)"),
                };
                return new FieldValue { Date = DateTime.SpecifyKind(d, DateTimeKind.Unspecified) };
            }
            case CustomFieldType.SingleSelect:
            {
                var text = (input as string ?? throw Bad(name, "must be one of its options")).Trim();
                return new FieldValue { Text = MatchOption(f, text) };
            }
            case CustomFieldType.MultiSelect:
            {
                IEnumerable<string> items = input switch
                {
                    string str => str.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                    IEnumerable e => e.Cast<object?>().Select(o => Unwrap(o) as string ?? throw Bad(name, "must be a list of options")),
                    _ => throw Bad(name, "must be a list of options"),
                };
                // Keep the field's option order, so equal sets compare equal.
                var chosen = items.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => MatchOption(f, x.Trim())).ToHashSet(StringComparer.Ordinal);
                return chosen.Count == 0 ? FieldValue.Empty : new FieldValue { Options = f.Options.Where(chosen.Contains).ToList() };
            }
            case CustomFieldType.User:
                return input switch
                {
                    int id => new FieldValue { UserId = id },
                    long l when l is > 0 and <= int.MaxValue => new FieldValue { UserId = (int)l },
                    double d when d is > 0 and <= int.MaxValue && d == Math.Floor(d) => new FieldValue { UserId = (int)d },
                    string str => new FieldValue { UserName = str.Trim().TrimStart('@') },
                    _ => throw Bad(name, "must be a username"),
                };
            case CustomFieldType.Checkbox:
                return input switch
                {
                    bool b => new FieldValue { Bool = b },
                    string str => str.Trim().ToLowerInvariant() switch
                    {
                        "true" or "yes" or "1" or "on" => new FieldValue { Bool = true },
                        "false" or "no" or "0" or "off" => new FieldValue { Bool = false },
                        _ => throw Bad(name, "must be true or false"),
                    },
                    _ => throw Bad(name, "must be true or false"),
                };
            case CustomFieldType.Url:
            {
                var text = (input as string ?? throw Bad(name, "must be a URL")).Trim();
                if (text.Length > MaxUrl) throw Bad(name, $"can be at most {MaxUrl} characters");
                if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                    throw Bad(name, "must be an http(s) URL");
                return new FieldValue { Text = text };
            }
            default:
                throw Bad(name, "has an unknown type");
        }
    }

    private static string MatchOption(CustomField f, string text) =>
        f.Options.FirstOrDefault(o => o.Equals(text, StringComparison.OrdinalIgnoreCase))
        ?? throw Bad(f.Name, $"must be one of: {string.Join(", ", f.Options)}");

    private static InvalidOperationException Bad(string field, string what) => new($"{field} {what}.");

    /// <summary>JsonElement → CLR (string, double, bool, list of objects, null); everything else unchanged.</summary>
    private static object? Unwrap(object? input) => input is not JsonElement e ? input : e.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        JsonValueKind.String => e.GetString(),
        JsonValueKind.Number => e.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Array => e.EnumerateArray().Select(x => Unwrap(x)).ToList(),
        _ => throw new InvalidOperationException("Custom field values must be strings, numbers, booleans or lists."),
    };

    /// <summary>Checks a required field has a value. Call with the final value of an issue the field applies to.</summary>
    public static void RequireValue(CustomField f, FieldValue v)
    {
        if (f.Required && v.IsEmpty) throw new InvalidOperationException($"{f.Name} is required.");
    }

    // ----- Storage -----

    /// <summary>The rows that store <paramref name="v"/> (none when empty; one per option for multi select).</summary>
    public static List<CustomFieldValue> ToRows(CustomField f, int issueId, FieldValue v)
    {
        if (v.IsEmpty) return [];
        if (f.Type == CustomFieldType.MultiSelect)
            return v.Options.Select(o => new CustomFieldValue { IssueId = issueId, FieldId = f.Id, TextValue = o }).ToList();
        return
        [
            new CustomFieldValue
            {
                IssueId = issueId, FieldId = f.Id, TextValue = v.Text, NumberValue = v.Number, DateValue = v.Date, UserId = v.UserId, BoolValue = v.Bool,
            },
        ];
    }

    /// <summary>The value stored in <paramref name="rows"/> (all of one field on one issue).</summary>
    public static FieldValue FromRows(CustomField f, IEnumerable<CustomFieldValue> rows)
    {
        var list = rows.ToList();
        if (list.Count == 0) return FieldValue.Empty;
        if (f.Type == CustomFieldType.MultiSelect)
        {
            var set = list.Select(r => r.TextValue).Where(t => t != null).ToHashSet();
            // Options removed from the field since are kept (at the end) until someone changes the value.
            return new FieldValue { Options = f.Options.Where(set.Contains).Concat(set.Where(o => !f.Options.Contains(o!)).Select(o => o!)).ToList() };
        }
        var r = list[0];
        return f.Type switch
        {
            CustomFieldType.Number => new FieldValue { Number = r.NumberValue },
            CustomFieldType.Date => new FieldValue { Date = r.DateValue },
            CustomFieldType.User => r.UserId == null ? FieldValue.Empty : new FieldValue { UserId = r.UserId },
            CustomFieldType.Checkbox => new FieldValue { Bool = r.BoolValue },
            _ => new FieldValue { Text = r.TextValue },
        };
    }

    // ----- Display -----

    /// <summary>The value as plain text (dates yyyy-MM-dd, numbers invariant, options comma-separated, users by <paramref name="userName"/>).</summary>
    public static string ToText(CustomField f, FieldValue v, Func<int, string?>? userName = null) => v.IsEmpty ? "" : f.Type switch
    {
        CustomFieldType.Number => v.Number?.ToString("0.##########", CultureInfo.InvariantCulture) ?? "",
        CustomFieldType.Date => v.Date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "",
        CustomFieldType.MultiSelect => string.Join(", ", v.Options),
        CustomFieldType.User => v.UserId is { } id ? userName?.Invoke(id) ?? $"user #{id}" : v.UserName ?? "",
        CustomFieldType.Checkbox => v.Bool == true ? "Yes" : "No",
        _ => v.Text ?? "",
    };

    /// <summary>JSON-friendly value: string, number, bool, list of strings, "yyyy-MM-dd" or the username; null when empty.</summary>
    public static object? ToJson(CustomField f, FieldValue v, Func<int, string?> userName) => v.IsEmpty ? null : f.Type switch
    {
        CustomFieldType.Number => v.Number,
        CustomFieldType.Date => v.Date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        CustomFieldType.MultiSelect => v.Options,
        CustomFieldType.User => v.UserId is { } id ? userName(id) : null,
        CustomFieldType.Checkbox => v.Bool,
        _ => v.Text,
    };

    /// <summary>The activity text for a change ("set **Severity** to **Major**").</summary>
    public static string ChangeText(CustomField f, FieldValue before, FieldValue after, Func<int, string?>? userName = null)
    {
        if (after.IsEmpty) return $"cleared **{f.Name}**";
        if (f.Type == CustomFieldType.LongText) return $"updated **{f.Name}**";
        var text = ToText(f, after, userName);
        if (text.Length > 80) text = text[..79] + "…";
        return before.IsEmpty ? $"set **{f.Name}** to **{text}**" : $"changed **{f.Name}** to **{text}**";
    }
}
