import { test as base, expect, type Page, type APIRequestContext, request as pwRequest } from '@playwright/test';
import { execFileSync } from 'node:child_process';
import { mkdtempSync, mkdirSync, writeFileSync, rmSync } from 'node:fs';
import os from 'node:os';
import path from 'node:path';

export const DEMO_PASSWORD = 'ByteBench123';
export type DemoUser = 'alice' | 'bob' | 'carol' | 'dan';

/** Console errors the app is known to produce that are not bugs in what a test exercises (keep this list short and justified). */
const IGNORED_CONSOLE = [
  /favicon/i,
  /Failed to load resource: the server responded with a status of (401|403|404)/, // probes and expected denials; unexpected 5xx are still caught below
];

export interface Observed {
  consoleErrors: string[];
  pageErrors: string[];
  serverErrors: string[]; // 5xx responses
}

export const test = base.extend<{
  observed: Observed;
  /** Opens a URL and waits until Blazor's interactive circuit is connected (so clicks and typing are not lost). */
  open: (url: string) => Promise<void>;
  login: (user: DemoUser | string, password?: string) => Promise<void>;
}>({
  baseURL: async ({}, use) => use(process.env.E2E_BASE_URL!),

  observed: [async ({ page }, use, testInfo) => {
    const o: Observed = { consoleErrors: [], pageErrors: [], serverErrors: [] };
    page.on('console', (m) => {
      if (m.type() !== 'error') return;
      const t = m.text();
      if (IGNORED_CONSOLE.some((r) => r.test(t))) return;
      o.consoleErrors.push(t);
    });
    page.on('pageerror', (e) => o.pageErrors.push(e.message));
    page.on('response', (r) => { if (r.status() >= 500) o.serverErrors.push(`${r.status()} ${r.request().method()} ${r.url()}`); });
    await use(o);
    // Invariants for every test: no uncaught JS errors, no 5xx responses, no console errors.
    if (testInfo.status === testInfo.expectedStatus) {
      expect.soft(o.pageErrors, 'uncaught JavaScript errors').toEqual([]);
      expect.soft(o.serverErrors, '5xx responses').toEqual([]);
      expect.soft(o.consoleErrors, 'console errors').toEqual([]);
    }
  }, { auto: true }],

  open: async ({ page }, use) => {
    await use((url) => openReady(page, url));
  },

  login: async ({ page }, use) => {
    await use((user, password = DEMO_PASSWORD) => loginAs(page, user, password));
  },
});

export { expect };
export type { Page } from '@playwright/test';

/** Waits for the Blazor Server circuit: its WebSocket is open and has received the server's first frames. */
export async function openReady(page: Page, url: string): Promise<void> {
  let frames = 0;
  let seen = false;
  const onWs = (ws: import('@playwright/test').WebSocket) => {
    if (!ws.url().includes('_blazor')) return;
    seen = true;
    ws.on('framereceived', () => { frames++; });
  };
  page.on('websocket', onWs);
  try {
    await page.goto(url);
    // Static (non-interactive) pages open no circuit: give it a moment to appear, otherwise carry on.
    await expect.poll(() => seen, { timeout: 5_000 }).toBe(true).catch(() => {});
    // Static server-rendered pages (profile, login) may open a socket that never sends; an interactive page that really fails to start fails its own assertions.
    if (seen) await expect.poll(() => frames, { timeout: 8_000 }).toBeGreaterThanOrEqual(2).catch(() => {});
  } finally {
    page.off('websocket', onWs);
  }
  await page.waitForTimeout(150);
}

/** Blazor's own "An unhandled error has occurred" bar and the reconnect dialog must not be showing. */
export async function expectNoBlazorError(page: Page): Promise<void> {
  await expect(page.locator('#blazor-error-ui')).toBeHidden();
  await expect(page.locator('#components-reconnect-modal.components-reconnect-show, .components-reconnect-show, .components-reconnect-failed, .components-reconnect-rejected')).toHaveCount(0);
}

