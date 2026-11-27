using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ByteBench.Services.Editor;
using ByteBench.Services.Editor.Ai;

namespace ByteBench.Services.Workspaces;

/// <summary>
/// The assistant's tools for one workspace request: files and a shell, run inside the workspace container with <c>docker exec</c> as
/// the unprivileged user <c>bbai</c> (same group as the developer, but no access to the developer's git credentials or git config).
/// Every path is validated here (no absolute paths, no <c>..</c>, nothing under <c>.git</c>) and again in the container after symlinks are
/// resolved; every command has a timeout and its output is capped. Output is untrusted data and is wrapped as such. The tools can't commit
/// or push: there are no credentials for <c>bbai</c>, and commands that try (<c>git commit/push/…</c>) are refused up front.
/// </summary>
public sealed partial class WorkspaceAiTools(IWorkspaceBackend backend, string containerId, string slug, WorkspaceAiOptions options, AiProposalCollector? proposals = null)
{
    public const int MaxListed = 1000;
    public const int MaxCommandChars = 4000;

    public string Root { get; } = WorkspaceLifecycle.WorkDir(slug);

    /// <summary>Counts per tool, for the audit summary (never the commands or contents).</summary>
    public Dictionary<string, int> Used { get; } = [];
    /// <summary>The first word of each command that was run (for the audit summary).</summary>
    public List<string> CommandVerbs { get; } = [];
    /// <summary>Paths written or deleted straight in the working tree (apply-directly mode; for the audit summary).</summary>
    public List<string> ChangedPaths { get; } = [];
    /// <summary>In review mode: where write_file, edit_file and delete_file put their changes instead of the working tree.</summary>
    public AiProposalCollector? Proposals { get; } = proposals;

    public static readonly IReadOnlyList<EditorToolDefinition> Definitions =
    [
        new("list_files", "List the files in the workspace's checkout (tracked and untracked, not ignored), optionally only under a folder.",
            Props(("path_prefix", new { type = "string", description = "Only files under this folder, e.g. 'src/api'. Omit for all files." })), []),
        new("read_file", "Read a text file from the workspace. Returns the content with line numbers. Use start_line/end_line for part of a large file.",
            Props(("path", new { type = "string", description = "File path relative to the repository root." }),
                  ("start_line", new { type = "integer", description = "First line to return (1-based). Optional." }),
                  ("end_line", new { type = "integer", description = "Last line to return. Optional." })), ["path"]),
        new("write_file", "Create or overwrite a file in the workspace with the given complete content. In review mode the change is proposed to the developer and applied only when they accept it; otherwise it is made right away.",
            Props(("path", new { type = "string", description = "File path relative to the repository root." }),
                  ("content", new { type = "string", description = "The complete new content of the file." })), ["path", "content"]),
        new("edit_file", "Replace one exact, unique occurrence of `find` with `replace` in a file.",
            Props(("path", new { type = "string", description = "File path relative to the repository root." }),
                  ("find", new { type = "string", description = "Exact text to replace; must occur exactly once." }),
                  ("replace", new { type = "string", description = "Replacement text." })), ["path", "find", "replace"]),
        new("delete_file", "Delete one file from the workspace (or propose its deletion in review mode).",
            Props(("path", new { type = "string", description = "File path relative to the repository root." })), ["path"]),
        new("search_code", "Search the workspace's files for text (git grep). Returns matching lines as path:line: text.",
            Props(("query", new { type = "string", description = "The text to look for (max 200 characters)." }),
                  ("regex", new { type = "boolean", description = "Treat query as an extended regular expression. Default false." })), ["query"]),
        new("run_command", "Run a shell command in the workspace's repository directory (build, test, run a script, inspect). It runs as an unprivileged user with a time limit and its output is truncated. You cannot commit or push; leave that to the user.",
            Props(("command", new { type = "string", description = "The shell command line." }),
                  ("timeout_seconds", new { type = "integer", description = "Time limit in seconds (default and maximum come from the server's settings)." })), ["command"]),
    ];

