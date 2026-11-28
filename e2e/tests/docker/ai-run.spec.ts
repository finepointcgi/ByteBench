// OPT-IN: a REAL AI run through the local Claude Code CLI against a real Docker workspace (spends a few cents of the logged-in subscription).
//
//   E2E_REAL_AI=1 e2e/run.sh docker -g "real AI run"
//
// Skipped unless E2E_REAL_AI=1 *and* the `claude` CLI is installed and answers --version. The server is started with the CLI backend and small limits
// (global-setup.ts). The test creates a repository with a broken function and a `test` task, adds the AI user as a Developer, opens an issue, starts a
// run, and checks the whole path: tools executed in the container as bbai, run_task, a pull request with the fix (checked with real git),
// an issue comment, the workspace deleted, no credential in anything people can read, and that the host's Claude login is not inside the container.
// A second run is cancelled mid-flight and must leave no `claude` process behind.
import { test, expect, loginAs, createToken, apiFor, Git, openReady } from '../../support/fixtures.ts';
import { spawnSync } from 'node:child_process';
import { cpSync, existsSync, readdirSync, readFileSync } from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import type { APIRequestContext, BrowserContext, Page } from '@playwright/test';
import { createRepoViaUi, PROJECT } from '../../support/workspace.ts';
import { instanceKey } from '../../support/docker.ts';
import { E2E_ROOT } from '../../support/server.ts';

const claudeVersion = (() => { const r = spawnSync('claude', ['--version'], { encoding: 'utf8' }); return r.status === 0 ? r.stdout.trim() : ''; })();
test.skip(process.env.E2E_REAL_AI !== '1' || !claudeVersion, 'opt-in: set E2E_REAL_AI=1 and have the `claude` CLI installed and logged in');
test.describe.configure({ mode: 'serial' });

const SLUG = 'ai-app';
let ctx: BrowserContext;
let page: Page;
let api: APIRequestContext;
let token: string;
const git = new Git();

interface Run { id: string; status: string; stopReason: string | null; branch: string | null; pullRequest: number | null; steps: number; toolCalls: number; costUsd: number; summary: string | null; error: string | null; inputTokens: number; outputTokens: number }

async function newIssue(title: string, description: string): Promise<number> {
  const r = await api.post(`/api/v1/projects/${PROJECT}/issues`, { data: { title, description } });
  expect(r.ok(), await r.text()).toBeTruthy();
  return (await r.json()).number;
}
async function startRun(n: number): Promise<Run> {
  const r = await api.post(`/api/v1/projects/${PROJECT}/issues/${n}/ai-runs`, { data: { repository: SLUG } });
  expect(r.status(), await r.text()).toBe(202);
  return r.json();
}
const getRun = async (id: string): Promise<Run> => (await api.get(`/api/v1/ai-runs/${id}`)).json();
const events = async (id: string): Promise<{ kind: string; message: string }[]> => (await api.get(`/api/v1/ai-runs/${id}/events`)).json();
const done = (s: string) => ['succeeded', 'failed', 'cancelled'].includes(s);
const workspaceContainers = () => spawnSync('docker', ['ps', '-a', '--format', '{{.Names}}', '--filter', `name=^bb-ws-${instanceKey()}-`], { encoding: 'utf8' }).stdout.trim();

function claudeProcesses(): string[] {
  const r = spawnSync('ps', ['-axo', 'pid,command'], { encoding: 'utf8' });
  return r.stdout.split('\n').filter((l) => l.includes('bytebench-ai-runs') && /claude/.test(l) && !l.includes('ps -axo'));
}

test.beforeAll(async ({ browser }) => {
  ctx = await browser.newContext({ baseURL: process.env.E2E_BASE_URL });
  page = await ctx.newPage();
  await loginAs(page, 'alice');
  token = await createToken(page, 'e2e-real-ai');
  api = await apiFor(process.env.E2E_BASE_URL!, token);
  await createRepoViaUi(page, SLUG);
  const url = git.url(process.env.E2E_BASE_URL!, PROJECT, SLUG, 'alice', token);
  git.run(null, 'clone', url, 'work');
  const work = path.join(git.home, 'work');
  cpSync(path.join(E2E_ROOT, 'fixtures', 'ai-repo'), work, { recursive: true });
  git.run(work, 'add', '-A'); git.run(work, 'commit', '-m', 'A tiny app with a bug'); git.run(work, 'push', 'origin', 'main');
  // The AI user must be a Developer of the project.
  const m = await api.post(`/api/v1/projects/${PROJECT}/members`, { data: { user: 'bytebench-ai', role: 'Developer' } });
  expect(m.ok(), await m.text()).toBeTruthy();
});

test.afterAll(async () => { git.dispose(); await ctx?.close(); });

