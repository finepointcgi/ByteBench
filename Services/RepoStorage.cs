namespace GitBench.Services;

/// <summary>
/// Where bare Git repositories live on disk: {Root}/{PROJECTKEY}/{slug}.git
/// Root comes from config "Git:RepositoryRoot", defaulting to {ContentRoot}/repos.
/// </summary>
public class RepoStorage(IConfiguration config, IWebHostEnvironment env)
{
    public string Root { get; } = Path.GetFullPath(config["Git:RepositoryRoot"] ?? Path.Combine(env.ContentRootPath, "repos"));

    public string PathFor(string projectKey, string slug) =>
        Path.Combine(Root, projectKey.ToUpperInvariant(), slug.ToLowerInvariant() + ".git");
}
