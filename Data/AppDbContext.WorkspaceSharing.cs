using Microsoft.EntityFrameworkCore;

namespace ByteBench.Data;

public partial class AppDbContext
{
    public DbSet<WorkspaceShare> WorkspaceShares => Set<WorkspaceShare>();
    public DbSet<WorkspaceEgressRecord> WorkspaceEgressRecords => Set<WorkspaceEgressRecord>();

    private static void ConfigureWorkspaceSharing(ModelBuilder b)
    {
        b.Entity<WorkspaceShare>().HasIndex(s => new { s.WorkspaceId, s.UserId });
        b.Entity<WorkspaceShare>().HasIndex(s => new { s.UserId, s.EndedAt });
        b.Entity<WorkspaceShare>().HasOne(s => s.Workspace).WithMany().HasForeignKey(s => s.WorkspaceId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<WorkspaceShare>().HasOne(s => s.User).WithMany().HasForeignKey(s => s.UserId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<WorkspaceEgressRecord>().HasOne(r => r.Workspace).WithOne().HasForeignKey<WorkspaceEgressRecord>(r => r.WorkspaceId).OnDelete(DeleteBehavior.Cascade);
    }
}
