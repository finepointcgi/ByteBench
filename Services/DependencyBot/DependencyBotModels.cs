namespace ByteBench.Services.DependencyBot;

/// <summary>Status of a row of the "Outdated dependencies" table.</summary>
public static class ItemStatus
{
    /// <summary>A newer version the strategy allows exists and no pull request covers it.</summary>
    public const string Update = "update";
    /// <summary>An open bot pull request already exists for this package.</summary>
    public const string PullRequestOpen = "pr-open";
    /// <summary>The bot's pull request for this version was closed without merging, so it isn't proposed again.</summary>
    public const string Closed = "closed";
    /// <summary>Newer versions exist, but the update strategy or the ignore list holds them back.</summary>
    public const string HeldBack = "held-back";
}

/// <summary>One outdated direct dependency in one manifest.</summary>
public sealed class OutdatedItem
{
    public string Ecosystem { get; set; } = "";
    public string Name { get; set; } = "";
    public string Manifest { get; set; } = "";
    /// <summary>e.g. dependencies / devDependencies (npm), project / central (NuGet).</summary>
    public string Group { get; set; } = "";
    public string Current { get; set; } = "";
    /// <summary>The version the bot would update to (null when held back).</summary>
    public string? Proposed { get; set; }
    /// <summary>The newest stable version the registry lists.</summary>
    public string? Latest { get; set; }
    /// <summary>major / minor / patch: from Current to Proposed (or to Latest when held back).</summary>
    public string? Kind { get; set; }
    public string Status { get; set; } = ItemStatus.Update;
    public string? Note { get; set; }
    public string? ReleaseUrl { get; set; }
    /// <summary>The existing bot pull request (open, or the one closed unmerged).</summary>
    public int? PullRequestNumber { get; set; }
    /// <summary>Night Owl: an open dependency advisory exists for the current version.</summary>
    public string? SecurityNote { get; set; }
    public string Key => $"{Ecosystem}|{Manifest}|{Name}";
}

public sealed class DependencyReport
{
    public DateTime CheckedAt { get; set; } = DateTime.UtcNow;
    public string? Branch { get; set; }
    public string? HeadSha { get; set; }
    public int ManifestsRead { get; set; }
    public int PackagesChecked { get; set; }
    public List<OutdatedItem> Items { get; set; } = [];
    /// <summary>Per-package and per-registry problems; a check never fails because of one.</summary>
    public List<string> Warnings { get; set; } = [];
}

public sealed class ApplyResult
{
    /// <summary>Numbers of the pull requests opened.</summary>
    public List<int> PullRequests { get; set; } = [];
    public List<string> Warnings { get; set; } = [];
}

/// <summary>What the settings form edits.</summary>
public sealed class DependencyBotInput
{
    public bool Enabled { get; set; }
    public Data.DependencySchedule Schedule { get; set; } = Data.DependencySchedule.Weekly;
    public List<string> Ecosystems { get; set; } = [.. DependencyBot.Ecosystems.All];
    public Data.DependencyStrategy Strategy { get; set; } = Data.DependencyStrategy.Minor;
    public bool Grouped { get; set; }
    public int MaxOpenPullRequests { get; set; } = 5;
    public string? IgnoreList { get; set; }
    public string? TargetBranch { get; set; }
    public string? Labels { get; set; }
    public string? Reviewers { get; set; }
}
