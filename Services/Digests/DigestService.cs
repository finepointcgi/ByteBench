using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using ByteBench.Data;
using ByteBench.Services.Email;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ByteBench.Services.Digests;

/// <summary>A user's digest settings as the preferences page edits them.</summary>
/// <param name="TimeZoneId">The stored IANA zone, or null when the user never chose one (UTC is used).</param>
public sealed record DigestSettings(DigestFrequency Frequency, int Hour, DayOfWeek Weekday, string? TimeZoneId, DateTime? NextDigestAt);

/// <summary>Signed, expiring (90 days) tokens for the digest unsubscribe link: bound to the user id and to this purpose only.</summary>
public sealed class DigestUnsubscribeTokens(IDataProtectionProvider provider)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(90);
    // Stored identifier: keep "GitBench" (the product was renamed to ByteBench). Changing it would make existing encrypted data/tokens unreadable.
    public const string Purpose = "GitBench.Digest.Unsubscribe.v1";

    private readonly ITimeLimitedDataProtector protector = provider.CreateProtector(Purpose).ToTimeLimitedDataProtector();

    public string Create(int userId) => protector.Protect("digest:" + userId.ToString(CultureInfo.InvariantCulture), Lifetime);

    /// <summary>The token's user id, or null when it is forged, expired, for another purpose or malformed.</summary>
    public int? Read(string? token)
    {
        if (string.IsNullOrEmpty(token) || token.Length > 2000) return null;
        try
        {
            var value = protector.Unprotect(token);
            return value.StartsWith("digest:", StringComparison.Ordinal) && int.TryParse(value.AsSpan(7), NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : null;
        }
        catch (CryptographicException) { return null; }
        catch (FormatException) { return null; }
    }
}

/// <summary>In-memory sliding one-hour windows per user for "Send me a preview now" (resets on restart, which is fine).</summary>
public sealed class DigestPreviewLimiter(TimeProvider clock, IOptionsMonitor<DigestOptions> options)
{
    public static readonly TimeSpan Window = TimeSpan.FromHours(1);
    private readonly ConcurrentDictionary<int, Queue<DateTimeOffset>> hits = new();

    public bool TryAcquire(int userId)
    {
        var limit = Math.Max(1, options.CurrentValue.PreviewsPerHour);
        var now = clock.GetUtcNow();
        var q = hits.GetOrAdd(userId, _ => new Queue<DateTimeOffset>());
        lock (q)
        {
            while (q.Count > 0 && now - q.Peek() >= Window) q.Dequeue();
            if (q.Count >= limit) return false;
            q.Enqueue(now);
            return true;
        }
    }
}

public enum DigestPreviewResult { Sent, NothingToReport, RateLimited, EmailDisabled, NoEmailAddress, Failed }

/// <summary>Counts from one <see cref="DigestService.RunOnceAsync"/> pass.</summary>
public sealed record DigestRunResult(int Claimed, int Sent, int Empty, int Failed);

