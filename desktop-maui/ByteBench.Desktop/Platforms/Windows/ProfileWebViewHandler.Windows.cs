using ByteBench.Desktop.Core;
using ByteBench.Desktop.Services;
using Microsoft.Maui.Handlers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;

namespace ByteBench.Desktop.Controls;

// NOT COMPILED OR RUN when this was written (no Windows machine, no MAUI workload). See desktop-maui/README.md.
public partial class ProfileWebViewHandler : ViewHandler<ProfileWebView, WebView2>
{
    private Backend? _backend;

    public ProfileWebViewHandler() : base(Mapper) { }

    protected override WebView2 CreatePlatformView() => new();

    protected override void ConnectHandler(WebView2 platformView)
    {
        base.ConnectHandler(platformView);
        _backend = new Backend(platformView);
        VirtualView.BackendReady(_backend);
        _ = InitializeAsync(platformView, _backend);
    }

    protected override void DisconnectHandler(WebView2 platformView)
    {
        VirtualView.BackendGone();
        _backend = null;
        try { platformView.Close(); } catch (Exception) { /* already closed */ }
        base.DisconnectHandler(platformView);
    }

    private async Task InitializeAsync(WebView2 view, Backend backend)
    {
        var owner = VirtualView;
        try
        {
            // The user-data folder is what isolates this server's cookies and storage from every other server's.
            var folder = ProfileData.FolderFor(owner.ProfileId);
            Directory.CreateDirectory(folder);
            var env = await CoreWebView2Environment.CreateAsync(browserExecutableFolder: null, userDataFolder: folder);
            await view.EnsureCoreWebView2Async(env);
        }
        catch (Exception e)
        {
            owner.RaiseFailed(owner.Origin, "The web view could not start (is the WebView2 runtime installed?): " + e.Message);
            return;
        }

        var core = view.CoreWebView2;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsWebMessageEnabled = false;
        core.Settings.AreHostObjectsAllowed = false;

        // Main-frame navigations go through the allow-list; iframes are not restricted.
        core.NavigationStarting += (_, e) => { if (!owner.ShouldAllow(e.Uri)) e.Cancel = true; };
        // window.open / target=_blank: never a second window.
        core.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            if (owner.ShouldAllow(e.Uri)) core.Navigate(e.Uri);
        };
        core.PermissionRequested += (_, e) =>
        {
            var decision = owner.Classify(e.Uri);
            var allow = e.PermissionKind switch
            {
                CoreWebView2PermissionKind.Notifications => decision == NavDecision.Internal,
                CoreWebView2PermissionKind.ClipboardRead => decision is NavDecision.Internal or NavDecision.Workspace,
                _ => false,
            };
            e.State = allow ? CoreWebView2PermissionState.Allow : CoreWebView2PermissionState.Deny;
        };
        core.NavigationCompleted += (_, e) =>
        {
            if (e.IsSuccess) owner.RaiseNavigated(core.Source);
            else if (e.WebErrorStatus != CoreWebView2WebErrorStatus.OperationCanceled) owner.RaiseFailed(core.Source, Describe(e.WebErrorStatus));
        };

        backend.Ready(core);
    }

    private static string Describe(CoreWebView2WebErrorStatus status) => status switch
    {
        CoreWebView2WebErrorStatus.HostNameNotResolved => "The server name could not be found. Check the address and your network connection.",
        CoreWebView2WebErrorStatus.CannotConnect or CoreWebView2WebErrorStatus.ConnectionAborted => "The connection was refused or lost. The server may be down, or the address or port may be wrong.",
        CoreWebView2WebErrorStatus.Timeout => "The server took too long to answer.",
        CoreWebView2WebErrorStatus.Disconnected => "You appear to be offline.",
        CoreWebView2WebErrorStatus.CertificateCommonNameIsIncorrect or CoreWebView2WebErrorStatus.CertificateExpired
            or CoreWebView2WebErrorStatus.ClientCertificateContainsErrors or CoreWebView2WebErrorStatus.CertificateRevoked
            or CoreWebView2WebErrorStatus.CertificateIsInvalid => "The security certificate of this server could not be verified.",
        _ => "The server could not be reached (" + status + ").",
    };

    private sealed class Backend(WebView2 view) : IWebViewBackend
    {
        private CoreWebView2? _core;
        private string? _pending;
        private string? _last;

        public void Ready(CoreWebView2 core)
        {
            _core = core;
            if (_pending is { } p) { _pending = null; core.Navigate(p); }
        }

        public void Navigate(string url)
        {
            _last = url;
            if (_core is null) _pending = url; // queued until WebView2 has finished starting
            else _core.Navigate(url);
        }

        public void Reload()
        {
            if (_core is null) return;
            if (string.IsNullOrEmpty(_core.Source) || _core.Source == "about:blank") { if (_last is not null) _core.Navigate(_last); }
            else _core.Reload();
        }

        public void GoBack() { if (_core?.CanGoBack == true) _core.GoBack(); }
        public void GoForward() { if (_core?.CanGoForward == true) _core.GoForward(); }
        public bool CanGoBack => _core?.CanGoBack == true;
        public bool CanGoForward => _core?.CanGoForward == true;
        public string? CurrentUrl => _core is { Source: { Length: > 0 } s } && s != "about:blank" ? s : _last;
    }
}
