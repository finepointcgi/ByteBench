using System.ComponentModel.DataAnnotations;

namespace ByteBench.Data;

/// <summary>
/// The computed insights of one repository (one row per repository), valid for the default-branch head <see cref="HeadSha"/>:
/// a push moves the head, so the next view recomputes it. <see cref="DataJson"/> is the aggregate (daily buckets, contributors,
/// languages); see <c>Services/Insights</c>.
/// </summary>
public class RepositoryInsight
{
    public int Id { get; set; }
    public int RepositoryId { get; set; }
    public Repository Repository { get; set; } = null!;
    [Required, MaxLength(250)] public string Branch { get; set; } = "";
    [Required, MaxLength(40)] public string HeadSha { get; set; } = "";
    public DateTime ComputedAt { get; set; } = DateTime.UtcNow;
    /// <summary>Commits read from <c>git log</c>.</summary>
    public int CommitsScanned { get; set; }
    /// <summary>The history was longer than <c>Insights:MaxCommits</c> (or the time limit hit): older commits aren't counted.</summary>
    public bool Truncated { get; set; }
    public string DataJson { get; set; } = "{}";
}

public enum DependencySchedule { Daily, Weekly, Monthly }

/// <summary>How far an update may jump: Patch (1.2.3 → 1.2.x), Minor (1.x.y) or Major (anything).</summary>
public enum DependencyStrategy { Patch, Minor, Major }

/// <summary>A repository's dependency bot settings (one row per repository). Absent row = the bot is off.</summary>
public class DependencyBotSettings
{
    public int Id { get; set; }
    public int RepositoryId { get; set; }
    public Repository Repository { get; set; } = null!;
    public bool Enabled { get; set; }
    public DependencySchedule Schedule { get; set; } = DependencySchedule.Weekly;
    /// <summary>Comma-separated: npm, nuget, pip, go.</summary>
    [Required, MaxLength(100)] public string Ecosystems { get; set; } = "npm,nuget,pip,go";
    public DependencyStrategy Strategy { get; set; } = DependencyStrategy.Minor;
    /// <summary>One pull request for all updates of a run instead of one per package.</summary>
    public bool Grouped { get; set; }
    public int MaxOpenPullRequests { get; set; } = 5;
    /// <summary>One entry per line: <c>name</c> or <c>name@range</c> (e.g. <c>left-pad@&gt;=2</c>); a trailing <c>*</c> in the name matches a prefix.</summary>
    [MaxLength(4000)] public string? IgnoreList { get; set; }
    /// <summary>Branch the updates are based on and merged into (null: the default branch).</summary>
    [MaxLength(250)] public string? TargetBranch { get; set; }
    /// <summary>Comma-separated; pull requests have no labels in ByteBench, so they are written into the description.</summary>
    [MaxLength(500)] public string? Labels { get; set; }
    /// <summary>Comma-separated user names added as reviewers.</summary>
    [MaxLength(500)] public string? Reviewers { get; set; }
    public DateTime? NextRunAt { get; set; }
    public DateTime? LastRunAt { get; set; }
    [MaxLength(40)] public string? LastStatus { get; set; }
    [MaxLength(1000)] public string? LastMessage { get; set; }
    /// <summary>The "Outdated dependencies" table of the last check (JSON).</summary>
    public string? LastReportJson { get; set; }
}

/// <summary>A pull request the dependency bot opened for one package: remembered so it isn't opened again (open, or closed unmerged at that version).</summary>
public class DependencyBotPullRequest
{
    public int Id { get; set; }
    public int RepositoryId { get; set; }
    [Required, MaxLength(20)] public string Ecosystem { get; set; } = "";
    [Required, MaxLength(300)] public string Package { get; set; } = "";
    [MaxLength(100)] public string FromVersion { get; set; } = "";
    [Required, MaxLength(100)] public string ToVersion { get; set; } = "";
    [MaxLength(300)] public string Branch { get; set; } = "";
    public int PullRequestId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
