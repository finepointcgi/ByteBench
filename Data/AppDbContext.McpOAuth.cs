using Microsoft.EntityFrameworkCore;

namespace ByteBench.Data;

public partial class AppDbContext
{
    public DbSet<OAuthClient> OAuthClients => Set<OAuthClient>();
    public DbSet<OAuthGrant> OAuthGrants => Set<OAuthGrant>();
    public DbSet<OAuthCode> OAuthCodes => Set<OAuthCode>();
    public DbSet<OAuthToken> OAuthTokens => Set<OAuthToken>();

    private static void ConfigureMcpOAuth(ModelBuilder b)
    {
        b.Entity<OAuthClient>().HasIndex(c => c.ClientId).IsUnique();
        b.Entity<OAuthClient>().HasIndex(c => c.CreatedAt);
        b.Entity<OAuthGrant>().HasIndex(g => new { g.UserId, g.RevokedAt });
        b.Entity<OAuthGrant>().HasOne(g => g.Client).WithMany().HasForeignKey(g => g.ClientId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<OAuthGrant>().HasOne(g => g.User).WithMany().HasForeignKey(g => g.UserId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<OAuthCode>().HasIndex(c => c.CodeHash).IsUnique();
        b.Entity<OAuthCode>().HasIndex(c => c.ExpiresAt);
        b.Entity<OAuthCode>().HasOne<OAuthClient>().WithMany().HasForeignKey(c => c.ClientId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<OAuthCode>().HasOne<User>().WithMany().HasForeignKey(c => c.UserId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<OAuthCode>().HasOne<OAuthGrant>().WithMany().HasForeignKey(c => c.GrantId).OnDelete(DeleteBehavior.SetNull);
        b.Entity<OAuthToken>().HasIndex(t => t.TokenHash).IsUnique();
        b.Entity<OAuthToken>().HasIndex(t => t.ExpiresAt);
        b.Entity<OAuthToken>().HasOne(t => t.Grant).WithMany().HasForeignKey(t => t.GrantId).OnDelete(DeleteBehavior.Cascade);
    }
}
