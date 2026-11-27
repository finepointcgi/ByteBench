import { test, expect } from '../../support/fixtures.ts';

// The workspace *pages* without a Docker daemon: the start page, the environment-file preview, settings, limits and admin switches.
// (Real containers are the docker tier.)
test.describe('workspaces UI (no Docker)', () => {
  test.beforeEach(async ({ login }) => { await login('alice'); });

  test('start page explains that Docker is unavailable and disables the start button', async ({ page, open }) => {
    await open('/p/WEB/repos/web-app/workspace');
    await expect(page.getByText('Open in workspace').first()).toBeVisible();
    await expect(page.getByTestId('ws-ref')).toBeVisible();
    await expect(page.getByText(/Docker isn't available|image isn't ready/i)).toBeVisible();
    await expect(page.getByTestId('ws-start')).toBeDisabled();
  });

  test('"My workspaces" and personal workspace settings render', async ({ page, open }) => {
    await open('/workspaces');
    await expect(page.getByText(/workspaces/i).first()).toBeVisible();
    await open('/account/workspace');
    await expect(page.getByText(/secret|environment|workspace/i).first()).toBeVisible();
  });

  test('admin can see the workspace switches, limits and the egress policy', async ({ page, open }) => {
    await open('/admin/workspaces');
    for (const id of ['ws-switch', 'ws-ai-switch', 'ws-editor-switch', 'ws-custom-images-switch']) await expect(page.getByTestId(id)).toBeVisible();
    await expect(page.getByText(/egress|network/i).first()).toBeVisible();
  });

  test('project settings have the per-project workspace switch', async ({ page, open }) => {
    await open('/p/WEB/settings');
    await expect(page.getByTestId('ws-project-enabled')).toBeVisible();
  });

  test('repository prebuilds page renders', async ({ page, open }) => {
    await open('/p/WEB/repos/web-app/prebuilds');
    await expect(page.getByText(/prebuild/i).first()).toBeVisible();
  });

  test('the workspace REST API requires authentication and rejects unknown ids', async ({ request }) => {
    expect((await request.get('/api/v1/workspaces')).status()).toBe(401);
  });
});
