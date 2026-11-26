import * as fs from 'node:fs';
import * as os from 'node:os';
import * as path from 'node:path';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { decideCertificate } from '../../src/main/cert';
import { ProfileStore } from '../../src/main/profiles';
import { SecretStore, type Codec } from '../../src/main/secrets';
import type { ProfileInput } from '../../src/shared/types';

let dir: string;
beforeEach(() => { dir = fs.mkdtempSync(path.join(os.tmpdir(), 'bb-desktop-')); });
afterEach(() => { fs.rmSync(dir, { recursive: true, force: true }); });

const input = (over: Partial<ProfileInput> = {}): ProfileInput => ({ name: '', url: 'https://bb.example.com', allowSelfSigned: false, extraOrigins: [], confirmInsecureHttp: false, ...over });

describe('ProfileStore', () => {
  it('adds, persists and reloads profiles', () => {
    const file = path.join(dir, 'profiles.json');
    const s = new ProfileStore(file);
    const r = s.add(input({ name: ' Work ', url: 'bb.example.com/p/X' }));
    expect(r.ok).toBe(true);
    const again = new ProfileStore(file).list();
    expect(again).toHaveLength(1);
    expect(again[0]).toMatchObject({ name: 'Work', origin: 'https://bb.example.com' });
  });
  it('defaults the name to the host and rejects duplicates and bad urls', () => {
    const s = new ProfileStore(path.join(dir, 'p.json'));
    const r = s.add(input());
    expect(r.ok && r.value.name).toBe('bb.example.com');
    expect(s.add(input({ url: 'https://BB.example.com/' })).ok).toBe(false);
    expect(s.add(input({ url: 'javascript:alert(1)' })).ok).toBe(false);
    expect(s.add(input({ url: 'http://public.example.com' }))).toMatchObject({ ok: false, needsHttpConfirmation: true });
    expect(s.add(input({ url: 'http://public.example.com', confirmInsecureHttp: true })).ok).toBe(true);
  });
  it('only allows self-signed trust on https, and forgets the pin when trust is switched off', () => {
    const s = new ProfileStore(path.join(dir, 'p.json'));
    const http = s.add(input({ url: 'http://localhost:5108', allowSelfSigned: true }));
    expect(http.ok && http.value.allowSelfSigned).toBe(false);
    const r = s.add(input({ allowSelfSigned: true }));
    if (!r.ok) throw new Error('add failed');
    s.pinCertificate(r.value.id, 'sha256/AAAA');
    expect(s.get(r.value.id)?.pinnedCert).toBe('sha256/AAAA');
    s.update(r.value.id, input({ allowSelfSigned: false }));
    expect(s.get(r.value.id)?.pinnedCert).toBeUndefined();
  });
  it('does not pin a certificate for a profile without the opt-in', () => {
    const s = new ProfileStore(path.join(dir, 'p.json'));
    const r = s.add(input());
    if (!r.ok) throw new Error('add failed');
    s.pinCertificate(r.value.id, 'sha256/AAAA');
    expect(s.get(r.value.id)?.pinnedCert).toBeUndefined();
  });
  it('validates extra sign-in origins', () => {
    const s = new ProfileStore(path.join(dir, 'p.json'));
    expect(s.add(input({ extraOrigins: ['http://idp.example'] })).ok).toBe(false);
    const r = s.add(input({ extraOrigins: ['idp.example/path', 'https://idp.example'] }));
    expect(r.ok && r.value.extraOrigins).toEqual(['https://idp.example']);
  });
  it('removes profiles and the last-profile setting', () => {
    const s = new ProfileStore(path.join(dir, 'p.json'));
    const r = s.add(input());
    if (!r.ok) throw new Error('add failed');
    s.markOpened(r.value.id);
    expect(s.settings().lastProfileId).toBe(r.value.id);
    expect(s.remove(r.value.id)).toBe(true);
    expect(s.list()).toEqual([]);
    expect(s.settings().lastProfileId).toBeUndefined();
  });
  it('survives a corrupt file and drops malformed entries', () => {
    const file = path.join(dir, 'p.json');
    fs.writeFileSync(file, '{not json');
    expect(new ProfileStore(file).list()).toEqual([]);
    fs.writeFileSync(file, JSON.stringify({ version: 1, profiles: [{ id: 'x' }, { id: 'abcdefgh-1', name: 'ok', origin: 'https://ok.example.com', extraOrigins: [] }, { id: 'abcdefgh-2', origin: 'file:///etc' }], settings: { appearance: 'purple' } }));
    const s = new ProfileStore(file);
    expect(s.list().map((p) => p.origin)).toEqual(['https://ok.example.com']);
    expect(s.settings().appearance).toBe('system');
  });
  it('only moves lastNotifiedId forward', () => {
    const s = new ProfileStore(path.join(dir, 'p.json'));
    const r = s.add(input());
    if (!r.ok) throw new Error('add failed');
    s.setLastNotified(r.value.id, 10);
    s.setLastNotified(r.value.id, 5);
    expect(s.get(r.value.id)?.lastNotifiedId).toBe(10);
  });
  it('writes the profile file with owner-only permissions', () => {
    if (process.platform === 'win32') return;
    const file = path.join(dir, 'p.json');
    new ProfileStore(file).add(input());
    expect(fs.statSync(file).mode & 0o077).toBe(0);
  });
});

describe('SecretStore', () => {
  const codec = (available = true): Codec => ({
    available: () => available,
    encrypt: (s) => Buffer.from(s.split('').reverse().join('')),
    decrypt: (b) => b.toString().split('').reverse().join(''),
  });
  it('stores tokens encrypted, never in plain text', () => {
    const file = path.join(dir, 's.json');
    const s = new SecretStore(file, codec());
    expect(s.set('p1', ' gbp_secret ')).toBe(true);
    expect(fs.readFileSync(file, 'utf8')).not.toContain('gbp_secret');
    expect(new SecretStore(file, codec()).get('p1')).toBe('gbp_secret');
    s.delete('p1');
    expect(s.has('p1')).toBe(false);
  });
  it('refuses to store when the keychain is unavailable', () => {
    const file = path.join(dir, 's.json');
    const s = new SecretStore(file, codec(false));
    expect(s.set('p1', 'tok')).toBe(false);
    expect(fs.existsSync(file)).toBe(false);
    expect(s.get('p1')).toBeUndefined();
  });
});

describe('decideCertificate', () => {
  const base = { origin: 'https://bb.example.com', allowSelfSigned: true } as const;
  it('rejects unless the profile opted in', () => {
    expect(decideCertificate({ ...base, allowSelfSigned: false }, 'bb.example.com', 'sha256/A')).toBe('reject');
  });
  it('asks the first time, then accepts only the pinned certificate', () => {
    expect(decideCertificate(base, 'bb.example.com', 'sha256/A')).toBe('prompt');
    expect(decideCertificate({ ...base, pinnedCert: 'sha256/A' }, 'bb.example.com', 'sha256/A')).toBe('accept');
    expect(decideCertificate({ ...base, pinnedCert: 'sha256/A' }, 'bb.example.com', 'sha256/B')).toBe('reject');
  });
  it('rejects other host names', () => {
    expect(decideCertificate({ ...base, pinnedCert: 'sha256/A' }, 'evil.example', 'sha256/A')).toBe('reject');
  });
});
