import { test, expect, expectNoBlazorError, type Page } from '../../support/fixtures.ts';

// Cheap, deterministic layout and accessibility basics on the main pages at desktop and phone widths.
const PAGES = ['/', '/p/WEB', '/p/WEB/board', '/p/WEB/backlog', '/p/WEB/issues/3', '/p/WEB/repos', '/p/WEB/repos/web-app', '/explore', '/account/profile', '/admin/users'];

async function horizontalOverflow(page: Page): Promise<number> {
  return page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
}

for (const [label, size] of [['desktop', { width: 1360, height: 860 }], ['phone', { width: 390, height: 800 }]] as const) {
  test.describe(`layout at ${label} width`, () => {
    test.use({ viewport: size });
    test.beforeEach(async ({ login }) => { await login('alice'); });

    for (const url of PAGES) {
      test(`${url} has no horizontal page scroll and renders its content`, async ({ page, open }) => {
        await open(url);
        await expectNoBlazorError(page);
        expect(await horizontalOverflow(page), 'page scrolls sideways').toBeLessThanOrEqual(2);
      });
    }
  });
}

test.describe('accessibility basics', () => {
  test.beforeEach(async ({ login }) => { await login('alice'); });

  for (const url of PAGES) {
    test(`${url}: title, language, landmarks, names on controls`, async ({ page, open }) => {
      await open(url);
      expect((await page.title()).trim().length, 'document title').toBeGreaterThan(0);
      expect(await page.locator('html').getAttribute('lang'), 'html lang').toBeTruthy();
      // Advisory (known debt, reported in the run's annotations rather than failing the build): a <main> landmark and a single <h1>.
      if (await page.locator('main, [role=main]').count() === 0) test.info().annotations.push({ type: 'a11y-advisory', description: `${url}: no <main> landmark` });
      const problems = await page.evaluate(() => {
        const out: string[] = [];
        const name = (e: Element) => (e.getAttribute('aria-label') || e.getAttribute('aria-labelledby') || e.getAttribute('title') || (e as HTMLElement).innerText || e.textContent || '').trim();
        document.querySelectorAll('img').forEach((i) => { if (!i.hasAttribute('alt')) out.push('img without alt: ' + i.src.slice(-40)); });
        document.querySelectorAll('button, [role=button]').forEach((b) => {
          if ((b as HTMLElement).offsetParent === null) return;
          if (b.getAttribute('tabindex') === '-1') return; // decorative input adornments (not keyboard-reachable)
          // KNOWN DEBT (reported as an advisory): MudMenu's icon activator ignores AriaLabel, so menus like "More list actions" have a nameless button.
          if (!name(b) && !b.querySelector('img[alt]')) out.push((b.parentElement?.classList.contains('mud-menu') ? 'ADVISORY ' : '') + 'button without an accessible name: ' + b.outerHTML.slice(0, 100));
        });
        document.querySelectorAll('input:not([type=hidden]):not([type=checkbox]):not([type=radio]), textarea, select').forEach((i) => {
          if ((i as HTMLElement).offsetParent === null) return;
          const id = i.getAttribute('id');
          const labelled = i.getAttribute('aria-label') || i.getAttribute('aria-labelledby') || i.getAttribute('title') || i.getAttribute('placeholder') || (id && document.querySelector(`label[for="${id}"]`)) || i.closest('label');
          if (!labelled) out.push('field without a label: ' + i.outerHTML.slice(0, 100));
        });
        return out;
      });
      const h1s = await page.locator('h1').count();
      if (h1s !== 1) test.info().annotations.push({ type: 'a11y-advisory', description: `${url}: ${h1s} h1 elements` });
      for (const a of problems.filter((p) => p.startsWith('ADVISORY '))) test.info().annotations.push({ type: 'a11y-advisory', description: `${url}: ${a}` });
      expect(problems.filter((p) => !p.startsWith('ADVISORY '))).toEqual([]);
    });
  }
});
