import * as path from 'node:path';
import { pathToFileURL } from 'node:url';

/** What a WebContents is for. Every WebContents the app creates is registered here; anything unregistered is untrusted. */
export type WcContext =
  | { kind: 'ui'; windowId: number }
  | { kind: 'profile'; profileId: string; windowId: number }
  | { kind: 'workspace'; profileId: string };

export const wcContexts = new Map<number, WcContext>();

const root = path.join(__dirname, '..');
export const paths = {
  renderer: path.join(root, 'renderer'),
  preloadUi: path.join(root, 'preload', 'ui.js'),
  preloadWeb: path.join(root, 'preload', 'web.js'),
  assets: path.join(root, '..', 'resources'),
};
export const rendererUrlPrefix = pathToFileURL(paths.renderer + path.sep).href;

export function partitionFor(profileId: string): string {
  return `persist:profile-${profileId}`;
}
