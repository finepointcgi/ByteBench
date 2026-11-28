import { test, expect, DEMO_PASSWORD } from '../../support/fixtures.ts';
import { request as pwRequest } from '@playwright/test';

// A page of a project the user cannot see must look exactly like a page of a project that does not exist: a real 404 status and the
// site's own "Not found" title, never a 200 with an empty "Issues · " title (which also leaked nothing, but was wrong) and never a
// different answer for "exists but private" than for "does not exist".
const PAGES = [
  '', '/issues', '/issues/new', '/issues/1', '/board', '/boards/1', '/boards/1/settings', '/backlog', '/epics', '/roadmap', '/milestones', '/versions', '/filters',
  '/members', '/reports', '/reports/delivery', '/releases', '/environments', '/flags', '/flags/some-flag', '/packages', '/packages/1', '/snippets', '/wiki', '/wiki/home',
  '/tests', '/tests/plans', '/tests/runs', '/tests/runs/1', '/tests/cases/1', '/service-desk', '/code-search', '/import', '/import/tracker',
  '/pull-requests', '/pull-requests/new', '/workspace',
  '/repos', '/repos/web-app', '/repos/web-app/commits', '/repos/web-app/commits/abc1234', '/repos/web-app/branches', '/repos/web-app/tags', '/repos/web-app/compare',
  '/repos/web-app/pull-requests', '/repos/web-app/pull-requests/1', '/repos/web-app/pipelines', '/repos/web-app/pipelines/1', '/repos/web-app/insights',
  '/repos/web-app/settings', '/repos/web-app/settings/branches', '/repos/web-app/workspace', '/repos/web-app/edit/main', '/repos/web-app/search', '/repos/web-app/security',
  '/settings', '/settings/workflow', '/settings/webhooks', '/settings/audit', '/settings/fields', '/settings/automation', '/settings/integrations',
];

async function fetchAs(baseURL: string, cookie: string | null, path: string) {
  const ctx = await pwRequest.newContext({ baseURL, extraHTTPHeaders: cookie ? { cookie } : {} });
  try {
    const r = await ctx.get(path, { maxRedirects: 0 });
    const body = await r.text();
    return { status: r.status(), title: /<title[^>]*>([\s\S]*?)<\/title>/i.exec(body)?.[1]?.trim() ?? '(no title)', body };
  } finally { await ctx.dispose(); }
}

test.describe('not-found pages', () => {
  test('a private project the user cannot see answers 404 with the site title on every /p/{key}/... page, like a project that does not exist', async ({ page, login, baseURL }) => {
    await login('bob'); // not a member of the private OPS project
    const cookie = (await page.context().cookies()).map((c) => `${c.name}=${c.value}`).join('; ');
    const bad: string[] = [];
    for (const p of PAGES) {
      const hidden = await fetchAs(baseURL!, cookie, `/p/OPS${p}`);
      const missing = await fetchAs(baseURL!, cookie, `/p/NOSUCH${p}`);
      if (hidden.status !== 404) bad.push(`/p/OPS${p}: status ${hidden.status}`);
      if (missing.status !== 404) bad.push(`/p/NOSUCH${p}: status ${missing.status}`);
      if (hidden.title !== missing.title) bad.push(`/p/OPS${p}: title "${hidden.title}" differs from a missing project's "${missing.title}"`);
      if (!/not found/i.test(hidden.title) || hidden.title.endsWith('·')) bad.push(`/p/OPS${p}: title "${hidden.title}"`);
      if (/Infrastructure/.test(hidden.body)) bad.push(`/p/OPS${p}: the page mentions the private project`);
    }
    expect(bad).toEqual([]);
  });

  test('something missing inside a project the user can see is a 404 too', async ({ page, login, baseURL }) => {
    await login('alice');
    const cookie = (await page.context().cookies()).map((c) => `${c.name}=${c.value}`).join('; ');
    const bad: string[] = [];
    for (const p of ['/issues/99999', '/repos/no-such-repo', '/repos/no-such-repo/pull-requests/1', '/repos/web-app/pull-requests/99999']) {
      const r = await fetchAs(baseURL!, cookie, `/p/WEB${p}`);
      if (r.status !== 404) bad.push(`/p/WEB${p}: status ${r.status}`);
      if (r.title.endsWith('·') || r.title.endsWith('· ')) bad.push(`/p/WEB${p}: title "${r.title}"`);
    }
    expect(bad).toEqual([]);
  });

  test('the not-found page has a heading, a landmark and a way back', async ({ page, login, open }) => {
    await login('bob');
    await open('/p/OPS/issues');
    await expect(page).toHaveTitle(/Not found/);
    await expect(page.getByRole('heading', { level: 1, name: 'Not found' })).toBeVisible();
    await expect(page.getByRole('link', { name: 'Back to dashboard' }).or(page.getByRole('button', { name: 'Back to dashboard' }))).toBeVisible();
    // The same when the user gets there by in-app navigation (no page load, so no HTTP status: the page itself must say so).
    await open('/');
    await page.evaluate(() => (window as unknown as { Blazor: { navigateTo(u: string): void } }).Blazor.navigateTo('/p/OPS/board'));
    await expect(page.getByRole('heading', { level: 1, name: 'Not found' })).toBeVisible();
    await expect(page).toHaveTitle(/Not found/);
  });
});
void DEMO_PASSWORD;
