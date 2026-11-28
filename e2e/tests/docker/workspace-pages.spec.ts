// The workspace *pages* against a real container: top bar, git bar (commit / push / open PR), ports menu, the AI review panel,
// sharing (viewer and collaborator), history and network tabs, the start/resume button on issues and pull requests, and who may open what.
// The lifecycle itself (IDE origin, secrets, egress, prebuild speed-up, ...) is workspaces.spec.ts.
import { test, expect, loginAs, openReady, createToken, apiFor, type Page } from '../../support/fixtures.ts';
import { type BrowserContext, type APIRequestContext } from '@playwright/test';
import { DatabaseSync } from 'node:sqlite';
import path from 'node:path';
import { spawnSync } from 'node:child_process';
import { startWorkspace, waitForState, stopAndDelete, sh, type WorkspaceDto } from '../../support/workspace.ts';
import { containerFor } from '../../support/docker.ts';

test.describe.configure({ mode: 'serial' });

const REPO = '/workspace/web-app';

let ctx: BrowserContext;
let page: Page;
let api: APIRequestContext;
let ws: WorkspaceDto;

/** The page's invariants (no console error, ...) must hold except for what the OpenVSCode workbench itself logs inside its own iframe. */
const IDE_NOISE = /Framing 'http:\/\/ide-|CloseEvent|Failed to load resource: the server responded with a status of (404|502)/;
const consoleErrors: string[] = [];

function db(): DatabaseSync { return new DatabaseSync(path.join(process.env.E2E_DATA_DIR!, 'e2e.db')); }

async function theme(p: Page, mode: 'light' | 'dark'): Promise<void> {
  await p.emulateMedia({ colorScheme: mode });
  await p.context().addCookies([{ name: 'bytebench-theme', value: mode, url: process.env.E2E_BASE_URL! }]);
  await p.addInitScript((m) => { try { localStorage.setItem('bytebench-theme', m); } catch { /* ignore */ } }, mode);
}

async function inViewport(p: Page, testId: string): Promise<boolean> {
  const box = await p.getByTestId(testId).boundingBox();
  const vp = p.viewportSize()!;
  return !!box && box.x >= 0 && box.y >= 0 && box.x + box.width <= vp.width + 0.5 && box.y + box.height <= vp.height + 0.5;
}

test.beforeAll(async ({ browser }) => {
  ctx = await browser.newContext({ baseURL: process.env.E2E_BASE_URL, viewport: { width: 1360, height: 860 } });
  page = await ctx.newPage();
  page.on('console', (m) => { if (m.type() === 'error' && !IDE_NOISE.test(m.text()) && !new URL(m.location().url || 'about:blank').hostname.startsWith('ide-')) consoleErrors.push(m.text()); });
  await loginAs(page, 'alice');
  const token = await createToken(page, 'e2e-workspace-pages');
  api = await apiFor(process.env.E2E_BASE_URL!, token);
  ws = await startWorkspace(api, 'web-app');
  await waitForState(api, ws.id, 'Running');
});

test.afterAll(async () => {
  expect(consoleErrors, 'console errors on the workspace page (outside the IDE iframe)').toEqual([]);
  if (process.env.E2E_KEEP_DOCKER !== '1' && ws?.id) await stopAndDelete(api, ws.id).catch(() => {});
  await ctx?.close();
});

test('top bar: every action is reachable at desktop and phone width, light and dark', async ({ browser }) => {
  for (const [w, h, mode] of [[1360, 860, 'light'], [390, 800, 'dark']] as const) {
    const c = await browser.newContext({ baseURL: process.env.E2E_BASE_URL, viewport: { width: w, height: h } });
    const p = await c.newPage();
    await theme(p, mode);
    await loginAs(p, 'alice');
    await openReady(p, `/workspaces/${ws.id}`);
    await expect(p.getByTestId('ws-state')).toHaveText('Running');
    await expect(p.getByTestId('ws-page')).toHaveAttribute('data-fullscreen', 'true');
    for (const id of ['ws-back', 'ws-gitbar', 'ws-editor', 'ws-preview-menu', 'ws-tasks-toggle', 'ws-ai-toggle', 'ws-tools-toggle', 'ws-restart', 'ws-stop', 'ws-fullscreen-toggle']) {
      await expect(p.getByTestId(id), `${id} at ${w}px`).toBeVisible();
      expect(await inViewport(p, id), `${id} is inside the ${w}px viewport`).toBe(true);
    }
    expect(await p.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth), 'no horizontal page scroll').toBeLessThanOrEqual(0);
    // Leaving full screen shows ByteBench's own menu and the page stays usable.
    await p.getByTestId('ws-fullscreen-toggle').click();
    await expect(p.getByTestId('ws-page')).toHaveAttribute('data-fullscreen', 'false');
    await expect(p.getByTestId('ws-stop')).toBeVisible();
    await c.close();
  }
});

