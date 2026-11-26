using System.Text;
using ByteBench.Data;
using ByteBench.Services.Git;
using ByteBench.Services.Security;
using ByteBench.Services.Signing;
using ByteBench.Services.Tests;
using Microsoft.EntityFrameworkCore;

namespace ByteBench.Services.Releases;

public sealed record HubArtifact(int Id, int PipelineId, string JobName, long SizeBytes, int FileCount, DateTime CreatedAt, DateTime? ExpiresAt, bool Expired);

public sealed record HubPipeline(int Id, string Ref, PipelineStatus Status, DateTime CreatedAt, IReadOnlyList<HubArtifact> Artifacts);

public sealed record HubPackage(int PackageId, string Name, PackageType Type, string Version, string? ManifestDigest, long Size, bool ByPipeline);

public sealed record HubSecurity(int PipelineId, int Critical, int High, int Medium, int Low, int Other, int Dismissed, DateTime ScannedAt)
{
    public int Open => Critical + High + Medium + Low + Other;
}

public sealed record HubIssue(int Number, string Title, IssueStatus Status, string Via);

public sealed record HubPullRequest(int Number, string Title, string? Author);

/// <summary>Everything ByteBench knows about one release, gathered from the places it already lives.</summary>
public sealed class ReleaseHub
{
    public required Release Release { get; init; }
    public string? CommitSha { get; init; }
    public string? PreviousTag { get; init; }
    public CommitSignatureResult? Signature { get; init; }
    public IReadOnlyList<HubPipeline> Pipelines { get; init; } = [];
    public IReadOnlyList<HubPackage> Packages { get; init; } = [];
    public HubSecurity? Security { get; init; }
    public IReadOnlyList<ReleaseAsset> Sboms { get; init; } = [];
    public IReadOnlyList<HubIssue> Issues { get; init; } = [];
    public IReadOnlyList<HubPullRequest> PullRequests { get; init; } = [];
    public IReadOnlyList<TestRunRow> TestRuns { get; init; } = [];
    public ReleaseAutoAttachRule? AutoAttach { get; init; }
    public long TotalDownloads { get; init; }
    public bool TagMissing { get; init; }
    public string? CompareUrl { get; init; }
    public IReadOnlyList<HubIssue> NotFound { get; init; } = [];
}

/// <summary>One row of the project-level releases overview.</summary>
public sealed record ReleaseOverviewRow(Release Release, Repository Repository, bool IsLatest, int AssetCount, long Downloads, TestRunSummary? Tests, int? OpenFindings);

