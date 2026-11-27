using ByteBench.Desktop.Core;
using Microsoft.Win32;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace ByteBench.Desktop.Services;

// NOT COMPILED OR RUN when this was written (no Windows machine, no MAUI workload). See desktop-maui/README.md.

public static partial class ProfileData
{
    public static partial bool IsPersistent => true;

    /// <summary>The WebView2 user-data folder of a profile: one folder per profile, so cookies, storage and cache never mix.</summary>
    public static string FolderFor(string profileId) => Path.Combine(FileSystem.AppDataDirectory, "webdata", profileId);

    public static partial Task RemoveAsync(string profileId)
    {
        // The page has closed the WebView2 by now; the browser process may need a moment to release its files.
        return Task.Run(async () =>
        {
            for (var attempt = 0; attempt < 5; attempt++)
            {
                try { if (Directory.Exists(FolderFor(profileId))) Directory.Delete(FolderFor(profileId), recursive: true); return; }
                catch (IOException) { await Task.Delay(500); }
                catch (UnauthorizedAccessException) { await Task.Delay(500); }
            }
        });
    }
}

public static partial class LinkRegistration
{
    public static partial bool IsNeeded => true;

    public static partial string? Register()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return "The path of the app could not be determined.";
            using var root = Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + DeepLink.Scheme);
            root.SetValue("", "URL:ByteBench");
            root.SetValue("URL Protocol", "");
            using var cmd = root.CreateSubKey(@"shell\open\command");
            cmd.SetValue("", $"\"{exe}\" \"%1\"");
            return null;
        }
        catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            return e.Message;
        }
    }
}

public sealed partial class OsNotifier
{
    private bool _ok;

    public partial void Initialize()
    {
        try
        {
            var manager = AppNotificationManager.Default;
            manager.NotificationInvoked += (_, e) =>
            {
                if (e.Arguments.TryGetValue("profile", out var profile))
                    RaiseClicked(profile, e.Arguments.TryGetValue("path", out var path) ? path : "/");
            };
            manager.Register();
            _ok = true;
        }
        catch (Exception)
        {
            _ok = false; // WinRT surfaces COM failures as several exception types; without registration there are simply no OS notifications
        }
    }

    public partial void Show(string profileId, string path, string title, string body)
    {
        if (!_ok) return;
        try
        {
            var n = new AppNotificationBuilder()
                .AddArgument("profile", profileId)
                .AddArgument("path", path)
                .AddText(title)
                .AddText(body)
                .BuildNotification();
            AppNotificationManager.Default.Show(n);
        }
        catch (Exception) { /* a notification that cannot be shown is not worth crashing the poller for */ }
    }

    // Windows has no simple taskbar-badge call for unpackaged apps; the page shows the unread count in the window title instead.
    public partial void SetBadge(int unread) { }
}
