import { test, expect, Page } from '@playwright/test';

// ─── JS error collector helper ────────────────────────────────────────────────
function collectJsErrors(page: Page): string[] {
  const errors: string[] = [];
  page.on('pageerror', (err) => errors.push(err.message));
  page.on('console', (msg) => {
    if (msg.type() === 'error') errors.push(`[console.error] ${msg.text()}`);
  });
  return errors;
}

// ─── Health API ───────────────────────────────────────────────────────────────

test.describe('Health API', () => {
  test('GET /api/health returns 200', async ({ request }) => {
    const response = await request.get('/api/health');
    expect(response.status()).toBe(200);
  });

  test('/api/health status is healthy or degraded', async ({ request }) => {
    const body = await (await request.get('/api/health')).json();
    expect(['healthy', 'degraded']).toContain(body.status);
  });

  test('/api/health returns application name', async ({ request }) => {
    const body = await (await request.get('/api/health')).json();
    expect(body.application).toBe('PoPunkouterSoftware');
  });

  test('/api/health returns timestamp', async ({ request }) => {
    const body = await (await request.get('/api/health')).json();
    expect(body.timestamp).toBeTruthy();
  });

  test('/api/config returns apiBase', async ({ request }) => {
    const body = await (await request.get('/api/config')).json();
    expect(body.apiBase).toMatch(/^http/);
  });
});

// ─── Home page ────────────────────────────────────────────────────────────────

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

// ─── Azure Dashboard page ─────────────────────────────────────────────────────

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

  test('page has content rendered', async ({ page }) => {
    await page.goto('/azure');
    await page.waitForLoadState('networkidle');
    const bodyText = await page.locator('body').textContent();
    expect(bodyText?.trim().length).toBeGreaterThan(10);
  });

  test('WASM hydrates and renders Web Services tab', async ({ page }) => {
    // WASM must fully hydrate and fetch /api/diag/report before tabs appear.
    // This test catches regressions in the WebRootPath fix and prerender:false setup.
    await page.goto('/azure');
    const tab = page.getByRole('tab', { name: /Web Services/ });
    await expect(tab).toBeVisible({ timeout: 20_000 });
  });

  test('all dashboard tabs are rendered', async ({ page }) => {
    await page.goto('/azure');
    await page.getByRole('tab', { name: /Web Services/ }).waitFor({ timeout: 20_000 });
    const tabs = page.getByRole('tab');
    const count = await tabs.count();
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

// ─── Diag page ────────────────────────────────────────────────────────────────

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

// ─── Static assets ────────────────────────────────────────────────────────────

test.describe('Static assets', () => {
  test('main.css is served', async ({ request }) => {
    const response = await request.get('/css/main.css');
    expect(response.status()).toBe(200);
  });

  test('apps.json is served', async ({ request }) => {
    const response = await request.get('/data/apps.json');
    expect(response.status()).toBe(200);
  });

  test('main.js is served', async ({ request }) => {
    const response = await request.get('/js/main.js');
    expect(response.status()).toBe(200);
  });

  test('azure-full-report.json is served', async ({ request }) => {
    const response = await request.get('/data/azure-full-report.json');
    expect([200, 404]).toContain(response.status());
  });
});

// ─── API Diag endpoints ───────────────────────────────────────────────────────

test.describe('Diag API endpoints', () => {
  test('/api/diag/report returns 200', async ({ request }) => {
    const response = await request.get('/api/diag/report');
    expect(response.status()).toBe(200);
  });

  test('/api/diag/report returns valid JSON with WebServices', async ({ request }) => {
    const response = await request.get('/api/diag/report');
    expect(response.status()).toBe(200);
    const body = await response.json();
    expect(body.generatedAt).toBeTruthy();
    expect(body.webServices).toBeTruthy();
    expect(typeof body.webServices.total).toBe('number');
  });

  test('/api/diag/report content-type is json when 200', async ({ request }) => {
    const response = await request.get('/api/diag/report');
    if (response.status() === 200) {
      expect(response.headers()['content-type']).toContain('application/json');
    }
  });

  test('/api/diag/az-status returns 200 with loggedIn field', async ({ request }) => {
    const response = await request.get('/api/diag/az-status');
    expect(response.status()).toBe(200);
    const body = await response.json();
    expect(typeof body.loggedIn).toBe('boolean');
  });
});

// ─── Mobile viewport ─────────────────────────────────────────────────────────

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

// ─── OpenAPI ──────────────────────────────────────────────────────────────────

test.describe('OpenAPI', () => {
  test('/openapi/v1.json returns 200', async ({ request }) => {
    const response = await request.get('/openapi/v1.json');
    expect(response.status()).toBe(200);
  });

  test('openapi doc has paths', async ({ request }) => {
    const body = await (await request.get('/openapi/v1.json')).json();
    expect(body.paths).toBeTruthy();
  });
});
