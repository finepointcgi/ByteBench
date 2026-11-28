// Real-Docker verification of collaborator isolation and of the shell environment (README "Workspaces" > Sharing / Environment).
// Real image, real containers, real proxy: nothing here is faked. One serial flow on one small workspace.
//
// What is promised and therefore asserted:
//  * a collaborator gets their own OS user (bbc<slot>), own home (mode 700), own OpenVSCode server on 127.0.0.1:13400+slot with their own token;
//  * they cannot read the owner's ~/.bb-env, ~/.git-credentials, IDE token or process environment;
//  * /workspace is shared through the group (setgid, umask 002), without "May push" no credential exists in their home, with it a user-scoped token
//    works for git over HTTP and revoking kills it;
//  * End sharing / Stop wipe their home and processes, a reused slot starts clean;
//  * the owner's own flows (IDE, tasks, secrets, git) still work; a workspace from an older image gets a clear error; at most 10 collaborators;
//  * the injected environment reaches every kind of bash shell, not only interactive ones.
import { test, expect, loginAs, openReady } from '../../support/fixtures.ts';
import { type BrowserContext, type Page, type Frame } from '@playwright/test';
import { spawnSync } from 'node:child_process';
import { readFileSync } from 'node:fs';
import {
  createSimpleRepo, startWorkspace, waitForState, stopAndDelete, pollSetupDone, exec, type Fixtures, type WorkspaceDto,
} from '../../support/workspace.ts';
import { PROJECT } from '../../support/workspace.ts';
import { containerFor, dockerLogs } from '../../support/docker.ts';

test.describe.configure({ mode: 'serial' });

const SLUG = 'collab-fixture';
const REPO = `/workspace/${SLUG}`;
const SECRET_NAME = 'E2E_SECRET';
const SECRET_VALUE = `s3cr3t-${Math.random().toString(36).slice(2)}-${Date.now()}`;

let ctx: BrowserContext;
let page: Page;
let fx: Fixtures;
let ws: WorkspaceDto;
let base: URL;

async function frameOn(p: Page, host: RegExp, timeout = 60_000): Promise<Frame> {
  let found: Frame | undefined;
  await expect.poll(() => { found = p.frames().find((f) => f !== p.mainFrame() && host.test(new URL(f.url() || 'about:blank').hostname)); return !!found; }, { timeout }).toBe(true);
  return found!;
}

/** All processes in the container as `user<TAB>args` rows. */
function processes(id: string): { user: string; args: string }[] {
  const r = exec(id, 'coder', 'ps', '-eo', 'user:20,args');
  return r.out.split('\n').slice(1).map((l) => l.trim()).filter(Boolean).map((l) => { const m = /^(\S+)\s+(.*)$/.exec(l); return { user: m?.[1] ?? '', args: m?.[2] ?? '' }; });
}

/** The slot (1..10) of a collaborator: the bbc<slot> user that runs an OpenVSCode server on the port. */
function ideUsers(id: string): string[] {
  return [...new Set(processes(id).filter((p) => /openvscode-server/.test(p.args) && /^bbc\d+$/.test(p.user)).map((p) => p.user))];
}

function bash(id: string, user: string, script: string): { code: number; out: string } {
  return exec(id, user, 'bash', '-c', script);
}

/** Like a terminal in the IDE: the IDE servers run with umask 002 and their terminals inherit it (a bare docker exec has 022). */
function term(id: string, user: string, script: string): { code: number; out: string } {
  return exec(id, user, 'bash', '-c', `umask 002; ${script}`);
}

async function invite(user: string, role: 'collaborator' | 'viewer' = 'collaborator', allowPush = false): Promise<{ status: number; text: string }> {
  const r = await fx.api.post(`/api/v1/workspaces/${ws.id}/shares`, { data: { user, role, allowPush } });
  return { status: r.status(), text: await r.text() };
}

async function waitIdeUsers(id: string, count: number): Promise<string[]> {
  let users: string[] = [];
  await expect.poll(() => { users = ideUsers(id); return users.length; }, { timeout: 60_000, message: 'collaborator IDE servers' }).toBe(count);
  return users.sort();
}

