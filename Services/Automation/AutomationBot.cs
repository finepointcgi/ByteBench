using ByteBench.Data;
using Microsoft.EntityFrameworkCore;

namespace ByteBench.Services.Automation;

/// <summary>
/// The "ByteBench Automation" bot account that automation rules act as (see <see cref="AutomationEngine"/>). It has no password hash, so it can't sign in
/// (web or Git), and is locked out permanently as a second guard.
/// </summary>
public static class AutomationBot
{
    public const string UserName = "bytebench-automation";
    public const string DisplayName = "ByteBench Automation";

    public static bool IsBot(User? u) => u?.UserName == UserName;

    /// <summary>Creates the bot account if it doesn't exist and returns its id. Idempotent.</summary>
    public static async Task<int> EnsureAsync(AppDbContext db, CancellationToken ct = default)
    {
        var normalized = UserName.ToUpperInvariant();
        var id = await db.Users.Where(u => u.NormalizedUserName == normalized).Select(u => (int?)u.Id).FirstOrDefaultAsync(ct);
        if (id is { } existing) return existing;

        const string email = "bytebench-automation@bytebench.local";
        var bot = new User
        {
            UserName = UserName, NormalizedUserName = normalized,
            Email = email, NormalizedEmail = email.ToUpperInvariant(), EmailConfirmed = true,
            PasswordHash = null, // no usable password
            LockoutEnabled = true, LockoutEnd = DateTimeOffset.MaxValue,
            SecurityStamp = Guid.NewGuid().ToString(),
            DisplayName = DisplayName, Color = "#0891b2",
        };
        db.Users.Add(bot);
        try
        {
            await db.SaveChangesAsync(ct);
            return bot.Id;
        }
        catch (DbUpdateException)
        {
            // Lost a race with another caller: the account exists now.
            db.Entry(bot).State = EntityState.Detached;
            return await db.Users.Where(u => u.NormalizedUserName == normalized).Select(u => u.Id).FirstAsync(ct);
        }
    }
}
