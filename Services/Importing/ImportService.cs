using ByteBench.Data;
using ByteBench.Services.Mirroring;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ByteBench.Services.Importing;

/// <summary>What wizard step 2 submits.</summary>
public sealed class ImportInput
{
    public string Url { get; set; } = "";
    /// <summary>Used for this import only (held in memory); stored, encrypted, only when <see cref="KeepAsMirror"/> is on.</summary>
    public string? Username { get; set; }
    public string? Secret { get; set; }
    public string Name { get; set; } = "";
    public string? Slug { get; set; }
    public string? Description { get; set; }
    public string? DefaultBranch { get; set; }
    public bool KeepAsMirror { get; set; }
}

/// <summary>
/// The repository import wizard's backend (scoped). Everything needs <see cref="Permission.ManageRepositories"/>, the same as creating
/// a repository, and is audited (<c>repository.import_*</c>, then <c>repository.created</c> and <c>repository.imported</c> from the job).
/// The job itself runs in <see cref="ImportRunner"/>.
/// </summary>
public class ImportService(IDbContextFactory<AppDbContext> dbFactory, ProjectAccess access, Audit.AuditLog audit, ImportRunner runner,
    IOptionsMonitor<ImportOptions> options)
{
    public ImportOptions Limits => options.CurrentValue;

    /// <summary>Wizard step 1: checks the URL and credentials by listing the remote's refs.</summary>
    public async Task<ImportProbe> ProbeAsync(int projectId, string url, string? username, string? secret, int? actorId = null, CancellationToken ct = default)
    {
        await access.RequireAsync(projectId, Permission.ManageRepositories, actorId);
        if (runner.Validate(url) is { } invalid) throw new InvalidOperationException(invalid);
        CheckCredentials(username, secret);
        try { return await runner.ProbeAsync(url.Trim(), Clean(username), Clean(secret), ct); }
        catch (MirrorException ex) { throw new InvalidOperationException(ex.Message); }
    }

    /// <summary>Wizard step 2 → 3: queues the import and returns it (watch it with <see cref="GetAsync"/>).</summary>
    public async Task<RepositoryImport> StartAsync(int projectId, ImportInput input, int? actorId = null)
    {
        await access.RequireAsync(projectId, Permission.ManageRepositories, actorId);
        var uid = await access.ResolveUserIdAsync(actorId) ?? throw new ForbiddenException("Sign in to import a repository.");
        var url = (input.Url ?? "").Trim();
        if (runner.Validate(url) is { } invalid) throw new InvalidOperationException(invalid);
        CheckCredentials(input.Username, input.Secret);
        var (name, slug) = RepositoryService.NormalizeNameAndSlug(input.Name, input.Slug);
        var branch = string.IsNullOrWhiteSpace(input.DefaultBranch) ? null : input.DefaultBranch.Trim();
        if (branch != null && !Git.GitOps.IsValidBranchName(branch)) throw new InvalidOperationException($"'{branch}' is not a valid branch name.");

        await using var db = await dbFactory.CreateDbContextAsync();
        var project = await db.Projects.AsNoTracking().FirstOrDefaultAsync(p => p.Id == projectId) ?? throw new InvalidOperationException("Project not found.");
        if (await db.Repositories.AnyAsync(r => r.ProjectId == projectId && r.Slug == slug))
            throw new InvalidOperationException($"A repository with slug '{slug}' already exists in {project.Key}.");
        var active = await db.RepositoryImports.Where(i => i.ProjectId == projectId && (i.Status == ImportStatus.Queued || i.Status == ImportStatus.Cloning))
            .Select(i => i.Slug).ToListAsync();
        if (active.Contains(slug)) throw new InvalidOperationException($"An import into '{slug}' is already in progress.");
        if (active.Count >= Limits.MaxActivePerProject)
            throw new InvalidOperationException($"This project already has {active.Count} imports in progress. Wait for one to finish or cancel it.");

        var job = new RepositoryImport
        {
            ProjectId = projectId, RequestedById = uid, SourceUrl = url, Name = name, Slug = slug, DefaultBranch = branch, KeepAsMirror = input.KeepAsMirror,
            Description = string.IsNullOrWhiteSpace(input.Description) ? null : input.Description.Trim(),
        };
        db.RepositoryImports.Add(job);
        await db.SaveChangesAsync();
        await audit.RecordAsync("repository.import_requested", "repository_import", job.Id, projectId,
            new { slug, source = url, credentials = !string.IsNullOrEmpty(input.Secret), keepAsMirror = input.KeepAsMirror }, uid);
        runner.Enqueue(job.Id, Clean(input.Username), Clean(input.Secret));
        return job;
    }

    /// <summary>An import, or null if it doesn't exist. Needs Maintainer+ on its project.</summary>
    public async Task<RepositoryImport?> GetAsync(int importId, int? actorId = null)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var job = await db.RepositoryImports.AsNoTracking().Include(i => i.Project).Include(i => i.Repository).ThenInclude(r => r!.Project)
            .FirstOrDefaultAsync(i => i.Id == importId);
        if (job == null || !await access.CanAsync(job.ProjectId, Permission.ManageRepositories, actorId)) return null;
        return job;
    }

    /// <summary>The project's most recent imports, newest first.</summary>
    public async Task<List<RepositoryImport>> ListAsync(int projectId, int take = 10, int? actorId = null)
    {
        await access.RequireAsync(projectId, Permission.ManageRepositories, actorId);
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.RepositoryImports.AsNoTracking().Include(i => i.Repository).ThenInclude(r => r!.Project).Where(i => i.ProjectId == projectId)
            .OrderByDescending(i => i.Id).Take(take).ToListAsync();
    }

    /// <summary>Stops a queued or running import (its partial data is removed). Returns false if it had already finished.</summary>
    public async Task<bool> CancelAsync(int importId, int? actorId = null)
    {
        var job = await GetAsync(importId, actorId) ?? throw new InvalidOperationException("Import not found.");
        var cancelled = await runner.CancelAsync(importId);
        if (cancelled) await audit.RecordAsync("repository.import_cancel_requested", "repository_import", importId, job.ProjectId, new { slug = job.Slug }, actorId);
        return cancelled;
    }

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static void CheckCredentials(string? username, string? secret)
    {
        if (username?.Length > 200 || username?.Any(char.IsControl) == true) throw new InvalidOperationException("The username isn't valid.");
        if (secret?.Length > 1000 || secret?.Any(char.IsControl) == true) throw new InvalidOperationException("The password or token isn't valid.");
    }
}
