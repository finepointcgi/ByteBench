import { spawn, type ChildProcess } from 'node:child_process';
import * as fs from 'node:fs';
import * as net from 'node:net';
import * as os from 'node:os';
import * as path from 'node:path';
import { _electron as electron, type ElectronApplication, type Page } from 'playwright-core';
import { afterAll, beforeAll, describe, expect, it } from 'vitest';

/**
 * Smoke test: launches the real app against a scratch ByteBench server (own port, temp database, temp folders),
 * adds the profile through the picker, signs in, sees the dashboard and checks that external navigation is blocked.
 * Needs the .NET SDK and `npm run build`. Set BB_E2E_SHOTS=<dir> to also save screenshots.
 */
const desktopDir = path.resolve(__dirname, '../..');
const serverDir = path.resolve(desktopDir, '..');
const shots = process.env.BB_E2E_SHOTS;

let tmp: string;
let server: ChildProcess;
let app: ElectronApplication;
let base: string;

function freePort(): Promise<number> {
  return new Promise((resolve, reject) => {
    const s = net.createServer();
    s.listen(0, '127.0.0.1', () => {
      const { port } = s.address() as net.AddressInfo;
      s.close(() => resolve(port));
    });
    s.on('error', reject);
  });
}

async function waitFor<T>(fn: () => Promise<T | undefined | false> | T | undefined | false, what: string, ms = 60_000): Promise<T> {
  const end = Date.now() + ms;
  let last: unknown;
  while (Date.now() < end) {
    try {
      const v = await fn();
      if (v) return v;
    } catch (e) {
      last = e;
    }
    await new Promise((r) => setTimeout(r, 250));
  }
  throw new Error(`Timed out waiting for ${what}${last ? `: ${String(last)}` : ''}`);
}

/** A page of the app (the app's own pages and server pages are all WebContents). */
async function pageWhere(pred: (url: string) => boolean, what: string): Promise<Page> {
  return waitFor(() => app.windows().find((p) => !p.isClosed() && pred(p.url())), what);
}

async function shot(page: Page, name: string): Promise<void> {
  if (shots) await page.screenshot({ path: path.join(shots, `${name}.png`) }).catch(() => undefined);
}

beforeAll(async () => {
  tmp = fs.mkdtempSync(path.join(os.tmpdir(), 'bb-e2e-'));
  const port = await freePort();
  const ssh = await freePort();
  base = `http://127.0.0.1:${port}`;
  server = spawn('dotnet', ['run', '--no-launch-profile'], {
    cwd: serverDir,
    env: {
      ...process.env,
      ASPNETCORE_ENVIRONMENT: 'Development',
      Security__SecureCookies: 'false',
      ASPNETCORE_URLS: base,
      Ssh__Port: String(ssh),
      ConnectionStrings__Default: `Data Source=${path.join(tmp, 'x.db')}`,
      Git__RepositoryRoot: path.join(tmp, 'repos'),
      Attachments__Root: path.join(tmp, 'attachments'),
      Ci__Enabled: 'false',
    },
    stdio: 'ignore',
    detached: true, // own process group, so the whole `dotnet run` tree can be stopped
  });
  await waitFor(async () => (await fetch(`${base}/healthz`).catch(() => null))?.ok, 'the scratch server', 180_000);

  app = await electron.launch({
    args: [desktopDir, `--user-data-dir=${path.join(tmp, 'userdata')}`],
    env: { ...process.env, BYTEBENCH_DISABLE_UPDATE: '1', BYTEBENCH_QUIT_ON_CLOSE: '1' },
  });
});

afterAll(async () => {
  if (app) {
    await Promise.race([app.close().catch(() => undefined), new Promise((r) => setTimeout(r, 10_000))]);
    app.process().kill('SIGKILL');
  }
  if (server?.pid) {
    try { process.kill(-server.pid, 'SIGTERM'); } catch { /* already gone */ }
  }
  await new Promise((r) => setTimeout(r, 1000));
  fs.rmSync(tmp, { recursive: true, force: true });
});

