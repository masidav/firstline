import { expect, test } from '@playwright/test';

import { signUp } from './fixtures';

test('a fan signs out and back in to the same account', async ({ page }) => {
  const name = await signUp(page);

  await page.goto('/profile');
  await page.getByRole('button', { name: 'Sign out' }).click();
  await page.waitForURL(/\/sign-in/);

  await page.getByLabel('Test account name').fill(name);
  await page.getByRole('button', { name: 'Sign in' }).click();
  await page.waitForURL(/\/home/);
  await expect(page.getByLabel('Handle')).toHaveCount(0);
});

test('a fan deletes their account; signing in again starts a new one', async ({ page }) => {
  const name = await signUp(page);

  await page.goto('/profile');
  await page.getByRole('button', { name: 'Delete account' }).click();
  await page.getByRole('button', { name: 'Yes, delete my account' }).click();
  await page.waitForURL(/\/sign-in/);

  await page.getByLabel('Test account name').fill(name);
  await page.getByRole('button', { name: 'Sign in' }).click();
  // The old account is gone, so this sign-in creates a new one and asks for a handle.
  await expect(page.getByLabel('Handle')).toBeVisible();
});