async function register(browser: import('@playwright/test').Browser, name: string): Promise<void> {
  const c = await browser.newContext({ baseURL: process.env.E2E_BASE_URL });
  try {
    const p = await c.newPage();
    await p.goto('/account/register');
    await p.locator('#reg-user').fill(name);
    await p.locator('#reg-name').fill(name);
    await p.locator('#reg-email').fill(`${name}@example.com`);
    await p.locator('#reg-password').fill('ByteBench123');
    await p.locator('#reg-confirm').fill('ByteBench123');
    await p.getByRole('button', { name: 'Create account' }).click();
    await p.waitForURL((u) => !u.pathname.startsWith('/account/register'), { timeout: 20_000 });
  } finally { await c.close(); }
}

const cleanup: string[] = [];

test.beforeAll(async ({ browser }) => {
  base = new URL(process.env.E2E_BASE_URL!);
  ctx = await browser.newContext({ baseURL: process.env.E2E_BASE_URL, viewport: { width: 1360, height: 860 } });
  page = await ctx.newPage();
  await loginAs(page, 'alice');
  fx = await createSimpleRepo(page, process.env.E2E_BASE_URL!, SLUG, 'collab-repo');

  // A personal secret (injected when the workspace starts)
  await openReady(page, '/account/workspace');
  await page.getByTestId('ws-secret-add').click();
  await page.getByTestId('ws-secret-name').locator('input').or(page.locator('input[data-testid=ws-secret-name]')).fill(SECRET_NAME); await page.keyboard.press('Tab');
  await page.getByTestId('ws-secret-value').locator('input').or(page.locator('input[data-testid=ws-secret-value]')).fill(SECRET_VALUE); await page.keyboard.press('Tab');
  await page.getByTestId('ws-secret-save').click();
  await expect(page.getByTestId('ws-secrets')).toContainText(SECRET_NAME);

  // Anyone who can view the repository may be invited (so the cap test needs no project memberships).
  await openReady(page, '/admin/workspaces');
  await page.getByTestId('ws-sharing-mode').click();
  await page.getByRole('option', { name: /Anyone who can view/ }).click();
  await expect(page.getByText('Sharing policy saved.')).toBeVisible();
});

test.afterAll(async () => {
  if (process.env.E2E_KEEP_DOCKER !== '1') for (const id of [ws?.id, ...cleanup].filter(Boolean) as string[]) { try { await stopAndDelete(fx.api, id); } catch { /* the tier teardown removes leftovers */ } }
  fx?.git.dispose();
  await ctx?.close();
});

test('start the workspace; setup ran; the owner is uid 1000 with a private home', async () => {
  const w = await startWorkspace(fx.api, SLUG);
  ws = { id: w.id } as WorkspaceDto;
  await waitForState(fx.api, ws.id, 'Running', 300_000);
  const setup = await pollSetupDone(fx.api, ws.id);
  expect(setup.state, setup.message ?? setup.lines.join('\n')).toBe('Succeeded');
  expect(exec(ws.id, 'coder', 'id', '-u').out.trim()).toBe('1000');
  expect(exec(ws.id, 'coder', 'stat', '-c', '%a', '/home/coder').out.trim(), 'the owner home is private').toBe('700');
  expect(exec(ws.id, 'coder', 'stat', '-c', '%a', '/home/coder/.bb-env').out.trim()).toBe('600');
  const wsDir = exec(ws.id, 'coder', 'stat', '-c', '%a %G', '/workspace').out.trim();
  expect(wsDir, '/workspace is setgid and group-writable').toBe('2775 coder');
});