describe('desktop app against a scratch server', () => {
  it('shows the picker on first run and rejects a bad address', async () => {
    const picker = await pageWhere((u) => u.includes('app.html'), 'the picker');
    await picker.waitForSelector('#f-url');
    await shot(picker, 'picker');
    await picker.fill('#f-url', 'javascript:alert(1)');
    await picker.click('button[type=submit]');
    await expect(picker.locator('.msg.error').textContent()).resolves.toMatch(/web address|http/i);
  });

  it('adds the server, signs in and reaches the dashboard', async () => {
    const picker = await pageWhere((u) => u.includes('app.html'), 'the picker');
    await picker.fill('#f-name', 'Scratch');
    await picker.fill('#f-url', base);
    await picker.click('button[type=submit]');

    const web = await pageWhere((u) => u.startsWith(base), 'the server page');
    await web.waitForURL(/\/account\/login/, { timeout: 60_000 });
    await web.waitForSelector('#login-user');
    await shot(web, 'login');
    await web.fill('#login-user', 'alice');
    await web.fill('#login-password', 'ByteBench123');
    await web.click('button[type=submit], input[type=submit]');
    await web.waitForFunction(() => !location.pathname.startsWith('/account/login'), undefined, { timeout: 60_000 });
    await web.goto(`${base}/`);
    await web.waitForFunction(() => /project|issue|dashboard/i.test(document.body.innerText), undefined, { timeout: 60_000 });
    await shot(web, 'dashboard');

    const profileFile = JSON.parse(fs.readFileSync(path.join(tmp, 'userdata', 'profiles.json'), 'utf8'));
    expect(profileFile.profiles).toHaveLength(1);
    expect(profileFile.profiles[0].origin).toBe(base);
    expect(JSON.stringify(profileFile)).not.toContain('ByteBench123');
  });

  it('exposes only the narrow desktop bridge to the server page, with no Node', async () => {
    const web = await pageWhere((u) => u.startsWith(base), 'the server page');
    const probe = await web.evaluate(() => ({
      bridge: Object.keys((window as unknown as { byteBenchDesktop?: object }).byteBenchDesktop ?? {}).sort(),
      hasRequire: typeof (window as unknown as { require?: unknown }).require,
      hasProcess: typeof (window as unknown as { process?: unknown }).process,
      hasBb: typeof (window as unknown as { bb?: unknown }).bb,
    }));
    expect(probe.bridge).toEqual(['cloneRepository', 'getInfo', 'isDesktop', 'platform']);
    expect(probe.hasRequire).toBe('undefined');
    expect(probe.hasProcess).toBe('undefined');
    expect(probe.hasBb).toBe('undefined');
  });

  it('blocks navigation and window.open to other origins and hands links to the browser', async () => {
    const opened: string[] = [];
    await app.evaluate(({ shell }) => {
      // Never open a real browser from a test: record the request instead.
      (globalThis as unknown as { __opened: string[] }).__opened = [];
      (shell as unknown as { openExternal: (u: string) => Promise<void> }).openExternal = async (u: string) => {
        (globalThis as unknown as { __opened: string[] }).__opened.push(u);
      };
    });
    const web = await pageWhere((u) => u.startsWith(base), 'the server page');
    const before = web.url();

    await web.evaluate(() => { location.href = 'https://example.invalid/phish'; });
    await new Promise((r) => setTimeout(r, 1200));
    expect(web.url()).toBe(before);

    await web.evaluate(() => { location.href = 'file:///etc/hosts'; });
    await new Promise((r) => setTimeout(r, 800));
    expect(web.url()).toBe(before);

    await web.evaluate(() => { window.open('https://example.invalid/popup', '_blank'); });
    await new Promise((r) => setTimeout(r, 1200));
    expect(app.windows().filter((p) => p.url().includes('example.invalid'))).toHaveLength(0);

    const recorded = await app.evaluate(() => (globalThis as unknown as { __opened: string[] }).__opened);
    opened.push(...recorded);
    expect(opened.some((u) => u.startsWith('https://example.invalid/'))).toBe(true);
    expect(opened.some((u) => u.startsWith('file:'))).toBe(false);
  });

  it('opens an issue page inside the app', async () => {
    const web = await pageWhere((u) => u.startsWith(base), 'the server page');
    await web.goto(`${base}/search?q=a`);
    const link = await web.evaluate(async (b) => {
      const r = await fetch(`${b}/api/v1/projects`, { headers: { Accept: 'application/json' } });
      const projects = (await r.json()) as { key: string }[];
      const key = projects[0]?.key;
      const i = await fetch(`${b}/api/v1/projects/${key}/issues?perPage=1`);
      const issues = (await i.json()) as { key?: string; number?: number }[];
      return key && issues[0] ? `/p/${key}/issues/${issues[0].number ?? String(issues[0].key).split('-')[1]}` : null;
    }, base);
    if (link) {
      await web.goto(`${base}${link}`);
      await web.waitForFunction(() => document.body.innerText.trim().length > 20, undefined, { timeout: 30_000 });
      await shot(web, 'issue');
    }
  });

  it('notifications API answers for the signed-in session', async () => {
    const web = await pageWhere((u) => u.startsWith(base), 'the server page');
    const r = await web.evaluate(async (b) => {
      const info = (await (await fetch(`${b}/api/v1/server-info`)).json()) as { name: string };
      const n = await fetch(`${b}/api/v1/notifications`);
      return { name: info.name, status: n.status };
    }, base);
    expect(r).toEqual({ name: 'ByteBench', status: 200 });
  });
});