export async function loginAs(page: Page, user: string, password = DEMO_PASSWORD): Promise<void> {
  await page.goto('/account/login');
  // The sign-in post is occasionally lost when the click lands while Blazor is still wiring the page (the form then just re-renders): retry the whole
  // step a few times instead of waiting out one long timeout.
  await expect(async () => {
    if (!new URL(page.url()).pathname.startsWith('/account/login')) return;
    await page.locator('#login-user').fill(user);
    await page.locator('#login-password').fill(password);
    await page.getByRole('button', { name: 'Sign in' }).click();
    await page.waitForURL((u) => !u.pathname.startsWith('/account/login'), { timeout: 8_000 });
  }).toPass({ timeout: 45_000 });
}

/** Creates a personal access token through the profile page (a static form) and returns it. */
export async function createToken(page: Page, name: string, scopes: { writeApi?: boolean; readApi?: boolean; writeRepo?: boolean } = { writeApi: true, readApi: true, writeRepo: true }): Promise<string> {
  await page.goto('/account/profile#tokens');
  await page.locator('#tk-name').fill(name);
  const boxes = page.locator('section#tokens input[type=checkbox]');
  // Scope checkboxes in page order: read repository, write repository, read API, write API.
  const want = [true, !!scopes.writeRepo, !!scopes.readApi, !!scopes.writeApi];
  for (let i = 0; i < 4; i++) await boxes.nth(i).setChecked(want[i]);
  await page.getByRole('button', { name: 'Create token' }).click();
  const v = await page.locator('#new-token').inputValue();
  if (!v.startsWith('gbp_')) throw new Error('No token was created: ' + v);
  return v;
}

/** A REST API client for test setup (Bearer personal access token). */
export async function apiFor(baseURL: string, token: string): Promise<APIRequestContext> {
  return pwRequest.newContext({ baseURL, extraHTTPHeaders: { Authorization: `Bearer ${token}`, Accept: 'application/json' } });
}

let counter = 0;
/** A short unique suffix, so tests never collide on names/keys when they run in parallel. */
export function uniq(prefix = ''): string {
  counter++;
  return `${prefix}${Date.now().toString(36).slice(-4)}${counter}${Math.random().toString(36).slice(2, 4)}`.toUpperCase();
}

/** Runs the real git CLI isolated from the developer's config. */
export class Git {
  readonly home = mkdtempSync(path.join(os.tmpdir(), 'bb-e2e-git-'));
  run(cwd: string | null, ...args: string[]): string {
    try { return this.exec(cwd, args); } catch (e) {
      const err = e as { stderr?: string; stdout?: string };
      throw new Error(`git ${args.join(' ').replace(/\/\/[^@/]*@/, '//***@')} failed:\n${err.stderr ?? ''}${err.stdout ?? ''}`);
    }
  }
  private exec(cwd: string | null, args: string[]): string {
    return execFileSync('git', ['-c', 'credential.helper=', '-c', 'user.name=E2E', '-c', 'user.email=e2e@example.com', '-c', 'init.defaultBranch=main', '-c', 'protocol.version=2', ...args], {
      cwd: cwd ?? this.home, encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'],
      env: { ...process.env, HOME: this.home, GIT_CONFIG_NOSYSTEM: '1', GIT_CONFIG_GLOBAL: path.join(this.home, '.none'), GIT_TERMINAL_PROMPT: '0', GIT_ASKPASS: '', GIT_ALLOW_PROTOCOL: 'file:http:https:ssh' },
    });
  }
  dir(name: string): string { const d = path.join(this.home, name); mkdirSync(d, { recursive: true }); return d; }
  write(dir: string, file: string, content: string): void { const f = path.join(dir, file); mkdirSync(path.dirname(f), { recursive: true }); writeFileSync(f, content); }
  /** http://user:password@host/git/KEY/slug.git for the e2e server. */
  url(baseURL: string, key: string, slug: string, user: string, password: string): string {
    const u = new URL(baseURL);
    u.username = encodeURIComponent(user); u.password = encodeURIComponent(password); u.pathname = `/git/${key}/${slug}.git`;
    return u.toString();
  }
  dispose(): void { try { rmSync(this.home, { recursive: true, force: true }); } catch { /* ignore */ } }
}
