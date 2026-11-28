// Helpers for the docker tier: fixture repositories, starting/waiting for workspaces, running commands inside them.
import { expect, type APIRequestContext, type Page } from '@playwright/test';
import { cpSync, existsSync, mkdirSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import { E2E_ROOT } from './server.ts';
import { Git, createToken, apiFor, openReady } from './fixtures.ts';
import { spawnSync } from 'node:child_process';
import { containerFor } from './docker.ts';

export const PROJECT = 'WEB'; // seeded project; alice owns it, bob is a developer in it
export const FIXTURE_SLUG = 'ws-fixture';
export const LIB_SLUG = 'ws-lib';

export interface WorkspaceDto { id: string; project: string; repository: string; ref: string; state: string; error?: string | null; stopReason?: string | null; url: string }

export interface Fixtures { token: string; api: APIRequestContext; git: Git; fixtureUrl: string; libUrl: string; workDir: string; }

export async function createRepoViaUi(page: Page, slug: string): Promise<void> {
  await openReady(page, `/p/${PROJECT}/repos`);
  await page.getByRole('button', { name: 'Create repository' }).first().click();
  await page.getByLabel('Name', { exact: true }).fill(slug);
  await page.getByRole('button', { name: 'Create repository' }).last().click();
  await expect(page).toHaveURL(new RegExp(`/p/${PROJECT}/repos/${slug}`));
}

/** Creates the fixture repository (workspace.yml, a submodule) and its library repository on the server, as alice. */
export async function createFixtures(page: Page, baseURL: string): Promise<Fixtures> {
  const token = await createToken(page, 'e2e-docker');
  const api = await apiFor(baseURL, token);
  const git = new Git();
  await createRepoViaUi(page, LIB_SLUG);
  await createRepoViaUi(page, FIXTURE_SLUG);
  const libUrl = git.url(baseURL, PROJECT, LIB_SLUG, 'alice', token);
  const fixtureUrl = git.url(baseURL, PROJECT, FIXTURE_SLUG, 'alice', token);

  // The library
  git.run(null, 'clone', libUrl, 'lib');
  const lib = path.join(git.home, 'lib');
  cpSync(path.join(E2E_ROOT, 'fixtures', 'library-repo'), lib, { recursive: true });
  git.run(lib, 'add', '-A'); git.run(lib, 'commit', '-m', 'Library content'); git.run(lib, 'push', 'origin', 'main');
  const libSha = git.run(lib, 'rev-parse', 'HEAD').trim();

  // The fixture, with the library as a submodule (a relative URL resolves to the same server)
  git.run(null, 'clone', fixtureUrl, 'fixture');
  const work = path.join(git.home, 'fixture');
  cpSync(path.join(E2E_ROOT, 'fixtures', 'workspace-repo'), work, { recursive: true });
  writeFileSync(path.join(work, '.gitmodules'), `[submodule "libs/sub"]\n\tpath = libs/sub\n\turl = ../${LIB_SLUG}.git\n`);
  git.run(work, 'add', '-A');
  git.run(work, 'update-index', '--add', '--cacheinfo', `160000,${libSha},libs/sub`);
  git.run(work, 'commit', '-m', 'Fixture: environment file and submodule');
  git.run(work, 'push', 'origin', 'main');
  return { token, api, git, fixtureUrl, libUrl, workDir: work };
}

export async function startWorkspace(api: APIRequestContext, slug = FIXTURE_SLUG, ref?: string): Promise<WorkspaceDto> {
  const r = await api.post(`/api/v1/projects/${PROJECT}/repos/${slug}/workspaces`, { data: ref ? { ref } : {} });
  expect(r.status(), await r.text()).toBe(202);
  return r.json();
}

export async function getWorkspace(api: APIRequestContext, id: string): Promise<WorkspaceDto> {
  const r = await api.get(`/api/v1/workspaces/${id}`);
  expect(r.ok(), await r.text()).toBeTruthy();
  return r.json();
}

/** Polls until the workspace reaches `state`; fails fast (with the server's message) if it ends up Failed. */
export async function waitForState(api: APIRequestContext, id: string, state: string, timeoutMs = 300_000): Promise<WorkspaceDto> {
  const t0 = Date.now();
  for (;;) {
    const w = await getWorkspace(api, id);
    if (w.state === state) return w;
    if (w.state === 'Failed' && state !== 'Failed') throw new Error(`Workspace ${id} failed: ${w.error ?? '(no message)'}`);
    if (Date.now() - t0 > timeoutMs) throw new Error(`Workspace ${id} is ${w.state}, not ${state}, after ${timeoutMs / 1000}s (${w.error ?? ''})`);
    await new Promise((r) => setTimeout(r, 1500));
  }
}

export async function stopAndDelete(api: APIRequestContext, id: string): Promise<void> {
  await api.post(`/api/v1/workspaces/${id}/stop`).catch(() => {});
  await api.delete(`/api/v1/workspaces/${id}`).catch(() => {});
}

/**
 * Runs a command inside the workspace container the way ByteBench's task runner does: a non-interactive shell that sources ~/.bb-env
 * (secrets and repository env) first. Use {@link shell} for what the IDE terminal sees.
 */
export function sh(id: string, cmd: string, user = 'coder'): { code: number; out: string } {
  const r = spawnSync('docker', ['exec', '-u', user, containerFor(id), 'bash', '-c', `[ -f "$HOME/.bb-env" ] && . "$HOME/.bb-env"; ${cmd}`], { encoding: 'utf8' });
  return { code: r.status ?? -1, out: (r.stdout ?? '') + (r.stderr ?? '') };
}

/** Runs a command in an *interactive* bash as the workspace user: what a terminal in the IDE gets (~/.bashrc, which loads ~/.bb-env). stdout only. */
export function shell(id: string, cmd: string): string {
  const r = spawnSync('docker', ['exec', '-u', 'coder', containerFor(id), 'bash', '-ic', cmd], { encoding: 'utf8' });
  return r.stdout ?? '';
}

export async function pollSetupDone(api: APIRequestContext, id: string, timeoutMs = 240_000): Promise<{ state: string; message?: string | null; lines: string[] }> {
  const t0 = Date.now();
  for (;;) {
    const r = await api.get(`/api/v1/workspaces/${id}/setup`);
    const s = await r.json();
    if (s.state && !['Running', 'Pending'].includes(s.state)) return s;
    if (Date.now() - t0 > timeoutMs) throw new Error(`Setup still ${s.state} after ${timeoutMs / 1000}s: ${(s.lines ?? []).slice(-10).join('\n')}`);
    await new Promise((r2) => setTimeout(r2, 2000));
  }
}

export { existsSync, mkdirSync };
