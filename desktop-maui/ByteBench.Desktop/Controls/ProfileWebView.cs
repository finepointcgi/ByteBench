using ByteBench.Desktop.Core;
using ByteBench.Desktop.Services;

namespace ByteBench.Desktop.Controls;

/// <summary>What the platform web view offers the shell. Implemented per platform inside the handler.</summary>
public interface IWebViewBackend
{
    void Navigate(string url);
    void Reload();
    void GoBack();
    void GoForward();
    bool CanGoBack { get; }
    bool CanGoForward { get; }
    string? CurrentUrl { get; }
}

public sealed class NavigationFailedEventArgs(string url, string message) : EventArgs
{
    public string Url { get; } = url;
    public string Message { get; } = message;
}

/// <summary>
/// One web view bound to one saved server. Its web data (cookies, storage, cache) is isolated per profile by the platform handler:
/// a WebView2 user-data folder named after the profile id on Windows, a WKWebsiteDataStore identified by the profile id on Mac Catalyst.
/// The navigation allow-list (<see cref="NavigationPolicy"/>) is applied to every main-frame navigation and every new-window request.
/// </summary>
public sealed class ProfileWebView : View
{
    private string? _pendingUrl;

    public ProfileWebView(ServerProfile profile)
    {
        ProfileId = profile.Id;
        Origin = profile.Origin;
    }

    public string ProfileId { get; }
    public string Origin { get; }
    /// <summary>Workspace base domain from server-info, once known.</summary>
    public string? AdvertisedWorkspaceBase { get; set; }

    internal IWebViewBackend? Backend { get; private set; }

    public event EventHandler<string>? Navigated;
    public event EventHandler<NavigationFailedEventArgs>? NavigationFailed;

    public NavContext Context => new(Origin, AdvertisedWorkspaceBase);
    public string? CurrentUrl => Backend?.CurrentUrl;
    public bool CanGoBack => Backend?.CanGoBack == true;
    public bool CanGoForward => Backend?.CanGoForward == true;

    public void Navigate(string url)
    {
        if (Backend is null) _pendingUrl = url;
        else Backend.Navigate(url);
    }

    public void Reload()
    {
        if (CurrentUrl is null) { Navigate(Origin + "/"); return; }
        Backend?.Reload();
    }

    public void GoBack() { if (CanGoBack) Backend!.GoBack(); }
    public void GoForward() { if (CanGoForward) Backend!.GoForward(); }

    internal void BackendReady(IWebViewBackend backend)
    {
        Backend = backend;
        if (_pendingUrl is { } u) { _pendingUrl = null; backend.Navigate(u); }
    }

    internal void BackendGone() => Backend = null;

    public NavDecision Classify(string? url) => NavigationPolicy.Classify(url, Context);

    /// <summary>
    /// The allow-list decision for a main-frame navigation or a new-window request: the server's own origin and its workspace origins
    /// stay in the app, other web links go to the system browser, everything else is refused.
    /// </summary>
    public bool ShouldAllow(string? url)
    {
        switch (Classify(url))
        {
            case NavDecision.Internal:
            case NavDecision.Workspace:
                return true;
            case NavDecision.External:
                ExternalLauncher.Open(url!);
                return false;
            default:
                return false;
        }
    }

    internal void RaiseNavigated(string url) => MainThread.BeginInvokeOnMainThread(() => Navigated?.Invoke(this, url));
    internal void RaiseFailed(string url, string message) => MainThread.BeginInvokeOnMainThread(() => NavigationFailed?.Invoke(this, new NavigationFailedEventArgs(url, message)));

    /// <summary>JavaScript alert/confirm/prompt dialogs of the web UI, shown with the app's own dialogs.</summary>
    internal async Task<bool> ConfirmAsync(string message, string accept = "OK", string? cancel = "Cancel")
    {
        var page = Window?.Page;
        if (page is null) return false;
#pragma warning disable CS0618 // DisplayAlert is obsolete in newer MAUI in favour of DisplayAlertAsync; it still works.
        if (cancel is null)
        {
            await page.DisplayAlert(Origin, message, accept);
            return true;
        }
        return await page.DisplayAlert(Origin, message, accept, cancel);
#pragma warning restore CS0618
    }

    internal async Task<string?> PromptAsync(string message, string defaultText)
    {
        var page = Window?.Page;
        if (page is null) return null;
#pragma warning disable CS0618
        return await page.DisplayPromptAsync(Origin, message, initialValue: defaultText);
#pragma warning restore CS0618
    }
}
