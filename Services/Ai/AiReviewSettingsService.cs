using GitBench.Data;
using GitBench.Services.Audit;
using GitBench.Services.Editor.Ai;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GitBench.Services.Ai;

/// <summary>Configuration section <c>Ai:Review</c>: server-wide guardrails for AI pull request review.</summary>
public class AiReviewOptions
{
    /// <summary>Reviews a repository may start per hour (all triggers); further requests are refused until the window moves on.</summary>
    public int MaxReviewsPerRepoPerHour { get; set; } = 12;
    /// <summary>The most a Maintainer can raise a repository's "max diff size" to.</summary>
    public int MaxDiffCharsLimit { get; set; } = 1_000_000;
    /// <summary>Findings kept from one review (the most severe first); the rest are counted in the summary.</summary>
    public int MaxFindings { get; set; } = 30;
    /// <summary>Seconds to wait after a push before reviewing, so a burst of pushes is reviewed once.</summary>
    public int PushDebounceSeconds { get; set; } = 30;
}

/// <summary>Whether AI review can run on this server right now, and which backend would run it.</summary>
public sealed record AiReviewAvailability(bool Available, string? BackendName, string? Reason);

/// <summary>What a Maintainer edits on the repository's AI review settings page.</summary>
public sealed record AiReviewSettingsInput(bool Enabled, AiReviewMode Mode, int MaxDiffChars, string? IgnorePaths, string? FocusPrompt,
    bool BlockOnUnresolvedFindings);

/// <summary>
/// Per-repository AI review settings (Maintainer+; everything off by default) and whether the server's AI backend
/// (<c>Ai:Editor:Backend</c>, the same one the web editor uses) is available.
/// </summary>
public class AiReviewSettingsService(IDbContextFactory<AppDbContext> dbFactory, ProjectAccess access, AuditLog audit,
    IEnumerable<IEditorAiBackend> backends, IOptions<EditorAiOptions> editorOptions, IOptions<AiReviewOptions> options)
{
    public const int MaxIgnoreLines = 50;
    public const int MinDiffChars = 5_000;

    public async Task<AiReviewAvailability> GetAvailabilityAsync(CancellationToken ct = default)
    {
        var backend = await EditorAiBackendSelector.SelectAsync(backends, editorOptions.Value.Backend, ct);
        if (backend is not IStructuredAiBackend)
        {
            return new AiReviewAvailability(false, null, editorOptions.Value.Backend switch
            {
                EditorAiBackendKind.ClaudeCode => "AI isn't set up: Claude Code wasn't found on the server (Ai:ClaudeCode:Path).",
                EditorAiBackendKind.Api => "AI isn't set up: no Claude API key is configured (ANTHROPIC_API_KEY or Ai:ApiKey).",
                _ => "AI isn't set up: configure a Claude API key or install Claude Code on the server.",
            });
        }
        return new AiReviewAvailability(true, backend.DisplayName, null);
    }

    /// <summary>The repository's settings; a repository without a row has AI review off.</summary>
    public async Task<RepositoryAiReviewSettings> GetAsync(int repositoryId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.RepositoryAiReviewSettings.AsNoTracking().FirstOrDefaultAsync(s => s.RepositoryId == repositoryId, ct)
               ?? new RepositoryAiReviewSettings { RepositoryId = repositoryId };
    }

    /// <summary>Validates and saves the settings. Maintainer+. Audited.</summary>
    public async Task<RepositoryAiReviewSettings> SaveAsync(int repositoryId, AiReviewSettingsInput input, int userId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var repo = await db.Repositories.AsNoTracking().FirstOrDefaultAsync(r => r.Id == repositoryId)
                   ?? throw new InvalidOperationException("Repository not found.");
        await access.RequireRoleAsync(repo.ProjectId, ProjectRole.Maintainer, "change AI review settings", userId);

        var limit = Math.Max(MinDiffChars, options.Value.MaxDiffCharsLimit);
        if (input.MaxDiffChars < MinDiffChars || input.MaxDiffChars > limit)
            throw new InvalidOperationException($"Max diff size must be between {MinDiffChars:N0} and {limit:N0} characters.");
        if (!Enum.IsDefined(input.Mode)) throw new InvalidOperationException("Unknown review trigger.");
        var ignore = NormalizeIgnore(input.IgnorePaths);
        var focus = string.IsNullOrWhiteSpace(input.FocusPrompt) ? null : input.FocusPrompt.Trim();
        if (focus is { Length: > 2000 }) throw new InvalidOperationException("The focus prompt is too long (max 2,000 characters).");

        var row = await db.RepositoryAiReviewSettings.FirstOrDefaultAsync(s => s.RepositoryId == repositoryId);
        if (row == null) db.RepositoryAiReviewSettings.Add(row = new RepositoryAiReviewSettings { RepositoryId = repositoryId });
        row.Enabled = input.Enabled;
        row.Mode = input.Mode;
        row.MaxDiffChars = input.MaxDiffChars;
        row.IgnorePaths = ignore;
        row.FocusPrompt = focus;
        row.BlockOnUnresolvedFindings = input.BlockOnUnresolvedFindings;
        row.UpdatedAt = DateTime.UtcNow;
        row.UpdatedById = userId;
        await db.SaveChangesAsync();
        await audit.RecordAsync("repository.ai_review_settings_changed", "repository", repositoryId, repo.ProjectId, new
        {
            enabled = row.Enabled, mode = row.Mode.ToString(), maxDiffChars = row.MaxDiffChars,
            ignoreLines = ignore?.Split('\n').Length ?? 0, hasFocusPrompt = focus != null, blockOnUnresolved = row.BlockOnUnresolvedFindings,
        }, actorId: userId);
        return row;
    }

    /// <summary>One pattern per line; blank lines and duplicates dropped; at most <see cref="MaxIgnoreLines"/> of at most 200 characters.</summary>
    public static string? NormalizeIgnore(string? text)
    {
        var lines = (text ?? "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')).Distinct().ToList();
        if (lines.Count > MaxIgnoreLines) throw new InvalidOperationException($"At most {MaxIgnoreLines} ignore patterns.");
        if (lines.Any(l => l.Length > 200 || l.Any(char.IsWhiteSpace))) throw new InvalidOperationException("Ignore patterns can't contain spaces and are at most 200 characters.");
        return lines.Count == 0 ? null : string.Join('\n', lines);
    }

    /// <summary>A predicate for the gitignore-style patterns in <paramref name="text"/> (same syntax as CODEOWNERS).</summary>
    public static Func<string, bool> IgnoreMatcher(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return _ => false;
        var file = CodeOwnersFile.Parse(string.Join('\n', text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')).Select(l => l + " x")));
        var matchers = file.Rules.Select(r => r.Matcher).ToList();
        return path => matchers.Any(m => m.IsMatch(path));
    }
}
