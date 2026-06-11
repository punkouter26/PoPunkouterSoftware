import { expect, test } from '@playwright/test';
import { collectJsErrors } from './_helpers';

test.describe('Smoke regressions', () => {
  test('/diag renders a readable diagnostics page with no missing required keys', async ({ page }) => {
    const errors = collectJsErrors(page);

    const response = await page.goto('/diag');

    expect(response?.status()).toBe(200);
    await expect(page.getByRole('heading', { name: 'Diagnostics' })).toBeVisible();
    await expect(page.getByText('Missing Required Keys')).toBeVisible();
    await expect(page.locator('section').filter({ hasText: 'Missing Required Keys' }).getByText('None')).toBeVisible();
    expect(errors.filter(error => !error.includes('favicon'))).toHaveLength(0);
  });

  test('Portfolio Trends action routes to the Azure dashboard instead of a dead page', async ({ page }) => {
    const errors = collectJsErrors(page);

    await page.goto('/');
    await page.waitForLoadState('networkidle');

    await page.getByRole('button', { name: /^Trends$/ }).first().click();

    await expect(page).toHaveURL(/\/azure(\?.*)?$/);
    await expect(page.getByRole('button', { name: /Refresh from Azure/i }).first()).toBeVisible();
    expect(errors.filter(error => !error.includes('favicon'))).toHaveLength(0);
  });
});
