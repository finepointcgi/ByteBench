using ByteBench.Data;
using Microsoft.EntityFrameworkCore;

namespace ByteBench.Services.Workspaces;

/// <summary>
/// The workspace's git credential: the <c>gbw_…</c> token a container clones and pushes with. It is checked from Git over HTTP's Basic
/// authentication (the user name is the owner's, the password is the token) and is deliberately narrower than a personal access token:
/// it only works for the one repository of its workspace, only while that workspace is starting or running, and only for its owner. Whether it may
/// push is decided twice: here (the workspace was started with PushCode) and by the git endpoint for every request (the owner's current role,
/// archived and mirror checks, branch protection). It is not accepted by the REST API, MCP or the package registry.
/// </summary>
public static class WorkspaceGitAuth
{
    /// <summary><see cref="HttpContext.Items"/> key: the only repository id the request's credential is valid for.</summary>
    public const string RepositoryKey = "bytebench.workspace-repository";

    /// <summary>The scopes of a valid token (read, plus write when the workspace may push) and the repository it is limited to; null if invalid.</summary>
    public static async Task<(TokenScopes Scopes, int RepositoryId)?> ValidateAsync(IServiceProvider services, int userId, string presented)
    {
        if (!presented.StartsWith(WorkspaceTokens.GitPrefix, StringComparison.Ordinal)) return null;
        var hash = WorkspaceTokens.Hash(presented);
        var factory = services.GetRequiredService<IDbContextFactory<AppDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        var ws = await db.Workspaces.AsNoTracking()
            .Where(w => w.OwnerId == userId && w.GitTokenHash == hash && (w.State == WorkspaceState.Starting || w.State == WorkspaceState.Running))
            .Select(w => new { w.RepositoryId, w.CanPush, w.GitTokenHash }).FirstOrDefaultAsync();
        // The lookup compared hashes in the database; compare again in constant time before trusting it.
        if (ws?.GitTokenHash == null || !System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(ws.GitTokenHash, hash)) return null;
        return (TokenScopes.ReadRepository | (ws.CanPush ? TokenScopes.WriteRepository : TokenScopes.None), ws.RepositoryId);
    }

    /// <summary>True when the request carried a workspace token for a different repository than <paramref name="repositoryId"/>.</summary>
    public static bool WrongRepository(HttpContext ctx, int repositoryId) => ctx.Items[RepositoryKey] is int allowed && allowed != repositoryId;
}
