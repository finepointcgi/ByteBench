import { Menu, app, dialog, shell, type MenuItemConstructorOptions } from 'electron';
import { repoFromPath } from './clone';
import type { Controller } from './controller';
import { openFind, openPalette } from './overlays';
import { showWorkspacePortsMenu } from './ports-menu';
import { checkForUpdates, updatesEnabled } from './updater';

const isMac = process.platform === 'darwin';

export function buildMenu(c: Controller): void {
  const profiles = c.store.list();
  const settings = c.store.settings();
  const current = () => c.focusedWindow();
  const contents = () => current()?.activeContents();
  const zoom = (delta: number | 'reset') => {
    const wc = contents();
    if (!wc) return;
    wc.setZoomLevel(delta === 'reset' ? 0 : Math.max(-3, Math.min(5, wc.getZoomLevel() + delta)));
  };
  const goto = (path: string) => () => {
    const w = current();
    const id = w?.activeProfileId;
    if (w && id) void w.openProfile(id, path);
  };

  const serverItems: MenuItemConstructorOptions[] = profiles.map((p, i) => ({
    label: p.name + (c.notifier.unreadFor(p.id) ? ` (${c.notifier.unreadFor(p.id)})` : ''),
    accelerator: i < 9 ? `CommandOrControl+${i + 1}` : undefined,
    type: 'checkbox',
    checked: current()?.activeProfileId === p.id,
    click: () => c.openProfile(p.id),
  }));

  const template: MenuItemConstructorOptions[] = [
    ...(isMac
      ? [
          {
            label: app.name,
            submenu: [
              { role: 'about' },
              { label: 'Check for Updates…', enabled: updatesEnabled(), click: () => checkForUpdates(true) },
              { type: 'separator' },
              { label: 'Servers…', click: () => c.showPicker() },
              { type: 'separator' },
              { role: 'services' },
              { type: 'separator' },
              { role: 'hide' },
              { role: 'hideOthers' },
              { role: 'unhide' },
              { type: 'separator' },
              { role: 'quit' },
            ],
          } as MenuItemConstructorOptions,
        ]
      : []),
    {
      label: 'File',
      submenu: [
        { label: 'New Window', accelerator: 'CommandOrControl+N', click: () => c.newWindow() },
        { label: 'Switch Server…', accelerator: 'CommandOrControl+K', click: () => { const w = current(); if (w) openPalette(w); } },
        { label: 'Add or Manage Servers…', accelerator: 'CommandOrControl+Shift+N', click: () => c.showPicker() },
        { type: 'separator' },
        {
          label: 'Clone This Repository…',
          accelerator: 'CommandOrControl+Shift+C',
          click: () => {
            const w = current();
            const id = w?.activeProfileId;
            const wc = id ? w?.profileContents(id) : undefined;
            if (!id || !wc) return;
            const path = (() => {
              try { return new URL(wc.getURL()).pathname; } catch { return ''; }
            })();
            if (!repoFromPath(path)) {
              void dialog.showMessageBox({ type: 'info', message: 'Open a repository first', detail: 'Go to a repository page (under Projects, Repositories) and choose this command again.' });
              return;
            }
            void c.cloneRepository(id, path).then((r) => {
              if (!r.ok && r.message !== 'Cancelled.') void dialog.showMessageBox({ type: 'error', message: 'Clone failed', detail: r.message });
            });
          },
        },
        ...(isMac ? [] : ([{ type: 'separator' }, { role: 'quit' }] as MenuItemConstructorOptions[])),
        ...(isMac ? ([{ type: 'separator' }, { role: 'close' }] as MenuItemConstructorOptions[]) : []),
      ],
    },
    {
      label: 'Edit',
      submenu: [
        { role: 'undo' }, { role: 'redo' }, { type: 'separator' },
        { role: 'cut' }, { role: 'copy' }, { role: 'paste' }, { role: 'selectAll' }, { type: 'separator' },
        { label: 'Find in Page…', accelerator: 'CommandOrControl+F', click: () => { const w = current(); if (w) openFind(w); } },
      ],
    },
    {
      label: 'View',
      submenu: [
        { label: 'Reload', accelerator: 'CommandOrControl+R', click: () => contents()?.reload() },
        { label: 'Hard Reload', accelerator: 'CommandOrControl+Shift+R', click: () => contents()?.reloadIgnoringCache() },
        { type: 'separator' },
        { label: 'Back', accelerator: isMac ? 'Command+[' : 'Alt+Left', click: () => { const wc = contents(); if (wc?.navigationHistory.canGoBack()) wc.navigationHistory.goBack(); } },
        { label: 'Forward', accelerator: isMac ? 'Command+]' : 'Alt+Right', click: () => { const wc = contents(); if (wc?.navigationHistory.canGoForward()) wc.navigationHistory.goForward(); } },
        { type: 'separator' },
        { label: 'Zoom In', accelerator: 'CommandOrControl+Plus', click: () => zoom(0.5) },
        { label: 'Zoom In', accelerator: 'CommandOrControl+=', visible: false, click: () => zoom(0.5) },
        { label: 'Zoom Out', accelerator: 'CommandOrControl+-', click: () => zoom(-0.5) },
        { label: 'Actual Size', accelerator: 'CommandOrControl+0', click: () => zoom('reset') },
        { type: 'separator' },
        {
          label: 'Appearance',
          submenu: (['system', 'light', 'dark'] as const).map((a) => ({
            label: a === 'system' ? 'Follow System' : a[0]!.toUpperCase() + a.slice(1),
            type: 'radio' as const,
            checked: settings.appearance === a,
            click: () => { c.setAppearance(a); buildMenu(c); },
          })),
        },
        { role: 'togglefullscreen' },
        ...(app.isPackaged ? [] : ([{ type: 'separator' }, { label: 'Developer Tools', accelerator: 'F12', click: () => contents()?.openDevTools({ mode: 'detach' }) }] as MenuItemConstructorOptions[])),
      ],
    },
    {
      label: 'Go',
      submenu: [
        { label: 'Home', accelerator: 'CommandOrControl+Shift+H', click: goto('/') },
        { label: 'Notifications', click: goto('/notifications') },
        { label: 'Workspaces', click: goto('/workspaces') },
        { label: 'Workspace Ports…', accelerator: 'CommandOrControl+Shift+P', click: () => void showWorkspacePortsMenu(c) },
        { label: 'Search', click: goto('/search') },
        { type: 'separator' },
        {
          label: 'Open Current Page in Browser',
          click: () => {
            const url = contents()?.getURL();
            if (url && /^https?:\/\//.test(url)) void shell.openExternal(url);
          },
        },
      ],
    },
    {
      label: 'Servers',
      submenu: [
        ...(serverItems.length ? serverItems : ([{ label: 'No servers yet', enabled: false }] as MenuItemConstructorOptions[])),
        { type: 'separator' },
        { label: 'Add or Manage Servers…', click: () => c.showPicker() },
        { label: 'Check Notifications Now', click: () => c.notifier.pollNow() },
        {
          label: 'Desktop Notifications',
          type: 'checkbox',
          checked: settings.notificationsEnabled,
          click: (item) => { c.store.updateSettings({ notificationsEnabled: item.checked }); },
        },
        ...(process.platform === 'linux'
          ? []
          : ([
              {
                label: 'Launch at Login',
                type: 'checkbox',
                checked: app.getLoginItemSettings().openAtLogin,
                click: (item) => app.setLoginItemSettings({ openAtLogin: item.checked }),
              },
            ] as MenuItemConstructorOptions[])),
      ],
    },
    { role: 'windowMenu' },
    {
      role: 'help',
      submenu: [
        ...(isMac ? [] : ([{ label: 'Check for Updates…', enabled: updatesEnabled(), click: () => checkForUpdates(true) }, { type: 'separator' }] as MenuItemConstructorOptions[])),
        { label: 'ByteBench on GitHub', click: () => void shell.openExternal('https://github.com/finepointcgi/ByteBench') },
        { label: 'Desktop App Documentation', click: () => void shell.openExternal('https://github.com/finepointcgi/ByteBench/blob/master/desktop/README.md') },
        { label: `Version ${app.getVersion()}`, enabled: false },
      ],
    },
  ];
  Menu.setApplicationMenu(Menu.buildFromTemplate(template));
}
