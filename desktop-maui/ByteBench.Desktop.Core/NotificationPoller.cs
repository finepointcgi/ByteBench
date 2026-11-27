using System.Net;
using System.Net.Http.Headers;

namespace ByteBench.Desktop.Core;

public static class NotificationLogic
{
    public const int BaseDelayMs = 30_000;
    public const int MaxDelayMs = 300_000;

    /// <summary>Back-off for polling after failures: 30s normally, doubling to at most 5 minutes.</summary>
    public static TimeSpan NextPollDelay(int consecutiveFailures)
    {
        var shift = Math.Clamp(consecutiveFailures, 0, 4);
        return TimeSpan.FromMilliseconds(Math.Min((double)BaseDelayMs * (1 << shift), MaxDelayMs));
    }

    /// <summary>
    /// Which notifications to announce. A null <paramref name="lastNotifiedId"/> means this profile was never polled: nothing is
    /// announced (the first poll only records where we are) so adding a server does not produce a burst of old notifications.
    /// <c>Newest</c> is the id to record, or null when nothing changed.
    /// </summary>
    public static (IReadOnlyList<ApiNotification> Show, int More, int? Newest) SelectToAnnounce(IReadOnlyList<ApiNotification> items, int? lastNotifiedId, int max = 5)
    {
        int? newest = items.Count == 0 ? null : items.Max(n => n.Id);
        if (lastNotifiedId is not { } last) return ([], 0, newest ?? 0);
        var fresh = items.Where(n => !n.IsRead && n.Id > last).OrderBy(n => n.Id).ToList();
        var show = fresh.Skip(Math.Max(0, fresh.Count - max)).ToList();
        return (show, Math.Max(0, fresh.Count - max), newest is { } nw && nw > last ? nw : null);
    }

    /// <summary>The path to open for a notification, or "/notifications" if the server sent something that is not a safe server path.</summary>
    public static string PathForNotification(ApiNotification n, string origin) =>
        DeepLink.IsSafePath(n.Url, origin) ? n.Url : "/notifications";
}

public enum PollStatus { Ok, NoToken, Unsupported, Unauthorized, Failed, Disabled }

public sealed record PollOutcome(PollStatus Status, int Unread, IReadOnlyList<ApiNotification> Announced, int More, string? Message);

/// <summary>
/// Polls <c>GET /api/v1/notifications</c> of every saved server (about every 30 s, backing off to 5 min while a server is down or
/// refusing). It authenticates with the server's API token only: a WebView's cookie session is not readable from here, so a
/// server without a stored token is not polled. A server without <c>GET /api/v1/server-info</c> (an older ByteBench) is skipped.
/// The poller raises events; the app turns them into OS notifications and badge/tray updates.
/// </summary>
public sealed class NotificationPoller
{
    private sealed class State
    {
        public int Failures;
        public DateTimeOffset Due = DateTimeOffset.MinValue;
        public int Unread;
        public bool Supported;
    }

    private readonly ProfileStore _store;
    private readonly ITokenStore _tokens;
    private readonly HttpClient _http;
    private readonly TimeProvider _time;
    private readonly Dictionary<string, State> _states = [];
    private readonly object _gate = new();

    public NotificationPoller(ProfileStore store, ITokenStore tokens, HttpClient http, TimeProvider? time = null)
    {
        _store = store;
        _tokens = tokens;
        _http = http;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>A client that never follows redirects (a server must not bounce an API call, and its token, to another origin).</summary>
    public static HttpClient CreateDefaultClient() =>
        new(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false }) { Timeout = TimeSpan.FromSeconds(10) };

    /// <summary>A notification to show: the profile, the notification, and the server-relative path to open when it is clicked.</summary>
    public event Action<ServerProfile, ApiNotification, string>? NotificationAnnounced;
    /// <summary>More new notifications than the per-poll limit: (profile, number not individually announced).</summary>
    public event Action<ServerProfile, int>? MoreAnnounced;
    /// <summary>The unread count of a profile changed: (profile id, unread, total over all profiles).</summary>
    public event Action<string, int, int>? UnreadChanged;

    public int UnreadFor(string profileId) { lock (_gate) return _states.TryGetValue(profileId, out var s) ? s.Unread : 0; }
    public int TotalUnread { get { lock (_gate) return _states.Values.Sum(s => s.Unread); } }

    /// <summary>Forgets what is known about a profile (call when a server is removed).</summary>
    public void Forget(string profileId)
    {
        lock (_gate) _states.Remove(profileId);
        UnreadChanged?.Invoke(profileId, 0, TotalUnread);
    }

    /// <summary>Makes every profile due now (the "check now" menu item).</summary>
    public void PollSoon() { lock (_gate) foreach (var s in _states.Values) s.Due = DateTimeOffset.MinValue; }

