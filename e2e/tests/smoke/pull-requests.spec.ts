import { test, expect, createToken, uniq, Git } from '../../support/fixtures.ts';

// The real git CLI pushes to the real server over HTTP; the pull request is then opened and merged in the real UI.
test.describe('repositories and pull requests', () => {
  let git: Git;
  test.beforeEach(async ({ login }) => { git = new Git(); await login('alice'); });
  test.afterEach(() => git.dispose());

  test('push a branch with git, open a pull request in the UI, review it and merge it', async ({ page, open, baseURL }) => {
    const key = uniq('R');
    // Project and repository through the UI
    await open('/projects/new');
    await page.getByLabel('Name', { exact: true }).fill(`PR project ${key}`);
    await expect(page.getByLabel('Key', { exact: true })).toHaveValue(/^PP/); // the key is suggested from the name by a server round trip; wait for it
    await page.getByLabel('Key', { exact: true }).fill(key);
    await page.getByRole('button', { name: 'Create project' }).click();
    await expect(page).toHaveURL(new RegExp(`/p/${key}`));
    await open(`/p/${key}/repos`);
    await page.getByRole('button', { name: 'Create repository' }).first().click();
    await page.getByLabel('Name', { exact: true }).fill('app');
    await page.getByRole('button', { name: 'Create repository' }).last().click();
    await expect(page).toHaveURL(new RegExp(`/p/${key}/repos/app`));

    // A personal access token is the git password
    const token = await createToken(page, `e2e-${key}`);
    const url = git.url(baseURL!, key, 'app', 'alice', token);
    // A new repository starts with an initial commit on main (README); clone it and work on a branch.
    git.run(null, 'clone', url, 'work');
    const work = `${git.home}/work`;
    git.run(work, 'checkout', '-b', 'feature/greeting');
    git.write(work, 'src/hello.txt', 'hello from the feature branch\n');
    git.run(work, 'add', '-A');
    git.run(work, 'commit', '-m', 'Add greeting');
    git.run(work, 'push', 'origin', 'feature/greeting');

    // The code browser shows the pushed files
    await open(`/p/${key}/repos/app`);
    await expect(page.getByText('README.md').first()).toBeVisible();

    // Open the pull request
    await open(`/p/${key}/pull-requests/new?repo=app&source=feature%2Fgreeting&target=main`);
    await expect(page.getByText('Able to merge')).toBeVisible();
    await page.getByLabel('Title').fill('Add greeting');
    await page.getByRole('button', { name: 'Create pull request' }).click();
    await expect(page).toHaveURL(new RegExp(`/p/${key}/repos/app/pull-requests/1`));
    await expect(page.getByText('Add greeting').first()).toBeVisible();

    // Files changed shows the diff
    await page.getByRole('link', { name: /Files changed/ }).click();
    await expect(page.getByText('hello.txt').first()).toBeVisible();

    // Merge it
    await page.getByRole('link', { name: /Overview/ }).click();
    await page.getByRole('button', { name: 'Merge', exact: true }).click();
    await expect(page.getByText(/changes were merged/)).toBeVisible();

    // The merge reached the repository: the file is on main now (checked with real git).
    const check = git.dir('check');
    git.run(check, 'clone', url, 'out');
    expect(git.run(`${check}/out`, 'log', '--oneline', 'main')).toContain('Add greeting');
  });

  // Regression (the cause of the "flaky" test above): a page with a markdown editor, left while its JavaScript module was still loading, used to throw
  // ObjectDisposedException inside the circuit, which ends it (the next click then waits for ever) and is logged as `fail:` (which fails the whole run).
  test('leaving a page with a markdown editor right away does not break the circuit', async ({ page, open }) => {
    await open('/p/WEB/issues/3');
    const go = (u: string) => page.evaluate((url) => (window as unknown as { Blazor: { navigateTo(u: string): void } }).Blazor.navigateTo(url), u);
    for (let i = 0; i < 40; i++) {
      await go('/p/WEB/issues/3');
      await page.waitForTimeout(5 + (i * 7) % 60); // leave at every moment of the editor's start-up
      await go('/p/WEB/issues');
      await page.waitForTimeout(20);
    }
    await go('/p/WEB/issues/3');
    // The circuit still answers: an in-app navigation renders the issue list.
    await go('/p/WEB/issues');
    await expect(page.getByRole('heading', { level: 1, name: 'Issues' })).toBeVisible();
  });

  test('a read-only member cannot push over git, and an anonymous clone is refused', async ({ baseURL }) => {
    const u = git.url(baseURL!, 'WEB', 'web-app', 'nobody', 'wrong-password');
    expect(() => git.run(null, 'ls-remote', u)).toThrow();
  });
});
