using ByteBench.Desktop.Controls;
using ByteBench.Desktop.Core;
using ByteBench.Desktop.Services;
using Microsoft.Maui.LifecycleEvents;

namespace ByteBench.Desktop;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .ConfigureMauiHandlers(handlers => handlers.AddHandler<ProfileWebView, ProfileWebViewHandler>())
            .ConfigureLifecycleEvents(events =>
            {
#if MACCATALYST
                // bytebench:// links (CFBundleURLTypes in Info.plist). Mac Catalyst uses the iOS lifecycle hooks.
                events.AddiOS(ios => ios.OpenUrl((application, url, options) =>
                {
                    DeepLinkRouter.Handle(url.AbsoluteString);
                    return true;
                }));
#endif
            });

        var dataDir = FileSystem.AppDataDirectory;
        builder.Services.AddSingleton(new ProfileStore(Path.Combine(dataDir, "profiles.json")));
        builder.Services.AddSingleton<ITokenStore, SecureTokenStore>();
        builder.Services.AddSingleton(NotificationPoller.CreateDefaultClient());
        builder.Services.AddSingleton<NotificationPoller>();
        builder.Services.AddSingleton<OsNotifier>();
        builder.Services.AddSingleton<MainPage>();
        return builder.Build();
    }
}
