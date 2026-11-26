import { BrowserWindow, Notification, app, dialog, nativeTheme, screen, session, shell, webContents, type Rectangle, type Session } from 'electron';
import { execFile, spawn } from 'node:child_process';
import * as fs from 'node:fs';
import * as path from 'node:path';
import type { Appearance, ErrorState, Profile, ProfileInput, ProfileView, Result } from '../shared/types';
import { Channels } from '../shared/types';
import type { NavContext } from './allowlist';
import { decideCertificate } from './cert';
import { cloneArgs, cloneEnv, cloneUrl, editorFromEnvironment, findOnPath, repoFromPath, targetDirectory } from './clone';
import type { Compat } from './compat';
import { partitionFor, wcContexts } from './context';
import { parseDeepLink } from './deeplink';
import { describeNetError, getJson, isCertError, probeServer } from './net';
import { Notifier } from './notifier';
import { ProfileStore } from './profiles';
import { SecretStore } from './secrets';
import { hardenSession, type SecurityHost } from './security';
import { AppWindow, type WindowHost } from './windows';

interface PendingCert {
  fingerprint: string;
  subject: string;
  issuer: string;
  validExpiry: string;
  hostname: string;
}

interface WindowState extends Rectangle {
  maximized?: boolean;
}

/** The application: owns the stores, the windows, the sessions and the notifier. */
export class Controller implements WindowHost, SecurityHost {
  readonly windows = new Set<AppWindow>();
  readonly notifier: Notifier;
  private readonly configured = new Set<string>();
  private readonly compatCache = new Map<string, Compat>();
  private readonly pendingCerts = new Map<string, PendingCert>();
  private readonly warned = new Set<string>();
  private readonly workspaceWindows = new Set<BrowserWindow>();
  private lastFocused?: AppWindow;
  private stateFile: string;
  onProfilesChanged: () => void = () => undefined;
  onUnread: (total: number) => void = () => undefined;

  constructor(readonly store: ProfileStore, readonly secrets: SecretStore, userData: string) {
    this.stateFile = path.join(userData, 'window-state.json');
    this.notifier = new Notifier({
      profiles: () => this.store.list(),
      enabled: () => this.store.settings().notificationsEnabled,
      session: (p) => this.sessionFor(p),
      token: (id) => this.secrets.get(id),
      compat: (p) => this.compatCache.get(p.id),
      setLastNotified: (id, n) => this.store.setLastNotified(id, n),
      openProfile: (id, p) => this.openProfile(id, p),
      unreadChanged: (total) => {
        this.onUnread(total);
        this.broadcastProfiles();
      },
    });
  }

  // ---------------------------------------------------------------- profiles

  views(): ProfileView[] {
    return this.store.list().map((p) => ({ ...p, hasToken: this.secrets.has(p.id), unread: this.notifier.unreadFor(p.id) }));
  }

  addProfile(input: ProfileInput): Result<Profile> {
    const r = this.store.add(input);
    if (r.ok) this.afterProfileWrite(r.value.id, input);
    return r;
  }

  updateProfile(id: string, input: ProfileInput): Result<Profile> {
    const r = this.store.update(id, input);
    if (r.ok) this.afterProfileWrite(id, input);
    return r;
  }

  private afterProfileWrite(id: string, input: ProfileInput): void {
    if (input.clearToken) this.secrets.delete(id);
    if (input.token?.trim()) {
      if (!this.secrets.set(id, input.token)) {
        void dialog.showMessageBox({ type: 'warning', message: 'The token was not saved', detail: 'The operating system keychain is not available, and ByteBench never stores tokens unencrypted. Notifications use your signed-in web session instead.' });
      }
    }
    this.broadcastProfiles();
    this.notifier.sync();
  }

  async removeProfile(id: string): Promise<void> {
    const profile = this.store.get(id);
    if (!profile) return;
    for (const w of [...this.windows]) w.destroyProfileView(id);
    for (const w of [...this.workspaceWindows]) if (wcContexts.get(w.webContents.id)?.kind === 'workspace') w.destroy();
    this.store.remove(id);
    this.secrets.delete(id);
    this.compatCache.delete(id);
    this.pendingCerts.delete(id);
    this.configured.delete(id);
    // Forget the cookies and caches of that server.
    const ses = session.fromPartition(partitionFor(id));
    await ses.clearStorageData().catch(() => undefined);
    await ses.clearCache().catch(() => undefined);
    this.broadcastProfiles();
    this.notifier.sync();
  }

