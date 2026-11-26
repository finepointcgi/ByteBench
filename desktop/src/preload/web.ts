// Preload for ByteBench server pages. Sandboxed, and deliberately tiny: it only lets the page ask the desktop app to do
// something the user then confirms natively (picking a folder). The main process re-checks who is asking.
import { contextBridge, ipcRenderer } from 'electron';

contextBridge.exposeInMainWorld('byteBenchDesktop', {
  isDesktop: true,
  platform: process.platform,
  /** Clone the repository of the current page (or of `repoPath`, a /p/KEY/repos/NAME or /git/KEY/NAME.git path) into a folder the user picks. */
  cloneRepository: (repoPath?: string): Promise<{ ok: boolean; message: string }> =>
    ipcRenderer.invoke('bb:web:clone', typeof repoPath === 'string' ? repoPath.slice(0, 512) : undefined),
  /** Version of the desktop app. */
  getInfo: (): Promise<{ version: string }> => ipcRenderer.invoke('bb:web:info'),
});