    public async Task<(string Content, bool IsError)> ExecuteAsync(string name, JsonElement input, CancellationToken ct)
    {
        try
        {
            if (input.ValueKind != JsonValueKind.Object) return ("Tool input must be a JSON object.", true);
            Used[name] = Used.GetValueOrDefault(name) + 1;
            return name switch
            {
                "list_files" => await ListFilesAsync(OptString(input, "path_prefix"), ct),
                "read_file" => await ReadFileAsync(ReqString(input, "path"), OptInt(input, "start_line"), OptInt(input, "end_line"), ct),
                "write_file" => await WriteFileAsync(ReqString(input, "path"), ReqString(input, "content"), ct),
                "edit_file" => await EditFileAsync(ReqString(input, "path"), ReqString(input, "find"), ReqString(input, "replace"), ct),
                "delete_file" => await DeleteFileAsync(ReqString(input, "path"), ct),
                "search_code" => await SearchAsync(ReqString(input, "query"), OptBool(input, "regex") ?? false, ct),
                "run_command" => await RunCommandAsync(ReqString(input, "command"), OptInt(input, "timeout_seconds"), ct),
                _ => ($"Unknown tool '{name}'.", true),
            };
        }
        catch (InvalidOperationException ex) { return (ex.Message, true); }
        catch (FormatException ex) { return (ex.Message, true); }
    }

    public static string Describe(string name, JsonElement input)
    {
        string? S(string k) => input.ValueKind == JsonValueKind.Object && input.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        return name switch
        {
            "list_files" => S("path_prefix") is { Length: > 0 } p ? $"Listing files in {p}" : "Listing files",
            "read_file" => $"Reading {S("path")}",
            "write_file" => $"Writing {S("path")}",
            "edit_file" => $"Editing {S("path")}",
            "delete_file" => $"Deleting {S("path")}",
            "search_code" => $"Searching for \"{S("query")}\"",
            "run_command" => "Running: " + Trim(S("command") ?? "", 120),
            _ => name,
        };
    }

    // ---------- path validation ----------

    /// <summary>A repository-relative path: absolute paths, '..', NUL/control characters, backslashes and anything under .git are refused.</summary>
    public static string ValidatePath(string? path)
    {
        var raw = (path ?? "").Trim();
        if (raw.StartsWith('/') || raw.StartsWith('\\') || raw.StartsWith('~') || (raw.Length > 1 && raw[1] == ':'))
            throw new InvalidOperationException("Use a path relative to the repository root (no leading '/', '~' or drive letter).");
        if (raw.Contains('\\')) throw new InvalidOperationException("Use '/' as the path separator.");
        return EditorPaths.Normalize(raw); // rejects '.', '..', empty segments, control characters and '.git'
    }

    private const string ReadScript = """
        umask 002
        root=$(realpath -e -- "$1") || exit 3
        t=$(realpath -e -- "$root/$2" 2>/dev/null) || { echo "No such file: $2" >&2; exit 3; }
        case "$t" in "$root"/*) ;; *) echo "Path escapes the workspace: $2" >&2; exit 4 ;; esac
        [ -f "$t" ] || { echo "Not a regular file: $2" >&2; exit 5; }
        if [ -s "$t" ] && ! grep -Iq . "$t"; then echo "Binary file: $2" >&2; exit 6; fi
        awk -v s="$3" -v e="$4" 'NR>=s && NR<=e { print NR "\t" $0 }' "$t"
        """;

    private const string RawReadScript = """
        umask 002
        root=$(realpath -e -- "$1") || exit 3
        t=$(realpath -e -- "$root/$2" 2>/dev/null) || { echo "No such file: $2" >&2; exit 3; }
        case "$t" in "$root"/*) ;; *) echo "Path escapes the workspace: $2" >&2; exit 4 ;; esac
        [ -f "$t" ] || { echo "Not a regular file: $2" >&2; exit 5; }
        if [ -s "$t" ] && ! grep -Iq . "$t"; then echo "Binary file: $2" >&2; exit 6; fi
        cat -- "$t"
        """;

