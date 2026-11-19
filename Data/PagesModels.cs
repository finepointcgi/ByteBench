using System.ComponentModel.DataAnnotations;

namespace GitBench.Data;

/// <summary>Who may open a repository's site. <see cref="Members"/> means everyone who can view the project (see ProjectAccess).</summary>
public enum PagesVisibility { Members, Public }

/// <summary>What the site answers for a path that has no file.</summary>
public enum PagesNotFoundMode
{
    /// <summary>The site's own <c>404.html</c> when it has one, else GitBench's plain page (status 404).</summary>
    Site404,
    /// <summary>Always GitBench's plain page, even if the site ships a <c>404.html</c>.</summary>
    Plain,
    /// <summary>Single-page apps: paths without a file extension get <c>index.html</c> with status 200; others 404.</summary>
    SpaFallback,
}

/// <summary>
/// A repository's static site (GitLab-Pages style): settings only; the content lives in <see cref="PagesDeployment"/>s.
/// Created by the first deployment of a CI job that publishes pages. One per repository.
/// </summary>
public class PagesSite
{
    public int Id { get; set; }
    public int RepositoryId { get; set; }
    public Repository Repository { get; set; } = null!;
    /// <summary>When false the site (and its review sites) answer 404 and CI jobs don't deploy.</summary>
    public bool Enabled { get; set; } = true;
    public PagesVisibility Visibility { get; set; } = PagesVisibility.Members;
    public PagesNotFoundMode NotFound { get; set; } = PagesNotFoundMode.Site404;
    /// <summary>Deploy branches other than the default branch as review sites (only for jobs that opt in with <c>review: true</c>).</summary>
    public bool ReviewSites { get; set; } = true;
    /// <summary>A review site is removed this many days after its last deployment.</summary>
    public int ReviewExpireDays { get; set; } = 7;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public List<PagesDeployment> Deployments { get; set; } = [];
}

/// <summary>
/// One deployed version of a site: a directory of files under <c>Pages:Root</c>. Per <see cref="Slot"/> (empty for the main
/// site, else the review site's name) exactly one deployment has <see cref="IsCurrent"/>; switching it is one database update
/// after the new directory is complete, which is the atomic switch.
/// </summary>
public class PagesDeployment
{
    public int Id { get; set; }
    public int SiteId { get; set; }
    public PagesSite Site { get; set; } = null!;
    /// <summary>"" for the main site; for a review site the slug of its branch (see PagesSlots).</summary>
    [MaxLength(64)] public string Slot { get; set; } = "";
    /// <summary>1, 2, 3... per site and slot.</summary>
    public int Version { get; set; }
    [Required, MaxLength(250)] public string Ref { get; set; } = "";
    [MaxLength(40)] public string CommitSha { get; set; } = "";
    public int? PipelineId { get; set; }
    public int? JobId { get; set; }
    public int? DeployedById { get; set; }
    public User? DeployedBy { get; set; }
    /// <summary>Folder name (32 hex digits) under <c>Pages:Root/{repositoryId}</c>.</summary>
    [Required, MaxLength(32)] public string Directory { get; set; } = "";
    public int FileCount { get; set; }
    public long SizeBytes { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    /// <summary>Review sites: when this deployment (and the slot) is removed. Null for the main site.</summary>
    public DateTime? ExpiresAt { get; set; }
    public bool IsCurrent { get; set; }
    /// <summary>Became current again through "Roll back" rather than a pipeline.</summary>
    public bool IsRollback { get; set; }
    /// <summary>When it was last made current.</summary>
    public DateTime? ActivatedAt { get; set; }
}
