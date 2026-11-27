using ByteBench.Desktop.Core;
using ByteBench.Desktop.Services;
using CoreGraphics;
using Foundation;
using Microsoft.Maui.Handlers;
using ObjCRuntime;
using UIKit;
using WebKit;

namespace ByteBench.Desktop.Controls;

// Compiled with Xcode 27.2 and launched once; the workspace top-level handling and diagnostics below are compiled but not seen working
// (no screen access when they were written). See desktop-maui/README.md.
public partial class ProfileWebViewHandler : ViewHandler<ProfileWebView, WKWebView>
{
    private Backend? _backend;
    private DiagnosticsBridge? _bridge;

    public ProfileWebViewHandler() : base(Mapper) { }

    /// <summary>
    /// Reports page-side problems to the app log: script errors, unhandled promise rejections, Content-Security-Policy violations and
    /// console.error/warn, from every frame, plus a one-off summary of each page (its frames, and whether it has any visible content).
    /// URLs are sent without query. Each frame reports at most 40 messages.
    /// </summary>
    private const string DiagnosticsScript = """
        (function () {
          if (window.__bbdiag) return; window.__bbdiag = true;
          var n = 0;
          function send(k, m) {
            if (n++ > 40) return;
            try { window.webkit.messageHandlers.bbdiag.postMessage({ k: k, m: String(m).slice(0, 500), u: location.href.split('?')[0], top: window === window.top }); } catch (e) {}
          }
          window.addEventListener('error', function (e) { send('error', (e.message || 'error') + ' @' + String(e.filename || '').split('?')[0] + ':' + (e.lineno || '')); }, true);
          window.addEventListener('unhandledrejection', function (e) { send('rejection', e.reason && e.reason.message || e.reason); });
          document.addEventListener('securitypolicyviolation', function (e) { send('csp', e.violatedDirective + ' blocked ' + String(e.blockedURI).split('?')[0]); });
          ['error', 'warn'].forEach(function (l) {
            var o = console[l];
            console[l] = function () { send('console.' + l, Array.prototype.join.call(arguments, ' ')); return o.apply(console, arguments); };
          });
          window.addEventListener('load', function () {
            if (window !== window.top) { send('frame-loaded', 'children=' + document.body.childElementCount); return; }
            var frames = Array.prototype.map.call(document.querySelectorAll('iframe'), function (f) { return String(f.src).split('?')[0]; });
            send('page-loaded', 'title=' + JSON.stringify(document.title) + ' frames=' + JSON.stringify(frames) + ' cookiesEnabled=' + navigator.cookieEnabled);
            setTimeout(function () {
              var b = document.body;
              send('page-check', 'textLength=' + (b ? b.innerText.length : -1) + ' elements=' + (b ? b.getElementsByTagName('*').length : -1));
            }, 4000);
          });
        })();
        """;

    protected override WKWebView CreatePlatformView()
    {
        var config = new WKWebViewConfiguration { WebsiteDataStore = ProfileData.DataStoreFor(VirtualView.ProfileId) };
        _bridge = new DiagnosticsBridge(VirtualView);
        config.UserContentController.AddScriptMessageHandler(_bridge, "bbdiag");
        config.UserContentController.AddUserScript(new WKUserScript(new NSString(DiagnosticsScript), WKUserScriptInjectionTime.AtDocumentStart, false));
        var view = new BbWebView(CGRect.Empty, config, () => VirtualView?.GoBack(), () => VirtualView?.GoForward()) { AllowsBackForwardNavigationGestures = true };
#if DEBUG
        if (OperatingSystem.IsMacCatalystVersionAtLeast(16, 4)) view.Inspectable = true; // Safari > Develop > this Mac
#endif
        return view;
    }

    protected override void ConnectHandler(WKWebView platformView)
    {
        base.ConnectHandler(platformView);
        _backend = new Backend(platformView);
        platformView.NavigationDelegate = new NavDelegate(VirtualView);
        platformView.UIDelegate = new UiDelegate(VirtualView);
        VirtualView.Log("web view started for " + DiagnosticsLog.RedactUrl(VirtualView.Origin));
        VirtualView.BackendReady(_backend);
    }

