# ByteBench desktop app

An Electron app that opens any ByteBench server (yours, `https://bytebench.example.com`, or `http://localhost:5108`) in its own
window, so ByteBench feels like a native application: several saved servers with separate sign-ins, a quick switcher,
OS notifications and an unread badge, a tray or menu-bar item, `bytebench://` links, and "Clone with ByteBench".

The app is a thin shell. It loads the server's own web UI; nothing of ByteBench's logic is duplicated here.

## Using it

1. Start the app. On first run it asks for a server address (for example `https://bytebench.example.com` or `localhost:5108`).
   Plain `http` is accepted without a prompt for localhost and private networks; for any other host you must confirm it.
2. Sign in on the server page as you would in a browser. Each server has its own isolated cookie store.
3. Switch servers with **File > Switch Server** (Cmd/Ctrl+K), the **Servers** menu (Cmd/Ctrl+1..9), or the tray menu.

| Shortcut | Action |
| --- | --- |
| Cmd/Ctrl+K | Switch server |
| Cmd/Ctrl+N | New window |
| Cmd/Ctrl+Shift+N | Add or manage servers |
| Cmd/Ctrl+F | Find in page |
| Cmd/Ctrl+R, +Shift+R | Reload, hard reload |
| Cmd+[ / Cmd+] (Alt+Left / Alt+Right) | Back, forward |
| Cmd/Ctrl + `+` `-` `0` | Zoom |
| Cmd/Ctrl+Shift+C | Clone the current repository |

The app follows the OS light/dark setting (**View > Appearance** can force light or dark). The web UI's own theme menu keeps working.

### Notifications and badge

The app polls `GET /api/v1/notifications` of every saved server about every 30 seconds (backing off to 5 minutes while a server is
down). It uses your signed-in web session, or an API token if you saved one in the server's settings. New unread items become OS
notifications (clicking opens the item); the unread total is the dock badge (macOS/Linux), the taskbar overlay (Windows) and the
tray tooltip. A server without `GET /api/v1/server-info` (an older ByteBench) works but has notifications switched off.
On Windows and Linux, closing the window keeps the app running in the tray while you have servers and notifications on; use the
tray menu to quit.

### `bytebench://` links

`bytebench://open?server=<id|name|host>&path=/p/KEY/issues/12` opens a path of a server you already added. The server is matched
by profile id, name or host; the path must be a server-relative path (a single leading `/`). A link can never add a server or
navigate to another origin. Links are registered by installed (packaged) builds only.

### Clone with ByteBench

**File > Clone This Repository** (or `window.byteBenchDesktop.cloneRepository()` from the web UI) asks for a folder with the
native picker, runs `git clone -- <server>/git/KEY/slug.git <folder>/slug` (an argument array, no shell), then opens the folder in
VS Code (`code` on `PATH`, or the macOS app), or `$VISUAL`/`$EDITOR` if it is a known graphical editor, else reveals it. If you
stored an API token for the server it is passed to git through environment config (never on the command line).

### Workspaces

Workspace pages (`/workspaces`) work as in the browser. The IDE and previews live on `ide-*.{base}` and `p<port>-*.{base}` origins
of the same server; those open in an app window that shares the server's session. Other sites open in your default browser.

### Self-signed certificates and single sign-on

* **Self-signed:** tick "Trust a self-signed certificate" for a server. On the first failed connection the error page shows the
  certificate's fingerprint; "Review and trust" asks for a native confirmation and then pins that exact certificate. A different
  certificate later is refused (shown as a possible interception), and turning the option off forgets the pin.
* **SSO:** a server that signs in through another site (OIDC/SAML) needs that site listed under "Trusted sign-in addresses" for
  the server (https origins only); otherwise the redirect is handed to your default browser, where the session cannot return.

## Develop

Requirements: Node 22+ (24 recommended) and npm.

```
cd desktop
npm ci
npm run build          # tsc + copy html/css into dist/
npm start              # build and run against your own server
npm run typecheck && npm run lint && npm test
npm run test:e2e       # needs the .NET SDK: starts a scratch ByteBench server and drives the real app
```

Layout: `src/main` (main process; pure modules `urls`, `allowlist`, `deeplink`, `profiles`, `cert`, `compat`, `clone`,
`notify-logic` are unit tested without Electron), `src/preload` (two tiny sandboxed preloads), `src/renderer` (the app's own
pages: picker, error page, switcher, find bar), `tests/unit`, `tests/e2e`.

For a side-by-side dev profile use `npm start -- --user-data-dir=/tmp/bb-dev`. `BYTEBENCH_DISABLE_UPDATE=1` turns updates off.

