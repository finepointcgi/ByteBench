using Microsoft.Maui.Handlers;

namespace ByteBench.Desktop.Controls;

/// <summary>
/// Maps <see cref="ProfileWebView"/> to the platform's web view. The platform halves (Platforms/MacCatalyst, Platforms/Windows) create the
/// view with per-profile isolated storage and enforce the navigation allow-list.
/// </summary>
public partial class ProfileWebViewHandler
{
    public static IPropertyMapper<ProfileWebView, ProfileWebViewHandler> Mapper =
        new PropertyMapper<ProfileWebView, ProfileWebViewHandler>(ViewHandler.ViewMapper);
}
