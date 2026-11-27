# ByteBench desktop (.NET MAUI)

An alternative to the Electron app in `../desktop`. Same idea: a thin shell that loads the server's own web UI, with several saved
servers (separate sign-ins), a switcher, OS notifications, `bytebench://` links and "Clone with ByteBench". Feature spec:
`../desktop/README.md`. The Electron app is untouched.

```
desktop-maui/
  ByteBench.Desktop.Core/   plain net10.0 library, no workload needed: all the logic that can be tested
  ByteBench.Desktop/        the MAUI app (Mac Catalyst; Windows too when built on Windows). NOT in ByteBench.sln
../tests/ByteBench.Desktop.Core.Tests/   xunit tests of Core (in ByteBench.sln, run by CI)
```

## Build and run

Needs the .NET 10 SDK and the MAUI workload for your OS. Install it once (macOS needs `sudo`; Windows needs an elevated shell):

```
# macOS
sudo dotnet workload install maui-maccatalyst
# Windows
dotnet workload install maui-windows
```

Then, from the repository root:

```
# macOS
dotnet build desktop-maui/ByteBench.Desktop -f net10.0-maccatalyst
dotnet build desktop-maui/ByteBench.Desktop -f net10.0-maccatalyst -t:Run     # builds and launches
# Windows (unpackaged, runs from the output folder)
dotnet build desktop-maui/ByteBench.Desktop -f net10.0-windows10.0.19041.0
dotnet run --project desktop-maui/ByteBench.Desktop -f net10.0-windows10.0.19041.0
```

Windows needs the WebView2 runtime (present on current Windows 10/11). On macOS you may need to ad-hoc sign the app for it to launch
on Apple Silicon: `codesign --force --deep -s - <path to ByteBench.app>`.

Core and its tests need no workload:

```
dotnet build --no-incremental -warnaserror desktop-maui/ByteBench.Desktop.Core
dotnet test tests/ByteBench.Desktop.Core.Tests
```

## What is where

| Feature | Core (tested) | App (not compiled, see below) |
| --- | --- | --- |
| Server address, https/http rules, private hosts | `ServerUrl` | first-run / add-server panel in `MainPage` |
| Saved servers (JSON, atomic write, owner-only file, re-validated on load) | `ProfileStore` | server list, rename, remove, switcher, Servers menu (Cmd/Ctrl+1..9, Cmd/Ctrl+K) |
| Navigation allow-list | `NavigationPolicy` | `ProfileWebView.ShouldAllow`, hooked to the platform web views |
| `bytebench://open?server=&path=` | `DeepLink` | `Info.plist` (Mac), command line + opt-in registry registration (Windows), `DeepLinkRouter` |
| Notifications, badge | `NotificationPoller`, `NotificationLogic`, `ServerApi` | `OsNotifier` (UNUserNotificationCenter / Windows App SDK), window title and dock badge |
| Clone | `GitClone` (`git clone -- <url> <dir>` via `ArgumentList`, token via `GIT_CONFIG_*` env) | File > Clone This Repository |
| Tokens | `ITokenStore` | `SecureTokenStore` over `SecureStorage` (Keychain / Windows) |
| Isolated sign-in per server | - | see next section |

Behaviour that matches the Electron app: polling about every 30 s, doubling to 5 min on failures; the first poll of a server only
records a baseline (no burst of old notifications); at most 5 notifications per poll, the rest summarised; a notification URL that is
not a server-relative path opens `/notifications`; a link can never add a server or leave the server's origin; a server without
`GET /api/v1/server-info` still loads but has notifications off.

## Per-server isolated sign-in

* **Windows:** each server gets its own WebView2 user-data folder, `<app data>/webdata/<profile id>`, created through
  `CoreWebView2Environment.CreateAsync(null, folder)`. Removing a server deletes the folder.
* **Mac Catalyst:** each server gets its own `WKWebsiteDataStore` from `dataStoreForIdentifier:` (the profile id as UUID). That API
  exists from Mac Catalyst 17 (macOS 14). **On older systems it does not exist**, so the closest honest fallback is used: a separate
  *non-persistent* store per server. Servers are still isolated from each other, but the sign-in is forgotten when the app quits.
  `ProfileData.IsPersistent` reports which mode is active.

## Differences from the Electron app, stated plainly

* **No system tray / menu-bar item.** MAUI has no tray API. A tray would need a Win32/WinForms `NotifyIcon` on Windows and an AppKit
  plug-in on macOS (Catalyst cannot create an `NSStatusItem`); I did not write code I cannot run. Replacement: the unread count is in
  the window title on both platforms and on the dock icon on Mac. Closing the window quits the app (no background mode).
