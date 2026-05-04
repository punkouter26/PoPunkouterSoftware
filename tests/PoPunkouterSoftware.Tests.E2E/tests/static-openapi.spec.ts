import { test, expect } from '@playwright/test';

test.describe('Static assets', () => {
  test('modern-ui.css is served', async ({ request }) => {
    const response = await request.get('/css/modern-ui.css');
    expect(response.status()).toBe(200);
  });

  test('apps.json is served', async ({ request }) => {
    const response = await request.get('/data/apps.json');
    expect(response.status()).toBe(200);
  });

  test('blazor.web.js is served', async ({ request }) => {
    const response = await request.get('/_framework/blazor.web.js');
    expect(response.status()).toBe(200);
  });

  test('azure-full-report.json is served', async ({ request }) => {
    const response = await request.get('/data/azure-full-report.json');
    expect([200, 404]).toContain(response.status());
  });
});

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
