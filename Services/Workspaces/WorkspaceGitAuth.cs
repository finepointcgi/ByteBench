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
            // Collaborators run as their own OS user and can't read this token (it lives in the owner's mode-700 home), so it needs no pausing while they are invited.
            return (TokenScopes.ReadRepository | (ws.CanPush ? TokenScopes.WriteRepository : TokenScopes.None), ws.RepositoryId, extra);
        }

        // A collaborator's own token (only exists when the owner ticked "may push"; kept in memory with their session, dies with the share, the workspace or
        // the permission): this repository only, and the user it was issued to. The git endpoint still applies that user's own role and branch protection.
        if (services.GetRequiredService<WorkspaceRuntime>().Access.FindByGitToken(userId, hash) is { } collaborator)
        {
            var shared = await db.WorkspaceShares.AsNoTracking()
                .Where(s => s.WorkspaceId == collaborator.WorkspaceId && s.UserId == userId && s.EndedAt == null && s.Role == WorkspaceShareRole.Collaborator && s.AllowPush
                            && (s.Workspace.State == WorkspaceState.Starting || s.Workspace.State == WorkspaceState.Running))
                .Select(s => new { s.Workspace.RepositoryId, s.Workspace.CanPush, s.Workspace.SubmoduleRepoIdsJson }).FirstOrDefaultAsync();
            if (shared != null)
            {
                IReadOnlyList<int> extra = [];
                try { extra = System.Text.Json.JsonSerializer.Deserialize<List<int>>(shared.SubmoduleRepoIdsJson) ?? []; } catch (System.Text.Json.JsonException) { }
                return (TokenScopes.ReadRepository | (shared.CanPush ? TokenScopes.WriteRepository : TokenScopes.None), shared.RepositoryId, extra);
            }
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
