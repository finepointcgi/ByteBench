using ByteBench.Data;
using ByteBench.Services.Git;

namespace ByteBench.Services.Code;

/// <summary>Route builders for code search and repository settings (see <see cref="RepoUrls"/> for the rest).</summary>
public static class CodeUrls
{
    public static string Search(Repository r, string? q = null, string? @ref = null, bool caseSensitive = false, bool regex = false) =>
        RepoUrls.Base(r) + "/search" + Query(("q", q), ("ref", string.IsNullOrEmpty(@ref) || @ref == r.DefaultBranch ? null : @ref),
            ("case", caseSensitive ? "1" : null), ("regex", regex ? "1" : null));

    public static string ProjectSearch(string projectKey, string? q = null, bool caseSensitive = false, bool regex = false) =>
        $"/p/{projectKey}/code-search" + Query(("q", q), ("case", caseSensitive ? "1" : null), ("regex", regex ? "1" : null));

    /// <summary>The blame view of a file at <paramref name="rev"/> (a branch, tag or sha; escaped as one segment because branch names contain '/').</summary>
    public static string Blame(Repository r, string rev, string path) =>
        $"{RepoUrls.Base(r)}/blame/{Uri.EscapeDataString(rev)}/" + string.Join("/", path.Trim('/').Split('/').Select(Uri.EscapeDataString));

    public static string Settings(Repository r) => RepoUrls.Base(r) + "/settings";

    /// <summary>A file at a ref, scrolled to <paramref name="line"/>.</summary>
    public static string Line(Repository r, string? @ref, string path, int line) => RepoUrls.Source(r, @ref, path) + "#L" + line;

    private static string Query(params (string Key, string? Value)[] parts)
    {
        var q = string.Join("&", parts.Where(p => !string.IsNullOrEmpty(p.Value))
            .Select(p => p.Key + "=" + Uri.EscapeDataString(p.Value!)));
        return q.Length == 0 ? "" : "?" + q;
    }
}
