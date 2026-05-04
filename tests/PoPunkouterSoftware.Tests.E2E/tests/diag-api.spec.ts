import { test, expect } from '@playwright/test';

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
