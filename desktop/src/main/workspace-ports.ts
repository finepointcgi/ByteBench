/**
 * Workspace ports: the parts of "list a workspace's forwarded ports and open one" that don't need Electron. Pure and unit tested.
 * Everything a server sends back is validated here before the app acts on it (the app trusts nothing a server or a middlebox returns).
 */
import { isPortEntryUrl, type NavContext } from './allowlist';

/** The server capability that says GET /api/v1/workspaces/{id}/ports and POST …/ports/{port}/open exist. */
export const PORTS_CAPABILITY = 'workspace-ports';

const WORKSPACE_ID = /^[a-z2-7]{20}$/;
const MAX_WORKSPACES = 50;
const MAX_PORTS = 200;

export interface WorkspaceSummary {
  id: string;
  repository: string;
  ref: string;
}

export interface ForwardedPort {
  port: number;
  /** Where the port is served, e.g. http://p5173-{id}.localhost:5108 (its own origin). Null when the server can't say. */
  origin: string | null;
}

export interface WorkspacePorts {
  workspace: WorkspaceSummary;
  ports: ForwardedPort[];
}

// eslint-disable-next-line no-control-regex
const text = (v: unknown, max: number): string => (typeof v === 'string' ? v.replace(/[\u0000-\u001f\u007f]/g, ' ').slice(0, max) : '');

/** The running workspaces in GET /api/v1/workspaces (anything else, and anything malformed, is dropped). */
export function parseWorkspaceList(body: unknown): WorkspaceSummary[] {
  if (!Array.isArray(body)) return [];
  const out: WorkspaceSummary[] = [];
  for (const w of body) {
    if (out.length >= MAX_WORKSPACES) break;
    if (!w || typeof w !== 'object') continue;
    const o = w as Record<string, unknown>;
    if (o.state !== 'Running' || typeof o.id !== 'string' || !WORKSPACE_ID.test(o.id)) continue;
    out.push({ id: o.id, repository: `${text(o.project, 40)}/${text(o.repository, 100)}`, ref: text(o.ref, 100) });
  }
  return out;
}

/** The ports in GET /api/v1/workspaces/{id}/ports: valid port numbers only, sorted, without duplicates. */
export function parsePorts(body: unknown, ctx: NavContext, workspaceId: string): ForwardedPort[] {
  if (!Array.isArray(body)) return [];
  const seen = new Map<number, ForwardedPort>();
  for (const p of body) {
    if (seen.size >= MAX_PORTS) break;
    if (!p || typeof p !== 'object') continue;
    const o = p as Record<string, unknown>;
    if (typeof o.port !== 'number' || !Number.isInteger(o.port) || o.port < 1 || o.port > 65535 || seen.has(o.port)) continue;
    seen.set(o.port, { port: o.port, origin: typeof o.origin === 'string' && originMatches(o.origin, ctx, workspaceId, o.port) ? o.origin : null });
  }
  return [...seen.values()].sort((a, b) => a.port - b.port);
}

/** An advertised origin is only shown when it really is that port's origin of that workspace on this server. */
function originMatches(origin: string, ctx: NavContext, workspaceId: string, port: number): boolean {
  try {
    const u = new URL(origin);
    if (u.pathname !== '/' || u.search || u.hash) return false;
    // Reuse the entry-link check with a dummy entry path: same origin rules, same strict label.
    return isPortEntryUrl(`${u.origin}/_bb/enter?ticket=x&next=%2F`, ctx, workspaceId, port);
  } catch {
    return false;
  }
}

/** The entry URL of POST …/ports/{port}/open, or null if it isn't exactly the single-use link for that workspace and port on this server. */
export function parseOpenResponse(body: unknown, ctx: NavContext, workspaceId: string, port: number): string | null {
  if (!body || typeof body !== 'object') return null;
  const url = (body as Record<string, unknown>).url;
  return typeof url === 'string' && url.length <= 2048 && isPortEntryUrl(url, ctx, workspaceId, port) ? url : null;
}

/** A short, local-looking address for display: the host and port of the workspace origin (p5173-….localhost:5108). */
export function displayAddress(origin: string | null): string {
  if (!origin) return '';
  try {
    return new URL(origin).host;
  } catch {
    return '';
  }
}

export function serverSupportsPorts(capabilities: string[] | undefined): boolean {
  return !!capabilities && capabilities.includes(PORTS_CAPABILITY);
}

export interface PortMenuEntry {
  label: string;
  workspaceId: string;
  port: number;
}

/** The flat list a menu shows: one line per forwarded port, grouped by workspace in the order given. */
export function menuEntries(items: WorkspacePorts[]): PortMenuEntry[] {
  return items.flatMap((i) =>
    i.ports.map((p) => ({
      label: `${i.workspace.repository} (${i.workspace.ref}) · port ${p.port}${p.origin ? ' · ' + displayAddress(p.origin) : ''}`,
      workspaceId: i.workspace.id,
      port: p.port,
    })),
  );
}
