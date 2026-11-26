/** A saved ByteBench server. Only the origin is stored: the app never navigates outside it (see allowlist.ts). */
export interface Profile {
  id: string;
  name: string;
  /** Normalized origin, e.g. https://bytebench.example.com or http://localhost:5108 */
  origin: string;
  /** The user opted in to a self-signed certificate for this server. It is pinned by fingerprint on first connect. */
  allowSelfSigned: boolean;
  /** sha256 fingerprint of the pinned certificate ("sha256/BASE64", Electron's format), if the user trusted one. */
  pinnedCert?: string;
  /** Extra https origins the user explicitly trusts for sign-in (SSO identity providers). */
  extraOrigins: string[];
  createdAt: string;
  lastOpenedAt?: string;
  /** Highest notification id already shown, so a restart does not re-announce old ones. */
  lastNotifiedId?: number;
}

export type Appearance = 'system' | 'light' | 'dark';

export interface Settings {
  appearance: Appearance;
  lastProfileId?: string;
  notificationsEnabled: boolean;
}

/** What the picker UI receives: a profile plus whether an API token is stored (never the token). */
export interface ProfileView extends Profile {
  hasToken: boolean;
  unread: number;
}

export interface ProfileInput {
  name: string;
  url: string;
  allowSelfSigned: boolean;
  extraOrigins: string[];
  /** The user confirmed they want plain http to a non-private host. */
  confirmInsecureHttp: boolean;
  /** Optional personal access token; stored encrypted with the OS keychain, never in profiles.json. */
  token?: string;
  clearToken?: boolean;
}

export type Result<T = undefined> = ({ ok: true } & (T extends undefined ? object : { value: T })) | { ok: false; error: string; needsHttpConfirmation?: boolean };

export interface ErrorState {
  profileId: string;
  profileName: string;
  url: string;
  code: string;
  description: string;
  /** Present when the failure is a certificate problem the user may pin. */
  certificate?: { fingerprint: string; subject: string; issuer: string; validExpiry: string; pinned: boolean; canTrust: boolean };
}

export interface ServerInfo {
  name: string;
  version: string;
  apiVersion: number;
  capabilities: string[];
  workspaceBaseDomain?: string | null;
}

/** IPC channels. Only the preloads and ipc.ts refer to these strings. */
export const Channels = {
  profilesList: 'bb:profiles:list',
  profilesAdd: 'bb:profiles:add',
  profilesUpdate: 'bb:profiles:update',
  profilesRemove: 'bb:profiles:remove',
  profilesOpen: 'bb:profiles:open',
  profilesChanged: 'bb:profiles:changed',
  errorState: 'bb:error:state',
  errorRetry: 'bb:error:retry',
  errorTrust: 'bb:error:trust',
  paletteClose: 'bb:palette:close',
  findQuery: 'bb:find:query',
  findClose: 'bb:find:close',
  cloneRepository: 'bb:web:clone',
  webInfo: 'bb:web:info',
} as const;
