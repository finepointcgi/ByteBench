using Microsoft.EntityFrameworkCore;

namespace ByteBench.Data;

public partial class AppDbContext
{
    public DbSet<ScimGroup> ScimGroups => Set<ScimGroup>();

    private static void ConfigureScimGroups(ModelBuilder b)
    {
        b.Entity<ScimGroup>().HasIndex(g => g.GroupId).IsUnique();
        b.Entity<ScimGroup>().HasIndex(g => g.NormalizedDisplayName).IsUnique();
        b.Entity<ScimGroup>().HasIndex(g => g.ExternalId);
        b.Entity<ScimGroup>().HasOne(g => g.Group).WithMany().HasForeignKey(g => g.GroupId).OnDelete(DeleteBehavior.Cascade);
    }
}