    private const string DeleteScript = """
        umask 002
        root=$(realpath -e -- "$1") || exit 3
        t=$(realpath -e -- "$root/$2" 2>/dev/null) || { echo "No such file: $2" >&2; exit 3; }
        case "$t" in "$root"/*) ;; *) echo "Path escapes the workspace: $2" >&2; exit 4 ;; esac
        case "$t" in "$root"/.git|"$root"/.git/*) echo "Refusing to touch .git" >&2; exit 4 ;; esac
        [ -f "$t" ] || { echo "Not a regular file: $2" >&2; exit 5; }
        rm -f -- "$t"
        """;

    private const string WriteScript = """
        umask 002
        root=$(realpath -e -- "$1") || exit 3
        t=$(realpath -m -- "$root/$2") || exit 3
        case "$t" in "$root"/*) ;; *) echo "Path escapes the workspace: $2" >&2; exit 4 ;; esac
        case "$t" in "$root"/.git|"$root"/.git/*) echo "Refusing to touch .git" >&2; exit 4 ;; esac
        [ -d "$t" ] && { echo "Is a directory: $2" >&2; exit 5; }
        mkdir -p -- "$(dirname -- "$t")"
        cat > "$t"
        """;

    // ---------- tools ----------

    private ExecSpec Spec(IReadOnlyList<string> command, string? stdin = null, int? seconds = null) => new(command)
    {
        User = "bbai", WorkDir = Root, Stdin = stdin, MaxOutputBytes = options.MaxFileReadBytes,
        Timeout = TimeSpan.FromSeconds(seconds ?? 30),
        Env = new Dictionary<string, string> { ["HOME"] = "/home/bbai", ["GIT_TERMINAL_PROMPT"] = "0" },
    };

    private async Task<(string, bool)> ListFilesAsync(string? prefix, CancellationToken ct)
    {
        var p = string.IsNullOrWhiteSpace(prefix) ? null : ValidatePath(prefix);
        var cmd = new List<string> { "git", "ls-files", "-co", "--exclude-standard", "--" };
        if (p != null) cmd.Add(p);
        var r = await backend.ExecAsync(containerId, Spec(cmd), ct);
        if (!r.Ok) return ("Could not list files: " + Trim(r.Output, 400), true);
        var files = r.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Distinct().OrderBy(f => f, StringComparer.Ordinal).ToList();
        if (files.Count == 0) return (p == null ? "The workspace has no files." : $"No files under {p}", false);
        var sb = new StringBuilder();
        foreach (var f in files.Take(MaxListed)) sb.AppendLine(f);
        if (files.Count > MaxListed) sb.AppendLine($"… and {files.Count - MaxListed} more (use path_prefix to narrow down).");
        return (sb.ToString().TrimEnd(), false);
    }

    private async Task<(string, bool)> ReadFileAsync(string path, int? start, int? end, CancellationToken ct)
    {
        var p = ValidatePath(path);
        var from = Math.Max(1, start ?? 1);
        var to = Math.Min(int.MaxValue, end ?? int.MaxValue);
        if (from > to) return ("start_line is after end_line.", true);
        if (Proposals?.Find(p) is { } pending)
        {
            // Review mode: what the assistant itself proposed for this file is what it sees when it reads it again.
            if (pending.NewText == null) return ($"{p} is proposed for deletion (pending review).", true);
            var numbered = string.Concat(LineDiff.Split(pending.NewText).Select((l, i) => (n: i + 1, l)).Where(x => x.n >= from && x.n <= to).Select(x => $"{x.n}\t{x.l.TrimEnd('\n')}\n"));
            return (EditorAiPrompts.Untrusted("FILE (proposed, not applied yet)", p, numbered.TrimEnd('\n'), EditorAiPrompts.NewBoundary()), false);
        }
        var r = await backend.ExecAsync(containerId, Spec(["sh", "-c", ReadScript, "sh", Root, p, from.ToString(), to.ToString()]), ct);
        if (!r.Ok) return (Trim(r.Output, 400), true);
        var text = r.Output;
        if (r.Truncated) text += $"\n… (truncated at {options.MaxFileReadBytes:N0} bytes; read more with start_line)";
        return (EditorAiPrompts.Untrusted("FILE", p, text.TrimEnd('\n'), EditorAiPrompts.NewBoundary()), false);
    }

