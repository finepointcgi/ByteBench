namespace GitBench.Services;

/// <summary>
/// Where bare Git repositories live on disk: {Root}/{PROJECTKEY}/{slug}.git
/// Root comes from config "Git:RepositoryRoot", defaulting to {ContentRoot}/repos.
/// </summary>
public class RepoStorage(IConfiguration config, IWebHostEnvironment env, ILogger<RepoStorage>? log = null)
{
    public string Root { get; } = Path.GetFullPath(config["Git:RepositoryRoot"] ?? Path.Combine(env.ContentRootPath, "repos"));

    public string PathFor(string projectKey, string slug) =>
        Path.Combine(Root, projectKey.ToUpperInvariant(), slug.ToLowerInvariant() + ".git");

    /// <summary>
    /// Removes {Root}/{KEY} with every bare repo in it, after the project's rows were deleted (a deleted project must not
    /// leave its code on disk, nor block a new project with the same key from creating a repo with the same slug).
    /// The directory is first renamed to a <c>.trash-…</c> name, so a half-finished delete never looks like a project.
    /// Failures are logged, never thrown: the database delete has already happened.
    /// </summary>
    public void DeleteProjectDirectory(string projectKey)
    {
        var root = Path.TrimEndingDirectorySeparator(Root) + Path.DirectorySeparatorChar;
        var dir = Path.GetFullPath(Path.Combine(Root, projectKey.ToUpperInvariant()));
        if (projectKey.Length == 0 || !dir.StartsWith(root, StringComparison.Ordinal) || dir.Length <= root.Length || !Directory.Exists(dir)) return;
        var trash = Path.Combine(Root, $".trash-{projectKey.ToUpperInvariant()}-{Guid.NewGuid():N}");
        try
        {
            Directory.Move(dir, trash);
            foreach (var f in Directory.EnumerateFiles(trash, "*", SearchOption.AllDirectories))
                File.SetAttributes(f, FileAttributes.Normal); // git marks pack files read-only
            Directory.Delete(trash, recursive: true);
            log?.LogInformation("Deleted the repositories of project {Key}", projectKey);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log?.LogError(ex, "Project {Key} was deleted but its repository directory could not be removed ({Dir} / {Trash})", projectKey, dir, trash);
        }
    }
}