    protected override void DisconnectHandler(WKWebView platformView)
    {
        VirtualView.BackendGone();
        platformView.NavigationDelegate = null!;
        platformView.UIDelegate = null!;
        platformView.Configuration.UserContentController.RemoveScriptMessageHandler("bbdiag");
        platformView.StopLoading();
        _backend = null;
        _bridge = null;
        base.DisconnectHandler(platformView);
    }

    /// <summary>A WKWebView that also answers Cmd+[ and Cmd+] itself when it has the keyboard focus (the View menu has them too).</summary>
    private sealed class BbWebView : WKWebView
    {
        private readonly Action _back, _forward;

        public BbWebView(CGRect frame, WKWebViewConfiguration config, Action back, Action forward) : base(frame, config)
        {
            _back = back;
            _forward = forward;
        }

        public override UIKeyCommand[] KeyCommands =>
        [
            UIKeyCommand.Create(new NSString("["), UIKeyModifierFlags.Command, new Selector("bbBack:")),
            UIKeyCommand.Create(new NSString("]"), UIKeyModifierFlags.Command, new Selector("bbForward:")),
        ];

        [Export("bbBack:")] private void BbBack(UIKeyCommand _) => _back();
        [Export("bbForward:")] private void BbForward(UIKeyCommand _) => _forward();
    }

    private sealed class DiagnosticsBridge(ProfileWebView owner) : NSObject, IWKScriptMessageHandler
    {
        public void DidReceiveScriptMessage(WKUserContentController userContentController, WKScriptMessage message)
        {
            if (message.Body is not NSDictionary d) return;
            string Get(string key) => d[new NSString(key)]?.ToString() ?? "";
            var kind = Get("k");
            var text = $"page {kind}: {Get("m")} (frame {(Get("top") == "1" ? "top" : "sub")} {Get("u")})";
            // Script errors are common in any web app: log only. A blocked frame/resource (CSP) can explain a blank page: tell the user.
            if (kind == "csp") owner.RaiseProblem(Get("u"), text);
            else owner.Log(text);
        }
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
        public string? Title => view.Title;

        public void GoBackFromWorkspace(string? framedFromUrl, string fallbackUrl)
        {
            var back = view.BackForwardList.BackList;
            var urls = back.Select(i => i.Url?.AbsoluteString ?? "").ToList();
            var steps = NavigationPolicy.WorkspaceBackSteps(view.Url?.AbsoluteString, urls, framedFromUrl);
            if (steps is { } n && n >= 1 && n <= back.Length) view.GoTo(back[back.Length - n]);
            else Navigate(fallbackUrl);
        }
    }

    /// <summary>
    /// Main-frame navigations and new-window requests go through the allow-list. A workspace origin framed by a ByteBench page is not loaded
    /// in the frame but opened as the top-level page (see <see cref="ProfileWebView.TryPromoteFrame"/>); other frames are not restricted.
    /// Everything that fails or is refused is logged and, where it can explain a blank page, shown in a banner.
    /// </summary>
    private sealed class NavDelegate(ProfileWebView owner) : WKNavigationDelegate
    {
        public override void DecidePolicy(WKWebView webView, WKNavigationAction navigationAction, Action<WKNavigationActionPolicy> decisionHandler)
        {
            var url = navigationAction.Request.Url?.AbsoluteString;
            var frame = navigationAction.TargetFrame;
            if (frame is not null && !frame.MainFrame)
            {
                if (owner.TryPromoteFrame(url, webView.Url?.AbsoluteString, webView.Title)) { decisionHandler(WKNavigationActionPolicy.Cancel); return; }
                if (url is not null && url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) owner.Log($"frame navigation allowed: {DiagnosticsLog.RedactUrl(url)}");
                decisionHandler(WKNavigationActionPolicy.Allow);
                return;
            }
            if (frame is not null && navigationAction.NavigationType == WKNavigationType.BackForward && owner.RedirectBackFromWorkspace(url))
            {
                owner.Log("back/forward onto the page that framed the workspace: going to the page before it instead");
                decisionHandler(WKNavigationActionPolicy.Cancel);
                return;
            }
            var allow = owner.ShouldAllow(url);
            if (!allow) owner.Log($"navigation not allowed in the app: {DiagnosticsLog.RedactUrl(url)} ({owner.Classify(url)})");
            if (frame is null)
            {
                // target=_blank: never a second web view. A same-server link opens in this view, anything else was handled by the policy.
                if (allow) webView.LoadRequest(navigationAction.Request);
                decisionHandler(WKNavigationActionPolicy.Cancel);
                return;
            }
            decisionHandler(allow ? WKNavigationActionPolicy.Allow : WKNavigationActionPolicy.Cancel);
        }

