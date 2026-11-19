using System.Globalization;
using ByteBench.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ByteBench.Services.Query;

/// <summary>Limits for CSV import (configuration section <c>CsvImport</c>).</summary>
public sealed class CsvImportOptions
{
    /// <summary>Largest file accepted, in bytes (default 2 MB).</summary>
    public long MaxBytes { get; set; } = 2 * 1024 * 1024;
    /// <summary>Most data rows in one import (default 1000).</summary>
    public int MaxRows { get; set; } = 1000;
}

/// <summary>The issue fields a CSV column can be mapped to.</summary>
public enum CsvImportField { Key, Title, Description, Type, Priority, Status, Assignee, Labels, StoryPoints, DueDate, Parent }

/// <summary>An uploaded CSV: the header row and the data rows (each padded to the header's width).</summary>
public sealed record CsvTable(IReadOnlyList<string> Headers, IReadOnlyList<IReadOnlyList<string>> Rows);

/// <summary>One data row after validation. <see cref="Line"/> is the row number in the file (the header is row 1).</summary>
public sealed class CsvImportRow
{
    public int Line { get; init; }
    public string? Key { get; set; }
    public string Title { get; set; } = "";
    public string? Description { get; set; }
    public IssueType Type { get; set; } = IssueType.Task;
    public IssuePriority Priority { get; set; } = IssuePriority.Medium;
    public WorkflowStatus? Status { get; set; }
    public User? Assignee { get; set; }
    public List<Label> Labels { get; set; } = [];
    public int? StoryPoints { get; set; }
    public DateTime? DueDate { get; set; }
    /// <summary>The parent as written (an existing issue's key, or another row's <see cref="Key"/>).</summary>
    public string? ParentKey { get; set; }
    public int? ExistingParentId { get; set; }
    public CsvImportRow? ParentRow { get; set; }
    public List<string> Errors { get; } = [];
    public bool IsValid => Errors.Count == 0;
}

public sealed record CsvImportPreview(IReadOnlyList<CsvImportRow> Rows)
{
    public int ErrorRows => Rows.Count(r => !r.IsValid);
}

public sealed record CsvImportResult(int Count, IReadOnlyList<string> Keys);