    /// <summary>The file's exact text, or <c>Exists = false</c> if there is no such file; an error for binary, escaping or oversized files.</summary>
    public async Task<(bool Exists, string? Text, string? Error)> ReadRawAsync(string path, CancellationToken ct)
    {
        var p = ValidatePath(path);
        var r = await backend.ExecAsync(containerId, Spec(["sh", "-c", RawReadScript, "sh", Root, p]), ct);
        if (r.ExitCode == 3) return (false, null, null);
        if (!r.Ok) return (true, null, Trim(r.Output, 400));
        if (r.Truncated) return (true, null, "The file is too large to handle here.");
        return (true, r.Output, null);
    }

    /// <summary>Writes a file in the working tree (path validated here and again in the container): direct edits and accepted changes.</summary>
    public async Task<(bool Ok, string Message)> WriteTextAsync(string path, string content, CancellationToken ct)
    {
        var p = ValidatePath(path);
        if (Encoding.UTF8.GetByteCount(content) > options.MaxFileWriteBytes)
            return (false, $"The file is larger than the {options.MaxFileWriteBytes / 1024:N0} KB limit.");
        var r = await backend.ExecAsync(containerId, Spec(["sh", "-c", WriteScript, "sh", Root, p], content), ct);
        return r.Ok ? (true, $"Wrote {p} ({Encoding.UTF8.GetByteCount(content):N0} bytes).") : (false, Trim(r.Output, 400));
    }

    /// <summary>Deletes a regular file from the working tree.</summary>
    public async Task<(bool Ok, string Message)> DeleteAsync(string path, CancellationToken ct)
    {
        var p = ValidatePath(path);
        var r = await backend.ExecAsync(containerId, Spec(["sh", "-c", DeleteScript, "sh", Root, p]), ct);
        return r.Ok ? (true, $"Deleted {p}.") : (false, Trim(r.Output, 400));
    }

    private async Task<(string, bool)> WriteFileAsync(string path, string content, CancellationToken ct)
    {
        var p = ValidatePath(path);
        if (Encoding.UTF8.GetByteCount(content) > options.MaxFileWriteBytes)
            return ($"The file is larger than the {options.MaxFileWriteBytes / 1024:N0} KB limit.", true);
        if (Proposals != null) return await ProposeAsync(p, content, ct);
        var (ok, message) = await WriteTextAsync(p, content, ct);
        if (ok) ChangedPaths.Add(p);
        return (message, !ok);
    }

    private async Task<(string, bool)> ProposeAsync(string p, string? newText, CancellationToken ct)
    {
        var collector = Proposals!;
        string? original;
        if (collector.Find(p) is { } known) original = known.OriginalText;
        else
        {
            var (exists, text, error) = await ReadRawAsync(p, ct);
            if (error != null) return (error, true);
            original = exists ? text : null;
            if (newText == null && !exists) return ($"No such file: {p}", true);
        }
        var problem = collector.Set(p, original, newText);
        if (problem != null) return (problem, true);
        return (newText == null
            ? $"Proposed deleting {p}. It is applied only if the developer accepts it."
            : $"Proposed the change to {p}. It is applied only if the developer accepts it; until then the workspace is unchanged and run_command does not see it.", false);
    }

    private async Task<(string, bool)> DeleteFileAsync(string path, CancellationToken ct)
    {
        var p = ValidatePath(path);
        if (Proposals != null) return await ProposeAsync(p, null, ct);
        var (ok, message) = await DeleteAsync(p, ct);
        if (ok) ChangedPaths.Add(p);
        return (message, !ok);
    }

