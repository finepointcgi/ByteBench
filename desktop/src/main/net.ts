import type { Session } from 'electron';
import { evaluateServerInfo, type Compat } from './compat';

export interface JsonResponse {
  status: number | null;
  body: unknown;
  /** Error text from the network layer (net::ERR_…), when the request failed outright. */
  error?: string;
}

/**
 * GET JSON from a ByteBench server through a profile's own session (so its cookies, proxy settings and pinned certificate apply).
 * With a token, the cookie jar is not used. Never throws.
 */
export async function getJson(ses: Session, origin: string, pathAndQuery: string, opts: { token?: string; timeoutMs?: number } = {}): Promise<JsonResponse> {
  return requestJson(ses, origin, 'GET', pathAndQuery, opts);
}

/** POST an empty JSON object (an action endpoint such as "open this port"). Same rules as {@link getJson}; a cookie session also sends a JSON content type, which the API requires for cookie writes. */
export async function postJson(ses: Session, origin: string, pathAndQuery: string, opts: { token?: string; timeoutMs?: number } = {}): Promise<JsonResponse> {
  return requestJson(ses, origin, 'POST', pathAndQuery, opts);
}

async function requestJson(ses: Session, origin: string, method: 'GET' | 'POST', pathAndQuery: string, opts: { token?: string; timeoutMs?: number }): Promise<JsonResponse> {
  const controller = new AbortController();
  const timer = setTimeout(() => controller.abort(), opts.timeoutMs ?? 8000);
  try {
    const res = await ses.fetch(new URL(pathAndQuery, origin).toString(), {
      method,
      body: method === 'POST' ? '{}' : undefined,
      headers: {
        Accept: 'application/json',
        ...(method === 'POST' ? { 'Content-Type': 'application/json' } : {}),
        ...(opts.token ? { Authorization: `Bearer ${opts.token}` } : {}),
      },
      credentials: opts.token ? 'omit' : 'include',
      redirect: 'manual', // a server must not redirect an API call somewhere else (e.g. to a login page on another origin)
      signal: controller.signal,
    });
    const text = await res.text();
    let body: unknown = null;
    try {
      body = text ? JSON.parse(text) : null;
    } catch {
      body = null;
    }
    return { status: res.status, body };
  } catch (e) {
    return { status: null, body: null, error: e instanceof Error ? e.message : String(e) };
  } finally {
    clearTimeout(timer);
  }
}

export async function probeServer(ses: Session, origin: string): Promise<{ compat: Compat; error?: string }> {
  const r = await getJson(ses, origin, '/api/v1/server-info', { timeoutMs: 8000 });
  return { compat: evaluateServerInfo(r.status, r.body), error: r.error };
}

/** Turns a Chromium error string ("net::ERR_NAME_NOT_RESOLVED") into something a person can act on. */
export function describeNetError(code: string): string {
  const c = code.replace(/^net::/, '');
  if (c.includes('NAME_NOT_RESOLVED')) return 'The server name could not be found. Check the address and your network connection.';
  if (c.includes('CONNECTION_REFUSED')) return 'The connection was refused. The server may be down, or the address or port may be wrong.';
  if (c.includes('TIMED_OUT') || c.includes('CONNECTION_TIMED_OUT')) return 'The server took too long to answer.';
  if (c.includes('INTERNET_DISCONNECTED') || c.includes('NETWORK_CHANGED')) return 'You appear to be offline.';
  if (c.includes('CERT_') || c.includes('SSL_')) return 'The security certificate of this server could not be verified.';
  if (c.includes('CONNECTION_RESET') || c.includes('CONNECTION_CLOSED') || c.includes('EMPTY_RESPONSE')) return 'The connection to the server was interrupted.';
  return 'The server could not be reached.';
}

export function isCertError(code: string): boolean {
  return /ERR_CERT_|ERR_SSL_|ERR_BAD_SSL/.test(code);
}
