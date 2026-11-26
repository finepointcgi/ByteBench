import { randomUUID } from 'node:crypto';
import * as fs from 'node:fs';
import * as path from 'node:path';
import type { Appearance, Profile, ProfileInput, Result, Settings } from '../shared/types';
import { normalizeServerUrl, suggestName } from './urls';

interface FileShape {
  version: 1;
  profiles: Profile[];
  settings: Settings;
}

const DEFAULT_SETTINGS: Settings = { appearance: 'system', notificationsEnabled: true };
const MAX_PROFILES = 50;

/** Saved servers and app settings in one JSON file (userData/profiles.json). No secrets: tokens live in secrets.ts. */
export class ProfileStore {
  private data: FileShape = { version: 1, profiles: [], settings: { ...DEFAULT_SETTINGS } };

  constructor(private readonly file: string) {
    this.load();
  }

  list(): Profile[] {
    return this.data.profiles.map((p) => ({ ...p, extraOrigins: [...p.extraOrigins] }));
  }

  get(id: string): Profile | undefined {
    const p = this.data.profiles.find((x) => x.id === id);
    return p ? { ...p, extraOrigins: [...p.extraOrigins] } : undefined;
  }

  settings(): Settings {
    return { ...this.data.settings };
  }

  updateSettings(patch: Partial<Settings>): Settings {
    const next = { ...this.data.settings, ...patch };
    if (!(['system', 'light', 'dark'] as Appearance[]).includes(next.appearance)) next.appearance = 'system';
    this.data.settings = next;
    this.save();
    return { ...next };
  }

  add(input: ProfileInput): Result<Profile> {
    if (this.data.profiles.length >= MAX_PROFILES) return { ok: false, error: `At most ${MAX_PROFILES} servers can be saved.` };
    const n = normalizeServerUrl(input.url, { allowInsecureRemote: input.confirmInsecureHttp });
    if (!n.ok) return n;
    if (this.data.profiles.some((p) => p.origin === n.origin)) return { ok: false, error: 'That server is already in the list.' };
    const extra = cleanExtraOrigins(input.extraOrigins);
    if (!extra.ok) return extra;
    const profile: Profile = {
      id: randomUUID(),
      name: cleanName(input.name) || suggestName(n.origin),
      origin: n.origin,
      allowSelfSigned: n.scheme === 'https' && input.allowSelfSigned,
      extraOrigins: extra.value,
      createdAt: new Date().toISOString(),
    };
    this.data.profiles.push(profile);
    this.save();
    return { ok: true, value: { ...profile } };
  }

  /** Name, trust flag and extra origins can change; the origin cannot (a different address is a different server with its own cookies). */
  update(id: string, input: ProfileInput): Result<Profile> {
    const p = this.data.profiles.find((x) => x.id === id);
    if (!p) return { ok: false, error: 'Unknown server.' };
    const extra = cleanExtraOrigins(input.extraOrigins);
    if (!extra.ok) return extra;
    p.name = cleanName(input.name) || p.name;
    p.extraOrigins = extra.value;
    p.allowSelfSigned = p.origin.startsWith('https:') && input.allowSelfSigned;
    if (!p.allowSelfSigned) delete p.pinnedCert; // turning trust off also forgets the pin
    this.save();
    return { ok: true, value: { ...p } };
  }

  remove(id: string): boolean {
    const before = this.data.profiles.length;
    this.data.profiles = this.data.profiles.filter((p) => p.id !== id);
    if (this.data.settings.lastProfileId === id) delete this.data.settings.lastProfileId;
    this.save();
    return this.data.profiles.length !== before;
  }

  pinCertificate(id: string, fingerprint: string): void {
    const p = this.data.profiles.find((x) => x.id === id);
    if (!p || !p.allowSelfSigned) return;
    p.pinnedCert = fingerprint;
    this.save();
  }

  markOpened(id: string): void {
    const p = this.data.profiles.find((x) => x.id === id);
    if (!p) return;
    p.lastOpenedAt = new Date().toISOString();
    this.data.settings.lastProfileId = id;
    this.save();
  }