  broadcastProfiles(): void {
    this.onProfilesChanged();
    for (const [id, ctx] of wcContexts) {
      if (ctx.kind !== 'ui') continue;
      const wc = webContents.fromId(id);
      if (wc && !wc.isDestroyed()) wc.send(Channels.profilesChanged, this.views());
    }
  }

  setAppearance(a: Appearance): void {
    this.store.updateSettings({ appearance: a });
    nativeTheme.themeSource = a;
  }

  // ---------------------------------------------------------------- sessions

  sessionFor(profile: Profile): Session {
    const ses = session.fromPartition(partitionFor(profile.id));
    if (!this.configured.has(profile.id)) {
      this.configured.add(profile.id);
      this.configureSession(ses, profile.id);
    }
    return ses;
  }

  private configureSession(ses: Session, profileId: string): void {
    hardenSession(ses, profileId, this);
    ses.setUserAgent(`${ses.getUserAgent().replace(/\sElectron\/\S+/, '')} ByteBenchDesktop/${app.getVersion()}`);
    ses.setCertificateVerifyProc((request, callback) => {
      if (request.verificationResult === 'OK') return callback(0);
      const profile = this.store.get(profileId);
      const cert = request.certificate;
      const info: PendingCert = { fingerprint: cert.fingerprint, subject: cert.subjectName, issuer: cert.issuerName, validExpiry: String(cert.validExpiry), hostname: request.hostname };
      this.pendingCerts.set(profileId, info);
      if (profile && decideCertificate(profile, request.hostname, cert.fingerprint) === 'accept') return callback(0);
      return callback(-2); // refuse; the error page offers pinning when the profile allows it
    });
    ses.on('will-download', (_e, item) => {
      // Downloads from a server page go to the user's chosen location (Electron shows the save dialog). Nothing is opened automatically.
      item.on('done', (_ev, state) => {
        if (state === 'completed' && Notification.isSupported()) new Notification({ title: 'Download finished', body: path.basename(item.getSavePath()) }).show();
      });
    });
  }

  // SecurityHost
  navContext(profileId: string): NavContext | undefined {
    const p = this.store.get(profileId);
    if (!p) return undefined;
    const compat = this.compatCache.get(profileId);
    return { serverOrigin: p.origin, extraOrigins: p.extraOrigins, advertisedWorkspaceBase: compat && 'info' in compat ? compat.info.workspaceBaseDomain : undefined };
  }

  openInProfile(profileId: string, url: string): void {
    const p = this.store.get(profileId);
    if (!p) return;
    const u = new URL(url);
    if (u.origin !== p.origin) return;
    this.openProfile(profileId, u.pathname + u.search + u.hash);
  }

  openWorkspace(profileId: string, url: string): void {
    const p = this.store.get(profileId);
    if (!p) return;
    const win = new BrowserWindow({
      width: 1280,
      height: 860,
      title: `Workspace: ${p.name}`,
      webPreferences: { partition: partitionFor(p.id), sandbox: true, contextIsolation: true, nodeIntegration: false, webviewTag: false },
    });
    wcContexts.set(win.webContents.id, { kind: 'workspace', profileId: p.id });
    this.workspaceWindows.add(win);
    const id = win.webContents.id;
    win.on('closed', () => {
      this.workspaceWindows.delete(win);
      wcContexts.delete(id);
    });
    this.sessionFor(p);
    void win.loadURL(url);
  }

  // ---------------------------------------------------------------- WindowHost

  prepareProfile(profileId: string): Profile | undefined {
    const p = this.store.get(profileId);
    if (p) this.sessionFor(p);
    return p;
  }

  async probe(profile: Profile): Promise<ErrorState | null> {
    const { compat, error } = await probeServer(this.sessionFor(profile), profile.origin);
    const base = { profileId: profile.id, profileName: profile.name, url: profile.origin };
    if (compat.level === 'unavailable') {
      const code = error ?? 'net::ERR_FAILED';
      return {
        ...base,
        code,
        description: error ? describeNetError(error) : compat.message,
        certificate: isCertError(code) ? this.certificateFor(profile, code) : undefined,
      };
    }
    if (compat.level === 'not-bytebench') return { ...base, code: 'NOT_BYTEBENCH', description: compat.message };
    this.compatCache.set(profile.id, compat);
    if ((compat.level === 'legacy' || compat.level === 'newer') && !this.warned.has(profile.id)) {
      this.warned.add(profile.id);
      void dialog.showMessageBox({ type: 'info', message: profile.name, detail: compat.message });
    }
    return null;
  }

