using Anthropic;
using Anthropic.Models.Beta;
using Anthropic.Models.Beta.Messages;
using GitBench.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GitBench.Services.Ai;

/// <summary>Configuration section "Ai" in appsettings.json.</summary>
public class AiOptions
{
    /// <summary>Master switch. When false (or no credentials are found), every AI feature hides itself.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Claude model used by every feature unless a feature overrides it.</summary>
    public string Model { get; set; } = "claude-opus-5-5";

    /// <summary>Optional API key. Prefer the ANTHROPIC_API_KEY environment variable or user-secrets.</summary>
    public string? ApiKey { get; set; }
}

/// <summary>Result of a single Claude call made through <see cref="AiClient.CompleteAsync"/>.</summary>
public record AiResult(string Text, string? StopReason, bool Refused);

/// <summary>
/// Shared entry point to Claude for every AI feature in GitBench. Owns the SDK client, the model choice,
/// refusal fallbacks and usage logging, so features only build prompts and parse answers.
/// Register as a singleton.
/// </summary>
public partial class AiClient(IOptions<AiOptions> options, IDbContextFactory<AppDbContext> dbFactory, ILogger<AiClient> log)
{
    private readonly AiOptions opts = options.Value;
    private AnthropicClient? client;

    private string? ApiKey => !string.IsNullOrWhiteSpace(opts.ApiKey) ? opts.ApiKey
        : Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY") is { Length: > 0 } k ? k : null;

    /// <summary>True when AI is enabled and an API key is available. UI should hide AI actions otherwise.</summary>
    public bool IsConfigured => opts.Enabled && ApiKey != null;

    public string Model => opts.Model;

    /// <summary>The underlying SDK client, for features that need streaming or tool use.</summary>
    public AnthropicClient Client => client ??= IsConfigured
        ? new AnthropicClient { ApiKey = ApiKey! }
        : throw new InvalidOperationException("AI is not configured. Set ANTHROPIC_API_KEY or Ai:ApiKey.");

    /// <summary>
    /// One-shot text completion. <paramref name="system"/> should be stable across calls so it can be cached.
    /// Safety refusals are re-served by a fallback model server-side; <see cref="AiResult.Refused"/> is true if it still refused.
    /// </summary>
    public async Task<AiResult> CompleteAsync(string feature, string system, string user, Effort effort,
        int maxTokens = 16000, int? userId = null, int? projectId = null, CancellationToken ct = default)
    {
        var response = await Client.Beta.Messages.Create(new MessageCreateParams
        {
            Model = Model,
            MaxTokens = maxTokens,
            Betas = [AnthropicBeta.ServerSideFallback2026_07_01],
            Fallbacks = new Default(),
            OutputConfig = new BetaOutputConfig { Effort = effort },
            System = new List<BetaTextBlockParam> { new() { Text = system, CacheControl = new BetaCacheControlEphemeral() } },
            Messages = [new BetaUserMessageParam { Content = user }],
        }, ct);

        await RecordUsageAsync(feature, userId, projectId,
            response.Usage.InputTokens, response.Usage.OutputTokens,
            response.Usage.CacheReadInputTokens ?? 0, response.Usage.CacheCreationInputTokens ?? 0, ct);

        var stop = response.StopReason?.Raw();
        var text = string.Concat(response.Content.Select(b => b.TryPickText(out var t) ? t.Text : ""));
        if (stop == "refusal") log.LogWarning("Claude declined a {Feature} request", feature);
        return new AiResult(text, stop, stop == "refusal");
    }

    /// <summary>Logs token usage for cost reporting. Call this after every API call you make directly via <see cref="Client"/>.</summary>
    public async Task RecordUsageAsync(string feature, int? userId, int? projectId,
        long input, long output, long cacheRead, long cacheWrite, CancellationToken ct = default)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            db.AiUsages.Add(new AiUsage
            {
                Feature = feature, Model = Model, UserId = userId, ProjectId = projectId,
                InputTokens = input, OutputTokens = output, CacheReadTokens = cacheRead, CacheWriteTokens = cacheWrite,
            });
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex) { log.LogError(ex, "Failed to record AI usage for {Feature}", feature); }
    }
}
