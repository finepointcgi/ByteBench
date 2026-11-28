import { test, expect } from '../../support/fixtures.ts';

test.describe('sign in and access control', () => {
  test('wrong password is refused with a message, right password signs in and out', async ({ page, open }) => {
    await open('/account/login');
    await page.locator('#login-user').fill('alice');
    await page.locator('#login-password').fill('not-the-password');
    await page.getByRole('button', { name: 'Sign in' }).click();
    await expect(page.getByRole('alert')).toContainText(/invalid|incorrect|wrong/i);
    await expect(page).toHaveURL(/\/account\/login/);

    // Blazor's enhanced navigation may still be applying the error page when we type again, and the dashboard's account menu is interactive
    // only once its circuit is up: a lost keystroke or click is retried instead of waited on for ever (this was the source of rare failures).
    await expect(async () => {
      await page.locator('#login-user').fill('alice');
      await page.locator('#login-password').fill('ByteBench123');
      await page.getByRole('button', { name: 'Sign in' }).click();
      await page.waitForURL((u) => !u.pathname.startsWith('/account/login'), { timeout: 8_000 });
    }).toPass({ timeout: 40_000 });
    await expect(page.getByText('Web Platform').first()).toBeVisible();

    // Sign out (a POST with an antiforgery token) returns to the sign-in page, and the session is gone.
    await expect(async () => {
      await page.getByTestId('account-menu').click();
      await page.getByTestId('sign-out').click({ timeout: 3_000 });
    }).toPass({ timeout: 30_000 });
    await expect(page).toHaveURL(/\/account\/login/);
    await page.goto('/projects/new');
    await expect(page).toHaveURL(/\/account\/login/);
  });

  test('anonymous visitors are sent to sign in', async ({ page }) => {
    for (const url of ['/', '/admin/users', '/p/WEB', '/workspaces']) {
      await page.goto(url);
      await expect(page, url).toHaveURL(/\/account\/login/);
    }
  });

  test('a private project does not exist for a non-member (no data leak)', async ({ page, login, open }) => {
    await login('bob'); // bob is not a member of OPS (private)
    await open('/p/OPS');
    await expect(page.locator('body')).not.toContainText('Terraform and deployment scripts');
    await expect(page.getByText(/not found|don't have access|no access/i).first()).toBeVisible();
    // Found by the AI explorer, fixed: the not-found page of an inaccessible project answers 404 with the site's own title (see not-found.spec.ts for every page).
    for (const sub of ['issues', 'members']) {
      const resp = await page.goto(`/p/OPS/${sub}`);
      expect(resp?.status(), `/p/OPS/${sub} for a non-member`).toBe(404);
      expect(await page.title()).toMatch(/^Not found/);
    }
    // ...and the API says the same thing
    const r = await page.request.get('/api/v1/projects/OPS/issues');
    expect([401, 403, 404]).toContain(r.status());
    await open('/p/OPS/repos/infra');
    await expect(page.locator('body')).not.toContainText('main.tf');
  });

  test('a sign-out POST without a form body or token is a plain 400 (and not a server error in the log)', async ({ page, login }) => {
    await login('bob');
    // The run fails on any `fail:` line in the server log, so this also proves the bad request is not logged as an error.
    expect((await page.request.post('/account/logout')).status()).toBe(400);
    expect((await page.request.post('/account/logout', { form: {} })).status()).toBe(400);
    expect((await page.request.post('/account/logout', { data: {} })).status()).toBe(400);
    // still signed in: nothing was done
    await page.goto('/');
    await expect(page.getByTestId('account-menu')).toBeVisible();
  });

  test('non-admins cannot open administration pages', async ({ page, login, open }) => {
    await login('bob');
    for (const url of ['/admin/users', '/admin/audit', '/admin/system', '/admin/workspaces']) {
      await open(url);
      await expect(page.locator('body'), url).not.toContainText('New user');
      await expect(page.getByText(/administrators|admin only|forbidden|not allowed|permission|access denied|not found/i).first(), url).toBeVisible();
    }
  });
});
