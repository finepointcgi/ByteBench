using ByteBench.Data;
using ByteBench.Services.Git;
using Microsoft.EntityFrameworkCore;

namespace ByteBench.Services;

public partial class RepositoryService
{
    /// <summary>
    /// Creates a repository from a bare repository the import wizard already filled (<paramref name="importedBarePath"/>, moved into
    /// place and pointed at <paramref name="defaultBranch"/>). Same permission, validation, slug rules and audit event as
    /// <see cref="CreateAsync"/> (plus where it came from). Nothing is left on disk or in the database if it fails.
    /// </summary>
    public async Task<Repository> CreateImportedAsync(int projectId, string name, string? slug, string? description, string defaultBranch,
        string importedBarePath, string sourceUrl, User author)
    {
        await access.RequireAsync(projectId, Permission.ManageRepositories, author.Id);
        (name, slug) = NormalizeNameAndSlug(name, slug);
        if (!GitOps.IsValidBranchName(defaultBranch)) throw new InvalidOperationException($"'{defaultBranch}' is not a valid branch name.");

        await using var db = await dbFactory.CreateDbContextAsync();
        var project = await db.Projects.FindAsync(projectId) ?? throw new InvalidOperationException("Project not found.");
        if (await db.Repositories.AnyAsync(r => r.ProjectId == projectId && r.Slug == slug))
            throw new InvalidOperationException($"A repository with slug '{slug}' already exists in {project.Key}.");

        var repo = new Repository
        {
            ProjectId = projectId, Project = project, Name = name, Slug = slug, DefaultBranch = defaultBranch,
            Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim(),
        };
        var path = storage.PathFor(project.Key, slug);
        await Task.Run(() =>
        {
            GitOps.MoveRepository(importedBarePath, path);
            GitOps.SetHead(path, defaultBranch);
        });
        try
        {
            db.Repositories.Add(repo);
            await db.SaveChangesAsync();
        }
        catch
        {
            try { Directory.Delete(path, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
        await audit.RecordAsync("repository.created", "repository", repo.Id, projectId, new { repo.Slug, repo.Name, imported = true, source = sourceUrl }, author.Id);
        return repo;
    }
}
