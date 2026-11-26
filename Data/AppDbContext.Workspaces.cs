using Microsoft.EntityFrameworkCore;

namespace ByteBench.Data;

public partial class AppDbContext
{
    public DbSet<Workspace> Workspaces => Set<Workspace>();
    public DbSet<ProjectWorkspaceSettings> ProjectWorkspaceSettings => Set<ProjectWorkspaceSettings>();

    private static void ConfigureWorkspaces(ModelBuilder b)
    {
        b.Entity<Workspace>().HasIndex(w => w.PublicId).IsUnique();
        b.Entity<Workspace>().HasIndex(w => new { w.OwnerId, w.State });
        b.Entity<Workspace>().HasIndex(w => w.State);
        b.Entity<Workspace>().HasOne(w => w.Owner).WithMany().HasForeignKey(w => w.OwnerId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<Workspace>().HasOne(w => w.Repository).WithMany().HasForeignKey(w => w.RepositoryId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<ProjectWorkspaceSettings>().HasOne<Project>().WithMany().HasForeignKey(s => s.ProjectId).OnDelete(DeleteBehavior.Cascade);
    }
}