/// <summary>
/// The release hub (scoped): pulls together the release notes and assets that already exist with the CI artifacts, packages and
/// images published from the tagged commit, linked issues and pull requests, the commit's security scan, SBOM files, test runs,
/// the signature of the tagged commit and download counts. Reading needs ViewProject (drafts: PushCode, like the release itself);
/// changing the auto-attach rule needs ManageRepositories and attaching needs PushCode.
/// </summary>
public class ReleaseHubService(IDbContextFactory<AppDbContext> dbFactory, RepositoryService repos, ReleaseService releases, ProjectAccess access,
    CommitSignatureService signatures, TestService tests, ReleaseAutoAttachService autoAttach)
{
    /// <summary>Names a version is known by: the tag, without a leading v, with one.</summary>
    public static IReadOnlyList<string> VersionNames(string tag)
    {
        var bare = tag.Length > 1 && (tag[0] is 'v' or 'V') && char.IsDigit(tag[1]) ? tag[1..] : tag;
        return [tag, bare, "v" + bare];
    }

    public async Task<ReleaseHub?> GetAsync(Repository repo, int releaseId)
    {
        var release = await releases.GetAsync(repo, releaseId);
        if (release == null) return null;
        var tag = release.TagName;
        string? sha = null, previous = null;
        var tagMissing = false;
        List<CommitInfo> commits = [];
        await using var db = await dbFactory.CreateDbContextAsync();
        var others = await db.Releases.Where(r => r.RepositoryId == repo.Id && r.Id != release.Id).Select(r => r.TagName).ToListAsync();
        (sha, previous, commits) = await repos.Git(repo, p =>
        {
            if (!GitOps.Exists(p) || !GitOps.IsValidTagName(tag) || !GitOps.TagExists(p, tag)) return ((string?)null, (string?)null, new List<CommitInfo>());
            var s = GitOps.ResolveSha(p, "refs/tags/" + tag);
            var prev = GitOps.FindPreviousTag(p, tag, others);
            var (items, _) = GitOps.CommitsBetween(p, prev, tag, 300);
            return (s, prev, items);
        });
        tagMissing = sha == null;

        // Pipelines of the commit and their artifacts.
        var pipelines = new List<HubPipeline>();
        if (sha != null)
        {
            var ps = await db.Pipelines.AsNoTracking().Where(p => p.RepositoryId == repo.Id && p.CommitSha == sha).OrderByDescending(p => p.Id).Take(10).ToListAsync();
            var ids = ps.Select(p => p.Id).ToList();
            var arts = await db.PipelineArtifacts.AsNoTracking().Include(a => a.Job).Where(a => ids.Contains(a.PipelineId)).ToListAsync();
            var now = DateTime.UtcNow;
            pipelines = ps.Select(p => new HubPipeline(p.Id, p.Ref, p.Status, p.CreatedAt,
                arts.Where(a => a.PipelineId == p.Id).OrderBy(a => a.Job.Name)
                    .Select(a => new HubArtifact(a.Id, a.PipelineId, a.Job.Name, a.SizeBytes, a.FileCount, a.CreatedAt, a.ExpiresAt, a.ExpiresAt < now)).ToList())).ToList();
        }

        // Packages and images: published by those pipelines, or versioned/tagged like the release.
        var pipelineIds = pipelines.Select(p => (int?)p.Id).ToList();
        var names = VersionNames(tag);
        var pvs = await db.PackageVersions.AsNoTracking().Include(v => v.Package)
            .Where(v => v.Package.ProjectId == repo.ProjectId && ((v.PipelineId != null && pipelineIds.Contains(v.PipelineId)) || names.Contains(v.Version)))
            .OrderBy(v => v.Package.Name).ThenBy(v => v.Version).Take(100).ToListAsync();
        var packages = pvs.Select(v => new HubPackage(v.PackageId, v.Package.Name, v.Package.Type, v.Version, v.ManifestDigest, v.Size,
            v.PipelineId != null && pipelineIds.Contains(v.PipelineId))).ToList();

        // Security scan of the commit.
        HubSecurity? security = null;
        if (sha != null && await SecuritySnapshots.ForCommitAsync(db, repo.Id, sha) is { } snap)
        {
            var open = snap.Open.ToList();
            int C(SecuritySeverity s) => open.Count(f => f.Severity == s);
            security = new HubSecurity(snap.PipelineId, C(SecuritySeverity.Critical), C(SecuritySeverity.High), C(SecuritySeverity.Medium), C(SecuritySeverity.Low),
                C(SecuritySeverity.Info) + C(SecuritySeverity.Unknown), snap.Findings.Count(f => !f.IsOpen), snap.CreatedAt);
        }

        // Issues: fix version or milestone named like the tag.
        var issues = new List<HubIssue>();
        var fixes = await db.IssueVersions.AsNoTracking().Where(iv => iv.Kind == VersionLinkKind.Fix && iv.Version.ProjectId == repo.ProjectId && names.Contains(iv.Version.Name))
            .Select(iv => iv.IssueId).ToListAsync();
        var viaMilestone = await db.Issues.AsNoTracking().Where(i => i.ProjectId == repo.ProjectId && i.Milestone != null && names.Contains(i.Milestone.Title)).Select(i => i.Id).ToListAsync();
        var wanted = fixes.Concat(viaMilestone).Distinct().Take(200).ToList();
        if (wanted.Count > 0)
        {
            var rows = await db.Issues.AsNoTracking().Where(i => wanted.Contains(i.Id)).OrderBy(i => i.Number).Select(i => new { i.Number, i.Title, i.Status, Fix = fixes.Contains(i.Id) }).ToListAsync();
            issues = rows.Select(r => new HubIssue(r.Number, r.Title, r.Status, r.Fix ? "fix version" : "milestone")).ToList();
        }

        // Pull requests merged since the previous release.
        var shas = commits.Select(c => c.Sha).ToHashSet();
        var prs = shas.Count == 0 ? [] : (await db.PullRequests.AsNoTracking().Include(p => p.Author)
            .Where(p => p.RepositoryId == repo.Id && p.State == PullRequestState.Merged && p.MergeCommitSha != null).ToListAsync())
            .Where(p => shas.Contains(p.MergeCommitSha!)).OrderBy(p => p.Number).Select(p => new HubPullRequest(p.Number, p.Title, p.Author?.UserName)).ToList();

        CommitSignatureResult? sig = null;
        if (sha != null) sig = await signatures.GetAsync(repo, sha);

        var sboms = release.Assets.Where(a => IsSbom(a.FileName)).ToList();
        var runs = await tests.RunsForVersionAsync(repo.ProjectId, sha, tag, release.Id, 5);

        return new ReleaseHub
        {
            Release = release, CommitSha = sha, PreviousTag = previous, Signature = sig, Pipelines = pipelines, Packages = packages, Security = security,
            Sboms = sboms, Issues = issues, PullRequests = prs, TestRuns = runs, TotalDownloads = release.Assets.Sum(a => (long)a.DownloadCount),
            TagMissing = tagMissing, AutoAttach = await autoAttach.GetRuleAsync(repo.Id),
            CompareUrl = previous == null ? null : PrUrls.Compare(repo, previous, tag),
        };
    }

    public static bool IsSbom(string fileName)
    {
        var n = fileName.ToLowerInvariant();
        return n.Contains("sbom") || n.EndsWith(".cdx.json") || n.EndsWith(".cdx.xml") || n.EndsWith(".spdx.json") || n.EndsWith(".spdx") || n.EndsWith("bom.json") || n.EndsWith("bom.xml");
    }

    /// <summary><c>sha256sum</c>-compatible text for the release's assets (two spaces, then the file name), sorted by name.</summary>
    public async Task<string?> ChecksumsAsync(Repository repo, int releaseId)
    {
        var r = await releases.GetAsync(repo, releaseId);
        if (r == null) return null;
        var sb = new StringBuilder();
        foreach (var a in r.Assets.OrderBy(a => a.FileName, StringComparer.Ordinal)) sb.Append(a.Sha256).Append("  ").Append(a.FileName).Append('\n');
        return sb.ToString();
    }

    /// <summary>Attach matching CI artifacts now (Developer+).</summary>
    public async Task<AttachResult> AttachNowAsync(Repository repo, int releaseId)
    {
        await access.RequireAsync(repo.ProjectId, Permission.PushCode);
        await RepositorySettingsService.EnsureNotArchivedAsync(dbFactory, repo.Id);
        if (await releases.GetAsync(repo, releaseId) == null) throw new InvalidOperationException("Release not found.");
        return await autoAttach.AttachAsync(repo, releaseId, await access.ResolveUserIdAsync(), manual: true);
    }

    public async Task SaveAutoAttachAsync(Repository repo, bool enabled, string? patterns)
    {
        await access.RequireAsync(repo.ProjectId, Permission.ManageRepositories);
        await autoAttach.SaveRuleAsync(repo.Id, enabled, patterns);
    }

    /// <summary>Releases across all repositories of the project the user can see, newest first, with headline numbers.</summary>
    public async Task<List<ReleaseOverviewRow>> OverviewAsync(int projectId)
    {
        await access.RequireAsync(projectId, Permission.ViewProject);
        var drafts = await access.CanAsync(projectId, Permission.PushCode);
        await using var db = await dbFactory.CreateDbContextAsync();
        var list = await db.Releases.AsNoTracking().Include(r => r.Repository).ThenInclude(r => r.Project).Include(r => r.Assets)
            .Where(r => r.Repository.ProjectId == projectId && (drafts || !r.IsDraft)).ToListAsync();
        list = list.OrderByDescending(r => r.IsDraft).ThenByDescending(r => r.PublishedAt ?? r.CreatedAt).ThenByDescending(r => r.Id).Take(200).ToList();
        var latest = list.Where(r => !r.IsDraft && !r.IsPrerelease).GroupBy(r => r.RepositoryId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(r => r.PublishedAt).ThenByDescending(r => r.Id).First().Id);
        var runs = await db.TestRuns.AsNoTracking().Where(r => r.ProjectId == projectId && r.ReleaseId != null).OrderByDescending(r => r.CreatedAt).ToListAsync();
        var sums = await tests.SummariesAsync(db, runs.Select(r => r.Id).ToList());
        var rows = new List<ReleaseOverviewRow>();
        foreach (var r in list)
        {
            var run = runs.FirstOrDefault(x => x.ReleaseId == r.Id);
            rows.Add(new ReleaseOverviewRow(r, r.Repository, latest.GetValueOrDefault(r.RepositoryId) == r.Id, r.Assets.Count,
                r.Assets.Sum(a => (long)a.DownloadCount), run == null ? null : sums[run.Id], null));
        }
        return rows;
    }
}
