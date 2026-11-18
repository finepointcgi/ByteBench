using System.Text;
using System.Text.Json;
using Anthropic;
using Anthropic.Helpers;
using Anthropic.Models.Beta;
using Anthropic.Models.Beta.Messages;
using GitBench.Services.Ai;

namespace GitBench.Services.Editor.Ai;

/// <summary>
/// <see cref="IEditorChatModel"/> on the Anthropic C# SDK through <see cref="AiClient"/>: streaming beta Messages calls
/// (text deltas go to the chat panel as they arrive) with the editor's client tools, server-side refusal fallbacks and a
/// cached system prompt. Each conversation keeps the full SDK message history, so thinking blocks are passed back unchanged.
/// </summary>
public sealed class AnthropicEditorChatModel(AiClient ai) : IEditorChatModel
{
    public bool IsConfigured => ai.IsConfigured;

    public IEditorChatConversation Begin(EditorChatSetup setup) => new Conversation(ai, setup);

    private sealed class Conversation : IEditorChatConversation
    {
        private readonly AiClient ai;
        private readonly EditorChatSetup setup;
        private readonly List<BetaMessageParam> messages = [];
        private readonly List<BetaToolUnion> tools;

        public Conversation(AiClient ai, EditorChatSetup setup)
        {
            this.ai = ai;
            this.setup = setup;
            tools = setup.Tools.Select(t => (BetaToolUnion)new BetaTool
            {
                Name = t.Name,
                Description = t.Description,
                InputSchema = new() { Properties = t.Properties.ToDictionary(), Required = t.Required.ToList() },
                // Large inputs (whole files in propose_edit) stream as they are generated; inputs are validated by EditorAiTools.
                EagerInputStreaming = true,
            }).ToList();
            foreach (var h in setup.History)
                messages.Add(h.FromUser ? new BetaUserMessageParam { Content = h.Text } : new BetaAssistantMessageParam { Content = h.Text });
            messages.Add(new BetaUserMessageParam { Content = setup.UserMessage });
        }

        public async Task<EditorModelTurn> NextAsync(Func<string, Task> onText, CancellationToken ct)
        {
            var parameters = new MessageCreateParams
            {
                Model = ai.Model,
                MaxTokens = setup.MaxTokensPerTurn,
                Betas = [AnthropicBeta.ServerSideFallback2026_07_01],
                Fallbacks = new Default(),
                OutputConfig = new BetaOutputConfig { Effort = Effort.Medium },
                System = new List<BetaTextBlockParam> { new() { Text = setup.System, CacheControl = new BetaCacheControlEphemeral() } },
                Tools = tools,
                Messages = messages.ToList(),
            };
            var aggregator = new BetaMessageContentAggregator();
            await foreach (var ev in ai.Client.Beta.Messages.CreateStreaming(parameters, ct).CollectAsync(aggregator))
            {
                if (ev.TryPickContentBlockDelta(out var delta) && delta.Delta.TryPickText(out var t) && t.Text.Length > 0)
                    await onText(t.Text);
            }
            var response = aggregator.Message();

            var assistant = new List<BetaContentBlockParam>();
            var calls = new List<EditorModelToolCall>();
            var text = new StringBuilder();
            foreach (var block in response.Content)
            {
                if (block.TryPickText(out var tb))
                {
                    assistant.Add(new BetaTextBlockParam { Text = tb.Text });
                    text.Append(tb.Text);
                }
                else if (block.TryPickThinking(out var th))
                    assistant.Add(new BetaThinkingBlockParam { Thinking = th.Thinking, Signature = th.Signature });
                else if (block.TryPickRedactedThinking(out var rt))
                    assistant.Add(new BetaRedactedThinkingBlockParam { Data = rt.Data });
                else if (block.TryPickToolUse(out var tu))
                {
                    assistant.Add(new BetaToolUseBlockParam { ID = tu.ID, Name = tu.Name, Input = tu.Input });
                    calls.Add(new EditorModelToolCall(tu.ID, tu.Name, JsonSerializer.SerializeToElement(tu.Input)));
                }
            }
            if (assistant.Count > 0) messages.Add(new BetaAssistantMessageParam { Content = assistant });

            var stop = response.StopReason?.Raw();
            var usage = response.Usage;
            return new EditorModelTurn(text.ToString(), calls, stop, usage.InputTokens, usage.OutputTokens,
                usage.CacheReadInputTokens ?? 0, usage.CacheCreationInputTokens ?? 0);
        }

        public void AddToolResults(IReadOnlyList<EditorToolResult> results)
        {
            if (results.Count == 0) return;
            messages.Add(new BetaUserMessageParam
            {
                Content = results.Select(r => (BetaContentBlockParam)new BetaToolResultBlockParam
                {
                    ToolUseID = r.ToolUseId,
                    Content = r.Content,
                    IsError = r.IsError,
                }).ToList(),
            });
        }
    }
}