test('the injected environment reaches every kind of bash shell, and only the owner', async () => {
  const want = SECRET_VALUE;
  const probe = `printf %s "$${SECRET_NAME}"`;
  const plain = 'printf %s "$E2E_PLAIN"';
  const ways: Record<string, string[]> = {
    'bash -c (docker exec, no login, not interactive)': ['bash', '-c'],
    'bash -lc (login, not interactive)': ['bash', '-lc'],
    'bash -ic (interactive: the IDE terminal)': ['bash', '-ic'],
    'sh -lc (login dash: /etc/profile and ~/.profile)': ['sh', '-lc'],
  };
  for (const [name, argv] of Object.entries(ways)) {
    expect(exec(ws.id, 'coder', ...argv, probe).stdout.trim(), `secret via ${name}`).toBe(want);
    expect(exec(ws.id, 'coder', ...argv, plain).stdout.trim(), `repository env via ${name}`).toBe('plain-value');
  }
  // A script file run by bash, and a shell started by a shell: both inherit.
  expect(exec(ws.id, 'coder', 'bash', '-c', `printf '#!/bin/bash\\nprintf %%s "$${SECRET_NAME}"\\n' > /tmp/e2e-probe.sh; chmod +x /tmp/e2e-probe.sh; /tmp/e2e-probe.sh; env -i HOME=/home/coder BASH_ENV=/usr/local/lib/bb-env.sh bash -c '${probe}'`).stdout.trim(), 'scripts and env -i bash').toBe(want + want);
  // The PATH additions of ~/.bb-env are applied once, however many hooks run.
  const path1 = exec(ws.id, 'coder', 'bash', '-ic', 'echo $PATH').stdout.trim().split(':');
  expect(path1.filter((p) => p === '/home/coder/.bbcache/go/bin').length, 'PATH not extended twice').toBe(1);
  // sh -c (dash) reads no startup file at all: that is the shell's contract, so ByteBench's tasks and terminals use bash. The value must not leak there either way.
  const dash = exec(ws.id, 'coder', 'sh', '-c', probe).stdout.trim();
  test.info().annotations.push({ type: 'known-limit', description: `sh -c (dash) reads no startup files; sees secret: ${dash === want}` });

  // Not for anyone else: the AI user and a root exec read nothing of the owner's.
  expect(exec(ws.id, 'bbai', 'bash', '-lc', probe).stdout.trim(), 'bbai gets no secret').toBe('');
  expect(exec(ws.id, 'bbai', 'bash', '-ic', probe).stdout.trim(), 'bbai (interactive) gets no secret').toBe('');
  // ... and the value is not in anything that logs or lists.
  const inspect = spawnSync('docker', ['inspect', containerFor(ws.id)], { encoding: 'utf8' }).stdout;
  expect(inspect, 'docker inspect').not.toContain(want);
  expect(dockerLogs(containerFor(ws.id)), 'container logs').not.toContain(want);
  expect(readFileSync(process.env.E2E_SERVER_LOG!, 'utf8'), 'server log').not.toContain(want);
  expect(await (await fx.api.get(`/api/v1/workspaces/${ws.id}/audit`)).text(), 'audit').not.toContain(want);
  expect(exec(ws.id, 'bbai', 'bash', '-c', 'cat /home/coder/.bb-env; ls -A /home/coder').out, 'bbai cannot read the owner home').toMatch(/Permission denied/);
});

test('a task shell sees the secret and the repository env', async () => {
  const r = await fx.api.post(`/api/v1/workspaces/${ws.id}/tasks/secret/run`);
  expect(r.status(), await r.text()).toBe(202);
  await expect.poll(async () => JSON.stringify((await (await fx.api.get(`/api/v1/workspaces/${ws.id}/tasks/secret/logs`)).json()).lines), { timeout: 60_000 }).toContain('[task finished');
  const lines = JSON.stringify((await (await fx.api.get(`/api/v1/workspaces/${ws.id}/tasks/secret/logs`)).json()).lines);
  expect(lines, 'masked in the log, never plain').not.toContain(SECRET_VALUE);
  expect(lines).toContain('task-sees:***');
  const h = await fx.api.post(`/api/v1/workspaces/${ws.id}/tasks/hello/run`);
  expect(h.status(), await h.text()).toBe(202);
  await expect.poll(async () => JSON.stringify((await (await fx.api.get(`/api/v1/workspaces/${ws.id}/tasks/hello/logs`)).json()).lines), { timeout: 60_000 }).toContain('[task finished');
  expect(JSON.stringify((await (await fx.api.get(`/api/v1/workspaces/${ws.id}/tasks/hello/logs`)).json()).lines)).toContain('hello-from-task plain-value');
});

let bobSlot = '';
let bobCredentialLine = '';

