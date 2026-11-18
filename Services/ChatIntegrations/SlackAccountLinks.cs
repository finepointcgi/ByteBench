using System.Security.Cryptography;
using GitBench.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace GitBench.Services.ChatIntegrations;

/// <summary>A decoded account-link token: which Slack user asked to be linked.</summary>
public sealed record SlackLinkRequest(string TeamId, string SlackUserId, string? SlackUserName, string Nonce, DateTimeOffset Expires);

/// <summary>
/// Maps Slack users to GitBench users. A Slack user who isn't linked gets a link to <c>/integrations/slack/link?t=…</c>:
/// the token is signed and encrypted with Data Protection, expires after <see cref="TokenLifetime"/>, and carries a
/// nonce that is stored with the link, so each token links at most once. The signed-in GitBench user confirms on that
/// page. Until linked, slash commands, buttons and unfurls do nothing for that Slack user: every action runs as the linked
/// user through <see cref="ProjectAccess"/>.
/// </summary>
public sealed class SlackAccountLinks(IDbContextFactory<AppDbContext> dbFactory, IDataProtectionProvider provider, Audit.AuditLog audit)
{
    public const string Provider = "slack";
    public const string Purpose = "GitBench.Chat.SlackLink.v1";
    public static readonly TimeSpan TokenLifetime = TimeSpan.FromMinutes(10);

    private readonly ITimeLimitedDataProtector protector = provider.CreateProtector(Purpose).ToTimeLimitedDataProtector();

    public string CreateToken(string teamId, string slackUserId, string? slackUserName)
    {
        var nonce = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        var payload = string.Join('\n', teamId, slackUserId, slackUserName ?? "", nonce);
        return protector.Protect(payload, TokenLifetime);
    }

    public string LinkUrl(string baseUrl, string teamId, string slackUserId, string? slackUserName) =>
        $"{baseUrl.TrimEnd('/')}/integrations/slack/link?t={Uri.EscapeDataString(CreateToken(teamId, slackUserId, slackUserName))}";

    /// <summary>The request in <paramref name="token"/>, or null if it was tampered with or has expired.</summary>
    public SlackLinkRequest? Read(string? token)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 2000) return null;
        try
        {
            var parts = protector.Unprotect(token, out var expires).Split('\n');
            return parts.Length == 4 ? new SlackLinkRequest(parts[0], parts[1], parts[2].Length == 0 ? null : parts[2], parts[3], expires) : null;
        }
        catch (CryptographicException) { return null; }
        catch (FormatException) { return null; }
    }

    /// <summary>Links the Slack user in <paramref name="token"/> to <paramref name="userId"/>. Throws with a message for the page if it can't.</summary>
    public async Task<ChatUserLink> LinkAsync(string token, int userId)
    {
        var request = Read(token) ?? throw new InvalidOperationException("This link is invalid or has expired. Run /gitbench link in Slack to get a new one.");
        await using var db = await dbFactory.CreateDbContextAsync();
        if (await db.ChatUserLinks.AnyAsync(l => l.Nonce == request.Nonce))
            throw new InvalidOperationException("This link has already been used. Run /gitbench link in Slack to get a new one.");
        if (await db.ChatUserLinks.AnyAsync(l => l.Provider == Provider && l.TeamId == request.TeamId && l.ExternalUserId == request.SlackUserId && l.RevokedAt == null))
            throw new InvalidOperationException("This Slack account is already linked to a GitBench account. Run /gitbench unlink in Slack first.");
        var link = new ChatUserLink
        {
            Provider = Provider, TeamId = request.TeamId, ExternalUserId = request.SlackUserId,
            ExternalUserName = request.SlackUserName is { Length: > 100 } n ? n[..100] : request.SlackUserName,
            UserId = userId, Nonce = request.Nonce, CreatedAt = DateTime.UtcNow,
        };
        db.ChatUserLinks.Add(link);
        try { await db.SaveChangesAsync(); }
        catch (DbUpdateException) { throw new InvalidOperationException("This link has already been used. Run /gitbench link in Slack to get a new one."); }
        await audit.RecordAsync("chat.slack_linked", "user", userId, details: new { team = request.TeamId, slackUser = request.SlackUserId }, actorId: userId);
        return link;
    }

    /// <summary>The GitBench user linked to this Slack user, if any (and not deactivated).</summary>
    public async Task<int?> FindUserAsync(string? teamId, string? slackUserId)
    {
        if (string.IsNullOrEmpty(teamId) || string.IsNullOrEmpty(slackUserId)) return null;
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.ChatUserLinks.Where(l => l.Provider == Provider && l.TeamId == teamId && l.ExternalUserId == slackUserId
                && l.RevokedAt == null && l.User.DeactivatedAt == null)
            .Select(l => (int?)l.UserId).FirstOrDefaultAsync();
    }

    public async Task<List<ChatUserLink>> ListForUserAsync(int userId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.ChatUserLinks.AsNoTracking().Where(l => l.UserId == userId && l.RevokedAt == null).OrderBy(l => l.CreatedAt).ToListAsync();
    }

    /// <summary>Unlinks from Slack (<c>/gitbench unlink</c>).</summary>
    public async Task<bool> UnlinkAsync(string teamId, string slackUserId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var link = await db.ChatUserLinks.FirstOrDefaultAsync(l => l.Provider == Provider && l.TeamId == teamId && l.ExternalUserId == slackUserId && l.RevokedAt == null);
        if (link == null) return false;
        link.RevokedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        await audit.RecordAsync("chat.slack_unlinked", "user", link.UserId, details: new { team = teamId, slackUser = slackUserId }, actorId: link.UserId);
        return true;
    }

    /// <summary>Unlinks from GitBench (the user's own preferences page).</summary>
    public async Task UnlinkForUserAsync(int userId, int linkId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var link = await db.ChatUserLinks.FirstOrDefaultAsync(l => l.Id == linkId && l.UserId == userId && l.RevokedAt == null);
        if (link == null) return;
        link.RevokedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        await audit.RecordAsync("chat.slack_unlinked", "user", userId, details: new { team = link.TeamId, slackUser = link.ExternalUserId }, actorId: userId);
    }
}
