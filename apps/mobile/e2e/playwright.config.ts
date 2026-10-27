import { defineConfig } from '@playwright/test';
import { randomBytes } from 'node:crypto';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';

import { settings } from './settings';

const repo = resolve(__dirname, '../../..');
const mobile = resolve(__dirname, '..');

// Critical fan journeys in a real browser against the real API and database (e2e/README.md). Playwright builds the
// production web export and starts the API itself; the database comes ready (run.sh or the CI job).
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
      command: 'dotnet run --project services/api --no-launch-profile',
      cwd: repo,
      url: `${settings.apiUrl}/health`,
      timeout: 240_000,
      reuseExistingServer: !process.env.CI,
      stdout: 'ignore',
      stderr: 'pipe',
      env: {
        ASPNETCORE_ENVIRONMENT: 'Development',
        ASPNETCORE_URLS: `http://127.0.0.1:${settings.apiPort}`,
        ConnectionStrings__Database: settings.npgsql,
        Auth__SigningKey: randomBytes(32).toString('base64'),
        Auth__DevSignIn: 'true',
        // Every test signs fans up from one address; the production limit (20 a minute) would throttle the suite.
        Auth__PermitsPerMinute: '1000',
        Cors__AllowedOrigins: settings.appUrl,
        Storage__LocalPath: join(tmpdir(), 'fandom-e2e-storage'),
      },
    },
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
