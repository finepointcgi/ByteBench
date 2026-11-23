using ByteBench.Data;
using Microsoft.EntityFrameworkCore;

namespace ByteBench.Services.Accounts;

/// <summary>Reads and writes <see cref="SiteSetting"/> rows (values an admin changes at runtime, no restart).</summary>
public sealed class SiteSettings(IDbContextFactory<AppDbContext> dbFactory)
{
    public async Task<string?> GetAsync(string key, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.SiteSettings.AsNoTracking().Where(s => s.Key == key).Select(s => s.Value).FirstOrDefaultAsync(ct);
    }

    public async Task SetAsync(string key, string value, int? userId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var row = await db.SiteSettings.FirstOrDefaultAsync(s => s.Key == key, ct);
        if (row == null) db.SiteSettings.Add(new SiteSetting { Key = key, Value = value, UpdatedById = userId });
        else { row.Value = value; row.UpdatedAt = DateTime.UtcNow; row.UpdatedById = userId; }
        await db.SaveChangesAsync(ct);
    }
}
