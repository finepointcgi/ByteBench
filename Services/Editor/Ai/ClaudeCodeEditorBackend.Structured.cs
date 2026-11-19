using System.Text.Json;
using ByteBench.Services.Ai;

namespace ByteBench.Services.Editor.Ai;

public sealed partial class ClaudeCodeEditorBackend
{
    /// <summary>Every tool Claude Code has: a structured request (AI pull request review) may use none of them.</summary>
    public const string NoTools = "Bash,Read,Edit,Write,MultiEdit,Grep,Glob,WebFetch,WebSearch,NotebookEdit,Task,TodoWrite";

    /// <summary>The command line for a structured request: one turn, no tools, nothing prompts.</summary>
    public IReadOnlyList<string> BuildStructuredArguments(ClaudeCodeStatus status, string system)
    {
        var args = new List<string> { "-p", "--output-format", "stream-json", "--verbose" };
        void Flag(string flag, params string[] values)
        {
            if (!status.Supports(flag)) return;
            args.Add(flag);
            args.AddRange(values);
        }
        Flag("--tools", "");
        args.AddRange(["--disallowedTools", NoTools]);
        args.AddRange(["--permission-mode", "dontAsk"]);
        Flag("--permission-prompts", "none");
        Flag("--restricted");
        Flag("--strict-mcp-config");
        Flag("--no-session-persistence");
        Flag("--disable-slash-commands");
        Flag("--max-turns", "1");
        if (!string.IsNullOrWhiteSpace(opts.Model)) args.AddRange(["--model", opts.Model]);
        args.AddRange(["--append-system-prompt", system]);
        return args;
    }

    /// <summary>
    /// A one-shot structured answer from Claude Code: run headless in a fresh empty private directory (no repository files, no
    /// repository-supplied configuration), all tools disallowed, the prompt on stdin with the JSON schema spelled out, the
    /// reply parsed as JSON. Shares the concurrency gate and timeout of the editor runs.
    /// </summary>
    public async Task<AiJsonResult<T>> CompleteJsonAsync<T>(StructuredAiRequest request, CancellationToken ct) where T : class
    {
        var status = await detector.GetAsync(false, ct);
        if (!status.Installed || status.ExecutablePath == null)
            return new(null, "", null, false, "Claude Code isn't available on the server: " + (status.Error ?? "not found."));
        if (!await gate.WaitAsync(TimeSpan.FromSeconds(30), ct))
            return new(null, "", null, false, "The server is busy with other Claude Code requests. Try again in a moment.");
        string? dir = null;
        try
        {
            dir = CreatePrivateDirectory();
            var prompt = request.User + "\n\nReply with only one JSON object that matches this JSON schema, with no text around it:\n"
                         + JsonSerializer.Serialize(request.Schema);
            var run = await RunProcessAsync(status, dir, prompt, NullEditorAiSink.Instance, BuildStructuredArguments(status, request.System), ct);
            if (run.Usage is { } u)
                await ai.RecordUsageAsync(request.Feature + ".claude-code", request.UserId, request.ProjectId, u.Input, u.Output, u.CacheRead, u.CacheWrite, CancellationToken.None);
            if (run.Error != null) return new(null, run.Text, null, false, run.Error);
            return AiClient.ParseJson<T>(AiJsonText.ExtractObject(run.Text), null);
        }
        finally
        {
            gate.Release();
            if (dir != null) DeleteDirectory(dir);
        }
    }
}
