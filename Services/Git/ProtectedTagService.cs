using GitBench.Data;
using Microsoft.EntityFrameworkCore;

namespace GitBench.Services.Git;

/// <summary>
/// The protected tag decision, shared by git push (<see cref="ProtectedPushGuard.Policy"/>, HTTP and SSH) and the tag
/// pages and API (<see cref="ProtectedTagService.EnsureAllowedAsync"/>). Patterns are matched like branch protection
/// patterns (<see cref="BranchPatterns"/>). When several rules match a tag, the strictest setting of each wins.
/// </summary>
public static class ProtectedTags
{
    /// <summary>The rules of <paramref name="rules"/> that match <paramref name="tag"/>.</summary>
    public static List<ProtectedTagRule> Matching(IEnumerable<ProtectedTagRule> rules, string tag) =>
        rules.Where(r => BranchPatterns.Matches(r.Pattern, tag)).ToList();

    /// <summary>
    /// Null if someone with <paramref name="role"/> may make the change, else why not. Creating, moving and deleting a
    /// matching tag need the rules' <see cref="ProtectedTagRule.CreateRole"/>; "block deletion" and "block update" refuse
    /// deleting and moving it for everyone (remove or relax the rule first).
    /// </summary>
    public static string? Refusal(IEnumerable<ProtectedTagRule> rules, string tag, RefChangeKind kind, ProjectRole role)
    {
        var matching = Matching(rules, tag);
        if (matching.Count == 0) return null;
        if (kind == RefChangeKind.Delete && matching.Any(r => r.BlockDeletion))
            return $"tag '{tag}' is protected and can't be deleted";
        if (kind == RefChangeKind.Update && matching.Any(r => r.BlockUpdate))
            return $"tag '{tag}' is protected and can't be moved";
        var needed = matching.Max(r => r.CreateRole);
        if (role < needed)
        {
            var verb = kind switch { RefChangeKind.Delete => "delete", RefChangeKind.Update => "move", _ => "create" };
            var who = needed == ProjectRole.Maintainer ? "Maintainers and Owners" : $"{needed}s and above";
            return $"tag '{tag}' is protected: only {who} can {verb} it";
        }
        return null;
    }

    /// <summary>Null if <paramref name="pattern"/> is usable, else why not.</summary>
    public static string? Validate(string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return "Enter a tag name or pattern.";
        if (pattern.Length > 250) return "The pattern is too long (max 250 characters).";
        if (pattern.Any(char.IsWhiteSpace) || pattern.Contains("..") || pattern.StartsWith('/') || pattern.EndsWith('/')
            || pattern.StartsWith("refs/", StringComparison.Ordinal))
            return "Use a tag name such as v1.0 or v* (no spaces, no leading refs/tags/).";
        return null;
    }
}

/// <summary>
/// Protected tag rules per repository (<see cref="ProtectedTagRule"/>). Managing them requires
/// <see cref="Permission.ManageRepositories"/> (Maintainer+); changes are audited (<c>protected_tag.*</c>).
/// <see cref="EnsureAllowedAsync"/> is what the tag create and delete paths of <see cref="Releases.ReleaseService"/> call.
/// </summary>
public class ProtectedTagService(IDbContextFactory<AppDbContext> dbFactory, ProjectAccess access, Audit.AuditLog audit)
{
    public async Task<List<ProtectedTagRule>> ListAsync(int repositoryId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var projectId = await db.Repositories.Where(r => r.Id == repositoryId).Select(r => (int?)r.ProjectId).FirstOrDefaultAsync();
        if (projectId == null || !await access.CanAsync(projectId.Value, Permission.ViewProject)) return [];
        return await db.ProtectedTagRules.AsNoTracking().Where(r => r.RepositoryId == repositoryId).OrderBy(r => r.Pattern).ToListAsync();
    }

    /// <summary>The rules of a repository, without a permission check (for the push guard).</summary>
    public static Task<List<ProtectedTagRule>> RulesForAsync(AppDbContext db, int repositoryId) =>
        db.ProtectedTagRules.AsNoTracking().Where(r => r.RepositoryId == repositoryId).ToListAsync();

