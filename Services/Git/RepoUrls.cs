using GitBench.Data;

namespace GitBench.Services.Git;

/// <summary>
/// Route builders for the code-hosting pages. Refs and paths travel as <c>?ref=</c> / <c>?path=</c> query
/// parameters (branch names contain '/', so they can't be route segments).
/// </summary>
public static class RepoUrls
{
    public static string List(string projectKey) => $"/p/{projectKey}/repos";
    public static string Base(Repository r) => $"/p/{r.Project.Key}/repos/{r.Slug}";

    public static string Source(Repository r, string? @ref = null, string? path = null) =>
        Base(r) + Query(("ref", NullIfDefault(r, @ref)), ("path", Trim(path)));

    public static string Commits(Repository r, string? @ref = null, string? path = null, int? page = null) =>
        Base(r) + "/commits" + Query(("ref", NullIfDefault(r, @ref)), ("path", Trim(path)), ("page", page > 1 ? page.ToString() : null));

    public static string Commit(Repository r, string sha) => $"{Base(r)}/commits/{sha}";
    public static string Branches(Repository r) => Base(r) + "/branches";
    public static string BranchProtection(Repository r) => Base(r) + "/settings/branches";

    public static string PullRequests(string projectKey, string? state = null, string? repo = null) =>
        $"/p/{projectKey}/pull-requests" + Query(("state", state), ("repo", repo));

    public static string RepoPullRequests(Repository r, string? state = null) => Base(r) + "/pull-requests" + Query(("state", state));
    public static string PullRequest(Repository r, int number) => $"{Base(r)}/pull-requests/{number}";
    public static string PullRequest(PullRequest pr) => PullRequest(pr.Repository, pr.Number);

    public static string NewPullRequest(string projectKey, string? repo = null, string? source = null, string? target = null) =>
        $"/p/{projectKey}/pull-requests/new" + Query(("repo", repo), ("source", source), ("target", target));

    public static string CloneUrl(string baseUri, Repository r) =>
        $"{baseUri.TrimEnd('/')}/git/{r.Project.Key}/{r.Slug}.git";

    private static string? NullIfDefault(Repository r, string? @ref) =>
        string.IsNullOrEmpty(@ref) || @ref == r.DefaultBranch ? null : @ref;

    private static string? Trim(string? path) => string.IsNullOrEmpty(path?.Trim('/')) ? null : path.Trim('/');

    private static string Query(params (string Key, string? Value)[] parts)
    {
        var q = string.Join("&", parts.Where(p => !string.IsNullOrEmpty(p.Value))
            .Select(p => p.Key + "=" + Uri.EscapeDataString(p.Value!).Replace("%2F", "/")));
        return q.Length == 0 ? "" : "?" + q;
    }
}
