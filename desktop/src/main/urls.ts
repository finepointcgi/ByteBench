/** Server URL validation and normalization. Pure: no Electron imports, unit tested. */

export type NormalizeResult =
  | { ok: true; origin: string; host: string; scheme: 'http' | 'https'; privateHost: boolean }
  | { ok: false; error: string; needsHttpConfirmation?: boolean };

/** True for hosts where plain http is acceptable without a prompt: loopback, RFC 1918, link-local, ULA and local-only names. */
export function isPrivateHost(rawHost: string): boolean {
  const host = rawHost.replace(/^\[|\]$/g, '').toLowerCase().replace(/\.$/, '');
  if (host === 'localhost' || host.endsWith('.localhost')) return true;
  if (host.endsWith('.local') || host.endsWith('.lan') || host.endsWith('.internal') || host.endsWith('.home.arpa')) return true;
  const v4 = /^(\d{1,3})\.(\d{1,3})\.(\d{1,3})\.(\d{1,3})$/.exec(host);
  if (v4) {
    const [a, b] = [Number(v4[1]), Number(v4[2])];
    return a === 10 || a === 127 || (a === 172 && b >= 16 && b <= 31) || (a === 192 && b === 168) || (a === 169 && b === 254);
  }
  if (host.includes(':')) {
    return host === '::1' || /^f[cd][0-9a-f]{2}:/.test(host) || /^fe[89ab][0-9a-f]:/.test(host);
  }
  // A single-label name (a corporate intranet host such as "bytebench") never resolves on the public internet.
  return !host.includes('.');
}

/**
 * Turns what the user typed into an origin. Rules: http(s) only; no credentials; query, fragment and path are dropped;
 * a missing scheme means https (http for private hosts); plain http to a non-private host needs `allowInsecureRemote`.
 */
export function normalizeServerUrl(input: string, opts: { allowInsecureRemote?: boolean } = {}): NormalizeResult {
  const text = input.trim();
  if (!text) return { ok: false, error: 'Enter the address of your ByteBench server.' };
  // eslint-disable-next-line no-control-regex
  if (text.length > 2048 || /[\s\u0000-\u001f]/.test(text)) return { ok: false, error: 'That does not look like a web address.' };

  const hasScheme = /^[a-z][a-z0-9+.-]*:\/\//i.test(text);
  let candidate = text;
  if (!hasScheme) {
    // "host:port/…" without a scheme: decide http or https from the host.
    const probe = safeUrl('https://' + text);
    if (!probe) return { ok: false, error: 'That does not look like a web address.' };
    candidate = (isPrivateHost(probe.hostname) ? 'http://' : 'https://') + text;
  }
  const url = safeUrl(candidate);
  if (!url) return { ok: false, error: 'That does not look like a web address.' };
  if (url.protocol !== 'http:' && url.protocol !== 'https:') return { ok: false, error: 'Only http and https addresses are supported.' };
  if (url.username || url.password) return { ok: false, error: 'Do not put a user name or password in the address; sign in on the server page.' };
  if (!url.hostname) return { ok: false, error: 'The address has no host name.' };

  const scheme = url.protocol === 'https:' ? 'https' : 'http';
  const privateHost = isPrivateHost(url.hostname);
  if (scheme === 'http' && !privateHost && !opts.allowInsecureRemote) {
    return {
      ok: false,
      needsHttpConfirmation: true,
      error: 'This address uses plain http on a public host. Traffic, including your password, can be read on the network. Use https, or confirm that you accept this.',
    };
  }
  return { ok: true, origin: url.origin, host: url.hostname, scheme, privateHost };
}

function safeUrl(text: string): URL | null {
  try {
    return new URL(text);
  } catch {
    return null;
  }
}

/** The display name to suggest for a new server: its host name. */
export function suggestName(origin: string): string {
  const u = safeUrl(origin);
  return u ? u.host : origin;
}
