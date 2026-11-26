/**
 * Navigation policy. Everything a profile's web content tries to open is classified here; the Electron hooks in
 * security.ts only act on the result. Pure and unit tested.
 */

export interface NavContext {
  /** The profile's origin (https://host[:port]). */
  serverOrigin: string;
  /** Workspace base domain the server advertises (server-info), if any. */
  advertisedWorkspaceBase?: string | null;
  /** Origins the user explicitly trusted for sign-in (SSO). */
  extraOrigins?: string[];
}

/**
 * internal   the profile's own server: stays in the app
 * workspace  an IDE or preview origin of the same server (ide-{id}.{base}, p{port}-{id}.{base}): opens in an app window
 * sso        an origin the user trusted for sign-in: stays in the app
 * external   some other web page or mailto: link: handed to the default browser
 * blocked    anything else (file:, javascript:, data:, custom schemes, junk): never opened
 */
export type NavDecision = 'internal' | 'workspace' | 'sso' | 'external' | 'blocked';

const WORKSPACE_LABEL = /^(?:ide|p\d{1,5})-[a-z0-9]+(?:-[a-z0-9]+)*$/;

function parse(raw: string): URL | null {
  try {
    return new URL(raw);
  } catch {
    return null;
  }
}

/** The base domains under which this server's workspace origins can live. */
export function workspaceBases(serverHost: string, advertised?: string | null): string[] {
  const bases = new Set([serverHost.toLowerCase()]);
  const adv = advertised?.trim().toLowerCase().replace(/^\./, '');
  // The advertised base must be the server's own host or a parent of it, and not a bare public suffix like "com".
  if (adv && /^[a-z0-9.-]+$/.test(adv) && (adv === 'localhost' || adv.includes('.')) && (serverHost === adv || serverHost.endsWith('.' + adv))) {
    bases.add(adv);
  }
  return [...bases];
}

export function isWorkspaceUrl(target: URL, server: URL, advertised?: string | null): boolean {
  if (target.protocol !== server.protocol || target.port !== server.port) return false;
  const host = target.hostname.toLowerCase();
  for (const base of workspaceBases(server.hostname, advertised)) {
    if (!host.endsWith('.' + base)) continue;
    const label = host.slice(0, host.length - base.length - 1);
    if (WORKSPACE_LABEL.test(label)) return true; // exactly one label in front of the base, no dots
  }
  return false;
}

export function classifyUrl(raw: string, ctx: NavContext): NavDecision {
  const target = parse(raw);
  const server = parse(ctx.serverOrigin);
  if (!target || !server) return 'blocked';
  if (target.protocol === 'mailto:') return 'external';
  if (target.protocol !== 'http:' && target.protocol !== 'https:') return 'blocked';
  if (target.username || target.password) return 'blocked';
  if (target.origin === server.origin) return 'internal';
  if (isWorkspaceUrl(target, server, ctx.advertisedWorkspaceBase)) return 'workspace';
  if (target.protocol === 'https:' && (ctx.extraOrigins ?? []).includes(target.origin)) return 'sso';
  return 'external';
}

/** Whether the app may hand this URL to the operating system (default browser or mail client). */
export function isSafeExternal(raw: string): boolean {
  const u = parse(raw);
  return !!u && (u.protocol === 'https:' || u.protocol === 'http:' || u.protocol === 'mailto:') && !u.username && !u.password;
}

export type PermissionKind = 'notifications' | 'clipboard-read' | 'clipboard-sanitized-write' | string;

/**
 * Permission policy: only notifications and the clipboard, and only for the server (notifications) or the server and its workspace
 * origins (clipboard, which the IDE needs). Everything else is denied.
 */
export function permissionAllowed(permission: PermissionKind, requestingUrl: string, ctx: NavContext): boolean {
  const decision = classifyUrl(requestingUrl, ctx);
  if (permission === 'notifications') return decision === 'internal';
  if (permission === 'clipboard-read' || permission === 'clipboard-sanitized-write') return decision === 'internal' || decision === 'workspace';
  return false;
}
