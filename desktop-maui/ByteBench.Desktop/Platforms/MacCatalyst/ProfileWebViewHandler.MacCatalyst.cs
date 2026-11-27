using ByteBench.Desktop.Services;
using CoreGraphics;
using Foundation;
using Microsoft.Maui.Handlers;
using WebKit;

namespace ByteBench.Desktop.Controls;

// NOT COMPILED OR RUN when this was written (the maui-maccatalyst workload was not installable). See desktop-maui/README.md.
public partial class ProfileWebViewHandler : ViewHandler<ProfileWebView, WKWebView>
{
    private Backend? _backend;

    public ProfileWebViewHandler() : base(Mapper) { }

    protected override WKWebView CreatePlatformView()
    {
        var config = new WKWebViewConfiguration { WebsiteDataStore = ProfileData.DataStoreFor(VirtualView.ProfileId) };
        return new WKWebView(CGRect.Empty, config) { AllowsBackForwardNavigationGestures = true };
    }

    protected override void ConnectHandler(WKWebView platformView)
    {
        base.ConnectHandler(platformView);
        _backend = new Backend(platformView);
        platformView.NavigationDelegate = new NavDelegate(VirtualView);
        platformView.UIDelegate = new UiDelegate(VirtualView);
        VirtualView.BackendReady(_backend);
    }

    protected override void DisconnectHandler(WKWebView platformView)
    {
        VirtualView.BackendGone();
        platformView.NavigationDelegate = null!;
        platformView.UIDelegate = null!;
        platformView.StopLoading();
        _backend = null;
        base.DisconnectHandler(platformView);
    }

    private sealed class Backend(WKWebView view) : IWebViewBackend
    {
        private string? _lastRequested;
        public void Navigate(string url)
        {
            _lastRequested = url;
            view.LoadRequest(new NSUrlRequest(new NSUrl(url)));
        }
        public void Reload()
        {
            if (view.Url is null && _lastRequested is not null) Navigate(_lastRequested);
            else view.Reload();
        }
        public void GoBack() => view.GoBack();
        public void GoForward() => view.GoForward();
        public bool CanGoBack => view.CanGoBack;
        public bool CanGoForward => view.CanGoForward;
        public string? CurrentUrl => view.Url?.AbsoluteString ?? _lastRequested;
    }

    /// <summary>Main-frame navigations and new-window requests go through the allow-list. Sub-frames (iframes) are not restricted.</summary>
    private sealed class NavDelegate(ProfileWebView owner) : WKNavigationDelegate
    {
        public override void DecidePolicy(WKWebView webView, WKNavigationAction navigationAction, Action<WKNavigationActionPolicy> decisionHandler)
        {
            var url = navigationAction.Request.Url?.AbsoluteString;
            var frame = navigationAction.TargetFrame;
            if (frame is not null && !frame.MainFrame) { decisionHandler(WKNavigationActionPolicy.Allow); return; }
            var allow = owner.ShouldAllow(url);
            if (frame is null)
            {
                // target=_blank: never a second web view. A same-server link opens in this view, anything else was handled by the policy.
                if (allow) webView.LoadRequest(navigationAction.Request);
                decisionHandler(WKNavigationActionPolicy.Cancel);
                return;
            }
            decisionHandler(allow ? WKNavigationActionPolicy.Allow : WKNavigationActionPolicy.Cancel);
        }

        public override void DidFinishNavigation(WKWebView webView, WKNavigation navigation)
        {
            if (webView.Url?.AbsoluteString is { } u) owner.RaiseNavigated(u);
        }

        public override void DidFailProvisionalNavigation(WKWebView webView, WKNavigation navigation, NSError error) => Fail(webView, error);
        public override void DidFailNavigation(WKWebView webView, WKNavigation navigation, NSError error) => Fail(webView, error);

        private void Fail(WKWebView webView, NSError error)
        {
            if (error.Code == -999) return; // NSURLErrorCancelled: our own Cancel above, or the user navigated away
            owner.RaiseFailed(webView.Url?.AbsoluteString ?? owner.Origin, error.LocalizedDescription);
        }
    }

    private sealed class UiDelegate(ProfileWebView owner) : WKUIDelegate
    {
        // window.open: never creates a window; routed by the same policy.
        public override WKWebView? CreateWebView(WKWebView webView, WKWebViewConfiguration configuration, WKNavigationAction navigationAction, WKWindowFeatures windowFeatures)
        {
            var url = navigationAction.Request.Url?.AbsoluteString;
            if (owner.ShouldAllow(url)) webView.LoadRequest(navigationAction.Request);
            return null;
        }

        // Without these WKWebView swallows alert()/confirm()/prompt() (confirm() would always answer "cancel").
        public override async void RunJavaScriptAlertPanel(WKWebView webView, string message, WKFrameInfo frame, Action completionHandler)
        {
            await owner.ConfirmAsync(message, "OK", null);
            completionHandler();
        }

        public override async void RunJavaScriptConfirmPanel(WKWebView webView, string message, WKFrameInfo frame, Action<bool> completionHandler) =>
            completionHandler(await owner.ConfirmAsync(message));

        public override async void RunJavaScriptTextInputPanel(WKWebView webView, string prompt, string? defaultText, WKFrameInfo frame, Action<string> completionHandler) =>
            completionHandler(await owner.PromptAsync(prompt, defaultText ?? "") ?? "");
    }
}
