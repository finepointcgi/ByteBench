using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ByteBench.Services.Ai;
using Microsoft.Extensions.Options;

namespace ByteBench.Services.Editor.Ai;

/// <summary>
/// The assistant through Claude Code installed on the server. Per request:
/// <list type="number">
/// <item>the workspace (base commit plus working changes; text files only, within caps) is written to a fresh temporary
/// directory (mode 700). Repository-supplied Claude Code configuration (<c>.claude/</c>, <c>.mcp.json</c>) is not copied;</item>
/// <item><c>claude -p</c> runs there headless with a clean environment, the prompt on stdin, <c>stream-json</c> output, only
/// the Read/Grep/Glob/Edit/Write tools (Bash, web and MCP tools disallowed), <c>dontAsk</c> permissions (nothing prompts:
/// anything not allowed is refused), a turn cap and a timeout that kills the process tree;</item>
/// <item>its text and tool calls stream to the chat panel;</item>
/// <item>afterwards the directory is compared with what was written: every changed, added or deleted file becomes a
/// pending <see cref="ProposedEdit"/>, exactly like the API backend. Nothing is applied;</item>
/// <item>the directory is deleted.</item>
/// </list>
/// At most <see cref="ClaudeCodeOptions.MaxConcurrent"/> runs at once. Uses the server account's Claude Code login.
/// </summary>
public sealed partial class ClaudeCodeEditorBackend : IEditorAiBackend, IStructuredAiBackend, IDisposable
{
    public const string Tools = "Read,Grep,Glob,Edit,Write";
    public const string AllowedTools = "Read(./**),Edit(./**),Write(./**),Grep,Glob";
    public const string DisallowedTools = "Bash,WebFetch,WebSearch,NotebookEdit,Task";
    private static readonly string[] SkippedPrefixes = [".claude/", ".mcp.json", ".git/"];

    private readonly ClaudeCodeDetector detector;
    private readonly AiClient ai;
    private readonly ClaudeCodeOptions opts;
    private readonly ILogger<ClaudeCodeEditorBackend> log;
    private readonly SemaphoreSlim gate;

    public ClaudeCodeEditorBackend(ClaudeCodeDetector detector, AiClient ai, IOptions<ClaudeCodeOptions> options, ILogger<ClaudeCodeEditorBackend> log)
    {
        this.detector = detector;
        this.ai = ai;
        this.log = log;
        opts = options.Value;
        gate = new SemaphoreSlim(Math.Max(1, opts.MaxConcurrent));
    }

    public EditorAiBackendKind Kind => EditorAiBackendKind.ClaudeCode;
    public string DisplayName => "Claude Code (on the server)";

    /// <summary>Root of the temporary workspaces (tests check it is emptied).</summary>
    public static string TempRoot => Path.Combine(Path.GetTempPath(), "bytebench-claude-code");

    public async Task<bool> IsAvailableAsync(CancellationToken ct = default) => (await detector.GetAsync(false, ct)).Installed;

    public async Task<EditorAiResult> RunAsync(EditorAiRequest request, IEditorAiSink sink, CancellationToken ct)
    {
        var status = await detector.GetAsync(false, ct);
        if (!status.Installed || status.ExecutablePath == null)
            return new EditorAiResult("", 0, [], "Claude Code isn't available on the server: " + (status.Error ?? "not found."));
        if (!await gate.WaitAsync(TimeSpan.FromSeconds(30), ct))
            return new EditorAiResult("", 0, [], "The server is busy with other Claude Code requests. Try again in a moment.");
        string? dir = null;
        try
        {
            dir = CreatePrivateDirectory();
            await sink.ToolAsync("workspace", "Preparing a copy of your workspace");
            var written = await Task.Run(() => Materialize(request.Snapshot, dir), ct);
            var prompt = EditorAiPrompts.BuildUserMessage(request, p => written.GetValueOrDefault(p), inlineAttachments: false, EditorAiPrompts.NewBoundary());
            var run = await RunProcessAsync(status, dir, prompt, sink, BuildArguments(status), ct);
            if (run.Usage is { } u)
                await ai.RecordUsageAsync(request.Feature + ".claude-code", request.UserId, request.ProjectId, u.Input, u.Output, u.CacheRead, u.CacheWrite, CancellationToken.None);
            if (run.Error != null) return new EditorAiResult(run.Text, run.ToolCalls, [], run.Error, run.StopNote);

            var proposals = await Task.Run(() => Diff(dir, written), ct);
            foreach (var p in proposals) await sink.ProposalAsync(p);
            return new EditorAiResult(run.Text, run.ToolCalls, proposals, null, run.StopNote);
        }
        finally
        {
            gate.Release();
            if (dir != null) DeleteDirectory(dir);
        }
    }

