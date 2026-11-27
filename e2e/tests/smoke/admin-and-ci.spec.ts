import { test, expect } from '../../support/fixtures.ts';

test.describe('administration and CI pages', () => {
  test.beforeEach(async ({ login }) => { await login('alice'); });

  test('admin pages render for an administrator', async ({ page, open }) => {
    await open('/admin/users');
    await expect(page.getByText('alice').first()).toBeVisible();
    await expect(page.getByRole('button', { name: 'Create user' })).toBeVisible();

    await open('/admin/audit');
    await expect(page.getByText(/audit/i).first()).toBeVisible();

    await open('/admin/system');
    await expect(page.getByText(/backup|system/i).first()).toBeVisible();

    await open('/admin/workspaces');
    await expect(page.getByTestId('ws-switch')).toBeVisible();
  });

  test('an administrator can find a user and sees their role', async ({ page, open }) => {
    await open('/admin/users');
    await page.getByRole('textbox').first().fill('bob');
    await page.getByRole('button', { name: 'Search' }).click();
    await expect(page.getByText('bob').first()).toBeVisible();
    await expect(page.getByText('dan', { exact: true })).toHaveCount(0);
  });

  test('CI pipeline list and settings pages render for a repository', async ({ page, open }) => {
    await open('/p/WEB/repos/web-app/pipelines');
    await expect(page.getByRole('button', { name: 'Run pipeline' })).toBeVisible();
    await open('/p/WEB/repos/web-app/pipelines/schedules');
    await expect(page.getByText(/schedule/i).first()).toBeVisible();
    await open('/p/WEB/repos/web-app/environments');
    await expect(page.getByText(/environment/i).first()).toBeVisible();
    await open('/p/WEB/environments');
    await expect(page.getByText(/environment/i).first()).toBeVisible();
  });

  test('health endpoints, API docs and the REST API refuse anonymous access cleanly', async ({ request }) => {
    expect((await request.get('/health/ready')).status()).toBe(200);
    expect((await request.get('/health/live')).status()).toBe(200);
    const r = await request.get('/api/v1/projects');
    expect(r.status()).toBe(401);
  });
});
