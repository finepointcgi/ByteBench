namespace ByteBench.Services.Editor.Ai;

/// <summary>Which engine runs the web editor's assistant (<c>Ai:Editor:Backend</c>).</summary>
public enum EditorAiBackendKind
{
    /// <summary>Claude Code if it is installed on the server, else the Anthropic API if configured, else off.</summary>
    Auto,
    /// <summary>The Anthropic API through <see cref="ByteBench.Services.Ai.AiClient"/> (server-side tool loop).</summary>
    Api,
    /// <summary>Claude Code installed on the server, run headless in a temporary copy of the workspace.</summary>
    ClaudeCode,
}

/// <summary>Configuration section <c>Ai:Editor</c>.</summary>
public class EditorAiOptions
{
    public EditorAiBackendKind Backend { get; set; } = EditorAiBackendKind.Auto;
    /// <summary>Tool calls per request before the loop is stopped (API backend).</summary>
    public int MaxToolCalls { get; set; } = 15;
    /// <summary>max_tokens of each model turn (API backend).</summary>
    public int MaxTokensPerTurn { get; set; } = 16000;
    /// <summary>Output tokens per request across all turns before the loop is stopped (API backend).</summary>
    public int MaxOutputTokensPerRequest { get; set; } = 48000;
    /// <summary>Assistant requests (chat and inline actions) per user per minute.</summary>
    public int RequestsPerMinute { get; set; } = 6;
    /// <summary>Inline completions per user per minute.</summary>
    public int CompletionsPerMinute { get; set; } = 20;
    public int CompletionMaxTokens { get; set; } = 256;
}

/// <summary>Configuration section <c>Ai:ClaudeCode</c> (the <see cref="EditorAiBackendKind.ClaudeCode"/> backend).</summary>
public class ClaudeCodeOptions
{
    /// <summary>The <c>claude</c> executable: a name looked up on PATH, or a full path.</summary>
    public string Path { get; set; } = "claude";
    /// <summary>Optional model (alias or full name) passed as <c>--model</c>.</summary>
    public string? Model { get; set; }
    /// <summary>Wall-clock limit of one run; the process tree is killed after it.</summary>
    public int TimeoutSeconds { get; set; } = 300;
    /// <summary>Agentic turns per run (passed as <c>--max-turns</c> when the CLI lists it, and enforced on the event stream).</summary>
    public int MaxTurns { get; set; } = 15;
    /// <summary>Claude Code runs at once across the server.</summary>
    public int MaxConcurrent { get; set; } = 2;
    /// <summary>Caps on what is copied into the temporary workspace.</summary>
    public int MaxFiles { get; set; } = 5000;
    public long MaxBytes { get; set; } = 50L * 1024 * 1024;
}

public enum ProposedEditKind { Create, Modify, Delete }

public enum ProposalState { Pending, Accepted, Rejected }

/// <summary>
/// A change the assistant proposes. Never applied by itself: the user accepts it (it then goes into the workspace, still
/// uncommitted) or rejects it. <see cref="OriginalText"/> is the workspace content the proposal was made against.
/// </summary>
public sealed class ProposedEdit
{
    public Guid Id { get; } = Guid.NewGuid();
    public required string Path { get; init; }
    public required ProposedEditKind Kind { get; init; }
    public string? OriginalText { get; init; }
    public string? NewText { get; init; }
    public string? Summary { get; init; }
    public ProposalState State { get; set; } = ProposalState.Pending;
}

public sealed record EditorChatMessage(bool FromUser, string Text);

/// <summary>One assistant request: the user's message plus what the editor shows (open file, selection, attached files).</summary>
public sealed record EditorAiRequest
{
    public required WorkspaceSnapshot Snapshot { get; init; }
    public required int UserId { get; init; }
    public required string Message { get; init; }
    /// <summary>Usage feature name (AiUsage): editor.chat, editor.action.refactor…</summary>
    public string Feature { get; init; } = "editor.chat";
    public string? OpenFile { get; init; }
    public string? Selection { get; init; }
    public int? SelectionStartLine { get; init; }
    public int? SelectionEndLine { get; init; }
    public IReadOnlyList<string> AttachedFiles { get; init; } = [];
    public IReadOnlyList<EditorChatMessage> History { get; init; } = [];
    /// <summary>Searches the workspace (git grep at the base + working copies), as the user.</summary>
    public Func<string, bool, CancellationToken, Task<(List<EditorSearchHit> Hits, string? Error)>>? Search { get; init; }
    public int ProjectId => Snapshot.Repository.ProjectId;
}

/// <summary>Receives an assistant run's progress for the chat panel.</summary>
public interface IEditorAiSink
{
    Task TextAsync(string delta);
    Task ToolAsync(string tool, string detail);
    Task ProposalAsync(ProposedEdit edit);
}

public sealed record EditorAiResult(string Text, int ToolCalls, IReadOnlyList<ProposedEdit> Proposals, string? Error = null, string? StopNote = null);

/// <summary>An engine for the assistant: the Anthropic API tool loop or Claude Code.</summary>
public interface IEditorAiBackend
{
    EditorAiBackendKind Kind { get; }
    string DisplayName { get; }
    /// <summary>Whether it can run now (API key configured / Claude Code found), without side effects beyond detection.</summary>
    Task<bool> IsAvailableAsync(CancellationToken ct = default);
    Task<EditorAiResult> RunAsync(EditorAiRequest request, IEditorAiSink sink, CancellationToken ct);
}

/// <summary>A sink that drops everything (tests, background runs).</summary>
public sealed class NullEditorAiSink : IEditorAiSink
{
    public static readonly NullEditorAiSink Instance = new();
    public Task TextAsync(string delta) => Task.CompletedTask;
    public Task ToolAsync(string tool, string detail) => Task.CompletedTask;
    public Task ProposalAsync(ProposedEdit edit) => Task.CompletedTask;
}
