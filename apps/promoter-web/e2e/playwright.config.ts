import { defineConfig } from '@playwright/test';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

import { settings } from './settings';

const here = dirname(fileURLToPath(import.meta.url));
const app = resolve(here, '..');

// Promoter journeys in a real browser against the real API and database (README.md, "End-to-end"). Playwright
// builds the dashboard and serves it with `vite preview`; the backend (API, migrated and seeded database) is already
// running at settings.apiUrl, started from the backend's compose stack by run.sh or CI (tools/testkit/compose.yml).
export default defineConfig({
  testDir: '.',
  testMatch: '*.spec.ts',
  outputDir: join(here, 'test-results'),
  fullyParallel: true,
  workers: process.env.CI ? 2 : 3,
  forbidOnly: !!process.env.CI,
  retries: 0,
  timeout: 90_000,
  expect: { timeout: 15_000 },
  reporter: process.env.CI ? [['list'], ['html', { open: 'never', outputFolder: join(here, 'playwright-report') }]] : 'list',
  use: {
    baseURL: settings.appUrl,
    viewport: { width: 1280, height: 900 },
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
    channel: process.env.E2E_CHANNEL || undefined,
  },
  webServer: [
    {
      command: `npx vite build && npx vite preview --host 127.0.0.1 --port ${settings.appPort} --strictPort`,
      cwd: app,
      url: settings.appUrl,
      timeout: 180_000,
      reuseExistingServer: !process.env.CI,
      stdout: 'ignore',
      env: {
        VITE_API_URL: settings.apiUrl,
        VITE_DEV_SIGN_IN: 'true',
        VITE_GOOGLE_WEB_CLIENT_ID: 'e2e-web-client.apps.googleusercontent.com',
      },
    },
  ],
});