test('invite a collaborator without "May push": own user, own IDE on loopback, no credential, no access to the owner', async ({ browser }) => {
  const r = await invite('bob');
  expect(r.status, r.text).toBe(201);
  const users = await waitIdeUsers(ws.id, 1);
  bobSlot = users[0];
  const n = Number(bobSlot.slice(3));
  const port = 13400 + n;

  // bb-collab-ide started OpenVSCode on 127.0.0.1:13400+slot, as that user, and only there.
  const procs = processes(ws.id).filter((p) => p.user === bobSlot && /openvscode-server/.test(p.args));
  expect(procs.length).toBeGreaterThan(0);
  expect(procs[0].args).toContain(`--port ${port}`);
  expect(procs[0].args).toContain('--host 127.0.0.1');
  const up = bash(ws.id, 'coder', `curl -s -o /dev/null -w '%{http_code}' http://127.0.0.1:${port}/version`);
  expect(up.out.trim(), 'the collaborator IDE answers on loopback').toMatch(/^(200|3\d\d|40[13])$/);
  const viaEth = bash(ws.id, 'coder', `ip=$(hostname -i | awk '{print $1}'); curl -s -m 4 -o /dev/null -w '%{http_code}' http://$ip:${port}/version; echo " exit=$?"`);
  expect(viaEth.out, 'not reachable on the container address').toMatch(/^000|exit=[1-9]/);
  // Their own connection token, readable by them only; the IDE refuses a request without it.
  const tok = bash(ws.id, bobSlot, 'cat "$HOME/.bb-token"').out.trim();
  expect(tok.length).toBeGreaterThan(20);
  expect(bash(ws.id, bobSlot, 'stat -c %a "$HOME/.bb-token"').out.trim()).toBe('600');
  expect(bash(ws.id, 'coder', `curl -s -o /dev/null -w '%{http_code}' http://127.0.0.1:${port}/`).out.trim(), 'no token, no IDE').toBe('403');
  expect(bash(ws.id, 'coder', `curl -s -o /dev/null -w '%{http_code}' 'http://127.0.0.1:${port}/?tkn=${tok}'`).out.trim(), 'their token opens it').toMatch(/^(200|302)$/);
  expect(bash(ws.id, bobSlot, 'cat /home/coder/.bb-token /tmp/.bb-token 2>&1').out, 'not the owner token either').not.toContain(tok);
  expect(exec(ws.id, 'bbai', 'bash', '-c', 'cat /home/' + bobSlot + '/.bb-token').out, 'bbai cannot read the collaborator token').toMatch(/Permission denied/);

  // Their home is theirs (HOME is the slot's, not /home/coder).
  expect(bash(ws.id, bobSlot, 'echo $HOME').out.trim()).toBe(`/home/${bobSlot}`);
  expect(bash(ws.id, bobSlot, 'stat -c %a "$HOME"').out.trim()).toBe('700');
  // Without "May push": no credential anywhere in their home, no helper configured.
  expect(bash(ws.id, bobSlot, 'ls -A "$HOME"; echo; cat "$HOME/.git-credentials" 2>&1').out).not.toMatch(/\.git-credentials\n|@host\.docker\.internal/);
  expect(bash(ws.id, bobSlot, 'test -e "$HOME/.git-credentials"; echo $?').out.trim()).toBe('1');
  expect(bash(ws.id, bobSlot, 'git config --global --get credential.helper; echo "rc=$?"').out).toContain('rc=1');
  expect(bash(ws.id, bobSlot, 'git config --global user.name').out.trim()).toBeTruthy();

  // What they must not read: the owner's environment file, git credential, IDE token, process environment, home listing.
  const denied = (cmd: string) => bash(ws.id, bobSlot, cmd);
  expect(denied('cat /home/coder/.bb-env').out).toMatch(/Permission denied/);
  expect(denied('cat /home/coder/.git-credentials').out).toMatch(/Permission denied/);
  expect(denied('ls /home/coder').out).toMatch(/Permission denied/);
  expect(denied('cat /home/coder/.bb-token /tmp/.bb-token 2>&1; true').out, 'the owner IDE token').not.toMatch(/^[0-9a-f-]{20,}$/m);
  const psOut = bash(ws.id, 'coder', 'ps -u coder -o pid=,args=').out;
  const ownerPid = /^\s*(\d+)\s+\/opt\/openvscode-server\/node .*server-main\.js/m.exec(psOut)?.[1] ?? '';
  expect(ownerPid, psOut).toMatch(/^\d+$/);
  const ownerPort = /server-main\.js .*--port (\d+)/.exec(psOut.split('\n').find((l) => l.includes(ownerPid) && l.includes('server-main'))!)?.[1] ?? '3000';
  expect(denied(`cat /proc/${ownerPid}/environ 2>&1 | head -c 200`).out, 'the owner process environment').toMatch(/Permission denied/);
  expect(denied(`kill -0 ${ownerPid} 2>&1`).out, 'cannot signal the owner').toMatch(/not permitted/i);
  expect(denied(`curl -s -o /dev/null -w "%{http_code}" http://127.0.0.1:${ownerPort}/`).out.trim(), "the owner's IDE refuses a request without its token").toBe('403');
  expect(denied('printf %s "$E2E_SECRET"; env | grep -c "s3cr3t" ').out, 'no secret in their shell').not.toContain(SECRET_VALUE);
  expect(exec(ws.id, bobSlot, 'bash', '-lc', 'printf %s "$E2E_SECRET$E2E_PLAIN"').stdout.trim(), 'neither login shell').toBe('');
  expect(exec(ws.id, bobSlot, 'bash', '-ic', 'printf %s "$E2E_SECRET$E2E_PLAIN"').stdout.trim(), 'nor interactive').toBe('');

  // Through the proxy, in a browser: bob reaches *his* IDE on its own origin.
  const bobCtx = await browser.newContext({ baseURL: process.env.E2E_BASE_URL, viewport: { width: 1360, height: 860 } });
  try {
    const bp = await bobCtx.newPage();
    await loginAs(bp, 'bob');
    await openReady(bp, `/workspaces/shared/${ws.id}`);
    await expect(bp.getByTestId('shared-role')).toHaveText(/Collaborator/i, { timeout: 30_000 });
    const ide = bp.getByTestId('shared-ide');
    await expect(ide).toBeVisible({ timeout: 30_000 });
    const origin = new URL((await ide.getAttribute('src'))!, bp.url());
    expect(origin.hostname).toMatch(/^ide-.+\.localhost$/);
    const frame = await frameOn(bp, /^ide-/);
    await expect.poll(async () => (await frame.content()).toLowerCase(), { timeout: 90_000, message: "bob's IDE did not load" }).toMatch(/workbench/);
    // The IDE answered *as bbc<slot>*: the server processes of that user have a connection from the proxy path.
    expect(processes(ws.id).some((p) => p.user === bobSlot && /server-main|openvscode/.test(p.args))).toBe(true);
  } finally { await bobCtx.close(); }
});

