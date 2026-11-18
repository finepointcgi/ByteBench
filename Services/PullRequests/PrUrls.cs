using GitBench.Data;
using GitBench.Services.Git;

namespace GitBench.Services;

/// <summary>Routes for the pull request feature pages (compare, merge settings).</summary>
public static class PrUrls
{
    /// <summary>/p/KEY/repos/slug/compare/base...head (each ref escaped, '/' kept; head may be left out). Without a base: the picker on the default branch.</summary>
    public static string Compare(Repository r, string? @base = null, string? head = null) =>
        @base == null ? RepoUrls.Base(r) + "/compare" : $"{RepoUrls.Base(r)}/compare/{Escape(@base)}...{(head == null ? "" : Escape(head))}";

    public static string MergeSettings(Repository r) => RepoUrls.Base(r) + "/settings/merge";

    private static string Escape(string refName) => string.Join('/', refName.Split('/').Select(Uri.EscapeDataString));

    /// <summary>Splits "base...head" (or "base..head"; head may be empty); null when it isn't one.</summary>
    public static (string Base, string Head)? ParseSpec(string? spec)
    {
        if (string.IsNullOrEmpty(spec)) return null;
        spec = Uri.UnescapeDataString(spec);
        var i = spec.IndexOf("...", StringComparison.Ordinal);
        var len = 3;
        if (i < 0) { i = spec.IndexOf("..", StringComparison.Ordinal); len = 2; }
        if (i <= 0) return null;
        return (spec[..i], spec[(i + len)..]);
    }
}
