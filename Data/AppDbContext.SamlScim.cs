using Microsoft.EntityFrameworkCore;

namespace ByteBench.Data;

public partial class AppDbContext
{
    public DbSet<SamlMessageRecord> SamlMessages => Set<SamlMessageRecord>();
    public DbSet<ScimToken> ScimTokens => Set<ScimToken>();
    public DbSet<ScimIdentity> ScimIdentities => Set<ScimIdentity>();

    private static void ConfigureSamlScim(ModelBuilder b)
    {
        b.Entity<SamlMessageRecord>().HasIndex(m => m.ExpiresAt);
        b.Entity<ScimToken>().HasIndex(t => t.TokenHash).IsUnique();
        b.Entity<ScimIdentity>().HasIndex(i => i.NormalizedUserName).IsUnique();
        b.Entity<ScimIdentity>().HasIndex(i => i.ExternalId);
        b.Entity<ScimIdentity>().HasOne(i => i.User).WithMany().HasForeignKey(i => i.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}