test('/workspace is shared through the group: both sides read and edit each other\'s files, new files stay group-writable', async () => {
  // The IDE servers run with umask 002 (so does everything they spawn: terminals).
  const umasks = bash(ws.id, 'coder', 'for p in $(pgrep -f "^/opt/openvscode-server/node .*--port"); do echo "$(stat -c %U /proc/$p) $(grep Umask /proc/$p/status | tr -d "\\t")"; done | sort -u').out;
  expect(umasks).toMatch(/coder Umask:0002/);
  expect(umasks).toMatch(new RegExp(`${bobSlot} Umask:0002`));
  // A file of the checkout (made by ByteBench's checkout, as the owner): the collaborator can edit it, the owner can edit the collaborator's file.
  expect(term(ws.id, bobSlot, `cd ${REPO} && echo "edit by bob" >> README.md && cat README.md | tail -1`).out, 'collaborator edits a checkout file').toContain('edit by bob');
  expect(term(ws.id, bobSlot, `umask 002; mkdir ${REPO}/bobdir && echo hi > ${REPO}/bobdir/f.txt && stat -c "%G %a" ${REPO}/bobdir`).out.trim(), 'new directories inherit the group (setgid)').toMatch(/^coder (2775|2770)$/);
  expect(term(ws.id, 'coder', `echo "edit by owner" >> ${REPO}/bobdir/f.txt && tail -1 ${REPO}/bobdir/f.txt`).out, 'the owner edits the collaborator file').toContain('edit by owner');
  expect(bash(ws.id, 'coder', `umask 002; echo owner > ${REPO}/ownerfile.txt`).code).toBe(0);
  expect(term(ws.id, bobSlot, `echo "bob appends" >> ${REPO}/ownerfile.txt && tail -1 ${REPO}/ownerfile.txt`).out, 'the collaborator edits the owner file').toContain('bob appends');
  // git inside the shared checkout works for both and keeps the repository group-writable (core.sharedRepository=group).
  const c1 = term(ws.id, bobSlot, `cd ${REPO} && git add -A && git commit -q -m "bob commit" && git log -1 --format=%an`);
  expect(c1.code, c1.out).toBe(0);
  expect(c1.out.trim()).toBeTruthy();
  const c2 = term(ws.id, 'coder', `cd ${REPO} && echo more >> README.md && git add -A && git commit -q -m "owner commit after bob" && git log --format=%s | head -3`);
  expect(c2.code, c2.out).toBe(0);
  expect(c2.out).toContain('bob commit');
  // Without "May push" the push is refused by the server (no credential), not by luck.
  const push = term(ws.id, bobSlot, `cd ${REPO} && GIT_TERMINAL_PROMPT=0 git push origin HEAD:refs/heads/bob-nopush 2>&1; echo "rc=$?"`);
  expect(push.out, 'no credential, no push').toMatch(/rc=[1-9]/);
  expect(push.out).not.toContain('bob-nopush -> bob-nopush');
});