/// <summary>
/// Per-user digest emails (daily or weekly, at a local hour in the user's IANA time zone). Settings, the scheduled pass
/// <see cref="DigestWorker"/> runs, and previews. Every send goes through <see cref="DigestBuilder"/> and the app's
/// <see cref="IEmailSender"/>, and is skipped when there is nothing to report.
/// </summary>
public sealed class DigestService(IDbContextFactory<AppDbContext> dbFactory, IServiceScopeFactory scopes, IEmailSender sender,
    IOptions<EmailOptions> email, IOptionsMonitor<DigestOptions> options, NotificationEmailComposer composer, DigestUnsubscribeTokens tokens,
    DigestPreviewLimiter previews, TimeProvider clock, ILogger<DigestService> log)
{
    /// <summary>A failing send is retried on the next polls this many times, then skipped until the next period.</summary>
    public const int MaxRetries = 3;
    public static readonly TimeSpan LogRetention = TimeSpan.FromDays(30);
    public const string UnsubscribePath = "/account/digest/unsubscribe";

    public bool EmailEnabled => email.Value.Enabled;

    public async Task<DigestSettings> GetSettingsAsync(int userId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var s = await db.UserNotificationSettings.AsNoTracking().FirstOrDefaultAsync(x => x.UserId == userId, ct);
        return new DigestSettings(s?.DigestFrequency ?? DigestFrequency.Off, s?.DigestHour ?? UserNotificationSettings.DefaultDigestHour,
            s?.DigestWeekday ?? DayOfWeek.Monday, s?.TimeZoneId, s?.NextDigestAt);
    }

    /// <summary>Validates and saves the settings and schedules the next digest. Returns null or an error message.</summary>
    public async Task<string?> SaveSettingsAsync(int userId, DigestFrequency frequency, int hour, DayOfWeek weekday, string? timeZoneId, CancellationToken ct = default)
    {
        if (!Enum.IsDefined(frequency)) return "Choose off, daily or weekly.";
        if (hour is < 0 or > 23) return "The hour must be between 0 and 23.";
        if (!Enum.IsDefined(weekday)) return "Choose a weekday.";
        var zoneId = string.IsNullOrWhiteSpace(timeZoneId) ? DigestSchedule.DefaultZone : timeZoneId.Trim();
        if (DigestSchedule.FindZone(zoneId) == null) return $"\"{zoneId}\" isn't an IANA time zone (for example Europe/Berlin or America/New_York).";

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var s = await db.UserNotificationSettings.FindAsync([userId], ct);
        if (s == null) db.UserNotificationSettings.Add(s = new UserNotificationSettings { UserId = userId });
        s.DigestFrequency = frequency;
        s.DigestHour = hour;
        s.DigestWeekday = weekday;
        s.TimeZoneId = zoneId;
        s.DigestFailures = 0;
        s.NextDigestAt = DigestSchedule.Next(s, clock.GetUtcNow().UtcDateTime);
        await db.SaveChangesAsync(ct);
        return null;
    }

    /// <summary>Turns digests off (the unsubscribe link). False when the user doesn't exist.</summary>
    public async Task<bool> DisableAsync(int userId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (!await db.Users.AnyAsync(u => u.Id == userId, ct)) return false;
        await db.UserNotificationSettings.Where(s => s.UserId == userId)
            .ExecuteUpdateAsync(x => x.SetProperty(s => s.DigestFrequency, DigestFrequency.Off).SetProperty(s => s.NextDigestAt, (DateTime?)null)
                .SetProperty(s => s.DigestFailures, 0), ct);
        return true;
    }

    /// <summary>"Send me a preview now": the same builder and sender, at most <see cref="DigestOptions.PreviewsPerHour"/> per hour.</summary>
    public async Task<DigestPreviewResult> SendPreviewAsync(int userId, CancellationToken ct = default)
    {
        if (!email.Value.Enabled) return DigestPreviewResult.EmailDisabled;
        if (!previews.TryAcquire(userId)) return DigestPreviewResult.RateLimited;
        var settings = await GetSettingsAsync(userId, ct);
        var frequency = settings.Frequency == DigestFrequency.Off ? DigestFrequency.Daily : settings.Frequency;
        var (outcome, _) = await BuildAndSendAsync(userId, frequency, clock.GetUtcNow().UtcDateTime, preview: true, ct);
        return outcome;
    }

    /// <summary>
    /// One scheduled pass at <paramref name="nowUtc"/>: every user whose <c>NextDigestAt</c> has passed (at most
    /// <see cref="DigestOptions.MaxPerRun"/>) is claimed with a conditional update that moves <c>NextDigestAt</c> to the next
    /// period and sets <c>LastDigestAt</c>, so concurrent passes (other processes) never send twice. A failed send is put back
    /// for the next poll up to <see cref="MaxRetries"/> times. One user's failure never stops the others.
    /// </summary>
    public async Task<DigestRunResult> RunOnceAsync(DateTime nowUtc, CancellationToken ct = default)
    {
        nowUtc = DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc);
        if (!email.Value.Enabled) return new(0, 0, 0, 0);
        int claimed = 0, sent = 0, empty = 0, failed = 0;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var due = await db.UserNotificationSettings.AsNoTracking()
            .Where(s => s.DigestFrequency != DigestFrequency.Off && s.NextDigestAt != null && s.NextDigestAt <= nowUtc)
            .OrderBy(s => s.NextDigestAt).Take(Math.Max(1, options.CurrentValue.MaxPerRun)).ToListAsync(ct);

        foreach (var s in due)
        {
            var observed = s.NextDigestAt;
            var next = DigestSchedule.Next(s, nowUtc);
            if (await db.UserNotificationSettings.Where(x => x.UserId == s.UserId && x.NextDigestAt == observed && x.DigestFrequency != DigestFrequency.Off)
                    .ExecuteUpdateAsync(x => x.SetProperty(v => v.NextDigestAt, next).SetProperty(v => v.LastDigestAt, nowUtc), ct) == 0)
                continue; // another pass claimed it, or the user changed the settings meanwhile
            claimed++;

            DigestPreviewResult outcome;
            try
            {
                if (!s.EmailEnabled) outcome = DigestPreviewResult.NothingToReport; // all notification emails are off (or unsubscribed)
                else (outcome, _) = await BuildAndSendAsync(s.UserId, s.DigestFrequency, nowUtc, preview: false, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                log.LogError(ex, "Building the digest for user {UserId} failed", s.UserId);
                outcome = DigestPreviewResult.Failed;
            }

            if (outcome == DigestPreviewResult.Failed)
            {
                failed++;
                var failures = s.DigestFailures + 1;
                if (failures <= MaxRetries)
                    await db.UserNotificationSettings.Where(x => x.UserId == s.UserId && x.NextDigestAt == next)
                        .ExecuteUpdateAsync(x => x.SetProperty(v => v.NextDigestAt, nowUtc).SetProperty(v => v.DigestFailures, failures), ct);
                else
                {
                    log.LogWarning("Giving up on this period's digest for user {UserId} after {Attempts} attempts", s.UserId, failures);
                    await db.UserNotificationSettings.Where(x => x.UserId == s.UserId).ExecuteUpdateAsync(x => x.SetProperty(v => v.DigestFailures, 0), ct);
                }
                continue;
            }
            if (s.DigestFailures != 0)
                await db.UserNotificationSettings.Where(x => x.UserId == s.UserId).ExecuteUpdateAsync(x => x.SetProperty(v => v.DigestFailures, 0), ct);
            if (outcome == DigestPreviewResult.Sent) sent++; else empty++;
        }

        await db.DigestLogs.Where(l => l.CreatedAt < nowUtc - LogRetention).ExecuteDeleteAsync(ct);
        return new(claimed, sent, empty, failed);
    }

    private async Task<(DigestPreviewResult, DigestContent?)> BuildAndSendAsync(int userId, DigestFrequency frequency, DateTime nowUtc, bool preview, CancellationToken ct)
    {
        DigestContent? content;
        await using (var scope = scopes.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<ProjectAccess>().ActAs(userId);
            content = await scope.ServiceProvider.GetRequiredService<DigestBuilder>().BuildAsync(userId, frequency, nowUtc, ct);
        }
        if (content == null) return (DigestPreviewResult.NothingToReport, null);
        if (string.IsNullOrWhiteSpace(content.User.Email)) { await LogAsync(userId, preview, content, false, false, nowUtc, ct); return (DigestPreviewResult.NoEmailAddress, content); }
        if (content.IsEmpty) { await LogAsync(userId, preview, content, false, false, nowUtc, ct); return (DigestPreviewResult.NothingToReport, content); }

        var unsubscribe = composer.Absolute(UnsubscribePath + "?token=" + Uri.EscapeDataString(tokens.Create(userId)));
        var message = DigestBuilder.Compose(content, composer.Absolute, unsubscribe);
        try
        {
            await sender.SendAsync(message, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Sending the digest to {To} failed", EmailWorker.Mask(content.User.Email!));
            await LogAsync(userId, preview, content, false, true, nowUtc, ct);
            return (DigestPreviewResult.Failed, content);
        }
        await LogAsync(userId, preview, content, true, false, nowUtc, ct);
        log.LogInformation("Sent a {Kind} digest to {To}", preview ? "preview" : frequency.ToString().ToLowerInvariant(), EmailWorker.Mask(content.User.Email!));
        return (DigestPreviewResult.Sent, content);
    }

    private async Task LogAsync(int userId, bool preview, DigestContent c, bool sent, bool failed, DateTime nowUtc, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        db.DigestLogs.Add(new DigestLog
        {
            UserId = userId, CreatedAt = nowUtc, Preview = preview, Sent = sent, Failed = failed,
            UnreadCount = c.UnreadTotal, MentionCount = c.MentionTotal, DueCount = c.DueTotal, ReviewCount = c.ReviewTotal, MyPullRequestCount = c.MyPullRequestTotal,
        });
        await db.SaveChangesAsync(ct);
    }
}
