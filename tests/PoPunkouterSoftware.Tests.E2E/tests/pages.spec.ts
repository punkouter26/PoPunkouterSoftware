import { test, expect } from '@playwright/test';
import { collectJsErrors } from './_helpers';

test.describe('Home page', () => {
  test('loads without JS errors', async ({ page }) => {
    const errors = collectJsErrors(page);
    await page.goto('/');
    await page.waitForLoadState('networkidle');
    const blazorErrors = errors.filter(e => !e.includes('favicon'));
    expect(blazorErrors, `JS errors: ${blazorErrors.join(', ')}`).toHaveLength(0);
  });

  test('page title is set', async ({ page }) => {
    await page.goto('/');
    await expect(page).toHaveTitle(/.+/);
  });

  test('has visible body content', async ({ page }) => {
    await page.goto('/');
    await page.waitForLoadState('networkidle');
    const bodyText = await page.locator('body').textContent();
    expect(bodyText?.trim().length).toBeGreaterThan(0);
  });

  test('does not show .NET unhandled exception page', async ({ page }) => {
    await page.goto('/');
    await page.waitForLoadState('networkidle');
    const bodyText = await page.locator('body').innerText();
    expect(bodyText).not.toContain('An unhandled exception occurred');
    expect(bodyText).not.toContain('System.');
  });
});

test.describe('Azure Dashboard', () => {
  test('loads without JS errors', async ({ page }) => {
    const errors = collectJsErrors(page);
    await page.goto('/azure');
    await page.waitForLoadState('networkidle');
    const blazorErrors = errors.filter(e => !e.includes('favicon'));
    expect(blazorErrors, `JS errors: ${blazorErrors.join(', ')}`).toHaveLength(0);
  });

  test('returns 200 status', async ({ page }) => {
    const response = await page.goto('/azure');
    expect(response?.status()).toBe(200);
  });

  test('does not show unhandled exception', async ({ page }) => {
    await page.goto('/azure');
    await page.waitForLoadState('networkidle');
    const bodyText = await page.locator('body').innerText();
    expect(bodyText).not.toContain('An unhandled exception occurred');
  });

  test('WASM hydrates and renders Web Services tab', async ({ page }) => {
    await page.goto('/azure');
    const tab = page.getByRole('tab', { name: /Web Services/ });
    await expect(tab).toBeVisible({ timeout: 20_000 });
  });

  test('all dashboard tabs are rendered', async ({ page }) => {
    await page.goto('/azure');
    await page.getByRole('tab', { name: /Web Services/ }).waitFor({ timeout: 20_000 });
    const count = await page.getByRole('tab').count();
    expect(count).toBeGreaterThanOrEqual(6);
  });

  test('clicking Cost tab shows cost data', async ({ page }) => {
    await page.goto('/azure');
    await page.getByRole('tab', { name: /Web Services/ }).waitFor({ timeout: 20_000 });
    await page.getByRole('tab', { name: /Cost/ }).click();
    const content = await page.locator('[role="tabpanel"]').innerText();
    expect(content.length).toBeGreaterThan(5);
  });
});

test.describe('Diag page', () => {
  test('loads /diag without JS errors', async ({ page }) => {
    const errors = collectJsErrors(page);
    await page.goto('/diag');
    await page.waitForLoadState('networkidle');
    const blazorErrors = errors.filter(e => !e.includes('favicon'));
    expect(blazorErrors, `JS errors: ${blazorErrors.join(', ')}`).toHaveLength(0);
  });

  test('/diag returns 200', async ({ page }) => {
    const response = await page.goto('/diag');
    expect(response?.status()).toBe(200);
  });
});

test.describe('Mobile viewport', () => {
  test.use({ viewport: { width: 393, height: 852 } }); // iPhone 15

  test('home page renders on mobile', async ({ page }) => {
    const errors = collectJsErrors(page);
    await page.goto('/');
    await page.waitForLoadState('networkidle');
    const blazorErrors = errors.filter(e => !e.includes('favicon'));
    expect(blazorErrors).toHaveLength(0);
    const bodyText = await page.locator('body').textContent();
    expect(bodyText?.trim().length).toBeGreaterThan(0);
  });

  test('azure page renders on mobile', async ({ page }) => {
    const errors = collectJsErrors(page);
    await page.goto('/azure');
    await page.waitForLoadState('networkidle');
    const blazorErrors = errors.filter(e => !e.includes('favicon'));
    expect(blazorErrors).toHaveLength(0);
  });
});
