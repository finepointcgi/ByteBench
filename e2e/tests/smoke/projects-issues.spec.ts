import { test, expect, uniq, expectNoBlazorError } from '../../support/fixtures.ts';

test.describe('projects, issues and boards', () => {
  test.beforeEach(async ({ login }) => { await login('alice'); });

  test('create a project, a repository and an issue; comment and close it', async ({ page, open }) => {
    const key = uniq('P');
    await open('/projects/new');
    await page.getByLabel('Name', { exact: true }).fill(`E2E ${key}`);
    await expect(page.getByLabel('Key', { exact: true })).toHaveValue(/^EP/); // suggested from the name by a server round trip; wait for it
    await page.getByLabel('Key', { exact: true }).fill(key);
    await page.getByLabel('Description').fill('Created by the e2e suite');
    await page.getByRole('button', { name: 'Create project' }).click();
    await expect(page).toHaveURL(new RegExp(`/p/${key}`));
    await expect(page.getByText(`E2E ${key}`).first()).toBeVisible();

    // A repository
    await open(`/p/${key}/repos`);
    await page.getByRole('button', { name: 'Create repository' }).first().click();
    await page.getByLabel('Name', { exact: true }).fill('service');
    await page.getByRole('button', { name: 'Create repository' }).last().click();
    await expect(page).toHaveURL(new RegExp(`/p/${key}/repos/service`));

    // An issue
    await open(`/p/${key}/issues/new`);
    await page.getByLabel('Title').fill('Login button does nothing');
    await page.getByPlaceholder(/Describe the problem/).fill('Steps: click it. Expected: sign in.');
    await page.getByRole('button', { name: 'Create issue' }).click();
    await expect(page).toHaveURL(new RegExp(`/p/${key}/issues/1`));
    await expect(page.getByText('Login button does nothing').first()).toBeVisible();

    // A comment
    await page.getByPlaceholder('Leave a comment…').fill('Reproduced on Chrome');
    await page.getByRole('button', { name: 'Comment', exact: true }).click();
    await expect(page.getByText('Reproduced on Chrome')).toBeVisible();

    // Close it
    await page.getByRole('button', { name: 'Close issue', exact: true }).first().click();
    await expect(page.getByRole('button', { name: 'Reopen' })).toBeVisible();

    // It is on the closed list, not the open one
    await open(`/p/${key}/issues`);
    await expect(page.getByText('Login button does nothing')).toHaveCount(0);
  });

  test('the new-issue form rejects an empty title and survives hostile input', async ({ page, open }) => {
    await open('/p/WEB/issues/new');
    await page.getByRole('button', { name: 'Create issue' }).click();
    await expect(page.getByText('The Title field is required.')).toBeVisible();
    await expect(page).toHaveURL(/issues\/new/);

    const nasty = `<img src=x onerror="window.__xss=1"> ${'x'.repeat(300)} '; DROP TABLE Issues;--`;
    await page.getByLabel('Title').fill(nasty);
    await page.getByPlaceholder(/Describe the problem/).fill('<script>window.__xss=1</script>');
    await page.getByRole('button', { name: 'Create issue' }).click();
    await page.waitForTimeout(1500);
    // Either created or rejected with a validation message; never an error page, and the markup never executes.
    expect(await page.evaluate(() => (window as unknown as { __xss?: number }).__xss)).toBeUndefined();
    await expectNoBlazorError(page);
  });

  test('the board shows the active sprint in its columns', async ({ page, open }) => {
    await open('/p/WEB/board');
    for (const col of ['To Do', 'In Progress', 'In Review', 'Done']) await expect(page.getByText(col, { exact: true }).first()).toBeVisible();
    await expect(page.getByText('WEB-3').first()).toBeVisible();
    await expect(page.getByText('Drag-and-drop Kanban board').first()).toBeVisible();
  });

  test('plan a sprint: create it, move a backlog issue into it', async ({ page, open }) => {
    const name = `E2E sprint ${uniq()}`;
    await open('/p/WEB/backlog');
    await page.getByRole('button', { name: 'Create sprint' }).click();
    await page.getByLabel('Name').first().fill(name);
    await page.getByRole('button', { name: /^(Create|Save)/ }).last().click();
    await expect(page.getByText(name)).toBeVisible();

    await page.getByRole('button', { name: 'Move WEB-6' }).click();
    await page.getByRole('menuitem', { name: new RegExp(name) }).click();
    const sprint = page.locator('section, .mud-paper, div').filter({ hasText: name }).filter({ hasText: 'WEB-6' }).first();
    await expect(sprint).toBeVisible();
  });

  test('issue search and filters respond', async ({ page, open }) => {
    await open('/p/WEB/issues');
    await expect(page.getByText('8 issues')).toBeVisible();
    await page.getByRole('tab', { name: /Closed/ }).click();
    await expect(page.getByText(/26 issues/)).toBeVisible();
  });
});
