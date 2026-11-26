import { app, dialog, shell } from 'electron';
import { autoUpdater } from 'electron-updater';

/**
 * Auto-update through electron-updater. The feed URL comes from the app-update.yml electron-builder bakes into packaged builds
 * (the rolling "desktop-latest" GitHub release, see desktop/README.md). Off in development and when BYTEBENCH_DISABLE_UPDATE=1.
 */
export function updatesEnabled(): boolean {
  return app.isPackaged && process.env.BYTEBENCH_DISABLE_UPDATE !== '1';
}

let started = false;
let manual = false;

export function initUpdater(): void {
  if (!updatesEnabled() || started) return;
  started = true;
  autoUpdater.autoDownload = true;
  autoUpdater.autoInstallOnAppQuit = true;
  autoUpdater.allowPrerelease = false;
  autoUpdater.logger = null;

  autoUpdater.on('update-available', (info) => {
    if (manual) void dialog.showMessageBox({ type: 'info', message: `Downloading ByteBench ${info.version}…`, detail: 'You will be asked to restart when it is ready.' });
  });
  autoUpdater.on('update-not-available', () => {
    if (manual) void dialog.showMessageBox({ type: 'info', message: 'You are up to date', detail: `ByteBench ${app.getVersion()} is the latest version.` });
    manual = false;
  });
  autoUpdater.on('update-downloaded', (info) => {
    manual = false;
    void dialog
      .showMessageBox({ type: 'info', buttons: ['Restart now', 'Later'], defaultId: 0, cancelId: 1, message: `ByteBench ${info.version} is ready`, detail: 'Restart the app to finish updating.' })
      .then((r) => {
        if (r.response === 0) autoUpdater.quitAndInstall();
      });
  });
  autoUpdater.on('error', (err) => {
    if (!manual) return; // background failures are silent (offline, unsigned build, no release yet)
    manual = false;
    void dialog
      .showMessageBox({ type: 'warning', buttons: ['Open releases page', 'Close'], message: 'Could not update automatically', detail: `${err.message.slice(0, 300)}\n\nYou can download the latest version by hand.` })
      .then((r) => {
        if (r.response === 0) void shell.openExternal('https://github.com/finepointcgi/ByteBench/releases');
      });
  });

  setTimeout(() => void autoUpdater.checkForUpdates().catch(() => undefined), 20_000);
  setInterval(() => void autoUpdater.checkForUpdates().catch(() => undefined), 6 * 60 * 60 * 1000).unref();
}

export function checkForUpdates(userInitiated: boolean): void {
  if (!updatesEnabled()) return;
  manual = userInitiated;
  void autoUpdater.checkForUpdates().catch(() => undefined);
}