/// <summary>
/// Imports issues into a project from CSV (Maintainer+): read the file, map columns to fields, preview with per-row
/// errors, then create every issue in one transaction (all or nothing). Each issue gets an "imported from CSV" activity
/// entry and the import is audited (<c>issue.imported</c>). Imports don't send notifications or webhooks.
/// </summary>
public sealed class CsvImportService(IDbContextFactory<AppDbContext> dbFactory, ProjectAccess access, Audit.AuditLog audit, LiveEvents live,
    IOptions<CsvImportOptions> options)
{
    public CsvImportOptions Limits => options.Value;

    /// <summary>Parses an uploaded file (UTF-8, optional BOM). Throws <see cref="InvalidOperationException"/> with a readable message.</summary>
    public async Task<CsvTable> ReadAsync(Stream stream)
    {
        using var reader = new StreamReader(stream, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var buffer = new char[Limits.MaxBytes + 1];
        var read = await reader.ReadBlockAsync(buffer.AsMemory());
        if (read > Limits.MaxBytes) throw new InvalidOperationException($"The file is larger than {Limits.MaxBytes / 1024} KB.");
        return Read(new string(buffer, 0, read));
    }

    public CsvTable Read(string text)
    {
        var rows = Csv.Parse(text, Limits.MaxRows + 1);
        if (rows.Count == 0) throw new InvalidOperationException("The file is empty.");
        var headers = rows[0].Select(h => h.Trim()).ToList();
        if (headers.All(h => h.Length == 0)) throw new InvalidOperationException("The first row must name the columns.");
        var data = rows.Skip(1).Select(r => (IReadOnlyList<string>)r.Concat(Enumerable.Repeat("", Math.Max(0, headers.Count - r.Count))).ToList()).ToList();
        if (data.Count == 0) throw new InvalidOperationException("The file has a header row but no issues.");
        return new CsvTable(headers, data);
    }

    private static string Norm(string s) => new(s.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static readonly Dictionary<string, CsvImportField> HeaderNames = new()
    {
        ["key"] = CsvImportField.Key, ["issuekey"] = CsvImportField.Key, ["id"] = CsvImportField.Key,
        ["title"] = CsvImportField.Title, ["summary"] = CsvImportField.Title, ["name"] = CsvImportField.Title,
        ["description"] = CsvImportField.Description, ["body"] = CsvImportField.Description,
        ["type"] = CsvImportField.Type, ["issuetype"] = CsvImportField.Type,
        ["priority"] = CsvImportField.Priority,
        ["status"] = CsvImportField.Status, ["state"] = CsvImportField.Status,
        ["assignee"] = CsvImportField.Assignee, ["assignedto"] = CsvImportField.Assignee,
        ["labels"] = CsvImportField.Labels, ["label"] = CsvImportField.Labels, ["tags"] = CsvImportField.Labels,
        ["storypoints"] = CsvImportField.StoryPoints, ["points"] = CsvImportField.StoryPoints, ["estimate"] = CsvImportField.StoryPoints,
        ["duedate"] = CsvImportField.DueDate, ["due"] = CsvImportField.DueDate,
        ["parent"] = CsvImportField.Parent, ["parentkey"] = CsvImportField.Parent, ["epic"] = CsvImportField.Parent, ["epiclink"] = CsvImportField.Parent,
    };

    /// <summary>A starting mapping from the header names (the export's headers map one to one).</summary>
    public static Dictionary<CsvImportField, int> GuessMapping(IReadOnlyList<string> headers)
    {
        var map = new Dictionary<CsvImportField, int>();
        for (var i = 0; i < headers.Count; i++)
            if (HeaderNames.TryGetValue(Norm(headers[i]), out var f)) map.TryAdd(f, i);
        return map;
    }

    /// <summary>Validates every row against the project (statuses, labels, users, parents) without changing anything.</summary>
    public async Task<CsvImportPreview> PreviewAsync(int projectId, CsvTable table, IReadOnlyDictionary<CsvImportField, int> map)
    {
        await access.RequireRoleAsync(projectId, ProjectRole.Maintainer, "import issues");
        if (!map.ContainsKey(CsvImportField.Title)) throw new InvalidOperationException("Map a column to Title.");
        await using var db = await dbFactory.CreateDbContextAsync();
        var project = await db.Projects.AsNoTracking().FirstAsync(p => p.Id == projectId);
        var statuses = (await WorkflowService.LoadAsync(dbFactory, projectId)).Statuses;
        var chain = project.GroupId is { } gid ? await ProjectAccess.GroupChainAsync(db, gid) : [];
        var labels = await db.Labels.AsNoTracking().Where(l => l.ProjectId == projectId || (l.GroupId != null && chain.Contains(l.GroupId.Value))).ToListAsync();
        var users = new Dictionary<string, User?>(StringComparer.OrdinalIgnoreCase);

        string? Cell(IReadOnlyList<string> row, CsvImportField f) =>
            map.TryGetValue(f, out var col) && col >= 0 && col < row.Count && Csv.Unescape(row[col].Trim()) is { Length: > 0 } v ? v : null;

        var rows = new List<CsvImportRow>();
        for (var r = 0; r < table.Rows.Count; r++)
        {
            var cells = table.Rows[r];
            var row = new CsvImportRow { Line = r + 2 };
            rows.Add(row);

            row.Key = Cell(cells, CsvImportField.Key)?.ToUpperInvariant();
            row.Title = Cell(cells, CsvImportField.Title) ?? "";
            if (row.Title.Length == 0) row.Errors.Add("Title is empty.");
            else if (row.Title.Length > 200) row.Errors.Add("Title is longer than 200 characters.");
            row.Description = map.TryGetValue(CsvImportField.Description, out var dc) && dc < cells.Count && cells[dc].Trim().Length > 0 ? Csv.Unescape(cells[dc]) : null;

            if (Cell(cells, CsvImportField.Type) is { } type)
            {
                if (ParseEnum<IssueType>(type) is { } t) row.Type = t;
                else row.Errors.Add($"Unknown type '{type}' (use {string.Join(", ", Enum.GetNames<IssueType>())}).");
            }
            if (Cell(cells, CsvImportField.Priority) is { } prio)
            {
                if (ParseEnum<IssuePriority>(prio) is { } p) row.Priority = p;
                else row.Errors.Add($"Unknown priority '{prio}' (use {string.Join(", ", Enum.GetNames<IssuePriority>())}).");
            }
            var status = Cell(cells, CsvImportField.Status);
            row.Status = status == null
                ? statuses.FirstOrDefault(s => s.Category == IssueStatus.Todo) ?? statuses.FirstOrDefault(s => s.Category != IssueStatus.Done)
                : statuses.FirstOrDefault(s => s.Name.Equals(status, StringComparison.OrdinalIgnoreCase))
                  ?? (ParseEnum<IssueStatus>(status) is { } cat ? statuses.FirstOrDefault(s => s.Category == cat) : null);
            if (row.Status == null) row.Errors.Add(status == null ? "The workflow has no open status." : $"Unknown status '{status}' in this project's workflow.");

            if (Cell(cells, CsvImportField.Assignee) is { } who)
            {
                if (!users.TryGetValue(who, out var user))
                {
                    var upper = who.ToUpperInvariant();
                    user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.NormalizedUserName == upper || u.NormalizedEmail == upper);
                    if (user != null && !await access.CanAsync(projectId, Permission.ViewProject, user.Id)) user = null;
                    users[who] = user;
                }
                if (user == null) row.Errors.Add($"'{who}' is not a user who can see this project.");
                row.Assignee = user;
            }
            if (Cell(cells, CsvImportField.Labels) is { } names)
                foreach (var name in names.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (labels.FirstOrDefault(l => l.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) is { } label) row.Labels.Add(label);
                    else row.Errors.Add($"Unknown label '{name}'.");
                }
            if (Cell(cells, CsvImportField.StoryPoints) is { } sp)
            {
                if (int.TryParse(sp, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n is >= 0 and <= 100) row.StoryPoints = n;
                else row.Errors.Add($"Story points must be a whole number from 0 to 100, not '{sp}'.");
            }
            if (Cell(cells, CsvImportField.DueDate) is { } due)
            {
                if (DateTime.TryParseExact(due, ["yyyy-MM-dd", "yyyy/MM/dd", "yyyy-MM-dd'T'HH:mm:ss'Z'", "yyyy-MM-dd HH:mm"], CultureInfo.InvariantCulture,
                        DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d))
                    row.DueDate = DateTime.SpecifyKind(d.Date, DateTimeKind.Utc);
                else row.Errors.Add($"Due date '{due}' is not a date like 2026-01-31.");
            }
            row.ParentKey = Cell(cells, CsvImportField.Parent)?.ToUpperInvariant();
        }

        // Keys must be unique; parents are another row (by Key) or an existing issue of this project.
        foreach (var dup in rows.Where(r => r.Key != null).GroupBy(r => r.Key).Where(g => g.Count() > 1).SelectMany(g => g.Skip(1)))
            dup.Errors.Add($"Key {dup.Key} appears more than once in the file.");
        var byKey = rows.Where(r => r.Key != null).GroupBy(r => r.Key!).ToDictionary(g => g.Key, g => g.First());
        foreach (var row in rows)
        {
            if (row.ParentKey is not { } pk)
            {
                if (row.Type == IssueType.Subtask) row.Errors.Add("A sub-task needs a parent.");
                continue;
            }
            IssueType parentType;
            if (byKey.TryGetValue(pk, out var parentRow))
            {
                if (parentRow == row) { row.Errors.Add("An issue can't be its own parent."); continue; }
                row.ParentRow = parentRow;
                parentType = parentRow.Type;
            }
            else
            {
                var dash = pk.LastIndexOf('-');
                var number = dash > 0 && pk[..dash] == project.Key && int.TryParse(pk[(dash + 1)..], out var n) ? n : -1;
                var parent = await db.Issues.AsNoTracking().Where(i => i.ProjectId == projectId && i.Number == number).Select(i => new { i.Id, i.Type }).FirstOrDefaultAsync();
                if (parent == null) { row.Errors.Add($"Parent {pk} is neither an issue of {project.Key} nor a Key in this file."); continue; }
                row.ExistingParentId = parent.Id;
                parentType = parent.Type;
            }
            if (!HierarchyRules.CanBeChildOf(row.Type, parentType)) row.Errors.Add(HierarchyRules.ParentRule(row.Type));
        }
        return new CsvImportPreview(rows);
    }

    /// <summary>
    /// Validates again, then creates every row's issue in one transaction (all or nothing). Hierarchy rules (depth,
    /// cycles) are checked inside the transaction once all issues exist. Returns the new keys in file order.
    /// </summary>
    public async Task<CsvImportResult> ImportAsync(int projectId, CsvTable table, IReadOnlyDictionary<CsvImportField, int> map, string? fileName = null)
    {
        var preview = await PreviewAsync(projectId, table, map);
        if (preview.ErrorRows > 0)
            throw new InvalidOperationException($"{preview.ErrorRows} row{(preview.ErrorRows == 1 ? " has" : "s have")} errors; nothing was imported.");
        var actorId = await access.ResolveUserIdAsync() ?? throw new ForbiddenException("Sign in to import issues.");

        await using var db = await dbFactory.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();
        var project = await db.Projects.FirstAsync(p => p.Id == projectId);
        var sort = await db.Issues.Where(i => i.ProjectId == projectId).MaxAsync(i => (double?)i.SortOrder) ?? 0;
        var labelIds = preview.Rows.SelectMany(r => r.Labels).Select(l => l.Id).Distinct().ToList();
        var labels = await db.Labels.Where(l => labelIds.Contains(l.Id)).ToDictionaryAsync(l => l.Id);
        var now = DateTime.UtcNow;

        var created = new List<(CsvImportRow Row, Issue Issue)>();
        foreach (var row in preview.Rows)
        {
            var status = row.Status!;
            var issue = new Issue
            {
                ProjectId = projectId,
                Number = project.NextIssueNumber++,
                Title = row.Title,
                Description = row.Description,
                Type = row.Type,
                Priority = row.Priority,
                Status = status.Category,
                WorkflowStatusId = status.Id,
                ClosedAt = status.Category.IsClosed() ? now : null,
                AssigneeId = row.Assignee?.Id,
                ReporterId = actorId,
                StoryPoints = row.StoryPoints,
                DueDate = row.DueDate,
                ParentId = row.ExistingParentId,
                SortOrder = ++sort,
                CreatedAt = now,
                UpdatedAt = now,
                Labels = row.Labels.Select(l => labels[l.Id]).ToList(),
            };
            issue.Activities.Add(new Activity { ActorId = actorId, Text = "imported from CSV", CreatedAt = now });
            db.Issues.Add(issue);
            created.Add((row, issue));
        }
        await db.SaveChangesAsync();

        // Parents that are other rows of the file, now that they have ids; then the hierarchy rules for every child.
        var issueOf = created.ToDictionary(c => c.Row, c => c.Issue);
        foreach (var (row, issue) in created.Where(c => c.Row.ParentRow != null)) issue.ParentId = issueOf[row.ParentRow!].Id;
        await db.SaveChangesAsync();
        foreach (var (row, issue) in created.Where(c => c.Issue.ParentId != null))
        {
            try { await HierarchyRules.ValidateAsync(db, issue); }
            catch (InvalidOperationException ex) { throw new InvalidOperationException($"Row {row.Line}: {ex.Message} Nothing was imported."); }
            await HierarchyRules.RecordAsync(db, issue, null, actorId);
        }
        await db.SaveChangesAsync();
        await tx.CommitAsync();

        var keys = created.Select(c => $"{project.Key}-{c.Issue.Number}").ToList();
        await audit.RecordAsync("issue.imported", "project", projectId, projectId,
            new { file = fileName, count = keys.Count, first = keys.FirstOrDefault(), last = keys.LastOrDefault() }, actorId);
        live.Publish([.. created.Select(c => (LiveEvent)new IssueChanged(projectId, c.Issue.Id)), new BoardChanged(projectId), new ActivityChanged()]);
        return new CsvImportResult(keys.Count, keys);
    }

    private static T? ParseEnum<T>(string text) where T : struct, Enum
    {
        var n = Norm(text);
        foreach (var v in Enum.GetValues<T>())
        {
            if (Norm(v.ToString()) == n) return v;
            if (v is IssueStatus s && Norm(s.Name()) == n) return v;
        }
        return null;
    }
}
