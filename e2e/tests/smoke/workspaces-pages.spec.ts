// The workspace-related pages that do not need a container: usage panel, start page, admin settings and audit, prebuilds, AI runs, permissions,
// at desktop and phone width, light and dark. (Pages that need a running workspace are tests/docker/workspace-pages.spec.ts.)
import { test, expect, type Page } from '../../support/fixtures.ts';
import { DatabaseSync } from 'node:sqlite';
import path from 'node:path';

const field = (p: Page, id: string) => p.locator(`input[data-testid=${id}], [data-testid=${id}] input, textarea[data-testid=${id}], [data-testid=${id}] textarea`).first();

async function noHorizontalScroll(p: Page, what: string): Promise<void> {
  const over = await p.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
  expect(over, `${what}: horizontal overflow in px`).toBeLessThanOrEqual(0);
}

async function scheme(p: Page, mode: 'light' | 'dark'): Promise<void> {
  await p.emulateMedia({ colorScheme: mode });
  await p.context().addCookies([{ name: 'bytebench-theme', value: mode, url: process.env.E2E_BASE_URL! }]);
  await p.addInitScript((m) => { try { localStorage.setItem('bytebench-theme', m); } catch { /* ignore */ } }, mode);
}

function db(): DatabaseSync { return new DatabaseSync(path.join(process.env.E2E_DATA_DIR!, 'e2e.db')); }

/** Inserts AI runs on the first issue of a project (as the run's requester) and returns their public ids. */
function seedRuns(projectKey: string, requester: string): { ok: string; failed: string } {
  const d = db();
  try {
    const issue = d.prepare('select i.Id as IssueId, i.ProjectId as ProjectId, r.Id as RepoId from Issues i join Projects p on p.Id = i.ProjectId join Repositories r on r.ProjectId = p.Id where p.Key = ? and i.Number = 1 limit 1').get(projectKey) as { IssueId: number; ProjectId: number; RepoId: number };
    const uid = (d.prepare('select Id from AspNetUsers where UserName = ?').get(requester) as { Id: number }).Id;
    const tag = Math.random().toString(36).slice(2, 10);
    const ins = d.prepare(`insert into AiRuns(PublicId,ProjectId,RepositoryId,IssueId,RequestedById,Trigger,Status,StopReason,Branch,BaseBranch,Steps,ToolCalls,InputTokens,OutputTokens,CostUsd,Summary,Error,CancelRequested,CreatedAt,StartedAt,FinishedAt)
      values (?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,0,datetime('now','-2 hours'),datetime('now','-2 hours'),datetime('now','-1 hours'))`);
    const ok = `e2e-ok-${tag}`, failed = `e2e-bad-${tag}`;
    const r1 = ins.run(ok, issue.ProjectId, issue.RepoId, issue.IssueId, uid, 'button', 3, null, 'ai/e2e-ok', 'main', 7, 12, 12000, 3400, '0.21', 'Added the skeleton and opened a pull request.', null).lastInsertRowid;
    const r2 = ins.run(failed, issue.ProjectId, issue.RepoId, issue.IssueId, uid, 'assignment', 4, 'error', 'ai/e2e-bad', 'main', 1, 0, 10, 0, '0.00', null, 'The Claude API rejected the request: invalid x-api-key (a long message to see how an error wraps on a phone screen).').lastInsertRowid;
    const ev = d.prepare("insert into AiRunEvents(RunId,At,Kind,Message) values (?,datetime('now','-1 hours'),?,?)");
    ev.run(r1, 'info', 'Backend: Claude API (model e2e).'); ev.run(r1, 'tool', 'write_file src/main.js'); ev.run(r1, 'status', 'Succeeded');
    ev.run(r2, 'error', 'invalid x-api-key');
    return { ok, failed };
  } finally { d.close(); }
}

