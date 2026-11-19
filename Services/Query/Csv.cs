using System.Text;
using ByteBench.Data;
using Microsoft.EntityFrameworkCore;

namespace ByteBench.Services.Query;

/// <summary>RFC 4180 CSV: writing (with spreadsheet formula-injection escaping, like the audit export) and reading.</summary>
public static class Csv
{
    /// <summary>One cell: a value starting like a formula (= + - @ tab CR) gets a leading <c>'</c>; quoted when needed.</summary>
    public static string Cell(string? v)
    {
        if (string.IsNullOrEmpty(v)) return "";
        if (v[0] is '=' or '+' or '-' or '@' or '\t' or '\r') v = "'" + v;
        return v.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;
    }

    /// <summary>Undoes <see cref="Cell"/>'s formula escaping on import: <c>'=x</c> → <c>=x</c>.</summary>
    public static string Unescape(string v) => v.Length > 1 && v[0] == '\'' && v[1] is '=' or '+' or '-' or '@' or '\t' or '\r' ? v[1..] : v;

    public static void AppendRow(StringBuilder sb, IEnumerable<string?> cells) => sb.AppendJoin(',', cells.Select(Cell)).Append("\r\n");

    /// <summary>UTF-8 bytes with a byte-order mark, so spreadsheet apps detect the encoding.</summary>
    public static byte[] Utf8WithBom(string text) => [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(text)];

    /// <summary>
    /// Parses CSV text (an optional BOM, CRLF or LF line ends, quoted cells with <c>""</c> escapes and line breaks).
    /// Blank lines are skipped. Throws <see cref="InvalidOperationException"/> for an unterminated quote or too many rows/columns.
    /// </summary>
    public static List<List<string>> Parse(string text, int maxRows = int.MaxValue, int maxColumns = 200)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var cell = new StringBuilder();
        var i = text.Length > 0 && text[0] == '﻿' ? 1 : 0;
        var line = 1;
        var quoted = false;
        var quoteLine = 0;

        void EndCell()
        {
            row.Add(cell.ToString());
            cell.Clear();
            if (row.Count > maxColumns) throw new InvalidOperationException($"Line {line} has more than {maxColumns} columns.");
        }
        void EndRow()
        {
            EndCell();
            if (row.Count > 1 || row[0].Length > 0)
            {
                rows.Add(row);
                if (rows.Count > maxRows) throw new InvalidOperationException($"The file has more than {maxRows - 1} rows; split it into smaller files.");
            }
            row = [];
        }

        for (; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
                    else quoted = false;
                }
                else
                {
                    if (c == '\n') line++;
                    cell.Append(c);
                }
                continue;
            }
            switch (c)
            {
                case '"' when cell.Length == 0: quoted = true; quoteLine = line; break;
                case ',': EndCell(); break;
                case '\r' when i + 1 < text.Length && text[i + 1] == '\n': break;
                case '\r' or '\n': EndRow(); line++; break;
                default: cell.Append(c); break;
            }
        }
        if (quoted) throw new InvalidOperationException($"The quoted cell starting on line {quoteLine} is never closed.");
        if (cell.Length > 0 || row.Count > 0) EndRow();
        return rows;
    }
}

/// <summary>A column of the issue CSV export: its id (as in <c>?columns=</c>), header and value.</summary>
public sealed record IssueCsvColumn(string Id, string Header, Func<Issue, string?> Value);

/// <summary>CSV export of issue lists and GQL results. Rows are loaded by id, in the order given.</summary>
public sealed class IssueCsvExporter(IDbContextFactory<AppDbContext> dbFactory)
{
    public const int MaxRows = 10_000;

    private static string? Date(DateTime? d) => d?.ToString("yyyy-MM-dd");
    private static string? Time(DateTime? d) => d is { } t ? DateTime.SpecifyKind(t, DateTimeKind.Utc).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'") : null;

    public static readonly IReadOnlyList<IssueCsvColumn> Columns =
    [
        new("key", "Key", i => i.Key),
        new("project", "Project", i => i.Project.Key),
        new("title", "Title", i => i.Title),
        new("type", "Type", i => i.Type.ToString()),
        new("status", "Status", Display.StatusName),
        new("statusCategory", "Status category", i => i.Status.Name()),
        new("priority", "Priority", i => i.Priority.ToString()),
        new("assignee", "Assignee", i => i.Assignee?.UserName),
        new("reporter", "Reporter", i => i.Reporter?.UserName),
        new("labels", "Labels", i => string.Join(", ", i.Labels.Select(l => l.Name).Order(StringComparer.OrdinalIgnoreCase))),
        new("milestone", "Milestone", i => i.Milestone?.Title),
        new("sprint", "Sprint", i => i.Sprint?.Name),
        new("parent", "Parent", i => i.Parent is { } p ? $"{p.Project.Key}-{p.Number}" : null),
        new("storyPoints", "Story points", i => i.StoryPoints?.ToString()),
        new("due", "Due date", i => Date(i.DueDate)),
        new("created", "Created", i => Time(i.CreatedAt)),
        new("updated", "Updated", i => Time(i.UpdatedAt)),
        new("resolved", "Resolved", i => Time(i.ClosedAt)),
        new("description", "Description", i => i.Description),
    ];

    public static readonly IReadOnlyList<string> DefaultColumns =
        ["key", "title", "type", "status", "priority", "assignee", "reporter", "labels", "milestone", "sprint", "parent", "storyPoints", "due", "created", "updated"];

    /// <summary>The columns named in <paramref name="ids"/> (comma-separated, case-insensitive; unknown ones ignored), or the defaults.</summary>
    public static List<IssueCsvColumn> Pick(string? ids)
    {
        var wanted = (ids ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var picked = wanted.Select(w => Columns.FirstOrDefault(c => c.Id.Equals(w, StringComparison.OrdinalIgnoreCase))).OfType<IssueCsvColumn>().Distinct().ToList();
        return picked.Count > 0 ? picked : DefaultColumns.Select(d => Columns.First(c => c.Id == d)).ToList();
    }

    /// <summary>The CSV text (header row first) for these issues. The caller has already checked that they may be viewed.</summary>
    public async Task<string> ExportAsync(IReadOnlyList<int> issueIds, IReadOnlyList<IssueCsvColumn> columns)
    {
        var sb = new StringBuilder();
        Csv.AppendRow(sb, columns.Select(c => c.Header));
        await using var db = await dbFactory.CreateDbContextAsync();
        foreach (var chunk in issueIds.Take(MaxRows).Chunk(500))
        {
            var ids = chunk.ToList();
            var issues = await db.Issues.AsNoTracking().Where(i => ids.Contains(i.Id))
                .Include(i => i.Project).Include(i => i.Assignee).Include(i => i.Reporter).Include(i => i.Labels).Include(i => i.Milestone)
                .Include(i => i.Sprint).Include(i => i.WorkflowStatus).Include(i => i.Parent).ThenInclude(p => p!.Project)
                .AsSplitQuery().ToDictionaryAsync(i => i.Id);
            foreach (var id in ids)
                if (issues.TryGetValue(id, out var issue)) Csv.AppendRow(sb, columns.Select(c => c.Value(issue)));
        }
        return sb.ToString();
    }
}
