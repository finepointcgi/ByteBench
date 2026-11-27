import { test, expect } from '../../support/fixtures.ts';

test.describe('sign in and access control', () => {
  test('wrong password is refused with a message, right password signs in and out', async ({ page, open }) => {
    await open('/account/login');
    await page.locator('#login-user').fill('alice');
    await page.locator('#login-password').fill('not-the-password');
    await page.getByRole('button', { name: 'Sign in' }).click();
    await expect(page.getByRole('alert')).toContainText(/invalid|incorrect|wrong/i);
    await expect(page).toHaveURL(/\/account\/login/);

    await page.locator('#login-user').fill('alice');
    await page.locator('#login-password').fill('ByteBench123');
    await page.getByRole('button', { name: 'Sign in' }).click();
    await page.waitForURL((u) => !u.pathname.startsWith('/account/login'));
    await expect(page.getByText('Web Platform').first()).toBeVisible();

    // Sign out (a POST with an antiforgery token) returns to the sign-in page, and the session is gone.
    await page.getByTestId('account-menu').click();
    await page.getByTestId('sign-out').click();
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
    // Found by the AI explorer: the not-found page of an inaccessible project has a title like "Issues · " (nothing after the separator). Advisory, not failing.
    for (const sub of ['issues', 'members']) {
      const resp = await page.goto(`/p/OPS/${sub}`);
      if (/·\s*$/.test(await page.title()) || resp?.status() === 200) test.info().annotations.push({ type: 'ux-advisory', description: `/p/OPS/${sub} for a non-member: HTTP ${resp?.status()}, title "${await page.title()}"` });
    }
    // ...and the API says the same thing
    const r = await page.request.get('/api/v1/projects/OPS/issues');
    expect([401, 403, 404]).toContain(r.status());
    await open('/p/OPS/repos/infra');
    await expect(page.locator('body')).not.toContainText('main.tf');
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
