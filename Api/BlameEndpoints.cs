using ByteBench.Services;
using ByteBench.Services.Code;
using ByteBench.Services.Git;

namespace ByteBench.Api;

/// <param name="Sha">The commit the blame was taken at (the resolved <c>rev</c>).</param>
/// <param name="Ranges">Consecutive lines of one commit: lines <c>startLine</c>..<c>endLine</c> (1-based) belong to <c>commit</c>.</param>
public record BlameDto(string Rev, string Sha, string Path, int LineCount, IReadOnlyList<BlameCommitDto> Commits, IReadOnlyList<BlameRangeDto> Ranges);

/// <param name="Path">The file's path in this commit (differs from the requested path if it was renamed since).</param>
/// <param name="ParentSha">First parent that still has the file: blame it with <c>rev=ParentSha&amp;path=ParentPath</c> to see the file before this commit. Null for the commit that added the file.</param>
public record BlameCommitDto(string Sha, string Subject, string AuthorName, string AuthorEmail, DateTimeOffset Date, string Path, string? ParentSha, string? ParentPath);

public record BlameRangeDto(int StartLine, int EndLine, string Commit);

/// <summary>GET /api/v1/projects/{key}/repos/{slug}/blame</summary>
public static class BlameEndpoints
{
    public static void MapBlameEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/projects/{key}/repos/{slug}/blame", GetBlame).WithTags("Repositories").WithName("GetBlame")
            .WithSummary("Blame a file")
            .WithDescription("Who last changed each line of ?path= at ?rev= (branch, tag or SHA; default branch if omitted). Needs the same access as viewing the file. " +
                             "Lines are grouped into ranges per commit. 404 if the ref or file doesn't exist, 413 if the file is too large to blame, 422 if it is binary.");
    }

    private static async Task<BlameDto> GetBlame(RepositoryService repos, BlameService blame, string key, string slug, string? rev, string? path)
    {
        var repo = await repos.GetRepositoryAsync(key, slug) ?? throw ApiException.NotFound($"Repository '{key.ToUpperInvariant()}/{slug}' not found.");
        if (string.IsNullOrWhiteSpace(path) || path.Split('/').Any(s => s is ".." or ".")) throw ApiException.BadRequest("path is required.");
        var data = await blame.BlameAsync(repo, rev, path);
        switch (data.Status)
        {
            case BlameStatus.NotFound: throw ApiException.NotFound($"File '{path.Trim('/')}' not found at '{(string.IsNullOrWhiteSpace(rev) ? repo.DefaultBranch : rev)}'.");
            case BlameStatus.Binary: throw ApiException.Unprocessable("Binary files can't be blamed.");
            case BlameStatus.TooLarge:
                throw new ApiException(StatusCodes.Status413PayloadTooLarge, "Payload Too Large", $"The file is too large to blame ({data.Size} bytes).");
        }
        return new BlameDto(string.IsNullOrWhiteSpace(rev) ? repo.DefaultBranch : rev, data.Sha!, data.Path, data.Lines.Length,
            data.Commits.Select(c => new BlameCommitDto(c.Sha, c.Subject, c.AuthorName, c.AuthorEmail, c.When, c.Path, c.ParentSha, c.ParentPath)).ToList(),
            data.Ranges.Select(r => new BlameRangeDto(r.StartLine, r.StartLine + r.LineCount - 1, data.Commits[r.CommitIndex].Sha)).ToList());
    }
}
