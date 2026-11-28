using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using ByteBench.Data;
using ByteBench.Services.Ai;
using ByteBench.Services.Editor.Ai;
using Microsoft.Extensions.Options;

namespace ByteBench.Services.Workspaces;

/// <summary>
/// The Claude Code CLI backend of AI runs. <c>claude -p</c> (print mode, <c>stream-json</c>) runs on the host, in an empty private directory, with a clean
/// environment (the server account's Claude login only) and with every built-in tool switched off. Its only tools are the run's own, served over MCP by
/// <see cref="AiRunToolBridge"/> and executed inside the workspace container exactly like the API backend's. So the CLI's credentials never enter the
/// container (repository code cannot read them) and never appear in a tool's output, and the model can't touch anything but its workspace. The run's
/// limits are enforced on the event stream (steps = model messages, tool calls = tool_use blocks, tokens and cost from the usage events) and by killing
/// the process tree; cancelling and the time limit kill it too.
/// </summary>
public sealed partial class AiRunExecutor
{
    private const string CliSystemNote = """


        In this session ByteBench serves your tools over MCP: they are named mcp__bytebench__list_files, mcp__bytebench__read_file,
        mcp__bytebench__write_file, mcp__bytebench__edit_file, mcp__bytebench__delete_file, mcp__bytebench__search_code,
        mcp__bytebench__run_command and mcp__bytebench__run_task, and work as described above. You have no other tools: there is no
        built-in shell, file, web or task tool, and nothing on this machine besides the repository workspace is reachable. Your changes take
        effect in the workspace right away (there is no separate approval step); people review the pull request ByteBench opens from them.
        """;

    /// <summary>Tools of Claude Code that must never be available to a run (the CLI also gets <c>--tools ""</c> when it lists that flag).</summary>
    public const string CliDisallowedTools = "Bash,BashOutput,KillShell,Read,Write,Edit,MultiEdit,Glob,Grep,LS,NotebookEdit,NotebookRead,WebFetch,WebSearch,Task,Agent,TodoWrite,ExitPlanMode,SlashCommand,Skill";

    /// <summary>The CLI's command line (without the executable). Only flags that this version's <c>--help</c> lists are passed, except the ones that restrict it (always passed).</summary>
    public static IReadOnlyList<string> BuildCliArguments(ClaudeCodeStatus status, string mcpConfigPath, string? model, AiRunOptions o, string systemPrompt)
    {
        var args = new List<string> { "-p", "--output-format", "stream-json", "--verbose" };
        void Flag(string flag, params string[] values)
        {
            if (!status.Supports(flag)) return;
            args.Add(flag);
            args.AddRange(values);
        }
        args.AddRange(["--mcp-config", mcpConfigPath, "--strict-mcp-config"]);
        Flag("--tools", ""); // no built-in tools at all
        args.AddRange(["--allowedTools", "mcp__" + AiRunToolBridge.ServerName]);
        args.AddRange(["--disallowedTools", CliDisallowedTools]);
        args.AddRange(["--permission-mode", "dontAsk"]);
        Flag("--setting-sources", "project"); // not the server account's own settings, hooks, plugins or memory: the run directory is empty
        Flag("--no-session-persistence");
        Flag("--disable-slash-commands");
        Flag("--max-turns", o.MaxSteps.ToString(CultureInfo.InvariantCulture));
        if (o.MaxCostUsd > 0) Flag("--max-budget-usd", o.MaxCostUsd.ToString(CultureInfo.InvariantCulture));
        if (!string.IsNullOrWhiteSpace(model)) args.AddRange(["--model", model]);
        args.AddRange(["--append-system-prompt", systemPrompt]);
        return args;
    }

