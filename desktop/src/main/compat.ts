import type { ServerInfo } from '../shared/types';

/** The highest server-info apiVersion this desktop build understands. */
export const SUPPORTED_API_VERSION = 1;

export type Compat =
  | { level: 'ok'; info: ServerInfo }
  | { level: 'legacy'; message: string } // a ByteBench server without /api/v1/server-info: works, but without notifications
  | { level: 'newer'; info: ServerInfo; message: string }
  | { level: 'unavailable'; message: string }
  | { level: 'not-bytebench'; message: string };

/** Interprets the answer of GET /api/v1/server-info (HTTP status and parsed JSON body, or null status when the request failed outright). */
export function evaluateServerInfo(status: number | null, body: unknown): Compat {
  if (status === null) return { level: 'unavailable', message: 'The server could not be reached.' };
  if (status >= 500) return { level: 'unavailable', message: `The server answered with an error (HTTP ${status}). It may be starting up or down for maintenance.` };
  const obj = body && typeof body === 'object' ? (body as Record<string, unknown>) : null;
  if (status === 404 || status === 401 || status === 405) {
    // An older ByteBench answers unknown /api paths with a JSON problem document; a random website does not.
    if (obj && typeof obj.title === 'string' && typeof obj.status === 'number') {
      return { level: 'legacy', message: 'This server is older than the desktop app expects: notifications and badges are off until it is updated.' };
    }
    return { level: 'not-bytebench', message: 'This address did not answer like a ByteBench server.' };
  }
  if (status !== 200 || !obj) return { level: 'not-bytebench', message: 'This address did not answer like a ByteBench server.' };
  if (obj.name !== 'ByteBench' || typeof obj.version !== 'string' || typeof obj.apiVersion !== 'number') {
    return { level: 'not-bytebench', message: 'This address did not answer like a ByteBench server.' };
  }
  const info: ServerInfo = {
    name: 'ByteBench',
    version: obj.version,
    apiVersion: obj.apiVersion,
    capabilities: Array.isArray(obj.capabilities) ? obj.capabilities.filter((c): c is string => typeof c === 'string') : [],
    workspaceBaseDomain: typeof obj.workspaceBaseDomain === 'string' ? obj.workspaceBaseDomain : null,
  };
  if (info.apiVersion > SUPPORTED_API_VERSION) {
    return { level: 'newer', info, message: `This server (ByteBench ${info.version}) is newer than this desktop app. Update the desktop app for full support.` };
  }
  return { level: 'ok', info };
}