test('git bar: branch, changed files, commit, push and the pull request dialog', async () => {
  await openReady(page, `/workspaces/${ws.id}`);
  await expect(page.getByTestId('ws-git-branch')).toContainText('main');
  await expect(page.getByTestId('ws-git-changes')).toHaveText('0 changed');
  await expect(page.getByTestId('ws-git-commit')).toBeDisabled();

  const branch = 'e2e/pages-' + Date.now().toString(36);
  expect(sh(ws.id, `cd ${REPO} && git checkout -q -b ${branch} && echo changed-by-e2e > e2e-note.txt && echo ok`).out).toContain('ok');
  await expect(page.getByTestId('ws-git-branch')).toContainText(branch, { timeout: 30_000 });
  await expect(page.getByTestId('ws-git-branch')).toContainText('(not pushed)');
  await expect(page.getByTestId('ws-git-changes')).toHaveText('1 changed', { timeout: 30_000 });

  // Commit: the dialog lists the changed file; commit & push in one go.
  await page.getByTestId('ws-git-commit').click();
  const dlg = page.getByRole('dialog');
  await expect(dlg.getByTestId('ws-status')).toContainText('e2e-note.txt');
  await dlg.getByTestId('ws-commit-message').locator('textarea').or(dlg.locator('textarea[data-testid=ws-commit-message]')).fill('E2E: add a note');
  await dlg.getByTestId('ws-commit-push').click();
  await expect(dlg.locator('pre')).toHaveCount(2, { timeout: 60_000 }); // the status and the command's output
  console.log('commit dialog:', (await dlg.innerText()).replace(/\n+/g, ' | '));
  await expect(dlg.locator('pre').nth(1)).toContainText('HEAD -> ' + branch);
  await dlg.getByRole('button', { name: 'Close' }).click();
  await expect(page.getByTestId('ws-git-changes')).toHaveText('0 changed', { timeout: 30_000 });
  await expect(page.getByTestId('ws-git-branch')).not.toContainText('(not pushed)', { timeout: 30_000 });
  await expect(page.getByTestId('ws-git-push')).toBeDisabled(); // nothing ahead of the remote

  // A second local commit: ahead 1, Push enabled and works.
  sh(ws.id, `cd ${REPO} && echo more >> e2e-note.txt && git commit -qam "E2E: second" && echo done`);
  await expect(page.getByTestId('ws-git-branch')).toContainText('↑1', { timeout: 30_000 });
  await expect(page.getByTestId('ws-git-push')).toBeEnabled();
  await page.getByTestId('ws-git-push').click();
  await expect(page.getByTestId('ws-git-branch')).not.toContainText('↑', { timeout: 30_000 });

  // Open PR: the dialog opens a pull request (a snackbar/alert shows the result), a second try says it already exists.
  await page.getByTestId('ws-git-pr').click();
  const pr = page.getByRole('dialog');
  await pr.getByTestId('ws-pr-title').locator('input').or(pr.locator('input[data-testid=ws-pr-title]')).fill('E2E pull request');
  await pr.getByTestId('ws-pr-open').click();
  await expect(pr.getByTestId('ws-pr-result')).toContainText('Pull request opened', { timeout: 30_000 });
  await pr.getByRole('button', { name: 'Close' }).click();
  await openReady(page, '/p/WEB/repos/web-app/pull-requests');
  await expect(page.getByText('E2E pull request').first()).toBeVisible();
});

test('ports menu: empty state, then a listening port opens the preview', async () => {
  await openReady(page, `/workspaces/${ws.id}`);
  await page.getByTestId('ws-preview-menu').click();
  await expect(page.getByRole('menuitem', { name: /Nothing is listening yet/ })).toBeVisible({ timeout: 15_000 });
  await page.keyboard.press('Escape');

  spawnSync('docker', ['exec', '-d', '-u', 'coder', containerFor(ws.id), 'sh', '-c', 'cd /tmp && python3 -m http.server 8088'], { encoding: 'utf8' });
  // The menu re-renders as the port list refreshes: open it and click in one go, retrying when it was replaced under the pointer.
  await expect.poll(async () => {
    await page.getByTestId('ws-preview-menu').click();
    try { await page.getByRole('menuitem', { name: /Port 8088/ }).click({ timeout: 3_000 }); return true; } catch { await page.keyboard.press('Escape'); return false; }
  }, { timeout: 60_000, intervals: [1_000] }).toBe(true);
  await expect(page.getByTestId('ws-preview')).toBeVisible({ timeout: 30_000 });
  await expect(page.getByTestId('ws-preview')).toHaveAttribute('src', /p8088-/);
  await page.getByRole('button', { name: 'Close the preview' }).click();
  await expect(page.getByTestId('ws-preview')).toHaveCount(0);
});