    // ---------- workspace copy ----------

    private static string CreatePrivateDirectory()
    {
        var root = TempRoot;
        var dir = Path.Combine(root, Guid.NewGuid().ToString("N"));
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(dir);
        }
        else
        {
            const UnixFileMode Private = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
            Directory.CreateDirectory(root, Private);
            Directory.CreateDirectory(dir, Private);
            File.SetUnixFileMode(dir, Private);
        }
        return dir;
    }

    private static bool Skipped(string path) =>
        SkippedPrefixes.Any(p => path.Equals(p.TrimEnd('/'), StringComparison.OrdinalIgnoreCase) || path.StartsWith(p, StringComparison.OrdinalIgnoreCase));

    /// <summary>Writes the workspace's text files into <paramref name="dir"/>; returns path → content written.</summary>
    private Dictionary<string, string> Materialize(WorkspaceSnapshot snapshot, string dir)
    {
        var written = new Dictionary<string, string>(StringComparer.Ordinal);
        long bytes = 0;
        var root = Path.GetFullPath(dir) + Path.DirectorySeparatorChar;
        foreach (var (path, size) in snapshot.ListFiles(opts.MaxFiles))
        {
            if (Skipped(path) || size > Git.GitOps.MaxBlobBytes || bytes + size > opts.MaxBytes) continue;
            var file = snapshot.Read(path);
            if (file.Text == null) continue;
            var full = Path.GetFullPath(Path.Combine(dir, path));
            if (!full.StartsWith(root, StringComparison.Ordinal)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, file.Text, new UTF8Encoding(false));
            written[path] = file.Text;
            bytes += size;
        }
        return written;
    }

    /// <summary>Every file Claude Code changed, added or deleted, as pending proposals.</summary>
    private static List<ProposedEdit> Diff(string dir, Dictionary<string, string> written)
    {
        var result = new List<ProposedEdit>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var root = new DirectoryInfo(dir);
        foreach (var f in Walk(root))
        {
            var rel = Path.GetRelativePath(dir, f.FullName).Replace('\\', '/');
            if (Skipped(rel) || !EditorPaths.TryNormalize(rel, out var path)) continue;
            seen.Add(path);
            if (f.Length > Git.GitOps.MaxBlobBytes) continue;
            var bytes = File.ReadAllBytes(f.FullName);
            if (bytes.AsSpan().IndexOf((byte)0) >= 0) continue; // binary
            string text;
            try { text = new UTF8Encoding(false, true).GetString(bytes).Replace("\r\n", "\n"); }
            catch (DecoderFallbackException) { continue; }
            if (written.TryGetValue(path, out var original))
            {
                if (original != text) result.Add(new ProposedEdit { Path = path, Kind = ProposedEditKind.Modify, OriginalText = original, NewText = text, Summary = "Edited by Claude Code" });
            }
            else result.Add(new ProposedEdit { Path = path, Kind = ProposedEditKind.Create, NewText = text, Summary = "Created by Claude Code" });
        }
        foreach (var (path, original) in written)
            if (!seen.Contains(path)) result.Add(new ProposedEdit { Path = path, Kind = ProposedEditKind.Delete, OriginalText = original, Summary = "Deleted by Claude Code" });
        return result.OrderBy(p => p.Path, StringComparer.Ordinal).ToList();
    }

    /// <summary>Regular files under <paramref name="dir"/>, never following symbolic links.</summary>
    private static IEnumerable<FileInfo> Walk(DirectoryInfo dir)
    {
        foreach (var e in dir.EnumerateFileSystemInfos())
        {
            if (e.LinkTarget != null) continue;
            if (e is DirectoryInfo d) { foreach (var f in Walk(d)) yield return f; }
            else if (e is FileInfo f) yield return f;
        }
    }

    private void DeleteDirectory(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return;
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(dir, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.LogWarning(ex, "Couldn't delete the Claude Code workspace {Dir}", dir);
        }
    }

    // ---------- the process ----------

    private sealed record Usage(long Input, long Output, long CacheRead, long CacheWrite);

    private sealed record RunResult(string Text, int ToolCalls, string? Error, string? StopNote, Usage? Usage);

    /// <summary>The command line (without the executable). Only flags that this version's <c>--help</c> lists are passed.</summary>
    public IReadOnlyList<string> BuildArguments(ClaudeCodeStatus status)
    {
        var args = new List<string> { "-p", "--output-format", "stream-json", "--verbose" };
        void Flag(string flag, params string[] values)
        {
            if (!status.Supports(flag)) return;
            args.Add(flag);
            args.AddRange(values);
        }
        Flag("--include-partial-messages");
        Flag("--tools", Tools);
        args.AddRange(["--allowedTools", AllowedTools]);
        args.AddRange(["--disallowedTools", DisallowedTools]);
        args.AddRange(["--permission-mode", "dontAsk"]);
        Flag("--permission-prompts", "none");
        Flag("--restricted");
        Flag("--strict-mcp-config");
        Flag("--no-session-persistence");
        Flag("--disable-slash-commands");
        Flag("--max-turns", opts.MaxTurns.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (!string.IsNullOrWhiteSpace(opts.Model)) args.AddRange(["--model", opts.Model]);
        args.AddRange(["--append-system-prompt", EditorAiPrompts.ClaudeCodeSystem]);
        return args;
    }

    private async Task<RunResult> RunProcessAsync(ClaudeCodeStatus status, string dir, string prompt, IEditorAiSink sink, IReadOnlyList<string> arguments, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(status.ExecutablePath!)
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = dir,
            StandardOutputEncoding = Encoding.UTF8, StandardInputEncoding = new UTF8Encoding(false),
        };
        foreach (var a in arguments) psi.ArgumentList.Add(a);
        ClaudeCodeEnvironment.Apply(psi);

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Could not start Claude Code.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, opts.TimeoutSeconds)));
        var stderr = new StringBuilder();
        var stderrTask = Task.Run(async () =>
        {
            var buffer = new char[4096];
            int n;
            while ((n = await process.StandardError.ReadAsync(buffer)) > 0)
                if (stderr.Length < 16_000) stderr.Append(buffer, 0, n);
        }, CancellationToken.None);

        var text = new StringBuilder();
        var sawDeltas = false;
        var toolCalls = 0;
        var turns = 0;
        string? stopNote = null, error = null, finalResult = null;
        var capped = false;
        Usage? usage = null;
        try
        {
            await process.StandardInput.WriteAsync(prompt.AsMemory(), timeout.Token);
            process.StandardInput.Close();
            while (await process.StandardOutput.ReadLineAsync(timeout.Token) is { } line)
            {
                if (capped) break; // the turn cap was hit: the killed process may still have output in the pipe, which must not count
                if (line.Length == 0 || line[0] != '{') continue;
                JsonElement ev;
                try { ev = JsonDocument.Parse(line).RootElement; }
                catch (JsonException) { continue; }
                switch (Str(ev, "type"))
                {
                    case "stream_event" when ev.TryGetProperty("event", out var se) && Str(se, "type") == "content_block_delta"
                                             && se.TryGetProperty("delta", out var d) && Str(d, "type") == "text_delta":
                        sawDeltas = true;
                        var delta = Str(d, "text") ?? "";
                        text.Append(delta);
                        await sink.TextAsync(delta);
                        break;
                    case "assistant" when ev.TryGetProperty("message", out var m) && m.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array:
                        var usedTool = false;
                        foreach (var block in content.EnumerateArray())
                        {
                            if (Str(block, "type") == "tool_use")
                            {
                                usedTool = true;
                                toolCalls++;
                                await sink.ToolAsync(Str(block, "name") ?? "tool", DescribeTool(block));
                            }
                            else if (Str(block, "type") == "text" && !sawDeltas && Str(block, "text") is { } t)
                            {
                                text.Append(t);
                                await sink.TextAsync(t);
                            }
                        }
                        if (usedTool && ++turns > opts.MaxTurns)
                        {
                            stopNote = $"Stopped after {opts.MaxTurns} turns (Ai:ClaudeCode:MaxTurns).";
                            capped = true;
                            Kill(process);
                        }
                        else if (!sawDeltas && text.Length > 0 && !text.ToString().EndsWith('\n')) { text.Append("\n\n"); await sink.TextAsync("\n\n"); }
                        break;
                    case "result":
                        finalResult = Str(ev, "result");
                        if (ev.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object)
                            usage = new Usage(Long(u, "input_tokens"), Long(u, "output_tokens"), Long(u, "cache_read_input_tokens"), Long(u, "cache_creation_input_tokens"));
                        var subtype = Str(ev, "subtype");
                        if (subtype == "error_max_turns") stopNote = $"Stopped after {opts.MaxTurns} turns (Ai:ClaudeCode:MaxTurns).";
                        else if (ev.TryGetProperty("is_error", out var isErr) && isErr.ValueKind == JsonValueKind.True)
                            error = "Claude Code reported an error: " + (finalResult ?? subtype ?? "unknown");
                        break;
                }
            }
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Kill(process);
            return new RunResult(text.ToString(), toolCalls, $"Claude Code didn't finish within {opts.TimeoutSeconds} seconds and was stopped.", null, usage);
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            throw;
        }
        catch (IOException ex)
        {
            Kill(process);
            return new RunResult(text.ToString(), toolCalls, "Claude Code stopped unexpectedly: " + ex.Message, null, usage);
        }
        try { await stderrTask.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None); } catch (TimeoutException) { }

        if (text.Length == 0 && !string.IsNullOrWhiteSpace(finalResult))
        {
            text.Append(finalResult);
            await sink.TextAsync(finalResult);
        }
        if (error == null && stopNote == null && process.ExitCode != 0)
        {
            var why = stderr.ToString().Trim();
            log.LogWarning("Claude Code exited with {Code}: {Error}", process.ExitCode, why);
            error = $"Claude Code exited with code {process.ExitCode}" + (why.Length > 0 ? ": " + (why.Length > 500 ? why[..500] + "…" : why) : ".");
        }
        return new RunResult(text.ToString().Trim(), toolCalls, error, stopNote, usage);
    }

    private static string DescribeTool(JsonElement block)
    {
        var name = Str(block, "name") ?? "tool";
        if (!block.TryGetProperty("input", out var input) || input.ValueKind != JsonValueKind.Object) return name;
        var target = Str(input, "file_path") ?? Str(input, "path") ?? Str(input, "pattern");
        return target == null ? name : $"{name} {target}";
    }

    private static void Kill(Process p)
    {
        try { if (!p.HasExited) p.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static long Long(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var l) ? l : 0;

    public void Dispose() => gate.Dispose();
}
