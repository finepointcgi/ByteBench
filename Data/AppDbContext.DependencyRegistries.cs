using Microsoft.EntityFrameworkCore;

namespace ByteBench.Data;

public partial class AppDbContext
{
    public DbSet<DependencyBotRegistry> DependencyBotRegistries => Set<DependencyBotRegistry>();

    private static void ConfigureDependencyRegistries(ModelBuilder b)
    {
        b.Entity<DependencyBotRegistry>().HasIndex(r => new { r.RepositoryId, r.Ecosystem, r.Scope }).IsUnique();
        b.Entity<DependencyBotRegistry>().HasOne<Repository>().WithMany().HasForeignKey(r => r.RepositoryId).OnDelete(DeleteBehavior.Cascade);
    }
}
