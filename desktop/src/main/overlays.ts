import { BrowserWindow, nativeTheme } from 'electron';
import * as path from 'node:path';
import { paths, wcContexts } from './context';
import type { AppWindow } from './windows';

/** Small frameless windows over an app window: the server switcher (Cmd/Ctrl+K) and the find-in-page bar. */
function makeOverlay(owner: AppWindow, file: string, width: number, height: number, place: (parent: Electron.Rectangle) => { x: number; y: number }): BrowserWindow {
  const parent = owner.win;
  const pb = parent.getBounds();
  const { x, y } = place(pb);
  const win = new BrowserWindow({
    parent,
    width,
    height,
    x,
    y,
    frame: false,
    resizable: false,
    minimizable: false,
    maximizable: false,
    fullscreenable: false,
    skipTaskbar: true,
    show: false,
    hasShadow: true,
    backgroundColor: nativeTheme.shouldUseDarkColors ? '#1b1f2a' : '#ffffff',
    webPreferences: { preload: paths.preloadUi, sandbox: true, contextIsolation: true, nodeIntegration: false, webviewTag: false, spellcheck: false },
  });
  wcContexts.set(win.webContents.id, { kind: 'ui', windowId: owner.id });
  const id = win.webContents.id;
  win.on('closed', () => wcContexts.delete(id));
  void win.loadFile(path.join(paths.renderer, file));
  win.once('ready-to-show', () => win.show());
  return win;
}

const palettes = new Map<number, BrowserWindow>();
const finders = new Map<number, BrowserWindow>();

export function openPalette(owner: AppWindow): void {
  const existing = palettes.get(owner.id);
  if (existing && !existing.isDestroyed()) {
    existing.focus();
    return;
  }
  const win = makeOverlay(owner, 'palette.html', 520, 380, (p) => ({ x: Math.round(p.x + (p.width - 520) / 2), y: p.y + 90 }));
  palettes.set(owner.id, win);
  win.on('blur', () => {
    if (!win.isDestroyed()) win.close();
  });
  win.on('closed', () => palettes.delete(owner.id));
}

export function closePaletteFor(windowId: number): void {
  const w = palettes.get(windowId);
  if (w && !w.isDestroyed()) w.close();
}

export function openFind(owner: AppWindow): void {
  const existing = finders.get(owner.id);
  if (existing && !existing.isDestroyed()) {
    existing.focus();
    existing.webContents.send('bb:find:focus');
    return;
  }
  const win = makeOverlay(owner, 'find.html', 380, 46, (p) => ({ x: p.x + p.width - 380 - 24, y: p.y + 56 }));
  finders.set(owner.id, win);
  win.on('closed', () => {
    finders.delete(owner.id);
    const contents = owner.activeContents();
    if (!contents.isDestroyed()) contents.stopFindInPage('clearSelection');
  });
  // Results flow back to the bar from the page being searched.
  const contents = owner.activeContents();
  contents.on('found-in-page', (_e, result) => {
    if (!win.isDestroyed()) win.webContents.send('bb:find:result', { matches: result.matches, active: result.activeMatchOrdinal });
  });
}

export function closeFindFor(windowId: number): void {
  const w = finders.get(windowId);
  if (w && !w.isDestroyed()) w.close();
}
