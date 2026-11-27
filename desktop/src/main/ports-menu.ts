import { Menu, dialog, type MenuItemConstructorOptions } from 'electron';
import type { Controller } from './controller';

/**
 * Go → Workspace Ports…: lists the running workspaces' forwarded ports of the current server and opens one in an app window or the default
 * browser. The list and the entry link are validated in workspace-ports.ts; this only shows them.
 */
export async function showWorkspacePortsMenu(c: Controller): Promise<void> {
  const profileId = c.focusedWindow()?.activeProfileId;
  if (!profileId) {
    void dialog.showMessageBox({ type: 'info', message: 'Open a server first', detail: 'Workspace ports belong to a ByteBench server. Open one, then choose this command again.' });
    return;
  }
  const r = await c.workspacePorts(profileId);
  if (!r.ok) {
    void dialog.showMessageBox({ type: 'info', message: 'Workspace ports', detail: r.message });
    return;
  }
  const fail = (message: string) => void dialog.showMessageBox({ type: 'error', message: 'Could not open the port', detail: message });
  const open = (workspaceId: string, port: number, where: 'window' | 'browser') => () =>
    void c.openWorkspacePort(profileId, workspaceId, port, where).then((o) => (o.ok ? undefined : fail(o.message)));

  const template: MenuItemConstructorOptions[] = [];
  for (const item of r.items) {
    if (template.length) template.push({ type: 'separator' });
    template.push({ label: `${item.workspace.repository} (${item.workspace.ref})`, enabled: false });
    if (item.ports.length === 0) template.push({ label: 'Nothing is listening yet', enabled: false });
    for (const p of item.ports) {
      template.push({
        label: `Port ${p.port}`,
        submenu: [
          { label: 'Open in App Window', click: open(item.workspace.id, p.port, 'window') },
          { label: 'Open in Browser', click: open(item.workspace.id, p.port, 'browser') },
        ],
      });
    }
  }
  if (template.length === 0) {
    void dialog.showMessageBox({ type: 'info', message: 'No running workspaces', detail: 'Start a workspace on this server, run your app in it, and its ports will be listed here.' });
    return;
  }
  Menu.buildFromTemplate(template).popup();
}
