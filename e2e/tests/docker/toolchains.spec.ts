// Real-Docker check of every curated toolchain image (README "Workspaces" > toolchains): the image is built on demand from docker/workspace
// on the toolchain's base image, a workspace starts from it as uid 1000 with the collaborator users in place, the injected environment reaches
// non-interactive shells, and a trivial task of that toolchain runs. The first run builds six images (slow, several GB; they are cached afterwards),
// so this file is opt-in: E2E_TOOLCHAINS=1 e2e/run.sh docker toolchains   (the nightly workflow sets it).
import { test, expect, loginAs } from '../../support/fixtures.ts';
import { type BrowserContext, type Page } from '@playwright/test';
import path from 'node:path';
import { writeFileSync } from 'node:fs';
import { createSimpleRepo, startWorkspace, waitForState, stopAndDelete, pollSetupDone, exec, type Fixtures } from '../../support/workspace.ts';

test.describe.configure({ mode: 'serial' });
test.skip(process.env.E2E_TOOLCHAINS !== '1', 'set E2E_TOOLCHAINS=1 to build and run every toolchain image');

const SLUG = 'toolchain-fixture';

const CASES: { toolchain: string; branch: string; command: string; expect: RegExp }[] = [
  { toolchain: 'dotnet@10', branch: 'tc-dotnet', command: 'dotnet --version', expect: /^10\.\d+\.\d+/m },
  { toolchain: 'node@22', branch: 'tc-node', command: 'node --version', expect: /^v22\./m },
  { toolchain: 'python@3.13', branch: 'tc-python', command: 'python --version', expect: /Python 3\.13\./ },
  { toolchain: 'go', branch: 'tc-go', command: 'go version', expect: /go version go1\.24/ },
  { toolchain: 'java@21', branch: 'tc-java', command: 'java -version 2>&1; mvn -v | head -1', expect: /openjdk version "21[^"]*"[\s\S]*Apache Maven 3\.9/ },
  { toolchain: 'rust', branch: 'tc-rust', command: 'rustc --version && cargo --version', expect: /rustc 1\.\d+[\s\S]*cargo 1\.\d+/ },
];

let ctx: BrowserContext;
let page: Page;
let fx: Fixtures;

test.beforeAll(async ({ browser }) => {
  ctx = await browser.newContext({ baseURL: process.env.E2E_BASE_URL, viewport: { width: 1360, height: 860 } });
  page = await ctx.newPage();
  await loginAs(page, 'alice');
  fx = await createSimpleRepo(page, process.env.E2E_BASE_URL!, SLUG, 'collab-repo');
  for (const c of CASES) {
    fx.git.run(fx.workDir, 'checkout', '-b', c.branch, 'main');
    writeFileSync(path.join(fx.workDir, '.bytebench', 'workspace.yml'),
      `toolchain: ${c.toolchain}\nsetup:\n  - echo setup-ran >> .setup-marker\ntasks:\n  ver: '(${c.command.replace(/'/g, "''")}) 2>&1'\nenv:\n  E2E_PLAIN: plain-value\nsubmodules: false\n`);
    fx.git.run(fx.workDir, 'add', '-A'); fx.git.run(fx.workDir, 'commit', '-m', `toolchain ${c.toolchain}`);
    fx.git.run(fx.workDir, 'push', 'origin', c.branch);
  }
});

test.afterAll(async () => {
  fx?.git.dispose();
  await ctx?.close();
});

for (const c of CASES) {
  test(`toolchain ${c.toolchain}: the image builds, the workspace starts and a ${c.command.split(' ')[0]} task runs`, async () => {
    test.setTimeout(30 * 60_000);
    const w = await startWorkspace(fx.api, SLUG, c.branch);
    try {
      await waitForState(fx.api, w.id, 'Running', 25 * 60_000);
      const setup = await pollSetupDone(fx.api, w.id);
      expect(setup.state, setup.message ?? setup.lines.join('\n')).toBe('Succeeded');
      expect(exec(w.id, 'coder', 'id', '-u').out.trim(), 'unprivileged').toBe('1000');
      expect(exec(w.id, 'coder', 'bash', '-c', 'printf %s "$E2E_PLAIN"').stdout, 'the environment reaches a plain bash -c').toBe('plain-value');
      expect(exec(w.id, 'coder', 'getent', 'passwd', 'bbc10').code, 'collaborator users exist').toBe(0);
      const run = await fx.api.post(`/api/v1/workspaces/${w.id}/tasks/ver/run`);
      expect(run.status(), await run.text()).toBe(202);
      await expect.poll(async () => JSON.stringify((await (await fx.api.get(`/api/v1/workspaces/${w.id}/tasks/ver/logs`)).json()).lines), { timeout: 120_000 }).toContain('[task finished');
      const lines = ((await (await fx.api.get(`/api/v1/workspaces/${w.id}/tasks/ver/logs`)).json()).lines as string[]).join('\n');
      expect(lines, lines).toMatch(c.expect);
    } finally { await stopAndDelete(fx.api, w.id); }
  });
}
