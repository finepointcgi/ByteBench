using System.Text;
using System.Text.Json;

namespace GitBench.Services.Editor.Ai;

/// <summary>A client tool offered to the model: name, description and JSON-schema properties.</summary>
public sealed record EditorToolDefinition(string Name, string Description, IReadOnlyDictionary<string, JsonElement> Properties, IReadOnlyList<string> Required);

/// <summary>
/// The assistant's tools for one request (API backend). They read only the request's <see cref="WorkspaceSnapshot"/> (this
/// repository and branch, which the user can view) plus the edits proposed earlier in the same request, so a later
/// read_file sees an earlier proposal. <c>propose_edit</c> only records a <see cref="ProposedEdit"/>; nothing is written.
/// Tool input is model output: every field is validated, and errors come back as tool errors.
/// </summary>
public sealed class EditorAiTools(EditorAiRequest request)
{
    public const int MaxReadChars = 200_000;
    public const int MaxListed = 1000;

    private readonly Dictionary<string, string?> overlay = new(StringComparer.Ordinal); // path -> proposed content (null: deleted)
    private readonly List<ProposedEdit> proposals = [];

    public IReadOnlyList<ProposedEdit> Proposals => proposals;

    public static readonly IReadOnlyList<EditorToolDefinition> Definitions =
    [
        new("list_files", "List the files in the workspace (the open branch plus the user's uncommitted changes), optionally only under a folder.",
            Props(("path_prefix", new { type = "string", description = "Only files under this folder, e.g. 'src/api'. Omit for all files." })), []),
        new("read_file", "Read a text file from the workspace. Returns the content with line numbers. Use start_line/end_line for part of a large file.",
            Props(("path", new { type = "string", description = "File path relative to the repository root, e.g. 'src/app.ts'." }),
                  ("start_line", new { type = "integer", description = "First line to return (1-based). Optional." }),
                  ("end_line", new { type = "integer", description = "Last line to return. Optional." })), ["path"]),
        new("search_code", "Search the workspace's files for text (git grep). Returns matching lines as path:line: text.",
            Props(("query", new { type = "string", description = "The text to look for (max 200 characters)." }),
                  ("regex", new { type = "boolean", description = "Treat query as a regular expression. Default false." })), ["query"]),
        new("propose_edit", "Propose a change to one file for the user to review. Give either `content` (the complete new file, also for new files), or `find` and `replace` (replace one exact, unique occurrence), or `delete: true`. Nothing is applied until the user accepts.",
            Props(("path", new { type = "string", description = "File path relative to the repository root." }),
                  ("content", new { type = "string", description = "The complete new content of the file." }),
                  ("find", new { type = "string", description = "Exact text to replace; must occur exactly once in the current file." }),
                  ("replace", new { type = "string", description = "Replacement for `find`." }),
                  ("delete", new { type = "boolean", description = "Delete the file." }),
                  ("summary", new { type = "string", description = "One line describing the change." })), ["path"]),
    ];

    /// <summary>Runs a tool. Returns the result text and whether it is an error (sent back as an error tool_result).</summary>
    public async Task<(string Content, bool IsError)> ExecuteAsync(string name, JsonElement input, CancellationToken ct)
    {
        try
        {
            if (input.ValueKind != JsonValueKind.Object) return ("Tool input must be a JSON object.", true);
            return name switch
            {
                "list_files" => (await Task.Run(() => ListFiles(OptString(input, "path_prefix")), ct), false),
                "read_file" => await Task.Run(() => ReadFile(ReqString(input, "path"), OptInt(input, "start_line"), OptInt(input, "end_line")), ct),
                "search_code" => await SearchAsync(ReqString(input, "query"), OptBool(input, "regex") ?? false, ct),
                "propose_edit" => await Task.Run(() => Propose(input), ct),
                _ => ($"Unknown tool '{name}'.", true),
            };
        }
        catch (InvalidOperationException ex) { return (ex.Message, true); }
        catch (FormatException ex) { return (ex.Message, true); }
    }

