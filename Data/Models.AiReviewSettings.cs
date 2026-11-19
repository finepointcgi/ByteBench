using System.ComponentModel.DataAnnotations;

namespace GitBench.Data;

/// <summary>When a repository's pull requests are reviewed by GitBench AI without anyone asking.</summary>
public enum AiReviewMode
{
    /// <summary>Only when someone clicks "Review with AI".</summary>
    OnDemand,
    /// <summary>When a pull request is opened.</summary>
    OnOpen,
    /// <summary>When a pull request is opened and again after each push to its source branch.</summary>
    OnPush,
}

/// <summary>
/// Per-repository AI pull request review settings (Maintainer+). Absent row = everything off. Read by
/// <see cref="GitBench.Services.Ai.AiReviewSettingsService"/>.
/// </summary>
public class RepositoryAiReviewSettings
{
    public int Id { get; set; }
    public int RepositoryId { get; set; }
    /// <summary>Master switch (off by default). Reviews also need the server's AI backend to be available.</summary>
    public bool Enabled { get; set; }
    public AiReviewMode Mode { get; set; } = AiReviewMode.OnDemand;
    /// <summary>Most diff characters sent to the model per review; larger pull requests are reviewed partly and say so.</summary>
    public int MaxDiffChars { get; set; } = 240_000;
    /// <summary>Gitignore-style patterns (one per line) of files that are never sent to the model.</summary>
    [MaxLength(4000)] public string? IgnorePaths { get; set; }
    /// <summary>Extra guidance from the maintainers appended to the review request ("pay attention to SQL injection").</summary>
    [MaxLength(2000)] public string? FocusPrompt { get; set; }
    /// <summary>Merge check: no unresolved blocking (bug or security) AI findings. Off by default.</summary>
    public bool BlockOnUnresolvedFindings { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public int? UpdatedById { get; set; }
}

public partial class AiReview
{
    /// <summary>Which backend ran it ("Claude (Anthropic API)", "Claude Code (on the server)").</summary>
    [MaxLength(60)] public string? Backend { get; set; }
    /// <summary>Diff characters sent to the model.</summary>
    public int DiffChars { get; set; }
}

public partial class PullRequestComment
{
    /// <summary>The AI review that posted this comment (null for human comments).</summary>
    public int? AiReviewId { get; set; }
    /// <summary>"bug", "security", "suggestion" or "nit" on an AI finding's thread root.</summary>
    [MaxLength(20)] public string? AiSeverity { get; set; }
    /// <summary>Stable hash of the finding (file + title) so a later review doesn't post the same finding again.</summary>
    [MaxLength(40)] public string? AiFingerprint { get; set; }
    /// <summary>The finding was dismissed (resolved as not applicable) rather than fixed.</summary>
    public bool AiDismissed { get; set; }
}
