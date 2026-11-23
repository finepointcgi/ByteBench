namespace ByteBench.Services.Insights;

/// <summary>Configuration section <c>Insights</c>.</summary>
public sealed class InsightsOptions
{
    /// <summary>Most commits read from the default branch's history; older ones are not counted (the page says so).</summary>
    public int MaxCommits { get; set; } = 50_000;
    /// <summary><c>git log</c> is killed after this long; what was read so far is used (marked truncated).</summary>
    public int TimeoutSeconds { get; set; } = 120;
    /// <summary>Most files looked at for the language breakdown.</summary>
    public int MaxFiles { get; set; } = 100_000;
    /// <summary>How long the REST endpoint waits for a computation before answering 202.</summary>
    public int ApiWaitSeconds { get; set; } = 20;
}

/// <summary>The cached aggregate (JSON in <c>RepositoryInsight.DataJson</c>). Daily buckets are <c>[commits, additions, deletions]</c> per UTC day.</summary>
public sealed class InsightsData
{
    public Dictionary<string, int[]> Days { get; set; } = new();
    public List<ContributorData> Contributors { get; set; } = [];
    /// <summary>Commits per UTC hour of the day (24 entries) and per weekday (Sunday = 0, 7 entries).</summary>
    public int[] Hours { get; set; } = new int[24];
    public int[] Weekdays { get; set; } = new int[7];
    public List<LanguageStat> Languages { get; set; } = [];
    public string? FirstCommit { get; set; }
    public string? LastCommit { get; set; }
}

public sealed class ContributorData
{
    public string Name { get; set; } = "";
    public string Email { get; set; } = "";
    public Dictionary<string, int[]> Days { get; set; } = new();
}

public sealed record LanguageStat(string Name, string Color, int Files, long Bytes, double Percent);

public enum InsightsStatus { Ready, Computing, Empty, Failed }

public sealed record ContributorView(string Name, string Email, int Commits, int Additions, int Deletions, string? UserName, string? DisplayName);
public sealed record WeekCommits(string WeekStart, int Commits);
public sealed record WeekLines(string WeekStart, int Additions, int Deletions);
public sealed record DayCommits(string Date, int Commits);
public sealed record MonthPullRequests(string Month, int Opened, int Merged, double? MedianHoursToMerge);

/// <summary>Everything the page and the REST endpoint show, for one range.</summary>
public sealed class InsightsView
{
    public InsightsStatus Status { get; set; }
    public string? Message { get; set; }
    public string Range { get; set; } = "1y";
    public string? Branch { get; set; }
    public string? HeadSha { get; set; }
    public DateTime? ComputedAt { get; set; }
    public int CommitsScanned { get; set; }
    public bool Truncated { get; set; }
    /// <summary>True when the numbers shown are for an older head than the current one (a newer computation is running).</summary>
    public bool Stale { get; set; }
    public List<ContributorView> Contributors { get; set; } = [];
    /// <summary>Commits per week for the last 52 weeks (weeks start on Monday, UTC).</summary>
    public List<WeekCommits> CommitActivity { get; set; } = [];
    /// <summary>Commits per day for the last 52 weeks.</summary>
    public List<DayCommits> Heatmap { get; set; } = [];
    public List<WeekLines> CodeFrequency { get; set; } = [];
    public List<LanguageStat> Languages { get; set; } = [];
    public int[] Hours { get; set; } = new int[24];
    public int[] Weekdays { get; set; } = new int[7];
    public List<MonthPullRequests> PullRequests { get; set; } = [];
}
