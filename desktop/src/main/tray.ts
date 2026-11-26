import { Menu, Tray, app, nativeImage, type NativeImage } from 'electron';
import * as path from 'node:path';
import { paths } from './context';
import type { Controller } from './controller';

let tray: Tray | undefined;

function trayIcon(): NativeImage {
  // macOS: a template image (black with alpha) so the system tints it for light and dark menu bars.
  const file = process.platform === 'darwin' ? 'trayTemplate.png' : 'tray.png';
  const img = nativeImage.createFromPath(path.join(paths.assets, file));
  return img.isEmpty() ? nativeImage.createEmpty() : img;
}

export function createTray(c: Controller): void {
  if (tray) return;
  tray = new Tray(trayIcon());
  tray.setToolTip('ByteBench');
  tray.on('click', () => {
    if (process.platform !== 'darwin') showApp(c);
  });
  refreshTray(c);
}

function showApp(c: Controller): void {
  const w = c.focusedWindow();
  if (w) w.focus();
  else c.newWindow();
}

export function refreshTray(c: Controller, total = 0): void {
  if (!tray) return;
  const profiles = c.store.list();
  const menu = Menu.buildFromTemplate([
    { label: 'Open ByteBench', click: () => showApp(c) },
    { type: 'separator' },
    ...(profiles.length
      ? profiles.map((p) => ({
          label: p.name + (c.notifier.unreadFor(p.id) ? `  (${c.notifier.unreadFor(p.id)} unread)` : ''),
          click: () => c.openProfile(p.id),
        }))
      : [{ label: 'No servers yet', enabled: false }]),
    { type: 'separator' },
    { label: 'Check Notifications Now', click: () => c.notifier.pollNow() },
    { label: 'Add or Manage Servers…', click: () => c.showPicker() },
    { type: 'separator' },
    { label: 'Quit ByteBench', click: () => app.quit() },
  ]);
  tray.setContextMenu(menu);
  tray.setToolTip(total > 0 ? `ByteBench: ${total} unread` : 'ByteBench');
  if (process.platform === 'darwin') tray.setTitle(total > 0 ? ` ${total}` : '');
}

/** Dock/taskbar badge. macOS and Linux use the badge count; Windows gets a small overlay dot on the taskbar button. */
export function applyBadge(c: Controller, total: number): void {
  if (process.platform === 'win32') {
    for (const w of c.windows) {
      if (w.win.isDestroyed()) continue;
      w.win.setOverlayIcon(total > 0 ? dotIcon() : null, total > 0 ? `${total} unread notifications` : '');
    }
  } else {
    app.setBadgeCount(total);
  }
  refreshTray(c, total);
}

let dot: NativeImage | undefined;
function dotIcon(): NativeImage {
  if (dot) return dot;
  const size = 16;
  const buf = Buffer.alloc(size * size * 4);
  for (let y = 0; y < size; y++) {
    for (let x = 0; x < size; x++) {
      const d = Math.hypot(x - 7.5, y - 7.5);
      const o = (y * size + x) * 4;
      if (d <= 7) {
        buf[o] = 0x2f; // BGRA
        buf[o + 1] = 0x3b;
        buf[o + 2] = 0xdc;
        buf[o + 3] = 255;
      }
    }
  }
  dot = nativeImage.createFromBitmap(buf, { width: size, height: size });
  return dot;
}