    /// <summary>A short description of a call for the chat panel's progress list.</summary>
    public static string Describe(string name, JsonElement input)
    {
        string? S(string k) => input.ValueKind == JsonValueKind.Object && input.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        return name switch
        {
            "list_files" => S("path_prefix") is { Length: > 0 } p ? $"Listing files in {p}" : "Listing files",
            "read_file" => $"Reading {S("path")}",
            "search_code" => $"Searching for \"{S("query")}\"",
            "propose_edit" => $"Proposing a change to {S("path")}",
            _ => name,
        };
    }

    /// <summary>The file as this request sees it: the workspace plus earlier proposals. Null text: missing or not editable.</summary>
    public (bool Exists, string? Text) Current(string path)
    {
        if (overlay.TryGetValue(path, out var o)) return (o != null, o);
        var f = request.Snapshot.Read(path);
        return (f.Exists, f.Text);
    }

    private string ListFiles(string? prefix)
    {
        var p = string.IsNullOrWhiteSpace(prefix) ? "" : EditorPaths.Normalize(prefix) + "/";
        var files = request.Snapshot.ListFiles().Select(f => f.Path).Where(f => !overlay.TryGetValue(f, out var o) || o != null)
            .Concat(overlay.Where(o => o.Value != null).Select(o => o.Key)).Distinct()
            .Where(f => f.StartsWith(p, StringComparison.Ordinal)).OrderBy(f => f, StringComparer.Ordinal).ToList();
        if (files.Count == 0) return p.Length == 0 ? "The workspace has no files." : $"No files under {p}";
        var sb = new StringBuilder();
        foreach (var f in files.Take(MaxListed)) sb.AppendLine(f);
        if (files.Count > MaxListed) sb.AppendLine($"… and {files.Count - MaxListed} more (use path_prefix to narrow down).");
        return sb.ToString().TrimEnd();
    }

    private (string, bool) ReadFile(string path, int? start, int? end)
    {
        var p = EditorPaths.Normalize(path);
        var (exists, text) = Current(p);
        if (!exists) return ($"'{p}' doesn't exist in this workspace.", true);
        if (text == null) return ($"'{p}' is a binary or large file and can't be read.", true);
        var lines = text.Split('\n');
        var from = Math.Max(1, start ?? 1);
        var to = Math.Min(lines.Length, end ?? lines.Length);
        if (from > to) return ($"'{p}' has {lines.Length} lines.", true);
        var sb = new StringBuilder();
        for (var i = from; i <= to; i++)
        {
            sb.Append(i).Append('\t').Append(lines[i - 1]).Append('\n');
            if (sb.Length > MaxReadChars) { sb.Append($"… (truncated at line {i}; read more with start_line={i + 1})\n"); break; }
        }
        var boundary = EditorAiPrompts.NewBoundary();
        return (EditorAiPrompts.Untrusted("FILE", $"{p} (lines {from}-{to} of {lines.Length})", sb.ToString().TrimEnd('\n'), boundary), false);
    }

    private async Task<(string, bool)> SearchAsync(string query, bool regex, CancellationToken ct)
    {
        if (query.Length is 0 or > 200) return ("The query must be 1 to 200 characters.", true);
        if (request.Search == null) return ("Search is not available.", true);
        var (hits, error) = await request.Search(query, regex, ct);
        if (error != null) return (error, true);
        if (hits.Count == 0) return ("No matches.", false);
        var sb = new StringBuilder();
        foreach (var h in hits.Take(100)) sb.Append(h.Path).Append(':').Append(h.Line).Append(": ").AppendLine(h.Text.Length > 300 ? h.Text[..300] + "…" : h.Text);
        if (hits.Count > 100) sb.AppendLine($"… {hits.Count - 100} more matches.");
        var boundary = EditorAiPrompts.NewBoundary();
        return (EditorAiPrompts.Untrusted("SEARCH RESULTS", query, sb.ToString().TrimEnd(), boundary), false);
    }

