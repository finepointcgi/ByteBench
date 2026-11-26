import { app, nativeImage, nativeTheme, safeStorage, session } from 'electron';
import * as path from 'node:path';
import { Controller } from './controller';
import { paths } from './context';
import { DEEP_LINK_SCHEME, extractDeepLink } from './deeplink';
import { registerIpc } from './ipc';
import { buildMenu } from './menu';
import { ProfileStore } from './profiles';
import { SecretStore, type Codec } from './secrets';
import { hardenWebContents } from './security';
import { applyBadge, createTray, refreshTray } from './tray';
import { initUpdater } from './updater';

// Chromium sandbox for every renderer, not just those that ask for it.
app.enableSandbox();
app.setAppUserModelId('com.bytebench.desktop');

const gotLock = app.requestSingleInstanceLock();
if (!gotLock) {
  app.quit();
} else {
  void main();
}

async function main(): Promise<void> {
  // eslint-disable-next-line prefer-const
  let controller: Controller | undefined;
  const pendingLinks: string[] = [];
  const firstLink = extractDeepLink(process.argv);
  if (firstLink) pendingLinks.push(firstLink);

  app.on('open-url', (event, url) => {
    event.preventDefault();
    if (controller) controller.handleDeepLink(url);
    else pendingLinks.push(url);
  });
  app.on('second-instance', (_event, argv) => {
    const link = extractDeepLink(argv);
    if (link && controller) controller.handleDeepLink(link);
    else if (controller) {
      const w = controller.focusedWindow();
      if (w) w.focus();
      else controller.newWindow();
    }
  });

  // Every WebContents created from here on gets the navigation policy. Registered before any window exists.
  app.on('web-contents-created', (_event, contents) => {
    if (controller) hardenWebContents(contents, controller);
  });

  await app.whenReady();

  // Nothing but the app's own pages uses the default session; give it the same deny-by-default permissions.
  session.defaultSession.setPermissionRequestHandler((_wc, _perm, callback) => callback(false));
  session.defaultSession.setPermissionCheckHandler(() => false);

  const userData = app.getPath('userData');
  const store = new ProfileStore(path.join(userData, 'profiles.json'));
  const codec: Codec = {
    available: () => {
      if (!safeStorage.isEncryptionAvailable()) return false;
      // Linux without a keyring falls back to a hard-coded key ("basic_text"): that is not protection, so treat it as unavailable.
      const backend = (safeStorage as unknown as { getSelectedStorageBackend?: () => string }).getSelectedStorageBackend?.();
      return backend !== 'basic_text';
    },
    encrypt: (s) => safeStorage.encryptString(s),
    decrypt: (b) => safeStorage.decryptString(b),
  };
  const secrets = new SecretStore(path.join(userData, 'secrets.json'), codec);
  controller = new Controller(store, secrets, userData);
  const c = controller;
  nativeTheme.themeSource = store.settings().appearance;

  registerIpc(c);
  c.onProfilesChanged = () => {
    buildMenu(c);
    refreshTray(c);
  };
  c.onUnread = (total) => applyBadge(c, total);

  if (app.isPackaged) {
    // Only a packaged build claims bytebench:// (a dev run would register the bare Electron binary as the handler).
    app.setAsDefaultProtocolClient(DEEP_LINK_SCHEME);
  }
  if (process.platform === 'darwin') {
    const icon = nativeImage.createFromPath(path.join(paths.assets, 'icon.png'));
    if (!icon.isEmpty()) app.dock?.setIcon(icon);
  }

  buildMenu(c);
  createTray(c);
  c.startup();
  for (const link of pendingLinks.splice(0)) c.handleDeepLink(link);
  c.notifier.start();
  initUpdater();

  app.on('activate', () => {
    if (c.windows.size === 0) c.newWindow();
  });
  app.on('window-all-closed', () => {
    // Windows and Linux: keep running in the tray so notifications continue, if the user has servers and notifications on.
    const resident = c.store.list().length > 0 && c.store.settings().notificationsEnabled && process.env.BYTEBENCH_QUIT_ON_CLOSE !== '1';
    if (process.platform !== 'darwin' && !resident) app.quit();
  });
  app.on('before-quit', () => c.notifier.stop());
}
