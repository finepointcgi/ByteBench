using GitBench.Data;
using Microsoft.EntityFrameworkCore;

namespace GitBench.Services.ServiceDesk;

/// <summary>
/// The "Service Desk" system account that creates issues and posts requesters' replies (the requester is recorded on the
/// ticket and comment). Like <see cref="Automation.AutomationBot"/> it has no password and is locked out, so it can't sign in.
/// </summary>
public static class ServiceDeskBot
{
    public const string UserName = "gitbench-service-desk";
    public const string DisplayName = "Service Desk";

    public static bool IsBot(User? u) => u?.UserName == UserName;

    /// <summary>Creates the bot account if it doesn't exist and returns its id. Idempotent.</summary>
    public static async Task<int> EnsureAsync(AppDbContext db, CancellationToken ct = default)
    {
        var normalized = UserName.ToUpperInvariant();
        var id = await db.Users.Where(u => u.NormalizedUserName == normalized).Select(u => (int?)u.Id).FirstOrDefaultAsync(ct);
        if (id is { } existing) return existing;

        const string email = "gitbench-service-desk@gitbench.local";
        var bot = new User
        {
            UserName = UserName, NormalizedUserName = normalized,
            Email = email, NormalizedEmail = email.ToUpperInvariant(), EmailConfirmed = true,
            PasswordHash = null,
            LockoutEnabled = true, LockoutEnd = DateTimeOffset.MaxValue,
            SecurityStamp = Guid.NewGuid().ToString(),
            DisplayName = DisplayName, Color = "#0d9488",
        };
        db.Users.Add(bot);
        try
        {
            await db.SaveChangesAsync(ct);
            return bot.Id;
        }
        catch (DbUpdateException)
        {
            db.Entry(bot).State = EntityState.Detached;
            return await db.Users.Where(u => u.NormalizedUserName == normalized).Select(u => u.Id).FirstAsync(ct);
        }
    }
}