test('"May push": a user-scoped token works for git over HTTP; changing it replaces the token; revoking kills it', async () => {
  const bob = await invite('bob', 'collaborator', true);
  expect(bob.status, bob.text).toBeLessThan(300);
  // Same slot, same IDE (idempotent), now with a credential in their own home only.
  expect(await waitIdeUsers(ws.id, 1)).toEqual([bobSlot]);
  expect(bash(ws.id, bobSlot, 'stat -c %a "$HOME/.git-credentials"').out.trim()).toBe('600');
  bobCredentialLine = bash(ws.id, bobSlot, 'cat "$HOME/.git-credentials"').out.trim();
  expect(bobCredentialLine).toMatch(/^http:\/\/bob:[^@]+@host\.docker\.internal:\d+$/);
  expect(bash(ws.id, 'coder', 'cat /home/' + bobSlot + '/.git-credentials').out, 'the owner cannot read it either').toMatch(/Permission denied/);
  expect(bash(ws.id, 'bbai', 'cat /home/' + bobSlot + '/.git-credentials').out).toMatch(/Permission denied/);
  expect(bash(ws.id, bobSlot, 'git config --global credential.helper').out.trim()).toBe('store');
  // The owner's own credential is a different one.
  expect(bash(ws.id, 'coder', 'cat "$HOME/.git-credentials"').out).not.toContain(bobCredentialLine.split('@')[0]);

  // git over HTTP as bob from inside the workspace: push works.
  const push = bash(ws.id, bobSlot, `cd ${REPO} && GIT_TERMINAL_PROMPT=0 git push origin HEAD:refs/heads/bob-push 2>&1; echo "rc=$?"`);
  expect(push.out, push.out).toContain('rc=0');
  const refs = fx.git.run(fx.git.dir('refs-check'), 'ls-remote', fx.fixtureUrl);
  expect(refs).toContain('refs/heads/bob-push');
  // The token is bob's: the server attributes the push to bob and the token is not alice's.
  const url = bobCredentialLine.replace('host.docker.internal', base.hostname);
  const work = fx.workDir;
  fx.git.run(work, 'fetch', fx.fixtureUrl);
  fx.git.run(work, 'push', url + '/git/' + PROJECT + '/' + SLUG + '.git', 'FETCH_HEAD:refs/heads/bob-from-host'); // works while the token is valid
});

test('revoking kills the token, the processes and the home; the slot is reused clean', async () => {
  const url = bobCredentialLine.replace('host.docker.internal', base.hostname) + '/git/' + PROJECT + '/' + SLUG + '.git';
  const oldToken = bash(ws.id, bobSlot, 'cat "$HOME/.bb-token"').out.trim();
  const before = processes(ws.id).filter((p) => p.user === bobSlot).length;
  expect(before).toBeGreaterThan(0);
  // A stray background process of theirs must go too.
  bash(ws.id, bobSlot, 'setsid sleep 1000 >/dev/null 2>&1 < /dev/null & touch "$HOME/leftover.txt" /tmp/bob-tmp-file');

  const del = await fx.api.delete(`/api/v1/workspaces/${ws.id}/shares/bob`);
  expect(del.status(), await del.text()).toBe(204);
  // 1. the token no longer authenticates a push
  const dead = fx.git.run.bind(fx.git);
  let failed = false;
  try { dead(fx.workDir, 'push', url, 'FETCH_HEAD:refs/heads/bob-after-revoke'); } catch { failed = true; }
  expect(failed, 'a revoked collaborator token must not push').toBe(true);
  expect(fx.git.run(fx.git.dir('refs-check'), 'ls-remote', fx.fixtureUrl)).not.toContain('bob-after-revoke');
  // 2. the processes are gone, the home is empty, /tmp is clean
  await expect.poll(() => processes(ws.id).filter((p) => p.user === bobSlot).length, { timeout: 30_000 }).toBe(0);
  expect(bash(ws.id, bobSlot, 'ls -A "$HOME" | wc -l').out.trim(), 'home emptied').toBe('0');
  expect(bash(ws.id, bobSlot, 'find /tmp -mindepth 1 -maxdepth 1 -user "$(id -u)" | wc -l').out.trim()).toBe('0');
  // 3. his own work stays in the shared checkout (it is the project's, not his home)
  expect(bash(ws.id, 'coder', `test -f ${REPO}/bobdir/f.txt && echo kept`).out).toContain('kept');
  // 4. the IDE port no longer answers
  const n = Number(bobSlot.slice(3));
  expect(bash(ws.id, 'coder', `curl -s -m 3 -o /dev/null -w '%{http_code}' http://127.0.0.1:${13400 + n}/version`).out.trim()).toBe('000');

  // Slot reuse: the next person gets the same slot, fresh token, nothing of bob's.
  const c = await invite('carol');
  expect(c.status, c.text).toBe(201);
  const users = await waitIdeUsers(ws.id, 1);
  expect(users, 'the freed slot is reused').toEqual([bobSlot]);
  const newToken = bash(ws.id, bobSlot, 'cat "$HOME/.bb-token"').out.trim();
  expect(newToken).not.toBe(oldToken);
  const listing = bash(ws.id, bobSlot, 'ls -A "$HOME"').out;
  expect(listing).not.toContain('leftover.txt');
  expect(listing).not.toContain('.git-credentials');
  expect(bash(ws.id, bobSlot, 'git config --global user.name').out.trim(), 'identity is carol\'s').not.toBe('bob');
  expect(bash(ws.id, bobSlot, 'cat "$HOME/.bash_history" 2>&1').out).toMatch(/No such file/);
  const bobCreds = await fx.api.delete(`/api/v1/workspaces/${ws.id}/shares/carol`);
  expect(bobCreds.status()).toBe(204);
  await expect.poll(() => ideUsers(ws.id).length, { timeout: 30_000 }).toBe(0);
});

