using Microsoft.EntityFrameworkCore;

namespace ByteBench.Data;

public partial class AppDbContext
{
    public DbSet<WorkspaceLink> WorkspaceLinks => Set<WorkspaceLink>();

    private static void ConfigureWorkspaceLinks(ModelBuilder b)
    {
        b.Entity<WorkspaceLink>().HasIndex(l => l.WorkspaceId);
        b.Entity<WorkspaceLink>().HasIndex(l => l.IssueId);
        b.Entity<WorkspaceLink>().HasIndex(l => l.PullRequestId);
        b.Entity<WorkspaceLink>().HasOne(l => l.Workspace).WithMany().HasForeignKey(l => l.WorkspaceId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<WorkspaceLink>().HasOne(l => l.Issue).WithMany().HasForeignKey(l => l.IssueId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<WorkspaceLink>().HasOne(l => l.PullRequest).WithMany().HasForeignKey(l => l.PullRequestId).OnDelete(DeleteBehavior.Cascade);
    }
}
