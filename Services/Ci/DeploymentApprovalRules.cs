using ByteBench.Data;
using Microsoft.EntityFrameworkCore;

namespace ByteBench.Services.Ci;

/// <summary>
/// Who approves deployments to a protected environment. With approvers listed on the environment (users, or every direct
/// member of a group), those of them who can push (Developer+) approve; with none listed, Maintainers and Owners do.
/// A job needs <see cref="CiEnvironment.RequiredApprovals"/> approvals from distinct approvers before it runs.
/// </summary>
public static class DeploymentApprovalRules
{
    /// <summary>The environment a job deploys to, with its approvers (null if it hasn't been recorded yet).</summary>
    public static Task<CiEnvironment?> EnvironmentAsync(AppDbContext db, int repositoryId, string? name) =>
        name == null ? Task.FromResult<CiEnvironment?>(null)
            : db.CiEnvironments.AsNoTracking().Include(e => e.Approvers).FirstOrDefaultAsync(e => e.RepositoryId == repositoryId && e.Name == name);

    public static int Required(CiEnvironment? env) => Math.Max(1, env?.RequiredApprovals ?? 1);

    /// <summary>True if the job waits for approval: it says so, or its environment is protected.</summary>
    public static bool IsGated(PipelineJob job, CiEnvironment? env) => job.RequiresApproval || env?.IsProtected == true;

    public static async Task<bool> CanApproveAsync(AppDbContext db, ProjectAccess access, int projectId, CiEnvironment? env, int userId)
    {
        if (env == null || env.Approvers.Count == 0) return await access.CanAsync(projectId, Permission.ManageRepositories, userId);
        if (!await access.CanAsync(projectId, Permission.PushCode, userId)) return false;
        if (env.Approvers.Any(a => a.UserId == userId)) return true;
        var groups = env.Approvers.Where(a => a.GroupId != null).Select(a => a.GroupId!.Value).ToList();
        return groups.Count > 0 && await db.GroupMembers.AnyAsync(m => m.UserId == userId && groups.Contains(m.GroupId));
    }

    /// <summary>Everyone who may approve (for "waiting for your approval" notifications).</summary>
    public static async Task<List<int>> ApproverIdsAsync(AppDbContext db, ProjectAccess access, int projectId, CiEnvironment? env)
    {
        List<int> candidates;
        if (env == null || env.Approvers.Count == 0)
        {
            candidates = await db.ProjectMembers.Where(m => m.ProjectId == projectId).Select(m => m.UserId).ToListAsync();
            if (await db.Projects.Where(p => p.Id == projectId).Select(p => p.GroupId).FirstOrDefaultAsync() is { } groupId)
            {
                var chain = await ProjectAccess.GroupChainAsync(db, groupId);
                candidates.AddRange(await db.GroupMembers.Where(m => chain.Contains(m.GroupId)).Select(m => m.UserId).ToListAsync());
            }
        }
        else
        {
            candidates = env.Approvers.Where(a => a.UserId != null).Select(a => a.UserId!.Value).ToList();
            var groups = env.Approvers.Where(a => a.GroupId != null).Select(a => a.GroupId!.Value).ToList();
            if (groups.Count > 0) candidates.AddRange(await db.GroupMembers.Where(m => groups.Contains(m.GroupId)).Select(m => m.UserId).ToListAsync());
        }
        var result = new List<int>();
        foreach (var id in candidates.Distinct())
            if (await CanApproveAsync(db, access, projectId, env, id)) result.Add(id);
        return result;
    }

    /// <summary>Approvals of a job's current run (not stale), oldest first.</summary>
    public static Task<List<DeploymentApproval>> CurrentAsync(AppDbContext db, int jobId) =>
        db.DeploymentApprovals.AsNoTracking().Include(a => a.User).Where(a => a.JobId == jobId && !a.IsStale).OrderBy(a => a.Id).ToListAsync();

    /// <summary>A short "2 of 3 approvals" style summary of the rule.</summary>
    public static string Describe(CiEnvironment env)
    {
        var n = Required(env);
        var who = env.Approvers.Count == 0 ? "Maintainers"
            : string.Join(", ", env.Approvers.Select(a => a.User?.DisplayName ?? (a.Group != null ? "group " + a.Group.FullPath : "?")));
        return $"{n} approval{(n == 1 ? "" : "s")} from {who}";
    }
}
