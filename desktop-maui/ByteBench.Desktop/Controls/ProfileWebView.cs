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
    /// <summary>The current page's title, if the platform reports one.</summary>
    string? Title => null;
    /// <summary>
    /// Back out of a workspace page (see <see cref="NavigationPolicy.WorkspaceBackSteps"/>): to the page before it, skipping the ByteBench page
    /// that framed it; <paramref name="fallbackUrl"/> if there is none. Platforms without a history list just go back or to the fallback.
    /// </summary>
    void GoBackFromWorkspace(string? framedFromUrl, string fallbackUrl)
    {
        if (CanGoBack) GoBack(); else Navigate(fallbackUrl);
    }
}

public sealed class DiagnosticEventArgs(string url, string message) : EventArgs
{
    public string Url { get; } = url;
    public string Message { get; } = message;
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
    /// <summary>A problem that is not a main-frame failure but may explain a blank page (a frame that failed or was refused, an HTTP error, a script error).</summary>
    public event EventHandler<DiagnosticEventArgs>? Problem;
    /// <summary>The page became or stopped being a workspace page (IDE or preview) shown top-level; <see cref="InWorkspace"/> and <see cref="WorkspaceTitle"/> are current.</summary>
    public event EventHandler? WorkspaceChanged;

    /// <summary>True while the top-level document is a workspace origin: the app shows its native workspace bar.</summary>
    public bool InWorkspace { get; private set; }
    public string WorkspaceTitle { get; private set; } = "Workspace";
    /// <summary>The ByteBench page whose workspace frame was opened top-level; Back skips it, because going there would just re-open the IDE.</summary>
    public string? FramedFromUrl { get; private set; }
    private string? _pageTitle;
    private DateTime _promotingUntil;

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

    public void GoBack()
    {
        if (InWorkspace) Backend?.GoBackFromWorkspace(FramedFromUrl, Origin + "/workspaces");
        else if (CanGoBack) Backend!.GoBack();
    }
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

    /// <summary>
    /// A frame inside the current page is loading <paramref name="frameUrl"/>. WebKit blocks third-party cookies in frames, so a workspace
    /// origin framed by a ByteBench page never gets its session cookie and stays blank. Instead the frame load is cancelled (returns true)
    /// and the workspace is opened as the top-level document, where its cookie is first-party. The server and its cookies are unchanged.
    /// </summary>
    public bool TryPromoteFrame(string? frameUrl, string? topUrl, string? topTitle)
    {
        if (!NavigationPolicy.ShouldPromoteFrame(frameUrl, topUrl, Context)) return false;
        if (DateTime.UtcNow < _promotingUntil) { Log($"frame load for {DiagnosticsLog.RedactUrl(frameUrl)} ignored: a top-level load is already starting"); return true; }
        _promotingUntil = DateTime.UtcNow.AddSeconds(5);
        FramedFromUrl = topUrl;
        _pageTitle = topTitle;
        Log($"workspace frame {DiagnosticsLog.RedactUrl(frameUrl)} inside {DiagnosticsLog.RedactUrl(topUrl)}: cancelled, opening it as the top-level page");
        var url = frameUrl!;
        MainThread.BeginInvokeOnMainThread(() => Backend?.Navigate(url));
        return true;
    }

    /// <summary>A back/forward navigation (swipe or button) from a workspace page onto the ByteBench page that framed it would re-open the workspace: go to the page before it instead.</summary>
    public bool RedirectBackFromWorkspace(string? targetUrl)
    {
        if (!InWorkspace || FramedFromUrl is null || targetUrl != FramedFromUrl) return false;
        MainThread.BeginInvokeOnMainThread(GoBack);
        return true;
    }

    private void UpdateWorkspaceState(string url)
    {
        var inWs = NavigationPolicy.WorkspaceKindOf(url, Context) != WorkspaceKind.None;
        var title = inWs ? NavigationPolicy.WorkspaceTitle(_pageTitle) : WorkspaceTitle;
        if (inWs == InWorkspace && title == WorkspaceTitle) return;
        InWorkspace = inWs;
        WorkspaceTitle = title;
        if (!inWs) { FramedFromUrl = null; _pageTitle = null; }
        WorkspaceChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Main-frame navigation reached <paramref name="url"/>.</summary>
    internal void RaiseNavigated(string url) => MainThread.BeginInvokeOnMainThread(() =>
    {
        _promotingUntil = default;
        UpdateWorkspaceState(url);
        Navigated?.Invoke(this, url);
    });

    internal void Log(string message) => DiagnosticsLog.Default?.Info(message);

    /// <summary>Records a problem in the log and tells the shell, which shows a banner with the URL and reason.</summary>
    internal void RaiseProblem(string url, string message)
    {
        DiagnosticsLog.Default?.Error($"{message} [{DiagnosticsLog.RedactUrl(url)}]");
        MainThread.BeginInvokeOnMainThread(() => Problem?.Invoke(this, new DiagnosticEventArgs(DiagnosticsLog.RedactUrl(url), message)));
    }

    internal void RaiseFailed(string url, string message)
    {
        _promotingUntil = default;
        DiagnosticsLog.Default?.Error($"main-frame load failed: {message} [{DiagnosticsLog.RedactUrl(url)}]");
        MainThread.BeginInvokeOnMainThread(() => NavigationFailed?.Invoke(this, new NavigationFailedEventArgs(url, message)));
    }

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
