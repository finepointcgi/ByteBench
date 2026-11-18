using System.Text;
using System.Text.RegularExpressions;
using GitBench.Data;
using Microsoft.EntityFrameworkCore;

namespace GitBench.Services;

/// <summary>Branch name patterns: exact names, or globs where <c>*</c>/<c>?</c> stay within one path segment and <c>**</c> crosses <c>/</c>.</summary>
public static class BranchPatterns
{
    public static bool IsGlob(string pattern) => pattern.IndexOfAny(['*', '?']) >= 0;

    public static bool Matches(string pattern, string branch)
    {
        if (!IsGlob(pattern)) return string.Equals(pattern, branch, StringComparison.Ordinal);
        var rx = new StringBuilder("^");
        for (var i = 0; i < pattern.Length; i++)
        {
            var c = pattern[i];
            if (c == '*' && i + 1 < pattern.Length && pattern[i + 1] == '*') { rx.Append(".*"); i++; }
            else if (c == '*') rx.Append("[^/]*");
            else if (c == '?') rx.Append("[^/]");
            else rx.Append(Regex.Escape(c.ToString()));
        }
        rx.Append('$');
        return Regex.IsMatch(branch, rx.ToString(), RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    }

    /// <summary>Null if <paramref name="pattern"/> is usable, else why not.</summary>
    public static string? Validate(string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return "Enter a branch name or pattern.";
        if (pattern.Length > 250) return "The pattern is too long (max 250 characters).";
        if (pattern.Any(char.IsWhiteSpace) || pattern.Contains("..") || pattern.StartsWith('/') || pattern.EndsWith('/')
            || pattern.StartsWith("refs/", StringComparison.Ordinal))
            return "Use a branch name such as main or release/* (no spaces, no leading refs/heads/).";
        return null;
    }
}

/// <summary>The combined effect of every rule that matches a branch (strictest wins).</summary>
public sealed record EffectiveProtection(IReadOnlyList<BranchProtectionRule> Rules)
{
    public bool IsProtected => Rules.Count > 0;
    public int RequiredApprovals => Rules.Count == 0 ? 0 : Rules.Max(r => r.RequiredApprovals);
    public bool BlockDirectPushes => Rules.Any(r => r.BlockDirectPushes);
    public bool BlockForcePushes => Rules.Any(r => r.BlockForcePushes);
    public bool BlockDeletion => Rules.Any(r => r.BlockDeletion);
    public bool RequireChecksToPass => Rules.Any(r => r.RequireChecksToPass);
    public bool DismissStaleApprovals => Rules.Any(r => r.DismissStaleApprovals);
    public string Describe => string.Join(", ", Rules.Select(r => r.Pattern));
}

public enum RefChangeKind { Create, Update, Delete }

/// <summary>Thrown when branch protection refuses a ref change or a merge. An <see cref="InvalidOperationException"/>, so pages show its message.</summary>
public class BranchProtectedException(string message) : InvalidOperationException(message);

/// <summary>
/// Branch protection rules per repository: CRUD (requires <see cref="Permission.ManageRepositories"/>) and the
/// checks every ref-changing path calls: Git HTTP push (<see cref="Git.ProtectedPushGuard"/>), branch create/delete
/// (<see cref="RepositoryService"/>) and PR merge (<see cref="MergeGate"/>).
/// <para>
/// A rule with <see cref="BranchProtectionRule.AllowAdminBypass"/> doesn't apply to site admins and project Owners.
/// "Block direct pushes" refuses creating and updating matching branches outside a PR merge; deletion is governed
/// only by "Block deletion"; "Block force pushes" refuses non-fast-forward updates.
/// </para>
/// </summary>
public class BranchProtectionService(IDbContextFactory<AppDbContext> dbFactory, ProjectAccess access, Audit.AuditLog audit)
{
    // ---------- Rules ----------

    public async Task<List<BranchProtectionRule>> ListAsync(int repositoryId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var projectId = await db.Repositories.Where(r => r.Id == repositoryId).Select(r => (int?)r.ProjectId).FirstOrDefaultAsync();
        if (projectId == null || !await access.CanAsync(projectId.Value, Permission.ViewProject)) return [];
        return await db.BranchProtectionRules.Where(r => r.RepositoryId == repositoryId).OrderBy(r => r.Pattern).ToListAsync();
    }

