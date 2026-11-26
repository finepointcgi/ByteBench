import { app, ipcMain, type IpcMainInvokeEvent } from 'electron';
import { Channels } from '../shared/types';
import type { ProfileInput } from '../shared/types';
import type { Controller } from './controller';
import { rendererUrlPrefix, wcContexts } from './context';
import { closeFindFor, closePaletteFor } from './overlays';

/**
 * IPC handlers. Every handler first proves who is asking:
 *  - app pages (ui): the sender must be a WebContents we registered as 'ui', the call must come from its top frame, and that
 *    frame must be one of our bundled pages (file:// inside dist/renderer)
 *  - server pages (web): the sender must be a registered profile WebContents, from its top frame, whose URL is on that profile's origin
 * Anything else is rejected with an exception, so a compromised or navigated frame gets nothing.
 */
export function registerIpc(c: Controller): void {
  const uiSender = (e: IpcMainInvokeEvent) => {
    const ctx = wcContexts.get(e.sender.id);
    const frame = e.senderFrame;
    if (!ctx || ctx.kind !== 'ui' || !frame || frame !== e.sender.mainFrame || !frame.url.startsWith(rendererUrlPrefix)) throw new Error('Rejected IPC sender');
    return c.windowById(ctx.windowId);
  };
  const webSender = (e: IpcMainInvokeEvent) => {
    const ctx = wcContexts.get(e.sender.id);
    const frame = e.senderFrame;
    if (!ctx || ctx.kind !== 'profile' || !frame || frame !== e.sender.mainFrame) throw new Error('Rejected IPC sender');
    const profile = c.store.get(ctx.profileId);
    let origin = '';
    try {
      origin = new URL(frame.url).origin;
    } catch {
      /* rejected below */
    }
    if (!profile || origin !== profile.origin) throw new Error('Rejected IPC sender');
    return { profile, window: c.windowById(ctx.windowId), url: frame.url };
  };

  const asInput = (raw: unknown): ProfileInput => {
    const o = (raw && typeof raw === 'object' ? raw : {}) as Record<string, unknown>;
    return {
      name: typeof o.name === 'string' ? o.name : '',
      url: typeof o.url === 'string' ? o.url : '',
      allowSelfSigned: o.allowSelfSigned === true,
      extraOrigins: Array.isArray(o.extraOrigins) ? o.extraOrigins.filter((x): x is string => typeof x === 'string') : [],
      confirmInsecureHttp: o.confirmInsecureHttp === true,
      token: typeof o.token === 'string' ? o.token.slice(0, 512) : undefined,
      clearToken: o.clearToken === true,
    };
  };

  ipcMain.handle(Channels.profilesList, (e) => {
    const w = uiSender(e);
    return { profiles: c.views(), message: w?.pickerMessage };
  });
  ipcMain.handle(Channels.profilesAdd, async (e, raw: unknown) => {
    const w = uiSender(e);
    const r = c.addProfile(asInput(raw));
    if (r.ok && w) {
      // Adding the first (or any) server from the picker opens it straight away.
      void w.openProfile(r.value.id);
    }
    return r.ok ? { ok: true, value: { ...r.value, hasToken: c.secrets.has(r.value.id), unread: 0 } } : r;
  });
  ipcMain.handle(Channels.profilesUpdate, (e, id: unknown, raw: unknown) => {
    uiSender(e);
    if (typeof id !== 'string') return { ok: false, error: 'Unknown server.' };
    const r = c.updateProfile(id, asInput(raw));
    return r.ok ? { ok: true, value: { ...r.value, hasToken: c.secrets.has(r.value.id), unread: c.notifier.unreadFor(r.value.id) } } : r;
  });
  ipcMain.handle(Channels.profilesRemove, async (e, id: unknown) => {
    uiSender(e);
    if (typeof id !== 'string') return false;
    await c.removeProfile(id);
    return true;
  });
  ipcMain.handle(Channels.profilesOpen, (e, id: unknown) => {
    const w = uiSender(e);
    if (typeof id !== 'string' || !w) return;
    closePaletteFor(w.id);
    void w.openProfile(id);
  });
  ipcMain.handle(Channels.errorState, (e) => uiSender(e)?.errorState ?? null);
  ipcMain.handle(Channels.errorRetry, async (e) => {
    await uiSender(e)?.retry();
  });
  ipcMain.handle(Channels.errorTrust, async (e) => {
    const w = uiSender(e);
    if (w?.errorState) await c.trustCertificate(w, w.errorState.profileId);
  });
  ipcMain.handle(Channels.paletteClose, (e) => {
    const w = uiSender(e);
    if (w) closePaletteFor(w.id);
  });
  ipcMain.handle(Channels.findQuery, (e, text: unknown, forward: unknown, next: unknown) => {
    const w = uiSender(e);
    if (!w || typeof text !== 'string') return;
    const contents = w.activeContents();
    if (!text) contents.stopFindInPage('clearSelection');
    else contents.findInPage(text.slice(0, 200), { forward: forward !== false, findNext: next === true });
  });
  ipcMain.handle(Channels.findClose, (e) => {
    const w = uiSender(e);
    if (w) closeFindFor(w.id);
  });

  // Server pages.
  ipcMain.handle(Channels.cloneRepository, async (e, repoPath: unknown) => {
    const { profile, url } = webSender(e);
    const page = typeof repoPath === 'string' ? repoPath : new URL(url).pathname;
    return c.cloneRepository(profile.id, page);
  });
  ipcMain.handle(Channels.webInfo, (e) => {
    webSender(e);
    return { version: app.getVersion() };
  });
}