    public async Task<PollOutcome> PollOnceAsync(ServerProfile profile, CancellationToken ct = default)
    {
        State st;
        lock (_gate) st = _states.TryGetValue(profile.Id, out var s) ? s : _states[profile.Id] = new State();

        var outcome = await PollCoreAsync(profile, st, ct).ConfigureAwait(false);

        lock (_gate)
        {
            var now = _time.GetUtcNow();
            switch (outcome.Status)
            {
                case PollStatus.Ok:
                    st.Failures = 0;
                    st.Due = now + NotificationLogic.NextPollDelay(0);
                    break;
                case PollStatus.Failed:
                case PollStatus.Unauthorized:
                    st.Failures++;
                    st.Due = now + NotificationLogic.NextPollDelay(st.Failures);
                    break;
                default: // no token, unsupported server, disabled: nothing to do until the user changes something; re-check slowly
                    st.Due = now + TimeSpan.FromMilliseconds(NotificationLogic.MaxDelayMs);
                    break;
            }
        }
        return outcome;
    }

    private async Task<PollOutcome> PollCoreAsync(ServerProfile profile, State st, CancellationToken ct)
    {
        if (!_store.Settings().NotificationsEnabled) { SetUnread(profile.Id, st, 0); return new(PollStatus.Disabled, 0, [], 0, null); }

        var token = await _tokens.GetAsync(profile.Id, ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(token)) { SetUnread(profile.Id, st, 0); return new(PollStatus.NoToken, 0, [], 0, "Save an API token for this server to get notifications."); }

        bool supported; lock (_gate) supported = st.Supported;
        if (!supported)
        {
            var info = await GetAsync(profile, "/api/v1/server-info", null, ct).ConfigureAwait(false);
            var compat = ServerApi.EvaluateServerInfo(info.Status, info.Body);
            if (compat.Level == CompatLevel.Unavailable) return new(PollStatus.Failed, 0, [], 0, compat.Message);
            if (!compat.SupportsNotifications) { SetUnread(profile.Id, st, 0); return new(PollStatus.Unsupported, 0, [], 0, compat.Message); }
            lock (_gate) st.Supported = true;
        }

        var query = "/api/v1/notifications?unread=true&limit=50";
        if (profile.LastNotifiedId is { } after) query += $"&afterId={after}";
        var res = await GetAsync(profile, query, token, ct).ConfigureAwait(false);
        if (res.Status is 401 or 403) return new(PollStatus.Unauthorized, UnreadFor(profile.Id), [], 0, "The server refused the saved API token.");
        if (res.Status != 200) return new(PollStatus.Failed, UnreadFor(profile.Id), [], 0, res.Status is null ? "The server could not be reached." : $"HTTP {res.Status}");
        var list = ServerApi.ParseNotificationList(res.Body);
        if (list is null) return new(PollStatus.Failed, UnreadFor(profile.Id), [], 0, "The server's answer was not understood.");

        // Re-read the profile: LastNotifiedId may have moved since the caller took its copy.
        var current = _store.Get(profile.Id);
        if (current is null) return new(PollStatus.Failed, 0, [], 0, "The server was removed.");
        var (show, more, newest) = NotificationLogic.SelectToAnnounce(list.Items, current.LastNotifiedId);
        if (newest is { } nw) _store.SetLastNotified(profile.Id, nw);

        SetUnread(profile.Id, st, list.UnreadCount);
        foreach (var n in show) NotificationAnnounced?.Invoke(current, n, NotificationLogic.PathForNotification(n, current.Origin));
        if (more > 0) MoreAnnounced?.Invoke(current, more);
        return new(PollStatus.Ok, list.UnreadCount, show, more, null);
    }

    private void SetUnread(string profileId, State st, int unread)
    {
        int total;
        lock (_gate)
        {
            if (st.Unread == unread) return;
            st.Unread = unread;
            total = _states.Values.Sum(s => s.Unread);
        }
        UnreadChanged?.Invoke(profileId, unread, total);
    }

    private async Task<(int? Status, string? Body)> GetAsync(ServerProfile profile, string pathAndQuery, string? token, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, new Uri(new Uri(profile.Origin), pathAndQuery));
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            if (token is not null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var res = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
            var status = (int)res.StatusCode;
            if (status is >= 300 and < 400) return (null, null); // never follow redirects with a token
            return (status, await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return (null, null);
        }
    }

    /// <summary>Polls every profile whose time has come. Returns the earliest time anything is next due.</summary>
    public async Task<DateTimeOffset?> PollDueAsync(CancellationToken ct = default)
    {
        var now = _time.GetUtcNow();
        var profiles = _store.List();
        lock (_gate)
        {
            foreach (var id in _states.Keys.Where(k => profiles.All(p => p.Id != k)).ToList()) _states.Remove(id);
        }
        foreach (var p in profiles)
        {
            ct.ThrowIfCancellationRequested();
            bool due;
            lock (_gate) due = !_states.TryGetValue(p.Id, out var s) || s.Due <= now;
            if (due) await PollOnceAsync(p, ct).ConfigureAwait(false);
        }
        lock (_gate) return _states.Count == 0 ? null : _states.Values.Min(s => s.Due);
    }

    /// <summary>Runs until cancelled. Wakes at least every 15 s to notice added servers and "check now".</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var next = await PollDueAsync(ct).ConfigureAwait(false);
            var wait = next is { } n ? n - _time.GetUtcNow() : TimeSpan.FromSeconds(15);
            wait = wait < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : wait > TimeSpan.FromSeconds(15) ? TimeSpan.FromSeconds(15) : wait;
            try { await Task.Delay(wait, _time, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }
}
