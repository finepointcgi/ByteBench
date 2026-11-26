import { BrowserWindow, Menu, clipboard, WebContentsView, nativeTheme, screen, type Rectangle } from 'electron';
import * as path from 'node:path';
import type { ErrorState, Profile } from '../shared/types';
import { describeNetError, isCertError } from './net';
import { partitionFor, paths, wcContexts } from './context';
import { openExternalSafely } from './security';

/** What an AppWindow needs from the rest of the app. */
export interface WindowHost {
  /** Make sure the profile's session is configured (permissions, certificate pinning) and return the profile. */
  prepareProfile(profileId: string): Profile | undefined;
  /** Probe the server; resolves to an error state if the profile should not be loaded, else null. */
  probe(profile: Profile): Promise<ErrorState | null>;
  /** Certificate details recorded by the verify proc for a failed connection. */
  certificateFor(profile: Profile, code: string): ErrorState['certificate'];
  markOpened(profileId: string): void;
  windowClosed(window: AppWindow): void;
  stateFor(): Rectangle & { maximized?: boolean };
  saveState(bounds: Rectangle, maximized: boolean): void;
}

export type UiView = 'picker' | 'connecting' | 'error';

export class AppWindow {
  readonly win: BrowserWindow;
  private readonly ui: WebContentsView;
  private readonly views = new Map<string, WebContentsView>();
  private activeProfile?: string;
  private seq = 0;
  errorState?: ErrorState;
  pickerMessage?: string;
  connectingName?: string;
  currentUi: UiView = 'picker';

  constructor(private readonly host: WindowHost) {
    const state = host.stateFor();
    this.win = new BrowserWindow({
      ...clampToDisplay(state),
      minWidth: 820,
      minHeight: 560,
      title: 'ByteBench',
      show: false,
      backgroundColor: nativeTheme.shouldUseDarkColors ? '#12151c' : '#ffffff',
      webPreferences: { sandbox: true, contextIsolation: true, nodeIntegration: false, webviewTag: false },
    });
    wcContexts.set(this.win.webContents.id, { kind: 'ui', windowId: this.win.id });
    if (state.maximized) this.win.maximize();

    this.ui = new WebContentsView({
      webPreferences: { preload: paths.preloadUi, sandbox: true, contextIsolation: true, nodeIntegration: false, webviewTag: false, spellcheck: false },
    });
    wcContexts.set(this.ui.webContents.id, { kind: 'ui', windowId: this.win.id });
    this.win.contentView.addChildView(this.ui);

    this.win.once('ready-to-show', () => this.win.show());
    const relayout = () => this.layout();
    this.win.on('resize', relayout);
    this.win.on('enter-full-screen', relayout);
    this.win.on('leave-full-screen', relayout);
    this.win.on('maximize', relayout);
    this.win.on('unmaximize', relayout);
    let timer: NodeJS.Timeout | undefined;
    const save = () => {
      clearTimeout(timer);
      timer = setTimeout(() => {
        if (this.win.isDestroyed() || this.win.isMinimized() || this.win.isFullScreen()) return;
        this.host.saveState(this.win.getNormalBounds(), this.win.isMaximized());
      }, 400);
    };
    this.win.on('resize', save);
    this.win.on('move', save);
    this.win.on('closed', () => {
      clearTimeout(timer);
      this.destroyViews();
      this.host.windowClosed(this);
    });
    this.layout();
    this.ui.webContents.on('context-menu', (_e, params) => {
      if (params.isEditable) Menu.buildFromTemplate([{ role: 'cut' }, { role: 'copy' }, { role: 'paste' }, { role: 'selectAll' }]).popup();
    });
  }

  get id(): number {
    return this.win.id;
  }

  get activeProfileId(): string | undefined {
    return this.activeProfile;
  }

  /** The WebContents that currently has the user's attention (the server page, or the app's own page). */
  activeContents(): Electron.WebContents {
    const v = this.activeProfile ? this.views.get(this.activeProfile) : undefined;
    return v && v.getVisible() ? v.webContents : this.ui.webContents;
  }

  profileContents(profileId: string): Electron.WebContents | undefined {
    return this.views.get(profileId)?.webContents;
  }

  // ----- the app's own pages -----

