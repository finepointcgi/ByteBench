using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace GitBench.Services.Pages;

/// <summary>Configuration section <c>Pages</c>.</summary>
public class PagesOptions
{
    /// <summary>Master switch: when false nothing is deployed or served.</summary>
    public bool Enabled { get; set; } = true;
    /// <summary>Where deployed sites are stored: {Root}/{repositoryId}/{32 hex}/. Default: {ContentRoot}/pages.</summary>
    public string? Root { get; set; }
    /// <summary>
    /// Serve sites on their own hosts: <c>{slug}.{projectKey}.{BaseDomain}</c> and review sites on
    /// <c>{branch}.{slug}.{projectKey}.{BaseDomain}</c> (for example <c>pages.example.com</c>, with a wildcard DNS record and
    /// certificate for <c>*.*.pages.example.com</c>, or <c>pages.localhost</c> in development). Empty: path mode only.
    /// </summary>
    public string? BaseDomain { get; set; }
    /// <summary>
    /// Keep serving <c>/pages/{KEY}/{slug}/...</c> on the GitBench host. Those responses are sandboxed (see PagesMiddleware).
    /// Set to false together with <see cref="BaseDomain"/> to serve sites only from their own hosts.
    /// </summary>
    public bool PathModeEnabled { get; set; } = true;
    /// <summary>Largest site (uncompressed bytes). Default 100 MB.</summary>
    public long MaxSiteBytes { get; set; } = 100L * 1024 * 1024;
    /// <summary>Largest single file. Default 50 MB.</summary>
    public long MaxFileBytes { get; set; } = 50L * 1024 * 1024;
    /// <summary>Most files in a site. Default 10000.</summary>
    public int MaxFiles { get; set; } = 10_000;
    /// <summary>Deployments kept per site (main site); older ones are deleted. Review sites keep the last 2. At least 1.</summary>
    public int KeepVersions { get; set; } = 10;
    /// <summary>Allow review sites (deployments of branches other than the default branch).</summary>
    public bool ReviewSites { get; set; } = true;
    /// <summary>Allow a repository's site to be public (no sign-in). When false every site needs a project member.</summary>
    public bool AllowPublic { get; set; } = true;
    /// <summary>How long a members-only path-mode link (the <c>/-/t/{token}/</c> part) stays valid.</summary>
    public int PathTokenMinutes { get; set; } = 60;
    /// <summary>How long the members-only cookie of a dedicated host lasts.</summary>
    public int HostCookieHours { get; set; } = 8;

    public PagesLimits Limits => new(Math.Max(1024, MaxSiteBytes), Math.Max(1, MaxFiles), Math.Max(1024, MaxFileBytes));
    public string? Domain => string.IsNullOrWhiteSpace(BaseDomain) ? null : BaseDomain.Trim().Trim('.').ToLowerInvariant();
}

/// <summary>Folders of <see cref="PagesOptions"/> (singleton).</summary>
public sealed class PagesStorage(IOptionsMonitor<PagesOptions> options, IWebHostEnvironment env)
{
    public string Root => Path.GetFullPath(options.CurrentValue.Root is { Length: > 0 } r ? r : Path.Combine(env.ContentRootPath, "pages"));

    public string RepositoryDirectory(int repositoryId) => Path.Combine(Root, repositoryId.ToString());

    /// <summary>The folder of a deployment. <paramref name="directory"/> must be 32 lowercase hex digits.</summary>
    public string DeploymentPath(int repositoryId, string directory)
    {
        if (!IsValidDirectory(directory)) throw new InvalidOperationException("Invalid deployment folder.");
        return Path.Combine(RepositoryDirectory(repositoryId), directory);
    }

    public static string NewDirectoryName() => Guid.NewGuid().ToString("N");

    public static bool IsValidDirectory(string d) => d.Length == 32 && d.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
}

/// <summary>Names of review sites and the host name syntax of dedicated hosts.</summary>
public static partial class PagesSlots
{
    public const int MaxLength = 47;

    [GeneratedRegex("^[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?$")]
    private static partial Regex LabelPattern();

    /// <summary>A DNS-label-safe name for a branch: lowercase letters, digits and '-'; changed or long names get a short hash so two branches never collide.</summary>
    public static string ForRef(string branch)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var c in branch.ToLowerInvariant())
        {
            if (c is >= 'a' and <= 'z' or >= '0' and <= '9') sb.Append(c);
            else if (sb.Length > 0 && sb[^1] != '-') sb.Append('-');
        }
        var clean = sb.ToString().Trim('-');
        if (clean.Length == 0) clean = "branch";
        if (clean == branch && clean.Length <= 40) return clean;
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(branch)))[..6].ToLowerInvariant();
        if (clean.Length > 40) clean = clean[..40].TrimEnd('-');
        return clean + "-" + hash;
    }

    /// <summary>True if <paramref name="slot"/> is a name <see cref="ForRef"/> can produce (used to validate URLs).</summary>
    public static bool IsValid(string slot) => slot.Length <= MaxLength && LabelPattern().IsMatch(slot);

    /// <summary>True if <paramref name="label"/> can be one label of a host name.</summary>
    public static bool IsHostLabel(string label) => LabelPattern().IsMatch(label);
}
