using Microsoft.EntityFrameworkCore;

namespace ByteBench.Data;

public partial class AppDbContext
{
    public DbSet<DigestLog> DigestLogs => Set<DigestLog>();
    public DbSet<WebPushSubscription> WebPushSubscriptions => Set<WebPushSubscription>();

    /// <summary>Digest emails and browser push (Services/Digests, Services/Push).</summary>
    private static void ConfigureNotificationDelivery(ModelBuilder b)
    {
        b.Entity<UserNotificationSettings>().HasIndex(s => s.NextDigestAt);
        b.Entity<DigestLog>().HasIndex(l => l.CreatedAt);
        b.Entity<DigestLog>().HasIndex(l => l.UserId);
        b.Entity<DigestLog>().HasOne<User>().WithMany().HasForeignKey(l => l.UserId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<WebPushSubscription>().HasIndex(s => s.EndpointHash).IsUnique();
        b.Entity<WebPushSubscription>().HasIndex(s => s.UserId);
        b.Entity<WebPushSubscription>().HasOne<User>().WithMany().HasForeignKey(s => s.UserId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<Notification>().HasIndex(n => n.PushPending);
    }
}
