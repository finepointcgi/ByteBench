using System.Globalization;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using ByteBench.Data;
using ByteBench.Services.Accounts;
using Microsoft.EntityFrameworkCore;

namespace ByteBench.Services.Ci;

/// <summary>A badge to draw: <see cref="Label"/> on the left, <see cref="Value"/> on a <see cref="Color"/> background on the right.</summary>
public sealed record CiBadge(string Label, string Value, string Color);

/// <summary>
/// Pipeline and coverage badges (scoped). ByteBench has no anonymous public projects, so a badge is served to a signed-in user who
/// can view the repository, or to anyone holding the repository's badge token (<c>?token=</c>, 24 random bytes kept in
/// <see cref="SiteSetting"/>, created and rotated by a Maintainer). A wrong token and a missing repository look the same (404).
/// </summary>
public sealed class CiBadgeService(IDbContextFactory<AppDbContext> dbFactory, SiteSettings settings, ProjectAccess access, Audit.AuditLog audit)
{
    private static string TokenKey(int repositoryId) => $"ci:badge-token:{repositoryId}";

    /// <summary>The repository's badge token, or null if none was created yet. Needs ManageRepositories.</summary>
    public async Task<string?> GetTokenAsync(Repository repo)
    {
        if (!await access.CanAsync(repo.ProjectId, Permission.ManageRepositories)) return null;
        return await settings.GetAsync(TokenKey(repo.Id));
    }

    /// <summary>Creates the token if there is none, else (with <paramref name="rotate"/>) replaces it, which breaks every badge URL in use. Needs ManageRepositories.</summary>
    public async Task<string> EnsureTokenAsync(Repository repo, bool rotate = false)
    {
        await access.RequireAsync(repo.ProjectId, Permission.ManageRepositories);
        var existing = await settings.GetAsync(TokenKey(repo.Id));
        if (existing != null && !rotate) return existing;
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        await settings.SetAsync(TokenKey(repo.Id), token, await access.ResolveUserIdAsync());
        await audit.RecordAsync(existing == null ? "ci_badge.token_created" : "ci_badge.token_rotated", "repository", repo.Id, repo.ProjectId);
        return token;
    }

