using Microsoft.EntityFrameworkCore;

namespace ByteBench.Data;

public partial class AppDbContext
{
    public DbSet<RepositoryInsight> RepositoryInsights => Set<RepositoryInsight>();
    public DbSet<DependencyBotSettings> DependencyBotSettings => Set<DependencyBotSettings>();
    public DbSet<DependencyBotPullRequest> DependencyBotPullRequests => Set<DependencyBotPullRequest>();

    private static void ConfigureInsights(ModelBuilder b)
    {
        b.Entity<RepositoryInsight>().HasIndex(i => i.RepositoryId).IsUnique();
        b.Entity<RepositoryInsight>().HasOne(i => i.Repository).WithMany().HasForeignKey(i => i.RepositoryId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<DependencyBotSettings>().HasIndex(s => s.RepositoryId).IsUnique();
        b.Entity<DependencyBotSettings>().HasIndex(s => new { s.Enabled, s.NextRunAt });
        b.Entity<DependencyBotSettings>().HasOne(s => s.Repository).WithMany().HasForeignKey(s => s.RepositoryId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<DependencyBotPullRequest>().HasIndex(p => new { p.RepositoryId, p.Ecosystem, p.Package });
        b.Entity<DependencyBotPullRequest>().HasOne<Repository>().WithMany().HasForeignKey(p => p.RepositoryId).OnDelete(DeleteBehavior.Cascade);
    }
}
