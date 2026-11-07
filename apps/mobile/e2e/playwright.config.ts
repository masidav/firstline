import { defineConfig } from '@playwright/test';
import { join, resolve } from 'node:path';

import { settings } from './settings';

const mobile = resolve(__dirname, '..');

// Critical fan journeys in a real browser against the real API and database (e2e/README.md). Playwright builds the
// production web export and serves it; the backend (API, migrated and seeded database) is already running at
// settings.apiUrl, started from the backend's compose stack by run.sh or the CI job (tools/testkit/compose.yml).
export default defineConfig({
  testDir: '.',
  testMatch: '*.spec.ts',
  outputDir: join(__dirname, 'test-results'),
  fullyParallel: true,
  workers: process.env.CI ? 2 : 3,
  forbidOnly: !!process.env.CI,
  retries: 0,
  timeout: 60_000,
  expect: { timeout: 15_000 },
  reporter: process.env.CI ? [['list'], ['html', { open: 'never', outputFolder: join(__dirname, 'playwright-report') }]] : 'list',
  use: {
    baseURL: settings.appUrl,
    viewport: { width: 390, height: 844 },
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
    // Locally, the installed Chrome (E2E_CHANNEL=chrome); in CI, the Chromium `playwright install` downloads.
    channel: process.env.E2E_CHANNEL || undefined,
  },
  webServer: [
    {
      // The production web build, served like a static host (index.html for every route). --clear: Metro's cache can
      // keep a module built with other EXPO_PUBLIC_ values (from a dev server run) and put those in this build.
      command: `npx expo export --platform web --clear --output-dir e2e/.web && node e2e/serve.mjs e2e/.web ${settings.appPort}`,
      cwd: mobile,
      url: settings.appUrl,
      timeout: 300_000,
      reuseExistingServer: !process.env.CI,
      stdout: 'ignore',
      env: {
        CI: '1',
        // Build only from these values: a developer's local env files must not point the build elsewhere.
        EXPO_NO_DOTENV: '1',
        EXPO_NO_TELEMETRY: '1',
        EXPO_PUBLIC_API_URL: settings.apiUrl,
        EXPO_PUBLIC_DEV_SIGN_IN: 'true',
        EXPO_PUBLIC_GOOGLE_WEB_CLIENT_ID: 'e2e-web-client.apps.googleusercontent.com',
      },
    },
  ],
});
