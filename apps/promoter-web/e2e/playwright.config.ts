import { defineConfig } from '@playwright/test';
import { randomBytes } from 'node:crypto';
import { tmpdir } from 'node:os';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

import { settings } from './settings';

const here = dirname(fileURLToPath(import.meta.url));
const repo = resolve(here, '../../..');
const app = resolve(here, '..');

// Promoter journeys in a real browser against the real API and database (README.md, "End-to-end"). Playwright
// builds the dashboard, serves it with `vite preview`, and starts the API; the database comes ready (run.sh or CI).
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
        Auth__PermitsPerMinute: '1000',
        Cors__AllowedOrigins: settings.appUrl,
        Storage__LocalPath: join(tmpdir(), 'fandom-e2e-storage'),
      },
    },
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
