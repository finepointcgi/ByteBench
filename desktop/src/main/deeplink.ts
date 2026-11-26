/**
 * bytebench:// deep links. Shape: bytebench://open?server=<profile id | name | host>&path=/p/KEY/issues/12
 * A link can only navigate inside a profile the user already configured; it can never add a server or open another origin.
 */

export const DEEP_LINK_SCHEME = 'bytebench';

export interface DeepLinkProfile {
  id: string;
  name: string;
  origin: string;
}

export type DeepLinkResult =
  | { ok: true; profileId: string; path: string; url: string }
  | { ok: false; reason: string };

const MAX_LINK = 4096;

/** A server-relative path: starts with a single "/", no control characters, stays on the same origin once resolved. */
export function isSafePath(path: string, origin: string): boolean {
  if (path.length === 0 || path.length > 2048) return false;
  if (!path.startsWith('/') || path.startsWith('//') || path.startsWith('/\\')) return false;
  // eslint-disable-next-line no-control-regex
  if (/[\u0000-\u001f\u007f\\]/.test(path)) return false;
  try {
    return new URL(path, origin).origin === new URL(origin).origin;
  } catch {
    return false;
  }
}

export function parseDeepLink(raw: string, profiles: DeepLinkProfile[]): DeepLinkResult {
  if (raw.length > MAX_LINK) return { ok: false, reason: 'The link is too long.' };
  let link: URL;
  try {
    link = new URL(raw);
  } catch {
    return { ok: false, reason: 'The link is not valid.' };
  }
  if (link.protocol !== `${DEEP_LINK_SCHEME}:`) return { ok: false, reason: 'Not a bytebench:// link.' };
  if (link.username || link.password) return { ok: false, reason: 'The link is not valid.' };
  if (link.hostname !== 'open') return { ok: false, reason: 'Unsupported link action.' };

  const server = link.searchParams.get('server');
  const path = link.searchParams.get('path') || '/';

  let matches: DeepLinkProfile[];
  if (server === null || server === '') {
    matches = profiles.length === 1 ? profiles : [];
    if (matches.length === 0) return { ok: false, reason: 'The link does not say which server to use.' };
  } else {
    const wanted = server.toLowerCase();
    matches = profiles.filter((p) => p.id === server);
    if (matches.length === 0) matches = profiles.filter((p) => p.name.toLowerCase() === wanted);
    if (matches.length === 0) matches = profiles.filter((p) => hostOf(p.origin) === wanted || p.origin.toLowerCase() === wanted);
    if (matches.length === 0) return { ok: false, reason: `No saved server matches "${server}". Add the server first.` };
    if (matches.length > 1) return { ok: false, reason: `More than one saved server matches "${server}".` };
  }

  const profile = matches[0]!;
  if (!isSafePath(path, profile.origin)) return { ok: false, reason: 'The link points outside the server.' };
  return { ok: true, profileId: profile.id, path, url: new URL(path, profile.origin).toString() };
}

function hostOf(origin: string): string {
  try {
    return new URL(origin).host.toLowerCase();
  } catch {
    return '';
  }
}

/** Finds a bytebench:// URL in a command line (Windows and Linux deliver deep links as an argument). */
export function extractDeepLink(argv: readonly string[]): string | undefined {
  return argv.find((a) => a.toLowerCase().startsWith(`${DEEP_LINK_SCHEME}://`));
}
