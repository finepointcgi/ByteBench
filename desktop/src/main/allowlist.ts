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

/** The single label in front of a workspace base domain (ide-… / p3000-…), or null if the host is not directly under one. */
function workspaceLabel(target: URL, server: URL, advertised?: string | null): string | null {
  if (target.protocol !== server.protocol || target.port !== server.port) return null;
  const host = target.hostname.toLowerCase();
  for (const base of workspaceBases(server.hostname, advertised)) {
    if (!host.endsWith('.' + base)) continue;
    const label = host.slice(0, host.length - base.length - 1);
    if (WORKSPACE_LABEL.test(label)) return label; // exactly one label in front of the base, no dots
  }
  return null;
}

export function isWorkspaceUrl(target: URL, server: URL, advertised?: string | null): boolean {
  return workspaceLabel(target, server, advertised) !== null;
}

/** The exact shape of the labels ByteBench mints: ide-{id} and p{port}-{id}, where an id is 20 characters of [a-z2-7]. */
const STRICT_LABEL = /^(?:(ide)|p([0-9]{1,5}))-([a-z2-7]{20})$/;

export interface WorkspaceOrigin {
  kind: 'ide' | 'preview';
  /** The forwarded port, for a preview. */
  port?: number;
  workspaceId: string;
}

/** Which workspace (and port) a URL on one of the server's workspace origins belongs to; null for anything else, including look-alike labels. */
export function parseWorkspaceOrigin(raw: string, ctx: NavContext): WorkspaceOrigin | null {
  const target = parse(raw);
  const server = parse(ctx.serverOrigin);
  if (!target || !server || classifyUrl(raw, ctx) !== 'workspace') return null;
  const label = workspaceLabel(target, server, ctx.advertisedWorkspaceBase);
  const m = label ? STRICT_LABEL.exec(label) : null;
  if (!m) return null;
  if (m[1]) return { kind: 'ide', workspaceId: m[3]! };
  const port = Number(m[2]);
  return port >= 1 && port <= 65535 ? { kind: 'preview', port, workspaceId: m[3]! } : null;
}

/**
 * Whether a URL the server handed back for "open port {port} of workspace {workspaceId}" is exactly that: a single-use entry link
 * (/_bb/enter?ticket=…&next=/) on that workspace's own preview origin for that port. A server (or a middlebox) can't use this call to make the
 * app load some other page, another workspace, the IDE or another port.
 */
export function isPortEntryUrl(raw: string, ctx: NavContext, workspaceId: string, port: number): boolean {
  const o = parseWorkspaceOrigin(raw, ctx);
  if (!o || o.kind !== 'preview' || o.workspaceId !== workspaceId || o.port !== port) return false;
  const u = parse(raw);
  if (!u || u.pathname !== '/_bb/enter' || u.hash) return false;
  const names = [...u.searchParams.keys()].sort();
  const next = u.searchParams.get('next');
  return (
    names.length === 2 && names[0] === 'next' && names[1] === 'ticket' && (u.searchParams.get('ticket') ?? '').length > 0 &&
    next !== null && next.startsWith('/') && !next.startsWith('//') && !next.includes('\\')
  );
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
