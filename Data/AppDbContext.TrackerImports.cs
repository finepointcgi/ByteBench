using Microsoft.EntityFrameworkCore;

namespace ByteBench.Data;

public partial class AppDbContext
{
    public DbSet<TrackerImportJob> TrackerImportJobs => Set<TrackerImportJob>();
    public DbSet<ImportedItem> ImportedItems => Set<ImportedItem>();

    private static void ConfigureTrackerImports(ModelBuilder b)
    {
        b.Entity<TrackerImportJob>().HasIndex(i => new { i.ProjectId, i.Status });
        b.Entity<TrackerImportJob>().HasOne(i => i.Project).WithMany().HasForeignKey(i => i.ProjectId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<TrackerImportJob>().HasOne(i => i.RequestedBy).WithMany().HasForeignKey(i => i.RequestedById).OnDelete(DeleteBehavior.SetNull);
        b.Entity<ImportedItem>().HasIndex(i => new { i.ProjectId, i.SourceKey, i.Kind, i.ExternalId }).IsUnique();
        b.Entity<ImportedItem>().HasOne<Project>().WithMany().HasForeignKey(i => i.ProjectId).OnDelete(DeleteBehavior.Cascade);
    }
}
