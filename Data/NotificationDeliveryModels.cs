using System.ComponentModel.DataAnnotations;

namespace ByteBench.Data;

/// <summary>How often a user gets the summary email (<c>Services/Digests</c>). Off by default.</summary>
public enum DigestFrequency { Off = 0, Daily = 1, Weekly = 2 }

public partial class UserNotificationSettings
{
    public DigestFrequency DigestFrequency { get; set; }
    /// <summary>Local hour (0-23) the digest goes out; null means <see cref="DefaultDigestHour"/>.</summary>
    public int? DigestHour { get; set; }
    /// <summary>Weekly digests: the local day; null means Monday.</summary>
    public DayOfWeek? DigestWeekday { get; set; }
    /// <summary>The user's IANA time zone (e.g. <c>Europe/Berlin</c>); null means UTC. Used to schedule digests.</summary>
    [MaxLength(64)] public string? TimeZoneId { get; set; }
    /// <summary>UTC time the next digest is due (null while digests are off). Claimed atomically by <c>DigestWorker</c>.</summary>
    public DateTime? NextDigestAt { get; set; }
    /// <summary>UTC time a digest run last claimed this user (sent or found nothing to send).</summary>
    public DateTime? LastDigestAt { get; set; }
    /// <summary>Consecutive failed digest sends in the current period (retried on the next poll up to 3 times).</summary>
    public int DigestFailures { get; set; }

    /// <summary>Send browser push notifications to this user's subscribed browsers.</summary>
    public bool PushEnabled { get; set; }
    /// <summary>Bit mask of <see cref="NotificationGroup"/>s sent as pushes (bit = 1 &lt;&lt; group); null means the default groups.</summary>
    public int? PushGroups { get; set; }

    public const int DefaultDigestHour = 8;
}

public partial class Notification
{
    /// <summary>True while this notification still has to be sent as a browser push (<c>Services/Push/PushWorker</c>).</summary>
    public bool PushPending { get; set; }
}

/// <summary>One digest run for one user: counts only (no titles or links), for debugging. Pruned after 30 days.</summary>
public class DigestLog
{
    public long Id { get; set; }
    public int UserId { get; set; }
    public DateTime CreatedAt { get; set; }
    /// <summary>"Send me a preview now" rather than the schedule.</summary>
    public bool Preview { get; set; }
    /// <summary>An email went out (false: nothing to report, or the send failed).</summary>
    public bool Sent { get; set; }
    public bool Failed { get; set; }
    public int UnreadCount { get; set; }
    public int MentionCount { get; set; }
    public int DueCount { get; set; }
    public int ReviewCount { get; set; }
    public int MyPullRequestCount { get; set; }
}

/// <summary>
/// A browser's Web Push subscription (RFC 8030). The endpoint is the push service URL the browser handed out; the
/// <c>p256dh</c> and <c>auth</c> keys are encrypted with Data Protection. At most 10 per user.
/// </summary>
public class WebPushSubscription
{
    public int Id { get; set; }
    public int UserId { get; set; }
    [Required, MaxLength(2000)] public string Endpoint { get; set; } = "";
    /// <summary>SHA-256 (hex) of <see cref="Endpoint"/>: unique, so one browser belongs to one account.</summary>
    [Required, MaxLength(64)] public string EndpointHash { get; set; } = "";
    [Required, MaxLength(1000)] public string EncryptedP256dh { get; set; } = "";
    [Required, MaxLength(1000)] public string EncryptedAuth { get; set; } = "";
    /// <summary>"Firefox on Windows": a short label for the device list.</summary>
    [MaxLength(60)] public string? UserAgent { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? LastUsedAt { get; set; }
    /// <summary>Consecutive failed deliveries; the subscription is removed at 5.</summary>
    public int FailureCount { get; set; }
    /// <summary>The push service answered 429: nothing is sent to this subscription before this UTC time.</summary>
    public DateTime? RetryAfterUntil { get; set; }
}
