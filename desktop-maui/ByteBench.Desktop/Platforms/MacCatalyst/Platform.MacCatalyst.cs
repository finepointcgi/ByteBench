using ByteBench.Desktop.Core;
using Foundation;
using UIKit;
using UserNotifications;
using WebKit;

namespace ByteBench.Desktop.Services;

// Compiled with Xcode 27.2 and launched once; see desktop-maui/README.md for what is still unverified.

public static partial class ProfileData
{
    // A WKWebsiteDataStore with a persistent identifier (dataStoreForIdentifier:) exists from Mac Catalyst 17 (macOS 14). On older
    // systems the closest honest fallback is a separate NON-persistent store per profile: still isolated from the other servers,
    // but the sign-in is forgotten when the app quits.
    public static partial bool IsPersistent => OperatingSystem.IsMacCatalystVersionAtLeast(17);

    public static WKWebsiteDataStore DataStoreFor(string profileId)
    {
        if (OperatingSystem.IsMacCatalystVersionAtLeast(17))
            return ObjCRuntime.Runtime.GetNSObject<WKWebsiteDataStore>(MsgSend(new ObjCRuntime.Class(typeof(WKWebsiteDataStore)).Handle, new ObjCRuntime.Selector("dataStoreForIdentifier:").Handle, new NSUuid(Guid.ParseExact(profileId, "N").ToString("D")).Handle))!;
        return NonPersistent(profileId);
    }

    // The binding has no static for dataStoreForIdentifier:, so the selector is sent directly.
    [System.Runtime.InteropServices.DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern IntPtr MsgSend(IntPtr receiver, IntPtr selector, IntPtr arg);

    private static readonly Dictionary<string, WKWebsiteDataStore> Ephemeral = [];

    private static WKWebsiteDataStore NonPersistent(string profileId)
    {
        if (!Ephemeral.TryGetValue(profileId, out var store)) Ephemeral[profileId] = store = WKWebsiteDataStore.NonPersistentDataStore;
        return store;
    }

    public static partial Task RemoveAsync(string profileId)
    {
        Ephemeral.Remove(profileId);
        if (!OperatingSystem.IsMacCatalystVersionAtLeast(17)) return Task.CompletedTask;
        var done = new TaskCompletionSource();
        // Fails (harmlessly) while a web view still uses the store; the page drops its view first.
        WKWebsiteDataStore.Remove(new NSUuid(Guid.ParseExact(profileId, "N").ToString("D")), _ => done.TrySetResult());
        return done.Task;
    }
}

public static partial class LinkRegistration
{
    public static partial bool IsNeeded => false; // CFBundleURLTypes in Info.plist registers the scheme when the app is first launched from Finder
    public static partial string? Register() => null;
}

public sealed partial class OsNotifier
{
    public partial void Initialize()
    {
        var center = UNUserNotificationCenter.Current;
        center.Delegate = new CenterDelegate(this);
        center.RequestAuthorization(UNAuthorizationOptions.Alert | UNAuthorizationOptions.Badge | UNAuthorizationOptions.Sound, (_, _) => { });
    }

    public partial void Show(string profileId, string path, string title, string body)
    {
        var content = new UNMutableNotificationContent
        {
            Title = title,
            Body = body,
            UserInfo = NSDictionary.FromObjectsAndKeys(
                new NSObject[] { new NSString(profileId), new NSString(path) },
                new NSObject[] { new NSString("profile"), new NSString("path") }),
        };
        var request = UNNotificationRequest.FromIdentifier(Guid.NewGuid().ToString(), content, null);
        UNUserNotificationCenter.Current.AddNotificationRequest(request, _ => { });
    }

    public partial void SetBadge(int unread)
    {
#pragma warning disable CA1422 // ApplicationIconBadgeNumber is marked obsolete on newer OS versions; still the simplest call.
        MainThread.BeginInvokeOnMainThread(() => UIApplication.SharedApplication.ApplicationIconBadgeNumber = unread);
#pragma warning restore CA1422
    }

    private sealed class CenterDelegate(OsNotifier owner) : UNUserNotificationCenterDelegate
    {
        public override void WillPresentNotification(UNUserNotificationCenter center, UNNotification notification, Action<UNNotificationPresentationOptions> completionHandler) =>
            completionHandler(UNNotificationPresentationOptions.Banner | UNNotificationPresentationOptions.List);

        public override void DidReceiveNotificationResponse(UNUserNotificationCenter center, UNNotificationResponse response, Action completionHandler)
        {
            var info = response.Notification.Request.Content.UserInfo;
            var profile = info[new NSString("profile")]?.ToString();
            var path = info[new NSString("path")]?.ToString();
            if (profile is not null) owner.RaiseClicked(profile, path ?? "/");
            completionHandler();
        }
    }
}