        // Responses of every frame (WebKit reports no failures of a sub-frame itself, but it does report its response): a 4xx/5xx in a
        // frame, or a header that forbids framing, is the usual reason for an empty box.
        public override void DecidePolicy(WKWebView webView, WKNavigationResponse navigationResponse, Action<WKNavigationResponsePolicy> decisionHandler)
        {
            try
            {
                var url = navigationResponse.Response.Url?.AbsoluteString;
                if (navigationResponse.Response is NSHttpUrlResponse http)
                {
                    var status = (int)http.StatusCode;
                    var kind = navigationResponse.IsForMainFrame ? "main-frame" : "frame";
                    string Header(string name) => http.AllHeaderFields.TryGetValue(new NSString(name), out var v) ? v?.ToString() ?? "" : "";
                    var xfo = Header("X-Frame-Options");
                    var csp = Header("Content-Security-Policy");
                    var ancestors = csp.Split(';', StringSplitOptions.TrimEntries).FirstOrDefault(p => p.StartsWith("frame-ancestors", StringComparison.OrdinalIgnoreCase));
                    owner.Log($"{kind} response {status} {http.MimeType} {DiagnosticsLog.RedactUrl(url)}"
                        + (xfo.Length > 0 ? $" X-Frame-Options={xfo}" : "") + (ancestors is not null ? $" {ancestors}" : ""));
                    if (!navigationResponse.IsForMainFrame && status >= 400) owner.RaiseProblem(url ?? "", $"A frame on this page got HTTP {status}.");
                }
            }
            catch (Exception e) { owner.Log("response logging failed: " + e.Message); }
            decisionHandler(WKNavigationResponsePolicy.Allow);
        }

        public override void DidCommitNavigation(WKWebView webView, WKNavigation navigation)
        {
            if (webView.Url?.AbsoluteString is { } u) owner.RaiseNavigated(u);
        }

        public override void DidFinishNavigation(WKWebView webView, WKNavigation navigation)
        {
            if (webView.Url?.AbsoluteString is { } u)
            {
                owner.Log("main frame finished: " + DiagnosticsLog.RedactUrl(u));
                owner.RaiseNavigated(u);
            }
        }

        public override void DidFailProvisionalNavigation(WKWebView webView, WKNavigation navigation, NSError error) => Fail(webView, error, "before it started");
        public override void DidFailNavigation(WKWebView webView, WKNavigation navigation, NSError error) => Fail(webView, error, "while loading");

        // The classic WKWebView white screen: the web content process was killed (memory, crash). Reload brings the page back.
        public override void ContentProcessDidTerminate(WKWebView webView)
        {
            owner.RaiseProblem(webView.Url?.AbsoluteString ?? owner.Origin, "The web content process ended unexpectedly; reloading the page.");
            webView.Reload();
        }

        private void Fail(WKWebView webView, NSError error, string when)
        {
            if (error.Code == -999) return; // NSURLErrorCancelled: our own Cancel above, or the user navigated away
            var url = error.UserInfo?[new NSString("NSErrorFailingURLStringKey")]?.ToString() ?? webView.Url?.AbsoluteString ?? owner.Origin;
            owner.RaiseFailed(url, $"{error.LocalizedDescription} ({error.Domain} {error.Code}, {when})");
        }
    }

    private sealed class UiDelegate(ProfileWebView owner) : WKUIDelegate
    {
        // window.open: never creates a window; routed by the same policy.
        public override WKWebView? CreateWebView(WKWebView webView, WKWebViewConfiguration configuration, WKNavigationAction navigationAction, WKWindowFeatures windowFeatures)
        {
            var url = navigationAction.Request.Url?.AbsoluteString;
            if (owner.ShouldAllow(url)) webView.LoadRequest(navigationAction.Request);
            else owner.Log($"window.open not allowed in the app: {DiagnosticsLog.RedactUrl(url)}");
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
