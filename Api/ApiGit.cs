using LibGit2Sharp;

namespace ByteBench.Api;

/// <summary>Read-only git lookups the API needs beyond <see cref="Services.Git.GitOps"/> (raw bytes, commit counts).</summary>
public static class ApiGit
{
    public const long MaxRawBytes = 20 * 1024 * 1024;

    public sealed record RawFile(string Sha, long Size, bool IsBinary, byte[]? Content);

    /// <summary>The file at <paramref name="filePath"/> in <paramref name="rev"/>; null if the rev or file doesn't exist (or is a directory). Content is null when larger than <see cref="MaxRawBytes"/>.</summary>
    public static RawFile? GetRaw(string path, string rev, string filePath)
    {
        using var r = new LibGit2Sharp.Repository(path);
        if (Resolve(r, rev) is not { } commit || commit[filePath.Trim('/')]?.Target is not Blob blob) return null;
        if (blob.Size > MaxRawBytes) return new RawFile(blob.Sha, blob.Size, blob.IsBinary, null);
        using var stream = blob.GetContentStream();
        using var ms = new MemoryStream((int)blob.Size);
        stream.CopyTo(ms);
        return new RawFile(blob.Sha, blob.Size, blob.IsBinary, ms.ToArray());
    }

    /// <summary>Number of commits reachable from <paramref name="rev"/> (optionally touching <paramref name="filePath"/>), capped at <paramref name="cap"/>; null if the rev doesn't resolve.</summary>
    public static int? CountCommits(string path, string rev, string? filePath, int cap = 100_000)
    {
        using var r = new LibGit2Sharp.Repository(path);
        if (Resolve(r, rev) is not { } tip) return null;
        var filter = new CommitFilter { IncludeReachableFrom = tip };
        return string.IsNullOrEmpty(filePath)
            ? r.Commits.QueryBy(filter).Take(cap).Count()
            : r.Commits.QueryBy(filePath.Trim('/'), filter).Take(cap).Count();
    }

    private static Commit? Resolve(LibGit2Sharp.Repository r, string rev)
    {
        if (r.Branches[rev]?.Tip is { } branchTip) return branchTip;
        if (r.Tags[rev]?.PeeledTarget is Commit tagged) return tagged;
        try { return r.Lookup<Commit>(rev); }
        catch (LibGit2SharpException) { return null; }
        catch (ArgumentException) { return null; }
    }
}