    /// <summary>Creates (Id 0) or updates a rule.</summary>
    public async Task<ProtectedTagRule> SaveAsync(int repositoryId, ProtectedTagRule input, int? actorId = null)
    {
        await access.RequireForRepositoryAsync(repositoryId, Permission.ManageRepositories, actorId);
        var pattern = (input.Pattern ?? "").Trim();
        if (pattern.StartsWith("refs/tags/", StringComparison.Ordinal)) pattern = pattern["refs/tags/".Length..];
        if (ProtectedTags.Validate(pattern) is { } problem) throw new InvalidOperationException(problem);
        if (input.CreateRole is not (ProjectRole.Developer or ProjectRole.Maintainer))
            throw new InvalidOperationException("Choose who can create matching tags: Developers or Maintainers.");

        await using var db = await dbFactory.CreateDbContextAsync();
        var projectId = await db.Repositories.Where(r => r.Id == repositoryId).Select(r => r.ProjectId).FirstAsync();
        if (await db.ProtectedTagRules.AnyAsync(r => r.RepositoryId == repositoryId && r.Pattern == pattern && r.Id != input.Id))
            throw new InvalidOperationException($"There is already a rule for '{pattern}'.");

        ProtectedTagRule rule;
        if (input.Id == 0)
        {
            rule = new ProtectedTagRule { RepositoryId = repositoryId };
            db.ProtectedTagRules.Add(rule);
        }
        else
        {
            rule = await db.ProtectedTagRules.FirstOrDefaultAsync(r => r.Id == input.Id && r.RepositoryId == repositoryId)
                   ?? throw new InvalidOperationException("Rule not found.");
        }
        rule.Pattern = pattern;
        rule.CreateRole = input.CreateRole;
        rule.BlockDeletion = input.BlockDeletion;
        rule.BlockUpdate = input.BlockUpdate;
        rule.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        await audit.RecordAsync(input.Id == 0 ? "protected_tag.created" : "protected_tag.updated", "protected_tag_rule", rule.Id, projectId,
            new { repositoryId, rule.Pattern, createRole = rule.CreateRole.ToString(), rule.BlockDeletion, rule.BlockUpdate }, actorId);
        return rule;
    }

    public async Task DeleteAsync(int repositoryId, int ruleId, int? actorId = null)
    {
        await access.RequireForRepositoryAsync(repositoryId, Permission.ManageRepositories, actorId);
        await using var db = await dbFactory.CreateDbContextAsync();
        var gone = await db.ProtectedTagRules.Where(r => r.Id == ruleId && r.RepositoryId == repositoryId)
            .Select(r => new { r.Pattern, r.Repository.ProjectId }).FirstOrDefaultAsync();
        if (gone == null) return;
        await db.ProtectedTagRules.Where(r => r.Id == ruleId && r.RepositoryId == repositoryId).ExecuteDeleteAsync();
        await audit.RecordAsync("protected_tag.deleted", "protected_tag_rule", ruleId, gone.ProjectId, new { repositoryId, gone.Pattern }, actorId);
    }

    /// <summary>
    /// Throws <see cref="BranchProtectedException"/> if the acting user (or <paramref name="actorId"/>) may not make the tag
    /// change, or if the repository is a pull mirror (its tags come from the upstream repository).
    /// </summary>
    public async Task EnsureAllowedAsync(Repository repo, string tag, RefChangeKind kind, int? actorId = null)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        if (await Mirroring.MirrorService.IsPullMirrorAsync(db, repo.Id))
            throw new BranchProtectedException(Mirroring.MirrorService.PullMirrorReadOnlyMessage);
        var rules = await RulesForAsync(db, repo.Id);
        if (rules.Count == 0) return;
        var role = await access.GetRoleAsync(repo.ProjectId, actorId) ?? ProjectRole.Viewer;
        if (ProtectedTags.Refusal(rules, tag, kind, role) is { } why)
            throw new BranchProtectedException(char.ToUpperInvariant(why[0]) + why[1..] + ".");
    }
}
