import { test, expect } from '@playwright/test';
import { collectJsErrors } from './_helpers';

test.describe('Guest auth, model selection, and lobby', () => {
  test('guest login persists across reload and can enter the lobby', async ({ page }) => {
    const errors = collectJsErrors(page);

    await page.goto('/');
    await page.getByTestId('guest-login-button').click();

    await expect(page.getByTestId('session-status')).toContainText('Signed in as');
    const guestName = (await page.getByTestId('session-status').textContent()) ?? '';

    await page.reload();
    await expect(page.getByTestId('session-status')).toContainText('Signed in as');
    await expect(page.getByTestId('session-status')).toContainText(guestName.replace('Signed in as', '').trim());

    await page.getByTestId('open-lobby-button').click();
    await expect(page).toHaveURL(/\/lobby$/);
    await expect(page.getByTestId('lobby-player-name')).toContainText(/GUEST\d{4}/);
    await expect(page.getByTestId('lobby-role')).toHaveText('Host');
    await expect(page.getByTestId('lobby-ready-state')).toHaveText('Waiting');

    await page.getByTestId('lobby-ready-button').click();
    await expect(page.getByTestId('lobby-ready-state')).toHaveText('Ready');

    const blazorErrors = errors.filter(e => !e.includes('favicon'));
    expect(blazorErrors, `JS errors: ${blazorErrors.join(', ')}`).toHaveLength(0);
  });

  test('model selector presents grouped AI model choices', async ({ page }) => {
    await page.goto('/');
    await expect(page.getByTestId('model-selector-card')).toContainText('Select a model');

    await page.locator('[data-testid="model-selector"] .rz-dropdown').click();
    await expect(page.locator('.rz-dropdown-items')).toContainText('Remote');
    await expect(page.locator('.rz-dropdown-items')).toContainText('Browser');
    await expect(page.locator('.rz-dropdown-items')).toContainText('Ollama');

    await page.getByText('Ollama llama3.1', { exact: true }).click();
    await expect(page.locator('[data-testid="model-selector"] .rz-dropdown-label')).toContainText('Ollama llama3.1');
  });

  test('lobby redirects unauthenticated users to the login prompt state', async ({ page }) => {
    await page.goto('/lobby');
    await expect(page.getByTestId('lobby-login-required')).toContainText('Login on the home page first');
  });
});
