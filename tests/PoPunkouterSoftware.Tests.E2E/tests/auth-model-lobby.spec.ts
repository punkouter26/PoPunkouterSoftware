import { test, expect } from '@playwright/test';
import { collectJsErrors } from './_helpers';

test.describe('Homepage', () => {
  test('homepage renders portfolio shell without JavaScript errors', async ({ page }) => {
    const errors = collectJsErrors(page);

    await page.goto('/');
    await expect(page.getByRole('heading', { name: 'Punkouter Software' })).toBeVisible();
    await expect(page.getByRole('heading', { name: 'Web Apps' })).toBeVisible();
    await expect(page.getByText('Apps currently shown')).toBeVisible();

    const blazorErrors = errors.filter(e => !e.includes('favicon'));
    expect(blazorErrors, `JS errors: ${blazorErrors.join(', ')}`).toHaveLength(0);
  });
});
