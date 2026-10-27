import { expect, test } from '@playwright/test';

import { chooseHandle, chooseHubCity, uniqueName } from './fixtures';

// Google itself is stubbed: the test checks what the app sends to Google and what it does with Google's answer.
// The answer carries a developer token, which the Development API accepts as a Google identity.

test('Continue with Google sends the fan to Google, and a new fan comes back to choose a handle', async ({ page }) => {
  let authorize: URL | undefined;
  await page.route('https://accounts.google.com/**', async (route) => {
    authorize = new URL(route.request().url());
    await route.fulfill({ status: 200, contentType: 'text/html', body: '<h1>Google (stub)</h1>' });
  });

  await page.goto('/sign-in');
  await page.getByRole('button', { name: 'Continue with Google' }).click();
  await page.waitForURL(/accounts\.google\.com/);

  expect(authorize?.origin + (authorize?.pathname ?? '')).toBe('https://accounts.google.com/o/oauth2/v2/auth');
  const params = authorize!.searchParams;
  expect(params.get('client_id')).toBe('e2e-web-client.apps.googleusercontent.com');
  expect(params.get('redirect_uri')).toBe('http://127.0.0.1:8081/google-callback');
  expect(params.get('response_type')).toBe('id_token');
  expect(params.get('state')).toHaveLength(32);
  expect(params.get('nonce')).toHaveLength(32);

  const name = uniqueName('google');
  await page.goto(`/google-callback#state=${params.get('state')}&id_token=dev:${name}`);
  await expect(page.getByLabel('Handle')).toBeVisible();
  expect(page.url()).not.toContain('id_token'); // the token leaves the address bar
  await chooseHandle(page, name);
  await chooseHubCity(page, 'Chicago');
});

test('a Google answer that did not start in this browser is refused', async ({ page }) => {
  await page.goto(`/google-callback#state=forged&id_token=dev:${uniqueName('forged')}`);

  await expect(page.getByText("This Google sign-in expired or didn't start here. Try again.")).toBeVisible();
  await page.getByRole('button', { name: 'Back to sign-in' }).click();
  await expect(page.getByRole('button', { name: 'Continue with Google' })).toBeVisible();
});
