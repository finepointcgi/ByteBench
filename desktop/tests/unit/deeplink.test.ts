import { describe, expect, it } from 'vitest';
import { extractDeepLink, isSafePath, parseDeepLink } from '../../src/main/deeplink';

const profiles = [
  { id: 'aaaaaaaa-1111', name: 'Work', origin: 'https://bb.example.com' },
  { id: 'bbbbbbbb-2222', name: 'Local', origin: 'http://localhost:5108' },
];

describe('parseDeepLink', () => {
  it('opens a path in a profile matched by id, name or host', () => {
    expect(parseDeepLink('bytebench://open?server=aaaaaaaa-1111&path=/p/WEB/issues/12', profiles)).toEqual({
      ok: true, profileId: 'aaaaaaaa-1111', path: '/p/WEB/issues/12', url: 'https://bb.example.com/p/WEB/issues/12',
    });
    expect(parseDeepLink('bytebench://open?server=local&path=/', profiles)).toMatchObject({ ok: true, profileId: 'bbbbbbbb-2222' });
    expect(parseDeepLink('bytebench://open?server=bb.example.com&path=/x', profiles)).toMatchObject({ ok: true, profileId: 'aaaaaaaa-1111' });
    expect(parseDeepLink('bytebench://open?server=localhost:5108', profiles)).toMatchObject({ ok: true, path: '/' });
  });
  it('uses the only profile when no server is given, and refuses when ambiguous', () => {
    expect(parseDeepLink('bytebench://open?path=/a', [profiles[0]!])).toMatchObject({ ok: true });
    expect(parseDeepLink('bytebench://open?path=/a', profiles).ok).toBe(false);
  });
  it('never adds or navigates to unknown servers', () => {
    expect(parseDeepLink('bytebench://open?server=evil.example.com&path=/', profiles).ok).toBe(false);
  });
  it.each([
    '//evil.example.com/x',
    '/\\evil.example.com',
    'https://evil.example.com/',
    'javascript:alert(1)',
    '/a\nb',
    '/a\\b',
    '',
  ])('rejects the path %j', (path) => {
    const link = `bytebench://open?server=Work&path=${encodeURIComponent(path)}`;
    const r = parseDeepLink(link, profiles);
    // an empty path means "/" (fine); everything else is refused
    if (path === '') expect(r.ok).toBe(true);
    else expect(r.ok).toBe(false);
  });
  it('rejects other schemes, actions, credentials and oversized links', () => {
    expect(parseDeepLink('https://open?server=Work', profiles).ok).toBe(false);
    expect(parseDeepLink('bytebench://install?server=Work', profiles).ok).toBe(false);
    expect(parseDeepLink('bytebench://u:p@open?server=Work', profiles).ok).toBe(false);
    expect(parseDeepLink('bytebench://open?server=Work&path=/' + 'a'.repeat(5000), profiles).ok).toBe(false);
    expect(parseDeepLink('not a url', profiles).ok).toBe(false);
  });
  it('refuses two profiles with the same name', () => {
    const dup = [...profiles, { id: 'cccccccc-3333', name: 'work', origin: 'https://other.example.com' }];
    expect(parseDeepLink('bytebench://open?server=work', dup).ok).toBe(false);
  });
});

describe('isSafePath / extractDeepLink', () => {
  it('only accepts single-slash server paths', () => {
    expect(isSafePath('/p/KEY?x=1#h', 'https://a.example')).toBe(true);
    expect(isSafePath('//b.example', 'https://a.example')).toBe(false);
    expect(isSafePath('p/KEY', 'https://a.example')).toBe(false);
  });
  it('finds the link in argv', () => {
    expect(extractDeepLink(['electron', '.', 'bytebench://open?server=x'])).toBe('bytebench://open?server=x');
    expect(extractDeepLink(['electron', '--flag'])).toBeUndefined();
  });
});