    private async Task<(string, bool)> EditFileAsync(string path, string find, string replace, CancellationToken ct)
    {
        var p = ValidatePath(path);
        if (find.Length == 0) return ("`find` can't be empty.", true);
        string current;
        if (Proposals?.Find(p) is { } pending)
        {
            if (pending.NewText == null) return ($"{p} is proposed for deletion.", true);
            current = pending.NewText;
        }
        else
        {
            // Read the raw file (no line numbers) through the same checked script path.
            var read = await backend.ExecAsync(containerId, Spec(["sh", "-c", ReadScript.Replace("print NR \"\\t\" $0", "print $0"), "sh", Root, p, "1", int.MaxValue.ToString()]), ct);
            if (!read.Ok) return (Trim(read.Output, 400), true);
            if (read.Truncated) return ("The file is too large to edit with edit_file; use write_file or a command.", true);
            current = read.Output.EndsWith('\n') ? read.Output : read.Output + "\n";
        }
        var first = current.IndexOf(find, StringComparison.Ordinal);
        if (first < 0) return ("`find` text was not found. Read the file again and copy the text exactly.", true);
        if (current.IndexOf(find, first + 1, StringComparison.Ordinal) >= 0) return ("`find` text occurs more than once; include more context.", true);
        var updated = current[..first] + replace + current[(first + find.Length)..];
        return await WriteFileAsync(p, updated, ct);
    }

    private async Task<(string, bool)> SearchAsync(string query, bool regex, CancellationToken ct)
    {
        if (query.Length is 0 or > 200) return ("The query must be 1 to 200 characters.", true);
        var r = await backend.ExecAsync(containerId, Spec(["git", "grep", "-n", "-I", "--no-color", "--untracked", regex ? "-E" : "-F", "-e", query, "--"]), ct);
        if (r.ExitCode == 1) return ("No matches.", false);
        if (!r.Ok) return ("Search failed: " + Trim(r.Output, 400), true);
        var lines = r.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var sb = new StringBuilder();
        foreach (var l in lines.Take(100)) sb.AppendLine(Trim(l, 300));
        if (lines.Length > 100) sb.AppendLine($"… {lines.Length - 100} more matches.");
        return (EditorAiPrompts.Untrusted("SEARCH RESULTS", query, sb.ToString().TrimEnd(), EditorAiPrompts.NewBoundary()), false);
    }

    [GeneratedRegex(@"(^|[\s;&|(`$])(?:\S*/)?git\s+(?:-\S+\s+(?:\S+\s+)?)*(commit|push|merge|rebase|cherry-pick|revert|am|tag|pull|fetch|remote|config|credential|update-ref|commit-tree|send-email)\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex ForbiddenGit();

    /// <summary>True for a command line that tries to commit, push or reach a git remote (refused; the user does that).</summary>
    public static bool IsForbiddenGitCommand(string command) => ForbiddenGit().IsMatch(command);

    private async Task<(string, bool)> RunCommandAsync(string command, int? seconds, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(command)) return ("Give a command.", true);
        if (command.Length > MaxCommandChars) return ($"The command is longer than {MaxCommandChars} characters; put it in a script file.", true);
        if (command.Contains('\0')) return ("The command contains a NUL character.", true);
        if (IsForbiddenGitCommand(command))
            return ("Committing, pushing and git remotes are not available to you. Make the changes in the working tree; the user reviews and commits them.", true);
        var timeout = Math.Clamp(seconds ?? options.CommandTimeoutSeconds, 1, options.CommandTimeoutSeconds);
        CommandVerbs.Add(command.TrimStart().Split([' ', '\t', '\n', ';', '&', '|'], 2)[0] is { Length: > 0 } v ? Trim(v, 30) : "?");
        var spec = Spec(["sh", "-c", "umask 002; eval \"$1\"", "sh", command], null, timeout) with { MaxOutputBytes = options.MaxOutputBytes };
        var r = await backend.ExecAsync(containerId, spec, ct);
        var sb = new StringBuilder();
        sb.Append(r.TimedOut ? $"Timed out after {timeout} s (the command was stopped)." : $"Exit code {r.ExitCode}.");
        if (r.Truncated) sb.Append($" Output truncated at {options.MaxOutputBytes:N0} bytes.");
        sb.AppendLine();
        sb.Append(EditorAiPrompts.Untrusted("COMMAND OUTPUT", Trim(command, 80), r.Output.TrimEnd('\n'), EditorAiPrompts.NewBoundary()));
        return (sb.ToString(), r.TimedOut || r.ExitCode != 0);
    }

    // ---------- input parsing ----------

    private static string Trim(string s, int max) => s.Length <= max ? s : s[..max] + "…";

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
