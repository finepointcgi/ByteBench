using System.Text;
using GitBench.Data;
using GitBench.Services.Ci;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace GitBench.Services.Packages;

/// <summary>What a registry request wants to do in a project.</summary>
public enum RegistryOp { Read, Write, Delete }

/// <summary>Who a registry request is: a user (with a personal access token's scopes, or a browser session) or a CI job.</summary>
public sealed record RegistryIdentity(User? User, TokenScopes? Scopes, CiJobIdentity? Job)
{
    /// <summary>The user recorded as publisher/actor: the user, or whoever triggered the job's pipeline (null for schedules).</summary>
    public int? ActorId => User?.Id ?? Job?.TriggeredById;
    public string Name => User?.UserName ?? $"{CiJobTokens.UserName} (job {Job?.JobId})";
}

public enum RegistryDecision { Allowed, Unauthenticated, NotFound, Forbidden }

/// <summary>The outcome of <see cref="RegistryAuth.AuthorizeAsync"/>: the project when allowed, else why not.</summary>
public sealed record RegistryCheck(RegistryDecision Decision, string Message, Project? Project = null)
{
    public bool Allowed => Decision == RegistryDecision.Allowed;
}

/// <summary>
/// Authentication and authorization for the registry protocols (NuGet, npm, OCI). Accepted credentials:
/// <list type="bullet">
/// <item>Basic with any user name and a personal access token (<c>gbp_…</c>) as the password, or a CI job token
/// (<c>gbj_…</c>) with any user name. Account passwords are refused: use a token.</item>
/// <item><c>Authorization: Bearer</c> with a personal access token, a job token, or a registry token from
/// <c>GET /v2/token</c> (<c>gbr_…</c>, one hour).</item>
/// <item>NuGet's <c>X-NuGet-ApiKey</c> header with a personal access token or job token.</item>
/// <item>The browser session cookie, for downloads from the Packages page.</item>
/// </list>
/// Permissions come from <see cref="ProjectAccess"/>: reading needs ViewProject, publishing PublishPackages (Developer),
/// deleting DeletePackages (Maintainer). A personal access token needs a write scope (write_repository or write_api) to
/// publish or delete. A job token counts as a Developer in its own project and can read other projects the pipeline's
/// user can view. Projects a user can't see, and projects whose registry is off, are 404.
/// </summary>
public sealed class RegistryAuth(IServiceProvider services, IDbContextFactory<AppDbContext> dbFactory, ProjectAccess access,
    AccessTokenService tokens, CiJobTokens jobTokens, UserManager<User> users, IDataProtectionProvider dataProtection, ILogger<RegistryAuth> log)
{
    public const string BearerPrefix = "gbr_";
    public static readonly TimeSpan BearerLifetime = TimeSpan.FromHours(1);
    public const string PasswordRefused = "Use a personal access token (Profile → Access tokens) or a CI job token as the password.";

    private ITimeLimitedDataProtector Protector => dataProtection.CreateProtector("GitBench.Packages.Bearer.v1").ToTimeLimitedDataProtector();

    /// <summary>The request's identity, null when it has no (valid) credentials; <c>Error</c> explains a refusal.</summary>
    public async Task<(RegistryIdentity? Identity, string? Error)> AuthenticateAsync(HttpContext ctx, bool allowCookie = false)
    {
        var header = ctx.Request.Headers.Authorization.ToString();
        if (header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            string decoded;
            try { decoded = Encoding.UTF8.GetString(Convert.FromBase64String(header[6..].Trim())); }
            catch (FormatException) { return (null, "Malformed Basic credentials."); }
            var sep = decoded.IndexOf(':');
            if (sep < 0) return (null, "Malformed Basic credentials.");
            var login = decoded[..sep];
            var secret = decoded[(sep + 1)..];
            if (secret.StartsWith(CiJobTokens.Prefix, StringComparison.Ordinal)) return await FromJobTokenAsync(secret, ctx.RequestAborted);
            if (!secret.StartsWith(AccessTokenService.TokenPrefix, StringComparison.Ordinal)) return (null, PasswordRefused);
            var user = await users.FindByNameAsync(login);
            if (user == null && login.Contains('@')) user = await users.FindByEmailAsync(login);
            // npm and NuGet clients often send a placeholder user name: fall back to the token's owner.
            var token = user != null ? await tokens.ValidateAsync(user.Id, secret) : null;
            token ??= await tokens.FindActiveAsync(secret);
            if (token == null) return (null, "Invalid or expired access token.");
            return await FromUserAsync(token.UserId, token.Scopes);
        }
        if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return await FromSecretAsync(header[7..].Trim(), ctx.RequestAborted);
        var apiKey = ctx.Request.Headers["X-NuGet-ApiKey"].ToString();
        if (apiKey.Length > 0) return await FromSecretAsync(apiKey.Trim(), ctx.RequestAborted);
        if (allowCookie && ctx.User.Identity?.IsAuthenticated == true && await users.GetUserAsync(ctx.User) is { DeactivatedAt: null } u
            && !await users.IsLockedOutAsync(u))
            return (new RegistryIdentity(u, null, null), null);
        return (null, null);
    }

    private async Task<(RegistryIdentity?, string?)> FromSecretAsync(string secret, CancellationToken ct)
    {
        if (secret.StartsWith(CiJobTokens.Prefix, StringComparison.Ordinal)) return await FromJobTokenAsync(secret, ct);
        if (secret.StartsWith(BearerPrefix, StringComparison.Ordinal))
        {
            try
            {
                var parts = Protector.Unprotect(secret[BearerPrefix.Length..]).Split(':');
                if (parts.Length == 2 && int.TryParse(parts[0], out var uid) && int.TryParse(parts[1], out var scopes))
                    return await FromUserAsync(uid, scopes < 0 ? null : (TokenScopes)scopes);
            }
            catch (System.Security.Cryptography.CryptographicException) { /* expired or forged */ }
            return (null, "Invalid or expired registry token.");
        }
        if (secret.StartsWith(AccessTokenService.TokenPrefix, StringComparison.Ordinal) && await tokens.FindActiveAsync(secret) is { } token)
            return await FromUserAsync(token.UserId, token.Scopes);
        return (null, "Invalid or expired access token.");
    }

    private async Task<(RegistryIdentity?, string?)> FromJobTokenAsync(string secret, CancellationToken ct) =>
        await jobTokens.ValidateAsync(secret, ct) is { } job
            ? (new RegistryIdentity(null, null, job), null)
            : (null, "Invalid job token, or the job has finished.");

    private async Task<(RegistryIdentity?, string?)> FromUserAsync(int userId, TokenScopes? scopes)
    {
        var user = await users.FindByIdAsync(userId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (user == null || user.DeactivatedAt != null || await users.IsLockedOutAsync(user)) return (null, "This account is locked or deactivated.");
        return (new RegistryIdentity(user, scopes, null), null);
    }

    /// <summary>A short-lived bearer token for <paramref name="id"/> (the answer of <c>GET /v2/token</c>).</summary>
    public string IssueBearer(RegistryIdentity id, string? jobToken)
    {
        if (id.Job != null) return jobToken!; // the job token already expires with the job
        return BearerPrefix + Protector.Protect($"{id.User!.Id}:{(id.Scopes is { } s ? (int)s : -1)}", BearerLifetime);
    }

    /// <summary>Decides whether <paramref name="id"/> may do <paramref name="op"/> in the registry of project <paramref name="projectKey"/>.</summary>
    public async Task<RegistryCheck> AuthorizeAsync(RegistryIdentity? id, string projectKey, RegistryOp op)
    {
        if (id == null) return new(RegistryDecision.Unauthenticated, "Authentication required.");
        var key = projectKey.ToUpperInvariant();
        Project? project;
        await using (var db = await dbFactory.CreateDbContextAsync())
            project = await db.Projects.AsNoTracking().FirstOrDefaultAsync(p => p.Key == key);
        const string notFound = "Project not found, or its package registry is off.";
        if (project == null) return new(RegistryDecision.NotFound, notFound);

        var permission = op switch { RegistryOp.Read => Permission.ViewProject, RegistryOp.Write => Permission.PublishPackages, _ => Permission.DeletePackages };
        ProjectRole? role;
        if (id.Job is { } job)
        {
            // Developer in its own project; elsewhere only what the pipeline's user can read.
            if (job.ProjectId == project.Id) role = ProjectRole.Developer;
            else role = job.TriggeredById is { } uid && await access.GetRoleAsync(project.Id, uid) is not null ? ProjectRole.Viewer : null;
        }
        else role = await access.GetRoleAsync(project.Id, id.User!.Id);

        if (role == null || !await services.GetRequiredService<PackageService>().IsEnabledAsync(project.Id))
            return new(RegistryDecision.NotFound, notFound);
        if (!ProjectAccess.RoleHas(role.Value, permission))
        {
            log.LogInformation("Registry {Op} in {Key} denied for {Who} (role {Role})", op, project.Key, id.Name, role);
            return new(RegistryDecision.Forbidden, id.Job != null
                ? (op == RegistryOp.Write ? "A job token can only publish to its own project." : "A job token can't delete packages.")
                : $"You don't have permission to {(op == RegistryOp.Write ? "publish packages" : "delete packages")} in this project (requires {ProjectAccess.MinimumRole(permission)}; you are {role}).");
        }
        if (op != RegistryOp.Read && id.Scopes is { } scopes && (scopes & (TokenScopes.WriteRepository | TokenScopes.WriteApi)) == 0)
            return new(RegistryDecision.Forbidden, "This access token needs the write_repository or write_api scope to publish or delete packages.");
        return new(RegistryDecision.Allowed, "", project);
    }
}
