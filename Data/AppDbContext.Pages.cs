using Microsoft.EntityFrameworkCore;

namespace GitBench.Data;

public partial class AppDbContext
{
    public DbSet<PagesSite> PagesSites => Set<PagesSite>();
    public DbSet<PagesDeployment> PagesDeployments => Set<PagesDeployment>();

    private static void ConfigurePages(ModelBuilder b)
    {
        b.Entity<PagesSite>().HasIndex(s => s.RepositoryId).IsUnique();
        b.Entity<PagesSite>().HasOne(s => s.Repository).WithMany().HasForeignKey(s => s.RepositoryId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<PagesDeployment>().HasIndex(d => new { d.SiteId, d.Slot, d.Version }).IsUnique();
        b.Entity<PagesDeployment>().HasIndex(d => new { d.SiteId, d.Slot, d.IsCurrent });
        b.Entity<PagesDeployment>().HasIndex(d => d.ExpiresAt);
        b.Entity<PagesDeployment>().HasOne(d => d.Site).WithMany(s => s.Deployments).HasForeignKey(d => d.SiteId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<PagesDeployment>().HasOne(d => d.DeployedBy).WithMany().HasForeignKey(d => d.DeployedById).OnDelete(DeleteBehavior.SetNull);
    }
}
