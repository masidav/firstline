import { expect, test } from '@playwright/test';

import { signUp } from './fixtures';

test('a new fan signs up with a handle and a hub city and lands on home', async ({ page }) => {
  await signUp(page);

  await expect(page.getByRole('button', { name: 'Journal' })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Profile' })).toBeVisible();
});

test('reloading a deep page keeps the fan on it', async ({ page }) => {
  await signUp(page);

  // page.goto is a full page load: the session restores from storage while the route waits.
  await page.goto('/journal');
  await expect(page.getByRole('heading', { name: 'Your journal' })).toBeVisible();
  expect(new URL(page.url()).pathname).toBe('/journal');

  await page.goto('/profile');
  await expect(page.getByRole('button', { name: 'Sign out' })).toBeVisible();
  expect(new URL(page.url()).pathname).toBe('/profile');
});

test('a signed-out deep link ends at sign-in', async ({ page }) => {
  await page.goto('/journal');

  await page.waitForURL(/\/sign-in/);
  await expect(page.getByLabel('Test account name')).toBeVisible();
});