    private (string, bool) Propose(JsonElement input)
    {
        var p = EditorPaths.Normalize(ReqString(input, "path"));
        var content = OptString(input, "content");
        var find = OptString(input, "find");
        var replace = OptString(input, "replace");
        var delete = OptBool(input, "delete") ?? false;
        var summary = OptString(input, "summary");
        var (exists, current) = Current(p);
        if (exists && current == null) return ($"'{p}' is a binary or large file and can't be edited.", true);

        ProposedEdit edit;
        if (delete)
        {
            if (!exists) return ($"'{p}' doesn't exist.", true);
            edit = new ProposedEdit { Path = p, Kind = ProposedEditKind.Delete, OriginalText = current, Summary = summary };
        }
        else if (content != null)
        {
            EditorPaths.EnsureEditableSize(content);
            if (exists && current == content) return ("That content is the same as the current file; nothing to propose.", true);
            edit = new ProposedEdit { Path = p, Kind = exists ? ProposedEditKind.Modify : ProposedEditKind.Create, OriginalText = current, NewText = content, Summary = summary };
        }
        else if (find != null && replace != null)
        {
            if (!exists) return ($"'{p}' doesn't exist; give `content` to create it.", true);
            if (find.Length == 0) return ("`find` can't be empty.", true);
            var first = current!.IndexOf(find, StringComparison.Ordinal);
            if (first < 0) return ($"`find` text was not found in '{p}'. Read the file again and copy the text exactly.", true);
            if (current.IndexOf(find, first + 1, StringComparison.Ordinal) >= 0) return ($"`find` text occurs more than once in '{p}'; include more context.", true);
            var updated = current[..first] + replace + current[(first + find.Length)..];
            EditorPaths.EnsureEditableSize(updated);
            edit = new ProposedEdit { Path = p, Kind = ProposedEditKind.Modify, OriginalText = current, NewText = updated, Summary = summary };
        }
        else return ("Give `content`, or `find` and `replace`, or `delete: true`.", true);

        // A later proposal for the same file supersedes the earlier one (and is based on the workspace, not on it).
        var earlier = proposals.FirstOrDefault(x => x.Path == p);
        if (earlier != null)
        {
            proposals.Remove(earlier);
            var existedInWorkspace = earlier.OriginalText != null; // proposals only ever touch text files
            overlay[p] = edit.Kind == ProposedEditKind.Delete ? null : edit.NewText;
            if (edit.Kind == ProposedEditKind.Delete && !existedInWorkspace)
                return ($"Withdrew the earlier proposal to create '{p}'.", false);
            edit = new ProposedEdit
            {
                Path = p, Summary = edit.Summary ?? earlier.Summary, NewText = edit.NewText, OriginalText = earlier.OriginalText,
                Kind = edit.Kind == ProposedEditKind.Delete ? ProposedEditKind.Delete
                    : existedInWorkspace ? ProposedEditKind.Modify : ProposedEditKind.Create,
            };
        }
        overlay[p] = edit.Kind == ProposedEditKind.Delete ? null : edit.NewText;
        proposals.Add(edit);
        return ($"Proposed {edit.Kind.ToString().ToLowerInvariant()} of '{p}'. The user will review it; you can continue or finish.", false);
    }

    // ---------- input parsing ----------

    private static string ReqString(JsonElement o, string name) =>
        OptString(o, name) ?? throw new FormatException($"Missing required string field '{name}'.");

    private static string? OptString(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null
            ? v.ValueKind == JsonValueKind.String ? v.GetString() : throw new FormatException($"Field '{name}' must be a string.")
            : null;

    private static int? OptInt(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null
            ? v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : throw new FormatException($"Field '{name}' must be an integer.")
            : null;

    private static bool? OptBool(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null
            ? v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : throw new FormatException($"Field '{name}' must be true or false.")
            : null;

    private static Dictionary<string, JsonElement> Props(params (string Name, object Schema)[] props) =>
        props.ToDictionary(p => p.Name, p => JsonSerializer.SerializeToElement(p.Schema));
}
