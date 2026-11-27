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
    /// <summary><see cref="HttpContext.Items"/> key: submodule repository ids the credential may read (never write).</summary>
    public const string ReadOnlyRepositoriesKey = "bytebench.workspace-readonly-repositories";

    /// <summary>The scopes of a valid token (read, plus write when the workspace may push) and the repository it is limited to; null if invalid.</summary>
    public static async Task<(TokenScopes Scopes, int RepositoryId, IReadOnlyList<int> ReadOnlyRepositoryIds)?> ValidateAsync(IServiceProvider services, int userId, string presented)
    {
        if (!presented.StartsWith(WorkspaceTokens.GitPrefix, StringComparison.Ordinal)) return null;
        var hash = WorkspaceTokens.Hash(presented);
        var factory = services.GetRequiredService<IDbContextFactory<AppDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        var ws = await db.Workspaces.AsNoTracking()
            .Where(w => w.OwnerId == userId && w.GitTokenHash == hash && (w.State == WorkspaceState.Starting || w.State == WorkspaceState.Running))
            .Select(w => new { w.Id, w.RepositoryId, w.CanPush, w.GitTokenHash, w.SubmoduleRepoIdsJson }).FirstOrDefaultAsync();
        // The lookup compared hashes in the database; compare again in constant time before trusting it.
        if (ws?.GitTokenHash != null && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(ws.GitTokenHash, hash))
        {
            IReadOnlyList<int> extra = [];
            try { extra = System.Text.Json.JsonSerializer.Deserialize<List<int>>(ws.SubmoduleRepoIdsJson) ?? []; } catch (System.Text.Json.JsonException) { }
            // Collaborators share the container's credentials: unless the owner allowed them to push, the token stays read-only while they are invited.
            var paused = ws.CanPush && await db.WorkspaceShares.AnyAsync(s => s.WorkspaceId == ws.Id && s.EndedAt == null && s.Role == WorkspaceShareRole.Collaborator && !s.AllowPush);
            return (TokenScopes.ReadRepository | (ws.CanPush && !paused ? TokenScopes.WriteRepository : TokenScopes.None), ws.RepositoryId, extra);
        }

        // A prebuild's token: read-only, one repository (its submodule repositories are added by the build), only while that build runs.
        var build = await db.WorkspacePrebuilds.AsNoTracking()
            .Where(p => p.TriggeredById == userId && p.GitTokenHash == hash && p.State == PrebuildState.Building)
            .Select(p => new { p.RepositoryId, p.GitTokenHash, p.SubmoduleRepoIdsJson }).FirstOrDefaultAsync();
        if (build?.GitTokenHash != null && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(build.GitTokenHash, hash))
        {
            IReadOnlyList<int> extra = [];
            try { extra = System.Text.Json.JsonSerializer.Deserialize<List<int>>(build.SubmoduleRepoIdsJson) ?? []; } catch (System.Text.Json.JsonException) { }
            return (TokenScopes.ReadRepository, build.RepositoryId, extra);
        }
        return null;
    }

    /// <summary>
    /// True when the request carried a workspace token that isn't valid for <paramref name="repositoryId"/>: any repository other than its own,
    /// except that a submodule repository may be <i>read</i> (<paramref name="writing"/> false).
    /// </summary>
    public static bool WrongRepository(HttpContext ctx, int repositoryId, bool writing = true)
    {
        if (ctx.Items[RepositoryKey] is not int allowed) return false;
        if (allowed == repositoryId) return false;
        return !(!writing && ctx.Items[ReadOnlyRepositoriesKey] is IReadOnlyList<int> readable && readable.Contains(repositoryId));
    }
}
