using System.ComponentModel.DataAnnotations;

namespace ByteBench.Data;

public enum TestPriority { Low, Medium, High, Critical }

/// <summary>Manual cases are executed by a person in a run; automated cases get their results from CI or the API.</summary>
public enum TestCaseType { Manual, Automated }

/// <summary><see cref="NotRun"/> is the state of a case in a run before anyone recorded a result.</summary>
public enum TestResultStatus { NotRun, Passed, Failed, Blocked, Skipped }

/// <summary>Where a run came from.</summary>
public enum TestRunSource { Manual, Ci, Api }

/// <summary>
/// A test case of a project (Services/Tests). <see cref="Number"/> is the per-project display number (TC-12).
/// Editing the text bumps <see cref="Version"/> and keeps the previous text as a <see cref="TestCaseRevision"/>.
/// </summary>
public class TestCase
{
    public int Id { get; set; }
    public int ProjectId { get; set; }
    public int Number { get; set; }
    [Required, MaxLength(200)] public string Title { get; set; } = "";
    public string? Preconditions { get; set; }
    /// <summary>Plain text or Markdown, one step per line.</summary>
    public string? Steps { get; set; }
    public string? Expected { get; set; }
    public TestPriority Priority { get; set; } = TestPriority.Medium;
    public TestCaseType Type { get; set; } = TestCaseType.Manual;
    /// <summary>Comma-separated tags and components.</summary>
    [MaxLength(300)] public string? Tags { get; set; }
    /// <summary>Folder / suite path, e.g. <c>Checkout/Payments</c>.</summary>
    [MaxLength(200)] public string? Suite { get; set; }
    /// <summary>Comma-separated issue numbers of the project this case covers.</summary>
    [MaxLength(300)] public string? LinkedIssues { get; set; }
    /// <summary>Free-text requirement references (e.g. REQ-12, a URL).</summary>
    [MaxLength(500)] public string? Requirements { get; set; }
    /// <summary>How automated results name this case: <c>Namespace.Class.Method</c> or the JUnit/TRX test name.</summary>
    [MaxLength(400)] public string? AutomatedName { get; set; }
    public int Version { get; set; } = 1;
    public bool IsArchived { get; set; }
    public int? CreatedById { get; set; }
    public int? UpdatedById { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public List<TestCaseRevision> Revisions { get; set; } = [];

    public string Key => $"TC-{Number}";
    public IEnumerable<string> TagList => (Tags ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    public IEnumerable<int> LinkedIssueNumbers => (LinkedIssues ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(s => int.TryParse(s, out var n) ? n : 0).Where(n => n > 0);
}

/// <summary>The text of a case as it was at <see cref="Version"/> (the current version lives in <see cref="TestCase"/>).</summary>
public class TestCaseRevision
{
    public int Id { get; set; }
    public int TestCaseId { get; set; }
    public TestCase TestCase { get; set; } = null!;
    public int Version { get; set; }
    [Required, MaxLength(200)] public string Title { get; set; } = "";
    public string? Preconditions { get; set; }
    public string? Steps { get; set; }
    public string? Expected { get; set; }
    public TestPriority Priority { get; set; }
    [MaxLength(300)] public string? Tags { get; set; }
    [MaxLength(200)] public string? Suite { get; set; }
    public int? EditedById { get; set; }
    public DateTime EditedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>A named selection of cases that is run again and again (regression, smoke, release acceptance).</summary>
public class TestPlan
{
    public int Id { get; set; }
    public int ProjectId { get; set; }
    [Required, MaxLength(120)] public string Name { get; set; } = "";
    public string? Description { get; set; }
    public int? CreatedById { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public List<TestPlanCase> Cases { get; set; } = [];
}

public class TestPlanCase
{
    public int PlanId { get; set; }
    public TestPlan Plan { get; set; } = null!;
    public int TestCaseId { get; set; }
    public TestCase TestCase { get; set; } = null!;
    public int Order { get; set; }
}

/// <summary>
/// One execution of a set of cases against a version: a tag, a release, a CI pipeline build or just a label.
/// A manual run gets a <see cref="TestResult"/> row per case (NotRun until recorded); CI and API runs get rows for the cases they report.
/// </summary>
public class TestRun
{
    public int Id { get; set; }
    public int ProjectId { get; set; }
    public int Number { get; set; }
    [Required, MaxLength(200)] public string Name { get; set; } = "";
    public int? PlanId { get; set; }
    public TestPlan? Plan { get; set; }
    /// <summary>The build or version label shown in the results matrix (defaults to the tag, else the run number).</summary>
    [MaxLength(200)] public string? Build { get; set; }
    [MaxLength(250)] public string? Tag { get; set; }
    [MaxLength(250)] public string? Ref { get; set; }
    [MaxLength(40)] public string? CommitSha { get; set; }
    public int? RepositoryId { get; set; }
    public int? ReleaseId { get; set; }
    public int? PipelineId { get; set; }
    public TestRunSource Source { get; set; } = TestRunSource.Manual;
    public bool IsClosed { get; set; }
    public int? CreatedById { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ClosedAt { get; set; }
    public List<TestResult> Results { get; set; } = [];

    public string Label => !string.IsNullOrWhiteSpace(Build) ? Build! : !string.IsNullOrWhiteSpace(Tag) ? Tag! : $"Run #{Number}";
}

/// <summary>The outcome of one case in one run (one row per case and run).</summary>
public class TestResult
{
    public int Id { get; set; }
    public int RunId { get; set; }
    public TestRun Run { get; set; } = null!;
    public int TestCaseId { get; set; }
    public TestCase TestCase { get; set; } = null!;
    /// <summary>The case's <see cref="TestCase.Version"/> when the result was recorded.</summary>
    public int CaseVersion { get; set; }
    public TestResultStatus Status { get; set; } = TestResultStatus.NotRun;
    public int? ExecutedById { get; set; }
    public DateTime? ExecutedAt { get; set; }
    public string? Comment { get; set; }
    /// <summary>Failure message / stack trace of an automated result (truncated).</summary>
    public string? ErrorMessage { get; set; }
    public long? DurationMs { get; set; }
    /// <summary>Number of the bug issue created from this failure.</summary>
    public int? IssueNumber { get; set; }
    public List<TestEvidence> Evidence { get; set; } = [];
}

/// <summary>A screenshot, log or other file attached to a result. Stored like issue attachments (random name, never the user's).</summary>
public class TestEvidence
{
    public int Id { get; set; }
    public int ResultId { get; set; }
    public TestResult Result { get; set; } = null!;
    [Required, MaxLength(32)] public string StoredName { get; set; } = "";
    [Required, MaxLength(255)] public string FileName { get; set; } = "";
    [Required, MaxLength(100)] public string ContentType { get; set; } = "application/octet-stream";
    public long Size { get; set; }
    [Required, MaxLength(64)] public string Sha256 { get; set; } = "";
    public int? UploaderId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Per repository: CI artifacts of the pipeline that built a release's commit are copied onto the release as assets when it is
/// published (or when a matching pipeline finishes later), if the file path matches one of <see cref="Patterns"/>.
/// </summary>
public class ReleaseAutoAttachRule
{
    public int Id { get; set; }
    public int RepositoryId { get; set; }
    public bool Enabled { get; set; }
    /// <summary>One glob per line (<c>dist/*.zip</c>, <c>**/*.nupkg</c>), matched against paths inside the job's artifact zip.</summary>
    [MaxLength(2000)] public string Patterns { get; set; } = "";
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
