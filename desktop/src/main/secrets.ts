import * as fs from 'node:fs';
import * as path from 'node:path';

/** Encrypts and decrypts with the OS keychain (Electron safeStorage); injected so the store can be unit tested. */
export interface Codec {
  available(): boolean;
  encrypt(plain: string): Buffer;
  decrypt(data: Buffer): string;
}

/**
 * API tokens per profile, encrypted at rest with the OS keychain. If the keychain is unavailable the token is NOT stored
 * (we never fall back to plain text); the user is told and signs in with the web UI's cookie session instead.
 */
export class SecretStore {
  private map: Record<string, string> = {};

  constructor(private readonly file: string, private readonly codec: Codec) {
    try {
      const raw = JSON.parse(fs.readFileSync(file, 'utf8')) as Record<string, unknown>;
      for (const [k, v] of Object.entries(raw)) if (typeof v === 'string') this.map[k] = v;
    } catch {
      /* none yet */
    }
  }

  available(): boolean {
    return this.codec.available();
  }

  has(profileId: string): boolean {
    return profileId in this.map;
  }

  set(profileId: string, token: string): boolean {
    if (!this.codec.available()) return false;
    const trimmed = token.trim();
    if (!trimmed) return false;
    this.map[profileId] = this.codec.encrypt(trimmed).toString('base64');
    this.save();
    return true;
  }

  get(profileId: string): string | undefined {
    const stored = this.map[profileId];
    if (!stored || !this.codec.available()) return undefined;
    try {
      return this.codec.decrypt(Buffer.from(stored, 'base64'));
    } catch {
      return undefined;
    }
  }

  delete(profileId: string): void {
    if (!(profileId in this.map)) return;
    delete this.map[profileId];
    this.save();
  }

  private save(): void {
    fs.mkdirSync(path.dirname(this.file), { recursive: true });
    const tmp = `${this.file}.${process.pid}.tmp`;
    fs.writeFileSync(tmp, JSON.stringify(this.map), { mode: 0o600 });
    fs.renameSync(tmp, this.file);
  }
}