  showUi(view: UiView): void {
    this.seq++;
    this.currentUi = view;
    for (const v of this.views.values()) v.setVisible(false);
    this.ui.setVisible(true);
    void this.ui.webContents.loadFile(path.join(paths.renderer, 'app.html'), { query: { view } });
    this.layout();
    this.ui.webContents.focus();
  }

  showPicker(message?: string): void {
    this.pickerMessage = message;
    this.errorState = undefined;
    this.activeProfile = undefined;
    this.showUi('picker');
    this.win.setTitle('ByteBench');
  }

  showError(state: ErrorState): void {
    this.errorState = state;
    this.showUi('error');
    this.win.setTitle(`ByteBench: ${state.profileName}`);
  }

  // ----- server pages -----

  /** Opens a profile (and optionally a path on it) in this window: probe, then load. */
  async openProfile(profileId: string, pathAndQuery?: string): Promise<void> {
    const profile = this.host.prepareProfile(profileId);
    if (!profile) return this.showPicker('That server is no longer in the list.');
    const my = ++this.seq;
    const existing = this.views.get(profile.id);
    this.activeProfile = profile.id;
    this.errorState = undefined;
    const target = pathAndQuery ? new URL(pathAndQuery, profile.origin).toString() : undefined;

    // Already loaded and nothing specific asked for: just bring it forward.
    if (existing && !pathAndQuery && !existing.webContents.isLoading() && !this.failed.has(profile.id)) {
      this.show(profile, existing);
      return;
    }
    if (!existing) {
      this.connectingName = profile.name;
      this.showUiKeepSeq('connecting', my);
    }
    const failure = await this.host.probe(profile);
    if (my !== this.seq) return; // the user switched to something else meanwhile
    if (failure) return this.showError(failure);

    const view = existing ?? this.createView(profile);
    this.failed.delete(profile.id);
    this.show(profile, view);
    this.host.markOpened(profile.id);
    if (!existing || target) void view.webContents.loadURL(target ?? profile.origin + '/').catch(() => undefined);
    else if (view.webContents.getURL() === '') void view.webContents.loadURL(profile.origin + '/').catch(() => undefined);
  }

  private readonly failed = new Set<string>();

  private showUiKeepSeq(view: UiView, seq: number): void {
    this.showUi(view);
    this.seq = seq;
  }

  private show(profile: Profile, view: WebContentsView): void {
    for (const [id, v] of this.views) v.setVisible(id === profile.id);
    view.setVisible(true);
    this.ui.setVisible(false);
    this.activeProfile = profile.id;
    this.layout();
    view.webContents.focus();
    this.win.setTitle(`${view.webContents.getTitle() || 'ByteBench'}: ${profile.name}`);
  }

  private createView(profile: Profile): WebContentsView {
    const view = new WebContentsView({
      webPreferences: {
        partition: partitionFor(profile.id),
        preload: paths.preloadWeb,
        sandbox: true,
        contextIsolation: true,
        nodeIntegration: false,
        webviewTag: false,
        spellcheck: true,
        // The server is untrusted code from our point of view: no Node, no remote content from file:, no insecure content.
        allowRunningInsecureContent: false,
        experimentalFeatures: false,
      },
    });
    const wc = view.webContents;
    wcContexts.set(wc.id, { kind: 'profile', profileId: profile.id, windowId: this.win.id });
    this.views.set(profile.id, view);
    this.win.contentView.addChildView(view);

    wc.on('page-title-updated', (_e, title) => {
      if (this.activeProfile === profile.id && view.getVisible()) this.win.setTitle(`${title}: ${profile.name}`);
    });
    wc.on('did-fail-load', (_e, code, desc, url, isMainFrame) => {
      if (!isMainFrame || code === -3 /* aborted: a redirect or a new navigation */) return;
      this.failed.add(profile.id);
      if (this.activeProfile !== profile.id || !view.getVisible()) return; // a background server failing must not steal the window
      const netCode = desc.startsWith('ERR_') ? `net::${desc}` : desc;
      this.showError({
        profileId: profile.id,
        profileName: profile.name,
        url,
        code: netCode,
        description: describeNetError(netCode),
        certificate: isCertError(netCode) ? this.host.certificateFor(profile, netCode) : undefined,
      });
    });
    wc.on('did-finish-load', () => {
      this.failed.delete(profile.id);
      if (this.activeProfile === profile.id && this.currentUi === 'error' && this.ui.getVisible()) this.show(profile, view);
    });
    wc.on('render-process-gone', (_e, details) => {
      this.failed.add(profile.id);
      this.showError({ profileId: profile.id, profileName: profile.name, url: profile.origin, code: details.reason, description: 'The page crashed. Retry to reload it.' });
    });
    wc.on('context-menu', (_e, params) => {
      const items: Electron.MenuItemConstructorOptions[] = [];
      if (params.linkURL) {
        items.push(
          { label: 'Open Link in Browser', click: () => openExternalSafely(params.linkURL) },
          { label: 'Copy Link Address', click: () => clipboard.writeText(params.linkURL) },
          { type: 'separator' },
        );
      }
      if (params.isEditable) {
        for (const s of params.dictionarySuggestions.slice(0, 4)) items.push({ label: s, click: () => wc.replaceMisspelling(s) });
        if (params.dictionarySuggestions.length) items.push({ type: 'separator' });
        items.push({ role: 'cut' }, { role: 'copy' }, { role: 'paste' }, { role: 'selectAll' });
      } else if (params.selectionText) {
        items.push({ role: 'copy' });
      }
      if (items.length) Menu.buildFromTemplate(items).popup();
    });
    return view;
  }

