using Microsoft.EntityFrameworkCore;

namespace ByteBench.Data;

public partial class AppDbContext
{
    public DbSet<Workspace> Workspaces => Set<Workspace>();
    public DbSet<ProjectWorkspaceSettings> ProjectWorkspaceSettings => Set<ProjectWorkspaceSettings>();
    public DbSet<RepositoryPrebuildSettings> RepositoryPrebuildSettings => Set<RepositoryPrebuildSettings>();
    public DbSet<WorkspacePrebuild> WorkspacePrebuilds => Set<WorkspacePrebuild>();
    public DbSet<WorkspaceAllocation> WorkspaceAllocations => Set<WorkspaceAllocation>();
    public DbSet<UserWorkspacePreference> UserWorkspacePreferences => Set<UserWorkspacePreference>();

    private static void ConfigureWorkspaces(ModelBuilder b)
    {
        b.Entity<Workspace>().HasIndex(w => w.PublicId).IsUnique();
        b.Entity<Workspace>().HasIndex(w => new { w.OwnerId, w.State });
        b.Entity<Workspace>().HasIndex(w => w.State);
        b.Entity<Workspace>().HasOne(w => w.Owner).WithMany().HasForeignKey(w => w.OwnerId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<Workspace>().HasOne(w => w.Repository).WithMany().HasForeignKey(w => w.RepositoryId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<ProjectWorkspaceSettings>().HasOne<Project>().WithMany().HasForeignKey(s => s.ProjectId).OnDelete(DeleteBehavior.Cascade);
        ConfigureWorkspacePrebuilds(b);
    }

    private static void ConfigureWorkspacePrebuilds(ModelBuilder b)
    {
        b.Entity<RepositoryPrebuildSettings>().HasOne(s => s.Repository).WithMany().HasForeignKey(s => s.RepositoryId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<WorkspacePrebuild>().HasOne(p => p.Repository).WithMany().HasForeignKey(p => p.RepositoryId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<WorkspacePrebuild>().HasIndex(p => new { p.RepositoryId, p.Ref, p.State });
        b.Entity<WorkspaceAllocation>().HasOne(a => a.Workspace).WithOne().HasForeignKey<WorkspaceAllocation>(a => a.WorkspaceId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<UserWorkspacePreference>().HasOne(p => p.User).WithMany().HasForeignKey(p => p.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}
