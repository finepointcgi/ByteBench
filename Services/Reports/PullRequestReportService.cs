using GitBench.Data;
using GitBench.Services.Git;
using Microsoft.EntityFrameworkCore;

namespace GitBench.Services.Reports;

/// <param name="Size">Lines changed (additions + deletions), or null when the diff could not be measured.</param>
public sealed record PullRequestRow(int Id, string Repository, int Number, string Title, string Author, PullRequestState State, bool IsDraft, DateTime CreatedAt,
    DateTime? FirstReviewAt, double? HoursToFirstReview, DateTime? MergedAt, double? HoursToMerge, int? Size, int? Files, string? SizeClass)
{
    public string Url(string projectKey) => $"/p/{projectKey}/repos/{Repository}/pull-requests/{Number}";
}

public sealed record SizeBucket(string Name, string Rule, int Count);

public sealed record PullRequestTrendPoint(DateTime Period, int Merged, double? MedianHoursToMerge, double? MedianHoursToFirstReview);

public sealed record PullRequestReport(int PullRequests, int Merged, int Reviewed, Percentiles? TimeToFirstReviewHours, Percentiles? TimeToMergeHours,
    Percentiles? Size, int Measured, IReadOnlyList<SizeBucket> SizeBuckets, IReadOnlyList<PullRequestTrendPoint> Trend, IReadOnlyList<PullRequestRow> Rows);

/// <summary>
/// Pull request metrics for pull requests <i>created</i> in the range, across the project's repositories.
/// <b>Time to first review</b>: from creation to the first review verdict or comment by someone other than the author
/// (SQL aggregate). <b>Time to merge</b>: creation to merge, merged pull requests only. <b>Size</b>: lines added plus
/// removed against the merge base (measured from git for at most <see cref="MaxMeasured"/> of the newest pull requests;
/// the merged/declined tips stored on the pull request are used when present, otherwise the live branches).
/// Needs ViewProject.
/// </summary>
public class PullRequestReportService(IDbContextFactory<AppDbContext> dbFactory, ProjectAccess access, RepoStorage storage)
{
    public const int MaxMeasured = 300;
    public const int MaxRows = 2000;

    public static readonly (string Name, int Max, string Rule)[] Sizes =
        [("XS", 10, "up to 10 lines"), ("S", 50, "11–50"), ("M", 250, "51–250"), ("L", 1000, "251–1000"), ("XL", int.MaxValue, "over 1000")];

    public static string SizeClass(int lines) => Sizes.First(s => lines <= s.Max).Name;

    private sealed record Raw(int Id, int RepositoryId, string Slug, int Number, string Title, string Author, PullRequestState State, bool IsDraft,
        DateTime CreatedAt, DateTime? ClosedAt, DateTime? FirstReview, DateTime? FirstComment, string TargetBranch, string SourceBranch, bool IsCrossFork,
        string? SourceSha, string? TargetSha);

    public async Task<PullRequestReport> ReportAsync(int projectId, ReportFilter filter)
    {
        await access.RequireAsync(projectId, Permission.ViewProject);
        var (from, to, toEx, _) = filter.Resolve();
        await using var db = await dbFactory.CreateDbContextAsync();
        var project = await db.Projects.AsNoTracking().FirstAsync(p => p.Id == projectId);

        var raw = await db.PullRequests.AsNoTracking()
            .Where(p => p.Repository.ProjectId == projectId && p.CreatedAt >= from && p.CreatedAt < toEx)
            .OrderByDescending(p => p.CreatedAt).Take(MaxRows)
            .Select(p => new Raw(p.Id, p.RepositoryId, p.Repository.Slug, p.Number, p.Title, p.Author.DisplayName, p.State, p.IsDraft, p.CreatedAt, p.ClosedAt,
                p.Reviews.Where(r => r.UserId != p.AuthorId).Min(r => (DateTime?)r.CreatedAt),
                p.Comments.Where(c => c.AuthorId != p.AuthorId).Min(c => (DateTime?)c.CreatedAt),
                p.TargetBranch, p.SourceBranch, p.IsCrossFork, p.SourceCommitSha, p.TargetCommitSha)).ToListAsync();

        // Size comes from git, for the newest pull requests only.
        var sizes = new Dictionary<int, (int Lines, int Files)>();
        await Task.Run(() =>
        {
            foreach (var p in raw.Take(MaxMeasured))
            {
                try
                {
                    var path = storage.PathFor(project.Key, p.Slug);
                    var cmp = p.State != PullRequestState.Open && p.SourceSha != null && p.TargetSha != null
                        ? GitOps.Compare(path, p.TargetSha, p.SourceSha, 1)
                        : p.State == PullRequestState.Open
                            ? GitOps.Compare(path, p.TargetBranch, p.IsCrossFork ? PullRequest.PullHeadRef(p.Number) : p.SourceBranch, 1) : null;
                    if (cmp != null) sizes[p.Id] = (cmp.Files.Sum(f => f.Additions + f.Deletions), cmp.Files.Count);
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { /* repository missing or ref gone */ }
            }
        });

        var rows = raw.Select(p =>
        {
            DateTime? first = p.FirstReview is { } r && p.FirstComment is { } c ? (r < c ? r : c) : p.FirstReview ?? p.FirstComment;
            DateTime? merged = p.State == PullRequestState.Merged ? p.ClosedAt : null;
            var size = sizes.TryGetValue(p.Id, out var s) ? s : ((int Lines, int Files)?)null;
            return new PullRequestRow(p.Id, p.Slug, p.Number, p.Title, p.Author, p.State, p.IsDraft, p.CreatedAt, first,
                first is { } f ? Math.Round(ReportMath.Hours(f - p.CreatedAt), 2) : null, merged,
                merged is { } m ? Math.Round(ReportMath.Hours(m - p.CreatedAt), 2) : null, size?.Lines, size?.Files, size is { } z ? SizeClass(z.Lines) : null);
        }).ToList();

        var firstH = rows.Where(r => r.HoursToFirstReview != null).Select(r => r.HoursToFirstReview!.Value).ToList();
        var mergeH = rows.Where(r => r.HoursToMerge != null).Select(r => r.HoursToMerge!.Value).ToList();
        var sizeL = rows.Where(r => r.Size != null).Select(r => (double)r.Size!.Value).ToList();
        var buckets = Sizes.Select((s, k) => new SizeBucket(s.Name, s.Rule, rows.Count(r => r.SizeClass == s.Name))).ToList();
        var trend = ReportMath.Periods(from, to, filter.Monthly).Select(p =>
        {
            var next = ReportMath.NextPeriod(p, filter.Monthly);
            var inP = rows.Where(r => r.CreatedAt >= p && r.CreatedAt < next).ToList();
            var mg = inP.Where(r => r.HoursToMerge != null).Select(r => r.HoursToMerge!.Value).ToList();
            var fr = inP.Where(r => r.HoursToFirstReview != null).Select(r => r.HoursToFirstReview!.Value).ToList();
            return new PullRequestTrendPoint(p, mg.Count, mg.Count == 0 ? null : Math.Round(ReportMath.Percentile(mg, 50), 2),
                fr.Count == 0 ? null : Math.Round(ReportMath.Percentile(fr, 50), 2));
        }).ToList();

        return new PullRequestReport(rows.Count, mergeH.Count, firstH.Count, ReportMath.Summarize(firstH), ReportMath.Summarize(mergeH),
            ReportMath.Summarize(sizeL), sizeL.Count, buckets, trend, rows);
    }
}