  setLastNotified(id: string, notificationId: number): void {
    const p = this.data.profiles.find((x) => x.id === id);
    if (!p || (p.lastNotifiedId ?? 0) >= notificationId) return;
    p.lastNotifiedId = notificationId;
    this.save();
  }

  private load(): void {
    let raw: unknown;
    try {
      raw = JSON.parse(fs.readFileSync(this.file, 'utf8'));
    } catch {
      return; // first run, or unreadable: start empty (a corrupt file is replaced on the next save)
    }
    if (!raw || typeof raw !== 'object') return;
    const o = raw as Partial<FileShape>;
    const profiles = Array.isArray(o.profiles) ? o.profiles.map(sanitizeProfile).filter((p): p is Profile => p !== null) : [];
    const s = (o.settings ?? {}) as Partial<Settings>;
    this.data = {
      version: 1,
      profiles,
      settings: {
        appearance: s.appearance === 'light' || s.appearance === 'dark' ? s.appearance : 'system',
        lastProfileId: typeof s.lastProfileId === 'string' ? s.lastProfileId : undefined,
        notificationsEnabled: s.notificationsEnabled !== false,
      },
    };
  }

  private save(): void {
    fs.mkdirSync(path.dirname(this.file), { recursive: true });
    const tmp = `${this.file}.${process.pid}.tmp`;
    fs.writeFileSync(tmp, JSON.stringify(this.data, null, 2), { mode: 0o600 });
    fs.renameSync(tmp, this.file);
  }
}

function cleanName(name: string): string {
  // eslint-disable-next-line no-control-regex
  return (name ?? '').replace(/[\u0000-\u001f\u007f]/g, '').trim().slice(0, 60);
}

/** Extra sign-in origins must be plain https origins. */
export function cleanExtraOrigins(list: readonly string[]): Result<string[]> {
  const out: string[] = [];
  for (const raw of list ?? []) {
    const text = raw.trim();
    if (!text) continue;
    let u: URL;
    try {
      u = new URL(/^[a-z][a-z0-9+.-]*:\/\//i.test(text) ? text : `https://${text}`);
    } catch {
      return { ok: false, error: `"${text}" is not a valid address.` };
    }
    if (u.protocol !== 'https:' || u.username || u.password) return { ok: false, error: 'Trusted sign-in addresses must be https.' };
    if (!out.includes(u.origin)) out.push(u.origin);
  }
  if (out.length > 10) return { ok: false, error: 'At most 10 extra sign-in addresses.' };
  return { ok: true, value: out };
}

function sanitizeProfile(raw: unknown): Profile | null {
  if (!raw || typeof raw !== 'object') return null;
  const p = raw as Partial<Profile>;
  if (typeof p.id !== 'string' || !/^[A-Za-z0-9-]{8,64}$/.test(p.id)) return null;
  if (typeof p.origin !== 'string') return null;
  const n = normalizeServerUrl(p.origin, { allowInsecureRemote: true });
  if (!n.ok || n.origin !== p.origin) return null;
  const extra = cleanExtraOrigins(Array.isArray(p.extraOrigins) ? p.extraOrigins.filter((x): x is string => typeof x === 'string') : []);
  return {
    id: p.id,
    name: typeof p.name === 'string' && p.name ? cleanName(p.name) || suggestName(p.origin) : suggestName(p.origin),
    origin: p.origin,
    allowSelfSigned: p.allowSelfSigned === true && n.scheme === 'https',
    pinnedCert: p.allowSelfSigned === true && typeof p.pinnedCert === 'string' && /^sha256\/[A-Za-z0-9+/=]+$/.test(p.pinnedCert) ? p.pinnedCert : undefined,
    extraOrigins: extra.ok ? extra.value : [],
    createdAt: typeof p.createdAt === 'string' ? p.createdAt : new Date().toISOString(),
    lastOpenedAt: typeof p.lastOpenedAt === 'string' ? p.lastOpenedAt : undefined,
    lastNotifiedId: typeof p.lastNotifiedId === 'number' && p.lastNotifiedId >= 0 ? p.lastNotifiedId : undefined,
  };
}
