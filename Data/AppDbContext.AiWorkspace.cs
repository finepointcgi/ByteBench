using Microsoft.EntityFrameworkCore;

namespace ByteBench.Data;

public partial class AppDbContext
{
    public DbSet<AiWorkspacePreference> AiWorkspacePreferences => Set<AiWorkspacePreference>();
    public DbSet<AiChangeSet> AiChangeSets => Set<AiChangeSet>();
    public DbSet<AiChangeFile> AiChangeFiles => Set<AiChangeFile>();
    public DbSet<AiRun> AiRuns => Set<AiRun>();
    public DbSet<AiRunEvent> AiRunEvents => Set<AiRunEvent>();

    private static void ConfigureAiWorkspace(ModelBuilder b)
    {
        b.Entity<AiWorkspacePreference>().HasIndex(p => new { p.UserId, p.WorkspaceId }).IsUnique();
        b.Entity<AiWorkspacePreference>().HasOne<User>().WithMany().HasForeignKey(p => p.UserId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<AiWorkspacePreference>().HasOne<Workspace>().WithMany().HasForeignKey(p => p.WorkspaceId).OnDelete(DeleteBehavior.Cascade);

        b.Entity<AiChangeSet>().HasIndex(c => new { c.WorkspaceId, c.State });
        b.Entity<AiChangeSet>().HasOne(c => c.Workspace).WithMany().HasForeignKey(c => c.WorkspaceId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<AiChangeFile>().HasOne(f => f.ChangeSet).WithMany(c => c.Files).HasForeignKey(f => f.ChangeSetId).OnDelete(DeleteBehavior.Cascade);

        b.Entity<AiRun>().HasIndex(r => r.PublicId).IsUnique();
        b.Entity<AiRun>().HasIndex(r => new { r.IssueId, r.Status });
        b.Entity<AiRun>().HasIndex(r => new { r.Status, r.CreatedAt });
        b.Entity<AiRun>().HasOne(r => r.Issue).WithMany().HasForeignKey(r => r.IssueId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<AiRun>().HasOne(r => r.Repository).WithMany().HasForeignKey(r => r.RepositoryId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<AiRun>().HasOne(r => r.RequestedBy).WithMany().HasForeignKey(r => r.RequestedById).OnDelete(DeleteBehavior.Cascade);
        b.Entity<AiRunEvent>().HasIndex(e => new { e.RunId, e.Id });
        b.Entity<AiRunEvent>().HasOne(e => e.Run).WithMany(r => r.Events).HasForeignKey(e => e.RunId).OnDelete(DeleteBehavior.Cascade);
    }
}