test('connect-from-editor dialog explains that local editors are off, then accepts a key once an admin turns them on', async () => {
  await openReady(page, `/workspaces/${ws.id}`);
  await page.getByTestId('ws-editor').click();
  await expect(page.getByRole('dialog')).toContainText(/turned off on this server/);
  await page.getByRole('dialog').getByRole('button', { name: 'Close' }).click();

  await openReady(page, '/admin/workspaces');
  await page.getByTestId('ws-editor-switch').click();
  await expect(page.getByTestId('ws-editor-switch').locator('input').or(page.locator('input[data-testid=ws-editor-switch]'))).toBeChecked();
  try {
    await openReady(page, `/workspaces/${ws.id}`);
    await page.getByTestId('ws-editor').click();
    await expect(page.getByTestId('ws-editor-panel')).toContainText('SSH public key');
    await page.locator('textarea[data-testid=ws-editor-key], [data-testid=ws-editor-key] textarea').fill('this is not a key');
    await page.getByTestId('ws-editor-connect').click();
    await expect(page.getByTestId('ws-editor-panel').locator('.mud-alert')).toContainText(/key/i); // a readable error, not a crash
    await page.getByRole('dialog').getByRole('button', { name: 'Close' }).click();
  } finally {
    await openReady(page, '/admin/workspaces');
    await page.getByTestId('ws-editor-switch').click(); // back off
  }
});

