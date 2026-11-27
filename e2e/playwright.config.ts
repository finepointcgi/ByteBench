import { defineConfig, devices } from '@playwright/test';

// Two tiers share this config:
//   smoke  no Docker; a real server on a random port with a temp SQLite DB and seeded demo accounts; runs on every PR.
//   docker needs a Docker daemon; builds the real workspace image and drives real workspaces; nightly / on demand.
// support/global-setup.ts starts the server for the tier named by E2E_TIER (or --project=docker).
const isCi = !!process.env.CI;

export default defineConfig({
  globalSetup: './support/global-setup.ts',
  outputDir: './test-results/artifacts',
  reporter: isCi
    ? [['list'], ['html', { open: 'never', outputFolder: 'playwright-report' }], ['json', { outputFile: 'test-results/results.json' }]]
    : [['list'], ['html', { open: 'never', outputFolder: 'playwright-report' }], ['json', { outputFile: 'test-results/results.json' }]],
  forbidOnly: isCi,
  retries: isCi ? 1 : 0,
  use: {
    // Failures keep a trace, a screenshot and a video; passing tests keep nothing.
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
    video: 'retain-on-failure',
    actionTimeout: 15_000,
    navigationTimeout: 30_000,
    viewport: { width: 1360, height: 860 },
  },
  expect: { timeout: 10_000 },
  projects: [
    {
      name: 'smoke',
      testDir: './tests/smoke',
      timeout: 60_000,
      fullyParallel: true,
      workers: isCi ? 2 : 3,
      use: { ...devices['Desktop Chrome'], viewport: { width: 1360, height: 860 } },
    },
    {
      name: 'docker',
      testDir: './tests/docker',
      // Workspaces start real containers; one test file at a time, and tests inside a file run in order.
      timeout: 6 * 60_000,
      fullyParallel: false,
      workers: 1,
      retries: 0,
      use: { ...devices['Desktop Chrome'], viewport: { width: 1360, height: 860 } },
    },
  ],
});
