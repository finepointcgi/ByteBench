// Real-Docker workspace verification: real image, real containers, real proxy. One serial flow over a fixture repository.
// Each test asserts what the product promises (README "Workspaces"); a failure is a product bug or a gap in the promise.
import { test, expect, loginAs, openReady } from '../../support/fixtures.ts';
import { type BrowserContext, type Page, type Frame } from '@playwright/test';
import { spawnSync } from 'node:child_process';
import { readFileSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import WebSocket from 'ws';
import {
  createFixtures, startWorkspace, waitForState, stopAndDelete, sh, shell, pollSetupDone, FIXTURE_SLUG, PROJECT, type Fixtures, type WorkspaceDto,
} from '../../support/workspace.ts';
import { containerFor, dockerLogs } from '../../support/docker.ts';
import { E2E_ROOT } from '../../support/server.ts';

test.describe.configure({ mode: 'serial' });

const REPO = `/workspace/${FIXTURE_SLUG}`; // where a workspace checks the repository out
const SECRET_NAME = 'E2E_SECRET';
const SECRET_VALUE = `s3cr3t-${Math.random().toString(36).slice(2)}-${Date.now()}`;

let ctx: BrowserContext;
let page: Page;
let fx: Fixtures;
let ws: WorkspaceDto; // the main workspace of the flow
let startedAt = 0;
let firstReadySeconds = 0;
const cleanup: string[] = [];

/** Waits for an iframe whose host matches (the ticket in its src is single-use, so we inspect what it loaded rather than re-requesting). */
async function frameOn(p: Page, host: RegExp, timeout = 60_000): Promise<Frame> {
  let found: Frame | undefined;
  await expect.poll(() => { found = p.frames().find((f) => f !== p.mainFrame() && host.test(new URL(f.url() || 'about:blank').hostname)); return !!found; }, { timeout }).toBe(true);
  return found!;
}

test.beforeAll(async ({ browser }) => {
  ctx = await browser.newContext({ baseURL: process.env.E2E_BASE_URL, viewport: { width: 1360, height: 860 } });
  page = await ctx.newPage();
  await loginAs(page, 'alice');
  fx = await createFixtures(page, process.env.E2E_BASE_URL!);

  // A personal secret, set through the UI before anything starts (secrets are injected when a workspace starts).
  await openReady(page, '/account/workspace');
  await page.getByTestId('ws-secret-add').click();
  await page.getByTestId('ws-secret-name').locator('input').or(page.locator('input[data-testid=ws-secret-name]')).fill(SECRET_NAME); await page.keyboard.press('Tab');
  await page.getByTestId('ws-secret-value').locator('input').or(page.locator('input[data-testid=ws-secret-value]')).fill(SECRET_VALUE); await page.keyboard.press('Tab');
  await page.getByTestId('ws-secret-save').click();
  await expect(page.getByTestId('ws-secrets')).toContainText(SECRET_NAME);
  await expect(page.locator('body')).not.toContainText(SECRET_VALUE); // never shown after saving
});

test.afterAll(async () => {
  if (process.env.E2E_KEEP_DOCKER !== '1') for (const id of [ws?.id, ...cleanup].filter(Boolean) as string[]) { try { await stopAndDelete(fx.api, id); } catch { /* the tier teardown removes leftovers */ } }
  fx?.git.dispose();
  await ctx?.close();
});

test('the start page reads the repository environment file', async () => {
  await openReady(page, `/p/${PROJECT}/repos/${FIXTURE_SLUG}/workspace`);
  await expect(page.getByTestId('ws-env-preview')).toContainText('.bytebench/workspace.yml');
  await expect(page.getByTestId('ws-env-preview')).toContainText('hello');
  await expect(page.getByTestId('ws-start')).toBeEnabled({ timeout: 120_000 }); // enabled once Docker and the image are ready
});

test('open the repository in a workspace: it starts and the IDE loads through the proxy origin', async () => {
  const ideStatuses: string[] = [];
  page.on('response', (r) => { if (new URL(r.url()).hostname.startsWith('ide-') && r.request().resourceType() !== 'websocket') ideStatuses.push(`${r.status()} ${new URL(r.url()).pathname}`); });
  await openReady(page, `/p/${PROJECT}/repos/${FIXTURE_SLUG}/workspace`);
  await expect(page.getByTestId('ws-start')).toBeEnabled({ timeout: 120_000 });
  startedAt = Date.now();
  await page.getByTestId('ws-start').click();
  await page.waitForURL(/\/workspaces\/[A-Za-z0-9_-]+$/, { timeout: 30_000 });
  const id = page.url().split('/').pop()!;
  ws = { id } as WorkspaceDto;
  await expect(page.getByTestId('ws-state')).toHaveText('Running', { timeout: 300_000 });

  // The IDE is an iframe on its own origin (ide-<id>.<BaseDomain>), never ByteBench's own.
  const ide = page.getByTestId('ws-ide');
  await expect(ide).toBeVisible({ timeout: 60_000 });
  const ideOrigin = new URL((await ide.getAttribute('src'))!, page.url());
  expect(ideOrigin.hostname).toMatch(/^ide-.+\.localhost$/);
  expect(ideOrigin.origin).not.toBe(new URL(page.url()).origin);

  const ideFrame = await frameOn(page, /^ide-/);
  await ideFrame.waitForLoadState('load', { timeout: 60_000 });
  await expect.poll(async () => (await ideFrame.content()).toLowerCase(), { timeout: 90_000, message: 'the IDE workbench did not load in the iframe' }).toMatch(/workbench/);
  expect(ideStatuses[0], 'the single-use ticket exchange redirects into the IDE').toMatch(/^302 \/_bb\/enter/);
  // vsda is Microsoft's proprietary signing module that OpenVSCode Server does not ship: its 404 is expected.
  expect(ideStatuses.filter((s) => /^[45]/.test(s) && !/favicon|manifest|\.map$|\/vsda\//.test(s)), `IDE responses:\n${ideStatuses.join('\n')}`).toEqual([]);
  page.removeAllListeners('response');

  // The ByteBench session cookie is host-only: the workspace origin must not receive it.
  const cookies = await ctx.cookies(`http://${ideOrigin.host}/`);
  expect(cookies.map((c) => c.name).filter((n) => /Identity|AspNetCore/.test(n))).toEqual([]);
});

test('the environment is provisioned: setup ran, packages installed, submodule fetched, env vars set', async () => {
  const setup = await pollSetupDone(fx.api, ws.id);
  firstReadySeconds = (Date.now() - startedAt) / 1000;
  expect(setup.state, setup.message ?? setup.lines.join('\n')).toBe('Succeeded');
  expect(sh(ws.id, `test -d ${REPO}/node_modules/is-odd && echo yes`).out).toContain('yes');
  expect(sh(ws.id, `cat ${REPO}/.setup-marker`).out).toContain('setup-ran');
  expect(sh(ws.id, `cat ${REPO}/libs/sub/lib.js`).out, 'the submodule is checked out').toContain('hello from the submodule');
  expect(sh(ws.id, 'echo "$E2E_PLAIN"').out.trim()).toBe('plain-value');
  expect(shell(ws.id, 'echo "$E2E_PLAIN"').trim(), 'an interactive terminal (the IDE terminal) sees the repository env too').toBe('plain-value');
  expect(sh(ws.id, 'id -u').out.trim(), 'runs unprivileged').toBe('1000');
});

test('run a task from the Tasks panel and open the preview of a server task', async () => {
  await openReady(page, `/workspaces/${ws.id}`);
  await expect(page.getByTestId('ws-state')).toHaveText('Running');
  await page.getByTestId('ws-tasks-toggle').click();
  const panel = page.getByTestId('ws-tasks-panel');
  await expect(panel).toBeVisible();

  const hello = panel.getByTestId('ws-task-hello');
  await hello.getByTestId('ws-task-run').click();
  await expect(hello.getByTestId('ws-task-log')).toContainText('hello-from-task', { timeout: 60_000 });

  const serve = panel.getByTestId('ws-task-serve');
  await serve.getByTestId('ws-task-run').click();
  await expect(serve.getByTestId('ws-task-state')).toHaveText('Running', { timeout: 60_000 });
  await expect(serve.getByTestId('ws-task-open')).toBeVisible();
  await serve.getByTestId('ws-task-open').click();
  await expect(page.getByTestId('ws-preview')).toBeVisible({ timeout: 30_000 });
  const frame = await frameOn(page, /^p8088-/);
  await expect.poll(async () => (await frame.content()), { timeout: 60_000, message: 'the preview did not render the served page' }).toContain('preview-ok');
  expect(new URL(frame.url()).hostname).toMatch(/^p8088-.+\.localhost$/);
  await serve.getByTestId('ws-task-stop').click();
  await expect(serve.getByTestId('ws-task-state')).not.toHaveText('Running', { timeout: 30_000 });
});

test('a secret is visible in the workspace shell but nowhere in logs, audit, inspect or pages', async () => {
  expect(shell(ws.id, `echo "$${SECRET_NAME}"`).trim(), 'secret visible in the workspace terminal').toBe(SECRET_VALUE); await page.keyboard.press('Tab');
  expect(sh(ws.id, `echo "$${SECRET_NAME}"`).out.trim(), 'secret visible to task-style shells').toBe(SECRET_VALUE); await page.keyboard.press('Tab');
  const inspect = spawnSync('docker', ['inspect', containerFor(ws.id)], { encoding: 'utf8' }).stdout;
  expect(inspect, 'the container definition (docker inspect) must not carry the secret').not.toContain(SECRET_VALUE); await page.keyboard.press('Tab');
  expect(dockerLogs(containerFor(ws.id)), 'container logs').not.toContain(SECRET_VALUE); await page.keyboard.press('Tab');
  expect(readFileSync(process.env.E2E_SERVER_LOG!, 'utf8'), 'server log').not.toContain(SECRET_VALUE); await page.keyboard.press('Tab');
  const audit = await (await fx.api.get(`/api/v1/workspaces/${ws.id}/audit`)).text();
  expect(audit, 'audit history').not.toContain(SECRET_VALUE); await page.keyboard.press('Tab');
  expect(await (await fx.api.get(`/api/v1/workspaces/${ws.id}/setup`)).text(), 'setup log').not.toContain(SECRET_VALUE); await page.keyboard.press('Tab');
  await openReady(page, `/workspaces/${ws.id}`);
  expect(await page.content(), 'the workspace page').not.toContain(SECRET_VALUE); await page.keyboard.press('Tab');
  // the AI user (bbai) shares the group but not the owner's credential files
  expect(sh(ws.id, 'cat /home/coder/.bb-env 2>&1', 'bbai').out, 'bbai must not read ~/.bb-env').not.toContain(SECRET_VALUE); await page.keyboard.press('Tab');
});

test('a viewer sees files read-only and never the owner secrets or credentials', async ({ browser }) => {
  sh(ws.id, `echo "TOKEN=hunter2" > ${REPO}/.env`);
  const r = await fx.api.post(`/api/v1/workspaces/${ws.id}/shares`, { data: { user: 'bob', role: 'viewer' } });
  expect(r.status(), await r.text()).toBeLessThan(300);
  const bob = await browser.newContext({ baseURL: process.env.E2E_BASE_URL });
  try {
    const bp = await bob.newPage();
    await loginAs(bp, 'bob');
    await openReady(bp, `/workspaces/shared/${ws.id}`);
    await expect(bp.getByTestId('viewer-panel')).toBeVisible({ timeout: 30_000 });
    // The viewer's window starts in the repository checkout.
    await expect(bp.getByTestId('viewer-entry').filter({ hasText: 'README.md' })).toBeVisible();
    const inRepo = (await bp.getByTestId('viewer-entry').allInnerTexts()).join(' ');
    for (const hidden of ['.env', '.git/', '.bb-env', '.git-credentials']) expect(inRepo.split(/\s+/), `a viewer must not see ${hidden}`).not.toContain(hidden);
    await bp.getByTestId('viewer-entry').filter({ hasText: 'README.md' }).click();
    await expect(bp.getByTestId('viewer-file')).toContainText('workspace fixture');
    await expect(bp.getByTestId('shared-ide'), 'a viewer gets no IDE').toHaveCount(0);
    expect(await bp.locator('body').innerText()).not.toContain('hunter2');
  } finally { await bob.close(); }
  await fx.api.delete(`/api/v1/workspaces/${ws.id}/shares`);
});

test('local editor: connect an SSH key, the tunnel handshakes and a real ssh login works through it', async () => {
  // The administrator switch is off by default.
  await openReady(page, '/admin/workspaces');
  const sw = page.getByTestId('ws-editor-switch').locator('input').or(page.locator('input[data-testid=ws-editor-switch]'));
  await expect(sw).toBeAttached();
  if (!(await sw.isChecked())) { await sw.check({ force: true }); await expect(sw).toBeChecked(); }

  const key = path.join(fx.git.home, 'e2e_ed25519');
  spawnSync('ssh-keygen', ['-q', '-t', 'ed25519', '-N', '', '-f', key, '-C', 'e2e']);
  const pub = readFileSync(`${key}.pub`, 'utf8').trim();
  const c = await fx.api.post(`/api/v1/workspaces/${ws.id}/editor/connect`, { data: { publicKey: pub } });
  expect(c.status(), await c.text()).toBeLessThan(300);

  const base = new URL(process.env.E2E_BASE_URL!);
  const wsUrl = `ws://${base.host}/api/v1/workspaces/${ws.id}/editor/tunnel`;

  // 1. the raw handshake: the first bytes are the SSH banner of the workspace's sshd
  const banner = await new Promise<string>((resolve, reject) => {
    const sock = new WebSocket(wsUrl, { headers: { Authorization: `Bearer ${fx.token}` } });
    const t = setTimeout(() => { sock.terminate(); reject(new Error('no SSH banner within 30s')); }, 30_000);
    sock.on('message', (d) => { clearTimeout(t); sock.close(); resolve(d.toString()); });
    sock.on('error', (e) => { clearTimeout(t); reject(e); });
    sock.on('unexpected-response', (_q, res) => { clearTimeout(t); reject(new Error(`tunnel refused: HTTP ${res.statusCode}`)); });
  });
  expect(banner).toMatch(/^SSH-2\.0-/);

  // 2. a browser session cookie must not open the tunnel (only an explicit write_api token)
  const cookieHeader = (await ctx.cookies()).map((x) => `${x.name}=${x.value}`).join('; ');
  const denied = await new Promise<number>((resolve) => {
    const sock = new WebSocket(wsUrl, { headers: { Cookie: cookieHeader } });
    sock.on('open', () => { sock.close(); resolve(101); });
    sock.on('unexpected-response', (_q, res) => resolve(res.statusCode ?? 0));
    sock.on('error', () => resolve(-1));
  });
  expect([401, 403]).toContain(denied);

  // 3. the real handshake and login: ssh -o ProxyCommand through the tunnel bridge
  const tsx = path.join(E2E_ROOT, 'node_modules', '.bin', 'tsx');
  const bridge = path.join(E2E_ROOT, 'support', 'ws-stdio-bridge.ts');
  const login = spawnSync('ssh', [
    '-i', key, '-o', 'IdentitiesOnly=yes', '-o', 'StrictHostKeyChecking=no', '-o', 'UserKnownHostsFile=/dev/null', '-o', 'BatchMode=yes', '-o', 'ConnectTimeout=30',
    '-o', `ProxyCommand=${tsx} ${bridge} ${wsUrl} ${fx.token}`, 'coder@bytebench-ws', 'echo ssh-ok:$(id -un)',
  ], { encoding: 'utf8', timeout: 60_000 });
  expect(login.stdout + login.stderr).toContain('ssh-ok:coder');

  // 4. `ssh host command` is a non-interactive shell too: it must see the repository env and the secrets.
  const sshArgs = [
    '-i', key, '-o', 'IdentitiesOnly=yes', '-o', 'StrictHostKeyChecking=no', '-o', 'UserKnownHostsFile=/dev/null', '-o', 'BatchMode=yes', '-o', 'ConnectTimeout=30',
    '-o', `ProxyCommand=${tsx} ${bridge} ${wsUrl} ${fx.token}`, 'coder@bytebench-ws',
  ];
  const viaSsh = spawnSync('ssh', [...sshArgs, `printf 'plain=%s secret=%s' "$E2E_PLAIN" "$${SECRET_NAME}"`], { encoding: 'utf8', timeout: 60_000 });
  expect(viaSsh.stdout, 'ssh host command sees the injected environment').toBe(`plain=plain-value secret=${SECRET_VALUE}`);
  const viaSshBash = spawnSync('ssh', [...sshArgs, `bash -c 'printf %s "$${SECRET_NAME}"'`], { encoding: 'utf8', timeout: 60_000 });
  expect(viaSshBash.stdout, 'a nested bash -c over ssh too').toBe(SECRET_VALUE);
});

test('Stop and Resume keep the installed packages and do not re-run setup', async () => {
  await openReady(page, `/workspaces/${ws.id}`);
  await page.getByTestId('ws-stop').click();
  await page.getByRole('dialog').getByRole('button', { name: 'Stop', exact: true }).click(); // the confirmation
  await expect(page.getByTestId('ws-state')).toHaveText('Stopped', { timeout: 120_000 });
  const stopped = await waitForState(fx.api, ws.id, 'Stopped', 60_000);
  expect(stopped.stopReason).toBeTruthy();

  const r = await fx.api.post(`/api/v1/workspaces/${ws.id}/resume`);
  expect(r.status(), await r.text()).toBe(202);
  await waitForState(fx.api, ws.id, 'Running', 240_000);
  await pollSetupDone(fx.api, ws.id);
  expect(sh(ws.id, `test -d ${REPO}/node_modules/is-odd && echo yes`).out, 'packages survive Stop/Resume').toContain('yes');
  expect(sh(ws.id, `grep -c setup-ran ${REPO}/.setup-marker`).out.trim(), 'setup must not re-run on resume').toBe('1');
  expect(shell(ws.id, `echo "$${SECRET_NAME}"`).trim(), 'secrets are injected again after resume').toBe(SECRET_VALUE); await page.keyboard.press('Tab');
  await openReady(page, `/workspaces/${ws.id}`);
  await expect(page.getByTestId('ws-state')).toHaveText('Running');
  const ideFrame = await frameOn(page, /^ide-/);
  await expect.poll(async () => (await ideFrame.content()).toLowerCase(), { timeout: 90_000, message: 'the IDE did not load after Resume' }).toMatch(/workbench/);
});

test('egress policy: a repository allow-list lets an allowed host through and blocks a denied one', async () => {
  // A branch whose environment file narrows the network to the npm registry only.
  const work = fx.workDir;
  fx.git.run(work, 'checkout', '-b', 'egress-allowlist');
  writeFileSync(path.join(work, '.bytebench', 'workspace.yml'), 'env:\n  E2E_PLAIN: egress\nsubmodules: false\negress:\n  mode: allowlist\n  allow:\n    - registry.npmjs.org\n');
  fx.git.run(work, 'add', '-A'); fx.git.run(work, 'commit', '-m', 'egress allow-list');
  fx.git.run(work, 'push', 'origin', 'egress-allowlist');
  fx.git.run(work, 'checkout', 'main');

  const w = await startWorkspace(fx.api, FIXTURE_SLUG, 'egress-allowlist');
  cleanup.push(w.id);
  await waitForState(fx.api, w.id, 'Running', 300_000);
  const eg = await (await fx.api.get(`/api/v1/workspaces/${w.id}/egress`)).json();
  expect(eg.mode, JSON.stringify(eg)).toBe('allowlist');

  const allowed = sh(w.id, "curl -sS -m 30 -o /dev/null -w '%{http_code}' https://registry.npmjs.org/is-odd");
  expect(allowed.out.trim(), `allowed host: ${allowed.out}`).toBe('200');
  const denied = sh(w.id, "curl -sS -m 20 -o /dev/null -w '%{http_code}' https://example.org/ 2>&1; echo \" exit=$?\"");
  expect(denied.out, 'a host that is not on the list must not be reachable').not.toMatch(/^200/);
  expect(denied.out).toMatch(/403|refused|denied|Could not|Failed|exit=[1-9]/);
  // the refusal is recorded
  await expect.poll(async () => JSON.stringify(await (await fx.api.get(`/api/v1/workspaces/${w.id}/egress`)).json()), { timeout: 30_000 }).toContain('example.org');
  // the default (public-only) mode on the main workspace: link-local addresses are not reachable
  expect(sh(ws.id, "curl -sS -m 6 -o /dev/null -w '%{http_code}' http://169.254.169.254/ 2>&1; echo \" exit=$?\"").out, 'link-local must be blocked in public-only mode').toMatch(/exit=[1-9]/);
  await stopAndDelete(fx.api, w.id);
});

test('per-user limits refuse a start over the cap with a clear message', async () => {
  const before = await (await fx.api.get('/api/v1/workspaces/limits')).json();
  const running = before.usage.running as number;
  const set = await fx.api.put('/api/v1/workspaces/limits', { data: { maxRunning: running } });
  expect(set.status(), await set.text()).toBeLessThan(300);
  try {
    const r = await fx.api.post(`/api/v1/projects/${PROJECT}/repos/${FIXTURE_SLUG}/workspaces`, { data: {} });
    expect(r.status(), await r.text()).toBe(422);
    expect(await r.text()).toMatch(/limit|running/i);
  } finally {
    await fx.api.put('/api/v1/workspaces/limits', { data: { maxRunning: before.limits.maxRunning } });
  }
});

test('a prebuild makes the next start faster (setup already done)', async () => {
  const slug = FIXTURE_SLUG;
  const set = await fx.api.put(`/api/v1/projects/${PROJECT}/repos/${slug}/prebuild-settings`, { data: { enabled: true } });
  expect(set.status(), await set.text()).toBeLessThan(300);
  const rb = await fx.api.post(`/api/v1/projects/${PROJECT}/repos/${slug}/prebuilds`, { data: {} });
  expect(rb.status(), await rb.text()).toBe(202);
  const pb = await rb.json();
  await expect.poll(async () => (await (await fx.api.get(`/api/v1/projects/${PROJECT}/repos/${slug}/prebuilds/${pb.id}`)).json()).state, { timeout: 420_000, intervals: [3000] })
    .toMatch(/Ready|Failed/);
  const done = await (await fx.api.get(`/api/v1/projects/${PROJECT}/repos/${slug}/prebuilds/${pb.id}`)).json();
  expect(done.state, `${done.error ?? ''}\n${done.log ?? ''}`).toBe('Ready');
  expect(done.usable).toBe(true);

  // A fresh workspace (no volumes to reuse): remove the old one first.
  await stopAndDelete(fx.api, ws.id);
  const t0 = Date.now();
  const w = await startWorkspace(fx.api, slug);
  ws = { id: w.id } as WorkspaceDto;
  await waitForState(fx.api, w.id, 'Running', 300_000);
  const setup = await pollSetupDone(fx.api, w.id, 240_000);
  const secondReadySeconds = (Date.now() - t0) / 1000;
  expect(setup.state, setup.message ?? '').toBe('UpToDate'); // the prebuild already ran the setup commands
  expect(sh(w.id, `cat ${REPO}/.slow-marker`).out, 'the slow setup step ran in the prebuild, not again').toContain('slow-setup-done');
  expect(secondReadySeconds, `first start ${firstReadySeconds.toFixed(1)}s vs prebuilt start ${secondReadySeconds.toFixed(1)}s`).toBeLessThan(firstReadySeconds - 6);
});

test('the workspace audit trail recorded the lifecycle', async () => {
  const hist = await (await fx.api.get(`/api/v1/workspaces/${ws.id}/audit`)).text();
  expect(hist).toMatch(/workspace\.started|workspace\.created/);
});