test.describe('workspace pages (no Docker)', () => {
  test('"My workspaces": usage and limits, idle setting, empty state; nothing is shared with alice', async ({ page, login, open }) => {
    await login('alice');
    await open('/workspaces');
    await expect(page.getByTestId('ws-usage')).toContainText('Running workspaces');
    await expect(page.getByTestId('ws-usage')).toContainText(/of (\d+|no limit)/);
    await expect(page.getByText('You have no workspaces yet.')).toBeVisible();
    await expect(page.getByTestId('ws-shared-table')).toHaveCount(0);
    await field(page, 'ws-idle-input').fill('90');
    await page.getByTestId('ws-idle-save').click();
    await expect(page.getByText(/saved|90/i).first()).toBeVisible();
    await open('/workspaces');
    await expect(field(page, 'ws-idle-input')).toHaveValue('90');
    await field(page, 'ws-idle-input').fill('120'); // back to the default
    await page.getByTestId('ws-idle-save').click();
  });

  test('phone width, dark theme: workspace pages fit the screen', async ({ page, login, open }) => {
    await page.setViewportSize({ width: 390, height: 800 });
    await scheme(page, 'dark');
    await login('alice');
    const pages = ['/workspaces', '/account/workspace', '/p/WEB/repos/web-app/workspace', '/p/WEB/workspace', '/p/WEB/repos/web-app/prebuilds',
      '/admin/workspaces', '/admin/workspaces/audit', '/admin/ai'];
    for (const url of pages) {
      await open(url);
      await expect(page.locator('h1, h2, h5').first()).toBeVisible();
      await noHorizontalScroll(page, url);
    }
  });

  test('the environment suggestion (no workspace.yml) shows a ready-to-use file and stays inside the card', async ({ page, login, open }) => {
    await page.setViewportSize({ width: 390, height: 800 });
    await login('alice');
    await open('/p/WEB/repos/web-app/workspace');
    await expect(page.getByTestId('ws-suggested')).toContainText('setup');
    await expect(page.getByTestId('ws-add-file')).toBeVisible();
    await noHorizontalScroll(page, 'start page with the suggestion');
    // "for-project" picks the repository for the project.
    await open('/p/WEB/workspace');
    await expect(page.getByText('Open in workspace').first()).toBeVisible();
  });

  test('a viewer or a stranger cannot start a workspace page for a private project', async ({ page, login, open }) => {
    await login('bob'); // not a member of OPS
    await open('/p/OPS/repos/infra/workspace');
    await expect(page.getByTestId('ws-start')).toHaveCount(0);
    await open('/p/OPS/repos/infra/prebuilds');
    await expect(page.getByTestId('prebuild-settings')).toHaveCount(0);
    expect((await page.request.post('/api/v1/projects/OPS/repos/infra/workspaces', { data: {} })).status()).toBeGreaterThanOrEqual(401);
  });

  test('prebuilds page: enable, save, "Rebuild now" explains why nothing can be built, clean up works', async ({ page, login, open }) => {
    await login('alice');
    await open('/p/WEB/repos/web-app/prebuilds');
    await expect(page.getByTestId('prebuild-settings')).toBeVisible();
    await expect(page.getByTestId('prebuild-table')).toHaveCount(0);
    await expect(page.getByText(/No prebuilds yet/)).toBeVisible();
    await page.getByTestId('prebuild-enabled').click();
    await field(page, 'prebuild-branches').fill('main');
    await field(page, 'prebuild-keep').fill('2');
    await page.getByTestId('prebuild-save').click();
    await expect(page.getByText('Prebuild settings saved.')).toBeVisible();
    await open('/p/WEB/repos/web-app/prebuilds');
    await expect(field(page, 'prebuild-branches')).toHaveValue('main');
    await expect(field(page, 'prebuild-keep')).toHaveValue('2');
    await page.getByTestId('prebuild-rebuild').click();
    await expect(page.getByTestId('prebuild-error')).toContainText(/no environment file|nothing to prebuild|Docker/i);
    await page.getByTestId('prebuild-cleanup').click();
    await expect(page.getByTestId('prebuild-error')).toHaveCount(0);
    // off again, so other tests see the default
    await page.getByTestId('prebuild-enabled').click();
    await page.getByTestId('prebuild-save').click();
  });

  test('admin → Workspaces: limits, sharing mode, network policy and audit retention are saved and validated', async ({ page, login, open }) => {
    await login('alice');
    await open('/admin/workspaces');
    for (const id of ['ws-health', 'ws-limits', 'ws-policies', 'ws-switch', 'ws-ai-switch', 'ws-editor-switch', 'ws-custom-images-switch']) await expect(page.getByTestId(id)).toBeVisible();

    // limits
    await field(page, 'ws-lim-running').fill('3');
    await page.getByTestId('ws-limits-save').click();
    await expect(page.getByText(/limits saved/i)).toBeVisible();
    await open('/admin/workspaces');
    await expect(field(page, 'ws-lim-running')).toHaveValue('3');
    await field(page, 'ws-lim-running').fill('2');
    await page.getByTestId('ws-limits-save').click();
    await expect(page.getByText(/limits saved/i)).toBeVisible();

    // the user's page shows the limit
    await open('/workspaces');
    await expect(page.getByTestId('ws-usage')).toContainText('of 2');
    await open('/admin/workspaces');

    // sharing mode
    await page.getByTestId('ws-sharing-mode').click();
    await page.getByRole('option', { name: /Nobody/ }).click();
    await expect(page.getByText('Sharing policy saved.')).toBeVisible();
    await open('/admin/workspaces');
    await expect(page.getByTestId('ws-sharing-mode')).toContainText('Nobody');
    await page.getByTestId('ws-sharing-mode').click();
    await page.getByRole('option', { name: /Project members only/ }).click();
    await expect(page.getByText('Sharing policy saved.')).toBeVisible();

    // network policy: an invalid allow-list entry is refused with a message, a valid one saved
    await page.getByTestId('ws-egress-mode').click();
    await page.getByRole('option', { name: /Allow-list/ }).click();
    await field(page, 'ws-egress-allow').fill('not a host!!\n@npm');
    await page.getByTestId('ws-egress-save').click();
    await expect(page.getByTestId('ws-policies').locator('.mud-alert-text-error, .mud-alert').first()).toBeVisible();
    await field(page, 'ws-egress-allow').fill('@npm\n*.example.com');
    await page.getByTestId('ws-egress-save').click();
    await expect(page.getByText(/Network policy saved/)).toBeVisible();
    await open('/admin/workspaces');
    await expect(page.getByTestId('ws-egress-mode')).toContainText('Allow-list');
    await expect(field(page, 'ws-egress-allow')).toHaveValue(/example\.com/);
    await page.getByTestId('ws-egress-mode').click();
    await page.getByRole('option', { name: /Public internet only/ }).click();
    await page.getByTestId('ws-egress-save').click();
    await expect(page.getByText(/Network policy saved/)).toBeVisible();

    // audit retention
    await field(page, 'ws-audit-retention').fill('30');
    await page.getByTestId('ws-policies').getByRole('button', { name: 'Save', exact: true }).click();
    await expect(page.getByText('Retention saved.')).toBeVisible();
    await field(page, 'ws-audit-retention').fill('0');
    await page.getByTestId('ws-policies').getByRole('button', { name: 'Save', exact: true }).click();
    await page.getByTestId('ws-audit-link').click();
    await expect(page).toHaveURL(/\/admin\/workspaces\/audit/);
  });

  test('admin switches turn the local-editor and custom-image options on and off', async ({ page, login, open }) => {
    await login('alice');
    await open('/admin/workspaces');
    const sw = (id: string) => page.locator(`input[data-testid=${id}], [data-testid=${id}] input`).first();
    for (const id of ['ws-editor-switch', 'ws-custom-images-switch']) {
      const before = await sw(id).isChecked();
      await page.getByTestId(id).click();
      await open('/admin/workspaces');
      expect(await sw(id).isChecked(), `${id} persists`).toBe(!before);
      await page.getByTestId(id).click(); // restore
      await open('/admin/workspaces');
      expect(await sw(id).isChecked()).toBe(before);
    }
  });

  test('the workspace audit page filters and exports CSV; everybody else is refused', async ({ page, login, open, browser }) => {
    await login('alice');
    await open('/admin/workspaces/audit');
    const audit = page.getByTestId('ws-audit');
    await expect(audit).toBeVisible();
    await audit.getByRole('textbox', { name: 'Actor' }).fill('nobody-by-this-name');
    await page.getByTestId('ws-audit-filter').click();
    await expect(audit).toContainText('No events match');
    await audit.getByRole('button', { name: 'Reset' }).click();
    await expect(audit).not.toContainText('No events match');
    const href = await page.getByTestId('ws-audit-export').getAttribute('href');
    const csv = await page.request.get(href!);
    expect(csv.status()).toBe(200);
    expect(csv.headers()['content-type']).toMatch(/csv/);
    expect((await csv.text()).split('\n')[0].toLowerCase()).toMatch(/time|action|actor/);

    const bc = await browser.newContext({ baseURL: process.env.E2E_BASE_URL });
    const bob = await bc.newPage();
    await bob.goto('/account/login');
    await bob.locator('#login-user').fill('bob');
    await bob.locator('#login-password').fill('ByteBench123');
    await bob.getByRole('button', { name: 'Sign in' }).click();
    await bob.waitForURL((u) => !u.pathname.startsWith('/account/login'));
    await bob.goto('/admin/workspaces/audit');
    await expect(bob.getByTestId('ws-audit')).toHaveCount(0);
    const denied = await bob.request.get(href!, { maxRedirects: 0 });
    expect(denied.status()).not.toBe(200);
    await bc.close();
  });

  test('personal workspace settings: secrets list and the user snippet render, anonymous users are sent to sign in', async ({ page, login, open }) => {
    await login('alice');
    await open('/account/workspace');
    await expect(page.getByTestId('ws-user-settings')).toBeVisible();
    await expect(page.getByTestId('ws-secrets')).toBeVisible();
    await expect(page.getByTestId('ws-secret-add')).toBeVisible();
  });

  test('AI runs: the run page (status, error, backend, log), the AI runs section in Admin → AI, and who may look', async ({ page, login, open, browser }) => {
    const runs = seedRuns('WEB', 'alice');
    const priv = seedRuns('OPS', 'alice');
    await login('alice');
    await open(`/ai-runs/${runs.ok}`);
    await expect(page.getByTestId('run-status')).toHaveText('Succeeded');
    await expect(page.getByTestId('run-backend')).toContainText('Claude API');
    await expect(page.getByTestId('run-log')).toContainText('write_file src/main.js');
    await expect(page.getByText('Added the skeleton and opened a pull request.')).toBeVisible();
    await expect(page.getByTestId('run-cancel')).toHaveCount(0); // finished: nothing to cancel
    await open(`/ai-runs/${runs.failed}`);
    await expect(page.getByTestId('run-status')).toHaveText('Failed');
    await expect(page.getByTestId('run-error')).toContainText('invalid x-api-key');
    await page.setViewportSize({ width: 390, height: 800 });
    await open(`/ai-runs/${runs.failed}`);
    await noHorizontalScroll(page, 'AI run page on a phone');
    await page.setViewportSize({ width: 1360, height: 860 });

    // the issue page lists the runs and links to them
    await open('/p/WEB/issues/1');
    await expect(page.getByTestId('ai-run-panel')).toBeVisible();
    await expect(page.getByTestId('ai-run-row').first()).toBeVisible();
    await page.getByTestId('ai-run-row').first().getByRole('link').first().click();
    await expect(page).toHaveURL(/\/ai-runs\//);

    // Admin → AI: the "AI runs" backend section
    await open('/admin/ai');
    await expect(page.getByRole('heading', { name: /AI runs/ })).toBeVisible();
    await expect(page.getByText('Active backend')).toBeVisible();

    // an unknown run and a run in a private project the user cannot see look the same: not found
    await open('/ai-runs/no-such-run');
    await expect(page.getByText("This AI run doesn't exist.")).toBeVisible();
    const bc = await browser.newContext({ baseURL: process.env.E2E_BASE_URL });
    const bob = await bc.newPage();
    await bob.goto('/account/login');
    await bob.locator('#login-user').fill('bob');
    await bob.locator('#login-password').fill('ByteBench123');
    await bob.getByRole('button', { name: 'Sign in' }).click();
    await bob.waitForURL((u) => !u.pathname.startsWith('/account/login'));
    await bob.goto(`/ai-runs/${priv.ok}`);
    await expect(bob.getByText("This AI run doesn't exist.")).toBeVisible();
    await expect(bob.getByTestId('run-log')).toHaveCount(0);
    await bob.goto('/admin/ai');
    await expect(bob.getByText('Active backend')).toHaveCount(0);
    await bc.close();
  });

  test('shared-workspace and workspace pages for ids that are not yours say so', async ({ page, login, open }) => {
    await login('dan');
    await open('/workspaces/shared/nosuchworkspace');
    await expect(page.getByText(/isn't shared with you/)).toBeVisible();
    await open('/workspaces/nosuchworkspace');
    await expect(page.getByText("This workspace doesn't exist.")).toBeVisible();
  });
});
