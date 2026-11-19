using System.Security.Cryptography;
using System.Text.RegularExpressions;
using GitBench.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace GitBench.Services.Ci;

/// <summary>A CI variable as the UI sees it: never its value.</summary>
public sealed record CiVariableInfo(int Id, string Name, bool IsProtected, int? RepositoryId, DateTime UpdatedAt, string? UpdatedBy);

/// <summary>The variables a pipeline gets, plus notes for its log (e.g. a value that could not be decrypted).</summary>
public sealed record CiResolvedVariables(IReadOnlyDictionary<string, string> Values, IReadOnlyList<string> Notes)
{
    public static readonly CiResolvedVariables None = new(new Dictionary<string, string>(), []);
}

/// <summary>
/// CI secret variables per project and per repository (<see cref="CiVariable"/>). Values are encrypted with ASP.NET Core
/// Data Protection and never returned to the UI. Managing them needs <see cref="Permission.ManageRepositories"/>
/// (Maintainer+). <see cref="ResolveAsync"/> is the runner's entry point: repository variables override project ones
/// with the same name, and protected variables only reach pipelines whose branch is protected.
/// </summary>
public partial class CiVariableService(IDbContextFactory<AppDbContext> dbFactory, IDataProtectionProvider dataProtection,
    ProjectAccess access, BranchProtectionService protection, Audit.AuditLog audit, ILogger<CiVariableService> log)
{
    public const int MaxValueLength = 10_000;
    private readonly IDataProtector protector = dataProtection.CreateProtector("GitBench.CiVariables.v1");

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]{0,99}$")]
    private static partial Regex NamePattern();

    /// <summary>Null if <paramref name="name"/> can be used as a variable name, else why not.</summary>
    public static string? ValidateName(string name)
    {
        if (!NamePattern().IsMatch(name)) return "Use letters, digits and underscores, not starting with a digit (max 100 characters).";
        if (name == "CI" || name.StartsWith("GITBENCH_", StringComparison.OrdinalIgnoreCase))
            return "CI and GITBENCH_* are set by GitBench itself.";
        return null;
    }

    /// <summary>The project's variables (<paramref name="repositoryId"/> null) or one repository's own variables.</summary>
    public async Task<List<CiVariableInfo>> ListAsync(int projectId, int? repositoryId)
    {
        await access.RequireAsync(projectId, Permission.ManageRepositories);
        await using var db = await dbFactory.CreateDbContextAsync();
        await EnsureRepoInProjectAsync(db, projectId, repositoryId);
        return await db.CiVariables.AsNoTracking()
            .Where(v => v.ProjectId == projectId && v.RepositoryId == repositoryId)
            .OrderBy(v => v.Name)
            .Select(v => new CiVariableInfo(v.Id, v.Name, v.IsProtected, v.RepositoryId, v.UpdatedAt, v.UpdatedBy == null ? null : v.UpdatedBy.UserName))
            .ToListAsync();
    }

    /// <summary>
    /// Creates (<paramref name="id"/> null) or updates a variable. On update, a null or empty <paramref name="value"/>
    /// keeps the stored value, so the form never needs to show it.
    /// </summary>
    public async Task SaveAsync(int projectId, int? repositoryId, int? id, string name, string? value, bool isProtected)
    {
        var me = await access.ResolveUserIdAsync();
        await access.RequireAsync(projectId, Permission.ManageRepositories);
        name = name.Trim();
        if (ValidateName(name) is { } bad) throw new InvalidOperationException(bad);
        if (value != null)
        {
            if (value.Length > MaxValueLength) throw new InvalidOperationException($"The value is too long (max {MaxValueLength} characters).");
            if (value.Contains('\n') || value.Contains('\r')) throw new InvalidOperationException("The value must be a single line.");
        }

        await using var db = await dbFactory.CreateDbContextAsync();
        await EnsureRepoInProjectAsync(db, projectId, repositoryId);
        if (await db.CiVariables.AnyAsync(v => v.ProjectId == projectId && v.RepositoryId == repositoryId && v.Name == name && v.Id != (id ?? 0)))
            throw new InvalidOperationException($"A variable named {name} already exists here.");

        CiVariable v;
        var created = id == null;
        if (created)
        {
            if (string.IsNullOrEmpty(value)) throw new InvalidOperationException("Enter a value.");
            v = new CiVariable { ProjectId = projectId, RepositoryId = repositoryId };
            db.CiVariables.Add(v);
        }
        else
        {
            v = await db.CiVariables.FirstOrDefaultAsync(x => x.Id == id && x.ProjectId == projectId && x.RepositoryId == repositoryId)
                ?? throw new InvalidOperationException("That variable no longer exists.");
        }
        var renamedFrom = !created && v.Name != name ? v.Name : null;
        var valueChanged = !string.IsNullOrEmpty(value);
        v.Name = name;
        v.IsProtected = isProtected;
        if (valueChanged) v.EncryptedValue = protector.Protect(value!);
        v.UpdatedAt = DateTime.UtcNow;
        v.UpdatedById = me;
        await db.SaveChangesAsync();

        await audit.RecordAsync(created ? "ci_variable.created" : "ci_variable.updated", "ci_variable", v.Id, projectId,
            new { name, renamedFrom, scope = repositoryId == null ? "project" : "repository", repositoryId, isProtected, valueChanged });
    }

    public async Task DeleteAsync(int projectId, int? repositoryId, int id)
    {
        await access.RequireAsync(projectId, Permission.ManageRepositories);
        await using var db = await dbFactory.CreateDbContextAsync();
        var v = await db.CiVariables.FirstOrDefaultAsync(x => x.Id == id && x.ProjectId == projectId && x.RepositoryId == repositoryId);
        if (v == null) return;
        db.CiVariables.Remove(v);
        await db.SaveChangesAsync();
        await audit.RecordAsync("ci_variable.deleted", "ci_variable", id, projectId,
            new { name = v.Name, scope = repositoryId == null ? "project" : "repository", repositoryId });
    }

    /// <summary>
    /// The branch whose protection decides which CI variables (and cache scope) a pipeline gets: normally its own ref. A
    /// merge queue pipeline runs on <c>refs/merge-queue/…</c>, which GitBench itself created, so it would never see
    /// protected variables. It is treated as running on its target branch only when ALL of these hold: it belongs to an
    /// active queue entry of this repository, its PR is not from a fork, and the user who queued it can merge pull
    /// requests in the project right now (so they could already have merged this very code, whose pipeline on the target
    /// would get those variables). Anything else keeps the pipeline's own ref (no protected variables).
    /// </summary>
    public async Task<string> VariableBranchAsync(Pipeline p)
    {
        if (p.Trigger != PipelineTrigger.MergeQueue || p.IsForkPullRequest || !p.Ref.StartsWith(Git.GitOps.MergeQueueRefPrefix, StringComparison.Ordinal))
            return p.Ref;
        await using var db = await dbFactory.CreateDbContextAsync();
        var entry = await db.MergeQueueEntries.AsNoTracking()
            .Where(e => e.PipelineId == p.Id && e.RepositoryId == p.RepositoryId
                        && (e.State == MergeQueueEntryState.Testing || e.State == MergeQueueEntryState.Passed))
            .Select(e => new { e.TargetBranch, e.EnqueuedById, e.PullRequest.IsCrossFork, e.PullRequest.RepositoryId, ProjectId = e.Repository.ProjectId })
            .FirstOrDefaultAsync();
        if (entry == null || entry.IsCrossFork || !p.Ref.StartsWith(Git.GitOps.MergeQueueRefPrefix + entry.TargetBranch + "/", StringComparison.Ordinal)) return p.Ref;
        if (!await access.CanAsync(entry.ProjectId, Permission.MergePullRequest, entry.EnqueuedById)) return p.Ref;
        return entry.TargetBranch;
    }

    /// <summary>
    /// The decrypted variables for a pipeline on <paramref name="branch"/> of the repository (internal: no permission
    /// check). Protected variables are left out unless the branch is protected; for pull request pipelines the branch
    /// is the source branch, so an unprotected source branch never sees them. A pipeline of a pull request from a fork
    /// (<paramref name="forkPullRequest"/>) gets none at all, protected or not: its code comes from outside the project.
    /// </summary>
    public async Task<CiResolvedVariables> ResolveAsync(int projectId, int repositoryId, string branch, bool forkPullRequest = false)
    {
        if (forkPullRequest)
            return new(new Dictionary<string, string>(), ["[gitbench] This pipeline runs code from a fork's pull request: no CI variables are exposed to it."]);
        await using var db = await dbFactory.CreateDbContextAsync();
        var rows = await db.CiVariables.AsNoTracking()
            .Where(v => v.ProjectId == projectId && (v.RepositoryId == null || v.RepositoryId == repositoryId))
            .ToListAsync();
        if (rows.Count == 0) return CiResolvedVariables.None;

        var branchProtected = (await protection.ForBranchAsync(repositoryId, branch)).IsProtected;
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var notes = new List<string>();
        var skippedProtected = 0;
        // Project variables first, then repository ones, which override them.
        foreach (var v in rows.OrderBy(v => v.RepositoryId != null).ThenBy(v => v.Name))
        {
            if (v.IsProtected && !branchProtected) { values.Remove(v.Name); skippedProtected++; continue; }
            try { values[v.Name] = protector.Unprotect(v.EncryptedValue); }
            catch (CryptographicException ex)
            {
                log.LogWarning(ex, "CI variable {Name} (id {Id}) could not be decrypted; the Data Protection key may be missing", v.Name, v.Id);
                notes.Add($"[gitbench] CI variable {v.Name} could not be decrypted and was skipped (the server's Data Protection keys changed). Save it again.");
            }
        }
        if (skippedProtected > 0)
            notes.Add($"[gitbench] {skippedProtected} protected CI variable(s) not exposed: branch '{branch}' is not protected.");
        return new(values, notes);
    }

    private static async Task EnsureRepoInProjectAsync(AppDbContext db, int projectId, int? repositoryId)
    {
        if (repositoryId is { } rid && !await db.Repositories.AnyAsync(r => r.Id == rid && r.ProjectId == projectId))
            throw new InvalidOperationException("That repository isn't part of this project.");
    }
}