test('AI review panel: per-hunk selection, accept, reject, accept all and a conflict', async () => {
  // README.md padded to 30 lines and committed, so two edits far apart make two hunks.
  const lines = sh(ws.id, `cat ${REPO}/README.md`).out.replace(/\n$/, '').split('\n');
  while (lines.length < 30) lines.push('filler line ' + lines.length);
  const base2 = lines.join('\n') + '\n';
  spawnSync('docker', ['exec', '-i', '-u', 'coder', containerFor(ws.id), 'sh', '-c', `cat > ${REPO}/README.md`], { input: base2 });
  sh(ws.id, `cd ${REPO} && git commit -qam "E2E: readme base"`);
  const editedLines = [...lines];
  editedLines[0] = 'Edited first line';
  editedLines[25] = 'Edited line 26';
  const edited = editedLines.join('\n') + '\n';
  const d = db();
  try {
    const wid = (d.prepare('select Id from Workspaces where PublicId = ?').get(ws.id) as { Id: number }).Id;
    const uid = (d.prepare("select Id from AspNetUsers where UserName = 'alice'").get() as { Id: number }).Id;
    const set = d.prepare("insert into AiChangeSets(WorkspaceId,UserId,Prompt,Summary,State,CreatedAt) values (?,?,?,?,0,datetime('now'))").run(wid, uid, 'E2E: edit the readme and add files', 'x').lastInsertRowid;
    const ins = d.prepare('insert into AiChangeFiles(ChangeSetId,Path,Kind,OriginalText,NewText,Decision) values (?,?,?,?,?,0)');
    ins.run(set, 'README.md', 1, base2, edited);
    ins.run(set, 'src/ai-new.js', 0, null, 'console.log("ai");\n');
    ins.run(set, 'stale.txt', 1, 'what the AI saw\n', 'what the AI wants\n'); // the workspace holds something else: a conflict
  } finally { d.close(); }
  sh(ws.id, `cd ${REPO} && printf 'something else\\n' > stale.txt && git add stale.txt && git commit -qm "E2E: stale"`);

  await openReady(page, `/workspaces/${ws.id}`);
  await page.getByTestId('ws-ai-toggle').click();
  const panel = page.getByTestId('ws-review-panel');
  await expect(panel).toBeVisible();
  await expect(page.getByTestId('ws-mode-review')).toBeVisible();
  await expect(page.getByTestId('ws-mode-direct')).toBeVisible();
  await expect(panel.getByTestId('ws-change-file')).toHaveCount(3);
  await expect(panel.getByText(/^Hunk 1/)).toBeVisible();
  await expect(panel.getByText(/^Hunk 2/)).toBeVisible();

  // Take only hunk 1 of README.md: "Accept selected".
  await panel.getByText(/^Hunk 2/).click();
  const readme = panel.getByTestId('ws-change-file').filter({ hasText: 'README.md' });
  await expect(readme.getByTestId('ws-accept-file')).toContainText('selected');
  await readme.getByTestId('ws-accept-file').click();
  await expect.poll(() => sh(ws.id, `head -1 ${REPO}/README.md`).out.trim(), { timeout: 30_000 }).toBe('Edited first line');
  expect(sh(ws.id, `grep -c "Edited line 26" ${REPO}/README.md; true`).out.trim(), 'hunk 2 was not applied').toBe('0');

  // Accept all: the new file is created, the stale one is a conflict with a readable message.
  await panel.getByTestId('ws-accept-all').click();
  await expect(page.getByText(/couldn't be applied because they changed/)).toBeVisible({ timeout: 30_000 });
  await expect.poll(() => sh(ws.id, `cat ${REPO}/src/ai-new.js`).out, { timeout: 30_000 }).toContain('console.log("ai")');
  expect(sh(ws.id, `cat ${REPO}/stale.txt`).out.trim(), 'a conflicting file is left alone').toBe('something else');
  await expect(panel.getByTestId('ws-no-changes')).toBeVisible();

  // Switching the mode is remembered.
  await page.getByTestId('ws-mode-direct').click();
  await expect(page.getByTestId('ws-no-changes')).toContainText('applied directly', { timeout: 15_000 });
  await page.getByTestId('ws-mode-review').click();
  await expect(page.getByTestId('ws-no-changes')).toContainText('Nothing waiting', { timeout: 15_000 });
});

test('sharing: a viewer sees the files read-only, a collaborator gets a prepared home; history and network tabs render', async ({ browser }) => {
  await openReady(page, `/workspaces/${ws.id}`);
  await page.getByTestId('ws-tools-toggle').click();
  const tools = page.getByTestId('ws-tools');
  await expect(tools.getByTestId('ws-invite')).toBeVisible();
  const user = page.locator('input[data-testid=ws-invite-user], [data-testid=ws-invite-user] input');

  // An unknown person: a readable error.
  await user.fill('nosuchuser');
  await page.getByTestId('ws-invite-send').click();
  await expect(tools.locator('.mud-alert').first()).toBeVisible();

  // bob as viewer
  await user.fill('bob');
  await page.getByTestId('ws-invite-role').click();
  await page.getByRole('option', { name: 'Viewer' }).click();
  await page.getByTestId('ws-invite-send').click();
  await expect(page.getByTestId('ws-shares')).toContainText('bob');
  // carol as collaborator (a separate OS user with its own home inside the container)
  await user.fill('carol');
  await page.getByTestId('ws-invite-role').click();
  await page.getByRole('option', { name: 'Collaborator' }).click();
  await page.getByTestId('ws-invite-send').click();
  await expect(page.getByTestId('ws-shares')).toContainText('carol');
  await expect(tools.locator('.mud-alert')).toHaveCount(0); // the collaborator's home was prepared without an error
  const home = spawnSync('docker', ['exec', '-u', 'bbc1', containerFor(ws.id), 'sh', '-c', 'cat /home/bbc1/.gitconfig 2>&1; ls -ld /home/bbc1'], { encoding: 'utf8' });
  expect((home.stdout ?? '') + (home.stderr ?? ''), 'the collaborator slot has its own git config').toMatch(/name = /);

  // ...and its IDE server really started (the starter script once mistook "nothing answers" for "healthy").
  const ide = spawnSync('docker', ['exec', '-u', 'bbc1', containerFor(ws.id), 'sh', '-c', 'ls /home/bbc1/.bb-ide.log && curl -s -o /dev/null -w "%{http_code}" http://127.0.0.1:13401/version'], { encoding: 'utf8' });
  expect((ide.stdout ?? '') + (ide.stderr ?? ''), 'the collaborator IDE answers on its port').toMatch(/\.bb-ide\.log[\s\S]*[1-4]\d\d$/);

  // History and Network tabs
  await tools.getByRole('tab', { name: 'History' }).click();
  await expect(tools).toContainText(/workspace\.(created|started|shared)/);
  await tools.getByRole('tab', { name: /Network/ }).click();
  await expect(tools.getByTestId('ws-egress')).toBeVisible();

  // bob (viewer): "Shared with you" lists it, the shared page is read-only and shows the files.
  const bc = await browser.newContext({ baseURL: process.env.E2E_BASE_URL, viewport: { width: 390, height: 800 } });
  const bob = await bc.newPage();
  await theme(bob, 'dark');
  await loginAs(bob, 'bob');
  await openReady(bob, '/workspaces');
  await expect(bob.getByTestId('ws-shared-table')).toContainText('web-app');
  await bob.getByTestId('ws-shared-table').getByRole('link').first().click();
  await expect(bob.getByTestId('shared-role')).toHaveText('Viewer');
  await expect(bob.getByTestId('viewer-panel')).toBeVisible();
  await expect(bob.getByTestId('viewer-entry').first()).toBeVisible({ timeout: 20_000 });
  expect(await bob.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth), 'shared page fits a phone').toBeLessThanOrEqual(0);
  // A viewer cannot open the owner's page, and cannot reach the owner's actions through the API.
  await openReady(bob, `/workspaces/${ws.id}`);
  await expect(bob.getByRole('heading', { name: 'Not found' })).toBeVisible();
  // dan was not invited: not shared with him.
  const dc = await browser.newContext({ baseURL: process.env.E2E_BASE_URL });
  const dan = await dc.newPage();
  await loginAs(dan, 'dan');
  await openReady(dan, `/workspaces/shared/${ws.id}`);
  await expect(dan.getByRole('heading', { name: 'Not found' })).toBeVisible();
  await dc.close();

  // The viewer leaves; the owner ends sharing for the collaborator.
  await openReady(bob, `/workspaces/shared/${ws.id}`);
  await bob.getByTestId('shared-leave').click();
  await expect(bob).toHaveURL(/\/workspaces$/, { timeout: 30_000 });
  await bc.close();
  await tools.getByRole('tab', { name: 'Sharing' }).click();
  await page.getByTestId('ws-end-sharing').first().click();
  await expect(page.getByTestId('ws-shares')).toHaveCount(0);
});

test('start workspace on an issue and a pull request: Resume while one is live, Start after it stopped', async () => {
  // The workspace of this file is on `main`; an issue's workspace is a different branch, so the button offers Start, and
  // a pull request whose source branch is checked out shows Resume.
  await openReady(page, '/p/WEB/issues/2');
  await expect(page.getByTestId('start-workspace')).toContainText(/Start workspace|Resume workspace/);
  await openReady(page, '/p/WEB/repos/web-app/pull-requests/1');
  await expect(page.getByTestId('start-workspace')).toBeVisible();

  // Stop through the confirm dialog; the workspace page then explains and offers to resume.
  await openReady(page, `/workspaces/${ws.id}`);
  await page.getByTestId('ws-stop').click();
  await page.getByRole('dialog').getByRole('button', { name: 'Stop', exact: true }).click();
  await expect(page.getByTestId('ws-state')).toHaveText('Stopped', { timeout: 60_000 });
  await expect(page.getByText(/This workspace is stopped/)).toBeVisible();
  await openReady(page, '/workspaces');
  await expect(page.getByTestId('ws-table')).toContainText('Stopped');
  await expect(page.getByTestId('ws-usage')).toBeVisible();
});

test('the workspace audit page lists the lifecycle, filters it and exports CSV; non-admins are refused', async ({ browser }) => {
  await openReady(page, '/admin/workspaces/audit');
  const audit = page.getByTestId('ws-audit');
  // The table shows the newest 25 events: after a long serial run `workspace.created` is on a later page, so look for any lifecycle event here.
  await expect(audit).toContainText(/workspace\.(created|started|stopped|ticket_redeemed|share_ended)/);
  await audit.getByRole('textbox', { name: 'Actor' }).fill('nobody-by-this-name');
  await page.getByTestId('ws-audit-filter').click();
  await expect(audit).toContainText('No events match');
  await audit.getByRole('button', { name: 'Reset' }).click();
  await expect(audit).toContainText(/workspace\.(created|started|stopped|ticket_redeemed|share_ended)/);
  const href = await page.getByTestId('ws-audit-export').getAttribute('href');
  const csv = await page.request.get(href!);
  expect(csv.status()).toBe(200);
  expect(csv.headers()['content-type']).toMatch(/csv/);
  expect(await csv.text()).toContain('workspace.created');

  const bc = await browser.newContext({ baseURL: process.env.E2E_BASE_URL });
  const bob = await bc.newPage();
  await loginAs(bob, 'bob');
  const denied = await bob.request.get(href!);
  expect([401, 403, 404, 302]).toContain(denied.status());
  expect(await denied.text()).not.toContain('workspace.created');
  await bc.close();
});
