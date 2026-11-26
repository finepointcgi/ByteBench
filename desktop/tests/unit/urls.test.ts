import { describe, expect, it } from 'vitest';
import { isPrivateHost, normalizeServerUrl } from '../../src/main/urls';

describe('normalizeServerUrl', () => {
  it('defaults to https and strips paths, queries and default ports', () => {
    const r = normalizeServerUrl('Bytebench.Example.com:443/p/KEY/issues?x=1#y');
    expect(r).toMatchObject({ ok: true, origin: 'https://bytebench.example.com' });
  });
  it('keeps explicit non-default ports', () => {
    expect(normalizeServerUrl('https://bb.example.com:8443/')).toMatchObject({ ok: true, origin: 'https://bb.example.com:8443' });
  });
  it('uses http for a bare private host', () => {
    expect(normalizeServerUrl('localhost:5108')).toMatchObject({ ok: true, origin: 'http://localhost:5108', scheme: 'http' });
    expect(normalizeServerUrl('192.168.1.20:5108')).toMatchObject({ ok: true, origin: 'http://192.168.1.20:5108' });
  });
  it('allows http on private hosts without confirmation', () => {
    expect(normalizeServerUrl('http://10.0.0.5')).toMatchObject({ ok: true });
    expect(normalizeServerUrl('http://[::1]:5108')).toMatchObject({ ok: true });
    expect(normalizeServerUrl('http://bytebench.local')).toMatchObject({ ok: true });
  });
  it('requires confirmation for http on a public host', () => {
    const r = normalizeServerUrl('http://bytebench.example.com');
    expect(r).toMatchObject({ ok: false, needsHttpConfirmation: true });
    expect(normalizeServerUrl('http://bytebench.example.com', { allowInsecureRemote: true })).toMatchObject({ ok: true, origin: 'http://bytebench.example.com' });
  });
  it.each(['', '   ', 'javascript:alert(1)', 'file:///etc/passwd', 'ftp://example.com', 'https://', 'https://user:pw@example.com', 'ex ample.com', 'https://exa\nmple.com'])('rejects %j', (input) => {
    expect(normalizeServerUrl(input).ok).toBe(false);
  });
  it('normalizes IDN hosts to punycode', () => {
    expect(normalizeServerUrl('https://bücher.example')).toMatchObject({ ok: true, origin: 'https://xn--bcher-kva.example' });
  });
});

describe('isPrivateHost', () => {
  it.each(['localhost', 'a.localhost', '127.0.0.1', '10.1.2.3', '172.16.0.1', '172.31.255.255', '192.168.0.1', '169.254.1.1', '::1', 'fd12::1', 'fe80::1', 'intranet', 'x.lan'])('%s is private', (h) => {
    expect(isPrivateHost(h)).toBe(true);
  });
  it.each(['example.com', '8.8.8.8', '172.32.0.1', '172.15.0.1', '2001:db8::1', 'bb.example.org'])('%s is public', (h) => {
    expect(isPrivateHost(h)).toBe(false);
  });
});
