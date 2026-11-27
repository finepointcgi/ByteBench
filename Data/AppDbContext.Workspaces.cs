using Microsoft.EntityFrameworkCore;

namespace ByteBench.Data;

public partial class AppDbContext
{
    public DbSet<Workspace> Workspaces => Set<Workspace>();
    public DbSet<ProjectWorkspaceSettings> ProjectWorkspaceSettings => Set<ProjectWorkspaceSettings>();

    public DbSet<WorkspaceSecret> WorkspaceSecrets => Set<WorkspaceSecret>();
    public DbSet<UserWorkspaceSettings> UserWorkspaceSettings => Set<UserWorkspaceSettings>();

    private static void ConfigureWorkspaces(ModelBuilder b)
    {
        b.Entity<Workspace>().HasIndex(w => w.PublicId).IsUnique();
        b.Entity<Workspace>().HasIndex(w => new { w.OwnerId, w.State });
        b.Entity<Workspace>().HasIndex(w => w.State);
        b.Entity<Workspace>().HasOne(w => w.Owner).WithMany().HasForeignKey(w => w.OwnerId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<Workspace>().HasOne(w => w.Repository).WithMany().HasForeignKey(w => w.RepositoryId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<ProjectWorkspaceSettings>().HasOne<Project>().WithMany().HasForeignKey(s => s.ProjectId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<WorkspaceSecret>().HasIndex(x => new { x.UserId, x.Name });
        b.Entity<WorkspaceSecret>().HasIndex(x => new { x.RepositoryId, x.Name });
        b.Entity<WorkspaceSecret>().HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<WorkspaceSecret>().HasOne<Repository>().WithMany().HasForeignKey(x => x.RepositoryId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<UserWorkspaceSettings>().HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}
