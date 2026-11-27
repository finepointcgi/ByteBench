using ByteBench.Desktop.Core;

namespace ByteBench.Desktop.Services;

/// <summary>Tokens in the OS secure store (Keychain on Mac Catalyst, the Windows credential-protected app store on Windows). Never plain text.</summary>
public sealed class SecureTokenStore : ITokenStore
{
    private static string Key(string profileId) => $"bytebench.token.{profileId}";

    public bool Available => true; // probed on first use: Set returns false when the platform refuses (e.g. an unsigned Mac build without keychain entitlement)

    public async Task<string?> GetAsync(string profileId, CancellationToken ct = default)
    {
        try { return await SecureStorage.Default.GetAsync(Key(profileId)); }
        catch (Exception) { return null; } // the platform throws several different types when the store is unavailable
    }

    public async Task<bool> SetAsync(string profileId, string token, CancellationToken ct = default)
    {
        var t = token.Trim();
        if (t.Length == 0) return false;
        try { await SecureStorage.Default.SetAsync(Key(profileId), t); return true; }
        catch (Exception) { return false; }
    }

    public Task DeleteAsync(string profileId, CancellationToken ct = default)
    {
        try { SecureStorage.Default.Remove(Key(profileId)); }
        catch (Exception) { /* nothing stored, or the store is unavailable */ }
        return Task.CompletedTask;
    }
}

/// <summary>Receives bytebench:// links from the OS (Mac: lifecycle OpenUrl; Windows: the command line) and hands them to the page.</summary>
public static class DeepLinkRouter
{
    private static readonly object Gate = new();
    private static Action<string>? _handler;
    private static string? _pending;

    public static void Handle(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return;
        Action<string>? h;
        lock (Gate) { h = _handler; if (h is null) _pending = raw; }
        h?.Invoke(raw);
    }

    /// <summary>The page registers once; a link that arrived before it did is delivered now.</summary>
    public static void Attach(Action<string> handler)
    {
        string? p;
        lock (Gate) { _handler = handler; p = _pending; _pending = null; }
        if (p is not null) handler(p);
    }
}

/// <summary>Hands a URL to the system browser or mail client, only when it passes <see cref="NavigationPolicy.IsSafeExternal"/>, and not more than about once a second.</summary>
public static class ExternalLauncher
{
    private static long _last;

    public static void Open(string url)
    {
        if (!NavigationPolicy.IsSafeExternal(url)) return;
        var now = Environment.TickCount64;
        if (now - _last < 750) return;
        _last = now;
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            try { await Launcher.Default.OpenAsync(new Uri(url)); }
            catch (Exception) { /* no handler for the link: nothing to do */ }
        });
    }
}

/// <summary>Per-profile isolated web storage. Platform parts live in Platforms/*.</summary>
public static partial class ProfileData
{
    /// <summary>True when sign-ins of this profile survive an app restart (see README for the Mac fallback).</summary>
    public static partial bool IsPersistent { get; }

    /// <summary>Deletes the stored cookies and cache of a removed profile (best effort).</summary>
    public static partial Task RemoveAsync(string profileId);
}

/// <summary>bytebench:// registration. Mac: declared in Info.plist (nothing to do). Windows (unpackaged): an explicit menu action writes HKCU keys.</summary>
public static partial class LinkRegistration
{
    public static partial bool IsNeeded { get; }
    /// <summary>Returns null on success, else a message.</summary>
    public static partial string? Register();
}

/// <summary>OS notifications and the unread badge. Platform parts live in Platforms/*.</summary>
public sealed partial class OsNotifier
{
    /// <summary>(profile id, server-relative path) when the user clicks a notification.</summary>
    public event Action<string, string>? Clicked;

    private void RaiseClicked(string profileId, string path) => Clicked?.Invoke(profileId, path);

    public partial void Initialize();
    public partial void Show(string profileId, string path, string title, string body);
    public partial void SetBadge(int unread);
}
