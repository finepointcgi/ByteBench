using System.Text.RegularExpressions;

namespace ByteBench.Services.Git;

/// <summary>Jira-style detection of issue keys (e.g. WEB-12) in branch names, PR titles and commit messages.</summary>
public static class IssueKeys
{
    /// <summary>Matches any issue key of <paramref name="projectKey"/>; group 1 is the issue number.</summary>
    public static Regex Any(string projectKey) =>
        new($@"(?<![A-Za-z0-9]){Regex.Escape(projectKey)}-(\d+)(?!\d)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Matches exactly the key <c>{projectKey}-{number}</c>.</summary>
    public static Regex Exact(string projectKey, int number) =>
        new($@"(?<![A-Za-z0-9]){Regex.Escape(projectKey)}-{number}(?!\d)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// Issue numbers closed by a closing keyword (<c>closes</c>, <c>fixes</c>, <c>resolves</c> and their other tenses, optionally followed by a colon)
    /// right before one or more keys of <paramref name="projectKey"/>: <c>Fixes SHOP-1</c>, <c>closes SHOP-2, SHOP-3 and SHOP-4</c>.
    /// A bare mention (<c>see SHOP-1</c>) never closes anything.
    /// </summary>
    public static SortedSet<int> ClosingNumbers(string projectKey, IEnumerable<string?> texts)
    {
        var key = Regex.Escape(projectKey);
        var rx = new Regex($@"(?<![A-Za-z0-9])(?:close[sd]?|fix(?:e[sd])?|resolve[sd]?)\s*:?\s+(?<keys>{key}-\d+(?!\d)(?:\s*(?:,|&|and)\s*{key}-\d+(?!\d))*)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var any = Any(projectKey);
        var set = new SortedSet<int>();
        foreach (var t in texts)
        {
            if (string.IsNullOrEmpty(t)) continue;
            foreach (Match m in rx.Matches(t))
                foreach (Match k in any.Matches(m.Groups["keys"].Value))
                    if (int.TryParse(k.Groups[1].Value, out var n)) set.Add(n);
        }
        return set;
    }

    /// <summary>Distinct issue numbers referenced in any of <paramref name="texts"/>.</summary>
    public static SortedSet<int> Numbers(string projectKey, IEnumerable<string?> texts)
    {
        var rx = Any(projectKey);
        var set = new SortedSet<int>();
        foreach (var t in texts)
        {
            if (string.IsNullOrEmpty(t)) continue;
            foreach (Match m in rx.Matches(t))
                if (int.TryParse(m.Groups[1].Value, out var n)) set.Add(n);
        }
        return set;
    }
}
