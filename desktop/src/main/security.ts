import { shell, type Session, type WebContents } from 'electron';
import { classifyUrl, isSafeExternal, permissionAllowed, type NavContext } from './allowlist';
import { rendererUrlPrefix, wcContexts } from './context';

/** What the hardening hooks need from the rest of the app. */
export interface SecurityHost {
  /** Navigation context (server origin, workspace base, trusted sign-in origins) for a profile; undefined if it was removed. */
  navContext(profileId: string): NavContext | undefined;
  openInProfile(profileId: string, url: string): void;
  openWorkspace(profileId: string, url: string): void;
}

let lastExternalAt = 0;

/** Hand a URL to the OS, at most a few per second (a hostile page must not be able to spam the user's browser). */
export function openExternalSafely(url: string): boolean {
  if (!isSafeExternal(url)) return false;
  const now = Date.now();
  if (now - lastExternalAt < 500) return false;
  lastExternalAt = now;
  void shell.openExternal(url);
  return true;
}

/**
 * Applied to every WebContents the app creates (call from app.on('web-contents-created')). The policy itself lives in allowlist.ts;
 * this only wires it to Electron's events:
 *   - will-navigate / will-redirect / will-frame-navigate: block, or hand off to the browser or a workspace window
 *   - window.open: never creates a window of its own; the target is routed by the same policy
 *   - <webview> is refused
 */
export function hardenWebContents(contents: WebContents, host: SecurityHost): void {
  contents.on('will-attach-webview', (event) => event.preventDefault());

  const decide = (url: string) => {
    const ctx = wcContexts.get(contents.id);
    if (!ctx) return { ctx: undefined, nav: undefined, decision: 'blocked' as const };
    if (ctx.kind === 'ui') return { ctx, nav: undefined, decision: url.startsWith(rendererUrlPrefix) ? ('internal' as const) : ('blocked' as const) };
    const nav = host.navContext(ctx.profileId);
    return { ctx, nav, decision: nav ? classifyUrl(url, nav) : ('blocked' as const) };
  };

  const topLevel = (event: { preventDefault(): void }, url: string) => {
    const { ctx, decision } = decide(url);
    if (!ctx) return event.preventDefault();
    if (ctx.kind === 'ui') return decision === 'internal' ? undefined : event.preventDefault();
    switch (decision) {
      case 'sso':
        return;
      case 'internal':
        if (ctx.kind === 'workspace') {
          event.preventDefault();
          host.openInProfile(ctx.profileId, url);
        }
        return;
      case 'workspace':
        if (ctx.kind === 'workspace') return;
        event.preventDefault();
        host.openWorkspace(ctx.profileId, url);
        return;
      case 'external':
        event.preventDefault();
        openExternalSafely(url);
        return;
      default:
        event.preventDefault();
    }
  };

  contents.on('will-navigate', (event) => topLevel(event, event.url));
  contents.on('will-redirect', (event) => {
    if (event.isMainFrame) topLevel(event, event.url);
    else if (!['internal', 'workspace', 'sso'].includes(decide(event.url).decision)) event.preventDefault();
  });
  contents.on('will-frame-navigate', (event) => {
    if (event.isMainFrame) return; // handled above
    const { decision } = decide(event.url);
    if (!['internal', 'workspace', 'sso'].includes(decision)) event.preventDefault();
  });

  contents.setWindowOpenHandler(({ url }) => {
    const { ctx, decision } = decide(url);
    if (!ctx || ctx.kind === 'ui') return { action: 'deny' };
    switch (decision) {
      case 'internal':
        host.openInProfile(ctx.profileId, url);
        break;
      case 'workspace':
        host.openWorkspace(ctx.profileId, url);
        break;
      case 'sso':
        void contents.loadURL(url);
        break;
      case 'external':
        openExternalSafely(url);
        break;
      default:
        break;
    }
    return { action: 'deny' };
  });
}

/** Permissions: notifications and the clipboard only, and only for the profile's own origins. Everything else is denied. */
export function hardenSession(ses: Session, profileId: string, host: SecurityHost): void {
  const allowed = (permission: string, url: string): boolean => {
    const nav = host.navContext(profileId);
    return !!nav && permissionAllowed(permission, url, nav);
  };
  ses.setPermissionRequestHandler((_wc, permission, callback, details) => callback(allowed(permission, details.requestingUrl)));
  ses.setPermissionCheckHandler((_wc, permission, requestingOrigin) => allowed(permission, requestingOrigin));
  ses.setDevicePermissionHandler(() => false);
}
