using ByteBench.Data;
using ByteBench.Services;
using ByteBench.Services.Git;
using ByteBench.Services.Signing;

namespace ByteBench.Api;

/// <summary>A commit's signature verification. <c>status</c>: unsigned, verified, unverified or unverifiable.</summary>
public record CommitVerificationDto(string Sha, string Status, string? Format, string? Reason, string? Signer, string? KeyFingerprint, string Description);

public static class SignedCommitEndpoints
{
    public static void MapSignedCommitEndpoints(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/projects/{key}/repos/{slug}").WithTags("Repositories");
        g.MapGet("/commits/{sha}/signature", Get).WithName("GetCommitSignature").WithSummary("A commit's signature status")
            .WithDescription("verified only when the signature is good, its key is a registered, unrevoked, unexpired key of a user, and the committer email is that user's confirmed email. " +
                             "unverifiable: signed, but this server has no gpg / ssh-keygen to check it. Results are cached per commit.");
    }

    private static async Task<CommitVerificationDto> Get(RepositoryService repos, CommitSignatureService signatures, string key, string slug, string sha)
    {
        var repo = await repos.GetRepositoryAsync(key, slug) ?? throw ApiException.NotFound($"Repository '{key.ToUpperInvariant()}/{slug}' not found.");
        var full = await repos.Git(repo, path => GitOps.Exists(path) && sha.Length >= 4 ? GitOps.ResolveSha(path, sha) : null)
                   ?? throw ApiException.NotFound($"Commit '{sha}' not found.");
        var r = await signatures.GetAsync(repo, full);
        return new CommitVerificationDto(r.Sha, r.Status.ToString().ToLowerInvariant(), r.Format, r.Reason, r.SignerName, r.KeyFingerprint, r.Tooltip);
    }
}
