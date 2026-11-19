using System.Text;
using System.Text.Json;
using ByteBench.Services.Ai;
using Microsoft.Extensions.Options;

namespace ByteBench.Services.Editor.Ai;

/// <summary>A tool call the model made in one turn. <see cref="Input"/> is the parsed input object (validate before use).</summary>
public sealed record EditorModelToolCall(string Id, string Name, JsonElement Input);

/// <summary>One model turn: its text, tool calls, stop reason and token usage.</summary>
public sealed record EditorModelTurn(string Text, IReadOnlyList<EditorModelToolCall> ToolCalls, string? StopReason,
    long InputTokens, long OutputTokens, long CacheReadTokens = 0, long CacheWriteTokens = 0);

public sealed record EditorToolResult(string ToolUseId, string Content, bool IsError);

/// <summary>What a conversation with the model starts from.</summary>
public sealed record EditorChatSetup(string System, IReadOnlyList<EditorToolDefinition> Tools, IReadOnlyList<EditorChatMessage> History,
    string UserMessage, int MaxTokensPerTurn, IReadOnlyDictionary<string, JsonElement>? OutputSchema = null);

/// <summary>
/// The model behind the API backend, abstracted so tests can script it (<c>FakeEditorChatModel</c>). The real one is
/// <see cref="AnthropicEditorChatModel"/>; a conversation object keeps the SDK's own message history (thinking blocks included).
/// </summary>
public interface IEditorChatModel
{
    bool IsConfigured { get; }
    IEditorChatConversation Begin(EditorChatSetup setup);
}

public interface IEditorChatConversation
{
    /// <summary>Sends the conversation so far and returns the next assistant turn, streaming its text to <paramref name="onText"/>.</summary>
    Task<EditorModelTurn> NextAsync(Func<string, Task> onText, CancellationToken ct);
    /// <summary>Answers the last turn's tool calls (one result per call, in one user message).</summary>
    void AddToolResults(IReadOnlyList<EditorToolResult> results);
}

/// <summary>
/// The assistant through the Anthropic API: a server-side tool-use loop over <see cref="EditorAiTools"/>, capped at
/// <see cref="EditorAiOptions.MaxToolCalls"/> tool calls and <see cref="EditorAiOptions.MaxOutputTokensPerRequest"/> output
/// tokens. Every turn's usage is logged to AiUsage under the request's feature name.
/// </summary>
public sealed class ApiEditorBackend(IEditorChatModel model, AiClient ai, IOptions<EditorAiOptions> options, ILogger<ApiEditorBackend> log) : IEditorAiBackend, IStructuredAiBackend
{
    private readonly EditorAiOptions opts = options.Value;

    public EditorAiBackendKind Kind => EditorAiBackendKind.Api;
    public string DisplayName => "Claude (Anthropic API)";

    public Task<bool> IsAvailableAsync(CancellationToken ct = default) => Task.FromResult(model.IsConfigured);

    public async Task<EditorAiResult> RunAsync(EditorAiRequest request, IEditorAiSink sink, CancellationToken ct)
    {
        var tools = new EditorAiTools(request);
        var boundary = EditorAiPrompts.NewBoundary();
        var user = EditorAiPrompts.BuildUserMessage(request, p => tools.Current(p).Text, inlineAttachments: true, boundary);
        var conversation = model.Begin(new EditorChatSetup(EditorAiPrompts.ApiSystem, EditorAiTools.Definitions, request.History, user, opts.MaxTokensPerTurn));

        var text = new StringBuilder();
        var toolCalls = 0;
        long outputTokens = 0;
        string? stopNote = null;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var turn = await conversation.NextAsync(async delta => { text.Append(delta); await sink.TextAsync(delta); }, ct);
            outputTokens += turn.OutputTokens;
            await ai.RecordUsageAsync(request.Feature, request.UserId, request.ProjectId, turn.InputTokens, turn.OutputTokens,
                turn.CacheReadTokens, turn.CacheWriteTokens, CancellationToken.None);

            if (turn.StopReason == "refusal")
            {
                stopNote = "Claude declined this request.";
                break;
            }
            if (turn.ToolCalls.Count == 0 || turn.StopReason is not ("tool_use" or null))
            {
                if (turn.StopReason == "max_tokens") stopNote = "The reply hit the length limit and was cut short.";
                break;
            }
            if (toolCalls + turn.ToolCalls.Count > opts.MaxToolCalls)
            {
                stopNote = $"Stopped after {toolCalls} tool calls (the limit is {opts.MaxToolCalls}). Ask again to continue.";
                break;
            }
            if (outputTokens >= opts.MaxOutputTokensPerRequest)
            {
                stopNote = $"Stopped: this request used its token budget ({opts.MaxOutputTokensPerRequest:N0} output tokens).";
                break;
            }

            var results = new List<EditorToolResult>();
            foreach (var call in turn.ToolCalls)
            {
                toolCalls++;
                await sink.ToolAsync(call.Name, EditorAiTools.Describe(call.Name, call.Input));
                var before = tools.Proposals.Count;
                var (content, isError) = await tools.ExecuteAsync(call.Name, call.Input, ct);
                if (isError) log.LogDebug("Editor AI tool {Tool} failed: {Error}", call.Name, content);
                if (call.Name == "propose_edit" && !isError && tools.Proposals.Count >= before && tools.Proposals.Count > 0)
                    await sink.ProposalAsync(tools.Proposals[^1]);
                results.Add(new EditorToolResult(call.Id, content, isError));
            }
            conversation.AddToolResults(results);
            if (text.Length > 0 && !text.ToString().EndsWith('\n')) { text.Append("\n\n"); await sink.TextAsync("\n\n"); }
        }
        return new EditorAiResult(text.ToString().Trim(), toolCalls, tools.Proposals.ToList(), null, stopNote);
    }

    /// <summary>
    /// One model turn with no tools and the answer constrained to the request's schema (the real model enforces it; either way
    /// the text is parsed and validated by the caller). Usage is logged under the request's feature name.
    /// </summary>
    public async Task<AiJsonResult<T>> CompleteJsonAsync<T>(StructuredAiRequest request, CancellationToken ct) where T : class
    {
        var conversation = model.Begin(new EditorChatSetup(request.System, [], [], request.User, request.MaxTokens, request.Schema));
        var turn = await conversation.NextAsync(_ => Task.CompletedTask, ct);
        await ai.RecordUsageAsync(request.Feature, request.UserId, request.ProjectId, turn.InputTokens, turn.OutputTokens,
            turn.CacheReadTokens, turn.CacheWriteTokens, CancellationToken.None);
        var result = AiClient.ParseJson<T>(AiJsonText.ExtractObject(turn.Text), turn.StopReason);
        if (result.Refused) log.LogWarning("Claude declined a {Feature} request", request.Feature);
        return result;
    }
}
