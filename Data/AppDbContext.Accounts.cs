using Microsoft.EntityFrameworkCore;

namespace ByteBench.Data;

public partial class AppDbContext
{
    public DbSet<SiteSetting> SiteSettings => Set<SiteSetting>();
    public DbSet<RegistrationInvite> RegistrationInvites => Set<RegistrationInvite>();

    private static void ConfigureAccounts(ModelBuilder b)
    {
        b.Entity<RegistrationInvite>().HasIndex(i => i.TokenHash).IsUnique();
    }
}
