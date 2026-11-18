namespace GitBench.Services.Git;

/// <summary>Pull request description templates, read from the target branch of a new PR.</summary>
public static class PullRequestTemplates
{
    /// <summary>Paths tried in order; the first text file found wins.</summary>
    public static readonly string[] Paths =
    [
        ".gitbench/pull_request_template.md",
        "PULL_REQUEST_TEMPLATE.md",
        ".github/pull_request_template.md",
        ".github/PULL_REQUEST_TEMPLATE.md",
    ];

    /// <summary>The template (and where it was found) on <paramref name="branch"/> of the bare repo at <paramref name="repoPath"/>, or null.</summary>
    public static (string Path, string Text)? Find(string repoPath, string branch)
    {
        foreach (var p in Paths)
        {
            if (GitOps.GetBlob(repoPath, branch, p) is { IsBinary: false, TooLarge: false, Text: { } text } && !string.IsNullOrWhiteSpace(text))
                return (p, text.TrimEnd() + "\n");
        }
        return null;
    }
}