    private static string CreateRunDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "bytebench-ai-runs");
        var dir = Path.Combine(root, Guid.NewGuid().ToString("N"));
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(dir);
        else
        {
            const UnixFileMode Private = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
            Directory.CreateDirectory(root, Private);
            Directory.CreateDirectory(dir, Private);
            File.SetUnixFileMode(dir, Private);
        }
        return dir;
    }

    private static void KillTree(Process p)
    {
        try { if (!p.HasExited) p.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }

    private static string? JStr(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static long JLong(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var l) ? l : 0;

    private sealed record CliUsage(long Input, long Output, long CacheRead, long CacheWrite)
    {
        public static CliUsage From(JsonElement u) =>
            new(JLong(u, "input_tokens"), JLong(u, "output_tokens"), JLong(u, "cache_read_input_tokens"), JLong(u, "cache_creation_input_tokens"));
    }

    private async Task<LoopResult> RunCliLoopAsync(IServiceProvider sp, AiRun run, WorkspaceAiTools tools, string user, Func<string, string> M, LiveWorkspace live,
        AiRunMeter meter, Cancel cancel, CancellationToken ct)
    {
        var o = Opts;
        var cc = sp.GetRequiredService<IOptions<ClaudeCodeOptions>>().Value;
        var status = await sp.GetRequiredService<ClaudeCodeDetector>().GetAsync(false, ct);
        if (!status.Installed || status.ExecutablePath == null)
            throw new InvalidOperationException("The Claude Code CLI isn't available on the server: " + (status.Error ?? "not found."));

        var dir = CreateRunDirectory();
        Process? process = null;
        try
        {
            using var bridge = AiRunToolBridge.Start(tools, M, live.Touch, o.MaxToolCalls, ct);
            var mcpPath = Path.Combine(dir, "mcp.json");
            await File.WriteAllTextAsync(mcpPath, bridge.McpConfigJson(), new UTF8Encoding(false), ct);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(mcpPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);

            var psi = new ProcessStartInfo(status.ExecutablePath)
            {
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = dir,
                StandardOutputEncoding = Encoding.UTF8, StandardInputEncoding = new UTF8Encoding(false),
            };
            foreach (var a in BuildCliArguments(status, mcpPath, cc.Model, o, SystemPrompt + CliSystemNote)) psi.ArgumentList.Add(a);
            ClaudeCodeEnvironment.Apply(psi);

            await AddEventAsync(run.Id, "info", $"Starting Claude Code {status.Version} on the server; its tools run inside the workspace container");
            process = Process.Start(psi) ?? throw new InvalidOperationException("Could not start the Claude Code CLI.");
            var stderr = new StringBuilder();
            var stderrTask = Task.Run(async () =>
            {
                var buffer = new char[4096];
                int n;
                while ((n = await process.StandardError.ReadAsync(buffer)) > 0)
                    lock (stderr) if (stderr.Length < 16_000) stderr.Append(buffer, 0, n);
            }, CancellationToken.None);

            // A cancel request that arrives without the in-process signal (another instance) is noticed here.
            using var watchDone = new CancellationTokenSource();
            _ = Task.Run(async () =>
            {
                try
                {
                    while (!watchDone.IsCancellationRequested)
                    {
                        await Task.Delay(1500, watchDone.Token);
                        if (await IsCancelRequestedAsync(run.Id)) { cancel.MarkByUser(); return; }
                    }
                }
                catch (OperationCanceledException) { }
            }, CancellationToken.None);

            await process.StandardInput.WriteAsync(user.AsMemory(), ct);
            process.StandardInput.Close();

            var stepIds = new HashSet<string>();
            var toolIds = new HashSet<string>();
            var usageById = new Dictionary<string, CliUsage>();
            var lastText = new StringBuilder();
            var anon = 0;
            string? currentMessage = null, stopReason = null, stopNote = null, resultText = null, resultError = null;
            var sawResult = false;

            void Limit(string reason, string note) { if (stopReason != null) return; stopReason = reason; stopNote = note; KillTree(process!); }

            try
            {
                while (await process.StandardOutput.ReadLineAsync(ct) is { } line)
                {
                    if (stopReason != null) break; // killed on a limit: whatever is still in the pipe no longer counts
                    live.Touch();
                    if (line.Length == 0 || line[0] != '{') continue;
                    JsonElement ev;
                    try { ev = JsonDocument.Parse(line).RootElement; }
                    catch (JsonException) { continue; }
                    switch (JStr(ev, "type"))
                    {
                        case "assistant" when ev.TryGetProperty("message", out var msg) && msg.ValueKind == JsonValueKind.Object:
                        {
                            var id = JStr(msg, "id") ?? (currentMessage ?? $"anon{++anon}");
                            var isNew = stepIds.Add(id);
                            if (isNew) { currentMessage = id; lastText.Clear(); }
                            if (msg.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object) usageById[id] = CliUsage.From(u);
                            meter.Steps = stepIds.Count;
                            meter.SetUsage(usageById.Values.Sum(x => x.Input), usageById.Values.Sum(x => x.Output), usageById.Values.Sum(x => x.CacheRead), usageById.Values.Sum(x => x.CacheWrite));
                            if (isNew)
                            {
                                if (meter.Steps > o.MaxSteps) { meter.Steps = o.MaxSteps; Limit("step limit", $"it used all {o.MaxSteps} steps"); break; }
                                await AddEventAsync(run.Id, "step", $"Step {meter.Steps}: {meter.WeightedTokens:N0} tokens, about ${meter.Cost(o):0.00}");
                                await SaveMeterAsync(run.Id, meter, o);
                            }
                            if (msg.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
                                foreach (var block in content.EnumerateArray())
                                {
                                    var type = JStr(block, "type");
                                    if (type == "text" && JStr(block, "text") is { Length: > 0 } t)
                                    {
                                        lastText.Append(t);
                                        await AddEventAsync(run.Id, "info", "AI: " + M(Truncate(t.Trim().ReplaceLineEndings(" "), 300)));
                                    }
                                    else if (type == "tool_use" && toolIds.Add(JStr(block, "id") ?? $"tool{++anon}"))
                                    {
                                        meter.ToolCalls++;
                                        var name = JStr(block, "name") ?? "tool";
                                        if (name.StartsWith(AiRunToolBridge.ToolPrefix, StringComparison.Ordinal)) name = name[AiRunToolBridge.ToolPrefix.Length..];
                                        var input = block.TryGetProperty("input", out var inp) ? inp : default;
                                        await AddEventAsync(run.Id, "tool", M(WorkspaceAiTools.Describe(name, input)));
                                        if (meter.ToolCalls > o.MaxToolCalls) { Limit("tool call limit", $"it used all {o.MaxToolCalls} tool calls"); break; }
                                    }
                                }
                            if (stopReason == null && meter.WeightedTokens >= o.MaxTokens) Limit("token budget", $"it used its token budget ({o.MaxTokens:N0})");
                            if (stopReason == null && o.MaxCostUsd > 0 && meter.Cost(o) >= o.MaxCostUsd) Limit("cost budget", $"it reached its cost budget (${o.MaxCostUsd:0.00})");
                            break;
                        }
                        case "result":
                        {
                            sawResult = true;
                            resultText = JStr(ev, "result");
                            if (ev.TryGetProperty("usage", out var ru) && ru.ValueKind == JsonValueKind.Object)
                            {
                                var total = CliUsage.From(ru);
                                if (total.Input + total.Output + total.CacheRead + total.CacheWrite > 0) meter.SetUsage(total.Input, total.Output, total.CacheRead, total.CacheWrite);
                            }
                            if (ev.TryGetProperty("total_cost_usd", out var cost) && cost.ValueKind == JsonValueKind.Number && cost.TryGetDecimal(out var usd)) meter.ReportedCostUsd = usd;
                            var subtype = JStr(ev, "subtype");
                            if (subtype == "error_max_turns") { stopReason ??= "step limit"; stopNote ??= $"it used all {o.MaxSteps} steps"; }
                            else if (subtype == "error_max_budget_usd") { stopReason ??= "cost budget"; stopNote ??= $"it reached its cost budget (${o.MaxCostUsd:0.00})"; }
                            else if (ev.TryGetProperty("is_error", out var isErr) && isErr.ValueKind == JsonValueKind.True) resultError = resultText ?? subtype ?? "unknown error";
                            break;
                        }
                    }
                }
                await process.WaitForExitAsync(ct);
            }
            finally
            {
                watchDone.Cancel();
                KillTree(process); // a no-op after a normal exit; on cancel or the time limit this ends the CLI and everything it started
            }
            try { await stderrTask.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None); } catch (TimeoutException) { }
            var why = ""; lock (stderr) why = stderr.ToString().Trim();

            await sp.GetRequiredService<AiClient>().RecordUsageAsync(Feature + ".claude-cli", run.RequestedById, run.ProjectId, meter.Input, meter.Output, meter.CacheRead, meter.CacheWrite, CancellationToken.None);
            if (bridge.ToolLimitHit && stopReason == null) { stopReason = "tool call limit"; stopNote = $"it used all {o.MaxToolCalls} tool calls"; }
            if (resultError != null)
            {
                var hint = System.Text.RegularExpressions.Regex.IsMatch(resultError, "log ?in|authenticat|credential|api key|unauthor|oauth", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
                    ? " Sign in again with the claude command as the server's account (or set CLAUDE_CODE_OAUTH_TOKEN)." : "";
                throw new InvalidOperationException("The Claude Code CLI reported an error: " + M(Truncate(resultError, 400)) + hint);
            }
            if (!sawResult && stopReason == null && process.ExitCode != 0)
                throw new InvalidOperationException($"The Claude Code CLI exited with code {process.ExitCode}" + (why.Length > 0 ? ": " + M(Truncate(why, 500)) : "."));
            var final = !string.IsNullOrWhiteSpace(resultText) ? resultText : lastText.ToString();
            return new LoopResult(final, stopReason, stopNote);
        }
        finally
        {
            if (process != null) { KillTree(process); process.Dispose(); }
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { log.LogWarning(ex, "Couldn't delete the run directory {Dir}", dir); }
        }
    }
}
