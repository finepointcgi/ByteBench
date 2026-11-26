// Preload for the app's own pages (server picker, error page, switcher, find bar). Sandboxed: only 'electron' may be required.
import { contextBridge, ipcRenderer } from 'electron';
import type { ErrorState, ProfileInput, ProfileView, Result } from '../shared/types';

const C = {
  profilesList: 'bb:profiles:list',
  profilesAdd: 'bb:profiles:add',
  profilesUpdate: 'bb:profiles:update',
  profilesRemove: 'bb:profiles:remove',
  profilesOpen: 'bb:profiles:open',
  profilesChanged: 'bb:profiles:changed',
  errorState: 'bb:error:state',
  errorRetry: 'bb:error:retry',
  errorTrust: 'bb:error:trust',
  paletteClose: 'bb:palette:close',
  findQuery: 'bb:find:query',
  findClose: 'bb:find:close',
} as const;

contextBridge.exposeInMainWorld('bb', {
  platform: process.platform,
  listProfiles: (): Promise<{ profiles: ProfileView[]; message?: string }> => ipcRenderer.invoke(C.profilesList),
  addProfile: (input: ProfileInput): Promise<Result<ProfileView>> => ipcRenderer.invoke(C.profilesAdd, input),
  updateProfile: (id: string, input: ProfileInput): Promise<Result<ProfileView>> => ipcRenderer.invoke(C.profilesUpdate, id, input),
  removeProfile: (id: string): Promise<boolean> => ipcRenderer.invoke(C.profilesRemove, id),
  openProfile: (id: string): Promise<void> => ipcRenderer.invoke(C.profilesOpen, id),
  onProfilesChanged: (cb: (profiles: ProfileView[]) => void): void => {
    ipcRenderer.on(C.profilesChanged, (_e, profiles: ProfileView[]) => cb(profiles));
  },
  getErrorState: (): Promise<ErrorState | null> => ipcRenderer.invoke(C.errorState),
  retry: (): Promise<void> => ipcRenderer.invoke(C.errorRetry),
  trustCertificate: (): Promise<void> => ipcRenderer.invoke(C.errorTrust),
  closePalette: (): Promise<void> => ipcRenderer.invoke(C.paletteClose),
  find: (text: string, forward: boolean, next: boolean): Promise<void> => ipcRenderer.invoke(C.findQuery, text, forward, next),
  closeFind: (): Promise<void> => ipcRenderer.invoke(C.findClose),
  onFindResult: (cb: (r: { matches: number; active: number }) => void): void => {
    ipcRenderer.on('bb:find:result', (_e, r: { matches: number; active: number }) => cb(r));
  },
});