    /// <summary>Creates (Id 0) or updates a rule.</summary>
    public async Task<BranchProtectionRule> SaveAsync(int repositoryId, BranchProtectionRule input, int? actorId = null)
    {
        await access.RequireForRepositoryAsync(repositoryId, Permission.ManageRepositories, actorId);
        var pattern = input.Pattern.Trim();
        if (pattern.StartsWith("refs/heads/", StringComparison.Ordinal)) pattern = pattern["refs/heads/".Length..];
        if (BranchPatterns.Validate(pattern) is { } problem) throw new InvalidOperationException(problem);
        if (input.RequiredApprovals is < 0 or > 10) throw new InvalidOperationException("Required approvals must be between 0 and 10.");

        await using var db = await dbFactory.CreateDbContextAsync();
        var projectId = await db.Repositories.Where(r => r.Id == repositoryId).Select(r => r.ProjectId).FirstAsync();
        if (await db.BranchProtectionRules.AnyAsync(r => r.RepositoryId == repositoryId && r.Pattern == pattern && r.Id != input.Id))
            throw new InvalidOperationException($"There is already a rule for '{pattern}'.");
        var reviewers = new List<int>();
        foreach (var uid in input.DefaultReviewerIds.Distinct())
            if (await access.CanAsync(projectId, Permission.ReviewPullRequest, uid)) reviewers.Add(uid);

        BranchProtectionRule rule;
        if (input.Id == 0)
        {
            rule = new BranchProtectionRule { RepositoryId = repositoryId };
            db.BranchProtectionRules.Add(rule);
        }
        else
        {
            rule = await db.BranchProtectionRules.FirstOrDefaultAsync(r => r.Id == input.Id && r.RepositoryId == repositoryId)
                   ?? throw new InvalidOperationException("Rule not found.");
        }
        rule.Pattern = pattern;
        rule.RequiredApprovals = input.RequiredApprovals;
        rule.BlockDirectPushes = input.BlockDirectPushes;
        rule.BlockForcePushes = input.BlockForcePushes;
        rule.BlockDeletion = input.BlockDeletion;
        rule.RequireChecksToPass = input.RequireChecksToPass;
        rule.DismissStaleApprovals = input.DismissStaleApprovals;
        rule.AllowAdminBypass = input.AllowAdminBypass;
        rule.DefaultReviewerIds = reviewers;
        rule.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        await audit.RecordAsync(input.Id == 0 ? "branch_protection.created" : "branch_protection.updated", "branch_protection_rule", rule.Id, projectId,
            new { repositoryId, rule.Pattern, rule.RequiredApprovals, rule.BlockDirectPushes, rule.BlockForcePushes, rule.BlockDeletion,
                  rule.RequireChecksToPass, rule.DismissStaleApprovals, rule.AllowAdminBypass, rule.DefaultReviewerIds }, actorId);
        return rule;
    }

    public async Task DeleteAsync(int repositoryId, int ruleId, int? actorId = null)
    {
        await access.RequireForRepositoryAsync(repositoryId, Permission.ManageRepositories, actorId);
        await using var db = await dbFactory.CreateDbContextAsync();
        var gone = await db.BranchProtectionRules.Where(r => r.Id == ruleId && r.RepositoryId == repositoryId)
            .Select(r => new { r.Pattern, r.Repository.ProjectId }).FirstOrDefaultAsync();
        await db.BranchProtectionRules.Where(r => r.Id == ruleId && r.RepositoryId == repositoryId).ExecuteDeleteAsync();
        if (gone != null) await audit.RecordAsync("branch_protection.deleted", "branch_protection_rule", ruleId, gone.ProjectId, new { repositoryId, gone.Pattern }, actorId);
    }

    // ---------- Evaluation ----------

    /// <summary>Every rule of the repository that matches <paramref name="branch"/> (regardless of who is acting).</summary>
    public async Task<EffectiveProtection> ForBranchAsync(int repositoryId, string branch)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var rules = await db.BranchProtectionRules.AsNoTracking().Where(r => r.RepositoryId == repositoryId).ToListAsync();
        return new(rules.Where(r => BranchPatterns.Matches(r.Pattern, branch)).ToList());
    }

    /// <summary>All rules of a repository, for evaluating many refs at once (no permission check: internal use).</summary>
    public async Task<List<BranchProtectionRule>> RulesForAsync(int repositoryId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.BranchProtectionRules.AsNoTracking().Where(r => r.RepositoryId == repositoryId).ToListAsync();
    }

    /// <summary>Site admins and project Owners: the people "allow admins to bypass" exempts.</summary>
    public async Task<bool> IsBypassAdminAsync(int projectId, int? userId = null) =>
        await access.GetRoleAsync(projectId, userId) == ProjectRole.Owner;

    /// <summary>The rules matching <paramref name="branch"/> that apply to the user (bypassable rules are dropped for admins).</summary>
    public static EffectiveProtection ApplicableTo(IEnumerable<BranchProtectionRule> rules, string branch, bool isAdmin) =>
        new(rules.Where(r => BranchPatterns.Matches(r.Pattern, branch) && !(isAdmin && r.AllowAdminBypass)).ToList());

    /// <summary>
    /// Why the ref change is refused, or null if it's allowed. <paramref name="isFastForward"/> is only consulted for
    /// updates (null = unknown, treated as allowed: the caller must check it later).
    /// </summary>
    public static string? Refusal(EffectiveProtection p, string branch, RefChangeKind kind, bool? isFastForward = true)
    {
        if (!p.IsProtected) return null;
        return kind switch
        {
            RefChangeKind.Delete when p.BlockDeletion => $"branch '{branch}' is protected and can't be deleted",
            RefChangeKind.Create when p.BlockDirectPushes => $"branch '{branch}' is protected: it can't be created directly (ask a Maintainer to relax the rule)",
            RefChangeKind.Update when p.BlockDirectPushes => $"branch '{branch}' is protected: changes must go through a pull request",
            RefChangeKind.Update when p.BlockForcePushes && isFastForward == false => $"branch '{branch}' is protected: force pushes are not allowed",
            _ => null,
        };
    }

    /// <summary>Throws <see cref="BranchProtectedException"/> if branch protection refuses the change for the acting user.</summary>
    public async Task EnsureAllowedAsync(Repository repo, string branch, RefChangeKind kind, int? actorId = null, bool? isFastForward = true)
    {
        var rules = await RulesForAsync(repo.Id);
        if (rules.Count == 0) return;
        var admin = await IsBypassAdminAsync(repo.ProjectId, actorId);
        if (Refusal(ApplicableTo(rules, branch, admin), branch, kind, isFastForward) is { } why)
            throw new BranchProtectedException(char.ToUpperInvariant(why[0]) + why[1..] + ".");
    }
}