    /// <summary>Constant-time comparison with the stored token.</summary>
    public async Task<bool> TokenValidAsync(int repositoryId, string? token)
    {
        if (string.IsNullOrEmpty(token) || token.Length > 128) return false;
        var stored = await settings.GetAsync(TokenKey(repositoryId));
        return stored != null && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(stored), Encoding.UTF8.GetBytes(token));
    }

    public async Task<Repository?> FindRepositoryAsync(string projectKey, string slug)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Repositories.AsNoTracking().Include(r => r.Project)
            .FirstOrDefaultAsync(r => r.Project.Key == projectKey && r.Slug == slug);
    }

    /// <summary>
    /// Whether the request may see the repository's badges: the token matches, or the signed-in user (if any) has ViewProject.
    /// </summary>
    public async Task<bool> MayViewAsync(Repository repo, string? token, ClaimsPrincipal user)
    {
        if (await TokenValidAsync(repo.Id, token)) return true;
        if (!int.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var userId)) return false;
        access.ActAs(userId);
        return await access.CanAsync(repo.ProjectId, Permission.ViewProject);
    }

    /// <summary>The newest branch pipeline's status (not a tag or fork pull request one) and the newest coverage on <paramref name="branch"/> (default: the default branch).</summary>
    public async Task<(PipelineStatus? Status, double? Coverage)> LatestAsync(Repository repo, string? branch)
    {
        branch = string.IsNullOrWhiteSpace(branch) ? repo.DefaultBranch : branch.Trim();
        await using var db = await dbFactory.CreateDbContextAsync();
        var status = await db.Pipelines.AsNoTracking().Where(p => p.RepositoryId == repo.Id && p.Ref == branch && !p.AwaitingApproval)
            .OrderByDescending(p => p.Id).Select(p => (PipelineStatus?)p.Status).FirstOrDefaultAsync();
        var coverage = await db.Pipelines.AsNoTracking().Where(p => p.RepositoryId == repo.Id && p.Ref == branch && p.Coverage != null)
            .OrderByDescending(p => p.Id).Select(p => p.Coverage).FirstOrDefaultAsync();
        return (status, coverage);
    }

    /// <summary>The coverage of the newest pipelines on a branch, oldest first (for the trend).</summary>
    public async Task<List<(int PipelineId, double Coverage)>> TrendAsync(Repository repo, string branch, int take = 30)
    {
        if (!await access.CanAsync(repo.ProjectId, Permission.ViewProject)) return [];
        await using var db = await dbFactory.CreateDbContextAsync();
        var rows = await db.Pipelines.AsNoTracking().Where(p => p.RepositoryId == repo.Id && p.Ref == branch && p.Coverage != null)
            .OrderByDescending(p => p.Id).Take(take).Select(p => new { p.Id, Coverage = p.Coverage!.Value }).ToListAsync();
        rows.Reverse();
        return rows.Select(r => (r.Id, r.Coverage)).ToList();
    }

    public static CiBadge PipelineBadge(PipelineStatus? status) => status switch
    {
        PipelineStatus.Passed => new("pipeline", "passed", "#4c1"),
        PipelineStatus.Failed => new("pipeline", "failed", "#e05d44"),
        PipelineStatus.Error => new("pipeline", "error", "#e05d44"),
        PipelineStatus.Running => new("pipeline", "running", "#007ec6"),
        PipelineStatus.Queued => new("pipeline", "pending", "#9f9f9f"),
        PipelineStatus.Manual => new("pipeline", "blocked", "#dfb317"),
        PipelineStatus.Canceled => new("pipeline", "canceled", "#9f9f9f"),
        _ => new("pipeline", "unknown", "#9f9f9f"),
    };

    public static CiBadge CoverageBadge(double? coverage) => coverage switch
    {
        null => new("coverage", "unknown", "#9f9f9f"),
        >= 90 => new("coverage", Percent(coverage.Value), "#4c1"),
        >= 75 => new("coverage", Percent(coverage.Value), "#a3c51c"),
        >= 60 => new("coverage", Percent(coverage.Value), "#dfb317"),
        >= 40 => new("coverage", Percent(coverage.Value), "#fe7d37"),
        _ => new("coverage", Percent(coverage.Value), "#e05d44"),
    };

    private static string Percent(double v) => v.ToString("0.##", CultureInfo.InvariantCulture) + "%";

    /// <summary>A flat badge as SVG. Text is XML-escaped; the colour is one of ours.</summary>
    public static string Svg(CiBadge badge)
    {
        static int Width(string s) => (int)Math.Ceiling(s.Length * 6.6) + 12;
        var lw = Width(badge.Label);
        var rw = Width(badge.Value);
        var w = lw + rw;
        var label = WebUtility.HtmlEncode(badge.Label);
        var value = WebUtility.HtmlEncode(badge.Value);
        var title = $"{label}: {value}";
        var lx = (lw / 2.0).ToString("0.#", CultureInfo.InvariantCulture);
        var rx = (lw + rw / 2.0).ToString("0.#", CultureInfo.InvariantCulture);
        return $"""
            <svg xmlns="http://www.w3.org/2000/svg" width="{w}" height="20" role="img" aria-label="{title}"><title>{title}</title>
            <linearGradient id="s" x2="0" y2="100%"><stop offset="0" stop-color="#bbb" stop-opacity=".1"/><stop offset="1" stop-opacity=".1"/></linearGradient>
            <clipPath id="r"><rect width="{w}" height="20" rx="3" fill="#fff"/></clipPath>
            <g clip-path="url(#r)"><rect width="{lw}" height="20" fill="#555"/><rect x="{lw}" width="{rw}" height="20" fill="{badge.Color}"/><rect width="{w}" height="20" fill="url(#s)"/></g>
            <g fill="#fff" text-anchor="middle" font-family="Verdana,Geneva,DejaVu Sans,sans-serif" font-size="11">
            <text x="{lx}" y="15" fill="#010101" fill-opacity=".3">{label}</text><text x="{lx}" y="14">{label}</text>
            <text x="{rx}" y="15" fill="#010101" fill-opacity=".3">{value}</text><text x="{rx}" y="14">{value}</text></g></svg>
            """;
    }
}
