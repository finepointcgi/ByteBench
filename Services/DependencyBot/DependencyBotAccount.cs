using ByteBench.Data;
using Microsoft.EntityFrameworkCore;

namespace ByteBench.Services.DependencyBot;

/// <summary>
/// The "ByteBench dependency bot" account that dependency update pull requests are opened by. Like the other service accounts
/// (<see cref="Automation.AutomationBot"/>) it has no password and is locked out, so it can't sign in (web or Git).
/// </summary>
public static class DependencyBotAccount
{
    public const string UserName = "bytebench-dependencies";
    public const string DisplayName = "ByteBench dependency bot";
    public const string Email = "bytebench-dependencies@bytebench.local";

    public static bool IsBot(User? u) => u?.UserName == UserName;

    /// <summary>Creates the bot account if it doesn't exist and returns its id. Idempotent.</summary>
    public static async Task<int> EnsureAsync(AppDbContext db, CancellationToken ct = default)
    {
        var normalized = UserName.ToUpperInvariant();
        var id = await db.Users.Where(u => u.NormalizedUserName == normalized).Select(u => (int?)u.Id).FirstOrDefaultAsync(ct);
        if (id is { } existing) return existing;

        var bot = new User
        {
            UserName = UserName, NormalizedUserName = normalized,
            Email = Email, NormalizedEmail = Email.ToUpperInvariant(), EmailConfirmed = true,
            PasswordHash = null,
            LockoutEnabled = true, LockoutEnd = DateTimeOffset.MaxValue,
            SecurityStamp = Guid.NewGuid().ToString(),
            DisplayName = DisplayName, Color = "#7c3aed",
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