test('real AI run: the Claude CLI fixes a failing test through the tool bridge and opens a pull request', async () => {
  test.setTimeout(14 * 60_000);
  const number = await newIssue('Make the failing test pass', 'The test task fails: add(2, 3) must return 5. Fix the broken function in mathlib.js and make sure the `test` task passes.');
  const started = await startRun(number);
  console.log(`[real-ai] claude ${claudeVersion}; run ${started.id} for issue ${PROJECT}-${number}`);

  // While it runs: look at the container (what the host login would expose), and watch the process list.
  let container = '';
  let containerProbe = '';
  let sawProcess = false;
  let run = started;
  const t0 = Date.now();
  while (!done(run.status)) {
    if (Date.now() - t0 > 13 * 60_000) throw new Error('the run did not finish in 13 minutes: ' + JSON.stringify(run));
    await new Promise((r) => setTimeout(r, 2500));
    run = await getRun(started.id);
    if (!sawProcess && claudeProcesses().length > 0) sawProcess = true;
    if (!container) container = workspaceContainers().split('\n')[0] ?? '';
    if (container && !containerProbe) {
      const r = spawnSync('docker', ['exec', '-u', 'bbai', container, 'bash', '-c',
        'env; echo ---; ls -la ~ /root /home/*/ 2>&1; echo ---; find / -xdev \\( -name ".claude*" -o -name ".credentials*" \\) 2>/dev/null; echo ---'], { encoding: 'utf8' });
      containerProbe = (r.stdout ?? '') + (r.stderr ?? '');
    }
  }
  const evs = await events(started.id);
  console.log(`[real-ai] finished: ${run.status} (${run.stopReason ?? 'no stop reason'}); steps ${run.steps}, tool calls ${run.toolCalls}, tokens in/out ${run.inputTokens}/${run.outputTokens}, cost $${run.costUsd}`);
  console.log(evs.map((e) => `[${e.kind}] ${e.message}`).join('\n'));

  expect(run.status, JSON.stringify(run) + '\n' + evs.map((e) => e.message).join('\n')).toBe('succeeded');
  expect(sawProcess, 'the claude process ran with the run directory').toBe(true);
  expect(run.steps).toBeGreaterThan(0);
  expect(run.toolCalls).toBeGreaterThan(0);
  expect(run.costUsd).toBeGreaterThan(0);
  expect(run.costUsd).toBeLessThan(1.5);
  expect(evs.some((e) => e.kind === 'tool' && /run_task|test/i.test(e.message)), 'the model used run_task (or ran the test)').toBe(true);
  expect(evs.some((e) => e.kind === 'tool' && /edit|write/i.test(e.message)), 'the model edited a file').toBe(true);

  // The host's Claude login is not in the container.
  expect(container, 'a workspace container was seen while the run was working').not.toBe('');
  expect(containerProbe).not.toMatch(/ANTHROPIC|CLAUDE|OAUTH/i);
  expect(containerProbe).not.toMatch(/\.claude|\.credentials/);

  // The pull request has the fix; check it with real git.
  expect(run.pullRequest, 'a pull request was opened').not.toBeNull();
  const pr = await (await api.get(`/api/v1/projects/${PROJECT}/repos/${SLUG}/pull-requests/${run.pullRequest}`)).json();
  expect(pr.sourceBranch).toMatch(/^ai\//);
  const comments = await (await api.get(`/api/v1/projects/${PROJECT}/issues/${number}/comments`)).json();
  expect(JSON.stringify(comments), 'the issue got a comment from the AI run').toMatch(/pull request|#/i);

  const work = path.join(git.home, 'work');
  git.run(work, 'fetch', 'origin', pr.sourceBranch);
  git.run(work, 'checkout', '-q', '-B', 'check', `origin/${pr.sourceBranch}`);
  expect(readFileSync(path.join(work, 'mathlib.js'), 'utf8')).toMatch(/a \+ b/);
  const t = spawnSync('node', ['test.js'], { cwd: work, encoding: 'utf8' });
  expect(t.status, t.stdout + t.stderr).toBe(0);
  const changed = git.run(work, 'diff', '--name-only', 'origin/main', `origin/${pr.sourceBranch}`).trim().split('\n');
  expect(changed, 'a focused change').toEqual(['mathlib.js']);

  // No credential anywhere people (or logs) can read it.
  const leak = /gbw_[A-Za-z0-9_-]{8,}|gbp_[A-Za-z0-9_-]{8,}|gbc_[A-Za-z0-9_-]{8,}|sk-ant-|Bearer [A-Za-z0-9]{20,}/;
  const log = readFileSync(process.env.E2E_SERVER_LOG!, 'utf8');
  const noOwnToken = (s: string) => s.split(token).join('<e2e-token>');
  for (const [what, text] of [['events', JSON.stringify(evs)], ['pull request', JSON.stringify(pr)], ['issue comments', JSON.stringify(comments)], ['run', JSON.stringify(run)], ['server log', log]] as const)
    expect(noOwnToken(text), `no credential in the ${what}`).not.toMatch(leak);

  // The workspace is gone and so is the process.
  await expect.poll(workspaceContainers, { timeout: 60_000 }).toBe('');
  expect(claudeProcesses(), 'no claude process left').toEqual([]);
});

test('real AI run: Cancel kills the CLI process tree and discards the work', async () => {
  test.setTimeout(8 * 60_000);
  const number = await newIssue('Add a multiply function', 'Add multiply(a, b) to mathlib.js with tests in test.js, then run the test task. Take your time and check your work thoroughly.');
  const started = await startRun(number);
  const t0 = Date.now();
  // Wait until the model is really working (a tool call was logged), then cancel.
  for (;;) {
    const run = await getRun(started.id);
    const evs = await events(started.id);
    if (evs.some((e) => e.kind === 'tool') || done(run.status)) break;
    if (Date.now() - t0 > 5 * 60_000) throw new Error('no tool call within 5 minutes');
    await new Promise((r) => setTimeout(r, 1500));
  }
  expect(claudeProcesses().length, 'the CLI is running').toBeGreaterThan(0);
  const c = await api.post(`/api/v1/ai-runs/${started.id}/cancel`);
  expect(c.ok(), await c.text()).toBeTruthy();
  await expect.poll(async () => (await getRun(started.id)).status, { timeout: 90_000 }).toBe('cancelled');
  await expect.poll(claudeProcesses, { timeout: 20_000, message: 'the claude process tree is gone' }).toEqual([]);
  expect((await getRun(started.id)).pullRequest).toBeNull();
  await expect.poll(workspaceContainers, { timeout: 60_000 }).toBe('');
  const branches = await (await api.get(`/api/v1/projects/${PROJECT}/repos/${SLUG}/branches`)).json();
  expect(JSON.stringify(branches)).not.toContain(`ai/${PROJECT.toLowerCase()}-${number}-`);
});

test('real editor assistant: the Claude CLI proposes edits in the web editor; accept one, reject another', async () => {
  test.setTimeout(8 * 60_000);
  const p = await ctx.newPage();
  try {
    await openReady(p, `/p/${PROJECT}/repos/${SLUG}/edit/main`);
    const input = p.getByLabel('Message to the assistant'); // the panel is open by default
    if (!await input.isVisible()) await p.getByRole('button', { name: 'Assistant', exact: true }).click();
    await expect(input).toBeVisible({ timeout: 30_000 });
    await expect(p.locator('.we-chat-backend')).toContainText('Claude Code');

    const ask = async (text: string) => {
      await input.fill(text);
      await p.getByRole('button', { name: 'Send' }).click();
      await expect(p.locator('.we-proposal').first()).toBeVisible({ timeout: 4 * 60_000 });
      await expect(p.getByRole('button', { name: 'Stop' })).toBeHidden({ timeout: 60_000 });
    };

    await ask('In mathlib.js the add function subtracts. Change it to return a + b. Change nothing else and do not touch other files.');
    const first = p.locator('.we-proposal').first();
    await expect(first).toContainText('mathlib.js');
    await first.getByRole('button', { name: 'Diff' }).click();
    await expect(p.getByText('a + b').first()).toBeVisible({ timeout: 15_000 });
    await first.getByRole('button', { name: 'Accept' }).click();
    await expect(first).toContainText('Accepted');

    await ask('Create a new file notes.md containing just the heading "# Notes". Do not touch other files.');
    const second = p.locator('.we-proposal').filter({ hasText: 'notes.md' });
    await expect(second).toBeVisible();
    await second.getByRole('button', { name: 'Reject' }).click();
    await expect(second).toContainText('Rejected');

    // Only the accepted edit is a working change; nothing was committed.
    await p.getByRole('button', { name: 'Source control' }).click();
    await expect(p.locator('.we-changes .we-change')).toHaveCount(1);
    await expect(p.locator('.we-changes')).toContainText('mathlib.js');
    await expect(p.locator('.we-changes')).not.toContainText('notes.md');
  } finally { await p.close(); }
  // The request's private directory is gone, and no CLI process is left.
  expect(spawnSync('ps', ['-axo', 'command'], { encoding: 'utf8' }).stdout.split('\n').filter((l) => l.includes('bytebench-claude-code') && l.includes('claude'))).toEqual([]);
  expect(existsSync(path.join(os.tmpdir(), 'bytebench-claude-code')) ? readdirSync(path.join(os.tmpdir(), 'bytebench-claude-code')) : []).toEqual([]);
});
