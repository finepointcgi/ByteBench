using ByteBench.Data;

namespace ByteBench.Services.Push;

/// <summary>The <c>Push</c> configuration section (browser push notifications, see <c>site/pages/notifications.html#web-push</c>).</summary>
public sealed class PushOptions
{
    /// <summary>Master switch. When false the preferences page hides browser push and pending pushes are dropped.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// The VAPID <c>sub</c> claim (RFC 8292): a <c>mailto:</c> or <c>https:</c> URL push services can use to contact the
    /// operator. Default: <c>PublicBaseUrl</c> when it is https, else <c>mailto:</c> the first site admin's email.
    /// </summary>
    public string? Subject { get; set; }

    /// <summary>
    /// When set, subscriptions are accepted only for push service hosts equal to or ending with one of these
    /// (e.g. <c>push.services.mozilla.com</c>, <c>fcm.googleapis.com</c>, <c>push.apple.com</c>, <c>notify.windows.com</c>).
    /// Empty (default): any public https host.
    /// </summary>
    public string[] AllowedHostSuffixes { get; set; } = [];

    /// <summary>Allow push endpoints on private, loopback and link-local addresses (and plain http on loopback). Null: follow <c>Webhooks:AllowPrivateNetworks</c>.</summary>
    public bool? AllowPrivateNetworks { get; set; }

    /// <summary>How long (seconds) a push service keeps an undelivered message (the <c>TTL</c> header).</summary>
    public int TtlSeconds { get; set; } = 86400;

    /// <summary>Run the background delivery worker (tests turn it off and drive <see cref="PushWorker.ProcessAsync"/> themselves).</summary>
    public bool DeliveryWorker { get; set; } = true;
}

/// <summary>The per-user set of <see cref="NotificationGroup"/>s sent as pushes, stored as a bit mask.</summary>
public static class PushGroupMask
{
    /// <summary>Default groups: the ones that email by default (assigned, mentions, review requests).</summary>
    public static readonly IReadOnlySet<NotificationGroup> Defaults = NotificationPreferences.Groups
        .Where(g => g.Default == NotificationChannel.InAppAndEmail).Select(g => g.Group).ToHashSet();

    public static IReadOnlySet<NotificationGroup> ToSet(int? mask) => mask is not { } m ? Defaults
        : Enum.GetValues<NotificationGroup>().Where(g => (m & (1 << (int)g)) != 0).ToHashSet();

    public static int ToMask(IEnumerable<NotificationGroup> groups) => groups.Aggregate(0, (m, g) => m | (1 << (int)g));
}