  certificateFor(profile: Profile, _code: string): ErrorState['certificate'] {
    const c = this.pendingCerts.get(profile.id);
    if (!c) return undefined;
    return {
      fingerprint: c.fingerprint,
      subject: c.subject,
      issuer: c.issuer,
      validExpiry: c.validExpiry,
      pinned: !!profile.pinnedCert,
      canTrust: profile.allowSelfSigned && decideCertificate(profile, c.hostname, c.fingerprint) === 'prompt',
    };
  }

  /** The error page's "Trust this certificate": confirm natively (a page can never trigger this by itself), then pin and retry. */
  async trustCertificate(owner: AppWindow, profileId: string): Promise<void> {
    const profile = this.store.get(profileId);
    const pending = this.pendingCerts.get(profileId);
    if (!profile || !pending || decideCertificate(profile, pending.hostname, pending.fingerprint) !== 'prompt') return;
    const answer = await dialog.showMessageBox(owner.win, {
      type: 'warning',
      buttons: ['Cancel', 'Trust this certificate'],
      defaultId: 0,
      cancelId: 0,
      message: `Trust this certificate for ${profile.name}?`,
      detail:
        `Only continue if you can confirm this fingerprint with the server's administrator.\n\n` +
        `Server: ${pending.hostname}\nSubject: ${pending.subject}\nIssuer: ${pending.issuer}\nSHA-256: ${pending.fingerprint}\n\n` +
        `ByteBench will accept exactly this certificate for this server and refuse any other one.`,
    });
    if (answer.response !== 1) return;
    this.store.pinCertificate(profileId, pending.fingerprint);
    this.sessionFor(profile).closeAllConnections().catch(() => undefined);
    this.broadcastProfiles();
    await owner.retry();
  }

  markOpened(profileId: string): void {
    this.store.markOpened(profileId);
    this.broadcastProfiles();
  }

  windowClosed(window: AppWindow): void {
    this.windows.delete(window);
    if (this.lastFocused === window) this.lastFocused = undefined;
  }

  stateFor(): WindowState {
    const fallback: WindowState = { x: 0, y: 0, width: 1360, height: 880 };
    try {
      const o = JSON.parse(fs.readFileSync(this.stateFile, 'utf8')) as Partial<WindowState>;
      if ([o.x, o.y, o.width, o.height].every((n) => typeof n === 'number' && Number.isFinite(n)) && o.width! >= 400 && o.height! >= 300) {
        // Cascade extra windows so they do not sit exactly on top of each other.
        const offset = this.windows.size * 28;
        return { x: o.x! + offset, y: o.y! + offset, width: o.width!, height: o.height!, maximized: o.maximized === true && this.windows.size === 0 };
      }
    } catch {
      /* first run */
    }
    const wa = screen.getPrimaryDisplay().workArea;
    return { ...fallback, x: wa.x + Math.round((wa.width - fallback.width) / 2), y: wa.y + Math.round((wa.height - fallback.height) / 2) };
  }

  saveState(bounds: Rectangle, maximized: boolean): void {
    try {
      fs.mkdirSync(path.dirname(this.stateFile), { recursive: true });
      fs.writeFileSync(this.stateFile, JSON.stringify({ ...bounds, maximized }));
    } catch {
      /* not critical */
    }
  }

  // ---------------------------------------------------------------- windows

  createWindow(): AppWindow {
    const w = new AppWindow(this);
    this.windows.add(w);
    w.win.on('focus', () => (this.lastFocused = w));
    this.lastFocused = w;
    return w;
  }

  focusedWindow(): AppWindow | undefined {
    const focused = BrowserWindow.getFocusedWindow();
    for (const w of this.windows) if (w.win === focused || w.win === focused?.getParentWindow()) return w;
    return this.lastFocused && !this.lastFocused.win.isDestroyed() ? this.lastFocused : [...this.windows][0];
  }

  windowById(id: number): AppWindow | undefined {
    for (const w of this.windows) if (w.id === id) return w;
    return undefined;
  }

  /** Open a profile (and path) in the focused window, creating one if none is open. */
  openProfile(profileId: string, pathAndQuery?: string): void {
    const w = this.focusedWindow() ?? this.createWindow();
    w.focus();
    void w.openProfile(profileId, pathAndQuery);
  }

  newWindow(profileId?: string): void {
    const w = this.createWindow();
    const id = profileId ?? this.focusedProfileId() ?? this.store.settings().lastProfileId;
    if (id && this.store.get(id)) void w.openProfile(id);
    else w.showPicker();
  }

  private focusedProfileId(): string | undefined {
    return this.focusedWindow()?.activeProfileId;
  }

  showPicker(message?: string): void {
    (this.focusedWindow() ?? this.createWindow()).showPicker(message);
    this.focusedWindow()?.focus();
  }