test('the owner\'s own flows still work after the permission changes: IDE, tasks, git, secrets', async () => {
  await openReady(page, `/workspaces/${ws.id}`);
  await expect(page.getByTestId('ws-state')).toHaveText('Running');
  const ide = await frameOn(page, /^ide-/);
  await expect.poll(async () => (await ide.content()).toLowerCase(), { timeout: 90_000, message: 'the owner IDE did not load' }).toMatch(/workbench/);
  const r = await fx.api.post(`/api/v1/workspaces/${ws.id}/tasks/hello/run`);
  expect(r.status(), await r.text()).toBe(202);
  await expect.poll(async () => JSON.stringify((await (await fx.api.get(`/api/v1/workspaces/${ws.id}/tasks/hello/logs`)).json()).lines), { timeout: 60_000 }).toContain('hello-from-task plain-value');
  // git as the owner: commit and push with the owner's own credential.
  const g = exec(ws.id, 'coder', 'bash', '-c', `cd ${REPO} && git config user.email >/dev/null; echo owner-push >> README.md && git add -A && git -c user.name=Alice -c user.email=alice@example.com commit -q -m "owner push" && GIT_TERMINAL_PROMPT=0 git push origin HEAD:refs/heads/owner-push 2>&1; echo "rc=$?"`);
  expect(g.out, g.out).toContain('rc=0');
  expect(fx.git.run(fx.git.dir('refs-check'), 'ls-remote', fx.fixtureUrl)).toContain('refs/heads/owner-push');
  expect(exec(ws.id, 'coder', 'bash', '-lc', `printf %s "$${SECRET_NAME}"`).out.trim()).toBe(SECRET_VALUE);
  // AI user still can work in the shared folder and still has no access to the owner's home.
  expect(bash(ws.id, 'bbai', `cd ${REPO} && git status --short | head -1; echo ok`).out).toContain('ok');
});

