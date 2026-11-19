using Anthropic.Models.Beta.Messages;

namespace ByteBench.Services.Ai.Triage;

/// <summary>
/// The one place AI triage talks to a model. The default implementation goes through the shared <see cref="AiClient"/>
/// (structured output, cached system prompt, refusal fallback, usage logging); tests replace it with a fake that returns canned JSON.
/// </summary>
public interface ITriageModel
{
    /// <summary>False when no model is configured; triage then hides itself and never queues anything.</summary>
    bool IsAvailable { get; }

    /// <summary>Asks for a structured answer matching <paramref name="schemaJson"/>. Never throws for refusals or malformed output (see <see cref="AiJsonResult{T}.Ok"/>).</summary>
    Task<AiJsonResult<TriageDto>> SuggestAsync(string system, string prompt, string schemaJson, int? userId, int projectId, CancellationToken ct);
}

public sealed class AiClientTriageModel(AiClient ai) : ITriageModel
{
    public bool IsAvailable => ai.IsConfigured;

    public Task<AiJsonResult<TriageDto>> SuggestAsync(string system, string prompt, string schemaJson, int? userId, int projectId, CancellationToken ct) =>
        ai.CompleteJsonAsync<TriageDto>("issue-triage", system, prompt, AiClient.Schema(schemaJson), Effort.Low, 1500, userId, projectId, ct);
}