  /** Reload the failed or current server page (the error page's Retry). */
  async retry(): Promise<void> {
    const id = this.errorState?.profileId ?? this.activeProfile;
    if (!id) return this.showPicker();
    const view = this.views.get(id);
    const failedUrl = this.errorState?.url;
    this.failed.delete(id);
    // Same path as before when it is on the profile's origin; the probe runs again so the error is current.
    const profile = this.host.prepareProfile(id);
    if (!profile) return this.showPicker();
    let pathAndQuery: string | undefined;
    try {
      const u = new URL(failedUrl ?? '');
      if (u.origin === profile.origin) pathAndQuery = u.pathname + u.search;
    } catch {
      /* no usable URL */
    }
    if (view) {
      view.webContents.stop();
      this.destroyView(id); // a fresh view: after a crash or a certificate change the old one is not worth keeping
    }
    await this.openProfile(id, pathAndQuery);
  }

  destroyProfileView(profileId: string): void {
    this.destroyView(profileId);
    if (this.activeProfile === profileId) this.showPicker();
  }

  private destroyView(profileId: string): void {
    const v = this.views.get(profileId);
    if (!v) return;
    this.views.delete(profileId);
    wcContexts.delete(v.webContents.id);
    try {
      this.win.contentView.removeChildView(v);
    } catch {
      /* window already gone */
    }
    if (!v.webContents.isDestroyed()) v.webContents.close();
  }

  private destroyViews(): void {
    for (const id of [...this.views.keys()]) this.destroyView(id);
    wcContexts.delete(this.ui.webContents.id);
    wcContexts.delete(this.win.webContents.id);
    if (!this.ui.webContents.isDestroyed()) this.ui.webContents.close();
  }

  private layout(): void {
    if (this.win.isDestroyed()) return;
    const [width, height] = this.win.getContentSize();
    const bounds = { x: 0, y: 0, width, height };
    this.ui.setBounds(bounds);
    for (const v of this.views.values()) v.setBounds(bounds);
  }

  focus(): void {
    if (this.win.isMinimized()) this.win.restore();
    this.win.show();
    this.win.focus();
  }
}

function clampToDisplay(state: Rectangle): Rectangle {
  const displays = screen.getAllDisplays();
  const visible = displays.some((d) => {
    const a = d.workArea;
    return state.x < a.x + a.width - 40 && state.x + state.width > a.x + 40 && state.y >= a.y - 10 && state.y < a.y + a.height - 40;
  });
  if (visible) return state;
  const primary = screen.getPrimaryDisplay().workArea;
  const width = Math.min(state.width, primary.width);
  const height = Math.min(state.height, primary.height);
  return { width, height, x: primary.x + Math.round((primary.width - width) / 2), y: primary.y + Math.round((primary.height - height) / 2) };
}
