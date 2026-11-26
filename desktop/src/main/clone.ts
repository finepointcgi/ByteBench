import * as fs from 'node:fs';
import * as path from 'node:path';

/**
 * "Clone with ByteBench": pure helpers. The actual process is started with an argument array (never a shell) in index/clone-run.
 */

const KEY = /^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$/;
const SLUG = /^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$/;

export interface RepoRef {
  key: string;
  slug: string;
}

/** Repository for a page path such as /p/WEB/repos/api/blob/main/x.cs or a clone path /git/WEB/api.git. */
export function repoFromPath(pathname: string): RepoRef | null {
  const page = /^\/p\/([^/]+)\/repos\/([^/]+)(?:\/|$)/.exec(pathname);
  const git = /^\/git\/([^/]+)\/([^/]+?)\.git(?:\/|$)/.exec(pathname);
  const m = page ?? git;
  if (!m) return null;
  let key: string;
  let slug: string;
  try {
    key = decodeURIComponent(m[1]!);
    slug = decodeURIComponent(m[2]!);
  } catch {
    return null;
  }
  return KEY.test(key) && SLUG.test(slug) && !slug.endsWith('.git') ? { key, slug } : null;
}

export function cloneUrl(origin: string, repo: RepoRef): string {
  return `${origin}/git/${encodeURIComponent(repo.key)}/${encodeURIComponent(repo.slug)}.git`;
}

/** Arguments for `git clone`. The URL never contains credentials; "--" stops a hostile value being read as an option. */
export function cloneArgs(url: string, targetDir: string): string[] {
  return ['clone', '--', url, targetDir];
}

/** Extra environment for git so a stored token authenticates without appearing in the process list. */
export function cloneEnv(username: string | undefined, token: string | undefined): Record<string, string> {
  const env: Record<string, string> = { GIT_TERMINAL_PROMPT: '0' };
  if (username && token) {
    const basic = Buffer.from(`${username}:${token}`).toString('base64');
    env.GIT_CONFIG_COUNT = '1';
    env.GIT_CONFIG_KEY_0 = 'http.extraHeader';
    env.GIT_CONFIG_VALUE_0 = `Authorization: Basic ${basic}`;
  }
  return env;
}

/** The folder the repository is cloned into: <chosen parent>/<slug>. Must not exist yet (or be empty). */
export function targetDirectory(parent: string, repo: RepoRef): { ok: true; dir: string } | { ok: false; error: string } {
  const dir = path.join(parent, repo.slug);
  if (path.dirname(dir) !== path.resolve(parent)) return { ok: false, error: 'Invalid folder name.' };
  if (fs.existsSync(dir)) {
    try {
      if (fs.readdirSync(dir).length > 0) return { ok: false, error: `The folder ${dir} already exists and is not empty.` };
    } catch {
      return { ok: false, error: `Cannot use ${dir}.` };
    }
  }
  return { ok: true, dir };
}

const GUI_EDITORS = new Set(['code', 'code-insiders', 'codium', 'cursor', 'windsurf', 'subl', 'zed', 'idea', 'webstorm', 'pycharm', 'rider', 'goland', 'mate', 'atom']);

/** An editor command from $VISUAL or $EDITOR, only when it is a known graphical editor (a terminal editor would not open from here). */
export function editorFromEnvironment(env: NodeJS.ProcessEnv): string | undefined {
  for (const value of [env.VISUAL, env.EDITOR]) {
    if (!value) continue;
    const first = value.trim().split(/\s+/)[0] ?? '';
    const name = path.basename(first).replace(/\.(cmd|exe|bat)$/i, '').toLowerCase();
    if (GUI_EDITORS.has(name)) return first;
  }
  return undefined;
}

/** Finds an executable by walking PATH (no shell, no `which`). */
export function findOnPath(names: readonly string[], envPath: string | undefined, isWindows: boolean): string | undefined {
  const dirs = (envPath ?? '').split(isWindows ? ';' : ':').filter(Boolean);
  const exts = isWindows ? ['.exe', '.cmd', '.bat', ''] : [''];
  for (const dir of dirs) {
    for (const name of names) {
      for (const ext of exts) {
        const candidate = path.join(dir, name + ext);
        try {
          if (fs.statSync(candidate).isFile()) return candidate;
        } catch {
          /* next */
        }
      }
    }
  }
  return undefined;
}