* **Notifications need an API token.** The Electron app can reuse the web session's cookie because the cookie jar is in its own
  process; a WebView2 / WKWebView cookie jar is not reachable from the poller here. A server without a saved token is not polled.
  Tokens are saved under the *Servers* panel, verified against `GET /api/v1/user`, and stored only in the OS secure store: if that is
  unavailable the token is not saved at all (no plain-text fallback).
* **On Mac, the secure store needs a signed build.** Keychain access from Mac Catalyst needs the `keychain-access-groups`
  entitlement, which needs a team identity and provisioning profile. The default `Entitlements.plist` is for local ad-hoc builds and has
  no such entitlement, so there the token cannot be stored (the app says so). For a signed build use
  `-p:CodesignEntitlements=Platforms/MacCatalyst/Entitlements.Signed.plist` with your `CodesignKey` / `CodesignProvision`.
* **Not sandboxed on Mac** (`app-sandbox` false) because Clone starts `git` and writes to a folder the user names. That rules out the
  Mac App Store; it is fine for a Developer ID or local build.
* **Clone asks for the parent folder in a text prompt** (MAUI has no folder picker without an extra package) and afterwards reveals the folder with
  the system. It does not open VS Code.
* **Self-signed certificates and trusted SSO origins** (Electron's per-server options) are not implemented. A sign-in that redirects
  to another site opens in the system browser, where the session cannot return.
* **Windows has no single-instance forwarding.** A `bytebench://` link starts a new copy of the app that handles it. Registration of the
  scheme is an explicit menu action (File > Register bytebench:// Links), which writes `HKCU\Software\Classes\bytebench`.
* **File downloads/uploads and the Web Notifications API** are not wired up in the Mac web view. Sub-frames (iframes) are not
  subject to the allow-list; main-frame navigations and new-window requests are. Permissions on Windows: only notifications (server
  origin) and clipboard read (server and workspace origins); everything else denied.
* No auto-update, installers, signing or release workflow. Workspace origins (`ide-*`, `p<port>-*`) stay in the same web view
  (Electron opens an app window).

## What was verified, and what was not

Verified (run on macOS with the .NET 10.0.402 SDK):

* `dotnet build --no-incremental -warnaserror` of `ByteBench.Desktop.Core` and `ByteBench.Desktop.Core.Tests`: 0 warnings, 0 errors.
* `dotnet test tests/ByteBench.Desktop.Core.Tests`: 153 passed (URL normalization, private-host rules, allow-list incl. lookalike hosts,
  deep links incl. hostile paths and ambiguous names, profile store incl. corrupt/hand-edited files, server-info and notification
  parsing, announce selection, back-off 30/60/120/240/300 s, the poller against a fake HTTP server with a fake clock (bearer token,
  no token leaves with the anonymous server-info call, no redirects followed, legacy server skipped), and `GitClone` including a real
  `git clone` of a local bare repository through the argument list).
* `dotnet build ByteBench.sln -warnaserror` still succeeds with Core and its tests added. `ByteBench.csproj` now excludes
  `desktop-maui/**` from its default globs (like `tests/**` and `cli/**`) so the server does not compile these sources.
* The `.plist` files and `.csproj`/`App.xaml` are well-formed XML (`plutil -lint`, `xmllint`).

**NOT verified: the MAUI project has never been compiled or run.** The `maui-maccatalyst` workload could not be installed (it needs
`sudo`), and `dotnet build desktop-maui/ByteBench.Desktop` stops with `NETSDK1147: ... workloads must be installed: maccatalyst`.
There is no Windows machine here either. So none of the following has been seen working: the UI, the menu and its shortcuts, the
web views, per-profile isolation, navigation blocking in the real web views, notifications, the badge, deep links, token storage,
clone from the UI. Expect compile errors to fix on the first build. The places most likely to need a change, because they rely on
platform API names I wrote from memory:

* Mac: `WKWebsiteDataStore.GetDataStore(NSUuid)` and `RemoveDataStore(NSUuid, Action<NSError>)`; the `WKUIDelegate` JavaScript panel
  overrides; `UNUserNotificationCenterDelegate` overrides; the `OpenUrl` lifecycle hook (a scene-based Catalyst app may deliver URLs
  through a scene delegate instead); whether the app launches un-sandboxed with ad-hoc signing.
* Windows: `Microsoft.Windows.AppNotifications` (unpackaged registration), `KeyboardAccelerator.Key` strings for "Left"/"Right",
  `WebView2.EnsureCoreWebView2Async(env)` being callable before MAUI's own initialisation.
* Both: dynamic `MenuBarItems` rebuilds, `Window.Title` updates, partial properties/methods across the two platform folders.