  startup(): void {
    const w = this.createWindow();
    const last = this.store.settings().lastProfileId;
    const profiles = this.store.list();
    if (profiles.length === 0) w.showPicker();
    else if (last && this.store.get(last)) void w.openProfile(last);
    else if (profiles.length === 1) void w.openProfile(profiles[0]!.id);
    else w.showPicker();
  }

  // ---------------------------------------------------------------- deep links

  handleDeepLink(raw: string): void {
    const r = parseDeepLink(raw, this.store.list().map((p) => ({ id: p.id, name: p.name, origin: p.origin })));
    if (!r.ok) {
      void dialog.showMessageBox({ type: 'warning', message: 'Cannot open this link', detail: r.reason });
      if (this.windows.size === 0) this.startup();
      return;
    }
    this.openProfile(r.profileId, new URL(r.url).pathname + new URL(r.url).search + new URL(r.url).hash);
  }

  // ---------------------------------------------------------------- clone

  /** Clone a repository of the active profile into a folder the user picks, then open it in an editor. */
  async cloneRepository(profileId: string, pagePath: string | undefined): Promise<{ ok: boolean; message: string }> {
    const profile = this.store.get(profileId);
    if (!profile) return { ok: false, message: 'Unknown server.' };
    const repo = repoFromPath(pagePath ?? '');
    if (!repo) return { ok: false, message: 'Open a repository page first (a page under /p/KEY/repos/NAME).' };
    const git = findOnPath(['git'], this.searchPath(), process.platform === 'win32');
    if (!git) return { ok: false, message: 'Git was not found. Install Git and try again.' };

    const parent = this.focusedWindow()?.win;
    const pick = await dialog.showOpenDialog(parent ?? new BrowserWindow({ show: false }), {
      title: `Choose a folder to clone ${repo.slug} into`,
      buttonLabel: 'Clone here',
      properties: ['openDirectory', 'createDirectory'],
    });
    const chosen = pick.filePaths[0];
    if (pick.canceled || !chosen) return { ok: false, message: 'Cancelled.' };
    const target = targetDirectory(chosen, repo);
    if (!target.ok) return { ok: false, message: target.error };

    const token = this.secrets.get(profile.id);
    let username: string | undefined;
    if (token) {
      const me = await getJson(this.sessionFor(profile), profile.origin, '/api/v1/user', { token });
      const body = me.body as { userName?: unknown } | null;
      if (me.status === 200 && typeof body?.userName === 'string') username = body.userName;
    }
    const url = cloneUrl(profile.origin, repo);
    const result = await new Promise<{ ok: boolean; message: string }>((resolve) => {
      execFile(git, cloneArgs(url, target.dir), { env: { ...process.env, ...cloneEnv(username, token) }, timeout: 10 * 60_000, maxBuffer: 4 * 1024 * 1024, windowsHide: true }, (err, _out, stderr) => {
        if (err) resolve({ ok: false, message: `git clone failed: ${lastLines(stderr || err.message)}` });
        else resolve({ ok: true, message: `Cloned into ${target.dir}` });
      });
    });
    if (result.ok) this.openInEditor(target.dir);
    if (Notification.isSupported()) new Notification({ title: result.ok ? 'Clone finished' : 'Clone failed', body: result.message.slice(0, 200) }).show();
    return result;
  }

  private searchPath(): string {
    const extra = process.platform === 'darwin' ? ['/opt/homebrew/bin', '/usr/local/bin', '/usr/bin'] : [];
    return [process.env.PATH ?? '', ...extra].filter(Boolean).join(process.platform === 'win32' ? ';' : ':');
  }

  private openInEditor(dir: string): void {
    const win = process.platform === 'win32';
    const detach = (cmd: string, args: string[]) => {
      try {
        const child = spawn(cmd, args, { detached: true, stdio: 'ignore', windowsHide: true });
        child.on('error', () => void shell.showItemInFolder(dir));
        child.unref();
        return true;
      } catch {
        return false;
      }
    };
    const code = findOnPath(['code'], this.searchPath(), win);
    if (code && detach(code, [dir])) return;
    if (process.platform === 'darwin' && fs.existsSync('/Applications/Visual Studio Code.app') && detach('/usr/bin/open', ['-a', 'Visual Studio Code', dir])) return;
    const fromEnv = editorFromEnvironment(process.env);
    if (fromEnv && detach(fromEnv, [dir])) return;
    void shell.showItemInFolder(dir);
  }
}

function lastLines(text: string): string {
  return text.trim().split(/\r?\n/).slice(-3).join(' ').slice(0, 300);
}