## Build installers

```
npm run dist:dir   # unpacked app for the current OS in release/ (quick check)
npm run dist       # dmg+zip (mac, arm64 and x64), nsis (windows), AppImage+deb (linux): build on that OS
```

On Apple Silicon an unsigned build must at least be ad-hoc signed or macOS kills it at launch: after `dist:dir` run
`codesign --force --deep -s - release/mac-arm64/ByteBench.app` (CI does this with `-c.mac.identity=-` when no certificate secret exists).
Only the macOS arm64 `--dir` build has been exercised by the author; the other targets are built by CI on their own runners.

### Signing and notarization (optional)

Unsigned builds work; macOS shows a Gatekeeper warning and **auto-update on macOS requires a signed app**.

| Variable | Purpose |
| --- | --- |
| `CSC_LINK`, `CSC_KEY_PASSWORD` | macOS Developer ID certificate (base64 .p12) |
| `APPLE_ID`, `APPLE_APP_SPECIFIC_PASSWORD`, `APPLE_TEAM_ID` | notarization (set `mac.notarize: true` in `electron-builder.yml`) |
| `WIN_CSC_LINK`, `WIN_CSC_KEY_PASSWORD` | Windows code-signing certificate |

The release workflow passes these through from repository secrets when they exist.

## Release and auto-update

Push a tag `desktop-v<semver>` (for example `desktop-v0.2.0`). `.github/workflows/desktop-release.yml` builds installers on macOS,
Windows and Ubuntu and attaches them to a GitHub release named after the tag. Stable (non-prerelease) tags also refresh the rolling
release **`desktop-latest`**, which holds the newest installers and the `latest*.yml` metadata. Installed apps read their updates
from `https://github.com/finepointcgi/ByteBench/releases/download/desktop-latest` (a generic feed), so the CLI's `cli-v*`
releases can never be mistaken for a desktop update. Updates are disabled in development and in unpackaged runs.

## Security notes

* Renderers: `contextIsolation` on, `nodeIntegration` off, `sandbox` on, `webviewTag` off (and `will-attach-webview` refused),
  Chromium sandbox forced with `app.enableSandbox()`. Packaged builds flip the Electron fuses (no `RUN_AS_NODE`, no
  `NODE_OPTIONS`, no `--inspect`, load only from the asar, encrypted cookies).
* Each server has its own persistent session partition, so cookies, storage and cache are isolated per server. Removing a server
  deletes them.
* Navigation policy (`src/main/allowlist.ts`, wired in `security.ts` to every WebContents): the server's origin stays in the app;
  same-server workspace origins open in an app window; user-trusted SSO origins stay in the app; other http(s) and `mailto:` links
  go to the default browser (rate limited); everything else (`file:`, `javascript:`, `data:`, `blob:`, custom schemes) is
  blocked. `window.open` never creates a window by itself; it is routed by the same policy. Sub-frames and redirects are checked too.
* Permissions: only notifications (the server origin) and clipboard (server and workspace origins) are granted; everything else,
  including devices, is denied in every session.
* IPC: two preloads with a handful of calls each. Every handler verifies the sender: app pages must be the app's own
  `file://` pages in the top frame of a WebContents the app created; the server bridge only answers the top frame of a profile whose
  URL is on that profile's origin. The preloads accept no URLs or paths to open; clone only accepts repository paths matching strict patterns.
* Local pages carry a strict CSP (`default-src 'none'; script-src 'self'`), render text with `textContent`, and run no remote code.
* Tokens are stored with Electron `safeStorage` (OS keychain). If no keychain exists (Linux without a secret service) tokens are
  not stored at all; there is no plaintext fallback. `profiles.json` holds no secrets and is written with mode 0600.
* Certificates: the verification callback is overridden only for a profile that opted in, only for that host, and only for the
  exact pinned fingerprint; trusting a certificate needs a native confirmation dialog that a web page cannot trigger.
* The preload on server pages exposes `window.byteBenchDesktop` (`cloneRepository`, `getInfo`, `isDesktop`, `platform`). Cloning
  always shows a native folder picker.

## Server requirements

Works with any ByteBench server. For notifications and the compatibility check the server needs `GET /api/v1/server-info`
(anonymous: name, version, API version, capabilities, workspace base domain) and `GET /api/v1/notifications` (your own, with
`unread`, `afterId`, `limit`) plus `POST /api/v1/notifications/{id}/read`, added together with the desktop app. Older servers
still load in the app.

## Limits

Windows and Linux builds, code signing, notarization and auto-update are configured but are only verified by the release workflow.
Web Push (service worker) is not available in Electron; the notification poller replaces it.
