using Microsoft.EntityFrameworkCore;

namespace ByteBench.Data;

public partial class AppDbContext
{
    public DbSet<PullRequestAutoMerge> PullRequestAutoMerges => Set<PullRequestAutoMerge>();
    public DbSet<GpgKey> GpgKeys => Set<GpgKey>();
    public DbSet<CommitSignature> CommitSignatures => Set<CommitSignature>();

    private static void ConfigurePrSigning(ModelBuilder b)
    {
        b.Entity<PullRequestAutoMerge>().HasIndex(a => a.PullRequestId).IsUnique();
        b.Entity<PullRequestAutoMerge>().HasIndex(a => a.State);
        b.Entity<PullRequestAutoMerge>().Property(a => a.State).HasConversion<int>();
        b.Entity<PullRequestAutoMerge>().HasOne(a => a.PullRequest).WithMany().HasForeignKey(a => a.PullRequestId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<PullRequestAutoMerge>().HasOne(a => a.ArmedBy).WithMany().HasForeignKey(a => a.ArmedById).OnDelete(DeleteBehavior.Cascade);

        b.Entity<GpgKey>().HasIndex(k => k.Fingerprint).IsUnique();
        b.Entity<GpgKey>().HasIndex(k => k.UserId);
        b.Entity<GpgKey>().HasOne(k => k.User).WithMany().HasForeignKey(k => k.UserId).OnDelete(DeleteBehavior.Cascade);

        b.Entity<CommitSignature>().HasIndex(s => s.Sha).IsUnique();
        b.Entity<CommitSignature>().Property(s => s.Status).HasConversion<int>();
    }
}