test('End sharing wipes every collaborator\'s home and processes; at most 10 collaborators', async ({ browser }) => {
  test.setTimeout(10 * 60_000);
  const names = ['carol', 'dan', 'bob'];
  const extra: string[] = [];
  for (let i = 1; i <= 8; i++) { const n = `collab${Date.now().toString(36).slice(-4)}${i}`; extra.push(n); await register(browser, n); }
  const all = [...names, ...extra];
  for (const n of all.slice(0, 10)) {
    const r = await invite(n, 'collaborator', n === 'dan');
    expect(r.status, `${n}: ${r.text}`).toBe(201);
  }
  const users = await waitIdeUsers(ws.id, 10);
  expect(users.map((u) => Number(u.slice(3))).sort((a, b) => a - b), 'ten distinct slots').toEqual([1, 2, 3, 4, 5, 6, 7, 8, 9, 10]);
  const eleventh = await invite(all[10]);
  expect(eleventh.status, eleventh.text).toBeGreaterThanOrEqual(400);
  expect(eleventh.text).toMatch(/At most 10/i);

  // Every collaborator has a different token and cannot use another's port; none can read another's home.
  const toks = users.map((u) => bash(ws.id, u, 'cat "$HOME/.bb-token"').out.trim());
  expect(new Set(toks).size).toBe(10);
  const a = users[0], b = users[1];
  expect(bash(ws.id, a, `ls /home/${b} 2>&1`).out).toMatch(/Permission denied/);
  expect(bash(ws.id, a, `curl -s -o /dev/null -w '%{http_code}' 'http://127.0.0.1:${13400 + Number(b.slice(3))}/?tkn=${toks[0]}'`).out.trim(), "one collaborator's token is no key to another's IDE").toBe('403');
  // Only dan may push: exactly one credential file among the ten homes.
  const withCred = users.filter((u) => bash(ws.id, u, 'test -f "$HOME/.git-credentials" && echo y').out.includes('y'));
  expect(withCred.length).toBe(1);

  const end = await fx.api.delete(`/api/v1/workspaces/${ws.id}/shares`);
  expect(end.status(), await end.text()).toBe(204);
  await expect.poll(() => ideUsers(ws.id).length, { timeout: 60_000 }).toBe(0);
  for (const u of users) {
    expect(processes(ws.id).filter((p) => p.user === u).length, `${u} has no processes`).toBe(0);
    expect(bash(ws.id, u, 'ls -A "$HOME" | wc -l').out.trim(), `${u} home is empty`).toBe('0');
  }
  expect(await (await fx.api.get(`/api/v1/workspaces/${ws.id}/shares`)).json()).toEqual([]);
});

test('a workspace from an older image (no collaborator support) gets a clear error, and nothing is half-granted', async () => {
  // Simulate the old image: the starter script is not there (root owns /usr/local/bin and may rename inside it).
  const mv = exec(ws.id, 'root', 'mv', '/usr/local/bin/bb-collab-ide', '/usr/local/bin/bb-collab-ide.off');
  expect(mv.code, mv.out).toBe(0);
  try {
    const r = await invite('bob');
    expect(r.status, r.text).toBeGreaterThanOrEqual(400);
    expect(r.text).toMatch(/older workspace image/i);
    expect(r.text).toMatch(/new workspace|start a new/i);
    expect(ideUsers(ws.id)).toEqual([]);
    expect(await (await fx.api.get(`/api/v1/workspaces/${ws.id}/shares`)).json(), 'no share is left behind').toEqual([]);
  } finally {
    exec(ws.id, 'root', 'mv', '/usr/local/bin/bb-collab-ide.off', '/usr/local/bin/bb-collab-ide');
  }
});

test('Stop wipes the collaborators; after Resume the slot starts clean and sharing is over', async () => {
  const r = await invite('dan', 'collaborator', true);
  expect(r.status, r.text).toBe(201);
  const [slot] = await waitIdeUsers(ws.id, 1);
  expect(bash(ws.id, slot, 'test -f "$HOME/.git-credentials" && echo cred').out).toContain('cred');
  await fx.api.post(`/api/v1/workspaces/${ws.id}/stop`);
  await waitForState(fx.api, ws.id, 'Stopped', 120_000);
  const res = await fx.api.post(`/api/v1/workspaces/${ws.id}/resume`);
  expect(res.status(), await res.text()).toBe(202);
  await waitForState(fx.api, ws.id, 'Running', 240_000);
  expect(ideUsers(ws.id), 'no collaborator IDE survives a stop').toEqual([]);
  const left = bash(ws.id, slot, 'ls -A "$HOME"').out;
  for (const f of ['.bb-token', '.git-credentials', '.gitconfig', '.bb-ide.log', '.openvscode-server']) expect(left, `${f} must not survive a stop`).not.toContain(f);
  expect(await (await fx.api.get(`/api/v1/workspaces/${ws.id}/shares`)).json(), 'sharing ended with the stop').toEqual([]);
  // and the owner still has the environment after the restart
  expect(exec(ws.id, 'coder', 'bash', '-c', `printf %s "$${SECRET_NAME}"`).out.trim()).toBe(SECRET_VALUE);
  // a collaborator can be invited again on the resumed workspace
  const again = await invite('bob');
  expect(again.status, again.text).toBe(201);
  await waitIdeUsers(ws.id, 1);
});
