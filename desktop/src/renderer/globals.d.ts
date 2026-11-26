interface BbProfile {
  id: string;
  name: string;
  origin: string;
  allowSelfSigned: boolean;
  pinnedCert?: string;
  extraOrigins: string[];
  lastOpenedAt?: string;
  hasToken: boolean;
  unread: number;
}

interface BbInput {
  name: string;
  url: string;
  allowSelfSigned: boolean;
  extraOrigins: string[];
  confirmInsecureHttp: boolean;
  token?: string;
  clearToken?: boolean;
}

type BbResult = { ok: true } | { ok: false; error: string; needsHttpConfirmation?: boolean };

interface BbErrorState {
  profileId: string;
  profileName: string;
  url: string;
  code: string;
  description: string;
  certificate?: { fingerprint: string; subject: string; issuer: string; validExpiry: string; pinned: boolean; canTrust: boolean };
}

interface BbApi {
  platform: string;
  listProfiles(): Promise<{ profiles: BbProfile[]; message?: string }>;
  addProfile(input: BbInput): Promise<BbResult>;
  updateProfile(id: string, input: BbInput): Promise<BbResult>;
  removeProfile(id: string): Promise<boolean>;
  openProfile(id: string): Promise<void>;
  onProfilesChanged(cb: (profiles: BbProfile[]) => void): void;
  getErrorState(): Promise<BbErrorState | null>;
  retry(): Promise<void>;
  trustCertificate(): Promise<void>;
  closePalette(): Promise<void>;
  find(text: string, forward: boolean, next: boolean): Promise<void>;
  closeFind(): Promise<void>;
  onFindResult(cb: (r: { matches: number; active: number }) => void): void;
}

declare const bb: BbApi;
