using System.Text.Json;
using Anthropic.Models.Beta;
using Anthropic.Models.Beta.Messages;

namespace ByteBench.Services.Ai;

/// <summary>
/// Result of <see cref="AiClient.CompleteJsonAsync{T}"/>. <see cref="Value"/> is null when the model refused,
/// ran out of tokens before finishing the JSON, or returned something that doesn't match <typeparamref name="T"/>;
/// <see cref="Error"/> then says which.
/// </summary>
public record AiJsonResult<T>(T? Value, string Raw, string? StopReason, bool Refused, string? Error) where T : class
{
    public bool Ok => Value != null && Error == null;
}

public partial class AiClient
{
    /// <summary>Case-insensitive, snake_case-friendly options used to read structured output into C# records.</summary>
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    /// <summary>
    /// Structured-output completion: Claude's answer is constrained to <paramref name="schema"/> (a JSON Schema object,
    /// see <see cref="Schema"/>) and deserialized into <typeparamref name="T"/>. <paramref name="system"/> should be stable
    /// across calls so it is cached. Usage is recorded under <paramref name="feature"/>.
    /// Never throws for refusals, truncation or malformed JSON; check <see cref="AiJsonResult{T}.Ok"/>.
    /// </summary>
    public async Task<AiJsonResult<T>> CompleteJsonAsync<T>(string feature, string system, string user,
        IReadOnlyDictionary<string, JsonElement> schema, Effort effort, int maxTokens = 16000,
        int? userId = null, int? projectId = null, CancellationToken ct = default) where T : class
    {
        var response = await Client.Beta.Messages.Create(new MessageCreateParams
        {
            Model = Model,
            MaxTokens = maxTokens,
            Betas = [AnthropicBeta.ServerSideFallback2026_07_01],
            Fallbacks = new Default(),
            OutputConfig = new BetaOutputConfig
            {
                Effort = effort,
                Format = new BetaJsonOutputFormat { Schema = schema.ToDictionary(kv => kv.Key, kv => kv.Value) },
            },
            System = new List<BetaTextBlockParam> { new() { Text = system, CacheControl = new BetaCacheControlEphemeral() } },
            Messages = [new BetaUserMessageParam { Content = user }],
        }, ct);

        await RecordUsageAsync(feature, userId, projectId,
            response.Usage.InputTokens, response.Usage.OutputTokens,
            response.Usage.CacheReadInputTokens ?? 0, response.Usage.CacheCreationInputTokens ?? 0, ct);

        var stop = response.StopReason?.Raw();
        var text = string.Concat(response.Content.Select(b => b.TryPickText(out var t) ? t.Text : ""));
        var result = ParseJson<T>(text, stop);
        if (result.Refused) log.LogWarning("Claude declined a {Feature} request", feature);
        else if (result.Error != null) log.LogWarning("Unusable {Feature} response ({Stop}): {Error}", feature, stop, result.Error);
        return result;
    }

    /// <summary>
    /// Pure parsing step of <see cref="CompleteJsonAsync{T}"/>: interprets the stop reason and deserializes the text.
    /// A <c>max_tokens</c> stop is still parsed (the JSON may be complete) but reported as an error if it isn't.
    /// </summary>
    public static AiJsonResult<T> ParseJson<T>(string text, string? stopReason) where T : class
    {
        if (stopReason == "refusal")
            return new(null, text, stopReason, true, "Claude declined to answer this request.");
        if (string.IsNullOrWhiteSpace(text))
            return new(null, text, stopReason, false, stopReason == "max_tokens"
                ? "The response ran out of tokens before any output was written."
                : "The response was empty.");
        try
        {
            var value = JsonSerializer.Deserialize<T>(text.Trim(), JsonOptions);
            return value == null
                ? new(null, text, stopReason, false, "The response was JSON null.")
                : new(value, text, stopReason, false, null);
        }
        catch (JsonException ex)
        {
            var why = stopReason == "max_tokens" ? "The response was cut off (max_tokens) before the JSON was complete." : $"The response wasn't valid JSON: {ex.Message}";
            return new(null, text, stopReason, false, why);
        }
    }

    /// <summary>Builds a structured-output schema from a JSON Schema literal, e.g. <c>Schema("""{"type":"object",…}""")</c>.</summary>
    public static IReadOnlyDictionary<string, JsonElement> Schema(string jsonSchema)
    {
        using var doc = JsonDocument.Parse(jsonSchema);
        return doc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
    }
}
