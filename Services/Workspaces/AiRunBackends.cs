using ByteBench.Services.Accounts;
using ByteBench.Services.Editor.Ai;
using Microsoft.Extensions.Options;

namespace ByteBench.Services.Workspaces;

/// <summary>What a task-driven AI run talks to: the Anthropic API (a tool loop run by ByteBench) or the Claude Code CLI on the server (a tool bridge, see <see cref="AiRunToolBridge"/>).</summary>
public enum AiRunBackendKind { None, Api, ClaudeCli }

/// <param name="Setting">What is configured: <c>auto</c>, <c>api</c> or <c>claude-cli</c> (the site administrator's choice, else <c>Workspaces:AiRuns:Backend</c>).</param>
/// <param name="Active">The backend runs use now, or <see cref="AiRunBackendKind.None"/>.</param>
/// <param name="Explanation">Why this backend is the active one, or why no run can start.</param>
public sealed record AiRunBackendInfo(string Setting, AiRunBackendKind Active, string Explanation, bool ApiConfigured, bool CliInstalled, string? CliVersion, string? CliProblem, bool FromSite)
{
    public bool Available => Active != AiRunBackendKind.None;

    public string Label => Active switch { AiRunBackendKind.Api => "Anthropic API", AiRunBackendKind.ClaudeCli => "Claude Code CLI", _ => "none" };
}

/// <summary>
/// Chooses the backend of AI runs. <c>auto</c> (default) prefers the Anthropic API when a key is configured and otherwise uses the Claude Code CLI when it is
/// installed on the server; <c>api</c> and <c>claude-cli</c> insist on one (a run is then unavailable when it is missing, never silently moved to the other).
/// The setting comes from the site administrator (Admin → AI) when they chose one, else from <c>Workspaces:AiRuns:Backend</c>.
/// </summary>
public sealed class AiRunBackendResolver(IOptionsMonitor<WorkspaceOptions> options, IEditorChatModel model, ClaudeCodeDetector detector, SiteSettings site)
{
    public const string SettingKey = "workspaces.airuns.backend";
    public static readonly string[] Settings = ["auto", "api", "claude-cli"];

    public const string NotSetUp = "AI isn't set up: configure a Claude API key (ANTHROPIC_API_KEY or Ai:ApiKey), or install the Claude Code CLI on the server and sign in (Ai:ClaudeCode:Path).";

    public static string Normalize(string? value) => (value ?? "").Trim().ToLowerInvariant() switch
    {
        "api" or "anthropic" => "api",
        "claude-cli" or "claudecli" or "cli" or "claude-code" or "claudecode" => "claude-cli",
        _ => "auto",
    };

    /// <summary>The site administrator's choice (null: none, use the configuration).</summary>
    public async Task<string?> GetSiteSettingAsync(CancellationToken ct = default) =>
        await site.GetAsync(SettingKey, ct) is { Length: > 0 } v ? Normalize(v) : null;

    public async Task<AiRunBackendInfo> ResolveAsync(CancellationToken ct = default, bool refreshCli = false)
    {
        var fromSite = await GetSiteSettingAsync(ct);
        var setting = fromSite ?? Normalize(options.CurrentValue.AiRuns.Backend);
        var api = model.IsConfigured;
        var cli = await detector.GetAsync(refreshCli, ct);
        string? cliProblem = null;
        if (!cli.Installed) cliProblem = cli.Error ?? "The Claude Code CLI wasn't found on the server.";
        else if (!cli.Supports("--mcp-config")) cliProblem = $"Claude Code {cli.Version} doesn't list --mcp-config; update it (ByteBench gives the run its tools through MCP).";
        var cliUsable = cliProblem == null;
        var source = fromSite != null ? "set by a site administrator" : "from Workspaces:AiRuns:Backend";

        AiRunBackendInfo Make(AiRunBackendKind kind, string why) => new(setting, kind, why, api, cli.Installed, cli.Version, cliProblem, fromSite != null);

        switch (setting)
        {
            case "api":
                return api
                    ? Make(AiRunBackendKind.Api, $"The backend is the Anthropic API ({source}).")
                    : Make(AiRunBackendKind.None, $"AI runs are set to use the Anthropic API ({source}) but no Claude API key is configured (ANTHROPIC_API_KEY or Ai:ApiKey).");
            case "claude-cli":
                return cliUsable
                    ? Make(AiRunBackendKind.ClaudeCli, $"The backend is the Claude Code CLI on the server ({source}).")
                    : Make(AiRunBackendKind.None, $"AI runs are set to use the Claude Code CLI ({source}) but it can't be used: {cliProblem}");
            default:
                if (api) return Make(AiRunBackendKind.Api, "Automatic: a Claude API key is configured, so runs use the Anthropic API.");
                if (cliUsable) return Make(AiRunBackendKind.ClaudeCli, "Automatic: no Claude API key is configured, so runs use the Claude Code CLI on the server (its login or subscription).");
                return Make(AiRunBackendKind.None, NotSetUp + $" (Claude Code CLI: {cliProblem})");
        }
    }
}
