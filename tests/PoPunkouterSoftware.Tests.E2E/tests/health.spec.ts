import { test, expect } from '@playwright/test';

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
