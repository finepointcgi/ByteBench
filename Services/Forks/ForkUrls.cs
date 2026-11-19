using ByteBench.Data;
using ByteBench.Services.Git;

namespace ByteBench.Services.Forks;

/// <summary>Routes for the fork pages.</summary>
public static class ForkUrls
{
    public static string List(Repository r) => RepoUrls.Base(r) + "/forks";

    /// <summary>New cross-fork PR from <paramref name="source"/> (a fork); <c>into</c> is "KEY/slug" of the target.</summary>
    public static string NewPullRequest(Repository source, string? into = null, string? branch = null, string? target = null)
    {
        var q = new List<string>();
        if (into != null) q.Add("into=" + Uri.EscapeDataString(into));
        if (branch != null) q.Add("source=" + Uri.EscapeDataString(branch).Replace("%2F", "/"));
        if (target != null) q.Add("target=" + Uri.EscapeDataString(target).Replace("%2F", "/"));
        return RepoUrls.Base(source) + "/fork-pull-request" + (q.Count == 0 ? "" : "?" + string.Join("&", q));
    }
}
